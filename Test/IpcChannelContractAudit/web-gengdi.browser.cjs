const assert = require('node:assert/strict');
const { fixture } = require('./web-gengdi-fixture.cjs');
(async () => {
    const f = await fixture(), p = f.page;
    try {
        await p.evaluate(() => rejectJoin = true);
        await p.locator('#roomCode').fill('isolated'); await p.locator('#password').fill('bad');
        await p.locator('#joinButton').click();
        await p.waitForFunction(() => document.querySelector('#loginError').textContent.includes('密码错误'));
        assert.equal(await p.locator('#controlPanel').isVisible(), false);
        await p.evaluate(() => rejectJoin = false); await f.login();
        await p.locator('[data-ui="select"][data-uid="b"]').click();
        assert.equal(await p.locator('[data-ui="select"][data-uid="off"]').isDisabled(), true);
        await p.locator('[data-ui="detail"][data-uid="a"]').first().click();
        await p.locator('[data-section="settings"]').click();
        await p.waitForFunction(() => document.querySelector('#policyResult').textContent.includes('已读取'));
        await p.locator('#scheduledTime').fill('22:15'); await p.locator('#saveSchedule').click();
        await p.waitForFunction(() => document.querySelector('#scheduleResult').textContent.includes('已确认'));
        await p.locator('#bindingChoice').selectOption(JSON.stringify(['onedragon', '收尾']));
        await p.locator('#addBinding').click(); await p.locator('#saveBindings').click();
        await p.waitForFunction(() => document.querySelector('#bindingResult').textContent.includes('已确认'));
        await p.locator('#completionPolicy').selectOption('runSpecified');
        await p.locator('#completionTask').selectOption(JSON.stringify(['group', '精英']));
        await p.locator('#completionPriority').fill('7'); await p.locator('#savePolicy').click();
        await p.waitForFunction(() => document.querySelector('#policyResult').textContent.includes('已确认'));
        await p.locator('[data-quick-index="0"]').selectOption(JSON.stringify(['group', '精英']));
        await p.locator('[data-save-quick="0"]').click();
        await p.waitForFunction(() => document.querySelector('[data-quick-result="0"]').textContent.includes('已确认'));
        const commands = await p.evaluate(() => gatewayCalls.filter(x => x.name === 'control.sendCommand').map(x => x.payload.command));
        assert.deepEqual(commands.find(c => c.cmd === 'bind_hoeing_group').params.groupTypes, ['group', 'onedragon']);
        assert.equal(commands.find(c => c.cmd === 'set_scheduled_online_time').params.scheduledOnlineTime, '22:15');
        assert.equal(commands.find(c => c.cmd === 'task_policy.push').params.specifiedPriority, '7');
        assert.equal(commands.find(c => c.cmd === 'set_quick_command').params.value, 'GROUP:精英');
        assert(commands.every(c => c.target.length === 1 && c.target[0] === 'a'));
        // Broadcasts must not replace an unfinished editor.
        await p.locator('#scheduledTime').fill('23:40');
        await p.evaluate(() => emit('control.playersUpdated', { full: true, players: testMembers }));
        assert.equal(await p.locator('#scheduledTime').inputValue(), '23:40');
        await p.evaluate(() => failPolicy = true); await p.locator('#reloadPolicy').click();
        await p.waitForFunction(() => document.querySelector('#policyResult').textContent.includes('读取未完成'));
        assert.equal(await p.locator('#savePolicy').isDisabled(), true);
        // Preserve typed task identities, and apply the checkbox's last state.
        await p.locator('[data-section="tasks"]').click();
        await p.locator('.catalog-item').filter({ hasText: '精英' }).click();
        const check = p.locator('.task-checkbox').first();
        await check.uncheck(); await check.check(); await check.uncheck();
        await p.locator('#tskOk').click(); await p.locator('#tskOk').waitFor({ state: 'detached' });
        const changes = await p.evaluate(() => gatewayCalls.filter(x => x.payload?.command?.cmd === 'set_task_enabled').map(x => x.payload.command));
        assert.equal(changes.at(-1).params.enabled, false); assert.equal(changes.at(-1).params.taskIndex, 1);
        const start = await p.evaluate(() => gatewayCalls.filter(x => x.payload?.command?.cmd === 'start_group').at(-1).payload.command);
        assert.equal(start.params.expectedConfigRevision, 'rev-2'); assert.equal(start.params.bgiEpoch.startTicksUtc, '638937012345678901');
        await p.locator('[data-kind="oneclick"]').click(); await p.locator('.catalog-item').filter({ hasText: '收尾' }).click();
        await p.locator('.task-row').nth(1).click(); await p.locator('#tskOk').click(); await p.locator('#tskOk').waitFor({ state: 'detached' });
        assert.equal(await p.evaluate(() => gatewayCalls.filter(x => x.payload?.command?.cmd === 'start_oneclick').at(-1).payload.command.params.startFromTaskId), 'guid-2');
        await p.locator('[data-section="history"]').click(); await p.locator('#clearHistory').click(); await p.locator('#clearConfirm').click();
        await p.waitForFunction(() => document.querySelector('.modal .form-result').textContent.includes('已确认'));
        await p.locator('.modal [data-close]').click();
        await p.locator('#homeTab').click(); await p.locator('.selection-actions .btn-primary').click();
        await p.locator('#startBatch').click();
        await p.waitForFunction(() => document.querySelector('.batch-feedback').textContent.includes('登记成员'));
        assert.deepEqual(await p.evaluate(() => gatewayCalls.find(x => x.name === 'control.batch.begin').payload.targetUids), ['a']);
        await p.locator('.modal [data-close]').click();
        // Recheck settings on confirm: a concurrent binding change blocks this request.
        await p.locator('.selection-actions .btn-primary').click();
        const before = await p.evaluate(() => gatewayCalls.filter(x => x.name === 'control.batch.begin').length);
        await p.evaluate(() => { testMembers[0].onlineHoeingGroupNames = ['特殊<任务>"名称']; emit('control.playersUpdated', { full: true, players: testMembers }); });
        await p.locator('#startBatch').click();
        assert.match(await p.locator('.batch-feedback').textContent(), /已改变/);
        assert.equal(await p.evaluate(() => gatewayCalls.filter(x => x.name === 'control.batch.begin').length), before);
        await p.locator('.modal [data-close]').click();
        // Per-member quick commands may mix group and one-dragon; normal ordered bindings may not.
        await p.evaluate(() => {
            testMembers[0].quickCommands['一键精英'] = 'GROUP:精英';
            testMembers[1].quickCommands['一键精英'] = 'ONEDRAGON:收尾';
            emit('control.playersUpdated', { full: true, players: testMembers });
        });
        await p.locator('[data-ui="select"][data-uid="b"]').click();
        await p.locator('.quick-btn[data-key="精英"]').click();
        assert.equal(await p.locator('#startBatch').isEnabled(), true);
        await p.locator('.modal [data-close]').click();
        for (const width of [360, 390, 1280]) {
            await p.setViewportSize({ width, height: 900 });
            assert.equal(await p.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
            if (width === 1280) assert.equal(await p.locator('#memberList').evaluate(el => getComputedStyle(el).gridTemplateColumns.split(' ').length), 4);
        }
        assert.deepEqual(f.errors, []);
        console.log('PASS real DOM: rejected login, selected scope, settings/ack, binding types, policy failure, unsaved edits, typed starts, history, batch confirmation/race, responsive 360/390/1280. Inert gateway.');
    } finally { await f.close(); }
})().catch(err => { console.error(err); process.exitCode = 1; });
