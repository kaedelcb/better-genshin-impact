import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { webcrypto } from 'node:crypto';
import vm from 'node:vm';

// 正式脚本与事件绑定在隔离 VM 中执行；输入、桥、网关和时钟全是假实现。
// 这些断言决定网页能否开放控制：ACK 不能当成功，旧请求不能解锁新来源，
// 新帧/两秒/三分钟门控与取消都需成立。既有 protocol/timeout 测试未覆盖输入。
const source = readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/dodoco-web.js', import.meta.url), 'utf8');
const localSource = readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/local-dodoco.js', import.meta.url), 'utf8');
const contracts = readFileSync(new URL('../../BgiCoordinatorServer/Models/DesktopControlContracts.cs', import.meta.url), 'utf8');
const geometry = { left:-1920, top:120, width:2560, height:1440, displayId:'primary', layoutId:'layout-150' };
const flush = () => new Promise(resolve => setImmediate(resolve));
const plain = value => JSON.parse(JSON.stringify(value));
const pointer = (overrides={}) => ({ detail:1, button:0, clientX:210, clientY:170, preventDefault(){this.defaultPrevented=true;}, ...overrides });

// 只实现页面使用的 DOM 原语，不替换 dodocoRenderScreen/dodocoDrawFrames 等正式函数。
function dom() {
    const ids=new Map(), listeners=[];
    const decode=s=>s.replace(/&quot;/g,'"').replace(/&#39;/g,"'").replace(/&lt;/g,'<').replace(/&gt;/g,'>').replace(/&amp;/g,'&');
    class Element {
        constructor(tag='div',attrs={}) {
            this.tagName=tag.toUpperCase(); this.children=[]; this.parentElement=null; this.attributes={}; this.dataset={};
            this.style={}; this.value=''; this.checked=false; this.disabled=false; this.open=false; this._text=''; this._html='';
            this.naturalWidth=1600; this.naturalHeight=900; this.rect={left:10,top:20,width:400,height:300};
            this.classList={
                contains:name=>(this.className||'').split(/\s+/).includes(name),
                toggle:(name,force)=>{const names=new Set((this.className||'').split(/\s+/).filter(Boolean));
                    const on=force??!names.has(name); if(on)names.add(name);else names.delete(name);this.className=[...names].join(' ');return on;},
            };
            for(const [name,value] of Object.entries(attrs))this.setAttribute(name,value);
        }
        setAttribute(name,value) {
            this.attributes[name]=String(value);
            if(name==='id'){this.id=value;ids.set(value,this);}
            else if(name==='class')this.className=value;
            else if(name==='value')this.value=value;
            else if(name==='disabled'||name==='checked'||name==='selected')this[name]=true;
            else if(name.startsWith('data-'))this.dataset[name.slice(5).replace(/-([a-z])/g,(_,c)=>c.toUpperCase())]=value;
        }
        getAttribute(name){return this.attributes[name]??null;}
        removeChildren(){for(const child of this.children){child.removeChildren();if(child.id && ids.get(child.id)===child)ids.delete(child.id);}this.children=[];}
        set innerHTML(html){
            this.removeChildren(); this._html=String(html); this._text='';
            const stack=[this],voids=new Set(['input','img','br','hr','meta','link']);
            for(const token of this._html.matchAll(/<\/?[^>]+>|[^<]+/g)){
                const text=token[0];
                if(text.startsWith('</')){if(stack.length>1)stack.pop();continue;}
                if(!text.startsWith('<')){stack.at(-1)._text+=decode(text);continue;}
                const tag=/^<([\w-]+)/.exec(text)?.[1];if(!tag)continue;
                const attrs={};for(const m of text.slice(tag.length+1,-1).matchAll(/([\w:-]+)(?:="([^"]*)"|='([^']*)'|=([^\s>]+))?/g))attrs[m[1]]=decode(m[2]??m[3]??m[4]??'');
                const child=new Element(tag,attrs);stack.at(-1).appendChild(child);
                if(!voids.has(tag))stack.push(child);
            }
        }
        get innerHTML(){return this._html;}
        set textContent(value){this.removeChildren();this._text=String(value);}
        get textContent(){return this._text+this.children.map(c=>c.textContent).join('');}
        appendChild(child){child.parentElement=this;this.children.push(child);if(child.tagName==='OPTION' && (child.selected || this.children.length===1))this.value=child.value;return child;}
        matches(selector){
            if(selector.startsWith('.'))return selector.slice(1).split('.').every(name=>this.classList.contains(name));
            if(selector.startsWith('#'))return this.id===selector.slice(1);
            const attr=/^\[([^=\]]+)(?:="([^"]*)")?\]$/.exec(selector);
            if(attr)return Object.hasOwn(this.attributes,attr[1]) && (attr[2]===undefined || this.attributes[attr[1]]===attr[2]);
            return this.tagName===selector.toUpperCase();
        }
        querySelectorAll(selector){return this.children.flatMap(c=>[...(c.matches(selector)?[c]:[]),...c.querySelectorAll(selector)]);}
        querySelector(selector){return this.querySelectorAll(selector)[0]||null;}
        addEventListener(type,callback){listeners.push({target:this,type,callback});}
        getBoundingClientRect(){return this.rect;}
        showModal(){this.open=true;}
        close(){this.open=false;this.onclose?.({target:this});}
        click(event=pointer()){if(!this.disabled)this.onclick?.({...event,target:this});}
    }
    const body=new Element('body');
    for(const id of ['dodocoView','gengdiProduct','dodocoProduct','gengdiNav','activity','homeView','detailView','selectionBar','loginError','loginPanel','controlPanel','roomInfo','connectionText','connDot'])body.appendChild(new Element('div',{id}));
    body.appendChild(new Element('div',{class:'topbar'}));
    return {ids,listeners,element:id=>ids.get(id),document:{
        getElementById:id=>ids.get(id)||null,querySelector:s=>body.querySelector(s),createElement:tag=>new Element(tag),
        addEventListener:(type,callback)=>listeners.push({target:'document',type,callback}),
    }};
}
function harness({local=false}={}) {
    let now=0,wall=0,serial=0;const timers=new Map(),requests=[],localCalls=[],windowListeners=[];
    const ui=dom();
    const schedule=(callback,delay,interval=0)=>{const id=++serial;timers.set(id,{callback,delay,at:now+delay,interval});return id;};
    const context=vm.createContext({
        document:ui.document,window:{addEventListener:(type,callback)=>windowListeners.push({type,callback}),devicePixelRatio:1.5},
        crypto:{getRandomValues:array=>webcrypto.getRandomValues(array)},Uint8Array,AbortController,
        performance:{now:()=>now},Date:class extends Date {static now(){return wall;}},
        setTimeout:(fn,delay=0)=>schedule(fn,delay),setInterval:(fn,delay)=>schedule(fn,delay,delay),
        clearTimeout:id=>timers.delete(id),clearInterval:id=>timers.delete(id),
        players:[{playerUid:'alice',playerName:'成员甲',online:true},{playerUid:'bob',playerName:'成员乙',online:true}],roomReady:true,roomCode:'ROOM01',
        connection:{invoke:async(_method,envelope)=>{requests.push(envelope);return {payload:{ack:true}};}},
        makeEnvelope:(_type,name,payload)=>({name,payload}),checkGatewayError(){},newRequestId:()=>`other${++serial}`,
        escapeHtml:value=>String(value).replace(/&/g,'&amp;').replace(/"/g,'&quot;').replace(/</g,'&lt;').replace(/>/g,'&gt;'),
        showHome(){},console,
    });
    vm.runInContext(source+`
        this.api={dodoco,dodocoControl,dodocoControlKeys,dodocoControlSend,dodocoControlReason,dodocoControlSync,dodocoControlPoint,
            dodocoCapture,dodocoCaptureOne,dodocoEvent,dodocoConnection,dodocoMembers,dodocoScreenAllowed,dodocoRender,showProduct};
    `,context);
    const api=context.api;
    if(local){context.window.bgiLocalBridge={available:true,usesLocal:()=>true,members:players=>players,selectTarget(){},
        execute:(operation,args,timeout,uid,signal)=>{
            const call={operation,args,timeout,uid,signal};localCalls.push(call);
            if(operation==='desktop.control.cancel')return Promise.resolve({});
            return new Promise((resolve,reject)=>{call.resolve=resolve;call.reject=reject;});
        },
    };}
    api.dodoco.active=true;api.dodoco.tab='screen';api.dodoco.target='alice';api.dodocoRender();
    const captures=()=>local?localCalls.filter(c=>c.operation==='screenshot.capture'):requests.filter(c=>c.name==='screenshot.request');
    const inputs=()=>local?localCalls.filter(c=>c.operation==='desktop.control.input'):requests.filter(c=>c.name==='desktop.control.request');
    const cancels=()=>local?localCalls.filter(c=>c.operation==='desktop.control.cancel'):requests.filter(c=>c.name==='desktop.control.cancel');
    const reply=(request=captures().at(-1),extra={})=>{
        const data={jpegBase64:'YQ==',width:1600,height:900,capturedAt:'2026-11-12T10:00:00',geometry:{...geometry},controlAllowed:true,...extra};
        if(local)request.resolve({frameId:(captures().indexOf(request)+1).toString(16).padStart(32,'0'),...data});
        else api.dodocoEvent({name:'screenshot.member',payload:{uid:request.payload.targetUid,requestId:request.payload.requestId,...data}});
    };
    return {...ui,api,context,timers,requests,localCalls,windowListeners,captures,inputs,cancels,reply,
        enable(){const el=ui.element('dodocoControlEnabled');el.checked=true;el.onchange({target:el});return api.dodocoControl.enabled;},
        key(key='Win+D',event=pointer()){ui.element('dodocoControlKeys').querySelector(`[data-control-key="${key}"]`).click(event);},
        image(){return ui.element('dodocoFrames').querySelector('.dd-image-btn');},
        member(uid){const el=ui.element('dodocoMember');el.value=uid;el.onchange({target:el});},
        tab(name){ui.element('dodocoView').querySelector(`[data-tab="${name}"]`).click();},
        async capture(extra={}){const count=captures().length,promise=api.dodocoCapture();await flush();assert.equal(captures().length,count+1,'确实通过正式入口请求新帧');reply(captures().at(-1),extra);await promise;},
        result(status='executed',extra={}){const p=api.dodocoControl.pending;const result={requestId:p.id,targetUid:p.commandTarget,status,...extra};
            if(local)localCalls.filter(c=>c.operation==='desktop.control.input').at(-1).resolve(result);
            else api.dodocoEvent({name:'desktop.control.result',payload:result});},
        advance(ms){now+=ms;wall+=ms;},suspend(ms){wall+=ms;},
        async tick(ms){const end=now+ms;
            for(let i=0;i<1000;i++){
                const next=[...timers].filter(([,t])=>t.at<=end).sort((a,b)=>a[1].at-b[1].at || a[0]-b[0])[0];
                if(!next)break;
                const [id,t]=next;wall+=t.at-now;now=t.at;
                if(t.interval)t.at+=t.interval;else timers.delete(id);
                t.callback();await flush();if(i===999)throw new Error('定时器循环未结束');
            }
            wall+=end-now;now=end;await flush();
        },
    };
}

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
    assert.deepEqual(plain(h.cancels()[0].payload),{roomCode:'ROOM01',sessionId:p.visit.id,targetUid:'alice'});
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
        const h=harness();await h.capture();h.enable();if(suspend)h.suspend(15000);else h.advance(15000);
        h.api.dodocoControlSend('key',0,0,'Win+D');assert.equal(h.inputs().length,0);assert.match(h.element('dodocoControlStatus').textContent,/15 秒/);
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
            if(body.operation==='desktop.control.input')return new Promise((_resolve,reject)=>options.signal.addEventListener('abort',()=>{const e=new Error('aborted');e.name='AbortError';reject(e);},{once:true}));
            return {ok:true,json:async()=>({requestId:body.requestId,instanceId:'bridge-1',status:'ok',data})};
        },
    });
    vm.runInContext(localSource,h.context);await flush();
    assert.equal(h.context.window.bgiLocalBridge.available,true);
    await h.api.dodocoCapture();h.enable();h.key();await flush();
    const input=calls.find(c=>c.body.operation==='desktop.control.input');assert.ok(input);assert.equal(input.body.args.targetUid,'local');
    const checkbox=h.element('dodocoControlEnabled');checkbox.checked=false;checkbox.onchange({target:checkbox});await flush();
    const cancel=calls.find(c=>c.body.operation==='desktop.control.cancel');assert.ok(cancel);assert.equal(cancel.body.args.requestId,input.body.args.requestId);
    assert.equal(input.signal.aborted,true);assert.equal(cancel.signal.aborted,false,'取消使用新的 fetch controller');
    assert.notEqual(cancel.body.requestId,input.body.requestId);assert.match(cancel.body.requestId,/^[a-f0-9]{32}$/);
    assert.equal(h.api.dodocoControl.pending,null);assert.equal(h.api.dodocoControl.enabled,false);
});
