// 异常监控和日报均读取成员的真实数据；请求结果由 dodocoRequest 关联。
const watchState = { target:'', rules:[], settings:null, dates:[], records:[], date:'', offset:0, total:0, cursors:[null], page:0, nextCursor:null, keyword:'', level:'', ruleId:'', searchEpoch:0, editing:null, frames:[], record:null };
const reportState = { target:'', dates:[], date:'', result:null };
const watchAlerts = { timer:null, token:0, target:'', seen:new Set(), initialized:false };
function watchCurrent() { return dodoco.active && dodoco.tab === 'watch' && watchState.target === dodoco.target; }
function watchStatus(message) { if (watchCurrent()) dodocoStatus(message); }
function watchStopAlerts(message) {
    clearTimeout(watchAlerts.timer);watchAlerts.timer=null;watchAlerts.token++;watchAlerts.seen.clear();watchAlerts.initialized=false;
    const button=d('watchAlertToggle'),state=d('watchAlertStatus');if(button)button.textContent='开启网页告警监视';if(state)state.textContent=message||'网页告警监视未开启。';
}
function dodocoRenderWatch() {
    if (watchState.target !== dodoco.target) { watchStopAlerts(); Object.assign(watchState,{ target:dodoco.target, rules:[], settings:null, dates:[], records:[], date:'', offset:0, total:0, cursors:[null], page:0, nextCursor:null, keyword:'', level:'', ruleId:'', editing:null, frames:[], record:null }); }
    d('dodocoContent').innerHTML = `<h2>异常监控</h2><p class="dodoco-hint">需成员在 PC 助手中允许房间成员远程访问。规则、设置及记录操作作用于所选成员。</p><div class="dodoco-actions"><button id="watchRefresh">读取规则与设置</button><button id="watchAlertToggle">${watchAlerts.timer?'停止网页告警监视':'开启网页告警监视'}</button></div><p id="watchAlertStatus" class="dodoco-status" role="status" aria-live="polite">${watchAlerts.timer?'网页告警监视中。':'网页告警监视未开启；开启后每 15 秒检查最近 100 条记录，仅在本页显示新告警。'}</p><div id="watchBody"></div>`;
    d('watchRefresh').onclick=watchLoad;
    d('watchAlertToggle').onclick=()=>watchAlerts.timer?watchStopAlerts('网页告警监视已停止。'):watchStartAlerts();
    if (watchState.settings) watchDraw();
}
function watchStartAlerts() {
    if(!dodocoReady())return;watchStopAlerts();watchAlerts.target=dodoco.target;const token=++watchAlerts.token;
    d('watchAlertToggle').textContent='停止网页告警监视';d('watchAlertStatus').textContent='正在建立告警监视基线…';
    const poll=async()=>{
        if(token!==watchAlerts.token)return;
        if(!watchCurrent()||!roomReady||watchAlerts.target!==dodoco.target){watchStopAlerts('已离开页面或连接中断，告警监视已停止。');return;}
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
                if(watchAlerts.initialized&&fresh.length){const latest=fresh[0];d('watchAlertStatus').textContent=`新告警 ${fresh.length} 条：${latest.time} · ${latest.ruleName} · ${latest.message}`;}
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
    root.innerHTML=`<section class="dodoco-operation-section"><h3>监控规则</h3><div class="dodoco-rule-list">${watchState.rules.length ? watchState.rules.map((x,i)=>`<div class="dodoco-member"><strong>${de(x.name||'未命名规则')}</strong> · ${x.enabled?'启用':'停用'} · ${de(x.minLevel)} 及以上<small>${de(x.isRegex?'正则':'关键词')}：${de(x.pattern||'仅级别匹配')}　${x.alert?'告警 ':''}${x.snapshot?'快照':''}</small><div class="dodoco-actions"><button data-edit="${i}">编辑</button><button data-delete="${i}">删除</button></div></div>`).join(''):'<p class="dodoco-hint">成员尚无监控规则。</p>'}</div>
    <details id="watchRuleForm" ${watchState.editing?'open':''}><summary>${watchState.editing?'编辑规则':'添加规则'}</summary><div class="dodoco-controls"><label>名称<input id="watchName" maxlength="256" value="${de(r.name)}"></label><label>关键词或正则<input id="watchPattern" maxlength="2048" value="${de(r.pattern)}" placeholder="留空仅按级别匹配"></label><label>最低级别<select id="watchLevel">${['DBG','INF','WRN','ERR'].map(x=>`<option${selected(x,r.minLevel)}>${x}</option>`).join('')}</select></label><label>备注<input id="watchNote" maxlength="1024" value="${de(r.note)}"></label></div><div class="dodoco-checks"><label><input type="checkbox" id="watchEnabled" ${r.enabled?'checked':''}>启用</label><label><input type="checkbox" id="watchRegex" ${r.isRegex?'checked':''}>正则表达式</label><label><input type="checkbox" id="watchAlert" ${r.alert?'checked':''}>命中告警</label><label><input type="checkbox" id="watchSnapshot" ${r.snapshot?'checked':''}>保存快照</label></div><div class="dodoco-actions"><button id="watchSaveRule">保存规则</button><button id="watchNewRule">新建规则</button></div></details></section>
    <section class="dodoco-operation-section"><h3>告警与事发快照设置</h3><div class="dodoco-checks"><label><input id="watchMute" type="checkbox" ${s.muteAll?'checked':''}>全部静音</label><label><input id="watchIncident" type="checkbox" ${s.incidentSnapshotEnabled?'checked':''}>开启事发录像</label></div><div class="dodoco-controls"><label>卡死阈值（分钟）<input id="watchStall" type="number" min="1" max="1440" value="${de(s.stallThresholdMinutes)}"></label><label>缓冲时长（秒）<input id="watchBuffer" type="number" min="5" max="60" value="${de(s.incidentBufferSeconds)}"></label><label>截帧间隔（秒）<input id="watchInterval" type="number" min="0.5" max="5" step="0.5" value="${de(s.incidentCaptureIntervalSeconds)}"></label><label>事发前（秒）<input id="watchPre" type="number" min="1" max="20" value="${de(s.incidentPreSeconds)}"></label><label>事发后（秒）<input id="watchPost" type="number" min="1" max="20" value="${de(s.incidentPostSeconds)}"></label><label>保留次数（0 不自动清理）<input id="watchRetention" type="number" min="0" max="10000" value="${de(s.incidentRetentionCount)}"></label><button id="watchSaveSettings">保存设置</button></div></section>
    <section class="dodoco-operation-section"><h3>异常记录</h3><div class="dodoco-controls"><label>日期<select id="watchDate"><option value="">选择日期</option>${watchState.dates.map(x=>`<option value="${de(x)}"${selected(x,watchState.date)}>${de(x)}</option>`).join('')}</select></label><label>关键词<input id="watchFilter" type="search" maxlength="256" placeholder="搜索所选日期" value="${de(watchState.keyword)}"></label><label>规则<select id="watchRecordRule"><option value="">全部规则</option>${watchState.rules.map(x=>`<option value="${de(x.id)}"${selected(x.id,watchState.ruleId)}>${de(x.name)}</option>`).join('')}</select></label><label>级别<select id="watchRecordLevel"><option value="">全部级别</option>${['DBG','INF','WRN','ERR'].map(x=>`<option${selected(x,watchState.level)}>${x}</option>`).join('')}</select></label><button id="watchLoadRecords">搜索记录</button><button id="watchClearRecords">清理所选日期记录</button></div><p class="dodoco-hint">搜索覆盖所选日期的记录；清理只删除该成员当天异常记录，不删除日志或事发快照。</p><div class="dodoco-actions"><button id="watchPrev">上一页</button><button id="watchNext">下一页</button><span id="watchPage"></span></div><div id="watchRecords"></div><div id="watchFrames"></div></section>
    <section class="dodoco-operation-section"><h3>导出诊断包</h3><p class="dodoco-hint">时间按成员电脑的本地时间填写。需开启远程访问及日志文件共享；范围最多 7 天，包最大 8 MB。</p><div class="dodoco-controls"><label>开始<input id="diagnosticStart" type="datetime-local"></label><label>结束<input id="diagnosticEnd" type="datetime-local"></label><button id="diagnosticDownload">生成并下载诊断包</button></div></section>`;
    root.querySelectorAll('[data-edit]').forEach(b=>b.onclick=()=>{watchState.editing={...watchState.rules[Number(b.dataset.edit)]};watchDraw();});
    root.querySelectorAll('[data-delete]').forEach(b=>b.onclick=()=>watchDeleteRule(watchState.rules[Number(b.dataset.delete)]));
    d('watchSaveRule').onclick=watchSaveRule; d('watchNewRule').onclick=()=>{watchState.editing=null;watchDraw();d('watchRuleForm').open=true;};
    d('watchSaveSettings').onclick=watchSaveSettings;
    d('watchDate').onchange=e=>{watchState.searchEpoch++;watchState.date=e.target.value;watchState.offset=0;watchState.cursors=[null];watchState.page=0;watchState.nextCursor=null;watchState.records=[];watchState.frames=[];watchDrawRecords();};
    d('watchLoadRecords').onclick=()=>{watchState.keyword=d('watchFilter').value.trim();watchState.level=d('watchRecordLevel').value;watchState.ruleId=d('watchRecordRule').value;watchState.cursors=[null];watchRecords(0);}; d('watchClearRecords').onclick=watchClear;
    d('watchPrev').onclick=()=>watchRecords(Math.max(0,watchState.page-1)); d('watchNext').onclick=()=>{if(watchState.nextCursor){watchState.cursors[watchState.page+1]=watchState.nextCursor;watchRecords(watchState.page+1);}};
    d('watchFilter').onkeydown=e=>{if(e.key==='Enter'){e.preventDefault();d('watchLoadRecords').click();}};watchDrawRecords();
    const localTime=t=>new Date(t-new Date(t).getTimezoneOffset()*60000).toISOString().slice(0,16);
    d('diagnosticStart').value=localTime(Date.now()-600000);d('diagnosticEnd').value=localTime(Date.now());
    d('diagnosticDownload').onclick=watchDiagnostic;
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
    if(!rule.name){watchStatus('请输入规则名称。');return;}
    try { await dodocoRequest('watch.rule.upsert',{rule}); if(!watchCurrent())return; watchState.editing=null; await watchLoad(); watchStatus('成员已保存规则。'); } catch(e){watchStatus('规则未确认保存：'+e.message);}
}
async function watchDeleteRule(rule) {
    if(!confirm(`删除所选成员的规则“${rule.name}”？此操作不会删除历史记录。`))return;
    try { await dodocoRequest('watch.rule.delete',{id:rule.id}); if(!watchCurrent())return;watchState.editing=null;await watchLoad();watchStatus('成员已删除规则。'); } catch(e){watchStatus('规则未确认删除：'+e.message);}
}
async function watchSaveSettings() {
    const settings={muteAll:d('watchMute').checked,incidentSnapshotEnabled:d('watchIncident').checked,stallThresholdMinutes:Number(d('watchStall').value),incidentBufferSeconds:Number(d('watchBuffer').value),incidentCaptureIntervalSeconds:Number(d('watchInterval').value),incidentPreSeconds:Number(d('watchPre').value),incidentPostSeconds:Number(d('watchPost').value),incidentRetentionCount:Number(d('watchRetention').value)};
    try { await dodocoRequest('watch.settings',{settings}); if(!watchCurrent())return;await watchLoad();watchStatus('成员已保存设置。'); } catch(e){watchStatus('设置未确认保存：'+e.message);}
}
async function watchRecords(page) {
    if(!watchState.date){watchStatus('请选择有记录的日期。');return;}
    const date=watchState.date, target=dodoco.target, epoch=++watchState.searchEpoch;watchStatus('正在读取异常记录…');
    const cursor=watchState.cursors[page];
    try { const result=await dodocoRequest('watch.records',{date,limit:50,keyword:watchState.keyword,level:watchState.level,ruleId:watchState.ruleId,...(cursor||{})}); if(!watchCurrent()||date!==watchState.date||target!==dodoco.target||epoch!==watchState.searchEpoch)return;
        if(!Array.isArray(result?.records)||!Number.isInteger(result.total))throw new Error('记录数据格式无效');
        Object.assign(watchState,{records:result.records,total:result.total,offset:page*50,page,nextCursor:result.nextCursor||null,frames:[],record:null});watchDrawRecords();watchStatus('已读取成员异常记录。');
    } catch(e){if(epoch===watchState.searchEpoch)watchStatus('记录读取失败：'+e.message);}
}
function watchDrawRecords() {
    const root=d('watchRecords');if(!root)return;
    const rows=watchState.records.map((x,i)=>({x,i}));
    root.innerHTML=rows.length?rows.map(({x,i})=>`<article class="dodoco-member"><strong>${de(x.time)} · ${de(x.ruleName)} · ${de(x.level)}</strong><small>${de(x.instance||'')} ${de(x.taskGroup||'')} ${de(x.routeName||'')}</small><p class="dodoco-record-text">${de(x.message)}</p><details><summary>上下文</summary><pre class="dodoco-record-text">${de([...(x.contextBefore||[]),x.matchedLine||x.message,...(x.contextAfter||[])].join('\n'))}</pre></details><div class="dodoco-actions"><button data-incident="${i}">查看事发快照</button></div></article>`).join(''):'<p class="dodoco-hint">当前页没有匹配记录。</p>';
    root.querySelectorAll('[data-incident]').forEach(b=>b.onclick=()=>watchIncident(watchState.records[Number(b.dataset.incident)]));
    d('watchPage').textContent=watchState.date?(watchState.total?`${watchState.offset+1}–${Math.min(watchState.offset+watchState.records.length,watchState.total)} / ${watchState.total} 条`:'0 条'):'尚未选择日期';
    d('watchPrev').disabled=watchState.page===0;d('watchNext').disabled=!watchState.nextCursor;
}
async function watchClear() {
    const date=watchState.date;if(!date){watchStatus('请选择日期。');return;}
    if(!confirm(`删除所选成员 ${date} 的全部异常记录？日志和事发快照会保留。`))return;
    try {const result=await dodocoRequest('watch.clear',{date});if(!watchCurrent())return;await watchLoad();watchStatus(result.deleted?'成员已清理该日期异常记录。':'该日期记录文件不存在，无内容被删除。');}catch(e){watchStatus('清理未确认：'+e.message);}
}
async function watchIncident(record) {
    const date=watchState.date;try{watchStatus('正在读取事发快照列表…');const result=await dodocoRequest('watch.incidents',{date,time:record.time,ruleName:record.ruleName});if(!watchCurrent())return;
        if(!Array.isArray(result?.frames))throw new Error('快照列表格式无效');watchState.frames=result.frames;watchState.record=record;
        const root=d('watchFrames');root.innerHTML=`<h4>事发快照 · ${de(record.time)}</h4>${result.frames.length?'<div class="dodoco-actions">'+result.frames.map((f,i)=>`<button data-frame="${i}">第 ${i+1} 帧</button>`).join('')+'</div><img id="watchFrameImage" class="dodoco-image" alt="事发快照" hidden><button id="watchFrameDownload" disabled>下载当前帧</button>':'<p>没有可用的事发快照。</p>'}`;
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
function dodocoRenderReport(host) {
    if(reportState.target!==dodoco.target)Object.assign(reportState,{target:dodoco.target,dates:[],date:'',result:null});
    const section=document.createElement('section');section.className='dodoco-operation-section';section.innerHTML=`<h2>每日运行报告与任务总览</h2><p class="dodoco-hint">由所选成员本地 BGI 日志生成，需要其允许网页访问并开启日志文件共享。</p><div class="dodoco-controls"><button id="reportDates">读取可用日期</button><label>日期<select id="reportDate"><option value="">选择日期</option>${reportState.dates.map(x=>`<option value="${de(x)}" ${x===reportState.date?'selected':''}>${de(x)}</option>`).join('')}</select></label><button id="reportLoad">读取报告</button></div><div id="reportBody"></div>`;host.appendChild(section);
    d('reportDates').onclick=async()=>{try{dodocoStatus('正在读取报告日期…');const x=await dodocoRequest('report.dates');if(!dodoco.active||dodoco.tab!=='members')return;if(!Array.isArray(x?.dates))throw new Error('日期格式无效');reportState.dates=x.dates;reportState.date=x.dates[0]||'';reportState.result=null;dodocoDrawMembers();dodocoStatus(x.dates.length?'已读取可用日期。':'成员没有可用日报日期。');}catch(e){dodocoStatus('读取日期失败：'+e.message);}};
    d('reportDate').onchange=e=>{reportState.date=e.target.value;reportState.result=null;reportDraw();};
    d('reportLoad').onclick=async()=>{if(!reportState.date){dodocoStatus('请选择报告日期。');return;}const date=reportState.date;try{dodocoStatus('正在等待成员生成日报…');const x=await dodocoRequest('report.day',{date},30000);if(!dodoco.active||dodoco.tab!=='members'||reportState.date!==date)return;reportState.result=x;reportDraw();dodocoStatus('已读取成员日报。');}catch(e){dodocoStatus('日报未取得：'+e.message);}};
    reportDraw();
}
function reportDraw(){const root=d('reportBody'),x=reportState.result;if(!root)return;if(!x){root.innerHTML='<p class="dodoco-hint">选择日期后读取报告。</p>';return;}
    const groups=x.report?.groups||[],units=x.overview?.topUnits||[];
    const unit=(u,depth=0)=>`<div class="dodoco-member" style="margin-left:${Math.min(depth,5)*12}px"><strong>${de(u.kind)} · ${de(u.name)}</strong><small>${de(u.start)} 开始 · ${de(u.duration)}${u.unclosed?' · 未闭合':''}</small></div>${(u.children||[]).map(v=>unit(v,depth+1)).join('')}`;
    root.innerHTML=`<h3>${de(x.date)} · 配置组报告</h3><p>累计时长 ${de(x.report?.totalDuration||'未提供')}</p>${groups.length?groups.map(g=>`<div class="dodoco-member"><strong>${de(g.name)}</strong> · ${de(g.totalDuration)} · ${de(g.runCount)} 次${g.hasUnclosedRun?' · 未闭合':''}<details><summary>联机轮次 (${(g.rounds||[]).length})</summary><pre class="dodoco-record-text">${de((g.rounds||[]).join('\n')||'无轮次记录')}</pre></details></div>`).join(''):'<p>该日期没有配置组记录。</p>'}<h3>全天任务总览</h3><p>累计时长 ${de(x.overview?.totalDuration||'未提供')}</p>${units.length?units.map(u=>unit(u)).join(''):'<p>该日期没有任务总览记录。</p>'}`;
}
