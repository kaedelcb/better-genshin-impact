using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// TaskCenterPanelViewModel（R4.8 Batch D/E）组件夹具（设计稿 §4.7/4.8）：
/// 列表加载三态（active 可启动可编辑 / candidate-ready 禁启动禁编辑 / 隔离禁写）、
/// 保存生成新修订（修订号变更 + _backup 产生）、uid 掩码不落盘（未触碰编辑框 → 原值回写）、
/// 追加源剔除 SingleTask（缓存降级目录）、动作转发宿主（停止 → 在飞取消留痕 + 终态化）、
/// 历史 20 条截断、编辑冲突保留草稿。
/// 宿主一律测试接缝构造（runnerFactory/readinessOverride），无真实 IPC、不碰真实 User 目录。
/// </summary>
public class TaskCenterPanelViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly string _flowsDir;
    private readonly string _runsDir;
    private readonly string _cacheFile;
    private readonly WorkflowStore _workflows;

    public TaskCenterPanelViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tcpanel-" + Guid.NewGuid().ToString("N")[..8]);
        _flowsDir = Path.Combine(_dir, "flows");
        _runsDir = Path.Combine(_dir, "runs");
        _cacheFile = Path.Combine(_dir, "catalog-cache.json");
        _workflows = new WorkflowStore(_flowsDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("该流程存在结果不确定（Unknown）的运行，需先对账再启动")]
    [InlineData("主体原轮次恢复/门面结清未成立；原键只读对账无唯一身份命中")]
    [InlineData("存在未决发送或收尾事实，必须先按原身份对账")]
    public void UnknownRunAction_UsesPlainChineseNextStep(string internalMessage)
    {
        var message = TaskCenterPanelViewModel.UserFacingActionMessage(internalMessage);

        Assert.Contains("核对状态", message);
        Assert.Contains("不要连续点击", message);
        Assert.DoesNotContain("Unknown", message);
        Assert.DoesNotContain("门面结清", message);
    }

    [Fact]
    public void UnknownRunCard_Explains核对AndBlocksDuplicateStart()
    {
        var workflowId = SeedFlow("未知结果计划");
        var host = MakePlainHost();
        var snapshot = host.LoadFlowSnapshot(workflowId);
        var run = new WorkflowRunRecord
        {
            RunId = "run-unknown-ui",
            WorkflowId = workflowId,
            WorkflowRevision = snapshot.Revision,
            State = WorkflowRunState.Unknown,
            Cursor = new WorkflowNodeCursor { NodeId = "n-1" },
        };

        var card = ActiveRunVm.Build(run, host);

        Assert.Equal("无法确认上次结果", card.StateText);
        Assert.Equal("核对状态", card.StopActionText);
        Assert.Equal("#E4B86A", card.StateColor);
        Assert.True(card.BlocksNewStart);
        Assert.Contains("不要连续点击", card.ActionHintText);
    }

    [Fact]
    public void DeliveryScheduling_EditorSavesVisibleModeWindowPriorityAndPreservesDraft()
    {
        var draft = new WorkflowDocument
        {
            Name = "调度编辑", Nodes = [new WorkflowNode { NodeId = "n1", Kind = "resource.oneDragonConfig" }],
            Triggers = [new WorkflowTrigger
            {
                Kind = "trigger.time",
                Params = new() { ["time"] = JsonSerializer.SerializeToElement("08:00"), ["custom"] = JsonSerializer.SerializeToElement("preserve") },
            }],
        };
        var vm = new WorkflowEditVm(draft, "revision", new ResourceCatalogService(() => null, _cacheFile));
        vm.TriggerModeIndex = 2; vm.TriggerTimeText = "09:00"; vm.TriggerUntilText = "12:00";
        vm.Nodes[0].PriorityText = "7";
        var copy = vm.BuildSubmissionCopy();
        Assert.Equal("trigger.timeFlexible", copy.Triggers[0].Kind);
        Assert.Equal("09:00", copy.Triggers[0].GetString("time"));
        Assert.Equal("12:00", copy.Triggers[0].GetString("until"));
        Assert.Equal("skip", copy.Triggers[0].GetString("missPolicy"));
        Assert.Equal("preserve", copy.Triggers[0].GetString("custom"));
        Assert.Equal(7, TaskCenterMechanismPolicy.PriorityOfNode(copy.Nodes[0]));
        Assert.Equal("trigger.time", draft.Triggers[0].Kind);
        Assert.Empty(draft.Nodes[0].Strategies);
    }

    [Fact]
    public void DeliveryManagement_PublicHostWiresSuccessorAdmission()
    {
        var host = new TaskCenterHost(_flowsDir, _runsDir, _cacheFile, () => null, () => true, () => null);
        Assert.True(host.SuccessorAdmissionWiredForTest);
    }

    [Fact]
    public void DeliveryManagement_ShareRoundtripRemovesAccountValuesWithoutChangingOriginal()
    {
        var id = SeedFlow("分享", nodeExtra: ", \"strategies\": [{\"kind\":\"prerequisite.account\",\"params\":{\"uid\":\"123456789\",\"bindingCode\":\"private-binding\"}}], \"future\":{\"keep\":42}");
        var snapshot = _workflows.LoadSnapshot(id);
        var original = File.ReadAllBytes(_workflows.List().Single(e => e.WorkflowId == id).FilePath);
        var panel = MakePanel(MakePlainHost());
        var export = Path.Combine(_dir, "share.json");
        panel.ExportFlowToFile(id, export);
        var json = File.ReadAllText(export);
        Assert.DoesNotContain("123456789", json);
        Assert.DoesNotContain("private-binding", json);
        var otherHost = new TaskCenterHost(Path.Combine(_dir, "other-flows"), Path.Combine(_dir, "other-runs"),
            Path.Combine(_dir, "other-cache.json"), () => null, (Action<string>?)null, null, null);
        var receiver = MakePanel(otherHost);
        receiver.ImportFlowFromFile(export);
        var received = otherHost.Workflows.LoadSnapshot(id).Document;
        Assert.Equal("配置A", received.Nodes[0].Ref!.Config);
        Assert.Equal(42, received.Nodes[0].ExtensionData!["future"].GetProperty("keep").GetInt32());
        Assert.Null(received.Nodes[0].Strategies[0].GetString("uid"));
        Assert.Equal(original, File.ReadAllBytes(_workflows.List().Single(e => e.WorkflowId == id).FilePath));
        Assert.False(panel.StatusIsError);
    }

    [Fact]
    public void DeliveryManagement_ImportConflictAndManagedExportCannotOverwriteDefinition()
    {
        var id = SeedFlow("原件");
        var snapshot = _workflows.LoadSnapshot(id);
        var original = File.ReadAllBytes(_workflows.List().Single(e => e.WorkflowId == id).FilePath);
        var panel = MakePanel(MakePlainHost());
        var external = Path.Combine(_dir, "import.json");
        File.WriteAllBytes(external, original);
        Assert.Throws<WorkflowRevisionConflictException>(() => panel.ImportFlowFromFile(external));
        Assert.Throws<InvalidOperationException>(() => panel.ExportFlowToFile(id, _workflows.List().Single(e => e.WorkflowId == id).FilePath));
        Assert.Equal(original, File.ReadAllBytes(_workflows.List().Single(e => e.WorkflowId == id).FilePath));
        Assert.Equal(original, File.ReadAllBytes(external));
    }

    // ================= 假边界（同 TaskCenterHostTests 模式） =================

    private sealed class FakeBoundary : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported => false;
        public List<string> Submissions { get; } = new();
        public List<string> CancelRequests { get; } = new();
        public Func<string, CancellationToken, Task<string>>? OnAwait { get; set; }

        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            Submissions.Add(request.Occurrence.NodeId);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-" + Submissions.Count));
        }

        public async Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
            => BoundaryTerminalResult.Observed(OnAwait is not null ? await OnAwait(jobId, ct) : "succeeded");

        public Task RequestCancelAsync(string jobId, CancellationToken ct)
        {
            CancelRequests.Add(jobId);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopPrerequisite : IWorkflowPrerequisiteAdapter
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct)
            => Task.FromResult(PrerequisiteResult.ProceedInstance);
    }

    private sealed class NoopTerminal : IWorkflowTerminalExecutor
    {
        public Func<WorkflowTerminalAction, WorkflowRunRecord, CancellationToken, Task<TerminalExecutionResult>>? OnExecute { get; set; }

        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run,
            CancellationToken ct)
            => OnExecute is not null ? OnExecute(action, run, ct) : Task.FromResult(TerminalExecutionResult.Executed("job-t"));
    }

    // ================= 种子与宿主 =================

    /// <summary>经 JSON 反序列化播种（保证字段形状与盘上合同一致；activation/策略由调用方拼装）。</summary>
    private string SeedFlow(string name, string activation = "active", string? nodeExtra = null)
    {
        var json = $$"""
        {
          "schema": "mistletoe.workflow",
          "schemaVersion": 1,
          "name": "{{name}}",
          "activation": { "status": "{{activation}}" },
          "nodes": [
            { "nodeId": "n-1", "kind": "resource.oneDragonConfig",
              "ref": { "config": "配置A", "revision": "rev-1" }{{nodeExtra}} }
          ]
        }
        """;
        var doc = JsonSerializer.Deserialize<WorkflowDocument>(json)!;
        _workflows.Save(doc, null);
        return doc.WorkflowId!;
    }

    /// <summary>生产行为宿主（无 runner 接缝；不启动运行，仅列表/编辑/保存路径）。</summary>
    private TaskCenterHost MakePlainHost()
        => new(_flowsDir, _runsDir, _cacheFile, () => null, (Action<string>?)null, null, null); // 第 5 参显式 Action<string>? 消歧：走测试接缝构造（runnerFactory/readinessOverride 均 null=生产行为）

    private static TaskCenterPanelViewModel MakePanel(TaskCenterHost host)
        => new(host, null, autoRefresh: false);

    private static async Task WaitUntilAsync(Func<bool> condition, int spins = 1000) // 10s 预算：全量并行负载下抗调度抖动（条件仍必须为真，不断言放松）
    {
        for (var i = 0; i < spins && !condition(); i++) await Task.Delay(10);
    }

    // ================= 夹具 =================

    [Fact]
    public void List_ThreeStates_CandidateAndQuarantined_ReadOnly()
    {
        var activeId = SeedFlow("正常流程");
        var candidateId = SeedFlow("候选流程", activation: "candidate-ready");
        Directory.CreateDirectory(_flowsDir);
        File.WriteAllText(Path.Combine(_flowsDir, "broken.flow.json"), "{ not-json");
        var panel = MakePanel(MakePlainHost());

        var active = panel.Flows.First(f => f.WorkflowId == activeId);
        Assert.True(active.CanStart);
        Assert.True(active.CanEdit);
        Assert.False(active.IsCandidate);
        Assert.False(active.IsQuarantined);

        var candidate = panel.Flows.First(f => f.WorkflowId == candidateId);
        Assert.True(candidate.IsCandidate);
        Assert.False(candidate.CanStart); // 一轮 I1：候选禁启动
        Assert.False(candidate.CanEdit);  // 一轮 I1：候选禁编辑（只读预览）
        Assert.Contains("candidate-ready", candidate.StateBadge);

        var broken = panel.Flows.First(f => f.IsQuarantined);
        Assert.False(broken.CanStart);
        Assert.False(broken.CanEdit);     // 隔离禁一切写路径
        Assert.Contains("隔离原因", broken.DetailNote);

        // 候选可走只读预览（唯一能看内容的口子）
        panel.PreviewFlowCommand.Execute(candidate);
        Assert.True(panel.IsPreviewing);
        Assert.NotNull(panel.Previewing);

        // 隔离文件预览失败 → 响亮反馈，不炸面板
        panel.PreviewFlowCommand.Execute(broken);
        Assert.Contains("预览加载失败", panel.StatusMessage);
        Assert.True(panel.StatusIsError);
    }

    [Fact]
    public void Save_GeneratesNewRevision_AndBackup()
    {
        var workflowId = SeedFlow("修订流程");
        var host = MakePlainHost();
        var panel = MakePanel(host);
        var before = host.LoadFlowSnapshot(workflowId).Revision;

        panel.EditFlowCommand.Execute(panel.Flows.First(f => f.WorkflowId == workflowId));
        Assert.NotNull(panel.Editing);
        panel.Editing!.NameText = "修订流程·改名";
        panel.SaveFlowCommand.Execute(null);

        var after = host.LoadFlowSnapshot(workflowId).Revision;
        Assert.NotEqual(before, after); // 锚点 2：修改生成新修订
        Assert.Null(panel.Editing);     // 保存成功关闭编辑器
        Assert.False(panel.StatusIsError);
        var backupDir = Path.Combine(_flowsDir, "_backup");
        Assert.True(Directory.Exists(backupDir));
        Assert.NotEmpty(Directory.GetFiles(backupDir, workflowId + ".*.flow.json")); // 原子写前备份
    }

    [Fact]
    public void Save_UidMaskNotPersisted_UntouchedEditBoxKeepsOriginal()
    {
        const string uid = "123456789012";
        var workflowId = SeedFlow("掩码流程", nodeExtra:
            """, "strategies": [ { "kind": "prerequisite.account", "uid": "123456789012", "bindingCode": "bind-xyz" } ]""");
        var host = MakePlainHost();
        var panel = MakePanel(host);

        panel.EditFlowCommand.Execute(panel.Flows.First(f => f.WorkflowId == workflowId));
        var node = Assert.Single(panel.Editing!.Nodes);

        // 掩码展示：既不是原值，也不是可逆编码
        Assert.Contains("***", node.AccountUidMasked);
        Assert.DoesNotContain("45678901", node.AccountUidMasked);
        Assert.Contains("***", node.BindingCodeMasked);

        // 不触碰编辑框（空=不修改）直接保存 → 原值回写
        panel.SaveFlowCommand.Execute(null);

        var raw = File.ReadAllText(Path.Combine(_flowsDir, workflowId + ".flow.json"));
        Assert.Contains(uid, raw);
        Assert.Contains("bind-xyz", raw);
        Assert.DoesNotContain("***", raw); // 掩码绝不落盘
    }

    [Fact]
    public async Task AppendSources_FromCacheDegradedCatalog_ExcludesSingleTask()
    {
        // 预置缓存目录：整龙 + 配置组 + 单项任务（单项禁追加，执行依据必须实时）
        var snapshot = new ResourceCatalogSnapshot
        {
            CapturedAt = DateTimeOffset.Now,
            Entries =
            [
                new TaskCenterResourceEntry { Kind = TaskCenterResourceKind.OneDragonConfig, StableId = "onedragon:整龙A", DisplayName = "整龙A", ConfigRevision = "rev-a" },
                new TaskCenterResourceEntry { Kind = TaskCenterResourceKind.ConfigGroup, StableId = "group:组B", DisplayName = "组B", ConfigRevision = "rev-b" },
                new TaskCenterResourceEntry { Kind = TaskCenterResourceKind.SingleTask, StableId = "single:组B:t-1", DisplayName = "任务C", OwnerConfig = "组B" },
            ],
        };
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_cacheFile, JsonSerializer.Serialize(snapshot));

        var workflowId = SeedFlow("追加流程");
        var host = MakePlainHost();
        await host.Catalog.RefreshAsync(); // 无传输 → 缓存降级（留痕），Current=缓存快照
        var panel = MakePanel(host);

        panel.EditFlowCommand.Execute(panel.Flows.First(f => f.WorkflowId == workflowId));
        var sources = panel.Editing!.AppendSources;
        Assert.Equal(2, sources.Count);
        Assert.DoesNotContain(sources, s => s.Kind == TaskCenterResourceKind.SingleTask);
        Assert.Contains(sources, s => s.Kind == TaskCenterResourceKind.OneDragonConfig);
        Assert.Contains(sources, s => s.Kind == TaskCenterResourceKind.ConfigGroup);
        Assert.NotNull(panel.Editing.AppendStatusText); // 降级留痕可见
    }

    [Fact]
    public async Task RunAction_Stop_ForwardsToHost_CancelsInFlight_Terminalizes()
    {
        var workflowId = SeedFlow("可停流程");
        var awaitingSubmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundary = new FakeBoundary
        {
            OnAwait = async (_, ct) =>
            {
                awaitingSubmission.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return "succeeded";
            },
        };
        var host = new TaskCenterHost(_flowsDir, _runsDir, _cacheFile, () => null, null,
            (_, w, r) => new WorkflowRunner(w, r, boundary, new NoopPrerequisite(), new NoopTerminal(),
                new WorkflowRunnerOptions { SkipConfirmTimeout = TimeSpan.FromMilliseconds(100) }),
            () => (true, null));
        var panel = MakePanel(host);
        try
        {
            var start = await host.StartWorkflowAsync(workflowId);
            Assert.NotEqual(HostActionStatus.Unavailable, start.Status);

            WorkflowRunRecord? run = null;
            await WaitUntilAsync(() =>
            {
                run = host.ListActiveRuns().FirstOrDefault();
                return run?.State == WorkflowRunState.Running;
            });
            Assert.NotNull(run);
            await awaitingSubmission.Task.WaitAsync(TimeSpan.FromSeconds(15)); // 真实覆盖已受理、在飞观察期间的 Stop。

            panel.Refresh();
            var vm = Assert.Single(panel.ActiveRuns);
            Assert.True(vm.CanStop);

            panel.StopRunCommand.Execute(vm);
            Assert.False(string.IsNullOrEmpty(panel.StatusMessage)); // 结构化反馈入 StatusMessage

            await WaitUntilAsync(() => boundary.CancelRequests.Count > 0);
            Assert.Contains("job-1", boundary.CancelRequests); // 在飞作业 best-effort 取消留痕

            // Unknown 仍是需关注的活动责任；完成的是驱动退出，不是把未决作业从活动列表隐藏。
            await WaitUntilAsync(() => !host.IsDriving(workflowId));
            Assert.False(host.IsDriving(workflowId));
            var rec = new RunStore(_runsDir).Load(run!.RunId);
            Assert.NotNull(rec);
            Assert.Equal(WorkflowRunState.Unknown, rec!.State); // 无活动驱动仍保留远端责任
            Assert.True(rec.StopRequested);
            Assert.Equal("job-1", rec.CurrentSubmission!.JobId);
            Assert.False(rec.CurrentSubmission.ExecutionExitConfirmed);
            Assert.Equal(run.RunId, Assert.Single(host.ListActiveRuns()).RunId);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [Fact]
    public async Task RunAction_Stop_Completing_CanStop_PreservesCompletionResponsibility()
    {
        // [Completing 面板停止] 收尾执行期（Completing + PendingCompletion 已落盘）必须可从面板停止：
        // CanStop=true，停止走驱动路径（登记 StopRequested + 取消令牌），收尾事实保留（禁止补发）。
        var workflowId = SeedFlow("收尾可停流程");
        var doc = new WorkflowStore(_flowsDir).LoadSnapshot(workflowId).Document;
        doc.Terminal = [new WorkflowTerminalAction
        {
            Kind = "terminal.completionAction",
            Params = new Dictionary<string, System.Text.Json.JsonElement>
            { ["action"] = System.Text.Json.JsonSerializer.SerializeToElement("关闭游戏") },
        }];
        var store = new WorkflowStore(_flowsDir); var expectedRevision = store.LoadSnapshot(workflowId).Revision; store.Save(doc, expectedRevision);

        var terminalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new NoopTerminal
        {
            OnExecute = async (_, run, ct) =>
            {
                terminalStarted.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { return TerminalExecutionResult.UnknownWith("job-t", "收尾执行被取消，效果未确认"); }
                return TerminalExecutionResult.Executed("job-t");
            },
        };
        var boundary = new FakeBoundary();
        var host = new TaskCenterHost(_flowsDir, _runsDir, _cacheFile, () => null, null,
            (_, w, r) => new WorkflowRunner(w, r, boundary, new NoopPrerequisite(), terminal),
            () => (true, null));
        var panel = MakePanel(host);
        try
        {
            var start = await host.StartWorkflowAsync(workflowId);
            Assert.NotEqual(HostActionStatus.Unavailable, start.Status);

            await terminalStarted.Task.WaitAsync(TimeSpan.FromSeconds(15)); // 收尾执行期（Completing）
            panel.Refresh();
            var vm = Assert.Single(panel.ActiveRuns);
            Assert.Equal(WorkflowRunState.Completing, new RunStore(_runsDir).Load(vm.RunId)!.State);
            Assert.True(vm.CanStop); // Completing 可从面板停止

            panel.StopRunCommand.Execute(vm);
            Assert.False(string.IsNullOrEmpty(panel.StatusMessage));

            await WaitUntilAsync(() => host.ListActiveRuns().Count == 0);
            var rec = new RunStore(_runsDir).Load(vm.RunId);
            Assert.NotNull(rec);
            Assert.True(rec!.StopRequested);
            Assert.NotNull(rec.PendingCompletion); // 收尾事实保留（效果未确认，禁止补发）
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [Fact]
    public void RunAction_Stop_Unknown_OfflineKeepsUnknownResponsibility()
    {
        // [Unknown 面板停止·离线分支] Unknown 运行可从面板停止（CanStop=true），但 BGI 离线时只读对账不可考：
        // 返回结构化 Unavailable，保持 Unknown 且 StopRequested 已耐久登记（供后续重试对账，不重发、不新建 run）。
        var workflowId = SeedFlow("Unknown 离线停止流程");
        var runs = new RunStore(_runsDir);
        var run = runs.CreateRun(workflowId, "rev-1");
        run.State = WorkflowRunState.Unknown;
        run.CurrentSubmission = new WorkflowSubmission
        {
            Key = "idem-offline", NodeId = "n-1", Intent = SubmitIntentState.Submitted,
        };
        runs.Update(run);

        var host = new TaskCenterHost(_flowsDir, _runsDir, _cacheFile, () => null, null, null, () => (true, null));
        var panel = MakePanel(host);
        panel.Refresh();
        var vm = Assert.Single(panel.ActiveRuns);
        Assert.True(vm.CanStop); // Unknown 可从面板停止

        panel.StopRunCommand.Execute(vm);
        Assert.False(string.IsNullOrEmpty(panel.StatusMessage)); // 结构化反馈（离线对账不可考）

        var rec = new RunStore(_runsDir).Load(run.RunId);
        Assert.NotNull(rec);
        Assert.Equal(WorkflowRunState.Unknown, rec!.State); // 离线保持 Unknown（不降级、不假成功）
        Assert.True(rec.StopRequested); // 停止意图已耐久登记
    }

    [Fact]
    public void HistoryRuns_TruncatesTo20()
    {
        var workflowId = SeedFlow("历史流程");
        var runs = new RunStore(_runsDir);
        for (var i = 0; i < 25; i++)
        {
            var rec = runs.CreateRun(workflowId, "rev-1");
            rec.State = WorkflowRunState.Succeeded; // 终态进历史
            runs.Update(rec);
        }
        var panel = MakePanel(MakePlainHost());

        Assert.Equal(20, panel.HistoryRuns.Count); // 最近 20 条截断
        Assert.Empty(panel.ActiveRuns);
        Assert.False(panel.HasActiveRuns);
    }

    [Fact]
    public void Save_RevisionConflict_KeepsDraft()
    {
        var workflowId = SeedFlow("冲突流程");
        var host = MakePlainHost();
        var panel = MakePanel(host);

        panel.EditFlowCommand.Execute(panel.Flows.First(f => f.WorkflowId == workflowId));
        Assert.NotNull(panel.Editing);
        panel.Editing!.NameText = "我的未保存修改";

        // 外部合作写方先保存一轮（携带当前修订，合法覆盖）→ 盘上修订前进
        var snapshot = host.LoadFlowSnapshot(workflowId);
        snapshot.Document.Name = "外部修改";
        _workflows.Save(snapshot.Document, snapshot.Revision);

        panel.SaveFlowCommand.Execute(null);

        Assert.NotNull(panel.Editing);                       // 一轮 I4：冲突保留草稿
        Assert.Equal("我的未保存修改", panel.Editing!.NameText); // 草稿内容未丢
        Assert.True(panel.StatusIsError);
        Assert.Contains("草稿保留", panel.StatusMessage);

        // 盘上仍是外部版本，未被草稿覆盖
        Assert.Equal("外部修改", host.LoadFlowSnapshot(workflowId).Document.Name);
    }

    // ================= 二轮夹具（ASTRA 阶段终审 阻断/重要 处置回归证明） =================

    [Fact]
    public async Task Candidate_WithUnknownKind_StillReadOnly_AndHostGuards()
    {
        // 阻断2：候选身份只依据 activation——候选+未知类型不得显示为可编辑 active
        var json = """
        {
          "schema": "mistletoe.workflow", "schemaVersion": 1, "name": "候选未知类型",
          "activation": { "status": "candidate-ready" },
          "nodes": [ { "nodeId": "n-1", "kind": "resource.futureKind", "ref": { "config": "配置X" } } ]
        }
        """;
        var doc = JsonSerializer.Deserialize<WorkflowDocument>(json)!;
        _workflows.Save(doc, null);
        var host = MakePlainHost();
        var panel = MakePanel(host);

        var item = panel.Flows.First(f => f.WorkflowId == doc.WorkflowId);
        Assert.True(item.IsCandidate);
        Assert.False(item.CanStart);
        Assert.False(item.CanEdit);
        Assert.Contains("未支持类型", item.DetailNote);

        // 宿主层独立禁启动（不依赖 UI 列表新鲜度）
        var start = await host.StartWorkflowAsync(doc.WorkflowId!);
        Assert.Equal(HostActionStatus.Unavailable, start.Status);
        Assert.Contains("candidate-ready", start.Message);

        // 宿主层独立禁写（candidate 不得经任务中心保存覆盖）
        doc.Name = "试图改名";
        Assert.Throws<InvalidOperationException>(() => host.SaveFlow(doc, host.LoadFlowSnapshot(doc.WorkflowId!).Revision));
    }

    [Fact]
    public void Save_NoModification_PreservesCustomLoopTerminalMissPolicyAndDays()
    {
        // 阻断1：未触碰字段原样保留——自定义 loop mode / 未知收尾 action / 既有触发器不补 missPolicy / days 顺序与未映射值
        var json = """
        {
          "schema": "mistletoe.workflow", "schemaVersion": 1, "name": "复杂保留流程",
          "activation": { "status": "active" },
          "triggers": [ { "kind": "trigger.time", "time": "06:10" } ],
          "loop": { "mode": "cronCustom", "expr": "0 */6 * * *" },
          "terminal": [ { "kind": "terminal.completionAction", "action": "hibernateFuture" } ],
          "nodes": [ { "nodeId": "n-1", "kind": "resource.oneDragonConfig",
                       "ref": { "config": "配置A", "revision": "rev-1" },
                       "strategies": [ { "kind": "condition.weekdays", "days": ["周五", "周一", "周八"], "dayBoundary": "localMidnight" } ] } ]
        }
        """;
        var doc = JsonSerializer.Deserialize<WorkflowDocument>(json)!;
        _workflows.Save(doc, null);
        var host = MakePlainHost();
        var panel = MakePanel(host);

        panel.EditFlowCommand.Execute(panel.Flows.First(f => f.WorkflowId == doc.WorkflowId));
        Assert.NotNull(panel.Editing);
        // 自定义值映射到「保留自定义」索引，而不是「无」
        Assert.Equal(3, panel.Editing!.LoopModeIndex);
        Assert.Equal(5, panel.Editing.TerminalActionIndex);
        panel.SaveFlowCommand.Execute(null); // 什么都不改直接保存
        Assert.False(panel.StatusIsError);

        var saved = host.LoadFlowSnapshot(doc.WorkflowId!).Document;
        Assert.Equal("cronCustom", saved.Loop!.Mode);                       // 自定义 loop 原样保留
        Assert.Equal("0 */6 * * *", saved.Loop.GetString("expr"));          // loop 其余参数保留
        Assert.Equal("hibernateFuture", saved.Terminal[0].GetString("action")); // 未知收尾 action 保留
        var trigger = Assert.Single(saved.Triggers);
        Assert.Equal("06:10", trigger.GetString("time"));
        Assert.Null(trigger.GetString("missPolicy"));                        // 既有触发器不补默认
        var days = saved.Nodes[0].Strategies[0].GetStringArray("days");
        Assert.Equal(["周五", "周一", "周八"], days);                        // 顺序与未映射值原样保留
    }

    [Fact]
    public void Save_UidFailureRetry_EditCleared_RestoresOriginalBaseline()
    {
        // 阻断1/重要4：失败重试不被前次 Apply 污染——清空编辑框后保存，落盘的必须是构造基线原值
        const string uid = "123456789012";
        var workflowId = SeedFlow("重试流程", nodeExtra:
            """, "strategies": [ { "kind": "prerequisite.account", "uid": "123456789012" } ]""");
        var host = MakePlainHost();
        var panel = MakePanel(host);
        panel.EditFlowCommand.Execute(panel.Flows.First(f => f.WorkflowId == workflowId));
        var node = Assert.Single(panel.Editing!.Nodes);

        node.AccountUidEdit = "999888777666";
        panel.Editing!.NameText = "   "; // 触发保存失败（名称空）
        panel.SaveFlowCommand.Execute(null);
        Assert.True(panel.StatusIsError);
        Assert.NotNull(panel.Editing); // 草稿保留

        node.AccountUidEdit = "";        // 用户清空编辑框（=不修改 uid）
        panel.Editing!.NameText = "重试流程";
        panel.SaveFlowCommand.Execute(null);
        Assert.True(!panel.StatusIsError, panel.StatusMessage); // 诊断：失败时带出状态文案

        var raw = File.ReadAllText(Path.Combine(_flowsDir, workflowId + ".flow.json"));
        Assert.Contains(uid, raw);              // 原值回写
        Assert.DoesNotContain("999888777666", raw); // 前次失败尝试不留痕
    }

    [Fact]
    public void Edit_StaleList_CandidateRecheck_RefusesEditAndOpensPreview()
    {
        // 阻断2：列表条目仍是 active，但盘上已变 candidate-ready → BeginEdit 复核快照拒绝编辑转预览
        var workflowId = SeedFlow("过期列表流程");
        var host = MakePlainHost();
        var panel = MakePanel(host);
        panel.DiscardEditCommand.Execute(null); // 正式工作区会默认打开计划；此场景专门验证过期列表入口。
        var staleItem = panel.Flows.First(f => f.WorkflowId == workflowId);
        Assert.True(staleItem.CanEdit); // 列表条目过期（仍显示 active）

        var snapshot = host.LoadFlowSnapshot(workflowId);
        snapshot.Document.Activation = new WorkflowActivation { Status = "candidate-ready" };
        _workflows.Save(snapshot.Document, snapshot.Revision); // 外部合法推进修订

        panel.EditFlowCommand.Execute(staleItem);
        Assert.Null(panel.Editing);          // 拒绝编辑
        Assert.True(panel.IsPreviewing);     // 转为只读预览
        Assert.Contains("candidate-ready", panel.StatusMessage);
    }

    [Fact]
    public async Task Start_Concurrent_BarrierShared_OnlyOneRun()
    {
        // 阻断5：并发 Start 共同等待同一恢复屏障，互斥临界区保证只建一个运行
        var workflowId = SeedFlow("并发流程");
        var boundary = new FakeBoundary
        {
            OnAwait = async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return "succeeded"; },
        };
        var host = new TaskCenterHost(_flowsDir, _runsDir, _cacheFile, () => null, null,
            (_, w, r) => new WorkflowRunner(w, r, boundary, new NoopPrerequisite(), new NoopTerminal()),
            () => (true, null));
        try
        {
            var results = await Task.WhenAll(
                host.StartWorkflowAsync(workflowId),
                host.StartWorkflowAsync(workflowId),
                host.StartWorkflowAsync(workflowId));
            Assert.Equal(1, results.Count(r => r.Status != HostActionStatus.Unavailable));
            Assert.Equal(2, results.Count(r => r.Status == HostActionStatus.Unavailable));
            await WaitUntilAsync(() => host.ListActiveRuns().Count > 0);
            Assert.Single(host.ListActiveRuns());
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Resume_UnknownPlusInterrupted_SameFlow_Rejected()
    {
        // 阻断5：同流程存在 Unknown（结果不可考）+ 另一条 Interrupted → 恢复被拒（需先对账）
        var workflowId = SeedFlow("恢复护栏流程");
        var runs = new RunStore(_runsDir);
        var unknown = runs.CreateRun(workflowId, "rev-1");
        unknown.State = WorkflowRunState.Unknown;
        runs.Update(unknown);
        var interrupted = runs.CreateRun(workflowId, "rev-1");
        interrupted.State = WorkflowRunState.Interrupted;
        runs.Update(interrupted);

        var host = new TaskCenterHost(_flowsDir, _runsDir, _cacheFile, () => null, null, null,
            () => (true, null)); // 就绪判定接缝：让检查推进到同流程护栏
        var result = await host.ResumeRunAsync(interrupted.RunId);
        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Contains("Unknown", result.Message);
        // 被拒后 Interrupted 记录原样（未被恢复动作触碰）
        Assert.Equal(WorkflowRunState.Interrupted, new RunStore(_runsDir).Load(interrupted.RunId)!.State);
    }

    [Fact]
    public void HistoryRuns_ReordersOnRefresh_NewestFirst()
    {
        // 重要6：新进入历史的记录按 UpdatedAt 倒序重排（不是追加尾部）
        var workflowId = SeedFlow("排序流程");
        var runs = new RunStore(_runsDir);
        var r1 = runs.CreateRun(workflowId, "rev-1");
        r1.State = WorkflowRunState.Succeeded;
        runs.Update(r1);
        var panel = MakePanel(MakePlainHost());
        Assert.Single(panel.HistoryRuns);

        Thread.Sleep(20); // 保证 UpdatedAt 可区分
        var r2 = runs.CreateRun(workflowId, "rev-1");
        r2.State = WorkflowRunState.Succeeded;
        runs.Update(r2);
        panel.Refresh();

        Assert.Equal(2, panel.HistoryRuns.Count);
        Assert.Equal(r2.RunId, panel.HistoryRuns[0].Key); // 最新在最前
        Assert.Equal(r1.RunId, panel.HistoryRuns[1].Key);
    }
}
