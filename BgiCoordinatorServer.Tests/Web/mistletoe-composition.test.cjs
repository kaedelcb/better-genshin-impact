const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
function setup() {
    const host = {innerHTML:'',hidden:true,querySelectorAll:()=>[],querySelector:()=>({})};
    const notices=[],calls=[];let next=0;
    const ctx=vm.createContext({console,clearTimeout,setTimeout,Map,Set,mt:{target:{key:'local',instanceId:'inst'},identity:{}},
        MT_WATCHABLE:['bgiRunning','gameRunning','processRunning','bgiTaskRunning','bgiTaskName'],
        mtEl:()=>host,mtPick:(o,k)=>o?.[k],mtToast:(...x)=>notices.push(x),mtNewId:()=>String(++next),
        mtClone:structuredClone,mte:s=>String(s).replace(/[&<>\"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c])),
        mtOperate:async(...args)=>{calls.push(args);return {status:'ok',revision:'r2'};}
    });
    for(const file of ['mistletoe-taskcenter.js','mistletoe-composition.js']) vm.runInContext(fs.readFileSync(path.join(__dirname,'../../BgiCoordinatorServer/wwwroot',file),'utf8'),ctx);
    vm.runInContext('tcEdited=()=>{tc.dirty=true};tcRenderPlanHead=()=>{};',ctx);
    return {ctx,host,notices,calls,run:s=>vm.runInContext(s,ctx)};
}
const resource=(id,name)=>({nodeId:id,kind:'resource.configGroup',ref:{config:name},strategies:[]});
const condition={id:'check',kind:'weekday',nodeType:'condition',enabled:true,weekdays:[6,7],trueSteps:[],falseSteps:[]};
const plan={workflowId:'wf-test',name:'日常',future:{keep:true},nodes:[
    {nodeId:'decision',kind:'control.flow',flow:{name:'旧配对条件',steps:[condition],exitStepId:'check'}},
    {nodeId:'lanes',kind:'control.lanes',lanes:{name:'本日 <安排>',sourceFlowNodeId:'decision',yesName:'周末',noName:'工作日',yesNodeIds:['weekend'],noNodeIds:['weekday']}},
    resource('weekend','周末采集'),resource('weekday','工作日一条龙'),resource('tail','日常收尾')
]};
function load(x,doc=plan){x.ctx.plan=structuredClone(doc);x.run("tc.doc=plan;tc.selected='wf-test';tc.revision='r1';tc.list={flows:[{workflowId:'wf-test',name:'日常',unsupportedKinds:[]}]};");}
test('编排展示保留旧配对身份且每项车道任务仅展示一次，整计划可编辑',()=>{
    const x=setup();load(x);x.run('tcRenderNodes()');
    assert.match(x.host.innerHTML,/本日 &lt;安排&gt;/);
    assert.match(x.host.innerHTML,/周末/);assert.match(x.host.innerHTML,/工作日/);
    assert.equal((x.host.innerHTML.match(/周末采集/g)||[]).length,1);
    assert.equal((x.host.innerHTML.match(/工作日一条龙/g)||[]).length,1);
    assert.equal(x.run('!!tcCanEdit(tcFlow())'),true);
    assert.equal(x.run('JSON.stringify(tcMainNodes().map(n=>n.nodeId))'),'["lanes","tail"]');
});
test('复制旧配对车道时重建节点、出口及任务绑定身份，原结构保持',()=>{
    const x=setup();load(x);x.run('tcDuplicateComposition(tc.doc.nodes[1])');
    const doc=x.run('tc.doc'),copy=doc.nodes.filter(n=>!plan.nodes.some(p=>p.nodeId===n.nodeId));
    assert.equal(copy.length,4);
    const lane=copy.find(n=>n.lanes),flow=copy.find(n=>n.flow);
    assert.equal(lane.lanes.sourceFlowNodeId,flow.nodeId);
    assert.notEqual(flow.flow.exitStepId,'check');assert.equal(flow.flow.exitStepId,flow.flow.steps[0].id);
    assert.ok(copy.some(n=>n.nodeId===lane.lanes.yesNodeIds[0]));
    assert.equal(JSON.stringify(doc.nodes.filter(n=>plan.nodes.some(p=>p.nodeId===n.nodeId))),JSON.stringify(plan.nodes));
    assert.equal(doc.future.keep,true);
});
test('删除整组车道只移除所属条件及任务，普通复制保留自定义路径与未知字段',()=>{
    const x=setup();load(x);x.run('tcRemoveComposition(tc.doc.nodes[1])');
    assert.equal(x.run('JSON.stringify(tc.doc.nodes.map(n=>n.nodeId))'),'["tail"]');
    x.run("tc.doc.nodes[0].path={mode:'jump',targetNodeId:'end'};tc.doc.nodes[0].future=42;tcDuplicateComposition(tc.doc.nodes[0])");
    const copy=x.run('tc.doc.nodes[1]');assert.equal(copy.path.targetNodeId,'end');assert.equal(copy.future,42);
});
test('保存编排使用明确编辑版本、保留未知字段并更新修订',async()=>{
    const x=setup();load(x);x.run('tc.dirty=true');assert.equal(await x.run('tcSave()'),true);
    assert.equal(x.calls[0][2].flowEditVersion,1);
    assert.equal(x.calls[0][1].future.keep,true);assert.equal(x.run('tc.revision'),'r2');
});
test('后台触发链拒绝人工确认和任意程序，任务绑定必须指向存在任务',()=>{
    const x=setup();load(x);
    x.ctx.flow={name:'日志',steps:[{id:'log',kind:'logTrigger',nodeType:'action',triggerScope:'task',targetNodeId:'missing',logKeyword:'完成',fireSteps:[]}]};
    assert.throws(()=>x.run('tcFlowValidate(flow)'),/监听的任务/);
    x.run("flow.steps[0].targetNodeId='tail';flow.steps[0].fireSteps=[{id:'ask',kind:'manualConfirm',nodeType:'condition',confirmTimeoutSeconds:60}]");
    assert.throws(()=>x.run('tcFlowValidate(flow)'),/后台子链/);
    x.ctx.flow={name:'程序',steps:[{id:'program',kind:'startProgram',nodeType:'action',path:'allowed.exe',arguments:'evil',windowsUserName:'user'}]};
    assert.throws(()=>x.run("tcFlowValidate(flow,[],[{path:'allowed.exe',arguments:'safe',windowsUserName:'user'}])"),/已配置/);
});
test('深层复制更新四种子链和任务绑定，出口判断不能附加支链',()=>{
    const x=setup();load(x);x.ctx.flow={name:'绑定',exitStepId:'root',steps:[{...condition,id:'root',trueSteps:[{id:'child',beforeAttemptSteps:[{id:'before',targetNodeId:'old'}]}]}]};
    x.run("tcRenewSteps(flow,{old:'new'})");
    const flow=x.ctx.flow;assert.notEqual(flow.steps[0].trueSteps[0].beforeAttemptSteps[0].id,'before');
    assert.equal(flow.steps[0].trueSteps[0].beforeAttemptSteps[0].targetNodeId,'new');
    x.ctx.flow={name:'错误出口',exitStepId:'check',steps:[{...structuredClone(condition),trueSteps:[{id:'wait',kind:'wait',nodeType:'action',waitSeconds:1}]}]};
    assert.throws(()=>x.run('tcFlowValidate(flow)'),/出口/);
});

for(const inline of [false,true])test(`${inline?'独立':'旧配对'}车道定位嵌套步骤，内层应用只改草稿，最终保存保留身份`,()=>{
    const x=setup(),doc=structuredClone(plan);
    doc.nodes[0].flow.steps.unshift({id:'log',kind:'logTrigger',nodeType:'action',triggerScope:'round',logKeyword:'完成',fireSteps:[{id:'nested',kind:'wait',nodeType:'action',waitSeconds:2}]});
    if(inline){doc.nodes[1].lanes.decision=doc.nodes[0].flow;doc.nodes[1].lanes.sourceFlowNodeId='';doc.nodes.shift();}
    load(x,doc);
    function element(){const children=new Map();return {innerHTML:'',open:true,querySelector(s){if(!children.has(s))children.set(s,element());return children.get(s);},querySelectorAll:()=>[],closest(){return this;},setAttribute(){},close(){this.open=false;}};}
    const dialog=element(),calls=[];x.ctx.dialog=dialog;
    x.ctx.mtField=(_label,html)=>html;
    x.ctx.tcFlowDialogShell=()=>dialog;
    x.ctx.tcFlowEditor=(_node,id,options)=>calls.push({id,options});
    x.run("tcLanesEditor(tc.doc.nodes.find(n=>n.lanes),'nested')");
    assert.equal(calls[0].id,'nested');
    const before=calls[0].options.source;before.steps[0].fireSteps[0].waitSeconds=8;
    calls[0].options.onApply(before);
    const current=()=>x.run("tc.doc.nodes.find(n=>n.lanes).lanes.decision||tc.doc.nodes.find(n=>n.nodeId==='decision').flow");
    assert.equal(current().steps[0].fireSteps[0].waitSeconds,2,'内层应用不写入计划');
    dialog.querySelector('[data-apply]').onclick();
    assert.equal(current().steps[0].fireSteps[0].waitSeconds,8);
    assert.equal(x.run("tc.doc.nodes.find(n=>n.lanes).lanes.sourceFlowNodeId"),inline?'':'decision');
});

test('运行触发器定位把步骤身份传入所属车道，丢失步骤不会冒充定位成功',async()=>{
    const x=setup();load(x);x.ctx.mtRenderHead=()=>{};x.ctx.mtRenderBody=()=>{};
    x.run('tcRenderNodes=()=>{}');const calls=[];x.ctx.tcLanesEditor=(node,id)=>calls.push([node.nodeId,id]);
    assert.equal(await x.run("tcLocateFlowStep('wf-test','decision','check')"),true);
    assert.deepEqual(calls,[['lanes','check']]);
    assert.equal(await x.run("tcLocateFlowStep('wf-test','decision','missing')"),false);
    assert.equal(calls.length,1);
});


test('编排显式起点可选中间任务、两条车道内任务、车道和后续主表，失效及重复身份仍拒绝',()=>{
    const x=setup();load(x);
    for(const id of ['decision','lanes','weekend','weekday','tail']) {
        x.ctx.entry=id;assert.equal(x.run('tcCanUseEntry(entry)'),true,id);
    }
    for(const id of ['', ' ', 'missing', null]) {
        x.ctx.entry=id;assert.equal(x.run('tcCanUseEntry(entry)'),false,String(id));
    }
    x.run("tc.doc.nodes.push({...tc.doc.nodes[4]})");
    assert.equal(x.run("tcCanUseEntry('tail')"),false);
});

test('流程和车道更多菜单提供从此开始和标记，车道任务提供单任务且解释沿本路线继续',async()=>{
    const x=setup();load(x);const buttons=[],menus=[],subtitles=[];
    x.ctx.tcDialogShell=(_title,_subtitle,html)=>{
        menus.push(html);subtitles.push(_subtitle);buttons.length=0;
        for(const match of html.matchAll(/<button[^>]*data-a="([^"]+)"([^>]*)>/g)) buttons.push({dataset:{a:match[1]},disabled:/disabled/.test(match[2])});
        return {querySelectorAll:()=>buttons,close(){}};
    };
    const starts=[];x.ctx.tcStart=(...args)=>starts.push(args);
    for(const id of ['decision','lanes']) {
        x.ctx.entry=id;x.run('tcNodeMenu(tc.doc.nodes.find(n=>n.nodeId===entry))');
        assert.equal(subtitles.at(-1),id==='lanes'?'车道操作':'流程操作');
        assert.doesNotMatch(subtitles.at(-1),/第\s*0\s*项/);
        assert.ok(buttons.find(b=>b.dataset.a==='from'&&!b.disabled));
        assert.ok(buttons.find(b=>b.dataset.a==='mark'&&!b.disabled));
        assert.ok(!buttons.some(b=>['single','settings','ref','editor'].includes(b.dataset.a)));
        await buttons.find(b=>b.dataset.a==='from').onclick();
        assert.deepEqual(starts.at(-1),[id,null,true]);
    }
    x.run("tcNodeMenu(tc.doc.nodes.find(n=>n.nodeId==='weekday'))");
    assert.ok(buttons.find(b=>b.dataset.a==='single'&&!b.disabled));
    assert.match(menus.at(-1),/沿当前路线剩余任务继续，再回到主表/);
    await buttons.find(b=>b.dataset.a==='single').onclick();
    assert.deepEqual(starts.at(-1),['weekday','weekday']);
    x.run('tcRenderNodes()');assert.match(x.host.innerHTML,/data-action="more" data-id="lanes"/);
});

test('编排中途启动原样传递所选入口及单任务边界，不静默改成第一行',async()=>{
    const x=setup(),doc=structuredClone(plan);doc.nodes.splice(1,0,resource('middle','中间任务'));load(x,doc);
    x.run('tcFlush=async()=>true;tcLoad=async()=>{};mtStageBar=()=>{};');
    for(const [entry,stop] of [['middle',null],['lanes',null],['weekend',null],['weekday','weekday'],['tail',null]]) {
        x.ctx.entry=entry;x.ctx.stop=stop;await x.run('tcStart(entry,stop,true)');
        const [op,payload]=x.calls.at(-1);assert.equal(op,'task.run.start');
        assert.equal(payload.entryNodeId,entry);assert.equal(payload.stopAfterNodeId,stop);assert.equal(payload.expectedRevision,'r1');
    }
});

test('中间起点标记用于一次连续启动，失效起点保留且不发执行请求',async()=>{
    const x=setup();load(x);
    x.run('tcRenderNodes=()=>{};tcFlush=async()=>true;tcLoad=async()=>{};mtStageBar=()=>{};');
    await x.run("tcMark(tc.doc.nodes.find(n=>n.nodeId==='weekday'),true)");
    assert.equal(x.run('tcStartMarker()'),'weekday');
    await x.run('tcStart()');
    const start=x.calls.find(c=>c[0]==='task.run.start');assert.equal(start[1].entryNodeId,'weekday');assert.equal(start[1].stopAfterNodeId,null);
    assert.equal(x.run('tcStartMarker()'),null);
    x.run("tc.doc[TC_START_KEY]='missing';tc.consumed.clear()");
    const before=x.calls.length;await x.run('tcStart()');assert.equal(x.calls.length,before);
    assert.equal(x.run('tcStartMarker()'),'missing');assert.match(x.notices.at(-1)[0],/起点已失效/);
    x.run("tc.doc.nodes.push({...tc.doc.nodes[4]})");await x.run("tcStart('tail')");assert.equal(x.calls.length,before);
});
