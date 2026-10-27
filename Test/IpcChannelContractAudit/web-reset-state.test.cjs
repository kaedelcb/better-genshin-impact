// Run the production reset flow against an inert gateway: never touches a real room.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../../BgiCoordinatorServer/wwwroot/control-room.js'), 'utf8');
const end = source.indexOf('// ---------- 记住登录');
assert(end > 0);
const tick = () => new Promise(resolve => setImmediate(resolve));
function fixture(options = {}) {
    const calls = [], timers = new Map();
    let timerId = 0;
    const ctx = vm.createContext({ crypto: { randomUUID: () => 'reset-id' },
        setTimeout: fn => { timers.set(++timerId, fn); return timerId; },
        clearTimeout: id => timers.delete(id), renderMembers() {} });
    vm.runInContext(source.slice(0, end) + `
        log = () => {};
        globalThis.api = { resetMemberState, consumeStateResetResult, interruptStateResetWaits,
            setConnection: value => { connection = value; }, setPlayers: value => { players = value; } };
    `, ctx);
    const api = ctx.api;
    api.setPlayers([{ playerUid: 'a', playerName: '成员甲', online: true },
        { playerUid: 'b', playerName: '成员乙', online: true },
        { playerUid: 'off', online: false }]);
    api.setConnection({ invoke: async (method, env) => {
        calls.push({ method, ...env });
        if (env.name === 'control.batch.current')
            return { payload: options.noBatch ? null : { batchId: 'batch-1', generation: 7 } };
        if (env.name === 'control.batch.reset')
            return { payload: options.resetResponse ?? { resetId: env.payload.resetId,
                batchId: options.noBatch ? '' : 'batch-1', generation: options.noBatch ? 0 : 7 } };
        if (options.sendError) throw new Error('transport interrupted');
        if (options.immediate) receipt('a');
        return { payload: { ack: true } };
    } });
    function receipt(uid, status = 'success', extra = {}) {
        const commandId = calls.find(c => c.name === 'control.batch.reset')?.payload.resetId;
        api.consumeStateResetResult({ cmd: 'reset_state', commandId, targetUid: uid, status, ...extra });
    }
    return { ...api, calls, timers, receipt, run: all => api.resetMemberState('a', !!all, () => {}) };
}
test('server invalidation precedes executor reset; delivery ack is not completion', async () => {
    const f = fixture(); let finished = false;
    const done = f.run().then(result => { finished = true; return result; });
    await tick();
    assert.deepEqual(f.calls.map(c => c.name), ['control.batch.current', 'control.batch.reset', 'control.sendCommand']);
    assert.equal(finished, false);
    const reset = f.calls[1].payload, cmd = f.calls[2].payload.command;
    assert.equal(reset.expectedBatchId, 'batch-1'); assert.equal(reset.expectedGeneration, 7);
    assert.equal(cmd.commandId, reset.resetId); assert.equal(cmd.params.resetId, reset.resetId);
    assert.equal(cmd.params.batchId, 'batch-1'); assert.equal(cmd.params.generation, 7);
    assert.deepEqual(Array.from(cmd.target), ['a']);
    f.receipt('a', 'accepted'); f.receipt('b'); f.receipt('a', 'success', { commandId: 'other' });
    await tick(); assert.equal(finished, false);
    f.receipt('a'); assert.match(await done, /均已确认重置成功/); assert.equal(f.timers.size, 0);
});
test('all online members require separate receipts, with partial failures retained', async () => {
    const f = fixture(); const done = f.run(true); await tick();
    assert.deepEqual(Array.from(f.calls[1].payload.targetUids), ['a', 'b']);
    assert.deepEqual(Array.from(f.calls[2].payload.command.target), ['a', 'b']);
    f.receipt('a'); f.receipt('a', 'failed');
    f.receipt('b', 'failed', { message: 'BGI仍在执行' });
    const text = await done;
    assert.match(text, /成员甲：重置成功/); assert.match(text, /成员乙：重置未完成（BGI仍在执行）/);
});
test('stale server batch blocks executor command', async () => {
    const f = fixture({ resetResponse: { resetId: 'reset_resetid', reason: 'stale_reset' } });
    await assert.rejects(f.run(), /批次已改变/); assert.equal(f.calls.length, 2);
});
test('missing or rejected server response cannot authorize executor reset', async () => {
    for (const resetResponse of [{}, { error: { code: 'unsupported_operation' } }]) {
        const f = fixture({ resetResponse }); await assert.rejects(f.run());
        assert.equal(f.calls.length, 2); assert.equal(f.timers.size, 0);
    }
});
test('no active batch still allows recovery of local execution state', async () => {
    const f = fixture({ noBatch: true, immediate: true });
    assert.match(await f.run(), /重置成功/);
    assert.equal(f.calls[2].payload.command.params.batchId, undefined);
    assert.equal(f.timers.size, 0);
});
test('timeout retains successful member and marks only missing receipt unknown', async () => {
    const f = fixture(); const done = f.run(true); await tick(); f.receipt('a');
    [...f.timers.values()][0]();
    const text = await done;
    assert.match(text, /成员甲：重置成功/); assert.match(text, /成员乙：未确认/);
    f.receipt('b'); assert.equal(f.timers.size, 0);
});
test('disconnect and send failures remain unconfirmed and never resend automatically', async () => {
    for (const sendError of [false, true]) {
        const f = fixture({ sendError }); const done = f.run(); await tick();
        if (!sendError) f.interruptStateResetWaits();
        assert.match(await done, /未确认/); assert.equal(f.calls.length, 3); assert.equal(f.timers.size, 0);
    }
});
test('duplicate click while pending is blocked and busy state releases after completion', async () => {
    const f = fixture(); const done = f.run(); await tick();
    await assert.rejects(f.run(), /已有重置/); assert.equal(f.calls.length, 3);
    f.receipt('a'); await done;
    const again = f.run(); await tick(); f.receipt('a'); await again;
    assert.equal(f.calls.length, 6);
});
test('offline selected member or no online members does not invalidate the shared batch', async () => {
    const f = fixture();
    await assert.rejects(f.resetMemberState('off', false, () => {}), /在线成员/);
    f.setPlayers([]); await assert.rejects(f.run(true), /在线成员/);
    assert.equal(f.calls.length, 0);
});
