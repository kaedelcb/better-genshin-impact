import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const context = vm.createContext({});
const html = readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/admin-monitor.html', import.meta.url), 'utf8');
const helper = html.match(/<script data-hoeing-history>([\s\S]*?)<\/script>/);
assert.ok(helper, 'production page contains the history helpers inline under its existing CSP');
vm.runInContext(helper[1], context);
const view = context.AdminHoeingHistory;
const run = (status, extra = {}) => ({ id: 'one', status, requestedAtUtc: '2026-10-25T01:00:00Z',
    startedAtUtc: '2026-10-25T01:01:00Z', observedAtUtc: '2026-10-25T02:00:00Z',
    endedAtUtc: '2026-10-25T02:00:00Z', members: [], ...extra });
const day = runs => ({ date: '2026-10-25', status: runs.at(-1)?.status || 'none', runs, warnings: [] });

test('finished history remains finished when there is no current running member', () => {
    const history = day([run('completed')]);
    assert.equal(view.headline(history), '已完成');
    assert.match(view.overview(history), /今日开锄 1 次 · 已完成 1 次/);
    assert.match(view.overview(history), /最近开锄 09:01:00 · 结束 10:00:00/);
});

test('no record, unreadable history and incomplete history are distinct', () => {
    assert.equal(view.headline(day([])), '今日暂无记录');
    assert.match(view.overview(day([])), /不代表今天没锄/);
    assert.equal(view.headline(null), '今日记录不可用');
    assert.equal(view.headline({ ...day([]), warnings: ['读取失败'] }), '今日记录不完整');
});

test('unknown or failed runs and completed runs with errors never count as success', () => {
    const history = day([run('completed'), run('completedWithErrors'), run('stopped'), run('failed'), run('unknown')]);
    assert.equal(view.headline(history), '结束未确认');
    assert.match(view.overview(history), /已完成 1 次/);
    assert.match(view.overview(history), /含任务异常 1 次/);
    assert.equal(view.runLabel(run('unknown')), '结束未确认');
});

test('completed ordinary flow without actual hoeing is clearly qualified and not counted', () => {
    const history = day([run('completed', { startedAtUtc: null })]);
    assert.match(view.headline(history), /未收到开锄上报/);
    assert.match(view.overview(history), /今日开锄 0 次 · 已完成 0 次/);
});

test('cross-midnight runs use Beijing dates even with a different host timezone', () => {
    const history = day([run('completed', { startedAtUtc: '2026-10-24T15:59:00Z' })]);
    assert.match(view.overview(history), /今日开锄 0 次 · 跨日继续 1 次/);
    assert.equal(view.time('2026-10-24T15:59:00Z', history.date), '2026-10-24 23:59:00');
    assert.equal(view.time('2026-10-24T16:00:00Z', history.date), '00:00:00');
});

test('preparing after a completed run preserves the completed count', () => {
    const history = day([run('completed'), run('preparing', { requestedAtUtc: '2026-10-25T03:00:00Z', startedAtUtc: null, endedAtUtc: null })]);
    assert.equal(view.headline(history), '准备开锄');
    assert.match(view.overview(history), /已完成 1 次/);
});
