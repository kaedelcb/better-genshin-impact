const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function setup(local) {
    const calls = [];
    const ctx = vm.createContext({
        console, setTimeout, clearTimeout, Map, Set,
        document:{getElementById:()=>null}, localStorage:{getItem:()=>null,setItem(){},removeItem(){}},
        escapeHtml:String, newRequestId:()=>String(calls.length + 1),
        window:{bgiLocalBridge:{available:true, execute:async(op,args)=>{
            calls.push({op,request:args.request}); return {status:'ok',targetInstanceId:'inst'};
        }}},
        roomReady:true, roomCode:'room', players:[],
        makeEnvelope:(_type,_name,payload)=>({payload}), checkGatewayError:()=>{},
        connection:{invoke:async(_op,env)=>{
            calls.push({op:'remote',request:env.payload.request});
            ctx.replyId = env.payload.requestId;
            vm.runInContext("mtEvent({name:'mistletoe.result',payload:{requestId:replyId,reply:{status:'ok',targetInstanceId:'inst'}}})",ctx);
        }}
    });
    vm.runInContext(fs.readFileSync(path.join(__dirname,'../../BgiCoordinatorServer/wwwroot/mistletoe-core.js'),'utf8'),ctx);
    vm.runInContext(`mt.target={key:'target',instanceId:'inst',uid:'owner',local:${local}};mtTargetReady=()=>true;mtCredentialFor=()=>({browserId:'browser',secret:'secret'});`,ctx);
    return {calls,run:s=>vm.runInContext(s,ctx)};
}

for (const local of [true,false]) test(`${local?'本机':'远程'}保存计划携带独立编辑版本和修订`,async()=>{
    const x=setup(local);
    await x.run("mtSend('task.flow.save',{workflowId:'wf',nodes:[]},{expectedRevision:'r1',flowEditVersion:1})");
    const request=x.calls[0].request;
    assert.equal(request.flowEditVersion,1);
    assert.equal(request.expectedRevision,'r1');
    assert.equal(request.targetInstanceId,'inst');
    assert.equal(request.payload.workflowId,'wf');
    assert.equal(x.calls[0].op,local?'mistletoe.request':'remote');
    assert.equal(!!request.browserSecret,!local);
});

test('寄存器读取走只读桥，配置和归零均有持久回执保护',async()=>{
    const x=setup(true);
    await x.run("mtSend('registers.get')");
    assert.equal(x.calls[0].op,'mistletoe.read');
    for(const op of ['registers.configure','registers.reset']) {
        assert.equal(x.run(`mtDurable('${op}')`),true);
        await x.run(`mtSend('${op}',{number:1})`);
        assert.equal(x.calls.at(-1).op,'mistletoe.request');
    }
    assert.equal(x.run("mtDurable('task.trigger.cancel')"),false);
    assert.equal(x.run("mtDurable('task.prompt.answer')"),false);
});
