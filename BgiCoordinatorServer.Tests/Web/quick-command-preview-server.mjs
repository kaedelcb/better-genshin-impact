// Isolated UI/parameter fixture only. It never connects to a real gateway or executor.
import http from 'node:http';
import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';

function makeMembers(scenario) {
    const keys = ['一键传奇', '一键次数盾', '一键精英', '一键小怪', '一键自定义'];
    const actions = [
        { policy: 'resume', taskType: 'group', taskName: '', priority: 0 },
        { policy: 'stop', taskType: 'group', taskName: '', priority: 0 },
        { policy: 'runSpecified', taskType: 'group', taskName: '成员丙收尾配置组', priority: 3 },
        { policy: 'runSpecified', taskType: 'onedragon', taskName: '成员丁收尾一条龙', priority: 4 }
    ];
    return ['a', 'b', 'c', 'd'].map((uid, i) => {
        const quickCommands = Object.fromEntries(keys.map(key => [key, (i % 2 ? 'ONEDRAGON:' : 'GROUP:') + key + uid]));
        const configGroups = ['网页统一配置组', ...keys.map(key => key + uid)];
        const oneClickConfigs = ['网页统一一条龙', ...keys.map(key => key + uid)];
        const names = ['传送到起点', '执行中段任务', '收尾任务'];
        const tasks = configs => Object.fromEntries(configs.map(name => [name, names]));
        const statuses = (configs, oneClick) => Object.fromEntries(configs.map(name => [name, names.map((text, index) =>
            oneClick ? { name: text, enabled: true, taskId: `00000000-0000-0000-0000-00000000000${index + 1}` }
                : { name: text, status: 'Enabled', index: index + 1 })]));
        if (scenario === 'changed' && i === 0) {
            for (const key of keys) quickCommands[key] = 'GROUP:状态变化后的任务';
            actions[i] = { policy: 'runSpecified', taskType: 'group', taskName: '状态变化后的收尾', priority: 0 };
        }
        return { playerUid: uid, playerName: ['成员甲', '成员乙', '成员丙', '成员丁'][i],
            online: scenario !== 'offline' || i !== 3, bgiStatus: 'running', taskRunning: false,
            quickCommands, hoeingCompletionAction: scenario === 'missing' && i === 3 ? null : actions[i],
            onlineHoeingGroupNames: ['原有联机任务' + uid], onlineHoeingGroupTypes: ['group'],
            configGroups, oneClickConfigs, configGroupTasks: tasks(configGroups), oneClickTasks: tasks(oneClickConfigs),
            configGroupTasksWithStatus: statuses(configGroups, false), oneClickTasksWithStatus: statuses(oneClickConfigs, true) };
    });
}

function mockGateway(makeMembers) {
    const query = new URLSearchParams(location.search);
    let scenario = query.get('scenario') || (query.get('capability') === 'old' ? 'old' : 'normal');
    let members = makeMembers(scenario), lastBatch = null, nextFailure = '', connection;
    const dispatches = [], originalBinding = localStorage.getItem('web_hie_binding');
    if (query.get('legacy') === '1' && originalBinding === null)
        localStorage.setItem('web_hie_binding', JSON.stringify(Object.fromEntries(
            ['传奇', '次数盾', '精英', '小怪', '自定义'].map(key => [key, key === '次数盾' ? 'ONEDRAGON:网页统一一条龙' : 'GROUP:网页统一配置组']))));
    const section = document.createElement('section');
    section.style.cssText = 'margin:24px;padding:12px;border:2px solid #b60;background:#fff;color:#222';
    section.innerHTML = '<strong>隔离网页夹具：虚构成员；仅验证 UI 和发送参数，不代表真实联机通过。</strong><pre id="fixtureDispatch" style="white-space:pre-wrap;overflow-wrap:anywhere;font-size:12px"></pre>';
    document.body.appendChild(section);
    const toolbar = document.createElement('div');
    toolbar.style.cssText = 'position:fixed;top:0;right:0;z-index:2000;background:#fff4d2;border:1px solid #b60;padding:4px;font-size:12px';
    toolbar.append('隔离夹具：');
    function render() {
        section.querySelector('pre').textContent = JSON.stringify({ scenario, nextFailure, originalBinding,
            currentBinding: localStorage.getItem('web_hie_binding'), dispatches }, null, 2);
    }
    function publish() { connection?.handlers.evt?.({ name: 'control.playersUpdated', payload: { full: true, players: structuredClone(members) } }); render(); }
    function button(text, action) { const button = document.createElement('button'); button.textContent = text; button.onclick = action; toolbar.appendChild(button); }
    for (const state of ['normal', 'old', 'missing', 'changed', 'offline']) button(state, () => {
        const capabilitiesChanged = (scenario === 'old') !== (state === 'old');
        scenario = state; members = makeMembers(state); publish();
        if (capabilitiesChanged) connection?.reconnected?.();
    });
    button('下次传输未知', () => { nextFailure = 'unknown'; render(); });
    button('下次明确拒绝', () => { nextFailure = 'rejected'; render(); });
    button('清空发送记录', () => { dispatches.length = 0; render(); });
    document.body.appendChild(toolbar); render();
    window.addEventListener('storage', render);
    class MockConnection {
        handlers = {};
        state = 'Disconnected';
        on(name, callback) { this.handlers[name] = callback; }
        onreconnecting(callback) { this.reconnecting = callback; }
        onreconnected(callback) { this.reconnected = callback; }
        onclose(callback) { this.closed = callback; }
        async start() { this.state = 'Connected'; }
        async stop() { this.state = 'Disconnected'; this.closed?.(); }
        async invoke(method, envelope) {
            if (method === 'Dispatch') { dispatches.push(structuredClone({ method, envelope })); render(); }
            const payload = envelope.payload || {};
            if (method === 'Query' && envelope.name === 'session.hello') return { payload: { capabilities: [
                'hoeing.start.v2', 'hoeing.completionChoice.v1', ...(scenario === 'old' ? [] : ['hoeing.quickCommandCompletion.v1'])] } };
            if (envelope.name === 'control.joinRoom') { publish(); return { payload: {} }; }
            if (envelope.name === 'control.batch.current') return { payload: structuredClone(lastBatch) };
            if (envelope.name === 'control.batch.begin') {
                const failure = nextFailure; nextFailure = ''; render();
                if (failure === 'rejected') return { payload: { error: { code: 'fixture_rejected', message: '隔离夹具明确拒绝 begin' } } };
                lastBatch = { batchId: 'fixture-' + (dispatches.length), protocol: 'hoeing.start.v2', source: 'manual',
                    generation: 1, round: 0, index: 0, total: 1, phase: 'registering', reason: '',
                    requestId: payload.requestId, quickCommandKey: payload.quickCommandKey || '',
                    useCompletionActions: payload.useCompletionActions === true, participantUids: [...payload.targetUids],
                    bindings: Object.fromEntries(payload.targetUids.map(uid => {
                        const value = members.find(member => member.playerUid === uid)?.quickCommands[payload.quickCommandKey] || 'GROUP:原有联机任务';
                        return [uid, [{ name: value.slice(value.startsWith('GROUP:') ? 6 : 10), type: value.startsWith('GROUP:') ? 'group' : 'onedragon' }]];
                    })) };
                if (failure === 'unknown') throw new Error('隔离夹具模拟传输未知；批次已保存供 current 核对');
                return { payload: structuredClone(lastBatch) };
            }
            if (envelope.name === 'control.sendCommand') {
                const command = payload.command;
                if (command.cmd === 'set_task_enabled') queueMicrotask(() => this.handlers.evt?.({ name: 'control.remoteCommandResult', payload: {
                    commandId: command.commandId, cmd: command.cmd, targetUid: command.target[0], status: 'success',
                    configRevision: 'fixture-revision', targetProcessId: 101, targetStartTicksUtc: 638000000000000000 } }));
                return { payload: {} };
            }
            throw new Error('隔离夹具未实现路径：' + method + ' ' + envelope.name);
        }
    }
    window.signalR = { HubConnectionState: { Connected: 'Connected' }, HubConnectionBuilder: class {
        withUrl() { return this; } withAutomaticReconnect() { return this; }
        build() { connection = new MockConnection(); return connection; }
    } };
}

assert.equal(makeMembers('normal').length, 4);
assert.equal(Object.keys(makeMembers('normal')[0].quickCommands).length, 5);
assert.equal(makeMembers('missing')[3].hoeingCompletionAction, null);
assert.equal(makeMembers('offline')[3].online, false);
assert.equal(makeMembers('changed')[0].quickCommands['一键传奇'], 'GROUP:状态变化后的任务');
const assets = new Map([['/', 'index.html'], ['/index.html', 'index.html'], ['/control-room.css', 'control-room.css'], ['/control-room.js', 'control-room.js']]);
const server = http.createServer(async (req, res) => {
    const pathname = new URL(req.url, 'http://127.0.0.1').pathname;
    res.setHeader('Cache-Control', 'no-store');
    try {
        if (pathname === '/signalr.min.js') {
            res.setHeader('Content-Type', 'text/javascript; charset=utf-8');
            res.end(`(${mockGateway.toString()})(${makeMembers.toString()});`); return;
        }
        const asset = assets.get(pathname);
        if (!asset) { res.writeHead(404).end(); return; }
        res.setHeader('Content-Type', asset.endsWith('.html') ? 'text/html; charset=utf-8' : asset.endsWith('.css') ? 'text/css; charset=utf-8' : 'text/javascript; charset=utf-8');
        res.end(await readFile(new URL('../../BgiCoordinatorServer/wwwroot/' + asset, import.meta.url)));
    } catch (error) { res.writeHead(500).end(String(error)); }
});
server.listen(0, '127.0.0.1', () => console.log(`http://127.0.0.1:${server.address().port}/`));
