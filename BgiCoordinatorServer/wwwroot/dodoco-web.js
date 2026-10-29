// 房间内嘟嘟可：使用现有 v3 网关；转发确认不代表成员响应。
const dodoco = { levels:new Set(['ERR','WRN','INF','DBG']), follow:true, programScroll:false, active:false, tab:'live', target:'', subscribed:'', paused:false, live:[], files:[], lines:[], fileName:'', raw:null, page:0, fileCount:0, worker:null, workerId:0, workerLatest:0, workerTimer:null, regexTimedOut:false, fileBusy:false, uncertain:false, pending:new Map(), frame:null, frames:new Map(), frameSelection:new Map(), autoTimer:null, autoBusy:false, generation:0 };
const dodocoRoot = document.getElementById('dodocoView');
const d = id => document.getElementById(id);
const de = value => escapeHtml(value);
const dodocoMaxLines = 12000, dodocoMaxBytes = 200 * 1024 * 1024, dodocoMaxLiveChars = 8 * 1024 * 1024;

function showProduct(name) {
    const mistletoe = name === 'mistletoe' && !!d('mistletoeView');
    dodoco.active = name === 'dodoco';
    if(!dodoco.active) dodocoStopAuto();
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
function dodocoStatus(text) { const el = d('dodocoStatus'); if (el) el.textContent = (globalThis.window?.bgiLocalBridge && dodoco.target ? (dodoco.target==='*' ? '本机与服务器 · ' : dodocoIsLocal() ? (window.bgiLocalBridge.available?'本机直连 · ':'本机连接未就绪 · ') : '服务器中转 · ') : '') + (dodoco.uncertain ? '先前请求因断线结果未确认。' : '') + text; }
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
async function dodocoDispatch(name, payload) {
    try {
        const response = await connection.invoke('Dispatch', makeEnvelope('command', name, { roomCode, ...payload }));
        checkGatewayError(response);
    } catch (error) {
        if (!roomReady || connection?.state === 'Disconnected' || connection?.state === 'Reconnecting')
            throw new Error('连接已断开，结果未确认。');
        throw error;
    }
}
function dodocoCancelPending(message) {
    dodoco.generation++;
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
    dodocoRoot.innerHTML = `<div class="dd-head"><nav class="dodoco-tabs" aria-label="嘟嘟可功能"><button data-tab="live">实时日志</button><button data-tab="files">日志浏览</button><button data-tab="watch">异常监控</button><button data-tab="members">锄地数据</button><button data-tab="screen">桌面监控</button></nav><div class="dd-head-target"><label for="dodocoMember">成员</label><select id="dodocoMember">${dodocoMemberOptions()}</select></div></div><p id="dodocoStatus" class="dodoco-status dd-status" role="status">${dodoco.uncertain?'先前请求因断线结果未确认。':''}${dodocoIsLocal()?(window.bgiLocalBridge.available?'本机直连。':'本机连接未就绪。'):roomReady?'请选择在线成员。':'连接未就绪。'}</p><div id="dodocoContent" class="dd-body"></div>`;
    dodocoRoot.querySelectorAll('[data-tab]').forEach(b => { b.classList.toggle('active',b.dataset.tab===dodoco.tab); b.onclick=()=>{ dodocoStopAuto(); dodoco.tab=b.dataset.tab; if(dodoco.target==='*' && dodoco.tab!=='screen')dodoco.target=''; dodocoRender(); dodocoSyncSubscription(); }; });
    d('dodocoMember').onchange=e=>{ dodoco.target=e.target.value; window.bgiLocalBridge?.selectTarget(dodoco.target); dodocoCancelPending('成员已切换，原请求结果未确认。'); dodoco.files=[]; dodoco.fileDate=undefined; dodoco.lines=[]; dodoco.fileName=''; dodoco.raw=null; dodocoStopAuto(); clearTimeout(dodoco.workerTimer); dodoco.regexTimedOut=false; dodoco.workerLatest=++dodoco.workerId; if(dodoco.worker){dodoco.worker.terminate();dodoco.worker=null;} dodoco.fileBusy=false; dodocoRender(); dodocoSyncSubscription(); };
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
function dodocoStopAuto(){if(dodoco.autoTimer){clearInterval(dodoco.autoTimer);dodoco.autoTimer=null;}const checkbox=d('dodocoAuto');if(checkbox)checkbox.checked=false;}
function dodocoRenderScreen(){
 const host=d('dodocoContent');host.innerHTML=`<section class="dd-card"><header class="dd-card-head"><div><h2>桌面监控</h2><p class="dodoco-hint">${window.bgiLocalBridge?'本机画面直连获取，其他成员按房间共享规则获取':'按需请求成员桌面画面'}；历史只保存在本网页，每人最多 10 帧。画面只在你请求时由成员采集，不录制、不保存到服务器</p></div><div class="dd-actions"><label class="dd-check"><input id="dodocoAuto" type="checkbox">自动刷新</label><label class="dd-field dd-inline"><span>间隔</span><select id="dodocoInterval"><option value="1000">1 秒</option><option value="3000" selected>3 秒</option><option value="5000">5 秒</option><option value="10000">10 秒</option></select></label><button id="dodocoClearFrames" class="dd-btn dd-btn-quiet">清空本页画面历史</button><button id="dodocoCapture" class="dd-btn dd-btn-primary">刷新画面</button></div></header><div id="dodocoFrames"></div></section><dialog id="dodocoZoomDialog" class="dodoco-image-dialog"><header class="dd-zoom-head"><span class="dd-zoom-title"></span><span class="dodoco-hint">滚轮缩放 · 拖动查看</span><button id="dodocoZoomClose" class="dd-btn">关闭</button></header><div class="dd-zoom-stage"><img alt="放大的成员桌面截图"></div></dialog>`;
 d('dodocoCapture').onclick=dodocoCapture;d('dodocoZoomClose').onclick=()=>d('dodocoZoomDialog').close();
 d('dodocoZoomDialog').querySelector('.dd-zoom-stage').onwheel=e=>{e.preventDefault();const z=d('dodocoZoomDialog').querySelector('img');dodoco.zoom=Math.min(6,Math.max(1,(dodoco.zoom||1)*(e.deltaY<0?1.15:1/1.15)));z.style.transform=`scale(${dodoco.zoom})`;};
 d('dodocoClearFrames').onclick=()=>{if(dodoco.target==='*')dodoco.frames.clear();else dodoco.frames.delete(dodoco.target);dodocoDrawFrames();dodocoStatus('已清空本网页所选成员的画面历史。');};
 const startAuto=()=>{dodocoStopAuto();if(!dodoco.active||dodoco.tab!=='screen')return;d('dodocoAuto').checked=true;dodoco.autoTimer=setInterval(()=>{if(!dodoco.autoBusy)dodocoCapture();},Math.max(1100,Number(d('dodocoInterval').value)));dodocoCapture();};
 d('dodocoAuto').onchange=e=>{if(e.target.checked)startAuto();else dodocoStopAuto();};d('dodocoInterval').onchange=()=>{if(d('dodocoAuto').checked)startAuto();};dodocoDrawFrames();
}
function dodocoDrawFrames(){
 const host=d('dodocoFrames');if(!host)return;
 const members=dodocoMembersList().filter(p=>p.playerUid && (dodoco.target==='*' ? !String(p.playerUid).startsWith('web_') : p.playerUid===dodoco.target));
 if(!members.length){host.innerHTML=`<p class="dd-empty">${dodoco.target?'未找到可显示的成员':'先在右上角选择成员，再刷新画面'}</p>`;return;}
 host.innerHTML=members.map((p,i)=>{const frames=dodoco.frames.get(p.playerUid)||[],sel=Math.max(0,Math.min(dodoco.frameSelection.get(p.playerUid)||0,frames.length-1)),frame=frames[sel];const ready=p.isLocalDirect?!!window.bgiLocalBridge?.available:roomReady&&p.online;const waiting=ready?'等待首帧，点击“刷新画面”获取':p.isLocalDirect?'本机连接未就绪':!roomReady?'房间连接未就绪':'成员离线，无法获取画面';return `<section class="dodoco-frame-card${ready?'':' is-off'}" data-frame-index="${i}"><header><h3>${de(p.playerName||p.playerUid)}</h3><span class="dd-pill${ready?' ok':''}">${p.isLocalDirect?(ready?'本机直连':'本机未就绪'):ready?'在线':'离线'}</span></header>${frame?`<p class="dodoco-hint">采集时间 ${de(frame.capturedAt||'未提供')}<span class="dd-frame-size"></span>${sel>0?' · 历史帧':''}</p><button class="dd-image-btn dodoco-frame-zoom" title="点击放大"><img class="dodoco-image" alt="${de(p.playerName||p.playerUid)}的桌面截图"></button>${frames.length>1?`<div class="dd-thumbs">${frames.map((f,j)=>`<button class="dd-thumb${j===sel?' active':''}" data-thumb="${j}" title="${de(f.capturedAt||`第 ${j+1} 帧`)}"><img alt=""><small>${de(String(f.capturedAt||`第 ${j+1} 帧`).slice(-8))}</small></button>`).join('')}</div>`:''}<div class="dd-frame-tools"><label class="dd-field dd-grow"><span>历史画面</span><select class="dodoco-frame-select">${frames.map((f,j)=>`<option value="${j}" ${j===sel?'selected':''}>${de(f.capturedAt||`第 ${j+1} 帧`)}</option>`).join('')}</select></label><button class="dodoco-frame-zoom dd-btn">放大</button><button class="dodoco-frame-save dd-btn">下载画面</button></div>`:`<p class="dd-empty">${waiting}</p>`}</section>`;}).join('');
 host.querySelectorAll('[data-frame-index]').forEach(section=>{const p=members[Number(section.dataset.frameIndex)],frames=dodoco.frames.get(p.playerUid)||[],sel=Math.max(0,Math.min(dodoco.frameSelection.get(p.playerUid)||0,frames.length-1)),frame=frames[sel];if(!frame)return;const src='data:image/jpeg;base64,'+frame.jpegBase64;const img=section.querySelector('.dodoco-image');img.src=src;img.onload=()=>{const s=section.querySelector('.dd-frame-size');if(s)s.textContent=` · ${img.naturalWidth}×${img.naturalHeight}`;};section.querySelectorAll('[data-thumb]').forEach(t=>{t.querySelector('img').src='data:image/jpeg;base64,'+frames[Number(t.dataset.thumb)].jpegBase64;t.onclick=()=>{dodoco.frameSelection.set(p.playerUid,Number(t.dataset.thumb));dodocoDrawFrames();};});section.querySelector('.dodoco-frame-select').onchange=e=>{dodoco.frameSelection.set(p.playerUid,Number(e.target.value));dodocoDrawFrames();};section.querySelectorAll('.dodoco-frame-zoom').forEach(b=>b.onclick=()=>{const dlg=d('dodocoZoomDialog'),z=dlg.querySelector('img');z.src=src;dodoco.zoom=1;z.style.transform='';dlg.querySelector('.dd-zoom-title').textContent=`${p.playerName||p.playerUid} · ${frame.capturedAt||''}`;dlg.showModal();});section.querySelector('.dodoco-frame-save').onclick=()=>{const a=document.createElement('a');a.href=src;a.download='member-'+p.playerUid+'-screenshot.jpg';a.click();};});
}
async function dodocoCaptureOne(uid){
 if(dodocoIsLocal(uid)){const frame=await window.bgiLocalBridge.execute('screenshot.capture',{},20000,uid);if(!frame||typeof frame.jpegBase64!=='string'||!frame.jpegBase64||frame.jpegBase64.length>Math.ceil(1024*1024/3)*4)throw new Error('本机画面无效或超过大小限制');const history=dodoco.frames.get(uid)||[];history.unshift(frame);history.length=Math.min(history.length,10);dodoco.frames.set(uid,history);dodoco.frameSelection.set(uid,0);return true;}
 const id=newRequestId(),wait=dodocoWait('frame',uid,id);try{await dodocoDispatch('screenshot.request',{targetUid:uid,requestId:id});const frame=await wait;const history=dodoco.frames.get(uid)||[];history.unshift(frame);history.length=Math.min(history.length,10);dodoco.frames.set(uid,history);dodoco.frameSelection.set(uid,0);return true;}catch(e){const p=dodoco.pending.get(id);if(p)dodocoFinish(p,e.message);throw e;}
}
async function dodocoCapture(){
 const targets=dodoco.target==='*'?dodocoMembersList().filter(p=>p.playerUid && !String(p.playerUid).startsWith('web_') && (p.isLocalDirect ? window.bgiLocalBridge?.available : roomReady && p.online)).map(p=>p.playerUid):(dodocoSelectedOnline()?[dodoco.target]:[]);
 if(!targets.length){dodocoStatus('请选择在线成员；没有可请求的画面。');dodocoStopAuto();return;}
    dodoco.uncertain=false;
 if(dodoco.autoBusy)return;const generation=dodoco.generation;dodoco.autoBusy=true;const button=d('dodocoCapture');if(button)button.disabled=true;dodocoStatus('正在等待成员返回画面…');
 try{const results=await Promise.allSettled(targets.map(dodocoCaptureOne));if(generation!==dodoco.generation || !dodoco.active || dodoco.tab!=='screen')return;const success=results.filter(x=>x.status==='fulfilled').length;dodocoDrawFrames();dodocoStatus(`已取得 ${success}/${targets.length} 个成员画面。${success<targets.length?'其余成员未响应、离线或采集失败；旧服务端缺少响应关联标识时也无法确认。':''}`);}
 finally{dodoco.autoBusy=false;const b=d('dodocoCapture');if(b)b.disabled=false;}
}
