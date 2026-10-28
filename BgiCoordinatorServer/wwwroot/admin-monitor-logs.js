'use strict';
    let activeLogCode=null,activeLogSelection={},logView=0,downloadController=null,logRequest=0,logDateRequest=0,logDownloading=false;
    const rangeControls=['logStartDate','logStartHour','logStartMinute','logEndDate','logEndHour','logEndMinute'];
    function beijingMinute(time=Date.now()){return new Date(time+8*60*60*1000).toISOString().slice(0,16);}
    function setLogRange(start,end){
      $('logStartDate').value=start.slice(0,10);$('logStartHour').value=start.slice(11,13);$('logStartMinute').value=start.slice(14,16);
      $('logEndDate').value=end.slice(0,10);$('logEndHour').value=end.slice(11,13);$('logEndMinute').value=end.slice(14,16);updateLogRange();
    }
    function selectedLogRange(){return {start:`${$('logStartDate').value}T${$('logStartHour').value}:${$('logStartMinute').value}`,end:`${$('logEndDate').value}T${$('logEndHour').value}:${$('logEndMinute').value}`};}
    function updateLogRange(){
      const {start,end}=selectedLogRange();let error='';
      if(!$('logStartDate').value||!$('logEndDate').value)error='请点击日历选择开始和结束日期';
      else if(start>end)error='结束时间不能早于开始时间，请重新选择';
      $('logRangeStatus').textContent=error||`${start.replace('T',' ')} 至 ${end.replace('T',' ')}（北京时间）`;
      $('logDownload').disabled=logDownloading||!!error;
      return !error;
    }
    function todayLogRange(){const today=beijingMinute().slice(0,10);setLogRange(today+'T00:00',today+'T23:59');}
    async function loadLogDates(code){
      const request=++logDateRequest;
      $('logAvailableDates').textContent='正在读取已保存日期…';$('logSavedDate').replaceChildren(new Option('选择已保存日期',''));
      try{
        const response=await fetch(`/api/admin/rooms/${encodeURIComponent(code)}/logs/dates`,{credentials:'same-origin',cache:'no-store',signal:AbortSignal.timeout(15000)});
        if(response.status===401){location.replace('/admin/login');return;}
        if(!response.ok)throw new Error();
        const data=await response.json();if(request!==logDateRequest||activeLogCode!==code)return;
        const dates=data.dates||[];for(const date of dates)$('logSavedDate').add(new Option(date,date));
        $('logAvailableDates').textContent=dates.length?`已保存 ${dates.length} 天：${dates.at(-1)} 至 ${dates[0]}，可从下拉框选择（房间级日期，具体批次以下载结果为准）`:'索引中暂无已保存日期；可选其他日期查询，旧版未保存的日志无法补回';
      }catch{if(request===logDateRequest&&activeLogCode===code)$('logAvailableDates').textContent='已保存日期读取失败，仍可选择时间直接下载';}
    }
    function openLogs(code,date=view==='history'?$('historyDate').value:beijingMinute().slice(0,10),selection={direct:true},label='仅此房间号自身保存的日志'){
      logView++;activeLogCode=code;activeLogSelection=selection;$('logMergeLabel').hidden=!!(selection.batch||selection.session);$('logMerge').checked=!selection.direct&&!selection.batch&&!selection.session;$('logDownloadStatus').textContent='';$('logTitle').textContent=`房间 ${code} · 服务器日志`;$('logScope').textContent=label;
      setLogRange(date+'T00:00',date+'T23:59');$('logDialog').showModal();loadLogs();loadLogDates(code);
    }
    async function downloadLogs(){
      const code=activeLogCode,view=logView;if(!code||logDownloading||!updateLogRange())return;
      const {start,end}=selectedLogRange(),button=$('logDownload');logDownloading=true;$('logMerge').disabled=true;button.disabled=true;button.textContent='正在下载…';
      $('logDownloadStatus').textContent='正在准备所选时段的日志…';
      try{
        const query=new URLSearchParams({start,end,...activeLogSelection});downloadController=new AbortController();
        const response=await fetch(`/api/admin/rooms/${encodeURIComponent(code)}/logs/download?${query}`,{credentials:'same-origin',cache:'no-store',signal:downloadController.signal});
        if(response.status===401){location.replace('/admin/login');return;}
        if(!response.ok){const problem=await response.json().catch(()=>({}));throw new Error(problem.detail||'下载失败，请稍后重试');}
        const blob=await response.blob(),url=URL.createObjectURL(blob),link=document.createElement('a');
        if(activeLogCode!==code||view!==logView){URL.revokeObjectURL(url);return;}
        const disposition=response.headers.get('Content-Disposition')||'';
        link.href=url;link.download=(disposition.match(/filename="?([^";]+)"?/i)||[])[1]||`room-${code}-${start.replace(/[:T]/g,'-')}.txt`;
        document.body.append(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),60000);
        const count=response.headers.get('X-Room-Log-Count'),sources=response.headers.get('X-Room-Log-Sources'),sourceCount=Number(response.headers.get('X-Room-Log-Source-Count')||0);
        const first=response.headers.get('X-Room-Log-First-Utc'),last=response.headers.get('X-Room-Log-Last-Utc');
        const format=time=>new Date(time).toLocaleString('zh-CN',{timeZone:'Asia/Shanghai',hour12:false});
        let warnings=[];try{warnings=JSON.parse(decodeURIComponent(response.headers.get('X-Room-Log-Warnings')||'%5B%5D'));}catch{warnings=['下载摘要读取失败，请查看TXT文件内的保存说明'];}
        $('logDownloadStatus').textContent=`已发起下载${count!==null?'，共 '+count+' 条':''}${sources?'，来源房间 '+sources+(sourceCount>20?' 等 '+sourceCount+' 个（完整列表见TXT）':''):''}${first&&last?'；实际记录 '+format(first)+' 至 '+format(last)+'（北京时间）':''}${warnings.length?'。保存说明：'+warnings.join('；'):''}`;
      }catch(error){if(activeLogCode===code&&view===logView)$('logDownloadStatus').textContent=error.message||'下载失败，请稍后重试';}
      finally{logDownloading=false;$('logMerge').disabled=false;button.textContent='下载所选日志';updateLogRange();}
    }
    async function loadLogs(){
      if(!activeLogCode||!$('logDialog').open)return;
      if(activeLogSelection.batch||activeLogSelection.session){$('logLines').replaceChildren();$('logStatus').textContent='已按批次／世界会话限定下载范围，点击“下载所选日志”获取历史内容。';return;}
      const request=++logRequest,code=activeLogCode;
      $('logStatus').textContent='正在读取…';$('logLines').replaceChildren();
      try{
        const response=await fetch(`/api/admin/rooms/${encodeURIComponent(code)}/logs`,{credentials:'same-origin',cache:'no-store',signal:AbortSignal.timeout(15000)});
        if(response.status===401){location.replace('/admin/login');return;}
        if(!response.ok)throw new Error('HTTP '+response.status);
        const data=await response.json();
        if(request!==logRequest||!$('logDialog').open)return;
        $('logStatus').textContent=data.entries.length?`实时预览：最近 ${data.entries.length} 条（下载条数另行统计）`:'本次启动暂无此房间的实时预览；历史日志仍可下载';
        for(const entry of data.entries){
          const line=node('div','log-line '+entry.level.toLowerCase());
          line.append(node('div','log-meta',`${new Date(entry.timestampUtc).toLocaleString('zh-CN',{timeZone:'Asia/Shanghai',hour12:false})} · ${entry.level} · ${entry.category}`));
          line.append(node('div','log-message',entry.message));$('logLines').append(line);
        }
      }catch{if(request===logRequest&&$('logDialog').open)$('logStatus').textContent='读取失败，请点击刷新重试';}
    }
