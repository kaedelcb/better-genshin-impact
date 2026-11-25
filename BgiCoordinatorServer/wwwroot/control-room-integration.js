// 跨房浏览与本人执行端：不将查看列表写入当前控制房间的成员/批次状态。
let webMainServerUrl = '', webMainGeneration = 0, webMainOwnerView = null;
let webMainAllInstancePlayers = [], webMainSnapshotGeneration = -1, webMainSnapshotRevision = null;
function webDefaultRoomServer() {
    try { return window.bgiLocalBridge ? window.bgiLocalBridge.gatewayUrl().replace(/\/gateway\/?$/i,'') : location.origin; }
    catch { return ''; }
}
function webMainContext() {
    return {serverUrl:webMainServerUrl||webDefaultRoomServer(),roomCode,nickname:playerName,connection,ready:roomReady&&webMainSnapshotGeneration===webMainGeneration,
        players:webMainAllInstancePlayers,exactPlayers:webMainAllInstancePlayers,senderUid:webSessionUid,capabilities:serverCapabilities,generation:webMainGeneration};
}
function webOwnerTargets() {
    const own = mtTargets().filter(t=>t.local);
    const remote = (window.webRooms?.getKnownInstances()||[]).filter(x=>x.member.clientInstanceId&&x.member.machineId).map(({member:p,context,source})=>({
        key:'room-owner:'+source.roomKey+':'+p.playerUid+':'+p.clientInstanceId, uid:p.playerUid,instanceId:p.clientInstanceId,machineId:p.machineId,
        name:p.playerName||p.playerUid,online:p.online===true&&context.ready,local:false,roomLabel:context.name||context.roomCode,source,
        mine:!!mtCredentialFor({instanceId:p.clientInstanceId})}));
    const keys=new Map();
    for (const t of [...own,...remote]) {
        const key=JSON.stringify([t.uid,t.instanceId,t.machineId]); const old=keys.get(key);
        if(!old||!old.online&&t.online)keys.set(key,t);
        else if(old.online&&t.online&&!old.local&&!t.local&&old.source.roomKey!==t.source.roomKey)keys.set(key,{...old,online:false,ambiguous:true});
    }
    return [...keys.values()];
}
function webOwnerReady(t) { return !t.ambiguous && mtTargetReady(t); }
function webOwnerPreferenceKey() { return 'bgi.room.owner.target.v1:'+(webDefaultRoomServer()||'local'); }
function webOwnerPreference() { try{return localStorage.getItem(webOwnerPreferenceKey())||'';}catch{return '';} }
function webCloseOwnerView() {
    webMainOwnerView?.dispose?.();webMainOwnerView=null;
    const host=document.getElementById('roomOwnerView');if(host)host.hidden=true;
}
function webOpenOwnerView(mode) {
    webCloseOwnerView();
    const host=document.getElementById('roomOwnerView');if(!host)return;
    document.getElementById('homeView').hidden=true;document.getElementById('detailView').hidden=true;
    document.getElementById('selectionBar').hidden=true;host.hidden=false;
    webMainOwnerView=window.webRoomOwner.mount(host,{mode,targets:webOwnerTargets,ready:webOwnerReady,
        send:(target,op,payload,options)=>mtSend(op,payload,{...options,target}),initialTargetKey:webOwnerPreference(),
        onSelect:key=>{try{localStorage.setItem(webOwnerPreferenceKey(),key);}catch{}},onMessage:text=>{if(text)log(text);}});
}
function webRoomViewChanged(view) {
    if(view.mode==='orchestration'){
        if(!webMainOwnerView)webOpenOwnerView('orchestration');return;
    }
    webCloseOwnerView();
    if(document.getElementById('gengdiProduct')?.classList.contains('active'))showHome();
    renderControlRoom();
}
function webRoomSecondaryEvent(key,env,context) {
    if(env.name==='mistletoe.result')mtEvent(env,key);
    if(env.name==='control.remoteCommandResult') { consumeMemberResult(env.payload,key); consumeConfigApplied(env.payload,key); consumeStateResetResult(env.payload,key); }
    if(env.name==='control.remoteCommand'&&env.payload?.command){consumeMemberReply(env.payload.command,key);consumeStateResetAck(env.payload.command,key);}
}
function webRoomSecondaryDisconnected(key) {
    mtCancelRoomPending(key,'房间连接中断，结果待核对。');
    if(typeof interruptMemberRoomWaits==='function')interruptMemberRoomWaits(key);
    webMainOwnerView?.refresh?.();
}
function webRoomUpdateMain(update) {
    if(!update)return;
    const rev=update.revision ?? null;
    if(rev!==null&&(!Number.isSafeInteger(rev)||rev<0))return webInvalidateMainSnapshot();
    if(webMainSnapshotGeneration===webMainGeneration&&rev!==null&&webMainSnapshotRevision!==null&&rev<=webMainSnapshotRevision)return;
    if(update.full===true&&Array.isArray(update.players)){webMainAllInstancePlayers=update.players.slice();webMainSnapshotGeneration=webMainGeneration;webMainSnapshotRevision=rev;}
    else if(update.full===false&&webMainSnapshotGeneration===webMainGeneration&&(rev===null&&webMainSnapshotRevision===null||rev!==null&&update.baseRevision===webMainSnapshotRevision)) {
        if(update.changed!=null&&!Array.isArray(update.changed)||update.removed!=null&&!Array.isArray(update.removed))return webInvalidateMainSnapshot();
        const key=p=>JSON.stringify([p.playerUid,p.clientInstanceId||'',p.clientInstanceId?'':p.connectionId||'']);
        const by=new Map(webMainAllInstancePlayers.map(p=>[key(p),p]));
        const changed=update.changed||[];
        for(const uid of new Set(changed.map(p=>p.playerUid))){const existing=[...by.values()].filter(p=>p.playerUid===uid),incoming=changed.filter(p=>p.playerUid===uid);
            if(existing.length&&(existing.length!==incoming.length||existing.some(p=>!incoming.some(q=>key(p)===key(q)))))return webInvalidateMainSnapshot();}
        for(const p of changed)by.set(key(p),p);
        for(const uid of update.removed||[])for(const [k,p] of by)if(p.playerUid===uid)by.delete(k);
        webMainAllInstancePlayers=[...by.values()];webMainSnapshotRevision=rev;
    }else if(roomReady)webInvalidateMainSnapshot();
}
let webMainSnapshotRequest=null;
function webInvalidateMainSnapshot(){
    webMainSnapshotGeneration=-1;webMainAllInstancePlayers=[];
    window.webRooms?.mainChanged();
    if(webMainSnapshotRequest||!connection||!roomReady)return;
    const conn=connection,gen=webMainGeneration,code=roomCode;
    const job=conn.invoke('Query',makeEnvelope('query','control.roomSnapshot',{roomCode:code})).then(reply=>{
        checkGatewayError(reply);
        if(conn===connection&&gen===webMainGeneration&&reply?.payload?.success===true&&reply.payload.playersUpdate?.full===true)onPlayersUpdated(reply.payload.playersUpdate);
    }).catch(err=>log('控制房间成员快照未确认：'+err.message)).finally(()=>{if(webMainSnapshotRequest===job)webMainSnapshotRequest=null;});
    webMainSnapshotRequest=job;
}
function webRoomsInit() {
    if(!window.webRooms)return;
    window.webRooms.init({getMainContext:webMainContext,defaultServerUrl:webDefaultRoomServer,
        legacyServerUrl:()=>document.body.dataset.localBrowser==='true'?null:location.origin,
        loadLegacyCredentials:loadCreds,clearLegacyCredentials:clearCreds,
        onChange:()=>{if(window.webRooms.getView().mode==='orchestration'&&!webMainOwnerView&&document.getElementById('gengdiProduct')?.classList.contains('active')&&document.getElementById('controlPanel').style.display!=='none')webOpenOwnerView('orchestration');renderControlRoom();webMainOwnerView?.refresh?.();},onViewChange:webRoomViewChanged,
        onEvent:webRoomSecondaryEvent,onDisconnect:webRoomSecondaryDisconnected,
        onSwitchMain:creds=>webSwitchControlRoom(creds),onOrchestration:()=>{if(!webMainOwnerView)webOpenOwnerView('orchestration');},onOwnerProfiles:()=>webOpenOwnerView('profiles')});
    window.webRooms.mount();
    if(document.body.dataset.localBrowser==='true') {
        const legacy=loadCreds();if(legacy&&!legacy.serverUrl)document.getElementById('password').value='';
    }
    const remembered=window.webRooms.getLogin?.();
    if(remembered?.password&&!roomReady){
        document.getElementById('roomCode').value=remembered.roomCode;
        document.getElementById('password').value=remembered.password;
        document.getElementById('nickname').value=remembered.nickname||'';
        document.getElementById('rememberMe').checked=true;
        const form=document.getElementById('loginForm'),roomInput=document.getElementById('roomCode'),passwordInput=document.getElementById('password');
        let stillRemembered=true;
        roomInput.addEventListener('input',()=>{if(stillRemembered&&roomInput.value.trim()!==remembered.roomCode){stillRemembered=false;passwordInput.value='';}});
        form?.addEventListener('input',ev=>{if(ev.target.id==='localServerUrl'&&stillRemembered){stillRemembered=false;passwordInput.value='';}});
        if(form)form.onsubmit=ev=>{ev.preventDefault();joinRoom(stillRemembered?remembered.serverUrl:undefined);};
    }
}
// 主连接事务由控制页面原 joinRoom 负责；切换浏览器控制上下文不是执行端切房。
async function webSwitchControlRoom(creds) {
    if(joiningRoom)return;
    if(!creds?.roomCode||!creds?.password)throw new Error('请先填写目标房间凭据。');
    const old=connection;
    ++webMainGeneration;
    setConn(false);
    interruptStateResetWaits();interruptMemberWaits();mtCancelRoomPending(null,'原控制房间已切换，原操作结果待核对。');
    if(old)await old.stop();
    connection=null;roomReady=false;players=[];webMainAllInstancePlayers=[];latestControlBatch=null;selectedMemberUids.clear();selectionInitialized=false;
    webCloseOwnerView();
    document.getElementById('roomCode').value=creds.roomCode;
    document.getElementById('password').value=creds.password;
    document.getElementById('nickname').value=creds.nickname||'';
    document.getElementById('rememberMe').checked=!!creds.remember;
    document.getElementById('loginPanel').style.display='block';document.getElementById('controlPanel').style.display='none';
    if(document.getElementById('localServerUrl'))document.getElementById('localServerUrl').value=creds.serverUrl;
    return joinRoom(creds.serverUrl);
}
window.addEventListener('DOMContentLoaded',webRoomsInit,{once:true});
