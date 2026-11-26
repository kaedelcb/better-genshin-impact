// 本人档案和编排的视图入口；房间控制权限与本人授权保持分离。
(() => {
    const e = value => escapeHtml(value);
    const pick = (obj, key) => obj?.[key] ?? obj?.[key[0].toUpperCase() + key.slice(1)];
    const pendingKey = 'bgi.room.owner.pending.v1';
    const targetKey = t => JSON.stringify([t.uid, t.instanceId, t.machineId || '']);
    const pending = (() => { try { return JSON.parse(localStorage.getItem(pendingKey) || '{}') || {}; } catch { return {}; } })();
    const persist = () => { try { localStorage.setItem(pendingKey, JSON.stringify(pending)); } catch {} };
    const operationId = (identity, operation, requestId) => {
        const prefix = pick(pick(identity,'roomRequestPrefixes'),mtRoomOperationKind(operation));
        if (!prefix) throw new Error('执行端尚未提供此房间操作的回执合同。');
        return prefix + requestId;
    };
    function makeAdapter(target, identity, send, ready, onMessage) {
        const key = targetKey(target);
        const read = async (operation, payload, options = {}) => {
            if (!ready()) throw new Error('原执行端连接未确认，请重新核对。');
            const reply = await send(target, operation, payload, options);
            if (operation === 'operation.get') {
                if (pending[key]?.id===payload && reply.operationId===payload && ['ok','rejected'].includes(reply.status)) { delete pending[key];persist(); }
                return reply;
            }
            if (reply.status !== 'ok') throw new Error(reply.message || '执行端未返回数据');
            return reply;
        };
        async function reconcile() {
            const item = pending[key];
            if (!item || !ready()) return null;
            try {
                const reply = await send(target, 'operation.get', item.id, {});
                if (reply.operationId === item.id && (['ok','rejected'].includes(reply.status) || reply.status==='unknown' && pick(reply.data,'recoveryCleared')===true)) {
                    if (pending[key]?.id === item.id) { delete pending[key]; persist(); }
                    return reply;
                }
                return { ...reply, status:reply.status === 'accepted' ? 'accepted' : 'unknown' };
            } catch (error) { return {status:'unknown',message:error.message}; }
        }
        async function operate(operation, payload, options = {}) {
            if (!ready()) return {status:'rejected',message:'原执行端连接未确认，未提交。'};
            const track = mtDurable(operation,payload) && !options.allowWhilePending;
            const stage = options.onStage || (() => {});
            if (track && pending[key]) {
                const checked = await reconcile();
                if (pending[key]) return {status:'blocked',message:'上次房间操作仍未确认；本次未发送。'};
                if (checked) return {status:'blocked',message:'上次操作已核对，请重新读取配置后再次确认。'};
            }
            const requestId = options.requestId || newRequestId();
            let id;
            if (track) {
                id = operationId(identity,operation,requestId);
                pending[key] = {id,operation,label:options.label || operation,at:Date.now()}; persist();
            }
            stage('sent','已发送，等待执行端确认。');
            let reply;
            try { reply = await send(target,operation,payload,{...options,requestId}); }
            catch (error) {
                if (track && !error.unconfirmed && pending[key]?.id === id) { delete pending[key]; persist(); }
                return { status:error.unconfirmed?'unknown':'rejected',message:error.message,operationId:error.unconfirmed?id:undefined };
            }
            if (track && (reply.operationId && reply.operationId !== id || reply.status==='ok' && reply.operationId!==id)) return {status:'unknown',message:'原回执编号缺失或不匹配，未确认。',operationId:id};
            if (track && reply.status === 'accepted') {
                if (reply.operationId !== id) return {status:'unknown',message:'执行端受理编号不匹配，未确认。',operationId:id};
                stage('accepted','已受理；正在等待终态。');
                const deadline = Date.now() + (options.waitBudget || 12000);
                while (ready() && Date.now() < deadline) {
                    await new Promise(r=>setTimeout(r,700));
                    if (!ready()) break;
                    const state = await reconcile();
                    if (state && ['ok','rejected'].includes(state.status)) return state;
                    if (state?.status === 'unknown') return {...state,operationId:id};
                }
                return {status:'accepted',message:'已受理，终态尚未确认；不会重发。',operationId:id};
            }
            if (track && ['ok','rejected','denied'].includes(reply.status) && pending[key]?.id === id) { delete pending[key]; persist(); }
            stage(reply.status==='ok'?'done':reply.status==='unknown'?'unknown':'failed',reply.message || '执行端已回复');
            return reply;
        }
        return {target:{...target,requestPrefixes:pick(identity,'roomRequestPrefixes')},read,operate,ready,onMessage,reconcile,
            pending:()=>pending[key],acknowledgeUnknown:()=>{if(pending[key]){delete pending[key];persist();}},operationId:(operation,requestId)=>operationId(identity,operation,requestId)};
    }
    function mount(host, options) {
        let chosen = null, identity = null, adapter = null, inner = null, disposed = false, generation = 0;
        let mode = options.mode || 'profiles';
        let content;
        const message = text => { const status=host.querySelector('[data-owner-status]'); if(status)status.textContent=text || ''; options.onMessage?.(text); };
        const current = () => !!chosen && options.ready(chosen) && !disposed;
        const getTargets = () => options.targets().filter(t => t?.instanceId && t?.machineId && !t.isObserver);
        const shell = () => {
            const targets = getTargets();
            host.innerHTML = `<div class="room-owner-head"><label>本人执行端<select data-owner-target><option value="">选择执行端</option>${targets.map(t=>`<option value="${e(targetKey(t))}" ${chosen&&targetKey(t)===targetKey(chosen)?'selected':''}>${e(t.name || t.uid)} · ${e(t.uid)} · ${e(t.local?'本机':t.roomLabel || t.instanceId.slice(0,8))}${t.online?'':'（待确认）'}</option>`).join('')}</select></label><button class="btn btn-secondary" data-owner-refresh>刷新</button><button class="btn btn-secondary" data-owner-mode>${mode==='profiles'?'任务编排':'房间档案'}</button></div><p class="room-owner-status note" data-owner-status role="status"></p><div data-owner-content></div>`;
            content = host.querySelector('[data-owner-content]');
            host.querySelector('[data-owner-target]').onchange = async ev => { chosen = targets.find(t=>targetKey(t)===ev.target.value)||null; options.onSelect?.(chosen?targetKey(chosen):''); await load(); };
            host.querySelector('[data-owner-refresh]').onclick = () => { if(chosen)chosen=getTargets().find(t=>targetKey(t)===targetKey(chosen))||chosen;load(); };
            host.querySelector('[data-owner-mode]').onclick = () => {mode=mode==='profiles'?'orchestration':'profiles';load();};
        };
        async function load() {
            const epoch=++generation;
            inner?.dispose?.();inner=null; shell();
            if (!chosen) { content.textContent='请选择自己的执行端。网页房间密码不代表个人授权。'; return; }
            if (!current()) { content.textContent='原执行端暂不可用，不能编辑房间配置。';return; }
            try {
                const reply=await options.send(chosen,'web.identity',undefined,{});
                if (disposed || epoch!==generation) return;
                identity=reply.data;
                if(reply.status!=='ok'||pick(identity,'instanceId')!==chosen.instanceId||pick(identity,'machineId')!==chosen.machineId||!pick(identity,'isExecutor')) throw new Error('执行端身份未确认。');
                if(!pick(identity,'owner')) {
                    content.innerHTML='<section class="panel"><h3>本人授权</h3><p class="note">请在自己的执行端助手设置中生成网页授权码。房间密码不等于本人授权。</p><label>授权码<input data-owner-code autocomplete="off"></label><button class="btn btn-primary" data-owner-authorize>确认授权</button></section>';
                    content.querySelector('[data-owner-authorize]').onclick=async ev=>{
                        const code=content.querySelector('[data-owner-code]').value.trim(); if(!code||!current())return;
                        ev.target.disabled=true;
                        try { const r=await options.send(chosen,'web.authorize',undefined,{extra:{code,label:'耕地机网页'}});
                            if(epoch!==generation||disposed)return;
                            if(r.status!=='ok'||!r.data?.browserId||!r.data?.secret)throw new Error(r.message||'授权失败');
                            mtSaveCredential(chosen,{browserId:r.data.browserId,secret:r.data.secret,uid:chosen.uid});await load();
                        }catch(error){message(error.message);ev.target.disabled=false;}
                    };return;
                }
                adapter=makeAdapter(chosen,identity,options.send,()=>current()&&epoch===generation&&!!pick(identity,'owner')&&(chosen.local||!!mtCredentialFor(chosen)),message);
                if(adapter.pending()) {
                    message('上次房间操作尚未核对。请使用「核对上次操作」，不会重发。');
                }
                let mounted;
                if (mode==='orchestration') {
                    if(!pick(identity,'capabilities')?.includes('roomOrchestrationV1'))throw new Error('此执行端未提供网页编排。');
                    mounted=window.roomOrchestration.mount(content,adapter);
                } else if(pick(identity,'capabilities')?.includes('roomProfilesV1')) mounted=await mountProfiles(content,adapter);
                else throw new Error('此执行端未提供网页房间档案。');
                if(epoch!==generation||disposed){mounted?.dispose?.();return;}
                inner=mounted;
                if(adapter.pending()) {
                    const b=document.createElement('button');b.type='button';b.className='btn btn-secondary';b.textContent='核对上次操作';
                    host.querySelector('.room-owner-head').append(b);
                    b.onclick=async()=>{const r=await adapter.reconcile();message(r?.message||'尚未确认');if(r&&['ok','rejected'].includes(r.status)){await load();return;}
                        if(r?.status==='unknown'&&r.operationId===adapter.pending()?.id){
                            const release=document.createElement('button');release.type='button';release.className='btn btn-secondary';release.textContent='已人工核对';
                            release.onclick=()=>{if(confirm('请先核对执行端实际状态。解除待核对不会认定原操作成功或失败，也不会重发。确认继续？')){adapter.acknowledgeUnknown();load();}};
                            b.replaceWith(release);
                        }};
                }
            } catch(error) { if(epoch===generation&&!disposed) message(error.message); }
        }
        const initial = getTargets();
        chosen = initial.find(t=>targetKey(t)===options.initialTargetKey) || (!options.initialTargetKey?initial.find(t=>t.local&&t.online):null) || null;
        void load();
        let renewing=false;
        return {refresh:()=>{
            if(disposed||renewing)return;
            const desired=chosen?targetKey(chosen):options.initialTargetKey;
            const candidate=desired&&getTargets().find(t=>targetKey(t)===desired&&options.ready(t));
            if((!chosen||!current())&&candidate){renewing=true;chosen=candidate;void load().finally(()=>{renewing=false;});return;}
            if(!current()&&inner){inner.refresh?.();message('原执行端状态待核对。');}else if(!chosen){const select=host.querySelector('[data-owner-target]');if(select&&select.options.length!==getTargets().length+1)shell();}else inner?.refresh?.();
        },dispose:()=>{disposed=true;generation++;inner?.dispose?.();}};
    }
    async function mountProfiles(host, adapter) {
        let closed=false, data=null, revision='', selected=null, mobileEditing=false, saving=false, dirty=false, codeTimer=null, codeGeneration=0;
        const message=text=>adapter.onMessage?.(text);
        const refresh=async()=>{
            if(closed||saving||dirty||!adapter.ready())return;
            try {const reply=await adapter.read('room.profiles.get'); if(closed)return;data=reply.data;revision=reply.revision||pick(data,'revision')||'';selected=selected||pick(data,'activeProfileId')||null;draw();}
            catch(error){message(error.message);}
        };
        function draw(){
            if(closed||!data)return;
            const rooms=pick(data,'profiles')||[], active=pick(data,'activeProfileId');
            const room=rooms.find(r=>pick(r,'id')===selected)||null;
            const p=k=>pick(room,k);const locked=!!(p('isLocalRoom')||p('isActiveRoom'));
            const ready=adapter.ready()&&!!pick(data,'canEdit')&&!adapter.pending();
            const suppliedMax=pick(data,'maxProfiles'), maxProfiles=Number.isInteger(suppliedMax)&&suppliedMax>0?suppliedMax:5;
            host.innerHTML=`<div class="room-owner-profiles ${mobileEditing?'room-owner-editing':''}"><section class="panel room-owner-list"><h3>房间档案</h3><p class="note">执行端独立保存 · ${rooms.length}/${maxProfiles}</p>${rooms.map(r=>`<button type="button" class="room-owner-item ${pick(r,'id')===selected?'selected':''}" data-profile="${e(pick(r,'id'))}"><b>${e(pick(r,'name'))}</b><small>${e(pick(r,'roomCode'))} · ${e(pick(r,'serverUrl'))}</small><small>${pick(r,'isActiveRoom')?(pick(data,'activeRoomConfirmed')?'当前登录 · ':'活动档案（连接待核对） · '):''}${pick(r,'isLocalRoom')?'本机房间':''}</small></button>`).join('')}<button type="button" class="btn btn-secondary" data-add ${!ready||rooms.length>=maxProfiles?'disabled':''}>＋ 添加房间</button></section><form class="panel room-owner-form"><button type="button" class="btn btn-secondary room-owner-back" data-profile-back>‹ 房间列表</button><h3>${room?'编辑房间':'添加房间'}</h3><label>档案名称<input name="name" required maxlength="64" value="${e(p('name')||'')}"></label><label>备注<input name="note" value="${e(p('note')||'')}"></label><label>服务器地址<input name="serverUrl" required type="url" ${locked?'readonly':''} value="${e(p('serverUrl')||pick(data,'defaultServerUrl')||'')}"></label><label>房间号<input name="roomCode" maxlength="${p('preserveRoomCode')?128:6}" ${locked?'readonly':''} value="${e(p('roomCode')||'')}"></label><label>房间密码<input name="password" type="password" autocomplete="new-password" maxlength="128" ${locked?'disabled':''} placeholder="${room?'留空保留原密码':'请输入房间密码'}"></label><label>成员 UID（逗号分隔）<input name="memberUids" ${locked?'readonly':''} value="${e((p('memberUids')||[pick(data,'playerUid')]).filter(Boolean).join(', '))}"></label><label>预期开锄人数<select name="expectedPlayers">${[1,2,3,4].map(n=>`<option ${n===(p('expectedPlayers')||4)?'selected':''}>${n}</option>`).join('')}</select></label><p class="note">${locked?'本机/当前房间的服务器与凭据在本机设置中维护。':'已有房间填写房号；新建房间由服务器发号。'}</p><div class="room-owner-actions"><button type="submit" class="btn btn-primary" ${!ready?'disabled':''}>${room?'保存档案':'保存已有房间'}</button>${!room?`<button type="button" class="btn btn-secondary" data-create ${!ready||!pick(data,'canCreate')?'disabled':''}>建房并保存</button>`:''}${room&&p('canRemove')?'<button class="btn btn-secondary danger" type="button" data-remove>移除本机档案</button>':''}${room&&p('id')!==active?'<button type="button" class="btn btn-secondary" data-switch>切换执行端房间</button>':''}</div></form></div>`;
            const form=host.querySelector('form');
            const changeSelection=id=>{if(saving||dirty&&!confirm('放弃尚未保存的修改？'))return;selected=id;dirty=false;mobileEditing=true;draw();};
            form.querySelector('[data-profile-back]').onclick=()=>{if(saving||dirty&&!confirm('放弃尚未保存的修改？'))return;mobileEditing=false;dirty=false;draw();};
            host.querySelectorAll('[data-profile]').forEach(b=>b.onclick=()=>changeSelection(b.dataset.profile));
            host.querySelector('[data-add]').onclick=()=>changeSelection(null);
            form.oninput=()=>{dirty=true;};
            const members=form.elements.memberUids, code=form.elements.roomCode;
            let calculated='';
            function deriveCode(){
                const before=code.value, epoch=++codeGeneration;
                if(before && before!==calculated)return;
                clearTimeout(codeTimer);
                codeTimer=setTimeout(async()=>{
                    const uids=members.value.split(/[,，\s]+/).filter(Boolean);
                    try { const r=await adapter.read('room.profiles.legacyCode',uids);
                        if(!closed&&epoch===codeGeneration&&form.isConnected&&code.value===before){calculated=pick(r.data,'roomCode')||'';code.value=calculated; }
                    }catch{/* 输入无效或目录断线时保留原房号，不替换已有身份。 */}
                },300);
            }
            if(!locked&&ready){members.addEventListener('input',deriveCode);if(!room&&!code.value)deriveCode();}
            if(!ready)for(const input of form.querySelectorAll('input,select,button'))input.disabled=true;
            async function submit(op,payload,ask){
                if(saving||!adapter.ready()||!revision||ask&&!confirm(ask))return;saving=true;
                const buttons=[...host.querySelectorAll('button')];buttons.forEach(b=>b.disabled=true);
                try {const r=await adapter.operate(op,payload,{expectedRevision:revision,label:'房间档案操作'});message(r.message||r.status);if(r.status==='ok'){dirty=false;selected=op==='room.profiles.remove'?null:pick(r.data,'id')||selected;await reload();}else if(op==='room.switch.start'&&r.operationId)showSwitch(r.operationId);
                    else if(adapter.pending())showPending();}
                catch(error){message(error.message);}finally{saving=false;buttons.forEach(b=>{if(b.isConnected)b.disabled=!adapter.ready()||!!adapter.pending();});}
            }
            function payload(create){const f=new FormData(form);return {profileId:selected||undefined,name:String(f.get('name')||''),note:String(f.get('note')||''),serverUrl:String(f.get('serverUrl')||''),roomCode:create?'':String(f.get('roomCode')||''),password:String(f.get('password')||''),memberUids:String(f.get('memberUids')||'').split(/[,，\s]+/).filter(Boolean),expectedPlayers:Number(f.get('expectedPlayers'))};}
            form.onsubmit=ev=>{ev.preventDefault();submit('room.profiles.save',payload(false));};
            const create=host.querySelector('[data-create]');if(create)create.onclick=()=>{if(code.value.trim()&&code.value.trim()!==calculated){message('已填写现有房间号，请选择保存已有房间。');return;}if(form.reportValidity())submit('room.profiles.create',payload(true),'由执行端创建新房间并保存档案？');};
            const remove=host.querySelector('[data-remove]');if(remove)remove.onclick=()=>submit('room.profiles.remove',selected,'仅移除本机档案，不删除服务器房间或历史。继续？');
            const sw=host.querySelector('[data-switch]');if(sw){sw.disabled=!adapter.ready()||!pick(data,'canSwitch');sw.onclick=()=>{if(dirty){message('请先保存或取消修改。');return;}submit('room.switch.start',selected,'将此执行端实际切换到所选房间？切房不等于上线。');};}
            const switchState=pick(data,'switchStatus')||{};const previousSwitch=pick(data,'switchOperationId')||pick(switchState,'operationId')||pick(switchState,'currentOperationId');if(previousSwitch&&(pick(data,'switchPending')||pick(data,'switchBlocked')||pick(switchState,'state')&&pick(switchState,'state')!=='idle'||adapter.pending()?.id===previousSwitch))showSwitch(previousSwitch);
        }
        async function reload(){const r=await adapter.read('room.profiles.get');if(closed)return;data=r.data;revision=r.revision||pick(data,'revision')||'';draw();}
        function showPending(){
            if(host.querySelector('[data-room-profile-pending]'))return;
            const box=document.createElement('section');box.className='panel';box.dataset.roomProfilePending='';box.innerHTML='<p>原档案操作结果尚未确认，不会重发。</p><button type="button" class="btn btn-secondary" data-check>核对原操作</button><p role="status"></p>';host.prepend(box);
            box.querySelector('[data-check]').onclick=async()=>{
                const r=await adapter.reconcile();box.querySelector('[role=status]').textContent=r?.message||'尚未确认';
                if(r&&['ok','rejected'].includes(r.status)){dirty=false;await reload();return;}
                if(r?.status==='unknown'&&r.operationId===adapter.pending()?.id){const release=document.createElement('button');release.type='button';release.className='btn btn-secondary';release.textContent='已人工核对';release.onclick=async()=>{if(confirm('请先核对执行端实际状态。解除待核对不会认定原操作成功或失败，也不会重发。继续？')){adapter.acknowledgeUnknown();dirty=false;await reload();}};box.append(release);}
            };
        }
        function showSwitch(id){
            const box=document.createElement('section');box.className='panel';box.innerHTML='<p>切房已提交，实际结果待核对。</p><div class="room-owner-actions"><button class="btn btn-secondary" data-check>核对切房</button><button class="btn btn-secondary" data-cancel>取消等待</button><button class="btn btn-secondary" data-recheck>重新检查</button></div><p role="status"></p>';host.prepend(box);
            const status=box.querySelector('[role=status]');
            let checking=false;
            const handle=async op=>{if(checking)return;checking=true;try{const r=await (op==='room.switch.status'?adapter.read(op,id):adapter.operate(op,id,{allowWhilePending:true}));status.textContent=r.message||pick(r.data,'message')||pick(r.data,'operationStatus')||r.status;if(op==='room.switch.status'&&r.status==='ok') {
                    const s=pick(r.data,'operationStatus');box.querySelector('[data-cancel]').disabled=!pick(r.data,'canCancel');box.querySelector('[data-recheck]').disabled=!pick(r.data,'canRecheck');
                    if(s==='ok'||s==='rejected'||pick(r.data,'recoveryCleared')===true){await adapter.reconcile();if(!dirty&&!saving)await reload();}
                }}catch(error){status.textContent=error.message;}finally{checking=false;}};
            box.querySelector('[data-check]').onclick=()=>handle('room.switch.status');box.querySelector('[data-cancel]').onclick=()=>handle('room.switch.cancel');box.querySelector('[data-recheck]').onclick=()=>handle('room.switch.recheck');
        }
        await refresh();return {refresh:()=>{if(!adapter.ready()){message('执行端不在线，保存前请重新核对。');host.querySelectorAll('form input,form select,form button').forEach(el=>el.disabled=true);}},dispose:()=>{closed=true;clearTimeout(codeTimer);}};
    }
    window.webRoomOwner={mount,makeAdapter};
})();
