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

    private static MigrationActivationRequest Activation(string path = FlowPath)
        => new(path, "candidate-ready", "active");

    private static bool BytesEqual(string path, byte[] expected)
        => File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);

    private static void AssertActivation(ref MigrationManifest? unused) { }

    /// <summary>夹具用副作用服务：写侧可脚本化（自报成功不写、写集外写入/删除、部分写后失败），读侧始终委托真实实现。</summary>
    private sealed class ScriptedEffectService : IMigrationEffectService
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

        public MigrationEffectResult ApplyReferenceUpdate(string configRoot, MigrationReferenceUpdatePlan plan)
        {
            ApplyCalls++;
            Trace.Add("apply:" + plan.Targets.Count);
            if (SkipApplyWrite)
            {
                ApplyExtras(configRoot);
                return MigrationEffectResult.Ok(0);
            }
            if (ApplyTargetsLimit is { } limit && limit < plan.Targets.Count)
            {
                _real.ApplyReferenceUpdate(configRoot, new MigrationReferenceUpdatePlan(plan.Targets.Take(limit).ToList()));
                ApplyExtras(configRoot);
                return new MigrationEffectResult(ApplyOutcome, "scripted_partial", limit);
            }
            if (ApplyOutcome != MigrationEffectOutcome.Succeeded)
            {
                ApplyExtras(configRoot);
                return new MigrationEffectResult(ApplyOutcome, "scripted", ApplyCompletedWrites);
            }
            var result = _real.ApplyReferenceUpdate(configRoot, plan);
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
            if (SkipActivateWrite) return MigrationEffectResult.Ok(0);
            if (ActivateOutcome != MigrationEffectOutcome.Succeeded)
                return new MigrationEffectResult(ActivateOutcome, "scripted", 0);
            return _real.Activate(configRoot, request);
        }

        public bool TryReadReferenceState(string configRoot, MigrationReferenceWriteTarget target, out string detail)
            => _real.TryReadReferenceState(configRoot, target, out detail);

        public bool TryReadActivationStatus(string configRoot, string relPath, out string status, out string detail)
            => _real.TryReadActivationStatus(configRoot, relPath, out status, out detail);
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

        var tooEarly = tx.ActivateCandidate(Activation());
        Assert.False(tooEarly.Success);
        Assert.Equal("activation_requires_confirmed_reference_update:SnapshotReady", tooEarly.Reason);
        Assert.Equal(0, effects.ActivateCalls);
        Assert.Contains("candidate-ready", File.ReadAllText(Full(FlowPath)));

        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        var activated = tx.ActivateCandidate(Activation());

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

        Assert.True(tx.ActivateCandidate(Activation()).Success);
        var activateAgain = tx.ActivateCandidate(Activation());
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
        Assert.True(tx.ActivateCandidate(Activation()).Success);

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
        Assert.Equal("scripted", tx.LoadValidated()!.BlockedReason);
        Assert.True(BytesEqual(Full(FlowPath), baseline));
        Assert.Equal("blocked:scripted", tx.Commit().Reason);
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
        Assert.Equal("blocked:scripted", tx.Commit().Reason);
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
        var effects = new ScriptedEffectService
        {
            ApplyOutcome = MigrationEffectOutcome.Cancelled,
            ApplyCompletedWrites = 1,
        };
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var cancelled = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.False(cancelled.Success);
        Assert.StartsWith("reference_update_cancelled:", cancelled.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Equal("blocked:scripted", tx.Commit().Reason);
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
        Assert.Equal("lock_not_held", tx.ActivateCandidate(Activation()).Reason);
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
        Assert.True(tx.ActivateCandidate(Activation()).Success);

        Assert.True(tx.Rollback().Success);                                     // 提交前回滚

        Assert.Equal(MigrationStage.RolledBack, tx.LoadManifest()!.Stage);
        Assert.StartsWith("illegal_stage", tx.Commit().Reason, StringComparison.Ordinal);
        Assert.True(BytesEqual(Full(FlowPath), baseline));                      // 旧态字节与引用一致
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }
