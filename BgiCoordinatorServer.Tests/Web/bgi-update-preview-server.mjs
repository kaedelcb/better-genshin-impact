// Source-only isolated browser fixture. Start explicitly from the main session
// when needed; this file is never imported by the regression runner.
// Fixed loopback port: http://127.0.0.1:8947/ . No gateway, update, or User I/O.
import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';

const require = createRequire(import.meta.url);
// require.main guard in the CJS module prevents node:test registration here.
const { createUpdateTransport, createFakeClock } = require('./bgi-update.test.cjs');

function installBrowserFixture(createTransport, createClock) {
    const transport = createTransport(), clock = createClock(), confirmations = [];
    const operations = ['catalog', 'start', 'status', 'cancel'];
    const outcomes = ['ok', 'rejected', 'denied', 'unknown', 'error', 'unconfirmed'];
    const fullOperation = operation => operation.startsWith('bgi.update.') ? operation : 'bgi.update.' + operation;
    const field = name => document.querySelector('#bgiUpdateDialog [data-' + name + ']');
    const targetKey = value => {
        const target = transport.targets[Number.isInteger(value) ? value : transport.targets.findIndex(item => item.key === value)];
        if (!target) throw new Error('隔离目标无效：' + value);
        return target.key;
    };
    const escape = value => String(value ?? '').replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char]));
    let panel = null;
    const flush = async () => { for (let index = 0; index < 16; index++) await Promise.resolve(); };
    function render() {
        const trace = document.getElementById('fixture-trace');
        const pending = transport.snapshot().filter(call => !call.settled);
        const state = {
            fakeMilliseconds: clock.now(), fakeTimers: clock.pending(),
            currentTarget: field('target')?.value || null,
            pending: pending.map(call => ({ number: call.number, operation: call.operation, target: call.target.key })),
            rememberedTasks: Object.fromEntries(transport.targets.map(target => [target.key, sessionStorage.getItem('bgi.update.pending.' + target.instanceId)])),
            confirmations, calls: transport.snapshot()
        };
        if (trace) trace.textContent = JSON.stringify(state, null, 2);
        const compact = document.getElementById('fixture-live');
        if (compact) compact.textContent = '模拟时间 ' + clock.now() + ' ms · 等待回复 ' + pending.length + ' · 已发起更新 ' +
            transport.calls.filter(call => call.operation === 'bgi.update.start').length;
    }
    function report(action) {
        return async () => {
            try {
                await action();
                const host = document.getElementById('fixture-error') || document.getElementById('fixture-page-error');
                if (host) host.textContent = '';
            } catch (error) {
                const host = document.getElementById('fixture-error') || document.getElementById('fixture-page-error');
                if (host) host.textContent = error.message;
            }
            await flush(); render();
        };
    }
    function attachPanel() {
        const dialog = document.getElementById('bgiUpdateDialog');
        if (!dialog || dialog.querySelector('[data-fixture-panel]')) return;
        // Native modal dialogs make the outside page inert. Put fixture controls
        // inside the top layer so preview_click can operate them while it is open.
        panel = document.createElement('details'); panel.dataset.fixturePanel = ''; panel.open = true;
        panel.className = 'fixture-tools';
        panel.innerHTML = '<summary><strong>隔离夹具控制台（非产品 UI）</strong></summary>' +
            '<p>四个虚构授权成员；请求纯内存模拟。确认框自动同意并记入记录。轮询使用手动时钟，不连接真实 gateway、不执行更新。</p>' +
            '<div class="fixture-row"><label>模拟目标<select id="fixture-target">' + transport.targets.map(target => '<option value="' + target.key + '">' + escape(target.name) + '</option>').join('') + '</select></label>' +
            '<label>请求<select id="fixture-operation">' + operations.map(operation => '<option value="' + operation + '">' + operation + '</option>').join('') + '</select></label>' +
            '<label>返回结果<select id="fixture-outcome">' + outcomes.map(outcome => '<option value="' + outcome + '">' + outcome + '</option>').join('') + '</select></label></div>' +
            '<div class="fixture-buttons"><button type="button" id="fixture-hold">暂停该类回复</button><button type="button" id="fixture-unhold">后续自动回复</button>' +
            '<button type="button" id="fixture-release">放行最早等待回复</button><button type="button" id="fixture-next">设置下次结果</button>' +
            '<button type="button" id="fixture-tick">推进轮询 1700ms</button><button type="button" id="fixture-complete">目标任务模拟完成</button>' +
            '<button type="button" id="fixture-start-four">四人各发起一次</button><button type="button" id="fixture-reopen">关闭重开（保留任务）</button></div>' +
            '<p id="fixture-live" role="status"></p><p id="fixture-error" role="alert"></p>' +
            '<details><summary>请求目标、参数与 sessionStorage 记录</summary><pre id="fixture-trace"></pre></details>';
        dialog.append(panel);
        const control = id => panel.querySelector('#fixture-' + id);
        const values = () => [control('operation').value, control('target').value, control('outcome').value];
        control('hold').onclick = report(() => { const [operation, target] = values(); fixture.hold(operation, target); });
        control('unhold').onclick = report(() => { const [operation, target] = values(); fixture.hold(operation, target, false); });
        control('release').onclick = report(() => { const [operation, target, outcome] = values(); fixture.release(operation, target, outcome); });
        control('next').onclick = report(() => { const [operation, target, outcome] = values(); fixture.next(operation, target, outcome); });
        control('tick').onclick = report(() => fixture.tick());
        control('complete').onclick = report(() => fixture.complete(control('target').value));
        control('start-four').onclick = report(() => fixture.startFour());
        control('reopen').onclick = report(() => fixture.reopen());
        dialog.addEventListener('change', () => { void flush().then(render); });
        dialog.addEventListener('click', () => { void flush().then(render); });
        render();
    }
    Object.assign(window, {
        roomCode: 'FIXTURE_ONLY', mtTargets: () => transport.targets,
        mtCredentialFor: target => ({ browserId: 'fixture-browser-' + target.key, secret: 'fixture-no-real-secret' }),
        mtTargetReady: () => true, mtRead: transport.read, mtSend: transport.send,
        escapeHtml: escape, scheduleMemberCardAlignment() {},
        setTimeout: clock.setTimeout, clearTimeout: clock.clearTimeout,
        confirm: message => { confirmations.push(message); render(); return true; }
    });
    const fixture = window.fixture = {
        transport, clock, confirmations,
        get calls() { return transport.snapshot(); },
        async open(index = 0) {
            const target = transport.targets[index];
            if (!target) throw new Error('隔离成员编号必须为 0–3');
            window.bgiWebUpdate.open({ playerUid: target.uid, clientInstanceId: target.instanceId });
            await flush(); attachPanel(); render();
        },
        async select(index) {
            const key = targetKey(index); if (!key) throw new Error('隔离目标无效');
            const box = field('target'); if (!box) throw new Error('请先打开更新弹窗');
            box.value = key; box.dispatchEvent(new Event('change', { bubbles: true })); await flush(); render();
        },
        async choose(index = 0) {
            const box = field('package'); if (!box || box.disabled || !box.options[index]) throw new Error('当前成员目录未就绪或已有待确认任务');
            box.value = box.options[index].value; box.dispatchEvent(new Event('change', { bubbles: true })); await flush(); render();
        },
        async closeBgi(value) {
            const box = field('close-bgi'); if (!box || box.disabled) throw new Error('当前 closeBgi 选项已锁定');
            box.checked = !!value; box.dispatchEvent(new Event('change', { bubbles: true })); await flush(); render();
        },
        async action(name) {
            const button = field(name);
            if (!button || button.disabled || button.hidden) throw new Error('当前按钮不可用：' + name);
            button.click(); await flush(); render();
        },
        hold(operation, target, enabled = true) { transport.hold(fullOperation(operation), targetKey(target), enabled); render(); },
        next(operation, target, outcome) { transport.queue(fullOperation(operation), targetKey(target), outcome); render(); },
        release(operation, target, outcome = 'ok') {
            const call = transport.pending(fullOperation(operation), targetKey(target))[0];
            if (!call) throw new Error('该成员该类请求没有等待回复');
            transport.release(call, outcome); render(); return call.number;
        },
        releaseNumber(number, outcome = 'ok') { transport.release(number, outcome); render(); },
        async tick(milliseconds = 1700) { clock.advance(milliseconds); await flush(); render(); },
        complete(target, message = '隔离模拟完成') { transport.complete(targetKey(target), message); render(); },
        async startFour() {
            for (let index = 0; index < 4; index++) {
                await fixture.select(index); await fixture.choose(index % 2); await fixture.closeBgi(index % 2 === 0);
                await fixture.action('install');
            }
            render();
        },
        async reopen() {
            const dialog = document.getElementById('bgiUpdateDialog');
            const current = field('target')?.value, index = transport.targets.findIndex(target => target.key === current);
            if (dialog) {
                dialog.close();
                // 隐藏的预览页可能暂停原生 close 事件；夹具显式派发，先运行产品清理再重开。
                if (dialog.isConnected) dialog.dispatchEvent(new Event('close'));
            }
            await fixture.open(Math.max(0, index));
        },
        async reset() {
            document.getElementById('bgiUpdateDialog')?.close();
            // This origin belongs solely to the isolated fixture. Never clear all storage.
            for (const target of transport.targets) sessionStorage.removeItem('bgi.update.pending.' + target.instanceId);
            location.reload();
        },
        async flush() { await flush(); render(); }, render
    };
    transport.onChange(render);
    document.getElementById('fixture-open').onclick = report(() => fixture.open());
    document.getElementById('fixture-reset').onclick = report(() => fixture.reset());
    window.addEventListener('error', event => { const host = document.getElementById('fixture-page-error'); if (host) host.textContent = event.message; });
}

export function fixturePage() {
    return `<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>BGI 更新 · 隔离浏览器夹具</title><link rel="stylesheet" href="/control-room.css">
<style>
.fixture-page{max-width:960px;margin:32px auto;padding:0 20px}.fixture-banner,.fixture-tools{border:2px solid #e8c96d;background:#302e21;color:#fff2c4;padding:12px;border-radius:10px}
.fixture-tools{margin-top:20px;font-size:12px}.fixture-tools p{color:#fff2c4}.fixture-row{display:flex;gap:8px;flex-wrap:wrap}.fixture-row label{flex:1;min-width:110px}
.fixture-tools .fixture-row select{min-height:32px;margin:3px 0}.fixture-buttons{display:flex;gap:6px;flex-wrap:wrap}.fixture-tools button{font-size:12px;padding:5px 8px;min-height:32px}
.fixture-tools pre{max-height:220px;overflow:auto;white-space:pre-wrap;overflow-wrap:anywhere}.fixture-tools summary{cursor:pointer}#fixture-error,#fixture-page-error{color:#ffb0a0}
</style></head><body><main class="fixture-page" id="homeView">
<div class="fixture-banner"><h1>隔离夹具 · 虚构四人 BGI 更新</h1>
<p>只服务真实 bgi-update-web.js、control-room.css 和本页面。所有执行端与安装包均为内存模拟；不连接真实 gateway、不执行更新、不接触 User。</p></div>
<p>产品弹窗使用真实样式。可用实际执行端选择框、安装包、自动关闭 BGI、更新、取消与解除待确认；确认框在夹具内自动同意。轮询暂停在手动时钟，点击控制台“推进轮询”。</p>
<p><button type="button" id="fixture-open" class="btn btn-primary">打开更新弹窗</button> <button type="button" id="fixture-reset" class="btn btn-secondary">重置隔离任务并刷新</button></p>
<p>迟到响应示例：选控制台目标甲和请求 start → 暂停回复 → 在产品弹窗发起甲更新 → 切乙并发起更新 → 放行甲回复 → 推进轮询。目录、status、cancel 同样可暂停。</p>
<p>自动化入口：<code>fixture.select(0..3)</code>、<code>fixture.choose(0..1)</code>、<code>fixture.closeBgi(false)</code>、<code>fixture.action('install')</code>、<code>fixture.hold('start',0)</code>、<code>fixture.release('start',0,'unknown')</code>、<code>fixture.tick()</code>、<code>fixture.reopen()</code>；<code>fixture.calls</code> 包含全部目标和参数。<code>fixture.startFour()</code> 仅适合无已有任务且目录自动回复的初始页面。</p>
<p>重新加载页面保留本 origin 的 sessionStorage，但内存执行端任务消失，原任务会显示待确认且不会重发；关闭重开保留内存执行端供恢复查询。</p><p id="fixture-page-error" role="alert"></p>
</main><script>(${installBrowserFixture.toString()})(${createUpdateTransport.toString()},${createFakeClock.toString()});</script>
<script src="/bgi-update-web.js"></script><script>fixture.open().catch(error => { document.getElementById('fixture-page-error').textContent = error.message; });</script>
</body></html>`;
}

export function createPreviewServer() {
    // Exact allowlist: no static directory traversal, proxy, API endpoint or writes.
    const assets = new Map([
        ['/bgi-update-web.js', ['bgi-update-web.js', 'text/javascript; charset=utf-8']],
        ['/control-room.css', ['control-room.css', 'text/css; charset=utf-8']]
    ]);
    return http.createServer(async (request, response) => {
        response.setHeader('Cache-Control', 'no-store');
        response.setHeader('X-Content-Type-Options', 'nosniff');
        response.setHeader('Content-Security-Policy', "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'none'; img-src 'none'; font-src 'none'; frame-src 'none'; form-action 'none'; base-uri 'none'");
        if (request.method !== 'GET' && request.method !== 'HEAD') { response.writeHead(405).end(); return; }
        try {
            const pathname = new URL(request.url, 'http://127.0.0.1:8947').pathname;
            if (pathname === '/' || pathname === '/index.html') {
                response.setHeader('Content-Type', 'text/html; charset=utf-8');
                response.end(request.method === 'HEAD' ? undefined : fixturePage()); return;
            }
            const asset = assets.get(pathname);
            if (!asset) { response.writeHead(404).end(); return; }
            response.setHeader('Content-Type', asset[1]);
            // Read current source per request, so the main session's small script
            // edits appear on reload without a copied/frozen product script.
            const content = await readFile(new URL('../../BgiCoordinatorServer/wwwroot/' + asset[0], import.meta.url));
            response.end(request.method === 'HEAD' ? undefined : content);
        } catch (error) {
            response.writeHead(500, { 'Content-Type': 'text/plain; charset=utf-8' }).end('隔离夹具读取失败：' + error.message);
        }
    });
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const server = createPreviewServer();
    server.on('error', error => { console.error('隔离夹具启动失败：' + error.message); process.exitCode = 1; });
    server.listen(8947, '127.0.0.1', () => console.log('隔离 BGI 更新夹具：http://127.0.0.1:8947/ （无真实 gateway 或更新操作）'));
}
