// 网页自己的房间簿。与执行端档案、本人授权及主房间批次完全分离。
// 接入契约：init({ getMainContext, defaultServerUrl, loadLegacyCredentials, clearLegacyCredentials,
//   onChange, onViewChange, onEvent, onDisconnect, onSwitchMain, onOrchestration, onOwnerProfiles })。
// getMainContext 适配器返回 {serverUrl,roomCode,nickname,connection,ready,players,senderUid,capabilities,generation}；
// serverUrl 必须是该主连接实际使用的基地址，players 是按实例保存的独立快照，不是主批量选择集合。
// 成员/连接变化调用 mainChanged()；仅在确认登录成功后传 mainChanged({password,remember})（本次输入）。
// getContext/getMainContext/listProfiles 不返回密码或次房连接。getLogin 仅供登录表单恢复；
// onSwitchMain 接收显式切房确认的浏览器凭据；不切换任何执行端。onEvent(key,env,context) 仅次房应答。
// onDisconnect(key,oldGeneration) 令该代等待者进入未确认。不要把次房事件送入主批次/Dodoco 等入口。
(() => {
    'use strict';
    const root = window;
    const storageKey = 'web_hie_rooms.v1';
    const MAX_ROOMS = 5, CONNECT_TIMEOUT = 15000, RETRY_DELAY = 15000;
    const roomCommands = new Set(['start_bgi', 'start_group', 'start_oneclick', 'hotkey_execute', 'stop',
        'close_game', 'clear_online', 'set_scheduled_online_time', 'bind_hoeing_group', 'set_quick_command',
        'task_policy.pull', 'task_policy.push', 'set_task_enabled', 'reset_state']);
    const policyReplies = { 'task_policy.pull': 'task_policy.data', 'task_policy.push': 'task_policy.push_result' };
    // 本人管理仍走原执行端授权；仅转发明确的房间档案/编排请求，不借房间密码扩大本人权限。
    const ownerOperations = new Set(['web.identity', 'web.authorize', 'operation.get', 'room.profiles.get',
        'room.profiles.legacyCode', 'room.profiles.save', 'room.profiles.create', 'room.profiles.remove',
        'room.switch.start', 'room.switch.status', 'room.switch.cancel', 'room.switch.recheck',
        'room.orchestration.get', 'room.orchestration.edit', 'room.orchestration.layout',
        'room.orchestration.names', 'room.orchestration.catalog', 'room.orchestration.save', 'room.orchestration.cancel']);
    const profiles = new Map(), sessions = new Map();
    // 本人档案名称仅作已认证房间的显示别名，不改变浏览器档案、凭证或房间身份。
    let ownerRoomNames = new Map();
    function setOwnerRoomNames(rows) {
        if (follow) return; // 原本人授权别名不受跟随期间迟到的查询结果影响。
        const next = new Map();
        for (const p of Array.isArray(rows) ? rows : []) {
            try { const name = trim(p.name ?? p.Name); if (name) next.set(roomKey(p.serverUrl ?? p.ServerUrl, p.roomCode ?? p.RoomCode), name); } catch { /* 无效身份不采用 */ }
        }
        if (JSON.stringify([...next]) === JSON.stringify([...ownerRoomNames])) return;
        ownerRoomNames = next; notify();
    }
    function profileDisplayName(p) {
        if (!p) return '';
        if (p.followed) return p.name || p.roomCode || '未命名房间';
        const name = trim(p.name), code = p.roomCode;
        const autoServerName = p.serverUrl && `${code} · ${new URL(p.serverUrl).host}`;
        if (!name || name === code || name === '房间 ' + code || name === '房间' + code || name === autoServerName)
            return ownerRoomNames.get(p.roomKey) || code;
        return name;
    }
    let options = {}, initialized = false, active = false, suspended = false, generation = 0;
    let main = null, view = { mode: 'current', roomKey: '' }, lastLoginKey = '', legacyMigrated = false;
    // 跟随只替换显示投影，原浏览器房间簿（包括本页未记住的登录）不被删除或写回。
    let follow = null;
    const visibleProfiles = () => follow?.profiles || profiles;
    const visibleView = () => follow?.view || view;
    let storageError = '', storageRaw = null, storageBlocked = false, viewNotice = '', switchingKey = '', notifyQueued = false;
    let selector = null, notice = null, menu = null, manager = null, switchDialog = null, placeSelector = null;

    const text = value => typeof value === 'string' ? value : '';
    const trim = value => text(value).trim();
    const id = () => root.crypto?.randomUUID?.().replaceAll('-', '') || Date.now().toString(36) + Math.random().toString(36).slice(2);
    const e = value => String(value ?? '').replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch]));

    function normalizeServer(value) {
        const input = trim(value);
        if (!/^https?:\/\//i.test(input) || /[\s\\?#]/.test(input)) throw new Error('请填写有效的 HTTP 或 HTTPS 服务器基地址（不含账号、查询参数或片段）。');
        let url;
        try { url = new URL(input); } catch { throw new Error('协调服务器地址无效。'); }
        if (!['http:', 'https:'].includes(url.protocol) || !url.hostname || url.username || url.password || url.search || url.hash)
            throw new Error('协调服务器地址无效。');
        // 与桌面 SDK 对齐 /hub 后缀，并兼容网页适配器传入 /gateway；其余路径保留。
        let path = url.pathname.replace(/\/+$/, '');
        while (/\/(hub|gateway)$/i.test(path)) path = path.replace(/\/(hub|gateway)$/i, '').replace(/\/+$/, '');
        return url.origin + path;
    }
    function normalizeCode(value) {
        const code = trim(value);
        // 网关登记的房号区分大小写；不要把已有非 6 位旧房号擅自转大写或截断。
        if (!/^[A-Za-z0-9_-]{1,128}$/.test(code)) throw new Error('请输入已有房间号（字母、数字、下划线或短横线）。');
        return code;
    }
    function roomKey(serverUrl, roomCode) { return JSON.stringify([normalizeServer(serverUrl), normalizeCode(roomCode)]); }
    function defaultServer() {
        try {
            const value = typeof options.defaultServerUrl === 'function' ? options.defaultServerUrl() : options.defaultServerUrl;
            return normalizeServer(value || (!options.defaultServerUrl && root.location?.origin) || '');
        } catch { return ''; }
    }
    function uniqueName(code, serverUrl) {
        let name = ('房间 ' + code).slice(0, 64);
        if (!nameUsed(name)) return name;
        const host = new URL(serverUrl).host;
        name = `${code} · ${host}`.slice(0, 64);
        let n = 2, candidate = name;
        while (nameUsed(candidate)) { const suffix = ` (${n++})`; candidate = name.slice(0, 64 - suffix.length) + suffix; }
        return candidate;
    }
    function nameUsed(name, exceptKey) {
        return [...profiles.values()].some(p => p.roomKey !== exceptKey && p.name.toLowerCase() === name.toLowerCase());
    }
    function makeProfile(serverUrl, roomCode, saved = false) {
        return { profileId: 'wr_' + id(), roomKey: roomKey(serverUrl, roomCode), serverUrl, roomCode,
            name: uniqueName(roomCode, serverUrl), note: '', password: '', nickname: '', remember: false, saved };
    }
    function storage() { return root.localStorage; }
    function load() {
        profiles.clear(); lastLoginKey = ''; legacyMigrated = false; view = { mode: 'current', roomKey: '' };
        storageBlocked = false; storageError = ''; storageRaw = null;
        try {
            storageRaw = storage().getItem(storageKey);
            if (!storageRaw) return;
            const data = JSON.parse(storageRaw);
            if (data?.version !== 1 || !Array.isArray(data.profiles) || data.profiles.length > MAX_ROOMS)
                throw new Error('invalid book');
            for (const item of data.profiles) {
                const serverUrl = normalizeServer(item.serverUrl), code = normalizeCode(item.roomCode), key = roomKey(serverUrl, code);
                if (profiles.has(key) || !trim(item.profileId) || [...profiles.values()].some(p => p.profileId === item.profileId))
                    throw new Error('duplicate profile');
                const name = trim(item.name), note = text(item.note);
                if (!name || name.length > 64 || note.length > 256 || nameUsed(name)) throw new Error('invalid profile');
                const remember = item.remember === true;
                profiles.set(key, { profileId: item.profileId, roomKey: key, serverUrl, roomCode: code, name, note, saved: true, remember,
                    password: remember && typeof item.password === 'string' && item.password.length <= 128 ? item.password : '',
                    nickname: remember ? trim(item.nickname).slice(0, 50) : '' });
            }
            lastLoginKey = profiles.get(data.lastLoginKey)?.remember ? data.lastLoginKey : '';
            legacyMigrated = data.legacyMigrated === true;
            const saved = data.view;
            if (['current', 'all', 'orchestration'].includes(saved?.mode)) view = { mode: saved.mode, roomKey: '' };
            else if (saved?.mode === 'room') {
                if (profiles.has(saved.roomKey)) view = { mode: 'room', roomKey: saved.roomKey };
                else viewNotice = '上次查看的房间已不存在，当前只显示控制房间。';
            }
        } catch {
            // 不覆盖无法识别的原资料簿；允许本次会话临时使用，明确提示持久化未完成。
            profiles.clear(); storageBlocked = true; storageError = '浏览器房间簿无法读取，原存储未覆盖；本次修改仅在当前页面有效。';
        }
    }
    function persist() {
        if (follow || storageBlocked) return false;
        try {
            if (storage().getItem(storageKey) !== storageRaw) {
                storageBlocked = true;
                storageError = '其他页面已修改房间簿，本页没有覆盖；请重新加载后再保存。';
                return false;
            }
            const data = { version: 1, legacyMigrated, lastLoginKey,
                view: view.mode === 'room' && !profiles.get(view.roomKey)?.saved ? { mode: 'current', roomKey: '' } : view,
                profiles: [...profiles.values()].filter(p => p.saved).map(p => ({ profileId: p.profileId, serverUrl: p.serverUrl,
                    roomCode: p.roomCode, name: p.name, note: p.note, remember: p.remember,
                    ...(p.remember ? { password: p.password, nickname: p.nickname } : {}) })) };
            const raw = JSON.stringify(data);
            storage().setItem(storageKey, raw); storageRaw = raw; storageError = '';
            return true;
        } catch {
            storageError = '浏览器未确认保存（旧记住信息可能仍存在），本次修改仅在当前页面有效。';
            return false;
        }
    }
    function forgetLegacy() {
        try { options.clearLegacyCredentials?.(); }
        catch { storageError = '旧版记住信息未确认移除，请检查浏览器存储。'; }
    }
    function migrateLegacy() {
        if (legacyMigrated || !options.loadLegacyCredentials || storageBlocked) return;
        let old;
        try { old = options.loadLegacyCredentials(); } catch { return; }
        if (!old?.roomCode || !old?.password || old.remember === false) return;
        try {
            // 普通服务页的旧登录确定属于同源；本机页旧条目无服务器时，禁止猜测当前助手默认地址。
            let legacyServer = typeof options.legacyServerUrl === 'function' ? options.legacyServerUrl() : options.legacyServerUrl;
            const fallback = defaultServer();
            if (!legacyServer && options.legacyServerUrl === undefined && fallback === root.location?.origin && !root.bgiLocalBridge && !String(root.location?.pathname || '').endsWith('local-dodoco.html'))
                legacyServer = fallback;
            if (!old.serverUrl && !legacyServer) return;
            const server = normalizeServer(old.serverUrl || legacyServer), code = normalizeCode(old.roomCode), key = roomKey(server, code);
            if (trim(old.password).length > 128 || !trim(old.password)) return;
            let p = profiles.get(key);
            if (!p) {
                if (profiles.size >= MAX_ROOMS) return;
                p = makeProfile(server, code, true);
                p.password = trim(old.password); p.nickname = trim(old.nickname).slice(0, 50); p.remember = true;
                profiles.set(key, p);
                if (!lastLoginKey) lastLoginKey = key;
            }
            legacyMigrated = true;
            // 若新房间簿中已有相同身份，保留其较新设置（特别是取消记住）；不复活旧密码。
            if (persist()) forgetLegacy();
        } catch { /* 旧记录不完整：原样保留，不向猜测的地址发送。 */ }
    }
    function loginFor(p) {
        return p?.password ? { roomKey: p.roomKey, serverUrl: p.serverUrl, roomCode: p.roomCode,
            nickname: p.nickname, password: p.password, remember: p.remember } : null;
    }
    function getLogin(key = lastLoginKey) {
        if (!key && !follow) { migrateLegacy(); key = lastLoginKey; }
        return loginFor(profiles.get(key));
    }
    function canBeginFollow() { return initialized && !follow && !switchingKey && !manager?.dialog.isConnected && !switchDialog?.isConnected; }
    function requireOrdinaryBook() {
        if (follow) throw new Error('请先关闭房间跟随，恢复原浏览器房间簿后再操作。');
    }
    function beginFollow() {
        if (!canBeginFollow()) throw new Error('原房间仍有操作正在处理，请完成后开启跟随。');
        const old = getView();
        follow = { profiles: new Map(), targetKey: '', loginAllowed: false, view: { mode: 'current', roomKey: '' }, rejected: new Set() };
        mainChanged(); options.onViewChange?.(getView(), old); notify();
    }
    function followRoomKey(p) {
        // 空白/无效旧档案、同地址同房号的重复项仍可显示，但不能用它们猜测密码归属。
        try { return p.roomCode === trim(p.roomCode) ? roomKey(p.serverUrl, p.roomCode) : ''; }
        catch { return ''; }
    }
    function setFollowSnapshot(snapshot, rejected = new Set()) {
        if (!follow) throw new Error('跟随已关闭。');
        const old = getView(), rows = snapshot?.profiles || [], counts = new Map(), keys = new Map();
        for (const p of rows) { const key = followRoomKey(p); counts.set(key, (counts.get(key) || 0) + 1); }
        const projected = new Map();
        for (const p of rows) {
            const exact = followRoomKey(p), key = exact && counts.get(exact) === 1 ? exact : JSON.stringify(['follow', p.id, 'unavailable']);
            keys.set(p.id, key);
            projected.set(key, { profileId: p.id, roomKey: key, serverUrl: p.serverUrl, roomCode: p.roomCode,
                name: p.name, note: p.note, teamUids: [...p.teamUids], expectedHoeingPlayers: p.expectedHoeingPlayers, followed: true });
        }
        const targetKey = snapshot?.confirmed ? keys.get(snapshot.activeId) || '' : '';
        const credentials = targetKey && !rejected.has(targetKey) ? loginFor(profiles.get(targetKey)) : null;
        // 普通主连接仍需原网页登录昵称，次房旁观认证不能代替这一项。
        const login = credentials?.nickname ? credentials : null;
        const viewed = keys.get(snapshot?.viewedId);
        follow.profiles = projected; follow.targetKey = targetKey; follow.loginAllowed = !!login; follow.rejected = rejected;
        follow.view = snapshot?.showOrchestration ? { mode: 'orchestration', roomKey: '' }
            : viewed && viewed !== targetKey ? { mode: 'room', roomKey: viewed }
                : viewed || !snapshot ? { mode: 'current', roomKey: '' } : { mode: 'all', roomKey: '' };
        mainChanged();
        if (old.mode !== follow.view.mode || old.roomKey !== follow.view.roomKey) options.onViewChange?.(getView(), old);
        notify();
        return { key: targetKey, login };
    }
    function endFollow() {
        if (!follow) return;
        const old = getView();
        // 切回原档案时，丢弃临时会话的代次，防止迟到的卡片或回执复用。
        for (const record of sessions.values()) retire(record);
        sessions.clear(); follow = null; main = null; mainChanged();
        options.onViewChange?.(getView(), old); notify();
    }
    function isFollowing() { return !!follow; }
    function validateMainLogin(serverUrl, roomCode, temporary = false) {
        const key = roomKey(serverUrl, roomCode);
        if (temporary) {
            // 临时登录仅使用原簿中已有的房间；必须由普通网关继续验证密码和昵称。
            if (!getLogin(key)) throw new Error('没有本浏览器的目标房间登录凭据。');
            return key;
        }
        requireOrdinaryBook();
        if (!profiles.has(key) && [...profiles.values()].filter(p => p.saved || p.roomKey !== main?.roomKey).length >= MAX_ROOMS)
            throw new Error('浏览器房间簿最多保存 5 个房间。请先移除一个非当前房间。');
        return key;
    }

    function normalizedMembers(rows) {
        if (!Array.isArray(rows)) return [];
        // 同 UID 多个实例要保留到 owner 路由；展示时才做 UID 去重。
        const members = new Map();
        for (const p of rows) {
            if (!trim(p?.playerUid) || p.playerUid.startsWith('web_') || p.playerUid.startsWith('log_viewer_')) continue;
            members.set(memberKey(p), p);
        }
        return [...members.values()];
    }
    function memberKey(p) {
        return JSON.stringify([p.playerUid, text(p.clientInstanceId), text(p.clientInstanceId) ? '' : text(p.connectionId)]);
    }
    function sameRows(a, b) { return a.length === b.length && a.every((p, i) => p === b[i]); }
    function notify() {
        if (notifyQueued || !initialized) return;
        notifyQueued = true;
        Promise.resolve().then(() => {
            notifyQueued = false;
            if (!initialized) return;
            refreshUi();
            options.onChange?.();
        });
    }
    function disconnected(key, oldGeneration) {
        try { options.onDisconnect?.(key, oldGeneration); } catch { /* UI 等待器异常不能阻止连接释放 */ }
    }
    function mainChanged(entered) {
        if (!initialized) return;
        const previous = main;
        let raw = null, key = '', serverUrl = '', code = '';
        try {
            raw = options.getMainContext?.();
            if (raw?.roomCode && raw.serverUrl) {
                serverUrl = normalizeServer(raw.serverUrl); code = normalizeCode(raw.roomCode); key = roomKey(serverUrl, code);
            }
        } catch { /* 主登录地址尚未确认 */ }
        const ready = !!(key && raw?.ready === true && raw.connection && (!follow || key === follow.targetKey && follow.loginAllowed));
        const sameTransport = !!(previous && key === previous.roomKey && raw?.connection === previous.connection && raw?.generation === previous.adapterGeneration);
        const sameGeneration = !previous && !key || sameTransport && ready === previous.ready;
        const rows = ready ? normalizedMembers(raw.players) : [];
        main = key ? { roomKey: key, serverUrl, roomCode: code, connection: raw?.connection,
            nickname: trim(raw?.nickname), ready, players: rows, senderUid: text(raw?.senderUid), capabilities: raw?.capabilities || [],
            adapterGeneration: raw?.generation, generation: sameGeneration ? previous.generation : ++generation } : null;
        if (previous?.ready && (!sameGeneration || !main)) disconnected(previous.roomKey, previous.generation);
        if (follow) {
            // 临时主连接的成功、失败和断线都不改变原档案或 lastLoginKey。
            syncSessions();
            if (!sameGeneration || !sameRows(previous?.players || [], rows)) notify();
            return;
        }
        if (previous && previous.roomKey !== key && !profiles.get(previous.roomKey)?.saved) profiles.delete(previous.roomKey);
        let profile = profiles.get(key);
        if (ready && !profile) {
            profile = makeProfile(serverUrl, code);
            profiles.set(key, profile);
        }
        if (ready && typeof entered?.remember === 'boolean' && typeof entered.password === 'string' && entered.password) {
            // 仅本次已成功的网页登录凭据，不读执行端、不从 adapter 的其他字段提取密码。
            profile.password = entered.password; profile.nickname = main.nickname; profile.remember = entered.remember;
            profile.saved = profile.saved || entered.remember;
            lastLoginKey = entered.remember ? key : '';
            legacyMigrated = true;
            if (persist() || !entered.remember) forgetLegacy();
        }
        if (ready) active = true;
        if (view.mode === 'room' && !profiles.has(view.roomKey) && view.roomKey !== key) {
            view = { mode: 'current', roomKey: '' }; viewNotice = '原查看房间已不在本浏览器房间簿；没有切换任何执行端。';
        }
        syncSessions();
        if (entered || !sameGeneration || !sameRows(previous?.players || [], rows)) notify();
    }

    function statusFor(p) {
        const credentials = follow ? profiles.get(p.roomKey) : p;
        if (follow && follow.targetKey && (follow.rejected.has(p.roomKey) || !credentials?.password || p.roomKey === follow.targetKey && !credentials.nickname))
            return { status: 'unauthenticated', ready: false, error: '需普通入房授权，请关闭跟随并在管理房间中确认登录' };
        if (follow && (!follow.loginAllowed || !follow.targetKey)) return { status: 'unconfirmed', ready: false, error: '跟随登录未确认' };
        if (main?.roomKey === p.roomKey) return { status: main.ready ? 'ready' : 'unconfirmed', ready: main.ready, error: main.ready ? '' : '控制房间连接未就绪' };
        if (switchingKey === p.roomKey) return { status: 'connecting', ready: false, error: '正在切换网页控制房间' };
        const record = sessions.get(p.roomKey);
        if (record) return { status: record.status, ready: record.status === 'ready', error: record.error || '' };
        return { status: credentials?.password ? 'idle' : 'unauthenticated', ready: false, error: credentials?.password ? '尚未连接' : '请填写本浏览器的房间密码' };
    }
    function publicProfile(p) {
        const credentials = follow ? profiles.get(p.roomKey) : p;
        return { id: p.profileId, profileId: p.profileId, roomKey: p.roomKey, serverUrl: p.serverUrl,
            roomCode: p.roomCode, name: p.name, displayName: profileDisplayName(p), note: p.note, remember: !!credentials?.remember, saved: !!p.saved, hasPassword: !!credentials?.password,
            isMain: main?.roomKey === p.roomKey && (!follow || main.ready), ...(p.followed ? { teamUids: [...p.teamUids], expectedHoeingPlayers: p.expectedHoeingPlayers } : {}), ...statusFor(p) };
    }
    function listProfiles() { return [...visibleProfiles().values()].map(publicProfile); }
    function getContext(key) {
        if (!key) return null;
        const isMain = main?.roomKey === key;
        const p = visibleProfiles().get(key) || (!follow && isMain ? { roomKey: key, serverUrl: main.serverUrl, roomCode: main.roomCode, name: '房间 ' + main.roomCode } : null);
        if (!p) return null;
        const s = statusFor(p), record = sessions.get(key), attempt = record?.attempt;
        const state = isMain ? main : attempt;
        return { roomKey: key, serverUrl: p.serverUrl, roomCode: p.roomCode, profileId: p.profileId || '',
            name: p.name, displayName: profileDisplayName(p), note: p.note || '', isMain, ...s,
            authenticated: isMain ? s.ready : !!attempt?.joined,
            players: s.ready ? [...(isMain ? main.players : attempt.rows)] : [],
            senderUid: state?.senderUid || '', capabilities: [...(state?.capabilities || [])], generation: state?.generation ?? 0 };
    }
    function getMainContext() { return getContext(main?.roomKey); }
    function sourceOf(member, context) {
        return Object.freeze({ roomKey: context.roomKey, uid: member.playerUid, instanceId: text(member.clientInstanceId),
            machineId: text(member.machineId), connectionId: text(member.connectionId), generation: context.generation });
    }
    function entriesFor(context) {
        return context?.ready ? context.players.map(member => ({ member, context, source: sourceOf(member, context) })) : [];
    }
    function orderedContexts() {
        const keys = [...visibleProfiles().keys()];
        if (main?.roomKey && (!follow || follow.profiles.has(main.roomKey))) { const pos = keys.indexOf(main.roomKey); if (pos >= 0) keys.splice(pos, 1); keys.unshift(main.roomKey); }
        return keys.map(getContext).filter(Boolean);
    }
    function getKnownInstances() { return orderedContexts().flatMap(entriesFor); }
    function dedupe(entries) {
        const winners = new Map();
        for (const candidate of entries) {
            const old = winners.get(candidate.member.playerUid);
            // 当前房优先已体现在顺序；只有在线能替换离线，其他同态按档案顺序。
            if (!old || old.member.online !== true && candidate.member.online === true) winners.set(candidate.member.playerUid, candidate);
        }
        return [...winners.values()];
    }
    function getDisplayedMembers() {
        const view = visibleView();
        if (view.mode === 'all') return dedupe(getKnownInstances());
        if (view.mode === 'room') return dedupe(entriesFor(getContext(view.roomKey)));
        if (view.mode === 'current') return dedupe(entriesFor(getMainContext()));
        return [];
    }
    function sourceFor(uid, key) {
        const entries = key ? dedupe(entriesFor(getContext(key))) : getDisplayedMembers();
        return entries.find(x => x.member.playerUid === uid)?.source || null;
    }
    function getMember(source) {
        const context = source && getContext(source.roomKey);
        if (!context?.ready || context.generation !== source.generation) return null;
        const matches = context.players.filter(p => p.playerUid === source.uid && text(p.clientInstanceId) === text(source.instanceId)
            && text(p.machineId) === text(source.machineId) && (source.connectionId === undefined || text(p.connectionId) === source.connectionId));
        return matches.length === 1 ? matches[0] : null;
    }
    function requireSource(source, { exactInstance = false, allowOffline = false, online, uniqueUid } = {}) {
        const context = source && getContext(source.roomKey), member = getMember(source);
        if (!context?.ready || !member) throw new Error('房间连接或成员实例已变化，本次未发送；请重新选择。');
        const requireOnline = online !== undefined ? online === true : !allowOffline;
        const requireUnique = uniqueUid !== undefined ? uniqueUid === true : !exactInstance;
        if (requireOnline && member.online !== true) throw new Error('该成员已离线，本次未发送。');
        if (requireUnique && context.players.filter(p => p.playerUid === source.uid && (!requireOnline || p.online === true)).length !== 1)
            throw new Error('同一房间的成员 UID 对应多个或未确认的实例，不能按 UID 单卡操作。');
        if (exactInstance && (!source.instanceId || !source.machineId)) throw new Error('执行端实例或机器身份未确认。');
        return { member, context, source: sourceOf(member, context) };
    }
    function getView() { return { ...visibleView() }; }
    function setView(value, key) {
        if (follow) return getView();
        const next = typeof value === 'string' ? { mode: value, roomKey: key || '' } : value;
        if (!['current', 'all', 'room', 'orchestration'].includes(next?.mode)) throw new Error('查看模式无效。');
        if (next.mode === 'room' && !getContext(next.roomKey)) throw new Error('该房间已不在浏览器房间簿。');
        const old = getView();
        view = { mode: next.mode, roomKey: next.mode === 'room' ? next.roomKey : '' };
        viewNotice = ''; active = true; syncSessions();
        if (old.mode !== view.mode || old.roomKey !== view.roomKey) {
            persist(); options.onViewChange?.(getView(), old); notify();
        }
        return getView();
    }
    function saveProfile(draft, existingKey = '') {
        requireOrdinaryBook();
        const previous = profiles.get(existingKey);
        if (existingKey && !previous) throw new Error('房间档案已不存在，请重新选择。');
        const serverUrl = normalizeServer(draft.serverUrl), code = normalizeCode(draft.roomCode), key = roomKey(serverUrl, code);
        const name = trim(draft.name), note = trim(draft.note), nickname = trim(draft.nickname ?? previous?.nickname);
        if (!name || name.length > 64) throw new Error('房间名称需为 1–64 个字符。');
        if (nameUsed(name, existingKey)) throw new Error('房间名称已存在，请使用不同名称。');
        if (note.length > 256 || nickname.length > 50) throw new Error('备注最多 256 个字符，登录昵称最多 50 个字符。');
        if (!previous && profiles.size >= MAX_ROOMS) throw new Error('浏览器房间簿最多保存 5 个房间。');
        if (key !== existingKey && (profiles.has(key) || main?.roomKey === key)) throw new Error('该服务器与房号已存在。');
        const locked = existingKey && (existingKey === main?.roomKey || existingKey === switchingKey);
        if (locked && (key !== existingKey || trim(draft.password))) throw new Error('不能修改当前控制房间的地址或密码；请先切换网页登录房间。');
        // 地址变化绝不沿用上一个地址的秘密；空白密码只有身份不变才表示保留本浏览器原输入。
        const password = trim(draft.password) || (key === existingKey ? previous?.password || '' : '');
        if (password.length > 128) throw new Error('房间密码最多 128 个字符。');
        const remember = draft.remember === true;
        if (remember && !password) throw new Error('如需记住登录信息，请明确填写房间密码。');
        const next = { profileId: previous?.profileId || 'wr_' + id(), roomKey: key, serverUrl, roomCode: code,
            name, note, nickname, password, remember, saved: true };
        if (key !== existingKey && previous) {
            const replaced = [...profiles].map(([k, p]) => k === existingKey ? [key, next] : [k, p]);
            profiles.clear(); replaced.forEach(([k, p]) => profiles.set(k, p));
            if (view.mode === 'room' && view.roomKey === existingKey) {
                view = { mode: 'current', roomKey: '' }; viewNotice = '原查看房间地址已变更，请重新选择要查看的房间。';
                options.onViewChange?.(getView(), { mode: 'room', roomKey: existingKey });
            }
            if (lastLoginKey === existingKey) lastLoginKey = '';
        } else profiles.set(key, next);
        if (main?.roomKey === key) lastLoginKey = remember ? key : '';
        if (lastLoginKey === key && !remember) lastLoginKey = '';
        persist(); active = true; syncSessions(); notify();
        return publicProfile(next);
    }
    function removeProfile(key) {
        requireOrdinaryBook();
        if (key === main?.roomKey || key === switchingKey) throw new Error('当前控制房间不可移除。');
        if (!profiles.delete(key)) throw new Error('房间档案已不存在。');
        if (lastLoginKey === key) lastLoginKey = '';
        if (view.mode === 'room' && view.roomKey === key) {
            const old = getView(); view = { mode: 'current', roomKey: '' };
            viewNotice = '原查看房间已移除；仅移除了本浏览器档案，没有删除服务器房间。';
            options.onViewChange?.(getView(), old);
        }
        persist(); syncSessions(); notify();
    }

    // 独立旁观连接：每次重新认证都用新的浏览器身份和连接代次，断开时丢弃未经确认的快照与等待者。
    function live(record, attempt) { return initialized && sessions.get(record.roomKey) === record && record.attempt === attempt; }
    function safeStop(connection) { if (connection) try { Promise.resolve(connection.stop()).catch(() => {}); } catch {} }
    function retire(record) {
        const a = record.attempt; record.attempt = null;
        clearTimeout(record.retryTimer); record.retryTimer = null;
        if (!a) return;
        clearTimeout(a.deadline);
        for (const cancel of [...a.waiters]) cancel();
        a.sent.clear(); a.rows = []; a.joined = false; a.valid = false;
        disconnected(record.roomKey, a.generation);
        safeStop(a.connection);
    }
    function fail(record, attempt, status, error, retry) {
        if (!live(record, attempt)) return;
        record.status = status; record.error = error;
        retire(record);
        if (retry) record.retryTimer = setTimeout(() => {
            record.retryTimer = null;
            if (initialized && sessions.get(record.roomKey) === record) startRecord(record, true);
        }, RETRY_DELAY);
        notify();
    }
    function syncSessions() {
        const currentView = visibleView();
        // 跟随只连接当前确认视图中的已知房间，所需密码仅取自本浏览器原簿。
        const candidates = follow ? (follow.loginAllowed && main?.roomKey === follow.targetKey && main.ready
            ? [...follow.profiles.keys()].filter(key => !follow.rejected.has(key) && (currentView.mode === 'all' || currentView.mode === 'room' && currentView.roomKey === key))
                .map(key => profiles.get(key)).filter(Boolean) : []) : active ? [...profiles.values()] : [];
        const desired = !suspended ? candidates.filter(p => p.roomKey !== main?.roomKey && p.roomKey !== switchingKey && p.password) : [];
        const wanted = new Map(desired.map(p => [p.roomKey, p]));
        for (const [key, record] of sessions) {
            const p = wanted.get(key);
            if (!p || p.password !== record.password) { sessions.delete(key); retire(record); }
        }
        for (const p of desired) {
            if (sessions.has(p.roomKey)) continue;
            const record = { roomKey: p.roomKey, serverUrl: p.serverUrl, roomCode: p.roomCode, password: p.password,
                status: 'connecting', error: '', retryTimer: null, attempt: null };
            sessions.set(p.roomKey, record); startRecord(record);
        }
    }
    function reconnect(key) {
        if (follow || !profiles.get(key)?.password || key === main?.roomKey || key === switchingKey) return false;
        active = true;
        const existing = sessions.get(key);
        syncSessions();
        const record = sessions.get(key);
        if (!record) return false;
        if (record === existing) { retire(record); startRecord(record, true); }
        return true;
    }
    function envelope(channel, name, payload) {
        return { protocolVersion: 3, type: channel === 'Query' ? 'query' : 'command', name,
            requestId: id(), payload, sentAtUtc: new Date().toISOString() };
    }
    function gatewayResponse(response, request) {
        if (response?.name && response.name !== request.name || response?.requestId && response.requestId !== request.requestId)
            throw new Error('网关回复身份不一致，结果未确认。');
        if (!response?.payload || typeof response.payload !== 'object') throw new Error('网关回复无效，结果未确认。');
        const error = response.payload.error;
        if (error) throw Object.assign(new Error(text(error.message) || '网关拒绝请求'), { gatewayRejected: true });
        return response;
    }
    function waitFor(attempt, promise, timeout = CONNECT_TIMEOUT) {
        return new Promise((resolve, reject) => {
            let done = false;
            const finish = (callback, value) => {
                if (done) return; done = true; clearTimeout(timer); attempt.waiters.delete(cancel); callback(value);
            };
            const cancel = () => finish(reject, new Error('房间连接已失效，结果未确认。'));
            const timer = setTimeout(() => finish(reject, new Error('房间连接或回复超时。')), timeout);
            attempt.waiters.add(cancel);
            Promise.resolve(promise).then(v => finish(resolve, v), err => finish(reject, err));
        });
    }
    async function attemptInvoke(record, a, channel, name, payload, timeout) {
        if (!live(record, a)) throw new Error('房间连接已失效。');
        const request = envelope(channel, name, payload);
        const response = await waitFor(a, a.connection.invoke(channel, request), timeout);
        if (!live(record, a)) throw new Error('房间连接已变化，结果未确认。');
        return gatewayResponse(response, request);
    }
    function startRecord(record, retry = false) {
        const a = { generation: ++generation, senderUid: 'web_logviewer_' + id(), connection: null,
            rows: [], revision: null, latestSeen: null, valid: false, joined: false, joining: false,
            capabilities: [], waiters: new Set(), sent: new Map(), resync: null, deadline: null };
        record.attempt = a; record.status = retry ? 'reconnecting' : 'connecting'; record.error = '';
        a.deadline = setTimeout(() => fail(record, a, 'failed', '入房连接或完整成员快照超时；等待重连。', true), CONNECT_TIMEOUT);
        notify();
        void (async () => {
            try {
                // 普通浏览器 CORS/混合内容规则仍生效；不代理、不发送主 cookie/本机桥授权。
                const connection = new root.signalR.HubConnectionBuilder().withUrl(record.serverUrl + '/gateway', { withCredentials: false }).build();
                a.connection = connection;
                connection.on('evt', env => receive(record, a, env));
                connection.onclose(() => fail(record, a, 'reconnecting', '连接已断开，状态待确认；等待重连。', true));
                // 不依赖 SignalR 的旧业务会话自动复用；每个新 transport 都重新 hello 和认证。
                const starting = Promise.resolve(connection.start());
                starting.then(() => { if (!live(record, a)) safeStop(connection); }, () => {});
                await waitFor(a, starting);
                if (!live(record, a)) return;
                const hello = await attemptInvoke(record, a, 'Query', 'session.hello', { clientKind: 'web', clientVersion: '1.0.0', protocolVersion: 3, capabilities: [] });
                if (hello.payload.protocolVersion !== undefined && hello.payload.protocolVersion !== 3) throw new Error('网关协议不匹配');
                a.capabilities = Array.isArray(hello.payload.capabilities) ? hello.payload.capabilities : [];
                a.joining = true;
                const response = await attemptInvoke(record, a, 'Dispatch', 'control.joinRoom', {
                    roomCode: record.roomCode, password: record.password, playerUid: a.senderUid, playerName: '网页房间旁观',
                    allowedUids: [], isRemote: true, clientInstanceId: '', requireReady: false });
                if (!live(record, a)) return;
                if (response.payload.success === false) { rejectJoin(record, a); return; }
                if (response.payload.success !== true && response.payload.ack !== true) throw new Error('入房未确认');
                a.joined = true;
                if (response.payload.playersUpdate) applySnapshot(record, a, response.payload.playersUpdate);
                if (!a.valid) await refreshSnapshot(record, a);
                if (live(record, a) && a.valid) markReady(record, a);
            } catch {
                if (live(record, a)) fail(record, a, 'failed', '未确认房间连接。请检查服务器地址、网络及跨源/HTTPS 访问权限；等待重连。', true);
            }
        })();
    }
    function rejectJoin(record, a) {
        if (follow && live(record, a)) follow.rejected.add(record.roomKey);
        fail(record, a, 'unauthenticated', '服务器拒绝加入。请核对已有房间号、密码和房间访问权限。', false);
    }
    function markReady(record, a) {
        if (!live(record, a) || !a.joined || !a.valid) return;
        clearTimeout(a.deadline); record.status = 'ready'; record.error = ''; notify();
    }
    function invalidateSnapshot(record, a) {
        const wasReady = record.status === 'ready';
        a.valid = false; a.rows = [];
        if (wasReady) {
            const old = a.generation; a.generation = ++generation; a.sent.clear();
            disconnected(record.roomKey, old);
        }
        record.status = 'unconfirmed'; record.error = '成员快照待重新核对'; notify();
        if (a.joined) void refreshSnapshot(record, a);
    }
    function applySnapshot(record, a, update) {
        if (!live(record, a)) return;
        if (!update || typeof update.full !== 'boolean') { invalidateSnapshot(record, a); return; }
        const rev = update.revision == null ? null : update.revision;
        if (rev !== null && (!Number.isSafeInteger(rev) || rev < 0)) { invalidateSnapshot(record, a); return; }
        if (rev !== null && a.revision !== null && rev <= a.revision && a.valid) return;
        if (rev !== null) a.latestSeen = Math.max(a.latestSeen ?? rev, rev);
        if (update.full) {
            if (!Array.isArray(update.players) || rev === null && a.latestSeen !== null) { invalidateSnapshot(record, a); return; }
            if (rev !== null && rev < a.latestSeen) return;
            a.rows = normalizedMembers(update.players); a.revision = rev; a.valid = true;
        } else {
            if (!a.valid || rev === null && a.revision !== null || rev !== null && a.revision === null
                || rev !== null && update.baseRevision !== a.revision
                || update.changed != null && !Array.isArray(update.changed) || update.removed != null && !Array.isArray(update.removed)) {
                invalidateSnapshot(record, a); return;
            }
            const changed = Array.isArray(update.changed) ? normalizedMembers(update.changed) : [];
            const rows = new Map(a.rows.map(p => [memberKey(p), p]));
            for (const uid of Array.isArray(update.removed) ? update.removed : []) {
                for (const [key, p] of rows) if (p.playerUid === uid) rows.delete(key);
            }
            for (const uid of new Set(changed.map(p => p.playerUid))) {
                const peers = [...rows.values()].filter(p => p.playerUid === uid), updates = changed.filter(p => p.playerUid === uid);
                // 服务端旧增量仅按 UID 给 removed；多实例拓扑变化必须重新读全量，不留下虚假候选。
                // 完整的同 UID 双实例更新仍可直接合并；避免每次心跳都使 owner 的来源代次失效。
                if (peers.length && (peers.length !== updates.length || peers.some(p => !updates.some(x => memberKey(x) === memberKey(p))))) {
                    invalidateSnapshot(record, a); return;
                }
                for (const p of updates) rows.set(memberKey(p), p);
            }
            a.rows = [...rows.values()]; a.revision = rev;
        }
        if (a.joined) markReady(record, a);
    }
    function refreshSnapshot(record, a) {
        if (a.resync) return a.resync;
        const job = (async () => {
            try {
                for (let count = 0; live(record, a) && !a.valid && count < 3; count++) {
                    const response = await attemptInvoke(record, a, 'Query', 'control.roomSnapshot', { roomCode: record.roomCode });
                    if (response.payload.success === false) { rejectJoin(record, a); return; }
                    if (response.payload.success !== true || response.payload.playersUpdate?.full !== true) throw new Error('快照未确认');
                    applySnapshot(record, a, response.payload.playersUpdate);
                }
                if (live(record, a) && !a.valid) throw new Error('增量基线未确认');
            } catch {
                if (live(record, a) && !a.valid) fail(record, a, 'failed', '未取得完整成员快照；状态未确认，等待重连。', true);
            }
        })();
        a.resync = job;
        void job.finally(() => { if (a.resync === job) a.resync = null; });
        return job;
    }
    function eventRoomMatches(code, value) { return !value || value === code || value === 'CTRL_' + code; }
    function pruneSent(a) {
        for (const [key, sent] of a.sent) if (sent.until <= Date.now()) a.sent.delete(key);
    }
    function rememberSent(a, key, sent) {
        pruneSent(a);
        if (!key || a.sent.has(key) || a.sent.size >= 256) throw new Error('操作编号重复或等待中的操作过多，未发送。');
        a.sent.set(key, { ...sent, until: Date.now() + 210000 });
    }
    function receive(record, a, env) {
        if (!live(record, a) || !env || !eventRoomMatches(record.roomCode, env.roomCode)) return;
        if (env.name === 'control.joinRejected' && a.joining) { rejectJoin(record, a); return; }
        if (env.name === 'control.playersUpdated' && a.joining) { applySnapshot(record, a, env.payload); return; }
        if (record.status !== 'ready' || !a.valid) return;
        pruneSent(a);
        const payload = env.payload, cmd = payload?.command, key = env.name === 'mistletoe.result' ? payload?.requestId : cmd?.commandId || payload?.commandId;
        const sent = a.sent.get(key);
        if (!sent) return;
        const member = getMember(sent.source);
        // 本人请求的回投由服务器绑定发起连接、目标连接和请求号；合法切房后原房成员可能已离开，
        // 仍应接收这一代已发送请求的应答。普通 UID 命令没有这个额外身份合同，须重核快照。
        if (sent.kind !== 'owner' && (!member || member.online !== true || getContext(record.roomKey).players.filter(p => p.playerUid === sent.source.uid && p.online === true).length !== 1)) return;
        if (env.name === 'control.remoteCommand') {
            if (sent.kind !== 'command' || cmd?.roomCode !== record.roomCode || cmd.senderUid !== sent.source.uid
                || !Array.isArray(cmd.target) || cmd.target.length !== 1 || cmd.target[0] !== a.senderUid
                || cmd.cmd !== 'ack' && cmd.cmd !== policyReplies[sent.cmd]) return;
        } else if (env.name === 'control.remoteCommandResult') {
            if (sent.kind !== 'command' || payload.roomCode !== record.roomCode || payload.senderUid !== a.senderUid
                || payload.targetUid !== sent.source.uid || payload.cmd !== sent.cmd) return;
        } else if (env.name === 'control.remoteCommandAck') {
            if (sent.kind !== 'command') return;
        } else if (env.name === 'mistletoe.result') {
            if (sent.kind !== 'owner' || !payload.reply || payload.reply.targetInstanceId && payload.reply.targetInstanceId !== sent.source.instanceId) return;
            a.sent.delete(key);
        } else return; // 入站执行命令、批次事件、屏幕、日志等均不执行、不转交给主上下文。
        try { options.onEvent?.(record.roomKey, env, getContext(record.roomKey)); } catch { /* UI 错误不伤及旁观连接 */ }
    }
    async function sendRemoteCommand(source, input) {
        requireOrdinaryBook();
        const { member, context } = requireSource(source);
        if (!roomCommands.has(input?.cmd) || !Array.isArray(input.target) || input.target.length !== 1 || input.target[0] !== source.uid
            || typeof input.commandId !== 'string' || !input.commandId || input.targetInstanceId && input.targetInstanceId !== source.instanceId
            || ['start_group', 'start_oneclick'].includes(input.cmd) && input.params?.key
            || input.cmd === 'stop' && input.params?.stopScope === 'all') throw new Error('只允许明确选择的单成员耕地机操作，本次未发送。');
        if (!member.clientInstanceId && !context.isMain) throw new Error('该成员未上报实例身份，请更新助手后再从其他房间操作。');
        // 房间与发起方只能由被冻结来源的当前会话确定；绝不沿用主命令信封中的登录信息。
        const command = { cmd: input.cmd, commandId: input.commandId, roomCode: context.roomCode,
            sender: context.isMain ? main.nickname : '网页房间旁观', senderUid: context.senderUid, target: [source.uid],
            ...(member.clientInstanceId ? { targetInstanceId: member.clientInstanceId } : {}),
            timestamp: input.timestamp || new Date().toISOString(), expiresAtUtc: input.expiresAtUtc || new Date(Date.now() + 120000).toISOString(),
            params: input.params || {} };
        if (context.isMain) return invokeMain(context, 'Dispatch', 'control.sendCommand', { command });
        const record = sessions.get(source.roomKey), a = record.attempt;
        rememberSent(a, command.commandId, { kind: 'command', cmd: command.cmd, source: sourceOf(member, context) });
        return attemptInvoke(record, a, 'Dispatch', 'control.sendCommand', { command });
    }
    async function invokeMain(context, channel, name, payload) {
        const chosen = main;
        if (!chosen?.ready || chosen.roomKey !== context.roomKey || chosen.generation !== context.generation) throw new Error('主房间已变化，未发送。');
        const request = envelope(channel, name, payload);
        const response = await chosen.connection.invoke(channel, request);
        if (main?.generation !== context.generation || main.roomKey !== context.roomKey || !main.ready) throw new Error('主房间已变化，结果未确认。');
        return gatewayResponse(response, request);
    }
    async function invoke(source, channel, name, payload = {}) {
        requireOrdinaryBook();
        if (channel !== 'Dispatch') throw new Error('不支持的旁观操作。');
        const owner = name === 'mistletoe.request';
        const { member, context } = requireSource(source, { exactInstance: owner, allowOffline: name === 'control.clearOnlineHistory' });
        if (owner) {
            const request = payload?.request;
            if (!request || !ownerOperations.has(request.operation) || payload.roomCode !== context.roomCode
                || payload.targetUid !== source.uid || payload.targetInstanceId !== source.instanceId
                || request.targetInstanceId !== source.instanceId || !payload.requestId || payload.requestId !== request.requestId
                || request.operation.startsWith('room.') && request.expectedMachineId !== source.machineId)
                throw new Error('本人房间操作的目标或请求身份不匹配，未发送。');
        } else if (name === 'control.clearOnlineHistory') {
            if (payload.targetUid !== source.uid) throw new Error('清除记录只能针对所选成员。');
            payload = { targetUid: source.uid };
        } else if (name === 'control.batch.reset') {
            if (!Array.isArray(payload.targetUids) || payload.targetUids.length !== 1 || payload.targetUids[0] !== source.uid || !payload.resetId)
                throw new Error('其他房间仅支持明确确认的单卡重置。');
            payload = { resetId: payload.resetId, targetUids: [source.uid], expectedGeneration: payload.expectedGeneration, expectedBatchId: payload.expectedBatchId };
        } else if (name === 'control.batch.current') {
            payload = typeof payload.batchId === 'string' ? { batchId: payload.batchId } : {};
        } else throw new Error('不支持的旁观操作；联机批次开始和其他产品仍使用当前控制房间。');
        if (!owner && !context.isMain && !member.clientInstanceId) throw new Error('该成员未上报实例身份，未执行操作。');
        if (context.isMain) return invokeMain(context, channel, name, payload);
        const record = sessions.get(source.roomKey), a = record.attempt;
        if (owner) rememberSent(a, payload.requestId, { kind: 'owner', source: sourceOf(member, context) });
        return attemptInvoke(record, a, channel, name, payload);
    }
    async function requestSwitch(key, credentials = {}) {
        requireOrdinaryBook();
        if (!options.onSwitchMain) throw new Error('网页控制房间切换入口暂不可用。');
        if (switchingKey) throw new Error('正在切换网页控制房间，请等待确认。');
        const p = profiles.get(key);
        if (!p || key === main?.roomKey && main.ready) throw new Error('请选择其他房间作为网页控制房间。');
        const password = trim(credentials.password) || p.password, nickname = trim(credentials.nickname ?? p.nickname);
        const remember = typeof credentials.remember === 'boolean' ? credentials.remember : p.remember;
        if (!password || password.length > 128 || !nickname || nickname.length > 50) throw new Error('请填写房间密码和房间内成员昵称。');
        switchingKey = key; syncSessions(); notify();
        try {
            // 仅此显式入口可获得待切换房间的本浏览器输入；认证仍由父级原网页登录流程完成。
            const result = await options.onSwitchMain({ roomKey: key, serverUrl: p.serverUrl, roomCode: p.roomCode, nickname, password, remember });
            mainChanged();
            if (main?.roomKey !== key || !main.ready) throw new Error('网页控制房间切换尚未确认；请检查登录状态。');
            return result;
        } finally { switchingKey = ''; syncSessions(); notify(); }
    }

    // -------- 仅本模块的响应式 UI（原成员卡、顶部产品导航和批量控件由现有页面负责） --------
    const statuses = { ready: '已连接', connecting: '连接中', reconnecting: '重连中', unauthenticated: '需认证',
        failed: '连接失败', unconfirmed: '状态待确认', idle: '未连接' };
    function statusText(p) { return p.isMain ? (p.ready ? '当前控制 · 已连接' : '当前控制 · 状态待确认') : statuses[p.status] || '状态待确认'; }
    function roomCaption(context) {
        if (!context) return '';
        const code = context.roomCode, name = context.displayName || profileDisplayName(context) || code;
        return name === code || name === '房间 ' + code || name === '房间' + code ? code : `${name} · ${code}`;
    }
    function roomLabel(context) { return context ? `${roomCaption(context)}（${context.serverUrl}）` : '未登录控制房间'; }
    function getViewNotice() {
        const view = visibleView();
        if (view.mode === 'orchestration') return '';
        const relevant = view.mode === 'all' ? orderedContexts() : [view.mode === 'room' ? getContext(view.roomKey) : getMainContext()].filter(Boolean);
        const missing = relevant.filter(c => !c.ready);
        return [follow ? '' : viewNotice, follow && !follow.targetKey ? '执行端的登录房间尚未确认。' : !relevant.length ? '尚未连接控制房间。' : '',
            missing.length ? missing.map(c => `${roomLabel(c)}：${c.error || statuses[c.status] || '状态待确认'}`).join('；') : '',
            follow ? '' : storageError].filter(Boolean).join(' ');
    }
    function viewLabel() {
        const view = visibleView();
        if (view.mode === 'all') return '所有房间';
        if (view.mode === 'orchestration') return '任务编排';
        const context = view.mode === 'room' ? getContext(view.roomKey) : getMainContext();
        return context?.displayName || '当前控制房间';
    }
    function refreshUi() {
        if (selector) {
            placeSelector?.();
            const button = selector.querySelector('button'), caption = selector.querySelector('.wr-trigger-label');
            const label = viewLabel(); caption.textContent = label; button.title = label;
            button.setAttribute('aria-label', `查看：${label}，打开房间菜单`);
        }
        if (notice) { const message = getViewNotice(); notice.textContent = message; notice.hidden = !message; }
        if (menu?.isConnected) renderMenu(menu);
        if (manager?.dialog.isConnected) updateManager(manager);
    }
    function mount(host = document.getElementById('gengdiNav')) {
        if (typeof host === 'string') host = document.querySelector(host);
        if (!host) return null;
        if (selector?.isConnected) return selector;
        selector = document.createElement('div'); selector.className = 'wr-selector';
        selector.innerHTML = '<button type="button" class="wr-trigger" aria-haspopup="dialog" aria-expanded="false"><span class="wr-trigger-prefix">查看</span><span class="wr-trigger-label"></span><span class="wr-chevron" aria-hidden="true"></span></button>';
        host.classList.add('wr-nav'); host.append(selector);
        // 手机把选择器放入已有房间状态行，不再额外撑高导航。
        placeSelector = () => {
            if (!selector) return;
            const room = root.matchMedia?.('(max-width:700px)').matches && document.querySelector('.topbar .room');
            const parent = room || host;
            if (selector.parentElement !== parent) parent.append(selector);
        };
        root.addEventListener?.('resize', placeSelector); placeSelector();
        selector.querySelector('button').onclick = openMenu;
        // 对不可用/非当前视图在原成员区域之外给出独立提示；正常旧单房布局不增高。
        notice = document.createElement('p'); notice.className = 'wr-view-notice'; notice.setAttribute('role', 'status'); notice.hidden = true;
        const home = document.getElementById('homeView'); if (home) home.prepend(notice);
        refreshUi(); return selector;
    }
    function makeDialog(className, label) {
        const dialog = document.createElement('dialog'); dialog.className = 'wr-dialog ' + className;
        dialog.setAttribute('aria-label', label); dialog.setAttribute('aria-modal', 'true');
        return dialog;
    }
    function showDialog(dialog, focus) {
        document.body.append(dialog); dialog.showModal(); (focus || dialog.querySelector('button, input, select'))?.focus();
    }
    function closeDialog(dialog, opener) {
        if (!dialog?.isConnected) return;
        dialog.close(); dialog.remove();
        if (opener?.isConnected && !document.querySelector('.wr-dialog[open]')) opener.focus();
    }
    function closeMenu(restore = true) {
        const button = selector?.querySelector('button'); button?.setAttribute('aria-expanded', 'false');
        closeDialog(menu, restore ? button : null); menu = null;
    }
    function renderMenu(dialog) {
        const rooms = listProfiles(), view = visibleView(), currentMain = getMainContext();
        const confirmedMain = !follow || currentMain?.ready === true;
        const mainKey = confirmedMain ? main?.roomKey : '';
        const signature = JSON.stringify([!!follow, view, mainKey, rooms.map(p => [p.roomKey, p.displayName, p.note])]);
        if (dialog.dataset.signature !== signature) {
            const focusKey = document.activeElement?.dataset?.wrAction;
            dialog.dataset.signature = signature;
            const row = (action, key, title, detail, selected) => `<button type="button" class="wr-menu-row" data-wr-action="${e(action)}" data-wr-key="${e(key)}" aria-pressed="${selected}"${follow ? ' disabled' : ''}><span><strong>${e(title)}</strong>${detail ? `<small>${e(detail)}</small>` : ''}</span>${selected ? '<span class="wr-selected-label">已选</span>' : ''}</button>`;
            dialog.innerHTML = `<div class="wr-dialog-head"><h3>助手房间</h3><button type="button" class="wr-link" data-wr-action="close">关闭</button></div>
                <div class="wr-menu-content">${row('all', '', '所有房间', `${rooms.length} 个${follow ? '共享' : '浏览器'}房间`, view.mode === 'all')}
                ${row('current', mainKey || '', '当前控制房间', confirmedMain && currentMain ? roomLabel(currentMain) : follow ? '实际登录待确认' : '尚未登录', view.mode === 'current' || view.mode === 'room' && view.roomKey === mainKey)}
                ${rooms.filter(p => !p.isMain).map(p => `<div class="wr-menu-room">${row('room', p.roomKey, roomCaption(p), p.serverUrl, view.mode === 'room' && view.roomKey === p.roomKey)}<span class="wr-room-status" data-wr-status="${e(p.roomKey)}"></span></div>`).join('')}
                ${row('orchestration', '', '任务编排', follow ? '仅共享档案元数据' : '本人授权的执行端档案', view.mode === 'orchestration')}
                ${options.followPanel ? '<section data-wr-follow-panel class="wr-follow-panel"></section>' : ''}
                <p class="wr-muted wr-menu-copy-result" role="status"></p></div>
                <div class="wr-dialog-actions wr-menu-actions"><button type="button" class="wr-action" data-wr-action="copy">复制房间号</button><button type="button" class="wr-action" data-wr-action="manage">管理房间</button><button type="button" class="wr-action" data-wr-action="switch">切换控制房间</button></div>`;
            dialog.querySelectorAll('[data-wr-action]').forEach(b => b.onclick = () => menuAction(b.dataset.wrAction, b.dataset.wrKey));
            if (focusKey) [...dialog.querySelectorAll('[data-wr-action]')].find(b => b.dataset.wrAction === focusKey)?.focus();
        }
        for (const el of dialog.querySelectorAll('[data-wr-status]')) setStatus(el, rooms.find(p => p.roomKey === el.dataset.wrStatus));
        dialog.querySelector('[data-wr-action="copy"]').disabled = !(view.mode === 'room' ? getContext(view.roomKey) : confirmedMain && currentMain);
        dialog.querySelector('[data-wr-action="manage"]').disabled = !!follow;
        dialog.querySelector('[data-wr-action="switch"]').disabled = !!follow || !options.onSwitchMain || !rooms.some(p => !p.isMain);
        dialog.querySelector('[data-wr-action="orchestration"]').disabled = !!follow || !options.onOrchestration;
        options.followPanel?.(dialog.querySelector('[data-wr-follow-panel]'));
    }
    function setStatus(el, p) {
        if (!el || !p) return;
        const label = statusText(p); if (el.textContent !== label) el.textContent = label;
        el.dataset.state = p.status; el.title = p.error || label;
    }
    function openMenu() {
        if (menu?.isConnected) { closeMenu(); return; }
        const button = selector?.querySelector('button'); if (!button) return;
        menu = makeDialog('wr-menu', '查看房间');
        renderMenu(menu);
        const rect = button.getBoundingClientRect(), menuWidth = Math.min(460, root.innerWidth - 24);
        menu.style.setProperty('--wr-menu-top', Math.max(8, Math.min(rect.bottom + 7, root.innerHeight - 260)) + 'px');
        menu.style.setProperty('--wr-menu-right', Math.max(12, Math.min(root.innerWidth - rect.right, root.innerWidth - menuWidth - 12)) + 'px');
        menu.addEventListener('cancel', event => { event.preventDefault(); closeMenu(); });
        menu.addEventListener('click', event => { if (event.target === menu && outside(menu, event)) closeMenu(); });
        button.setAttribute('aria-expanded', 'true'); showDialog(menu);
    }
    function outside(dialog, event) {
        const r = dialog.getBoundingClientRect();
        return event.clientX < r.left || event.clientX > r.right || event.clientY < r.top || event.clientY > r.bottom;
    }
    async function menuAction(action, key) {
        const view = visibleView();
        if (action === 'close') { closeMenu(); return; }
        if (action === 'copy') {
            const p = view.mode === 'room' ? getContext(view.roomKey) : getMainContext(); if (!p) return;
            try { await root.navigator.clipboard.writeText(p.roomCode); if (menu) menu.querySelector('.wr-menu-copy-result').textContent = '已复制房间号 ' + p.roomCode; }
            catch { root.prompt('复制房间号', p.roomCode); }
            return;
        }
        if (follow) return;
        if (action === 'manage' || action === 'switch') { closeMenu(false); action === 'manage' ? openManager() : openSwitch(key); return; }
        closeMenu();
        setView(action, key);
        if (action === 'orchestration') options.onOrchestration?.();
    }
    function openManager(initialKey = main?.roomKey || '') {
        if (follow) { if (!menu?.isConnected) openMenu(); return; }
        if (manager?.dialog.isConnected) return;
        active = true; syncSessions();
        const opener = selector?.querySelector('button') || document.activeElement;
        const dialog = makeDialog('wr-manager', '管理房间');
        dialog.innerHTML = '<div class="wr-dialog-head"><div><h3>管理房间</h3><p class="wr-muted">浏览器房间簿 · 仅在本浏览器保存已有房间</p></div><button type="button" class="wr-link" data-wr-close>关闭</button></div><div class="wr-manager-tabs"><span class="wr-current-tab">浏览器房间</span><button type="button" class="wr-link" data-wr-owner>本人执行端档案 ›</button></div><div class="wr-manager-body"><div class="wr-book-list"><div class="wr-list-header"><strong data-wr-count></strong><button type="button" class="wr-action" data-wr-add>添加房间</button></div><div data-wr-list></div></div><div class="wr-book-editor" data-wr-editor></div></div><p class="wr-book-result" role="status" data-wr-result></p>';
        const state = { dialog, opener, selected: profiles.has(initialKey) ? initialKey : [...profiles.keys()][0] || '', dirty: false, editing: false, form: null };
        manager = state;
        const leave = () => { if (!state.dirty || root.confirm('放弃尚未保存的房间资料？')) { closeDialog(dialog, opener); manager = null; return true; } return false; };
        state.leave = leave;
        const select = key => {
            if (state.dirty && !root.confirm('放弃尚未保存的房间资料？')) return;
            state.selected = key; state.dirty = false; state.editing = true; drawManagerEditor(state); updateManager(state); state.form?.querySelector('[name="name"]')?.focus();
        };
        state.select = select;
        dialog.querySelector('[data-wr-close]').onclick = leave;
        dialog.querySelector('[data-wr-add]').onclick = () => select('');
        dialog.querySelector('[data-wr-owner]').disabled = !options.onOwnerProfiles;
        dialog.querySelector('[data-wr-owner]').onclick = () => { if (leave()) options.onOwnerProfiles?.(); };
        dialog.addEventListener('cancel', event => { event.preventDefault(); leave(); });
        drawManagerEditor(state); updateManager(state); showDialog(dialog);
    }
    function managerMessage(state, message, error = false) {
        const el = state.dialog.querySelector('[data-wr-result]'); el.textContent = message; el.classList.toggle('wr-error', error);
    }
    function updateManager(state) {
        const rooms = listProfiles(), dialog = state.dialog, list = dialog.querySelector('[data-wr-list]');
        dialog.querySelector('[data-wr-count]').textContent = `${rooms.length} / ${MAX_ROOMS} 个房间`;
        dialog.querySelector('[data-wr-add]').disabled = rooms.length >= MAX_ROOMS;
        const sig = JSON.stringify([state.selected, rooms.map(p => [p.roomKey, p.name, p.note, p.isMain, p.saved])]);
        if (state.listSignature !== sig) {
            state.listSignature = sig;
            list.innerHTML = rooms.map(p => `<button type="button" class="wr-book-row" data-wr-key="${e(p.roomKey)}" aria-pressed="${state.selected === p.roomKey}"><strong>${e(p.name)}</strong><span>${e(p.roomCode)} · ${e(p.serverUrl)}</span>${p.note ? `<small>${e(p.note)}</small>` : ''}<span class="wr-room-status" data-wr-status="${e(p.roomKey)}"></span></button>`).join('') || '<p class="wr-muted">暂无房间，添加一个已有房间开始。</p>';
            list.querySelectorAll('[data-wr-key]').forEach(b => b.onclick = () => state.select(b.dataset.wrKey));
        }
        for (const el of list.querySelectorAll('[data-wr-status]')) setStatus(el, rooms.find(p => p.roomKey === el.dataset.wrStatus));
        dialog.classList.toggle('wr-editing', state.editing);
        if (state.form) {
            const p = profiles.get(state.selected), locked = state.selected && (state.selected === main?.roomKey || state.selected === switchingKey);
            for (const field of ['serverUrl', 'roomCode']) state.form.elements[field].readOnly = !!locked;
            state.form.elements.password.disabled = !!locked;
            const remove = state.form.querySelector('[data-wr-remove]'), retry = state.form.querySelector('[data-wr-retry]');
            if (remove) remove.disabled = !!locked;
            if (retry) retry.disabled = !!locked || !p?.password;
            const currentStatus = state.form.querySelector('[data-wr-current-status]');
            if (p && currentStatus) { const snapshot = getContext(p.roomKey); currentStatus.textContent = `${statusText(publicProfile(p))}${snapshot?.ready ? ` · ${snapshot.players.filter(x => x.online).length} 个在线实例` : ` · ${snapshot?.error || '未确认'}`}`; }
        }
    }
    function drawManagerEditor(state) {
        const p = profiles.get(state.selected), host = state.dialog.querySelector('[data-wr-editor]');
        host.innerHTML = `<form class="wr-profile-form" autocomplete="off"><div class="wr-editor-head"><button type="button" class="wr-link wr-back" data-wr-back>‹ 房间列表</button><h3>${p ? '编辑浏览器房间' : '添加已有房间'}</h3></div>
            <div class="wr-fields"><label>房间名称<input type="text" name="name" required maxlength="64" value="${e(p?.name)}" placeholder="例如：好友房间"></label>
            <label>备注<textarea name="note" maxlength="256" rows="2" placeholder="仅本浏览器可见">${e(p?.note)}</textarea></label>
            <label>协调服务器地址<input type="url" name="serverUrl" required value="${e(p?.serverUrl || defaultServer())}" placeholder="https://服务器地址"></label>
            <label>房间号<input type="text" name="roomCode" required maxlength="128" value="${e(p?.roomCode)}" placeholder="已有房间号" autocapitalize="off" spellcheck="false"></label>
            <label>房间密码<input type="password" name="password" maxlength="128" autocomplete="new-password" placeholder="${p?.password ? '留空保留本浏览器原密码' : '填写密码后才能连接'}"></label>
            <label>网页登录昵称<input type="text" name="nickname" maxlength="50" value="${e(p?.nickname)}" placeholder="手动切为控制房间时所需的成员昵称"></label>
            <label class="wr-check"><input type="checkbox" name="remember" ${p?.remember ? 'checked' : ''}>在此浏览器记住登录信息</label>
            <p class="wr-muted">未勾选时只保存档案信息，密码仅本页有效。修改服务器或房号后必须重新输入对应密码。不会读取执行端已保存的密码，也不会在服务器建房。</p>
            <p class="wr-muted">当前控制房间的地址和密码须在切出后修改；跨服务器连接由目标服务和浏览器的访问规则决定。</p>
            <p class="wr-muted" data-wr-current-status></p></div>
            <div class="wr-editor-actions">${p ? '<button type="button" class="wr-action wr-danger" data-wr-remove>移除档案</button><button type="button" class="wr-action" data-wr-retry>重新连接</button>' : ''}<button type="button" class="wr-action" data-wr-cancel>取消修改</button><button class="wr-action wr-primary" type="submit">${p ? '保存档案' : '保存已有房间'}</button></div></form>`;
        state.form = host.querySelector('form'); const form = state.form;
        form.oninput = () => { state.dirty = true; };
        const discard = () => {
            if (state.dirty && !root.confirm('放弃尚未保存的修改？')) return;
            state.dirty = false; state.editing = false; drawManagerEditor(state); updateManager(state);
        };
        form.querySelector('[data-wr-back]').onclick = discard; form.querySelector('[data-wr-cancel]').onclick = discard;
        form.onsubmit = ev => {
            ev.preventDefault();
            try {
                const draft = Object.fromEntries(new FormData(form)); draft.remember = form.elements.remember.checked;
                const saved = saveProfile(draft, state.selected); state.selected = saved.roomKey; state.dirty = false;
                state.editing = false; drawManagerEditor(state); updateManager(state);
                managerMessage(state, storageError || `已保存「${saved.name}」。${saved.hasPassword ? '连接状态以认证结果为准。' : '未填写密码，暂不能查看。'}`, !!storageError);
            } catch (error) { managerMessage(state, error.message, true); }
        };
        const remove = form.querySelector('[data-wr-remove]');
        if (remove) remove.onclick = () => {
            if (!root.confirm(`仅从本浏览器移除「${p.name}」？不会删除服务器房间、执行端档案和历史记录。`)) return;
            try { removeProfile(state.selected); state.selected = [...profiles.keys()][0] || ''; state.dirty = false; state.editing = false; drawManagerEditor(state); updateManager(state); managerMessage(state, storageError || '已移除本浏览器档案。', !!storageError); }
            catch (err) { managerMessage(state, err.message, true); }
        };
        const retry = form.querySelector('[data-wr-retry]');
        if (retry) retry.onclick = () => { if (state.dirty) { managerMessage(state, '请先保存或取消修改。', true); return; } if (reconnect(state.selected)) managerMessage(state, '正在重新认证，未重发任何命令。'); };
    }
    function openSwitch(key = '') {
        if (follow || switchDialog?.isConnected || !options.onSwitchMain) return;
        const rooms = [...profiles.values()].filter(p => p.roomKey !== main?.roomKey), opener = selector?.querySelector('button') || document.activeElement;
        if (!rooms.length) return;
        const dialog = makeDialog('wr-switch', '切换网页控制房间'); switchDialog = dialog;
        let selected = rooms.find(p => p.roomKey === key) || rooms.find(p => view.mode === 'room' && p.roomKey === view.roomKey) || rooms[0], busy = false;
        dialog.innerHTML = `<div class="wr-dialog-head"><h3>切换网页控制房间</h3><button type="button" class="wr-link" data-wr-close>取消</button></div><form class="wr-switch-form" autocomplete="off"><div class="wr-fields"><label>目标房间<select name="target">${rooms.map(p => `<option value="${e(p.roomKey)}">${e(roomLabel(p))}</option>`).join('')}</select></label><p data-wr-switch-target></p><label>房间内成员昵称<input type="text" name="nickname" required maxlength="50"></label><label>房间密码<input type="password" name="password" maxlength="128" autocomplete="new-password"></label><label class="wr-check"><input type="checkbox" name="remember">在此浏览器记住登录信息</label><p class="wr-muted">仅切换本网页的控制房间，不会切换或上线任何执行端。原连接的待确认操作不会自动重发。跨服务器登录后本机桥仍绑定原助手。</p><p role="status" data-wr-switch-status></p></div><div class="wr-dialog-actions"><button type="submit" class="wr-action wr-primary">确认切换网页登录</button></div></form>`;
        const form = dialog.querySelector('form'), status = dialog.querySelector('[data-wr-switch-status]');
        const fill = () => {
            form.elements.target.value = selected.roomKey; form.elements.nickname.value = selected.nickname; form.elements.password.value = '';
            form.elements.password.placeholder = selected.password ? '留空使用本浏览器原密码' : '输入房间密码'; form.elements.password.required = !selected.password;
            form.elements.remember.checked = selected.remember; dialog.querySelector('[data-wr-switch-target]').textContent = roomLabel(selected);
        };
        const close = () => { if (!busy) { closeDialog(dialog, opener); switchDialog = null; } };
        dialog.querySelector('[data-wr-close]').onclick = close;
        dialog.addEventListener('cancel', ev => { ev.preventDefault(); close(); });
        form.elements.target.onchange = () => { selected = profiles.get(form.elements.target.value); if (selected) fill(); };
        form.onsubmit = async ev => {
            ev.preventDefault(); if (busy || !selected) return; busy = true;
            const credentials = { nickname: form.elements.nickname.value, password: form.elements.password.value, remember: form.elements.remember.checked };
            [...form.elements].forEach(el => el.disabled = true); dialog.querySelector('[data-wr-close]').disabled = true; status.textContent = '正在切换，等待目标房间认证…';
            try { await requestSwitch(selected.roomKey, credentials); busy = false; close(); }
            catch (error) { status.textContent = '切换未完成或未确认：' + error.message; }
            finally { busy = false; if (dialog.isConnected) { [...form.elements].forEach(el => el.disabled = false); dialog.querySelector('[data-wr-close]').disabled = false; } }
        };
        fill(); showDialog(dialog);
    }
    function setActive(value) { if (!follow) active = !!value; syncSessions(); notify(); }
    function pagehide() { suspended = true; syncSessions(); }
    function pageshow() { if (suspended) { suspended = false; mainChanged(); syncSessions(); notify(); } }
    function init(adapter = {}) {
        if (initialized) return root.webRooms;
        options = adapter; initialized = true; active = false; suspended = false; main = null; viewNotice = ''; switchingKey = '';
        load(); migrateLegacy(); mainChanged(); notify();
        root.addEventListener?.('pagehide', pagehide); root.addEventListener?.('pageshow', pageshow);
        return root.webRooms;
    }
    function dispose() {
        if (!initialized) return;
        initialized = false; active = false; root.removeEventListener?.('pagehide', pagehide); root.removeEventListener?.('pageshow', pageshow);
        for (const record of sessions.values()) retire(record);
        sessions.clear(); closeMenu(false); closeDialog(manager?.dialog); closeDialog(switchDialog); manager = null; switchDialog = null;
        root.removeEventListener?.('resize', placeSelector); placeSelector = null;
        if (selector) document.querySelector('#gengdiNav')?.classList.remove('wr-nav');
        selector?.remove(); notice?.remove(); selector = null; notice = null;
        profiles.clear(); ownerRoomNames.clear(); main = null; follow = null; options = {}; switchingKey = '';
    }
    root.webRooms = { init, dispose, mount, normalizeServer, roomKey, validateMainLogin, getLogin,
        mainChanged, getMainContext, getContext, listProfiles, getView, setView, getViewNotice, getDisplayedMembers,
        getKnownInstances, sourceFor, getMember, requireSource, sendRemoteCommand, invoke, setActive, reconnect,
        saveProfile, removeProfile, requestSwitch, openManager, openSwitch, setOwnerRoomNames,
        canBeginFollow, beginFollow, setFollowSnapshot, endFollow, isFollowing, refreshUi };
})();
