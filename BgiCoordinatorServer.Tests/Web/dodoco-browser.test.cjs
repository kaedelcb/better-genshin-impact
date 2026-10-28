// Edge + mock gateway: tests browser UI and rendering, not a real member.
const assert=require('node:assert/strict'), zlib=require('node:zlib');
const {fixture}=require('../../Test/IpcChannelContractAudit/web-gengdi-fixture.cjs');
(async()=>{const f=await fixture(),p=f.page,images={};try{
 await f.login();await p.locator('#dodocoProduct').click();await p.locator('#dodocoMember').selectOption('a');
 await p.waitForFunction(()=>gatewayCalls.some(x=>x.name==='log.subscribe'));
 await p.evaluate(()=>emit('log.batch',{uid:'a',senderName:'成员甲',infoOnly:false,lines:['[10:00:00.000] [ERR] [Main:S1] 来源 | '+ '很长的日志内容'.repeat(90)]}));
 await p.waitForFunction(()=>document.querySelector('#dodocoLive')?.textContent.includes('很长的日志内容'));
 await p.locator('#dodocoMember').selectOption('b');
 await p.waitForFunction(()=>gatewayCalls.some(x=>x.name==='log.subscribe' && x.payload.targetUid==='b'));
 await p.evaluate(()=>emit('log.batch',{uid:'b',senderName:'成员乙',lines:['[10:00:00.000] [INF] [Other:S1] 来源 | 乙的日志']}));
 await p.waitForFunction(()=>document.querySelector('#dodocoLive')?.textContent.includes('乙的日志'));
 await p.locator('#dodocoClear').click();await p.locator('#dodocoMember').selectOption('a');
 await p.waitForFunction(()=>document.querySelector('#dodocoLive')?.textContent.includes('很长的日志内容'));
 for(const width of [375,1440]){await p.setViewportSize({width,height:1000});await p.waitForTimeout(100);assert.equal(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,'live horizontal overflow');images[`live-${width}.png`]=(await p.screenshot({fullPage:true})).toString('base64');}
 await p.locator('[data-tab="files"]').click();
 await p.locator('#dodocoLocal').setInputFiles({name:'sample.log',mimeType:'text/plain',buffer:Buffer.from('[09:59:59] [INF] before\n[10:00:00] [WRN] within\ncontinuation\n[11:01:00] [ERR] after')});
 await p.waitForFunction(()=>document.querySelector('#dodocoFileText')?.textContent.includes('continuation'));
 await p.locator('#dodocoFrom').fill('10:00');await p.locator('#dodocoTo').fill('11:00');
 await p.waitForFunction(()=>document.querySelector('#dodocoFileStatus')?.textContent.includes('匹配 2 行'));
 await p.locator('#dodocoList').click();
 const list=await p.evaluate(()=>gatewayCalls.filter(x=>x.name==='log.requestFiles').at(-1).payload);
 await p.evaluate(x=>emit('log.fileList',{uid:'a',requestId:x.requestId,files:[{name:'remote.log',size:120,lastWrite:'2026-10-29'}]}),list);
 await p.locator('.dodoco-file').click();
 const req=await p.evaluate(()=>gatewayCalls.filter(x=>x.name==='log.requestDownload').at(-1).payload);
 const compressed=zlib.gzipSync(Buffer.from('[10:00:00] [ERR] remote line')).toString('base64');
 await p.evaluate(({req,compressed})=>emit('log.fileChunk',{uid:'a',requestId:req.requestId,fileName:req.fileName,seq:0,totalChunks:1,chunkBase64:compressed,done:true}),{req,compressed});
 await p.waitForFunction(()=>document.querySelector('#dodocoFileText')?.textContent.includes('remote line'));
 await p.locator('#dodocoLocal').setInputFiles({name:'regex.log',mimeType:'text/plain',buffer:Buffer.from('[10:00:00] [INF] '+ 'a'.repeat(15000)+'!')});
 await p.waitForFunction(()=>document.querySelector('#dodocoFileStatus')?.textContent.includes('regex.log'));
 await p.locator('#dodocoRegex').check();await p.locator('#dodocoSearch').fill('[');
 await p.waitForFunction(()=>document.querySelector('#dodocoStatus')?.textContent.includes('正则表达式无效'));
 await p.locator('#dodocoSearch').fill('(a+)+$');
 await p.waitForFunction(()=>document.querySelector('#dodocoStatus')?.textContent.includes('正则匹配超时，已停止'),null,{timeout:15000});
 await p.locator('#dodocoRegex').uncheck();await p.locator('#dodocoSearch').fill('不存在的内容');
 await p.waitForFunction(()=>document.querySelector('#dodocoFileStatus')?.textContent.includes('匹配 0 行'));
 await p.locator('#dodocoSearch').fill('');await p.locator('#dodocoLocal').setInputFiles({name:'sample.log',mimeType:'text/plain',buffer:Buffer.from('[10:00:00] [INF] 示例日志\n[10:01:00] [WRN] 带关键词的长日志')});
 await p.waitForFunction(()=>document.querySelector('#dodocoFileText')?.textContent.includes('带关键词'));
 await p.setViewportSize({width:375,height:1000});assert.equal(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,'file horizontal overflow');images['files-375.png']=(await p.screenshot({fullPage:true})).toString('base64');
 // 异常监控只作用于本机助手：服务器中转页面没有本机连接时，不显示成员选择、不向其他成员发请求。
 const watchCallsBefore=await p.evaluate(()=>gatewayCalls.filter(x=>x.name==='dodoco.request'&&String(x.payload.operation).startsWith('watch.')).length);
 await p.locator('[data-tab="watch"]').click();
 assert.equal(await p.locator('#dodocoMember').count(),0,'异常监控不应出现成员选择');
 assert.match(await p.locator('.dd-own-target').textContent(),/未连接本机助手/);
 assert.match(await p.locator('#dodocoContent').textContent(),/只作用于本机助手/);
 assert.equal(await p.evaluate(()=>gatewayCalls.filter(x=>x.name==='dodoco.request'&&String(x.payload.operation).startsWith('watch.')).length),watchCallsBefore,'异常监控不得向其他成员发请求');
 for(const width of [375,1440]){await p.setViewportSize({width,height:1000});await p.waitForTimeout(100);assert.equal(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,'watch horizontal overflow');images[`watch-${width}.png`]=(await p.screenshot({fullPage:true})).toString('base64');}
 await p.locator('[data-tab="members"]').click();await p.locator('#dodocoMember').selectOption('a');
 await p.locator('[data-tab="members"]').click();
 assert.match(await p.locator('#dodocoContent').textContent(),/BGI：运行中/);
 assert.match(await p.locator('#dodocoContent').textContent(),/状态待确认/);
 await p.locator('#reportDates').click();
 const dates=await p.evaluate(()=>gatewayCalls.filter(x=>x.name==='dodoco.request').at(-1).payload);
 await p.evaluate(x=>emit('dodoco.result',{requestId:x.requestId,status:'ok',data:{dates:['2026-10-29']}}),dates);
 await p.waitForFunction(()=>document.querySelector('#reportDate')?.value==='2026-10-29');await p.locator('#reportLoad').click();
 const report=await p.evaluate(()=>gatewayCalls.filter(x=>x.name==='dodoco.request').at(-1).payload);
 await p.evaluate(x=>emit('dodoco.result',{requestId:x.requestId,status:'ok',data:{date:'2026-10-29',report:{totalDuration:'02:15',groups:[{name:'很长的任务配置名称'.repeat(10),totalDuration:'02:15',runCount:2,rounds:['第一轮']} ]},overview:{totalDuration:'02:15',topUnits:[{kind:'配置组',name:'任务总览',start:'10:00',duration:'02:15',children:[]}]}}}),report);
 await p.waitForFunction(()=>document.querySelector('#reportBody')?.textContent.includes('任务总览'));
 for(const width of [375,1440]){await p.setViewportSize({width,height:1000});assert.equal(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,'report horizontal overflow');images[`report-${width}.png`]=(await p.screenshot({fullPage:true})).toString('base64');}
 await p.locator('[data-tab="screen"]').click();await p.locator('#dodocoMember').selectOption('*');await p.locator('#dodocoCapture').click();
 await p.waitForFunction(()=>gatewayCalls.filter(x=>x.name==='screenshot.request').length===2);
 await p.evaluate(()=>{const canvas=document.createElement('canvas');canvas.width=640;canvas.height=360;const ctx=canvas.getContext('2d');ctx.fillStyle='#343652';ctx.fillRect(0,0,640,360);ctx.fillStyle='#f7df96';ctx.font='24px sans-serif';ctx.fillText('Simulated member screen',40,80);const jpegBase64=canvas.toDataURL('image/jpeg').split(',')[1];for(const c of gatewayCalls.filter(x=>x.name==='screenshot.request'))emit('screenshot.member',{uid:c.payload.targetUid,requestId:c.payload.requestId,jpegBase64,width:640,height:360,capturedAt:'2026-10-29 12:00'});});
 await p.waitForFunction(()=>document.querySelectorAll('.dodoco-frame-card img').length===2);
 for(const width of [375,1440]){await p.setViewportSize({width,height:1000});assert.equal(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,'screen horizontal overflow');if(width===375)assert.ok(await p.locator('.dodoco-frame-select').first().evaluate(x=>x.clientWidth)>220,'mobile history timestamp must have enough space');images[`screen-${width}.png`]=(await p.screenshot({fullPage:true})).toString('base64');}
 await p.locator('#dodocoMember').selectOption('off');await p.locator('#dodocoCapture').click();assert.match(await p.locator('#dodocoStatus').textContent(),/在线成员|离线/);
 await p.setViewportSize({width:375,height:1000});images['offline-375.png']=(await p.screenshot({fullPage:true})).toString('base64');
 await p.locator('#gengdiProduct').click();assert.equal(await p.locator('#homeView').isVisible(),true);
 assert.deepEqual(f.errors,[]);
 const outputImages=process.env.DODOCO_NO_IMAGES?{}:process.env.DODOCO_IMAGE_ONLY?{[process.env.DODOCO_IMAGE_ONLY]:images[process.env.DODOCO_IMAGE_ONLY]}:images;
 console.log(JSON.stringify({ok:true,images:outputImages}));
 }finally{await f.close();}})().catch(e=>{console.error(e);process.exitCode=1});
