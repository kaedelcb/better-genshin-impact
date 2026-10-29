// 异常监控和日报均读取成员的真实数据；请求结果由 dodocoRequest 关联。
const watchState = { target:'', rules:[], settings:null, dates:[], records:[], date:'', offset:0, total:0, cursors:[null], page:0, nextCursor:null, keyword:'', level:'', ruleId:'', searchEpoch:0, editing:null, frames:[], record:null };
const reportState = { target:'', dates:[], date:'', result:null };
const watchAlerts = { timer:null, token:0, target:'', seen:new Set(), initialized:false };
function watchIsLocal() { return typeof dodocoIsLocal === 'function' && dodocoIsLocal(); }
function watchTargetReady() { return typeof dodocoTargetReady === 'function' ? dodocoTargetReady() : roomReady; }
function watchCurrent() { return dodoco.active && dodoco.tab === 'watch' && watchState.target === dodoco.target; }
function watchStatus(message) { if (watchCurrent()) dodocoStatus(message); }
function watchStopAlerts(message) {
    clearTimeout(watchAlerts.timer);watchAlerts.timer=null;watchAlerts.token++;watchAlerts.seen.clear();watchAlerts.initialized=false;
    const button=d('watchAlertToggle'),state=d('watchAlertStatus');if(button)button.textContent='开启网页告警监视';if(state)state.textContent=message||'网页告警监视未开启。';
}
/** 修改类操作（规则、设置、清理记录）只接受本人凭据：本机直连即本人；远程附带这个浏览器为该成员助手保存的授权凭据。 */
const WATCH_OWNER_OPS = new Set(['watch.rule.upsert','watch.rule.delete','watch.settings','watch.clear']);
function watchOwnerArgs() { return typeof mtOwnerArgsForUid === 'function' ? mtOwnerArgsForUid(dodoco.target) : null; }
function watchCanEdit() { return watchIsLocal() || !!watchOwnerArgs(); }
async function watchMutate(operation, args) {
    if (!watchIsLocal()) {
        const owner = watchOwnerArgs();
        if (!owner) throw new Error('只能修改自己的嘟嘟可；请先在下方授权此浏览器');
        args = { ...args, owner };
    }
    try { return await dodocoRequest(operation, args); }
    catch (e) { if (/只能修改自己的嘟嘟可|owner_required/.test(e.message) && typeof mtSaveCredential === 'function') { const p = players.find(x => x.playerUid === dodoco.target); if (p?.clientInstanceId) mtSaveCredential({ instanceId:p.clientInstanceId }, null); } throw e; }
}
function watchOwnerNotice() {
    if (watchCanEdit()) return '';
    return `<section class="dd-card dd-owner-note"><header class="dd-card-head"><div><h3>只读查看</h3><p class="dodoco-hint">共享开关只开放查看。修改规则、设置或清理记录只能由本人进行：在你自己的助手「设置 → 网页授权」生成授权码，填到这里绑定此浏览器。同一台电脑从助手「本机网页」打开免授权。</p></div></header><div class="dd-filters"><label class="dd-field"><span>授权码</span><input id="watchOwnerCode" inputmode="numeric" maxlength="9" placeholder="0000 0000"></label><button id="watchOwnerGo" class="dd-btn dd-align-end">这是我的助手，授权此浏览器</button></div></section>`;
}
function watchBindOwner() {
    const go = d('watchOwnerGo'); if (!go) return;
    go.onclick = async () => {
        const code = (d('watchOwnerCode').value || '').replace(/\D/g, '');
        if (code.length !== 8) { watchStatus('授权码是 8 位数字。'); return; }
        go.disabled = true;
        try { await mtAuthorizeUid(dodoco.target, code); watchStatus('已授权此浏览器，可以修改自己的规则和设置。'); watchDraw(); }
        catch (e) { watchStatus('授权未完成：' + e.message); go.disabled = false; }
    };
}function dodocoRenderWatch() {
    if (watchState.target !== dodoco.target) { watchStopAlerts(); Object.assign(watchState,{ target:dodoco.target, rules:[], settings:null, dates:[], records:[], date:'', offset:0, total:0, cursors:[null], page:0, nextCursor:null, keyword:'', level:'', ruleId:'', editing:null, frames:[], record:null }); }
    d('dodocoContent').innerHTML = `<section class="dd-card dd-watch-bar"><header class="dd-card-head"><div><h2>异常监控</h2><p class="dodoco-hint">${watchIsLocal()?'本机直连：操作作用于本机助手':'需成员在 PC 助手中允许远程访问；规则、设置和记录都作用于所选成员'}</p></div><div class="dd-actions"><button id="watchAlertToggle" class="dd-btn">${watchAlerts.timer?'停止网页告警监视':'开启网页告警监视'}</button><button id="watchRefresh" class="dd-btn dd-btn-primary">读取规则与设置</button></div></header><div class="dd-alert-opts"><label class="dd-check"><input id="watchAlertSound" type="checkbox" ${watchAlerts.sound!==false?'checked':''}>提示音</label><label class="dd-check"><input id="watchAlertNotify" type="checkbox" ${watchAlerts.notify?'checked':''}>浏览器通知</label></div><p id="watchAlertStatus" class="dd-alert-line" role="status" aria-live="polite">${watchAlerts.timer?'网页告警监视中。':'网页告警监视未开启；开启后每 15 秒检查最近 100 条记录，仅在本页显示新告警。'}</p></section><div id="watchBody"><p class="dd-empty dd-card">点击“读取规则与设置”后，在这里管理监控规则、异常记录和诊断包</p></div>`;
    d('watchRefresh').onclick=watchLoad;
    d('watchAlertSound').onchange=e=>{watchAlerts.sound=e.target.checked;};
    d('watchAlertNotify').onchange=async e=>{watchAlerts.notify=e.target.checked;if(!e.target.checked)return;if(!('Notification' in window)){e.target.checked=watchAlerts.notify=false;watchStatus('此浏览器不支持系统通知。');return;}if(Notification.permission==='default')await Notification.requestPermission();if(Notification.permission!=='granted'){e.target.checked=watchAlerts.notify=false;watchStatus('浏览器未允许通知；可在地址栏的网站权限中开启。');}};
    d('watchAlertToggle').onclick=()=>watchAlerts.timer?watchStopAlerts('网页告警监视已停止。'):watchStartAlerts();
    if (watchState.settings) watchDraw();
}
function watchBeep(){try{const ctx=new (window.AudioContext||window.webkitAudioContext)(),o=ctx.createOscillator(),g=ctx.createGain();o.type='sine';o.frequency.value=880;g.gain.setValueAtTime(.0001,ctx.currentTime);g.gain.exponentialRampToValueAtTime(.2,ctx.currentTime+.02);g.gain.exponentialRampToValueAtTime(.0001,ctx.currentTime+.45);o.connect(g).connect(ctx.destination);o.start();o.stop(ctx.currentTime+.5);setTimeout(()=>ctx.close(),800);}catch{}}
function watchNotify(record,count,settings){
    if(settings?.muteAll)return;
    if(d('watchAlertSound')?.checked)watchBeep();
    if(d('watchAlertNotify')?.checked&&'Notification' in window&&Notification.permission==='granted'){try{new Notification(`嘟嘟可 · ${record.ruleName}${count>1?`（共 ${count} 条）`:''}`,{body:`${record.time} ${record.message}`.slice(0,180),tag:'dodoco-watch'});}catch{}}
}
function watchStartAlerts() {
    if(!dodocoReady())return;watchStopAlerts();watchAlerts.target=dodoco.target;const token=++watchAlerts.token;
    d('watchAlertToggle').textContent='停止网页告警监视';d('watchAlertStatus').textContent='正在建立告警监视基线…';
    const poll=async()=>{
        if(token!==watchAlerts.token)return;
        if(!watchCurrent()||!watchTargetReady()||watchAlerts.target!==dodoco.target){watchStopAlerts('已离开页面或连接中断，告警监视已停止。');return;}
        try {
            const state=await dodocoRequest('watch.read');
            if(token!==watchAlerts.token)return;if(!watchCurrent()){watchStopAlerts();return;}
            if(!Array.isArray(state?.dates)||!Array.isArray(state?.rules))throw new Error('成员返回的监控数据无效');
            const date=state.dates[0];
            if(date){
                const result=await dodocoRequest('watch.records',{date,limit:100});
                if(token!==watchAlerts.token)return;if(!watchCurrent()){watchStopAlerts();return;}
                if(!Array.isArray(result?.records))throw new Error('成员返回的记录无效');
                const alerts=new Set(state.rules.filter(r=>r.enabled && r.alert).map(r=>r.id));
                const key=r=>`${r.time}|${r.ruleId}|${r.sourceFile||''}|${r.fileOffset||0}|${r.message||''}`;
                const fresh=result.records.filter(r=>alerts.has(r.ruleId)&&!watchAlerts.seen.has(key(r)));
                for(const record of result.records)watchAlerts.seen.add(key(record));
                if(watchAlerts.seen.size>1000)watchAlerts.seen=new Set(result.records.map(key));
                if(watchAlerts.initialized&&fresh.length){const latest=fresh[0],text=`新告警 ${fresh.length} 条：${latest.time} · ${latest.ruleName} · ${latest.message}`;d('watchAlertStatus').textContent=text;d('watchAlertStatus').classList.add('is-alert');watchNotify(latest,fresh.length,state.settings);}
                else if(!watchAlerts.initialized)d('watchAlertStatus').textContent='网页告警监视中；仅提示建立基线后的新告警。';
            } else if(!watchAlerts.initialized)d('watchAlertStatus').textContent='网页告警监视中，成员当前没有异常记录。';
            watchAlerts.initialized=true;
        } catch(e){if(token===watchAlerts.token){watchStopAlerts('告警监视未能继续：'+e.message);return;}}
        if(token===watchAlerts.token)watchAlerts.timer=setTimeout(poll,15000);
    };
    watchAlerts.timer=setTimeout(poll,0);
}
async function watchLoad() {
    const target=dodoco.target; watchStatus('正在等待成员返回异常监控数据…');
    try {
        const result=await dodocoRequest('watch.read'); if(!watchCurrent() || target!==dodoco.target)return;
        if(!result || !Array.isArray(result.rules) || !result.settings || !Array.isArray(result.dates)) throw new Error('成员返回的数据格式无效');
        Object.assign(watchState,{rules:result.rules,settings:result.settings,dates:result.dates});
        if(watchState.ruleId && !watchState.rules.some(x=>x.id===watchState.ruleId)) watchState.ruleId='';
        if(!watchState.dates.includes(watchState.date)) watchState.date=watchState.dates[0]||'';
        watchDraw(); watchStatus('已读取成员的规则和设置。');
        if(watchState.date) await watchRecords(0);
    } catch(e) { watchStatus('读取失败：'+e.message); }
}
function watchDraw() {
    const root=d('watchBody'); if(!root)return;
    const s=watchState.settings, r=watchState.editing || {id:'',name:'',pattern:'',note:'',minLevel:'DBG',enabled:true,isRegex:false,alert:false,snapshot:false};
    const selected=(value,current)=>value===current?' selected':'';
    const canEdit=watchCanEdit(), ro=canEdit?'':' disabled';
    root.innerHTML=watchOwnerNotice()+`<div class="dd-watch-grid"><div class="dd-col">
    <section class="dd-card"><header class="dd-card-head"><div><h3>异常记录</h3><p class="dodoco-hint">搜索覆盖所选日期；清理只删除该成员当天的异常记录，日志和事发快照保留</p></div></header>
    <div class="dd-filters"><label class="dd-field"><span>日期</span><select id="watchDate"><option value="">选择日期</option>${watchState.dates.length>1?`<option value="*"${selected('*',watchState.date)}>全部日期（${watchState.dates.length} 天）</option>`:''}${watchState.dates.map(x=>`<option value="${de(x)}"${selected(x,watchState.date)}>${de(x)}</option>`).join('')}</select></label><label class="dd-field dd-grow"><span>关键词</span><input id="watchFilter" type="search" maxlength="256" placeholder="搜索所选日期" value="${de(watchState.keyword)}"></label><label class="dd-field"><span>规则</span><select id="watchRecordRule"><option value="">全部规则</option>${watchState.rules.map(x=>`<option value="${de(x.id)}"${selected(x.id,watchState.ruleId)}>${de(x.name)}</option>`).join('')}</select></label><label class="dd-field"><span>级别</span><select id="watchRecordLevel"><option value="">全部级别</option>${['DBG','INF','WRN','ERR'].map(x=>`<option${selected(x,watchState.level)}>${x}</option>`).join('')}</select></label><button id="watchLoadRecords" class="dd-btn dd-btn-primary dd-align-end">搜索记录</button></div>
    <div class="dd-result-bar"><span id="watchPage" class="dodoco-status"></span><div class="dd-actions"><span class="dd-pager"><button id="watchPrev" class="dd-btn dd-btn-icon" aria-label="上一页" title="上一页">‹</button><button id="watchNext" class="dd-btn dd-btn-icon" aria-label="下一页" title="下一页">›</button></span><button id="watchClearRecords" class="dd-btn dd-btn-danger"${ro}>${watchState.date==='*'?'清理全部日期记录':'清理所选日期记录'}</button></div></div>
    <div id="watchRecords" class="dd-record-list"></div><div id="watchFrames"></div></section>
    <section class="dd-card"><header class="dd-card-head"><div><h3>导出诊断包</h3><p class="dodoco-hint">按成员电脑的本地时间填写；${watchIsLocal()?'本机直连无需远程共享':'需开启远程访问及日志文件共享'}；最多 7 天，包最大 8 MB</p></div></header><div class="dd-filters"><label class="dd-field dd-grow"><span>开始</span><input id="diagnosticStart" type="datetime-local"></label><label class="dd-field dd-grow"><span>结束</span><input id="diagnosticEnd" type="datetime-local"></label><span class="dd-actions dd-align-end"><button id="diagnosticNow" class="dd-btn" title="10 分钟前到现在">现在</button><button id="diagnosticDay" class="dd-btn" title="今天 00:00 到现在">全天</button></span><button id="diagnosticDownload" class="dd-btn dd-btn-primary dd-align-end">生成并下载诊断包</button></div></section>
    </div><div class="dd-col">
    <section class="dd-card"><header class="dd-card-head"><div><h3>监控规则</h3><p class="dodoco-hint">共 ${watchState.rules.length} 条</p></div><button id="watchNewRule" class="dd-btn"${ro}>新建规则</button></header><div class="dodoco-rule-list">${watchState.rules.length ? watchState.rules.map((x,i)=>`<div class="dd-rule${x.enabled?'':' is-off'}"><div class="dd-rule-main"><strong>${de(x.name||'未命名规则')}</strong><div class="dd-tags"><span class="dd-pill${x.enabled?' ok':''}">${x.enabled?'启用':'停用'}</span><span class="dd-pill">${de(x.minLevel)} 及以上</span>${x.alert?'<span class="dd-pill warn">告警</span>':''}${x.snapshot?'<span class="dd-pill">快照</span>':''}</div><small>${de(x.isRegex?'正则':'关键词')}：${de(x.pattern||'仅级别匹配')}</small></div><div class="dd-rule-actions"><div class="dd-rule-toggles"><label title="启用"><input type="checkbox" data-toggle="enabled" data-rule="${i}" ${x.enabled?'checked':''}${ro}>启用</label><label title="命中告警"><input type="checkbox" data-toggle="alert" data-rule="${i}" ${x.alert?'checked':''}${ro}>告警</label><label title="保存快照"><input type="checkbox" data-toggle="snapshot" data-rule="${i}" ${x.snapshot?'checked':''}${ro}>快照</label></div><button class="dd-btn dd-btn-sm" data-edit="${i}"${ro}>编辑</button><button class="dd-btn dd-btn-sm dd-btn-danger" data-delete="${i}"${ro}>删除</button></div></div>`).join(''):'<p class="dd-empty">成员尚无监控规则</p>'}</div>
    ${canEdit?'':'<p class="dodoco-hint">只读：这是其他成员的规则，只有本人可以修改。</p>'}<details id="watchRuleForm" class="dd-form" ${watchState.editing&&canEdit?'open':''}${canEdit?'':' hidden'}><summary>${watchState.editing?'编辑规则':'添加规则'}</summary><div class="dd-form-grid"><label class="dd-field"><span>名称</span><input id="watchName" maxlength="256" value="${de(r.name)}" placeholder="留空则用匹配内容命名"></label><label class="dd-field"><span>最低级别</span><select id="watchLevel">${['DBG','INF','WRN','ERR'].map(x=>`<option${selected(x,r.minLevel)}>${x}</option>`).join('')}</select></label><label class="dd-field dd-span"><span>关键词或正则</span><input id="watchPattern" maxlength="2048" value="${de(r.pattern)}" placeholder="例如：战斗超时 或 超时|失败（勾选正则）"></label><p class="dodoco-hint dd-span">留空时只按最低级别匹配；勾选“正则表达式”后可用 | 匹配多个关键词。</p><label class="dd-field dd-span"><span>备注</span><input id="watchNote" maxlength="1024" value="${de(r.note)}"></label></div><div class="dodoco-checks"><label class="dd-check"><input type="checkbox" id="watchEnabled" ${r.enabled?'checked':''}>启用</label><label class="dd-check"><input type="checkbox" id="watchRegex" ${r.isRegex?'checked':''}>正则表达式</label><label class="dd-check"><input type="checkbox" id="watchAlert" ${r.alert?'checked':''}>命中告警</label><label class="dd-check"><input type="checkbox" id="watchSnapshot" ${r.snapshot?'checked':''}>保存快照</label></div><div class="dd-form-foot"><button id="watchSaveRule" class="dd-btn dd-btn-primary">保存规则</button></div></details></section>
    <section class="dd-card"><header class="dd-card-head"><div><h3>告警与事发快照</h3></div></header><div class="dodoco-checks"><label class="dd-check"><input id="watchMute" type="checkbox" ${s.muteAll?'checked':''}>全部静音</label><label class="dd-check"><input id="watchIncident" type="checkbox" ${s.incidentSnapshotEnabled?'checked':''}>开启事发录像</label></div><div class="dd-form-grid dd-num-grid"><label class="dd-field"><span>卡死阈值 · 分钟</span><input id="watchStall" type="number" min="1" max="1440" value="${de(s.stallThresholdMinutes)}"></label><label class="dd-field"><span>缓冲时长 · 秒</span><input id="watchBuffer" type="number" min="5" max="60" value="${de(s.incidentBufferSeconds)}"></label><label class="dd-field"><span>截帧间隔 · 秒</span><input id="watchInterval" type="number" min="0.5" max="5" step="0.5" value="${de(s.incidentCaptureIntervalSeconds)}"></label><label class="dd-field"><span>事发前 · 秒</span><input id="watchPre" type="number" min="1" max="20" value="${de(s.incidentPreSeconds)}"></label><label class="dd-field"><span>事发后 · 秒</span><input id="watchPost" type="number" min="1" max="20" value="${de(s.incidentPostSeconds)}"></label><label class="dd-field"><span>保留次数 · 0 不清理</span><input id="watchRetention" type="number" min="0" max="10000" value="${de(s.incidentRetentionCount)}"></label></div><div class="dd-form-foot"><button id="watchSaveSettings" class="dd-btn dd-btn-primary"${ro}>${canEdit?'保存设置':'只有本人可以修改设置'}</button></div></section>
    </div></div>`;
    root.querySelectorAll('[data-edit]').forEach(b=>b.onclick=()=>{watchState.editing={...watchState.rules[Number(b.dataset.edit)]};watchDraw();});
    root.querySelectorAll('[data-delete]').forEach(b=>b.onclick=()=>watchDeleteRule(watchState.rules[Number(b.dataset.delete)]));
    root.querySelectorAll('[data-toggle]').forEach(c=>c.onchange=()=>watchToggleRule(watchState.rules[Number(c.dataset.rule)],c.dataset.toggle,c.checked,c));
    d('watchSaveRule').onclick=watchSaveRule; d('watchNewRule').onclick=()=>{watchState.editing=null;watchDraw();d('watchRuleForm').open=true;};
    d('watchSaveSettings').onclick=watchSaveSettings;
    d('watchDate').onchange=e=>{const b=d('watchClearRecords');if(b)b.textContent=e.target.value==='*'?'清理全部日期记录':'清理所选日期记录';watchState.searchEpoch++;watchState.date=e.target.value;watchState.offset=0;watchState.cursors=[null];watchState.page=0;watchState.nextCursor=null;watchState.records=[];watchState.frames=[];watchDrawRecords();};
    d('watchLoadRecords').onclick=()=>{watchState.allCache=null;watchState.keyword=d('watchFilter').value.trim();watchState.level=d('watchRecordLevel').value;watchState.ruleId=d('watchRecordRule').value;watchState.cursors=[null];watchRecords(0);}; d('watchClearRecords').onclick=watchClear;
    d('watchPrev').onclick=()=>watchRecords(Math.max(0,watchState.page-1)); d('watchNext').onclick=()=>{if(watchState.nextCursor){if(!watchState.nextCursor.all)watchState.cursors[watchState.page+1]=watchState.nextCursor;watchRecords(watchState.page+1);}};
    d('watchFilter').onkeydown=e=>{if(e.key==='Enter'){e.preventDefault();d('watchLoadRecords').click();}};watchDrawRecords();
    if(!canEdit){root.querySelectorAll('#watchMute,#watchIncident,#watchStall,#watchBuffer,#watchInterval,#watchPre,#watchPost,#watchRetention').forEach(x=>x.disabled=true);watchBindOwner();}
    const localTime=t=>new Date(t-new Date(t).getTimezoneOffset()*60000).toISOString().slice(0,16);
    d('diagnosticStart').value=localTime(Date.now()-600000);d('diagnosticEnd').value=localTime(Date.now());
    d('diagnosticDownload').onclick=watchDiagnostic;
    d('diagnosticNow').onclick=()=>{d('diagnosticStart').value=localTime(Date.now()-600000);d('diagnosticEnd').value=localTime(Date.now());};
    d('diagnosticDay').onclick=()=>{const start=new Date();start.setHours(0,0,0,0);d('diagnosticStart').value=localTime(start.getTime());d('diagnosticEnd').value=localTime(Date.now());};
}
async function watchDiagnostic() {
    const start=d('diagnosticStart').value,end=d('diagnosticEnd').value,target=dodoco.target,generation=dodoco.generation;
    if(!start||!end||start>end||new Date(end)-new Date(start)>7*86400000){watchStatus('请选择不超过 7 天的有效时间范围。');return;}
    d('diagnosticDownload').disabled=true;
    try {
        watchStatus('正在等待成员生成诊断包…');
        const info=await dodocoRequest('watch.diagnostic.start',{start,end},30000);
        if(!info||typeof info.id!=='string'||!Number.isInteger(info.size)||info.size<1||info.size>8*1024*1024)throw new Error('诊断包信息无效。');
        const parts=[];let offset=0;
        while(offset<info.size){
            if(generation!==dodoco.generation||target!==dodoco.target)throw new Error('成员或连接已变化，下载已中断。');
            const chunk=await dodocoRequest('watch.diagnostic.chunk',{id:info.id,offset},30000);
            if(!chunk||chunk.id!==info.id||chunk.offset!==offset||chunk.total!==info.size||typeof chunk.base64!=='string'||chunk.base64.length>262144)throw new Error('诊断包分块无效。');
            const binary=atob(chunk.base64),bytes=Uint8Array.from(binary,c=>c.charCodeAt(0));
            if(!bytes.length||offset+bytes.length>info.size)throw new Error('诊断包分块长度无效。');
            parts.push(bytes);offset+=bytes.length;
            if(!!chunk.done !== (offset===info.size))throw new Error('诊断包不完整。');
            watchStatus(`正在接收诊断包：${offset} / ${info.size} 字节…`);
        }
        if(!watchCurrent())return;
        dodocoSave(new Blob(parts,{type:'application/zip'}),'dodoco-diagnostic.zip','application/zip');
        watchStatus('诊断包已接收，浏览器已发起下载。'+(info.summary?' '+info.summary:''));
    } catch(e){watchStatus('诊断包未完成：'+e.message);}
    finally{const button=d('diagnosticDownload');if(button)button.disabled=false;}
}
async function watchSaveRule() {
    const old=watchState.editing, rule={id:old?.id||newRequestId(),name:d('watchName').value.trim(),pattern:d('watchPattern').value,note:d('watchNote').value,minLevel:d('watchLevel').value,enabled:d('watchEnabled').checked,isRegex:d('watchRegex').checked,alert:d('watchAlert').checked,snapshot:d('watchSnapshot').checked};
    if(!rule.name)rule.name=rule.pattern.trim();
    if(!rule.name){watchStatus('请填写规则名称或匹配内容。');return;}
    try { await watchMutate('watch.rule.upsert',{rule}); if(!watchCurrent())return; watchState.editing=null; await watchLoad(); watchStatus('成员已保存规则。'); } catch(e){watchStatus('规则未确认保存：'+e.message);}
}
async function watchToggleRule(rule,key,value,input) {
    if(!rule)return;input.disabled=true;
    try { await watchMutate('watch.rule.upsert',{rule:{...rule,[key]:value}}); if(!watchCurrent())return; await watchLoad(); watchStatus(`已${value?'开启':'关闭'}规则“${rule.name}”的${{enabled:'启用',alert:'告警',snapshot:'快照'}[key]}。`); }
    catch(e){ input.checked=!value; input.disabled=false; watchStatus('规则未确认修改：'+e.message); }
}
async function watchDeleteRule(rule) {
    if(!confirm(`删除所选成员的规则“${rule.name}”？此操作不会删除历史记录。`))return;
    try { await watchMutate('watch.rule.delete',{id:rule.id}); if(!watchCurrent())return;watchState.editing=null;await watchLoad();watchStatus('成员已删除规则。'); } catch(e){watchStatus('规则未确认删除：'+e.message);}
}
async function watchSaveSettings() {
    const settings={muteAll:d('watchMute').checked,incidentSnapshotEnabled:d('watchIncident').checked,stallThresholdMinutes:Number(d('watchStall').value),incidentBufferSeconds:Number(d('watchBuffer').value),incidentCaptureIntervalSeconds:Number(d('watchInterval').value),incidentPreSeconds:Number(d('watchPre').value),incidentPostSeconds:Number(d('watchPost').value),incidentRetentionCount:Number(d('watchRetention').value)};
    try { await watchMutate('watch.settings',{settings}); if(!watchCurrent())return;await watchLoad();watchStatus('成员已保存设置。'); } catch(e){watchStatus('设置未确认保存：'+e.message);}
}
async function watchRecords(page) {
    if(!watchState.date){watchStatus('请选择有记录的日期。');return;}
    if(watchState.date==='*')return watchRecordsAll(page);
    const date=watchState.date, target=dodoco.target, epoch=++watchState.searchEpoch;watchStatus('正在读取异常记录…');
    const cursor=watchState.cursors[page];
    try { const result=await dodocoRequest('watch.records',{date,limit:50,keyword:watchState.keyword,level:watchState.level,ruleId:watchState.ruleId,...(cursor||{})}); if(!watchCurrent()||date!==watchState.date||target!==dodoco.target||epoch!==watchState.searchEpoch)return;
        if(!Array.isArray(result?.records)||!Number.isInteger(result.total))throw new Error('记录数据格式无效');
        Object.assign(watchState,{records:result.records.map(r=>({...r,day:date})),total:result.total,offset:page*50,page,nextCursor:result.nextCursor||null,frames:[],record:null});watchDrawRecords();watchStatus('已读取成员异常记录。');
    } catch(e){if(epoch===watchState.searchEpoch)watchStatus('记录读取失败：'+e.message);}
}
/** 全部日期：按日期从新到旧逐日读取（每日最多 100 条），合并后在网页分页；与 PC 端“全部日期”一致按天分组显示。 */
async function watchRecordsAll(page) {
    const target=dodoco.target, epoch=++watchState.searchEpoch, all=[];let total=0;
    try {
        if(page===0||!watchState.allCache){
            for(const [i,date] of watchState.dates.entries()){
                watchStatus(`正在读取全部日期的异常记录…（${i+1}/${watchState.dates.length}）`);
                const result=await dodocoRequest('watch.records',{date,limit:100,keyword:watchState.keyword,level:watchState.level,ruleId:watchState.ruleId});
                if(!watchCurrent()||target!==dodoco.target||epoch!==watchState.searchEpoch)return;
                if(!Array.isArray(result?.records)||!Number.isInteger(result.total))throw new Error('记录数据格式无效');
                all.push(...result.records.map(r=>({...r,day:date})));total+=result.total;
            }
            watchState.allCache={records:all,total};
        }
        const cache=watchState.allCache;
        Object.assign(watchState,{records:cache.records.slice(page*50,page*50+50),total:cache.total,offset:page*50,page,nextCursor:(page+1)*50<cache.records.length?{all:true}:null,frames:[],record:null});
        watchDrawRecords();
        watchStatus(cache.records.length<cache.total?`已读取全部日期；每天最多取最近 100 条，共显示 ${cache.records.length} / ${cache.total} 条，可选单日查看完整记录。`:'已读取全部日期的异常记录。');
    } catch(e){if(epoch===watchState.searchEpoch)watchStatus('记录读取失败：'+e.message);}
}
function watchDrawRecords() {
    const root=d('watchRecords');if(!root)return;
    const rows=watchState.records.map((x,i)=>({x,i}));
    let lastDay='';
    root.innerHTML=rows.length?rows.map(({x,i})=>{const dayHead=watchState.date==='*'&&x.day!==lastDay?`<h4 class="dd-day-head">${de(x.day)}</h4>`:'';lastDay=x.day;const canJump=x.sourceFile&&!String(x.sourceFile).startsWith('远程:');return `${dayHead}<article class="dd-record lv-${de(String(x.level||'').toLowerCase())}"><header><time>${de(x.time)}</time><span class="dd-pill">${de(x.level)}</span><strong>${de(x.ruleName)}</strong>${Number(x.repeatCount)>1?`<span class="dd-pill warn" title="同一规则 60 秒内超出限流后合并计数">×${Number(x.repeatCount)}</span>`:''}</header>${[x.instance,x.taskGroup].filter(Boolean).length||x.routeName?`<small>${de([x.instance,x.taskGroup].filter(Boolean).join(' · '))}${x.routeName?`${x.instance||x.taskGroup?' · ':''}路线 ${de(x.routeName)} <button class="dd-link" data-copy-route="${i}" title="复制路线名">复制</button>`:''}</small>`:''}<p class="dodoco-record-text">${de(x.message)}</p><div class="dd-record-foot"><details><summary>上下文</summary><pre class="dodoco-record-text">${de([...(x.contextBefore||[]),x.matchedLine||x.message,...(x.contextAfter||[])].join('\n'))}</pre></details><div class="dd-actions">${canJump?`<button class="dd-btn dd-btn-sm" data-jump="${i}">在日志浏览中查看</button>`:''}<button class="dd-btn dd-btn-sm" data-incident="${i}">查看事发快照</button></div></div></article>`;}).join(''):'<p class="dd-empty">当前页没有匹配记录</p>';
    root.querySelectorAll('[data-copy-route]').forEach(b=>b.onclick=async()=>{const r=watchState.records[Number(b.dataset.copyRoute)];try{await navigator.clipboard.writeText(r.routeName);watchStatus('已复制路线名：'+r.routeName);}catch{watchStatus('浏览器未允许写入剪贴板。');}});
    root.querySelectorAll('[data-jump]').forEach(b=>b.onclick=()=>watchJump(watchState.records[Number(b.dataset.jump)]));
    root.querySelectorAll('[data-incident]').forEach(b=>b.onclick=()=>watchIncident(watchState.records[Number(b.dataset.incident)]));
    d('watchPage').textContent=watchState.date==='*'?(watchState.records.length?`${watchState.offset+1}–${watchState.offset+watchState.records.length} 条 · 全部日期`:'0 条'):watchState.date?(watchState.total?`${watchState.offset+1}–${Math.min(watchState.offset+watchState.records.length,watchState.total)} / ${watchState.total} 条`:'0 条'):'尚未选择日期';
    d('watchPrev').disabled=watchState.page===0;d('watchNext').disabled=!watchState.nextCursor;
}
async function watchClear() {
    const date=watchState.date;if(!date){watchStatus('请选择日期。');return;}
    const days=date==='*'?[...watchState.dates]:[date];
    if(!confirm(date==='*'?`删除所选成员全部 ${days.length} 天的异常记录？日志和事发快照会保留。`:`删除所选成员 ${date} 的全部异常记录？日志和事发快照会保留。`))return;
    let deleted=0;
    try {for(const day of days){const result=await watchMutate('watch.clear',{date:day});if(result?.deleted)deleted++;if(!watchCurrent())return;}
        watchState.allCache=null;await watchLoad();watchStatus(deleted?`成员已清理 ${deleted} 天的异常记录。`:'记录文件不存在，无内容被删除。');}
    catch(e){watchState.allCache=null;watchStatus(`清理未完成：已确认清理 ${deleted} 天，其余结果未确认：`+e.message);}
}
/** 跳到日志浏览：下载记录来源文件（本机直连读本机日志；远程向成员请求同名文件），再按命中行搜索定位。 */
async function watchJump(record) {
    const name=String(record.sourceFile).split(/[\\/]/).pop(), needle=(record.matchedLine||record.message||'').slice(0,120);
    dodoco.tab='files';dodocoRender();dodocoStatus(`正在打开 ${name} 并定位记录…`);
    try{
        if(!dodoco.files.length)await dodocoLoadFiles();
        const file=dodoco.files.find(f=>f.name===name);
        if(!file){dodocoStatus(`成员日志列表中没有 ${name}（可能已轮转、未共享或不在可共享范围），无法定位。`);return;}
        dodoco.pendingSearch=needle;await dodocoLoadFile(file);
    }catch(e){dodocoStatus('定位未完成：'+e.message);}
}
async function watchIncident(record) {
    const date=watchState.date;try{watchStatus('正在读取事发快照列表…');const result=await dodocoRequest('watch.incidents',{date,time:record.time,ruleName:record.ruleName});if(!watchCurrent())return;
        if(!Array.isArray(result?.frames))throw new Error('快照列表格式无效');watchState.frames=result.frames;watchState.record=record;
        const root=d('watchFrames');root.innerHTML=`<div class="dd-incident"><h4>事发快照 · ${de(record.time)}</h4>${result.frames.length?'<div class="dd-actions">'+result.frames.map((f,i)=>`<button class="dd-btn dd-btn-sm" data-frame="${i}">第 ${i+1} 帧</button>`).join('')+'<button id="watchFrameDownload" class="dd-btn dd-btn-sm dd-btn-primary" disabled>下载当前帧</button></div><img id="watchFrameImage" class="dodoco-image" alt="事发快照" hidden>':'<p class="dd-empty">没有可用的事发快照</p>'}</div>`;
        root.querySelectorAll('[data-frame]').forEach(b=>b.onclick=()=>watchFrame(result.frames[Number(b.dataset.frame)]));watchStatus('已读取事发快照列表。');
    }catch(e){watchStatus('快照列表未取得：'+e.message);}
}
async function watchFrame(frame) {
    const record=watchState.record;if(!record)return;
    try {const result=await dodocoRequest('watch.frame',{date:watchState.date,time:record.time,ruleName:record.ruleName,index:frame.index});if(!watchCurrent())return;
        if(typeof result?.jpegBase64!=='string'||!result.jpegBase64)throw new Error('图片数据无效');
        const img=d('watchFrameImage'),src='data:image/jpeg;base64,'+result.jpegBase64;img.src=src;img.hidden=false;
        const button=d('watchFrameDownload');button.disabled=false;button.onclick=()=>{const a=document.createElement('a');a.href=src;a.download=result.name||'incident.jpg';a.click();};watchStatus('事发快照已取得。');
    }catch(e){watchStatus('快照未取得：'+e.message);}
}
function watchLocalToday(){const t=new Date();return new Date(t.getTime()-t.getTimezoneOffset()*60000).toISOString().slice(0,10);}
/** 锄地数据页“导出诊断包”：与 PC 一致取当前时刻前后 10 分钟，复用 watch.diagnostic.*。 */
async function reportDiagnostic(){
    const local=t=>new Date(t-new Date(t).getTimezoneOffset()*60000).toISOString().slice(0,16);
    const button=d('reportDiag');if(button)button.disabled=true;
    try{
        dodocoStatus('正在等待成员生成诊断包（前后 10 分钟）…');
        const info=await dodocoRequest('watch.diagnostic.start',{start:local(Date.now()-600000),end:local(Date.now()+600000)},30000);
        if(!info||typeof info.id!=='string'||!Number.isInteger(info.size)||info.size<1||info.size>8*1024*1024)throw new Error('诊断包信息无效。');
        const parts=[];let offset=0;
        while(offset<info.size){
            const c=await dodocoRequest('watch.diagnostic.chunk',{id:info.id,offset},30000);
            if(!c||c.id!==info.id||c.offset!==offset||c.total!==info.size||typeof c.base64!=='string'||c.base64.length>262144)throw new Error('诊断包分块无效。');
            const bytes=Uint8Array.from(atob(c.base64),ch=>ch.charCodeAt(0));
            if(!bytes.length||offset+bytes.length>info.size)throw new Error('诊断包分块长度无效。');
            parts.push(bytes);offset+=bytes.length;
            if(!!c.done!==(offset===info.size))throw new Error('诊断包不完整。');
            dodocoStatus(`正在接收诊断包：${offset} / ${info.size} 字节…`);
        }
        dodocoSave(new Blob(parts,{type:'application/zip'}),'dodoco-diagnostic.zip','application/zip');dodocoStatus('诊断包已接收，浏览器已发起下载。');
    }catch(e){dodocoStatus('诊断包未完成：'+e.message);}
    finally{const b=d('reportDiag');if(b)b.disabled=false;}
}
function dodocoRenderReport(host) {
    if(reportState.target!==dodoco.target)Object.assign(reportState,{target:dodoco.target,dates:[],date:'',result:null});
    const section=document.createElement('section');section.className='dd-card';section.innerHTML=`<header class="dd-card-head"><div><h2>每日运行报告与任务总览</h2><p class="dodoco-hint">${watchIsLocal()?'由本机 BGI 日志生成':'由所选成员本地 BGI 日志生成，需允许网页访问并开启日志文件共享'}</p></div><div class="dd-actions"><button id="reportDates" class="dd-btn">读取可用日期</button><label class="dd-field dd-inline"><span>日期</span><select id="reportDate"><option value="">选择日期</option>${reportState.dates.map(x=>`<option value="${de(x)}" ${x===reportState.date?'selected':''}>${de(x)}</option>`).join('')}</select></label><button id="reportLoad" class="dd-btn dd-btn-primary">读取报告</button><button id="reportCopy" class="dd-btn" ${reportState.result?'':'disabled'}>复制报告</button><button id="reportDiag" class="dd-btn" title="导出当前时刻前后 10 分钟的诊断包">导出诊断包</button></div></header><div id="reportBody"></div>`;host.appendChild(section);
    d('reportDates').onclick=async()=>{try{dodocoStatus('正在读取报告日期…');const x=await dodocoRequest('report.dates');if(!dodoco.active||dodoco.tab!=='members')return;if(!Array.isArray(x?.dates))throw new Error('日期格式无效');reportState.dates=[...x.dates].sort().reverse();const today=watchLocalToday();reportState.date=reportState.dates.includes(today)?today:reportState.dates[0]||'';reportState.result=null;dodocoDrawMembers();dodocoStatus(x.dates.length?'已读取可用日期。':'成员没有可用日报日期。');}catch(e){dodocoStatus('读取日期失败：'+e.message);}};
    d('reportDate').onchange=e=>{reportState.date=e.target.value;reportState.result=null;reportDraw();};
    d('reportCopy').onclick=async()=>{try{await navigator.clipboard.writeText(reportText());dodocoStatus('已复制日报到剪贴板。');}catch{dodocoStatus('浏览器未允许写入剪贴板。');}};
    d('reportDiag').onclick=reportDiagnostic;
    d('reportLoad').onclick=async()=>{if(!reportState.date){dodocoStatus('请选择报告日期。');return;}const date=reportState.date;try{dodocoStatus('正在等待成员生成日报…');const x=await dodocoRequest('report.day',{date},30000);if(!dodoco.active||dodoco.tab!=='members'||reportState.date!==date)return;reportState.result=x;reportState.base=null;const prev=reportState.dates.filter(v=>v<date).sort().reverse()[0];if(prev){try{const b=await dodocoRequest('report.day',{date:prev},30000);if(reportState.date===date&&reportState.result===x)reportState.base=b;}catch{}}if(!dodoco.active||dodoco.tab!=='members'||reportState.date!==date)return;reportDraw();dodocoStatus(reportState.base?`已读取成员日报，括号为与 ${reportState.base.date} 对比的时长差。`:'已读取成员日报。');const c=d('reportCopy');if(c)c.disabled=false;}catch(e){dodocoStatus('日报未取得：'+e.message);}};
    reportDraw();
}
// 时长：服务端为 "hh:mm:ss" 或 "d.hh:mm:ss"；按 PC 日报样式显示 1h32m18s / 9m0s / 37s。
function reportSeconds(v){const m=/^(?:(\d+)\.)?(\d+):(\d+):(\d+)/.exec(String(v||''));return m?((+m[1]||0)*86400+(+m[2])*3600+(+m[3])*60+(+m[4])):null;}
function reportHms(v){const t=typeof v==='number'?v:reportSeconds(v);if(t===null)return String(v||'未提供');const s=Math.round(Math.abs(t)),h=Math.floor(s/3600),mi=Math.floor(s%3600/60),se=s%60;return h?`${h}h${mi}m${se}s`:mi?`${mi}m${se}s`:`${se}s`;}
function reportDiff(g){const base=reportState.base;if(!base)return '';const prev=(base.report?.groups||[]).find(x=>x.name===g.name);if(!prev)return '(新)';const a=reportSeconds(g.totalDuration),b=reportSeconds(prev.totalDuration);if(a===null||b===null)return '';const d=a-b;return d<0?`(-${reportHms(-d)})`:`(${reportHms(d)})`;}
function reportUnitName(u){return u.name?`${u.kind} · ${u.name}`:u.kind;}
function reportText(){
    const x=reportState.result;if(!x)return '';const groups=x.report?.groups||[],out=[`配置组运行日报 (${x.date})`];
    if(reportState.base)out.push(`括号 = 与 ${reportState.base.date} 对比的时间差`);
    groups.forEach((g,i)=>{out.push(`${i}.【${g.name}--${reportHms(g.totalDuration)}${g.hasUnclosedRun?'（含未正常结束的一次）':''}】${reportDiff(g)}`);if(g.runCount>1)out.push(`　当天执行 ${g.runCount} 次，已合并`);for(const r of g.rounds||[])out.push('　'+r);});
    out.push(`【合计--${reportHms(x.report?.totalDuration)}】`,'',`全天总运行 ${reportHms(x.overview?.totalDuration)}`);
    const walk=(u,depth,i)=>{out.push(depth?`${'　'.repeat(depth)}├ ${reportUnitName(u)}--${reportHms(u.duration)}${u.unclosed?'（未正常结束）':''}`:`${i}.【${reportUnitName(u)}--${reportHms(u.duration)}${u.unclosed?'（未正常结束）':''}】`);for(const c of u.children||[])walk(c,depth+1);};
    (x.overview?.topUnits||[]).forEach((u,i)=>walk(u,0,i));return out.join('\n');
}
function reportDraw(){const root=d('reportBody'),x=reportState.result;if(!root)return;if(!x){root.innerHTML='<p class="dd-empty">选择日期后读取报告</p>';return;}
    const groups=x.report?.groups||[],units=x.overview?.topUnits||[];reportState.open=reportState.open||new Set();
    const unit=(u,key,depth=0)=>{const kids=u.children||[],open=reportState.open.has(key);return `<div class="dd-unit" style="--depth:${Math.min(depth,5)}">${kids.length?`<button class="dd-unit-toggle" data-unit="${de(key)}" aria-expanded="${open}" aria-label="${open?'收起':'展开'}子任务">${open?'▼':'▶'}</button>`:'<span class="dd-unit-dot"></span>'}<div><strong>${de(reportUnitName(u))}</strong><small>${de(u.start)} 开始 · ${reportHms(u.duration)}${u.unclosed?' · 未正常结束':''}${kids.length&&!open?` · ${kids.length} 项子任务`:''}</small></div></div>${open?kids.map((v,j)=>unit(v,key+'/'+j,depth+1)).join(''):''}`;};
    const allKeys=[];const collect=(u,key)=>{if((u.children||[]).length){allKeys.push(key);(u.children||[]).forEach((v,j)=>collect(v,key+'/'+j));}};units.forEach((u,i)=>collect(u,String(i)));
    const allOpen=allKeys.length>0&&allKeys.every(k=>reportState.open.has(k));
    root.innerHTML=`<div class="dd-report"><section><div class="dd-report-head"><h3>${de(x.date)} · 配置组报告</h3><span class="dd-stat">合计 <b>${reportHms(x.report?.totalDuration)}</b></span></div><p class="dodoco-hint">${reportState.base?`括号 = 与 ${de(reportState.base.date)} 对比的时间差`:'无更早日志可对比'}</p>${groups.length?groups.map(g=>`<div class="dd-unit"><span class="dd-unit-dot"></span><div><strong>${de(g.name)}</strong> <span class="dd-diff">${de(reportDiff(g))}</span><small>${reportHms(g.totalDuration)}${g.runCount>1?` · 当天执行 ${de(g.runCount)} 次，已合并`:''}${g.hasUnclosedRun?' · 含未正常结束的一次':''}</small><details><summary>联机轮次 (${(g.rounds||[]).length})</summary><pre class="dodoco-record-text">${de((g.rounds||[]).join('\n')||'无轮次记录')}</pre></details></div></div>`).join(''):'<p class="dd-empty">该日无配置组运行记录；若一直为空，请确认成员已在设置中配置 BGI 路径</p>'}</section><section><div class="dd-report-head"><h3>全天任务总览</h3><span class="dd-stat">全天总运行 <b>${reportHms(x.overview?.totalDuration)}</b></span>${allKeys.length?`<button id="reportExpandAll" class="dd-link">${allOpen?'一键收起全部':'一键展开全部'}</button>`:''}</div>${units.length?units.map((u,i)=>unit(u,String(i))).join(''):'<p class="dd-empty">该日期没有任务总览记录</p>'}</section></div>`;
    root.querySelectorAll('[data-unit]').forEach(b=>b.onclick=()=>{const k=b.dataset.unit;if(reportState.open.has(k))reportState.open.delete(k);else reportState.open.add(k);reportDraw();});
    const all=d('reportExpandAll');if(all)all.onclick=()=>{if(allOpen)reportState.open.clear();else allKeys.forEach(k=>reportState.open.add(k));reportDraw();};
}

// ===== 全房间当天日志共用工具（PC「远程日志」窗口的批量下载）=====
// 沿用 log.requestFiles / log.requestDownload，依次向在线成员收集当天 BGI 与助手日志，打成一个 ZIP（不压缩），
// 附“下载结果.txt”；离线、空列表、失败如实写入结果。只在服务器中转可用，本机直连无房间。
const roomLogs = { running:false, cancel:false, generation:0 };
const roomLogBgi = /^better-genshin-impact(\d{8})?(_\d{3})*\.log$/i;
const roomLogAssistant = /^assistant_runtime\.\d{4}-\d{2}-\d{2}(?:\.(?:s\d+|\d+))*\.log$/i;
function roomLogLocalDate(value){const t=new Date(value);if(!Number.isFinite(t.getTime()))return '';return new Date(t.getTime()-t.getTimezoneOffset()*60000).toISOString().slice(0,10);}
function roomLogSafe(text,max){return String(text||'').replace(/[\\/:*?"<>|\u0000-\u001f]/g,'_').slice(0,max)||'未命名';}
const roomLogCrcTable=(()=>{const t=new Uint32Array(256);for(let n=0;n<256;n++){let c=n;for(let k=0;k<8;k++)c=c&1?0xEDB88320^(c>>>1):c>>>1;t[n]=c>>>0;}return t;})();
function roomLogCrc(bytes){let c=0xFFFFFFFF;for(let i=0;i<bytes.length;i++)c=roomLogCrcTable[(c^bytes[i])&0xFF]^(c>>>8);return (c^0xFFFFFFFF)>>>0;}
/** 不压缩的 ZIP；文件名带 UTF-8 标志，资源管理器可直接打开中文名。 */
function roomLogZip(entries){
    const enc=new TextEncoder(),parts=[],central=[];let offset=0;
    for(const e of entries){
        const name=enc.encode(e.name),data=e.data,crc=roomLogCrc(data),h=new DataView(new ArrayBuffer(30));
        h.setUint32(0,0x04034b50,true);h.setUint16(4,20,true);h.setUint16(6,0x0800,true);h.setUint32(14,crc,true);h.setUint32(18,data.length,true);h.setUint32(22,data.length,true);h.setUint16(26,name.length,true);
        parts.push(new Uint8Array(h.buffer),name,data);
        const c=new DataView(new ArrayBuffer(46));c.setUint32(0,0x02014b50,true);c.setUint16(4,20,true);c.setUint16(6,20,true);c.setUint16(8,0x0800,true);c.setUint32(16,crc,true);c.setUint32(20,data.length,true);c.setUint32(24,data.length,true);c.setUint16(28,name.length,true);c.setUint32(42,offset,true);
        central.push(new Uint8Array(c.buffer),name);offset+=30+name.length+data.length;
    }
    const size=central.reduce((n,x)=>n+x.length,0),end=new DataView(new ArrayBuffer(22));
    end.setUint32(0,0x06054b50,true);end.setUint16(8,entries.length,true);end.setUint16(10,entries.length,true);end.setUint32(12,size,true);end.setUint32(16,offset,true);
    return new Blob([...parts,...central,new Uint8Array(end.buffer)],{type:'application/zip'});
}
async function roomLogGunzip(chunks){
    if(typeof DecompressionStream==='undefined')throw new Error('浏览器不支持 gzip 解压');
    const parts=chunks.map(s=>Uint8Array.from(atob(s),c=>c.charCodeAt(0)));
    const buffer=await new Response(new Blob(parts).stream().pipeThrough(new DecompressionStream('gzip'))).arrayBuffer();
    if(buffer.byteLength>200*1024*1024)throw new Error('文件超过 200 MB 上限');
    return new Uint8Array(buffer);
}
// 单成员列表/下载、批量收集和面板挂载见 dodoco-remote-logs.js（远程日志面板，支持协助房间）。
