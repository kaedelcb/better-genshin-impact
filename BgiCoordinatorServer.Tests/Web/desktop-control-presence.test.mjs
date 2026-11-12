import test from 'node:test';
import assert from 'node:assert/strict';
import {harness,flush} from './desktop-control-harness.mjs';

// 所有目标、时钟、桥和全屏 API 都是模拟；不发送真实输入。
const requests=h=>h.requests.filter(r=>r.name==='desktop.control.presence').map(r=>r.payload.presence);
function result(h,p,status='active',message='') {
    h.api.dodocoEvent({name:'desktop.control.presence.result',payload:{requestId:p.requestId,controlId:p.controlId,targetUid:p.targetUid,status,message}});
}

test('转发 ACK 不能解锁输入；目标确认后绑定控制 ID',async()=>{
    const h=harness({autoPresence:false});await h.capture();h.enable();await flush();
    const start=requests(h)[0];assert.equal(start.action,'start');
    assert.match(h.element('dodocoControlStatus').textContent,/等待对方确认/);
    assert.equal(h.element('dodocoControlKeys').querySelector('[data-control-key="Win+D"]').disabled,true);
    h.api.dodocoControlSend('key',0,0,'Win+D');assert.equal(h.inputs().length,0);
    result(h,start);h.key();assert.equal(h.inputs().length,1);
    assert.equal(h.inputs()[0].payload.command.controlId,start.controlId);
});

test('目标拒绝和无回执都关闭模式并发送 end',async()=>{
    for(const denied of [true,false]){
        const h=harness({autoPresence:false});await h.capture();h.enable();const start=requests(h)[0];
        if(denied)result(h,start,'denied','窗口不可用');else await h.tick(3000);
        assert.equal(h.api.dodocoControl.enabled,false);assert.equal(requests(h).at(-1).action,'end');
        assert.match(h.element('dodocoControlStatus').textContent,denied?/窗口不可用/:/超时/);
        result(h,start);assert.equal(h.api.dodocoControl.enabled,false);
    }
});

test('关闭先于 start 回执，快速重开不接纳旧确认或旧 end',async()=>{
    const h=harness({autoPresence:false});await h.capture();h.enable();const first=requests(h)[0];
    let toggle=h.element('dodocoControlEnabled');toggle.checked=false;toggle.onchange({target:toggle});
    assert.equal(requests(h).at(-1).controlId,first.controlId);
    h.enable();const second=requests(h).at(-1);assert.notEqual(first.controlId,second.controlId);
    result(h,first);h.key();assert.equal(h.inputs().length,0);
    result(h,second);h.key();assert.equal(h.inputs().length,1);
    assert.equal(h.inputs()[0].payload.command.controlId,second.controlId);
});

test('关闭旧输入时 cancel 保留旧控制 ID，重开使用新 ID',async()=>{
    const h=harness();await h.capture();h.enable();h.key();
    const old=h.api.dodocoControl.pending,oldId=old.controlId;
    const toggle=h.element('dodocoControlEnabled');toggle.checked=false;toggle.onchange({target:toggle});
    assert.equal(h.cancels().at(-1).payload.controlId,oldId);
    assert.equal(h.cancels().at(-1).payload.requestId,old.id);
    await h.capture();h.advance(2000);h.enable();const current=h.api.dodocoControl.presence;
    assert.notEqual(current.id,oldId);
    assert.equal(h.cancels().at(-1).payload.controlId,oldId);
    h.key();assert.equal(h.inputs().at(-1).payload.command.controlId,current.id);
});

test('续期使用初始帧，拒绝或超时后停止输入并结束',async()=>{
    for(const reject of [true,false]){
        const h=harness({autoPresence:false});await h.capture();h.enable();const start=requests(h)[0];result(h,start);
        await h.capture();await h.tick(4000);const renew=requests(h).at(-1);
        assert.equal(renew.action,'renew');assert.equal(renew.frameId,start.frameId);
        if(reject)result(h,renew,'denied','租约失效');else await h.tick(3000);
        assert.equal(h.api.dodocoControl.enabled,false);assert.equal(requests(h).at(-1).action,'end');
        result(h,renew);h.api.dodocoControlSend('key',0,0,'Win+D');assert.equal(h.inputs().length,0);
    }
});

test('本机桥使用独立 presence 调用，目标确认前禁用输入',async()=>{
    const h=harness({local:true,autoPresence:false});await h.capture();h.enable();
    const call=h.localCalls.find(c=>c.operation==='desktop.control.presence');
    assert.equal(call.args.targetUid,'local');assert.equal(call.uid,'alice');assert.equal(call.signal,undefined);
    h.key();assert.equal(h.inputs().length,0);
    call.resolve({requestId:call.args.requestId,controlId:call.args.controlId,targetUid:'local',status:'active'});await flush();
    h.key();assert.equal(h.inputs().length,1);assert.equal(h.inputs()[0].args.controlId,call.args.controlId);
    h.emitWindow('pagehide');const end=h.localCalls.filter(c=>c.operation==='desktop.control.presence').at(-1);
    assert.equal(end.args.action,'end');assert.equal(end.signal,undefined);
});

test('手机全屏请求与点击同步，等待确认时画面可见但输入禁用',async()=>{
    const h=harness({mobile:true,autoPresence:false});await h.capture();let gesture=false,called=false;
    h.element('dodocoRemoteRoot').requestFullscreen=()=>{assert.equal(gesture,true);called=true;return Promise.resolve();};
    gesture=true;h.enable();gesture=false;
    assert.equal(called,true);assert.equal(h.api.dodocoRemote.open,true);
    assert.equal(h.element('dodocoRemoteKeys').querySelector('[data-remote-key="Win+D"]').disabled,true);
    h.api.dodocoControlSend('key',0,0,'Win+D');assert.equal(h.inputs().length,0);
    result(h,requests(h)[0]);h.paint();assert.equal(h.element('dodocoRemoteKeys').querySelector('[data-remote-key="Win+D"]').disabled,false);
});

test('多次刷新和续期不会延长三分钟',async()=>{
    const h=harness();await h.capture();h.enable();const visit=h.api.dodoco.screenVisit;
    for(let i=0;i<3;i++){await h.tick(4000);await h.capture();assert.equal(h.api.dodoco.screenVisit,visit);}
    h.advance(168000);assert.equal(h.api.dodocoScreenAllowed(),false);
    assert.equal(h.api.dodocoControl.enabled,false);assert.equal(requests(h).at(-1).action,'end');
});

test('续期等待目标确认时禁止输入，迟到确认无法恢复',async()=>{
    const h=harness({autoPresence:false});await h.capture();h.enable();result(h,requests(h)[0]);
    await h.tick(4000);const renew=requests(h).at(-1);
    h.key();h.image().click();assert.equal(h.inputs().length,0);
    await h.tick(3000);result(h,renew);assert.equal(h.api.dodocoControl.enabled,false);
    assert.equal(requests(h).at(-1).action,'end');
});

test('只观看不发 presence；没有输入时退出和隐藏也发送 end',async()=>{
    for(const action of ['关闭','隐藏','断线','历史']){
        const h=harness();await h.capture();await h.capture();assert.equal(requests(h).length,0);h.enable();
        if(action==='关闭'){const el=h.element('dodocoControlEnabled');el.checked=false;el.onchange({target:el});}
        if(action==='隐藏'){h.document.hidden=true;h.emitDocument('visibilitychange');}
        if(action==='断线'){h.context.roomReady=false;h.api.dodocoConnection(false);}
        if(action==='历史'){const el=h.element('dodocoFrames').querySelector('.dodoco-frame-select');el.value='1';el.onchange({target:el});}
        assert.equal(h.inputs().length,0);assert.equal(requests(h).at(-1).action,'end');
        assert.equal(h.api.dodocoControl.enabled,false);
    }
});

test('房间、连接和本机桥变化不能接受旧确认或发送输入',async()=>{
    for(const kind of ['room','connection','bridge']){
        const h=harness({local:kind==='bridge',autoPresence:false});await h.capture();h.enable();
        const start=kind==='bridge'?h.localCalls.find(c=>c.operation==='desktop.control.presence'):requests(h)[0];
        if(kind==='room')h.context.roomCode='OTHER';
        if(kind==='connection')h.context.connection={invoke:async()=>({})};
        if(kind==='bridge')h.context.window.bgiLocalBridge={...h.context.window.bgiLocalBridge};
        if(kind==='bridge'){start.resolve({requestId:start.args.requestId,controlId:start.args.controlId,targetUid:'local',status:'active'});await flush();}
        else result(h,start);
        h.api.dodocoControlSend('key',0,0,'Win+D');assert.equal(h.inputs().length,0);
        assert.equal(h.api.dodocoControl.enabled,false);
        if(kind==='bridge')assert.equal(h.localCalls.filter(c=>c.operation==='desktop.control.presence').at(-1).args.action,'end');
        else assert.equal(requests(h).at(-1).action,'end');
    }
});

test('房间变化后旧画面和旧输入回执不能用于新上下文',async()=>{
    const stale=harness();await stale.capture();stale.context.roomCode='OTHER';
    assert.equal(stale.enable(),false);assert.equal(requests(stale).length,0);
    const h=harness();await h.capture();h.enable();h.key();const pending=h.api.dodocoControl.pending;
    h.context.roomCode='OTHER';h.result('executed');
    assert.equal(h.api.dodocoControl.enabled,false);assert.equal(requests(h).at(-1).action,'end');
    assert.doesNotMatch(h.api.dodocoControl.status,/系统已接受输入/);
    assert.equal(h.api.dodocoControl.pending,null);assert.ok(pending);
});

test('本机桥确认不能由远端事件冒充',async()=>{
    const h=harness({local:true,autoPresence:false});await h.capture();h.enable();
    const call=h.localCalls.find(c=>c.operation==='desktop.control.presence');
    result(h,{...call.args},'active');h.key();assert.equal(h.inputs().length,0);
    call.resolve({requestId:call.args.requestId,controlId:call.args.controlId,targetUid:'local',status:'active'});await flush();
    h.key();assert.equal(h.inputs().length,1);
});

test('网页和全屏键盘实际发送 W S A D，旧方向键不存在',async()=>{
    for(const mobile of [false,true])for(const key of ['W','S','A','D']){
        const h=harness({mobile});await h.capture();h.enable();if(mobile)h.paint();
        const host=h.element(mobile?'dodocoRemoteKeys':'dodocoControlKeys'),attr=mobile?'data-remote-key':'data-control-key';
        for(const old of ['Up','Down','Left','Right'])assert.equal(host.querySelector(`[${attr}="${old}"]`),null);
        const button=host.querySelector(`[${attr}="${key}"]`);assert.ok(button);button.click();
        assert.equal(h.inputs().length,1);assert.equal(h.inputs()[0].payload.command.key,key);
    }
});
