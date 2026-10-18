using System.IO;
using System.Text;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest;

public sealed class ConfigTransferHostGateTests
{
    [Fact]
    public async Task IdleTransferLeaseRejectsStartResumeAndHandoffThenReleasesWithoutRunWrites()
    {
        await using var fixture = new Fixture();
        var lease = fixture.Host.TryBeginConfigTransfer();
        try
        {
            var start = await fixture.Host.StartWorkflowAsync("wf-not-started");
            var resume = await fixture.Host.ResumeRunAsync("run-not-resumed");
            var handoff = await fixture.Host.RegisterHandoffAsync(new StartupHandoffRequest
            {
                WorkflowId = "wf-not-started", ExecutionId = "test-execution", StepId = "test-step",
                IntentKey = "test-intent", Mode = StartupHandoffModes.Start
            });

            Assert.False(start.Ok);
            Assert.Contains("迁移", start.Message);
            Assert.False(resume.Ok);
            Assert.Contains("迁移", resume.Message);
            Assert.Equal(HandoffOutcome.Rejected, handoff.Outcome);
            Assert.Equal(HandoffReasonCodes.NotReady, handoff.ReasonCode);
            Assert.Throws<InvalidOperationException>(() => fixture.Host.TryBeginConfigTransfer());
            Assert.False(Directory.Exists(fixture.Runs));
        }
        finally { lease.Dispose(); }
        lease.Dispose(); // 重复释放不可改变操作计数。

        var ordinaryResume = await fixture.Host.ResumeRunAsync("run-not-resumed");
        Assert.False(ordinaryResume.Ok);
        Assert.Contains("不存在", ordinaryResume.Message);
        using var nextLease = fixture.Host.TryBeginConfigTransfer();
        Assert.False(Directory.Exists(fixture.Runs));
    }

    [Fact]
    public async Task CorruptRunFileIsAnUnknownResponsibilityRatherThanAnIdleTransferSlot()
    {
        await using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Runs);
        var path = Path.Combine(fixture.Runs, "run-damaged.run.json");
        var original = Encoding.UTF8.GetBytes("{broken-run-record");
        File.WriteAllBytes(path, original);

        Assert.Throws<InvalidOperationException>(() => fixture.Host.TryBeginConfigTransfer());

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(fixture.Runs, "*.run.json"));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ConfigTransferHostGateTests-" + Guid.NewGuid().ToString("N"));
        public string Runs => Path.Combine(Root, "runs");
        public TaskCenterHost Host { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Host = new TaskCenterHost(Path.Combine(Root, "flows"), Runs, Path.Combine(Root, "catalog.json"),
                () => null, null, null, () => (false, "隔离测试未连接 BGI"));
        }
        public async ValueTask DisposeAsync()
        {
            await Host.ShutdownAsync();
            var absolute = Path.GetFullPath(Root);
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith("ConfigTransferHostGateTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("拒绝清理测试根以外的目录");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
    }
}
