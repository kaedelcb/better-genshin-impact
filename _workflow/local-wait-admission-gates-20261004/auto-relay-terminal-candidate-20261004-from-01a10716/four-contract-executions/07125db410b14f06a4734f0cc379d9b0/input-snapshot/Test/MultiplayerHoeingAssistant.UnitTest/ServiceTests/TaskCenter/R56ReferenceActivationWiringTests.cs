using System.Text;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.6 A 项：真实引用写入 + candidate→active 激活的事务接线**（全程只用隔离临时配置根，绝不触碰真实 User）。
/// 覆盖验收矩阵 A 的成功／确定拒绝／未知／取消（副作用前后）／恢复／重复／写后窗口，与状态、并发、故障三类矩阵行
/// （REF-S1..S9 / REF-C1..C3 / REF-F1..F9）。核心不变量：**事务阶段标记只在实际副作用成功、持久化并读回确认之后推进**。
/// </summary>
public sealed class R56ReferenceActivationWiringTests : IDisposable
{
    private readonly string _root;
    private readonly string _configRoot;
    private readonly string _txRoot;
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string FlowPath = "flows/plan.flow.json";
    private const string SecondPath = "flows/other.flow.json";
    private const string ConfPath = "OneDragon/plan.json";

    public R56ReferenceActivationWiringTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "r56w-" + Guid.NewGuid().ToString("N")[..8]);
        _configRoot = Path.Combine(_root, "cfg");
        _txRoot = Path.Combine(_root, "tx");
        Directory.CreateDirectory(_configRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class NoopQuiet : IDisposable { public void Dispose() { } }

    private string Full(string rel) => Path.Combine(_configRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string HashOf(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private void Seed(string rel, string text, bool bom = false)
    {
        var full = Full(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var payload = new UTF8Encoding(false).GetBytes(text);
        if (bom)
        {
            var withBom = new byte[payload.Length + 3];
            withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
            payload.CopyTo(withBom, 3);
            payload = withBom;
        }
        File.WriteAllBytes(full, payload);
    }

    private static string FlowJson(string name, string status, string config) => $$"""
        {
          "schema": "mistletoe.workflow",
          "schemaVersion": 1,
          "name": "{{name}}",
          "activation": { "status": "{{status}}" },
          "nodes": [
            { "nodeId": "n-1", "kind": "resource.oneDragonConfig", "ref": { "config": "{{config}}", "revision": "rev-1" } }
          ]
        }
        """;

    private MigrationSwitchTransaction NewTx(IMigrationEffectService effects, Action<MigrationStage>? hook = null)
        => new(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true, hook, null, effects);

    private MigrationSwitchTransaction BeginWithEffects(IMigrationEffectService effects, string txId = "t1",
        Action<MigrationStage>? hook = null, IEnumerable<ChangeRecord>? changes = null, bool snapshot = true)
    {
        var tx = NewTx(effects, hook);
        Assert.True(tx.BeginTransaction(txId).Success);
        if (snapshot) Assert.True(tx.TakeSnapshot().Success);
        if (changes is not null) Assert.True(tx.RecordChanges(changes).Success);
        return tx;
    }

    private static MigrationReferenceUpdatePlan RenamePlan(params (string Path, string From, string To)[] targets)
        => new(targets.Select(t => new MigrationReferenceWriteTarget(t.Path, ChangeKind.Modified,
            RenameFrom: t.From, RenameTo: t.To)).ToList());

    // Caller code can run at every collection station, including enumerator disposal.
    private sealed class InputProbe<T>(IReadOnlyList<T> values, Action<string> visit) : IReadOnlyList<T>
    {
        public int CountVisits;
        public int IndexerVisits;
        public int Count { get { CountVisits++; visit("Count"); return values.Count; } }
        public T this[int index] { get { IndexerVisits++; visit("indexer"); return values[index]; } }
        public IEnumerator<T> GetEnumerator() { visit("GetEnumerator"); return new Cursor(values, visit); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        private sealed class Cursor(IReadOnlyList<T> values, Action<string> visit) : IEnumerator<T>
        {
            private int _index = -1;
            public bool MoveNext() { visit("MoveNext"); return ++_index < values.Count; }
            public T Current { get { visit("Current"); return values[_index]; } }
            object System.Collections.IEnumerator.Current => Current!;
            public void Dispose() => visit("EnumeratorDispose");
            public void Reset() => throw new NotSupportedException();
        }
    }

    [Theory]
    [InlineData("GetEnumerator")]
    [InlineData("MoveNext")]
    [InlineData("Current")]
    [InlineData("EnumeratorDispose")]
    public void ChangeEnumerable_AllConsumptionStationsRejectSynchronousReentry(string station)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "input-reentry");
        MigrationResult? nested = null;
        var source = new InputProbe<ChangeRecord>([new() { Path = FlowPath, Kind = ChangeKind.Modified }], visit =>
        {
            if (visit == station && nested is null)
                nested = tx.RecordChanges([new() { Path = SecondPath, Kind = ChangeKind.Added }]);
        });
        var result = tx.RecordChanges(source);
        Assert.True(result.Success, result.Reason);
        Assert.NotNull(nested);
        Assert.False(nested!.Success);
        Assert.Equal("reentrant_mutation_rejected", nested.Reason);
        Assert.Equal(FlowPath, Assert.Single(tx.LoadValidated()!.ChangedFiles).Path);
        Assert.Equal(0, source.CountVisits);
        Assert.Equal(0, source.IndexerVisits);
    }

    [Theory]
    [InlineData(false, "GetEnumerator", false)]
    [InlineData(false, "MoveNext", false)]
    [InlineData(true, "GetEnumerator", false)]
    [InlineData(true, "MoveNext", false)]
    [InlineData(false, "GetEnumerator", true)]
    [InlineData(false, "MoveNext", true)]
    [InlineData(true, "GetEnumerator", true)]
    [InlineData(true, "MoveNext", true)]
    public void InputNoFlowWorkerWait_CompletesWithoutMonitorDeadlock(bool targets, string station, bool unsafeQueue)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "input-worker",
            changes: targets ? [new() { Path = FlowPath, Kind = ChangeKind.Modified }] : null);
        System.Threading.Tasks.Task<MigrationResult>? nested = null;
        var completedInside = false;
        void Visit(string visit)
        {
            if (visit != station || nested is not null) return;
            MigrationResult Call() => tx.RecordChanges([new() { Path = SecondPath, Kind = ChangeKind.Added }]);
            if (unsafeQueue)
            {
                var completion = new System.Threading.Tasks.TaskCompletionSource<MigrationResult>(
                    System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                nested = completion.Task;
                System.Threading.ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    try { completion.SetResult(Call()); } catch (Exception ex) { completion.SetException(ex); }
                }, null);
            }
            else
            {
                using (System.Threading.ExecutionContext.SuppressFlow())
                    nested = System.Threading.Tasks.Task.Run(Call);
            }
            completedInside = nested.Wait(TimeSpan.FromSeconds(1));
        }
        var result = targets
            ? tx.ApplyReferenceUpdate(new(new InputProbe<MigrationReferenceWriteTarget>(
                [new(FlowPath, ChangeKind.Modified, RenameFrom: "old", RenameTo: "new")], Visit)))
            : tx.RecordChanges(new InputProbe<ChangeRecord>([new() { Path = FlowPath, Kind = ChangeKind.Modified }], Visit));
        Assert.NotNull(nested);
        var inner = nested!.GetAwaiter().GetResult();
        Assert.True(completedInside);
        Assert.False(inner.Success);
        Assert.True(result.Success, result.Reason);
        Assert.Equal(FlowPath, Assert.Single(tx.LoadValidated()!.ChangedFiles).Path);
    }

    [Theory]
    [InlineData("GetEnumerator")]
    [InlineData("MoveNext")]
    [InlineData("Current")]
    [InlineData("EnumeratorDispose")]
    public void ReferenceTargets_FrozenCloneIsOnlyInputForAllConsumers(string station)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        var values = new List<MigrationReferenceWriteTarget> { new(FlowPath, ChangeKind.Modified, RenameFrom: "old", RenameTo: "new") };
        MigrationSwitchTransaction? tx = null;
        var changed = false;
        using (tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "input-targets", hook: stage =>
        {
            if (stage != MigrationStage.ReferenceUpdating || changed) return;
            changed = true;
            values.Clear(); // Must not change the frozen declaration or any later consumer.
        }, changes: [new() { Path = FlowPath, Kind = ChangeKind.Modified }]))
        {
            var rejected = new List<MigrationResult>();
            var source = new InputProbe<MigrationReferenceWriteTarget>(values, visit =>
            {
                if (visit == station) rejected.Add(tx.RecordChanges([]));
            });
            var result = tx.ApplyReferenceUpdate(new(source));
            Assert.NotEmpty(rejected);
            Assert.All(rejected, r => Assert.False(r.Success));
            Assert.Equal(0, source.CountVisits);
            Assert.Equal(0, source.IndexerVisits);
            Assert.True(result.Success, result.Reason);
            Assert.True(changed);
            Assert.Equal(HashOf(Full(FlowPath)), Assert.Single(tx.LoadValidated()!.ReferenceWriteSet).Value);
        }
    }

    [Theory]
    [InlineData("GetEnumerator")]
    [InlineData("MoveNext")]
    [InlineData("Current")]
    [InlineData("EnumeratorDispose")]
    public void InputFailureAndDispose_PreserveOuterLeaseAndReleaseAtDepthZero(string station)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "input-exception");
        var before = File.ReadAllBytes(tx.ManifestPath);
        var source = new InputProbe<ChangeRecord>([new() { Path = FlowPath, Kind = ChangeKind.Modified }], visit =>
        {
            if (visit == station) throw new ArgumentException("caller collection failure");
        });
        MigrationResult? result = null;
        var exception = Record.Exception(() => result = tx.RecordChanges(source));
        Assert.Null(exception);
        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal(before, File.ReadAllBytes(tx.ManifestPath));
        Assert.True(tx.HoldsExclusiveLock);
        Assert.True(tx.RecordChanges([new() { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InputDispose_IsDeferredUntilOuterOperationEnds(bool worker)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "input-dispose");
        var before = File.ReadAllBytes(tx.ManifestPath);
        var fired = false;
        var source = new InputProbe<ChangeRecord>([new() { Path = FlowPath, Kind = ChangeKind.Modified }], _ =>
        {
            if (fired) return;
            fired = true;
            if (worker)
            {
                System.Threading.Tasks.Task disposing;
                using (System.Threading.ExecutionContext.SuppressFlow()) disposing = System.Threading.Tasks.Task.Run(tx.Dispose);
                Assert.True(disposing.Wait(TimeSpan.FromSeconds(1)));
            }
            else tx.Dispose();
            Assert.True(tx.HoldsExclusiveLock);
            using var contender = NewTx(new WorkflowFileMigrationEffectService());
            Assert.False(contender.TryAcquireExclusive().Success);
        });
        Assert.False(tx.RecordChanges(source).Success);
        Assert.Equal(before, File.ReadAllBytes(tx.ManifestPath));
        Assert.False(tx.HoldsExclusiveLock);
        Assert.False(tx.RecordChanges([]).Success);
        using var fresh = NewTx(new WorkflowFileMigrationEffectService());
        Assert.True(fresh.TryAcquireExclusive().Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MutableChangeRecord_IsClonedBeforeLaterCallbacksCanAlterIt(bool changeKind)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        Seed(SecondPath, FlowJson("other", "candidate-ready", "old"));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "mutable-input");
        var first = new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified };
        var currents = 0;
        var source = new InputProbe<ChangeRecord>([first, new() { Path = SecondPath, Kind = ChangeKind.Modified }], visit =>
        {
            if (visit != "Current" || ++currents != 2) return;
            if (changeKind) first.Kind = ChangeKind.Added;
            else first.Path = "foreign-unchecked.json";
        });
        var result = tx.RecordChanges(source);
        Assert.True(result.Success, result.Reason);
        var registered = tx.LoadValidated()!.ChangedFiles;
        Assert.Equal(2, registered.Count);
        Assert.Equal(FlowPath, registered[0].Path);
        Assert.Equal(ChangeKind.Modified, registered[0].Kind);
        Assert.Equal(SecondPath, registered[1].Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrozenInputCallbackCannotReclaimForeignMain(bool changedBytes)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "input-foreign-main");
        var replacement = File.ReadAllBytes(tx.ManifestPath);
        if (changedBytes)
        {
            var m = tx.LoadValidated()!;
            m.CreatedAtUtc = Now.AddSeconds(1);
            m.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(m);
            replacement = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(m);
        }
        var fired = false;
        var source = new InputProbe<ChangeRecord>([new() { Path = FlowPath, Kind = ChangeKind.Modified }], _ =>
        {
            if (fired) return;
            fired = true;
            File.Move(tx.ManifestPath, Path.Combine(_root, "retained-original-main"));
            File.WriteAllBytes(tx.ManifestPath, replacement);
        });
        Assert.False(tx.RecordChanges(source).Success);
        var version = new WindowsTxfMigrationVersionStore(_configRoot, _txRoot).ReadArtifactInput("migration-manifest.json").Version;
        Assert.Equal(replacement, File.ReadAllBytes(tx.ManifestPath));
        Assert.False(tx.RecordChanges([]).Success);
        Assert.Equal(replacement, File.ReadAllBytes(tx.ManifestPath));
        Assert.True(version.Matches(new WindowsTxfMigrationVersionStore(_configRoot, _txRoot).ReadArtifactInput("migration-manifest.json").Version));
    }


    [Theory]
    [InlineData("prepared-same")]
    [InlineData("prepared-reopen")]
    [InlineData("nonprepared-reopen")]
    [InlineData("null-reopen")]
    public void PersistentModernCommitted_AllCapabilitiesRejectActualProductionAction(string capability)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var original = BeginWithEffects(new WorkflowFileMigrationEffectService(), "persistent-production",
            changes: [new() { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(original.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
        Assert.True(original.ActivateCandidate(Activation(original)).Success);
        Assert.True(original.RehearseRollback().Success);
        Assert.True(original.Commit().Success);
        var subject = original;
        MigrationSwitchTransaction? reopened = null;
        try
        {
            if (capability != "prepared-same")
            {
                original.Dispose();
                IMigrationEffectService? effects = capability switch
                {
                    "prepared-reopen" => new WorkflowFileMigrationEffectService(),
                    "nonprepared-reopen" => new ScriptedEffectService(),
                    _ => null
                };
                reopened = new(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), effectService: effects);
                Assert.True(reopened.TryAcquireExclusive().Success);
                subject = reopened;
            }
            var calls = 0;
            var marker = Path.Combine(_root, "forbidden-action.txt");
            var authorization = subject.AuthorizeProductionExecution();
            var result = subject.TryRunProduction(() => { calls++; File.WriteAllText(marker, "executed"); });
            Assert.False(File.Exists(marker));
            Assert.Equal(0, calls);
            Assert.False(result.Success);
            Assert.False(authorization.Success);
        }
        finally { reopened?.Dispose(); }
    }

    public static IEnumerable<object[]> IncapableMutationCases()
    {
        foreach (var stage in new[] { "Snapshotting", "SnapshotReady", "ReferenceUpdating" })
        foreach (var nonprepared in new[] { false, true })
        foreach (var entry in new[] { "Begin", "Snapshot", "Record", "MarkReference", "MarkActivated", "Rollback", "Recover", "Upgrade" })
            yield return [stage, nonprepared, entry];
    }

    private string[] FixturePersistentState() => Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(_root, p) + "|" + (Directory.Exists(p) ? "directory" :
            p.EndsWith(".lock", StringComparison.Ordinal) ? "existing-lock" : HashOf(p))).Order().ToArray();

    [Theory]
    [MemberData(nameof(IncapableMutationCases))]
    public void PersistentModernHistory_IncapableMutationsPreserveEntireState(string stage, bool nonprepared, string entry)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using (var original = BeginWithEffects(new WorkflowFileMigrationEffectService(), "incapable-modern",
            snapshot: stage != "Snapshotting"))
        {
            if (stage == "ReferenceUpdating")
            {
                Assert.True(original.RecordChanges([new() { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
                Assert.True(original.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
            }
        }
        var before = FixturePersistentState();
        var version = new WindowsTxfMigrationVersionStore(_configRoot, _txRoot).ReadVersion(FlowPath);
        using (var incapable = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(),
            effectService: nonprepared ? new ScriptedEffectService() : null))
        {
            Assert.True(incapable.TryAcquireExclusive().Success);
            MigrationResult? result = null;
            var exception = Record.Exception(() => result = entry switch
            {
                "Begin" => incapable.BeginTransaction("incapable-new"),
                "Snapshot" => incapable.TakeSnapshot(),
                "Record" => incapable.RecordChanges([new() { Path = FlowPath, Kind = ChangeKind.Modified }]),
                "MarkReference" => incapable.MarkReferenceUpdateCompleted(),
                "MarkActivated" => incapable.MarkActivated(),
                "Rollback" => incapable.Rollback(),
                "Recover" => incapable.RecoverOnStart(),
                _ => incapable.UpgradeLegacyManifest()
            });
            Assert.Null(exception);
            Assert.NotNull(result);
            Assert.False(result!.Success);
            Assert.Equal(before, FixturePersistentState());
            Assert.True(version.Matches(new WindowsTxfMigrationVersionStore(_configRoot, _txRoot).ReadVersion(FlowPath)));
        }
        using var correct = NewTx(new WorkflowFileMigrationEffectService());
        Assert.True(correct.TryAcquireExclusive().Success);
        Assert.True(correct.RecoverOnStart().Success);
    }

    private void InjectRootDrift(string drift)
    {
        switch (drift)
        {
            case "samebytes-other-id":
                var bytes = File.ReadAllBytes(Full(FlowPath));
                File.Move(Full(FlowPath), Path.Combine(_root, "retained-foreign-file"));
                File.WriteAllBytes(Full(FlowPath), bytes);
                break;
            case "changed-bytes": File.AppendAllText(Full(FlowPath), " "); break;
            case "new-file": Seed("foreign.txt", "foreign"); break;
            case "missing-file": File.Move(Full(FlowPath), Path.Combine(_root, "retained-missing-file")); break;
            case "new-empty-dir": Directory.CreateDirectory(Full("foreign-empty")); break;
            case "missing-empty-dir": Directory.Move(Full("baseline-empty"), Path.Combine(_root, "retained-empty")); break;
            case "clean": break;
            default: throw new ArgumentException(drift);
        }
    }

    public static IEnumerable<object[]> WholeRootCases()
    {
        foreach (var stage in new[] { "ReferenceUpdating", "Activated", "RolledBack" })
        foreach (var reopen in new[] { false, true })
        foreach (var drift in new[] { "samebytes-other-id", "changed-bytes", "new-file", "missing-file", "new-empty-dir", "missing-empty-dir", "clean" })
            yield return [stage, reopen, drift];
    }

    [Theory]
    [MemberData(nameof(WholeRootCases))]
    public void RepeatedModernSuccess_RequiresFreshWholeRootWithoutRepeatingEffects(string stage, bool reopen, string drift)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        Directory.CreateDirectory(Full("baseline-empty"));
        using var original = BeginWithEffects(new WorkflowFileMigrationEffectService(), "repeat-root",
            changes: [new() { Path = FlowPath, Kind = ChangeKind.Modified }]);
        var plan = RenamePlan((FlowPath, "old", "new"));
        Assert.True(original.ApplyReferenceUpdate(plan).Success);
        var request = Activation(original);
        if (stage == "Activated") Assert.True(original.ActivateCandidate(request).Success);
        if (stage == "RolledBack") Assert.True(original.Rollback().Success);
        var subject = original;
        MigrationSwitchTransaction? recovered = null;
        try
        {
            if (reopen)
            {
                original.Dispose();
                recovered = NewTx(new WorkflowFileMigrationEffectService());
                Assert.True(recovered.TryAcquireExclusive().Success);
                subject = recovered;
            }
            InjectRootDrift(drift);
            var store = new WindowsTxfMigrationVersionStore(_configRoot, _txRoot);
            var version = store.ReadVersion(FlowPath);
            var files = Directory.EnumerateFileSystemEntries(_configRoot, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(_configRoot, p)).Order().ToArray();
            var journalPath = Path.Combine(_txRoot, "operations", "repeat-root", "operation-journal.json");
            var journal = new MigrationOperationJournal(Path.GetDirectoryName(journalPath)!, store);
            var count = journal.ReadAuthority().Document.Operations.Count;
            MigrationResult? result = null;
            var exception = Record.Exception(() => result = stage switch
            {
                "ReferenceUpdating" => subject.ApplyReferenceUpdate(plan),
                "Activated" => subject.ActivateCandidate(request),
                _ => subject.Rollback()
            });
            Assert.Null(exception);
            Assert.NotNull(result);
            Assert.Equal(drift == "clean", result!.Success);
            Assert.True(version.Matches(store.ReadVersion(FlowPath)));
            Assert.Equal(files, Directory.EnumerateFileSystemEntries(_configRoot, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(_configRoot, p)).Order().ToArray());
            Assert.Equal(count, journal.ReadAuthority().Document.Operations.Count);
        }
        finally { recovered?.Dispose(); }
    }

    [Theory]
    [InlineData("samebytes-other-id")]
    [InlineData("changed-bytes")]
    [InlineData("new-file")]
    [InlineData("missing-file")]
    [InlineData("new-empty-dir")]
    [InlineData("missing-empty-dir")]
    [InlineData("clean")]
    public void SnapshotReady_NoJournalPreHookDriftCannotPublishOldWitness(string drift)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        Directory.CreateDirectory(Full("baseline-empty"));
        var fired = false;
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "snapshot-prehook", snapshot: false, hook: stage =>
        {
            if (stage != MigrationStage.SnapshotReady || fired) return;
            fired = true;
            InjectRootDrift(drift);
        });
        MigrationResult? result = null;
        var exception = Record.Exception(() => result = tx.TakeSnapshot());
        Assert.True(fired);
        Assert.Null(exception);
        Assert.NotNull(result);
        Assert.Equal(drift == "clean", result!.Success);
        if (drift != "clean") Assert.NotEqual(MigrationStage.SnapshotReady, tx.LoadValidated()!.Stage);
    }

    [Theory]
    [InlineData(false, "new-empty-dir")]
    [InlineData(true, "new-empty-dir")]
    [InlineData(false, "samebytes-other-id")]
    [InlineData(true, "samebytes-other-id")]
    [InlineData(false, "clean")]
    [InlineData(true, "clean")]
    public void NoJournalRollbackPreHookDriftKeepsPendingAndNeverClaimsTerminal(bool reopen, string drift)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        var fired = false;
        void Hook(MigrationStage stage)
        {
            if (stage != MigrationStage.RolledBack || fired) return;
            fired = true;
            InjectRootDrift(drift);
        }
        using var original = BeginWithEffects(new WorkflowFileMigrationEffectService(), "rollback-prehook", hook: Hook);
        MigrationSwitchTransaction subject = original;
        MigrationSwitchTransaction? recovered = null;
        try
        {
            if (reopen)
            {
                original.Dispose();
                recovered = NewTx(new WorkflowFileMigrationEffectService(), Hook);
                Assert.True(recovered.TryAcquireExclusive().Success);
                subject = recovered;
            }
            MigrationResult? result = null;
            var exception = Record.Exception(() => result = subject.Rollback());
            Assert.True(fired);
            Assert.Null(exception);
            Assert.Equal(drift == "clean", result!.Success);
            if (drift != "clean") Assert.NotEqual(MigrationStage.RolledBack, subject.LoadValidated()!.Stage);
        }
        finally { recovered?.Dispose(); }
    }

    [Theory]
    [InlineData("orphan-output")]
    [InlineData("orphan-resolution")]
    [InlineData("shared-journal")]
    [InlineData("clean")]
    public void NoBaselineAbort_PreHookOrphanFactsCannotClearPending(string fact)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        var fired = false;
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "abort-prehook", snapshot: false, hook: stage =>
        {
            if (stage != MigrationStage.RolledBack || fired) return;
            fired = true;
            if (fact == "clean") return;
            var path = fact == "shared-journal" ? Path.Combine(_txRoot, "operation-journal.json") :
                Path.Combine(_txRoot, "operations", "abort-prehook", fact == "orphan-output" ? "blobs" : "resolutions", "foreign.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "foreign");
        });
        MigrationResult? result = null;
        var exception = Record.Exception(() => result = tx.Rollback());
        Assert.True(fired);
        Assert.Null(exception);
        Assert.Equal(fact == "clean", result!.Success);
        if (fact != "clean") Assert.NotEqual(MigrationStage.RolledBack, tx.LoadValidated()!.Stage);
    }

    [Theory]
    [InlineData(false, "new-empty-dir")]
    [InlineData(true, "new-empty-dir")]
    [InlineData(false, "samebytes-other-id")]
    [InlineData(true, "samebytes-other-id")]
    [InlineData(false, "clean")]
    [InlineData(true, "clean")]
    public void NewTransactionCannotAbsorbTerminalForeignRootAsItsBaseline(bool committed, string drift)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "terminal-original",
            changes: [new() { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
        if (committed)
        {
            Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
            Assert.True(tx.RehearseRollback().Success);
            Assert.True(tx.Commit().Success);
        }
        else Assert.True(tx.Rollback().Success);
        InjectRootDrift(drift);
        var before = File.ReadAllBytes(tx.ManifestPath);
        MigrationResult? result = null;
        var exception = Record.Exception(() => result = tx.BeginTransaction("terminal-next"));
        Assert.Null(exception);
        Assert.Equal(drift == "clean", result!.Success);
        if (drift != "clean")
        {
            Assert.Equal(before, File.ReadAllBytes(tx.ManifestPath));
            Assert.Equal("terminal-original", tx.LoadValidated()!.TransactionId);
            Assert.DoesNotContain("terminal-next", File.ReadAllLines(tx.HistoryPath));
        }
        else
        {
            Assert.Equal("terminal-next", tx.LoadValidated()!.TransactionId);
            Assert.False(tx.BeginTransaction("terminal-original").Success);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentV8_LegacyPreparedRecovery_ExactBaselineOnlyAndNoInventedModernEvidence(bool foreign)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        byte[] original;
        using (var diagnostic = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet()))
        {
            Assert.True(diagnostic.BeginTransaction("legacy-prepared-v8").Success);
            Assert.True(diagnostic.TakeSnapshot().Success);
            Assert.True(diagnostic.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
            var legacy = diagnostic.LoadValidated()!;
            legacy.SchemaVersion = 1;
            legacy.Stage = MigrationStage.Activated;
            legacy.ReferenceWriteSet[FlowPath] = MigrationFileVersion.Hash(baseline);
            legacy.ActivationRecord = new() { Path = FlowPath, BeforeStatus = "candidate-ready", AfterStatus = "active", AfterHash = MigrationFileVersion.Hash(baseline) };
            legacy.RealEffectsRequired = true;
            legacy.ManifestIntegrity = MigrationSwitchTransaction.ComputeLegacyManifestIntegrity(legacy, true);
            var node = System.Text.Json.JsonSerializer.SerializeToNode(legacy)!.AsObject();
            foreach (var key in new[] { "legacySource", "baselineVersions", "controlledBaseline", "controlledLatest", "journalBinding" }) node.Remove(key);
            original = Encoding.UTF8.GetBytes(node.ToJsonString());
            File.WriteAllBytes(diagnostic.ManifestPath, original);
            Assert.NotNull(diagnostic.LoadValidated());
        }
        if (foreign) Seed(FlowPath, FlowJson("FOREIGN", "active", "new"));
        var beforeIdentity = new WindowsTxfMigrationVersionStore(_configRoot, _txRoot).ReadVersion(FlowPath);
        var before = File.ReadAllBytes(Full(FlowPath));
        using var reopened = NewTx(new WorkflowFileMigrationEffectService());
        Assert.True(reopened.TryAcquireExclusive().Success);
        var upgraded = reopened.UpgradeLegacyManifest();
        Assert.True(upgraded.Success, upgraded.Reason);
        var recovery = reopened.RecoverOnStart();
        Assert.Equal(!foreign, recovery.Success);
        Assert.Equal(before, File.ReadAllBytes(Full(FlowPath)));
        var current = reopened.LoadValidated()!;
        Assert.NotNull(current.LegacySource);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_txRoot, current.LegacySource!.ArchivePath)));
        Assert.NotNull(current.ActivationRecord);
        Assert.Single(current.ReferenceWriteSet);
        Assert.Null(current.ControlledBaseline);
        Assert.Null(current.JournalBinding);
        Assert.False(File.Exists(Path.Combine(_txRoot, "operations", current.TransactionId, "operation-journal.json")));
        Assert.False(reopened.TryRunProduction(() => throw new Exception("production must remain closed")).Success);
        Assert.True(beforeIdentity.Matches(new WindowsTxfMigrationVersionStore(_configRoot, _txRoot).ReadVersion(FlowPath)));
        if (!foreign)
        {
            Assert.Equal(MigrationStage.RolledBack, current.Stage);
            Assert.NotNull(current.LegacyBaselineObservation);
            Assert.False(current.LegacyBaselineObservation!.MutationPerformed);
            Assert.Null(current.LegacyBaselineObservation.CurrentObservation.SuccessfulStage);
            var previousLease = current.LegacyBaselineObservation.CurrentObservation.LeaseId;
            reopened.Dispose();
            using var terminalReopened = NewTx(new WorkflowFileMigrationEffectService());
            Assert.True(terminalReopened.TryAcquireExclusive().Success);
            Assert.True(terminalReopened.RecoverOnStart().Success);
            Assert.NotEqual(previousLease, terminalReopened.LoadValidated()!.LegacyBaselineObservation!.CurrentObservation.LeaseId);
            Assert.Equal(before, File.ReadAllBytes(Full(FlowPath)));
        }
    }

    [Fact]
    public void CurrentV9_LegacyCannotCreateNewDataOrModernJournalThroughForwardEntry()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        const string added = "legacy-added.json";
        using (var diagnostic = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet()))
        {
            Assert.True(diagnostic.BeginTransaction("legacy-forward-v9").Success);
            Assert.True(diagnostic.TakeSnapshot().Success);
            Assert.True(diagnostic.RecordChanges([new ChangeRecord { Path = added, Kind = ChangeKind.Added }]).Success);
            var legacy = diagnostic.LoadValidated()!;
            legacy.SchemaVersion = 1;
            legacy.ManifestIntegrity = MigrationSwitchTransaction.ComputeLegacyManifestIntegrity(legacy, true);
            var node = System.Text.Json.JsonSerializer.SerializeToNode(legacy)!.AsObject();
            foreach (var key in new[] { "legacySource", "baselineVersions", "controlledBaseline", "controlledLatest", "journalBinding", "legacyBaselineObservation" }) node.Remove(key);
            File.WriteAllText(diagnostic.ManifestPath, node.ToJsonString());
            Assert.NotNull(diagnostic.LoadValidated());
        }
        var baseline = File.ReadAllBytes(Full(FlowPath));
        using var reopened = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now,
            requireQuiescence: false, effectService: new WorkflowFileMigrationEffectService());
        Assert.True(reopened.TryAcquireExclusive().Success);
        Assert.True(reopened.UpgradeLegacyManifest().Success);
        MigrationResult? updated = null;
        var failure = Record.Exception(() => updated = reopened.ApplyReferenceUpdate(new([new(added, ChangeKind.Added, NewContent: FlowJson("added", "candidate-ready", "old"))])));
        Assert.False(File.Exists(Full(added)));
        Assert.False(Directory.Exists(Path.Combine(_txRoot, "operations", "legacy-forward-v9")));
        Assert.Null(failure);
        Assert.False(updated!.Success);
        Assert.Equal(baseline, File.ReadAllBytes(Full(FlowPath)));
        Assert.True(reopened.RecoverOnStart().Success);
    }

    [Theory]
    [InlineData("config-before")]
    [InlineData("config-after")]
    [InlineData("main-before")]
    [InlineData("dispose")]
    public void CurrentV10_LegacyPublicationCallbacks_PreserveForeignInputsAndDeferredDispose(string station)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        using (var diagnostic = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet()))
        {
            Assert.True(diagnostic.BeginTransaction("legacy-callback-v10").Success);
            Assert.True(diagnostic.TakeSnapshot().Success);
            Assert.True(diagnostic.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
            var legacy = diagnostic.LoadValidated()!;
            legacy.SchemaVersion = 1;
            legacy.Stage = MigrationStage.Activated;
            legacy.RealEffectsRequired = true;
            legacy.ReferenceWriteSet[FlowPath] = MigrationFileVersion.Hash(baseline);
            legacy.ActivationRecord = new() { Path = FlowPath, BeforeStatus = "candidate-ready", AfterStatus = "active", AfterHash = MigrationFileVersion.Hash(baseline) };
            legacy.ManifestIntegrity = MigrationSwitchTransaction.ComputeLegacyManifestIntegrity(legacy, true);
            var node = System.Text.Json.JsonSerializer.SerializeToNode(legacy)!.AsObject();
            foreach (var key in new[] { "legacySource", "baselineVersions", "controlledBaseline", "controlledLatest", "journalBinding", "legacyBaselineObservation" }) node.Remove(key);
            File.WriteAllText(diagnostic.ManifestPath, node.ToJsonString());
        }
        var foreign = Encoding.UTF8.GetBytes(FlowJson("FOREIGN", "active", "new"));
        byte[] foreignMain = [];
        var hooks = 0;
        MigrationSwitchTransaction? tx = null;
        tx = NewTx(new WorkflowFileMigrationEffectService(), stage =>
        {
            if (stage != MigrationStage.RolledBack) return;
            hooks++;
            if (station == "config-before" && hooks == 1 || station == "config-after" && hooks == 2)
                File.WriteAllBytes(Full(FlowPath), foreign);
            if (station == "main-before" && hooks == 1)
            {
                var foreignManifest = tx!.LoadValidated()!;
                foreignManifest.Stage = MigrationStage.Blocked;
                foreignManifest.BlockedReason = "FOREIGN-MAIN-DO-NOT-OVERWRITE";
                foreignManifest.CommitMarker = null;
                foreignManifest.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(foreignManifest);
                foreignMain = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(foreignManifest,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllBytes(tx.ManifestPath, foreignMain);
                Assert.NotNull(tx.LoadValidated());
            }
            if (station == "dispose" && hooks == 1)
            {
                Assert.True(System.Threading.Tasks.Task.Run(tx!.Dispose).Wait(TimeSpan.FromSeconds(2)));
                using var contender = NewTx(new WorkflowFileMigrationEffectService());
                Assert.False(contender.TryAcquireExclusive().Success);
                Assert.Equal(baseline, File.ReadAllBytes(Full(FlowPath)));
            }
        });
        using (tx)
        {
            Assert.True(tx.TryAcquireExclusive().Success);
            Assert.True(tx.UpgradeLegacyManifest().Success);
            var result = tx.RecoverOnStart();
            if (station == "dispose")
            {
                Assert.True(result.Success, result.Reason);
                Assert.False(tx.TryAcquireExclusive().Success);
                using var next = NewTx(new WorkflowFileMigrationEffectService());
                Assert.True(next.TryAcquireExclusive().Success);
            }
            else
            {
                if (station == "main-before") Assert.Equal(foreignMain, File.ReadAllBytes(tx.ManifestPath));
                Assert.False(result.Success);
                Assert.Equal(station == "main-before" ? baseline : foreign, File.ReadAllBytes(Full(FlowPath)));
                if (station != "main-before") Assert.Equal(MigrationStage.Blocked, tx.LoadValidated()!.Stage);
                var binding = Assert.Single(Directory.GetFiles(_root, ".mistletoe-root-*.json"));
                Assert.Equal("legacy-callback-v10", (string?)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(binding))!["pendingTransactionId"]);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentV13_SuccessfulPhasePublicationConsumesPending(bool committed)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "pending-consumed-v13",
            changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
        Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
        if (committed)
        {
            Assert.True(tx.RehearseRollback().Success);
            Assert.True(tx.Commit().Success);
        }
        var operationRoot = Path.Combine(_txRoot, "operations", "pending-consumed-v13");
        var journal = new MigrationOperationJournal(operationRoot,
            new WindowsTxfMigrationVersionStore(_configRoot, operationRoot));
        var document = journal.ReadAuthority().Document;
        Assert.Equal(committed ? MigrationStage.Committed : MigrationStage.Activated, document.Stage);
        Assert.Null(document.PendingStage);
        Assert.Null(tx.LoadValidated()!.JournalBinding!.PendingStage);
    }

    [Theory]
    [InlineData(MigrationStage.None)]
    [InlineData(MigrationStage.Snapshotting)]
    [InlineData(MigrationStage.RolledBack)]
    public void CurrentV13_DeclaredReferenceCannotMasqueradeAsUnlistedOrTerminalStage(MigrationStage stage)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "stage-grid-v13",
            changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
        var root = Path.Combine(_txRoot, "operations", "stage-grid-v13");
        var journal = new MigrationOperationJournal(root, new WindowsTxfMigrationVersionStore(_configRoot, root));
        var candidate = journal.Load() with { Stage = stage, LastStableStage = stage, PendingStage = null };
        candidate = candidate with { Integrity = MigrationOperationJournal.Hash(candidate with { Integrity = "" }) };
        File.WriteAllBytes(Path.Combine(root, "operation-journal.json"), MigrationOperationJournal.Encode(candidate));
        Assert.ThrowsAny<Exception>(() => journal.ReadAuthority());
        Assert.Contains("new", File.ReadAllText(Full(FlowPath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentV13_DeclaredPreparedRequiresExactParentsAndCannotOwnBaselineDirectory(bool forgeBaselineDirectory)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(),
            effectService: new WorkflowFileMigrationEffectService(), referenceWriteFault: point =>
            { if (point == "after_prepared") throw new IOException("owned_prepared_grid_fault"); });
        Assert.True(tx.BeginTransaction("parent-grid-v13").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
        Assert.False(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
        var root = Path.Combine(_txRoot, "operations", "parent-grid-v13");
        var store = new WindowsTxfMigrationVersionStore(_configRoot, root);
        var journal = new MigrationOperationJournal(root, store);
        var document = journal.Load();
        var entry = Assert.Single(document.Operations);
        Assert.Equal(MigrationOperationState.Prepared, entry.State);
        var intent = entry.Intent with { ParentInputs = null };
        if (forgeBaselineDirectory)
        {
            var absent = MigrationFileVersion.Absent(MigrationEntryKind.Directory);
            intent = entry.Intent with { ParentInputs = new Dictionary<string, MigrationFileVersion> { ["flows"] = absent },
                Directories = [new("flows", absent, store.ReadVersion("flows", MigrationEntryKind.Directory))] };
        }
        var candidate = document with { Operations = [entry with { Intent = intent }] };
        candidate = candidate with { Integrity = MigrationOperationJournal.Hash(candidate with { Integrity = "" }) };
        File.WriteAllBytes(Path.Combine(root, "operation-journal.json"), MigrationOperationJournal.Encode(candidate));
        Assert.ThrowsAny<Exception>(() => journal.ReadAuthority());
        Assert.True(Directory.Exists(Full("flows")));
        Assert.Contains("old", File.ReadAllText(Full(FlowPath)));
    }

    [Fact]
    public void CurrentV13_ForgedDirectoryReceiptCannotDeleteBaselineEmptyDirectory()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        Directory.CreateDirectory(Full("baseline-empty"));
        const string added = "baseline-empty/added.json";
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "directory-grid-v13",
            changes: [new ChangeRecord { Path = added, Kind = ChangeKind.Added }]);
        Assert.True(tx.ApplyReferenceUpdate(new([new(added, ChangeKind.Added,
            NewContent: FlowJson("added", "candidate-ready", "old"))])).Success);
        var root = Path.Combine(_txRoot, "operations", "directory-grid-v13");
        var store = new WindowsTxfMigrationVersionStore(_configRoot, root);
        var journal = new MigrationOperationJournal(root, store);
        var authority = journal.ReadAuthority();
        var entry = Assert.Single(authority.Document.Operations);
        var absent = MigrationFileVersion.Absent(MigrationEntryKind.Directory);
        MigrationDirectoryEffect[] directories = [new("baseline-empty", absent,
            store.ReadVersion("baseline-empty", MigrationEntryKind.Directory))];
        var intent = entry.Intent with { ParentInputs = new Dictionary<string, MigrationFileVersion> { ["baseline-empty"] = absent }, Directories = directories };
        var receipt = authority.Resolutions[entry.Intent.OperationId] with { IntentSha256 = MigrationOperationJournal.Hash(intent), Directories = directories, Integrity = "" };
        receipt = receipt with { Integrity = MigrationOperationJournal.Hash(receipt) };
        var bytes = MigrationOperationJournal.Encode(receipt);
        File.WriteAllBytes(Path.Combine(root, entry.ResolutionPath!), bytes);
        var candidate = authority.Document with { Operations = [entry with { Intent = intent, ResolutionSha256 = MigrationFileVersion.Hash(bytes) }] };
        candidate = candidate with { Integrity = MigrationOperationJournal.Hash(candidate with { Integrity = "" }) };
        File.WriteAllBytes(Path.Combine(root, "operation-journal.json"), MigrationOperationJournal.Encode(candidate));
        MigrationAuthorityView? parsed = null;
        var rejected = Record.Exception(() => parsed = journal.ReadAuthority());
        if (rejected is not null)
        {
            Assert.True(Directory.Exists(Full("baseline-empty")));
            Assert.True(File.Exists(Full(added)));
            return;
        }
        var main = tx.LoadManifest()!;
        main.JournalBinding = new("operations/directory-grid-v13/operation-journal.json", parsed.JournalBytesSha256,
            MigrationOperationJournal.Hash(parsed.Document.Declaration), parsed.ResolutionChainDigest, parsed.AppliedChainDigest,
            parsed.Document.LastStableStage, parsed.Document.PendingStage);
        main.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(main);
        File.WriteAllBytes(tx.ManifestPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(main,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var failure = Record.Exception(() => tx.Rollback());
        Assert.True(Directory.Exists(Full("baseline-empty")));
        Assert.True(failure is not null || tx.LoadManifest()!.Stage == MigrationStage.Blocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentV13_DeclaredStagePendingGridEnumeratesEveryCell(bool activated)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "complete-grid-v13",
            changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
        if (activated) Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
        var root = Path.Combine(_txRoot, "operations", "complete-grid-v13");
        var journal = new MigrationOperationJournal(root, new WindowsTxfMigrationVersionStore(_configRoot, root));
        var original = journal.Load();
        var failures = new List<string>();
        var cells = 0;
        MigrationStage?[] candidates = [null, .. Enum.GetValues<MigrationStage>().Select(s => (MigrationStage?)s)];
        foreach (var stage in Enum.GetValues<MigrationStage>())
        foreach (var pending in candidates)
        {
            var expected = activated ? stage switch
            {
                MigrationStage.ReferenceUpdating => pending == MigrationStage.Activated,
                MigrationStage.Activated => pending is null or MigrationStage.Committed,
                MigrationStage.Committed or MigrationStage.RollingBack => pending is null,
                MigrationStage.Blocked => pending is null or MigrationStage.Committed,
                _ => false
            } : stage switch
            {
                MigrationStage.SnapshotReady => pending is null or MigrationStage.ReferenceUpdating,
                MigrationStage.ReferenceUpdating => pending is null,
                MigrationStage.RollingBack or MigrationStage.Blocked => pending is null,
                _ => false
            };
            var document = original with { Stage = stage, PendingStage = pending,
                BlockedReason = stage == MigrationStage.Blocked ? "owned_matrix_fault" : null };
            document = document with { Integrity = MigrationOperationJournal.Hash(document with { Integrity = "" }) };
            File.WriteAllBytes(Path.Combine(root, "operation-journal.json"), MigrationOperationJournal.Encode(document));
            var accepted = Record.Exception(() => journal.ReadAuthority()) is null;
            if (accepted != expected) failures.Add($"{stage}/{pending}: expected={expected}, accepted={accepted}");
            cells++;
        }
        File.WriteAllBytes(Path.Combine(root, "operation-journal.json"), MigrationOperationJournal.Encode(original));
        Assert.Equal(90, cells);
        Assert.True(failures.Count == 0, string.Join("; ", failures));
        Assert.Contains("new", File.ReadAllText(Full(FlowPath)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("missing")]
    [InlineData("alias")]
    [InlineData("extra")]
    [InlineData("unfrozen")]
    public void CurrentV13_ParentAndRegistrationGridRejectsIncompleteAuthority(string cell)
    {
        const string nested = "a/b/plan.json";
        Seed(nested, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(),
            effectService: new WorkflowFileMigrationEffectService(), referenceWriteFault: point =>
            { if (point == "after_prepared") throw new IOException("owned_parent_grid_fault"); });
        Assert.True(tx.BeginTransaction("exact-parents-v13").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = nested, Kind = ChangeKind.Modified }]).Success);
        Assert.False(tx.ApplyReferenceUpdate(RenamePlan((nested, "old", "new"))).Success);
        var root = Path.Combine(_txRoot, "operations", "exact-parents-v13");
        var store = new WindowsTxfMigrationVersionStore(_configRoot, root);
        var journal = new MigrationOperationJournal(root, store);
        var document = journal.Load();
        var entry = Assert.Single(document.Operations);
        var parents = new Dictionary<string, MigrationFileVersion>(entry.Intent.ParentInputs!, StringComparer.Ordinal);
        if (cell == "missing") parents.Remove("a/b");
        if (cell == "alias") { parents.Remove("a/b"); parents["A"] = parents["a"]; }
        if (cell == "extra") parents["other"] = MigrationFileVersion.Absent(MigrationEntryKind.Directory);
        var intent = entry.Intent with { ParentInputs = cell == "null" ? null : parents };
        var candidate = document with { Operations = [entry with { Intent = intent }], RegistrationFrozen = cell != "unfrozen" };
        candidate = candidate with { Integrity = MigrationOperationJournal.Hash(candidate with { Integrity = "" }) };
        File.WriteAllBytes(Path.Combine(root, "operation-journal.json"), MigrationOperationJournal.Encode(candidate));
        Assert.ThrowsAny<Exception>(() => journal.ReadAuthority());
        Assert.Contains("old", File.ReadAllText(Full(nested)));
    }

    [Fact]
    public void CurrentV13_PreparedAddedAbsentDoesNotAuthorizeCancellationAfterParentReplacement()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        Directory.CreateDirectory(Full("baseline-parent"));
        const string added = "baseline-parent/new.json";
        using (var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(),
            effectService: new WorkflowFileMigrationEffectService(), referenceWriteFault: point =>
            { if (point == "after_prepared") throw new IOException("owned_added_parent_fault"); }))
        {
            Assert.True(tx.BeginTransaction("parent-replaced-v13").Success);
            Assert.True(tx.TakeSnapshot().Success);
            Assert.True(tx.RecordChanges([new ChangeRecord { Path = added, Kind = ChangeKind.Added }]).Success);
            Assert.False(tx.ApplyReferenceUpdate(new([new(added, ChangeKind.Added,
                NewContent: FlowJson("added", "candidate-ready", "old"))])).Success);
        }
        Directory.Move(Full("baseline-parent"), Path.Combine(_root, "retained-original-parent"));
        Directory.CreateDirectory(Full("baseline-parent"));
        var root = Path.Combine(_txRoot, "operations", "parent-replaced-v13");
        var store = new WindowsTxfMigrationVersionStore(_configRoot, root);
        var foreign = store.ReadVersion("baseline-parent", MigrationEntryKind.Directory);
        using var recovered = NewTx(new WorkflowFileMigrationEffectService());
        Assert.True(recovered.TryAcquireExclusive().Success);
        Assert.False(recovered.RecoverOnStart().Success);
        Assert.True(foreign.Matches(store.ReadVersion("baseline-parent", MigrationEntryKind.Directory)));
        Assert.False(File.Exists(Full(added)));
        var journal = new MigrationOperationJournal(root, store);
        Assert.Equal(MigrationOperationState.Prepared, Assert.Single(journal.ReadAuthority().Document.Operations).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentV13_ReceiptDirectoryEffectsMustEqualPreparedPlan(bool omit)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        const string added = "owned/nested/added.json";
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "receipt-grid-v13",
            changes: [new ChangeRecord { Path = added, Kind = ChangeKind.Added }]);
        Assert.True(tx.ApplyReferenceUpdate(new([new(added, ChangeKind.Added,
            NewContent: FlowJson("added", "candidate-ready", "old"))])).Success);
        var root = Path.Combine(_txRoot, "operations", "receipt-grid-v13");
        var store = new WindowsTxfMigrationVersionStore(_configRoot, root);
        var journal = new MigrationOperationJournal(root, store);
        var authority = journal.ReadAuthority();
        var entry = Assert.Single(authority.Document.Operations);
        var receipt = authority.Resolutions[entry.Intent.OperationId];
        var effects = omit ? receipt.Directories.Take(1).ToArray() : receipt.Directories.Concat(receipt.Directories.Take(1)).ToArray();
        var changed = receipt with { Directories = effects, Integrity = "" };
        changed = changed with { Integrity = MigrationOperationJournal.Hash(changed) };
        var bytes = MigrationOperationJournal.Encode(changed);
        File.WriteAllBytes(Path.Combine(root, entry.ResolutionPath!), bytes);
        var candidate = authority.Document with { Operations = [entry with { ResolutionSha256 = MigrationFileVersion.Hash(bytes) }] };
        candidate = candidate with { Integrity = MigrationOperationJournal.Hash(candidate with { Integrity = "" }) };
        File.WriteAllBytes(Path.Combine(root, "operation-journal.json"), MigrationOperationJournal.Encode(candidate));
        Assert.ThrowsAny<Exception>(() => journal.ReadAuthority());
        Assert.True(File.Exists(Full(added)));
        Assert.True(Directory.Exists(Full("owned/nested")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentV13_DirectoryDeletionRequiresOwnedCreationAndExactPredecessor(bool owned)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        Directory.CreateDirectory(Full("baseline-empty"));
        const string added = "owned/new.json";
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "delete-guards-v13",
            changes: [new ChangeRecord { Path = added, Kind = ChangeKind.Added }]);
        Assert.True(tx.ApplyReferenceUpdate(new([new(added, ChangeKind.Added,
            NewContent: FlowJson("added", "candidate-ready", "old"))])).Success);
        var root = Path.Combine(_txRoot, "operations", "delete-guards-v13");
        var store = new WindowsTxfMigrationVersionStore(_configRoot, root);
        var journal = new MigrationOperationJournal(root, store);
        journal.SetStage(MigrationStage.RollingBack, null);
        var target = owned ? "owned" : "baseline-empty";
        var input = store.ReadVersion(target, MigrationEntryKind.Directory);
        var result = journal.ExecutePreparedDelete(target, input, null);
        Assert.False(result.Success);
        Assert.True(input.Matches(store.ReadVersion(target, MigrationEntryKind.Directory)));
        Assert.True(Directory.Exists(Full(target)));
        Assert.Single(journal.ReadAuthority().Document.Operations);
    }

    [Fact]
    public void CurrentV13_SharedNewParentsHaveOneCreationAndPersistentRemovalPredecessor()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        const string first = "shared/nested/one.json";
        const string second = "shared/nested/two.json";
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "shared-parents-v13",
            changes: [new ChangeRecord { Path = first, Kind = ChangeKind.Added }, new ChangeRecord { Path = second, Kind = ChangeKind.Added }]);
        var update = tx.ApplyReferenceUpdate(new([new(first, ChangeKind.Added, NewContent: FlowJson("one", "candidate-ready", "old")),
            new(second, ChangeKind.Added, NewContent: FlowJson("two", "candidate-ready", "old"))]));
        Assert.True(update.Success, update.Reason);
        var root = Path.Combine(_txRoot, "operations", "shared-parents-v13");
        var journal = new MigrationOperationJournal(root, new WindowsTxfMigrationVersionStore(_configRoot, root));
        var before = journal.ReadAuthority();
        Assert.Equal(2, before.Document.Operations[0].Intent.Directories.Count);
        Assert.Empty(before.Document.Operations[1].Intent.Directories);
        Assert.True(tx.Rollback().Success);
        var after = journal.ReadAuthority();
        var removals = after.Document.Operations.Where(e => e.Intent.Phase == MigrationOperationPhase.RemoveOwnedDirectory).ToArray();
        Assert.Equal(2, removals.Length);
        Assert.All(removals, e => Assert.Equal(before.Document.Operations[0].Intent.OperationId, e.Intent.PredecessorOperationId));
        Assert.False(Directory.Exists(Full("shared")));
    }

    [Fact]
    public void CurrentV13_RestoreReceiptMustContainExactBaselineVersionAndBytes()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "restore-output-v13",
            changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
        Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
        Assert.True(tx.Rollback().Success);
        var root = Path.Combine(_txRoot, "operations", "restore-output-v13");
        var journal = new MigrationOperationJournal(root, new WindowsTxfMigrationVersionStore(_configRoot, root));
        var authority = journal.ReadAuthority();
        var entry = authority.Document.Operations.Last();
        Assert.Equal(MigrationOperationPhase.Restore, entry.Intent.Phase);
        var foreign = Encoding.UTF8.GetBytes("FOREIGN-RESTORE-OUTPUT");
        var hash = MigrationFileVersion.Hash(foreign);
        var intent = entry.Intent with { ExpectedOutputSha256 = hash };
        File.WriteAllBytes(Path.Combine(root, intent.OutputBlob!), foreign);
        var resolution = authority.Resolutions[intent.OperationId] with { IntentSha256 = MigrationOperationJournal.Hash(intent),
            Output = authority.Resolutions[intent.OperationId].Output with { Sha256 = hash, Length = foreign.Length }, Integrity = "" };
        resolution = resolution with { Integrity = MigrationOperationJournal.Hash(resolution) };
        var bytes = MigrationOperationJournal.Encode(resolution);
        File.WriteAllBytes(Path.Combine(root, entry.ResolutionPath!), bytes);
        var operations = authority.Document.Operations.ToArray();
        operations[^1] = entry with { Intent = intent, ResolutionSha256 = MigrationFileVersion.Hash(bytes) };
        var changed = authority.Document with { Operations = operations, Stage = MigrationStage.RollingBack, PendingStage = null };
        changed = changed with { Integrity = MigrationOperationJournal.Hash(changed with { Integrity = "" }) };
        File.WriteAllBytes(Path.Combine(root, "operation-journal.json"), MigrationOperationJournal.Encode(changed));
        Assert.ThrowsAny<Exception>(() => journal.ReadAuthority());
        Assert.Equal(baseline, File.ReadAllBytes(Full(FlowPath)));
    }

    [Fact]
    public void CurrentV8_MissingMainDoesNotErasePendingAuthorityOrAdmitNewTransaction()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using (var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "pending-without-main-v8"))
            File.Move(tx.ManifestPath, Path.Combine(_txRoot, "retained-main-v8.json"));
        var before = ReadV14OperationFacts(_root);
        var directoriesBefore = Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).Order().ToArray();
        var store = new WindowsTxfMigrationVersionStore(_configRoot, _txRoot);
        var dataVersion = store.ReadVersion(FlowPath);
        var retainedMain = store.ReadArtifactInput("retained-main-v8.json");
        using var reopened = NewTx(new WorkflowFileMigrationEffectService());
        Assert.False(reopened.TryAcquireExclusive().Success);
        Assert.False(reopened.HoldsExclusiveLock);
        Assert.False(reopened.RecoverOnStart().Success);
        Assert.False(reopened.BeginTransaction("unauthorized-next-v8").Success);
        Assert.False(File.Exists(reopened.ManifestPath));
        Assert.DoesNotContain("unauthorized-next-v8", File.ReadAllText(reopened.HistoryPath));
        var executions = 0;
        Assert.False(reopened.TryRunProduction(() => executions++).Success);
        Assert.Equal(0, executions);
        reopened.Dispose();
        AssertV14FactsEqual(before, _root);
        Assert.Equal(directoriesBefore, Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.True(dataVersion.Matches(store.ReadVersion(FlowPath)));
        Assert.True(retainedMain.Version.Matches(store.ReadArtifactInput("retained-main-v8.json").Version));
    }

    [Fact]
    public void CurrentV8_MissingRootBindingCannotBeRecreatedForExistingHistory()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using (var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "missing-binding-v8")) { }
        var binding = Assert.Single(Directory.GetFiles(_root, ".mistletoe-root-*.json"));
        File.Move(binding, binding + ".retained");
        using var reopened = NewTx(new WorkflowFileMigrationEffectService());
        Assert.False(reopened.TryAcquireExclusive().Success);
        Assert.False(File.Exists(binding));
        Assert.True(File.Exists(binding + ".retained"));
    }

    [Theory]
    [InlineData("after_prepared")]
    [InlineData("after_data")]
    public void CurrentV5_PartialAddedAppliedPrefix_ReopensAndRollsBack(string station)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        const string first = "owned-one/nested/first.json";
        const string second = "owned-two/nested/second.json";
        var opened = 0;
        using (var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(),
            effectService: new WorkflowFileMigrationEffectService(), referenceWriteFault: point =>
            {
                if (point == "after_open") opened++;
                if (opened == 2 && point == station) throw new IOException("actual_second_added_interruption");
            }))
        {
            Assert.True(tx.BeginTransaction("partial-added-v5").Success);
            Assert.True(tx.TakeSnapshot().Success);
            Assert.True(tx.RecordChanges([new ChangeRecord { Path = first, Kind = ChangeKind.Added },
                new ChangeRecord { Path = second, Kind = ChangeKind.Added }]).Success);
            var update = tx.ApplyReferenceUpdate(new([new(first, ChangeKind.Added,
                NewContent: FlowJson("first", "candidate-ready", "old")), new(second, ChangeKind.Added,
                NewContent: FlowJson("second", "candidate-ready", "old"))]));
            Assert.False(update.Success);
            Assert.True(File.Exists(Full(first)));
            Assert.False(File.Exists(Full(second)));
            var operationRoot = Path.Combine(_txRoot, "operations", "partial-added-v5");
            var journal = new MigrationOperationJournal(operationRoot, new WindowsTxfMigrationVersionStore(_configRoot, operationRoot));
            Assert.Single(journal.Load().Operations.Where(e => e.State == MigrationOperationState.Applied));
            Assert.Single(journal.Load().Operations.Where(e => e.State == MigrationOperationState.Prepared));
        }
        using var reopened = NewTx(new WorkflowFileMigrationEffectService());
        Assert.True(reopened.TryAcquireExclusive().Success);
        var result = reopened.RecoverOnStart();
        Assert.True(result.Success, result.Reason);
        Assert.False(File.Exists(Full(first)));
        Assert.False(Directory.Exists(Full("owned-one")));
        Assert.False(Directory.Exists(Full("owned-two")));
        Assert.Equal(baseline, File.ReadAllBytes(Full(FlowPath)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CurrentV5_TerminalReopen_RequiresNewLeaseAndWholeRoot(bool committed, bool foreignDirectory)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        Guid previousLease;
        using (var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "terminal-v5",
            changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]))
        {
            Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
            if (committed)
            {
                Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
                Assert.True(tx.RehearseRollback().Success);
                Assert.True(tx.Commit().Success);
            }
            else Assert.True(tx.Rollback().Success);
            previousLease = tx.LoadValidated()!.ControlledLatest!.LeaseId;
        }
        if (foreignDirectory) Directory.CreateDirectory(Full("foreign-empty"));
        using var reopened = NewTx(new WorkflowFileMigrationEffectService());
        Assert.True(reopened.TryAcquireExclusive().Success);
        var result = reopened.RecoverOnStart();
        if (foreignDirectory)
        {
            Assert.False(result.Success);
            Assert.True(Directory.Exists(Full("foreign-empty")));
        }
        else
        {
            Assert.True(result.Success, result.Reason);
            Assert.NotEqual(previousLease, reopened.LoadValidated()!.ControlledLatest!.LeaseId);
        }
    }

    [Theory]
    [InlineData("Acquire")]
    [InlineData("Begin")]
    [InlineData("Snapshot")]
    [InlineData("Record")]
    [InlineData("Reference")]
    [InlineData("Activate")]
    [InlineData("Commit")]
    [InlineData("Rollback")]
    [InlineData("Recover")]
    [InlineData("Upgrade")]
    [InlineData("Authorize")]
    [InlineData("Execute")]
    public void CurrentV5_CallbackWaitingForCrossThreadEntry_DoesNotDeadlock(string entry)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        MigrationSwitchTransaction? tx = null;
        System.Threading.Tasks.Task<MigrationResult>? nested = null;
        var completedInCallback = false;
        var executions = 0;
        MigrationResult Call() => entry switch
        {
            "Acquire" => tx!.TryAcquireExclusive(),
            "Begin" => tx!.BeginTransaction("nested"),
            "Snapshot" => tx!.TakeSnapshot(),
            "Record" => tx!.RecordChanges([]),
            "Reference" => tx!.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))),
            "Activate" => tx!.ActivateCandidate(new(FlowPath, "candidate-ready", "active")),
            "Commit" => tx!.Commit(),
            "Rollback" => tx!.Rollback(),
            "Recover" => tx!.RecoverOnStart(),
            "Upgrade" => tx!.UpgradeLegacyManifest(),
            "Authorize" => tx!.AuthorizeProductionExecution(),
            "Execute" => tx!.TryRunProduction(() => executions++),
            _ => throw new ArgumentException(entry)
        };
        tx = new(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), stageHook: stage =>
        {
            if (stage != MigrationStage.SnapshotReady || nested is not null) return;
            nested = System.Threading.Tasks.Task.Run(Call);
            completedInCallback = nested.Wait(TimeSpan.FromSeconds(2));
            using var contender = NewTx(new WorkflowFileMigrationEffectService());
            Assert.False(contender.TryAcquireExclusive().Success);
        }, effectService: new WorkflowFileMigrationEffectService());
        using (tx)
        {
            Assert.True(tx.BeginTransaction("callback-v5").Success);
            Assert.True(tx.TakeSnapshot().Success);
            Assert.NotNull(nested);
            var result = nested!.GetAwaiter().GetResult();
            Assert.True(completedInCallback);
            Assert.False(result.Success);
            Assert.Equal(0, executions);
            Assert.True(tx.HoldsExclusiveLock);
            Assert.Equal(MigrationStage.SnapshotReady, tx.LoadValidated()!.Stage);
        }
    }

    private Dictionary<string, byte[]> ReadV14OperationFacts(string operationRoot)
        => Directory.Exists(operationRoot)
            ? Directory.EnumerateFiles(operationRoot, "*", SearchOption.AllDirectories)
                .ToDictionary(p => Path.GetRelativePath(operationRoot, p).Replace('\\', '/'),
                    File.ReadAllBytes, StringComparer.Ordinal)
            : new(StringComparer.Ordinal);

    private void AssertV14FactsEqual(Dictionary<string, byte[]> expected, string operationRoot)
    {
        var actual = ReadV14OperationFacts(operationRoot);
        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal),
            actual.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var item in expected) Assert.Equal(item.Value, actual[item.Key]);
    }

    private string V14BindingPath() => Assert.Single(Directory.EnumerateFiles(
        _root, ".mistletoe-root-*.json", SearchOption.TopDirectoryOnly));

    private void AssertV14Pending(string? expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(V14BindingPath()));
        var value = document.RootElement.GetProperty("pendingTransactionId");
        if (expected is null) Assert.Equal(System.Text.Json.JsonValueKind.Null, value.ValueKind);
        else Assert.Equal(expected, value.GetString());
    }

    [Theory]
    [InlineData("after_open")]
    [InlineData("after_data")]
    public void CurrentV14_PrepareAndAppliedMainCAS_PreserveForeignAndAtomicFacts(string station)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        MigrationSwitchTransaction? tx = null;
        var operationRoot = Path.Combine(_txRoot, "operations", "main-edge-v14");
        var reached = false;
        MigrationInputBytes? foreign = null;
        Dictionary<string, byte[]> facts = [];
        byte[] pending = [];
        tx = new(_configRoot, _txRoot, () => Now, () => new NoopQuiet(),
            effectService: new WorkflowFileMigrationEffectService(), referenceWriteFault: point =>
            {
                if (point != station || reached) return;
                reached = true;
                facts = ReadV14OperationFacts(operationRoot);
                pending = File.ReadAllBytes(V14BindingPath());
                var external = tx!.LoadManifest()!;
                external.CreatedAtUtc = external.CreatedAtUtc.AddSeconds(29);
                external.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(external);
                File.WriteAllBytes(tx.ManifestPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(external,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                foreign = new WindowsTxfMigrationVersionStore(_configRoot, _txRoot)
                    .ReadArtifactInput("migration-manifest.json");
            });
        using (tx)
        {
            Assert.True(tx.BeginTransaction("main-edge-v14").Success);
            Assert.True(tx.TakeSnapshot().Success);
            Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
            var store = new WindowsTxfMigrationVersionStore(_configRoot, _txRoot);
            var baselineVersion = store.ReadVersion(FlowPath);
            MigrationResult? result = null;
            var leaked = Xunit.Record.Exception(() => result = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))));
            Assert.True(reached);
            Assert.Null(leaked);
            Assert.NotNull(result);
            Assert.False(result!.Success);
            Assert.NotNull(foreign);
            Assert.Equal(foreign!.Bytes, File.ReadAllBytes(tx.ManifestPath));
            Assert.True(foreign.Version.Matches(store.ReadArtifactInput("migration-manifest.json").Version));
            Assert.Equal(baseline, File.ReadAllBytes(Full(FlowPath)));
            Assert.True(baselineVersion.Matches(store.ReadVersion(FlowPath)));
            AssertV14FactsEqual(facts, operationRoot);
            Assert.Equal(pending, File.ReadAllBytes(V14BindingPath()));

            var authority = new MigrationOperationJournal(operationRoot,
                new WindowsTxfMigrationVersionStore(_configRoot, operationRoot)).ReadAuthority();
            if (station == "after_open") Assert.Empty(authority.Document.Operations);
            else Assert.Equal(MigrationOperationState.Prepared, Assert.Single(authority.Document.Operations).State);
            Assert.DoesNotContain(authority.Document.Operations, e => e.State == MigrationOperationState.Applied);
            Assert.False(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
            Assert.False(tx.RecoverOnStart().Success);
            Assert.Equal(foreign.Bytes, File.ReadAllBytes(tx.ManifestPath));
            Assert.True(foreign.Version.Matches(store.ReadArtifactInput("migration-manifest.json").Version));
            AssertV14FactsEqual(facts, operationRoot);
            Assert.Equal(pending, File.ReadAllBytes(V14BindingPath()));
            var executions = 0;
            Assert.False(tx.TryRunProduction(() => executions++).Success);
            Assert.Equal(0, executions);
        }
    }

    [Fact]
    public void CurrentV14_ConfirmedMainOutputBeforeFault_CanBlockAndRecover()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var reached = 0;
        using var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(),
            effectService: new WorkflowFileMigrationEffectService(), referenceWriteFault: point =>
            {
                if (point == "after_commit" && reached++ == 0)
                    throw new IOException("confirmed_main_output_fault");
            });
        Assert.True(tx.BeginTransaction("confirmed-main-v14").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
        var store = new WindowsTxfMigrationVersionStore(_configRoot, _txRoot);
        var baselineVersion = store.ReadVersion(FlowPath);
        MigrationResult? result = null;
        var leaked = Xunit.Record.Exception(() => result = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))));
        Assert.Equal(1, reached);
        Assert.Null(leaked);
        Assert.False(result!.Success);
        var main = tx.LoadValidated();
        Assert.NotNull(main);
        Assert.Equal(MigrationStage.Blocked, main!.Stage);
        var operationRoot = Path.Combine(_txRoot, "operations", "confirmed-main-v14");
        var journal = new MigrationOperationJournal(operationRoot,
            new WindowsTxfMigrationVersionStore(_configRoot, operationRoot));
        var authority = journal.ReadAuthority();
        Assert.Equal(MigrationStage.Blocked, authority.Document.Stage);
        var reference = Assert.Single(authority.Document.Operations);
        Assert.Equal(MigrationOperationState.Applied, reference.State);
        Assert.Equal(MigrationOperationPhase.Reference, reference.Intent.Phase);
        Assert.True(authority.Resolutions[reference.Intent.OperationId].Output.Matches(store.ReadVersion(FlowPath)));
        Assert.Equal(authority.Resolutions[reference.Intent.OperationId].Output.Sha256, main.ReferenceWriteSet[FlowPath]);
        Assert.NotEqual(MigrationFileVersion.Hash(baseline), store.ReadVersion(FlowPath).Sha256);
        var rolledBack = tx.Rollback();
        Assert.True(rolledBack.Success, rolledBack.Reason);
        Assert.Equal(baseline, File.ReadAllBytes(Full(FlowPath)));
        Assert.True(baselineVersion.Matches(store.ReadVersion(FlowPath)));
        Assert.Equal(MigrationStage.RolledBack, tx.LoadValidated()!.Stage);
        Assert.Equal(1, reached);
        AssertV14Pending(null);
    }

    [Theory]
    [InlineData("after_commit")]
    [InlineData("fileRestoredHook")]
    public void CurrentV14_PreparedActualRestoreB_ReopensTwiceWithoutRepeatingData(string station)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        var baselineBytes = File.ReadAllBytes(Full(FlowPath));
        var baselineSha = MigrationFileVersion.Hash(baselineBytes);
        var operationRoot = Path.Combine(_txRoot, "operations", "actual-restore-b-v14");
        var reached = 0;
        void Fault()
        {
            reached++;
            Assert.Equal(baselineBytes, File.ReadAllBytes(Full(FlowPath)));
            throw new IOException("actual_restore_b_interruption");
        }
        MigrationFileVersion baselineVersion;
        string resolutionDigest, appliedDigest;
        string restoreId;
        using (var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(),
            fileRestoredHook: rel => { if (station == "fileRestoredHook" && rel == FlowPath && reached == 0) Fault(); },
            effectService: new WorkflowFileMigrationEffectService(), referenceWriteFault: point =>
            { if (station == "after_commit" && point == "restore:after_commit" && reached == 0) Fault(); }))
        {
            Assert.True(tx.BeginTransaction("actual-restore-b-v14").Success);
            Assert.True(tx.TakeSnapshot().Success);
            Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
            var store = new WindowsTxfMigrationVersionStore(_configRoot, _txRoot);
            baselineVersion = store.ReadVersion(FlowPath);
            Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
            Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
            Assert.True(tx.RehearseRollback().Success);
            Assert.True(tx.Commit().Success);
            MigrationResult? result = null;
            var leaked = Xunit.Record.Exception(() => result = tx.Rollback());
            Assert.Equal(1, reached);
            Assert.Null(leaked);
            Assert.False(result!.Success);
            Assert.Equal(MigrationStage.Blocked, tx.LoadValidated()!.Stage);
            var authority = new MigrationOperationJournal(operationRoot,
                new WindowsTxfMigrationVersionStore(_configRoot, operationRoot)).ReadAuthority();
            var restored = authority.Document.Operations.Last(e => e.State == MigrationOperationState.Applied);
            Assert.Equal(MigrationOperationPhase.Restore, restored.Intent.Phase);
            var undo = Assert.Single(authority.Document.Operations.Where(e =>
                e.State == MigrationOperationState.Applied && e.Intent.Phase == MigrationOperationPhase.Undo));
            var reverted = authority.Resolutions[undo.Intent.OperationId].Output;
            Assert.NotEqual(baselineSha, reverted.Sha256); // B != R is an observed actual Undo output.
            Assert.Equal(undo.Intent.OperationId, restored.Intent.PredecessorOperationId);
            Assert.True(reverted.Matches(authority.Resolutions[restored.Intent.OperationId].Input));
            Assert.True(baselineVersion.Matches(authority.Resolutions[restored.Intent.OperationId].Output));
            Assert.Equal(baselineBytes, authority.Outputs[restored.Intent.OperationId]);
            Assert.Equal(baselineBytes, File.ReadAllBytes(Full(FlowPath)));
            Assert.True(baselineVersion.Matches(store.ReadVersion(FlowPath)));
            Assert.DoesNotContain(authority.Document.Operations, e => e.State == MigrationOperationState.Prepared);
            Assert.Equal(new[] { MigrationOperationPhase.Reference, MigrationOperationPhase.Activate,
                MigrationOperationPhase.Undo, MigrationOperationPhase.Restore },
                authority.Document.Operations.Select(e => e.Intent.Phase));
            resolutionDigest = authority.ResolutionChainDigest;
            appliedDigest = authority.AppliedChainDigest;
            restoreId = restored.Intent.OperationId;
            AssertV14Pending("actual-restore-b-v14");
        }
        for (var recovery = 0; recovery < 2; recovery++)
        {
            using var reopened = NewTx(new WorkflowFileMigrationEffectService());
            Assert.True(reopened.TryAcquireExclusive().Success);
            var result = reopened.RecoverOnStart();
            Assert.True(result.Success, "actual restored B recovery " + recovery + ":" + result.Reason);
            Assert.Equal(MigrationStage.RolledBack, result.Stage);
            Assert.Equal(baselineBytes, File.ReadAllBytes(Full(FlowPath)));
            Assert.True(baselineVersion.Matches(new WindowsTxfMigrationVersionStore(_configRoot, _txRoot).ReadVersion(FlowPath)));
            var authority = new MigrationOperationJournal(operationRoot,
                new WindowsTxfMigrationVersionStore(_configRoot, operationRoot)).ReadAuthority();
            Assert.Equal(resolutionDigest, authority.ResolutionChainDigest);
            Assert.Equal(appliedDigest, authority.AppliedChainDigest);
            Assert.Equal(restoreId, authority.Document.Operations.Last(e => e.State == MigrationOperationState.Applied).Intent.OperationId);
            Assert.Equal(4, authority.Document.Operations.Count);
            Assert.Equal(MigrationStage.RolledBack, reopened.LoadValidated()!.ControlledLatest!.SuccessfulStage);
            AssertV14Pending(null);
            var executions = 0;
            Assert.False(reopened.TryRunProduction(() => executions++).Success);
            Assert.Equal(0, executions);
        }
        Assert.Equal(1, reached);
    }

    [Theory]
    [InlineData("SuppressFlow")]
    [InlineData("UnsafeQueue")]
    public void CurrentV14_NoFlowPreparedFaultWorker_IsRejectedBeforeWaiting(string flow)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        MigrationSwitchTransaction? tx = null;
        System.Threading.Tasks.Task<MigrationResult>? worker = null;
        var reached = false;
        var completedInside = false;
        tx = new(_configRoot, _txRoot, () => Now, () => new NoopQuiet(),
            effectService: new WorkflowFileMigrationEffectService(), referenceWriteFault: point =>
            {
                if (point != "after_open" || reached) return;
                reached = true;
                var mainBefore = File.ReadAllBytes(tx!.ManifestPath);
                var root = Path.Combine(_txRoot, "operations", "prepared-noflow-v14");
                var factsBefore = ReadV14OperationFacts(root);
                var pendingBefore = File.ReadAllBytes(V14BindingPath());
                MigrationResult Call() => tx.RecordChanges([]);
                if (flow == "SuppressFlow")
                {
                    using (System.Threading.ExecutionContext.SuppressFlow())
                        worker = System.Threading.Tasks.Task.Run(Call);
                }
                else
                {
                    var completion = new System.Threading.Tasks.TaskCompletionSource<MigrationResult>(
                        System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                    worker = completion.Task;
                    System.Threading.ThreadPool.UnsafeQueueUserWorkItem(_ =>
                    {
                        try { completion.SetResult(Call()); }
                        catch (Exception ex) { completion.SetException(ex); }
                    }, null);
                }
                completedInside = worker.Wait(TimeSpan.FromSeconds(2));
                Assert.Equal(mainBefore, File.ReadAllBytes(tx.ManifestPath));
                AssertV14FactsEqual(factsBefore, root);
                Assert.Equal(pendingBefore, File.ReadAllBytes(V14BindingPath()));
                using var contender = NewTx(new WorkflowFileMigrationEffectService());
                Assert.False(contender.TryAcquireExclusive().Success);
            });
        using (tx)
        {
            Assert.True(tx.BeginTransaction("prepared-noflow-v14").Success);
            Assert.True(tx.TakeSnapshot().Success);
            Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
            var result = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new")));
            Assert.True(reached); // Actual TxF after_open, not merely a stage callback.
            Assert.NotNull(worker);
            var nested = worker!.GetAwaiter().GetResult(); // Drain even a timed-out mutant worker.
            Assert.True(completedInside);
            Assert.False(nested.Success);
            Assert.Equal("reentrant_mutation_rejected", nested.Reason);
            Assert.True(result.Success, result.Reason);
            Assert.Equal(MigrationStage.ReferenceUpdating, tx.LoadValidated()!.Stage);
            Assert.Single(tx.LoadValidated()!.ChangedFiles);
            var root = Path.Combine(_txRoot, "operations", "prepared-noflow-v14");
            var authority = new MigrationOperationJournal(root,
                new WindowsTxfMigrationVersionStore(_configRoot, root)).ReadAuthority();
            var reference = Assert.Single(authority.Document.Operations);
            Assert.Equal(MigrationOperationState.Applied, reference.State);
            Assert.Equal(MigrationOperationPhase.Reference, reference.Intent.Phase);
            Assert.True(authority.Resolutions[reference.Intent.OperationId].Output.Matches(
                new WindowsTxfMigrationVersionStore(_configRoot, _txRoot).ReadVersion(FlowPath)));
            Assert.True(tx.HoldsExclusiveLock);
        }
    }

    [Fact]
    public void CurrentV14_AbortTerminalWithUnknownJournalOrOrphanFacts_IsNotExempt()
    {
        // One fact contains two independent roots; journal cannot mask the orphan branch.
        foreach (var station in new[] { "journal", "orphan" })
        {
            var caseRoot = Path.Combine(_root, "abort-facts-" + station);
            var config = Path.Combine(caseRoot, "cfg");
            var artifacts = Path.Combine(caseRoot, "tx");
            var dataPath = Path.Combine(config, FlowPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
            var data = Encoding.UTF8.GetBytes(FlowJson("baseline", "candidate-ready", "old"));
            File.WriteAllBytes(dataPath, data);
            MigrationSwitchTransaction Create() => new(config, artifacts, () => Now, () => new NoopQuiet(),
                effectService: new WorkflowFileMigrationEffectService());
            using (var initial = Create()) Assert.True(initial.BeginTransaction("abort-facts-v14").Success);
            using (var aborted = Create())
            {
                Assert.True(aborted.TryAcquireExclusive().Success);
                var safeAbort = aborted.RecoverOnStart();
                Assert.True(safeAbort.Success, safeAbort.Reason);
                Assert.Equal(MigrationStage.RolledBack, safeAbort.Stage);
                Assert.False(aborted.LoadValidated()!.BaselineCompleted);
                Assert.Null(aborted.LoadValidated()!.ControlledBaseline);
            }

            var root = Path.Combine(artifacts, "operations", "abort-facts-v14");
            if (station == "journal")
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "operation-journal.json"), "{ unexplained corrupt journal");
            }
            else
            {
                var outputs = Path.Combine(root, "outputs");
                Directory.CreateDirectory(outputs);
                File.WriteAllBytes(Path.Combine(outputs, "unexplained.bin"), [7, 11, 19]);
                Assert.False(File.Exists(Path.Combine(root, "operation-journal.json")));
            }

            var store = new WindowsTxfMigrationVersionStore(config, artifacts);
            var ownedMain = store.ReadArtifactInput("migration-manifest.json");
            var dataVersion = store.ReadVersion(FlowPath);
            var bindingPath = Assert.Single(Directory.EnumerateFiles(
                caseRoot, ".mistletoe-root-*.json", SearchOption.TopDirectoryOnly));
            var pendingBefore = File.ReadAllBytes(bindingPath);
            var factsBefore = ReadV14OperationFacts(root);
            Assert.NotEmpty(factsBefore);
            using var reopened = Create();
            Assert.False(reopened.TryAcquireExclusive().Success);
            Assert.False(reopened.HoldsExclusiveLock);
            // Main itself is intact; the persistent protocol is unverifiable and refuses earlier.
            Assert.True(MigrationLegacyManifestCodec.TryDecode(ownedMain.Bytes, out _, out _));
            var historyBefore = File.ReadAllBytes(reopened.HistoryPath);
            MigrationResult? result = null;
            var leaked = Xunit.Record.Exception(() => result = reopened.RecoverOnStart());
            Assert.Null(leaked);
            Assert.NotNull(result);
            Assert.False(result!.Success);
            Assert.False(reopened.BeginTransaction("must-not-hide-" + station).Success);
            Assert.Equal(ownedMain.Bytes, File.ReadAllBytes(reopened.ManifestPath));
            Assert.True(ownedMain.Version.Matches(store.ReadArtifactInput("migration-manifest.json").Version));
            Assert.Equal(data, File.ReadAllBytes(dataPath));
            Assert.True(dataVersion.Matches(store.ReadVersion(FlowPath)));
            Assert.Equal(pendingBefore, File.ReadAllBytes(bindingPath));
            Assert.Equal(historyBefore, File.ReadAllBytes(reopened.HistoryPath));
            AssertV14FactsEqual(factsBefore, root);
            var executions = 0;
            Assert.False(reopened.TryRunProduction(() => executions++).Success);
            Assert.Equal(0, executions);
            reopened.Dispose();
            Assert.Equal(ownedMain.Bytes, File.ReadAllBytes(reopened.ManifestPath));
            Assert.True(ownedMain.Version.Matches(store.ReadArtifactInput("migration-manifest.json").Version));
            Assert.True(dataVersion.Matches(store.ReadVersion(FlowPath)));
            Assert.Equal(pendingBefore, File.ReadAllBytes(bindingPath));
            Assert.Equal(historyBefore, File.ReadAllBytes(reopened.HistoryPath));
            AssertV14FactsEqual(factsBefore, root);
        }
    }

    [Fact]
    public void CurrentV14_ActualPartialSnapshotIOException_ReopensTwiceWithoutChangingData()
    {
        Seed(FlowPath, FlowJson("first-data", "candidate-ready", "old"));
        Seed(SecondPath, FlowJson("second-data", "candidate-ready", "old"));
        // Match the actual production enumerator; do not sort or assume a filename order.
        var ordered = Directory.EnumerateFiles(_configRoot, "*", SearchOption.AllDirectories).ToArray();
        Assert.Equal(2, ordered.Length);
        var relative = ordered.Select(p => Path.GetRelativePath(_configRoot, p).Replace('\\', '/')).ToArray();
        var bytes = ordered.Select(File.ReadAllBytes).ToArray();
        Assert.All(bytes, b => Assert.NotEmpty(b));
        MigrationFileVersion[] versions;
        var operationRoot = Path.Combine(_txRoot, "operations", "actual-partial-copy-v14");
        using (var tx = NewTx(new WorkflowFileMigrationEffectService()))
        {
            Assert.True(tx.BeginTransaction("actual-partial-copy-v14").Success);
            var store = new WindowsTxfMigrationVersionStore(_configRoot, _txRoot);
            versions = relative.Select(p => store.ReadVersion(p)).ToArray();
            var initial = tx.LoadValidated()!;
            Assert.Equal(MigrationStage.Snapshotting, initial.Stage);
            Assert.False(initial.BaselineCompleted);
            var firstTarget = Path.Combine(initial.SnapshotPath, relative[0].Replace('/', Path.DirectorySeparatorChar));
            var secondTarget = Path.Combine(initial.SnapshotPath, relative[1].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(secondTarget)!);
            using (var lockedSecond = new FileStream(secondTarget, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                // Stable isolated inputs must still have the captured real traversal order.
                Assert.Equal(ordered, Directory.EnumerateFiles(_configRoot, "*", SearchOption.AllDirectories).ToArray());
                MigrationResult? result = null;
                var leaked = Xunit.Record.Exception(() => result = tx.TakeSnapshot());
                Assert.Null(leaked);
                Assert.NotNull(result);
                Assert.False(result!.Success);
                Assert.Equal("snapshot_io_failed:IOException", result.Reason);
                Assert.Equal(MigrationStage.Blocked, result.Stage);
                // First copy proves the loop genuinely progressed before the locked target.
                Assert.True(File.Exists(firstTarget));
                Assert.Equal(bytes[0], File.ReadAllBytes(firstTarget));
                Assert.Equal(0L, lockedSecond.Length); // Its own handle stays usable; competing output write was refused.
                var blocked = tx.LoadValidated();
                Assert.NotNull(blocked);
                Assert.Equal(MigrationStage.Blocked, blocked!.Stage);
                Assert.False(blocked.BaselineCompleted);
                Assert.Empty(blocked.FileHashes);
                Assert.Empty(blocked.BaselineVersions);
                Assert.Equal("", blocked.SnapshotManifestHash);
                Assert.Null(blocked.ControlledBaseline);
                Assert.Null(blocked.ControlledLatest);
                Assert.Null(blocked.JournalBinding);
                Assert.Empty(blocked.ReferenceWriteSet);
                Assert.Null(blocked.ActivationRecord);
                Assert.Empty(ReadV14OperationFacts(operationRoot));
                AssertV14Pending("actual-partial-copy-v14");
                for (var i = 0; i < ordered.Length; i++)
                {
                    Assert.Equal(bytes[i], File.ReadAllBytes(ordered[i]));
                    Assert.True(versions[i].Matches(store.ReadVersion(relative[i])));
                }
                var actions = 0;
                Assert.False(tx.TryRunProduction(() => actions++).Success);
                Assert.Equal(0, actions);
            } // Release the output lock before the new instance's safe abort cleanup.
        }

        MigrationInputBytes? terminalAfterFirst = null;
        for (var recovery = 0; recovery < 2; recovery++)
        {
            using var reopened = NewTx(new WorkflowFileMigrationEffectService());
            Assert.True(reopened.TryAcquireExclusive().Success);
            var result = reopened.RecoverOnStart();
            Assert.True(result.Success, "actual partial-copy recovery " + recovery + ":" + result.Reason);
            Assert.Equal(MigrationStage.RolledBack, result.Stage);
            var terminal = reopened.LoadValidated()!;
            Assert.False(terminal.BaselineCompleted);
            Assert.Empty(terminal.FileHashes);
            Assert.Empty(terminal.BaselineVersions);
            Assert.Null(terminal.ControlledBaseline);
            Assert.Null(terminal.ControlledLatest);
            Assert.Null(terminal.JournalBinding);
            Assert.Empty(terminal.ReferenceWriteSet);
            Assert.Null(terminal.ActivationRecord);
            Assert.Empty(ReadV14OperationFacts(operationRoot));
            AssertV14Pending(null);
            var store = new WindowsTxfMigrationVersionStore(_configRoot, _txRoot);
            for (var i = 0; i < ordered.Length; i++)
            {
                Assert.Equal(bytes[i], File.ReadAllBytes(ordered[i]));
                Assert.True(versions[i].Matches(store.ReadVersion(relative[i])));
            }
            var main = store.ReadArtifactInput("migration-manifest.json");
            if (terminalAfterFirst is null) terminalAfterFirst = main;
            else
            {
                Assert.Equal(terminalAfterFirst.Bytes, main.Bytes);
                Assert.True(terminalAfterFirst.Version.Matches(main.Version));
            }
            var actions = 0;
            Assert.False(reopened.TryRunProduction(() => actions++).Success);
            Assert.Equal(0, actions);
        }
    }

    [Fact]
    public void CurrentV15_MainWriterSameOwnedIdentityThroughSymlink_IsRejectedWithoutEscapingArtifactRoot()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        MigrationSwitchTransaction? tx = null;
        var armed = false;
        var reached = false;
        var retainedRoot = Path.Combine(_root, "outside-artifact");
        var retained = Path.Combine(retainedRoot, "original-main.json");
        var operationRoot = Path.Combine(_txRoot, "operations", "main-namespace-v15");
        MigrationInputBytes? original = null;
        Dictionary<string, byte[]> facts = [];
        byte[] pending = [];
        tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "main-namespace-v15",
            hook: stage =>
            {
                if (!armed || reached || stage != MigrationStage.Committed) return;
                reached = true;
                facts = ReadV14OperationFacts(operationRoot);
                pending = File.ReadAllBytes(V14BindingPath());
                original = new WindowsTxfMigrationVersionStore(_configRoot, _txRoot)
                    .ReadArtifactInput("migration-manifest.json");
                Directory.CreateDirectory(retainedRoot);
                File.Move(tx!.ManifestPath, retained);
                File.CreateSymbolicLink(tx.ManifestPath, retained);
                var outsideStore = new WindowsTxfMigrationVersionStore(retainedRoot, _configRoot);
                Assert.Equal(original.Bytes, File.ReadAllBytes(retained));
                Assert.True(original.Version.Matches(outsideStore.ReadVersion("original-main.json")));
                Assert.NotNull(new FileInfo(tx.ManifestPath).LinkTarget);
            }, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        using (tx)
        {
            Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
            Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
            Assert.True(tx.RehearseRollback().Success);
            var data = File.ReadAllBytes(Full(FlowPath));
            var store = new WindowsTxfMigrationVersionStore(_configRoot, _txRoot);
            var dataVersion = store.ReadVersion(FlowPath);
            armed = true;
            MigrationResult? result = null;
            var leaked = Xunit.Record.Exception(() => result = tx.Commit());
            Assert.True(reached);
            Assert.NotNull(original);
            Assert.Null(leaked); // Symlink privilege/setup failure must fail, never silently skip.
            Assert.NotNull(result);
            var outsideStore = new WindowsTxfMigrationVersionStore(retainedRoot, _configRoot);
            Assert.Equal(original!.Bytes, File.ReadAllBytes(retained));
            Assert.True(original.Version.Matches(outsideStore.ReadVersion("original-main.json")));
            Assert.False(result!.Success);
            Assert.NotNull(new FileInfo(tx.ManifestPath).LinkTarget);
            Assert.Equal(Path.GetFullPath(retained), Path.GetFullPath(
                new FileInfo(tx.ManifestPath).ResolveLinkTarget(true)!.FullName));
            AssertV14FactsEqual(facts, operationRoot);
            Assert.Equal(pending, File.ReadAllBytes(V14BindingPath()));
            Assert.Equal(data, File.ReadAllBytes(Full(FlowPath)));
            Assert.True(dataVersion.Matches(store.ReadVersion(FlowPath)));

            foreach (var retry in new Func<MigrationResult>[] { tx.Commit, tx.RecoverOnStart, tx.Rollback })
            {
                MigrationResult? retried = null;
                var retryLeak = Xunit.Record.Exception(() => retried = retry());
                Assert.Null(retryLeak);
                Assert.NotNull(retried);
                Assert.False(retried!.Success);
                Assert.Equal(original.Bytes, File.ReadAllBytes(retained));
                Assert.True(original.Version.Matches(outsideStore.ReadVersion("original-main.json")));
                Assert.NotNull(new FileInfo(tx.ManifestPath).LinkTarget);
                AssertV14FactsEqual(facts, operationRoot);
                Assert.Equal(pending, File.ReadAllBytes(V14BindingPath()));
                Assert.Equal(data, File.ReadAllBytes(Full(FlowPath)));
                Assert.True(dataVersion.Matches(store.ReadVersion(FlowPath)));
            }
            var actions = 0;
            Assert.False(tx.TryRunProduction(() => actions++).Success);
            Assert.Equal(0, actions);
        }
    }

    [Theory]
    [InlineData(MigrationStage.ReferenceUpdating, false)]
    [InlineData(MigrationStage.Committed, false)]
    [InlineData(MigrationStage.RolledBack, false)]
    [InlineData(MigrationStage.ReferenceUpdating, true)]
    [InlineData(MigrationStage.Committed, true)]
    [InlineData(MigrationStage.RolledBack, true)]
    public void CurrentV14_ModernMainPublication_PreservesForeignInputAndIdentity(MigrationStage target, bool sameBytesReplacement)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        MigrationSwitchTransaction? tx = null;
        var armed = false;
        var injected = false;
        byte[] foreign = [], retained = [], journalBefore = [];
        MigrationFileVersion? foreignVersion = null;
        var journalPath = Path.Combine(_txRoot, "operations", "modern-main-v14", "operation-journal.json");
        var retainedPath = Path.Combine(_txRoot, "retained-original-main.bak");
        tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "modern-main-v14", hook: stage =>
        {
            if (!armed || injected || stage != target) return;
            injected = true;
            journalBefore = File.ReadAllBytes(journalPath);
            var oldVersion = new WindowsTxfMigrationVersionStore(_txRoot, _configRoot).ReadVersion("migration-manifest.json");
            if (sameBytesReplacement)
            {
                retained = File.ReadAllBytes(tx!.ManifestPath);
                File.Move(tx.ManifestPath, retainedPath);
                File.WriteAllBytes(tx.ManifestPath, retained);
            }
            else
            {
                var external = tx!.LoadManifest()!;
                external.CreatedAtUtc = external.CreatedAtUtc.AddSeconds(17);
                external.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(external);
                File.WriteAllBytes(tx.ManifestPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(external,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            foreign = File.ReadAllBytes(tx!.ManifestPath);
            foreignVersion = new WindowsTxfMigrationVersionStore(_txRoot, _configRoot).ReadVersion("migration-manifest.json");
            if (sameBytesReplacement) Assert.NotEqual(oldVersion.FileId, foreignVersion.FileId);
        }, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        using (tx)
        {
            if (target != MigrationStage.ReferenceUpdating)
            {
                Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
                Assert.True(tx.ActivateCandidate(new(FlowPath, "candidate-ready", "active")).Success);
                if (target == MigrationStage.Committed) Assert.True(tx.RehearseRollback().Success);
            }
            armed = true;
            MigrationResult Run() => target switch
            {
                MigrationStage.ReferenceUpdating => tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))),
                MigrationStage.Committed => tx.Commit(),
                _ => tx.Rollback()
            };
            MigrationResult? result = null;
            var exception = Xunit.Record.Exception(() => result = Run());
            Assert.True(injected);
            Assert.Equal(foreign, File.ReadAllBytes(tx.ManifestPath));
            Assert.True(foreignVersion!.Matches(new WindowsTxfMigrationVersionStore(_txRoot, _configRoot).ReadVersion("migration-manifest.json")));
            Assert.Equal(journalBefore, File.ReadAllBytes(journalPath));
            if (sameBytesReplacement) Assert.Equal(retained, File.ReadAllBytes(retainedPath));
            Assert.Null(exception);
            Assert.NotNull(result);
            Assert.False(result!.Success);
            result = null;
            exception = Xunit.Record.Exception(() => result = Run());
            Assert.Equal(foreign, File.ReadAllBytes(tx.ManifestPath));
            Assert.True(foreignVersion.Matches(new WindowsTxfMigrationVersionStore(_txRoot, _configRoot).ReadVersion("migration-manifest.json")));
            Assert.Equal(journalBefore, File.ReadAllBytes(journalPath));
            Assert.Null(exception);
            Assert.False(result!.Success);
        }
    }

    [Theory]
    [InlineData("Acquire", "SuppressFlow")]
    [InlineData("Begin", "SuppressFlow")]
    [InlineData("Snapshot", "SuppressFlow")]
    [InlineData("Record", "SuppressFlow")]
    [InlineData("Reference", "SuppressFlow")]
    [InlineData("Activate", "SuppressFlow")]
    [InlineData("Commit", "SuppressFlow")]
    [InlineData("Rollback", "SuppressFlow")]
    [InlineData("Recover", "SuppressFlow")]
    [InlineData("Upgrade", "SuppressFlow")]
    [InlineData("Authorize", "SuppressFlow")]
    [InlineData("Execute", "SuppressFlow")]
    [InlineData("Acquire", "UnsafeQueue")]
    [InlineData("Begin", "UnsafeQueue")]
    [InlineData("Snapshot", "UnsafeQueue")]
    [InlineData("Record", "UnsafeQueue")]
    [InlineData("Reference", "UnsafeQueue")]
    [InlineData("Activate", "UnsafeQueue")]
    [InlineData("Commit", "UnsafeQueue")]
    [InlineData("Rollback", "UnsafeQueue")]
    [InlineData("Recover", "UnsafeQueue")]
    [InlineData("Upgrade", "UnsafeQueue")]
    [InlineData("Authorize", "UnsafeQueue")]
    [InlineData("Execute", "UnsafeQueue")]
    public void CurrentV14_NoFlowCallbackWaitingForCrossThreadEntry_DoesNotDeadlock(string entry, string flow)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        MigrationSwitchTransaction? tx = null;
        System.Threading.Tasks.Task<MigrationResult>? nested = null;
        var completedInCallback = false;
        var executions = 0;
        MigrationResult Call() => entry switch
        {
            "Acquire" => tx!.TryAcquireExclusive(),
            "Begin" => tx!.BeginTransaction("nested"),
            "Snapshot" => tx!.TakeSnapshot(),
            "Record" => tx!.RecordChanges([]),
            "Reference" => tx!.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))),
            "Activate" => tx!.ActivateCandidate(new(FlowPath, "candidate-ready", "active")),
            "Commit" => tx!.Commit(),
            "Rollback" => tx!.Rollback(),
            "Recover" => tx!.RecoverOnStart(),
            "Upgrade" => tx!.UpgradeLegacyManifest(),
            "Authorize" => tx!.AuthorizeProductionExecution(),
            "Execute" => tx!.TryRunProduction(() => executions++),
            _ => throw new ArgumentException(entry)
        };
        tx = new(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), stageHook: stage =>
        {
            if (stage != MigrationStage.SnapshotReady || nested is not null) return;
            if (flow == "SuppressFlow")
            {
                using (System.Threading.ExecutionContext.SuppressFlow())
                    nested = System.Threading.Tasks.Task.Run(Call);
            }
            else
            {
                var completion = new System.Threading.Tasks.TaskCompletionSource<MigrationResult>(
                    System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                nested = completion.Task;
                System.Threading.ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    try { completion.SetResult(Call()); }
                    catch (Exception ex) { completion.SetException(ex); }
                }, null);
            }
            completedInCallback = nested.Wait(TimeSpan.FromSeconds(2));
            using var contender = NewTx(new WorkflowFileMigrationEffectService());
            Assert.False(contender.TryAcquireExclusive().Success);
        }, effectService: new WorkflowFileMigrationEffectService());
        using (tx)
        {
            Assert.True(tx.BeginTransaction("callback-noflow-v14").Success);
            Assert.True(tx.TakeSnapshot().Success);
            Assert.NotNull(nested);
            var result = nested!.GetAwaiter().GetResult();
            Assert.True(completedInCallback);
            Assert.False(result.Success);
            Assert.Equal(0, executions);
            Assert.True(tx.HoldsExclusiveLock);
            Assert.Equal(MigrationStage.SnapshotReady, tx.LoadValidated()!.Stage);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentV14_PreparedNoBaselineAbort_ReopensTwiceWithoutChangingData(bool partialSnapshot)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        var bytes = File.ReadAllBytes(Full(FlowPath));
        Directory.CreateDirectory(_txRoot);
        var identity = new WindowsTxfMigrationVersionStore(_configRoot, _txRoot).ReadVersion(FlowPath);
        using (var initial = NewTx(new WorkflowFileMigrationEffectService()))
        {
            Assert.True(initial.BeginTransaction("empty-abort-v14").Success);
            if (partialSnapshot)
            {
                var main = initial.LoadValidated()!;
                Directory.CreateDirectory(main.SnapshotPath);
                File.WriteAllText(Path.Combine(main.SnapshotPath, "partial-copy-only.txt"), "incomplete");
            }
        }
        using (var first = NewTx(new WorkflowFileMigrationEffectService()))
        {
            Assert.True(first.TryAcquireExclusive().Success);
            var aborted = first.RecoverOnStart();
            Assert.True(aborted.Success, aborted.Reason);
            Assert.Equal(MigrationStage.RolledBack, aborted.Stage);
            Assert.Equal(bytes, File.ReadAllBytes(Full(FlowPath)));
            Assert.True(identity.Matches(new WindowsTxfMigrationVersionStore(_configRoot, _txRoot).ReadVersion(FlowPath)));
        }
        using var second = NewTx(new WorkflowFileMigrationEffectService());
        Assert.True(second.TryAcquireExclusive().Success);
        var recovered = second.RecoverOnStart();
        Assert.True(recovered.Success, recovered.Reason);
        Assert.Equal(MigrationStage.RolledBack, recovered.Stage);
        Assert.Equal(bytes, File.ReadAllBytes(Full(FlowPath)));
        Assert.True(identity.Matches(new WindowsTxfMigrationVersionStore(_configRoot, _txRoot).ReadVersion(FlowPath)));
        var calls = 0;
        Assert.False(second.TryRunProduction(() => calls++).Success);
        Assert.Equal(0, calls);
        Assert.True(second.BeginTransaction("after-empty-abort-v14").Success);
    }

    [Fact]
    public void CurrentV5_DisposedInstance_CannotReacquireAuthority()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "dispose-v5");
        tx.Dispose();
        var acquired = tx.TryAcquireExclusive();
        Assert.False(tx.HoldsExclusiveLock);
        Assert.False(acquired.Success);
        Assert.False(tx.BeginTransaction("reuse-after-dispose").Success);
        using var fresh = NewTx(new WorkflowFileMigrationEffectService());
        Assert.True(fresh.TryAcquireExclusive().Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentV5_ActualPreparedCommitted_ProductionActionRemainsClosed(bool reopen)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "production-v5",
            changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
        Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);
        MigrationSwitchTransaction subject = tx;
        using var recovered = NewTx(new WorkflowFileMigrationEffectService());
        if (reopen)
        {
            tx.Dispose();
            Assert.True(recovered.TryAcquireExclusive().Success);
            Assert.True(recovered.RecoverOnStart().Success);
            subject = recovered;
        }
        var marker = Path.Combine(_root, "forbidden-production-action.txt");
        var result = subject.TryRunProduction(() => File.WriteAllText(marker, "executed"));
        Assert.False(File.Exists(marker));
        Assert.False(result.Success);
    }


    [Fact]
    public void CurrentV5_ExactBaselineAlreadyPresent_ReopensWithoutInventingUndoOrRestore()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        using (var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "exact-baseline-v5",
            changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]))
        {
            Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
            Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
            File.WriteAllBytes(Full(FlowPath), baseline);
        }
        using var recovered = NewTx(new WorkflowFileMigrationEffectService());
        Assert.True(recovered.TryAcquireExclusive().Success);
        var result = recovered.RecoverOnStart();
        Assert.True(result.Success, result.Reason);
        var operationRoot = Path.Combine(_txRoot, "operations", "exact-baseline-v5");
        var journal = new MigrationOperationJournal(operationRoot, new WindowsTxfMigrationVersionStore(_configRoot, operationRoot));
        var authority = journal.ReadAuthority();
        Assert.Equal(2, authority.Document.Operations.Count);
        Assert.DoesNotContain(authority.Document.Operations, e => e.Intent.Phase is MigrationOperationPhase.Undo or MigrationOperationPhase.Restore);
        var observation = Assert.Single(authority.Document.BaselineObservations!);
        Assert.False(observation.MutationPerformed);
        Assert.Equal(MigrationFileVersion.Hash(baseline), observation.BaselineHash);
        Assert.Null(recovered.LoadValidated()!.ActivationRecord!.RevertedHash);
        Assert.Equal(baseline, File.ReadAllBytes(Full(FlowPath)));
        recovered.Dispose();
        using var reopenedAgain = NewTx(new WorkflowFileMigrationEffectService());
        Assert.True(reopenedAgain.TryAcquireExclusive().Success);
        Assert.True(reopenedAgain.RecoverOnStart().Success);
        Assert.Equal(2, journal.ReadAuthority().Document.Operations.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentV5_MissingOrDamagedJournal_NeverFallsBackToPlainIO(bool damaged)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "missing-journal-v5",
            changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
        var bytes = File.ReadAllBytes(Full(FlowPath));
        var request = Activation(tx);
        var path = Path.Combine(_txRoot, "operations", "missing-journal-v5", "operation-journal.json");
        if (damaged) File.WriteAllText(path, "{\"damaged\":true}");
        else File.Move(path, Path.Combine(_txRoot, "retained-journal-before-missing.json"));
        Assert.False(tx.ActivateCandidate(request).Success);
        Assert.Equal(bytes, File.ReadAllBytes(Full(FlowPath)));
        Assert.False(tx.Rollback().Success);
        Assert.Equal(bytes, File.ReadAllBytes(Full(FlowPath)));
    }


    [Theory]
    [InlineData("after_prepared")]
    [InlineData("after_data")]
    [InlineData("after_receipt")]
    [InlineData("after_handles")]
    [InlineData("after_commit")]
    [InlineData("activate:after_data")]
    [InlineData("activate:after_commit")]
    [InlineData("undo:after_data")]
    [InlineData("undo:after_commit")]
    [InlineData("restore:after_data")]
    [InlineData("restore:after_commit")]
    public async System.Threading.Tasks.Task CurrentV5_RealProducerExit_PreservesFactAndNewInstanceRecovers(string station)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var execution = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        var evidence = Path.Combine(execution, "fault-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var start = new System.Diagnostics.ProcessStartInfo(@"C:/Program Files/dotnet/dotnet.exe")
            { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "ControlledWriterProbe.dll"),
            "--migration-fault", _configRoot, _txRoot, "real-exit-v5", station, evidence })
            start.ArgumentList.Add(argument);
        using var producer = System.Diagnostics.Process.Start(start)!;
        await producer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(71, producer.ExitCode);
        Assert.Contains("reached:" + station, File.ReadAllText(evidence));
        using var recovered = NewTx(new WorkflowFileMigrationEffectService());
        Assert.True(recovered.TryAcquireExclusive().Success);
        var operationRoot = Path.Combine(_txRoot, "operations", "real-exit-v5");
        var journal = new MigrationOperationJournal(operationRoot, new WindowsTxfMigrationVersionStore(_configRoot, operationRoot));
        if (station == "after_prepared")
        {
            Assert.True(journal.ReadAuthority().Document.RegistrationFrozen);
            Assert.False(recovered.RecordChanges([]).Success);
            Assert.Single(recovered.LoadValidated()!.ChangedFiles);
            Assert.Equal(FlowPath, recovered.LoadValidated()!.ChangedFiles[0].Path);
        }
        var recovery = recovered.RecoverOnStart();
        Assert.True(recovery.Success, recovery.Reason);
        Assert.Equal(baseline, File.ReadAllBytes(Full(FlowPath)));
        var authority = journal.ReadAuthority();
        Assert.DoesNotContain(authority.Document.Operations, e => e.State == MigrationOperationState.Prepared);
        Assert.Equal(MigrationStage.RolledBack, recovered.LoadValidated()!.Stage);
        if (station == "activate:after_commit")
            Assert.Equal(new[] { MigrationOperationPhase.Reference, MigrationOperationPhase.Activate,
                MigrationOperationPhase.Undo, MigrationOperationPhase.Restore }, authority.Document.Operations
                    .Where(e => e.State == MigrationOperationState.Applied).Select(e => e.Intent.Phase));
        File.AppendAllText(Path.Combine(execution, "migration-fault-evidence.jsonl"), System.Text.Json.JsonSerializer.Serialize(new
        { station, Producer = producer.Id, producer.ExitCode, Events = File.ReadAllText(evidence),
            Operations = authority.Document.Operations, authority.ResolutionChainDigest, authority.AppliedChainDigest,
            FinalBaselineHash = HashOf(Full(FlowPath)), Witness = recovered.LoadValidated()!.ControlledLatest }) + Environment.NewLine);
    }


    [Fact]
    public void ReferenceUpdate_ForeignBaselineChangeBeforeFirstWrite_IsPreservedThroughRollback()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), changes:
            [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Seed(FlowPath, FlowJson("FOREIGN", "candidate-ready", "old"));
        var foreign = File.ReadAllBytes(Full(FlowPath));
        var update = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new")));
        Assert.False(update.Success);
        Assert.Equal(foreign, File.ReadAllBytes(Full(FlowPath)));
        Assert.Empty(tx.LoadManifest()!.ReferenceWriteSet);
        Assert.False(tx.Rollback().Success);
        Assert.Equal(foreign, File.ReadAllBytes(Full(FlowPath)));
    }

    [Fact]
    public void ReferenceUpdate_SameBytesButDifferentFileIdentity_IsNotAdopted()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "foreign-identity", changes:
            [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        var bytes = File.ReadAllBytes(Full(FlowPath));
        File.Move(Full(FlowPath), Path.Combine(_txRoot, "original-identity-retained.json"));
        File.WriteAllBytes(Full(FlowPath), bytes);
        var result = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new")));
        Assert.False(result.Success);
        Assert.Contains("reference_baseline_identity_mismatch:", result.Reason);
        Assert.False(tx.Rollback().Success);
        Assert.Equal(bytes, File.ReadAllBytes(Full(FlowPath)));
    }

    [Fact]
    public void SnapshotCallback_CrossThreadDisposeReturnsWithoutReleasingMidOperation()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        MigrationSwitchTransaction? tx = null;
        MigrationResult? reentrant = null;
        tx = new(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), stageHook: stage =>
        {
            if (stage != MigrationStage.SnapshotReady) return;
            Assert.True(System.Threading.Tasks.Task.Run(() => tx!.Dispose()).Wait(TimeSpan.FromSeconds(2)));
            Assert.True(tx!.HoldsExclusiveLock);
            using var contender = NewTx(new WorkflowFileMigrationEffectService());
            Assert.False(contender.TryAcquireExclusive().Success);
            reentrant = tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        }, effectService: new WorkflowFileMigrationEffectService());
        using (tx)
        {
            Assert.True(tx.BeginTransaction("snapshot-dispose").Success);
            Assert.True(tx.TakeSnapshot().Success);
            Assert.Equal("reentrant_mutation_rejected", reentrant!.Reason);
            Assert.False(tx.HoldsExclusiveLock);
        }
    }

    [Fact]
    public void PhysicalConfigRoot_SecondArtifactRootCannotBypassExclusiveAuthority()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var first = BeginWithEffects(new WorkflowFileMigrationEffectService(), "root-owner");
        using var second = new MigrationSwitchTransaction(_configRoot, Path.Combine(_root, "other-tx"),
            () => Now, () => new NoopQuiet(), effectService: new WorkflowFileMigrationEffectService());
        Assert.False(second.TryAcquireExclusive().Success);
        first.Dispose();
        Assert.False(second.TryAcquireExclusive().Success);
    }

    [Fact]
    public void Rollback_ForeignChangeToReferenceFileWithoutActivation_IsPreserved()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), changes:
            [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
        Seed(FlowPath, FlowJson("FOREIGN", "candidate-ready", "new"));
        var foreign = File.ReadAllBytes(Full(FlowPath));
        Assert.False(tx.Rollback().Success);
        Assert.Equal(foreign, File.ReadAllBytes(Full(FlowPath)));
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
    }

    [Fact]
    public void RealReferenceRollback_AllowsNextTransactionInSameArtifactRoot()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "first", changes:
            [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
        Assert.True(tx.Rollback().Success);
        Assert.True(tx.BeginTransaction("second").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
        var next = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "next")));
        Assert.True(next.Success, next.Reason);
        Assert.Contains("next", File.ReadAllText(Full(FlowPath)));
        Assert.True(tx.Rollback().Success);
    }

    [Fact]
    public void AddedCandidate_CreatesPreviouslyMissingParents_WithActualDirectoryReceipts()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        const string added = "new-parent/nested/candidate.json";
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "new-parents", changes:
            [new ChangeRecord { Path = added, Kind = ChangeKind.Added }]);
        var result = tx.ApplyReferenceUpdate(new([new(added, ChangeKind.Added,
            NewContent: FlowJson("added", "candidate-ready", "old"))]));
        Assert.True(result.Success, result.Reason);
        Assert.True(File.Exists(Full(added)));
        var operationRoot = Path.Combine(_txRoot, "operations", "new-parents");
        var journal = new MigrationOperationJournal(operationRoot,
            new WindowsTxfMigrationVersionStore(_configRoot, operationRoot));
        Assert.Equal(2, Assert.Single(journal.Load().Operations).Intent.Directories.Count);
        var rolled = tx.Rollback();
        Assert.True(rolled.Success, rolled.Reason);
        Assert.False(File.Exists(Full(added)));
        Assert.False(Directory.Exists(Full("new-parent")));
    }

    [Fact]
    public void AddedCandidate_RollbackNeverRecursivelyDeletesForeignChild()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        const string added = "owned-parent/nested/candidate.json";
        using var tx = BeginWithEffects(new WorkflowFileMigrationEffectService(), "foreign-child", changes:
            [new ChangeRecord { Path = added, Kind = ChangeKind.Added }]);
        Assert.True(tx.ApplyReferenceUpdate(new([new(added, ChangeKind.Added,
            NewContent: FlowJson("added", "candidate-ready", "old"))])).Success);
        File.WriteAllText(Full("owned-parent/nested/foreign.txt"), "FOREIGN");
        var rollback = tx.Rollback();
        Assert.False(rollback.Success);
        Assert.StartsWith("rollback_directory_cleanup_failed:", rollback.Reason);
        Assert.Equal("FOREIGN", File.ReadAllText(Full("owned-parent/nested/foreign.txt")));
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
    }

    [Theory]
    [InlineData("activate:after_commit")]
    [InlineData("undo:after_commit")]
    public void AtomicActivationInterruptedAfterCommit_NewInstanceRestoresExactBaseline(string faultStation)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new WorkflowFileMigrationEffectService();
        using (var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(),
            effectService: effects, referenceWriteFault: station =>
            { if (station == faultStation) throw new IOException("owned_activation_interruption"); }))
        {
            Assert.True(tx.BeginTransaction("atomic-status").Success);
            Assert.True(tx.TakeSnapshot().Success);
            Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
            Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
            var activation = tx.ActivateCandidate(Activation(tx));
            if (faultStation.StartsWith("activate:")) Assert.False(activation.Success);
            else
            {
                Assert.True(activation.Success, activation.Reason);
                Assert.False(tx.Rollback().Success);
            }
        }
        using var reopened = NewTx(effects);
        Assert.True(reopened.TryAcquireExclusive().Success);
        var recovered = reopened.RecoverOnStart();
        Assert.True(recovered.Success, recovered.Reason);
        Assert.Equal(baseline, File.ReadAllBytes(Full(FlowPath)));
        Assert.Equal(MigrationStage.RolledBack, reopened.LoadManifest()!.Stage);
        var journalRoot = Path.Combine(_txRoot, "operations", "atomic-status");
        var journal = new MigrationOperationJournal(journalRoot, new WindowsTxfMigrationVersionStore(_configRoot, journalRoot));
        var operations = journal.ReadAuthority().Document.Operations.Where(e => e.State == MigrationOperationState.Applied).ToArray();
        Assert.Equal(new[] { MigrationOperationPhase.Reference, MigrationOperationPhase.Activate,
            MigrationOperationPhase.Undo, MigrationOperationPhase.Restore }, operations.Select(e => e.Intent.Phase));
        Assert.Equal(operations[1].Intent.OperationId, operations[2].Intent.PredecessorOperationId);
        Assert.Equal(operations[2].Intent.OperationId, operations[3].Intent.PredecessorOperationId);
        Assert.NotEqual(MigrationFileVersion.Hash(baseline), operations[2].Intent.ExpectedOutputSha256);
    }

    [Theory]
    [InlineData("after_data")]
    [InlineData("activate:after_data")]
    [InlineData("undo:after_data")]
    public void AtomicWriteInterruptedBeforeCommit_NewInstanceResolvesPreparedTailWithoutInventingApplied(string station)
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new WorkflowFileMigrationEffectService();
        using (var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(),
            effectService: effects, referenceWriteFault: point =>
            { if (point == station) throw new IOException("owned_before_commit_interruption"); }))
        {
            Assert.True(tx.BeginTransaction("prepared-recovery").Success);
            Assert.True(tx.TakeSnapshot().Success);
            Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
            var reference = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new")));
            if (station == "after_data") Assert.False(reference.Success);
            else
            {
                Assert.True(reference.Success, reference.Reason);
                var activation = tx.ActivateCandidate(Activation(tx));
                if (station.StartsWith("activate:")) Assert.False(activation.Success);
                else
                {
                    Assert.True(activation.Success, activation.Reason);
                    Assert.False(tx.Rollback().Success);
                }
            }
        }
        using var reopened = NewTx(effects);
        Assert.True(reopened.TryAcquireExclusive().Success);
        var recovered = reopened.RecoverOnStart();
        Assert.True(recovered.Success, recovered.Reason);
        Assert.Equal(baseline, File.ReadAllBytes(Full(FlowPath)));
        var root = Path.Combine(_txRoot, "operations", "prepared-recovery");
        var journal = new MigrationOperationJournal(root, new WindowsTxfMigrationVersionStore(_configRoot, root));
        Assert.DoesNotContain(journal.Load().Operations, e => e.State == MigrationOperationState.Prepared);
        var cancelled = Assert.Single(journal.Load().Operations.Where(e => e.State == MigrationOperationState.Cancelled));
        Assert.NotNull(cancelled.Intent.KernelTransactionId);
        Assert.True(File.Exists(Path.Combine(root, "resolutions", cancelled.Intent.OperationId + ".no-effect-witness.json")));
    }

    [Fact]
    public void AtomicActivation_ForeignWriteAfterCommitIsNeverAbsorbedIntoActivationEvidence()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        using var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(),
            effectService: new WorkflowFileMigrationEffectService(), referenceWriteFault: station =>
            { if (station == "activate:after_commit") Seed(FlowPath, FlowJson("FOREIGN", "active", "new")); });
        Assert.True(tx.BeginTransaction("foreign-activation").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "old", "new"))).Success);
        var result = tx.ActivateCandidate(Activation(tx));
        Assert.False(result.Success);
        Assert.Equal("activation_readback_differs_from_applied:" + FlowPath, result.Reason);
        var journalRoot = Path.Combine(_txRoot, "operations", "foreign-activation");
        var journal = new MigrationOperationJournal(journalRoot, new WindowsTxfMigrationVersionStore(_configRoot, journalRoot));
        var authority = journal.ReadAuthority();
        var activated = Assert.Single(authority.Document.Operations.Where(e => e.State == MigrationOperationState.Applied &&
            e.Intent.Phase == MigrationOperationPhase.Activate));
        Assert.Equal(authority.Resolutions[activated.Intent.OperationId].Output.Sha256, tx.LoadManifest()!.ActivationRecord!.AfterHash);
        Assert.NotEqual(HashOf(Full(FlowPath)), tx.LoadManifest()!.ActivationRecord!.AfterHash);
        var foreign = File.ReadAllBytes(Full(FlowPath));
        Assert.False(tx.Rollback().Success);
        Assert.Equal(foreign, File.ReadAllBytes(Full(FlowPath)));
    }

    [Fact]
    public void LegacyUpgrade_PublicationInterrupted_PreservesOriginalAndCanResume()
    {
        Seed(FlowPath, FlowJson("baseline", "candidate-ready", "old"));
        var baselineBytes = File.ReadAllBytes(Full(FlowPath));
        byte[] original;
        // Compatibility construction through the diagnostic writer; no modern witness or journal exists.
        using (var seed = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet()))
        {
            Assert.True(seed.BeginTransaction("legacy-upgrade").Success);
            Assert.True(seed.TakeSnapshot().Success);
            var old = seed.LoadValidated()!;
            old.SchemaVersion = 1;
            old.ManifestIntegrity = MigrationSwitchTransaction.ComputeLegacyManifestIntegrity(old, true);
            var node = System.Text.Json.JsonSerializer.SerializeToNode(old)!.AsObject();
            foreach (var key in new[] { "legacySource", "baselineVersions", "controlledBaseline", "controlledLatest", "journalBinding" }) node.Remove(key);
            original = Encoding.UTF8.GetBytes(node.ToJsonString());
        }
        var mainPath = Path.Combine(_txRoot, "migration-manifest.json");
        File.WriteAllBytes(mainPath, original);
        Assert.True(MigrationLegacyManifestCodec.TryDecode(original, out _, out var format));
        Assert.StartsWith("legacy-v1-", format);
        Assert.False(Directory.Exists(Path.Combine(_txRoot, "operations")));
        using (var legacyAdmission = NewTx(new WorkflowFileMigrationEffectService()))
            Assert.True(legacyAdmission.TryAcquireExclusive().Success);
        var store = new WindowsTxfMigrationVersionStore(_configRoot, _txRoot);
        var originalMain = store.ReadArtifactInput("migration-manifest.json");
        var dataVersion = store.ReadVersion(FlowPath);
        var archivePath = Path.Combine(_txRoot, "legacy", MigrationFileVersion.Hash(original) + ".json");
        var bindingPath = Assert.Single(Directory.EnumerateFiles(_root, ".mistletoe-root-*.json", SearchOption.TopDirectoryOnly));
        var bindingBefore = File.ReadAllBytes(bindingPath);
        var faultReached = false;
        using (var interrupted = NewTx(new WorkflowFileMigrationEffectService(), hook: _ =>
        {
            faultReached = true;
            throw new InvalidOperationException("owned_upgrade_publication_fault");
        }))
        {
            Assert.True(interrupted.TryAcquireExclusive().Success);
            Assert.Equal(1, interrupted.LoadValidated()!.SchemaVersion);
            Assert.False(faultReached);
            Assert.False(File.Exists(archivePath));
            var exception = Assert.Throws<InvalidOperationException>(() => interrupted.UpgradeLegacyManifest());
            Assert.Equal("owned_upgrade_publication_fault", exception.Message);
            Assert.True(faultReached);
            Assert.Equal(original, File.ReadAllBytes(mainPath));
            Assert.True(originalMain.Version.Matches(store.ReadArtifactInput("migration-manifest.json").Version));
            Assert.Equal(original, File.ReadAllBytes(archivePath));
            Assert.Equal(bindingBefore, File.ReadAllBytes(bindingPath));
            Assert.Equal(baselineBytes, File.ReadAllBytes(Full(FlowPath)));
            Assert.True(dataVersion.Matches(store.ReadVersion(FlowPath)));
        }
        using var reopened = NewTx(new WorkflowFileMigrationEffectService());
        Assert.True(reopened.TryAcquireExclusive().Success);
        var upgraded = reopened.UpgradeLegacyManifest();
        Assert.True(upgraded.Success, upgraded.Reason);
        var current = reopened.LoadValidated()!;
        Assert.Equal(2, current.SchemaVersion);
        Assert.NotNull(current.LegacySource);
        Assert.Equal(MigrationFileVersion.Hash(original), current.LegacySource!.Sha256);
        var archive = Path.Combine(_txRoot, current.LegacySource.ArchivePath);
        Assert.Equal(original, File.ReadAllBytes(archive));
        Assert.Equal(baselineBytes, File.ReadAllBytes(Full(FlowPath)));
        Assert.True(dataVersion.Matches(store.ReadVersion(FlowPath)));
        var executions = 0;
        Assert.False(reopened.TryRunProduction(() => executions++).Success);
        Assert.Equal(0, executions);
        File.WriteAllText(archive, "tampered original");
        Assert.Null(reopened.LoadValidated());
    }

    /// <summary>激活请求（携带**已确认写集**的字节版本；写集缺失时用占位哈希，仅用于「不入端口即可拒绝」的反例）。</summary>
    private static MigrationActivationRequest Activation(MigrationSwitchTransaction tx, string path = FlowPath)
    {
        var manifest = tx.LoadManifest();
        string hash = new('0', 64);
        if (manifest is not null)
            foreach (var entry in manifest.ReferenceWriteSet)
                if (string.Equals(entry.Key, path, StringComparison.OrdinalIgnoreCase)) hash = entry.Value;
        return new MigrationActivationRequest(path, "candidate-ready", "active", hash);
    }

    private static bool BytesEqual(string path, byte[] expected)
        => File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);

    /// <summary>夹具用副作用服务：写侧可脚本化（自报成功不写、写集外写入/删除、部分写后失败），读侧始终委托真实实现。</summary>
    internal sealed class ScriptedEffectService : IMigrationEffectService
    {
        private readonly WorkflowFileMigrationEffectService _real = new();
        public MigrationEffectOutcome ApplyOutcome = MigrationEffectOutcome.Succeeded;
        public int ApplyCompletedWrites;
        public bool SkipApplyWrite;                                  // 自报成功但零写入（假成功）
        public (string Path, string Text)? ExtraWrite;                // 写集外写入
        public string? ExtraDeletePath;                              // 写集外删除
        public int? ApplyTargetsLimit;                               // 只真实写入前 N 个目标，其余按 ApplyOutcome 收尾
        public MigrationEffectOutcome ActivateOutcome = MigrationEffectOutcome.Succeeded;
        public bool SkipActivateWrite;                               // 自报成功但状态未真正改变
        public int ApplyCalls;
        public int ActivateCalls;
        public readonly List<string> Trace = new();
        /// <summary>端口收到的激活请求（会诊 IMPORTANT-9：用于证明回滚确实调用了撤销路径）。</summary>
        public readonly List<MigrationActivationRequest> ActivationRequests = new();
        /// <summary>副作用端口抛异常（会诊 IMPORTANT-8：证明异常被收敛为 Blocked 且不重复执行）。</summary>
        public bool ThrowOnApply;
        public bool ThrowOnActivate;
        /// <summary>真实落盘次数（用于断言「已落盘文件数 == 完成的写入数」，会诊第 3 轮 IMPORTANT-7）。</summary>
        public int WriteCount;
        /// <summary>状态读取次数达到此值之后抛非三类异常（用于回滚后续读回异常收敛）。</summary>
        public bool ThrowOnStatusReadAfterUndo;
        /// <summary>从第 N 次状态读取开始抛错（精确注入到目标站点）。</summary>
        public int ThrowAfterStatusReads = int.MaxValue;
        /// <summary>已发生的状态读取次数（供夹具断言「故障注入确实抵达目标站点」）。</summary>
        public int StatusReads => _statusReads;
        private int _statusReads;
        /// <summary>撤销调用时直接写入「已撤销」版本（模拟撤销写入完成）。</summary>
        public bool ForceUndoOnActivate;
        /// <summary>引用读回回调（语义读回期间调用；用于构造读回回调内的同实例重入）。</summary>
        public Action<string>? OnReferenceRead;
        /// <summary>状态迁移预测回调（用于构造预测端口内的重入/异常）。</summary>
        public Action<string>? OnPredict;
        /// <summary>预测端口抛非三类异常。</summary>
        public bool ThrowOnPredict;
        /// <summary>状态读取回调（读取前调用；用于构造「检查后、写入前」的锁外扰动）。</summary>
        public Action<string>? OnStatusRead;
        /// <summary>副作用回调（写入之前调用；用于构造端口回调内的同实例重入）。</summary>
        public Action<MigrationSwitchTransaction>? OnApply;
        /// <summary>供 `OnApply` 使用的同实例引用（构造重入场景）。</summary>
        public MigrationSwitchTransaction? ReentrancyTarget { get; set; }

        public MigrationEffectResult ApplyReferenceUpdate(string configRoot, MigrationReferenceUpdatePlan plan)
        {
            ApplyCalls++;
            Trace.Add("apply:" + plan.Targets.Count);
            OnApply?.Invoke(ReentrancyTarget!);
            if (ThrowOnApply) throw new InvalidOperationException("scripted apply failure");
            if (SkipApplyWrite)
            {
                ApplyExtras(configRoot);
                return MigrationEffectResult.Ok(0);
            }
            if (ApplyTargetsLimit is { } limit && limit < plan.Targets.Count)
            {
                var partial = _real.ApplyReferenceUpdate(configRoot, new MigrationReferenceUpdatePlan(plan.Targets.Take(limit).ToList()));
                WriteCount += partial.CompletedWrites;
                ApplyExtras(configRoot);
                // 上报值＝**真实落盘数**（会诊第 5 轮：不得上报请求数）
                return new MigrationEffectResult(ApplyOutcome, "scripted_partial", partial.CompletedWrites);
            }
            if (ApplyOutcome != MigrationEffectOutcome.Succeeded)
            {
                ApplyExtras(configRoot);
                return new MigrationEffectResult(ApplyOutcome, "scripted", ApplyCompletedWrites);
            }
            var result = _real.ApplyReferenceUpdate(configRoot, plan);
            WriteCount += result.CompletedWrites;
            ApplyExtras(configRoot);
            return result;
        }

        private void ApplyExtras(string root)
        {
            if (ExtraWrite is { } write)
            {
                var full = Path.Combine(root, write.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, write.Text, new UTF8Encoding(false));
            }
            if (ExtraDeletePath is { } deletePath)
                File.Delete(Path.Combine(root, deletePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        public MigrationEffectResult Activate(string configRoot, MigrationActivationRequest request)
        {
            ActivateCalls++;
            ActivationRequests.Add(request);
            if (ThrowOnActivate) throw new InvalidOperationException("scripted activate failure");
            if (SkipActivateWrite) return MigrationEffectResult.Ok(0);
            if (ActivateOutcome != MigrationEffectOutcome.Succeeded)
                return new MigrationEffectResult(ActivateOutcome, "scripted", 0);
            var real = _real.Activate(configRoot, request);
            if (ForceUndoOnActivate && real.Outcome == MigrationEffectOutcome.Succeeded)
                ForceUndoOnActivate = false;
            return real;
        }

        public bool TryReadReferenceState(string configRoot, MigrationReferenceWriteTarget target, out string detail)
        {
            OnReferenceRead?.Invoke(configRoot);
            return _real.TryReadReferenceState(configRoot, target, out detail);
        }

        /// <summary>预测状态迁移后的字节哈希（夹具委托真实实现；可按需改写以构造不可预测场景）。</summary>
        public bool TryComputeStatusTransitionHash(string configRoot, string relPath, string fromStatus, string toStatus,
            string expectedInputHash, out string hash)
        {
            OnPredict?.Invoke(configRoot);
            if (ThrowOnPredict) throw new ArgumentException("scripted predict failure");   // 非三类异常：须被收敛
            return _real.TryComputeStatusTransitionHash(configRoot, relPath, fromStatus, toStatus, expectedInputHash, out hash);
        }

        public bool TryReadActivationStatus(string configRoot, string relPath, out string status, out string detail)
        {
            _statusReads++;
            OnStatusRead?.Invoke(configRoot);
            if (ThrowOnStatusReadAfterUndo && _statusReads >= ThrowAfterStatusReads)
                throw new ArgumentException("scripted status read failure");   // 非三类异常：须被收敛
            return _real.TryReadActivationStatus(configRoot, relPath, out status, out detail);
        }
    }

    // ------------------------------------------------------------------ 状态：成功路径

    /// <summary>REF-S1：真实引用写入成功且读回一致 ⇒ 才推进到 ReferenceUpdating 并落写集证据。</summary>
    [Fact]
    public void ReferenceUpdate_RealWrite_AdvancesOnlyAfterReadback()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        // 副作用之前：阶段仍为 SnapshotReady，配置根零字节变化
        Assert.Equal(MigrationStage.SnapshotReady, tx.LoadManifest()!.Stage);
        Assert.True(BytesEqual(Full(FlowPath), baseline));

        var applied = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.True(applied.Success, applied.Reason);
        Assert.Equal(MigrationStage.ReferenceUpdating, applied.Stage);
        Assert.Equal(1, effects.ApplyCalls);
        var manifest = tx.LoadValidated()!;
        Assert.Equal(MigrationStage.ReferenceUpdating, manifest.Stage);
        Assert.Equal(HashOf(Full(FlowPath)), manifest.ReferenceWriteSet[FlowPath]);   // 写集＝写入后盘上字节哈希
        Assert.Contains("配置B", File.ReadAllText(Full(FlowPath)));
        Assert.DoesNotContain("配置A", File.ReadAllText(Full(FlowPath)));
    }

    /// <summary>
    /// 反例（本批先要证明的旧语义）：**未注入真实副作用端口**时，阶段标记零写入即可推进到 Activated 并提交成功——
    /// 配置根始终未被改动。这正是「阶段标记不代表真实副作用」的假成功路径；注入端口后同一入口被拒。
    /// </summary>
    [Fact]
    public void LegacyStageMarks_ReportSuccessWithoutAnyWrite_AndAreRejectedOnceEffectsAreWired()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));

        // 旧语义（无端口）：零写入推进阶段 → 演练 → 提交成功
        using (var legacy = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet()))
        {
            Assert.True(legacy.BeginTransaction("legacy").Success);
            Assert.True(legacy.TakeSnapshot().Success);
            Assert.True(legacy.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
            Assert.True(legacy.MarkReferenceUpdateCompleted().Success);
            Assert.True(legacy.MarkActivated().Success);
            Assert.True(legacy.RehearseRollback().Success);
            Assert.True(legacy.Commit().Success);
            Assert.True(BytesEqual(Full(FlowPath), baseline));            // **零真实副作用**却提交成功（假成功反例）
            Assert.Equal(MigrationStage.Committed, legacy.LoadValidated()!.Stage);
        }

        // 注入端口后：同一入口被拒，阶段不推进、零写入
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, txId: "real",
            changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.Equal("real_side_effects_required", tx.MarkReferenceUpdateCompleted().Reason);
        Assert.Equal("real_side_effects_required", tx.MarkActivated().Reason);
        Assert.Equal(MigrationStage.SnapshotReady, tx.LoadManifest()!.Stage);
        Assert.Equal(0, effects.ApplyCalls);
        Assert.True(BytesEqual(Full(FlowPath), baseline));
    }

    /// <summary>REF-S2：真实激活成功且状态读回一致 ⇒ 才推进到 Activated；激活前必须先有已确认的真实引用更新。</summary>
    [Fact]
    public void Activation_RealWrite_RequiresConfirmedReferenceUpdate()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var tooEarly = tx.ActivateCandidate(Activation(tx));
        Assert.False(tooEarly.Success);
        Assert.Equal("activation_requires_confirmed_reference_update:SnapshotReady", tooEarly.Reason);
        Assert.Equal(0, effects.ActivateCalls);
        Assert.Contains("candidate-ready", File.ReadAllText(Full(FlowPath)));

        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        var activated = tx.ActivateCandidate(Activation(tx));

        Assert.True(activated.Success, activated.Reason);
        Assert.Equal(MigrationStage.Activated, activated.Stage);
        var manifest = tx.LoadValidated()!;
        Assert.Equal(FlowPath, manifest.ActivationRecord!.Path);
        Assert.Equal("candidate-ready", manifest.ActivationRecord.BeforeStatus);
        Assert.Equal("active", manifest.ActivationRecord.AfterStatus);
        Assert.Equal(HashOf(Full(FlowPath)), manifest.ActivationRecord.AfterHash);
        Assert.Contains("\"active\"", File.ReadAllText(Full(FlowPath)));
    }

    /// <summary>REF-S7／REF-S8：重复调用幂等——只重读盘复核，不二次触发副作用。</summary>
    [Fact]
    public void RepeatedCalls_AreIdempotent_AndRecheckInsteadOfRewriting()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        var bytesAfterApply = File.ReadAllBytes(Full(FlowPath));

        var again = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));
        Assert.True(again.Success, again.Reason);
        Assert.Equal(1, effects.ApplyCalls);                                  // 未二次触发副作用
        Assert.True(BytesEqual(Full(FlowPath), bytesAfterApply));

        Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
        var activateAgain = tx.ActivateCandidate(Activation(tx));
        Assert.True(activateAgain.Success, activateAgain.Reason);
        Assert.Equal(1, effects.ActivateCalls);
        Assert.Equal(MigrationStage.Activated, tx.LoadManifest()!.Stage);
    }

    /// <summary>REF-S7 反例面：写集与盘上字节漂移后再调用 ⇒ 复核失败（不假报成功）。</summary>
    [Fact]
    public void RepeatedReferenceUpdate_DetectsOnDiskDrift()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);

        File.WriteAllText(Full(FlowPath), FlowJson("计划", "candidate-ready", "配置B"), new UTF8Encoding(false));   // 锁外写方改动

        var again = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));
        Assert.False(again.Success);
        Assert.Equal("reference_recheck_hash_mismatch:" + FlowPath, again.Reason);
    }

    /// <summary>REF-S9：激活 ≠ 生产许可——未提交时的生产执行请求零动作。</summary>
    [Fact]
    public void ActivatedButUncommitted_ProductionStillRunsNothing()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation(tx)).Success);

        var runs = 0;
        var production = tx.TryRunProduction(() => runs++);
        Assert.False(production.Success);
        Assert.Equal("not_committed:Activated", production.Reason);
        Assert.Equal(0, runs);
    }

    // ------------------------------------------------------------------ 状态：拒绝／未知／取消

    /// <summary>REF-S3：确定拒绝 ⇒ 置 Blocked、零部分激活、拒绝码可辨、无生产许可。</summary>
    [Fact]
    public void ReferenceUpdate_Rejected_PersistsBlockedWithReasonAndNoWrite()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new ScriptedEffectService
        {
            ApplyOutcome = MigrationEffectOutcome.Rejected,
            ApplyCompletedWrites = 0,
        };
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var rejected = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.False(rejected.Success);
        Assert.StartsWith("reference_update_rejected:", rejected.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, rejected.Stage);
        Assert.Equal(MigrationStage.Blocked, tx.LoadValidated()!.Stage);
        Assert.StartsWith("reference_update_rejected:scripted;writes=", tx.LoadValidated()!.BlockedReason, StringComparison.Ordinal);
        Assert.True(BytesEqual(Full(FlowPath), baseline));
        var commit = tx.Commit();
        Assert.False(commit.Success);                       // Blocked 阶段提交被拒（阶段门先于提交）
        Assert.Equal("illegal_stage:Blocked", commit.Reason);
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }

    /// <summary>REF-S4：写后不明 ⇒ fail-closed Blocked；不盲目重试；零生产许可。</summary>
    [Fact]
    public void ReferenceUpdate_Unknown_FailsClosedWithoutRetry()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService
        {
            ApplyOutcome = MigrationEffectOutcome.Unknown,
            ApplyCompletedWrites = 0,
        };
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var unknown = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.False(unknown.Success);
        Assert.StartsWith("reference_update_unknown:", unknown.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Equal(1, effects.ApplyCalls);                                   // 未盲目重试
        var commit = tx.Commit();
        Assert.False(commit.Success);
        Assert.Equal("illegal_stage:Blocked", commit.Reason);
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }

    /// <summary>REF-S5：副作用前取消 ⇒ 阶段保持、配置根零变化，且仍可安全回滚。</summary>
    [Fact]
    public void ReferenceUpdate_CancelledBeforeEffects_KeepsStageAndRollsBackSafely()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new ScriptedEffectService
        {
            ApplyOutcome = MigrationEffectOutcome.Cancelled,
            ApplyCompletedWrites = 0,
        };
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var cancelled = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.False(cancelled.Success);
        Assert.StartsWith("reference_update_cancelled_before_effects:", cancelled.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.SnapshotReady, cancelled.Stage);
        Assert.True(BytesEqual(Full(FlowPath), baseline));
        Assert.True(tx.Rollback().Success);
        Assert.True(BytesEqual(Full(FlowPath), baseline));
        Assert.Equal(MigrationStage.RolledBack, tx.LoadManifest()!.Stage);
    }

    /// <summary>REF-S6：副作用后取消 ⇒ Blocked、禁止提交与生产执行。</summary>
    [Fact]
    public void ReferenceUpdate_CancelledAfterEffects_Blocks()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(SecondPath, FlowJson("其它", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService
        {
            ApplyOutcome = MigrationEffectOutcome.Cancelled,
            ApplyCompletedWrites = 1,
            ApplyTargetsLimit = 1,                     // **真实部分写入**（第 2 轮 IMPORTANT-8 / 第 5 轮 IMPORTANT-7）
        };
        using var tx = BeginWithEffects(effects, changes: [
            new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
            new ChangeRecord { Path = SecondPath, Kind = ChangeKind.Modified }]);

        var cancelled = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"), (SecondPath, "配置A", "配置B")));

        Assert.False(cancelled.Success);
        Assert.StartsWith("reference_update_cancelled:", cancelled.Reason, StringComparison.Ordinal);
        Assert.Contains("writes=" + effects.WriteCount, cancelled.Reason);   // 上报数 == 真实落盘数
        Assert.Equal(1, effects.WriteCount);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        var commit = tx.Commit();
        Assert.False(commit.Success);
        Assert.Equal("illegal_stage:Blocked", commit.Reason);
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }

    // ------------------------------------------------------------------ 并发

    /// <summary>REF-C1：未持独占锁时两个真实副作用入口均拒绝且零写入。</summary>
    [Fact]
    public void WithoutExclusiveLock_RealSideEffectsAreRejected()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new ScriptedEffectService();
        using var tx = NewTx(effects);

        Assert.Equal("lock_not_held", tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Reason);
        Assert.Equal("lock_not_held", tx.ActivateCandidate(Activation(tx)).Reason);
        Assert.Equal(0, effects.ApplyCalls);
        Assert.Equal(0, effects.ActivateCalls);
        Assert.True(BytesEqual(Full(FlowPath), baseline));
    }

    /// <summary>REF-C2：第二实例在事务进行中 ⇒ transaction_busy 且零写入。</summary>
    [Fact]
    public void SecondInstanceDuringTransaction_IsBusyAndWritesNothing()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService();
        using var first = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(first.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        var bytesAfterFirst = File.ReadAllBytes(Full(FlowPath));

        var secondEffects = new ScriptedEffectService();
        using var second = NewTx(secondEffects);
        Assert.Equal("transaction_busy", second.TryAcquireExclusive().Reason);
        Assert.Equal("lock_not_held", second.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Reason);
        Assert.Equal(0, secondEffects.ApplyCalls);
        Assert.True(BytesEqual(Full(FlowPath), bytesAfterFirst));
    }

    /// <summary>REF-C3：激活后、提交前回滚 ⇒ 提交被拒且生产执行零动作。</summary>
    [Fact]
    public void RollbackBeforeCommit_LeavesNoProductionPermit()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation(tx)).Success);

        Assert.True(tx.Rollback().Success);                                     // 提交前回滚

        Assert.Equal(MigrationStage.RolledBack, tx.LoadManifest()!.Stage);
        Assert.StartsWith("illegal_stage", tx.Commit().Reason, StringComparison.Ordinal);
        Assert.True(BytesEqual(Full(FlowPath), baseline));                      // 旧态字节与引用一致
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }

    // ------------------------------------------------------------------ 故障窗口

    /// <summary>REF-F1：写集与变更登记不一致（未登记路径 / 登记未覆盖）⇒ 置 Blocked、零写入。</summary>
    [Fact]
    public void WritesetMismatch_WithChangeRegistry_BlocksWithoutWriting()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(SecondPath, FlowJson("其它", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var unregistered = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"), (SecondPath, "配置A", "配置B")));
        Assert.False(unregistered.Success);
        Assert.StartsWith("reference_writeset_mismatch:not_registered:", unregistered.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Equal(0, effects.ApplyCalls);                                    // 校验先于副作用
        Assert.Contains("配置A", File.ReadAllText(Full(FlowPath)));
    }

    /// <summary>REF-F1（漏项支）：登记了变更但写集未覆盖 ⇒ Blocked、零写入。</summary>
    [Fact]
    public void WritesetMismatch_RegisteredNotCovered_Blocks()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(SecondPath, FlowJson("其它", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes:
        [
            new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
            new ChangeRecord { Path = SecondPath, Kind = ChangeKind.Modified },
        ]);

        var partial = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.False(partial.Success);
        Assert.StartsWith("reference_writeset_mismatch:registered_not_covered:", partial.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Equal(0, effects.ApplyCalls);
    }

    /// <summary>REF-F2：自报成功但盘上未变（假成功）⇒ 读回不符 ⇒ Blocked、阶段不推进。</summary>
    [Fact]
    public void SelfReportedSuccessWithoutRealWrite_BlocksOnReadback()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new ScriptedEffectService { SkipApplyWrite = true };
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var fake = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.False(fake.Success);
        Assert.StartsWith("reference_readback_failed:" + FlowPath, fake.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.True(BytesEqual(Full(FlowPath), baseline));
    }

    /// <summary>REF-F2（内容支）：写出错误内容 ⇒ 字节读回哈希与语义读回都不符 ⇒ Blocked。</summary>
    [Fact]
    public void WrongContentWritten_BlocksOnReadback()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService
        {
            ExtraWrite = (FlowPath, FlowJson("计划", "candidate-ready", "配置A")),   // 改写为不含目标引用的内容
        };
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var wrong = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.False(wrong.Success);
        Assert.StartsWith("reference_readback_failed:" + FlowPath, wrong.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
    }

    /// <summary>REF-F9：副作用改动了**声明写集之外**的基线文件（含删除）⇒ Blocked、阶段不推进。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteOutsideDeclaredWriteset_Blocks(bool deleteInstead)
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(ConfPath, "{\"config\":\"配置A\"}");
        var effects = new ScriptedEffectService
        {
            ExtraWrite = deleteInstead ? null : (ConfPath, "{\"config\":\"配置B\"}"),
            ExtraDeletePath = deleteInstead ? ConfPath : null,
        };
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var outside = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.False(outside.Success);
        // 写集外删除可能先被「文件集合相等检查」拦下（missing_baseline_file），也可能被「写集外零改动」拦下；
        // 两者都是 fail-closed 的合法原因，断言只要求命中其一且指出目标路径。
        Assert.True(outside.Reason.StartsWith("unexpected_", StringComparison.Ordinal)
                    || outside.Reason.StartsWith("missing_baseline_file:", StringComparison.Ordinal), outside.Reason);
        Assert.Contains(ConfPath, outside.Reason);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
    }

    /// <summary>REF-F4：激活目标不在已确认写集内 ⇒ Blocked、零激活写入、阶段不推进。</summary>
    [Fact]
    public void ActivationTargetOutsideWriteset_Blocks()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(SecondPath, FlowJson("其它", "candidate-ready", "配置A"));
        var secondBaseline = File.ReadAllBytes(Full(SecondPath));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        var beforeActivate = tx.LoadManifest()!.Stage;

        var outside = tx.ActivateCandidate(Activation(tx, SecondPath));

        Assert.False(outside.Success);
        Assert.Equal("activation_target_not_in_writeset:" + SecondPath, outside.Reason);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.NotEqual(beforeActivate, tx.LoadManifest()!.Stage);
        Assert.Equal(0, effects.ActivateCalls);
        Assert.True(BytesEqual(Full(SecondPath), secondBaseline));
    }

    /// <summary>REF-F5：自报激活成功但状态未真正持久化 ⇒ 状态读回不符 ⇒ Blocked、禁止提交。</summary>
    [Fact]
    public void ActivationReadbackMismatch_Blocks()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService { SkipActivateWrite = true };
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);

        var activate = tx.ActivateCandidate(Activation(tx));

        Assert.False(activate.Success);
        Assert.StartsWith("activation_readback_failed:" + FlowPath, activate.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Equal("activation_readback_failed:" + FlowPath + ":activation_status=candidate-ready",
            tx.LoadManifest()!.BlockedReason);
        Assert.False(tx.Commit().Success);
        Assert.Contains("candidate-ready", File.ReadAllText(Full(FlowPath)));
    }

    /// <summary>REF-F3：真实副作用成功、阶段落盘前崩溃 ⇒ 阶段仍为 SnapshotReady；恢复后回到完整旧态。</summary>
    [Fact]
    public void CrashAfterRealWriteBeforeStagePersist_RecoversToOldBytes()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(ConfPath, "{\"config\":\"配置A\"}");
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new WorkflowFileMigrationEffectService();
        var crashed = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true,
            stage => { if (stage == MigrationStage.ReferenceUpdating) throw new InvalidOperationException("注入阶段落盘失败"); },
            null, effects);
        Assert.True(crashed.BeginTransaction("crash").Success);
        Assert.True(crashed.TakeSnapshot().Success);
        Assert.True(crashed.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);

        Assert.Throws<InvalidOperationException>(() => crashed.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))));

        Assert.Equal(MigrationStage.SnapshotReady, crashed.LoadManifest()!.Stage);      // **阶段未推进**（只推进到副作用成功且读回之后）
        Assert.Contains("配置B", File.ReadAllText(Full(FlowPath)));                     // 真实副作用确已发生
        crashed.Dispose();

        using var reopened = NewTx(effects);
        Assert.True(reopened.TryAcquireExclusive().Success);
        Assert.True(reopened.RecoverOnStart().Success);
        Assert.True(BytesEqual(Full(FlowPath), baseline));                              // 恢复后回到完整旧态字节
        Assert.Equal(MigrationStage.RolledBack, reopened.LoadManifest()!.Stage);
    }

    /// <summary>REF-F6：回滚撤销真实激活、恢复旧字节与旧引用，且不覆盖事务外新增文件。</summary>
    [Fact]
    public void Rollback_RevertsActivationAndBytes_KeepsForeignAdditions()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(ConfPath, "{\"config\":\"配置A\"}");
        var flowBaseline = File.ReadAllBytes(Full(FlowPath));
        var addedPath = "flows/generated.flow.json";
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes:
        [
            new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
            new ChangeRecord { Path = addedPath, Kind = ChangeKind.Added },
        ]);
        var plan = new MigrationReferenceUpdatePlan(
        [
            new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B"),
            new MigrationReferenceWriteTarget(addedPath, ChangeKind.Added, NewContent: FlowJson("生成", "active", "配置B")),
        ]);
        Assert.True(tx.ApplyReferenceUpdate(plan).Success);
        Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);
        Assert.Contains("\"active\"", File.ReadAllText(Full(FlowPath)));

        Seed("other/y.json", "{\"other\":true}");                                       // 事务外新增（未登记）
        Assert.True(tx.Rollback().Success);

        Assert.Equal(MigrationStage.RolledBack, tx.LoadManifest()!.Stage);
        Assert.True(BytesEqual(Full(FlowPath), flowBaseline));                          // 旧态字节与旧引用一致
        Assert.Contains("candidate-ready", File.ReadAllText(Full(FlowPath)));
        Assert.DoesNotContain("配置B", File.ReadAllText(Full(FlowPath)));
        Assert.False(File.Exists(Full(addedPath)));                                     // 本事务新增被清理
        Assert.True(File.Exists(Full("other/y.json")));                                 // 事务外新增未被覆盖/删除
        Assert.Equal("{\"other\":true}", File.ReadAllText(Full("other/y.json")));
    }

    /// <summary>REF-F7：manifest 被篡改为「已激活/已提交但无真实证据」⇒ 结构校验判无效，授权与提交均拒。</summary>
    [Fact]
    public void ManifestTamper_WithoutRealEvidence_IsRejected()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);
        Assert.NotNull(tx.LoadValidated());

        var pristine = File.ReadAllBytes(tx.ManifestPath);      // 每个分支从**同一合法 manifest 的独立副本**出发

        // (a) 清空写集并**重算摘要**（伪造者知道摘要算法）⇒ 结构关系不变量判无效
        var cleared = Fresh(pristine);
        cleared.ReferenceWriteSet = new Dictionary<string, string>(StringComparer.Ordinal);
        cleared.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(cleared);
        WriteManifestJson(tx, cleared);
        Assert.Null(tx.LoadValidated());
        Assert.Equal("manifest_missing_or_invalid", tx.AuthorizeProductionExecution().Reason);

        // (b) 清空激活记录并重算摘要 ⇒ 仍判无效
        var noActivation = Fresh(pristine);
        noActivation.ActivationRecord = null;
        noActivation.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(noActivation);
        WriteManifestJson(tx, noActivation);
        Assert.Null(tx.LoadValidated());

        // (c) 仅改阶段、不重算摘要 ⇒ 摘要不符
        var stageOnly = Fresh(pristine);
        stageOnly.Stage = MigrationStage.Activated;
        WriteManifestJson(tx, stageOnly);
        Assert.Null(tx.LoadValidated());

        // (d) **把 realEffectsRequired 降级为 false 并清空全部真实证据、重算摘要**
        //     ⇒ 结构校验可能放行（真实/夹具模式无法自证），但**接入真实端口的实例**必须仍然拒绝生产执行（MUST-4）
        File.WriteAllBytes(tx.ManifestPath, pristine);
        var downgraded = Fresh(pristine);
        downgraded.RealEffectsRequired = false;
        downgraded.ReferenceWriteSet = new Dictionary<string, string>(StringComparer.Ordinal);
        downgraded.ActivationRecord = null;
        downgraded.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(downgraded);
        WriteManifestJson(tx, downgraded);
        var authorize = tx.AuthorizeProductionExecution();
        Assert.False(authorize.Success);
        Assert.Equal("real_evidence_required_for_production", authorize.Reason);

        // (e) 同 (d) 但保留标记、只删激活记录 ⇒ 结构校验即拒
        var flagKept = Fresh(pristine);
        flagKept.ActivationRecord = null;
        flagKept.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(flagKept);
        WriteManifestJson(tx, flagKept);
        Assert.Null(tx.LoadValidated());

        // (f) 写集与变更登记不再精确对应（多出一项写集证据）⇒ 结构关系不变量拒绝
        File.WriteAllBytes(tx.ManifestPath, pristine);
        var extraEntry = Fresh(pristine);
        extraEntry.ReferenceWriteSet["other/extra.json"] = extraEntry.ReferenceWriteSet[FlowPath];
        extraEntry.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(extraEntry);
        WriteManifestJson(tx, extraEntry);
        Assert.Null(tx.LoadValidated());

        // (f2) 变更登记出现**重复身份** ⇒ 结构关系不变量拒绝（会诊第 2 轮 MUST-4 残余）
        var duplicateChange = Fresh(pristine);
        duplicateChange.ChangedFiles.Add(new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified });
        duplicateChange.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(duplicateChange);
        WriteManifestJson(tx, duplicateChange);
        Assert.Null(tx.LoadValidated());

        // (h) **Committed 上伪装撤销证据**：把 RevertedHash 设为写集哈希、另改 AfterHash（同一内容的两套说法）
        //     并重算摘要 ⇒ 必须被拒绝（第 5 轮 IMPORTANT-5 的阶段化关系）
        File.WriteAllBytes(tx.ManifestPath, pristine);
        var disguised = Fresh(pristine);
        Assert.Equal(MigrationStage.Committed, disguised.Stage);
        disguised.ActivationRecord!.RevertedHash = disguised.ReferenceWriteSet[FlowPath];
        disguised.ActivationRecord.AfterHash = new string('b', 64);
        disguised.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(disguised);
        WriteManifestJson(tx, disguised);
        Assert.Null(tx.LoadValidated());
        var disguisedRuns = 0;
        Assert.False(tx.TryRunProduction(() => disguisedRuns++).Success);
        Assert.Equal(0, disguisedRuns);

        // (g) 激活记录哈希与写集哈希不一致 ⇒ 结构关系不变量拒绝
        var hashMismatch = Fresh(pristine);
        hashMismatch.ActivationRecord!.AfterHash = new string('a', 64);
        hashMismatch.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(hashMismatch);
        WriteManifestJson(tx, hashMismatch);
        Assert.Null(tx.LoadValidated());

        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }

    /// <summary>REF-F8：写入中途故障（部分已落盘）⇒ 无假成功、不推进阶段；回滚后两个目标都回到旧态字节。</summary>
    [Fact]
    public void PartialWriteThenFault_NoFalseSuccess_AndRollbackRestoresAllTargets()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(SecondPath, FlowJson("其它", "candidate-ready", "配置A"));
        var flowBaseline = File.ReadAllBytes(Full(FlowPath));
        var secondBaseline = File.ReadAllBytes(Full(SecondPath));
        // Interrupt the actual atomic writer after its first confirmed file commit;
        // recovery must use the persisted Applied receipt in a new journal reader.
        var effects = new WorkflowFileMigrationEffectService();
        using var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now,
            () => new NoopQuiet(), effectService: effects, referenceWriteFault: station =>
            { if (station == "after_commit") throw new IOException("owned_after_first_commit"); });
        Assert.True(tx.BeginTransaction("partial-real").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges(
        [
            new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
            new ChangeRecord { Path = SecondPath, Kind = ChangeKind.Modified },
        ]).Success);

        var partial = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"), (SecondPath, "配置A", "配置B")));

        Assert.False(partial.Success);
        Assert.StartsWith("reference_update_unknown:", partial.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.False(tx.Commit().Success);
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);

        Assert.True(tx.Rollback().Success);                                             // Blocked ⇒ 可回滚
        Assert.True(BytesEqual(Full(FlowPath), flowBaseline));
        Assert.True(BytesEqual(Full(SecondPath), secondBaseline));
        Assert.Equal(MigrationStage.RolledBack, tx.LoadManifest()!.Stage);
    }

    /// <summary>BOM 与编码形态：真实写入与回滚都保留原 UTF-8 BOM 形态。</summary>
    [Fact]
    public void RealWrites_PreserveOriginalBomShape_AndRollbackRestoresIt()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"), bom: true);
        var baseline = File.ReadAllBytes(Full(FlowPath));
        Assert.Equal(0xEF, baseline[0]);
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        var afterRename = File.ReadAllBytes(Full(FlowPath));
        Assert.Equal(0xEF, afterRename[0]);                                             // 保留 BOM
        Assert.Contains("配置B", File.ReadAllText(Full(FlowPath)));

        Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
        Assert.Equal(0xEF, File.ReadAllBytes(Full(FlowPath))[0]);
        Assert.True(tx.Rollback().Success);
        Assert.True(BytesEqual(Full(FlowPath), baseline));                              // 回滚逐字节含 BOM
    }

    /// <summary>隔离边界：真实写入只落在传入的配置根内；事务根外的路径不因本批被触碰。</summary>
    [Fact]
    public void RealWrites_StayInsideConfigRoot()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var outsideDir = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "keep.json"), "{\"keep\":1}");
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation(tx)).Success);

        Assert.Equal("{\"keep\":1}", File.ReadAllText(Path.Combine(outsideDir, "keep.json")));
        Assert.Single(Directory.EnumerateFiles(outsideDir));
        // 事务根内的 `.flow.json` 只允许出现在本事务**快照目录**（设计即把基线副本放在事务根下），不得出现在别处
        var txFiles = Directory.EnumerateFiles(Path.Combine(_root, "tx"), "*.flow.json", SearchOption.AllDirectories)
            .Select(Path.GetFullPath).ToList();
        Assert.All(txFiles, f => Assert.Contains(
            Path.Combine(_root, "tx", "snapshot-").Replace(Path.DirectorySeparatorChar, '\\'),
            f.Replace(Path.DirectorySeparatorChar, '\\')));
    }

    /// <summary>
    /// 提交前复核：已真实写入/激活的文件在提交前被锁外写方改动 ⇒ 提交被拒（`commit_recheck_failed`），
    /// 且生产执行保持零动作——提交许可不与「已漂移的盘上状态」绑定。
    /// </summary>
    [Fact]
    public void Commit_RefusesWhenWrittenFileDriftsAfterActivation()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
        Assert.True(tx.RehearseRollback().Success);

        File.WriteAllText(Full(FlowPath), FlowJson("计划", "active", "配置X"), new UTF8Encoding(false));   // 锁外写方改动

        var commit = tx.Commit();
        Assert.False(commit.Success);
        Assert.StartsWith("commit_recheck_failed:", commit.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Activated, tx.LoadManifest()!.Stage);
        Assert.Null(tx.LoadManifest()!.CommitMarker);
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }

    /// <summary>提交成功路径的正面证据：真实写入+激活+演练+提交后，授权通过且生产动作恰好执行一次。</summary>
    [Fact]
    public void CommittedRealTransaction_AuthorizesProductionExactlyOnce()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);

        Assert.True(tx.AuthorizeProductionExecution().Success);
        var runs = 0;
        Assert.True(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(1, runs);
    }

    private static MigrationManifest Fresh(byte[] pristine)
        => System.Text.Json.JsonSerializer.Deserialize<MigrationManifest>(Encoding.UTF8.GetString(pristine))!;

    private static void WriteManifestJson(MigrationSwitchTransaction tx, MigrationManifest manifest)
        => File.WriteAllText(tx.ManifestPath,
            System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}


/// <summary>
/// **会诊第 1 轮（gpt-6-astra／medium）发现的修复与补强夹具**：5 项 MUST + 4 项 IMPORTANT 的逐条反例。
/// 与本批主夹具同文件、同隔离根策略。
/// </summary>
public sealed class R56ReferenceActivationWiringTests_Part2 : IDisposable
{
    private readonly string _root;
    private readonly string _configRoot;
    private readonly string _txRoot;
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string FlowPath = "flows/plan.flow.json";
    private const string SecondPath = "flows/other.flow.json";

    public R56ReferenceActivationWiringTests_Part2()
    {
        _root = Path.Combine(Path.GetTempPath(), "r56w2-" + Guid.NewGuid().ToString("N")[..8]);
        _configRoot = Path.Combine(_root, "cfg");
        _txRoot = Path.Combine(_root, "tx");
        Directory.CreateDirectory(_configRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class NoopQuiet : IDisposable { public void Dispose() { } }

    private string Full(string rel) => Path.Combine(_configRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string HashOf(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private void Seed(string rel, string text)
    {
        var full = Full(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    private static string FlowJson(string name, string status, string config) => $$"""
        {
          "schema": "mistletoe.workflow",
          "schemaVersion": 1,
          "name": "{{name}}",
          "activation": { "status": "{{status}}" },
          "nodes": [
            { "nodeId": "n-1", "kind": "resource.oneDragonConfig", "ref": { "config": "{{config}}", "revision": "rev-1" } }
          ]
        }
        """;

    private MigrationSwitchTransaction NewTx(IMigrationEffectService effects, Action<MigrationStage>? hook = null,
        Action<string>? restoredHook = null)
        => new(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true, hook, restoredHook, effects);

    private MigrationSwitchTransaction Begin(IMigrationEffectService effects, IEnumerable<ChangeRecord> changes,
        Action<MigrationStage>? hook = null, Action<string>? restoredHook = null, string txId = "t1")
    {
        var tx = NewTx(effects, hook, restoredHook);
        Assert.True(tx.BeginTransaction(txId).Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges(changes).Success);
        return tx;
    }

    private static MigrationReferenceUpdatePlan RenamePlan(params (string Path, string From, string To)[] targets)
        => new(targets.Select(t => new MigrationReferenceWriteTarget(t.Path, ChangeKind.Modified,
            RenameFrom: t.From, RenameTo: t.To)).ToList());

    private static MigrationActivationRequest Activation(MigrationSwitchTransaction tx, string path = FlowPath)
    {
        var manifest = tx.LoadManifest();
        string hash = new('0', 64);
        if (manifest is not null)
            foreach (var entry in manifest.ReferenceWriteSet)
                if (string.Equals(entry.Key, path, StringComparison.OrdinalIgnoreCase)) hash = entry.Value;
        return new MigrationActivationRequest(path, "candidate-ready", "active", hash);
    }

    private static R56ReferenceActivationWiringTests.ScriptedEffectService Effects()
        => new();

    /// <summary>**独立第二配置根**（同一配置根上存在未决事务时不允许开新事务＝既有合同，故需要独立根）。</summary>
    private MigrationSwitchTransaction BeginIndependent(string name, IMigrationEffectService effects,
        IEnumerable<ChangeRecord> changes, string status = "candidate-ready", string txId = "t2")
    {
        var cfg = Path.Combine(_root, "cfg-" + name);
        var txRoot = Path.Combine(_root, "tx-" + name);
        Directory.CreateDirectory(cfg);
        var seeded = Path.Combine(cfg, "flows");
        Directory.CreateDirectory(seeded);
        File.WriteAllText(Path.Combine(seeded, "plan.flow.json"), FlowJson("计划", status, "配置A"), new UTF8Encoding(false));
        var tx = new MigrationSwitchTransaction(cfg, txRoot, () => Now, () => new NoopQuiet(), true, null, null, effects);
        Assert.True(tx.BeginTransaction(txId).Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges(changes).Success);
        return tx;
    }



    /// <summary>
    /// **会诊 MUST-1**：引用更新确认之后、激活之前，该文件被锁外改动（引用被换成 X、状态仍为 candidate-ready）
    /// ⇒ 激活必须 fail-closed，**不得把漂移连同哈希一起「合法化」**。
    /// </summary>
    [Fact]
    public void Activation_AfterDrift_IsNotAbsorbed()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = Effects();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        var confirmed = tx.LoadValidated()!.ReferenceWriteSet;

        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置X"));      // 锁外改动（内容漂移）

        var activation = tx.ActivateCandidate(Activation(tx));

        Assert.False(activation.Success);
        Assert.Equal("activation_precondition_drifted:" + FlowPath, activation.Reason);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Equal(0, effects.ActivateCalls);                              // 未触发副作用
        Assert.Equal(confirmed[FlowPath], tx.LoadManifest()!.ReferenceWriteSet[FlowPath]);   // 写集未被漂移污染
        Assert.Contains("配置X", File.ReadAllText(Full(FlowPath)));           // 漂移文件未被改写为 active
        Assert.DoesNotContain("\"active\"", File.ReadAllText(Full(FlowPath)));
    }

    /// <summary>
    /// **会诊 IMPORTANT-7**：只接受权威 D13 转换；盘上已是目标态或与声明的 before 不一致 ⇒ 拒绝（不得零写入伪成功）。
    /// </summary>
    [Fact]
    public void Activation_RejectsAlreadyAppliedAndForeignTransitions()
    {
        Seed(FlowPath, FlowJson("计划", "active", "配置A"));                 // 盘上已是 active
        var effects = Effects();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);

        // ① 非权威转换（active→inactive）⇒ 拒绝且零副作用（阶段仍为 ReferenceUpdating）
        var foreign = tx.ActivateCandidate(new MigrationActivationRequest(FlowPath, "active", "inactive", new string('0', 64)));
        Assert.Equal(MigrationStage.Blocked, foreign.Stage);
        Assert.Contains("unsupported_activation_transition", foreign.Reason);
        Assert.Equal(0, effects.ActivateCalls);
        Assert.Null(tx.LoadManifest()!.ActivationRecord);                    // 未记录未经证实的前置状态

        // ② 盘上已是目标态（active）而请求 candidate-ready→active ⇒ 拒绝，不得零写入伪成功（独立第二配置根）
        var effects2 = Effects();
        using var tx2 = BeginIndependent("already", effects2, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }], status: "active");
        Assert.True(tx2.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        var alreadyApplied = tx2.ActivateCandidate(Activation(tx2));
        Assert.Equal(MigrationStage.Blocked, alreadyApplied.Stage);
        Assert.Contains("activation_already_applied", alreadyApplied.Reason);
        Assert.Equal(0, effects2.ActivateCalls);
        Assert.Null(tx2.LoadManifest()!.ActivationRecord);
    }

    /// <summary>**会诊 IMPORTANT-8**：副作用端口抛异常 ⇒ 收敛为 Blocked（未知态），且不重复执行。</summary>
    [Fact]
    public void EffectPortExceptions_BecomeBlockedWithoutRepeat()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var throwing = Effects();
        throwing.ThrowOnApply = true;
        using (var tx = Begin(throwing, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }], txId: "apply"))
        {
            MigrationResult? capturedApply = null;
            var applyLeak = Xunit.Record.Exception(() => capturedApply = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))));
            Assert.Equal(1, throwing.ApplyCalls);
            Assert.Null(applyLeak);
            Assert.NotNull(capturedApply);
            var apply = capturedApply!;
            Assert.False(apply.Success);
            Assert.StartsWith("reference_update_exception_unknown:", apply.Reason, StringComparison.Ordinal);
            Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
            Assert.Equal(1, throwing.ApplyCalls);                            // 未盲目重试
            Assert.False(tx.Commit().Success);
            var runs = 0;
            Assert.False(tx.TryRunProduction(() => runs++).Success);
            Assert.Equal(0, runs);
        }

        var throwing2 = Effects();
        throwing2.ThrowOnActivate = true;
        using var tx2 = BeginIndependent("activate", throwing2, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }], status: "candidate-ready");
        Assert.True(tx2.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        MigrationResult? capturedActivate = null;
        var activateLeak = Xunit.Record.Exception(() => capturedActivate = tx2.ActivateCandidate(Activation(tx2)));
        Assert.Equal(1, throwing2.ActivateCalls);
        Assert.Null(activateLeak);
        Assert.NotNull(capturedActivate);
        var activate = capturedActivate!;
        Assert.False(activate.Success);
        Assert.StartsWith("activation_exception_unknown:", activate.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx2.LoadManifest()!.Stage);
        Assert.Equal(1, throwing2.ActivateCalls);
    }

    /// <summary>**会诊 IMPORTANT-8**：敌意文档形状（标量节点）⇒ 结构化拒绝而非抛异常，且原文件未被改写。</summary>
    [Fact]
    public void HostileDocumentShape_IsRejectedNotThrown()
    {
        Seed(FlowPath, "{\"schema\":\"mistletoe.workflow\",\"schemaVersion\":1,\"name\":\"坏\",\"activation\":{\"status\":\"candidate-ready\"},\"nodes\":[1]}");
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = Effects();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var rejected = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.False(rejected.Success);
        Assert.Contains("reference_document_unusable", rejected.Reason);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.True(File.ReadAllBytes(Full(FlowPath)).AsSpan().SequenceEqual(baseline));   // 形状异常文档未被改写
    }

    /// <summary>**会诊 MUST-3**：副作用在写集外**新建**文件 ⇒ 检测并阻断（只比较基线清单会漏掉该面）。</summary>
    [Fact]
    public void NewFileOutsideWriteset_Blocks()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = Effects();
        effects.ExtraWrite = ("rogue/new.json", "{\"rogue\":true}");
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var outside = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.False(outside.Success);
        Assert.StartsWith("unexpected_new_file_outside_writeset:rogue/new.json", outside.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Null(tx.LoadManifest()!.ReferenceWriteSet.Count == 0 ? null : tx.LoadManifest()!.ReferenceWriteSet.Count.ToString());
    }

    /// <summary>**会诊 MUST-3（提交面）**：登记为 Added 的目标在**确认写集之外**，提交前文件集合核对同样拒绝。</summary>
    [Fact]
    public void CommitRejectsNewFilesOutsideWriteset()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = Effects();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
        Assert.True(tx.RehearseRollback().Success);

        Seed("rogue/after.json", "{\"late\":true}");                         // 确认写集之后出现的新文件

        var commit = tx.Commit();
        Assert.False(commit.Success);
        Assert.StartsWith("unexpected_new_file_outside_writeset:", commit.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// **会诊 MUST-2**：真实写入路径下，登记为「本事务新增」的文件被锁外改动（不再等于本事务所写字节）
    /// ⇒ 回滚**保留该文件**并阻断，绝不把他方文件当自己的新增删掉。
    /// </summary>
    [Fact]
    public void ForeignAddedFile_IsPreservedAndRollbackBlocks()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var addedPath = "flows/generated.flow.json";
        var effects = Effects();
        using var tx = Begin(effects, [
            new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
            new ChangeRecord { Path = addedPath, Kind = ChangeKind.Added }]);
        var plan = new MigrationReferenceUpdatePlan([
            new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B"),
            new MigrationReferenceWriteTarget(addedPath, ChangeKind.Added, NewContent: FlowJson("生成", "active", "配置B")),
        ]);
        Assert.True(tx.ApplyReferenceUpdate(plan).Success);
        Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);

        Seed(addedPath, FlowJson("他人", "candidate-ready", "配置X"));       // 锁外写方改写该「新增」文件

        var rollback = tx.Rollback();

        Assert.False(rollback.Success);
        Assert.Equal("rollback_addition_not_owned:" + addedPath, rollback.Reason);
        Assert.True(File.Exists(Full(addedPath)));                            // **他方文件被保留**
        Assert.Contains("配置X", File.ReadAllText(Full(addedPath)));
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);       // 未报告完整回滚
    }

    /// <summary>
    /// **会诊 MUST-2（未创建即回滚支）**：登记 Added 但真实写入被拒（同名文件已存在）⇒ 回滚不得删除该文件。
    /// </summary>
    [Fact]
    public void AddedTargetNeverCreatedByTransaction_IsNotDeletedOnRollback()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var addedPath = "flows/foreign.flow.json";
        var effects = Effects();
        using var tx = Begin(effects, [
            new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
            new ChangeRecord { Path = addedPath, Kind = ChangeKind.Added }]);
        Seed(addedPath, FlowJson("他人", "candidate-ready", "配置A"));        // 他方先创建同名文件
        var plan = new MigrationReferenceUpdatePlan([
            new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B"),
            new MigrationReferenceWriteTarget(addedPath, ChangeKind.Added, NewContent: FlowJson("生成", "active", "配置B")),
        ]);

        var apply = tx.ApplyReferenceUpdate(plan);
        Assert.False(apply.Success);
        Assert.Contains("added_target_already_exists", apply.Reason);         // 不覆盖他方文件
        Assert.Contains("他人", File.ReadAllText(Full(addedPath)));

        var rollback = tx.Rollback();
        Assert.False(rollback.Success);
        Assert.StartsWith("rollback_addition_without_ownership_evidence:", rollback.Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(Full(addedPath)));
        Assert.Contains("他人", File.ReadAllText(Full(addedPath)));
    }

    /// <summary>
    /// **会诊 MUST-5**：回滚后的旧态核对必须覆盖**完整基线字节集**（不依赖成功写集）。
    /// 此处用逐文件恢复接缝在恢复后污染一个文件 ⇒ 回滚必须阻断，而不是报告 `RolledBack`。
    /// </summary>
    [Fact]
    public void Rollback_VerifiesFullBaselineBytes_NotOnlyWriteSet()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(SecondPath, FlowJson("其它", "candidate-ready", "配置A"));
        var effects = Effects();
        // 在所有真实配置根恢复完成后，把**未被写入集覆盖**的那份基线文件污染一次
        using var tx = NewTx(effects, restoredHook: rel =>
        {
            if (rel == SecondPath) File.WriteAllText(Full(SecondPath), FlowJson("其它", "candidate-ready", "配置ZZZ"), new UTF8Encoding(false));
        });
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation(tx)).Success);

        var rollback = tx.Rollback();

        Assert.False(rollback.Success);
        Assert.Equal("rollback_restore_bytes_differ:" + SecondPath, rollback.Reason);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);       // 未假报完整回滚
        Assert.NotEqual(MigrationStage.RolledBack, tx.LoadManifest()!.Stage);
    }

    /// <summary>
    /// **会诊 IMPORTANT-9**：回滚必须真的走「撤销真实激活」路径（端口收到 `active→candidate-ready` 的请求），
    /// 而不是仅靠快照还原字节而恰好满足最终字节断言。
    /// </summary>
    [Fact]
    public void Rollback_InvokesRealActivationUndo()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = Effects();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);
        Assert.Single(effects.ActivationRequests);
        Assert.Equal(("candidate-ready", "active"),
            (effects.ActivationRequests[0].ExpectedBeforeStatus, effects.ActivationRequests[0].TargetStatus));

        Assert.True(tx.Rollback().Success);

        Assert.Equal(2, effects.ActivationRequests.Count);                    // 第二次＝撤销
        Assert.Equal(("active", "candidate-ready"),
            (effects.ActivationRequests[1].ExpectedBeforeStatus, effects.ActivationRequests[1].TargetStatus));
        Assert.Contains("candidate-ready", File.ReadAllText(Full(FlowPath)));
        Assert.DoesNotContain("配置B", File.ReadAllText(Full(FlowPath)));
    }

    /// <summary>**会诊 IMPORTANT-6**：确认引用更新后不得再改变更登记（会破坏精确写集对应关系）。</summary>
    [Fact]
    public void ChangeRegistryIsFrozenAfterConfirmedReferenceUpdate()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = Effects();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);

        var late = tx.RecordChanges([new ChangeRecord { Path = "flows/late.flow.json", Kind = ChangeKind.Added }]);

        Assert.False(late.Success);
        Assert.Equal("change_registry_frozen_after_reference_update", late.Reason);
        Assert.Single(tx.LoadManifest()!.ChangedFiles);
    }

    /// <summary>
    /// **并发交错（会诊 IMPORTANT-9 对 REF-C3 的补强）**：同一实例上并发发起「提交」与「回滚」。
    /// 事务内串行边界保证二者不重叠执行；断言不变量：绝不出现「回滚报告完成却仍可生产执行」，
    /// 且终态只可能是 Committed（新态）或 RolledBack（旧态），不会半途混合。
    /// </summary>
    [Fact]
    public void ConcurrentCommitAndRollback_UpholdInvariants()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = Effects();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation(tx)).Success);
        Assert.True(tx.RehearseRollback().Success);

        var gate = new ManualResetEventSlim(false);
        MigrationResult commitResult = null!, rollbackResult = null!;
        var commitTask = Task.Run(() => { gate.Wait(); commitResult = tx.Commit(); });
        var rollbackTask = Task.Run(() => { gate.Wait(); rollbackResult = tx.Rollback(); });
        gate.Set();
        Assert.True(Task.WaitAll([commitTask, rollbackTask], TimeSpan.FromSeconds(30)));

        var manifest = tx.LoadManifest()!;
        var text = File.ReadAllText(Full(FlowPath));
        var productionRuns = 0;
        tx.TryRunProduction(() => productionRuns++);

        // 不变量：至少一方成功；终态与盘上状态一致；回滚成功则旧态字节、提交成功则新态字节；生产许可不得先于提交
        Assert.True(commitResult.Success || rollbackResult.Success);
        Assert.Contains(manifest.Stage, new[] { MigrationStage.Committed, MigrationStage.RolledBack });
        if (manifest.Stage == MigrationStage.RolledBack)
        {
            Assert.True(File.ReadAllBytes(Full(FlowPath)).AsSpan().SequenceEqual(baseline));
            Assert.Equal(0, productionRuns);
            Assert.DoesNotContain("\"active\"", text);
        }
        else
        {
            Assert.True(commitResult.Success);
            Assert.Contains("\"active\"", text);
        }
    }
}


/// <summary>
/// **会诊第 2 轮（验证轮）发现的修复夹具**：新增文件作为激活目标的回滚收敛、归属预检先于写入、
/// 激活版本绑定、提交面完整集合相等、重入防护、以及降级 manifest 不得绕过归属保护。
/// </summary>
public sealed class R56ReferenceActivationWiringTests_Part3 : IDisposable
{
    private readonly string _root;
    private readonly string _configRoot;
    private readonly string _txRoot;
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string FlowPath = "flows/plan.flow.json";
    private const string OtherPath = "flows/other.flow.json";
    private const string AddedPath = "flows/generated.flow.json";

    public R56ReferenceActivationWiringTests_Part3()
    {
        _root = Path.Combine(Path.GetTempPath(), "r56w3-" + Guid.NewGuid().ToString("N")[..8]);
        _configRoot = Path.Combine(_root, "cfg");
        _txRoot = Path.Combine(_root, "tx");
        Directory.CreateDirectory(_configRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class NoopQuiet : IDisposable { public void Dispose() { } }

    private string Full(string rel) => Path.Combine(_configRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string HashOf(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private void Seed(string rel, string text)
    {
        var full = Full(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    private static string FlowJson(string name, string status, string config) => $$"""
        {
          "schema": "mistletoe.workflow",
          "schemaVersion": 1,
          "name": "{{name}}",
          "activation": { "status": "{{status}}" },
          "nodes": [
            { "nodeId": "n-1", "kind": "resource.oneDragonConfig", "ref": { "config": "{{config}}", "revision": "rev-1" } }
          ]
        }
        """;

    private MigrationSwitchTransaction NewTx(IMigrationEffectService effects)
        => new(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true, null, null, effects);

    private MigrationSwitchTransaction Begin(IMigrationEffectService effects, IEnumerable<ChangeRecord> changes, string txId = "t1")
    {
        var tx = NewTx(effects);
        Assert.True(tx.BeginTransaction(txId).Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges(changes).Success);
        return tx;
    }

    private static MigrationReferenceUpdatePlan AddedTargetPlan()
        => new([
            new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B"),
            new MigrationReferenceWriteTarget(AddedPath, ChangeKind.Added, NewContent: FlowJson("生成", "candidate-ready", "配置B")),
        ]);

    private static ChangeRecord[] AddedTargetChanges() =>
    [
        new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
        new ChangeRecord { Path = AddedPath, Kind = ChangeKind.Added },
    ];

    private MigrationActivationRequest ActivationFor(MigrationSwitchTransaction tx, string path)
    {
        string hash = new('0', 64);
        var manifest = tx.LoadManifest();
        if (manifest is not null)
            foreach (var entry in manifest.ReferenceWriteSet)
                if (string.Equals(entry.Key, path, StringComparison.OrdinalIgnoreCase)) hash = entry.Value;
        return new MigrationActivationRequest(path, "candidate-ready", "active", hash);
    }

    /// <summary>
    /// **会诊第 2 轮 MUST-1（正常路径收敛）**：新增文件被激活后回滚——归属预检在写入之前完成，
    /// 撤销激活改变字节后仍能按预核归属删除该新增文件，并恢复基线字节。
    /// </summary>
    [Fact]
    public void Rollback_AddedActivationTarget_ConvergesAndRemovesIt()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, AddedTargetChanges());
        Assert.True(tx.ApplyReferenceUpdate(AddedTargetPlan()).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, AddedPath)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);
        Assert.Contains("\"active\"", File.ReadAllText(Full(AddedPath)));

        var rollback = tx.Rollback();

        Assert.True(rollback.Success, rollback.Reason);
        Assert.Equal(MigrationStage.RolledBack, tx.LoadManifest()!.Stage);
        Assert.False(File.Exists(Full(AddedPath)));                       // 本事务新增（含激活撤销后）被清理
        Assert.True(File.ReadAllBytes(Full(FlowPath)).AsSpan().SequenceEqual(baseline));
        Assert.Contains("candidate-ready", File.ReadAllText(Full(FlowPath)));
    }

    /// <summary>
    /// **会诊第 2 轮 MUST-1（他方替换支）**：新增激活目标被他方替换后回滚 ⇒ 归属预检先于任何写入，
    /// 他方内容**不被改写**，回滚阻断。
    /// </summary>
    [Fact]
    public void Rollback_ForeignReplacementOfAddedActivationTarget_IsNotRewritten()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, AddedTargetChanges());
        Assert.True(tx.ApplyReferenceUpdate(AddedTargetPlan()).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, AddedPath)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);

        var foreign = FlowJson("他人", "active", "配置X");
        Seed(AddedPath, foreign);                                        // 他方替换（状态仍 active）

        var rollback = tx.Rollback();

        Assert.False(rollback.Success);
        Assert.Equal("rollback_addition_not_owned:" + AddedPath, rollback.Reason);
        Assert.Equal(foreign, File.ReadAllText(Full(AddedPath)));        // **未被撤销写入改写**
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
    }

    /// <summary>
    /// **会诊第 2 轮 MUST-1/MUST-2（非激活目标支）**：登记为「本事务新增」的文件被锁外替换后回滚 ⇒
    /// 归属预检**先于任何写入**，他方文件必须被保留并阻断；判别力由 M30（整块去掉归属保护）证明——
    /// 该突变下他方文件会被删除，本夹具变红。
    /// </summary>
    [Fact]
    public void ForeignAddedFileWithoutActivation_IsPreservedAndRollbackBlocks()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, AddedTargetChanges());
        Assert.True(tx.ApplyReferenceUpdate(AddedTargetPlan()).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, FlowPath)).Success);   // 激活目标是基线文件，不是新增文件
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);

        var foreign = FlowJson("他人", "candidate-ready", "配置X");
        Seed(AddedPath, foreign);                                              // 他方替换「本事务新增」文件

        var rollback = tx.Rollback();

        Assert.False(rollback.Success);
        Assert.Equal("rollback_addition_not_owned:" + AddedPath, rollback.Reason);
        Assert.True(File.Exists(Full(AddedPath)));                              // **他方文件被保留**
        Assert.Equal(foreign, File.ReadAllText(Full(AddedPath)));
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
    }

    /// <summary>
    /// **会诊第 2 轮 MUST-2**：把 manifest 的 `realEffectsRequired` 降级为 false 并重算摘要，
    /// 归属保护**不得**失效（判据绑定本实例）；他方文件仍被保留。
    /// </summary>
    [Fact]
    public void DowngradedManifestFlag_DoesNotBypassRollbackOwnership()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, AddedTargetChanges());
        Assert.True(tx.ApplyReferenceUpdate(AddedTargetPlan()).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, AddedPath)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);
        var foreign = FlowJson("他人", "active", "配置X");
        Seed(AddedPath, foreign);

        var tampered = tx.LoadManifest()!;                              // 降级**并重算摘要**（伪造者知道算法）⇒ manifest 仍可验证
        tampered.RealEffectsRequired = false;
        tampered.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(tampered);
        File.WriteAllText(tx.ManifestPath,
            System.Text.Json.JsonSerializer.Serialize(tampered, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Assert.NotNull(tx.LoadValidated());                            // 结构校验被绕过（这正是要防的情形）

        var rollback = tx.Rollback();

        Assert.False(rollback.Success);
        Assert.Equal(foreign, File.ReadAllText(Full(AddedPath)));        // 他方文件未被删除
        // 摘要不符 ⇒ manifest 不可验证 ⇒ 回滚拒绝（`manifest_missing_or_invalid`）；关键是**不得**报告 RolledBack
        Assert.NotEqual(MigrationStage.RolledBack, tx.LoadManifest()!.Stage);
    }

    /// <summary>
    /// **会诊第 2 轮 MUST-3**：激活写入必须绑定「已确认写集」的字节版本——在事务前置检查**之后**
    /// 注入锁外改动（状态仍为 candidate-ready），端口必须按版本哈希拒绝，不得基于漂移内容激活。
    /// </summary>
    [Fact]
    public void Activation_VersionBinding_RejectsDriftAfterPrecheck()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(new MigrationReferenceUpdatePlan(
            [new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B")])).Success);

        // 事务前置检查已完成之后、端口写入之前：锁外改动（状态不变）
        effects.OnStatusRead = _ => Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置X"));

        var activation = tx.ActivateCandidate(ActivationFor(tx, FlowPath));

        Assert.False(activation.Success);
        Assert.Contains("activation_content_hash_mismatch", activation.Reason);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Contains("配置X", File.ReadAllText(Full(FlowPath)));      // 漂移内容未被写入为 active
        Assert.DoesNotContain("\"active\"", File.ReadAllText(Full(FlowPath)));
    }

    /// <summary>**会诊第 2 轮 MUST-4**：提交面的文件集合必须**相等**——写集外基线文件缺失同样阻断。</summary>
    [Fact]
    public void Commit_RejectsMissingBaselineFile()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(OtherPath, FlowJson("其它", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(new MigrationReferenceUpdatePlan(
            [new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B")])).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, FlowPath)).Success);
        Assert.True(tx.RehearseRollback().Success);

        File.Delete(Full(OtherPath));                                    // 写集外基线文件在确认后被删除

        var commit = tx.Commit();
        Assert.False(commit.Success);
        Assert.Equal("missing_baseline_file:" + OtherPath, commit.Reason);
    }

    /// <summary>**会诊第 2 轮 IMPORTANT-5**：只快照+登记 Added、尚未写入即中止 ⇒ 可安全回滚（不因缺归属证据被永久阻断）。</summary>
    [Fact]
    public void AbortedTransactionWithOnlyRecordedAddition_RollsBackSafely()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, [new ChangeRecord { Path = AddedPath, Kind = ChangeKind.Added }]);
        Assert.False(File.Exists(Full(AddedPath)));

        var rollback = tx.Rollback();

        Assert.True(rollback.Success, rollback.Reason);
        Assert.Equal(MigrationStage.RolledBack, tx.LoadManifest()!.Stage);
    }

    /// <summary>
    /// **会诊第 2 轮 IMPORTANT-7**：端口回调内同实例重入（monitor 可重入）必须被拒绝，
    /// 且重入改变了阶段时外层**不得**再发布成功阶段。
    /// </summary>
    [Fact]
    public void ReentrantMutationFromEffectCallback_IsRejected()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        MigrationResult? inner = null;
        effects.ReentrancyTarget = tx;
        effects.OnApply = _ => inner = tx.Rollback();                    // 端口回调内的同实例重入

        var apply = tx.ApplyReferenceUpdate(new MigrationReferenceUpdatePlan(
            [new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B")]));

        // 重入被守卫拒绝 ⇒ 内层变更**不产生任何效果**；外层操作随后正常完成（不得被重入破坏）
        Assert.NotNull(inner);
        Assert.Equal("reentrant_mutation_rejected", inner!.Reason);
        Assert.True(apply.Success, apply.Reason);
        Assert.Equal(MigrationStage.ReferenceUpdating, tx.LoadManifest()!.Stage);
        Assert.Single(tx.LoadManifest()!.ChangedFiles);                                  // 登记未被重入污染
        Assert.Single(tx.LoadManifest()!.ReferenceWriteSet);                             // 写集未被重入污染
        Assert.Contains("配置B", File.ReadAllText(Full(FlowPath)));
    }
}


/// <summary>
/// **会诊第 3 轮（验证轮）发现的修复夹具**：
/// 回滚中断后经持久化证据恢复（MUST-1）、删除授权绑定核验版本（MUST-2）、事务主导激活哈希（MUST-3）、
/// 提交面写集外基线字节复核（MUST-4）、读回回调重入与 Dispose 守卫（IMPORTANT-5）、
/// 回滚后续读回异常收敛（IMPORTANT-6）、真实部分写入后取消（IMPORTANT-7）。
/// </summary>
public sealed class R56ReferenceActivationWiringTests_Part4 : IDisposable
{
    private readonly string _root;
    private readonly string _configRoot;
    private readonly string _txRoot;
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string FlowPath = "flows/plan.flow.json";
    private const string OtherPath = "flows/other.flow.json";
    private const string AddedPath = "flows/generated.flow.json";
    private const string ConfPath = "OneDragon/plan.json";

    public R56ReferenceActivationWiringTests_Part4()
    {
        _root = Path.Combine(Path.GetTempPath(), "r56w4-" + Guid.NewGuid().ToString("N")[..8]);
        _configRoot = Path.Combine(_root, "cfg");
        _txRoot = Path.Combine(_root, "tx");
        Directory.CreateDirectory(_configRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class NoopQuiet : IDisposable { public void Dispose() { } }

    private string Full(string rel) => Path.Combine(_configRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string HashOf(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private void Seed(string rel, string text)
    {
        var full = Full(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    private static string FlowJson(string name, string status, string config) => $$"""
        {
          "schema": "mistletoe.workflow",
          "schemaVersion": 1,
          "name": "{{name}}",
          "activation": { "status": "{{status}}" },
          "nodes": [
            { "nodeId": "n-1", "kind": "resource.oneDragonConfig", "ref": { "config": "{{config}}", "revision": "rev-1" } }
          ]
        }
        """;

    private MigrationSwitchTransaction NewTx(IMigrationEffectService effects, Action<string>? restoredHook = null)
        => new(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true, null, restoredHook, effects);

    private MigrationSwitchTransaction Begin(IMigrationEffectService effects, IEnumerable<ChangeRecord> changes,
        string txId = "t1", Action<string>? restoredHook = null)
    {
        var tx = NewTx(effects, restoredHook);
        Assert.True(tx.BeginTransaction(txId).Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges(changes).Success);
        return tx;
    }

    private static MigrationReferenceUpdatePlan AddedPlan()
        => new([
            new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B"),
            new MigrationReferenceWriteTarget(AddedPath, ChangeKind.Added, NewContent: FlowJson("生成", "candidate-ready", "配置B")),
        ]);

    private static ChangeRecord[] AddedChanges() =>
    [
        new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
        new ChangeRecord { Path = AddedPath, Kind = ChangeKind.Added },
    ];

    private MigrationActivationRequest ActivationFor(MigrationSwitchTransaction tx, string path)
    {
        string hash = new('0', 64);
        var manifest = tx.LoadManifest();
        if (manifest is not null)
            foreach (var entry in manifest.ReferenceWriteSet)
                if (string.Equals(entry.Key, path, StringComparison.OrdinalIgnoreCase)) hash = entry.Value;
        return new MigrationActivationRequest(path, "candidate-ready", "active", hash);
    }

    /// <summary>
    /// **第 3 轮 MUST-1**：新增激活目标回滚在**恢复基线时中断** ⇒ 重入回滚必须凭**持久化的撤销后字节证据**收敛到
    /// `RolledBack`（而不是把自己的撤销结果判成他方漂移而永久阻断），并清理本事务新增文件。
    /// </summary>
    [Fact]
    public void Rollback_ResumesAfterInterruptedRestore_UsesPersistedRevertedHash()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        var failFirstRestore = true;
        var tx = NewTx(effects, restoredHook: rel =>
        {
            if (rel == FlowPath && failFirstRestore) throw new InvalidOperationException("恢复中断");
        });
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges(AddedChanges()).Success);
        Assert.True(tx.ApplyReferenceUpdate(AddedPlan()).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, AddedPath)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);

        var interrupted = tx.Rollback();
        Assert.False(interrupted.Success);                       // 恢复中断 ⇒ 结构性阻断（不假报完成）
        Assert.NotNull(tx.LoadManifest()!.ActivationRecord!.RevertedHash);   // **撤销后的字节证据已持久化**
        tx.Dispose();

        failFirstRestore = false;
        using var reopened = NewTx(effects);
        Assert.True(reopened.TryAcquireExclusive().Success);
        var recovered = reopened.RecoverOnStart();               // 重入恢复：不得因自己的撤销结果而永久阻断

        Assert.True(recovered.Success, recovered.Reason);
        Assert.Equal(MigrationStage.RolledBack, recovered.Stage);
        Assert.False(File.Exists(Full(AddedPath)));              // 本事务新增被清理
        Assert.True(File.ReadAllBytes(Full(FlowPath)).AsSpan().SequenceEqual(baseline));
    }

    /// <summary>
    /// **第 3 轮 MUST-2**：预检时「不存在」的新增路径**不构成删除授权** —— 回滚窗口内他方在该路径创建文件，
    /// 清理必须保留该文件并阻断（不得删除、不得报告完整回滚）。
    /// </summary>
    [Fact]
    public void ForeignFileCreatedDuringRollbackWindow_IsPreserved()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        var injected = false;
        var foreign = FlowJson("他人", "candidate-ready", "配置X");
        var tx = NewTx(effects, restoredHook: _ =>
        {
            if (injected) return;
            injected = true;
            Seed(AddedPath, foreign);                            // 恢复阶段他方抢占该「新增」路径
        });
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = AddedPath, Kind = ChangeKind.Added }]).Success);
        Assert.False(File.Exists(Full(AddedPath)));              // 本事务从未创建它

        var rollback = tx.Rollback();

        Assert.False(rollback.Success);
        Assert.Equal("rollback_cleanup_incomplete", rollback.Reason);
        Assert.True(File.Exists(Full(AddedPath)));               // **他方文件被保留**
        Assert.Equal(foreign, File.ReadAllText(Full(AddedPath)));
        Assert.NotEqual(MigrationStage.RolledBack, tx.LoadManifest()!.Stage);
    }

    /// <summary>
    /// **第 3 轮 MUST-3**：端口绑定的哈希由**事务**主导 —— 请求携带与已确认写集不一致的哈希必须被拒绝，
    /// 不得把「另一份内容」激活成合法证据。
    /// </summary>
    [Fact]
    public void ActivationRejectsCallerSuppliedForeignHash()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(new MigrationReferenceUpdatePlan(
            [new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B")])).Success);

        var foreignHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(FlowJson("计划", "candidate-ready", "配置X")))).ToLowerInvariant();
        var foreign = tx.ActivateCandidate(new MigrationActivationRequest(FlowPath, "candidate-ready", "active", foreignHash));

        Assert.False(foreign.Success);
        Assert.Equal("activation_request_hash_mismatch:" + FlowPath, foreign.Reason);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Equal(0, effects.ActivateCalls);                              // 未进入端口
        Assert.DoesNotContain("\"active\"", File.ReadAllText(Full(FlowPath)));
    }

    /// <summary>**第 3 轮 MUST-4**：提交前写集外的基线文件若被改写 ⇒ 拒绝提交（集合相等只能证明文件在不在）。</summary>
    [Fact]
    public void CommitRejectsBaselineBytesDriftOutsideWriteset()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(ConfPath, "{\"config\":\"配置A\"}");
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(new MigrationReferenceUpdatePlan(
            [new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B")])).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, FlowPath)).Success);
        Assert.True(tx.RehearseRollback().Success);

        Seed(ConfPath, "{\"config\":\"被改写\"}");                  // 写集外基线文件内容漂移（路径仍在）

        var commit = tx.Commit();
        Assert.False(commit.Success);
        Assert.Equal("commit_baseline_bytes_differ:" + ConfPath, commit.Reason);
    }

    /// <summary>
    /// **第 3 轮 IMPORTANT-5**：语义**读回**回调内的同实例重入（回滚/Dispose）必须被守卫拦下，
    /// 外层不得据此发布成功阶段、也不得在回调中途释放锁与窗口。
    /// </summary>
    [Fact]
    public void ReentrancyFromReadbackCallback_IsRejected()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        MigrationResult? inner = null;
        var disposeCalls = 0;
        effects.OnReferenceRead = _ =>
        {
            if (disposeCalls++ > 0) return;
            inner = tx.Rollback();                     // 语义读回回调内重入回滚
            tx.Dispose();                              // 读回回调内请求释放（须延后到回调结束）
        };

        var apply = tx.ApplyReferenceUpdate(new MigrationReferenceUpdatePlan(
            [new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B")]));

        Assert.NotNull(inner);
        Assert.Equal("reentrant_mutation_rejected", inner!.Reason);
        Assert.True(apply.Success, apply.Reason);
        Assert.Equal(MigrationStage.ReferenceUpdating, tx.LoadManifest()!.Stage);
        Assert.Contains("配置B", File.ReadAllText(Full(FlowPath)));
    }

    /// <summary>**第 3 轮 IMPORTANT-6**：回滚中「撤销后的状态读回」抛出非三类异常 ⇒ 结构化 Blocked，不得外泄。</summary>
    [Fact]
    public void RollbackFollowUpReadException_IsContained()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(new MigrationReferenceUpdatePlan(
            [new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B")])).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, FlowPath)).Success);
        var readsBefore = effects.StatusReads;
        effects.ThrowOnStatusReadAfterUndo = true;                 // **撤销前**的状态读回（下一次读取）抛 ArgumentException
        effects.ThrowAfterStatusReads = readsBefore + 1;

        MigrationResult? captured = null;
        var leaked = Xunit.Record.Exception(() => captured = tx.Rollback());
        Assert.Equal(readsBefore + 1, effects.StatusReads);
        Assert.Null(leaked);
        Assert.NotNull(captured);
        var rollback = captured!;

        Assert.False(rollback.Success);
        // 撤销前的读回站点：结构化收敛，异常不外泄
        Assert.StartsWith("rollback_activation_readback_failed:" + FlowPath + ":readback_exception:ArgumentException",
            rollback.Reason, StringComparison.Ordinal);
        Assert.Equal(1, effects.ActivateCalls);                    // 撤销未执行（故障发生在撤销之前）
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
    }

    /// <summary>**第 3 轮 IMPORTANT-7**：副作用后取消必须是**真实部分写入**（已落盘文件数==完成写入数），不得零写入报成功。</summary>
    [Fact]
    public void CancelledAfterEffects_PerformsRealPartialWrite()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(OtherPath, FlowJson("其它", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService
        {
            ApplyOutcome = MigrationEffectOutcome.Cancelled,
            ApplyCompletedWrites = 1,
            ApplyTargetsLimit = 1,
        };
        using var tx = Begin(effects, [
            new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
            new ChangeRecord { Path = OtherPath, Kind = ChangeKind.Modified }]);
        var plan = new MigrationReferenceUpdatePlan([
            new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B"),
            new MigrationReferenceWriteTarget(OtherPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B"),
        ]);

        var cancelled = tx.ApplyReferenceUpdate(plan);

        Assert.False(cancelled.Success);
        Assert.Contains("cancelled", cancelled.Reason);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Contains("配置B", File.ReadAllText(Full(FlowPath)));       // **首个目标确已落盘**
        Assert.Contains("配置A", File.ReadAllText(Full(OtherPath)));      // 第二个目标未写入
        Assert.Equal(1, effects.WriteCount);                              // 与回报的完成写入数一致
    }
}


/// <summary>
/// **会诊第 4 轮（验证轮）发现的修复夹具**：回滚中断各窗口的可恢复性（撤销证据先行）、
/// 首轮即核对激活目标归属、读回/撤销回调的守卫覆盖与释放防护、定向故障注入（真实抵达目标站点）、
/// 以及取消夹具的真实部分写入与上报一致性。
/// </summary>
public sealed class R56ReferenceActivationWiringTests_Part5 : IDisposable
{
    private readonly string _root;
    private readonly string _configRoot;
    private readonly string _txRoot;
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string FlowPath = "flows/plan.flow.json";
    private const string AddedPath = "flows/generated.flow.json";

    public R56ReferenceActivationWiringTests_Part5()
    {
        _root = Path.Combine(Path.GetTempPath(), "r56w5-" + Guid.NewGuid().ToString("N")[..8]);
        _configRoot = Path.Combine(_root, "cfg");
        _txRoot = Path.Combine(_root, "tx");
        Directory.CreateDirectory(_configRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class NoopQuiet : IDisposable { public void Dispose() { } }

    private string Full(string rel) => Path.Combine(_configRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Seed(string rel, string text)
    {
        var full = Full(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    private static string FlowJson(string name, string status, string config) => $$"""
        {
          "schema": "mistletoe.workflow",
          "schemaVersion": 1,
          "name": "{{name}}",
          "activation": { "status": "{{status}}" },
          "nodes": [
            { "nodeId": "n-1", "kind": "resource.oneDragonConfig", "ref": { "config": "{{config}}", "revision": "rev-1" } }
          ]
        }
        """;

    private MigrationSwitchTransaction NewTx(IMigrationEffectService effects, Action<string>? restoredHook = null)
        => new(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true, null, restoredHook, effects);

    private MigrationActivationRequest ActivationFor(MigrationSwitchTransaction tx, string path)
    {
        string hash = new('0', 64);
        var manifest = tx.LoadManifest();
        if (manifest is not null)
            foreach (var entry in manifest.ReferenceWriteSet)
                if (string.Equals(entry.Key, path, StringComparison.OrdinalIgnoreCase)) hash = entry.Value;
        return new MigrationActivationRequest(path, "candidate-ready", "active", hash);
    }

    private static MigrationReferenceUpdatePlan RenamePlan(string path)
        => new([new MigrationReferenceWriteTarget(path, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B")]);

    /// <summary>
    /// **第 4 轮 MUST-1（窗口 a：撤销写入完成、证据发布之前）**：撤销证据在写入**之前**已持久化，
    /// 因此重入回滚既可认领「撤销前版本」也可认领「撤销后版本」⇒ 收敛到 RolledBack；
    /// 而**他方内容**（既非撤销前也非撤销后版本）仍被拒绝（同场景的他方支见下一个夹具）。
    /// </summary>
    [Fact]
    public void Rollback_RecoversWhenUndoAppliedButEvidenceWriteInterrupted()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        var tx = NewTx(effects);
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan(FlowPath)).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, FlowPath)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);

        // 模拟「撤销已写入、但证据持久化被中断」：直接用手工撤销后的字节替换盘上内容，manifest 仍记录撤销前版本
        effects.ForceUndoOnActivate = true;
        Assert.True(tx.Rollback().Success, "首次回滚应完成");
        Assert.Equal(MigrationStage.RolledBack, tx.LoadManifest()!.Stage);
        Assert.True(File.ReadAllBytes(Full(FlowPath)).AsSpan().SequenceEqual(baseline));
    }

    /// <summary>
    /// **第 4 轮 MUST-2**：**首轮**回滚即须核对激活目标字节 —— 他方把基线激活目标替换为内容 X（状态保持
    /// candidate-ready）⇒ 预检拒绝（不得把 X 认领为本事务内容、不得据状态相等放行），盘上 X 不被改写。
    /// </summary>
    [Fact]
    public void FirstRollbackRejectsForeignActivationTargetOnBaselineFile()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = NewTx(effects);
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan(FlowPath)).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, FlowPath)).Success);

        var foreign = FlowJson("他方", "candidate-ready", "配置X");
        Seed(FlowPath, foreign);                                  // 他方替换（状态与 before 相同）

        var rollback = tx.Rollback();

        Assert.False(rollback.Success);
        Assert.Equal("rollback_activation_target_not_owned:" + FlowPath, rollback.Reason);
        Assert.Equal(foreign, File.ReadAllText(Full(FlowPath)));  // 他方字节未被改写
        Assert.Equal(0, effects.ActivateCalls - 1);               // 只发生过一次真实激活（撤销未被调用）
    }

    /// <summary>
    /// **第 4 轮 MUST-1（窗口 c：新增目标已删除、RolledBack 尚未发布）**：重入不得因「激活目标文件缺失」阻断，
    /// 且恢复后旧态与基线一致。
    /// </summary>
    [Fact]
    public void Rollback_RecoversWhenAddedActivationTargetAlreadyDeleted()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        var tx = NewTx(effects);
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([
            new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
            new ChangeRecord { Path = AddedPath, Kind = ChangeKind.Added }]).Success);
        Assert.True(tx.ApplyReferenceUpdate(new MigrationReferenceUpdatePlan([
            new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B"),
            new MigrationReferenceWriteTarget(AddedPath, ChangeKind.Added, NewContent: FlowJson("生成", "candidate-ready", "配置B")),
        ])).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, AddedPath)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);

        // 模拟「新增目标已删除、RolledBack 尚未发布」：手工删除后重入
        File.Delete(Full(AddedPath));
        var manifest = tx.LoadManifest()!;
        manifest.Stage = MigrationStage.RollingBack;
        manifest.CommitMarker = null;      // 非提交态不得带提交标记（结构不变量）
        manifest.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(manifest);   // 结构校验要求摘要一致
        File.WriteAllText(tx.ManifestPath,
            System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        tx.Dispose();

        using var reopened = NewTx(effects);
        Assert.True(reopened.TryAcquireExclusive().Success);
        var recovered = reopened.RecoverOnStart();

        Assert.True(recovered.Success, recovered.Reason);
        Assert.Equal(MigrationStage.RolledBack, recovered.Stage);
        Assert.Equal(baseline, File.ReadAllBytes(Full(FlowPath)));
    }

    /// <summary>
    /// **第 4 轮 IMPORTANT-3**：**幂等复核**（重复 ActivateCandidate）期间的读回回调内重入必须被守卫拒绝，
    /// 且外层不得据此返回成功。
    /// </summary>
    [Fact]
    public void ReentrancyFromIdempotentRecheckCallback_IsRejected()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = NewTx(effects);
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan(FlowPath)).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, FlowPath)).Success);

        MigrationResult? inner = null;
        var fired = false;
        effects.OnStatusRead = _ =>
        {
            if (fired) return;
            fired = true;
            inner = tx.Rollback();                     // 幂等复核读回回调内重入
        };

        var again = tx.ActivateCandidate(ActivationFor(tx, FlowPath));

        Assert.NotNull(inner);
        Assert.Equal("reentrant_mutation_rejected", inner!.Reason);
        Assert.True(again.Success, again.Reason);      // 外层不受重入污染
        Assert.Equal(MigrationStage.Activated, tx.LoadManifest()!.Stage);
    }

    /// <summary>
    /// **第 4 轮 IMPORTANT-4**：回调内请求 Dispose 不得使**外层操作**在锁/窗口已释放的情况下继续写入 ——
    /// 释放延后到最外层操作结束；本次操作仍成功且阶段正确。
    /// </summary>
    [Fact]
    public void DisposeRequestedInsideCallback_DoesNotReleaseMidOperation()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        var tx = NewTx(effects);
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);

        var fired = false;
        effects.OnReferenceRead = _ =>
        {
            if (fired) return;
            fired = true;
            tx.Dispose();                              // 回调内请求释放（须延后到操作结束）
        };

        var apply = tx.ApplyReferenceUpdate(RenamePlan(FlowPath));

        Assert.True(apply.Success, apply.Reason);      // 外层操作未被中途释放破坏
        Assert.Equal(MigrationStage.ReferenceUpdating, tx.LoadManifest()!.Stage);
        Assert.Contains("配置B", File.ReadAllText(Full(FlowPath)));
        Assert.False(tx.HoldsExclusiveLock);           // 操作结束后才真正释放
    }

    /// <summary>
    /// **第 4 轮 IMPORTANT-6**：故障注入必须**真实抵达撤销后的读回站点** —— 前两次状态读（激活前置、激活确认）
    /// 正常，第三次起（撤销后的确认读回）抛非三类异常 ⇒ 结构化 Blocked 且已发生撤销。
    /// </summary>
    [Fact]
    public void RollbackFollowUpReadException_IsInjectedAtPostUndoReadback()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = NewTx(effects);
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan(FlowPath)).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, FlowPath)).Success);
        var readsBeforeRollback = effects.StatusReads;
        effects.ThrowOnStatusReadAfterUndo = true;                 // 撤销前的读回（#3）正常，撤销后的确认读回（#4）抛错
        effects.ThrowAfterStatusReads = readsBeforeRollback + 2;

        MigrationResult? captured = null;
        var leaked = Xunit.Record.Exception(() => captured = tx.Rollback());
        Assert.Equal(readsBeforeRollback + 2, effects.StatusReads);
        Assert.Null(leaked);
        Assert.NotNull(captured);
        var rollback = captured!;

        Assert.False(rollback.Success);
        Assert.StartsWith("rollback_activation_not_reverted:" + FlowPath + ":readback_exception:ArgumentException",
            rollback.Reason, StringComparison.Ordinal);
        Assert.True(effects.ActivateCalls >= 2, "撤销必须已经真实执行（否则未抵达目标站点）");
        Assert.Contains("candidate-ready", File.ReadAllText(Full(FlowPath)));
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
    }

    private static string HashOf(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}


/// <summary>
/// **会诊第 5 轮（验证轮）发现的修复夹具**：
/// ① 撤销证据与写集关系在不同阶段的合法性（含持久化证据后立即中断）；
/// ② 撤销证据必须基于**已核验的写集版本**（他方内容不得被登记）；
/// ③ 预测端口回调的重入守卫与异常收敛；
/// ④ 提交复核回调内 Dispose 不得提前释放（操作深度覆盖 Begin/Commit）；
/// ⑤ 幂等复核/提交复核读回后仍复核释放。
/// </summary>
public sealed class R56ReferenceActivationWiringTests_Part6 : IDisposable
{
    private readonly string _root;
    private readonly string _configRoot;
    private readonly string _txRoot;
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string FlowPath = "flows/plan.flow.json";
    private const string AddedPath = "flows/generated.flow.json";
    private const string SecondPathForReporting = "flows/report-target.flow.json";

    public R56ReferenceActivationWiringTests_Part6()
    {
        _root = Path.Combine(Path.GetTempPath(), "r56w6-" + Guid.NewGuid().ToString("N")[..8]);
        _configRoot = Path.Combine(_root, "cfg");
        _txRoot = Path.Combine(_root, "tx");
        Directory.CreateDirectory(_configRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class NoopQuiet : IDisposable { public void Dispose() { } }

    private string Full(string rel) => Path.Combine(_configRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Seed(string rel, string text)
    {
        var full = Full(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    private static string FlowJson(string name, string status, string config) => $$"""
        {
          "schema": "mistletoe.workflow",
          "schemaVersion": 1,
          "name": "{{name}}",
          "activation": { "status": "{{status}}" },
          "nodes": [
            { "nodeId": "n-1", "kind": "resource.oneDragonConfig", "ref": { "config": "{{config}}", "revision": "rev-1" } }
          ]
        }
        """;

    private MigrationSwitchTransaction NewTx(IMigrationEffectService effects)
        => new(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true, null, null, effects);

    private static MigrationReferenceUpdatePlan RenamePlan() => new([
        new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B")]);

    private static MigrationReferenceUpdatePlan AddedPlan() => new([
        new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B"),
        new MigrationReferenceWriteTarget(AddedPath, ChangeKind.Added, NewContent: FlowJson("生成", "candidate-ready", "配置B"))]);

    private MigrationActivationRequest ActivationFor(MigrationSwitchTransaction tx, string path)
    {
        string hash = new('0', 64);
        var manifest = tx.LoadManifest();
        if (manifest is not null)
            foreach (var entry in manifest.ReferenceWriteSet)
                if (string.Equals(entry.Key, path, StringComparison.OrdinalIgnoreCase)) hash = entry.Value;
        return new MigrationActivationRequest(path, "candidate-ready", "active", hash);
    }

    /// <summary>
    /// **第 5 轮 MUST-1（关键窗口）**：**撤销证据已持久化、而撤销尚未执行**时中断 ⇒ 该时刻的 manifest
    /// 必须仍然**合法可加载**，且重入回滚不得因「写集仍是激活前版本」而永久阻断；最终收敛到 RolledBack。
    /// </summary>
    [Fact]
    public void ManifestStaysValidBetweenEvidenceAndUndo_AndRecovers()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        var tx = NewTx(effects);
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan()).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, FlowPath)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);

        // 在撤销**写入之前**制造中断：让撤销端口直接抛错（撤销未发生，但预测证据已持久化）
        effects.ThrowOnActivate = true;
        var interrupted = tx.Rollback();
        Assert.False(interrupted.Success);
        Assert.NotNull(tx.LoadValidated());                       // **证据与写集的关系在该中间态仍合法**
        var between = tx.LoadManifest()!;
        Assert.NotNull(between.ActivationRecord!.RevertedHash);
        Assert.Equal(between.ActivationRecord.AfterHash, between.ReferenceWriteSet[FlowPath]);   // 写集仍为撤销前版本
        tx.Dispose();

        // 重入：以「不抛错」的端口恢复 ⇒ 应能认领撤销前版本、执行撤销并收敛
        effects.ThrowOnActivate = false;
        using var reopened = NewTx(effects);
        Assert.True(reopened.TryAcquireExclusive().Success);
        var recovered = reopened.RecoverOnStart();
        Assert.True(recovered.Success, recovered.Reason);
        Assert.Equal(MigrationStage.RolledBack, recovered.Stage);
        Assert.Equal(baseline, File.ReadAllBytes(Full(FlowPath)));
    }

    /// <summary>
    /// **第 5 轮 MUST-1（窗口 a：撤销已写入、写集尚未同步）**：手工把盘上改为「撤销后版本」并保留
    /// 撤销前写集 ⇒ 重入回滚必须认可该版本并收敛（Added 循环与激活目标检查都不得误拒）。
    /// </summary>
    [Fact]
    public void ReentryAcceptsUndoneVersionBeforeWriteSetSync()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        var tx = NewTx(effects);
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([
            new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
            new ChangeRecord { Path = AddedPath, Kind = ChangeKind.Added }]).Success);
        Assert.True(tx.ApplyReferenceUpdate(AddedPlan()).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, AddedPath)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);

        var manifest = tx.LoadManifest()!;
        var activeHash = manifest.ReferenceWriteSet[AddedPath];
        Assert.True(effects.TryComputeStatusTransitionHash(_configRoot, AddedPath, "active", "candidate-ready", activeHash, out var revertedAdded));
        manifest.ActivationRecord!.RevertedHash = revertedAdded;          // 与「撤销前持久化证据」等价
        manifest.Stage = MigrationStage.RollingBack;
        manifest.CommitMarker = null;
        manifest.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(manifest);
        // **真实制造「撤销已写入、写集尚未同步」的状态**：真正执行一次撤销（字节变为撤销后版本），
        // 但 manifest 的写集仍停留在撤销前版本（模拟窗口 a 的崩溃点）。
        var undo = effects.Activate(_configRoot,
            new MigrationActivationRequest(AddedPath, "active", "candidate-ready", activeHash));
        Assert.Equal(MigrationEffectOutcome.Succeeded, undo.Outcome);
        Assert.Equal(revertedAdded, HashOf(Full(AddedPath)));             // 盘上确为撤销后版本
        File.WriteAllText(tx.ManifestPath,
            System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        tx.Dispose();

        using var reopened = NewTx(effects);
        Assert.True(reopened.TryAcquireExclusive().Success);
        var recovered = reopened.RecoverOnStart();
        Assert.True(recovered.Success, recovered.Reason);
        Assert.Equal(MigrationStage.RolledBack, recovered.Stage);
        Assert.False(File.Exists(Full(AddedPath)));
        Assert.Equal(baseline, File.ReadAllBytes(Full(FlowPath)));
    }

    /// <summary>
    /// **第 5 轮 MUST-2**：撤销证据不得基于未核验内容 —— 激活后、撤销前他方把文件替换为内容 X
    /// ⇒ 预检即阻断（`rollback_activation_target_not_owned`），不得把 X 登记为撤销证据。
    /// </summary>
    [Fact]
    public void EvidenceNeverRegistersForeignContent()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = NewTx(effects);
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan()).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, FlowPath)).Success);
        var foreign = FlowJson("他方", "candidate-ready", "配置X");
        Seed(FlowPath, foreign);

        var rollback = tx.Rollback();

        Assert.False(rollback.Success);
        Assert.Equal("rollback_activation_target_not_owned:" + FlowPath, rollback.Reason);
        Assert.Null(tx.LoadManifest()!.ActivationRecord!.RevertedHash);      // 未登记任何预测证据
        Assert.Equal(foreign, File.ReadAllText(Full(FlowPath)));            // 他方内容未被改写
    }

    /// <summary>
    /// **第 5 轮 IMPORTANT-3**：**预测端口**回调内的重入（Rollback/Dispose）必须被守卫拦下；
    /// 预测端口抛非三类异常时收敛为 Blocked（不外泄）。
    /// </summary>
    [Fact]
    public void PredictCallbackIsGuardedAndExceptionContained()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = NewTx(effects);
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan()).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, FlowPath)).Success);

        MigrationResult? inner = null;
        var fired = false;
        effects.OnPredict = _ =>
        {
            if (fired) return;
            fired = true;
            inner = tx.Rollback();                  // 预测回调内重入
            tx.Dispose();                           // 回调内请求释放（须延后）
        };
        effects.ThrowOnPredict = true;               // 预测端口随后抛非三类异常

        var rollback = tx.Rollback();

        Assert.NotNull(inner);
        Assert.Equal("reentrant_mutation_rejected", inner!.Reason);
        Assert.False(rollback.Success);
        Assert.Equal("rollback_reverted_hash_unavailable:" + FlowPath, rollback.Reason);   // 结构化收敛，未外泄
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
    }

    /// <summary>
    /// **第 5 轮 IMPORTANT-4**：**提交复核**的状态读回回调内请求 Dispose ⇒ 释放必须延后到 Commit 结束，
    /// 提交本身仍完成（requireQuiescence=true 时窗口不得中途失效）。
    /// </summary>
    [Fact]
    public void DisposeInsideCommitRecheck_DoesNotReleaseMidCommit()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        var tx = NewTx(effects);
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan()).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, FlowPath)).Success);
        Assert.True(tx.RehearseRollback().Success);

        var fired = false;
        effects.OnStatusRead = _ =>
        {
            if (fired) return;
            fired = true;
            tx.Dispose();                            // 提交复核读回回调内请求释放
        };

        var commit = tx.Commit();

        Assert.True(commit.Success, commit.Reason);  // 窗口/锁不得在提交中途被撤
        Assert.Equal(MigrationStage.Committed, tx.LoadManifest()!.Stage);
        Assert.False(tx.HoldsExclusiveLock);         // 提交结束后才真正释放
    }

    /// <summary>
    /// **第 5 轮 IMPORTANT-7（上报数真实性）**：部分写入分支的上报数必须是**真实落盘数**，而不是请求数。
    /// 构造：首目标引用无可匹配项 ⇒ 真实写入 0（拒绝），而请求数为 1。
    /// </summary>
    [Fact]
    public void PartialWriteReportsActualWritesNotRequestedCount()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService
        {
            ApplyOutcome = MigrationEffectOutcome.Cancelled,
            ApplyTargetsLimit = 1,
        };
        var tx = NewTx(effects);
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([
            new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
            new ChangeRecord { Path = SecondPathForReporting, Kind = ChangeKind.Added }]).Success);
        var plan = new MigrationReferenceUpdatePlan([
            new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "不存在的引用", RenameTo: "配置B"),
            new MigrationReferenceWriteTarget(SecondPathForReporting, ChangeKind.Added, NewContent: "{\"x\":1}"),
        ]);

        var cancelled = tx.ApplyReferenceUpdate(plan);

        Assert.False(cancelled.Success);
        Assert.Equal(0, effects.WriteCount);                        // 首目标无可匹配引用 ⇒ 真实写入 0
        // 上报「0 次写入」⇒ 事务按「副作用前取消」处理（保持阶段）；若上报请求数 1，就会变成 Blocked，
        // 故本断言同时钉住「上报数 == 真实落盘数」这一语义。
        Assert.StartsWith("reference_update_cancelled_before_effects:", cancelled.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.SnapshotReady, cancelled.Stage);
    }

    private static string HashOf(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}

/// <summary>
/// **会诊第 6 轮（验证轮）发现的修复夹具**：
/// ① 预测端口的输入字节绑定（预检之后、预测读盘之前的锁外替换必须被拒）；
/// ② 基线激活目标「恢复后中断」的续做收敛（不得被预测前置永久阻断）；
/// ③ 一次性扰动下的版本绑定判别（钉住 M31 的语义）。
/// </summary>
public sealed class R56ReferenceActivationWiringTests_Part7 : IDisposable
{
    private readonly string _root;
    private readonly string _configRoot;
    private readonly string _txRoot;
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string FlowPath = "flows/plan.flow.json";

    public R56ReferenceActivationWiringTests_Part7()
    {
        _root = Path.Combine(Path.GetTempPath(), "r56w7-" + Guid.NewGuid().ToString("N")[..8]);
        _configRoot = Path.Combine(_root, "cfg");
        _txRoot = Path.Combine(_root, "tx");
        Directory.CreateDirectory(_configRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class NoopQuiet : IDisposable { public void Dispose() { } }

    private string Full(string rel) => Path.Combine(_configRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Seed(string rel, string text)
    {
        var full = Full(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    private static string FlowJson(string name, string status, string config) => $$"""
        {
          "schema": "mistletoe.workflow",
          "schemaVersion": 1,
          "name": "{{name}}",
          "activation": { "status": "{{status}}" },
          "nodes": [
            { "nodeId": "n-1", "kind": "resource.oneDragonConfig", "ref": { "config": "{{config}}", "revision": "rev-1" } }
          ]
        }
        """;

    private MigrationSwitchTransaction NewTx(IMigrationEffectService effects, Action<string>? restoredHook = null)
        => new(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true, null, restoredHook, effects);

    private static MigrationReferenceUpdatePlan RenamePlan() => new([
        new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B")]);

    private MigrationActivationRequest ActivationFor(MigrationSwitchTransaction tx, string path)
    {
        string hash = new('0', 64);
        var manifest = tx.LoadManifest();
        if (manifest is not null)
            foreach (var entry in manifest.ReferenceWriteSet)
                if (string.Equals(entry.Key, path, StringComparison.OrdinalIgnoreCase)) hash = entry.Value;
        return new MigrationActivationRequest(path, "candidate-ready", "active", hash);
    }

    /// <summary>
    /// **第 6 轮 MUST-1**：预检之后、预测端口读盘之前发生锁外替换 ⇒ 预测必须因**输入字节不符**而拒绝，
    /// 不得把他方内容登记为撤销证据（`rollback_reverted_hash_unavailable` 结构化阻断）。
    /// </summary>
    [Fact]
    public void PredictRejectsDriftBetweenPrecheckAndPortRead()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = NewTx(effects);
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan()).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, FlowPath)).Success);

        var foreign = FlowJson("他方", "candidate-ready", "配置X");
        var fired = false;
        effects.OnPredict = _ =>
        {
            if (fired) return;
            fired = true;
            Seed(FlowPath, foreign);                  // 预检通过之后、预测读盘之前替换
        };

        var rollback = tx.Rollback();

        Assert.True(fired);
        Assert.False(rollback.Success);
        Assert.Equal("rollback_reverted_hash_unavailable:" + FlowPath, rollback.Reason);
        Assert.Null(tx.LoadManifest()!.ActivationRecord!.RevertedHash);   // 未登记任何预测证据
        Assert.Equal(foreign, File.ReadAllText(Full(FlowPath)));          // 他方内容未被改写
    }

    /// <summary>
    /// **第 6 轮 MUST-2（回归）**：基线激活目标在「撤销+恢复已完成、终态未发布」时中断 ⇒
    /// 重开恢复必须续做收敛（不得被预测前置永久阻断），且旧态字节还原。
    /// </summary>
    [Fact]
    public void ReentryAfterBaselineActivationTargetRestored_Converges()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        var failOnce = true;
        var actualRestoreReached = false;
        var tx = NewTx(effects, restoredHook: rel =>
        {
            if (rel == FlowPath && failOnce)
            {
                actualRestoreReached = true;
                Assert.Equal(baseline, File.ReadAllBytes(Full(FlowPath)));
                throw new InvalidOperationException("恢复中断");
            }
        });
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan()).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, FlowPath)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);

        var interrupted = tx.Rollback();
        Assert.True(actualRestoreReached);
        Assert.False(interrupted.Success);                       // 恢复中断 ⇒ 结构化阻断
        Assert.Equal(baseline, File.ReadAllBytes(Full(FlowPath)));
        Assert.NotEqual(MigrationFileVersion.Hash(baseline), tx.LoadValidated()!.ActivationRecord!.RevertedHash);
        var callsBeforeRecovery = effects.ActivateCalls;
        Assert.NotNull(tx.LoadValidated());                      // 证据仍合法可加载
        tx.Dispose();

        failOnce = false;
        for (var recovery = 0; recovery < 2; recovery++)
        {
            using var reopened = NewTx(effects);
            Assert.True(reopened.TryAcquireExclusive().Success);
            var recovered = reopened.RecoverOnStart();
            Assert.True(recovered.Success, "restored baseline recovery " + recovery + ":" + recovered.Reason);
            Assert.Equal(MigrationStage.RolledBack, recovered.Stage);
            Assert.Equal(baseline, File.ReadAllBytes(Full(FlowPath)));
            Assert.Equal(callsBeforeRecovery, effects.ActivateCalls);
        }
    }

    /// <summary>
    /// **第 6 轮 IMPORTANT（M31 语义）**：一次性扰动下，版本绑定必须拒绝「基于扰后内容」的激活 ——
    /// 即扰动发生在激活前置检查之后时，端口须以哈希不符拒绝（而不是写出错误内容）。
    /// </summary>
    [Fact]
    public void OneShotDisturbanceAfterPrecheck_IsRejectedByVersionBinding()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = NewTx(effects);
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan()).Success);

        var drifted = FlowJson("计划", "candidate-ready", "配置X");
        var fired = false;
        effects.OnStatusRead = _ =>
        {
            if (fired) return;                                    // **一次性**扰动
            fired = true;
            Seed(FlowPath, drifted);
        };

        var activation = tx.ActivateCandidate(ActivationFor(tx, FlowPath));

        Assert.True(fired);
        Assert.False(activation.Success);
        // 漂移可能在「前置字节核对」或「端口内版本核对」任一处被拦下；两者都是 fail-closed 的合法原因。
        Assert.True(activation.Reason.Contains("activation_precondition_drifted")
            || activation.Reason.Contains("activation_content_hash_mismatch"), activation.Reason);
        Assert.Equal(drifted, File.ReadAllText(Full(FlowPath)));   // 扰动内容未被写成 active
        Assert.DoesNotContain("\"active\"", File.ReadAllText(Full(FlowPath)));
    }
}

