import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
import {harness,pointer,flush} from './desktop-control-harness.mjs';

// 正式全屏视图在假DOM中执行；不调用真实输入、全屏或设备方向接口。
// 验证手机能操作放大的画面，同时滚动/旋转/旧帧不能变成误输入。
function point(overrides={}){return pointer({pointerId:1,pointerType:'touch',isPrimary:true,clientX:225,clientY:400,...overrides});}
function image(h){const img=h.element('dodocoRemoteImage');img.rect={left:0,top:0,width:450,height:800};return img;}
function tap(h,event=point()){const img=image(h);img.onpointerdown(event);img.onpointerup(event);}
async function mobile(options={}){const h=harness({mobile:true,pointerEvents:true,...options});await h.capture();assert.equal(h.enable(),true);return h;}

test('手机启用控制自动打开横屏页面全屏；系统API缺失仍可用，不续观看计时',async()=>{
 const h=await mobile(),visit=h.api.dodoco.screenVisit;
 assert.equal(h.api.dodocoRemote.open,true);assert.equal(h.element('dodocoRemoteDialog').open,true);
 assert.equal(h.api.dodocoRemote.rotated,true);assert.equal(h.element('dodocoRemoteShell').style.width,'812px');
 assert.equal(h.element('dodocoRemoteKeys').hidden,true);
 tap(h);assert.equal(h.inputs().length,0,'图片解码前不能操作');
 h.paint();tap(h);assert.equal(h.inputs().length,1);
 assert.equal(h.inputs()[0].payload.command.action,'left');
 assert.equal(h.inputs()[0].payload.command.x,.5);assert.equal(h.inputs()[0].payload.command.y,.5);
 assert.equal(h.api.dodoco.screenVisit,visit);
 h.element('dodocoRemoteExit').click();assert.equal(h.api.dodocoRemote.open,false);
 assert.equal(h.api.dodocoControl.enabled,false);assert.equal(h.cancels().length,1);
 assert.equal(h.document.body.classList.contains('dd-remote-open'),false);
});

test('手机鼠标方式不随截图、解码和输入冷却禁用，面板节点保持原位',async()=>{
 const h=await mobile();h.paint();
 const dialog=h.element('dodocoRemoteDialog'),mode=h.element('dodocoRemoteMode'),keys=h.element('dodocoRemoteKeys');
 h.element('dodocoRemoteKeyboard').click();h.element('dodocoRemoteTools').open=true;
 mode.value='double';mode.onchange({target:mode});
 const refresh=h.api.dodocoCapture();await flush();
 assert.equal(h.api.dodoco.autoBusy,true);assert.equal(mode.disabled,false,'请求截图不应收起手机原生选择菜单');
 mode.value='right';mode.onchange({target:mode});h.reply();await refresh;
 assert.equal(h.api.dodocoRemoteReady(),false,'新图解码期间仍禁止对画面发输入');
 assert.equal(mode.disabled,false);assert.equal(mode.value,'right');
 assert.equal(h.element('dodocoRemoteDialog'),dialog);assert.equal(h.element('dodocoRemoteKeys'),keys);
 assert.equal(keys.hidden,false);assert.equal(h.element('dodocoRemoteTools').open,true);
 h.paint();keys.querySelector('[data-remote-key="W"]').click();assert.equal(h.inputs().length,1);
 assert.equal(mode.disabled,false,'输入回执和两秒冷却不影响选择下一次鼠标方式');
 mode.value='double';mode.onchange({target:mode});assert.equal(h.api.dodocoControl.mode,'double');
});

test('全屏交互期间推迟定时截图，空闲时恢复；输入后仍自动获取新画面',async()=>{
 const h=harness({mobile:true,pointerEvents:true});
 const auto=h.element('dodocoAuto');h.element('dodocoInterval').value='1000';auto.checked=true;auto.onchange({target:auto});
 await flush();h.reply();await flush();assert.equal(h.api.dodoco.autoBusy,false);
 h.enable();h.paint();const count=h.captures().length,img=image(h),visit=h.api.dodoco.screenVisit;
 h.advance(900);img.onpointerdown(point());await h.tick(300);assert.equal(h.captures().length,count);img.onpointerup(point());
 assert.equal(h.inputs().length,1);h.result();await h.tick(1100);
 assert.equal(h.captures().length,count+1,'系统接受输入后仍会自动获取新画面');
 h.reply();await flush();h.paint();h.element('dodocoRemoteKeyboard').click();await h.tick(3000);
 assert.equal(h.captures().length,count+1,'按键抽屉展开时不穿插后台定时截图');
 assert.equal(h.api.dodoco.screenVisit,visit);assert.equal(auto.checked,true);
 h.element('dodocoRemoteKeyboard').click();await h.tick(1100);
 assert.equal(h.captures().length,count+2,'全屏空闲时继续自动刷新');h.reply();await flush();h.paint();
 h.element('dodocoRemoteTools').open=true;await h.tick(2200);
 assert.equal(h.captures().length,count+2,'工具菜单展开时暂缓定时刷新');
 h.element('dodocoRemoteTools').open=false;h.element('dodocoRemoteExit').click();await h.tick(1100);
 assert.equal(h.captures().length,count+3,'退出全屏后保持原定时刷新设置');h.reply();await flush();
});

test('桌面保留原内联控制，显式全屏入口才进入大画面',async()=>{
 const h=harness();await h.capture();h.enable();assert.equal(h.api.dodocoRemote.open,false);
 h.element('dodocoRemoteEnter').click();assert.equal(h.api.dodocoRemote.open,true);
 assert.equal(h.api.dodocoRemote.rotated,false);assert.equal(h.element('dodocoZoomDialog').open,false);
});

for(const rejected of [false,true])test(`系统全屏${rejected?'拒绝':'成功'}及横屏锁定拒绝均保持可用回退`,async()=>{
 const h=harness({mobile:true,pointerEvents:true});await h.capture();let exits=0,locks=0;
 const root=h.element('dodocoRemoteRoot');
 h.document.exitFullscreen=async()=>{exits++;h.document.fullscreenElement=null;h.emitDocument('fullscreenchange');};
 root.requestFullscreen=async()=>{if(rejected)throw Error('not allowed');h.document.fullscreenElement=root;h.emitDocument('fullscreenchange');};
 h.context.screen={orientation:{lock:async()=>{locks++;throw Error('unsupported');},unlock(){assert.fail('未取得方向锁不能释放别人的锁');}}};
 h.enable();await flush();
 assert.equal(h.api.dodocoRemote.open,true);assert.equal(h.api.dodocoRemote.rotated,true);
 assert.equal(locks,rejected?0:1);
 h.element('dodocoRemoteExit').click();await flush();assert.equal(exits,rejected?0:1);
});

test('浏览器退出系统全屏关闭控制并取消在途输入，释放本页方向锁',async()=>{
 const h=harness({mobile:true,pointerEvents:true});await h.capture();let unlocks=0;
 const root=h.element('dodocoRemoteRoot');root.requestFullscreen=async()=>{h.document.fullscreenElement=root;h.emitDocument('fullscreenchange');};
 h.context.screen={orientation:{lock:async()=>{},unlock:()=>unlocks++}};
 h.enable();await flush();h.paint();tap(h);
 h.document.fullscreenElement=null;h.emitDocument('fullscreenchange');
 assert.equal(h.api.dodocoRemote.open,false);assert.equal(h.api.dodocoControl.enabled,false);
 assert.equal(h.cancels().length,1);assert.equal(unlocks,1);
});

test('关闭后迟到的全屏promise不重新打开视图；同元素新视图也不能被它退出',async()=>{
 const h=harness({mobile:true,pointerEvents:true});await h.capture();const root=h.element('dodocoRemoteRoot');let resolve,exits=0;
 root.requestFullscreen=()=>new Promise(r=>resolve=r);
 h.document.exitFullscreen=async()=>{exits++;h.document.fullscreenElement=null;h.emitDocument('fullscreenchange');};
 h.enable();h.element('dodocoRemoteExit').click();h.document.fullscreenElement=root;resolve();await flush();
 assert.equal(h.api.dodocoRemote.open,false);assert.equal(exits,1);
 // 再测试旧请求晚于同一DOM元素的新视图打开。
 await h.capture();root.requestFullscreen=()=>new Promise(r=>resolve=r);h.enable();h.element('dodocoRemoteExit').click();
 root.requestFullscreen=undefined;await h.capture();h.enable();const epoch=h.api.dodocoRemote.epoch;
 h.document.fullscreenElement=root;resolve();await flush();
 assert.equal(h.api.dodocoRemote.open,true);assert.equal(h.api.dodocoRemote.epoch,epoch);assert.equal(exits,1);
});

test('原只读放大窗口仍只读，全屏远控与之分开',async()=>{
 const h=harness();await h.capture();h.image().click();
 assert.equal(h.element('dodocoZoomDialog').open,true);assert.equal(h.api.dodocoRemote.open,false);
 assert.equal(h.inputs().length,0);assert.match(h.api.dodocoControlReason(),/放大窗口/);
});

for(const rotated of [false,true])test(`全屏${rotated?'旋转90度':'普通'}坐标含缩放和平移，拒绝留白且不乘DPI`,async()=>{
 const h=harness();const img=h.element('dodocoRemoteImage');
 img.naturalWidth=1600;img.naturalHeight=900;
 // 未旋转盒为800x600，内容800x450，上下各留75。
 img.rect=rotated?{left:10,top:20,width:600,height:800}:{left:-310,top:-180,width:800,height:600};
 const e=rotated?point({clientX:220,clientY:180}):point({clientX:-150,clientY:210});
 const result=h.api.dodocoRemotePoint(img,e,rotated);
 assert.ok(Math.abs(result.x-.2)<1e-10);assert.ok(Math.abs(result.y-.7)<1e-10);
 const blank=rotated?point({clientX:605,clientY:100}):point({clientX:-150,clientY:-175});
 assert.equal(h.api.dodocoRemotePoint(img,blank,rotated),null);
 h.context.window.devicePixelRatio=3;
 assert.deepEqual(h.api.dodocoRemotePoint(img,e,rotated),result);
 assert.equal(h.api.dodocoRemotePoint(img,point({clientX:NaN}),rotated),null);
});

test('拖动、长按、双指、取消与兼容click都不会额外输入',async()=>{
 const h=await mobile();h.paint();const img=image(h);
 img.onpointerdown(point());img.onpointermove(point({clientX:245}));img.onpointerup(point({clientX:245}));
 img.onclick(point());assert.equal(h.inputs().length,0);
 img.onpointerdown(point());h.advance(801);img.onpointerup(point());assert.equal(h.inputs().length,0);
 img.onpointerdown(point());img.onpointerdown(point({pointerId:2,isPrimary:false}));
 img.onpointerup(point());img.onpointerup(point({pointerId:2,isPrimary:false}));assert.equal(h.inputs().length,0);
 img.onpointerdown(point());img.onpointercancel(point());img.onpointerup(point());assert.equal(h.inputs().length,0);
 img.onpointerdown(point());h.element('dodocoRemoteViewport').onscroll();img.onpointerup(point());assert.equal(h.inputs().length,0);
 tap(h);img.onclick(point());assert.equal(h.inputs().length,1);
});

test('缺少PointerEvent时触摸回退也区分拖动和点按，不依赖click',async()=>{
 const h=await mobile({pointerEvents:false});h.paint();const img=image(h);
 const touch=(x=225,y=400)=>({identifier:7,clientX:x,clientY:y});
 const event=(t,active=true)=>({changedTouches:[t],touches:active?[t]:[],preventDefault(){}});
 img.ontouchstart(event(touch()));img.ontouchmove(event(touch(250)));img.ontouchend(event(touch(250),false));
 img.onclick(point());assert.equal(h.inputs().length,0);
 img.ontouchstart(event(touch()));img.ontouchend(event(touch(),false));img.onclick(point());assert.equal(h.inputs().length,1);
});

for(const rejected of ['长按','收起菜单','拖动'])test(`旧触摸浏览器${rejected}后完整兼容鼠标链仍不输入，真正鼠标可恢复`,async()=>{
 const h=await mobile({pointerEvents:false});h.paint();const img=image(h);
 const t={identifier:9,clientX:225,clientY:400};
 const event=(touch,active=true)=>({changedTouches:[touch],touches:active?[touch]:[],preventDefault(){}});
 if(rejected==='收起菜单')h.element('dodocoRemoteTools').open=true;
 img.ontouchstart(event(t));
 if(rejected==='长按')h.advance(801);
 const end=rejected==='拖动'?{...t,clientX:260}:t;
 if(rejected==='拖动')img.ontouchmove(event(end));
 img.ontouchend(event(end,false));
 img.onmousedown(point());img.onmousemove(point());img.onmouseup(point());img.onclick(point());
 assert.equal(h.inputs().length,0);
 h.advance(1001);
 img.onmousedown(point());img.onmouseup(point());assert.equal(h.inputs().length,1);
});

test('全屏BGI入口拒绝原因显示在全屏状态并跨周期保留',async()=>{
 const h=await mobile();h.paint();
 h.element('dodocoRemoteBgi').click();assert.match(h.element('dodocoRemoteStatus').textContent,/尚未上报/);
 await h.tick(2000);assert.match(h.element('dodocoRemoteStatus').textContent,/尚未上报/);
 h.context.players[0].hotkeys=[{configName:'SuspendHotkey'}];
 h.element('dodocoRemoteBgi').click();assert.match(h.element('dodocoRemoteStatus').textContent,/尚未加载/);
 h.context.roomReady=false;h.element('dodocoRemoteBgi').click();assert.match(h.element('dodocoRemoteStatus').textContent,/房间内/);
 assert.equal(h.inputs().length,0);
});

test('旋转和新画面在手势期间发生时取消点按；旧load回调不能解锁新图',async()=>{
 const h=await mobile();h.paint();const img=image(h),oldLoad=img.onload;
 img.onpointerdown(point());h.context.window.innerWidth=812;h.context.window.innerHeight=375;h.emitWindow('resize');
 img.onpointerup(point());assert.equal(h.inputs().length,0);assert.equal(h.api.dodocoRemote.rotated,false);
 await h.capture();oldLoad();assert.equal(h.api.dodocoRemoteReady(),false);
 img.onerror();assert.match(h.element('dodocoRemoteStatus').textContent,/加载失败/);assert.equal(h.inputs().length,0);
 h.paint();assert.equal(h.api.dodocoRemoteReady(),true);
 img.onpointerdown(point());await h.capture();h.paint();img.onpointerup(point());assert.equal(h.inputs().length,0);
});

test('键盘抽屉和Win+D复用原输入门控，成功刷新后保持全屏及缩放',async()=>{
 const h=await mobile();h.paint();const dialog=h.element('dodocoRemoteDialog'),visit=h.api.dodoco.screenVisit;
 h.element('dodocoRemoteZoomIn').click();assert.equal(h.api.dodocoRemote.zoom,1.5);
 assert.equal(h.element('dodocoRemoteViewport').scrollLeft,75);
 assert.equal(h.element('dodocoRemoteViewport').scrollTop,100);
 h.element('dodocoRemoteKeyboard').click();assert.equal(h.element('dodocoRemoteKeys').hidden,false);
 const key=h.element('dodocoRemoteKeys').querySelector('[data-remote-key="Win+D"]');
 key.click(pointer({detail:0}));assert.equal(h.inputs().length,0);
 key.click();assert.equal(h.inputs().length,1);assert.equal(h.inputs()[0].payload.command.key,'Win+D');
 assert.equal(key.disabled,true);h.result();await h.tick(1100);
 assert.equal(h.captures().length,2);h.reply();await flush();
 assert.equal(h.api.dodocoRemoteReady(),false,'新图解码前仍不可用');h.paint();await h.tick(900);
 assert.equal(h.api.dodocoRemote.open,true);assert.equal(h.element('dodocoRemoteDialog'),dialog);
 assert.equal(h.api.dodocoRemote.zoom,1.5);assert.equal(h.api.dodoco.screenVisit,visit);
 assert.equal(key.disabled,false);h.element('dodocoRemoteFit').click();assert.equal(h.api.dodocoRemote.zoom,1);
});

for(const action of ['成员','历史','断线','到期','离开'])test(`${action}变化退出全屏并取消旧输入，迟到回执不能重新开启`,async()=>{
 const h=await mobile();h.paint();tap(h);const pending=h.api.dodocoControl.pending;
 if(action==='成员')h.member('bob');
 if(action==='历史')h.element('dodocoFrames').querySelector('.dodoco-frame-select').onchange({target:{value:'0'}});
 if(action==='断线')h.api.dodocoConnection(false);
 if(action==='到期'){h.advance(180000);h.api.dodocoScreenAllowed();}
 if(action==='离开')h.tab('members');
 assert.equal(h.api.dodocoRemote.open,false);assert.equal(h.api.dodocoControl.enabled,false);assert.equal(h.cancels().length,1);
 h.api.dodocoEvent({name:'desktop.control.result',payload:{requestId:pending.id,targetUid:'alice',status:'executed'}});
 assert.equal(h.api.dodocoRemote.open,false);assert.equal(h.inputs().length,1);
});

test('工具操作不续三分钟；系统方向变化和缩放仍受观看截止约束',async()=>{
 const h=await mobile(),visit=h.api.dodoco.screenVisit;
 h.advance(179999);h.element('dodocoRemoteZoomIn').click();h.context.window.innerWidth=812;h.context.window.innerHeight=375;
 h.emitWindow('resize');assert.equal(h.api.dodoco.screenVisit,visit);
 h.advance(1);h.api.dodocoScreenAllowed();assert.equal(h.api.dodocoRemote.open,false);assert.equal(visit.expired,true);
});

test('BGI快捷命令失败反馈留在当前全屏，旧视图迟到失败不污染新视图',async()=>{
 const h=await mobile();h.context.players[0].hotkeys=[{configName:'SuspendHotkey',functionName:'暂停脚本',hotkeyText:'D9'}];
 const old=readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/control-room.js',import.meta.url),'utf8');
 const begin=old.indexOf('function showHotkeySelect('),end=old.indexOf('// ---------- 弹窗：任务列表选择',begin);
 h.context.log=()=>{};h.context.makeCmd=(cmd,params)=>({cmd,params});
 h.context.sendTrackedCommand=async()=>{throw Error('执行端拒绝');};vm.runInContext(old.slice(begin,end),h.context);
 h.element('dodocoRemoteBgi').click();let ok=h.element('dodocoRemoteShell').querySelector('#hkOk');
 await h.listeners.find(l=>l.target===ok&&l.type==='click').callback();
 assert.match(h.element('dodocoRemoteStatus').textContent,/执行端拒绝/);
 let reject;h.context.sendTrackedCommand=()=>new Promise((resolve,r)=>reject=r);
 h.element('dodocoRemoteBgi').click();ok=h.element('dodocoRemoteShell').querySelector('#hkOk');
 const pending=h.listeners.find(l=>l.target===ok&&l.type==='click').callback();
 h.element('dodocoRemoteExit').click();h.element('dodocoRemoteEnter').click();
 reject(Error('旧视图失败'));await pending;
 assert.equal(h.api.dodocoRemote.open,true);assert.doesNotMatch(h.element('dodocoRemoteStatus').textContent,/旧视图失败/);
 assert.equal(h.inputs().length,0);
});

test('全屏内BGI快捷选择器仍复用原函数，弹窗挂入可交互的全屏容器',async()=>{
 const h=await mobile();h.context.players[0].hotkeys=[{configName:'SuspendHotkey',functionName:'暂停脚本',hotkeyText:'D9'}];
 const old=readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/control-room.js',import.meta.url),'utf8');
 const begin=old.indexOf('function showHotkeySelect('),end=old.indexOf('// ---------- 弹窗：任务列表选择',begin);
 h.context.log=()=>{};h.context.makeCmd=(cmd,params)=>({cmd,params});const commands=[];
 h.context.sendTrackedCommand=async command=>commands.push(command);
 vm.runInContext(old.slice(begin,end),h.context);
 h.element('dodocoRemoteBgi').click();const shell=h.element('dodocoRemoteShell'),overlay=shell.querySelector('.modal-overlay');
 assert.ok(overlay);const ok=overlay.querySelector('#hkOk');
 await h.listeners.find(l=>l.target===ok&&l.type==='click').callback();
 assert.equal(commands.length,1);assert.equal(commands[0].cmd,'hotkey_execute');assert.deepEqual([...commands[0].target],['alice']);
 assert.equal(commands[0].params.hotkeyConfigName,'SuspendHotkey');assert.equal(shell.querySelector('.modal-overlay'),null);
 assert.equal(h.api.dodocoRemote.open,true);assert.equal(h.inputs().length,0);
});
