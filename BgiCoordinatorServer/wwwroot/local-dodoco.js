// 本机入口：一次性令牌只用于同源配对；短期会话仅留在当前标签的 sessionStorage。
(async function startLocalDodoco() {
    // 浏览器复用现有标签页时，新的配对链接可能只触发片段导航，不会重新执行脚本。
    // 重新加载保留新令牌，让下面的正常配对流程替换旧会话。
    window.addEventListener('hashchange', () => {
        if (new URLSearchParams(location.hash.slice(1)).get('token')) location.reload();
    });
    const hash = new URLSearchParams(location.hash.slice(1));
    const pairingToken = hash.get('token');
    history.replaceState(null, '', location.pathname + location.search);
    const storageKey='bgi.dodoco.localSession.v1';
    let saved=null;
    try { saved=JSON.parse(sessionStorage.getItem(storageKey)||'null'); } catch { /* 浏览器禁用存储时仍允许本次配对 */ }
    if (location.protocol !== 'http:' || location.hostname !== '127.0.0.1' || (!pairingToken && !saved?.sessionId)) {
        document.getElementById('loginError').textContent = '本机入口需要从助手打开并完成配对。';
        return;
    }
    let session = '', bridgeInstance = '', identity = null, localKey = '', selected = null;
    let logTimer = null, logEpoch = 0, logCursor = null, identityTimer = null, available = false, remoteButton = null;
    const byId = id => document.getElementById(id);
    function markUnavailable(message) {
        available=false;session='';try{sessionStorage.removeItem(storageKey);}catch{}stopLog();
        if(typeof watchStopAlerts==='function')watchStopAlerts('本机连接中断，告警监视已停止。');
        if(byId('connectionText'))byId('connectionText').textContent=roomReady?'本机连接已断开 · 远程已连接':'本机连接已断开';
        if(byId('connDot'))byId('connDot').className='dot dot-offline';
        if(window.bgiLocalBridge && typeof dodocoMembers==='function')dodocoMembers();if(typeof mtMembersChanged==='function')mtMembersChanged();
        if(window.bgiLocalBridge && dodoco.active)dodocoStatus(message || '本机连接已断开，请从助手重新打开。');
    }
    const matches = p => !!(identity?.uid && identity?.clientInstanceId && identity?.machineId && p &&
        p.playerUid === identity.uid && p.clientInstanceId === identity.clientInstanceId && p.machineId === identity.machineId);
    const localTarget = uid => uid === localKey || (selected?.uid === uid && selected.local === true) ||
        (dodoco.target === '*' && players.some(p => p.playerUid === uid && matches(p)));
    function validateTarget(uid) {
        if (uid === localKey) return;
        if (selected?.uid === uid && selected.local) {
            const current = players.find(p => p.playerUid === uid);
            if (current && !matches(current)) throw new Error('成员实例身份已变化，请重新选择目标；未改走服务器。');
            return;
        }
        if (dodoco.target === '*' && players.some(p => p.playerUid === uid && matches(p))) return;
        throw new Error('本机目标身份未确认；未改走服务器。');
    }
    async function call(operation, args = {}, timeout = 20000, routeUid = dodoco.target) {
        // localStatus 供槲寄生区分「确定未执行」与「结果未确认」；嘟嘟可只读 message。
        if (!available || !session) throw Object.assign(new Error('本机连接未就绪，请从助手重新打开。'), { localStatus:'not_sent' });
        if (operation !== 'local.identity' && !operation.startsWith('mistletoe.')) validateTarget(routeUid);
        const requestId = crypto.randomUUID().replaceAll('-', ''), controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), timeout);
        try {
            const response = await fetch('/api/execute', { method:'POST', cache:'no-store', credentials:'omit', signal:controller.signal,
                headers:{'Content-Type':'application/json','X-BGI-Session':session}, body:JSON.stringify({requestId,operation,args}) });
            if (!response.ok) {
                if (response.status === 401 || response.status === 403) markUnavailable('本机会话已失效，请从助手重新打开。');
                // 非 200 只会在进入执行前返回（会话、来源或格式无效）。
                throw Object.assign(new Error(`本机连接拒绝请求（${response.status}），结果未确认。`), { localStatus:'error' });
            }
            const result = await response.json();
            if (!result || result.requestId !== requestId) throw new Error('本机回复身份不匹配，结果未确认。');
            if (result.instanceId !== bridgeInstance) {
                markUnavailable('本机助手实例已变化，请从助手重新打开。');
                throw new Error('本机助手实例已变化，结果未确认，请重新打开。');
            }
            if (result.status !== 'ok') throw Object.assign(new Error(result.message || '本机操作未完成或结果未知。'), { localStatus:result.status === 'error' ? 'error' : 'unknown' });
            return result.data;
        } catch (error) {
            if (error.name === 'AbortError') throw new Error('等待本机回复超时，结果未确认；未改走服务器。');
            if (error instanceof TypeError) { markUnavailable('本机连接中断，结果未确认；未改走服务器。'); throw new Error('本机连接中断，结果未确认；未改走服务器。'); }
            throw error;
        } finally { clearTimeout(timer); }
    }
    function stopLog() { logEpoch++; clearTimeout(logTimer); logTimer=null; logCursor=null; }
    function startLog(uid) {
        stopLog(); const epoch=logEpoch;
        const poll = async () => {
            if (epoch!==logEpoch || !available || dodoco.subscribed!==uid || !dodoco.active || dodoco.tab!=='live') return;
            try {
                const result=await call('log.poll',{cursor:logCursor},20000,uid);
                if(epoch!==logEpoch || dodoco.subscribed!==uid)return;
                if(!result || !Array.isArray(result.lines) || !Number.isSafeInteger(result.cursor)) throw new Error('本机日志响应无效');
                logCursor=result.cursor;
                if(result.sourceAvailable===false) dodocoStatus('本机日志来源不可用或尚未配置，正在等待。');
                else if(result.dropped>0) dodocoStatus(`本机日志缓冲已跳过 ${result.dropped} 条，请在日志浏览中查看历史。`);
                else dodocoStatus('正在读取本机实时日志。');
                if(result.lines.length) dodocoEvent({name:'log.batch',payload:{uid,lines:result.lines,infoOnly:false}});
            } catch(e) {
                if(epoch!==logEpoch)return;
                dodocoStatus('本机实时日志未取得：'+e.message);stopLog();dodoco.subscribed='';return;
            }
            if(epoch===logEpoch)logTimer=setTimeout(poll,1000);
        };
        logTimer=setTimeout(poll,0);
    }
    function gatewayUrl() {
        let value=(byId('localServerUrl')?.value || identity?.serverUrl || '').trim().replace(/\/+$/, '');
        if(value.toLowerCase().endsWith('/hub'))value=value.slice(0,-4);
        if(!value)throw new Error('请填写协调服务器地址；本机嘟嘟可不需要服务器。');
        let url;try{url=new URL(value);}catch{throw new Error('协调服务器地址无效。');}
        if(!['http:','https:'].includes(url.protocol)||url.username||url.password||url.search||url.hash)
            throw new Error('协调服务器地址必须是 HTTP 或 HTTPS 基地址。');
        return value + '/gateway';
    }
    function selectTarget(uid) {
        if(uid===localKey) selected={uid,local:true};
        else { const candidate=players.find(p=>p.playerUid===uid);selected={uid,local:available && !!candidate && matches(candidate)}; }
    }
    function members(remote) {
        const status=identity?.status && typeof identity.status==='object' ? identity.status : {};
        const own={...status,playerUid:localKey,playerName:identity?.name||'本机助手',online:available,isLocalDirect:true};
        return [own,...remote.filter(p=>!available || !matches(p))];
    }
    function showRemoteLogin() {
        byId('loginPanel').style.display='block';byId('controlPanel').style.display='none';
        if(!byId('localServerUrl')) {
            const form=byId('loginForm'), label=document.createElement('label'),input=document.createElement('input');
            label.className='field-label';label.htmlFor='localServerUrl';label.textContent='协调服务器地址';
            input.id='localServerUrl';input.className='local-server-url';input.type='url';input.placeholder='例如 https://服务器地址';input.value=identity?.serverUrl||'';
            form.insertBefore(label,form.firstChild);form.insertBefore(input,label.nextSibling);
            const back=document.createElement('button');back.type='button';back.className='btn';back.textContent='返回本机嘟嘟可';
            back.onclick=()=>{byId('loginPanel').style.display='none';byId('controlPanel').style.display='block';showProduct('dodoco');};
            form.appendChild(back);
        }
        byId('roomCode').value=identity?.roomCode||byId('roomCode').value;
        byId('nickname').value=identity?.name||byId('nickname').value;
        byId('rememberMe').checked=false;
    }
    function networkChanged(online) {
        if(remoteButton)remoteButton.textContent=online?'断开远程房间':'连接远程房间';
        if(!available){byId('connectionText').textContent=online?'本机连接已断开 · 远程已连接':'本机连接已断开';byId('connDot').className='dot dot-offline';return;}
        if(!online && available) { byId('roomInfo').textContent='本机助手'; byId('connectionText').textContent='本机直连';byId('connDot').className='dot dot-online'; }
    }
    try {
        let pair=saved;
        if(pairingToken) {
            try{sessionStorage.removeItem(storageKey);}catch{}
            const response=await fetch('/api/pair',{method:'POST',cache:'no-store',credentials:'omit',headers:{'Content-Type':'application/json'},body:JSON.stringify({token:pairingToken})});
            if(!response.ok)throw new Error('本机配对失败或已过期，请从助手重新打开。');
            pair=await response.json();
        }
        if(typeof pair.sessionId!=='string'||!pair.sessionId||typeof pair.instanceId!=='string'||!pair.instanceId)throw new Error('本机配对响应无效。');
        session=pair.sessionId;bridgeInstance=pair.instanceId;available=true;
        identity=await call('local.identity');
        if(!identity||typeof identity!=='object')throw new Error('本机助手身份不可用。');
        try{sessionStorage.setItem(storageKey,JSON.stringify({sessionId:session,instanceId:bridgeInstance}));}catch{}
        localKey='local_'+bridgeInstance;
        window.bgiLocalBridge={get available(){return available;},execute:call,usesLocal:localTarget,selectTarget,members,gatewayUrl,startLog,stopLog,networkChanged,localIdentity:()=>identity};
        remoteButton=document.createElement('button');remoteButton.type='button';remoteButton.className='icon-button local-connect';remoteButton.textContent='连接远程房间';
        remoteButton.onclick=async()=>{if(roomReady){try{await connection.stop();setConn(false);}catch(e){dodocoStatus('远程断开未确认：'+e.message);}return;}showRemoteLogin();};
        document.querySelector('.topbar').appendChild(remoteButton);
        byId('loginPanel').style.display='none';byId('controlPanel').style.display='block';
        byId('roomInfo').textContent='本机助手';byId('connectionText').textContent='本机直连';byId('connDot').className='dot dot-online';
        dodoco.target=localKey;selectTarget(localKey);showProduct('dodoco');
        identityTimer=setInterval(async()=>{
            if(!available)return;
            try {const next=await call('local.identity');if(!next||next.uid!==identity.uid||next.clientInstanceId!==identity.clientInstanceId||next.machineId!==identity.machineId)throw new Error('本机助手身份已变化');identity=next;if(dodoco.active&&dodoco.tab==='members')dodocoDrawMembers();}
            catch(e){markUnavailable('本机连接未就绪：'+e.message);clearInterval(identityTimer);}
        },10000);
    } catch(error) {
        markUnavailable();byId('loginError').textContent='本机入口不可用：'+error.message;
    }
})();
