import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/admin-hoeing-summary.js', import.meta.url), 'utf8');
function harness() {
    const elements = new Map(), requests = [];
    const $ = id => {
        if (!elements.has(id)) elements.set(id, { value: '', textContent: '', hidden: false });
        return elements.get(id);
    };
    const context = vm.createContext({ $, Date, AbortController, URLSearchParams, setTimeout, clearTimeout,
        AdminHoeingHistory: { time: () => '12:00' }, location: { replace(path) { context.redirect = path; } },
        fetch(url, options) { return new Promise(resolve => requests.push({ url, options, resolve(data, status = 200) { resolve({ ok: status === 200, status, json: async () => data }); } })); }
    });
    vm.runInContext(source, context);
    vm.runInContext(`renderSummary = () => {}; globalThis.api = { loadSummary, summaryDuration, summaryRate, summaryDay, summarySortValue, data: () => summaryData };`, context);
    const range = (start, end = start) => { $('summaryStart').value = start; $('summaryEnd').value = end; };
    return { $, range, requests, api: context.api, context };
}
const data = date => ({ start: date, end: date, capturedAtUtc: date + 'T00:00:00Z' });

test('duration distinguishes zero and missing, and Beijing day is independent of system timezone', () => {
    const { api } = harness();
    assert.equal(api.summaryDuration(0), '0 分');
    assert.equal(api.summaryDuration(13), '< 1 分');
    assert.equal(api.summaryDuration(7260), '2 小时 01 分');
    assert.equal(api.summaryDuration(NaN), '—');
    assert.equal(api.summaryDay(Date.parse('2026-11-25T16:00:00Z')), '2026-11-26');
    assert.equal(api.summaryRate(null), '—');
    assert.equal(api.summaryRate(0), '0.0%');
});
test('out-of-order date responses never replace the selected range and same range is deduplicated', async () => {
    const h = harness(); h.range('2026-10-20');
    const first = h.api.loadSummary(); await h.api.loadSummary(); assert.equal(h.requests.length, 1);
    h.range('2026-10-21'); const second = h.api.loadSummary();
    assert.equal(h.requests[0].options.signal.aborted, true);
    h.requests[1].resolve(data('2026-10-21')); await second;
    h.requests[0].resolve(data('2026-10-20')); await first;
    assert.equal(h.api.data().start, '2026-10-21');
    assert.equal(h.$('summaryContent').hidden, false);
});
test('invalid input clears old data, failed refresh identifies stale data, unauthorized redirects', async () => {
    const h = harness(); h.range('2026-10-20');
    const first = h.api.loadSummary(); h.requests[0].resolve(data('2026-10-20')); await first;
    const failure = h.api.loadSummary(); h.requests[1].resolve({}, 503); await failure;
    assert.match(h.$('summaryMessage').textContent, /上次成功/);
    h.range('2026-10-21', '2026-10-20'); await h.api.loadSummary();
    assert.equal(h.api.data(), null); assert.equal(h.$('summaryContent').hidden, true);
    h.range('2026-10-21'); const denied = h.api.loadSummary(); h.requests[2].resolve({}, 401); await denied;
    assert.equal(h.context.redirect, '/admin/login');
});
test('ranking treats unknown completion rate as missing, not 100% or zero', () => {
    const { api } = harness();
    assert.equal(api.summarySortValue({ counts: { completionRate: null } }, 'completionRate'), null);
    assert.equal(api.summarySortValue({ counts: { completionRate: 0 } }, 'completionRate'), 0);
    assert.equal(api.summarySortValue({ seconds: 3600, counts: {} }, 'seconds'), 3600);
});
