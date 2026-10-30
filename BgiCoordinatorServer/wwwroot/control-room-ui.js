// A 舰桥：仅网页状态与交互；执行权仍归现有助手和服务端。
const selectedMemberUids = new Set();
let selectionInitialized = false;
let activeMemberUid = null;
let detailSection = 'status';
let latestControlBatch = null;
const webEscapeHtml = escapeHtml;
const quickKeys = ['一键传奇', '一键次数盾', '一键精英', '一键小怪', '一键自定义'];
function currentMember(uid = activeMemberUid) { return players.find(p => p.playerUid === uid); }
function getSelectedMembers() { return players.filter(p => selectedMemberUids.has(p.playerUid)); }
function avatarFor(p) {
    const names = ['Furina', 'Ayaka', 'Kazuha', 'Zhongli'];
    const hash = [...String(p.playerUid)].reduce((n, c) => (n * 31 + c.charCodeAt(0)) >>> 0, 0);
    return `<img class="avatar" src="gengdi-avatars/${names[hash % names.length]}.png" alt="">`;
}
function memberState(p) {
    if (!p.online) return { label: '助手离线', tone: 'muted' };
    if (!roomReady) return { label: '连接中断 · 状态待确认', tone: 'danger' };
    if (p.taskStatusAvailable === false && p.bgiStatus !== 'stopped') return { label: '任务状态待确认', tone: 'danger' };
    if (p.bgiStatus === 'stopped') return { label: 'BGI 未运行', tone: 'muted' };
    if (p.bgiStatus === 'crashed') return { label: 'BGI 异常', tone: 'danger' };
    if (!p.bgiStatus || p.bgiStatus === 'unknown') return { label: 'BGI 状态待确认', tone: 'danger' };
    if (p.taskPaused) return { label: '已暂停', tone: 'muted' };
    return { label: p.taskRunning ? '执行中' : '空闲', tone: 'good' };
}
function batchPhaseName(phase) {
    return ({ registering: '登记成员', preparing: '全队准备中', takeover: '接管原任务', running: '联机执行中',
        stopping: '全队停止中', cleaning: '清理中', cleanupPending: '清理待确认', aborted: '已中止，等待清理',
        completed: '已完成', failed: '失败', cancelled: '已取消', unknown: '结果待确认', completionPending: '执行锄地后动作' })[phase] || '状态待确认';
}
function onControlBatchState(batch) {
    if (!batch?.batchId) return;
    if (latestControlBatch?.batchId === batch.batchId && batch.revision < latestControlBatch.revision) return;
    latestControlBatch = batch;
    document.querySelectorAll('[data-batch-id]').forEach(overlay => {
        if (overlay.dataset.batchId === batch.batchId) {
            const feedback = overlay.querySelector('.batch-feedback');
            if (feedback) feedback.textContent = `联机批次：${batchPhaseName(batch.phase)}${batch.reason ? ' · ' + batch.reason : ''}`;
        }
    });
    renderControlRoom();
}
function showHome() {
    document.getElementById('homeView').hidden = false;
    document.getElementById('detailView').hidden = true;
    document.getElementById('homeTab').classList.add('active');
    document.getElementById('detailTab').classList.remove('active');
    renderSelectionBar();
    scheduleMemberCardAlignment();
}
function openMemberDetail(uid) {
    if (!currentMember(uid)) return;
    activeMemberUid = uid;
    detailSection = 'status';
    document.getElementById('homeView').hidden = true;
    document.getElementById('detailView').hidden = false;
    document.getElementById('detailTab').disabled = false;
    document.getElementById('homeTab').classList.remove('active');
    document.getElementById('detailTab').classList.add('active');
    renderDetailHeader(); renderDetailTabs(); renderDetailContent(); renderSelectionBar();
    window.scrollTo({ top: 0, behavior: 'smooth' });
}
function toggleSelectedMembers() {
    const online = players.filter(p => p.online).map(p => p.playerUid);
    const all = online.length && online.every(uid => selectedMemberUids.has(uid));
    selectedMemberUids.clear();
    if (!all) online.forEach(uid => selectedMemberUids.add(uid));
    renderControlRoom();
}
function renderControlRoom() {
    if (!selectionInitialized && players.length) {
        players.filter(p => p.online).forEach(p => selectedMemberUids.add(p.playerUid));
        selectionInitialized = true;
    }
    const online = players.filter(p => p.online).length;
    const running = players.filter(p => p.online && p.taskRunning && p.taskStatusAvailable !== false).length;
    const batchMember = players.find(p => p.hoeingBatchId && ['registering', 'preparing', 'takeover', 'running', 'cleaning', 'stopping', 'cleanupPending'].includes(p.hoeingStartPhase));
    document.getElementById('roomSummary').innerHTML = `<section class="hero"><div><p class="eyebrow">${online} 人在线 · ${running} 人正在执行任务</p><h1>${batchMember ? webEscapeHtml(batchPhaseName(batchMember.hoeingStartPhase)) : '准备一起开锄'}</h1><p>${batchMember ? webEscapeHtml(batchMember.hoeingStartMessage || '成员正在执行当前联机批次') : '选择成员，检查各自安排。全部准备就绪后接管当前任务。'}</p></div><div class="hero-metric"><b>${selectedMemberUids.size}</b><span>已选成员</span></div></section>`;
    document.getElementById('memberCount').textContent = players.length;
    document.getElementById('selectAll').textContent = online && players.filter(p => p.online).every(p => selectedMemberUids.has(p.playerUid)) ? '取消全选' : '选择在线成员';
    document.querySelectorAll('.quick-btn').forEach(b => { b.disabled = !roomReady || !selectedMemberUids.size; });
    const list = document.getElementById('memberList');
    list.innerHTML = players.map(p => {
        const state = memberState(p), selected = selectedMemberUids.has(p.playerUid);
        const taskAvailable = roomReady && p.online;
        const presenceText = !roomReady ? '待确认' : !p.online ? '离线' : p.onlineReady ? '上线' : '未上线';
        const presence = !roomReady ? '连接待确认' : !p.online ? '助手离线' : `助手在线 · 锄地${presenceText}`;
        const presenceClass = !roomReady ? 'presence-unknown' : !p.online ? 'presence-offline' : p.onlineReady ? 'presence-online' : 'presence-not-ready';
        const task = p.taskRunning ? p.currentTaskName || '正在执行任务' : p.bgiStatus === 'stopped' ? '可远程启动 BGI' : '等待下一项任务';
        return `<article class="member-card ${selected ? 'selected' : ''} ${taskAvailable ? 'assistant-online' : ''} ${p.online ? '' : 'offline'}" data-member-uid="${webEscapeHtml(p.playerUid)}">
            <div class="member-head">${avatarFor(p)}<div class="member-identity"><button data-ui="detail" data-uid="${webEscapeHtml(p.playerUid)}">${webEscapeHtml(p.playerName || p.playerUid)}</button><small>${webEscapeHtml(p.playerUid)}</small></div><button class="member-select" data-ui="select" data-uid="${webEscapeHtml(p.playerUid)}" aria-label="选择${webEscapeHtml(p.playerName || p.playerUid)}" aria-pressed="${selected}" ${!p.online && !selected ? 'disabled' : ''}><span>${selected ? '✓' : ''}</span></button></div>
            <div class="member-version-row"><span class="version-label">BGI</span><span class="member-version">${webEscapeHtml(p.bgiVersion || '版本未上报')}</span><span class="presence ${presenceClass}" title="${presence}" aria-label="${presence}"><span class="presence-dot" aria-hidden="true"></span><span class="presence-visible" aria-hidden="true">${presenceText}</span><span class="sr-only">${presence}</span></span></div>
            <div class="member-body"><div class="status-row"><span>当前任务</span><span class="${taskAvailable ? state.tone : 'muted'}">${webEscapeHtml(taskAvailable ? state.label : '状态待确认')}</span></div><div class="task-name${taskAvailable && p.taskRunning ? ' task-active' : ''}">${webEscapeHtml(taskAvailable ? task : '当前状态待确认')}</div><p class="task-route">${webEscapeHtml(taskAvailable ? p.currentTaskGroupName || '' : roomReady ? '助手离线后无法获取实时任务' : '网页连接中断，无法确认实时任务')}</p><p class="task-route">${webEscapeHtml(taskAvailable ? p.currentRouteDisplay || p.currentScriptRouteName || '' : p.bgiVersion ? '上次版本仅供参考' : '')}</p><p class="task-route">${webEscapeHtml(taskAvailable ? p.hoeingStartMessage || p.scriptTaskProgress || p.friendshipProgress || '' : '')}</p></div>
            <div class="member-foot"><span>${webEscapeHtml(hoeingCompletionDescription(p.hoeingCompletionAction))}</span><button data-ui="detail" data-uid="${webEscapeHtml(p.playerUid)}">管理 ›</button></div><div class="member-result" data-result-uid="${webEscapeHtml(p.playerUid)}" hidden></div></article>`;
    }).join('') || '<div class="section-empty">暂无成员连接。请使用 PC 助手加入相同控制房间。</div>';
    renderMemberResults(); renderSelectionBar();
    scheduleMemberCardAlignment();
    if (!document.getElementById('detailView').hidden) {
        renderDetailHeader();
        // Status broadcasts must not destroy edits in progress.
        if (detailSection === 'status' || detailSection === 'history') renderDetailContent();
    }
}
function renderMemberResults() {
    document.querySelectorAll('[data-result-uid]').forEach(el => {
        const result = memberCommandResults.get(el.dataset.resultUid);
        el.hidden = !result;
        if (result) {
            el.className = 'member-result result-' + result.status;
            el.textContent = `${result.time} ${commandTitles[result.cmd] || result.cmd}：${result.status === 'success' ? '已确认' : result.status === 'pending' ? '处理中' : result.status === 'unknown' ? '未确认' : '未完成'} · ${result.message}`;
        }
    });
    scheduleMemberCardAlignment();
}

// 不支持 subgrid 的浏览器按每个实际卡片行同步高度，保留完整长版本及状态对齐。
let memberAlignmentFrame = 0;
function scheduleMemberCardAlignment() {
    if (window.CSS?.supports?.('grid-template-rows', 'subgrid')) return;
    if (memberAlignmentFrame) cancelAnimationFrame(memberAlignmentFrame);
    memberAlignmentFrame = requestAnimationFrame(() => {
        memberAlignmentFrame = 0;
        const cards = [...document.querySelectorAll('#memberList .member-card')];
        const selectors = ['.member-head', '.member-version-row', '.member-body', '.member-foot'];
        const rows = new Map();
        for (const card of cards) {
            for (const selector of selectors) card.querySelector(selector).style.minHeight = '';
        }
        for (const card of cards) {
            const rect = card.getBoundingClientRect();
            if (!rect.width) continue;
            const top = Math.round(rect.top);
            if (!rows.has(top)) rows.set(top, []);
            rows.get(top).push(card);
        }
        for (const row of rows.values()) {
            for (const selector of selectors) {
                const elements = row.map(card => card.querySelector(selector));
                const height = Math.max(...elements.map(el => el.getBoundingClientRect().height));
                elements.forEach(el => { el.style.minHeight = height + 'px'; });
            }
        }
    });
}
window.addEventListener('resize', scheduleMemberCardAlignment);
if (document.fonts?.ready) document.fonts.ready.then(scheduleMemberCardAlignment);

function renderSelectionBar() {
    const bar = document.getElementById('selectionBar');
    bar.hidden = document.getElementById('controlPanel').style.display === 'none' || document.getElementById('homeView').hidden;
    const unavailable = [...selectedMemberUids].some(uid => !currentMember(uid)?.online);
    bar.innerHTML = `<div class="selection-inner"><div><strong>已选 ${selectedMemberUids.size} 人</strong><small>${unavailable ? '含离线或已离开成员，请重新选择' : '按各自绑定执行'}</small></div><div class="selection-actions"><button class="btn btn-secondary" onclick="showBatchTools()" ${!roomReady || !selectedMemberUids.size ? 'disabled' : ''}>批量操作</button><button class="btn btn-primary" onclick="showCoordinatedConfirm('一键锄地')" ${!roomReady || !selectedMemberUids.size || unavailable ? 'disabled' : ''}>准备联机</button></div></div>`;
}
document.getElementById('memberList').addEventListener('click', event => {
    const button = event.target.closest('[data-ui]');
    if (!button || button.disabled) return;
    if (button.dataset.ui === 'detail') openMemberDetail(button.dataset.uid);
    if (button.dataset.ui === 'select') {
        const uid = button.dataset.uid;
        selectedMemberUids.has(uid) ? selectedMemberUids.delete(uid) : selectedMemberUids.add(uid);
        renderControlRoom();
    }
});
function renderDetailHeader() {
    const p = currentMember();
    const host = document.getElementById('detailHeader');
    if (!p) { host.innerHTML = '<button class="back-button" onclick="showHome()">‹ 返回房间</button><p class="error">该成员已离开，操作前请重新选择。</p>'; return; }
    const state = memberState(p);
    host.innerHTML = `<button class="back-button" onclick="showHome()">‹ 返回房间</button><div class="detail-top">${avatarFor(p)}<div class="detail-identity"><h1>${webEscapeHtml(p.playerName || p.playerUid)}</h1><p class="muted">${webEscapeHtml(p.playerUid)} · <span class="${state.tone}">${webEscapeHtml(state.label)}</span><span class="detail-version"> · BGI ${webEscapeHtml(p.bgiVersion || '版本未上报')}</span></p></div></div>`;
}
function renderDetailTabs() {
    const tabs = [['status', '状态'], ['tasks', '任务'], ['settings', '联机设置'], ['history', '记录']];
    const host = document.getElementById('detailTabs');
    host.innerHTML = tabs.map(([id, label]) => `<button data-section="${id}" class="${detailSection === id ? 'active' : ''}">${label}</button>`).join('');
    host.querySelectorAll('button').forEach(b => b.onclick = () => { detailSection = b.dataset.section; renderDetailTabs(); renderDetailContent(); });
}
function renderDetailContent() {
    const p = currentMember(), host = document.getElementById('detailContent');
    if (!p) { host.innerHTML = '<p class="note">成员已离开，未发送任何操作。</p>'; return; }
    if (detailSection === 'status') renderStatusDetail(p, host);
    if (detailSection === 'tasks') renderTaskCatalog(p, host);
    if (detailSection === 'settings') renderMemberSettings(p, host);
    if (detailSection === 'history') renderOnlineHistory(p, host);
}
function renderStatusDetail(p, host) {
    const state = memberState(p), disabled = !p.online || !roomReady ? 'disabled' : '';
    const progress = p.autoHoeingProgress || p.scriptTaskProgress || p.friendshipProgress;
    host.innerHTML = `<div class="status-stack"><section class="panel"><div class="panel-header run-header"><div class="run-title"><span class="run-label">当前运行</span><h2>${webEscapeHtml(p.currentTaskName || (p.taskStatusAvailable === false ? '任务状态待确认' : '暂无运行任务'))}</h2></div><span class="run-state ${state.tone}">${webEscapeHtml(state.label)}</span></div>
        <div class="kv-strip">${[['配置组 / 一条龙', p.currentTaskGroupName], ['线路', p.currentRouteDisplay || p.currentScriptRouteName], ['联机状态', p.hoeingStartMessage], ['定时上线', p.scheduledOnlineTime || '未设置'], ['上线标记', p.onlineReady ? '已上线，等待联机' : '未上线']].filter(([, v]) => v).map(([k, v]) => `<div class="kv"><span>${webEscapeHtml(k)}</span><b>${webEscapeHtml(v)}</b></div>`).join('')}</div>
        ${progress ? `<div class="run-progress"><span>执行进度</span><p>${webEscapeHtml(progress)}</p></div>` : ''}
        <div class="action-grid"><button class="btn btn-secondary" data-op="start_bgi" ${disabled}>启动 BGI</button><button class="btn btn-secondary danger" data-op="stop" ${disabled}>停止任务</button><button class="btn btn-secondary" data-op="clear_online" ${disabled}>清除上线</button><button class="btn btn-secondary danger" data-op="close_game" ${disabled}>关闭游戏</button><button class="btn btn-secondary" id="detailReset" ${disabled || resetStateBusy ? 'disabled' : ''}>重置状态</button></div></section>
        <section class="panel plan-panel"><div class="panel-header"><h2>联机安排</h2><button id="editBindings" class="text-button">编辑联机安排 ›</button></div><div class="plan-items">${memberBinding(p).names.map((n, i) => `<div class="binding-row"><span class="order">${i + 1}</span><div class="binding-name">${webEscapeHtml(n)}<small>${memberBinding(p).types[i] === 'onedragon' ? '一条龙' : '配置组'}</small></div></div>`).join('') || '<p class="note">尚未绑定联机任务。</p>'}</div><p class="note plan-after">结束后：${webEscapeHtml(hoeingCompletionDescription(p.hoeingCompletionAction))}</p><div data-result-uid="${webEscapeHtml(p.playerUid)}" class="member-result" hidden></div></section></div>`;
    host.querySelectorAll('[data-op]').forEach(b => b.onclick = () => showMemberOperation([p.playerUid], b.dataset.op));
    host.querySelector('#detailReset').onclick = () => showResetStateModal(p.playerUid, p.playerName || p.playerUid);
    host.querySelector('#editBindings').onclick = () => { detailSection = 'settings'; renderDetailTabs(); renderDetailContent(); };
    renderMemberResults();
}
function renderTaskCatalog(p, host) {
    let type = 'group';
    host.innerHTML = `<div class="panel"><h2>任务目录</h2><p class="note">选择配置后可查看任务起点与启用状态；执行前会再次检查目标成员。</p><input id="catalogSearch" type="search" placeholder="搜索配置名称" aria-label="搜索配置名称"><div class="filter-tabs"><button class="active" data-kind="group">配置组</button><button data-kind="oneclick">一条龙</button><button data-kind="hotkey">快捷键</button></div><div id="catalogItems" class="catalog-grid"></div></div>`;
    const draw = () => {
        const current = currentMember(p.playerUid), query = host.querySelector('#catalogSearch').value.toLowerCase();
        const items = type === 'hotkey' ? (current?.hotkeys || []).map(h => ({ name: h.functionName || h.configName, value: h.configName, sub: h.hotkeyText || '' }))
            : ((type === 'oneclick' ? current?.oneClickConfigs : current?.configGroups) || []).map(name => ({ name, value: name, sub: type === 'oneclick' ? '一条龙' : '配置组' }));
        const filtered = items.filter(x => String(x.name).toLowerCase().includes(query));
        const grid = host.querySelector('#catalogItems');
        grid.innerHTML = filtered.map((item, i) => `<button class="catalog-item" data-index="${i}" ${!current?.online || !roomReady ? 'disabled' : ''}>${webEscapeHtml(item.name)}<small>${webEscapeHtml(item.sub)}</small></button>`).join('') || '<p class="note">没有匹配的项目，等待执行端上报。</p>';
        grid.querySelectorAll('button').forEach(b => b.onclick = () => {
            const item = filtered[Number(b.dataset.index)];
            if (type === 'hotkey') showMemberOperation([p.playerUid], 'hotkey_execute', { hotkeyConfigName: item.value });
            else showMemberConfirmModal(p.playerUid, p.playerName, item.value, type === 'oneclick');
        });
    };
    host.querySelector('#catalogSearch').oninput = draw;
    host.querySelectorAll('[data-kind]').forEach(b => b.onclick = () => { type = b.dataset.kind; host.querySelectorAll('[data-kind]').forEach(x => x.classList.toggle('active', x === b)); draw(); });
    draw();
}
function createModal(title, body, buttons = '') {
    const overlay = document.createElement('div'); overlay.className = 'modal-overlay';
    overlay.innerHTML = `<div class="modal" role="dialog" aria-modal="true" aria-label="${webEscapeHtml(title)}"><h3>${webEscapeHtml(title)}</h3>${body}<div class="btn-row">${buttons || '<button class="btn btn-secondary" data-close>关闭</button>'}</div></div>`;
    overlay.querySelectorAll('[data-close]').forEach(b => b.onclick = () => overlay.remove());
    document.body.appendChild(overlay);
    overlay.querySelector('button, input, select')?.focus();
    return overlay;
}
function formatResult(result) { return `${result.status === 'success' ? '已确认' : result.status === 'unknown' ? '未确认' : '未完成'}：${result.message || ''}`; }
async function runFormButton(button, status, action) {
    if (button.disabled) return;
    button.disabled = true; status.textContent = '处理中，等待执行端确认…';
    try { const result = await action(); status.textContent = formatResult(result); return result; }
    catch (err) { status.textContent = '未完成：' + err.message; }
    finally { button.disabled = false; }
}
function showMemberOperation(targets, operation, parameters = {}) {
    const frozen = [...targets];
    const names = frozen.map(uid => currentMember(uid)?.playerName || uid);
    const stopBatches = new Map(frozen.map(uid => [uid, currentMember(uid)?.hoeingBatchId || '']));
    const overlay = createModal(commandTitles[operation] || '确认操作', `<p>对以下 ${frozen.length} 名成员${webEscapeHtml(commandTitles[operation] || '执行操作')}：</p><p class="note">${names.map(webEscapeHtml).join('、')}</p>${operation === 'stop' ? '<p class="note">停止联机中的成员可能中止共享批次。</p>' : ''}<div class="form-result" role="status"></div>`, '<button class="btn btn-secondary" data-close>取消</button><button class="btn btn-primary" id="operationConfirm">确认</button>');
    overlay.querySelector('#operationConfirm').onclick = async event => {
        const button = event.currentTarget, cancel = overlay.querySelector('[data-close]');
        if (button.disabled) return;
        button.disabled = true; cancel.disabled = true;
        try {
            requireOnlineTargets(frozen);
            const results = await Promise.all(frozen.map(async uid => {
                try { return { uid, ...(await sendMemberOperation(uid, operation, operation === 'stop' ? { ...parameters, hoeingBatchId: stopBatches.get(uid) } : parameters)) }; }
                catch (err) { return { uid, status: 'failed', message: err.message }; }
            }));
            overlay.querySelector('.form-result').textContent = results.map(r => `${currentMember(r.uid)?.playerName || r.uid}：${formatResult(r)}`).join('\n');
        } catch (err) { overlay.querySelector('.form-result').textContent = err.message; }
        finally { button.hidden = true; cancel.disabled = false; cancel.textContent = '关闭'; }
    };
}
function showBatchTools() {
    const targets = [...selectedMemberUids];
    const overlay = createModal(`已选 ${targets.length} 人 · 批量操作`, '<p class="note">下一步会再次列出目标并确认，不会扩大到未选成员。</p><div class="action-grid"><button class="btn btn-secondary" data-op="start_bgi">启动 BGI</button><button class="btn btn-secondary danger" data-op="stop">停止任务</button><button class="btn btn-secondary" data-op="clear_online">清除上线</button><button class="btn btn-secondary" id="batchSchedule">定时上线</button></div>');
    overlay.querySelectorAll('[data-op]').forEach(b => b.onclick = () => { overlay.remove(); showMemberOperation(targets, b.dataset.op); });
    overlay.querySelector('#batchSchedule').onclick = () => {
        overlay.remove();
        const form = createModal('批量设置定时上线', `<p>目标：${targets.map(uid => webEscapeHtml(currentMember(uid)?.playerName || uid)).join('、')}</p><label class="field">执行端本地时间<input type="time" id="batchTime"></label><p class="note">留空表示取消定时。按各执行端的本地时间触发。</p>`, '<button class="btn btn-secondary" data-close>取消</button><button class="btn btn-primary" id="batchSave">确认设置</button>');
        form.querySelector('#batchSave').onclick = () => { const time = form.querySelector('#batchTime').value; form.remove(); showMemberOperation(targets, 'set_scheduled_online_time', { scheduledOnlineTime: time }); };
    };
}
function renderOnlineHistory(p, host) {
    const rows = p.onlineHistory || [];
    host.innerHTML = `<section class="panel"><div class="panel-header"><h2>上线记录</h2><button id="clearHistory" class="text-button">清除该成员记录</button></div><p class="note">这是服务端当前保存的上线消费记录，不是完整任务日志。</p>${rows.map(row => `<div class="history-row"><strong>${row.mode === 'scheduled' ? '定时上线' : row.mode === 'command' ? '命令上线' : webEscapeHtml(row.mode || '上线')}</strong><p class="muted">上线 ${webEscapeHtml(row.onlineTime || '—')}　联机 ${webEscapeHtml(row.consumeTime || '—')}</p></div>`).join('') || '<p class="note">暂无上线记录。</p>'}</section>`;
    host.querySelector('#clearHistory').onclick = () => {
        const overlay = createModal('清除上线记录', `<p>确认清除 ${webEscapeHtml(p.playerName || p.playerUid)} 的上线记录？不会清除定时设置或任务配置。</p><div class="form-result" role="status"></div>`, '<button class="btn btn-secondary" data-close>取消</button><button class="btn btn-primary" id="clearConfirm">清除记录</button>');
        overlay.querySelector('#clearConfirm').onclick = event => runFormButton(event.currentTarget, overlay.querySelector('.form-result'), async () => {
            if (!roomReady) throw new Error('房间连接未就绪');
            const response = await connection.invoke('Dispatch', makeEnvelope('command', 'control.clearOnlineHistory', { targetUid: p.playerUid }));
            checkGatewayError(response);
            if (response?.payload?.ack !== true) throw new Error('服务端未确认清除');
            return { status: 'success', message: '服务端已清除该成员上线记录' };
        });
    };
}

function taskOptions(p, includeEmpty = true) {
    return (includeEmpty ? '<option value="">请选择任务</option>' : '')
        + (p.configGroups || []).map(n => `<option value="${webEscapeHtml(JSON.stringify(['group', n]))}">配置组 · ${webEscapeHtml(n)}</option>`).join('')
        + (p.oneClickConfigs || []).map(n => `<option value="${webEscapeHtml(JSON.stringify(['onedragon', n]))}">一条龙 · ${webEscapeHtml(n)}</option>`).join('');
}
function renderMemberSettings(p, host) {
    const uid = p.playerUid, draft = memberBinding(p);
    const originalBinding = JSON.stringify(draft);
    host.innerHTML = `<div class="detail-grid"><div>
        <section class="panel"><h2>联机任务顺序</h2><p class="note">同轮成员的任务数量、每个位置的类型必须一致，任务名称可以不同。</p><div id="bindingRows"></div><label class="field">添加配置组或一条龙<select id="bindingChoice">${taskOptions(p)}</select></label><button class="btn btn-secondary" id="addBinding">添加到末尾</button><button class="btn btn-primary" id="saveBindings">保存绑定</button><div class="form-result" id="bindingResult" role="status"></div></section>
        <section class="panel"><h2>定时上线</h2><label class="field">执行端本地时间<input type="time" id="scheduledTime" value="${webEscapeHtml(p.scheduledOnlineTime || '')}"></label><p class="note">按该执行端的本地时间触发；留空保存即可取消定时。</p><button class="btn btn-primary" id="saveSchedule">保存时间</button><div class="form-result" id="scheduleResult" role="status"></div></section>
        <section class="panel"><h2>成员快捷绑定</h2><p class="note">保存到该成员的助手。与“网页任务”中的浏览器本地绑定相互独立。</p><div id="quickBindingRows"></div></section>
        </div><section class="panel"><div class="panel-header"><h2>锄地后动作</h2><button class="text-button" id="reloadPolicy">重新读取</button></div><p class="note">先读取执行端现有设置，再编辑。恢复、停止和指定任务沿用 PC 规则。</p><div id="policyFields"><label class="field">完成后<select id="completionPolicy" disabled><option value="resume">恢复原任务</option><option value="stop">锄完停止</option><option value="runSpecified">执行指定任务</option></select></label><label class="field" id="specifiedTaskField" hidden>指定任务<select id="completionTask" disabled>${taskOptions(p)}</select></label><label class="field" id="priorityField" hidden>任务优先级<input type="number" id="completionPriority" step="1" min="-2147483648" max="2147483647" disabled></label></div><button class="btn btn-primary" id="savePolicy" disabled>保存结束动作</button><div class="form-result" id="policyResult" role="status">正在读取执行端设置…</div></section></div>`;
    let savedBinding = originalBinding;
    const drawBindings = () => {
        const rows = host.querySelector('#bindingRows');
        rows.innerHTML = draft.names.map((name, i) => `<div class="binding-row"><span class="order">${i + 1}</span><div class="binding-name">${webEscapeHtml(name)}<small>${draft.types[i] === 'onedragon' ? '一条龙' : '配置组'}</small></div><div class="binding-tools"><button data-move="${i}" data-delta="-1" aria-label="上移第${i + 1}项" ${i === 0 ? 'disabled' : ''}>↑</button><button data-move="${i}" data-delta="1" aria-label="下移第${i + 1}项" ${i === draft.names.length - 1 ? 'disabled' : ''}>↓</button><button data-remove="${i}" aria-label="删除第${i + 1}项">×</button></div></div>`).join('') || '<p class="note">尚未绑定，请添加至少一项。</p>';
        rows.querySelectorAll('[data-move]').forEach(b => b.onclick = () => {
            const i = Number(b.dataset.move), j = i + Number(b.dataset.delta);
            [draft.names[i], draft.names[j]] = [draft.names[j], draft.names[i]];
            [draft.types[i], draft.types[j]] = [draft.types[j], draft.types[i]];
            drawBindings();
        });
        rows.querySelectorAll('[data-remove]').forEach(b => b.onclick = () => { const i = Number(b.dataset.remove); draft.names.splice(i, 1); draft.types.splice(i, 1); drawBindings(); });
    };
    drawBindings();
    host.querySelector('#addBinding').onclick = () => {
        const value = host.querySelector('#bindingChoice').value;
        if (!value) return;
        const [type, name] = JSON.parse(value);
        if (draft.names.some((n, i) => n.toLowerCase() === name.toLowerCase() && draft.types[i] === type)) {
            host.querySelector('#bindingResult').textContent = '该任务已经添加，不能重复绑定'; return;
        }
        draft.names.push(name); draft.types.push(type); drawBindings();
    };
    host.querySelector('#saveBindings').onclick = event => runFormButton(event.currentTarget, host.querySelector('#bindingResult'), async () => {
        const latest = currentMember(uid);
        requireOnlineTargets([uid]); validateBinding(draft.names, draft.types, latest);
        if (JSON.stringify(memberBinding(latest)) !== savedBinding) throw new Error('成员绑定已在其他端改变，请重新打开设置后编辑');
        const submitted = { names: [...draft.names], types: [...draft.types] };
        const result = await sendMemberOperation(uid, 'bind_hoeing_group', { groupNames: submitted.names, groupTypes: submitted.types, groupIndex: 0 });
        if (result.status === 'success') {
            const member = currentMember(uid);
            if (member) { member.onlineHoeingGroupNames = submitted.names; member.onlineHoeingGroupTypes = submitted.types; }
            savedBinding = JSON.stringify(submitted);
        }
        return result;
    });
    host.querySelector('#saveSchedule').onclick = event => runFormButton(event.currentTarget, host.querySelector('#scheduleResult'), async () => {
        const value = host.querySelector('#scheduledTime').value;
        if (value && !/^([01]\d|2[0-3]):[0-5]\d$/.test(value)) throw new Error('请输入有效的时分');
        const result = await sendMemberOperation(uid, 'set_scheduled_online_time', { scheduledOnlineTime: value });
        if (result.status === 'success' && currentMember(uid)) currentMember(uid).scheduledOnlineTime = value;
        return result;
    });
    const quickHost = host.querySelector('#quickBindingRows');
    quickHost.innerHTML = quickKeys.map((key, i) => `<div class="quick-setting"><label class="field">${webEscapeHtml(key.replace('一键', ''))}<select data-quick-index="${i}">${taskOptions(p)}</select></label><button class="btn btn-secondary" data-save-quick="${i}">保存${webEscapeHtml(key.replace('一键', ''))}绑定</button><div class="form-result" data-quick-result="${i}" role="status"></div></div>`).join('');
    quickKeys.forEach((key, i) => {
        const old = parseQuickCommandBinding(p.quickCommands?.[key]);
        const select = quickHost.querySelector(`[data-quick-index="${i}"]`);
        if (old) select.value = JSON.stringify([old.isOneClick ? 'onedragon' : 'group', old.name]);
        quickHost.querySelector(`[data-save-quick="${i}"]`).onclick = event => runFormButton(event.currentTarget, quickHost.querySelector(`[data-quick-result="${i}"]`), async () => {
            if (!select.value) throw new Error('请选择绑定任务');
            const [type, name] = JSON.parse(select.value);
            const latest = currentMember(uid); requireOnlineTargets([uid]); validateBinding([name], [type], latest);
            const value = (type === 'onedragon' ? 'ONEDRAGON:' : 'GROUP:') + name;
            const result = await sendMemberOperation(uid, 'set_quick_command', { key, value });
            if (result.status === 'success' && currentMember(uid)) {
                currentMember(uid).quickCommands = { ...currentMember(uid).quickCommands, [key]: value };
            }
            return result;
        });
    });
    let loadedPolicy = null;
    const policySelect = host.querySelector('#completionPolicy'), taskSelect = host.querySelector('#completionTask');
    const priority = host.querySelector('#completionPriority'), savePolicy = host.querySelector('#savePolicy');
    const policyStatus = host.querySelector('#policyResult'), reload = host.querySelector('#reloadPolicy');
    const updateFields = () => {
        host.querySelector('#specifiedTaskField').hidden = policySelect.value !== 'runSpecified';
        host.querySelector('#priorityField').hidden = policySelect.value !== 'runSpecified';
    };
    policySelect.onchange = updateFields;
    const loadPolicy = async () => {
        reload.disabled = true; savePolicy.disabled = true; policySelect.disabled = true; taskSelect.disabled = true; priority.disabled = true; loadedPolicy = null;
        policyStatus.textContent = '正在读取执行端设置…';
        try {
            const result = await sendMemberOperation(uid, 'task_policy.pull', {}, 'task_policy.data');
            if (!host.querySelector('#policyResult') || activeMemberUid !== uid || detailSection !== 'settings') return;
            if (result.status !== 'success') throw new Error(result.message);
            loadedPolicy = normalizePolicy(result.data);
            policySelect.value = loadedPolicy.policy; priority.value = loadedPolicy.priority;
            taskSelect.value = JSON.stringify([loadedPolicy.taskType, loadedPolicy.taskName]);
            if (loadedPolicy.policy === 'runSpecified' && !taskSelect.value) {
                const option = new Option('原任务（当前目录未上报） · ' + loadedPolicy.taskName, JSON.stringify([loadedPolicy.taskType, loadedPolicy.taskName]));
                taskSelect.add(option); taskSelect.value = option.value;
            }
            policySelect.disabled = false; taskSelect.disabled = false; priority.disabled = false; savePolicy.disabled = false;
            updateFields(); policyStatus.textContent = '已读取执行端当前设置';
        } catch (err) { policyStatus.textContent = '读取未完成：' + err.message + '；未使用默认值覆盖原配置'; }
        finally { reload.disabled = false; }
    };
    reload.onclick = loadPolicy;
    savePolicy.onclick = event => runFormButton(event.currentTarget, policyStatus, async () => {
        if (!loadedPolicy) throw new Error('请先成功读取执行端设置');
        const policy = policySelect.value;
        let type = loadedPolicy.taskType, name = loadedPolicy.taskName;
        if (policy === 'runSpecified') {
            if (!taskSelect.value) throw new Error('请选择结束后执行的任务');
            [type, name] = JSON.parse(taskSelect.value);
            validateBinding([name], [type], currentMember(uid));
        }
        const rank = policy === 'runSpecified' ? Number(priority.value) : loadedPolicy.priority;
        if (!Number.isInteger(rank) || rank < -2147483648 || rank > 2147483647) throw new Error('任务优先级必须为有效整数');
        const result = await sendMemberOperation(uid, 'task_policy.push', { policy, specifiedType: type, specifiedName: name, specifiedPriority: String(rank) }, 'task_policy.push_result');
        if (result.status === 'success') {
            loadedPolicy = { policy, taskType: type, taskName: name, priority: rank };
            if (currentMember(uid)) currentMember(uid).hoeingCompletionAction = { ...loadedPolicy };
        }
        return result;
    });
    loadPolicy();
}

function snapshotForStart(targets) {
    return targets.map(p => ({ ...p, onlineHoeingGroupNames: [...(p.onlineHoeingGroupNames || [])],
        onlineHoeingGroupTypes: [...(p.onlineHoeingGroupTypes || [])], quickCommands: { ...p.quickCommands },
        hoeingCompletionAction: p.hoeingCompletionAction ? { ...p.hoeingCompletionAction } : null }));
}
function startSettingsSignature(p, key) {
    return JSON.stringify({ binding: key ? p.quickCommands?.[key] : memberBinding(p), action: p.hoeingCompletionAction });
}
function showCoordinatedConfirm(label) {
    const ids = [...selectedMemberUids];
    try { requireOnlineTargets(ids); } catch (err) { createModal('无法准备联机', `<p>${webEscapeHtml(err.message)}</p>`); return; }
    const targets = snapshotForStart(ids.map(uid => currentMember(uid)));
    const quickKey = label === '一键锄地' ? '' : '一键' + label;
    const errors = [];
    if (targets.length > 4) errors.push('同一联机批次最多 4 名成员，请返回调整选择');
    if (!serverCapabilities.includes('hoeing.start.v2')) errors.push('服务端未支持统一联机开始');
    if (quickKey && !serverCapabilities.includes('hoeing.quickCommandCompletion.v1')) errors.push('服务端未支持各成员快捷绑定的统一批次');
    let layout = null;
    for (const p of targets) {
        try {
            const binding = quickKey ? parseQuickCommandBinding(p.quickCommands?.[quickKey]) : null;
            if (quickKey && !binding) throw new Error(`尚未绑定「${label}」`);
            const { names, types } = quickKey ? { names: [binding.name], types: [binding.isOneClick ? 'onedragon' : 'group'] } : memberBinding(p);
            validateBinding(names, types, p);
            // 快捷指令允许每人的单项绑定类型不同；只有常规联机顺序要求相同布局。
            if (!quickKey && layout !== null && layout !== types.join(',')) throw new Error('绑定数量或每项类型顺序与其他成员不一致');
            layout = types.join(',');
        } catch (err) { errors.push(`${p.playerName || p.playerUid}：${err.message}`); }
    }
    const canUseActions = serverCapabilities.includes('hoeing.completionChoice.v1') && targets.every(p => isValidCompletionAction(p.hoeingCompletionAction));
    const overlay = createModal(`确认${label} · ${targets.length} 人`, `<p>全部成员准备完成后，才接管当前任务并开始联机。</p>${targets.map(p => {
        const binding = quickKey ? parseQuickCommandBinding(p.quickCommands?.[quickKey]) : null;
        const tasks = quickKey ? binding?.name || '尚未绑定' : memberBinding(p).names.join(' → ');
        return `<div class="confirmation-member">${avatarFor(p)}<div><strong>${webEscapeHtml(p.playerName || p.playerUid)}</strong><p>${webEscapeHtml(tasks)}</p><p>结束后：${webEscapeHtml(hoeingCompletionDescription(p.hoeingCompletionAction))}</p></div></div>`;
    }).join('')}<label class="check-row" style="margin-top:18px"><input type="checkbox" id="completionActions" ${canUseActions ? '' : 'disabled'}>执行各成员已有的锄地后动作</label><p class="note">${canUseActions ? '不勾选：本次锄完停止。失败或手动停止不会触发后续动作。' : '结束动作尚未上报完整或服务端暂不支持；本次可按锄完停止执行。'}</p>${errors.length ? `<p class="error">${errors.map(webEscapeHtml).join('<br>')}</p>` : ''}<div class="batch-feedback" hidden role="status"></div>`, `<button class="btn btn-secondary" data-close>返回调整</button><button class="btn btn-primary" id="startBatch" ${errors.length ? 'disabled' : ''}>确认开锄</button>`);
    overlay.querySelector('#startBatch').onclick = async event => {
        const button = event.currentTarget, cancel = overlay.querySelector('[data-close]'), option = overlay.querySelector('#completionActions');
        button.disabled = true; cancel.disabled = true; option.disabled = true;
        const feedback = overlay.querySelector('.batch-feedback'); feedback.hidden = false; feedback.textContent = '正在提交准备请求…';
        try {
            requireOnlineTargets(ids);
            if (targets.some(p => startSettingsSignature(currentMember(p.playerUid), quickKey) !== startSettingsSignature(p, quickKey)))
                throw new Error('成员绑定或结束动作已改变，本次未发送，请返回重新确认');
            const result = await beginCoordinatedQuickCommand(targets, canUseActions && option.checked, quickKey, label);
            feedback.textContent = result?.message || '受理结果未确认，请查看操作记录；未自动重发';
            if (result?.batch) { overlay.dataset.batchId = result.batch.batchId; onControlBatchState(result.batch); }
        } catch (err) { feedback.textContent = '未开始：' + err.message; }
        finally { button.hidden = true; cancel.disabled = false; cancel.textContent = '关闭'; }
    };
}
function showLegacyQuickMenu() {
    const overlay = createModal('网页任务绑定', '<p class="note">保留旧版保存在此浏览器的快捷任务。它将对已选成员执行同名配置；“联机快捷指令”使用的是各成员自己的绑定。</p><div class="action-grid">' + ['传奇', '次数盾', '精英', '小怪', '自定义'].map(key => `<button class="btn btn-secondary" data-key="${key}">${key}</button>`).join('') + '</div>');
    overlay.querySelectorAll('[data-key]').forEach(b => b.onclick = () => { overlay.remove(); showQuickCommandConfirm(b.dataset.key); });
}
renderControlRoom();
