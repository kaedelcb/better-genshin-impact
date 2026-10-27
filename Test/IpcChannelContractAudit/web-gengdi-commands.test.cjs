const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../../BgiCoordinatorServer/wwwroot/control-room-commands.js'), 'utf8');
const tick = () => new Promise(resolve => setImmediate(resolve));
function fixture() {
    const timers = new Map(), sent = [], results = [];
    let id = 0;
    const ctx = vm.createContext({ roomReady: true, players: [{ playerUid: 'a', online: true }, { playerUid: 'off', online: false }],
        pendingConfigApplied: new Map(), log() {},
        setTimeout: fn => { timers.set(++id, fn); return id; }, clearTimeout: id => timers.delete(id),
        sendRemoteCommand: cmd => { sent.push(cmd); return Promise.resolve(); },
        isValidCompletionAction: a => ['resume', 'stop', 'runSpecified'].includes(a.policy) && ['group', 'onedragon'].includes(a.taskType) && typeof a.taskName === 'string' });
    vm.runInContext(source + '\nglobalThis.api={sendTrackedCommand,consumeMemberReply,consumeMemberResult,interruptMemberWaits,validateBinding,normalizePolicy};', ctx);
    return { ...ctx.api, ctx, timers, sent, results };
}
const command = { cmd: 'set_scheduled_online_time', commandId: 'id-1', target: ['a'] };
function ack(extra = {}) { return { cmd: 'ack', commandId: 'id-1', senderUid: 'a', params: { status: 'success', message: 'saved' }, ...extra }; }
test('delivery is not application; wrong sender and request cannot finish the operation', async () => {
    const f = fixture(); let done = false;
    const pending = f.sendTrackedCommand(command).then(r => { done = true; return r; }); await tick();
    assert.equal(done, false); f.consumeMemberReply(ack({ senderUid: 'b' })); f.consumeMemberReply(ack({ commandId: 'other' }));
    f.consumeMemberReply(ack({ params: { status: 'queued' } })); await tick(); assert.equal(done, false);
    f.consumeMemberReply(ack()); assert.equal((await pending).status, 'success'); assert.equal(f.timers.size, 0);
});
test('policy reads require a matching typed data reply, not generic ack', async () => {
    const f = fixture(); let done = false;
    const pending = f.sendTrackedCommand({ ...command, cmd: 'task_policy.pull' }, 'task_policy.data').then(r => { done = true; return r; });
    f.consumeMemberReply(ack()); f.consumeMemberReply(ack({ cmd: 'task_policy.push_result', params: { ok: 'true' } }));
    await tick(); assert.equal(done, false);
    f.consumeMemberReply(ack({ cmd: 'task_policy.data', params: { ok: 'false', message: 'refused' } }));
    assert.equal((await pending).status, 'failed');
});
test('timeout and disconnect preserve unknown; late success never triggers a new send', async () => {
    for (const disconnect of [false, true]) {
        const f = fixture(); const pending = f.sendTrackedCommand(command); await tick();
        if (disconnect) f.interruptMemberWaits(); else [...f.timers.values()][0]();
        assert.equal((await pending).status, 'unknown'); f.consumeMemberReply(ack()); assert.equal(f.sent.length, 1);
    }
});
test('same pending write is blocked and offline/disconnected targets never send', async () => {
    const f = fixture(); const pending = f.sendTrackedCommand(command);
    assert.throws(() => f.sendTrackedCommand({ ...command, commandId: 'id-2' }), /正在等待/);
    assert.throws(() => f.sendTrackedCommand({ ...command, target: ['off'] }), /离线/);
    f.consumeMemberReply(ack()); await pending; f.ctx.roomReady = false;
    assert.throws(() => f.sendTrackedCommand(command), /尚未连接/);
});
test('binding validation preserves type positions, rejects empty, duplicate and unavailable tasks', () => {
    const f = fixture(), member = { configGroups: ['A'], oneClickConfigs: ['B'] };
    f.validateBinding(['A', 'B'], ['group', 'onedragon'], member);
    assert.throws(() => f.validateBinding([], [], member));
    assert.throws(() => f.validateBinding(['A', 'A'], ['group', 'group'], member), /重复/);
    assert.throws(() => f.validateBinding(['A'], ['onedragon'], member), /不存在/);
    assert.throws(() => f.validateBinding(['A', 'B'], ['group'], member));
    assert.throws(() => f.normalizePolicy({ policy: 'invalid', specifiedType: 'group', specifiedName: '' }));
});
