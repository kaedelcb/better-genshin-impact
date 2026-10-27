// Real Edge DOM/layout and event routing, with a fully inert SignalR transport.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../../BgiCoordinatorServer/wwwroot');
const mock = `
window.gatewayCalls = [];
window.testMembers = [
 {playerUid:'a',playerName:'成员甲',online:true,bgiStatus:'running',configGroups:['联机锄地-精英','一条龙日常']},
 {playerUid:'b',playerName:'成员乙',online:true,bgiStatus:'running'},
 {playerUid:'off',playerName:'离线成员',online:false}];
const handlers = {};
window.emitResult = (uid,status='success') => {
 const cmd = gatewayCalls.filter(x=>x.name==='control.sendCommand').at(-1).payload.command;
 handlers.evt({name:'control.remoteCommandResult',payload:{cmd:cmd.cmd,commandId:cmd.commandId,targetUid:uid,status,message:status==='failed'?'执行端未完成清理':''}});
};
const transport = {
 on(name,fn){handlers[name]=fn;}, onreconnecting(fn){this.reconnecting=fn;}, onreconnected(){}, onclose(){},
 start:async()=>{}, invoke:async(method,env)=>{
  gatewayCalls.push(env);
  if(env.name==='session.hello')return {payload:{capabilities:[]}};
  if(env.name==='control.joinRoom'){handlers.evt({name:'control.playersUpdated',payload:{full:true,players:testMembers}});return {payload:{ack:true}};}
  if(env.name==='control.batch.current')return {payload:{batchId:'batch-1',generation:7}};
  if(env.name==='control.batch.reset')return {payload:{resetId:env.payload.resetId,batchId:'batch-1',generation:7}};
  return {payload:{ack:true}};
 }
};
window.signalR={HubConnectionBuilder:class {withUrl(){return this;}withAutomaticReconnect(){return this;}build(){return transport;}}};
`;
(async () => {
    const browser = await chromium.launch({ channel: 'msedge', headless: true });
    try {
        const page = await browser.newPage({ viewport: { width: 360, height: 800 } });
        const errors = []; page.on('pageerror', err => errors.push(err.message));
        await page.route('**/*', route => {
            const url = new URL(route.request().url());
            if (url.hostname !== '127.0.0.1') return route.abort();
            const file = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
            if (!['index.html', 'control-room.js', 'control-room.css', 'signalr.min.js'].includes(file)) return route.abort();
            return route.fulfill({ body: file === 'signalr.min.js' ? mock : fs.readFileSync(path.join(root, file)),
                contentType: file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'text/html' });
        });
        await page.goto('http://127.0.0.1:48279/');
        await page.locator('#roomCode').fill('test'); await page.locator('#password').fill('test');
        await page.getByRole('button', { name: '进入控制房间' }).click();
        await page.locator('#controlPanel').waitFor({ state: 'visible', timeout: 5000 }).catch(async err => {
            console.error({ errors, loginError: await page.locator('#loginError').textContent() }); throw err;
        });
        const resetButton = page.locator('[data-action="reset-state"][data-uid="a"]');
        assert.equal(await page.locator('[data-action="reset-state"][data-uid="off"]').isDisabled(), true);
        const layout = await page.locator('.member-card').first().locator('.action-btn').evaluateAll(buttons => buttons.map(b => {
            const r = b.getBoundingClientRect(); return { x:r.x,y:r.y,right:r.right,width:r.width,scroll:b.scrollWidth,client:b.clientWidth };
        }));
        assert.equal(layout.length, 6); assert.equal(layout[0].y, layout[2].y);
        assert(layout[3].y > layout[0].y); assert.equal(layout[3].y, layout[5].y);
        assert(layout.every(b => b.right <= 360 && b.scroll <= b.client));
        await resetButton.click(); await page.getByRole('button', { name: '取消', exact: true }).click();
        assert.equal(await page.evaluate(() => gatewayCalls.filter(x => x.name === 'control.batch.reset').length), 0);
        await resetButton.click(); await page.getByRole('button', { name: '确认重置', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.reset-state-result').textContent.includes('等待执行端确认'));
        assert.equal(await resetButton.isDisabled(), true);
        await page.evaluate(() => emitResult('a'));
        await page.waitForFunction(() => document.querySelector('.reset-state-result').textContent.includes('均已确认重置成功'));
        assert.equal(await page.getByRole('button', { name: '确认重置', exact: true }).isVisible(), false);
        await page.getByRole('button', { name: '关闭', exact: true }).click();
        await resetButton.click(); await page.locator('.reset-state-all').check();
        await page.getByRole('button', { name: '确认重置', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.reset-state-result').textContent.includes('等待执行端确认'));
        const targets = await page.evaluate(() => gatewayCalls.filter(x => x.name === 'control.sendCommand').at(-1).payload.command.target);
        assert.deepEqual(targets, ['a', 'b']);
        await page.evaluate(() => { emitResult('a'); emitResult('b','failed'); });
        await page.waitForFunction(() => document.querySelector('.reset-state-result').textContent.includes('成员乙：重置未完成'));
        if (process.env.WEB_RESET_SCREENSHOT) await page.screenshot({ path: process.env.WEB_RESET_SCREENSHOT });
        await page.getByRole('button', { name: '关闭', exact: true }).click();
        await page.setViewportSize({ width: 1024, height: 900 });
        assert.equal(await resetButton.isEnabled(), true);
        assert.deepEqual(errors, []);
        console.log('PASS Edge: 360px mobile 3x2 layout, offline disabled, cancel, single/all, receipt events, partial failure, busy state, 1024px desktop; inert gateway only.');
    } finally { await browser.close(); }
})().catch(err => { console.error(err); process.exitCode = 1; });
