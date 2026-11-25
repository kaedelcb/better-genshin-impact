const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const root = path.resolve(__dirname, '../../BgiCoordinatorServer/wwwroot');
function setup() {
    const notices = [], target = { key:'local', instanceId:'test-instance', machineId:'test-machine' };
    let id = 0;
    const ctx = vm.createContext({ console, Map, Set, clearTimeout, setTimeout,
        mt:{ target, identity:{ isExecutor:true, capabilities:['roomOrchestrationV1'] }, tab:'taskcenter' },
        mtEl:()=>null, mtNewId:()=>String(++id), mte:s=>String(s), mtPick:(obj,k)=>obj?.[k] ?? obj?.[k[0].toUpperCase()+k.slice(1)],
        mtToast:(...x)=>notices.push(x), mtRead:async()=>({data:{Presentation:{Cards:[{Id:'a',Name:'甲房',Identity:'ABC123'}]}}}) });
    for (const file of ['mistletoe-startup.js','mistletoe-composition.js']) vm.runInContext(fs.readFileSync(path.join(root,file),'utf8'),ctx);
    const run = script=>vm.runInContext(script,ctx);
    run('su.config={steps:[]}; mtEdited=()=>{}');
    return { ctx, notices, run };
}
test('房间切换从本人执行端确认的档案选取，旧目标失效不替换',async()=>{
    const x=setup(); await x.run('mtLoadRoomSwitchChoices()');
    assert.equal(x.run("mtRoomSwitchSummary({roomProfileId:'a'})"),'等待 甲房 · ABC123 就绪');
    assert.match(x.run("mtRoomSwitchSummary({roomProfileId:'old'})"),/尚未核对/);
    assert.equal(x.run("mtRoomSwitchOptions('old').at(-1)[0]"),'old');
    x.run("mtAddStep('switchAssistantRoom',su.config.steps,'主流程')");
    assert.equal(x.run('su.config.steps.length'),1);
    assert.equal(x.run('su.config.steps[0].roomProfileId'),'');
    x.run("su.config.steps[0].roomProfileId='a'");
    assert.equal(x.run('mtBaseSummary(su.config.steps[0])'),'等待 甲房 · ABC123 就绪');
});
test('启动中心拒绝后台添加或搬入房间切换节点',()=>{
    const x=setup();
    x.run("mtAddStep('switchAssistantRoom',su.config.steps,'主流程');su.config.steps.push(mtNewStep('timerTrigger'));const fire=su.config.steps[1].fireSteps;mtAddStep('switchAssistantRoom',fire,'触发执行');mtMoveStepTo(su.config.steps[0].id,fire,0,'触发执行')");
    assert.equal(x.run('su.config.steps.length'),2);
    assert.equal(x.run('fire.length'),0);
    assert.ok(x.notices.every(n=>n[1]===true));
});
test('任务中心房间切换只能在普通流程的已选房间节点，不能放入后台或车道判定',()=>{
    const x=setup();
    x.run("const s=tcNewFlowStep('switchAssistantRoom');s.roomProfileId='a';const f={name:'接力',steps:[s]};tcFlowValidate(f,[],[])");
    assert.throws(()=>x.run("s.roomProfileId='';tcFlowValidate(f,[],[])"),/请选择目标房间/);
    assert.throws(()=>x.run("s.roomProfileId='a';f.exitStepId='bad';tcFlowValidate(f,[],[])"),/车道选择流程/);
    assert.throws(()=>x.run("delete f.exitStepId;const t=tcNewFlowStep('timerTrigger');t.fireSteps=[s];f.steps=[t];tcFlowValidate(f,[],[])"),/后台子链/);
});
