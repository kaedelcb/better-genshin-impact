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
                statusSnapshotProvider: () => new ControlStatus { TaskRunning = false },
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
    /// **§24.4-4 v2 发送成功**：ext 通道不可用（对端老 BGI）⇒ 回退 v2 `task.start`；入口 `success`，
    /// 但**台账保持未终局**（受理≠终态）、责任 `Pending`（不得以「发送成功」冒充终态）。
    /// </summary>
    [Fact]
    public async Task CompositionRoot_V2Success_EntrySucceedsButLedgerStaysUnterminated()
    {
        var root = Path.Combine(Path.GetTempPath(), "r5comp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            _double.AcceptHello = false;   // 老 BGI：ext 不可用 ⇒ v2 路径
            using var client = new BgiExternalClient();
            var host = NewHost(root, client);
            var executor = new CommandExecutor(null!, "unused",
                externalClientProvider: () => client,
                externalStartAdmission: (request, ct) => host.AdmitExternalStartAsync(request, ct));
            await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

            var result = await executor.ExecuteAsync(StartGroupCommand("组合根组D")).WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("success", result.Status);                             // §24.4-4：v2 发送成功＝入口 success
            Assert.False(result.IsTerminal);                                    // 受理≠终态
            Assert.Equal(ResponsibilityState.Pending, result.ResponsibilityState);
            Assert.Equal(1, _double.CountOf("task.start"));                     // 恰一次（无换通道重发）
            Assert.Equal(0, _double.CountOf("ext.task.start"));                 // ext 不可用 ⇒ 未走 ext
            var ledger = new ExternalStartLedger(root).Read().File!.Entries.Single();
            Assert.Equal(LedgerEntryState.AcceptedPendingExecution, ledger.State);   // 台账保持未终局
            var arbitrationDir = Path.Combine(root, "arbitration");
            var op = new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!.Operations.Single(o => o.LastSendSeq > 0);
            Assert.Equal(OperationRequestState.Accepted, op.RequestState);
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
            statusSnapshotProvider: () => new ControlStatus { TaskRunning = false },
            admissionWired: true,
            admissionSeams: new TaskCenterAdmissionSeams { Epoch = "1:1" });

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
}
