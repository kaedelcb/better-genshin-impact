const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function setup() {
    const groups = {}, nodes = {};
    const host = {
        innerHTML:'', className:'',
        querySelector(s) { return nodes[s] ||= {scrollTop:0, value:'', open:false}; },
        querySelectorAll(s) {
            const attr = s.slice(1,-1), key = attr.slice(5);
            return groups[key] = [...this.innerHTML.matchAll(new RegExp(attr+'="(\\d+)"','g'))].map(m => ({dataset:{[key]:m[1]}}));
        }
    };
    const calls = [];
    const ctx = vm.createContext({ console, su:{triggers:[],unifiedTriggers:null,triggerFilter:'all',triggersExpanded:false},
        mtEl:() => host, mtPick:(o,k) => o?.[k], mte:s=>String(s||'').replace(/[<>&"]/g,c=>({'<':'&lt;','>':'&gt;','&':'&amp;','"':'&quot;'}[c])),
        mtCancelTrigger:(...args)=>calls.push(args), mtToast:()=>{}, mtFind:()=>null, mtSelect:()=>{},
        mt:{}, tc:{}, tcLoad:async()=>{},tcOpen:async()=>{},mtRenderHead:()=>{},mtRenderBody:()=>{}
    });
    vm.runInContext(fs.readFileSync(path.join(__dirname,'../../BgiCoordinatorServer/wwwroot/mistletoe-startup-view.js'),'utf8'),ctx);
    return {host,calls,ctx,run:s=>vm.runInContext(s,ctx),groups,nodes};
}
const items = [
    {runtimeId:'same',source:'startup',kind:'timer',text:'同名'},
    {runtimeId:'same',source:'taskCenter',kind:'log',text:'同名',runId:'run-1'},
    {runtimeId:'root:r',source:'taskCenter',kind:'planTimer',text:'计划定时',workflowId:'wf-a',isRootTimer:true,canCancel:false}
];
function fill(x) {
    x.ctx.items=structuredClone(items);
    x.run("su.unifiedTriggers={version:1,taskCenterAvailable:true,items};mtRenderTriggers()");
}

test('折叠卡统计两个来源，刷新保持展开选择且根定时明确管理计划',()=>{
    const x=setup();fill(x);
    assert.match(x.host.innerHTML,/启动中心 1 · 任务中心 2/);
    assert.ok(!x.host.innerHTML.includes('mt-trigger-card" open'));
    x.run('su.triggersExpanded=true;mtRenderTriggers()');
    assert.match(x.host.innerHTML,/mt-trigger-card" open/);
    assert.match(x.host.innerHTML,/class="mt-trigger-list"/);
    assert.match(x.host.innerHTML,/管理计划/);
    x.run("su.triggerFilter='taskCenter';mtRenderTriggers()");
    assert.equal((x.host.innerHTML.match(/class="mt-trigger"/g)||[]).length,2);
    assert.match(x.host.innerHTML,/启动中心 1 · 任务中心 2/);
});
test('同名相同RuntimeId的取消仍带真实来源与运行身份',()=>{
    const x=setup();fill(x);
    x.groups.cancel[0].onclick();x.groups.cancel[1].onclick();
    assert.equal(x.calls[0][2],'startup');
    assert.equal(x.calls[1][2],'taskCenter');
    assert.equal(x.calls[1][3],'run-1');
    assert.equal(x.calls[0][0],x.calls[1][0]);
});
test('旧端和断连不能冒充任务中心无挂载',()=>{
    const x=setup();
    x.run("su.triggers=[{runtimeId:'old',kind:'timer',text:'旧'}];mtRenderTriggers()");
    assert.match(x.host.innerHTML,/该端未提供/);
    x.run('su.triggerStale=true;mtRenderTriggers()');
    assert.match(x.host.innerHTML,/状态未知/);
    assert.match(x.host.innerHTML,/data-cancel="0" disabled/);
});
test('所属步骤已删除时定位明确提示查看挂载快照',async()=>{
    const x=setup(),notices=[];x.ctx.tcLocateFlowStep=async()=>false;
    x.ctx.mtToast=(message)=>notices.push(message);fill(x);
    x.run("su.unifiedTriggers.items[1].workflowId='wf-a';mtRenderTriggers()");
    await x.groups.locate[1].onclick();
    assert.match(notices[0],/不在当前计划.*挂载时流程/);
});
