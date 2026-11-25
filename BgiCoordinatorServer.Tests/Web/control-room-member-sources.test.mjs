// Browser member integration through actual cards, dialogs, editor and room adapter.
// All connections, identities, snapshots and receipts are in-memory; no real gateway or executor.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { dom, flush, plain } from './desktop-control-harness.mjs';

const source = name => readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/' + name, import.meta.url), 'utf8');
const A = 'https://room-a.invalid', B = 'https://room-b.invalid', CODE = 'ROOM01';
const capabilities = ['hoeing.start.v2', 'hoeing.completionChoice.v1', 'hoeing.quickCommandCompletion.v1'];
function member(uid, prefix, online = true) {
    return Object.freeze({ playerUid: uid, playerName: prefix + '-member', clientInstanceId: prefix + '-instance',
        machineId: prefix + '-machine', connectionId: prefix + '-connection', online, bgiStatus: 'running', bgiVersion: prefix + '-version',
        configGroups: [prefix + '-group', prefix + '-other'], oneClickConfigs: [prefix + '-one'],
        configGroupTasks: { [prefix + '-group']: ['first', 'second'] },
        configGroupTasksWithStatus: { [prefix + '-group']: [{ status: 'Enabled' }, { status: 'Enabled' }] },
        oneClickTasks: { [prefix + '-one']: ['one first', 'one second'] },
        oneClickTasksWithStatus: { [prefix + '-one']: [{ taskId: prefix + '-uid-1', enabled: true }, { taskId: prefix + '-uid-2', enabled: true }] },
        hotkeys: [{ functionName: prefix + '-hotkey', configName: prefix + '-key', hotkeyText: 'F1' }],
        onlineHoeingGroupNames: [prefix + '-group'], onlineHoeingGroupTypes: ['group'], scheduledOnlineTime: '07:30',
        hoeingCompletionAction: { policy: 'resume', taskType: 'group', taskName: '', priority: 0 },
        quickCommands: { '一键传奇': 'GROUP:' + prefix + '-group' }, hoeingBatchId: prefix + '-batch',
        onlineHistory: [{ mode: 'scheduled', onlineTime: prefix + '-time' }] });
}
const policyData = (policy = 'resume') => ({ policy, specifiedType: 'group', specifiedName: '', specifiedPriority: '0',
    completionStartVersion: '1', specifiedStartMode: 'default', specifiedStartTaskId: '', specifiedStartTaskName: '',
    specifiedConfigRevision: '', specifiedWorkflowId: '', ok: 'true', catalog: JSON.stringify({ workflows: [], target: null, error: null }) });

// Extend the shared DOM primitives only for the selectors/events used by these production forms.
function controlDom() {
    const ui = dom(), { document, listeners } = ui, Element = document.createElement('div').constructor;
    const originalSet = Element.prototype.setAttribute, originalMatches = Element.prototype.matches;
    Element.prototype.setAttribute = function(name, value) { originalSet.call(this, name, value); if (name === 'hidden') this.hidden = true; };
    Element.prototype.matches = function(selector) { return selector.split(',').some(s => originalMatches.call(this, s.trim())); };
    Element.prototype.closest = function(selector) { for (let e = this; e; e = e.parentElement) if (e.matches(selector)) return e; return null; };
    Element.prototype.append = function(...children) { children.forEach(child => this.appendChild(child)); };
    Element.prototype.add = function(option) { this.appendChild(option); };
    Element.prototype.focus = function() {};
    Object.defineProperty(Element.prototype, 'isConnected', { get() {
        let node = this;
        while (node.parentElement?.children.includes(node)) node = node.parentElement;
        return node === document.body;
    } });
    document.querySelectorAll = selector => document.body.querySelectorAll(selector);
    const put = (id, parent = document.body, tag = 'div', attrs = {}) => {
        const el = new Element(tag, { ...(id ? { id } : {}), ...attrs }); parent.appendChild(el); return el;
    };
    const home = ui.element('homeView'), detail = ui.element('detailView');
    ui.element('gengdiProduct').classList.add('active'); detail.hidden = true;
    const quick = put('', home, 'section', { class: 'quick-section' });
    put('quickRow', quick).appendChild(new Element('button', { class: 'quick-btn', 'data-key': '传奇' }));
    for (const id of ['roomSummary', 'memberCount', 'memberList']) put(id, home);
    put('selectAll', home, 'button');
    for (const id of ['detailHeader', 'detailTabs', 'detailContent']) put(id, detail);
    for (const id of ['homeTab', 'detailTab', 'bgiUpdateNav']) put(id, ui.element('gengdiNav'), 'button');
    put('roomOwnerView').hidden = true;
    const click = el => {
        assert.ok(el, 'expected actual UI button');
        if (el.disabled) return Promise.resolve();
        const pending = [];
        // Production uses both delegated click handlers and element onclick handlers.
        for (let node = el; node; node = node.parentElement) {
            const event = { target: el, currentTarget: node };
            if (node.onclick) pending.push(node.onclick(event));
            for (const handler of listeners.filter(l => l.target === node && l.type === 'click')) pending.push(handler.callback(event));
        }
        return Promise.all(pending);
    };
    return { ...ui, put, quick, click, modal: () => document.body.querySelectorAll('.modal-overlay').at(-1),
        option: (text, value) => { const el = new Element('option', { value }); el.textContent = text; return el; } };
}

async function fixture(t, { mainRows = [member('same', 'a'), member('main-only', 'a2')],
    otherRows = [member('same', 'b'), member('other-only', 'b2')] } = {}) {
    const ui = controlDom(), logs = [], calls = [], timers = new Map(), connections = [], storage = new Map();
    let serial = 0, mainGeneration = 1, ownersOpened = 0, ownersClosed = 0, updatesOpened = [];
    const caps = [...capabilities];
    class Hub {
        constructor(url) { this.url = url; this.code = CODE; this.onEvent = null; this.onClosed = null; this.rows = otherRows; }
        on(name, handler) { assert.equal(name, 'evt'); this.onEvent = handler; }
        onclose(handler) { this.onClosed = handler; }
        start() { return Promise.resolve(); }
        stop() { return Promise.resolve(); }
        emit(name, payload, code = this.code) { this.onEvent?.({ name, roomCode: code, payload }); }
        drop() { this.onClosed?.(new Error('synthetic disconnect')); }
        invoke(channel, envelope) {
            const call = { hub: this, channel, name: envelope.name, payload: plain(envelope.payload) }; calls.push(call);
            const result = payload => ({ name: envelope.name, requestId: envelope.requestId, payload });
            if (envelope.name === 'session.hello') return Promise.resolve(result({ protocolVersion: 3, capabilities: caps }));
            if (envelope.name === 'control.joinRoom') {
                this.code = call.payload.roomCode; this.join = call.payload;
                return Promise.resolve(result({ success: true, playersUpdate: { full: true, revision: 1, players: this.rows } }));
            }
            if (envelope.name === 'control.batch.current') return this.holdCurrent ? this.holdCurrent() : Promise.resolve(result({ generation: 3, batchId: 'batch-' + this.url }));
            if (envelope.name === 'control.batch.reset') return Promise.resolve(result({ resetId: call.payload.resetId, generation: 3, batchId: 'batch-' + this.url }));
            if (envelope.name === 'control.batch.begin') return Promise.resolve(result({ protocol: 'hoeing.start.v2', batchId: 'batch-main', phase: 'preparing' }));
            return Promise.resolve(result({ ack: true }));
        }
    }
    class Builder {
        withUrl(url) { this.url = url; return this; }
        build() { const hub = new Hub(this.url); connections.push(hub); return hub; }
    }
    const mainHub = new Hub(A + '/gateway');
    const context = vm.createContext({ document: ui.document, URL, crypto: { randomUUID: () => 'fake-' + ++serial },
        CSS: { supports: () => true }, scrollTo() {}, addEventListener() {}, removeEventListener() {}, console,
        localStorage: { getItem: k => storage.get(k) ?? null, setItem: (k, v) => storage.set(k, v), removeItem: k => storage.delete(k) },
        setTimeout(fn, ms) { const id = ++serial; timers.set(id, { fn, ms }); return id; }, clearTimeout: id => timers.delete(id),
        location: { origin: A, protocol: 'https:', href: A + '/' }, signalR: { HubConnectionBuilder: Builder },
        Option: ui.option, __mainRows: mainRows, __mainHub: mainHub, __caps: caps, __logs: logs,
        mtCancelRoomPending() {}, webRoomOwner: { mount() { ownersOpened++; return { dispose() { ownersClosed++; }, refresh() {} }; } },
        bgiWebUpdate: { renderVersion(p) { return `${p.bgiVersion}<button data-ui="bgi-update" data-uid="${p.playerUid}" data-instance="${p.clientInstanceId}"></button>`; },
            refreshHints() {}, targetFor: p => !!p.clientInstanceId, open: p => updatesOpened.push(p.clientInstanceId) } });
    context.window = context;
    const evaluate = (expression, bindings = {}) => { Object.assign(context, bindings); return vm.runInContext(expression, context); };
    for (const file of ['control-room.js', 'control-room-commands.js', 'control-room-ui.js', 'control-room-profiles.js', 'control-room-integration.js'])
        vm.runInContext(source(file), context, { filename: file });
    evaluate(`players = __mainRows; connection = __mainHub; roomCode = 'ROOM01'; playerName = 'main';
        roomReady = true; serverCapabilities = __caps; log = m => __logs.push(m);`);
    const api = context.webRooms;
    const rawMain = () => ({ serverUrl: A, roomCode: CODE, nickname: 'main', ready: evaluate('roomReady'),
        connection: mainHub, players: evaluate('players'), senderUid: evaluate('webSessionUid'), capabilities: caps, generation: mainGeneration });
    api.init({ getMainContext: rawMain, defaultServerUrl: A,
        onChange: () => evaluate('renderControlRoom()'), onViewChange: view => evaluate('webRoomViewChanged(__view)', { __view: view }),
        onEvent: (key, env, room) => evaluate('webRoomSecondaryEvent(__key, __env, __room)', { __key: key, __env: env, __room: room }),
        onDisconnect: key => evaluate('webRoomSecondaryDisconnected(__key)', { __key: key }) });
    const mainKey = api.roomKey(A, CODE), otherKey = api.roomKey(B, CODE);
    api.saveProfile({ serverUrl: B, roomCode: CODE, name: '旁观乙房', password: 'fake-room-secret', remember: false });
    await flush();
    assert.ok(api.getContext(otherKey).ready, 'synthetic secondary joined');
    const otherHub = connections[0];
    const setView = async (mode, key) => { api.setView(mode, key); await flush(); };
    const dispatchReply = (hub, reply, kind) => {
        if (hub !== mainHub) hub.emit(kind, kind === 'control.remoteCommand' ? { command: reply } : reply);
        else if (kind === 'control.remoteCommand') evaluate('consumeMemberReply(__reply); consumeStateResetAck(__reply)', { __reply: reply });
        else evaluate('consumeMemberResult(__reply); consumeConfigApplied(__reply); consumeStateResetResult(__reply)', { __reply: reply });
    };
    const commands = hub => calls.filter(c => c.name === 'control.sendCommand' && (!hub || c.hub === hub)).map(c => ({ hub: c.hub, ...c.payload.command }));
    const reply = (cmd, params = { status: 'success', message: 'synthetic ack' }, name = 'ack', overrides = {}, hub = cmd.hub) => dispatchReply(hub, {
        roomCode: cmd.roomCode, commandId: cmd.commandId, senderUid: cmd.target[0], target: [cmd.senderUid], cmd: name, params, ...overrides
    }, 'control.remoteCommand');
    const applied = (cmd, overrides = {}, hub = cmd.hub) => dispatchReply(hub, { roomCode: cmd.roomCode, commandId: cmd.commandId,
        senderUid: cmd.senderUid, targetUid: cmd.target[0], cmd: cmd.cmd, status: 'success', configRevision: 'revision-b',
        targetProcessId: 42, targetStartTicksUtc: '123456789012345678', ...overrides }, 'control.remoteCommandResult');
    const card = uid => ui.element('memberList').querySelector(`[data-member-uid="${uid}"]`);
    const open = async uid => { await ui.click(card(uid).querySelector('[data-ui="detail"]')); };
    const section = async id => { await ui.click(ui.element('detailTabs').querySelector(`[data-section="${id}"]`)); };
    const click = selector => typeof selector === 'string'
        ? ui.click(ui.element(selector) || ui.modal()?.querySelector(selector.startsWith('#') ? selector : '#' + selector))
        : ui.click(selector);
    const last = () => commands().at(-1);
    t.after(() => api.dispose());
    return { ...ui, evaluate, api, mainKey, otherKey, mainHub, otherHub, calls, logs, commands, last, reply, applied, card, open, section, click, setView,
        source: (uid = 'same', key = otherKey) => api.sourceFor(uid, key),
        mainChanged(rows = mainRows) { mainGeneration++; evaluate('players = __rows', { __rows: rows }); api.mainChanged(); },
        otherChanged(rows) { otherHub.emit('control.playersUpdated', { full: true, revision: ++serial + 10, players: rows }); },
        ownerCounts: () => [ownersOpened, ownersClosed], updatesOpened };
}

test('aggregate cards use adapter dedupe and room labels while selection and bulk stay in main', async t => {
    const h = await fixture(t); await h.setView('all');
    assert.equal(h.document.querySelectorAll('.member-card').length, 3);
    assert.match(h.card('same').textContent, /a-member/);
    assert.equal(h.document.querySelectorAll('.wr-member-source').length, 3);
    assert.match(h.card('other-only').textContent, /旁观乙房/);
    assert.equal(h.card('other-only').querySelector('.member-select'), null);
    assert.ok(h.card('same').querySelector('.member-select'));
    assert.deepEqual(plain(h.evaluate('getSelectedMembers().map(p => p.playerUid)')), ['same', 'main-only']);
    assert.equal(h.quick.hidden, false); assert.equal(h.element('selectionBar').hidden, false);
    await h.setView('room', h.otherKey);
    assert.match(h.card('same').textContent, /b-member/);
    assert.equal(h.quick.hidden, true); assert.equal(h.element('selectAll').hidden, true);
    assert.equal(h.element('selectionBar').hidden, true); assert.equal(h.element('bgiUpdateNav').hidden, true);
    assert.equal(h.document.querySelectorAll('.member-select').length, 0);
    await h.open('same');
    assert.match(h.element('detailHeader').textContent, /b-member/);
    assert.match(h.element('detailHeader').textContent, /暂不支持更新 BGI/);
    assert.equal(h.element('detailHeader').querySelector('.detail-bgi-update'), null);
    assert.deepEqual(h.updatesOpened, []);
    assert.equal(h.evaluate('players[0].clientInstanceId'), 'a-instance');
    h.evaluate('showHome()'); await h.setView('current');
    assert.equal(h.element('detailTab').disabled, true, 'old detail source cannot be reopened by UID');
    assert.equal(h.element('memberList').querySelectorAll('.wr-member-source').length, 0, 'current card styling unchanged');
});

test('aggregate online secondary wins an offline main UID, but never inherits main selection', async t => {
    const h = await fixture(t, { mainRows: [member('same', 'a', false), member('main-only', 'a2')] });
    await h.setView('all');
    assert.match(h.card('same').textContent, /b-member/);
    assert.equal(h.card('same').querySelector('.member-select'), null);
    assert.deepEqual(plain(h.evaluate('getSelectedMembers().map(p => p.playerUid)')), ['main-only']);
    h.evaluate('toggleSelectedMembers()'); assert.equal(h.evaluate('selectedMemberUids.size'), 0);
    h.evaluate('toggleSelectedMembers()'); assert.deepEqual(plain(h.evaluate('[...selectedMemberUids]')), ['main-only']);
});

for (const op of ['start_bgi', 'stop', 'close_game', 'clear_online']) {
    test('single-card ' + op + ' uses secondary room even when an equal UID exists in main', async t => {
        const h = await fixture(t); await h.setView('room', h.otherKey); await h.open('same');
        await h.click(h.element('detailContent').querySelector(`[data-op="${op}"]`));
        const modal = h.modal(); assert.match(modal.textContent, /b-member.*旁观乙房/);
        if (op === 'stop') modal.querySelector('#stopCloseBgi').checked = true;
        const waiting = h.click(modal.querySelector('#operationConfirm')); await flush();
        const command = h.last();
        assert.equal(command.hub, h.otherHub); assert.equal(command.roomCode, CODE);
        assert.equal(command.senderUid, h.otherHub.join.playerUid); assert.equal(command.targetInstanceId, 'b-instance');
        assert.deepEqual(command.target, ['same']);
        if (op === 'stop') assert.deepEqual(command.params, { hoeingBatchId: 'b-batch', closeBgi: 'true' });
        h.reply(command, undefined, 'ack', { target: [h.evaluate('webSessionUid')] }, h.mainHub); await flush();
        assert.equal(h.evaluate('pendingMemberCommands.size'), 1);
        h.reply(command); await waiting;
        assert.equal(h.evaluate('pendingMemberCommands.size'), 0);
        assert.match(modal.querySelector('.form-result').textContent, /已确认/);
        assert.equal(h.commands(h.mainHub).length, 0);
        assert.equal(h.evaluate('memberCommandResults.has(memberSourceKey("same", __source))', { __source: h.source() }), true);
        assert.equal(h.evaluate('memberCommandResults.has(memberSourceKey("same", __source))', { __source: h.source('same', h.mainKey) }), false);
    });
}

test('a frozen stop confirmation refuses replacement or ambiguity, never routes to main by equal UID', async t => {
    const h = await fixture(t); await h.setView('room', h.otherKey); await h.open('same');
    await h.click(h.element('detailContent').querySelector('[data-op="stop"]'));
    const modal = h.modal(); h.otherChanged([member('same', 'b-new')]); await flush();
    await h.click(modal.querySelector('#operationConfirm'));
    assert.equal(h.commands().length, 0); assert.match(modal.querySelector('.form-result').textContent, /变化/);
    h.otherChanged([member('same', 'b-new'), member('same', 'b-duplicate')]); await flush();
    await h.setView('all'); await h.setView('room', h.otherKey); await h.open('same');
    assert.equal(h.element('detailContent').querySelector('[data-op="stop"]').disabled, true);
    await assert.rejects(h.evaluate('sendMemberOperation("same", "stop", {}, null, __source)', { __source: h.source() }), /多个/);
    assert.equal(h.commands().length, 0);
});

test('task catalog starts only its secondary executor after applied config receipt, with both epoch fields', async t => {
    const h = await fixture(t); await h.setView('room', h.otherKey); await h.open('same'); await h.section('tasks');
    const task = h.element('catalogItems').querySelector('button'); assert.match(task.textContent, /b-group/);
    await h.click(task); const modal = h.modal(); assert.match(modal.textContent, /b-group/);
    modal.querySelectorAll('.task-checkbox')[0].checked = false;
    await h.click(modal.querySelectorAll('.task-row')[1]);
    const waiting = h.click(modal.querySelector('#tskOk')); await flush();
    const change = h.last(); assert.equal(change.cmd, 'set_task_enabled'); assert.equal(change.hub, h.otherHub);
    assert.equal(change.params.groupName, 'b-group');
    h.applied(change, { senderUid: h.evaluate('webSessionUid') }, h.mainHub); await flush();
    assert.equal(h.commands().length, 1, 'main receipt cannot authorize secondary start');
    h.applied(change, { targetInstanceId: 'stale-instance' }); await flush(); assert.equal(h.commands().length, 1);
    h.applied(change); await waiting; await flush();
    const start = h.last(); assert.equal(start.cmd, 'start_group'); assert.equal(start.hub, h.otherHub);
    assert.equal(start.targetInstanceId, 'b-instance'); assert.equal(start.params.startFromIndex, 2);
    assert.equal(start.params.expectedConfigRevision, 'revision-b');
    assert.deepEqual(start.params.bgiEpoch, { processId: 42, startTicksUtc: '123456789012345678' });
    h.reply(start); await flush(); assert.equal(h.evaluate('pendingMemberCommands.size'), 0);
});

test('hotkey and one-dragon catalog use only the source member and stable task IDs', async t => {
    const h = await fixture(t); await h.setView('room', h.otherKey); await h.open('same'); await h.section('tasks');
    const host = h.element('detailContent'); await h.click(host.querySelector('[data-kind="hotkey"]'));
    const hotkey = h.element('catalogItems').querySelector('button'); assert.match(hotkey.textContent, /b-hotkey/);
    await h.click(hotkey); let modal = h.modal(), waiting = h.click(modal.querySelector('#operationConfirm')); await flush();
    assert.equal(h.last().cmd, 'hotkey_execute'); assert.deepEqual(h.last().params, { hotkeyConfigName: 'b-key' });
    h.reply(h.last()); await waiting; await h.click(modal.querySelector('[data-close]'));
    await h.click(host.querySelector('[data-kind="oneclick"]')); await h.click(h.element('catalogItems').querySelector('button'));
    modal = h.modal(); modal.querySelectorAll('.task-checkbox')[0].checked = false;
    await h.click(modal.querySelectorAll('.task-row')[1]); waiting = h.click(modal.querySelector('#tskOk')); await flush();
    assert.deepEqual(h.last().params, { configName: 'b-one', taskId: 'b-uid-1', enabled: false });
    h.applied(h.last()); await waiting; await flush();
    assert.equal(h.last().cmd, 'start_oneclick');
    assert.equal(h.last().params.configName, 'b-one'); assert.equal(h.last().params.startFromTaskId, 'b-uid-2');
    assert.equal(h.last().params.expectedConfigRevision, 'revision-b');
    assert.ok(h.commands().every(c => c.hub === h.otherHub && c.targetInstanceId === 'b-instance'));
    h.reply(h.last()); await flush();
});

for (const lost of ['disconnect', 'replace-after-receipt']) {
    test('task start refuses ' + lost + ' during configuration apply', async t => {
        const h = await fixture(t); await h.setView('room', h.otherKey); await h.open('same'); await h.section('tasks');
        await h.click(h.element('catalogItems').querySelector('button')); const modal = h.modal();
        modal.querySelectorAll('.task-checkbox')[0].checked = false;
        const waiting = h.click(modal.querySelector('#tskOk')); await flush();
        const change = h.last(); assert.equal(change.cmd, 'set_task_enabled');
        if (lost === 'disconnect') h.otherHub.drop();
        else { h.applied(change); h.otherChanged([member('same', 'replacement')]); }
        await waiting; await flush();
        assert.equal(h.commands().length, 1); assert.match(modal.querySelector('#tskResult').textContent, /未完成/);
        assert.equal(h.commands(h.mainHub).length, 0);
    });
}

test('binding, schedule, quick and completion editor read/write all remain source-bound without mutating room snapshots', async t => {
    const h = await fixture(t); await h.setView('room', h.otherKey); await h.open('same'); await h.section('settings'); await flush();
    const initialPull = h.last(); assert.equal(initialPull.cmd, 'task_policy.pull'); assert.equal(initialPull.hub, h.otherHub);
    h.reply(initialPull, policyData(), 'task_policy.data'); await flush();
    const host = h.element('detailContent');
    host.querySelector('#bindingChoice').value = JSON.stringify(['onedragon', 'b-one']); await h.click(host.querySelector('#addBinding'));
    let waiting = h.click(host.querySelector('#saveBindings')); await flush();
    assert.equal(h.last().cmd, 'bind_hoeing_group'); assert.deepEqual(h.last().params.groupNames, ['b-group', 'b-one']);
    h.reply(h.last()); await waiting;
    host.querySelector('#scheduledTime').value = '09:12'; waiting = h.click(host.querySelector('#saveSchedule')); await flush();
    assert.equal(h.last().cmd, 'set_scheduled_online_time'); h.reply(h.last()); await waiting;
    host.querySelector('[data-quick-index="0"]').value = JSON.stringify(['group', 'b-other']);
    waiting = h.click(host.querySelector('[data-save-quick="0"]')); await flush();
    assert.equal(h.last().cmd, 'set_quick_command'); h.reply(h.last()); await waiting;
    const policy = host.querySelector('#completionPolicy'); policy.value = 'stop'; policy.onchange();
    waiting = h.click(host.querySelector('#savePolicy')); await flush();
    assert.equal(h.last().cmd, 'task_policy.pull'); h.reply(h.last(), policyData(), 'task_policy.data'); await flush();
    assert.equal(h.last().cmd, 'task_policy.push'); assert.equal(h.last().params.policy, 'stop');
    h.reply(h.last(), { ...h.last().params, ok: 'true' }, 'task_policy.push_result'); await flush();
    assert.equal(h.last().cmd, 'task_policy.pull'); h.reply(h.last(), policyData('stop'), 'task_policy.data'); await waiting;
    assert.match(host.querySelector('#policyResult').textContent, /已保存并回读确认/);
    assert.ok(h.commands().every(c => c.hub === h.otherHub && c.targetInstanceId === 'b-instance'));
    const b = h.evaluate('currentMember("same", __source)', { __source: h.source() });
    assert.deepEqual(plain(b.onlineHoeingGroupNames), ['b-group', 'b-one']); assert.equal(b.scheduledOnlineTime, '09:12');
    assert.equal(b.quickCommands['一键传奇'], 'GROUP:b-other'); assert.equal(b.hoeingCompletionAction.policy, 'stop');
    assert.equal(h.api.getMember(h.source()).scheduledOnlineTime, '07:30', 'authoritative snapshot remains intact');
    assert.equal(h.evaluate('players[0].scheduledOnlineTime'), '07:30');
    assert.equal(h.evaluate('currentMember("same").hoeingCompletionAction.policy'), 'resume');
});

test('a late completion read from secondary never populates a new main editor for the same UID', async t => {
    const h = await fixture(t); await h.setView('room', h.otherKey); await h.open('same'); await h.section('settings'); await flush();
    const oldPull = h.last(); assert.equal(oldPull.hub, h.otherHub);
    await h.setView('current'); await h.open('same'); await h.section('settings'); await flush();
    const newPull = h.last(); assert.equal(newPull.hub, h.mainHub);
    h.reply(oldPull, policyData('stop'), 'task_policy.data'); await flush();
    const select = h.element('completionPolicy'); assert.equal(select.disabled, true);
    h.reply(newPull, policyData('resume'), 'task_policy.data'); await flush();
    assert.equal(select.disabled, false); assert.equal(select.value, 'resume');
    assert.equal(h.evaluate('currentMember("same").hoeingCompletionAction.policy'), 'resume');
});

test('history uses selected room; secondary reset uses only single-card current/reset/command with scoped result', async t => {
    const h = await fixture(t); await h.setView('room', h.otherKey); await h.open('same'); await h.section('history');
    assert.match(h.element('detailContent').textContent, /b-time/);
    await h.click(h.element('detailContent').querySelector('#clearHistory')); let modal = h.modal();
    await h.click(modal.querySelector('#clearConfirm')); assert.match(modal.querySelector('.form-result').textContent, /已清除/);
    const clear = h.calls.find(c => c.name === 'control.clearOnlineHistory');
    assert.equal(clear.hub, h.otherHub); assert.deepEqual(clear.payload, { targetUid: 'same' });
    await h.click(modal.querySelector('[data-close]')); await h.section('status');
    await assert.rejects(h.evaluate('resetMemberState("same", true, () => {}, null, __source)', { __source: h.source() }), /仅支持/);
    await h.click(h.element('detailContent').querySelector('#detailReset')); modal = h.modal();
    assert.equal(modal.querySelector('.reset-state-all').parentElement.hidden, true);
    const waiting = h.click(modal.querySelector('.reset-state-confirm')); await flush();
    assert.deepEqual(h.calls.filter(c => ['control.batch.current', 'control.batch.reset'].includes(c.name)).map(c => [c.hub, c.name]),
        [[h.otherHub, 'control.batch.current'], [h.otherHub, 'control.batch.reset']]);
    const reset = h.calls.find(c => c.name === 'control.batch.reset'); assert.deepEqual(reset.payload.targetUids, ['same']);
    const command = h.last(); assert.equal(command.cmd, 'reset_state'); assert.equal(command.hub, h.otherHub); assert.deepEqual(command.target, ['same']);
    h.reply(command, undefined, 'ack', { target: [h.evaluate('webSessionUid')] }, h.mainHub); await flush();
    assert.equal(h.evaluate('pendingStateResets.size'), 1);
    h.reply(command); await waiting;
    assert.match(modal.querySelector('.reset-state-result').textContent, /确认重置成功/);
});

test('offline history remains available in the selected ready room, and stale history cannot clear a replacement', async t => {
    const h = await fixture(t, { otherRows: [member('same', 'b', false)] });
    await h.setView('room', h.otherKey); await h.open('same'); await h.section('history');
    const history = h.element('detailContent').querySelector('#clearHistory'); assert.equal(history.disabled, false);
    await h.click(history); let modal = h.modal(); await h.click(modal.querySelector('#clearConfirm'));
    assert.equal(h.calls.filter(c => c.name === 'control.clearOnlineHistory').length, 1);
    await h.click(modal.querySelector('[data-close]'));
    await h.click(history); modal = h.modal(); h.otherChanged([member('same', 'replacement')]); await flush();
    await h.click(modal.querySelector('#clearConfirm'));
    assert.equal(h.calls.filter(c => c.name === 'control.clearOnlineHistory').length, 1);
    assert.match(modal.querySelector('.form-result').textContent, /变化/);
});

test('disconnect during secondary batch preflight never sends the reset to either room', async t => {
    const h = await fixture(t); h.otherHub.holdCurrent = () => new Promise(() => {});
    const waiting = h.evaluate('resetMemberState("same", false, () => {}, null, __source)', { __source: h.source() });
    await flush(); assert.equal(h.calls.filter(c => c.name === 'control.batch.current').length, 1);
    h.otherHub.drop(); await assert.rejects(waiting, /失效|变化|断开/);
    assert.equal(h.calls.filter(c => c.name === 'control.batch.reset').length, 0);
    assert.equal(h.commands().length, 0);
});

test('main aggregate reset retains the existing one main-room batch and per-member terminal confirmations', async t => {
    const h = await fixture(t); await h.setView('current'); await h.open('same');
    await h.click(h.element('detailContent').querySelector('#detailReset')); const modal = h.modal();
    modal.querySelector('.reset-state-all').checked = true;
    const waiting = h.click(modal.querySelector('.reset-state-confirm')); await flush();
    assert.equal(h.calls.find(c => c.name === 'control.batch.reset').hub, h.mainHub);
    const cmd = h.last(); assert.equal(cmd.hub, h.mainHub); assert.deepEqual(cmd.target, ['same', 'main-only']);
    h.reply(cmd); await flush(); assert.equal(h.evaluate('pendingStateResets.size'), 1);
    h.reply(cmd, undefined, 'ack', { senderUid: 'main-only' }); await waiting;
    assert.match(modal.querySelector('.reset-state-result').textContent, /所选成员均已确认重置成功/);
    assert.equal(h.commands(h.otherHub).length, 0);
});

test('disconnect cancels just that room pending commands; equal UID and equal command id stay independent', async t => {
    const h = await fixture(t);
    const start = key => h.evaluate(`(() => {const cmd = makeCmd('start_bgi', {}); cmd.target=['same']; cmd.commandId='same-id';
        return sendTrackedCommand(cmd, null, __source);})()`, { __source: h.source('same', key) });
    const a = start(h.mainKey), b = start(h.otherKey); await flush();
    assert.equal(h.evaluate('pendingMemberCommands.size'), 2);
    const main = h.commands(h.mainHub)[0];
    h.reply(main, undefined, 'ack', { senderInstanceId: 'different' }); await flush();
    assert.equal(h.evaluate('pendingMemberCommands.size'), 2);
    h.otherHub.drop(); assert.equal((await b).status, 'unknown');
    assert.equal(h.evaluate('pendingMemberCommands.size'), 1);
    h.reply(main); assert.equal((await a).status, 'success'); assert.equal(h.evaluate('pendingMemberCommands.size'), 0);
});

test('old main bulk confirmation cannot be sent after generation changes; home explicitly exits orchestration', async t => {
    const h = await fixture(t); await h.setView('all');
    h.evaluate('showCoordinatedConfirm("一键锄地")'); const modal = h.modal();
    h.mainChanged(); await flush(); await h.click(modal.querySelector('#startBatch'));
    assert.equal(h.calls.filter(c => c.name === 'control.batch.begin').length, 0);
    assert.match(modal.querySelector('.batch-feedback').textContent, /变化/);
    await h.setView('orchestration'); assert.equal(h.element('roomOwnerView').hidden, false);
    h.evaluate('showHome()'); await flush();
    assert.equal(h.api.getView().mode, 'current'); assert.equal(h.element('roomOwnerView').hidden, true);
    assert.equal(h.element('homeView').hidden, false); assert.deepEqual(h.ownerCounts(), [1, 1]);
});
