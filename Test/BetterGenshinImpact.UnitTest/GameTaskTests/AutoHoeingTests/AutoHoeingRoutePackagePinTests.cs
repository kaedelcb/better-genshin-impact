using System.Reflection;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 直接覆盖任务实际准备/封存入口：预取不能抢占房主 pin，执行期不能再安装，守护新对象继承身份但重新核验。
/// 这些断言决定是否允许预判/执行，不以下载替身返回或旧成功标志代替有效身份；不运行游戏或访问真实 User。
/// </summary>
[Collection("WorldPlanStartupState")]
public sealed class AutoHoeingRoutePackagePinTests
{
    private const string First = "11111111111111111111111111111111";
    private const string Host = "22222222222222222222222222222222";

    static AutoHoeingRoutePackagePinTests()
    {
        var config = typeof(BetterGenshinImpact.Service.ConfigService).GetField(
            "<Config>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        if (config?.GetValue(null) == null) config?.SetValue(null, new AllConfig());
    }

    [Fact]
    public async Task MemberPrefetchDoesNotPinCurrentAndHostConfigMustConfirmItsOwnPin()
    {
        var task = CreateTask();
        var requested = new List<string?>();
        task.UpdateHoeingRoutes = (pin, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            requested.Add(pin);
            return Task.FromResult(pin ?? First);
        };
        await Prepare(task, pinResult: false);
        Assert.Equal("", Field<string>(task, "_routePackageId"));
        Assert.Equal(First, Field<string>(task, "_confirmedRoutePackageId"));
        await Apply(task, Host);
        Assert.Equal(new string?[] { null, Host }, requested);
        Assert.Equal(Host, Field<string>(task, "_routePackageId"));
        Assert.Equal(Host, Field<string>(task, "_confirmedRoutePackageId"));
        Assert.True(Field<bool>(task, "_worldRoutesUpdateSucceeded"));
    }

    [Fact]
    public async Task PinCannotChangeAfterPreparationEvenBeforeExecution()
    {
        var task = CreateTask();
        var calls = 0;
        task.UpdateHoeingRoutes = (pin, _, _) => { calls++; return Task.FromResult(pin ?? First); };
        await Prepare(task);
        await Assert.ThrowsAsync<HoeingStartupException>(() => Apply(task, Host));
        Assert.Equal(First, Field<string>(task, "_routePackageId"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SamePinAcrossWorldsAndAsyncHostConfigCannotReinstallAfterExecutionStarts()
    {
        var task = CreateTask();
        var calls = 0;
        task.UpdateHoeingRoutes = (pin, _, _) => { calls++; return Task.FromResult(pin ?? First); };
        await Prepare(task);
        await InvokeAsync(task, "SealRoutePackageForExecutionAsync", CancellationToken.None);
        await Apply(task, First);
        await Prepare(task);
        await InvokeAsync(task, "SealRoutePackageForExecutionAsync", CancellationToken.None);
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<HoeingStartupException>(() => Prepare(task, repair: true));
        await Assert.ThrowsAsync<HoeingStartupException>(() => Apply(task, Host));
        Assert.Equal(1, calls);
        Assert.Equal(First, Field<string>(task, "_routePackageId"));
    }

    [Fact]
    public async Task GuardRestartInheritsOnlyPinAndMustReconfirmItInsteadOfFetchingCurrent()
    {
        var task = CreateTask();
        var requested = new List<string?>();
        task.UpdateHoeingRoutes = (pin, _, _) =>
        {
            requested.Add(pin);
            return Task.FromResult(pin ?? First);
        };
        await Prepare(task);
        await InvokeAsync(task, "SealRoutePackageForExecutionAsync", CancellationToken.None);
        var restart = (AutoHoeingTask)Invoke(task, "CreateGuardRestartTask")!;
        Set(restart, "_config", new AutoHoeingConfig { MultiplayerEnabled = true });
        Assert.Equal(First, Field<string>(restart, "_routePackageId"));
        Assert.Equal("", Field<string>(restart, "_confirmedRoutePackageId"));
        Assert.False(Field<bool>(restart, "_routePackageExecutionStarted"));
        Assert.False(Field<bool>(restart, "_worldRoutesUpdateSucceeded"));
        Assert.True(Field<bool>(restart, "_isGuardRestartRun"));
        Assert.Null(Field<object?>(restart, "_routePackageLease"));
        await Assert.ThrowsAsync<HoeingStartupException>(() => InvokeAsync(restart, "SealRoutePackageForExecutionAsync", CancellationToken.None));
        await Prepare(restart);
        Assert.Equal(new string?[] { null, First }, requested);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData(First)]
    public async Task EmptyInvalidOrWrongInstallerResultCannotAuthorizePreviewOrExecution(string returned)
    {
        var task = CreateTask();
        task.UpdateHoeingRoutes = (_, _, _) => Task.FromResult(returned);
        await Assert.ThrowsAsync<HoeingStartupException>(() => Apply(task, Host));
        Assert.False(Field<bool>(task, "_worldRoutesUpdateSucceeded"));
        Assert.Equal("", Field<string>(task, "_confirmedRoutePackageId"));
        Assert.Equal((false, 0), ((bool, int))Invoke(task, "PreviewOwnWorld")!);
        await Assert.ThrowsAsync<HoeingStartupException>(() => InvokeAsync(task, "SealRoutePackageForExecutionAsync", CancellationToken.None));
    }

    [Fact]
    public async Task StopAfterInstallerReturnsStillCannotAuthorizeExecution()
    {
        var task = CreateTask();
        using var stop = new CancellationTokenSource();
        task.UpdateHoeingRoutes = (_, _, _) => { stop.Cancel(); return Task.FromResult(First); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Prepare(task, token: stop.Token));
        Assert.False(Field<bool>(task, "_worldRoutesUpdateSucceeded"));
        Assert.Equal("", Field<string>(task, "_routePackageId"));
        await Assert.ThrowsAsync<HoeingStartupException>(() => InvokeAsync(task, "SealRoutePackageForExecutionAsync", CancellationToken.None));
    }

    [Fact]
    public async Task PreparationAndSealSerializeRatherThanAllowingExecutionDuringAnInstall()
    {
        var task = CreateTask();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        task.UpdateHoeingRoutes = (_, _, _) => { started.SetResult(); return finished.Task; };
        var prepare = Prepare(task);
        await started.Task;
        var seal = InvokeAsync(task, "SealRoutePackageForExecutionAsync", CancellationToken.None);
        Assert.False(seal.IsCompleted);
        Assert.False(Field<bool>(task, "_routePackageExecutionStarted"));
        finished.SetResult(First);
        await Task.WhenAll(prepare, seal);
        Assert.True(Field<bool>(task, "_routePackageExecutionStarted"));
    }

    [Fact]
    public async Task EndedTaskRejectsLateConfigurationWithoutCallingInstaller()
    {
        var task = CreateTask();
        Set(task, "_routePackageTaskEnded", true);
        task.UpdateHoeingRoutes = (_, _, _) => throw new InvalidOperationException("installer must not be called");
        var error = await Assert.ThrowsAsync<HoeingStartupException>(() => Prepare(task));
        Assert.Contains("已经结束", error.Message);
    }

    private static AutoHoeingTask CreateTask()
    {
        var task = new AutoHoeingTask();
        Set(task, "_config", new AutoHoeingConfig { MultiplayerEnabled = true, MultiplayerRole = "member", AutoUpdateRoutes = false });
        Set(task, "_ct", CancellationToken.None);
        return task;
    }

    private static Task Prepare(AutoHoeingTask task, bool repair = false, bool pinResult = true, CancellationToken token = default)
        => InvokeAsync(task, "RunAutoUpdateRoutesAsync", token, repair, pinResult);
    private static Task Apply(AutoHoeingTask task, string pin)
        => InvokeAsync(task, "ApplyPinnedHostConfigAsync", new RoomConfig { RoutePackageId = pin, AutoUpdateRoutes = false }, CancellationToken.None);
    private static Task InvokeAsync(AutoHoeingTask task, string method, params object?[] args)
        => (Task)Invoke(task, method, args)!;
    private static object? Invoke(AutoHoeingTask task, string method, params object?[] args)
        => typeof(AutoHoeingTask).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(task, args);
    private static T Field<T>(AutoHoeingTask task, string name)
        => (T)typeof(AutoHoeingTask).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(task)!;
    private static void Set(AutoHoeingTask task, string name, object value)
        => typeof(AutoHoeingTask).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(task, value);
}
