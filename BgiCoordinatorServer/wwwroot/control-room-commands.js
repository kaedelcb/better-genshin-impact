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
function requireOnlineTargets(targets, source = undefined) {
    requireFrozenMemberTargets(targets, freezeMemberTargets(targets, source));
}
function rememberMemberResult(uid, cmd, status, message, source = null) {
    memberCommandResults.set(memberSourceKey(uid, source), { cmd, status, message, time: new Date().toLocaleTimeString() });
    if (typeof renderMemberResults === 'function') renderMemberResults();
}
function isCompletionCatalogRefresh(cmd) {
    return cmd.cmd === 'task_policy.pull' && cmd.params?.ensureBgiReady === 'true'
        && ['group', 'onedragon'].includes(cmd.params?.catalogType);
}
function memberWriteKey(uid, cmd, source = null) {
    // 配置组和一条龙共享同一个执行入口，不能用切换类型绕过重复下发检查。
    return memberSourceKey(uid, source) + '\0' + (cmd === 'start_oneclick' ? 'start_group' : cmd);
}
function requireNoPendingMemberWrite(targets, cmd, sources = freezeMemberTargets(targets)) {
    if (targets.some(uid => activeMemberWrites.has(memberWriteKey(uid, cmd, sources.get(uid)))))
        throw new Error('有成员的同类操作仍在等待执行回执，本次未发送');
}
function sendTrackedCommand(cmd, expectedReply = null, source = undefined) {
    if (cmd.target.length !== 1) throw new Error('执行回执必须逐成员关联');
    const uid = cmd.target[0], entry = requireMemberTarget(uid, source), route = memberReceiptRoute(entry);
    const key = memberPendingKey(route.roomKey, cmd.commandId), writeKey = memberWriteKey(uid, cmd.cmd, route.source);
    if (pendingMemberCommands.has(key)) throw new Error('命令编号仍在等待回执，本次未发送');
    requireNoPendingMemberWrite(cmd.target, cmd.cmd, new Map([[uid, route.source]]));
    activeMemberWrites.add(writeKey);
    rememberMemberResult(uid, cmd.cmd, 'pending', '正在发送，等待执行端确认', route.source);
    const refresh = isCompletionCatalogRefresh(cmd);
    let cancelRefresh;
    const pending = new Promise(resolve => {
        let sent = false, cancellationSent = false;
        const revokeRefresh = () => {
            if (!refresh || !sent || cancellationSent) return;
            cancellationSent = true;
            const cancel = makeCmd('task_policy.pull', { cancelCatalogRefresh: cmd.commandId });
            cancel.target = [uid];
            Promise.resolve().then(() => sendMemberCommand(cancel, route.source)).catch(() => {});
        };
        const finish = result => {
            if (!pendingMemberCommands.delete(key)) return;
            if (result.status !== 'success') revokeRefresh();
            clearTimeout(timer);
            activeMemberWrites.delete(writeKey);
            rememberMemberResult(uid, cmd.cmd, result.status, result.message || '执行端未提供说明', route.source);
            log(`${route.name} · ${commandTitles[cmd.cmd] || cmd.cmd}：${result.status === 'success' ? '已确认' : result.status === 'unknown' ? '未确认' : '未完成'} ${result.message || ''}`);
            resolve(result);
        };
        // 普通目录查询沿用短等待；显式刷新覆盖执行端 150 秒启动确保预算。
        const timeout = refresh ? 180000 : cmd.cmd === 'task_policy.pull' ? 8000 : 90000;
        const timer = setTimeout(() => finish({ status: 'unknown', message: '执行回执超时；未自动重发' }), timeout);
        cancelRefresh = refresh ? () => finish({ status: 'cancelled', message: '目录刷新等待已取消；若原连接仍有效，将通知原执行端撤销' }) : null;
        pendingMemberCommands.set(key, { ...route, cmd: cmd.cmd, expectedReply, finish,
            completionStartVersion: cmd.cmd === 'task_policy.push' ? cmd.params?.completionStartVersion : null,
            completionPolicy: cmd.cmd === 'task_policy.push' ? { ...cmd.params } : null });
        Promise.resolve().then(() => {
            if (!pendingMemberCommands.has(key)) return;
            requireMemberTarget(uid, route.source);
            sent = true;
            if (refresh) cmd.expiresAtUtc = new Date(Date.now() + timeout).toISOString();
            return sendMemberCommand(cmd, route.source);
        }).catch(err => finish({ status: 'unknown', message: '发送或执行结果未确认：' + err.message }));
    });
    pending.cancelCatalogRefresh = cancelRefresh;
    return pending;
}
function consumeMemberResult(result, roomKey = mainMemberRoomKey()) {
    const pending = pendingMemberCommands.get(memberPendingKey(roomKey, result?.commandId));
    if (!pending || !matchesMemberReceipt(pending, result) || pending.cmd !== result.cmd || pending.expectedReply) return;
    if (['success', 'failed', 'cancelled', 'unknown'].includes(result.status)) pending.finish(result);
}
function consumeMemberReply(cmd, roomKey = mainMemberRoomKey()) {
    const pending = pendingMemberCommands.get(memberPendingKey(roomKey, cmd?.commandId));
    if (!pending || !matchesMemberReceipt(pending, cmd, true)) return;
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
function interruptMemberWaits(roomKey = mainMemberRoomKey()) {
    for (const pending of pendingMemberCommands.values())
        if (pending.roomKey === roomKey)
            pending.finish({ status: 'unknown', message: '连接断开，执行结果未确认；未自动重发' });
    // Configuration-dependent starts must stop on disconnect as well.
    for (const [id, pending] of pendingConfigApplied) {
        if (pending.roomKey !== roomKey) continue;
        clearTimeout(pending.timer);
        pendingConfigApplied.delete(id);
        pending.reject(new Error('连接断开，配置应用未确认，未继续启动任务'));
    }
}
function interruptMemberRoomWaits(roomKey) {
    interruptMemberWaits(roomKey);
    interruptStateResetWaits(roomKey);
}
function sendMemberOperation(uid, cmdName, params = {}, expectedReply = null, source = undefined) {
    try {
        const cmd = makeCmd(cmdName, params);
        cmd.target = [uid];
        return sendTrackedCommand(cmd, expectedReply, source);
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
// 与 CompletionPolicyFields.Parameters / TaskPolicySyncService.CompletionEditRevision 一致。
// 读取修订包含原始房间关系（包括无效配置），不同于开始确认使用的公开 actionRevision。
const roomSwitchEditFields = ['roomSwitchRunAfter', 'roomSwitchSourceProfileId', 'roomSwitchSourceRoomCode',
    'roomSwitchSourceServerUrl', 'roomSwitchTargetName', 'roomSwitchTargetProfileId', 'roomSwitchTargetRoomCode', 'roomSwitchTargetServerUrl'];
const completionEditFields = ['completionStartVersion', 'policy', 'specifiedConfigRevision', 'specifiedName',
    'specifiedPriority', 'specifiedStartMode', 'specifiedStartTaskId', 'specifiedStartTaskName', 'specifiedType', 'specifiedWorkflowId',
    ...roomSwitchEditFields].sort();
function hasCompletionEditFields(data) {
    return completionEditFields.every(key => typeof data?.[key] === 'string');
}
function completionEditRevision(data) {
    // System.Text.Json 默认编码在字符串内部使用大写 \uXXXX（包括非 ASCII、HTML 敏感字符和 '+'）。
    const shortEscapes = { '\b': '\\b', '\t': '\\t', '\n': '\\n', '\f': '\\f', '\r': '\\r', '\\': '\\\\' };
    const encode = value => '"' + String(value ?? '').replace(/[^ -~]|["'&+<>`\\]/g, (ch, index, text) => {
        if (shortEscapes[ch]) return shortEscapes[ch];
        let code = ch.charCodeAt(0);
        // .NET 的 UTF-8 编码器将孤立代理项替换为 U+FFFD。
        if (code >= 0xD800 && code <= 0xDBFF && !(text.charCodeAt(index + 1) >= 0xDC00 && text.charCodeAt(index + 1) <= 0xDFFF)
            || code >= 0xDC00 && code <= 0xDFFF && !(text.charCodeAt(index - 1) >= 0xD800 && text.charCodeAt(index - 1) <= 0xDBFF)) code = 0xFFFD;
        return '\\u' + code.toString(16).toUpperCase().padStart(4, '0');
    }) + '"';
    const json = '[' + completionEditFields.map(key => '[' + encode(key) + ',' + encode(data?.[key]) + ']').join(',') + ']';
    return sha256CompletionEdit(json);
}

// 这里的 JSON 已全为 ASCII。HTTP 局域网网页也需能够计算读回修订，不能依赖仅安全上下文提供的 crypto.subtle。
function sha256CompletionEdit(text) {
    const hash = [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19];
    const constants = [0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2];
    const bytes = new Uint8Array(Math.ceil((text.length + 9) / 64) * 64);
    for (let i = 0; i < text.length; ++i) bytes[i] = text.charCodeAt(i);
    bytes[text.length] = 0x80;
    const view = new DataView(bytes.buffer);
    view.setUint32(bytes.length - 8, Math.floor(text.length / 0x20000000));
    view.setUint32(bytes.length - 4, text.length * 8);
    const w = new Uint32Array(64), rotate = (value, bits) => (value >>> bits) | (value << (32 - bits));
    for (let offset = 0; offset < bytes.length; offset += 64) {
        for (let i = 0; i < 16; ++i) w[i] = view.getUint32(offset + i * 4);
        for (let i = 16; i < 64; ++i) {
            const x = w[i - 15], y = w[i - 2];
            w[i] = w[i - 16] + (rotate(x, 7) ^ rotate(x, 18) ^ (x >>> 3))
                + w[i - 7] + (rotate(y, 17) ^ rotate(y, 19) ^ (y >>> 10));
        }
        let [a, b, c, d, e, f, g, h] = hash;
        for (let i = 0; i < 64; ++i) {
            const t1 = (h + (rotate(e, 6) ^ rotate(e, 11) ^ rotate(e, 25)) + ((e & f) ^ (~e & g)) + constants[i] + w[i]) >>> 0;
            const t2 = ((rotate(a, 2) ^ rotate(a, 13) ^ rotate(a, 22)) + ((a & b) ^ (a & c) ^ (b & c))) >>> 0;
            h = g; g = f; f = e; e = (d + t1) >>> 0; d = c; c = b; b = a; a = (t1 + t2) >>> 0;
        }
        [a, b, c, d, e, f, g, h].forEach((value, i) => { hash[i] = (hash[i] + value) >>> 0; });
    }
    return hash.map(value => value.toString(16).toUpperCase().padStart(8, '0')).join('');
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
    if (action.policy === 'switchRoom') {
        // 拉取的是编辑原始字段，不包含可用于批次确认的 actionRevision。即使目标无效，也可明确替换为原三种动作。
        if (data.completionStartVersion !== '1' || !hasCompletionEditFields(data)
            || !Number.isInteger(action.priority) || action.priority < -2147483648 || action.priority > 2147483647)
            throw new Error('执行端返回的切房动作字段不完整，请重新读取或在执行端本机编辑');
        return { ...action, targetName: data.roomSwitchTargetName, targetRoomCode: data.roomSwitchTargetRoomCode,
            runAfterSwitch: data.roomSwitchRunAfter === 'true' ? true : data.roomSwitchRunAfter === 'false' ? false : null };
    }
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
    if (action?.policy === 'switchRoom') throw new Error('切房关系须在执行端本机编辑，未发送设置');
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
