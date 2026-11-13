// Exercises the production completion editor and protocol helpers with a minimal DOM.
// No browser, server, real executor, BGI process, or user data is used.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const read = name => readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/' + name, import.meta.url), 'utf8');
const core = read('control-room.js'), commands = read('control-room-commands.js');
// Suppress only page bootstrap; editor functions, callbacks and validation are unchanged.
const ui = read('control-room-ui.js').replace(/\nrenderControlRoom\(\);\s*$/, '\n');
const copy = value => structuredClone(value);
const flush = () => new Promise(resolve => setImmediate(resolve));
const decode = value => value.replace(/&quot;|&#39;|&amp;|&lt;|&gt;/g, ch =>
    ({ '&quot;': '"', '&#39;': "'", '&amp;': '&', '&lt;': '<', '&gt;': '>' }[ch]));
const key = (type, name, id = '') => JSON.stringify([type, name, id]);
const names = { group: '配置 A', onedragon: '每日一条龙', workflow: '晚间计划' };
const flows = [{ workflowId: 'flow-1', name: names.workflow, revision: 'workflow-r1',
    nodes: [{ taskId: 'flow-node-1', name: '计划任务一', enabled: true }, { taskId: 'flow-node-2', name: '计划任务二', enabled: true }] }];
function target(type, name = names[type], id = type === 'workflow' ? 'flow-1' : '') {
    return type === 'workflow' ? { type, name, workflowId: id, revision: flows[0].revision, nodes: copy(flows[0].nodes) }
        : { type, name, workflowId: '', revision: type + ':' + name + '-r1', nodes: [1, 2].map(i =>
            ({ taskId: type + ':' + name + ':' + i, name: '任务' + i, enabled: true })) };
}
function action(type = 'group', mode = 'beginning', changes = {}) {
    const t = target(type);
    return { policy: 'runSpecified', taskType: type, taskName: t.name, priority: 7,
        startMode: mode, startTaskId: mode === 'node' ? t.nodes[1].taskId : '',
        startTaskName: mode === 'node' ? t.nodes[1].name : '', configRevision: mode === 'default' ? '' : t.revision,
        workflowId: t.workflowId, ...changes };
}
function wire(a) {
    return { policy: a.policy, specifiedType: a.taskType, specifiedName: a.taskName, specifiedPriority: String(a.priority),
        completionStartVersion: '1', specifiedStartMode: a.startMode, specifiedStartTaskId: a.startTaskId,
        specifiedStartTaskName: a.startTaskName, specifiedConfigRevision: a.configRevision, specifiedWorkflowId: a.workflowId };
}
function harness({ initial = action(), initialData, operation, catalogTransform } = {}) {
    const elements = new Map(), calls = [], remote = [], logs = [], timers = new Map();
    const selects = new Set(['completionPolicy', 'completionTaskType', 'completionTask', 'completionStartMode', 'completionStartTask']);
    let stored = initialData ? copy(initialData) : wire(initial), nextId = 0;
    const element = id => {
        if (elements.has(id)) return elements.get(id);
        const el = { id, isConnected: true, disabled: false, hidden: false, style: {}, textContent: '', options: [], _value: '', _html: '',
            addEventListener() {}, querySelector(selector) { return element(selector.slice(1)); },
            add(option) { this.options.push(option); },
            get value() { return this._value; },
            set value(value) { value = String(value); this._value = selects.has(id) && !this.options.some(o => o.value === value) ? '' : value; },
            get innerHTML() { return this._html; },
            set innerHTML(value) {
                this._html = value;
                if (id === 'completionSettings') {
                    for (const match of value.matchAll(/<select\b[^>]*id="([^"]+)"[^>]*>([\s\S]*?)<\/select>/g)) element(match[1]).innerHTML = match[2];
                } else if (selects.has(id)) {
                    this.options = [...value.matchAll(/<option\b([^>]*)>([\s\S]*?)<\/option>/g)].map(match => ({
                        value: decode(/\bvalue="([^"]*)"/.exec(match[1])?.[1] || ''), text: decode(match[2]), disabled: /\bdisabled\b/.test(match[1])
                    }));
                    this._value = this.options[0]?.value || '';
                }
            }
        };
        elements.set(id, el); return el;
    };
    const member = { playerUid: 'u0', playerName: '成员零', online: true,
        configGroups: [names.group, '配置 B'], oneClickConfigs: [names.onedragon], hoeingCompletionAction: copy(initial) };
    const context = vm.createContext({
        document: { getElementById: element, querySelectorAll: () => [] },
        window: { addEventListener() {}, CSS: { supports: () => true } },
        localStorage: { getItem: () => null }, crypto: { randomUUID: () => 'test-' + ++nextId },
        Option: function(text, value) { this.text = text; this.value = value; this.disabled = false; },
        setTimeout(fn) { const id = ++nextId; timers.set(id, fn); return id; }, clearTimeout(id) { timers.delete(id); },
        __members: [member, { ...copy(member), playerUid: 'u1' }], __logs: logs,
        __sendRemote: async cmd => { remote.push(copy(cmd)); }
    });
    const evaluate = (expression, values = {}) => { Object.assign(context, values); return vm.runInContext(expression, context); };
    vm.runInContext(core, context, { filename: 'control-room.js' });
    vm.runInContext(commands, context, { filename: 'control-room-commands.js' });
    vm.runInContext(ui, context, { filename: 'control-room-ui.js' });
    evaluate('players = __members; roomReady = true; activeMemberUid = "u0"; detailSection = "settings"; log = message => __logs.push(message); globalThis.__trackedOperation = sendMemberOperation; sendRemoteCommand = __sendRemote;');
    const fallback = async (uid, name, params) => {
        if (name === 'task_policy.push') {
            stored = copy(params);
            return { status: 'success', message: '已保存', data: { ...copy(params), ok: 'true' } };
        }
        const type = params.catalogType ?? stored.specifiedType, nameOfTarget = params.catalogName ?? stored.specifiedName;
        const id = params.catalogWorkflowId ?? stored.specifiedWorkflowId ?? '';
        let catalog = { workflows: copy(flows), target: nameOfTarget ? target(type, nameOfTarget, id) : null, error: null };
        if (catalogTransform) catalog = catalogTransform(catalog, params);
        return { status: 'success', message: '已读取', data: { ...copy(stored), ok: 'true', catalog: JSON.stringify(catalog) } };
    };
    context.__operation = async (uid, name, params, expectedReply) => {
        calls.push({ uid, name, params: copy(params), expectedReply });
        return operation ? operation({ uid, name, params, expectedReply, fallback: () => fallback(uid, name, params) }) : fallback(uid, name, params);
    };
    evaluate('sendMemberOperation = __operation');
    return {
        calls, remote, logs, timers, member, context, evaluate, element,
        mount() { return evaluate('initializeCompletionPolicyEditor("u0", document.getElementById("completionSettings"))'); },
        change(id, value) { const el = element(id); el.value = value; return Promise.resolve(el.onchange?.()); },
        click(id) { const el = element(id); return el.disabled ? Promise.resolve() : Promise.resolve(el.onclick?.()); },
        pushes() { return calls.filter(call => call.name === 'task_policy.push'); },
        useTracked() { evaluate('sendMemberOperation = __trackedOperation'); },
        reply(params, command = remote.at(-1), uid = 'u0', name = 'task_policy.push_result') {
            evaluate('consumeMemberReply(__reply)', { __reply: { commandId: command.commandId, senderUid: uid, cmd: name, params } });
        }
    };
}

for (const type of ['group', 'onedragon', 'workflow']) for (const mode of ['beginning', 'node']) {
    test(type + ' editor saves ' + mode + ' using target-member catalog and confirms a full readback', async () => {
        const h = harness(); await h.mount();
        await h.change('completionTaskType', type);
        const t = target(type);
        await h.change('completionTask', key(type, t.name, t.workflowId));
        await h.change('completionStartMode', mode);
        if (mode === 'node') await h.change('completionStartTask', t.nodes[1].taskId);
        const result = await h.click('savePolicy');
        assert.equal(result.status, 'success');
        assert.deepEqual(h.pushes().map(call => call.params), [wire(action(type, mode))]);
        assert.ok(h.calls.every(call => call.uid === 'u0'));
        assert.deepEqual(h.calls.filter(call => call.params.catalogType).map(call => call.params),
            Array(2).fill({ catalogType: type, catalogName: t.name, catalogWorkflowId: t.workflowId }));
        assert.equal(h.calls.at(-1).name, 'task_policy.pull');
        assert.deepEqual(copy(h.member.hoeingCompletionAction), action(type, mode));
        assert.match(h.element('policyResult').textContent, /已保存并回读确认/);
    });
}

test('workflow options do not enter hoeing sequence or quick binding task options', async () => {
    const h = harness(); await h.mount(); await h.change('completionTaskType', 'workflow');
    assert.match(h.element('completionTask').innerHTML, /晚间计划/);
    assert.doesNotMatch(h.evaluate('taskOptions(currentMember("u0"))'), /晚间计划|workflow/);
});

test('legacy data remains original-entry and a versionless executor cannot receive any edited settings', async () => {
    const legacy = { policy: 'runSpecified', specifiedType: 'group', specifiedName: names.group, specifiedPriority: '7' };
    const h = harness({ initial: action('group', 'default'), initialData: legacy }); await h.mount();
    assert.equal(h.element('completionStartMode').value, 'default');
    assert.match(h.element('completionPreview').textContent, /沿用原入口/);
    assert.doesNotMatch(h.element('completionPreview').textContent, /从头开始/);
    assert.equal(h.element('savePolicy').disabled, true);
    await h.click('savePolicy'); assert.equal(h.pushes().length, 0);
    for (const policy of ['resume', 'stop', 'runSpecified']) {
        assert.throws(() => h.evaluate('completionPolicyParams(__action, undefined)', { __action: { ...action(), policy } }), /更新/);
    }
});

test('a modern executor preserves a legacy entry until the user chooses an explicit start', async () => {
    const h = harness({ initial: action('onedragon', 'default') }); await h.mount();
    assert.equal(h.element('completionStartMode').value, 'default');
    assert.equal((await h.click('savePolicy')).status, 'failed'); assert.equal(h.pushes().length, 0);
    await h.change('completionStartMode', 'beginning');
    assert.equal((await h.click('savePolicy')).status, 'success');
    assert.equal(h.pushes()[0].params.specifiedStartMode, 'beginning');
});

for (const type of ['group', 'onedragon', 'workflow']) {
    test(type + ' saved missing node stays selected and refuses instead of falling back to beginning', async () => {
        const h = harness({ initial: action(type, 'node', { startTaskId: 'removed-node', startTaskName: '原起点' }) }); await h.mount();
        assert.equal(h.element('completionStartMode').value, 'node');
        assert.equal(h.element('completionStartTask').value, 'removed-node');
        assert.match(h.element('completionStartHint').textContent, /失效|禁用/);
        assert.equal((await h.click('savePolicy')).status, 'failed'); assert.equal(h.pushes().length, 0);
        await h.change('completionStartTask', target(type).nodes[0].taskId);
        assert.equal((await h.click('savePolicy')).status, 'success');
        assert.equal(h.pushes()[0].params.specifiedStartTaskId, target(type).nodes[0].taskId);
    });
}

test('a disabled saved node is displayed as disabled and cannot be saved as a usable start', async () => {
    const h = harness({ initial: action('group', 'node'), catalogTransform: catalog => {
        catalog.target.nodes[1].enabled = false; return catalog;
    } }); await h.mount();
    assert.equal(h.element('completionStartTask').options.find(o => o.value === target('group').nodes[1].taskId).disabled, true);
    assert.equal((await h.click('savePolicy')).status, 'failed'); assert.equal(h.pushes().length, 0);
    assert.equal(h.element('completionStartMode').value, 'node');
});

test('a changed revision requires explicit confirmation of the current start', async () => {
    const h = harness({ initial: action('group', 'node', { configRevision: 'old-revision' }) }); await h.mount();
    assert.equal(h.element('confirmCompletionStart').hidden, false);
    assert.match(h.element('completionStartHint').textContent, /修订/);
    assert.equal((await h.click('savePolicy')).status, 'failed'); assert.equal(h.pushes().length, 0);
    await h.click('confirmCompletionStart');
    assert.equal((await h.click('savePolicy')).status, 'success');
    assert.equal(h.pushes()[0].params.specifiedConfigRevision, target('group').revision);
});

test('changing target or type clears old node identity and saves an explicit beginning', async () => {
    const h = harness({ initial: action('group', 'node') }); await h.mount();
    await h.change('completionTaskType', 'onedragon');
    assert.equal(h.element('completionTask').value, '');
    assert.equal(h.element('completionStartTask').value, '');
    assert.equal(h.element('completionStartMode').value, 'beginning');
    await h.change('completionTask', key('onedragon', names.onedragon));
    assert.equal((await h.click('savePolicy')).status, 'success');
    assert.equal(h.pushes()[0].params.specifiedStartTaskId, '');
    assert.equal(h.pushes()[0].params.specifiedStartTaskName, '');
    assert.equal(h.pushes()[0].params.specifiedWorkflowId, '');
});

test('late catalog results cannot replace a newer target; every query remains bound to the original member', async () => {
    let release;
    const h = harness({ operation: async request => request.name === 'task_policy.pull' && request.params.catalogName === names.group
        ? new Promise(resolve => { release = async () => resolve(await request.fallback()); }) : request.fallback() });
    await h.mount();
    const first = h.change('completionTask', key('group', names.group)); await flush();
    const second = h.change('completionTask', key('group', '配置 B'));
    await release(); await first; await second;
    assert.equal(h.element('completionTask').value, key('group', '配置 B'));
    assert.ok(h.element('completionStartTask').options.slice(1).every(o => o.value.includes('配置 B')));
    assert.ok(h.calls.every(call => call.uid === 'u0'));
    assert.deepEqual(h.calls.filter(call => call.params.catalogType).map(call => call.params.catalogName), [names.group, '配置 B']);
});

test('leaving member settings before its reply prevents late settings from filling another detail page', async () => {
    let release;
    const h = harness({ operation: request => new Promise(resolve => { release = async () => resolve(await request.fallback()); }) });
    const loading = h.mount(); await flush();
    h.evaluate('activeMemberUid = "u1"'); await release(); await loading;
    assert.equal(h.element('completionPolicy').disabled, true);
    assert.equal(h.pushes().length, 0);
    assert.ok(h.calls.every(call => call.uid === 'u0'));
});

test('losing completion version during the save preflight refuses before push', async () => {
    let pulls = 0;
    const h = harness({ operation: async request => {
        const result = await request.fallback();
        if (request.name === 'task_policy.pull' && ++pulls > 1) delete result.data.completionStartVersion;
        return result;
    } }); await h.mount();
    assert.equal((await h.click('savePolicy')).status, 'failed'); assert.equal(h.pushes().length, 0);
    assert.match(h.element('policyResult').textContent, /版本/);
});

test('a concurrent PC policy change refuses before overwriting the saved action', async () => {
    let pulls = 0;
    const h = harness({ operation: async request => {
        const result = await request.fallback();
        if (request.name === 'task_policy.pull' && ++pulls > 1) result.data.policy = 'stop';
        return result;
    } }); await h.mount();
    assert.equal((await h.click('savePolicy')).status, 'failed'); assert.equal(h.pushes().length, 0);
    assert.match(h.element('policyResult').textContent, /其他端改变/);
    assert.equal(h.element('savePolicy').disabled, true);
});

for (const failure of ['version', 'echo', 'readback']) {
    test('save with missing ' + failure + ' confirmation stays unknown and requires reload', async () => {
        let pulls = 0;
        const h = harness({ operation: async request => {
            const result = await request.fallback();
            if (request.name === 'task_policy.push') {
                if (failure === 'version') delete result.data.completionStartVersion;
                if (failure === 'echo') result.data.specifiedStartTaskId = 'unexpected-node';
            } else if (request.name === 'task_policy.pull' && ++pulls === 3 && failure === 'readback') result.data.policy = 'stop';
            return result;
        } }); await h.mount();
        await h.change('completionStartMode', 'node'); await h.change('completionStartTask', target('group').nodes[1].taskId);
        const result = await h.click('savePolicy');
        assert.equal(result.status, 'unknown');
        assert.equal(h.member.hoeingCompletionAction.startMode, 'beginning');
        assert.equal(h.evaluate('memberCommandResults.get("u0").status'), 'unknown');
        assert.equal(h.element('savePolicy').disabled, true);
        assert.match(h.element('policyResult').textContent, /未确认/);
        assert.doesNotMatch(h.element('policyResult').textContent, /已保存并回读确认/);
    });
}

test('version 1 policy replies must include every start field and catalog accepts an explicit null error', () => {
    const h = harness();
    for (const missing of ['specifiedStartMode', 'specifiedStartTaskId', 'specifiedStartTaskName', 'specifiedConfigRevision', 'specifiedWorkflowId']) {
        const data = wire(action()); delete data[missing];
        assert.throws(() => h.evaluate('normalizePolicy(__data)', { __data: data }), /字段不完整/);
    }
    const catalog = { workflows: flows, target: target('group'), error: null };
    assert.deepEqual(copy(h.evaluate('normalizeCompletionCatalog(__data)', {
        __data: { completionStartVersion: '1', catalog: JSON.stringify(catalog) }
    })), catalog);
});

test('tracked save ignores gateway success and another member then accepts only the exact versioned echo', async () => {
    const h = harness(); h.useTracked();
    const params = wire(action('workflow', 'node'));
    let finished = false;
    const pending = h.evaluate('sendMemberOperation("u0", "task_policy.push", __params, "task_policy.push_result")', { __params: params })
        .then(result => { finished = true; return result; });
    await flush();
    h.reply({ status: 'success' }, h.remote[0], 'u0', 'ack'); await flush(); assert.equal(finished, false);
    h.reply({ ...params, ok: 'true' }, h.remote[0], 'u1'); await flush(); assert.equal(finished, false);
    h.reply({ ...params, ok: 'true' });
    assert.equal((await pending).status, 'success'); assert.equal(h.timers.size, 0);
});

for (const failure of ['version', 'fields', 'node']) {
    test('tracked save refuses an ok reply with missing or mismatched ' + failure, async () => {
        const h = harness(); h.useTracked();
        const params = wire(action('group', 'node'));
        const pending = h.evaluate('sendMemberOperation("u0", "task_policy.push", __params, "task_policy.push_result")', { __params: params });
        await flush();
        const reply = { ...params, ok: 'true' };
        if (failure === 'version') delete reply.completionStartVersion;
        if (failure === 'fields') delete reply.specifiedConfigRevision;
        if (failure === 'node') reply.specifiedStartTaskId = 'wrong-node';
        h.reply(reply); assert.equal((await pending).status, 'unknown'); assert.equal(h.timers.size, 0);
    });
}

test('every explicit start identity field participates in the confirmation signature', () => {
    const h = harness({ initial: action('workflow', 'node') });
    const before = h.evaluate('startSettingsSignature(currentMember("u0"), "")');
    for (const field of ['startMode', 'startTaskId', 'startTaskName', 'configRevision', 'workflowId']) {
        const changed = { ...action('workflow', 'node'), [field]: 'changed' };
        const signature = h.evaluate('startSettingsSignature({ onlineHoeingGroupNames: [], hoeingCompletionAction: __action }, "")', { __action: changed });
        assert.notEqual(signature, before, field);
    }
});
