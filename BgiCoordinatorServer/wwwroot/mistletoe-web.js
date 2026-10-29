// 槲寄生网页外壳：分段切换（启动中心 / 任务中心 / 运行记录）+ 执行端选择 + 本人授权 + 未确认操作条。
// 只连本人的执行端：本机直连免授权；远程需在执行端助手「设置 → 网页授权」生成授权码绑定此浏览器。
function mtDialog(id) {
    let el = mtEl(id);
    if (!el) { el = document.createElement('dialog'); el.id = id; el.className = 'mt-dialog'; mtRoot.appendChild(el); }
    return el;
}
function mtTargetLabel(t) {
    const short = t.instanceId ? ' · ' + String(t.instanceId).slice(0, 6) : '';
    return `${t.name}${t.local ? ' · 本机直连' : ' · 服务器转发'}${t.local ? '' : short}${t.online ? '' : '（离线）'}`;
}
function mtRender() {
    if (!mtRoot || !mt.active) return;
    mtRoot.innerHTML = `<div class="dd-head mt-head"><nav class="dodoco-tabs" aria-label="槲寄生功能"><button data-mt-tab="startup">启动中心</button><button data-mt-tab="taskcenter">任务中心</button><button data-mt-tab="history">运行记录</button></nav>
        <div class="dd-head-target"><label for="mtTarget">执行端</label><select id="mtTarget"></select></div></div>
      <p id="mtStatus" class="dodoco-status dd-status" role="status" aria-live="polite"></p>
      <div id="mtOpStage" class="mt-opstage" hidden></div>
      <div id="mtPending"></div>
      <div id="mtContent" class="dd-body"></div>`;
    mtRoot.querySelectorAll('[data-mt-tab]').forEach(b => b.onclick = async () => {
        if (b.dataset.mtTab === mt.tab) return;
        if (mt.tab === 'taskcenter' && typeof tcFlush === 'function' && !(await tcFlush())) return;
        if (mt.tab === 'startup' && su.dirty) await mtSaveConfig();
        mt.tab = b.dataset.mtTab; mtRenderHead(); mtRenderBody();
    });
    mtEl('mtTarget').onchange = e => mtChooseTarget(e.target.value);
    mtRenderHead(); mtRenderBody();
    clearInterval(mt.pollTimer);
    mt.pollTimer = setInterval(mtPoll, 3000);
}
function mtRenderHead() {
    if (!mt.active || !mtEl('mtTarget')) return;
    mtRoot.querySelectorAll('[data-mt-tab]').forEach(b => b.classList.toggle('active', b.dataset.mtTab === mt.tab));
    const list = mtTargets();
    if (!mt.target && list.length) { const first = list.find(t => t.local && t.online) || list.find(t => mtCredentialFor(t) && t.online) || null; if (first) { mt.target = first; mtResetTarget(); } }
    // 所选执行端暂时离线或从成员列表消失时保留选择（草稿和未确认台账都跟着它），显示为离线。
    if (mt.target && !list.some(t => t.key === mt.target.key)) list.push({ ...mt.target, online:false });
    else if (mt.target) Object.assign(mt.target, list.find(t => t.key === mt.target.key));
    const sel = mtEl('mtTarget');
    const option = t => `<option value="${mte(t.key)}"${mt.target?.key === t.key ? ' selected' : ''}>${mte(mtTargetLabel(t))}</option>`;
    // 只有本机直连和已授权（凭据在此浏览器）的实例算「我的执行端」；选中但未授权的仍归在待绑定组。
    const mine = list.filter(t => t.mine), others = list.filter(t => !t.mine);
    sel.innerHTML = `<option value="">${list.length ? '选择我的执行端' : '没有可连接的助手'}</option>`
        + (mine.length ? `<optgroup label="我的执行端">${mine.map(option).join('')}</optgroup>` : '')
        + (others.length ? `<optgroup label="绑定新的执行端（需在该助手生成授权码）">${others.map(option).join('')}</optgroup>` : '');
    mtRenderPending();
    if (!mt.target) mtToast(list.length ? '请选择你自己的执行端助手。' : (window.bgiLocalBridge ? '本机连接未就绪，请从助手「设置 → 本机网页」重新打开。' : roomReady ? '房间里没有在线的助手。槲寄生只能连接你自己的助手。' : '连接未就绪，等待重新连接。'), !list.length, true);
    else if (!mtTargetReady()) mtToast(mt.target.local ? '本机连接已断开，请从助手重新打开；未改走服务器。' : roomReady ? '所选执行端当前离线；上线后自动恢复，不会补发离线期间的操作。' : '房间连接已断开，等待重新连接；不会自动重发请求。', true, true);
    else if (mt.headHint) mtToast('');
}
function mtChooseTarget(key) {
    const next = mtTargets().find(t => t.key === key) || null;
    if (next?.key === mt.target?.key) return;
    mtCancelPending('执行端已切换，原请求结果未确认');
    mt.target = next; mtResetTarget(); mtRenderHead(); mtRenderBody();
}
function mtResetTarget() {
    mt.identity = null; mt.identityError = ''; mt.lastReady = mt.target ? mtTargetReady() : null;
    Object.assign(su, { loaded:false, loading:false, config:null, dirty:false, saving:false, saveError:'', selected:null, states:new Map(), flows:[] });
    clearTimeout(su.saveTimer); mtViewReset();
    if (typeof tcReset === 'function') tcReset();
}
/** 执行端重新可用后补存离线期间留下的草稿；保存仍带原修订，执行端已被他处修改时按冲突处理、草稿保留。 */
function mtResumeDrafts() {
    if (!mt.target || !mtTargetReady() || !mtOwner()) return;
    if (su.dirty && !su.saving) { clearTimeout(su.saveTimer); su.saveTimer = setTimeout(mtSaveConfig, 600); }
    if (typeof tc !== 'undefined' && tc.dirty && !tc.saving && !tc.conflict) { clearTimeout(tc.saveTimer); tc.saveTimer = setTimeout(tcSave, 600); }
}
/**
 * 未确认的保存经核对已在执行端生效：采用执行端记录的新修订，草稿继续以它为基准保存，
 * 避免把自己的上一次保存误判成「他处修改」。只处理 ok，rejected 保持原修订。
 */
function mtOnReconciled(entry, state) {
    if (state.status === 'ok') {
        if (entry.operation === 'startup.save' && state.revision) su.revision = state.revision;
        else if (entry.operation === 'task.flow.save' && state.revision && typeof tc !== 'undefined' && entry.scope && entry.scope === tc.selected) tc.revision = state.revision;
        else if (entry.operation === 'startup.schemes.save' || entry.operation === 'startup.autoLaunch.save') setTimeout(mtRefreshStartupMeta, 0);
    }
    // 台账解除后，之前因阻断留下的草稿继续按当前修订保存。
    setTimeout(mtResumeDrafts, 0);
}
/** 只重读方案库和开机自启动（含修订），不动正在编辑的启动流程草稿。 */
async function mtRefreshStartupMeta() {
    if (!su.loaded || !mtTargetReady()) return;
    try {
        const d = (await mtRead('startup.get')).data || {};
        su.schemes = mtPick(d, 'schemes') || []; su.schemesRevision = mtPick(d, 'schemesRevision') || '';
        su.autoLaunch = { enabled:!!mtPick(d, 'autoLaunchOnBoot'), mode:mtPick(d, 'autoLaunchOnBootModeIndex') || 0, revision:mtPick(d, 'autoLaunchRevision') || '' };
        const fresh = !su.dirty && !su.saving;
        if (fresh) { su.config = mtPick(d, 'config') || su.config; su.config.steps ||= []; su.revision = mtPick(d, 'revision') || su.revision; }
        // 只更新方案栏和设置行；编辑区仅在没有草稿时刷新，保留正在输入的内容。
        if (mt.tab === 'startup' && mtEl('mtRunCard')) { mtRenderSchemes(); mtRenderRunSettings(); if (fresh) { mtRenderOutline(); mtRenderCanvas(); mtRenderInspector(); mtRenderSaveState(); } }
    } catch { /* 下一次「读取最新启动设置」再核对 */ }
}
async function mtRenderBody() {
    const host = mtEl('mtContent'); if (!host) return;
    const t = mt.target;
    if (!t) { host.innerHTML = `<section class="dd-card dd-empty">${window.bgiLocalBridge || roomReady ? '在右上角选择你自己的执行端助手。槲寄生只能连接本人的助手：同一台电脑从助手「本机网页」打开免授权；远程网页需要一次授权码。' : '连接未就绪，等待重新连接。'}</section>`; return; }
    if (!mtTargetReady(t)) {
        const draft = su.dirty || (typeof tc !== 'undefined' && tc.dirty) ? '<br>未保存的修改仍保留在这个页面，执行端恢复后会自动补存（带原修订核对冲突）。' : '';
        host.innerHTML = `<section class="dd-card dd-empty mt-offline"><span class="dd-pill warn">${t.local ? '本机连接已断开' : roomReady ? '执行端离线' : '房间连接已断开'}</span><p>${t.local ? '请从助手「设置 → 本机网页」重新打开本页；本机请求不会改走服务器。' : roomReady ? '执行端助手上线并连接房间后，这里会自动恢复。' : '正在等待重新连接房间；进行中的请求按结果未确认处理，不会自动重发。'}${draft}</p></section>`;
        return;
    }
    if (!mt.identity) {
        if (!host.querySelector('.mt-checking')) host.innerHTML = '<section class="dd-card dd-empty mt-checking">正在核对执行端身份…</section>';
        const key = t.key; await mtLoadIdentity();
        if (mt.target?.key !== key || !mtEl('mtContent')) return;
        if (!mt.identity) { host.innerHTML = `<section class="dd-card dd-empty">执行端没有回应：${mte(mt.identityError)}<br><button class="dd-btn" id="mtRetryIdentity">重试</button></section>`; mtEl('mtRetryIdentity').onclick = () => mtRenderBody(); return; }
        if (mt.identity.instanceId && mt.identity.instanceId !== t.instanceId) { mt.identity = null; host.innerHTML = '<section class="dd-card dd-empty">执行端实例已变化，请重新选择执行端。</section>'; return; }
        mtRenderHead();
    }
    if (!mtOwner()) return mtRenderAuthorize(host);
    if (mt.tab === 'startup') mtRenderStartup(host);
    else if (mt.tab === 'taskcenter') tcRender(host);
    else tcRenderHistory(host);
}
function mtRenderAuthorize(host) {
    // 替换「请选择执行端」之类的旧提示；首次绑定和授权被撤销都会走到这里。
    mtToast('此浏览器还没有这台执行端的本人授权；输入授权码后即可管理。');
    host.innerHTML = `<section class="dd-card mt-auth"><header class="dd-card-head"><div><h2>绑定我的执行端</h2><p class="dodoco-hint">槲寄生只能连接你本人的助手。房间密码大家都知道，所以远程修改本机流程前需要证明「这是我的电脑」。</p></div></header>
      <ol class="mt-steps"><li>在执行端助手打开「设置 → 网页授权」，点「生成授权码」。</li><li>把 8 位授权码填到下面（5 分钟内有效，只能用一次）。</li><li>授权后这个浏览器会记住凭据；可随时在助手里撤销。</li></ol>
      <div class="dd-filters"><label class="dd-field"><span>执行端</span><b class="mt-auth-target">${mte(mt.identity?.name || mt.target.name)} · ${mte(mt.target.uid)}</b></label>
      <label class="dd-field"><span>授权码</span><input id="mtAuthCode" inputmode="numeric" autocomplete="one-time-code" maxlength="9" placeholder="0000 0000"></label>
      <button id="mtAuthGo" class="dd-btn dd-btn-primary dd-align-end">授权此浏览器</button></div>
      <p class="dodoco-hint">同一台电脑上，从助手「设置 → 本机网页」打开即可免授权直连。</p></section>`;
    const go = async () => {
        const code = mtEl('mtAuthCode').value.replace(/\D/g, '');
        if (code.length !== 8) return mtToast('授权码是 8 位数字', true);
        mtEl('mtAuthGo').disabled = true; mtToast('正在核对授权码…');
        try { await mtAuthorize(code); mtToast('已授权此浏览器，可以管理这台执行端的槲寄生。'); mtRenderHead(); mtRenderBody(); }
        catch (error) { mtToast('授权未完成：' + error.message, true); if (mtEl('mtAuthGo')) mtEl('mtAuthGo').disabled = false; }
    };
    mtEl('mtAuthGo').onclick = go; mtEl('mtAuthCode').onkeydown = e => { if (e.key === 'Enter') go(); };
}
function mtRenderPending() {
    const host = mtEl('mtPending'); if (!host) return;
    const e = mt.unconfirmed;
    if (!e || e.instanceId !== mt.target?.instanceId) { host.innerHTML = ''; return; }
    host.innerHTML = `<div class="mt-pending" role="alert"><span class="dd-pill warn">结果未确认</span><span class="mt-pending-text">「${mte(e.label)}」发出后没有收到终态（${new Date(e.at).toLocaleTimeString()}）。核对前不会再次发起执行类操作，也不会自动重发；停止类操作不受影响。</span><div class="dd-actions"><button id="mtCheckPending" class="dd-btn dd-btn-sm">核对上次操作</button><button id="mtAckPending" class="dd-btn dd-btn-sm dd-btn-quiet">我已核对，解除</button></div></div>`;
    mtEl('mtCheckPending').onclick = async () => { const done = await mtReconcile(false); if (done) mtToast(`上次「${done.label}」已核对：${done.message}；没有重发。`); mtRenderPending(); };
    mtEl('mtAckPending').onclick = () => { mtAcknowledgeUnknown(); mtRenderPending(); };
}
async function mtPoll() {
    if (!mt.active || !mt.target || !mtTargetReady() || !mtOwner() || document.hidden) return;
    if (mt.tab === 'startup') await mtPollStartup();
    else if (typeof tcPoll === 'function') await tcPoll();
    mtRenderPending();
}
/** 嘟嘟可远程修改类操作用的本人凭据：按成员 UID 找到其助手实例，再取这个浏览器为该实例保存的凭据。 */
function mtOwnerArgsForUid(uid) {
    const p = (players || []).find(x => x.playerUid === uid && x.clientInstanceId);
    const cred = p ? mtCredentials()[p.clientInstanceId] : null;
    return cred ? { browserId:cred.browserId, secret:cred.secret } : null;
}
/** 嘟嘟可页内授权：向该成员的助手实例兑换授权码（与槲寄生同一份凭据）。 */
async function mtAuthorizeUid(uid, code) {
    const p = (players || []).find(x => x.playerUid === uid && x.clientInstanceId && x.online);
    if (!p) throw new Error('成员不在线或实例未知');
    const target = { key:'remote:' + uid + ':' + p.clientInstanceId, local:false, uid, name:p.playerName || uid, instanceId:p.clientInstanceId, online:true };
    const label = (navigator.userAgent.includes('Mobile') ? '手机' : '电脑') + '网页 · ' + new Date().toLocaleDateString();
    const reply = await mtSend('web.authorize', undefined, { target, extra:{ code, label } });
    if (reply.status !== 'ok' || !reply.data?.browserId || typeof reply.data.secret !== 'string') throw new Error(reply.message || '授权失败');
    mtSaveCredential(target, { browserId:reply.data.browserId, secret:reply.data.secret, uid });
}
