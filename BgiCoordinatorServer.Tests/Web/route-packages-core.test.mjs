import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

// Pure browser implementation checks only. Server ZIP validation, persistence and
// authentication must be exercised separately against the real ASP.NET application.
const source = readFileSync(new URL('../../BgiCoordinatorServer/wwwroot/route-packages-core.js', import.meta.url), 'utf8');
const scope = vm.createContext({ TextEncoder, Blob, DOMException });
vm.runInContext(source, scope, { filename: 'route-packages-core.js' });
const C = scope.RoutePackages;
const plain = value => JSON.parse(JSON.stringify(value));
const route = JSON.stringify({ info: { name: '中文线路' }, positions: [{ x: 1.25, y: -20 }] });
const file = (path, text = route) => Object.defineProperty(new Blob([text]), 'webkitRelativePath', { value: path });
const plannedPaths = files => Array.from(C.planFiles(files).entries, entry => entry.path);
const id = 'a'.repeat(32), destination = 'b'.repeat(32);

function referenceCrc(bytes) {
    let crc = 0xffffffff;
    for (const byte of bytes) {
        crc ^= byte;
        for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
    }
    return (crc ^ 0xffffffff) >>> 0;
}

// Inspect ZIP bytes independently, not by calling the production packer's helpers.
async function unpackStore(blob) {
    const buffer = await blob.arrayBuffer(), view = new DataView(buffer), bytes = new Uint8Array(buffer);
    const end = buffer.byteLength - 22;
    assert.equal(view.getUint32(end, true), 0x06054b50);
    assert.equal(view.getUint16(end + 4, true), 0);
    assert.equal(view.getUint16(end + 6, true), 0);
    assert.equal(view.getUint16(end + 20, true), 0);
    const count = view.getUint16(end + 10, true), result = new Map();
    assert.equal(view.getUint16(end + 8, true), count);
    let central = view.getUint32(end + 16, true), expectedLocal = 0;
    assert.equal(central + view.getUint32(end + 12, true), end);
    for (let i = 0; i < count; i++) {
        assert.equal(view.getUint32(central, true), 0x02014b50);
        assert.equal(view.getUint16(central + 8, true), 0x0800, 'UTF-8 filenames, no encryption');
        assert.equal(view.getUint16(central + 10, true), 0, 'STORE, not claimed compression');
        const crc = view.getUint32(central + 16, true), size = view.getUint32(central + 24, true);
        assert.equal(view.getUint32(central + 20, true), size);
        const nameLength = view.getUint16(central + 28, true), local = view.getUint32(central + 42, true);
        assert.equal(local, expectedLocal);
        const nameBytes = bytes.slice(central + 46, central + 46 + nameLength);
        const path = new TextDecoder('utf-8', { fatal: true }).decode(nameBytes);
        assert.equal(view.getUint32(local, true), 0x04034b50);
        assert.equal(view.getUint16(local + 6, true), 0x0800);
        assert.equal(view.getUint16(local + 8, true), 0);
        assert.equal(view.getUint16(local + 12, true), 33, 'fixed DOS date, no filesystem timestamps');
        assert.equal(view.getUint32(local + 14, true), crc);
        assert.equal(view.getUint32(local + 18, true), size);
        assert.equal(view.getUint32(local + 22, true), size);
        assert.equal(view.getUint16(local + 26, true), nameLength);
        assert.equal(view.getUint16(local + 28, true), 0);
        assert.deepEqual(bytes.slice(local + 30, local + 30 + nameLength), nameBytes);
        const data = bytes.slice(local + 30 + nameLength, local + 30 + nameLength + size);
        assert.equal(referenceCrc(data), crc, path + ' must have a genuine CRC32');
        result.set(path, data);
        expectedLocal = local + 30 + nameLength + size;
        central += 46 + nameLength;
    }
    assert.equal(expectedLocal, view.getUint32(end + 16, true));
    assert.equal(central, end);
    return result;
}

test('normalizes selected Chinese route folders and both standard layouts', () => {
    assert.deepEqual(plannedPaths([file('纳塔线路/第一条.json')]), ['builtin/纳塔线路/第一条.json']);
    assert.deepEqual(plannedPaths([file('线路库/纳塔/第一条.json')]), ['builtin/纳塔/第一条.json']);
    assert.deepEqual(plannedPaths([file('builtin/纳塔/第一条.json')]), ['builtin/纳塔/第一条.json']);
    assert.deepEqual(plannedPaths([file('线路库/builtin/纳塔/A变体/甲_a.json'), file('线路库/pathing/璃月/乙.json')]),
        ['builtin/纳塔/A变体/甲_a.json', 'pathing/璃月/乙.json']);
    assert.throws(() => C.planFiles([file('builtin/第一条.json')]), /线路目录/);
    assert.throws(() => C.planFiles([file('线路库/pathing/璃月/乙.json')]), /builtin/);
    assert.deepEqual(plannedPaths([file('线路库/builtin/甲/一.json'), file('线路库/pathing/一.json')]),
        ['builtin/甲/一.json', 'pathing/一.json']);
    assert.throws(() => C.planFiles([file('甲/一.json'), file('乙/二.json')]), /一个根/);
    assert.throws(() => C.planFiles([file('一.json')]), /选择文件夹/);
    assert.throws(() => C.planFiles([]), /文件夹为空/);
});

test('CRC vector and Chinese ZIP headers/data are correct; JSON5 is byte-for-byte preserved', async () => {
    const bytes = new TextEncoder().encode('123456789');
    assert.equal((C.crc32(bytes) ^ 0xffffffff) >>> 0, 0xcbf43926);
    let running = C.crc32(bytes.slice(0, 3));
    running = C.crc32(bytes.slice(3), running);
    assert.equal((running ^ 0xffffffff) >>> 0, 0xcbf43926);
    const json5 = "\ufeff// 原样保留中文与注释\r\n{ref: '../公用/control.json5', global_cover: {name: '路径\\\\原名',},}\r\n";
    const files = [file('线路库/builtin/纳塔/A变体/第一条_a.json'), file('线路库/builtin/纳塔/A变体/control.json5', json5)];
    const plan = C.planFiles(files), progress = [];
    const blob = await C.makeZip(plan, { onProgress: p => progress.push(p) });
    assert.equal(blob.type, 'application/zip');
    assert.equal(blob.size, plan.zipBytes);
    const entries = await unpackStore(blob);
    assert.deepEqual(Array.from(entries.keys()), ['builtin/纳塔/A变体/control.json5', 'builtin/纳塔/A变体/第一条_a.json']);
    assert.deepEqual(entries.get('builtin/纳塔/A变体/control.json5'), new TextEncoder().encode(json5));
    assert.deepEqual(entries.get('builtin/纳塔/A变体/第一条_a.json'), new TextEncoder().encode(route));
    assert.equal(progress.at(-1).processed, plan.expandedBytes);
    assert.ok(progress.every(p => p.phase === 'pack'));
});

test('route JSON supports server comments/trailing commas without changing uploaded bytes', async () => {
    const text = '\ufeff{ // 说明\r"info":{"name":"https://host/a/*b*/?q=\\\"[{}]\\\"",},\n"positions":[{"x":1,"y":2,},], /*尾注*/ }';
    assert.doesNotThrow(() => C.validateRoute(text, 'builtin/中文/甲.json'));
    const entries = await unpackStore(await C.makeZip(C.planFiles([file('中文/甲.json', text)])));
    assert.deepEqual(entries.get('builtin/中文/甲.json'), new TextEncoder().encode(text));
    assert.throws(() => C.validateRoute('{/*未闭合', '甲.json'), /注释未闭合/);
});

test('rejects dangerous Windows/ZIP paths, including reserved superscript aliases', () => {
    const bad = ['/绝对/一.json', 'C:/甲/一.json', '甲\\乙/一.json', '甲/../一.json', '甲/./一.json',
        '甲//一.json', '甲/一.json:stream', '甲/CON.json', '甲/LPT¹.json', '甲/com0.json', '甲/末尾./一.json',
        '甲/末尾 /一.json', '甲/坏\u0085名.json', '甲/\u0000.json', '甲/问?.json', '甲/' + '长'.repeat(181) + '.json',
        ['甲'.repeat(170), '乙'.repeat(170), '丙'.repeat(170), '一.json'].join('/')];
    for (const path of bad) assert.throws(() => C.checkPath(path), undefined, path);
});

test('rejects private directories and unsupported files, even when selected root would be stripped', () => {
    for (const path of ['User/纳塔/一.json', 'accounts/一.json', '库/纳塔/settings/一.json', '库/node_modules/一.json',
        '库/纳塔/main.js', '库/纳塔/desktop.ini', '库/纳塔/readme.md', '库/纳塔/control.JSON5']) {
        assert.throws(() => C.planFiles([file(path)]), undefined, path);
    }
});

test('detects implicit directory case/Unicode aliases and file-directory collisions', () => {
    for (const paths of [
        ['库/A/一.json', '库/a/二.json'],
        ['库/é/一.json', '库/e\u0301/二.json'],
        ['库/甲/É.json', '库/甲/e\u0301.json'],
        ['库/甲/一.json', '库/甲/一.json/二.json'],
        ['库/甲/一.json/二.json', '库/甲/一.json'],
        ['库/甲/一.json', '库/甲/一.json']
    ]) assert.throws(() => C.planFiles(paths.map(path => file(path))), /碰撞|重名|占用/);
});

test('bounds file count, individual reads and STORE output before reading any content', () => {
    const metadata = (path, size) => ({ relativePath: path, file: { size, text() { assert.fail('planning must not read file contents'); } } });
    assert.throws(() => C.planFiles(Array.from({ length: 10001 }, (_, i) => metadata(`库/甲/${i}.json`, 1))), /10000/);
    for (const size of [0, -1, NaN, Infinity, 1.5, C.MAX_FILE_BYTES + 1])
        assert.throws(() => C.planFiles([metadata('甲/一.json', size)]), /16 MiB/);
    assert.throws(() => C.planFiles(Array.from({ length: 9 }, (_, i) => metadata(`库/甲/${i}.json`, 15 * 1024 * 1024))), /128 MiB/);
});

test('basic route semantics and depth are bounded; controls defer merge-dependent semantics', async () => {
    for (const text of ['[]', '{}', '{"positions":[]}', '{"positions":[{"x":"1","y":2}]}', '{"positions":[{"x":1e999,"y":2}]}', '{"positions":[null]}'])
        assert.throws(() => C.validateRoute(text, '甲.json'), /对象|positions|坐标/);
    assert.throws(() => C.validateRoute('{"info":' + '['.repeat(65) + '0' + ']'.repeat(65) + '}', '甲.json'), /64 层/);
    assert.doesNotThrow(() => C.validateRoute('{"positions":[{"x":1,"y":2}],"info":{"name":"' + '['.repeat(100) + '"}}', '甲.json'));
    const dependent = [file('库/甲/一.json', '{}'), file('库/甲/control.json5', "{global_cover:{positions:[{x:1,y:2}]}}")];
    assert.ok((await C.makeZip(C.planFiles(dependent))).size > 0, 'local acceptance does not claim server dependency validation');
    await assert.rejects(C.makeZip(C.planFiles([dependent[0]])), /positions/);
});

test('cancels before any reads and after a bounded CRC chunk, without producing an uploadable Blob', async () => {
    const early = new AbortController(); early.abort();
    const unread = file('甲/一.json');
    Object.defineProperty(unread, 'text', { value() { assert.fail('already-cancelled operation must not read'); } });
    await assert.rejects(C.makeZip(C.planFiles([unread]), { signal: early.signal }), { name: 'AbortError' });
    const reading = new AbortController(), pending = file('甲/一.json');
    Object.defineProperty(pending, 'text', { value: async () => { reading.abort(); return 'not-json'; } });
    await assert.rejects(C.makeZip(C.planFiles([pending]), { signal: reading.signal }), { name: 'AbortError' });
    const controller = new AbortController(), slices = [];
    const large = file('甲/大.json', JSON.stringify({ positions: [{ x: 1, y: 2 }], note: '中'.repeat(800000) }));
    const slice = large.slice.bind(large);
    Object.defineProperty(large, 'slice', { value(start, end) { slices.push([start, end]); return slice(start, end); } });
    let progress = 0;
    await assert.rejects(C.makeZip(C.planFiles([large]), { signal: controller.signal, onProgress() { progress++; controller.abort(); } }), { name: 'AbortError' });
    assert.equal(progress, 1);
    assert.ok(slices.every(([start, end]) => end - start <= 1024 * 1024));
});

test('file read errors reject packing rather than returning a partial archive', async () => {
    const broken = file('甲/一.json');
    Object.defineProperty(broken, 'text', { value: async () => { throw new Error('本地文件已移动'); } });
    await assert.rejects(C.makeZip(C.planFiles([broken])), /本地文件已移动/);
});

const historical = { id, files: [
    { path: 'builtin/甲/一.json' }, { path: 'builtin/甲/control.json5' },
    { path: 'builtin/甲/A变体/二_a.json' }, { path: 'builtin/乙/三.json' }, { path: 'pathing/璃月/四.json' }
] };

test('restoration offers real directory prefixes and compresses only explicitly selected ancestors', () => {
    const options = plain(C.restoreOptions(historical));
    assert.deepEqual(options.find(option => option.path === 'builtin/甲'), { path: 'builtin/甲', directory: true, fileCount: 3 });
    assert.deepEqual(plain(C.restoreSelection(historical, ['builtin/甲/一.json', 'builtin/甲/', 'builtin/甲/A变体', 'builtin/甲'])), ['builtin/甲']);
    const filesOnly = ['builtin/甲/一.json', 'builtin/甲/control.json5', 'builtin/甲/A变体/二_a.json'];
    assert.deepEqual(plain(C.restoreSelection(historical, filesOnly)), [...filesOnly].sort(), 'do not infer a parent which could remove destination-only files');
    assert.throws(() => C.restoreSelection(historical, []), /请先选择/);
    assert.throws(() => C.restoreSelection(historical, ['builtin/丙']), /不属于/);
    assert.throws(() => C.restoreSelection(historical, ['builtin/甲/../乙']), /不安全/);
});

test('pure upload controls enforce confirmation, packing, token capability and uncertain-result latch', () => {
    const base = { phase: 'idle', plan: {}, blob: null, uploaded: false, uploadUnknown: false, privacyConfirmed: true, admin: false, token: '' };
    assert.deepEqual(plain(C.uploadControls(base)), { busy: false, canPack: true, canUpload: false });
    assert.equal(C.uploadControls({ ...base, blob: {}, token: ' upload-only ' }).canUpload, true);
    assert.equal(C.uploadControls({ ...base, blob: {}, admin: true }).canUpload, true);
    for (const override of [{ phase: 'pack' }, { phase: 'upload' }, { privacyConfirmed: false }, { uploaded: true }, { uploadUnknown: true }, { plan: null }]) {
        const controls = C.uploadControls({ ...base, blob: {}, token: 'token', ...override });
        assert.equal(controls.canPack, false); assert.equal(controls.canUpload, false);
    }
});

test('admin mutation requests use CAS/reason where consumed; DELETE has neither body nor fake reason', () => {
    const deletion = plain(C.buildActionRequest({ kind: 'delete', pkg: { id } }, ''));
    assert.deepEqual(deletion, { path: '/api/admin/routes/' + id, method: 'DELETE' });
    const publish = C.buildActionRequest({ kind: 'publish', pkg: { id }, revision: 8 }, ' 修复线路 ');
    assert.deepEqual(JSON.parse(publish.body), { expectedRevision: 8, reason: '修复线路' });
    assert.equal(publish.path, '/api/admin/routes/' + id + '/publish');
    assert.throws(() => C.buildActionRequest({ kind: 'publish', pkg: { id }, revision: 8 }, ''), /原因/);
    assert.throws(() => C.buildActionRequest({ kind: 'rollback', pkg: { id }, revision: -1 }, '回滚'), /修订号/);
    assert.throws(() => C.buildActionRequest({ kind: 'publish', pkg: { id: '../x' }, revision: 1 }, '原因'), /ID/);
    const restore = C.buildActionRequest({ kind: 'restore', pkg: historical, destination, revision: 8,
        paths: ['builtin/甲/一.json', 'builtin/甲'] }, '恢复目录');
    assert.deepEqual(JSON.parse(restore.body), { expectedRevision: 8, reason: '恢复目录', sourceId: id, paths: ['builtin/甲'] });
    assert.equal(restore.path, '/api/admin/routes/' + destination + '/restore');
});

test('UTF-8 restore selection uses the real 2 MiB limit, not the smaller publish limit', () => {
    const large = { id, files: Array.from({ length: 120 }, (_, i) => ({ path: `builtin/中文/${'线'.repeat(120)}${i}.json` })) };
    const action = { kind: 'restore', pkg: large, destination, revision: 2, paths: large.files.map(file => file.path) };
    const accepted = C.buildActionRequest(action, '恢复');
    assert.equal(JSON.parse(accepted.body).paths.length, 120);
    assert.ok(new TextEncoder().encode(accepted.body).length > C.MAX_ACTION_BYTES);
    const huge = { id, files: Array.from({ length: 6200 }, (_, i) => ({ path: `builtin/中文/${'线'.repeat(120)}${i}.json` })) };
    const tooLarge = { ...action, pkg: huge, paths: huge.files.map(file => file.path) };
    assert.throws(() => C.buildActionRequest(tooLarge, '恢复'), /2 MiB/);
    const command = C.buildActionRequest({ ...tooLarge, paths: ['builtin/中文', ...tooLarge.paths] }, '恢复');
    assert.deepEqual(JSON.parse(command.body).paths, ['builtin/中文']);
    assert.ok(new TextEncoder().encode(command.body).length < C.MAX_RESTORE_BYTES);
});

test('variant descriptions are preserved, bounded and never counted or parsed as routes', async () => {
    const files = [file('库/甲/A变体/一_a.json'), file('库/甲/A变体/变体说明.txt', '中文说明\r\n只用于展示。')];
    const plan = C.planFiles(files);
    assert.equal(plan.routeCount, 1);
    const entries = await unpackStore(await C.makeZip(plan));
    assert.deepEqual(entries.get('builtin/甲/A变体/变体说明.txt'), new TextEncoder().encode('中文说明\r\n只用于展示。'));
    assert.equal(C.planFiles([files[0], file('库/甲/A变体/变体说明.txt', '')]).routeCount, 1);
    assert.throws(() => C.planFiles([file('库/甲/变体说明.txt', '说明')]), /只允许/);
    assert.throws(() => C.planFiles([file('库/甲/A变体/变体说明.txt', '说明')]), /至少需要一条/);
    assert.throws(() => C.planFiles([files[0], file('库/甲/A变体/变体说明.txt', 'a'.repeat(16385))]), /16 KiB/);
    await assert.rejects(C.makeZip(C.planFiles([files[0], file('库/甲/A变体/变体说明.txt', '坏\u0001文本')])), /普通 UTF-8/);
});
