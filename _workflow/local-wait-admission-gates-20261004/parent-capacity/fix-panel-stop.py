from pathlib import Path
for name in ['MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs']:
    p=Path(name); b=p.read_bytes(); Path('_workflow/local-wait-admission-gates-20261004/parent-capacity/'+p.name+'.before').write_bytes(b)
p=Path('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs'); t=p.read_bytes().decode()
old='    public HostActionResult RequestRunAction(string runId, WorkflowRunAction action)\r\n    {'
new='''    public HostActionResult RequestRunAction(string runId, WorkflowRunAction action)
        => RequestRunActionAsync(runId, action).GetAwaiter().GetResult();

    /// <summary>面板异步动作入口：等待原身份对账/终局写回时让出调用线程，结果仍按原耐久事实判定。</summary>
    public async Task<HostActionResult> RequestRunActionAsync(string runId, WorkflowRunAction action)
    {'''.replace('\n','\r\n')
assert t.count(old)==1;t=t.replace(old,new)
t=t.replace('return StopParkedRun(runId);','return await StopParkedRunAsync(runId).ConfigureAwait(false);')
t=t.replace('private HostActionResult StopParkedRun(string runId)','private async Task<HostActionResult> StopParkedRunAsync(string runId)')
t=t.replace('return ReconcileAdmissionTerminalForExplicitStop(runId,','return await ReconcileAdmissionTerminalForExplicitStopAsync(runId,')
t=t.replace('"运行已终态化，关联受理登记已核对");','"运行已终态化，关联受理登记已核对").ConfigureAwait(false);')
t=t.replace('"已放弃停驻运行（终态化，未触发收尾）");','"已放弃停驻运行（终态化，未触发收尾）").ConfigureAwait(false);')
t=t.replace('"已停止（Unknown 经原身份只读对账，全部事实清偿）");','"已停止（Unknown 经原身份只读对账，全部事实清偿）").ConfigureAwait(false);')
t=t.replace('return ReconcileUnknownRunForStopAsync(runId).GetAwaiter().GetResult();','return await ReconcileUnknownRunForStopAsync(runId).ConfigureAwait(false);')
p.write_bytes(t.encode())
p=Path('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs'); t=p.read_bytes().decode()
t=t.replace('private HostActionResult ReconcileAdmissionTerminalForExplicitStop(string runId, string successMessage)','private async Task<HostActionResult> ReconcileAdmissionTerminalForExplicitStopAsync(string runId, string successMessage)')
t=t.replace('outcome = work.WaitAsync(timeout).GetAwaiter().GetResult();','outcome = await work.WaitAsync(timeout).ConfigureAwait(false);')
t=t.replace('=> Task.Run(() => ReconcileAdmissionTerminalCoreAsync(runId));','=> ReconcileAdmissionTerminalCoreAsync(runId);')
p.write_bytes(t.encode())
p=Path('MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs');t=p.read_bytes().decode()
for action in ['Stop','SkipCurrent','Pause','ReloadDefinition']:
    old='new(p => ApplyRunAction(p, WorkflowRunAction.'+action+'));'
    new='new(async p => await ApplyRunActionAsync(p, WorkflowRunAction.'+action+'));'
    assert t.count(old)==1;t=t.replace(old,new)
t=t.replace('private void ApplyRunAction(object? p, WorkflowRunAction action)','private async Task ApplyRunActionAsync(object? p, WorkflowRunAction action)')
t=t.replace('ApplyActionResult(_host.RequestRunAction(vm.RunId, action));','ApplyActionResult(await _host.RequestRunActionAsync(vm.RunId, action));')
p.write_bytes(t.encode())
