'use strict';

// Pure-memory transport shared with the browser fixture. Importing this module
// neither registers tests nor opens sockets. No real gateway/update API is used.
function createUpdateTransport() {
    const targets = ['a', 'b', 'c', 'd'].map((letter, index) => ({
        key: 'fixture-' + letter, instanceId: 'fixture-instance-' + letter,
        uid: String(900000001 + index), name: ['隔离成员甲', '隔离成员乙', '隔离成员丙', '隔离成员丁'][index],
        local: index === 0, isObserver: false
    }));
    const calls = [], held = new Set(), queued = new Map(), jobs = new Map();
    const copy = value => value === undefined ? undefined : JSON.parse(JSON.stringify(value));
    const keyFor = (operation, targetKey) => operation + ':' + targetKey;
    let changed = () => {};
    function targetFor(value) {
        const target = targets.find(t => t.key === value || t.instanceId === value || t.uid === value);
        if (!target) throw new Error('隔离夹具不认识执行端：' + value);
        return target;
    }
    function catalog(target, sourceId = 'fixture-stable') {
        return {
            sourceId: sourceId || 'fixture-stable', currentVersion: 'fixture-old-' + target.key,
            targetDirectory: 'FIXTURE_ONLY/' + target.key + '/BetterGI', canInstall: true,
            sources: [{ id: 'fixture-stable', name: '隔离稳定源' }, { id: 'fixture-preview', name: '隔离预览源' }],
            packages: [0, 1].map(index => ({
                id: target.key + ':' + (sourceId || 'fixture-stable') + ':ticket-' + index,
                fileName: 'BetterGI-' + target.key + '-' + index + '.zip', size: (index + 1) + ' MiB（虚构）', canDownload: true
            })), job: copy(jobs.get(target.key) || null)
        };
    }
    function running(target, id) {
        return { id, done: false, canCancel: true, message: target.name + '：模拟下载中（不会执行更新）' };
    }
    function defaultReply(call) {
        const target = targetFor(call.target.key), { operation, payload } = call;
        if (operation === 'bgi.update.catalog') return { status: 'ok', data: catalog(target, payload?.sourceId) };
        if (operation === 'bgi.update.hint') return { status: 'ok', data: { available: false } };
        if (operation === 'bgi.update.start') {
            const job = running(target, payload.id); jobs.set(target.key, job);
            return { status: 'ok', data: copy(job) };
        }
        if (operation === 'bgi.update.status') {
            const job = jobs.get(target.key);
            return job?.id === payload.id ? { status: 'ok', data: copy(job) }
                : { status: 'unknown', message: target.name + '：隔离任务结果未知' };
        }
        if (operation === 'bgi.update.cancel') {
            const job = { id: payload.id, done: true, canCancel: false, message: target.name + '：隔离下载已取消' };
            jobs.set(target.key, job); return { status: 'ok', data: copy(job) };
        }
        throw new Error('隔离夹具禁止未实现的操作：' + operation);
    }
    function settle(call, outcome = 'ok') {
        if (!call || call.settled) throw new Error('隔离请求不存在或已经返回');
        call.settled = true;
        call.outcome = typeof outcome === 'string' ? outcome : 'custom';
        if (outcome === 'error' || outcome === 'unconfirmed') {
            const error = new Error(call.target.name + '：隔离传输异常' + (outcome === 'unconfirmed' ? '，结果未确认' : '，明确未发送'));
            if (outcome === 'unconfirmed') error.unconfirmed = true;
            call.reject(error);
        } else {
            const reply = typeof outcome === 'object' ? copy(outcome)
                : outcome === 'ok' ? defaultReply(call)
                : { status: outcome, message: call.target.name + '：隔离回复 ' + outcome };
            call.resolve(reply);
        }
        changed();
    }
    function send(operation, payload, options = {}) {
        // A missing/foreign target is a test failure, never a fallback to another member.
        const target = targetFor(options.target?.key);
        if (options.target.instanceId !== target.instanceId || options.target.uid !== target.uid)
            throw new Error('隔离请求的执行端身份不一致');
        let resolve, reject;
        const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
        const call = { number: calls.length + 1, operation, payload: copy(payload), target: copy(target), settled: false, resolve, reject };
        calls.push(call);
        const key = keyFor(operation, target.key), queue = queued.get(key);
        if (!held.has(key)) settle(call, queue?.length ? queue.shift() : 'ok');
        else changed();
        return promise;
    }
    return {
        targets, calls, jobs, catalog, running, targetFor, send,
        async read(...args) {
            const reply = await send(...args);
            if (reply.status !== 'ok') throw new Error(reply.message || '隔离执行端未返回数据');
            return reply;
        },
        hold(operation, targetKey, enabled = true) {
            const key = keyFor(operation, targetFor(targetKey).key);
            if (enabled) held.add(key); else held.delete(key);
            changed();
        },
        queue(operation, targetKey, outcome) {
            const key = keyFor(operation, targetFor(targetKey).key);
            if (!queued.has(key)) queued.set(key, []);
            queued.get(key).push(outcome); changed();
        },
        pending(operation, targetKey) {
            return calls.filter(call => !call.settled && (!operation || call.operation === operation) && (!targetKey || call.target.key === targetKey));
        },
        release(callOrNumber, outcome = 'ok') {
            settle(typeof callOrNumber === 'number' ? calls.find(call => call.number === callOrNumber) : callOrNumber, outcome);
        },
        complete(targetKey, message = '隔离模拟完成') {
            const key = targetFor(targetKey).key, job = jobs.get(key);
            if (!job) throw new Error('该隔离成员尚无任务');
            jobs.set(key, { ...job, done: true, canCancel: false, message }); changed();
        },
        onChange(callback) { changed = callback; },
        snapshot() {
            return calls.map(({ number, operation, payload, target, settled, outcome }) => ({ number, operation, payload, target, settled, outcome }));
        }
    };
}

function createFakeClock() {
    let now = 0, next = 1;
    const timers = new Map();
    return {
        now: () => now,
        setTimeout(callback, delay = 0) {
            const id = next++; timers.set(id, { callback, at: now + Math.max(0, Number(delay) || 0) }); return id;
        },
        clearTimeout(id) { timers.delete(id); },
        advance(milliseconds) {
            const until = now + milliseconds;
            let count = 0;
            for (;;) {
                const entry = [...timers.entries()].filter(([, timer]) => timer.at <= until)
                    .sort((a, b) => a[1].at - b[1].at || a[0] - b[0])[0];
                if (!entry) break;
                if (++count > 1000) throw new Error('隔离定时器循环超过上限');
                const [id, timer] = entry;
                timers.delete(id); now = timer.at; timer.callback();
            }
            now = until;
        },
        pending: () => timers.size
    };
}

function createMemoryStorage() {
    const items = new Map();
    return { getItem: key => items.get(key) ?? null, setItem: (key, value) => items.set(key, String(value)), removeItem: key => items.delete(key) };
}

// Only the APIs used by the real popup are doubled. Selects reject values not in
// their options, and replaceChildren resets selection as a browser does.
function createDocumentDouble() {
    let activeDialog = null;
    function node(tag = 'div') {
        const listeners = new Map();
        let value = '';
        const element = {
            tagName: tag.toUpperCase(), hidden: false, disabled: false, checked: false,
            textContent: '', children: [], dataset: {},
            get value() { return value; },
            set value(next) {
                const text = String(next);
                value = this.tagName === 'SELECT' && !this.children.some(option => option.value === text) ? '' : text;
            },
            replaceChildren(...children) { this.children = children; value = children[0]?.value || ''; },
            setAttribute(name, text) { this[name] = text; },
            addEventListener(name, callback) { listeners.set(name, callback); },
            showModal() { this.open = true; },
            close() { this.open = false; listeners.get('close')?.(); },
            remove() { if (activeDialog === this) activeDialog = null; },
            querySelector(selector) { return this.nodes?.[selector] || null; },
            set innerHTML(html) {
                this.html = html; this.nodes = {};
                for (const match of html.matchAll(/<(\w+)\b([^>]*\bdata-([\w-]+)(?=[\s=>])[^>]*)>/g)) {
                    const child = node(match[1]); child.hidden = /\bhidden\b/.test(match[2]);
                    child.checked = /\bchecked\b/.test(match[2]); this.nodes['[data-' + match[3] + ']'] = child;
                }
            },
            get innerHTML() { return this.html; }
        };
        return element;
    }
    return {
        hidden: false,
        body: { append(dialog) { activeDialog = dialog; } },
        createElement: node,
        getElementById(id) { return activeDialog?.id === id ? activeDialog : null; },
        querySelectorAll: () => [], addEventListener() {},
        get dialog() { return activeDialog; }
    };
}

async function flushPromises() {
    // No wall-clock waiting. This drains the bounded send/read/finally chains.
    for (let index = 0; index < 16; index++) await Promise.resolve();
}

function setup(options = {}) {
    const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
    const transport = options.transport || createUpdateTransport(), clock = createFakeClock();
    const storage = options.storage || createMemoryStorage(), document = createDocumentDouble();
    const confirmations = [], targets = options.targets || transport.targets;
    let randomByte = 0;
    const context = vm.createContext({
        console, document, sessionStorage: storage, Map, Set, Uint8Array,
        setTimeout: clock.setTimeout, clearTimeout: clock.clearTimeout,
        crypto: { getRandomValues(array) { return array.fill(++randomByte); } },
        Option: function Option(text, value) { this.textContent = text; this.value = value; },
        roomCode: 'FIXTURE_ONLY', mtTargets: () => targets,
        mtCredentialFor: target => target.unauthorized ? null : { browserId: 'fixture-browser-' + target.key },
        mtTargetReady: target => !target.offline,
        escapeHtml: value => String(value).replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char])),
        scheduleMemberCardAlignment() {}, mtRead: transport.read, mtSend: transport.send,
        confirm(message) { confirmations.push(message); return options.confirm !== false; }
    });
    context.window = context;
    vm.runInContext(fs.readFileSync(path.join(__dirname, '../../BgiCoordinatorServer/wwwroot/bgi-update-web.js'), 'utf8'), context,
        { filename: 'bgi-update-web.js' });
    const field = name => document.dialog?.querySelector('[data-' + name + ']');
    const member = index => ({ playerUid: targets[index].uid, clientInstanceId: targets[index].instanceId });
    const api = {
        transport, clock, storage, document, context, confirmations, field,
        async open(index = 0) { context.bgiWebUpdate.open(member(index)); await flushPromises(); },
        async select(index) { field('target').value = targets[index].key; field('target').onchange(); await flushPromises(); },
        async choose(index = 0) {
            const box = field('package'); box.value = box.children[index].value; box.onchange(); await flushPromises();
        },
        async source(id) { field('source').value = id; field('source').onchange(); await flushPromises(); },
        closeBgi(checked) { field('close-bgi').checked = checked; field('close-bgi').onchange(); },
        click(name) { return field(name).onclick(); },
        async tick(milliseconds = 1700) { clock.advance(milliseconds); await flushPromises(); },
        pendingKey(index) { return 'bgi.update.pending.' + targets[index].instanceId; },
        calls(operation, index) { return transport.calls.filter(call => call.operation === operation && (index === undefined || call.target.key === targets[index].key)); },
        view() {
            return Object.fromEntries(['target', 'source', 'package', 'close-bgi', 'version', 'package-note', 'status', 'refresh', 'download', 'install', 'cancel', 'clear']
                .map(name => {
                    const control = field(name);
                    return [name, { value: control.value, text: control.textContent, hidden: control.hidden, disabled: control.disabled,
                        checked: control.checked, options: control.children.map(option => ({ value: option.value, text: option.textContent })) }];
                }));
        }
    };
    return api;
}

function registerTests() {
    const test = require('node:test'), assert = require('node:assert/strict');
    const plain = value => JSON.parse(JSON.stringify(value));
    const last = values => values[values.length - 1];
    async function start(x, index = 0, install = true) {
        await x.select(index); await x.choose(index % 2); await x.click(install ? 'install' : 'download'); await flushPromises();
        return last(x.calls('bgi.update.start', index));
    }
    function assertMemberSwitchable(x) { assert.equal(x.field('target').disabled, false, '异步请求等待期间必须可以切换成员'); }

    test('四个已授权成员可各自更新，目标实例、票据、安装与 closeBgi 参数互不混用', async () => {
        const x = setup(); await x.open();
        for (let index = 0; index < 4; index++) {
            await x.select(index); await x.choose(index % 2); x.closeBgi(index % 2 === 0);
            await x.click('install'); await flushPromises(); assertMemberSwitchable(x);
        }
        const calls = x.calls('bgi.update.start'); assert.equal(calls.length, 4);
        assert.equal(new Set(calls.map(call => call.payload.id)).size, 4);
        for (let index = 0; index < 4; index++) {
            const target = x.transport.targets[index], call = calls[index];
            assert.deepEqual(call.target, target);
            assert.deepEqual(call.payload, { id: call.payload.id, ticket: target.key + ':fixture-stable:ticket-' + index % 2,
                install: true, closeBgi: index % 2 === 0 });
            assert.match(call.payload.id, /^[a-f0-9]{32}$/);
            assert.equal(x.storage.getItem(x.pendingKey(index)), call.payload.id);
            await x.select(index);
            assert.equal(x.field('package').value, call.payload.ticket);
            assert.equal(x.field('close-bgi').checked, index % 2 === 0);
            assert.match(x.field('status').textContent, new RegExp(target.name));
            assert.equal(x.field('install').disabled, true);
        }
        assert.equal(x.confirmations.length, 4);
    });

    test('等待目录时切到其他成员可立即读取并更新，迟到目录只在切回时显示', async () => {
        const x = setup(); x.transport.hold('bgi.update.catalog', 'fixture-a'); await x.open();
        assertMemberSwitchable(x); assert.equal(x.field('install').disabled, true);
        await x.select(1); await x.choose(1); const before = x.view();
        x.transport.release(x.transport.pending('bgi.update.catalog', 'fixture-a')[0]); await flushPromises();
        assert.deepEqual(x.view(), before);
        await x.select(0); assert.match(x.field('version').textContent, /fixture-a/);
        assert.equal(x.calls('bgi.update.catalog', 0).length, 1);
    });

    test('切回非活动成员保留自己的来源、选包和 closeBgi 草稿', async () => {
        const x = setup(); await x.open(); await x.source('fixture-preview'); await x.choose(1); x.closeBgi(false);
        const expected = x.view(); await x.select(1); await x.choose(0); x.closeBgi(true); await x.select(0);
        assert.deepEqual(x.view(), expected);
        await x.click('install');
        assert.deepEqual(last(x.calls('bgi.update.start')).payload, {
            id: last(x.calls('bgi.update.start')).payload.id, ticket: 'fixture-a:fixture-preview:ticket-1', install: true, closeBgi: false
        });
    });

    test('仅下载发送 install=false 和 closeBgi=false，不借用其他成员安装参数', async () => {
        const x = setup(); await x.open(); await x.choose(); x.closeBgi(true); await x.click('download');
        const call = last(x.calls('bgi.update.start'));
        assert.deepEqual(call.payload, { id: call.payload.id, ticket: 'fixture-a:fixture-stable:ticket-0', install: false, closeBgi: false });
        assert.equal(x.confirmations.length, 0); assert.equal(call.target.instanceId, 'fixture-instance-a');
    });

    test('取消确认框不发送更新；未授权、观察者与实例不匹配不能成为指定成员', async () => {
        const x = setup({ confirm: false }); await x.open(); await x.choose(); await x.click('install');
        assert.equal(x.calls('bgi.update.start').length, 0); assert.equal(x.storage.getItem(x.pendingKey(0)), null);
        const transport = createUpdateTransport();
        const denied = { ...transport.targets[1], unauthorized: true };
        const observer = { ...transport.targets[2], isObserver: true };
        const y = setup({ transport, targets: [transport.targets[0], denied, observer, transport.targets[3]] });
        for (const target of [denied, observer]) assert.equal(y.context.bgiWebUpdate.targetFor({ playerUid: target.uid, clientInstanceId: target.instanceId }), undefined);
        y.context.bgiWebUpdate.open({ playerUid: denied.uid, clientInstanceId: denied.instanceId });
        assert.equal(y.document.dialog, null);
        await y.open(); assert.deepEqual(y.field('target').children.map(option => option.value), ['fixture-a', 'fixture-d']);
        assert.equal(y.context.bgiWebUpdate.targetFor({ playerUid: transport.targets[0].uid, clientInstanceId: 'wrong-instance' }), undefined);
    });

    // Each former member's success, refusal, unknown and thrown exception runs
    // its actual catch/finally. The visible member must keep its UI AND timer.
    for (const operation of ['catalog', 'start', 'status', 'cancel']) {
        for (const outcome of ['ok', 'rejected', 'unknown', 'error', 'unconfirmed']) {
            test('迟到 ' + operation + ' / ' + outcome + ' 不覆盖当前成员 UI，不取消当前轮询', async () => {
                const x = setup(); await x.open(); await x.choose(1); x.closeBgi(false);
                if (operation === 'status' || operation === 'cancel') await x.click('install');
                const fullOperation = 'bgi.update.' + operation;
                x.transport.hold(fullOperation, 'fixture-a');
                if (operation === 'catalog') void x.click('refresh');
                else if (operation === 'start') void x.click('install');
                else void x.click(operation === 'status' ? 'refresh' : 'cancel');
                await flushPromises(); assertMemberSwitchable(x);
                const oldCall = x.transport.pending(fullOperation, 'fixture-a')[0]; assert.ok(oldCall);
                const b = await start(x, 1); const before = x.view();
                assert.equal(x.storage.getItem(x.pendingKey(1)), b.payload.id);
                x.transport.release(oldCall, outcome); await flushPromises();
                assert.deepEqual(x.view(), before, '旧成员 catch/finally 不可改变当前控件');
                assert.equal(x.storage.getItem(x.pendingKey(1)), b.payload.id);
                const polls = x.calls('bgi.update.status', 1).length;
                await x.tick(); assert.equal(x.calls('bgi.update.status', 1).length, polls + 1, '旧回复不得清掉新成员定时器');
                assert.equal(last(x.calls('bgi.update.status')).target.instanceId, 'fixture-instance-b');
                await x.select(0);
                assert.equal(x.field('close-bgi').checked, false);
                if (operation !== 'catalog' || outcome === 'ok') assert.equal(x.field('package').value, 'fixture-a:fixture-stable:ticket-1');
                assert.equal(x.calls('bgi.update.start', 1).length, 1);
            });
        }
    }

    test('四人发送尚未返回也能继续切换；同一成员 pending 时禁止重复发送', async () => {
        const x = setup(); await x.open();
        for (const target of x.transport.targets) x.transport.hold('bgi.update.start', target.key);
        for (let index = 0; index < 4; index++) {
            await x.select(index); await x.choose(); void x.click('install'); await flushPromises(); assertMemberSwitchable(x);
            assert.equal(x.field('install').disabled, true);
            await x.click('install'); await x.click('download'); assert.equal(x.calls('bgi.update.start', index).length, 1);
        }
        for (let index = 0; index < 4; index++) {
            await x.select(index); assertMemberSwitchable(x); assert.equal(x.field('install').disabled, true);
            assert.equal(x.field('status').textContent, '正在向执行端发送…');
            assert.equal(x.calls('bgi.update.status', index).length, 0, '发送仍等待时不并行查询同一成员');
        }
        assert.equal(x.transport.pending('bgi.update.start').length, 4);
        for (const call of x.transport.pending('bgi.update.start').reverse()) { x.transport.release(call); await flushPromises(); }
        assert.equal(x.calls('bgi.update.start').length, 4);
    });

    for (const outcome of ['unknown', 'unconfirmed']) test(outcome + ' 保留本成员 pending，重新选择和刷新也不会重复更新', async () => {
        const x = setup(); await x.open(); await x.choose(); x.transport.queue('bgi.update.start', 'fixture-a', outcome);
        await x.click('install'); const id = x.storage.getItem(x.pendingKey(0)); assert.ok(id);
        await x.click('install'); await x.click('download'); await x.select(1); await x.select(0); await x.click('refresh');
        assert.equal(x.calls('bgi.update.start', 0).length, 1);
        assert.equal(x.storage.getItem(x.pendingKey(0)), id); assert.equal(x.field('install').disabled, true);
        assert.equal(x.field('clear').hidden, false);
        for (const call of x.calls('bgi.update.status', 0)) assert.deepEqual(call.payload, { id });
    });

    for (const outcome of ['rejected', 'denied', 'error']) test('明确失败 ' + outcome + ' 仅解除失败成员 pending，保留其他三人', async () => {
        const x = setup(); await x.open();
        for (let index = 1; index < 4; index++) await start(x, index);
        await x.select(0); await x.choose(); x.transport.queue('bgi.update.start', 'fixture-a', outcome); await x.click('install');
        assert.equal(x.storage.getItem(x.pendingKey(0)), null); assert.equal(x.field('install').disabled, false);
        for (let index = 1; index < 4; index++) assert.equal(x.storage.getItem(x.pendingKey(index)), last(x.calls('bgi.update.start', index)).payload.id);
        await x.click('install'); assert.equal(x.calls('bgi.update.start', 0).length, 2, '明确未执行后允许用户重新发起');
    });

    test('取消只发送当前成员任务 ID，迟到取消成功不清除新成员 pending', async () => {
        const x = setup(); await x.open(); const a = await start(x); const b = await start(x, 1);
        x.transport.hold('bgi.update.cancel', 'fixture-b'); void x.click('cancel'); await flushPromises();
        const cancel = last(x.calls('bgi.update.cancel'));
        assert.deepEqual(cancel.payload, { id: b.payload.id }); assert.deepEqual(cancel.target, b.target); assertMemberSwitchable(x);
        await x.select(0); const before = x.view(); x.transport.release(cancel); await flushPromises();
        assert.deepEqual(x.view(), before); assert.equal(x.storage.getItem(x.pendingKey(0)), a.payload.id);
        assert.equal(x.storage.getItem(x.pendingKey(1)), null);
        await x.select(1); assert.equal(x.field('cancel').hidden, true); assert.equal(x.field('install').disabled, false);
        assert.equal(x.calls('bgi.update.start').length, 2);
    });

    test('解除待确认仅清当前成员，不发送取消或重发，不改变其他三人 pending', async () => {
        const x = setup(); await x.open();
        for (let index = 0; index < 4; index++) {
            x.transport.queue('bgi.update.start', x.transport.targets[index].key, 'unknown'); await start(x, index);
        }
        await x.select(0); assert.equal(x.field('clear').hidden, false);
        const ids = [0, 1, 2, 3].map(index => x.storage.getItem(x.pendingKey(index)));
        await x.click('clear'); await flushPromises();
        assert.equal(x.storage.getItem(x.pendingKey(0)), null); assert.equal(x.field('install').disabled, false);
        assert.match(x.field('status').textContent, /解除等待.*仍未确认/);
        for (let index = 1; index < 4; index++) assert.equal(x.storage.getItem(x.pendingKey(index)), ids[index]);
        await x.tick(); assert.equal(x.calls('bgi.update.start').length, 4); assert.equal(x.calls('bgi.update.cancel').length, 0);
    });

    test('取消解除待确认的确认框时保留 pending，既不取消也不重发', async () => {
        const storage = createMemoryStorage(); storage.setItem('bgi.update.pending.fixture-instance-a', 'previous-unknown-id');
        const x = setup({ storage, confirm: false }); await x.open();
        assert.equal(x.field('clear').hidden, false); await x.click('clear');
        assert.equal(storage.getItem(x.pendingKey(0)), 'previous-unknown-id');
        assert.equal(x.calls('bgi.update.start').length, 0); assert.equal(x.calls('bgi.update.cancel').length, 0);
        assert.equal(x.field('install').disabled, true);
    });

    test('发送回复任务 ID 不匹配时保留原 pending，核对前禁止重复发送', async () => {
        const x = setup(); await x.open(); await x.choose();
        x.transport.queue('bgi.update.start', 'fixture-a', { status: 'ok', data: { id: 'foreign-job', done: true, message: '不属于本次请求' } });
        await x.click('install'); const call = last(x.calls('bgi.update.start'));
        assert.equal(x.storage.getItem(x.pendingKey(0)), call.payload.id);
        assert.equal(x.field('install').disabled, true); assert.doesNotMatch(x.field('status').textContent, /不属于本次请求/);
        await x.click('install'); await x.tick(); assert.equal(x.calls('bgi.update.start').length, 1);
        assert.deepEqual(last(x.calls('bgi.update.status')).payload, { id: call.payload.id });
    });

    test('进度 ID 不匹配不能清除 pending 或冒充成功，之后仍按原 ID 查询', async () => {
        const x = setup(); await x.open(); const a = await start(x);
        x.transport.queue('bgi.update.status', 'fixture-a', { status: 'ok', data: { id: 'foreign-job', done: true, canCancel: false, message: '错误成员完成' } });
        await x.click('refresh'); assert.equal(x.storage.getItem(x.pendingKey(0)), a.payload.id);
        assert.doesNotMatch(x.field('status').textContent, /错误成员完成/); assert.equal(x.field('install').disabled, true);
        await x.tick(); assert.deepEqual(last(x.calls('bgi.update.status')).payload, { id: a.payload.id });
    });

    test('同一成员查询仍在等待时往返切换不重复查询；回复后仅恢复自己的轮询', async () => {
        const x = setup(); await x.open(); await start(x);
        x.transport.hold('bgi.update.status', 'fixture-a'); void x.click('refresh'); await flushPromises();
        const a = last(x.calls('bgi.update.status', 0)); await x.select(1); await x.select(0);
        await x.click('refresh'); await x.tick(10000); assert.equal(x.calls('bgi.update.status', 0).length, 1);
        x.transport.release(a); await flushPromises(); await x.tick();
        assert.equal(x.calls('bgi.update.status', 0).length, 2); assert.equal(x.calls('bgi.update.start', 0).length, 1);
    });

    test('关闭重开从 sessionStorage 恢复四人任务，仅查询不自动重发', async () => {
        const x = setup(); await x.open();
        for (let index = 0; index < 4; index++) await start(x, index);
        const ids = [0, 1, 2, 3].map(index => x.storage.getItem(x.pendingKey(index)));
        x.click('close'); assert.equal(x.document.dialog, null); await x.tick(5000);
        assert.equal(x.clock.pending(), 0);
        await x.open();
        for (let index = 0; index < 4; index++) {
            await x.select(index);
            assert.equal(x.storage.getItem(x.pendingKey(index)), ids[index]);
            assert.deepEqual(last(x.calls('bgi.update.status', index)).payload, { id: ids[index] });
            assert.equal(x.field('install').disabled, true); assertMemberSwitchable(x);
        }
        assert.equal(x.calls('bgi.update.start').length, 4);
        // A fresh JS context models a full page reload, preserving only storage and the fake executor.
        const reloaded = setup({ storage: x.storage, transport: x.transport }); await reloaded.open(2);
        assert.deepEqual(last(reloaded.calls('bgi.update.status', 2)).payload, { id: ids[2] });
        assert.equal(reloaded.calls('bgi.update.start').length, 4);
    });

    test('关闭时尚未返回的旧发送 finally 不取消重开窗口轮询，也不自动重发', async () => {
        const x = setup(); await x.open(); await x.choose(); x.transport.hold('bgi.update.start', 'fixture-a');
        void x.click('install'); await flushPromises(); const old = x.transport.pending('bgi.update.start', 'fixture-a')[0];
        x.click('close'); await x.open(1); const b = await start(x, 1), before = x.view();
        x.transport.release(old); await flushPromises(); assert.deepEqual(x.view(), before);
        const count = x.calls('bgi.update.status', 1).length; await x.tick();
        assert.equal(x.calls('bgi.update.status', 1).length, count + 1);
        assert.equal(x.storage.getItem(x.pendingKey(1)), b.payload.id);
        await x.select(0); assert.deepEqual(last(x.calls('bgi.update.status', 0)).payload, { id: old.payload.id });
        assert.equal(x.calls('bgi.update.start', 0).length, 1);
    });

    for (const operation of ['start', 'status', 'cancel']) {
        for (const outcome of ['ok', 'rejected', 'unknown', 'error', 'unconfirmed']) {
            test('关闭重开后旧窗口 ' + operation + ' / ' + outcome + ' 不清除同成员新任务', async () => {
                const x = setup(); await x.open(); await x.choose();
                if (operation !== 'start') await x.click('install');
                const fullOperation = 'bgi.update.' + operation;
                x.transport.hold(fullOperation, 'fixture-a');
                void x.click(operation === 'start' ? 'install' : operation === 'status' ? 'refresh' : 'cancel');
                await flushPromises(); const old = x.transport.pending(fullOperation, 'fixture-a')[0]; assert.ok(old);
                const oldId = x.storage.getItem(x.pendingKey(0)); x.click('close');
                // The fake executor lost its old record. The new window must
                // explicitly reconcile/clear it before the user starts anew.
                x.transport.jobs.delete('fixture-a');
                x.transport.hold(fullOperation, 'fixture-a', false);
                await x.open(); assert.equal(x.field('clear').hidden, false);
                await x.click('clear'); await x.click('refresh'); await x.choose(1);
                await x.click('install'); const newer = last(x.calls('bgi.update.start', 0));
                assert.notEqual(newer.payload.id, oldId); const before = x.view();
                x.transport.release(old, outcome === 'ok'
                    ? { status: 'ok', data: { id: oldId, done: true, canCancel: false, message: '旧窗口迟到终态' } }
                    : outcome);
                await flushPromises(); assert.deepEqual(x.view(), before);
                assert.equal(x.storage.getItem(x.pendingKey(0)), newer.payload.id, '旧窗口不得清除同一成员新窗口保存的新 ID');
                const polls = x.calls('bgi.update.status', 0).length; await x.tick();
                assert.equal(x.calls('bgi.update.status', 0).length, polls + 1);
                assert.deepEqual(last(x.calls('bgi.update.status', 0)).payload, { id: newer.payload.id });
                assert.equal(x.calls('bgi.update.start', 0).length, 2);
            });
        }
    }

    test('选择包和轮询仅更新状态时保留原 option 节点，切换成员后使用对应目录', async () => {
        const x = setup(); await x.open();
        const sourceOptions = x.field('source').children, packageOptions = x.field('package').children;
        await x.choose(1); x.closeBgi(false); await x.click('install'); await x.tick();
        assert.equal(x.field('source').children, sourceOptions);
        assert.equal(x.field('package').children, packageOptions);
        assert.equal(x.field('package').value, 'fixture-a:fixture-stable:ticket-1');
        await x.select(1);
        assert.notEqual(x.field('package').children, packageOptions);
        assert.equal(x.field('package').children[0].value, 'fixture-b:fixture-stable:ticket-0');
    });

    test('终态只解除对应成员，刷新目录取得新票据后允许用户继续操作', async () => {
        const x = setup(); await x.open(); const a = await start(x); const b = await start(x, 1);
        x.transport.complete('fixture-a', '甲模拟完成'); await x.select(0);
        assert.equal(x.storage.getItem(x.pendingKey(0)), null);
        assert.equal(x.storage.getItem(x.pendingKey(1)), b.payload.id);
        assert.equal(x.field('install').disabled, false); assert.equal(x.field('package').value, a.payload.ticket);
        assert.equal(x.calls('bgi.update.start', 0).length, 1);
        assert.ok(x.calls('bgi.update.catalog', 0).length >= 2);
        assert.equal(x.field('cancel').hidden, true);
    });

    test('sessionStorage 不可用时仍可在同一窗口按成员保留任务并阻止重发', async () => {
        const storage = { getItem() { throw new Error('blocked'); }, setItem() { throw new Error('blocked'); }, removeItem() { throw new Error('blocked'); } };
        const x = setup({ storage }); await x.open(); await start(x); await x.select(1); await x.select(0);
        await x.click('install'); assert.equal(x.calls('bgi.update.start', 0).length, 1);
        assert.equal(x.field('install').disabled, true);
        assert.deepEqual(plain(last(x.calls('bgi.update.status', 0)).payload), { id: last(x.calls('bgi.update.start', 0)).payload.id });
    });
}

module.exports = { createUpdateTransport, createFakeClock, createMemoryStorage, createDocumentDouble, flushPromises, setup };
if (require.main === module) registerTests();
