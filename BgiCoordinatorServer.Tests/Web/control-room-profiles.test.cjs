// Isolated browser-room contract tests; no real server, user data, browser profile or file-backed storage.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const source = fs.readFileSync(path.join(__dirname, '../../BgiCoordinatorServer/wwwroot/control-room-profiles.js'), 'utf8');
const BOOK = 'web_hie_rooms.v1';
const A = 'https://coordinator-a.invalid', B = 'https://coordinator-b.invalid';
const player = (uid, instanceId, online = true) => ({ playerUid: uid, playerName: 'fake-' + uid,
    clientInstanceId: instanceId, machineId: 'fake-machine-' + instanceId, connectionId: 'fake-connection-' + instanceId, online });
const full = (players, revision = 1) => ({ full: true, revision, players });
const copy = value => JSON.parse(JSON.stringify(value));
const drain = () => new Promise(resolve => setImmediate(resolve));

function fixture(t, supplied = {}) {
    const store = supplied.store || new Map(), connections = [], registry = new Map(), events = [], disconnects = [], views = [], switches = [];
    const timers = new Map(), pageEvents = new Map();
    let now = 0, clock = 0, main, changed = 0;
    const later = (fn, ms) => { const key = ++clock; timers.set(key, { fn, at: now + ms }); return key; };
    const advance = async ms => {
        const end = now + ms;
        for (;;) {
            const next = [...timers].sort((a, b) => a[1].at - b[1].at)[0];
            if (!next || next[1].at > end) break;
            now = next[1].at; timers.delete(next[0]); next[1].fn(); await drain();
        }
        now = end; await drain();
    };
    class Hub {
        constructor(url, options) { this.url = url; this.options = options; this.calls = []; this.handler = null; this.closed = null; this.stops = 0; }
        on(name, handler) { assert.equal(name, 'evt'); this.handler = handler; }
        onclose(handler) { this.closed = handler; }
        start() { return supplied.start?.(this) || Promise.resolve(); }
        stop() { this.stops++; this.closed?.(); return Promise.resolve(); }
        emit(name, payload, roomCode = this.roomCode) { this.handler?.({ name, roomCode, payload }); }
        invoke(channel, envelope) {
            const call = { channel, ...copy(envelope) }; this.calls.push(call);
            const payload = call.payload;
            const response = data => ({ name: call.name, requestId: call.requestId, payload: data });
            if (call.name === 'session.hello') return Promise.resolve(response({ protocolVersion: 3, capabilities: ['hoeing.start.v2'] }));
            if (call.name === 'control.joinRoom') {
                this.roomCode = payload.roomCode; this.join = payload;
                this.registration = registry.get(JSON.stringify([this.url, this.roomCode]));
                if (!this.registration || this.registration.password !== payload.password) {
                    if (supplied.rejectWithEvent) this.emit('control.joinRejected', { reason: 'denied' });
                    return Promise.resolve(response({ success: false, reason: 'denied' }));
                }
                if (this.registration.ackOnly) { this.emit('control.playersUpdated', this.registration.snapshot, 'CTRL_' + this.roomCode); return Promise.resolve(response({ ack: true })); }
                return Promise.resolve(response({ success: true, playersUpdate: this.registration.snapshot }));
            }
            if (call.name === 'control.roomSnapshot') return Promise.resolve(this.registration?.read?.() || this.registration?.snapshot)
                .then(snapshot => response({ success: true, playersUpdate: snapshot }));
            return Promise.resolve(response({ ack: true, accepted: true }));
        }
        drop() { this.closed?.(new Error('synthetic disconnect')); }
    }
    class Builder {
        withUrl(url, options) { this.url = url; this.options = options; return this; }
        build() { const conn = new Hub(this.url, copy(this.options)); connections.push(conn); return conn; }
    }
    const mainHub = new Hub(A + '/gateway', {});
    const mainSnapshot = (serverUrl = A, roomCode = 'ROOM01', rows = []) => ({ serverUrl, roomCode, nickname: 'fake-login', ready: true,
        connection: mainHub, senderUid: 'web_main_fixture', capabilities: ['hoeing.start.v2'], generation: 1, players: rows });
    if (supplied.main) main = mainSnapshot(...supplied.main);
    const ctx = vm.createContext({ URL, crypto, console, setTimeout: later, clearTimeout: key => timers.delete(key),
        location: { origin: A, protocol: 'https:' }, signalR: { HubConnectionBuilder: Builder },
        localStorage: { getItem: key => store.get(key) ?? null, setItem: (key, value) => store.set(key, String(value)), removeItem: key => store.delete(key) },
        addEventListener: (name, handler) => pageEvents.set(name, handler), removeEventListener: name => pageEvents.delete(name) });
    ctx.window = ctx;
    vm.runInContext(source, ctx, { filename: 'control-room-profiles.js' });
    const api = ctx.webRooms, register = (server, roomCode, password, rows, extra = {}) => {
        const registration = { password, snapshot: full(rows), ...extra };
        registry.set(JSON.stringify([server + '/gateway', roomCode]), registration); return registration;
    };
    const setMain = (value, login) => { main = value; api.mainChanged(login); };
    api.init({ defaultServerUrl: supplied.defaultServerUrl || (() => A), getMainContext: () => main,
        ...supplied.adapter,
        onChange: () => { changed++; supplied.adapter?.onChange?.(api); },
        onViewChange: (next, previous) => views.push([copy(next), copy(previous)]),
        onDisconnect: (key, generation) => disconnects.push([key, generation]),
        onEvent: (key, envelope, context) => events.push({ key, envelope: copy(envelope), generation: context.generation }),
        onSwitchMain: async credentials => {
            switches.push(copy(credentials));
            if (supplied.switchFailed) return false;
            setMain(mainSnapshot(credentials.serverUrl, credentials.roomCode, []), { password: credentials.password, remember: credentials.remember });
            return true;
        } });
    t.after(() => api.dispose());
    const save = (serverUrl, roomCode, name, password = 'fake-room-secret', remember = false, extra = {}) => api.saveProfile({
        serverUrl, roomCode, name, password, remember, nickname: 'fake-login', ...extra });
    const connFor = (server, roomCode) => connections.filter(c => c.url === server + '/gateway' && c.roomCode === roomCode).at(-1);
    return { api, store, register, save, connections, connFor, setMain, mainSnapshot, mainHub, events, disconnects, views, switches, advance,
        pageEvents, changes: () => changed };
}

test('规范化服务器与房号的复合身份；同房号跨服务器不合并，禁止含账户/查询的地址', t => {
    const x = fixture(t), w = x.api;
    assert.equal(w.normalizeServer(' HTTPS://Coordinator-A.invalid:443/path/HUB/gateway/ '), A + '/path');
    assert.notEqual(w.roomKey(A, 'ROOM01'), w.roomKey(B, 'ROOM01'));
    assert.notEqual(w.roomKey(A, 'room01'), w.roomKey(A, 'ROOM01'));
    for (const url of ['https://name:password@host.invalid', 'https://host.invalid/?x=1', 'https://host.invalid/#fragment', 'file:///host', '/gateway'])
        assert.throws(() => w.normalizeServer(url));
    assert.throws(() => w.roomKey(A, 'Room With Space'));
});

test('旧单房记住登录迁移；未开控制房不连接次房，公开快照不返回密码', async t => {
    let old = { roomCode: 'ROOM01', password: 'fake-legacy-secret', nickname: 'fake-name' }, cleared = 0;
    const x = fixture(t, { adapter: { loadLegacyCredentials: () => old, clearLegacyCredentials: () => { old = null; cleared++; } } });
    await drain();
    assert.equal(cleared, 1); assert.equal(x.connections.length, 0);
    assert.equal(x.api.getLogin().password, 'fake-legacy-secret');
    assert.equal(x.api.getLogin().serverUrl, A);
    const p = x.api.listProfiles()[0];
    assert.equal(p.hasPassword, true); assert.equal('password' in p, false);
    assert.equal('password' in x.api.getContext(p.roomKey), false);
    assert.equal(JSON.parse(x.store.get(BOOK)).version, 1);
});

test('本机默认服务器异步出现时不会猜测旧无来源密码；明确 remember=false 只留本次凭据', async t => {
    let server = '', old = { roomCode: 'ROOM01', password: 'unknown-source' };
    const x = fixture(t, { defaultServerUrl: () => server, adapter: { legacyServerUrl: null, loadLegacyCredentials: () => old } });
    assert.equal(x.api.getLogin(), null);
    server = B; assert.equal(x.api.getLogin(), null); assert.equal(x.connections.length, 0);
    x.setMain(x.mainSnapshot(B, 'ROOM01'), { password: 'entered-now', remember: false });
    assert.equal(x.api.getLogin(), null);
    assert.equal(x.api.getLogin(x.api.roomKey(B, 'ROOM01')).password, 'entered-now');
    assert.equal(JSON.parse(x.store.get(BOOK)).profiles.length, 0);
    assert.ok(!x.store.get(BOOK).includes('entered-now'));
});

test('保存、上限、重复身份、地址变更和删除只作用本浏览器档案', async t => {
    const x = fixture(t, { main: [A, 'ROOM01', []] }), w = x.api;
    x.setMain(x.mainSnapshot(), { remember: true, password: 'fake-main' });
    const b = x.save(B, 'ROOM01', '乙', 'fake-b', true);
    assert.throws(() => x.save(B + '/hub/', 'ROOM01', '重复房'), /已存在/);
    assert.throws(() => x.save(B, 'ROOM02', '乙'), /名称/);
    x.save(B, 'ROOM02', '丙', '', false); x.save(B, 'ROOM03', '丁', '', false); x.save(B, 'ROOM04', '戊', '', false);
    assert.throws(() => x.save(B, 'ROOM05', '第六房'), /5 个/);
    assert.throws(() => w.validateMainLogin(B, 'ROOM05'), /5 个/);
    assert.throws(() => w.removeProfile(w.getMainContext().roomKey), /不可移除/);
    const moved = w.saveProfile({ serverUrl: A, roomCode: 'NEW001', name: '乙新地址', password: '', nickname: '', remember: false }, b.roomKey);
    assert.equal(moved.profileId, b.profileId); assert.equal(moved.hasPassword, false); assert.equal(w.getLogin(moved.roomKey), null);
    assert.equal(w.getContext(b.roomKey), null); assert.ok(!x.store.get(BOOK).includes('fake-b'));
    w.removeProfile(moved.roomKey); await drain();
    assert.equal(w.listProfiles().length, 4);
    assert.ok(x.connections.every(c => !c.calls.some(call => call.name === 'control.createRoom')));
});

test('未勾记住的档案重载仍有名称备注但无密码；取消旧记住不会复活旧凭据', async t => {
    const x = fixture(t), b = x.save(B, 'ROOM01', '乙', 'fake-secret', true, { note: '乙房备注' });
    x.api.saveProfile({ serverUrl: B, roomCode: 'ROOM01', name: '重命名', note: '乙房备注', password: '', nickname: 'fake-name', remember: false }, b.roomKey);
    assert.equal(x.api.getLogin(b.roomKey).password, 'fake-secret');
    assert.ok(!x.store.get(BOOK).includes('fake-secret')); assert.ok(!x.store.get(BOOK).includes('fake-name'));
    x.api.dispose();
    const y = fixture(t, { store: x.store });
    assert.equal(y.api.listProfiles()[0].name, '重命名'); assert.equal(y.api.listProfiles()[0].note, '乙房备注');
    assert.equal(y.api.getLogin(b.roomKey), null); assert.equal(y.api.getContext(b.roomKey).status, 'unauthenticated');
    await drain(); assert.equal(y.connections.length, 0);
});

test('两服务器各自旁观认证；全房按在线/当前优先去重，owner 保留所有真实实例', async t => {
    const aOnline = player('uid-1', 'a1'), aOffline = player('uid-2', 'a2', false), aOther = player('uid-3', 'a3');
    const bOnline = player('uid-1', 'b1'), bSecond = player('uid-2', 'b2'), bOther = player('uid-4', 'b4');
    const x = fixture(t, { main: [A, 'ROOM01', [aOnline, aOffline, aOther]] }), w = x.api;
    x.register(B, 'ROOM01', 'fake-b', [bOnline, bSecond, bOther, player('web_fake', 'observer')], { ackOnly: true });
    const b = x.save(B, 'ROOM01', '乙', 'fake-b'); await drain();
    const conn = x.connFor(B, 'ROOM01');
    assert.deepEqual(conn.calls.slice(0, 2).map(c => c.name), ['session.hello', 'control.joinRoom']);
    assert.equal(conn.options.withCredentials, false); assert.match(conn.join.playerUid, /^web_logviewer_/);
    assert.equal(conn.join.isRemote, true); assert.deepEqual(conn.join.allowedUids, []); assert.equal(conn.join.requireReady, false);
    assert.equal(w.getContext(b.roomKey).ready, true); assert.equal(w.getContext(b.roomKey).players.length, 3);
    w.setView('all');
    assert.deepEqual(copy(w.getDisplayedMembers().map(x => [x.member.playerUid, x.source.instanceId])),
        [['uid-1', 'a1'], ['uid-2', 'b2'], ['uid-3', 'a3'], ['uid-4', 'b4']]);
    assert.equal(w.getKnownInstances().length, 6);
    w.setView('room', b.roomKey); assert.equal(w.sourceFor('uid-1').instanceId, 'b1');
    assert.equal(w.getMainContext().players.length, 3); assert.equal(x.mainHub.calls.length, 0); assert.equal(x.switches.length, 0);
});

test('拒绝/断线不公布旧快照；错密码不会自动反复尝试', async t => {
    const x = fixture(t, { rejectWithEvent: true, main: [A, 'ROOM01', [player('uid', 'a1')]] });
    x.register(B, 'ROOM01', 'correct', [player('uid', 'b1')]);
    const b = x.save(B, 'ROOM01', '乙', 'wrong'); await drain();
    assert.equal(x.api.getContext(b.roomKey).status, 'unauthenticated'); assert.equal(x.api.getContext(b.roomKey).players.length, 0);
    await x.advance(120000); assert.equal(x.connections.length, 1);
    const updated = x.api.saveProfile({ serverUrl: B, roomCode: 'ROOM01', name: '乙', password: 'correct', remember: false }, b.roomKey);
    await drain(); assert.equal(x.api.getContext(updated.roomKey).ready, true);
    const oldSource = x.api.sourceFor('uid', b.roomKey), conn = x.connFor(B, 'ROOM01'); conn.drop();
    assert.equal(x.api.getContext(b.roomKey).ready, false); assert.equal(x.api.getContext(b.roomKey).players.length, 0);
    assert.equal(x.api.getMember(oldSource), null); assert.ok(x.disconnects.some(([k, epoch]) => k === b.roomKey && epoch === oldSource.generation));
    await x.advance(15000);
    const fresh = x.connFor(B, 'ROOM01'); assert.notEqual(fresh, conn); assert.notEqual(fresh.join.playerUid, conn.join.playerUid);
    conn.emit('control.playersUpdated', full([player('stale', 'old')], 200));
    assert.equal(x.api.sourceFor('stale', b.roomKey), null); assert.notEqual(x.api.sourceFor('uid', b.roomKey).generation, oldSource.generation);
});

test('增量按 revision/baseRevision 合并；缺基线时撤销就绪、只以最新完整快照恢复', async t => {
    const x = fixture(t), p = player('uid', 'b1');
    const reg = x.register(B, 'ROOM01', 'fake-room-secret', [p]);
    const b = x.save(B, 'ROOM01', '乙'); await drain();
    const conn = x.connFor(B, 'ROOM01'), old = x.api.sourceFor('uid', b.roomKey);
    conn.emit('control.playersUpdated', { full: false, revision: 3, baseRevision: 1, changed: [{ ...p, currentTaskName: 'new' }], removed: [] }, 'CTRL_ROOM01');
    assert.equal(x.api.getMember(old).currentTaskName, 'new');
    conn.emit('control.playersUpdated', full([player('foreign', 'bad')], 50), 'CTRL_OTHER');
    conn.emit('control.playersUpdated', full([player('stale', 'old')], 2));
    assert.equal(x.api.sourceFor('stale', b.roomKey), null); assert.equal(x.api.sourceFor('foreign', b.roomKey), null);
    let supply; reg.read = () => new Promise(resolve => { supply = resolve; });
    conn.emit('control.playersUpdated', { full: false, revision: 6, baseRevision: 5, changed: [], removed: ['uid'] });
    assert.equal(x.api.getContext(b.roomKey).status, 'unconfirmed'); assert.equal(x.api.getMember(old), null);
    assert.equal(x.api.getContext(b.roomKey).players.length, 0);
    conn.emit('control.playersUpdated', full([p], 5)); assert.equal(x.api.getContext(b.roomKey).ready, false);
    supply(full([player('new-uid', 'b2')], 6)); await drain();
    assert.equal(x.api.getContext(b.roomKey).ready, true); assert.equal(x.api.sourceFor('uid', b.roomKey), null);
    assert.notEqual(x.api.sourceFor('new-uid', b.roomKey).generation, old.generation);
    assert.equal(conn.calls.filter(c => c.name === 'control.roomSnapshot').length, 1);
});

test('同房同 UID 多实例拒绝旧 UID 命令；本人房间请求只选精确实例且原回执不串房', async t => {
    const x = fixture(t), w = x.api, p = player('uid', 'b1'), q = player('uid', 'b2');
    x.register(B, 'ROOM01', 'fake-room-secret', [p, q]);
    const b = x.save(B, 'ROOM01', '乙'); await drain();
    const candidates = w.getKnownInstances().filter(x => x.source.uid === 'uid'); assert.equal(candidates.length, 2);
    const target = candidates.find(x => x.source.instanceId === 'b2').source;
    assert.equal(w.requireSource(target, { online: false, uniqueUid: false }).member.clientInstanceId, 'b2');
    assert.throws(() => w.requireSource(target, { online: true, uniqueUid: true }), /多个/);
    await assert.rejects(w.sendRemoteCommand(target, { cmd: 'stop', commandId: 'cmd-1', target: ['uid'] }), /多个/);
    const conn = x.connFor(B, 'ROOM01');
    conn.emit('control.playersUpdated', { full: false, revision: 2, baseRevision: 1, changed: [p, { ...q, taskRunning: true }], removed: [] });
    assert.equal(w.getMember(target).taskRunning, true);
    const request = { operation: 'room.profiles.get', requestId: 'owner-1', targetInstanceId: 'b2', expectedMachineId: target.machineId };
    const payload = { request, roomCode: 'ROOM01', targetUid: 'uid', targetInstanceId: 'b2', requestId: 'owner-1' };
    await assert.rejects(w.invoke(target, 'Dispatch', 'mistletoe.request', { ...payload, request: { ...request, expectedMachineId: 'wrong' } }), /身份不匹配/);
    await w.invoke(target, 'Dispatch', 'mistletoe.request', payload);
    assert.deepEqual(conn.calls.at(-1).payload, copy(payload));
    conn.emit('mistletoe.result', { requestId: 'owner-1', reply: { targetInstanceId: 'b1', status: 'ok' } });
    assert.equal(x.events.length, 0);
    conn.emit('mistletoe.result', { requestId: 'owner-1', reply: { targetInstanceId: 'b2', status: 'ok' } });
    assert.equal(x.events.length, 1); assert.equal(x.events[0].key, b.roomKey);
});

test('次房单卡命令重写房间和发起方、绑定唯一实例；不接收入站执行或转发主批次', async t => {
    const x = fixture(t, { main: [A, 'ROOM01', [player('uid', 'a1')]] }), w = x.api;
    x.register(B, 'ROOM02', 'fake-room-secret', [player('uid', 'b1')]);
    const b = x.save(B, 'ROOM02', '乙'); await drain();
    const target = w.sourceFor('uid', b.roomKey), conn = x.connFor(B, 'ROOM02');
    const original = { cmd: 'start_group', commandId: 'cmd-1', target: ['uid'], roomCode: 'ROOM01', senderUid: 'web_main_fixture', sender: 'main', params: { groupName: 'fake-group' } };
    await assert.rejects(w.sendRemoteCommand(target, { ...original, target: ['*'] }), /单成员/);
    await assert.rejects(w.sendRemoteCommand(target, { ...original, params: { key: '一键锄地' } }), /单成员/);
    await assert.rejects(w.invoke(target, 'Dispatch', 'control.batch.begin', { targetUids: ['uid'] }), /不支持/);
    await assert.rejects(w.invoke(target, 'Dispatch', 'control.batch.reset', { resetId: 'fake', targetUids: ['uid', 'other'] }), /单卡/);
    await w.sendRemoteCommand(target, original);
    const cmd = conn.calls.at(-1).payload.command;
    assert.equal(cmd.roomCode, 'ROOM02'); assert.equal(cmd.senderUid, conn.join.playerUid); assert.equal(cmd.targetInstanceId, 'b1');
    assert.equal(original.roomCode, 'ROOM01'); assert.equal(original.senderUid, 'web_main_fixture'); assert.equal(x.mainHub.calls.length, 0);
    conn.emit('control.remoteCommand', { command: { commandId: 'cmd-1', cmd: 'stop', roomCode: 'ROOM02', senderUid: 'uid', target: [cmd.senderUid] } });
    conn.emit('control.batch.state', { batchId: 'other-batch' });
    conn.emit('control.remoteCommandResult', { commandId: 'cmd-1', roomCode: 'ROOM02', senderUid: 'web_main_fixture', targetUid: 'uid', cmd: 'start_group', status: 'success' });
    assert.equal(x.events.length, 0);
    conn.emit('control.remoteCommandResult', { commandId: 'cmd-1', roomCode: 'ROOM02', senderUid: cmd.senderUid, targetUid: 'uid', cmd: 'start_group', status: 'success' });
    assert.equal(x.events.length, 1); assert.equal(x.events[0].key, b.roomKey);
    w.reconnect(b.roomKey); await drain();
    conn.emit('control.remoteCommandResult', { commandId: 'cmd-1', roomCode: 'ROOM02', senderUid: cmd.senderUid, targetUid: 'uid', cmd: 'start_group', status: 'success' });
    assert.equal(x.events.length, 1); await assert.rejects(w.sendRemoteCommand(target, { ...original, commandId: 'new' }), /已变化/);
});

test('显式切浏览器主房才调用适配器；查看偏好保留，原主房/旁观代次均不可沿用', async t => {
    const a = player('uid', 'a1'), bMember = player('uid', 'b1'), x = fixture(t, { main: [A, 'ROOM01', [a]] }), w = x.api;
    x.setMain(x.mainSnapshot(A, 'ROOM01', [a]), { password: 'fake-main', remember: true });
    x.register(A, 'ROOM01', 'fake-main', [a]); x.register(B, 'ROOM01', 'fake-room-secret', [bMember]);
    const b = x.save(B, 'ROOM01', '乙', 'fake-room-secret', true); await drain();
    const oldMain = w.sourceFor('uid', w.getMainContext().roomKey), oldViewed = w.sourceFor('uid', b.roomKey);
    w.setView('all'); assert.equal(x.switches.length, 0);
    await w.requestSwitch(b.roomKey); await drain();
    assert.equal(x.switches.length, 1); assert.equal(x.switches[0].serverUrl, B);
    assert.equal(w.getMainContext().roomKey, b.roomKey); assert.equal(w.getView().mode, 'all');
    assert.equal(w.getMember(oldMain), null); assert.equal(w.getMember(oldViewed), null);
    assert.equal(w.getContext(oldMain.roomKey).isMain, false); assert.equal(w.getContext(oldMain.roomKey).ready, true);
    assert.equal(w.getLogin().roomKey, b.roomKey); assert.equal(x.views.length, 1);
});

test('初次连接超时、迟到 start 完成和页面释放不复活旧连接；自动重连先 hello', async t => {
    let finishStart, first = true;
    const x = fixture(t, { start: () => { if (!first) return Promise.resolve(); first = false; return new Promise(resolve => { finishStart = resolve; }); } });
    x.register(B, 'ROOM01', 'fake-room-secret', [player('uid', 'b1')]); const b = x.save(B, 'ROOM01', '乙');
    const stale = x.connections[0]; await x.advance(15000);
    assert.equal(x.api.getContext(b.roomKey).status, 'failed'); assert.ok(stale.stops > 0);
    finishStart(); await drain(); assert.equal(stale.calls.length, 0);
    await x.advance(15000); assert.equal(x.api.getContext(b.roomKey).ready, true);
    assert.equal(x.connections.length, 2); assert.equal(x.connections[1].calls[0].name, 'session.hello');
    x.pageEvents.get('pagehide')(); assert.equal(x.api.getContext(b.roomKey).ready, false);
    x.connections[1].emit('control.playersUpdated', full([player('bad', 'bad')], 90));
    x.pageEvents.get('pageshow')(); await drain(); assert.equal(x.connections.length, 3); assert.equal(x.api.sourceFor('bad', b.roomKey), null);
    x.api.dispose(); await x.advance(60000); assert.equal(x.connections.length, 3);
});

test('同窗口主房断线与代次变化立即撤销可操作来源；其他页面存储变化不覆盖', async t => {
    const x = fixture(t, { main: [A, 'ROOM01', [player('uid', 'a1')]] }), w = x.api;
    const old = w.sourceFor('uid'); x.setMain({ ...x.mainSnapshot(A, 'ROOM01', [player('uid', 'a1')]), ready: false });
    assert.equal(w.getMainContext().players.length, 0); assert.equal(w.getMember(old), null);
    x.save(B, 'ROOM01', '乙', '', false); const prior = JSON.parse(x.store.get(BOOK));
    prior.profiles[0].name = '另一标签页编辑'; x.store.set(BOOK, JSON.stringify(prior));
    w.saveProfile({ serverUrl: B, roomCode: 'ROOM01', name: '本标签页编辑', remember: false }, w.roomKey(B, 'ROOM01'));
    assert.equal(JSON.parse(x.store.get(BOOK)).profiles[0].name, '另一标签页编辑');
    assert.match(w.getViewNotice(), /其他页面已修改/);
});
