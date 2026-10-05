from pathlib import Path
import sys,json,hashlib
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent
def edit(rel,changes):
    p=root/rel;data=p.read_bytes();s.write(base/'before-entry'/rel,data)
    nl='\r\n' if b'\r\n' in data else '\n';text=data.decode('utf-8-sig').replace('\r\n','\n')
    for old,new in changes:
        assert text.count(old)==1,(rel,old[:80],text.count(old));text=text.replace(old,new)
    encoded=text.replace('\n',nl).encode();encoded=(b'\xef\xbb\xbf'+encoded) if data.startswith(b'\xef\xbb\xbf') else encoded
    assert len(encoded)>len(data)*.98;s.write(p,encoded,mode='wb')
with s.Session(root,'full-product-explicit-run-entry') as budget:
    budget.track(base)
    edit('MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowPlanner.cs',[
      ('    /// <summary>首轮首个节点出现（空链返回 null）。</summary>','''    /// <summary>显式单次起点只绑定唯一稳定节点，不修改流程定义或迁移水位。</summary>
    internal WorkflowNodeCursor? ExplicitEntryCursor(string? nodeId)
    {
        if (nodeId is null) return null;
        if (string.IsNullOrWhiteSpace(nodeId) || _doc.Nodes.Count(n => n.NodeId == nodeId) != 1)
            throw new InvalidOperationException("指定起点不存在或身份有歧义，禁止从链首执行。");
        return new WorkflowNodeCursor { NodeId = nodeId, Occurrence = 0, LoopIteration = 0, Attempt = 1 };
    }

    /// <summary>首轮首个节点出现（空链返回 null）。</summary>''')])
    edit('MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs',[
      ('    public string? MigrationEntrySeedKey { get; set; }','''    public string? MigrationEntrySeedKey { get; set; }

    /// <summary>用户为本次运行指定的起点；不属于流程定义，重启后禁止失效时回落链首。</summary>
    [JsonPropertyName("explicitEntryNodeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExplicitEntryNodeId { get; set; }''')])
    edit('MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs',[
      ('HandoffIdentity? handoff = null, string? admissionSourceScope = null, WorkflowStopAuthorityRecord? stopAuthority = null)','HandoffIdentity? handoff = null, string? admissionSourceScope = null, WorkflowStopAuthorityRecord? stopAuthority = null,\n        WorkflowNodeCursor? initialCursor = null)'),
      ('            StopAuthority = stopAuthority,','''            StopAuthority = stopAuthority,
            Cursor = initialCursor is null ? null : new WorkflowNodeCursor
            { NodeId = initialCursor.NodeId, Occurrence = initialCursor.Occurrence, LoopIteration = initialCursor.LoopIteration, Attempt = initialCursor.Attempt },
            ExplicitEntryNodeId = initialCursor?.NodeId,''')])
    edit('MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs',[
      ('long? explicitIntentTimestamp = null, string? explicitIntentId = null)','long? explicitIntentTimestamp = null, string? explicitIntentId = null, string? entryNodeId = null)'),
      ('        var run = _runs.CreateRun(workflowId, snapshot.Revision, stopAuthority: authority);','        var run = _runs.CreateRun(workflowId, snapshot.Revision, stopAuthority: authority, initialCursor: plan.ExplicitEntryCursor(entryNodeId));'),
      ('if (run.MigrationEntrySeedKey is not null && run.NodeOutcomes.Count == 0)','if ((run.MigrationEntrySeedKey is not null || run.ExplicitEntryNodeId is not null) && run.NodeOutcomes.Count == 0)'),
      ('迁移初始入口在当前修订中已失效，未执行、未回落链首。','初始入口在当前修订中已失效，未执行、未回落链首。')])
    edit('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs',[
      ('public async Task<HostActionResult> StartWorkflowAsync(string workflowId)','public async Task<HostActionResult> StartWorkflowAsync(string workflowId, string? entryNodeId = null, string? expectedRevision = null)'),
      ('        // 环境确保（2026-09-20，锁外有界等待）：BGI 未运行/通道未就绪','''        WorkflowNodeCursor? initialCursor;
        try
        {
            if (expectedRevision is not null && !string.Equals(expectedRevision, snapshot.Revision, StringComparison.OrdinalIgnoreCase))
                return HostActionResult.Unavailable("流程修订已改变，请重新预览并选择起点。");
            initialCursor = new WorkflowPlan(snapshot.Document).ExplicitEntryCursor(entryNodeId);
        }
        catch (Exception ex) { return HostActionResult.Unavailable(ex.Message); }

        // 环境确保（2026-09-20，锁外有界等待）：BGI 未运行/通道未就绪'''),
      ('SubmitFlowStartViaAdmissionAsync(workflowId, snapshot, explicitIntentId, explicitIntentTimestamp)','SubmitFlowStartViaAdmissionAsync(workflowId, snapshot, explicitIntentId, explicitIntentTimestamp, initialCursor)'),
      ('runner.StartAsync(workflowId, cts.Token, explicitIntentTimestamp, explicitIntentId)','runner.StartAsync(workflowId, cts.Token, explicitIntentTimestamp, explicitIntentId, entryNodeId)')])
    edit('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs',[
      ('string explicitIntentId, long explicitIntentTimestamp)\n    {','string explicitIntentId, long explicitIntentTimestamp, WorkflowNodeCursor? initialCursor = null)\n    {'),
      ('            stopAuthority: authority);','            stopAuthority: authority, initialCursor: initialCursor);')])
    edit('MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs',[
      ('    private void BeginPreview(string workflowId)','''    public sealed record StartPointVm(string WorkflowId, string Revision, string NodeId, string Label);
    public ObservableCollection<StartPointVm> StartPoints { get; } = [];
    private StartPointVm? _selectedStartPoint;
    public StartPointVm? SelectedStartPoint { get => _selectedStartPoint; set => SetProperty(ref _selectedStartPoint, value); }

    public RelayCommand StartFromNodeCommand => new(async _ =>
    {
        if (SelectedStartPoint is not { } point || _startResumeInFlight) return;
        _startResumeInFlight = true;
        try { ApplyActionResult(await _host.StartWorkflowAsync(point.WorkflowId, point.NodeId, point.Revision)); }
        catch (Exception ex) { SetStatus("指定起点启动失败：" + ex.Message, true); }
        finally { _startResumeInFlight = false; }
        Refresh();
    });

    private void BeginPreview(string workflowId)'''),
      ('            Previewing = WorkflowPreviewVm.Build(snapshot.Document, snapshot.Revision);','''            Previewing = WorkflowPreviewVm.Build(snapshot.Document, snapshot.Revision);
            StartPoints.Clear();
            SelectedStartPoint = null;
            if (snapshot.Document.Activation?.Status != "candidate-ready")
                foreach (var node in snapshot.Document.Nodes)
                    StartPoints.Add(new StartPointVm(workflowId, snapshot.Revision, node.NodeId,
                        $"{StartPoints.Count + 1}. {node.Ref?.Config ?? node.Kind} [{node.NodeId}]"));
            SelectedStartPoint = StartPoints.FirstOrDefault();'''),
      ('            SetStatus($"预览加载失败：{ex.Message}", isError: true);','            StartPoints.Clear(); SelectedStartPoint = null;\n            SetStatus($"预览加载失败：{ex.Message}", isError: true);')])
    edit('MultiplayerHoeingAssistant/Views/MistletoePage.xaml',[
      ('                                <ItemsControl ItemsSource="{Binding TaskCenter.Previewing.Lines}"','''                                <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
                                    <ComboBox ItemsSource="{Binding TaskCenter.StartPoints}" DisplayMemberPath="Label" SelectedItem="{Binding TaskCenter.SelectedStartPoint, Mode=TwoWay}" Style="{StaticResource GoldComboBox}" MinWidth="240"/>
                                    <Button Content="从选定节点启动" Style="{DynamicResource BtnGold}" Command="{Binding TaskCenter.StartFromNodeCommand}" Margin="8,0,0,0"/>
                                </StackPanel>
                                <TextBlock Text="本次运行从所选节点开始；计划定义与迁移once水位保持。" Foreground="{DynamicResource Dim}" FontSize="10" Margin="0,4,0,0"/>
                                <ItemsControl ItemsSource="{Binding TaskCenter.Previewing.Lines}"''')])
    s.write(base/'entry-scope.json',json.dumps(dict(function='C06 explicit single-run start via preview UI',preserves='workflow definition and migration once key',checks=['unique node','preview revision before environment','atomic cursor+entry marker','lost initial identity fails before fallback'],independent_closed=False),ensure_ascii=False,indent=2).encode())
