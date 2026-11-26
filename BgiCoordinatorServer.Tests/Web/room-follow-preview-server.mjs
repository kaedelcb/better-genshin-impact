// Loopback-only browser UI fixture for the production room-follow page. All rooms,
// credentials and invitations below are synthetic. No outgoing real gateway or relay.
import http from 'node:http';
import { createHash, randomBytes } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../BgiCoordinatorServer/wwwroot');
const port = 8963;
const hash = value => createHash('sha256').update(value).digest('hex');
const readSecret = randomBytes(32).toString('hex'), writeHash = hash(randomBytes(32).toString('hex'));
const invitation = { version: 1, serverUrl: 'https://follow-preview.invalid', local: false, uid: 'preview-executor',
    instanceId: 'preview-instance', machineId: 'preview-machine', readSecret, writeHash, channelId: hash(writeHash + ':' + hash(readSecret)) };
const encoded = 'BGIFOLLOW1.' + Buffer.from(JSON.stringify(invitation)).toString('base64url');
const gateway = `(() => {
    const invitation = ${JSON.stringify(invitation)}, encoded = ${JSON.stringify(encoded)}, server = location.origin;
    const codes = ['ROOM-A', 'ROOM-B', 'ROOM-C'], credentials = ['fixture-a', 'fixture-b'];
    const names = ['本地甲房', '本地乙房'], shared = ['共享甲房', '共享乙房', '未认证新房'];
    const person = code => ({ playerUid: 'preview-executor', playerName: code === codes[0] ? '甲房成员' : '乙房成员',
        clientInstanceId: 'preview-instance', machineId: 'preview-machine', connectionId: 'fixture-' + code,
        online: true, taskStatusAvailable: true, taskRunning: false, bgiStatus: 'stopped', bgiVersion: 'fixture' });
    const full = code => ({ full: true, revision: 1, players: [person(code)] });
    const profile = (id, index) => ({ id, name: shared[index], note: '隔离模拟档案', serverUrl: server,
        roomCode: codes[index], teamUids: ['preview-executor'], expectedHoeingPlayers: 4 });
    const key = code => JSON.stringify([server, code]);
    if (!localStorage.getItem('web_hie_rooms.v1')) localStorage.setItem('web_hie_rooms.v1', JSON.stringify({ version: 1, legacyMigrated: true,
        lastLoginKey: key(codes[0]), view: { mode: 'room', roomKey: key(codes[0]) }, profiles: [0,1].map(i => ({
            profileId: 'local-' + i, serverUrl: server, roomCode: codes[i], name: names[i], note: '', remember: true,
            password: credentials[i], nickname: '预览监控端' })) }));
    let version = 1, active = 1, viewed = 1, confirmed = true, subscribed = null;
    const snapshot = () => JSON.stringify({ version: 1, uid: invitation.uid, instanceId: invitation.instanceId,
        machineId: invitation.machineId, confirmed, profiles: [0,1,2].map(i => profile('shared-' + i, i)),
        activeId: 'shared-' + active, viewedId: viewed === -1 ? '__all_rooms__' : 'shared-' + viewed, showOrchestration: false });
    window.__roomFollowPreview = { invitation: encoded, select(index, view = index, ready = true) {
        active = index; viewed = view; confirmed = ready; subscribed?.callbacks.FollowState?.(++version, snapshot());
    } };
    class Hub {
        constructor(url) { this.url = url; this.callbacks = {}; this.state = 'Disconnected'; this.connectionId = 'fixture-browser'; }
        on(name, handler) { this.callbacks[name] = handler; }
        onreconnecting(handler) { this.reconnecting = handler; }
        onreconnected(handler) { this.reconnected = handler; }
        onclose(handler) { this.closed = handler; }
        start() { this.state = 'Connected'; return Promise.resolve(); }
        stop() { this.state = 'Disconnected'; if (subscribed === this) subscribed = null; this.closed?.(); return Promise.resolve(); }
        async invoke(method, ...args) {
            if (this.url === invitation.serverUrl + '/room-follow') {
                if (method !== 'BindSubscriber' || args[0] !== invitation.readSecret || args[1] !== invitation.writeHash) throw Error('fixture binding rejected');
                subscribed = this; return { version, state: snapshot(), demand: true };
            }
            const e = args[0], p = e.payload || {}; let payload = { ack: true };
            if (e.name === 'session.hello') payload = { protocolVersion: 3, capabilities: [] };
            if (e.name === 'control.joinRoom') {
                this.code = p.roomCode; const i = codes.indexOf(this.code);
                if (i < 0 || credentials[i] !== p.password) payload = { success: false, reason: 'fixture rejected' };
                else { payload = { success: true, playersUpdate: full(this.code) }; this.callbacks.evt?.({ name:'control.playersUpdated', payload: payload.playersUpdate }); }
            }
            if (e.name === 'control.roomSnapshot') payload = { success: true, playersUpdate: full(this.code) };
            return { name: e.name, requestId: e.requestId, payload };
        }
    }
    class Builder { withUrl(url) { this.url = new URL(url, server).href; return this; } configureLogging() { return this; }
        withAutomaticReconnect() { return this; } build() { return new Hub(this.url); } }
    window.signalR = { HubConnectionBuilder: Builder, HttpTransportType: { WebSockets: 1 }, LogLevel: { None: 6 } };
})();`;

http.createServer(async (req, res) => {
    try {
        const pathname = new URL(req.url, 'http://127.0.0.1:' + port).pathname;
        const send = (contentType, body) => { res.writeHead(200, { 'Content-Type': contentType, 'Cache-Control': 'no-store' }); res.end(body); };
        if (pathname === '/') return send('text/html; charset=utf-8', (await readFile(path.join(root, 'index.html'), 'utf8')).replace('src="signalr.min.js"', 'src="/fixture-gateway.js"'));
        if (pathname === '/fixture-gateway.js') return send('text/javascript; charset=utf-8', gateway);
        if (!/^\/(?:[a-z0-9-]+\.(?:js|css)|gengdi-avatars\/[a-z0-9-]+\.png)$/i.test(pathname)) { res.writeHead(404); return res.end(); }
        const type = pathname.endsWith('.png') ? 'image/png' : pathname.endsWith('.css') ? 'text/css; charset=utf-8' : 'text/javascript; charset=utf-8';
        return send(type, await readFile(path.join(root, pathname.slice(1))));
    } catch { res.writeHead(404); res.end(); }
}).listen(port, '127.0.0.1', () => console.log('Room follow browser fixture listening on loopback.'));
