using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

[CollectionDefinition("TaskCenterPipeTransport", DisableParallelization = true)]
public sealed class TaskCenterPipeTransportCollection
{
}

/// <summary>
/// **R5.3 §24.4 生产组合根闭环验收集（第一批：外部启动 ext 队列通道闭环）**：
/// **真实组装**＝真实 `CommandExecutor`（E3 接线）→ 真实 `TaskCenterHost`（真实仲裁门面＋真实接管台账＋真实
/// `BgiExternalClient`）→ 真实 Core；**仅替换外部传输**＝私有管道名上的 BGI 实例管道替身
/// （<see cref="BgiInstancePipeDouble"/>，帧格式与生产一致）。
/// 覆盖：①**连续两次成功启动**（第二次不被第一次未终结台账阻断，§24.4-1）；②ext `Completed` 后**台账 Terminal
/// ＋外部 Operation `TerminalCompleted`＋主槽位释放＋JobId 可读**（§24.4-2）。
/// 其余 §24.4 场景（取消/落盘失败交错、v2 行为、容量 &gt;32、冷启动/旧 v2/切模式）在后续批次补齐（登记 §24.25）。
/// </summary>
[Collection("TaskCenterPipeTransport")]
public sealed class R5CompositionRootAcceptanceTests : IAsyncLifetime
{
    private readonly BgiInstancePipeDouble _double = new();
    private string? _previousExternalPipe;
    private string? _previousIpcPipe;

    public Task InitializeAsync()
    {
        // 仅替换外部传输：真实客户端指向私有管道（生产恒 null）。
        _previousExternalPipe = BgiExternalClient.PipeNameOverrideForTest;
        _previousIpcPipe = IpcClient.PipeNameOverrideForTest;
        BgiExternalClient.PipeNameOverrideForTest = _double.PipeName;
        IpcClient.PipeNameOverrideForTest = _double.PipeName;
        _double.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // 成对还原**初始化前的原值**（不是无条件置 null）——避免覆盖同一进程内其它接缝使用者。
        BgiExternalClient.PipeNameOverrideForTest = _previousExternalPipe;
        IpcClient.PipeNameOverrideForTest = _previousIpcPipe;
        await _double.DisposeAsync();
    }

    private static RemoteCommand StartGroupCommand(string groupName)
        => new() { Cmd = "start_group", Params = new() { ["groupName"] = groupName } };

    [Fact]
    public async Task PipeIdentity_ForgedPingCannotAuthorizeTaskStatus()
    {
        _double.ReportedStartTicksOverride = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks + 1;
        using var client = new IpcClient();
        await client.ConnectAsync(2000);
        Assert.Equal(IpcSessionCheck.Unknown, client.SessionCheck);
        Assert.False(client.IsSessionTrusted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PipeIdentity_InvalidHelloCannotBecomeReady(bool omitData)
    {
        _double.OmitHelloData = omitData;
        if (!omitData)
            _double.ReportedProcessIdOverride = System.Diagnostics.Process.GetCurrentProcess().Id + 1;
        using var client = new BgiExternalClient();
        await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(BgiExternalLinkState.Ready, client.State);
        Assert.Null(client.ServerEpoch);
    }

    [Fact]
    public async Task CompositionRoot_ExtQueue_TwoConsecutiveStarts_SettleAndReleaseSlot()
    {
        var root = Path.Combine(Path.GetTempPath(), "r5comp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            using var client = new BgiExternalClient();
            var host = new TaskCenterHost(
                Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
                () => client, log: null, runnerFactory: null, readinessOverride: () => (true, null),
                localExecutionCapability: () => true,
                statusSnapshotProvider: () => new ControlStatus { TaskStatusAvailable = true, TaskStatusBgiEpoch = "9:900", TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = false },
                admissionWired: true,
                admissionSeams: new TaskCenterAdmissionSeams { Epoch = "1:1" });
            // 真实组装：E3 的准入由宿主仲裁面承担（= 生产组合根的接线形状）；传输=进程内管道替身。
            var executor = new CommandExecutor(null!, "unused",
                externalClientProvider: () => client,
                externalStartAdmission: (request, ct) => host.AdmitExternalStartAsync(request, ct));

            await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(BgiExternalLinkState.Ready, client.State);   // 前置：ext 通道经替身就绪
            // 镜像生产启动步骤（`MainViewModel.BgiExternal` 在 Ready 且未激活时订阅全部事件）：终态事件快速路径
            // 依赖该订阅；替身会拒绝推送未订阅事件（同构纪律）。
            await client.SubscribeAsync([]);

            // ── 第一次启动：受理 → 推送 ext Completed → 结算（台账 Terminal／Operation 终局／槽位释放）──
            var first = executor.ExecuteAsync(StartGroupCommand("组合根组A"));
            await WaitForAsync(() => _double.CountOf("ext.task.start") == 1, TimeSpan.FromSeconds(10));
            // 同构纪律（会诊重要项）：终态事件的**订阅**必须先于推送（否则推送会被替身拒绝）——证明生产订阅链可达。
            Assert.Equal(1, _double.CountOf("ext.event.subscribe"));
            var handle = HandleOf(_double, 1);
            await _double.PushEventAsync("task.completed", handle);
            CommandResult firstResult;
            try
            {
                firstResult = await first.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException)
            {
                Assert.Fail("第一次启动未在预算内返回；替身收到的操作＝["
                            + string.Join(",", _double.Received.Select(r => r.Operation)) + "]");
                throw;
            }

            Assert.Equal("success", firstResult.Status);                        // 受理≠失败；终局后仍为 success
            Assert.Equal(1, _double.CountOf("ext.task.start"));                 // 发送恰好一次（无重发）
            var arbitrationDir = Path.Combine(root, "arbitration");
            var ledgerEntry = new ExternalStartLedger(root).Read().File!.Entries.Single();
            Assert.Equal(LedgerEntryState.Terminal, ledgerEntry.State);         // 台账 Terminal（§24.4-2）
            Assert.Equal(handle, ledgerEntry.JobId);                            // JobId 可读
            var op = new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Operations
                .Single(o => o.LastSendSeq > 0);
            Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState);   // 外部 Operation 终局
            // §24.4-2「主槽位释放」：终局操作必须**已迁出计容区**（Active → TerminalPendingTransfer／Tombstone），
            // 不能只以「Submission 关闭」代替计容释放（§24.1-3）。
            Assert.NotEqual(OperationZone.Active, op.Zone);
            Assert.DoesNotContain(new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Operations,
                o => o.Zone == OperationZone.Active);
            Assert.Null(new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Submission); // 主槽位释放
            Assert.Equal(ExecutionResultKind.Succeeded, op.ExecutionResult!.Kind);
            Assert.Equal("completed", op.ExecutionResult.RawTerminal);

            // ── 第二次启动：**不被第一次未终局台账阻断**（§24.4-1：第一次已终局，此处验证可再次受理）──
            var second = executor.ExecuteAsync(StartGroupCommand("组合根组B"));
            await WaitForAsync(() => _double.CountOf("ext.task.start") == 2, TimeSpan.FromSeconds(10));
            await _double.PushEventAsync("task.completed", HandleOf(_double, 2));
            var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("success", secondResult.Status);
            Assert.Equal(2, _double.CountOf("ext.task.start"));
            Assert.Equal(2, new ExternalStartLedger(root).Read().File!.Entries.Count);
            Assert.All(new ExternalStartLedger(root).Read().File!.Entries,
                e => Assert.Equal(LedgerEntryState.Terminal, e.State));
            Assert.Null(new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Submission);
            await host.ShutdownAsync();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string HandleOf(BgiInstancePipeDouble pipeDouble, int startOrdinal)
    {
        // 替身按入队次序命名句柄（`fixture-handle-N`）：从收到的请求次序推导，避免依赖内部状态。
        Assert.True(pipeDouble.CountOf("ext.task.start") >= startOrdinal, "ext.task.start 尚未发生");
        return "fixture-handle-" + startOrdinal;
    }

    /// <summary>
    /// **§24.4-3 权威取消终态**：ext `task.queueCancelled` ⇒ 入口 `cancelled`（线路词表 `failed`+`cancelled` 错误码，
    /// 结果维 `Cancelled`）且**责任结清**（台账 Terminal＋Operation 终局＋槽位释放、不重建 Submission）。
    /// </summary>
    [Fact]
    public async Task CompositionRoot_ExtQueueCancelled_ReturnsCancelledAndSettles()
    {
        var root = Path.Combine(Path.GetTempPath(), "r5comp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            using var client = new BgiExternalClient();
            var host = NewHost(root, client);
            var executor = new CommandExecutor(null!, "unused",
                externalClientProvider: () => client,
                externalStartAdmission: (request, ct) => host.AdmitExternalStartAsync(request, ct));
            await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await client.SubscribeAsync([]);   // 同首个夹具：镜像生产订阅步骤（终态事件快速路径的前提）

            var start = executor.ExecuteAsync(StartGroupCommand("组合根组C"));
            await WaitForAsync(() => _double.CountOf("ext.task.start") == 1, TimeSpan.FromSeconds(10));
            await _double.PushEventAsync("task.queueCancelled", HandleOf(_double, 1));
            var result = await start.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("failed", result.Status);                              // 现有线路词表（不改词）
            Assert.Equal("cancelled", result.ErrorCode);
            Assert.Equal(ExecutionDisposition.Cancelled, result.ExecutionDisposition);
            Assert.Equal(ResponsibilityState.Settled, result.ResponsibilityState);
            Assert.Equal(1, _double.CountOf("ext.task.start"));                 // 不重发
            var arbitrationDir = Path.Combine(root, "arbitration");
            Assert.Equal(LedgerEntryState.Terminal, new ExternalStartLedger(root).Read().File!.Entries.Single().State);
            var op = new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Operations.Single(o => o.LastSendSeq > 0);
            Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState);
            Assert.Equal(ExecutionResultKind.Cancelled, op.ExecutionResult!.Kind);
            Assert.Null(new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Submission);   // 不重建
            await host.ShutdownAsync();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// owner 新合同：队列通道不可用时明确拒绝，不能回退到无任务编号的 v2 `task.start`。
    /// </summary>
    [Fact]
    public async Task CompositionRoot_QueueUnavailable_RejectsWithoutV2Fallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "r5comp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            _double.AcceptHello = false;   // 队列通道不可用，即使 v2 仍可达也不得回退
            using var client = new BgiExternalClient();
            var host = NewHost(root, client);
            var executor = new CommandExecutor(null!, "unused",
                externalClientProvider: () => client,
                externalStartAdmission: (request, ct) => host.AdmitExternalStartAsync(request, ct));
            await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

            var result = await executor.ExecuteAsync(StartGroupCommand("组合根组D")).WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("failed", result.Status);
            Assert.Equal("task_queue_unavailable", result.ErrorCode);
            Assert.Equal(0, _double.CountOf("task.start"));
            Assert.Equal(0, _double.CountOf("ext.task.start"));
            Assert.Empty(new ExternalStartLedger(root).Read().File?.Entries ?? []);
            var arbitrationDir = Path.Combine(root, "arbitration");
            var op = new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Operations.Single(o => o.LastSendSeq > 0);
            Assert.Equal(OperationRequestState.TerminalRejected, op.RequestState);
            Assert.Null(op.ExecutionResult);
            Assert.Null(op.PendingTerminal);
            await host.ShutdownAsync();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static TaskCenterHost NewHost(string root, BgiExternalClient client)
        => new(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
            () => client, log: null, runnerFactory: null, readinessOverride: () => (true, null),
            localExecutionCapability: () => true,
            statusSnapshotProvider: () => new ControlStatus { TaskStatusAvailable = true, TaskStatusBgiEpoch = "9:900", TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = false },
            admissionWired: true,
            admissionSeams: new TaskCenterAdmissionSeams { Epoch = "1:1" });

    /// <summary>
    /// **§24.4-6 大于 32 笔外部启动完成后的容量与终局释放**：33 轮闭环（受理→推送 Completed→结算）后
    /// 全部成功、无重发，且**不留任何 `Active` 计容操作**（主槽位随终局释放）。
    /// </summary>
    [Fact]
    public async Task CompositionRoot_MoreThan32CompletedStarts_NoCapacityExhaustion()
    {
        var root = Path.Combine(Path.GetTempPath(), "r5comp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            using var client = new BgiExternalClient();
            var host = NewHost(root, client);
            var executor = new CommandExecutor(null!, "unused",
                externalClientProvider: () => client,
                externalStartAdmission: (request, ct) => host.AdmitExternalStartAsync(request, ct));
            await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await client.SubscribeAsync([]);

            for (var i = 1; i <= 33; i++)
            {
                var start = executor.ExecuteAsync(StartGroupCommand("容量组" + i));
                await WaitForAsync(() => _double.CountOf("ext.task.start") == i, TimeSpan.FromSeconds(10));
                await _double.PushEventAsync("task.completed", HandleOf(_double, i));
                var result = await start.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal("success", result.Status);
            }

            Assert.Equal(33, _double.CountOf("ext.task.start"));           // 无重发（发送恰 33 次）
            var arbitrationDir = Path.Combine(root, "arbitration");
            var ops = new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Operations;
            // 计容公式＝`Active + TerminalPendingTransfer ≤ 32`：两类都占主槽位，故必须**双双为 0**才算释放
            // （仅断言「无 Active」不足以证明容量的终局释放，§24.1-3／§24.1-8）。
            Assert.DoesNotContain(ops, o => o.Zone is OperationZone.Active or OperationZone.TerminalPendingTransfer);
            Assert.Equal(33, new ExternalStartLedger(root).Read().File!.Entries
                .Count(e => e.State == LedgerEntryState.Terminal));
            await host.ShutdownAsync();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// **§24.8-3 默认未注入路径（生产门仍关闭）**：`CommandExecutor` **不注入**外部启动准入委托（＝当前生产默认）
    /// ⇒ 启动走既有直启路径（ext 队列通道旧词表），**完全不触碰仲裁面与外部启动台账**（门关闭的可观测证据）。
    /// 同时断言控制热键在未接线时同样不经门面（IPC 直发）。
    /// </summary>
    [Fact]
    public async Task CompositionRoot_DefaultUnwiredPath_DoorStaysClosedAndLegacyQueuePathUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "r5comp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            using var client = new BgiExternalClient();
            var host = NewHost(root, client);
            var executor = new CommandExecutor(null!, "unused", externalClientProvider: () => client);   // 不注入＝生产默认
            await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await client.SubscribeAsync([]);

            var start = executor.ExecuteAsync(StartGroupCommand("未接线组A"));
            await WaitForAsync(() => _double.CountOf("ext.task.start") == 1, TimeSpan.FromSeconds(10));
            await _double.PushEventAsync("task.completed", HandleOf(_double, 1));
            var result = await start.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("success", result.Status);
            Assert.Contains("队列通道", result.Message);                       // 旧词表（未接线直启路径逐字保留）
            Assert.Equal(1, _double.CountOf("ext.task.start"));
            // 门关闭的可观测证据：仲裁面无任何操作、外部启动台账不存在
            var arbitrationDir = Path.Combine(root, "arbitration");
            Assert.True(!Directory.Exists(arbitrationDir)
                        || new ArbitrationLeaseStore(arbitrationDir).Read().File?.Handoff?.Operations is null
                        || new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Operations.Count == 0,
                "默认未注入路径不得产生仲裁操作（生产门应保持关闭）");
            Assert.True(!File.Exists(Path.Combine(root, "external-start-ledger.json")),
                "默认未注入路径不得写外部启动台账");

            // 说明：`cold_start_required` 只由**接线态核心**（`allowPreemption=false`）产生；未接线路径的冷启动
            // 走既有「裸拉起回退」语义（不属本批范围，另有组件级夹具覆盖）。故接线态出口的对外合同＝
            // 队列通道未就绪按新合同明确拒绝且零发送（见 `CompositionRoot_ColdStart_WiredPathRefusesWithoutSending`）。
            await host.ShutdownAsync();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// **§24.4-5 终态落盘失败 → 保守停驻 → 恢复补终局**（组合根层面）：
    /// 受理后把接管台账置为**只读**（模拟台账写入失败）⇒ 完成结算的「台账 Terminal」步失败：
    /// ①本笔落保守停驻（责任保留、**不重发**、不释放占用）；②`PendingTerminal` 已持久化；
    /// ③恢复写权限后，重启路径（`RecoverExternalStartObservationsAsync`）按已持久化事实**补终局**。
    /// </summary>
    [Fact]
    public async Task CompositionRoot_LedgerTerminalWriteFails_StaysPendingThenRecoveryCompletes()
    {
        var root = Path.Combine(Path.GetTempPath(), "r5comp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            using var client = new BgiExternalClient();
            var host = NewHost(root, client);
            var executor = new CommandExecutor(null!, "unused",
                externalClientProvider: () => client,
                externalStartAdmission: (request, ct) => host.AdmitExternalStartAsync(request, ct));
            await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await client.SubscribeAsync([]);

            var start = executor.ExecuteAsync(StartGroupCommand("落盘失败组"));
            await WaitForAsync(() => _double.CountOf("ext.task.start") == 1, TimeSpan.FromSeconds(10));
            var ledgerPath = Path.Combine(root, "external-start-ledger.json");
            Assert.True(File.Exists(ledgerPath), "受理接管台账应已落盘");
            File.SetAttributes(ledgerPath, FileAttributes.ReadOnly);      // 注入「台账写入失败」
            await _double.PushEventAsync("task.completed", HandleOf(_double, 1));
            var blocked = await start.WaitAsync(TimeSpan.FromSeconds(15));

            // ① 保守停驻：不得报成功、不得重发、责任未结清（台账未 Terminal ⇒ 结果不可考，禁止重发）
            Assert.NotEqual("success", blocked.Status);
            Assert.Equal(ResponsibilityState.Pending, blocked.ResponsibilityState);
            Assert.Equal(1, _double.CountOf("ext.task.start"));
            var arbitrationDir = Path.Combine(root, "arbitration");
            var op = new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Operations.Single(o => o.LastSendSeq > 0);
            var targetSubmission = op.SubmissionIdentity;
            Assert.Equal(OperationRequestState.Accepted, op.RequestState);        // 未终局（责任保留）
            Assert.NotNull(op.PendingTerminal);                                    // ② 待终局事实已持久化
            Assert.Equal(LedgerEntryState.AcceptedPendingExecution,
                new ExternalStartLedger(root).Read().File!.Entries.Single().State); // 台账未终局（写入失败）

            // ③ 恢复写权限后重启：本地已有完整权威 PendingTerminal，恢复流程续跑台账 Terminal → 关闭 → 终局。
            File.SetAttributes(ledgerPath, FileAttributes.Normal);
            await host.ShutdownAsync();
            var host2 = NewHost(root, client);
            await ProbeRecoveryAsync(host2);
            var repaired = new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Operations
                .Single(o => string.Equals(o.SubmissionIdentity, targetSubmission, StringComparison.Ordinal));
            Assert.Equal(OperationRequestState.TerminalCompleted, repaired.RequestState);
            Assert.Equal(LedgerEntryState.Terminal, new ExternalStartLedger(root).Read().File!.Entries.Single().State);
            Assert.Equal(1, _double.CountOf("ext.task.start"));                    // 恢复扫描续跑，不重发
            await host2.ShutdownAsync();
        }
        finally
        {
            var ledgerPath = Path.Combine(root, "external-start-ledger.json");
            try { if (File.Exists(ledgerPath)) File.SetAttributes(ledgerPath, FileAttributes.Normal); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// **§24.4-7 冷启动独立拒绝**：ext 通道不可用且 IPC 不可达 ⇒ 接线态零发送，
    /// 按新版队列必需合同报告可证实未发送的 `task_queue_unavailable`。
    /// </summary>
    [Fact]
    public async Task CompositionRoot_ColdStart_WiredPathRefusesWithoutSending()
    {
        var root = Path.Combine(Path.GetTempPath(), "r5comp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            _double.AcceptHello = false;                       // ext 不可用
            IpcClient.PipeNameOverrideForTest = _double.PipeName + ".dead";   // IPC 不可达（无服务端）
            using var client = new BgiExternalClient();
            var host = NewHost(root, client);
            var executor = new CommandExecutor(null!, "unused",
                externalClientProvider: () => client,
                externalStartAdmission: (request, ct) => host.AdmitExternalStartAsync(request, ct));
            await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

            var result = await executor.ExecuteAsync(StartGroupCommand("冷启动组")).WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal("failed", result.Status);
            Assert.Equal("task_queue_unavailable", result.ErrorCode);
            Assert.Equal(0, _double.CountOf("ext.task.start"));
            Assert.Equal(0, _double.CountOf("task.start"));
            await host.ShutdownAsync();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail("等待条件超时：" + nameof(WaitForAsync));
    }

    /// <summary>
    /// 触发宿主初始化（含 §24.12-3 恢复对齐）：用一枚**明确拒绝**的探针请求（不产生发送副作用）。
    /// </summary>
    private static async Task ProbeRecoveryAsync(TaskCenterHost host)
    {
        await host.AdmitExternalStartAsync(new ExternalStartAdmissionRequest
        {
            Namespace = "v2",
            WorkflowId = "onedragon:恢复探针",
            TriggerOccurrenceId = "v2:remote:{requestIdentity}",
            ResourceRef = "onedragon:恢复探针",
            SourceDetail = "fixture:recovery_probe",
            ExecuteAsync = _ => Task.FromResult(ExternalStartExecution.RejectedWith("fixture_probe", false, "fixture")),
        });
    }
    /// <summary>
    /// **§24.4-7 宿主关闭交错**：受理后宿主关闭（租约释放）再送达权威终态 ⇒ 结算写入被拒 ⇒ **不得假成功、
    /// 不得重发**，本笔保守停驻（责任 `Pending`、台账保持未终结，责任由恢复/当前所有者承接）。
    /// </summary>
    [Fact]
    public async Task CompositionRoot_HostShutdownBeforeTerminal_NoFalseSuccessNoResend()
    {
        var root = Path.Combine(Path.GetTempPath(), "r5comp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            using var client = new BgiExternalClient();
            var host = NewHost(root, client);
            var executor = new CommandExecutor(null!, "unused",
                externalClientProvider: () => client,
                externalStartAdmission: (request, ct) => host.AdmitExternalStartAsync(request, ct));
            await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await client.SubscribeAsync([]);

            var start = executor.ExecuteAsync(StartGroupCommand("关闭交错组"));
            await WaitForAsync(() => _double.CountOf("ext.task.start") == 1, TimeSpan.FromSeconds(10));
            var handle = HandleOf(_double, 1);
            // **先证明受理链已完成**（否则本夹具会退化为「未受理即释放租约」的另一种交错）：
            // 台账 = AcceptedPendingExecution、Operation = Accepted、Submission 已关闭。
            var arbitrationDir = Path.Combine(root, "arbitration");
            await WaitForAsync(() =>
                new ExternalStartLedger(root).Read().File?.Entries.SingleOrDefault()?.State == LedgerEntryState.AcceptedPendingExecution
                && new ArbitrationLeaseStore(arbitrationDir).Read().File?.Handoff?.Submission is null,
                TimeSpan.FromSeconds(10));
            Assert.Equal(OperationRequestState.Accepted,
                new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Operations.Single().RequestState);
            await host.ShutdownAsync();                    // 交错：先关闭宿主（释放租约）
            await _double.PushEventAsync("task.completed", handle);
            var result = await start.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.NotEqual("success", result.Status);                   // 不得假成功
            Assert.Equal(ResponsibilityState.Pending, result.ResponsibilityState);   // 责任保留
            Assert.Equal(1, _double.CountOf("ext.task.start"));          // 不得重发
            Assert.Equal(LedgerEntryState.AcceptedPendingExecution,
                new ExternalStartLedger(root).Read().File!.Entries.Single().State);  // 台账未终结（待承接）
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
    /// <summary>
    /// **§24.41-C#7／S2·S3 组合根反例（阻断零发送）**（[新增·2026-09-21 批次二十九]）：
    /// **F11 独立停止闸门激活**时，E3 `start_group` 经真实组合根（`externalStartAdmission` 已注入＝测试接线态）
    /// 必须**不成功且零 `ext.task.start`**，且**无租约副作用**（判定先于租约获取 ⇒ `arbitration` 目录不应被创建；
    /// 若已存在则不得新增任何操作）。依据：§7.1-1（F11 判定先于租约获取）／§24.4-7／§16 交错③「阻断 ⇒ 零发送」。
    /// </summary>
    [Fact]
    public async Task CompositionRoot_F11Active_DeniedZeroSendNoLeaseSideEffect()
    {
        var root = Path.Combine(Path.GetTempPath(), "r5comp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            using var client = new BgiExternalClient();
            var host = new TaskCenterHost(
                Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
                () => client, log: null, runnerFactory: null, readinessOverride: () => (true, null),
                localExecutionCapability: () => true,
                statusSnapshotProvider: () => new ControlStatus { TaskStatusAvailable = true, TaskStatusBgiEpoch = "9:900", TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = false },
                admissionWired: true,
                admissionSeams: new TaskCenterAdmissionSeams { Epoch = "1:1", F11Active = true });
            var executor = new CommandExecutor(null!, "unused",
                externalClientProvider: () => client,
                externalStartAdmission: (request, ct) => host.AdmitExternalStartAsync(request, ct));

            await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await client.SubscribeAsync([]);
            Assert.Equal(BgiExternalLinkState.Ready, client.State);           // 前提：传输就绪（防「传输故障伪造零计数」）
            var arbitrationDir = Path.Combine(root, "arbitration");
            Assert.False(Directory.Exists(arbitrationDir), "前提：本用例调用前不应存在仲裁目录");

            // 组名**中性**（不含 "F11" 字样）：避免「通用错误文案回显组名」把因果断言蒙对
            var blocked = await executor.ExecuteAsync(StartGroupCommand("组合根阻断对照组")).WaitAsync(TimeSpan.FromSeconds(20));

            Assert.NotEqual("success", blocked.Status);                       // 不得报成功
            // **因果证据**：拒绝来自 F11 独立闸门——①结构化责任维为「不适用」`None`（该拒绝未产生发送责任）；
            // ②消息命中 F11 **专属文案**（不得靠「零发送」或组名回显推断——别的故障也会零发送）
            Assert.Equal(ResponsibilityState.None, blocked.ResponsibilityState);
            Assert.Contains("F11 独立停止闸门激活", blocked.Message);
            Assert.Equal(0, _double.CountOf("ext.task.start"));               // **零发送**（直接证据）
            Assert.Equal(0, _double.CountOf("task.start"));                   // 回退面（v2 直发）同样零发送
            // **零租约副作用**（严格）：F11 判定先于租约获取 ⇒ 调用后仍不得创建仲裁目录/台账；
            // 若目录被创建（防御分支）则必须完整为空：无 Operations／无 PreObservations／无未决 Submission／无未决交接。
            Assert.False(Directory.Exists(arbitrationDir),
                "F11 判定必须早于租约获取：不得因被阻断的请求创建 arbitration 目录");
            var handoff = new ArbitrationLeaseStore(arbitrationDir).Read().File?.Handoff;
            if (handoff is not null)
            {
                Assert.Empty(handoff.Operations ?? []);
                Assert.Empty(handoff.PreObservations ?? []);
                Assert.Null(handoff.Submission);
                Assert.Null(handoff.Pending);
            }
            Assert.False(File.Exists(Path.Combine(root, "external-start-ledger.json")),
                "被阻断的请求不得写入外部启动台账");
            await host.ShutdownAsync();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// **§24.41-C#7／S2·S3 组合根反例（冲突零重发）**（[新增·2026-09-21 批次三十九]）：
    /// 替身让 ext 不可用且 v2 即使发送也会回业务冲突；新合同必须在 v2 之前拒绝，
    /// `task.start` 与 `ext.task.start` 都是零发送，不能借旧冲突重试旁路队列要求。
    /// </summary>
    [Fact]
    public async Task CompositionRoot_V2ConflictSetup_NoFallbackSend()
    {
        var root = Path.Combine(Path.GetTempPath(), "r5comp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            using var client = new BgiExternalClient();
            // ext 不可用；v2 被设为冲突，若误走回退便能从发送计数发现。
            _double.AcceptHello = false;
            _double.AcceptV2TaskStart = false;                 // 脚本化 v2「业务冲突」回执（task_already_running）
            var host = new TaskCenterHost(
                Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
                () => client, log: null, runnerFactory: null, readinessOverride: () => (true, null),
                localExecutionCapability: () => true,
                statusSnapshotProvider: () => new ControlStatus { TaskStatusAvailable = true, TaskStatusBgiEpoch = "9:900", TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = false },
                admissionWired: true,
                admissionSeams: new TaskCenterAdmissionSeams { Epoch = "1:1" });
            var executor = new CommandExecutor(null!, "unused",
                externalClientProvider: () => client,
                externalStartAdmission: (request, ct) => host.AdmitExternalStartAsync(request, ct));

            await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            // ext 不可用时客户端降级 `Legacy`（订阅属 ext 操作 ⇒ 此时不得调用，否则响亮失败）
            if (client.State == BgiExternalLinkState.Ready) await client.SubscribeAsync([]);
            Assert.NotEqual(BgiExternalLinkState.Down, client.State);

            // 新合同的无通道拒绝应有界返回；超时报告两种发送计数。
            CommandResult result;
            try
            {
                result = await executor.ExecuteAsync(StartGroupCommand("冲突组")).WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException(
                    "冲突路径未在预算内返回（诊断计数）：task.start=" + _double.CountOf("task.start")
                    + " ext.task.start=" + _double.CountOf("ext.task.start")
                    + " task.status=" + _double.CountOf("task.status"));
            }

            Assert.NotEqual("success", result.Status);                         // 冲突不得报成功
            Assert.Equal("task_queue_unavailable", result.ErrorCode);
            Assert.Equal(0, _double.CountOf("task.start"));                    // 无 v2 回退
            Assert.Equal(0, _double.CountOf("ext.task.start"));
            var ops = new ArbitrationLeaseStore(Path.Combine(root, "arbitration")).Read().File?.Handoff?.Operations ?? [];
            var op = ops.SingleOrDefault(o => o.Candidate?.WorkflowId == "group:冲突组");
            Assert.NotNull(op);                                                // 已经仲裁面登记
            Assert.Equal(1, op!.LastSendSeq);                                  // 许可曾签发，适配器前检确定未发送
            Assert.Equal(OperationRequestState.TerminalRejected, op.RequestState);
            await host.ShutdownAsync();
        }
        finally
        {
            _double.AcceptV2TaskStart = true;                                  // 还原脚本（同一替身实例跨用例复用）
            _double.AcceptHello = true;
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// **§24.4-7 切监控模式（切换闸门）双向断言**：置 `Diag.SwitchGateActive` ⇒ 新启动被**拒绝且零发送**；
    /// 闸门解除 ⇒ 启动恢复正常。切换闸门属控制面写入（不续命、不需所有权，§6.1）。
    /// </summary>
    [Fact]
    public async Task CompositionRoot_SwitchGate_DeniesThenAllows()
    {
        var root = Path.Combine(Path.GetTempPath(), "r5comp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            using var client = new BgiExternalClient();
            var host = NewHost(root, client);
            var executor = new CommandExecutor(null!, "unused",
                externalClientProvider: () => client,
                externalStartAdmission: (request, ct) => host.AdmitExternalStartAsync(request, ct));
            await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await client.SubscribeAsync([]);
            var arbitrationDir = Path.Combine(root, "arbitration");

            // 先跑通一笔（同时让仲裁目录/租约就位）
            var first = executor.ExecuteAsync(StartGroupCommand("闸门组A"));
            await WaitForAsync(() => _double.CountOf("ext.task.start") == 1, TimeSpan.FromSeconds(10));
            await _double.PushEventAsync("task.completed", HandleOf(_double, 1));
            Assert.Equal("success", (await first.WaitAsync(TimeSpan.FromSeconds(15))).Status);

            // ① 切模式：闸门激活 ⇒ 拒绝且**零发送**
            Assert.True(new ArbitrationLeaseStore(arbitrationDir).SetSwitchGate(true, "夹具：切监控模式").Success);
            var opsBeforeGate = new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Operations.Count;
            var blocked = await executor.ExecuteAsync(StartGroupCommand("闸门组B")).WaitAsync(TimeSpan.FromSeconds(20));
            // 闸门**因果证据**（不靠错误码宽松匹配）：①适配器出口不得成功；②**零新增发送**；
            // ③被阻断候选只**登记**（`LastSendSeq==0`）且回到 `Queued` 可再驱动（未占位、无发送责任）。
            Assert.NotEqual("success", blocked.Status);
            Assert.Equal(1, _double.CountOf("ext.task.start"));   // 零新增发送
            var opsAfterGate = new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Operations;
            Assert.Equal(opsBeforeGate + 1, opsAfterGate.Count);
            var gated = opsAfterGate.Single(o => o.Candidate is { WorkflowId: "group:闸门组B" });
            Assert.Equal(0, gated.LastSendSeq);                       // 未签发发送许可
            Assert.Equal(OperationRequestState.Queued, gated.RequestState);   // 可再驱动（非 InRound 悬挂）
            Assert.True(new ArbitrationLeaseStore(arbitrationDir).Read().File!.Diag!.SwitchGateActive);  // 前提：闸门确实激活

            // ② 解除闸门 ⇒ 恢复
            Assert.True(new ArbitrationLeaseStore(arbitrationDir).SetSwitchGate(false, null).Success);
            var allowed = executor.ExecuteAsync(StartGroupCommand("闸门组C"));
            await WaitForAsync(() => _double.CountOf("ext.task.start") == 2, TimeSpan.FromSeconds(10));
            await _double.PushEventAsync("task.completed", HandleOf(_double, 2));
            Assert.Equal("success", (await allowed.WaitAsync(TimeSpan.FromSeconds(15))).Status);
            await host.ShutdownAsync();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// **§16 台账行「控制热键不经准入」·组合根层强制版**：**控制键**（`CancelTaskHotkey`／`BgiEnabledHotkey`／
    /// `SuspendHotkey`）必须**始终直通**——断言：`action.execute_hotkey` 每次恰一次、**`task.status` 零次**、
    /// **零新增仲裁操作**（不经统一仲裁面）。与**普通热键**（E4：接线态经统一仲裁面）形成对照：
    /// `CompositionRoot_ControlHotkey_WiredGoesThroughAdmission_UnwiredDoesNot`。
    /// </summary>
    [Fact]
    public async Task CompositionRoot_ControlKeyHotkeys_BypassAdmissionEntirely()
    {
        var root = Path.Combine(Path.GetTempPath(), "r5comp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            using var client = new BgiExternalClient();
            var host = NewHost(root, client);
            // 即使**注入准入委托**（＝接线态），控制键也不得经仲裁面。
            var wired = new CommandExecutor(null!, "unused", externalClientProvider: () => client,
                externalStartAdmission: (request, ct) => host.AdmitExternalStartAsync(request, ct));
            await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

            var executed = 0;
            foreach (var key in new[] { "CancelTaskHotkey", "BgiEnabledHotkey", "SuspendHotkey" })
            {
                var result = await wired.ExecuteAsync(new RemoteCommand
                {
                    Cmd = "hotkey_execute",
                    Params = new() { ["hotkeyConfigName"] = key },
                }).WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal("success", result.Status);
                executed++;
                Assert.Equal(executed, _double.CountOf("action.execute_hotkey"));   // 每次都恰发一次
            }

            Assert.Equal(0, _double.CountOf("task.status"));    // 状态查询零次（控制键直通路径不查状态）
            var arbitrationDir = Path.Combine(root, "arbitration");
            Assert.True(!Directory.Exists(arbitrationDir)
                        || new ArbitrationLeaseStore(arbitrationDir).Read().File?.Handoff?.Operations is null
                        || new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Operations.Count == 0,
                "控制键不得经统一仲裁面（零仲裁操作）");
            await host.ShutdownAsync();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// **§24.8-3 控制热键双向断言（组合根层）**：接线态 ⇒ 热键经统一仲裁面（产生仲裁操作、核心仍发热键 IPC）；
    /// 未接线（生产默认）⇒ **不产生任何仲裁操作**，热键直接走 IPC（既有语义逐字不变）。
    /// </summary>
    [Fact]
    public async Task CompositionRoot_ControlHotkey_WiredGoesThroughAdmission_UnwiredDoesNot()
    {
        var root = Path.Combine(Path.GetTempPath(), "r5comp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            using var client = new BgiExternalClient();
            var host = NewHost(root, client);
            var wired = new CommandExecutor(null!, "unused", externalClientProvider: () => client,
                externalStartAdmission: (request, ct) => host.AdmitExternalStartAsync(request, ct));
            var unwired = new CommandExecutor(null!, "unused", externalClientProvider: () => client);   // 生产默认
            await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

            var hotkey = new RemoteCommand { Cmd = "hotkey_execute", Params = new() { ["hotkeyConfigName"] = "组合根热键" } };
            var wiredResult = await wired.ExecuteAsync(hotkey).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal("success", wiredResult.Status);
            Assert.True(_double.CountOf("action.execute_hotkey") == 1,
                $"接线态热键应经统一仲裁面并执行核心：status={wiredResult.Status} code={wiredResult.ErrorCode} msg={wiredResult.Message}");
            var arbitrationDir = Path.Combine(root, "arbitration");
            var ops = new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Operations;
            Assert.Contains(ops, o => o.Candidate is { WorkflowId: "hotkey:组合根热键" });   // 接线态：经统一仲裁面
            var identitiesBeforeUnwired = ops.Select(o => o.RequestIdentity).ToArray();

            var unwiredResult = await unwired.ExecuteAsync(hotkey).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal("success", unwiredResult.Status);
            Assert.Equal(2, _double.CountOf("action.execute_hotkey"));                     // 未接线仍直发热键
            // 门关闭证据：**操作集合逐项不变**（不只是数量相同——避免「新增一笔又裁掉一笔」的假通过）。
            var opsAfterUnwired = new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Operations;
            Assert.Equal(identitiesBeforeUnwired, opsAfterUnwired.Select(o => o.RequestIdentity).ToArray());
            Assert.DoesNotContain(opsAfterUnwired,
                o => o.Candidate is { WorkflowId: "hotkey:组合根热键" }
                     && !identitiesBeforeUnwired.Contains(o.RequestIdentity));
            await host.ShutdownAsync();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
