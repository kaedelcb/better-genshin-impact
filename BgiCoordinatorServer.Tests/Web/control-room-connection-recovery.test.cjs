// node control-room-connection-recovery.test.cjs http://127.0.0.1:<port>
// Or provide RECOVERY_SERVER_DLL and RECOVERY_DATA_ROOT to run an isolated server.
// All actors use synthetic identities and only record commands; none execute them.
const assert = require('node:assert/strict');
const { randomUUID } = require('node:crypto');
const { spawn } = require('node:child_process');
const { mkdir, writeFile } = require('node:fs/promises');
const path = require('node:path');
const { setTimeout: sleep } = require('node:timers/promises');

async function until(label, read, timeout = 7000) {
  const end = Date.now() + timeout;
  do {
    const value = read();
    if (value) return value;
    await sleep(15);
  } while (Date.now() < end);
  throw new Error('Timed out waiting for ' + label);
}

const clients = [];
async function connect(base, label) {
  const negotiation = await fetch(base + '/gateway/negotiate?negotiateVersion=1', {
    method: 'POST', redirect: 'error', signal: AbortSignal.timeout(5000)
  });
  assert.ok(negotiation.ok, 'gateway negotiate: ' + negotiation.status);
  const { connectionId, connectionToken } = await negotiation.json();
  assert.ok(connectionId && connectionToken);
  const url = new URL('/gateway', base);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  url.searchParams.set('id', connectionToken);
  const ws = new WebSocket(url);
  const events = [], players = new Map(), completions = new Map();
  let handshake, error, revision = 0, seq = 0;
  const send = value => ws.send(JSON.stringify(value) + '\x1e');
  const wait = (what, read) => until(label + ': ' + what, () => {
    if (error) throw error;
    return read();
  });
  ws.onopen = () => send({ protocol: 'json', version: 1 });
  ws.onerror = () => { error = new Error(label + ': websocket error'); };
  ws.onclose = e => { error ??= new Error(label + ': websocket closed: ' + e.code); };
  ws.onmessage = ({ data }) => {
    try {
      for (const record of data.split('\x1e').filter(Boolean)) {
        const msg = JSON.parse(record);
        if (!handshake) {
          assert.deepEqual(msg, {});
          handshake = true;
        } else if (msg.type === 3) {
          completions.set(msg.invocationId, msg);
        } else if (msg.type === 1 && msg.target.toLowerCase() === 'evt') {
          const evt = msg.arguments[0];
          events.push(evt);
          if (evt.name === 'control.playersUpdated' && evt.payload.revision > revision) {
            const p = evt.payload;
            revision = p.revision;
            if (p.full) players.clear();
            for (const uid of p.removed || []) players.delete(uid);
            for (const player of (p.full ? p.players : p.changed) || []) players.set(player.playerUid, player);
          }
        }
      }
    } catch (e) { error = e; }
  };
  async function invoke(target, name, payload) {
    const id = String(++seq);
    send({ type: 1, invocationId: id, target, arguments: [{
      protocolVersion: 3, type: target === 'Query' ? 'query' : 'command', name, payload,
      requestId: randomUUID(), sentAtUtc: new Date().toISOString()
    }] });
    const result = await wait(name, () => completions.get(id));
    completions.delete(id);
    assert.ifError(result.error ? Error(result.error) : null);
    const response = result.result;
    assert.equal(response.name, name);
    assert.ok(!response.payload?.error, JSON.stringify(response.payload));
    return response.payload;
  }
  const client = { label, events, players, wait, connectionId, close: () => ws.close(),
    cmd: (name, payload) => invoke('Dispatch', name, payload),
    query: (name, payload) => invoke('Query', name, payload) };
  clients.push(client);
  await wait('SignalR handshake', () => handshake);
  await client.query('session.hello', { protocolVersion: 3, clientKind: 'assistant', clientVersion: 'smoke', capabilities: [] });
  return client;
}

async function scenario(base) {
  const token = randomUUID(), roomCode = 'test-' + token, password = randomUUID();
  const uid = 'executor-' + token, otherUid = 'independent-' + token, remoteUid = 'observer-' + token;
  const instance = randomUUID(), machineId = randomUUID(), allowedUids = [uid, otherUid, remoteUid];
  const join = (p, playerUid, clientInstanceId, isRemote = false) => p.cmd('control.joinRoom', {
    roomCode, password, allowedUids, playerUid, playerName: p.label, clientInstanceId, machineId, isRemote
  });
  const report = (p, playerUid, task, progress) => p.cmd('control.reportStatus', { status: {
    roomCode, playerUid, bgiStatus: 'running', taskStatusAvailable: true, taskRunning: true,
    currentTaskName: task, scriptTaskProgress: progress
  } });
  const obs = await connect(base, 'observer'), a = await connect(base, 'A');
  const b = await connect(base, 'B'), c = await connect(base, 'independent');
  async function command(target, cmd, expected) {
    const id = randomUUID();
    await obs.cmd('control.sendCommand', { command: {
      roomCode, commandId: id, senderUid: remoteUid, sender: obs.label, target: [target],
      cmd, params: { key: 'smoke-only' }
    } });
    const ack = await obs.wait('ack for ' + cmd, () => obs.events.find(
      e => e.name === 'control.remoteCommandAck' && e.payload.commandId === id));
    assert.equal(ack.payload.deliveredTo, expected);
    return id;
  }
  const member = (id, predicate) => obs.wait('member ' + id, () => predicate(obs.players.get(id)));
  const received = p => p.events.filter(e => e.name === 'control.remoteCommand').map(e => e.payload.command.commandId);
  await join(obs, remoteUid, randomUUID(), true);
  await join(a, uid, instance);
  await report(a, uid, 'original route', 'step 1');
  await member(uid, p => p?.connectionId === a.connectionId && p.taskRunning && p.scriptTaskProgress === 'step 1');
  await join(c, otherUid, randomUUID());
  await report(c, otherUid, 'independent route', 'unchanged');
  await join(b, uid, instance);
  await report(b, uid, 'duplicate marker', 'B owns registration');
  await member(uid, p => p?.connectionId === b.connectionId && p.currentTaskName === 'duplicate marker');
  await report(a, uid, 'original route', 'step 2');
  const before = await command(uid, 'recovery_probe', 1);
  await b.wait('probe still routed to B', () => received(b).includes(before));
  assert.equal(obs.players.get(uid).currentTaskName, 'duplicate marker');
  b.close();
  await member(uid, p => p?.online === false);
  const cached = await command(uid, 'start_group', 0);
  await report(a, uid, 'original route', 'step 3');
  await member(uid, p => p?.online && p.connectionId === a.connectionId && p.taskRunning &&
    p.currentTaskName === 'original route' && p.scriptTaskProgress === 'step 3');
  const after = await command(uid, 'recovery_probe', 1);
  const parallel = await command(otherUid, 'recovery_probe', 1);
  await a.wait('probe after recovery', () => received(a).includes(after));
  await c.wait('independent probe', () => received(c).includes(parallel));
  assert.equal(obs.players.get(otherUid)?.connectionId, c.connectionId);
  assert.equal(obs.players.get(otherUid)?.scriptTaskProgress, 'unchanged');
  // Per-connection round trips drain earlier server deliveries; no quiet-window sleep.
  await Promise.all([a, c, obs].map(p => p.cmd('control.batch.current', {})));
  assert.deepEqual(received(a), [after]);
  assert.deepEqual(received(b), [before]);
  assert.deepEqual(received(c), [parallel]);
  assert.deepEqual(received(obs), []);
  for (const p of clients) {
    assert.ok(!received(p).includes(cached), p.label + ': replayed cached start');
    assert.ok(!p.events.some(e => ['control.batch.start', 'control.allReady', 'control.allReadyConfirm'].includes(e.name)),
      p.label + ': unexpected batch/ready event');
  }
}

async function main() {
  let base = process.argv[2], child, exited, log = '', dataRoot;
  try {
    if (!base) {
      const { RECOVERY_SERVER_DLL: dll, RECOVERY_DATA_ROOT: root } = process.env;
      assert.ok(dll && root, 'Provide a loopback server URL or both RECOVERY_SERVER_DLL and RECOVERY_DATA_ROOT');
      dataRoot = path.resolve(root);
      await mkdir(dataRoot, { recursive: true });
      child = spawn('dotnet', [path.resolve(dll), '--contentRoot', dataRoot], {
        cwd: dataRoot, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
        env: { ...process.env, ASPNETCORE_URLS: 'http://127.0.0.1:0', ROOM_LOG_ROOT: path.join(dataRoot, 'room-logs'),
          LOCALAPPDATA: path.join(dataRoot, 'localappdata'), APPDATA: path.join(dataRoot, 'appdata'),
          'Logging__LogLevel__Microsoft.Hosting.Lifetime': 'Information' }
      });
      let ended = false, startupError;
      exited = new Promise(resolve => child.once('close', () => { ended = true; resolve(); }));
      child.once('error', e => { startupError = e; });
      for (const stream of [child.stdout, child.stderr]) stream.on('data', chunk => { log += chunk.toString(); });
      base = await until('isolated server listen address', () => {
        if (startupError) throw startupError;
        if (ended) throw Error('Isolated server exited during startup');
        return /Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)(?:\s|$)/.exec(log)?.[1];
      }, 20000);
    }
    const url = new URL(base);
    assert.ok(['http:', 'https:'].includes(url.protocol) && ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname),
      'Use an isolated loopback coordinator');
    await scenario(url.origin);
    console.log('PASS: original task progress recovered on its next report; no cached start or batch replay; targeted and independent routes correct.');
  } catch (error) {
    if (dataRoot) {
      const file = path.join(dataRoot, 'recovery-server-' + randomUUID() + '.log');
      await writeFile(file, log, 'utf8');
      console.error('Isolated server output:', file, '\n' + log.slice(-7000));
    }
    throw error;
  } finally {
    for (const p of clients) p.close();
    if (child) { child.kill(); await exited; }
  }
}
main().catch(e => { console.error(e); process.exitCode = 1; });
