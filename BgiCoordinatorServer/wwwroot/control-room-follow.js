// 房间跟随：独立订阅元数据；网页自己的房间凭据只交给原 /gateway 登录流程。
(() => {
    'use strict';
    const root = window, prefix = 'BGIFOLLOW1.', limit = 64 * 1024, invitationLimit = 24 * 1024;
    const markerKey = 'bgi.room.follow.interrupted.v1';
    const invalidInvitation = () => new Error('房间跟随邀请格式无效。');
    const hex = s => typeof s === 'string' && /^[0-9a-f]{64}$/.test(s);
    const validText = (s, max, multiline = false) => typeof s === 'string' && s.length <= max
        && !(multiline ? /[\x00-\x08\x0b\x0c\x0e-\x1f\x7f-\x9f]/ : /[\x00-\x1f\x7f-\x9f]/).test(s)
        && !/[\uD800-\uDFFF]/u.test(s);
    const identity = s => validText(s, 512) && !!s.trim() && s === s.trim();
    const fields = (o, expected) => o !== null && typeof o === 'object' && !Array.isArray(o)
        && Object.keys(o).length === expected.length && expected.every(k => Object.hasOwn(o, k));
    // JSON.parse 本身允许重复属性。拒绝它们并对齐桌面/中继的最大深度，避免不同端采用不同身份。
    function strictJson(text) {
        const value = JSON.parse(text), stack = [];
        for (const token of text.matchAll(/"(?:\\.|[^"\\])*"|[{}\[\]:,]/gs)) {
            const part = token[0];
            if (part === '{' || part === '[') {
                stack.push(part === '{' ? new Set() : null);
                if (stack.length > 8) throw new Error('invalid depth');
            } else if (part === '}' || part === ']') stack.pop();
            else if (part.startsWith('"') && /^\s*:/.test(text.slice(token.index + part.length))) {
                const key = JSON.parse(part), keys = stack.at(-1);
                if (!keys || keys.has(key)) throw new Error('duplicate field');
                keys.add(key);
            }
        }
        return value;
    }
    function serverAddress(value, httpsOnly = false) {
        if (!validText(value, 2048) || !/^https?:\/\/[^/]+/i.test(value) || /[\s\\?#]/.test(value)
            || value.slice(value.indexOf('://') + 3).split('/')[0].includes('@')) throw new Error('invalid address');
        const url = new URL(value);
        if (!['http:', 'https:'].includes(url.protocol) || httpsOnly && url.protocol !== 'https:'
            || !url.hostname || url.username || url.password || url.search || url.hash) throw new Error('invalid address');
        return url.origin + url.pathname.replace(/\/+$/, '');
    }
    async function parseInvitation(raw) {
        if (!validText(raw, invitationLimit, true)) throw invalidInvitation();
        const input = raw.trim(), encoded = input.slice(prefix.length);
        if (!input.startsWith(prefix) || !encoded || !/^[A-Za-z0-9_-]+$/.test(encoded) || encoded.length % 4 === 1) throw invalidInvitation();
        let value;
        try {
            const binary = atob(encoded.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(encoded.length / 4) * 4, '='));
            if (btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '') !== encoded) throw invalidInvitation();
            const bytes = Uint8Array.from(binary, c => c.charCodeAt(0));
            try { value = strictJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes)); }
            finally { bytes.fill(0); }
            if (!fields(value, ['version','serverUrl','local','uid','instanceId','machineId','channelId','readSecret','writeHash'])
                || value.version !== 1 || typeof value.local !== 'boolean' || !identity(value.uid) || !identity(value.instanceId)
                || !identity(value.machineId) || !hex(value.channelId) || !hex(value.readSecret) || !hex(value.writeHash)
                || (value.local ? value.serverUrl !== '' : !/^https:\/\//.test(value.serverUrl) || value.serverUrl.endsWith('/')
                    || !serverAddress(value.serverUrl, true) || /\/(hub|gateway|room-follow)$/i.test(value.serverUrl))) throw invalidInvitation();
        } catch { throw invalidInvitation(); }
        if (value.local) throw new Error('网页仅支持执行端生成的 HTTPS 远程跟随邀请，本机管道邀请请在桌面监控端使用。');
        // 能力只经过 TLS 中继的绑定方法；非安全网页本身不能保密或校验传入的邀请。
        if (!root.crypto?.subtle) throw new Error('请在 HTTPS 网页或助手本机网页中使用远程跟随邀请。');
        try {
            const hash = async s => Array.from(new Uint8Array(await root.crypto.subtle.digest('SHA-256', new TextEncoder().encode(s))), b => b.toString(16).padStart(2, '0')).join('');
            const readHash = await hash(value.readSecret);
            if (readHash === value.writeHash || await hash(value.writeHash + ':' + readHash) !== value.channelId) throw invalidInvitation();
        } catch { throw invalidInvitation(); }
        return Object.freeze(value);
    }
    function decodeSnapshot(state, invitation) {
        if (state === null) return null;
        try {
            if (typeof state !== 'string' || state.length > limit || new TextEncoder().encode(state).length > limit) throw new Error();
            const value = strictJson(state);
            if (!fields(value, ['version','uid','instanceId','machineId','confirmed','profiles','activeId','viewedId','showOrchestration'])
                || value.version !== 1 || value.uid !== invitation.uid || value.instanceId !== invitation.instanceId || value.machineId !== invitation.machineId
                || typeof value.confirmed !== 'boolean' || typeof value.showOrchestration !== 'boolean'
                || !Array.isArray(value.profiles) || value.profiles.length > 5 || !validText(value.activeId, 512) || !validText(value.viewedId, 512)) throw new Error();
            const ids = new Set();
            for (const p of value.profiles) {
                if (!fields(p, ['id','name','note','serverUrl','roomCode','teamUids','expectedHoeingPlayers']) || !identity(p.id)
                    || p.id === '__all_rooms__' || ids.has(p.id) || !validText(p.name, 256) || !validText(p.note, 8192, true)
                    || !validText(p.roomCode, 256) || !Array.isArray(p.teamUids) || p.teamUids.length > 64 || !p.teamUids.every(identity)
                    || !Number.isInteger(p.expectedHoeingPlayers) || p.expectedHoeingPlayers < 1 || p.expectedHoeingPlayers > 4) throw new Error();
                if (p.serverUrl !== '') serverAddress(p.serverUrl);
                ids.add(p.id);
            }
            if (value.activeId && !ids.has(value.activeId) || value.confirmed && !value.activeId
                || value.viewedId && value.viewedId !== '__all_rooms__' && !ids.has(value.viewedId)) throw new Error();
            return value;
        } catch { throw new Error('共享房间元数据格式或执行端身份不匹配，登录未确认。'); }
    }
    function within(promise, timeout = 15000) {
        let timer;
        return Promise.race([Promise.resolve(promise), new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('timeout')), timeout); })])
            .finally(() => clearTimeout(timer));
    }
    async function stopTransport(transport) {
        if (!transport) return true;
        try { await within(transport.connection.stop()); return true; }
        catch { return false; } // 已从当前订阅代次剥离；不回显网络异常或能力。
    }
    function createController(adapter) {
        let session = null, invitation = null, draft = '', validating = false, restoring = false, pageActive = true;
        let idleStatus = root.crypto?.subtle ? '房间跟随未开启，浏览器房间簿与执行端保持独立。' : '跟随邀请需要 HTTPS 网页或助手本机网页，请先通过安全入口打开。', attention = false;
        try {
            const marker = root.sessionStorage.getItem(markerKey);
            if (marker === 'unsaved' || marker === 'active') {
                idleStatus = marker === 'unsaved'
                    ? '页面已重新加载，跟随已关闭。上次未记住的原登录只保留在原页面，请按普通流程重新输入凭据。'
                    : '页面已重新加载，跟随默认关闭。仅保留原已记住的登录信息，请按普通流程登录。';
                adapter.reloadNotice?.(idleStatus);
            }
            root.sessionStorage.removeItem(markerKey);
        } catch { /* 不支持存储时也不持久化任何秘密。 */ }
        const isLive = s => session === s && pageActive;
        const roomLabel = p => p ? `${p.name || p.roomCode || '未命名房间'}（${p.roomCode || '房号未确认'} · ${p.serverUrl || '服务器未确认'}）` : '目标房间';
        const identityLabel = () => invitation ? `UID ${invitation.uid} · 实例 ${invitation.instanceId} · 机器 ${invitation.machineId} · 中继 ${invitation.serverUrl}` : '尚未选择执行端邀请';
        function state() {
            let message = idleStatus;
            if (validating) message = '正在校验跟随邀请…';
            else if (restoring) message = '已停止跟随，正在恢复本浏览器原登录和页面…';
            else if (session) {
                const s = session, target = s.snapshot?.profiles.find(p => p.id === s.snapshot.activeId);
                const actual = adapter.mainContext();
                if (s.transportStatus !== 'connected') message = s.transportStatus;
                else if (s.metadataError) message = s.metadataError;
                else if (!s.snapshot) message = '等待执行端共享状态（可能离线或已撤回授权），登录未确认。';
                else if (!s.snapshot.confirmed) message = '已收到房间档案，执行端登录尚未确认，等待后续状态。';
                else if (!s.desiredKey) message = `登录待授权：${roomLabel(target)}。请关闭跟随，在普通登录或房间管理中核对本浏览器凭据并确认入房，再重新开启。`;
                else if (s.mainStatus === 'failed' || s.mainStatus === 'closed') message = `目标房间登录未确认：${roomLabel(target)}。请检查网络或原网页登录凭据，关闭后可重试。`;
                else message = actual?.ready && actual.roomKey === s.desiredKey
                    ? `跟随中：已使用本浏览器身份登录 ${roomLabel(target)}。`
                    : `正在等待普通房间登录确认：${roomLabel(target)}。`;
                if (!s.snapshot && s.lastSnapshot) message += ' 档案为上次收到的元数据，成员及登录尚未确认。';
                if (s.snapshot?.confirmed && s.desiredKey) {
                    const view = adapter.view(), needed = adapter.profiles().filter(p => (p.roomKey === s.desiredKey || view.mode === 'all'
                        || view.mode === 'room' && p.roomKey === view.roomKey) && p.status === 'unauthenticated');
                    if (needed.length) message += ' 其他待授权房间：' + needed.map(roomLabel).join('、') + '。请关闭跟随后普通授权。';
                }
            }
            return { enabled: !!session, busy: validating || restoring, attention, identity: identityLabel(), message };
        }
        const refresh = () => adapter.changed?.(state());
        function retireRelay(s) {
            const transport = s.transport; s.transport = null;
            if (transport) s.retiredTransport = transport;
            if (!s.relayClosing && s.retiredTransport) {
                const retired = s.retiredTransport;
                const job = stopTransport(retired).then(closed => {
                    if (closed && s.retiredTransport === retired) s.retiredTransport = null;
                    return closed;
                }).finally(() => { if (s.relayClosing === job) s.relayClosing = null; });
                s.relayClosing = job;
            }
            return s.relayClosing || Promise.resolve(true);
        }
        function apply(s, snapshot, metadataError = '') {
            if (!isLive(s)) return;
            s.snapshot = snapshot; s.metadataError = metadataError;
            if (snapshot) s.lastSnapshot = snapshot;
            // Keep the last metadata for display only when the executor/relay goes offline.
            // No confirmed active room or credential-based login may survive this projection.
            const projection = snapshot || (s.lastSnapshot ? { ...s.lastSnapshot, confirmed: false } : null);
            const { key, login } = adapter.project(projection, s.rejected);
            const desired = login ? key : '';
            if (s.desiredKey === desired) { refresh(); return; } // 只变名称/备注/查看页不重登。
            s.desiredKey = desired; s.mainStatus = desired ? 'connecting' : '';
            const serial = ++s.loginSerial, closing = adapter.retireMain();
            const current = () => isLive(s) && s.loginSerial === serial && s.desiredKey === desired;
            refresh();
            if (!desired) return;
            void (async () => {
                try {
                    if (!await closing) throw new Error('previous connection not closed');
                    if (!current()) return;
                    await within(adapter.connect(login, current, status => {
                        if (!current()) return;
                        s.mainStatus = status;
                        if (status === 'rejected') {
                            s.rejected.add(desired);
                            apply(s, s.snapshot, s.metadataError); // 失败凭据在本轮跟随内不自动重试。
                        } else {
                            if (status === 'failed' || status === 'closed') adapter.retireMain();
                            refresh();
                        }
                    }), 25000);
                } catch {
                    if (!current()) return;
                    const actual = adapter.mainContext();
                    if (actual?.ready && actual.roomKey === desired) { s.mainStatus = 'ready'; refresh(); return; }
                    s.mainStatus = 'failed'; adapter.retireMain(); refresh();
                }
            })();
        }
        function transportLive(s, t) { return isLive(s) && s.transport === t; }
        function failTransport(s, t, message) {
            if (!transportLive(s, t)) return;
            s.transportStatus = message;
            void retireRelay(s); apply(s, null);
        }
        function deliver(s, t) {
            if (!transportLive(s, t) || !t.bound || !t.latest) return;
            let next;
            try { next = decodeSnapshot(t.latest.state, s.invitation); }
            catch { t.applied = undefined; apply(s, null, '共享房间元数据格式或执行端身份不匹配，登录未确认。'); return; }
            const signature = JSON.stringify(next);
            if (signature === t.applied) return;
            t.applied = signature; apply(s, next);
        }
        function offer(s, t, version, raw) {
            if (!transportLive(s, t) || !t.accepting) return;
            if (!Number.isSafeInteger(version) || version < 0) {
                failTransport(s, t, '中继状态序号无效，登录未确认；请关闭跟随后重试。'); return;
            }
            if (t.latest && version <= t.latest.version) return;
            t.latest = { version, state: typeof raw === 'string' && raw.length > limit ? undefined : raw };
            if (t.bound) deliver(s, t);
        }
        async function bind(s, t) {
            const epoch = ++t.epoch;
            t.accepting = true; t.bound = false; t.latest = null; t.applied = undefined;
            s.transportStatus = '正在绑定跟随中继…'; refresh();
            try {
                const binding = await within(t.connection.invoke('BindSubscriber', s.invitation.readSecret, s.invitation.writeHash));
                if (!transportLive(s, t) || epoch !== t.epoch) return;
                if (!fields(binding, ['version','state','demand']) || typeof binding.demand !== 'boolean') throw new Error();
                // Bind 的基线可能比已排队的 FollowState 旧；只采用最大序号，且必须先确认绑定。
                offer(s, t, binding.version, binding.state);
                if (!transportLive(s, t) || epoch !== t.epoch) return;
                t.bound = true; s.transportStatus = 'connected'; deliver(s, t);
            } catch {
                if (transportLive(s, t) && epoch === t.epoch) failTransport(s, t, '中继绑定未确认，可能已撤回邀请或连接失败；请核对后重试。');
            }
        }
        async function openTransport(s) {
            if (s.retiredTransport && !await retireRelay(s)) {
                if (isLive(s)) { s.transportStatus = '旧中继连接关闭未确认，请关闭跟随后重试。'; refresh(); }
                return;
            }
            if (!isLive(s) || s.transport) return;
            s.transportStatus = '正在连接 HTTPS 跟随中继…'; refresh();
            let t;
            try {
                const signalR = root.signalR;
                const connection = new signalR.HubConnectionBuilder().withUrl(s.invitation.serverUrl + '/room-follow', {
                    transport: signalR.HttpTransportType.WebSockets, skipNegotiation: true, withCredentials: false, logMessageContent: false
                }).configureLogging(signalR.LogLevel.None).withAutomaticReconnect([0, 2000, 10000, 30000]).build();
                t = { connection, epoch: 0, accepting: false, bound: false, latest: null }; s.transport = t;
                connection.on('FollowState', (version, raw) => offer(s, t, version, raw));
                connection.onreconnecting(() => {
                    if (!transportLive(s, t)) return;
                    t.epoch++; t.accepting = t.bound = false; t.latest = null;
                    s.transportStatus = '跟随中继掉线，正在重连；房间登录待确认。'; apply(s, null);
                });
                connection.onreconnected(() => { if (transportLive(s, t)) void bind(s, t); });
                connection.onclose(() => failTransport(s, t, '跟随中继已断开，登录未确认；请关闭跟随后重试。'));
                const starting = connection.start();
                Promise.resolve(starting).then(() => { if (!transportLive(s, t)) void stopTransport(t); }, () => {});
                await within(starting);
                if (!transportLive(s, t)) { void stopTransport(t); return; }
                await bind(s, t);
            } catch {
                if (t) failTransport(s, t, 'HTTPS 跟随中继连接未确认；请检查邀请、网络和服务器版本。');
                else if (isLive(s)) { s.transportStatus = '无法建立跟随中继连接；请检查网页和服务器版本。'; apply(s, null); }
            }
        }
        async function setEnabled(enabled) {
            if (enabled) {
                if (session || validating || restoring || !pageActive || !root.crypto?.subtle) return;
                validating = true; attention = false; refresh();
                try {
                    if (!adapter.canBegin()) throw new Error('原登录或房间操作仍在处理中，请稍后开启跟随。');
                    const next = draft.trim() ? await parseInvitation(draft) : invitation;
                    if (!next) throw new Error('请粘贴执行端生成的 HTTPS 跟随邀请。');
                    if (!pageActive || !adapter.canBegin()) throw new Error('原页面状态已变化，请重新开启跟随。');
                    const original = adapter.captureOriginal();
                    adapter.begin();
                    invitation = next; draft = '';
                    session = { invitation: next, original, rejected: new Set(), desiredKey: '', loginSerial: 0,
                        transport: null, snapshot: null, lastSnapshot: null, metadataError: '', transportStatus: '正在连接跟随中继…', mainStatus: '' };
                    try { root.sessionStorage.setItem(markerKey, original.unsaved ? 'unsaved' : 'active'); } catch {}
                    adapter.retireMain();
                    void openTransport(session);
                } catch (error) { idleStatus = error.message; }
                finally { validating = false; refresh(); }
                return;
            }
            const s = session;
            if (!s || restoring) return;
            session = null; restoring = true; s.loginSerial++;
            const relayStopped = retireRelay(s), mainStopped = adapter.retireMain();
            adapter.project(null, s.rejected); refresh();
            let relayClosed = false;
            try {
                relayClosed = await relayStopped;
                const restored = await adapter.restore(s.original, mainStopped, () => pageActive);
                attention = !restored;
                idleStatus = restored ? '跟随已关闭，已恢复原浏览器登录和页面。' : '跟随已关闭，原登录或连接未确认，请按普通流程重新登录。';
            } catch { attention = true; idleStatus = '跟随已关闭，原登录未确认，请按普通流程重新登录。'; }
            finally {
                try { adapter.end(s.original); }
                catch { attention = true; idleStatus = '跟随已关闭，原页面恢复未确认，请重新打开网页并登录。'; }
                if (!relayClosed) { attention = true; idleStatus += ' 旧中继连接关闭未确认，请刷新页面或在执行端撤回邀请。'; }
                try { root.sessionStorage.removeItem(markerKey); } catch {}
                restoring = false; refresh();
            }
        }
        function mountPanel(host) {
            if (!host) return;
            if (!host.querySelector('[data-follow-enabled]')) {
                host.innerHTML = '<label class="wr-check"><input type="checkbox" data-follow-enabled>跟随执行端房间</label><label class="wr-follow-input">HTTPS 跟随邀请<input type="password" data-follow-invitation autocomplete="off" maxlength="24576" spellcheck="false" placeholder="粘贴执行端生成的 BGIFOLLOW1 邀请"></label><p class="wr-muted" data-follow-identity></p><p class="wr-muted" data-follow-status role="status" aria-live="polite"></p><p class="wr-muted">只同步共享档案与查看页；使用本浏览器已有登录进行监视。邀请仅保留于本页，关闭跟随后可操作原房间簿和任务。</p>';
                const input = host.querySelector('[data-follow-invitation]'); input.value = draft;
                input.oninput = () => { draft = input.value; };
                host.querySelector('[data-follow-enabled]').onchange = event => { draft = input.value; void setEnabled(event.target.checked); };
            }
            const current = state(), check = host.querySelector('[data-follow-enabled]'), input = host.querySelector('[data-follow-invitation]');
            check.checked = current.enabled; check.disabled = current.busy || !root.crypto?.subtle;
            input.disabled = current.enabled || current.busy || !root.crypto?.subtle;
            if (input.value !== draft) input.value = draft;
            host.querySelector('[data-follow-identity]').textContent = current.identity;
            host.querySelector('[data-follow-status]').textContent = current.message;
        }
        function pagehide() {
            if (session) {
                const s = session;
                // BFCache 返回前重新绑定，期间没有旧快照或仍可操作的主连接。
                s.transportStatus = '页面已暂停，等候重新绑定。'; apply(s, null); void retireRelay(s);
            }
            pageActive = false;
            if (restoring) adapter.retireMain();
        }
        function pageshow() { const resume = !pageActive; pageActive = true; if (session && resume) void openTransport(session); }
        root.addEventListener?.('pagehide', pagehide); root.addEventListener?.('pageshow', pageshow);
        return { setEnabled, state, refresh, mountPanel, isActive: () => !!session || restoring, setDraft: value => { draft = value; } };
    }
    root.webRoomFollow = { parseInvitation, decodeSnapshot, createController };
})();
