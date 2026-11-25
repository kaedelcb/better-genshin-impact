// 编排模块的 UI/协议边界测试：只使用内存 DOM 与合成执行端，不连接服务器、不启动 BGI。
// 实际浏览器的排版/键盘/触摸由独立浏览器测试核验；这里检查真实模块发出的请求与状态变更。
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../../BgiCoordinatorServer/wwwroot/control-room-orchestration.js'), 'utf8');
const clone = value => value === undefined ? undefined : JSON.parse(JSON.stringify(value));
const flush = async () => { for (let i = 0; i < 3; ++i) await new Promise(resolve => setImmediate(resolve)); };
const target = {uid:'fixture-uid',instanceId:'fixture-instance',machineId:'fixture-machine',name:'合成执行端',
    requestPrefixes:{orchestration:'room-orchestration-web-fixture-'}};
const prefix = target.requestPrefixes.orchestration;

function documentDouble() {
    const doc = {hidden:false, activeElement:null, addEventListener(){}, removeEventListener(){}};
    const datasetKey = raw => raw.replace(/-([a-z])/g,(_,c) => c.toUpperCase());
    class Element {
        constructor(tag) {
            this.tagName = tag.toUpperCase(); this.children = []; this.parentElement = null;
            this.dataset = {}; this.attrs = new Map(); this.className = ''; this.listeners = new Map();
            this._text = ''; this._value = ''; this.disabled = false; this.hidden = false; this.checked = false;
            this.classList = {
                contains: c => this.className.split(/\s+/).includes(c),
                add: c => { if (!this.classList.contains(c)) this.className = (this.className+' '+c).trim(); },
                remove: c => { this.className = this.className.split(/\s+/).filter(x=>x!==c).join(' '); },
                toggle: (c, on) => { const wanted=on ?? !this.classList.contains(c); wanted ? this.classList.add(c) : this.classList.remove(c); }
            };
            this.scrollTop=0; this.scrollHeight=0; this.clientHeight=0;
        }
        get textContent() { return this._text + this.children.map(n=>n.textContent).join(''); }
        set textContent(v) { for (const n of this.children) n.parentElement=null; this.children=[]; this._text=String(v ?? ''); }
        get value() { return this._value; }
        set value(v) {
            const value=String(v ?? '');
            this._value=this.tagName==='SELECT' && !this.children.some(n=>n.value===value) ? '' : value;
        }
        append(...nodes) {
            for (const n of nodes) {
                if (n == null) continue;
                const child = typeof n === 'string' ? Object.assign(new Element('#text'),{_text:n}) : n;
                child.remove(); child.parentElement=this; this.children.push(child);
            }
        }
        insertBefore(node,before) {
            node.remove(); const at=before ? this.children.indexOf(before) : this.children.length;
            node.parentElement=this; this.children.splice(at < 0 ? this.children.length : at,0,node);
        }
        replaceChildren(...nodes) {
            for (const n of this.children) n.parentElement=null;
            this._text=''; this.children=[]; this.append(...nodes);
            if (this.tagName==='SELECT') this._value=this.children[0]?.value || '';
        }
        remove() {
            if (this.parentElement) { const p=this.parentElement; p.children=p.children.filter(n=>n!==this); this.parentElement=null; }
        }
        contains(node) { return node===this || this.children.some(child=>child.contains(node)); }
        setAttribute(k,v) { this.attrs.set(k,String(v)); if(k.startsWith('data-')) this.dataset[datasetKey(k.slice(5))]=String(v); }
        removeAttribute(k) { this.attrs.delete(k); }
        getAttribute(k) { return this.attrs.get(k) ?? null; }
        addEventListener(k,cb) { const list=this.listeners.get(k)||[]; list.push(cb); this.listeners.set(k,list); }
        effectiveDisabled() {
            for (let n=this; n; n=n.parentElement) if (n.disabled && (n===this || n.tagName==='FIELDSET')) return true;
            return false;
        }
        dispatch(type) { for(const cb of this.listeners.get(type)||[]) cb({type,target:this,preventDefault(){}}); }
        click() { if (!this.effectiveDisabled()) this.dispatch('click'); }
        change(value) { this.value=value; this.dispatch('change'); }
        focus() { doc.activeElement=this; }
        showModal() { this.open=true; }
        close() { this.open=false; }
        scrollIntoView() {}
        getBoundingClientRect() { return {top:0,bottom:0}; }
        matches(selector) {
            if (selector==='button:not(:disabled)') return this.tagName==='BUTTON'&&!this.effectiveDisabled();
            if (selector.startsWith('.')) return selector.slice(1).split('.').every(c=>this.classList.contains(c));
            const attr=/^\[data-([\w-]+)(?:="([^"]*)")?\]$/.exec(selector);
            if (attr) { const key=datasetKey(attr[1]); return key in this.dataset && (attr[2]===undefined||this.dataset[key]===attr[2]); }
            return this.tagName===selector.toUpperCase();
        }
        querySelectorAll(selector) { return this.children.flatMap(n=>[...(n.matches(selector)?[n]:[]),...n.querySelectorAll(selector)]); }
        querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
    }
    doc.createElement = tag => new Element(tag);
    doc.body = new Element('body'); doc.scrollingElement = doc.body; doc.activeElement=doc.body;
    doc.getElementById = id => [doc.body,...doc.body.querySelectorAll('h3')].find(n=>n.id===id) || null;
    return doc;
}

const route = (id='room-b',code='B23456',name='乙房间') => ({SourceProfileId:'room-a',SourceServerUrl:'https://fixture.example/',SourceRoomCode:'A12345',
    TargetProfileId:id,TargetServerUrl:'https://fixture.example/',TargetRoomCode:code,TargetName:name,RunAfterSwitch:false});
const completion = extra => ({Policy:0,SpecifiedTaskType:'group',SpecifiedTaskName:'',SpecifiedTaskPriority:0,SpecifiedStartMode:'default',
    SpecifiedStartTaskId:'',SpecifiedStartTaskName:'',SpecifiedConfigRevision:'',SpecifiedWorkflowId:'',RoomSwitch:null,...extra});
function editFixture(extra={}) {
    return {Revision:'initial-revision',HasPeerSnapshot:true,Choices:[route()],Snapshot:{
        Key:{ProfileId:'room-a',ServerUrl:'https://fixture.example/',RoomCode:'A12345',PlayerUid:target.uid},Name:'甲房间',BgiPath:'',
        Names:['任务甲','任务乙'],Types:['group','onedragon'],ExpectedPlayers:2,Schedule:'08:00',ScheduleRequiresConfirmation:true,ScheduleSuppressed:true,
        Completion:completion(),QuickCommands:{'一键传奇':'GROUP:任务甲','一键精英':''},...extra}};
}
const state = (Kind,Text,Detail='') => ({Kind,Text,Detail});
function snapshotFixture(current='room-a') {
    const card=(Id,Name,Identity)=>({Id,Name,Identity,SectionTitle:'',Current:Id===current,CanEdit:true,Status:state('none','无执行记录'),
        UpdatedText:'',Detail:'',Tasks:[],NextTasks:[],TasksCaption:'',ExpectedPlayers:'2 人',Schedule:'08:00（待确认）',Completion:'恢复任务',
        Edge:null,PreviousEdge:null,ProgressUnconfirmed:false,HasConfirmedProgress:false,ProgressText:'未绑定联机任务'});
    const a=card('room-a','甲房间','A12345'),b=card('room-b','乙房间','B23456');
    Object.assign(a,{Status:state('running','联机执行中'),UpdatedText:'11-26 08:03',TasksCaption:'本轮冻结任务',HasConfirmedProgress:true,ProgressText:'1 / 2 项已完成',
        Tasks:[{Number:1,Name:'任务甲',Type:'配置组',Status:state('done','已完成')},{Number:2,Name:'任务乙',Type:'一条龙',Status:state('running','进行中')}],
        NextTasks:[{Number:1,Name:'下轮不同任务',Type:'配置组',Status:state('none','无执行记录')}],
        Edge:{Relation:'甲房间 → 乙房间 · B23456',ContinuesToNext:true,SwitchStatus:state('waiting','等待旧批次结清'),PostAction:'切换成功后：待命',PostStatus:null,OnlineResult:'',Detail:''}});
    Object.assign(b,{PreviousEdge:{Relation:'最近接续：乙房间 → 甲房间 · A12345',ContinuesToNext:false,SwitchStatus:state('failed','切房失败'),
        PostAction:'切房后：待命',PostStatus:null,OnlineResult:'',Detail:'11-25 20:00 · 合成历史失败'}});
    return {PlayerUid:target.uid,InstanceId:target.instanceId,MachineId:target.machineId,ExecutorName:target.name,CapturedAtUtc:'2026-11-26T08:03:00+08:00',
        Presentation:{LoginText:current==='room-a'?'当前登录：甲房间 A12345':'当前登录：乙房间 B23456',LoginContext:'等待旧批次结清',
            CurrentStatus:state('waiting','等待旧批次结清'),Cards:[a,b]}};
}
function camel(value) {
    if(Array.isArray(value)) return value.map(camel);
    if(value&&typeof value==='object') return Object.fromEntries(Object.entries(value).map(([k,v])=>[k[0].toLowerCase()+k.slice(1),camel(v)]));
    return value;
}

function setup(t, options={}) {
    const doc=documentDouble(),host=doc.createElement('div');doc.body.append(host);
    const calls=[], confirms=[], notices=[], timers=new Map();let serial=0, random=0, live=true;
    const fixture={edit:options.edit || editFixture(),snapshot:options.snapshot || snapshotFixture(),
        names:options.names===undefined?{Groups:['任务甲','下轮不同任务'],OneClicks:['任务乙']}:options.names,
        conflict:options.conflict || null, catalogRevision:'catalog-r1', lookup:null};
    const ok=data=>({status:'ok',targetInstanceId:target.instanceId,data:clone(data)});
    function apply(changes) {
        const s=fixture.edit.Snapshot;
        if('ExpectedPlayers' in changes)s.ExpectedPlayers=changes.ExpectedPlayers;
        if('Schedule' in changes){s.Schedule=changes.Schedule;s.ScheduleRequiresConfirmation=false;s.ScheduleSuppressed=false;}
        if('Bindings' in changes){s.Names=changes.Bindings.map(b=>b.Name);s.Types=changes.Bindings.map(b=>b.Type);}
        if('Completion' in changes)s.Completion=clone(changes.Completion);
        if('QuickCommands' in changes)Object.assign(s.QuickCommands,changes.QuickCommands);
        fixture.edit.Revision='saved-revision';
    }
    const adapter={target:clone(target),ready:()=>live,operationId:(_operation,id)=>prefix+id,
        onMessage:(...args)=>notices.push(args),
        async read(operation,payload,opts) {
            calls.push({kind:'read',operation,payload:clone(payload),opts});
            if(operation==='room.orchestration.get')return ok(options.camel ? camel(fixture.snapshot) : fixture.snapshot);
            if(operation==='room.orchestration.edit')return ok(options.camel ? camel(fixture.edit) : fixture.edit);
            if(operation==='room.orchestration.names')return ok(fixture.names);
            if(operation==='room.orchestration.layout')return ok(fixture.conflict);
            if(operation==='operation.get')return fixture.lookup || {status:'unknown',operationId:payload};
            if(operation==='room.orchestration.catalog'){
                const nodes=[{taskId:'node-off',name:'跳过',enabled:false},{taskId:'node-on',name:'起点',enabled:true}];
                const workflows=[{workflowId:'plan-01',name:'隔离计划表',revision:fixture.catalogRevision,nodes}];
                const selected=payload.Type==='workflow'?payload.WorkflowId==='plan-01':!!payload.Name;
                return ok({workflows,target:selected?{type:payload.Type,name:payload.Type==='workflow'?'隔离计划表':payload.Name,
                    workflowId:payload.Type==='workflow'?'plan-01':'',revision:fixture.catalogRevision,nodes}:null});
            }
            throw new Error('unexpected read '+operation);
        },
        async operate(operation,payload,opts) {
            calls.push({kind:'operate',operation,payload:clone(payload),opts});
            if(options.operate)return options.operate(operation,payload,opts,{fixture,apply,ok});
            assert.equal(operation,'room.orchestration.save');apply(payload.Changes);
            return {...ok(fixture.snapshot),operationId:prefix+opts.requestId};
        }};
    const context=vm.createContext({document:doc,URL,Uint8Array,console,crypto:{getRandomValues(b){b.fill(++random);return b;}},
        setInterval(fn){const id=++serial;timers.set(id,fn);return id;},clearInterval(id){timers.delete(id);},
        confirm(message){confirms.push(message);return options.confirm ? options.confirm(message) : true;},innerHeight:800,getComputedStyle:()=>({overflowY:'visible'})});
    context.window=context;vm.runInContext(source,context,{filename:'control-room-orchestration.js'});
    const api=context.roomOrchestration.mount(host,adapter);t.after(()=>{api.dispose();assert.equal(timers.size,0);});
    const selector=(s,root=host)=>{const n=root.querySelector(s);assert.ok(n,'missing '+s);return n;};
    const button=(label,root=host)=>{const n=root.querySelectorAll('button').find(n=>n.textContent===label);assert.ok(n,'missing button '+label);return n;};
    return {api,host,fixture,calls,confirms,notices,selector,button,adapter,apply,ok,
        online:value=>{live=value;},
        operations:()=>calls.filter(c=>c.kind==='operate'),
        async open(){await api.refresh();selector('[data-ro-edit="room-a"]').click();await flush();return selector('dialog');},
        field:role=>selector(`[data-ro-field="${role}"]`,selector('dialog')),
        async choose(role,value){selector(`[data-ro-field="${role}"]`,selector('dialog')).change(value);await flush();},
        async save(){button('保存配置',selector('dialog')).click();await flush();}};
}

test('Pascal/camel 投影只展示真实状态；断线旧快照全部待核对',async t=>{
    const x=setup(t,{camel:true});await x.api.refresh();
    assert.equal(x.host.querySelectorAll('.ro-login-bar').length,1);
    assert.equal(x.host.querySelectorAll('.ro-card').length,2);
    assert.match(x.host.textContent,/本轮冻结任务.*任务甲.*已完成/);
    assert.match(x.host.textContent,/下轮任务绑定.*下轮不同任务/);
    assert.match(x.host.textContent,/最近接续记录.*切房失败/);
    assert.equal(x.operations().length,0);
    x.online(false);await x.api.refresh();
    assert.match(x.host.textContent,/当前登录：待核对/);
    assert.equal(x.host.querySelectorAll('.ro-status-running').length,0);
    assert.equal(x.host.querySelectorAll('.ro-status-done').length,0);
    assert.equal(x.host.querySelectorAll('.ro-status-failed').length,0);
    assert.ok(x.host.querySelectorAll('[data-ro-edit]').every(b=>b.disabled));
});

test('目录不可用和原失效切房设置不阻止人数差量保存，保留原定时和快捷指令',async t=>{
    const legacy=editFixture({Schedule:' 08:00 ',Completion:completion({Policy:1,RoomSwitch:route('deleted','OLD','已删除房间')}),QuickCommands:{'原值':null}});
    const x=setup(t,{edit:legacy,names:null});await x.open();
    x.field('people').value='3';await x.save();
    assert.equal(x.operations().length,1);
    const save=x.operations()[0];assert.equal(save.operation,'room.orchestration.save');
    assert.deepEqual(save.payload,{ProfileId:'room-a',Changes:{ExpectedPlayers:3},AcceptedLayoutConflict:null});
    assert.equal(save.opts.expectedRevision,'initial-revision');
    assert.match(save.opts.requestId,/^[a-f0-9]{32}$/);
    assert.equal(x.fixture.edit.Snapshot.Schedule,' 08:00 ');
    assert.equal(x.fixture.edit.Snapshot.ScheduleSuppressed,true);
    assert.equal(x.fixture.edit.Snapshot.Completion.RoomSwitch.TargetProfileId,'deleted');
    assert.equal(x.fixture.edit.Snapshot.QuickCommands['原值'],null);
    assert.equal(x.host.querySelector('dialog'),null);
    assert.ok(x.notices.some(([message])=>/已确认保存并重新读取/.test(message)));
    assert.ok(x.calls.filter(c=>c.operation==='room.orchestration.names'||c.operation==='room.orchestration.catalog').every(c=>c.payload.ExplicitRefresh===false));
});

test('切房目标空占位符不隐式选择第一个，有效关系才保存',async t=>{
    const x=setup(t);await x.open();
    await x.choose('action','switchRoom');await x.choose('targetRoom','0');await x.choose('targetRoom','');
    assert.equal(x.field('targetRoom').value,'');await x.save();
    assert.equal(x.operations().length,0);assert.match(x.selector('.ro-editor-message').textContent,/有效目标房间/);
    await x.choose('targetRoom','0');await x.save();
    assert.equal(x.operations().length,1);
    const c=x.operations()[0].payload.Changes.Completion;
    assert.equal(c.Policy,1);assert.deepEqual(c.RoomSwitch,route());assert.equal(c.SpecifiedTaskName,'');
});

test('实际登录移动使已打开的表单失效，不发送写入',async t=>{
    const x=setup(t);await x.open();x.field('people').value='4';
    x.fixture.snapshot=snapshotFixture('room-b');await x.api.refresh();await flush();
    assert.equal(x.button('保存配置',x.selector('dialog')).disabled,true);
    assert.match(x.selector('.ro-editor-message').textContent,/实际登录房间/);
    await x.save();assert.equal(x.operations().length,0);
});

test('原编辑修订已变化时禁止覆盖，即使只是修改人数',async t=>{
    const x=setup(t);await x.open();x.field('people').value='3';
    x.fixture.edit.Revision='other-writer';await x.api.refresh();await flush();
    assert.equal(x.button('保存配置',x.selector('dialog')).disabled,true);
    assert.match(x.selector('.ro-editor-message').textContent,/配置或原修订已变化/);
    assert.equal(x.operations().length,0);
});

test('目标档案改址不会改用同 ID 新去向，源档案修订相同也不提交',async t=>{
    const x=setup(t);await x.open();await x.choose('action','switchRoom');await x.choose('targetRoom','0');
    x.fixture.edit.Choices=[route('room-b','NEW123','乙房间')];await x.save();
    assert.equal(x.operations().length,0);
    assert.match(x.selector('.ro-editor-message').textContent,/切房目标已改址/);
    assert.equal(x.button('保存配置',x.selector('dialog')).disabled,true);
});

test('明确 blocked 的旧回执不认领成这次新未知操作',async t=>{
    const x=setup(t,{operate:()=>({status:'blocked',operationId:prefix+'old',message:'上次操作尚未确认，本次未发送。'})});
    await x.open();x.field('people').value='3';await x.save();
    assert.equal(x.operations().length,1);
    assert.equal(x.selector('.ro-editor-pending').hidden,true);
    assert.match(x.selector('.ro-editor-message').textContent,/本次未发送/);
    assert.ok(!x.calls.some(c=>c.operation==='operation.get'));
});

test('未知保存只按本次编号核对，正常快照或错编号不冒充成功',async t=>{
    const x=setup(t,{operate:(op,p,opts,env)=>{env.apply(p.Changes);return {status:'unknown',operationId:prefix+opts.requestId,message:'原终态丢失'};}});
    await x.open();x.field('people').value='4';await x.save();
    assert.equal(x.selector('.ro-editor-pending').hidden,false);
    assert.match(x.selector('.ro-editor-pending').textContent,/待核对.*不会自动重发/);
    assert.ok(!x.notices.some(([m])=>/已确认保存/.test(m)));
    await x.api.refresh();assert.ok(x.selector('dialog').open);
    const id=prefix+x.operations()[0].opts.requestId;
    x.fixture.lookup={...x.ok(x.fixture.snapshot),operationId:prefix+'other'};
    x.button('核对原操作',x.selector('dialog')).click();await flush();
    assert.ok(x.selector('dialog').open);assert.match(x.selector('.ro-editor-pending').textContent,/本次操作编号/);
    x.fixture.lookup={...x.ok(x.fixture.snapshot),operationId:id};
    x.button('核对原操作',x.selector('dialog')).click();await flush();
    assert.equal(x.host.querySelector('dialog'),null);
    assert.equal(x.operations().length,1);
    assert.deepEqual(x.calls.filter(c=>c.operation==='operation.get').map(c=>c.payload),[id,id]);
    assert.ok(x.notices.some(([m])=>/已确认保存并重新读取/.test(m)));
});

test('回执 ok 仍须回读本次差量，配置不一致不显示成功',async t=>{
    const x=setup(t,{operate:(op,p,opts,env)=>({...env.ok(env.fixture.snapshot),operationId:prefix+opts.requestId})});
    await x.open();x.field('people').value='4';await x.save();
    assert.equal(x.operations().length,1);
    assert.equal(x.fixture.edit.Snapshot.ExpectedPlayers,2);
    assert.ok(x.selector('dialog').open);assert.equal(x.button('保存配置',x.selector('dialog')).disabled,true);
    assert.match(x.selector('.ro-editor-message').textContent,/读回内容与提交不一致/);
    assert.ok(!x.notices.some(([m])=>/已确认保存并重新读取/.test(m)));
});

test('布局冲突需明确确认原文本才保存绑定顺序',async t=>{
    let accept=false;const conflict='成员乙当前布局不一致';
    const x=setup(t,{conflict,confirm:()=>accept});await x.open();
    const down=x.selector('.ro-binding-list').querySelectorAll('button').find(b=>b.dataset.roBindingAction==='down'&&!b.disabled);assert.ok(down);down.click();
    await x.save();assert.equal(x.operations().length,0);assert.match(x.confirms[0],/成员乙当前布局不一致/);
    accept=true;await x.save();assert.equal(x.operations().length,1);
    const p=x.operations()[0].payload;
    assert.equal(p.AcceptedLayoutConflict,conflict);
    assert.deepEqual(p.Changes.Bindings,[{Name:'任务乙',Type:'onedragon'},{Name:'任务甲',Type:'group'}]);
});

test('只有显式目录刷新可启动准备过程，取消针对原持久编号',async t=>{
    let finish;const x=setup(t,{operate:(op,p,opts)=>{
        if(op==='room.orchestration.names')return new Promise(resolve=>{finish=resolve;});
        if(op==='room.orchestration.cancel')return {status:'ok',message:'已请求取消目录刷新'};
        throw new Error('unexpected '+op);
    }});await x.open();assert.equal(x.operations().length,0);
    x.button('刷新任务目录',x.selector('dialog')).click();await flush();
    const refresh=x.operations()[0],id=prefix+refresh.opts.requestId;
    assert.deepEqual(refresh.payload,{ProfileId:'room-a',Type:'group',Name:'',WorkflowId:'',ExplicitRefresh:true});
    assert.match(x.confirms[0],/可能启动.*BGI/);
    x.button('取消目录刷新',x.selector('dialog')).click();await flush();
    const cancel=x.operations()[1];assert.equal(cancel.operation,'room.orchestration.cancel');assert.equal(cancel.payload,id);
    assert.equal(cancel.opts.allowWhilePending,true);assert.match(x.selector('.ro-editor-pending').textContent,/已请求取消.*等待原操作终态/);
    finish({status:'rejected',operationId:id,message:'目录刷新已取消'});await flush();
    assert.equal(x.selector('.ro-editor-pending').hidden,true);assert.equal(x.operations().length,2);
});

test('所选目标目录修订改变后不可静默采用，明确重新确认起点才能保存',async t=>{
    const x=setup(t);await x.open();await x.choose('action','runSpecified');await x.choose('taskType','group');await x.choose('taskName','任务甲');
    await x.choose('startMode','beginning');x.fixture.catalogRevision='catalog-r2';await x.save();
    assert.equal(x.operations().length,0);
    assert.match(x.selector('.ro-editor-message').textContent,/目录或修订尚未确认/);
    x.button('确认当前起点',x.selector('dialog')).click();await flush();await x.save();
    assert.equal(x.operations().length,1);
    assert.equal(x.operations()[0].payload.Changes.Completion.SpecifiedConfigRevision,'catalog-r2');
});

test('切房后计划表使用真实稳定节点和目录修订，只有启用节点可选',async t=>{
    const x=setup(t);await x.open();await x.choose('action','switchRoom');await x.choose('targetRoom','0');
    await x.choose('afterSwitch','specified');await x.choose('taskType','workflow');await x.choose('taskName','plan-01');
    await x.choose('startMode','node');
    assert.ok(!x.field('startNode').children.some(n=>n.value==='node-off'));
    await x.choose('startNode','node-on');x.field('priority').value='-7';x.field('priority').dispatch('input');
    await x.save();assert.equal(x.operations().length,1);
    const c=x.operations()[0].payload.Changes.Completion;
    assert.equal(c.Policy,1);assert.equal(c.RoomSwitch.RunAfterSwitch,true);assert.equal(c.SpecifiedTaskType,'workflow');
    assert.equal(c.SpecifiedWorkflowId,'plan-01');assert.equal(c.SpecifiedTaskName,'隔离计划表');assert.equal(c.SpecifiedStartMode,'node');
    assert.equal(c.SpecifiedStartTaskId,'node-on');assert.equal(c.SpecifiedConfigRevision,'catalog-r1');assert.equal(c.SpecifiedTaskPriority,-7);
    assert.ok(x.calls.filter(c=>c.operation==='room.orchestration.catalog').every(c=>c.kind==='read'&&c.payload.ExplicitRefresh===false));
});
