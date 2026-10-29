// 嘟嘟可「远程日志」：对应 PC RemoteMemberLogsWindow 的独立面板。
// 日志来源是本房间（复用主连接）或「协助房间」（独立旁观连接，只读日志；不注册执行端、不接收命令，
// 本房间连接、耕地机和槲寄生继续使用原房间）。请求单独登记，切换顶部成员不会打断面板里的下载。
const rl = { built:false, src:'own', assist:null, connecting:null, pending:new Map(), files:[], fileDate:undefined, selected:-1, member:'', listUid:'', busy:false, version:0 };
const RL_HISTORY_KEY = 'bgi.dodoco.assistRooms.v1';
const RL_TODAY = () => roomLogLocalDate(Date.now());

function rlSource() { return rl.src === 'assist' ? rl.assist : 'own'; }
function rlSourceReady(source) { return source === 'own' ? !!roomReady && !!connection : !!source && source === rl.assist && source.ready === true; }
function rlPlayers(source = rlSource()) {
    const list = source === 'own' ? players : (source?.players || []);
    return [...new Map((Array.isArray(list) ? list : []).filter(p => p && p.playerUid && !String(p.playerUid).startsWith('web_')).map(p => [p.playerUid, p])).values()]
        .sort((a, b) => String(a.playerName || a.playerUid).localeCompare(String(b.playerName || b.playerUid), 'zh-CN'));
}
function rlRoomCode(source = rlSource()) { return source === 'own' ? roomCode : source?.room || ''; }
function rlLabel() {
    if (rl.connecting) return '正在连接协助房间…';
    if (rl.src === 'assist') return `协助房间：${rl.assist?.room || ''}${rl.assist?.ready ? '' : '（未连接）'}`;
    return roomReady ? `本房间${roomCode ? '：' + roomCode : ''}` : '本房间（未连接）';
}

// ---------- 请求：按来源连接发送，应答只认同一来源、同一成员、同一 requestId ----------
async function rlSend(source, name, payload) {
    if (!rlSourceReady(source)) throw new Error(source === 'own' ? '本房间连接未就绪。' : '协助房间未连接。');
    const conn = source === 'own' ? connection : source.conn;
    try { checkGatewayError(await conn.invoke('Dispatch', makeEnvelope('command', name, { roomCode: rlRoomCode(source), ...payload }))); }
    catch (e) { if (!rlSourceReady(source)) throw new Error('连接已断开，结果未确认。'); throw e; }
}
function rlWait(kind, uid, requestId, source, timeout, fileName) {
    const promise = new Promise((resolve, reject) => {
        const p = { kind, uid, requestId, source, fileName, resolve, reject, chunks: [], total: 0, bytes: 0, timer: null };
        p.expire = () => { clearTimeout(p.timer); p.timer = setTimeout(() => { if (rl.pending.delete(requestId)) reject(new Error('等待成员响应超时；可能离线、未开启共享或版本不支持。')); }, timeout); };
        p.expire(); rl.pending.set(requestId, p);
    });
    promise.catch(() => {});
    return promise;
}
function rlFinish(p, error, value) {
    if (!rl.pending.delete(p.requestId)) return;
    clearTimeout(p.timer); if (error) p.reject(new Error(error)); else p.resolve(value);
}
function rlCancel(message, source) { for (const p of [...rl.pending.values()]) if (!source || p.source === source) rlFinish(p, message); }
function rlEvent(env, source) {
    const x = env?.payload || {}, p = rl.pending.get(x.requestId);
    if (!p || p.source !== source || x.uid !== p.uid) return;
    if (env.name === 'log.fileList' && p.kind === 'files') {
        if (!Array.isArray(x.files)) return rlFinish(p, '成员返回的文件列表无效。');
        rlFinish(p, null, x.files.slice(0, 200));
    } else if (env.name === 'log.fileChunk' && p.kind === 'download') {
        if (x.fileName !== p.fileName) return;
        if (x.done && x.totalChunks === 0) return rlFinish(p, '成员正忙、拒绝共享或文件超限，无法确定具体原因。');
        if (!Number.isInteger(x.seq) || !Number.isInteger(x.totalChunks) || x.totalChunks < 1 || x.totalChunks > 400 || x.seq !== p.chunks.length || (p.total && p.total !== x.totalChunks) || typeof x.chunkBase64 !== 'string' || x.chunkBase64.length > 256 * 1024) return rlFinish(p, '日志分块缺失或格式无效。');
        p.total = x.totalChunks; p.bytes += x.chunkBase64.length;
        if (p.bytes > 100 * 1024 * 1024) return rlFinish(p, '压缩日志超过网页处理上限。');
        p.chunks.push(x.chunkBase64); p.expire();
        if (typeof p.progress === 'function') p.progress(p.chunks.length / p.total);
        if (x.done) { if (p.chunks.length !== p.total) return rlFinish(p, '日志分块不完整。'); rlFinish(p, null, p.chunks); }
    }
}
async function rlRequestFiles(source, uid) {
    const id = newRequestId(), wait = rlWait('files', uid, id, source, 20000);
    try { await rlSend(source, 'log.requestFiles', { targetUid: uid, requestId: id }); return await wait; }
    catch (e) { const p = rl.pending.get(id); if (p) rlFinish(p, e.message); throw e; }
}
async function rlRequestDownload(source, uid, fileName, progress) {
    const id = newRequestId(), wait = rlWait('download', uid, id, source, 30000, fileName);
    rl.pending.get(id).progress = progress;
    try { await rlSend(source, 'log.requestDownload', { targetUid: uid, requestId: id, fileName }); return await roomLogGunzip(await wait); }
    catch (e) { const p = rl.pending.get(id); if (p) rlFinish(p, e.message); throw e; }
}

// ---------- 本房间连接变化（control-room.js 调用） ----------
function rlConnection(online) {
    if (!online) rlCancel('本房间连接已断开，结果未确认。', 'own');
    rlRender();
}
function rlMembersChanged() { if (rl.src === 'own') rlRender(); }

// ---------- 协助房间：独立旁观连接 ----------
function rlAssistPlayers(assist, update) {
    if (!update || typeof update !== 'object') return;
    if (update.full) { assist.players = Array.isArray(update.players) ? update.players : []; assist.ready = true; assist.error = ''; }
    else {
        if (!assist.ready) return;
        const byUid = new Map(assist.players.map(p => [p.playerUid, p]));
        for (const p of update.changed || []) if (p && p.playerUid) byUid.set(p.playerUid, p);
        for (const uid of update.removed || []) byUid.delete(uid);
        assist.players = [...byUid.values()];
    }
    if (rl.assist === assist) rlRender();
}
async function rlCloseAssist(assist) {
    if (!assist || assist.closed) return;
    assist.closed = true; assist.ready = false;
    rlCancel('日志房间已切换，原请求结果未确认。', assist);
    try { await assist.conn?.stop(); } catch { /* 释放失败不影响本房间 */ }
}
async function rlConnectAssist(room, pass) {
    if (rl.connecting) return false;
    room = String(room || '').trim();
    if (!room || !pass) { rlStatus('请输入房间号和密码。'); return false; }
    let address = '/gateway';
    if (window.bgiLocalBridge) { try { address = window.bgiLocalBridge.gatewayUrl(); } catch (e) { rlStatus(e.message); return false; } }
    const assist = { room, conn: null, players: [], ready: false, error: '', closed: false, uid: 'web_logviewer_' + newRequestId() };
    const builder = new signalR.HubConnectionBuilder();
    assist.conn = (window.bgiLocalBridge ? builder.withUrl(address, { withCredentials: false }) : builder.withUrl(address))
        .withAutomaticReconnect([0, 2000, 10000, 30000]).build();
    let settle; const first = new Promise(resolve => settle = resolve);
    assist.conn.on('evt', env => {
        if (!env || typeof env !== 'object' || assist.closed) return;
        if (env.name === 'control.playersUpdated') { rlAssistPlayers(assist, env.payload); if (env.payload?.full) settle(null); }
        else if (env.name === 'control.joinRejected') {
            assist.ready = false; assist.error = '连接被拒绝：' + (env.payload?.reason || '加入被拒绝');
            settle(assist.error); if (rl.assist === assist) { rlStatus(assist.error); rlRender(); }
        } else rlEvent(env, assist);
    });
    // 与 PC 相同：独立 web_ 身份 + isRemote，不进入成员列表、不接收任务命令。
    const join = async () => {
        checkGatewayError(await assist.conn.invoke('Query', makeEnvelope('query', 'session.hello', { clientKind: 'web', clientVersion: WEB_CLIENT_VERSION, protocolVersion: PROTOCOL_VERSION, capabilities: [] })));
        checkGatewayError(await assist.conn.invoke('Dispatch', makeEnvelope('command', 'control.joinRoom', { roomCode: room, password: pass, playerUid: assist.uid, playerName: '日志协助', allowedUids: [], isRemote: true, clientInstanceId: '' })));
    };
    const dropped = message => {
        assist.ready = false; assist.players = []; assist.error = message;
        rlCancel('协助房间连接已断开，结果未确认。', assist);
        if (rl.assist === assist) { rlStatus(message); rlRender(); }
    };
    assist.conn.onreconnecting(() => { if (!assist.closed) dropped('协助房间已断线，等待重连；也可返回本房间。'); });
    assist.conn.onreconnected(() => { if (!assist.closed) join().catch(e => dropped('重新加入协助房间失败：' + e.message)); });
    assist.conn.onclose(() => { if (!assist.closed) dropped('协助房间连接已关闭，请返回本房间或重新连接。'); });

    const token = { assist }; rl.connecting = token; rlStatus('正在连接协助房间…'); rlRender();
    let timer = null;
    try {
        await assist.conn.start(); await join();
        // join 的应答不代表认证成功；收到认证后的完整成员快照才切换面板。
        const rejection = await Promise.race([first, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('连接协助房间超时，请检查房间与网络。')), 15000); })]);
        if (rejection) throw new Error(rejection);
        if (!assist.ready) throw new Error(assist.error || '协助房间连接已断开。');
        if (rl.connecting !== token) { await rlCloseAssist(assist); return false; }
        const old = rl.assist;
        rlResetRequests('日志房间已切换，原请求结果未确认。');
        rl.assist = assist; rl.src = 'assist';
        rlStatus(rlPlayers(assist).some(p => p.online) ? '已连接协助房间，请选择成员获取日志列表。' : '已连接协助房间，当前没有在线成员。');
        if (old) rlCloseAssist(old);
        return true;
    } catch (e) {
        await rlCloseAssist(assist);
        if (rl.connecting === token) rlStatus(/^连接/.test(e.message) ? e.message : '连接协助房间失败：' + e.message);
        return false;
    } finally {
        clearTimeout(timer);
        if (rl.connecting === token) rl.connecting = null;
        rlRender();
    }
}
function rlReturnToOwn() {
    rl.connecting = null;
    const old = rl.assist;
    rlResetRequests('已返回本房间，原请求结果未确认。');
    rl.assist = null; rl.src = 'own';
    if (old) rlCloseAssist(old);
    rlStatus('已返回本房间，请选择成员获取日志列表。');
    rlRender();
}
function rlResetRequests(message) {
    if (roomLogs.running) roomLogs.cancel = true;
    rl.version++; rlCancel(message);
    rl.files = []; rl.fileDate = undefined; rl.selected = -1; rl.listUid = ''; rl.member = ''; rl.busy = false;
    rlProgress(undefined);
    if (rl.built) rlDrawFiles();
}

// ---------- 协助房间记录：按服务器地址分开，保存在此浏览器 ----------
function rlHistoryScope() { try { return window.bgiLocalBridge ? window.bgiLocalBridge.gatewayUrl() : location.origin; } catch { return location.origin; } }
function rlHistoryAll() { const all = JSON.parse(localStorage.getItem(RL_HISTORY_KEY) || '[]'); if (!Array.isArray(all)) throw new Error('房间记录格式无效'); return all; }
function rlHistory() {
    try { const scope = rlHistoryScope(); return rlHistoryAll().filter(r => r && r.scope === scope && typeof r.room === 'string').sort((a, b) => (b.lastUsed || 0) - (a.lastUsed || 0)); }
    catch { return []; }
}
function rlRemember(room, pass) {
    try {
        const scope = rlHistoryScope(), all = rlHistoryAll().filter(r => !(r && r.scope === scope && r.room === room)); // 损坏的记录不覆盖
        all.push({ scope, room, password: pass, lastUsed: Date.now() });
        localStorage.setItem(RL_HISTORY_KEY, JSON.stringify(all)); return '';
    } catch (e) { return '；房间记录保存失败：' + e.message; }
}
function rlForget(room) {
    try { const scope = rlHistoryScope(); localStorage.setItem(RL_HISTORY_KEY, JSON.stringify(rlHistoryAll().filter(r => !(r && r.scope === scope && r.room === room)))); return true; }
    catch (e) { rlAssistStatus('删除记录失败：' + e.message); return false; }
}

// ---------- 面板 ----------
// 日志浏览左栏的入口（dodoco-web.js 渲染时调用）；本机直连页同样可用，经服务器或协助房间读取。
function roomLogMount(host) {
    if (!host) return;
    const box = document.createElement('section');
    box.className = 'dd-room-logs';
    box.innerHTML = `<div class="dd-room-logs-head"><div><h3>远程日志</h3><p id="rlEntryHint" class="dodoco-hint"></p></div><button id="rlEntry" class="dd-btn" title="打开远程成员日志窗口，选择成员、获取列表并下载日志">远程日志</button></div>`;
    host.appendChild(box);
    d('rlEntry').onclick = rlOpenDialog;
    rlEntryHint();
}
function rlEntryHint() {
    const el = d('rlEntryHint'); if (!el) return;
    el.textContent = roomLogs.running ? '全房间日志收集中…' : rl.src === 'assist' ? `${rlLabel()} · 可返回本房间` : '选择成员获取并下载日志、下载全房间当天日志，也可协助其他房间';
}
function rlBuild() {
    if (rl.built) return;
    rl.built = true;
    const dlg = document.createElement('dialog');
    dlg.id = 'rlDialog'; dlg.className = 'dd-rl-dialog'; dlg.setAttribute('aria-labelledby', 'rlTitle');
    dlg.innerHTML = `<header class="dd-rl-head"><div class="dd-rl-title"><h2 id="rlTitle">远程成员日志</h2><span id="rlRoomLabel" class="dd-rl-room"></span></div><div class="dd-actions"><button id="rlAssistToggle" class="dd-btn dd-btn-sm" title="输入房间号和密码，仅切换日志面板">协助房间</button><button id="rlReturn" class="dd-btn dd-btn-sm" hidden>返回本房间</button><button id="rlClose" class="dd-btn dd-btn-sm dd-btn-quiet">关闭</button></div></header>
<form id="rlAssistForm" class="dd-rl-assist" hidden><h3>查看其他房间成员日志</h3><p class="dodoco-hint">仅切换此日志面板；本房间的连接、耕地机和槲寄生继续使用原房间。</p><div id="rlHistoryRow" class="dd-rl-history" hidden><label class="dd-field dd-grow"><span>已记录房间</span><select id="rlHistory"></select></label><button type="button" id="rlForget" class="dd-btn dd-btn-sm dd-btn-quiet">删除此记录</button></div><div class="dd-rl-cred"><label class="dd-field"><span>被协助房间号</span><input id="rlRoom" autocomplete="off"></label><label class="dd-field"><span>房间密码</span><input id="rlPassword" type="password" autocomplete="off"></label></div><p class="dodoco-hint">成功连接后记录在此浏览器（含密码），可随时删除。</p><p id="rlAssistStatus" class="dd-rl-assist-status" role="status"></p><div class="dd-actions dd-rl-assist-actions"><button type="button" id="rlAssistCancel" class="dd-btn dd-btn-quiet">取消</button><button type="submit" id="rlConnect" class="dd-btn dd-btn-primary">连接</button></div></form>
<div class="dd-actions dd-rl-batch"><button id="roomLogStart" class="dd-btn dd-btn-sm" title="收集当前日志房间全部成员当天的 BGI 和助手日志，打包为一个 ZIP；共享关闭或离线会在结果中说明">下载全房间当天日志</button><button id="roomLogCancel" class="dd-btn dd-btn-sm dd-btn-quiet" hidden>取消批量下载</button></div>
<progress id="roomLogProgress" class="dd-rl-bar" max="1" value="0" hidden></progress>
<div class="dd-rl-filters"><label class="dd-field dd-grow"><span>成员</span><select id="rlMember" title="选择在线执行端"></select></label><label class="dd-field"><span>日期</span><select id="rlDate" title="按文件最后修改日期筛选"></select></label><button id="rlFetch" class="dd-btn" title="向选中成员请求日志文件列表（需对方开启「共享日志文件」）">获取列表</button></div>
<div id="rlFiles" class="dodoco-file-list dd-rl-files" aria-label="远程日志文件"></div>
<div class="dd-rl-messages"><p id="rlStatus" class="dodoco-status" role="status"></p><p id="roomLogStatus" class="dd-room-logs-status" hidden></p></div>
<footer class="dd-rl-foot"><div class="dd-actions"><button id="rlDownload" class="dd-btn dd-btn-primary" title="保存到浏览器下载目录，并在日志浏览中打开">下载</button><button id="rlOpen" class="dd-btn" title="不保存文件，直接在日志浏览中查看">在日志浏览中打开</button><span id="rlCount" class="dodoco-hint"></span></div><progress id="rlProgress" class="dd-rl-bar" max="1" value="0" hidden></progress></footer>`;
    document.body.appendChild(dlg);
    d('rlClose').onclick = () => dlg.close();
    d('rlAssistToggle').onclick = () => rlShowAssist(d('rlAssistForm').hidden);
    d('rlAssistCancel').onclick = () => rlShowAssist(false);
    d('rlReturn').onclick = rlReturnToOwn;
    d('rlHistory').onchange = rlFillHistory;
    d('rlRoom').oninput = () => { const sel = d('rlHistory'); if (sel.value && d('rlRoom').value.trim() !== sel.value) { sel.value = ''; d('rlPassword').value = ''; d('rlForget').disabled = true; } };
    d('rlForget').onclick = () => { const room = d('rlHistory').value; if (room && rlForget(room)) { rlLoadHistory(false); d('rlRoom').value = ''; d('rlPassword').value = ''; rlAssistStatus('已删除该房间的记录。'); } };
    d('rlAssistForm').onsubmit = async e => {
        e.preventDefault();
        const room = d('rlRoom').value.trim(), pass = d('rlPassword').value;
        rlAssistStatus('正在连接…');
        const ok = await rlConnectAssist(room, pass);
        d('rlPassword').value = '';
        if (ok) { const warn = rlRemember(room, pass); if (warn) rlStatus(d('rlStatus').textContent + warn); rlShowAssist(false); }
        else rlAssistStatus(d('rlStatus').textContent);
        rlRender();
    };
    d('rlMember').onchange = e => { rl.member = e.target.value; rlRender(); };
    d('rlDate').onchange = e => { rl.fileDate = e.target.value; rl.selected = -1; rlDrawFiles(); rlRender(); };
    d('rlFetch').onclick = rlFetch;
    d('rlDownload').onclick = () => rlTakeFile(true);
    d('rlOpen').onclick = () => rlTakeFile(false);
    d('roomLogStart').onclick = roomLogRun;
    d('roomLogCancel').onclick = () => { roomLogs.cancel = true; roomLogSetStatus('正在取消，当前文件结束后停止…'); };
}
function rlOpenDialog() {
    rlBuild();
    const dlg = d('rlDialog');
    if (!rl.statusText) rlStatus(rlSourceReady(rlSource()) ? '请选择成员获取日志列表。' : rl.src === 'own' ? '本房间未连接；可点「协助房间」连接其他房间查看成员日志。' : '协助房间未连接。');
    rlRender(); rlDrawFiles();
    if (roomLogs.lastStatus) roomLogSetStatus(roomLogs.lastStatus);
    if (!dlg.open) dlg.showModal();
}
function rlShowAssist(show) {
    const form = d('rlAssistForm'); if (!form) return;
    form.hidden = !show; d('rlAssistToggle').classList.toggle('active', show);
    if (show) { rlAssistStatus(''); rlLoadHistory(true); (d('rlPassword').value ? d('rlConnect') : d('rlRoom')).focus(); }
}
function rlAssistStatus(text) { const el = d('rlAssistStatus'); if (el) el.textContent = text; }
function rlLoadHistory(selectLatest) {
    const entries = rlHistory(), sel = d('rlHistory');
    sel.innerHTML = '<option value="">（不使用记录）</option>' + entries.map(r => `<option value="${de(r.room)}">${de(r.room)}</option>`).join('');
    d('rlHistoryRow').hidden = !entries.length;
    sel.value = selectLatest && entries.length ? entries[0].room : '';
    rlFillHistory();
}
function rlFillHistory() {
    const room = d('rlHistory').value, entry = rlHistory().find(r => r.room === room);
    d('rlForget').disabled = !entry;
    if (!entry) return;
    d('rlRoom').value = entry.room; d('rlPassword').value = typeof entry.password === 'string' ? entry.password : '';
    rlAssistStatus(entry.password ? '已填入记录，可直接连接或选择其他房间。' : '这条记录没有密码，请重新输入。');
}
function rlStatus(text) { rl.statusText = text; const el = d('rlStatus'); if (el) el.textContent = text; }
function rlProgress(value) { const bar = d('rlProgress'); if (!bar) return; bar.hidden = value === undefined; if (value !== undefined) bar.value = value; }
function rlCanSingle() { return rlSourceReady(rlSource()) && !rl.busy && !roomLogs.running && !rl.connecting; }
function rlRender() {
    rlEntryHint();
    if (!rl.built) return;
    const ready = rlSourceReady(rlSource()), list = rlPlayers(), online = list.filter(p => p.online);
    d('rlRoomLabel').textContent = rlLabel();
    d('rlReturn').hidden = !(rl.src === 'assist' || rl.connecting);
    d('rlConnect').disabled = !!rl.connecting;
    if (rl.member && !online.some(p => p.playerUid === rl.member)) rl.member = '';
    const sel = d('rlMember');
    sel.innerHTML = `<option value="">${ready ? (online.length ? '选择在线成员' : '没有在线成员') : '房间未连接'}</option>` + online.map(p => `<option value="${de(p.playerUid)}" ${p.playerUid === rl.member ? 'selected' : ''}>${de(p.playerName || p.playerUid)}</option>`).join('');
    sel.disabled = !rlCanSingle();
    d('rlFetch').disabled = !rlCanSingle() || !rl.member;
    const file = rlShownFiles()[rl.selected]?.[0];
    d('rlDownload').disabled = d('rlOpen').disabled = !rlCanSingle() || !file || rl.listUid !== rl.member && !!rl.member;
    roomLogSyncButtons();
}
function rlShownFiles() {
    return rl.files.map((f, i) => [f, i]).filter(([f]) => !rl.fileDate || roomLogLocalDate(f.lastWrite) === rl.fileDate);
}
function rlDrawFiles() {
    const host = d('rlFiles'), dateSel = d('rlDate'); if (!host) return;
    const today = RL_TODAY(), dates = [...new Set(rl.files.map(f => roomLogLocalDate(f.lastWrite)).filter(Boolean))].sort().reverse();
    if (rl.fileDate === undefined) rl.fileDate = rl.files.length && !dates.includes(today) ? '' : today;
    const options = [...new Set([today, ...dates])].sort().reverse();
    dateSel.innerHTML = '<option value="">全部日期</option>' + options.map(x => `<option value="${x}">${x}${x === today ? '（今天）' : ''}</option>`).join('');
    dateSel.value = rl.fileDate;
    const shown = rlShownFiles(), size = n => { n = Number(n) || 0; return n >= 1048576 ? (n / 1048576).toFixed(1) + ' MB' : n >= 1024 ? Math.round(n / 1024) + ' KB' : n + ' 字节'; };
    const when = v => { const t = new Date(v); return Number.isFinite(t.getTime()) ? t.toLocaleString('zh-CN', { hour12: false }) : String(v || ''); };
    host.innerHTML = shown.length ? shown.map(([f], k) => `<button class="dodoco-file${k === rl.selected ? ' active' : ''}" data-rl-file="${k}" aria-pressed="${k === rl.selected}"><span class="dd-file-name">${de(f.name)}</span><small>${de(when(f.lastWrite))} · ${size(f.size)}</small></button>`).join('')
        : `<p class="dd-empty">${rl.files.length ? (rl.fileDate ? `${rl.fileDate} 没有日志文件，可选择其他日期或全部日期` : '没有日志文件') : rl.listUid ? '成员返回空列表：可能未开启「共享日志文件」，也可能没有可共享的日志' : '选择成员后点「获取列表」'}</p>`;
    host.querySelectorAll('[data-rl-file]').forEach(b => { b.onclick = () => { rl.selected = Number(b.dataset.rlFile); rlDrawFiles(); rlRender(); }; b.ondblclick = () => rlTakeFile(false); });
    d('rlCount').textContent = rl.files.length ? `显示 ${shown.length} 个` : '';
}
async function rlFetch() {
    const source = rlSource(), uid = rl.member, version = rl.version;
    if (!uid || !rlCanSingle()) return;
    rl.busy = true; rl.files = []; rl.selected = -1; rl.listUid = ''; rl.fileDate = undefined; rlDrawFiles(); rlRender();
    rlStatus('正在等待成员返回日志列表…');
    try {
        const files = await rlRequestFiles(source, uid);
        if (version !== rl.version) return;
        rl.files = files; rl.listUid = uid; rl.fileDate = undefined;
        rlStatus(files.length ? `成员返回 ${files.length} 个日志文件。` : '成员返回空列表：可能未开启「共享日志文件」，也可能没有可共享的日志。');
    } catch (e) { if (version === rl.version) rlStatus('获取日志列表未完成：' + e.message); }
    finally { if (version === rl.version) { rl.busy = false; rlDrawFiles(); rlRender(); } }
}
async function rlTakeFile(save) {
    const file = rlShownFiles()[rl.selected]?.[0], source = rlSource(), uid = rl.listUid, version = rl.version;
    if (!file || !uid || !rlCanSingle()) return;
    const member = rlPlayers(source).find(p => p.playerUid === uid), who = member?.playerName || uid, where = rl.src === 'assist' ? `协助房间 ${rlRoomCode(source)}` : '本房间';
    rl.busy = true; rlRender(); rlProgress(0); rlStatus(`正在接收 ${who} 的 ${file.name}…`);
    try {
        const bytes = await rlRequestDownload(source, uid, file.name, value => { if (version === rl.version) rlProgress(value); });
        if (version !== rl.version) return;
        if (save) dodocoSave(bytes, file.name, 'text/plain');
        rlStatus(`${save ? '已保存' : '已读取'} ${file.name}（${who}），已在日志浏览中打开。`);
        rlShowInViewer(file.name, bytes, `${where} · ${who}`);
    } catch (e) { if (version === rl.version) rlStatus(`${save ? '下载' : '读取'}未完成：` + e.message); }
    finally { if (version === rl.version) { rl.busy = false; rlProgress(undefined); rlRender(); } }
}
function rlShowInViewer(name, bytes, from) {
    d('rlDialog')?.close();
    if (!dodoco.active) showProduct('dodoco');
    if (dodoco.tab !== 'files') { dodocoStopAuto(); dodoco.tab = 'files'; if (dodoco.target === '*') dodoco.target = ''; dodocoRender(); dodocoSyncSubscription(); }
    dodocoLoadBlob(name, { file: new Blob([bytes]) }, '远程日志：' + from);
}

// ---------- 全房间当天日志（按当前日志房间收集，ZIP 内附“下载结果.txt”） ----------
function roomLogSetStatus(text, progress) {
    roomLogs.lastStatus = text; if (progress !== undefined) roomLogs.lastProgress = progress;
    const box = d('roomLogStatus'); if (box) { box.hidden = false; box.textContent = text; }
    const bar = d('roomLogProgress'); if (bar && roomLogs.lastProgress !== undefined) { bar.hidden = !roomLogs.running && roomLogs.lastProgress >= 1; bar.value = roomLogs.lastProgress; }
}
function roomLogSyncButtons() {
    const start = d('roomLogStart'), stop = d('roomLogCancel');
    if (start) start.disabled = roomLogs.running || rl.busy || !!rl.connecting || !rlSourceReady(rlSource());
    if (stop) stop.hidden = !roomLogs.running;
    const bar = d('roomLogProgress'); if (bar && !roomLogs.running) bar.hidden = true;
}
async function roomLogRun() {
    if (roomLogs.running) return;
    const source = rlSource();
    if (!rlSourceReady(source)) { roomLogSetStatus('日志房间未连接，无法收集全房间日志。'); return; }
    if (rl.busy) { roomLogSetStatus('请等待当前列表请求或单文件下载结束，再收集全房间日志。'); return; }
    const members = rlPlayers(source);
    if (!members.length) { roomLogSetStatus('当前房间没有可识别的成员，无法收集日志。'); return; }
    const date = RL_TODAY(), generation = ++roomLogs.generation, entries = [], results = [], room = rlRoomCode(source) || '本房间';
    roomLogs.running = true; roomLogs.cancel = false; roomLogs.lastProgress = 0; rlRender();
    const stopped = () => roomLogs.cancel || generation !== roomLogs.generation || !rlSourceReady(source) || rlSource() !== source;
    const stopReason = () => !rlSourceReady(source) ? '日志房间连接已断开，下载中止' : rlSource() !== source ? '日志房间已切换，下载中止' : '已取消批量下载';
    roomLogSetStatus(`正在收集 ${date} 的全房间日志，共 ${members.length} 位成员…`, 0);
    try {
        for (let i = 0; i < members.length; i++) {
            const m = members[i], name = m.playerName || m.playerUid, r = { name, uid: m.playerUid, bgi: 0, assistant: 0, failed: 0, messages: [] }; results.push(r);
            if (stopped()) { r.messages.push(stopReason() + '，尚未开始该成员'); continue; }
            if (!m.online) { r.messages.push('成员离线，已跳过；未取得当天 BGI 和助手日志'); roomLogSetStatus(`${name}：离线，已跳过`, (i + 1) / members.length); continue; }
            roomLogSetStatus(`正在获取 ${name} 的日志列表…（${i + 1}/${members.length}）`, i / members.length);
            let files;
            try { files = await rlRequestFiles(source, m.playerUid); } catch (e) { r.messages.push('获取列表失败：' + e.message); continue; }
            if (!files.length) { r.messages.push('收到空列表：对方可能未开启共享或没有可分享日志，无法区分'); continue; }
            const picked = [...new Map(files.filter(f => roomLogLocalDate(f.lastWrite) === date && (roomLogBgi.test(f.name) || roomLogAssistant.test(f.name))).map(f => [String(f.name).toLowerCase(), f])).values()].sort((a, b) => a.name.localeCompare(b.name));
            if (!picked.length) { r.messages.push(`列表中没有修改日期为 ${date} 的 BGI 或助手日志`); continue; }
            const folder = `成员-${String(i + 1).padStart(2, '0')}-${roomLogSafe(name, 40)}-${roomLogSafe(m.playerUid, 48)}/`;
            for (let k = 0; k < picked.length; k++) {
                const f = picked[k];
                if (stopped()) { r.messages.push(f.name + '：' + stopReason()); break; }
                roomLogSetStatus(`正在下载 ${name}：${f.name}（${k + 1}/${picked.length}）`, (i + k / picked.length) / members.length);
                try { const data = await rlRequestDownload(source, m.playerUid, f.name); entries.push({ name: folder + f.name, data }); if (roomLogBgi.test(f.name)) r.bgi++; else r.assistant++; }
                catch (e) { r.failed++; r.messages.push(f.name + '：下载失败，' + e.message); }
                // 对端在最后一块确认后才释放单文件忙标记；间隔 200ms 与 PC 端一致。
                if (k < picked.length - 1) await new Promise(res => setTimeout(res, 200));
            }
            if (!r.bgi && !r.assistant) r.messages.push('未取得当天 BGI 和助手日志');
            else if (!r.bgi) r.messages.push('未取得当天 BGI 日志');
            else if (!r.assistant) r.messages.push('未取得当天助手日志');
            else if (!r.failed) r.messages.push('当天 BGI 和助手日志均已下载');
        }
    } finally {
        const cancelled = stopped(), total = results.reduce((n, r) => n + r.bgi + r.assistant, 0), complete = results.filter(r => r.bgi && r.assistant && !r.failed).length;
        for (const m of members.slice(results.length)) results.push({ name: m.playerName || m.playerUid, uid: m.playerUid, bgi: 0, assistant: 0, failed: 0, messages: [stopReason() + '，尚未开始该成员'] });
        const lines = [`房间：${room}${source === 'own' ? '' : '（协助房间）'}`, `日志日期：${date}（按成员上报的文件修改日期筛选）`, `结束时间：${new Date().toLocaleString('zh-CN', { hour12: false })}`, `状态：${cancelled ? stopReason() : '收集结束，请核对各成员结果'}`, `成员快照：${members.length} 人；BGI ${results.reduce((n, r) => n + r.bgi, 0)} 个，助手 ${results.reduce((n, r) => n + r.assistant, 0)} 个，文件失败 ${results.reduce((n, r) => n + r.failed, 0)} 个`, '空列表可能代表未开启共享或没有可分享日志，协议无法区分；未取得日志不等于无日志。', ''];
        for (const r of results) lines.push(`${r.name}（UID：${r.uid}）`, `  BGI：${r.bgi} 个；助手：${r.assistant} 个；文件失败：${r.failed} 个；${r.bgi && r.assistant && !r.failed ? '两类当天日志均已下载' : '未完整取得两类当天日志'}`, '  ' + r.messages.join('；'), '');
        entries.push({ name: '下载结果.txt', data: new TextEncoder().encode('\ufeff' + lines.join('\r\n')) });
        const zipName = `房间-${roomLogSafe(room, 40)}-${date}-全房间日志.zip`;
        if (generation === roomLogs.generation) dodocoSave(roomLogZip(entries), zipName, 'application/zip');
        roomLogs.running = false;
        roomLogSetStatus(`${cancelled ? stopReason() : '全房间日志收集结束'}：已下载 ${total} 个文件；BGI 和助手均收齐 ${complete}/${members.length} 人。已保存 ${zipName}（内含“下载结果.txt”）。\n` + results.map(r => `${r.name}：${r.messages.join('；')}`).join('\n'), cancelled ? undefined : 1);
        rlRender();
    }
}
