'use strict';
const $ = id => document.getElementById(id);
const labels = { started: '已开锄', waiting: '等待开锄', disconnected: '无人在线', finished: '本轮结束' };
const memberStates = { Waiting: '等待', Ready: '就绪', Pathing: '路径中', WaitingAtSync: '同步等待', Fighting: '战斗中' };
let snapshot = null, dailySnapshot = null, view = 'live';
const selected = { live: null, history: null }, queries = { live: '', history: '' }, openBatches = new Map();
let historyRequest = 0, liveRequest = 0, historyLoading = false, archiveLoaded = false, archiveRooms = [], unlinkedPage = 0;
let liveDetailOpen = false;
let liveLoading = false, historyDateLoading = null, historyController = null;
function node(tag, className, value) {
  const item = document.createElement(tag);
  if (className) item.className = className;
  if (value !== undefined) item.textContent = String(value);
  return item;
}
function action(text, handler, className = 'button') {
  const button = node('button', className, text); button.type = 'button';
  button.addEventListener('click', handler); return button;
}
function cell(value, sub) {
  const box = node('div', 'value', value);
  if (sub !== undefined) box.append(node('span', 'sub', sub)); return box;
}
function pill(text, state = '') { return node('span', 'pill ' + state, text); }
function matches(value, query) { return String(value || '').toLowerCase().includes(query); }
function heartbeat(time) {
  if (!time) return '未上报';
  const age = Math.max(0, Math.floor((Date.now() - Date.parse(time)) / 1000));
  return age < 60 ? `${age}秒前` : `${Math.floor(age / 60)}分钟前`;
}
function duration(start) {
  if (!start) return '未记录';
  const seconds = Math.max(0, Math.floor((Date.now() - Date.parse(start)) / 1000));
  const hours = Math.floor(seconds / 3600), minutes = Math.floor(seconds % 3600 / 60);
  return hours ? `${hours}小时${minutes}分` : `${minutes}分`;
}
function route(room) {
  const numbers = room.members.filter(m => m.online && m.routeNumber !== null).map(m => m.routeNumber);
  if (!numbers.length || room.totalRoutes === null) return '未上报';
  const min = Math.min(...numbers), max = Math.max(...numbers);
  return `第 ${min}${min === max ? '' : '–' + max} / ${room.totalRoutes} 条`;
}
function memberSummary(members) {
  const list = node('div', 'member-summary');
  for (const m of members || []) {
    const state = m.reconnecting ? 'reconnecting' : m.online ? '' : 'offline';
    list.append(node('span', 'member-chip ' + state, (m.name || m.uid || '未命名') +
      (m.reconnecting ? '（重连中）' : m.online ? '' : '（离线）')));
  }
  return list;
}
function logButton(text, code, date, selection = { direct: true }, label = '仅此房间号自身保存的日志') {
  return action(text, () => openLogs(code, date, selection, label), 'button compact');
}
function worldRow(world, date) {
  const row = node('div', 'world-row'), info = node('div', 'world-info');
  info.append(node('strong', 'mono', `世界房间 ${world.code}`),
    node('div', 'history-note', `房主：${world.hostName || world.hostUid || '旧记录未保存'}${world.hostName && world.hostUid ? ' · UID ' + world.hostUid : ''}`),
    node('div', 'history-note', `建房 ${AdminHoeingHistory.time(world.createdAtUtc, date)} · ${world.endedAtUtc ? '结束 ' + AdminHoeingHistory.time(world.endedAtUtc, date) : '结束时间未记录'}`));
  if (!world.hasSavedLogs) info.append(node('div', 'history-warning', '当天未发现此房间日志文件'));
  row.append(info, logButton('下载日志', world.code, date, { session: world.sessionId }, '仅此世界房间会话'));
  return row;
}
function summaryLine(items) {
  const line = node('div', 'summary-line'); for (const text of items) line.append(node('span', '', text)); return line;
}
function heading(text, button) {
  const line = node('div', 'section-head'); line.append(node('h3', '', text)); if (button) line.append(button); return line;
}
function memberTable(members, game, totalRoutes) {
  const wrap = node('div', 'table-wrap'), table = node('table', 'member-table'), head = node('tr'), thead = node('thead'), body = node('tbody');
  for (const text of ['成员', '连接', game ? '游戏状态' : '助手状态', '当前线路', '最近心跳']) head.append(node('th', '', text));
  thead.append(head);
  for (const m of members) {
    const row = node('tr'), name = node('td'); name.append(cell(m.name || '未命名', 'UID ' + (m.uid || '未上报'))); row.append(name);
    for (const text of [m.reconnecting ? '重连中' : m.online ? '在线' : '离线',
      m.online ? (game ? (memberStates[m.status] || m.status) : (m.autoHoeingRunning ? '锄地中' : '已连接')) : '—',
      m.online ? (game ? (m.routeNumber !== null && totalRoutes !== null ? `第 ${m.routeNumber} / ${totalRoutes} 条` : '未上报') :
        (m.autoHoeingRunning ? (m.routeDisplay || m.progress || '未上报') : '—')) : '—', heartbeat(m.lastHeartbeatUtc)]) row.append(node('td', '', text));
    body.append(row);
  }
  table.append(thead, body); wrap.append(table); return wrap;
}
function batchDetails(code, day, worlds = [], scope = 'history') {
  const section = node('div');
  for (const warning of day?.warnings || []) section.append(node('div', 'history-warning', warning));
  const query = scope === 'history' ? $('search').value.trim().toLowerCase() : '';
  const fullMatch = !query || matches(code, query) || (day?.runs || []).some(r => r.members.some(m => matches(m.name, query) || matches(m.uid, query)));
  const batches = new Map((day?.runs || []).filter(r => fullMatch || matches(r.id, query)).map(run => [run.id, { run, worlds: [] }]));
  for (const world of worlds) {
    if (!fullMatch && ![world.code, world.batchId, world.hostUid, world.hostName].some(value => matches(value, query))) continue;
    const id = world.batchId || '';
    if (!batches.has(id)) batches.set(id, { run: (day?.runs || []).find(r => r.id === id) || null, worlds: [] });
    batches.get(id).worlds.push(world);
  }
  if (!batches.size) {
    section.append(node('p', 'history-note', day ? '此日期暂无已保存的锄地批次；启用记录前的行为无法补回。' : '锄地记录暂不可用。'));
    return section;
  }
  let ordinal = 0;
  for (const [id, batch] of [...batches].reverse()) {
    const key = scope + ':' + code + ':' + (day?.date || '') + ':' + id;
    const details = node('details', 'batch'), summary = node('summary'), title = node('div'), first = node('div', 'batch-title');
    details.dataset.key = key;
    details.open = openBatches.get(key) ?? (!fullMatch && !!query);
    first.append(node('strong', '', id ? `锄地批次 ${++ordinal}` : '批次归属未记录'));
    if (batch.run) first.append(pill(AdminHoeingHistory.runLabel(batch.run), batch.run.status)); title.append(first);
    title.append(node('div', 'history-note', batch.run ?
      `${batch.run.startedAtUtc ? '开锄' : '受理'} ${AdminHoeingHistory.time(batch.run.startedAtUtc || batch.run.requestedAtUtc, day.date)}${batch.run.endedAtUtc ? ' · 结束 ' + AdminHoeingHistory.time(batch.run.endedAtUtc, day.date) : ''}` :
      `${batch.worlds.length} 个世界房间`));
    if (id && scope === 'history') title.append(node('div', 'history-note', `${batch.worlds.length} 个已关联世界房间`)); summary.append(title);
    const body = node('div', 'batch-body');
    if (id) body.append(node('div', 'batch-code', '批次 ID：' + id));
    if (batch.run) {
      body.append(node('div', 'history-members', '参与成员：' + (batch.run.members.map(m => m.name || m.uid).join('、') || '未记录')));
      const reason = AdminHoeingHistory.reasonText(batch.run.reason); if (reason) body.append(node('div', 'history-note', reason));
    }
    if (id && batch.worlds.length) body.append(logButton('下载本批次世界日志', code, day.date, { batch: id }, '仅此完整批次的世界日志（所选日期范围）'));
    if (scope === 'live') body.append(action('查看关联世界与历史日志', () => showHistoryFor(code), 'text-button'));
    else if (!batch.worlds.length) body.append(node('div', 'history-warning', '未保存可确认属于此批次的世界房间关联；可在其他世界记录中按房间号下载。'));
    const list = node('div', 'world-list'); for (const world of batch.worlds) list.append(worldRow(world, day.date));
    if (list.childElementCount) body.append(list);
    details.append(summary, body); details.addEventListener('toggle', () => { if (details.isConnected) openBatches.set(key, details.open); }); section.append(details);
  }
  return section;
}
function liveEntries() {
  return [...(snapshot?.controlRooms || []).map(room => ({ key: 'control:' + room.code, type: 'control', code: room.code, room })),
    ...(snapshot?.rooms || []).map(room => ({ key: 'game:' + room.code, type: 'game', code: room.code, room }))];
}
function entryMatches(entry, query) {
  const r = entry.room;
  if (entry.type === 'unlinked') return !query || dailySnapshot.unlinkedRooms.some(code => matches(code, query));
  if (entry.type === 'history') return !query || matches(r.code, query) || r.worlds.some(w => [w.code, w.batchId, w.hostName, w.hostUid].some(v => matches(v, query))) ||
    r.hoeing.runs.some(run => matches(run.id, query) || run.members.some(m => matches(m.name, query) || matches(m.uid, query)));
  return !query || matches(r.code, query) || matches(r.hostName, query) || r.members.some(m => matches(m.name, query) || matches(m.uid, query));
}
function liveFilter(entry) {
  const type = $('roomType').value, filter = $('filter').value, r = entry.room;
  if (type !== 'all' && entry.type !== type) return false;
  return filter === 'all' || filter === 'connected' && r.onlineCount > 0 || filter === 'disconnected' && r.onlineCount === 0 ||
    filter === 'started' && (entry.type === 'control' ? r.autoHoeingRunning : r.status === 'started') ||
    filter === 'waiting' && entry.type === 'game' && r.status === 'waiting' || filter === 'finished' && entry.type === 'game' && r.status === 'finished' ||
    filter === 'today' && (entry.type === 'control' ? r.todayHoeing?.runs?.length > 0 : !!r.startedAtUtc &&
      new Date(Date.parse(r.startedAtUtc) + 8 * 3600000).toISOString().slice(0, 10) === beijingMinute().slice(0, 10));
}
function entries() {
  const query = $('search').value.trim().toLowerCase();
  if (view === 'live') return liveEntries().filter(entry => liveFilter(entry) && entryMatches(entry, query));
  if (!dailySnapshot || dailySnapshot.date !== $('historyDate').value) return [];
  return [...dailySnapshot.rooms.map(room => ({ key: 'history:' + room.code, type: 'history', code: room.code, room })),
    ...(dailySnapshot.unlinkedRooms.length ? [{ key: 'unlinked', type: 'unlinked', code: '其他已保存房间' }] : [])].filter(entry => entryMatches(entry, query));
}
function option(entry) {
  const button = node('button', 'room-option'); button.type = 'button'; button.dataset.key = entry.key;
  button.setAttribute('aria-pressed', String(selected[view] === entry.key));
  const top = node('div', 'room-option-top'); top.append(node('strong', entry.type === 'unlinked' ? '' : 'mono', entry.code)); const r = entry.room;
  if (entry.type === 'control') top.append(pill(r.autoHoeingRunning ? '锄地中' : r.onlineCount ? '已连接' : '无人在线', r.onlineCount ? 'connected' : 'disconnected'));
  else if (entry.type === 'game') top.append(pill(labels[r.status] || '未知', r.status));
  else top.append(pill(entry.type === 'unlinked' ? dailySnapshot.unlinkedRooms.length + ' 个' : r.worlds.length + ' 个世界'));
  button.append(top, node('span', 'sub', entry.type === 'unlinked' ? '归属未确认，按房间号查询' : entry.type === 'history' ?
    r.hoeing.runs.length + ' 次批次记录' : `${entry.type === 'control' ? '助手' : '世界'}房间 · 在线 ${r.onlineCount} / ${r.memberCount}`));
  if (r) {
    const names = entry.type === 'history' ? r.hoeing.runs.flatMap(run => run.members) : r.members;
    button.append(node('span', 'sub', [...new Set(names.map(m => m.name || m.uid))].join('、') || '未记录成员'));
  }
  button.addEventListener('click', () => { selected[view] = entry.key; unlinkedPage = 0; render(); if (matchMedia('(max-width:760px)').matches) $('detail').focus({ preventScroll: true }); });
  return button;
}
function directQuery() {
  const box = node('div', 'direct-query'); box.append(node('h3', '', '按房间号直接查日志'));
  const row = node('div', 'query-row'), input = node('input'), choose = node('select');
  input.id = 'archiveCode'; input.type = 'text'; input.maxLength = 6; input.placeholder = '输入六位房间号'; input.setAttribute('aria-label', '历史房间码');
  choose.id = 'archiveRoom'; choose.setAttribute('aria-label', '已保存日志的历史房间'); choose.add(new Option('选择已保存房间', ''));
  for (const room of archiveRooms) choose.add(new Option(room.code, room.code));
  const status = node('span', 'archive-note', '使用上方所选日期，可在下载窗口调整时间范围。'); status.id = 'archiveStatus';
  row.append(input, choose, action('查询日志', () => {
    if (!$('historyDate').value) { status.textContent = '请先选择有效的历史日期。'; return; }
    const code = (input.value.trim() || choose.value).toUpperCase();
    if (!/^[A-Z0-9]{6}$/.test(code)) { status.textContent = '请输入或选择六位房间号。'; return; }
    openLogs(code, $('historyDate').value);
  }));
  choose.addEventListener('change', () => { input.value = ''; }); box.append(row, status); return box;
}
async function loadArchive() {
  try {
    const response = await fetch('/api/admin/logs/rooms', { credentials: 'same-origin', cache: 'no-store', signal: AbortSignal.timeout(15000) });
    if (response.status === 401) { location.replace('/admin/login'); return; }
    if (!response.ok) throw new Error(); const data = await response.json(); archiveRooms = data.rooms || []; archiveLoaded = true;
    const choose = $('archiveRoom');
    if (choose) {
      const current = choose.value; choose.replaceChildren(new Option('选择已保存房间', ''));
      for (const room of archiveRooms) choose.add(new Option(room.code, room.code)); choose.value = current;
    }
  } catch { const status = $('archiveStatus'); if (status) status.textContent = '历史目录读取失败，仍可输入房间号查询。'; }
}
// 卡片空间有限：只显示次数和最近一次时段，完整说明留在详情页。
function cardHoeing(day){
  const runs=day?.runs||[],started=runs.filter(run=>run.startedAtUtc);
  if(!day)return '今日记录不可用';if(!runs.length)return '今日暂无锄地记录';
  const done=started.filter(run=>run.status==='completed').length,last=runs.reduce((a,b)=>Date.parse(b.requestedAtUtc)>=Date.parse(a.requestedAtUtc)?b:a);
  const clock=value=>AdminHoeingHistory.time(value,day.date).replace(/:\d\d$/,'');
  const span=clock(last.startedAtUtc||last.requestedAtUtc)+(last.endedAtUtc?'–'+clock(last.endedAtUtc):' 起');
  return `今日 ${started.length} 次 · 完成 ${done} · 最近 ${span}`;
}
function renderOverview() {  const box=$('overviewRooms'),items=entries(),focusKey=document.activeElement?.closest('.overview-card')?.dataset.key;
  items.sort((a,b)=>Number(b.type==='control'?b.room.autoHoeingRunning:b.room.status==='started')-Number(a.type==='control'?a.room.autoHoeingRunning:a.room.status==='started'));
  box.replaceChildren();$('overviewCount').textContent=`${items.length} 个`;
  if(!items.length){box.append(node('div','empty',snapshot?'没有符合条件的房间':'正在读取房间…'));return;}
  for(const entry of items){
    const r=entry.room,game=entry.type==='game',running=game?r.status==='started':r.autoHoeingRunning;
    const card=node('button','overview-card'+(running?' running':''));card.type='button';card.dataset.key=entry.key;
    const top=node('div','overview-card-top');top.append(node('h3','mono',r.code),pill(game?(labels[r.status]||'未知'):running?'锄地中':r.onlineCount?'在线 · 未锄地':'无人在线',game?r.status:running?'running':r.onlineCount?'':'disconnected'));card.append(top);
    card.append(node('div','history-note',`${game?'世界':'助手'}房间 · 在线 ${r.onlineCount} / ${r.memberCount}`),memberSummary(r.members));
    card.append(node('div','history-note',game?(r.worldRound?`世界轮次 ${r.worldRound} · `:'')+`线路 ${route(r)}`:cardHoeing(r.todayHoeing)));
    const foot=node('div','overview-card-foot');foot.append(node('span','',`心跳 ${heartbeat(r.lastHeartbeatUtc)}`),node('span','','查看详情'));card.append(foot);
    card.addEventListener('click',()=>{selected.live=entry.key;liveDetailOpen=true;render();});box.append(card);
  }
  if(focusKey)box.querySelector(`[data-key="${CSS.escape(focusKey)}"]`)?.focus({preventScroll:true});
}
function render() {
  const overview=view==='live'&&!liveDetailOpen;$('liveOverview').hidden=!overview;$('workbench').hidden=overview;$('backToOverview').hidden=view!=='live'||overview;$('viewHeading').hidden=overview;
  if(view==='live'){$('viewTitle').textContent=overview?'实时房间':'房间详情';$('viewHint').textContent=overview?'查看全部房间和当前锄地状态':'选择左侧房间，在右侧查看状态与成员';}
  if(overview){renderOverview();return;}
  const list = $('rooms'), detail = $('detail'), listScroll = list.scrollTop, detailScroll = detail.scrollTop, items = entries();
  // 刷新时房间消失不偷偷换成另一个房间；筛选或主动选择时才重置。
  if (selected[view] === null) selected[view] = items[0]?.key || null;
  const focusKey = document.activeElement?.closest('.room-option')?.dataset.key;
  list.replaceChildren(...items.map(option));
  if (!items.length) list.append(node('div', 'empty', view === 'history' && historyLoading ? '正在读取历史记录…' :
    $('search').value.trim() ? '没有匹配的房间' : '此范围暂无房间记录'));
  $('directoryCount').textContent = view === 'history' ? `${$('historyDate').value} · ${items.length} 个目录项` : `${items.length} 个匹配房间`;
  // 查询表单节点跨刷新保留，避免十秒更新打断正在输入的房间号。
  const queryBox = detail.querySelector('.direct-query'), focusedId = document.activeElement?.id;
  detail.replaceChildren();
  if (view === 'history') {
    detail.append(queryBox || directQuery());
    for (const warning of dailySnapshot?.warnings || []) detail.append(node('div', 'history-warning', warning));
  }
  const entry = items.find(item => item.key === selected[view]);
  if (!entry) {
    const empty = node('div', 'empty');
    empty.append(node('strong', '', selected[view] ? '所选房间不在当前列表中' : view === 'history' ? '选择日期或输入房间号' : '选择一个房间'),
      node('p', '', selected[view] ? '房间可能已结束或不符合筛选条件，请从左侧重新选择；历史日志仍可查询。' :
        view === 'history' ? '历史批次和世界记录将在这里显示。' : '有连接或今日记录的房间会显示在左侧目录。'));
    detail.append(empty);
  } else if (entry.type === 'unlinked') renderUnlinked(detail);
  else if (entry.type === 'history') renderHistory(detail, entry.room);
  else renderLive(detail, entry);
  list.scrollTop = listScroll; detail.scrollTop = detailScroll;
  revealSelection();
  if (focusKey) list.querySelector(`[data-key="${CSS.escape(focusKey)}"]`)?.focus({ preventScroll: true });
  else if (focusedId === 'archiveCode' || focusedId === 'archiveRoom') $(focusedId)?.focus({ preventScroll: true });
}
function revealSelection() {
  const list = $('rooms'), active = list.querySelector('.room-option[aria-pressed="true"]');
  if (!active) return;
  const outer = list.getBoundingClientRect(), inner = active.getBoundingClientRect();
  if (inner.top < outer.top || inner.bottom > outer.bottom)
    list.scrollTop += inner.top - outer.top - Math.max(0, (list.clientHeight - inner.height) / 2);
}
function renderLive(panel, entry) {
  const room = entry.room, game = entry.type === 'game', top = node('div', 'detail-top'), title = node('div'), buttons = node('div', 'detail-actions');
  title.append(node('div', 'scope-label', game ? '当前游戏世界房间' : '当前助手房间'), node('h2', 'mono', room.code));
  buttons.append(logButton(game ? '下载世界房间日志' : '下载助手自身日志', room.code, beijingMinute().slice(0, 10)));
  if (!game) buttons.append(action('历史与日志', () => showHistoryFor(room.code), 'button compact'));
  top.append(title, buttons); panel.append(top, pill(game ? (labels[room.status] || '未知') : room.autoHoeingRunning ? '有成员锄地中' :
    room.onlineCount ? '已连接' : '无人在线', game ? room.status : room.onlineCount ? 'connected' : 'disconnected'));
  panel.append(summaryLine(game ? [`在线 ${room.onlineCount} / ${room.memberCount}`, room.worldRound ? `世界轮次 ${room.worldRound}` : '世界轮次未记录',
    `线路 ${route(room)}`, room.startedAtUtc ? '开锄 ' + duration(room.startedAtUtc) : '尚未开锄'] :
    [`在线 ${room.onlineCount} / ${room.memberCount}`, `最近状态 ${heartbeat(room.lastHeartbeatUtc)}`, AdminHoeingHistory.headline(room.todayHoeing)]),
    memberSummary(room.members), heading('房间成员'));
  if (room.members.length) panel.append(memberTable(room.members, game, room.totalRoutes));
  else panel.append(node('p', 'history-note', '暂无已保存的成员信息。'));
  if (game) {
    panel.append(heading('世界房间信息'), summaryLine([`房主 ${room.hostName || '未记录'}`,
      `建房 ${AdminHoeingHistory.time(room.createdAtUtc, beijingMinute().slice(0, 10))}`, `最近心跳 ${heartbeat(room.lastHeartbeatUtc)}`])); return;
  }
  panel.append(heading('今日锄地记录', action('查看全部历史', () => showHistoryFor(room.code), 'text-button')),
    node('p', 'history-note', AdminHoeingHistory.overview(room.todayHoeing)), batchDetails(room.code, room.todayHoeing, [], 'live'));
}
function renderHistory(panel, room) {
  const top = node('div', 'detail-top'), title = node('div');
  title.append(node('div', 'scope-label', dailySnapshot.date + ' · 助手房间历史'), node('h2', 'mono', room.code));
  top.append(title, logButton('下载助手自身日志', room.code, dailySnapshot.date));
  panel.append(top, summaryLine([`${room.hoeing.runs.length} 次批次记录`, `${room.worlds.length} 个已关联世界房间`]),
    node('p', 'history-note', '这里只表示所选日期的已保存记录，不代表房间当前在线。'));
  for (const warning of room.warnings || []) panel.append(node('div', 'history-warning', warning));
  const members = [...new Map(room.hoeing.runs.flatMap(r => r.members).map(m => [m.uid, m])).values()];
  if (members.length) {
    const line = node('div', 'member-summary'); for (const m of members) line.append(node('span', 'member-chip offline', m.name || m.uid)); panel.append(line);
  }
  panel.append(heading('锄地批次与世界房间'), batchDetails(room.code, room.hoeing, room.worlds));
}
function renderUnlinked(panel) {
  const query = $('search').value.trim().toLowerCase(), codes = dailySnapshot.unlinkedRooms.filter(code => !query || matches(code, query)), size = 20;
  unlinkedPage = Math.min(unlinkedPage, Math.max(0, Math.ceil(codes.length / size) - 1));
  panel.append(heading('其他已保存房间日志'), node('p', 'history-note', '这些房间没有可确认的助手或批次归属，仍可按房间号下载；不会猜测归并。'));
  for (const code of codes.slice(unlinkedPage * size, (unlinkedPage + 1) * size)) {
    const row = node('div', 'world-row'); row.append(cell(code, '归属未确认'), logButton('下载日志', code, dailySnapshot.date)); panel.append(row);
  }
  if (codes.length > size) {
    const pager = node('div', 'pager'), prev = action('上一页', () => { unlinkedPage--; render(); }), next = action('下一页', () => { unlinkedPage++; render(); });
    prev.disabled = unlinkedPage === 0; next.disabled = (unlinkedPage + 1) * size >= codes.length;
    pager.append(prev, node('span', '', `${unlinkedPage + 1} / ${Math.ceil(codes.length / size)}`), next); panel.append(pager);
  }
}
function setView(next) {
  if (next === 'live') liveDetailOpen = false;
  if (view !== next) { queries[view] = $('search').value; view = next; $('search').value = queries[view]; }
  $('liveTab').setAttribute('aria-pressed', String(view === 'live')); $('historyTab').setAttribute('aria-pressed', String(view === 'history'));
  $('historyControls').hidden = view !== 'history'; $('liveFilters').hidden = view !== 'live';
  $('viewTitle').textContent = view === 'history' ? '历史与日志' : '实时房间';
  $('viewHint').textContent = view === 'history' ? '先选日期和助手房间，再查看批次与世界日志' : '选择左侧房间，在右侧查看状态与成员';
  $('directoryTitle').textContent = view === 'history' ? '已保存房间' : '当前房间';
  $('search').placeholder = view === 'history' ? '房间号、成员或批次 ID' : '搜索房间号、昵称或 UID'; render();
  if (view === 'history') { if (!archiveLoaded) loadArchive(); if (!dailySnapshot || dailySnapshot.date !== $('historyDate').value) loadDaily(); }
}
function showHistoryFor(code) {
  $('historyDate').value = beijingMinute().slice(0, 10); selected.history = 'history:' + code; queries.history = '';
  setView('history'); $('search').value = ''; render();
}
async function loadDaily() {
  const date = $('historyDate').value;
  if (!date) {
    historyRequest++; historyController?.abort(); historyLoading = false; historyDateLoading = null;
    dailySnapshot = null; selected.history = null;
    if (view === 'history') { $('viewHint').textContent = '请选择有效的历史日期'; render(); }
    return;
  }
  // 同一日期的慢响应不被下一次轮询作废；换日期则取消旧请求。
  if (historyLoading && historyDateLoading === date) return;
  const request = ++historyRequest; historyController?.abort();
  const controller = new AbortController(); historyController = controller; historyDateLoading = date;
  const timeout = setTimeout(() => controller.abort(), 15000);
  historyLoading = true; if (dailySnapshot?.date !== date) dailySnapshot = null;
  if (view === 'history') render();
  try {
    const response = await fetch('/api/admin/logs/cards?' + new URLSearchParams({ date }), { credentials: 'same-origin', cache: 'no-store', signal: controller.signal });
    if (response.status === 401) { location.replace('/admin/login'); return; }
    if (!response.ok) throw new Error(); const data = await response.json();
    if (request !== historyRequest || date !== $('historyDate').value) return;
    dailySnapshot = data; $('historyDates').replaceChildren(new Option('选择已有记录日期', ''));
    for (const day of data.dates) $('historyDates').add(new Option(day, day)); $('historyDates').value = date;
    if (view === 'history') { $('viewHint').textContent = '按日期选择助手房间，展开批次查看世界日志'; render(); }
  } catch { if (request === historyRequest && view === 'history') $('viewHint').textContent = '历史读取失败或超时，当前内容可能已过期；仍可输入房间号查询'; }
  finally { clearTimeout(timeout); if (request === historyRequest) { historyLoading = false; historyDateLoading = null; if (view === 'history') render(); } }
}
async function load() {
  if (liveLoading) return;
  liveLoading = true;
  const request = ++liveRequest;
  try {
    const response = await fetch('/api/admin/rooms', { credentials: 'same-origin', cache: 'no-store', signal: AbortSignal.timeout(15000) });
    if (response.status === 401) { location.replace('/admin/login'); return; }
    if (!response.ok) throw new Error(); const data = await response.json(); if (request !== liveRequest) return;
    snapshot = data; $('controlRooms').textContent = data.connectedControlRoomCount; $('controlMembers').textContent = data.connectedControlMemberCount;
    $('totalRooms').textContent = data.totalRoomCount; $('startedRooms').textContent = data.startedRoomCount;
    if (view === 'live') render();
    $('updated').textContent = '更新于 ' + new Date(data.capturedAtUtc).toLocaleTimeString('zh-CN', { timeZone: 'Asia/Shanghai', hour12: false }); $('notice').hidden = true;
  } catch {
    if (request === liveRequest) { $('notice').hidden = false; $('notice').textContent = '无法取得最新状态，当前内容可能已过期。请刷新重试。'; $('updated').textContent = '连接失败'; }
  } finally { liveLoading = false; }
}
