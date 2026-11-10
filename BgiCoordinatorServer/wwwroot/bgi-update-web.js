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
        let target = preferred, data = null, job = null, pending = target && getPending(target), working = false;
        let epoch = 0, pollTimer = 0, stopped = false;

        function controls() {
            const active = !!pending || (!!job && !job.done);
            const pack = data?.packages?.find(p => p.id === packages.value);
            sources.disabled = working || active;
            packages.disabled = working || active;
            targetBox.disabled = working || active;
            el('refresh').disabled = working;
            el('download').disabled = working || active || !pack?.canDownload;
            el('install').disabled = working || active || !pack || !data?.canInstall;
            el('cancel').hidden = !job?.canCancel;
            el('cancel').disabled = working || !job?.canCancel;
        }
        function showJob(value) {
            job = value;
            if (!value) return;
            status.textContent = value.message + (value.savedPath && value.done ? '\n保存位置：' + value.savedPath : '');
            el('clear').hidden = true;
            if (value.done && value.id === pending) {
                const name = data?.packages?.find(p => p.id === packages.value)?.fileName;
                const sourceId = sources.value;
                pending = null; remember(target, null); data = null;
                hints.delete(hintKey(target)); refreshHints();
                setTimeout(() => load(sourceId, name), 0); // 发起后票据已消耗，刷新后可继续安装刚下载的包。
            }
            if (!value.done && !pending) { pending = value.id; remember(target, pending); }
        }
        function schedule() {
            clearTimeout(pollTimer);
            if (!stopped && pending) pollTimer = setTimeout(checkStatus, 1700);
        }
        async function checkStatus() {
            if (!pending || stopped || working) return;
            const id = pending;
            const selected = target;
            working = true; controls();
            try {
                const reply = await mtSend('bgi.update.status', { id }, { target: selected });
                if (stopped || target !== selected || id !== pending) return;
                if (reply.status === 'ok' && reply.data?.id === id) showJob(reply.data);
                else {
                    status.textContent = reply.message || '执行端未确认结果。请核对后再操作。';
                    el('clear').hidden = reply.status !== 'unknown';
                }
            } catch (e) { if (!stopped) status.textContent = '暂时无法核对更新状态：' + e.message; }
            finally { working = false; if (!stopped) { controls(); schedule(); } }
        }
        async function load(sourceId, selectedName) {
            if (!target || stopped || working) return;
            const turn = ++epoch;
            const selected = target;
            working = true; controls();
            try {
                if (!mtTargetReady(selected)) throw new Error('执行端不在线或连接未就绪。');
                status.textContent = '正在读取安装包…';
                const reply = await mtRead('bgi.update.catalog', { sourceId }, { target: selected });
                if (stopped || epoch !== turn) return;
                data = reply.data;
                sources.replaceChildren(...data.sources.map(x => option(x.id, x.name)));
                sources.value = data.sourceId;
                packages.replaceChildren(...data.packages.map(p => option(p.id, p.fileName + ' · ' + p.size)));
                const chosenPackage = data.packages.find(p => p.fileName === selectedName);
                if (chosenPackage) packages.value = chosenPackage.id; else packages.selectedIndex = -1;
                el('version').textContent = '当前版本：' + (data.currentVersion || '未知') + ' · BGI 目录：' + (data.targetDirectory || '未配置');
                el('package-note').textContent = data.packageMessage || (data.packages.length ? '请选择一个安装包。' : '没有可用安装包。');
                if (data.job && (!pending || data.job.id === pending)) showJob(data.job);
                else status.textContent = pending ? '正在核对上次更新操作…' : '';
            } catch (e) {
                if (!stopped) { data = null; packages.replaceChildren(); status.textContent = e.message; }
            } finally {
                working = false;
                if (!stopped) { controls(); schedule(); }
            }
        }

        async function start(install) {
            const pack = data?.packages?.find(p => p.id === packages.value);
            if (!pack || working || pending || (!install && !pack.canDownload) || (install && !data.canInstall)) return;
            const closeBgi = install && el('close-bgi').checked;
            if (install && !confirm('安装 ' + pack.fileName + '？\n执行端：' + target.name +
                '\n目标目录：' + data.targetDirectory + (closeBgi
                    ? '\n将先关闭执行端电脑上的所有 BGI（正在运行的任务会中断），再覆盖程序文件，保留 User 目录。\n完成后该电脑上的所有助手和原来运行的 BGI 会自动重启。'
                    : '\n请先退出执行端电脑上的所有 BGI。安装会覆盖程序文件，保留 User 目录。\n完成后该电脑上的所有助手和原来运行的 BGI 会自动重启。'))) return;

            const id = Array.from(crypto.getRandomValues(new Uint8Array(16)), n => n.toString(16).padStart(2, '0')).join('');
            pending = id; remember(target, id); job = null;
            working = true; status.textContent = '正在向执行端发送…'; controls();
            try {
                const reply = await mtSend('bgi.update.start', { id, ticket: pack.id, install, closeBgi }, { target });
                if (stopped) return;
                if (reply.status === 'ok' && reply.data?.id === id) showJob(reply.data);
                else {
                    status.textContent = reply.message || '执行端未确认受理。';
                    if (['rejected', 'denied'].includes(reply.status)) { pending = null; remember(target, null); }
                }
            } catch (e) {
                if (!e.unconfirmed) { pending = null; remember(target, null); }
                if (!stopped) status.textContent = e.message;
            } finally {
                working = false;
                if (!stopped) { controls(); schedule(); }
            }
        }
        async function cancel() {
            if (!job?.canCancel || working) return;
            working = true; controls();
            try {
                const reply = await mtSend('bgi.update.cancel', { id: job.id }, { target });
                if (!stopped && reply.status === 'ok' && reply.data?.id === job.id) showJob(reply.data);
                else if (!stopped) status.textContent = reply.message || '取消结果未确认。';
            } catch (e) { if (!stopped) status.textContent = e.message; }
            finally { working = false; if (!stopped) { controls(); schedule(); } }
        }
        el('close').onclick = () => dlg.close();
        dlg.addEventListener('close', () => { stopped = true; clearTimeout(pollTimer); dlg.remove(); });
        if (!target) {
            el('notice').textContent = '没有已授权的执行端。请从助手打开本机网页，或先在槲寄生页面用自己的助手授权码绑定执行端。';
            el('fields').hidden = true;
        } else {
            targetBox.replaceChildren(...targets.map(t => option(t.key, (t.local ? '本机 · ' : '') + t.name + (t.uid ? ' · ' + t.uid : ''))));
            targetBox.value = target.key;
            targetBox.onchange = () => {
                const selected = targets.find(t => t.key === targetBox.value);
                if (!selected || working || pending) return;
                target = selected; data = null; job = null; pending = getPending(target); load();
            };
            sources.onchange = () => load(sources.value);
            packages.onchange = controls;
            el('refresh').onclick = () => pending ? checkStatus() : load(sources.value || undefined);
            el('download').onclick = () => start(false);
            el('install').onclick = () => start(true);
            el('cancel').onclick = cancel;
            el('clear').onclick = () => {
                if (!pending || working || !confirm('执行端无法确认上次操作。只有在你已核对实际状态后才解除等待；不会重发。')) return;
                pending = null; remember(target, null); job = null; el('clear').hidden = true;
                status.textContent = '已解除等待；上次结果仍未确认。'; controls();
            };
        }
        dlg.showModal();
        if (target) load(hints.get(hintKey(target))?.sourceId);
    }

    window.bgiWebUpdate = { targetFor, open, renderVersion, refreshHints };
    document.addEventListener('visibilitychange', () => refreshHints());
    document.getElementById('bgiUpdateNav')?.addEventListener('click', () => open());
})();
