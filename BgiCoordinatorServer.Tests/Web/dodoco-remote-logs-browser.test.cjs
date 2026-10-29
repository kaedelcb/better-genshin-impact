// 远程日志面板 + 协助房间：隔离的真实 /gateway，两个房间各一个模拟成员，网页走生产页面。
// Run after building BgiCoordinatorServer to DODOCO_SERVER_DLL; NODE_PATH must provide Playwright and jszip.
const { chromium } = require('playwright');
const { spawn } = require('node:child_process');
const { mkdtemp, mkdir, readFile, writeFile } = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const net = require('node:net');
const zlib = require('node:zlib');
const assert = require('node:assert/strict');
const JSZip = require('jszip');

async function freePort() {
  const server = net.createServer();
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  await new Promise(resolve => server.close(resolve));
  return port;
}
// 模拟成员：加入指定房间，按请求返回列表和 gzip 分块，并记录收到的任务命令。
async function addMember(page, { room, secret, uid, name, files }) {
  const today = new Date(); const pad = n => String(n).padStart(2, '0');
  const stamp = `${today.getFullYear()}-${pad(today.getMonth() + 1)}-${pad(today.getDate())}T10:00:00`;
  const bodies = Object.fromEntries(files.map(f => [f, zlib.gzipSync(Buffer.from(`[10:00:00.000] [INF] [Main] ${name} 的 ${f} 内容\n`)).toString('base64')]));
  await page.evaluate(async ({ room, secret, uid, name, files, stamp, bodies }) => {
    let counter = 0;
    const conn = new signalR.HubConnectionBuilder().withUrl('/gateway').build();
    const env = (type, n, payload) => ({ protocolVersion: 3, type, name: n, payload, requestId: uid + (++counter), sentAtUtc: new Date().toISOString() });
    const send = (n, payload) => conn.invoke('Dispatch', env('command', n, { roomCode: room, ...payload }));
    window.sim = { events: [], commands: [] };
    conn.on('evt', async e => {
      const x = e.payload || {}; window.sim.events.push(e.name);
      if (e.name === 'control.remoteCommand') window.sim.commands.push(x.command?.cmd);
      if (e.name === 'log.filesRequested')
        await send('log.reportFiles', { uid, requestId: x.requestId, files: files.map(f => ({ name: f, size: 64, lastWrite: stamp })) });
      if (e.name === 'log.downloadRequested')
        await send('log.reportChunk', { uid, requestId: x.requestId, seq: 0, totalChunks: 1, chunkBase64: bodies[x.fileName], fileName: x.fileName, done: true });
    });
    await conn.start();
    await conn.invoke('Query', env('query', 'session.hello', { clientKind: 'assistant', clientVersion: 'sim', protocolVersion: 3, capabilities: [] }));
    await send('control.joinRoom', { password: secret, playerUid: uid, playerName: name, allowedUids: [], isRemote: false, clientInstanceId: uid + '-inst' });
  }, { room, secret, uid, name, files, stamp, bodies });
}

(async () => {
  const exe = process.env.DODOCO_SERVER_DLL;
  if (!exe) throw new Error('DODOCO_SERVER_DLL must name the freshly built server DLL');
  const root = path.resolve(__dirname, '../../BgiCoordinatorServer');
  const isolated = await mkdtemp(path.join(os.tmpdir(), 'bgi-dodoco-remote-logs-'));
  const downloads = path.join(isolated, 'downloads'); await mkdir(path.join(isolated, 'room-logs')); await mkdir(downloads);
  const port = await freePort(), base = `http://127.0.0.1:${port}`;
  const server = spawn('dotnet', [exe, '--contentRoot', root], {
    env: { ...process.env, ASPNETCORE_URLS: base, ROOM_LOG_ROOT: path.join(isolated, 'room-logs'),
      NEXUSBGI_DATA_ROOT: path.join(isolated, 'user'), LOCALAPPDATA: path.join(isolated, 'localappdata') }, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
  });
  let serverLog = ''; server.stdout.on('data', d => serverLog += d); server.stderr.on('data', d => serverLog += d);
  let browser;
  try {
    const deadline = Date.now() + 15000;
    while (Date.now() < deadline) {
      if (server.exitCode !== null) throw new Error(`isolated server exited ${server.exitCode}: ${serverLog}`);
      try { if ((await fetch(base + '/health')).ok) break; } catch {}
      await new Promise(r => setTimeout(r, 100));
    }
    browser = await chromium.launch({ headless: true, executablePath: process.env.EDGE_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' });
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, acceptDownloads: true });
    const own = 'A' + Date.now().toString(36), other = 'B' + Date.now().toString(36);
    const simA = await context.newPage(), simB = await context.newPage(), page = await context.newPage();
    await simA.goto(base); await simB.goto(base);
    await simA.waitForFunction(() => typeof signalR !== 'undefined'); await simB.waitForFunction(() => typeof signalR !== 'undefined');
    await addMember(simA, { room: own, secret: 'own-secret', uid: 'own_member', name: '本房成员', files: ['own.log'] });
    await addMember(simB, { room: other, secret: 'other-secret', uid: 'other_member', name: '协助成员',
      files: ['better-genshin-impact20261030.log', 'assistant_runtime.2026-10-30.log'] });
    const errors = []; page.on('pageerror', e => errors.push(String(e)));
    await page.goto(base); await page.locator('#roomCode').fill(own); await page.locator('#password').fill('own-secret');
    await page.locator('#joinButton').click(); await page.locator('#dodocoProduct').click();
    await page.locator('[data-tab="files"]').click();

    // 本房间：入口 → 选成员 → 获取列表 → 在日志浏览中打开。
    await page.getByRole('button', { name: '远程日志', exact: true }).click();
    const dlg = page.locator('#rlDialog');
    await dlg.waitFor({ state: 'visible' });
    assert.match(await page.locator('#rlRoomLabel').textContent(), new RegExp('本房间：' + own));
    assert.equal(await page.locator('#rlReturn').isVisible(), false, '本房间时不显示返回本房间');
    await page.locator('#rlMember option[value="own_member"]').waitFor({ state: 'attached' });
    await page.locator('#rlMember').selectOption('own_member'); await page.locator('#rlFetch').click();
    await page.getByRole('button', { name: /own\.log/ }).click(); await page.locator('#rlOpen').click();
    await dlg.waitFor({ state: 'hidden' });
    await page.getByText('本房成员 的 own.log 内容').waitFor();

    // 协助房间：错误密码不切换来源。
    await page.getByRole('button', { name: '远程日志', exact: true }).click();
    await page.locator('#rlAssistToggle').click();
    await page.locator('#rlRoom').fill(other); await page.locator('#rlPassword').fill('wrong');
    await page.locator('#rlConnect').click();
    await page.waitForFunction(() => /拒绝|失败/.test(document.querySelector('#rlAssistStatus').textContent));
    assert.match(await page.locator('#rlRoomLabel').textContent(), /本房间/);
    assert.equal(await page.evaluate(() => JSON.parse(localStorage.getItem('bgi.dodoco.assistRooms.v1') || '[]').length), 0, '失败不记录');

    // 正确密码：只切换日志面板，本房间连接与成员保持。
    await page.locator('#rlPassword').fill('other-secret'); await page.locator('#rlConnect').click();
    await page.waitForFunction(r => document.querySelector('#rlRoomLabel').textContent.includes('协助房间：' + r), other);
    assert.equal(await page.locator('#rlAssistForm').isVisible(), false);
    assert.equal(await page.locator('#rlReturn').isVisible(), true);
    assert.equal(await page.evaluate(() => roomReady && roomCode), own, '本房间连接不受影响');
    assert.ok(await page.evaluate(() => players.some(p => p.playerUid === 'own_member')), '本房间成员列表保留');
    const bMembers = await simB.evaluate(() => window.sim.events.filter(n => n === 'control.playersUpdated').length);
    assert.ok(bMembers >= 1);
    await page.locator('#rlMember option[value="other_member"]').waitFor({ state: 'attached' });
    assert.equal(await page.locator('#rlMember option[value="own_member"]').count(), 0, '协助房间不列本房间成员');
    await page.locator('#rlMember').selectOption('other_member'); await page.locator('#rlFetch').click();
    await page.getByRole('button', { name: /better-genshin-impact20261030\.log/ }).click();
    const single = page.waitForEvent('download'); await page.locator('#rlDownload').click();
    const singleFile = await single; assert.equal(singleFile.suggestedFilename(), 'better-genshin-impact20261030.log');
    const singlePath = path.join(downloads, 'single.log'); await singleFile.saveAs(singlePath);
    assert.match(await readFile(singlePath, 'utf8'), /协助成员 的 better-genshin-impact20261030\.log 内容/);
    await page.getByText('协助成员 的 better-genshin-impact20261030.log 内容').waitFor();
    assert.match(await page.locator('#dodocoFileStatus').textContent(), /协助房间/);
    const saved = await page.evaluate(() => JSON.parse(localStorage.getItem('bgi.dodoco.assistRooms.v1')));
    assert.equal(saved.length, 1); assert.equal(saved[0].room, other);

    // 全房间当天日志按协助房间收集。
    await page.getByRole('button', { name: '远程日志', exact: true }).click();
    const batch = page.waitForEvent('download'); await page.locator('#roomLogStart').click();
    const zipFile = await batch; assert.match(zipFile.suggestedFilename(), new RegExp(other));
    const zipPath = path.join(downloads, 'room.zip'); await zipFile.saveAs(zipPath);
    const zip = await JSZip.loadAsync(await readFile(zipPath));
    const names = Object.keys(zip.files);
    assert.ok(names.some(n => n.endsWith('better-genshin-impact20261030.log')) && names.some(n => n.endsWith('assistant_runtime.2026-10-30.log')), names.join(','));
    assert.match(await zip.file('下载结果.txt').async('string'), /协助房间/);
    assert.equal(names.some(n => n.includes('own_member')), false, '批量不混入本房间成员');

    // 返回本房间，旁观连接释放；协助端成员未收到任何任务命令。
    await page.locator('#rlReturn').click();
    await page.waitForFunction(r => document.querySelector('#rlRoomLabel').textContent.includes('本房间：' + r), own);
    await page.locator('#rlMember option[value="own_member"]').waitFor({ state: 'attached' });
    assert.equal(await page.locator('#rlFiles .dodoco-file').count(), 0, '返回后不残留协助房间的文件列表');
    assert.equal(await page.locator('#rlDownload').isDisabled(), true);
    assert.equal(await simB.evaluate(() => window.sim.commands.length), 0);

    // 记录自动填入并可删除。
    await page.locator('#rlAssistToggle').click();
    assert.equal(await page.locator('#rlRoom').inputValue(), other);
    assert.equal(await page.locator('#rlPassword').inputValue(), 'other-secret');
    await page.locator('#rlForget').click();
    assert.equal(await page.evaluate(() => JSON.parse(localStorage.getItem('bgi.dodoco.assistRooms.v1')).length), 0);
    assert.equal(await page.locator('#rlHistoryRow').isVisible(), false);

    for (const width of [375, 1280]) {
      await page.setViewportSize({ width, height: 900 }); await page.waitForTimeout(100);
      const box = await dlg.boundingBox(); assert.ok(box && box.x >= 0 && box.x + box.width <= width + 1, 'dialog overflow at ' + width);
      if (process.env.DODOCO_SHOTS) await writeFile(path.join(process.env.DODOCO_SHOTS, `remote-logs-${width}.png`), await page.screenshot());
    }
    assert.deepEqual(errors, []);
    console.log('PASS remote logs panel: own room, assist room reject/accept, single download, room ZIP, return, history');
  } finally {
    await browser?.close();
    server.kill();
  }
})().catch(e => { console.error(e); process.exit(1); });
