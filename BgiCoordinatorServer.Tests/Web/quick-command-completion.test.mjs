// Protocol and confirmation-snapshot checks against the production web script.
// No browser, DOM rendering, real gateway, executor, or user data is used here.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const read = name => readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/' + name, import.meta.url), 'utf8');
const source = read('control-room.js'), commands = read('control-room-commands.js');
const ui = read('control-room-ui.js').replace(/\nrenderControlRoom\(\);\s*$/, '\n');
const quickKeys = ['一键传奇', '一键次数盾', '一键精英', '一键小怪', '一键自定义'];
const capabilities = ['hoeing.start.v2', 'hoeing.completionChoice.v1', 'hoeing.quickCommandCompletion.v1'];
const clone = value => structuredClone(value);

function members() {
    return ['u0', 'u1', 'u2'].map((uid, i) => ({
        playerUid: uid, playerName: '成员' + i, online: true,
        quickCommands: Object.fromEntries(quickKeys.map(key => [key, (i % 2 ? 'ONEDRAGON:' : 'GROUP:') + key + i])),
        onlineHoeingGroupNames: ['联机' + i], onlineHoeingGroupTypes: ['group'],
        configGroups: ['联机' + i, ...(i % 2 ? [] : quickKeys.map(key => key + i))],
        oneClickConfigs: i % 2 ? quickKeys.map(key => key + i) : [],
        hoeingCompletionAction: { policy: i === 0 ? 'resume' : 'runSpecified', taskType: i === 2 ? 'onedragon' : 'group',
            taskName: i === 0 ? '' : '收尾' + i, priority: i }
    }));
}
function switchAction(runAfterSwitch = false, changes = {}) {
    return { policy: 'switchRoom', taskType: runAfterSwitch ? 'workflow' : 'group', taskName: runAfterSwitch ? '准备计划' : '',
        priority: runAfterSwitch ? 7 : 0, startMode: runAfterSwitch ? 'node' : 'default', startTaskId: runAfterSwitch ? 'stable-task' : '',
        startTaskName: runAfterSwitch ? '上线准备' : '', configRevision: runAfterSwitch ? 'task-revision' : '', workflowId: runAfterSwitch ? 'flow-1' : '',
        targetName: '乙房', targetRoomCode: 'ROOM-B', runAfterSwitch, actionRevision: 'v1:' + 'A'.repeat(64), ...changes };
}
function confirmedAction(action) {
    return { startMode: 'default', startTaskId: '', startTaskName: '', configRevision: '', workflowId: '',
        targetName: '', targetRoomCode: '', runAfterSwitch: false, actionRevision: '', ...action };
}

// Only the DOM features consumed by the three confirmation dialogs. The actual handlers run unmodified.
function makeElement() {
    const selectors = new Map(), listeners = new Map();
    return { style: {}, dataset: {}, value: '', innerHTML: '', textContent: '', checked: false, disabled: false, hidden: false, children: [],
        addEventListener(type, handler) { listeners.set(type, handler); },
        dispatch(type) { return (this['on' + type] || listeners.get(type))?.({ currentTarget: this }); },
        appendChild(child) { this.children.push(child); },
        remove() { this.removed = true; }, focus() {},
        querySelector(selector) {
            if (!selectors.has(selector)) {
                const child = makeElement();
                const marker = selector.startsWith('#') ? 'id="' + selector.slice(1) + '"' : selector === '[data-close]' ? 'data-close' : '';
                const tag = marker ? this.innerHTML.match(new RegExp('<[^>]*' + marker + '[^>]*>'))?.[0] || '' : '';
                child.disabled = /\sdisabled(?:\s|>)/.test(tag);
                child.hidden = /\shidden(?:\s|>)/.test(tag);
                selectors.set(selector, child);
            }
            return selectors.get(selector);
        },
        querySelectorAll(selector) { return selector === '[data-close]' && this.innerHTML.includes('data-close') ? [this.querySelector(selector)] : []; }
    };
}
function harness({ current = members(), selected = clone(current.slice(0, 2)), caps = capabilities, invoke, withUi = false, legacyBindings = {} } = {}) {
    const calls = [], logs = [], elements = new Map(), overlays = [], legacyStarts = [];
    let requestNumber = 0;
    const context = vm.createContext({
        document: { getElementById(id) {
            if (!elements.has(id)) elements.set(id, makeElement());
            return elements.get(id);
        }, createElement: makeElement, querySelectorAll: () => [], body: { appendChild(overlay) { overlays.push(overlay); } } },
        window: { addEventListener() {}, CSS: { supports: () => true } },
        localStorage: { getItem(key) { return key === 'web_hie_binding' ? JSON.stringify(legacyBindings) : null; } },
        crypto: { randomUUID() { return 'test-request-' + ++requestNumber; } },
        batchPhaseName: phase => phase,
        __current: current, __selected: selected, __caps: clone(caps), __logs: logs, __legacyStarts: legacyStarts,
        __invoke: async (method, envelope) => {
            calls.push({ method, envelope: clone(envelope) });
            if (invoke) return invoke(method, envelope, calls);
            return { payload: { batchId: 'batch-test', protocol: 'hoeing.start.v2', phase: 'registering' } };
        }
    });
    vm.runInContext(source, context, { filename: 'control-room.js' });
    if (withUi) {
        vm.runInContext(commands, context, { filename: 'control-room-commands.js' });
        vm.runInContext(ui, context, { filename: 'control-room-ui.js' });
        // Do not render the unrelated home/status views after an accepted begin or run a legacy task.
        vm.runInContext('roomReady = true; for (const p of __selected) selectedMemberUids.add(p.playerUid); onControlBatchState = () => {}; showTaskListSelect = (...args) => __legacyStarts.push(args);', context);
    }
    vm.runInContext('players = __current; serverCapabilities = __caps; connection = { invoke: __invoke }; log = message => __logs.push(message);', context);
    return {
        calls, logs, context, current, selected, legacyStarts,
        open(expression) { vm.runInContext(expression, context); return overlays.at(-1); },
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
        assert.deepEqual(envelope.payload.confirmedCompletionActions, Object.fromEntries(h.selected.map(p => [p.playerUid, confirmedAction(p.hoeingCompletionAction)])));
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
    assert.ok(!Object.hasOwn(h.calls[0].envelope.payload, 'confirmedCompletionActions'));
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

for (const quickKey of [quickKeys[0], '']) {
    for (const changedField of ['startTaskId', 'startTaskName', 'configRevision', 'workflowId']) {
        test('a changed completion ' + changedField + ' requires confirmation again; quick=' + Boolean(quickKey), async () => {
            const current = members();
            current[1].hoeingCompletionAction = { policy: 'runSpecified', taskType: 'workflow', taskName: '收尾计划', priority: 1,
                startMode: 'node', startTaskId: 'stable-task', startTaskName: '任务二', configRevision: 'revision-1', workflowId: 'flow-1' };
            const selected = clone(current.slice(0, 2));
            current[1].hoeingCompletionAction[changedField] = 'changed';
            const h = harness({ current, selected });
            await h.begin(quickKey);
            assert.equal(h.calls.length, 0);
            assert.ok(h.logs.some(message => message.includes('已改变')));
        });
    }
}

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

test('switch summaries require a stable destination and revision; successors require an explicit confirmed start', () => {
    const { context } = harness();
    const valid = action => { context.__action = action; return Boolean(vm.runInContext('isValidCompletionAction(__action)', context)); };
    const describe = action => { context.__action = action; return vm.runInContext('hoeingCompletionDescription(__action)', context); };
    assert.equal(valid(switchAction()), true);
    assert.match(describe(switchAction()), /切换助手房间「乙房 · ROOM-B」，切换成功后待命/);
    assert.equal(valid(switchAction(true)), true);
    assert.match(describe(switchAction(true)), /切换成功后执行计划表「准备计划」.*上线准备.*优先级 7/);
    assert.equal(valid(switchAction(true, { taskType: 'group', workflowId: '', startMode: 'beginning', startTaskId: '', startTaskName: '' })), true);
    for (const changes of [{ targetName: '' }, { targetRoomCode: ' ' }, { targetRoomCode: 'X'.repeat(1025) },
        { actionRevision: '' }, { actionRevision: 'v1:' + 'G'.repeat(64) }, { actionRevision: 'v1:' + 'A'.repeat(64) + '\n' },
        { runAfterSwitch: 'true' }, { runAfterSwitch: undefined }, { priority: 2147483648 }, { startMode: 'default' },
        { workflowId: '' }, { configRevision: '' }, { startTaskName: '' }, { taskName: ' ' }]) {
        assert.equal(valid(switchAction(true, changes)), false, JSON.stringify(changes));
        assert.match(describe(switchAction(true, changes)), /配置无效/);
    }
});

for (const quickKey of ['', quickKeys[0]]) {
    test('complete checked snapshot sends each selected switch and ordinary action, not private fields; quick=' + Boolean(quickKey), async () => {
        const current = members(), roomAction = switchAction(true);
        current[0].hoeingCompletionAction = { ...roomAction, unrelatedField: 'must not enter the confirmation' };
        const h = harness({ current });
        await h.begin(quickKey);
        assert.equal(h.calls.length, 1);
        const payload = h.calls[0].envelope.payload;
        assert.deepEqual(payload.targetUids, ['u0', 'u1']);
        assert.deepEqual(payload.confirmedCompletionActions, { u0: roomAction, u1: confirmedAction(h.selected[1].hoeingCompletionAction) });
    });
    test('unchecked start does not require a valid or unchanged switch; quick=' + Boolean(quickKey), async () => {
        const current = members(), selected = clone(current.slice(0, 2));
        current[0].hoeingCompletionAction = switchAction(false, { targetRoomCode: '', actionRevision: '' });
        const h = harness({ current, selected, caps: capabilities.filter(cap => cap !== 'hoeing.completionChoice.v1') });
        await h.begin(quickKey, false);
        assert.equal(h.calls.length, 1);
        assert.equal(h.calls[0].envelope.payload.useCompletionActions, false);
        assert.ok(!Object.hasOwn(h.calls[0].envelope.payload, 'confirmedCompletionActions'));
    });
    test('unconfirmed switch transition in either direction is rejected; quick=' + Boolean(quickKey), async () => {
        for (const fromSwitch of [false, true]) {
            const current = members();
            if (fromSwitch) current[0].hoeingCompletionAction = switchAction();
            const selected = clone(current.slice(0, 2));
            current[0].hoeingCompletionAction = fromSwitch ? { policy: 'stop', taskType: 'group', taskName: '', priority: 0 } : switchAction();
            const h = harness({ current, selected }); await h.begin(quickKey);
            assert.equal(h.calls.length, 0);
            assert.ok(h.logs.some(message => message.includes('已改变')));
        }
    });
    test('every switch destination and successor identity change requires reconfirmation; quick=' + Boolean(quickKey), async () => {
        for (const changes of [{ targetRoomCode: 'ROOM-C' }, { actionRevision: 'v1:' + 'B'.repeat(64) }, { runAfterSwitch: false },
            { taskType: 'onedragon', workflowId: '' }, { taskName: '新收尾' }, { priority: 8 }, { startMode: 'beginning', startTaskId: '', startTaskName: '' },
            { startTaskId: 'new-id' }, { startTaskName: '新起点' }, { configRevision: 'new-revision' }, { workflowId: 'new-flow' }]) {
            const current = members(); current[0].hoeingCompletionAction = switchAction(true);
            const selected = clone(current.slice(0, 2)); Object.assign(current[0].hoeingCompletionAction, changes);
            const h = harness({ current, selected }); await h.begin(quickKey);
            assert.equal(h.calls.length, 0, JSON.stringify(changes));
            assert.ok(h.logs.some(message => message.includes('已改变')));
        }
    });
}

test('a changed ordinary hoeing binding is still refused when completion actions are unchecked', async () => {
    const current = members(), selected = clone(current.slice(0, 2));
    current[0].onlineHoeingGroupNames[0] = '另一项联机';
    const h = harness({ current, selected }); await h.begin('', false);
    assert.equal(h.calls.length, 0);
    assert.ok(h.logs.some(message => message.includes('已改变')));
});

const confirmationEntries = [
    { name: 'main hoeing', open: 'showCoordinatedConfirm("一键锄地")', option: '#completionActions', hint: '#completionActionsHint', confirm: '#startBatch', quick: false },
    { name: 'main quick', open: 'showCoordinatedConfirm("传奇")', option: '#completionActions', hint: '#completionActionsHint', confirm: '#startBatch', quick: true },
    { name: 'legacy hoeing', open: 'showQuickHoeingConfirmModal()', option: '#hoeingCompletionActions', hint: '#hoeingCompletionHint', list: '#hoeingMembers', confirm: '#hoeingOk', quick: false },
    { name: 'legacy quick', open: 'showQuickCommandConfirm("传奇")', option: '#quickCompletionActions', hint: '#quickCompletionHint', list: '#quickMembers', confirm: '#confOk', quick: true }
];
for (const entry of confirmationEntries) {
    test(entry.name + ' displays switches, freezes the selected summaries and sends only once even after a display-name rename', async () => {
        const current = members();
        current[0].hoeingCompletionAction = switchAction();
        current[1].hoeingCompletionAction = switchAction(true, { targetName: '丙房', targetRoomCode: 'ROOM-C' });
        const h = harness({ current, withUi: true, legacyBindings: { '传奇': 'GROUP:网页配置' } });
        const modal = h.open(entry.open), option = modal.querySelector(entry.option);
        assert.equal(option.disabled, false); assert.equal(option.checked, false);
        option.checked = true; option.dispatch('change');
        assert.match(modal.querySelector(entry.hint).textContent, /实际联机锄地正常完成.*结清/);
        if (entry.quick) assert.match(modal.querySelector(entry.hint).textContent, /纯普通任务完成时仅停止/);
        const shown = entry.list ? modal.querySelector(entry.list).children.map(row => row.textContent).join('\n') : modal.innerHTML;
        assert.match(shown, /乙房 · ROOM-B.*切换成功后待命/);
        assert.match(shown, /丙房 · ROOM-C.*准备计划.*上线准备/);
        current[0].hoeingCompletionAction.targetName = '乙房新名称';
        const button = modal.querySelector(entry.confirm);
        await Promise.all([button.dispatch('click'), button.dispatch('click')]);
        assert.equal(h.calls.length, 1);
        const payload = h.calls[0].envelope.payload;
        assert.equal(payload.useCompletionActions, true);
        assert.equal(payload.quickCommandKey, entry.quick ? quickKeys[0] : undefined);
        assert.deepEqual(payload.targetUids, ['u0', 'u1']);
        assert.deepEqual(payload.confirmedCompletionActions, { u0: h.selected[0].hoeingCompletionAction, u1: h.selected[1].hoeingCompletionAction });
        assert.equal(h.legacyStarts.length, 0);
    });
    test(entry.name + ' rejects a live in-place switch revision change after the dialog is shown', async () => {
        const current = members(); current[0].hoeingCompletionAction = switchAction(true);
        const h = harness({ current, withUi: true });
        const modal = h.open(entry.open), option = modal.querySelector(entry.option);
        option.checked = true; option.dispatch('change');
        current[0].hoeingCompletionAction.actionRevision = 'v1:' + 'C'.repeat(64);
        await modal.querySelector(entry.confirm).dispatch('click');
        assert.equal(h.calls.length, 0);
        assert.equal(h.legacyStarts.length, 0);
        assert.ok(h.logs.some(message => message.includes('已改变')));
    });
}
for (const entry of confirmationEntries.filter(entry => !entry.quick || entry.name === 'main quick')) {
    test(entry.name + ' keeps unchecked start available for an invalid switch and does not send a confirmation map', async () => {
        const current = members(); current[0].hoeingCompletionAction = switchAction(false, { actionRevision: '' });
        const h = harness({ current, withUi: true });
        const modal = h.open(entry.open), option = modal.querySelector(entry.option);
        assert.equal(option.disabled, true);
        current[0].hoeingCompletionAction = switchAction(true); // not participating in this unchecked choice
        await modal.querySelector(entry.confirm).dispatch('click');
        assert.equal(h.calls.length, 1);
        const payload = h.calls[0].envelope.payload;
        assert.equal(payload.useCompletionActions, false);
        assert.ok(!Object.hasOwn(payload, 'confirmedCompletionActions'));
    });
}

test('legacy quick unchecked still opens the existing ordinary task dialog, including when a switch is invalid', async () => {
    const current = members(); current[0].hoeingCompletionAction = switchAction(false, { actionRevision: '' });
    const h = harness({ current, withUi: true, legacyBindings: { '传奇': 'GROUP:网页配置' } });
    const modal = h.open('showQuickCommandConfirm("传奇")');
    assert.equal(modal.querySelector('#quickCompletionActions').disabled, true);
    const button = modal.querySelector('#confOk');
    await Promise.all([button.dispatch('click'), button.dispatch('click')]);
    assert.equal(h.calls.length, 0);
    assert.equal(h.legacyStarts.length, 1);
    assert.deepEqual(clone(h.legacyStarts[0]).slice(0, 6), ['*', '已选成员', '网页配置', false, null, ['u0', 'u1']]);
});

test('legacy checked quick includes the displayed bound members only, not an unbound member with a switch', async () => {
    const current = members(); current[0].hoeingCompletionAction = switchAction();
    current[1].hoeingCompletionAction = switchAction(true, { actionRevision: '' });
    delete current[1].quickCommands[quickKeys[0]];
    const h = harness({ current, withUi: true });
    const modal = h.open('showQuickCommandConfirm("传奇")'), option = modal.querySelector('#quickCompletionActions');
    assert.equal(option.disabled, false);
    assert.match(modal.querySelector('#quickMembers').children[1].textContent, /未绑定，本次不下发/);
    option.checked = true; option.dispatch('change');
    await modal.querySelector('#confOk').dispatch('click');
    assert.equal(h.calls.length, 1);
    const payload = h.calls[0].envelope.payload;
    assert.deepEqual(payload.targetUids, ['u0']);
    assert.deepEqual(payload.confirmedCompletionActions, { u0: h.selected[0].hoeingCompletionAction });
});

test('a gateway switch confirmation rejection is explicit, not transport uncertainty', async () => {
    const current = members(); current[0].hoeingCompletionAction = switchAction();
    const h = harness({ current, invoke: async () => ({ payload: { error: { code: 'bad_request', message: '成员动作未经确认，请重新确认：hoeing_completion_action_changed:u0' } } }) });
    const result = await h.begin('', true);
    assert.equal(result.status, 'failed');
    assert.deepEqual(h.calls.map(call => call.envelope.name), ['control.batch.begin']);
    assert.match(result.message, /未受理.*重新确认/);
});

test('a switch begin with unknown transport outcome only queries the original request, never refreshes and resends it', async () => {
    const current = members(); current[0].hoeingCompletionAction = switchAction();
    const h = harness({ current, invoke: async (_method, envelope) => {
        if (envelope.name === 'control.batch.begin') {
            current[0].hoeingCompletionAction.actionRevision = 'v1:' + 'B'.repeat(64);
            throw new Error('connection lost');
        }
        return { payload: { requestId: 'another-request', batchId: 'other-batch' } };
    } });
    const result = await h.begin('', true);
    assert.equal(result.status, 'unknown');
    assert.deepEqual(h.calls.map(call => call.envelope.name), ['control.batch.begin', 'control.batch.current']);
    assert.deepEqual(h.calls[0].envelope.payload.confirmedCompletionActions.u0, h.selected[0].hoeingCompletionAction);
    assert.match(result.message, /待确认.*未重复发送/);
});
