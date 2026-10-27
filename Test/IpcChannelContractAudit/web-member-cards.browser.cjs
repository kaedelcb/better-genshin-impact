// Verify real page layout with long/short versions, offline state, and selection.
const assert = require('node:assert/strict');
const path = require('node:path');
const { fixture } = require('./web-gengdi-fixture.cjs');
const output = process.env.GENGDI_CARD_SCREEN_DIR;
const frames = page => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
function aligned(values, label) {
    assert(Math.max(...values) - Math.min(...values) < 1.1, label + ': ' + values.join(', '));
}
async function cardMetrics(page) {
    return page.locator('.member-card').evaluateAll(cards => cards.map(card => {
        const bounds = selector => {
            const rect = (selector === ':scope' ? card : card.querySelector(selector)).getBoundingClientRect();
            return { top: rect.top, bottom: rect.bottom, left: rect.left, right: rect.right };
        };
        const version = card.querySelector('.member-version');
        return { card: bounds(':scope'), header: bounds('.member-head'), version: bounds('.member-version-row'),
            badge: bounds('.presence'), task: bounds('.status-row'), foot: bounds('.member-foot'),
            clipped: version.scrollWidth > version.clientWidth || version.scrollHeight > version.clientHeight,
            avatarFilter: getComputedStyle(card.querySelector('.avatar')).filter,
            avatarImage: card.querySelector('.avatar').complete && card.querySelector('.avatar').naturalWidth > 0 };
    }));
}
async function exercise(forceFallback) {
    const f = await fixture(), p = f.page;
    try {
        if (forceFallback) {
            await p.addInitScript(() => {
                const original = CSS.supports.bind(CSS);
                CSS.supports = (...args) => args[0] === 'grid-template-rows' && args[1] === 'subgrid' ? false : original(...args);
            });
            await p.reload();
            await p.addStyleTag({ content: '.member-card{grid-template-rows:auto auto auto auto auto 1fr!important;grid-row:auto!important;margin-bottom:0!important}.member-list{row-gap:16px!important}' });
        } else assert.equal(await p.evaluate(() => CSS.supports('grid-template-rows', 'subgrid')), true);
        await p.evaluate(() => {
            testMembers[0].playerUid = '100000001';
            testMembers[1].playerUid = '100000002';
            testMembers[2].playerUid = '100000003';
        });
        await f.login();
        const realistic = '0.66.3+lcb.23.0-NexusBGI-fix2';
        await p.evaluate(version => {
            testMembers[0].bgiVersion = version;
            testMembers[1].bgiVersion = '0.66.3-alpha.1';
            testMembers[2].bgiVersion = version;
            testMembers.push({ ...testMembers[0], playerUid: '100000004', playerName: '第四个成员', online: true });
            emit('control.playersUpdated', { full: true, players: testMembers });
        }, realistic);
        await frames(p);
        await p.evaluate(() => { testMembers[1].bgiVersion = ''; emit('control.playersUpdated', { full: true, players: testMembers }); });
        assert.equal(await p.locator('[data-member-uid="100000002"] .member-version').textContent(), '版本未上报');
        await p.evaluate(() => { testMembers[1].bgiVersion = '0.66.3-alpha.1'; emit('control.playersUpdated', { full: true, players: testMembers }); });
        await frames(p);
        assert.equal(await p.locator('[data-member-uid="100000001"] .member-version').textContent(), realistic);
        assert.equal(await p.locator('[data-member-uid="100000003"] .presence').textContent(), '助手离线');
        assert.equal(await p.locator('[data-member-uid="100000001"] .presence').textContent(), '助手在线');
        const contrast = await p.evaluate(() => {
            const rgb = s => s.match(/\d+/g).slice(0, 3).map(Number);
            const luminance = c => c.map(v => { v /= 255; return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; })
                .reduce((sum, v, i) => sum + v * [0.2126, 0.7152, 0.0722][i], 0);
            const ratio = (foreground, background) => {
                const a = luminance(rgb(foreground)), b = luminance(rgb(background));
                return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
            };
            const version = document.querySelector('.member-version');
            const online = document.querySelector('.presence-online');
            const offline = document.querySelector('.presence-offline');
            return [ratio(getComputedStyle(version).color, getComputedStyle(version.closest('.member-card')).backgroundColor),
                ratio(getComputedStyle(online).color, getComputedStyle(online).backgroundColor),
                ratio(getComputedStyle(offline).color, getComputedStyle(offline).backgroundColor)];
        });
        assert(contrast.every(value => value >= 4.5), 'version and status contrast: ' + contrast.join(', '));
        assert.equal(await p.locator('[data-member-uid="100000003"] .task-name').textContent(), '当前状态待确认');
        assert.equal(await p.locator('[data-member-uid="100000003"] .member-select').isDisabled(), true);
        for (const [width, columns] of [[1920, 4], [1600, 4], [1440, 4], [1200, 4], [1199, 3], [1100, 2], [1000, 2], [390, 1], [320, 1]]) {
            await p.setViewportSize({ width, height: 1080 }); await frames(p);
            const metrics = await cardMetrics(p);
            assert.equal(await p.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, 'horizontal overflow at ' + width);
            assert(metrics.every(m => m.avatarFilter === 'none' && m.avatarImage), 'avatar should stay colored and loaded');
            assert(metrics.every(m => !m.clipped), 'version should be complete');
            const rows = new Map();
            metrics.forEach(m => { const y = Math.round(m.card.top); if (!rows.has(y)) rows.set(y, []); rows.get(y).push(m); });
            assert.equal(rows.values().next().value.length, columns, 'columns at ' + width);
            for (const row of rows.values()) {
                for (const key of ['header', 'version', 'badge', 'task', 'foot']) aligned(row.map(m => m[key].top), `${width} ${key}`);
                row.forEach(m => { assert(m.header.bottom <= m.version.top + 1); assert(m.version.bottom <= m.badge.top + 1); assert(m.badge.bottom <= m.task.top + 1); });
            }
            if (output && !forceFallback && width === 1600) {
                await p.screenshot({ path: path.join(output, 'desktop.png') });
                await p.locator('.member-list').screenshot({ path: path.join(output, 'cards.png') });
            }
        }
        await p.setViewportSize({ width: 1200, height: 1080 });
        await p.evaluate(() => {
            testMembers[0].playerName = '特别长的成员昵称也不会挤压状态信息';
            testMembers[1].bgiVersion = '0.66.3+lcb.23.0-NexusBGI-fix2-preview-build-20261028-extra-long-label-example';
            emit('control.playersUpdated', { full: true, players: testMembers });
        });
        await frames(p);
        let metrics = await cardMetrics(p);
        for (const key of ['header', 'version', 'badge', 'task', 'foot']) aligned(metrics.map(m => m[key].top), 'long text ' + key);
        assert(metrics.every(m => !m.clipped));
        assert.equal(await p.locator('[data-member-uid="100000002"] .member-version').textContent(), '0.66.3+lcb.23.0-NexusBGI-fix2-preview-build-20261028-extra-long-label-example');
        if (output && !forceFallback) await p.locator('.member-list').screenshot({ path: path.join(output, 'long-cards.png') });
        // One result row must not move another member's footer in either layout implementation.
        await p.evaluate(() => { memberCommandResults.set('100000001', { cmd: 'start_bgi', status: 'success', message: '执行端已确认', time: '12:00' }); renderMemberResults(); });
        await frames(p); metrics = await cardMetrics(p); aligned(metrics.map(m => m.foot.top), 'footer with one result');
        await p.locator('[data-ui="select"][data-uid="100000001"]').click();
        assert.equal(await p.locator('[data-ui="select"][data-uid="100000001"]').getAttribute('aria-pressed'), 'false');
        await p.locator('[data-ui="detail"][data-uid="100000001"]').first().click();
        assert.equal(await p.locator('#detailView').isVisible(), true);
        // While home is hidden, broadcasts rebuild cards with no measurable width.
        await p.evaluate(() => emit('control.playersUpdated', { full: true, players: testMembers }));
        await frames(p);
        await p.locator('#homeTab').click(); await frames(p);
        metrics = await cardMetrics(p);
        for (const key of ['header', 'version', 'badge', 'task', 'foot']) aligned(metrics.map(m => m[key].top), 'return from details ' + key);
        // A lost web connection cannot present cached member connectivity as live.
        await p.evaluate(() => transport.reconnecting());
        assert.equal(await p.locator('[data-member-uid="100000001"] .presence').textContent(), '连接待确认');
        assert.equal(await p.locator('[data-member-uid="100000003"] .presence').textContent(), '连接待确认');
        assert.equal(await p.locator('[data-member-uid="100000001"] .task-name').textContent(), '当前状态待确认');
        assert.deepEqual(f.errors, []);
        console.log('PASS member cards:', forceFallback ? 'fallback' : 'subgrid', 'long/short version, four/two/one columns, alignment, results, selection, connection state');
    } finally { await f.close(); }
}
(async () => { await exercise(false); await exercise(true); })().catch(error => { console.error(error); process.exitCode = 1; });
