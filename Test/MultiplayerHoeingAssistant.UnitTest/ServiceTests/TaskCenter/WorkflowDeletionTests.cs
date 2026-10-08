using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>同一隔离数据根验证删除、恢复、修订冲突和运行责任；不使用真实IPC。</summary>
public sealed class WorkflowDeletionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flow-delete-" + Guid.NewGuid().ToString("N"));
    private string Flows => Path.Combine(_root, "flows");
    private string Runs => Path.Combine(_root, "runs");
    private WorkflowStore Store => new(Flows);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (string Id, string Revision) Seed()
    {
        var document = new WorkflowDocument { Name = "日常计划", Activation = new WorkflowActivation { Status = "active" },
            Nodes = [new() { NodeId = "end", Kind = "control.end" }] };
        var revision = Store.Save(document, null);
        return (document.WorkflowId!, revision);
    }

    private TaskCenterHost Host(Func<CancellationToken, Task<string?>>? ensureReady = null)
        => new(Flows, Runs, Path.Combine(_root, "catalog.json"), () => null, null,
            (_, workflows, runs) => new WorkflowRunner(workflows, runs, new NoSendBoundary(), new NoopPrerequisite(), new NoopTerminal()),
            () => ensureReady is null ? (true, null) : (false, "BGI未就绪（隔离测试）"), ensureExecutionReady: ensureReady);

    private sealed class NoopPrerequisite : IWorkflowPrerequisiteAdapter
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct)
            => Task.FromResult(PrerequisiteResult.ProceedInstance);
    }

    private sealed class NoopTerminal : IWorkflowTerminalExecutor
    {
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
            => Task.FromResult(TerminalExecutionResult.Executed("no-effect-test"));
    }

    private sealed class NoSendBoundary : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported => true;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
            => throw new InvalidOperationException("删除测试不得发送资源任务。");
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
            => throw new InvalidOperationException("删除测试不得观察真实任务。");
        public Task RequestCancelAsync(string jobId, CancellationToken ct)
            => throw new InvalidOperationException("删除测试不得取消真实任务。");
    }

    [Fact]
    public void DeleteRestore_PreservesExactOriginalBytes_AndRecoveryRemains()
    {
        var (id, revision) = Seed();
        var original = File.ReadAllBytes(Path.Combine(Flows, id + ".flow.json"));
        var receipt = Store.Delete(id, revision);

        Assert.Empty(Store.List());
        Assert.Equal(original, File.ReadAllBytes(receipt.RecoveryFilePath));
        var listed = Assert.Single(Store.ListDeleted());
        Assert.Equal(id, listed.WorkflowId);
        Assert.Equal(revision, listed.Revision);
        Assert.True(listed.CanRestore);
        var restored = Store.RestoreDeleted(listed.RecoveryId);

        Assert.Equal(revision, restored.Revision);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Flows, id + ".flow.json")));
        Assert.True(File.Exists(receipt.RecoveryFilePath));
        Assert.False(Assert.Single(Store.ListDeleted()).CanRestore);
    }

    [Fact]
    public void QuarantinedDeleteRestore_PreservesBomNewlinesAndMalformedBytes()
    {
        const string id = "wf-quarantined";
        Directory.CreateDirectory(Flows);
        var original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{坏文件\r\n保留原件\r\n")).ToArray();
        File.WriteAllBytes(Path.Combine(Flows, id + ".flow.json"), original);
        var revision = Assert.Single(Store.List()).Revision;

        var receipt = Store.Delete(id, revision);
        Store.RestoreDeleted(receipt.RecoveryId);

        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Flows, id + ".flow.json")));
        Assert.Equal(WorkflowFileStatus.Quarantined, Assert.Single(Store.List()).Status);
    }

    [Fact]
    public void Delete_StaleRevision_PreservesCurrentFile()
    {
        var (id, originalRevision) = Seed();
        var snapshot = Store.LoadSnapshot(id);
        snapshot.Document.Name = "用户后来修改";
        var latest = Store.Save(snapshot.Document, snapshot.Revision);

        Assert.Throws<WorkflowRevisionConflictException>(() => Store.Delete(id, originalRevision));

        Assert.Equal(latest, Store.LoadSnapshot(id).Revision);
        Assert.Empty(Store.ListDeleted());
    }

    [Fact]
    public void Restore_NewerSameIdentity_IsNotOverwritten()
    {
        var (id, revision) = Seed();
        var receipt = Store.Delete(id, revision);
        var newer = new WorkflowDocument { WorkflowId = id, Name = "新的计划" };
        var newerRevision = Store.Save(newer, null);

        Assert.Throws<WorkflowRevisionConflictException>(() => Store.RestoreDeleted(receipt.RecoveryId));

        Assert.Equal(newerRevision, Store.LoadSnapshot(id).Revision);
        Assert.True(File.Exists(receipt.RecoveryFilePath));
    }

    [Fact]
    public void Restore_ModifiedArchive_IsRefused()
    {
        var (id, revision) = Seed();
        var receipt = Store.Delete(id, revision);
        File.AppendAllText(receipt.RecoveryFilePath, " ");

        Assert.False(Assert.Single(Store.ListDeleted()).CanRestore);
        Assert.Throws<WorkflowRevisionConflictException>(() => Store.RestoreDeleted(receipt.RecoveryId));
        Assert.Empty(Store.List());
    }

    [Fact]
    public async Task SaveDelete_TwoStoreInstances_OnlyOneAcceptsOriginalRevision()
    {
        var (id, revision) = Seed();
        var changed = Store.Load(id);
        changed.Name = "并发改名";
        using var start = new ManualResetEventSlim();
        var operations = new[]
        {
            Task.Run(() => { start.Wait(); try { Store.Save(changed, revision); return true; } catch (WorkflowRevisionConflictException) { return false; } }),
            Task.Run(() => { start.Wait(); try { Store.Delete(id, revision); return true; } catch (WorkflowRevisionConflictException) { return false; } catch (FileNotFoundException) { return false; } })
        };
        start.Set();
        var results = await Task.WhenAll(operations);

        Assert.Equal(1, results.Count(accepted => accepted));
        Assert.Equal(1, Store.List().Count + Store.ListDeleted().Count);
    }

    [Theory]
    [InlineData(WorkflowRunState.Planned)]
    [InlineData(WorkflowRunState.Waiting)]
    [InlineData(WorkflowRunState.Running)]
    [InlineData(WorkflowRunState.Paused)]
    [InlineData(WorkflowRunState.Completing)]
    [InlineData(WorkflowRunState.Interrupted)]
    [InlineData(WorkflowRunState.Unknown)]
    [InlineData(WorkflowRunState.LocalWaitParking)]
    public void HostDelete_ResponsibleRun_IsRefused(WorkflowRunState state)
    {
        var (id, revision) = Seed();
        var host = Host();
        var run = host.Runs.CreateRun(id, revision);
        run.State = state;
        host.Runs.Update(run);
        var before = File.ReadAllBytes(Path.Combine(Runs, run.RunId + ".run.json"));

        Assert.False(host.DeleteFlow(id, revision).Ok);

        Assert.Single(Store.List());
        Assert.Empty(Store.ListDeleted());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Runs, run.RunId + ".run.json")));
    }

    [Fact]
    public void HostDelete_CaseAliasOfResponsiblePlan_IsAlsoRefused()
    {
        var (id, revision) = Seed();
        var host = Host();
        var run = host.Runs.CreateRun(id, revision);
        run.State = WorkflowRunState.Unknown;
        host.Runs.Update(run);

        Assert.False(host.DeleteFlow(id.ToUpperInvariant(), revision).Ok);
        Assert.Single(Store.List());
    }

    [Fact]
    public void HostDelete_TerminalButUnsettledSubmission_IsRefused()
    {
        var (id, revision) = Seed();
        var host = Host();
        var run = host.Runs.CreateRun(id, revision);
        run.State = WorkflowRunState.Failed;
        run.CurrentSubmission = new WorkflowSubmission { Key = "pending", JobId = "job-unconfirmed", SendAttempted = true };
        host.Runs.Update(run);

        Assert.False(host.DeleteFlow(id, revision).Ok);
        Assert.Single(Store.List());
    }

    [Fact]
    public void HostDelete_SettledHistoryIsPreserved_AndRestoreDoesNotStart()
    {
        var (id, revision) = Seed();
        var host = Host();
        var run = host.Runs.CreateRun(id, revision);
        run.State = WorkflowRunState.Cancelled;
        host.Runs.Update(run);
        var originalRun = File.ReadAllBytes(Path.Combine(Runs, run.RunId + ".run.json"));

        Assert.True(host.DeleteFlow(id, revision).Ok);
        var receipt = Assert.Single(host.ListDeletedFlows());
        Assert.True(host.RestoreFlow(receipt.RecoveryId).Ok);

        Assert.Equal(originalRun, File.ReadAllBytes(Path.Combine(Runs, run.RunId + ".run.json")));
        Assert.Equal(WorkflowRunState.Cancelled, Assert.Single(host.Runs.List()).State);
        Assert.False(host.IsDriving(id));
    }

    [Fact]
    public void HostDelete_UnrelatedUnknownRun_DoesNotBlockThisPlan()
    {
        var (id, revision) = Seed();
        var host = Host();
        var other = host.Runs.CreateRun("wf-other", "other-revision");
        other.State = WorkflowRunState.Unknown;
        host.Runs.Update(other);

        Assert.True(host.DeleteFlow(id, revision).Ok);
        Assert.Equal(WorkflowRunState.Unknown, host.Runs.Load(other.RunId)!.State);
    }

    [Theory]
    [InlineData("{broken", false)]
    [InlineData("{\"runId\":42,\"workflowId\":\"wf-other\"}", true)]
    [InlineData("{\"runId\":42,\"workflowId\":\"wf-other\",\"workflowId\":\"wf-other\"}", false)]
    public void HostDelete_CorruptRunAssociation_MustBeUnambiguouslyUnrelated(string corrupt, bool allowed)
    {
        var (id, revision) = Seed();
        var host = Host();
        Directory.CreateDirectory(Runs);
        var path = Path.Combine(Runs, "bad.run.json");
        File.WriteAllText(path, corrupt);

        Assert.Equal(allowed, host.DeleteFlow(id, revision).Ok);
        Assert.Equal(corrupt, File.ReadAllText(path));
    }

    [Fact]
    public async Task StartWaitingForBgi_DeletedPlanCannotLaunchFromStaleSnapshot()
    {
        var (id, revision) = Seed();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = Host(async _ => { reached.SetResult(); await release.Task; return null; });
        var pending = host.StartWorkflowAsync(id);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(host.DeleteFlow(id, revision).Ok);
        release.SetResult();
        var result = await pending;

        Assert.False(result.Ok);
        Assert.Empty(host.Runs.List());
        Assert.False(host.IsDriving(id));
        await host.ShutdownAsync();
    }
}
