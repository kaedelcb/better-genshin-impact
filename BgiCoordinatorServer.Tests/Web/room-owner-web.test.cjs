const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');const vm=require('node:vm');const path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'../../BgiCoordinatorServer/wwwroot/control-room-owner.js'),'utf8');
function setup(send){
    const values=new Map();let ready=true,id=0;
    const context=vm.createContext({window:{},localStorage:{getItem:k=>values.get(k),setItem:(k,v)=>values.set(k,v)},escapeHtml:String,JSON,Date,Map,Set,setTimeout,
        mtDurable:op=>op==='room.profiles.save',mtRoomOperationKind:()=> 'profiles',newRequestId:()=>`r${++id}`});
    vm.runInContext(source,context);
    const target={uid:'user',instanceId:'instance',machineId:'machine'},identity={roomRequestPrefixes:{profiles:'profile-web-'}};
    const adapter=context.window.webRoomOwner.makeAdapter(target,identity,send,()=>ready);
    return {adapter,target,identity,context,values,disconnect:()=>{ready=false;}};
}
test('不明保存回执按原身份核对，不自动重发或覆盖其他编辑',async()=>{
    const calls=[];let result='unknown';
    const x=setup(async(t,operation,payload,options)=>{
        calls.push({t,operation,payload,options});
        if(operation==='operation.get')return {status:result,operationId:'profile-web-r1',message:'原回执'};
        throw Object.assign(new Error('连接中断'),{unconfirmed:true});
    });
    const a=await x.adapter.operate('room.profiles.save',{name:'甲房'},{expectedRevision:'old'});
    assert.equal(a.status,'unknown');assert.equal(a.operationId,'profile-web-r1');assert.equal(x.adapter.pending().id,a.operationId);
    const b=await x.adapter.operate('room.profiles.save',{name:'乙房'},{expectedRevision:'old'});
    assert.equal(b.status,'blocked');assert.equal(calls.filter(c=>c.operation==='room.profiles.save').length,1);
    result='ok';const checked=await x.adapter.reconcile();assert.equal(checked.status,'ok');assert.equal(x.adapter.pending(),undefined);
    assert.ok(calls.every(c=>c.t===x.target));
});
test('断线不发送写入，只有匹配的原操作终态解除待核对',async()=>{
    let calls=0;const x=setup(async()=>{calls++;return {status:'ok',operationId:'other-operation'};});
    const r=await x.adapter.operate('room.profiles.save',{name:'甲房'});
    assert.equal(r.status,'unknown');assert.ok(x.adapter.pending());
    const s=await x.adapter.reconcile();assert.equal(s.status,'unknown');assert.ok(x.adapter.pending());
    x.disconnect();assert.equal((await x.adapter.operate('room.profiles.save',{})).status,'rejected');assert.equal(calls,2);
});
