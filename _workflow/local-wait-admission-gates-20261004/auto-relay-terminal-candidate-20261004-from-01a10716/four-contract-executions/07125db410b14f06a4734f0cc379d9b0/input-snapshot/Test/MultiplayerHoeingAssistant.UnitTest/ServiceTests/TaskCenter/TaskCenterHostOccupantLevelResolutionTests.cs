using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **证据族 ev-1**：`TaskCenterHost.ResolveOccupantLevels`（§24.99 生产接线唯一路径）**宿主分支**夹具——
/// 落位 §24.99 残余表「宿主分支测试证据 ❌ 未覆盖：`ResolveOccupantLevels` 的『租约未组装／非 Valid／
/// 解析未命中／异常』分支只有代码与日志路径，无夹具」。
/// 边界：解析器纯函数已有独立覆盖（`OccupantLevelResolverTests`），本类只钉**宿主包装分支**的合同：
/// ①租约未组装（`_admissionStore` 未装配的接缝态）②非 Valid（Absent/Expired/Corrupt/Unsupported）
/// ③解析未命中（run 记录缺失/操作缺失）④台账读取异常——任一分支 ⇒ 级别事实**保持未知**（不覆盖已有值、
/// 不改占用语义）并留痕；⑤正向解析命中 ⇒ 只填 Tier/Priority，`HighestClass` **无受信来源保持 null**
/// （§24.100 残余 ⑥，生产来源接线未获 owner 放行）。早出分支（非占用／身份不可信）不触台账、不留解析痕。
/// 访问方式：宿主分支为 private，夹具经**反射**调用（零生产可见性改动）；租约存储与运行台账均为真实文件
/// 组件（独立临时目录）；租约时钟经 store 构造注入（Expired 判定确定性，不依赖 sleep）。
/// **R5 判别力**：关键断言经反向突变验证 MUT-EV1-1..4（改坏→红→还原→复绿，日志 `_ev1/ev1_mutation_log.md`）。
/// </summary>
public sealed class TaskCenterHostOccupantLevelResolutionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _arbDir;
    private readonly List<string> _hostLog = [];
    private DateTimeOffset _clock = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    public TaskCenterHostOccupantLevelResolutionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ev1-occ-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _arbDir = Path.Combine(_dir, "arbitration");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── 夹具装置 ────────────────────────────────────────────────────────────────

    private TaskCenterHost MakeHost()
        => new(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog-cache.json"),
            clientAccessor: () => null,
            log: m => _hostLog.Add(m),
            runnerFactory: null,
            readinessOverride: null,
            localExecutionCapability: () => true,
            statusSnapshotProvider: null,
            admissionWired: true,
            arbitrationDir: _arbDir);

    private static void AttachLeaseStore(TaskCenterHost host, ArbitrationLeaseStore store)
        => typeof(TaskCenterHost)
            .GetField("_admissionStore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host, store);

    private static RunStore HostRuns(TaskCenterHost host)
        => (RunStore)typeof(TaskCenterHost)
            .GetField("_runs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(host)!;

    private static RunningOccupantFacts InvokeResolve(TaskCenterHost host, RunningOccupantFacts occupant, ControlStatus? status)
        => (RunningOccupantFacts)typeof(TaskCenterHost)
            .GetMethod("ResolveOccupantLevels", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host, new object?[] { occupant, status })!;

    /// <summary>占用中且身份可核验的占用者事实：**全字段高辨识值**（第 3 轮会诊必改 3）——
    /// 使「宿主自行构造丢字段返回值」类突变对每个字段都可判定（默认值无法识别清空/重置）。</summary>
    private static RunningOccupantFacts OccupiedTrusted(string reference = "ev1-occupied")
        => new()
        {
            State = OccupantFactsState.Occupied,
            HasTrustedIdentity = true,
            ExecutionInstanceId = "11111111-1111-1111-1111-111111111111",
            RunId = "run-ev1-a6",
            JobId = "job-ev1-22222222",
            Kind = "kind-ev1",
            Source = "source-ev1",
            Name = "name-ev1",
            HoeingClass = true,
            Reference = reference,
            StopRequested = true,
            // 预填级别事实（第 5 轮会诊重要 3）：失败分支必须**保留**（不得清空/覆盖）
            Tier = ArbitrationTier.Fixed,
            Priority = 3,
            HighestClass = false,
        };

    /// <summary>无预填级别的占用者输入（生产 FromStatus 同形状：级别恒 null）——正向命中夹具用。</summary>
    private static RunningOccupantFacts OccupiedTrustedNoLevels(string reference = "ev1-occupied-nolvl")
        => new()
        {
            State = OccupantFactsState.Occupied,
            HasTrustedIdentity = true,
            ExecutionInstanceId = "11111111-1111-1111-1111-111111111111",
            RunId = "run-ev1-a6",
            JobId = "job-ev1-22222222",
            Kind = "kind-ev1",
            Source = "source-ev1",
            Name = "name-ev1",
            HoeingClass = true,
            Reference = reference,
            StopRequested = true,
        };

    /// <summary>新鲜状态快照（CurrentExecution setter 只在快照新鲜时保留身份，观测时刻取当下）。</summary>
    private static ControlStatus StatusWithExecution(Guid wireRunId)
        => new()
        {
            TaskRunning = true,
            TaskStatusAvailable = true,
            TaskStatusBgiEpoch = "ev1-epoch",
            TaskStatusObservedAtUtc = DateTimeOffset.Now,
            CurrentExecution = new TaskExecutionIdentitySnapshot(
                Guid.NewGuid(), 1, RunId: wireRunId, JobId: null,
                Kind: "workflow", Source: "ev1", Name: "ev1-flow", StopRequested: false),
        };

    /// <summary>与被测宿主共享时钟的租约库（过期判定确定性）：创建与读取两端用同一 <see cref="_clock"/> 闭包。</summary>
    private ArbitrationLeaseStore NewClockedStore()
        => new(_arbDir, () => _clock);

    /// <summary>推进共享时钟（Expired 夹具用：TTL 默认 15s，推进 2 小时必然过期）。</summary>
    private void AdvanceClockHours(int hours) => _clock = _clock.AddHours(hours);

    private static void AcquireValidLease(ArbitrationLeaseStore store)
    {
        var acq = store.TryAcquire("ev1-owner-epoch");
        Assert.True(acq.Success, "夹具前置失败：租约获取不成功 " + acq.Reason);
    }

    /// <summary>与生产同一 MutateHandoff 串行边界追加流程级登记操作（候选快照 Tier/Priority）。</summary>
    private static void AddFlowRegistrationOp(ArbitrationLeaseStore store, string runBinding, ArbitrationTier tier, int priority)
    {
        var read = store.Read();
        var m = store.MutateHandoff(read.File!.Lease!.LeaseId, read.File.Lease.OwnerEpoch, read.File.Revision, file =>
        {
            file.Handoff ??= new LeaseHandoffSegment();
            file.Handoff.Operations.Add(new OperationRecord
            {
                RequestIdentity = "ev1-req-" + Guid.NewGuid().ToString("N")[..8],
                CandidateId = "ev1-cand-1",
                RequestState = OperationRequestState.Queued,
                Zone = OperationZone.Active,
                RunBinding = runBinding,
                Intent = "start",
                OperationType = OperationType.FlowRegistration,
                Candidate = new ArbitrationCandidate { Tier = tier, Priority = priority },
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedRevision = file.Revision + 1,
            });
            return null;
        });
        Assert.True(m.Success, "夹具前置失败：流程级登记操作写入不成功 " + m.Reason);
    }

    /// <summary>「静默读取」探针（第 1 轮会诊必改 2）：锁文件被测试进程独占持有（FileShare.None）⇒
    /// 任何租约读取都会经历争用预算耗尽并以 Corrupt（fail-closed）返回＋留痕——早出合同（不触台账）下
    /// 读取根本不应发生；「多余读取」类反向突变（MUT-EV1-4）由此必然变红。</summary>
    private IDisposable AttachContendedLease(TaskCenterHost host)
    {
        Directory.CreateDirectory(_arbDir);
        File.WriteAllText(Path.Combine(_arbDir, "arbitration-lease.json"), "{\"version\": 5}");
        var lockPath = Path.Combine(_arbDir, "arbitration-lease.lock");
        File.WriteAllText(lockPath, "ev1-probe");
        var held = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None);
        AttachLeaseStore(host, new ArbitrationLeaseStore(_arbDir));
        return held;
    }

    /// <summary>失败分支合同（第 5 轮会诊重要 3 处置）：**全字段逐项保持**＋已有级别值不被覆盖——
    /// 配合预填级别输入（OccupiedTrusted），「清空已有值/局部丢字段」类突变可判定。</summary>
    private static void AssertLevelUnknown(RunningOccupantFacts result, RunningOccupantFacts original)
    {
        Assert.Equal(original.State, result.State);
        Assert.Equal(original.HasTrustedIdentity, result.HasTrustedIdentity);
        Assert.Equal(original.Reference, result.Reference);
        Assert.Equal(original.ExecutionInstanceId, result.ExecutionInstanceId);
        Assert.Equal(original.RunId, result.RunId);
        Assert.Equal(original.JobId, result.JobId);
        Assert.Equal(original.Kind, result.Kind);
        Assert.Equal(original.Source, result.Source);
        Assert.Equal(original.Name, result.Name);
        Assert.Equal(original.HoeingClass, result.HoeingClass);
        Assert.Equal(original.StopRequested, result.StopRequested);
        Assert.Equal(original.Tier, result.Tier);
        Assert.Equal(original.Priority, result.Priority);
        Assert.Equal(original.HighestClass, result.HighestClass);
    }

    private bool HasResolveLog() => _hostLog.Any(l => l.Contains("占用者级别事实", StringComparison.Ordinal));

    // ── ① 租约未组装（接缝态） ──────────────────────────────────────────────────

    [Fact]
    public void Resolve_AdmissionStoreNotAssembled_KeepsFactsUnchanged_NoResolveLog()
    {
        var host = MakeHost(); // admissionWired=true 但门面未初始化 ⇒ _admissionStore 恒 null（生产接缝态）
        var original = OccupiedTrusted();
        var status = StatusWithExecution(Guid.NewGuid());

        var result = InvokeResolve(host, original, status);

        AssertLevelUnknown(result, original);
        Assert.False(HasResolveLog(), "接缝态分支不得产生解析留痕");
    }

    /// <summary>第 6 轮会诊重要项（原层闭合）：「未组装」合同在**台账读取故障**的交错下也必须成立。
    /// 生产原顺序 `_runs.List()` 先于未组装判定 ⇒ 故障被 catch 误记「解析失败」。本夹具对修复前代码
    /// **天然红**（真实缺陷反例先行），修复（未组装判定前置）后转绿。</summary>
    [Fact]
    public void Resolve_AdmissionStoreNotAssembled_WithFaultyLedger_StillSilent()
    {
        var host = MakeHost(); // _admissionStore 恒 null（未组装）
        var runs = HostRuns(host);
        runs.CreateRun("wf-ev1", "rev-1");
        runs.FileOperationFaultForTest = op =>
            string.Equals(op, "read-list", StringComparison.Ordinal)
                ? new InvalidOperationException("ev1-notassembled-probe：未组装态不应读取运行台账")
                : null;
        var original = OccupiedTrusted();
        try
        {
            var result = InvokeResolve(host, original, StatusWithExecution(Guid.NewGuid()));

            AssertLevelUnknown(result, original);
            Assert.False(HasResolveLog(), "未组装态不得留解析痕（即便台账读取故障）");
        }
        finally
        {
            runs.FileOperationFaultForTest = null;
        }
    }

    // ── ② 非 Valid 四态 ─────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_LeaseAbsent_KeepsUnknownAndLogsStatus()
    {
        var host = MakeHost();
        var store = NewClockedStore();
        AttachLeaseStore(host, store); // 不落任何租约文件 ⇒ Absent
        var original = OccupiedTrusted();

        var result = InvokeResolve(host, original, StatusWithExecution(Guid.NewGuid()));

        AssertLevelUnknown(result, original);
        Assert.Contains(_hostLog, l => l.Contains("租约读取状态=Absent", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_LeaseExpired_KeepsUnknownAndLogsStatus()
    {
        var host = MakeHost();
        var store = NewClockedStore();
        AcquireValidLease(store);
        AttachLeaseStore(host, store);
        var original = OccupiedTrusted();

        AdvanceClockHours(2); // TTL 默认 15s ⇒ 推进 2 小时后读取侧按同一共享时钟判 Expired
        var result = InvokeResolve(host, original, StatusWithExecution(Guid.NewGuid()));

        AssertLevelUnknown(result, original);
        Assert.Contains(_hostLog, l => l.Contains("租约读取状态=Expired", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_LeaseCorrupt_KeepsUnknownAndLogsStatus()
    {
        var host = MakeHost();
        Directory.CreateDirectory(_arbDir);
        File.WriteAllText(Path.Combine(_arbDir, "arbitration-lease.json"), "Not json at all。{ev1");
        AttachLeaseStore(host, new ArbitrationLeaseStore(_arbDir));
        var original = OccupiedTrusted();

        var result = InvokeResolve(host, original, StatusWithExecution(Guid.NewGuid()));

        AssertLevelUnknown(result, original);
        Assert.Contains(_hostLog, l => l.Contains("租约读取状态=Corrupt", StringComparison.Ordinal));
    }

    /// <summary>第 7 轮会诊重要项（原层闭合，与第 6 轮同族）：租约已判非 Valid 时，运行台账故障
    /// 不得把留痕改写成「解析失败」。生产原顺序 `List()` 先于非 Valid 分支 ⇒ 本夹具对修复前代码
    /// **天然红**；修复（`List()` 后移到非 Valid 判定之后）后转绿。</summary>
    [Fact]
    public void Resolve_LeaseNonValid_WithFaultyLedger_KeepsLeaseStatusLog()
    {
        var host = MakeHost();
        Directory.CreateDirectory(_arbDir);
        File.WriteAllText(Path.Combine(_arbDir, "arbitration-lease.json"), "{\"version\": 999}"); // Unsupported
        AttachLeaseStore(host, new ArbitrationLeaseStore(_arbDir));
        var runs = HostRuns(host);
        runs.CreateRun("wf-ev1", "rev-1");
        runs.FileOperationFaultForTest = op =>
            string.Equals(op, "read-list", StringComparison.Ordinal)
                ? new InvalidOperationException("ev1-nonvalid-probe：非 Valid 分支不应再读运行台账")
                : null;
        var original = OccupiedTrusted();
        try
        {
            var result = InvokeResolve(host, original, StatusWithExecution(Guid.NewGuid()));

            AssertLevelUnknown(result, original);
            Assert.Contains(_hostLog, l => l.Contains("租约读取状态=Unsupported", StringComparison.Ordinal));
            Assert.DoesNotContain(_hostLog, l => l.Contains("解析失败", StringComparison.Ordinal));
        }
        finally
        {
            runs.FileOperationFaultForTest = null;
        }
    }

    [Fact]
    public void Resolve_LeaseUnsupported_KeepsUnknownAndLogsStatus()
    {
        var host = MakeHost();
        Directory.CreateDirectory(_arbDir);
        File.WriteAllText(Path.Combine(_arbDir, "arbitration-lease.json"), "{\"version\": 999}");
        AttachLeaseStore(host, new ArbitrationLeaseStore(_arbDir));
        var original = OccupiedTrusted();

        var result = InvokeResolve(host, original, StatusWithExecution(Guid.NewGuid()));

        AssertLevelUnknown(result, original);
        Assert.Contains(_hostLog, l => l.Contains("租约读取状态=Unsupported", StringComparison.Ordinal));
    }

    // ── ③ 解析未命中（租约 Valid 但解析链断） ───────────────────────────────────

    [Fact]
    public void Resolve_RunRecordNotFound_KeepsUnknownAndLogsReference()
    {
        var host = MakeHost();
        var store = NewClockedStore();
        AcquireValidLease(store);
        AttachLeaseStore(host, store);
        var original = OccupiedTrusted();

        var result = InvokeResolve(host, original, StatusWithExecution(Guid.NewGuid()));

        AssertLevelUnknown(result, original);
        Assert.Contains(_hostLog, l => l.Contains("run_record_not_found", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_OperationRecordNotFound_KeepsUnknownAndLogsReference()
    {
        var host = MakeHost();
        var store = NewClockedStore();
        AcquireValidLease(store);
        AttachLeaseStore(host, store);
        var runs = HostRuns(host);
        var rec = runs.CreateRun("wf-ev1", "rev-1"); // 有运行记录、无流程级登记操作
        var original = OccupiedTrusted();

        var result = InvokeResolve(host, original, StatusWithExecution(Guid.Parse(rec.WireRunId)));

        AssertLevelUnknown(result, original);
        Assert.Contains(_hostLog, l => l.Contains("operation_record_not_found", StringComparison.Ordinal));
    }

    // ── ④ 正向解析命中 ─────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_FlowRegistrationResolved_FillsTierPriority_HighestClassStaysNull()
    {
        var host = MakeHost();
        var store = NewClockedStore();
        AcquireValidLease(store);
        AttachLeaseStore(host, store);
        var runs = HostRuns(host);
        var rec = runs.CreateRun("wf-ev1", "rev-1");
        AddFlowRegistrationOp(store, rec.RunId, ArbitrationTier.System, 7);
        var original = OccupiedTrustedNoLevels(); // 正向命中语义（级别覆盖）用无预填输入，与生产 FromStatus 同形状

        var result = InvokeResolve(host, original, StatusWithExecution(Guid.Parse(rec.WireRunId)));

        // 级别事实：只填解析命中项
        Assert.Equal(ArbitrationTier.System, result.Tier);
        Assert.Equal(7, result.Priority);
        Assert.Null(result.HighestClass); // §24.100 残余⑥：无受信最高级来源，宿主不得冒充
        // 其余字段**逐项保留**（全字段高辨识输入 ⇒ 丢字段/重置类突变可判定；第 3 轮会诊必改 3）
        Assert.Equal(OccupantFactsState.Occupied, result.State);
        Assert.Equal(original.Reference, result.Reference);
        Assert.True(result.HasTrustedIdentity);
        Assert.Equal(original.ExecutionInstanceId, result.ExecutionInstanceId);
        Assert.Equal(original.RunId, result.RunId);
        Assert.Equal(original.JobId, result.JobId);
        Assert.Equal(original.Kind, result.Kind);
        Assert.Equal(original.Source, result.Source);
        Assert.Equal(original.Name, result.Name);
        Assert.True(result.HoeingClass);
        Assert.True(result.StopRequested);
    }

    // ── ⑤ 台账读取异常 ─────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_RunListThrows_KeepsUnknownAndLogsFailure()
    {
        var host = MakeHost();
        var store = NewClockedStore();
        AcquireValidLease(store);
        AttachLeaseStore(host, store);
        var runs = HostRuns(host);
        runs.CreateRun("wf-ev1", "rev-1"); // 先有记录文件 ⇒ List() 真正走到 per-file 读取（read-list 标签）
        runs.FileOperationFaultForTest = op =>
            string.Equals(op, "read-list", StringComparison.Ordinal)
                ? new InvalidOperationException("ev1-injected：运行台账读取异常注入")
                : null;
        var original = OccupiedTrusted();
        try
        {
            var result = InvokeResolve(host, original, StatusWithExecution(Guid.NewGuid()));

            AssertLevelUnknown(result, original);
            Assert.Contains(_hostLog, l => l.Contains("占用者级别事实解析失败", StringComparison.Ordinal));
        }
        finally
        {
            runs.FileOperationFaultForTest = null;
        }
    }

    // ── ⑥ 早出分支（非占用／身份不可信：不触租约台账、不留解析痕） ─────────────
    // 判别力（第 1/2/9/10/11 轮会诊处置迭代后的终版）：
    // ① 计数时钟版夹具（本节前两例，第 11 轮会诊重要项方案）：租约库构造注入的 _utcNow 在 ReadCore 开头必被调用
    //    ⇒ 前置获取租约后取基线、调用早出路径、断言计数不变 ⇒ **任何**租约读取（含零痕静默读取）确定性地使断言红；
    //    无墙钟、无调度敏感性。mut4（守卫整体移除）/mut10（静默读）/mut14（仅删 State 检查）均由此红。
    // ② 有效租约＋台账钩子版夹具（本节后两例，第 10 轮会诊必改 1）：租约 Valid ⇒ 绕过守卫者直达 `_runs.List()` ⇒
    //    read-list 故障钩子抛非争用异常 ⇒ 生产 catch 留「解析失败」痕 ⇒ 「无解析日志」红（mut4/mut14）。
    //    解析路径的静默多读台账由 mut6 的解析判据覆盖。

    /// <summary>「读取计数」探针（第 11 轮会诊重要项方案）：租约库构造注入计数时钟（_utcNow 在 ReadCore
    /// 开头必被调用）⇒ 前置获取租约后取基线，调用早出路径后断言计数不变 ⇒ **任何**租约读取（含零痕静默读取）
    /// 确定性地使断言红；无墙钟、无调度敏感性，替代已移除的争用/计时探针。</summary>
    private (TaskCenterHost Host, Func<long> Reads) MakeHostWithCountedLease()
    {
        var host = MakeHost();
        long calls = 0;
        var store = new ArbitrationLeaseStore(_arbDir, () => { Interlocked.Increment(ref calls); return _clock; });
        AcquireValidLease(store); // 前置：有效租约（此过程自身消耗读数，基线在其后取）
        AttachLeaseStore(host, store);
        var baseline = Interlocked.Read(ref calls);
        return (host, () => Interlocked.Read(ref calls) - baseline);
    }

    [Fact]
    public void Resolve_IdleOccupant_EarlyOut_NoLeaseRead()
    {
        var (host, reads) = MakeHostWithCountedLease();
        // HasTrustedIdentity=true（第 9 轮会诊必改 1）：隔离 **State 守卫**——若沿用默认 false，
        // 本夹具同时命中两项早出条件，无法独立证明「非占用 ⇒ 早出」（mut14 仅删 State 检查的突变由此获得判别力）。
        // 生产 FromStatus 的 Idle 恒不可信；本夹具为守卫结构钉死的合成输入，如实注明。
        var idle = new RunningOccupantFacts { State = OccupantFactsState.Idle, HasTrustedIdentity = true, Reference = "ev1-idle" };

        var result = InvokeResolve(host, idle, StatusWithExecution(Guid.NewGuid()));

        Assert.Same(idle, result); // 早出返回原对象（不复制、不覆盖）
        Assert.Equal(0, reads()); // 计数时钟（第 11 轮方案）：任何租约读取（含零痕静默读取）必使计数 >0
        Assert.False(HasResolveLog());
    }

    [Fact]
    public void Resolve_UntrustedIdentity_EarlyOut_NoLeaseRead()
    {
        var (host, reads) = MakeHostWithCountedLease();
        var untrusted = new RunningOccupantFacts
        {
            State = OccupantFactsState.Occupied,
            HasTrustedIdentity = false,
            Reference = "ev1-untrusted",
        };

        var result = InvokeResolve(host, untrusted, StatusWithExecution(Guid.NewGuid()));

        Assert.Same(untrusted, result);
        Assert.Equal(0, reads()); // 计数时钟：任何租约读取必使计数 >0
        Assert.False(HasResolveLog());
    }

    // ── ⑥-b 有效租约＋台账钩子版早出（第 10 轮会诊必改 1：台账读取判据的独立突变验证） ──

    /// <summary>共享构造：租约 Valid＋运行台账 read-list 故障钩子。绕过守卫者路径＝读租约(Valid)→过非 Valid→
    /// `List()` 触钩子抛非争用异常→生产 catch 留「解析失败」痕 ⇒ 「无解析日志」判据必红。</summary>
    private RunningOccupantFacts InvokeWithValidLeaseAndLedgerHook(RunningOccupantFacts earlyOutInput)
    {
        var host = MakeHost();
        var store = NewClockedStore();
        AcquireValidLease(store);
        AttachLeaseStore(host, store);
        var runs = HostRuns(host);
        runs.CreateRun("wf-ev1", "rev-1");
        runs.FileOperationFaultForTest = op =>
            string.Equals(op, "read-list", StringComparison.Ordinal)
                ? new InvalidOperationException("ev1-ledgerhook-probe：早出路径不应读取运行台账")
                : null;
        try
        {
            return InvokeResolve(host, earlyOutInput, StatusWithExecution(Guid.NewGuid()));
        }
        finally
        {
            runs.FileOperationFaultForTest = null;
        }
    }

    [Fact]
    public void Resolve_IdleOccupant_EarlyOut_NoLedgerRead()
    {
        var idle = new RunningOccupantFacts { State = OccupantFactsState.Idle, HasTrustedIdentity = true, Reference = "ev1-idle-ledgerhook" };

        var result = InvokeWithValidLeaseAndLedgerHook(idle);

        Assert.Same(idle, result);
        Assert.False(HasResolveLog(), "早出不得读运行台账（台账钩子：多读一次必留「解析失败」痕）");
    }

    [Fact]
    public void Resolve_UntrustedIdentity_EarlyOut_NoLedgerRead()
    {
        var untrusted = new RunningOccupantFacts
        {
            State = OccupantFactsState.Occupied,
            HasTrustedIdentity = false,
            Reference = "ev1-untrusted-ledgerhook",
        };

        var result = InvokeWithValidLeaseAndLedgerHook(untrusted);

        Assert.Same(untrusted, result);
        Assert.False(HasResolveLog(), "早出不得读运行台账（台账钩子：多读一次必留「解析失败」痕）");
    }
}
