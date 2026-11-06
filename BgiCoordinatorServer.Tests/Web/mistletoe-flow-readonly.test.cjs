const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function setup() {
    const host = { innerHTML:'', hidden:true };
    const notices = [];
    const ctx = vm.createContext({ console, clearTimeout, setTimeout, Map, Set,
        mtEl: () => host, mtPick: (o,k) => o?.[k], mtToast: (...x) => notices.push(x),
        mte: s => String(s).replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]))
    });
    vm.runInContext(fs.readFileSync(path.join(__dirname, '../../BgiCoordinatorServer/wwwroot/mistletoe-taskcenter.js'), 'utf8'), ctx);
    return {ctx,host,notices,run:s => vm.runInContext(s,ctx)};
}
const plan = {
    workflowId:'wf-test', nodes:[
        {nodeId:'j',kind:'control.flow',flow:{name:'周末 <分流>',steps:[{kind:'weekday'}],exitStepId:'w'}},
        {nodeId:'l',kind:'control.lanes',lanes:{name:'本日安排',sourceFlowNodeId:'j',yesName:'周末',noName:'工作日',yesNodeIds:['b'],noNodeIds:['c']}},
        {nodeId:'b',kind:'resource.configGroup',ref:{config:'周末采集'}},
        {nodeId:'c',kind:'resource.oneDragonConfig',ref:{config:'工作日一条龙'}},
        {nodeId:'d',kind:'resource.singleTask',ref:{config:'日常收尾',taskId:'finish'}}
    ]
};
function load(x) {
    x.ctx.plan = structuredClone(plan);
    x.run("tc.doc=plan;tc.selected='wf-test';tc.list={flows:[{workflowId:'wf-test',name:'日常',unsupportedKinds:[]}]};");
}
test('网页能看见通用流程与两条独立车道，避免把车道任务冒充主线', () => {
    const x = setup(); load(x); x.run('tcRenderNodes()');
    assert.match(x.host.innerHTML, /只读/);
    assert.match(x.host.innerHTML, /星期判断/);
    assert.match(x.host.innerHTML, /是 · 周末/);
    assert.match(x.host.innerHTML, /否 · 工作日/);
    assert.match(x.host.innerHTML, /周末 &lt;分流&gt;/);
    assert.equal((x.host.innerHTML.match(/周末采集/g)||[]).length, 1);
    assert.equal((x.host.innerHTML.match(/工作日一条龙/g)||[]).length, 1);
    assert.ok(!x.host.innerHTML.includes('data-enable'));
    assert.equal(x.run('!!tcCanEdit(tcFlow())'), false);
    assert.equal(x.run('!!tcCanStart(tcFlow())'), true);
});
test('空车道可直接汇流，新计划被只读页拒绝保存而不丢草稿', async () => {
    const x = setup(); load(x);
    x.run("tc.doc.nodes[1].lanes.noNodeIds=[];tc.dirty=true;tcRenderNodes()");
    assert.match(x.host.innerHTML, /空车道，直接汇流/);
    assert.equal(await x.run('tcSave()'), false);
    assert.equal(x.run('tc.dirty'), true);
    assert.match(x.notices.at(-1)[0], /网页未保存任何改动/);
});
test('普通旧计划继续使用原编辑入口', () => {
    const x = setup(); load(x);
    x.run("tc.doc.nodes=tc.doc.nodes.filter(n=>n.kind.startsWith('resource.'))");
    assert.equal(x.run('tcHasFlowRows()'), false);
    assert.equal(x.run('!!tcCanEdit(tcFlow())'), true);
});
