// 槲寄生网页：目标选择与两条传输。
// 本机直连（从助手「本机网页」打开）走 /api/execute；其余走服务器转发 mistletoe.request。
// 执行端复用 PC 远程中心同一处理流程：请求身份、指纹、持久回执、重复启动防护都在执行端判定。
// 网页只做三件事：选准本人执行端、附带本人凭据、把「已发送 / 已受理 / 终态 / 未确认」如实显示。
const mt = {
    active:false, tab:'startup', target:null, identity:null, identityError:'', pending:new Map(),
    unconfirmed:null, busy:false, generation:0, pollTimer:null, lastReady:null, identityLoading:null, members:new Map()
};
const mtRoot = document.getElementById('mistletoeView');
const mtEl = id => document.getElementById(id);
const mte = value => escapeHtml(value);
const MT_CRED_KEY = 'bgi.mistletoe.owner.v1';
const MT_UNCONFIRMED_KEY = 'bgi.mistletoe.unconfirmed.v1';
const MT_READ_OPS = new Set(['web.identity','startup.get','startup.status','task.list','task.flow.get','task.flow.export','task.schedule.export','task.resource.layout.get','task.resource.revision.get','task.bgiPath.get','startup.programs.list','operation.get']);
/**
 * 执行端留有持久回执（operation.get 可查终态）的操作，与 PC 远程中心 UsesDurableCenterReceipt 一致。
 * 只有这些操作进入「未确认台账」：结果未知时阻断再次发起，核对后才解除。
 * 人工确认、取消触发器、清除路径显示按运行身份天然幂等，与 PC 一样不登记、不受台账阻挡。
 */
const MT_DURABLE_OPS = new Set(['startup.save','startup.schemes.save','startup.autoLaunch.save','startup.run',
    'task.resource.layout.save','task.resource.editor.open','task.bgiPath.save','task.flow.save','task.flow.delete','task.flow.restore',
    'task.catalog.connect','task.flow.import','task.schedule.import','task.migration.prepare','task.migration.activate',
    'task.migration.rollback','task.migration.rehearsal','task.run.start','task.run.resume','task.flow.resetExecutionState']);
function mtDurable(operation, payload) { return MT_DURABLE_OPS.has(operation) || operation === 'task.run.action' && payload?.action !== 'Stop'; }

// ---------- 本人凭据（按执行端实例保存；只在这个浏览器里；uid 只用于把它归到「我的执行端」） ----------
function mtCredentials() { try { return JSON.parse(localStorage.getItem(MT_CRED_KEY) || '{}') || {}; } catch { return {}; } }
function mtCredentialFor(target) { return target ? mtCredentials()[target.instanceId] || null : null; }
function mtSaveCredential(target, value) {
    const all = mtCredentials();
    if (value) all[target.instanceId] = value; else delete all[target.instanceId];
    try { localStorage.setItem(MT_CRED_KEY, JSON.stringify(all)); } catch { /* 浏览器禁用存储时只能本次使用 */ }
}

// ---------- 未确认操作台账：结果未知的执行类操作在核对前不允许再次发起 ----------
function mtLoadUnconfirmed() { try { return JSON.parse(localStorage.getItem(MT_UNCONFIRMED_KEY) || 'null'); } catch { return null; } }
function mtRemember(entry) { mt.unconfirmed = entry; try { if (entry) localStorage.setItem(MT_UNCONFIRMED_KEY, JSON.stringify(entry)); else localStorage.removeItem(MT_UNCONFIRMED_KEY); } catch {} }
mt.unconfirmed = mtLoadUnconfirmed();

// ---------- 目标：本机直连 + 已授权实例归入「我的执行端」；其余在线助手只能走授权码绑定 ----------
function mtIsLocalTarget(target = mt.target) { return !!target?.local; }
/**
 * 成员按「UID + 助手实例」各记一条。页面公用的 players 列表在增量更新时按 UID 合并，
 * 同一 UID 的多个执行端会互相覆盖；槲寄生必须按实例精确投递，所以自己维护一份。
 */
function mtMemberKey(p) { return p.playerUid + '\n' + (p.clientInstanceId || ''); }
function mtApplyMembers(update) {
    if (!update || typeof update !== 'object') return;
    if (update.full) { mt.members = new Map((update.players || []).filter(p => p?.playerUid).map(p => [mtMemberKey(p), p])); return; }
    for (const p of update.changed || []) if (p?.playerUid) mt.members.set(mtMemberKey(p), p);
    for (const uid of update.removed || []) for (const [k, p] of mt.members) if (p.playerUid === uid) mt.members.delete(k);
}
function mtMemberList() { return mt.members.size ? [...mt.members.values()] : (players || []); }
/** 已知属于本人的 UID：本机助手身份、已授权凭据记下的 UID。 */
function mtOwnUids() {
    const set = new Set(), own = globalThis.window?.bgiLocalBridge?.localIdentity?.();
    if (own?.uid) set.add(own.uid);
    for (const c of Object.values(mtCredentials())) if (c?.uid) set.add(c.uid);
    return set;
}
function mtTargets() {
    const list = [];
    const bridge = globalThis.window?.bgiLocalBridge;
    const own = bridge?.localIdentity?.();
    if (bridge && own) list.push({ key:'local', local:true, uid:own.uid, name:own.name || '本机助手', instanceId:own.clientInstanceId,
        machineId:own.machineId, online:!!bridge.available, isObserver:!!own.isObserver, mine:true });
    const creds = mtCredentials(), ownUids = mtOwnUids();
    for (const p of mtMemberList()) {
        if (!p.playerUid || String(p.playerUid).startsWith('web_') || !p.clientInstanceId) continue;
        if (own && p.clientInstanceId === own.clientInstanceId) continue;
        // 同一 UID 的多个实例逐个列出；按实例精确投递，不能合并。
        if (list.some(t => t.instanceId === p.clientInstanceId)) continue;
        list.push({ key:'remote:' + p.playerUid + ':' + p.clientInstanceId, local:false, uid:p.playerUid, name:p.playerName || p.playerUid,
            instanceId:p.clientInstanceId, machineId:p.machineId || '', online:p.online === true && !!roomReady, isObserver:false,
            mine:!!creds[p.clientInstanceId] || ownUids.has(p.playerUid) });
    }
    return list;
}
function mtTargetReady(target = mt.target) {
    if (!target) return false;
    if (target.local) return !!window.bgiLocalBridge?.available;
    return !!roomReady && mtMemberList().some(p => p.playerUid === target.uid && p.clientInstanceId === target.instanceId && p.online === true);
}

// ---------- 传输 ----------
function mtWaitRemote(requestId, timeout) {
    const promise = new Promise((resolve, reject) => {
        const timer = setTimeout(() => { if (mt.pending.delete(requestId)) reject(Object.assign(new Error('等待执行端回复超时，结果未确认'), { unconfirmed:true })); }, timeout);
        mt.pending.set(requestId, { resolve, reject, timer });
    });
    promise.catch(() => {});
    return promise;
}
function mtEvent(env) {
    if (env?.name !== 'mistletoe.result') return;
    const x = env.payload || {}, p = mt.pending.get(x.requestId);
    if (!p) return;
    mt.pending.delete(x.requestId); clearTimeout(p.timer);
    if (!x.reply || typeof x.reply !== 'object') p.reject(Object.assign(new Error('执行端回复无效，结果未确认'), { unconfirmed:true }));
    else p.resolve(x.reply);
}
function mtCancelPending(message) {
    mt.generation++;
    for (const p of mt.pending.values()) { clearTimeout(p.timer); p.reject(Object.assign(new Error(message), { unconfirmed:true })); }
    mt.pending.clear();
}

/** 发一条请求，返回执行端回复 {status,message,data,revision,operationId}。传输失败抛出带 unconfirmed 的错误。 */
async function mtSend(operation, payload, options = {}) {
    const target = options.target || mt.target;
    if (!target) throw new Error('请先选择执行端');
    if (!mtTargetReady(target)) throw new Error(target.local ? '本机连接未就绪，请从助手重新打开' : '执行端不在线或房间连接未就绪');
    const request = { operation, targetInstanceId:target.instanceId };
    if (payload !== undefined) request.payload = payload;
    if (options.expectedRevision) request.expectedRevision = options.expectedRevision;
    if (options.extra) Object.assign(request, options.extra);
    const cred = mtCredentialFor(target);
    if (!target.local && cred) { request.browserId = cred.browserId; request.browserSecret = cred.secret; }
    const requestId = options.requestId || newRequestId();
    request.requestId = requestId;
    const timeout = options.timeout || 25000;
    let reply;
    if (target.local) {
        // 只读请求可被本机桥按身份复用结果；写入请求每次独立认领，防止同编号被执行两次。
        // 本机桥在进入执行前拒绝（会话失效、来源无效、请求为空）时是确定未执行；
        // 超时、连接中断、实例变化或桥返回 unknown 都可能已经执行，一律按结果未确认处理。
        try { reply = await window.bgiLocalBridge.execute(MT_READ_OPS.has(operation) ? 'mistletoe.read' : 'mistletoe.request', { request }, timeout, 'local'); }
        catch (error) {
            if (error?.localStatus === 'not_sent' || error?.localStatus === 'error') throw new Error(error.message || '本机连接拒绝了请求，未执行');
            throw Object.assign(new Error((error?.message || '本机请求中断') + (/未确认/.test(error?.message || '') ? '' : '，结果未确认')), { unconfirmed:true });
        }
    } else {
        const wait = mtWaitRemote(requestId, timeout);
        try {
            const response = await connection.invoke('Dispatch', makeEnvelope('command', 'mistletoe.request',
                { roomCode, targetUid:target.uid, targetInstanceId:target.instanceId, requestId, request }));
            checkGatewayError(response);
        } catch (error) {
            const p = mt.pending.get(requestId); if (p) { clearTimeout(p.timer); mt.pending.delete(requestId); }
            if (error.gatewayRejected) throw error; // 服务端明确拒收：确定未送达
            throw Object.assign(new Error('请求发送中断，结果未确认：' + error.message), { unconfirmed:true });
        }
        reply = await wait;
    }
    if (!reply || typeof reply !== 'object' || typeof reply.status !== 'string') throw Object.assign(new Error('执行端回复格式无效，结果未确认'), { unconfirmed:true });
    if (reply.targetInstanceId && reply.targetInstanceId !== target.instanceId) throw Object.assign(new Error('执行端实例已变化，回复未采用'), { unconfirmed:true });
    if (reply.status === 'denied') {
        // 执行端已撤销或不认这个浏览器：凭据作废，当前页面回到授权卡。
        if (!target.local) mtSaveCredential(target, null);
        if (mt.target?.key === target.key) {
            mt.identity = { ...(mt.identity || {}), owner:false };
            setTimeout(() => { if (mt.active && mt.target?.key === target.key) mtRenderBody(); }, 0);
        }
    }
    return reply;
}

/** 读取类请求：失败直接抛错，调用方显示原因。 */
async function mtRead(operation, payload, options) {
    const reply = await mtSend(operation, payload, options);
    if (reply.status !== 'ok') throw new Error(reply.message || '执行端未返回数据');
    return reply;
}

/**
 * 执行类操作：区分已发送 → 已受理 → 终态；结果未知时登记台账，核对前不允许再次发起，也从不自动重发。
 * onStage(stage, message) 用于界面逐段显示。返回最终回复（status 为 ok / rejected / denied / unknown / accepted / blocked）。
 * options.allowWhilePending：停止类操作，用户的停止权不受台账阻挡（与 PC 一致），本身也不登记台账。
 * options.proceedAfterCheck：带修订的保存在上次操作核对结案后可以直接继续（修订会挡住重复写入）。
 */
async function mtOperate(operation, payload, options = {}) {
    const target = mt.target;
    if (!target) return { status:'rejected', message:'请先选择执行端' };
    const stage = options.onStage || (() => {});
    const label = options.label || operation;
    const track = !options.allowWhilePending && mtDurable(operation, payload);
    if (track && mt.unconfirmed && mt.unconfirmed.instanceId === target.instanceId) {
        const checked = await mtReconcile(true);
        let blocked = '';
        if (mt.unconfirmed) blocked = `上次「${mt.unconfirmed.label}」${mt.unconfirmed.accepted ? '仍在执行端处理' : '结果仍未确认'}；「${label}」没有发送` + (mt.unconfirmed.accepted ? '，等它结束后再操作' : '，请先核对上次操作');
        else if (checked && !options.proceedAfterCheck) blocked = `上次「${checked.label}」已核对：${checked.message}；「${label}」没有发送，请确认后再点`;
        if (blocked) { stage('blocked', blocked); mtRenderPendingSoon(); return { status:'blocked', message:blocked }; }
    }
    const requestId = newRequestId();
    const durableId = 'web-' + requestId;
    // 修订在核对上次操作之后再取：上次保存经核对已生效时使用执行端的新修订。
    const expectedRevision = typeof options.expectedRevision === 'function' ? options.expectedRevision() : options.expectedRevision;
    if (track) mtRemember({ id:durableId, label, operation, scope:options.scope || '', instanceId:target.instanceId, uid:target.uid, local:target.local, at:Date.now() });
    stage('sent', `已发送「${label}」，等待执行端受理…`);
    let reply;
    try { reply = await mtSend(operation, payload, { ...options, expectedRevision, requestId }); }
    catch (error) {
        if (!error.unconfirmed) { if (track) mtRemember(null); stage('failed', `「${label}」没有送达：${error.message}`); return { status:'rejected', message:error.message }; }
        stage('unknown', `「${label}」${error.message}；不会自动重发` + (track ? '，可点「核对上次操作」' : '，请以执行端状态为准'));
        mtRenderPendingSoon();
        return { status:'unknown', message:error.message };
    }
    if (reply.status === 'accepted' && !reply.operationId) {
        // 停止请求：执行端只确认已受理，生效与否以下一次状态刷新为准。
        if (track) mtRemember(null);
        stage('accepted', reply.message || ('执行端已受理「' + label + '」；请以运行状态为准'));
        return reply;
    }
    if (reply.status === 'accepted') {
        if (track && reply.operationId !== durableId) { stage('unknown', `「${label}」的受理身份与本次请求不一致，结果未确认；不会重发`); mtRenderPendingSoon(); return { status:'unknown', message:'执行端受理身份不匹配' }; }
        if (track && mt.unconfirmed?.id === durableId) mtRemember({ ...mt.unconfirmed, accepted:true });
        stage('accepted', `执行端已受理「${label}」，正在执行…`);
        const budget = options.waitBudget || 15000, started = Date.now();
        while (Date.now() - started < budget) {
            await new Promise(r => setTimeout(r, 700));
            let state;
            try { state = await mtSend('operation.get', reply.operationId, { target }); }
            catch (error) { stage('unknown', `「${label}」已受理，但查询终态失败：${error.message}；不会重发`); mtRenderPendingSoon(); return { status:'unknown', message:error.message, accepted:true }; }
            if (state.operationId && state.operationId !== reply.operationId) { stage('unknown', '执行端回执身份不匹配，结果未确认'); mtRenderPendingSoon(); return { status:'unknown', message:'回执身份不匹配' }; }
            // 「找不到这次操作」没有回执身份：不能当作未执行，按结果未确认处理。
            if (!state.operationId && state.status !== 'accepted') { stage('unknown', `「${label}」已受理，但执行端没有给出这次操作的终态：${state.message || ''}；不会重发`); mtRenderPendingSoon(); return { status:'unknown', message:state.message || '终态不可确认', accepted:true }; }
            if (state.status !== 'accepted') { reply = state; break; }
        }
        if (reply.status === 'accepted') {
            stage('running', `「${label}」仍在执行端处理中；终态确认后才会显示结果，不会重发`);
            mtRenderPendingSoon();
            return { status:'accepted', message:'执行端仍在处理', accepted:true };
        }
    }
    // 与 PC 一致：只有终态回执属于本次请求（或执行端直接给出无回执身份的结论）才解除台账。
    if (track && (reply.status === 'ok' || reply.status === 'rejected' || reply.status === 'denied') && (!reply.operationId || reply.operationId === durableId)) mtRemember(null);
    if (reply.status === 'unknown') mtRenderPendingSoon();
    stage(reply.status === 'ok' ? 'done' : reply.status === 'unknown' ? 'unknown' : 'failed', reply.message || (reply.status === 'ok' ? `「${label}」已完成` : `「${label}」未完成`));
    return reply;
}
/** 台账变化后刷新顶部的「结果未确认」条（外壳脚本加载后才有渲染函数）。 */
function mtRenderPendingSoon() { setTimeout(() => { if (typeof mtRenderPending === 'function') mtRenderPending(); }, 0); }
/** 核对台账里的未确认操作；只查询，不重发。返回已结案的条目或 null。 */
async function mtReconcile(silent) {
    const entry = mt.unconfirmed;
    if (!entry) return null;
    const target = mtTargets().find(t => t.instanceId === entry.instanceId);
    if (!target || !mtTargetReady(target)) { if (!silent) mtToast('原执行端不在线，暂时无法核对上次操作', true); return null; }
    try {
        const state = await mtSend('operation.get', entry.id, { target });
        // 只认带着原操作身份的终态；「执行端找不到这次操作」不能证明没有执行，保持阻断，交给人工核对。
        if ((state.status === 'ok' || state.status === 'rejected') && state.operationId === entry.id) {
            mtRemember(null);
            if (typeof mtOnReconciled === 'function') mtOnReconciled(entry, state);
            return { ...entry, message:state.message || state.status };
        }
        // 执行端重启后只能确认「受理过但无终态」：此时允许人工核对后解除。
        mtRemember({ ...entry, accepted:state.status === 'accepted' });
        if (!silent) mtToast(state.status === 'accepted' ? `执行端仍在处理「${entry.label}」，不能解除，也没有重发` : `执行端无法确认「${entry.label}」的终态：${state.message || ''}`, true);
    } catch (error) { if (!silent) mtToast('核对失败，台账保持：' + error.message, true); }
    return null;
}
function mtAcknowledgeUnknown() {
    const entry = mt.unconfirmed; if (!entry) return;
    if (!confirm(`执行端无法确认「${entry.label}」的终态。只有你已核对执行端实际状态、并接受旧结果仍为未知时才解除。解除不会把旧操作判成功或失败，也不会重发。是否继续？`)) return;
    mtRemember(null); mtToast('已按人工核对解除阻断；原操作结果仍未知，本次没有重发。'); mtRender();
    if (typeof mtResumeDrafts === 'function') setTimeout(mtResumeDrafts, 0);
}

// ---------- 授权 ----------
async function mtAuthorize(code) {
    const target = mt.target;
    const label = (navigator.userAgent.includes('Mobile') ? '手机' : '电脑') + '网页 · ' + new Date().toLocaleDateString();
    const reply = await mtSend('web.authorize', undefined, { extra:{ code, label } });
    if (reply.status !== 'ok' || !reply.data?.browserId || typeof reply.data.secret !== 'string') throw new Error(reply.message || '授权失败');
    mtSaveCredential(target, { browserId:reply.data.browserId, secret:reply.data.secret, uid:target.uid });
    await mtLoadIdentity();
}
async function mtLoadIdentity() {
    // 同一时刻只核对一次身份；成员列表刷新、轮询和切页可能同时触发。
    if (mt.identityLoading) return mt.identityLoading;
    const key = mt.target?.key;
    mt.identityLoading = (async () => {
        let identity = null, error = '';
        try { identity = (await mtRead('web.identity')).data || null; }
        catch (e) { error = e.message; }
        if (mt.target?.key !== key) return;
        mt.identity = identity; mt.identityError = error;
    })();
    try { await mt.identityLoading; } finally { mt.identityLoading = null; }
}

// ---------- 状态行与提示 ----------
/** fromHead：执行端选择/连接状态类提示；目标就绪后由 mtRenderHead 自动清掉，避免旧提示残留。 */
function mtToast(message, isError, fromHead) {
    const el = mtEl('mtStatus'); if (!el) return;
    el.textContent = message; el.classList.toggle('is-error', !!isError);
    mt.headHint = !!fromHead;
}
function mtStageBar(stage, message) {
    const el = mtEl('mtOpStage'); if (!el) return;
    const names = { sent:'已发送', accepted:'已受理', running:'执行中', done:'已完成', failed:'未执行', unknown:'结果未确认', blocked:'未发送' };
    el.hidden = false; el.dataset.stage = stage;
    el.innerHTML = `<span class="mt-stage-pill">${names[stage] || stage}</span><span>${mte(message)}</span>`;
}

// ---------- 产品切换 ----------
function mtShow(active) {
    mt.active = active;
    if (!active) { clearInterval(mt.pollTimer); mt.pollTimer = null; return; }
    mtRender();
}
function mtMembersChanged(update) {
    mtApplyMembers(update);
    if (!mt.active) return;
    // 目标暂时从成员列表消失（离线、重连）时保留选择和草稿，界面显示离线；同一实例回来后自动恢复。
    mtRenderHead();
    mtReadinessChanged();
}
function mtConnection(online) {
    // 只有服务器转发的请求在 mt.pending 里；本机直连请求不受房间断线影响。
    if (!online && mt.pending.size) { mtCancelPending('房间连接已断开，结果未确认'); if (mt.active) mtToast('房间连接已断开；进行中的请求结果未确认，不会自动重发。', true); }
    if (!mt.active) return;
    mtRenderHead();
    mtReadinessChanged();
}
/** 所选执行端从可用变为不可用（或反过来）时重绘内容区；恢复后重新核对身份并补存草稿。 */
function mtReadinessChanged() {
    const ready = !!mt.target && mtTargetReady();
    if (ready === mt.lastReady) return;
    const wasReady = mt.lastReady;
    mt.lastReady = ready;
    if (wasReady === null) return;
    if (ready) { mt.identity = null; mtRenderBody().then(mtResumeDrafts); }
    else mtRenderBody();
}

// ---------- 通用 ----------
/** 执行端用两种命名序列化（带 JsonPropertyName 的是 camelCase，记录/匿名对象是 PascalCase），统一读取。 */
function mtPick(o, name) {
    if (!o || typeof o !== 'object') return undefined;
    if (name in o) return o[name];
    const pascal = name[0].toUpperCase() + name.slice(1);
    return o[pascal];
}
function mtNewId() { return (crypto.randomUUID ? crypto.randomUUID() : Date.now().toString(16) + Math.random().toString(16).slice(2)).replaceAll('-', '').slice(0, 32); }
function mtClone(value) { return JSON.parse(JSON.stringify(value)); }
function mtDownload(text, name) { const url = URL.createObjectURL(new Blob([text], { type:'application/json' })); const a = document.createElement('a'); a.href = url; a.download = name; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000); }
function mtReadFile(file, limit = 2000000) {
    return new Promise((resolve, reject) => {
        if (!file) return reject(new Error('没有选择文件'));
        if (file.size > limit) return reject(new Error('文件超过 ' + Math.round(limit / 1000000) + ' MB 上限'));
        const reader = new FileReader(); reader.onload = () => resolve(String(reader.result)); reader.onerror = () => reject(new Error('文件读取失败')); reader.readAsText(file, 'utf-8');
    });
}
function mtPickFile(accept = '.json,application/json') {
    return new Promise(resolve => { const input = document.createElement('input'); input.type = 'file'; input.accept = accept; input.onchange = () => resolve(input.files[0] || null); input.click(); });
}
