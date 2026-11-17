/* Browser/Worker shared pure logic. STORE ZIP references File slices; never buffers the whole library. */
(function (root) {
    'use strict';
    const MAX_ARCHIVE_BYTES = 128 * 1024 * 1024;
    const MAX_EXPANDED_BYTES = 512 * 1024 * 1024;
    const MAX_FILES = 10000;
    const MAX_FILE_BYTES = 16 * 1024 * 1024;
    const MAX_ACTION_BYTES = 32768;
    const MAX_RESTORE_BYTES = 2 * 1024 * 1024;
    const privateDirectory = /^(user|users|accounts?|config|configs|settings|scripts?|javascript|node_modules|\.git|\.claude)$/i;
    const encoder = new TextEncoder();
    const crcTable = Uint32Array.from({ length: 256 }, (_, n) => {
        for (let k = 0; k < 8; k++) n = (n & 1) ? 0xedb88320 ^ (n >>> 1) : n >>> 1;
        return n >>> 0;
    });
    function crc32(bytes, state = 0xffffffff) {
        for (const byte of bytes) state = crcTable[(state ^ byte) & 255] ^ (state >>> 8);
        return state >>> 0;
    }
    function checkPath(path) {
        if (typeof path !== 'string' || !path || path.length > 512 || path.includes('\\') || path.startsWith('/'))
            throw new Error('文件路径不合法：' + path);
        const segments = path.split('/');
        for (const part of segments) {
            if (!part || part.length > 180 || part === '.' || part === '..' || /[<>:"|?*\u0000-\u001f\u007f-\u009f]/.test(part) || /[. ]$/.test(part) ||
                /^(con|prn|aux|nul|com[0-9¹²³]|lpt[0-9¹²³])(?:\.|$)/i.test(part))
                throw new Error('不安全或 Windows 不支持的路径：' + path);
        }
        return segments;
    }
    function planFiles(input) {
        const files = Array.from(input || []);
        if (!files.length) throw new Error('文件夹为空，请选择包含线路 JSON 的目录。');
        if (files.length > MAX_FILES) throw new Error('文件数量超过 10000 个上限。');
        const inputs = files.map(value => ({ file: value.file || value, parts: checkPath(value.relativePath ?? value.webkitRelativePath) }));
        if (inputs.some(item => item.parts.length < 2)) throw new Error('请选择文件夹，而不是单个文件。');
        const rootName = inputs[0].parts[0];
        if (inputs.some(item => item.parts[0] !== rootName)) throw new Error('一次只能选择一个根文件夹。');
        if (inputs.some(item => item.parts.slice(0, -1).some(part => privateDirectory.test(part))))
            throw new Error('所选目录包含用户配置、账户或脚本目录，请只选择线路。');
        const directRoutes = inputs.some(item => item.parts.length === 2 && /\.json$/i.test(item.parts[1]));
        const explicitBuiltinRoot = rootName === 'builtin';
        const explicitLayout = !explicitBuiltinRoot && inputs.some(item => ['builtin', 'pathing'].includes(item.parts[1]));
        const seen = new Set(), canonical = new Map();
        let expandedBytes = 0, zipBytes = 22, routeCount = 0;
        const entries = inputs.map(({ file, parts }) => {
            let path;
            if (explicitBuiltinRoot) path = parts.join('/');
            else if (explicitLayout) path = parts.slice(1).join('/');
            else path = ['builtin', ...(directRoutes ? parts : parts.slice(1))].join('/');
            const target = checkPath(path);
            if (target[0] !== 'builtin' && target[0] !== 'pathing') throw new Error('标准布局只允许 builtin/ 和 pathing/：' + path);
            if (target.length < (target[0] === 'builtin' ? 3 : 2)) throw new Error('builtin 下必须先有线路目录：' + path);
            const key = path.normalize('NFC').toLowerCase();
            if (seen.has(key)) throw new Error('存在大小写或 Unicode 重名文件：' + path);
            seen.add(key);
            for (let length = 1; length <= target.length; length++) {
                const prefix = target.slice(0, length).join('/'), prefixKey = prefix.normalize('NFC').toLowerCase();
                const prior = canonical.get(prefixKey), directory = length < target.length;
                if (prior && (prior.path !== prefix || prior.directory !== directory))
                    throw new Error('目录大小写/Unicode 碰撞，或文件占用了目录路径：' + path);
                canonical.set(prefixKey, { path: prefix, directory });
            }
            const control = target[target.length - 1] === 'control.json5';
            const description = target[target.length - 1] === '变体说明.txt' && target.length >= 4 && /^[ABCD]变体$/.test(target[target.length - 2]);
            if (!control && !description && !/\.json$/i.test(path)) throw new Error('只允许线路 JSON、control.json5 和变体目录中的变体说明.txt，不会悄悄跳过文件：' + path);
            const fileLimit = description ? 16384 : MAX_FILE_BYTES;
            if (!Number.isSafeInteger(file.size) || file.size < 0 || (!description && file.size === 0) || file.size > fileLimit)
                throw new Error(description ? '变体说明大小无效或超过 16 KiB：' + path : '单文件为空、大小无效或超过 16 MiB：' + path);
            if (!control && !description) routeCount++;
            expandedBytes += file.size;
            zipBytes += 76 + encoder.encode(path).length * 2 + file.size;
            return { path, sizeBytes: file.size, control, description, file };
        });
        if (!entries.some(item => item.path.startsWith('builtin/') && !item.control && !item.description)) throw new Error('builtin/ 下至少需要一条线路 JSON。');
        if (expandedBytes > MAX_EXPANDED_BYTES) throw new Error('展开后超过 512 MiB 上限。');
        if (zipBytes > MAX_ARCHIVE_BYTES) throw new Error('STORE ZIP 预计超过 128 MiB，请减少线路；本工具不会伪称已压缩。');
        entries.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
        return { rootName, entries, routeCount, expandedBytes, zipBytes,
            layout: explicitLayout || explicitBuiltinRoot ? '标准 builtin/pathing 布局' : directRoutes ? '根目录含线路，保留所选目录名' : '保留所选目录的子目录' };
    }
    function routeJson(text, path) {
        // Match server JSON comments/trailing commas without eval or modifying ZIP bytes.
        // JSON5 control files are never normalized here; the server resolves their dependencies.
        let depth = 0, start = 0;
        const pieces = [];
        for (let i = 0; i < text.length; i++) {
            const char = text[i];
            if (char === '"') {
                for (i++; i < text.length; i++) {
                    if (text[i] === '\\') i++;
                    else if (text[i] === '"') break;
                }
            } else if (char === '/' && (text[i + 1] === '/' || text[i + 1] === '*')) {
                const line = text[i + 1] === '/';
                let end = i + 2;
                if (line) { while (end < text.length && text[end] !== '\n' && text[end] !== '\r') end++; }
                else {
                    end = text.indexOf('*/', end);
                    if (end < 0) throw new Error('线路 JSON 注释未闭合：' + path);
                    end += 2;
                }
                pieces.push(text.slice(start, i), ' '); start = end; i = end - 1;
                if (pieces.length > 200000) throw new Error('线路 JSON 注释数量过多：' + path);
            } else if (char === '{' || char === '[') {
                if (++depth > 64) throw new Error('线路 JSON 嵌套超过 64 层：' + path);
            } else if (char === '}' || char === ']') depth--;
        }
        pieces.push(text.slice(start));
        // At this point comments are removed. Preserve strings, removing only commas
        // followed by an array/object terminator outside those strings.
        const cleaned = pieces.join('').replace(/^﻿/, '');
        const spans = []; start = 0;
        for (let i = 0; i < cleaned.length; i++) {
            if (cleaned[i] === '"') {
                for (i++; i < cleaned.length; i++) { if (cleaned[i] === '\\') i++; else if (cleaned[i] === '"') break; }
            } else if (cleaned[i] === ',') {
                let next = i + 1; while (/\s/.test(cleaned[next] || '') && next < cleaned.length) next++;
                if (cleaned[next] === '}' || cleaned[next] === ']') { spans.push(cleaned.slice(start, i)); start = i + 1; }
                if (spans.length > 500000) throw new Error('线路 JSON 尾逗号数量过多：' + path);
            }
        }
        spans.push(cleaned.slice(start));
        try { return JSON.parse(spans.join('')); } catch { throw new Error('线路不是有效 JSON：' + path); }
    }
    function validateRoute(text, path, { deferPositions = false } = {}) {
        const data = routeJson(text, path);
        if (!data || typeof data !== 'object' || Array.isArray(data)) throw new Error('线路 JSON 根必须是对象：' + path);
        // A control merge may supply/replace positions. Do not pretend this local check
        // has resolved it, or reject an otherwise valid server-side merged route.
        if (deferPositions) return;
        if (!Array.isArray(data.positions) || !data.positions.length || data.positions.length > 100000)
            throw new Error('不是线路文件（需要 1–100000 个 positions）：' + path);
        for (const position of data.positions) {
            if (!position || typeof position !== 'object' || !Number.isFinite(position.x) || !Number.isFinite(position.y))
                throw new Error('线路坐标 x/y 必须是有限数值：' + path);
        }
    }
    function abortIfNeeded(signal) {
        if (signal?.aborted) throw new DOMException('已取消本机打包', 'AbortError');
    }
    async function makeZip(plan, { signal, onProgress = () => {} } = {}) {
        const parts = [], central = [];
        let offset = 0, processed = 0;
        const deferPositions = plan.entries.some(entry => entry.control);
        for (let index = 0; index < plan.entries.length; index++) {
            abortIfNeeded(signal);
            const entry = plan.entries[index], name = encoder.encode(entry.path);
            if (!entry.control) {
                const text = await entry.file.text();
                abortIfNeeded(signal);
                if (entry.description) {
                    if (/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/.test(text)) throw new Error('变体说明须为普通 UTF-8 文本：' + entry.path);
                } else validateRoute(text, entry.path, { deferPositions });
            }
            abortIfNeeded(signal);
            let crc = 0xffffffff;
            // One bounded chunk at a time. File slices in the output remain Blob references.
            for (let start = 0; start < entry.file.size; start += 1024 * 1024) {
                abortIfNeeded(signal);
                const bytes = new Uint8Array(await entry.file.slice(start, start + 1024 * 1024).arrayBuffer());
                abortIfNeeded(signal); crc = crc32(bytes, crc);
                processed += bytes.byteLength;
                onProgress({ phase: 'pack', processed, total: plan.expandedBytes, index, count: plan.entries.length, path: entry.path });
            }
            crc = (crc ^ 0xffffffff) >>> 0;
            const local = new Uint8Array(30 + name.length), lv = new DataView(local.buffer);
            lv.setUint32(0, 0x04034b50, true); lv.setUint16(4, 20, true); lv.setUint16(6, 0x0800, true);
            lv.setUint16(12, 33, true); // 1980-01-01; deterministic and no local timestamp disclosure.
            lv.setUint32(14, crc, true); lv.setUint32(18, entry.sizeBytes, true); lv.setUint32(22, entry.sizeBytes, true);
            lv.setUint16(26, name.length, true); local.set(name, 30);
            parts.push(local, entry.file.slice());
            const record = new Uint8Array(46 + name.length), cv = new DataView(record.buffer);
            cv.setUint32(0, 0x02014b50, true); cv.setUint16(4, 20, true); cv.setUint16(6, 20, true); cv.setUint16(8, 0x0800, true);
            cv.setUint16(14, 33, true); cv.setUint32(16, crc, true); cv.setUint32(20, entry.sizeBytes, true); cv.setUint32(24, entry.sizeBytes, true);
            cv.setUint16(28, name.length, true); cv.setUint32(42, offset, true); record.set(name, 46);
            central.push(record); offset += local.length + entry.sizeBytes;
        }
        abortIfNeeded(signal);
        const centralSize = central.reduce((sum, part) => sum + part.length, 0);
        const end = new Uint8Array(22), ev = new DataView(end.buffer);
        ev.setUint32(0, 0x06054b50, true); ev.setUint16(8, plan.entries.length, true); ev.setUint16(10, plan.entries.length, true);
        ev.setUint32(12, centralSize, true); ev.setUint32(16, offset, true);
        const blob = new Blob([...parts, ...central, end], { type: 'application/zip' });
        if (blob.size !== plan.zipBytes || blob.size > MAX_ARCHIVE_BYTES) throw new Error('ZIP 大小校验失败，未生成可上传结果。');
        return blob;
    }
    function publicationRevision(catalog) {
        const revision = catalog?.current?.revision ?? 0;
        if (!Number.isSafeInteger(revision) || revision < 0) throw new Error('服务器发布修订号无效，请刷新。');
        return revision;
    }
    function buildMutation(catalog, reason) {
        reason = String(reason || '').trim();
        if (!reason || reason.length > 500) throw new Error('请填写 1–500 字操作原因。');
        return { expectedRevision: publicationRevision(catalog), reason };
    }
    function restoreOptions(source) {
        const options = new Map();
        for (const file of source.files || []) {
            const parts = checkPath(file.path);
            for (let length = 1; length <= parts.length; length++) {
                const path = parts.slice(0, length).join('/');
                if (!options.has(path)) options.set(path, { path, directory: length < parts.length, fileCount: 0 });
                options.get(path).fileCount++;
            }
        }
        return [...options.values()].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
    }
    function restoreSelection(source, paths) {
        const available = new Set(restoreOptions(source).map(option => option.path));
        const unique = [...new Set(paths.map(path => typeof path === 'string' ? path.replace(/\/+$/, '') : path))].sort();
        if (!unique.length) throw new Error('请先选择要恢复的线路文件或目录。');
        const selected = new Set(), result = [];
        for (const path of unique) {
            const parts = checkPath(path);
            if (!available.has(path)) throw new Error('恢复路径不属于所选历史版本：' + path);
            // Only compress explicitly chosen directories. Inferring a directory from all its
            // source files would also remove unrelated destination files under that prefix.
            if (parts.some((_, i) => selected.has(parts.slice(0, i).join('/')))) continue;
            selected.add(path); result.push(path);
        }
        return result;
    }
    function uploadControls({ phase, plan, blob, uploaded, uploadUnknown, privacyConfirmed, admin, token }) {
        const busy = phase !== 'idle', allowed = !!plan && privacyConfirmed && !busy && !uploaded && !uploadUnknown;
        return { busy, canPack: allowed && !blob, canUpload: allowed && !!blob && (admin || !!String(token || '').trim()) };
    }
    function buildActionRequest(action, reason) {
        const validId = id => typeof id === 'string' && /^[a-f0-9]{32}$/.test(id);
        if (!validId(action.pkg?.id)) throw new Error('版本 ID 格式无效。');
        const base = '/api/admin/routes/';
        // DELETE has no reason/CAS body on the server. Never imply it records either.
        if (action.kind === 'delete') return { path: base + action.pkg.id, method: 'DELETE' };
        let payload = buildMutation({ current: { revision: action.revision } }, reason), id = action.pkg.id;
        if (action.kind === 'restore') {
            if (!validId(action.destination)) throw new Error('请选择有效目标草稿。');
            payload = { ...payload, sourceId: id, paths: restoreSelection(action.pkg, action.paths) }; id = action.destination;
        } else if (!['publish', 'rollback'].includes(action.kind)) throw new Error('不支持的管理操作。');
        const body = JSON.stringify(payload);
        const limit = action.kind === 'restore' ? MAX_RESTORE_BYTES : MAX_ACTION_BYTES;
        if (encoder.encode(body).length > limit)
            throw new Error('操作超出请求上限（恢复 2 MiB，其他操作 32 KiB），请改选完整父目录或分批恢复到新草稿；未发送请求。');
        return { path: base + id + '/' + action.kind, method: 'POST', body };
    }
    function formatBytes(value) {
        if (!Number.isFinite(value) || value < 0) return '未知';
        if (value < 1024) return value + ' B';
        return (value / (value < 1048576 ? 1024 : 1048576)).toFixed(2) + (value < 1048576 ? ' KiB' : ' MiB');
    }
    root.RoutePackages = Object.freeze({ MAX_ARCHIVE_BYTES, MAX_EXPANDED_BYTES, MAX_FILES, MAX_FILE_BYTES, MAX_ACTION_BYTES, MAX_RESTORE_BYTES,
        crc32, checkPath, planFiles, validateRoute, makeZip, publicationRevision, buildMutation,
        restoreOptions, restoreSelection, uploadControls, buildActionRequest, formatBytes });
})(globalThis);
