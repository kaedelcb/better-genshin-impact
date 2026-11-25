// Run the production member views and dialogs against synthetic room contexts.
// No network, live executor, BGI process, persisted user data, or browser test dependencies.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const scripts = ['control-room.js', 'control-room-commands.js', 'control-room-ui.js'].map(name => ({ name,
    code: readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/' + name, import.meta.url), 'utf8') }));
const copy = value => structuredClone(value);
const flush = () => new Promise(resolve => setImmediate(resolve));
const decode = text => text.replace(/&quot;|&#39;|&amp;|&lt;|&gt;/g, ch =>
    ({ '&quot;': '"', '&#39;': "'", '&amp;': '&', '&lt;': '<', '&gt;': '>' }[ch]));

// Small DOM sufficient for real template attributes, click delegation, form controls and modal removal.
class Element {
    constructor(tag = 'div', attributes = {}) {
        this.tag = tag; this.attrs = attributes; this.children = []; this.parent = null; this.style = {};
        this.dataset = Object.fromEntries(Object.entries(attributes).filter(([key]) => key.startsWith('data-'))
            .map(([key, value]) => [key.slice(5).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase()), value]));
        this.className = attributes.class || ''; this.id = attributes.id || ''; this._text = ''; this._html = '';
        this.hidden = Object.hasOwn(attributes, 'hidden'); this.disabled = Object.hasOwn(attributes, 'disabled');
        this.checked = Object.hasOwn(attributes, 'checked'); this._value = attributes.value;
        this.listeners = new Map();
        this.classList = {
            contains: name => this.className.split(/\s+/).includes(name),
            toggle: (name, force = !this.classList.contains(name)) => {
                const set = new Set(this.className.split(/\s+/).filter(Boolean));
                if (force) set.add(name); else set.delete(name);
                this.className = [...set].join(' '); return force;
            },
            add: name => this.classList.toggle(name, true), remove: name => this.classList.toggle(name, false)
        };
    }
    get isConnected() { return this.tag === 'body' || !!this.parent?.isConnected; }
    get options() { return this.querySelectorAll('option'); }
    get value() { return this._value ?? (this.tag === 'select' ? this.options[0]?.value || '' : ''); }
    set value(value) { const text = String(value); this._value = this.tag === 'select' && !this.options.some(o => o.value === text) ? '' : text; }
    get innerHTML() { return this._html; }
    set innerHTML(html) {
        this._html = String(html); this._text = ''; this._value = this.tag === 'select' ? undefined : this._value;
        for (const child of this.children) child.parent = null; this.children = [];
        const stack = [this];
        for (const token of this._html.matchAll(/<\/?[a-z][^>]*>|[^<]+/gi)) {
            const value = token[0], parent = stack.at(-1);
            if (value.startsWith('</')) { if (stack.length > 1) stack.pop(); continue; }
            if (!value.startsWith('<')) { parent._text += decode(value); continue; }
            const [, tag, rest] = /^<([a-z][\w-]*)([^>]*)>/i.exec(value) || [];
            if (!tag) continue;
            const attrs = Object.fromEntries([...rest.matchAll(/([\w:-]+)(?:="([^"]*)")?/g)].map(match => [match[1], decode(match[2] || '')]));
            const child = new Element(tag.toLowerCase(), attrs);
            parent.appendChild(child);
            if (!['input', 'img', 'br', 'hr'].includes(child.tag)) stack.push(child);
        }
    }
    get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
    set textContent(text) { this.innerHTML = ''; this._text = String(text ?? ''); }
    appendChild(child) { child.parent = this; this.children.push(child); return child; }
    append(child) { return this.appendChild(child); }
    add(option) { return this.appendChild(option); }
    remove() {
        if (this.parent) this.parent.children = this.parent.children.filter(child => child !== this);
        this.parent = null;
    }
    focus() {}
    getAttribute(key) { return this.attrs[key] ?? null; }
    matches(selector) {
        if (selector === 'button' || selector === 'input' || selector === 'select' || selector === 'option' || selector === 'h3') return this.tag === selector;
        if (selector.startsWith('#')) return this.id === selector.slice(1);
        if (selector.startsWith('.')) return this.classList.contains(selector.slice(1));
        const [, key, expected] = /^\[([\w-]+)(?:="([^\"]*)")?\]$/.exec(selector) || [];
        if (key) return Object.hasOwn(this.attrs, key) && (expected === undefined || this.attrs[key] === expected);
        return this.tag === selector;
    }
    querySelectorAll(selector) {
        const selectors = selector.split(',').map(x => x.trim()), found = [];
        const visit = node => {
            for (const child of node.children) {
                if (selectors.some(s => child.matches(s))) found.push(child);
                visit(child);
            }
        };
        visit(this); return found;
    }
    querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
    closest(selector) { return this.matches(selector) ? this : this.parent?.closest(selector) || null; }
    addEventListener(type, listener) {
        if (!this.listeners.has(type)) this.listeners.set(type, []);
        this.listeners.get(type).push(listener);
    }
    dispatch(type, target = this) {
        if (target.disabled) return Promise.resolve();
        const results = [];
        for (let node = this; node; node = node.parent) {
            const event = { target, currentTarget: node };
            for (const handler of [node['on' + type], ...(node.listeners.get(type) || [])]) if (handler) results.push(handler(event));
        }
        return Promise.all(results);
    }
    click() { return this.dispatch('click'); }
}

const caps = ['hoeing.start.v2', 'hoeing.completionChoice.v1', 'hoeing.quickCommandCompletion.v1'];
function member(uid, label, online = true) {
    return Object.freeze({ playerUid: uid, playerName: label, clientInstanceId: label + '-instance', machineId: label + '-machine', connectionId: label + '-connection',
        online, bgiStatus: 'running', bgiVersion: '1.0.0', taskRunning: true, currentTaskName: label + '-task', hoeingBatchId: label + '-batch',
        configGroups: [label + '-group', label + '-alternate'], oneClickConfigs: [label + '-dragon'],
        configGroupTasks: { [label + '-group']: ['first', 'second'] }, configGroupTasksWithStatus: { [label + '-group']: [{ status: 'Enabled' }, { status: 'Enabled' }] },
        oneClickTasks: { [label + '-dragon']: ['dragon-one'] }, oneClickTasksWithStatus: { [label + '-dragon']: [{ taskId: label + '-node', enabled: true }] },
        onlineHoeingGroupNames: [label + '-group'], onlineHoeingGroupTypes: ['group'], scheduledOnlineTime: '08:00', quickCommands: {},
        hoeingCompletionAction: { policy: 'resume', taskType: 'group', taskName: '', priority: 0 },
        hotkeys: [{ configName: label + '-hotkey', functionName: label + '-hotkey-name', hotkeyText: 'F9' }],
        onlineHistory: [{ mode: 'scheduled', onlineTime: '08:00', consumeTime: '08:01' }] });
}
function harness({ scoped = true, duplicateMain = false, bothOnline = false } = {}) {
    const body = new Element('body');
    body.innerHTML = '<section id="loginPanel"><input id="roomCode"><input id="password"><input id="nickname"><input id="rememberMe"><button id="joinButton"></button><p id="loginError"></p></section>' +
        '<main id="controlPanel"><p id="roomInfo"></p><b id="connDot"></b><span id="connectionText"></span><button id="gengdiProduct" class="active"></button>' +
        '<nav><button id="homeTab" class="active"></button><button id="detailTab" disabled></button><button id="bgiUpdateNav"></button></nav>' +
        '<section id="homeView"><div id="roomSummary"></div><section class="quick-section"><div id="quickRow"><button class="quick-btn" data-key="传奇">传奇</button></div></section>' +
        '<span id="memberCount"></span><button id="selectAll"></button><div id="memberList"></div></section>' +
        '<section id="detailView" hidden><div id="detailHeader"></div><div id="detailTabs"></div><div id="detailContent"></div></section>' +
        '<section id="roomOwnerView" hidden></section><div id="logArea"></div></main><footer id="selectionBar"></footer>';
    const rooms = new Map(), sent = [], gateway = [], mainCalls = [], logs = [], timers = new Map(), updateOpens = [];
    const mainMembers = [member('shared', 'A-person', bothOnline), member('main-only', 'A-only')];
    if (duplicateMain) mainMembers.push(member('main-only', 'A-duplicate'));
    // Same room code on different servers, overlapping UID. All view deduplication belongs to the adapter.
    const profiles = [['A', '甲房', true, mainMembers], ['B', '乙房', false, [member('shared', 'B-person'), member('other', 'B-only')]]];
    for (const [key, name, isMain, players] of profiles) rooms.set(key, { roomKey: key, name, isMain, players, roomCode: 'ROOM',
        serverUrl: `https://${key.toLowerCase()}.invalid`, senderUid: 'web-' + key, generation: 1, ready: true, status: 'ready', capabilities: caps });
    let view = { mode: 'all', roomKey: '' }, sequence = 0, viewChanges = 0;
    const sourceOf = (p, c) => Object.freeze({ roomKey: c.roomKey, uid: p.playerUid, instanceId: p.clientInstanceId,
        machineId: p.machineId, connectionId: p.connectionId, generation: c.generation });
    const entries = c => !c?.ready ? [] : c.players.map(p => ({ member: p, context: c, source: sourceOf(p, c) }));
    const dedupe = list => {
        const map = new Map();
        for (const row of list) { const old = map.get(row.member.playerUid); if (!old || !old.member.online && row.member.online) map.set(row.member.playerUid, row); }
        return [...map.values()];
    };
    const adapter = {
        getMainContext: () => rooms.get('A'), getContext: key => rooms.get(key), getView: () => view,
        getDisplayedMembers: () => view.mode === 'orchestration' ? [] : view.mode === 'all' ? dedupe([...rooms.values()].flatMap(entries))
            : dedupe(entries(rooms.get(view.mode === 'current' ? 'A' : view.roomKey))),
        sourceFor: (uid, key) => dedupe(entries(rooms.get(key))).find(row => row.member.playerUid === uid)?.source,
        requireSource(source, options = {}) {
            const c = rooms.get(source?.roomKey);
            const entry = entries(c).find(e => ['uid', 'instanceId', 'machineId', 'connectionId', 'generation'].every(field => e.source[field] === source[field]));
            if (!entry || options.online !== false && !entry.member.online || options.uniqueUid !== false && c.players.filter(p => p.playerUid === source.uid && (options.online === false || p.online)).length !== 1)
                throw new Error('成员来源已变化或不唯一');
            return entry;
        },
        getMember(source) { try { return this.requireSource(source, { online: false, uniqueUid: false }).member; } catch { return null; } },
        sendRemoteCommand: async (source, cmd) => { adapter.requireSource(source); sent.push({ room: source.roomKey, source, cmd: copy(cmd) }); return { payload: { ack: true } }; },
        invoke: async (source, channel, name, payload) => {
            adapter.requireSource(source, { online: name !== 'control.clearOnlineHistory' }); gateway.push({ room: source.roomKey, channel, name, payload: copy(payload) });
            if (name === 'control.batch.current') return { payload: { batchId: 'B-batch', generation: 6 } };
            if (name === 'control.batch.reset') return { payload: { resetId: payload.resetId, batchId: 'B-batch', generation: 7 } };
            return { payload: { ack: true } };
        },
        setView(mode, roomKey = '') { view = { mode, roomKey }; viewChanges++; if (context.__viewChanged) context.__viewChanged(); },
        mainChanged() {}
    };
    const document = { body, getElementById: id => body.querySelector('#' + id), createElement: tag => new Element(tag), querySelectorAll: s => body.querySelectorAll(s) };
    let ownerCloses = 0;
    const context = vm.createContext({ document, window: { ...(scoped ? { webRooms: adapter } : {}), CSS: { supports: () => true }, addEventListener() {}, removeEventListener() {}, scrollTo() {},
        bgiWebUpdate: { renderVersion: p => p.bgiVersion + '<button data-ui="bgi-update" data-uid="' + p.playerUid + '" data-instance="' + p.clientInstanceId + '">更新</button>', targetFor: () => true,
            open: p => updateOpens.push(p.playerName), refreshHints() {} } },
        localStorage: { getItem: () => null }, crypto: { randomUUID: () => 'test-' + (++sequence) },
        Option: function(text, value) { const o = new Element('option', { value }); o.textContent = text; return o; },
        setTimeout(fn, delay) { const id = ++sequence; timers.set(id, { fn, delay }); return id; }, clearTimeout(id) { timers.delete(id); },
        __main: mainMembers, __caps: caps, __log: msg => logs.push(msg), __closeOwner: () => { ownerCloses++; document.getElementById('roomOwnerView').hidden = true; },
        __mainInvoke: async (channel, envelope) => { mainCalls.push({ channel, envelope: copy(envelope) }); return { payload: { batchId: 'A-batch', protocol: 'hoeing.start.v2', phase: 'registering' } }; }
    });
    const evaluate = (code, values = {}) => { Object.assign(context, values); return vm.runInContext(code, context); };
    for (const script of scripts.slice(0, 2)) vm.runInContext(script.code, context, { filename: script.name });
    evaluate('players = __main; roomReady = true; roomCode = "ROOM"; playerName = "main"; serverCapabilities = __caps; connection = { invoke: __mainInvoke }; log = __log;');
    vm.runInContext(scripts[2].code, context, { filename: scripts[2].name });
    evaluate('globalThis.webCloseOwnerView = __closeOwner');
    const source = (room, uid = 'shared') => adapter.sourceFor(uid, room);
    const element = id => document.getElementById(id);
    const overlay = () => body.querySelectorAll('.modal-overlay').at(-1);
    const card = uid => element('memberList').querySelector(`[data-member-uid="${uid}"]`);
    const openCard = async (uid = 'shared') => card(uid).querySelector('[data-ui="detail"]').click();
    const section = async name => element('detailTabs').querySelector(`[data-section="${name}"]`).click();
    function commandReply(record, { room = record.room, instance, status = 'success', uid = record.source.uid, params = {}, reply = 'ack' } = {}) {
        const c = rooms.get(room);
        evaluate('consumeMemberReply(__reply, __room); consumeStateResetAck(__reply, __room);', {
            __room: room, __reply: { commandId: record.cmd.commandId, cmd: reply, roomCode: c.roomCode, senderUid: uid,
                ...(instance === undefined ? {} : { senderInstanceId: instance }), target: [c.senderUid], params: { status, ok: 'true', ...params } }
        });
    }
    function result(record, { room = record.room, instance, cmd = record.cmd.cmd, uid = record.source.uid, ...extra } = {}) {
        const c = rooms.get(room);
        evaluate('consumeMemberResult(__result, __room); consumeConfigApplied(__result, __room); consumeStateResetResult(__result, __room);', {
            __room: room, __result: { commandId: record.cmd.commandId, cmd, roomCode: c.roomCode, senderUid: c.senderUid, targetUid: uid,
                ...(instance === undefined ? {} : { targetInstanceId: instance }), status: 'success', message: 'confirmed', ...extra }
        });
    }
    return { body, rooms, adapter, sent, gateway, mainCalls, logs, timers, updateOpens, evaluate, element, source, overlay, card, openCard, section,
        commandReply, result, ownerCloses: () => ownerCloses, viewChanges: () => viewChanges,
        setView(mode, key) { adapter.setView(mode, key); evaluate('showHome(); renderControlRoom();'); },
        openOperation: (name, selected = source('B')) => evaluate('showMemberOperation([__source.uid], __name, {}, __source)', { __source: selected, __name: name }),
        selectValue(selector, value, host = element('detailContent')) { const el = host.querySelector(selector); el.value = value; return el.dispatch('change'); }
    };
}

// The same UID on two coordinators must neither share its selection nor select the main detail by accident.
test('all-room card click freezes the displayed source; selection stays main and secondary BGI update is unavailable', async () => {
    const h = harness();
    assert.equal(h.element('memberCount').textContent, '3');
    assert.match(h.card('shared').textContent, /B-person/);
    assert.match(h.card('shared').textContent, /乙房/);
    assert.equal(h.card('shared').querySelector('[data-ui="select"]'), null);
    assert.equal(h.card('shared').querySelector('[data-ui="bgi-update"]'), null);
    assert.deepEqual(copy(h.evaluate('[...selectedMemberUids]')), ['main-only']);
    await h.openCard();
    assert.match(h.element('detailHeader').innerHTML, /B-person/);
    assert.match(h.element('detailHeader').innerHTML, /暂不支持更新 BGI/);
    assert.equal(h.evaluate('activeMemberSource.roomKey'), 'B');
    h.setView('room', 'B');
    assert.equal(h.element('selectionBar').hidden, true);
    assert.equal(h.element('selectAll').hidden, true);
    assert.equal(h.element('quickRow').closest('.quick-section').hidden, true);
    assert.deepEqual(copy(h.evaluate('[...selectedMemberUids]')), ['main-only']);
    h.setView('current');
    assert.equal(h.element('detailTab').disabled, true);
    h.evaluate('openMemberDetail(activeMemberUid)');
    assert.equal(h.element('detailView').hidden, true);
    assert.match(h.card('shared').textContent, /A-person/);
    assert.equal(h.card('shared').querySelector('.web-room-source-label'), null);
});

test('status stop, start BGI, close game and clear online send exact single-card source and ignore another room receipt', async () => {
    const h = harness(); await h.openCard();
    for (const op of ['stop', 'start_bgi', 'close_game', 'clear_online']) {
        await h.element('detailContent').querySelector(`[data-op="${op}"]`).click();
        const modal = h.overlay(), confirm = modal.querySelector('#operationConfirm');
        assert.match(modal.innerHTML, /B-person.*乙房/);
        const pending = confirm.click(); await flush(); const last = h.sent.at(-1);
        assert.equal(last.room, 'B'); assert.equal(last.cmd.cmd, op);
        assert.equal(last.cmd.senderUid, 'web-B'); assert.deepEqual(last.cmd.target, ['shared']);
        if (op === 'stop') assert.equal(last.cmd.params.hoeingBatchId, 'B-person-batch');
        h.commandReply(last, { room: 'A' }); await flush(); assert.equal(confirm.hidden, false);
        h.commandReply(last, { instance: 'wrong-instance' }); await flush(); assert.equal(confirm.hidden, false);
        h.commandReply(last); await pending;
        assert.match(modal.querySelector('.form-result').textContent, /B-person.*已确认/);
        modal.remove();
    }
    assert.equal(h.mainCalls.length, 0);
    assert.equal(h.sent.length, 4);
    const sourceB = h.source('B'), sourceA = h.source('A');
    assert.ok(h.evaluate('memberCommandResults.has(memberSourceKey("shared", __source))', { __source: sourceB }));
    assert.equal(h.evaluate('memberCommandResults.has(memberSourceKey("shared", __source))', { __source: sourceA }), false);
});

test('a stale single-card confirmation never retargets the main room or new instance, even after same-UID reappearance', async () => {
    const h = harness(); h.openOperation('stop'); const modal = h.overlay();
    h.rooms.get('B').generation++;
    h.rooms.get('B').players = [member('shared', 'B-replacement')];
    h.setView('current');
    await modal.querySelector('#operationConfirm').click();
    assert.equal(h.sent.length + h.mainCalls.length, 0);
    assert.match(modal.querySelector('.form-result').textContent, /变化|未确认/);
    const old = h.source('B'); h.rooms.get('B').players.push(member('shared', 'B-duplicate'));
    await assert.rejects(h.evaluate('sendMemberOperation("shared", "start_bgi", {}, null, __source)', { __source: old }), /不唯一|多个/);
    assert.equal(h.sent.length, 0);
});

test('independent room commands and waits do not consume or interrupt each other for the same UID and command ID', async () => {
    const h = harness({ bothOnline: true });
    const make = room => h.evaluate('sendTrackedCommand({...makeCmd("start_bgi", {}), target:["shared"], commandId:"equal-id"}, null, __source)', { __source: h.source(room) });
    const a = make('A'), b = make('B'); await flush();
    assert.equal(h.sent.length, 2);
    const bCommand = h.sent.find(record => record.room === 'B');
    h.evaluate('interruptMemberRoomWaits("A")');
    assert.equal((await a).status, 'unknown');
    assert.equal(h.evaluate('pendingMemberCommands.size'), 1);
    h.commandReply(bCommand, { room: 'A' }); await flush();
    assert.equal(h.evaluate('pendingMemberCommands.size'), 1);
    h.commandReply(bCommand); assert.equal((await b).status, 'success');
    assert.equal(h.timers.size, 0);
});

test('secondary settings persist only to the selected room source and completion reads/writes/readback remain scoped', async () => {
    const h = harness(); await h.openCard(); await h.section('settings'); await flush();
    const completion = h.sent.at(-1), wire = { completionStartVersion: '1', policy: 'resume', specifiedType: 'group', specifiedName: '', specifiedPriority: '0',
        specifiedStartMode: 'default', specifiedStartTaskId: '', specifiedStartTaskName: '', specifiedConfigRevision: '', specifiedWorkflowId: '',
        catalog: JSON.stringify({ workflows: [], target: null, error: null }) };
    assert.equal(completion.cmd.cmd, 'task_policy.pull');
    h.commandReply(completion, { room: 'A', reply: 'task_policy.data', params: wire }); await flush();
    assert.equal(h.element('savePolicy').disabled, true);
    h.commandReply(completion, { reply: 'task_policy.data', params: wire }); await flush();
    assert.equal(h.element('savePolicy').disabled, false);
    const host = h.element('detailContent');
    await h.selectValue('#bindingChoice', JSON.stringify(['group', 'B-person-alternate'])); await h.element('addBinding').click();
    const binding = h.element('saveBindings').click(); await flush();
    assert.deepEqual(h.sent.at(-1).cmd.params.groupNames, ['B-person-group', 'B-person-alternate']);
    h.commandReply(h.sent.at(-1)); await binding;
    h.element('scheduledTime').value = '12:34'; const schedule = h.element('saveSchedule').click(); await flush();
    assert.equal(h.sent.at(-1).cmd.params.scheduledOnlineTime, '12:34'); h.commandReply(h.sent.at(-1)); await schedule;
    const quick = host.querySelector('[data-quick-index="0"]'); quick.value = JSON.stringify(['onedragon', 'B-person-dragon']);
    const savingQuick = host.querySelector('[data-save-quick="0"]').click(); await flush();
    assert.equal(h.sent.at(-1).cmd.params.value, 'ONEDRAGON:B-person-dragon'); h.commandReply(h.sent.at(-1)); await savingQuick;
    assert.equal(h.evaluate('currentMember("shared", __source).scheduledOnlineTime', { __source: h.source('B') }), '12:34');
    assert.equal(h.rooms.get('A').players[0].scheduledOnlineTime, '08:00');
    await h.selectValue('#completionPolicy', 'stop'); const save = h.element('savePolicy').click(); await flush();
    const before = h.sent.at(-1); assert.equal(before.cmd.cmd, 'task_policy.pull');
    h.commandReply(before, { reply: 'task_policy.data', params: wire }); await flush();
    const push = h.sent.at(-1); assert.equal(push.cmd.cmd, 'task_policy.push'); assert.equal(push.cmd.params.policy, 'stop');
    h.commandReply(push, { reply: 'task_policy.push_result', params: { ...wire, policy: 'stop' } }); await flush();
    const after = h.sent.at(-1); assert.equal(after.cmd.cmd, 'task_policy.pull');
    h.commandReply(after, { reply: 'task_policy.data', params: { ...wire, policy: 'stop' } }); await save;
    assert.match(h.element('policyResult').textContent, /回读确认/);
    assert.equal(h.evaluate('currentMember("shared", __source).hoeingCompletionAction.policy', { __source: h.source('B') }), 'stop');
    assert.equal(h.rooms.get('A').players[0].hoeingCompletionAction.policy, 'resume');
    assert.ok(h.sent.every(record => record.room === 'B'));
});

for (const disconnected of [false, true]) test(`config-applied receipt fences secondary task start; disconnect=${disconnected}`, async () => {
    const h = harness(); await h.openCard(); await h.section('tasks');
    await h.element('catalogItems').querySelector('button').click();
    const modal = h.overlay(); assert.match(modal.innerHTML, /B-person-group/);
    const boxes = modal.querySelectorAll('.task-checkbox'); boxes[1].checked = false;
    const confirm = modal.querySelector('#tskOk'), pending = confirm.click(); await flush();
    const config = h.sent.at(-1); assert.equal(config.cmd.cmd, 'set_task_enabled'); assert.equal(config.room, 'B');
    const identity = { configRevision: 'revision-2', targetProcessId: 12, targetStartTicksUtc: '123456789012345678' };
    h.result(config, { room: 'A', ...identity }); h.result(config, { instance: 'other', ...identity }); await flush();
    assert.equal(h.sent.length, 1);
    if (disconnected) {
        h.rooms.get('B').ready = false; h.evaluate('interruptMemberRoomWaits("B")'); await pending;
        assert.equal(h.sent.length, 1); assert.match(modal.querySelector('#tskResult').textContent, /连接断开|未确认/);
    } else {
        h.result(config, identity); await pending; await flush();
        const start = h.sent.at(-1); assert.equal(start.cmd.cmd, 'start_group'); assert.equal(start.room, 'B');
        assert.equal(start.cmd.params.expectedConfigRevision, 'revision-2');
        assert.deepEqual(start.cmd.params.bgiEpoch, { processId: 12, startTicksUtc: identity.targetStartTicksUtc });
        h.result(start); await flush(); assert.equal(modal.isConnected, false);
    }
    assert.equal(h.mainCalls.length, 0);
});

test('secondary one-dragon start keeps its taskId, and stale task catalog actions fail instead of opening a main task', async () => {
    const h = harness(); await h.openCard(); await h.section('tasks');
    await h.element('detailContent').querySelector('[data-kind="oneclick"]').click();
    const button = h.element('catalogItems').querySelector('button'); await button.click();
    const modal = h.overlay(); await modal.querySelector('.task-row').click();
    await modal.querySelector('#tskOk').click(); await flush();
    assert.equal(h.sent[0].cmd.cmd, 'start_oneclick'); assert.equal(h.sent[0].cmd.params.startFromTaskId, 'B-person-node');
    assert.equal(h.sent[0].room, 'B'); h.result(h.sent[0]);
    h.rooms.get('B').generation++;
    await button.click(); assert.equal(h.sent.length, 1);
    assert.ok(h.logs.some(msg => /变化|重新选择/.test(msg)));
});

test('history and single-card reset use their own gateway, no secondary reset-all, and reset receipt is scoped', async () => {
    const h = harness(); await h.openCard(); await h.section('history');
    await h.element('clearHistory').click(); const history = h.overlay(); await history.querySelector('#clearConfirm').click();
    assert.deepEqual(h.gateway[0], { room: 'B', channel: 'Dispatch', name: 'control.clearOnlineHistory', payload: { targetUid: 'shared' } });
    assert.match(history.querySelector('.form-result').textContent, /已清除/); history.remove();
    await h.section('status'); await h.element('detailReset').click(); const modal = h.overlay();
    assert.match(modal.innerHTML, /旁观房间仅可重置所选成员/);
    assert.equal(modal.querySelector('.reset-state-all').parent.hidden, true);
    const waiting = modal.querySelector('.reset-state-confirm').click(); await flush();
    assert.deepEqual(h.gateway.slice(1).map(c => [c.room, c.name]), [['B', 'control.batch.current'], ['B', 'control.batch.reset']]);
    assert.deepEqual(h.gateway[2].payload.targetUids, ['shared']);
    const reset = h.sent.at(-1); assert.equal(reset.cmd.cmd, 'reset_state'); assert.equal(reset.room, 'B'); assert.equal(reset.cmd.params.generation, 7);
    h.commandReply(reset, { room: 'A' }); await flush(); assert.equal(modal.querySelector('.reset-state-confirm').hidden, false);
    h.commandReply(reset); await waiting;
    assert.match(modal.querySelector('.reset-state-result').textContent, /重置成功/);
    assert.equal(h.mainCalls.length, 0);
    await assert.rejects(h.evaluate('resetMemberState("shared", true, () => {}, null, __source)', { __source: h.source('B') }), /旁观房间仅支持/);
});

test('main bulk confirmation only uses current selected main roster, and stale view or generation cannot dispatch it', async () => {
    const h = harness();
    h.evaluate('showCoordinatedConfirm("一键锄地")'); const begin = h.overlay();
    assert.match(begin.innerHTML, /A-only/); assert.doesNotMatch(begin.innerHTML, /B-person|B-only/);
    await begin.querySelector('#startBatch').click();
    assert.deepEqual(h.mainCalls[0].envelope.payload.targetUids, ['main-only']);
    assert.equal(h.sent.length, 0); begin.remove();
    h.evaluate('showCoordinatedConfirm("一键锄地")'); const stale = h.overlay();
    h.setView('room', 'B'); await stale.querySelector('#startBatch').click();
    assert.equal(h.mainCalls.length, 1); assert.match(stale.querySelector('.batch-feedback').textContent, /未开始|未下发/);
    h.setView('current'); h.evaluate('showCoordinatedConfirm("一键锄地")'); const changed = h.overlay();
    h.rooms.get('A').generation++; await changed.querySelector('#startBatch').click();
    assert.equal(h.mainCalls.length, 1);
});

test('returning from owner orchestration uses setView once and closes owner without re-entering', () => {
    const h = harness(); h.adapter.setView('orchestration'); h.element('roomOwnerView').hidden = false;
    h.evaluate('globalThis.__viewChanged = () => { if (window.webRooms.getView().mode === "current") showHome(); };');
    const before = h.viewChanges(); h.evaluate('showHome()');
    assert.equal(h.viewChanges(), before + 1); assert.equal(h.adapter.getView().mode, 'current');
    assert.equal(h.element('homeView').hidden, false); assert.equal(h.element('roomOwnerView').hidden, true);
    assert.ok(h.ownerCloses() >= 1);
});

test('settings opens the room book without touching legacy credentials; the old shell still supports legacy settings', async () => {
    const h = harness();
    let opened = 0, legacyReads = 0, legacyWrites = 0;
    h.adapter.openManager = () => opened++;
    h.evaluate('roomReady = false; localStorage.getItem = __get; localStorage.setItem = __set; openSettings()', {
        __get: () => { legacyReads++; return null; }, __set: () => legacyWrites++
    });
    assert.equal(opened, 1);
    assert.equal(h.overlay(), undefined);
    assert.equal(legacyReads + legacyWrites, 0);

    const legacy = harness({ scoped: false }), old = { roomCode: 'OLD', password: 'old-password', nickname: 'old-name' };
    const writes = [];
    legacy.evaluate('localStorage.getItem = __get; localStorage.setItem = __set; openSettings()', {
        __get: key => key === 'web_hie_cred' ? JSON.stringify(old) : null,
        __set: (key, value) => writes.push([key, JSON.parse(value)])
    });
    const modal = legacy.overlay();
    assert.equal(modal.querySelector('#setRoomCode').value, old.roomCode);
    assert.equal(modal.querySelector('#setPassword').value, old.password);
    modal.querySelector('#setNickname').value = 'new-name';
    await modal.querySelector('#setSave').click();
    assert.deepEqual(writes, [['web_hie_cred', { ...old, nickname: 'new-name' }]]);
    assert.equal(modal.isConnected, false);
});

test('main join waits for a full snapshot from its own gateway; explicit reject or snapshot mismatch never becomes ready', async () => {
    const h = harness(), invocations = [];
    const respond = async (channel, envelope) => {
        invocations.push([channel, envelope.name, copy(envelope.payload)]);
        if (envelope.name === 'control.joinRoom') return { payload: { ack: true } };
        return { payload: { success: true, playersUpdate: { full: true, players: [member('new', 'A-new')], revision: 3 } } };
    };
    h.evaluate('globalThis.webMainGeneration = 4; globalThis.webMainSnapshotGeneration = -1; globalThis.webRoomUpdateMain = update => { if (update.full) webMainSnapshotGeneration = webMainGeneration; }; roomReady = false; connection = { invoke: __respond };', { __respond: respond });
    await h.evaluate('joinControlRoom()');
    assert.deepEqual(invocations.map(call => call.slice(0, 2)), [['Dispatch', 'control.joinRoom'], ['Query', 'control.roomSnapshot']]);
    assert.equal(invocations[0][2].isRemote, false); assert.equal(invocations[0][2].requireReady, false);
    assert.equal(h.evaluate('webMainSnapshotGeneration'), 4);
    h.evaluate('webMainGeneration++; connection = {invoke: async () => ({payload:{success:false,reason:"join rejected"}})};');
    await assert.rejects(h.evaluate('joinControlRoom()'), /join rejected/);
    h.evaluate('connection = {invoke: async (_method, env) => env.name === "control.joinRoom" ? {payload:{ack:true}} : {payload:{success:true,playersUpdate:{full:false}}}};');
    await assert.rejects(h.evaluate('joinControlRoom()'), /全量快照未确认/);
    assert.equal(h.evaluate('roomReady'), false);
});
