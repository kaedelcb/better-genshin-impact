(() => {
    'use strict';
    const storageKey = target => 'bgi.update.pending.' + target.instanceId;
    const getPending = target => { try { return sessionStorage.getItem(storageKey(target)); } catch { return null; } };
    function remember(target, id) {
        try { if (id) sessionStorage.setItem(storageKey(target), id); else sessionStorage.removeItem(storageKey(target)); } catch {}
    }
    const allowed = target => target && !target.isObserver && (target.local || !!mtCredentialFor(target));
    function targetFor(member) {
        return mtTargets().find(t => t.uid === member?.playerUid && t.instanceId === member?.clientInstanceId && allowed(t));
    }

    const hints = new Map();
    let hintTimer = 0;
    const hintKey = target => JSON.stringify([target.local ? 'local' : roomCode, target.key, mtCredentialFor(target)?.browserId || '']);
    function renderVersion(member) {
        const version = String(member.bgiVersion || '版本未上报');
        if (!member.bgiVersion || !member.clientInstanceId) return escapeHtml(version);
        // 长版本号可以折行，最后几个字符与小点保持在同一行。
        return escapeHtml(version.slice(0, -5)) + '<span class="bgi-version-tail">' + escapeHtml(version.slice(-5)) +
            '<button type="button" class="bgi-update-dot" data-ui="bgi-update" data-uid="' + escapeHtml(member.playerUid) +
            '" data-instance="' + escapeHtml(member.clientInstanceId) +
            '" aria-label="有 BGI 更新，点击查看" title="有 BGI 更新，点击查看" hidden></button></span>';
    }

    function refreshHints(fetch = true) {
        clearTimeout(hintTimer);
        const buttons = document.querySelectorAll('.bgi-update-dot');
        if (!buttons.length) return;
        const pageVisible = !document.hidden && !document.getElementById('homeView')?.hidden;
        const targets = new Map(mtTargets().filter(t => allowed(t) && mtTargetReady(t)).map(t => [t.instanceId, t]));
        const now = Date.now();
        let nextRead = Infinity;
        let changed = false;
        for (const button of buttons) {
            const target = targets.get(button.dataset.instance);
            const valid = target && target.uid === button.dataset.uid;
            const key = valid ? hintKey(target) : null;
            const current = hints.get(key);
            const visible = !!current?.sourceId && now - current.at < 30000;
            if (button.hidden === visible) { button.hidden = !visible; changed = true; }
            if (!pageVisible || !valid || current?.pending) continue;
            if (!fetch || (current && now < current.retryAt)) {
                nextRead = Math.min(nextRead, current?.retryAt ?? now);
                continue;
            }

            const entry = { pending: true, at: current?.at ?? now, sourceId: current?.sourceId, retryDelay: 15000 };
            hints.set(key, entry);
            mtSend('bgi.update.hint', undefined, { target }).then(reply => {
                entry.sourceId = reply.status === 'ok' && reply.data?.available === true ? reply.data.sourceId : null;
                if (reply.status === 'ok') entry.retryDelay = 3000;
            }).catch(() => { entry.sourceId = null; }).finally(() => {
                entry.at = Date.now(); entry.pending = false;
                entry.retryAt = entry.at + entry.retryDelay;
                refreshHints(false);
            });
        }
        for (const [key, entry] of hints) if (!entry.pending && now - entry.at >= 30000) hints.delete(key);
        if (changed) scheduleMemberCardAlignment();
        // 只在可见的成员页读取执行端已有的提示状态；不检查网络安装包，也不需要房间广播变化。
        if (Number.isFinite(nextRead)) hintTimer = setTimeout(refreshHints, Math.max(0, nextRead - Date.now()));
    }

    function open(member) {
        if (document.getElementById('bgiUpdateDialog')) return;
        const targets = mtTargets().filter(allowed);
        const preferred = member ? targetFor(member) : (targets.find(t => t.local) || targets[0]);
        if (member && !preferred) return;
        const dlg = document.createElement('dialog');
        dlg.id = 'bgiUpdateDialog';
        dlg.className = 'modal bgi-update-dialog';
        dlg.setAttribute('aria-labelledby', 'bgiUpdateTitle');
        dlg.innerHTML = '<div class="bgi-update-head"><h2 id="bgiUpdateTitle">更新 BGI</h2><button type="button" data-close class="text-button">关闭</button></div>' +
            '<p data-notice class="note"></p><div data-fields><div class="bgi-update-fields">' +
            '<label>执行端<select data-target></select></label><label>下载来源<select data-source></select></label></div>' +
            '<p class="note" data-version></p><label>安装包<select data-package size="4"></select></label><p class="note" data-package-note></p>' +
            '<p class="note">更新会保留 User，其他排除项和备份沿用助手设置。更新完成后，执行端电脑上的所有助手（执行端和监控端，所有 Windows 账户）都会自动重启，原来在运行的 BGI 也会重新启动；网页会短暂断线后自动重连。</p>' +
            '<label class="note"><input type="checkbox" data-close-bgi checked> 更新前自动关闭执行端电脑上的所有 BGI（会结束 BGI 进程，正在运行的任务将中断；取消勾选则需自行先退出所有 BGI）</label>' +
            '<p class="bgi-update-status note" data-status role="status" aria-live="polite"></p>' +
            '<div class="bgi-update-actions"><button type="button" class="btn btn-secondary" data-refresh>刷新</button>' +
            '<button type="button" class="btn btn-secondary" data-download>仅下载</button>' +
            '<button type="button" class="btn btn-primary" data-install>更新</button>' +
            '<button type="button" class="btn btn-secondary danger" data-cancel hidden>取消下载</button>' +
            '<button type="button" class="text-button" data-clear hidden>解除待确认</button></div></div>';
        document.body.append(dlg);
        const el = name => dlg.querySelector('[data-' + name + ']');
        const targetBox = el('target'), sources = el('source'), packages = el('package'), status = el('status');
        const option = (value, text) => new Option(text, value);
        const states = new Map();
        let current = null, renderedState = null, renderedData = null, pollTimer = 0, stopped = false;
        function stateFor(target) {
            if (!states.has(target.key)) states.set(target.key, {
                target, data: null, job: null, pending: getPending(target), working: false,
                sourceId: hints.get(hintKey(target))?.sourceId, packageId: '', packageName: undefined,
                closeBgi: true, message: '', canClear: false, needsReload: false
            });
            return states.get(target.key);
        }
        const visible = state => !stopped && current === state;
        function render(state) {
            if (!visible(state)) return;
            const { data, job, pending, working } = state;
            const active = !!pending || (!!job && !job.done);
            const pack = data?.packages?.find(p => p.id === state.packageId);
            // 只锁当前成员的操作；切换成员不取消已经交给执行端的更新。
            targetBox.disabled = false;
            if (renderedState !== state || renderedData !== data) {
                sources.replaceChildren(...(data?.sources || []).map(x => option(x.id, x.name)));
                packages.replaceChildren(...(data?.packages || []).map(p => option(p.id, p.fileName + ' · ' + p.size)));
                renderedState = state; renderedData = data;
            }
            sources.value = state.sourceId || '';
            packages.value = state.packageId;
            el('version').textContent = data ? '当前版本：' + (data.currentVersion || '未知') + ' · BGI 目录：' + (data.targetDirectory || '未配置') : '';
            el('package-note').textContent = data ? data.packageMessage || (data.packages.length ? '请选择一个安装包。' : '没有可用安装包。') : '';
            status.textContent = state.message;
            el('close-bgi').checked = state.closeBgi;
            el('close-bgi').disabled = working || active;
            sources.disabled = working || active;
            packages.disabled = working || active;
            el('refresh').disabled = working;
            el('download').disabled = working || active || !pack?.canDownload;
            el('install').disabled = working || active || !pack || !data?.canInstall;
            el('cancel').hidden = !job?.canCancel;
            el('cancel').disabled = working || !job?.canCancel;
            el('clear').hidden = !state.canClear;
            el('clear').disabled = working;
        }
        function showJob(state, value) {
            state.job = value;
            if (!value) return;
            state.message = value.message + (value.savedPath && value.done ? '\n保存位置：' + value.savedPath : '');
            state.canClear = false;
            if (value.done && value.id === state.pending) {
                state.pending = null; remember(state.target, null);
                state.data = null; state.needsReload = true;
                hints.delete(hintKey(state.target)); refreshHints();
            }
            if (!value.done && !state.pending) { state.pending = value.id; remember(state.target, value.id); }
        }
        function schedule(state) {
            // 未选中成员的迟到回复只更新自己的记录，不能改动当前成员的轮询。
            if (!visible(state)) return;
            clearTimeout(pollTimer);
            if (state.needsReload && !state.working) {
                void load(state); // 发起后票据已消耗，重新读取才能继续安装刚下载的包。
            } else if (state.pending && !state.working) {
                pollTimer = setTimeout(() => checkStatus(state), 1700);
            }
        }
        function finish(state) {
            state.working = false;
            render(state); schedule(state);
        }
        async function checkStatus(state) {
            if (!state.pending || !visible(state) || state.working) return;
            const id = state.pending;
            state.working = true;
            if (!state.message) state.message = '正在核对上次更新操作…';
            render(state);
            try {
                const reply = await mtSend('bgi.update.status', { id }, { target: state.target });
                if (stopped || id !== state.pending) return;
                if (reply.status === 'ok' && reply.data?.id === id) showJob(state, reply.data);
                else {
                    state.message = reply.message || '执行端未确认结果。请核对后再操作。';
                    state.canClear = reply.status === 'unknown';
                }
            } catch (e) { if (!stopped) state.message = '暂时无法核对更新状态：' + e.message; }
            finally { finish(state); }
        }
        async function load(state, sourceId = state.sourceId) {
            if (!visible(state) || state.working) return;
            state.needsReload = false; state.sourceId = sourceId;
            state.working = true; state.message = '正在读取安装包…'; render(state);
            try {
                if (!mtTargetReady(state.target)) throw new Error('执行端不在线或连接未就绪。');
                const reply = await mtRead('bgi.update.catalog', { sourceId }, { target: state.target });
                if (stopped) return;
                const data = state.data = reply.data;
                state.sourceId = data.sourceId;
                const pack = data.packages.find(p => p.fileName === state.packageName);
                state.packageId = pack?.id || '';
                if (data.job && (!state.pending || data.job.id === state.pending)) showJob(state, data.job);
                else state.message = state.pending ? '正在核对上次更新操作…' : '';
            } catch (e) {
                state.data = null; state.message = e.message;
            } finally { finish(state); }
        }

        async function start(state, install) {
            const pack = state.data?.packages?.find(p => p.id === state.packageId);
            if (!pack || state.working || state.pending || (!install && !pack.canDownload) || (install && !state.data.canInstall)) return;
            const closeBgi = install && state.closeBgi;
            if (install && !confirm('安装 ' + pack.fileName + '？\n执行端：' + state.target.name +
                '\n目标目录：' + state.data.targetDirectory + (closeBgi
                    ? '\n将先关闭执行端电脑上的所有 BGI（正在运行的任务会中断），再覆盖程序文件，保留 User 目录。\n完成后该电脑上的所有助手和原来运行的 BGI 会自动重启。'
                    : '\n请先退出执行端电脑上的所有 BGI。安装会覆盖程序文件，保留 User 目录。\n完成后该电脑上的所有助手和原来运行的 BGI 会自动重启。'))) return;

            const id = Array.from(crypto.getRandomValues(new Uint8Array(16)), n => n.toString(16).padStart(2, '0')).join('');
            state.pending = id; remember(state.target, id); state.job = null; state.canClear = false;
            state.working = true; state.message = '正在向执行端发送…'; render(state);
            try {
                const reply = await mtSend('bgi.update.start', { id, ticket: pack.id, install, closeBgi }, { target: state.target });
                if (stopped) return;
                if (reply.status === 'ok' && reply.data?.id === id) showJob(state, reply.data);
                else {
                    state.message = reply.message || '执行端未确认受理。';
                    if (['rejected', 'denied'].includes(reply.status)) { state.pending = null; remember(state.target, null); }
                }
            } catch (e) {
                if (stopped) return;
                if (!e.unconfirmed) { state.pending = null; remember(state.target, null); }
                state.message = e.message;
            } finally { finish(state); }
        }
        async function cancel(state) {
            if (!state.job?.canCancel || state.working) return;
            const id = state.job.id;
            state.working = true; render(state);
            try {
                const reply = await mtSend('bgi.update.cancel', { id }, { target: state.target });
                if (stopped) return;
                if (reply.status === 'ok' && reply.data?.id === id) showJob(state, reply.data);
                else state.message = reply.message || '取消结果未确认。';
            } catch (e) { state.message = e.message; }
            finally { finish(state); }
        }
        function select(target) {
            clearTimeout(pollTimer);
            current = stateFor(target);
            render(current);
            if (current.pending) void checkStatus(current);
            else if (!current.data || current.needsReload) void load(current);
        }
        el('close').onclick = () => dlg.close();
        dlg.addEventListener('close', () => { stopped = true; clearTimeout(pollTimer); dlg.remove(); });
        if (!preferred) {
            el('notice').textContent = '没有已授权的执行端。请从助手打开本机网页，或先在槲寄生页面用自己的助手授权码绑定执行端。';
            el('fields').hidden = true;
        } else {
            targetBox.replaceChildren(...targets.map(t => option(t.key, (t.local ? '本机 · ' : '') + t.name + (t.uid ? ' · ' + t.uid : ''))));
            targetBox.value = preferred.key;
            targetBox.onchange = () => {
                const target = targets.find(t => t.key === targetBox.value);
                if (target) select(target);
            };
            sources.onchange = () => load(current, sources.value);
            packages.onchange = () => {
                current.packageId = packages.value;
                current.packageName = current.data?.packages?.find(p => p.id === packages.value)?.fileName;
                render(current);
            };
            el('close-bgi').onchange = () => { current.closeBgi = el('close-bgi').checked; };
            el('refresh').onclick = () => current.pending ? checkStatus(current) : load(current);
            el('download').onclick = () => start(current, false);
            el('install').onclick = () => start(current, true);
            el('cancel').onclick = () => cancel(current);
            el('clear').onclick = () => {
                const state = current;
                if (!state.pending || state.working || !confirm('执行端无法确认上次操作。只有在你已核对实际状态后才解除等待；不会重发。')) return;
                state.pending = null; remember(state.target, null); state.job = null; state.canClear = false;
                state.message = '已解除等待；上次结果仍未确认。';
                render(state);
            };
        }
        dlg.showModal();
        if (preferred) select(preferred);
    }

    window.bgiWebUpdate = { targetFor, open, renderVersion, refreshHints };
    document.addEventListener('visibilitychange', () => refreshHints());
    document.getElementById('bgiUpdateNav')?.addEventListener('click', () => open());
})();
