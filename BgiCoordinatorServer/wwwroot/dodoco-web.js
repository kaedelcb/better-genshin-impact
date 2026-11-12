// 房间内嘟嘟可：使用现有 v3 网关；转发确认不代表成员响应。
const dodoco = { levels:new Set(['ERR','WRN','INF','DBG']), follow:true, programScroll:false, active:false, tab:'live', target:'', subscribed:'', paused:false, live:[], files:[], lines:[], fileName:'', raw:null, page:0, fileCount:0, worker:null, workerId:0, workerLatest:0, workerTimer:null, regexTimedOut:false, fileBusy:false, uncertain:false, pending:new Map(), frame:null, frames:new Map(), frameSelection:new Map(), autoTimer:null, autoBusy:false, generation:0 };
const dodocoRoot = document.getElementById('dodocoView');
const d = id => document.getElementById(id);
const de = value => escapeHtml(value);
const dodocoMaxLines = 12000, dodocoMaxBytes = 200 * 1024 * 1024, dodocoMaxLiveChars = 8 * 1024 * 1024;

// 一次进入监控最多三分钟；刷新、切换成员和重连都沿用同一次观看。
const dodocoScreenLimitMs = 3 * 60 * 1000;
const dodocoScreenExpiredText = '本次桌面监控已满 3 分钟，请退出桌面监控后重新进入。';
// HTTP 部署也支持 getRandomValues，不能依赖只在安全上下文提供的 randomUUID。
function dodocoScreenId() { return Array.from(crypto.getRandomValues(new Uint8Array(16)), b=>b.toString(16).padStart(2,'0')).join(''); }
function dodocoScreenSync() {
    if (!dodoco.active || dodoco.tab !== 'screen') { dodocoScreenLeave(); return; }
    if (dodoco.screenVisit) return;
    dodoco.screenVisit = { id:dodocoScreenId(), started:performance.now(), wallStarted:Date.now(), expired:false };
    dodoco.screenTimer = setInterval(() => {
        dodocoScreenAllowed();
        const p=dodocoControl.presence;
        if(p?.active && Math.max(performance.now()-p.leaseStarted,Date.now()-p.leaseWallStarted)>=12000)
            dodocoControlClose('控制提示租约已过期，控制已关闭。');
        dodocoControlSync();
    }, 1000);
}
function dodocoScreenLeave() {
    dodocoControlClose('已退出桌面监控，控制已关闭。');
    dodocoStopAuto();
    clearInterval(dodoco.screenTimer);
    dodoco.screenTimer = null;
    dodoco.screenVisit = null;
    dodoco.captureRun = null;
    dodoco.autoBusy = false;
    for (const p of [...dodoco.pending.values()])
        if (p.kind === 'frame') dodocoFinish(p, '已退出桌面监控。');
}
function dodocoScreenAllowed() {
    const visit = dodoco.screenVisit;
    if (!visit || !dodoco.active || dodoco.tab !== 'screen') return false;
    if (!visit.expired && Math.max(performance.now()-visit.started, Date.now()-visit.wallStarted) < dodocoScreenLimitMs) return true;
    visit.expired = true;
    dodocoControlClose(dodocoScreenExpiredText);
    dodocoStopAuto();
    clearInterval(dodoco.screenTimer);
    dodoco.screenTimer = null;
    for (const p of [...dodoco.pending.values()])
        if (p.kind === 'frame') dodocoFinish(p, dodocoScreenExpiredText);
    for (const id of ['dodocoCapture', 'dodocoAuto', 'dodocoInterval']) { const el=d(id); if(el)el.disabled=true; }
    dodocoStatus(dodocoScreenExpiredText);
    return false;
}

function showProduct(name) {
    const mistletoe = name === 'mistletoe' && !!d('mistletoeView');
    dodoco.active = name === 'dodoco';
    dodocoScreenSync();
    const gengdi = !dodoco.active && !mistletoe;
    d('gengdiProduct').classList.toggle('active', gengdi);
    d('dodocoProduct').classList.toggle('active', dodoco.active);
    d('mistletoeProduct')?.classList.toggle('active', mistletoe);
    d('gengdiNav').hidden = !gengdi;
    d('activity').hidden = !gengdi;
    dodocoRoot.hidden = !dodoco.active;
    if (d('mistletoeView')) d('mistletoeView').hidden = !mistletoe;
    if (typeof mtShow === 'function') mtShow(mistletoe);
    if (!gengdi) { d('homeView').hidden = true; d('detailView').hidden = true; d('selectionBar').hidden = true; }
    if (dodoco.active) { dodocoRender(); dodocoSyncSubscription(); }
    else { dodocoSyncSubscription(); if (gengdi) showHome(); }
}
function dodocoIsLocal(uid = dodoco.target) { return !!globalThis.window?.bgiLocalBridge?.usesLocal(uid); }
function dodocoMembersList() { return globalThis.window?.bgiLocalBridge ? window.bgiLocalBridge.members(players) : players; }
function dodocoTargetReady(uid = dodoco.target) {
    return dodocoIsLocal(uid) ? !!window.bgiLocalBridge?.available : !!roomReady && players.some(p => p.playerUid === uid && p.online === true);
}
function dodocoStatus(text) { if(dodoco.active && dodoco.tab==='screen' && dodoco.screenVisit?.expired)text=dodocoScreenExpiredText; const el = d('dodocoStatus'); if (el) el.textContent = (globalThis.window?.bgiLocalBridge && dodoco.target ? (dodoco.target==='*' ? '本机与服务器 · ' : dodocoIsLocal() ? (window.bgiLocalBridge.available?'本机直连 · ':'本机连接未就绪 · ') : '服务器中转 · ') : '') + (dodoco.uncertain ? '先前请求因断线结果未确认。' : '') + text; }
function dodocoMemberOptions() {
    const members = dodocoMembersList().filter(p => p.playerUid && !String(p.playerUid).startsWith('web_'));
    return '<option value="">选择成员</option>' + (dodoco.tab==='screen'?`<option value="*" ${dodoco.target==='*'?'selected':''}>全部在线成员</option>`:'') + members.map(p => `<option value="${de(p.playerUid)}" ${p.playerUid===dodoco.target?'selected':''}>${de(p.playerName || p.playerUid)}${p.isLocalDirect ? (p.online?'（本机直连）':'（本机连接已断开）') : p.online ? '' : '（离线）'}</option>`).join('');
}
function dodocoSelectedOnline() { return dodocoTargetReady(); }
function dodocoReady() {
    if (!dodoco.target) { dodocoStatus('请先选择成员。'); return false; }
    if (dodocoIsLocal() && !window.bgiLocalBridge.available) { dodocoStatus('本机连接未就绪，请从助手重新打开。'); return false; }
    if (!dodocoIsLocal() && !roomReady) { dodocoStatus('房间连接未就绪，请等待重新连接。'); return false; }
    if (!dodocoSelectedOnline()) { dodocoStatus('成员当前离线，无法请求数据。'); return false; }
    dodoco.uncertain=false;
    return true;
}
async function dodocoDispatch(name, payload, transport=connection) {
    try {
        const response = await transport.invoke('Dispatch', makeEnvelope('command', name, { roomCode, ...payload }));
        checkGatewayError(response);
    } catch (error) {
        if (!roomReady || transport?.state === 'Disconnected' || transport?.state === 'Reconnecting')
            throw new Error('连接已断开，结果未确认。');
        throw error;
    }
}
function dodocoCancelPending(message) {
    dodoco.generation++;
    dodoco.captureRun=null; dodoco.autoBusy=false;
    for (const p of dodoco.pending.values()) { clearTimeout(p.timer); p.reject(new Error(message)); }
    dodoco.pending.clear();
}
function dodocoWait(kind, uid, requestId, timeout = 20000) {
    const promise = new Promise((resolve, reject) => {
        const p = {kind, uid, requestId, resolve, reject, timer:null, chunks:[], total:0, bytes:0};
        p.expire = () => { clearTimeout(p.timer); p.timer = setTimeout(() => { if (dodoco.pending.delete(requestId)) reject(new Error('等待成员响应超时；可能离线、未共享、版本不支持或采集失败。')); }, timeout); };
        p.expire(); dodoco.pending.set(requestId, p);
    });
    promise.catch(() => {});
    return promise;
}
async function dodocoRequest(operation, args = {}, timeout = 20000) {
    if (!dodocoReady()) throw new Error('房间或目标成员未就绪。');
    if (dodocoIsLocal()) return window.bgiLocalBridge.execute(operation, args, timeout);
    const id=newRequestId(),uid=dodoco.target,wait=dodocoWait('operation',uid,id,timeout);
    try { await dodocoDispatch('dodoco.request',{targetUid:uid,requestId:id,operation,args}); return await wait; }
    catch(e) { const p=dodoco.pending.get(id); if(p)dodocoFinish(p,e.message); throw e; }
}
function dodocoFinish(p, error, value) {
    if (!dodoco.pending.delete(p.requestId)) return;
    clearTimeout(p.timer); if (error) p.reject(new Error(error)); else p.resolve(value);
}
function dodocoConnection(online) {
    if (!online) dodocoControlClose('连接已断开，控制已关闭；已发送的输入结果可能未确认。');
    if (typeof rlConnection === 'function') rlConnection(online);
    if (!online && dodocoIsLocal() && window.bgiLocalBridge.available) { dodocoStatus('服务器连接中断，本机连接仍可使用。'); return; }
    if (!online) { if(dodoco.pending.size) dodoco.uncertain=true; dodocoStopAuto(); if(typeof watchStopAlerts==='function')watchStopAlerts('连接中断，网页告警监视已停止。'); dodoco.subscribed=''; dodocoCancelPending('连接已断开，结果未确认。'); dodocoStatus('连接已断开，等待重新连接；不会自动重发请求。'); }
    else if (dodoco.active) { dodocoStatus('已连接房间。'); dodocoSyncSubscription(); }
}
function dodocoMembers() {
    if (typeof rlMembersChanged === 'function') rlMembersChanged();
    if (!dodoco.active) return;
    const select = d('dodocoMember'); if (select) select.innerHTML = dodocoMemberOptions();
    if (dodoco.tab === 'members') dodocoDrawMembers();
    if ((dodocoControl.enabled || dodocoControl.pending) && !dodocoTargetReady()) dodocoControlClose('成员连接已断开，控制已关闭。');
    dodocoControlSync();
    dodocoSyncSubscription();
}
async function dodocoSyncSubscription() {
    const desired = dodoco.active && dodoco.tab === 'live' && dodocoSelectedOnline() ? dodoco.target : '';
    const old = dodoco.subscribed;
    if (old === desired) return;
    const oldLocal = dodoco.subscribedLocal;
    dodoco.subscribed = desired;
    dodoco.subscribedLocal = !!desired && dodocoIsLocal(desired);
    if (oldLocal) window.bgiLocalBridge?.stopLog();
    else if (old && roomReady) { try { await dodocoDispatch('log.unsubscribe', {targetUid:old}); } catch {} }
    if (desired && dodoco.subscribedLocal && dodoco.subscribed === desired) {
        window.bgiLocalBridge.startLog(desired);
    } else if (desired && roomReady && dodoco.subscribed === desired) {
        try { await dodocoDispatch('log.subscribe', {targetUid:desired}); if (dodoco.subscribed===desired) dodocoStatus('已请求实时日志；等待成员上报，暂无上报不代表日志为空。'); }
        catch (e) { if (dodoco.subscribed===desired) dodoco.subscribed=''; dodocoStatus('日志订阅未确认：' + e.message); }
    }
}
function dodocoEvent(env) {
    if (typeof rlEvent === 'function') rlEvent(env, 'own');
    const x=env.payload || {};
    if (env.name === 'desktop.control.result') { dodocoControlResult(x); return; }
    if (env.name === 'desktop.control.presence.result') { dodocoPresenceResult(x); return; }
    if (env.name === 'log.batch' && x.uid === dodoco.subscribed && Array.isArray(x.lines)) {
        for (const line of x.lines) if (typeof line === 'string') dodoco.live.push({uid:x.uid, text:line, infoOnly:!!x.infoOnly});
        if (dodoco.live.length > dodocoMaxLines) dodoco.live.splice(0, dodoco.live.length-dodocoMaxLines);
        let chars=dodoco.live.reduce((n,item)=>n+item.text.length,0);while(chars>dodocoMaxLiveChars && dodoco.live.length) chars-=dodoco.live.shift().text.length;
        if (dodoco.active && dodoco.tab === 'live' && !dodoco.paused) dodocoDrawLive();
        return;
    }
    if (env.name === 'screenshot.member' && !x.requestId) return; // 旧服务端无请求身份，不能认领。
    const p=dodoco.pending.get(x.requestId); if (!p) return;
    if (env.name==='dodoco.result' && p.kind==='operation') {
        if(x.status==='ok') dodocoFinish(p,null,x.data);
        else dodocoFinish(p,x.message || (x.status==='denied'?'成员未授权此操作。':'成员操作失败或结果未知。'));
        return;
    }
    if (x.uid!==p.uid) return;
    if (env.name === 'screenshot.member' && p.kind==='frame') {
        if (typeof x.jpegBase64 !== 'string' || !x.jpegBase64 || x.jpegBase64.length > 512*1024) return dodocoFinish(p,'截图无效或超过大小限制。');
        dodocoFinish(p,null,x);
    } else if (env.name==='log.fileList' && p.kind==='files') {
        if (!Array.isArray(x.files)) return dodocoFinish(p,'成员返回的文件列表无效。');
        dodocoFinish(p,null,x.files.slice(0,200));
    } else if (env.name==='log.fileChunk' && p.kind==='download') {
        if (x.fileName!==p.fileName) return;
        if (x.done && x.totalChunks===0) return dodocoFinish(p,'成员正忙、拒绝共享或文件超限，无法确定具体原因。');
        if (!Number.isInteger(x.seq) || !Number.isInteger(x.totalChunks) || x.totalChunks<1 || x.totalChunks>400 || x.seq!==p.chunks.length || (p.total && p.total!==x.totalChunks) || typeof x.chunkBase64!=='string' || x.chunkBase64.length>256*1024) return dodocoFinish(p,'日志分块缺失或格式无效。');
        p.total=x.totalChunks; p.bytes+=x.chunkBase64.length;
        if (p.bytes>100*1024*1024) return dodocoFinish(p,'压缩日志超过网页处理上限。');
        p.chunks.push(x.chunkBase64); p.expire();
        if (x.done) { if (p.chunks.length!==p.total) return dodocoFinish(p,'日志分块不完整。'); dodocoFinish(p,null,p.chunks); }
    }
}
function dodocoRender() {
    if(dodocoRemote.open)dodocoControlClose('页面已刷新，控制已关闭。');
    dodocoScreenSync();
    dodocoRoot.innerHTML = `<div class="dd-head"><nav class="dodoco-tabs" aria-label="嘟嘟可功能"><button data-tab="live">实时日志</button><button data-tab="files">日志浏览</button><button data-tab="watch">异常监控</button><button data-tab="members">锄地数据</button><button data-tab="screen">桌面监控</button></nav><div class="dd-head-target"><label for="dodocoMember">成员</label><select id="dodocoMember">${dodocoMemberOptions()}</select></div></div><p id="dodocoStatus" class="dodoco-status dd-status" role="status">${dodoco.uncertain?'先前请求因断线结果未确认。':''}${dodocoIsLocal()?(window.bgiLocalBridge.available?'本机直连。':'本机连接未就绪。'):roomReady?'请选择在线成员。':'连接未就绪。'}</p><div id="dodocoContent" class="dd-body"></div>`;
    dodocoRoot.querySelectorAll('[data-tab]').forEach(b => { b.classList.toggle('active',b.dataset.tab===dodoco.tab); b.onclick=()=>{ if(b.dataset.tab!==dodoco.tab)dodocoControlClose('已切换页面，控制已关闭。'); dodocoStopAuto(); dodoco.tab=b.dataset.tab; if(dodoco.target==='*' && dodoco.tab!=='screen')dodoco.target=''; dodocoRender(); dodocoSyncSubscription(); }; });
    d('dodocoMember').onchange=e=>{ dodocoControlClose('成员已切换，控制已关闭。'); dodoco.target=e.target.value; window.bgiLocalBridge?.selectTarget(dodoco.target); dodocoCancelPending('成员已切换，原请求结果未确认。'); dodoco.files=[]; dodoco.fileDate=undefined; dodoco.lines=[]; dodoco.fileName=''; dodoco.raw=null; dodocoStopAuto(); clearTimeout(dodoco.workerTimer); dodoco.regexTimedOut=false; dodoco.workerLatest=++dodoco.workerId; if(dodoco.worker){dodoco.worker.terminate();dodoco.worker=null;} dodoco.fileBusy=false; dodocoRender(); dodocoSyncSubscription(); };
    if (dodoco.tab==='live') dodocoRenderLive();
    if (dodoco.tab==='files') dodocoRenderFiles();
    if (dodoco.tab==='watch' && typeof dodocoRenderWatch==='function') dodocoRenderWatch();
    if (dodoco.tab==='members') dodocoDrawMembers();
    if (dodoco.tab==='screen') dodocoRenderScreen();
}
function dodocoLiveEmptyText(){
    if(!dodoco.target)return '先在右上角选择成员';
    if(dodocoIsLocal())return window.bgiLocalBridge?.available?'等待本机日志；BGI 未运行时不会产生新日志':'本机连接未就绪，请从助手重新打开';
    if(!roomReady)return '房间连接未就绪，等待重新连接';
    const p=players.find(x=>x.playerUid===dodoco.target);
    if(!p||!p.online)return '成员当前离线，上线后才会推送日志';
    if(p.bgiStatus&&p.bgiStatus!=='running')return '成员的 BGI 未运行，暂时没有新日志';
    const all=dodoco.live.some(x=>x.uid===dodoco.target);
    return all?'当前筛选条件下没有匹配的日志':'已订阅，等待成员上报；只显示进入本页后的新日志';
}
function dodocoRenderLive() {
    d('dodocoContent').innerHTML = `<section class="dd-card dd-card-log"><header class="dd-card-head"><div><h2>实时日志</h2><p class="dodoco-hint">仅显示进入本页后收到的日志；清空只影响本网页，不删除源文件</p></div><div class="dd-actions"><button id="dodocoPause" class="dd-btn">${dodoco.paused?'继续':'暂停显示'}</button><label class="dd-check"><input id="dodocoFollow" type="checkbox" ${dodoco.follow?'checked':''}>自动滚动</label><button id="dodocoLatest" class="dd-btn">回到最新</button><button id="dodocoClear" class="dd-btn dd-btn-quiet">清空当前视图</button></div></header><div class="dd-filters"><label class="dd-field dd-grow"><span>关键词</span><input id="dodocoLiveFilter" type="search" placeholder="筛选当前视图"></label><div class="dd-field"><span>级别</span><div class="dd-levels" id="dodocoLevels">${['ERR','WRN','INF','DBG'].map(x=>`<label class="dd-level lv-${x.toLowerCase()}"><input type="checkbox" value="${x}" ${dodoco.levels.has(x)?'checked':''}>${x}</label>`).join('')}</div></div><label class="dd-field"><span>实例</span><select id="dodocoInstance"><option value="">全部实例</option></select></label></div><div id="dodocoLive" class="dodoco-log" role="log"></div></section>`;
    d('dodocoPause').onclick=()=>{dodoco.paused=!dodoco.paused;d('dodocoPause').textContent=dodoco.paused?'继续':'暂停显示';if(!dodoco.paused)dodocoDrawLive();};
    d('dodocoLatest').onclick=()=>{dodoco.follow=true;d('dodocoFollow').checked=true;const box=d('dodocoLive');box.scrollTop=box.scrollHeight;};
    d('dodocoFollow').onchange=e=>{dodoco.follow=e.target.checked;if(dodoco.follow){const box=d('dodocoLive');box.scrollTop=box.scrollHeight;}};
    d('dodocoLive').onscroll=e=>{const box=e.target;if(box.scrollHeight-box.scrollTop-box.clientHeight>60&&dodoco.follow&&!dodoco.programScroll){dodoco.follow=false;d('dodocoFollow').checked=false;}dodoco.programScroll=false;};
    d('dodocoClear').onclick=()=>{dodoco.live=dodoco.live.filter(x=>x.uid!==dodoco.target);dodocoDrawLive();};
    d('dodocoLevels').onchange=()=>{dodoco.levels=new Set([...d('dodocoLevels').querySelectorAll('input:checked')].map(x=>x.value));dodocoDrawLive();}; d('dodocoInstance').onchange=dodocoDrawLive; d('dodocoLiveFilter').oninput=dodocoDrawLive; dodocoDrawLive();
}
function dodocoDrawLive() {
    const box=d('dodocoLive'); if(!box)return;
    const follow=dodoco.follow;
    const levels=dodoco.levels, q=d('dodocoLiveFilter').value.toLowerCase(), instanceSelect=d('dodocoInstance'), selected=instanceSelect.value;
    const instanceOf=text=>/^\[[^\]]+\] \[[^\]]+\] \[([^\]]*)\]/.exec(text)?.[1]||'';
    const instances=[...new Set(dodoco.live.filter(x=>x.uid===dodoco.target).map(x=>instanceOf(x.text)).filter(Boolean))].sort();
    if (instanceSelect.options.length!==instances.length+1 || instances.some((x,i)=>instanceSelect.options[i+1]?.value!==x)) {
        instanceSelect.innerHTML='<option value="">全部实例</option>'+instances.map(x=>`<option value="${de(x)}">${de(x)}</option>`).join('');
        instanceSelect.value=instances.includes(selected)?selected:'';
    }
    const lines=dodoco.live.filter(x=>x.uid===dodoco.target && (levels.size===4 || [...levels].some(l=>x.text.includes('['+l+']')) || (levels.has('INF') && !/\[(ERR|WRN|INF|DBG)\]/.test(x.text))) && (!instanceSelect.value || instanceOf(x.text)===instanceSelect.value) && x.text.toLowerCase().includes(q));
    box.innerHTML=lines.slice(-1500).map(x=>`<div class="dodoco-line ${/\[(ERR|WRN|DBG)\]/.exec(x.text)?.[1]?.toLowerCase()||''}">${de(x.text)}</div>`).join('');
    if(follow){dodoco.programScroll=true;box.scrollTop=box.scrollHeight;}
    if(!lines.length) box.innerHTML=`<p class="dd-empty">${dodocoLiveEmptyText()}</p>`;
    else if(lines.length>1500) dodocoStatus(`当前筛选 ${lines.length} 行，显示最近 1500 行；浏览器最多保留 ${dodocoMaxLines} 行。`);
    else if(lines.some(x=>x.infoOnly)) dodocoStatus('成员启用了省流，仅上报信息及以上级别。');
}
function dodocoRenderFiles() {
    d('dodocoContent').innerHTML=`<div class="dd-split"><aside class="dd-card dd-files"><header class="dd-card-head"><div><h2>日志文件</h2><p class="dodoco-hint">${dodocoIsLocal()?'本机直连读取助手可用的日志':'按成员共享设置提供；空列表可能是未共享'}</p></div><button id="dodocoList" class="dd-btn dd-btn-primary">读取成员日志列表</button></header><label class="dd-field dd-file-date"><span>按修改日期</span><select id="dodocoFileDate"><option value="">全部日期</option></select></label><div id="dodocoFiles" class="dodoco-file-list"></div><label class="dd-drop"><input type="file" id="dodocoLocal" accept=".log,.txt,text/plain"><strong>打开本机日志文件</strong><small>.log / .txt · 最大 200 MB · 只在本网页处理</small></label><div id="roomLogHost" class="dd-remote-entry"></div></aside><section class="dd-card dd-card-log dd-viewer"><div class="dd-filters"><label class="dd-field dd-grow"><span>关键词</span><input type="search" id="dodocoSearch" placeholder="搜索日志内容"></label><label class="dd-check"><input id="dodocoRegex" type="checkbox">正则搜索</label><label class="dd-field"><span>级别</span><select id="dodocoFileLevel"><option value="">全部级别</option><option>ERR</option><option>WRN</option><option>INF</option><option>DBG</option></select></label><label class="dd-field"><span>实例</span><input type="search" id="dodocoFileInstance" placeholder="实例关键词"></label><div class="dd-field dd-range"><span>时间段</span><div><input type="time" id="dodocoFrom" aria-label="起始时间"><i aria-hidden="true">至</i><input type="time" id="dodocoTo" aria-label="结束时间"></div></div></div><div class="dd-result-bar"><p id="dodocoFileStatus" class="dodoco-status">选择左侧日志或打开本机文件。</p><div class="dd-actions"><span class="dd-pager"><button id="dodocoPrev" class="dd-btn dd-btn-icon" aria-label="上一页" title="上一页">‹</button><button id="dodocoNext" class="dd-btn dd-btn-icon" aria-label="下一页" title="下一页">›</button></span><button id="dodocoExport" class="dd-btn" disabled>导出筛选结果</button><button id="dodocoCsv" class="dd-btn" disabled>导出 CSV</button><button id="dodocoDownload" class="dd-btn dd-btn-primary" disabled>下载完整日志</button></div></div><div id="dodocoFileText" class="dodoco-log"></div></section></div>`;
    d('dodocoList').onclick=dodocoLoadFiles; d('dodocoLocal').onchange=dodocoOpenLocal;
    for(const id of ['dodocoSearch','dodocoFrom','dodocoTo','dodocoFileLevel','dodocoFileInstance','dodocoRegex']) d(id).oninput=()=>{dodoco.page=0;dodocoDrawFile();};
    d('dodocoPrev').onclick=()=>{dodoco.page=Math.max(0,dodoco.page-1);dodocoDrawFile();};
    d('dodocoNext').onclick=()=>{dodoco.page++;dodocoDrawFile();};
    d('dodocoExport').onclick=()=>dodocoQuery('text'); d('dodocoCsv').onclick=()=>dodocoQuery('csv');
    d('dodocoDownload').onclick=()=>dodocoSave(dodoco.raw,dodoco.fileName||'log.txt','application/octet-stream');
    if(typeof roomLogMount==='function') roomLogMount(d('roomLogHost'));
    dodocoDrawFiles(); if(dodoco.fileName) dodocoDrawFile();
}
function dodocoDrawFiles(){ const host=d('dodocoFiles');if(!host)return;const day=v=>{const t=new Date(v);return Number.isFinite(t.getTime())?new Date(t.getTime()-t.getTimezoneOffset()*60000).toISOString().slice(0,10):'';};const dates=[...new Set(dodoco.files.map(f=>day(f.lastWrite)).filter(Boolean))].sort().reverse();const sel=d('dodocoFileDate');if(sel){if(dodoco.fileDate===undefined&&dodoco.files.length)dodoco.fileDate=dates.includes(day(Date.now()))?day(Date.now()):'';sel.innerHTML='<option value="">全部日期</option>'+dates.map(x=>`<option value="${x}">${x}${x===day(Date.now())?'（今天）':''}</option>`).join('');sel.value=dates.includes(dodoco.fileDate)?dodoco.fileDate:'';sel.onchange=()=>{dodoco.fileDate=sel.value;dodocoDrawFiles();};}const shown=dodoco.files.map((f,i)=>[f,i]).filter(([f])=>!sel||!sel.value||day(f.lastWrite)===sel.value);const size=n=>{n=Number(n)||0;return n>=1048576?(n/1048576).toFixed(1)+' MB':n>=1024?Math.round(n/1024)+' KB':n+' 字节';};host.innerHTML=shown.length?shown.map(([f,i])=>`<button class="dodoco-file${f.name===dodoco.fileName?' active':''}" data-file="${i}"><span class="dd-file-name">${de(f.name)}</span><small>${f.group?de(f.group)+' · ':''}${de(f.lastWrite||'')} · ${size(f.size)}</small></button>`).join(''):`<p class="dd-empty">${dodoco.files.length?'所选日期没有日志文件':'尚未读取文件列表'}</p>`; host.querySelectorAll('[data-file]').forEach(b=>b.onclick=()=>dodocoLoadFile(dodoco.files[Number(b.dataset.file)])); }
async function dodocoLoadFiles(){if(!dodocoReady())return;
 if(dodocoIsLocal()){const generation=dodoco.generation;try{dodocoStatus('正在读取本机日志列表…');const result=await window.bgiLocalBridge.execute('log.files',{});if(generation!==dodoco.generation)return;if(!Array.isArray(result?.files))throw new Error('本机返回的文件列表无效');dodoco.files=result.files;dodoco.fileDate=undefined;dodocoDrawFiles();const missing=Array.isArray(result.unavailable)&&result.unavailable.length?'；部分日志来源不可用：'+result.unavailable.join('、'):'';dodocoStatus(`本机返回 ${result.files.length} 个日志文件${missing}。`);}catch(e){dodocoStatus('本机日志列表未取得：'+e.message);}return;}
 const id=newRequestId(), uid=dodoco.target;const wait=dodocoWait('files',uid,id);try{await dodocoDispatch('log.requestFiles',{targetUid:uid,requestId:id});dodocoStatus('等待成员返回日志列表…');const files=await wait;dodoco.files=files;dodoco.fileDate=undefined;dodocoDrawFiles();dodocoStatus(files.length?`成员返回 ${files.length} 个日志文件。`:'成员返回空列表：可能未开启文件共享，也可能没有可共享的日志。');}catch(e){const p=dodoco.pending.get(id);if(p)dodocoFinish(p,e.message);wait.catch(()=>{});dodocoStatus('读取日志列表未完成：'+e.message);}}
function dodocoWorker() {
    if (dodoco.worker) return dodoco.worker;
    const worker=new Worker('dodoco-log-worker.js?v=20261029-1');dodoco.worker=worker;
    worker.onmessage=({data:m})=>{
        if(m.id!==dodoco.workerLatest)return;
        if(m.type==='progress'){if(dodoco.workerTimer)dodocoArmRegexTimeout(m.id);dodocoStatus(m.message);return;}
        clearTimeout(dodoco.workerTimer);dodoco.workerTimer=null;
        dodoco.fileBusy=false;
        if(m.type==='error'){dodocoStatus('日志处理未完成：'+m.message);return;}
        if(m.raw){dodoco.raw=m.raw;dodoco.fileCount=m.count;if(dodoco.regexTimedOut){dodoco.regexTimedOut=false;dodocoStatus('正则匹配超时，已停止；请修改表达式后重新搜索。');return;}dodocoStatus(`日志已读取，共 ${m.count} 行。`);if(dodoco.pendingSearch){const s=d('dodocoSearch');if(s){s.value=dodoco.pendingSearch;const r=d('dodocoRegex');if(r)r.checked=false;}dodoco.pendingSearch='';dodoco.page=0;}dodocoDrawFile();return;}
        if(m.blob){dodocoSave(m.blob, (m.blob.type.startsWith('text/csv')?'filtered.csv':'filtered.log'),m.blob.type);dodocoStatus(`已发起导出，共 ${m.count} 行。`);return;}
        const box=d('dodocoFileText');if(!box)return;dodoco.page=m.page;dodoco.fileCount=m.count;box.textContent=m.rows.join('\n');box.scrollTop=0;
        d('dodocoFileStatus').textContent=`${dodoco.fileSource?dodoco.fileSource+' · ':''}${dodoco.fileName} · 匹配 ${m.count} 行 · 第 ${m.page+1}/${Math.max(1,Math.ceil(m.count/1500))} 页（每页 1500 行）${m.truncated?' · 超长内容预览截断，完整内容可下载或导出':''}`;
        d('dodocoPrev').disabled=m.page===0;d('dodocoNext').disabled=(m.page+1)*1500>=m.count;
        d('dodocoExport').disabled=false;d('dodocoCsv').disabled=false;d('dodocoDownload').disabled=false;dodocoStatus('日志筛选完成。');
    };
    worker.onerror=()=>{clearTimeout(dodoco.workerTimer);dodoco.workerTimer=null;dodoco.fileBusy=false;dodocoStatus('日志处理线程失败，无法确认结果。');};return worker;
}
function dodocoArmRegexTimeout(id){clearTimeout(dodoco.workerTimer);dodoco.workerTimer=setTimeout(()=>{if(id!==dodoco.workerLatest)return;const raw=dodoco.raw,name=dodoco.fileName;dodoco.worker.terminate();dodoco.worker=null;dodoco.workerTimer=null;dodoco.regexTimedOut=true;dodocoLoadBlob(name,{file:raw});dodocoStatus('正则匹配超时，正在恢复日志…');},3000);}
function dodocoLoadBlob(name,payload,source=''){if(!dodoco.regexTimedOut)dodoco.fileSource=source;dodoco.fileName=name;dodoco.raw=null;dodoco.page=0;dodoco.fileBusy=true;const id=++dodoco.workerId;dodoco.workerLatest=id;dodocoStatus('正在后台处理日志…');dodocoWorker().postMessage({id,type:'load',...payload});}
async function dodocoLoadFile(file){if(!dodocoReady())return;
 if(dodocoIsLocal()){const generation=dodoco.generation,target=dodoco.target;try{if(typeof file.id!=='string'||!file.id)throw new Error('文件身份无效，请重新读取列表');const parts=[];let offset=0,total=null;for(;;){if(generation!==dodoco.generation||target!==dodoco.target)throw new Error('目标已切换，读取已中断');const chunk=await window.bgiLocalBridge.execute('log.chunk',{id:file.id,offset},30000);if(!chunk||chunk.offset!==offset||!Number.isInteger(chunk.total)||chunk.total<0||chunk.total>dodocoMaxBytes||(total!==null&&total!==chunk.total)||typeof chunk.base64!=='string'||chunk.base64.length>262144)throw new Error('本机日志分块无效');total=chunk.total;const bytes=Uint8Array.from(atob(chunk.base64),c=>c.charCodeAt(0));if(offset+bytes.length>total||(!bytes.length&&offset<total))throw new Error('本机日志分块不完整');parts.push(bytes);offset+=bytes.length;dodocoStatus(`正在读取本机日志：${offset} / ${total} 字节…`);if(chunk.done){if(offset!==total)throw new Error('本机日志分块不完整');break;}if(offset>=total)throw new Error('本机日志末块未确认');}if(generation!==dodoco.generation)return;dodocoLoadBlob(file.name,{file:new Blob(parts)});}catch(e){dodocoStatus('本机日志读取未完成：'+e.message);}return;}
 const id=newRequestId(),uid=dodoco.target,generation=dodoco.generation;const wait=dodocoWait('download',uid,id,30000);dodoco.pending.get(id).fileName=file.name;try{await dodocoDispatch('log.requestDownload',{targetUid:uid,requestId:id,fileName:file.name});dodocoStatus('正在等待并接收成员日志…');const chunks=await wait;if(generation!==dodoco.generation)return;dodocoLoadBlob(file.name,{chunks});}catch(e){const p=dodoco.pending.get(id);if(p)dodocoFinish(p,e.message);wait.catch(()=>{});dodocoStatus('日志读取未完成：'+e.message);}}
function dodocoOpenLocal(e){const file=e.target.files[0];if(!file)return;if(file.size>dodocoMaxBytes){dodocoStatus('文件超过网页 200 MB 处理上限。');return;}dodocoLoadBlob(file.name,{file});}
let dodocoSearchTimer;
function dodocoDrawFile(){clearTimeout(dodocoSearchTimer);dodocoSearchTimer=setTimeout(()=>dodocoQuery(),200);}
function dodocoQuery(exportKind=''){
    if(!dodoco.fileName||!dodoco.raw||dodoco.fileBusy)return;
    const from=d('dodocoFrom')?.value||'',to=d('dodocoTo')?.value||'';
    if(from&&to&&from>to){dodocoStatus('开始时间不能晚于结束时间。');return;}
    clearTimeout(dodoco.workerTimer);dodoco.workerTimer=null;const id=++dodoco.workerId;dodoco.workerLatest=id;dodocoStatus('正在后台筛选日志…');
    const regex=!!d('dodocoRegex')?.checked;if(regex)dodocoArmRegexTimeout(id);
    dodocoWorker().postMessage({id,type:'query',query:d('dodocoSearch')?.value||'',regex,from,to,level:d('dodocoFileLevel')?.value||'',instance:d('dodocoFileInstance')?.value||'',page:dodoco.page,export:exportKind});
}
function dodocoSave(value,name,type){const url=URL.createObjectURL(new Blob([value],{type})),a=document.createElement('a');a.href=url;a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);}
function dodocoDrawMembers(){
 const host=d('dodocoContent');if(!host)return;
 const rows=dodocoMembersList().filter(p=>p.playerUid&&!String(p.playerUid).startsWith('web_'));
 host.innerHTML='<section class="dd-card"><header class="dd-card-head"><div><h2>成员实时状态</h2><p class="dodoco-hint">来自成员上报；离线或断线时不把旧状态当作实时状态</p></div></header><div class="dd-member-grid">'+(rows.map(p=>{
  const available=p.isLocalDirect ? !!window.bgiLocalBridge?.available : roomReady && p.online===true;
  const bgi=available?({running:'运行中',stopped:'未运行',crashed:'异常'}[p.bgiStatus]||'未上报'):'状态待确认';
  const task=available?(p.taskStatusAvailable===false||typeof p.taskRunning!=='boolean'?'未上报':p.taskPaused===true?'已暂停':p.taskRunning?'执行中':'空闲'):'状态待确认';
  const value=x=>available && x ? de(x) : available?'未上报':'状态待确认';
  const presence=p.isLocalDirect?(available?'本机直连':'本机连接待确认'):!roomReady?'连接待确认':p.online?'在线':'离线';
  const kv=(k,v)=>`<div class="dd-kv"><span>${k}<i class="dd-sep">：</i></span><b>${v}</b></div>`;
  return `<article class="dd-member-card${available?'':' is-off'}"><header><strong>${de(p.playerName||p.playerUid)}</strong><span class="dd-pill${available?' ok':''}">${presence}</span></header>${kv('BGI',bgi)}${kv('任务状态',task)}${kv('当前任务',value(p.currentTaskName))}${kv('配置组',value(p.currentTaskGroupName))}${kv('路线',value(p.currentRouteDisplay||p.currentScriptRouteName))}${kv('锄地进度',value(p.autoHoeingProgress||p.hoeingStartMessage||p.scriptTaskProgress))}</article>`;
 }).join('')||'<p class="dd-empty">房间尚无可显示的成员</p>')+'</div></section>';
 if(typeof dodocoRenderReport==='function') dodocoRenderReport(host);
}
// 控制仅作用于本次观看的最新单帧；不监听控制端实体键盘，也不发送 BGI 任务命令。
const dodocoControlKeys = ['Esc','Enter','Space','Tab','F','W','S','A','D','F11','F12','Alt+F4','Win+D'];
const dodocoControl = { enabled:false, mode:'left', epoch:0, pending:null, lastSent:new Map(), usedFrames:new Set(), refreshTimer:null, cooldownTimer:null, refreshNotBefore:0, status:'本页控制默认关闭；点击画面可放大。' };
function dodocoControlFrame() { return (dodoco.frames.get(dodoco.target)||[])[dodoco.frameSelection.get(dodoco.target)||0]; }
function dodocoControlGeometry(g) {
    return !!g && Number.isInteger(g.left) && Number.isInteger(g.top)
        && Number.isInteger(g.width) && g.width>0 && g.width<=32768
        && Number.isInteger(g.height) && g.height>0 && g.height<=32768
        && typeof g.displayId==='string' && g.displayId.length>0 && g.displayId.length<=256
        && typeof g.layoutId==='string' && g.layoutId.length>0 && g.layoutId.length<=256;
}
// 手机全屏远控独立于只读放大；横屏回退只旋转显示，不改变观看会话或输入协议。
const dodocoRemote = {open:false,epoch:0,uid:'',visit:null,generation:0,landscape:true,rotated:false,zoom:1,
    frame:null,painted:null,nativeRoot:null,orientationOwned:false,pointers:new Map(),gesture:null};
function dodocoRemoteMobile(){return (typeof window.matchMedia==='function' && window.matchMedia('(pointer: coarse)').matches)
    || (Number.isFinite(window.innerWidth) && window.innerWidth<=700);}
function dodocoRemoteMarkup(){return `<dialog id="dodocoRemoteDialog" class="dd-remote-dialog" aria-label="全屏远程控制"><div id="dodocoRemoteRoot" class="dd-remote-root"><div id="dodocoRemoteSafe" class="dd-remote-safe"><div id="dodocoRemoteShell" class="dd-remote-shell"><header class="dd-remote-toolbar"><strong id="dodocoRemoteTitle"></strong><select id="dodocoRemoteMode" aria-label="鼠标方式"><option value="left">左键</option><option value="right">右键</option><option value="double">双击</option></select><button type="button" id="dodocoRemoteRefresh" title="操作面板展开或点按时暂停定时刷新；操作后自动更新，也可手动刷新">刷新</button><button type="button" id="dodocoRemoteKeyboard" aria-expanded="false">按键</button><details id="dodocoRemoteTools"><summary>工具</summary><div class="dd-remote-menu"><label><input id="dodocoRemoteLandscape" type="checkbox" checked>横屏布局</label><div class="dd-remote-zoom"><button type="button" id="dodocoRemoteZoomOut" aria-label="缩小画面">−</button><button type="button" id="dodocoRemoteFit">适应</button><button type="button" id="dodocoRemoteZoomIn" aria-label="放大画面">＋</button></div><button type="button" id="dodocoRemoteBgi">BGI 快捷功能</button><p>点按操作，拖动查看；每端两秒一次。横屏锁定不可用时自动使用旋转布局。</p></div></details><button type="button" id="dodocoRemoteExit">退出</button></header><div id="dodocoRemoteViewport" class="dd-remote-viewport"><div id="dodocoRemoteCanvas" class="dd-remote-canvas"><img id="dodocoRemoteImage" alt="可操作的最新桌面画面" draggable="false"></div></div><div id="dodocoRemoteKeys" class="dd-remote-keys" role="group" aria-label="固定键盘按钮" hidden>${dodocoControlKeys.map(key=>`<button type="button" data-remote-key="${key}"${key==='Alt+F4'?' class="dd-remote-danger"':''} title="${key==='Win+D'?'显示桌面':key==='Alt+F4'?'关闭当前窗口':key}">${key}</button>`).join('')}</div><p id="dodocoRemoteStatus" class="dd-remote-status" role="status" aria-live="polite"></p></div></div></div></dialog>`;}
function dodocoRemoteCurrent(){return dodocoRemote.open && dodocoRemote.uid===dodoco.target
    && dodocoRemote.visit===dodoco.screenVisit && dodocoRemote.generation===dodoco.generation;}
function dodocoRemoteLayout(){
    if(!dodocoRemoteCurrent())return;
    const safe=d('dodocoRemoteSafe'),shell=d('dodocoRemoteShell');if(!safe || !shell)return;
    const box=safe.getBoundingClientRect(),width=safe.clientWidth||window.innerWidth||box.width,
        height=safe.clientHeight||window.innerHeight||box.height;
    if(!Number.isFinite(width)||!Number.isFinite(height)||width<=0||height<=0)return;
    const rotated=dodocoRemote.landscape && height>width;
    const w=rotated?height:width,h=rotated?width:height;
    if(dodocoRemote.rotated!==rotated || shell.style.width!==`${w}px` || shell.style.height!==`${h}px`){
        dodocoRemote.gesture=null;dodocoRemote.pointers.clear();
    }
    dodocoRemote.rotated=rotated;shell.style.width=`${w}px`;shell.style.height=`${h}px`;
    shell.style.transform=`translate(-50%,-50%) rotate(${rotated?90:0}deg)`;
    dodocoRemoteSizeImage();
}
function dodocoRemoteSizeImage(){
    const viewport=d('dodocoRemoteViewport'),canvas=d('dodocoRemoteCanvas');if(!viewport||!canvas)return;
    const box=viewport.getBoundingClientRect();
    const width=viewport.clientWidth||(dodocoRemote.rotated?box.height:box.width),
        height=viewport.clientHeight||(dodocoRemote.rotated?box.width:box.height);
    if(width>0 && height>0){canvas.style.width=`${width*dodocoRemote.zoom}px`;canvas.style.height=`${height*dodocoRemote.zoom}px`;}
}
function dodocoRemoteZoom(value){
    const viewport=d('dodocoRemoteViewport'),box=viewport?.getBoundingClientRect();
    const w=viewport?.clientWidth||(dodocoRemote.rotated?box?.height:box?.width)||0,
        h=viewport?.clientHeight||(dodocoRemote.rotated?box?.width:box?.height)||0;
    const x=((viewport?.scrollLeft||0)+w/2)/dodocoRemote.zoom,y=((viewport?.scrollTop||0)+h/2)/dodocoRemote.zoom;
    dodocoRemote.zoom=Math.min(4,Math.max(1,value));dodocoRemote.gesture=null;
    dodocoRemoteSizeImage();
    if(viewport){viewport.scrollLeft=dodocoRemote.zoom===1?0:Math.max(0,x*dodocoRemote.zoom-w/2);
        viewport.scrollTop=dodocoRemote.zoom===1?0:Math.max(0,y*dodocoRemote.zoom-h/2);}
    dodocoRemoteSync();
}
function dodocoRemoteOrientation(){
    const orientation=globalThis.screen?.orientation;
    if(!dodocoRemote.landscape){if(dodocoRemote.orientationOwned){try{orientation?.unlock?.();}catch{}}dodocoRemote.orientationOwned=false;dodocoRemoteLayout();return;}
    if(!dodocoRemote.nativeRoot || typeof orientation?.lock!=='function')return;
    Promise.resolve().then(()=>orientation.lock('landscape')).then(()=>{
        if(!dodocoRemote.open || !dodocoRemote.landscape){try{orientation.unlock?.();}catch{}return;}
        dodocoRemote.orientationOwned=true;dodocoRemoteLayout();
    }).catch(()=>{dodocoRemoteLayout();});
}
function dodocoRemoteOpen(){
    if(dodocoRemote.open)return;
    if(!dodocoScreenAllowed())return;
    if(!dodocoControl.enabled && !dodocoPresenceStart())return;
    const dialog=d('dodocoRemoteDialog'),root=d('dodocoRemoteRoot');if(!dialog||!root)return;
    const epoch=++dodocoRemote.epoch;
    Object.assign(dodocoRemote,{open:true,uid:dodoco.target,visit:dodoco.screenVisit,generation:dodoco.generation,
        landscape:true,rotated:false,zoom:1,frame:null,painted:null,nativeRoot:null,gesture:null,sawPointer:false,notice:''});
    dodocoRemote.pointers.clear();
    const landscape=d('dodocoRemoteLandscape');if(landscape)landscape.checked=true;
    d('dodocoRemoteKeys').hidden=true;d('dodocoRemoteKeyboard').setAttribute('aria-expanded','false');
    d('dodocoRemoteTools').open=false;
    const viewport=d('dodocoRemoteViewport');if(viewport){viewport.scrollLeft=0;viewport.scrollTop=0;}
    document.body?.classList?.toggle('dd-remote-open',true);
    try{if(typeof dialog.showModal==='function')dialog.showModal();else{dialog.setAttribute('open','');dialog.open=true;}}
    catch{dodocoRemote.open=false;document.body?.classList?.toggle('dd-remote-open',false);dodocoControlClose('无法打开全屏视图，请刷新网页。');return;}
    dodocoRemoteLayout();dodocoControlSync();
    const request=root.requestFullscreen||root.webkitRequestFullscreen;
    if(typeof request==='function'){
        try{Promise.resolve(request.call(root)).then(()=>{
            if(!dodocoRemote.open || dodocoRemote.epoch!==epoch){
                // 同一元素已被新的控制视图重新打开时，旧 promise 不得退出新视图。
                if(dodocoRemoteCurrent()&&d('dodocoRemoteRoot')===root){dodocoRemote.nativeRoot=root;dodocoRemoteLayout();dodocoRemoteOrientation();}
                else dodocoRemoteLeaveNative(root);
                return;
            }
            dodocoRemote.nativeRoot=root;dodocoRemoteLayout();dodocoRemoteOrientation();
        }).catch(()=>{if(dodocoRemoteCurrent())dodocoRemoteLayout();});}catch{}
    }
}
function dodocoRemoteLeaveNative(root){
    if(!root || (document.fullscreenElement||document.webkitFullscreenElement)!==root)return;
    try{const exit=document.exitFullscreen||document.webkitExitFullscreen;Promise.resolve(exit?.call(document)).catch(()=>{});}catch{}
}
function dodocoRemoteClose(){
    if(!dodocoRemote.open)return;
    const root=dodocoRemote.nativeRoot||d('dodocoRemoteRoot'),dialog=d('dodocoRemoteDialog');
    dodocoRemote.open=false;dodocoRemote.epoch++;dodocoRemote.frame=null;dodocoRemote.painted=null;
    dodocoRemote.gesture=null;dodocoRemote.pointers.clear();dodocoRemote.nativeRoot=null;
    if(dodocoRemote.orientationOwned){try{globalThis.screen?.orientation?.unlock?.();}catch{}dodocoRemote.orientationOwned=false;}
    document.body?.classList?.toggle('dd-remote-open',false);
    d('dodocoRemoteShell')?.querySelectorAll('.modal-overlay').forEach(modal=>modal.remove?.());
    try{if(dialog?.open){if(typeof dialog.close==='function')dialog.close();else{dialog.open=false;dialog.removeAttribute?.('open');}}}catch{}
    dodocoRemoteLeaveNative(root);
}
function dodocoRemotePoint(image,event,rotated=dodocoRemote.rotated){
    const rect=image.getBoundingClientRect(),width=image.naturalWidth,height=image.naturalHeight;
    let boxW=rect.width,boxH=rect.height,x=event.clientX-rect.left,y=event.clientY-rect.top;
    if(rotated){boxW=rect.height;boxH=rect.width;x=event.clientY-rect.top;y=rect.left+rect.width-event.clientX;}
    if(![boxW,boxH,x,y,width,height].every(Number.isFinite)||boxW<=0||boxH<=0||width<=0||height<=0)return null;
    const scale=Math.min(boxW/width,boxH/height),w=width*scale,h=height*scale;
    x=(x-(boxW-w)/2)/w;y=(y-(boxH-h)/2)/h;
    return x>=0&&x<=1&&y>=0&&y<=1?{x,y}:null;
}
function dodocoRemoteReady(){return dodocoRemoteCurrent() && dodocoControl.enabled
    && dodocoRemote.painted===dodocoControlFrame() && !dodocoControlReason();}
function dodocoRemoteTap(event){
    if(!dodocoRemoteReady())return;
    dodocoRemote.notice='';
    const point=dodocoRemotePoint(d('dodocoRemoteImage'),event);
    if(!point){dodocoControl.status='留白区域不会发送输入。';dodocoControlSync();return;}
    dodocoControlSend(dodocoControl.mode,point.x,point.y);
}
function dodocoRemoteBind(){
    const dialog=d('dodocoRemoteDialog'),image=d('dodocoRemoteImage');if(!dialog||!image)return;
    d('dodocoRemoteExit').onclick=()=>dodocoControlClose('已退出全屏控制；已发送的输入无法撤回。');
    dialog.oncancel=event=>{event.preventDefault();dodocoControlClose('已退出全屏控制。');};
    dialog.onclose=()=>{if(dodocoRemote.open)dodocoControlClose('已退出全屏控制。');};
    d('dodocoRemoteRefresh').onclick=()=>{dodocoRemote.notice='';dodocoCapture();};
    d('dodocoRemoteMode').onchange=event=>{if(['left','right','double'].includes(event.target.value)){dodocoControl.mode=event.target.value;dodocoControlSync();}};
    d('dodocoRemoteKeyboard').onclick=()=>{const keys=d('dodocoRemoteKeys');keys.hidden=!keys.hidden;d('dodocoRemoteKeyboard').setAttribute('aria-expanded',String(!keys.hidden));};
    d('dodocoRemoteLandscape').onchange=event=>{dodocoRemote.landscape=event.target.checked;dodocoRemote.gesture=null;dodocoRemoteOrientation();dodocoRemoteLayout();};
    d('dodocoRemoteZoomIn').onclick=()=>dodocoRemoteZoom(dodocoRemote.zoom+.5);
    d('dodocoRemoteZoomOut').onclick=()=>dodocoRemoteZoom(dodocoRemote.zoom-.5);
    d('dodocoRemoteFit').onclick=()=>dodocoRemoteZoom(1);
    d('dodocoRemoteBgi').onclick=()=>{d('dodocoRemoteTools').open=false;dodocoBgiHotkey(d('dodocoRemoteShell'));};
    d('dodocoRemoteKeys').querySelectorAll('[data-remote-key]').forEach(button=>button.onclick=event=>{
        if(dodocoControlPointer(event)&&dodocoRemoteReady())dodocoControlSend('key',0,0,button.dataset.remoteKey);
    });
    image.oncontextmenu=event=>event.preventDefault();image.ondragstart=event=>event.preventDefault();
    image.onpointerdown=event=>{
        if(!dodocoRemoteCurrent())return;
        if(!event.compat)dodocoRemote.sawPointer=true;
        dodocoRemote.pointers.set(event.pointerId,true);
        if(d('dodocoRemoteTools').open){d('dodocoRemoteTools').open=false;dodocoRemote.gesture=null;return;}
        if(dodocoRemote.pointers.size!==1 || event.isPrimary===false || event.button!==0){dodocoRemote.gesture=null;return;}
        dodocoRemote.gesture={id:event.pointerId,x:event.clientX,y:event.clientY,started:performance.now(),frame:dodocoRemote.painted,epoch:dodocoRemote.epoch};
    };
    image.onpointermove=event=>{const g=dodocoRemote.gesture;if(g&&g.id===event.pointerId&&Math.hypot(event.clientX-g.x,event.clientY-g.y)>8)dodocoRemote.gesture=null;};
    image.onpointerup=event=>{
        const g=dodocoRemote.gesture;dodocoRemote.pointers.delete(event.pointerId);dodocoRemote.gesture=null;
        if(!g || g.id!==event.pointerId || g.epoch!==dodocoRemote.epoch || g.frame!==dodocoRemote.painted
            || dodocoRemote.pointers.size || Math.hypot(event.clientX-g.x,event.clientY-g.y)>8
            || performance.now()-g.started>800 || event.button!==0)return;
        event.preventDefault();dodocoRemoteTap(event);
    };
    image.onpointercancel=event=>{dodocoRemote.pointers.delete(event.pointerId);dodocoRemote.gesture=null;};
    image.onpointerleave=event=>{if(event.pointerType==='mouse'){dodocoRemote.pointers.delete(event.pointerId);dodocoRemote.gesture=null;}};
    // 无 PointerEvent 的浏览器也记录手势，不把滚动或长按产生的兼容 click 当成输入。
    const legacy=()=>typeof window.PointerEvent!=='function'&&!dodocoRemote.sawPointer;
    const touchActivity=()=>{dodocoRemote.ignoreMouseUntil=performance.now()+1000;};
    const legacyMouse=event=>legacy()&&!event.sourceCapabilities?.firesTouchEvents
        && performance.now()>=(dodocoRemote.ignoreMouseUntil||0);
    const adapted=(event,touch,id)=>({pointerId:id,button:0,isPrimary:true,pointerType:touch?'touch':'mouse',compat:true,
        clientX:(touch||event).clientX,clientY:(touch||event).clientY,preventDefault:()=>event.preventDefault()});
    image.onmousedown=event=>{if(legacyMouse(event)&&event.button===0)image.onpointerdown(adapted(event,null,'mouse'));};
    image.onmousemove=event=>{if(legacyMouse(event))image.onpointermove(adapted(event,null,'mouse'));};
    image.onmouseup=event=>{if(legacyMouse(event)&&event.button===0)image.onpointerup(adapted(event,null,'mouse'));};
    image.onmouseleave=()=>{if(legacy()){dodocoRemote.pointers.delete('mouse');dodocoRemote.gesture=null;}};
    image.ontouchstart=event=>{if(legacy()){touchActivity();for(const touch of event.changedTouches){const p=adapted(event,touch,touch.identifier);p.isPrimary=event.touches.length===1;image.onpointerdown(p);}}};
    image.ontouchmove=event=>{if(legacy()){touchActivity();for(const touch of event.changedTouches)image.onpointermove(adapted(event,touch,touch.identifier));}};
    image.ontouchend=event=>{if(legacy()){touchActivity();for(const touch of event.changedTouches)image.onpointerup(adapted(event,touch,touch.identifier));}};
    image.ontouchcancel=event=>{if(legacy()){touchActivity();for(const touch of event.changedTouches)image.onpointercancel(adapted(event,touch,touch.identifier));}};
    image.onclick=event=>event.preventDefault();
    d('dodocoRemoteViewport').onscroll=()=>{dodocoRemote.gesture=null;};
}
function dodocoRemoteSync(){
    const entry=d('dodocoRemoteEnter');if(entry)entry.disabled=!dodocoControl.enabled&&!!dodocoControlReason();
    if(!dodocoRemote.open)return;
    if(!dodocoRemoteCurrent() || !dodocoControl.enabled || !d('dodocoRemoteDialog')){dodocoRemoteClose();return;}
    const frame=dodocoControlFrame(),image=d('dodocoRemoteImage');
    if(frame && image && frame!==dodocoRemote.frame){
        dodocoRemote.frame=frame;dodocoRemote.painted=null;dodocoRemote.gesture=null;dodocoRemote.paintError=false;
        const epoch=dodocoRemote.epoch;
        image.onload=()=>{if(dodocoRemoteCurrent()&&epoch===dodocoRemote.epoch&&dodocoRemote.frame===frame){dodocoRemote.painted=frame;dodocoRemoteSync();}};
        image.onerror=()=>{if(dodocoRemoteCurrent()&&dodocoRemote.frame===frame){dodocoRemote.painted=null;dodocoRemote.paintError=true;dodocoControl.status='画面加载失败，请刷新。';dodocoRemoteSync();}};
        image.src='data:image/jpeg;base64,'+frame.jpegBase64;
    }
    const member=dodocoMembersList().find(p=>p.playerUid===dodoco.target);
    d('dodocoRemoteTitle').textContent=member?.playerName||dodoco.target;
    const reason=dodocoControlReason() || (dodocoRemote.painted!==frame?(dodocoRemote.paintError?'画面加载失败，请刷新。':'画面加载中…'):'');
    // 选择下一次鼠标方式不发送输入；刷新/解码/冷却时禁用原生 select 会关闭手机选择菜单。
    const mode=d('dodocoRemoteMode');if(mode.value!==dodocoControl.mode)mode.value=dodocoControl.mode;mode.disabled=false;
    d('dodocoRemoteKeys').querySelectorAll('[data-remote-key]').forEach(button=>button.disabled=!!reason);
    d('dodocoRemoteRefresh').disabled=!!dodocoControl.pending||dodoco.autoBusy||performance.now()<dodocoControl.refreshNotBefore;
    const status=d('dodocoRemoteStatus');status.textContent=dodocoRemote.notice||reason||dodocoControl.status;
    status.title=[dodocoRemote.notice,dodocoControl.status,reason].filter(Boolean).join(' ');
    d('dodocoRemoteZoomOut').disabled=dodocoRemote.zoom<=1;d('dodocoRemoteZoomIn').disabled=dodocoRemote.zoom>=4;
    dodocoRemoteSizeImage();
}
globalThis.window?.addEventListener?.('resize',dodocoRemoteLayout);
globalThis.window?.visualViewport?.addEventListener?.('resize',dodocoRemoteLayout);
for(const name of ['fullscreenchange','webkitfullscreenchange'])document.addEventListener?.(name,()=>{
    if(!dodocoRemote.open)return;
    const root=d('dodocoRemoteRoot'),active=document.fullscreenElement||document.webkitFullscreenElement;
    if(active===root){dodocoRemote.nativeRoot=root;dodocoRemoteLayout();}
    else if(dodocoRemote.nativeRoot)dodocoControlClose('系统全屏已退出，控制已关闭。');
});
function dodocoPresenceCurrent(p) {
    return !!p && dodocoControl.presence===p && p.visit===dodoco.screenVisit
        && p.generation===dodoco.generation && p.epoch===dodocoControl.epoch
        && p.uid===dodoco.target && p.roomCode===roomCode && p.local===dodocoIsLocal(p.uid)
        && (p.local ? p.bridge===window.bgiLocalBridge : p.connection===connection) && dodocoTargetReady(p.uid)
        && !document.hidden;
}
function dodocoControlReason() {
    const visit=dodoco.screenVisit, frame=dodocoControlFrame();
    if (dodocoControl.enabled && !dodocoPresenceCurrent(dodocoControl.presence)) return '控制来源已变化，请重新开启控制。';
    if (!dodoco.active || dodoco.tab!=='screen' || !visit) return '请进入桌面监控。';
    if (visit.expired || Math.max(performance.now()-visit.started,Date.now()-visit.wallStarted)>=dodocoScreenLimitMs) return dodocoScreenExpiredText;
    if (!dodoco.target || dodoco.target==='*') return '选择单个在线成员并刷新最新画面后，才能开启控制。';
    if (!dodocoTargetReady()) return '成员连接未就绪，无法控制。';
    if (d('dodocoZoomDialog')?.open) return '放大窗口仅供查看，请关闭后再控制主画面。';
    if ((dodoco.frameSelection.get(dodoco.target)||0)!==0) return '历史画面只供查看，请刷新最新画面。';
    if (!frame || frame.controlVisitId!==visit.id || frame.controlGeneration!==dodoco.generation
        || frame.controlRoomCode!==roomCode || frame.controlLocal!==dodocoIsLocal()
        || (frame.controlLocal ? frame.controlBridge!==window.bgiLocalBridge : frame.controlConnection!==connection)) return '请先刷新本次观看的最新画面。';
    if (frame.controlAllowed!==true) return '对方未开启“允许桌面控制”，请在对方助手本机开启；当前只能查看。';
    if (typeof frame.frameId!=='string' || !frame.frameId || frame.frameId.length>100 || !dodocoControlGeometry(frame.geometry)) return '此画面缺少有效控制信息，请刷新或更新对方助手。';
    if (dodocoControl.enabled && !dodocoControl.presence?.active) return '等待对方确认控制提示…';
    if (dodocoControl.enabled && Math.max(performance.now()-dodocoControl.presence.leaseStarted,Date.now()-dodocoControl.presence.leaseWallStarted)>=12000) return '控制提示租约已过期，请重新开启控制。';
    if (dodocoControl.pending) return '正在等待执行回执；自动刷新暂时暂停。';
    if (frame.controlUsed) return '此画面已经用于输入，请刷新新画面后再操作。';
    if (!Number.isFinite(frame.controlStarted) || !Number.isFinite(frame.controlWallStarted)
        || Math.max(performance.now()-frame.controlStarted,Date.now()-frame.controlWallStarted)>=15000) return '画面已超过 15 秒，请刷新新画面。';
    if (dodoco.autoBusy) return '正在刷新画面，请等待新画面。';
    if (performance.now()<dodocoControl.refreshNotBefore) return '等待桌面响应后刷新新画面。';
    const last=dodocoControl.lastSent.get(dodoco.target);
    if (last) { const left=2000-Math.max(performance.now()-last.started,Date.now()-last.wallStarted); if(left>0)return `每个成员两秒一次，请等待 ${(left/1000).toFixed(1)} 秒。`; }
    return '';
}
function dodocoControlSync() {
    const toggle=d('dodocoControlEnabled');
    if (!toggle || !dodoco.active || dodoco.tab!=='screen') return;
    const reason=dodocoControlReason(), enabled=dodocoControl.enabled;
    toggle.checked=enabled; toggle.disabled=!enabled && !!reason;
    const mode=d('dodocoControlMode'); if(mode){mode.value=dodocoControl.mode;mode.disabled=!enabled || !!reason;}
    d('dodocoControlKeys')?.querySelectorAll('[data-control-key]').forEach(b=>b.disabled=!enabled || !!reason);
    const status=d('dodocoControlStatus'); if(status)status.textContent=dodocoControl.status+(reason?' '+reason:'');
    const capture=d('dodocoCapture'); if(capture)capture.disabled=!!dodocoControl.pending || dodoco.autoBusy || !!dodoco.screenVisit?.expired || performance.now()<dodocoControl.refreshNotBefore;
    d('dodocoFrames')?.querySelectorAll('.dd-image-btn').forEach(b=>{
        b.classList.toggle('is-control',enabled); b.classList.toggle('is-control-wait',enabled && !!reason);
        b.setAttribute?.('aria-disabled',String(enabled && !!reason));
        b.title=enabled?(reason || '点击画面发送一次所选鼠标操作'):'点击放大';
    });
    const inlineKeys=d('dodocoControlKeys');if(inlineKeys)inlineKeys.hidden=dodocoRemoteMobile()&&!enabled;
    dodocoRemoteSync();
}
function dodocoControlCancel(p) {
    if (!p) return;
    // HttpListener 中 abort 只结束等待；必须另发不带该 signal 的独立取消操作。
    if (p.local) {
        p.bridge.execute('desktop.control.cancel',{requestId:p.id},3000,p.uid).catch(()=>{});
    } else if (roomReady) {
        dodocoDispatch('desktop.control.cancel',{roomCode:p.roomCode,sessionId:p.visit.id,targetUid:p.uid,controlId:p.controlId,requestId:p.id},p.connection).catch(()=>{});
    }
}
// 请求和回执分别关联控制代次；结束使用独立调用，不复用可能已中止的输入 signal。
function dodocoPresenceSend(p, action) {
    const request={requestId:dodocoScreenId(),controlId:p.id,targetUid:p.local?'local':p.uid,frameId:p.frameId,action};
    if(p.local && action!=='end')request.expiresAtUtc=new Date(Date.now()+3000).toISOString();
    if(action!=='end') {
        p.active=false;
        p.wait={id:request.requestId,action,started:performance.now(),wallStarted:Date.now()};
        clearTimeout(p.timeout);
        p.timeout=setTimeout(()=>{if(dodocoControl.presence===p && p.wait?.id===request.requestId)
            dodocoControlClose('等待对方确认控制提示超时，控制已关闭。');},3000);
    }
    const fail=error=>{if(action!=='end' && dodocoControl.presence===p && p.wait?.id===request.requestId)
        dodocoControlClose('控制提示未确认，控制已关闭：'+(error?.message||'请求失败'));};
    try {
        if(p.local) p.bridge.execute('desktop.control.presence',request,3000,p.uid)
            .then(result=>{if(action!=='end')dodocoPresenceResult(result,p);}).catch(fail);
        else dodocoDispatch('desktop.control.presence',{roomCode:p.roomCode,presence:request},p.connection).catch(fail);
    } catch(error) { fail(error); }
}
function dodocoPresenceStart() {
    const reason=dodocoControlReason();
    if(reason){dodocoControl.status=reason;dodocoControlSync();return false;}
    const uid=dodoco.target,local=dodocoIsLocal(uid);
    const p={id:dodocoScreenId(),uid,local,bridge:local?window.bgiLocalBridge:null,connection:local?null:connection,roomCode,
        frameId:dodocoControlFrame().frameId,visit:dodoco.screenVisit,generation:dodoco.generation,epoch:dodocoControl.epoch,active:false,wait:null};
    dodocoControl.presence=p;dodocoControl.enabled=true;dodocoControl.status='等待对方确认控制提示…';
    dodocoPresenceSend(p,'start');dodocoControlSync();return dodocoControl.presence===p;
}
function dodocoPresenceResult(result, expected=null) {
    const p=dodocoControl.presence,w=p?.wait;
    if(!p || !w || (p.local ? expected!==p : expected!==null)
        || result?.requestId!==w.id || result.controlId!==p.id
        || result.targetUid!==(p.local?'local':p.uid))return;
    if(!dodocoPresenceCurrent(p)){dodocoControlClose('控制来源已变化，控制已关闭。');return;}
    if(!dodocoScreenAllowed())return;
    if(Math.max(performance.now()-w.started,Date.now()-w.wallStarted)>=3000){dodocoControlClose('等待对方确认控制提示超时，控制已关闭。');return;}
    clearTimeout(p.timeout);p.wait=null;
    if(result.status!=='active'){dodocoControlClose('对方未确认控制提示，控制已关闭'+(result.message?'：'+result.message:'。'));return;}
    p.active=true;p.leaseStarted=w.started;p.leaseWallStarted=w.wallStarted;
    dodocoControl.status='控制已开启；输入前请确认最新画面。';
    clearTimeout(p.renewTimer);
    p.renewTimer=setTimeout(()=>{
        if(dodocoControl.presence!==p)return;
        if(!dodocoPresenceCurrent(p) || !dodocoScreenAllowed()){dodocoControlClose('连接或观看已结束，控制已关闭。');return;}
        if(Math.max(performance.now()-p.leaseStarted,Date.now()-p.leaseWallStarted)>=12000){dodocoControlClose('控制提示租约已过期，控制已关闭。');return;}
        dodocoPresenceSend(p,'renew');dodocoControlSync();
    },4000);
    dodocoControlSync();
}
function dodocoControlClose(message='本页控制已关闭；点击画面可放大。') {
    const presence=dodocoControl.presence;
    dodocoControl.presence=null;
    if(presence){clearTimeout(presence.timeout);clearTimeout(presence.renewTimer);dodocoPresenceSend(presence,'end');}
    dodocoRemoteClose();
    const p=dodocoControl.pending;
    dodocoControl.epoch++;
    dodocoControl.enabled=false; dodocoControl.pending=null; dodocoControl.status=message;
    clearTimeout(dodocoControl.refreshTimer); clearTimeout(dodocoControl.cooldownTimer);
    dodocoControl.refreshTimer=null; dodocoControl.cooldownTimer=null;
    // 已执行输入的 1100ms 响应间隔不能通过关闭控制或切换成员提前结束。
    if(p){clearTimeout(p.timer);p.controller?.abort();dodocoControlCancel(p);dodocoControl.status+=' 已发送的输入无法撤回；未确认结果不代表未执行。';}
    dodocoControlSync();
}
function dodocoControlTimeout(p) {
    if(dodocoControl.pending!==p)return;
    p.controller?.abort();dodocoControlCancel(p);
    dodocoControlResult({requestId:p.id,targetUid:p.commandTarget,status:'timeout'},p);
}
function dodocoControlResult(result, expectedPending=null) {
    const p=dodocoControl.pending;
    if (!p || (expectedPending && expectedPending!==p) || (p.local && expectedPending!==p)
        || result?.requestId!==p.id || result.targetUid!==p.commandTarget) return;
    // 网关 ACK/accepted 仍需等待终态；与输入服务一致，三秒后不再认领迟到回执。
    if (!['executed','denied','expired','cancelled','busy','rate_limited','error','timeout','unknown'].includes(result.status)) return;
    if (!dodocoPresenceCurrent(dodocoControl.presence)) { dodocoControlClose('控制来源已变化，控制已关闭。'); return; }
    if (p.visit!==dodoco.screenVisit || p.generation!==dodoco.generation || p.epoch!==dodocoControl.epoch
        || p.uid!==dodoco.target || !dodocoScreenAllowed()) return;
    if (result.status!=='timeout' && Math.max(performance.now()-p.started,Date.now()-p.wallStarted)>=3000) { dodocoControlTimeout(p); return; }
    clearTimeout(p.timer); dodocoControl.pending=null;
    const labels={denied:'对方拒绝输入',expired:'指令已过期',cancelled:'指令已取消',busy:'对方正忙',rate_limited:'操作间隔不足',error:'输入失败，结果未确认',timeout:'等待执行回执超时，结果未确认'};
    dodocoControl.status=result.status==='executed'?'系统已接受输入，不承诺游戏效果；等待新画面。':`${labels[result.status]||'输入结果未确认'}${result.message?'：'+result.message:''}；请以新画面确认。`;
    const delay=result.status==='executed'?1100:0;
    dodocoControl.refreshNotBefore=performance.now()+delay;
    clearTimeout(dodocoControl.refreshTimer);
    dodocoControl.refreshTimer=setTimeout(()=>{
        if(p.epoch!==dodocoControl.epoch || p.generation!==dodoco.generation || p.visit!==dodoco.screenVisit || p.uid!==dodoco.target)return;
        dodocoControl.refreshTimer=null;
        if(dodocoControl.enabled && dodocoScreenAllowed()) dodocoCapture();
    },delay);
    dodocoControlSync();
}
function dodocoControlPointer(event) { return event?.detail>0 && event.button===0; }
function dodocoControlPoint(image,event) {
    const rect=image.getBoundingClientRect(), width=image.naturalWidth, height=image.naturalHeight;
    if (![rect.left,rect.top,rect.width,rect.height,width,height,event.clientX,event.clientY].every(Number.isFinite)
        || rect.width<=0 || rect.height<=0 || width<=0 || height<=0) return null;
    // object-fit:contain 默认居中；使用 CSS 像素和实际图像内容区，不乘控制端 DPI。
    const scale=Math.min(rect.width/width,rect.height/height),w=width*scale,h=height*scale;
    const x=(event.clientX-rect.left-(rect.width-w)/2)/w,y=(event.clientY-rect.top-(rect.height-h)/2)/h;
    return x>=0 && x<=1 && y>=0 && y<=1 ? {x,y} : null;
}
function dodocoControlClick(event,image,uid,frame) {
    if(!dodocoControlPointer(event) || uid!==dodoco.target || frame!==(dodoco.frames.get(uid)||[])[0])return;
    const point=dodocoControlPoint(image,event);
    if(!point){dodocoControl.status='点击位于图像留白或画面尚未加载，未发送输入。';dodocoControlSync();return;}
    dodocoControlSend(dodocoControl.mode,point.x,point.y);
}
function dodocoControlSend(action,x=0,y=0,key='') {
    if(!dodocoScreenAllowed())return;
    if(dodocoControl.enabled && !dodocoPresenceCurrent(dodocoControl.presence)){dodocoControlClose('控制来源已变化，控制已关闭。');return;}
    const presence=dodocoControl.presence;
    if(presence?.active && Math.max(performance.now()-presence.leaseStarted,Date.now()-presence.leaseWallStarted)>=12000){dodocoControlClose('控制提示租约已过期，控制已关闭。');return;}
    const reason=dodocoControlReason();
    if(!dodocoControl.enabled || reason){dodocoControl.status=reason || '请先开启本页控制。';dodocoControlSync();return;}
    if (!['left','right','double','key'].includes(action) || !Number.isFinite(x) || !Number.isFinite(y)
        || (action==='key'?!dodocoControlKeys.includes(key):x<0 || x>1 || y<0 || y>1 || key!=='')) return;
    dodocoRemote.notice='';
    const frame=dodocoControlFrame(),uid=dodoco.target,id=dodocoScreenId(),local=dodocoIsLocal(uid);
    const command={requestId:id,targetUid:local?'local':uid,frameId:frame.frameId,controlId:dodocoControl.presence.id,action,x,y,key};
    // 本机浏览器与助手共享系统时钟；保留点击时的截止，不能在 HTTP 处理时重新开始三秒。
    if(local)command.expiresAtUtc=new Date(Date.now()+3000).toISOString();
    const p={id,uid,commandTarget:command.targetUid,visit:dodoco.screenVisit,generation:dodoco.generation,epoch:dodocoControl.epoch,
        roomCode,local,controlId:command.controlId,connection:local?null:connection,bridge:local?window.bgiLocalBridge:null,controller:null,timer:null,started:performance.now(),wallStarted:Date.now()};
    frame.controlUsed=true; dodocoControl.pending=p;
    dodocoControl.usedFrames.add(JSON.stringify([uid,frame.frameId]));
    if(dodocoControl.usedFrames.size>256)dodocoControl.usedFrames.delete(dodocoControl.usedFrames.values().next().value);
    dodocoControl.lastSent.set(uid,{started:p.started,wallStarted:p.wallStarted});
    dodocoControl.status='输入请求已发送，等待执行回执；转发确认不代表已经执行。';
    clearTimeout(dodocoControl.refreshTimer);dodocoControl.refreshTimer=null;
    clearTimeout(dodocoControl.cooldownTimer);dodocoControl.cooldownTimer=setTimeout(dodocoControlSync,2000);
    p.timer=setTimeout(()=>dodocoControlTimeout(p),3000);
    dodocoControlSync();
    const fail=e=>{if(dodocoControl.pending===p){dodocoControlCancel(p);dodocoControlResult({requestId:id,targetUid:p.commandTarget,status:'error',message:e.message},p);}};
    if(p.local){
        p.controller=new AbortController();
        p.bridge.execute('desktop.control.input',command,3000,uid,p.controller.signal)
            .then(result=>dodocoControlResult(result,p)).catch(fail);
    } else {
        dodocoDispatch('desktop.control.request',{roomCode:p.roomCode,command}).catch(fail);
    }
}
// 页面卸载与浏览器往返缓存也要清掉控制；没有实体键盘监听。
globalThis.window?.addEventListener?.('pagehide',()=>dodocoScreenLeave());
document.addEventListener?.('visibilitychange',()=>{if(document.hidden) dodocoControlClose('页面已隐藏，控制已关闭。');});
function dodocoStopAuto(){if(dodoco.autoTimer){clearInterval(dodoco.autoTimer);dodoco.autoTimer=null;}const checkbox=d('dodocoAuto');if(checkbox)checkbox.checked=false;}
// BGI 快捷功能复用耕地机原入口；它不是桌面按键模拟，也不依赖截图控制授权。
function dodocoBgiStatus(message){
 dodocoStatus(message);
 if(dodocoRemoteCurrent()){dodocoRemote.notice=message;dodocoRemoteSync();}
}
function dodocoBgiHotkey(host){
 let member;
 if(dodocoIsLocal()){
  const bridge=window.bgiLocalBridge,identity=bridge.localIdentity?.();
  if(bridge.available && identity?.uid && identity.clientInstanceId && identity.machineId)
   member=players.find(p=>p.playerUid===identity.uid && p.clientInstanceId===identity.clientInstanceId && p.machineId===identity.machineId);
 }else member=players.find(p=>p.playerUid===dodoco.target);
 if(!dodoco.active || dodoco.tab!=='screen' || !roomReady || !member?.online){dodocoBgiStatus('请先选择一位房间内的在线成员，再执行 BGI 快捷功能；本机直连需与房间中的执行端身份一致。');return;}
 if(!Array.isArray(member.hotkeys) || !member.hotkeys.length){dodocoBgiStatus('该成员尚未上报 BGI 快捷键，请等待状态更新。');return;}
 if(typeof showHotkeySelect!=='function'){dodocoBgiStatus('BGI 快捷功能入口尚未加载，请刷新网页。');return;}
 dodocoRemote.notice='';
 if(host && typeof host.appendChild==='function'){
  const epoch=dodocoRemote.epoch;
  showHotkeySelect(member.playerUid,member.playerName||member.playerUid,host,message=>{
   if(dodocoRemoteCurrent()&&dodocoRemote.epoch===epoch)dodocoBgiStatus(message);
  });
 }else showHotkeySelect(member.playerUid,member.playerName||member.playerUid);
}
function dodocoRenderScreen(){
 const host=d('dodocoContent');host.innerHTML=`<section class="dd-card"><header class="dd-card-head"><div><h2>桌面监控</h2><p class="dodoco-hint">${window.bgiLocalBridge?'本机画面直连获取，其他成员按房间共享规则获取':'按需请求成员桌面画面'}；每次进入最多观看 3 分钟，到期需退出后重新进入。历史只保存在本网页，每人最多 10 帧。画面只在你请求时由成员采集，不录制、不保存到服务器</p></div><div class="dd-actions"><label class="dd-check"><input id="dodocoAuto" type="checkbox">自动刷新</label><label class="dd-field dd-inline"><span>间隔</span><select id="dodocoInterval"><option value="1000">1 秒</option><option value="3000" selected>3 秒</option><option value="5000">5 秒</option><option value="10000">10 秒</option></select></label><button id="dodocoBgiHotkey" class="dd-btn" title="复用耕地机快捷键，可手动暂停或停止所选成员的 BGI；无需开启桌面点击控制">BGI 快捷功能</button><button id="dodocoClearFrames" class="dd-btn dd-btn-quiet">清空本页画面历史</button><button id="dodocoCapture" class="dd-btn dd-btn-primary">刷新画面</button></div></header><div class="dd-control"><div class="dd-control-row"><label class="dd-check"><input id="dodocoControlEnabled" type="checkbox" aria-describedby="dodocoControlStatus dodocoControlHint">启用本页控制</label><button type="button" id="dodocoRemoteEnter" class="dd-btn dd-btn-primary">全屏远控</button><label class="dd-field dd-inline" for="dodocoControlMode"><span>鼠标方式</span><select id="dodocoControlMode" disabled><option value="left">左键单击</option><option value="right">右键单击</option><option value="double">左键双击</option></select></label></div><p id="dodocoControlHint" class="dodoco-hint">输入作用于对方当前主屏桌面，每个成员两秒一次；需对方先开启“允许桌面控制”。BGI 或本机鼠标同时操作可能干扰，可手动暂停 BGI；不支持锁屏/UAC 安全桌面。不捕获你的实体键盘。</p><div id="dodocoControlKeys" class="dd-control-keys" role="group" aria-label="固定键盘按钮">${dodocoControlKeys.map(key=>`<button type="button" class="dd-btn${key==='Alt+F4'?' dd-btn-danger':''}" data-control-key="${key}" disabled>${key==='Alt+F4'?'Alt+F4（关闭窗口）':key==='Win+D'?'Win+D（显示桌面）':key}</button>`).join('')}</div><p id="dodocoControlStatus" class="dodoco-status" role="status" aria-live="polite"></p></div><div id="dodocoFrames"></div></section><dialog id="dodocoZoomDialog" class="dodoco-image-dialog"><header class="dd-zoom-head"><span class="dd-zoom-title"></span><span class="dodoco-hint">滚轮缩放 · 拖动查看</span><button id="dodocoZoomClose" class="dd-btn">关闭</button></header><div class="dd-zoom-stage"><img alt="放大的成员桌面截图"></div></dialog>${dodocoRemoteMarkup()}`;
 d('dodocoCapture').onclick=dodocoCapture;d('dodocoBgiHotkey').onclick=()=>dodocoBgiHotkey();d('dodocoZoomClose').onclick=()=>d('dodocoZoomDialog').close();
 d('dodocoZoomDialog').onclose=dodocoControlSync;
 d('dodocoRemoteEnter').onclick=dodocoRemoteOpen;dodocoRemoteBind();
 d('dodocoControlEnabled').onchange=e=>{if(!e.target.checked){dodocoControlClose();return;}if(dodocoPresenceStart() && dodocoRemoteMobile())dodocoRemoteOpen();};
 d('dodocoControlMode').onchange=e=>{if(['left','right','double'].includes(e.target.value))dodocoControl.mode=e.target.value;};
 d('dodocoControlKeys').querySelectorAll('[data-control-key]').forEach(b=>b.onclick=e=>{if(dodocoControlPointer(e))dodocoControlSend('key',0,0,b.dataset.controlKey);});
 d('dodocoZoomDialog').querySelector('.dd-zoom-stage').onwheel=e=>{e.preventDefault();const z=d('dodocoZoomDialog').querySelector('img');dodoco.zoom=Math.min(6,Math.max(1,(dodoco.zoom||1)*(e.deltaY<0?1.15:1/1.15)));z.style.transform=`scale(${dodoco.zoom})`;};
 d('dodocoClearFrames').onclick=()=>{dodocoControlClose('已清空画面历史，控制已关闭。');if(dodoco.target==='*')dodoco.frames.clear();else dodoco.frames.delete(dodoco.target);dodocoDrawFrames();dodocoStatus('已清空本网页所选成员的画面历史。');};
 const startAuto=()=>{dodocoStopAuto();if(!dodocoScreenAllowed())return;d('dodocoAuto').checked=true;dodoco.autoTimer=setInterval(dodocoAutoCapture,Math.max(1100,Number(d('dodocoInterval').value)));dodocoAutoCapture();};
 d('dodocoAuto').onchange=e=>{if(e.target.checked)startAuto();else dodocoStopAuto();};d('dodocoInterval').onchange=()=>{if(d('dodocoAuto').checked)startAuto();};dodocoDrawFrames();dodocoScreenAllowed();dodocoControlSync();
}
function dodocoDrawFrames(){
 const host=d('dodocoFrames');if(!host)return;
 const members=dodocoMembersList().filter(p=>p.playerUid && (dodoco.target==='*' ? !String(p.playerUid).startsWith('web_') : p.playerUid===dodoco.target));
 if(!members.length){host.innerHTML=`<p class="dd-empty">${dodoco.target?'未找到可显示的成员':'先在右上角选择成员，再刷新画面'}</p>`;return;}
 host.innerHTML=members.map((p,i)=>{const frames=dodoco.frames.get(p.playerUid)||[],sel=Math.max(0,Math.min(dodoco.frameSelection.get(p.playerUid)||0,frames.length-1)),frame=frames[sel];const ready=p.isLocalDirect?!!window.bgiLocalBridge?.available:roomReady&&p.online;const waiting=ready?'等待首帧，点击“刷新画面”获取':p.isLocalDirect?'本机连接未就绪':!roomReady?'房间连接未就绪':'成员离线，无法获取画面';return `<section class="dodoco-frame-card${ready?'':' is-off'}" data-frame-index="${i}"><header><h3>${de(p.playerName||p.playerUid)}</h3><span class="dd-pill${ready?' ok':''}">${p.isLocalDirect?(ready?'本机直连':'本机未就绪'):ready?'在线':'离线'}</span></header>${frame?`<p class="dodoco-hint">采集时间 ${de(frame.capturedAt||'未提供')}<span class="dd-frame-size"></span>${sel>0?' · 历史帧':''}</p><button class="dd-image-btn dodoco-frame-zoom" title="点击放大"><img class="dodoco-image" alt="${de(p.playerName||p.playerUid)}的桌面截图"></button>${frames.length>1?`<div class="dd-thumbs">${frames.map((f,j)=>`<button class="dd-thumb${j===sel?' active':''}" data-thumb="${j}" title="${de(f.capturedAt||`第 ${j+1} 帧`)}"><img alt=""><small>${de(String(f.capturedAt||`第 ${j+1} 帧`).slice(-8))}</small></button>`).join('')}</div>`:''}<div class="dd-frame-tools"><label class="dd-field dd-grow"><span>历史画面</span><select class="dodoco-frame-select">${frames.map((f,j)=>`<option value="${j}" ${j===sel?'selected':''}>${de(f.capturedAt||`第 ${j+1} 帧`)}</option>`).join('')}</select></label><button class="dodoco-frame-zoom dd-btn">放大</button><button class="dodoco-frame-save dd-btn">下载画面</button></div>`:`<p class="dd-empty">${waiting}</p>`}</section>`;}).join('');
 host.querySelectorAll('[data-frame-index]').forEach(section=>{
  const p=members[Number(section.dataset.frameIndex)],frames=dodoco.frames.get(p.playerUid)||[],sel=Math.max(0,Math.min(dodoco.frameSelection.get(p.playerUid)||0,frames.length-1)),frame=frames[sel];if(!frame)return;
  const src='data:image/jpeg;base64,'+frame.jpegBase64,img=section.querySelector('.dodoco-image'),imageButton=section.querySelector('.dd-image-btn');
  img.onload=()=>{const s=section.querySelector('.dd-frame-size');if(s)s.textContent=` · ${img.naturalWidth}×${img.naturalHeight}`;};img.src=src;
  const selectFrame=index=>{dodocoControlClose('已切换画面，控制已关闭；刷新最新画面后可重新开启。');dodoco.frameSelection.set(p.playerUid,index);dodocoDrawFrames();};
  section.querySelectorAll('[data-thumb]').forEach(t=>{t.querySelector('img').src='data:image/jpeg;base64,'+frames[Number(t.dataset.thumb)].jpegBase64;t.onclick=()=>selectFrame(Number(t.dataset.thumb));});
  section.querySelector('.dodoco-frame-select').onchange=e=>selectFrame(Number(e.target.value));
  section.querySelectorAll('.dodoco-frame-zoom').forEach(b=>b.onclick=e=>{
   if(b===imageButton && dodocoControl.enabled){e.preventDefault();dodocoControlClick(e,img,p.playerUid,frame);return;}
   dodocoControlClose('放大窗口仅供查看，控制已关闭。');
   const dlg=d('dodocoZoomDialog'),z=dlg.querySelector('img');z.src=src;dodoco.zoom=1;z.style.transform='';dlg.querySelector('.dd-zoom-title').textContent=`${p.playerName||p.playerUid} · ${frame.capturedAt||''}`;dlg.showModal();dodocoControlSync();
  });
  // 浏览器原始右键菜单和放大窗口都不会发送控制指令。
  imageButton.oncontextmenu=e=>{if(dodocoControl.enabled)e.preventDefault();};
  section.querySelector('.dodoco-frame-save').onclick=()=>{const a=document.createElement('a');a.href=src;a.download='member-'+p.playerUid+'-screenshot.jpg';a.click();};
 });
 dodocoControlSync();
}
async function dodocoCaptureOne(uid){
 if(!dodocoScreenAllowed())throw new Error(dodocoScreenExpiredText);
 if(dodocoControl.pending || performance.now()<dodocoControl.refreshNotBefore)throw new Error('正在等待输入结果或桌面响应，请稍候刷新。');
 const visit=dodoco.screenVisit,generation=dodoco.generation,local=dodocoIsLocal(uid),bridge=window.bgiLocalBridge,
  captureRoom=roomCode,captureConnection=local?null:connection,controlStarted=performance.now(),controlWallStarted=Date.now();
 const accept=(frame,frameId=frame.frameId)=>{
  if(visit!==dodoco.screenVisit || generation!==dodoco.generation || captureRoom!==roomCode || !dodocoScreenAllowed()
   || !dodocoTargetReady(uid) || local!==dodocoIsLocal(uid) || (local ? bridge!==window.bgiLocalBridge : captureConnection!==connection))throw new Error('本次画面请求已结束。');
  const history=dodoco.frames.get(uid)||[],previous=history.find(f=>f.frameId===frameId && f.controlVisitId===visit.id);
  const stored={...frame,frameId,controlVisitId:visit.id,controlGeneration:generation,controlRoomCode:captureRoom,controlConnection:captureConnection,controlLocal:local,controlBridge:local?bridge:null,
   controlStarted:previous?.controlStarted??controlStarted,controlWallStarted:previous?.controlWallStarted??controlWallStarted,
   controlUsed:dodocoControl.usedFrames.has(JSON.stringify([uid,frameId]))};
  history.unshift(stored);history.length=Math.min(history.length,10);dodoco.frames.set(uid,history);dodoco.frameSelection.set(uid,0);
  if(frame.controlAllowed!==true && dodocoControl.presence?.uid===uid)dodocoControlClose('对方未授权控制，控制已关闭。');
  return true;
 };
 if(local){const frame=await bridge.execute('screenshot.capture',{},20000,uid);if(!frame||typeof frame.jpegBase64!=='string'||!frame.jpegBase64||frame.jpegBase64.length>Math.ceil(1024*1024/3)*4)throw new Error('本机画面无效或超过大小限制');return accept(frame);}
 const id=`screen:${visit.id}:${dodocoScreenId()}`,wait=dodocoWait('frame',uid,id);
 try{await dodocoDispatch('screenshot.request',{targetUid:uid,requestId:id});return accept(await wait,id);}
 catch(e){const p=dodoco.pending.get(id);if(p)dodocoFinish(p,e.message);throw e;}
}
function dodocoAutoCapture(){
 // 交互期间推迟定时截图，避免按下到抬起之间换帧或禁用展开面板中的按键。
 // 空闲时仍自动刷新；手动刷新和输入终态后的取帧不受此处影响。
 if(dodocoRemoteCurrent() && (dodocoRemote.pointers.size || !d('dodocoRemoteKeys')?.hidden || d('dodocoRemoteTools')?.open))return;
 if(!dodoco.autoBusy && !dodocoControl.pending && performance.now()>=dodocoControl.refreshNotBefore)dodocoCapture();
}
async function dodocoCapture(){
 if(!dodocoScreenAllowed())return;
 if(dodocoControl.pending || performance.now()<dodocoControl.refreshNotBefore){dodocoControlSync();return;}
 const targets=dodoco.target==='*'?dodocoMembersList().filter(p=>p.playerUid && !String(p.playerUid).startsWith('web_') && (p.isLocalDirect ? window.bgiLocalBridge?.available : roomReady && p.online)).map(p=>p.playerUid):(dodocoSelectedOnline()?[dodoco.target]:[]);
 if(!targets.length){dodocoStatus('请选择在线成员；没有可请求的画面。');dodocoStopAuto();return;}
 dodoco.uncertain=false;
 if(dodoco.autoBusy)return;
 const generation=dodoco.generation,visit=dodoco.screenVisit,run={};dodoco.captureRun=run;dodoco.autoBusy=true;
 const button=d('dodocoCapture');if(button)button.disabled=true;dodocoControlSync();dodocoStatus('正在等待成员返回画面…');
 try{const results=await Promise.allSettled(targets.map(dodocoCaptureOne));if(generation!==dodoco.generation || visit!==dodoco.screenVisit || !dodocoScreenAllowed())return;const success=results.filter(x=>x.status==='fulfilled').length;dodocoDrawFrames();dodocoStatus(`已取得 ${success}/${targets.length} 个成员画面。${success<targets.length?'其余成员未响应、离线或采集失败；旧服务端缺少响应关联标识时也无法确认。':''}`);}
 finally{if(dodoco.captureRun===run){dodoco.autoBusy=false;dodoco.captureRun=null;const b=d('dodocoCapture');if(b)b.disabled=!dodocoScreenAllowed();dodocoControlSync();}}
}
