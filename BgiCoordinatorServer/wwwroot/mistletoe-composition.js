// 任务中心编排编辑：只提交作者结构；执行端编译路径并拥有监听、暂停及续接责任。
const TC_FLOW_KINDS = ['wait','setRegister','startProgram','timeRange','weekday','bgiRunning','gameRunning','processRunning','bgiTaskRunning','bgiTaskName','judgeRegister','manualConfirm','timerTrigger','watchdog','logTrigger'];
const TC_FLOW_LABELS = { wait:'等待', setRegister:'设置寄存器', startProgram:'启动辅助程序', timeRange:'时间区间', weekday:'星期判断', bgiRunning:'BGI 进程状态', gameRunning:'游戏进程状态', processRunning:'指定进程状态', bgiTaskRunning:'BGI 任务状态', bgiTaskName:'BGI 当前任务名', judgeRegister:'判断寄存器', manualConfirm:'人工确认', timerTrigger:'定时触发器', watchdog:'电子狗', logTrigger:'日志触发器' };
const TC_FLOW_CONDITIONS = new Set(['timeRange','weekday','bgiRunning','gameRunning','processRunning','bgiTaskRunning','bgiTaskName','judgeRegister','manualConfirm']);
const TC_FLOW_TRIGGERS = new Set(['timerTrigger','watchdog','logTrigger']);
function tcIsComposition(n) { return n?.kind === 'control.flow' || n?.kind === 'control.lanes'; }
function tcMainNodes(doc = tc.doc) {
    const all = doc?.nodes || [], hidden = new Set();
    for (const n of all) if (n.lanes) { for (const id of [...(n.lanes.yesNodeIds || []), ...(n.lanes.noNodeIds || [])]) hidden.add(id); if (!n.lanes.decision && n.lanes.sourceFlowNodeId) hidden.add(n.lanes.sourceFlowNodeId); }
    return all.filter(n => (tcIsComposition(n) || String(n.kind).startsWith('resource.')) && !hidden.has(n.nodeId));
}
function tcLaneOwner(id, doc = tc.doc) {
    for (const n of doc?.nodes || []) if (n.lanes) for (const key of ['yesNodeIds','noNodeIds']) if ((n.lanes[key] || []).includes(id)) return { node:n, key, ids:n.lanes[key] };
    return null;
}
function tcCompositionSiblings(n) { const owner = tcLaneOwner(n.nodeId); return owner ? owner.ids.map(id => tc.doc.nodes.find(x => x.nodeId === id)).filter(Boolean) : tcMainNodes(); }
// 显式起点表示跳过前面的行；选中车道内任务就沿该路线继续，实际去向由执行端编译。
function tcCanUseEntry(id, doc = tc.doc) { return typeof id === 'string' && !!id.trim() && (doc?.nodes || []).filter(n => n.nodeId === id).length === 1; }
function tcCompositionGroup(n, doc = tc.doc) {
    const root = n.lanes && !n.lanes.decision && n.lanes.sourceFlowNodeId ? doc.nodes.find(x => x.nodeId === n.lanes.sourceFlowNodeId) || n : n;
    const ids = new Set([root.nodeId]);
    for (const pair of [root, ...doc.nodes.filter(x => x.lanes && !x.lanes.decision && x.lanes.sourceFlowNodeId === root.nodeId)]) {
        ids.add(pair.nodeId); for (const id of [...(pair.lanes?.yesNodeIds || []), ...(pair.lanes?.noNodeIds || [])]) ids.add(id);
    }
    return ids;
}
function tcMoveComposition(id, targetId, after) {
    const list = tc.doc.nodes, node = list.find(n => n.nodeId === id), target = list.find(n => n.nodeId === targetId);
    if (!node || !target) return;
    const owner = tcLaneOwner(id), targetOwner = tcLaneOwner(targetId);
    if (owner || targetOwner) {
        if (!owner || !targetOwner || owner.ids !== targetOwner.ids) return mtToast('车道任务请在所属路线内调整顺序。', true);
        owner.ids.splice(owner.ids.indexOf(id), 1); owner.ids.splice(owner.ids.indexOf(targetId) + (after ? 1 : 0), 0, id);
    } else {
        const moving = tcIsComposition(node) ? tcCompositionGroup(node) : new Set([id]), targets = tcIsComposition(target) ? tcCompositionGroup(target) : new Set([targetId]);
        if ([...moving].some(x => targets.has(x))) return;
        const rows = list.filter(x => moving.has(x.nodeId)), remaining = list.filter(x => !moving.has(x.nodeId));
        const indexes = remaining.map((x,i) => targets.has(x.nodeId) ? i : -1).filter(i => i >= 0);
        if (!indexes.length) return;
        remaining.splice(after ? Math.max(...indexes) + 1 : Math.min(...indexes), 0, ...rows); tc.doc.nodes = remaining;
    }
    tcEdited('已调整任务顺序，正在自动保存。');
}
function tcFlowWalk(steps, fn, background = false, depth = 0) {
    if (depth > 40) throw new Error('流程子链层级过深，请先简化结构。');
    for (const s of steps || []) { fn(s, steps, background); for (const key of ['trueSteps','falseSteps','fireSteps','beforeAttemptSteps']) tcFlowWalk(s[key], fn, background || key === 'fireSteps' || key === 'beforeAttemptSteps', depth + 1); }
}
function tcRenewSteps(flow, mapping = {}) {
    const ids = new Map(); tcFlowWalk(flow.steps, s => ids.set(s.id, mtNewId()));
    tcFlowWalk(flow.steps, s => { s.id = ids.get(s.id); if (mapping[s.targetNodeId]) s.targetNodeId = mapping[s.targetNodeId]; });
    if (flow.exitStepId) flow.exitStepId = ids.get(flow.exitStepId) || null;
}
function tcDuplicateComposition(node) {
    const ids = tcIsComposition(node) ? tcCompositionGroup(node) : new Set([node.nodeId]);
    const original = tc.doc.nodes.filter(n => ids.has(n.nodeId)), mapping = Object.fromEntries(original.map(n => [n.nodeId, 'n-' + mtNewId()]));
    const copies = original.map(n => { const c = mtClone(n); c.nodeId = mapping[n.nodeId]; if (tcIsComposition(node) || c.compositionDerivedPath === true) { delete c.path; c.strategies = (c.strategies || []).filter(s => s.kind !== 'flow.route'); delete c.compositionDerivedPath; }
        if (c.lanes) { c.lanes.sourceFlowNodeId = mapping[c.lanes.sourceFlowNodeId] || c.lanes.sourceFlowNodeId; for (const key of ['yesNodeIds','noNodeIds']) c.lanes[key] = (c.lanes[key] || []).map(id => mapping[id] || id); c.lanes.name += ' 副本'; }
        for (const flow of [c.flow, c.lanes?.decision].filter(Boolean)) { tcRenewSteps(flow, mapping); flow.name += ' 副本'; } return c; });
    const at = Math.max(...original.map(n => tc.doc.nodes.indexOf(n))) + 1; tc.doc.nodes.splice(at, 0, ...copies);
    const owner = tcLaneOwner(node.nodeId); if (owner) owner.ids.splice(owner.ids.indexOf(node.nodeId) + 1, 0, mapping[node.nodeId]);
    tc.selectedNode = mapping[node.nodeId]; tcEdited('已复制任务及其内部结构，正在自动保存。');
}
function tcRemoveComposition(node) {
    const ids = tcIsComposition(node) ? tcCompositionGroup(node) : new Set([node.nodeId]);
    tc.doc.nodes = tc.doc.nodes.filter(n => !ids.has(n.nodeId));
    for (const n of tc.doc.nodes) if (n.lanes) for (const key of ['yesNodeIds','noNodeIds']) n.lanes[key] = (n.lanes[key] || []).filter(id => !ids.has(id));
    if (ids.has(tc.doc[TC_START_KEY])) delete tc.doc[TC_START_KEY]; if (ids.has(tc.selectedNode)) tc.selectedNode = '';
    tcEdited('已移除任务及其所属结构，正在自动保存。');
}
function tcFlowLabel(s) { return s.name?.trim() || TC_FLOW_LABELS[s.kind] || s.kind; }
function tcFlowSummary(s) {
    if (s.kind === 'setRegister' || s.kind === 'judgeRegister') return typeof mtRegisterSummary === 'function' ? mtRegisterSummary(s) : (s.kind === 'setRegister' ? '设置寄存器' : '判断寄存器');
    if (s.kind === 'wait') return `等待 ${s.waitSeconds} 秒`;
    if (s.kind === 'timeRange') return `${s.timeStart}–${s.timeEnd}`;
    if (s.kind === 'weekday') return (s.weekdays || []).map(d => TC_WEEK[d - 1]).join('、') || '未选择星期（不会满足）';
    if (s.kind === 'logTrigger') return `日志出现「${s.logKeyword || '待填写'}」 · ${(s.fireSteps || []).length} 个触发动作`;
    if (s.kind === 'timerTrigger') return `${s.triggerTime} 到点时触发`;
    if (s.kind === 'watchdog') return `每 ${s.watchIntervalSeconds} 秒检查${TC_FLOW_LABELS[s.watchKind] || '状态'}`;
    if (s.kind === 'startProgram') return s.path ? s.path.split(/[\\/]/).pop() : '待选择已配置辅助程序';
    if (s.kind === 'manualConfirm') return s.confirmMessage || '等待人工选择';
    return `${s.kind === 'processRunning' ? s.processName : s.kind === 'bgiTaskName' ? s.taskName : TC_FLOW_LABELS[s.kind]} · ${s.expectRunning !== false ? '正在运行 / 名称包含' : '没有运行 / 名称不包含'}`;
}
function tcAddMenu() {
    if (!tcCanEdit(tcFlow())) return;
    const dialog = tcDialogShell('新增任务', '普通任务、顺序流程和条件车道均保存在当前计划中。', '<div class="tc-menu"><button class="dd-btn" data-kind="resource">配置组 / 一条龙 / 单项任务</button><button class="dd-btn" data-kind="flow">流程：按顺序执行动作、判断与触发器</button><button class="dd-btn" data-kind="lanes">车道：判断一次，只选择一条路线执行</button></div>');
    dialog.querySelectorAll('[data-kind]').forEach(b => b.onclick = () => { try { if (b.dataset.kind !== 'resource') tcAssertCompositionCompatible(); dialog.close(); if (b.dataset.kind === 'resource') tcAddDialog(); else if (b.dataset.kind === 'flow') tcFlowEditor(); else tcLanesEditor(); } catch(e) { mtToast(e.message,true); } });
}
function tcAssertCompositionCompatible() {
    if ((tc.doc?.nodes || []).some(n => ['control.condition','control.end'].includes(n.kind) || n.path && n.compositionDerivedPath !== true)) throw new Error('当前计划含旧自定义路径，不能混用基础流程和车道；请在新计划中配置，原计划保持不变。');
}
function tcFlowContext() { return { workflowId:tc.selected, target:mt.target, owner:mt.identity?.instanceId, doc:tc.doc }; }
function tcCheckFlowContext(context) {
    if (context.target !== mt.target || context.workflowId !== tc.selected || !tcCanEdit(tcFlow())) throw new Error('执行端或计划已经切换；此草稿未写入新目标，请返回原计划后再应用。');
}
function tcAllowedPrograms() { return mtPick(tc.list, 'allowedTaskPrograms') || []; }
function tcNewFlowStep(kind) {
    const s = typeof mtNewStep === 'function' ? mtNewStep(kind) : { id:mtNewId(), kind, enabled:true, name:'', trueSteps:[], falseSteps:[], fireSteps:[] };
    s.nodeType = TC_FLOW_CONDITIONS.has(kind) ? 'condition' : 'action'; s.triggerScope = 'round'; s.targetNodeId = ''; s.beforeAttemptSteps = [];
    if (kind === 'setRegister') s.registerWrite = { values:Array(10).fill(null) }; if (kind === 'judgeRegister') s.registerCondition = { values:Array(10).fill(null), match:'all' }; return s;
}
function tcFlowDialogShell(id, title, subtitle) {
    const dialog = mtDialog(id); dialog.classList.add('tc-composition-dialog');
    dialog.innerHTML = `<header class="mt-dialog-head"><div><h3>${mte(title)}</h3><p class="dodoco-hint">${mte(subtitle)}</p></div><button class="dd-btn dd-btn-sm" data-close>关闭</button></header><div class="tc-dialog-body" data-body></div><footer class="tc-dialog-foot"><span class="tc-flow-error" data-error role="alert"></span><button class="dd-btn" data-cancel>取消</button><button class="dd-btn dd-btn-primary" data-apply>应用到计划</button></footer>`;
    dialog.querySelector('[data-close]').onclick = dialog.querySelector('[data-cancel]').onclick = () => dialog.close(); if (!dialog.open) dialog.showModal(); return dialog;
}
function tcFlowValidate(flow, resources = tcResourceNodes(), programs = tcAllowedPrograms(), allowEmpty = false) {
    if (!flow.name?.trim()) throw new Error('请填写流程名称。'); if (!allowEmpty && !flow.steps?.length) throw new Error('请至少添加一个步骤。');
    const seen = new Set(), time = /^([01]\d|2[0-3]):[0-5]\d$/;
    tcFlowWalk(flow.steps, (s, chain, background) => {
        const label = tcFlowLabel(s), fail = msg => { throw new Error(label + '：' + msg); };
        if (!s.id || seen.has(s.id)) fail('节点身份缺失或重复'); seen.add(s.id);
        if (!TC_FLOW_KINDS.includes(s.kind) || s.nodeType !== (TC_FLOW_CONDITIONS.has(s.kind) ? 'condition' : 'action')) fail('不支持的节点类型');
        if (background && (TC_FLOW_TRIGGERS.has(s.kind) || ['startProgram','manualConfirm'].includes(s.kind))) fail('后台子链不能添加触发器、辅助程序或人工确认');
        const integer = (value,min,max) => Number.isInteger(value) && value >= min && value <= max;
        if (s.kind === 'wait' && !integer(s.waitSeconds,0,86400)) fail('等待秒数须为 0–86400 的整数');
        if (s.kind === 'manualConfirm' && !integer(s.confirmTimeoutSeconds,0,86400)) fail('超时秒数须为 0–86400 的整数');
        if (s.kind === 'timeRange' && (!time.test(s.timeStart) || !time.test(s.timeEnd))) fail('请填写有效开始和结束时刻');
        const conditionKind = s.kind === 'watchdog' ? s.watchKind : s.kind;
        if (conditionKind === 'processRunning' && !s.processName?.trim()) fail('请填写进程名称'); if (conditionKind === 'bgiTaskName' && !s.taskName?.trim()) fail('请填写任务名称');
        if (s.kind === 'setRegister' || s.kind === 'judgeRegister') { const v = (s.kind === 'setRegister' ? s.registerWrite : s.registerCondition)?.values; if (!Array.isArray(v) || v.length !== 10 || v.some(x => x !== null && x !== 0 && x !== 1) || !v.some(x => x !== null)) fail('请选择至少一个寄存器，值须为 0 或 1'); if (s.kind === 'judgeRegister' && !['all','any'].includes(s.registerCondition.match)) fail('请选择全部或任一条件满足'); }
        if (s.kind === 'weekday' && (!Array.isArray(s.weekdays) || s.weekdays.some(d => !Number.isInteger(d) || d < 1 || d > 7))) fail('星期参数无效');
        if (['bgiRunning','gameRunning','processRunning','bgiTaskRunning','bgiTaskName','watchdog'].includes(s.kind)) { if (!['currentSession','startupOrder','userName'].includes(s.statusSource)) fail('请选择状态来源'); if (s.statusSource === 'startupOrder' && !integer(s.statusTargetOrder,1,100)) fail('BGI 顺序须为 1–100 的整数'); if (s.statusSource === 'userName' && !s.statusTargetUser?.trim()) fail('请填写 Windows 用户'); }
        if (s.kind === 'startProgram' && !programs.some(p => p.path === s.path && (p.arguments || '') === (s.arguments || '') && (p.windowsUserName || '') === (s.windowsUserName || ''))) fail('请选择执行端启动中心已配置的辅助程序');
        if (TC_FLOW_TRIGGERS.has(s.kind)) {
            if (!['round','flow','task'].includes(s.triggerScope)) fail('请选择有效范围');
            if (s.triggerScope === 'task' && !resources.some(n => n.nodeId === s.targetNodeId)) fail('请选择监听的任务');
            if (s.kind === 'logTrigger' && !s.logKeyword?.trim()) fail('请填写日志关键字'); if (s.kind === 'timerTrigger' && !time.test(s.triggerTime)) fail('请填写触发时刻');
            if (s.kind === 'watchdog' && (!MT_WATCHABLE.includes(s.watchKind) || !integer(s.watchIntervalSeconds,1,86400) || !integer(s.watchConfirmSeconds,1,3600) || !integer(s.watchConfirmTimes,1,100))) fail('电子狗检测或复核参数无效');
        }
    });
    if (flow.exitStepId) { const last = flow.steps?.at(-1); if (!last || last.id !== flow.exitStepId || !TC_FLOW_CONDITIONS.has(last.kind) || last.enabled === false || last.trueSteps?.length || last.falseSteps?.length) throw new Error('车道出口必须是主链最后一个已启用的判断，且不能再带是/否子链。'); }
}
function tcFlowParameterHtml(s, options = {}) {
    const input = (label,key,type='text',extra='') => mtField(label, `<input data-param="${key}" type="${type}" value="${mte(s[key] ?? '')}" ${extra}>`);
    const select = (label,key,choices) => mtField(label, `<select data-param="${key}">${choices.map(([value,text]) => `<option value="${mte(value)}"${String(s[key] ?? '') === String(value) ? ' selected' : ''}>${mte(text)}</option>`).join('')}</select>`);
    const check = (label,key) => `<label class="dd-check"><input data-param="${key}" type="checkbox"${s[key] ? ' checked' : ''}>${mte(label)}</label>`;
    const condition = kind => {
        if (kind === 'setRegister' || kind === 'judgeRegister') return typeof mtRegisterFields === 'function' ? mtRegisterFields(s) : '<p>寄存器编辑组件未加载，请刷新页面。</p>';
        if (kind === 'wait') return input('等待秒数','waitSeconds','number','min="0" max="86400" step="1"');
        if (kind === 'timeRange') return `<div class="mt-form-row">${input('开始时间','timeStart','time')}${input('结束时间','timeEnd','time')}</div><p class="dodoco-hint">按执行端时间判断，支持跨午夜。</p>`;
        if (kind === 'weekday') return `<div class="mt-week">${TC_WEEK.map((w,i) => `<label><input data-week="${i + 1}" type="checkbox"${(s.weekdays || []).includes(i + 1) ? ' checked' : ''}>${w}</label>`).join('')}</div>`;
        if (kind === 'manualConfirm') return input('询问内容','confirmMessage') + input('超时秒数（0 表示一直等待）','confirmTimeoutSeconds','number','min="0" max="86400"') + check('超时选择“是”','confirmTimeoutGoTrue');
        if (kind === 'startProgram') { const programs = tcAllowedPrograms(), index = programs.findIndex(p => p.path === s.path && (p.arguments || '') === (s.arguments || '') && (p.windowsUserName || '') === (s.windowsUserName || ''));
            return mtField('执行端已配置辅助程序', `<select data-program><option value="">请选择辅助程序</option>${programs.map((p,i) => `<option value="${i}"${i === index ? ' selected' : ''}>${mte(p.name || p.path)}${p.windowsUserName ? ' · ' + mte(p.windowsUserName) : ''}</option>`).join('')}</select>`) + '<p class="dodoco-hint">目标来自执行端启动中心；在启动中心配置程序后刷新任务中心即可使用。</p>'; }
        let html = kind === 'processRunning' ? input('进程名称','processName') : kind === 'bgiTaskName' ? input('当前任务名包含','taskName') : '';
        html += check(kind === 'bgiTaskName' ? '名称包含时满足（取消则不包含时满足）' : '正在运行时满足（取消则没有运行时满足）','expectRunning');
        html += select('状态来源','statusSource',[['currentSession','本会话'],['startupOrder','BGI 启动顺序'],['userName','Windows 用户']]);
        if (s.statusSource === 'startupOrder') html += input('BGI 顺序（1–100）','statusTargetOrder','number','min="1" max="100"'); if (s.statusSource === 'userName') html += input('Windows 用户','statusTargetUser'); return html;
    };
    let html = options.conditionOnly ? '' : input('步骤名称（可选）','name') + check('启用这一步','enabled');
    if (!TC_FLOW_TRIGGERS.has(s.kind)) return html + condition(s.kind);
    html += select('有效范围','triggerScope',[['round','这一轮计划内'],['flow','这些流程步骤执行期间'],['task','指定任务执行期间']]);
    if (s.triggerScope === 'task') html += select('监听哪个任务','targetNodeId',[['','请选择任务'],...tcResourceNodes().map(n => [n.nodeId,tcNodeName(n)])]);
    if (s.kind === 'logTrigger') html += input('日志出现这些文字','logKeyword') + check('每次出现都执行动作','watchRepeat');
    if (s.kind === 'timerTrigger') html += input('触发时刻','triggerTime','time') + check('范围内每天重复','repeatDaily');
    if (s.kind === 'watchdog') html += select('盯梢条件','watchKind',MT_WATCHABLE.map(k => [k,TC_FLOW_LABELS[k]])) + `<div class="mt-form-row">${input('检测间隔（秒）','watchIntervalSeconds','number','min="1" max="86400"')}${input('复核间隔（秒）','watchConfirmSeconds','number','min="1" max="3600"')}${input('复核次数','watchConfirmTimes','number','min="1" max="100"')}</div>` + check('触发后继续盯梢','watchRepeat') + condition(s.watchKind);
    return html + '<p class="dodoco-hint">在流程中展开此触发器，向“触发时”添加动作；指定任务范围还可设置“任务每次开始前”的初始化动作。有效范围结束后自动停止监听。</p>';
}
function tcBindFlowParameters(host,s,changed,redraw) {
    host.querySelectorAll('[data-param]').forEach(el => el.onchange = () => { const key = el.dataset.param; s[key] = el.type === 'checkbox' ? el.checked : el.type === 'number' ? el.value.trim() === '' ? NaN : Number(el.value) : el.value; changed(); if (['statusSource','watchKind','triggerScope'].includes(key)) redraw(); });
    host.querySelectorAll('[data-week]').forEach(el => el.onchange = () => { s.weekdays = [...host.querySelectorAll('[data-week]:checked')].map(x => +x.dataset.week); changed(); });
    const program = host.querySelector('[data-program]'); if (program) program.onchange = () => { const p = program.value === '' ? null : tcAllowedPrograms()[+program.value]; s.path = p?.path || ''; s.arguments = p?.arguments || ''; s.windowsUserName = p?.windowsUserName || ''; changed(); };
    if ((s.kind === 'setRegister' || s.kind === 'judgeRegister') && typeof mtBindRegisterFields === 'function') mtBindRegisterFields(host,s,changed);
}
function tcFlowEditor(node = null, selectedStepId = null, options = {}) {
    const context = tcFlowContext(), original = node ? JSON.stringify(node.flow) : null, nodeId = node?.nodeId;
    const draft = mtClone(options.source || node?.flow || { name:'流程', steps:[], exitStepId:null });
    const dialog = tcFlowDialogShell(options.dialogId || 'tcFlowDialog', options.title || '编辑流程', '在局部草稿中编辑，应用后保存到当前计划；取消保留原计划。');
    let selected = selectedStepId, targetChain = draft.steps, insertAfter = null, targetBackground = false, targetName = '流程步骤';
    const body = dialog.querySelector('[data-body]'), error = msg => { dialog.querySelector('[data-error]').textContent = msg || ''; };
    let slots = new Map(), chains = new Map();
    function selectChain(chain,background,name) { targetChain=chain; targetBackground=background; targetName=name; insertAfter=null; selected=null; draw(); }
    function add(kind) { const s = tcNewFlowStep(kind), at = insertAfter ? targetChain.findIndex(x => x.id === insertAfter) + 1 : targetChain.length, exit = targetChain === draft.steps ? targetChain.findIndex(x => x.id === draft.exitStepId) : -1;
        targetChain.splice(exit >= 0 ? Math.min(at,exit) : at,0,s); selected=s.id; draw(); }
    function stepTree(chain,background=false,depth=0) {
        if (depth > 40) return '<p class="tc-flow-error">子链层级过深</p>';
        return chain.map((s,i) => { slots.set(s.id,{step:s,chain,background}); const groups = [];
            if (TC_FLOW_CONDITIONS.has(s.kind) && s.id !== draft.exitStepId) groups.push(['trueSteps','条件满足时',background],['falseSteps','不满足时',background]);
            if (TC_FLOW_TRIGGERS.has(s.kind)) { groups.push(['fireSteps',s.kind === 'logTrigger' ? '日志出现时' : s.kind === 'timerTrigger' ? '到点时' : '检测满足时',true]); if (s.triggerScope === 'task') groups.push(['beforeAttemptSteps','任务每次开始前',true]); }
            return `<div class="tc-flow-step${selected === s.id ? ' selected' : ''}${s.enabled === false ? ' off' : ''}"><button type="button" class="tc-flow-select" data-step="${mte(s.id)}"><b>${i + 1} · ${mte(tcFlowLabel(s))}${s.id === draft.exitStepId ? ' · 车道选择条件' : ''}</b><small>${mte(tcFlowSummary(s))}</small></button>${groups.map(([key,label,bg]) => { const list = s[key] ||= [], token = s.id + ':' + key; chains.set(token,{chain:list,background:bg,name:tcFlowLabel(s) + ' → ' + label}); return `<details class="tc-flow-chain" open><summary>${mte(label)} · ${list.length} 个动作</summary><button class="dd-btn dd-btn-sm" data-chain="${mte(token)}">＋ 添加动作</button>${list.length ? stepTree(list,bg,depth+1) : '<p class="dodoco-hint">暂无动作</p>'}</details>`; }).join('')}</div>`; }).join('');
    }
    function draw() {
        slots = new Map(); chains = new Map(); const tree = stepTree(draft.steps); const slot = slots.get(selected);
        if (slot) { targetChain=slot.chain; targetBackground=slot.background; insertAfter=selected; }
        const background = targetBackground, kinds = TC_FLOW_KINDS.filter(k => !background || ['wait','setRegister'].includes(k) || TC_FLOW_CONDITIONS.has(k) && k !== 'manualConfirm');
        body.innerHTML = `<label class="dd-field"><span>流程名称</span><input data-flow-name value="${mte(draft.name)}"></label><div class="tc-flow-editor"><section class="tc-flow-tree"><header><h4>按顺序执行</h4><button class="dd-btn dd-btn-sm" data-main>＋ 添加步骤</button></header>${tree || '<p class="dd-empty">还没有步骤，选择右侧动作开始。</p>'}</section><aside class="tc-flow-catalog"><h4>选择要做的事情</h4><p class="dodoco-hint">添加到：${mte(slot ? tcFlowLabel(slot.step) + '之后' : targetName)}</p>${[['动作',kinds.filter(k => !TC_FLOW_CONDITIONS.has(k) && !TC_FLOW_TRIGGERS.has(k))],['判断',kinds.filter(k => TC_FLOW_CONDITIONS.has(k))],['触发器',kinds.filter(k => TC_FLOW_TRIGGERS.has(k))]].filter(x => x[1].length).map(([title,list]) => `<h5>${title}</h5><div class="dd-actions">${list.map(k => `<button class="dd-btn dd-btn-sm" data-add="${k}">＋ ${TC_FLOW_LABELS[k]}</button>`).join('')}</div>`).join('')}</aside></div><section class="tc-flow-parameters tc-sec">${slot ? `<div class="dd-actions"><strong>${mte(tcFlowLabel(slot.step))}</strong><button class="dd-btn dd-btn-sm" data-move="-1">上移</button><button class="dd-btn dd-btn-sm" data-move="1">下移</button><button class="dd-btn dd-btn-sm" data-copy>复制</button><button class="dd-btn dd-btn-sm dd-btn-danger" data-remove>移除</button>${options.requireExit ? '<button class="dd-btn dd-btn-sm" data-exit>作为车道选择条件</button>' : ''}</div><div data-parameters>${tcFlowParameterHtml(slot.step)}</div>` : '<p class="dodoco-hint">选择一个步骤编辑参数；选择子链的“添加动作”，可在对应上下文继续添加。</p>'}</section>`;
        body.querySelector('[data-flow-name]').oninput = e => draft.name=e.target.value;
        body.querySelector('[data-main]').onclick = () => selectChain(draft.steps,false,'流程步骤');
        body.querySelectorAll('[data-step]').forEach(b => b.onclick = () => { selected=b.dataset.step; draw(); });
        body.querySelectorAll('[data-chain]').forEach(b => b.onclick = () => { const c=chains.get(b.dataset.chain); selectChain(c.chain,c.background,c.name); });
        body.querySelectorAll('[data-add]').forEach(b => b.onclick=() => add(b.dataset.add));
        if (!slot) return;
        const { step:s,chain } = slot, index=chain.indexOf(s);
        body.querySelectorAll('[data-move]').forEach(b => { const to=index + +b.dataset.move; b.disabled=to<0 || to>=chain.length || s.id===draft.exitStepId || chain[to]?.id===draft.exitStepId; b.onclick=() => { [chain[index],chain[to]]=[chain[to],chain[index]]; draw(); }; });
        body.querySelector('[data-copy]').onclick=() => { const c=mtClone(s); tcRenewSteps({steps:[c]}); c.name=tcFlowLabel(s)+' 副本'; chain.splice(index+(s.id===draft.exitStepId?0:1),0,c); selected=c.id; draw(); };
        body.querySelector('[data-remove]').onclick=() => { chain.splice(index,1); if(s.id===draft.exitStepId) draft.exitStepId=null; selectChain(chain,slot.background,'当前子链'); };
        if (body.querySelector('[data-exit]')) body.querySelector('[data-exit]').onclick=() => { if (chain!==draft.steps || !TC_FLOW_CONDITIONS.has(s.kind) || s.enabled===false || s.trueSteps?.length || s.falseSteps?.length) return error('请选择主链中已启用且没有子链的判断步骤。'); chain.splice(index,1); chain.push(s); draft.exitStepId=s.id; draw(); };
        tcBindFlowParameters(body.querySelector('[data-parameters]'),s,() => { const selectedButton=[...body.querySelectorAll('[data-step]')].find(b=>b.dataset.step===s.id); if(selectedButton) { selectedButton.querySelector('b').textContent=`${chain.indexOf(s)+1} · ${tcFlowLabel(s)}${s.id===draft.exitStepId?' · 车道选择条件':''}`; selectedButton.querySelector('small').textContent=tcFlowSummary(s); } },draw);
    }
    dialog.querySelector('[data-apply]').onclick = () => {
        try {
            tcCheckFlowContext(context); if (!options.onApply) tcAssertCompositionCompatible(); tcFlowValidate(draft,tcResourceNodes(),tcAllowedPrograms(),!!options.allowEmpty);
            if (options.requireExit && !draft.exitStepId) throw new Error('请选择决定车道的出口判断。');
            if (options.onApply) options.onApply(draft);
            else if (nodeId) { const current=tc.doc.nodes.find(n => n.nodeId===nodeId); if (!current?.flow || JSON.stringify(current.flow)!==original) throw new Error('原流程已被修改或删除，未覆盖新内容；你的草稿仍保留在窗口中。'); if (!draft.exitStepId && tc.doc.nodes.some(n => n.lanes?.sourceFlowNodeId===nodeId)) throw new Error('此流程仍连接车道，请保留出口判断或先删除配对车道。'); current.flow=draft; }
            else { const n={nodeId:tcNewNodeId(),kind:'control.flow',flow:draft,strategies:[]}; tc.doc.nodes.push(n); tc.selectedNode=n.nodeId; }
            dialog.close(); if (!options.onApply) tcEdited('流程已应用到计划，正在自动保存。');
        } catch(e) { error(e.message); }
    };
    draw(); return dialog;
}
function tcFlowHasStep(flow,stepId) { let found=false; if(stepId)tcFlowWalk(flow?.steps,s=>{if(s.id===stepId)found=true;}); return found; }
function tcLanesEditor(node = null, selectedStepId = null) {
    const context=tcFlowContext(), nodeId=node?.nodeId, original=node ? JSON.stringify(node.lanes) : null;
    const source=node?.lanes?.decision ? null : tc.doc.nodes.find(n => n.nodeId===node?.lanes?.sourceFlowNodeId), originalSource=source ? JSON.stringify(source.flow) : null;
    const draft=mtClone(node?.lanes || {name:'选择路线',yesName:'路线 1',noName:'路线 2',yesNodeIds:[],noNodeIds:[],sourceFlowNodeId:''});
    const decision=mtClone(draft.decision || source?.flow || {name:'车道条件',steps:[],exitStepId:null});
    let condition=decision.steps.find(s => s.id===decision.exitStepId) || null, before=decision.steps.filter(s => s.id!==decision.exitStepId);
    const cached=new Map(condition?[[condition.kind,condition]]:[]), dialog=tcFlowDialogShell('tcLaneDialog','设置车道','到这里时判断一次：满足条件走上面的路线，其他情况走下面的路线；完成后继续下一项任务。');
    const body=dialog.querySelector('[data-body]'), error=msg => dialog.querySelector('[data-error]').textContent=msg||'';
    function preview() { const p=body.querySelector('[data-preview]'); if(p) p.textContent=`满足「${condition?tcFlowSummary(condition):'尚未设置条件'}」 → ${draft.yesName}\n其他情况 → ${draft.noName}\n选中路线执行完 → 继续下一项任务`; }
    function openBefore(stepId=null) {
        body.querySelector('[data-before]')?.closest('details')?.setAttribute('open','');
        return tcFlowEditor(null,stepId,{title:'当前车道 · 判断前的步骤',source:{name:'判断前的步骤',steps:before,exitStepId:null},allowEmpty:true,onApply:flow=>{if(flow.exitStepId) throw new Error('选择车道的判断请在车道窗口中设置。');before=flow.steps;draw();}});
    }
    function draw() {
        body.innerHTML=`${mtField('车道名称',`<input data-name="name" value="${mte(draft.name)}">`)}<h4>1. 两条车道叫什么</h4><div class="mt-form-row">${mtField('满足条件时走',`<input data-name="yesName" value="${mte(draft.yesName)}">`)}${mtField('其他情况走',`<input data-name="noName" value="${mte(draft.noName)}">`)}</div><h4>2. 怎样选择车道</h4>${mtField('判断方式',`<select data-condition><option value="">请选择判断方式</option>${[...TC_FLOW_CONDITIONS].map(k=>`<option value="${k}"${condition?.kind===k?' selected':''}>${TC_FLOW_LABELS[k]}</option>`).join('')}</select>`)}<section class="tc-sec" data-parameters>${condition?tcFlowParameterHtml(condition,{conditionOnly:true}):'<p class="dodoco-hint">选择一种判断方式，设置具体条件。</p>'}</section><p class="tc-flow-preview" data-preview></p><details class="tc-sec"><summary>可选：判断之前先做几件事</summary><p class="dodoco-hint">这些步骤只属于当前车道，不增加独立流程任务；一般可留空。</p><p>${before.length?before.map(tcFlowLabel).map(mte).join(' → '):'未设置额外步骤'}</p><button class="dd-btn dd-btn-sm" data-before>编辑判断前的步骤</button></details>`;
        body.querySelectorAll('[data-name]').forEach(el=>el.oninput=()=>{draft[el.dataset.name]=el.value;preview();});
        body.querySelector('[data-condition]').onchange=e=>{const kind=e.target.value; if(!kind) condition=null; else {if(!cached.has(kind)) cached.set(kind,tcNewFlowStep(kind));condition=cached.get(kind);}draw();};
        if(condition) tcBindFlowParameters(body.querySelector('[data-parameters]'),condition,preview,draw);
        body.querySelector('[data-before]').onclick=()=>openBefore();
        preview();
    }
    dialog.querySelector('[data-apply]').textContent='保存车道';
    dialog.querySelector('[data-apply]').onclick=()=>{try {
        tcCheckFlowContext(context); tcAssertCompositionCompatible(); if(!draft.name.trim()||!draft.yesName.trim()||!draft.noName.trim()) throw new Error('请填写车道和两条路线的名称。'); if(!condition) throw new Error('请选择怎样判断进入哪条车道。');
        condition.enabled=true;condition.trueSteps=[];condition.falseSteps=[];decision.steps=[...before,condition];decision.exitStepId=condition.id;tcFlowValidate(decision);
        if(nodeId) {const current=tc.doc.nodes.find(n=>n.nodeId===nodeId);if(!current?.lanes||JSON.stringify(current.lanes)!==original||source&&JSON.stringify(tc.doc.nodes.find(n=>n.nodeId===source.nodeId)?.flow)!==originalSource) throw new Error('原车道或条件已被修改，未覆盖新内容；你的草稿仍保留在窗口中。'); if(source){tc.doc.nodes.find(n=>n.nodeId===source.nodeId).flow=decision;draft.decision=null;draft.sourceFlowNodeId=source.nodeId;}else {draft.decision=decision;draft.sourceFlowNodeId='';}current.lanes=draft;}
        else {draft.decision=decision;draft.sourceFlowNodeId='';const n={nodeId:tcNewNodeId(),kind:'control.lanes',lanes:draft,strategies:[]};tc.doc.nodes.push(n);tc.selectedNode=n.nodeId;}
        dialog.close();tcEdited('车道已保存。在两条路线中分别添加任务即可。');
    } catch(e){error(e.message);}};
    draw();
    if(selectedStepId && selectedStepId!==condition?.id && tcFlowHasStep({steps:before},selectedStepId))openBefore(selectedStepId);
    return dialog;
}
function tcLaneStatus(owner,yes) {
    const runs=[...tcActive(),...(mtPick(tc.list,'history')||[])].filter(r=>mtPick(r,'workflowId')===tc.selected).sort((a,b)=>String(mtPick(b,'createdAt')).localeCompare(String(mtPick(a,'createdAt'))));
    const run=runs[0];if(!run)return '';const round=run.cursor?.loopIteration??Math.max(0,...(run.nodeOutcomes||[]).map(o=>o.loopIteration||0));const outcome=(run.nodeOutcomes||[]).filter(o=>o.nodeId===owner&&o.loopIteration===round).at(-1);
    return !['branchYes','branchNo'].includes(outcome?.result)?'执行到这里时选择':(outcome.result==='branchYes')===yes?'已选中此车道':'本轮未选择';
}
function tcRenderCompositionRows(host,doc) {
    const editable=tcCanEdit(tcFlow()), open=new Set([...host.querySelectorAll('details[data-composition][open]')].map(el=>el.dataset.composition)), nodes=tcMainNodes(doc), byId=new Map(doc.nodes.map(n=>[n.nodeId,n]));
    const disabled=editable?'':' disabled', button=(label,action,id)=>`<button class="dd-btn dd-btn-sm" data-action="${action}" data-id="${mte(id)}"${disabled}>${label}</button>`;
    const runTrace=typeof tcRunTrace==='function'?tcRunTrace(doc):null, badge=n=>typeof tcRunBadge==='function'?tcRunBadge(n,runTrace):'';
    const resource=(n,index,lane=false)=>`<div class="tc-composition-resource${tcEnabled(n)?'':' off'}${runTrace?.node(n.nodeId)==='running'?' is-running':''}" data-resource="${mte(n.nodeId)}"><span class="tc-idx">${index+1}</span><button class="tc-cell tc-name" data-action="tasks" data-id="${mte(n.nodeId)}"><b>${mte(tcNodeName(n))}${badge(n)}</b><small>${mte(TC_KIND[n.kind]||n.kind)}</small></button><button class="tc-cell" data-action="week" data-id="${mte(n.nodeId)}"${disabled}>${mte(tcWeekText(n))}</button><button class="tc-cell" data-action="window" data-id="${mte(n.nodeId)}"${disabled}>${mte(tcWindowText(n))}</button><button class="tc-cell" data-action="account" data-id="${mte(n.nodeId)}"${disabled}>${tcStrat(n,'prerequisite.account')?mte(tcMask(tcStrat(n,'prerequisite.account').uid)):'未绑定账号'}</button><label class="dd-check"><input type="checkbox" data-enable-node="${mte(n.nodeId)}"${tcEnabled(n)?' checked':''}${disabled}>启用</label><div class="dd-actions">${button('上移','up',n.nodeId)}${button('下移','down',n.nodeId)}<button class="dd-btn dd-btn-sm" data-action="more" data-id="${mte(n.nodeId)}">更多</button></div></div>`;
    const trace=typeof tcRunTrace==='function'?tcRunTrace(doc):null, walked={running:' · 执行中',success:' · ✓ 本轮已完成',condTrue:' · 本轮 → 是',condFalse:' · 本轮 → 否',failed:' · 未完成'};
    const tree=(list,depth=0,owner='')=>depth>40?'<p>子链层级过深</p>':`<ol class="tc-flow-summary">${(list||[]).map(s=>{const w=trace?walked[trace.step(owner,s.id)]||'':'';return `<li><button class="tc-cell${w?' is-walked':''}" data-action="locate-step" data-step="${mte(s.id)}"><b>${mte(tcFlowLabel(s))}${s.enabled===false?' · 已停用':''}${w}</b><small>${mte(tcFlowSummary(s))}</small></button>${[['条件满足时',s.trueSteps],['不满足时',s.falseSteps],['触发时',s.fireSteps],['任务每次开始前',s.beforeAttemptSteps]].filter(x=>x[1]?.length).map(([label,sub])=>`<details><summary>${label}</summary>${tree(sub,depth+1,owner)}</details>`).join('')}</li>`;}).join('')}</ol>`;
    host.innerHTML=`<header class="tc-nodes-head"><div><h3>任务与流程</h3><p class="dodoco-hint">按计划顺序执行；车道只选择一条路线，完成后继续下一项任务。</p></div>${editable?'<button id="tcAdd" class="dd-btn dd-btn-sm dd-btn-primary">＋ 新增任务</button>':''}</header><div class="tc-composition">${nodes.map((n,i)=>{
        if(!tcIsComposition(n))return resource(n,i);
        const lane=n.lanes,decision=lane?.decision||byId.get(lane?.sourceFlowNodeId)?.flow,final=decision?.steps?.find(s=>s.id===decision.exitStepId);
        return `<details class="tc-composition-row" data-composition="${mte(n.nodeId)}"${open.has(n.nodeId)||tc.selectedNode===n.nodeId?' open':''}><summary><span class="tc-idx">${i+1}</span><span class="dd-pill">${lane?'车道':'流程'}</span><strong>${mte(tcNodeName(n))}${badge(n)}</strong><small>${lane?mte(final?tcFlowSummary(final):'尚未设置条件'):mte((n.flow?.steps?.length||0)+' 个步骤，按顺序执行')}</small></summary><div class="dd-actions">${button(lane?'设置车道':'编辑步骤','edit',n.nodeId)}${button('上移','up',n.nodeId)}${button('下移','down',n.nodeId)}${button('复制','copy',n.nodeId)}${button('移除','remove',n.nodeId)}<button class="dd-btn dd-btn-sm" data-action="more" data-id="${mte(n.nodeId)}">更多</button></div>${lane?`<p class="dodoco-hint">先判断：${mte(final?tcFlowSummary(final):'尚未设置')}</p><div class="tc-lanes">${[['yesNodeIds','yesName',true],['noNodeIds','noName',false]].map(([key,name,yes])=>`<section class="tc-lane${({ '已选中此车道':' is-taken ' + (yes ? 'yes' : 'no'), '本轮未选择':' is-skipped' })[tcLaneStatus(lane.decision?n.nodeId:lane.sourceFlowNodeId,yes)] || ''}"><header><h4>${yes?'满足条件 → ':'其他情况 → '}${mte(lane[name]||'路线')}<small>${mte(tcLaneStatus(lane.decision?n.nodeId:lane.sourceFlowNodeId,yes))}</small></h4>${button('＋ 任务',yes?'add-yes':'add-no',n.nodeId)}</header>${lane[key]?.length?lane[key].map((id,j)=>byId.has(id)?resource(byId.get(id),j,true):'<p class="tc-flow-error">任务引用缺失，请重新绑定</p>').join(''):'<p class="dodoco-hint">暂无任务；留空则直接继续下一项。</p>'}</section>`).join('')}</div><p class="dodoco-hint">选中路线中的任务执行完，继续下一项任务。</p>`:tree(n.flow?.steps,0,n.nodeId)}</details>`;
    }).join('')}</div>`;
    if(editable)host.querySelector('#tcAdd').onclick=tcAddMenu;
    if(typeof tcAutoExpandLanes==='function')tcAutoExpandLanes(host,doc);
    host.querySelectorAll('[data-enable-node]').forEach(el=>el.onchange=()=>{const n=byId.get(el.dataset.enableNode);tcSetEnabled(n,el.checked);tcEdited('已修改任务启用状态。');});
    host.querySelectorAll('[data-action]').forEach(b=>b.onclick=e=>{e.stopPropagation();const action=b.dataset.action,node=byId.get(b.dataset.id||b.closest('[data-composition]')?.dataset.composition);if(!node)return;
        if(action==='more')return tcNodeMenu(node);if(action==='tasks')return tcInnerTasks(node);if(!editable)return;
        if(['week','window','account'].includes(action))return tcNodeDialog(node,action);
        if(action==='edit')return node.lanes?tcLanesEditor(node):tcFlowEditor(node);
        if(action==='locate-step')return tcFlowEditor(node,b.dataset.step);
        if(action==='up'||action==='down')return tcMove(node,action==='up'?-1:1);
        if(action==='copy')return tcDuplicateComposition(node);
        if(action==='remove'){if(confirm(`移除「${tcNodeName(node)}」及其所属任务？`))tcRemoveComposition(node);return;}
        if(action==='add-yes'||action==='add-no')return tcAddDialog({cat:null,q:'',picked:new Set(),laneNodeId:node.nodeId,laneKey:action==='add-yes'?'yesNodeIds':'noNodeIds',workflowId:tc.selected,target:mt.target});
    });
}
async function tcLocateFlowStep(workflowId,nodeId,stepId) {
    if(tc.selected!==workflowId){if(!(await tcFlush()))return false;await tcOpen(workflowId,true);}
    if(tc.selected!==workflowId)return false;
    mt.tab='taskcenter';mtRenderHead();mtRenderBody();
    const node=tc.doc?.nodes?.find(n=>n.nodeId===nodeId);if(!node)return false;tc.selectedNode=node.nodeId;tcRenderNodes();
    const lane=node.lanes?node:tc.doc.nodes.find(n=>n.lanes&&!n.lanes.decision&&n.lanes.sourceFlowNodeId===nodeId);
    const flow=lane ? lane.lanes.decision || tc.doc.nodes.find(n=>n.nodeId===lane.lanes.sourceFlowNodeId)?.flow : node.flow;
    if(stepId && !tcFlowHasStep(flow,stepId))return false;
    if(lane)tcLanesEditor(lane,stepId);else if(node.flow)tcFlowEditor(node,stepId);else return false;return true;
}
