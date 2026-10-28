// 大日志解析和筛选放在独立线程；切片让新的搜索可取消旧搜索。
let lines=[], raw=null, generation=0;
const LIMIT=200*1024*1024, PAGE=1500;
const tick=()=>new Promise(resolve=>setTimeout(resolve,0));
function post(id,type,data={}){postMessage({id,type,...data});}
self.onmessage=async ({data:m})=>{
 const id=m.id;
 if(m.type==='load'){
  const token=++generation;
  try{
   let blob=m.file;
   if(m.chunks){
    const parts=[];for(let i=0;i<m.chunks.length;i++){const s=atob(m.chunks[i]);parts.push(Uint8Array.from(s,c=>c.charCodeAt(0)));if(i%20===0){post(id,'progress',{message:`正在解码压缩块 ${i+1}/${m.chunks.length}…`});await tick();if(token!==generation)return;}}
    if(typeof DecompressionStream==='undefined')throw new Error('浏览器不支持 gzip 解压。');
    const reader=new Blob(parts).stream().pipeThrough(new DecompressionStream('gzip')).getReader(), output=[];let size=0;
    for(;;){const {value,done}=await reader.read();if(done)break;size+=value.length;if(size>LIMIT){await reader.cancel();throw new Error('日志超过 200 MB 处理上限。');}output.push(value);post(id,'progress',{message:`正在解压：${(size/1048576).toFixed(1)} MB…`});if(token!==generation){await reader.cancel();return;}}
    blob=new Blob(output);
   }
   if(!blob||blob.size>LIMIT)throw new Error('日志超过 200 MB 处理上限。');
   post(id,'progress',{message:`正在解析 ${(blob.size/1048576).toFixed(1)} MB 日志…`});
   const text=await blob.text(); if(token!==generation)return;
   const parsed=text.split(/\r?\n/);if(token!==generation)return;
   lines=parsed;raw=blob;post(id,'done',{count:lines.length,raw:blob});
  }catch(e){post(id,'error',{message:e.message||'日志解析失败。'});}
  return;
 }
 if(m.type==='query'){
  const token=++generation, query=m.query||'', q=query.toLowerCase(), from=m.from||'', to=m.to||'', level=m.level||'', instance=(m.instance||'').toLowerCase();
  let regex=null;
  if(m.regex && query){try{regex=new RegExp(query,'i');}catch(e){post(id,'error',{message:'正则表达式无效：'+e.message});return;}}
  const selected=[];let current='',currentLevel='',currentInstance='';
  for(let i=0;i<lines.length;i++){
   const line=lines[i],time=line.match(/\[(?:\d{4}-\d\d-\d\d[ T])?(\d\d:\d\d)(?::\d\d(?:\.\d+)?)?\]/),lvl=line.match(/\[(DBG|INF|WRN|ERR)\]/);
   if(time)current=time[1];if(lvl){currentLevel=lvl[1];currentInstance=(line.match(/\[([A-Za-z][A-Za-z0-9]*:S\d+:P\d+:T\d+)\]/)?.[1]||'').toLowerCase();}
   if((!q||(regex?regex.test(line):line.toLowerCase().includes(q)))&&(!instance||currentInstance.includes(instance))&&(!level||currentLevel===level)&&(!(from||to)||(current&&(!from||current>=from)&&(!to||current<=to))))selected.push(line);
   if(i%10000===0){post(id,'progress',{message:`正在筛选 ${i}/${lines.length} 行…`});await tick();if(token!==generation)return;}
  }
  if(m.export){
   let content;if(m.export==='csv'){const quote=s=>'"'+s.replaceAll('"','""')+'"';content='\ufeff行内容\r\n'+selected.map(quote).join('\r\n');}else content=selected.join('\n');
   post(id,'done',{blob:new Blob([content],{type:m.export==='csv'?'text/csv;charset=utf-8':'text/plain;charset=utf-8'}),count:selected.length});
  }else{
   const page=Math.min(m.page||0,Math.max(0,Math.ceil(selected.length/PAGE)-1)), rows=[];let chars=0,truncated=false;
   for(const line of selected.slice(page*PAGE,(page+1)*PAGE)){
    if(chars>=500000){truncated=true;break;}
    const preview=line.slice(0,Math.min(16000,500000-chars));chars+=preview.length;rows.push(preview+(preview.length<line.length?'…（本行预览截断）':''));if(preview.length<line.length)truncated=true;
   }
   post(id,'done',{rows,page,count:selected.length,truncated});
  }
 }
};
