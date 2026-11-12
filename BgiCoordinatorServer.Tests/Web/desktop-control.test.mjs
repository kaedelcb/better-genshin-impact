import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { webcrypto } from 'node:crypto';
import vm from 'node:vm';

// 正式脚本与事件绑定在隔离 VM 中执行；输入、桥、网关和时钟全是假实现。
// 这些断言决定网页能否开放控制：ACK 不能当成功，旧请求不能解锁新来源，
// 新帧/两秒/三分钟门控与取消都需成立。既有 protocol/timeout 测试未覆盖输入。
import { source, localSource, contracts, geometry, flush, plain, pointer, dom, harness } from './desktop-control-harness.mjs';

test('默认关闭；须在线单成员、本次最新授权帧及有效 geometry 才能开启',async()=>{
    const h=harness();assert.equal(h.api.dodocoControl.enabled,false);assert.equal(h.enable(),false);
    await h.capture({controlAllowed:false});assert.equal(h.enable(),false);assert.match(h.element('dodocoControlStatus').textContent,/对方助手本机/);
    await h.capture({geometry:{...geometry,width:0}});assert.equal(h.enable(),false);
    await h.capture();assert.equal(h.enable(),true);assert.equal(h.inputs().length,0);
    h.member('*');assert.equal(h.enable(),false);assert.equal(h.api.dodocoControl.enabled,false);
    h.member('alice');assert.equal(h.enable(),false,'切换成员后旧来源帧须重新获取');
    await h.capture();h.context.players[0].online=false;h.api.dodocoMembers();assert.equal(h.enable(),false);
    assert.equal(h.requests.some(r=>r.name==='dodoco.request' || /settings/.test(r.name)),false,'网页不提供远程授权写接口');
});

test('HTTP 无 randomUUID 也能运行；正式 Win+D 按钮仅接受指针点击且与 DTO 白名单一致',async()=>{
    const h=harness();assert.equal(h.context.crypto.randomUUID,undefined);
    const dtoKeys=JSON.parse(contracts.match(/Keys = (\[[^;]+\]);/)[1]);assert.deepEqual(plain(h.api.dodocoControlKeys),dtoKeys);
    await h.capture();h.enable();
    const key=h.element('dodocoControlKeys').querySelector('[data-control-key="Win+D"]');
    assert.equal(key.disabled,false);assert.match(key.textContent,/显示桌面/);
    h.key('Win+D',pointer({detail:0}));assert.equal(h.inputs().length,0,'键盘触发的 button click 不发送远程输入');
    h.key();await flush();const command=h.inputs()[0].payload.command;
    assert.equal(command.key,'Win+D');assert.equal(command.action,'key');assert.equal(command.targetUid,'alice');
    assert.equal(command.frameId,h.captures()[0].payload.requestId);assert.match(command.requestId,/^[a-f0-9]{32}$/);
    assert.ok(h.api.dodocoControl.pending,'转发 ACK 不结束等待');
    assert.equal([...h.listeners,...h.windowListeners].some(l=>/^key/.test(l.type)),false);
    assert.equal(h.element('dodocoControlMode').disabled,true);assert.equal(key.disabled,true);
});

for(const mode of ['left','right','double'])test(`主画面按选定方式 ${mode} 发送一次；浏览器右键、键盘 click 和双次点击不额外输入`,async()=>{
    const h=harness();await h.capture();h.enable();
    const select=h.element('dodocoControlMode');select.value=mode;select.onchange({target:select});
    const image=h.image(),event=pointer();image.oncontextmenu(event);assert.equal(event.defaultPrevented,true);assert.equal(h.inputs().length,0);
    image.click(pointer({detail:0}));assert.equal(h.inputs().length,0);
    image.click();image.click(pointer({detail:2}));assert.equal(h.inputs().length,1);
    const command=h.inputs()[0].payload.command;assert.equal(command.action,mode);assert.equal(command.x,0.5);assert.equal(command.y,0.5);
});

test('contain 内容区归一化不乘 DPI；上下/左右留白、无效点与未加载图片拒绝',async()=>{
    const h=harness();await h.capture();h.enable();const image=h.element('dodocoFrames').querySelector('.dodoco-image');
    assert.deepEqual(plain(h.api.dodocoControlPoint(image,pointer())),{x:0.5,y:0.5});
    assert.equal(h.api.dodocoControlPoint(image,pointer({clientY:30})),null);
    h.image().click(pointer({clientY:30}));assert.equal(h.inputs().length,0);
    image.rect={left:30,top:40,width:600,height:200};image.naturalWidth=1000;image.naturalHeight=1000;
    assert.deepEqual(plain(h.api.dodocoControlPoint(image,pointer({clientX:330,clientY:140}))),{x:0.5,y:0.5});
    assert.equal(h.api.dodocoControlPoint(image,pointer({clientX:40,clientY:140})),null);
    image.rect={left:10,top:20,width:800,height:450};image.naturalWidth=1600;image.naturalHeight=900;
    h.context.window.devicePixelRatio=2;
    assert.deepEqual(plain(h.api.dodocoControlPoint(image,pointer({clientX:210,clientY:357.5}))),{x:0.25,y:0.75});
    assert.equal(h.api.dodocoControlPoint(image,pointer({clientX:NaN})),null);
    image.naturalWidth=0;assert.equal(h.api.dodocoControlPoint(image,pointer()),null);
});

test('ACK 期间手动和自动截图暂停；不能并发发送第二次输入',async()=>{
    const h=harness();await h.capture();
    const auto=h.element('dodocoAuto');auto.checked=true;auto.onchange({target:auto});await flush();h.reply();await flush();h.enable();h.key();
    const count=h.captures().length,pending=h.api.dodocoControl.pending;
    h.timers.get(h.api.dodoco.autoTimer).callback();await h.api.dodocoCapture();await flush();
    await assert.rejects(h.api.dodocoCaptureOne('alice'),/等待输入结果/);h.key();h.api.dodocoControlSend('left',0.5,0.5);
    h.api.dodocoEvent({name:'desktop.control.result',payload:{requestId:pending.id,targetUid:'alice',status:'accepted'}});
    assert.equal(h.api.dodocoControl.pending,pending);assert.equal(h.captures().length,count);assert.equal(h.inputs().length,1);
});

test('executed 后至少 1100ms 才刷新，取得新帧且满两秒后才能再输入',async()=>{
    const h=harness();await h.capture();h.enable();h.key();h.result();
    assert.equal(h.api.dodocoControl.pending,null);assert.match(h.element('dodocoControlStatus').textContent,/系统已接受输入/);
    await h.api.dodocoCapture();assert.equal(h.captures().length,1);
    await h.tick(1099);assert.equal(h.captures().length,1);assert.equal(h.element('dodocoCapture').disabled,true);
    await h.tick(1);assert.equal(h.captures().length,2);assert.equal(h.api.dodoco.autoBusy,true);
    h.api.dodocoControlSend('key',0,0,'Win+D');assert.equal(h.inputs().length,1);
    h.reply();await flush();assert.equal(h.element('dodocoCapture').disabled,false);
    await h.tick(899);h.api.dodocoControlSend('key',0,0,'Win+D');assert.equal(h.inputs().length,1);
    await h.tick(1);h.key();assert.equal(h.inputs().length,2);
});

test('busy 终态保持真实状态且不重试输入；刷新完成和两秒间隔分别门控',async()=>{
    const h=harness();await h.capture();h.enable();h.key();h.result('busy',{message:'输入闸门占用'});
    assert.match(h.element('dodocoControlStatus').textContent,/对方正忙/);assert.equal(h.api.dodocoControl.pending,null);
    h.api.dodocoControlSend('key',0,0,'Win+D');assert.equal(h.inputs().length,1);
    await h.tick(0);assert.equal(h.captures().length,2);assert.equal(h.inputs().length,1);
    await h.tick(2000);h.api.dodocoControlSend('key',0,0,'Win+D');assert.equal(h.inputs().length,1,'等够时间仍不能使用旧画面');
    h.reply();await flush();h.key();assert.equal(h.inputs().length,2);
});

test('三秒到期检查不依赖定时器；迟到 executed 仍是结果未确认且发送取消',async()=>{
    const h=harness();await h.capture();h.enable();h.key();const p=h.api.dodocoControl.pending;
    h.advance(3000);h.result();
    assert.equal(h.api.dodocoControl.pending,null);assert.match(h.element('dodocoControlStatus').textContent,/超时，结果未确认/);
    assert.doesNotMatch(h.element('dodocoControlStatus').textContent,/系统已接受输入/);
    assert.deepEqual(plain(h.cancels()[0].payload),{roomCode:'ROOM01',sessionId:p.visit.id,targetUid:'alice',controlId:p.controlId,requestId:p.id});
    h.api.dodocoEvent({name:'desktop.control.result',payload:{requestId:p.id,targetUid:'alice',status:'executed'}});
    h.api.dodocoControlSend('key',0,0,'Win+D');assert.equal(h.inputs().length,1);
});

test('回执须匹配 requestId 和被控 UID；无身份 ACK 不解除等待',async()=>{
    const h=harness();await h.capture();h.enable();h.key();const p=h.api.dodocoControl.pending;
    for(const result of [{ack:true},{requestId:'other',targetUid:'alice',status:'executed'},{requestId:p.id,targetUid:'bob',status:'executed'}])h.api.dodocoEvent({name:'desktop.control.result',payload:result});
    assert.equal(h.api.dodocoControl.pending,p);h.result('denied');assert.match(h.element('dodocoControlStatus').textContent,/拒绝/);
});

test('本机 command targetUid=local，桥路由保留所选 UID；取消使用独立无 signal 请求',async()=>{
    const h=harness({local:true});await h.capture();h.enable();h.key();const call=h.inputs()[0],p=h.api.dodocoControl.pending;
    assert.equal(call.args.targetUid,'local');assert.equal(call.uid,'alice');assert.equal(call.timeout,3000);assert.equal(call.signal.aborted,false);
    const toggle=h.element('dodocoControlEnabled');toggle.checked=false;toggle.onchange({target:toggle});
    assert.equal(call.signal.aborted,true);assert.equal(h.cancels().length,1);
    assert.deepEqual(plain(h.cancels()[0].args),{requestId:p.id});assert.equal(h.cancels()[0].uid,'alice');assert.equal(h.cancels()[0].signal,undefined);
    call.resolve({requestId:p.id,targetUid:'local',status:'executed'});await flush();assert.equal(h.api.dodocoControl.enabled,false);
    assert.doesNotMatch(h.element('dodocoControlStatus').textContent,/系统已接受输入/);
});

test('本机 local 回执被正确认领；三秒无回复同样调用独立取消',async()=>{
    const h=harness({local:true});await h.capture();h.enable();h.key();h.result('executed');await flush();
    assert.equal(h.api.dodocoControl.pending,null);assert.match(h.element('dodocoControlStatus').textContent,/系统已接受输入/);
    const timeout=harness({local:true});await timeout.capture();timeout.enable();timeout.key();await timeout.tick(3000);
    assert.equal(timeout.cancels().length,1);assert.equal(timeout.inputs()[0].signal.aborted,true);
    assert.match(timeout.element('dodocoControlStatus').textContent,/超时，结果未确认/);assert.equal(timeout.inputs().length,1);
});

for(const action of ['关闭','成员','历史','离开','断线','到期','放大','pagehide'])test(`${action} 时清控制并取消在途请求；晚回包不再改变状态`,async()=>{
    const h=harness();await h.capture();await h.capture();h.enable();h.key();const p=h.api.dodocoControl.pending;
    if(action==='关闭'){const el=h.element('dodocoControlEnabled');el.checked=false;el.onchange({target:el});}
    if(action==='成员')h.member('bob');
    if(action==='历史'){const el=h.element('dodocoFrames').querySelector('.dodoco-frame-select');el.value='1';el.onchange({target:el});}
    if(action==='离开')h.api.showProduct('gengdi');
    if(action==='断线'){h.context.roomReady=false;h.api.dodocoConnection(false);}
    if(action==='到期'){h.advance(180000);h.api.dodocoScreenAllowed();}
    if(action==='放大')h.element('dodocoFrames').querySelectorAll('.dodoco-frame-zoom')[1].click();
    if(action==='pagehide')h.windowListeners.find(l=>l.type==='pagehide').callback();
    assert.equal(h.api.dodocoControl.enabled,false);assert.equal(h.api.dodocoControl.pending,null);
    if(action!=='断线')assert.equal(h.cancels().length,1);
    const status=h.api.dodocoControl.status;
    h.api.dodocoEvent({name:'desktop.control.result',payload:{requestId:p.id,targetUid:'alice',status:'executed'}});
    assert.equal(h.api.dodocoControl.status,status);assert.equal(h.api.dodocoControl.pending,null);
});

test('未开启时点击放大；历史、放大和全员视图均不能输入',async()=>{
    const h=harness();await h.capture();h.image().click();assert.equal(h.element('dodocoZoomDialog').open,true);assert.equal(h.enable(),false);
    h.element('dodocoZoomClose').click();assert.equal(h.enable(),true);
    await h.capture();const history=h.element('dodocoFrames').querySelector('.dodoco-frame-select');history.value='1';history.onchange({target:history});
    assert.equal(h.enable(),false);h.member('*');assert.equal(h.enable(),false);assert.equal(h.inputs().length,0);
});

test('15 秒帧新鲜度包含系统休眠；授权撤销后不再发送',async()=>{
    for(const suspend of [false,true]){
        const h=harness();await h.capture();if(suspend)h.suspend(15000);else h.advance(15000);
        assert.equal(h.enable(),false);h.api.dodocoControlSend('key',0,0,'Win+D');assert.equal(h.inputs().length,0);assert.match(h.element('dodocoControlStatus').textContent,/15 秒/);
    }
    const h=harness();await h.capture();h.enable();await h.capture({controlAllowed:false});h.api.dodocoControlSend('key',0,0,'Win+D');
    assert.equal(h.inputs().length,0);assert.equal(h.element('dodocoControlKeys').querySelector('[data-control-key="Win+D"]').disabled,true);
});

test('重绘、换成员、重连不延长观看；新 visit 默认关闭且不能使用旧帧',async()=>{
    const h=harness();await h.capture();h.enable();const visit=h.api.dodoco.screenVisit;
    h.advance(120000);h.api.dodocoRender();h.member('bob');h.member('alice');h.api.dodocoConnection(false);h.api.dodocoConnection(true);
    assert.equal(h.api.dodoco.screenVisit,visit);h.advance(60000);assert.equal(h.api.dodocoScreenAllowed(),false);assert.equal(h.api.dodocoControl.enabled,false);
    h.tab('members');h.tab('screen');assert.notEqual(h.api.dodoco.screenVisit.id,visit.id);assert.equal(h.enable(),false);
    await h.capture();assert.equal(h.enable(),true);
});

test('切成员后旧本机截图及回执不解锁新成员或返回同一成员的新请求',async()=>{
    const h=harness({local:true});const oldCapture=h.api.dodocoCapture();await flush();const old=h.captures()[0];
    h.member('bob');const nextCapture=h.api.dodocoCapture();await flush();const next=h.captures()[1];
    h.reply(old);await oldCapture;assert.equal(h.api.dodoco.frames.has('alice'),false);assert.equal(h.api.dodoco.autoBusy,true);
    h.reply(next);await nextCapture;h.enable();h.key();const oldInput=h.inputs()[0],oldPending=h.api.dodocoControl.pending;
    h.member('alice');h.member('bob');await h.capture();h.advance(2000);h.enable();h.key();const pending=h.api.dodocoControl.pending;
    assert.notEqual(pending,oldPending);
    oldInput.resolve({requestId:oldPending.id,targetUid:'local',status:'executed'});await flush();assert.equal(h.api.dodocoControl.pending,pending);
    assert.equal(h.element('dodocoCapture').disabled,true);
});

for(const local of [false,true])test(`${local?'本机':'远端'} 旧 visit 回执不能解锁重入后的新输入`,async()=>{
    const h=harness({local});await h.capture();h.enable();h.key();const old=h.api.dodocoControl.pending,call=local?h.inputs()[0]:null;
    h.tab('members');h.tab('screen');h.advance(2000);await h.capture();h.enable();h.key();const current=h.api.dodocoControl.pending;
    const result={requestId:old.id,targetUid:old.commandTarget,status:'executed'};
    if(local)call.resolve(result);else h.api.dodocoEvent({name:'desktop.control.result',payload:result});
    await flush();assert.notEqual(current.visit,old.visit);assert.equal(h.api.dodocoControl.pending,current);
    assert.equal(h.element('dodocoCapture').disabled,true);assert.equal(h.inputs().length,2);
});

test('未知键、任意动作与非有限坐标不消耗新帧或发送输入',async()=>{
    const h=harness();await h.capture();h.enable();
    for(const args of [['key',0,0,'Ctrl+V'],['text',0,0,'Win+D'],['left',Infinity,0.5,''],['right',0.5,-0.1,''],['left',0.5,0.5,'F']])h.api.dodocoControlSend(...args);
    assert.equal(h.inputs().length,0);assert.equal(h.api.dodoco.frames.get('alice')[0].controlUsed,false);
    h.key();assert.equal(h.inputs().length,1);
});

test('同一本机帧刷新或重画不能重复使用，也不能续帧新鲜度',async()=>{
    const h=harness({local:true});await h.capture({frameId:'same'});h.enable();h.key();h.result('busy');await flush();await h.tick(0);
    h.reply(h.captures().at(-1),{frameId:'same'});await flush();await h.tick(2000);
    h.api.dodocoControlSend('key',0,0,'Win+D');assert.equal(h.inputs().length,1);
    assert.match(h.element('dodocoControlStatus').textContent,/已经用于输入/);
    const fresh=harness({local:true});await fresh.capture({frameId:'old'});fresh.advance(14000);await fresh.capture({frameId:'old'});fresh.advance(1000);
    assert.equal(fresh.enable(),false);assert.match(fresh.element('dodocoControlStatus').textContent,/15 秒/);
});

test('旧 executed 刷新回调不能在返回同一 UID 后干扰新请求',async()=>{
    const h=harness();await h.capture();h.enable();h.key();h.result();
    const callback=h.timers.get(h.api.dodocoControl.refreshTimer).callback;
    h.member('bob');h.member('alice');h.advance(2000);await h.capture();h.enable();h.key();const pending=h.api.dodocoControl.pending,count=h.captures().length;
    callback();await flush();assert.equal(h.api.dodocoControl.pending,pending);assert.equal(h.captures().length,count);assert.equal(h.element('dodocoCapture').disabled,true);
});

test('正式本机桥在 HTTP 下使用 getRandomValues，input abort 后仍独立发送 cancel',async()=>{
    const h=harness();const calls=[],stored=new Map();
    Object.assign(h.context,{
        URLSearchParams,URL,location:{protocol:'http:',hostname:'127.0.0.1',hash:'#token=pair',pathname:'/',search:''},history:{replaceState(){}},
        sessionStorage:{getItem:key=>stored.get(key),setItem:(key,value)=>stored.set(key,value),removeItem:key=>stored.delete(key)},
        fetch:async(url,options)=>{
            const body=JSON.parse(options.body);calls.push({url,body,signal:options.signal});
            if(url==='/api/pair')return {ok:true,json:async()=>({sessionId:'paired',instanceId:'bridge-1'})};
            let data={};
            if(body.operation==='local.identity')data={uid:'alice',name:'本机助手',clientInstanceId:'instance-1',machineId:'machine-1',status:{}};
            if(body.operation==='screenshot.capture')data={frameId:'real-bridge-frame',jpegBase64:'YQ==',geometry,controlAllowed:true};
            if(body.operation==='desktop.control.presence')data={requestId:body.args.requestId,controlId:body.args.controlId,targetUid:'local',status:body.args.action==='end'?'ended':'active'};
            if(body.operation==='desktop.control.input')return new Promise((_resolve,reject)=>options.signal.addEventListener('abort',()=>{const e=new Error('aborted');e.name='AbortError';reject(e);},{once:true}));
            return {ok:true,json:async()=>({requestId:body.requestId,instanceId:'bridge-1',status:'ok',data})};
        },
    });
    vm.runInContext(localSource,h.context);await flush();
    assert.equal(h.context.window.bgiLocalBridge.available,true);
    await h.api.dodocoCapture();h.enable();await flush();h.key();await flush();
    const input=calls.find(c=>c.body.operation==='desktop.control.input');assert.ok(input);assert.equal(input.body.args.targetUid,'local');
    const checkbox=h.element('dodocoControlEnabled');checkbox.checked=false;checkbox.onchange({target:checkbox});await flush();
    const cancel=calls.find(c=>c.body.operation==='desktop.control.cancel');assert.ok(cancel);assert.equal(cancel.body.args.requestId,input.body.args.requestId);
    assert.equal(input.signal.aborted,true);assert.equal(cancel.signal.aborted,false,'取消使用新的 fetch controller');
    assert.notEqual(cancel.body.requestId,input.body.requestId);assert.match(cancel.body.requestId,/^[a-f0-9]{32}$/);
    assert.equal(h.api.dodocoControl.pending,null);assert.equal(h.api.dodocoControl.enabled,false);
});
