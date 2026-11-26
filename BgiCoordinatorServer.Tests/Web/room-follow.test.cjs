// 实际网页脚本与房间弹窗的隔离契约：只使用内存网关/中继、合成房间及浏览器存储，不连真实执行端。
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { createHash, randomBytes, webcrypto } = require('node:crypto');
const src = name => fs.readFileSync(path.join(__dirname, '../../BgiCoordinatorServer/wwwroot', name), 'utf8');
const A = 'https://room-follow-a.invalid', B = 'https://room-follow-b.invalid', C = 'https://room-follow-c.invalid';
const RELAY = 'https://follow-relay.invalid', CODE = 'ROOM01', BOOK = 'web_hie_rooms.v1';
const publisher = { uid: 'fixture-executor', instanceId: 'fixture-instance', machineId: 'fixture-machine' };
const random = () => randomBytes(32).toString('hex');
const hash = value => createHash('sha256').update(value).digest('hex');
const invite = (overrides = {}) => {
    const readSecret = random(), writeHash = hash(random());
    return { version: 1, serverUrl: RELAY, local: false, ...publisher, channelId: hash(writeHash + ':' + hash(readSecret)), readSecret, writeHash, ...overrides };
};
const encoded = value => 'BGIFOLLOW1.' + Buffer.from(typeof value === 'string' ? value : JSON.stringify(value)).toString('base64url');
const profile = (id, serverUrl, roomCode = CODE, extra = {}) => ({ id, name: '共享' + id, note: '房间备注', serverUrl, roomCode,
    teamUids: ['fixture-executor'], expectedHoeingPlayers: 4, ...extra });
const pa = profile('a', A), pb = profile('b', B), pc = profile('c', C);
const snapshot = (activeId = 'b', viewedId = activeId, extra = {}) => ({ version: 1, ...publisher, confirmed: true,
    profiles: [pa, pb, pc], activeId, viewedId, showOrchestration: false, ...extra });
const clone = value => JSON.parse(JSON.stringify(value));
const member = label => ({ playerUid: 'fixture-member', playerName: label, clientInstanceId: label + '-instance', machineId: 'fixture-machine',
    connectionId: label + '-connection', online: true, bgiStatus: 'running', taskRunning: false, bgiVersion: 'test' });
const full = label => ({ full: true, revision: 1, players: [member(label)] });
const later = () => new Promise(resolve => setImmediate(resolve));
async function until(read, description = 'expected state') {
    for (let i = 0; i < 200; i++) { const value = read(); if (value) return value; await later(); }
    assert.fail('did not reach ' + description);
}
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };

function codec() {
    const ctx = vm.createContext({ URL, TextEncoder, TextDecoder, Uint8Array, atob, btoa, crypto: webcrypto });
    ctx.window = ctx; vm.runInContext(src('control-room-follow.js'), ctx); return ctx.webRoomFollow;
}

test('邀请与快照校验使用实际 BGIFOLLOW1 白名单、身份及能力绑定', async () => {
    const api = codec(), valid = invite();
    const parsed = await api.parseInvitation(encoded(valid));
    assert.equal(parsed.uid, publisher.uid); assert.equal(parsed.serverUrl, RELAY);
    assert.ok(parsed.channelId === valid.channelId, 'capability verified');
    assert.equal(api.decodeSnapshot(JSON.stringify(snapshot()), parsed).activeId, 'b');
    assert.equal(api.decodeSnapshot(null, parsed), null);
    // 只生成合成的一次性能力；错误提示不得回显输入。
    for (const invalid of [encoded({ ...valid, channelId: random() }), encoded({ ...valid, serverUrl: RELAY.replace('https:', 'http:') }),
        encoded({ ...valid, serverUrl: RELAY + '?query=x' }), encoded({ ...valid, serverUrl: 'https://name@follow-relay.invalid' }),
        encoded({ ...valid, local: true, serverUrl: '' }), encoded({ ...valid, password: 'not-a-follow-field' }),
        encoded(JSON.stringify(valid).replace('"version":1', '"version":1,"version":1'))]) {
        await assert.rejects(api.parseInvitation(invalid), e => !e.message.includes(valid.readSecret) && /邀请/.test(e.message));
    }
    for (const invalid of [snapshot('b', 'missing'), snapshot('missing'), snapshot('b', 'b', { machineId: 'wrong' }),
        snapshot('b', 'b', { profiles: [{ ...pb, password: 'not-a-metadata-field' }] }), snapshot('b', 'b', { profiles: [pb, pb] }),
        snapshot('b', 'b', { profiles: [{ ...pb, serverUrl: RELAY + '#fragment' }] })]) {
        assert.throws(() => api.decodeSnapshot(JSON.stringify(invalid), parsed), /元数据/);
    }
    assert.throws(() => api.decodeSnapshot(JSON.stringify(snapshot()).replace('"confirmed":true', '"confirmed":false,"confirmed":true'), parsed), /元数据/);
});

async function fixture(t, options = {}) {
    const { dom } = await import('./desktop-control-harness.mjs');
    const ui = dom(), doc = ui.document, Element = doc.createElement('div').constructor;
    const originalMatches = Element.prototype.matches, originalSet = Element.prototype.setAttribute;
    Element.prototype.matches = function(selector) { return selector.split(',').some(s => originalMatches.call(this, s.trim())); };
    Element.prototype.setAttribute = function(name, value) { originalSet.call(this, name, value); if (name === 'hidden') this.hidden = true; };
    Element.prototype.append = function(...children) { children.forEach(c => this.appendChild(c)); };
    Element.prototype.prepend = function(child) { child.parentElement = this; this.children.unshift(child); };
    Element.prototype.insertBefore = function(child, before) { child.parentElement = this; this.children.splice(Math.max(0, this.children.indexOf(before)), 0, child); };
    Element.prototype.closest = function(selector) { for (let e = this; e; e = e.parentElement) if (e.matches(selector)) return e; return null; };
    Element.prototype.focus = function() { doc.activeElement = this; };
    Object.defineProperty(Element.prototype, 'isConnected', { get() {
        let node = this; while (node.parentElement?.children.includes(node)) node = node.parentElement; return node === doc.body;
    } });
    doc.querySelectorAll = selector => doc.body.querySelectorAll(selector);
    const add = (tag, id, parent = doc.body, attrs = {}) => { const el = new Element(tag, { ...(id ? { id } : {}), ...attrs }); parent.appendChild(el); return el; };
    const get = id => doc.getElementById(id);
    const panel = get('controlPanel'), login = get('loginPanel'), home = get('homeView');
    const form = add('form', 'loginForm', login);
    for (const id of ['roomCode','password','nickname','rememberMe','joinButton']) add('input', id, form);
    get('rememberMe').checked = options.remember === true;
    get('gengdiProduct').classList.add('active'); get('detailView').hidden = true;
    add('div', 'roomSummary', home); add('div', 'memberCount', home); add('button', 'selectAll', home); add('div', 'memberList', home);
    const quick = add('section', '', home, { class: 'quick-section' }); add('div', 'quickRow', quick);
    for (const id of ['homeTab','detailTab','bgiUpdateNav']) add('button', id, get('gengdiNav'));
    for (const id of ['detailHeader','detailTabs','detailContent']) add('div', id, get('detailView'));
    add('section', 'roomOwnerView', panel).hidden = true;
    add('button', 'mistletoeProduct', panel); add('section', 'mistletoeView', panel).hidden = true;
    doc.body.dataset = options.local ? { localBrowser: 'true' } : {};
    const backdropStyle = Object.create(null);
    // Only the existing popup's native-dialog positioning needs CSSStyleDeclaration.setProperty.
    const create = doc.createElement;
    doc.createElement = tag => { const el = create(tag); el.style.setProperty = (key, value) => { backdropStyle[key] = value; }; return el; };
    panel.style.display = options.local ? 'block' : 'none'; login.style.display = options.local ? 'none' : 'block';
    if (options.local) { get('roomInfo').textContent = '本机助手'; add('input', 'localServerUrl', form).value = A; }

    const stores = { local: options.store || new Map(), session: options.sessionStore || new Map() }, storageOps = [];
    const storage = (kind) => ({ getItem: key => stores[kind].get(key) ?? null,
        setItem(key, value) { stores[kind].set(key, String(value)); storageOps.push([kind, key, 'write']); },
        removeItem(key) { stores[kind].delete(key); storageOps.push([kind, key, 'remove']); } });
    const regs = new Map(), hubs = [], calls = [], events = new Map(), timers = new Map(), logs = [];
    const token = invite(); let bindResult = { version: 1, state: JSON.stringify(options.initial || snapshot()), demand: true }, holdBind = null, holdJoin = null;
    let timerId = 0, owners = 0;
    function registered(server, label, nickname = 'browser-' + label) {
        const entry = { password: random(), nickname, rows: full(label) };
        regs.set(server + '/gateway', entry); return entry;
    }
    const a = registered(A, 'a'), b = registered(B, 'b'), c = registered(C, 'c');
    class Hub {
        constructor(url, opts, retry, logging) { this.url = url; this.opts = opts; this.retry = retry; this.logging = logging; this.relay = url === RELAY + '/room-follow'; this.state = 'Disconnected'; this.handlers = {}; this.stops = 0; hubs.push(this); }
        on(name, fn) { this.handlers[name] = fn; }
        onclose(fn) { this.closed = fn; }
        onreconnecting(fn) { this.reconnecting = fn; }
        onreconnected(fn) { this.reconnected = fn; }
        start() { this.state = 'Connected'; return Promise.resolve(); }
        stop() { this.stops++; this.state = 'Disconnected'; this.closed?.(); return Promise.resolve(); }
        relayEvent(version, next) { this.handlers.FollowState?.(version, typeof next === 'string' || next === null ? next : JSON.stringify(next)); }
        lost() { this.state = 'Reconnecting'; this.reconnecting?.(); }
        resumed() { this.state = 'Connected'; this.reconnected?.(); }
        invoke(method, ...args) {
            if (this.relay) {
                calls.push({ hub: this, method, count: args.length, capabilityCorrect: args.length === 2 && args[0] === token.readSecret && args[1] === token.writeHash });
                if (method !== 'BindSubscriber') throw new Error('unexpected relay method');
                return holdBind ? holdBind.promise : Promise.resolve(bindResult);
            }
            const env = args[0], p = env?.payload;
            // Never put credentials or capabilities into recorded diagnostics.
            const reg = regs.get(this.url), loginCorrect = p?.password === reg?.password && (p?.isRemote === true || p?.playerName === reg?.nickname);
            calls.push({ hub: this, method, name: env.name, code: p?.roomCode, uid: p?.playerUid, remote: p?.isRemote, loginCorrect, payloadKeys: Object.keys(p || {}) });
            const reply = payload => ({ name: env.name, requestId: env.requestId, payload });
            if (env.name === 'session.hello') return Promise.resolve(reply({ protocolVersion: 3, capabilities: [] }));
            if (env.name === 'control.joinRoom') {
                if (holdJoin?.url === this.url && !p.isRemote) { const hold = holdJoin; holdJoin = null; hold.seen.resolve(this); return hold.reply.promise.then(() => reply({ success: loginCorrect, playersUpdate: reg.rows })); }
                return Promise.resolve(reply(loginCorrect ? { success: true, playersUpdate: reg.rows } : { success: false, reason: 'fixture rejected' }));
            }
            if (env.name === 'control.roomSnapshot') return Promise.resolve(reply({ success: true, playersUpdate: reg.rows }));
            return Promise.resolve(reply({ ack: true }));
        }
    }
    class Builder {
        withUrl(url, opts) { this.url = new URL(url, A).href; this.opts = opts; return this; }
        configureLogging(value) { this.logging = value; return this; }
        withAutomaticReconnect(retry) { this.retry = retry; return this; }
        build() { return new Hub(this.url, this.opts, this.retry, this.logging); }
    }
    const ctx = vm.createContext({ document: doc, URL, TextEncoder, TextDecoder, Uint8Array, atob, btoa, crypto: webcrypto, console,
        location: { origin: options.local ? 'http://127.0.0.1:12345' : A, href: options.local ? 'http://127.0.0.1:12345/' : A + '/' },
        localStorage: storage('local'), sessionStorage: storage('session'), innerHeight: 900, innerWidth: 1200,
        signalR: { HubConnectionBuilder: Builder, HttpTransportType: { WebSockets: 1 }, LogLevel: { None: 6 } },
        setTimeout(fn, ms) { const id = ++timerId; timers.set(id, { fn, ms }); return id; }, clearTimeout: id => timers.delete(id),
        addEventListener(name, handler) { if (!events.has(name)) events.set(name, []); events.get(name).push(handler); },
        removeEventListener(name, handler) { events.set(name, (events.get(name) || []).filter(fn => fn !== handler)); },
        CSS: { supports: () => true }, scrollTo() {}, matchMedia: () => ({ matches: false }),
        mtTargets: () => [], mtCredentialFor: () => null, mtTargetReady: () => false, mtCancelRoomPending() {},
        webRoomOwner: { mount() { owners++; return { dispose() {}, refresh() {} }; } },
        showProduct(name) { for (const id of ['gengdi','dodoco','mistletoe']) get(id + 'Product').classList.toggle('active', name === id); }
    });
    ctx.window = ctx;
    if (options.local) ctx.bgiLocalBridge = { gatewayUrl: () => A + '/gateway', networkChanged() {} };
    const evaluate = (expression, vars = {}) => { Object.assign(ctx, vars); return vm.runInContext(expression, ctx); };
    for (const f of ['control-room.js','control-room-commands.js','control-room-ui.js','control-room-profiles.js','control-room-follow.js','control-room-integration.js'])
        vm.runInContext(src(f), ctx, { filename: f });
    evaluate('log = value => __logs.push(value)', { __logs: logs });
    const fire = name => { for (const fn of events.get(name) || []) fn(); };
    fire('DOMContentLoaded');
    const rooms = ctx.webRooms, controller = evaluate('webFollowController');
    const save = (server, credential, label) => rooms.saveProfile({ serverUrl: server, roomCode: CODE, name: '本地' + label,
        nickname: credential.nickname, password: credential.password, remember: true });
    if (!options.local && !options.skipLogin) {
        get('roomCode').value = CODE; get('password').value = a.password; get('nickname').value = a.nickname;
        assert.equal(await evaluate('joinRoom(__server)', { __server: A }), true);
    }
    if (!options.skipSave) save(B, b, 'b');
    await later();
    const key = server => rooms.roomKey(server, CODE);
    const mainJoins = () => calls.filter(call => call.name === 'control.joinRoom' && call.remote === false);
    const relay = () => hubs.filter(h => h.relay).at(-1);
    const status = () => controller.state().message;
    const followOn = async (viaUi = false, putInvite = true) => {
        if (viaUi) {
            doc.body.querySelector('.wr-trigger').onclick();
            const dialog = doc.body.querySelector('.wr-menu'), input = dialog.querySelector('[data-follow-invitation]');
            const check = dialog.querySelector('[data-follow-enabled]'); assert.equal(check.checked, false);
            input.value = encoded(token); input.oninput(); check.checked = true; check.onchange({ target: check });
        } else { if (putInvite) controller.setDraft(encoded(token)); await controller.setEnabled(true); }
        await until(() => relay() && calls.some(c => c.hub === relay() && c.method === 'BindSubscriber'), 'relay bind');
        await later();
    };
    const off = async () => { await controller.setEnabled(false); await later(); };
    t.after(async () => { if (controller.state().enabled) await off(); rooms.dispose(); timers.clear(); });
    return { ...ui, get, ctx, doc, evaluate, rooms, controller, stores, storageOps, calls, hubs, token, a, b, c, key, mainJoins, relay, status, followOn, off, save, logs, fire,
        setBinding(value, version = 1) { bindResult = { version, state: value === null ? null : JSON.stringify(value), demand: true }; },
        holdBinding() { const hold = deferred(); holdBind = hold; return value => { holdBind = null; hold.resolve(value); }; },
        holdMain(server) { const hold = { url: server + '/gateway', seen: deferred(), reply: deferred() }; holdJoin = hold; return hold; },
        ownerMounts: () => owners, timerCount: () => timers.size, mainReady: server => rooms.getMainContext()?.ready && rooms.getMainContext().serverUrl === server };
}

test('正式弹窗默认关闭；跨服务器真主房登录、临时视图、原未记住登录及房间簿无损恢复', async t => {
    const x = await fixture(t, { initial: snapshot('b', '__all_rooms__') });
    x.rooms.setView('room', x.key(B)); await later();
    const original = x.stores.local.get(BOOK), originalSource = x.rooms.sourceFor('fixture-member', x.key(A)), before = x.storageOps.length;
    assert.equal(x.relay(), undefined);
    await x.followOn(true); await until(() => x.mainReady(B), 'normal web main login to B');
    const bind = x.calls.find(c => c.hub === x.relay());
    assert.equal(bind.method, 'BindSubscriber'); assert.ok(bind.capabilityCorrect, 'only invitation read capability in bind');
    assert.equal(x.relay().opts.skipNegotiation, true); assert.equal(x.relay().opts.transport, 1); assert.equal(x.relay().logging, 6);
    assert.equal(x.relay().url, RELAY + '/room-follow'); assert.equal(x.mainJoins().at(-1).hub.url, B + '/gateway');
    assert.equal(x.mainJoins().at(-1).uid, x.evaluate('webSessionUid')); assert.ok(x.mainJoins().at(-1).loginCorrect);
    assert.notEqual(x.mainJoins().at(-1).uid, publisher.uid);
    assert.equal(x.rooms.getView().mode, 'all'); assert.equal(x.rooms.listProfiles().length, 3);
    assert.ok(x.get('password').value === x.a.password && x.get('rememberMe').checked === false, 'temporary login never replaces the original form');
    assert.equal(x.rooms.getContext(x.key(B)).displayName, '共享b'); assert.equal(x.rooms.getContext(x.key(C)).status, 'unauthenticated');
    assert.ok(x.rooms.listProfiles().every(p => !('password' in p))); assert.equal(x.rooms.getMember(originalSource), null);
    assert.equal(x.rooms.getLogin(x.key(A)).remember, false);
    assert.equal(x.stores.local.get(BOOK), original); assert.equal(x.storageOps.slice(before).filter(op => op[0] === 'local').length, 0);
    assert.match(x.get('roomSummary').textContent, /其余待确认/);
    assert.match(x.status(), /已使用本浏览器身份登录/); assert.match(x.status(), /其他待授权房间/);
    const followSource = x.rooms.sourceFor('fixture-member', x.key(B));
    await assert.rejects(x.rooms.sendRemoteCommand(followSource, { cmd: 'stop', commandId: 'stale', target: ['fixture-member'] }), /关闭房间跟随/);
    // 同房只切查看页不会重登；编排只使用共享元数据，不能挂载原 owner 查询器。
    const joins = x.mainJoins().length, owners = x.ownerMounts();
    x.relay().relayEvent(2, snapshot('b', 'a')); await later();
    assert.equal(x.rooms.getView().roomKey, x.key(A)); assert.equal(x.mainJoins().length, joins);
    x.relay().relayEvent(3, snapshot('b', 'b', { showOrchestration: true })); await later();
    assert.equal(x.rooms.getView().mode, 'orchestration'); assert.equal(x.get('roomOwnerView').hidden, false);
    assert.match(x.get('roomOwnerView').textContent, /任务进度未共享/); assert.equal(x.ownerMounts(), owners);
    await x.off(); assert.equal(x.mainReady(A), true); assert.equal(x.rooms.getView().roomKey, x.key(B));
    assert.equal(x.rooms.getContext(x.key(B)).displayName, '本地b'); assert.equal(x.rooms.getMember(followSource), null);
    assert.equal(x.stores.local.get(BOOK), original); assert.ok(x.mainJoins().at(-1).loginCorrect);
    assert.equal(x.stores.session.has('bgi.room.follow.interrupted.v1'), false);
    assert.ok(x.calls.every(c => c.hub.relay || ['session.hello','control.joinRoom','control.roomSnapshot'].includes(c.name)));
    assert.ok(x.logs.every(line => !line.includes(x.token.readSecret) && !line.includes(x.a.password) && !line.includes(x.b.password)));
});

test('新房间没有本地凭据时不连接；关闭跟随后普通授权再开启才能自动真实登录', async t => {
    const x = await fixture(t, { remember: true, initial: snapshot('c') });
    const original = x.stores.local.get(BOOK);
    await x.followOn(); await until(() => /登录待授权/.test(x.status()));
    assert.equal(x.evaluate('roomReady'), false); assert.equal(x.rooms.getDisplayedMembers().length, 0);
    assert.match(x.get('roomSummary').textContent, /人数待确认/);
    assert.equal(x.hubs.filter(h => h.url === C + '/gateway').length, 0); assert.equal(x.stores.local.get(BOOK), original);
    assert.throws(() => x.save(C, x.c, 'c'), /关闭房间跟随/);
    await x.off(); assert.equal(x.mainReady(A), true);
    const profile = x.save(C, x.c, 'c'); await later(); await x.rooms.requestSwitch(profile.roomKey); await later();
    assert.equal(x.mainReady(C), true);
    const authorizedBook = x.stores.local.get(BOOK);
    await x.followOn(false, false); await until(() => x.mainReady(C));
    assert.ok(x.mainJoins().at(-1).loginCorrect); assert.equal(x.stores.local.get(BOOK), authorizedBook);
});

test('绑定请求在途的更新胜过旧基线；重连前清空确认状态，服务器新代次可从较低版本开始', async t => {
    const x = await fixture(t), release = x.holdBinding();
    await x.followOn(); const relay = x.relay();
    relay.relayEvent(8, snapshot('b')); assert.equal(x.mainJoins().length, 1);
    release({ version: 7, state: JSON.stringify(snapshot('a')), demand: true });
    await until(() => x.mainReady(B)); assert.equal(x.mainJoins().length, 2);
    relay.relayEvent(6, snapshot('a')); await later(); assert.equal(x.mainReady(B), true);
    const old = x.rooms.sourceFor('fixture-member', x.key(B));
    relay.lost(); assert.equal(x.evaluate('roomReady'), false); assert.equal(x.rooms.getMember(old), null);
    relay.relayEvent(100, snapshot('a')); await later(); assert.equal(x.mainJoins().length, 2);
    x.setBinding(snapshot('a'), 1); relay.resumed(); await until(() => x.mainReady(A));
    assert.equal(x.calls.filter(c => c.hub === relay && c.method === 'BindSubscriber').length, 2);
    assert.equal(x.rooms.getMember(old), null);
});

test('快速切房与关闭期间迟到的主房 join 不复活原连接或修改浏览器记住信息', async t => {
    const x = await fixture(t), original = x.stores.local.get(BOOK), hold = x.holdMain(B);
    await x.followOn(); const old = await hold.seen.promise;
    x.relay().relayEvent(2, snapshot('a')); await until(() => x.mainReady(A));
    hold.reply.resolve(); await later(); assert.equal(x.mainReady(A), true); assert.ok(old.stops > 0);
    const second = x.holdMain(B); x.relay().relayEvent(3, snapshot('b')); const oldAgain = await second.seen.promise;
    const relay = x.relay(); const finishing = x.off(); second.reply.resolve(); await finishing;
    assert.equal(x.mainReady(A), true); assert.ok(oldAgain.stops > 0);
    relay.relayEvent(4, snapshot('b')); relay.resumed(); await later();
    assert.equal(x.mainReady(A), true); assert.equal(x.stores.local.get(BOOK), original);
    assert.equal(x.controller.state().enabled, false);
});

test('未确认、坏身份、撤回、重复复合身份及关闭中继均清除主登录，不能沿用上次确认', async t => {
    const x = await fixture(t); await x.followOn(); await until(() => x.mainReady(B));
    const relay = x.relay(); relay.relayEvent(2, snapshot('b', 'b', { confirmed: false })); await later();
    assert.equal(x.evaluate('roomReady'), false); assert.match(x.status(), /尚未确认/);
    relay.relayEvent(3, snapshot('b')); await until(() => x.mainReady(B));
    relay.relayEvent(4, snapshot('b', 'b', { instanceId: 'different' })); await later();
    assert.equal(x.evaluate('roomReady'), false); assert.match(x.status(), /身份不匹配/);
    relay.relayEvent(5, snapshot('b', 'b', { profiles: [pa, pb, { ...pb, id: 'duplicate' }] })); await later();
    assert.match(x.status(), /待授权/); assert.equal(x.evaluate('roomReady'), false);
    relay.relayEvent(6, snapshot('b')); await until(() => x.mainReady(B));
    relay.relayEvent(7, null); assert.equal(x.evaluate('roomReady'), false); assert.match(x.status(), /等待执行端/);
    assert.match(x.status(), /上次收到的元数据/);
    assert.equal(x.rooms.listProfiles().length, 3);
    assert.ok(x.rooms.listProfiles().every(p => !p.ready));
    assert.equal(x.rooms.getDisplayedMembers().length, 0);
    relay.relayEvent(8, snapshot('a')); await until(() => x.mainReady(A));
    relay.stop(); assert.equal(x.evaluate('roomReady'), false); assert.match(x.status(), /中继已断开/);
    assert.equal(x.rooms.getDisplayedMembers().length, 0);
});

test('保存的房间虽存在但原网页登录昵称/密码被拒，换视图或较新元数据不会不断重复登录', async t => {
    const x = await fixture(t, { skipSave: true });
    x.save(B, { ...x.b, nickname: 'invalid-nickname' }, 'b'); await later();
    await x.followOn(); await until(() => /登录待授权/.test(x.status()));
    const joins = x.mainJoins().length; assert.equal(x.mainJoins().at(-1).hub.url, B + '/gateway');
    assert.equal(x.mainJoins().at(-1).loginCorrect, false); assert.equal(x.evaluate('roomReady'), false);
    x.relay().relayEvent(2, snapshot('b', '__all_rooms__')); x.relay().relayEvent(3, snapshot('a')); await until(() => x.mainReady(A));
    x.relay().relayEvent(4, snapshot('b')); await later();
    assert.equal(x.mainJoins().length, joins + 1); assert.match(x.status(), /待授权/);
});

test('本机网页原先没有远程主房；跟随确认后关闭恢复原本机页，不把临时凭据记住', async t => {
    const x = await fixture(t, { local: true });
    const original = x.stores.local.get(BOOK);
    await x.followOn(); await until(() => x.mainReady(B)); assert.equal(x.mainJoins().length, 1);
    assert.equal(x.mainJoins()[0].hub.opts.withCredentials, false);
    await x.off(); assert.equal(x.evaluate('roomReady'), false); assert.equal(x.rooms.getMainContext(), null);
    assert.equal(x.get('roomInfo').textContent, '本机助手'); assert.equal(x.mainJoins().length, 1);
    assert.equal(x.stores.local.get(BOOK), original); assert.equal(x.get('password').value, '');
});

test('页面挂起停止跟随连接，恢复重新绑定；未记住的原登录重载只提示、不持久化凭据', async t => {
    const x = await fixture(t); await x.followOn(); await until(() => x.mainReady(B));
    const marker = x.stores.session.get('bgi.room.follow.interrupted.v1'); assert.equal(marker, 'unsaved');
    const old = x.relay(); x.fire('pagehide'); assert.equal(x.evaluate('roomReady'), false);
    old.relayEvent(99, snapshot('a')); assert.equal(x.evaluate('roomReady'), false);
    x.fire('pageshow'); await until(() => x.relay() !== old && x.mainReady(B));
    assert.ok(old.stops > 0);
    const y = await fixture(t, { skipLogin: true, skipSave: true, store: new Map(x.stores.local), sessionStore: new Map(x.stores.session) });
    assert.equal(y.controller.state().enabled, false); assert.equal(y.relay(), undefined);
    assert.match(y.get('loginError').textContent, /未记住/);
    assert.equal(y.stores.session.has('bgi.room.follow.interrupted.v1'), false);
    assert.ok(![...y.stores.local.values()].some(raw => raw.includes(x.token.readSecret) || raw.includes(x.a.password)));
});

test('目标网关普通重连事件清空旧成员并重新 hello/正常登录，旧代次的迟到结果不覆盖', async t => {
    const x = await fixture(t); await x.followOn(); await until(() => x.mainReady(B));
    const gateway = x.mainJoins().at(-1).hub, old = x.rooms.sourceFor('fixture-member', x.key(B));
    const retry = x.holdMain(B); gateway.lost(); assert.equal(x.evaluate('roomReady'), false);
    assert.match(x.get('roomSummary').textContent, /人数待确认/);
    gateway.resumed(); await retry.seen.promise;
    gateway.lost(); gateway.resumed(); await until(() => x.mainReady(B));
    const confirmed = x.rooms.sourceFor('fixture-member', x.key(B)); retry.reply.resolve(); await later();
    assert.equal(x.mainReady(B), true); assert.equal(x.rooms.getMember(old), null); assert.ok(x.rooms.getMember(confirmed));
    assert.equal(x.mainJoins().filter(c => c.hub === gateway).length, 3);
    assert.equal(x.calls.filter(c => c.hub === gateway && c.name === 'session.hello').length, 3);
    assert.equal(x.timerCount(), 0, 'stable follow uses no polling timer');
});

test('开启前已成功登录但当前正在自动重连，仍恢复原实际房间和未记住凭据', async t => {
    const x = await fixture(t); const old = x.mainJoins().at(-1).hub;
    old.lost(); assert.equal(x.evaluate('roomReady'), false);
    await x.followOn(); await until(() => x.mainReady(B));
    await x.off(); assert.equal(x.mainReady(A), true); assert.ok(x.mainJoins().at(-1).loginCorrect);
    assert.equal(x.get('rememberMe').checked, false);
});

test('原房间凭据被服务端拒绝时保留原表单与资料簿，关闭跟随不声称登录恢复', async t => {
    const x = await fixture(t), book = x.stores.local.get(BOOK), originalPassword = x.a.password;
    await x.followOn(); await until(() => x.mainReady(B));
    x.a.password = random(); await x.off();
    assert.equal(x.rooms.isFollowing(), false); assert.equal(x.evaluate('roomReady'), false);
    assert.equal(x.get('loginPanel').style.display, 'block'); assert.equal(x.get('controlPanel').style.display, 'none');
    assert.ok(x.get('password').value === originalPassword, 'original password remains in original form only');
    assert.match(x.get('loginError').textContent, /原控制房间登录尚未确认/);
    assert.match(x.status(), /连接未确认/); assert.ok(x.stores.local.get(BOOK) === book);
});

test('远程页面及助手内嵌网页均加载相同跟随脚本；内嵌资源逻辑名与实际路径一致', () => {
    for (const page of ['index.html','local-dodoco.html']) {
        const html = src(page);
        assert.match(html, /control-room-follow\.js\?v=20261126-room-follow/);
        assert.ok(html.indexOf('control-room-follow.js') < html.indexOf('control-room-integration.js'));
        assert.match(html, /control-room-profiles\.css\?v=20261126-room-follow/);
    }
    const project = fs.readFileSync(path.join(__dirname, '../../MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj'), 'utf8');
    assert.match(project, /EmbeddedResource Include="\.\.\\BgiCoordinatorServer\\wwwroot\\control-room-follow\.js" LogicalName="LocalBrowser\/control-room-follow\.js"/);
});
