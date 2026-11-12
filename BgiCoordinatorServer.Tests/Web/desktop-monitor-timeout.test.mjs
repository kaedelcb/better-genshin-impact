import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { webcrypto } from 'node:crypto';
import vm from 'node:vm';

// 执行正式网页入口和取图逻辑，用可控时钟验证三分钟后不再发图请求。
// 覆盖后台定时器未触发、切成员/重连不能续期、退出重入及迟到帧；不连接真实房间。
const source = readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/dodoco-web.js', import.meta.url), 'utf8');
function harness() {
    let now = 0, wall = 0, timerId = 0;
    const timers = new Map(), elements = new Map(), requests = [];
    const tabs = ['live', 'files', 'watch', 'members', 'screen'].map(tab => ({ dataset:{tab}, classList:{toggle(){}} }));
    const element = id => {
        if (!elements.has(id)) elements.set(id, {
            textContent:'', innerHTML:'', checked:false, disabled:false, value:'3000', hidden:false,
            classList:{toggle(){}}, querySelectorAll:selector => selector === '[data-tab]' ? tabs : [],
            querySelector:selector => element(id+selector), style:{}, close(){},
        });
        return elements.get(id);
    };
    const schedule = (callback, delay) => { const id=++timerId; timers.set(id,{callback,delay}); return id; };
    const context = vm.createContext({
        document:{getElementById:element}, window:{}, crypto:{getRandomValues:array=>webcrypto.getRandomValues(array)},
        performance:{now:()=>now}, Date:class extends Date { static now(){return wall;} },
        setInterval:schedule, clearInterval:id=>timers.delete(id), setTimeout:schedule, clearTimeout:id=>timers.delete(id),
        players:[{playerUid:'alice',online:true},{playerUid:'bob',online:true}], roomReady:true, roomCode:'ABC123',
        connection:{invoke:async (_method,envelope)=>{requests.push(envelope);return {payload:{ack:true}};}},
        makeEnvelope:(_type,name,payload)=>({name,payload}), checkGatewayError(){}, escapeHtml:String,
        newRequestId:()=>`other${++timerId}`, showHome(){}, console,
    });
    vm.runInContext(source + `
        dodocoDrawFrames=()=>{};
        this.api={dodoco,showProduct,dodocoRender,dodocoScreenSync,dodocoScreenAllowed,dodocoCapture,dodocoCaptureOne,dodocoEvent,dodocoConnection,dodocoWait};
    `,context);
    const api=context.api;
    api.dodoco.active=true;api.dodoco.tab='screen';api.dodoco.target='alice';api.dodocoRender();
    return {api,requests,element,tabs,context,timers,
        advance(ms,fire=false){now+=ms;wall+=ms;if(fire)for(const {callback} of [...timers.values()])callback();},
        suspend(ms){wall+=ms;},
        reply(request=requests.at(-1)){api.dodocoEvent({name:'screenshot.member',payload:{uid:request.payload.targetUid,requestId:request.payload.requestId,jpegBase64:'YQ=='}});},
        tab(name){tabs.find(t=>t.dataset.tab===name).onclick();},
    };
}
const flush=()=>new Promise(resolve=>setImmediate(resolve));

test('remote requests share one visit, expire at exactly three minutes even without timer ticks',async()=>{
    const h=harness(),visit=h.api.dodoco.screenVisit;
    const first=h.api.dodocoCapture();await flush();h.reply();await first;
    assert.match(h.requests[0].payload.requestId,/^screen:[a-f0-9]{32}:[a-f0-9]{32}$/);
    assert.equal(h.requests[0].payload.requestId.split(':')[1],visit.id);
    h.advance(179999);
    assert.equal(h.api.dodocoScreenAllowed(),true);
    h.advance(1);
    await h.api.dodocoCapture();
    await assert.rejects(h.api.dodocoCaptureOne('bob'),/3 分钟/);
    assert.equal(h.requests.length,1);
    assert.equal(h.element('dodocoCapture').disabled,true);
    assert.equal(h.element('dodocoAuto').disabled,true);
    assert.match(h.element('dodocoStatus').textContent,/3 分钟/);
    assert.equal(h.api.dodoco.frames.get('alice').length,1,'保留已收到的历史画面');
});

test('member selection, repeated screen click, rendering and reconnect never renew a visit',async()=>{
    const h=harness(),visit=h.api.dodoco.screenVisit;
    h.advance(120000);
    h.element('dodocoMember').onchange({target:{value:'bob'}});
    h.tab('screen');h.api.dodocoRender();
    h.api.dodocoConnection(false);h.api.dodocoConnection(true);
    assert.equal(h.api.dodoco.screenVisit,visit);
    h.advance(60000,true);
    h.element('dodocoAuto').onchange({target:{checked:true}});
    await h.api.dodocoCapture();
    assert.equal(h.requests.length,0);
    assert.equal(h.api.dodoco.autoTimer,null);
    assert.match(h.element('dodocoStatus').textContent,/3 分钟/);
    h.api.dodocoRender();
    assert.equal(h.element('dodocoCapture').disabled,true,'重绘仍禁用取图');
});

test('leaving and reentering tab or product opens a fresh visit; leaving alone cannot capture',async()=>{
    const h=harness(),first=h.api.dodoco.screenVisit.id;
    h.advance(180000,true);h.tab('members');
    assert.equal(h.api.dodoco.screenVisit,null);
    await h.api.dodocoCapture();assert.equal(h.requests.length,0);
    h.tab('screen');
    assert.notEqual(h.api.dodoco.screenVisit.id,first);
    assert.equal(h.api.dodocoScreenAllowed(),true);
    const second=h.api.dodoco.screenVisit.id;
    h.api.showProduct('gengdi');h.api.showProduct('dodoco');
    assert.notEqual(h.api.dodoco.screenVisit.id,second);
    assert.equal(h.api.dodocoScreenAllowed(),true);
});

test('expiry cancels only screenshot requests, ignores late frames and keeps timeout message',async()=>{
    const h=harness();
    const capture=h.api.dodocoCapture();await flush();const old=h.requests[0];
    const log=h.api.dodocoWait('files','alice','log-request');
    h.advance(180000);h.api.dodocoScreenAllowed();await capture;
    h.reply(old);
    assert.equal(h.api.dodoco.frames.size,0);
    assert.equal(h.element('dodocoCapture').disabled,true);
    assert.match(h.element('dodocoStatus').textContent,/3 分钟/);
    assert.equal(h.api.dodoco.pending.has('log-request'),true,'监控到期不取消日志请求');
    h.api.dodocoEvent({name:'log.fileList',payload:{uid:'alice',requestId:'log-request',files:[]}});
    await log;
});

test('late local bridge response cannot overwrite a new visit or unlock its busy capture',async()=>{
    const h=harness(),local=[];
    h.context.window.bgiLocalBridge={available:true,usesLocal:()=>true,members:()=>h.context.players,
        execute:()=>new Promise(resolve=>local.push(resolve)),selectTarget(){}};
    const old=h.api.dodocoCapture();await flush();
    h.tab('members');h.tab('screen');
    const next=h.api.dodocoCapture();await flush();
    local[0]({jpegBase64:'b2xk'});await old;
    assert.equal(h.api.dodoco.frames.size,0);
    assert.equal(h.api.dodoco.autoBusy,true);
    assert.equal(h.element('dodocoCapture').disabled,true);
    local[1]({jpegBase64:'bmV3'});await next;
    assert.equal(h.api.dodoco.frames.get('alice')[0].jpegBase64,'bmV3');
});

test('automatic refresh stops at expiry and changing the interval cannot restart it',async()=>{
    const h=harness();
    h.element('dodocoAuto').onchange({target:{checked:true}});
    await flush();h.reply();await flush();
    assert.notEqual(h.api.dodoco.autoTimer,null);
    h.advance(180000,true);await flush();
    assert.equal(h.api.dodoco.autoTimer,null);
    assert.equal(h.element('dodocoAuto').checked,false);
    assert.equal(h.element('dodocoInterval').disabled,true);
    h.element('dodocoAuto').checked=true;
    h.element('dodocoInterval').onchange();await flush();
    assert.equal(h.requests.length,1);
    assert.equal(h.api.dodoco.autoTimer,null);
});

test('wall clock catches system sleep even if performance clock did not advance',async()=>{
    const h=harness();h.suspend(180000);
    await h.api.dodocoCapture();
    assert.equal(h.requests.length,0);
    assert.equal(h.api.dodoco.screenVisit.expired,true);
});
