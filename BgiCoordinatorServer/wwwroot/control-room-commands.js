// Web-only adapters for existing assistant commands. Never infer execution from a gateway ack.
const pendingMemberCommands = new Map();
const activeMemberWrites = new Set();
const memberCommandResults = new Map();
const commandTitles = {
    start_bgi: '启动 BGI', start_group: '启动配置组', start_oneclick: '启动一条龙',
    hotkey_execute: '执行快捷键', stop: '停止任务', close_game: '关闭游戏',
    clear_online: '清除上线', set_scheduled_online_time: '设置定时上线',
    bind_hoeing_group: '保存联机绑定', set_quick_command: '保存快捷绑定',
    'task_policy.pull': '读取结束动作', 'task_policy.push': '保存结束动作'
};
function requireOnlineTargets(targets) {
    if (!roomReady) throw new Error('尚未连接控制房间，请等待重新入房');
    if (!targets.length || targets.includes('*')) throw new Error('请明确选择操作成员');
    if (targets.some(uid => !players.some(p => p.playerUid === uid && p.online === true)))
        throw new Error('有成员已离线，本次未发送，请重新确认');
}
function rememberMemberResult(uid, cmd, status, message) {
    memberCommandResults.set(uid, { cmd, status, message, time: new Date().toLocaleTimeString() });
    if (typeof renderMemberResults === 'function') renderMemberResults();
}
function isCompletionCatalogRefresh(cmd) {
    return cmd.cmd === 'task_policy.pull' && cmd.params?.ensureBgiReady === 'true'
        && ['group', 'onedragon'].includes(cmd.params?.catalogType);
}
function sendTrackedCommand(cmd, expectedReply = null) {
    requireOnlineTargets(cmd.target);
    if (cmd.target.length !== 1) throw new Error('执行回执必须逐成员关联');
    const uid = cmd.target[0], writeKey = uid + '\0' + cmd.cmd;
    if (activeMemberWrites.has(writeKey)) throw new Error('该成员的同类操作正在等待结果');
    activeMemberWrites.add(writeKey);
    rememberMemberResult(uid, cmd.cmd, 'pending', '正在发送，等待执行端确认');
    const refresh = isCompletionCatalogRefresh(cmd);
    let cancelRefresh;
    const pending = new Promise(resolve => {
        let sent = false, cancellationSent = false;
        const revokeRefresh = () => {
            if (!refresh || !sent || cancellationSent) return;
            cancellationSent = true;
            const cancel = makeCmd('task_policy.pull', { cancelCatalogRefresh: cmd.commandId });
            cancel.target = [uid];
            Promise.resolve().then(() => sendRemoteCommand(cancel)).catch(() => {});
        };
        const finish = result => {
            if (!pendingMemberCommands.delete(cmd.commandId)) return;
            if (result.status !== 'success') revokeRefresh();
            clearTimeout(timer);
            activeMemberWrites.delete(writeKey);
            rememberMemberResult(uid, cmd.cmd, result.status, result.message || '执行端未提供说明');
            const name = players.find(p => p.playerUid === uid)?.playerName || uid;
            log(`${name} · ${commandTitles[cmd.cmd] || cmd.cmd}：${result.status === 'success' ? '已确认' : result.status === 'unknown' ? '未确认' : '未完成'} ${result.message || ''}`);
            resolve(result);
        };
        // 普通目录查询沿用短等待；显式刷新覆盖执行端 150 秒启动确保预算。
        const timeout = refresh ? 180000 : cmd.cmd === 'task_policy.pull' ? 8000 : 90000;
        const timer = setTimeout(() => finish({ status: 'unknown', message: '执行回执超时；未自动重发' }), timeout);
        cancelRefresh = refresh ? () => finish({ status: 'cancelled', message: '目录刷新等待已取消；已发送的请求正通知执行端撤销' }) : null;
        pendingMemberCommands.set(cmd.commandId, { uid, cmd: cmd.cmd, expectedReply, finish,
            completionStartVersion: cmd.cmd === 'task_policy.push' ? cmd.params?.completionStartVersion : null,
            completionPolicy: cmd.cmd === 'task_policy.push' ? { ...cmd.params } : null });
        Promise.resolve().then(() => {
            if (!pendingMemberCommands.has(cmd.commandId)) return;
            sent = true;
            if (refresh) cmd.expiresAtUtc = new Date(Date.now() + timeout).toISOString();
            return sendRemoteCommand(cmd);
        }).catch(err => finish({ status: 'unknown', message: '发送或执行结果未确认：' + err.message }));
    });
    pending.cancelCatalogRefresh = cancelRefresh;
    return pending;
}
function consumeMemberResult(result) {
    const pending = pendingMemberCommands.get(result?.commandId);
    if (!pending || pending.uid !== result.targetUid || pending.cmd !== result.cmd || pending.expectedReply) return;
    if (['success', 'failed', 'cancelled', 'unknown'].includes(result.status)) pending.finish(result);
}
function consumeMemberReply(cmd) {
    const pending = pendingMemberCommands.get(cmd?.commandId);
    if (!pending || pending.uid !== cmd.senderUid) return;
    if (pending.expectedReply && cmd.cmd === pending.expectedReply) {
        const ok = cmd.params?.ok === true || cmd.params?.ok === 'true';
        if (ok && pending.completionStartVersion === '1' && cmd.params?.completionStartVersion !== '1') {
            pending.finish({ status: 'unknown', message: '执行端未确认起点协议版本，保存结果未确认；请更新执行端后重新读取设置', data: cmd.params });
            return;
        }
        if (ok && pending.completionStartVersion === '1') {
            try {
                if (completionActionSignature(normalizePolicy(cmd.params)) !== completionActionSignature(normalizePolicy(pending.completionPolicy)))
                    throw new Error('执行端回传的结束动作与本次提交不一致');
            } catch (err) {
                pending.finish({ status: 'unknown', message: '保存结果未确认：' + err.message + '；请重新读取设置', data: cmd.params });
                return;
            }
        }
        pending.finish({ status: ok ? 'success' : 'failed',
            message: cmd.params?.message || (ok ? '执行端已确认' : '执行端拒绝操作'), data: cmd.params });
    } else if (cmd.cmd === 'ack') {
        const status = cmd.params?.status;
        // Policy read/write requires its data reply; a generic success ack has no settings payload.
        if (pending.expectedReply && status === 'success') return;
        if (['success', 'failed', 'cancelled', 'unknown'].includes(status))
            pending.finish({ status, message: cmd.params?.message || '' });
    }
}
function interruptMemberWaits() {
    for (const pending of pendingMemberCommands.values())
        pending.finish({ status: 'unknown', message: '连接断开，执行结果未确认；未自动重发' });
    // Configuration-dependent starts must stop on disconnect as well.
    for (const [id, pending] of pendingConfigApplied) {
        clearTimeout(pending.timer);
        pendingConfigApplied.delete(id);
        pending.reject(new Error('连接断开，配置应用未确认，未继续启动任务'));
    }
}
function sendMemberOperation(uid, cmdName, params = {}, expectedReply = null) {
    try {
        const cmd = makeCmd(cmdName, params);
        cmd.target = [uid];
        return sendTrackedCommand(cmd, expectedReply);
    } catch (err) { return Promise.reject(err); }
}
function validateBinding(names, types, member) {
    if (!names.length || names.length !== types.length) throw new Error('至少添加一项联机任务，且每项必须有明确类型');
    const seen = new Set();
    names.forEach((name, i) => {
        if (!name || name !== name.trim() || !['group', 'onedragon'].includes(types[i])) throw new Error('绑定名称或类型无效');
        const key = types[i] + '\0' + name.toLowerCase();
        if (seen.has(key)) throw new Error('同一种任务不能重复绑定');
        seen.add(key);
        const available = types[i] === 'onedragon' ? member.oneClickConfigs : member.configGroups;
        if (!available?.includes(name)) throw new Error(`目标成员已不存在任务「${name}」，请重新选择`);
    });
    if (types.join(',').length > 1024) throw new Error('绑定任务数量超过协议上限');
}
function memberBinding(member) {
    const names = member.onlineHoeingGroupNames || [];
    const types = member.onlineHoeingGroupTypes?.length ? member.onlineHoeingGroupTypes : names.map(() => 'group');
    return { names: [...names], types: [...types] };
}
function completionActionSignature(action) {
    return JSON.stringify([action?.policy, action?.taskType, action?.taskName, action?.priority ?? 0,
        action?.startMode ?? 'default', action?.startTaskId ?? '', action?.startTaskName ?? '',
        action?.configRevision ?? '', action?.workflowId ?? '']);
}
function normalizePolicy(data) {
    if (data?.completionStartVersion === '1' && (['specifiedStartMode', 'specifiedStartTaskId',
        'specifiedStartTaskName', 'specifiedConfigRevision', 'specifiedWorkflowId'].some(key => typeof data[key] !== 'string')
        || data.specifiedPriority == null || String(data.specifiedPriority).trim() === ''))
        throw new Error('执行端返回的起点字段不完整');
    const action = { policy: data?.policy, taskType: data?.specifiedType,
        taskName: data?.specifiedName, priority: Number(data?.specifiedPriority ?? 0),
        startMode: data?.specifiedStartMode === undefined ? 'default' : data.specifiedStartMode,
        startTaskId: data?.specifiedStartTaskId === undefined ? '' : data.specifiedStartTaskId,
        startTaskName: data?.specifiedStartTaskName === undefined ? '' : data.specifiedStartTaskName,
        configRevision: data?.specifiedConfigRevision === undefined ? '' : data.specifiedConfigRevision,
        workflowId: data?.specifiedWorkflowId === undefined ? '' : data.specifiedWorkflowId };
    if (!isValidCompletionAction(action) || !Number.isInteger(action.priority)
        || action.priority < -2147483648 || action.priority > 2147483647)
        throw new Error('执行端返回的结束动作不完整，未填入默认值覆盖原设置');
    return action;
}
// 完成动作目录只来自目标成员的 task_policy.pull；不复用任务中心或其他成员的目录。
function normalizeCompletionCatalog(data) {
    if (data?.completionStartVersion !== '1') return null;
    let catalog;
    try { catalog = JSON.parse(data.catalog); }
    catch { throw new Error('执行端未返回有效的完成动作目录'); }
    const text = (value, empty = false) => typeof value === 'string' && value.length <= 1024 && (empty || value.trim().length > 0);
    const nodesValid = nodes => Array.isArray(nodes) && nodes.every(node => node && text(node.taskId)
        && text(node.name) && typeof node.enabled === 'boolean') && new Set(nodes.map(node => node.taskId)).size === nodes.length;
    const namesValid = names => names == null || Array.isArray(names) && names.every(name => text(name))
        && new Set(names).size === names.length;
    if (!catalog || !Array.isArray(catalog.workflows) || !catalog.workflows.every(flow => flow && text(flow.workflowId)
        && text(flow.name) && text(flow.revision) && nodesValid(flow.nodes))
        || new Set(catalog.workflows.map(flow => flow.workflowId)).size !== catalog.workflows.length
        || catalog.target !== null && (!catalog.target || !['group', 'onedragon', 'workflow'].includes(catalog.target.type)
            || !text(catalog.target.name) || !text(catalog.target.workflowId, catalog.target.type !== 'workflow')
            || catalog.target.type !== 'workflow' && catalog.target.workflowId !== ''
            || !text(catalog.target.revision) || !nodesValid(catalog.target.nodes))
        || catalog.error != null && typeof catalog.error !== 'string'
        || !namesValid(catalog.configGroups) || !namesValid(catalog.oneClickConfigs))
        throw new Error('执行端返回的完成动作目录不完整');
    return catalog;
}
function completionTargetKey(type, name, workflowId = '') { return JSON.stringify([type, name, workflowId]); }
function validateCompletionSelection(action, catalog) {
    if (!isValidCompletionAction(action)) throw new Error('结束动作或执行起点不完整');
    if (action.policy !== 'runSpecified' || action.startMode === 'default') return;
    const target = catalog?.target;
    if (catalog?.error) throw new Error(catalog.error);
    if (!target || completionTargetKey(target.type, target.name, target.workflowId)
        !== completionTargetKey(action.taskType, action.taskName, action.workflowId))
        throw new Error('目标目录未确认，请重新读取或选择任务');
    if (target.revision !== action.configRevision) throw new Error('任务配置已修订，请重新选择执行起点');
    if (action.startMode === 'node' && !target.nodes.some(node => node.taskId === action.startTaskId && node.name === action.startTaskName && node.enabled))
        throw new Error('指定起点已失效或被禁用，请重新选择；未改成从头执行');
    if (!target.nodes.some(node => node.enabled)) throw new Error('目标没有可执行任务，请先在执行端启用任务');
}
function completionPolicyParams(action, version) {
    if (!isValidCompletionAction(action) || !Number.isInteger(action.priority)
        || action.priority < -2147483648 || action.priority > 2147483647) throw new Error('结束动作或执行起点不完整');
    if (version !== '1') throw new Error('执行端尚不支持明确起点，请更新该成员助手后重新读取');
    if (action.policy === 'runSpecified' && action.startMode === 'default')
        throw new Error('旧配置沿用原入口；保存前请选择从头开始或指定任务');
    const params = { policy: action.policy, specifiedType: action.taskType, specifiedName: action.taskName,
        specifiedPriority: String(action.priority) };
    return { ...params, completionStartVersion: '1', specifiedStartMode: action.startMode,
        specifiedStartTaskId: action.startTaskId, specifiedStartTaskName: action.startTaskName,
        specifiedConfigRevision: action.configRevision, specifiedWorkflowId: action.workflowId };
}
