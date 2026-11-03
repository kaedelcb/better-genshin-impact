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
            '<p class="note">网页更新须先退出执行端的 BGI；会保留 User 和运行中的助手，其他排除项和备份沿用助手设置。</p>' +
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
            if (install && !confirm('安装 ' + pack.fileName + '？\n执行端：' + target.name +
                '\n目标目录：' + data.targetDirectory + '\n请先退出执行端 BGI。安装会覆盖程序文件，保留 User 目录。')) return;

            const id = Array.from(crypto.getRandomValues(new Uint8Array(16)), n => n.toString(16).padStart(2, '0')).join('');
            pending = id; remember(target, id); job = null;
            working = true; status.textContent = '正在向执行端发送…'; controls();
            try {
                const reply = await mtSend('bgi.update.start', { id, ticket: pack.id, install }, { target });
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
        if (target) load();
    }

    window.bgiWebUpdate = { targetFor, open };
    document.getElementById('bgiUpdateNav')?.addEventListener('click', () => open());
})();
