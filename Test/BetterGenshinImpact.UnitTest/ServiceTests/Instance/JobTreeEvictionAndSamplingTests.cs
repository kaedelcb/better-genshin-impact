using System.Collections.Concurrent;
using System.Reflection;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Instance.MessageHandlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

// [R5 证据族 ev3] §24.103 残项「真实淘汰与并发采样夹具」（只补证据，不改生产、不接任何放行）：
// FIX-1 真实触发 64 条终态 FIFO 淘汰：容量守卫（终态数恰为 64）＋最旧本批条目被逐＋其余 64 条在场；
// FIX-2 真实容量淘汰把终态祖先挤出注册表 ⇒ 退出观测不可判定（null，不是 0）——
//       与 §24.103 既有的"未登记模拟淘汰"夹具不同，这里的祖先缺席由 TryMarkTerminal 容量淘汰真实造成；
// FIX-3 并发登记/终态推进下的 JobTreeSnapshot 采样不变量：不抛异常、JobId 唯一、终局不可逆、
//       终态数 ≤ 64、终态表最终 64 条全部来自本批、非终态作业永不淘汰。
// 边界（与 §24.103 同一口径，如实）：本文件不证明未登记叶子/逃逸任务已退出；
// `0` 只表示"本次采样未观察到"，FIX-3 的不变量同样不构成"派生作业已全部退出"的肯定结论。
// R5 反向突变守护映射（证据与红帧见 _ev3/ev3_mutation_log.md；M1-M3 均红→还原 0 差异→复绿）：
//   M1＝终态 FIFO 容量淘汰拆除（JobRegistry.TryMarkTerminal 淘汰循环）→ 守护 FIX-1/2/3；
//   M2＝JobTreeSnapshot 锁内复制拆除 → 守护 FIX-3（并发采样不变量）；
//   M3＝ExecutionScope 链完整性检查拆除 → 守护 FIX-2（真实淘汰 ⇒ 不可判定，非 0）。
// 全量回归差集核验见 _ev3/ev3_full_diff_baseline.md；TRX 帧清单 _ev3/ev3_trx_sha256.md。
// 会诊第 2 轮建议 #1 采纳（警示注）：本类三组夹具当前不武装 PreemptionGate、不触碰
// CancellationContext 单例，ctor/Dispose 故不做其复位；未来新增挂起/停止类夹具时，
// 须先按 TaskTakeoverIncidentTests 模式补 ctor 复位与 Dispose 还原，防跨用例静态污染。
// 隔离前提（2026-09-26 复核）：触碰 JobRegistry.Instance 的全部测试类均挂 TaskTakeoverIncident
// 非并行集合（ExecutionScopeSkippedTests／ManualStopCooldownTests 只 Start 根与只读快照，无 Submit），
// 因此本文件的精确计数断言不与其它集合的注册表写入竞争。
[Collection("TaskTakeoverIncident")]
public sealed class JobTreeEvictionAndSamplingTests : IDisposable
{
    private readonly AllConfig? previous = ConfigService.Config;
    private readonly AllConfig config = new();
    private readonly InstanceRequestHandler handler = new(null!, null!, null!, _ => { }, _ => { }, NullLogger.Instance);
    private static readonly PropertyInfo ConfigProperty = typeof(ConfigService).GetProperty(nameof(ConfigService.Config))!;

    public JobTreeEvictionAndSamplingTests()
    {
        ConfigProperty.SetValue(null, config);
        PreemptionGate.Disarm();
        ExecutionExitLedger.ResetForTest();
    }

    public void Dispose() => ConfigProperty.SetValue(null, previous);

    private static InstanceIpcEnvelope ExitQueryRequest(Guid instanceId)
        => InstanceIpcEnvelope.Request(InstanceOperations.TaskStatus, new
        {
            executionInstanceId = instanceId.ToString("N"),
            bgiEpoch = new
            {
                processId = JobRegistry.CurrentEpoch.ProcessId,
                startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc
            }
        });

    [Fact]
    public void JobRegistry_TerminalCapacityEviction_IsFifo_KeepsNewest64()
    {
        _ = JobRegistry.Instance;
        var fillers = new List<Guid>();
        try
        {
            for (var i = 1; i <= 65; i++)
            {
                var job = JobRegistry.Instance.Submit(JobKind.Solo, $"ev3淘汰填充{i:000}", JobSource.OneDragonInternal).Job;
                fillers.Add(job.JobId);
                Assert.True(JobRegistry.Instance.TryMarkTerminal(job.JobId, JobState.Succeeded));
            }

            // 65 条本批终态入列后：无论此前终态表中有多少旧条目（≤64），最旧的本批填充必被真实淘汰
            // （淘汰数量＝既有条数＋1，队首方向 FIFO；本批 #1 排在所有旧条目之后，仍必被逐出）
            Assert.Null(JobRegistry.Instance.Query(fillers[0]));
            // 容量不变量：终态条目数恰为 64（既有旧终态全部被挤出，本批 #2..#65 全部保留）
            var tree = JobRegistry.Instance.JobTreeSnapshot();
            Assert.Equal(64, tree.Count(n => n.IsTerminal));
            foreach (var id in fillers.Skip(1))
            {
                var job = JobRegistry.Instance.Query(id);
                Assert.NotNull(job);
                Assert.True(job!.IsTerminal);
            }
        }
        finally
        {
            // 会诊第 1 轮重要项 #2 同原则：终局化中途失败的异常路径不得滞留非终态作业
            foreach (var id in fillers)
            {
                var job = JobRegistry.Instance.Query(id);
                if (job is { } live && !live.IsTerminal)
                    JobRegistry.Instance.TryMarkTerminal(id, JobState.Succeeded);
            }
        }
    }

    [Fact]
    public void ExitReceipt_RealCapacityEvictionOfTerminalAncestor_IsUnknownNotZero()
    {
        _ = JobRegistry.Instance;
        var rootJobId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var root = ExecutionScope.Start(new JobDescriptor(JobKind.OneDragon, "ev3真实淘汰父链", JobSource.Ui,
            JobId: rootJobId, WorkflowRunId: runId));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        var created = new List<Guid>(); // 本例创建的全部作业：异常路径兜底终局化，防非终态滞留进程级单例
        try
        {
            // A：真实登记的终态祖先（父 = 本次根）
            var a = JobRegistry.Instance.Submit(JobKind.Solo, "ev3终态祖先A", JobSource.OneDragonInternal,
                parentJobId: rootJobId).Job;
            created.Add(a.JobId);
            Assert.True(JobRegistry.Instance.TryMarkTerminal(a.JobId, JobState.Succeeded));
            // 64 条填充把 A 真实挤出终态表：A 之后恰有 64 条更新的终态入列 ⇒ A 必被容量淘汰
            //（无论此前表中有多少旧条目）。与 §24.103 既有夹具用 Guid.NewGuid() 模拟缺席不同，
            // 本例的祖先缺席由 TryMarkTerminal 的 FIFO 淘汰真实造成。
            for (var i = 1; i <= 64; i++)
            {
                var f = JobRegistry.Instance.Submit(JobKind.Solo, $"ev3链断填充{i:000}", JobSource.OneDragonInternal).Job;
                created.Add(f.JobId);
                Assert.True(JobRegistry.Instance.TryMarkTerminal(f.JobId, JobState.Succeeded));
            }
            Assert.Null(JobRegistry.Instance.Query(a.JobId)); // 淘汰确已发生，而非登记前缺席

            // B：A 的孩子（同 run、终态、仍在表——B 晚于全部填充入列，必属最新 64 条）；
            // C：B 的孩子（同 run、未终局）。
            var b = JobRegistry.Instance.Submit(JobKind.Solo, "ev3终态中间B", JobSource.OneDragonInternal,
                parentJobId: a.JobId, identity: new JobExecutionIdentity(runId, "ev3-b", 0)).Job;
            created.Add(b.JobId);
            Assert.True(JobRegistry.Instance.TryMarkTerminal(b.JobId, JobState.Succeeded));
            var c = JobRegistry.Instance.Submit(JobKind.Solo, "ev3存活孙C", JobSource.OneDragonInternal,
                parentJobId: b.JobId, identity: new JobExecutionIdentity(runId, "ev3-c", 0)).Job;
            created.Add(c.JobId);
            Assert.NotNull(JobRegistry.Instance.Query(b.JobId));
            root.Dispose(); // 先释放再查退出凭证（顺序语义）；finally 里再次 Dispose 为幂等兜底

            // 链完整性检查（不按终局豁免）：B 的父 A 缺失且不是本次根 ⇒ 证据不可用（null），不得报 0/0
            // 线上形状事实（会诊第 2 轮建议 #2① 采纳后实测反例，2026-09-26）：该投影经
            // InstanceIpcProtocol.cs 的 NullValueHandling.Ignore 序列化，null 字段**整条不上线**——
            // 「键缺失」就是不可判定的线上形态，不存在「键存在但值为 null」的形态；
            // ContainsKey 断言实测红（_ev3_targeted_green_final3.trx 首帧），故不采用。
            // 本断言守护的真实漂移形态是「报 0 代替 null」（M3 突变已证红）；键缺失被 Assert.Null
            // 接受恰是正确语义。
            var data = handler.HandleTaskStatus(null!, ExitQueryRequest(fact.ExecutionInstanceId)).Data!;
            Assert.True(data["executionExitConfirmed"]!.ToObject<bool>());
            Assert.Null(data["executionExitRegisteredSameRunDescendantsAtExit"]?.ToObject<int?>());
            Assert.Null(data["executionExitRegisteredSameRunDescendantsStillOpenNow"]?.ToObject<int?>());
        }
        finally
        {
            // 会诊第 1 轮重要项 #2 处置：异常路径不得悬挂 _active 或滞留非终态作业
            root.Dispose(); // Dispose 有 _disposed 守卫，幂等
            foreach (var id in created)
            {
                var job = JobRegistry.Instance.Query(id);
                if (job is { } live && !live.IsTerminal)
                    JobRegistry.Instance.TryMarkTerminal(id, JobState.Succeeded);
            }
        }
    }

    [Fact]
    public void JobTreeSnapshot_UnderConcurrentSubmitAndTerminalChurn_HoldsSamplingInvariants()
    {
        _ = JobRegistry.Instance;
        const int writerCount = 6, opsPerWriter = 60, readerCount = 4, snapshotsPerReader = 300;
        var errors = new ConcurrentQueue<string>();
        var mineAll = new ConcurrentBag<Guid>();   // 本例创建的全部作业（finally 兜底终局化用）
        var mineTerminal = new ConcurrentBag<Guid>();
        var mineOpen = new ConcurrentBag<Guid>();

        using var startGate = new ManualResetEventSlim(false);
        var writers = Enumerable.Range(0, writerCount).Select(w => Task.Run(() =>
        {
            startGate.Wait();
            for (var i = 0; i < opsPerWriter; i++)
            {
                var job = JobRegistry.Instance.Submit(JobKind.Solo, $"ev3并发{w:00}-{i:000}", JobSource.OneDragonInternal).Job;
                mineAll.Add(job.JobId); // 先登记再分类：写者中途异常也不泄漏
                if ((w + i) % 2 == 0)
                {
                    // 恰半数终态化：本批终态 6×30＝180 条，足以在并发下翻页淘汰（容量 64）
                    if (!JobRegistry.Instance.TryMarkTerminal(job.JobId, JobState.Succeeded))
                        errors.Enqueue($"终态推进返回 false: {job.JobId}");
                    mineTerminal.Add(job.JobId);
                }
                else
                {
                    mineOpen.Add(job.JobId);
                }
            }
        })).ToArray();

        var readers = Enumerable.Range(0, readerCount).Select(_ => Task.Run(() =>
        {
            startGate.Wait();
            // 终局不可逆检查必须**按读者本地**记已见终态：不同读者的快照之间无时间全序，
            // 跨读者共享会把"读者 B 检查它较早拍的旧快照"误判为终态回退（初版假阳性，如实登记）。
            // 同一读者内快照严格按拍摄顺序处理，本地集合下"终态→非终态"才真是采样错位信号。
            var seenTerminal = new HashSet<Guid>();
            for (var i = 0; i < snapshotsPerReader; i++)
            {
                if (i % 4 == 0) Thread.Yield(); // 会诊第 1 轮建议 #1 采纳：让出核增大读/写交错概率
                IReadOnlyList<JobRegistry.JobTreeNode> snapshot;
                try
                {
                    snapshot = JobRegistry.Instance.JobTreeSnapshot();
                }
                catch (Exception ex)
                {
                    // 锁内复制若被改为锁外枚举等采样错位形态，最先表现为并发枚举异常
                    errors.Enqueue($"快照抛异常: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                var ids = new HashSet<Guid>();
                var terminalCount = 0;
                foreach (var node in snapshot)
                {
                    if (!ids.Add(node.JobId))
                        errors.Enqueue($"快照内重复 JobId: {node.JobId}");

                    if (node.IsTerminal)
                    {
                        terminalCount++;
                        seenTerminal.Add(node.JobId);
                    }
                    else if (seenTerminal.Contains(node.JobId))
                    {
                        // 终局不可逆：更晚的采样不得把已终局作业读成未终局（采样错位的直接信号）
                        errors.Enqueue($"终态回退为非终态: {node.JobId}");
                    }
                }

                if (terminalCount > 64)
                    errors.Enqueue($"终态数超容量: {terminalCount}");
            }
        })).ToArray();

        try
        {
            startGate.Set();
            Task.WaitAll(writers.Concat(readers).ToArray());

            Assert.Empty(errors);
            Assert.Equal(writerCount * opsPerWriter / 2, mineTerminal.Count); // 180，翻页前提成立
            var finalTree = JobRegistry.Instance.JobTreeSnapshot();
            Assert.Equal(64, finalTree.Count(n => n.IsTerminal));
            // 终态表中剩余 64 条全部来自本批（本批 180 条更新的终态把既有污染全部挤出）
            var finalTerminals = finalTree.Where(n => n.IsTerminal).Select(n => n.JobId).ToHashSet();
            Assert.True(finalTerminals.IsSubsetOf(mineTerminal.ToHashSet()));
            // 非终态作业永不淘汰
            foreach (var id in mineOpen)
                Assert.NotNull(JobRegistry.Instance.Query(id));
        }
        finally
        {
            // 会诊第 1 轮重要项 #2 处置：异常路径也兜底——本批创建作业全部终局化（随后由容量淘汰收走）；
            // finally 内不断言（不掩盖原始失败），幸路路径的终局化完整性已由上文断言覆盖
            foreach (var id in mineAll)
            {
                var job = JobRegistry.Instance.Query(id);
                if (job is { } live && !live.IsTerminal)
                    JobRegistry.Instance.TryMarkTerminal(id, JobState.Succeeded);
            }
        }
    }
}
