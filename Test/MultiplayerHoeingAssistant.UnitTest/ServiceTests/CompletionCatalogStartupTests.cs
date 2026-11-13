using System.Reflection;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

// 验证显式刷新与普通读的副作用边界；确保失败/取消必须阻止目录读取，决定是否可接入正式刷新按钮。
public sealed class CompletionCatalogStartupTests
{
    [Theory]
    [InlineData("group")]
    [InlineData("onedragon")]
    public async Task ExplicitBgiRefreshWaitsForEnsureBeforeReadingBoundCatalog(string type)
    {
        var ready = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var expected = new CompletionTargetCatalog([], new(type, "收尾", "", "r1", []));
        var loading = MainViewModel.ReadCompletionCatalogAsync(type, "收尾", "", true,
            _ => { events.Add("ensure"); return ready.Task; },
            (actualType, name, id, _) =>
            {
                events.Add("read"); Assert.Equal(type, actualType); Assert.Equal("收尾", name); Assert.Equal("", id);
                return Task.FromResult(expected);
            });
        Assert.Equal(new[] { "ensure" }, events);
        Assert.False(loading.IsCompleted);
        ready.SetResult(null);
        Assert.Same(expected, await loading);
        Assert.Equal(new[] { "ensure", "read" }, events);
    }

    [Theory]
    [InlineData("workflow", true)]
    [InlineData("workflow", false)]
    [InlineData("group", false)]
    [InlineData("onedragon", false)]
    public async Task WorkflowRefreshAndOrdinaryQueriesNeverEnsureBgi(string type, bool refresh)
    {
        var reads = 0;
        var catalog = await MainViewModel.ReadCompletionCatalogAsync(type, "保存的任务", "wf-original", refresh,
            _ => throw new InvalidOperationException("只读目录不能请求启动"),
            (actualType, name, id, _) =>
            {
                reads++; Assert.Equal(type, actualType); Assert.Equal("保存的任务", name); Assert.Equal("wf-original", id);
                return Task.FromResult(new CompletionTargetCatalog([], null, "未就绪"));
            });
        Assert.Equal(1, reads);
        Assert.Equal("未就绪", catalog.Error);
    }

    [Theory]
    [InlineData("正在更新")]
    [InlineData("观察者模式")]
    [InlineData("启动失败")]
    [InlineData("通道未就绪")]
    public async Task EnsureFailureReturnsCatalogErrorWithoutReadingOrClaimingReady(string failure)
    {
        var result = await MainViewModel.ReadCompletionCatalogAsync("group", "收尾", "", true,
            _ => Task.FromResult<string?>(failure),
            (_, _, _, _) => throw new InvalidOperationException("确保失败后不能读目录"));
        Assert.Null(result.Target);
        Assert.Equal(failure, result.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationBeforeOrDuringEnsureDoesNotReadCatalog(bool beforeEnsure)
    {
        using var cancellation = new CancellationTokenSource();
        var ensures = 0;
        if (beforeEnsure) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MainViewModel.ReadCompletionCatalogAsync(
            "onedragon", "收尾", "", true,
            _ => { ensures++; cancellation.Cancel(); return Task.FromResult<string?>(null); },
            (_, _, _, _) => throw new InvalidOperationException("取消后不能读目录"), cancellation.Token));
        Assert.Equal(beforeEnsure ? 0 : 1, ensures);
    }

    [Fact]
    public async Task ActualAllStopNotificationCancelsTheRefreshWhileItWaitsForTheSharedEnsureGate()
    {
        var vm = new MainViewModel();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var bgiPath = Path.Combine(AssistantDataDirectory.Root, "catalog-startup-fixture", "BetterGI.exe");
        var configuration = new AssistConfig { BgiPath = bgiPath };
        typeof(MainViewModel).GetField("_config", flags)!.SetValue(vm, configuration);
        var gate = (SemaphoreSlim)typeof(MainViewModel).GetField("_bgiEnsureGate", flags)!.GetValue(vm)!;
        await gate.WaitAsync(); // 持住真实共享确保门，整个用例绝不会进入进程枚举、启动或目录读取。
        using var cancellation = new CancellationTokenSource();
        try
        {
            var refresh = (Task<CompletionTargetCatalog>)typeof(MainViewModel).GetMethod("RefreshCompletionCatalogAsync", flags)!
                .Invoke(vm, ["group", "收尾", "", cancellation.Token, null])!;
            Assert.False(refresh.IsCompleted);
            var stop = Assert.IsType<Action>(typeof(MainViewModel).GetField("StopAllAutomationRequested", flags)!.GetValue(vm));
            stop();
            var result = await refresh.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(result.Target);
            Assert.Contains("取消", result.Error!);
            Assert.Equal(0, gate.CurrentCount);
            Assert.Equal(bgiPath, vm.Config!.BgiPath);
            Assert.Null(typeof(MainViewModel).GetField("_taskCenterHost", flags)!.GetValue(vm));
        }
        finally { cancellation.Cancel(); gate.Release(); }
    }

    [Theory]
    [InlineData("group")]
    [InlineData("onedragon")]
    public async Task EmptyExplicitTargetEnsuresThenReadsLiveNamesWithoutChoosingOrDescribingTheFirst(string type)
    {
        var events = new List<string>();
        var transport = new NamesTransport(events);
        var service = new CompletionTargetCatalogService(() => [], _ => throw new InvalidOperationException(), transport);
        var catalog = await MainViewModel.ReadCompletionCatalogAsync(type, "", "", true,
            _ => { events.Add("ensure"); return Task.FromResult<string?>(null); }, service.RefreshAsync);
        Assert.Equal(new[] { "ensure", BgiExternalCatalogTransport.OpConfigList }, events);
        Assert.Equal(new[] { "配置 A", "配置 B" }, catalog.ConfigGroups);
        Assert.Equal(new[] { "每日一条龙" }, catalog.OneClickConfigs);
        Assert.Null(catalog.Target);
        Assert.Contains("请选择", catalog.Error!);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("onedragon")]
    public async Task OrdinaryReadNeverRequestsNamesAndWorkflowRefreshNeverTouchesBgi(string type)
    {
        var events = new List<string>();
        var service = new CompletionTargetCatalogService(() => [], _ => throw new InvalidOperationException(), new NamesTransport(events));
        var empty = await service.LoadAsync(type, "");
        Assert.Empty(events);
        Assert.Null(empty.ConfigGroups); Assert.Null(empty.OneClickConfigs);
        var selected = await service.LoadAsync(type, "原目标");
        Assert.Equal(new[] { BgiExternalCatalogTransport.OpConfigDescribe }, events);
        Assert.Equal("原目标", selected.Target!.Name);
        Assert.Null(selected.ConfigGroups); Assert.Null(selected.OneClickConfigs);
        events.Clear();
        var workflow = await service.RefreshAsync("workflow", "", "");
        Assert.Empty(events);
        Assert.Null(workflow.ConfigGroups); Assert.Null(workflow.OneClickConfigs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"configGroups\":[],\"oneClickConfigs\":null}")]
    [InlineData("{\"configGroups\":[12],\"oneClickConfigs\":[]}")]
    [InlineData("{\"configGroups\":[\"重复\",\"重复\"],\"oneClickConfigs\":[]}")]
    public async Task MissingOrMalformedLiveNamesNeverBecomeAnEmptySuccessfulCatalog(string? list)
    {
        var events = new List<string>();
        var service = new CompletionTargetCatalogService(() => [], _ => throw new InvalidOperationException(),
            new NamesTransport(events) { ListJson = list });
        var result = await service.RefreshAsync("group", "");
        Assert.Equal(new[] { BgiExternalCatalogTransport.OpConfigList }, events);
        Assert.Null(result.Target); Assert.Null(result.ConfigGroups); Assert.Null(result.OneClickConfigs);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.DoesNotContain("请选择", result.Error!);
    }

    [Fact]
    public async Task FailedSelectedDescriptionRetainsConfirmedLiveNamesAndCancellationDoesNotDescribe()
    {
        var events = new List<string>();
        var transport = new NamesTransport(events) { DescriptionJson = null };
        var service = new CompletionTargetCatalogService(() => [], _ => throw new InvalidOperationException(), transport);
        var failed = await service.RefreshAsync("group", "原目标");
        Assert.Null(failed.Target); Assert.NotNull(failed.Error);
        Assert.Equal(new[] { "配置 A", "配置 B" }, failed.ConfigGroups);
        Assert.Equal(new[] { "每日一条龙" }, failed.OneClickConfigs);
        events.Clear();
        using var cancellation = new CancellationTokenSource();
        transport.AfterList = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync("group", "原目标", "", cancellation.Token));
        Assert.Equal(new[] { BgiExternalCatalogTransport.OpConfigList }, events);
    }

    [Fact]
    public void LiveNamesAreOptionalJsonFieldsAndOldCatalogConstructorsRemainValid()
    {
        var old = JsonSerializer.Deserialize<CompletionTargetCatalog>("{\"workflows\":[],\"target\":null,\"error\":null}")!;
        var constructed = new CompletionTargetCatalog([], null);
        Assert.Null(constructed.ConfigGroups); Assert.Null(constructed.OneClickConfigs);
        Assert.Null(old.ConfigGroups); Assert.Null(old.OneClickConfigs);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new CompletionTargetCatalog([], null, "请选择执行目标。", ["配置 A"], ["每日一条龙"])));
        Assert.Equal("配置 A", json.RootElement.GetProperty("configGroups")[0].GetString());
        Assert.Equal("每日一条龙", json.RootElement.GetProperty("oneClickConfigs")[0].GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("target").ValueKind);
    }

    // 使用真实执行端登记、撤销和刷新路径：先到撤销必须变成取消错误，且不能影响另一发送者的同 ID。
    [Theory]
    [InlineData("group")]
    [InlineData("onedragon")]
    public async Task RevocationBeforeRegistrationCancelsOnlyThatSendersRefresh(string type)
    {
        var vm = new MainViewModel();
        using var original = new CancellationTokenSource();
        using var otherSender = new CancellationTokenSource();
        using var duplicate = new CancellationTokenSource();
        CancelRefresh(vm, "original-sender", "same-command");
        Assert.True(RegisterRefresh(vm, ("other-sender", "same-command"), otherSender));
        Assert.False(otherSender.IsCancellationRequested);
        Assert.True(RegisterRefresh(vm, ("original-sender", "same-command"), original));
        Assert.True(original.IsCancellationRequested);
        CancelRefresh(vm, "original-sender", "same-command");
        Assert.False(otherSender.IsCancellationRequested);
        Assert.False(RegisterRefresh(vm, ("original-sender", "same-command"), duplicate));
        Assert.False(duplicate.IsCancellationRequested);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var refresh = (Task<CompletionTargetCatalog>)typeof(MainViewModel).GetMethod("RefreshCompletionCatalogAsync", flags)!
            .Invoke(vm, [type, "收尾", "", original.Token, DateTimeOffset.UtcNow.AddSeconds(180)])!;
        var result = await refresh.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(result.Target);
        Assert.Contains("取消", result.Error!);
        Assert.Null(typeof(MainViewModel).GetField("_taskCenterHost", flags)!.GetValue(vm));
    }

    [Fact]
    public async Task ConcurrentRegistrationAndRevocationCannotLeaveTheRegisteredTokenAuthorized()
    {
        var vm = new MainViewModel();
        using var cancellation = new CancellationTokenSource();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = Task.Run(async () =>
        {
            await start.Task;
            return RegisterRefresh(vm, ("sender", "crossing-command"), cancellation);
        });
        var revocation = Task.Run(async () =>
        {
            await start.Task;
            CancelRefresh(vm, "sender", "crossing-command");
        });
        start.SetResult();
        await Task.WhenAll(registration, revocation).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(await registration);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public void RevocationCapacityCannotAdmitAnUnrecordedCancelAndExpiredRecordsArePruned()
    {
        var vm = new MainViewModel();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var limit = (int)typeof(MainViewModel).GetField("CompletionCatalogRevocationLimit", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetRawConstantValue()!;
        var records = Assert.IsType<Dictionary<(string Sender, string Command), DateTimeOffset>>(
            typeof(MainViewModel).GetField("_completionCatalogRevocations", flags)!.GetValue(vm));
        using var unrelatedActive = new CancellationTokenSource();
        Assert.True(RegisterRefresh(vm, ("other-sender", "overflow-command"), unrelatedActive));
        for (var index = 0; index < limit; index++) CancelRefresh(vm, "sender", "command-" + index);
        CancelRefresh(vm, "sender", "overflow-command");
        Assert.Equal(limit, records.Count);
        using var overflow = new CancellationTokenSource();
        Assert.True(RegisterRefresh(vm, ("sender", "overflow-command"), overflow));
        Assert.True(overflow.IsCancellationRequested);
        Assert.False(unrelatedActive.IsCancellationRequested);

        // 直接推进记录期限，避免为了验证 180 秒回收合同等待真实时间。
        var expired = DateTimeOffset.UtcNow.AddSeconds(-1);
        foreach (var key in records.Keys.ToArray()) records[key] = expired;
        typeof(MainViewModel).GetField("_completionCatalogRevocationsFullUntil", flags)!.SetValue(vm, expired);
        using var fresh = new CancellationTokenSource();
        Assert.True(RegisterRefresh(vm, ("sender", "fresh-command"), fresh));
        Assert.False(fresh.IsCancellationRequested);
        Assert.Empty(records);
    }

    private static bool RegisterRefresh(MainViewModel vm, (string Sender, string Command) key, CancellationTokenSource cancellation)
        => (bool)typeof(MainViewModel).GetMethod("TryRegisterCompletionCatalogRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [key, cancellation])!;

    private static void CancelRefresh(MainViewModel vm, string sender, string command)
        => typeof(MainViewModel).GetMethod("CancelCompletionCatalogRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [sender, command]);

    private sealed class NamesTransport(List<string> events) : IResourceCatalogTransport
    {
        public bool IsReady => true;
        public bool HasCapability(string name) => true;
        public string? ListJson { get; init; } = "{\"configGroups\":[\"配置 A\",\"配置 B\"],\"oneClickConfigs\":[\"每日一条龙\"]}";
        public string? DescriptionJson { get; init; } = "{\"configRevision\":\"r1\",\"tasks\":[{\"taskId\":\"node-1\",\"name\":\"任务一\",\"enabled\":true}]}";
        public Action? AfterList { get; set; }
        public Task<string?> SendAsync(string operation, object payload, CancellationToken ct)
        {
            events.Add(operation);
            if (operation == BgiExternalCatalogTransport.OpConfigList) { AfterList?.Invoke(); return Task.FromResult(ListJson); }
            Assert.Equal(BgiExternalCatalogTransport.OpConfigDescribe, operation);
            return Task.FromResult(DescriptionJson);
        }
    }

    [Fact]
    public async Task CancellationRevokesAnAlreadyQueuedRefreshBeforeEnsureCanRead()
    {
        using var cancellation = new CancellationTokenSource();
        var queue = new SemaphoreSlim(0, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var pending = MainViewModel.ReadCompletionCatalogAsync("group", "收尾", "", true,
            async ct => { entered.SetResult(); await queue.WaitAsync(ct); ct.ThrowIfCancellationRequested(); starts++; return null; },
            (_, _, _, _) => throw new InvalidOperationException("已撤销的旧刷新不能读目录"), cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        queue.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, starts);
        queue.Dispose();
    }
}
