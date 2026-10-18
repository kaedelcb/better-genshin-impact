// Protocol and confirmation-snapshot checks against the production web script.
// No browser, DOM rendering, real gateway, executor, or user data is used here.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/control-room.js', import.meta.url), 'utf8');
const quickKeys = ['一键传奇', '一键次数盾', '一键精英', '一键小怪', '一键自定义'];
const capabilities = ['hoeing.start.v2', 'hoeing.completionChoice.v1', 'hoeing.quickCommandCompletion.v1'];
const clone = value => structuredClone(value);

function members() {
    return ['u0', 'u1', 'u2'].map((uid, i) => ({
        playerUid: uid, playerName: '成员' + i, online: true,
        quickCommands: Object.fromEntries(quickKeys.map(key => [key, (i % 2 ? 'ONEDRAGON:' : 'GROUP:') + key + i])),
        hoeingCompletionAction: { policy: i === 0 ? 'resume' : 'runSpecified', taskType: i === 2 ? 'onedragon' : 'group',
            taskName: i === 0 ? '' : '收尾' + i, priority: i }
    }));
}

function harness({ current = members(), selected = clone(current.slice(0, 2)), caps = capabilities, invoke } = {}) {
    const calls = [], logs = [], elements = new Map();
    let requestNumber = 0;
    const context = vm.createContext({
        document: { getElementById(id) {
            if (!elements.has(id)) elements.set(id, { addEventListener() {}, value: '', style: {} });
            return elements.get(id);
        } },
        localStorage: { getItem() { return null; } },
        crypto: { randomUUID() { return 'test-request-' + ++requestNumber; } },
        __current: current, __selected: selected, __caps: clone(caps), __logs: logs,
        __invoke: async (method, envelope) => {
            calls.push({ method, envelope: clone(envelope) });
            if (invoke) return invoke(method, envelope, calls);
            return { payload: { batchId: 'batch-test', protocol: 'hoeing.start.v2', phase: 'registering' } };
        }
    });
    vm.runInContext(source, context, { filename: 'control-room.js' });
    vm.runInContext('players = __current; serverCapabilities = __caps; connection = { invoke: __invoke }; log = message => __logs.push(message);', context);
    return {
        calls, logs, context, current, selected,
        begin(key = quickKeys[0], useActions = true, label = key || '一键锄地') {
            context.__key = key; context.__useActions = useActions; context.__label = label;
            return vm.runInContext('beginCoordinatedQuickCommand(__selected, __useActions, __key, __label)', context);
        }
    };
}

for (const key of quickKeys) {
    test(key + ' sends its exact members and completion choice in one begin', async () => {
        const h = harness();
        await h.begin(key);
        assert.equal(h.calls.length, 1);
        const { method, envelope } = h.calls[0];
        assert.equal(method, 'Dispatch');
        assert.equal(envelope.name, 'control.batch.begin');
        assert.equal(envelope.payload.quickCommandKey, key);
        assert.equal(envelope.payload.useCompletionActions, true);
        assert.deepEqual(envelope.payload.targetUids, ['u0', 'u1']);
        assert.ok(!envelope.payload.targetUids.includes('*'));
        assert.ok(!envelope.payload.targetUids.includes('u2'));
        assert.equal(envelope.payload.groupName, undefined);
        assert.equal(envelope.payload.configName, undefined);
        assert.match(envelope.payload.requestId, /^web_hoeing_/);
        assert.ok(Date.parse(envelope.payload.expiresAtUtc) > Date.now());
        assert.ok(h.logs.some(message => message.includes('已受理')));
    });
}

test('one-click hoeing keeps its false choice and omits the quick key for compatibility', async () => {
    const h = harness({ caps: ['hoeing.start.v2'] });
    await h.begin('', false, '一键锄地');
    assert.equal(h.calls.length, 1);
    assert.equal(h.calls[0].envelope.name, 'control.batch.begin');
    assert.equal(h.calls[0].envelope.payload.useCompletionActions, false);
    assert.ok(!Object.hasOwn(h.calls[0].envelope.payload, 'quickCommandKey'));
    assert.deepEqual(h.calls[0].envelope.payload.targetUids, ['u0', 'u1']);
});

for (const missing of capabilities) {
    test('missing ' + missing + ' refuses before sending', async () => {
        const h = harness({ caps: capabilities.filter(capability => capability !== missing) });
        await h.begin();
        assert.equal(h.calls.length, 0);
        assert.ok(h.logs.some(message => message.includes('未下发') || message.includes('未受理')));
    });
}

test('invalid completion action refuses before sending even when the snapshot is current', async () => {
    const current = members();
    current[1].hoeingCompletionAction = { policy: 'runSpecified', taskType: 'group', taskName: ' ', priority: 0 };
    const h = harness({ current });
    await h.begin();
    assert.equal(h.calls.length, 0);
    assert.ok(h.logs.some(message => message.includes('绑定动作不完整')));
});

test('a displayed member going offline refuses the whole request instead of shrinking its roster', async () => {
    const current = members(), selected = clone(current.slice(0, 2));
    current[1].online = false;
    const h = harness({ current, selected });
    await h.begin();
    assert.equal(h.calls.length, 0);
    assert.ok(h.logs.some(message => message.includes('已离线')));
});

test('a displayed task binding changing requires confirmation again', async () => {
    const current = members(), selected = clone(current.slice(0, 2));
    current[1].quickCommands[quickKeys[0]] = 'GROUP:另一个任务';
    const h = harness({ current, selected });
    await h.begin();
    assert.equal(h.calls.length, 0);
    assert.ok(h.logs.some(message => message.includes('已改变')));
});

test('a displayed completion action changing requires confirmation again', async () => {
    const current = members(), selected = clone(current.slice(0, 2));
    current[1].hoeingCompletionAction.taskName = '另一个收尾';
    const h = harness({ current, selected });
    await h.begin();
    assert.equal(h.calls.length, 0);
    assert.ok(h.logs.some(message => message.includes('已改变')));
});

test('an explicit gateway rejection never queries current state or resends begin', async () => {
    const h = harness({ invoke: async () => ({ payload: { error: { code: 'previous_batch_active', message: 'previous_batch_active' } } }) });
    await h.begin();
    assert.deepEqual(h.calls.map(call => call.envelope.name), ['control.batch.begin']);
    assert.ok(h.logs.some(message => message.includes('未受理') && message.includes('已有联机批次')));
    assert.ok(h.logs.every(message => !message.includes('已受理')));
});

for (const recovered of [true, false]) {
    test('transport uncertainty queries the original request once without resending; recovered=' + recovered, async () => {
        let originalRequest;
        const h = harness({ invoke: async (_method, envelope) => {
            if (envelope.name === 'control.batch.begin') {
                originalRequest = envelope.payload.requestId;
                throw new Error('connection lost after send');
            }
            assert.equal(envelope.name, 'control.batch.current');
            return { payload: recovered
                ? { requestId: originalRequest, batchId: 'accepted-original', phase: 'preparing' }
                : { requestId: 'another-request', batchId: 'another-batch', phase: 'running' } };
        } });
        await h.begin();
        assert.deepEqual(h.calls.map(call => call.envelope.name), ['control.batch.begin', 'control.batch.current']);
        assert.ok(h.calls.every(call => call.method === 'Dispatch'));
        if (recovered) assert.ok(h.logs.some(message => message.includes('按原请求确认') && message.includes('accepted-original')));
        else {
            assert.ok(h.logs.some(message => message.includes('待确认') && message.includes('未重复发送')));
            assert.ok(h.logs.every(message => !message.includes('按原请求确认')));
        }
    });
}

test('quick binding parser accepts only complete exact group or onedragon prefixes', () => {
    const { context } = harness();
    const parse = value => { context.__value = value; return vm.runInContext('parseQuickCommandBinding(__value)', context); };
    assert.deepEqual(clone(parse('GROUP:传奇任务')), { name: '传奇任务', isOneClick: false });
    assert.deepEqual(clone(parse('ONEDRAGON:收尾一条龙')), { name: '收尾一条龙', isOneClick: true });
    for (const invalid of [null, undefined, {}, '', 'GROUP:', 'GROUP: ', 'GROUP: 任务', 'ONEDRAGON:任务 ', 'group:任务', '未知:任务', 'GROUP:' + '长'.repeat(1025)])
        assert.equal(parse(invalid), null);
    assert.equal(parse('GROUP:' + '长'.repeat(1024)).name.length, 1024);
});

test('completion validation requires a supported action and complete task identity', () => {
    const { context } = harness();
    const valid = action => { context.__action = action; return Boolean(vm.runInContext('isValidCompletionAction(__action)', context)); };
    assert.equal(valid({ policy: 'resume', taskType: 'group', taskName: '' }), true);
    assert.equal(valid({ policy: 'stop', taskType: 'onedragon', taskName: '' }), true);
    assert.equal(valid({ policy: 'runSpecified', taskType: 'onedragon', taskName: '收尾' }), true);
    for (const invalid of [null, {}, { policy: 'runSpecified', taskType: 'group', taskName: ' ' },
        { policy: 'unknown', taskType: 'group', taskName: '' }, { policy: 'resume', taskType: 'unknown', taskName: '' },
        { policy: 'stop', taskType: 'group', taskName: null }, { policy: 'runSpecified', taskType: 'group', taskName: '长'.repeat(1025) }])
        assert.equal(valid(invalid), false);
});
