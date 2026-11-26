// 跨房浏览与本人执行端：不将查看列表写入当前控制房间的成员/批次状态。
let webMainServerUrl = '', webMainGeneration = 0, webMainOwnerView = null;
let webMainAllInstancePlayers = [], webMainSnapshotGeneration = -1, webMainSnapshotRevision = null;
let webFollowController = null, webFollowBanner = null, webConfirmedLogin = null, webMainDisconnecting = false;
const webFollowRetirements = new Map();
function webRememberMainLogin(conn, remember) { webConfirmedLogin = { conn, remember }; }
function webForgetMainLogin(conn) { if (webConfirmedLogin?.conn === conn) webConfirmedLogin = null; }
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
let webNamesTarget='', webNamesReadAt=0, webNamesRequest=null;
function webSyncOwnerRoomNames() {
    // 跟随邀请不是本人授权，更不能触发原房间档案查询或轮询。
    if (window.webRooms?.isFollowing?.()) return;
    // 仅使用已配对本机或已获本人授权的明确实例；多实例未选择时不猜测归属。
    const targets=webOwnerTargets().filter(t=>webOwnerReady(t)&&(t.local||mtCredentialFor(t)));
    const keyOf=t=>JSON.stringify([t.uid,t.instanceId,t.machineId]);
    const remembered=webOwnerPreference();
    const target=remembered?targets.find(t=>keyOf(t)===remembered):targets.length===1?targets[0]:null;
    const key=target?keyOf(target):'';
    if(key!==webNamesTarget){webNamesTarget=key;webNamesReadAt=0;window.webRooms.setOwnerRoomNames([]);}
    if(!target||webNamesRequest||Date.now()-webNamesReadAt<30000)return;
    webNamesReadAt=Date.now();
    const job=mtSend('room.profiles.get',undefined,{target}).then(reply=>{
        if(webNamesTarget!==key||!webOwnerReady(target)||!target.local&&!mtCredentialFor(target))return;
        window.webRooms.setOwnerRoomNames(reply.status==='ok'?(reply.data?.profiles??reply.data?.Profiles):[]);
    }).catch(()=>{if(webNamesTarget===key)window.webRooms.setOwnerRoomNames([]);}).finally(()=>{if(webNamesRequest===job)webNamesRequest=null;});
    webNamesRequest=job;
}
function webCloseOwnerView() {
    webMainOwnerView?.dispose?.();webMainOwnerView=null;
    const host=document.getElementById('roomOwnerView');if(host)host.hidden=true;
}
function webRenderFollowOrchestration() {
    webCloseOwnerView();
    const host=document.getElementById('roomOwnerView');if(!host)return;
    document.getElementById('homeView').hidden=true;document.getElementById('detailView').hidden=true;
    document.getElementById('selectionBar').hidden=true;host.hidden=false;
    const rooms = window.webRooms.listProfiles(), actual = window.webRooms.getMainContext(), escape = escapeHtml;
    // 邀请没有共享任务、进度、定时、接续动作或本人授权；绝不挂载普通 owner 查询器。
    host.innerHTML = `<div class="room-orchestration"><header class="ro-login-bar"><div class="ro-login-copy"><h2 class="ro-login-title">任务编排 · 跟随视图</h2><p>当前登录：${escape(actual?.ready ? actual.displayName + ' · ' + actual.roomCode : '待确认')}</p><p class="ro-muted">仅共享房间档案；任务配置、运行进度和切房关系尚未共享，不能确认。</p></div></header><div class="ro-timeline">${rooms.map(p => `<div class="ro-lane"><article class="ro-card${actual?.ready && p.roomKey === actual.roomKey ? ' ro-card-current' : ''}"><h3>${escape(p.displayName)}</h3><p class="ro-muted">${escape(p.roomCode || '房号未确认')} · ${escape(p.serverUrl || '服务器未确认')}</p><p class="ro-muted">${escape(p.note)}</p><p>预计 ${p.expectedHoeingPlayers} 人 · 队员 UID：${escape(p.teamUids?.join('、') || '未设置')}</p><p class="ro-hint">任务进度未共享</p></article></div>`).join('') || '<p class="ro-muted">尚未收到共享房间档案。</p>'}</div></div>`;
}
function webOpenOwnerView(mode) {
    if (window.webRooms?.isFollowing?.()) { if (mode === 'orchestration') webRenderFollowOrchestration(); return; }
    webCloseOwnerView();
    const host=document.getElementById('roomOwnerView');if(!host)return;
    document.getElementById('homeView').hidden=true;document.getElementById('detailView').hidden=true;
    document.getElementById('selectionBar').hidden=true;host.hidden=false;
    webMainOwnerView=window.webRoomOwner.mount(host,{mode,targets:webOwnerTargets,ready:webOwnerReady,
        send:async(target,op,payload,options)=>{
            const reply=await mtSend(op,payload,{...options,target});
            if(op==='room.profiles.get'&&reply.status==='ok'&&webOwnerReady(target)&&JSON.stringify([target.uid,target.instanceId,target.machineId])===webOwnerPreference())
                window.webRooms.setOwnerRoomNames(reply.data?.profiles??reply.data?.Profiles);
            return reply;
        },initialTargetKey:webOwnerPreference(),
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
function webFollowOriginal() {
    const value = id => document.getElementById(id)?.value || '';
    const current = !!connection && (roomReady || webConfirmedLogin?.conn === connection);
    const remembered = current && webConfirmedLogin?.conn === connection ? webConfirmedLogin.remember : document.getElementById('rememberMe').checked;
    return {
        login: current ? { serverUrl: webMainServerUrl || webDefaultRoomServer(), roomCode, password, nickname: playerName, remember: !!remembered } : null,
        unsaved: current && !remembered || !current && !!value('password') && !document.getElementById('rememberMe').checked,
        form: { roomCode: value('roomCode'), password: value('password'), nickname: value('nickname'),
            serverUrl: value('localServerUrl'), remember: document.getElementById('rememberMe').checked },
        loginDisplay: document.getElementById('loginPanel').style.display,
        controlDisplay: document.getElementById('controlPanel').style.display,
        roomInfo: document.getElementById('roomInfo').textContent,
        loginError: document.getElementById('loginError').textContent,
        selection: [...selectedMemberUids].map(uid=>window.webRooms.sourceFor(uid,mainMemberRoomKey())).filter(Boolean),
        selectionInitialized,
        product: document.getElementById('dodocoProduct')?.classList.contains('active') ? 'dodoco'
            : document.getElementById('mistletoeProduct')?.classList.contains('active') ? 'mistletoe' : 'gengdi',
        disabled: [...document.querySelectorAll('#dodocoProduct, #mistletoeProduct, .local-connect')].map(el => [el, el.disabled])
    };
}
function webFillLoginForm(creds) {
    document.getElementById('roomCode').value=creds.roomCode;
    document.getElementById('password').value=creds.password;
    document.getElementById('nickname').value=creds.nickname||'';
    document.getElementById('rememberMe').checked=!!creds.remember;
    if(document.getElementById('localServerUrl'))document.getElementById('localServerUrl').value=creds.serverUrl;
}
function webCloseFollowTransport(conn) {
    const existing = webFollowRetirements.get(conn);
    if (existing) return existing;
    let timer;
    const stopping = Promise.resolve().then(() => conn.stop()).then(() => { webFollowRetirements.delete(conn); return true; }, () => false);
    const job = Promise.race([stopping, new Promise(resolve => { timer = setTimeout(() => resolve(false), 15000); })]).finally(() => {
        clearTimeout(timer);
        if (webFollowRetirements.get(conn) === job) webFollowRetirements.set(conn, null);
    });
    webFollowRetirements.set(conn, job); return job;
}
function webRetireFollowMain() {
    // 立即废弃旧主连接与代次，再等待实际释放；任何迟到 hello/join/event 都不能恢复旧控制房。
    const old = connection, oldKey = mainMemberRoomKey();
    ++webMainGeneration;
    interruptStateResetWaits(oldKey);interruptMemberWaits(oldKey);mtCancelRoomPending(null,'房间跟随已切换连接，原操作结果待核对。');
    setConn(false);
    connection=null;joiningRoom=false;joinRejection='';roomCode='';password='';playerName='';serverCapabilities=[];players=[];lastProgress={};
    webMainServerUrl='';webConfirmedLogin=null;webMainAllInstancePlayers=[];webMainSnapshotGeneration=-1;webMainSnapshotRevision=null;webMainSnapshotRequest=null;
    latestControlBatch=null;selectedMemberUids.clear();selectionInitialized=false;activeMemberUid=null;activeMemberSource=null;
    document.getElementById('joinButton').disabled=false;
    document.getElementById('roomInfo').textContent='房间跟随 · 登录待确认';
    window.webRooms.mainChanged();
    if (typeof mtMembersChanged==='function')mtMembersChanged({full:true,players:[]});
    if (typeof dodocoMembers==='function')dodocoMembers();
    if (old && !webFollowRetirements.has(old))webFollowRetirements.set(old,null);
    return Promise.all([...webFollowRetirements.keys()].map(webCloseFollowTransport)).then(results=>results.every(Boolean));
}
function webFollowJoin(creds, current, onState, following = true) {
    if (!current()) return false;
    // 自动登录直接交给原 joinRoom 事务，不替换原页面的密码输入或记住登录选项。
    const joined=joinRoom(creds.serverUrl, { following, onState, credentials:creds });
    if(joined===false&&current())onState?.('failed');
    return joined;
}
function webRestoreLoginRetry(creds) {
    // 失败后保留原实际服务器的明确绑定。编辑房号/服务器时清除原密码，绝不改送默认服务器。
    const form=document.getElementById('loginForm');if(!form)return;
    const rc=document.getElementById('roomCode'), pw=document.getElementById('password'), server=document.getElementById('localServerUrl');
    let original=true;
    const edit=()=>{if(original&&(rc.value.trim()!==creds.roomCode||server&&server.value.trim()!==creds.serverUrl)){original=false;pw.value='';}};
    rc.addEventListener('input',edit);server?.addEventListener('input',edit);
    form.onsubmit=event=>{event.preventDefault();edit();joinRoom(original?creds.serverUrl:undefined);};
}
async function webRestoreFollowOriginal(original, stopped, current) {
    const closed = await stopped;
    let restored = !original.login && closed;
    if (original.login && closed && current()) {
        let timer;
        try {
            restored = await Promise.race([Promise.resolve(webFollowJoin(original.login,current,null,false)), new Promise(resolve => {timer=setTimeout(()=>resolve(false),25000);})]);
        } catch { restored=false; }
        finally { clearTimeout(timer); }
        if (!restored || !current()) { restored=false; webRetireFollowMain(); }
    }
    // 页面挂起或恢复登录失败也保留原表单，不能把未记住的输入丢在旧跟随连接内。
    webFillLoginForm(restored || !original.login ? original.form : original.login);
    document.getElementById('loginPanel').style.display=original.login&&!restored?'block':original.loginDisplay;
    document.getElementById('controlPanel').style.display=original.login&&!restored?'none':original.controlDisplay;
    if (restored || !original.login) {
        document.getElementById('roomInfo').textContent=original.roomInfo;showError(original.loginError);
    } else {
        webRestoreLoginRetry(original.login);
        showError('跟随已关闭，原控制房间登录尚未确认。请重新登录原房间。');
    }
    return restored;
}
function webFollowChanged(state) {
    if (webFollowBanner) {
        webFollowBanner.hidden = !state.enabled && !state.busy && !state.attention;
        webFollowBanner.textContent = state.message + (state.enabled ? '（跟随期间仅监视；关闭跟随恢复原房间与操作。）' : '');
    }
    window.webRooms.refreshUi();
}
function webInitFollow() {
    if (!window.webRoomFollow) return;
    const rooms=window.webRooms;
    webFollowBanner=document.createElement('p');webFollowBanner.className='wr-view-notice';webFollowBanner.hidden=true;
    webFollowBanner.setAttribute('role','status');webFollowBanner.setAttribute('aria-live','polite');
    document.getElementById('controlPanel').insertBefore(webFollowBanner,document.getElementById('gengdiNav'));
    webFollowController=window.webRoomFollow.createController({
        canBegin:()=>!joiningRoom&&!webMainDisconnecting&&rooms.canBeginFollow(),captureOriginal:webFollowOriginal,
        begin:()=>{
            rooms.beginFollow();
            document.getElementById('loginPanel').style.display='none';document.getElementById('controlPanel').style.display='block';
            document.querySelectorAll('#dodocoProduct, #mistletoeProduct, .local-connect').forEach(el=>el.disabled=true);
            showProduct('gengdi');
        },
        project:(snapshot,rejected)=>rooms.setFollowSnapshot(snapshot,rejected),mainContext:rooms.getMainContext,view:rooms.getView,profiles:rooms.listProfiles,
        retireMain:webRetireFollowMain,connect:webFollowJoin,restore:webRestoreFollowOriginal,
        end:original=>{
            rooms.endFollow();
            selectedMemberUids.clear();selectionInitialized=original.selectionInitialized&&!!rooms.getMainContext()?.ready;
            for(const source of original.selection) {
                const next=rooms.sourceFor(source.uid,source.roomKey);
                if(next&&rooms.getMainContext()?.roomKey===next.roomKey&&next.instanceId===source.instanceId&&next.machineId===source.machineId)selectedMemberUids.add(source.uid);
            }
            for(const [el,disabled] of original.disabled)if(el.isConnected)el.disabled=disabled;
            showProduct(original.product);renderControlRoom();
        },
        changed:webFollowChanged,reloadNotice:message=>showError(message)
    });
}
function webRoomsInit() {
    if(!window.webRooms)return;
    window.webRooms.init({getMainContext:webMainContext,defaultServerUrl:webDefaultRoomServer,
        legacyServerUrl:()=>document.body.dataset.localBrowser==='true'?null:location.origin,
        loadLegacyCredentials:loadCreds,clearLegacyCredentials:clearCreds,
        onChange:()=>{
            if(window.webRooms.isFollowing?.()) {
                if(window.webRooms.getView().mode==='orchestration')webRenderFollowOrchestration();else renderControlRoom();
                webFollowController?.refresh();return;
            }
            if(window.webRooms.getView().mode==='orchestration'&&!webMainOwnerView&&document.getElementById('gengdiProduct')?.classList.contains('active')&&document.getElementById('controlPanel').style.display!=='none')webOpenOwnerView('orchestration');
            renderControlRoom();webMainOwnerView?.refresh?.();webSyncOwnerRoomNames();
        },onViewChange:webRoomViewChanged,
        onEvent:webRoomSecondaryEvent,onDisconnect:webRoomSecondaryDisconnected,
        onSwitchMain:creds=>webSwitchControlRoom(creds),onOrchestration:()=>{if(!webMainOwnerView)webOpenOwnerView('orchestration');},onOwnerProfiles:()=>webOpenOwnerView('profiles'),
        ...(window.webRoomFollow ? { followPanel:host=>webFollowController?.mountPanel(host) } : {})});
    webInitFollow();
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
    if(window.webRooms?.isFollowing?.())throw new Error('请关闭房间跟随后切换控制房间。');
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
