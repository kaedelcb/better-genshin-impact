// 两个中心共用执行端的 R1–R10；网页只编辑规则和节点参数，状态由执行端确认。
const mtRegisters = { supported:false, available:false, entries:[], error:'尚未读取寄存器状态', busy:false, version:0, loading:false, targetKey:'', status:'', dialog:null };
function mtRegistersSupported() { return mtRegisters.supported && mtRegisters.targetKey === mt.target?.key; }
function mtRegistersVersion() { return mtRegisters.version; }
function mtRegistersResetState() {
    mtRegisters.dialog?.close();
    Object.assign(mtRegisters, { supported:false, available:false, entries:[], error:'尚未读取寄存器状态', busy:false, loading:false, targetKey:mt.target?.key || '', status:'', dialog:null });
    mtRegisters.version++;
}
function mtRegistersNormalize(snapshot) {
    const entries = mtPick(snapshot, 'entries');
    if (mtPick(snapshot, 'available') !== true || !Array.isArray(entries) || entries.length !== 10
        || entries.some((e, i) => !e || mtPick(e, 'number') !== i + 1 || ![0, 1].includes(mtPick(e, 'value')))) return null;
    return entries.map(e => Object.fromEntries(['number','name','value','resetMode','resetHour','resetMinute','delaySeconds','changedAtUtc','nextResetAtUtc','settingsRevision'].map(k => [k, mtPick(e, k)])));
}
function mtRegistersApply(snapshot, version = mtRegisters.version) {
    if (version !== mtRegisters.version || mtRegisters.busy) return false;
    const entries = mtRegistersNormalize(snapshot);
    Object.assign(mtRegisters, { targetKey:mt.target?.key || '', supported:snapshot != null, available:!!entries,
        entries:entries || mtRegisters.entries, error:entries ? '' : mtPick(snapshot, 'error') || '执行端尚未提供寄存器状态，请检查连接或版本。' });
    mtRegistersRefresh();
    return true;
}
function mtRegistersFail(error) {
    mtRegisters.available = false;
    mtRegisters.error = error || '寄存器状态未确认';
    mtRegistersRefresh();
}
async function mtRegistersPoll() {
    if (mtRegisters.loading || mtRegisters.busy || !mt.target || !mtTargetReady() || !mtOwner()) return;
    const key = mt.target.key, version = mtRegisters.version, pending = {};
    mtRegisters.loading = pending;
    try {
        const reply = await mtRead('registers.get', undefined, { timeout:12000 });
        if (mt.target?.key === key) mtRegistersApply(reply.data, version);
    } catch (error) {
        if (mt.target?.key === key && version === mtRegisters.version) mtRegistersFail('寄存器状态刷新失败：' + error.message);
    } finally { if (mtRegisters.loading === pending) mtRegisters.loading = false; }
}
function mtRegisterLabel(entry) { return `R${entry.number}${(entry.name || '').trim() ? ' · ' + entry.name.trim() : ''}`; }
function mtRegisterResetDescription(entry) {
    const pad = n => String(n || 0).padStart(2, '0');
    if (entry.resetMode === 'daily') return `每天 ${pad(entry.resetHour)}:${pad(entry.resetMinute)} 重置为0（执行端本地时间）`;
    if (entry.resetMode === 'afterChange') {
        const n = Number(entry.delaySeconds) || 0;
        return `状态从0变为1后 ${Math.floor(n / 86400)}天${pad(Math.floor(n / 3600) % 24)}时${pad(Math.floor(n / 60) % 60)}分${pad(n % 60)}秒重置为0`;
    }
    return '不自动重置';
}
function mtRegisterCanWrite() { return mtRegisters.available && mtRegistersSupported() && !mtRegisters.busy && mtTargetReady() && mtOwner(); }
function mtRenderRegisterStrip(host) {
    if (typeof host === 'string') host = mtEl(host);
    if (!host) return;
    host.dataset.registerStrip = '1';
    host.classList.add('mt-register-strip');
    const available = mtRegisters.available && mtRegistersSupported();
    const busy = mtRegisters.busy, disabled = !mtRegisterCanWrite();
    const html = `<span class="mt-register-head">寄存器</span><div class="mt-register-cells" role="group" aria-label="两个中心共用寄存器">${Array.from({length:10}, (_, i) => {
        const entry = mtRegisters.entries[i] || {number:i + 1}, value = available ? entry.value : '?';
        const title = available ? `${mtRegisterLabel(entry)}\n当前状态：${value}\n${mtRegisterResetDescription(entry)}\n点击设置名称与重置规则` : `R${i + 1}\n${mtRegisters.error}`;
        return `<button class="mt-register-cell${value === 1 ? ' is-one' : ''}${available ? '' : ' is-unknown'}" data-register-number="${i + 1}" title="${mte(title)}" aria-label="${mte(mtRegisterLabel(entry))}，状态${value}，设置"${disabled ? ' disabled' : ''}><small>R${i + 1}</small><strong>${value}</strong></button>`;
    }).join('')}</div><button class="dd-btn dd-btn-sm" data-register-reset-all${disabled ? ' disabled' : ''} title="立即将 R1–R10 全部归零，保留名称和自动重置规则">${busy ? '处理中…' : '全归零'}</button>
      <span class="mt-register-feedback" role="status">${mte(mtRegisters.status || (available ? '' : mtRegisters.error))}</span>`;
    // 相同轮询保留原按钮；在按下与抬起之间替换节点会丢失真实 click。
    if (host._mtRegisterHtml === html && host.querySelector('[data-register-reset-all]')) return;
    host.innerHTML = html; host._mtRegisterHtml = html;
    host.querySelectorAll('[data-register-number]').forEach(b => b.onclick = () => mtOpenRegisterSettings(+b.dataset.registerNumber));
    host.querySelector('[data-register-reset-all]').onclick = () => mtResetRegisters();
}
function mtRegistersRefresh() {
    document.querySelectorAll('[data-register-strip]').forEach(mtRenderRegisterStrip);
    document.querySelectorAll('[data-register-current]').forEach(el => {
        const n = Number(el.dataset.registerCurrent), entry = mtRegisters.entries[n - 1];
        el.textContent = mtRegisters.available && mtRegistersSupported() && entry ? `当前 ${entry.value}` : '状态未确认';
        el.title = entry && mtRegisters.available ? mtRegisterResetDescription(entry) : mtRegisters.error;
    });
    const dialog = mtRegisters.dialog;
    if (!dialog?.open) return;
    const n = Number(dialog.dataset.registerNumber), entry = mtRegisters.entries[n - 1];
    dialog.querySelector('[data-register-state]').textContent = `当前状态：${mtRegisters.available && entry ? entry.value : '未确认'}；启动中心与任务中心共用此位的名称和重置规则。`;
    dialog.querySelectorAll('input,select,[data-register-save],[data-register-close]').forEach(el => el.disabled = mtRegisters.busy);
    dialog.querySelectorAll('[data-register-reset]').forEach(el => el.disabled = !mtRegisterCanWrite());
}
async function mtRegisterOperation(operation, payload, label) {
    if (!mtRegisterCanWrite()) return mtToast(mtRegisters.busy ? '寄存器操作正在执行，请等待完成。' : '寄存器状态尚未确认，不能修改。', true);
    const key = mt.target.key;
    mtRegisters.busy = true; const version = ++mtRegisters.version;
    mtRegisters.status = `${label}，等待执行端确认…`; mtRegistersRefresh();
    let reply;
    try {
        reply = await mtOperate(operation, payload, { label });
        if (mt.target?.key !== key || version !== mtRegisters.version) return reply;
        mtRegisters.busy = false;
        if (reply.status === 'ok' && mtRegistersNormalize(reply.data)) {
            mtRegistersApply(reply.data);
            mtRegisters.status = operation === 'registers.reset'
                ? mtRegisters.entries.filter(e => payload.all || e.number === payload.number).every(e => e.value === 0)
                    ? payload.all ? 'R1–R10 已全部归零。' : `R${payload.number} 已归零。`
                    : '手动归零已确认，随后状态发生更新；以最新状态为准。'
                : `R${payload.number} 设置已保存。`;
            mtToast(mtRegisters.status);
        } else {
            mtRegisters.status = `${label}${reply.status === 'unknown' ? '结果未确认' : '未完成'}：${reply.message || '执行端未提供有效的最新状态'}`;
            if (reply.status === 'unknown' || reply.status === 'ok') mtRegistersFail(mtRegisters.status);
            mtToast(mtRegisters.status, true);
        }
        const feedback = mtRegisters.dialog?.querySelector('[data-register-feedback]');
        if (feedback) feedback.textContent = mtRegisters.status;
        mtRegistersRefresh();
        return reply;
    } catch (error) {
        if (mt.target?.key === key && version === mtRegisters.version) {
            mtRegisters.status = `${label}结果未确认：${error.message}`;
            mtRegistersFail(mtRegisters.status); mtToast(mtRegisters.status, true);
            const feedback = mtRegisters.dialog?.querySelector('[data-register-feedback]');
            if (feedback) feedback.textContent = mtRegisters.status;
        }
        return {status:'unknown',message:error.message};
    } finally { if (mt.target?.key === key && version === mtRegisters.version) { mtRegisters.busy = false; mtRegistersRefresh(); } }
}
async function mtResetRegisters(number) {
    return mtRegisterOperation('registers.reset', number == null ? {all:true} : {number}, number == null ? '全部寄存器归零' : `R${number} 归零`);
}
function mtOpenRegisterSettings(number) {
    if (!mtRegisterCanWrite()) return;
    const original = mtClone(mtRegisters.entries[number - 1]); if (!original) return;
    const dialog = mtDialog('mtRegisterDialog'), delay = Number(original.delaySeconds) || 0;
    mtRegisters.dialog = dialog; dialog.dataset.registerNumber = number;
    const input = (key, value, attrs = '') => `<input data-register-setting="${key}" value="${mte(value)}" ${attrs}>`;
    const field = (label, control) => `<label class="dd-field"><span>${label}</span>${control}</label>`;
    dialog.innerHTML = `<header class="mt-dialog-head"><div><h3>R${number} · 寄存器设置</h3><p class="dodoco-hint" data-register-state></p></div><button class="dd-btn dd-btn-sm" data-register-close>取消</button></header>
      <div class="mt-register-settings">${field('寄存器名称', input('name', original.name || '', 'maxlength="40" placeholder="最多40字；留空显示位号"'))}
      ${field('自动重置为0', `<select data-register-setting="resetMode"><option value="none">不自动重置</option><option value="daily">每天固定时间重置</option><option value="afterChange">状态变化后自动重置</option></select>`)}
      <div class="mt-register-time" data-register-daily>${field('重置时刻（执行端本地时间）', input('resetTime', `${String(original.resetHour || 0).padStart(2,'0')}:${String(original.resetMinute || 0).padStart(2,'0')}`, 'type="time" required'))}</div>
      <div data-register-after><div class="mt-register-delay">${[['天','days',Math.floor(delay / 86400),31],['时','hours',Math.floor(delay / 3600) % 24,23],['分','minutes',Math.floor(delay / 60) % 60,59],['秒','seconds',delay % 60,59]].map(([label,key,value,max]) => field(label, input(key, value, `type="number" min="0" max="${max}" step="1" required`))).join('')}</div><p class="dodoco-hint">从0变为1时开始计时；重复写入1和改名不会延长计时。延时须大于0且不超过31天。</p></div>
      <p class="dodoco-hint">名称、状态和重置规则会保存；重启助手时先兑现已到期的归零。手动归零只改状态，保留此处尚未保存的设置。</p>
      <p class="mt-register-feedback" role="status" data-register-feedback></p><div class="dd-actions"><button class="dd-btn dd-btn-sm" data-register-reset="one">本位归零</button><button class="dd-btn dd-btn-sm" data-register-reset="all">全部归零</button><button class="dd-btn dd-btn-primary" data-register-save>保存设置</button></div></div>`;
    const get = key => dialog.querySelector(`[data-register-setting="${key}"]`);
    get('resetMode').value = original.resetMode;
    const renderMode = () => { dialog.querySelector('[data-register-daily]').hidden = get('resetMode').value !== 'daily'; dialog.querySelector('[data-register-after]').hidden = get('resetMode').value !== 'afterChange'; };
    get('resetMode').onchange = renderMode; renderMode();
    dialog.querySelector('[data-register-close]').onclick = () => { if (!mtRegisters.busy) dialog.close(); };
    dialog.oncancel = event => { if (mtRegisters.busy) event.preventDefault(); };
    dialog.onclose = () => { if (mtRegisters.dialog === dialog) mtRegisters.dialog = null; };
    dialog.querySelectorAll('[data-register-reset]').forEach(b => b.onclick = () => mtResetRegisters(b.dataset.registerReset === 'all' ? undefined : number));
    dialog.querySelector('[data-register-save]').onclick = async () => {
        const fail = message => { dialog.querySelector('[data-register-feedback]').textContent = message; };
        const name = get('name').value.trim(), resetMode = get('resetMode').value;
        if (name.length > 40 || /[\u0000-\u001f\u007f-\u009f]/.test(name)) return fail('寄存器名称限40字，不能包含控制字符。');
        if (!/^\d\d:\d\d$/.test(get('resetTime').value)) return fail('请选择有效的重置时刻。');
        const [resetHour, resetMinute] = get('resetTime').value.split(':').map(Number);
        const parts = ['days','hours','minutes','seconds'].map(k => Number(get(k).value));
        if (parts.some((v,i) => !Number.isInteger(v) || v < 0 || v > [31,23,59,59][i])) return fail('延时时间的天、时、分、秒无效。');
        const delaySeconds = parts[0] * 86400 + parts[1] * 3600 + parts[2] * 60 + parts[3];
        if (delaySeconds > 2678400 || resetMode === 'afterChange' && delaySeconds === 0) return fail('延时重置时间须大于0且不超过31天。');
        const reply = await mtRegisterOperation('registers.configure', {number,name,resetMode,resetHour,resetMinute,delaySeconds,expectedSettingsRevision:original.settingsRevision}, `保存 R${number} 设置`);
        if (reply?.status === 'ok' && mtRegisters.available && mtRegisters.targetKey === mt.target?.key) dialog.close();
    };
    dialog.showModal(); mtRegistersRefresh();
}
function mtRegisterSummary(step) {
    const condition = step.kind === 'judgeRegister', settings = condition ? step.registerCondition : step.registerWrite;
    const values = settings?.values;
    if (!Array.isArray(values) || values.length !== 10 || values.some(v => v !== null && v !== 0 && v !== 1)) return '寄存器参数无效';
    const rows = values.map((v,i) => v === null ? '' : `R${i + 1}${condition ? '=' : '←'}${v}`).filter(Boolean);
    return rows.length ? rows.join(condition ? settings.match === 'any' ? ' 或 ' : ' 且 ' : '，') : condition ? '请选择判断条件（忽略的位不参与判断）' : '请选择要设置的位（其余保持）';
}
function mtRegisterFields(step) {
    const condition = step.kind === 'judgeRegister', settings = condition ? step.registerCondition : step.registerWrite;
    if (!settings || !Array.isArray(settings.values) || settings.values.length !== 10
        || settings.values.some(v => v !== null && v !== 0 && v !== 1)) return '<p class="dodoco-hint">寄存器参数无效，已保留原值；请修复来源配置后重新读取。</p>';
    const match = condition ? `<label class="dd-field"><span>条件关系</span><select data-register-match><option value="all"${settings.match === 'all' ? ' selected' : ''}>全部条件满足（且）</option><option value="any"${settings.match === 'any' ? ' selected' : ''}>任一条件满足（或）</option></select></label>` : '';
    return `${match}<div class="mt-register-selection">${settings.values.map((v,i) => {
        const entry = mtRegisters.entries[i] || {number:i + 1}, current = mtRegisters.available && mtRegistersSupported() ? `当前 ${entry.value}` : '状态未确认';
        return `<label class="mt-register-row"><span class="mt-register-label">${mte(mtRegisterLabel(entry))}<small data-register-current="${i + 1}">${current}</small></span><select data-register-select="${i}" aria-label="${mte(mtRegisterLabel(entry))}${condition ? '判断' : '设置'}"><option value=""${v === null ? ' selected' : ''}>${condition ? '忽略' : '保持'}</option><option value="0"${v === 0 ? ' selected' : ''}>${condition ? '等于0' : '置0'}</option><option value="1"${v === 1 ? ' selected' : ''}>${condition ? '等于1' : '置1'}</option></select></label>`;
    }).join('')}</div><p class="dodoco-hint">${condition ? '只读取选中的位；忽略的位不参与判断。' : '只设置选中的位；保持的位不变，设置节点不执行判断。'}至少选择一位。${mtRegistersSupported() ? '' : '当前执行端未提供寄存器能力，不能保存或执行这些节点。'}</p>`;
}
function mtBindRegisterFields(host, step, changed) {
    const settings = step.kind === 'judgeRegister' ? step.registerCondition : step.registerWrite;
    host.querySelectorAll('[data-register-select]').forEach(el => el.onchange = () => { settings.values[+el.dataset.registerSelect] = el.value === '' ? null : +el.value; changed(); });
    const match = host.querySelector('[data-register-match]');
    if (match) match.onchange = () => { settings.match = match.value; changed(); };
}
function mtValidateRegisterSteps(steps) {
    let error = '';
    const visit = chain => (chain || []).forEach(step => {
        if (error) return;
        if (step.kind === 'setRegister' || step.kind === 'judgeRegister') {
            if (!mtRegistersSupported()) { error = '当前执行端未提供寄存器能力，未保存或执行寄存器节点。'; return; }
            const settings = step.kind === 'judgeRegister' ? step.registerCondition : step.registerWrite;
            if (!settings || !Array.isArray(settings.values) || settings.values.length !== 10 || settings.values.some(v => v !== null && v !== 0 && v !== 1) || settings.values.every(v => v === null))
                error = `「${(step.name || '').trim() || (step.kind === 'judgeRegister' ? '判断寄存器' : '设置寄存器')}」请至少选择一位有效寄存器。`;
            else if (step.kind === 'judgeRegister' && !['all','any'].includes(settings.match)) error = '寄存器条件关系无效。';
        }
        visit(step.trueSteps); visit(step.falseSteps); visit(step.fireSteps); visit(step.beforeAttemptSteps);
    });
    visit(steps); return error;
}
