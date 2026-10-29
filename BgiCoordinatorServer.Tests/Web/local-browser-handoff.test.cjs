// 真实本机认证管道、独立模拟执行端进程、真实 Edge；不读取真实助手配置。
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const { spawn, execFile } = require('node:child_process');
const { promisify } = require('node:util');
const assert = require('node:assert/strict');
const exec = promisify(execFile);
function ready(stream) {
  return new Promise((resolve, reject) => {
    let text = '';
    const timer = setTimeout(() => reject(new Error('模拟执行端未就绪')), 15000);
    stream.on('data', chunk => { text += chunk; if (text.includes('\n')) { clearTimeout(timer); resolve(text.trim()); } });
    stream.on('error', reject);
  });
}
(async () => {
  const exe = process.env.DODOCO_LOCAL_MOCK_EXE;
  if (!exe) throw new Error('请设置 DODOCO_LOCAL_MOCK_EXE 为新构建的隔离模拟端');
  const executor = spawn(exe, ['--handoff-executor'], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  let browser;
  try {
    assert.equal(await ready(executor.stdout), 'READY');
    const requestLink = async () => (await exec(exe, ['--handoff-client'], { windowsHide: true, timeout: 15000 })).stdout.trim();
    const url = await requestLink();
    assert.match(url, /^http:\/\/127\.0\.0\.1:\d+\/#token=[a-f0-9]{64}$/);
    browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--no-proxy-server'] });
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(url);
    await page.locator('#dodocoView').waitFor({ state: 'visible' });
    await page.getByText('模拟本机日志').first().waitFor();
    assert.equal(await page.evaluate(() => location.hash), '');
    const session = await page.evaluate(() => sessionStorage.getItem('bgi.dodoco.localSession.v1'));
    await page.reload();
    await page.locator('#dodocoView').waitFor({ state: 'visible' });
    assert.equal(await page.evaluate(() => sessionStorage.getItem('bgi.dodoco.localSession.v1')), session);
    const token = new URL(url).hash.slice(7);
    assert.equal(await page.evaluate(async token => (await fetch('/api/pair', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ token })
    })).status, token), 403);
    const next = await requestLink();
    assert.equal(new URL(next).origin, new URL(url).origin, '重复打开必须由同一执行端配对');
    await page.goto(next);
    await page.waitForFunction(previous => {
      const value = sessionStorage.getItem('bgi.dodoco.localSession.v1');
      return value && value !== previous && window.bgiLocalBridge?.available;
    }, session);
    await page.locator('#dodocoView').waitFor({ state: 'visible' });
    assert.notEqual(await page.evaluate(() => sessionStorage.getItem('bgi.dodoco.localSession.v1')), session);
    const blocked = await fetch(new URL(url).origin + '/api/pair', { method: 'POST',
      headers: { Origin: 'https://untrusted.invalid', 'Content-Type': 'application/json' }, body: '{}' });
    assert.equal(blocked.status, 403);
    await page.goto(new URL(url).origin + '/#token=expired-test-token');
    await page.waitForFunction(() => document.getElementById('loginError')?.textContent.includes('配对失败'));
    await page.goto(await requestLink());
    await page.waitForFunction(() => window.bgiLocalBridge?.available && location.hash === '');
    await page.locator('#dodocoView').waitFor({ state: 'visible' });
    assert.deepEqual(errors, []);
    console.log('PASS: independent monitor process discovers authenticated executor, receives executor pairing link; Edge opens local logs, refreshes, reopens in same tab and recovers from expired pairing; token replay and external Origin rejected.');
    const second = spawn(exe, ['--handoff-executor'], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
      env: { ...process.env, DODOCO_TEST_INSTANCE: 'mock-instance-b' } });
    try {
      assert.equal(await ready(second.stdout), 'READY');
      assert.equal((await exec(exe, ['--handoff-client', '--handoff-list'], { windowsHide: true, timeout: 15000 })).stdout.trim(), '2');
      const selected = (await exec(exe, ['--handoff-client', '--instance=mock-instance-b'], { windowsHide: true, timeout: 15000 })).stdout.trim();
      assert.notEqual(new URL(selected).origin, new URL(url).origin, '明确选择第二执行端后链接必须来自第二进程');
      console.log('PASS: multiple executors remain separate; explicit target selection obtains selected executor link.');
    } finally {
      second.stdin.end('\n');
      if (second.exitCode === null) await new Promise(resolve => {
        const timer = setTimeout(() => { second.kill(); resolve(); }, 3000);
        second.once('exit', () => { clearTimeout(timer); resolve(); });
      });
    }
  } finally {
    if (browser) await browser.close();
    executor.stdin.end('\n');
    if (executor.exitCode === null) await new Promise(resolve => {
      const timer = setTimeout(() => { executor.kill(); resolve(); }, 3000);
      executor.once('exit', () => { clearTimeout(timer); resolve(); });
    });
  }
  try {
    await exec(exe, ['--handoff-client'], { windowsHide: true, timeout: 15000 });
    assert.fail('执行端退出后不应还能领取链接');
  } catch (error) {
    assert.match(error.stderr || '', /HANDOFF_TARGET_COUNT=0/);
  }
  console.log('PASS: stopped executor is absent; no substitute monitor pairing URL returned.');
})().catch(error => { console.error(error); process.exitCode = 1; });
