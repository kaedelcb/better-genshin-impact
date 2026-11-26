// ========== 耕地机 Web 控制端：现有 v3 网关与任务入口 ==========
// v3 网关协议（《通信方案》§4.2/§4.4）：信封走同源 /gateway 的 Dispatch/Query 两入口，
// 服务端回调统一为 evt；session.hello 握手先行（重连后 connectionId 变更，必须重新 hello 再入房）。
const PROTOCOL_VERSION = 3;
const WEB_CLIENT_VERSION = '1.0.0';  // 仅诊断用途（服务端握手日志记录），服务端不校验

let connection = null;
let serverCapabilities = [];
let roomCode = '';
let password = '';
let playerName = '';
let players = [];
let roomReady = false;
let joiningRoom = false;
let joinRejection = '';
const webSessionUid = 'web_' + newRequestId();
let lastProgress = {};  // 记录每成员的锄地进度，用于 WEB 端日志去重

const LS_CRED = 'web_hie_cred';
const LS_BIND = 'web_hie_binding';

// ---------- 工具 ----------
function escapeHtml(value) {
    return String(value ?? '').replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch]));
}
function log(msg) {
    const area = document.getElementById('logArea');
    const div = document.createElement('div');
    div.textContent = `[${new Date().toLocaleTimeString()}] ${msg}`;
    area.insertBefore(div, area.firstChild);
    while (area.childElementCount > 200) area.lastElementChild.remove();
}
function showError(msg) {
    document.getElementById('loginError').textContent = msg;
}
function loadCreds() {
    try {
        const raw = localStorage.getItem(LS_CRED);
        return raw ? JSON.parse(raw) : null;
    } catch { return null; }
}
function saveCreds(c) {
    localStorage.setItem(LS_CRED, JSON.stringify(c));
}
function clearCreds() {
    localStorage.removeItem(LS_CRED);
}
function loadBindings() {
    try {
        const raw = localStorage.getItem(LS_BIND);
        return raw ? JSON.parse(raw) : {};
    } catch { return {}; }
}
function saveBindings(b) {
    localStorage.setItem(LS_BIND, JSON.stringify(b));
}
function makeCmd(cmd, params) {
    return {
        cmd,
        roomCode,               // 服务端 SendRemoteCommand 依赖它定位控制房间，缺了会整条命令被拒
        sender: playerName,
        senderUid: webSessionUid,
        target: ['*'],
        commandId: 'web_' + newRequestId(),
        timestamp: new Date().toISOString(),
        expiresAtUtc: new Date(Date.now() + 120000).toISOString(),
        params: params || {}
    };
}

// ---------- v3 网关信封（《通信方案》§4.2，线形 camelCase） ----------
// requestId 仅排查用途（响应关联由 SignalR invoke 天然完成）；
// crypto.randomUUID 仅在安全上下文可用（http 非 localhost 时缺失），兜底时间戳+随机数
function newRequestId() {
    return crypto.randomUUID
        ? crypto.randomUUID().replaceAll('-', '')
        : 'web' + Date.now().toString(36) + Math.random().toString(36).slice(2);
}
function makeEnvelope(type, name, payload) {
    return {
        protocolVersion: PROTOCOL_VERSION,
        type,
        name,
        requestId: newRequestId(),
        payload,
        sentAtUtc: new Date().toISOString()
    };
}
// invoke 响应带 payload.error 即失败（handshake_required/bad_request 等），抛错落入既有 catch 提示路径
function checkGatewayError(resp) {
    const err = resp && resp.payload && resp.payload.error;
    if (err) {
        const rejected = new Error(err.message || err.code || '网关错误');
        rejected.gatewayRejected = true;
        throw rejected;
    }
}
// session.hello 握手（§4.4 DAP 时序：连接后第一条消息必须是 hello，否则一律 handshake_required）
async function sayHello(ownConnection = connection) {
    const generation = typeof webMainGeneration === 'undefined' ? null : webMainGeneration;
    const resp = await ownConnection.invoke('Query', makeEnvelope('query', 'session.hello', {
        clientKind: 'web',
        clientVersion: WEB_CLIENT_VERSION,
        protocolVersion: PROTOCOL_VERSION,
        capabilities: ['hoeing.start.v2', 'hoeing.completionChoice.v1', 'hoeing.quickCommandCompletion.v1']
    }));
    checkGatewayError(resp);
    if (connection !== ownConnection || generation !== null && webMainGeneration !== generation) throw new Error('控制房间连接已变化');
    serverCapabilities = resp?.payload?.capabilities || [];
}
// 主房间仍走原控制端身份及昵称认证；明确 requireReady:false 只是请求已有业务确认/全量快照。
async function joinControlRoom(ownConnection = connection) {
    const generation = typeof webMainGeneration === 'undefined' ? null : webMainGeneration;
    const ownRoomCode = roomCode;
    const requireOwnConnection = () => {
        if (connection !== ownConnection || roomCode !== ownRoomCode
            || generation !== null && webMainGeneration !== generation) throw new Error('控制房间连接已变化');
    };
    requireOwnConnection();
    joinRejection = '';
    const resp = await ownConnection.invoke('Dispatch', makeEnvelope('command', 'control.joinRoom', {
        roomCode: ownRoomCode,
        password,
        playerUid: webSessionUid,
        playerName,
        allowedUids: [],
        isRemote: false,
        clientInstanceId: '',
        ...(webMemberRooms() ? { requireReady: false } : {})
    }));
    requireOwnConnection();
    checkGatewayError(resp);
    if (joinRejection || resp?.payload?.success === false)
        throw Object.assign(new Error(joinRejection || resp.payload.reason || '加入被拒绝'), { roomAuthenticationRejected: true });
    if (webMemberRooms() && typeof webMainSnapshotGeneration !== 'undefined') {
        if (resp?.payload?.success !== true && resp?.payload?.ack !== true) throw new Error('加入控制房间结果未确认');
        if (resp?.payload?.playersUpdate?.full === true) onPlayersUpdated(resp.payload.playersUpdate);
        if (webMainSnapshotGeneration !== generation) {
            const snapshot = await ownConnection.invoke('Query', makeEnvelope('query', 'control.roomSnapshot', { roomCode: ownRoomCode }));
            requireOwnConnection();
            checkGatewayError(snapshot);
            if (snapshot?.payload?.success !== true || snapshot.payload.playersUpdate?.full !== true)
                throw new Error('控制房间成员全量快照未确认');
            onPlayersUpdated(snapshot.payload.playersUpdate);
        }
        if (webMainSnapshotGeneration !== generation) throw new Error('控制房间成员全量快照未确认');
    }
}
// control.sendCommand（旧 SendRemoteCommand；cmd 本体由 makeCmd 生成，逐字不动）
function sendRemoteCommand(cmd) {
    if (webMemberRooms()?.isFollowing?.()) return Promise.reject(new Error('房间跟随仅供监视，请关闭跟随后操作。'));
    return connection.invoke('Dispatch', makeEnvelope('command', 'control.sendCommand', { command: cmd }))
        .then(checkGatewayError);
}

// 单卡操作只使用打开时冻结的来源。省略来源的旧入口（包括嘟嘟可快捷键）始终指主房，
// 显式来源一旦失效，不再按 UID 从主房或当前查看列表补找。
function webMemberRooms() { return globalThis.window?.webRooms || null; }
function mainMemberContext() {
    const rooms = webMemberRooms();
    return rooms?.getMainContext?.() || rooms?.getDisplayedMembers().find(x => x.context.isMain)?.context || null;
}
function mainMemberRoomKey() { return mainMemberContext()?.roomKey || null; }
function mainBulkViewAllowed() {
    if (webMemberRooms()?.isFollowing?.()) return false;
    const view = webMemberRooms()?.getView();
    return !view || view.mode === 'current' || view.mode === 'all'
        || view.mode === 'room' && view.roomKey === mainMemberRoomKey();
}
function captureMainMemberScope() {
    if (!webMemberRooms()) return null;
    const context = mainMemberContext();
    return Object.freeze({ roomKey: context?.roomKey, generation: context?.generation, connection });
}
function requireMainMemberScope(scope, bulkView = true) {
    if (webMemberRooms()?.isFollowing?.()) throw new Error('房间跟随仅供监视，请关闭跟随后操作。');
    if (!scope && !webMemberRooms()) return; // 旧单房入口
    const context = mainMemberContext();
    if (!scope || !roomReady || !context?.ready || (bulkView && !mainBulkViewAllowed())
        || context.roomKey !== scope.roomKey || context.generation !== scope.generation || scope.connection !== connection)
        throw new Error('当前控制房间或查看范围已变化，本次未发送，请重新确认');
}
function memberSourceKey(uid, source) {
    return source ? JSON.stringify([source.roomKey, uid, source.instanceId || '', source.machineId || '',
        source.connectionId || '', source.generation]) : uid;
}
function sameMemberSource(p, source) {
    return p?.playerUid === source.uid && (p.clientInstanceId || '') === (source.instanceId || '')
        && (p.machineId || '') === (source.machineId || '')
        && (source.connectionId === undefined || (p.connectionId || '') === source.connectionId);
}
function freezeMemberSource(uid, source = undefined) {
    if (!uid || uid === '*') throw new Error('请明确选择操作成员');
    const rooms = webMemberRooms();
    if (source === undefined) {
        if (!rooms) return null;
        const context = mainMemberContext();
        if (context?.roomKey) source = rooms.sourceFor ? rooms.sourceFor(uid, context.roomKey)
            : rooms.getDisplayedMembers().find(x => x.context.isMain && x.member.playerUid === uid)?.source;
        if (source?.roomKey !== context?.roomKey) source = null;
    }
    if (source === null && !rooms) return null;
    if (!rooms || !source || source.uid !== uid || !source.roomKey)
        throw new Error('成员来源未确认，本次未发送，请重新选择');
    return Object.isFrozen(source) ? source : Object.freeze({ ...source });
}
function legacyMemberEntry(uid, member = players.find(p => p.playerUid === uid)) {
    return member ? { member, source: null, context: { roomKey: null, isMain: true, ready: roomReady,
        roomCode, nickname: playerName, senderUid: webSessionUid, players, capabilities: serverCapabilities } } : null;
}
function resolveMemberEntry(uid, source, online, uniqueUid) {
    const entry = webMemberRooms()?.requireSource(source, { online, uniqueUid });
    if (!entry?.context?.ready || entry.context.roomKey !== source.roomKey || entry.context.generation !== source.generation
        || entry.context.isMain && !roomReady || !sameMemberSource(entry.member, source) || online && entry.member.online !== true)
        throw new Error('房间连接或成员实例已变化，本次未发送，请重新选择');
    if (uniqueUid && !entry.context.isMain && !source.instanceId)
        throw new Error('该成员未上报实例身份，请更新助手后再从其他房间操作');
    if (uniqueUid && entry.context.players.filter(p => p.playerUid === uid && (!online || p.online === true)).length !== 1)
        throw new Error('该房间同 UID 存在多个目标实例，本次未发送');
    return { ...entry, source };
}
function readMemberEntry(uid, source = undefined) {
    try {
        const frozen = freezeMemberSource(uid, source);
        return frozen ? resolveMemberEntry(uid, frozen, false, false) : legacyMemberEntry(uid);
    } catch { return null; }
}
function requireMemberTarget(uid, source = undefined, online = true) {
    if (webMemberRooms()?.isFollowing?.()) throw new Error('房间跟随仅供监视，请关闭跟随后操作。');
    const frozen = freezeMemberSource(uid, source);
    if (frozen) return resolveMemberEntry(uid, frozen, online, true);
    const entry = legacyMemberEntry(uid, players.find(p => p.playerUid === uid && (!online || p.online === true)) || null);
    if (!roomReady) throw new Error('尚未连接控制房间，请等待重新入房');
    if (!entry) throw new Error('有成员已离线，本次未发送，请重新确认');
    return entry;
}
function freezeMemberTargets(targets, source = undefined) {
    if (!targets.length || targets.includes('*') || source && targets.length !== 1)
        throw new Error('请明确选择操作成员');
    return new Map(targets.map(uid => [uid, freezeMemberSource(uid, source)]));
}
function requireFrozenMemberTargets(targets, sources) {
    if (!targets.length || targets.includes('*')) throw new Error('请明确选择操作成员');
    for (const uid of targets) {
        const source = sources.get(uid);
        if (source === undefined) throw new Error('成员来源未确认，请重新选择');
        requireMemberTarget(uid, source);
    }
}
function requireMainMemberTargets(scope, targets, sources) {
    requireMainMemberScope(scope);
    requireFrozenMemberTargets(targets, sources);
    if (scope && targets.some(uid => sources.get(uid)?.roomKey !== scope.roomKey))
        throw new Error('批量操作仅支持当前控制房间成员');
}
function memberRoomLabel(context) {
    const code = context?.roomCode || '', name = context?.displayName || context?.name || '';
    const label = !name || name === code || name === '房间 ' + code || name === '房间' + code ? code : [name, code].filter(Boolean).join(' · ');
    // 相同房号跨服时仍要可辨认；不把服务器身份混进房间名或路由。
    const duplicateCode = webMemberRooms()?.listProfiles?.().some(p => p.roomCode === code && p.serverUrl !== context?.serverUrl);
    if (duplicateCode) { try { return `${label} · ${new URL(context.serverUrl).host}`; } catch {} }
    return label;
}
function memberTargetLabel(entry, fallback, showRoom = !entry?.context?.isMain || webMemberRooms()?.getView()?.mode === 'all') {
    if (!entry) return fallback;
    const name = entry.member.playerName || entry.member.playerUid || fallback;
    return entry.source && showRoom ? `${name}（${memberRoomLabel(entry.context)}）` : name;
}
function memberReceiptRoute(entry) {
    return { uid: entry.member.playerUid, source: entry.source, roomKey: entry.source?.roomKey || null,
        roomCode: entry.context.roomCode, senderUid: entry.context.senderUid,
        name: memberTargetLabel(entry, entry.member.playerUid, true) };
}
function memberPendingKey(roomKey, commandId) {
    return roomKey ? JSON.stringify([roomKey, commandId]) : commandId;
}
function matchesMemberReceipt(route, data, reply = false) {
    if (!route || route.uid !== (reply ? data?.senderUid : data?.targetUid)) return false;
    if (!route.source) return true; // 兼容未装载跨房模块的单房回执。
    if (data.roomCode !== route.roomCode || (reply
        ? !Array.isArray(data.target) || data.target.length !== 1 || data.target[0] !== route.senderUid
        : data.senderUid !== route.senderUid)) return false;
    const instance = reply ? data.senderInstanceId : data.targetInstanceId;
    if (instance && instance !== route.source.instanceId) return false;
    // 旧普通命令的回执只有 UID；仍须同一已知实例、同一入房代次，无其他在线同 UID 目标。
    const entry = readMemberEntry(route.uid, route.source);
    return !!entry && entry.context.senderUid === route.senderUid
        && !entry.context.players.some(p => p.playerUid === route.uid && p.online && !sameMemberSource(p, route.source));
}
function sendMemberCommand(cmd, source = undefined) {
    if (cmd.target?.length !== 1) throw new Error('单卡命令必须明确指定一名成员');
    const entry = requireMemberTarget(cmd.target[0], source);
    if (!entry.source) return sendRemoteCommand(cmd);
    return webMemberRooms().sendRemoteCommand(entry.source, { ...cmd, roomCode: entry.context.roomCode,
        senderUid: entry.context.senderUid, sender: entry.context.nickname || entry.context.name,
        ...(entry.source.instanceId ? { targetInstanceId: entry.source.instanceId } : {}) });
}
async function invokeMemberGateway(uid, source, name, payload, online = true) {
    const entry = requireMemberTarget(uid, source, online);
    const response = entry.source ? await webMemberRooms().invoke(entry.source, 'Dispatch', name, payload)
        : await connection.invoke('Dispatch', makeEnvelope('command', name, payload));
    checkGatewayError(response);
    if (entry.source) {
        try { requireMemberTarget(uid, entry.source, online); }
        catch { throw new Error('原成员连接或实例已变化，操作结果未确认；不会自动重发'); }
    }
    return response;
}

const pendingConfigApplied = new Map();
function resolveConfigTargets(requested, members) {
    // An applied receipt names an actual executor; '*' can never be its target UID.
    const targets = requested.includes('*')
        ? members.filter(p => p.online === true).map(p => p.playerUid)
        : requested;
    return [...new Set(targets.filter(uid => uid && uid !== '*'))];
}
function consumeConfigApplied(result, roomKey = mainMemberRoomKey()) {
    if (!result || result.cmd !== 'set_task_enabled') return;
    const key = memberPendingKey(roomKey, result.commandId), pending = pendingConfigApplied.get(key);
    if (!pending || !matchesMemberReceipt(pending, result)) return;
    pendingConfigApplied.delete(key);
    clearTimeout(pending.timer);
    if (result.status !== 'success' || !result.configRevision || !result.targetProcessId || !result.targetStartTicksUtc)
        pending.reject(new Error(result.message || '执行端未确认配置版本，未启动任务'));
    else pending.resolve(result);
}
function sendAndWaitConfigApplied(cmd, uid, source = undefined) {
    if (cmd.target?.length !== 1 || cmd.target[0] !== uid) throw new Error('配置回执必须对应单一明确成员');
    const route = memberReceiptRoute(requireMemberTarget(uid, source)), key = memberPendingKey(route.roomKey, cmd.commandId);
    if (pendingConfigApplied.has(key) || [...pendingConfigApplied.values()].some(p => memberSourceKey(p.uid, p.source) === memberSourceKey(uid, route.source)))
        throw new Error('该成员仍在等待上次的配置应用回执，本次未发送');
    return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
            pendingConfigApplied.delete(key);
            reject(new Error('等待配置应用回执超时，未启动任务'));
        }, 15000);
        pendingConfigApplied.set(key, { ...route, resolve, reject, timer });
        Promise.resolve().then(() => {
            if (pendingConfigApplied.has(key)) return sendMemberCommand(cmd, route.source);
        }).catch(error => {
            if (pendingConfigApplied.delete(key)) { clearTimeout(timer); reject(error); }
        });
    });
}

// ---------- 重置状态：服务端批次失效后，逐成员等待实际执行回执 ----------
const resetStateBusyRooms = new Set();
const pendingStateResets = new Map();
function memberResetBusy(source = undefined) { return resetStateBusyRooms.has(source?.roomKey || mainMemberRoomKey()); }
function consumeStateResetReceipt(result, roomKey, reply) {
    const key = memberPendingKey(roomKey, result.commandId), pending = pendingStateResets.get(key);
    const uid = reply ? result.senderUid : result.targetUid;
    if (!pending || !matchesMemberReceipt(pending.routes.get(uid), result, reply)
        || pending.results.get(uid) !== null) return;
    const status = reply ? result.params?.status : result.status;
    if (!['success', 'failed', 'cancelled', 'unknown'].includes(status)) return;
    pending.results.set(uid, { status, message: reply ? result.params?.message : result.message });
    if ([...pending.results.values()].every(Boolean)) pending.finish();
}
function consumeStateResetResult(result, roomKey = mainMemberRoomKey()) {
    if (result?.cmd === 'reset_state') consumeStateResetReceipt(result, roomKey, false);
}
function consumeStateResetAck(cmd, roomKey = mainMemberRoomKey()) {
    if (cmd?.cmd === 'ack') consumeStateResetReceipt(cmd, roomKey, true);
}
function sendAndWaitStateReset(cmd, onProgress, sources = freezeMemberTargets(cmd.target), send = null) {
    requireFrozenMemberTargets(cmd.target, sources);
    const routes = new Map(cmd.target.map(uid => [uid, memberReceiptRoute(requireMemberTarget(uid, sources.get(uid)))]));
    const roomKey = routes.values().next().value.roomKey;
    if ([...routes.values()].some(route => route.roomKey !== roomKey) || cmd.target.length > 1 && roomKey !== mainMemberRoomKey())
        throw new Error('批量重置仅支持当前控制房间成员');
    const key = memberPendingKey(roomKey, cmd.commandId);
    if (pendingStateResets.has(key)) throw new Error('重置编号仍在等待回执');
    return new Promise(resolve => {
        const results = new Map(cmd.target.map(uid => [uid, null]));
        const finish = (reason = '') => {
            if (!pendingStateResets.delete(key)) return;
            clearTimeout(timer);
            resolve([...results].map(([uid, result]) => ({
                uid, status: result?.status || 'unknown',
                message: result?.message || reason || '等待执行回执超时，重置结果未确认'
            })));
        };
        const timer = setTimeout(finish, 90000);
        pendingStateResets.set(key, { roomKey, routes, results, finish });
        // 先登记等待者，防止快回执早于 Dispatch 响应到达。
        Promise.resolve().then(() => {
            if (!pendingStateResets.has(key)) return;
            requireFrozenMemberTargets(cmd.target, sources);
            if (send) return send();
            if (cmd.target.length === 1) return sendMemberCommand(cmd, sources.get(cmd.target[0]));
            return sendRemoteCommand(cmd);
        }).then(() => {
            if (pendingStateResets.has(key))
                onProgress('重置命令已发送，等待执行端确认（最多 90 秒）…');
        }).catch(err => finish('命令发送或回执未确认：' + err.message));
    });
}
function interruptStateResetWaits(roomKey = mainMemberRoomKey()) {
    for (const pending of pendingStateResets.values())
        if (pending.roomKey === roomKey) pending.finish('连接已断开，重置结果未确认；不会自动重发');
}
async function resetMemberState(uid, resetAll, onProgress, explicitTargets = null, source = undefined) {
    const entry = requireMemberTarget(uid, source), frozen = entry.source;
    const key = frozen?.roomKey || null;
    if (resetStateBusyRooms.has(key)) throw new Error('该房间已有重置正在处理中，请等待结果');
    const targets = resolveConfigTargets(explicitTargets || (resetAll ? ['*'] : [uid]), entry.context.players);
    if (!entry.context.isMain && (resetAll || targets.length !== 1 || targets[0] !== uid))
        throw new Error('旁观房间仅支持所选成员重置；整房操作请先明确切换控制房间');
    if (!targets.length) throw new Error('没有可重置的在线成员');
    const sources = targets.length === 1 && targets[0] === uid ? new Map([[uid, frozen]]) : freezeMemberTargets(targets);
    const scope = captureMainMemberScope(), originalConnection = connection;
    const validate = () => {
        requireMemberTarget(uid, frozen);
        requireFrozenMemberTargets(targets, sources);
        if (resetAll || targets.length > 1) requireMainMemberScope(scope);
    };
    const invoke = async (name, payload) => {
        validate();
        if (targets.length === 1) return invokeMemberGateway(targets[0], sources.get(targets[0]), name, payload);
        const response = await originalConnection.invoke('Dispatch', makeEnvelope('command', name, payload));
        checkGatewayError(response);
        validate();
        return response;
    };
    validate();
    const names = new Map(targets.map(target => [target, memberTargetLabel(readMemberEntry(target, sources.get(target)), target, true)]));
    resetStateBusyRooms.add(key);
    try {
        renderMembers();
        const resetId = 'reset_' + newRequestId();
        onProgress('正在确认并撤销当前联机批次…');
        const current = await invoke('control.batch.current', {});
        const response = await invoke('control.batch.reset', {
            resetId, targetUids: targets,
            expectedGeneration: current?.payload?.generation ?? null,
            expectedBatchId: current?.payload?.batchId ?? null
        });
        const reset = response?.payload;
        if (!reset || reset.resetId !== resetId)
            throw new Error('服务端重置结果未确认，未下发执行端重置');
        if (reset.reason === 'stale_reset')
            throw new Error('当前批次已改变，本次重置未执行，请重新确认');
        const cmd = makeCmd('reset_state', { resetId });
        cmd.commandId = resetId;
        cmd.target = targets;
        if (reset.generation > 0) cmd.params.generation = reset.generation;
        if (reset.batchId) cmd.params.batchId = reset.batchId;
        validate();
        onProgress('服务端已撤销旧批次，正在发送执行端重置…');
        const results = await sendAndWaitStateReset(cmd, onProgress, sources, () => {
            validate();
            return targets.length === 1 ? sendMemberCommand(cmd, sources.get(targets[0])) : sendRemoteCommand(cmd);
        });
        const lines = results.map(r => {
            const name = names.get(r.uid);
            return r.status === 'success' ? `${name}：重置成功`
                : `${name}：${r.status === 'unknown' ? '未确认' : '重置未完成'}（${r.message || r.status}）`;
        });
        const summary = results.every(r => r.status === 'success')
            ? '所选成员均已确认重置成功。' : '部分成员重置未完成或未确认。';
        const message = summary + '\n' + lines.join('\n');
        log('[重置状态] ' + message);
        return message;
    } finally {
        resetStateBusyRooms.delete(key);
        renderMembers();
    }
}
function showResetStateModal(uid, memberName, source = undefined) {
    const entry = readMemberEntry(uid, source);
    if (!entry) { log('成员来源已变化，未发送重置'); return; }
    const frozen = entry.source, allowAll = entry.context.isMain && mainBulkViewAllowed();
    if (memberResetBusy(frozen)) return;
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
        <div class="modal" role="dialog" aria-modal="true" aria-labelledby="resetStateTitle">
            <h3 id="resetStateTitle">确认重置状态</h3>
            <p class="reset-state-description"></p>
            <p class="reset-state-description">这也会中止当前房间的共享联机批次，影响参与该轮联机的其他成员。</p>
            <label class="check-row" ${allowAll ? '' : 'hidden'}><input type="checkbox" class="reset-state-all">同时重置所有在线成员</label>
            ${allowAll ? '' : '<p class="reset-state-description">旁观房间仅可重置所选成员；整房操作请先切换控制房间。</p>'}
            <p class="reset-state-result" role="status" aria-live="polite"></p>
            <div class="btn-row">
                <button class="btn btn-secondary reset-state-cancel">取消</button>
                <button class="btn btn-primary reset-state-confirm">确认重置</button>
            </div>
        </div>`;
    overlay.querySelector('.reset-state-description').textContent =
        `将停止 ${memberTargetLabel(entry, memberName)} 正在运行的单机和联机任务，清空任务中心运行状态，恢复到可以重新组织联机的初始状态。`;
    const all = overlay.querySelector('.reset-state-all');
    const status = overlay.querySelector('.reset-state-result');
    const confirmButton = overlay.querySelector('.reset-state-confirm');
    const cancelButton = overlay.querySelector('.reset-state-cancel');
    cancelButton.onclick = () => overlay.remove();
    confirmButton.onclick = async () => {
        confirmButton.disabled = true;
        all.disabled = true;
        cancelButton.disabled = true;
        try {
            status.textContent = await resetMemberState(uid, allowAll && all.checked, message => { status.textContent = message; }, null, frozen);
        } catch (err) {
            status.textContent = '重置未完成或未确认：' + err.message;
            log('[重置状态] ' + status.textContent);
        } finally {
            confirmButton.hidden = true;
            cancelButton.disabled = false;
            cancelButton.textContent = '关闭';
        }
    };
    document.body.appendChild(overlay);
    cancelButton.focus();
}

// ---------- 记住登录：页面加载时回填 ----------
(function restoreForm() {
    const c = loadCreds();
    if (c) {
        if (c.roomCode) document.getElementById('roomCode').value = c.roomCode;
        if (c.nickname) document.getElementById('nickname').value = c.nickname;
        if (c.password) {
            document.getElementById('password').value = c.password;
            document.getElementById('rememberMe').checked = true;
        }
    }
})();
// ---------- 加入房间（无服务器地址输入，同源 /gateway） ----------
function joinRoom(explicitServerUrl, transition = null) {
    if (joiningRoom || roomReady || window.webRooms?.isFollowing?.() && !transition) return false;
    const temporaryLogin = transition?.credentials;
    roomCode = (temporaryLogin ? temporaryLogin.roomCode : document.getElementById('roomCode').value).trim();
    password = (temporaryLogin ? temporaryLogin.password : document.getElementById('password').value).trim();
    playerName = (temporaryLogin ? temporaryLogin.nickname || '' : document.getElementById('nickname').value).trim() || '网页端';

    if (!roomCode || !password) {
        showError('请输入房间码和密码');
        return false;
    }

    joiningRoom = true;
    document.getElementById('joinButton').disabled = true;
    showError('');
    // 临时跟随/恢复使用相同网页登录，但不写 legacy 记住登录或浏览器房间簿。
    const remember = temporaryLogin ? temporaryLogin.remember : document.getElementById('rememberMe').checked;
    const attemptedPassword = password;
    let gatewayAddress = '/gateway', actualServer = '';
    try {
        if(typeof explicitServerUrl==='string'&&explicitServerUrl.trim()){
            const url=new URL(explicitServerUrl);if(!['http:','https:'].includes(url.protocol)||url.username||url.password||url.search||url.hash)throw new Error('服务器地址无效');gatewayAddress=url.href.replace(/\/+$/,'')+'/gateway';
        } else if (window.bgiLocalBridge) gatewayAddress = window.bgiLocalBridge.gatewayUrl();
        actualServer = typeof location!=='undefined' ? new URL(gatewayAddress,location.href).href.replace(/\/gateway\/?$/i,'') : '';
        window.webRooms?.validateMainLogin?.(actualServer, roomCode, !!transition);
    } catch (err) { showError(transition ? '原房间登录地址或本浏览器凭据未确认' : err.message); joiningRoom=false; document.getElementById('joinButton').disabled=false; transition?.onState?.('failed'); return false; }
    const gatewayBuilder = new signalR.HubConnectionBuilder();
    const useDirect = !!window.bgiLocalBridge || (typeof location!=='undefined'&&new URL(gatewayAddress,location.href).origin!==location.origin);
    connection = (useDirect ? gatewayBuilder.withUrl(gatewayAddress, { withCredentials: false }) : gatewayBuilder.withUrl(gatewayAddress))
        .withAutomaticReconnect([0, 2000, 10000, 30000]).build();
    const ownConnection = connection;
    const current = () => connection === ownConnection;
    const state = value => { if (current()) transition?.onState?.(value); };
    const failJoin = err => {
        if (!current()) return;
        const reason = transition ? (err.roomAuthenticationRejected ? '原有房间鉴权未通过，请使用普通登录核对凭据。' : '房间连接或成员快照未确认。') : err.message;
        showError('连接失败: ' + reason); log('连接失败: ' + reason);
        setConn(false); state(err.roomAuthenticationRejected ? 'rejected' : 'failed');
        if (current()) void ownConnection.stop().catch(() => {});
    };
    if(typeof webMainGeneration!=='undefined'){webMainGeneration++;webMainServerUrl=actualServer;}
    state('connecting');
    const generation = () => typeof webMainGeneration === 'undefined' ? null : webMainGeneration;
    const initialGeneration = generation();
    const currentAttempt = epoch => current() && generation() === epoch;
    let confirmedOnce = false;
    function acceptJoin(reconnected = false) {
        if(!current()) return false;
        // 一次真实入房事务只保存一次普通登录。跟随与恢复的临时事务始终不写原存储。
        if(!confirmedOnce&&!transition) {
            if(remember)saveCreds({roomCode,password:attemptedPassword,nickname:playerName,serverUrl:actualServer});else clearCreds();
        }
        setConn(true);
        if(!confirmedOnce&&!transition)window.webRooms?.mainChanged({remember,password:attemptedPassword});
        confirmedOnce=true;
        if(typeof webRememberMainLogin==='function')webRememberMainLogin(ownConnection, remember);
        document.getElementById('loginPanel').style.display='none';document.getElementById('controlPanel').style.display='block';
        document.getElementById('roomInfo').textContent=`房间 ${roomCode}`;
        joiningRoom=false;document.getElementById('joinButton').disabled=false;
        log(reconnected?'已重新加入房间，未自动重发任何命令':`已加入控制房间：${roomCode}（${playerName}）`);
        renderMembers();state('ready');return true;
    }

    // evt 单订阅：服务端广播统一回调（§4.2），按 name 分发；未知 name 静默忽略
    connection.on('evt', env => {
        if (!current() || !env || typeof env !== 'object') return;
        if (transition?.following && !['control.playersUpdated', 'control.joinRejected'].includes(env.name)) return;
        const payload = env.payload;
        if (typeof dodocoEvent === 'function') dodocoEvent(env);
        if (typeof mtEvent === 'function') mtEvent(env);
        switch (env.name) {
            case 'control.remoteCommandResult':
                consumeConfigApplied(payload);
                consumeStateResetResult(payload);
                consumeMemberResult(payload);
                break;
            // 成员列表更新（payload 即全量/增量两态本体）
            case 'control.playersUpdated':
                onPlayersUpdated(payload);
                break;
            // 收到其他成员投递的命令（WEB 不做执行，仅记录；ack 已由 PC 端助手处理）
            case 'control.remoteCommand': {
                const cmd = payload && payload.command;
                if (cmd) {
                    consumeMemberReply(cmd);
                    consumeStateResetAck(cmd);
                    if (!['ack', 'task_policy.data', 'task_policy.push_result'].includes(cmd.cmd))
                        log(`收到命令: ${cmd.cmd} 来自 ${cmd.sender || '?'}`);
                }
                break;
            }
            // payload 即 ack 本体 { commandId, deliveredTo, message }
            case 'control.remoteCommandAck': {
                const ack = payload || {};
                log(`送达 ${ack.deliveredTo} 个目标${ack.message ? "：" + ack.message : ""}`);
                break;
            }
            case 'control.batch.state':
                onControlBatchState(payload);
                break;
            case 'control.joinRejected': {
                const reason = (payload && payload.reason) || '加入被拒绝';
                joinRejection = transition ? '原有房间鉴权未通过' : reason;
                showError(joinRejection);
                log('加入失败: ' + joinRejection);
                break;
            }
        }
    });

    connection.onreconnecting(() => {
        if(connection!==ownConnection)return;
        if(typeof webMainGeneration!=='undefined')webMainGeneration++;
        if(typeof mtCancelRoomPending==='function')mtCancelRoomPending(null,'控制房间连接中断，结果待核对。');
        interruptStateResetWaits();
        interruptMemberWaits();
        setConn(false); state('reconnecting');
        log('连接断开，重连中...');
    });
    connection.onreconnected(() => {
        if(!current())return;
        const epoch=generation();
        joiningRoom=true;document.getElementById('joinButton').disabled=true;state('connecting');
        const reconnect=sayHello(ownConnection).then(() => {
            if(!currentAttempt(epoch))throw new Error('原入房代次已失效');
            return joinControlRoom(ownConnection);
        });
        let deadline;
        const attempt=transition?.following?Promise.race([reconnect,new Promise((_,reject)=>{deadline=setTimeout(()=>reject(new Error('重新入房超时')),25000);})]):reconnect;
        attempt.then(() => {
            if(currentAttempt(epoch))acceptJoin(true);
        }).catch(err => {
            if (!currentAttempt(epoch)) return;
            if (transition?.following && window.webRooms?.isFollowing?.()) failJoin(err);
            else { setConn(false); log('重连后重新入房失败: ' + err.message); }
        }).finally(()=>{clearTimeout(deadline);if(currentAttempt(epoch)){joiningRoom=false;document.getElementById('joinButton').disabled=false;}});
    });
    connection.onclose(() => { if(!current())return; if(typeof webMainGeneration!=='undefined')webMainGeneration++;setConn(false); interruptStateResetWaits(); interruptMemberWaits();if(typeof mtCancelRoomPending==='function')mtCancelRoomPending(null,'控制房间断开，结果未确认。');joiningRoom=false;document.getElementById('joinButton').disabled=false;state('closed'); });

    const starting = ownConnection.start();
    return starting
        .then(() => {
            if (!current()) { void ownConnection.stop().catch(() => {}); throw new Error('控制房间连接已变化'); }
            if (!currentAttempt(initialGeneration)) throw new Error('原入房代次已失效');
            return sayHello(ownConnection);
        })
        .then(() => {
            if(!currentAttempt(initialGeneration))throw new Error('原入房代次已失效');
            return joinControlRoom(ownConnection);
        })
        .then(() => currentAttempt(initialGeneration) && acceptJoin())
        .catch(err => { if(currentAttempt(initialGeneration))failJoin(err); return false; })
        .finally(() => { if(currentAttempt(initialGeneration)){joiningRoom = false; document.getElementById('joinButton').disabled = false;} });
}

function setConn(online) {
    roomReady = online;
    if (typeof dodocoConnection === 'function') dodocoConnection(online);
    if (typeof mtConnection === 'function') mtConnection(online);
    document.getElementById('connectionText').textContent = online ? '连接正常' : '连接未就绪';
    window.webRooms?.mainChanged();
    if (typeof renderControlRoom === 'function') renderControlRoom();
    const dot = document.getElementById('connDot');
    dot.className = 'dot ' + (online ? 'dot-online' : 'dot-offline');
    if (window.bgiLocalBridge) window.bgiLocalBridge.networkChanged(online);
}

// ---------- 成员列表更新（全量/增量两形态，两态合并逻辑自旧 ControlRoomPlayersUpdated 原样保留） ----------
function onPlayersUpdated(update) {
    // 防御：payload 为空或非对象时直接忽略
    if (!update || typeof update !== 'object') return;
    if (update.full) {
        players = update.players || [];
    } else {
        // 增量：changed 按 uid 原地替换/末尾追加，removed 按 uid 过滤；
        // 不在 changed/removed 中的成员保持不变。
        if (!Array.isArray(players)) players = [];
        const byUid = new Map(players.map(p => [p.playerUid, p]));
        (update.changed || []).forEach(p => {
            if (byUid.has(p.playerUid)) {
                const idx = players.findIndex(x => x.playerUid === p.playerUid);
                players[idx] = p;
            } else {
                byUid.set(p.playerUid, p);
                players.push(p);
            }
        });
        (update.removed || []).forEach(uid => {
            players = players.filter(p => p.playerUid !== uid);
        });
    }
    if(typeof webRoomUpdateMain==='function')webRoomUpdateMain(update);
    window.webRooms?.mainChanged();
    renderMembers();
    if (typeof dodocoMembers === 'function') dodocoMembers();
    if (typeof mtMembersChanged === 'function') mtMembersChanged(update);
    // 记录锄地进度日志（去重）
    players.forEach(p => {
        const progress = p.autoHoeingProgress || '';
        if (progress && progress !== lastProgress[p.playerUid]) {
            lastProgress[p.playerUid] = progress;
            log(progress);
        }
    });
}

// ---------- 渲染成员列表 ----------
function renderMembers() {
    if (typeof renderControlRoom === 'function') renderControlRoom();
}

// ---------- 按真实行高折叠标签（配置组/一条龙） ----------
function applyTagFold(card) {
    const folds = card.querySelectorAll('.tag-fold');
    if (!folds.length) return;
    // 需要等 DOM 布局完成才能量到尺寸
    requestAnimationFrame(() => {
        folds.forEach(container => {
            const maxLines = parseInt(container.dataset.maxlines) || 3;
            const tags = Array.from(container.querySelectorAll('.tag'));
            if (tags.length === 0) return;
            const total = tags.length;
            // 相对容器顶部测量（getBoundingClientRect 差值不受 offsetParent 影响，可靠）
            const containerTop = container.getBoundingClientRect().top;
            const lineHeight = (tags[0].getBoundingClientRect().height || 20) + 4; // + gap 4
            const maxHeight = maxLines * lineHeight;
            // 找第一个进入超行区的标签（其相对 Y >= maxHeight）
            let keepCount = total;
            for (let i = 0; i < total; i++) {
                const relY = tags[i].getBoundingClientRect().top - containerTop;
                if (relY >= maxHeight) { keepCount = i; break; }
            }
            if (keepCount >= total) return; // 没超行，不折叠
            // 移除超出的标签
            for (let i = keepCount; i < total; i++) tags[i].remove();
            // 尾部追加"更多"按钮（复用事件委托的 .tag-more，显示总数）
            const uid = container.dataset.uid;
            const memberName = container.dataset.name;
            const type = container.dataset.type;
            const moreBtn = document.createElement('button');
            moreBtn.className = 'tag-more' + (type === 'oneclick' ? ' purple' : '');
            moreBtn.dataset.uid = uid;
            moreBtn.dataset.name = memberName;
            moreBtn.dataset.type = type;
            moreBtn.textContent = `更多(${total})`;
            container.appendChild(moreBtn);
        });
    });
}
// ---------- 成员卡片点击事件委托（按钮+标签，全局只绑一次） ----------
function memberSourceFromCard(element, uid) {
    if (!webMemberRooms()) return null;
    const entry = typeof renderedCardEntry === 'function' ? renderedCardEntry(element) : null;
    if (!entry?.source || entry.source.uid !== uid) throw new Error('原成员卡片已变化，请重新选择');
    return entry.source;
}
document.getElementById('memberList').addEventListener('click', e => {
    // "更多"按钮 → 弹窗查看全部配置组/一条龙
    const moreBtn = e.target.closest('.tag-more');
    if (moreBtn) {
        const uid = moreBtn.dataset.uid;
        const memberName = moreBtn.dataset.name;
        const type = moreBtn.dataset.type;
        if (!uid || !type) return;
        try { showTagMoreModal(uid, memberName, type, memberSourceFromCard(moreBtn, uid)); }
        catch (err) { log(err.message); }
        return;
    }

    // 配置组/一条龙标签 → 确认弹窗
    const tag = e.target.closest('.tag');
    if (!tag) return;
    const uid = tag.dataset.uid;
    const memberName = tag.dataset.name;
    const type = tag.dataset.type;
    const value = tag.dataset.value;
    if (!uid || !value) return;
    try { showMemberConfirmModal(uid, memberName, value, type === 'oneclick', memberSourceFromCard(tag, uid)); }
    catch (err) { log(err.message); }
});

// ---------- "更多"按钮 → 弹窗查看全部配置组/一条龙 ----------
function showTagMoreModal(uid, memberName, type, source = undefined) {
    const entry = readMemberEntry(uid, source), member = entry?.member;
    if (!member) { log('原成员来源已变化，请重新选择'); return; }
    const frozen = entry.source, targetName = memberTargetLabel(entry, memberName);

    const isOneClick = type === 'oneclick';
    const items = isOneClick ? (member.oneClickConfigs || []) : (member.configGroups || []);
    const label = isOneClick ? '一条龙' : '配置组';
    if (items.length === 0) {
        log(`${escapeHtml(memberName)} 没有可用${label}`);
        return;
    }

    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
        <div class="modal">
            <h3>${label}列表</h3>
            <p style="font-size:13px;color:#6E6E73;margin-bottom:12px;">「${escapeHtml(targetName)}」共 ${items.length} 个${label}，点击即可下发</p>
            <div class="tags modal-tags">
                ${items.map(g =>
                    `<span class="tag${isOneClick ? ' purple' : ''}" data-uid="${escapeHtml(uid)}" data-name="${escapeHtml(memberName)}" data-type="${type}" data-value="${escapeHtml(g)}">${escapeHtml(g)}</span>`
                ).join('')}
            </div>
            <div class="btn-row">
                <button class="btn btn-secondary" id="tmClose">关闭</button>
            </div>
        </div>`;
    document.body.appendChild(overlay);

    // 关闭按钮
    overlay.querySelector('#tmClose').addEventListener('click', () => overlay.remove());
    // 点击遮罩关闭
    overlay.addEventListener('click', e => {
        if (e.target === overlay) { overlay.remove(); return; }
    });
    // 弹窗内标签点击 → 与卡片一致：确认后下发
    overlay.querySelectorAll('.tag').forEach(tag => {
        tag.addEventListener('click', () => {
            const v = tag.dataset.value;
            if (!v) return;
            overlay.remove();
            showMemberConfirmModal(uid, memberName, v, isOneClick, frozen);
        });
    });
}
// ---------- 一键快捷指令 ----------
document.getElementById('quickRow').addEventListener('click', e => {
    const btn = e.target.closest('.quick-btn');
    if (!btn) return;
    const key = btn.dataset.key;
    showCoordinatedConfirm(key);
});

function parseQuickCommandBinding(binding) {
    if (typeof binding !== 'string') return null;
    const isOneClick = binding.startsWith('ONEDRAGON:');
    if (!isOneClick && !binding.startsWith('GROUP:')) return null;
    const name = binding.slice(isOneClick ? 10 : 6);
    return name.trim() && name === name.trim() && name.length <= 1024 ? { name, isOneClick } : null;
}

function selectedMainTargetUids() {
    return webMemberRooms() && typeof selectedMemberUids !== 'undefined'
        ? [...selectedMemberUids] : getSelectedMembers().map(p => p.playerUid);
}
function showQuickCommandConfirm(key, mainScope = captureMainMemberScope()) {
    const selectedTargets = selectedMainTargetUids();
    if (!selectedTargets.length) { log('请先选择在线成员'); return; }
    let sources;
    try { requireMainMemberScope(mainScope); sources = freezeMemberTargets(selectedTargets); }
    catch (err) { log('未打开快捷任务：' + err.message); return; }
    const parsed = parseQuickCommandBinding(loadBindings()[key]);
    showConfirmModal(key, parsed?.name || '', parsed?.isOneClick || false, result => {
        if (result === 'cancel') return;
        try { requireMainMemberScope(mainScope); }
        catch (err) { log(err.message); return; }
        if (result === 'confirm' && parsed) {
            // 不勾选仍保留网页原来的统一任务、起点及启用状态选择。
            let tasks = null;
            let tasksWithStatus = null;
            for (const p of players) {
                const dict = parsed.isOneClick ? (p.oneClickTasks || {}) : (p.configGroupTasks || {});
                if (Array.isArray(dict[parsed.name])) tasks = dict[parsed.name];
                const dictWs = parsed.isOneClick ? (p.oneClickTasksWithStatus || {}) : (p.configGroupTasksWithStatus || {});
                if (Array.isArray(dictWs[parsed.name])) tasksWithStatus = dictWs[parsed.name];
                if (tasks && tasksWithStatus) break;
            }
            showTaskListSelect('*', '已选成员', parsed.name, parsed.isOneClick, tasks, selectedTargets, tasksWithStatus, undefined, sources, mainScope);
        } else if (result === 'modify') {
            showBindModal(key, newBind => {
                if (!newBind) return;
                try { requireMainMemberScope(mainScope); } catch (err) { log(err.message); return; }
                const bindings = loadBindings();
                bindings[key] = newBind;
                saveBindings(bindings);
                log(`${escapeHtml(key)} 已绑定: ${newBind}`);
                showQuickCommandConfirm(key);
            });
        }
    }, mainScope, sources);
}

function isValidCompletionAction(action) {
    if (!action || !['resume', 'stop', 'runSpecified', 'switchRoom'].includes(action.policy)
        || !['group', 'onedragon', 'workflow'].includes(action.taskType)
        || typeof action.taskName !== 'string' || action.taskName.length > 1024
        || action.priority !== undefined && (!Number.isInteger(action.priority)
            || action.priority < -2147483648 || action.priority > 2147483647)) return false;
    const mode = action.startMode === undefined ? 'default' : action.startMode;
    const fields = ['startTaskId', 'startTaskName', 'configRevision', 'workflowId'];
    if (!['default', 'beginning', 'node'].includes(mode)
        || fields.some(key => action[key] !== undefined && (typeof action[key] !== 'string' || action[key].length > 1024))) return false;
    if (action.policy === 'switchRoom') {
        // 与 CoordinatedCompletionAction.IsValid 同一传输合同；仅确认摘要，不在网页验证或重建本机房间配置。
        const text = value => typeof value === 'string' && value.length <= 1024 && !!value.trim();
        if (!text(action.targetName) || !text(action.targetRoomCode) || typeof action.runAfterSwitch !== 'boolean'
            || typeof action.actionRevision !== 'string' || action.actionRevision.length !== 67
            || !/^v1:[0-9a-fA-F]{64}$/.test(action.actionRevision)) return false;
        if (!action.runAfterSwitch) return true;
        if (mode === 'default') return false;
    } else if (action.policy !== 'runSpecified') return true;
    const [id, name, revision, workflowId] = fields.map(key => action[key] ?? '');
    if (!action.taskName.trim() || (action.taskType === 'workflow' ? !workflowId.trim() : workflowId.length > 0)) return false;
    if (mode === 'default') return action.taskType !== 'workflow' && !id && !name && !revision;
    if (!revision.trim()) return false;
    return mode === 'beginning' ? !id && !name : !!id.trim() && !!name.trim();
}

// default 只兼容旧配置入口，不能将其描述为保证从头执行。
function specifiedCompletionDescription(action) {
    const type = { group: '配置组', onedragon: '一条龙', workflow: '计划表' }[action.taskType] || '任务';
    const mode = action.startMode ?? 'default';
    const start = mode === 'node' ? `从任务「${action.startTaskName || ''}」开始`
        : mode === 'beginning' ? '从头开始' : mode === 'default' ? '沿用原入口（旧配置）' : '起点未确认';
    return `执行${type}「${action.taskName || ''}」 · ${start}`;
}
function hoeingCompletionDescription(action) {
    if (!action) return '尚未上报（需更新该成员助手）';
    if (action.policy === 'switchRoom') {
        const target = [action.targetName, action.targetRoomCode].filter(value => typeof value === 'string' && value.trim()).join(' · ');
        return `切换助手房间${target ? `「${target}」` : '（未选择目标）'}`
            + (action.runAfterSwitch === true ? `，切换成功后${specifiedCompletionDescription(action)}（优先级 ${action.priority ?? 0}）`
                : action.runAfterSwitch === false ? '，切换成功后待命' : '，后继动作未确认')
            + (isValidCompletionAction(action) ? '' : '（配置无效，请核对）');
    }
    if (!isValidCompletionAction(action)) return '动作配置不完整';
    if (action.policy === 'resume') return '恢复原任务';
    if (action.policy === 'stop') return '锄完停止';
    return specifiedCompletionDescription(action);
}
function completionChoiceExplanation(targets, quickCommand = false) {
    return '正常完成后执行各成员已有动作；失败或手动停止不触发。'
        + (targets.some(p => p.hoeingCompletionAction?.policy === 'switchRoom')
            ? quickCommand ? ' 配置了切房的成员仅在实际联机锄地正常完成并结清后切房，纯普通任务完成时仅停止。'
                : ' 切房须有实际联机锄地正常完成并结清的证据。'
            : '');
}

// 仅序列化桌面与协调器的公开摘要字段；旧三种动作的缺省字段保持兼容。
function completionActionForConfirmation(action) {
    if (!action) return null;
    const { policy, taskType, taskName, priority = 0, startMode = 'default', startTaskId = '', startTaskName = '',
        configRevision = '', workflowId = '' } = action;
    const room = policy === 'switchRoom' ? action : {};
    const { targetName = '', targetRoomCode = '', runAfterSwitch = false, actionRevision = '' } = room;
    return { policy, taskType, taskName, priority, startMode, startTaskId, startTaskName, configRevision, workflowId,
        targetName, targetRoomCode, runAfterSwitch, actionRevision };
}
function completionActionSignature(action) {
    const summary = completionActionForConfirmation(action);
    // 显示名变化不改变目标身份；其余切房及后继任务字段均须与确认时一致。
    if (summary?.policy === 'switchRoom') summary.targetName = '';
    return JSON.stringify(summary);
}
function memberBinding(member) {
    const names = member?.onlineHoeingGroupNames || [];
    const types = member?.onlineHoeingGroupTypes?.length ? member.onlineHoeingGroupTypes : names.map(() => 'group');
    return { names: [...names], types: [...types] };
}
function snapshotForStart(targets) {
    return targets.map(p => ({ ...p, onlineHoeingGroupNames: [...(p.onlineHoeingGroupNames || [])],
        onlineHoeingGroupTypes: [...(p.onlineHoeingGroupTypes || [])], quickCommands: { ...p.quickCommands },
        hoeingCompletionAction: p.hoeingCompletionAction ? { ...p.hoeingCompletionAction } : null }));
}
function startSettingsSignature(p, key, useActions = true) {
    return JSON.stringify({ binding: key ? p?.quickCommands?.[key] : memberBinding(p),
        ...(useActions ? { action: completionActionSignature(p?.hoeingCompletionAction) } : {}) });
}

// 一键锄地与PC共用服务端批次；全部成员准备完成后才允许接管和执行。
function showQuickHoeingConfirmModal() {
    const targets = snapshotForStart(getSelectedMembers()), mainScope = captureMainMemberScope();
    if (targets.length === 0) {
        log('没有在线成员可下发一键锄地');
        return;
    }
    let sources;
    try {
        sources = freezeMemberTargets(selectedMainTargetUids());
        if (targets.length !== sources.size) throw new Error('有已选成员不在原控制房间，请重新选择');
        requireMainMemberTargets(mainScope, targets.map(p => p.playerUid), sources);
    } catch (err) { log('未打开一键锄地：' + err.message); return; }
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
        <div class="modal">
            <h3>确认下发一键锄地</h3>
            <p>以下成员全部准备就绪后，按顺序执行各自绑定的联机任务。未绑定或版本不支持时整批不启动。</p>
            <div class="list-items" id="hoeingMembers"></div>
            <label style="display:block;margin:14px 0 6px"><input type="checkbox" id="hoeingCompletionActions"> 执行各成员在“绑定”中设置的锄地后动作</label>
            <p id="hoeingCompletionHint" style="font-size:12px">不勾选：本次锄完停止。勾选后正常完成才执行已有动作，失败或手动停止不触发。</p>
            <div class="btn-row">
                <button class="btn btn-secondary" id="hoeingCancel">取消</button>
                <button class="btn btn-primary" id="hoeingOk">确认下发</button>
            </div>
        </div>`;
    const list = overlay.querySelector('#hoeingMembers');
    targets.forEach(p => {
        const row = document.createElement('div');
        row.className = 'list-item';
        const groups = p.onlineHoeingGroupNames || [];
        row.style.whiteSpace = 'pre-line';
        row.textContent = `${p.playerName || p.playerUid}：${groups.length ? groups.join(' → ') : '未绑定联机任务（需先绑定）'}\n锄地后动作：${hoeingCompletionDescription(p.hoeingCompletionAction)}`;
        list.appendChild(row);
    });
    document.body.appendChild(overlay);
    const completionOption = overlay.querySelector('#hoeingCompletionActions');
    const hint = overlay.querySelector('#hoeingCompletionHint');
    completionOption.disabled = !serverCapabilities.includes('hoeing.completionChoice.v1')
        || targets.some(p => !isValidCompletionAction(p.hoeingCompletionAction));
    const updateChoice = () => {
        hint.textContent = completionOption.disabled
            ? '服务端或成员尚不支持此选项，或绑定动作不完整；不勾选仍可按原方式开始。'
            : completionOption.checked ? completionChoiceExplanation(targets) : '不勾选：本次锄完停止。';
    };
    completionOption.addEventListener('change', updateChoice);
    updateChoice();
    const confirm = overlay.querySelector('#hoeingOk');
    const cancel = overlay.querySelector('#hoeingCancel');
    cancel.addEventListener('click', () => overlay.remove());
    confirm.addEventListener('click', async () => {
        if (confirm.disabled) return;
        confirm.disabled = true;
        cancel.disabled = true;
        try {
            await beginCoordinatedQuickCommand(targets, !completionOption.disabled && completionOption.checked, '', '一键锄地', mainScope, sources);
        } finally { overlay.remove(); }
    });
}

// 确认后的精确成员快照；已受理、拒绝和传输未知分别处理，不重发开始。
async function beginCoordinatedQuickCommand(targets, useCompletionActions, quickCommandKey, label,
    mainScope = captureMainMemberScope(), sources = null) {
    const refuse = detail => {
        const message = label + detail;
        log(message);
        return { status: 'failed', message };
    };
    // 冻结确认列表；有人离线或具体实例已变化时整批不下发，不能悄悄缩成更少成员。
    const targetUids = [...new Set(targets.map(p => p.playerUid))];
    if (webMemberRooms()) {
        try {
            sources ||= freezeMemberTargets(targetUids);
            requireMainMemberTargets(mainScope, targetUids, sources);
        } catch (err) { return refuse('未下发：' + err.message); }
    }
    const latestMember = uid => sources && webMemberRooms()
        ? (typeof currentMember === 'function' ? currentMember(uid, sources.get(uid)) : readMemberEntry(uid, sources.get(uid))?.member)
        : players.find(p => p.playerUid === uid);
    if (targetUids.some(uid => !latestMember(uid)?.online))
        return refuse('未下发：有成员已离线，请重新确认');
    if (useCompletionActions && (!serverCapabilities.includes('hoeing.completionChoice.v1')
        || targets.some(p => !isValidCompletionAction(p.hoeingCompletionAction)))
        || quickCommandKey && !serverCapabilities.includes('hoeing.quickCommandCompletion.v1'))
        return refuse('未下发：服务端或成员尚不支持完成动作，或绑定动作不完整');
    if (targets.some(p => {
        const current = latestMember(p.playerUid);
        return startSettingsSignature(current, quickCommandKey, useCompletionActions) !== startSettingsSignature(p, quickCommandKey, useCompletionActions)
            || useCompletionActions && !isValidCompletionAction(current?.hoeingCompletionAction);
    })) return refuse('未下发：成员绑定或锄地后动作已改变，请重新确认');
    // 来自弹窗中实际展示的各成员摘要，不从最新状态补齐或替执行端写设置；未勾选不携带动作。
    const confirmedCompletionActions = useCompletionActions
        ? Object.fromEntries(targets.map(p => [p.playerUid, completionActionForConfirmation(p.hoeingCompletionAction)])) : null;
    const requestId = 'web_hoeing_' + newRequestId(), originalConnection = connection;
    try {
        if (!serverCapabilities.includes('hoeing.start.v2'))
            return refuse('未受理：服务器尚未支持统一联机开始，请升级配套服务器');
        const response = await originalConnection.invoke('Dispatch', makeEnvelope('command', 'control.batch.begin', {
            requestId, targetUids,
            ...(quickCommandKey ? { quickCommandKey } : {}),
            useCompletionActions: useCompletionActions,
            ...(useCompletionActions ? { confirmedCompletionActions } : {}),
            expiresAtUtc: new Date(Date.now() + 120000).toISOString()
        }));
        checkGatewayError(response);
        // 原控制房间已改变时，不能将旧批次结果填进新房间的 UI。
        requireMainMemberScope(mainScope, false);
        const batch = response?.payload;
        if (!batch?.batchId || batch.protocol !== 'hoeing.start.v2')
            throw new Error('服务器未返回有效批次身份，受理结果待确认');
        const message = batch.phase === 'aborted'
            ? `${label}已中止：${batch.reason || '准备失败'}（批次 ${batch.batchId}）`
            : `${label}已受理：${targetUids.length} 名成员，正在全队准备；受理不代表已经起跑`;
        log(message);
        return { status: batch.phase === 'aborted' ? 'failed' : 'accepted', batch, message };
    } catch (err) {
        if (err.gatewayRejected) {
            const detail = err.message.includes('previous_batch_active')
                ? '已有联机批次正在执行或清理，请等待其结束'
                : err.message.includes('hoeing_start_capability_required')
                    ? '有参与成员尚未支持统一联机开始，请升级配套助手'
                    : err.message;
            const message = label + '未受理：' + detail;
            log(message);
            return { status: 'failed', message };
        } else {
            let recovered = null;
            try {
                requireMainMemberScope(mainScope, false);
                if (connection !== originalConnection) throw new Error('原连接已变化');
                const current = await originalConnection.invoke('Dispatch', makeEnvelope('command', 'control.batch.current', {}));
                checkGatewayError(current);
                requireMainMemberScope(mainScope, false);
                if (connection === originalConnection && current?.payload?.requestId === requestId) recovered = current.payload;
            } catch { /* 传输未知保留待确认，不另造请求重发。 */ }
            const message = recovered
                ? `已按原请求确认${label}批次 ${recovered.batchId}，状态：${batchPhaseName(recovered.phase)}`
                : label + '受理结果待确认，未重复发送：' + err.message;
            log(message);
            return { status: recovered ? 'accepted' : 'unknown', batch: recovered, message };
        }
    }
}

// ---------- 弹窗：绑定 ----------
function showBindModal(key, callback) {
    // 从成员列表收集可用配置组/一条龙（取第一个有配置的在线成员）
    const first = players.find(p => p.configGroups?.length > 0 || p.oneClickConfigs?.length > 0);
    const groups = first ? (first.configGroups || []) : [];
    const oneclicks = first ? (first.oneClickConfigs || []) : [];
    const all = [
        ...groups.map(g => ({ label: '[配置组] ' + g, value: 'GROUP:' + g })),
        ...oneclicks.map(o => ({ label: '[一条龙] ' + o, value: 'ONEDRAGON:' + o }))
    ];

    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
        <div class="modal">
            <h3>绑定「${escapeHtml(key)}」</h3>
            <p style="font-size:13px;color:#6E6E73;margin-bottom:12px;">选择要执行的配置组或一条龙（来自成员列表）</p>
            ${all.length === 0 ? '<p style="color:#FF3B30;font-size:13px;">暂无可用配置，请在成员列表同步后再试</p>' : ''}
            <div class="list-items" id="bindList">
                ${all.map((item, i) => `<div class="list-item" data-index="${i}" data-value="${escapeHtml(item.value)}">${escapeHtml(item.label)}</div>`).join('')}
            </div>
            <div class="btn-row">
                <button class="btn btn-secondary" id="bindCancel">取消</button>
                <button class="btn btn-primary" id="bindOk">绑定</button>
            </div>
        </div>`;
    document.body.appendChild(overlay);

    let selected = null;
    const items = overlay.querySelectorAll('.list-item');
    items.forEach(el => {
        el.addEventListener('click', () => {
            items.forEach(x => x.classList.remove('selected'));
            el.classList.add('selected');
            selected = el.dataset.value;
        });
    });
    overlay.querySelector('#bindOk').addEventListener('click', () => {
        if (selected) {
            callback(selected);
            overlay.remove();
        } else if (all.length === 0) {
            callback(null);
            overlay.remove();
        }
    });
    overlay.querySelector('#bindCancel').addEventListener('click', () => {
        callback(null);
        overlay.remove();
    });
}

// ---------- 弹窗：确认（含修改按钮） ----------
function showConfirmModal(key, value, isOneClick, callback, mainScope = captureMainMemberScope(), sources = null) {
    const quickCommandKey = '一键' + key;
    const members = snapshotForStart(getSelectedMembers());
    try {
        requireMainMemberScope(mainScope);
        sources ||= freezeMemberTargets(selectedMainTargetUids());
        if (members.length !== sources.size) throw new Error('有已选成员不在原控制房间，请重新选择');
    } catch (err) { log(err.message); return; }
    // 与桌面端一致：未绑定的成员只作说明，不下发该快捷指令。
    const targets = members.filter(p => parseQuickCommandBinding(p.quickCommands[quickCommandKey]));
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
        <div class="modal">
            <h3></h3>
            <p id="quickSummary" style="font-size:14px;margin-bottom:12px"></p>
            <label style="display:block;margin:14px 0 6px"><input type="checkbox" id="quickCompletionActions"> 执行各成员在“绑定”中设置的锄地后动作</label>
            <p id="quickCompletionHint" style="font-size:12px"></p>
            <div class="list-items" id="quickMembers" hidden></div>
            <div class="btn-row" style="flex-wrap:wrap">
                <button class="btn btn-secondary" id="confCancel">取消</button>
                <button class="btn btn-secondary" id="confModify">修改网页任务</button>
                <button class="btn btn-primary" id="confOk">确认</button>
            </div>
        </div>`;
    overlay.querySelector('h3').textContent = `确认下发「${escapeHtml(key)}」`;
    const summary = overlay.querySelector('#quickSummary');
    const completionOption = overlay.querySelector('#quickCompletionActions');
    const hint = overlay.querySelector('#quickCompletionHint');
    const list = overlay.querySelector('#quickMembers');
    const confirm = overlay.querySelector('#confOk');
    const modify = overlay.querySelector('#confModify');
    const cancel = overlay.querySelector('#confCancel');
    members.forEach(p => {
        const row = document.createElement('div');
        row.className = 'list-item';
        row.style.whiteSpace = 'pre-line';
        const task = parseQuickCommandBinding(p.quickCommands[quickCommandKey]);
        row.textContent = `${p.playerName || p.playerUid}：${task ? (task.isOneClick ? '一条龙「' : '配置组「') + task.name + '」' : '未绑定，本次不下发'}\n锄地后动作：${hoeingCompletionDescription(p.hoeingCompletionAction)}`;
        list.appendChild(row);
    });
    completionOption.disabled = !serverCapabilities.includes('hoeing.quickCommandCompletion.v1')
        || !serverCapabilities.includes('hoeing.completionChoice.v1') || targets.length === 0
        || targets.some(p => !isValidCompletionAction(p.hoeingCompletionAction));
    function updateChoice() {
        const useActions = !completionOption.disabled && completionOption.checked;
        summary.textContent = useActions
            ? `从头执行以下 ${targets.length} 个成员各自为「${escapeHtml(key)}」绑定的任务。`
            : value ? `对已选成员执行网页任务：${isOneClick ? '一条龙' : '配置组'}「${escapeHtml(value)}」。`
                : '尚未设置网页任务，请选择“修改网页任务”；也可勾选使用成员已有快捷绑定。';
        list.hidden = !useActions;
        hint.textContent = completionOption.disabled
            ? '服务端尚不支持此选项、成员未绑定该快捷指令，或锄地后动作尚未上报完整；不勾选仍可执行网页任务。'
            : useActions ? completionChoiceExplanation(targets, true)
                : '不勾选：本次锄完停止。';
        confirm.disabled = !useActions && !value;
    }
    completionOption.addEventListener('change', updateChoice);
    updateChoice();
    document.body.appendChild(overlay);
    confirm.addEventListener('click', async () => {
        if (confirm.disabled) return;
        const useActions = !completionOption.disabled && completionOption.checked;
        confirm.disabled = true;
        modify.disabled = true;
        cancel.disabled = true;
        if (!useActions) { overlay.remove(); callback('confirm'); return; }
        try { await beginCoordinatedQuickCommand(targets, true, quickCommandKey, key, mainScope, sources); }
        finally { overlay.remove(); }
    });
    modify.addEventListener('click', () => { overlay.remove(); callback('modify'); });
    cancel.addEventListener('click', () => { overlay.remove(); callback('cancel'); });
}

// ---------- 弹窗：确认对指定成员下发配置组/一条龙（含"从此处开始执行"任务列表） ----------
function showMemberConfirmModal(uid, memberName, value, isOneClick, source = undefined) {
    const entry = readMemberEntry(uid, source), member = entry?.member;
    if (!member) { log('原成员来源已变化，请重新选择'); return; }
    showTaskListSelect(uid, memberName, value, isOneClick,
        (isOneClick ? member.oneClickTasks : member.configGroupTasks)?.[value] || null,
        null, (isOneClick ? member.oneClickTasksWithStatus : member.configGroupTasksWithStatus)?.[value] || null, entry.source);
}

// ---------- 弹窗：选择成员的配置组/一条龙并下发（含"从此处开始执行"任务列表） ----------
function showMemberConfigSelect(uid, memberName, type, source = undefined) {
    const entry = readMemberEntry(uid, source), member = entry?.member;
    if (!member) { log(`找不到原成员 ${escapeHtml(memberName)} 的配置信息，请重新选择`); return; }
    const frozen = entry.source, targetName = memberTargetLabel(entry, memberName);

    const isOneClick = type === 'oneclick';
    const items = isOneClick ? (member.oneClickConfigs || []) : (member.configGroups || []);
    const label = isOneClick ? '一条龙' : '配置组';

    if (items.length === 0) {
        log(`${escapeHtml(memberName)} 没有可用${label}`);
        return;
    }

    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
        <div class="modal">
            <h3>选择${label}</h3>
            <p style="font-size:13px;color:#6E6E73;margin-bottom:12px;">选择对「${escapeHtml(targetName)}」下发的${label}</p>
            <div class="list-items">
                ${items.map((item, i) => `<div class="list-item" data-index="${i}" data-value="${escapeHtml(item)}">${escapeHtml(item)}</div>`).join('')}
            </div>
            <div class="btn-row">
                <button class="btn btn-secondary" id="mcfgCancel">取消</button>
                <button class="btn btn-primary" id="mcfgOk">确认</button>
            </div>
        </div>`;
    document.body.appendChild(overlay);

    let selected = null;
    const listItems = overlay.querySelectorAll('.list-item');
    listItems.forEach(el => {
        el.addEventListener('click', () => {
            listItems.forEach(x => x.classList.remove('selected'));
            el.classList.add('selected');
            selected = el.dataset.value;
        });
    });
    overlay.querySelector('#mcfgOk').addEventListener('click', () => {
        if (selected) {
            overlay.remove();
            showMemberConfirmModal(uid, memberName, selected, isOneClick, frozen);
        }
    });
    overlay.querySelector('#mcfgCancel').addEventListener('click', () => overlay.remove());
}

// ---------- 弹窗：快捷键选择 ----------
function showHotkeySelect(uid, memberName, host = document.body, onError = null, source = undefined) {
    const scoped = !!globalThis.window?.webRooms || source !== undefined;
    const entry = scoped ? readMemberEntry(uid, source) : null;
    if (scoped && !entry) { const message = '原成员连接或实例已变化，请重新选择快捷键目标'; log(message); if (typeof onError === 'function') onError(message); return; }
    const member = scoped ? entry?.member : players.find(p => p.playerUid === uid);
    const frozen = entry?.source || null, targetName = entry ? memberTargetLabel(entry, memberName) : memberName;
    const hotkeys = member ? member.hotkeys || [] : [];
    if (!hotkeys || hotkeys.length === 0) {
        log(`${escapeHtml(memberName)} 没有可用的快捷键`);
        return;
    }

    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
        <div class="modal">
            <h3>选择快捷键</h3>
            <p style="font-size:13px;color:#6E6E73;margin-bottom:12px;">选择要对「${escapeHtml(targetName)}」执行的快捷键</p>
            <div class="list-items">
                ${hotkeys.map((hk, i) => {
                    const funcName = hk.functionName || '';
                    const hotkeyText = hk.hotkeyText || '';
                    return `<div class="list-item" data-index="${i}" data-config-name="${escapeHtml(hk.configName || '')}">
                        ${escapeHtml(funcName)} <span style="font-size:11px;color:#6E6E73;">(${escapeHtml(hotkeyText)})</span>
                    </div>`;
                }).join('')}
            </div>
            <div class="btn-row">
                <button class="btn btn-secondary" id="hkCancel">取消</button>
                <button class="btn btn-primary" id="hkOk">执行</button>
            </div>
        </div>`;
    host.appendChild(overlay);

    let selected = null;
    const items = overlay.querySelectorAll('.list-item');
    items.forEach(el => {
        el.addEventListener('click', () => {
            items.forEach(x => x.classList.remove('selected'));
            el.classList.add('selected');
            selected = el.dataset.configName;
        });
    });
    if (items.length > 0) {
        items[0].classList.add('selected');
        selected = items[0].dataset.configName;
    }

    overlay.querySelector('#hkOk').addEventListener('click', async () => {
        overlay.querySelector('#hkOk').disabled = true;
        if (selected) {
            const cmd = makeCmd('hotkey_execute', { hotkeyConfigName: selected });
            cmd.target = [uid];
            try {
                if (frozen && !(requireMemberTarget(uid, frozen).member.hotkeys || []).some(h => h.configName === selected))
                    throw new Error('该成员快捷键已变化，请重新选择');
                await sendTrackedCommand(cmd, null, frozen);
            } catch (err) {
                const message='快捷键未完成: ' + err.message;log(message);
                if(typeof onError==='function')onError(message);
            }
        }
        overlay.remove();
    });
    overlay.querySelector('#hkCancel').addEventListener('click', () => overlay.remove());
}

// ---------- 弹窗：任务列表选择（"从此处开始执行" + 启用状态编辑） ----------
function showTaskListSelect(uid, memberName, configName, isOneClick, tasks, targetList, tasksWithStatus,
    source = undefined, frozenTargets = null, mainScope = captureMainMemberScope()) {
    const label = isOneClick ? '一条龙' : '配置组';
    // 旧网页快捷绑定只使用主房选中的成员；普通单卡固定其所属房间和实例。
    const bulk = !!targetList || uid === '*';
    if (bulk && source) { log('不能将单卡来源用于批量任务，本次未发送'); return; }
    const entry = bulk ? null : readMemberEntry(uid, source);
    if (!bulk && !entry) { log('原成员来源已变化，请重新选择'); return; }
    const targets = resolveConfigTargets(targetList || [uid], entry?.context.players || players);
    let sources;
    try {
        sources = frozenTargets || freezeMemberTargets(targets, bulk ? undefined : entry.source);
        if (bulk) requireMainMemberTargets(mainScope, targets, sources);
        else {
            if (memberSourceKey(uid, sources.get(uid)) !== memberSourceKey(uid, entry.source))
                throw new Error('任务启动须保留原单卡来源');
            requireFrozenMemberTargets(targets, sources);
        }
    } catch (err) { log('未打开任务：' + err.message); return; }
    const targetName = entry ? memberTargetLabel(entry, memberName) : memberName;
    const showRoom = entry?.source && (!entry.context.isMain || webMemberRooms()?.getView().mode === 'all');
    // 构建任务选项列表：第一项"从头开始"，后面是真实任务名
    const options = [{ index: 0, text: '从头开始', sub: '第一个任务', isTask: false }];
    if (tasks && Array.isArray(tasks)) {
        tasks.forEach((t, i) => {
            const statusInfo = tasksWithStatus && Array.isArray(tasksWithStatus) && i < tasksWithStatus.length
                ? tasksWithStatus[i] : null;
            const enabled = statusInfo
                ? (isOneClick ? (statusInfo.enabled !== false) : (statusInfo.status !== 'Disabled'))
                : true;
            // 真实任务键（R3 原生身份）：一条龙采信条目自带的 taskId（TaskEnabledList 的 GUID 键）；
            // 配置组保持位置序号（BGI 端两个消费端都先把 Index 按位置重排为 1..N 再匹配，只认位置）。
            // 对齐 WPF 端 MainViewModel.ShowStartFromDialog 的分支纪律。
            const taskKey = isOneClick
                ? (statusInfo && typeof statusInfo.taskId === 'string' && statusInfo.taskId ? statusInfo.taskId : '')
                : i + 1;
            options.push({
                index: taskKey,
                text: `${i + 1}. ${t}`,
                sub: '',
                isTask: true,
                status: statusInfo,
                enabled: enabled
            });
        });
    }

    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
        <div class="modal">
            <h3>从此处开始执行</h3>
            ${showRoom ? `<p class="note">目标：${escapeHtml(targetName)}</p>` : ''}
            <p style="font-size:13px;color:#1D1D1F;margin-bottom:4px;font-weight:600;">「${escapeHtml(configName)}」${tasks ? '共 ' + tasks.length + ' 个任务' : ''}</p>
            <p style="font-size:12px;color:#6E6E73;margin-bottom:10px;">选择从哪个任务开始（勾选切换启用状态）</p>
            <div class="list-items" style="max-height:300px;overflow-y:auto;">
                ${options.map((opt, i) => {
                    if (i === 0) {
                        return `<div class="list-item" data-index="0" style="display:flex;justify-content:space-between;align-items:center;">
                            <span>从头开始</span>
                            <span style="font-size:11px;color:#6E6E73;">第一个任务</span>
                        </div>`;
                    }
                    const checked = opt.enabled ? 'checked' : '';
                    return `<div class="list-item task-row" data-index="${escapeHtml(opt.index)}" style="display:flex;align-items:center;gap:8px;">
                        <input type="checkbox" class="task-checkbox" ${checked} data-index="${escapeHtml(opt.index)}">
                        <span>${escapeHtml(opt.text)}</span>
                    </div>`;
                }).join('')}
            </div>
            <p class="note" id="tskResult" role="status" hidden></p>
            <div class="btn-row">
                <button class="btn btn-secondary" id="tskCancel">取消</button>
                <button class="btn btn-primary" id="tskOk">确认</button>
            </div>
        </div>`;
    document.body.appendChild(overlay);

    const okBtn = overlay.querySelector('#tskOk');
    const resultLine = overlay.querySelector('#tskResult');
    const commandName = isOneClick ? 'start_oneclick' : 'start_group';
    let selectedIndex = 0; // 配置组=数字位置；一条龙=GUID taskId 字符串（"0"=从头开始）
    const checkboxes = overlay.querySelectorAll('.task-checkbox');
    const listItems = overlay.querySelectorAll('.list-item');
    listItems.forEach(el => {
        el.addEventListener('click', (e) => {
            if (okBtn.disabled || e.target.classList.contains('task-checkbox')) return;
            listItems.forEach(x => x.classList.remove('selected'));
            el.classList.add('selected');
            selectedIndex = isOneClick ? el.dataset.index : Number(el.dataset.index);
        });
    });
    listItems[0].classList.add('selected');

    function validateTargets() {
        if (bulk) requireMainMemberTargets(mainScope, targets, sources);
        else requireFrozenMemberTargets(targets, sources);
        requireNoPendingMemberWrite(targets, commandName, sources);
        for (const pending of pendingConfigApplied.values()) {
            if (targets.some(uid => memberSourceKey(uid, sources.get(uid)) === memberSourceKey(pending.uid, pending.source)))
                throw new Error('有成员仍在等待上次的配置应用回执，本次未发送');
        }
        if (entry?.source) {
            const current = requireMemberTarget(uid, entry.source).member;
            const names = isOneClick ? current.oneClickConfigs : current.configGroups;
            if (Array.isArray(names) && !names.includes(configName))
                throw new Error('原成员的任务目录已变化，请重新选择任务');
            const currentTasks = (isOneClick ? current.oneClickTasks : current.configGroupTasks)?.[configName];
            if (Array.isArray(tasks) && (!Array.isArray(currentTasks) || JSON.stringify(tasks) !== JSON.stringify(currentTasks)))
                throw new Error('原成员的任务列表已变化，请重新选择起点');
            if (isOneClick && Array.isArray(tasksWithStatus)) {
                const currentStatus = current.oneClickTasksWithStatus?.[configName];
                const taskIds = list => list.map(t => t?.taskId || '');
                if (!Array.isArray(currentStatus) || JSON.stringify(taskIds(tasksWithStatus)) !== JSON.stringify(taskIds(currentStatus)))
                    throw new Error('原成员的一条龙任务标识已变化，请重新选择起点');
            }
        }
    }
    function setResult(message, error = false) {
        resultLine.textContent = message;
        resultLine.hidden = !message;
        resultLine.classList.toggle('error', error);
    }
    okBtn.addEventListener('click', async () => {
        if (okBtn.disabled || !overlay.isConnected) return;
        okBtn.disabled = true;
        setResult('');
        const changes = new Map();
        checkboxes.forEach((cb, i) => {
            cb.disabled = true;
            const original = options[i + 1];
            if (cb.checked !== original.enabled) changes.set(original.index, cb.checked);
        });

        // Serialize dependent writes per target, and require execution-side applied receipts.
        // Gateway forwarding acknowledgements never authorize a dependent start.
        try {
            validateTargets();
            if (isOneClick && (changes.has('') || selectedIndex === ''))
                throw new Error('该一条龙尚未上报任务标识，不能修改启用状态或从指定任务开始');
            if (changes.size) setResult('正在应用启用状态。此时取消将阻止启动，已发送的修改可能仍会生效。');
            const appliedByUid = new Map();
            for (const uid of targets) {
                for (const [taskIdx, en] of changes) {
                    validateTargets();
                    const previous = appliedByUid.get(uid);
                    // R3 原生身份：一条龙按 GUID taskId 寻址；配置组保持数字 taskIndex
                    const changeCmd = makeCmd('set_task_enabled', {
                        [isOneClick ? 'configName' : 'groupName']: configName,
                        ...(isOneClick ? { taskId: taskIdx } : { taskIndex: Number(taskIdx) }), enabled: en,
                        expectedConfigRevision: previous?.configRevision,
                        bgiEpoch: previous ? { processId: previous.targetProcessId, startTicksUtc: previous.targetStartTicksUtc } : undefined
                    });
                    changeCmd.target = [uid];
                    appliedByUid.set(uid, await sendAndWaitConfigApplied(changeCmd, uid, sources.get(uid)));
                    if (!overlay.isConnected) return;
                }
            }
            if (!overlay.isConnected) return;
            validateTargets();
            // 启动回执可能等到整项任务结束才返回。对已确认的成员逐一登记并下发，
            // 由成员状态和操作记录显示结果，不让另一个成员或弹窗等待任务执行完毕。
            for (const uid of targets) {
                const applied = appliedByUid.get(uid);
                // 一条龙用 taskId，配置组用数字位置；"0"/空表示一条龙从头开始。
                const cmd = makeCmd(commandName, {
                    [isOneClick ? 'configName' : 'groupName']: configName,
                    ...(isOneClick
                        ? (selectedIndex && selectedIndex !== '0' ? { startFromTaskId: selectedIndex } : {})
                        : { startFromIndex: selectedIndex }),
                    expectedConfigRevision: applied?.configRevision,
                    bgiEpoch: applied ? { processId: applied.targetProcessId, startTicksUtc: applied.targetStartTicksUtc } : undefined
                });
                cmd.target = [uid];
                void sendTrackedCommand(cmd, null, sources.get(uid));
            }
            overlay.remove();
        } catch (error) {
            const message = '保存/启动未完成：' + error.message;
            log(message);
            if (overlay.isConnected) setResult(message, true);
        } finally {
            if (overlay.isConnected) {
                okBtn.disabled = false;
                checkboxes.forEach(cb => { cb.disabled = false; });
            }
        }
    });
    overlay.querySelector('#tskCancel').addEventListener('click', () => overlay.remove());
}

// ---------- 设置弹窗 ----------
function openSettings() {
    // 正式页面在显示入口前已初始化房间簿；不再另行编辑迁移前的登录凭据。
    const rooms = webMemberRooms();
    if (typeof rooms?.openManager === 'function') return rooms.openManager();
    const creds = loadCreds() || {};
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
        <div class="modal">
            <h3>设置</h3>
            <label class="field-label">房间码</label>
            <input type="text" id="setRoomCode" value="${escapeHtml(creds.roomCode || '')}" placeholder="6 位房间码">
            <label class="field-label">密码</label>
            <input type="password" id="setPassword" value="${escapeHtml(creds.password || '')}" placeholder="密码">
            <label class="field-label">昵称</label>
            <input type="text" id="setNickname" value="${escapeHtml(creds.nickname || '')}" placeholder="昵称">
            <div class="btn-row" style="margin-top:20px;">
                <button class="btn btn-secondary" id="setCancel">取消</button>
                <button class="btn btn-primary" id="setSave">保存</button>
            </div>
        </div>`;
    document.body.appendChild(overlay);
    overlay.querySelector('#setSave').addEventListener('click', () => {
        const rc = overlay.querySelector('#setRoomCode').value.trim();
        const pw = overlay.querySelector('#setPassword').value.trim();
        const nn = overlay.querySelector('#setNickname').value.trim();
        if (rc && pw) {
            saveCreds({ roomCode: rc, password: pw, nickname: nn });
            log('设置已保存（下次加入时生效）');
        }
        overlay.remove();
    });
    overlay.querySelector('#setCancel').addEventListener('click', () => overlay.remove());
}
