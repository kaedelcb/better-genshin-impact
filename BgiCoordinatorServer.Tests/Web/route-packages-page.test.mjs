import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

// UI-only unit harness: production event handlers, minimal DOM and deferred transport.
// Stubbed responses are NOT evidence of backend authorization, validation or persistence.
// No fake API server is started; integration preview must use the real ASP.NET app.
const core = readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/route-packages-core.js', import.meta.url), 'utf8');
const page = readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/route-packages-page.js', import.meta.url), 'utf8');
const id = 'a'.repeat(32), historyId = 'b'.repeat(32);
const draft = { id, state: 'draft', description: '新草稿', createdAtUtc: '2026-11-17T00:00:00Z',
    fileCount: 1, routeCount: 1, sizeBytes: 100, sha256: 'c'.repeat(64), files: [{ path: 'builtin/甲/一.json', sizeBytes: 10, sha256: 'd'.repeat(64) }] };
const historical = { ...draft, id: historyId, state: 'archived', description: '历史线路', files: [
    { path: 'builtin/甲/一.json', sizeBytes: 10 }, { path: 'builtin/甲/control.json5', sizeBytes: 20 },
    { path: 'builtin/甲/A变体/二_a.json', sizeBytes: 30 }, { path: 'builtin/乙/三.json', sizeBytes: 10 }
] };
const catalog = (revision = 4, packages = [draft]) => ({ packages, current: { revision, package: historical },
    requiredClientVersion: '0.58.0.0', storageBytes: 100, maxStorageBytes: 10000 });
const detail = pkg => ({ package: pkg, added: pkg.files.map(file => file.path), modified: [], removed: [] });
const settle = async () => { for (let i = 0; i < 15; i++) await Promise.resolve(); };

function harness(mode = 'uploader', protocol = 'https:') {
    const elements = new Map(), requests = [], workers = [], events = new Map(), timers = new Map();
    let nextTimer = 0;
    class Element {
        constructor(tag = 'div') {
            this.tagName = tag.toUpperCase(); this.children = []; this.listeners = new Map(); this.attributes = new Map();
            this.dataset = {}; this.value = ''; this._text = ''; this.hidden = false; this.disabled = false;
            this.checked = false; this.required = false; this.files = []; this.open = false;
            this.classList = { toggle() {} };
        }
        set id(value) { this._id = value; elements.set(value, this); }
        get id() { return this._id; }
        set textContent(value) { this._text = String(value); this.children = []; }
        get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
        append(...children) { this.children.push(...children); }
        replaceChildren(...children) { this._text = ''; this.children = [...children]; }
        setAttribute(name, value) { this.attributes.set(name, value); }
        removeAttribute(name) { this.attributes.delete(name); }
        addEventListener(type, fn) { if (!this.listeners.has(type)) this.listeners.set(type, []); this.listeners.get(type).push(fn); }
        dispatch(type) {
            if (type === 'click' && this.disabled) return Promise.resolve();
            const event = { target: this, preventDefault() {} };
            return Promise.all((this.listeners.get(type) || []).map(fn => fn(event)));
        }
        focus() { this.focused = true; }
        showModal() { this.open = true; }
        close() { this.open = false; void this.dispatch('close'); }
    }
    const element = id => {
        if (!elements.has(id)) { const e = new Element(); e.id = id; }
        return elements.get(id);
    };
    element('packageFilter').value = 'all';
    const document = { body: { dataset: { mode } }, getElementById: element, createElement: tag => new Element(tag) };
    Object.defineProperty(document, 'cookie', { get() { assert.fail('route pages must not read document.cookie'); } });
    const scope = vm.createContext({
        TextEncoder, Blob, DOMException, AbortController, document,
        location: { protocol, hostname: 'routes.example.test' },
        window: { addEventListener(type, fn) { events.set(type, fn); } },
        Option: class extends Element { constructor(text, value) { super('option'); this.textContent = text; this.value = value; } },
        Worker: class {
            constructor(url) { this.url = url; this.terminated = false; workers.push(this); }
            postMessage(data) { this.posted = data; }
            terminate() { this.terminated = true; }
        },
        setTimeout(fn) { const timer = ++nextTimer; timers.set(timer, fn); return timer; },
        clearTimeout(timer) { timers.delete(timer); },
        fetch(url, options) {
            return new Promise((resolve, reject) => {
                const record = { url, options, resolve, reject }; requests.push(record);
                options.signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')), { once: true });
            });
        }
    });
    for (const name of ['localStorage', 'sessionStorage']) Object.defineProperty(scope, name, { get() { assert.fail('upload tokens must never use browser storage'); } });
    vm.runInContext(core, scope, { filename: 'route-packages-core.js' });
    vm.runInContext(page, scope, { filename: 'route-packages-page.js' });
    return {
        element, requests, workers, events, timers,
        reply(index, data, status = 200) {
            assert.ok(requests[index], 'expected request ' + index);
            requests[index].resolve({ ok: status >= 200 && status < 300, status,
                text: async () => status === 204 ? '' : JSON.stringify(data), json: async () => data });
        }
    };
}

function find(root, predicate) {
    if (predicate(root)) return root;
    for (const child of root.children) { const result = find(child, predicate); if (result) return result; }
    return undefined;
}
const button = (h, text, root = 'packageDetail') => {
    const result = find(h.element(root), e => e.tagName === 'BUTTON' && e.textContent === text);
    assert.ok(result, 'expected visible button: ' + text); return result;
};
async function chooseAndPack(h) {
    const file = Object.defineProperty(new Blob(['{"positions":[{"x":1,"y":2}]}']), 'webkitRelativePath', { value: '中文线路/一.json' });
    h.element('folderInput').files = [file]; await h.element('folderInput').dispatch('change');
    assert.equal(h.element('packButton').disabled, true, 'privacy confirmation is required');
    h.element('privacyConfirm').checked = true; await h.element('privacyConfirm').dispatch('change');
    assert.equal(h.element('packButton').disabled, false);
    await h.element('packButton').dispatch('click');
    return h.workers.at(-1);
}
function finishPack(worker) { worker.onmessage({ data: { type: 'complete', blob: new Blob(['zip-test-fixture']) } }); }
async function adminReady(packages = [draft]) {
    const h = harness('admin');
    assert.deepEqual(h.requests.map(request => request.url), ['/api/admin/routes', '/api/admin/routes/audit']);
    h.reply(0, catalog(4, packages)); h.reply(1, []); await settle();
    return h;
}
async function selectPackage(h, pkg, index) {
    const item = find(h.element('packageList'), e => e.tagName === 'BUTTON' && e.textContent.includes(pkg.description));
    assert.ok(item); const loading = item.dispatch('click');
    h.reply(index, detail(pkg)); await loading;
}

test('uploader has no admin reads, omits cookie credentials and clears its memory-only token on success', async () => {
    const h = harness(); assert.equal(h.requests.length, 0);
    const worker = await chooseAndPack(h);
    assert.equal(worker.url, '/route-packages-worker.js?v=20261117');
    assert.equal(worker.posted.files[0].relativePath, '中文线路/一.json');
    finishPack(worker);
    assert.equal(h.element('uploadButton').disabled, true, 'upload-only token is required');
    h.element('uploadToken').value = 'only-upload'; await h.element('uploadToken').dispatch('input');
    h.element('uploadDescription').value = '线路更新';
    const uploading = h.element('uploadButton').dispatch('click'); await settle();
    assert.equal(h.requests.length, 1);
    const request = h.requests[0];
    assert.equal(request.options.credentials, 'omit');
    assert.equal(request.options.headers['X-Route-Upload-Token'], 'only-upload');
    assert.equal(request.options.headers['X-CSRF-Token'], undefined);
    assert.equal(request.options.headers['Content-Type'], 'application/zip');
    assert.equal(request.options.redirect, 'error');
    assert.ok(request.options.body instanceof Blob);
    assert.ok(request.url.startsWith('/api/admin/routes/upload?description='));
    h.reply(0, draft); await uploading;
    assert.equal(h.requests.length, 1, 'uploader must not fetch catalog/audit/CSRF after upload');
    assert.equal(h.element('uploadToken').value, '');
    assert.equal(h.element('uploadButton').disabled, true);
    assert.match(h.element('uploadResult').textContent, /未发布/);
});

test('worker cancellation ignores a late completion and never sends a request', async () => {
    const h = harness(), worker = await chooseAndPack(h);
    await h.element('cancelUpload').dispatch('click');
    assert.equal(worker.terminated, true);
    finishPack(worker);
    assert.equal(h.element('uploadButton').disabled, true);
    assert.equal(h.element('packButton').disabled, false);
    assert.match(h.element('uploadStatus').textContent, /取消/);
    assert.equal(h.requests.length, 0);
});

test('pagehide cancels packing and clears the token without leaving a restored page stuck busy', async () => {
    const h = harness(), worker = await chooseAndPack(h);
    h.element('uploadToken').value = 'only-upload';
    h.events.get('pagehide')(); finishPack(worker);
    assert.equal(worker.terminated, true);
    assert.equal(h.element('uploadToken').value, '');
    assert.equal(h.element('folderInput').disabled, false);
    assert.equal(h.element('packButton').disabled, false);
    assert.equal(h.element('uploadButton').disabled, true);
    assert.equal(h.requests.length, 0);
});

test('aborting an attempted upload latches unknown instead of claiming rollback or silently retrying', async () => {
    const h = harness(), worker = await chooseAndPack(h); finishPack(worker);
    h.element('uploadToken').value = 'only-upload'; await h.element('uploadToken').dispatch('input');
    h.element('uploadDescription').value = '线路更新';
    const uploading = h.element('uploadButton').dispatch('click'); await settle();
    await h.element('cancelUpload').dispatch('click'); await uploading;
    assert.equal(h.requests[0].options.signal.aborted, true);
    assert.match(h.element('uploadStatus').textContent, /结果未确认/);
    assert.equal(h.element('uploadButton').disabled, true);
    assert.equal(h.element('packButton').disabled, true);
    await h.element('uploadButton').dispatch('click');
    assert.equal(h.requests.length, 1);
    h.events.get('pagehide')();
    assert.equal(h.element('uploadToken').value, '');
});

test('plain HTTP outside loopback fails before transport', async () => {
    const h = harness('uploader', 'http:'), worker = await chooseAndPack(h); finishPack(worker);
    h.element('uploadToken').value = 'only-upload'; await h.element('uploadToken').dispatch('input');
    h.element('uploadDescription').value = '线路更新';
    await h.element('uploadButton').dispatch('click');
    assert.equal(h.requests.length, 0);
    assert.match(h.element('uploadStatus').textContent, /HTTPS/);
});

test('admin DELETE hides the unused reason, fetches CSRF and sends no body', async () => {
    const h = await adminReady(); await selectPackage(h, draft, 2);
    await button(h, '删除未发布草稿').dispatch('click');
    assert.equal(h.element('actionDialog').open, true);
    assert.equal(h.element('actionReason').hidden, true);
    assert.equal(h.element('actionReason').disabled, true);
    assert.equal(h.element('actionReason').required, false);
    assert.equal(h.element('actionReasonLabel').hidden, true);
    assert.doesNotMatch(h.element('actionSummary').textContent, /服务端会核对是否已被其他操作改变/);
    const deleting = h.element('actionForm').dispatch('submit'); await settle();
    assert.equal(h.requests[3].url, '/api/admin/routes/csrf');
    h.reply(3, { token: 'csrf-test' }); await settle();
    assert.equal(h.requests[4].options.method, 'DELETE');
    assert.equal(h.requests[4].options.credentials, 'same-origin');
    assert.equal(h.requests[4].options.headers['X-CSRF-Token'], 'csrf-test');
    assert.equal(h.requests[4].options.body, undefined);
    h.reply(4, null, 204); await settle();
    h.reply(5, catalog(4, [])); await settle(); h.reply(6, []); await deleting;
    assert.equal(h.element('actionDialog').open, false);
    assert.match(h.element('packageDetail').textContent, /已删除/);
});

test('publication conflict blocks resubmission until catalog is explicitly refreshed', async () => {
    const h = await adminReady(); await selectPackage(h, draft, 2);
    await button(h, '发布此草稿').dispatch('click');
    assert.equal(h.element('actionReason').hidden, false);
    assert.equal(h.element('actionReason').required, true);
    h.element('actionReason').value = '修复并发布';
    const publishing = h.element('actionForm').dispatch('submit'); await settle();
    h.reply(3, { token: 'csrf-test' }); await settle();
    assert.deepEqual(JSON.parse(h.requests[4].options.body), { expectedRevision: 4, reason: '修复并发布' });
    h.reply(4, { error: '另一个管理员已发布版本' }, 409); await publishing;
    assert.match(h.element('actionError').textContent, /修订冲突/);
    assert.equal(h.element('actionConfirm').disabled, true);
    await h.element('actionForm').dispatch('submit');
    assert.equal(h.requests.length, 5, 'must not auto-retry a non-idempotent operation');
});

test('historical directory checkboxes send compact prefixes and surface server dependency errors', async () => {
    const h = await adminReady([historical, draft]); await selectPackage(h, historical, 2);
    h.element('restoreDestination').value = id;
    const directoryLabel = find(h.element('packageDetail'), e => e.tagName === 'LABEL' && e.textContent.startsWith('builtin/甲/（目录'));
    assert.ok(directoryLabel, 'restore UI must expose directories, not only individual files');
    directoryLabel.children[0].checked = true; await directoryLabel.children[0].dispatch('change');
    const childLabel = find(h.element('packageDetail'), e => e.tagName === 'LABEL' && e.textContent === 'builtin/甲/一.json');
    assert.equal(childLabel.children[0].checked, true);
    assert.equal(childLabel.children[0].disabled, true, 'parent selection owns its descendants');
    await button(h, '恢复选定线路到新草稿').dispatch('click');
    h.element('actionReason').value = '恢复完整目录';
    const restoring = h.element('actionForm').dispatch('submit'); await settle();
    h.reply(3, { token: 'csrf-test' }); await settle();
    const request = h.requests[4];
    assert.equal(request.url, '/api/admin/routes/' + id + '/restore');
    assert.deepEqual(JSON.parse(request.options.body), { expectedRevision: 4, reason: '恢复完整目录', sourceId: historyId, paths: ['builtin/甲'] });
    h.reply(4, { error: 'control ref 目标不存在，请同时恢复依赖目录' }, 400); await restoring;
    assert.match(h.element('actionError').textContent, /control ref 目标不存在/);
    assert.equal(h.element('actionDialog').open, true);
    assert.equal(h.requests.length, 5, 'validation failure must not pretend to create/publish a new draft');
});

test('audit consumes the real bare-array field names and renders text, not untrusted markup', async () => {
    const h = harness('admin'); h.reply(0, catalog());
    h.reply(1, [{ atUtc: '2026-11-17T00:00:00Z', action: 'restore', packageId: id, actor: 'admin:<script>not-markup</script>', reason: '恢复中文线路', revision: 9 }]);
    await settle();
    const text = h.element('auditList').textContent;
    assert.match(text, /恢复为新草稿/); assert.ok(text.includes(id));
    assert.match(text, /admin:<script>not-markup<\/script>/);
    assert.match(text, /恢复中文线路/); assert.match(text, /修订：9/);
    assert.equal(find(h.element('auditList'), e => e.tagName === 'SCRIPT'), undefined);
    const refreshing = h.element('refreshAudit').dispatch('click');
    h.reply(2, { entries: [] }); await refreshing;
    assert.match(h.element('auditStatus').textContent, /应为记录数组/);
});

test('a catalog revision change makes old detail responses unusable for publication', async () => {
    const h = await adminReady();
    const item = h.element('packageList').children[0], oldDetail = item.dispatch('click');
    const refreshing = h.element('refreshCatalog').dispatch('click');
    h.reply(3, catalog(5)); await settle();
    assert.equal(h.requests[4].url, '/api/admin/routes/' + id);
    h.reply(2, detail(draft)); await oldDetail;
    assert.equal(find(h.element('packageDetail'), e => e.tagName === 'BUTTON' && e.textContent === '发布此草稿'), undefined);
    h.reply(4, detail(draft)); await refreshing;
    await button(h, '发布此草稿').dispatch('click');
    assert.match(h.element('actionSummary').textContent, /当前修订 5/);
});
