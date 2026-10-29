// 隔离浏览器测试：本地桥采用模拟执行端，另一个 SignalR 连接模拟房间成员。
const http=require('node:http');
const fs=require('node:fs');
const path=require('node:path');
const assert=require('node:assert/strict');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const sharp=require('sharp');
const crypto=require('node:crypto');
const root=path.resolve(__dirname,'../../BgiCoordinatorServer/wwwroot');
const runtime=`
window.remoteCalls=[];window.remoteOptions=null;window.remoteAddr=null;window.localRequests=[];
let handler={},mockRemoteConnection;
window.signalR={HubConnectionBuilder:class { withUrl(url,options){window.remoteAddr=url;window.remoteOptions=options;return this} withAutomaticReconnect(){return this} build(){
 mockRemoteConnection={state:'Connected',on(name,fn){handler[name]=fn},onreconnecting(fn){this.reconnecting=fn},onreconnected(fn){this.reconnected=fn},onclose(fn){this.closed=fn},start:async()=>{},stop:async()=>{},invoke:async(method,envelope)=>{
   remoteCalls.push(envelope); const p=envelope.payload;
   if(envelope.name==='session.hello')return {payload:{capabilities:[]}};
   if(envelope.name==='control.joinRoom'){handler.evt({name:'control.playersUpdated',payload:{full:true,players:[
    {playerUid:'self',playerName:'本机匹配',clientInstanceId:'real-inst',machineId:'machine-a',online:true},
    {playerUid:'other',playerName:'远程成员',clientInstanceId:'other-inst',machineId:'machine-b',online:true}]}});return {payload:{ack:true}};}
   if(envelope.name==='log.requestFiles')queueMicrotask(()=>handler.evt({name:'log.fileList',payload:{uid:p.targetUid,requestId:p.requestId,files:[{name:'remote.log',size:5}]}}));
   if(envelope.name==='dodoco.request')queueMicrotask(()=>handler.evt({name:'dodoco.result',payload:{requestId:p.requestId,status:'ok',data:{rules:[],settings:{muteAll:false,incidentSnapshotEnabled:false,stallThresholdMinutes:3,incidentBufferSeconds:10,incidentCaptureIntervalSeconds:1,incidentPreSeconds:3,incidentPostSeconds:3,incidentRetentionCount:50},dates:[]}}}));
   return {payload:{ack:true}};
 }};window.remoteConnection=mockRemoteConnection;return mockRemoteConnection;}}};`;
function body(req){return new Promise((resolve,reject)=>{let v='';req.on('data',c=>v+=c);req.on('end',()=>{try{resolve(JSON.parse(v));}catch(e){reject(e);}});req.on('error',reject);});}
(async()=>{
 const pixels=crypto.randomBytes(700*700*3);
 const jpeg=await sharp(pixels,{raw:{width:700,height:700,channels:3}}).jpeg({quality:95}).toBuffer();
 assert.ok(jpeg.length>384*1024&&jpeg.length<=1024*1024,'使用高于旧阈值且不超过 1 MiB 的真实 JPEG');
 const raw=Buffer.from('[10:00:00.000] [INF] [本机] 本机历史日志\n');
 const requests=[];let paired=0,expired=false;
 const identity={uid:'self',name:'本机助手',clientInstanceId:'real-inst',machineId:'machine-a',serverUrl:'http://127.0.0.1:5123',roomCode:'room',status:{bgiStatus:'running',taskRunning:true,taskStatusAvailable:true,currentTaskName:'本机任务'}};
 const server=http.createServer(async(req,res)=>{try {
   const u=new URL(req.url,'http://127.0.0.1');
   if(u.pathname==='/api/pair'){const payload=await body(req);assert.equal(payload.token,'valid-pair-token');paired++;res.setHeader('Content-Type','application/json');return res.end(JSON.stringify({sessionId:'test-session',instanceId:'bridge-id'}));}
   if(u.pathname==='/api/execute'){
     const payload=await body(req);requests.push(payload);if(expired||req.headers['x-bgi-session']!=='test-session'){res.writeHead(401);return res.end();}
     let data={};switch(payload.operation){
       case 'local.identity':data=identity;break;
       case 'log.poll':data={cursor:1,lines:payload.args.cursor===null?['[10:00:00.000] [INF] [本机] 本机实时日志']:[],dropped:0,sourceAvailable:true};break;
       case 'log.files':data={files:[{id:'opaque-file',name:'local.log',size:raw.length,group:'本机 BGI',lastWrite:'2026-10-29'}],unavailable:[]};break;
       case 'log.chunk':data={offset:0,total:raw.length,base64:raw.toString('base64'),done:true};break;
       case 'screenshot.capture':data={jpegBase64:jpeg.toString('base64'),width:700,height:700,capturedAt:'2026-10-29T10:00:00'};break;
       case 'watch.read':data={rules:[],settings:{muteAll:false,incidentSnapshotEnabled:false,stallThresholdMinutes:3,incidentBufferSeconds:10,incidentCaptureIntervalSeconds:1,incidentPreSeconds:3,incidentPostSeconds:3,incidentRetentionCount:50},dates:[]};break;
       default:data={};
     }
     res.setHeader('Content-Type','application/json');return res.end(JSON.stringify({requestId:payload.requestId,instanceId:'bridge-id',status:'ok',data}));
   }
   const relative=u.pathname==='/'?'local-dodoco.html':u.pathname.slice(1);
   if(!/^(local-dodoco\.(html|js|css)|project-star\.css|index\.html|control-room(?:-commands|-ui)?\.(js|css)|dodoco-(?:web\.(js|css)|log-worker\.js|operations\.js|remote-logs\.js)|mistletoe-(?:core|startup|startup-view|taskcenter|web)\.js|mistletoe-web\.css|signalr\.min\.js|gengdi-avatars\/[A-Za-z]+\.png)$/.test(relative)){res.writeHead(404);return res.end();}
   res.setHeader('Content-Type',relative.endsWith('.js')?'text/javascript':relative.endsWith('.css')?'text/css':relative.endsWith('.png')?'image/png':'text/html');
   res.end(relative==='signalr.min.js'?fs.readFileSync(path.join(root,relative),'utf8')+'\n;'+runtime:fs.readFileSync(path.join(root,relative)));
 }catch(e){res.writeHead(500);res.end(String(e));}});
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const browser=await chromium.launch({channel:'msedge',headless:true});let context;
 try {
   context=await browser.newContext({viewport:{width:375,height:900}});const page=await context.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
   const base=`http://127.0.0.1:${server.address().port}`;await page.goto(base+'/#token=valid-pair-token');
   try{await page.locator('#dodocoView').waitFor({state:'visible',timeout:5000});}catch(e){throw new Error(`${e.message}; login=${await page.locator('#loginError').textContent()}; errors=${JSON.stringify(errors)}; requests=${JSON.stringify(requests)}`);}await page.getByText('本机实时日志').first().waitFor();
   assert.equal(await page.evaluate(()=>location.hash),'');assert.equal(paired,1);assert.equal(await page.evaluate(()=>remoteCalls.length),0);
   assert.match(await page.locator('#dodocoStatus').textContent(),/本机直连/);
   for(const width of [320,375,1440]){await page.setViewportSize({width,height:900});assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,`${width}px 页面不得横向溢出`);}
   await page.setViewportSize({width:375,height:900});
   await page.locator('[data-tab="files"]').click();await page.locator('#dodocoList').click();await page.getByText('local.log').click();await page.getByText('本机历史日志').waitFor();
   assert.ok(requests.some(x=>x.operation==='log.chunk'&&x.args.id==='opaque-file'));
   await page.locator('[data-tab="screen"]').click();await page.locator('#dodocoCapture').click();
   await page.waitForFunction(()=>document.querySelector('#dodocoFrames img')?.naturalWidth===700);
   await page.reload();await page.locator('#dodocoView').waitFor({state:'visible'});assert.equal(paired,1,'刷新应复用本标签会话');
   await page.getByRole('button',{name:'连接远程房间'}).click();
   for(const width of [320,375,1440]){await page.setViewportSize({width,height:900});const widths=await page.evaluate(()=>[document.querySelector('#localServerUrl').getBoundingClientRect().width,document.querySelector('#roomCode').getBoundingClientRect().width]);assert.ok(Math.abs(widths[0]-widths[1])<1,`${width}px 服务器地址与房间字段应等宽`);assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);}
   await page.setViewportSize({width:375,height:900});await page.locator('#password').fill('room-password');await page.getByRole('button',{name:'进入控制房间'}).click();
   await page.waitForFunction(()=>roomReady===true);assert.equal(await page.evaluate(()=>remoteAddr),'http://127.0.0.1:5123/gateway');assert.equal(await page.evaluate(()=>remoteOptions.withCredentials),false);
   await page.locator('#dodocoMember').selectOption('other');await page.locator('[data-tab="files"]').click();await page.locator('#dodocoList').click();await page.getByText('remote.log').waitFor();
   assert.ok(await page.evaluate(()=>remoteCalls.some(x=>x.name==='log.requestFiles'&&x.payload.targetUid==='other')));
   await page.getByRole('button',{name:'远程日志',exact:true}).click();await page.locator('#rlMember option[value="other"]').waitFor({state:'attached'});assert.match(await page.locator('#rlRoomLabel').textContent(),/本房间/);await page.locator('#rlMember').selectOption('other');const listBefore=await page.evaluate(()=>remoteCalls.filter(x=>x.name==='log.requestFiles').length);await page.locator('#rlFetch').click();await page.locator('#rlFiles').getByText('remote.log').waitFor();assert.equal(await page.evaluate(()=>remoteCalls.filter(x=>x.name==='log.requestFiles').length),listBefore+1,'本机页远程日志经远程房间转发');await page.locator('#rlClose').click();
   const options=await page.locator('#dodocoMember option').allTextContents();assert.ok(options.some(x=>x.includes('本机直连')));assert.ok(!options.some(x=>x.includes('本机匹配')),'精确匹配的本机执行端不重复显示为服务器目标');
   const own=await page.locator('#dodocoMember option').evaluateAll(os=>os.find(o=>o.textContent.includes('本机直连')).value);await page.locator('#dodocoMember').selectOption(own);
   await page.locator('#dodocoList').click();await page.getByText('local.log').waitFor();const remoteBefore=await page.evaluate(()=>remoteCalls.length);
   await page.evaluate(()=>remoteConnection.reconnecting());await page.locator('[data-tab="watch"]').click();await page.locator('#watchRefresh').click();await page.getByRole('heading',{name:'监控规则'}).waitFor();
   assert.equal(await page.evaluate(()=>remoteCalls.length),remoteBefore,'服务器断开时本机请求不回退服务器');
   expired=true;await page.evaluate(()=>bgiLocalBridge.execute('watch.read',{}).catch(()=>{}));
   assert.match(await page.locator('#connectionText').textContent(),/本机连接已断开/);assert.ok(await page.locator('#connDot').evaluate(el=>el.classList.contains('dot-offline')));
   assert.deepEqual(errors,[]);await page.reload();await page.locator('#loginError').getByText(/本机入口/).waitFor();assert.equal(await page.evaluate(()=>sessionStorage.getItem('bgi.dodoco.localSession.v1')),null);
   expired=false;await page.goto(base+'/#token=valid-pair-token');await page.reload();await page.locator('#dodocoView').waitFor({state:'visible'});
   await page.getByRole('button',{name:'连接远程房间'}).click();await page.locator('#password').fill('room-password');await page.getByRole('button',{name:'进入控制房间'}).click();await page.waitForFunction(()=>roomReady===true);
   expired=true;await page.evaluate(()=>bgiLocalBridge.execute('watch.read',{}).catch(()=>{}));
   assert.match(await page.locator('#connectionText').textContent(),/本机连接已断开.*远程已连接/);assert.ok(await page.locator('#connDot').evaluate(el=>el.classList.contains('dot-offline')));
   assert.match(await page.locator('#dodocoStatus').textContent(),/本机连接未就绪/);assert.doesNotMatch(await page.locator('#dodocoStatus').textContent(),/本机直连/);
   assert.ok((await page.locator('#dodocoMember option').allTextContents()).some(x=>x.includes('本机连接已断开')));
   await page.locator('#dodocoMember option[value="self"]').waitFor({state:'attached'});await page.locator('#dodocoMember').selectOption('self');await page.locator('[data-tab="files"]').click();await page.locator('#dodocoList').click();await page.getByText('remote.log').waitFor();
   assert.ok(await page.evaluate(()=>remoteCalls.some(x=>x.name==='log.requestFiles'&&x.payload.targetUid==='self')),'本机断开后仅由用户显式选择服务器目标');
   console.log('PASS local page pairing, refresh, independent local flows, JPEG size, remote route and disconnect');
 } finally {if(context)await context.close();await browser.close();await new Promise(resolve=>server.close(resolve));}
})().catch(e=>{console.error(e);process.exitCode=1;});
