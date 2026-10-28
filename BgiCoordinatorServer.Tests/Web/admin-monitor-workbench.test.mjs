import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/admin-monitor-ui.js', import.meta.url), 'utf8');

// Exercise the production request lifecycle without duplicating its implementation or
// needing a browser. Rendering is observed separately; these checks concern stale data.
function harness(mode = 'live') {
    let now = 0, nextTimer = 0;
    const timers = new Map(), elements = new Map(), requests = [];
    const element = id => {
        if (!elements.has(id)) elements.set(id, {
            value: id === 'historyDate' ? '2026-10-29' : '', textContent: '', hidden: false,
            options: [], replaceChildren(...options) { this.options = options; },
            add(option) { this.options.push(option); }
        });
        return elements.get(id);
    };
    const schedule = (callback, delay) => {
        const id = ++nextTimer;
        timers.set(id, { at: now + delay, callback });
        return id;
    };
    const context = vm.createContext({
        document: { getElementById: element },
        fetch(url, options) {
            return new Promise((resolve, reject) => requests.push({
                url, options, resolve(data) { resolve({ ok: true, status: 200, json: async () => data }); }, reject
            }));
        },
        Option: class { constructor(text, value) { this.text = text; this.value = value; } },
        URLSearchParams, AbortController,
        AbortSignal: { timeout(delay) { const controller = new AbortController(); schedule(() => controller.abort(), delay); return controller.signal; } },
        setTimeout: schedule, clearTimeout: id => timers.delete(id),
        location: { replace() { assert.fail('successful test response must not redirect to login'); } }
    });
    vm.runInContext(source, context, { filename: 'admin-monitor-ui.js' });
    vm.runInContext(`
        view = ${JSON.stringify(mode)};
        globalThis.renderCount = 0;
        render = () => { globalThis.renderCount++; };
        globalThis.api = {
            load, loadDaily,
            seedHistory(data) { dailySnapshot = data; selected.history = 'history:OLD001'; },
            inspect() { return { snapshot, dailySnapshot, historyLoading, liveLoading, selectedHistory: selected.history, renderCount }; }
        };
    `, context);
    return {
        api: context.api, requests, element,
        setDate(value) { element('historyDate').value = value; },
        advance(milliseconds) {
            now += milliseconds;
            for (const [id, timer] of [...timers]) if (timer.at <= now) { timers.delete(id); timer.callback(); }
        }
    };
}

const liveData = count => ({ capturedAtUtc: '2026-10-29T01:00:00Z', connectedControlRoomCount: count,
    connectedControlMemberCount: count * 4, totalRoomCount: count, startedRoomCount: 1, controlRooms: [], rooms: [] });
const historyData = date => ({ date, dates: [date], rooms: [], unlinkedRooms: [], warnings: [] });

test('a live response taking twelve seconds survives the ten-second poll', async () => {
    const h = harness(), first = h.api.load();
    h.advance(10_000);
    await h.api.load();
    assert.equal(h.requests.length, 1, 'a poll must not start another request or invalidate the first');
    h.advance(2_000);
    const data = liveData(6);
    h.requests[0].resolve(data);
    await first;
    assert.equal(h.api.inspect().snapshot, data);
    assert.equal(h.element('controlRooms').textContent, 6);
    assert.equal(h.api.inspect().liveLoading, false);
    assert.equal(h.api.inspect().renderCount, 1);

    const next = h.api.load();
    assert.equal(h.requests.length, 2, 'later refreshes must remain available after completion');
    h.requests[1].resolve(liveData(7));
    await next;
    assert.equal(h.api.inspect().snapshot.connectedControlRoomCount, 7);
});

test('a same-date history response taking twelve seconds survives the ten-second poll', async () => {
    const h = harness('history'), first = h.api.loadDaily();
    h.advance(10_000);
    await h.api.loadDaily();
    assert.equal(h.requests.length, 1);
    h.advance(2_000);
    const data = historyData('2026-10-29');
    h.requests[0].resolve(data);
    await first;
    assert.equal(h.api.inspect().dailySnapshot, data);
    assert.equal(h.api.inspect().historyLoading, false);
    assert.equal(h.element('historyDates').value, '2026-10-29');
});

test('switching dates rejects the old response regardless of response order', async () => {
    for (const newerFirst of [false, true]) {
        const h = harness('history');
        h.setDate('2026-10-28');
        const oldRequest = h.api.loadDaily();
        h.setDate('2026-10-29');
        const newRequest = h.api.loadDaily();
        assert.equal(h.requests.length, 2);
        assert.equal(h.requests[0].options.signal.aborted, true, 'changing the date cancels the old transport');

        // Resolve aborted requests too: cancellation cannot be the only stale-data guard.
        if (newerFirst) {
            h.requests[1].resolve(historyData('2026-10-29'));
            await newRequest;
            h.requests[0].resolve(historyData('2026-10-28'));
            await oldRequest;
        } else {
            h.requests[0].resolve(historyData('2026-10-28'));
            await oldRequest;
            assert.equal(h.api.inspect().dailySnapshot, null);
            assert.equal(h.api.inspect().historyLoading, true, 'old completion must not finish the new loading state');
            h.requests[1].resolve(historyData('2026-10-29'));
            await newRequest;
        }
        assert.equal(h.api.inspect().dailySnapshot.date, '2026-10-29');
        assert.equal(h.element('historyDates').value, '2026-10-29');
        assert.equal(h.api.inspect().historyLoading, false);
    }
});

test('clearing the date clears the previous room and rejects an outstanding response', async () => {
    const h = harness('history');
    h.api.seedHistory(historyData('2026-10-28'));
    const outstanding = h.api.loadDaily();
    h.setDate('');
    await h.api.loadDaily();
    assert.equal(h.requests.length, 1, 'an empty date must not issue a request');
    assert.equal(h.requests[0].options.signal.aborted, true);
    assert.equal(h.api.inspect().dailySnapshot, null);
    assert.equal(h.api.inspect().selectedHistory, null);
    assert.equal(h.api.inspect().historyLoading, false);
    assert.equal(h.element('viewHint').textContent, '请选择有效的历史日期');

    h.requests[0].resolve(historyData('2026-10-29'));
    await outstanding;
    assert.equal(h.api.inspect().dailySnapshot, null, 'the old response must not restore stale room cards');
    assert.equal(h.api.inspect().selectedHistory, null);
    assert.equal(h.element('viewHint').textContent, '请选择有效的历史日期');
});
