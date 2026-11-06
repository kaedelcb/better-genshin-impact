const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const plain = value => JSON.parse(JSON.stringify(value));
function snapshot(value = 1) {
    return {available:true, entries:Array.from({length:10}, (_, i) => ({
        number:i + 1, name:i === 0 ? '采集完成' : '', value, resetMode:'none',
        resetHour:4, resetMinute:0, delaySeconds:1800, settingsRevision:'settings-1'
    }))};
}

// 仅提供设置窗口使用的控件接口；不启动浏览器，也不模拟布局。
function dialogDouble() {
    const fields = {}, nodes = {}, resets = ['one','all'].map(value => ({dataset:{registerReset:value}}));
    const dialog = {
        dataset:{}, open:false, html:'',
        set innerHTML(value) {
            this.html = value;
            for (const match of value.matchAll(/<input data-register-setting="([^"]+)" value="([^"]*)"/g))
                fields[match[1]] = {value:match[2]};
            fields.resetMode = {value:'none'};
        },
        get innerHTML() { return this.html; },
        querySelector(selector) {
            const match = selector.match(/^\[data-register-setting="([^"]+)"\]$/);
            return match ? fields[match[1]] : nodes[selector] ||= {textContent:''};
        },
        querySelectorAll(selector) {
            if (selector === '[data-register-reset]') return resets;
            if (selector === '[data-register-reset],[data-register-set-one]') return [...resets,this.querySelector('[data-register-set-one]')];
            return [...Object.values(fields), this.querySelector('[data-register-save]'), this.querySelector('[data-register-close]')];
        },
        showModal() { this.open = true; },
        close() { this.open = false; this.onclose?.(); }
    };
    return {dialog,fields};
}
function setup(options = {}) {
    const calls = [], notices = [], confirmations = [], {dialog,fields} = dialogDouble();
    const ctx = vm.createContext({console, setTimeout, clearTimeout, Map, Set,
        mt:{target:{key:'executor-a'},active:true,tab:'startup'},
        document:{querySelectorAll:() => []},
        mtTargetReady:() => true, mtOwner:() => true, mtEl:() => null,
        mtPick:(object, key) => !object ? undefined : key in object ? object[key] : object[key[0].toUpperCase() + key.slice(1)],
        mtClone:structuredClone, mtNewId:() => 'new-step', mtDialog:() => dialog,
        mte:value => String(value ?? '').replace(/[<>&"]/g, c => ({'<':'&lt;','>':'&gt;','&':'&amp;','"':'&quot;'}[c])),
        mtToast:(...args) => notices.push(args),
        confirm:message => { confirmations.push(message); return options.confirm ? options.confirm(message) : true; },
        mtOperate:async (...args) => { calls.push(args); return options.operate ? options.operate(...args) : {status:'ok',data:snapshot(0)}; },
        mtRead:async (...args) => options.read ? options.read(...args) : {status:'ok',data:snapshot()}
    });
    for (const file of ['mistletoe-registers.js','mistletoe-startup.js'])
        vm.runInContext(fs.readFileSync(path.join(__dirname,'../../BgiCoordinatorServer/wwwroot',file),'utf8'),ctx);
    const run = source => vm.runInContext(source,ctx);
    const apply = data => { ctx.incoming = structuredClone(data); return run('mtRegistersApply(incoming)'); };
    return {ctx,run,apply,calls,notices,confirmations,dialog,fields};
}

test('快照必须确认全部十位；损坏或缺能力不冒充全零且阻断归零', async () => {
    const x = setup(); x.apply(snapshot());
    assert.equal(x.run('mtRegisters.available'),true);
    for (const change of [data => data.entries.pop(), data => data.entries[2].number = 1, data => data.entries[2].value = 2]) {
        const bad = snapshot(); change(bad); x.apply(bad);
        assert.equal(x.run('mtRegisters.available'),false);
        await x.run('mtResetRegisters()');
        assert.equal(x.calls.length,0);
    }
    x.apply({available:false,error:'执行端读取失败',entries:[]});
    assert.equal(x.run('mtRegistersSupported()'),true);
    assert.equal(x.run('mtRegisters.error'),'执行端读取失败');
    x.apply(null);
    assert.equal(x.run('mtRegistersSupported()'),false);
    assert.equal(x.run('mtRegisters.entries[0].value'),1);
});

test('操作前旧版本快照和切换执行端后的完成回复不能覆盖新状态', async () => {
    let finish;
    const x = setup({operate:() => new Promise(resolve => finish = resolve)});
    x.apply(snapshot());
    const version = x.run('mtRegistersVersion()');
    const pending = x.run('mtResetRegisters(1)');
    x.ctx.old = snapshot(0); x.ctx.oldVersion = version;
    assert.equal(x.run('mtRegistersApply(old,oldVersion)'),false);
    x.run("mt.target={key:'executor-b'};mtRegistersResetState();mt.target={key:'executor-a'};mtRegistersResetState()");
    finish({status:'ok',data:snapshot(0)}); await pending;
    assert.equal(x.run('mtRegisters.entries.length'),0);
    assert.equal(x.run('mtRegisters.available'),false);
    assert.equal(x.run('mtRegisters.busy'),false);
});

test('三态参数区分保持与置零，判断支持全部和任一且不改变寄存器值', () => {
    const x = setup(); x.apply(snapshot());
    x.run("globalThis.write=mtNewStep('setRegister');globalThis.condition=mtNewStep('judgeRegister')");
    assert.equal(x.run('write.nodeType'),'action');
    assert.equal(x.run('condition.nodeType'),'condition');
    assert.deepEqual(plain(x.run('write.registerWrite.values')),Array(10).fill(null));
    assert.equal(x.run('mtChains(condition).length'),2);
    assert.match(x.run('mtValidateRegisterSteps([write])'),/至少选择/);
    x.run('write.registerWrite.values[0]=0;write.registerWrite.values[9]=1;condition.registerCondition.values[1]=0;condition.registerCondition.values[4]=1');
    assert.equal(x.run('mtRegisterSummary(write)'),'R1←0，R10←1');
    assert.equal(x.run('mtRegisterSummary(condition)'),'R2=0 且 R5=1');
    x.run("condition.registerCondition.match='any'");
    assert.equal(x.run('mtRegisterSummary(condition)'),'R2=0 或 R5=1');
    assert.equal(x.run('mtValidateRegisterSteps([write,condition])'),'');
    assert.match(x.run('mtRegisterFields(condition)'),/忽略/);
    assert.match(x.run('mtRegisterFields(write)'),/保持/);
    assert.equal(x.run('mtRegisters.entries[0].value'),1);
    x.run("condition.registerCondition.match='invalid'");
    assert.match(x.run('mtValidateRegisterSteps([condition])'),/条件关系无效/);
});

test('四类子链包括每次任务开始前都校验寄存器，无效参数原样保留', () => {
    const x = setup(); x.apply(snapshot());
    for (const key of ['trueSteps','falseSteps','fireSteps','beforeAttemptSteps']) {
        x.ctx.chainKey = key;
        x.run("globalThis.bad=mtNewStep('setRegister');globalThis.parent={kind:'logTrigger',[chainKey]:[bad]}");
        assert.match(x.run('mtValidateRegisterSteps([parent])'),/至少选择/);
        x.run('bad.registerWrite.values[2]=1');
        assert.equal(x.run('mtValidateRegisterSteps([parent])'),'');
        x.run('bad.registerWrite.values[2]=9');
        assert.match(x.run('mtRegisterFields(bad)'),/原值/);
        assert.equal(x.run('bad.registerWrite.values[2]'),9);
    }
    x.apply(null);
    assert.match(x.run('mtValidateRegisterSteps([parent])'),/未提供寄存器能力/);
});

test('单位置零与全部归零发送明确且互斥的参数，只采用执行端确认状态', async () => {
    const x = setup(); x.apply(snapshot());
    await x.run('mtResetRegisters(4)');
    assert.equal(x.calls[0][0],'registers.reset');
    assert.deepEqual(plain(x.calls[0][1]),{number:4});
    await x.run('mtResetRegisters()');
    assert.deepEqual(plain(x.calls[1][1]),{all:true});
    assert.equal(x.run('mtRegisters.entries.every(entry=>entry.value===0)'),true);
    assert.match(x.run('mtRegisters.status'),/已全部归零/);
});

test('本位归一使用明确位号和1，不保存草稿、不改其他位且不关闭窗口', async () => {
    const initial = snapshot(0); initial.entries[8].value = 1;
    const x = setup({operate:(operation,payload) => {
        const data = structuredClone(initial); data.entries[payload.number - 1].value = payload.value;
        return {status:'ok',data};
    }});
    x.apply(initial); x.run('mtOpenRegisterSettings(3)');
    x.fields.name.value = '未保存名称'; x.fields.resetMode.value = 'daily';
    x.fields.resetTime.value = '06:25';
    assert.match(x.dialog.innerHTML,/本位归一/);
    await x.dialog.querySelector('[data-register-set-one]').onclick();
    assert.equal(x.calls[0][0],'registers.set');
    assert.deepEqual(plain(x.calls[0][1]),{number:3,value:1});
    assert.deepEqual(plain(x.run('mtRegisters.entries.filter(e=>e.number!==3)')),initial.entries.filter(e=>e.number!==3));
    assert.equal(x.fields.name.value,'未保存名称'); assert.equal(x.fields.resetMode.value,'daily');
    assert.equal(x.fields.resetTime.value,'06:25'); assert.equal(x.dialog.open,true);
    assert.match(x.dialog.querySelector('[data-register-state]').textContent,/当前状态：1/);
    assert.equal(x.run('mtRegisters.status'),'R3 已归一。');
    assert.equal(x.confirmations.length,0);
});

test('全部归零必须确认，取消不发请求且保留当前值和设置草稿', async () => {
    let approved = false;
    const x = setup({confirm:() => approved}); x.apply(snapshot()); x.run('mtOpenRegisterSettings(3)');
    x.fields.name.value = '保留草稿';
    const all = x.dialog.querySelectorAll('[data-register-reset]').find(b => b.dataset.registerReset === 'all');
    await all.onclick();
    assert.equal(x.calls.length,0); assert.equal(x.run('mtRegisters.entries[2].value'),1);
    assert.equal(x.fields.name.value,'保留草稿'); assert.equal(x.dialog.open,true);
    assert.match(x.confirmations[0],/R1–R10 全部归零/);
    approved = true; await all.onclick();
    assert.equal(x.calls.length,1); assert.deepEqual(plain(x.calls[0][1]),{all:true});
    assert.equal(x.confirmations.length,2);
    await x.run('mtResetRegisters(3)');
    assert.equal(x.confirmations.length,2);
});

test('归一等待执行端时禁止重复操作，未知回执不虚构1', async () => {
    let finish;
    const x = setup({operate:() => new Promise(resolve => finish = resolve)});
    x.apply(snapshot(0)); x.run('mtOpenRegisterSettings(1)');
    const one = x.dialog.querySelector('[data-register-set-one]');
    const pending = one.onclick();
    assert.equal(one.disabled,true);
    await one.onclick(); await x.run('mtResetRegisters(1)');
    assert.equal(x.calls.length,1); assert.equal(x.run('mtRegisters.entries[0].value'),0);
    finish({status:'unknown',message:'终态查询超时'}); await pending;
    assert.equal(x.run('mtRegisters.available'),false);
    assert.equal(x.run('mtRegisters.entries[0].value'),0);
    assert.match(x.run('mtRegisters.status'),/归一结果未确认/);
    assert.equal(one.disabled,true);
});

test('手动归零不覆盖窗口草稿，保存仍携带打开时的设置修订及完整规则', async () => {
    const x = setup({operate:(operation,payload) => {
        const data = snapshot(0);
        if (operation === 'registers.configure') Object.assign(data.entries[payload.number - 1],payload,{settingsRevision:'settings-2'});
        return {status:'ok',data};
    }});
    x.apply(snapshot()); x.run('mtOpenRegisterSettings(3)');
    Object.assign(x.fields.name,{value:'未保存名称'});
    Object.assign(x.fields.resetMode,{value:'daily'});
    Object.assign(x.fields.resetTime,{value:'06:25'});
    Object.assign(x.fields.days,{value:'1'}); Object.assign(x.fields.hours,{value:'2'});
    Object.assign(x.fields.minutes,{value:'3'}); Object.assign(x.fields.seconds,{value:'4'});
    await x.run('mtResetRegisters(3)');
    assert.equal(x.dialog.open,true);
    assert.equal(x.fields.name.value,'未保存名称');
    assert.equal(x.fields.resetMode.value,'daily');
    assert.equal(x.fields.resetTime.value,'06:25');
    assert.equal(x.fields.seconds.value,'4');
    assert.match(x.dialog.querySelector('[data-register-state]').textContent,/当前状态：0/);
    await x.dialog.querySelector('[data-register-save]').onclick();
    assert.equal(x.calls[1][0],'registers.configure');
    assert.deepEqual(plain(x.calls[1][1]),{number:3,name:'未保存名称',resetMode:'daily',resetHour:6,resetMinute:25,delaySeconds:93784,expectedSettingsRevision:'settings-1'});
    assert.equal(x.dialog.open,false);
});

test('未知归零结果不能被更早读请求当作成功，后续最新快照保留未知结论', async () => {
    let finishRead;
    const x = setup({read:() => new Promise(resolve => finishRead = resolve),operate:() => ({status:'unknown',message:'终态查询超时'})});
    x.apply(snapshot());
    const reading = x.run('mtRegistersPoll()');
    await x.run('mtResetRegisters(1)');
    finishRead({status:'ok',data:snapshot()}); await reading;
    assert.equal(x.run('mtRegisters.loading'),false);
    assert.equal(x.run('mtRegisters.available'),false);
    assert.match(x.run('mtRegisters.status'),/结果未确认/);
    x.apply(snapshot(0));
    assert.equal(x.run('mtRegisters.available'),true);
    assert.match(x.run('mtRegisters.status'),/结果未确认/);
});

test('相同状态轮询保留寄存器按钮和事件引用，状态或写入许可变化才重绘', () => {
    const x = setup(); x.apply(snapshot());
    let html = '', buttons = [], reset, replacements = 0;
    const host = {
        dataset:{},classList:{add() {}},
        get innerHTML() { return html; },
        set innerHTML(value) {
            html = value; replacements++;
            buttons = [...value.matchAll(/data-register-number="(\d+)"/g)].map(match => ({dataset:{registerNumber:match[1]}}));
            reset = {};
        },
        querySelectorAll() { return buttons; },
        querySelector() { return reset; }
    };
    x.ctx.strip = host;
    x.ctx.document.querySelectorAll = selector => selector === '[data-register-strip]' ? [host] : [];
    x.run('mtRenderRegisterStrip(strip)');
    const button = buttons[0], click = button.onclick;
    assert.equal(typeof click,'function');
    x.apply(snapshot()); x.apply(snapshot());
    assert.equal(replacements,1);
    assert.equal(buttons[0],button);
    assert.equal(buttons[0].onclick,click);
    x.apply(snapshot(0));
    assert.equal(replacements,2);
    assert.notEqual(buttons[0],button);
    x.ctx.mtTargetReady = () => false;
    x.apply(snapshot(0));
    assert.equal(replacements,3);
    assert.match(html,/data-register-number="1"[^>]* disabled/);
});
