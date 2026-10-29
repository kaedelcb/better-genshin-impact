// 槲寄生 · 启动中心（方向 B：左侧大纲负责编辑与排序，右侧画布显示与 PC 流程图同构的整图）。
// 结构规则与 PC 一致：条件节点分「是/否」两条子链，跑完回到父链；定时/电子狗/日志触发器挂「触发执行」链；
// 「结束流程」必须是所在链的最后一个节点；全树只能有一个「进入任务中心执行」；节点不能移进自己的分支。
const MT_KINDS = [
    ['timeRange','condition','时间区间','◷','#E8C96D'], ['weekday','condition','星期判断','▦','#E8C96D'],
    ['bgiRunning','condition','BGI 进程状态','◇','#6FD8CE'], ['gameRunning','condition','游戏进程状态','◇','#6FD8CE'],
    ['processRunning','condition','指定进程状态','◇','#6FD8CE'], ['bgiTaskRunning','condition','BGI 任务状态','◇','#6FD8CE'],
    ['bgiTaskName','condition','BGI 当前任务名','◇','#6FD8CE'], ['manualConfirm','condition','人工确认','?','#B39DDB'],
    ['startBgi','action','启动 BGI','▶','#48C87E'], ['stopBgi','action','关闭 BGI','■','#EF5350'],
    ['startGame','action','启动游戏','▶','#48C87E'], ['startProgram','action','启动第三方程序','▶','#64B5F6'],
    ['killProgram','action','关闭程序','■','#EF5350'], ['runCmd','action','执行 CMD 命令','›_','#9575CD'],
    ['wait','action','等待','⧗','#9C97C0'], ['enterTaskCenter','action','进入任务中心执行','➤','#E8C96D'],
    ['timerTrigger','trigger','定时触发器','⏰','#FFC857'], ['watchdog','trigger','电子狗','🐕','#E88A6F'],
    ['logTrigger','trigger','日志触发器','📜','#4FC3F7'], ['endFlow','action','结束流程','■','#EF5350'],
    ['startGroup','legacy','旧版 · 启动配置组','▶','#90A4AE'], ['startOneClick','legacy','旧版 · 启动一条龙','▶','#90A4AE']
];
const MT_KIND = Object.fromEntries(MT_KINDS.map(([key,type,name,icon,color]) => [key,{key,type,name,icon,color}]));
const MT_COND = new Set(MT_KINDS.filter(k => k[1] === 'condition').map(k => k[0]));
const MT_FIRE = new Set(['timerTrigger','watchdog','logTrigger']);
const MT_EMPH = new Set(['endFlow','enterTaskCenter','timerTrigger','watchdog','logTrigger']);
const MT_WATCHABLE = ['bgiRunning','gameRunning','processRunning','bgiTaskRunning','bgiTaskName'];
const MT_RUN = ['', 'running', 'success', 'failed', 'skipped', 'condTrue', 'condFalse'];
const MT_RUN_TEXT = { running:'执行中', success:'已执行', failed:'失败', skipped:'已跳过', condTrue:'走「是」', condFalse:'走「否」' };
const MT_WEEK = ['一','二','三','四','五','六','日'];

const su = { config:null, revision:'', schemes:[], schemesRevision:'', isRunning:false, statusText:'', autoLaunch:null,
    triggers:[], prompts:[], states:new Map(), selected:null, dirty:false, saving:false, saveError:'', saveTimer:null,
    loaded:false, loading:false, view:{ zoom:1, x:0, y:0, mode:'fit', follow:true }, inspectorOpen:true, mobilePane:'outline',
    flows:[], schemeSaving:false, addTarget:null };

// ---------- 模型工具 ----------
function mtNewStep(kind) {
    const info = MT_KIND[kind];
    const s = { id:mtNewId(), nodeType:info.type === 'condition' ? 'condition' : 'action', kind, name:'', enabled:true,
        trueSteps:[], falseSteps:[], fireSteps:[], expectRunning:true, processName:'', timeStart:'00:00', timeEnd:'23:59', weekdays:[],
        confirmMessage:'', confirmTimeoutSeconds:60, confirmTimeoutGoTrue:false, path:'', arguments:'', windowsUserName:'', waitSeconds:10,
        triggerTime:'08:00', repeatDaily:false, watchKind:'bgiTaskRunning', watchIntervalSeconds:30, watchRepeat:true,
        watchConfirmSeconds:1, watchConfirmTimes:1, killBeforeStart:false, statusSource:'currentSession', statusTargetOrder:1,
        statusTargetUser:'', taskName:'', logKeyword:'', taskCenterFlowId:'', taskCenterHandoffMode:'start', continueAfterTaskCenterHandoff:false };
    if (kind === 'weekday') s.weekdays = [1,2,3,4,5,6,7];
    if (kind === 'processRunning') s.processName = 'notepad';
    if (kind === 'runCmd') s.arguments = 'echo hello';
    return s;
}
function mtChains(step) { return MT_COND.has(step.kind) ? [['trueSteps','是'],['falseSteps','否']] : MT_FIRE.has(step.kind) ? [['fireSteps', step.kind === 'timerTrigger' ? '到点执行' : '触发执行']] : []; }
/** 深度遍历：回调 (step, chain, index, parent, parentKey)。 */
function mtWalk(chain, fn, parent = null, key = 'steps') {
    (chain || []).forEach((step, index) => { fn(step, chain, index, parent, key); for (const [k] of mtChains(step)) mtWalk(step[k] || (step[k] = []), fn, step, k); });
}
function mtFind(id) { let hit = null; mtWalk(su.config?.steps, (s, chain, index, parent, key) => { if (!hit && s.id === id) hit = { step:s, chain, index, parent, key }; }); return hit; }
function mtChainLabel(parent, key) {
    if (!parent) return '主流程';
    // 与 PC StepChainViewModel.BranchName 同一口径。
    return `「${mtDisplayName(parent)}」的${key === 'trueSteps' ? '是分支' : key === 'falseSteps' ? '否分支' : parent.kind === 'timerTrigger' ? '到点执行链' : '触发执行链'}`;
}
function mtDisplayName(s) { return (s.name || '').trim() || MT_KIND[s.kind]?.name || s.kind || '未知节点'; }
function mtWatchDesc(s) {
    const on = s.expectRunning;
    return ({ bgiRunning:`BGI 进程${on?'在跑':'不在'}`, gameRunning:`游戏进程${on?'在跑':'不在'}`, processRunning:`进程 ${s.processName} ${on?'存在':'不存在'}`,
        bgiTaskRunning:on?'BGI 有任务在跑':'BGI 空闲', bgiTaskName:`当前任务名${on?'包含':'不包含'}「${s.taskName}」` })[s.watchKind] || s.watchKind;
}
/** 与 PC StartupStepViewModel.BuildSummary 同一口径。 */
function mtSummary(s) {
    const clamp = (v, a, b) => Math.min(b, Math.max(a, Number(v) || 0));
    switch (s.kind) {
        case 'timeRange': return `${s.timeStart} ~ ${s.timeEnd}`;
        case 'weekday': return (s.weekdays || []).length === 7 ? '每天' : !(s.weekdays || []).length ? '（未勾选任何星期）' : '周' + [...s.weekdays].sort().map(d => MT_WEEK[d - 1]).join('、');
        case 'bgiRunning': return s.expectRunning ? 'BGI 正在运行 → 是' : 'BGI 未运行 → 是';
        case 'gameRunning': return s.expectRunning ? '游戏正在运行 → 是' : '游戏未运行 → 是';
        case 'processRunning': return `${s.processName} ${s.expectRunning ? '存在' : '不存在'} → 是`;
        case 'bgiTaskRunning': return s.expectRunning ? 'BGI 有任务在跑 → 是' : 'BGI 空闲 → 是';
        case 'bgiTaskName': return (s.taskName || '').trim() ? `当前任务名${s.expectRunning ? '包含' : '不包含'}「${s.taskName}」→ 是` : '（未填写任务名）';
        case 'manualConfirm': return ((s.confirmMessage || '').trim() || '（未填提示内容）') + (s.confirmTimeoutSeconds > 0 ? `；${s.confirmTimeoutSeconds} 秒超时走「${s.confirmTimeoutGoTrue ? '是' : '否'}」` : '；不限时');
        case 'startBgi': return ((s.arguments || '').trim() ? `启动本机 BGI（参数：${s.arguments}）` : '启动本机 BGI') + (s.killBeforeStart ? '，先关闭再启动' : '');
        case 'stopBgi': return '强制结束本会话 BGI 进程';
        case 'startGame': case 'startProgram': return ((s.path || '').trim() || '（未填写程序路径）') + (s.kind === 'startProgram' && (s.windowsUserName || '').trim() ? `（Windows 用户：${s.windowsUserName.trim()}）` : '');
        case 'runCmd': return (s.arguments || '').trim() || '（未填写命令）';
        case 'killProgram': return (s.processName || '').trim() ? `结束进程 ${s.processName}` : '（未填写进程名）';
        case 'wait': return `等待 ${s.waitSeconds} 秒`;
        case 'timerTrigger': return `${s.triggerTime} 触发「到点执行」链（${(s.fireSteps || []).length} 个节点${s.repeatDaily ? '，每天重复' : ''}）`;
        case 'watchdog': return !(s.fireSteps || []).length ? `每 ${Math.max(1, s.watchIntervalSeconds)}s 盯「${mtWatchDesc(s)}」，仅显示状态（无触发动作，持续盯梢）`
            : `每 ${Math.max(1, s.watchIntervalSeconds)}s 盯「${mtWatchDesc(s)}」，成立执行 ${s.fireSteps.length} 个节点（${s.watchRepeat ? '触发后继续循环' : '触发后停止'}，复核 ${clamp(s.watchConfirmSeconds,1,60)}s×${clamp(s.watchConfirmTimes,1,10)}）`;
        case 'logTrigger': return (s.logKeyword || '').trim() ? `日志出现「${s.logKeyword}」时执行 ${(s.fireSteps || []).length} 个节点（${s.watchRepeat ? '循环触发' : '触发后停止'}）` : '（未填写日志关键字）';
        case 'enterTaskCenter': {
            const mode = { start:'立即执行', resume:'恢复既有运行', armTrigger:'挂载触发器' }[s.taskCenterHandoffMode] || `未知语义「${s.taskCenterHandoffMode}」（运行时将响亮拒绝）`;
            if (!(s.taskCenterFlowId || '').trim()) return '移交任务中心（未配置目标流程）';
            const flow = su.flows.find(f => f.id === s.taskCenterFlowId);
            return `移交任务中心：${flow ? flow.name : s.taskCenterFlowId}（${mode}）`;
        }
        case 'endFlow': return '立即终止整条启动流程';
        case 'startGroup': return (s.taskName || '').trim() ? `（旧版节点）配置组「${s.taskName}」` : '（旧版节点 · 未填写配置组名）';
        case 'startOneClick': return (s.taskName || '').trim() ? `（旧版节点）一条龙「${s.taskName}」` : '（旧版节点 · 未填写一条龙名）';
        default: return '';
    }
}
function mtNodeState(s) { return su.states.get(s.id)?.state || ''; }
function mtTaken(s) { const st = mtNodeState(s); return st === 'condTrue' ? 'trueSteps' : st === 'condFalse' ? 'falseSteps' : ''; }

// ---------- 编辑操作（规则同 PC） ----------
function mtChainHasEnd(chain) { return (chain || []).some(s => s.kind === 'endFlow'); }
function mtCountKind(kind) { let n = 0; mtWalk(su.config.steps, s => { if (s.kind === kind) n++; }); return n; }
function mtAddStep(kind, chain, label) {
    if (mtChainHasEnd(chain)) return mtToast(`「${label}」已有「结束流程」节点，它是终点，之后不能再添加节点`, true);
    if (kind === 'enterTaskCenter' && mtCountKind('enterTaskCenter')) return mtToast('启动流程中只能有一个「进入任务中心执行」节点；如需调整位置，请删除后重新添加', true);
    const step = mtNewStep(kind);
    chain.push(step); su.selected = step.id; su.inspectorOpen = true;
    mtEdited(`已在「${label}」添加节点「${mtDisplayName(step)}」`);
}
function mtCountDeep(chain) { let n = 0; mtWalk(chain, () => n++); return n; }
function mtRemoveStep(id) {
    const hit = mtFind(id); if (!hit) return;
    const nested = mtChains(hit.step).reduce((n, [k]) => n + mtCountDeep(hit.step[k]), 0);
    if (nested && !confirm(`删除「${mtDisplayName(hit.step)}」会同时删除它下面的 ${nested} 个节点，是否继续？`)) return;
    hit.chain.splice(hit.index, 1); su.selected = null;
    mtEdited(`已删除节点「${mtDisplayName(hit.step)}」`);
}
function mtMoveStep(id, delta) {
    const hit = mtFind(id); if (!hit) return;
    const target = hit.index + delta;
    if (target < 0 || target >= hit.chain.length) return;
    if (hit.step.kind === 'endFlow' || hit.chain[target].kind === 'endFlow') return mtToast('「结束流程」必须是链的最后一个节点，不能这样移动', true);
    hit.chain.splice(hit.index, 1); hit.chain.splice(target, 0, hit.step);
    mtEdited();
}
/** 跨链拖拽：含环检测；结束流程只能落在链尾；普通节点不能落在结束流程之后。 */
function mtMoveStepTo(id, targetChain, insertIndex, targetLabel) {
    const hit = mtFind(id); if (!hit) return;
    const contains = step => mtChains(step).some(([k]) => step[k] === targetChain || (step[k] || []).some(contains));
    if (contains(hit.step)) return mtToast(`不能把「${mtDisplayName(hit.step)}」拖进它自己的分支里`, true);
    hit.chain.splice(hit.index, 1);
    if (hit.chain === targetChain && hit.index < insertIndex) insertIndex--;
    if (hit.step.kind === 'endFlow') insertIndex = targetChain.length;
    else {
        insertIndex = Math.max(0, Math.min(insertIndex, targetChain.length));
        const end = targetChain.findIndex(s => s.kind === 'endFlow');
        if (end >= 0 && insertIndex > end) { insertIndex = end; mtToast(`「${targetLabel}」以「结束流程」收尾，节点已自动放到它前面`); }
    }
    targetChain.splice(insertIndex, 0, hit.step);
    mtEdited(`节点「${mtDisplayName(hit.step)}」已移动到「${targetLabel}」第 ${insertIndex + 1} 位`);
}
/** light=true：节点设置面板里改字段，只刷新大纲/画布/保存状态，保留正在输入的表单焦点。 */
function mtEdited(message, light) {
    su.dirty = true; su.saveError = '';
    if (message) mtToast(message);
    clearTimeout(su.saveTimer); su.saveTimer = setTimeout(mtSaveConfig, 1200);
    if (!mtEl('mtOutline')) { mtRenderStartupBody(); return; }
    // 只重绘编辑区：运行卡片、方案栏的输入状态和大纲滚动位置都保留。
    mtRenderOutline(); mtRenderCanvas(); mtRenderSaveState();
    if (!light) { mtRenderInspector(); mtEl('mtEditor')?.classList.toggle('has-selection', !!su.selected); if (su.selected) mtRevealRow(su.selected); }
}

// ---------- 与执行端同步 ----------
async function mtLoadStartup(force) {
    if (su.loading) return;
    if (su.dirty && !force) return;
    su.loading = true; mtRenderStartupBody();
    try {
        const reply = await mtRead('startup.get');
        const d = reply.data || {};
        su.config = mtPick(d, 'config') || { enabled:false, delaySeconds:5, steps:[], autoExpandBranch:true };
        su.config.steps = su.config.steps || [];
        su.revision = mtPick(d, 'revision') || reply.revision || '';
        su.schemes = mtPick(d, 'schemes') || []; su.schemesRevision = mtPick(d, 'schemesRevision') || '';
        su.autoLaunch = { enabled:!!mtPick(d, 'autoLaunchOnBoot'), mode:mtPick(d, 'autoLaunchOnBootModeIndex') || 0, revision:mtPick(d, 'autoLaunchRevision') || '' };
        mtApplyStatus(d);
        su.dirty = false; su.saveError = ''; su.loaded = true; su.statusSig = ''; mtViewReset();
        if (su.selected && !mtFind(su.selected)) su.selected = null;
        mtToast('已读取执行端启动中心。');
    } catch (error) { mtToast('启动中心读取失败：' + error.message, true); }
    finally { su.loading = false; mtRenderStartupBody(); }
    if (mt.identity?.isExecutor) mtLoadFlowChoices();
}
function mtApplyStatus(d) {
    su.isRunning = !!mtPick(d, 'isRunning'); su.statusText = mtPick(d, 'statusText') || '';
    su.triggers = mtPick(d, 'triggers') || []; su.prompts = mtPick(d, 'prompts') || [];
    su.states = new Map((mtPick(d, 'nodeStates') || []).map(x => [mtPick(x, 'stepId'), { state:MT_RUN[mtPick(x, 'state')] || '', note:mtPick(x, 'note') || '' }]));
}
async function mtLoadFlowChoices() {
    try {
        const reply = await mtRead('task.list');
        su.flows = (mtPick(reply.data, 'flows') || []).map(f => ({ id:mtPick(f, 'workflowId'), name:mtPick(f, 'name'), status:mtPick(f, 'status') }));
        // 只影响「进入任务中心执行」的目标名称显示。
        if (mt.tab === 'startup' && mtEl('mtOutline')) { mtRenderOutline(); mtRenderCanvas(); mtRenderInspector(); }
    } catch { /* 目标流程下拉退化为显示原身份 */ }
}
async function mtPollStartup() {
    if (!mt.active || mt.tab !== 'startup' || !su.loaded || mt.busy || !mtTargetReady() || !mtOwner()) return;
    try {
        const reply = await mtRead('startup.status', undefined, { timeout:12000 });
        if (mt.unconfirmed) await mtReconcile(true);
        // 状态没变就不重绘，避免每 3 秒替换大纲和画布打断点击、拖动。
        const sig = JSON.stringify(reply.data || {});
        if (sig === su.statusSig && mtEl('mtRunCard')) return;
        su.statusSig = sig;
        mtApplyStatus(reply.data || {});
        mtRenderStartupLive();
    } catch (error) { mtToast('运行状态刷新失败：' + error.message, true); }
}
async function mtSaveConfig() {
    clearTimeout(su.saveTimer);
    if (!su.dirty || su.saving || !su.config) return;
    su.saving = true; mtRenderSaveState();
    const snapshot = JSON.stringify(su.config);
    const reply = await mtOperate('startup.save', mtClone(su.config), { expectedRevision:() => su.revision, label:'保存启动流程', proceedAfterCheck:true });
    su.saving = false;
    if (reply.status === 'ok') {
        su.revision = reply.revision || su.revision;
        su.dirty = JSON.stringify(su.config) !== snapshot;
        if (su.dirty) su.saveTimer = setTimeout(mtSaveConfig, 600);
        mtToast('已保存到执行端。');
    } else {
        su.saveError = reply.message || '保存未完成';
        mtToast((reply.status === 'unknown' ? '保存结果未确认，草稿保留：' : reply.status === 'blocked' ? '草稿保留，' : '保存未完成，草稿保留：') + su.saveError, true);
    }
    mtRenderSaveState();
}
async function mtSaveSchemes(next, label) {
    if (su.schemeSaving) return false;
    su.schemeSaving = true;
    const reply = await mtOperate('startup.schemes.save', next, { expectedRevision:su.schemesRevision, label });
    su.schemeSaving = false;
    if (reply.status === 'ok') { su.schemes = next; su.schemesRevision = reply.revision || su.schemesRevision; mtToast(label + '已保存到执行端方案库。'); mtRenderSchemes(); return true; }
    mtToast(reply.status === 'blocked' ? reply.message : label + (reply.status === 'unknown' ? '结果未确认：' : '未完成：') + (reply.message || ''), true);
    return false;
}

// ---------- 方案（同 PC：同名覆盖；恢复前自动备份；导入只入方案库并自动改名避重） ----------
const MT_BACKUP = '恢复前自动备份';
function mtNowText() { return new Date(Date.now() - new Date().getTimezoneOffset() * 60000).toISOString().slice(0, 19); }
async function mtSchemeSave() {
    const name = (mtEl('mtSchemeName')?.value || '').trim();
    if (!name) return mtToast('请先给方案起个名字再保存', true);
    if (su.dirty) await mtSaveConfig();
    const next = mtClone(su.schemes), existing = next.find(s => s.name === name);
    if (existing) { existing.savedAt = mtNowText(); existing.config = mtClone(su.config); }
    else next.unshift({ name, savedAt:mtNowText(), config:mtClone(su.config) });
    await mtSaveSchemes(next, existing ? `方案「${name}」覆盖更新` : `方案「${name}」`);
}
async function mtSchemeRestore() {
    const name = mtEl('mtSchemeSelect')?.value; const scheme = su.schemes.find(s => s.name === name);
    if (!scheme) return mtToast('请先选择一个方案', true);
    if (su.isRunning) return mtToast('流程正在执行中，请先取消或等执行完再恢复方案', true);
    if (!confirm(`恢复方案「${name}」会替换当前启动流程；当前配置会先自动备份为「${MT_BACKUP}」。是否继续？`)) return;
    const next = mtClone(su.schemes), backup = next.find(s => s.name === MT_BACKUP);
    if (backup) { backup.savedAt = mtNowText(); backup.config = mtClone(su.config); }
    else next.push({ name:MT_BACKUP, savedAt:mtNowText(), config:mtClone(su.config) });
    if (!await mtSaveSchemes(next, '恢复前备份')) return;
    const restored = mtClone(scheme.config); restored.steps = restored.steps || [];
    su.config = restored; su.selected = null; su.dirty = true; mtViewReset();
    await mtSaveConfig(); mtRenderStartupBody();
}
async function mtSchemeDelete() {
    const name = mtEl('mtSchemeSelect')?.value; if (!name) return mtToast('请先选择一个方案', true);
    if (!confirm(`删除方案「${name}」？`)) return;
    await mtSaveSchemes(su.schemes.filter(s => s.name !== name), `删除方案「${name}」`);
}
function mtSchemeExport() {
    const name = mtEl('mtSchemeSelect')?.value; const scheme = su.schemes.find(s => s.name === name);
    if (!scheme) return mtToast('请先选择一个方案', true);
    mtDownload(JSON.stringify(scheme, null, 2), `${name}.json`); mtToast(`已导出方案「${name}」到这台设备。`);
}
async function mtSchemeImport() {
    try {
        const text = (await mtReadFile(await mtPickFile())).trim(); if (!text) return mtToast('文件里没有可用的方案', true);
        const parsed = JSON.parse(text), list = (Array.isArray(parsed) ? parsed : [parsed]).filter(x => x && typeof x === 'object' && x.config);
        if (!list.length) return mtToast('导入失败：文件里没有可用的方案（不是启动中心方案文件）', true);
        const now = new Date(), pad = n => String(n).padStart(2, '0');
        const stamp = `${pad(now.getMonth() + 1)}${pad(now.getDate())} ${pad(now.getHours())}${pad(now.getMinutes())}`;
        const names = su.schemes.map(s => s.name), next = mtClone(su.schemes);
        for (let i = list.length - 1; i >= 0; i--) {
            const s = list[i]; const base = (s.name || '').trim() || `导入方案 ${stamp}`; let name = base;
            if (names.includes(name)) { name = `${base}-导入${stamp}`; let n = 2; while (names.includes(name)) name = `${base}-导入${stamp}(${n++})`; }
            names.push(name); next.unshift({ ...s, name, config:{ ...s.config, steps:s.config.steps || [] } });
        }
        await mtSaveSchemes(next, `导入 ${list.length} 份方案`);
    } catch (error) { mtToast('方案导入失败（文件损坏或不是启动中心方案文件）：' + error.message, true); }
}

// ---------- 运行 ----------
async function mtRunStartup() {
    if (su.isRunning) return mtToast('启动流程已在运行', true);
    if (su.dirty) { await mtSaveConfig(); if (su.dirty) return mtToast('启动流程尚未确认保存，已取消执行', true); }
    mt.busy = true; mtRenderStartupRun();
    const reply = await mtOperate('startup.run', undefined, { label:'立即执行启动流程', onStage:mtStageBar, waitBudget:4000 });
    mt.busy = false;
    if (reply.status === 'accepted') mtStageBar('running', '启动流程已受理并在执行端运行；节点状态会实时点亮，终态以执行端为准。');
    await mtPollStartup(); mtRenderStartupRun();
}
async function mtCancelStartup() {
    const reply = await mtOperate('startup.cancel', undefined, { label:'停止启动流程', allowWhilePending:true, onStage:mtStageBar });
    if (reply.status !== 'accepted' && reply.status !== 'ok') mtToast('停止请求未确认：' + (reply.message || ''), true);
    setTimeout(mtPollStartup, 800);
}
async function mtAnswerPrompt(runtimeId, yes) {
    const reply = await mtOperate('startup.prompt.answer', { runtimeId, yes }, { label:`人工确认选择「${yes ? '是' : '否'}」`, onStage:mtStageBar });
    if (reply.status !== 'ok') mtToast(reply.message || '人工确认未送达', true);
    mtPollStartup();
}
async function mtCancelTrigger(runtimeId, text) {
    if (!confirm(`取消触发器「${text}」？不影响流程里的其他节点。`)) return;
    const reply = await mtOperate('startup.trigger.cancel', runtimeId, { label:'取消触发器', onStage:mtStageBar });
    if (reply.status !== 'ok') mtToast(reply.message || '取消未完成', true);
    mtPollStartup();
}
async function mtClearRunStates() {
    const reply = await mtOperate('startup.runStates.clear', undefined, { label:'清除路径显示' });
    if (reply.status === 'ok') { su.states.clear(); su.statusSig = ''; mtRenderStartupLive(); mtRenderInspector(); } else mtToast(reply.message || '清除未完成', true);
}
/** 开机自启动：与 PC 监控端相同，携带读取时的修订；远程不能代替执行端账户确认 UAC，无权限时执行端明确返回失败。 */
async function mtSaveAutoLaunch(enabled, mode) {
    su.autoLaunchSaving = true;
    const reply = await mtOperate('startup.autoLaunch.save', { enabled, modeIndex:mode }, { expectedRevision:su.autoLaunch?.revision, label:'修改开机自启动' });
    su.autoLaunchSaving = false;
    if (reply.status === 'ok') { su.autoLaunch = { enabled, mode, revision:reply.revision || su.autoLaunch.revision }; mtToast('执行端开机自启动设置已应用。'); }
    else mtToast('开机自启动' + (reply.status === 'unknown' ? '结果未确认：' : '未修改：') + (reply.message || '') + '；请读取最新启动设置核对。', true);
    mtRenderStartupRun(true);
}
