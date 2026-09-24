using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

#if DEBUG
/// <summary>
/// **批次17：写盘探针窗口机制的「特征化存档」夹具**（归档 §24.110 真·残余第 2 项——D3 夹具全量偶发红的机制）。
///
/// **它是什么**：把 b15e 帧（`_batch15/trx/b15e_full5.trx`，唯一失败＝
/// `LocalWaitReevaluationTriggerTests.Decide_DoesNotTouchWaitQueueStore` line 410 `Assert.Single` 空集合）
/// 的**确定性因果链**固化为可执行证据。该帧的机制（批次 17 查明并经确定性复现证明）：
///
/// ①批次 15e 时代（提交 `8bff943c4`）`Persist_MaterializedSnapshot_IsValidatedAsAWhole_PerLoadShapeRules`
///    以**裸赋值**安装进程级静态探针 `LocalWaitQueueStore.WriteSnapshotProbeMutator`（无 CAS／无首调用者门），
///    窗口＝其 `Upsert` 全程（含文件 I/O）；且该类当时**没有** `[Collection(LocalWaitSnapshotProbe)]`
///    （该属性为批次 16 所加）⇒ 完全并行。
/// ②其突变体按「批内第一条非本行 second 的项」选择改写对象（**无身份锚定**）。
/// ③D3 夹具的 `Upsert(WaitItem("s-1"))` 在窗口内触发 `Persist` ⇒ 外来批首项（s-1）被改写：
///    `duplicate-itemid` 行把我们的 ItemId 改写成该行 second 的**合法** ItemId ⇒ 形状校验通过 ⇒
///    **静默写出**「ItemId 与 StableIdentity 失配」的文件 ⇒ `Load` 正常读回 ⇒ `Decide` 的身份一致性校验
///    （批次 15b 修复 #3）**正确**跳过该失配项 ⇒ 空产出 ⇒ `Assert.Single` 红（即观察到的失败签名）。
/// ④D2 行自身**保持绿**：裸赋值下突变体对窗口内**每一次** Persist 都执行，本行自己的 Upsert 随后
///    照常改写自己的 baseline ⇒ probeRan=1、caught=CorruptException ⇒ 断言全过——
///    与 full5 观测（唯一失败＝D3 夹具、D2 四行全绿）一致。
/// ⑤签名选择：只有 `duplicate-itemid` 行产生上述「静默写坏」；`undefined-tier`／`undefined-state`
///    会令外来 Upsert **响亮抛异常**（另一签名）；`empty-itemid` 只动本行 second（外来批不命中）。
///
/// **它不是什么**：不证明生产代码有缺陷——**触发器/store 的行为全部正确**（失配项被身份校验跳过
/// 正是批次 15b 修复 #3 的预期行为）；缺陷在**测试基础设施**（无锚定＋裸赋值的进程级探针窗口）。
/// 批次 16 已加两层缓解（集合 DisableParallelization＋CAS 首调用者门），批次 17 补身份锚定；
/// 本夹具把「锚定为什么是必要的」钉成可重放事实。
///
/// **判别力边界**：本夹具**故意**用「b15e 式无锚定突变体」复刻事故——它锚定的是**本文件自建**的
/// 局部突变体，不检查 D2 夹具的现状；若未来有人移除 D2 突变体的身份锚定，本夹具**不会**变红
/// （那时的防线是集合串行＋CAS 门＋锚定评审）。本夹具在 `LocalWaitSnapshotProbe`
/// 集合内（DisableParallelization）⇒ 自身窗口不会与任何其他测试重叠，**不会**复刻出真实干扰。
///
/// **依赖**：`WriteSnapshotProbeMutator` 仅 DEBUG 构建存在 ⇒ 本文件整体 `#if DEBUG` 门控
/// （与 `Persist_WriteSnapshotMaterialization_IsLoadBearing` 同法）；Release 下不生成。
/// </summary>
[Collection("LocalWaitSnapshotProbe")]
public sealed class LocalWaitSnapshotProbeMechanismArchiveTests
{
    private static LocalWaitItem Item(string identity) => new()
    {
        ItemId = LocalWaitQueuePolicy.DeriveItemId(identity),
        StableIdentity = identity,
        CandidateId = "cand-" + identity,
        Namespace = "manual",
        WorkflowId = "wf",
        Tier = ArbitrationTier.Plan,
        Priority = 0,
        HasTrustedIdentity = true,
        EnqueuedAtUtc = DateTimeOffset.UnixEpoch,
        State = LocalWaitItemState.Waiting,
    };

    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), tag + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Exception? Catch(Action action)
    {
        try { action(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static string? RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null)
        {
            if (Directory.Exists(Path.Combine(root.FullName, "MultiplayerHoeingAssistant"))
                && Directory.Exists(Path.Combine(root.FullName, "Test")))
                return root.FullName;
            root = root.Parent;
        }
        return null;
    }

    /// <summary>
    /// **D2 突变体锚定防回归守卫（源文本级，会诊 B17-R3 的防回归判据）**：D2 探针夹具
    /// （`LocalWaitPrerequisiteContractTests`）的身份锚定是本批修复之一；存档夹具上三例只复刻
    /// **自建局部突变体**，检测不到「D2 实际锚定被还原为旧的无锚定形态」。本守卫把锚定钉进源文本：
    /// ①旧无锚定 victim 选择（`if (!isSecond)`）不得回归；②baseline 身份锚定与 ③reactivation 行身份锚定
    /// 必须在；④b15e 事故形态的**裸赋值**安装/清除（`WriteSnapshotProbeMutator = items`／`= null`）不得回归
    /// （两处必须是 CAS）。
    /// **能力边界（如实，与仓库既有源文本守卫同口径）**：文本匹配不覆盖语义等价改写（如把锚定改写成
    /// 其它等价条件、或经别名/拼接构造的无锚定形态）；语义判别力由存档夹具的机制复现与人工评审承担。
    /// **反向突变验证（MUT-B17-4）**：把 D2 突变体临时还原为 `if (!isSecond)` ⇒ 本守卫红；还原 ⇒ 绿
    /// （证据与日志见 `_batch17/b17_mutation_log.md` MUT-B17-4 节）。
    /// </summary>
    [Fact]
    public void D2MutatorAnchorGuard_SourceText()
    {
        var root = RepoRoot();
        Assert.True(root is not null, "未能定位仓库根（需同时存在 MultiplayerHoeingAssistant/ 与 Test/ 目录）。");
        var path = Path.Combine(root!, "Test", "MultiplayerHoeingAssistant.UnitTest", "ServiceTests",
            "TaskCenter", "LocalWaitPrerequisiteContractTests.cs");
        var text = File.ReadAllText(path);

        // ①旧无锚定 victim 选择不得回归（MUT-B17-4 的突变点）
        Assert.False(text.Contains("if (!isSecond)", StringComparison.Ordinal),
            "D2 探针突变体不得回归为无锚定 victim 选择「if (!isSecond)」——该形态在并行窗口内会改写外来测试的批内项"
            + "（b15e_full5 事故机制，见 §24.110 真残余第 2 项批次 17 闭合登记）。如确需调整 victim 选择，"
            + "必须保持按本行身份锚定并同步更新本守卫与存档夹具。");

        // ②③身份锚定必须在（baseline 与 reactivation 两处）
        Assert.True(text.Contains("string.Equals(victim.StableIdentity, baselineIdentity, StringComparison.Ordinal)", StringComparison.Ordinal),
            "D2 探针突变体缺少 baseline 身份锚定（victim.StableIdentity == baselineIdentity）——无锚定形态会波及外来批。");
        Assert.True(text.Contains("string.Equals(victim.StableIdentity, secondIdentity, StringComparison.Ordinal)", StringComparison.Ordinal),
            "D2 reactivation 行缺少 secondIdentity 身份锚定——仅靠固定时刻键匹配会波及同时刻值的外来项。");

        // ④b15e 事故形态的裸赋值安装/清除不得回归（两处必须是 CAS 安装/CAS 清除）
        Assert.False(text.Contains("WriteSnapshotProbeMutator = items", StringComparison.Ordinal),
            "D2 探针不得用裸赋值安装（会覆盖他人已装的探针；b15e 事故的安装形态）。必须 CAS 安装。");
        Assert.False(text.Contains("WriteSnapshotProbeMutator = null", StringComparison.Ordinal),
            "D2 探针不得用裸 null 清除（会清掉他人窗口；b15e 事故的清除形态）。必须 CAS 清除。");
    }

    /// <summary>
    /// **顺序 A（外来批先调用）**：装上 b15e 式（裸赋值＋无锚定＋duplicate-itemid）突变体后，
    /// 先执行 D3 夹具的 Upsert（外来批 Persist ⇒ s-1 被改写），再执行 D2 行自己的 Upsert。
    /// 期望：D3 侧 Decide 产 **0 条**（＝b15e_full5 line 410 的失败签名）；
    /// D2 行自身 caught 为 CorruptException（＝D2 全绿，与 full5 观测一致）。
    /// </summary>
    [Fact]
    public void B15eMechanism_ForeignUpsertInsideUnanchoredWindow_OurFirst_ReproducesEmptyReeval()
    {
        var (d3Requests, d2CaughtIsCorrupt) = RunB15eWindowSequence(ourFirst: true);
        Assert.Equal(0, d3Requests);
        Assert.True(d2CaughtIsCorrupt);
    }

    /// <summary>**顺序 B（D2 行先调用）**：同一窗口内先 D2 行 Upsert 再外来 Upsert，两种顺序都复现同一签名。</summary>
    [Fact]
    public void B15eMechanism_ForeignUpsertInsideUnanchoredWindow_TheirsFirst_ReproducesEmptyReeval()
    {
        var (d3Requests, d2CaughtIsCorrupt) = RunB15eWindowSequence(ourFirst: false);
        Assert.Equal(0, d3Requests);
        Assert.True(d2CaughtIsCorrupt);
    }

    /// <summary>
    /// **修复对照（批次 17 锚定）**：同序列换成**身份锚定**突变体（只改写自己行的 baseline 身份）⇒
    /// 外来项不被触碰 ⇒ D3 侧仍产 1 条；D2 行自身的响亮拒绝路径不变。证明锚定同时保住
    /// D2 夹具的判别力与外来批的安全。
    /// </summary>
    [Fact]
    public void B15eMechanism_AnchoredMutator_ForeignBatchUntouched()
    {
        var ourDir = TempDir("revalq-archive-anchored-our-");
        var theirDir = TempDir("revalq-archive-anchored-their-");
        try
        {
            var ourStore = new LocalWaitQueueStore(ourDir);
            var theirStore = new LocalWaitQueueStore(theirDir);
            var baselineIdentity = "s-whole-baseline-duplicate-itemid";
            theirStore.Upsert(Item(baselineIdentity));
            var theirSecond = Item("s-whole-second-duplicate-itemid");

            // 身份锚定版突变体（批次 17 落入 D2 夹具的同一防护形态）
            LocalWaitQueueStore.WriteSnapshotProbeMutator = items =>
            {
                foreach (var victim in items.ToList())
                {
                    if (string.Equals(victim.StableIdentity, baselineIdentity, StringComparison.Ordinal)
                        && !string.Equals(victim.ItemId, theirSecond.ItemId, StringComparison.Ordinal))
                        victim.ItemId = theirSecond.ItemId;
                }
            };
            Exception? caught = null;
            try
            {
                ourStore.Upsert(Item("s-1"));
                theirStore.Upsert(theirSecond);
            }
            catch (Exception ex) { caught = ex; }
            finally
            {
                LocalWaitQueueStore.WriteSnapshotProbeMutator = null;
            }

            // 锚定下两条性质同时成立：①外来批（我们的 s-1）不被触碰 ⇒ 正常写入、Decide 仍产 1 条；
            // ②本行自己的批照常被改写 ⇒ 响亮拒绝（D2 行自己的判据不变，不是「不再突变」）。
            Assert.IsType<LocalWaitQueueCorruptException>(caught);
            var loaded = ourStore.Load().ToList();
            var requests = new LocalWaitReevaluationTrigger()
                .Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, loaded).Requests;
            Assert.Equal(1, requests.Count);
        }
        finally
        {
            Directory.Delete(ourDir, recursive: true);
            Directory.Delete(theirDir, recursive: true);
        }
    }

    private static (int d3Requests, bool d2CaughtIsCorrupt) RunB15eWindowSequence(bool ourFirst)
    {
        var ourDir = TempDir("revalq-archive-our-");
        var theirDir = TempDir("revalq-archive-their-");
        try
        {
            var ourStore = new LocalWaitQueueStore(ourDir);
            var theirStore = new LocalWaitQueueStore(theirDir);
            var baselineIdentity = "s-whole-baseline-duplicate-itemid";
            theirStore.Upsert(Item(baselineIdentity));
            var theirSecond = Item("s-whole-second-duplicate-itemid");

            // ── b15e 时代突变体原样复刻（8bff943c4：裸赋值、无首调用者门、victim＝第一条非本行 second 项）──
            LocalWaitQueueStore.WriteSnapshotProbeMutator = items =>
            {
                foreach (var victim in items.ToList())
                {
                    var isSecond = string.Equals(victim.ItemId, theirSecond.ItemId, StringComparison.Ordinal)
                                   && string.Equals(victim.StableIdentity, theirSecond.StableIdentity, StringComparison.Ordinal);
                    if (!isSecond) victim.ItemId = theirSecond.ItemId;
                }
            };
            Exception? caught;
            try
            {
                if (ourFirst)
                {
                    ourStore.Upsert(Item("s-1"));                            // 外来批 Persist（D3 夹具的那一行）
                    caught = Catch(() => theirStore.Upsert(theirSecond));    // D2 行自己的 Upsert
                }
                else
                {
                    caught = Catch(() => theirStore.Upsert(theirSecond));
                    ourStore.Upsert(Item("s-1"));
                }
            }
            finally { LocalWaitQueueStore.WriteSnapshotProbeMutator = null; }

            // D3 夹具视角：Load（判定输入）→ Decide → 请求条数（1＝绿，0＝line 410 红）
            var decideInput = ourStore.Load().ToList();
            var requests = new LocalWaitReevaluationTrigger()
                .Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, decideInput).Requests;

            // D2 行视角：其自身断言（caught 须为 CorruptException ⇒ 本行保持绿）
            Assert.True(caught is LocalWaitQueueCorruptException,
                $"D2 行自身应保持绿（响亮拒绝），实际 caught={caught?.GetType().Name ?? "null"}");

            return (requests.Count, caught is LocalWaitQueueCorruptException);
        }
        finally
        {
            Directory.Delete(ourDir, recursive: true);
            Directory.Delete(theirDir, recursive: true);
        }
    }
}
#endif
