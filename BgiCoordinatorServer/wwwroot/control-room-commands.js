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
function sendTrackedCommand(cmd, expectedReply = null) {
    requireOnlineTargets(cmd.target);
    if (cmd.target.length !== 1) throw new Error('执行回执必须逐成员关联');
    const uid = cmd.target[0], writeKey = uid + '\0' + cmd.cmd;
    if (activeMemberWrites.has(writeKey)) throw new Error('该成员的同类操作正在等待结果');
    activeMemberWrites.add(writeKey);
    rememberMemberResult(uid, cmd.cmd, 'pending', '正在发送，等待执行端确认');
    return new Promise(resolve => {
        const finish = result => {
            if (!pendingMemberCommands.delete(cmd.commandId)) return;
            clearTimeout(timer);
            activeMemberWrites.delete(writeKey);
            rememberMemberResult(uid, cmd.cmd, result.status, result.message || '执行端未提供说明');
            const name = players.find(p => p.playerUid === uid)?.playerName || uid;
            log(`${name} · ${commandTitles[cmd.cmd] || cmd.cmd}：${result.status === 'success' ? '已确认' : result.status === 'unknown' ? '未确认' : '未完成'} ${result.message || ''}`);
            resolve(result);
        };
        const timer = setTimeout(() => finish({ status: 'unknown', message: '执行回执超时；未自动重发' }), 90000);
        pendingMemberCommands.set(cmd.commandId, { uid, cmd: cmd.cmd, expectedReply, finish });
        Promise.resolve().then(() => sendRemoteCommand(cmd)).catch(err =>
            finish({ status: 'unknown', message: '发送或执行结果未确认：' + err.message }));
    });
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
async function sendMemberOperation(uid, cmdName, params = {}, expectedReply = null) {
    const cmd = makeCmd(cmdName, params);
    cmd.target = [uid];
    return sendTrackedCommand(cmd, expectedReply);
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
function normalizePolicy(data) {
    const action = { policy: data?.policy, taskType: data?.specifiedType,
        taskName: data?.specifiedName, priority: Number(data?.specifiedPriority ?? 0) };
    if (!isValidCompletionAction(action) || !Number.isInteger(action.priority)
        || action.priority < -2147483648 || action.priority > 2147483647)
        throw new Error('执行端返回的结束动作不完整，未填入默认值覆盖原设置');
    return action;
}
