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
  state.registers = {available:true,entries:Array.from({length:10},(_,i)=>({number:i+1,name:'',value:i<2?1:0,resetMode:'none',resetHour:4,resetMinute:0,delaySeconds:1800,settingsRevision:'settings-1'}))};
  state.flow = {schema:'mistletoe.workflow',schemaVersion:1,workflowId:'wf-local',name:'今日计划',nodes:[],future:{keep:true}};
  state.flowRevision = 'flow-1';
  const unified = () => ({version:1,taskCenterAvailable:true,sequence:1,epoch:'local',capturedAtUtc:new Date().toISOString(),items:[{runtimeId:'trigger',source:'taskCenter',kind:'log',text:'今日监听',workflowId:'wf-local',flowNodeId:'flow-node',stepId:'log-node',runId:'run',canCancel:true}]});
  const identity = { uid: 'owner-uid', name: '本机助手', clientInstanceId: 'inst-local', machineId: 'm-local', serverUrl: 'http://127.0.0.1:1', roomCode: 'room', isObserver: false };
  function center(req) {
    const op = req.operation, id = 'web-' + req.requestId;
    const reply = (status, message, data, revision, operationId) => ({ status, message: message || '', data: data ?? null, revision: revision ?? null, operationId: operationId ?? null, targetInstanceId: 'inst-local' });
    if (op !== 'web.identity' && req.targetInstanceId !== 'inst-local') return reply('rejected', '执行端实例已变化');
    if (req.browserSecret) return reply('rejected', '本机请求不应携带浏览器凭据');
    switch (op) {
      case 'web.identity': return reply('ok', '', { uid: 'owner-uid', name: '本机助手', instanceId: 'inst-local', isExecutor: true, owner: true });
      case 'startup.get': return reply('ok', '', { config: state.startup, revision: state.revision, schemes: [], schemesRevision: 's1', IsRunning: false, StatusText: '', autoLaunchOnBoot: false, autoLaunchOnBootModeIndex: 0, autoLaunchRevision: 'a1', registers:state.registers, unifiedTriggers:unified(),triggers: [], prompts: [], nodeStates: [] }, state.revision);
      case 'startup.status': return reply('ok', '', { isRunning: false, statusText: '', registers:state.registers,unifiedTriggers:unified(),triggers: [], prompts: [], nodeStates: [],taskPrompts:state.prompts||[] });
      case 'registers.get': return reply('ok','',state.registers);
      case 'registers.configure': {
        const e=state.registers.entries[req.payload.number-1];
        if(e.settingsRevision!==req.payload.expectedSettingsRevision) return reply('rejected','规则已变化');
        Object.assign(e,req.payload,{settingsRevision:'settings-2'});return reply('ok','规则已保存',state.registers);
      }
      case 'registers.reset': state.registers.entries.filter(e=>req.payload.all||e.number===req.payload.number).forEach(e=>e.value=0);return reply('ok','已归零',state.registers);
      case 'registers.set': {
        const {number,value}=req.payload||{};
        if(!Number.isInteger(number)||number<1||number>10||![0,1].includes(value)) return reply('rejected','位号或设置值无效');
        state.registers.entries[number-1].value=value;return reply('ok','已设置',state.registers);
      }
      case 'task.list': return reply('ok','',{flows:[{workflowId:'wf-local',name:state.flow.name,revision:state.flowRevision,unsupportedKinds:[]}],active:[],history:[],catalog:{entries:[]},allowedTaskPrograms:[],serverTime:new Date().toISOString()});
      case 'task.flow.get': return reply('ok','',state.flow,state.flowRevision);
      case 'task.run.start': {
        if(req.payload.expectedRevision!==state.flowRevision) return reply('rejected','启动修订已变化');
        const result=reply('ok','测试执行端已受理',null,null,id);state.ops.set(id,result);return result;
      }
      case 'task.flow.save':
        if(req.expectedRevision!==state.flowRevision||req.flowEditVersion!==1) return reply('rejected','修订或编辑版本错误');
        state.flow=req.payload;state.flowRevision='flow-'+Date.now();return reply('ok','已保存',null,state.flowRevision);
      case 'task.prompt.answer': state.prompts=[];return reply('ok','已回答');
      case 'task.trigger.cancel': return reply('ok','已撤销');
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
      if (!/^(local-dodoco\.(html|js|css)|project-star\.css|control-room(?:-commands|-ui)?\.(js|css)|dodoco-(?:web\.(js|css)|log-worker\.js|operations\.js)|mistletoe-(?:core|registers|composition|startup|startup-view|taskcenter|web)\.js|mistletoe-web\.css|signalr\.min\.js|gengdi-avatars\/[A-Za-z]+\.png)$/.test(rel)) { res.writeHead(404); return res.end(); }
      res.setHeader('Content-Type', rel.endsWith('.js') ? 'text/javascript' : rel.endsWith('.css') ? 'text/css' : rel.endsWith('.png') ? 'image/png' : 'text/html');
      res.end(rel === 'signalr.min.js' ? fs.readFileSync(path.join(root, rel), 'utf8') + '\n;' + mock : fs.readFileSync(path.join(root, rel)));
    } catch (e) { res.writeHead(500); res.end(String(e)); }
  });
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  const browser = await chromium.launch({ headless: true, executablePath: process.env.EDGE_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' });
  let page; const errors = [];
  try {
    page = await browser.newPage({ viewport: { width: 1280, height: 860 } }); page.on('pageerror', e => errors.push(e.message));
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
    // 寄存器真实控件：归一/归零保留草稿，全部归零经原生确认；保存后另一个中心读同一份状态。
    const registerWrites=operation=>calls.filter(c=>c.operation==='mistletoe.request'&&c.args?.request?.operation===operation);
    const clickWithoutConfirm=async(selector,label)=>{
      const unexpected=[];
      const dismiss=dialog=>{unexpected.push(dialog.type()+':'+dialog.message());void dialog.dismiss();};
      page.on('dialog',dismiss);
      try {await page.locator(selector).click();await page.waitForFunction(()=>!mtRegisters.busy);assert.deepEqual(unexpected,[],label+'不应出现确认框');}
      finally {page.off('dialog',dismiss);}
    };
    const clickWithConfirm=async(selector,accept,label)=>{
      const nativeDialog=new Promise((resolve,reject)=>{
        const handler=dialog=>{
          clearTimeout(timer);
          const observed={type:dialog.type(),message:dialog.message()};
          (accept?dialog.accept():dialog.dismiss()).then(()=>resolve(observed),reject);
        };
        const timer=setTimeout(()=>{page.off('dialog',handler);reject(new Error(label+'未出现原生确认框'));},4000);
        page.once('dialog',handler);
      });
      const [dialog]=await Promise.all([nativeDialog,page.locator(selector).click()]);
      assert.equal(dialog.type,'confirm',label+'使用原生 confirm');assert.match(dialog.message,/R1–R10.*全部归零/);
      await page.waitForFunction(()=>!mtRegisters.busy);
    };
    const verifyAllResetConfirmation=async(selector,label)=>{
      const before=registerWrites('registers.reset').length,values=state.registers.entries.map(e=>e.value);
      await clickWithConfirm(selector,false,label+'取消');
      assert.equal(registerWrites('registers.reset').length,before,label+'取消不发送归零请求');
      assert.deepEqual(state.registers.entries.map(e=>e.value),values,label+'取消保留原状态');
      await clickWithConfirm(selector,true,label+'确认');
      assert.equal(registerWrites('registers.reset').length,before+1,label+'确认只发送一次归零请求');
      assert.deepEqual(registerWrites('registers.reset').at(-1).args.request.payload,{all:true});
      assert.ok(state.registers.entries.every(e=>e.value===0),label+'确认后全部归零');
    };
    await page.evaluate(()=>{
      window.registerTrace=[];const open=mtOpenRegisterSettings;
      mtOpenRegisterSettings=n=>{registerTrace.push({number:n,canWrite:mtRegisterCanWrite(),ready:mtTargetReady(),owner:mtOwner()});return open(n);};
      for(const type of ['pointerdown','pointerup','click'])document.addEventListener(type,e=>registerTrace.push({type,target:e.target.outerHTML.slice(0,130),x:e.clientX,y:e.clientY}),true);
      new MutationObserver(()=>registerTrace.push({mutation:true})).observe(mtEl('mtStartupRegisters'),{childList:true,subtree:true});
    });
    for(const width of [375,1440]) {
      await page.setViewportSize({width,height:900});
      const cells=await page.locator('#mtStartupRegisters [data-register-number]').evaluateAll(buttons=>buttons.map(button=>{
        const b=button.getBoundingClientRect();return {x:b.x,y:b.y,right:b.right,width:b.width,height:b.height,label:button.getAttribute('aria-label')};
      }));
      assert.equal(cells.length,10);
      assert.ok(cells.every(b=>b.x>=0&&b.right<=width&&b.width>0&&b.height>0&&b.label.includes('状态')),width+'px 十位完整可见且保留位号说明');
      assert.ok(cells.every(b=>Math.abs(b.y-cells[0].y)<1),width+'px 十格保持同一行');
      if(width>700) assert.ok(cells.every(b=>b.width<=30&&b.height<=30),'桌面启动寄存器使用紧凑小格');
      else assert.ok(cells.every(b=>b.height>=44),'手机保持点击高度');
      assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,width+'px 启动中心无横向溢出');
      if(process.argv[2]) await page.locator('#mtStartupRegisters').screenshot({path:path.join(process.argv[2],`startup-registers-${width}.png`)});
    }
    await page.setViewportSize({width:1280,height:860});
    await page.locator('[data-register-number="1"]').click();
    await page.locator('[data-register-setting="name"]').waitFor({timeout:4000});
    await page.locator('[data-register-setting="name"]').fill('今日采集');
    await page.locator('[data-register-setting="resetMode"]').selectOption('daily');
    await page.locator('[data-register-setting="resetTime"]').fill('05:30');
    const otherRegisters=structuredClone(state.registers.entries.slice(1)),savedSettings=structuredClone(state.registers.entries[0]);
    const resetsBefore=registerWrites('registers.reset').length;
    await clickWithoutConfirm('#mtRegisterDialog [data-register-reset="one"]','本位归零');
    assert.equal(registerWrites('registers.reset').length,resetsBefore+1);
    assert.deepEqual(registerWrites('registers.reset').at(-1).args.request.payload,{number:1});
    assert.equal(state.registers.entries[0].value,0);
    const setsBefore=registerWrites('registers.set').length;
    await clickWithoutConfirm('#mtRegisterDialog [data-register-set-one]','本位归一');
    assert.equal(registerWrites('registers.set').length,setsBefore+1);
    assert.deepEqual(registerWrites('registers.set').at(-1).args.request.payload,{number:1,value:1});
    assert.equal(state.registers.entries[0].value,1);
    assert.deepEqual(state.registers.entries.slice(1),otherRegisters,'本位归一保留其他位');
    assert.deepEqual(state.registers.entries[0],savedSettings,'归一和归零不保存草稿中的名称和规则');
    assert.equal(await page.locator('#mtRegisterDialog').evaluate(dialog=>dialog.open),true,'本位操作不关闭设置窗');
    assert.equal(await page.locator('[data-register-setting="name"]').inputValue(),'今日采集');
    assert.equal(await page.locator('[data-register-setting="resetMode"]').inputValue(),'daily');
    assert.equal(await page.locator('[data-register-setting="resetTime"]').inputValue(),'05:30');
    for(const width of [375,1440]) {
      await page.setViewportSize({width,height:900});
      const layout=await page.locator('#mtRegisterDialog').evaluate(dialog=>{
        const bounds=dialog.getBoundingClientRect();
        const buttons=[...dialog.querySelectorAll('.mt-register-settings .dd-actions button')].map(button=>{
          const b=button.getBoundingClientRect();return {text:button.textContent,x:b.x,y:b.y,right:b.right,bottom:b.bottom,width:b.width,height:b.height};
        });
        return {x:bounds.x,right:bounds.right,clientWidth:dialog.clientWidth,scrollWidth:dialog.scrollWidth,pageFits:document.documentElement.scrollWidth<=innerWidth,buttons};
      });
      assert.equal(layout.pageFits,true,width+'px 寄存器设置页横向溢出');
      assert.ok(layout.x>=-1&&layout.right<=width+1,width+'px 寄存器设置窗位于视口内');
      assert.ok(layout.scrollWidth<=layout.clientWidth+1,width+'px 寄存器设置窗内容横向溢出');
      assert.deepEqual(layout.buttons.map(b=>b.text),['本位归一','本位归零','全部归零','保存设置']);
      for(let i=0;i<layout.buttons.length;i++) {
        const a=layout.buttons[i];
        assert.ok(a.width>0&&a.height>0&&a.x>=layout.x-1&&a.right<=layout.right+1,width+'px 操作按钮位于设置窗内');
        for(const b of layout.buttons.slice(i+1)) assert.ok(a.right<=b.x+1||b.right<=a.x+1||a.bottom<=b.y+1||b.bottom<=a.y+1,width+'px '+a.text+'与'+b.text+'重叠');
      }
      if(process.argv[2]) await page.screenshot({path:path.join(process.argv[2],`register-settings-${width}.png`)});
    }
    await page.setViewportSize({width:1280,height:860});
    await verifyAllResetConfirmation('#mtRegisterDialog [data-register-reset="all"]','设置窗全部归零');
    assert.equal(await page.locator('#mtRegisterDialog').evaluate(dialog=>dialog.open),true);
    assert.equal(await page.locator('[data-register-setting="name"]').inputValue(),'今日采集');
    assert.equal(await page.locator('[data-register-setting="resetMode"]').inputValue(),'daily');
    await clickWithoutConfirm('#mtRegisterDialog [data-register-set-one]','全部归零后本位归一');
    await page.locator('[data-register-save]').click();
    await page.locator('#mtRegisterDialog').waitFor({state:'hidden'});
    assert.equal(state.registers.entries[0].resetHour,5);
    assert.equal(state.registers.entries[0].resetMinute,30);
    await verifyAllResetConfirmation('#mtStartupRegisters [data-register-reset-all]','启动中心状态条全归零');
    // 独立流程、寄存器设置和日志触发器，全部通过正式编辑控件往返。
    await page.locator('[data-mt-tab="taskcenter"]').click();
    await page.locator('#tcRegisters [data-register-number="2"]').click();
    await clickWithoutConfirm('#mtRegisterDialog [data-register-set-one]','任务中心本位归一');
    await page.locator('#mtRegisterDialog [data-register-close]').click();
    await page.locator('#mtRegisterDialog').waitFor({state:'hidden'});
    await verifyAllResetConfirmation('#tcRegisters [data-register-reset-all]','任务中心状态条全归零');
    await page.locator('#tcAdd').click();await page.locator('[data-kind="flow"]').click();
    await page.locator('#tcFlowDialog [data-flow-name]').fill('采集前准备');
    await page.locator('#tcFlowDialog [data-add="setRegister"]').click();
    await page.locator('#tcFlowDialog [data-register-select="0"]').selectOption('1');
    await page.locator('#tcFlowDialog [data-main]').click();
    await page.locator('#tcFlowDialog [data-add="logTrigger"]').click();
    await page.locator('#tcFlowDialog [data-param="logKeyword"]').fill('采集完成');
    await page.locator('#tcFlowDialog [data-apply]').click();
    await page.locator('#tcFlowDialog').waitFor({state:'hidden'});
    await page.waitForFunction(()=>!tc.dirty&&!tc.saving);
    assert.equal(state.flow.nodes[0].flow.steps[0].registerWrite.values[0],1);
    assert.equal(state.flow.nodes[0].flow.steps[1].logKeyword,'采集完成');
    assert.equal(state.flow.future.keep,true);
    await page.locator('#tcAdd').click();await page.locator('[data-kind="lanes"]').click();
    await page.locator('#tcLaneDialog [data-name="name"]').fill('今日分流');
    await page.locator('#tcLaneDialog [data-condition]').selectOption('judgeRegister');
    await page.locator('#tcLaneDialog [data-register-select="0"]').selectOption('1');
    await page.locator('#tcLaneDialog [data-apply]').click();
    await page.locator('#tcLaneDialog').waitFor({state:'hidden'});
    await page.waitForFunction(()=>!tc.dirty&&!tc.saving);
    assert.equal(state.flow.nodes[1].lanes.decision.steps[0].registerCondition.values[0],1);
    assert.ok(calls.some(c=>c.args?.request?.operation==='task.flow.save'&&c.args.request.flowEditVersion===1));
    // 局部窗口取消不改变计划，刷新重读保存内容。
    const editFirstFlow=async()=>{
      const row=page.locator('[data-composition]').first();
      if(await row.getAttribute('open')===null)await row.locator('summary').first().click();
      await row.locator('[data-action="edit"]').click();
    };
    await editFirstFlow();
    await page.locator('#tcFlowDialog [data-flow-name]').fill('不应保存');
    await page.locator('#tcFlowDialog [data-cancel]').click();
    assert.equal(state.flow.nodes[0].flow.name,'采集前准备');
    await page.reload();await page.locator('#mistletoeProduct').click();
    await page.locator('#mtRunCard').waitFor();await page.locator('[data-mt-tab="taskcenter"]').click();
    await page.locator('[data-composition]').first().waitFor();
    assert.equal(await page.locator('[data-register-number="1"]').getAttribute('title').then(x=>x.includes('今日采集')),true);
    for(const width of [375,768,1440]) {
      await page.setViewportSize({width,height:900});
      assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,width+'px 编排页横向溢出');
      await editFirstFlow();
      const bounds=await page.locator('#tcFlowDialog').boundingBox();assert.ok(bounds.x>=-1&&bounds.x+bounds.width<=width+1,'流程窗口边界');
      if(process.argv[2]&&[375,1440].includes(width)) await page.screenshot({path:path.join(process.argv[2],`composition-${width}.png`)});
      await page.locator('#tcFlowDialog [data-cancel]').click();
    }
    // 从真实更多菜单选择中途起点；模拟桥只确认协议，绝不调用 BGI。
    const entryResource=(id,name)=>({nodeId:id,kind:'resource.configGroup',ref:{config:name,revision:'ref-1'},strategies:[]});
    const laneId=state.flow.nodes[1].nodeId;
    state.flow.nodes.splice(1,0,entryResource('entry-between','流程与车道之间的 A'));
    state.flow.nodes[2].lanes.yesNodeIds=['entry-yes'];state.flow.nodes[2].lanes.noNodeIds=['entry-no'];
    state.flow.nodes.push(entryResource('entry-yes','上车道 B'),entryResource('entry-no','下车道 C'));
    state.flowRevision='flow-entry-fixture';
    await page.evaluate(async()=>{await tcOpen('wf-local',true);tcRenderPlanHead();tcRenderNodes();});
    const starts=()=>calls.filter(c=>c.args?.request?.operation==='task.run.start').map(c=>c.args.request.payload);
    const settleStart=()=>page.waitForFunction(()=>!tc.starting&&!tc.saving&&!tc.dirty&&!mt.unconfirmed);
    await page.locator('[data-resource="entry-between"] [data-action="more"]').click();
    await page.locator('#tcDialog [data-a="mark"]').click();
    await page.waitForFunction(()=>!tc.dirty&&!tc.saving&&tcStartMarker()==='entry-between');
    assert.equal(state.flow.taskCenterStartNodeId,'entry-between');
    await page.locator('#tcStart').click();await settleStart();
    assert.equal(starts().length,1,'连续启动只发送一次，不因编排改为链首或重复请求');
    assert.equal(starts()[0].entryNodeId,'entry-between');assert.equal(starts()[0].stopAfterNodeId,null);
    assert.equal(state.flow.taskCenterStartNodeId,undefined,'受理后清除一次性起点标记');
    const laneRow=page.locator(`[data-composition="${laneId}"]`);
    if(await laneRow.getAttribute('open')===null) await laneRow.locator('summary').first().click();
    await page.locator('[data-resource="entry-no"] [data-action="more"]').click();
    assert.match(await page.locator('#tcDialog').textContent(),/沿当前路线剩余任务继续，再回到主表/);
    await page.locator('#tcDialog [data-a="single"]').click();await settleStart();
    assert.equal(starts().length,2);assert.equal(starts()[1].entryNodeId,'entry-no');assert.equal(starts()[1].stopAfterNodeId,'entry-no');
    if(await laneRow.getAttribute('open')===null) await laneRow.locator('summary').first().click();
    await laneRow.locator(':scope > .dd-actions [data-action="more"]').click();
    assert.equal(await page.locator('#tcDialog [data-a="single"]').count(),0,'车道结构不显示单任务按钮');
    await page.locator('#tcDialog [data-a="from"]').click();await settleStart();
    assert.equal(starts().length,3);assert.equal(starts()[2].entryNodeId,laneId);assert.equal(starts()[2].stopAfterNodeId,null);
    assert.ok(starts().every(r=>r.workflowId==='wf-local'&&r.expectedRevision),'全部入口绑定当前计划和保存修订');
    // 共用运行触发器和任务人工确认在任务页可操作。
    assert.equal(await page.locator('#mtTriggers').count(),1);
    state.prompts=[{source:'taskCenter',runtimeId:'ask-task',message:'是否继续',yesPreview:'继续',noPreview:'跳过',remainingSeconds:30}];
    await page.evaluate(()=>mtPollStartup());
    await page.locator('#mtTaskPrompts [data-answer="1"]').click();
    await page.waitForFunction(()=>!su.prompts.length);
    assert.ok(calls.some(c=>c.args?.request?.operation==='task.prompt.answer'));
    await page.locator('[data-mt-tab="startup"]').click();await page.locator('#mtRunCard').waitFor();
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
    console.log('PASS mistletoe local direct: pairing, register set/reset controls and native confirmations, register settings 375/1440 layout, flow/lane controls and persistence, middle/branch/lane entry controls and request boundaries, shared triggers/prompts, 375/768/1440 layout, revisions, terminal/unknown/timeout, expired session');
  } catch(error) {
    console.error('BROWSER ERRORS',errors);
    if(page) {
      console.error('PAGE STATE',await page.evaluate(()=>({status:mtEl('mtStatus')?.textContent,ready:mtTargetReady(),owner:mtOwner(),trace:window.registerTrace,handler:String(document.querySelector('[data-register-number="1"]')?.onclick),registers:mtRegisters,dialogs:[...document.querySelectorAll('dialog')].map(x=>({id:x.id,open:x.open,text:x.textContent.slice(0,500)}))})).catch(()=>null));
      if(process.argv[2]) await page.screenshot({path:path.join(process.argv[2],'failure.png')}).catch(()=>{});
    }
    throw error;
  } finally { await browser.close(); await new Promise(r => server.close(r)); }
})().catch(e => { console.error(e); process.exitCode = 1; });
