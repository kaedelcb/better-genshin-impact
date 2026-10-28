'use strict';
$('liveTab').addEventListener('click', () => setView('live'));
$('historyTab').addEventListener('click', () => setView('history'));
$('findLogs').addEventListener('click', () => { setView('history'); $('archiveCode')?.focus(); });
$('search').addEventListener('input', () => { queries[view] = $('search').value; selected[view] = null; unlinkedPage = 0; render(); });
$('filter').addEventListener('change', () => { selected.live = null; render(); });
$('roomType').addEventListener('change', () => { selected.live = null; render(); });
$('refresh').addEventListener('click', () => { load(); if (view === 'history') { loadDaily(); loadArchive(); } });
$('logout').addEventListener('click', async () => {
  try {
    const response = await fetch('/api/admin/logout', { method: 'POST', credentials: 'same-origin' });
    if (!response.ok) throw new Error(); location.replace('/admin/login');
  } catch { $('notice').hidden = false; $('notice').textContent = '退出失败，请重试。'; }
});
$('historyDate').value = beijingMinute().slice(0, 10);
$('historyDate').addEventListener('change', () => { selected.history = null; unlinkedPage = 0; loadDaily(); });
$('historyDates').addEventListener('change', () => {
  if ($('historyDates').value) { $('historyDate').value = $('historyDates').value; selected.history = null; unlinkedPage = 0; loadDaily(); }
});
$('historyToday').addEventListener('click', () => { $('historyDate').value = beijingMinute().slice(0, 10); selected.history = null; unlinkedPage = 0; loadDaily(); });
$('logMerge').addEventListener('change', () => {
  if (logDownloading) return;
  activeLogSelection = $('logMerge').checked ? {} : { direct: true };
  $('logScope').textContent = $('logMerge').checked ? '合并此房间及已确认关联的历史世界日志' : '仅此房间号自身保存的日志';
});
$('logDownload').addEventListener('click', downloadLogs);
$('logRefresh').addEventListener('click', () => { loadLogs(); if (activeLogCode) loadLogDates(activeLogCode); });
$('logClose').addEventListener('click', () => $('logDialog').close());
for (const id of ['logStartHour', 'logEndHour', 'logStartMinute', 'logEndMinute']) {
  const max = id.endsWith('Hour') ? 24 : 60;
  for (let index = 0; index < max; index++) { const value = String(index).padStart(2, '0'); $(id).add(new Option(value, value)); }
}
for (const id of rangeControls) $(id).addEventListener('change', () => { $('logDownloadStatus').textContent = ''; updateLogRange(); });
$('logSavedDate').addEventListener('change', () => {
  const date = $('logSavedDate').value;
  if (date) { $('logDownloadStatus').textContent = ''; setLogRange(date + 'T00:00', date + 'T23:59'); }
});
$('logToday').addEventListener('click', () => { $('logDownloadStatus').textContent = ''; todayLogRange(); });
$('logLastHour').addEventListener('click', () => { $('logDownloadStatus').textContent = ''; setLogRange(beijingMinute(Date.now() - 3600000), beijingMinute()); });
$('logDialog').addEventListener('close', () => { activeLogCode = null; logView++; downloadController?.abort(); logRequest++; logDateRequest++; });
load();
let resizeTimer;
window.addEventListener('resize', () => { clearTimeout(resizeTimer); resizeTimer = setTimeout(revealSelection, 100); });
setInterval(() => { load(); if (view === 'history') loadDaily(); }, 10000);
