using System.Text.Json;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

[Collection("GlobalUserRoot")]
public sealed class PickupWaypointProgressTests
{
    [Fact]
    public void ThresholdCombinesSeparateListAppearancesWithinTheSameNode()
    {
        Assert.Null(PickupWaypointProgress.Capture());
        var point = PickupWaypointProgress.TryBegin(3)!;
        var empty = new PickupRowObservation(true, []);
        var one = new PickupRowObservation(true, [new PickupVisualRow(563, 0x789CE33256DBE2AC)]);
        var two = new PickupRowObservation(true,
            [new PickupVisualRow(563, 0x789CE33256DBE2AC), new PickupVisualRow(611, 0x789CE33256DBE2AC)]);
        using (point)
        {
            Assert.Same(point, PickupWaypointProgress.Capture());
            for (var i = 0; i < 3; i++) point.Observe(empty);
            point.Observe(two);
            point.Observe(two);
            Assert.Equal(2, point.Count);

            for (var i = 0; i < 3; i++) point.Observe(empty);
            Assert.Equal(2, point.Count);
            Assert.False(point.ReachedToken.IsCancellationRequested);

            point.Observe(one);
            point.Observe(one);
            Assert.Equal(3, point.Count);
            Assert.True(point.ReachedToken.IsCancellationRequested);
        }

        using var next = PickupWaypointProgress.TryBegin(1)!;
        point.Observe(one); // 上一节点的迟到画面不能算到下一点。
        Assert.Equal(0, next.Count);
        Assert.False(next.ReachedToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"pickup_target_count\":0")]
    [InlineData(",\"pickup_target_count\":-1")]
    [InlineData(",\"pickup_target_count\":2.5")]
    [InlineData(",\"pickup_target_count\":\"0\"")]
    [InlineData(",\"pickup_target_count\":\"-3\"")]
    [InlineData(",\"pickup_target_count\":\"+3\"")]
    [InlineData(",\"pickup_target_count\":\" 3\"")]
    [InlineData(",\"pickup_target_count\":\"2147483648\"")]
    [InlineData(",\"pickup_target_count\":{\"invalid\":true}")]
    [InlineData(",\"pickup_target_count\":[2]")]
    [InlineData(",\"pickup_target_count\":\"2/\"")]
    [InlineData(",\"pickup_target_count\":\"/3\"")]
    [InlineData(",\"pickup_target_count\":\"0/3\"")]
    [InlineData(",\"pickup_target_count\":\"2/-3\"")]
    [InlineData(",\"pickup_target_count\":\"2/3/4\"")]
    [InlineData(",\"pickup_target_count\":\"2147483648/3\"")]
    [InlineData(",\"pickup_target_count\":\"2/2147483648\"")]
    [InlineData(",\"pickup_target_count\":\"2/3\\u0000\"")]
    public void MissingOrInvalidCountStillLoadsExistingAction(string field)
    {
        var point = JsonSerializer.Deserialize<Waypoint>("{\"type\":\"target\",\"action\":\"combat_script\","
            + "\"action_params\":\"keypress(f);万叶 e;\"" + field + "}", PathRecorder.JsonOptions)!;

        Assert.Null(point.PickupTargetCount);
        Assert.Equal("keypress(f);万叶 e;", point.ActionParams);
        Assert.DoesNotContain("pickup_target_count", JsonSerializer.Serialize(point, PathRecorder.JsonOptions));
    }

    [Theory]
    [InlineData("2", 2, 0, "2")]
    [InlineData("\"3\"", 3, 0, "3")]
    [InlineData("\"2/0\"", 2, 0, "2")]
    [InlineData("\"2/3\"", 2, 3, "\"2/3\"")]
    [InlineData("\"2/2147483647\"", 2, int.MaxValue, "\"2/2147483647\"")]
    public void ValidCountRoundTripsThroughTheSameField(string input, int count, int skip, string output)
    {
        var point = JsonSerializer.Deserialize<Waypoint>("{\"pickup_target_count\":" + input + "}", PathRecorder.JsonOptions)!;
        Assert.Equal(new PickupTarget(count, skip), point.PickupTargetCount);
        var saved = JsonSerializer.Serialize(point, PathRecorder.JsonOptions);
        using var document = JsonDocument.Parse(saved);
        Assert.Equal(output, document.RootElement.GetProperty("pickup_target_count").GetRawText());
        Assert.Equal(point.PickupTargetCount, JsonSerializer.Deserialize<Waypoint>(saved, PathRecorder.JsonOptions)!.PickupTargetCount);
    }

    [Fact]
    public void AnOldConfigStillDefaultsOff()
    {
        Assert.False(JsonSerializer.Deserialize<PathingPartyConfig>("{}")!.EnablePickupTargetCount);
        var enabled = new PathingPartyConfig { EnablePickupTargetCount = true }.CloneForSoloTask();
        Assert.True(JsonSerializer.Deserialize<PathingPartyConfig>(JsonSerializer.Serialize(enabled))!.EnablePickupTargetCount);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(1000)]
    public async Task ReachedBeforeArrivalDoesNotStartTheTailWait(int milliseconds)
    {
        using var progress = PickupWaypointProgress.TryBegin(1)!;
        MarkReached(progress);

        await progress.WaitForArrivalTailAsync(milliseconds, CancellationToken.None,
            (_, _) => throw new InvalidOperationException("已达标不应启动尾等待"));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(1000)]
    public async Task ReachingDuringTheTailWaitCancelsItAndContinuesNormally(int milliseconds)
    {
        using var progress = PickupWaypointProgress.TryBegin(1)!;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedFullWait = false;
        var waiting = progress.WaitForArrivalTailAsync(milliseconds, CancellationToken.None, async (duration, stop) =>
        {
            Assert.Equal(milliseconds, duration);
            Assert.True(stop.CanBeCanceled);
            Assert.Equal(stop, BetterGenshinImpact.Core.Input.InputHub.CurrentOperationToken);
            started.SetResult();
            // 原等待只有收到取消才退出；不能靠测试耗时偶然通过。
            await Task.Delay(30_000, stop);
            completedFullWait = true;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        MarkReached(progress);

        await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(completedFullWait);
        Assert.Null(BetterGenshinImpact.Core.Input.InputHub.CurrentOperationToken);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(1000)]
    public async Task AnUnreachedNodeAwaitsTheCompleteOriginalTail(int milliseconds)
    {
        using var progress = PickupWaypointProgress.TryBegin(1)!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishedOriginalWait = false;
        var waiting = progress.WaitForArrivalTailAsync(milliseconds, CancellationToken.None, async (duration, stop) =>
        {
            Assert.Equal(milliseconds, duration);
            started.SetResult();
            await release.Task.WaitAsync(stop);
            finishedOriginalWait = true;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(waiting.IsCompleted);
        Assert.False(progress.ReachedToken.IsCancellationRequested);

        release.SetResult();
        await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(finishedOriginalWait);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RouteCancellationWinsWhenPickupAlsoReachesDuringArrivalWait(bool routeFirst)
    {
        using var progress = PickupWaypointProgress.TryBegin(1)!;
        using var route = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationMayReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = progress.WaitForArrivalTailAsync(1000, route.Token, async (_, stop) =>
        {
            started.SetResult();
            try { await Task.Delay(30_000, stop); }
            catch (OperationCanceledException)
            {
                await cancellationMayReturn.Task;
                throw;
            }
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (routeFirst) route.Cancel();
        MarkReached(progress);
        if (!routeFirst) route.Cancel();
        cancellationMayReturn.SetResult();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waiting.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(route.Token, error.CancellationToken);
    }

    [Fact]
    public async Task AStoppedRouteIsNotSwallowedEvenWhenPickupWasAlreadyComplete()
    {
        using var progress = PickupWaypointProgress.TryBegin(1)!;
        using var route = new CancellationTokenSource();
        MarkReached(progress);
        route.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            progress.WaitForArrivalTailAsync(1000, route.Token, (_, _) => Task.CompletedTask));
        Assert.Equal(route.Token, error.CancellationToken);
    }

    [Fact]
    public async Task ArrivalWaitDoesNotSwallowAnotherCancellationWhenPickupReaches()
    {
        using var progress = PickupWaypointProgress.TryBegin(1)!;
        using var other = new CancellationTokenSource();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            progress.WaitForArrivalTailAsync(1000, CancellationToken.None, (_, _) =>
            {
                MarkReached(progress);
                throw new OperationCanceledException(other.Token);
            }));
        Assert.Equal(other.Token, error.CancellationToken);
    }

    [Fact]
    public async Task ArrivalWaitStillPropagatesOrdinaryFailures()
    {
        using var progress = PickupWaypointProgress.TryBegin(1)!;
        var failure = new InvalidOperationException("等待失败");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            progress.WaitForArrivalTailAsync(1000, CancellationToken.None, (_, _) => throw failure));
        Assert.Same(failure, error);
        Assert.Null(BetterGenshinImpact.Core.Input.InputHub.CurrentOperationToken);
    }

    private static void MarkReached(PickupWaypointProgress progress)
    {
        for (var i = 0; i < 3; i++) progress.Observe(new PickupRowObservation(true, []));
        var one = new PickupRowObservation(true, [new PickupVisualRow(563, 0x789CE33256DBE2AC)]);
        progress.Observe(one);
        progress.Observe(one);
        Assert.True(progress.ReachedToken.IsCancellationRequested);
    }

}
