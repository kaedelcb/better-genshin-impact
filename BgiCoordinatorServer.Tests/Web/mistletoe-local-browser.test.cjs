// 槲寄生 · 本机直连：本机网页经 /api/pair + /api/execute 调用模拟本机桥（不连房间、不走服务器）。
// 覆盖：免授权、mistletoe.read/request 路由、写入不被只读复用、超时与桥 unknown 按结果未确认处理、
// 本机会话失效后不改走服务器。验证的是网页与本机桥协议；本机桥后面的执行端为协议级模拟。
const http = require('node:http'), fs = require('node:fs'), path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../../BgiCoordinatorServer/wwwroot');
const mock = `window.remoteCalls=[];window.signalR={HubConnectionBuilder:class{withUrl(){return this}withAutomaticReconnect(){return this}build(){return {on(){},onreconnecting(){},onreconnected(){},onclose(){},start:async()=>{},stop:async()=>{},invoke:async(m,e)=>{remoteCalls.push(e);return {payload:{}};}}}}};`;
function body(req) { return new Promise((resolve, reject) => { let v = ''; req.on('data', c => v += c); req.on('end', () => { try { resolve(JSON.parse(v)); } catch (e) { reject(e); } }); req.on('error', reject); }); }
(async () => {
  const calls = [];
  const state = { mode: 'ok', startup: { enabled: false, delaySeconds: 5, autoExpandBranch: true, steps: [] }, revision: 'r1', expired: false, ops: new Map() };
  const identity = { uid: 'owner-uid', name: '本机助手', clientInstanceId: 'inst-local', machineId: 'm-local', serverUrl: 'http://127.0.0.1:1', roomCode: 'room', isObserver: false };
  function center(req) {
    const op = req.operation, id = 'web-' + req.requestId;
    const reply = (status, message, data, revision, operationId) => ({ status, message: message || '', data: data ?? null, revision: revision ?? null, operationId: operationId ?? null, targetInstanceId: 'inst-local' });
    if (op !== 'web.identity' && req.targetInstanceId !== 'inst-local') return reply('rejected', '执行端实例已变化');
    if (req.browserSecret) return reply('rejected', '本机请求不应携带浏览器凭据');
    switch (op) {
      case 'web.identity': return reply('ok', '', { uid: 'owner-uid', name: '本机助手', instanceId: 'inst-local', isExecutor: true, owner: true });
      case 'startup.get': return reply('ok', '', { config: state.startup, revision: state.revision, schemes: [], schemesRevision: 's1', IsRunning: false, StatusText: '', autoLaunchOnBoot: false, autoLaunchOnBootModeIndex: 0, autoLaunchRevision: 'a1', triggers: [], prompts: [], nodeStates: [] }, state.revision);
      case 'startup.status': return reply('ok', '', { isRunning: false, statusText: '', triggers: [], prompts: [], nodeStates: [] });
      case 'startup.save': if (req.expectedRevision !== state.revision) return reply('rejected', '修订已变化'); state.startup = req.payload; state.revision = 'r' + Date.now(); state.ops.set(id, reply('ok', '已保存', null, state.revision, id)); return state.ops.get(id);
      case 'startup.run': state.ops.set(id, reply('accepted', '已受理', null, null, id)); setTimeout(() => state.ops.set(id, reply('ok', '启动流程已执行完成', null, null, id)), 150); return state.ops.get(id);
      case 'operation.get': return state.ops.get(req.payload) || reply('rejected', '执行端找不到这次操作');
      default: return reply('rejected', '不支持 ' + op);
    }
  }
  const server = http.createServer(async (req, res) => {
    try {
      const u = new URL(req.url, 'http://127.0.0.1');
      if (u.pathname === '/api/pair') { await body(req); res.setHeader('Content-Type', 'application/json'); return res.end(JSON.stringify({ sessionId: 'local-session', instanceId: 'bridge-1' })); }
      if (u.pathname === '/api/execute') {
        const payload = await body(req); calls.push(payload);
        if (state.expired) { res.writeHead(401); return res.end(); }
        res.setHeader('Content-Type', 'application/json');
        if (payload.operation === 'local.identity') return res.end(JSON.stringify({ requestId: payload.requestId, instanceId: 'bridge-1', status: 'ok', data: identity }));
        if (payload.operation === 'mistletoe.read' || payload.operation === 'mistletoe.request') {
          if (state.mode === 'hang') return; // 超时：浏览器侧中止请求
          if (state.mode === 'unknown') return res.end(JSON.stringify({ requestId: payload.requestId, instanceId: 'bridge-1', status: 'unknown', message: '本机操作尚未确认，请查询状态，不要重复执行', data: null }));
          return res.end(JSON.stringify({ requestId: payload.requestId, instanceId: 'bridge-1', status: 'ok', data: center(payload.args.request) }));
        }
        return res.end(JSON.stringify({ requestId: payload.requestId, instanceId: 'bridge-1', status: 'ok', data: {} }));
      }
      const rel = u.pathname === '/' ? 'local-dodoco.html' : u.pathname.slice(1);
      if (!/^(local-dodoco\.(html|js|css)|project-star\.css|control-room(?:-commands|-ui)?\.(js|css)|dodoco-(?:web\.(js|css)|log-worker\.js|operations\.js)|mistletoe-(?:core|startup|startup-view|taskcenter|web)\.js|mistletoe-web\.css|signalr\.min\.js|gengdi-avatars\/[A-Za-z]+\.png)$/.test(rel)) { res.writeHead(404); return res.end(); }
      res.setHeader('Content-Type', rel.endsWith('.js') ? 'text/javascript' : rel.endsWith('.css') ? 'text/css' : rel.endsWith('.png') ? 'image/png' : 'text/html');
      res.end(rel === 'signalr.min.js' ? fs.readFileSync(path.join(root, rel), 'utf8') + '\n;' + mock : fs.readFileSync(path.join(root, rel)));
    } catch (e) { res.writeHead(500); res.end(String(e)); }
  });
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  const browser = await chromium.launch({ headless: true, executablePath: process.env.EDGE_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 860 } }); const errors = []; page.on('pageerror', e => errors.push(e.message));
    await page.goto(`http://127.0.0.1:${server.address().port}/#token=pair-token`);
    await page.locator('#mistletoeProduct').waitFor({ state: 'visible' });
    await page.locator('#mistletoeProduct').click();
    await page.locator('#mtRunCard').waitFor({ timeout: 8000 });
    const opts = await page.locator('#mtTarget option').allTextContents();
    assert.ok(opts.some(t => t.includes('本机直连')), '本机执行端作为本机直连目标');
    assert.equal(await page.locator('.mt-auth').count(), 0, '本机直连免授权');
    assert.equal(await page.evaluate(() => remoteCalls.length), 0, '本机直连不连服务器');
    const reads = calls.filter(c => c.operation === 'mistletoe.read').map(c => c.args.request.operation);
    assert.ok(reads.includes('web.identity') && reads.includes('startup.get'), '只读请求走 mistletoe.read');
    // 写入走 mistletoe.request，且每次独立认领编号。
    await page.locator('#mtDelay').fill('11'); await page.locator('#mtDelay').dispatchEvent('change');
    await page.locator('#mtSaveState').getByText('已与执行端同步').waitFor({ timeout: 8000 });
    const save = calls.find(c => c.operation === 'mistletoe.request' && c.args.request.operation === 'startup.save');
    assert.ok(save && save.args.request.expectedRevision === 'r1' && state.startup.delaySeconds === 11);
    // 运行：受理后经 operation.get 查到终态。
    await page.locator('#mtRunNow').click();
    await page.locator('#mtOpStage[data-stage="done"]').waitFor({ timeout: 8000 });
    // 本机桥返回 unknown（30 秒内未完成）：结果未确认，登记台账，不重发。
    state.mode = 'unknown';
    const unknown = await page.evaluate(() => mtOperate('startup.run', undefined, { label: '本机未确认的运行' }));
    assert.equal(unknown.status, 'unknown');
    await page.locator('#mtPending').getByText('本机未确认的运行', { exact: false }).waitFor();
    const blocked = await page.evaluate(() => mtOperate('startup.run', undefined, { label: '再次运行' }));
    assert.equal(blocked.status, 'blocked');
    // 超时：同样按结果未确认处理，不当作「确定未执行」。
    state.mode = 'hang';
    const hang = await page.evaluate(() => mtSend('startup.status', undefined, { timeout: 600 }).then(() => 'ok', e => (e.unconfirmed ? 'unconfirmed' : 'plain') + ':' + e.message));
    assert.match(hang, /^unconfirmed:/);
    state.mode = 'ok';
    page.once('dialog', d => d.accept()); await page.locator('#mtAckPending').click();
    await page.locator('#mtPending .mt-pending').waitFor({ state: 'detached' });
    // 本机会话失效：确定未执行（请求在执行前被拒），页面提示从助手重新打开，不改走服务器。
    state.expired = true;
    const expired = await page.evaluate(() => mtOperate('startup.save', {}, { label: '会话失效后的保存' }));
    assert.equal(expired.status, 'rejected');
    await page.locator('.mt-offline').getByText(/本机连接已断开/).waitFor({ timeout: 5000 });
    assert.equal(await page.evaluate(() => remoteCalls.length), 0, '本机失效后不改走服务器');
    assert.equal(await page.evaluate(() => mt.unconfirmed), null, '确定未执行的请求不留台账');
    for (const width of [390, 1280]) { await page.setViewportSize({ width, height: 860 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, width + 'px 横向溢出'); }
    assert.deepEqual(errors, []);
    console.log('PASS mistletoe local direct: pairing, no authorization, read/request routing, save revision, run terminal, unknown/timeout unconfirmed, expired session');
  } finally { await browser.close(); await new Promise(r => server.close(r)); }
})().catch(e => { console.error(e); process.exitCode = 1; });
