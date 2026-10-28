// 隔离真实本机 TCP 桥 + 真实协调网关 + 模拟成员；不读取真实 User 或配置。
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const {spawn}=require('node:child_process');
const {mkdtemp,mkdir}=require('node:fs/promises');
const path=require('node:path');const os=require('node:os');const net=require('node:net');const assert=require('node:assert/strict');
async function freePort(){const s=net.createServer();await new Promise(r=>s.listen(0,'127.0.0.1',r));const p=s.address().port;await new Promise(r=>s.close(r));return p;}
async function firstLine(stream){return await new Promise((resolve,reject)=>{let b='';const timer=setTimeout(()=>reject(new Error('模拟本机桥未启动')),15000);stream.on('data',chunk=>{b+=chunk.toString();const i=b.indexOf('\n');if(i>=0){clearTimeout(timer);resolve(b.slice(0,i).trim());}});stream.on('error',reject);});}
(async()=>{
 const gatewayDll=process.env.DODOCO_SERVER_DLL,bridgeExe=process.env.DODOCO_LOCAL_MOCK_EXE;
 if(!gatewayDll||!bridgeExe)throw new Error('请设置隔离服务端 DLL 和模拟本机桥路径');
 const isolated=await mkdtemp(path.join(os.tmpdir(),'dodoco-local-gateway-'));await mkdir(path.join(isolated,'room-logs'));
 const port=await freePort(),base=`http://127.0.0.1:${port}`,remoteBase=`http://gateway-check.test:${port}`,room='L'+Date.now().toString(36),secret='simulation-password';
 const gateway=spawn('dotnet',[gatewayDll,'--contentRoot',path.resolve(__dirname,'../../BgiCoordinatorServer')],{windowsHide:true,stdio:['ignore','pipe','pipe'],env:{...process.env,ASPNETCORE_URLS:base,ROOM_LOG_ROOT:path.join(isolated,'room-logs'),NEXUSBGI_DATA_ROOT:path.join(isolated,'user'),LOCALAPPDATA:path.join(isolated,'local')}});
 let bridge,browser,context,serverLog='';gateway.stdout.on('data',x=>serverLog+=x);gateway.stderr.on('data',x=>serverLog+=x);
 try{
   const until=Date.now()+15000;while(Date.now()<until){if(gateway.exitCode!==null)throw new Error('隔离网关意外退出：'+serverLog);try{if((await fetch(base+'/health')).ok)break;}catch{}await new Promise(r=>setTimeout(r,100));}
   if(!(await fetch(base+'/health')).ok)throw new Error('隔离网关未就绪');
   bridge=spawn(bridgeExe,[],{windowsHide:true,stdio:['pipe','pipe','pipe'],env:{...process.env,DODOCO_TEST_SERVER:remoteBase,DODOCO_TEST_ROOM:room,NEXUSBGI_DATA_ROOT:path.join(isolated,'mock-user')}});
   const pairedUrl=await firstLine(bridge.stdout);assert.match(pairedUrl,/^http:\/\/127\.0\.0\.1:\d+\/#token=[a-f0-9]+$/);
   const token=new URL(pairedUrl).hash.slice('#token='.length),bridgeOrigin=new URL(pairedUrl).origin;
   browser=await chromium.launch({channel:'msedge',headless:true,args:['--no-proxy-server','--host-resolver-rules=MAP gateway-check.test 127.0.0.1']});context=await browser.newContext({viewport:{width:375,height:900}});
   const sim=await context.newPage(),page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
   await sim.goto(base);await sim.waitForFunction(()=>typeof signalR!=='undefined');
   await sim.evaluate(async({room,secret})=>{
     let seq=0;const uid='mock-owner',conn=new signalR.HubConnectionBuilder().withUrl('/gateway').build();
     const env=(type,name,payload)=>({protocolVersion:3,type,name,payload,requestId:'remote'+(++seq),sentAtUtc:new Date().toISOString()});
     const send=(name,payload)=>conn.invoke('Dispatch',env('command',name,{roomCode:room,...payload}));
     window.remoteSim={conn,send,events:[]};conn.on('evt',async evt=>{const x=evt.payload||{};remoteSim.events.push(evt.name);
       if(evt.name==='log.subscribersChanged'&&x.count>0)await send('log.reportBatch',{uid,senderName:'另一台电脑',lines:['[10:00:00.000] [INF] [Remote] 真实网关远程日志'],infoOnly:false});
       if(evt.name==='log.filesRequested')await send('log.reportFiles',{uid,requestId:x.requestId,files:[{name:'remote.log',size:12,lastWrite:'2026-10-29T10:00:00'}]});
     });
     await conn.start();await conn.invoke('Query',env('query','session.hello',{clientKind:'assistant',clientVersion:'sim',protocolVersion:3,capabilities:[]}));
     await send('control.joinRoom',{password:secret,playerUid:uid,playerName:'另一台电脑',allowedUids:[],isRemote:false,clientInstanceId:'other-instance',machineId:'other-machine'});
   },{room,secret});
   await page.goto(pairedUrl);await page.locator('#dodocoView').waitFor({state:'visible'});await page.getByText('模拟本机日志').first().waitFor();
   assert.equal(await page.evaluate(()=>location.hash),'');assert.equal(await sim.evaluate(()=>remoteSim.events.includes('log.subscribersChanged')),false,'本机日志不得经服务器订阅');
   for(const width of [320,375,1440]){await page.setViewportSize({width,height:900});assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,`${width}px 横向溢出`);}
   await page.setViewportSize({width:375,height:900});await page.locator('[data-tab="screen"]').click();await page.locator('#dodocoCapture').click();await page.waitForFunction(()=>document.querySelector('#dodocoFrames img')?.naturalWidth===16);
   await page.reload();await page.locator('#dodocoView').waitFor({state:'visible'});await page.getByText('模拟本机日志').first().waitFor();
   const session=await page.evaluate(()=>JSON.parse(sessionStorage.getItem('bgi.dodoco.localSession.v1')).sessionId);
   const crossOrigin=await sim.evaluate(async({bridgeOrigin,session})=>{try{await fetch(bridgeOrigin+'/api/execute',{method:'POST',headers:{'Content-Type':'application/json','X-BGI-Session':session},body:JSON.stringify({requestId:'bad',operation:'watch.read',args:{}})});return 'unexpected';}catch{return 'blocked';}},{bridgeOrigin,session});
   assert.equal(crossOrigin,'blocked','其他来源页面不得调用本机桥');
   const replay=await page.evaluate(async token=>{const r=await fetch('/api/pair',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({token})});return r.status;},token);assert.equal(replay,403,'一次性配对令牌不能重放');
   await page.getByRole('button',{name:'连接远程房间'}).click();assert.equal(await page.locator('#localServerUrl').inputValue(),remoteBase,'使用映射回隔离网关的非 localhost HTTP 域名');await page.locator('#password').fill(secret);await page.getByRole('button',{name:'进入控制房间'}).click();
   await page.waitForFunction(()=>roomReady===true,{timeout:12000});await page.locator('#dodocoMember option[value="mock-owner"]').waitFor({state:'attached'});
   await page.locator('#dodocoMember').selectOption('mock-owner');await page.getByText('真实网关远程日志').first().waitFor({timeout:10000});
   assert.match(await page.locator('#dodocoStatus').textContent(),/服务器中转/,'同 UID 但不同机器和实例必须走服务器');
   await page.locator('[data-tab="files"]').click();await page.locator('#dodocoList').click();await page.getByText('remote.log').waitFor();
   assert.ok(await sim.evaluate(()=>remoteSim.events.includes('log.filesRequested')),'远程请求必须到达网关成员');
   const local=await page.locator('#dodocoMember option').evaluateAll(os=>os.find(o=>o.textContent.includes('本机直连')).value);await page.locator('#dodocoMember').selectOption(local);
   await page.locator('[data-tab="watch"]').click();await page.locator('#watchRefresh').click();await page.getByRole('heading',{name:'监控规则'}).waitFor();
   assert.equal(await sim.evaluate(()=>remoteSim.events.includes('dodoco.requested')),false,'本机异常请求不得经服务器');
   gateway.kill();await page.waitForFunction(()=>roomReady===false,null,{timeout:15000});
   await page.locator('#watchRefresh').click();await page.getByRole('heading',{name:'监控规则'}).waitFor();assert.match(await page.locator('#dodocoStatus').textContent(),/本机直连/);
   assert.deepEqual(errors,[]);console.log('PASS real local bridge + real gateway: pairing, F5, cross-origin rejection, local/remote routing and offline local');
 }finally{
   if(context)await context.close();if(browser)await browser.close();if(bridge){bridge.stdin.write('\n');await new Promise(r=>{bridge.once('exit',r);setTimeout(()=>{if(bridge.exitCode===null)bridge.kill();r();},3000);});}
   if(gateway.exitCode===null)gateway.kill();
 }
})().catch(e=>{console.error(e);process.exitCode=1;});
