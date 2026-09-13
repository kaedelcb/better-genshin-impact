using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.Instance;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Execution;

/// <summary>
/// [A1.4/A3.1/A3.4] JobRegistry 核心语义单测：提交登记 / 幂等采用 / 状态机仲裁 /
/// Transitioned 事件出口（job.* 事件族事实源）/ 进程级幂等窗口重放。
/// 每个用例 new JobRegistry(startHeartbeatTimer: false) 隔离（不碰静态单例，避免跨用例污染）。
/// </summary>
public class JobRegistryTests
{
    [Fact]
    public void Submit_NewJob_FiresQueuedTransition()
    {
        var registry = new JobRegistry(startHeartbeatTimer: false);
        var fired = new List<BgiJob>();
        registry.Transitioned += fired.Add;

        var result = registry.Submit(JobKind.Group, "组A", JobSource.V2, generation: 7);

        Assert.False(result.Adopted);
        Assert.Equal(JobState.Queued, result.Job.State);
        Assert.Single(fired);
        Assert.Same(result.Job, fired[0]);
        Assert.Equal(7, result.Job.Generation);
    }

    [Fact]
    public void Submit_SameGenerationAndName_AdoptsWithoutSecondEvent()
    {
        var registry = new JobRegistry(startHeartbeatTimer: false);
        var fired = new List<BgiJob>();
        registry.Transitioned += fired.Add;

        var first = registry.Submit(JobKind.Group, "组A", JobSource.V2, generation: 7);
        var second = registry.Submit(JobKind.Group, "组A", JobSource.Ext, generation: 7);

        Assert.True(second.Adopted);
        Assert.Equal(first.Job.JobId, second.Job.JobId);
        Assert.Single(fired); // 采用不新发 job.queued
    }

    [Fact]
    public void TryMarkTerminal_FirstWriterWins_SecondMarkIsNoop()
    {
        var registry = new JobRegistry(startHeartbeatTimer: false);
        var fired = new List<BgiJob>();
        registry.Transitioned += fired.Add;
        var job = registry.Submit(JobKind.Solo, "自动秘境", JobSource.OneDragonInternal).Job;

        Assert.True(registry.TryMarkRunning(job.JobId));
        // 漏斗先写 Succeeded（执行真理）
        Assert.True(registry.TryMarkTerminal(job.JobId, JobState.Succeeded));
        // 协调器兜底重复登记 → 幂等无操作
        Assert.False(registry.TryMarkTerminal(job.JobId, JobState.Cancelled, JobErrorCodes.CancelledUser));

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(3, fired.Count); // queued + started + completed
        Assert.Equal(JobState.Succeeded, fired[2].State);
        Assert.NotNull(job.StartedAtUtc);
        Assert.NotNull(job.FinishedAtUtc);
        // 终态后幂等索引摘除：同 gen+name 可再次新建
        var again = registry.Submit(JobKind.Solo, "自动秘境", JobSource.OneDragonInternal);
        Assert.False(again.Adopted);
    }

    [Fact]
    public void Submit_ExplicitJobId_SkipsKeyAdoption()
    {
        var registry = new JobRegistry(startHeartbeatTimer: false);
        var first = registry.Submit(JobKind.Group, "组A", JobSource.V2, generation: 5);

        // 协调器别名场景：显式 jobId 直接新建，不并到按键命中的既有作业
        var explicitId = Guid.NewGuid();
        var second = registry.Submit(JobKind.Group, "组A", JobSource.Ext, generation: 5, jobId: explicitId);

        Assert.False(second.Adopted);
        Assert.Equal(explicitId, second.Job.JobId);
        Assert.NotEqual(first.Job.JobId, second.Job.JobId);
    }

    [Fact]
    public void TryReplayIdempotent_RewritesRequestId_ForCrossConnectionReplay()
    {
        var registry = new JobRegistry(startHeartbeatTimer: false);
        var originalRequestId = Guid.NewGuid();
        var cachedResponse = new InstanceIpcEnvelope
        {
            RequestId = originalRequestId,
            Operation = "op.response",
            Success = true,
            Data = new Newtonsoft.Json.Linq.JObject { ["status"] = "queued" },
        };
        registry.CacheIdempotentResponse("key:abc", cachedResponse);

        // 断线重连后新会话用新 requestId 重发同 key 写操作
        var newRequestId = Guid.NewGuid();
        Assert.True(registry.TryReplayIdempotent("key:abc", newRequestId, out var replay));
        Assert.NotNull(replay);
        Assert.Equal(newRequestId, replay!.RequestId); // 重写为当前请求，客户端才能关联
        Assert.True(replay.Success);
        Assert.Equal("queued", replay.Data?["status"]?.ToString());

        // 未登记/不同 key 不命中
        Assert.False(registry.TryReplayIdempotent("key:other", Guid.NewGuid(), out var miss));
        Assert.Null(miss);
    }

    [Fact]
    public void Epoch_StaticAndInstance_SameValue()
    {
        var registry = new JobRegistry(startHeartbeatTimer: false);
        Assert.Equal(JobRegistry.CurrentEpoch, registry.Epoch);
        Assert.Equal(Environment.ProcessId, JobRegistry.CurrentEpoch.ProcessId);
    }

    [Fact]
    public void Submit_WithParentJobId_ChildrenLinkToDragonParent()
    {
        // [A5-2] 父子模型：龙父作业存续期间子项逐个提交并挂 ParentJobId；
        // 子项终态（含被抢占的显式 Rejected）不影响父作业存续，父作业终态独立登记。
        var registry = new JobRegistry(startHeartbeatTimer: false);
        var parent = registry.Submit(JobKind.OneDragon, "日常一条龙", JobSource.Ui).Job;
        registry.TryMarkRunning(parent.JobId);

        var child1 = registry.Submit(JobKind.Solo, "自动秘境", JobSource.OneDragonInternal, parentJobId: parent.JobId).Job;
        var child2 = registry.Submit(JobKind.Group, "锄地组", JobSource.OneDragonInternal, parentJobId: parent.JobId).Job;

        Assert.Equal(parent.JobId, child1.ParentJobId);
        Assert.Equal(parent.JobId, child2.ParentJobId);
        Assert.Null(parent.ParentJobId);

        // 子项 1 正常终态；子项 2 模拟项间槽位被抢占 → 显式 Rejected（不再静默跳过）
        registry.TryMarkRunning(child1.JobId);
        Assert.True(registry.TryMarkTerminal(child1.JobId, JobState.Succeeded));
        Assert.True(registry.TryMarkTerminal(child2.JobId, JobState.Rejected, JobErrorCodes.TaskBusy));
        Assert.Equal(JobState.Running, parent.State); // 子项终态不扩散到父作业

        // 快照可按 ParentJobId 还原父子拓扑
        var snapshot = registry.Snapshot();
        Assert.Equal(2, snapshot.Count(j => j.ParentJobId == parent.JobId));
        Assert.Equal(JobState.Rejected, registry.Query(child2.JobId)!.State);

        // 父作业终态独立登记，先写者赢
        Assert.True(registry.TryMarkTerminal(parent.JobId, JobState.Succeeded));
        Assert.False(registry.TryMarkTerminal(parent.JobId, JobState.Cancelled));
    }
}
