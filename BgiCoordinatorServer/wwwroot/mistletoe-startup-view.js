// 启动中心界面：运行卡片 → 方案与工具栏 → 触发器 → 编辑区（大纲 | 画布 | 节点设置）。
function mtOwner() { return !!mt.target && (mt.target.local || mt.identity?.owner === true); }

function mtRenderStartup(host) {
    if (!su.loaded) {
        host.innerHTML = `<section class="dd-card dd-empty">${su.loading ? '正在读取执行端启动中心…' : '尚未读取启动中心'}</section>`;
        if (!su.loading) mtLoadStartup(true);
        return;
    }
    host.innerHTML = `
      <section id="mtRunCard" class="dd-card mt-run"></section>
      <section class="dd-card mt-tools">
        <div class="mt-scheme">
          <label class="dd-field mt-scheme-select"><span>方案</span><select id="mtSchemeSelect"></select></label>
          <div class="dd-actions"><button id="mtSchemeRestore" class="dd-btn dd-btn-sm">恢复</button><button id="mtSchemeExport" class="dd-btn dd-btn-sm">导出</button><button id="mtSchemeDelete" class="dd-btn dd-btn-sm dd-btn-danger">删除</button></div>
          <label class="dd-field mt-scheme-name"><span>保存当前流程为方案</span><input id="mtSchemeName" type="text" maxlength="60" placeholder="方案名，同名覆盖"></label>
          <div class="dd-actions"><button id="mtSchemeSave" class="dd-btn dd-btn-sm">保存方案</button><button id="mtSchemeImport" class="dd-btn dd-btn-sm">导入方案</button></div>
        </div>
        <div class="mt-tool-row">
          <label class="dd-check"><input id="mtAutoExpand" type="checkbox">执行时展开走过的分支</label>
          <span id="mtSaveState" class="mt-save-state"></span>
          <div class="dd-actions"><button id="mtClearStates" class="dd-btn dd-btn-sm dd-btn-quiet">清除路径显示</button><button id="mtReloadStartup" class="dd-btn dd-btn-sm">读取最新启动设置</button></div>
        </div>
      </section>
      <section id="mtTriggers"></section>
      <section id="mtEditor" class="mt-editor${su.selected ? ' has-selection' : ''}" data-pane="${su.mobilePane}">
        <nav class="mt-pane-switch" aria-label="编辑视图"><button data-pane="outline">流程大纲</button><button data-pane="canvas">流程图</button></nav>
        <div class="dd-card mt-outline"><header class="mt-outline-head"><div><h2>启动流程</h2><p class="dodoco-hint">从上到下依次执行；条件节点分「是 / 否」支路，跑完回到主流程。拖 ⠿ 排序</p></div><button id="mtExpandAll" class="dd-btn dd-btn-sm dd-btn-quiet">展开 / 收起分支</button></header><div id="mtOutline" class="mt-outline-body"></div></div>
        <div id="mtStage" class="mt-stage" tabindex="0" aria-label="流程图，可拖动平移、滚轮或双指缩放"><div id="mtWorld" class="mt-world"></div>
          <div class="mt-far-hint" aria-hidden="true">缩略视图 · 点任一节点放大到该处</div>
          <div class="mt-legend"><span><i class="yes"></i>是</span><span><i class="no"></i>否</span><span><i class="fire"></i>触发执行</span></div>
          <div class="mt-zoom"><button data-zoom="out" aria-label="缩小">－</button><span id="mtZoomText">100%</span><button data-zoom="in" aria-label="放大">＋</button><button data-zoom="fit">适应</button><button data-zoom="locate" title="定位到正在执行或已选中的节点">定位</button></div></div>
        <aside id="mtInspector" class="dd-card mt-inspector"></aside>
      </section>
      `;
    mtEl('mtSchemeSave').onclick = mtSchemeSave; mtEl('mtSchemeRestore').onclick = mtSchemeRestore;
    mtEl('mtSchemeDelete').onclick = mtSchemeDelete; mtEl('mtSchemeExport').onclick = mtSchemeExport; mtEl('mtSchemeImport').onclick = mtSchemeImport;
    mtEl('mtClearStates').onclick = mtClearRunStates;
    mtEl('mtReloadStartup').onclick = () => { if (su.dirty && !confirm('当前有未保存到执行端的修改，读取最新设置会放弃这些修改。是否继续？')) return; su.dirty = false; mtLoadStartup(true); };
    mtEl('mtAutoExpand').checked = su.config.autoExpandBranch !== false;
    mtEl('mtAutoExpand').onchange = e => { su.config.autoExpandBranch = e.target.checked; mtEdited(); };
    mtEl('mtExpandAll').onclick = () => { const any = su.collapsed?.size; su.collapsed = new Set(); if (!any) mtWalk(su.config.steps, s => { if (MT_COND.has(s.kind)) su.collapsed.add(s.id); }); mtRenderOutline(); };
    mtEl('mtEditor').querySelectorAll('.mt-pane-switch [data-pane]').forEach(b => b.onclick = () => { su.mobilePane = b.dataset.pane; mtEl('mtEditor').dataset.pane = su.mobilePane; if (su.mobilePane === 'canvas') mtRenderCanvas(); mtPaneButtons(); });
    mtPaneButtons();
    mtRenderSchemes(); mtRenderStartupRun(); mtRenderTriggers(); mtRenderSaveState(); mtRenderOutline(); mtRenderCanvas(); mtRenderInspector();
    mtBindStage();
}
function mtPaneButtons() { mtEl('mtEditor')?.querySelectorAll('.mt-pane-switch [data-pane]').forEach(b => b.classList.toggle('active', b.dataset.pane === su.mobilePane)); }
function mtRenderStartupBody() { if (mt.active && mt.tab === 'startup' && mtEl('mtContent') && mtOwner()) mtRenderStartup(mtEl('mtContent')); }
/** 轮询到新状态时只更新运行卡片的实时部分、触发器、大纲状态和画布；设置行和节点设置面板不动，保留输入焦点。 */
function mtRenderStartupLive() { if (!mtEl('mtRunCard')) return; mtRenderStartupRun(); mtRenderTriggers(); if (!su.dragging) mtRenderOutline(); mtRenderCanvas(); }
/** 把大纲里的节点行滚到可见处：电脑上只滚大纲面板自身，不带动整页（运行卡片保持在原位）；手机上大纲不是滚动区，才滚整页。 */
function mtRevealRow(id) {
    const body = mtEl('mtOutline'), row = body?.querySelector(`.mt-row[data-id="${CSS.escape(id)}"]`);
    if (!row) return;
    if (body.scrollHeight > body.clientHeight + 1 && getComputedStyle(body).overflowY !== 'visible') {
        const b = body.getBoundingClientRect(), r = row.getBoundingClientRect();
        if (r.top < b.top) body.scrollTop -= b.top - r.top + 8;
        else if (r.bottom > b.bottom) body.scrollTop += r.bottom - b.bottom + 8;
    } else row.scrollIntoView({ block:'nearest' });
}

function mtRenderSchemes() {
    const sel = mtEl('mtSchemeSelect'); if (!sel) return;
    const prev = sel.value;
    sel.innerHTML = su.schemes.length ? su.schemes.map(s => `<option value="${mte(s.name)}">${mte(s.name)} · ${mte(String(s.savedAt || '').replace('T', ' ').slice(0, 16))}</option>`).join('') : '<option value="">方案库为空</option>';
    if (su.schemes.some(s => s.name === prev)) sel.value = prev;
    for (const id of ['mtSchemeRestore', 'mtSchemeExport', 'mtSchemeDelete']) mtEl(id).disabled = !su.schemes.length;
}
function mtRenderSaveState() {
    const el = mtEl('mtSaveState'); if (!el) return;
    el.className = 'mt-save-state' + (su.saveError ? ' is-error' : su.dirty || su.saving ? ' is-dirty' : '');
    el.textContent = su.saving ? '正在保存到执行端…' : su.saveError ? '未保存：' + su.saveError : su.dirty ? '有修改，稍后自动保存' : '已与执行端同步';
}

/** withSettings：同时重绘「启动后自动执行 / 延迟 / 开机启动」设置行（读取、保存开机自启动后才需要）。 */
function mtRenderStartupRun(withSettings) {
    const host = mtEl('mtRunCard'); if (!host) return;
    if (!mtEl('mtRunLive')) { host.innerHTML = '<div id="mtRunLive" class="mt-run-live"></div><div id="mtRunSettings" class="mt-run-settings"></div><div id="mtRunNote"></div>'; withSettings = true; }
    const running = su.isRunning, prompts = su.prompts || [];
    mtEl('mtRunLive').innerHTML = `<div class="mt-run-main">
        <span class="dd-pill${running ? ' mt-pill-run' : ' ok'}">${running ? '● 执行中' : '空闲'}</span>
        <p class="mt-run-text">${mte(su.statusText || (running ? '启动流程正在执行' : '启动流程未在运行'))}</p>
        <div class="dd-actions"><button id="mtRunNow" class="dd-btn dd-btn-primary"${running || mt.busy ? ' disabled' : ''}>▶ 立即执行</button><button id="mtCancelRun" class="dd-btn dd-btn-danger"${running ? '' : ' disabled'}>停止流程</button></div></div>
      ${prompts.map(p => `<div class="mt-prompt"><div><strong>执行端等待人工确认</strong><p>${mte(mtPick(p, 'message'))}</p><small>是 → ${mte(mtPick(p, 'yesPreview') || '（空）')}；否 → ${mte(mtPick(p, 'noPreview') || '（空）')}${mtPick(p, 'remainingSeconds') > 0 ? ` · 剩余约 ${mtPick(p, 'remainingSeconds')} 秒` : ''}</small></div><div class="dd-actions"><button class="dd-btn dd-btn-primary" data-answer="1" data-id="${mte(mtPick(p, 'runtimeId'))}">选择是</button><button class="dd-btn" data-answer="0" data-id="${mte(mtPick(p, 'runtimeId'))}">选择否</button></div></div>`).join('')}`;
    mtEl('mtRunNow').onclick = mtRunStartup; mtEl('mtCancelRun').onclick = mtCancelStartup;
    mtEl('mtRunLive').querySelectorAll('[data-answer]').forEach(b => b.onclick = () => mtAnswerPrompt(b.dataset.id, b.dataset.answer === '1'));
    if (withSettings) mtRenderRunSettings();
}
function mtRenderRunSettings() {
    const host = mtEl('mtRunSettings'); if (!host) return;
    const busy = su.autoLaunchSaving ? ' disabled' : '';
    host.innerHTML = `<label class="dd-check"><input id="mtFlowEnabled" type="checkbox"${su.config.enabled ? ' checked' : ''}>启动后自动执行流程</label>
        <label class="dd-field dd-inline"><span>延迟</span><input id="mtDelay" type="number" min="0" max="600" value="${Number(su.config.delaySeconds) || 0}"><span>秒</span></label>
        <label class="dd-check" title="执行端助手随系统开机自启动；与执行端「设置 → 启动策略」是同一个开关"><input id="mtBoot" type="checkbox"${su.autoLaunch?.enabled ? ' checked' : ''}${busy}>开机启动助手</label><label class="dd-field dd-inline"><span>方式</span><select id="mtBootMode"${su.autoLaunch?.enabled && !busy ? '' : ' disabled'}><option value="0"${su.autoLaunch?.mode === 1 ? '' : ' selected'}>弹窗</option><option value="1"${su.autoLaunch?.mode === 1 ? ' selected' : ''}>静默</option></select></label>`;
    const observer = mt.identity && !mt.identity.isExecutor;
    mtEl('mtRunNote').innerHTML = observer ? '<p class="dodoco-hint">该助手是监控端：启动中心只编排本机动作，没有 BGI 的节点会自动跳过；任务中心只在执行端可用。</p>' : '';
    mtEl('mtFlowEnabled').onchange = e => { su.config.enabled = e.target.checked; mtEdited(e.target.checked ? '已开启启动后自动执行；保存后由执行端记录这次开启' : '已关闭启动后自动执行'); };
    mtEl('mtBoot').onchange = e => mtSaveAutoLaunch(e.target.checked, +mtEl('mtBootMode').value);
    mtEl('mtBootMode').onchange = e => mtSaveAutoLaunch(true, +e.target.value);
    mtEl('mtDelay').onchange = e => { su.config.delaySeconds = Math.max(0, Math.min(600, parseInt(e.target.value, 10) || 0)); e.target.value = su.config.delaySeconds; mtEdited(); };
}

function mtRenderTriggers() {
    const host = mtEl('mtTriggers'); if (!host) return;
    const list = su.triggers || [];
    if (!list.length) { host.innerHTML = ''; host.className = ''; return; }
    const icon = { timer:'⏰', watchdog:'🐕', log:'📜' }, title = { timer:'定时中的触发器', watchdog:'盯梢中的电子狗', log:'监听中的日志触发器' };
    host.className = 'dd-card mt-triggers';
    host.innerHTML = `<header class="dd-card-head"><div><h2>运行中的触发器</h2><p class="dodoco-hint">运行态：助手退出后全部失效，重启后由启动流程重新挂载</p></div></header>` +
        list.map((t, i) => { const kind = mtPick(t, 'kind'), note = mtPick(t, 'note'); return `<div class="mt-trigger"><span class="mt-trigger-icon" title="${title[kind] || ''}">${icon[kind] || '•'}</span><div class="mt-trigger-text"><strong>${mte(mtPick(t, 'text'))}</strong>${note ? `<small>${mte(note)}</small>` : ''}</div><div class="dd-actions"><button class="dd-btn dd-btn-sm" data-locate="${i}">定位</button><button class="dd-btn dd-btn-sm" data-flow="${i}">流程</button><button class="dd-btn dd-btn-sm dd-btn-danger" data-cancel="${i}">取消</button></div></div>`; }).join('');
    host.querySelectorAll('[data-locate]').forEach(b => b.onclick = () => { const step = mtPick(list[+b.dataset.locate], 'step'); if (!step || !mtFind(step.id)) return mtToast('这个运行实例不在当前启动流程中，可能挂载自切换方案前的流程；实例本身仍在运行', true); mtSelect(step.id, true); });
    host.querySelectorAll('[data-flow]').forEach(b => b.onclick = () => mtShowFireChain(list[+b.dataset.flow]));
    host.querySelectorAll('[data-cancel]').forEach(b => b.onclick = () => mtCancelTrigger(mtPick(list[+b.dataset.cancel], 'runtimeId'), mtPick(list[+b.dataset.cancel], 'text')));
}
function mtShowFireChain(trigger) {
    const dialog = mtDialog('mtPickDialog'), step = mtPick(trigger, 'step');
    const rows = []; mtWalk(step?.fireSteps || [], (s, chain, index, parent) => { let depth = 0; for (let p = parent; p; p = null) depth++; rows.push(`<li>${MT_KIND[s.kind]?.icon || '•'} <b>${mte(mtDisplayName(s))}</b> <small>${mte(mtSummary(s))}</small></li>`); });
    dialog.innerHTML = `<header class="mt-dialog-head"><h3>${mte(mtPick(trigger, 'text'))}</h3><button class="dd-btn dd-btn-sm" data-close>关闭</button></header><ol class="mt-fire-list">${rows.join('') || '<li class="dd-empty">（空：触发时无动作）</li>'}</ol>`;
    dialog.querySelector('[data-close]').onclick = () => dialog.close(); dialog.showModal();
}

// ---------- 大纲 ----------
function mtRenderOutline() {
    const host = mtEl('mtOutline'); if (!host) return;
    su.collapsed ||= new Set();
    const steps = su.config.steps;
    host.innerHTML = (steps.length ? mtOutlineChain(steps, null, 'steps') : '<p class="dd-empty">还没有节点。点下方「＋ 添加节点」，把启动前要判断的条件、要做的动作排进来；通常以「进入任务中心执行」收尾。</p>')
        + mtAddRow(null, 'steps');
    mtBindOutline(host);
}
function mtAddRow(parent, key) {
    const chain = parent ? parent[key] : su.config.steps, label = mtChainLabel(parent, key), locked = mtChainHasEnd(chain);
    const text = !parent ? '添加节点' : key === 'trueSteps' ? '添加到「是」分支' : key === 'falseSteps' ? '添加到「否」分支' : parent.kind === 'timerTrigger' ? '添加到「到点执行」链' : '添加到「触发执行」链';
    return `<button class="mt-add-row" data-add-parent="${parent ? mte(parent.id) : ''}" data-add-key="${key}" title="${locked ? '此链以「结束流程」收尾，之后不能再添加节点' : '添加到' + mte(label) + '末尾'}"${locked ? ' disabled' : ''}>＋ ${text}</button>`;
}
function mtOutlineChain(chain, parent, key) {
    return chain.map((s, index) => {
        const info = MT_KIND[s.kind] || { icon:'?', color:'#90A4AE', name:s.kind }, st = mtNodeState(s), note = su.states.get(s.id)?.note;
        let html = `<div class="mt-row${s.id === su.selected ? ' selected' : ''}${s.enabled === false ? ' disabled' : ''}${st ? ' st-' + st : ''}" draggable="true" data-id="${mte(s.id)}" title="${mte(mtSummary(s))}">
            <span class="mt-grip" aria-hidden="true">⠿</span><span class="mt-ic" style="--c:${info.color}">${info.icon}</span>
            <span class="mt-row-text"><b>${mte(mtDisplayName(s))}</b><small>${mte(mtSummary(s))}${note ? ' · ' + mte(note) : ''}</small></span>
            ${st ? `<span class="mt-state st-${st}">${MT_RUN_TEXT[st]}</span>` : s.enabled === false ? '<span class="mt-state">已停用</span>' : ''}</div>`;
        const chains = mtChains(s);
        if (chains.length) {
            const taken = mtTaken(s), collapsed = su.collapsed.has(s.id);
            html += `<div class="mt-branches${collapsed ? ' collapsed' : ''}">` + chains.map(([k, name]) => {
                const dim = taken && taken !== k, cls = k === 'trueSteps' ? 'yes' : k === 'falseSteps' ? 'no' : 'fire';
                const hideUntaken = collapsed || (su.config.autoExpandBranch !== false && dim);
                return `<div class="mt-branch ${cls}${dim ? ' dim' : ''}"><div class="mt-branch-head" data-toggle="${mte(s.id)}">${cls === 'yes' ? '✓' : cls === 'no' ? '✗' : '⚡'} ${name}${taken === k ? ' · 本次走这里' : ''}${hideUntaken ? ` <span class="mt-branch-count">（${s[k].length} 个节点，已收起）</span>` : ''}</div>`
                    + (hideUntaken ? '' : (s[k].length ? mtOutlineChain(s[k], s, k) : `<p class="mt-branch-empty">${cls === 'fire' ? '（空：触发时无动作）' : '（空：直接回到主流程）'}</p>`) + mtAddRow(s, k)) + '</div>';
            }).join('') + '</div>';
        }
        return html;
    }).join('');
}
function mtBindOutline(host) {
    host.querySelectorAll('.mt-row').forEach(row => {
        row.onclick = () => mtSelect(row.dataset.id, false);
        row.ondragstart = e => { su.dragging = row.dataset.id; e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', row.dataset.id); row.classList.add('dragging'); };
        row.ondragend = () => { su.dragging = null; host.querySelectorAll('.drop-before,.drop-after').forEach(x => x.classList.remove('drop-before', 'drop-after')); row.classList.remove('dragging'); };
        row.ondragover = e => { if (!su.dragging) return; e.preventDefault(); const r = row.getBoundingClientRect(), after = e.clientY > r.top + r.height / 2; row.classList.toggle('drop-after', after); row.classList.toggle('drop-before', !after); };
        row.ondragleave = () => row.classList.remove('drop-before', 'drop-after');
        row.ondrop = e => { e.preventDefault(); const id = su.dragging; su.dragging = null; const hit = mtFind(row.dataset.id); if (!id || !hit || id === row.dataset.id) return mtRenderOutline(); const after = row.classList.contains('drop-after'); mtMoveStepTo(id, hit.chain, hit.index + (after ? 1 : 0), mtChainLabel(hit.parent, hit.key)); };
    });
    host.querySelectorAll('.mt-add-row').forEach(b => {
        const resolve = () => { const parent = b.dataset.addParent ? mtFind(b.dataset.addParent)?.step : null; return { parent, chain:parent ? parent[b.dataset.addKey] : su.config.steps, label:mtChainLabel(parent, b.dataset.addKey) }; };
        b.onclick = () => { const t = resolve(); mtOpenAdd(t.chain, t.label); };
        b.ondragover = e => { if (su.dragging) { e.preventDefault(); b.classList.add('drop-after'); } };
        b.ondragleave = () => b.classList.remove('drop-after');
        b.ondrop = e => { e.preventDefault(); const id = su.dragging; su.dragging = null; if (!id) return; const t = resolve(); mtMoveStepTo(id, t.chain, t.chain.length, t.label); };
    });
    host.querySelectorAll('[data-toggle]').forEach(h => h.onclick = () => { su.collapsed.has(h.dataset.toggle) ? su.collapsed.delete(h.dataset.toggle) : su.collapsed.add(h.dataset.toggle); mtRenderOutline(); });
}
function mtOpenAdd(chain, label) {
    if (mtChainHasEnd(chain)) return mtToast(`「${label}」已有「结束流程」节点，它是终点，之后不能再添加节点`, true);
    const dialog = mtDialog('mtAddDialog');
    const group = (title, type) => `<h4>${title}</h4><div class="mt-kind-grid">${MT_KINDS.filter(k => k[1] === type).map(([key, , name, icon, color]) => `<button class="mt-kind" data-kind="${key}" style="--c:${color}"><span class="mt-ic">${icon}</span><span>${name}</span></button>`).join('')}</div>`;
    dialog.innerHTML = `<header class="mt-dialog-head"><div><h3>添加节点</h3><p class="dodoco-hint">添加到${label.startsWith('「') ? mte(label) : '「' + mte(label) + '」'}末尾</p></div><button class="dd-btn dd-btn-sm" data-close>取消</button></header>${group('条件判断', 'condition')}${group('动作', 'action')}${group('触发器', 'trigger')}`;
    dialog.querySelector('[data-close]').onclick = () => dialog.close();
    dialog.querySelectorAll('[data-kind]').forEach(b => b.onclick = () => { dialog.close(); mtAddStep(b.dataset.kind, chain, label); });
    dialog.showModal();
}
function mtSelect(id, center) {
    su.selected = id; su.inspectorOpen = true;
    mtEl('mtEditor')?.classList.toggle('has-selection', !!id);
    // 被选中节点所在分支若被收起，先展开，让大纲能看到它。
    const hit = mtFind(id); for (let p = hit?.parent; p; p = mtFind(p.id)?.parent) su.collapsed?.delete(p.id);
    mtRenderOutline(); mtRenderCanvas(); mtRenderInspector();
    mtRevealRow(id);
    if (center) mtCenterOn(id);
}

// ---------- 画布：与 PC FlowChartWindow 同构的树形布局（是左否右；未终止分支汇回主链；触发链虚线挂右侧） ----------
const MT_GEO = { nw:224, nh:66, vg:40, hg:30, bg:40, pad:36 };
function mtLayout(steps) {
    const { nw:NW, nh:NH, vg:VG, hg:HG, bg:BG } = MT_GEO;
    const empty = () => ({ nodes:[], edges:[], labels:[], phs:[] });
    const merge = (dst, b, dx, dy, dim) => {
        for (const n of b.nodes) dst.nodes.push({ ...n, x:n.x + dx, y:n.y + dy, dim:n.dim || dim });
        for (const e of b.edges) dst.edges.push({ ...e, pts:e.pts.map(([a, c]) => [a + dx, c + dy]), dim:e.dim || dim });
        for (const l of b.labels) dst.labels.push({ ...l, x:l.x + dx, y:l.y + dy, dim:l.dim || dim });
        for (const p of b.phs) dst.phs.push({ ...p, x:p.x + dx, y:p.y + dy, dim:p.dim || dim });
    };
    function branch(list) {
        if (list.length) return chain(list);
        return { ...empty(), phs:[{ x:0, y:0, w:96, h:26, t:'（空）' }], w:96, h:26, topX:48, botX:48, loose:null, term:false };
    }
    function node(s) {
        const out = empty();
        if (MT_COND.has(s.kind)) {
            const taken = mtTaken(s), t = branch(s.trueSteps || []), f = branch(s.falseSteps || []);
            const tot = t.w + HG + f.w, w = Math.max(NW, tot), cx = w / 2, by = NH + BG, tx = cx - tot / 2, fx = tx + t.w + HG, sy = NH + BG / 2;
            out.nodes.push({ s, x:cx - NW / 2, y:0 });
            merge(out, t, tx, by, !!taken && taken !== 'trueSteps'); merge(out, f, fx, by, !!taken && taken !== 'falseSteps');
            for (const [b, bx, key, c] of [[t, tx, 'trueSteps', 'yes'], [f, fx, 'falseSteps', 'no']]) {
                const to = bx + b.topX, dim = !!taken && taken !== key, lit = taken === key;
                out.edges.push({ pts:Math.abs(cx - to) < .5 ? [[cx, NH], [to, by]] : [[cx, NH], [cx, sy], [to, sy], [to, by]], c, arrow:true, dim, lit });
                // 「是/否」贴在分支入口竖线旁：是在左侧、否在右侧，紧挨箭头上方。
                out.labels.push({ x:to + (c === 'yes' ? -24 : 7), y:by - 22, t:c === 'yes' ? '是' : '否', c, dim, lit });
            }
            const loose = [];
            if (!t.term) loose.push([tx + t.botX, by + t.h]);
            if (!f.term) loose.push([fx + f.botX, by + f.h]);
            return { ...out, w, h:by + Math.max(t.h, f.h), topX:cx, botX:cx, loose, term:t.term && f.term };
        }
        out.nodes.push({ s, x:0, y:0 }); let w = NW, h = NH;
        if (MT_FIRE.has(s.kind) && (s.fireSteps || []).length) {
            const fb = chain(s.fireSteps), fxo = NW + HG + 46; merge(out, fb, fxo, 0);
            out.edges.push({ pts:[[NW, NH / 2], [fxo + fb.topX - NW / 2, NH / 2]], c:'fire', dash:true, arrow:true });
            out.labels.push({ x:NW + 8, y:NH / 2 - 22, t:s.kind === 'timerTrigger' ? '到点执行' : '触发执行', c:'fire' });
            w = fxo + fb.w; h = Math.max(NH, fb.h);
        }
        return { ...out, w, h, exitY:NH, topX:NW / 2, botX:NW / 2, loose:null, term:s.kind === 'endFlow' };
    }
    function chain(list) {
        const items = list.map(node), w = Math.max(...items.map(b => b.w)), out = empty();
        let y = 0, px = 0, py = 0, pt = false;
        items.forEach((b, i) => {
            const x = (w - b.w) / 2; merge(out, b, x, y);
            if (i > 0 && !pt) { const tx = x + b.topX; out.edges.push({ pts:Math.abs(px - tx) < .5 ? [[px, py], [tx, y]] : [[px, py], [px, (py + y) / 2], [tx, (py + y) / 2], [tx, y]], c:'n', arrow:true }); }
            if (b.loose && b.loose.length) {
                const ex = x + b.botX;
                if (i < items.length - 1) { const my = y + b.h + VG * .55; for (const [ax, ay] of b.loose) out.edges.push({ pts:[[x + ax, y + ay], [x + ax, my], [ex, my]], c:'n' }); px = ex; py = my; }
                else { px = ex; py = y + b.h; }
            } else { px = x + b.botX; py = y + (b.exitY ?? b.h); }
            pt = b.term || (b.loose && b.loose.length === 0); y += b.h + VG;
        });
        y -= VG; const f = items[0], l = items[items.length - 1];
        return { ...out, w, h:y, topX:(w - f.w) / 2 + f.topX, botX:(w - l.w) / 2 + l.botX, loose:null, term:l.term || (l.loose && l.loose.length === 0) };
    }
    return steps.length ? chain(steps) : null;
}
const MT_EDGE = { n:'#7f7aa2', yes:'#6fd8ce', no:'#e88a6f', fire:'#ffc857' };
function mtRenderCanvas() {
    const stage = mtEl('mtStage'), world = mtEl('mtWorld'); if (!stage || !world) return;
    const g = mtLayout(su.config.steps || []);
    if (!g) { world.innerHTML = '<div class="mt-canvas-empty">流程为空。在左侧大纲添加节点后，这里会画出整张流程图。</div>'; world.style.cssText = ''; su.view.w = 0; return; }
    const { pad, nw, nh } = MT_GEO, top = pad + 46, W = g.w + pad * 2, H = g.h + top + pad;
    const path = e => e.pts.map((p, i) => (i ? 'L' : 'M') + (p[0] + pad) + ',' + (p[1] + top)).join(' ');
    const markers = Object.entries(MT_EDGE).map(([k, c]) => `<marker id="mt-ar-${k}" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="7" markerHeight="7" orient="auto"><path d="M0,0L8,4L0,8z" fill="${c}"/></marker>`).join('');
    world.style.width = W + 'px'; world.style.height = H + 'px';
    world.innerHTML = `<svg class="mt-edges" width="${W}" height="${H}" aria-hidden="true"><defs>${markers}</defs><path d="M${pad + g.topX},${pad + 24} L${pad + g.topX},${top}" stroke="${MT_EDGE.n}" stroke-width="1.6" fill="none" marker-end="url(#mt-ar-n)"/>`
        + g.edges.map(e => `<path d="${path(e)}" stroke="${MT_EDGE[e.c]}" stroke-opacity="${e.dim ? .22 : e.lit ? 1 : .72}" stroke-width="${e.lit ? 2.6 : 1.6}" fill="none"${e.dash ? ' stroke-dasharray="6 5"' : ''}${e.arrow ? ` marker-end="url(#mt-ar-${e.c})"` : ''}/>`).join('') + '</svg>'
        + `<div class="mt-start" style="left:${pad + g.topX - 38}px;top:${pad}px">▶ 开始</div>`
        + g.labels.map(l => `<span class="mt-elabel${l.dim ? ' dim' : ''}${l.lit ? ' lit' : ''}" style="left:${l.x + pad}px;top:${l.y + top}px;color:${MT_EDGE[l.c]}">${l.t}</span>`).join('')
        + g.phs.map(p => `<span class="mt-ph${p.dim ? ' dim' : ''}" style="left:${p.x + pad}px;top:${p.y + top}px;width:${p.w}px;height:${p.h}px">${p.t}</span>`).join('')
        + g.nodes.map(n => mtNodeHtml(n.s, n.x + pad, n.y + top, nw, nh, n.dim)).join('');
    su.view.w = W; su.view.h = H;
    if (su.view.mode !== 'manual') mtFit();
    else mtApplyView();
    // 执行时自动对准正在执行的节点；用户手动平移后不再抢视角，点 ⌖ 恢复跟随。
    const running = [...su.states].find(([, v]) => v.state === 'running')?.[0];
    if (running && su.view.follow !== false && running !== su.view.lastRun) { su.view.lastRun = running; mtCenterOn(running); }
    if (!running) su.view.lastRun = null;
}
function mtNodeHtml(s, x, y, w, h, dim) {
    const info = MT_KIND[s.kind] || { icon:'?', color:'#90A4AE', name:s.kind }, st = mtNodeState(s);
    const cls = ['mt-node', st ? 'st-' + st : '', dim ? 'dim' : '', s.id === su.selected ? 'sel' : '', s.enabled === false ? 'off' : '', MT_EMPH.has(s.kind) ? 'emph' : ''].filter(Boolean).join(' ');
    return `<button type="button" class="${cls}" data-node="${mte(s.id)}" style="left:${x}px;top:${y}px;width:${w}px;height:${h}px;--c:${info.color}" title="${mte(mtDisplayName(s) + '\n' + mtSummary(s))}">`
        + `${st && !dim ? `<span class="mt-node-st">${MT_RUN_TEXT[st]}</span>` : s.enabled === false ? '<span class="mt-node-st">已停用</span>' : ''}`
        + `<span class="mt-ic">${info.icon}</span><span class="mt-node-tx"><b>${mte(mtDisplayName(s))}</b><small>${mte(info.name)} · ${mte(mtSummary(s))}</small></span></button>`;
}
function mtApplyView() {
    const world = mtEl('mtWorld'); if (!world) return;
    world.style.transform = `translate(${su.view.x}px,${su.view.y}px) scale(${su.view.zoom})`;
    world.style.setProperty('--mt-inv', String(1 / su.view.zoom));
    // 远景（缩小到看不清文字时）改画色块：按节点类型着色、加粗连线，文字隐藏，悬停仍可看名称。
    mtEl('mtStage')?.classList.toggle('mt-far', su.view.zoom < .5);
    const z = mtEl('mtZoomText'); if (z) z.textContent = Math.round(su.view.zoom * 100) + '%';
}
function mtStageSize() { const r = mtEl('mtStage')?.getBoundingClientRect(); return r && r.width > 40 && r.height > 40 ? r : null; }
/**
 * 视图模式：fit = 跟随舞台尺寸和流程变化自动适应整图（默认、点「适应」后）；
 * manual = 用户缩放/平移或定位节点后保持当前视角，打开/收起侧栏、窗口变化都不重置。
 */
function mtViewReset() { Object.assign(su.view, { mode:'fit', follow:true, lastRun:null }); }
/** 能把整图放进视口的缩放（最多 100%）。超大流程可低于手动缩放下限 20%，保证「适应」确实显示整图。 */
function mtFitZoom() {
    const r = mtStageSize(); if (!r || !su.view.w) return null;
    // 上方留出缩略提示、下方留出图例和缩放条。
    return Math.max(.02, Math.min(1, (r.width - 24) / su.view.w, (r.height - 84) / su.view.h));
}
/** 适应：整图完整放进视口，居中。舞台不可见（手机大纲页）时推迟到切换过去再算。 */
function mtFit() {
    const r = mtStageSize(), z = mtFitZoom(); if (!r || z === null) return;
    Object.assign(su.view, { zoom:z, x:(r.width - su.view.w * z) / 2, y:Math.max(32, (r.height - 52 - su.view.h * z) / 2) });
    mtApplyView();
}
function mtZoomAt(factor, cx, cy) {
    const r = mtStageSize(); if (!r) return;
    // 手动缩小下限与 PC 一致为 20%；整图比这还大时允许缩到「适应」的比例。
    const lo = Math.min(.2, mtFitZoom() ?? .2), z0 = su.view.zoom, z = Math.min(2, Math.max(lo, z0 * factor));
    cx ??= r.width / 2; cy ??= r.height / 2;
    su.view.x = cx - (cx - su.view.x) * z / z0; su.view.y = cy - (cy - su.view.y) * z / z0; su.view.zoom = z; su.view.mode = 'manual';
    mtApplyView();
}
function mtCenterOn(id) {
    const r = mtStageSize(), el = mtEl('mtWorld')?.querySelector(`[data-node="${CSS.escape(id)}"]`);
    if (!r || !el) return;
    const z = Math.max(su.view.zoom, .7), x = parseFloat(el.style.left) + MT_GEO.nw / 2, y = parseFloat(el.style.top) + MT_GEO.nh / 2;
    Object.assign(su.view, { zoom:z, x:r.width / 2 - x * z, y:r.height / 2 - y * z, mode:'manual' });
    mtApplyView();
}
function mtBindStage() {
    const stage = mtEl('mtStage'); if (!stage) return;
    const pts = new Map(); let start = null, pinch = null;
    stage.onpointerdown = e => {
        if (e.target.closest('.mt-zoom,.mt-legend')) return;
        stage.setPointerCapture(e.pointerId); pts.set(e.pointerId, { x:e.clientX, y:e.clientY });
        if (pts.size === 1) start = { x:e.clientX, y:e.clientY, vx:su.view.x, vy:su.view.y, moved:false, node:e.target.closest('[data-node]')?.dataset.node };
        if (pts.size === 2) { const [a, b] = [...pts.values()]; pinch = { d:Math.hypot(a.x - b.x, a.y - b.y), z:su.view.zoom }; start = null; }
    };
    stage.onpointermove = e => {
        if (!pts.has(e.pointerId)) return;
        pts.set(e.pointerId, { x:e.clientX, y:e.clientY });
        if (pinch && pts.size === 2) {
            const [a, b] = [...pts.values()], r = stage.getBoundingClientRect();
            mtZoomAt(pinch.z * Math.hypot(a.x - b.x, a.y - b.y) / pinch.d / su.view.zoom, (a.x + b.x) / 2 - r.left, (a.y + b.y) / 2 - r.top);
            su.view.follow = false; return;
        }
        if (!start) return;
        const dx = e.clientX - start.x, dy = e.clientY - start.y;
        if (!start.moved && Math.hypot(dx, dy) < 5) return;
        start.moved = true; su.view.follow = false; stage.classList.add('panning');
        su.view.x = start.vx + dx; su.view.y = start.vy + dy; su.view.mode = 'manual'; mtApplyView();
    };
    const end = e => {
        pts.delete(e.pointerId); stage.classList.remove('panning');
        if (pts.size < 2) pinch = null;
        // 远景下文字不可读：点色块直接放大到该节点；正常缩放只选中，不移动视角。
        if (start && !start.moved && start.node && e.type === 'pointerup') { const far = su.view.zoom < .5; if (far) su.view.follow = false; mtSelect(start.node, far); }
        if (!pts.size) start = null;
    };
    stage.onpointerup = end; stage.onpointercancel = end;
    stage.onwheel = e => { e.preventDefault(); const r = stage.getBoundingClientRect(); su.view.follow = false; mtZoomAt(e.deltaY < 0 ? 1.12 : 1 / 1.12, e.clientX - r.left, e.clientY - r.top); };
    stage.onkeydown = e => { if (e.key === '+' || e.key === '=') mtZoomAt(1.2); else if (e.key === '-') mtZoomAt(1 / 1.2); else if (e.key === '0') mtFit(); };
    stage.querySelectorAll('[data-zoom]').forEach(b => b.onclick = () => {
        const k = b.dataset.zoom;
        if (k === 'in') mtZoomAt(1.2); else if (k === 'out') mtZoomAt(1 / 1.2); else if (k === 'fit') { su.view.follow = true; su.view.mode = 'fit'; mtFit(); }
        else { su.view.follow = true; const running = [...su.states].find(([, v]) => v.state === 'running')?.[0]; const id = running || su.selected; if (id) mtCenterOn(id); else mtToast('没有正在执行或已选中的节点', true); }
    });
    // 只保留一个观察器：每次重绘换成新的舞台元素。适应模式随尺寸重算；手动模式保持视角。
    if (globalThis.ResizeObserver) {
        su.resize ||= new ResizeObserver(() => { if (su.view.mode === 'fit') mtFit(); else mtApplyView(); });
        su.resize.disconnect(); su.resize.observe(stage);
    }
}

// ---------- 节点设置（可收起的侧栏；字段与 PC 编辑器一致） ----------
function mtField(label, control, cls = '') { return `<label class="dd-field ${cls}"><span>${label}</span>${control}</label>`; }
function mtInput(f, value, attrs = '') { return `<input data-f="${f}" value="${mte(value ?? '')}" ${attrs}>`; }
function mtSel(f, value, options) { return `<select data-f="${f}">${options.map(([v, t]) => `<option value="${mte(v)}"${String(v) === String(value) ? ' selected' : ''}>${mte(t)}</option>`).join('')}</select>`; }
function mtBool(v) { return v ? '1' : '0'; }
function mtStatusSourceFields(s, kind) {
    if (!['bgiRunning', 'gameRunning', 'bgiTaskRunning', 'bgiTaskName'].includes(kind)) return '';
    const note = s.statusSource === 'startupOrder' ? `首次按 BGI 实际启动时间给会话编号，选择第 ${Math.max(1, s.statusTargetOrder)} 个（包含本会话）。助手运行期间序号不前移；重启助手重新编号，请先按固定顺序启动全部 BGI。`
        : s.statusSource === 'userName' ? `按 Windows 用户名${s.statusTargetUser ? '「' + s.statusTargetUser + '」' : ''}匹配（支持 DOMAIN\\user）；同名多会话无法唯一识别时按未知处理，不执行条件。`
        : '本会话（默认）：只读取当前助手所在 Windows 会话的状态。';
    return `<div class="mt-form-row">${mtField('状态来源', mtSel('statusSource', s.statusSource || 'currentSession', [['currentSession', '本会话（默认）'], ['startupOrder', '按启动顺序'], ['userName', '按用户名']]))}
        ${s.statusSource === 'startupOrder' ? mtField('序号', mtInput('statusTargetOrder', s.statusTargetOrder, 'type="number" min="1" max="99" data-int="1,99"')) : ''}
        ${s.statusSource === 'userName' ? mtField('用户名', mtInput('statusTargetUser', s.statusTargetUser, 'maxlength="128" placeholder="DOMAIN\\user"')) : ''}</div><p class="dodoco-hint">${mte(note)}</p>`;
}
function mtKindFields(s) {
    const exp = (a, b) => mtSel('expectRunning', mtBool(s.expectRunning), [['1', a], ['0', b]]);
    const repeat = () => mtField('触发后', mtSel('watchRepeat', mtBool(s.watchRepeat), [['1', '继续循环'], ['0', '停止']]));
    switch (s.kind) {
        case 'timeRange': return `<div class="mt-form-row">${mtField('开始', mtInput('timeStart', s.timeStart, 'type="time"'))}${mtField('结束', mtInput('timeEnd', s.timeEnd, 'type="time"'))}</div><p class="dodoco-hint">HH:mm，起点大于终点表示跨零点。</p>`;
        case 'weekday': return `<div class="dd-field"><span>星期</span><div class="mt-week">${MT_WEEK.map((w, i) => `<label><input type="checkbox" data-week="${i + 1}"${(s.weekdays || []).includes(i + 1) ? ' checked' : ''}>${w}</label>`).join('')}</div></div>`;
        case 'bgiRunning': case 'gameRunning': return mtField('「是」条件', exp('正在运行时走「是」', '未运行时走「是」')) + mtStatusSourceFields(s, s.kind);
        case 'bgiTaskRunning': return mtField('「是」条件', exp('有任务在跑时走「是」', '空闲（无任务）时走「是」')) + mtStatusSourceFields(s, s.kind);
        case 'bgiTaskName': return mtField('匹配文本', mtInput('taskName', s.taskName, 'maxlength="200" placeholder="当前任务名或配置组名包含即走「是」"')) + '<p class="dodoco-hint">不区分大小写；BGI 未在跑任务时一律走「否」。</p>' + mtStatusSourceFields(s, s.kind);
        case 'processRunning': return `<div class="mt-form-row">${mtField('进程名', mtInput('processName', s.processName, 'maxlength="260" placeholder="可带或不带 .exe"'), 'dd-grow')}${mtField('「是」条件', exp('存在时走「是」', '不存在时走「是」'))}</div>`;
        case 'manualConfirm': return mtField('提示内容', `<textarea data-f="confirmMessage" rows="2" maxlength="500" placeholder="留空则显示节点名">${mte(s.confirmMessage)}</textarea>`)
            + `<div class="mt-form-row">${mtField('超时（秒，0 不限时）', mtInput('confirmTimeoutSeconds', s.confirmTimeoutSeconds, 'type="number" min="0" max="86400" data-int="0,86400"'))}${mtField('超时后', mtSel('confirmTimeoutGoTrue', mtBool(s.confirmTimeoutGoTrue), [['1', '自动走「是」'], ['0', '自动走「否」']]))}</div><p class="dodoco-hint">执行端弹窗同时列出「是 / 否」两条分支的后续动作；网页运行卡片也会出现选择按钮。</p>`;
        case 'startBgi': return mtField('启动参数', mtInput('arguments', s.arguments, 'maxlength="1000" placeholder="可留空，例如 --startGroups &quot;组名&quot;"')) + `<label class="dd-check"><input type="checkbox" data-f="killBeforeStart"${s.killBeforeStart ? ' checked' : ''}>启动前先关闭 BGI</label><p class="dodoco-hint">已在运行的 BGI 不会因新参数自动重启；勾选后先强制关闭本会话 BGI 再带参数启动。</p>`;
        case 'startGame': case 'startProgram': return `<div class="dd-field"><span>程序路径（执行端上的路径）</span><div class="mt-path">${mtInput('path', s.path, 'maxlength="1000" placeholder="例如 D:\\Genshin Impact\\YuanShen.exe"')}<button class="dd-btn dd-btn-sm" data-browse>浏览执行端…</button></div></div>`
            + mtField('启动参数', mtInput('arguments', s.arguments, 'maxlength="1000" placeholder="可留空；参数由目标程序解析"'))
            + (s.kind === 'startProgram' ? mtField('Windows 用户名', mtInput('windowsUserName', s.windowsUserName, 'maxlength="128" placeholder="留空使用当前用户；跨用户需管理员权限"')) : '')
            + '<details class="mt-help"><summary>参数说明与常用示例</summary><p>这里只填参数，多个参数用空格分隔，含空格的值用英文双引号包住。</p><p>本助手：<code>--minimized</code> 启动后隐藏到托盘；OBS：<code>--minimize-to-tray --profile "直播配置"</code>；GameViewer / UU 远程：<code>--auto-run</code>；PowerShell：<code>-NoProfile -WindowStyle Hidden -File "D:\\脚本\\example.ps1"</code>。</p></details>';
        case 'killProgram': return mtField('进程名', mtInput('processName', s.processName, 'maxlength="260" placeholder="可带或不带 .exe；仅结束当前会话的同名进程"'));
        case 'runCmd': return mtField('CMD 命令', `<textarea data-f="arguments" rows="3" maxlength="4000" placeholder="以 cmd /c 隐藏窗口执行，不等待结果">${mte(s.arguments)}</textarea>`);
        case 'wait': return mtField('等待秒数', mtInput('waitSeconds', s.waitSeconds, 'type="number" min="0" max="3600" data-int="0,3600"'));
        case 'startGroup': case 'startOneClick': return mtField('任务名', mtInput('taskName', s.taskName, 'maxlength="200"')) + '<p class="dodoco-hint">旧版节点，建议改用「进入任务中心执行」。</p>';
        case 'timerTrigger': return `<div class="mt-form-row">${mtField('触发时间', mtInput('triggerTime', s.triggerTime, 'type="time"'))}<label class="dd-check dd-align-end"><input type="checkbox" data-f="repeatDaily"${s.repeatDaily ? ' checked' : ''}>每天重复</label></div><p class="dodoco-hint">流程跑到此节点时挂载定时器；今天该时刻已过则顺延到明天，到点执行「到点执行」链。</p>`;
        case 'watchdog': {
            const k = s.watchKind;
            const sub = k === 'processRunning' ? `<div class="mt-form-row">${mtField('进程名', mtInput('processName', s.processName, 'maxlength="260"'), 'dd-grow')}${mtField('触发条件', exp('存在时触发', '不存在时触发'))}</div>`
                : k === 'bgiTaskName' ? `<div class="mt-form-row">${mtField('匹配文本', mtInput('taskName', s.taskName, 'maxlength="200"'), 'dd-grow')}${mtField('触发条件', exp('包含时触发', '不包含时触发'))}</div>`
                : mtField('触发条件', exp('正在运行/在跑时触发', '未运行/空闲时触发'));
            return `<div class="mt-form-row">${mtField('盯什么', mtSel('watchKind', k, MT_WATCHABLE.map(x => [x, MT_KIND[x].name])))}${mtField('间隔（秒）', mtInput('watchIntervalSeconds', s.watchIntervalSeconds, 'type="number" min="1" max="86400" data-int="1,86400"'))}${repeat()}</div>${sub}
                <div class="mt-form-row">${mtField('防抖复核间隔（秒）', mtInput('watchConfirmSeconds', s.watchConfirmSeconds, 'type="number" min="1" max="60" data-int="1,60"'))}${mtField('复核次数', mtInput('watchConfirmTimes', s.watchConfirmTimes, 'type="number" min="1" max="10" data-int="1,10"'))}</div>
                <p class="dodoco-hint">发现新状态后按复核参数确认，全部一致才认定翻转。触发链为空时只显示监控状态并持续盯梢。</p>${mtStatusSourceFields(s, k)}`;
        }
        case 'logTrigger': return `<div class="mt-form-row">${mtField('日志关键字', mtInput('logKeyword', s.logKeyword, 'maxlength="500"'), 'dd-grow')}${repeat()}</div><p class="dodoco-hint">只盯挂载之后执行端本机 BGI 的新日志；正文、来源或异常段包含该文本即触发（不区分大小写）。</p>`;
        case 'enterTaskCenter': {
            const known = su.flows.some(f => f.id === s.taskCenterFlowId);
            const options = [['', su.flows.length ? '选择目标计划' : '（尚未读取到任务中心计划）'], ...su.flows.map(f => [f.id, f.name + (f.status === 1 ? '（已隔离 · 不可用）' : '')])];
            if (s.taskCenterFlowId && !known) options.push([s.taskCenterFlowId, `不可用：${s.taskCenterFlowId}`]);
            const mode = ['start', 'resume', 'armTrigger'].includes(s.taskCenterHandoffMode) ? s.taskCenterHandoffMode : s.taskCenterHandoffMode;
            const modes = [['start', '立即执行'], ['resume', '恢复既有运行'], ['armTrigger', '挂载触发器']];
            if (!modes.some(m => m[0] === mode)) modes.push([mode, `未知语义「${mode}」（运行时会拒绝）`]);
            return mtField('目标流程', mtSel('taskCenterFlowId', s.taskCenterFlowId, options)) + mtField('移交语义', mtSel('taskCenterHandoffMode', mode, modes))
                + `<label class="dd-check"><input type="checkbox" data-f="continueAfterTaskCenterHandoff"${s.continueAfterTaskCenterHandoff ? ' checked' : ''}>移交成功后继续后续启动步骤</label><p class="dodoco-hint">默认受理后结束启动流程。目标缺失或隔离保留原身份显示「不可用」，执行时由任务中心权威校验。</p>`;
        }
        case 'endFlow': return '<p class="dodoco-hint">执行到此节点立即终止整条启动流程（含外层链）。它必须是所在链的最后一个节点，常用于「否」分支：环境不满足时直接放弃本次启动。</p>';
        default: return '<p class="dodoco-hint">执行端返回了网页不认识的节点类型；保留原样，不在网页修改。</p>';
    }
}
function mtRenderInspector() {
    const host = mtEl('mtInspector'); if (!host) return;
    const editor = mtEl('mtEditor');
    editor?.classList.toggle('inspector-closed', !su.inspectorOpen);
    const hit = su.selected ? mtFind(su.selected) : null;
    if (!su.inspectorOpen) { host.innerHTML = `<button class="mt-insp-tab" data-open title="展开节点设置">节点设置 ‹</button>`; host.querySelector('[data-open]').onclick = () => { su.inspectorOpen = true; mtRenderInspector(); }; return; }
    if (!hit) {
        host.innerHTML = `<header class="mt-insp-head"><h3>节点设置</h3><button class="dd-btn dd-btn-sm dd-btn-quiet" data-close title="收起">›</button></header><p class="dd-empty">在大纲或流程图里点一个节点，在这里修改它的参数。</p>`;
        host.querySelector('[data-close]').onclick = () => { su.inspectorOpen = false; mtRenderInspector(); };
        return;
    }
    const s = hit.step, info = MT_KIND[s.kind] || { name:s.kind, icon:'?', color:'#90A4AE', type:'' };
    const typeText = { condition:'条件节点 · 分「是 / 否」两条支路', action:'动作节点', trigger:'触发器 · 挂载后在后台等待', legacy:'旧版节点' }[info.type] || '';
    host.innerHTML = `<header class="mt-insp-head"><span class="mt-ic" style="--c:${info.color}">${info.icon}</span><div><h3>${mte(info.name)}</h3><p class="dodoco-hint">${typeText} · 位于${mte(mtChainLabel(hit.parent, hit.key))}第 ${hit.index + 1} 位</p></div><button class="dd-btn dd-btn-sm dd-btn-quiet" data-close title="收起节点设置">›</button></header>
      <div class="mt-insp-body">${mtField('节点名称', mtInput('name', s.name, `maxlength="80" placeholder="${mte(info.name)}"`))}
      ${mtKindFields(s)}
      <label class="dd-check"><input type="checkbox" data-f="enabled"${s.enabled !== false ? ' checked' : ''}>启用此节点（停用后执行时跳过）</label>
      ${mtNodeState(s) ? `<p class="mt-insp-state st-${mtNodeState(s)}">本次执行：${MT_RUN_TEXT[mtNodeState(s)]}${su.states.get(s.id)?.note ? ' · ' + mte(su.states.get(s.id).note) : ''}</p>` : ''}</div>
      <footer class="mt-insp-foot"><button class="dd-btn dd-btn-sm" data-move="-1"${hit.index === 0 || s.kind === 'endFlow' ? ' disabled' : ''}>↑ 上移</button><button class="dd-btn dd-btn-sm" data-move="1"${hit.index >= hit.chain.length - 1 || s.kind === 'endFlow' ? ' disabled' : ''}>↓ 下移</button><button class="dd-btn dd-btn-sm" data-locate>在图中定位</button><button class="dd-btn dd-btn-sm dd-btn-danger" data-remove>删除</button></footer>`;
    host.querySelector('[data-close]').onclick = () => { su.inspectorOpen = false; mtRenderInspector(); };
    host.querySelectorAll('[data-move]').forEach(b => b.onclick = () => mtMoveStep(s.id, +b.dataset.move));
    host.querySelector('[data-remove]').onclick = () => mtRemoveStep(s.id);
    host.querySelector('[data-locate]').onclick = () => { if (su.mobilePane !== 'canvas' && matchMedia('(max-width: 900px)').matches) { su.mobilePane = 'canvas'; mtEl('mtEditor').dataset.pane = 'canvas'; mtPaneButtons(); } su.view.follow = false; setTimeout(() => mtCenterOn(s.id), 0); };
    host.querySelector('[data-browse]')?.addEventListener('click', () => mtBrowsePrograms(path => { s.path = path; mtEdited('已选择执行端程序 ' + path, true); mtRenderInspector(); }));
    const commit = (el, final) => {
        const f = el.dataset.f; let v;
        if (el.type === 'checkbox') v = el.checked;
        else if (el.dataset.int) { if (!final) return; const [a, b] = el.dataset.int.split(',').map(Number); v = Math.min(b, Math.max(a, parseInt(el.value, 10) || a)); el.value = v; }
        else if (['expectRunning', 'confirmTimeoutGoTrue', 'watchRepeat'].includes(f)) v = el.value === '1';
        else v = el.value;
        if (s[f] === v) return;
        s[f] = v;
        const structural = el.tagName === 'SELECT' && ['statusSource', 'watchKind'].includes(f);
        mtEdited(null, true);
        if (structural || el.type === 'checkbox' && f === 'enabled') mtRenderInspector();
    };
    host.querySelectorAll('[data-f]').forEach(el => {
        el.addEventListener('input', () => { if (el.type !== 'checkbox' && el.tagName !== 'SELECT') commit(el, false); });
        el.addEventListener('change', () => commit(el, true));
    });
    host.querySelectorAll('[data-week]').forEach(el => el.onchange = () => {
        const set = new Set(s.weekdays || []); el.checked ? set.add(+el.dataset.week) : set.delete(+el.dataset.week);
        s.weekdays = [...set].sort((a, b) => a - b); mtEdited(null, true);
    });
}
/** PC 的「浏览…」是执行端本机文件对话框；网页改为逐级浏览执行端目录（startup.programs.list，只列目录与 .exe）。 */
async function mtBrowsePrograms(onPick, dir = '', title = '选择执行端上的程序', accept = null, pickDir = false) {
    const dialog = mtDialog('mtPickDialog'); if (!dialog) return;
    if (!dialog.open) dialog.showModal();
    dialog.innerHTML = `<header class="mt-dialog-head"><div><h3>${mte(title)}</h3><p class="dodoco-hint">${mte(dir || '执行端磁盘')}</p></div><button class="dd-btn dd-btn-sm" data-close>关闭</button></header><p class="dd-empty">正在读取执行端目录…</p>`;
    dialog.querySelector('[data-close]').onclick = () => dialog.close();
    try {
        const reply = await mtRead('startup.programs.list', dir || undefined);
        const d = reply.data || {}, path = mtPick(d, 'path') || '', dirs = mtPick(d, 'directories') || [], programs = (mtPick(d, 'programs') || []).filter(x => !accept || accept(x));
        const parent = path ? path.replace(/[\\/]+$/, '').replace(/[\\/][^\\/]*$/, '') : '';
        const name = p => p.replace(/[\\/]+$/, '').split(/[\\/]/).pop() || p;
        dialog.innerHTML = `<header class="mt-dialog-head"><div><h3>${mte(title)}</h3><p class="dodoco-hint mt-break">${mte(path || '执行端磁盘')}</p></div><div class="dd-actions">${pickDir && path ? '<button class="dd-btn dd-btn-sm dd-btn-primary" data-pickdir>选择此目录</button>' : ''}<button class="dd-btn dd-btn-sm" data-close>关闭</button></div></header>
          <div class="mt-browse">${path ? `<button class="mt-browse-row" data-dir="${mte(parent && parent.length >= 3 ? parent : '')}">↰ 上一级</button>` : ''}
          ${dirs.map(x => `<button class="mt-browse-row" data-dir="${mte(x)}">📁 ${mte(path ? name(x) : x)}</button>`).join('')}
          ${programs.map(x => `<button class="mt-browse-row is-exe" data-exe="${mte(x)}">▶ ${mte(name(x))}</button>`).join('')}
          ${!dirs.length && !programs.length ? `<p class="dd-empty">此目录下没有子目录${pickDir ? '' : '或 .exe 程序'}</p>` : ''}${dirs.length >= 500 || programs.length >= 500 ? '<p class="dodoco-hint">条目较多，只显示前 500 个；可直接在路径框输入完整路径。</p>' : ''}</div>`;
        dialog.querySelector('[data-close]').onclick = () => dialog.close();
        dialog.querySelectorAll('[data-dir]').forEach(b => b.onclick = () => mtBrowsePrograms(onPick, b.dataset.dir, title, accept, pickDir));
        dialog.querySelector('[data-pickdir]')?.addEventListener('click', () => { dialog.close(); onPick(path); });
        dialog.querySelectorAll('[data-exe]').forEach(b => b.onclick = () => { dialog.close(); onPick(b.dataset.exe); });
    } catch (error) {
        dialog.innerHTML = `<header class="mt-dialog-head"><h3>${mte(title)}</h3><button class="dd-btn dd-btn-sm" data-close>关闭</button></header><p class="dd-empty">读取失败：${mte(error.message)}</p>`;
        dialog.querySelector('[data-close]').onclick = () => dialog.close();
    }
}
