// Real, isolated server + real SignalR transport. Executor peers simulate replies; no BGI/game is run.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const net = require('node:net');
const { spawn } = require('node:child_process');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const output = path.resolve(process.env.GENGDI_TEST_ROOT || path.join(__dirname, '../../Build/web-gengdi-20261027'));
const evidence = path.resolve(process.env.GENGDI_EVIDENCE_DIR || output);
(async () => {
    fs.mkdirSync(evidence, { recursive: true });
    const probe = net.createServer(); await new Promise(resolve => probe.listen(0, '127.0.0.1', resolve));
    const port = probe.address().port; await new Promise(resolve => probe.close(resolve));
    const url = `http://127.0.0.1:${port}`;
    const serverRoot = path.join(output, 'server');
    const log = fs.openSync(path.join(evidence, 'gateway-server.log'), 'a');
    const server = spawn('dotnet', [path.join(serverRoot, 'BgiCoordinatorServer.dll'), '--urls', url], {
        cwd: serverRoot, windowsHide: true, stdio: ['ignore', log, log],
        env: { ...process.env, ROOM_LOG_ROOT: path.join(evidence, 'isolated-room-data'), ADMIN_MONITOR_PASSWORD: 'isolated-test-only' }
    });
    let browser;
    try {
        let ready = false;
        for (let attempt = 0; attempt < 100; attempt++) {
            if (server.exitCode !== null) throw new Error('isolated server exited: ' + server.exitCode);
            try { if ((await fetch(url)).ok) { ready = true; break; } } catch {}
            await new Promise(resolve => setTimeout(resolve, 100));
        }
        assert(ready, 'isolated server must start');
        browser = await chromium.launch({ channel: 'msedge', headless: true });
        const p = await browser.newPage({ viewport: { width: 390, height: 844 } });
        const errors = []; p.on('pageerror', error => errors.push(error.message));
        await p.goto(url);
        await p.evaluate(async () => {
            window.executorCalls = []; window.executorErrors = []; window.executors = [];
            const caps = ['hoeing.start.v2', 'hoeing.completionChoice.v1', 'hoeing.quickCommandCompletion.v1',
                'hoeing.localFinish.v1', 'hoeing.localTaskFailure.v1'];
            const envelope = (name, payload, type = 'command') => ({ protocolVersion: 3, type, name, requestId: crypto.randomUUID(), payload, sentAtUtc: new Date().toISOString() });
            window.testRoom = 'WEBTEST' + Date.now();
            for (let i = 0; i < 2; i++) {
                const uid = String(100000001 + i), nickname = ['海蒂屁骨汤', '澪号大猫猫'][i];
                const conn = new signalR.HubConnectionBuilder().withUrl('/gateway').build();
                const status = { roomCode: testRoom, playerUid: uid, playerName: nickname, bgiStatus: 'running', bgiVersion: 'isolated-test',
                    taskRunning: true, taskStatusAvailable: true, currentTaskName: '精英混合一条龙', currentRouteDisplay: '须弥 · 禅那园',
                    configGroups: ['联机锄地-精英', '好感日常'], oneClickConfigs: ['联机收尾'], configGroupTasks: { '联机锄地-精英': ['任务一', '任务二'] },
                    configGroupTasksWithStatus: { '联机锄地-精英': [{ name: '任务一', status: 'Enabled' }, { name: '任务二', status: 'Enabled' }] },
                    onlineHoeingGroupNames: ['联机锄地-精英'], onlineHoeingGroupTypes: ['group'], expectedHoeingPlayers: 2,
                    quickCommands: { '一键精英': 'GROUP:联机锄地-精英' }, scheduledOnlineTime: '21:30',
                    hoeingCompletionAction: { policy: 'resume', taskType: 'group', taskName: '', priority: 0 } };
                const dispatch = async (name, payload) => {
                    const result = await conn.invoke('Dispatch', envelope(name, payload));
                    if (result?.payload?.error) throw new Error(JSON.stringify(result.payload.error));
                    return result;
                };
                const report = () => dispatch('control.reportStatus', { status });
                conn.on('evt', env => {
                    if (env.name !== 'control.remoteCommand') return;
                    const cmd = env.payload?.command;
                    if (!cmd || ['ack', 'task_policy.data', 'task_policy.push_result'].includes(cmd.cmd)) return;
                    (async () => {
                        executorCalls.push({ uid, ...cmd });
                        let replyName = 'ack', params = { status: 'success', message: '隔离执行端已确认' };
                        if (cmd.cmd === 'task_policy.pull') {
                            const a = status.hoeingCompletionAction; replyName = 'task_policy.data';
                            params = { ok: 'true', policy: a.policy, specifiedType: a.taskType, specifiedName: a.taskName, specifiedPriority: String(a.priority) };
                        } else if (cmd.cmd === 'task_policy.push') {
                            status.hoeingCompletionAction = { policy: cmd.params.policy, taskType: cmd.params.specifiedType, taskName: cmd.params.specifiedName, priority: Number(cmd.params.specifiedPriority) };
                            replyName = 'task_policy.push_result'; params = { ok: 'true', message: '隔离执行端已应用策略' };
                        } else if (cmd.cmd === 'set_scheduled_online_time') status.scheduledOnlineTime = cmd.params.scheduledOnlineTime;
                        else if (cmd.cmd === 'bind_hoeing_group') { status.onlineHoeingGroupNames = cmd.params.groupNames; status.onlineHoeingGroupTypes = cmd.params.groupTypes; }
                        else if (cmd.cmd === 'set_quick_command') status.quickCommands[cmd.params.key] = cmd.params.value;
                        await dispatch('control.sendCommand', { command: { roomCode: testRoom, cmd: replyName, sender: nickname, senderUid: uid, target: [cmd.senderUid], commandId: cmd.commandId, params } });
                        await report();
                    })().catch(error => executorErrors.push(error.message));
                });
                await conn.start();
                const hello = await conn.invoke('Query', envelope('session.hello', { clientKind: 'assistant', clientVersion: 'isolated-test', protocolVersion: 3, capabilities: caps }, 'query'));
                if (hello?.payload?.error) throw new Error(JSON.stringify(hello.payload.error));
                await dispatch('control.joinRoom', { roomCode: testRoom, password: 'isolation-only', playerUid: uid, playerName: nickname, allowedUids: [], isRemote: false, clientInstanceId: 'isolated-' + uid });
                await report(); executors.push(conn);
            }
        });
        await p.locator('#roomCode').fill(await p.evaluate(() => testRoom));
        await p.locator('#password').fill('isolation-only'); await p.locator('#nickname').fill('网页验收');
        await p.locator('#joinButton').click(); await p.locator('#controlPanel').waitFor({ state: 'visible' });
        await p.waitForFunction(() => document.querySelectorAll('.member-card').length === 2, null, { timeout: 8000 }).catch(async error => {
            console.error({ pageErrors: errors, state: await p.evaluate(() => ({ players, log: document.querySelector('#logArea').textContent })) });
            throw error;
        });
        assert.equal(await p.locator('.member-card').count(), 2);
        await p.locator('[data-ui="detail"][data-uid="100000001"]').first().click();
        await p.locator('[data-op="start_bgi"]').click(); await p.locator('#operationConfirm').click();
        await p.waitForFunction(() => document.querySelector('.modal .form-result').textContent.includes('已确认'));
        await p.locator('.modal [data-close]').click();
        await p.locator('[data-section="settings"]').click();
        await p.waitForFunction(() => document.querySelector('#policyResult').textContent.includes('已读取'));
        await p.locator('#scheduledTime').fill('22:25'); await p.locator('#saveSchedule').click();
        await p.waitForFunction(() => document.querySelector('#scheduleResult').textContent.includes('已确认'));
        await p.locator('#completionPolicy').selectOption('stop'); await p.locator('#savePolicy').click();
        await p.waitForFunction(() => document.querySelector('#policyResult').textContent.includes('已确认'));
        await p.locator('#reloadPolicy').click(); await p.waitForFunction(() => document.querySelector('#policyResult').textContent.includes('已读取'));
        assert.equal(await p.locator('#completionPolicy').inputValue(), 'stop');
        await p.locator('#saveBindings').click(); await p.waitForFunction(() => document.querySelector('#bindingResult').textContent.includes('已确认'));
        await p.locator('[data-quick-index="0"]').selectOption(JSON.stringify(['group', '联机锄地-精英']));
        await p.locator('[data-save-quick="0"]').click(); await p.waitForFunction(() => document.querySelector('[data-quick-result="0"]').textContent.includes('已确认'));
        await p.locator('#homeTab').click(); await p.locator('.selection-actions .btn-primary').click();
        await p.screenshot({ path: path.join(evidence, 'mobile-confirm.png') });
        await p.locator('#startBatch').click(); await p.waitForFunction(() => document.querySelector('.batch-feedback').textContent.includes('登记成员'));
        await p.locator('.modal [data-close]').click();
        await p.locator('[data-ui="detail"][data-uid="100000001"]').first().click();
        await p.locator('#detailReset').click(); await p.locator('.reset-state-all').check(); await p.locator('.reset-state-confirm').click();
        await p.waitForFunction(() => document.querySelector('.reset-state-result').textContent.includes('均已确认重置成功'));
        await p.locator('.reset-state-cancel').click();
        await p.screenshot({ path: path.join(evidence, 'mobile-detail.png') });
        await p.locator('#homeTab').click(); await p.screenshot({ path: path.join(evidence, 'mobile-home.png') });
        await p.setViewportSize({ width: 1440, height: 1000 }); await p.screenshot({ path: path.join(evidence, 'desktop-home.png') });
        const observed = await p.evaluate(() => ({ calls: executorCalls.map(c => ({ cmd: c.cmd, uid: c.uid, target: c.target })), errors: executorErrors }));
        assert.deepEqual(observed.errors, []); assert.deepEqual(errors, []);
        for (const command of ['start_bgi', 'set_scheduled_online_time', 'bind_hoeing_group', 'set_quick_command', 'task_policy.pull', 'task_policy.push', 'reset_state'])
            assert(observed.calls.some(c => c.cmd === command), command);
        await p.evaluate(async () => { await Promise.all(executors.map(c => c.stop())); });
        fs.writeFileSync(path.join(evidence, 'gateway-e2e.json'), JSON.stringify({ status: 'passed', boundary: 'real isolated server and SignalR; simulated executor replies; no PC or game runtime', ...observed }, null, 2));
        console.log('PASS isolated real gateway: web static assets, join/status, start BGI, schedule, bindings, policy pull/push, coordinated begin and reset ack; simulated executors only.');
    } finally {
        if (browser) await browser.close();
        if (server.exitCode === null) { server.kill(); await new Promise(resolve => server.once('exit', resolve)); }
        fs.closeSync(log);
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
