import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { webcrypto } from 'node:crypto';
import vm from 'node:vm';

export const source = readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/dodoco-web.js', import.meta.url), 'utf8');
export const localSource = readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/local-dodoco.js', import.meta.url), 'utf8');
export const contracts = readFileSync(new URL('../../BgiCoordinatorServer/Models/DesktopControlContracts.cs', import.meta.url), 'utf8');
export const geometry = { left:-1920, top:120, width:2560, height:1440, displayId:'primary', layoutId:'layout-150' };
export const flush = () => new Promise(resolve => setImmediate(resolve));
export const plain = value => JSON.parse(JSON.stringify(value));
export const pointer = (overrides={}) => ({ detail:1, button:0, clientX:210, clientY:170, preventDefault(){this.defaultPrevented=true;}, ...overrides });

// 只实现页面使用的 DOM 原语，不替换 dodocoRenderScreen/dodocoDrawFrames 等正式函数。
export function dom() {
    const ids=new Map(), listeners=[];
    const decode=s=>s.replace(/&quot;/g,'"').replace(/&#39;/g,"'").replace(/&lt;/g,'<').replace(/&gt;/g,'>').replace(/&amp;/g,'&');
    class Element {
        constructor(tag='div',attrs={}) {
            this.tagName=tag.toUpperCase(); this.children=[]; this.parentElement=null; this.attributes={}; this.dataset={};
            this.style={}; this.value=''; this.checked=false; this.disabled=false; this.open=false; this._text=''; this._html='';
            this.naturalWidth=1600; this.naturalHeight=900; this.rect={left:10,top:20,width:400,height:300};
            this.classList={
                contains:name=>(this.className||'').split(/\s+/).includes(name),
                add:(...names)=>names.forEach(name=>this.classList.toggle(name,true)),
                remove:(...names)=>names.forEach(name=>this.classList.toggle(name,false)),
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
        removeAttribute(name){delete this.attributes[name];}
        remove(){if(this.parentElement)this.parentElement.children=this.parentElement.children.filter(c=>c!==this);this.removeChildren();if(this.id)ids.delete(this.id);}
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
        body,getElementById:id=>ids.get(id)||null,querySelector:s=>body.querySelector(s),createElement:tag=>new Element(tag),
        addEventListener:(type,callback)=>listeners.push({target:'document',type,callback}),
    }};
}
export function harness({local=false,mobile=false,pointerEvents=false}={}) {
    let now=0,wall=0,serial=0;const timers=new Map(),requests=[],localCalls=[],windowListeners=[];
    const ui=dom();
    const schedule=(callback,delay,interval=0)=>{const id=++serial;timers.set(id,{callback,delay,at:now+delay,interval});return id;};
    const context=vm.createContext({
        document:ui.document,window:{addEventListener:(type,callback)=>windowListeners.push({type,callback}),devicePixelRatio:1.5,
            innerWidth:mobile?375:1024,innerHeight:mobile?812:768,matchMedia:()=>({matches:mobile}),
            ...(pointerEvents?{PointerEvent:function(){}}:{})},
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
            dodocoRemote,dodocoRemoteOpen,dodocoRemotePoint,dodocoRemoteLayout,dodocoRemoteReady,dodocoRemoteSync,
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
        paint(){ui.element('dodocoRemoteImage').onload?.();},
        emitWindow(type,event={}){for(const l of windowListeners)if(l.type===type)l.callback(event);},
        emitDocument(type,event={}){for(const l of ui.listeners)if(l.target==='document'&&l.type===type)l.callback(event);},
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
