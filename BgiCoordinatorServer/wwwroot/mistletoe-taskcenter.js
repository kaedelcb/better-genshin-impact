// 槲寄生 · 任务中心（基础模式 · 计划表）。数据全部来自执行端 task.* 远程中心协议：
// 计划文档原样往返，只改用户触碰的字段（未知字段、多触发器、自定义循环/收尾、控制节点、高级排程均保留）；
// 账号与绑定码掩码显示，输入框留空表示不修改；保存带修订号，冲突时保留草稿不覆盖。
const TC_STATE = ['Planned','Waiting','Running','Paused','Succeeded','Failed','Cancelled','Interrupted','Unknown','Completing','LocalWaitParking','OnlineHoeingParking'];
const TC_STATE_TEXT = { Succeeded:'已完成', Failed:'失败', Cancelled:'已取消', Interrupted:'已中断', Unknown:'结果未知', Planned:'已计划', Waiting:'等待触发', Running:'运行中', Paused:'已暂停', Completing:'收尾中', LocalWaitParking:'等待空闲', OnlineHoeingParking:'联机锄地中' };
const TC_WEEK = ['周一','周二','周三','周四','周五','周六','周日'];
const TC_KIND = { 'resource.oneDragonConfig':'整龙配置', 'resource.configGroup':'配置组', 'resource.singleTask':'单项任务' };
const TC_START_KEY = 'taskCenterStartNodeId';
const TC_TERMINAL = [['', '保持软件和游戏打开'], ['closeGame', '关闭游戏'], ['closeSoftware', '关闭软件'], ['closeGameAndSoftware', '关闭游戏和软件'], ['shutdown', '关闭计算机']];
const tc = { list:null, listError:'', loading:false, selected:'', doc:null, base:null, revision:'', dirty:false, saving:false, saveTimer:null,
    conflict:'', loadingDoc:false, selectedNode:'', starting:false, catalogBusy:false, consumed:new Map(), dragging:null };

function tcReset() { clearTimeout(tc.saveTimer); Object.assign(tc, { list:null, listError:'', loading:false, selected:'', doc:null, base:null, revision:'', dirty:false, saving:false, conflict:'', selectedNode:'', starting:false }); }
function tcState(run) { const s = mtPick(run, 'state'); return typeof s === 'number' ? TC_STATE[s] || String(s) : String(s || ''); }
function tcFlows() { return (mtPick(tc.list, 'flows') || []).map(f => ({ id:mtPick(f, 'workflowId'), name:mtPick(f, 'name'), revision:mtPick(f, 'revision'),
    quarantined:mtPick(f, 'status') === 1 || mtPick(f, 'status') === 'Quarantined', reason:mtPick(f, 'quarantineReason'), unsupported:mtPick(f, 'unsupportedKinds') || [],
    candidate:mtPick(f, 'activationStatus') === 'candidate-ready' })); }
function tcFlow(id = tc.selected) { return tcFlows().find(f => f.id === id) || null; }
function tcCanEdit(f) { return f && !f.candidate && !f.quarantined; }
function tcCanStart(f) { return tcCanEdit(f) && !f.unsupported.length; }
function tcActive() { return mtPick(tc.list, 'active') || []; }
function tcDriving(id) { return (mtPick(tc.list, 'drivingWorkflowIds') || []).includes(id); }
/** 只用于历史记录显示。运行列表（active）由执行端按完整重置证据过滤，已有效重置的运行不会出现在里面；
 *  网页无法复核证据哈希，因此运行卡片一律按原状态显示，不能因为字段存在就隐藏「停止」。 */
function tcReleased(run) { return !!run.executionResetRelease; }
function tcMask(v) { if (!v) return '未设置'; return v.length <= 5 ? '*****' : v.slice(0, 3) + '***' + v.slice(-2); }
function tcStrat(node, kind) { return (node.strategies || []).find(s => s.kind === kind) || null; }
function tcResourceNodes(doc = tc.doc) { return (doc?.nodes || []).filter(n => String(n.kind).startsWith('resource.')); }
function tcCatalog() { return mtPick(tc.list, 'catalog') || { entries:[] }; }
function tcNodeName(n) {
    if (n.kind === 'resource.singleTask') {
        const e = (tcCatalog().entries || []).find(x => x.kind === 2 && x.ownerConfig === n.ref?.config && x.stableId === `single:${x.ownerConfig}:${n.ref?.taskId}`);
        return e ? e.displayName : `${n.ref?.config || '（未绑定资源）'} · ${n.ref?.taskId || ''}`;
    }
    return n.ref?.config || '（未绑定资源）';
}
function tcEnabled(n) { return !(n.planDisabled === true || n.legacyFiltered === true); }
function tcWeekText(n) { const w = tcStrat(n, 'condition.weekdays'); if (!w) return '每天'; const d = Array.isArray(w.days) ? w.days : []; return d.length ? d.map(x => String(x).slice(1)).join('/') : '未选择日期'; }
function tcWindowText(n) {
    const w = n.timeWindow; if (!w) return '全天';
    const ok = /^\d\d:\d\d$/; if (!ok.test(w.start) || !ok.test(w.end)) return '时段无效';
    return w.start === w.end ? '全天' : `${w.start}–${w.start > w.end ? '次日 ' : ''}${w.end}`;
}
function tcInWindow(n, now) {
    const w = n.timeWindow; if (!w || w.start === w.end) return true;
    const m = now.getHours() * 60 + now.getMinutes(), t = s => +s.slice(0, 2) * 60 + +s.slice(3);
    const a = t(w.start), b = t(w.end); return a < b ? m >= a && m < b : m >= a || m < b;
}
/** 执行端时钟：用 task.list 的 serverTime 加本地经过时间推算，时段判断按执行端本机时间。 */
function tcNow() { const st = mtPick(tc.list, 'serverTime'); if (!st || !tc.listAt) return new Date(); const d = new Date(String(st).slice(0, 19)); return new Date(d.getTime() + (performance.now() - tc.listAt)); }
function tcStartMarker() { const v = tc.doc?.[TC_START_KEY]; return typeof v === 'string' && tc.consumed.get(tc.selected) !== v ? v : null; }

// ---------- 运行卡片（文案与可用动作同 PC ActiveRunVm） ----------
function tcRunView(run) {
    const st = tcState(run), id = mtPick(run, 'workflowId'), driving = tcDriving(id), wait = run.wait?.nextTriggerAt;
    const lw = run.localWaitDecision;
    let text = { Planned:driving ? '正在准备' : '已计划', Running:driving ? '运行中' : '执行已中断 · 等待核对', Waiting:`等待触发（${wait ? String(wait).slice(5, 16).replace('T', ' ') : '—'}）`,
        LocalWaitParking:lw && lw.kind === 1 && lw.binding ? '等待空闲 · 按优先级接续' : '等待确认 · 可恢复或停止', OnlineHoeingParking:'联机锄地中 · 等待原计划接续',
        Completing:'收尾中', Paused:'已暂停', Interrupted:'已中断（可显式恢复）', Unknown:'无法确认上次结果' }[st] || TC_STATE_TEXT[st] || st;
    let hint = { Unknown:'上次任务结果未知，当前计划已禁止重复启动；请保留未确认历史并删除旧计划后新建计划，或使用「重置执行状态」。',
        Planned:driving ? '任务正在准备；需要取消时点击「停止」。' : '', Running:driving ? '任务正在执行；需要结束时点击「停止」。' : '计划推进已中断，当前节点仅为最后记录；请核对恢复或停止结果，未确认前不会重跑。',
        Paused:'任务已暂停；准备继续时点击「恢复」。', Interrupted:'任务已中断；确认没有其他同计划运行后点击「恢复」。', LocalWaitParking:'任务正在等待可用执行位置；可继续等待，或点击「停止」结束本次计划。',
        OnlineHoeingParking:'原计划保留在当前节点；锄地完成且恢复作业终态确认后自动继续。', Waiting:'任务正在等待设定时间；需要取消时点击「停止」。', Completing:'任务正在收尾，请等待状态确认。' }[st] || '';
    let blocks = ['Running','Waiting','Paused','Completing','Unknown','LocalWaitParking','OnlineHoeingParking'].includes(st) || st === 'Planned' && driving;
    let canStop = ['Running','Waiting','Paused','Completing','LocalWaitParking','OnlineHoeingParking','Interrupted','Unknown'].includes(st) || st === 'Planned' && driving;
    let canSkip = driving && st === 'Running', canPause = !run.onlineHoeingHandoff && driving && (st === 'Running' || st === 'Waiting');
    let canReload = driving && ['Running','Waiting','Paused'].includes(st);
    const sub = run.currentSubmission;
    let canResume = !run.onlineHoeingHandoff && !run.stopRequested && (['Interrupted','Paused','LocalWaitParking'].includes(st) || !driving && (st === 'Running' || st === 'Waiting')
        || !driving && st === 'Unknown' && sub && (sub.intent === 3 || sub.intent === 'Accepted') && sub.sendAttempted && sub.jobId && sub.originalRequestEvidence && sub.acceptedSendIdentity);
    const snap = (mtPick(tc.list, 'activeFlowSnapshots') || {})[id], doc = mtPick(snap, 'document'), cur = run.cursor;
    const node = doc?.nodes?.find(n => n.nodeId === cur?.nodeId);
    const current = !doc ? '当前节点：—（流程定义不可读）' : `当前节点：${node ? tcNodeName(node) : cur?.nodeId || '—'}（第 ${(cur?.loopIteration || 0) + 1} 轮）${mtPick(snap, 'revision') !== run.workflowRevision ? '（流程已出新修订，进度按当前定义近似映射）' : ''}`;
    const tone = st === 'Unknown' || st === 'Running' && !driving ? 'warn' : ['Paused','Interrupted','OnlineHoeingParking'].includes(st) ? 'warn' : 'ok';
    return { run, id, st, text, hint, blocks, canStop, canSkip, canPause, canReload, canResume, current, tone, name:doc?.name || tcFlows().find(f => f.id === id)?.name || id };
}
function tcBlocking(id) { return tcActive().map(tcRunView).find(v => v.id === id && v.blocks) || null; }

// ---------- 读取与轮询 ----------
async function tcLoad(silent) {
    // 操作后的刷新若碰上正在进行的轮询，等它结束再读一次，避免显示操作前的旧列表。
    if (tc.loading) { const prev = tc.loadPromise; await prev?.catch(() => {}); if (tc.loading) return tc.loadPromise; }
    tc.loading = true;
    let done; tc.loadPromise = new Promise(r => done = r);
    try {
        const reply = await mtRead('task.list', undefined, { timeout:20000 });
        tc.list = reply.data || {}; tc.listAt = performance.now(); tc.listError = '';
        if (mt.unconfirmed) await mtReconcile(true);
        const flows = tcFlows();
        if (tc.selected && !flows.some(f => f.id === tc.selected)) { tc.selected = ''; tc.doc = null; }
        if (!tc.selected) { const f = flows.find(x => tcCanStart(x) && !tcBlocking(x.id)) || flows.find(tcCanStart) || flows.find(tcCanEdit) || flows.find(f => !f.quarantined); if (f) await tcOpen(f.id, true); }
        else {
            const f = tcFlow();
            if (f && tc.doc && f.revision !== tc.revision && !tc.saving) {
                if (tc.dirty) tc.conflict = '执行端已保存了另一份修改。本端草稿已保留；请「读取最新计划」后重新修改。';
                else await tcOpen(tc.selected, true);
            }
        }
        if (!silent) mtToast('已读取执行端任务中心。');
    } catch (error) { tc.listError = error.message; mtToast('执行端任务中心不可用：' + error.message, true); }
    finally { tc.loading = false; done(); }
}
async function tcPoll() {
    if (mt.tab !== 'history' && (mt.tab !== 'taskcenter' || tc.saving || tc.starting)) return;
    const before = tc.listSig, docBefore = tc.revision + tc.selected;
    await tcLoad(true);
    // serverTime 每次都变，比较时去掉；数据和打开的计划都没变就不重绘（保留展开的明细、拖动和点击）。
    const { serverTime, ServerTime, ...rest } = tc.list || {};
    tc.listSig = JSON.stringify(rest);
    if (tc.listSig === before && docBefore === tc.revision + tc.selected) { if (mt.tab === 'taskcenter') tcRenderNodesClock(); return; }
    if (mt.tab === 'history') { tcRenderHistory(mtEl('mtContent')); return; }
    tcRenderRuns(); tcRenderPlans(); tcRenderPlanHead(); if (!tc.dragging && !mtDialog('tcDialog').open) tcRenderNodes();
}
/** 「时段外」标记按执行端时钟推算；数据没变时每分钟刷新一次即可。 */
function tcRenderNodesClock() {
    const minute = Math.floor(tcNow().getTime() / 60000);
    if (minute === tc.clockMinute) return;
    tc.clockMinute = minute;
    if (tc.doc?.nodeTimeWindowEnabled && !tc.dragging && !mtDialog('tcDialog').open) tcRenderNodes();
}
async function tcOpen(id, quiet) {
    if (tc.dirty && !quiet && !(await tcFlush())) return;
    tc.loadingDoc = true;
    try {
        const reply = await mtRead('task.flow.get', id);
        const doc = reply.data;
        if (!doc || typeof doc !== 'object' || !reply.revision) throw new Error('执行端计划为空');
        if (doc.workflowId && doc.workflowId !== id) throw new Error('执行端计划身份不匹配');
        tc.selected = id; tc.doc = doc; tc.base = mtClone(doc); tc.revision = reply.revision; tc.dirty = false; tc.conflict = '';
        if (!tc.doc.nodes?.some(n => n.nodeId === tc.selectedNode)) tc.selectedNode = '';
    } catch (error) { mtToast('打开执行端计划失败：' + error.message, true); }
    finally { tc.loadingDoc = false; }
}

// ---------- 保存（防抖；带修订；冲突保留草稿） ----------
function tcEdited(message) {
    if (!tcCanEdit(tcFlow())) return;
    tc.dirty = true; if (message) mtToast(message);
    clearTimeout(tc.saveTimer); tc.saveTimer = setTimeout(tcSave, 1200);
    tcRenderPlanHead(); tcRenderNodes();
}
async function tcSave() {
    clearTimeout(tc.saveTimer);
    if (!tc.dirty || tc.saving || !tc.doc) return !tc.dirty;
    if (tc.conflict) { mtToast(tc.conflict, true); return false; }
    tc.saving = true; tcRenderPlanHead();
    const snapshot = JSON.stringify(tc.doc), id = tc.selected;
    const reply = await mtOperate('task.flow.save', mtClone(tc.doc), { expectedRevision:() => tc.revision, label:'保存任务计划', scope:id, proceedAfterCheck:true });
    tc.saving = false;
    if (reply.status === 'ok' && reply.revision && id === tc.selected) {
        tc.revision = reply.revision; tc.base = JSON.parse(snapshot);
        tc.dirty = JSON.stringify(tc.doc) !== snapshot;
        if (tc.dirty) tc.saveTimer = setTimeout(tcSave, 600); else mtToast('计划已自动保存到执行端。');
    } else if (reply.status === 'rejected' && /修订冲突|已被外部|不存在/.test(reply.message || '')) {
        tc.conflict = '执行端已保存了另一份修改。本端草稿已保留；请「读取最新计划」后重新修改。' + (reply.message || '');
        mtToast(tc.conflict, true);
    } else mtToast((reply.status === 'unknown' ? '执行端计划保存结果未确认，草稿已保留：' : reply.status === 'blocked' ? '草稿已保留，' : '计划未保存，草稿已保留：') + (reply.message || ''), true);
    tcRenderPlanHead();
    return reply.status === 'ok' && !tc.dirty;
}
async function tcFlush() {
    if (!tc.dirty) return true;
    if (tc.conflict) { mtToast(tc.conflict, true); return confirm('当前计划有未保存的草稿（与执行端冲突）。离开会放弃这些修改，是否继续？') ? (tc.dirty = false, true) : false; }
    for (let i = 0; tc.saving && i < 100; i++) await new Promise(r => setTimeout(r, 100));
    return tc.dirty ? await tcSave() : true;
}

// ---------- 渲染 ----------
function tcRender(host) {
    if (mt.identity && !mt.identity.isExecutor) { host.innerHTML = '<section class="dd-card dd-empty">该助手是监控端：任务中心只在执行端可用。请在右上角选择你的执行端助手。</section>'; return; }
    if (!tc.list) {
        host.innerHTML = `<section class="dd-card dd-empty">${tc.listError ? '执行端任务中心不可用：' + mte(tc.listError) + '<br><button class="dd-btn" id="tcRetry">重新读取</button>' : '正在读取执行端任务中心…'}</section>`;
        if (tc.listError) mtEl('tcRetry').onclick = async () => { tc.listError = ''; tcRender(host); await tcLoad(); tcRender(host); };
        else if (!tc.loading) tcLoad().then(() => mt.tab === 'taskcenter' && tcRender(mtEl('mtContent')));
        return;
    }
    host.innerHTML = `<div id="tcRuns"></div><div class="tc-layout"><aside class="dd-card tc-plans"><header class="tc-plans-head"><h2>计划表</h2><div class="dd-actions"><button id="tcNew" class="dd-btn dd-btn-sm" title="新建计划表">＋ 新建</button><button id="tcManage" class="dd-btn dd-btn-sm" title="导入、导出、恢复已删除计划">⋯ 管理</button></div></header><div id="tcPlanList" class="tc-plan-list"></div></aside>
      <section class="tc-main"><div id="tcPlanHead" class="dd-card tc-plan-head"></div><div id="tcNodes" class="dd-card tc-nodes"></div></section></div>`;
    mtEl('tcNew').onclick = tcNewFlow; mtEl('tcManage').onclick = tcManage;
    tcRenderRuns(); tcRenderPlans(); tcRenderPlanHead(); tcRenderNodes();
}
function tcRenderRuns() {
    const host = mtEl('tcRuns'); if (!host) return;
    // 同 PC CurrentPlanRuns：只展示当前选中计划的运行；结果未知（Unknown）和已重置的旧记录留在运行记录里，
    // 不铺成首屏卡片。它们仍按 PC 规则阻止同计划再次启动，原因和「重置执行状态」显示在计划标题区。
    const views = tcActive().filter(r => mtPick(r, 'workflowId') === tc.selected && tcState(r) !== 'Unknown' && !tcReleased(r)).map(tcRunView);
    if (!views.length) { host.innerHTML = ''; host.className = ''; return; }
    host.className = 'tc-runs';
    host.innerHTML = views.map(v => `<article class="dd-card tc-run tone-${v.tone}"><div class="tc-run-main"><span class="dd-pill ${v.tone}">${mte(v.text)}</span><div class="tc-run-text"><strong>${mte(v.name)}</strong><small>${mte(v.current)}</small>${v.hint ? `<small>${mte(v.hint)}</small>` : ''}${v.run.note ? `<small class="mt-break tc-run-note" title="${mte(v.run.note)}">${mte(v.run.note)}</small>` : ''}</div></div>
      <div class="dd-actions">${v.canPause ? `<button class="dd-btn dd-btn-sm" data-run-act="Pause" data-run="${mte(v.run.runId)}" title="当前节点结束后暂停计划">暂停</button>` : ''}${v.canResume ? `<button class="dd-btn dd-btn-sm" data-run-resume="${mte(v.run.runId)}">恢复</button>` : ''}${v.canSkip ? `<button class="dd-btn dd-btn-sm" data-run-act="SkipCurrent" data-run="${mte(v.run.runId)}">跳过当前</button>` : ''}${v.canReload ? `<button class="dd-btn dd-btn-sm dd-btn-quiet" data-run-act="ReloadDefinition" data-run="${mte(v.run.runId)}" title="新修订在下一节点边界生效">重载计划</button>` : ''}${v.canStop ? `<button class="dd-btn dd-btn-sm dd-btn-danger" data-run-act="Stop" data-run="${mte(v.run.runId)}">停止</button>` : ''}</div></article>`).join('');
    host.querySelectorAll('[data-run-act]').forEach(b => b.onclick = () => tcRunAction(b.dataset.run, b.dataset.runAct, b));
    host.querySelectorAll('[data-run-resume]').forEach(b => b.onclick = () => tcResume(b.dataset.runResume, b));
}
function tcRenderPlans() {
    const host = mtEl('tcPlanList'); if (!host) return;
    const flows = tcFlows();
    host.innerHTML = flows.length ? flows.map(f => { const run = tcBlocking(f.id); return `<button class="tc-plan${f.id === tc.selected ? ' active' : ''}" data-plan="${mte(f.id)}"><span class="tc-plan-name">${mte(f.name)}</span><small>${f.quarantined ? '已隔离 · 只能查看原因' : f.candidate ? '只读候选 · 可激活' : f.unsupported.length ? '含未支持类型 · 不可启动' : run ? mte(run.text) : '就绪'}</small></button>`; }).join('')
        : '<p class="dd-empty">还没有计划表。点「＋ 新建」开始安排配置单。</p>';
    host.querySelectorAll('[data-plan]').forEach(b => b.onclick = async () => { if (b.dataset.plan === tc.selected) return; await tcOpen(b.dataset.plan); tcRenderRuns(); tcRenderPlans(); tcRenderPlanHead(); tcRenderNodes(); });
}
function tcPlanSummary(doc) {
    const trig = (doc.triggers || []).length === 1 ? doc.triggers[0] : null, t = trig?.time || trig?.at;
    const a = !doc.triggers?.length ? '立即开始' : !t ? `触发器 ${doc.triggers.length} 个（自定义）` : trig.kind === 'trigger.timeFixed' ? `固定 ${t}` : trig.kind === 'trigger.timeFlexible' ? `灵活 ${t}${trig.until ? '-' + trig.until : ''}` : `按顺序 ${t}`;
    const l = !doc.loop ? '执行一轮' : doc.loop.mode === 'scheduled' ? (doc.loop.time ? `每天 ${doc.loop.time} 循环` : '每天循环') : doc.loop.mode === 'immediate' ? '完成后循环' : '沿用自定义循环';
    const f = doc.execution?.onNodeFailure === 'continue' ? '失败继续' : '失败停止';
    const term = doc.terminal?.[0]; const e = !term ? '' : ' · ' + (TC_TERMINAL.find(x => x[0] === term.action)?.[1] || '自定义收尾');
    return `${a} · ${l} · ${f}${e}`;
}
function tcRenderPlanHead() {
    const host = mtEl('tcPlanHead'); if (!host) return;
    const f = tcFlow(), doc = tc.doc;
    if (!f || !doc) { host.innerHTML = `<p class="dd-empty">${tcFlows().length ? (tc.loadingDoc ? '正在打开计划…' : '在左侧选择一张计划表') : '先新建一张计划表，再添加配置单。'}</p>`; return; }
    const nodes = tcResourceNodes(), enabled = nodes.filter(tcEnabled).length, block = tcBlocking(f.id), marker = tcStartMarker();
    const unknown = tcActive().filter(r => mtPick(r, 'workflowId') === f.id && tcState(r) === 'Unknown' && !tcReleased(r));
    const lastUnknown = unknown.map(r => String(mtPick(r, 'updatedAt') || '')).sort().at(-1) || '';
    const markerNode = marker ? doc.nodes.find(n => n.nodeId === marker) : null;
    const reason = f.quarantined ? '隔离原因：' + (f.reason || '未知') : f.candidate ? '迁移候选：只读预览；激活后才可编辑和启动。' : f.unsupported.length ? '未支持类型：' + f.unsupported.join('、') : unknown.length ? `这张计划有 ${unknown.length} 条旧运行结果无法确认（最近一次 ${lastUnknown.replace('T', ' ').slice(5, 16)}），执行端禁止它再次启动，PC 端同样不能启动。点「重置状态」放弃这些旧执行，历史保留、不会自动启动；之后即可重新启动。` : block ? `当前计划已有运行（${block.text}），处理后才能再次启动。` : !enabled ? '当前计划没有启用的配置单。' : '';
    const save = tc.conflict ? `<span class="mt-save-state is-error">${mte(tc.conflict)}</span>` : `<span class="mt-save-state${tc.dirty || tc.saving ? ' is-dirty' : ''}">${tc.saving ? '正在保存到执行端…' : tc.dirty ? '有修改，稍后自动保存' : '已与执行端同步'}</span>`;
    host.innerHTML = `<div class="tc-head-row"><div class="tc-head-title"><h2 class="mt-break">${mte(doc.name || f.name)}</h2><p class="dodoco-hint">${nodes.length ? `${nodes.length} 个配置单 · ${enabled} 个已启用` : '空计划 · 先新增任务'} · ${mte(tcPlanSummary(doc))}${markerNode ? ` · 从「${mte(tcNodeName(markerNode))}」开始` : ''}</p></div>
        <div class="dd-actions"><button id="tcReset" class="dd-btn dd-btn-sm${unknown.length ? ' dd-btn-primary' : ''}" title="停止或放弃当前计划的旧执行，保留计划内容和原失败历史；不会自动启动。执行端 BGI 未运行时会先启动 BGI 再核对"${f.quarantined || tc.resetting ? ' disabled' : ''}>${tc.resetting ? '正在重置…' : '重置状态'}</button><button id="tcSettings" class="dd-btn dd-btn-sm"${tcCanEdit(f) ? '' : ' disabled'}>计划设置</button><button id="tcStart" class="dd-btn dd-btn-primary"${!tcCanStart(f) || block || !enabled || tc.starting || tc.conflict ? ' disabled' : ''}>⏵⏵ 连续一条龙</button></div></div>
      <div class="tc-head-row tc-head-sub">${save}${reason ? `<span class="tc-reason">${mte(reason)}</span>` : ''}<div class="dd-actions">${tc.conflict ? '<button id="tcReloadPlan" class="dd-btn dd-btn-sm">读取最新计划</button>' : ''}${f.candidate ? '<button id="tcActivate" class="dd-btn dd-btn-sm">激活此候选</button>' : ''}<label class="dd-check" title="开启后时段外任务跳过；关闭后保留设置，按原计划执行"><input id="tcWindowSwitch" type="checkbox"${doc.nodeTimeWindowEnabled ? ' checked' : ''}${tcCanEdit(f) ? '' : ' disabled'}>按时段执行</label></div></div>`;
    mtEl('tcSettings').onclick = tcPlanSettings; mtEl('tcStart').onclick = () => tcStart(null);
    if (mtEl('tcReloadPlan')) mtEl('tcReloadPlan').onclick = async () => { if (!confirm('读取最新计划会放弃本端未保存的草稿，是否继续？')) return; tc.dirty = false; tc.conflict = ''; await tcOpen(tc.selected, true); tcRenderPlanHead(); tcRenderNodes(); };
    mtEl('tcReset').onclick = () => tcManageAction('reset');
    if (mtEl('tcActivate')) mtEl('tcActivate').onclick = () => tcMigration('task.migration.activate', f.id, `激活候选「${f.name}」`);
    mtEl('tcWindowSwitch').onchange = e => { doc.nodeTimeWindowEnabled = e.target.checked; tcEdited(e.target.checked ? '已开启按时段执行：时段外任务跳过。' : '已关闭按时段执行：保留时段设置，按原计划执行。'); };
}
function tcRenderNodes() {
    const host = mtEl('tcNodes'); if (!host) return;
    const f = tcFlow(), doc = tc.doc;
    if (!f || !doc) { host.innerHTML = ''; host.hidden = true; return; }
    host.hidden = false;
    const editable = tcCanEdit(f), nodes = tcResourceNodes(), marker = tcStartMarker(), now = tcNow();
    const head = `<header class="tc-nodes-head"><div><h3>配置单</h3><p class="dodoco-hint">${editable ? '拖动 ⠿ 调整顺序；点日期、时段或账号直接编辑。运行中修改会在下一个任务边界生效。' : '只读：' + (f.quarantined ? '隔离计划不可编辑' : '候选计划需先激活')}</p></div>${editable ? '<button id="tcAdd" class="dd-btn dd-btn-sm dd-btn-primary">＋ 新增任务</button>' : ''}</header>`;
    if (!nodes.length) { host.innerHTML = head + `<p class="dd-empty">${editable ? '这张计划表还是空的。先添加一个配置单，修改会自动保存，之后即可启动计划。' : '没有配置单。'}</p>`; if (editable) mtEl('tcAdd').onclick = () => tcAddDialog(); return; }
    host.innerHTML = head + `<div class="tc-table" role="table"><div class="tc-tr tc-th" role="row"><span></span><span>顺序</span><span>配置单</span><span>执行日期</span><span>执行时段</span><span>UID / 账号</span><span>启用</span><span></span></div>`
        + nodes.map((n, i) => {
            const acc = tcStrat(n, 'prerequisite.account'), out = doc.nodeTimeWindowEnabled && !tcInWindow(n, now) && tcEnabled(n);
            return `<div class="tc-tr${n.nodeId === tc.selectedNode ? ' selected' : ''}${tcEnabled(n) ? '' : ' off'}" role="row" data-node="${mte(n.nodeId)}"${editable ? ' draggable="true"' : ''}>
              <span class="mt-grip" aria-hidden="true">${editable ? '⠿' : ''}</span><span class="tc-idx">${i + 1}${n.nodeId === marker ? '<i class="tc-start" title="点击启动计划时从这里开始">起点</i>' : ''}</span>
              <span class="tc-name"><b class="mt-break">${mte(tcNodeName(n))}</b><small>${TC_KIND[n.kind]}${tcStrat(n, 'execution.failure') ? ' · ' + (tcStrat(n, 'execution.failure').onFailure === 'continue' ? '失败继续' : '失败停止') : ''}${n.legacyFiltered ? ' · 沿用旧版计划设置' : ''}</small></span>
              <button class="tc-cell" data-edit="week"${editable ? '' : ' disabled'}><span class="tc-label">执行日期</span>${mte(tcWeekText(n))}</button>
              <button class="tc-cell${out ? ' is-out' : ''}" data-edit="window"${editable ? '' : ' disabled'}><span class="tc-label">执行时段</span>${mte(tcWindowText(n))}${out ? ' <i class="tc-out">时段外</i>' : ''}</button>
              <button class="tc-cell" data-edit="account"${editable ? '' : ' disabled'}><span class="tc-label">账号</span>${acc ? mte(tcMask(acc.uid)) : '未绑定账号'}</button>
              <label class="tc-toggle"><input type="checkbox" data-enable${tcEnabled(n) ? ' checked' : ''}${editable ? '' : ' disabled'} aria-label="启用此配置单"></label>
              <span class="tc-row-act"><button class="dd-btn dd-btn-sm dd-btn-icon" data-tasks title="设置此配置单内部任务的启用、顺序和执行起点">⚙</button><button class="dd-btn dd-btn-sm dd-btn-icon" data-more title="更多操作">⋯</button></span></div>`;
        }).join('') + '</div>';
    if (editable) mtEl('tcAdd').onclick = () => tcAddDialog();
    host.querySelectorAll('.tc-tr[data-node]').forEach(row => {
        const node = doc.nodes.find(n => n.nodeId === row.dataset.node);
        row.onclick = e => { if (e.target.closest('button,input,label')) return; tc.selectedNode = node.nodeId; tcRenderNodes(); };
        row.querySelectorAll('[data-edit]').forEach(b => b.onclick = () => { tc.selectedNode = node.nodeId; tcNodeDialog(node, b.dataset.edit); });
        row.querySelector('[data-enable]').onchange = e => { tcSetEnabled(node, e.target.checked); tcEdited(e.target.checked ? `已启用「${tcNodeName(node)}」` : `已停用「${tcNodeName(node)}」：执行时跳过`); };
        row.querySelector('[data-tasks]').onclick = () => tcInnerTasks(node);
        row.querySelector('[data-more]').onclick = () => tcNodeMenu(node);
        if (!editable) return;
        row.ondragstart = e => { tc.dragging = node.nodeId; e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', node.nodeId); row.classList.add('dragging'); };
        row.ondragend = () => { tc.dragging = null; tcRenderNodes(); };
        row.ondragover = e => { if (!tc.dragging) return; e.preventDefault(); const r = row.getBoundingClientRect(), after = e.clientY > r.top + r.height / 2; row.classList.toggle('drop-after', after); row.classList.toggle('drop-before', !after); };
        row.ondragleave = () => row.classList.remove('drop-before', 'drop-after');
        row.ondrop = e => { e.preventDefault(); const id = tc.dragging, after = row.classList.contains('drop-after'); tc.dragging = null; if (id && id !== node.nodeId) tcMoveTo(id, node.nodeId, after); else tcRenderNodes(); };
    });
}
function tcSetEnabled(n, on) {
    const legacy = n.legacyFiltered === true;
    n.planDisabled = !on;
    if (on && legacy) n.legacyFiltered = false;
}
/** 同 PC MoveNodeTo：在完整节点数组里移动（控制节点位置不变），保留节点对象和稳定身份。 */
function tcMoveTo(id, targetId, after) {
    const list = tc.doc.nodes, from = list.findIndex(n => n.nodeId === id); let to = list.findIndex(n => n.nodeId === targetId);
    if (from < 0 || to < 0) return;
    const [node] = list.splice(from, 1); to = list.findIndex(n => n.nodeId === targetId) + (after ? 1 : 0); list.splice(to, 0, node);
    tcEdited(`已把「${tcNodeName(node)}」移到第 ${tcResourceNodes().indexOf(node) + 1} 位`);
}
function tcMove(node, delta) {
    const res = tcResourceNodes(), i = res.indexOf(node), j = i + delta;
    if (j < 0 || j >= res.length) return;
    tcMoveTo(node.nodeId, res[j].nodeId, delta > 0);
}
function tcNewNodeId() { let id; do id = 'n-' + mtNewId().slice(0, 8); while (tc.doc.nodes.some(n => n.nodeId === id)); return id; }

// ---------- 弹窗：节点设置（执行日期 / 时段 / 账号 / 失败处理 / 优先级） ----------
function tcDialogShell(title, sub, body, foot) {
    const dialog = mtDialog('tcDialog');
    dialog.innerHTML = `<header class="mt-dialog-head"><div><h3 class="mt-break">${title}</h3>${sub ? `<p class="dodoco-hint mt-break">${sub}</p>` : ''}</div><button class="dd-btn dd-btn-sm" data-close>关闭</button></header><div class="tc-dialog-body">${body}</div>${foot ? `<footer class="tc-dialog-foot">${foot}</footer>` : ''}`;
    dialog.querySelector('[data-close]').onclick = () => dialog.close();
    if (!dialog.open) dialog.showModal();
    return dialog;
}
function tcNodeDialog(node, focus) {
    const w = tcStrat(node, 'condition.weekdays'), days = Array.isArray(w?.days) ? w.days : TC_WEEK, acc = tcStrat(node, 'prerequisite.account');
    const fail = tcStrat(node, 'execution.failure'), pr = tcStrat(node, 'schedule.priority'), win = node.timeWindow;
    const redeem = tcStrat(node, 'prerequisite.redeemCode');
    const body = `<section class="tc-sec${focus === 'week' ? ' focus' : ''}"><h4>执行日期</h4><label class="dd-check"><input type="checkbox" id="tcWeekOn"${w ? ' checked' : ''}>只在选定的星期执行（未勾选时每天执行）</label><div class="mt-week" id="tcWeekDays">${TC_WEEK.map(d => `<label><input type="checkbox" value="${d}"${days.includes(d) ? ' checked' : ''}${w ? '' : ' disabled'}>${d.slice(1)}</label>`).join('')}</div></section>
      <section class="tc-sec${focus === 'window' ? ' focus' : ''}"><h4>执行时段</h4><p class="dodoco-hint">按执行端本机时间判断，包含开始、不含结束；结束早于开始表示跨午夜，起止相同表示全天。计划的「按时段执行」关闭时保留设置，按原计划执行。</p><div class="mt-form-row">${mtField('开始', `<input id="tcWinStart" type="time" value="${mte(win?.start || '00:00')}">`)}${mtField('结束', `<input id="tcWinEnd" type="time" value="${mte(win?.end || '00:00')}">`)}<button id="tcWinAll" class="dd-btn dd-btn-sm dd-align-end">设为全天</button></div></section>
      <section class="tc-sec${focus === 'account' ? ' focus' : ''}"><h4>账号</h4>${acc ? `<p class="dodoco-hint">当前绑定：UID ${mte(tcMask(acc.uid))} · 绑定码 ${mte(tcMask(acc.bindingCode))}</p><div class="mt-form-row">${mtField('新账号 UID（留空保留当前值）', '<input id="tcUid" maxlength="32" autocomplete="off">')}${mtField('新绑定码（留空保留当前值）', '<input id="tcBind" maxlength="128" autocomplete="off" type="password">')}</div><button id="tcAccDel" class="dd-btn dd-btn-sm dd-btn-danger">移除账号</button>` : '<p class="dodoco-hint">未绑定账号：按当前登录的账号执行。</p><button id="tcAccAdd" class="dd-btn dd-btn-sm">添加账号</button>'}
        ${redeem ? `<p class="dodoco-hint">兑换码：${redeem.uid ? 'UID ' + mte(tcMask(redeem.uid)) : '继承账号 UID'}</p>${mtField('兑换码 UID（留空保留当前值）', '<input id="tcRedeemUid" maxlength="32" autocomplete="off">')}` : ''}</section>
      <section class="tc-sec"><h4>此配置单的失败处理</h4><label class="dd-check"><input type="checkbox" id="tcFailPlan"${fail ? '' : ' checked'}>沿用计划的失败设置</label><div class="mt-form-row">${mtField('失败时', `<select id="tcFailMode"${fail ? '' : ' disabled'}><option value="stop"${fail?.onFailure === 'continue' ? '' : ' selected'}>失败后停止计划</option><option value="continue"${fail?.onFailure === 'continue' ? ' selected' : ''}>失败后继续下一个任务</option></select>`)}${mtField('最多重试次数（0–3）', `<input id="tcFailRetry" type="number" min="0" max="3" value="${mte(fail?.maxRetries ?? 0)}"${fail ? '' : ' disabled'}>`)}</div><p class="dodoco-hint">未知、取消和拒绝不会自动重试。</p></section>
      <section class="tc-sec"><h4>优先级</h4>${mtField('数值越大越优先，默认 0', `<input id="tcPriority" type="number" step="1" value="${mte(pr?.priority ?? 0)}">`)}</section>`;
    const dialog = tcDialogShell(mte(tcNodeName(node)), `${TC_KIND[node.kind]} · 节点 ${mte(node.nodeId)}`, body, '<button class="dd-btn" data-cancel>取消</button><button class="dd-btn dd-btn-primary" data-ok>保存</button>');
    const q = s => dialog.querySelector(s);
    q('#tcWeekOn').onchange = e => dialog.querySelectorAll('#tcWeekDays input').forEach(x => x.disabled = !e.target.checked);
    q('#tcWinAll').onclick = () => { q('#tcWinStart').value = '00:00'; q('#tcWinEnd').value = '00:00'; };
    q('#tcFailPlan').onchange = e => { q('#tcFailMode').disabled = q('#tcFailRetry').disabled = e.target.checked; };
    let accAction = acc ? 'keep' : 'none';
    if (q('#tcAccAdd')) q('#tcAccAdd').onclick = () => { accAction = 'add'; q('#tcAccAdd').outerHTML = `<div class="mt-form-row">${mtField('账号 UID', '<input id="tcUid" maxlength="32" autocomplete="off">')}${mtField('绑定码', '<input id="tcBind" maxlength="128" autocomplete="off" type="password">')}</div>`; };
    if (q('#tcAccDel')) q('#tcAccDel').onclick = () => { accAction = 'remove'; q('#tcAccDel').textContent = '保存后移除账号'; q('#tcAccDel').disabled = true; };
    q('[data-cancel]').onclick = () => dialog.close();
    q('[data-ok]').onclick = () => {
        try {
            const s = node.strategies ||= [];
            // 执行日期：相对原值有变化才重建 days；空选择保持「未选择日期」（不会被当作每天）。
            const on = q('#tcWeekOn').checked, picked = [...dialog.querySelectorAll('#tcWeekDays input:checked')].map(x => x.value);
            if (!on && w) s.splice(s.indexOf(w), 1);
            else if (on && !w) s.push({ kind:'condition.weekdays', days:picked.length ? picked : [...TC_WEEK], dayBoundary:'localMidnight' });
            else if (on && w && JSON.stringify(picked) !== JSON.stringify(days.filter(d => TC_WEEK.includes(d)))) w.days = picked;
            const a = q('#tcWinStart').value, b = q('#tcWinEnd').value;
            if (!/^\d\d:\d\d$/.test(a) || !/^\d\d:\d\d$/.test(b)) throw new Error('开始和结束时间须为 HH:mm');
            if (!win && a !== b) node.timeWindow = { start:a, end:b };
            else if (win && (win.start !== a || win.end !== b)) { if (a === b) delete node.timeWindow; else Object.assign(win, { start:a, end:b }); }
            const uid = q('#tcUid')?.value.trim(), bind = q('#tcBind')?.value.trim();
            if (accAction === 'remove' && acc) s.splice(s.indexOf(acc), 1);
            else if (accAction === 'add') { if (!uid) throw new Error('请填写账号 UID'); s.push({ kind:'prerequisite.account', uid, ...(bind ? { bindingCode:bind } : {}) }); }
            else if (accAction === 'keep' && acc) { if (uid) acc.uid = uid; if (bind) acc.bindingCode = bind; }
            const ru = q('#tcRedeemUid')?.value.trim(); if (ru && redeem) redeem.uid = ru;
            const usePlan = q('#tcFailPlan').checked;
            if (usePlan && fail) s.splice(s.indexOf(fail), 1);
            else if (!usePlan) {
                const r = Number(q('#tcFailRetry').value);
                if (!Number.isInteger(r) || r < 0 || r > 3) throw new Error('失败资源重试次数须为 0 至 3');
                const target = fail || (s.push({ kind:'execution.failure' }), s[s.length - 1]);
                target.onFailure = q('#tcFailMode').value; target.maxRetries = r;
            }
            const p = q('#tcPriority').value.trim(), old = pr?.priority ?? 0;
            if (!/^-?\d+$/.test(p)) throw new Error('节点优先级须为整数（数值越大越优先，默认 0）');
            if (+p !== old) { const target = pr || (s.push({ kind:'schedule.priority' }), s[s.length - 1]); target.priority = +p; }
            dialog.close(); tcEdited(`已修改「${tcNodeName(node)}」的设置，正在自动保存。`);
        } catch (error) { mtToast(error.message, true); }
    };
}
function tcNodeMenu(node) {
    const f = tcFlow(), editable = tcCanEdit(f), res = tcResourceNodes(), i = res.indexOf(node), marker = tcStartMarker();
    const block = tcBlocking(f.id);
    const dialog = tcDialogShell(mte(tcNodeName(node)), `第 ${i + 1} 个配置单 · ${TC_KIND[node.kind]}`, `<div class="tc-menu">
        <button class="dd-btn" data-a="single"${!tcCanStart(f) || block || tc.starting ? ' disabled' : ''}>▶ 启动单任务（只执行这一项）</button>
        <button class="dd-btn" data-a="from"${!tcCanStart(f) || block || tc.starting ? ' disabled' : ''}>⏵⏵ 从这里开始执行到最后</button>
        ${editable ? `<button class="dd-btn" data-a="mark">${marker === node.nodeId ? '清除「在此执行」起点' : '设为起点（下次点「连续一条龙」从这里开始）'}</button>
        <button class="dd-btn" data-a="up"${i === 0 ? ' disabled' : ''}>↑ 上移</button><button class="dd-btn" data-a="down"${i === res.length - 1 ? ' disabled' : ''}>↓ 下移</button>
        <button class="dd-btn" data-a="dup">复制此配置单</button><button class="dd-btn" data-a="settings">配置单设置…</button>
        <button class="dd-btn" data-a="ref">读取刚才的修改（同步 BGI 引用修订）</button>
        <button class="dd-btn" data-a="editor" title="在执行端桌面打开 BGI 资源编辑器">在执行端打开 BGI 资源编辑器</button>
        <button class="dd-btn dd-btn-danger" data-a="del">删除此配置单</button>` : ''}</div>`);
    dialog.querySelectorAll('[data-a]').forEach(b => b.onclick = async () => {
        const a = b.dataset.a; dialog.close();
        if (a === 'single') return tcStart(node.nodeId, node.nodeId);
        if (a === 'from') return tcStart(node.nodeId, null, true);
        if (a === 'mark') return tcMark(node, marker !== node.nodeId);
        if (a === 'up' || a === 'down') return tcMove(node, a === 'up' ? -1 : 1);
        if (a === 'dup') {
            const copy = mtClone(node); copy.nodeId = tcNewNodeId(); const list = tc.doc.nodes; list.splice(list.indexOf(node) + 1, 0, copy);
            tc.selectedNode = copy.nodeId; return tcEdited(`已复制「${tcNodeName(node)}」`);
        }
        if (a === 'settings') return tcNodeDialog(node);
        if (a === 'ref') return tcRefreshRef(node);
        if (a === 'editor') return tcOpenEditor(node);
        if (a === 'del') {
            if (!confirm(`从计划删除配置单「${tcNodeName(node)}」？`)) return;
            tc.doc.nodes.splice(tc.doc.nodes.indexOf(node), 1);
            if (tc.doc[TC_START_KEY] === node.nodeId) delete tc.doc[TC_START_KEY];
            if (tc.selectedNode === node.nodeId) tc.selectedNode = '';
            return tcEdited(`已移除配置单「${tcNodeName(node)}」，正在自动保存计划。`);
        }
    });
}
async function tcMark(node, on) {
    if (on) { tc.consumed.delete(tc.selected); tc.doc[TC_START_KEY] = node.nodeId; }
    else { tc.consumed.set(tc.selected, tc.doc[TC_START_KEY]); delete tc.doc[TC_START_KEY]; }
    tc.dirty = true; const ok = await tcSave();
    mtToast(ok ? (on ? `已设置从「${tcNodeName(node)}」开始执行；点击「连续一条龙」时将从此任务开始。` : '已清除指定起点，计划将从第一个任务开始。') : '起点标记未保存：' + (tc.conflict || '请查看计划状态'), !ok);
    tcRenderPlanHead(); tcRenderNodes();
}
async function tcRefreshRef(node) {
    try {
        if (!(await tcFlush())) throw new Error('计划尚未确认保存');
        const reply = await mtRead('task.resource.revision.get', { workflowId:tc.selected, nodeId:node.nodeId });
        if (!reply.revision) throw new Error('执行端未返回引用修订');
        const n = tc.doc.nodes.find(x => x.nodeId === node.nodeId); if (!n?.ref) return;
        if (n.ref.revision === reply.revision) return mtToast('引用修订已是最新。');
        n.ref.revision = reply.revision; tcEdited('已更新草稿中的引用修订，计划会自动保存；正在执行的资源保持原身份。');
    } catch (error) { mtToast('更新引用失败：' + error.message, true); }
}
async function tcOpenEditor(node) {
    if (!(await tcFlush())) return mtToast('计划尚未确认保存，未打开编辑器', true);
    const reply = await mtOperate('task.resource.editor.open', { workflowId:tc.selected, nodeId:node.nodeId }, { label:'在执行端打开 BGI 资源编辑器', onStage:mtStageBar });
    if (reply.status === 'ok') mtToast('已在执行端桌面打开 BGI 资源编辑器；在 BGI 保存后，用「读取刚才的修改」同步引用。');
    else mtToast(reply.message || '未打开', true);
}

// ---------- 新增任务（资源目录：配置组 / 整龙 / 单项） ----------
function tcSources() {
    const cat = tcCatalog(), entries = cat.entries || [];
    const ok = e => e.kind !== 2 || !cat.isDegraded && !e.isFromCache && e.singleExecutionSupported && e.enabled === true && tcTaskId(e);
    const list = entries.filter(ok);
    if (!entries.length) for (const e of mtPick(tc.list, 'storedResourceEntries') || []) if (e.kind !== 2 && !list.some(x => x.stableId === e.stableId)) list.push(e);
    return list;
}
function tcTaskId(e) { const prefix = 'single:' + e.ownerConfig + ':'; return e.kind === 2 && e.ownerConfig && e.stableId.startsWith(prefix) ? e.stableId.slice(prefix.length) : null; }
function tcAddDialog(state = { cat:null, q:'', picked:new Set() }) {
    const all = tcSources(), cat = tcCatalog(), kinds = [[0, '配置组', 1], [1, '整龙', 0], [2, '单项', 2]];
    // 与 PC BasicTaskCenterView 一致：有配置组先选配置组，否则整龙，否则单项（条目 kind：0 整龙、1 配置组、2 单项）。
    if (state.cat === null) state.cat = all.some(e => e.kind === 1) ? 0 : all.some(e => e.kind === 0) ? 1 : all.some(e => e.kind === 2) ? 2 : 0;
    const kindOf = k => kinds.find(x => x[0] === k)?.[2];
    const shown = all.filter(e => e.kind === kindOf(state.cat) && (!state.q || (e.displayName + ' ' + (e.ownerConfig || '')).toLowerCase().includes(state.q.toLowerCase())));
    const body = `<p class="dodoco-hint">${cat.isDegraded ? `资源目录降级（${mte(cat.degradedReason || '')}）：列表来自缓存，执行时按实时目录校验；单项任务需连接 BGI 后才能添加。` : !(cat.entries || []).length ? 'BGI 资源目录为空：只显示已保存计划里用过的配置。可点「连接 BGI 刷新」读取实时目录。' : '先选类别和任务，可多选，然后点「添加到计划」。'}</p>
      <div class="tc-add-bar"><nav class="dodoco-tabs">${kinds.map(([k, n]) => `<button data-cat="${k}" class="${k === state.cat ? 'active' : ''}">${n}</button>`).join('')}</nav><input id="tcAddSearch" type="search" placeholder="搜索当前类别" value="${mte(state.q)}"><button id="tcConnect" class="dd-btn dd-btn-sm"${tc.catalogBusy ? ' disabled' : ''}>${tc.catalogBusy ? '正在连接 BGI…' : '连接 BGI 刷新'}</button></div>
      <div class="tc-add-list">${shown.length ? shown.map(e => `<label class="tc-add-item"><input type="checkbox" value="${mte(e.stableId)}"${state.picked.has(e.stableId) ? ' checked' : ''}><span class="mt-break">${e.kind === 2 ? `<small>${mte(e.ownerConfig)} / </small>` : ''}${mte(e.displayName)}</span>${e.isFromCache ? '<i class="dd-pill">缓存</i>' : ''}</label>`).join('') : '<p class="dd-empty">当前类别没有可添加的任务</p>'}</div>`;
    const dialog = tcDialogShell('添加任务', mte(tc.doc?.name || ''), body, `<span class="tc-picked">已选 ${state.picked.size} 项</span><button class="dd-btn" data-cancel>取消</button><button class="dd-btn dd-btn-primary" data-ok${state.picked.size ? '' : ' disabled'}>添加到计划</button>`);
    dialog.querySelectorAll('[data-cat]').forEach(b => b.onclick = () => { state.cat = +b.dataset.cat; tcAddDialog(state); });
    const search = dialog.querySelector('#tcAddSearch'); search.oninput = () => { state.q = search.value; clearTimeout(state.t); state.t = setTimeout(() => { tcAddDialog(state); const s = mtDialog('tcDialog').querySelector('#tcAddSearch'); s.focus(); s.setSelectionRange(s.value.length, s.value.length); }, 250); };
    dialog.querySelectorAll('.tc-add-item input').forEach(c => c.onchange = () => { c.checked ? state.picked.add(c.value) : state.picked.delete(c.value); dialog.querySelector('.tc-picked').textContent = `已选 ${state.picked.size} 项`; dialog.querySelector('[data-ok]').disabled = !state.picked.size; });
    dialog.querySelector('#tcConnect').onclick = async () => { await tcConnectCatalog(); if (mtDialog('tcDialog').open) tcAddDialog(state); };
    dialog.querySelector('[data-cancel]').onclick = () => dialog.close();
    dialog.querySelector('[data-ok]').onclick = () => {
        const add = all.filter(e => state.picked.has(e.stableId));
        for (const e of add) {
            const node = { nodeId:tcNewNodeId(), kind:['resource.oneDragonConfig', 'resource.configGroup', 'resource.singleTask'][e.kind] || 'resource.oneDragonConfig',
                ref:{ config:e.kind === 2 ? e.ownerConfig : e.displayName, revision:e.configRevision ?? null, ...(e.kind === 2 ? { taskId:tcTaskId(e) } : {}) }, strategies:[] };
            tc.doc.nodes.push(node);
        }
        dialog.close(); tcEdited(`已添加 ${add.length} 个任务，正在自动保存计划。`);
    };
}
async function tcConnectCatalog() {
    if (tc.catalogBusy) return false;
    tc.catalogBusy = true;
    // 连接可能包含执行端 BGI 冷启动（执行端确保预算约 150 秒），与 PC 一致保留同一操作身份等待终态。
    const reply = await mtOperate('task.catalog.connect', undefined, { label:'连接执行端 BGI', onStage:mtStageBar, waitBudget:180000, timeout:25000 });
    tc.catalogBusy = false;
    if (reply.status === 'ok') { await tcLoad(true); mtToast('已连接执行端 BGI 并刷新资源目录。'); }
    else mtToast('连接 BGI 未完成：' + (reply.message || ''), true);
    return reply.status === 'ok';
}

// ---------- 计划设置 ----------
function tcPlanSettings() {
    const doc = tc.doc, trig = (doc.triggers || []).length <= 1 ? doc.triggers?.[0] : null;
    const trigEditable = (doc.triggers || []).length <= 1 && (!trig || ['trigger.time', 'trigger.timeFixed', 'trigger.timeFlexible'].includes(trig.kind));
    const mode = trig?.kind === 'trigger.timeFixed' ? 1 : trig?.kind === 'trigger.timeFlexible' ? 2 : 0;
    const miss = (trig?.missPolicy ?? (mode === 0 ? 'nextDay' : 'skip')) === 'nextDay';
    const loopIdx = !doc.loop ? 0 : doc.loop.mode === 'scheduled' ? 1 : doc.loop.mode === 'immediate' ? 2 : 3;
    const term = doc.terminal?.[0], termIdx = !doc.terminal?.length ? 0 : term.kind === 'terminal.completionAction' && TC_TERMINAL.findIndex(x => x[0] === term.action) > 0 ? TC_TERMINAL.findIndex(x => x[0] === term.action) : 5;
    const body = `${mtField('计划名称', `<input id="tcName" maxlength="80" value="${mte(doc.name)}">`)}
      <section class="tc-sec"><h4>开始</h4>${trigEditable ? `<div class="mt-form-row">${mtField('计划开始时间（留空 = 点击后立即开始）', `<input id="tcTrigTime" type="time" value="${mte(trig?.time || trig?.at || '')}">`)}${mtField('定时类型', `<select id="tcTrigMode"><option value="0"${mode === 0 ? ' selected' : ''}>普通定时 · 到点按序执行</option><option value="1"${mode === 1 ? ' selected' : ''}>固定型 · 错过时跳过</option><option value="2"${mode === 2 ? ' selected' : ''}>灵活型 · 窗口内空闲时执行</option></select>`)}${mtField('窗口结束（灵活型）', `<input id="tcTrigUntil" type="time" value="${mte(trig?.until || '')}"${mode === 2 ? '' : ' disabled'}>`)}</div><label class="dd-check"><input id="tcMiss" type="checkbox"${miss ? ' checked' : ''}>错过开始时间后顺延到次日</label><p class="dodoco-hint">灵活型仅在窗口内空闲时启动，开始后可越过窗口完成。</p>` : `<p class="dodoco-hint">触发器 ${(doc.triggers || []).length} 个（含自定义类型），保留原样不在此编辑。</p>`}</section>
      <section class="tc-sec"><h4>循环</h4><div class="mt-form-row">${mtField('循环', `<select id="tcLoop"><option value="0">执行一轮</option><option value="1">每天到点开始下一轮</option><option value="2">完成后立即开始下一轮</option>${loopIdx === 3 ? '<option value="3">沿用已有自定义循环</option>' : ''}</select>`)}${mtField('每轮开始时间（仅每天到点循环）', `<input id="tcLoopTime" type="time" value="${mte(doc.loop?.time || '')}">`)}${mtField('计划截止时间（循环时可选）', `<input id="tcLoopDeadline" type="time" value="${mte(doc.loop?.deadline || '')}">`)}</div><label class="dd-check"><input id="tcSkipDays" type="checkbox"${doc.loop?.skipAcrossDays !== false ? ' checked' : ''}>错过本轮时间后等到次日（取消后，未到截止时间时会立即补跑本轮）</label></section>
      <section class="tc-sec"><h4>失败与收尾</h4><div class="mt-form-row">${mtField('任务失败时', `<select id="tcFail"><option value="stop">停止计划</option><option value="continue"${doc.execution?.onNodeFailure === 'continue' ? ' selected' : ''}>继续下一个任务</option></select>`)}${mtField('失败后最多重试次数（0–3）', `<input id="tcRetry" type="number" min="0" max="3" value="${mte(doc.execution?.maxNodeRetries ?? 0)}">`)}${mtField('完成后的动作', `<select id="tcTerm">${TC_TERMINAL.map(([, t], i) => `<option value="${i}">${t}</option>`).join('')}${termIdx === 5 ? '<option value="5">沿用已有自定义动作</option>' : ''}</select>`)}</div><p class="dodoco-hint">收尾动作只在计划成功完成时执行；停止、失败或结果未知不会触发。</p></section>`;
    const dialog = tcDialogShell('计划设置', '改动会自动保存；运行中的计划保持开始时的设置。', body, '<button class="dd-btn" data-cancel>取消</button><button class="dd-btn dd-btn-primary" data-ok>保存</button>');
    const q = s => dialog.querySelector(s);
    q('#tcLoop').value = String(loopIdx); q('#tcTerm').value = String(termIdx);
    const syncLoop = () => { const v = +q('#tcLoop').value; q('#tcLoopTime').disabled = v !== 1; q('#tcLoopDeadline').disabled = q('#tcSkipDays').disabled = !(v === 1 || v === 2); };
    q('#tcLoop').onchange = syncLoop; syncLoop();
    if (q('#tcTrigMode')) q('#tcTrigMode').onchange = e => { q('#tcTrigUntil').disabled = e.target.value !== '2'; if (+e.target.value !== mode) q('#tcMiss').checked = e.target.value === '0'; };
    q('[data-cancel]').onclick = () => dialog.close();
    q('[data-ok]').onclick = () => {
        try {
            const name = q('#tcName').value.trim(); if (!name) throw new Error('计划名称不能为空');
            const next = mtClone(doc); next.name = name;
            if (trigEditable) {
                const time = q('#tcTrigTime').value.trim(), m = +q('#tcTrigMode').value, until = q('#tcTrigUntil').value.trim(), nd = q('#tcMiss').checked;
                const changed = time !== (trig?.time || trig?.at || '') || m !== mode || until !== (trig?.until || '') || nd !== miss;
                if (changed) {
                    next.triggers ||= [];
                    if (!time) next.triggers = next.triggers.slice(1);
                    else {
                        if (m === 2 && (!until || until === time)) throw new Error('灵活窗口须填写不同于启动时间的结束时间');
                        const created = !next.triggers.length, t = next.triggers[0] || {};
                        t.kind = ['trigger.time', 'trigger.timeFixed', 'trigger.timeFlexible'][m]; t.time = time;
                        if (created || m !== mode || nd !== miss) t.missPolicy = nd ? 'nextDay' : 'skip';
                        if (m === 2) t.until = until;
                        if (created) next.triggers.push(t);
                    }
                }
            }
            const l = +q('#tcLoop').value;
            if (l !== 3 && l !== loopIdx) { if (l === 0) next.loop = null; else { next.loop ||= {}; next.loop.mode = l === 1 ? 'scheduled' : 'immediate'; } }
            if ((l === 1 || l === 2) && next.loop) {
                const lt = q('#tcLoopTime').value.trim(), ld = q('#tcLoopDeadline').value.trim();
                if (lt !== (doc.loop?.time || '')) next.loop.time = lt;
                if (ld !== (doc.loop?.deadline || '')) { if (ld) next.loop.deadline = ld; else delete next.loop.deadline; }
                const sk = q('#tcSkipDays').checked; if (sk !== (doc.loop?.skipAcrossDays !== false)) next.loop.skipAcrossDays = sk;
            }
            const fm = q('#tcFail').value, rt = q('#tcRetry').value.trim();
            if (fm !== (doc.execution?.onNodeFailure === 'continue' ? 'continue' : 'stop') || rt !== String(doc.execution?.maxNodeRetries ?? 0)) {
                if (!/^[0-3]$/.test(rt)) throw new Error('失败资源重试次数须为 0 至 3；未知、取消和拒绝不会自动重试');
                next.execution ||= {}; next.execution.onNodeFailure = fm; next.execution.maxNodeRetries = +rt;
            }
            const ti = +q('#tcTerm').value;
            if (ti !== 5 && ti !== termIdx) {
                if (ti === 0) next.terminal = [];
                else if (!next.terminal?.length) next.terminal = [{ kind:'terminal.completionAction', action:TC_TERMINAL[ti][0] }];
                else { next.terminal[0].kind = 'terminal.completionAction'; next.terminal[0].action = TC_TERMINAL[ti][0]; }
            }
            tc.doc = next; dialog.close(); tcEdited('计划设置已修改，正在自动保存。');
        } catch (error) { mtToast(error.message, true); }
    };
}

// ---------- 配置单内部任务（PC 的「⚙」弹窗：启用、拖动排序、执行起点；保存写入 BGI 配置并同步计划引用） ----------
async function tcInnerTasks(node, connect) {
    const dialog = tcDialogShell(`${mte(tcNodeName(node))} · ${node.kind === 'resource.singleTask' ? '所属配置任务' : '内部任务'}`, '按住任务行拖动调整顺序；勾选启用；点「起点」从该任务开始执行。每次修改保存到执行端 BGI 配置。', '<p class="dd-empty">正在读取 BGI 配置…</p>',
        '<span id="tcInnerMsg" class="tc-picked"></span><button class="dd-btn" data-reload>重新读取 BGI</button><button class="dd-btn" data-sync disabled>同步计划引用</button>');
    const state = { node, layout:null, busy:false };
    const msg = (t, err) => { const el = dialog.querySelector('#tcInnerMsg'); if (el) { el.textContent = t; el.classList.toggle('is-error', !!err); } };
    const current = () => tc.doc?.nodes.find(n => n.nodeId === node.nodeId && n.kind === node.kind && n.ref?.config === node.ref?.config);
    const draw = () => {
        const body = dialog.querySelector('.tc-dialog-body'), l = state.layout;
        const tasks = l ? mtPick(l, 'tasks') || [] : [], start = l ? mtPick(l, 'startTaskId') : null;
        body.innerHTML = !l ? '<p class="dd-empty">尚未读取到内部任务</p>' : !tasks.length ? '<p class="dd-empty">此配置没有内部任务。</p>'
            : `<div class="tc-inner">${tasks.map((t, i) => { const id = mtPick(t, 'taskId'), on = mtPick(t, 'enabled'); return `<div class="tc-inner-row${on ? '' : ' off'}" draggable="true" data-i="${i}"><span class="mt-grip">⠿</span><label class="dd-check"><input type="checkbox" data-en="${i}"${on ? ' checked' : ''}></label><span class="mt-break">${mte(mtPick(t, 'name'))}</span>${id === start ? '<i class="tc-start">起点</i>' : ''}<button class="dd-btn dd-btn-sm" data-st="${i}"${on ? '' : ' disabled'}>${id === start ? '清除起点' : '从此执行'}</button></div>`; }).join('')}</div>`;
        const stale = l && current()?.ref?.revision !== mtPick(l, 'revision');
        dialog.querySelector('[data-sync]').disabled = !stale || state.busy;
        if (stale) msg('BGI 配置已变化；可同步当前计划引用。');
        body.querySelectorAll('[data-en]').forEach(c => c.onchange = () => { const list = tasks.map(x => ({ taskId:mtPick(x, 'taskId'), name:mtPick(x, 'name'), enabled:mtPick(x, 'enabled') })); list[+c.dataset.en].enabled = c.checked; apply(list, !c.checked && list[+c.dataset.en].taskId === start ? null : start, '启用状态'); });
        body.querySelectorAll('[data-st]').forEach(b => b.onclick = () => { const t = tasks[+b.dataset.st], id = mtPick(t, 'taskId'); apply(tasks.map(x => ({ taskId:mtPick(x, 'taskId'), name:mtPick(x, 'name'), enabled:mtPick(x, 'enabled') })), id === start ? null : id, '执行起点'); });
        let drag = null;
        body.querySelectorAll('.tc-inner-row').forEach(r => {
            r.ondragstart = e => { drag = +r.dataset.i; e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', r.dataset.i); };
            r.ondragover = e => { if (drag === null) return; e.preventDefault(); r.classList.add('drop-before'); };
            r.ondragleave = () => r.classList.remove('drop-before');
            r.ondrop = e => { e.preventDefault(); const to = +r.dataset.i, from = drag; drag = null; if (from === to) return draw(); const list = tasks.map(x => ({ taskId:mtPick(x, 'taskId'), name:mtPick(x, 'name'), enabled:mtPick(x, 'enabled') })); const [m] = list.splice(from, 1); list.splice(to, 0, m); apply(list, start, '内部任务顺序'); };
        });
    };
    const read = async allowConnect => {
        if (state.busy) return; state.busy = true; msg('正在读取 BGI 配置…');
        try {
            if (!(await tcFlush())) throw new Error('执行端计划未确认保存，请先处理计划状态再读取配置');
            let reply;
            try { reply = await mtRead('task.resource.layout.get', { workflowId:tc.selected, nodeId:node.nodeId }, { timeout:30000 }); }
            catch (error) {
                if (!allowConnect) throw error;
                msg('正在启动或连接执行端 BGI，连接后将读取内部任务…');
                state.busy = false; await tcConnectCatalog(); state.busy = true;
                reply = await mtRead('task.resource.layout.get', { workflowId:tc.selected, nodeId:node.nodeId }, { timeout:30000 });
            }
            state.layout = reply.data; const n = (mtPick(state.layout, 'tasks') || []).length;
            msg(n ? `已读取 ${n} 个内部任务` : '此配置没有内部任务。');
        } catch (error) { msg(error.message + (state.layout ? '；列表保留上次显示，请重新读取。' : ''), true); }
        finally { state.busy = false; if (dialog.open) draw(); }
    };
    const apply = async (tasks, startTaskId, action) => {
        if (state.busy || !state.layout) return;
        const n = current(); if (!n) return msg('计划已切换，请关闭后重新打开设置。', true);
        state.busy = true; msg('正在保存到 BGI 配置…');
        try {
            if (!(await tcFlush())) throw new Error('执行端状态未确认，配置未发送');
            const reply = await mtOperate('task.resource.layout.save', { workflowId:tc.selected, nodeId:n.nodeId, resourceKind:n.kind, resourceConfig:n.ref?.config ?? null,
                resourceTaskId:n.ref?.taskId ?? null, current:state.layout, tasks, startTaskId }, { label:'保存配置单内部任务' });
            if (reply.status === 'unknown' || reply.status === 'accepted') throw Object.assign(new Error(reply.message || '结果未确认'), { unknown:true });
            if (reply.status !== 'ok' || !reply.revision) throw new Error(reply.message || '未保存');
            await syncRef(reply.revision);
            msg(`${action}已保存到 BGI 配置和当前计划。`);
            state.busy = false; await read(false);
        } catch (error) { msg(action + (error.unknown ? '保存结果未确认：' : '未保存：') + error.message, true); state.layout = null; draw(); }
        finally { state.busy = false; }
    };
    // 同 PC SaveResourceRevisionAfterLayoutAsync：同一配置的所有引用一起更新修订，再保存计划。
    const syncRef = async revision => {
        const n = current(); if (!n?.ref) throw new Error('计划已切换');
        const isGroup = n.kind === 'resource.configGroup';
        for (const item of tc.doc.nodes) if (item.ref?.config === n.ref.config && (isGroup ? item.kind === 'resource.configGroup' : item.kind === 'resource.oneDragonConfig' || item.kind === 'resource.singleTask')) item.ref.revision = revision;
        tc.dirty = true; if (!(await tcSave())) throw new Error('BGI 配置已保存，但计划引用未保存；请处理计划状态后重新读取引用');
        tcRenderNodes();
    };
    dialog.querySelector('[data-reload]').onclick = () => read(true);
    dialog.querySelector('[data-sync]').onclick = async () => { try { state.busy = true; const r = await mtRead('task.resource.layout.get', { workflowId:tc.selected, nodeId:node.nodeId }); state.busy = false; await syncRef(mtPick(r.data, 'revision')); msg('已同步当前计划引用。'); await read(false); } catch (e) { state.busy = false; msg('同步失败：' + e.message, true); } };
    await read(!!connect);
}

// ---------- 启动 / 运行操作 ----------
/** 同 PC：先确认保存草稿，再以计划修订启动；单任务启动 stopAfter = 入口；「连续一条龙」使用起点标记并在受理后清除。 */
async function tcStart(entryNodeId, stopAfterNodeId, fromHere) {
    const f = tcFlow(); if (!f || tc.starting) return;
    if (!tcCanStart(f)) return mtToast(`计划「${f.name}」当前不可启动：${f.quarantined ? '已隔离' : f.candidate ? '只读候选' : '含未支持类型'}`, true);
    const block = tcBlocking(f.id); if (block) return mtToast(block.st === 'Unknown' ? '这张计划有结果无法确认的旧运行，执行端禁止再次启动；请先点「重置状态」，本次没有发送。' : `计划已有运行（${block.text}）；处理后才能再次启动，本次没有发送。`, true);
    tc.starting = true; tcRenderPlanHead();
    try {
        if (!(await tcFlush())) { mtToast('执行端计划尚未确认保存，已取消启动', true); return; }
        const marker = !entryNodeId ? tcStartMarker() : null;
        const entry = entryNodeId || marker || null;
        const label = stopAfterNodeId ? '启动单任务' : fromHere ? '从此处开始执行' : '连续一条龙';
        const reply = await mtOperate('task.run.start', { workflowId:f.id, entryNodeId:entry, expectedRevision:tc.revision, stopAfterNodeId:stopAfterNodeId || null }, { label, onStage:mtStageBar, waitBudget:12000 });
        if (reply.status === 'ok' && marker) { tc.consumed.set(f.id, marker); delete tc.doc[TC_START_KEY]; tc.dirty = true; await tcSave(); }
        if (reply.status === 'ok') mtStageBar('done', (reply.message || `「${label}」已受理`) + '；运行进度以上方运行卡片为准。');
        await tcLoad(true);
    } finally { tc.starting = false; if (mt.tab === 'taskcenter') { tcRenderRuns(); tcRenderPlans(); tcRenderPlanHead(); } }
}
async function tcRunAction(runId, action, button) {
    const names = { Stop:'停止计划', SkipCurrent:'跳过当前任务', Pause:'暂停计划', ReloadDefinition:'重载计划定义' };
    if (action === 'Stop' && !confirm('停止当前运行？停止后不会执行收尾动作。')) return;
    if (button) button.disabled = true;
    // 停止权不受未确认台账阻挡（与 PC 一致），也不登记台账；其余操作走持久回执。
    const reply = await mtOperate('task.run.action', { runId, action }, { label:names[action] || action, onStage:mtStageBar, allowWhilePending:action === 'Stop' });
    if (reply.status !== 'ok' && reply.status !== 'accepted') mtToast((names[action] || action) + '：' + (reply.message || '未完成'), true);
    await tcLoad(true); if (mt.tab === 'taskcenter') { tcRenderRuns(); tcRenderPlans(); tcRenderPlanHead(); }
}
async function tcResume(runId, button) {
    if (button) button.disabled = true;
    const reply = await mtOperate('task.run.resume', runId, { label:'恢复运行', onStage:mtStageBar, waitBudget:12000 });
    if (reply.status !== 'ok') mtToast('恢复运行：' + (reply.message || '未完成'), true);
    await tcLoad(true); if (mt.tab === 'taskcenter') { tcRenderRuns(); tcRenderPlans(); tcRenderPlanHead(); }
}

// ---------- 计划管理 ----------
async function tcNewFlow() {
    if (!(await tcFlush())) return;
    const now = new Date(), pad = n => String(n).padStart(2, '0');
    // 先固定身份：丢回复后再次点击不会生成另一张计划（与 PC 监控端一致）。
    const doc = { schema:'mistletoe.workflow', schemaVersion:1, workflowId:'wf-' + mtNewId(), name:`新计划 ${pad(now.getMonth() + 1)}-${pad(now.getDate())} ${pad(now.getHours())}:${pad(now.getMinutes())}`, nodes:[], triggers:[], terminal:[] };
    const reply = await mtOperate('task.flow.save', doc, { label:'新建计划表' });
    if (reply.status !== 'ok') return mtToast('新建计划未完成：' + (reply.message || ''), true);
    await tcLoad(true); await tcOpen(doc.workflowId, true); mtToast(`已新建计划「${doc.name}」。`);
    tcRenderPlans(); tcRenderPlanHead(); tcRenderNodes();
}
function tcManage() {
    const f = tcFlow(), deleted = mtPick(tc.list, 'deleted') || [];
    const candidates = tcFlows().filter(x => x.candidate);
    const dialog = tcDialogShell('计划管理', '导入导出走这台设备的文件；执行端只接收内容，不读写你选择的路径。', `<div class="tc-menu">
        <button class="dd-btn" data-m="import">导入计划表（可多选 .json）</button><button class="dd-btn" data-m="exportOne"${f && !f.quarantined ? '' : ' disabled'}>导出当前计划表</button>
        <button class="dd-btn" data-m="exportAll"${tcFlows().some(x => !x.quarantined) ? '' : ' disabled'}>导出全部计划表</button>
        <button class="dd-btn" data-m="schedExport">导出整份调度列表（账号值已移除）</button><button class="dd-btn" data-m="schedImport">导入调度列表（作为只读候选）</button>
        <button class="dd-btn" data-m="bgiPath">配置执行端 BGI 路径…</button><button class="dd-btn" data-m="connect">连接执行端 BGI 并刷新资源目录</button>
        <button class="dd-btn" data-m="reset"${f && !f.quarantined ? '' : ' disabled'}>重置当前计划执行状态…</button>
        <button class="dd-btn dd-btn-danger" data-m="delete"${f && !f.quarantined ? '' : ' disabled'}>删除当前计划表…</button></div>
      <section class="tc-sec"><h4>恢复已删除的计划表</h4>${deleted.length ? deleted.map((d, i) => `<div class="tc-restore"><span class="mt-break">${mte(mtPick(d, 'name'))} · ${mte(String(mtPick(d, 'deletedAt') || '').replace('T', ' ').slice(5, 16))}${mtPick(d, 'canRestore') === false ? ` · 不可恢复：${mte(mtPick(d, 'recoveryProblem') || '')}` : ''}</span><button class="dd-btn dd-btn-sm" data-restore="${i}"${mtPick(d, 'canRestore') === false ? ' disabled' : ''}>恢复</button></div>`).join('') : '<p class="dodoco-hint">没有可恢复的计划表。</p>'}</section>
      <section class="tc-sec"><h4>旧版迁移</h4><p class="dodoco-hint">从执行端上的旧 BGI User 目录生成迁移候选（只读取原件）；候选激活后才会成为可执行计划。</p><div class="tc-menu"><button class="dd-btn" data-m="migPrepare">从执行端旧目录导入…</button><button class="dd-btn" data-m="migRehearsal">迁移演练</button>${candidates.map(c => `<button class="dd-btn" data-act="${mte(c.id)}">激活候选「${mte(c.name)}」</button>`).join('')}${f && !f.quarantined ? `<button class="dd-btn" data-m="migRollback">回退当前计划的手动迁移</button>` : ''}</div></section>`);
    dialog.querySelectorAll('[data-m]').forEach(b => b.onclick = async () => { dialog.close(); await tcManageAction(b.dataset.m); });
    dialog.querySelectorAll('[data-restore]').forEach(b => b.onclick = async () => { dialog.close(); const d = deleted[+b.dataset.restore]; const reply = await mtOperate('task.flow.restore', mtPick(d, 'recoveryId'), { label:`恢复计划「${mtPick(d, 'name')}」` }); mtToast(reply.message || reply.status, reply.status !== 'ok'); await tcLoad(true); tcRender(mtEl('mtContent')); });
    dialog.querySelectorAll('[data-act]').forEach(b => b.onclick = async () => { dialog.close(); await tcMigration('task.migration.activate', b.dataset.act, '激活迁移候选'); });
}
function tcFileName(name, id, used) {
    let stem = String(name || '').replace(/[\\/:*?"<>|]/g, '_').trim() || '计划表', c = stem, n = 0;
    while (used.has(c.toLowerCase())) { n++; c = `${stem}_${id.slice(-8)}${n === 1 ? '' : '_' + n}`; }
    used.add(c.toLowerCase()); return c + '.json';
}
async function tcExport(f, used = new Set()) {
    const reply = await mtRead('task.flow.export', f.id, { timeout:30000 });
    if (typeof reply.data !== 'string') throw new Error('执行端导出结果为空');
    mtDownload(reply.data, tcFileName(f.name, f.id, used));
}
async function tcManageAction(m) {
    const f = tcFlow();
    try {
        if (m === 'import') {
            const files = await new Promise(resolve => { const input = document.createElement('input'); input.type = 'file'; input.accept = '.json,application/json'; input.multiple = true; input.onchange = () => resolve([...input.files]); input.click(); });
            if (!files.length || !(await tcFlush())) return;
            for (const file of files) { const text = await mtReadFile(file); const reply = await mtOperate('task.flow.import', text, { label:`导入计划 ${file.name}`, onStage:mtStageBar, waitBudget:20000 }); mtToast(`${file.name}：${reply.message || reply.status}`, reply.status !== 'ok'); if (reply.status === 'unknown') break; }
        } else if (m === 'exportOne') { await tcExport(f); mtToast('执行端计划已导出到这台设备；账号值已移除。'); }
        else if (m === 'exportAll') { await tcLoad(true); if (tc.listError) throw new Error('读取执行端计划列表失败：' + tc.listError); const used = new Set(); let n = 0; for (const x of tcFlows().filter(x => !x.quarantined)) { await tcExport(x, used); n++; } mtToast(`已导出 ${n} 张计划表；账号值已移除。`); }
        else if (m === 'schedExport') { const reply = await mtRead('task.schedule.export', undefined, { timeout:30000 }); if (typeof reply.data !== 'string') throw new Error('执行端调度列表导出结果为空'); mtDownload(reply.data, '任务中心调度列表.json'); mtToast('执行端整份调度列表已导出到这台设备；账号值已移除。'); }
        else if (m === 'schedImport') { const file = await mtPickFile(); if (!file || !(await tcFlush())) return; const reply = await mtOperate('task.schedule.import', await mtReadFile(file), { label:'导入调度列表', onStage:mtStageBar, waitBudget:20000 }); mtToast(reply.message || reply.status, reply.status !== 'ok'); }
        else if (m === 'reset') {
            if (!confirm(`重置计划「${f.name}」的执行状态？\n\n计划内容和原失败历史会保留。将停止当前执行，并放弃已中断的旧执行；无法确认已停止的任务会保留原责任并报告失败。\n\n重置不会自动启动计划。完成后可手动从头启动，或删除计划。`)) return mtToast('已取消重置，计划与执行状态未更改。');
            tc.resetting = true; tcRenderPlanHead();
            let reply;
            try { reply = await mtOperate('task.flow.resetExecutionState', f.id, { label:'重置执行状态', onStage:mtStageBar, waitBudget:200000 }); }
            finally { tc.resetting = false; }
            // 执行端要先连上 BGI 才能核对旧执行是否还在跑（PC 同一条规则）。这次是确定未执行的拒绝，
            // 经用户确认后先连接 BGI，再用新的请求重试一次；结果未知时不重试。
            if (reply.status === 'rejected' && /BGI 当前实例和执行纪元不可确认/.test(reply.message || '')
                && confirm('重置需要先让执行端连上 BGI，核对旧执行是否还在运行。执行端 BGI 未运行时会被启动（不会开始任何任务）。现在连接 BGI 并重试重置？')) {
                if (await tcConnectCatalog()) reply = await mtOperate('task.flow.resetExecutionState', f.id, { label:'重置执行状态（连接 BGI 后）', onStage:mtStageBar, waitBudget:200000 });
            }
            mtToast(reply.status === 'ok' ? (reply.message || '执行状态已重置') : '重置未完成：' + (reply.message || reply.status), reply.status !== 'ok');
        } else if (m === 'delete') await tcDelete(f);
        else if (m === 'migPrepare') mtBrowsePrograms(async dir => { const reply = await mtOperate('task.migration.prepare', dir, { label:'准备旧版迁移', onStage:mtStageBar, waitBudget:60000 }); mtToast(reply.message || reply.status, reply.status !== 'ok'); await tcLoad(true); tcRender(mtEl('mtContent')); }, '', '选择执行端上的旧 BGI User 目录', () => false, true);
        else if (m === 'connect') await tcConnectCatalog();
        else if (m === 'bgiPath') await tcBgiPath();
        else if (m === 'migRehearsal') await tcMigration('task.migration.rehearsal', undefined, '迁移演练');
        else if (m === 'migRollback') { if (confirm(`回退计划「${f.name}」的手动迁移？`)) await tcMigration('task.migration.rollback', f.id, '回退手动迁移'); }
    } catch (error) { mtToast(error.message, true); }
    await tcLoad(true); if (mt.tab === 'taskcenter') tcRender(mtEl('mtContent'));
}
async function tcMigration(op, payload, label) {
    const reply = await mtOperate(op, payload, { label, onStage:mtStageBar, waitBudget:60000 });
    mtToast(`${label}：${reply.message || reply.status}`, reply.status !== 'ok');
    await tcLoad(true); if (mt.tab === 'taskcenter') tcRender(mtEl('mtContent'));
}
async function tcDelete(f) {
    if (!(await tcFlush()) && !confirm('当前计划有未保存的修改，删除会一并放弃。是否继续？')) return;
    if (!confirm(`从执行端删除计划「${f.name}」？原计划会保留在可恢复记录中。`)) return;
    let reply = await mtOperate('task.flow.delete', { workflowId:f.id, acknowledgeUnknownRuns:false }, { expectedRevision:f.revision, label:`删除计划「${f.name}」` });
    if (reply.status === 'rejected' && tcActive().some(r => mtPick(r, 'workflowId') === f.id && tcState(r) === 'Unknown')
        && confirm('该计划有结果未知的旧运行。继续只删除计划定义并保留 Unknown 运行历史；不会把未知判成成功。是否解除占用？'))
        reply = await mtOperate('task.flow.delete', { workflowId:f.id, acknowledgeUnknownRuns:true }, { expectedRevision:f.revision, label:`删除计划「${f.name}」` });
    mtToast(reply.message || reply.status, reply.status !== 'ok');
    if (reply.status === 'ok') { tc.selected = ''; tc.doc = null; tc.dirty = false; }
}

// ---------- 运行记录 ----------
const TC_OUTCOME = { succeeded:'已完成', skippedUser:'显式跳过', skippedFilter:'过滤跳过', failed:'失败', rejected:'已拒绝', cancelled:'已取消', cancelUnconfirmed:'停止结果待确认', unknown:'待确认' };
function tcRenderHistory(host) {
    if (!host) return;
    if (mt.identity && !mt.identity.isExecutor) { host.innerHTML = '<section class="dd-card dd-empty">该助手是监控端：运行记录属于执行端任务中心。</section>'; return; }
    if (!tc.list) { host.innerHTML = '<section class="dd-card dd-empty">正在读取执行端运行记录…</section>'; if (!tc.loading) tcLoad(true).then(() => mt.tab === 'history' && tcRenderHistory(mtEl('mtContent'))); return; }
    // 重绘时保留已展开的节点明细。
    const open = new Set([...host.querySelectorAll('.tc-hist details[open]')].map(d => d.dataset.run));
    const names = Object.fromEntries(tcFlows().map(f => [f.id, f.name]));
    const filter = tc.historyFilter || '';
    const runs = [...tcActive().map(r => ({ r, live:true })), ...(mtPick(tc.list, 'history') || []).map(r => ({ r, live:false }))]
        .filter(x => !filter || mtPick(x.r, 'workflowId') === filter)
        .sort((a, b) => String(mtPick(b.r, 'updatedAt')).localeCompare(String(mtPick(a.r, 'updatedAt'))));
    host.innerHTML = `<section class="dd-card"><header class="dd-card-head"><div><h2>运行记录</h2><p class="dodoco-hint">执行端任务中心的运行与历史；结果未知的运行保留原样，不会被当作成功。每 3 秒刷新。</p></div><label class="dd-field"><span>计划</span><select id="tcHistFilter"><option value="">全部计划</option>${tcFlows().map(f => `<option value="${mte(f.id)}"${f.id === filter ? ' selected' : ''}>${mte(f.name)}</option>`).join('')}</select></label></header>
      <div class="dd-record-list">${runs.length ? runs.map(({ r, live }) => {
        const st = tcState(r), outs = r.nodeOutcomes || [], ok = outs.filter(o => ['succeeded', 'skippedUser', 'skippedFilter'].includes(o.result)).length;
        const fail = outs.find(o => o.result === 'failed' || o.result === 'rejected'), released = tcReleased(r);
        const tone = released ? '' : st === 'Succeeded' ? 'ok' : ['Failed', 'Unknown', 'Interrupted', 'Cancelled'].includes(st) ? 'warn' : '';
        return `<article class="dd-record tc-hist lv-${st === 'Failed' || st === 'Unknown' ? 'err' : st === 'Succeeded' ? 'inf' : 'wrn'}"><header><time>${mte(String(mtPick(r, 'updatedAt') || '').replace('T', ' ').slice(0, 19))}</time><span class="dd-pill ${tone}">${released ? `已重置执行状态（原状态：${mte(TC_STATE_TEXT[st] || st)}）` : mte(TC_STATE_TEXT[st] || st)}${live ? ' · 进行中' : ''}</span><strong class="mt-break">${mte(names[mtPick(r, 'workflowId')] || mtPick(r, 'workflowId'))}</strong><small>运行 ${mte(String(mtPick(r, 'runId')).slice(0, 8))}</small></header>
          <p>${ok}/${outs.length} 节点成功${fail ? ` · ${mte(fail.nodeId)} ${mte(TC_OUTCOME[fail.result] || fail.result)}：${mte(fail.reason || '')}` : ''}</p>${r.note ? `<p class="mt-break">${mte(r.note)}</p>` : ''}
          ${outs.length ? `<details data-run="${mte(mtPick(r, 'runId'))}"${open.has(String(mtPick(r, 'runId'))) ? ' open' : ''}><summary>节点明细（${outs.length}${outs.length > 60 ? '，显示最近 60 条' : ''}）</summary><ol class="tc-outcomes">${outs.slice(-60).map(o => `<li><b>${mte(o.nodeId)}</b> · 第 ${(o.loopIteration || 0) + 1} 轮 · ${mte(TC_OUTCOME[o.result] || o.result)}${o.reason ? ' · ' + mte(o.reason) : ''}</li>`).join('')}</ol></details>` : ''}</article>`;
      }).join('') : '<p class="dd-empty">还没有运行记录</p>'}</div></section>`;
    mtEl('tcHistFilter').onchange = e => { tc.historyFilter = e.target.value; tcRenderHistory(host); };
}
/** 同 PC「配置 BGI」：读取执行端 BetterGI.exe 路径和修订，浏览执行端目录选择后带修订保存。 */
async function tcBgiPath() {
    const reply = await mtRead('task.bgiPath.get');
    const current = mtPick(reply.data, 'path') || '', revision = reply.revision;
    if (!revision) throw new Error('执行端 BGI 路径读取失败：缺少修订');
    const start = current ? current.replace(/[\\/][^\\/]*$/, '') : '';
    mtBrowsePrograms(async path => {
        const saved = await mtOperate('task.bgiPath.save', path, { expectedRevision:revision, label:'保存执行端 BGI 路径' });
        mtToast(saved.message || saved.status, saved.status !== 'ok');
    }, start, `选择执行端 BetterGI.exe（当前：${current || '未设置'}）`, x => /(^|[\\/])BetterGI\.exe$/i.test(x));
}
