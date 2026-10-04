using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class TypedAdmissionParentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "typed-parent-" + Guid.NewGuid().ToString("N"));
    private RunStore Store() => new(Path.Combine(_root, "runs"));
    private static HandoffIdentity Original() => new()
    {
        IntentKey = "manual:original", ExecutionId = "execution-original", StepId = "step-original",
        Mode = StartupHandoffModes.Start,
    };

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void AcceptedOriginalIsDurableAndOptionalFieldsStayOptional()
    {
        var store = Store();
        var original = Original();
        original.ExecutionId = original.StepId = "";
        var run = store.CreateRun("wf-original", "rev-1", handoff: original, admissionSourceScope: "bgi:local:9:900");
        var reopened = Store().Load(run.RunId)!;
        var source = TaskCenterHost.ResolveAdmissionParent([], reopened, run.RunId, run.WorkflowId);
        Assert.Equal(run.AdmissionParentSource, source);
        Assert.Equal(original.IntentKey, source!.Value.RequestIdentity);
        Assert.Equal(AdmissionParentKind.StartupHandoff, source.Value.Kind);
        Assert.True(source.Value.MatchesHandoff(reopened));
        reopened.Handoffs.Add(new HandoffIdentity { IntentKey = "later-resume", Mode = StartupHandoffModes.Resume });
        store.Update(reopened);
        Assert.Equal(source, TaskCenterHost.ResolveAdmissionParent([], Store().Load(run.RunId), run.RunId, run.WorkflowId));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("run")]
    [InlineData("workflow")]
    [InlineData("scope")]
    [InlineData("identity")]
    [InlineData("json")]
    [InlineData("deleted")]
    [InlineData("duplicate")]
    [InlineData("changed")]
    public void ChangedOrMissingOriginalCannotAuthorize(string fault)
    {
        var run = Store().CreateRun("wf-original", "rev-1", handoff: Original(), admissionSourceScope: "bgi:local:9:900");
        var source = run.AdmissionParentSource!.Value;
        run.AdmissionParentSource = fault switch
        {
            "version" => source with { Version = 2 },
            "run" => source with { RunId = "other" },
            "workflow" => source with { WorkflowId = "other" },
            "scope" => source with { Scope = "bgi:local:other" },
            "identity" => source with { RequestIdentity = "other" },
            "json" => source with { OriginalHandoff = source.OriginalHandoff! with { ExecutionId = "forged" } },
            _ => source,
        };
        if (fault == "deleted") run.Handoffs.Clear();
        if (fault == "duplicate") run.Handoffs.Add(Original());
        if (fault == "changed") run.Handoffs[0].TriggerKind = "changed";
        Assert.Null(TaskCenterHost.ResolveAdmissionParent([], run, run.RunId, run.WorkflowId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MergingWriterCannotMintOrChangeParent(bool modern)
    {
        var store = Store();
        var run = store.CreateRun("wf-original", "rev-1", handoff: modern ? Original() : null,
            admissionSourceScope: modern ? "bgi:local:9:900" : null);
        var path = Path.Combine(_root, "runs", run.RunId + ".run.json");
        var bytes = File.ReadAllBytes(path);
        Assert.Throws<RunRecordConflictException>(() => store.UpdateMergingIf(run.RunId, current =>
        {
            current.AdmissionSourceScope = "bgi:local:forged";
            current.AdmissionParentSource = AdmissionParentSource.Handoff(current, Original());
            return true;
        }, out _));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void NewOrdinaryRecordCannotMintAcceptedParent()
    {
        var run = new WorkflowRunRecord { RunId = "forged", WorkflowId = "wf-original",
            AdmissionSourceScope = "bgi:local:9:900", Handoffs = [Original()] };
        run.AdmissionParentSource = AdmissionParentSource.Handoff(run, run.Handoffs[0]);
        Assert.Throws<RunRecordConflictException>(() => Store().Update(run));
        Assert.False(File.Exists(Path.Combine(_root, "runs", run.RunId + ".run.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PanelAndHandoffSourcesCannotBeChosenByPreference(bool differentWorkflow)
    {
        var run = Store().CreateRun("wf-original", "rev-1", handoff: Original(), admissionSourceScope: "bgi:local:9:900");
        var workflow = differentWorkflow ? "other-workflow" : run.WorkflowId;
        var panel = new OperationRecord { RequestIdentity = "panel-parent", RunBinding = run.RunId,
            Intent = "start", ResourceRef = "flow:" + workflow, OperationType = OperationType.FlowRegistration,
            Candidate = new ArbitrationCandidate { WorkflowId = workflow, Scope = run.AdmissionSourceScope!, NodeId = "" } };
        Assert.Null(TaskCenterHost.ResolveAdmissionParent([panel], run, run.RunId, run.WorkflowId));
    }

    [Fact]
    public void PanelParentCannotAuthorizeAnAliasedRunRecord()
    {
        var panel = new OperationRecord { RequestIdentity = "panel-parent", RunBinding = "original-run",
            Intent = "start", ResourceRef = "flow:wf-original", OperationType = OperationType.FlowRegistration,
            Candidate = new ArbitrationCandidate { WorkflowId = "wf-original", Scope = "bgi:local:9:900", NodeId = "" } };
        var run = new WorkflowRunRecord { RunId = "other-run", WorkflowId = "wf-original" };
        Assert.Null(TaskCenterHost.ResolveAdmissionParent([panel], run, "original-run", run.WorkflowId));
    }

    [Fact]
    public void LegacySourceFieldIsOmittedAndCannotBeBackfilled()
    {
        var store = Store();
        var run = store.CreateRun("wf-original", "rev-1");
        Assert.DoesNotContain("admissionParentSource", File.ReadAllText(Path.Combine(_root, "runs", run.RunId + ".run.json")));
        run.AdmissionParentSource = new AdmissionParentSource(1, AdmissionParentKind.StartupHandoff,
            run.RunId, run.WorkflowId, "bgi:local:9:900", "forged", AdmissionHandoffIdentity.Capture(Original()));
        Assert.Throws<RunRecordConflictException>(() => store.Update(run));
    }

    [Fact]
    public void LegacyBareScopeAndHandoffListCannotMintOriginalParent()
    {
        var run = new WorkflowRunRecord
        {
            RunId = "legacy-run", WorkflowId = "wf-original", AdmissionSourceScope = "bgi:local:9:900",
            Handoffs = [Original()],
        };
        Assert.Null(TaskCenterHost.ResolveAdmissionParent([], run, run.RunId, run.WorkflowId));
    }

    [Fact]
    public void OriginalHandoffSourceCannotAliasAnotherRunId()
    {
        var run = Store().CreateRun("wf-original", "rev-1", handoff: Original(), admissionSourceScope: "bgi:local:9:900");
        Assert.Null(TaskCenterHost.ResolveAdmissionParent([], run, "different-run", run.WorkflowId));
    }

    [Fact]
    public void OrdinaryWriterCannotRewriteOriginalAdmissionScope()
    {
        var store = Store();
        var run = store.CreateRun("wf-original", "rev-1", handoff: Original(), admissionSourceScope: "bgi:local:9:900");
        var bytes = File.ReadAllBytes(Path.Combine(_root, "runs", run.RunId + ".run.json"));
        run.AdmissionSourceScope = "bgi:local:new-epoch";
        Assert.Throws<RunRecordConflictException>(() => store.Update(run));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_root, "runs", run.RunId + ".run.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryWriterCannotDeleteOrReplaceOriginalHandoffBinding(bool replace)
    {
        var store = Store();
        var run = store.CreateRun("wf-original", "rev-1", handoff: Original(), admissionSourceScope: "bgi:local:9:900");
        var bytes = File.ReadAllBytes(Path.Combine(_root, "runs", run.RunId + ".run.json"));
        if (replace) run.Handoffs[0].ExecutionId = "another-execution";
        else run.Handoffs.Clear();
        Assert.Throws<RunRecordConflictException>(() => store.Update(run));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_root, "runs", run.RunId + ".run.json")));
    }
}
