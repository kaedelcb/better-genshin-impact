const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../../BgiCoordinatorServer/wwwroot');
const mock = `
window.gatewayCalls=[];window.noAck=false;window.rejectJoin=false;window.failPolicy=false;
window.testMembers=['a','b','off'].map((uid,i)=>({playerUid:uid,playerName:['成员甲','成员乙','离线成员'][i],online:i<2,
 bgiStatus:'running',bgiVersion:'test',taskStatusAvailable:true,taskRunning:i===0,currentTaskName:i===0?'当前任务':'',currentRouteDisplay:'第 2 条线路',
 configGroups:['精英','特殊<任务>"名称'],oneClickConfigs:['收尾'],configGroupTasks:{'精英':['任务甲','任务乙']},
 configGroupTasksWithStatus:{'精英':[{name:'任务甲',status:'Enabled'},{name:'任务乙',status:'Enabled'}]},
 oneClickTasks:{'收尾':['领取','收尾']},oneClickTasksWithStatus:{'收尾':[{name:'领取',enabled:true,taskId:'guid-1'},{name:'收尾',enabled:true,taskId:'guid-2'}]},
 hotkeys:[{configName:'自动对话',functionName:'自动对话',hotkeyText:'F'}],onlineHoeingGroupNames:['精英'],onlineHoeingGroupTypes:['group'],
 quickCommands:{'一键精英':'GROUP:精英'},scheduledOnlineTime:'21:30',onlineHistory:[{mode:'command',onlineTime:'20:00',consumeTime:'20:01'}],
 hoeingCompletionAction:{policy:'resume',taskType:'group',taskName:'',priority:0}}));
const handlers={};
window.emit=(name,payload)=>handlers.evt({name,payload});
window.receipt=(uid,status='success',commandId=null)=>{
 const cmd=gatewayCalls.filter(x=>x.name==='control.sendCommand').at(-1).payload.command;
 emit('control.remoteCommand',{command:{cmd:'ack',commandId:commandId||cmd.commandId,senderUid:uid,params:{status,message:status==='success'?'执行端已完成':'执行端拒绝'}}});
};
window.transport={on(name,fn){handlers[name]=fn},onreconnecting(fn){this.reconnecting=fn},onreconnected(fn){this.reconnected=fn},onclose(fn){this.closed=fn},start:async()=>{},stop:async()=>{},invoke:async(method,env)=>{
 gatewayCalls.push(env);
 if(env.name==='session.hello')return {payload:{capabilities:['hoeing.start.v2','hoeing.completionChoice.v1','hoeing.quickCommandCompletion.v1']}};
 if(env.name==='control.joinRoom'){if(rejectJoin)emit('control.joinRejected',{reason:'密码错误'});else emit('control.playersUpdated',{full:true,players:testMembers});return {payload:{ack:true}}}
 if(env.name==='control.batch.current')return {payload:null};
 if(env.name==='control.batch.reset')return {payload:{resetId:env.payload.resetId,found:false,generation:0}};
 if(env.name==='control.batch.begin')return {payload:{protocol:'hoeing.start.v2',batchId:'batch-1',phase:'registering',requestId:env.payload.requestId}};
 if(env.name==='control.sendCommand'){
  const cmd=env.payload.command;
  if(noAck)return {payload:{ack:true}};
  queueMicrotask(()=>{
   if(cmd.cmd==='task_policy.pull'||cmd.cmd==='task_policy.push'){
    emit('control.remoteCommand',{command:{cmd:cmd.cmd==='task_policy.pull'?'task_policy.data':'task_policy.push_result',commandId:cmd.commandId,senderUid:cmd.target[0],params:{ok:failPolicy?'false':'true',policy:'resume',specifiedType:'group',specifiedName:'',specifiedPriority:'0',message:failPolicy?'读取失败':'已应用'}}});
   }else if(cmd.cmd==='set_task_enabled'){
    emit('control.remoteCommandResult',{cmd:cmd.cmd,commandId:cmd.commandId,targetUid:cmd.target[0],status:'success',configRevision:'rev-2',targetProcessId:55,targetStartTicksUtc:'638937012345678901'});
   }else for(const uid of cmd.target)receipt(uid,'success',cmd.commandId);
  });
 }
 return {payload:{ack:true}};
}};
window.signalR={HubConnectionBuilder:class{withUrl(){return this}withAutomaticReconnect(){return this}build(){return transport}}};
`;
async function fixture() {
    const server = http.createServer((req, res) => {
        const file = decodeURIComponent(new URL(req.url, 'http://localhost').pathname).slice(1) || 'index.html';
        if (!/^(index\.html|control-room(?:-commands|-ui)?\.(js|css)|dodoco-(?:web\.(js|css)|log-worker\.js|operations\.js|remote-logs\.js)|mistletoe-(?:core|startup|startup-view|taskcenter|web)\.js|mistletoe-web\.css|signalr\.min\.js|gengdi-avatars\/[A-Za-z]+\.png)$/.test(file)) { res.writeHead(404); res.end(); return; }
        res.setHeader('Content-Type', file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : file.endsWith('.png') ? 'image/png' : 'text/html');
        res.end(file === 'signalr.min.js' ? fs.readFileSync(path.join(root, file), 'utf8') + '\n;' + mock : fs.readFileSync(path.join(root, file)));
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const browser = await chromium.launch({ channel: 'msedge', headless: true });
    const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
    const errors = []; page.on('pageerror', err => errors.push(err.message));
    await page.goto(`http://127.0.0.1:${server.address().port}/`);
    const login = async () => {
        await page.locator('#roomCode').fill('isolated'); await page.locator('#password').fill('test');
        await page.getByRole('button', { name: '进入控制房间' }).click();
        await page.locator('#controlPanel').waitFor({ state: 'visible', timeout: 5000 });
    };
    return { page, errors, login, close: async () => { await browser.close(); await new Promise(resolve => server.close(resolve)); } };
}
module.exports = { fixture };
