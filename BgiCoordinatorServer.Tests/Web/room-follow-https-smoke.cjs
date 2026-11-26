// Isolated HTTPS reverse-proxy smoke for the real coordinator's room-follow hub.
// Requires a test-only certificate trusted by this Node process (NODE_EXTRA_CA_CERTS).
// No global certificate store changes, real rooms, or execution commands.
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { createHash, randomBytes } = require('node:crypto');
const { readFileSync, mkdirSync } = require('node:fs');
const https = require('node:https');
const net = require('node:net');
const path = require('node:path');
const { setTimeout: delay } = require('node:timers/promises');

async function until(description, check, timeout = 12000) {
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) { const value = check(); if (value) return value; await delay(20); }
    throw Error('Timed out: ' + description);
}
function hash(value) { return createHash('sha256').update(value).digest('hex'); }
function secret() { return randomBytes(32).toString('hex'); }
const frame = message => JSON.stringify(message) + '\x1e';

async function hub(url) {
    const socket = new WebSocket(url), replies = new Map(), events = [];
    let handshake = false, error, seq = 0;
    socket.addEventListener('open', () => socket.send(frame({ protocol: 'json', version: 1 })));
    socket.addEventListener('error', () => { error = Error('TLS or WebSocket connection failed'); });
    socket.addEventListener('message', ({ data }) => {
        try {
            for (const record of String(data).split('\x1e').filter(Boolean)) {
                const value = JSON.parse(record);
                if (!handshake) { assert.deepEqual(value, {}); handshake = true; }
                else if (value.type === 3) replies.set(value.invocationId, value);
                else if (value.type === 1) events.push(value);
            }
        } catch (e) { error = e; }
    });
    const wait = (description, check) => until(description, () => { if (error) throw error; return check(); });
    await wait('SignalR HTTPS handshake', () => handshake);
    return { events, async invoke(target, ...args) {
        const id = String(++seq);
        socket.send(frame({ type: 1, invocationId: id, target, arguments: args }));
        const reply = await wait('reply ' + target, () => replies.get(id));
        if (reply.error) throw Error('Hub rejected fixture ' + target);
        return reply.result;
    }, async close() {
        if (socket.readyState === WebSocket.CLOSED) return;
        socket.close(); await until('WebSocket close', () => socket.readyState === WebSocket.CLOSED);
    } };
}

(async () => {
    const { FOLLOW_SMOKE_SERVER_DLL: dll, FOLLOW_SMOKE_DATA_ROOT: root, FOLLOW_SMOKE_CERT: cert, FOLLOW_SMOKE_KEY: key } = process.env;
    assert.ok(dll && root && cert && key && process.env.NODE_EXTRA_CA_CERTS, 'Use the isolated server and test certificate inputs');
    mkdirSync(root, { recursive: true });
    const child = spawn('dotnet', [path.resolve(dll), '--contentRoot', path.resolve(root)], { cwd: root, windowsHide: true,
        stdio: ['ignore','pipe','pipe'], env: { ...process.env, ASPNETCORE_URLS: 'http://127.0.0.1:0',
            ROUTE_TRUSTED_PROXY_IPS: '127.0.0.1', ROOM_LOG_ROOT: path.join(root,'room-logs'),
            CONTROL_ROOM_REGISTRY_ROOT: path.join(root,'control-rooms'), LOCALAPPDATA: path.join(root,'local'), APPDATA: path.join(root,'appdata'),
            'Logging__LogLevel__Microsoft.Hosting.Lifetime': 'Information' } });
    let output = '', proxy, publisher, subscriber;
    const sockets = new Set();
    const ended = new Promise(resolve => child.once('close', resolve));
    for (const stream of [child.stdout, child.stderr]) stream.on('data', part => { output += part.toString(); });
    try {
        const address = new URL(await until('isolated coordinator', () => /Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/.exec(output)?.[1], 20000));
        proxy = https.createServer({ key: readFileSync(key), cert: readFileSync(cert) }, (_, response) => { response.writeHead(404); response.end(); });
        proxy.on('upgrade', (request, browser, head) => {
            // This HTTPS test proxy forwards only the new hub, with forwarding headers set by
            // the proxy itself and its loopback IP explicitly trusted by the coordinator.
            if (request.url !== '/room-follow') { browser.destroy(); return; }
            const upstream = net.connect(Number(address.port), address.hostname);
            sockets.add(browser); sockets.add(upstream);
            const end = () => { browser.destroy(); upstream.destroy(); };
            browser.on('error', end).on('close', () => { sockets.delete(browser); upstream.destroy(); });
            upstream.on('error', end).on('close', () => { sockets.delete(upstream); browser.destroy(); });
            upstream.once('connect', () => {
                const headers = { ...request.headers, host: address.host, 'x-forwarded-proto': 'https', 'x-forwarded-for': '127.0.0.1' };
                upstream.write('GET /room-follow HTTP/1.1\r\n' + Object.entries(headers).map(([k,v]) => `${k}: ${v}`).join('\r\n') + '\r\n\r\n');
                if (head.length) upstream.write(head);
                browser.pipe(upstream).pipe(browser);
            });
        });
        await new Promise(resolve => proxy.listen(0, '127.0.0.1', resolve));
        const url = `wss://127.0.0.1:${proxy.address().port}/room-follow`;
        const writeSecret = secret(), readSecret = secret();
        publisher = await hub(url); subscriber = await hub(url);
        assert.equal((await publisher.invoke('BindPublisher', writeSecret, hash(readSecret))).demand, false);
        assert.equal((await subscriber.invoke('BindSubscriber', readSecret, hash(writeSecret))).state, null);
        await until('subscriber demand', () => publisher.events.some(e => e.target === 'FollowDemand' && e.arguments[0] === true));
        const state = JSON.stringify({ version: 1, uid: 'fixture', instanceId: 'fixture-instance', machineId: 'fixture-machine', confirmed: true,
            profiles: [{ id: 'a', name: '测试房间', note: '', serverUrl: 'https://room.invalid', roomCode: 'A1B2C3', teamUids: ['fixture'], expectedHoeingPlayers: 4 }],
            activeId: 'a', viewedId: 'a', showOrchestration: false });
        await publisher.invoke('Publish', state);
        const initial = await until('HTTPS metadata', () => subscriber.events.find(e => e.target === 'FollowState' && e.arguments[1] === state));
        assert.ok(Number.isSafeInteger(initial.arguments[0]));
        await publisher.close(); publisher = null;
        await until('publisher disconnect invalidates metadata', () => subscriber.events.some(e => e.target === 'FollowState' && e.arguments[0] > initial.arguments[0] && e.arguments[1] === null));
        console.log('PASS: real WSS handshakes, trusted HTTPS reverse proxy, demand notification, metadata delivery and disconnect invalidation.');
    } finally {
        for (const client of [publisher, subscriber]) if (client) await client.close().catch(() => {});
        for (const socket of sockets) socket.destroy();
        if (proxy) await new Promise(resolve => proxy.close(resolve));
        if (child.exitCode === null) child.kill();
        await ended;
    }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
