// 槲寄生网页 · 隔离端到端：真实协调服务器（隔离数据根）+ 真实网页 + 模拟执行端（真实 SignalR 网关往返）。
// 运行：MISTLETOE_SERVER_DLL=<新构建的服务端 DLL>，NODE_PATH 提供 playwright；可选 MISTLETOE_SHOTS=<截图目录>。
// 验证的是网页与网关协议；执行端是协议级模拟，不运行真实启动流程、BGI 或游戏。
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const { spawn } = require('node:child_process');
const { mkdtemp, mkdir, writeFile, readFile } = require('node:fs/promises');
const path = require('node:path'), os = require('node:os'), net = require('node:net');
const assert = require('node:assert/strict');
const installSimExecutor = require('./mistletoe-sim-executor.cjs');

async function freePort() { const s = net.createServer(); await new Promise(r => s.listen(0, '127.0.0.1', r)); const p = s.address().port; await new Promise(r => s.close(r)); return p; }
const LONG = '很长的节点名称用于检查折行'.repeat(6);
/** 大流程：数百节点、多层条件嵌套、触发链，每个分支都有内容。 */
function bigFlow(id = { n: 0 }) {
  const step = (kind, extra = {}) => ({ id: 's' + (++id.n), nodeType: ['timeRange', 'weekday', 'bgiRunning', 'gameRunning', 'processRunning', 'bgiTaskRunning', 'bgiTaskName', 'manualConfirm'].includes(kind) ? 'condition' : 'action', kind, name: '', enabled: true, trueSteps: [], falseSteps: [], fireSteps: [], expectRunning: true, processName: 'notepad', timeStart: '08:00', timeEnd: '23:00', weekdays: [1, 2, 3, 4, 5, 6, 7], waitSeconds: 5, triggerTime: '04:00', watchKind: 'bgiTaskRunning', watchIntervalSeconds: 30, watchRepeat: true, watchConfirmSeconds: 1, watchConfirmTimes: 1, statusSource: 'currentSession', statusTargetOrder: 1, taskCenterHandoffMode: 'start', ...extra });
  const steps = [];
  for (let g = 0; g < 18; g++) {
    const cond = step(g % 3 ? 'bgiRunning' : 'timeRange', { name: g === 2 ? LONG : '' });
    for (let i = 0; i < 5; i++) cond.trueSteps.push(step(i % 2 ? 'wait' : 'startProgram', { path: 'D:\\Programs\\' + 'VeryLongFolderName\\'.repeat(3) + 'app' + i + '.exe' }));
    const inner = step('processRunning'); inner.trueSteps.push(step('killProgram')); inner.falseSteps.push(step('runCmd', { arguments: 'echo ' + 'x'.repeat(200) }));
    cond.falseSteps.push(inner, step('wait'), step('stopBgi'));
    steps.push(cond, step('wait'));
  }
  const timer = step('timerTrigger'); for (let i = 0; i < 4; i++) timer.fireSteps.push(step('wait'));
  steps.push(timer, step('startBgi'), step('enterTaskCenter', { taskCenterFlowId: 'wf-sim' }));
  return { enabled: false, delaySeconds: 5, autoExpandBranch: true, steps };
}

const COND = ['timeRange', 'weekday', 'bgiRunning', 'gameRunning', 'processRunning', 'bgiTaskRunning', 'bgiTaskName', 'manualConfirm'];
function st(id, kind, extra = {}) { return { id, nodeType: COND.includes(kind) ? 'condition' : 'action', kind, name: '', enabled: true, trueSteps: [], falseSteps: [], fireSteps: [], expectRunning: true, processName: 'notepad', timeStart: '08:00', timeEnd: '23:00', weekdays: [1, 2, 3, 4, 5, 6, 7], waitSeconds: 5, triggerTime: '04:00', watchKind: 'bgiTaskRunning', watchIntervalSeconds: 30, watchRepeat: true, watchConfirmSeconds: 1, watchConfirmTimes: 1, statusSource: 'currentSession', statusTargetOrder: 1, taskCenterHandoffMode: 'start', ...extra }; }
/** 普通规模流程：条件分支（含结束流程）、人工确认、定时触发器、进入任务中心，共 11 个节点。 */
function normalFlow() {
  const c1 = st('c1', 'timeRange', { name: '白天才启动' }); c1.trueSteps.push(st('g1', 'startGame', { path: '' }), st('w1', 'wait', { waitSeconds: 10 })); c1.falseSteps.push(st('e1', 'endFlow'));
  const c2 = st('c2', 'manualConfirm', { confirmMessage: '是否继续执行今天的任务？', confirmTimeoutSeconds: 60, confirmTimeoutGoTrue: true }); c2.trueSteps.push(st('b1', 'startBgi')); c2.falseSteps.push(st('w2', 'wait'));
  const t1 = st('t1', 'timerTrigger', { name: '凌晨刷新', repeatDaily: true }); t1.fireSteps.push(st('w3', 'wait', { waitSeconds: 30 }));
  return { enabled: true, delaySeconds: 5, autoExpandBranch: true, steps: [c1, c2, t1, st('k1', 'killProgram', { processName: 'YuanShen' }), st('x1', 'enterTaskCenter', { taskCenterFlowId: 'wf-sim' })] };
}
/** DOM 量测：横向溢出、缩放条与图例相交、同一操作行按钮重叠、可换行文本被截断、打开的对话框超出视口。 */
async function layoutAudit(page, name) {
  const r = await page.evaluate(() => {
    const vis = e => e && e.getClientRects().length && getComputedStyle(e).display !== 'none' && getComputedStyle(e).visibility !== 'hidden';
    const hit = (a, b) => a.left < b.right - 1 && b.left < a.right - 1 && a.top < b.bottom - 1 && b.top < a.bottom - 1;
    const out = { overflow: document.documentElement.scrollWidth > innerWidth, overlaps: [], clipped: [], dialog: true };
    const z = document.querySelector('#mtStage .mt-zoom'), l = document.querySelector('#mtStage .mt-legend');
    if (vis(z) && vis(l) && hit(z.getBoundingClientRect(), l.getBoundingClientRect())) out.overlaps.push('缩放条/图例');
    for (const row of document.querySelectorAll('#mistletoeView .dd-actions, dialog[open] .dd-actions, dialog[open] .tc-dialog-foot')) {
      const bs = [...row.children].filter(vis).map(x => x.getBoundingClientRect());
      for (let i = 0; i < bs.length; i++) for (let j = i + 1; j < bs.length; j++) if (hit(bs[i], bs[j])) out.overlaps.push('操作行：' + row.textContent.trim().slice(0, 30));
    }
    for (const e of document.querySelectorAll('#mistletoeView .mt-break, dialog[open] .mt-break')) if (vis(e) && e.scrollWidth > e.clientWidth + 1) out.clipped.push(e.textContent.trim().slice(0, 30));
    const d = document.querySelector('dialog[open]');
    if (d) { const b = d.getBoundingClientRect(); out.dialog = b.left >= -1 && b.top >= -1 && b.right <= innerWidth + 1 && b.bottom <= innerHeight + 1; }
    return out;
  });
  assert.equal(r.overflow, false, name + '：横向溢出');
  assert.deepEqual(r.overlaps, [], name + '：控件重叠');
  assert.deepEqual(r.clipped, [], name + '：文本被截断');
  assert.equal(r.dialog, true, name + '：对话框超出视口');
}

(async () => {
  const dll = process.env.MISTLETOE_SERVER_DLL;
  if (!dll) throw new Error('MISTLETOE_SERVER_DLL must name the freshly built server DLL');
  const shots = process.env.MISTLETOE_SHOTS || '';
  const isolated = await mkdtemp(path.join(process.env.TEMP || os.tmpdir(), 'bgi-mistletoe-web-e2e-'));
  await mkdir(path.join(isolated, 'room-logs'));
  const port = await freePort(), base = `http://127.0.0.1:${port}`;
  const server = spawn('dotnet', [dll, '--contentRoot', path.resolve(__dirname, '../../BgiCoordinatorServer')], {
    env: { ...process.env, ASPNETCORE_URLS: base, ROOM_LOG_ROOT: path.join(isolated, 'room-logs'), NEXUSBGI_DATA_ROOT: path.join(isolated, 'user'), LOCALAPPDATA: path.join(isolated, 'localappdata') },
    windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let serverLog = ''; server.stdout.on('data', d => serverLog += d); server.stderr.on('data', d => serverLog += d);
  let browser; const report = { checks: [], shots: [] };
  let diag = async () => '';
  const check = (name, fn) => fn().then(() => report.checks.push(name), async e => { const extra = '\nPAGE: ' + await diag().catch(x => x.message); e.message += extra; if (e.stack) e.stack += extra; throw e; });
  try {
    for (const until = Date.now() + 20000; Date.now() < until;) {
      if (server.exitCode !== null) throw new Error('isolated server exited: ' + serverLog);
      try { if ((await fetch(base + '/health')).ok) break; } catch {}
      await new Promise(r => setTimeout(r, 120));
    }
    browser = await chromium.launch({ headless: true, executablePath: process.env.EDGE_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' });
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
    const room = 'M' + Date.now().toString(36), secret = 'simulation-password';
    const executor = await context.newPage(), other = await context.newPage(), page = await context.newPage();
    const errors = []; page.on('pageerror', e => errors.push(String(e)));
    for (const p of [executor, other]) { await p.goto(base); await p.waitForFunction(() => typeof signalR !== 'undefined'); await p.evaluate(installSimExecutor); }
    await executor.evaluate(a => sim.connect(a), { room, secret, uid: 'owner-uid', instanceId: 'inst-exec', name: '我的执行端' });
    await other.evaluate(a => sim.connect(a), { room, secret, uid: 'other-uid', instanceId: 'inst-other', name: '其他成员' });
    const S = (fn, arg) => executor.evaluate(fn, arg);
    diag = () => page.evaluate(() => JSON.stringify({ status: mtEl('mtStatus')?.textContent, stage: mtEl('mtOpStage')?.textContent, pending: mtEl('mtPending')?.textContent, unconfirmed: mt.unconfirmed, dirty: su.dirty, saving: su.saving, saveError: su.saveError, running: su.isRunning }));
    // 接受下一个确认框；同一个框被两个处理器接受时忽略第二次。
    const yes = () => page.once('dialog', d => d.accept().catch(() => {}));
    await S(() => { sim.flows.set('wf-sim', { doc: { schema: 'mistletoe.workflow', schemaVersion: 1, workflowId: 'wf-sim', name: '每日计划', nodes: [
      { nodeId: 'n-a', kind: 'resource.oneDragonConfig', ref: { config: '每日', revision: 'cfg-r1' }, strategies: [] },
      { nodeId: 'n-b', kind: 'resource.configGroup', ref: { config: '锄地' + '超长配置组名称'.repeat(5), revision: 'cfg-g1' }, strategies: [{ kind: 'prerequisite.account', uid: '123456789', bindingCode: 'secret-code' }], timeWindow: { start: '22:00', end: '06:00' } },
      { nodeId: 'ctl-1', kind: 'control.condition', flow: { route: 'keep' } }], triggers: [{ kind: 'trigger.time', time: '04:00', missPolicy: 'nextDay' }], terminal: [], unknownField: { keep: true } }, revision: 'wf-r1' }); });

    await page.goto(base); await page.locator('#roomCode').fill(room); await page.locator('#password').fill(secret);
    await page.locator('#joinButton').click(); await page.locator('#mistletoeProduct').waitFor({ state: 'visible' });
    await page.locator('#mistletoeProduct').click();

    await check('只列在线助手，未授权进入授权卡且请求被执行端拒绝', async () => {
      await page.locator('#mtTarget option[value="remote:owner-uid:inst-exec"]').waitFor({ state: 'attached' });
      const groups = await page.locator('#mtTarget optgroup').evaluateAll(gs => gs.map(g => g.label));
      assert.ok(groups.some(g => g.includes('绑定新的执行端')), '未授权的实例只能出现在「绑定新的执行端」');
      await page.locator('#mtTarget').selectOption('remote:owner-uid:inst-exec');
      await page.locator('.mt-auth #mtAuthCode').waitFor();
      const denied = await S(() => sim.requests.filter(r => r.op === 'web.identity').length);
      assert.ok(denied >= 1);
    });
    if (shots) { await page.screenshot({ path: path.join(shots, 'authorize-1440.png') }); report.shots.push('authorize-1440'); }

    await check('授权码错误被拒、正确码绑定此浏览器且只保存凭据', async () => {
      await S(() => sim.code = '12345678');
      await page.locator('#mtAuthCode').fill('1111 1111'); await page.locator('#mtAuthGo').click();
      await page.locator('#mtStatus').getByText(/授权未完成.*不正确/).waitFor();
      await page.locator('#mtAuthCode').fill('1234 5678'); await page.locator('#mtAuthGo').click();
      await page.locator('#mtRunCard').waitFor({ timeout: 10000 });
      const cred = await page.evaluate(() => JSON.parse(localStorage.getItem('bgi.mistletoe.owner.v1'))['inst-exec']);
      assert.equal(cred.browserId, 'wb-sim'); assert.equal(cred.uid, 'owner-uid');
      const groups = await page.locator('#mtTarget optgroup[label="我的执行端"] option').allTextContents();
      assert.ok(groups.some(t => t.includes('我的执行端')), '授权后归入「我的执行端」');
    });

    await check('他人助手：没有本人凭据，所有请求被拒，不显示任何数据', async () => {
      await page.locator('#mtTarget').selectOption('remote:other-uid:inst-other');
      await page.locator('.mt-auth').waitFor();
      assert.equal(await other.evaluate(() => sim.requests.filter(r => r.op !== 'web.identity').length), 0);
      await page.locator('#mtTarget').selectOption('remote:owner-uid:inst-exec');
      await page.locator('#mtRunCard').waitFor();
    });

    await check('启动中心：大流程适应整图、平移缩放、编辑保存带修订', async () => {
      await S(f => { sim.startup = f; sim.startupRevision = 'rev-big'; }, bigFlow());
      await page.locator('#mtReloadStartup').click();
      await page.locator('#mtWorld .mt-node').first().waitFor();
      const count = await page.locator('#mtWorld .mt-node').count();
      assert.ok(count > 150, '大流程节点全部绘出：' + count);
      const fit = await page.evaluate(() => { const s = document.querySelector('#mtStage').getBoundingClientRect(), w = document.querySelector('#mtWorld').getBoundingClientRect(); return { zoom: su.view.zoom, fits: w.width <= s.width + 2 && w.height <= s.height + 2 }; });
      assert.ok(fit.fits || fit.zoom <= 0.21, '默认适应整图（或到最小缩放）');
      await page.locator('[data-zoom="in"]').click(); await page.locator('[data-zoom="in"]').click();
      const zoomed = await page.evaluate(() => su.view.zoom); assert.ok(zoomed > fit.zoom);
      const stage = await page.locator('#mtStage').boundingBox();
      await page.mouse.move(stage.x + stage.width / 2, stage.y + stage.height / 2); await page.mouse.down();
      await page.mouse.move(stage.x + stage.width / 2 - 200, stage.y + stage.height / 2 - 120, { steps: 6 }); await page.mouse.up();
      const moved = await page.evaluate(() => ({ mode: su.view.mode, x: su.view.x }));
      assert.equal(moved.mode, 'manual');
      // 收起/展开节点设置不重置用户视角。
      await page.locator('#mtInspector [data-close]').click(); await page.waitForTimeout(150);
      assert.equal(await page.evaluate(() => su.view.mode), 'manual');
      await page.locator('#mtInspector [data-open]').click();
      await page.locator('[data-zoom="fit"]').click(); assert.equal(await page.evaluate(() => su.view.mode), 'fit');
      // 编辑：在主流程末尾前添加等待节点（结束流程/进入任务中心规则由页面判定）。
      const before = await S(() => sim.requests.filter(r => r.op === 'startup.save').length);
      await page.locator('#mtOutline > .mt-add-row').last().click();
      await page.locator('#mtAddDialog [data-kind="wait"]').click();
      await page.locator('#mtInspector [data-f="waitSeconds"]').fill('42'); await page.locator('#mtInspector [data-f="waitSeconds"]').blur();
      await executor.waitForFunction(n => sim.requests.filter(r => r.op === 'startup.save').length > n, before, { timeout: 8000 });
      const saved = await S(() => ({ last: sim.startup.steps.at(-1), req: sim.requests.filter(r => r.op === 'startup.save').at(-1) }));
      assert.equal(saved.last.kind, 'wait'); assert.equal(saved.last.waitSeconds, 42);
      assert.equal(saved.req.expectedRevision, 'rev-big', '保存携带读取时的修订');
      await page.locator('#mtSaveState').getByText('已与执行端同步').waitFor({ timeout: 8000 });
      // 只能有一个「进入任务中心执行」。
      await page.locator('#mtOutline > .mt-add-row').last().click();
      await page.locator('#mtAddDialog [data-kind="enterTaskCenter"]').click();
      await page.locator('#mtStatus').getByText(/只能有一个「进入任务中心执行」/).waitFor();
    });
    if (shots) { await page.locator('[data-zoom="fit"]').click(); await page.waitForTimeout(200); await page.locator('#mtEditor').scrollIntoViewIfNeeded(); await page.screenshot({ path: path.join(shots, 'startup-big-1440.png') }); report.shots.push('startup-big-1440'); }

    await check('他处修改造成修订冲突：草稿保留，不覆盖', async () => {
      await S(() => { sim.startupRevision = 'rev-external'; });
      await page.locator('#mtDelay').fill('9'); await page.locator('#mtDelay').dispatchEvent('change');
      await page.locator('#mtSaveState').getByText(/未保存.*修订已变化/).waitFor({ timeout: 8000 });
      assert.notEqual(await S(() => sim.startup.delaySeconds), 9);
      page.once('dialog', d => d.accept());
      await page.locator('#mtReloadStartup').click();
      await page.locator('#mtSaveState').getByText('已与执行端同步').waitFor();
    });

    await check('启动中心：普通流程（分支、人工确认、触发器、执行中）布局与功能', async () => {
      await page.setViewportSize({ width: 1440, height: 900 });
      await S(f => { sim.startup = f; sim.startupRevision = 'rev-n1'; sim.running = false; sim.mode = 'ok';
        sim.prompts = [{ RuntimeId: 'p-1', Message: '是否继续执行今天的任务？' + '需要人工判断的长说明'.repeat(4), YesPreview: '启动 BGI', NoPreview: '等待 5 秒', RemainingSeconds: 42 }];
        sim.triggers = [{ RuntimeId: 'tr-1', Kind: 'timer', Text: '定时触发器「凌晨刷新」04:00 到点执行（1 节点，每天重复）', Step: f.steps[2], Note: '' },
          { RuntimeId: 'tr-2', Kind: 'watchdog', Text: '电子狗「BGI 空闲」盯梢中', Step: { id: 'gone', kind: 'watchdog', fireSteps: [] }, Note: '上次检查：未成立' }];
        sim.states = [{ StepId: 'c1', State: 5, Note: '' }, { StepId: 'g1', State: 2, Note: '' }, { StepId: 'c2', State: 1, Note: '等待人工确认' }]; sim.statusText = '执行中：人工确认'; sim.running = true; }, normalFlow());
      await page.locator('#mtReloadStartup').click(); await page.locator('#mtWorld .mt-node[data-node="c2"]').waitFor();
      await page.locator('.mt-prompt').waitFor({ timeout: 8000 });
      await page.evaluate(() => scrollTo(0, 0)); await page.waitForTimeout(250);
      await layoutAudit(page, '普通流程 1440');
      if (shots) { await page.screenshot({ path: path.join(shots, 'startup-normal-1440.png') }); report.shots.push('startup-normal-1440'); }
      assert.ok(await page.locator('#mtWorld .mt-node.st-running').count() >= 1, '执行中节点点亮');
      const labels = await page.locator('#mtOutline .mt-add-row').allTextContents();
      assert.ok(labels.includes('＋ 添加到「是」分支') && labels.includes('＋ 添加到「到点执行」链'), '分支添加文案：' + labels.join('|'));
      const endRow = page.locator('#mtOutline .mt-add-row[data-add-parent="c1"][data-add-key="falseSteps"]');
      if (await endRow.count()) assert.equal(await endRow.isDisabled(), true, '以结束流程收尾的链不能再添加');
      await page.evaluate(() => { const c = mtFind('c1').step; mtOpenAdd(c.falseSteps, mtChainLabel(c, 'falseSteps')); });
      await page.locator('#mtStatus').getByText('「「白天才启动」的否分支」已有「结束流程」节点', { exact: false }).or(page.locator('#mtStatus').getByText(/否分支.*结束流程/)).first().waitFor();
      // 人工确认：选「是」只送一次，回答后提示消失。
      await page.locator('.mt-prompt [data-answer="1"]').click();
      await executor.waitForFunction(() => sim.answer === true);
      await page.locator('.mt-prompt').waitFor({ state: 'detached', timeout: 8000 });
      // 触发器：定位、查看流程、对不在当前流程中的实例给出说明、取消。
      await page.locator('#mtTriggers [data-locate="0"]').click();
      assert.equal(await page.evaluate(() => su.selected), 't1');
      await page.locator('#mtTriggers [data-flow="0"]').click();
      await page.locator('#mtPickDialog .mt-fire-list li').first().waitFor(); await layoutAudit(page, '触发器流程');
      await page.locator('#mtPickDialog [data-close]').click();
      await page.locator('#mtTriggers [data-locate="1"]').click();
      await page.locator('#mtStatus').getByText(/不在当前启动流程中/).waitFor();
      yes();
      await page.locator('#mtTriggers [data-cancel="1"]').click();
      await executor.waitForFunction(() => sim.triggers.length === 1);
      // 清除路径显示。
      await S(() => { sim.running = false; sim.statusText = ''; });
      await page.locator('#mtClearStates').click();
      await executor.waitForFunction(() => sim.states.length === 0);
      await page.locator('#mtWorld .mt-node.st-running').waitFor({ state: 'detached', timeout: 8000 });
      // 浏览执行端程序：逐级进入目录后选中 exe，写入节点路径并自动保存。
      await page.locator('#mtOutline .mt-row[data-id="g1"]').click();
      await page.locator('#mtInspector [data-browse]').click();
      await page.locator('#mtPickDialog [data-dir="D:\\\\"]').click();
      await page.locator('#mtPickDialog [data-exe="D:\\\\YuanShen.exe"]').click();
      await executor.waitForFunction(() => sim.startup.steps[0].trueSteps[0].path === 'D:\\YuanShen.exe', null, { timeout: 8000 });
      // 开机自启动：带修订保存。
      await page.locator('#mtBoot').check();
      await executor.waitForFunction(() => sim.autoLaunch.enabled === true, null, { timeout: 8000 });
      assert.equal(await S(() => sim.requests.filter(r => r.op === 'startup.autoLaunch.save').at(-1).expectedRevision), 'al-false0');
    });

    await check('启动中心方案：保存、导出、导入改名避重、恢复前自动备份、删除', async () => {
      await page.locator('#mtSchemeName').fill('日常方案'); await page.locator('#mtSchemeSave').click();
      await executor.waitForFunction(() => sim.schemes.some(s => s.name === '日常方案'), null, { timeout: 8000 });
      const [download] = await Promise.all([page.waitForEvent('download'), page.locator('#mtSchemeExport').click()]);
      const exported = JSON.parse(await readFile(await download.path(), 'utf8'));
      assert.equal(exported.name, '日常方案'); assert.equal(exported.config.steps.length, 5);
      const [chooser] = await Promise.all([page.waitForEvent('filechooser'), page.locator('#mtSchemeImport').click()]);
      await chooser.setFiles({ name: 'scheme.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(exported)) });
      await executor.waitForFunction(() => sim.schemes.length === 2, null, { timeout: 8000 });
      assert.ok(await S(() => sim.schemes.some(s => s.name.startsWith('日常方案-导入'))), '同名导入自动改名');
      // 恢复：先把当前流程备份为「恢复前自动备份」，再替换并保存。
      await S(() => { const s = sim.schemes.find(x => x.name === '日常方案'); s.config = { ...s.config, delaySeconds: 33 }; sim.schemesRevision = 'rev-sc-x'; });
      await page.waitForFunction(() => !su.dirty && !su.saving); await page.locator('#mtReloadStartup').click();
      await page.waitForFunction(() => su.schemesRevision === 'rev-sc-x' && !su.loading && !!mtEl('mtSchemeSelect'));
      await page.locator('#mtSchemeSelect').selectOption('日常方案');
      yes(); await page.locator('#mtSchemeRestore').click();
      await executor.waitForFunction(() => sim.startup.delaySeconds === 33 && sim.schemes.some(s => s.name === '恢复前自动备份'), null, { timeout: 8000 });
      await page.locator('#mtSchemeSelect').selectOption({ index: 0 });
      const before = await S(() => sim.schemes.length);
      const victim = await page.locator('#mtSchemeSelect').inputValue();
      yes(); await page.locator('#mtSchemeDelete').click();
      await executor.waitForFunction(n => sim.schemes.length === n - 1, before, { timeout: 8000 });
      assert.ok(!(await S(v => sim.schemes.some(s => s.name === v), victim)));
    });

    await check('启动中心：手机端普通流程与底部抽屉', async () => {
      await page.setViewportSize({ width: 390, height: 844 }); await page.waitForTimeout(300);
      await page.evaluate(() => scrollTo(0, 0));
      await layoutAudit(page, '普通流程 390 大纲');
      await page.locator('.mt-pane-switch [data-pane="canvas"]').click(); await page.waitForTimeout(250);
      await layoutAudit(page, '普通流程 390 画布');
      if (shots) { await page.locator('#mtEditor').scrollIntoViewIfNeeded(); await page.screenshot({ path: path.join(shots, 'startup-normal-canvas-390.png') }); report.shots.push('startup-normal-canvas-390'); }
      await page.locator('.mt-pane-switch [data-pane="outline"]').click();
      await page.locator('#mtOutline > .mt-add-row').last().click();
      await page.locator('#mtAddDialog [data-kind="wait"]').waitFor(); await layoutAudit(page, '添加节点抽屉 390');
      if (shots) { await page.screenshot({ path: path.join(shots, 'add-node-390.png') }); report.shots.push('add-node-390'); }
      await page.keyboard.press('Escape');
      await page.setViewportSize({ width: 1440, height: 900 }); await page.waitForTimeout(250);
    });

    await check('空流程：空状态提示与添加入口', async () => {
      await S(() => { sim.startup = { enabled: false, delaySeconds: 5, autoExpandBranch: true, steps: [] }; sim.startupRevision = 'rev-empty'; });
      await page.waitForFunction(() => !su.dirty && !su.saving); await page.locator('#mtReloadStartup').click();
      await page.locator('#mtOutline .dd-empty').waitFor();
      await layoutAudit(page, '空流程 1440');
      if (shots) { await page.evaluate(() => scrollTo(0, 0)); await page.screenshot({ path: path.join(shots, 'startup-empty-1440.png') }); report.shots.push('startup-empty-1440'); }
    });

    await check('运行：已发送 → 已受理 → 执行中 → 终态；执行中不能再次发起', async () => {
      await S(() => { sim.runHold = true; sim.startup = { enabled: false, delaySeconds: 5, autoExpandBranch: true, steps: [{ id: 'r1', nodeType: 'action', kind: 'wait', name: '等一下', enabled: true, trueSteps: [], falseSteps: [], fireSteps: [], waitSeconds: 3 }] }; sim.startupRevision = 'rev-run'; });
      await page.locator('#mtReloadStartup').click(); await page.locator('#mtRunNow').waitFor();
      await page.locator('#mtRunNow').click();
      await page.locator('#mtOpStage[data-stage="sent"], #mtOpStage[data-stage="accepted"]').first().waitFor();
      await page.locator('#mtOpStage[data-stage="running"]').waitFor({ timeout: 10000 });
      assert.equal(await page.locator('#mtRunNow').isDisabled(), true, '执行中「立即执行」不可用');
      await page.locator('#mtPending').getByText('仍在执行端处理', { exact: false }).or(page.locator('#mtPending .mt-pending')).first().waitFor();
      // 台账阻断：执行端还在处理时，页面不会发出第二次运行。
      const runs = await S(() => sim.requests.filter(r => r.op === 'startup.run').length);
      const blocked = await page.evaluate(() => mtOperate('startup.run', undefined, { label: '再次运行' }));
      assert.equal(blocked.status, 'blocked');
      assert.equal(await S(() => sim.requests.filter(r => r.op === 'startup.run').length), runs, '阻断时不得再次发送');
      // 节点实时点亮。
      await page.locator('#mtWorld .mt-node.st-running').waitFor({ timeout: 6000 });
      // 停止不受台账阻挡（与 F11 同一执行端停止路径）。
      await page.locator('#mtCancelRun').click();
      await executor.waitForFunction(() => sim.cancelCount >= 1);
      await S(() => { sim.runHold = false; });
      await page.locator('#mtCheckPending').click();
      await page.locator('#mtPending .mt-pending').waitFor({ state: 'detached', timeout: 8000 });
    });

    await check('结果未知：回复丢失按「结果未确认」显示，不重发，核对后解除', async () => {
      await S(() => { sim.mode = 'drop-reply'; sim.running = false; });
      const runsBefore = await S(() => sim.requests.filter(r => r.op === 'startup.run').length);
      const reply = await page.evaluate(() => mtOperate('startup.run', undefined, { label: '回复丢失的运行', timeout: 1500, onStage: mtStageBar }));
      assert.equal(reply.status, 'unknown');
      await page.locator('#mtOpStage[data-stage="unknown"]').waitFor();
      await page.locator('#mtPending').getByText('回复丢失的运行', { exact: false }).waitFor();
      await S(() => { sim.mode = 'ok'; });
      await page.waitForTimeout(400);
      assert.equal(await S(() => sim.requests.filter(r => r.op === 'startup.run').length), runsBefore + 1, '不会自动重发');
      // 3 秒轮询会静默核对（只查询不重发）；还没核对到时手动核对。
      await page.locator('#mtCheckPending').click({ timeout: 2000 }).catch(() => {});
      await page.locator('#mtPending .mt-pending').waitFor({ state: 'detached', timeout: 8000 });
      await S(() => { sim.running = false; });
    });

    await check('执行端超时无回复：结果未确认，阻断再次执行，人工确认后解除', async () => {
      await S(() => { sim.mode = 'silent'; });
      const reply = await page.evaluate(() => mtOperate('task.run.start', { workflowId: 'wf-sim' }, { label: '超时的启动', timeout: 800, onStage: mtStageBar }));
      assert.equal(reply.status, 'unknown');
      const second = await page.evaluate(() => mtOperate('task.run.start', { workflowId: 'wf-sim' }, { label: '第二次启动', timeout: 800 }));
      assert.equal(second.status, 'blocked');
      await S(() => { sim.mode = 'ok'; });
      // 执行端找不到这次操作：不能证明没执行，保持阻断；人工核对后才解除。
      await page.locator('#mtCheckPending').click(); await page.waitForTimeout(500);
      await page.locator('#mtPending .mt-pending').waitFor();
      page.once('dialog', d => d.accept());
      await page.locator('#mtAckPending').click();
      await page.locator('#mtPending .mt-pending').waitFor({ state: 'detached' });
    });

    await check('任务中心：计划原样往返、账号掩码、单元格编辑保存、连续一条龙、停止', async () => {
      await page.locator('[data-mt-tab="taskcenter"]').click();
      await page.locator('#tcNodes .tc-tr[data-node="n-b"]').waitFor({ timeout: 10000 });
      const acc = await page.locator('#tcNodes .tc-tr[data-node="n-b"] [data-edit="account"]').textContent();
      assert.ok(!acc.includes('123456789') && acc.includes('***'), '账号掩码显示');
      await page.locator('#tcNodes .tc-tr[data-node="n-a"] [data-edit="window"]').click();
      await page.locator('#tcWinStart').fill('08:00'); await page.locator('#tcWinEnd').fill('12:00');
      await page.locator('#tcDialog [data-ok]').click();
      await executor.waitForFunction(() => sim.flows.get('wf-sim').doc.nodes[0].timeWindow?.end === '12:00', null, { timeout: 8000 });
      const doc = await S(() => sim.flows.get('wf-sim').doc);
      assert.deepEqual(doc.unknownField, { keep: true }, '未知字段原样保留');
      assert.equal(doc.nodes[2].kind, 'control.condition', '控制节点保留');
      assert.equal(doc.nodes[1].strategies[0].uid, '123456789', '未触碰的账号保留原值');
      await page.locator('#tcSave, .mt-save-state').first().waitFor();
      await page.locator('#tcStart').click();
      await executor.waitForFunction(() => sim.startCount === 1, null, { timeout: 10000 });
      await page.locator('#tcRuns .tc-run').waitFor({ timeout: 10000 });
      assert.equal(await page.locator('#tcStart').isDisabled(), true, '已有运行时不能再次启动');
      page.once('dialog', d => d.accept());
      await page.locator('#tcRuns [data-run-act="Stop"]').click();
      await executor.waitForFunction(() => sim.stopCount === 1, null, { timeout: 8000 });
      await page.locator('#tcRuns .tc-run').waitFor({ state: 'detached', timeout: 10000 });
    });
    if (shots) { await page.screenshot({ path: path.join(shots, 'taskcenter-1440.png') }); report.shots.push('taskcenter-1440'); }

    await check('任务中心：新增任务默认配置组、计划设置、内部任务、配置单菜单', async () => {
      await page.locator('[data-mt-tab="taskcenter"]').click();
      await page.locator('#tcNodes .tc-tr[data-node="n-a"]').waitFor({ timeout: 10000 });
      // 新增任务：连接 BGI 后目录有配置组、整龙、单项；默认选中配置组（与 PC 一致）。
      await page.locator('#tcAdd').click();
      await page.locator('#tcConnect').click();
      await page.locator('#tcDialog .tc-add-item').first().waitFor({ timeout: 10000 });
      await page.keyboard.press('Escape'); await page.locator('#tcAdd').click(); await page.locator('#tcDialog .tc-add-item').first().waitFor();
      assert.equal((await page.locator('#tcDialog [data-cat].active').textContent()).trim(), '配置组');
      assert.match(await page.locator('#tcDialog .tc-add-list').textContent(), /锄地/);
      await layoutAudit(page, '新增任务 1440');
      await page.locator('#tcDialog [data-cat="2"]').click();
      await page.locator('#tcDialog .tc-add-item input').first().check();
      const saves = await S(() => sim.saveCount || 0);
      await page.locator('#tcDialog [data-ok]').click();
      await executor.waitForFunction(n => (sim.saveCount || 0) > n, saves, { timeout: 8000 });
      const added = await S(() => sim.flows.get('wf-sim').doc.nodes.at(-1));
      assert.equal(added.kind, 'resource.singleTask'); assert.equal(added.ref.config, '每日'); assert.equal(added.ref.taskId, 't1');
      // 计划设置：定时与循环。
      await page.locator('#tcSettings').click();
      await page.locator('#tcTrigTime').fill('05:30'); await page.locator('#tcLoop').selectOption('1'); await page.locator('#tcLoopTime').fill('05:30');
      await layoutAudit(page, '计划设置 1440');
      await page.locator('#tcDialog [data-ok]').click();
      await executor.waitForFunction(() => { const d = sim.flows.get('wf-sim').doc; return d.triggers[0].time === '05:30' && d.loop?.mode === 'scheduled'; }, null, { timeout: 8000 });
      assert.equal(await S(() => sim.flows.get('wf-sim').doc.triggers[0].missPolicy), 'nextDay', '未改的错过策略保留');
      // ⚙ 内部任务：取消启用一项 → 写入 BGI 配置 → 同一配置的引用一起更新修订。
      await page.locator('#tcNodes .tc-tr[data-node="n-a"] [data-tasks]').click();
      await page.locator('#tcDialog [data-en="1"]').waitFor({ timeout: 8000 });
      await page.locator('#tcDialog [data-en="1"]').uncheck();
      await executor.waitForFunction(() => sim.layout.tasks[1].enabled === false, null, { timeout: 8000 });
      await executor.waitForFunction(() => sim.flows.get('wf-sim').doc.nodes[0].ref.revision === sim.layout.revision, null, { timeout: 8000 });
      await page.locator('#tcDialog [data-close]').click();
      // ⋯ 菜单：设为起点、复制、打开编辑器、同步引用、删除。
      const menu = async (node, act) => { await page.locator(`#tcNodes .tc-tr[data-node="${node}"] [data-more]`).click(); await page.locator(`#tcDialog [data-a="${act}"]`).click(); };
      await menu('n-b', 'mark');
      await executor.waitForFunction(() => sim.flows.get('wf-sim').doc.taskCenterStartNodeId === 'n-b', null, { timeout: 8000 });
      await page.locator('#tcNodes .tc-tr[data-node="n-b"] .tc-start').waitFor();
      await menu('n-a', 'dup');
      await executor.waitForFunction(() => sim.flows.get('wf-sim').doc.nodes.filter(n => n.ref?.config === '每日' && n.kind === 'resource.oneDragonConfig').length === 2, null, { timeout: 8000 });
      await menu('n-a', 'editor');
      await executor.waitForFunction(() => sim.editorOpened?.nodeId === 'n-a', null, { timeout: 8000 });
      await S(() => { sim.layout.revision = 'cfg-ext'; });
      await menu('n-a', 'ref');
      await executor.waitForFunction(() => sim.flows.get('wf-sim').doc.nodes[0].ref.revision === 'cfg-ext', null, { timeout: 8000 });
      const copyId = await S(() => sim.flows.get('wf-sim').doc.nodes[1].nodeId);
      yes(); await menu(copyId, 'del');
      await executor.waitForFunction(id => !sim.flows.get('wf-sim').doc.nodes.some(n => n.nodeId === id), copyId, { timeout: 8000 });
      // 单任务启动：stopAfter = 入口；「连续一条龙」从起点开始并在受理后清除起点。
      await menu('n-a', 'single');
      await executor.waitForFunction(() => sim.lastStart?.stopAfterNodeId === 'n-a', null, { timeout: 8000 });
      await page.locator('#tcRuns .tc-run').waitFor({ timeout: 8000 });
      // 运行卡片：暂停 → 恢复 → 跳过 → 重载 → 停止。
      await page.locator('#tcRuns [data-run-act="Pause"]').click();
      await executor.waitForFunction(() => sim.runs[0]?.state === 3, null, { timeout: 8000 }); await page.waitForFunction(() => !mt.unconfirmed, null, { timeout: 8000 });
      await S(() => { sim.runs[0].driving = false; });
      await page.locator('#tcRuns [data-run-resume]').waitFor({ timeout: 8000 }); await page.locator('#tcRuns [data-run-resume]').click();
      await executor.waitForFunction(() => sim.runs[0]?.state === 2, null, { timeout: 8000 }); await page.waitForFunction(() => !mt.unconfirmed, null, { timeout: 8000 });
      await S(() => { sim.runs[0].driving = true; });
      await page.locator('#tcRuns [data-run-act="SkipCurrent"]').waitFor({ timeout: 8000 }); await page.locator('#tcRuns [data-run-act="SkipCurrent"]').click(); await page.waitForFunction(() => !mt.unconfirmed, null, { timeout: 8000 });
      await page.locator('#tcRuns [data-run-act="ReloadDefinition"]').waitFor({ timeout: 8000 }); await page.locator('#tcRuns [data-run-act="ReloadDefinition"]').click(); await page.waitForFunction(() => !mt.unconfirmed, null, { timeout: 8000 });
      await executor.waitForFunction(() => sim.requests.filter(r => r.op === 'task.run.action').map(r => r.payload.action).join() .includes('SkipCurrent,ReloadDefinition'), null, { timeout: 8000 });
      await layoutAudit(page, '运行卡片 1440');
      yes(); await page.locator('#tcRuns [data-run-act="Stop"]').click();
      await page.locator('#tcRuns .tc-run').waitFor({ state: 'detached', timeout: 10000 });
      await page.waitForFunction(() => !mt.unconfirmed, null, { timeout: 8000 });
      await page.locator('#tcStart').click();
      await executor.waitForFunction(() => sim.lastStart?.entryNodeId === 'n-b' && !sim.lastStart.stopAfterNodeId, null, { timeout: 8000 });
      await executor.waitForFunction(() => !sim.flows.get('wf-sim').doc.taskCenterStartNodeId, null, { timeout: 8000 });
      yes(); await page.locator('#tcRuns [data-run-act="Stop"]').click();
      await page.locator('#tcRuns .tc-run').waitFor({ state: 'detached', timeout: 10000 });
    });

    await check('任务中心管理：新建、导入导出、调度列表、BGI 路径、重置、迁移、删除与恢复', async () => {
      const multi = async fn => { const h = d => d.accept().catch(() => {}); page.on('dialog', h); try { await fn(); } finally { page.off('dialog', h); } };
      const manage = async m => { await page.waitForFunction(() => !mt.unconfirmed && !tc.loading, null, { timeout: 15000 }); await page.waitForTimeout(150); await page.locator('#tcManage').click(); await page.locator(`#tcDialog [data-m="${m}"]`).click(); };
      await page.locator('#tcNew').click();
      await executor.waitForFunction(() => [...sim.flows.keys()].some(k => k !== 'wf-sim' && sim.flows.get(k).doc.name.startsWith('新计划')), null, { timeout: 8000 });
      await page.locator('#tcNodes .dd-empty').waitFor();
      await page.locator('#tcPlanList [data-plan="wf-sim"]').click(); await page.locator('#tcNodes .tc-tr[data-node="n-a"]').waitFor();
      await page.locator('#tcManage').click(); await page.locator('#tcDialog [data-m]').first().waitFor(); await layoutAudit(page, '计划管理 1440');
      if (shots) { await page.screenshot({ path: path.join(shots, 'taskcenter-manage-1440.png') }); report.shots.push('taskcenter-manage-1440'); }
      await page.keyboard.press('Escape');
      const [dl] = await Promise.all([page.waitForEvent('download'), manage('exportOne')]);
      const flowText = await readFile(await dl.path(), 'utf8'); assert.equal(JSON.parse(flowText).workflowId, 'wf-sim');
      const [chooser] = await Promise.all([page.waitForEvent('filechooser'), manage('import')]);
      await chooser.setFiles({ name: 'plan.json', mimeType: 'application/json', buffer: Buffer.from(flowText) });
      await executor.waitForFunction(() => sim.imports.length === 1, null, { timeout: 10000 });
      const downloads = [], onDl = d => downloads.push(d); page.on('download', onDl);
      await manage('exportAll');
      const msg = await page.locator('#mtStatus').getByText(/已导出 \d+ 张计划表/).textContent({ timeout: 10000 });
      const expected = Number(msg.match(/已导出 (\d+)/)[1]), flowCount = await S(() => sim.flows.size);
      assert.equal(expected, flowCount, '导出全部计划：' + msg);
      for (const until = Date.now() + 5000; downloads.length < expected && Date.now() < until;) await page.waitForTimeout(100);
      page.off('download', onDl);
      assert.equal(downloads.length, expected, '导出全部计划下载数');
      assert.equal(new Set(downloads.map(d => d.suggestedFilename())).size, downloads.length, '导出文件名不重复');
      const [sd] = await Promise.all([page.waitForEvent('download'), manage('schedExport')]);
      const sched = await readFile(await sd.path(), 'utf8');
      const [sc] = await Promise.all([page.waitForEvent('filechooser'), manage('schedImport')]);
      await sc.setFiles({ name: 'schedule.json', mimeType: 'application/json', buffer: Buffer.from(sched) });
      await executor.waitForFunction(() => sim.flows.has('wf-cand-1'), null, { timeout: 10000 });
      // BGI 路径：读当前值 → 浏览执行端目录 → 带修订保存；只列 BetterGI.exe。
      await manage('bgiPath');
      await page.locator('#mtPickDialog [data-dir=""]').click(); // 从当前路径所在目录回到磁盘列表
      await page.locator('#mtPickDialog [data-dir="C:\\\\"]').click();
      assert.equal(await page.locator('#mtPickDialog [data-exe$="YuanShen.exe"]').count(), 0, '只列 BetterGI.exe');
      await page.locator('#mtPickDialog [data-exe="C:\\\\BetterGI.exe"]').click();
      await executor.waitForFunction(() => sim.bgiPath.path === 'C:\\BetterGI.exe', null, { timeout: 8000 });
      assert.equal(await S(() => sim.requests.filter(r => r.op === 'task.bgiPath.save').at(-1).expectedRevision), 'bp-1');
      await multi(() => manage('reset'));
      await executor.waitForFunction(() => sim.resetCount === 1, null, { timeout: 10000 });
      await manage('migRehearsal'); await page.locator('#mtStatus').getByText(/迁移演练通过/).waitFor({ timeout: 10000 });
      await manage('migPrepare');
      await page.locator('#mtPickDialog [data-dir="D:\\\\"]').click(); await page.locator('#mtPickDialog [data-pickdir]').click();
      await executor.waitForFunction(() => sim.migrations[0] === 'D:\\', null, { timeout: 10000 });
      await page.waitForFunction(() => !mt.unconfirmed && !tc.loading, null, { timeout: 15000 }); await page.locator('#tcManage').click(); await page.locator('#tcDialog [data-act="wf-mig"]').click();
      await executor.waitForFunction(() => sim.flows.get('wf-mig')?.candidate === false, null, { timeout: 10000 });
      await multi(() => manage('migRollback'));
      await executor.waitForFunction(() => sim.rollbackCount === 1, null, { timeout: 10000 });
      await page.locator('#mtStatus').getByText(/没有可回退/).waitFor({ timeout: 8000 });
      // 删除当前计划（带修订）→ 管理里恢复。
      await page.locator('#tcPlanList [data-plan="wf-mig"]').click(); await page.locator('#tcNodes .dd-empty').waitFor();
      await multi(() => manage('delete'));
      await executor.waitForFunction(() => !sim.flows.has('wf-mig') && sim.deleted.some(d => d.WorkflowId === 'wf-mig'), null, { timeout: 8000 });
      await page.locator('#tcManage').click(); await page.locator('#tcDialog [data-restore]').last().click();
      await executor.waitForFunction(() => sim.flows.has('wf-mig'), null, { timeout: 8000 });
    });

    await check('结果未知的旧运行：同 PC 不铺成卡片，计划区说明原因，重置后可再次启动', async () => {
      await page.locator('[data-mt-tab="taskcenter"]').click(); await page.locator('#tcPlanList [data-plan="wf-sim"]').click();
      await S(l => { for (let i = 0; i < 6; i++) sim.runs.push({ runId: 'run-unk-' + i, workflowId: 'wf-sim', workflowRevision: 'old', state: 8, driving: false, stopRequested: false, cursor: { nodeId: 'n-a', loopIteration: 0 }, nodeOutcomes: [], updatedAt: new Date(Date.now() - i * 60000).toISOString(), note: l }); }, '助手重启：提交存在未闭合发送事实（idem-xxx），标 Unknown，需按幂等键+job 查询对账，禁止自动重跑。'.repeat(3));
      await page.locator('#tcPlanHead .tc-reason').getByText(/旧运行结果无法确认/).waitFor({ timeout: 10000 });
      assert.match(await page.locator('#tcReset').getAttribute('class'), /dd-btn-primary/, '有未知旧运行时重置按钮高亮');
      assert.equal(await page.locator('#tcRuns .tc-run').count(), 0, 'Unknown 运行不显示为运行卡片');
      assert.match(await page.locator('#tcPlanHead .tc-reason').textContent(), /6 条旧运行结果无法确认/);
      assert.equal(await page.locator('#tcStart').isDisabled(), true, 'Unknown 仍阻止再次启动');
      await layoutAudit(page, 'Unknown 计划区 1440');
      if (shots) { await page.evaluate(() => scrollTo(0, 0)); await page.screenshot({ path: path.join(shots, 'taskcenter-unknown-1440.png') }); report.shots.push('taskcenter-unknown-1440'); }
      // 执行端没连上 BGI：第一次重置被拒且不改任何东西；确认后先连接 BGI 再重试一次。
      const resets = await S(() => { sim.bgiDisconnected = true; return sim.resetCount || 0; });
      const connects = await S(() => sim.requests.filter(r => r.op === 'task.catalog.connect').length);
      const answer = d => d.accept().catch(() => {}); page.on('dialog', answer);
      await page.locator('#tcReset').click();
      await executor.waitForFunction(n => sim.resetCount === n + 1, resets, { timeout: 20000 });
      page.off('dialog', answer);
      assert.equal(await S(n => sim.requests.filter(r => r.op === 'task.catalog.connect').length - n, connects), 1, '先连接 BGI 一次');
      assert.equal(await S(() => sim.requests.filter(r => r.op === 'task.flow.resetExecutionState').length), 3, '被拒一次、连接后重试一次（另一次是管理菜单里的重置）');
      await page.locator('#tcPlanHead .tc-reason').getByText(/旧运行结果无法确认/).waitFor({ state: 'detached', timeout: 10000 });
      assert.equal(await page.locator('#tcReset').isVisible(), true, '重置后按钮仍常驻');
      await page.waitForFunction(() => !document.querySelector('#tcStart').disabled, null, { timeout: 10000 });
    });

    await check('运行记录：历史显示、计划筛选、长文本换行', async () => {
      await S(l => { sim.history.unshift({ runId: 'run-hist', workflowId: 'wf-sim', state: 5, updatedAt: new Date().toISOString(), note: l, nodeOutcomes: [{ nodeId: 'n-a', loopIteration: 0, result: 'failed', reason: l }] }); }, '失败原因'.repeat(40));
      await page.locator('[data-mt-tab="history"]').click();
      await page.locator('.tc-hist').first().waitFor({ timeout: 8000 });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, '长文本不横向溢出');
    });
    if (shots) { await page.screenshot({ path: path.join(shots, 'history-1440.png') }); report.shots.push('history-1440'); }

    await check('成员离线：内容区明确提示，恢复后自动核对身份', async () => {
      await page.locator('[data-mt-tab="startup"]').click(); await page.locator('#mtRunCard').waitFor();
      await S(() => sim.conn.stop());
      await page.locator('.mt-offline').getByText(/执行端离线/).waitFor({ timeout: 10000 });
      if (shots) { await page.screenshot({ path: path.join(shots, 'offline-1440.png') }); report.shots.push('offline-1440'); }
      await executor.evaluate(a => sim.connect(a), { room, secret, uid: 'owner-uid', instanceId: 'inst-exec', name: '我的执行端' });
      await page.locator('#mtRunCard').waitFor({ timeout: 15000 });
    });

    await check('房间断线：进行中的请求按未确认处理，重连后恢复且不重发', async () => {
      await S(() => { sim.mode = 'silent'; });
      const pending = page.evaluate(() => mtSend('startup.status', undefined, { timeout: 20000 }).then(() => 'ok', e => (e.unconfirmed ? 'unconfirmed:' : '') + e.message));
      await page.waitForFunction(() => mt.pending.size > 0);
      const sent = await S(() => sim.requests.filter(r => r.op === 'startup.status').length);
      await page.evaluate(() => connection.stop());
      assert.match(await pending, /^unconfirmed:/);
      await page.locator('.mt-offline').getByText(/房间连接已断开/).waitFor({ timeout: 8000 });
      await S(() => { sim.mode = 'ok'; });
      await page.evaluate(() => joinRoom());
      await page.locator('#mtRunCard').waitFor({ timeout: 15000 });
      await page.waitForTimeout(600);
      assert.ok(await S(n => sim.requests.filter(r => r.op === 'startup.status').length >= n, sent), 'reconnected status poll');
    });

    await check('撤销授权：凭据作废并回到授权卡', async () => {
      await S(() => { sim.owner = { browserId: 'wb-new', secret: 'c'.repeat(64) }; });
      await page.evaluate(() => mtPollStartup());
      await page.locator('.mt-auth').waitFor({ timeout: 8000 });
      assert.equal(await page.evaluate(() => JSON.parse(localStorage.getItem('bgi.mistletoe.owner.v1') || '{}')['inst-exec'] || null), null);
    });

    // 手机：重新授权后检查流程图视图与计划表卡片。
    await S(() => { sim.code = '87654321'; sim.owner = { browserId: 'wb-sim', secret: 'b'.repeat(64) }; });
    await page.locator('#mtAuthCode').fill('87654321'); await page.locator('#mtAuthGo').click();
    await page.locator('#mtRunCard').waitFor({ timeout: 8000 });
    await S(f => { sim.startup = f; sim.startupRevision = 'rev-big2'; }, bigFlow());
    await page.locator('#mtReloadStartup').click(); await page.locator('#mtWorld .mt-node').first().waitFor();
    await page.setViewportSize({ width: 390, height: 844 }); await page.waitForTimeout(300);
    await check('手机：无横向溢出，流程图分页可完整查看并执行', async () => {
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      if (shots) { await page.screenshot({ path: path.join(shots, 'startup-outline-390.png') }); report.shots.push('startup-outline-390'); }
      await page.locator('.mt-pane-switch [data-pane="canvas"]').click(); await page.waitForTimeout(250);
      const fit = await page.evaluate(() => { const s = document.querySelector('#mtStage').getBoundingClientRect(), w = document.querySelector('#mtWorld').getBoundingClientRect(); return { sw: s.width, ww: w.width, zoom: su.view.zoom }; });
      assert.ok(fit.ww <= fit.sw + 2 || fit.zoom <= 0.21, '手机上默认适应整图');
      assert.equal(await page.locator('#mtRunNow').isVisible(), true);
      if (shots) { await page.screenshot({ path: path.join(shots, 'startup-canvas-390.png') }); report.shots.push('startup-canvas-390'); }
      await page.locator('[data-mt-tab="taskcenter"]').click(); await page.locator('#tcNodes .tc-tr[data-node]').first().waitFor();
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      if (shots) { await page.screenshot({ path: path.join(shots, 'taskcenter-390.png'), fullPage: true }); report.shots.push('taskcenter-390'); }
      await layoutAudit(page, '任务中心 390');
      await page.locator('#tcSettings').click(); await page.locator('#tcName').waitFor(); await layoutAudit(page, '计划设置抽屉 390');
      if (shots) { await page.screenshot({ path: path.join(shots, 'plan-settings-390.png') }); report.shots.push('plan-settings-390'); }
      await page.locator('#tcDialog [data-cancel]').click();
    });
    assert.deepEqual(errors, []);
    console.log('PASS mistletoe isolated gateway e2e: ' + report.checks.length + ' checks');
    for (const c of report.checks) console.log(' - ' + c);
    if (shots) await writeFile(path.join(shots, 'report.json'), JSON.stringify(report, null, 2));
  } catch (e) {
    console.error('FAIL mistletoe isolated gateway e2e:', e.stack || e);
    console.error('PASSED SO FAR:', report.checks.join(' | '));
    console.error('SERVER_LOG:', serverLog.slice(-6000)); process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
    server.kill();
  }
})().catch(e => { console.error(e); process.exitCode = 1; });
