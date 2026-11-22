using System.Net;
using System.Net.Http;
using System.Reflection;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

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
    public async Task MemberDownloadsOnlyTheRoomPinAndReusesPreparation()
    {
        var task = CreateTask();
        var requested = new List<string?>();
        task.UpdateHoeingRoutes = (pin, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            requested.Add(pin);
            return Task.FromResult(pin ?? First);
        };
        await Apply(task, Host);
        await Apply(task, Host);
        Assert.Equal(new[] { Host }, requested);
        Assert.Equal(Host, Field<string>(task, "_routePackageId"));
        Assert.Equal(Host, Field<string>(task, "_confirmedRoutePackageId"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task EitherOptOutPreventsAllRouteRequestsIncludingRepair(bool localUpdates, bool hostUpdates)
    {
        using var files = new AutoHoeingRouteUpdaterTests.Fixture();
        var installed = files.Add();
        await files.Ensure();
        files.Requests.Clear();
        var task = CreateTask(localUpdates);
        Set(task, "_routePackageUpdater", files.Updater);
        try
        {
            await Apply(task, Host, hostUpdates);
            Assert.False(await Prepare(task, repair: true));
            Assert.Equal(localUpdates, Field<AutoHoeingConfig>(task, "_config").AutoUpdateRoutes);
            Assert.Equal(installed.Id, Field<string>(task, "_confirmedRoutePackageId"));
            Assert.Empty(files.Requests);
            await Assert.ThrowsAsync<HoeingStartupException>(() => Seal(task));
            await Seal(task, routeHashesVerified: true);
        }
        finally { Field<AutoHoeingRouteUpdater.Lease?>(task, "_routePackageLease")?.Dispose(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HostKeepsInstalledRoutesWithoutRetryingAfterFailureOrOptOut(bool autoUpdate)
    {
        using var files = new AutoHoeingRouteUpdaterTests.Fixture();
        var installed = files.Add();
        await files.Ensure();
        files.Add();
        files.Requests.Clear();
        files.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsolutePath.EndsWith("/download", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null);
        var task = CreateTask(autoUpdate, role: "host");
        Set(task, "_routePackageUpdater", files.Updater);
        try
        {
            Assert.False(await Prepare(task));
            Assert.True(Field<bool>(task, "_roomUsesLocalRoutes"));
            Assert.Equal(installed.Id, Field<string>(task, "_routePackageId"));
            Assert.Equal(installed.Id, Field<string>(task, "_confirmedRoutePackageId"));
            Assert.Single(Directory.GetFiles(files.Current, "*.json", SearchOption.AllDirectories));
            var requestCount = files.Requests.Count;
            if (autoUpdate) Assert.Contains(files.Requests, path => path.EndsWith("/download", StringComparison.Ordinal));
            else Assert.Empty(files.Requests);
            await Apply(task, installed.Id, hostUpdates: false, useLocalRoutes: true);
            Assert.False(await Prepare(task, repair: true));
            await Seal(task, routeHashesVerified: true);
            Field<AutoHoeingRouteUpdater.Lease?>(task, "_routePackageLease")?.Dispose();

            var restart = (AutoHoeingTask)Invoke(task, "CreateGuardRestartTask")!;
            Set(restart, "_config", Field<AutoHoeingConfig>(task, "_config"));
            Set(restart, "_routePackageUpdater", files.Updater);
            try
            {
                Assert.False(await Prepare(restart));
                Assert.Equal(installed.Id, Field<string>(restart, "_confirmedRoutePackageId"));
                Assert.Equal(requestCount, files.Requests.Count);
            }
            finally { Field<AutoHoeingRouteUpdater.Lease?>(restart, "_routePackageLease")?.Dispose(); }
        }
        finally { Field<AutoHoeingRouteUpdater.Lease?>(task, "_routePackageLease")?.Dispose(); }
    }

    [Fact]
    public async Task MissingLocalPackageFallsThroughToResourceHandlingAndCannotAuthorizePreview()
    {
        using var files = new AutoHoeingRouteUpdaterTests.Fixture();
        var task = CreateTask(false, role: "host");
        Set(task, "_routePackageUpdater", files.Updater);
        try
        {
            Assert.False(await Prepare(task));
            Assert.True(Field<bool>(task, "_roomUsesLocalRoutes"));
            Assert.Empty(Field<string>(task, "_routePackageId"));
            Assert.Equal((false, 0), ((bool, int))Invoke(task, "PreviewOwnWorld")!);
            var error = Assert.Throws<TargetInvocationException>(() => Invoke(task, "LoadRoutesBasedOnConfig"));
            Assert.IsType<HoeingMissingResourceException>(error.InnerException);
            await Assert.ThrowsAsync<HoeingStartupException>(() => Seal(task));
            Assert.Empty(files.Requests);
        }
        finally { Field<AutoHoeingRouteUpdater.Lease?>(task, "_routePackageLease")?.Dispose(); }
    }

    [Fact]
    public async Task PinAndSourceCannotChangeAfterPreparationOrReinstallAfterExecutionStarts()
    {
        var task = CreateTask();
        var calls = 0;
        task.UpdateHoeingRoutes = (pin, _, _) => { calls++; return Task.FromResult(pin ?? First); };
        await Prepare(task);
        await Assert.ThrowsAsync<HoeingStartupException>(() => Apply(task, Host));
        await Assert.ThrowsAsync<HoeingStartupException>(() => Apply(task, First, hostUpdates: false, useLocalRoutes: true));
        await Seal(task);
        await Apply(task, First);
        await Prepare(task);
        await Seal(task);
        await Assert.ThrowsAsync<HoeingStartupException>(() => Prepare(task, repair: true));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GuardRestartInheritsPinAndMustPrepareAgainBeforeExecution()
    {
        var task = CreateTask();
        var requested = new List<string?>();
        task.UpdateHoeingRoutes = (pin, _, _) =>
        {
            requested.Add(pin);
            return Task.FromResult(pin ?? First);
        };
        await Prepare(task);
        await Seal(task);
        var restart = (AutoHoeingTask)Invoke(task, "CreateGuardRestartTask")!;
        Set(restart, "_config", new AutoHoeingConfig { MultiplayerEnabled = true });
        Assert.Equal(First, Field<string>(restart, "_routePackageId"));
        Assert.Empty(Field<string>(restart, "_confirmedRoutePackageId"));
        Assert.False(Field<bool>(restart, "_routePackageExecutionStarted"));
        Assert.False(Field<bool>(restart, "_routesPrepared"));
        Assert.True(Field<bool>(restart, "_isGuardRestartRun"));
        Assert.Null(Field<object?>(restart, "_routePackageLease"));
        await Assert.ThrowsAsync<HoeingStartupException>(() => Seal(restart));
        await Prepare(restart);
        Assert.Equal(new string?[] { null, First }, requested);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData(First)]
    public async Task InvalidInstallerResultRequiresActualRouteValidation(string returned)
    {
        using var files = new AutoHoeingRouteUpdaterTests.Fixture();
        var task = CreateTask();
        Set(task, "_routePackageUpdater", files.Updater);
        task.UpdateHoeingRoutes = (_, _, _) => Task.FromResult(returned);
        try
        {
            await Apply(task, Host);
            Assert.Empty(Field<string>(task, "_confirmedRoutePackageId"));
            Assert.Equal((false, 0), ((bool, int))Invoke(task, "PreviewOwnWorld")!);
            await Assert.ThrowsAsync<HoeingStartupException>(() => Seal(task));
        }
        finally { Field<AutoHoeingRouteUpdater.Lease?>(task, "_routePackageLease")?.Dispose(); }
    }

    [Fact]
    public async Task StopAfterInstallerReturnsStillCannotAuthorizeExecution()
    {
        var task = CreateTask();
        using var stop = new CancellationTokenSource();
        task.UpdateHoeingRoutes = (_, _, _) => { stop.Cancel(); return Task.FromResult(First); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Prepare(task, token: stop.Token));
        Assert.False(Field<bool>(task, "_routesPrepared"));
        Assert.Empty(Field<string>(task, "_routePackageId"));
        await Assert.ThrowsAsync<HoeingStartupException>(() => Seal(task));
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
        var seal = Seal(task);
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

    private static AutoHoeingTask CreateTask(bool autoUpdate = true, string role = "member")
    {
        var task = new AutoHoeingTask();
        Set(task, "_config", new AutoHoeingConfig { MultiplayerEnabled = true, MultiplayerRole = role, AutoUpdateRoutes = autoUpdate });
        Set(task, "_ct", CancellationToken.None);
        return task;
    }

    private static Task<bool> Prepare(AutoHoeingTask task, bool repair = false, CancellationToken token = default)
        => (Task<bool>)Invoke(task, "RunAutoUpdateRoutesAsync", token, repair)!;
    private static Task Apply(AutoHoeingTask task, string pin, bool hostUpdates = true, bool useLocalRoutes = false)
        => (Task)Invoke(task, "ApplyPinnedHostConfigAsync", new RoomConfig
        {
            RoutePackageId = pin, AutoUpdateRoutes = hostUpdates, UseLocalRouteFallback = useLocalRoutes
        }, CancellationToken.None)!;
    private static Task Seal(AutoHoeingTask task, bool routeHashesVerified = false)
        => (Task)Invoke(task, "SealRoutePackageForExecutionAsync", CancellationToken.None, routeHashesVerified)!;
    private static object? Invoke(AutoHoeingTask task, string method, params object?[] args)
        => typeof(AutoHoeingTask).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(task, args);
    private static T Field<T>(AutoHoeingTask task, string name)
        => (T)typeof(AutoHoeingTask).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(task)!;
    private static void Set(AutoHoeingTask task, string name, object value)
        => typeof(AutoHoeingTask).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(task, value);
}
