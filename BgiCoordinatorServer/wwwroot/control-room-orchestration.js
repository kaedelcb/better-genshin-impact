// 耕地机 · 任务编排（执行端只读投影 + 定向配置编辑）。
// 传输、授权、路由和持久回执台账属于调用者；本模块不选成员、不改 mt.target、不执行任务。
(() => {
    'use strict';

    const OP = Object.freeze({ get:'room.orchestration.get', edit:'room.orchestration.edit',
        names:'room.orchestration.names', catalog:'room.orchestration.catalog', layout:'room.orchestration.layout',
        save:'room.orchestration.save', cancel:'room.orchestration.cancel', receipt:'operation.get' });
    const REFRESH_MS = 3000, FRESH_MS = 12000;
    const kinds = { done:'✓', running:'▷', waiting:'◷', pending:'○', stopped:'□', failed:'△', unknown:'?', none:'—' };
    const types = { group:'配置组', onedragon:'一条龙', workflow:'计划表' };
    const quickKeys = ['一键传奇','一键次数盾','一键精英','一键小怪','一键自定义'];
    const completionStrings = ['SpecifiedTaskType','SpecifiedTaskName','SpecifiedStartMode','SpecifiedStartTaskId',
        'SpecifiedStartTaskName','SpecifiedConfigRevision','SpecifiedWorkflowId'];
    const routeFields = ['SourceProfileId','SourceServerUrl','SourceRoomCode','TargetProfileId','TargetServerUrl','TargetRoomCode','TargetName'];
    const mounts = new WeakMap();
    let serial = 0;

    // RemoteCenter's records use default PascalCase JSON; CompletionTargetCatalog has
    // explicit camelCase attributes. Accept either on read, but emit the actual C# record keys.
    const get = (o, key) => o?.[key] ?? o?.[key[0].toLowerCase() + key.slice(1)];
    const str = (value, fallback = '') => typeof value === 'string' ? value : fallback;
    const text = (o, key, fallback = '') => str(get(o, key), fallback);
    const array = value => Array.isArray(value) ? value : [];
    const list = (o, key) => array(get(o, key));
    const has = (o, key) => Object.prototype.hasOwnProperty.call(o, key);
    const nonempty = value => typeof value === 'string' && value.trim().length > 0;
    const typeLabel = type => has(types, type) ? types[type] : '类型待核对';
    const statusOf = reply => text(reply, 'Status');
    const messageOf = (reply, fallback) => text(reply, 'Message') || fallback;
    const errorText = (error, fallback = '未能取得执行端回复') => str(error?.message) || fallback;
    const bindingLabel = b => `${typeLabel(b.Type)} · ${b.Name || '名称待核对'}`;
    const sameBinding = (a, b) => a.Name === b.Name && a.Type === b.Type;
    const sameBindings = (a, b) => a.length === b.length && a.every((item, i) => sameBinding(item, b[i]));
    const int32 = value => {
        const raw = String(value).trim();
        if (!/^[+-]?\d+$/.test(raw)) return null;
        const n = Number(raw);
        return Number.isInteger(n) && n >= -2147483648 && n <= 2147483647 ? n : null;
    };
    const scheduleValid = value => !value || /^(?:[01]\d|2[0-3]):[0-5]\d$/.test(value);
    function requestId() {
        const bytes = new Uint8Array(16);
        // Correlation IDs, not credentials; valid even for HTTP origins using getRandomValues.
        window.crypto.getRandomValues(bytes);
        return Array.from(bytes, x => x.toString(16).padStart(2, '0')).join('');
    }
    function identity(target) {
        return { uid:text(target,'Uid') || text(target,'PlayerUid'),
            instanceId:text(target,'InstanceId') || text(target,'ClientInstanceId'), machineId:text(target,'MachineId') };
    }
    const sameIdentity = (a, b) => !!a.uid && !!a.instanceId && !!a.machineId
        && a.uid === b.uid && a.instanceId === b.instanceId && a.machineId === b.machineId;
    const routeAddress = value => {
        // Match the server's legacy /hub normalization for identity comparisons only.
        // These strings are never used to connect or navigate from this module.
        try {
            const url = new URL(value.trim());
            if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.search || url.hash) return value;
            let address = url.href.replace(/\/+$/, '');
            while (/\/hub$/i.test(address)) address = address.slice(0, -4).replace(/\/+$/, '');
            return address;
        } catch { return value; }
    };
    function roomSwitch(raw) {
        if (!raw || typeof raw !== 'object') return null;
        return { ...Object.fromEntries(routeFields.map(k => [k, text(raw,k)])), RunAfterSwitch:get(raw,'RunAfterSwitch') === true };
    }
    function sameRoute(a, b) {
        return !!a && !!b && routeFields.slice(0, 6).every(k => k.endsWith('ServerUrl')
            ? routeAddress(a[k]) === routeAddress(b[k]) : a[k] === b[k]);
    }
    function loginIdentity(presentation) {
        const cards = list(presentation,'Cards').filter(c => get(c,'Current') === true);
        return cards.length === 1 ? JSON.stringify([text(cards[0],'Id'),text(cards[0],'Identity')]) : '';
    }
    function sameCardIdentity(card,key) {
        const value = text(card,'Identity'), prefix = key.RoomCode + ' · ';
        return value === key.RoomCode || value.startsWith(prefix) && routeAddress(value.slice(prefix.length)) === routeAddress(key.ServerUrl);
    }
    function readCompletion(raw) {
        if (!raw || typeof raw !== 'object') throw new Error('执行端未返回完整完成动作，请重新读取。');
        const policy = get(raw,'Policy');
        const namedPolicies = { resume:0, stop:1, runspecified:2 };
        const normalizedPolicy = typeof policy === 'string' && has(namedPolicies,policy.toLowerCase())
            ? namedPolicies[policy.toLowerCase()] : policy;
        const priority = get(raw,'SpecifiedTaskPriority');
        if (!Number.isInteger(priority) || completionStrings.some(k => typeof get(raw,k) !== 'string'))
            throw new Error('执行端完成动作字段不完整，请更新或重新读取。');
        return { Policy:normalizedPolicy, ...Object.fromEntries(completionStrings.map(k => [k,text(raw,k)])),
            SpecifiedTaskPriority:priority, RoomSwitch:roomSwitch(get(raw,'RoomSwitch')) };
    }
    const actionName = c => c.RoomSwitch ? 'switchRoom' : c.Policy === 0 ? 'resume' : c.Policy === 1 ? 'stop' : c.Policy === 2 ? 'runSpecified' : 'unknown';
    const isSpecified = c => c.Policy === 2 || c.RoomSwitch?.RunAfterSwitch === true;
    const emptyCompletion = () => ({ Policy:1, SpecifiedTaskType:'group', SpecifiedTaskName:'', SpecifiedTaskPriority:0,
        SpecifiedStartMode:'default', SpecifiedStartTaskId:'', SpecifiedStartTaskName:'', SpecifiedConfigRevision:'', SpecifiedWorkflowId:'', RoomSwitch:null });
    function sameCompletion(a, b) {
        if (a.Policy !== b.Policy || a.SpecifiedTaskPriority !== b.SpecifiedTaskPriority || !!a.RoomSwitch !== !!b.RoomSwitch) return false;
        if (a.RoomSwitch && (!sameRoute(a.RoomSwitch,b.RoomSwitch) || a.RoomSwitch.RunAfterSwitch !== b.RoomSwitch.RunAfterSwitch)) return false;
        return completionStrings.every(k => k === 'SpecifiedTaskName' && a.SpecifiedTaskType === 'workflow'
            && b.SpecifiedTaskType === 'workflow' && a.SpecifiedWorkflowId === b.SpecifiedWorkflowId || a[k] === b[k]);
    }
    function snapshotIdentity(raw) {
        return { uid:text(raw,'PlayerUid'), instanceId:text(raw,'InstanceId'), machineId:text(raw,'MachineId') };
    }
    function readPresentation(raw, target) {
        const p = get(raw,'Presentation');
        const capturedAt = text(raw,'CapturedAtUtc');
        if (!sameIdentity(snapshotIdentity(raw), target) || !p || !Array.isArray(get(p,'Cards')) || !Number.isFinite(Date.parse(capturedAt)))
            throw new Error('执行端快照身份、时间或格式不匹配，未采用回复。');
        return { presentation:p, capturedAt, executorName:text(raw,'ExecutorName') };
    }
    function readEdit(reply, target, profileId) {
        const data = get(reply,'Data'), raw = get(data,'Snapshot'), key = get(raw,'Key');
        const revision = text(data,'Revision') || text(reply,'Revision');
        if (!raw || !nonempty(revision) || text(reply,'Revision') && text(reply,'Revision') !== revision
            || text(key,'ProfileId') !== profileId || text(key,'PlayerUid') !== target.uid
            || !nonempty(get(key,'ServerUrl')) || !nonempty(get(key,'RoomCode')))
            throw new Error('执行端编辑身份或原修订无法确认，请重新打开编辑器。');
        const names = get(raw,'Names'), bindingTypes = get(raw,'Types'), commands = get(raw,'QuickCommands');
        if (!Array.isArray(names) || !Array.isArray(bindingTypes) || !commands || typeof commands !== 'object'
            || Array.isArray(commands) || !Number.isInteger(get(raw,'ExpectedPlayers')) || typeof get(raw,'Schedule') !== 'string'
            || typeof get(raw,'ScheduleRequiresConfirmation') !== 'boolean' || typeof get(raw,'ScheduleSuppressed') !== 'boolean'
            || !Array.isArray(get(data,'Choices')))
            throw new Error('执行端未返回完整房间配置，未打开可写表单。');
        return { revision, key:{ ProfileId:profileId, PlayerUid:target.uid, ServerUrl:text(key,'ServerUrl'), RoomCode:text(key,'RoomCode') },
            name:text(raw,'Name'), bindings:names.map((name,i) => ({ Name:str(name), Type:bindingTypes.length ? str(bindingTypes[i]) : 'group' })),
            people:get(raw,'ExpectedPlayers'), schedule:get(raw,'Schedule'), requiresConfirmation:get(raw,'ScheduleRequiresConfirmation'),
            suppressed:get(raw,'ScheduleSuppressed'), completion:readCompletion(get(raw,'Completion')),
            quick:new Map(Object.entries(commands)), hasPeers:get(data,'HasPeerSnapshot') === true,
            choices:list(data,'Choices').map(roomSwitch).filter(r => r && r.SourceProfileId === profileId && r.TargetProfileId
                && r.TargetProfileId !== profileId && r.SourceRoomCode === text(key,'RoomCode') && r.TargetRoomCode
                && r.SourceServerUrl && r.TargetServerUrl && routeAddress(r.SourceServerUrl) === routeAddress(text(key,'ServerUrl'))
                && !(r.SourceRoomCode === r.TargetRoomCode && routeAddress(r.SourceServerUrl) === routeAddress(r.TargetServerUrl))) };
    }
    const sameKey = (a,b) => Object.keys(a).every(key => a[key] === b[key]);
    function namesFrom(raw, groupsKey = 'Groups', clicksKey = 'OneClicks') {
        const groups = get(raw,groupsKey), clicks = get(raw,clicksKey);
        const valid = names => Array.isArray(names) && names.every(n => nonempty(n) && n.length <= 1024) && new Set(names).size === names.length;
        return valid(groups) && valid(clicks) ? [...groups.map(Name => ({ Name, Type:'group' })),
            ...clicks.map(Name => ({ Name, Type:'onedragon' }))] : null;
    }
    const catalogRequest = (profileId,type = 'group',name = '',workflowId = '',explicit = false) => ({
        ProfileId:profileId, Type:type, Name:name, WorkflowId:workflowId, ExplicitRefresh:explicit });
    function readCatalogTarget(raw, requested) {
        const t = get(raw,'Target'), nodes = get(t,'Nodes');
        if (!t || text(t,'Type') !== requested.Type || !nonempty(get(t,'Revision'))
            || (requested.Type === 'workflow' ? text(t,'WorkflowId') !== requested.WorkflowId : text(t,'Name') !== requested.Name)
            || !Array.isArray(nodes) || nodes.some(n => !nonempty(get(n,'TaskId'))) || new Set(nodes.map(n => text(n,'TaskId'))).size !== nodes.length)
            return null;
        return { type:text(t,'Type'), name:text(t,'Name'), workflowId:text(t,'WorkflowId'), revision:text(t,'Revision'),
            nodes:nodes.map((n,i) => ({ id:text(n,'TaskId'), name:text(n,'Name'), enabled:get(n,'Enabled') === true, number:i+1 })) };
    }
    function readWorkflows(raw) {
        const flows = list(raw,'Workflows').map(w => ({ id:text(w,'WorkflowId'), name:text(w,'Name') })).filter(w => w.id && w.name);
        return flows.filter(w => flows.filter(f => f.id === w.id).length === 1);
    }
    const quickValue = b => (b.Type === 'onedragon' ? 'ONEDRAGON:' : 'GROUP:') + b.Name;
    const quickLabel = value => {
        const raw = str(value);
        if (raw.startsWith('GROUP:') && nonempty(raw.slice(6))) return '配置组 · ' + raw.slice(6);
        if (raw.startsWith('ONEDRAGON:') && nonempty(raw.slice(10))) return '一条龙 · ' + raw.slice(10);
        return raw ? raw + '（待核对）' : '未绑定';
    };
    function verifyReadback(before, changes, after) {
        return sameKey(before.key,after.key)
            && (!has(changes,'Bindings') || sameBindings(changes.Bindings,after.bindings))
            && (!has(changes,'ExpectedPlayers') || changes.ExpectedPlayers === after.people)
            && (!has(changes,'Schedule') || changes.Schedule === after.schedule && !after.requiresConfirmation && !after.suppressed)
            && (!has(changes,'Completion') || sameCompletion(changes.Completion,after.completion))
            && (!has(changes,'QuickCommands') || Object.entries(changes.QuickCommands).every(([key,value]) => after.quick.get(key) === value));
    }

    function el(tag, className, value) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (value !== undefined) node.textContent = value;
        return node;
    }
    function button(value, action, className = '') {
        const b = el('button','ro-button' + (className ? ' ' + className : ''),value);
        b.type = 'button';
        b.addEventListener('click',action);
        return b;
    }
    function setText(node, value) { if (node.textContent !== value) node.textContent = value; }
    function focus(node) { node?.focus({ preventScroll:true }); }
    function hint(value, extra = '') { return el('p','ro-hint' + (extra ? ' ' + extra : ''),value); }
    function live(node) { node.setAttribute('role','status'); node.setAttribute('aria-live','polite'); node.setAttribute('aria-atomic','true'); return node; }
    function status(raw, stale = false) {
        const original = text(raw,'Kind'), kind = !stale && has(kinds,original) ? original : 'unknown';
        const label = stale || !has(kinds,original) ? '待核对' : text(raw,'Text') || '待核对';
        const root = el('span','ro-status ro-status-' + kind);
        const glyph = el('span','ro-glyph',kinds[kind]); glyph.setAttribute('aria-hidden','true');
        root.append(glyph,el('span','',label));
        return root;
    }
    function taskRows(rows, label, stale) {
        const ol = el('ol','ro-tasks'); ol.setAttribute('aria-label',label);
        rows.forEach((task,i) => {
            const item = el('li','ro-task');
            const number = get(task,'Number');
            const n = el('span','ro-task-number',Number.isInteger(number) && number > 0 ? String(number) : String(i+1));
            const description = el('div','ro-task-name');
            description.append(el('strong','',text(task,'Name') || '名称待核对'),el('span','ro-muted',text(task,'Type') || '类型待核对'));
            const detail = stale ? '' : text(get(task,'Status'),'Detail');
            if (detail) description.append(hint(detail));
            item.append(n,description,status(get(task,'Status'),stale)); ol.append(item);
        });
        return ol;
    }
    function pair(label, value) { const root = el('div','ro-kv'); root.append(el('dt','',label),el('dd','',value || '待核对')); return root; }
    function capturedLabel(value) {
        const time = new Date(value);
        if (!Number.isFinite(time.getTime())) return '';
        return time.toLocaleString('zh-CN',{month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit',hour12:false});
    }
    function confirmAction(message) { return window.confirm(message); }

    class OrchestrationView {
        constructor(host, adapter) {
            this.host = host; this.adapter = adapter; this.id = 'room-orchestration-' + ++serial;
            this.target = identity(adapter.target); this.targetName = text(adapter.target,'Name') || text(adapter.target,'PlayerName') || '所选执行端';
            const prefixes = get(adapter.target,'RequestPrefixes') || get(adapter.target,'RoomRequestPrefixes');
            this.receiptPrefix = text(prefixes,'Orchestration');
            this.disposed = false; this.readyState = null; this.epoch = 0; this.fetchSeq = 0; this.installedSeq = 0;
            this.snapshot = null; this.receivedAt = 0; this.staleReason = '正在读取执行端房间配置…';
            this.reading = null; this.editor = null; this.pending = null; this.notice = ''; this.signature = ''; this.cardNodes = new Map();
            this.root = el('section','room-orchestration'); this.root.setAttribute('aria-label','执行端房间任务编排');
            const bar = el('header','ro-login-bar');
            const loginCopy = el('div','ro-login-copy');
            this.login = el('h2','ro-login-title','当前登录：尚未确认'); this.context = el('p','ro-login-context');
            this.executor = el('p','ro-hint'); loginCopy.append(this.login,this.context,this.executor);
            const actions = el('div','ro-login-actions');
            this.current = el('div','ro-current-status'); this.capture = el('p','ro-capture');
            this.refreshButton = button('刷新状态',() => { void this.refresh(); });
            actions.append(this.current,this.capture,this.refreshButton); bar.append(loginCopy,actions);
            this.feedback = live(el('p','ro-view-feedback')); this.feedback.hidden = true;
            this.pendingBar = el('div','ro-pending'); this.pendingBar.hidden = true;
            this.pendingText = live(el('p','ro-pending-text'));
            this.lookupButton = button('核对上次操作',() => { void this.reconcile(); });
            this.cancelRefreshButton = button('取消目录刷新',() => { void this.cancelRefresh(); },'ro-danger-button');
            const pendingActions = el('div','ro-actions'); pendingActions.append(this.lookupButton,this.cancelRefreshButton);
            this.pendingBar.append(this.pendingText,pendingActions);
            this.timeline = el('div','ro-timeline'); this.timeline.setAttribute('aria-label','房间任务及接续关系');
            this.root.append(bar,this.feedback,this.pendingBar,this.timeline); host.append(this.root);
            this.installDragScroll();
            this.render();
            this.visibility = () => { if (!document.hidden) void this.refresh(); };
            document.addEventListener('visibilitychange',this.visibility);
            this.healthTimer = window.setInterval(() => { this.sampleReady(); this.render(); },1000);
            this.pollTimer = window.setInterval(() => { if (!document.hidden) void this.refresh(); },REFRESH_MS);
            void this.refresh();
        }
        canTrack() { return typeof this.adapter.operationId === 'function' || nonempty(this.receiptPrefix); }
        operationId(operation,id) {
            const result = typeof this.adapter.operationId === 'function'
                ? this.adapter.operationId(operation,id) : this.receiptPrefix && this.receiptPrefix + id;
            if (!nonempty(result) || result === id) throw new Error('尚未取得该执行端的编排操作编号范围，未发送。');
            return result;
        }
        ready() {
            try { return !this.disposed && sameIdentity(this.target,identity(this.adapter.target)) && this.adapter.ready() === true; }
            catch { return false; }
        }
        sampleReady() {
            if (this.disposed) return false;
            const ready = this.ready();
            if (!ready) {
                if (this.readyState !== false) { ++this.epoch; this.reading = null; }
                this.markStale('所选执行端离线、授权或原房间尚未确认；只保留上次读取内容。');
            } else if (this.receivedAt && Date.now() - this.receivedAt >= FRESH_MS) {
                this.markStale('执行端快照已过期，请重新读取。');
            }
            this.readyState = ready;
            return ready;
        }
        markStale(reason) {
            this.staleReason = reason;
            if (this.editor) this.editor.invalidate(reason);
        }
        fresh() { return this.ready() && this.snapshot && !this.staleReason && Date.now() - this.receivedAt < FRESH_MS; }
        currentCard(id) {
            const cards = list(this.snapshot?.presentation,'Cards').filter(c => text(c,'Id') === id);
            return id && cards.length === 1 ? cards[0] : null;
        }
        externalPending() {
            try { return !!this.adapter.pending?.(); } catch { return true; }
        }
        canEdit(id) { return !!this.fresh() && this.canTrack() && !this.pending && !this.externalPending() && get(this.currentCard(id),'CanEdit') === true; }
        assertRoute(epoch) {
            if (!this.sampleReady() || this.epoch !== epoch || this.disposed)
                throw new Error('原执行端或连接已变化，请重新核对。');
        }
        async readRaw(operation,payload,options = {}) {
            const epoch = this.epoch;
            this.assertRoute(epoch);
            const reply = await this.adapter.read(operation,payload,{ timeout:25000, ...options, requestId:requestId() });
            this.assertRoute(epoch);
            this.checkReply(reply);
            return reply;
        }
        checkReply(reply) {
            if (!reply || !nonempty(get(reply,'Status')) || text(reply,'TargetInstanceId') && text(reply,'TargetInstanceId') !== this.target.instanceId)
                throw new Error('执行端回复身份或格式不匹配，结果未确认。');
        }
        async read(operation,payload,options) {
            const reply = await this.readRaw(operation,payload,options);
            if (statusOf(reply) !== 'ok') throw new Error(messageOf(reply,'执行端未确认读取成功。'));
            return reply;
        }
        async fetchPresentation() {
            const seq = ++this.fetchSeq, epoch = this.epoch;
            const reply = await this.read(OP.get,undefined);
            return { seq, epoch, value:readPresentation(get(reply,'Data'),this.target) };
        }
        installPresentation(response) {
            this.assertRoute(response.epoch);
            if (response.seq < this.installedSeq) return;
            this.installedSeq = response.seq; this.snapshot = response.value; this.receivedAt = Date.now(); this.staleReason = '';
            if (this.editor) {
                const card = this.currentCard(this.editor.profileId);
                if (get(card,'CanEdit') !== true || text(card,'Identity') !== text(this.editor.card,'Identity')
                    || loginIdentity(this.snapshot.presentation) !== this.editor.loginIdentity)
                    this.editor.invalidate('实际登录房间、原档案身份或编辑权限已变化。请重新打开编辑器。');
            }
            this.render();
        }
        refresh() {
            if (this.disposed) return Promise.resolve(false);
            if (!this.sampleReady()) { this.render(); return Promise.resolve(false); }
            if (this.reading) return this.reading;
            const epoch = this.epoch, seq = this.fetchSeq + 1;
            const task = (async () => {
                try {
                    this.installPresentation(await this.fetchPresentation());
                    const ed = this.editor;
                    if (ed?.original && !ed.stale && !ed.preparing && !this.pending && !ed.loadingNames && !ed.loadingCatalog)
                        await ed.checkRevision();
                    return true;
                } catch (error) {
                    if (!this.disposed && epoch === this.epoch && seq >= this.installedSeq)
                        this.markStale('执行端状态未确认：' + errorText(error));
                    return false;
                }
            })();
            this.reading = task;
            this.render();
            void task.finally(() => { if (this.reading === task) { this.reading = null; this.render(); } });
            return task;
        }
        notify(message, isError = false) {
            this.notice = message;
            try { this.adapter.onMessage?.(message,isError); } catch { /* UI callbacks cannot alter receipts */ }
            this.render();
        }
        render() {
            if (this.disposed) return;
            const p = this.snapshot?.presentation, stale = !this.fresh();
            setText(this.login,stale ? '当前登录：待核对' : text(p,'LoginText','当前登录：尚未确认'));
            setText(this.context,stale ? (p ? '上次快照 · ' + text(p,'LoginText') : this.staleReason) : text(p,'LoginContext'));
            const identityLabel = this.snapshot?.executorName || this.targetName;
            setText(this.executor,`执行端：${identityLabel}` + (this.target.instanceId ? ' · 实例 ' + this.target.instanceId.slice(0,8) : '')
                + (this.target.machineId ? ' · 设备 ' + this.target.machineId.slice(0,8) : ''));
            const capture = capturedLabel(this.snapshot?.capturedAt);
            setText(this.capture,capture ? (stale ? '上次快照 ' : '快照 ') + capture : '');
            const current = stale ? null : get(p,'CurrentStatus');
            const currentKey = JSON.stringify([current,stale]);
            if (currentKey !== this.currentKey) { this.currentKey = currentKey; this.current.replaceChildren(status(current,stale)); }
            this.refreshButton.disabled = !this.ready() || !!this.reading;
            setText(this.refreshButton,this.reading ? '读取中…' : '刷新状态');
            const feedback = [stale ? this.staleReason : !this.canTrack() ? '未确认编排操作编号范围，仅可查看。' : '',
                !this.pending && this.externalPending() ? '上次房间操作尚未核对，请先使用上级页面提供的核对入口。不会重发。' : '',this.notice].filter(Boolean).join('\n');
            this.feedback.hidden = !feedback; setText(this.feedback,feedback);
            const cards = list(p,'Cards'), signature = JSON.stringify([cards,stale]);
            if (signature !== this.signature) {
                this.signature = signature;
                const activeKey = this.timeline.contains(document.activeElement) ? document.activeElement.dataset.roFocus : '';
                // Preserve unchanged cards/selection and browser scroll anchoring during live
                // updates. A changed topology is rebuilt so its textual reference links stay true.
                const topology = cards.map(c => [text(c,'Id'),text(c,'Name'),text(c,'Identity')]);
                const nextNodes = new Map();
                const nodes = cards.map((card,i) => {
                    const key = i + ':' + text(card,'Id'), signature = JSON.stringify([card,stale,topology]);
                    const previous = this.cardNodes.get(key);
                    const entry = previous?.signature === signature ? previous : { signature, node:this.renderCard(card,i,cards,stale) };
                    nextNodes.set(key,entry); return entry.node;
                });
                if (!nodes.length) nodes.push(el('p','ro-empty',p ? '执行端没有已保存的房间档案。请先通过房间管理添加。' : '尚无可显示的执行端快照。'));
                for (const child of Array.from(this.timeline.children)) if (!nodes.includes(child)) child.remove();
                nodes.forEach((node,i) => { if (this.timeline.children[i] !== node) this.timeline.insertBefore(node,this.timeline.children[i] || null); });
                this.cardNodes = nextNodes;
                if (activeKey && !this.timeline.contains(document.activeElement)) {
                    const next = Array.from(this.timeline.querySelectorAll('[data-ro-focus]')).find(n => n.dataset.roFocus === activeKey);
                    if (next && !next.disabled) focus(next);
                }
            }
            for (const b of this.timeline.querySelectorAll('[data-ro-edit]')) b.disabled = !this.canEdit(b.dataset.roEdit) || !!this.editor;
            this.renderPending(); this.editor?.updateControls();
        }
        cardAnchor(card,i) { return this.id + '-card-' + i; }
        renderCard(card,i,cards,stale) {
            const id = text(card,'Id'), name = text(card,'Name') || '未命名房间', current = !stale && get(card,'Current') === true;
            const wrap = el('div','ro-step');
            const section = text(card,'SectionTitle'); if (section) wrap.append(el('h3','ro-section-label',section));
            const lane = el('div','ro-lane');
            const article = el('article','ro-card' + (current ? ' ro-card-current' : ''));
            const heading = el('div','ro-card-head');
            const titleBlock = el('div','ro-card-title');
            const title = el('h3','',name); title.id = this.cardAnchor(card,i); title.tabIndex = -1;
            titleBlock.append(title,el('p','ro-room-identity',text(card,'Identity') || '房间身份待核对'));
            article.setAttribute('aria-labelledby',title.id);
            const badges = el('div','ro-card-badges');
            if (current) badges.append(el('span','ro-current-label','当前登录'));
            badges.append(status(get(card,'Status'),stale)); heading.append(titleBlock,badges);
            article.append(heading);
            const detail = stale ? '上次读取的任务与配置，当前进度尚未确认。' : text(card,'Detail') || text(get(card,'Status'),'Detail');
            if (detail) article.append(hint(detail,'ro-card-detail'));
            const updated = text(card,'UpdatedText'); if (updated) article.append(el('p','ro-record-time','记录更新：' + updated));
            const grid = el('div','ro-card-grid'), tasks = el('div','ro-task-area');
            const taskCaption = stale ? '上次读取的任务' : text(card,'TasksCaption') || '联机任务绑定';
            const taskHead = el('div','ro-task-heading'); taskHead.append(el('h4','',taskCaption));
            const unconfirmed = stale || get(card,'ProgressUnconfirmed') === true;
            const progress = unconfirmed ? '进度待核对' : get(card,'HasConfirmedProgress') === true ? text(card,'ProgressText') : '';
            if (progress) taskHead.append(el('span','ro-progress',progress));
            tasks.append(taskHead);
            const rows = list(card,'Tasks'); tasks.append(rows.length ? taskRows(rows,taskCaption,stale)
                : hint(!Array.isArray(get(card,'Tasks')) ? '任务记录尚未确认。' : '未绑定联机任务。'));
            const next = list(card,'NextTasks');
            if (next.length) {
                const nextBlock = el('div','ro-next-tasks'); nextBlock.append(el('h4','','下轮任务绑定'),hint('配置已变更；不代表本轮已执行。'),taskRows(next,'下轮任务绑定',stale));
                tasks.append(nextBlock);
            }
            const previous = get(card,'PreviousEdge'); if (previous) tasks.append(this.renderEdge(previous,card,cards,stale,true,false));
            const settings = el('aside','ro-settings'); settings.setAttribute('aria-label',name+'配置摘要');
            const dl = el('dl',''); dl.append(pair('开锄人数',text(card,'ExpectedPlayers')),pair('定时上线',text(card,'Schedule')),pair('完成后动作',text(card,'Completion')));
            const edit = button('编辑配置',() => { void this.openEditor(id); });
            edit.dataset.roEdit = id; edit.dataset.roFocus = 'edit:' + id + ':' + i;
            edit.setAttribute('aria-label','编辑房间配置：' + name + ' ' + text(card,'Identity'));
            edit.disabled = !this.canEdit(id) || !!this.editor;
            settings.append(dl,edit); grid.append(tasks,settings); article.append(grid);
            lane.append(article);
            const edge = get(card,'Edge');
            const continues = !!edge && get(edge,'ContinuesToNext') === true && i < cards.length - 1;
            if (continues) {
                lane.classList.add('ro-lane-continues');
                if (stale || text(get(edge,'SwitchStatus'),'Kind') !== 'done') lane.classList.add('ro-lane-unconfirmed');
            }
            if (edge) lane.append(this.renderEdge(edge,card,cards,stale,false,continues));
            wrap.append(lane); return wrap;
        }
        renderEdge(edge,source,cards,stale,previous,continues) {
            const box = el('div',previous ? 'ro-previous-edge' : 'ro-edge' + (continues ? ' ro-edge-continues' : ''));
            if (previous) box.append(el('h4','','最近接续记录（历史）'));
            const relation = text(edge,'Relation') || '接续关系待核对', head = el('div','ro-edge-head');
            head.append(el('strong','',relation),status(get(edge,'SwitchStatus'),stale)); box.append(head);
            const post = text(edge,'PostAction'), postStatus = get(edge,'PostStatus');
            if (post) {
                const line = el('div','ro-edge-post'); line.append(el('span','',post));
                if (postStatus) line.append(status(postStatus,stale));
                else if (!post.endsWith('待命')) line.append(hint(stale ? '后继状态待核对' : '暂无后继回执'));
                box.append(line);
            }
            const online = stale ? '' : text(edge,'OnlineResult'); if (online) box.append(hint(online));
            const detail = stale ? '当前接续结果待核对。' : text(edge,'Detail'); if (detail) box.append(hint(detail));
            // The presentation carries a label rather than a target ID. Do not guess a link if
            // two saved profiles have the same label/code. Only an unambiguous displayed relation links.
            if (!previous && !continues && relation.endsWith('（引用已展示房间）')) {
                const matches = cards.map((card,i) => ({card,i})).filter(({card}) => {
                    const code = text(card,'Identity').split(' · ')[0];
                    return relation === `${text(source,'Name')} → ${text(card,'Name')} · ${code}（引用已展示房间）`;
                });
                if (matches.length === 1) {
                    const anchor = this.cardAnchor(matches[0].card,matches[0].i);
                    const link = button('定位已展示房间',() => {
                        const target = document.getElementById(anchor); focus(target); target?.scrollIntoView({ block:'start' });
                    },'ro-text-button');
                    link.dataset.roFocus = 'ref:' + text(source,'Id') + ':' + anchor;
                    link.setAttribute('aria-label','定位房间：' + text(matches[0].card,'Name') + ' ' + text(matches[0].card,'Identity'));
                    box.append(link);
                }
            }
            return box;
        }
        async openEditor(profileId) {
            this.sampleReady();
            if (this.disposed || this.editor || !this.canEdit(profileId)) { this.render(); return; }
            const ed = new RoomEditor(this,this.currentCard(profileId)); this.editor = ed;
            try { ed.dialog.showModal(); focus(ed.closeButton); }
            catch (error) { this.closeEditor(ed); this.notify('无法打开房间编辑器：' + errorText(error),true); return; }
            this.render(); await ed.load();
        }
        closeEditor(ed) {
            if (this.editor !== ed) return;
            ed.destroy(); this.editor = null; this.render();
            const replacement = Array.from(this.timeline.querySelectorAll('[data-ro-edit]')).find(b => b.dataset.roEdit === ed.profileId && !b.disabled);
            focus(replacement || this.refreshButton);
        }
        renderPending() {
            const p = this.pending;
            this.pendingBar.hidden = !p || !!this.editor;
            if (!p) return;
            setText(this.pendingText,p.message);
            this.lookupButton.disabled = !this.ready() || p.inFlight || p.checking || p.readback;
            this.cancelRefreshButton.hidden = p.kind === 'save';
            this.cancelRefreshButton.disabled = !this.ready() || p.canceling || p.cancelRequested;
        }
        async startTracked(ed,kind,payload,changes) {
            ed.assertUsable();
            if (this.pending || this.externalPending()) throw new Error('上次操作尚未确认，请先核对。');
            const operation = OP[kind], id = requestId();
            const p = { kind, operation, id:this.operationId(operation,id), requestId:id, ed, payload, changes, original:ed.original,
                profileId:ed.profileId, inFlight:true, checking:false, readback:false, cancelRequested:false,
                message:kind === 'save' ? '正在发送保存请求…' : '正在刷新执行端目录…' };
            this.pending = p; this.notice = ''; this.render();
            const options = { requestId:id, timeout:25000, waitBudget:kind === 'save' ? 24000 : 190000,
                label:kind === 'save' ? '保存房间配置' : '刷新房间任务目录', scope:p.profileId,
                onStage:(stage,message) => {
                    if (this.disposed || this.pending !== p) return;
                    p.message = stage === 'done' ? '执行端已回复，正在核对结果…' : str(message) || p.message;
                    this.render();
                } };
            if (kind === 'save') options.expectedRevision = ed.original.revision;
            let reply;
            try { reply = await this.adapter.operate(operation,payload,options); }
            catch (error) { reply = { status:'unknown', message:errorText(error) }; }
            p.inFlight = false;
            await this.finishTracked(p,reply,false);
        }
        async finishTracked(p,reply,lookup) {
            if (this.disposed || this.pending !== p) return;
            try {
                this.checkReply(reply);
                const state = statusOf(reply), receipt = text(reply,'OperationId');
                if (!lookup && state === 'blocked') {
                    // Adapter 明确阻止发送新操作；可能附带尚未核对的旧操作 ID，不能把它认成新请求的未知回执。
                    this.pending = null;
                    const message = messageOf(reply,'上次操作待核对，本次未发送。');
                    if (this.editor === p.ed) p.ed.showMessage(message,true);
                    this.notify(message,true); return;
                }
                if (receipt && receipt !== p.id || state === 'ok' && receipt !== p.id || lookup && receipt !== p.id)
                    throw new Error('未收到本次操作编号对应的终态回执。');
                if (state === 'ok') {
                    if (p.kind === 'save') {
                        // An ok without executor snapshot is not the successful save contract.
                        readPresentation(get(reply,'Data'),this.target);
                        p.receipt = reply;
                        await this.readbackSave(p);
                    } else {
                        this.pending = null;
                        const ed = this.editor === p.ed && !p.ed.stale && this.ready() ? p.ed : null;
                        if (ed) {
                            if (p.kind === 'names') {
                                ed.installNames(namesFrom(get(reply,'Data')),'执行端未返回有效名称目录；可能刷新失败或已取消。');
                                void ed.loadCatalog();
                            } else ed.installCatalog(get(reply,'Data'),p.payload,true);
                            ed.showMessage('');
                        } else this.notify('目录刷新已收到本次终态回复；请重新读取执行端目录。');
                        this.render();
                    }
                    return;
                }
                // Direct adapter rejection/denial/blocked before receipt is a known non-success.
                // A missing operation.get receipt is NOT proof that the original write never ran.
                if (state === 'rejected' || !lookup && state === 'denied') {
                    this.pending = null;
                    const message = messageOf(reply,'执行端拒绝本次操作。');
                    if (this.editor === p.ed) {
                        p.ed.showMessage(message,true);
                        if (state === 'denied') p.ed.invalidate('本人授权尚未确认，请重新打开编辑器。');
                        else if (p.kind === 'save') await p.ed.checkRevision();
                    } else this.notify(message,true);
                    this.render(); return;
                }
                p.message = (state === 'accepted' ? '操作已受理，尚无终态。' : '操作结果待核对。')
                    + ' 不会自动重发。' + (text(reply,'Message') ? '\n' + text(reply,'Message') : '') + '\n操作编号：' + p.id;
            } catch (error) {
                p.message = '操作结果待核对：' + errorText(error) + ' 不会自动重发。\n操作编号：' + p.id;
            }
            this.render();
        }
        async readbackSave(p) {
            p.readback = true; p.message = '已收到本次保存回执，正在重新读取执行端档案和编排快照…'; this.render();
            try {
                if (!this.sampleReady()) throw new Error('原执行端尚未就绪');
                const epoch = this.epoch;
                const after = readEdit(await this.read(OP.edit,p.profileId),this.target,p.profileId);
                this.assertRoute(epoch);
                const snapshot = await this.fetchPresentation();
                this.assertRoute(epoch);
                if (this.disposed || this.pending !== p) return;
                this.installPresentation(snapshot);
                this.pending = null;
                if (!verifyReadback(p.original,p.changes,after)) {
                    const message = '本次保存回执已确认，但当前档案读回内容与提交不一致。请重新打开并核对实际配置；本次不显示为保存成功。';
                    if (this.editor === p.ed) { p.ed.invalidate(message); p.ed.showMessage(message,true); }
                    this.notify(message,true); return;
                }
                if (this.editor === p.ed) this.closeEditor(p.ed);
                this.notify('已确认保存并重新读取执行端房间配置。');
            } catch (error) {
                p.message = '本次保存回执已收到，但配置回读尚未确认：' + errorText(error)
                    + '。请核对，不要重发。\n操作编号：' + p.id;
            } finally { p.readback = false; this.render(); }
        }
        async reconcile() {
            const p = this.pending;
            if (!p || p.inFlight || p.checking || p.readback || !this.sampleReady()) { this.render(); return; }
            p.checking = true; this.render();
            try {
                if (p.kind === 'save' && p.receipt) await this.readbackSave(p);
                else await this.finishTracked(p,await this.readRaw(OP.receipt,p.id),true);
            } catch (error) {
                p.message = '原操作终态待核对：' + errorText(error) + '。没有重发。\n操作编号：' + p.id;
            } finally { p.checking = false; this.render(); }
        }
        async cancelRefresh() {
            const p = this.pending;
            if (!p || p.kind === 'save' || p.canceling || p.cancelRequested || !this.sampleReady()) { this.render(); return; }
            p.canceling = true; this.render();
            try {
                const reply = await this.adapter.operate(OP.cancel,p.id,{ requestId:requestId(), timeout:25000,
                    label:'请求取消目录刷新', scope:p.profileId, allowWhilePending:true });
                if (this.disposed || this.pending !== p) return;
                this.checkReply(reply);
                p.cancelRequested = statusOf(reply) === 'ok';
                p.message = (p.cancelRequested ? '已请求取消目录刷新，等待原操作终态；不代表已取消。'
                    : '取消请求未确认：' + messageOf(reply,'请核对原操作。')) + '\n操作编号：' + p.id;
            } catch (error) {
                p.message = '取消请求结果待核对：' + errorText(error) + '\n操作编号：' + p.id;
            } finally { p.canceling = false; this.render(); }
        }
        installDragScroll() {
            let drag = null;
            const finish = event => {
                if (!drag || event.pointerId !== drag.pointer) return;
                drag = null; this.root.classList.remove('ro-dragging');
                if (this.root.hasPointerCapture?.(event.pointerId)) this.root.releasePointerCapture(event.pointerId);
            };
            // Desktop convenience only: a blank lane/background can be dragged vertically; no
            // pointer capture on text, cards, links, inputs, touch devices, or native scrollbars.
            this.root.addEventListener('pointerdown',event => {
                if (event.pointerType !== 'mouse' || event.button !== 0 || !window.matchMedia?.('(min-width: 861px) and (pointer: fine)').matches
                    || ![this.root,this.timeline].includes(event.target) && !event.target.classList.contains('ro-lane') && !event.target.classList.contains('ro-step')) return;
                let scroll = this.root.parentElement;
                while (scroll && (!/(auto|scroll)/.test(window.getComputedStyle(scroll).overflowY) || scroll.scrollHeight <= scroll.clientHeight)) scroll = scroll.parentElement;
                scroll ||= document.scrollingElement;
                if (scroll) drag = { pointer:event.pointerId, y:event.clientY, top:scroll.scrollTop, scroll, active:false };
            });
            this.root.addEventListener('pointermove',event => {
                if (!drag || event.pointerId !== drag.pointer || !(event.buttons & 1)) { if (drag) finish({pointerId:drag.pointer}); return; }
                if (!drag.active && Math.abs(drag.y - event.clientY) < 6) return;
                if (!drag.active) { drag.active = true; this.root.setPointerCapture?.(drag.pointer); this.root.classList.add('ro-dragging'); }
                event.preventDefault(); drag.scroll.scrollTop = drag.top + drag.y - event.clientY;
            });
            for (const name of ['pointerup','pointercancel','lostpointercapture','pointerleave']) this.root.addEventListener(name,event => {
                if (name !== 'pointerleave' || !drag?.active) finish(event);
            });
        }
        dispose() {
            if (this.disposed) return;
            this.disposed = true; ++this.epoch;
            window.clearInterval(this.healthTimer); window.clearInterval(this.pollTimer);
            document.removeEventListener('visibilitychange',this.visibility);
            this.editor?.destroy(); this.editor = null; this.root.remove();
            if (mounts.get(this.host) === this) mounts.delete(this.host);
            // No implicit retry/cancel or new target selection. The adapter owns any outstanding
            // durable operation even after the view is unmounted.
        }
    }

    class RoomEditor {
        constructor(view,card) {
            this.view = view; this.profileId = text(card,'Id'); this.card = card;
            this.loginIdentity = loginIdentity(view.snapshot?.presentation);
            this.epoch = view.epoch; this.closed = false; this.stale = false; this.preparing = false;
            this.original = null; this.names = null; this.workflows = []; this.catalog = null; this.completionTouched = false; this.fieldSerial = 0;
            this.namesSerial = 0; this.catalogSerial = 0; this.loadingNames = false; this.loadingCatalog = false;
            this.dialog = el('dialog','ro-editor'); this.dialog.setAttribute('aria-labelledby',view.id + '-editor-title');
            this.frame = el('div','ro-editor-frame');
            const head = el('header','ro-editor-head');
            this.title = el('h2','','编辑配置 · ' + text(card,'Name')); this.title.id = view.id + '-editor-title';
            this.identityLine = hint(text(card,'Identity') + ' · 执行端：' + view.targetName);
            head.append(this.title,this.identityLine);
            this.body = el('div','ro-editor-body');
            this.fields = el('fieldset','ro-editor-fields'); this.fields.disabled = true;
            this.fields.append(hint('正在读取该房间的实际执行端配置…'));
            this.body.append(this.fields);
            this.error = live(el('p','ro-editor-message')); this.error.id = view.id + '-editor-message'; this.error.tabIndex = -1; this.error.hidden = true;
            this.pendingBox = el('div','ro-editor-pending'); this.pendingBox.hidden = true;
            this.pendingStatus = live(el('p','ro-pending-text'));
            this.pendingLookup = button('核对原操作',() => { void view.reconcile(); });
            this.pendingCancel = button('取消目录刷新',() => { void view.cancelRefresh(); },'ro-danger-button');
            const pendingActions = el('div','ro-actions'); pendingActions.append(this.pendingLookup,this.pendingCancel);
            this.pendingBox.append(this.pendingStatus,pendingActions);
            const footer = el('footer','ro-editor-footer');
            const actions = el('div','ro-editor-actions');
            this.closeButton = button('取消',() => this.requestClose());
            this.saveButton = button('保存配置',() => { void this.save(); },'ro-primary'); this.saveButton.disabled = true;
            actions.append(this.closeButton,this.saveButton); footer.append(this.error,this.pendingBox,actions);
            this.frame.append(head,this.body,footer); this.dialog.append(this.frame);
            this.dialog.addEventListener('cancel',event => { event.preventDefault(); this.requestClose(); });
            view.root.append(this.dialog);
        }
        async load() {
            try {
                const reply = await this.view.read(OP.edit,this.profileId);
                if (!this.active() || this.stale) return;
                this.original = readEdit(reply,this.view.target,this.profileId);
                const o = this.original, c = o.completion;
                if (!sameCardIdentity(this.card,o.key)) throw new Error('原房间档案身份已变化，请重新读取。');
                this.bindings = o.bindings.map(b => ({...b})); this.quick = new Map(o.quick);
                this.completionDraft = { action:actionName(c), room:c.RoomSwitch, after:c.RoomSwitch?.RunAfterSwitch === true,
                    type:c.SpecifiedTaskType, name:c.SpecifiedTaskName, workflowId:c.SpecifiedWorkflowId,
                    mode:c.SpecifiedStartMode, nodeId:c.SpecifiedStartTaskId, nodeName:c.SpecifiedStartTaskName,
                    revision:c.SpecifiedConfigRevision, priority:String(c.SpecifiedTaskPriority) };
                this.title.textContent = '编辑配置 · ' + o.name;
                this.identityLine.textContent = `房间 ${o.key.RoomCode} · ${o.key.ServerUrl} · 执行端：${this.view.targetName}`;
                this.buildForm(); this.updateControls();
                focus(this.peopleInput);
                void this.loadNames(); void this.loadCatalog();
            } catch (error) { if (this.active()) this.invalidate('未能读取可编辑配置：' + errorText(error)); }
        }
        active() { return !this.closed && !this.view.disposed && this.view.editor === this; }
        assertUsable() {
            if (!this.active() || this.stale || !this.original || !this.view.sampleReady() || !this.view.fresh()
                || this.epoch !== this.view.epoch || get(this.view.currentCard(this.profileId),'CanEdit') !== true) {
                this.invalidate('执行端、房间或快照已变化，请关闭并重新读取。');
                throw new Error('原编辑状态已过期，请重新打开编辑器。');
            }
        }
        invalidate(reason) {
            if (this.closed || this.stale) return;
            this.stale = true; this.showMessage(reason + ' 未发送的新修改不会写入；请关闭并重新打开编辑器。',true);
            this.updateControls();
        }
        async checkRevision() {
            if (!this.active() || this.stale || !this.original || this.checkingRevision) return;
            this.checkingRevision = true; this.updateControls();
            try {
                const latest = readEdit(await this.view.read(OP.edit,this.profileId),this.view.target,this.profileId);
                if (this.active() && (latest.revision !== this.original.revision || !sameKey(latest.key,this.original.key)))
                    this.invalidate('执行端该房间的配置或原修订已变化。');
            } catch (error) { if (this.active()) this.invalidate('无法确认原编辑修订：' + errorText(error)); }
            finally { this.checkingRevision = false; this.updateControls(); }
        }
        section(title, description) {
            const node = el('section','ro-form-section'); node.append(el('h3','',title));
            if (description) node.append(hint(description)); this.fields.append(node); return node;
        }
        label(name,control,description = '') {
            control.id ||= this.view.id + '-field-' + ++this.fieldSerial;
            const row = el('div','ro-field'), label = el('label','ro-field-label',name);
            label.htmlFor = control.id; row.append(label,control);
            if (description) {
                const note = hint(description); note.id = control.id + '-hint';
                control.setAttribute('aria-describedby',note.id); row.append(note);
            }
            return row;
        }
        input(role,value) {
            const node = el('input',''); node.type = 'text'; node.value = value; node.dataset.roField = role;
            node.autocomplete = 'off'; return node;
        }
        select(role) { const node = el('select',''); node.dataset.roField = role; return node; }
        options(select,items,value) {
            select.replaceChildren(...items.map(item => { const option = el('option','',item.label); option.value = item.value; option.disabled = !!item.disabled; return option; }));
            select.value = value;
        }
        buildForm() {
            const o = this.original;
            this.fields.replaceChildren(hint('仅保存此房间在所选执行端的配置，不切换房间、不修改其他成员。已受理批次仍使用冻结的原任务。'));
            const limits = this.section('开锄人数与定时上线');
            const row = el('div','ro-field-grid');
            this.peopleInput = this.input('people',String(o.people)); this.peopleInput.inputMode = 'numeric';
            this.scheduleInput = this.input('schedule',o.schedule); this.scheduleInput.placeholder = 'HH:mm';
            row.append(this.label('预期开锄人数（1–4 人）',this.peopleInput),this.label('定时上线（HH:mm）',this.scheduleInput));
            limits.append(row,hint('当前房间已上线等待开锄或执行本轮任务时，执行端会拒绝人数修改。'),
                hint('24 小时制；留空禁用。重新设置已过时刻不会补触发今天的自动上线。修改其他设置不会解除定时抑制。'));
            this.confirmSchedule = el('input',''); this.confirmSchedule.type = 'checkbox'; this.confirmSchedule.dataset.roField = 'confirmSchedule';
            this.scheduleConfirmRow = el('label','ro-check'); this.scheduleConfirmRow.append(this.confirmSchedule,el('span','','确认重新设置当前定时（即使时间不变）'));
            this.scheduleConfirmRow.hidden = !o.schedule; limits.append(this.scheduleConfirmRow);
            this.scheduleInput.addEventListener('input',() => {
                this.scheduleConfirmRow.hidden = !this.scheduleInput.value.trim();
                if (this.scheduleConfirmRow.hidden) this.confirmSchedule.checked = false;
            });
            if (o.requiresConfirmation || o.suppressed) limits.append(hint('此档案定时尚未确认或处于抑制状态；仅修改时间或明确勾选重新设置才改变该项设置。','ro-warning'));
            const bindings = this.section('联机任务绑定','配置组和一条龙可混合；同轮各成员的项目数量、类型及顺序必须一致，名称可不同。');
            bindings.append(hint(o.hasPeers ? '保存变更时核对该房间成员的绑定结构。' : '暂无已确认的成员快照；实际开批时仍会校验布局。'));
            this.namesStatus = live(hint('正在读取执行端任务目录…')); bindings.append(this.namesStatus);
            const nameActions = el('div','ro-actions');
            this.namesReadButton = button('重新读取目录',() => { void this.loadNames(); });
            this.namesRefreshButton = button('刷新任务目录',() => { void this.explicitRefresh('names'); });
            nameActions.append(this.namesReadButton,this.namesRefreshButton); bindings.append(nameActions,hint('显式刷新可能启动执行端 BGI；普通读取不会启动 BGI。'));
            this.bindingList = el('ol','ro-binding-list'); bindings.append(this.bindingList);
            this.bindingSelect = this.select('addBinding'); this.bindingSelect.addEventListener('change',() => this.updateControls());
            this.addBindingButton = button('添加',() => this.addBinding());
            const addRow = el('div','ro-add-row'); addRow.append(this.label('添加执行端配置组或一条龙',this.bindingSelect),this.addBindingButton);
            this.bindingSelectionHint = hint(''); bindings.append(addRow,this.bindingSelectionHint);
            this.buildCompletion();
            const quick = this.section('本房间的快捷指令','快捷指令仅保存绑定，不在这里执行。清除某条绑定不依赖任务目录。');
            this.quickList = el('div','ro-quick-list'); quick.append(this.quickList);
            this.renderBindings(); this.renderNames(); this.renderCompletion();
        }
        renderBindings(focusIndex = -1,focusRole = '') {
            const rows = this.bindings.map((b,i) => {
                const li = el('li','ro-binding-edit');
                const name = el('div','ro-binding-title'); name.append(el('span','ro-task-number',String(i+1)),el('span','',bindingLabel(b)));
                const buttons = el('div','ro-actions');
                for (const [role,label,action,disabled] of [['up','上移',() => this.moveBinding(i,-1),i===0],
                    ['down','下移',() => this.moveBinding(i,1),i===this.bindings.length-1],['remove','移除',() => this.removeBinding(i),false]]) {
                    const btt = button(label,action,role === 'remove' ? 'ro-danger-button' : '');
                    btt.disabled = disabled; btt.dataset.roBindingAction = role;
                    btt.setAttribute('aria-label',`${label}第 ${i+1} 项 ${bindingLabel(b)}`); buttons.append(btt);
                }
                li.append(name,buttons); return li;
            });
            this.bindingList.replaceChildren(...(rows.length ? rows : [el('li','ro-empty','未绑定任务。添加有效目录项目后才能参加联机批次。')]));
            if (focusIndex >= 0 && rows[focusIndex]) {
                const target = Array.from(rows[focusIndex].querySelectorAll('button')).find(b => b.dataset.roBindingAction === focusRole && !b.disabled)
                    || rows[focusIndex].querySelector('button:not(:disabled)'); focus(target);
            }
        }
        availableBindings() { return (this.names || []).filter(b => !this.bindings.some(item => item.Type === b.Type && item.Name.toUpperCase() === b.Name.toUpperCase())); }
        renderNames() {
            if (!this.bindingSelect) return;
            const available = this.availableBindings(), previous = this.bindingSelect.value;
            this.options(this.bindingSelect,[{value:'',label:'请选择任务'},...available.map(b => ({value:quickValue(b),label:bindingLabel(b)}))],
                available.some(b => quickValue(b) === previous) ? previous : '');
            this.renderQuick(); this.renderTaskNames(); this.updateControls();
        }
        renderQuick() {
            if (!this.quickList) return;
            const active = this.quickList.contains(document.activeElement) ? document.activeElement.dataset.roQuick : '';
            this.quickList.replaceChildren(...Array.from(new Set([...this.quick.keys(),...quickKeys])).map(key => {
                const value = str(this.quick.get(key)); const select = this.select('quick'); select.dataset.roQuick = key;
                const available = (this.names || []).map(b => ({value:quickValue(b),label:bindingLabel(b)}));
                if (value && !available.some(item => item.value === value)) available.unshift({value,label:'原绑定：' + quickLabel(value) + '（待核对）',disabled:true});
                this.options(select,[{value:'',label:'未绑定'},...available],value);
                const preview = hint(quickLabel(value));
                select.addEventListener('change',() => { if (this.stale || this.view.pending || this.preparing) return; this.quick.set(key,select.value); setText(preview,quickLabel(select.value)); });
                const label = this.label(key || '原快捷指令（名称待核对）',select);
                preview.id = select.id + '-preview'; select.setAttribute('aria-describedby',preview.id); label.append(preview); return label;
            }));
            if (active) focus(Array.from(this.quickList.querySelectorAll('select')).find(n => n.dataset.roQuick === active));
        }
        addBinding() {
            if (this.stale || this.view.pending || this.preparing) return;
            const selected = this.availableBindings().find(b => quickValue(b) === this.bindingSelect.value);
            if (!selected) return;
            this.bindings.push({...selected}); this.renderBindings(); this.renderNames(); focus(this.bindingSelect);
        }
        moveBinding(index,delta) {
            if (this.stale || this.view.pending || this.preparing || index+delta < 0 || index+delta >= this.bindings.length) return;
            const [item] = this.bindings.splice(index,1); this.bindings.splice(index+delta,0,item);
            this.renderBindings(index+delta,delta < 0 ? 'up' : 'down');
        }
        removeBinding(index) {
            if (this.stale || this.view.pending || this.preparing) return;
            this.bindings.splice(index,1); this.renderBindings(Math.min(index,this.bindings.length-1),'remove'); this.renderNames();
            if (!this.bindings.length) focus(this.bindingSelect);
        }
        installNames(names, error = '') {
            this.names = names;
            setText(this.namesStatus,names ? `已读取执行端目录：${names.filter(b=>b.Type==='group').length} 个配置组、${names.filter(b=>b.Type==='onedragon').length} 个一条龙。`
                : error || '未取得执行端 BGI 名称目录；未修改的绑定及其他设置仍可保存。');
            this.renderNames();
        }
        async loadNames() {
            if (!this.active() || !this.original || this.stale || this.view.pending || this.loadingNames) return;
            const serial = ++this.namesSerial; this.loadingNames = true;
            setText(this.namesStatus,'正在读取执行端目录（不会启动 BGI）…'); this.updateControls();
            try {
                const reply = await this.view.read(OP.names,catalogRequest(this.profileId));
                if (this.active() && !this.stale && serial === this.namesSerial) this.installNames(namesFrom(get(reply,'Data')));
            } catch (error) { if (this.active() && !this.stale && serial === this.namesSerial) this.installNames(null,'目录未确认：' + errorText(error)); }
            finally { if (serial === this.namesSerial) { this.loadingNames = false; this.updateControls(); } }
        }
        buildCompletion() {
            const section = this.section('完成后动作','整轮联机正常完成后才执行。切房先按停止收尾，确认进入目标房间后才执行后继动作。');
            this.actionSelect = this.select('action');
            const actions = [{value:'resume',label:'恢复任务'},{value:'stop',label:'停止'},
                {value:'runSpecified',label:'执行指定任务'},{value:'switchRoom',label:'切换助手房间'}];
            if (this.completionDraft.action === 'unknown') actions.unshift({value:'unknown',label:'原动作待核对',disabled:true});
            this.options(this.actionSelect,actions,this.completionDraft.action);
            this.actionSelect.addEventListener('change',() => {
                this.completionTouched = true;
                const wasSpecified = this.specified();
                this.completionDraft.action = this.actionSelect.value;
                if (!wasSpecified && this.specified() && !isSpecified(this.original.completion)) this.clearStart();
                this.renderCompletion(); if (this.specified()) void this.loadCatalog();
            });
            section.append(this.label('整轮联机完成后',this.actionSelect));
            this.switchPanel = el('div','ro-subform'); this.roomSelect = this.select('targetRoom');
            this.afterSelect = this.select('afterSwitch'); this.options(this.afterSelect,[{value:'standby',label:'待命'},{value:'specified',label:'执行指定任务'}],this.completionDraft.after ? 'specified' : 'standby');
            this.roomStatus = hint('');
            this.roomSelect.addEventListener('change',() => {
                this.completionTouched = true;
                const selected = this.roomSelect.value;
                this.completionDraft.room = /^\d+$/.test(selected) ? this.original.choices[Number(selected)] || null : null;
                this.renderCompletion();
            });
            this.afterSelect.addEventListener('change',() => {
                this.completionTouched = true;
                this.completionDraft.after = this.afterSelect.value === 'specified';
                if (this.completionDraft.after && !isSpecified(this.original.completion)) this.clearStart();
                this.renderCompletion(); if (this.specified()) void this.loadCatalog();
            });
            this.switchPanel.append(this.label('目标助手房间',this.roomSelect),this.roomStatus,this.label('切换成功后',this.afterSelect),
                hint('后继任务属于源房间的完成动作；切房成功不等于已经上线。')); section.append(this.switchPanel);
            this.specifiedPanel = el('div','ro-subform');
            this.typeSelect = this.select('taskType'); this.options(this.typeSelect,[{value:'',label:'请选择类型'},...Object.entries(types).map(([value,label])=>({value,label}))],this.completionDraft.type);
            this.taskSelect = this.select('taskName');
            this.selectedTaskHint = hint('');
            const row = el('div','ro-field-grid'); row.append(this.label('任务类型',this.typeSelect),this.label('执行端任务目标',this.taskSelect));
            this.startSelect = this.select('startMode'); this.options(this.startSelect,[{value:'default',label:'请选择明确起点',disabled:true},
                {value:'beginning',label:'从头开始执行'},{value:'node',label:'从某个任务开始执行'}],this.completionDraft.mode);
            this.nodeSelect = this.select('startNode'); this.nodeField = this.label('从已启用任务开始',this.nodeSelect);
            this.priorityInput = this.input('priority',this.completionDraft.priority); this.priorityInput.inputMode = 'text';
            this.priorityInput.addEventListener('input',() => { this.completionTouched = true; this.completionDraft.priority = this.priorityInput.value; });
            this.catalogStatus = live(hint('')); this.startStatus = hint('');
            this.readCatalogButton = button('重新读取所选目录',() => { void this.loadCatalog(); });
            this.refreshCatalogButton = button('刷新所选任务目录',() => { void this.explicitRefresh('catalog'); });
            this.confirmStartButton = button('确认当前起点',() => this.confirmStart());
            const buttons = el('div','ro-actions'); buttons.append(this.readCatalogButton,this.refreshCatalogButton,this.confirmStartButton);
            this.specifiedPanel.append(row,this.selectedTaskHint,this.label('执行起点',this.startSelect),this.nodeField,buttons,this.catalogStatus,this.startStatus,
                this.label('任务优先级（32 位整数）',this.priorityInput,'同一仲裁层内越大越优先；只用于指定任务。'));
            section.append(this.specifiedPanel);
            this.typeSelect.addEventListener('change',() => {
                this.completionTouched = true;
                const d = this.completionDraft; d.type = this.typeSelect.value; d.name = ''; d.workflowId = ''; this.clearStart();
                this.renderCompletion(); void this.loadCatalog();
            });
            this.taskSelect.addEventListener('change',() => {
                this.completionTouched = true;
                const d = this.completionDraft;
                if (d.type === 'workflow') {
                    const flow = this.workflows.find(w => w.id === this.taskSelect.value);
                    d.workflowId = flow?.id || ''; d.name = flow?.name || '';
                } else { d.name = this.taskSelect.value; d.workflowId = ''; }
                this.clearStart(); this.renderCompletion(); void this.loadCatalog();
            });
            this.startSelect.addEventListener('change',() => {
                this.completionTouched = true;
                const d = this.completionDraft; d.mode = this.startSelect.value; d.nodeId = d.nodeName = d.revision = '';
                if (d.mode === 'beginning') this.confirmStart(); else this.renderCompletion();
            });
            this.nodeSelect.addEventListener('change',() => {
                this.completionTouched = true;
                this.completionDraft.nodeId = this.nodeSelect.value; this.confirmStart();
            });
        }
        specified() { const d = this.completionDraft; return d.action === 'runSpecified' || d.action === 'switchRoom' && d.after; }
        clearStart() {
            const d = this.completionDraft; d.mode = 'default'; d.nodeId = d.nodeName = d.revision = '';
            this.catalog = null; ++this.catalogSerial; this.loadingCatalog = false;
        }
        targetRequest(explicit = false) {
            const d = this.completionDraft, type = has(types,d.type) ? d.type : 'group';
            // Workflow list is included in all catalog responses (even with no selected task).
            return catalogRequest(this.profileId,type,d.name,type === 'workflow' ? d.workflowId : '',explicit);
        }
        renderTaskNames() {
            if (!this.taskSelect) return;
            const d = this.completionDraft, workflow = d.type === 'workflow';
            const items = workflow ? this.workflows.map(w => ({value:w.id,label:w.name + ' · ' + w.id.slice(0,8)}))
                : (this.names || []).filter(b => b.Type === d.type).map(b => ({value:b.Name,label:b.Name}));
            const selected = workflow ? d.workflowId : d.name;
            if (selected && !items.some(i => i.value === selected)) items.unshift({value:selected,label:(d.name || selected) + '（原值，待核对）',disabled:true});
            this.options(this.taskSelect,[{value:'',label:'请选择任务'},...items],selected);
            const displayName = workflow ? this.workflows.find(w => w.id === d.workflowId)?.name || d.name : d.name;
            setText(this.selectedTaskHint,displayName ? `${typeLabel(d.type)} · ${displayName}` + (workflow && d.workflowId ? ' · ' + d.workflowId : '') : '尚未选择指定任务。');
        }
        renderCompletion() {
            if (!this.actionSelect) return;
            const d = this.completionDraft, choices = this.original.choices;
            this.switchPanel.hidden = d.action !== 'switchRoom'; this.specifiedPanel.hidden = !this.specified();
            const selected = choices.findIndex(r => sameRoute(r,d.room));
            const roomItems = [{value:'',label:'请选择目标房间'},...choices.map((r,i) => ({value:String(i),label:`${r.TargetName || '未命名房间'} · ${r.TargetRoomCode} · ${r.TargetServerUrl}`}))];
            if (d.room && selected === -1) roomItems.push({value:'invalid',label:'已失效：' + (d.room.TargetName || '原目标') + ' · ' + d.room.TargetRoomCode,disabled:true});
            this.options(this.roomSelect,roomItems,selected >= 0 ? String(selected) : d.room ? 'invalid' : '');
            this.afterSelect.value = d.after ? 'specified' : 'standby';
            const displayedRoom = selected >= 0 ? choices[selected] : d.room;
            setText(this.roomStatus,displayedRoom ? `${displayedRoom.TargetName || '名称待核对'} · ${displayedRoom.TargetRoomCode || '房号待核对'} · ${displayedRoom.TargetServerUrl || '服务器待核对'}`
                + (selected < 0 ? '（原去向失效，请明确重新选择；未修改时保留）' : '') : choices.length ? '请选择其他已保存的房间。' : '暂无有效的其他房间，请先在房间管理中添加。');
            this.renderTaskNames(); this.typeSelect.value = d.type; this.startSelect.value = d.mode === 'beginning' || d.mode === 'node' ? d.mode : 'default';
            const nodes = this.catalog?.nodes.filter(n => n.enabled) || [];
            const nodeItems = [{value:'',label:'请选择启用的任务'},...nodes.map(n => ({value:n.id,label:`${n.number}. ${n.name || n.id}`}))];
            if (d.nodeId && !nodes.some(n=>n.id===d.nodeId)) nodeItems.push({value:d.nodeId,label:(d.nodeName || d.nodeId) + '（原节点待核对）',disabled:true});
            this.options(this.nodeSelect,nodeItems,d.nodeId); this.nodeField.hidden = d.mode !== 'node';
            setText(this.startStatus,d.mode === 'default' ? '旧配置可原样保留；修改完成动作前必须明确选择起点。'
                : !this.catalog ? '尚未取得执行目标的目录修订。' : d.revision !== this.catalog.revision ? '目录修订尚未确认或已变化，请重新确认当前起点。'
                    : d.mode === 'node' && !nodes.some(n=>n.id===d.nodeId) ? '原起点节点已删除或禁用。' : '当前选择的起点与读取的目录修订一致。');
            this.updateControls();
        }
        installCatalog(raw,requested,explicit = false) {
            const validReply = raw && typeof raw === 'object' && Array.isArray(get(raw,'Workflows'));
            const error = text(raw,'Error');
            this.workflows = validReply ? readWorkflows(raw) : [];
            this.catalog = validReply && !error ? readCatalogTarget(raw,requested) : null;
            const names = validReply ? namesFrom(raw,'ConfigGroups','OneClickConfigs') : null;
            if (names) this.installNames(names);
            setText(this.catalogStatus,this.catalog
                ? `已读取目标目录，${this.catalog.nodes.filter(n=>n.enabled).length} 个启用任务。`
                : error || (!validReply ? '执行端未返回完整任务目录。'
                    : requested.Name || requested.WorkflowId ? '所选目标目录尚未确认。' : `已读取计划表列表（${this.workflows.length} 项）；请选择任务目标。`));
            if (explicit && requested.Type !== 'workflow' && !names) {
                // An explicit refresh has to return the complete names as well; do not
                // advertise the old/partial response as a successfully refreshed directory.
                this.catalog = null;
                this.installNames(null,error || '显式刷新未返回完整执行端任务名称目录。');
                setText(this.catalogStatus,error || '显式刷新结果不完整，请重新核对。');
            }
            this.renderCompletion();
        }
        async loadCatalog() {
            if (!this.active() || !this.original || this.stale || this.view.pending) return;
            const requested = this.specified() ? this.targetRequest() : catalogRequest(this.profileId,'workflow');
            const token = ++this.catalogSerial;
            this.loadingCatalog = true; this.catalog = null;
            setText(this.catalogStatus,'正在读取执行端任务目录（不会启动 BGI）…'); this.renderCompletion();
            try {
                const reply = await this.view.read(OP.catalog,requested);
                if (this.active() && !this.stale && token === this.catalogSerial) this.installCatalog(get(reply,'Data'),requested);
            } catch (error) {
                if (this.active() && !this.stale && token === this.catalogSerial) {
                    this.catalog = null; setText(this.catalogStatus,'目标目录未确认：' + errorText(error)); this.renderCompletion();
                }
            } finally { if (token === this.catalogSerial) { this.loadingCatalog = false; this.updateControls(); } }
        }
        confirmStart() {
            const d = this.completionDraft, target = this.catalog;
            if (this.stale || this.view.pending || this.preparing || !target || !target.nodes.some(n=>n.enabled)) { this.renderCompletion(); return; }
            this.completionTouched = true;
            if (d.mode === 'beginning') { d.nodeId = d.nodeName = ''; d.revision = target.revision; }
            else if (d.mode === 'node') {
                const node = target.nodes.find(n=>n.id===d.nodeId && n.enabled);
                if (!node) { d.revision = ''; this.renderCompletion(); return; }
                d.nodeName = node.name; d.revision = target.revision;
            }
            if (d.type === 'workflow' && target.workflowId === d.workflowId) d.name = target.name;
            this.renderCompletion();
        }
        async explicitRefresh(kind) {
            if (this.view.pending || this.preparing || this.loadingNames || this.loadingCatalog) return;
            try {
                this.assertUsable();
                const payload = kind === 'names' ? catalogRequest(this.profileId,'group','','',true) : this.targetRequest(true);
                if (kind === 'catalog' && payload.Type === 'workflow') { await this.loadCatalog(); return; }
                if (!confirmAction('显式刷新可能启动所选执行端的 BGI，只读取目录，不会启动配置组或一条龙任务。是否继续？')) return;
                this.assertUsable(); this.showMessage('');
                await this.view.startTracked(this,kind,payload);
            } catch (error) { if (this.active()) this.showMessage(errorText(error),true); }
        }
        completionChanges(validate) {
            const original = this.original.completion, d = this.completionDraft;
            // Reading or refreshing a directory is not an edit. In particular, old invalid or
            // hidden completion fields must not be normalized when only other settings changed.
            if (!this.completionTouched || d.action === 'unknown') return null;
            const room = d.action === 'switchRoom' ? { ...(d.room || roomSwitch({})), RunAfterSwitch:d.after } : null;
            const policy = d.action === 'resume' ? 0 : d.action === 'runSpecified' ? 2 : 1;
            let next;
            const specified = this.specified(), priority = int32(d.priority);
            if (specified) next = { Policy:policy, RoomSwitch:room, SpecifiedTaskType:d.type, SpecifiedTaskName:d.name,
                SpecifiedTaskPriority:priority, SpecifiedStartMode:d.mode, SpecifiedStartTaskId:d.nodeId, SpecifiedStartTaskName:d.nodeName,
                SpecifiedConfigRevision:d.revision, SpecifiedWorkflowId:d.workflowId };
            else {
                // Preserve hidden fields of old resume/stop or an unchanged standby relation.
                // Leaving a switch or changing to standby follows CompletionPolicyFields.Apply.
                const preserve = !room && !original.RoomSwitch || room && original.RoomSwitch && !original.RoomSwitch.RunAfterSwitch && sameRoute(room,original.RoomSwitch);
                next = { ...(preserve ? original : emptyCompletion()), Policy:policy, RoomSwitch:room };
            }
            if (sameCompletion(next,original)) return null;
            if (!validate) return next;
            if (room && !this.original.choices.some(r=>sameRoute(r,room))) throw new Error('请选择有效目标房间，原去向失效时必须重新确认。');
            if (specified) {
                if (priority === null) throw new Error('任务优先级必须是 32 位整数。');
                if (!has(types,d.type) || !nonempty(d.name) || d.type === 'workflow' && !nonempty(d.workflowId)) throw new Error('请选择执行端的指定任务目标。');
                if (d.type !== 'workflow' && d.workflowId) throw new Error('原任务还携带旧计划表身份，请重新选择任务目标。');
                if (!['beginning','node'].includes(d.mode)) throw new Error('修改指定任务前必须明确选择从头或指定起点。');
                const target = this.catalog;
                if (!target || target.type !== d.type || (d.type === 'workflow' ? target.workflowId !== d.workflowId : target.name !== d.name)
                    || !d.revision || d.revision !== target.revision || !target.nodes.some(n=>n.enabled))
                    throw new Error('指定任务目录或修订尚未确认，请重新读取并确认起点。');
                if (d.mode === 'node' && !target.nodes.some(n=>n.id===d.nodeId && n.enabled)) throw new Error('原起点节点已删除或禁用，请重新选择。');
                if (d.mode === 'beginning' && (d.nodeId || d.nodeName)) throw new Error('从头执行不能同时指定起点节点。');
            }
            return next;
        }
        changes(validate) {
            const o = this.original, changes = {}, people = this.peopleInput.value.trim(), schedule = this.scheduleInput.value.trim();
            if (!sameBindings(o.bindings,this.bindings)) changes.Bindings = this.bindings.map(b=>({...b}));
            if (people !== String(o.people)) {
                const n = int32(people);
                if (validate && (n === null || n < 1 || n > 4)) { this.peopleInput.setAttribute('aria-invalid','true'); this.peopleInput.setAttribute('aria-describedby',this.error.id); throw new Error('预期开锄人数必须为 1–4 人。'); }
                if (n !== o.people) changes.ExpectedPlayers = n;
            }
            if (this.scheduleInput.value !== o.schedule && schedule !== o.schedule || this.confirmSchedule.checked) {
                if (validate && !scheduleValid(schedule)) { this.scheduleInput.setAttribute('aria-invalid','true'); this.scheduleInput.setAttribute('aria-describedby',this.error.id); throw new Error('定时上线时间必须为 HH:mm，或留空禁用。'); }
                changes.Schedule = schedule;
            }
            const completion = this.completionChanges(validate); if (completion) changes.Completion = completion;
            const quick = Object.fromEntries([...this.quick].filter(([key,value]) => value !== (o.quick.has(key) ? o.quick.get(key) : '')));
            if (Object.keys(quick).length) changes.QuickCommands = quick;
            return changes;
        }
        validateNames(changes,names) {
            if (has(changes,'Bindings')) {
                if (!changes.Bindings.length) throw new Error('至少绑定一项配置组或一条龙。');
                const seen = new Set();
                for (const b of changes.Bindings) {
                    const key = b.Type + '\u0000' + b.Name.toUpperCase();
                    if (!['group','onedragon'].includes(b.Type) || !nonempty(b.Name) || b.Name.trim() !== b.Name || seen.has(key))
                        throw new Error('联机绑定存在空名称、无效类型、重复或首尾空格，请核对。');
                    seen.add(key);
                    if (!names?.some(n=>sameBinding(b,n))) throw new Error('联机绑定「' + bindingLabel(b) + '」不在执行端当前目录中。');
                }
                if (changes.Bindings.map(b=>b.Type).join(',').length > 1024) throw new Error('联机绑定布局超过协议上限。');
            }
            for (const [key,value] of Object.entries(changes.QuickCommands || {}))
                if (!key || typeof value !== 'string' || value && !names?.some(n=>quickValue(n)===value))
                    throw new Error(`快捷指令「${key}」目标不在执行端当前目录中。`);
        }
        async save() {
            if (!this.active() || this.preparing || this.view.pending || this.view.externalPending() || this.stale || !this.original
                || this.loadingNames || this.loadingCatalog || this.checkingRevision) return;
            this.peopleInput.removeAttribute('aria-invalid'); this.scheduleInput.removeAttribute('aria-invalid');
            this.peopleInput.removeAttribute('aria-describedby'); this.scheduleInput.removeAttribute('aria-describedby');
            this.showMessage(''); this.preparing = true; this.updateControls();
            try {
                this.assertUsable();
                const changes = this.changes(true);
                if (!Object.keys(changes).length) { this.view.closeEditor(this); this.view.notify('没有需要保存的修改。'); return; }
                const latest = readEdit(await this.view.read(OP.edit,this.profileId),this.view.target,this.profileId);
                this.assertUsable();
                if (latest.revision !== this.original.revision || !sameKey(latest.key,this.original.key)) {
                    this.invalidate('执行端该房间的相关配置已变化。'); return;
                }
                if (changes.Completion?.RoomSwitch && !latest.choices.some(r => sameRoute(r,changes.Completion.RoomSwitch))) {
                    this.invalidate('切房目标已改址、删除或失效，请重新确认目标房间。'); return;
                }
                if (has(changes,'Bindings') || Object.values(changes.QuickCommands || {}).some(v=>!!v)) {
                    this.showMessage('正在核对执行端实时任务名称目录…');
                    const reply = await this.view.read(OP.names,catalogRequest(this.profileId)); this.assertUsable();
                    const names = namesFrom(get(reply,'Data'));
                    this.installNames(names);
                    if (!names) throw new Error('修改联机绑定或快捷指令目标前，请刷新执行端 BGI 任务目录。');
                    this.validateNames(changes,names);
                } else this.validateNames(changes,null);
                if (changes.Completion && isSpecified(changes.Completion)) {
                    const c = changes.Completion;
                    const requested = catalogRequest(this.profileId,c.SpecifiedTaskType,c.SpecifiedTaskName,c.SpecifiedWorkflowId);
                    const reply = await this.view.read(OP.catalog,requested); this.assertUsable();
                    this.installCatalog(get(reply,'Data'),requested);
                    // Passive reread never replaces the revision the user explicitly confirmed.
                    this.completionChanges(true);
                }
                let conflict = null;
                if (has(changes,'Bindings')) {
                    const reply = await this.view.read(OP.layout,{ ProfileId:this.profileId, Bindings:changes.Bindings }); this.assertUsable();
                    const data = get(reply,'Data');
                    if (data !== undefined && data !== null && typeof data !== 'string') throw new Error('执行端返回的成员布局核对结果无效。');
                    conflict = str(data) || null;
                    if (conflict && !confirmAction(conflict + '\n\n若正在逐个调整各成员的绑定，可确认继续保存该执行端配置；布局未统一前仍不能开始联机批次。\n是否继续保存？')) {
                        this.showMessage('未确认布局冲突，本次未发送保存。'); return;
                    }
                }
                this.assertUsable();
                await this.view.startTracked(this,'save',{ ProfileId:this.profileId, Changes:changes, AcceptedLayoutConflict:conflict },changes);
            } catch (error) { if (this.active()) this.showMessage(errorText(error),true,true); }
            finally { this.preparing = false; this.updateControls(); }
        }
        dirty() {
            if (!this.original || !this.peopleInput) return false;
            try { return Object.keys(this.changes(false)).length > 0; } catch { return true; }
        }
        showMessage(message,isError = false,focusError = false) {
            this.error.hidden = !message; setText(this.error,message); this.error.classList.toggle('ro-warning',isError);
            if (message && focusError) focus(this.error);
        }
        updateControls() {
            if (this.closed) return;
            const p = this.view.pending, loaded = !!this.original;
            const blocked = !loaded || this.stale || !this.view.fresh() || !!p || this.view.externalPending() || this.preparing;
            this.fields.disabled = blocked;
            this.saveButton.disabled = blocked || this.loadingNames || this.loadingCatalog || this.checkingRevision;
            setText(this.saveButton,this.preparing || p?.kind === 'save' && (p.inFlight || p.readback) ? '核对保存中…' : '保存配置');
            this.closeButton.disabled = p?.kind === 'save' && (p.inFlight || p.readback);
            setText(this.closeButton,p ? '关闭（继续核对）' : '取消');
            this.pendingBox.hidden = !p;
            if (p) {
                setText(this.pendingStatus,p.message); this.pendingLookup.disabled = !this.view.ready() || p.inFlight || p.checking || p.readback;
                this.pendingCancel.hidden = p.kind === 'save'; this.pendingCancel.disabled = !this.view.ready() || p.canceling || p.cancelRequested;
            }
            if (!loaded || !this.namesReadButton) return;
            this.namesReadButton.disabled = blocked || this.loadingNames;
            this.namesRefreshButton.disabled = blocked || this.loadingNames || this.loadingCatalog;
            this.addBindingButton.disabled = blocked || !this.availableBindings().some(b=>quickValue(b)===this.bindingSelect.value);
            const selected = this.availableBindings().find(b=>quickValue(b)===this.bindingSelect.value);
            setText(this.bindingSelectionHint,selected ? bindingLabel(selected) : '');
            this.readCatalogButton.disabled = blocked || this.loadingCatalog;
            this.refreshCatalogButton.disabled = blocked || this.loadingCatalog || this.loadingNames;
            this.refreshCatalogButton.hidden = this.completionDraft.type === 'workflow';
            const d = this.completionDraft, nodes = this.catalog?.nodes || [];
            this.startSelect.disabled = blocked || !nodes.some(n=>n.enabled);
            this.nodeSelect.disabled = blocked || !nodes.some(n=>n.enabled);
            this.confirmStartButton.disabled = blocked || !nodes.some(n=>n.enabled) || !(d.mode === 'beginning' || d.mode === 'node' && nodes.some(n=>n.id===d.nodeId && n.enabled));
            if (!blocked && this.stale) this.saveButton.disabled = true;
        }
        requestClose() {
            if (!this.active() || this.closeButton.disabled) return;
            if (this.view.pending) {
                if (!confirmAction('原操作仍需核对。关闭编辑器不会确认终态、取消或重发操作。是否关闭？')) return;
            } else if (this.dirty() && !confirmAction('还有未保存的房间配置修改，是否放弃并关闭？')) return;
            this.view.closeEditor(this);
        }
        destroy() {
            this.closed = true; ++this.namesSerial; ++this.catalogSerial;
            if (this.dialog.open) this.dialog.close(); this.dialog.remove();
        }
    }

    // Caller owns one adapter per authorized executor and route, and disposes before moving it.
    // target: {uid,instanceId,machineId,name,requestPrefixes:{orchestration}} (or roomRequestPrefixes).
    // operationId(operation,requestId) may instead map to the actual durable Web receipt ID.
    // read/operate take (operation,payload,{requestId,expectedRevision,timeout,waitBudget,scope,label,onStage,allowWhilePending}).
    // read('operation.get', id) returns the actual reply, including a matching rejected terminal receipt.
    // ready() must be false on lost authorization/route/instance; adapter must not silently retarget.
    window.roomOrchestration = Object.freeze({ mount(host,adapter) {
        if (!host || !adapter?.target || typeof adapter.read !== 'function' || typeof adapter.operate !== 'function' || typeof adapter.ready !== 'function')
            throw new TypeError('任务编排需要所选执行端的已授权传输适配器。');
        mounts.get(host)?.dispose();
        const view = new OrchestrationView(host,adapter); mounts.set(host,view);
        return { refresh:() => view.refresh(), dispose:() => view.dispose() };
    } });
})();
