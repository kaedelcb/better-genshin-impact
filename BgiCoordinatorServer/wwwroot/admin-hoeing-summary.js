'use strict';
let summaryData = null, summaryRequest = 0, summaryController = null, summaryPending = '', summaryRoom = null;
const summaryDayMs = 86400000;
const summaryStatus = { running: '锄地中', preparing: '准备开锄', completed: '正常完成', completedWithErrors: '含任务异常', stopped: '主动停止', failed: '异常结束', unknown: '结果未确认', none: '无记录' };
const summaryDay = time => new Date(time + 8 * 3600000).toISOString().slice(0, 10);
const summaryMidnight = date => Date.parse(date + 'T00:00:00+08:00');
function summaryDuration(seconds) {
  if (!Number.isFinite(seconds) || seconds < 0) return '—';
  if (seconds === 0) return '0 分';
  if (seconds < 60) return '< 1 分';
  const minutes = Math.floor(seconds / 60), hours = Math.floor(minutes / 60);
  return hours ? `${hours} 小时 ${String(minutes % 60).padStart(2, '0')} 分` : `${minutes} 分`;
}
const summaryRate = rate => rate == null ? '—' : `${(rate * 100).toFixed(1)}%`;
const summaryName = room => room.note || room.code;
const summaryClock = (value, date) => AdminHoeingHistory.time(value, date);
function summaryDatePreset(days) {
  const today = summaryDay(Date.now()), last = days === 0 ? summaryDay(Date.now() - summaryDayMs) : today;
  $('summaryStart').value = days > 1 ? summaryDay(Date.now() - (days - 1) * summaryDayMs) : last;
  $('summaryEnd').value = last;
  for (const button of $('summaryRange').querySelectorAll('[data-days]')) button.setAttribute('aria-pressed', String(Number(button.dataset.days) === days));
}
function initSummary() {
  summaryDatePreset(1);
  for (const button of $('summaryRange').querySelectorAll('[data-days]')) button.addEventListener('click', () => { summaryDatePreset(Number(button.dataset.days)); loadSummary(); });
  for (const id of ['summaryStart', 'summaryEnd']) $(id).addEventListener('input', () => {
    for (const button of $('summaryRange').querySelectorAll('[data-days]')) button.setAttribute('aria-pressed', 'false');
  });
  $('summaryRange').addEventListener('submit', event => { event.preventDefault(); loadSummary(); });
  $('summarySort').addEventListener('change', renderSummaryRank);
  $('summarySearch').addEventListener('input', () => { renderSummaryRank(); renderSummaryTimeline(); });
  $('summaryTimelineDate').addEventListener('change', renderSummaryTimeline);
  // No historical range polling, background work or refresh while interacting with a detail/log dialog.
  setInterval(() => {
    if (view !== 'summary' || document.hidden || $('logDialog').open || summaryPending || $('hoeingSummary').contains(document.activeElement)) return;
    const preset = $('summaryRange').querySelector('[data-days][aria-pressed=true]');
    if (preset && Number(preset.dataset.days) !== 0 && $('summaryEnd').value !== summaryDay(Date.now())) {
      summaryDatePreset(Number(preset.dataset.days)); loadSummary(); return;
    }
    if (summaryData?.end === summaryDay(Date.now()) && summaryData.start === $('summaryStart').value && summaryData.end === $('summaryEnd').value) loadSummary(true);
  }, 30000);
}
async function loadSummary(quiet = false) {
  const today = summaryDay(Date.now()), start = $('summaryStart').value, end = $('summaryEnd').value;
  $('summaryStart').max = $('summaryEnd').max = today;
  const valid = start && end && start <= end && end <= today && summaryMidnight(end) - summaryMidnight(start) < 366 * summaryDayMs;
  const message = $('summaryMessage');
  if (!valid) {
    summaryRequest++; summaryController?.abort(); summaryPending = ''; summaryData = null; $('summaryContent').hidden = true;
    message.textContent = '请选择有效的日期范围（最多366天，结束日期不能晚于今天）。'; return;
  }
  const key = `${start}/${end}`;
  if (summaryPending === key) return;
  const request = ++summaryRequest;
  summaryController?.abort(); summaryController = new AbortController(); summaryPending = key;
  const controller = summaryController, timeout = setTimeout(() => controller.abort(), 15000);
  if (summaryData?.start !== start || summaryData?.end !== end) { summaryData = null; summaryRoom = null; $('summaryContent').hidden = true; }
  if (!quiet) message.textContent = '正在读取锄地总结…';
  try {
    const response = await fetch('/api/admin/hoeing/summary?' + new URLSearchParams({ start, end }), { credentials: 'same-origin', cache: 'no-store', signal: controller.signal });
    if (response.status === 401) { location.replace('/admin/login'); return; }
    if (!response.ok) throw new Error('读取失败');
    const data = await response.json();
    if (request !== summaryRequest || start !== $('summaryStart').value || end !== $('summaryEnd').value) return;
    if (quiet && (view !== 'summary' || $('logDialog').open || $('hoeingSummary').contains(document.activeElement))) return;
    summaryData = data;
    const timeline = $('summaryTimelineDate'); timeline.min = start; timeline.max = end;
    if (!timeline.value || timeline.value < start || timeline.value > end) timeline.value = end;
    $('summaryContent').hidden = false;
    message.textContent = `${start === end ? start : start + ' 至 ' + end} · 更新于 ${summaryClock(data.capturedAtUtc, today)} · 仅统计服务端已保存记录`;
    renderSummary();
  } catch {
    if (request === summaryRequest) message.textContent = '总结读取失败或超时，请刷新重试。' + (summaryData ? '下方为上次成功读取的数据。' : '');
  } finally {
    clearTimeout(timeout); if (request === summaryRequest) summaryPending = '';
  }
}
function renderSummary() {
  const data = summaryData, c = data.counts;
  const metrics = [
    ['参与队伍', `${data.roomCount} 支`, '实际开锄或跨日继续'], ['记录参与玩家', `${data.memberCount} 人`, 'UID 去重'],
    ['累计锄地历时', summaryDuration(data.seconds), '各房间去重后求和'], ['队均历时', summaryDuration(data.averageSeconds), '仅实际参与队伍'],
    ['开锄批次', `${c.started} 次`, `跨日继续 ${c.continued} 次`], ['正常完成', `${c.completed} 次`, `含任务异常 ${c.completedWithErrors} 次`],
    ['正常完成率', summaryRate(c.completionRate), '明确终态批次'], ['当前锄地中', data.end === summaryDay(Date.now()) ? `${data.runningRoomCount} 支` : '—', `主动停止 ${c.stopped} · 异常 ${c.failed}`]
  ];
  $('summaryMetrics').replaceChildren(...metrics.map(([label, value, hint]) => {
    const tile = node('div', 'summary-tile'); tile.append(node('span', 'muted', label), node('strong', '', value), node('small', 'muted', hint)); return tile;
  }));
  const warnings = $('summaryWarnings'); warnings.replaceChildren();
  if (c.unknown || c.notStarted || c.preparing) warnings.append(node('p', '', `结果未确认 ${c.unknown} 批 · 未收到实际开锄 ${c.notStarted} 批（其中可能含正在准备的批次 ${c.preparing} 批）。未开锄不计时长、完成率。`));
  for (const warning of data.warnings) warnings.append(node('p', '', warning));
  warnings.hidden = !warnings.childElementCount;
  renderSummaryRank(); renderSummaryTimeline(); renderSummaryTrend();
}
function summaryFilteredRooms() {
  const query = $('summarySearch').value.trim().toLowerCase();
  return (summaryData?.rooms || []).filter(room => !query || [room.code, room.note, ...room.runs.flatMap(row => row.run.members.flatMap(member => [member.uid, member.name]))]
    .some(value => String(value || '').toLowerCase().includes(query)));
}
function summarySortValue(room, sort) { return room[sort] ?? room.counts[sort] ?? null; }
function summaryStatusLabel(room) {
  if (room.status === 'running' && summaryData.end !== summaryDay(Date.now())) return '后续仍在锄地';
  const run = room.runs.find(row => row.run.status === room.status)?.run;
  return run ? AdminHoeingHistory.runLabel(run) : (summaryStatus[room.status] || '结果未确认');
}
function summaryTable(headers) {
  const table = node('table', 'summary-table'), head = node('thead'), row = node('tr'), body = node('tbody');
  for (const header of headers) { const cell = node('th', '', header); cell.scope = 'col'; row.append(cell); }
  head.append(row); table.append(head, body); return { table, body };
}
function summaryRow(body, values) {
  const row = node('tr'); for (const value of values) { const cell = node('td'); if (value instanceof Node) cell.append(value); else cell.textContent = value; row.append(cell); } body.append(row); return row;
}
function renderSummaryRank() {
  if (!summaryData) return;
  const sort = $('summarySort').value, rooms = summaryFilteredRooms().sort((a, b) => (summarySortValue(b, sort) ?? -1) - (summarySortValue(a, sort) ?? -1) || b.seconds - a.seconds || a.code.localeCompare(b.code));
  $('summaryRankHint').textContent = `${rooms.length} 个助手房间有记录或记录缺口 · 点击队伍查看批次详情`;
  const box = $('summaryRank'); box.replaceChildren();
  if (!rooms.length) box.append(node('p', 'summary-empty', '此范围暂无匹配的已保存记录；没有记录不代表没有锄地。'));
  else {
    const { table, body } = summaryTable(['排名', '助手房间', '成员', '开锄 / 跨日', '正常完成', '累计时长', '最长单次', '正常完成率', '最新结果']);
    table.querySelectorAll('th')[{ seconds: 5, completed: 4, started: 3, completionRate: 7, longestSeconds: 6 }[sort]].setAttribute('aria-sort', 'descending');
    let rank = 0, previous;
    rooms.forEach((room, index) => {
      const value = summarySortValue(room, sort); if (index === 0 || value !== previous) rank = index + 1; previous = value;
      const name = action(summaryName(room), () => selectSummaryRoom(room.code), 'summary-room-link');
      name.setAttribute('aria-expanded', String(summaryRoom === room.code)); name.setAttribute('aria-controls', 'summaryRoomDetail');
      const team = node('div', 'summary-team'); team.append(name); if (room.note) team.append(node('small', 'muted mono', room.code));
      const c = room.counts;
      const row = summaryRow(body, [value == null || !room.runs.length ? '—' : String(rank), team, String(room.memberCount), `${c.started} / ${c.continued}`, String(c.completed), summaryDuration(room.seconds), summaryDuration(room.longestSeconds), summaryRate(c.completionRate), summaryStatusLabel(room)]);
      if (summaryRoom === room.code) row.classList.add('summary-selected');
    }); box.append(table);
  }
  renderSummaryDetail();
}
function selectSummaryRoom(code, runId) {
  summaryRoom = code; renderSummaryRank();
  const detail = $('summaryRoomDetail');
  const target = runId ? [...detail.querySelectorAll('[data-summary-run]')].find(item => item.dataset.summaryRun === runId) : detail;
  target?.scrollIntoView({ block: 'nearest' }); target?.focus({ preventScroll: true });
}
function summaryOpenLogs(code, selection, label) {
  openLogs(code, summaryData.start, selection, label);
  setLogRange(summaryData.start + 'T00:00', summaryData.end + 'T23:59');
}
function renderSummaryDetail() {
  const box = $('summaryRoomDetail'), room = summaryFilteredRooms().find(room => room.code === summaryRoom);
  box.replaceChildren(); box.hidden = !room; if (!room) return;
  box.tabIndex = -1;
  const title = node('div', 'summary-panel-head'); title.append(node('h3', '', `${summaryName(room)} · ${room.code}`), action('收起详情', () => { summaryRoom = null; renderSummaryRank(); })); box.append(title);
  const c = room.counts;
  box.append(node('p', 'muted', `累计 ${summaryDuration(room.seconds)} · 正常完成 ${c.completed} · 含任务异常 ${c.completedWithErrors} · 停止 ${c.stopped} · 异常 ${c.failed} · 未确认 ${c.unknown}`));
  for (const warning of room.warnings) box.append(node('p', 'history-warning', warning));
  for (const row of room.runs) {
    const run = row.run, card = node('article', 'summary-run'); card.dataset.summaryRun = run.id; card.tabIndex = -1;
    const top = node('div', 'summary-panel-head'); top.append(node('strong', '', AdminHoeingHistory.runLabel(run)), node('span', 'summary-duration', `${row.approximate ? '约 ' : ''}${summaryDuration(row.seconds)}`));
    card.append(top, node('p', 'muted', `批次 ${run.id}`), node('p', '', `开锄：${summaryClock(run.startedAtUtc, summaryData.start)} · 结束：${run.endedAtUtc ? summaryClock(run.endedAtUtc, summaryData.start) : '未确认'} · 最后记录：${summaryClock(run.observedAtUtc, summaryData.start)}`),
      node('p', '', '参与名单：' + (run.members.map(member => `${member.name || member.uid} (${member.uid})`).join('、') || '未记录')));
    if (row.fromUtc) card.append(node('p', 'muted', `所选范围计时：${summaryClock(row.fromUtc, summaryData.start)} 至 ${summaryClock(row.toUtc, summaryData.start)}`));
    const reason = AdminHoeingHistory.reasonText(run.reason); if (reason) card.append(node('p', 'muted', reason));
    const actions = node('div', 'summary-filters');
    if (row.worlds.length) actions.append(action('批次日志', () => summaryOpenLogs(room.code, { batch: run.id }, `仅此批次 ${run.id} 已确认关联的世界日志`)));
    actions.append(action('助手日志', () => summaryOpenLogs(room.code, { direct: true }, '仅此助手房间自身保存的日志'))); card.append(actions);
    if (!row.worlds.length) card.append(node('p', 'muted', '所选日期无已确认归属此批次的世界房间关联。'));
    else {
      const worlds = node('ul', 'summary-worlds');
      for (const world of row.worlds) {
        const li = node('li'); li.append(node('span', '', `${world.code} · 房主 ${world.hostName || world.hostUid || '未记录'} · ${summaryClock(world.createdAtUtc, summaryData.start)} – ${summaryClock(world.endedAtUtc, summaryData.start)}`), action('世界日志', () => summaryOpenLogs(world.code, { session: world.sessionId }, `仅世界房间 ${world.code} 此次会话日志`))); worlds.append(li);
      } card.append(worlds);
    }
    box.append(card);
  }
}
function renderSummaryTimeline() {
  if (!summaryData) return;
  const date = $('summaryTimelineDate').value, box = $('summaryTimeline'); box.replaceChildren();
  if (!date || date < summaryData.start || date > summaryData.end) { box.append(node('p', 'summary-empty', '请选择统计范围内的日期。')); return; }
  const start = summaryMidnight(date), end = start + summaryDayMs;
  $('summaryTimelineTitle').textContent = `${date} 锄地时间轴`;
  $('summaryTimelineHint').textContent = '每个批次单独一行，颜色及文字为最新已知结果；重叠区间不会重复计入排行。点击时间段查看明细。';
  const axis = node('div', 'summary-timeline-axis'); axis.append(node('span', '', '助手房间 / 批次'));
  const hours = node('div', 'summary-hour-labels'); for (const hour of [0, 6, 12, 18, 24]) hours.append(node('span', '', `${String(hour).padStart(2, '0')}:00`)); axis.append(hours); box.append(axis);
  const legend = node('div', 'summary-legend');
  for (const state of ['completed', 'running', 'stopped', 'completedWithErrors', 'failed', 'unknown']) {
    const item = node('span', 'summary-legend-item'), mark = node('i', 'summary-state-mark'); mark.dataset.state = state; mark.setAttribute('aria-hidden', 'true'); item.append(mark, node('span', '', summaryStatus[state])); legend.append(item);
  }
  box.append(legend);
  const plot = node('div', 'summary-timeline-rows'); let count = 0;
  for (const room of summaryFilteredRooms()) {
    const rows = room.runs.filter(row => row.fromUtc && Date.parse(row.fromUtc) < end && Date.parse(row.toUtc) > start);
    if (!rows.length) continue;
    const group = node('div', 'summary-timeline-group');
    rows.forEach((row, index) => {
      const a = Math.max(start, Date.parse(row.fromUtc)), b = Math.min(end, Date.parse(row.toUtc));
      const line = node('div', 'summary-timeline-line'); const label = node('div', 'summary-timeline-label');
      if (!index) label.append(node('strong', '', summaryName(room)));
      label.append(node('small', 'muted', `${summaryClock(new Date(a).toISOString(), date)} · ${summaryStatus[row.run.status] || '结果未确认'}`));
      const track = node('div', 'summary-timeline-track');
      const description = `${summaryName(room)} ${summaryClock(new Date(a).toISOString(), date)} 至 ${b === end ? '24:00' : summaryClock(new Date(b).toISOString(), date)} · ${summaryDuration((b - a) / 1000)} · ${summaryStatus[row.run.status] || '结果未确认'}`;
      const mark = action('', () => selectSummaryRoom(room.code, row.run.id), 'summary-timeline-mark'); mark.style.left = `${(a - start) / summaryDayMs * 100}%`; mark.style.width = `${(b - a) / summaryDayMs * 100}%`; mark.dataset.state = row.run.status; mark.title = description; mark.setAttribute('aria-label', description);
      track.append(mark); line.append(label, track); group.append(line); count++;
    }); plot.append(group);
  }
  box.append(count ? plot : node('p', 'summary-empty', '当天没有可计时的匹配批次。未实际开锄的记录仍可在房间排行中查看。'));
}
function renderSummaryTrend() {
  const box = $('summaryTrend'); box.replaceChildren();
  const days = summaryData.days, max = Math.max(1, ...days.map(day => day.seconds));
  const { table, body } = summaryTable(['日期（北京时间）', '累计时长', '参与队伍', '开锄批次', '正常完成']);
  for (const day of days) {
    const value = node('div', 'summary-trend-value'), track = node('div', 'summary-trend-track'), bar = node('span', 'summary-trend-bar');
    bar.style.width = `${day.seconds / max * 100}%`; track.append(bar); track.setAttribute('aria-hidden', 'true');
    value.append(track, node('span', '', summaryDuration(day.seconds)));
    const date = action(day.date, () => { $('summaryTimelineDate').value = day.date; renderSummaryTimeline(); $('summaryTimelineTitle').scrollIntoView({ block: 'nearest' }); }, 'summary-room-link');
    summaryRow(body, [date, value, String(day.rooms), String(day.started), String(day.completed)]);
  }
  const wrap = node('div', 'summary-table-wrap summary-trend-wrap'); wrap.append(table); box.append(wrap);
}
