using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// RunStore（R4.2）验收夹具——B3 会诊四个崩溃窗口：
/// ① 提交前：Planned 记录恢复标 Interrupted；② 受理后回执前（提交在飞）：标 Unknown 禁止自动重跑；
/// ③ 终态后水位提交前：已观察终态不标 Unknown；④ 收尾提交后：终态记录不动、待执行收尾保留不自动触发。
/// 另有：记录修订单调守卫、坏记录拒绝覆盖（原件保留）、原子写备份、恢复绝不换幂等键。
/// 涉盘用例走临时目录，finally 清理。
/// </summary>
public class RunStoreTests : IDisposable
{
    private readonly string _dir;

    public RunStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "runstore-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// **[P50 结题·批次四十九]** 文件**争用家族**有界重试与**权限故障响亮**的确定性验证（注入按操作标签）：
    /// ①**发布路径**（`publish`）瞬时拒绝两次后成功 ⇒ 写入成功（证明有界重试覆盖原子替换）；
    /// ②**读取路径**（`read-load`／`read-list`／`read-merging`）瞬时拒绝两次后成功 ⇒ 读取成功
    /// （覆盖 `Load`／`List`／`UpdateMergingIf` 的盘上读取——[首轮会诊阻断项]）；
    /// ③**持久化路径**（`read-persist-check`／`backup`／`write-tmp`）瞬时拒绝后仍能成功发布；并补**定向反证**：
    /// 陈旧修订仍抛 `RunRecordConflictException`（⇒ 盘上读取**未被吞成「无记录」**）、备份文件**确实新增**
    /// （⇒ 未被吞成「跳过备份」）；
    /// ③′**建目录／清残件路径**（`create-runs-dir`／`create-backup-dir`／`cleanup-tmp`）瞬时拒绝后仍能完成
    /// （[第三轮会诊重要项] 原先无注入标签 ⇒ 无覆盖证据）；
    /// ④**目录枚举路径**（`enumerate`）瞬时拒绝两次后仍能列出记录（**不得**把争用误判成「空目录」）；
    /// ⑤**预算耗尽后的权限故障必须响亮**：**逐点**断言「**恰好**烧完 80 次预算后**原样抛出**
    /// `UnauthorizedAccessException`」（`read-list`／`read-load`／`read-merging`／`read-persist-check`／
    /// `backup`／`write-tmp`／`publish`／`read-unknown`；**不得**被当作「坏记录」静默丢弃，也**不得**在
    /// 读路径折成「无记录」）；**枚举**争用耗尽 ⇒ `List()` 响亮而 `QueryHandoffLedger`
    /// 归**不可确认**（`Incomplete`，不得当未命中）；逐条记录读取争用耗尽 ⇒ 同样归 `Incomplete`；
    /// ⑥**共享冲突类 `IOException` 耗尽后原样抛出**（同一实例，不换类型、不包装、不静默）。
    /// **[第三轮会诊重要项处置]** 每个注入标签在回调内**计数**，收尾逐标签断言**确实命中**——
    /// 防止「注入点未接线/标签写错」让夹具空过（假绿）。
    /// </summary>
    [Fact]
    public void FileContentionFamily_TransientDeniedRetries_PermanentDeniedSurfacesLoudly()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-p50", "rev-1");
        var hitTags = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        // 注入器：[第三轮会诊重要项] **全部标签都计数**（含最终成功那一次）——否则「装机遇点是否真被走到」
        // 无法跨标签取证（例如断言「建备份目录重试时备份确实发生」）；**仅 `armedTag` 注入**，`faults` 次后放行
        // ⇒ 模拟瞬时争用（`int.MaxValue` ＝ 永久拒绝对，用于耗尽路径）。
        string? armedTag = null;
        var remaining = 0;
        Func<Exception> armedFactory = static () => new UnauthorizedAccessException("Access to the path is denied.（夹具注入：争用）");
        store.FileOperationFaultForTest = op =>
        {
            hitTags.AddOrUpdate(op, 1, (_, v) => v + 1);
            if (!string.Equals(op, armedTag, StringComparison.Ordinal)) return null;
            return remaining-- > 0 ? armedFactory() : null;
        };
        void Arm(string tag, int faults, Func<Exception>? factory = null)
        {
            armedTag = tag;
            remaining = faults;
            armedFactory = factory
                ?? (static () => new UnauthorizedAccessException("Access to the path is denied.（夹具注入：争用）"));
        }

        void AssertTagHits(string tag, int minHits)
        {
            var hits = hitTags.TryGetValue(tag, out var n) ? n : 0;
            Assert.True(hits >= minHits,
                $"注入标签 `{tag}` 未命中（实得 {hits} 次，期望 ≥{minHits}）——夹具未真正触达该文件访问点");
        }

        // ① 发布路径（原子替换）瞬时拒绝两次 ⇒ 有界重试后成功（2 次注入 + 1 次成功 = 3 次调用）
        Arm("publish", 2);
        rec.Note = "发布重试";
        store.Update(rec);                                   // 不抛 ⇒ 重试生效
        AssertTagHits("publish", 3);
        rec = store.Load(rec.RunId)!;                        // 每次写入后取回最新修订（避免后续自撞修订冲突）
        Assert.Equal("发布重试", rec.Note);

        // ② 读取路径瞬时拒绝两次 ⇒ 有界重试后成功（Load／List／UpdateMergingIf 三处盘上读取）
        Arm("read-load", 2);
        Assert.NotNull(store.Load(rec.RunId));
        AssertTagHits("read-load", 3);

        Arm("read-list", 2);
        Assert.Contains(store.List(), r => r.RunId == rec.RunId);
        AssertTagHits("read-list", 3);

        Arm("read-merging", 2);
        Assert.True(store.UpdateMergingIf(rec.RunId, latest => { latest.Note = "合并写"; return true; }, out _));
        AssertTagHits("read-merging", 3);
        rec = store.Load(rec.RunId)!;

        // ③ 持久化各步瞬时拒绝 ⇒ 仍能成功发布
        foreach (var tag in new[] { "read-persist-check", "backup", "write-tmp" })
        {
            Arm(tag, 1);
            rec.Note = "持久化重试-" + tag;
            store.Update(rec);
            AssertTagHits(tag, 2);
            rec = store.Load(rec.RunId)!;
        }
        Assert.Equal("持久化重试-write-tmp", store.Load(rec.RunId)!.Note);

        // ③′ 建目录／清残件同样纳入争用重试（新增注入标签），并补写入点的**定向反证**。
        Arm("create-runs-dir", 2);
        rec.Note = "建目录重试";
        store.Update(rec);                                   // 不抛 ⇒ 建目录重试生效
        AssertTagHits("create-runs-dir", 3);
        rec = store.Load(rec.RunId)!;

        var backupBefore = hitTags["backup"];
        var revisionBeforeBackup = rec.RecordRevision;
        var noteBeforeBackup = rec.Note;
        Arm("create-backup-dir", 1);
        rec.Note = "建备份目录重试";
        store.Update(rec);
        AssertTagHits("create-backup-dir", 2);
        // 备份步骤**确实执行**（吞掉争用即不会走到 File.Copy）；本步只注入建目录 ⇒ 备份恰多 1 次调用。
        Assert.Equal(backupBefore + 1, hitTags["backup"]);
        // [第五轮会诊建议处置] **不强依赖「文件总数 +1」**（那隐含「一修订一文件」的实现细节）：
        // 改为断言「该修订号的备份文件存在」**且其内容＝发布前盘上修订**（修订号单调不回退 ⇒ 该名由本步产生）。
        var backupPath = Path.Combine(_dir, "_backup", $"{rec.RunId}.{revisionBeforeBackup}.run.json");
        Assert.True(File.Exists(backupPath), "备份未发生（吞掉争用即不会走到 File.Copy）");
        // 备份内容＝**发布前**的盘上修订（非直接文本比对：序列化器默认转义非 ASCII，故按记录反序列化后比对）。
        var backupRecord = System.Text.Json.JsonSerializer.Deserialize<WorkflowRunRecord>(File.ReadAllText(backupPath));
        Assert.Equal(noteBeforeBackup, backupRecord!.Note);
        rec = store.Load(rec.RunId)!;

        // `read-persist-check` 的争用**不得被吞成「盘上无记录」**：用**陈旧修订**发布——只有真读到盘上记录
        // 才会抛修订冲突（若被吞成「无记录」则会静默覆盖成功 ⇒ 本断言转红）。
        var staleSnapshot = store.Load(rec.RunId)!;           // 携带当前（即将过期）修订的副本
        rec.Note = "占用一次修订";
        store.Update(rec);
        rec = store.Load(rec.RunId)!;
        var persistCheckBefore = hitTags["read-persist-check"];
        Arm("read-persist-check", 1);
        staleSnapshot.Note = "陈旧修订写入";
        Assert.Throws<RunRecordConflictException>(() => store.Update(staleSnapshot));
        Assert.Equal(persistCheckBefore + 2, hitTags["read-persist-check"]);   // 1 次注入 + 1 次真正读到盘上记录

        Arm("cleanup-tmp", 1);
        rec.Note = "清残件重试";
        store.Update(rec);
        AssertTagHits("cleanup-tmp", 2);
        rec = store.Load(rec.RunId)!;
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));      // 清残件在重试后不留痕

        // ④ 目录枚举瞬时拒绝两次 ⇒ 有界重试后仍可列出
        Arm("enumerate", 2);
        Assert.Contains(store.List(), r => r.RunId == rec.RunId);
        AssertTagHits("enumerate", 3);

        // ⑤ 预算耗尽 ⇒ **权限故障响亮**：逐点断言「**恰好**烧完一轮 80 次预算后原样抛出」（不缺不溢不静默）
        void AssertBudgetExhausted(string tag, Action action)
        {
            var before = hitTags.TryGetValue(tag, out var n) ? n : 0;
            Arm(tag, int.MaxValue);
            Assert.Throws<UnauthorizedAccessException>(action);
            Assert.Equal(before + 80, hitTags.TryGetValue(tag, out var after) ? after : 0);
        }

        AssertBudgetExhausted("read-list", () => store.List());
        AssertBudgetExhausted("read-load", () => store.Load(rec.RunId));      // 读路径**不得**折成「无记录」
        AssertBudgetExhausted("read-merging", () => store.UpdateMergingIf(rec.RunId, r => true, out _));
        AssertBudgetExhausted("read-persist-check", () => store.Update(rec));
        AssertBudgetExhausted("backup", () => store.Update(rec));
        AssertBudgetExhausted("write-tmp", () => store.Update(rec));
        AssertBudgetExhausted("publish", () => store.Update(rec));
        AssertBudgetExhausted("read-unknown", () => { _ = store.UnknownFiles; });

        // 逐条记录读取争用 ⇒ 对账**不可确认**（不得当未命中，也不得静默忽略）
        var handoffBefore = hitTags.TryGetValue("read-handoff", out var h0) ? h0 : 0;
        Arm("read-handoff", int.MaxValue);
        Assert.Equal(HandoffLedgerState.Incomplete, store.QueryHandoffLedger("intent:p50").State);
        Assert.Equal(handoffBefore + 80, hitTags["read-handoff"]);

        // 枚举争用耗尽 ⇒ 列出**响亮失败**，对账归**不可确认**（连续两次仍不可确认，语义幂等）
        var enumerateBefore = hitTags["enumerate"];
        Arm("enumerate", int.MaxValue);
        Assert.Throws<UnauthorizedAccessException>(() => store.List());
        Assert.Equal(enumerateBefore + 80, hitTags["enumerate"]);
        Assert.Equal(HandoffLedgerState.Incomplete, store.QueryHandoffLedger("intent:p50").State);
        Assert.Equal(HandoffLedgerState.Incomplete, store.QueryHandoffLedger("intent:p50").State);
        Assert.Equal(enumerateBefore + 240, hitTags["enumerate"]);   // 1×列出 + 2×对账，各烧一轮完整预算

        // ⑥ 共享冲突类 `IOException` 耗尽 ⇒ **原样抛出**（同一实例；发布未生效 ⇒ 盘上仍是上次成功载荷）
        var sharingViolation = new IOException(
            "The process cannot access the file because it is being used by another process.",
            unchecked((int)0x80070020));
        var publishBefore = hitTags["publish"];
        Arm("publish", int.MaxValue, () => sharingViolation);
        var thrown = Assert.Throws<IOException>(() => store.Update(rec));
        Assert.Same(sharingViolation, thrown);
        Assert.Equal(publishBefore + 80, hitTags["publish"]);
        Assert.Equal("清残件重试", store.Load(rec.RunId)!.Note);   // 失败**未半写**：盘上仍是上次成功载荷
    }

    /// <summary>
    /// **[P50 结题·第二轮会诊处置] 争用族**判定**（收窄口径）**：真正的争用＝共享冲突类 `IOException` 与
    /// Windows「拒绝访问」（`UnauthorizedAccessException`）；**「不存在」类（`FileNotFoundException`／
    /// `DirectoryNotFoundException`）不是争用**——等多久也不会出现，纳入重试只会白烧预算（实测：夹具收尾后台写
    /// 已删除临时根时单夹具白烧 237 次重试 ≈6s；收窄后整类 152s→8s）。
    /// </summary>
    [Fact]
    public void IsFileContention_NarrowFamily_ExcludesNotFound()
    {
        Assert.True(RunStore.IsFileContention(new UnauthorizedAccessException("Access to the path is denied.")));
        Assert.True(RunStore.IsFileContention(new IOException("The process cannot access the file because it is being used by another process.")));
        // **[第三轮会诊建议处置]** 真实 Windows 形态：共享冲突（`ERROR_SHARING_VIOLATION`=32）与字节区间锁冲突
        // （`ERROR_LOCK_VIOLATION`=33）以 `IOException` + HRESULT `0x80070020`／`0x80070021` 出现，同属争用族。
        Assert.True(RunStore.IsFileContention(new IOException("sharing violation", unchecked((int)0x80070020))));
        Assert.True(RunStore.IsFileContention(new IOException("lock violation", unchecked((int)0x80070021))));
        Assert.False(RunStore.IsFileContention(new FileNotFoundException("gone")));
        Assert.False(RunStore.IsFileContention(new DirectoryNotFoundException("gone")));
        Assert.False(RunStore.IsFileContention(new System.Text.Json.JsonException("bad json")));
    }

    /// <summary>
    /// **[P50 结题·第三轮会诊重要项处置] 有界重试的**预算语义**（机制级、精确、零等待）**：
    /// 所有文件访问点共用同一助手 ⇒ 在此**逐条锁死**其语义，替代「每个访问点各跑一次 ≈1.2s 真耗尽」：
    /// ①**瞬时争用后成功**：调用次数 = 注入次数 + 1；
    /// ②**预算耗尽**：**恰好** `attempts` 次调用后**原样抛出**（`Assert.Same`＝同一实例，不换类型、不包装；
    ///   「恰好」也证明**不多烧**预算）；
    /// ③**非争用异常一次也不重试**（`NotFound` 族／JSON 损坏／编程错误）——「不存在」等多久也不会出现，
    ///   纳入重试只会白烧预算（实测曾致单夹具 237 次重试、整类 152s）。
    /// </summary>
    [Fact]
    public void WithContentionRetry_BudgetEatsExactlyAttempts_NonContentionNeverRetried()
    {
        // ① 瞬时争用两次 ⇒ 第 3 次成功
        var calls = 0;
        var faults = 2;
        var value = RunStore.WithContentionRetry(
            () =>
            {
                calls++;
                if (faults-- > 0) throw new UnauthorizedAccessException("Access to the path is denied.");
                return 42;
            },
            attempts: 5, delayMs: 0);
        Assert.Equal(42, value);
        Assert.Equal(3, calls);

        // ② 预算耗尽 ⇒ **恰好 attempts 次**调用后原样抛出（同一实例）
        var exhausted = new IOException("sharing violation", unchecked((int)0x80070020));
        calls = 0;
        var thrown = Assert.Throws<IOException>(() => RunStore.WithContentionRetry<int>(
            () => { calls++; throw exhausted; }, attempts: 4, delayMs: 0));
        Assert.Same(exhausted, thrown);
        Assert.Equal(4, calls);

        // ③ 非争用异常**一次也不重试**（不白烧预算，也不改变异常类型）
        foreach (var nonContention in new Exception[]
                 {
                     new FileNotFoundException("gone"),
                     new DirectoryNotFoundException("gone"),
                     new System.Text.Json.JsonException("bad json"),
                     new ArgumentException("bad"),
                 })
        {
            calls = 0;
            var thrownNonContention = Assert.ThrowsAny<Exception>(() => RunStore.WithContentionRetry<int>(
                () => { calls++; throw nonContention; }, attempts: 80, delayMs: 0));
            Assert.Same(nonContention, thrownNonContention);
            Assert.Equal(1, calls);
        }
    }

    /// <summary>
    /// **G4a（[批次四十五 第三轮验证会诊处置]）`CreateRun` 的准入来源 Scope 写入边界**：
    /// ①空白 ⇒ 规范化为 `null`（无固定来源；旧记录缺字段同样反序列化为 null）；
    /// ②规范 `bgi:local:{非空完整 epoch}`（含冒号的完整 epoch）原样落盘；
    /// ③畸形非空（其它实例前缀／空 epoch 段）⇒ **响亮抛出**且**不创建运行记录**（不落权威字段）。
    /// </summary>
    [Fact]
    public void CreateRun_AdmissionSourceScope_WhitespaceNormalizedMalformedRejected()
    {
        var store = new RunStore(_dir);

        var blank = store.CreateRun("wf-g4a-a", "rev-1", admissionSourceScope: "   ");
        Assert.Null(blank.AdmissionSourceScope);
        Assert.Null(store.Load(blank.RunId)!.AdmissionSourceScope);   // 落盘同样为 null（缺字段=无来源）

        var canonical = store.CreateRun("wf-g4a-b", "rev-1",
            admissionSourceScope: "bgi:local:4821:638912345678901234");
        Assert.Equal("bgi:local:4821:638912345678901234", canonical.AdmissionSourceScope);
        Assert.Equal("bgi:local:4821:638912345678901234", store.Load(canonical.RunId)!.AdmissionSourceScope);

        var before = store.List().Count;
        Assert.Throws<InvalidOperationException>(() =>
            store.CreateRun("wf-g4a-c", "rev-1", admissionSourceScope: "bgi:other:ep"));
        Assert.Throws<InvalidOperationException>(() =>
            store.CreateRun("wf-g4a-d", "rev-1", admissionSourceScope: "bgi:local:"));
        Assert.Equal(before, store.List().Count);                      // 畸形值不产生运行记录
    }

    /// <summary>
    /// **[P7／§12.2 第 3 项] 字段合并：`UpdateMerging` 保留并发写入者的非自有字段改动**。
    /// 对照：旧对象整对象写回（`Update`）在修订漂移时**响亮冲突**（`RunRecordConflictException`）——
    /// 这正是「旧 Runner 对象覆盖接管事实」的既有护栏；`UpdateMerging` 则把自有字段合并进**最新记录**，
    /// 使并发改动被保留而非整笔失败。
    /// </summary>
    [Fact]
    public void UpdateMerging_PreservesConcurrentNonOwnedChange_WhileUpdateConflicts()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-merge", "rev-1");
        rec.Note = "初始";
        store.Update(rec);
        var stale = store.Load(rec.RunId)!;              // 旧对象（修订落后）

        // 并发写入者推进记录（自有字段：Note）
        var other = store.Load(rec.RunId)!;
        other.Note = "并发写入者的改动";
        store.Update(other);

        // ① 旧对象整写＝响亮冲突（不得静默覆盖）
        Assert.Throws<RunRecordConflictException>(() => store.Update(stale));

        // ② 合并写（前置条件成立）：自有字段（State）生效，且并发改动（Note）保留
        var applied = store.UpdateMergingIf(rec.RunId, latest =>
        {
            latest.State = WorkflowRunState.Running;
            return true;
        }, out var latest);
        Assert.True(applied);
        Assert.NotNull(latest);
        Assert.Equal(WorkflowRunState.Running, latest!.State);              // 自有字段已应用
        Assert.Equal("并发写入者的改动", latest.Note);                       // 非自有字段保留（未被旧对象覆盖）
        var reloaded = store.Load(rec.RunId)!;
        Assert.Equal(WorkflowRunState.Running, reloaded.State);
        Assert.Equal("并发写入者的改动", reloaded.Note);

        // ③ **旧对象 rebase**：把盘上最新字段整体同步回旧对象后，旧对象再整写**不再覆盖并发改动**
        RunStore.RebaseOnto(stale, reloaded);
        stale.State = WorkflowRunState.Waiting;      // 自有字段改动
        store.Update(stale);                          // rebase 后修订已对齐 ⇒ 不再冲突
        var after = store.Load(rec.RunId)!;
        Assert.Equal(WorkflowRunState.Waiting, after.State);
        Assert.Equal("并发写入者的改动", after.Note);   // **并发改动仍保留**（rebase 生效，未被旧字段洗回）

        // ④ 前置条件不成立 ⇒ **零发布、零修订推进**（返回 false，latest 为盘上原样）
        var revBefore = store.Load(rec.RunId)!.RecordRevision;
        var appliedNo = store.UpdateMergingIf(rec.RunId, _ => false, out var unchanged);
        Assert.False(appliedNo);
        Assert.NotNull(unchanged);
        Assert.Equal(revBefore, store.Load(rec.RunId)!.RecordRevision);   // 未推进修订

        // 不存在的记录：无副作用（返回 false）
        Assert.False(store.UpdateMergingIf("wf-not-exists", _ => true, out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void CrashWindow1_BeforeSubmit_RecoveredAsInterrupted_IdemKeyUnchanged()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");
        rec.State = WorkflowRunState.Running;
        rec.Cursor = new WorkflowNodeCursor { NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        store.Update(rec);
        var idemBefore = rec.IdempotencyKey;

        var recovered = Assert.Single(store.RecoverOnStart());
        Assert.Equal(WorkflowRunState.Interrupted, recovered.State);
        Assert.Equal(idemBefore, recovered.IdempotencyKey); // 绝不换键重跑
        Assert.Contains("Interrupted", recovered.Note);
    }

    [Fact]
    public void CrashWindow2_SubmitInFlight_RecoveredAsUnknown_NeverAutoResubmit()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");
        store.RecordIntent(rec, NewSubmission(rec));              // 提交意图先行落盘（一提交一身份，B2）
        rec.CurrentSubmission!.Intent = SubmitIntentState.Submitted; // 已发出，受理回执未确认
        store.Update(rec);
        var idemBefore = rec.IdempotencyKey;

        var recovered = Assert.Single(store.RecoverOnStart());
        Assert.Equal(WorkflowRunState.Unknown, recovered.State); // 结果不确定 ≠ 成功
        Assert.Equal(idemBefore, recovered.IdempotencyKey);
        Assert.Contains("禁止自动重跑", recovered.Note);

        // Unknown 不是终态结论，但也绝不被再次扫描改动（幂等）
        var again = store.RecoverOnStart();
        Assert.Single(again); // 仍非终态，保持 Unknown 等待对账
        Assert.Equal(WorkflowRunState.Unknown, again[0].State);
    }

    [Fact]
    public void CrashWindow3_TerminalObserved_BeforeCursorCommit_RecoveredAsInterrupted_NotUnknown()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");
        store.RecordIntent(rec, NewSubmission(rec));
        rec.CurrentSubmission!.Intent = SubmitIntentState.Accepted;
        rec.CurrentSubmission!.JobId = "job-123";
        rec.State = WorkflowRunState.Running;
        store.Update(rec);
        rec.CurrentSubmission!.ObservedTerminal = "succeeded"; // 终态已观察，水位尚未提交
        store.Update(rec);

        var recovered = Assert.Single(store.RecoverOnStart());
        Assert.Equal(WorkflowRunState.Interrupted, recovered.State); // 有终态事实 → 不是 Unknown
        Assert.Equal("succeeded", recovered.CurrentSubmission!.ObservedTerminal);
        Assert.Equal("job-123", recovered.CurrentSubmission!.JobId);
    }

    [Fact]
    public void CrashWindow4_TerminalRecord_Untouched_PendingCompletionRetainedNotFired()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");
        rec.State = WorkflowRunState.Succeeded;
        rec.PendingCompletion = new PendingCompletionRecord // 待执行收尾（E3' 记录形态）
        {
            ActionId = "$flow#0", Kind = "terminal.completionAction", Action = "关闭游戏并关机", State = "pending",
        };
        store.Update(rec);
        var revBefore = rec.RecordRevision;

        Assert.Empty(store.RecoverOnStart()); // 终态记录不动
        var loaded = store.Load(rec.RunId)!;
        Assert.Equal(WorkflowRunState.Succeeded, loaded.State);
        Assert.Equal("关闭游戏并关机", loaded.PendingCompletion!.Action); // 保留待显式处理，不自动触发
        Assert.Equal(revBefore, loaded.RecordRevision);
    }

    [Fact]
    public void Update_RecordRevisionGuard_RejectsStaleWrite()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");

        var stale = store.Load(rec.RunId)!;
        rec.State = WorkflowRunState.Running;
        store.Update(rec); // 推进到 RecordRevision=2

        stale.State = WorkflowRunState.Paused;
        Assert.Throws<RunRecordConflictException>(() => store.Update(stale)); // 并发旧副本拒绝覆盖
    }

    [Fact]
    public void CorruptedRecord_RefuseOverwrite_OriginalPreserved_AndListedAsUnknown()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");
        var file = Path.Combine(_dir, rec.RunId + ".run.json");
        File.WriteAllText(file, "{ 损坏");
        var bytesBefore = File.ReadAllBytes(file);

        Assert.Empty(store.List());
        var bad = Assert.Single(store.UnknownFiles);
        Assert.Equal(file, bad);

        // Load 对坏文件抛 JsonException（绝不回空对象；调用方按隔离处理）
        Assert.Throws<System.Text.Json.JsonException>(() => store.Load(rec.RunId));
        var replacement = new WorkflowRunRecord { RunId = rec.RunId, WorkflowId = "wf-aaaaaaaa", WorkflowRevision = "rev-1" };
        Assert.Throws<RunRecordConflictException>(() => store.Update(replacement)); // 拒绝静默覆盖坏文件
        Assert.Equal(bytesBefore, File.ReadAllBytes(file)); // 原件字节不动
    }

    [Fact]
    public void Persist_AtomicWrite_BackupKeepsPriorRecordRevision()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");
        rec.State = WorkflowRunState.Running;
        store.Update(rec);

        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
        var backup = Assert.Single(Directory.EnumerateFiles(Path.Combine(_dir, "_backup"), rec.RunId + ".*.run.json"));
        var prior = System.Text.Json.JsonSerializer.Deserialize<WorkflowRunRecord>(File.ReadAllText(backup));
        Assert.Equal(WorkflowRunState.Planned, prior!.State); // 上一版状态可回查
        Assert.Equal(1, prior.RecordRevision);
    }

    [Fact]
    public void RecordIntent_RequiresDerivedKey_AndRejectsOverlapWhileInFlight()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");

        // 无派生键拒绝
        Assert.Throws<InvalidOperationException>(() => store.RecordIntent(rec, new WorkflowSubmission { NodeId = "n-1" }));

        // B2：键按出现身份确定性派生——同身份同键（重复投递复用），不同身份不同键
        var key1 = RunStore.DeriveSubmissionKey(rec.RunId, "n-1", 0, 0, 1);
        Assert.Equal(key1, RunStore.DeriveSubmissionKey(rec.RunId, "n-1", 0, 0, 1));
        Assert.NotEqual(key1, RunStore.DeriveSubmissionKey(rec.RunId, "n-2", 0, 0, 1));
        Assert.NotEqual(key1, RunStore.DeriveSubmissionKey(rec.RunId, "n-1", 0, 1, 1));
        Assert.NotEqual(key1, RunStore.DeriveSubmissionKey(rec.RunId, "n-1", 0, 0, 2));

        // 前一提交在飞：拒绝重叠提交（防 jobId/终态跨节点残留）
        store.RecordIntent(rec, NewSubmission(rec, "n-1"));
        Assert.Throws<InvalidOperationException>(() => store.RecordIntent(rec, NewSubmission(rec, "n-2")));

        // 终态确认后允许下一提交
        rec.CurrentSubmission!.ObservedTerminal = "succeeded";
        store.Update(rec);
        store.RecordIntent(rec, NewSubmission(rec, "n-2"));
        Assert.Equal("n-2", rec.CurrentSubmission!.NodeId);
    }

    [Fact]
    public void CrashWindow5_CompletingInFlight_RecoveredAsUnknown_CompletionNeverAutoRefired()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");
        rec.State = WorkflowRunState.Completing; // B5：收尾意图已落盘，执行结果未证实
        rec.PendingCompletion = new PendingCompletionRecord
        {
            ActionId = "$flow#0", Kind = "terminal.completionAction", Action = "关闭游戏并关机", State = "submitted",
        };
        store.Update(rec);

        var recovered = Assert.Single(store.RecoverOnStart());
        Assert.Equal(WorkflowRunState.Unknown, recovered.State); // 收尾在飞 = 结果不确定
        Assert.Equal("terminal.completionAction", recovered.PendingCompletion!.Kind); // 保留待人工对账
        Assert.Equal("关闭游戏并关机", recovered.PendingCompletion!.Action);
        Assert.Contains("禁止自动补发收尾", recovered.Note);
    }

    private static WorkflowSubmission NewSubmission(WorkflowRunRecord rec, string nodeId = "n-1")
        => new()
        {
            Key = RunStore.DeriveSubmissionKey(rec.RunId, nodeId, 0, 0, 1),
            NodeId = nodeId,
            Occurrence = 0,
            LoopIteration = 0,
            Attempt = 1,
        };
}
