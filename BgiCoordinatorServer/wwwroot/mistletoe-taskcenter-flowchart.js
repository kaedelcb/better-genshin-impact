// 槲寄生 · 任务中心流程图（只读，与 PC TaskCenterFlowChartWindow 同口径）。
// 计划行自上而下；流程画成带框的步骤链（内部判断是/否、触发器触发链）；车道先判断再分两条路线后汇合。
// 按该计划最近一次运行的本轮记录点亮走过的节点与路线，没走的路线灰显。只读运行记录，不写计划。
const TC_FC_GEO = { nw:232, nh:70, vg:40, hg:32, bg:40, pad:30, fp:14, ft:26 };
const TC_FC_EDGE = { n:'#7f7aa2', yes:'#6fd8ce', no:'#e88a6f', fire:'#ffc857', jump:'#b39ddb' };
const TC_FC_RUN = { running:'执行中…', success:'✓ 完成', failed:'✗ 未完成', skipped:'已跳过', jumped:'本轮跳过（跳转）', condTrue:'→ 是', condFalse:'→ 否' };
const TC_FC_EXPAND_KEY = 'mistletoe.taskcenter.autoExpandBranch';
const tcfc = { view:{ zoom:1, x:0, y:0, w:0, h:0, mode:'fit' }, key:'', laneSeen:new Map() };

/** 执行时展开走过的分支（本浏览器保存，默认开）。 */
function tcAutoExpand() { try { return localStorage.getItem(TC_FC_EXPAND_KEY) !== '0'; } catch { return true; } }
function tcSetAutoExpand(on) { try { localStorage.setItem(TC_FC_EXPAND_KEY, on ? '1' : '0'); } catch { /* 禁用存储时只本次生效 */ } }

/** 最近一次运行在本轮走过的路径（同 PC TaskCenterRunTrace）。 */
function tcRunTrace(doc = tc.doc) {
    const empty = { run:null, round:0, active:false, node:() => '', step:() => '', lane:() => null, key:'' };
    if (!doc || !tc.selected) return empty;
    const runs = [...tcActive(), ...(mtPick(tc.list, 'history') || [])].filter(r => mtPick(r, 'workflowId') === tc.selected)
        .sort((a, b) => String(mtPick(b, 'createdAt')).localeCompare(String(mtPick(a, 'createdAt'))));
    const run = runs[0]; if (!run) return empty;
    const outs = run.nodeOutcomes || [], round = run.cursor?.loopIteration ?? Math.max(0, ...outs.map(o => o.loopIteration || 0));
    const st = tcState(run), active = ['Planned','Running','Waiting','Paused','Completing','LocalWaitParking','OnlineHoeingParking'].includes(st);
    const nodes = new Map(), steps = new Map();
    for (const o of outs.filter(o => (o.loopIteration || 0) === round))
        nodes.set(o.nodeId, { succeeded:'success', branchYes:'condTrue', branchNo:'condFalse', skippedUser:'skipped', skippedFilter:'skipped' }[o.result] || (o.result ? 'failed' : ''));
    if (active && run.cursor && (run.cursor.loopIteration || 0) === round && !nodes.has(run.cursor.nodeId)) nodes.set(run.cursor.nodeId, 'running');
    const bag = run.extensionData || run;
    for (const n of doc.nodes || []) {
        if (!n.flow && !n.lanes?.decision) continue;
        const prefix = `taskFlowStep:${n.nodeId}:${round}:`;
        for (const [k, v] of Object.entries(bag)) {
            if (!k.startsWith(prefix) || typeof v !== 'string') continue;
            const id = k.slice(prefix.length); if (id.includes(':')) continue;
            const s = { done:'success', yes:'condTrue', conditionYes:'condTrue', no:'condFalse', conditionNo:'condFalse', waiting:active ? 'running' : 'failed', executing:active ? 'running' : 'failed' }[v];
            if (s) steps.set(n.nodeId + '/' + id, s);
        }
        // 内部判断执行完后检查点改写为 done：按哪一支子链留下检查点推出所走分支。
        const touched = (owner, list) => (list || []).some(s => steps.has(owner + '/' + s.id) || touched(owner, s.trueSteps) || touched(owner, s.falseSteps));
        const resolve = (owner, list) => (list || []).forEach(s => {
            ['trueSteps','falseSteps','fireSteps','beforeAttemptSteps'].forEach(k => resolve(owner, s[k]));
            if (!MT_COND.has(s.kind) || steps.get(owner + '/' + s.id) !== 'success') return;
            const y = touched(owner, s.trueSteps), no = touched(owner, s.falseSteps);
            if (y !== no) steps.set(owner + '/' + s.id, y ? 'condTrue' : 'condFalse');
        });
        for (const f of [n.flow, n.lanes?.decision]) if (f) resolve(n.nodeId, f.steps);
    }
    const ownerOf = lane => lane.lanes?.decision ? lane.nodeId : lane.lanes?.sourceFlowNodeId || lane.nodeId;
    // 本轮被跳转跳过的行（与 PC TaskCenterRunTrace.MarkJumpedOver 同规则）：跳转来源本轮已实际经过，
    // 来源与目标之间本轮没有结果的主计划行算被跳过；车道成员随所在车道一起计。
    const jumped = new Set();
    if (typeof tcJumpOrder === 'function') {
        const order = tcJumpOrder(doc), byId = new Map((doc.nodes || []).map(n => [n.nodeId, n]));
        const mark = (sourceId, target) => {
            const resolved = target && tcJumpResolve(doc, target); if (!resolved) return;
            const from = order.indexOf(sourceId), to = order.indexOf(resolved); if (from < 0 || to <= from) return;
            for (let i = from + 1; i < to; i++) {
                const id = order[i]; if (nodes.has(id)) continue; jumped.add(id);
                const row = byId.get(id); if (row?.lanes) for (const m of [...(row.lanes.yesNodeIds || []), ...(row.lanes.noNodeIds || [])]) if (!nodes.has(m)) jumped.add(m);
            }
        };
        for (const id of order) {
            const n = byId.get(id); if (!n) continue;
            if (n.kind === 'control.flow' && nodes.get(id) === 'success') mark(id, n.flow?.jumpTo);
            if (!n.lanes?.decision) continue;
            const chose = ({ condTrue:true, condFalse:false })[nodes.get(id)]; if (chose === undefined) continue;
            const route = (chose ? n.lanes.yesNodeIds : n.lanes.noNodeIds) || [];
            const last = route[route.length - 1];
            if (!route.length || nodes.has(last) && nodes.get(last) !== 'running') mark(id, chose ? n.lanes.yesJumpTo : n.lanes.noJumpTo);
        }
    }
    return { run, round, active, state:st,
        node:id => jumped.has(id) ? 'jumped' : nodes.get(id) || '', step:(owner, id) => steps.get(owner + '/' + id) || '',
        jumped:id => jumped.has(id),
        lane:lane => ({ condTrue:true, condFalse:false })[nodes.get(ownerOf(lane))] ?? null,
        key:`${run.runId}|${run.recordRevision}|${st}|${round}|${[...nodes].join(',')}|${[...steps].join(',')}|${[...jumped].join(',')}` };
}

/** 任务卡摘要：日期 · 账号/UID · 失败处理 · 执行时段（与 PC 同口径）。 */
function tcFcTaskSummary(doc, n) {
    if (!tcEnabled(n)) return '已停用，执行时跳过';
    const acc = tcStrat(n, 'prerequisite.account'), fail = tcStrat(n, 'execution.failure');
    const parts = [tcStrat(n, 'condition.weekdays') ? '星期 ' + tcWeekText(n).replace(/\//g, '') : '每天', acc ? '账号 ' + tcMask(acc.uid) : '未绑定账号'];
    if (fail) parts.push(fail.onFailure === 'continue' ? '失败继续' : '失败停止');
    if (n.timeWindow) parts.push('时段 ' + tcWindowText(n) + (tcWindowOpts(n) ? '（' + tcWindowOpts(n) + '）' : '') + (doc.nodeTimeWindowEnabled ? '' : '（总开关关闭，不生效）'));
    return parts.join(' · ');
}

function tcFcLayout(doc, trace) {
    const G = TC_FC_GEO, empty = () => ({ cards:[], edges:[], labels:[], frames:[], pills:[] });
    const merge = (dst, b, dx, dy, dim) => {
        for (const k of ['cards','labels','frames','pills']) for (const o of b[k]) dst[k].push({ ...o, x:o.x + dx, y:o.y + dy, dim:o.dim || dim });
        for (const e of b.edges) dst.edges.push({ ...e, pts:e.pts.map(([a, c]) => [a + dx, c + dy]), dim:e.dim || dim });
    };
    const pill = (t, cls = '') => { const w = Math.max(96, t.length * 13 + 28); return { ...empty(), pills:[{ x:0, y:0, w, h:26, t, cls }], w, h:26, topX:w / 2, botX:w / 2, loose:null }; };
    const card = c => ({ ...empty(), cards:[{ ...c, x:0, y:0 }], w:G.nw, h:G.nh, topX:G.nw / 2, botX:G.nw / 2, loose:null });
    function chain(items) {
        const w = Math.max(...items.map(b => b.w)), out = empty(); let y = 0, px = 0, py = 0;
        items.forEach((b, i) => {
            const x = (w - b.w) / 2; merge(out, b, x, y);
            if (i > 0) { const tx = x + b.topX; out.edges.push({ pts:Math.abs(px - tx) < .5 ? [[px, py], [tx, y]] : [[px, py], [px, (py + y) / 2], [tx, (py + y) / 2], [tx, y]], c:'n', arrow:true }); }
            px = x + b.botX; py = y + b.h;
            if (b.loose?.length && i < items.length - 1) { const my = y + b.h + G.vg * .55; for (const [ax, ay] of b.loose) out.edges.push({ pts:[[x + ax, y + ay], [x + ax, my], [px, my]], c:'n' }); py = my; }
            y += b.h + G.vg;
        });
        y -= G.vg; const f = items[0], l = items[items.length - 1], lx = (w - l.w) / 2;
        return { ...out, w, h:y, topX:(w - f.w) / 2 + f.topX, botX:lx + l.botX, loose:l.loose?.map(([a, c]) => [lx + a, y - l.h + c]) || null };
    }
    function split(head, yes, no, yl, nl, chosen) {
        const tot = yes.w + G.hg + no.w, w = Math.max(head.w, tot), cx = w / 2, by = head.h + G.bg, tx = cx - tot / 2, fx = tx + yes.w + G.hg, sy = head.h + G.bg / 2, out = empty();
        merge(out, head, cx - head.w / 2, 0);
        merge(out, yes, tx, by, chosen === false); merge(out, no, fx, by, chosen === true);
        for (const [b, bx, c, t, isYes] of [[yes, tx, 'yes', yl, true], [no, fx, 'no', nl, false]]) {
            const to = bx + b.topX, lit = chosen === isYes, dim = chosen !== null && !lit;
            out.edges.push({ pts:Math.abs(cx - to) < .5 ? [[cx, head.h], [to, by]] : [[cx, head.h], [cx, sy], [to, sy], [to, by]], c, arrow:true, dim, lit });
            out.labels.push({ x:isYes ? to - 8 - t.length * 12 : to + 7, y:by - 22, t, c, dim, lit });
        }
        return { ...out, w, h:by + Math.max(yes.h, no.h), topX:cx, botX:cx, loose:[[tx + yes.botX, by + yes.h], [fx + no.botX, by + no.h]] };
    }
    function side(c, fire, t) {
        const out = empty(); merge(out, c, 0, 0); const fx = c.w + G.hg + 40; merge(out, fire, fx, 0);
        out.edges.push({ pts:[[c.w, G.nh / 2], [Math.max(c.w + 10, fx + fire.topX - G.nw / 2), G.nh / 2]], c:'fire', dash:true, arrow:true });
        out.labels.push({ x:c.w + 6, y:G.nh / 2 - 22, t, c:'fire' });
        return { ...out, w:fx + fire.w, h:Math.max(c.h, fire.h), topX:c.topX, botX:c.botX, loose:null };
    }
    function frame(title, inner, color, state, nodeId, stepId, off) {
        const w = Math.max(inner.w, G.nw) + G.fp * 2, h = inner.h + G.fp * 2 + G.ft, out = empty();
        out.frames.push({ x:0, y:0, w, h, title, color, state, nodeId, stepId, off });
        merge(out, inner, (w - inner.w) / 2, G.fp + G.ft);
        return { ...out, w, h, topX:w / 2, botX:w / 2, loose:null };
    }
    function steps(owner, list, exitId) {
        return chain(list.map(s => {
            const info = MT_KIND[s.kind] || { icon:'▶', color:'#90A4AE', name:s.kind }, state = trace.step(owner, s.id);
            const type = s.id === exitId ? '路线选择条件' : MT_COND.has(s.kind) ? '判断' : MT_FIRE.has(s.kind) ? '触发器' : '动作';
            let sum = tcFlowSummary(s); if ((s.beforeAttemptSteps || []).length) sum += `；任务每次开始前执行 ${s.beforeAttemptSteps.length} 个动作`;
            const c = card({ icon:info.icon, color:info.color, name:tcFlowLabel(s), type, sum, state, off:s.enabled === false, nodeId:owner, stepId:s.id });
            if (MT_COND.has(s.kind) && s.id !== exitId) {
                const y = (s.trueSteps || []).length ? steps(owner, s.trueSteps) : pill('（空）'), n = (s.falseSteps || []).length ? steps(owner, s.falseSteps) : pill('（空）');
                return split(c, y, n, '是', '否', state === 'condTrue' ? true : state === 'condFalse' ? false : null);
            }
            if (MT_FIRE.has(s.kind) && (s.fireSteps || []).length) return side(c, steps(owner, s.fireSteps), s.kind === 'timerTrigger' ? '⏰ 到点执行' : s.kind === 'logTrigger' ? '📜 日志出现时' : '🐕 触发执行');
            return c;
        }));
    }
    const byId = new Map((doc.nodes || []).map(n => [n.nodeId, n]));
    const task = (n, label) => card({ icon:'▶', color:'#48C87E', name:label, type:TC_KIND[n.kind] || n.kind, sum:tcFcTaskSummary(doc, n), state:trace.node(n.nodeId), off:!tcEnabled(n), nodeId:n.nodeId });
    const numbers = typeof tcRowNumbers === 'function' ? tcRowNumbers(doc) : new Map();
    const mainRows = tcMainNodes(doc);
    // 被跳转跳过的行整块灰显（与计划表、PC 同口径）。
    const dimIfJumped = (b, n) => trace.jumped?.(n.nodeId) ? { ...b, cards:b.cards.map(c => ({ ...c, dim:true })), frames:b.frames.map(f => ({ ...f, dim:true })),
        edges:b.edges.map(e => ({ ...e, dim:true })), labels:b.labels.map(l => ({ ...l, dim:true })), pills:b.pills.map(p => ({ ...p, dim:true })) } : b;
    const rows = mainRows.map((n, i) => dimIfJumped((() => {
        const no = numbers.get(n.nodeId) || String(i + 1).padStart(2, '0');
        if (n.flow) return frame(`${no}  流程 · ${tcNodeName(n)}${TC_FC_RUN[trace.node(n.nodeId)] ? '  ' + TC_FC_RUN[trace.node(n.nodeId)] : ''}`,
            (n.flow.steps || []).length ? steps(n.nodeId, n.flow.steps, n.flow.exitStepId) : pill('暂无步骤'), '#B39DDB', trace.node(n.nodeId), n.nodeId, null, !tcEnabled(n));
        if (n.lanes) {
            const l = n.lanes, owner = l.decision ? n.nodeId : l.sourceFlowNodeId, decision = l.decision || byId.get(l.sourceFlowNodeId)?.flow;
            const exit = decision?.steps?.find(s => s.id === decision.exitStepId), choice = trace.lane(n);
            const head = card({ icon:'⑂', color:'#E8C96D', name:`${no}  ${tcNodeName(n)}`, type:'车道', sum:exit ? '判断：' + tcFlowSummary(exit) : '还没有设置选择条件',
                state:choice === true ? 'condTrue' : choice === false ? 'condFalse' : trace.node(n.nodeId), off:!tcEnabled(n), nodeId:n.nodeId });
            const branch = ids => ids?.length ? chain(ids.map((id, j) => byId.has(id) ? task(byId.get(id), `${numbers.get(id) || String(j + 1).padStart(2, '0')}  ${tcNodeName(byId.get(id))}`) : pill('任务引用缺失'))) : pill('（空，直接继续）');
            const sp = split(head, branch(l.yesNodeIds), branch(l.noNodeIds), '满足 → ' + (l.yesName === '是' ? '路线 1' : l.yesName || '路线 1'), '其他 → ' + (l.noName === '否' ? '路线 2' : l.noName || '路线 2'), choice);
            const before = (decision?.steps || []).filter(s => s.id !== decision.exitStepId);
            return before.length ? chain([frame('判断前的步骤', steps(owner, before, null), '#9C97C0', '', owner, before[0].id), sp]) : sp;
        }
        return task(n, `${no}  ${tcNodeName(n)}`);
    })(), n));
    const start = pill(trace.run ? `▶ 开始 · 第 ${trace.round + 1} 轮` : '▶ 开始', 'gold'), end = pill(doc.loop ? '↻ 进入下一轮' : '■ 计划结束', 'gold');
    const items = [start, ...(rows.length ? rows : [pill('（计划表还是空的）')]), end];
    const g = chain(items);
    // 跳转连线：与 chain 同公式算出各行块位置，从来源右侧沿图右边各自独立的竖道连到目标右侧，不横穿卡片。
    if (rows.length && typeof tcJumpResolve === 'function') {
        const tops = []; let y = 0; for (const b of items) { tops.push(y); y += b.h + G.vg; }
        const rect = i => { const b = items[i + 1]; return { x:(g.w - b.w) / 2, y:tops[i + 1], w:b.w, h:b.h }; };
        const jumps = [];
        mainRows.forEach((n, i) => {
            const add = (target, label, lit) => {
                const resolved = tcJumpResolve(doc, target); if (!resolved) return;
                const to = mainRows.findIndex(r => r.nodeId === resolved || r.lanes?.sourceFlowNodeId === resolved);
                if (to > i) jumps.push({ from:i, to, label, lit });
            };
            if (n.kind === 'control.flow' && n.flow?.jumpTo && !n.flow.exitStepId) add(n.flow.jumpTo, '↪ ' + (numbers.get(n.flow.jumpTo) || ''), trace.node(n.nodeId) === 'success');
            if (n.lanes?.decision) {
                const chose = trace.lane(n);
                if (n.lanes.yesJumpTo) add(n.lanes.yesJumpTo, '满足 ↪ ' + (numbers.get(n.lanes.yesJumpTo) || ''), chose === true);
                if (n.lanes.noJumpTo) add(n.lanes.noJumpTo, '其他 ↪ ' + (numbers.get(n.lanes.noJumpTo) || ''), chose === false);
            }
        });
        jumps.forEach((j, k) => {
            const a = rect(j.from), b = rect(j.to), lane = g.w + 18 + k * 22;
            const y1 = a.y + Math.min(a.h, G.nh) / 2, y2 = b.y + Math.min(b.h, G.nh) / 2;
            g.edges.push({ pts:[[a.x + a.w + 4, y1], [lane, y1], [lane, y2], [b.x + b.w + 4, y2]], c:'jump', dash:true, arrow:true, lit:j.lit });
            g.labels.push({ x:a.x + a.w + 6, y:y1 - 20, t:j.label, c:'jump', lit:j.lit });
        });
        if (jumps.length) g.w += 18 + jumps.length * 22 + 10;
    }
    return g;
}

function tcFlowChart() {
    if (!tc.doc) { mtToast('请先选择一张计划表，再查看流程图。', true); return; }
    const dialog = mtDialog('tcFlowChart');
    dialog.classList.add('tc-fc-dialog');
    dialog.innerHTML = `<header class="mt-dialog-head"><div><h3 class="mt-break" id="tcFcTitle"></h3><p class="dodoco-hint mt-break" id="tcFcRun"></p></div><button class="dd-btn dd-btn-sm" data-close>关闭</button></header>
      <div id="tcFcStage" class="mt-stage tc-fc-stage" tabindex="0" aria-label="任务流程图，可拖动平移、滚轮或双指缩放"><div id="tcFcWorld" class="mt-world"></div>
        <div class="mt-legend"><span><i class="yes"></i>是 / 满足</span><span><i class="no"></i>否 / 其他</span><span><i class="fire"></i>触发执行</span></div>
        <div class="mt-zoom"><button data-zoom="out" aria-label="缩小">－</button><span id="tcFcZoom">100%</span><button data-zoom="in" aria-label="放大">＋</button><button data-zoom="fit">适应</button></div></div>`;
    dialog.querySelector('[data-close]').onclick = () => dialog.close();
    tcfc.key = ''; tcfc.view.mode = 'fit';
    if (!dialog.open) dialog.showModal();
    tcBindFlowChartStage();
    tcRenderFlowChart();
}

/** 结构或运行路径变了才重画（轮询时调用）。 */
function tcRenderFlowChart() {
    const dialog = mtEl('tcFlowChart'), world = mtEl('tcFcWorld'); if (!dialog?.open || !world) return;
    const doc = tc.doc; if (!doc) { dialog.close(); return; }
    const trace = tcRunTrace(doc), key = JSON.stringify(doc.nodes) + '|' + doc.loop?.mode + '|' + doc.name + '|' + doc.nodeTimeWindowEnabled + '|' + trace.key;
    if (key === tcfc.key) return; tcfc.key = key;
    mtEl('tcFcTitle').textContent = `🗺 任务流程图（只读） · ${doc.name || ''}`;
    mtEl('tcFcRun').textContent = !trace.run ? '这张计划表还没有运行记录；运行后会点亮实际走过的路径。'
        : `最近一次运行：${TC_STATE_TEXT[trace.state] || trace.state} · 第 ${trace.round + 1} 轮${trace.active ? ' · 实时刷新' : ''}　亮色为本轮走过的节点与路线，灰色为没有走的路线。`;
    const g = tcFcLayout(doc, trace), { pad } = TC_FC_GEO, W = g.w + pad * 2, H = g.h + pad * 2;
    const path = e => e.pts.map((p, i) => (i ? 'L' : 'M') + (p[0] + pad) + ',' + (p[1] + pad)).join(' ');
    const markers = Object.entries(TC_FC_EDGE).map(([k, c]) => `<marker id="tc-ar-${k}" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="7" markerHeight="7" orient="auto"><path d="M0,0L8,4L0,8z" fill="${c}"/></marker>`).join('');
    const data = (o, cls) => o.nodeId ? ` data-fc-node="${mte(o.nodeId)}"${o.stepId ? ` data-fc-step="${mte(o.stepId)}"` : ''}` : '';
    world.style.width = W + 'px'; world.style.height = H + 'px';
    world.innerHTML = g.frames.map(f => `<div class="tc-fc-frame${f.state ? ' st-' + f.state : ''}${f.dim ? ' dim' : ''}${f.off ? ' off' : ''}" style="left:${f.x + pad}px;top:${f.y + pad}px;width:${f.w}px;height:${f.h}px;--c:${f.color}"><button type="button" class="tc-fc-frame-title"${data(f)} title="点击回到计划表定位">${mte(f.title)}</button></div>`).join('')
        + `<svg class="mt-edges" width="${W}" height="${H}" aria-hidden="true"><defs>${markers}</defs>`
        + g.edges.map(e => `<path d="${path(e)}" stroke="${TC_FC_EDGE[e.c]}" stroke-opacity="${e.dim ? .22 : e.lit ? 1 : .72}" stroke-width="${e.lit ? 2.6 : 1.6}" fill="none"${e.dash ? ' stroke-dasharray="6 5"' : ''}${e.arrow ? ` marker-end="url(#tc-ar-${e.c})"` : ''}/>`).join('') + '</svg>'
        + g.labels.map(l => `<span class="mt-elabel${l.dim ? ' dim' : ''}${l.lit ? ' lit' : ''}" style="left:${l.x + pad}px;top:${l.y + pad}px;color:${TC_FC_EDGE[l.c]}">${mte(l.t)}</span>`).join('')
        + g.pills.map(p => `<span class="${p.cls === 'gold' ? 'mt-start tc-fc-pill' : 'mt-ph'}${p.dim ? ' dim' : ''}" style="left:${p.x + pad}px;top:${p.y + pad}px;width:${p.w}px;height:${p.h}px">${mte(p.t)}</span>`).join('')
        + g.cards.map(c => `<button type="button" class="mt-node${c.state ? ' st-' + c.state : ''}${c.dim ? ' dim' : ''}${c.off ? ' off' : ''}"${data(c)} style="left:${c.x + pad}px;top:${c.y + pad}px;width:${TC_FC_GEO.nw}px;height:${TC_FC_GEO.nh}px;--c:${c.color}" title="${mte(c.name + '\n' + c.sum)}">`
            + `${c.state && (!c.dim || c.state === 'jumped') ? `<span class="mt-node-st">${TC_FC_RUN[c.state]}</span>` : c.off ? '<span class="mt-node-st">已停用</span>' : ''}`
            + `<span class="mt-ic">${c.icon}</span><span class="mt-node-tx"><b>${mte(c.name)}</b><small>${mte(c.type)} · ${mte(c.sum)}</small></span></button>`).join('');
    tcfc.view.w = W; tcfc.view.h = H;
    if (tcfc.view.mode === 'fit') tcFcFit(); else tcFcApply();
}

function tcFcApply() {
    const world = mtEl('tcFcWorld'); if (!world) return;
    world.style.transform = `translate(${tcfc.view.x}px,${tcfc.view.y}px) scale(${tcfc.view.zoom})`;
    const z = mtEl('tcFcZoom'); if (z) z.textContent = Math.round(tcfc.view.zoom * 100) + '%';
}
function tcFcFit() {
    const r = mtEl('tcFcStage')?.getBoundingClientRect(); if (!r || r.width < 40 || !tcfc.view.w) return;
    const z = Math.max(.05, Math.min(1, (r.width - 24) / tcfc.view.w, (r.height - 60) / tcfc.view.h));
    Object.assign(tcfc.view, { zoom:z, x:(r.width - tcfc.view.w * z) / 2, y:12 });
    tcFcApply();
}
function tcFcZoomAt(f, cx, cy) {
    const r = mtEl('tcFcStage')?.getBoundingClientRect(); if (!r) return;
    const z0 = tcfc.view.zoom, z = Math.min(2.5, Math.max(.05, z0 * f)); cx ??= r.width / 2; cy ??= r.height / 2;
    Object.assign(tcfc.view, { x:cx - (cx - tcfc.view.x) * z / z0, y:cy - (cy - tcfc.view.y) * z / z0, zoom:z, mode:'manual' }); tcFcApply();
}
function tcBindFlowChartStage() {
    const stage = mtEl('tcFcStage'); if (!stage) return;
    const pts = new Map(); let start = null, pinch = null;
    stage.onpointerdown = e => {
        if (e.target.closest('.mt-zoom,.mt-legend')) return;
        stage.setPointerCapture(e.pointerId); pts.set(e.pointerId, { x:e.clientX, y:e.clientY });
        const hit = e.target.closest('[data-fc-node]');
        if (pts.size === 1) start = { x:e.clientX, y:e.clientY, vx:tcfc.view.x, vy:tcfc.view.y, moved:false, node:hit?.dataset.fcNode, step:hit?.dataset.fcStep };
        if (pts.size === 2) { const [a, b] = [...pts.values()]; pinch = { d:Math.hypot(a.x - b.x, a.y - b.y), z:tcfc.view.zoom }; start = null; }
    };
    stage.onpointermove = e => {
        if (!pts.has(e.pointerId)) return; pts.set(e.pointerId, { x:e.clientX, y:e.clientY });
        if (pinch && pts.size === 2) { const [a, b] = [...pts.values()], r = stage.getBoundingClientRect(); tcFcZoomAt(pinch.z * Math.hypot(a.x - b.x, a.y - b.y) / pinch.d / tcfc.view.zoom, (a.x + b.x) / 2 - r.left, (a.y + b.y) / 2 - r.top); return; }
        if (!start) return; const dx = e.clientX - start.x, dy = e.clientY - start.y;
        if (!start.moved && Math.hypot(dx, dy) < 5) return;
        start.moved = true; stage.classList.add('panning');
        Object.assign(tcfc.view, { x:start.vx + dx, y:start.vy + dy, mode:'manual' }); tcFcApply();
    };
    const end = e => {
        pts.delete(e.pointerId); stage.classList.remove('panning'); if (pts.size < 2) pinch = null;
        if (start && !start.moved && start.node && e.type === 'pointerup') { const { node, step } = start; mtEl('tcFlowChart')?.close(); tcFcLocate(node, step); }
        if (!pts.size) start = null;
    };
    stage.onpointerup = end; stage.onpointercancel = end;
    stage.onwheel = e => { e.preventDefault(); const r = stage.getBoundingClientRect(); tcFcZoomAt(e.deltaY < 0 ? 1.12 : 1 / 1.12, e.clientX - r.left, e.clientY - r.top); };
    stage.querySelectorAll('[data-zoom]').forEach(b => b.onclick = () => { const k = b.dataset.zoom; if (k === 'in') tcFcZoomAt(1.2); else if (k === 'out') tcFcZoomAt(1 / 1.2); else { tcfc.view.mode = 'fit'; tcFcFit(); } });
}

/** 点击图上节点：回到计划表选中该行；流程步骤打开流程编辑并选中；车道成员展开所在车道。 */
function tcFcLocate(nodeId, stepId) {
    if (stepId) { tcLocateFlowStep(tc.selected, nodeId, stepId); return; }
    const lane = tc.doc?.nodes?.find(n => n.lanes && ((n.lanes.yesNodeIds || []).includes(nodeId) || (n.lanes.noNodeIds || []).includes(nodeId)));
    tc.selectedNode = lane ? lane.nodeId : nodeId; tcRenderNodes();
    const el = mtEl('tcNodes')?.querySelector(`[data-composition="${CSS.escape(tc.selectedNode)}"],[data-node="${CSS.escape(tc.selectedNode)}"],[data-resource="${CSS.escape(nodeId)}"]`);
    if (el) { if (el.tagName === 'DETAILS') el.open = true; el.scrollIntoView({ block:'center', behavior:'smooth' }); el.classList.add('tc-fc-flash'); setTimeout(() => el.classList.remove('tc-fc-flash'), 1600); }
}

/** 车道判断出结果时（开关打开）展开该车道；每次运行的每轮只自动展开一次，用户收起后不再抢。 */
function tcAutoExpandLanes(host, doc) {
    if (!tcAutoExpand() || !doc) return;
    const trace = tcRunTrace(doc); if (!trace.run) return;
    for (const n of doc.nodes || []) {
        if (!n.lanes || trace.lane(n) === null) continue;
        const seen = `${trace.run.runId}|${trace.round}|${n.nodeId}`; if (tcfc.laneSeen.has(seen)) continue;
        tcfc.laneSeen.set(seen, true);
        const el = host.querySelector(`details[data-composition="${CSS.escape(n.nodeId)}"]`); if (el) el.open = true;
    }
}

/** 计划表行的运行标志（最近一次运行本轮；与流程图、PC 计划表同口径）。车道按选中的路线显示。 */
function tcRunBadge(n, trace) {
    if (!trace?.run || !n) return '';
    const choice = n.lanes ? trace.lane(n) : null;
    const st = choice === true || choice === false ? 'lane' : trace.node(n.nodeId);
    const waitAt = st === 'running' && trace.run.wait?.kind === 'timeWindow.start' && trace.run.cursor?.nodeId === n.nodeId && tcState(trace.run) === 'Waiting' ? String(trace.run.wait.nextTriggerAt || '').slice(11, 16) : '';
    const text = choice === true ? '→ 满足条件' : choice === false ? '→ 其他情况' : waitAt ? '等到 ' + waitAt : { running:'执行中…', success:'✓ 已完成', failed:'✗ 未完成', skipped:'已跳过', jumped:'本轮跳过（跳转）', condTrue:'→ 满足', condFalse:'→ 其他' }[st];
    return text ? `<i class="tc-run-badge st-${st}" title="最近一次运行本轮的执行状态">${text}</i>` : '';
}
