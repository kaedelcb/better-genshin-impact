using System.Reflection;
using System.Text.RegularExpressions;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoPathing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

[Collection("WorldPlanStartupState")]
public sealed class PostTeleportRevivalProtectionBugConditionTest
{
    private static readonly DateTime CompletedAt = DateTime.UnixEpoch;
    private const int Segment = 2;

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public async Task ProtectedRevivalSelectsCurrentSegmentRetryOnlyOnce(double elapsedSeconds)
    {
        await using var client = new CoordinatorClient();
        var executor = CreateExecutor(client, Segment, Segment, consumed: false);

        var revivalTime = CompletedAt.AddSeconds(elapsedSeconds);
        var suppressed = PathingConditionConfig.RecoverSuppressed;
        try
        {
            PathingConditionConfig.RecoverSuppressed = true;
            Assert.Equal(PathExecutor.RevivalRetryDisposition.RetryProtectedSegment,
                executor.PrepareRevivalRetry(revivalTime, "test revival"));
            Assert.False(executor.SkipToNextSegment);
            Assert.True((bool)ProtectionField("_skipOtherOperations").GetValue(executor)!);
            Assert.Equal(executor.CurWaypoints, typeof(PathExecutor)
                .GetProperty("RecordWaypoints", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(executor));
            Assert.False(PathingConditionConfig.RecoverSuppressed);
            Assert.Equal(PathExecutor.RevivalRetryDisposition.SkipAfterSyncPoint,
                executor.PrepareRevivalRetry(revivalTime, "second revival"));
        }
        finally
        {
            PathingConditionConfig.RecoverSuppressed = suppressed;
        }
    }

    [Theory]
    [InlineData(true, Segment, false, 10.001)]
    [InlineData(true, Segment, false, -1)]
    [InlineData(false, Segment, false, 2)]
    [InlineData(true, Segment + 1, false, 2)]
    [InlineData(true, -1, false, 2)]
    [InlineData(true, Segment, true, 2)]
    public async Task IneligibleRevivalLeavesSegmentRecoveryToTheNormalBranch(
        bool multiplayer, int protectedSegment, bool consumed, double elapsedSeconds)
    {
        await using var client = new CoordinatorClient();
        var executor = CreateExecutor(multiplayer ? client : null, Segment, protectedSegment, consumed);

        var expected = multiplayer
            ? PathExecutor.RevivalRetryDisposition.SkipAfterSyncPoint
            : PathExecutor.RevivalRetryDisposition.ContinueNormalRetry;
        Assert.Equal(expected, executor.PrepareRevivalRetry(CompletedAt.AddSeconds(elapsedSeconds), "test revival"));
        Assert.Equal(consumed ? 1 : 0, (int)ProtectionField("_protectionConsumed").GetValue(executor)!);
    }

    // The retry preparation above runs real executor code. This guard checks the
    // one loop-control edge whose execution would otherwise require a game session.
    [Fact]
    public void RetryCatchContinuesTheSegmentBeforeEvaluatingTheSkipPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "BetterGenshinImpact.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory.FullName,
            "BetterGenshinImpact", "GameTask", "AutoPathing", "PathExecutor.cs"));
        var catchStart = source.IndexOf("catch (RetryException retryException)", StringComparison.Ordinal);
        Assert.True(catchStart >= 0);
        var catchBody = source[catchStart..];
        var protectedBranch = Regex.Match(catchBody,
            @"var\s+(\w+)\s*=\s*PrepareRevivalRetry\([^;]+\);\s*if\s*\(\s*\1\s*==\s*RevivalRetryDisposition\.RetryProtectedSegment\s*\)\s*\{?\s*continue\s*;");
        Assert.True(protectedBranch.Success);
        var skipBranch = Regex.Match(catchBody,
            @"if\s*\(\s*\w+\s*==\s*RevivalRetryDisposition\.SkipAfterSyncPoint\b");
        Assert.True(skipBranch.Success && skipBranch.Index > protectedBranch.Index);
    }

    private static PathExecutor CreateExecutor(
        CoordinatorClient? client, int currentSegment, int protectedSegment, bool consumed)
    {
        var executor = new PathExecutor(CancellationToken.None)
        {
            PartyConfig = new() { AutoEatEnabled = false },
            CurWaypoints = (currentSegment, []),
            MultiplayerCoordinator = client == null
                ? null
                : new MultiplayerCoordinator(client, new SyncPointResolver(), new AutoHoeingConfig())
        };
        ProtectionField("_syncPointReached").SetValue(executor, true);
        ProtectionField("_protectionSegmentId").SetValue(executor, protectedSegment);
        ProtectionField("_protectionCompletedAtUtc").SetValue(executor, CompletedAt);
        ProtectionField("_protectionConsumed").SetValue(executor, consumed ? 1 : 0);
        return executor;
    }

    private static FieldInfo ProtectionField(string name)
        => typeof(PathExecutor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
}
