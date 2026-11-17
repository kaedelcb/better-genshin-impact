/* Isolated route management. Uploader mode never requests admin reads or cookie credentials. */
(() => {
    'use strict';
    const C = globalThis.RoutePackages;
    const $ = id => document.getElementById(id);
    const admin = document.body.dataset.mode === 'admin';
    const state = { plan: null, blob: null, worker: null, controller: null, phase: 'idle', uploadUnknown: false,
        uploaded: false, previewLimit: 100, catalog: null, catalogFresh: false, catalogSequence: 0, selectedId: null, detailSequence: 0,
        detail: null, detailRevision: null, selectedPaths: new Set(), action: null, mutationBusy: false };
    function node(tag, text, className) {
        const element = document.createElement(tag);
        if (text !== undefined) element.textContent = text;
        if (className) element.className = className;
        return element;
    }
    function status(id, message, error = false) {
        const element = $(id);
        element.textContent = message; element.classList.toggle('error', error);
    }
    function secureOrigin() {
        return location.protocol === 'https:' || ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname);
    }
    // 轻量提示：操作结果不用盯着页面顶部的小字，右下角弹出几秒后自动消失。
    function toast(message, error = false) {
        const box = $('toast'); if (!box) return;
        box.textContent = message; box.classList.toggle('error', error); box.hidden = false;
        clearTimeout(state.toastTimer); state.toastTimer = setTimeout(() => { box.hidden = true; }, error ? 8000 : 4500);
    }
    // 上传三步指示：1 选文件夹 → 2 核对并打包 → 3 上传/发布；只有管理员页有这个元素。
    function setStep(current, done = false) {
        ['step1', 'step2', 'step3'].forEach((id, index) => {
            const step = $(id); if (!step) return;
            step.classList.toggle('active', index + 1 === current && !done);
            step.classList.toggle('done', index + 1 < current || done);
        });
    }
    async function request(path, { method = 'GET', body, headers = {}, signal, timeout = 30000 } = {}) {
        if (!secureOrigin()) throw new Error('线路管理和令牌上传必须使用 HTTPS（本机 loopback 开发除外）。');
        const controller = new AbortController();
        const abort = () => controller.abort();
        if (signal?.aborted) abort();
        signal?.addEventListener('abort', abort, { once: true });
        let timedOut = false;
        const timer = setTimeout(() => { timedOut = true; controller.abort(); }, timeout);
        try {
            const response = await fetch(path, { method, body, headers, signal: controller.signal, cache: 'no-store',
                credentials: admin ? 'same-origin' : 'omit', redirect: 'error' });
            if (!response.ok) {
                let detail = '';
                try { const data = await response.json(); detail = data.message || data.error?.message || (typeof data.error === 'string' ? data.error : '') || data.title || ''; } catch { /* Status is authoritative even when no JSON body is available. */ }
                const labels = { 400: '请求或线路校验未通过', 401: admin ? '管理员登录已失效' : '上传令牌无效或已撤销', 403: '权限或 CSRF 校验失败，请刷新后重试',
                    404: '版本或接口不存在', 409: '版本修订冲突，请刷新清单，重新检查差异后再操作', 413: '线路包超过服务端大小上限',
                    429: '请求过于频繁，请稍后重试', 503: '线路服务尚未配置或暂不可用', 507: '线路包存储空间不足，请联系管理员' };
                const error = new Error((labels[response.status] || '请求失败（HTTP ' + response.status + '）') + (detail ? '：' + String(detail).slice(0, 1500) : ''));
                error.status = response.status;
                if (admin && response.status === 401) $('loginLink').hidden = false;
                throw error;
            }
            if (response.status === 204) return null;
            const text = await response.text();
            return text ? JSON.parse(text) : null;
        } catch (error) {
            if (timedOut) throw new Error('请求超时，结果未确认，请刷新核对；不会自动重试写入。');
            throw error;
        } finally {
            clearTimeout(timer); signal?.removeEventListener('abort', abort);
        }
    }
    async function csrf() {
        if (!admin) throw new Error('上传者不能执行管理员操作。');
        const data = await request('/api/admin/routes/csrf');
        if (typeof data?.token !== 'string' || !data.token) throw new Error('未取得 CSRF 令牌，请重新登录。');
        return data.token;
    }
    function updateUploadControls() {
        const controls = C.uploadControls({ ...state, privacyConfirmed: $('privacyConfirm').checked, admin, token: admin ? '' : $('uploadToken').value });
        $('folderInput').disabled = controls.busy; $('clearFolder').disabled = controls.busy;
        $('privacyConfirm').disabled = controls.busy; $('uploadDescription').disabled = controls.busy;
        if (!admin) $('uploadToken').disabled = controls.busy;
        $('packButton').disabled = !controls.canPack;
        $('uploadButton').disabled = !controls.canUpload;
        const publishNow = $('uploadPublishButton');
        if (publishNow) { publishNow.hidden = !admin; publishNow.disabled = !controls.canUpload; }
        $('cancelUpload').hidden = !controls.busy;
    }
    function renderPreview() {
        const query = $('previewSearch').value.toLowerCase();
        const files = (state.plan?.entries || []).filter(item => item.path.toLowerCase().includes(query));
        const list = $('folderPreview'); list.replaceChildren();
        for (const item of files.slice(0, state.previewLimit)) {
            const li = node('li');
            li.append(node('span', item.path, 'file-path'), node('span', C.formatBytes(item.sizeBytes), 'file-size')); list.append(li);
        }
        if (state.plan && !files.length) list.append(node('li', '没有匹配的入包文件。', 'muted'));
        $('morePreview').hidden = files.length <= state.previewLimit;
        $('morePreview').textContent = `再显示 ${Math.min(100, files.length - state.previewLimit)} 个（匹配 ${files.length} 个）`;
    }
    function clearSelection() {
        state.plan = null; state.blob = null; state.uploaded = false; state.uploadUnknown = false;
        $('folderInput').value = ''; $('privacyConfirm').checked = false; $('previewSearch').value = '';
        $('previewSearch').disabled = true; $('folderPreview').replaceChildren(); $('morePreview').hidden = true;
        $('uploadProgress').hidden = true; $('uploadResult').hidden = true; $('uploadResult').replaceChildren();
        $('folderSummary').textContent = '选择文件夹后在这里显示要上传的文件。';
        status('uploadStatus', '本机选择已清除，未删除任何本机文件。'); setStep(1); updateUploadControls();
    }
    // 版本名称默认当天（北京时间）日期；如果今天已经有同名版本，页面先按顺序预填 -2、-3，
    // 服务端也会兜底自动编号，所以同一天多次上传不会重名、也不会互相覆盖。管理员自己改过的内容不会被覆盖。
    function todayStamp() {
        return new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Shanghai', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date()).replaceAll('-', '');
    }
    function nextDescription() {
        const base = todayStamp();
        const used = new Set((state.catalog?.packages || []).map(pkg => pkg.description));
        if (!used.has(base)) return base;
        for (let n = 2; n < 1000; n++) if (!used.has(base + '-' + n)) return base + '-' + n;
        return base;
    }
    function fillDescription() {
        const box = $('uploadDescription');
        if (!box.value.trim() || box.value === state.autoDescription) { state.autoDescription = nextDescription(); box.value = state.autoDescription; }
        const hint = $('descriptionHint');
        if (hint) hint.textContent = box.value === state.autoDescription
            ? '自动命名：' + state.autoDescription + '。同一天再次上传会自动加 -2、-3，按名称就能看出先后。'
            : '已使用自定义名称；如果和已有版本同名，服务器会自动在后面加序号。';
    }
    $('uploadDescription').addEventListener('input', fillDescription);
    $('folderInput').addEventListener('change', () => {
        fillDescription();
        state.blob = null; state.plan = null; state.previewLimit = 100; state.uploaded = false; state.uploadUnknown = false;
        $('privacyConfirm').checked = false; $('uploadProgress').hidden = true; $('uploadResult').hidden = true; $('previewSearch').value = '';
        try {
            state.plan = C.planFiles($('folderInput').files);
            const p = state.plan;
            $('folderSummary').textContent = `${p.rootName} · ${p.entries.length} 个文件，${p.routeCount} 条线路；${p.layout}。预计 ZIP ${C.formatBytes(p.zipBytes)}，未压缩。`;
            $('previewSearch').disabled = false;
            status('uploadStatus', '请核对右侧预览，勾选确认后点“校验并本机打包”。');
            setStep(2);
        } catch (error) {
            $('folderSummary').textContent = '文件夹未通过检查，不会打包或上传。';
            $('previewSearch').disabled = true; status('uploadStatus', error.message, true); setStep(1);
        }
        renderPreview(); updateUploadControls();
    });
    $('previewSearch').addEventListener('input', () => { state.previewLimit = 100; renderPreview(); });
    fillDescription();
    $('morePreview').addEventListener('click', () => { state.previewLimit += 100; renderPreview(); });
    $('privacyConfirm').addEventListener('change', updateUploadControls);
    if (!admin) $('uploadToken').addEventListener('input', updateUploadControls);
    $('clearFolder').addEventListener('click', clearSelection);
    $('packButton').addEventListener('click', () => {
        if (!state.plan || !$('privacyConfirm').checked || state.phase !== 'idle') return;
        if (!globalThis.Worker) { status('uploadStatus', '此浏览器不支持后台 Worker，请使用新版 Edge、Chrome 或 Firefox。', true); return; }
        state.phase = 'pack'; $('uploadProgress').hidden = false; $('uploadProgress').value = 0; updateUploadControls();
        status('uploadStatus', '正在后台逐个校验线路并计算 CRC32，界面可继续操作…');
        let worker;
        const finish = () => { worker?.terminate(); state.worker = null; state.phase = 'idle'; updateUploadControls(); };
        try {
            worker = new Worker('/route-packages-worker.js?v=20261118'); state.worker = worker;
            worker.onmessage = event => {
                if (state.worker !== worker) return;
                const data = event.data;
                if (data.type === 'progress') {
                    $('uploadProgress').value = Math.round(data.processed / data.total * 100);
                    status('uploadStatus', `本机打包 ${Math.round(data.processed / data.total * 100)}% · ${data.index + 1}/${data.count} · ${data.path}`);
                } else if (data.type === 'complete') {
                    state.blob = data.blob; $('uploadProgress').value = 100;
                    status('uploadStatus', `校验通过，已打包成 ${C.formatBytes(data.blob.size)} 的 ZIP。` + (admin ? '点“上传并发布”一步完成，或先“上传为草稿”再检查差异。' : '请点“上传为草稿”。'));
                    setStep(3); finish();
                } else if (data.type === 'error') { status('uploadStatus', data.message, true); $('uploadProgress').hidden = true; finish(); }
            };
            worker.onerror = event => { event.preventDefault(); if (state.worker !== worker) return; status('uploadStatus', '后台打包失败，请确认本地文件未移动并重新选择目录。', true); $('uploadProgress').hidden = true; finish(); };
            worker.postMessage({ type: 'pack', files: state.plan.entries.map(entry => ({ file: entry.file, relativePath: entry.file.webkitRelativePath })) });
        } catch (error) { status('uploadStatus', '无法启动后台打包：' + error.message, true); $('uploadProgress').hidden = true; finish(); }
    });
    $('cancelUpload').addEventListener('click', () => {
        if (state.phase === 'pack') {
            state.worker?.terminate(); state.worker = null; state.phase = 'idle'; state.blob = null;
            $('uploadProgress').hidden = true; status('uploadStatus', '本机打包已取消，没有上传文件。'); updateUploadControls();
        } else if (state.phase === 'upload') {
            state.controller?.abort();
            status('uploadStatus', '已请求取消上传；若服务端已接收，仍可能创建草稿，结果需要管理员核对。');
        }
    });
    async function uploadFlow(publishAfter) {
        if ($('uploadButton').disabled) return;
        const description = $('uploadDescription').value.trim();
        if (!description) { status('uploadStatus', '请填写版本名称。', true); $('uploadDescription').focus(); return; }
        if (!secureOrigin()) { status('uploadStatus', '线路管理和令牌上传必须使用 HTTPS（本机 loopback 开发除外）。未发送请求。', true); return; }
        state.phase = 'upload'; state.controller = new AbortController(); updateUploadControls();
        $('uploadProgress').hidden = false; $('uploadProgress').removeAttribute('value');
        status('uploadStatus', '正在上传，服务端会重新校验全部文件，请稍候…');
        let sent = false;
        try {
            const headers = { 'Content-Type': 'application/zip' };
            if (admin) headers['X-CSRF-Token'] = await csrf();
            else headers['X-Route-Upload-Token'] = $('uploadToken').value.trim();
            if (state.controller.signal.aborted) throw new DOMException('已取消', 'AbortError');
            sent = true;
            const result = await request('/api/admin/routes/upload?description=' + encodeURIComponent(description), {
                method: 'POST', body: state.blob, headers, signal: state.controller.signal, timeout: 600000 });
            if (typeof result?.id !== 'string' || !/^[a-f0-9]{32}$/.test(result.id) || result.state !== 'draft') throw new Error('服务端未返回有效草稿，结果未确认。');
            state.uploaded = true;
            const output = $('uploadResult'); output.replaceChildren(node('strong', '已创建草稿（未发布）：' + (result.description || description)),
                node('p', `${result.fileCount} 个文件 / ${result.routeCount} 条线路 / ${C.formatBytes(result.sizeBytes)}`)); output.hidden = false;
            if (!admin) $('uploadToken').value = '';
            state.blob = null;
            if (!admin) { status('uploadStatus', '上传完成。请把草稿名称告诉管理员审核发布。'); return; }
            await loadCatalog();
            if (!publishAfter) {
                status('uploadStatus', '草稿已创建，还没有发布。请在下方版本列表查看差异后发布。'); toast('草稿已创建：' + (result.description || description));
                setStep(3, true); await loadDetail(result.id); await loadAudit(); return;
            }
            // 一步发布：只发布刚上传、服务端校验通过的这个草稿；修订号冲突时服务端会拒绝，不会覆盖别人的发布。
            status('uploadStatus', '草稿已创建，正在发布…');
            try {
                const published = await request('/api/admin/routes/' + result.id + '/publish', { method: 'POST',
                    headers: { 'X-CSRF-Token': await csrf(), 'Content-Type': 'application/json' },
                    body: JSON.stringify(C.buildMutation(state.catalog, '日常更新')), timeout: 120000 });
                if (published?.package?.id !== result.id) throw new Error('服务端未确认发布结果。');
                status('uploadStatus', '已发布：' + (result.description || description)); toast('已发布：' + (result.description || description));
                setStep(3, true);
            } catch (error) {
                status('uploadStatus', '草稿已创建但发布失败：' + error.message + ' 请在下方版本列表手动发布。', true);
                toast('发布失败，草稿已保留', true);
            }
            await loadCatalog(); await loadDetail(result.id); await loadAudit();
        } catch (error) {
            // A lost response/abort does not prove rollback. Never automatically resend a non-idempotent upload.
            state.uploadUnknown = sent && (!error.status || error.status >= 500);
            const suffix = state.uploadUnknown ? ' 上传结果未确认，不会自动重试；请刷新清单核对，避免重复草稿。' : '';
            status('uploadStatus', (error.name === 'AbortError' ? '上传已中断。' : error.message) + suffix, true);
        } finally {
            state.controller = null; state.phase = 'idle'; $('uploadProgress').hidden = true; updateUploadControls();
        }
    }
    $('uploadButton').addEventListener('click', () => uploadFlow(false));
    $('uploadPublishButton')?.addEventListener('click', () => uploadFlow(true));
    window.addEventListener('pagehide', () => {
        state.worker?.terminate(); state.worker = null; state.controller?.abort();
        if (state.phase === 'pack') {
            state.phase = 'idle'; state.blob = null; $('uploadProgress').hidden = true;
            status('uploadStatus', '离开页面已取消本机打包，没有上传文件。');
        }
        if (!admin) $('uploadToken').value = '';
        updateUploadControls();
    });

    if (!admin) { updateUploadControls(); return; }
    const label = pkg => ({ draft: '草稿', published: '已发布', archived: '历史版本' }[pkg.state] || pkg.state || '未知状态');
    const validId = id => typeof id === 'string' && /^[a-f0-9]{32}$/.test(id);
    const shortId = id => String(id).slice(0, 12);
    const date = text => { const d = new Date(text); return Number.isFinite(d.getTime()) ? d.toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', hour12: false }) + '（北京时间）' : '时间未记录'; };
    const shortDate = text => { const d = new Date(text); return Number.isFinite(d.getTime()) ? d.toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', hour12: false, month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' }) : '时间未记录'; };
    function packageName(pkg) { return (pkg.description || '未填写版本说明') + ' [' + shortId(pkg.id) + ']'; }
    function renderCatalog() {
        const catalog = state.catalog;
        if (!catalog) return;
        const current = catalog.current;
        $('currentName').textContent = current?.package ? (current.package.description || '未填写版本说明') : '尚无发布版本';
        $('currentMeta').textContent = current?.package
            ? `${shortDate(current.package.createdAtUtc)} 上传 · ${current.package.routeCount} 条线路 · ${C.formatBytes(current.package.sizeBytes)} · 修订 ${current.revision}`
            : '请上传线路并发布第一个版本。';
        $('currentPublication').textContent = current?.package ? `${packageName(current.package)} · 修订 ${current.revision}` : '尚无发布版本。请上传、校验并发布首个线路包。';
        $('requiredVersion').textContent = catalog.requiredClientVersion ? '客户端版本限制：' + catalog.requiredClientVersion : '不限制客户端版本';
        $('storageUsage').textContent = `已用存储 ${C.formatBytes(catalog.storageBytes)} / ${C.formatBytes(catalog.maxStorageBytes)}`;
        const filter = $('packageFilter').value, list = $('packageList'); list.replaceChildren();
        for (const tab of document.querySelectorAll?.('.filter-tabs .tab') || []) {
            const active = tab.dataset.filter === filter; tab.classList.toggle('active', active); tab.setAttribute('aria-selected', String(active));
        }
        // 当前使用的置顶，其余按上传时间从新到旧，一眼能看出最新版本。
        const packages = (catalog.packages || []).filter(pkg => filter === 'all' || (filter === 'draft' ? pkg.state === 'draft' : pkg.state !== 'draft'))
            .sort((a, b) => (current?.package?.id === b.id) - (current?.package?.id === a.id) || String(b.createdAtUtc).localeCompare(String(a.createdAtUtc)));
        for (const pkg of packages) {
            const isCurrent = current?.package?.id === pkg.id;
            const button = node('button', undefined, 'package-option'); button.type = 'button'; button.setAttribute('aria-pressed', String(state.selectedId === pkg.id));
            const badge = node('span', isCurrent ? '当前使用' : label(pkg), 'badge ' + (isCurrent ? 'badge-live' : pkg.state === 'draft' ? 'badge-draft' : 'badge-old'));
            const head = node('span', undefined, 'package-head'); head.append(node('strong', pkg.description || '未填写版本说明'), badge);
            button.append(head, node('small', `${shortDate(pkg.createdAtUtc)} · ${pkg.routeCount} 条线路 · ${C.formatBytes(pkg.sizeBytes)}`));
            button.disabled = !validId(pkg.id); button.addEventListener('click', () => loadDetail(pkg.id)); list.append(button);
        }
        if (!packages.length) list.append(node('p', filter === 'draft' ? '没有草稿。' : '没有版本。先在上面选择文件夹上传。', 'muted'));
        fillDescription();
    }
    async function loadCatalog() {
        const sequence = ++state.catalogSequence;
        state.catalogFresh = false; $('refreshCatalog').disabled = true;
        try {
            const catalog = await request('/api/admin/routes');
            if (sequence !== state.catalogSequence) return false;
            if (!Array.isArray(catalog?.packages)) throw new Error('服务器返回的版本清单无效。');
            const revision = C.publicationRevision(catalog);
            if (state.detail && (state.detailRevision !== revision || !catalog.packages.some(pkg => pkg.id === state.selectedId))) {
                state.detail = null; state.detailRevision = null; state.detailSequence++;
                $('packageDetail').setAttribute('aria-busy', 'false');
                $('packageDetail').replaceChildren(node('p', '发布修订或版本清单已变化，请重新选择版本，检查最新差异。', 'muted'));
            }
            state.catalog = catalog; state.catalogFresh = true; renderCatalog();
            status('globalStatus', '线路清单已刷新 · ' + new Date().toLocaleTimeString('zh-CN', { hour12: false }));
            return true;
        } catch (error) {
            if (sequence === state.catalogSequence) {
                state.catalogFresh = false; status('globalStatus', error.message + ' 当前不能发布、回滚或恢复。', true);
            }
            return false;
        } finally { if (sequence === state.catalogSequence) $('refreshCatalog').disabled = false; }
    }
    function actionButton(text, kind, pkg, className = '') {
        const button = node('button', text, className); button.type = 'button';
        button.addEventListener('click', () => openAction(kind, pkg)); return button;
    }
    function detailFiles(pkg, container) {
        const details = node('details'), summary = node('summary', `服务端完整文件清单（${pkg.files?.length || 0} 个）`);
        details.append(summary);
        const search = node('input'); search.type = 'search'; search.placeholder = '筛选文件或目录'; search.setAttribute('aria-label', '筛选版本文件');
        const list = node('ul', undefined, 'file-list'), more = node('button', '显示更多'); more.type = 'button';
        let limit = 100;
        const render = () => {
            const files = (pkg.files || []).filter(file => file.path.toLowerCase().includes(search.value.toLowerCase())); list.replaceChildren();
            for (const file of files.slice(0, limit)) {
                const li = node('li'); li.append(node('span', file.path, 'file-path'), node('span', C.formatBytes(file.sizeBytes), 'file-size'));
                li.title = 'SHA-256：' + file.sha256; list.append(li);
            }
            more.hidden = files.length <= limit;
        };
        search.addEventListener('input', () => { limit = 100; render(); }); more.addEventListener('click', () => { limit += 100; render(); });
        details.append(search, list, more); render(); container.append(details);
    }
    function restorePanel(pkg, container) {
        const drafts = (state.catalog?.packages || []).filter(item => item.state === 'draft' && validId(item.id));
        const panel = node('section', undefined, 'restore-panel'); panel.append(node('h4', '从此历史版本恢复选定线路'));
        panel.append(node('p', '将选定文件或目录恢复到目标草稿的新副本，原包不变。选择目录会完整替换新副本中该目录的内容。请选完整线路目录，避免遗漏变体或 control.json5；引用、控制合并与依赖由服务端最终验证，失败不会当作成功。', 'help'));
        const destinationLabel = node('label', '目标草稿'), destination = node('select'); destination.id = 'restoreDestination'; destinationLabel.htmlFor = destination.id;
        destination.append(new Option('选择接收线路的草稿', ''));
        for (const draft of drafts) destination.append(new Option(packageName(draft), draft.id));
        const searchLabel = node('label', '筛选要恢复的目录或文件'), search = node('input'); search.id = 'restoreSearch'; searchLabel.htmlFor = search.id; search.type = 'search'; search.placeholder = '例如 builtin/纳塔/';
        const tools = node('div', undefined, 'restore-tools'), select = node('button', '选中全部筛选结果'), clear = node('button', '清空选择'); select.type = clear.type = 'button';
        tools.append(select, clear);
        const count = node('p', '已选 0 个路径', 'help'), list = node('ul', undefined, 'file-list'), more = node('button', '显示更多目录 / 文件'); more.type = 'button';
        const options = C.restoreOptions(pkg);
        let limit = 100;
        const filtered = () => options.filter(option => option.path.toLowerCase().includes(search.value.toLowerCase()));
        const covered = path => path.split('/').some((_, i, parts) => state.selectedPaths.has(parts.slice(0, i).join('/')));
        const compact = () => { state.selectedPaths = new Set(C.restoreSelection(pkg, [...state.selectedPaths])); };
        const render = () => {
            const choices = filtered(); list.replaceChildren();
            for (const choice of choices.slice(0, limit)) {
                const li = node('li'), checkLabel = node('label'), checkbox = node('input'); checkbox.type = 'checkbox';
                const inherited = covered(choice.path);
                checkbox.checked = inherited || state.selectedPaths.has(choice.path); checkbox.disabled = inherited;
                checkbox.addEventListener('change', () => {
                    if (checkbox.checked) { state.selectedPaths.add(choice.path); compact(); } else state.selectedPaths.delete(choice.path);
                    render();
                });
                checkLabel.append(checkbox, node('span', choice.directory ? `${choice.path}/（目录 · ${choice.fileCount} 个文件）` : choice.path));
                if (inherited) checkLabel.title = '已由选定父目录包含；取消父目录后可单独选择。';
                li.append(checkLabel); list.append(li);
            }
            more.hidden = choices.length <= limit;
            const fileCount = (pkg.files || []).filter(file => state.selectedPaths.has(file.path) || covered(file.path)).length;
            count.textContent = `已选 ${state.selectedPaths.size} 个路径前缀，覆盖 ${fileCount} 个文件；父目录已包含的子项不会重复发送。筛选不会清空选择。`;
        };
        search.addEventListener('input', () => { limit = 100; render(); }); more.addEventListener('click', () => { limit += 100; render(); });
        select.addEventListener('click', () => { for (const choice of filtered()) state.selectedPaths.add(choice.path); if (state.selectedPaths.size) compact(); render(); });
        clear.addEventListener('click', () => { state.selectedPaths.clear(); render(); });
        const restore = actionButton('恢复选定线路到新草稿', 'restore', pkg); restore.disabled = !drafts.length;
        panel.append(destinationLabel, destination, searchLabel, search, tools, count, list, more, restore);
        if (!drafts.length) panel.append(node('p', '请先上传目标草稿，然后从此历史版本选择要恢复的线路。', 'help'));
        render(); container.append(panel);
    }
    function renderDetail(data) {
        const pkg = data.package, detail = $('packageDetail'); detail.replaceChildren();
        detail.append(node('h3', pkg.description || '未填写版本说明'), node('p', label(pkg) + (state.catalog?.current?.package?.id === pkg.id ? ' · 当前使用' : ''), 'state-tag'));
        const meta = node('dl', undefined, 'detail-meta');
        for (const [key, value] of [['包 ID', pkg.id], ['创建时间', date(pkg.createdAtUtc)], ['大小与线路', `${C.formatBytes(pkg.sizeBytes)} · ${pkg.fileCount} 个文件 · ${pkg.routeCount} 条线路`], ['客户端版本限制', pkg.requiredClientVersion || '无限制'], ['SHA-256', pkg.sha256]])
            meta.append(node('dt', key), node('dd', value));
        detail.append(meta);
        const actions = node('div', undefined, 'actions'), download = node('a', '下载此版本单 ZIP', 'button-link');
        download.href = '/api/admin/routes/' + encodeURIComponent(pkg.id) + '/download'; download.setAttribute('download', 'routes-' + todayStamp() + '-' + pkg.id.slice(0, 8) + '.zip'); actions.append(download);
        if (pkg.state === 'draft') actions.append(actionButton('发布此草稿', 'publish', pkg, 'primary'), actionButton('删除未发布草稿', 'delete', pkg, 'danger'));
        else if (state.catalog?.current?.package?.id !== pkg.id) actions.append(actionButton('回滚到此版本', 'rollback', pkg, 'primary'), actionButton('删除此版本', 'delete', pkg, 'danger'));
        detail.append(actions);
        const isCurrent = state.catalog?.current?.package?.id === pkg.id;
        if (!isCurrent) {
            const added = (data.added || []).length, modified = (data.modified || []).length, removed = (data.removed || []).length;
            const summaryLine = node('p', undefined, 'diff-summary');
            summaryLine.append(node('span', `新增 ${added}`, 'diff-add'), node('span', `修改 ${modified}`, 'diff-mod'), node('span', `移除 ${removed}`, 'diff-del'),
                node('span', '（相对当前发布版）', 'muted'));
            detail.append(node('h4', '和当前发布版的区别'), summaryLine);
            for (const [field, name] of [['added', '新增'], ['modified', '修改'], ['removed', '移除']]) {
                const paths = data[field] || []; if (!paths.length) continue;
                const group = node('details'), summary = node('summary', `${name} ${paths.length} 个文件`), list = node('ul', undefined, 'diff-list');
                for (const path of paths) list.append(node('li', path));
                group.append(summary, list); detail.append(group);
            }
            detail.append(node('p', '发布会完整替换官方线路，被移除的文件不会保留。', 'help'));
        }
        detailFiles(pkg, detail);
        if (pkg.state !== 'draft') restorePanel(pkg, detail);
    }
    async function loadDetail(id) {
        if (!validId(id)) { status('globalStatus', '版本 ID 格式无效。', true); return; }
        const sequence = ++state.detailSequence, revision = C.publicationRevision(state.catalog);
        state.selectedId = id; state.detail = null; state.detailRevision = null; state.selectedPaths.clear(); renderCatalog();
        $('packageDetail').setAttribute('aria-busy', 'true'); $('packageDetail').replaceChildren(node('p', '正在读取服务端校验结果…', 'muted'));
        try {
            const data = await request('/api/admin/routes/' + encodeURIComponent(id));
            if (sequence !== state.detailSequence) return;
            if (data?.package?.id !== id || !Array.isArray(data.package.files)) throw new Error('服务端未返回有效的版本文件清单。');
            if (!state.catalogFresh || revision !== C.publicationRevision(state.catalog)) throw new Error('读取期间发布清单发生变化，请刷新清单后重选版本。');
            state.detail = data; state.detailRevision = revision; renderDetail(data);
        } catch (error) { if (sequence === state.detailSequence) $('packageDetail').replaceChildren(node('p', error.message, 'error')); }
        finally { if (sequence === state.detailSequence) $('packageDetail').setAttribute('aria-busy', 'false'); }
    }
    function openAction(kind, pkg) {
        try {
            if (state.mutationBusy) return;
            if (!state.catalogFresh || !state.catalog) throw new Error('请先成功刷新线路清单，再检查版本差异。');
            if (state.detail?.package?.id !== pkg.id || state.detailRevision !== C.publicationRevision(state.catalog))
                throw new Error('版本详情已过期，请重新选择版本并检查差异。');
            const names = { publish: '发布此草稿', rollback: '回滚到此版本', delete: pkg.state === 'draft' ? '删除未发布草稿' : '删除此版本', restore: '恢复选定线路到新草稿' };
            const action = { kind, pkg, revision: C.publicationRevision(state.catalog) };
            if (kind === 'restore') {
                action.destination = $('restoreDestination').value;
                if (!state.catalog.packages.some(item => item.id === action.destination && item.state === 'draft')) throw new Error('请选择有效目标草稿。');
                action.paths = C.restoreSelection(pkg, [...state.selectedPaths]);
            }
            state.action = action; $('actionTitle').textContent = names[kind]; $('actionConfirm').textContent = names[kind];
            const deleting = kind === 'delete';
            $('actionSummary').textContent = `${packageName(pkg)}。` + (deleting ? (pkg.state === 'draft' ? '只删除未发布的草稿，此操作不可撤销。' : '删除后无法再回滚到这个版本，此操作不可撤销。当前发布版本不能删除。') + '服务端记录固定删除原因。' :
                (kind === 'restore' ? `将 ${action.paths.length} 个文件 / 目录前缀恢复到草稿 ${shortId(action.destination)} 的新副本，不直接发布；目录选择会替换该目录的内容。` : '将完整替换当前官方线路；进行中的轮次仍使用已锁定版本。') + ` 当前修订 ${action.revision}，服务端会核对是否已被其他操作改变。`);
            // 发布是日常操作，默认原因为“日常更新”，有特殊情况再改；回滚、恢复仍需手填，避免默认文案掩盖异常操作。
            $('actionReason').value = kind === 'publish' ? '日常更新' : ''; $('actionReason').hidden = deleting; $('actionReason').disabled = deleting;
            $('actionReason').required = !deleting; $('actionReasonLabel').hidden = deleting;
            $('actionError').textContent = ''; $('actionDialog').showModal(); (deleting ? $('actionCancel') : $('actionReason')).focus();
        } catch (error) { status('globalStatus', error.message, true); }
    }
    $('actionCancel').addEventListener('click', () => { if (!state.mutationBusy) { $('actionDialog').close(); state.action = null; } });
    $('actionDialog').addEventListener('cancel', event => { if (state.mutationBusy) event.preventDefault(); else state.action = null; });
    $('actionForm').addEventListener('submit', async event => {
        event.preventDefault(); if (!state.action || state.mutationBusy || $('actionConfirm').dataset.blocked === 'true') return;
        const action = state.action;
        let command;
        try { command = C.buildActionRequest(action, $('actionReason').value); }
        catch (error) { status('actionError', error.message, true); return; }
        state.mutationBusy = true; $('actionConfirm').disabled = true; $('actionCancel').disabled = true;
        let attempted = false;
        try {
            const headers = { 'X-CSRF-Token': await csrf() };
            if (command.body) headers['Content-Type'] = 'application/json';
            attempted = true;
            const result = await request(command.path, { method: command.method, headers, body: command.body, timeout: 120000 });
            if (action.kind === 'restore' && (!validId(result?.id) || result.state !== 'draft')) throw new Error('服务端未返回有效新草稿，写入结果未确认。');
            if (['publish', 'rollback'].includes(action.kind) && (result?.package?.id !== action.pkg.id || !Number.isSafeInteger(result?.revision) || result.revision <= action.revision))
                throw new Error('服务端未返回有效发布修订，写入结果未确认。');
            $('actionDialog').close(); state.action = null;
            const doneMessage = action.kind === 'restore' ? '已生成恢复后的新草稿，尚未发布。' : action.kind === 'delete' ? '已删除：' + (action.pkg.description || shortId(action.pkg.id)) : action.kind === 'rollback' ? '已回滚到：' + (action.pkg.description || shortId(action.pkg.id)) : '已发布：' + (action.pkg.description || shortId(action.pkg.id));
            status('globalStatus', doneMessage); toast(doneMessage);
            const nextId = action.kind === 'restore' ? result?.id : action.kind === 'delete' ? null : action.pkg.id;
            state.selectedId = nextId;
            await loadCatalog();
            if (nextId) await loadDetail(nextId); else { state.detail = null; state.detailSequence++; $('packageDetail').replaceChildren(node('p', '已删除，请选择其他版本。', 'muted')); }
            await loadAudit();
        } catch (error) {
            const unknown = attempted && (!error.status || error.status >= 500);
            status('actionError', error.message + (unknown ? ' 写入结果未确认，请关闭对话框并刷新清单/审计核对，不要直接重试。' : ''), true);
            if (unknown || error.status === 409) { state.catalogFresh = false; $('actionConfirm').dataset.blocked = 'true'; }
        } finally {
            state.mutationBusy = false;
            $('actionConfirm').disabled = $('actionConfirm').dataset.blocked === 'true'; $('actionCancel').disabled = false;
        }
    });
    // Reset the uncertain-operation latch only after explicitly opening a new confirmation against a fresh catalog.
    $('actionDialog').addEventListener('close', () => { delete $('actionConfirm').dataset.blocked; $('actionConfirm').disabled = false; });
    async function loadAudit() {
        $('refreshAudit').disabled = true;
        try {
            const entries = await request('/api/admin/routes/audit');
            if (!Array.isArray(entries)) throw new Error('服务端返回的审计格式无效（应为记录数组）。');
            const list = $('auditList'); list.replaceChildren();
            const actions = { upload: '上传草稿', publish: '发布版本', rollback: '回滚版本', restore: '恢复为新草稿', delete: '删除未发布草稿' };
            for (const entry of entries) {
                const li = node('li'), time = node('time', date(entry.atUtc));
                if (entry.atUtc) time.dateTime = entry.atUtc;
                li.append(time, node('strong', actions[entry.action] || String(entry.action || '未知操作')),
                    node('p', `包 ID：${entry.packageId || '未记录'} · 修订：${entry.revision ?? '未记录'}`),
                    node('p', '操作者：' + (entry.actor || '未记录')), node('p', '原因：' + (entry.reason || '未填写'))); list.append(li);
            }
            if (!entries.length) list.append(node('li', '尚无线路操作记录。', 'muted'));
            status('auditStatus', '已读取服务器保留的有界操作历史。');
        } catch (error) { status('auditStatus', error.message, true); }
        finally { $('refreshAudit').disabled = false; }
    }
    $('packageFilter').addEventListener('change', renderCatalog);
    // 标签式筛选：点标签只是替换隐藏下拉框的值再重绘，保持原有“全部 / 草稿 / 已发布”语义。
    for (const tab of document.querySelectorAll?.('.filter-tabs .tab') || [])
        tab.addEventListener('click', () => { $('packageFilter').value = tab.dataset.filter; renderCatalog(); });
    $('refreshCatalog').addEventListener('click', async () => { const id = state.selectedId; if (await loadCatalog() && id && state.catalog.packages.some(pkg => pkg.id === id)) await loadDetail(id); });
    $('refreshAudit').addEventListener('click', loadAudit);
    updateUploadControls();
    void loadCatalog(); void loadAudit();
})();
