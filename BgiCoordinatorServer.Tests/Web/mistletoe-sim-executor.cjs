// 槲寄生模拟执行端：在浏览器页面里用真实 SignalR 网关入房，监听 mistletoe.requested，按 PC 助手
// MainViewModel.MistletoeWeb 的同一形状回复（status/message/data/revision/operationId/targetInstanceId）。
// 数据形状取自 RemoteCenterProtocol / StartupFlowModels / WorkflowRunModels 的 JSON 命名：
// 记录类型（RemoteTriggerInfo、WorkflowCatalogEntry 等）是 PascalCase，带 JsonPropertyName 的是 camelCase，枚举是数字。
// 这是协议级模拟：不运行真实启动流程、BGI 或游戏。
module.exports = function installSimExecutor() {
  const sim = window.sim = {
    mode: 'ok', requests: [], operations: new Map(), owner: { browserId: 'wb-sim', secret: 'b'.repeat(64) },
    code: '', authorized: new Set(), runHold: false, prompts: [], triggers: [], states: [], silentOps: new Set(),
    startup: { enabled: false, delaySeconds: 5, autoExpandBranch: true, steps: [] }, startupRevision: 'rev-s1',
    schemes: [], schemesRevision: 'rev-sc1', autoLaunch: { enabled: false, mode: 0 },
    flows: new Map(), runs: [], history: [], deleted: [], catalog: { entries: [], isDegraded: false },
    bgiPath: { path: 'D:\\BGI\\BetterGI.exe', revision: 'bp-1' }, imports: [], migrations: [],
    layout: { tasks: [{ taskId: 't1', name: '每日委托', enabled: true }, { taskId: 't2', name: '纪行', enabled: true }], startTaskId: null, revision: 'cfg-r1' }
  };
  let counter = 0;
  const env = (type, name, payload) => ({ protocolVersion: 3, type, name, payload, requestId: 'sim' + (++counter), sentAtUtc: new Date().toISOString() });
  sim.connect = async ({ room, secret, uid, instanceId, name }) => {
    sim.uid = uid; sim.instanceId = instanceId;
    sim.conn = new signalR.HubConnectionBuilder().withUrl('/gateway').build();
    sim.send = (n, payload) => sim.conn.invoke('Dispatch', env('command', n, { roomCode: room, ...payload }));
    sim.conn.on('evt', e => { if (e.name === 'mistletoe.requested') handle(e.payload || {}).catch(err => console.error(err)); });
    await sim.conn.start();
    await sim.conn.invoke('Query', env('query', 'session.hello', { clientKind: 'assistant', clientVersion: 'sim', protocolVersion: 3, capabilities: [] }));
    await sim.send('control.joinRoom', { password: secret, playerUid: uid, playerName: name, allowedUids: [], isRemote: false, clientInstanceId: instanceId, machineId: 'sim-machine' });
  };
  const reply = (status, message, data, revision, operationId) => ({ status, message: message || '', data: data ?? null, revision: revision ?? null, operationId: operationId ?? null, targetInstanceId: sim.instanceId });
  const flowEntry = f => ({ WorkflowId: f.doc.workflowId, Name: f.doc.name, Revision: f.revision, Status: f.quarantined ? 1 : 0, QuarantineReason: f.quarantined ? '模拟隔离' : null, UnsupportedKinds: [], ActivationStatus: f.candidate ? 'candidate-ready' : null, FilePath: 'C:\\sim\\' + f.doc.workflowId + '.flow.json' });
  const rev = () => 'rev-' + Math.random().toString(16).slice(2, 10);
  // 持久回执：受理（accepted）→ 终态；runHold=true 时停在受理状态，用于验证「执行中」和台账阻断。
  function track(id, finish, delay = 120) {
    const accepted = reply('accepted', '执行端已受理，正在核对运行条件', null, null, id);
    sim.operations.set(id, accepted);
    const settle = () => { if (sim.runHold) return setTimeout(settle, 80); sim.operations.set(id, { ...finish(), operationId: id, targetInstanceId: sim.instanceId }); };
    setTimeout(settle, delay);
    return accepted;
  }
  function immediate(id, value) { const r = { ...value, operationId: id, targetInstanceId: sim.instanceId }; sim.operations.set(id, r); return r; }
  async function handle(x) {
    const request = x.request || {}, op = request.operation, id = 'web-' + request.requestId;
    sim.requests.push({ op, requestId: request.requestId, payload: request.payload, expectedRevision: request.expectedRevision, owner: !!request.browserSecret, at: Date.now() });
    if (sim.mode === 'silent' || sim.silentOps.has(op)) return;
    let r;
    try { r = execute(request, op, id); }
    catch (error) { r = reply('rejected', String(error.message || error)); }
    if (sim.mode === 'drop-reply') return;
    await sim.send('mistletoe.respond', { requestId: x.requestId, reply: r });
  }
  function execute(request, op, id) {
    if (op !== 'web.identity' && request.targetInstanceId !== sim.instanceId) return reply('rejected', '执行端实例已变化，请重新选择执行端；本次未执行');
    const owner = request.browserId === sim.owner.browserId && request.browserSecret === sim.owner.secret;
    if (op === 'web.identity') return reply('ok', '', { uid: sim.uid, name: '模拟执行端', instanceId: sim.instanceId, machineId: 'sim-machine', isExecutor: true, owner, capabilities: ['startup', 'taskCenter'] });
    if (op === 'web.authorize') {
      if (!sim.code || request.code !== sim.code) return reply('rejected', '授权码不正确');
      sim.code = '';
      return reply('ok', '已授权此浏览器', { browserId: sim.owner.browserId, secret: sim.owner.secret });
    }
    if (!owner) return reply('denied', '此浏览器尚未获得本人授权：请在执行端助手「设置 → 网页授权」生成授权码后输入');
    const payload = request.payload;
    switch (op) {
      case 'startup.get': return reply('ok', '', { config: sim.startup, revision: sim.startupRevision, schemes: sim.schemes, schemesRevision: sim.schemesRevision, IsRunning: sim.running === true, StatusText: sim.statusText || '', autoLaunchOnBoot: sim.autoLaunch.enabled, autoLaunchOnBootModeIndex: sim.autoLaunch.mode, autoLaunchRevision: 'al-' + sim.autoLaunch.enabled + sim.autoLaunch.mode, triggers: sim.triggers, prompts: sim.prompts, nodeStates: sim.states }, sim.startupRevision);
      case 'startup.status': return reply('ok', '', { isRunning: sim.running === true, statusText: sim.statusText || '', triggers: sim.triggers, prompts: sim.prompts, nodeStates: sim.states });
      case 'startup.save':
        if (request.expectedRevision !== sim.startupRevision) return immediate(id, reply('rejected', '启动流程修订已变化，请刷新后再保存；原文件未覆盖'));
        sim.startup = payload; sim.startupRevision = rev();
        return immediate(id, reply('ok', '执行端启动中心已保存', null, sim.startupRevision));
      case 'startup.schemes.save':
        if (request.expectedRevision !== sim.schemesRevision) return immediate(id, reply('rejected', '启动中心方案库已被修改，请刷新后再保存；原文件未覆盖'));
        sim.schemes = payload; sim.schemesRevision = rev();
        return immediate(id, reply('ok', '执行端方案库已保存', null, sim.schemesRevision));
      case 'startup.autoLaunch.save':
        if (request.expectedRevision !== 'al-' + sim.autoLaunch.enabled + sim.autoLaunch.mode) return immediate(id, reply('rejected', '执行端开机自启动设置已变化，请重新读取后再修改'));
        sim.autoLaunch = { enabled: payload.enabled, mode: payload.modeIndex };
        return immediate(id, reply('ok', '执行端开机自启动设置已应用', null, 'al-' + sim.autoLaunch.enabled + sim.autoLaunch.mode));
      case 'startup.programs.list':
        if (!payload) return reply('ok', '', { path: '', directories: ['C:\\', 'D:\\'], programs: [] });
        return reply('ok', '', { path: payload, directories: [payload.replace(/\\?$/, '\\') + 'Games'], programs: [payload.replace(/\\?$/, '\\') + 'YuanShen.exe', payload.replace(/\\?$/, '\\') + 'BetterGI.exe'] });
      case 'startup.run':
        if (sim.running) return reply('rejected', '启动流程已在运行');
        sim.running = true; sim.statusText = '执行中：' + (sim.startup.steps[0]?.name || '第一个节点');
        sim.states = sim.startup.steps.length ? [{ StepId: sim.startup.steps[0].id, State: 1, Note: '' }] : [];
        return track(id, () => reply('ok', '启动流程已执行完成'), 300);
      case 'startup.cancel': sim.running = false; sim.statusText = '已取消'; sim.cancelCount = (sim.cancelCount || 0) + 1; return reply('accepted', '执行端已受理取消请求');
      case 'startup.prompt.answer': { const p = sim.prompts.find(x => x.RuntimeId === payload.runtimeId); if (!p) return reply('rejected', '人工确认已结束或身份已变化'); sim.prompts = sim.prompts.filter(x => x !== p); sim.answer = payload.yes; return reply('ok', '执行端已接收人工确认'); }
      case 'startup.trigger.cancel': { const t = sim.triggers.find(x => x.RuntimeId === payload); if (!t) return reply('rejected', '执行端已找不到这条触发器；请刷新状态'); sim.triggers = sim.triggers.filter(x => x !== t); return reply('ok', '执行端已取消触发器'); }
      case 'startup.runStates.clear': sim.states = []; return reply('ok', '执行端执行路径显示已清除');
      case 'task.list': return reply('ok', '', {
        serverTime: new Date().toISOString(), flows: [...sim.flows.values()].map(flowEntry), active: sim.runs, history: sim.history,
        catalog: sim.catalog, storedResourceEntries: [], deleted: sim.deleted,
        activeFlowSnapshots: Object.fromEntries(sim.runs.filter(r => sim.flows.has(r.workflowId)).map(r => [r.workflowId, { Document: sim.flows.get(r.workflowId).doc, Revision: sim.flows.get(r.workflowId).revision }])),
        drivingWorkflowIds: sim.runs.filter(r => r.driving).map(r => r.workflowId) });
      case 'task.flow.get': { const f = sim.flows.get(payload); if (!f) return reply('rejected', '执行端找不到计划'); return reply('ok', '', f.doc, f.revision); }
      case 'task.flow.save': {
        const f = sim.flows.get(payload.workflowId);
        if (f && request.expectedRevision !== f.revision) return immediate(id, reply('rejected', `流程 ${payload.workflowId} 修订冲突（文件已被外部或并发修改，未覆盖）。`));
        if (!f && request.expectedRevision) return immediate(id, reply('rejected', `流程 ${payload.workflowId} 不存在，但携带了期望修订（可能已被外部删除）。`));
        const revision = rev(); sim.flows.set(payload.workflowId, { doc: payload, revision }); sim.saveCount = (sim.saveCount || 0) + 1;
        return immediate(id, reply('ok', '执行端任务计划已保存', { WorkflowId: payload.workflowId }, revision));
      }
      case 'task.run.start': {
        if (sim.runs.some(r => r.workflowId === payload.workflowId)) return track(id, () => reply('rejected', '该计划已有运行，未重复启动'));
        const f = sim.flows.get(payload.workflowId); if (!f || f.revision !== payload.expectedRevision) return track(id, () => reply('rejected', '计划修订已变化，未启动'));
        sim.startCount = (sim.startCount || 0) + 1; sim.lastStart = payload;
        const runId = 'run-' + Math.random().toString(16).slice(2, 10);
        sim.runs.push({ runId, workflowId: payload.workflowId, workflowRevision: f.revision, state: 2, driving: true, stopRequested: false, cursor: { nodeId: payload.entryNodeId || f.doc.nodes[0]?.nodeId, loopIteration: 0 }, nodeOutcomes: [], updatedAt: new Date().toISOString(), note: '' });
        return track(id, () => reply('ok', '已受理计划，运行开始'));
      }
      case 'task.run.action': {
        const run = sim.runs.find(r => r.runId === payload.runId); if (!run) return reply('rejected', '运行不存在');
        if (payload.action === 'Stop') { sim.stopCount = (sim.stopCount || 0) + 1; sim.runs = sim.runs.filter(r => r !== run); sim.history.unshift({ ...run, state: 6, updatedAt: new Date().toISOString(), note: '用户停止' }); return reply('ok', '已请求停止当前运行'); }
        return track(id, () => { if (payload.action === 'Pause') run.state = 3; return reply('ok', '已请求' + payload.action); });
      }
      case 'task.run.resume': { const run = sim.runs.find(r => r.runId === payload); return track(id, () => { if (run) run.state = 2; return run ? reply('ok', '已恢复运行') : reply('rejected', '运行不存在'); }); }
      case 'task.catalog.connect': return track(id, () => { sim.catalog = { entries: [{ kind: 0, stableId: 'onedragon:每日', displayName: '每日', configRevision: 'cfg-r1' }, { kind: 1, stableId: 'group:锄地', displayName: '锄地', configRevision: 'cfg-g1' }, { kind: 2, stableId: 'single:每日:t1', displayName: '每日委托', ownerConfig: '每日', configRevision: 'cfg-r1', enabled: true, singleExecutionSupported: true }], isDegraded: false }; return reply('ok', '资源目录已刷新'); }, 200);
      case 'task.resource.layout.get': return reply('ok', '', { Tasks: sim.layout.tasks.map(t => ({ TaskId: t.taskId, Name: t.name, Enabled: t.enabled })), StartTaskId: sim.layout.startTaskId, Revision: sim.layout.revision }, sim.layout.revision);
      case 'task.resource.layout.save': sim.layout = { tasks: payload.tasks, startTaskId: payload.startTaskId, revision: rev() }; return immediate(id, reply('ok', 'BGI 配置已保存；请同步当前计划引用修订', null, sim.layout.revision));
      case 'task.resource.revision.get': return reply('ok', '', null, sim.layout.revision);
      case 'task.flow.export': { const f = sim.flows.get(payload); return f ? reply('ok', '已读取执行端计划导出副本', JSON.stringify(f.doc)) : reply('rejected', '执行端找不到计划'); }
      case 'task.flow.delete': { const f = sim.flows.get(payload.workflowId); if (!f || f.revision !== request.expectedRevision) return immediate(id, reply('rejected', '计划已被修改，未删除；请刷新后重新确认。')); sim.flows.delete(payload.workflowId); sim.deleted.push({ RecoveryId: 'rec-' + payload.workflowId, WorkflowId: payload.workflowId, Name: f.doc.name, Revision: f.revision, RecoveryFilePath: 'C:\\sim\\deleted', DeletedAt: new Date().toISOString(), CanRestore: true, RecoveryProblem: null }); return immediate(id, reply('ok', '计划已删除，可在管理里恢复')); }
      // 以下与 MainViewModel.RemoteCenter 的同名分支同一形状：bgiPath.save / flow.restore / editor.open 立即回执，其余走持久回执。
      case 'task.bgiPath.get': return reply('ok', '', { path: sim.bgiPath.path }, sim.bgiPath.revision);
      case 'task.bgiPath.save':
        if (typeof payload !== 'string' || !/^[A-Za-z]:\\/.test(payload) || !/(^|\\)BetterGI\.exe$/i.test(payload)) return immediate(id, reply('rejected', '请选择执行端上存在的 BetterGI.exe'));
        if (request.expectedRevision !== sim.bgiPath.revision) return immediate(id, reply('rejected', 'BGI 路径已被修改，请重新读取'));
        sim.bgiPath = { path: payload, revision: rev() }; return immediate(id, reply('ok', '执行端 BGI 路径已保存', { path: payload }, sim.bgiPath.revision));
      case 'task.flow.import':
        if (typeof payload !== 'string' || payload.length > 2000000) return reply('rejected', '导入计划超过大小限制');
        return track(id, () => { const doc = JSON.parse(payload); doc.workflowId = 'wf-imp-' + (sim.imports.length + 1); sim.imports.push(doc); sim.flows.set(doc.workflowId, { doc, revision: rev() }); return reply('ok', '已在执行端导入计划 ' + doc.workflowId); });
      case 'task.schedule.export': return reply('ok', '已读取执行端整份调度列表导出副本', JSON.stringify({ schema: 'mistletoe.schedule', flows: [...sim.flows.values()].map(f => f.doc) }));
      case 'task.schedule.import':
        if (typeof payload !== 'string' || payload.length > 2000000) return reply('rejected', '调度列表超过大小限制');
        return track(id, () => { const list = JSON.parse(payload).flows || []; list.forEach((d, i) => { const wid = 'wf-cand-' + (i + 1); sim.flows.set(wid, { doc: { ...d, workflowId: wid }, revision: rev(), candidate: true }); }); return reply('ok', `已在执行端导入 ${list.length} 个只读候选`); });
      case 'task.flow.restore': {
        const d = sim.deleted.find(x => x.RecoveryId === payload); if (!d) return immediate(id, reply('rejected', '恢复记录不存在'));
        sim.deleted = sim.deleted.filter(x => x !== d); sim.flows.set(d.WorkflowId, { doc: { schema: 'mistletoe.workflow', schemaVersion: 1, workflowId: d.WorkflowId, name: d.Name, nodes: [], triggers: [], terminal: [] }, revision: rev() });
        return immediate(id, reply('ok', '计划已恢复'));
      }
      case 'task.flow.resetExecutionState': return track(id, () => { sim.resetCount = (sim.resetCount || 0) + 1; sim.runs = sim.runs.filter(r => r.workflowId !== payload); return reply('ok', '执行状态已重置；计划未自动启动'); });
      case 'task.migration.prepare':
        if (typeof payload !== 'string' || !/^[A-Za-z]:\\/.test(payload)) return reply('rejected', '迁移来源必须是执行端上的绝对目录');
        return track(id, () => { sim.migrations.push(payload); sim.flows.set('wf-mig', { doc: { schema: 'mistletoe.workflow', schemaVersion: 1, workflowId: 'wf-mig', name: '旧版迁移候选', nodes: [], triggers: [], terminal: [] }, revision: rev(), candidate: true }); return reply('ok', '已生成迁移候选'); });
      case 'task.migration.activate': return track(id, () => { const f = sim.flows.get(payload); if (!f?.candidate) return reply('rejected', '不是迁移候选'); f.candidate = false; f.revision = rev(); return reply('ok', '迁移候选已激活'); });
      case 'task.migration.rollback': return track(id, () => { sim.rollbackCount = (sim.rollbackCount || 0) + 1; return reply('rejected', '该计划没有可回退的手动迁移'); });
      case 'task.migration.rehearsal': return track(id, () => reply('ok', '迁移演练通过：3 个计划可迁移'));
      case 'task.resource.editor.open': sim.editorOpened = payload; return immediate(id, reply('ok', '已在执行端桌面打开 BGI 资源编辑器'));
      case 'operation.get': return sim.operations.get(payload) || reply('rejected', '执行端找不到这次操作，请刷新运行状态核对');
      default: return reply('rejected', '模拟执行端不支持操作：' + op);
    }
  }
};
