// A 舰桥：仅网页状态与交互；执行权仍归现有助手和服务端。
const selectedMemberUids = new Set();
let selectionInitialized = false;
let activeMemberUid = null, activeMemberSource = null;
let detailSection = 'status';
let latestControlBatch = null;
const webEscapeHtml = escapeHtml;
const quickKeys = ['一键传奇', '一键次数盾', '一键精英', '一键小怪', '一键自定义'];
const renderedMemberEntries = new Map();
// 房间适配器管理权威成员快照。已获执行端确认的设置只镜像到该来源的当前快照；
// 下一份真实状态上报自然替换镜像，不能将旁观成员写进主房批次名单。
const confirmedMemberFields = new WeakMap();
function memberViewFields(member, source) {
    const fields = member && source && confirmedMemberFields.get(member)?.get(memberSourceKey(member.playerUid, source));
    return fields ? { ...member, ...fields } : member;
}
function currentMember(uid = activeMemberUid, source = undefined) {
    if (arguments.length === 0) source = activeMemberSource;
    const entry = readMemberEntry(uid, source);
    return memberViewFields(entry?.member, entry?.source);
}
function reflectConfirmedMemberFields(uid, source, fields, before = null) {
    const entry = readMemberEntry(uid, source);
    if (!entry) return;
    if (!source) { Object.assign(entry.member, fields); return; }
    // 若回执到达时已收到该来源的新值，不以本次确认的旧值覆盖更近的状态上报。
    const current = memberViewFields(entry.member, source), equal = (a, b) => JSON.stringify(a) === JSON.stringify(b);
    if (before && Object.keys(fields).some(key => !equal(current[key], before[key]) && !equal(current[key], fields[key]))) return;
    if (!confirmedMemberFields.has(entry.member)) confirmedMemberFields.set(entry.member, new Map());
    const mirrors = confirmedMemberFields.get(entry.member), key = memberSourceKey(uid, source);
    mirrors.set(key, { ...mirrors.get(key), ...fields });
}
function getSelectedMembers() {
    if (!webMemberRooms()) return players.filter(p => selectedMemberUids.has(p.playerUid));
    return [...new Set((mainMemberContext()?.players || []).map(p => p.playerUid))]
        .filter(uid => selectedMemberUids.has(uid)).map(uid => currentMember(uid)).filter(Boolean);
}
function displayedMemberEntries() {
    const rooms = webMemberRooms();
    return rooms ? rooms.getDisplayedMembers() : players.map(p => legacyMemberEntry(p.playerUid, p));
}
function renderedCardEntry(element) {
    return renderedMemberEntries.get(element.closest?.('[data-member-key]')?.dataset.memberKey);
}
function memberCanOperate(uid, source = undefined, online = true) {
    try { requireMemberTarget(uid, source, online); return true; } catch { return false; }
}
function memberResultAttributes(uid, source) {
    return `data-result-uid="${webEscapeHtml(uid)}"${source ? ` data-result-key="${webEscapeHtml(memberSourceKey(uid, source))}"` : ''}`;
}
function memberSourceDisplayed(uid, source) {
    return displayedMemberEntries().some(entry => entry.member.playerUid === uid
        && memberSourceKey(uid, entry.source) === memberSourceKey(uid, source));
}
function avatarFor(p) {
    const names = ['Furina', 'Ayaka', 'Kazuha', 'Zhongli'];
    const hash = [...String(p.playerUid)].reduce((n, c) => (n * 31 + c.charCodeAt(0)) >>> 0, 0);
    return `<img class="avatar" src="gengdi-avatars/${names[hash % names.length]}.png" alt="">`;
}
function memberState(p, ready = roomReady) {
    if (!p.online) return { label: '助手离线', tone: 'muted' };
    if (!ready) return { label: '连接中断 · 状态待确认', tone: 'danger' };
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
    if (typeof webCloseOwnerView === 'function') webCloseOwnerView();
    const rooms = webMemberRooms();
    // 只在显式返回房间成员时离开编排视图，不在渲染时自动进出 owner 页面。
    if (rooms?.getView().mode === 'orchestration') rooms.setView?.('current');
    document.getElementById('homeView').hidden = false;
    document.getElementById('detailView').hidden = true;
    document.getElementById('homeTab').classList.add('active');
    document.getElementById('detailTab').classList.remove('active');
    document.getElementById('detailTab').disabled = !activeMemberUid || !memberSourceDisplayed(activeMemberUid, activeMemberSource);
    renderSelectionBar();
    window.bgiWebUpdate?.refreshHints();
    scheduleMemberCardAlignment();
}
function openMemberDetail(uid, source = undefined) {
    // detailTab 仍调用旧的 UID 入口，但它必须重开原先的那张卡，不能重新按 UID 匹配到其他房间。
    const reopening = source === undefined && uid === activeMemberUid;
    const entry = readMemberEntry(uid, reopening ? activeMemberSource : source);
    if (!entry || reopening && !memberSourceDisplayed(uid, entry.source)) {
        log('原成员卡片已变化或不在当前查看范围，请重新选择'); return;
    }
    if (typeof webCloseOwnerView === 'function') webCloseOwnerView();
    activeMemberUid = uid;
    activeMemberSource = entry.source;
    detailSection = 'status';
    document.getElementById('homeView').hidden = true;
    document.getElementById('detailView').hidden = false;
    document.getElementById('detailTab').disabled = false;
    document.getElementById('homeTab').classList.remove('active');
    document.getElementById('detailTab').classList.add('active');
    renderDetailHeader(); renderDetailTabs(); renderDetailContent(); renderSelectionBar();
    window.scrollTo({ top: 0, behavior: 'smooth' });
}
function mainOnlineSelectionUids() {
    const roster = webMemberRooms() ? mainMemberContext()?.players || [] : players;
    return [...new Set(roster.filter(p => p.online && (!webMemberRooms() || memberCanOperate(p.playerUid))).map(p => p.playerUid))];
}
function toggleSelectedMembers() {
    if (!mainBulkViewAllowed()) return;
    const online = mainOnlineSelectionUids();
    const all = online.length && online.every(uid => selectedMemberUids.has(uid));
    selectedMemberUids.clear();
    if (!all) online.forEach(uid => selectedMemberUids.add(uid));
    renderControlRoom();
}
function renderControlRoom() {
    const rooms = webMemberRooms(), view = rooms?.getView() || { mode: 'current' };
    const entries = displayedMemberEntries(), bulk = mainBulkViewAllowed(), allRooms = view.mode === 'all';
    const mainRoster = rooms ? mainMemberContext()?.players || [] : players;
    const onlineMain = mainOnlineSelectionUids();
    if (!selectionInitialized && mainRoster.length && (!rooms || roomReady)) {
        onlineMain.forEach(uid => selectedMemberUids.add(uid));
        selectionInitialized = true;
    }
    const online = entries.filter(({member}) => member.online).length;
    const running = entries.filter(({member}) => member.online && member.taskRunning && member.taskStatusAvailable !== false).length;
    const batchMember = !allRooms && bulk && entries.map(x => x.member).find(p => p.hoeingBatchId && ['registering', 'preparing', 'takeover', 'running', 'cleaning', 'stopping', 'cleanupPending'].includes(p.hoeingStartPhase));
    const viewContext = !bulk && rooms?.getContext?.(view.roomKey);
    const title = allRooms ? '所有房间' : !bulk ? viewContext?.displayName || viewContext?.name || '房间成员' : batchMember ? batchPhaseName(batchMember.hoeingStartPhase) : '准备一起开锄';
    const summary = allRooms ? '每张卡片使用其来源房间；批量操作仅用于当前控制房间。' : !bulk ? '仅对所选成员进行单卡操作。整房联机请先切换控制房间。'
        : batchMember ? batchMember.hoeingStartMessage || '成员正在执行当前联机批次' : '选择成员，检查各自安排。全部准备就绪后接管当前任务。';
    document.getElementById('roomSummary').innerHTML = `<section class="hero"><div><p class="eyebrow">${online} 人在线 · ${running} 人正在执行任务</p><h1>${webEscapeHtml(title)}</h1><p>${webEscapeHtml(summary)}</p></div><div class="hero-metric"><b>${bulk ? selectedMemberUids.size : entries.length}</b><span>${allRooms ? '控制房已选' : bulk ? '已选成员' : '房间成员'}</span></div></section>`;
    document.getElementById('memberCount').textContent = entries.length;
    const selectAll = document.getElementById('selectAll');
    selectAll.hidden = !bulk;
    selectAll.textContent = onlineMain.length && onlineMain.every(uid => selectedMemberUids.has(uid)) ? '取消全选' : '选择在线成员';
    const quickSection = document.getElementById('quickRow')?.closest?.('.quick-section');
    if (quickSection) quickSection.hidden = !bulk;
    const updateNav = document.getElementById('bgiUpdateNav');
    if (updateNav) updateNav.hidden = !bulk;
    document.querySelectorAll('.quick-btn').forEach(b => { b.disabled = !bulk || !roomReady || !selectedMemberUids.size; });
    const list = document.getElementById('memberList');
    renderedMemberEntries.clear();
    list.innerHTML = entries.map(entry => {
        const { context, source } = entry, p = memberViewFields(entry.member, source);
        const ready = context.ready && (!context.isMain || roomReady), selectable = bulk && context.isMain;
        const state = memberState(p, ready), selected = selectable && selectedMemberUids.has(p.playerUid);
        const key = memberSourceKey(p.playerUid, source);
        renderedMemberEntries.set(key, entry);
        const taskAvailable = ready && p.online;
        const presenceText = !ready ? '待确认' : !p.online ? '离线' : p.onlineReady ? '上线' : '未上线';
        const presence = !ready ? '连接待确认' : !p.online ? '助手离线' : `助手在线 · 锄地${presenceText}`;
        const presenceClass = !ready ? 'presence-unknown' : !p.online ? 'presence-offline' : p.onlineReady ? 'presence-online' : 'presence-not-ready';
        const task = p.taskRunning ? p.currentTaskName || '正在执行任务' : p.bgiStatus === 'stopped' ? '可远程启动 BGI' : '等待下一项任务';
        const version = context.isMain ? window.bgiWebUpdate?.renderVersion(p) ?? webEscapeHtml(p.bgiVersion || '版本未上报') : webEscapeHtml(p.bgiVersion || '版本未上报');
        return `<article class="member-card ${selected ? 'selected' : ''} ${taskAvailable ? 'assistant-online' : ''} ${p.online ? '' : 'offline'}" data-member-uid="${webEscapeHtml(p.playerUid)}" data-member-key="${webEscapeHtml(key)}">
            <div class="member-head">${avatarFor(p)}<div class="member-identity"><button data-ui="detail" data-uid="${webEscapeHtml(p.playerUid)}">${webEscapeHtml(p.playerName || p.playerUid)}</button><small>${webEscapeHtml(p.playerUid)}</small>${allRooms ? `<span class="wr-member-source" title="${webEscapeHtml(context.serverUrl)}">房间：${webEscapeHtml(memberRoomLabel(context))}</span>` : ''}</div>${selectable ? `<button class="member-select" data-ui="select" data-uid="${webEscapeHtml(p.playerUid)}" aria-label="选择${webEscapeHtml(p.playerName || p.playerUid)}" aria-pressed="${selected}" ${!selected && (!p.online || source && !memberCanOperate(p.playerUid, source)) ? 'disabled' : ''}><span>${selected ? '✓' : ''}</span></button>` : ''}</div>
            <div class="member-version-row"><span class="version-label">BGI</span><span class="member-version">${version}</span><span class="presence ${presenceClass}" title="${presence}" aria-label="${presence}"><span class="presence-dot" aria-hidden="true"></span><span class="presence-visible" aria-hidden="true">${presenceText}</span><span class="sr-only">${presence}</span></span></div>
            <div class="member-body"><div class="status-row"><span>当前任务</span><span class="${taskAvailable ? state.tone : 'muted'}">${webEscapeHtml(taskAvailable ? state.label : '状态待确认')}</span></div><div class="task-name${taskAvailable && p.taskRunning ? ' task-active' : ''}">${webEscapeHtml(taskAvailable ? task : '当前状态待确认')}</div><p class="task-route">${webEscapeHtml(taskAvailable ? p.currentTaskGroupName || '' : ready ? '助手离线后无法获取实时任务' : '网页连接中断，无法确认实时任务')}</p><p class="task-route">${webEscapeHtml(taskAvailable ? p.currentRouteDisplay || p.currentScriptRouteName || '' : p.bgiVersion ? '上次版本仅供参考' : '')}</p><p class="task-route">${webEscapeHtml(taskAvailable ? p.hoeingStartMessage || p.scriptTaskProgress || p.friendshipProgress || '' : '')}</p></div>
            <div class="member-foot"><span>${webEscapeHtml(hoeingCompletionDescription(p.hoeingCompletionAction))}</span><button data-ui="detail" data-uid="${webEscapeHtml(p.playerUid)}">管理 ›</button></div><div class="member-result" ${memberResultAttributes(p.playerUid, source)} hidden></div></article>`;
    }).join('') || (view.mode === 'current' ? '<div class="section-empty">暂无成员连接。请使用 PC 助手加入相同控制房间。</div>'
        : '<div class="section-empty">暂无已确认成员，请检查所查看房间的连接状态。</div>');
    renderMemberResults(); renderSelectionBar();
    window.bgiWebUpdate?.refreshHints();
    scheduleMemberCardAlignment();
    const detailVisible = !document.getElementById('detailView').hidden;
    if (detailVisible) {
        renderDetailHeader();
        // Status broadcasts must not destroy edits in progress, except when their exact source is gone.
        if (!currentMember() || detailSection === 'status' || detailSection === 'history') renderDetailContent();
    } else if (activeMemberUid) {
        document.getElementById('detailTab').disabled = !memberSourceDisplayed(activeMemberUid, activeMemberSource);
    }
}
function renderMemberResults() {
    document.querySelectorAll('[data-result-uid]').forEach(el => {
        const result = memberCommandResults.get(el.dataset.resultKey || el.dataset.resultUid);
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
    const bar = document.getElementById('selectionBar'), bulk = mainBulkViewAllowed();
    bar.hidden = !bulk || document.getElementById('controlPanel').style.display === 'none' || document.getElementById('homeView').hidden;
    const unavailable = [...selectedMemberUids].some(uid => !currentMember(uid)?.online || webMemberRooms() && !memberCanOperate(uid));
    const allRooms = webMemberRooms()?.getView().mode === 'all';
    bar.innerHTML = `<div class="selection-inner"><div><strong>${allRooms ? '控制房间' : ''}已选 ${selectedMemberUids.size} 人</strong><small>${unavailable ? '含离线或已离开成员，请重新选择' : '按各自绑定执行'}</small></div><div class="selection-actions"><button class="btn btn-secondary" onclick="showBatchTools()" ${!bulk || !roomReady || !selectedMemberUids.size ? 'disabled' : ''}>批量操作</button><button class="btn btn-primary" onclick="showCoordinatedConfirm('一键锄地')" ${!bulk || !roomReady || !selectedMemberUids.size || unavailable ? 'disabled' : ''}>准备联机</button></div></div>`;
}
document.getElementById('memberList').addEventListener('click', event => {
    const button = event.target.closest('[data-ui]');
    if (!button || button.disabled) return;
    const uid = button.dataset.uid, scoped = !!webMemberRooms(), rendered = renderedCardEntry(button);
    if (scoped && (!rendered?.source || rendered.member.playerUid !== uid)) {
        log('原成员卡片已变化，请重新选择'); return;
    }
    const source = scoped ? rendered.source : null;
    if (button.dataset.ui === 'detail') openMemberDetail(uid, source);
    if (button.dataset.ui === 'bgi-update') {
        const entry = readMemberEntry(uid, source);
        if (entry?.context.isMain && entry.member.clientInstanceId === button.dataset.instance) window.bgiWebUpdate?.open(entry.member);
    }
    if (button.dataset.ui === 'select') {
        const entry = readMemberEntry(uid, source);
        if (!mainBulkViewAllowed() || !entry?.context.isMain) return;
        if (selectedMemberUids.has(uid)) selectedMemberUids.delete(uid);
        else if (entry.member.online && (!scoped || memberCanOperate(uid, source))) selectedMemberUids.add(uid);
        renderControlRoom();
    }
});
function renderDetailHeader() {
    const source = activeMemberSource, entry = readMemberEntry(activeMemberUid, source), p = memberViewFields(entry?.member, source);
    const host = document.getElementById('detailHeader');
    if (!p) { host.innerHTML = '<button class="back-button" onclick="showHome()">‹ 返回房间</button><p class="error">原成员连接或实例已变化，操作前请重新选择。</p>'; return; }
    const state = memberState(p, entry.context.ready);
    const room = source && (!entry.context.isMain || webMemberRooms()?.getView().mode === 'all')
        ? `<span class="wr-member-source" title="${webEscapeHtml(entry.context.serverUrl)}">房间：${webEscapeHtml(memberRoomLabel(entry.context))}</span>` : '';
    host.innerHTML = `<button class="back-button" onclick="showHome()">‹ 返回房间</button><div class="detail-top">${avatarFor(p)}<div class="detail-identity"><h1>${webEscapeHtml(p.playerName || p.playerUid)}</h1><div class="muted detail-meta"><span class="detail-meta-text">${webEscapeHtml(p.playerUid)} · <span class="${state.tone}">${webEscapeHtml(state.label)}</span> · BGI ${webEscapeHtml(p.bgiVersion || '版本未上报')}</span>${room}</div>${!entry.context.isMain ? '<p class="note">此卡暂不支持更新 BGI，请切换到该控制房间或在执行端本机更新。</p>' : ''}</div></div>`;
    if (entry.context.isMain && window.bgiWebUpdate?.targetFor(p)) {
        const update = document.createElement('button');
        update.className = 'text-button detail-bgi-update'; update.type = 'button'; update.textContent = '更新 BGI';
        update.onclick = () => {
            const latest = readMemberEntry(p.playerUid, source);
            if (latest?.context.isMain) window.bgiWebUpdate.open(latest.member);
            else log('原控制房间或成员实例已变化，未打开更新');
        };
        host.querySelector('.detail-meta').append(update);
        host.querySelector('.detail-identity').classList.add('detail-identity-with-update');
    }
}
function renderDetailTabs() {
    const tabs = [['status', '状态'], ['tasks', '任务'], ['settings', '联机设置'], ['history', '记录']];
    const host = document.getElementById('detailTabs');
    host.innerHTML = tabs.map(([id, label]) => `<button data-section="${id}" class="${detailSection === id ? 'active' : ''}">${label}</button>`).join('');
    host.querySelectorAll('button').forEach(b => b.onclick = () => { detailSection = b.dataset.section; renderDetailTabs(); renderDetailContent(); });
}
function renderDetailContent() {
    const p = currentMember(), source = activeMemberSource, host = document.getElementById('detailContent');
    if (!p) { host.innerHTML = '<p class="note">原成员连接或实例已变化，请返回重新选择；此详情不会再发送新操作。</p>'; return; }
    if (detailSection === 'status') renderStatusDetail(p, host, source);
    if (detailSection === 'tasks') renderTaskCatalog(p, host, source);
    if (detailSection === 'settings') renderMemberSettings(p, host, source);
    if (detailSection === 'history') renderOnlineHistory(p, host, source);
}
function renderStatusDetail(p, host, source = undefined) {
    const entry = readMemberEntry(p.playerUid, source);
    if (!entry) { host.innerHTML = '<p class="note">原成员来源已失效，请重新选择。</p>'; return; }
    source = entry.source;
    const state = memberState(p, entry.context.ready), disabled = !memberCanOperate(p.playerUid, source) ? 'disabled' : '';
    const progress = p.autoHoeingProgress || p.scriptTaskProgress || p.friendshipProgress;
    host.innerHTML = `<div class="status-stack"><section class="panel"><div class="panel-header run-header"><div class="run-title"><span class="run-label">当前运行</span><h2>${webEscapeHtml(p.currentTaskName || (p.taskStatusAvailable === false ? '任务状态待确认' : '暂无运行任务'))}</h2></div><span class="run-state ${state.tone}">${webEscapeHtml(state.label)}</span></div>
        <div class="kv-strip">${[['配置组 / 一条龙', p.currentTaskGroupName], ['线路', p.currentRouteDisplay || p.currentScriptRouteName], ['联机状态', p.hoeingStartMessage], ['定时上线', p.scheduledOnlineTime || '未设置'], ['上线标记', p.onlineReady ? '已上线，等待联机' : '未上线']].filter(([, v]) => v).map(([k, v]) => `<div class="kv"><span>${webEscapeHtml(k)}</span><b>${webEscapeHtml(v)}</b></div>`).join('')}</div>
        ${progress ? `<div class="run-progress"><span>执行进度</span><p>${webEscapeHtml(progress)}</p></div>` : ''}
        <div class="action-grid"><button class="btn btn-secondary" data-op="start_bgi" ${disabled}>启动 BGI</button><button class="btn btn-secondary danger" data-op="stop" ${disabled}>停止任务</button><button class="btn btn-secondary" data-op="clear_online" ${disabled}>清除上线</button><button class="btn btn-secondary danger" data-op="close_game" ${disabled}>关闭游戏</button><button class="btn btn-secondary" id="detailReset" ${disabled || memberResetBusy(source) ? 'disabled' : ''}>重置状态</button></div>${p.online && disabled ? '<p class="note">房间连接或成员实例未唯一确认，暂不能操作；请核对后重新选择。</p>' : ''}</section>
        <section class="panel plan-panel"><div class="panel-header"><h2>联机安排</h2><button id="editBindings" class="text-button">编辑联机安排 ›</button></div><div class="plan-items">${memberBinding(p).names.map((n, i) => `<div class="binding-row"><span class="order">${i + 1}</span><div class="binding-name">${webEscapeHtml(n)}<small>${memberBinding(p).types[i] === 'onedragon' ? '一条龙' : '配置组'}</small></div></div>`).join('') || '<p class="note">尚未绑定联机任务。</p>'}</div><p class="note plan-after">结束后：${webEscapeHtml(hoeingCompletionDescription(p.hoeingCompletionAction))}</p><div ${memberResultAttributes(p.playerUid, source)} class="member-result" hidden></div></section></div>`;
    host.querySelectorAll('[data-op]').forEach(b => b.onclick = () => showMemberOperation([p.playerUid], b.dataset.op, {}, source));
    host.querySelector('#detailReset').onclick = () => showResetStateModal(p.playerUid, p.playerName || p.playerUid, source);
    host.querySelector('#editBindings').onclick = () => { detailSection = 'settings'; renderDetailTabs(); renderDetailContent(); };
    renderMemberResults();
}
function renderTaskCatalog(p, host, source = undefined) {
    const entry = readMemberEntry(p.playerUid, source);
    if (!entry) { host.innerHTML = '<p class="note">原成员来源已失效，请重新选择。</p>'; return; }
    source = entry.source;
    let type = 'group';
    host.innerHTML = `<div class="panel"><h2>任务目录</h2><p class="note">选择配置后可查看任务起点与启用状态；执行前会再次检查目标成员。</p><input id="catalogSearch" type="search" placeholder="搜索配置名称" aria-label="搜索配置名称"><div class="filter-tabs"><button class="active" data-kind="group">配置组</button><button data-kind="oneclick">一条龙</button><button data-kind="hotkey">快捷键</button></div><div id="catalogItems" class="catalog-grid"></div><div class="member-result" ${memberResultAttributes(p.playerUid, source)} role="status" hidden></div></div>`;
    const draw = () => {
        const current = currentMember(p.playerUid, source), query = host.querySelector('#catalogSearch').value.toLowerCase();
        const items = type === 'hotkey' ? (current?.hotkeys || []).map(h => ({ name: h.functionName || h.configName, value: h.configName, sub: h.hotkeyText || '' }))
            : ((type === 'oneclick' ? current?.oneClickConfigs : current?.configGroups) || []).map(name => ({ name, value: name, sub: type === 'oneclick' ? '一条龙' : '配置组' }));
        const filtered = items.filter(x => String(x.name).toLowerCase().includes(query));
        const grid = host.querySelector('#catalogItems');
        const usable = memberCanOperate(p.playerUid, source);
        grid.innerHTML = filtered.map((item, i) => `<button class="catalog-item" data-index="${i}" ${!usable ? 'disabled' : ''}>${webEscapeHtml(item.name)}<small>${webEscapeHtml(item.sub)}</small></button>`).join('') || '<p class="note">没有匹配的项目，等待执行端上报。</p>';
        grid.querySelectorAll('button').forEach(b => b.onclick = () => {
            const item = filtered[Number(b.dataset.index)];
            if (!item || b.disabled || !memberCanOperate(p.playerUid, source)) { log('原成员连接或实例已变化，请重新选择'); return; }
            if (type === 'hotkey') showMemberOperation([p.playerUid], 'hotkey_execute', { hotkeyConfigName: item.value }, source);
            else showMemberConfirmModal(p.playerUid, p.playerName, item.value, type === 'oneclick', source);
        });
    };
    host.querySelector('#catalogSearch').oninput = draw;
    host.querySelectorAll('[data-kind]').forEach(b => b.onclick = () => { type = b.dataset.kind; host.querySelectorAll('[data-kind]').forEach(x => x.classList.toggle('active', x === b)); draw(); });
    draw();
    renderMemberResults();
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
function showMemberOperation(targets, operation, parameters = {}, source = undefined, frozenTargets = null, mainScope = captureMainMemberScope()) {
    const frozen = [...targets], bulk = source === undefined;
    let sources;
    try {
        sources = frozenTargets || freezeMemberTargets(frozen, source);
        if (bulk) requireMainMemberTargets(mainScope, frozen, sources);
        else {
            if (frozen.length !== 1 || memberSourceKey(frozen[0], sources.get(frozen[0])) !== memberSourceKey(frozen[0], source))
                throw new Error('单卡操作必须保留原成员来源');
            requireFrozenMemberTargets(frozen, sources);
        }
    } catch (err) { createModal('无法操作成员', `<p>${webEscapeHtml(err.message)}</p>`); return; }
    const names = new Map(frozen.map(uid => [uid, memberTargetLabel(readMemberEntry(uid, sources.get(uid)), uid)]));
    const stopBatches = new Map(frozen.map(uid => [uid, currentMember(uid, sources.get(uid))?.hoeingBatchId || '']));
    const overlay = createModal(commandTitles[operation] || '确认操作', `<p>对以下 ${frozen.length} 名成员${webEscapeHtml(commandTitles[operation] || '执行操作')}：</p><p class="note">${[...names.values()].map(webEscapeHtml).join('、')}</p>${operation === 'stop' ? '<p class="note">停止联机中的成员可能中止共享批次。</p><label class="field"><input type="checkbox" id="stopCloseBgi"> 同时关闭 BGI（默认不勾选；停止任务后结束执行端的 BGI 进程）</label>' : ''}<div class="form-result" role="status"></div>`, '<button class="btn btn-secondary" data-close>取消</button><button class="btn btn-primary" id="operationConfirm">确认</button>');
    overlay.querySelector('#operationConfirm').onclick = async event => {
        const button = event.currentTarget, cancel = overlay.querySelector('[data-close]');
        if (button.disabled) return;
        const closeBgi = operation === 'stop' && overlay.querySelector('#stopCloseBgi')?.checked === true;
        button.disabled = true; cancel.disabled = true;
        try {
            if (bulk) requireMainMemberTargets(mainScope, frozen, sources);
            else requireFrozenMemberTargets(frozen, sources);
            const results = await Promise.all(frozen.map(async uid => {
                try {
                    const bound = sources.get(uid);
                    if (bound && operation === 'hotkey_execute' && !(requireMemberTarget(uid, bound).member.hotkeys || []).some(h => h.configName === parameters.hotkeyConfigName))
                        throw new Error('该成员快捷键已变化，请重新选择');
                    const params = operation === 'stop' ? { ...parameters, hoeingBatchId: stopBatches.get(uid), ...(closeBgi ? { closeBgi: 'true' } : {}) } : parameters;
                    return { uid, ...(await sendMemberOperation(uid, operation, params, null, bound)) };
                } catch (err) { return { uid, status: 'failed', message: err.message }; }
            }));
            overlay.querySelector('.form-result').textContent = results.map(r => `${names.get(r.uid)}：${formatResult(r)}`).join('\n');
        } catch (err) { overlay.querySelector('.form-result').textContent = err.message; }
        finally { button.hidden = true; cancel.disabled = false; cancel.textContent = '关闭'; }
    };
}
function showBatchTools() {
    const targets = [...selectedMemberUids], scope = captureMainMemberScope();
    let sources;
    try { requireMainMemberScope(scope); sources = freezeMemberTargets(targets); }
    catch (err) { createModal('无法批量操作', `<p>${webEscapeHtml(err.message)}</p>`); return; }
    const overlay = createModal(`已选 ${targets.length} 人 · 批量操作`, '<p class="note">下一步会再次列出目标并确认，不会扩大到未选成员。</p><div class="action-grid"><button class="btn btn-secondary" data-op="start_bgi">启动 BGI</button><button class="btn btn-secondary danger" data-op="stop">停止任务</button><button class="btn btn-secondary" data-op="clear_online">清除上线</button><button class="btn btn-secondary" id="batchSchedule">定时上线</button></div>');
    overlay.querySelectorAll('[data-op]').forEach(b => b.onclick = () => { overlay.remove(); showMemberOperation(targets, b.dataset.op, {}, undefined, sources, scope); });
    overlay.querySelector('#batchSchedule').onclick = () => {
        overlay.remove();
        const form = createModal('批量设置定时上线', `<p>目标：${targets.map(uid => webEscapeHtml(memberTargetLabel(readMemberEntry(uid, sources.get(uid)), uid))).join('、')}</p><label class="field">执行端本地时间<input type="time" id="batchTime"></label><p class="note">留空表示取消定时。按各执行端的本地时间触发。</p>`, '<button class="btn btn-secondary" data-close>取消</button><button class="btn btn-primary" id="batchSave">确认设置</button>');
        form.querySelector('#batchSave').onclick = () => { const time = form.querySelector('#batchTime').value; form.remove(); showMemberOperation(targets, 'set_scheduled_online_time', { scheduledOnlineTime: time }, undefined, sources, scope); };
    };
}
function renderOnlineHistory(p, host, source = undefined) {
    const entry = readMemberEntry(p.playerUid, source);
    if (!entry) { host.innerHTML = '<p class="note">原成员来源已失效，请重新选择。</p>'; return; }
    source = entry.source;
    const rows = p.onlineHistory || [], canClear = memberCanOperate(p.playerUid, source, false);
    host.innerHTML = `<section class="panel"><div class="panel-header"><h2>上线记录</h2><button id="clearHistory" class="text-button" ${canClear ? '' : 'disabled'}>清除该成员记录</button></div><p class="note">这是服务端当前保存的上线消费记录，不是完整任务日志。</p>${rows.map(row => `<div class="history-row"><strong>${row.mode === 'scheduled' ? '定时上线' : row.mode === 'command' ? '命令上线' : webEscapeHtml(row.mode || '上线')}</strong><p class="muted">上线 ${webEscapeHtml(row.onlineTime || '—')}　联机 ${webEscapeHtml(row.consumeTime || '—')}</p></div>`).join('') || '<p class="note">暂无上线记录。</p>'}</section>`;
    host.querySelector('#clearHistory').onclick = () => {
        const overlay = createModal('清除上线记录', `<p>确认清除 ${webEscapeHtml(memberTargetLabel(entry, p.playerUid))} 的上线记录？不会清除定时设置或任务配置。</p><div class="form-result" role="status"></div>`, '<button class="btn btn-secondary" data-close>取消</button><button class="btn btn-primary" id="clearConfirm">清除记录</button>');
        overlay.querySelector('#clearConfirm').onclick = event => runFormButton(event.currentTarget, overlay.querySelector('.form-result'), async () => {
            const response = await invokeMemberGateway(p.playerUid, source, 'control.clearOnlineHistory', { targetUid: p.playerUid }, false);
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
function renderMemberSettings(p, host, source = undefined) {
    const uid = p.playerUid, entry = readMemberEntry(uid, source), draft = memberBinding(p);
    if (!entry) { host.innerHTML = '<p class="note">原成员来源已失效，请重新选择。</p>'; return; }
    source = entry.source;
    const originalBinding = JSON.stringify(draft);
    host.innerHTML = `<div class="detail-grid"><div>
        <section class="panel"><h2>联机任务顺序</h2><p class="note">同轮成员的任务数量、每个位置的类型必须一致，任务名称可以不同。</p><div id="bindingRows"></div><label class="field">添加配置组或一条龙<select id="bindingChoice">${taskOptions(p)}</select></label><button class="btn btn-secondary" id="addBinding">添加到末尾</button><button class="btn btn-primary" id="saveBindings">保存绑定</button><div class="form-result" id="bindingResult" role="status"></div></section>
        <section class="panel"><h2>定时上线</h2><label class="field">执行端本地时间<input type="time" id="scheduledTime" value="${webEscapeHtml(p.scheduledOnlineTime || '')}"></label><p class="note">按该执行端的本地时间触发；留空保存即可取消定时。</p><button class="btn btn-primary" id="saveSchedule">保存时间</button><div class="form-result" id="scheduleResult" role="status"></div></section>
        <section class="panel"><h2>成员快捷绑定</h2><p class="note">保存到该成员的助手。与“网页任务”中的浏览器本地绑定相互独立。</p><div id="quickBindingRows"></div></section>
        </div><section class="panel" id="completionSettings"></section></div>`;
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
        const latest = currentMember(uid, source);
        requireOnlineTargets([uid], source); validateBinding(draft.names, draft.types, latest);
        if (JSON.stringify(memberBinding(latest)) !== savedBinding) throw new Error('成员绑定已在其他端改变，请重新打开设置后编辑');
        const submitted = { names: [...draft.names], types: [...draft.types] };
        const result = await sendMemberOperation(uid, 'bind_hoeing_group', { groupNames: submitted.names, groupTypes: submitted.types, groupIndex: 0 }, null, source);
        if (result.status === 'success') {
            reflectConfirmedMemberFields(uid, source, { onlineHoeingGroupNames: submitted.names, onlineHoeingGroupTypes: submitted.types }, latest);
            savedBinding = JSON.stringify(submitted);
        }
        return result;
    });
    host.querySelector('#saveSchedule').onclick = event => runFormButton(event.currentTarget, host.querySelector('#scheduleResult'), async () => {
        const value = host.querySelector('#scheduledTime').value;
        if (value && !/^([01]\d|2[0-3]):[0-5]\d$/.test(value)) throw new Error('请输入有效的时分');
        const before = currentMember(uid, source);
        const result = await sendMemberOperation(uid, 'set_scheduled_online_time', { scheduledOnlineTime: value }, null, source);
        if (result.status === 'success') reflectConfirmedMemberFields(uid, source, { scheduledOnlineTime: value }, before);
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
            const latest = currentMember(uid, source); requireOnlineTargets([uid], source); validateBinding([name], [type], latest);
            const value = (type === 'onedragon' ? 'ONEDRAGON:' : 'GROUP:') + name;
            const result = await sendMemberOperation(uid, 'set_quick_command', { key, value }, null, source);
            const member = currentMember(uid, source);
            if (result.status === 'success' && member && (!source || member.quickCommands?.[key] === latest.quickCommands?.[key] || member.quickCommands?.[key] === value))
                reflectConfirmedMemberFields(uid, source, { quickCommands: { ...member.quickCommands, [key]: value } });
            return result;
        });
    });
    initializeCompletionPolicyEditor(uid, host.querySelector('#completionSettings'), source);
}

// 与联机任务顺序、快捷绑定的 taskOptions 分开；计划表只用于完成后动作。
function completionTargetOptions(member, type, catalog, action) {
    const entries = type === 'workflow' ? (catalog?.workflows || []).map(flow => [flow.name, flow.workflowId])
        : ((type === 'onedragon' ? catalog?.oneClickConfigs ?? member?.oneClickConfigs
            : catalog?.configGroups ?? member?.configGroups) || []).map(name => [name, '']);
    if (action?.taskType === type && action.taskName && !entries.some(([name, id]) => name === action.taskName && id === action.workflowId))
        entries.push([action.taskName, action.workflowId]);
    return '<option value="">请选择任务</option>' + entries.map(([name, id]) =>
        `<option value="${webEscapeHtml(completionTargetKey(type, name, id))}">${webEscapeHtml(name)}</option>`).join('');
}
function initializeCompletionPolicyEditor(uid, host, source = undefined) {
    const entry = readMemberEntry(uid, source);
    if (!entry) { host.innerHTML = '<p class="note">原成员来源已失效，请重新选择。</p>'; return; }
    source = entry.source;
    const member = () => currentMember(uid, source);
    const ordinaryPolicies = '<option value="resume">恢复原任务</option><option value="stop">锄完停止</option><option value="runSpecified">执行指定任务</option>';
    const ordinaryDraft = policy => ({ policy, taskType: 'group', taskName: '', priority: 0,
        startMode: policy === 'runSpecified' ? 'beginning' : 'default', startTaskId: '', startTaskName: '', configRevision: '', workflowId: '' });
    host.innerHTML = `<div class="panel-header"><h2>锄地后动作</h2><button class="text-button" id="reloadPolicy">重新读取</button></div><p class="note">选择完成后要执行的配置组、一条龙或计划表，可从头开始，也可从其中一个启用的任务开始。</p>
        <div id="policyFields"><label class="field">完成后<select id="completionPolicy" disabled>${ordinaryPolicies}</select></label>
        <div id="specifiedTaskField" hidden><label class="field">任务类型<select id="completionTaskType" disabled><option value="group">配置组</option><option value="onedragon">一条龙</option><option value="workflow">计划表</option></select></label>
        <label class="field">指定任务<select id="completionTask" disabled><option value="">请选择任务</option></select></label><button class="btn btn-secondary" id="refreshCompletionCatalog" disabled>刷新任务目录</button><div class="form-result" id="completionCatalogResult" role="status"></div>
        <label class="field">执行起点<select id="completionStartMode" disabled><option value="default" disabled>沿用原入口（旧配置）</option><option value="beginning">从头开始</option><option value="node">从指定任务开始</option></select></label>
        <label class="field" id="completionStartTaskField" hidden>开始执行的任务<select id="completionStartTask" disabled><option value="">请选择起点任务</option></select></label>
        <p class="note" id="completionStartHint" role="status"></p><button class="btn btn-secondary" id="confirmCompletionStart" hidden>确认当前起点</button></div>
        <label class="field" id="priorityField" hidden>任务优先级<input type="number" id="completionPriority" step="1" min="-2147483648" max="2147483647" disabled></label></div>
        <p class="note" id="completionPreview"></p><button class="btn btn-primary" id="savePolicy" disabled>保存结束动作</button><div class="form-result" id="policyResult" role="status">正在读取执行端设置…</div>`;
    const field = id => host.querySelector('#' + id);
    const policySelect = field('completionPolicy'), typeSelect = field('completionTaskType'), taskSelect = field('completionTask');
    const startSelect = field('completionStartMode'), nodeSelect = field('completionStartTask'), priority = field('completionPriority');
    const save = field('savePolicy'), reload = field('reloadPolicy'), status = field('policyResult');
    const refresh = field('refreshCompletionCatalog');
    let loadedPolicy = null, action = null, catalog = null, version = null;
    let loadedRevision = null, revisionSupported = false, switchDescription = '';
    let busy = false, catalogLoading = false, mustReload = false, generation = 0, pullQueue = Promise.resolve(), activeRefresh = null;
    const live = () => host.isConnected && document.getElementById('completionSettings') === host
        && !document.getElementById('detailView')?.hidden && activeMemberUid === uid && detailSection === 'settings'
        && memberSourceKey(uid, activeMemberSource) === memberSourceKey(uid, source);
    const currentRequest = token => live() && token === generation;
    // 同类远程命令逐个关联；新选择使旧请求失效，排队的旧请求不再发送。
    const pull = (query, token) => {
        const result = pullQueue.then(async () => {
            if (!currentRequest(token)) return null;
            const pending = sendMemberOperation(uid, 'task_policy.pull', query, 'task_policy.data', source);
            let observer = null;
            const cancel = () => pending.cancelCatalogRefresh?.();
            if (query.ensureBgiReady === 'true') {
                activeRefresh = pending;
                // 离开成员设置即撤销原刷新，不能在旧页面退出后迟到拉起执行端。
                if (typeof MutationObserver !== 'undefined') {
                    observer = new MutationObserver(() => { if (!currentRequest(token)) cancel(); });
                    observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['hidden'] });
                }
                window.addEventListener('beforeunload', cancel);
            }
            try { return await pending; }
            finally {
                observer?.disconnect(); window.removeEventListener?.('beforeunload', cancel);
                if (activeRefresh === pending) activeRefresh = null;
            }
        });
        pullQueue = result.catch(() => {});
        return result;
    };
    // 后续普通目标查询不携带实时名称；保留本编辑器从同一执行端显式刷新的候选。
    const readCatalog = (data, preserveNames = true) => {
        const next = normalizeCompletionCatalog(data);
        return next && { ...next, configGroups: next.configGroups ?? (preserveNames ? catalog?.configGroups : null),
            oneClickConfigs: next.oneClickConfigs ?? (preserveNames ? catalog?.oneClickConfigs : null) };
    };
    const matchingTarget = () => catalog?.target && action && completionTargetKey(catalog.target.type, catalog.target.name, catalog.target.workflowId)
        === completionTargetKey(action.taskType, action.taskName, action.workflowId) ? catalog.target : null;
    const drawNodes = () => {
        const nodes = matchingTarget()?.nodes || [];
        nodeSelect.innerHTML = '<option value="">请选择起点任务</option>' + nodes.map((node, i) =>
            `<option value="${webEscapeHtml(node.taskId)}" ${node.enabled ? '' : 'disabled'}>${i + 1}. ${webEscapeHtml(node.name)}${node.enabled ? '' : '（已禁用）'}</option>`).join('');
        if (action?.startTaskId && !nodes.some(node => node.taskId === action.startTaskId))
            nodeSelect.add(new Option('原起点已失效 · ' + action.startTaskName, action.startTaskId));
        nodeSelect.value = action?.startTaskId || '';
    };
    const updateFields = () => {
        const specified = action?.policy === 'runSpecified', editable = !!loadedPolicy && version === '1' && !busy && !mustReload && memberCanOperate(uid, source);
        field('specifiedTaskField').hidden = !specified; field('priorityField').hidden = !specified;
        field('completionStartTaskField').hidden = action?.startMode !== 'node';
        policySelect.disabled = !editable; typeSelect.disabled = !editable; taskSelect.disabled = !editable; priority.disabled = !editable;
        startSelect.disabled = !editable || catalogLoading; nodeSelect.disabled = !editable || catalogLoading;
        save.disabled = !editable || catalogLoading || action?.policy === 'switchRoom'; reload.disabled = busy || catalogLoading;
        save.textContent = loadedPolicy?.policy === 'switchRoom' && action?.policy !== 'switchRoom' ? '替换结束动作' : '保存结束动作';
        refresh.disabled = !editable || !specified || catalogLoading;
        refresh.textContent = catalogLoading ? '正在读取任务目录…' : '刷新任务目录';
        const reported = member()?.hoeingCompletionAction;
        const existingSwitch = loadedPolicy?.policy === 'switchRoom'
            ? `执行端当前已保存：${switchDescription}。切房关系须在执行端本机编辑；改为原三种动作并保存将替换此设置。`
            : !loadedPolicy && reported?.policy === 'switchRoom' ? `上次上报（尚未核对）：${hoeingCompletionDescription(reported)}` : '';
        field('completionPreview').textContent = [existingSwitch, action && action.policy !== 'switchRoom'
            ? '结束后：' + hoeingCompletionDescription(action) : ''].filter(Boolean).join(' ');
        let hint = '';
        if (specified && action.startMode === 'default') hint = '旧配置沿用原入口，不保证从头执行。保存前请选择从头开始或指定任务。';
        else if (specified && !catalogLoading) {
            try { validateCompletionSelection(action, catalog); }
            catch (err) { hint = err.message; }
        }
        field('completionStartHint').textContent = hint;
        const target = matchingTarget();
        const canConfirm = specified && action.startMode !== 'default' && target && target.revision !== action.configRevision
            && (action.startMode === 'beginning' || target.nodes.some(node => node.taskId === action.startTaskId && node.enabled));
        field('confirmCompletionStart').hidden = !canConfirm;
        field('confirmCompletionStart').disabled = !editable || catalogLoading;
    };
    const drawAction = () => {
        policySelect.innerHTML = (loadedPolicy?.policy === 'switchRoom'
            ? '<option value="switchRoom" disabled>已设置切换助手房间（仅本机编辑）</option>' : '') + ordinaryPolicies;
        policySelect.value = action.policy; typeSelect.value = action.taskType; priority.value = action.priority;
        taskSelect.innerHTML = completionTargetOptions(member(), action.taskType, catalog, action);
        taskSelect.value = action.taskName ? completionTargetKey(action.taskType, action.taskName, action.workflowId) : '';
        startSelect.value = action.startMode; drawNodes(); updateFields();
    };
    const applyRead = data => {
        const nextAction = normalizePolicy(data), nextCatalog = readCatalog(data), nextRevision = completionEditRevision(data);
        loadedPolicy = { ...nextAction }; action = { ...nextAction }; catalog = nextCatalog;
        loadedRevision = nextRevision; revisionSupported = hasCompletionEditFields(data);
        switchDescription = nextAction.policy === 'switchRoom'
            ? typeof data.roomSwitchDescription === 'string' && data.roomSwitchDescription.trim()
                ? data.roomSwitchDescription : hoeingCompletionDescription(nextAction)
            : '';
        version = data.completionStartVersion; mustReload = false;
        // 切房拉取的编辑字段不是状态上报的确认摘要；不覆盖其 actionRevision 或伪造可确认的新关系。
        if (nextAction.policy !== 'switchRoom') reflectConfirmedMemberFields(uid, source, { hoeingCompletionAction: { ...nextAction } });
        field('completionCatalogResult').textContent = nextCatalog?.error || (nextCatalog?.target ? '已读取该成员的任务目录' : '选择目标后读取任务目录');
        drawAction();
    };
    const loadPolicy = async () => {
        if (busy) return;
        const token = ++generation; busy = true; catalogLoading = false; loadedPolicy = null; action = null; catalog = null;
        loadedRevision = null; revisionSupported = false; switchDescription = '';
        updateFields(); status.textContent = '正在读取执行端设置…';
        try {
            const result = await pull({}, token);
            if (!currentRequest(token)) return;
            if (result?.status !== 'success') throw new Error(result?.message || '执行端未确认设置');
            applyRead(result.data);
            status.textContent = version === '1' ? '已读取执行端当前设置' : '已读取旧版设置，仅供查看；请更新该成员助手后重新读取再保存';
        } catch (err) {
            if (currentRequest(token)) status.textContent = '读取未完成：' + err.message + '；未使用默认值覆盖原配置';
        } finally { if (currentRequest(token)) { busy = false; updateFields(); } }
    };
    const loadTarget = async (explicitRefresh = false) => {
        activeRefresh?.cancelCatalogRefresh?.();
        const token = ++generation;
        if (!action.taskName && !explicitRefresh) {
            catalogLoading = false; catalog = catalog ? { ...catalog, target: null, error: null } : null;
            field('completionCatalogResult').textContent = '请选择该成员的任务'; drawNodes(); updateFields(); return;
        }
        const query = { catalogType: action.taskType, catalogName: action.taskName, catalogWorkflowId: action.workflowId };
        if (explicitRefresh && ['group', 'onedragon'].includes(action.taskType)) query.ensureBgiReady = 'true';
        catalogLoading = true;
        if (!explicitRefresh) catalog = catalog ? { ...catalog, target: null, error: null } : null;
        field('completionCatalogResult').textContent = query.ensureBgiReady
            ? '正在刷新该成员的任务目录，必要时启动 BGI 并等待通道就绪（最长约 180 秒）…'
            : '正在读取该成员的目标与任务起点…'; drawNodes(); updateFields();
        try {
            const result = await pull(query, token);
            if (!currentRequest(token)) return;
            if (result?.status !== 'success') throw new Error(result?.message || '读取任务目录未确认');
            if (result.data?.completionStartVersion !== '1') throw new Error('执行端未支持明确起点，请更新助手后重新读取');
            catalog = readCatalog(result.data, !explicitRefresh);
            if (explicitRefresh) {
                if (['group', 'onedragon'].includes(action.taskType)
                    && (catalog.configGroups == null || catalog.oneClickConfigs == null))
                    throw new Error((catalog.error ? catalog.error + '；' : '') + '执行端未返回实时任务名称目录，未确认刷新成功');
                taskSelect.innerHTML = completionTargetOptions(member(), action.taskType, catalog, action);
                taskSelect.value = action.taskName ? completionTargetKey(action.taskType, action.taskName, action.workflowId) : '';
                if (!action.taskName && ['group', 'onedragon'].includes(action.taskType)) {
                    if (catalog.target !== null) throw new Error('执行端返回了未选择的任务，未采用该目标');
                    field('completionCatalogResult').textContent = catalog.error || '请选择执行目标。';
                    return; // 已有实时名称也不自动选第一项，不产生节点或任务启动。
                }
            }
            if (catalog.error) throw new Error(catalog.error);
            // 计划表按稳定 ID 查询，采用目录的当前名称。
            if (action.taskType === 'workflow' && catalog.target?.type === 'workflow' && catalog.target.workflowId === action.workflowId)
                action.taskName = catalog.target.name;
            const target = matchingTarget();
            if (!target) throw new Error('返回的目录与所选任务不一致');
            // 刷新保留用户的目标、节点与修订确认；目录变化仍需明确确认起点。
            if (!explicitRefresh) action.configRevision = target.revision;
            taskSelect.innerHTML = completionTargetOptions(member(), action.taskType, catalog, action);
            taskSelect.value = completionTargetKey(action.taskType, action.taskName, action.workflowId);
            field('completionCatalogResult').textContent = catalog.error || '已读取该成员的任务目录';
        } catch (err) {
            if (currentRequest(token)) { catalog = catalog ? { ...catalog, target: null, error: err.message } : null; field('completionCatalogResult').textContent = err.message; }
        } finally { if (currentRequest(token)) { catalogLoading = false; drawNodes(); updateFields(); } }
    };
    const clearStart = () => { action.startMode = 'beginning'; action.startTaskId = ''; action.startTaskName = ''; action.configRevision = ''; startSelect.value = action.startMode; };
    policySelect.onchange = () => {
        if (busy || !action || !['resume', 'stop', 'runSpecified'].includes(policySelect.value)) return;
        if (action.policy === 'switchRoom') {
            // 明确替换切房时不继承切房后的任务或起点；待命/恢复由执行端落盘为普通干净策略。
            action = ordinaryDraft(policySelect.value);
            catalog = catalog ? { ...catalog, target: null, error: null } : null;
            field('completionCatalogResult').textContent = '请选择该成员的任务';
            drawAction();
            return;
        }
        action.policy = policySelect.value;
        if (action.policy !== 'runSpecified') {
            activeRefresh?.cancelCatalogRefresh?.(); ++generation; catalogLoading = false;
        }
        updateFields();
    };
    typeSelect.onchange = () => {
        if (busy || !action) return;
        action.taskType = typeSelect.value; action.taskName = ''; action.workflowId = ''; clearStart();
        taskSelect.innerHTML = completionTargetOptions(member(), action.taskType, catalog); taskSelect.value = '';
        field('completionCatalogResult').textContent = '请选择该成员的任务'; return loadTarget();
    };
    taskSelect.onchange = () => {
        if (busy || !action) return;
        const [type, name, id] = taskSelect.value ? JSON.parse(taskSelect.value) : [typeSelect.value, '', ''];
        action.taskType = type; action.taskName = name; action.workflowId = id; clearStart(); return loadTarget();
    };
    startSelect.onchange = () => {
        if (busy || !action || !['beginning', 'node'].includes(startSelect.value)) return;
        action.startMode = startSelect.value; action.startTaskId = ''; action.startTaskName = '';
        action.configRevision = matchingTarget()?.revision || ''; drawNodes(); updateFields();
    };
    nodeSelect.onchange = () => {
        if (busy || !action) return;
        const target = matchingTarget(), node = target?.nodes.find(item => item.taskId === nodeSelect.value && item.enabled);
        action.startTaskId = node?.taskId || ''; action.startTaskName = node?.name || ''; action.configRevision = target?.revision || ''; updateFields();
    };
    field('confirmCompletionStart').onclick = () => {
        if (busy || !action) return;
        const target = matchingTarget(), node = target?.nodes.find(item => item.taskId === action.startTaskId && item.enabled);
        if (!target || action.startMode === 'node' && !node) return;
        action.configRevision = target.revision;
        if (node) action.startTaskName = node.name;
        updateFields();
    };
    reload.onclick = loadPolicy;
    refresh.onclick = () => { if (!refresh.disabled) return loadTarget(true); };
    const finishSave = result => {
        status.textContent = formatResult(result);
        rememberMemberResult(uid, 'task_policy.push', result.status, result.message, source);
        log(`${memberTargetLabel(readMemberEntry(uid, source), uid, true)} · 保存结束动作最终结果：${formatResult(result)}`);
        return result;
    };
    save.onclick = async () => {
        if (save.disabled || !loadedPolicy) return;
        const token = ++generation; busy = true; let writeStarted = false;
        updateFields(); status.textContent = '正在核对执行端当前设置与任务起点…';
        try {
            requireOnlineTargets([uid], source);
            // 离开切房后保存恢复/停止时，桌面会清空隐藏的切房后任务字段，按该规范值确认回执和回读。
            const submitted = loadedPolicy.policy === 'switchRoom' && action.policy !== 'runSpecified' ? ordinaryDraft(action.policy)
                : { ...action, priority: action.policy === 'runSpecified' ? Number(priority.value) : loadedPolicy.priority };
            if (submitted.policy === 'runSpecified' && !priority.value.trim() || !Number.isInteger(submitted.priority)
                || submitted.priority < -2147483648 || submitted.priority > 2147483647) throw new Error('任务优先级必须为有效整数');
            const params = completionPolicyParams(submitted, version);
            if (revisionSupported) params.expectedCompletionRevision = loadedRevision;
            const before = await pull(submitted.policy === 'runSpecified'
                ? { catalogType: submitted.taskType, catalogName: submitted.taskName, catalogWorkflowId: submitted.workflowId } : {}, token);
            if (!currentRequest(token)) return;
            if (before?.status !== 'success') throw new Error(before?.message || '保存前读取设置未确认');
            if (before.data?.completionStartVersion !== '1') throw new Error('执行端未确认起点协议版本，请更新助手后重新读取');
            if (revisionSupported && !hasCompletionEditFields(before.data) || completionEditRevision(before.data) !== loadedRevision) {
                mustReload = true; throw new Error('成员结束动作已在其他端改变，请重新读取后编辑');
            }
            normalizePolicy(before.data);
            catalog = readCatalog(before.data); drawNodes();
            validateCompletionSelection(submitted, catalog);
            writeStarted = true;
            const result = await sendMemberOperation(uid, 'task_policy.push', params, 'task_policy.push_result', source);
            if (!currentRequest(token)) return;
            if (result.status !== 'success') {
                mustReload = result.status === 'unknown'; return finishSave(result);
            }
            // 回执自身也要包含本次字段；仅有 ok 或通用 ack 不能确认保存。
            if (result.data?.completionStartVersion !== '1'
                || completionActionSignature(normalizePolicy(result.data)) !== completionActionSignature(submitted))
                throw new Error('执行端保存回执缺少起点版本或与本次提交不一致');
            const after = await pull({}, token);
            if (!currentRequest(token)) return;
            if (after?.status !== 'success' || after.data?.completionStartVersion !== '1') throw new Error(after?.message || '保存后的设置回读未确认');
            const confirmed = normalizePolicy(after.data), confirmedCatalog = readCatalog(after.data);
            if (completionActionSignature(confirmed) !== completionActionSignature(submitted)) throw new Error('保存后的回读值与本次提交不一致');
            validateCompletionSelection(confirmed, confirmedCatalog);
            applyRead(after.data);
            const success = { status: 'success', message: '执行端已保存并回读确认：' + hoeingCompletionDescription(confirmed) };
            return finishSave(success);
        } catch (err) {
            if (!currentRequest(token)) return;
            if (writeStarted) mustReload = true;
            const result = { status: writeStarted ? 'unknown' : 'failed', message: err.message + (writeStarted ? '；请重新读取设置' : '') };
            return finishSave(result);
        } finally { if (currentRequest(token)) { busy = false; updateFields(); } }
    };
    return loadPolicy();
}

function showCoordinatedConfirm(label) {
    const ids = [...selectedMemberUids], mainScope = captureMainMemberScope();
    let sources;
    try {
        sources = freezeMemberTargets(ids);
        requireMainMemberTargets(mainScope, ids, sources);
    } catch (err) { createModal('无法准备联机', `<p>${webEscapeHtml(err.message)}</p>`); return; }
    const targets = snapshotForStart(ids.map(uid => currentMember(uid, sources.get(uid))));
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
    }).join('')}<label class="check-row" style="margin-top:18px"><input type="checkbox" id="completionActions" ${canUseActions ? '' : 'disabled'}>执行各成员已有的锄地后动作</label><p class="note" id="completionActionsHint">${canUseActions ? '不勾选：本次锄完停止。失败或手动停止不会触发后续动作。' : '结束动作尚未上报完整或服务端暂不支持；本次可按锄完停止执行。'}</p>${errors.length ? `<p class="error">${errors.map(webEscapeHtml).join('<br>')}</p>` : ''}<div class="batch-feedback" hidden role="status"></div>`, `<button class="btn btn-secondary" data-close>返回调整</button><button class="btn btn-primary" id="startBatch" ${errors.length ? 'disabled' : ''}>确认开锄</button>`);
    const option = overlay.querySelector('#completionActions');
    if (canUseActions) option.onchange = () => {
        overlay.querySelector('#completionActionsHint').textContent = option.checked
            ? completionChoiceExplanation(targets, !!quickKey) : '不勾选：本次锄完停止。失败或手动停止不会触发后续动作。';
    };
    overlay.querySelector('#startBatch').onclick = async event => {
        const button = event.currentTarget, cancel = overlay.querySelector('[data-close]');
        if (button.disabled) return;
        const useActions = canUseActions && !option.disabled && option.checked;
        button.disabled = true; cancel.disabled = true; option.disabled = true;
        const feedback = overlay.querySelector('.batch-feedback'); feedback.hidden = false; feedback.textContent = '正在提交准备请求…';
        try {
            requireMainMemberScope(mainScope);
            requireFrozenMemberTargets(ids, sources);
            const result = await beginCoordinatedQuickCommand(targets, useActions, quickKey, label, mainScope, sources);
            feedback.textContent = result?.message || '受理结果未确认，请查看操作记录；未自动重发';
            if (result?.batch) { overlay.dataset.batchId = result.batch.batchId; onControlBatchState(result.batch); }
        } catch (err) { feedback.textContent = '未开始：' + err.message; }
        finally { button.hidden = true; cancel.disabled = false; cancel.textContent = '关闭'; }
    };
}
function showLegacyQuickMenu() {
    const mainScope = captureMainMemberScope();
    try { requireMainMemberScope(mainScope); } catch (err) { log(err.message); return; }
    const overlay = createModal('网页任务绑定', '<p class="note">保留旧版保存在此浏览器的快捷任务。它将对已选成员执行同名配置；“联机快捷指令”使用的是各成员自己的绑定。</p><div class="action-grid">' + ['传奇', '次数盾', '精英', '小怪', '自定义'].map(key => `<button class="btn btn-secondary" data-key="${key}">${key}</button>`).join('') + '</div>');
    overlay.querySelectorAll('[data-key]').forEach(b => b.onclick = () => { overlay.remove(); showQuickCommandConfirm(b.dataset.key, mainScope); });
}
renderControlRoom();
