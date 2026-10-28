// Isolated, real /gateway round trip through the production page and a simulated member.
// Run after building BgiCoordinatorServer to DODOCO_SERVER_DLL; NODE_PATH must provide Playwright.
const { chromium } = require('playwright');
const { spawn } = require('node:child_process');
const { mkdtemp, mkdir } = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const net = require('node:net');
const zlib = require('node:zlib');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const JSZip = require('jszip');

async function freePort() {
  const server = net.createServer();
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  await new Promise(resolve => server.close(resolve));
  return port;
}
(async () => {
  const exe = process.env.DODOCO_SERVER_DLL;
  if (!exe) throw new Error('DODOCO_SERVER_DLL must name the freshly built server DLL');
  const root = path.resolve(__dirname, '../../BgiCoordinatorServer');
  const isolated = await mkdtemp(path.join(os.tmpdir(), 'bgi-dodoco-web-e2e-'));
  await mkdir(path.join(isolated, 'room-logs'));
  const port = await freePort(), base = `http://127.0.0.1:${port}`;
  const server = spawn('dotnet', [exe, '--contentRoot', root], {
    env: {...process.env, ASPNETCORE_URLS:base, ROOM_LOG_ROOT:path.join(isolated,'room-logs'),
      NEXUSBGI_DATA_ROOT:path.join(isolated,'user'), LOCALAPPDATA:path.join(isolated,'localappdata')}, windowsHide:true, stdio:['ignore','pipe','pipe']
  });
  let serverLog='';
  server.stdout.on('data', data => serverLog += data); server.stderr.on('data', data => serverLog += data);
  let browser;
  try {
    const deadline=Date.now()+15000;
    while (Date.now()<deadline) {
      if (server.exitCode !== null) throw new Error(`isolated server exited ${server.exitCode}: ${serverLog}`);
      try { if ((await fetch(base+'/health')).ok) break; } catch {}
      await new Promise(r=>setTimeout(r,100));
    }
    if (!(await fetch(base+'/health')).ok) throw new Error('server health unavailable');
    browser=await chromium.launch({headless:true,executablePath:process.env.EDGE_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'});
    const context=await browser.newContext({viewport:{width:390,height:844}});
    const sim=await context.newPage(), page=await context.newPage();
    const errors=[]; page.on('pageerror',e=>errors.push(String(e)));
    await sim.goto(base); await sim.waitForFunction(()=>typeof signalR !== 'undefined');
    const room='T'+Date.now().toString(36), uid='sim_member', secret='simulation-password';
    const plain='[12:00:00.000] [INF] [Main:S1:P1:T1] 模拟大日志\n测试真实分块\n' + Array.from({length:20000},(_,i)=>`[12:00:00.000] [INF] [Main:S1:P1:T1] 第${i}行 ${'长文本'.repeat(40)}\n`).join('') + '[12:00:00.000] [ERR] [Main:S1:P1:T1] 大日志末尾关键词\n';
    const zipped=zlib.gzipSync(Buffer.from(plain));
    const jpeg=(await require('sharp')({create:{width:16,height:12,channels:3,background:'#394866'}}).jpeg().toBuffer()).toString('base64');
    const zip=new JSZip(); zip.file('log.txt', crypto.randomBytes(260000));
    const diagnosticBytes=await zip.generateAsync({type:'nodebuffer',compression:'STORE'});
    const diagnosticBase64=diagnosticBytes.toString('base64');
    const midpoint=Math.ceil(zipped.length/2);
    const chunks=[zipped.subarray(0,midpoint).toString('base64'),zipped.subarray(midpoint).toString('base64')];
    await sim.evaluate(async ({room,uid,secret,chunks,jpeg,diagnosticBase64})=>{
      const diagnosticBinary=atob(diagnosticBase64);
      const diagnostic=Uint8Array.from(diagnosticBinary,c=>c.charCodeAt(0));
      let counter=0;
      const conn=new signalR.HubConnectionBuilder().withUrl('/gateway').build();
      const env=(type,name,payload)=>({protocolVersion:3,type,name,payload,requestId:'sim'+(++counter),sentAtUtc:new Date().toISOString()});
      const send=(name,payload)=>conn.invoke('Dispatch',env('command',name,{roomCode:room,...payload}));
      window.sim={conn,send,mode:'enabled',events:[],diagnosticMode:'ok',diagnosticOwner:'',diagnosticEvents:[],operations:[],alertRecords:[{time:'2026-10-29T12:00:00',ruleId:'rule1',ruleName:'模拟警告规则',level:'WRN',message:'模拟异常记录正文',contextBefore:['前文'],contextAfter:['后文']}],rules:[{id:'rule1',name:'模拟警告规则',pattern:'异常',minLevel:'WRN',enabled:true,isRegex:false,alert:true,snapshot:false,note:''}]};
      conn.on('evt',async e=>{
        const x=e.payload||{}; window.sim.events.push(e.name);
        if(e.name==='log.subscribersChanged'&&x.count>0&&window.sim.mode==='enabled')
          await send('log.reportBatch',{uid,senderName:'模拟成员',lines:['[12:00:00.000] [INF] [Main:S1:P1:T1] 网关实时回传'],infoOnly:false});
        if(e.name==='log.filesRequested')
          await send('log.reportFiles',{uid,requestId:x.requestId,files:window.sim.mode==='enabled'?[{name:'sample.log',size:64,lastWrite:'2026-10-29T12:00:00'}]:[]});
        if(e.name==='log.downloadRequested')
          for(let seq=0;seq<chunks.length;seq++) await send('log.reportChunk',{uid,requestId:x.requestId,seq,totalChunks:chunks.length,chunkBase64:chunks[seq],fileName:x.fileName,done:seq===chunks.length-1});
        if(e.name==='screenshot.requested'&&window.sim.mode==='enabled')
          await send('screenshot.reportEx',{uid,requestId:x.requestId,jpegBase64:jpeg,width:16,height:12,capturedAt:new Date().toISOString()});
        if(e.name==='dodoco.requested'&&window.sim.mode!=='silent') {
          const date='2026-10-29'; let data={marker:'真实成员回执'}; window.sim.operations.push(x.operation);
          if(x.operation.startsWith('watch.diagnostic.')) window.sim.diagnosticEvents.push({operation:x.operation, requester:x.requesterConnectionId, offset:x.args?.offset});
          if(x.operation==='watch.diagnostic.start') {
            window.sim.diagnosticOwner=x.requesterConnectionId;
            data={id:'sim_diagnostic',size:window.sim.diagnosticMode==='oversize'?8*1024*1024+1:diagnostic.length,summary:'模拟诊断包'};
          }
          if(x.operation==='watch.diagnostic.chunk') {
            if(window.sim.diagnosticMode==='pause') return;
            if(x.requesterConnectionId!==window.sim.diagnosticOwner){await send('dodoco.respond',{requestId:x.requestId,status:'denied',message:'连接身份不匹配',errorCode:'owner_mismatch',data:null});return;}
            const offset=x.args.offset, block=diagnostic.slice(offset,offset+192*1024);
            let binary=''; for(const byte of block) binary+=String.fromCharCode(byte);
            data={id:'sim_diagnostic',offset:window.sim.diagnosticMode==='bad-offset'?offset+1:offset,total:diagnostic.length,base64:btoa(binary),done:offset+block.length===diagnostic.length};
          }
          if(x.operation==='watch.read') data={rules:window.sim.rules,dates:[date],settings:{muteAll:false,incidentSnapshotEnabled:false,stallThresholdMinutes:5,incidentBufferSeconds:15,incidentCaptureIntervalSeconds:1,incidentPreSeconds:3,incidentPostSeconds:3,incidentRetentionCount:10}};
          if(x.operation==='watch.records') data={records:window.sim.alertRecords,total:window.sim.alertRecords.length,offset:0,limit:50};
          if(x.operation==='watch.rule.upsert') { window.sim.rules.push(x.args.rule); data={rule:x.args.rule}; }
          if(x.operation==='report.dates') data={dates:[date]};
          if(x.operation==='report.day') data={date,report:{totalDuration:'01:00',groups:[{name:'模拟配置组',totalDuration:'01:00',runCount:1,rounds:['模拟联机轮次']}]},overview:{totalDuration:'01:00',topUnits:[{kind:'任务',name:'模拟任务总览',start:'12:00',duration:'01:00',children:[]}]}};
          await send('dodoco.respond',{requestId:x.requestId,status:window.sim.mode==='denied'?'denied':'ok',errorCode:'',message:window.sim.mode==='denied'?'共享未开启':'',data});
        }
      });
      await conn.start();
      await conn.invoke('Query',env('query','session.hello',{clientKind:'assistant',clientVersion:'sim',protocolVersion:3,capabilities:[]}));
      await send('control.joinRoom',{password:secret,playerUid:uid,playerName:'模拟成员',allowedUids:[],isRemote:false,clientInstanceId:'sim-instance'});
    },{room,uid,secret,chunks,jpeg,diagnosticBase64});
    await page.addInitScript(() => { const NativeWebSocket=window.WebSocket; window.testSockets=[]; window.WebSocket=class extends NativeWebSocket { constructor(...args) { super(...args); window.testSockets.push(this); } }; });
    await page.goto(base); await page.locator('#roomCode').fill(room); await page.locator('#password').fill(secret);
    await page.locator('#joinButton').click(); await page.locator('#dodocoProduct').waitFor({state:'visible'});
    await page.locator('#dodocoProduct').click();
    await page.locator('#dodocoMember option[value="sim_member"]').waitFor({state:'attached'});
    await page.locator('#dodocoMember').selectOption(uid);
    await page.getByText('网关实时回传',{exact:false}).waitFor({timeout:10000});
    await page.locator('[data-tab="files"]').click(); await page.locator('#dodocoList').click();
    await page.getByRole('button',{name:/sample\.log/}).click();
    await page.getByText('测试真实分块',{exact:false}).waitFor({timeout:10000});
    assert.match(await page.locator('#dodocoFileText').textContent(), /测试真实分块/);
    await page.locator('#dodocoSearch').fill('大日志末尾关键词');
    await page.getByText('大日志末尾关键词',{exact:false}).waitFor({timeout:10000});
    await page.locator('#dodocoFrom').fill('12:01');
    await page.waitForFunction(() => document.querySelector('#dodocoFileStatus').textContent.includes('匹配 0 行'));
    await page.locator('#dodocoFrom').fill('11:59');
    await page.getByText('大日志末尾关键词',{exact:false}).waitFor({timeout:10000});
    await page.locator('#dodocoSearch').fill('');await page.locator('#dodocoFrom').fill('');
    await page.locator('#dodocoLocal').setInputFiles({name:'browser-local.log',mimeType:'text/plain',buffer:Buffer.from('[12:00:00.000] [INF] [Main] 浏览器本地文件')});
    await page.getByText('浏览器本地文件',{exact:false}).waitFor({timeout:10000});
    await page.locator('[data-tab="screen"]').click(); await page.locator('#dodocoCapture').click();
    await page.locator('#dodocoFrames img').waitFor({state:'visible'});
    await page.waitForFunction(() => document.querySelector('#dodocoFrames img')?.naturalWidth === 16);
    assert.equal(await page.evaluate(()=>dodoco.frames.get('sim_member')[0].uid),uid);
    await page.locator('[data-tab="watch"]').click(); await page.locator('#watchRefresh').click();
    await page.getByText('模拟警告规则',{exact:false}).first().waitFor();
    await page.getByText('模拟异常记录正文',{exact:true}).waitFor();
    // Only the alert interval is shortened in the test page; production keeps its 15-second cadence.
    await page.evaluate(() => { const original=window.setTimeout; window.setTimeout=(fn,ms,...args)=>original(fn,ms===15000?120:ms,...args); });
    await page.locator('#watchAlertToggle').click();
    await page.locator('#watchAlertStatus').getByText(/仅提示建立基线后的新告警/).waitFor();
    await page.waitForTimeout(350);
    assert.doesNotMatch(await page.locator('#watchAlertStatus').textContent(),/新告警 [0-9]+ 条/);
    await sim.evaluate(()=>window.sim.alertRecords.unshift({time:'2026-10-29T12:01:00',ruleId:'rule1',ruleName:'模拟警告规则',level:'WRN',message:'新告警正文'}));
    await page.locator('#watchAlertStatus').getByText(/新告警 1 条.*新告警正文/).waitFor({timeout:5000});
    await page.locator('[data-tab="files"]').click();
    await page.waitForFunction(()=>watchAlerts.timer===null,null,{timeout:3000});
    const operationsAfterLeave=await sim.evaluate(()=>window.sim.operations.length);
    await page.waitForTimeout(350);
    assert.equal(await sim.evaluate(()=>window.sim.operations.length),operationsAfterLeave,'切页后不得继续轮询');
    await page.locator('[data-tab="watch"]').click();
    await sim.evaluate(()=>window.sim.mode='denied');
    await page.locator('#watchAlertToggle').click();
    await page.locator('#watchAlertStatus').getByText(/告警监视未能继续.*共享未开启/).waitFor({timeout:5000});
    assert.equal(await page.evaluate(()=>watchAlerts.timer),null);
    await sim.evaluate(()=>window.sim.mode='enabled');
    await page.locator('#watchAlertToggle').click();
    await page.locator('#watchAlertStatus').getByText(/仅提示建立基线后的新告警/).waitFor({timeout:5000});
    await page.evaluate(()=>window.testSockets.at(-1).close());
    await page.waitForFunction(()=>watchAlerts.timer===null,null,{timeout:5000});
    await page.waitForFunction(()=>roomReady,null,{timeout:15000});
    await page.locator('#watchAlertStatus').getByText(/告警监视.*停止/).waitFor();
    const expectedViewerConnection=await page.evaluate(()=>connection.connectionId);
    assert.ok(expectedViewerConnection);
    const downloadPromise=page.waitForEvent('download');
    await page.locator('#diagnosticDownload').click();
    const download=await downloadPromise;
    assert.equal(download.suggestedFilename(),'dodoco-diagnostic.zip');
    const downloadedChunks=[]; for await(const chunk of await download.createReadStream())downloadedChunks.push(chunk);
    assert.deepEqual(Buffer.concat(downloadedChunks),diagnosticBytes);
    const diagnosticEvents=await sim.evaluate(()=>window.sim.diagnosticEvents);
    assert.equal(diagnosticEvents[0].operation,'watch.diagnostic.start');
    assert.deepEqual(diagnosticEvents.filter(e=>e.operation==='watch.diagnostic.chunk').map(e=>e.offset),[0,192*1024]);
    assert.ok(diagnosticEvents.every(e=>e.requester===expectedViewerConnection));
    const secondViewer=await context.newPage();
    await secondViewer.goto(base); await secondViewer.locator('#roomCode').fill(room); await secondViewer.locator('#password').fill(secret);
    await secondViewer.locator('#joinButton').click(); await secondViewer.locator('#dodocoProduct').waitFor({state:'visible'});
    await secondViewer.locator('#dodocoProduct').click();
    await secondViewer.locator('#dodocoMember option[value="sim_member"]').waitFor({state:'attached'});
    await secondViewer.locator('#dodocoMember').selectOption(uid);
    const secondConnection=await secondViewer.evaluate(()=>connection.connectionId);
    assert.ok(secondConnection && secondConnection!==expectedViewerConnection);
    const ownerDenied=await secondViewer.evaluate(()=>dodocoRequest('watch.diagnostic.chunk',{id:'sim_diagnostic',offset:0},3000).then(()=>'',e=>e.message));
    assert.match(ownerDenied,/连接身份不匹配/);
    const blockedRequest=await sim.evaluate(()=>window.sim.diagnosticEvents.at(-1));
    assert.equal(blockedRequest.requester,secondConnection);
    await secondViewer.close();
    await sim.evaluate(()=>window.sim.diagnosticMode='bad-offset');
    await page.locator('#diagnosticDownload').click();
    await page.locator('#dodocoStatus').getByText(/分块无效/).waitFor();
    await sim.evaluate(()=>window.sim.diagnosticMode='oversize');
    await page.locator('#diagnosticDownload').click();
    await page.locator('#dodocoStatus').getByText(/诊断包信息无效/).waitFor();
    await sim.evaluate(()=>window.sim.diagnosticMode='ok');
    await page.locator('#watchRuleForm summary').click();
    await page.locator('#watchName').fill('模拟新增规则');
    await page.locator('#watchPattern').fill('测试');
    await page.locator('#watchSaveRule').click();
    await page.getByText('模拟新增规则',{exact:false}).first().waitFor();
    await sim.evaluate(()=>{window.sim.diagnosticMode='pause';window.sim.diagnosticEvents=[];});
    await page.locator('#diagnosticDownload').click();
    await sim.waitForFunction(()=>window.sim.diagnosticEvents.some(e=>e.operation==='watch.diagnostic.chunk'));
    await page.evaluate(()=>{ window.diagStatuses=[]; const original=dodocoStatus; dodocoStatus=text=>{window.diagStatuses.push(text);original(text);}; window.testSockets.at(-1).close(); });
    await page.waitForFunction(()=>window.diagStatuses.some(s=>s.includes('诊断包未完成')),null,{timeout:10000});
    await page.waitForFunction(()=>roomReady, null, {timeout:15000});
    await page.locator('#dodocoStatus').getByText(/先前请求因断线结果未确认/).waitFor({timeout:10000});
    console.log('DIAGNOSTIC_DISCONNECT_STATUSES='+JSON.stringify(await page.evaluate(()=>window.diagStatuses)));

    await sim.evaluate(()=>window.sim.diagnosticMode='ok');
    await sim.evaluate(()=>window.sim.mode='denied'); await page.locator('#watchRefresh').click();
    await page.locator('#dodocoStatus').getByText(/共享未开启/).waitFor();
    await sim.evaluate(()=>window.sim.mode='enabled');
    await page.locator('[data-tab="members"]').click(); await page.locator('#reportDates').click();
    await page.locator('#reportDate option[value="2026-10-29"]').waitFor({state:'attached'});
    await page.locator('#reportLoad').click(); await page.getByText('模拟配置组',{exact:false}).waitFor();
    await page.getByText('模拟任务总览',{exact:false}).waitFor();
    const result=await page.evaluate(()=>dodocoRequest('watch.read',{},3000));
    assert.equal(result.rules[0].name,'模拟警告规则');
    await sim.evaluate(()=>window.sim.mode='denied');
    const denied=await page.evaluate(()=>dodocoRequest('watch.read',{},3000).then(()=>'',e=>e.message));
    assert.match(denied,/共享未开启/);
    await sim.evaluate(()=>window.sim.mode='silent');
    const timeout=await page.evaluate(()=>dodocoRequest('watch.read',{},350).then(()=>'',e=>e.message));
    assert.match(timeout,/超时/);
    await sim.evaluate(()=>window.sim.mode='disabled');
    await page.locator('[data-tab="files"]').click(); await page.locator('#dodocoList').click();
    await page.locator('#dodocoStatus').getByText(/可能未开启文件共享/).waitFor();
    // Force the browser transport to fail and verify the built-in reconnect actually rejoins.
    await sim.evaluate(()=>window.sim.mode='silent');
    await page.locator('[data-tab="live"]').click();
    await page.evaluate(() => { window.testPending = dodocoRequest('watch.read',{},10000).then(()=>'', e=>e.message); });
    await page.waitForFunction(() => dodoco.pending.size > 0);
    await page.evaluate(() => {
      window.testReconnect=[];
      connection.onreconnecting(() => window.testReconnect.push('reconnecting'));
      connection.onreconnected(() => window.testReconnect.push('reconnected'));
      if (!window.testSockets.length) throw new Error('test websocket not found');
      window.testSockets.at(-1).close();
    });
    await page.waitForFunction(() => window.testReconnect.includes('reconnecting') && window.testReconnect.includes('reconnected') && roomReady, null, {timeout:15000});
    assert.match(await page.evaluate(()=>window.testPending),/断开|connection.*closed/i);
    await sim.evaluate(async()=>{window.sim.mode='enabled'; await window.sim.send('log.reportBatch',{uid:'sim_member',senderName:'模拟成员',lines:['[12:00:00.000] [INF] [Main:S1:P1:T1] 重连后实时日志'],infoOnly:false});});
    await page.waitForFunction(() => document.querySelectorAll('#dodocoLive .dodoco-line').length >= 2, null, {timeout:10000});
    await sim.evaluate(()=>window.sim.conn.stop());
    await page.locator('#dodocoMember option[value="sim_member"]').filter({hasText:'离线'}).waitFor({state:'attached',timeout:10000});
    assert.deepEqual(errors,[]);
    console.log('PASS isolated browser + gateway + simulated member: live, files/chunks, screenshot, alert monitoring, diagnostic ZIP, denied, timeout, disconnect/reconnect');
    console.log('DATA_ROOT='+isolated);
  } catch (e) {
    console.error('FAIL isolated browser gateway test:',e.stack || e);
    console.error('DATA_ROOT='+isolated); console.error('SERVER_LOG:',serverLog.slice(-12000)); process.exitCode=1;
  } finally {
    if(browser) await browser.close();
    server.kill();
  }
})().catch(e=>{console.error(e);process.exitCode=1;});
