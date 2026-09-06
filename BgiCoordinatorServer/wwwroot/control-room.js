// ========== 联机锄地 Web 控制端（苹果风） ==========
let connection = null;
let roomCode = '';
let password = '';
let playerName = '';
let players = [];
let lastProgress = {};  // 记录每成员的锄地进度，用于 WEB 端日志去重

const LS_CRED = 'web_hie_cred';
const LS_BIND = 'web_hie_binding';

// ---------- 工具 ----------
function log(msg) {
    const area = document.getElementById('logArea');
    const div = document.createElement('div');
    div.textContent = `[${new Date().toLocaleTimeString()}] ${msg}`;
    area.insertBefore(div, area.firstChild);
}
function showError(msg) {
    document.getElementById('loginError').textContent = msg;
}
function loadCreds() {
    try {
        const raw = localStorage.getItem(LS_CRED);
        return raw ? JSON.parse(raw) : null;
    } catch { return null; }
}
function saveCreds(c) {
    localStorage.setItem(LS_CRED, JSON.stringify(c));
}
function clearCreds() {
    localStorage.removeItem(LS_CRED);
}
function loadBindings() {
    try {
        const raw = localStorage.getItem(LS_BIND);
        return raw ? JSON.parse(raw) : {};
    } catch { return {}; }
}
function saveBindings(b) {
    localStorage.setItem(LS_BIND, JSON.stringify(b));
}
function makeCmd(cmd, params) {
    return {
        cmd,
        roomCode,               // 服务端 SendRemoteCommand 依赖它定位控制房间，缺了会整条命令被拒
        sender: playerName,
        senderUid: 'web_' + playerName,
        target: ['*'],
        commandId: 'web_' + Date.now(),
        timestamp: new Date().toISOString(),
        params: params || {}
    };
}

// ---------- 记住登录：页面加载时回填 ----------
(function restoreForm() {
    const c = loadCreds();
    if (c) {
        if (c.roomCode) document.getElementById('roomCode').value = c.roomCode;
        if (c.nickname) document.getElementById('nickname').value = c.nickname;
        if (c.password) {
            document.getElementById('password').value = c.password;
            document.getElementById('rememberMe').checked = true;
        }
    }
})();
// ---------- 加入房间（无服务器地址输入，同源 /hub） ----------
function joinRoom() {
    roomCode = document.getElementById('roomCode').value.trim();
    password = document.getElementById('password').value.trim();
    playerName = document.getElementById('nickname').value.trim() || '网页端';

    if (!roomCode || !password) {
        showError('请输入房间码和密码');
        return;
    }

    // 记住登录
    const remember = document.getElementById('rememberMe').checked;
    if (remember) {
        saveCreds({ roomCode, password, nickname: playerName });
    } else {
        clearCreds();
    }

    connection = new signalR.HubConnectionBuilder()
        .withUrl('/hub')   // 同源：WEB 由服务器自己运行
        .withAutomaticReconnect([0, 2000, 10000, 30000])
        .build();

    // 成员列表更新（全量/增量两形态）
    connection.on('ControlRoomPlayersUpdated', update => {
        // 防御：payload 为空或非对象时直接忽略
        if (!update || typeof update !== 'object') return;
        if (update.full) {
            players = update.players || [];
        } else {
            // 增量：changed 按 uid 原地替换/末尾追加，removed 按 uid 过滤；
            // 不在 changed/removed 中的成员保持不变。
            if (!Array.isArray(players)) players = [];
            const byUid = new Map(players.map(p => [p.playerUid, p]));
            (update.changed || []).forEach(p => {
                if (byUid.has(p.playerUid)) {
                    const idx = players.findIndex(x => x.playerUid === p.playerUid);
                    players[idx] = p;
                } else {
                    byUid.set(p.playerUid, p);
                    players.push(p);
                }
            });
            (update.removed || []).forEach(uid => {
                players = players.filter(p => p.playerUid !== uid);
            });
        }
        renderMembers();
        // 记录锄地进度日志（去重）
        players.forEach(p => {
            const progress = p.autoHoeingProgress || '';
            if (progress && progress !== lastProgress[p.playerUid]) {
                lastProgress[p.playerUid] = progress;
                log(progress);
            }
        });
    });

    // 收到其他成员投递的命令（WEB 不做执行，仅记录；ack 已由 PC 端助手处理）
    connection.on('RemoteCommand', cmd => {
        log(`收到命令: ${cmd.cmd} 来自 ${cmd.sender || '?'}`);
    });

    connection.on('RemoteCommandAck', ack => {
        log(`送达 ${ack.deliveredTo} 个目标`);
    });

    connection.on('JoinRejected', reason => {
        showError(reason);
        log('加入失败: ' + reason);
    });

    connection.onreconnecting(() => {
        setConn(false);
        log('连接断开，重连中...');
    });
    connection.onreconnected(() => {
        setConn(true);
        connection.invoke('JoinControlRoom', roomCode, password, 'web_' + playerName, playerName, [], false, '')
        log('已重连');
    });
    connection.onclose(() => setConn(false));

    connection.start()
        .then(() => connection.invoke('JoinControlRoom', roomCode, password, 'web_' + playerName, playerName, [], false, ''))
        .then(() => {
            document.getElementById('loginPanel').style.display = 'none';
            document.getElementById('controlPanel').style.display = 'block';
            document.getElementById('roomInfo').textContent = `房间 ${roomCode}`;
            log(`已加入控制房间：${roomCode}（${playerName}）`);
        })
        .catch(err => {
            showError('连接失败: ' + err.message);
            log('连接失败: ' + err.message);
        });
}

function setConn(online) {
    const dot = document.getElementById('connDot');
    dot.className = 'dot ' + (online ? 'dot-online' : 'dot-offline');
}

// ---------- 渲染成员列表 ----------
function renderMembers() {
    const list = document.getElementById('memberList');
    if (!players || players.length === 0) {
        list.innerHTML = '<div class="section-empty">暂无在线成员</div>';
        return;
    }
    list.innerHTML = '';
    players.forEach(p => {
        const card = document.createElement('div');
        card.className = 'member-card';

        // 在线圆点：online 绿，离线灰
        const online = !!p.online;
        const dotCls = online ? 'dot-online' : 'dot-offline';
        // 状态文本：助手离线 / BGI 未运行 / 正在执行任务 / 未运行任务
        let statusHtml = '';
        if (!online) {
            statusHtml = '<span class="bgi-status" style="color:#FF3B30;">助手已离线</span>';
        } else if (!p.bgiStatus || p.bgiStatus === 'stopped') {
            statusHtml = '<span class="bgi-status" style="color:#6E6E73;">BGI 未运行</span>';
        } else {
            const taskRunning = !!p.taskRunning;
            const taskName = p.currentTaskName || '';
            if (taskRunning) {
                statusHtml = `<span class="bgi-status" style="color:#34C759;">${taskName || '正在执行任务'}</span>`;
            } else {
                statusHtml = '<span class="bgi-status" style="color:#6E6E73;">未运行任务</span>';
            }
        }

        // 配置组 / 一条龙标签（含 data-uid 和 data-name 用于点击下发）
        // 数量多时按真实行高折叠：配置组最多 3 行、一条龙最多 2 行，超出收进"更多"按钮 → 弹窗查看（applyTagFold 测量截断）
        const uid = p.playerUid || '';
        const name = p.playerName || '?';
        const groups = (p.configGroups || []).map(g =>
            `<span class="tag" data-uid="${uid}" data-name="${name}" data-type="group" data-value="${g}">${g}</span>`).join('');
        const oneclicks = (p.oneClickConfigs || []).map(o =>
            `<span class="tag purple" data-uid="${uid}" data-name="${name}" data-type="oneclick" data-value="${o}">${o}</span>`).join('');

        card.innerHTML = `
            <div class="row1">
                <span class="dot ${dotCls}"></span>
                <span class="name">${name}</span>
                ${uid ? `<span class="bgi-status">(${uid})</span>` : ''}
                ${statusHtml}
            </div>
            ${groups ? `<div class="tags tag-fold" data-maxlines="3" data-uid="${uid}" data-name="${name}" data-type="group">${groups}</div>` : ''}
            ${oneclicks ? `<div class="tags tag-fold" data-maxlines="2" data-uid="${uid}" data-name="${name}" data-type="oneclick">${oneclicks}</div>` : ''}
            <div class="actions">
                <button class="action-btn danger" data-action="stop" data-uid="${uid}" data-name="${name}">停止</button>
                <button class="action-btn primary" data-action="start-group" data-uid="${uid}" data-name="${name}">配置组</button>
                <button class="action-btn primary" data-action="start-oneclick" data-uid="${uid}" data-name="${name}">一条龙</button>
                <button class="action-btn primary" data-action="hotkey" data-uid="${uid}" data-name="${name}">快捷键</button>
                <button class="action-btn primary" data-action="close-game" data-uid="${uid}" data-name="${name}">关闭游戏</button>
            </div>
        `;
        list.appendChild(card);
        applyTagFold(card);  // 真实行高折叠：配置组 3 行、一条龙 2 行，超出加"更多"按钮
    });
}

// ---------- 按真实行高折叠标签（配置组/一条龙） ----------
function applyTagFold(card) {
    const folds = card.querySelectorAll('.tag-fold');
    if (!folds.length) return;
    // 需要等 DOM 布局完成才能量到尺寸
    requestAnimationFrame(() => {
        folds.forEach(container => {
            const maxLines = parseInt(container.dataset.maxlines) || 3;
            const tags = Array.from(container.querySelectorAll('.tag'));
            if (tags.length === 0) return;
            const total = tags.length;
            // 相对容器顶部测量（getBoundingClientRect 差值不受 offsetParent 影响，可靠）
            const containerTop = container.getBoundingClientRect().top;
            const lineHeight = (tags[0].getBoundingClientRect().height || 20) + 4; // + gap 4
            const maxHeight = maxLines * lineHeight;
            // 找第一个进入超行区的标签（其相对 Y >= maxHeight）
            let keepCount = total;
            for (let i = 0; i < total; i++) {
                const relY = tags[i].getBoundingClientRect().top - containerTop;
                if (relY >= maxHeight) { keepCount = i; break; }
            }
            if (keepCount >= total) return; // 没超行，不折叠
            // 移除超出的标签
            for (let i = keepCount; i < total; i++) tags[i].remove();
            // 尾部追加"更多"按钮（复用事件委托的 .tag-more，显示总数）
            const uid = container.dataset.uid;
            const memberName = container.dataset.name;
            const type = container.dataset.type;
            const moreBtn = document.createElement('button');
            moreBtn.className = 'tag-more' + (type === 'oneclick' ? ' purple' : '');
            moreBtn.dataset.uid = uid;
            moreBtn.dataset.name = memberName;
            moreBtn.dataset.type = type;
            moreBtn.textContent = `更多(${total})`;
            container.appendChild(moreBtn);
        });
    });
}
// ---------- 成员卡片点击事件委托（按钮+标签，全局只绑一次） ----------
document.getElementById('memberList').addEventListener('click', e => {
    // 操作按钮：停止 / 启动配置组 / 启动一条龙
    const btn = e.target.closest('.action-btn');
    if (btn) {
        const uid = btn.dataset.uid;
        const memberName = btn.dataset.name;
        const action = btn.dataset.action;
        if (!uid) return;

        if (action === 'stop') {
            const cmd = makeCmd('stop');
            cmd.target = [uid];
            connection.invoke('SendRemoteCommand', cmd).catch(err => log('发送失败: ' + err.message));
            log(`已对 ${memberName} 下发停止`);
        } else if (action === 'start-group') {
            showMemberConfigSelect(uid, memberName, 'group');
        } else if (action === 'start-oneclick') {
            showMemberConfigSelect(uid, memberName, 'oneclick');
        } else if (action === 'hotkey') {
            showHotkeySelect(uid, memberName);
        } else if (action === 'close-game') {
            if (confirm(`确定要关闭 ${memberName} 的游戏吗？`)) {
                const cmd = makeCmd('close_game');
                cmd.target = [uid];
                connection.invoke('SendRemoteCommand', cmd).catch(err => log('发送失败: ' + err.message));
                log(`已对 ${memberName} 下发关闭游戏`);
            }
        }
        return;
    }

    // "更多"按钮 → 弹窗查看全部配置组/一条龙
    const moreBtn = e.target.closest('.tag-more');
    if (moreBtn) {
        const uid = moreBtn.dataset.uid;
        const memberName = moreBtn.dataset.name;
        const type = moreBtn.dataset.type;
        if (!uid || !type) return;
        showTagMoreModal(uid, memberName, type);
        return;
    }

    // 配置组/一条龙标签 → 确认弹窗
    const tag = e.target.closest('.tag');
    if (!tag) return;
    const uid = tag.dataset.uid;
    const memberName = tag.dataset.name;
    const type = tag.dataset.type;
    const value = tag.dataset.value;
    if (!uid || !value) return;
    showMemberConfirmModal(uid, memberName, value, type === 'oneclick');
});

// ---------- "更多"按钮 → 弹窗查看全部配置组/一条龙 ----------
function showTagMoreModal(uid, memberName, type) {
    const member = players.find(p => p.playerUid === uid);
    if (!member) return;

    const isOneClick = type === 'oneclick';
    const items = isOneClick ? (member.oneClickConfigs || []) : (member.configGroups || []);
    const label = isOneClick ? '一条龙' : '配置组';
    if (items.length === 0) {
        log(`${memberName} 没有可用${label}`);
        return;
    }

    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
        <div class="modal">
            <h3>${label}列表</h3>
            <p style="font-size:13px;color:#6E6E73;margin-bottom:12px;">「${memberName}」共 ${items.length} 个${label}，点击即可下发</p>
            <div class="tags modal-tags">
                ${items.map(g =>
                    `<span class="tag${isOneClick ? ' purple' : ''}" data-uid="${uid}" data-name="${memberName}" data-type="${type}" data-value="${g}">${g}</span>`
                ).join('')}
            </div>
            <div class="btn-row">
                <button class="btn btn-secondary" id="tmClose">关闭</button>
            </div>
        </div>`;
    document.body.appendChild(overlay);

    // 关闭按钮
    overlay.querySelector('#tmClose').addEventListener('click', () => overlay.remove());
    // 点击遮罩关闭
    overlay.addEventListener('click', e => {
        if (e.target === overlay) { overlay.remove(); return; }
    });
    // 弹窗内标签点击 → 与卡片一致：确认后下发
    overlay.querySelectorAll('.tag').forEach(tag => {
        tag.addEventListener('click', () => {
            const v = tag.dataset.value;
            if (!v) return;
            overlay.remove();
            showMemberConfirmModal(uid, memberName, v, isOneClick);
        });
    });
}
// ---------- 一键快捷指令 ----------
document.getElementById('quickRow').addEventListener('click', e => {
    const btn = e.target.closest('.quick-btn');
    if (!btn) return;
    const key = btn.dataset.key;
    const bindings = loadBindings();
    const binding = bindings[key];

    if (!binding) {
        // 未绑定：弹绑定弹窗
        showBindModal(key, newBind => {
            if (newBind) {
                bindings[key] = newBind;
                saveBindings(bindings);
                log(`${key} 已绑定: ${newBind}`);
            }
        });
        return;
    }

    // 已绑定：确认弹窗（含修改）
    const isOneClick = binding.startsWith('ONEDRAGON:');
    const value = isOneClick ? binding.slice(10) : binding.slice(6);
    showConfirmModal(key, value, isOneClick, ok => {
        if (ok === 'confirm') {
            // 从成员列表找该配置的任务列表，弹"从此处开始执行"让用户选起点，对全员下发
            let tasks = null;
            let tasksWithStatus = null;
            for (const p of players) {
                const dict = isOneClick ? (p.oneClickTasks || {}) : (p.configGroupTasks || {});
                if (dict && Array.isArray(dict[value])) { tasks = dict[value]; }
                const dictWs = isOneClick ? (p.oneClickTasksWithStatus || {}) : (p.configGroupTasksWithStatus || {});
                if (dictWs && Array.isArray(dictWs[value])) { tasksWithStatus = dictWs[value]; }
                if (tasks && tasksWithStatus) break;
            }
            showTaskListSelect('*', '全部在线成员', value, isOneClick, tasks || null, ['*'], tasksWithStatus || null);
        } else if (ok === 'modify') {
            showBindModal(key, newBind => {
                if (newBind) {
                    bindings[key] = newBind;
                    saveBindings(bindings);
                    log(`${key} 已绑定: ${newBind}`);
                }
            });
        }
    });
});

// ---------- 弹窗：绑定 ----------
function showBindModal(key, callback) {
    // 从成员列表收集可用配置组/一条龙（取第一个有配置的在线成员）
    const first = players.find(p => p.configGroups?.length > 0 || p.oneClickConfigs?.length > 0);
    const groups = first ? (first.configGroups || []) : [];
    const oneclicks = first ? (first.oneClickConfigs || []) : [];
    const all = [
        ...groups.map(g => ({ label: '[配置组] ' + g, value: 'GROUP:' + g })),
        ...oneclicks.map(o => ({ label: '[一条龙] ' + o, value: 'ONEDRAGON:' + o }))
    ];

    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
        <div class="modal">
            <h3>绑定「${key}」</h3>
            <p style="font-size:13px;color:#6E6E73;margin-bottom:12px;">选择要执行的配置组或一条龙（来自成员列表）</p>
            ${all.length === 0 ? '<p style="color:#FF3B30;font-size:13px;">暂无可用配置，请在成员列表同步后再试</p>' : ''}
            <div class="list-items" id="bindList">
                ${all.map((item, i) => `<div class="list-item" data-index="${i}" data-value="${item.value}">${item.label}</div>`).join('')}
            </div>
            <div class="btn-row">
                <button class="btn btn-secondary" id="bindCancel">取消</button>
                <button class="btn btn-primary" id="bindOk">绑定</button>
            </div>
        </div>`;
    document.body.appendChild(overlay);

    let selected = null;
    const items = overlay.querySelectorAll('.list-item');
    items.forEach(el => {
        el.addEventListener('click', () => {
            items.forEach(x => x.classList.remove('selected'));
            el.classList.add('selected');
            selected = el.dataset.value;
        });
    });
    overlay.querySelector('#bindOk').addEventListener('click', () => {
        if (selected) {
            callback(selected);
            overlay.remove();
        } else if (all.length === 0) {
            callback(null);
            overlay.remove();
        }
    });
    overlay.querySelector('#bindCancel').addEventListener('click', () => {
        callback(null);
        overlay.remove();
    });
}

// ---------- 弹窗：确认（含修改按钮） ----------
function showConfirmModal(key, value, isOneClick, callback) {
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
        <div class="modal">
            <h3>确认下发</h3>
            <p style="font-size:14px;color:#1D1D1F;margin-bottom:16px;">
                确认对全部在线成员下发「${key}」→ 本机${isOneClick ? '一条龙' : '配置组'}「${value}」？
            </p>
            <div class="btn-row">
                <button class="btn btn-secondary" id="confCancel">取消</button>
                <button class="btn btn-secondary" id="confModify">修改</button>
                <button class="btn btn-primary" id="confOk">确认</button>
            </div>
        </div>`;
    document.body.appendChild(overlay);

    overlay.querySelector('#confOk').addEventListener('click', () => { callback('confirm'); overlay.remove(); });
    overlay.querySelector('#confModify').addEventListener('click', () => { callback('modify'); overlay.remove(); });
    overlay.querySelector('#confCancel').addEventListener('click', () => { callback('cancel'); overlay.remove(); });
}

// ---------- 弹窗：确认对指定成员下发配置组/一条龙（含"从此处开始执行"任务列表） ----------
function showMemberConfirmModal(uid, memberName, value, isOneClick) {
    const member = players.find(p => p.playerUid === uid);
    const tasks = member
        ? (isOneClick ? (member.oneClickTasks || {}) : (member.configGroupTasks || {}))[value]
        : null;
    const tasksWithStatus = member
        ? (isOneClick ? (member.oneClickTasksWithStatus || {}) : (member.configGroupTasksWithStatus || {}))[value]
        : null;
    showTaskListSelect(uid, memberName, value, isOneClick, tasks || null, null, tasksWithStatus || null);
}

// ---------- 弹窗：选择成员的配置组/一条龙并下发（含"从此处开始执行"任务列表） ----------
function showMemberConfigSelect(uid, memberName, type) {
    const member = players.find(p => p.playerUid === uid);
    if (!member) { log(`找不到成员 ${memberName} 的配置信息`); return; }

    const isOneClick = type === 'oneclick';
    const items = isOneClick ? (member.oneClickConfigs || []) : (member.configGroups || []);
    const label = isOneClick ? '一条龙' : '配置组';

    if (items.length === 0) {
        log(`${memberName} 没有可用${label}`);
        return;
    }

    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
        <div class="modal">
            <h3>选择${label}</h3>
            <p style="font-size:13px;color:#6E6E73;margin-bottom:12px;">选择对「${memberName}」下发的${label}</p>
            <div class="list-items">
                ${items.map((item, i) => `<div class="list-item" data-index="${i}" data-value="${item}">${item}</div>`).join('')}
            </div>
            <div class="btn-row">
                <button class="btn btn-secondary" id="mcfgCancel">取消</button>
                <button class="btn btn-primary" id="mcfgOk">确认</button>
            </div>
        </div>`;
    document.body.appendChild(overlay);

    let selected = null;
    const listItems = overlay.querySelectorAll('.list-item');
    listItems.forEach(el => {
        el.addEventListener('click', () => {
            listItems.forEach(x => x.classList.remove('selected'));
            el.classList.add('selected');
            selected = el.dataset.value;
        });
    });
    overlay.querySelector('#mcfgOk').addEventListener('click', () => {
        if (selected) {
            const tasks = isOneClick
                ? (member.oneClickTasks || {})[selected]
                : (member.configGroupTasks || {})[selected];
            const tasksWs = isOneClick
                ? (member.oneClickTasksWithStatus || {})[selected]
                : (member.configGroupTasksWithStatus || {})[selected];
            overlay.remove();
            showTaskListSelect(uid, memberName, selected, isOneClick, tasks || null, null, tasksWs || null);
        }
    });
    overlay.querySelector('#mcfgCancel').addEventListener('click', () => overlay.remove());
}

// ---------- 弹窗：快捷键选择 ----------
function showHotkeySelect(uid, memberName) {
    const member = players.find(p => p.playerUid === uid);
    const hotkeys = member ? member.hotkeys || [] : [];
    if (!hotkeys || hotkeys.length === 0) {
        log(`${memberName} 没有可用的快捷键`);
        return;
    }

    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
        <div class="modal">
            <h3>选择快捷键</h3>
            <p style="font-size:13px;color:#6E6E73;margin-bottom:12px;">选择要对「${memberName}」执行的快捷键</p>
            <div class="list-items">
                ${hotkeys.map((hk, i) => {
                    const funcName = hk.functionName || '';
                    const hotkeyText = hk.hotkeyText || '';
                    return `<div class="list-item" data-index="${i}" data-config-name="${hk.configName || ''}">
                        ${funcName} <span style="font-size:11px;color:#6E6E73;">(${hotkeyText})</span>
                    </div>`;
                }).join('')}
            </div>
            <div class="btn-row">
                <button class="btn btn-secondary" id="hkCancel">取消</button>
                <button class="btn btn-primary" id="hkOk">执行</button>
            </div>
        </div>`;
    document.body.appendChild(overlay);

    let selected = null;
    const items = overlay.querySelectorAll('.list-item');
    items.forEach(el => {
        el.addEventListener('click', () => {
            items.forEach(x => x.classList.remove('selected'));
            el.classList.add('selected');
            selected = el.dataset.configName;
        });
    });
    if (items.length > 0) {
        items[0].classList.add('selected');
        selected = items[0].dataset.configName;
    }

    overlay.querySelector('#hkOk').addEventListener('click', () => {
        if (selected) {
            const cmd = makeCmd('hotkey_execute', { hotkeyConfigName: selected });
            cmd.target = [uid];
            connection.invoke('SendRemoteCommand', cmd).catch(err => log('发送失败: ' + err.message));
            log(`已对 ${memberName} 下发快捷键: ${selected}`);
        }
        overlay.remove();
    });
    overlay.querySelector('#hkCancel').addEventListener('click', () => overlay.remove());
}

// ---------- 弹窗：任务列表选择（"从此处开始执行" + 启用状态编辑） ----------
function showTaskListSelect(uid, memberName, configName, isOneClick, tasks, targetList, tasksWithStatus) {
    const label = isOneClick ? '一条龙' : '配置组';
    // 下发目标：默认单成员；targetList 传入时用自定义目标（如一键命令对全员 ['*']）
    const targets = targetList || [uid];
    // 构建任务选项列表：第一项"从头开始"，后面是真实任务名
    const options = [{ index: 0, text: '从头开始', sub: '第一个任务', isTask: false }];
    if (tasks && Array.isArray(tasks)) {
        tasks.forEach((t, i) => {
            const statusInfo = tasksWithStatus && Array.isArray(tasksWithStatus) && i < tasksWithStatus.length
                ? tasksWithStatus[i] : null;
            const enabled = statusInfo
                ? (isOneClick ? (statusInfo.enabled !== false) : (statusInfo.status !== 'Disabled'))
                : true;
            options.push({
                index: i + 1,
                text: `${i + 1}. ${t}`,
                sub: '',
                isTask: true,
                status: statusInfo,
                enabled: enabled
            });
        });
    }

    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
        <div class="modal">
            <h3>从此处开始执行</h3>
            <p style="font-size:13px;color:#1D1D1F;margin-bottom:4px;font-weight:600;">「${configName}」${tasks ? '共 ' + tasks.length + ' 个任务' : ''}</p>
            <p style="font-size:12px;color:#6E6E73;margin-bottom:10px;">选择从哪个任务开始（勾选切换启用状态）</p>
            <div class="list-items" style="max-height:300px;overflow-y:auto;">
                ${options.map((opt, i) => {
                    if (i === 0) {
                        return `<div class="list-item" data-index="0" style="display:flex;justify-content:space-between;align-items:center;">
                            <span>从头开始</span>
                            <span style="font-size:11px;color:#6E6E73;">第一个任务</span>
                        </div>`;
                    }
                    const checked = opt.enabled ? 'checked' : '';
                    return `<div class="list-item task-row" data-index="${opt.index}" style="display:flex;align-items:center;gap:8px;">
                        <input type="checkbox" class="task-checkbox" ${checked} data-index="${opt.index}">
                        <span>${opt.text}</span>
                    </div>`;
                }).join('')}
            </div>
            <div class="btn-row">
                <button class="btn btn-secondary" id="tskCancel">取消</button>
                <button class="btn btn-primary" id="tskOk">确认</button>
            </div>
        </div>`;
    document.body.appendChild(overlay);

    let selectedIndex = 0;
    // 记录启用状态变更
    const statusChanges = [];

    const listItems = overlay.querySelectorAll('.list-item');
    listItems.forEach(el => {
        el.addEventListener('click', (e) => {
            // 如果点击的是 checkbox，不触发选择起点
            if (e.target.classList.contains('task-checkbox')) return;
            listItems.forEach(x => x.classList.remove('selected'));
            el.classList.add('selected');
            selectedIndex = parseInt(el.dataset.index);
        });
    });
    // 默认选中第一项
    if (listItems.length > 0) listItems[0].classList.add('selected');

    // checkbox 变更记录
    overlay.querySelectorAll('.task-checkbox').forEach(cb => {
        cb.addEventListener('change', () => {
            const idx = parseInt(cb.dataset.index);
            statusChanges.push({ index: idx, enabled: cb.checked });
        });
    });

    overlay.querySelector('#tskOk').addEventListener('click', () => {
        // 先发启用状态变更
        const changes = {};
        // 用 Set 去重（同一索引可能多次 change）
        const seen = new Set();
        for (const ch of statusChanges) {
            if (!seen.has(ch.index)) {
                seen.add(ch.index);
                changes[ch.index] = ch.enabled;
            }
        }
        // 也检查所有当前 checkbox 状态，与原始态对比
        overlay.querySelectorAll('.task-checkbox').forEach(cb => {
            const idx = parseInt(cb.dataset.index);
            if (!seen.has(idx)) {
                if (tasksWithStatus && Array.isArray(tasksWithStatus) && idx - 1 < tasksWithStatus.length) {
                    const original = isOneClick
                        ? tasksWithStatus[idx - 1]?.enabled !== false
                        : tasksWithStatus[idx - 1]?.status !== 'Disabled';
                    if (cb.checked !== original) {
                        seen.add(idx);
                        changes[idx] = cb.checked;
                    }
                }
            }
        });

        // 逐个下发启用状态变更
        for (const [taskIdx, en] of Object.entries(changes)) {
            const changeCmd = makeCmd('set_task_enabled', {
                [isOneClick ? 'configName' : 'groupName']: configName,
                taskIndex: parseInt(taskIdx),
                enabled: en
            });
            changeCmd.target = targets;
            connection.invoke('SendRemoteCommand', changeCmd).catch(err => log('启用状态变更失败: ' + err.message));
        }
        if (Object.keys(changes).length > 0) {
            log(`已对 ${memberName} 更新 ${Object.keys(changes).length} 个任务的启用状态`);
        }

        // 发启动命令
        const cmd = makeCmd(isOneClick ? 'start_oneclick' : 'start_group', {
            [isOneClick ? 'configName' : 'groupName']: configName,
            startFromIndex: selectedIndex
        });
        cmd.target = targets;
        connection.invoke('SendRemoteCommand', cmd).catch(err => log('发送失败: ' + err.message));
        log(`已对 ${memberName} 下发 ${label}：${configName}（从第 ${selectedIndex} 个任务开始）`);
        overlay.remove();
    });
    overlay.querySelector('#tskCancel').addEventListener('click', () => overlay.remove());
}

// ---------- 设置弹窗 ----------
function openSettings() {
    const creds = loadCreds() || {};
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
        <div class="modal">
            <h3>设置</h3>
            <label class="field-label">房间码</label>
            <input type="text" id="setRoomCode" value="${creds.roomCode || ''}" placeholder="6 位房间码">
            <label class="field-label">密码</label>
            <input type="password" id="setPassword" value="${creds.password || ''}" placeholder="密码">
            <label class="field-label">昵称</label>
            <input type="text" id="setNickname" value="${creds.nickname || ''}" placeholder="昵称">
            <div class="btn-row" style="margin-top:20px;">
                <button class="btn btn-secondary" id="setCancel">取消</button>
                <button class="btn btn-primary" id="setSave">保存</button>
            </div>
        </div>`;
    document.body.appendChild(overlay);
    overlay.querySelector('#setSave').addEventListener('click', () => {
        const rc = overlay.querySelector('#setRoomCode').value.trim();
        const pw = overlay.querySelector('#setPassword').value.trim();
        const nn = overlay.querySelector('#setNickname').value.trim();
        if (rc && pw) {
            saveCreds({ roomCode: rc, password: pw, nickname: nn });
            log('设置已保存（下次加入时生效）');
        }
        overlay.remove();
    });
    overlay.querySelector('#setCancel').addEventListener('click', () => overlay.remove());
}