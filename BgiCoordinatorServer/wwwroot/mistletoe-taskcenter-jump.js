// 槲寄生 · 任务中心跳转（网页端，与 PC 同口径；Docs/design/taskcenter-jump-plan-20261111.md 第 4 阶段）。
// 流程整行（flow.jumpTo）、车道每条路线（lanes.yesJumpTo / noJumpTo）可设「执行完后跳到 #N」，只能往后跳到主计划行。
// 网页只编辑作者结构；去向派生、旧版保护标记（flow.jump）和启动前校验都在执行端保存/预检时完成，这里的校验只用于提示。

/** 每行编号：主计划行 #1、#2…，车道里的任务 #N.M；旧配对车道的条件流程与车道共用编号。 */
function tcRowNumbers(doc = tc.doc) {
    const numbers = new Map();
    tcMainNodes(doc).forEach((n, i) => {
        const no = '#' + (i + 1); numbers.set(n.nodeId, no);
        if (!n.lanes) return;
        if (n.lanes.sourceFlowNodeId) numbers.set(n.lanes.sourceFlowNodeId, no);
        [...(n.lanes.yesNodeIds || []), ...(n.lanes.noNodeIds || [])].forEach((id, k) => numbers.set(id, `${no}.${k + 1}`));
    });
    return numbers;
}
/** 编号 + 名称，例如「#5 至冬材料」。 */
function tcRowLabel(id, doc = tc.doc) {
    const n = doc?.nodes?.find(x => x.nodeId === id); if (!n) return null;
    const no = tcRowNumbers(doc).get(id); return (no ? no + ' ' : '') + tcNodeName(n);
}
/** 跳转目标实际进入的行：旧配对车道换算为它的条件流程。 */
function tcJumpResolve(doc, target) {
    const n = target && doc.nodes.find(x => x.nodeId === target); if (!n) return null;
    return n.lanes && !n.lanes.decision && n.lanes.sourceFlowNodeId ? n.lanes.sourceFlowNodeId : target;
}
function tcIsLaneMember(doc, id) { return doc.nodes.some(n => n.lanes && [...(n.lanes.yesNodeIds || []), ...(n.lanes.noNodeIds || [])].includes(id)); }
/** 主计划行的「执行顺序」列表：旧配对车道把条件流程放在车道前（与执行端 MainPlan 存储顺序一致，用于判断前后）。 */
function tcJumpOrder(doc) { return doc.nodes.filter(n => !tcIsLaneMember(doc, n.nodeId)).map(n => n.nodeId); }

/** 某行可选目标：第一项「继续下一行」，其后是它之后的主计划行；当前不合法的目标也列出并注明。 */
function tcJumpChoices(sourceId, current, doc = tc.doc) {
    const rows = tcMainNodes(doc), numbers = tcRowNumbers(doc), from = sourceId ? rows.findIndex(n => n.nodeId === sourceId) : rows.length;
    const choices = [{ id:'', label:'继续下一行' }];
    if (from >= 0) for (let i = from + 1; i < rows.length; i++) choices.push({ id:rows[i].nodeId, label:`跳到 ${numbers.get(rows[i].nodeId)} ${tcNodeName(rows[i])}` });
    if (current && !choices.some(c => c.id === current)) choices.push({ id:current, label:tcRowLabel(current, doc) ? `跳到 ${tcRowLabel(current, doc)}（在前面，不能执行）` : '跳到 已删除的行（请重新选择）' });
    return choices;
}
function tcJumpSelect(attr, choices, current) {
    return `<select ${attr}>${choices.map(c => `<option value="${mte(c.id)}"${c.id === (current || '') ? ' selected' : ''}>${mte(c.label)}</option>`).join('')}</select>`;
}

/** 逐行的跳转问题（来源行 → 原因），与执行端 WorkflowComposition.JumpIssues 同规则；执行端预检仍是权威。 */
function tcJumpIssues(doc = tc.doc) {
    const issues = new Map(), order = tcJumpOrder(doc);
    const add = (id, msg) => issues.set(id, issues.has(id) ? issues.get(id) + '；' + msg : msg);
    const check = (source, target, label) => {
        if (!target) return;
        const name = source.flow?.name || source.lanes?.name || source.nodeId;
        if (doc.nodes.filter(n => n.nodeId === target).length !== 1) return add(source.nodeId, `「${name}」${label}的跳转目标已不存在，请重新选择`);
        if (tcIsLaneMember(doc, target)) return add(source.nodeId, `「${name}」${label}不能跳进车道里的任务，只能跳到主计划行`);
        const from = order.indexOf(source.nodeId), to = order.indexOf(tcJumpResolve(doc, target));
        if (from < 0 || to <= from) add(source.nodeId, `「${name}」${label}的跳转目标在它前面或就是它自己，只能往后跳，请修改`);
    };
    for (const n of doc.nodes || []) {
        if (n.lanes?.decision?.jumpTo) add(n.nodeId, `车道「${n.lanes.name}」的选择条件不能设置跳转`);
        if (n.kind === 'control.flow' && n.flow?.jumpTo) { if (n.flow.exitStepId) add(n.nodeId, `「${n.flow.name}」是旧版车道的条件流程，不能设置跳转`); else check(n, n.flow.jumpTo, '执行完后'); }
        if (n.kind === 'control.lanes' && n.lanes && (n.lanes.yesJumpTo || n.lanes.noJumpTo)) {
            if (!n.lanes.decision) { add(n.nodeId, `旧版车道「${n.lanes.name}」不支持跳转，请改用新版车道`); continue; }
            check(n, n.lanes.yesJumpTo, '「满足条件」路线'); check(n, n.lanes.noJumpTo, '「其他情况」路线');
        }
    }
    return issues;
}
/** 从首行出发走不到的主计划行（只提示）。有跳转问题时不计算。 */
function tcUnreachable(doc = tc.doc) {
    if (!doc?.nodes?.some(tcIsComposition) || tcJumpIssues(doc).size) return new Set();
    const order = tcJumpOrder(doc), byId = new Map(doc.nodes.map(n => [n.nodeId, n])), seen = new Set(), stack = order.length ? [order[0]] : [];
    const after = id => { const i = order.indexOf(id); return i + 1 < order.length ? order[i + 1] : null; };
    while (stack.length) {
        const id = stack.pop(); if (!id || seen.has(id)) continue; seen.add(id);
        const n = byId.get(id); if (!n) continue;
        if (n.kind === 'control.flow' && n.flow?.jumpTo && !n.flow.exitStepId) { stack.push(tcJumpResolve(doc, n.flow.jumpTo)); continue; }
        if (n.lanes) {
            const decision = n.lanes.decision ? n : null, next = after(id);
            for (const [ids, jump] of [[n.lanes.yesNodeIds || [], n.lanes.yesJumpTo], [n.lanes.noNodeIds || [], n.lanes.noJumpTo]]) {
                ids.forEach(x => seen.add(x));
                stack.push(decision && jump ? tcJumpResolve(doc, jump) : next);
            }
            continue;
        }
        stack.push(after(id));
    }
    const integrated = new Set(doc.nodes.filter(n => n.lanes && !n.lanes.decision && n.lanes.sourceFlowNodeId).map(n => n.lanes.sourceFlowNodeId));
    return new Set(order.filter(id => !seen.has(id) && !integrated.has(id)));
}
/** 跳转说明，例如「执行完后 → #5 至冬材料」。 */
function tcJumpText(n, doc = tc.doc) {
    const t = id => tcRowLabel(id, doc) || '已删除的行';
    if (n.flow?.jumpTo) return '执行完后 → ' + t(n.flow.jumpTo);
    if (!n.lanes) return '';
    return [n.lanes.yesJumpTo && '满足条件路线走完 → ' + t(n.lanes.yesJumpTo), n.lanes.noJumpTo && '其他情况路线走完 → ' + t(n.lanes.noJumpTo)].filter(Boolean).join('；');
}
/** 行下方提示：跳转问题优先，其次是走不到。 */
function tcRowNotice(n, issues, unreachable) {
    if (issues.has(n.nodeId)) return { text:'⚠ ' + issues.get(n.nodeId), problem:true };
    if (unreachable.has(n.nodeId)) return { text:'没有路径会走到这一行：前面的流程会无条件跳过它，执行时不会执行。', problem:false };
    return null;
}

/** 删除前确认：这些行是别的行的跳转目标时先问；确认后调用方把指向它们的跳转改回继续下一行。 */
function tcConfirmDeleteJumpTarget(ids, doc = tc.doc) {
    const sources = doc.nodes.filter(n => !ids.has(n.nodeId) && (ids.has(n.flow?.jumpTo) || ids.has(n.lanes?.yesJumpTo) || ids.has(n.lanes?.noJumpTo)));
    if (!sources.length) return true;
    const target = [...ids].map(id => tcRowLabel(id, doc)).filter(Boolean)[0] || '';
    return confirm(`「${target}」是 ${sources.map(s => tcRowLabel(s.nodeId, doc) || tcNodeName(s)).join('、')} 的跳转目标。\n删除后，这些跳转会改回「继续下一行」。\n\n确定删除吗？`);
}
function tcClearJumpsTo(ids, doc = tc.doc) {
    for (const n of doc.nodes) {
        if (n.flow && ids.has(n.flow.jumpTo)) delete n.flow.jumpTo;
        if (n.lanes) { if (ids.has(n.lanes.yesJumpTo)) delete n.lanes.yesJumpTo; if (ids.has(n.lanes.noJumpTo)) delete n.lanes.noJumpTo; }
    }
}
