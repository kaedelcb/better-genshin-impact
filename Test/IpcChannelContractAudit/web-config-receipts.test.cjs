// Execute the actual web receipt functions with an inert transport and virtual timer.
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const source = fs.readFileSync(require('node:path').join(__dirname, '../../BgiCoordinatorServer/wwwroot/control-room.js'), 'utf8');
const start = source.indexOf('const pendingConfigApplied = new Map();');
const end = source.indexOf('// ---------- 记住登录', start);
assert(start > 0 && end > start, 'production receipt block not found');
let passed = 0;
async function check(name, action) { await action(); console.log('PASS ' + name); passed++; }
function fixture(send = () => Promise.resolve({ ack: true })) {
    const timers = new Map(); let id = 0;
    const context = vm.createContext({ sendRemoteCommand: send,
        setTimeout: fn => { timers.set(++id, fn); return id; }, clearTimeout: key => timers.delete(key) });
    vm.runInContext(source.slice(start, end) + '\nglobalThis.api = { sendAndWaitConfigApplied, consumeConfigApplied, pendingConfigApplied, resolveConfigTargets };', context);
    return { ...context.api, timers };
}
const command = { commandId: 'command-1', cmd: 'set_task_enabled' };
const receipt = { commandId: 'command-1', cmd: 'set_task_enabled', targetUid: 'uid', status: 'success',
    configRevision: 'revision', targetProcessId: 55, targetStartTicksUtc: '638937012345678901' };
(async () => {
    await check('W01 gateway acknowledgement never substitutes config applied', async () => {
        const f = fixture(); let done = false;
        const pending = f.sendAndWaitConfigApplied(command, 'uid').then(() => done = true);
        await Promise.resolve(); await Promise.resolve(); assert.equal(done, false);
        f.consumeConfigApplied(receipt); await pending; assert.equal(done, true); assert.equal(f.timers.size, 0);
    });
    await check('W02 wrong target or command cannot release waiter', async () => {
        const f = fixture(); let done = false;
        const pending = f.sendAndWaitConfigApplied(command, 'uid').then(() => done = true);
        f.consumeConfigApplied({ ...receipt, targetUid: 'other' });
        f.consumeConfigApplied({ ...receipt, commandId: 'other' });
        f.consumeConfigApplied({ ...receipt, cmd: 'start_group' });
        await Promise.resolve(); assert.equal(done, false);
        f.consumeConfigApplied(receipt); await pending;
    });
    await check('W03 failed apply blocks dependent action', async () => {
        const f = fixture(); const pending = f.sendAndWaitConfigApplied(command, 'uid');
        const rejected = assert.rejects(pending, /not-applied/);
        f.consumeConfigApplied({ ...receipt, status: 'failed', message: 'not-applied' }); await rejected;
        assert.equal(f.pendingConfigApplied.size, 0);
    });
    await check('W04 old client receipt missing revision cannot authorize start', async () => {
        const f = fixture(); const pending = f.sendAndWaitConfigApplied(command, 'uid');
        const rejected = assert.rejects(pending, /未确认配置版本/);
        f.consumeConfigApplied({ ...receipt, configRevision: undefined }); await rejected;
    });
    await check('W05 timeout blocks start and ignores late result', async () => {
        const f = fixture(); const pending = f.sendAndWaitConfigApplied(command, 'uid');
        const rejected = assert.rejects(pending, /超时/); [...f.timers.values()][0](); await rejected;
        f.consumeConfigApplied(receipt); assert.equal(f.pendingConfigApplied.size, 0);
    });
    await check('W06 forwarding failure blocks start and releases waiter', async () => {
        const f = fixture(() => Promise.reject(new Error('offline')));
        await assert.rejects(f.sendAndWaitConfigApplied(command, 'uid'), /offline/);
        assert.equal(f.pendingConfigApplied.size, 0); assert.equal(f.timers.size, 0);
    });
    await check('W07 synchronous receipt is not lost and ticks stay lossless', async () => {
        let f; f = fixture(() => { f.consumeConfigApplied(receipt); return Promise.resolve(); });
        const applied = await f.sendAndWaitConfigApplied(command, 'uid');
        assert.equal(applied.targetStartTicksUtc, '638937012345678901');
        assert.equal(typeof applied.targetStartTicksUtc, 'string');
    });
    await check('W08 each target waits for its own actual application', async () => {
        const f = fixture(); const a = f.sendAndWaitConfigApplied(command, 'uid');
        let done = false; const b = f.sendAndWaitConfigApplied({ ...command, commandId: 'b' }, 'second').then(() => done = true);
        f.consumeConfigApplied(receipt); await a; assert.equal(done, false);
        f.consumeConfigApplied({ ...receipt, commandId: 'b', targetUid: 'second' }); await b;
    });
    await check('W09 broadcast resolves to concrete online executor IDs', async () => {
        const f = fixture();
        const targets = f.resolveConfigTargets(['*'], [
            { playerUid: 'a', online: true }, { playerUid: 'b', online: true },
            { playerUid: 'a', online: true }, { playerUid: 'offline', online: false }]);
        assert.deepEqual(Array.from(targets), ['a', 'b']);
        assert.deepEqual(Array.from(f.resolveConfigTargets(['*'], [])), []);
    });
    await check('W10 explicit targets remain explicit and are deduplicated', async () => {
        const f = fixture();
        assert.deepEqual(Array.from(f.resolveConfigTargets(['selected', 'selected'], [{ playerUid: 'other', online: true }])), ['selected']);
    });
    console.log(`Web receipt audit: ${passed}/${passed}; no browser, network or product process.`);
})().catch(error => { console.error(error); process.exitCode = 1; });
