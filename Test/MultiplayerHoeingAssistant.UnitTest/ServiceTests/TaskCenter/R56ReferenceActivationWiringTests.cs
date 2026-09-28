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
            ActivationRequests.Add(request);
            if (ThrowOnActivate) throw new InvalidOperationException("scripted activate failure");
            if (SkipActivateWrite) return MigrationEffectResult.Ok(0);
            if (ActivateOutcome != MigrationEffectOutcome.Succeeded)
                return new MigrationEffectResult(ActivateOutcome, "scripted", 0);
            return _real.Activate(configRoot, request);
        }

        public bool TryReadReferenceState(string configRoot, MigrationReferenceWriteTarget target, out string detail)
            => _real.TryReadReferenceState(configRoot, target, out detail);

        public bool TryReadActivationStatus(string configRoot, string relPath, out string status, out string detail)
        {
            OnStatusRead?.Invoke(configRoot);
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
        var effects = new ScriptedEffectService
        {
            ApplyOutcome = MigrationEffectOutcome.Cancelled,
            ApplyCompletedWrites = 1,
            ApplyTargetsLimit = 1,                     // **真实部分写入**（会诊第 2 轮 IMPORTANT-8：此前未实际写入）
        };
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var cancelled = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.False(cancelled.Success);
        Assert.StartsWith("reference_update_cancelled:", cancelled.Reason, StringComparison.Ordinal);
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
        var effects = new ScriptedEffectService();
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
        var effects = new ScriptedEffectService
        {
            ApplyOutcome = MigrationEffectOutcome.Unknown,
            ApplyTargetsLimit = 1,
        };
        using var tx = BeginWithEffects(effects, changes:
        [
            new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
            new ChangeRecord { Path = SecondPath, Kind = ChangeKind.Modified },
        ]);

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
            var apply = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));
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
        var activate = tx2.ActivateCandidate(Activation(tx2));
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

