import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const source=readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/dodoco-web.js',import.meta.url),'utf8');
// 新入口必须调用耕地机原选择器，不将配置名称变成桌面按键，也不能自动暂停任务。
function harness(){
 const calls=[],status={textContent:''};
 const context=vm.createContext({document:{getElementById:id=>id==='dodocoStatus'?status:{}},window:{},
  players:[{playerUid:'alice',playerName:'队友 A',online:true,hotkeys:[{configName:'SuspendHotkey',functionName:'暂停当前脚本/独立任务',hotkeyText:'D9'}]}],
  roomReady:true,escapeHtml:String,showHotkeySelect:(...args)=>calls.push(args)});
 vm.runInContext(source+'\nthis.api={dodoco,dodocoBgiHotkey};',context);
 Object.assign(context.api.dodoco,{active:true,tab:'screen',target:'alice'});
 return {context,api:context.api,calls,status};
}
test('监控页BGI快捷功能复用原选择器，与桌面输入授权和截图无关',()=>{
 const h=harness();h.api.dodocoBgiHotkey();assert.deepEqual(h.calls,[['alice','队友 A']]);
 assert.match(source, /id="dodocoBgiHotkey"/);
 assert.match(source, /d\('dodocoBgiHotkey'\)\.onclick=dodocoBgiHotkey/);
});
test('本机合成UID按UID、实例和机器三项映射到原服务器快捷键入口',()=>{
 const h=harness(),identity={uid:'alice',clientInstanceId:'instance-a',machineId:'machine-a'};
 Object.assign(h.context.players[0],identity);
 h.context.window.bgiLocalBridge={available:true,usesLocal:()=>true,localIdentity:()=>identity};
 h.api.dodoco.target='local_paired-browser';
 h.api.dodocoBgiHotkey();assert.deepEqual(h.calls,[['alice','队友 A']]);
 h.context.players[0].machineId='another-machine';h.api.dodocoBgiHotkey();assert.equal(h.calls.length,1);
 h.context.players[0].machineId='machine-a';h.context.players[0].clientInstanceId='another-instance';
 h.api.dodocoBgiHotkey();assert.equal(h.calls.length,1);
});

test('全员、离线、无快捷键或未入房时不调用选择器',()=>{
 const h=harness();h.api.dodoco.target='*';h.api.dodocoBgiHotkey();
 h.api.dodoco.target='alice';h.context.players[0].online=false;h.api.dodocoBgiHotkey();
 h.context.players[0].online=true;h.context.players[0].hotkeys=[];h.api.dodocoBgiHotkey();
 assert.match(h.status.textContent,/尚未上报/);
 h.context.roomReady=false;h.api.dodocoBgiHotkey();assert.equal(h.calls.length,0);
});
