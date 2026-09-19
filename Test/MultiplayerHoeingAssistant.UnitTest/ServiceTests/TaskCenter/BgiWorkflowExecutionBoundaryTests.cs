using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// BgiWorkflowExecutionBoundary（R4.8 Batch B）纯函数夹具：
/// 资源引用 → ext.task.start 寻址映射（三类 + 缺名/缺修订/未知类型拒绝）、
/// 账号合同（至多一个/空白拒绝/expectedUid=完整原值）、
/// 节点终态解释（skipped 合法 D15、WasCancelled 优先、活动态/未知词不猜）。
/// 传输/异步窗口场景由 TaskCenterHost 组件夹具与 R4.10 集成验收覆盖。
/// </summary>
public class BgiWorkflowExecutionBoundaryTests
{
    private static WorkflowNode Node(string kind, string? config = "日常综合", string? taskId = null,
        string? revision = "rev-1", params WorkflowStrategy[] strategies)
        => new()
        {
            NodeId = "n-1",
            Kind = kind,
            Ref = new WorkflowResourceRef { Config = config, TaskId = taskId, Revision = revision },
            Strategies = [.. strategies],
        };

    [Fact]
    public void MapResource_ConfigGroup_ToGroupName()
    {
        var err = BgiWorkflowExecutionBoundary.TryMapResource(
            Node("resource.configGroup", config: "锄地B"), out var g, out var c, out var t, out var rev);

        Assert.Null(err);
        Assert.Equal("锄地B", g);
        Assert.Null(c);
        Assert.Null(t);
        Assert.Equal("rev-1", rev);
    }

    [Fact]
    public void MapResource_OneDragon_ToConfigName()
    {
        var err = BgiWorkflowExecutionBoundary.TryMapResource(
            Node("resource.oneDragonConfig"), out var g, out var c, out var t, out var rev);

        Assert.Null(err);
        Assert.Null(g);
        Assert.Equal("日常综合", c);
        Assert.Null(t);
        Assert.Equal("rev-1", rev);
    }

    [Fact]
    public void MapResource_SingleTask_RequiresConfigAndTaskId()
    {
        Assert.Null(BgiWorkflowExecutionBoundary.TryMapResource(
            Node("resource.singleTask", taskId: "domain"), out _, out var c, out var t, out _));
        Assert.Equal("日常综合", c);
        Assert.Equal("domain", t);

        Assert.NotNull(BgiWorkflowExecutionBoundary.TryMapResource(
            Node("resource.singleTask", taskId: null), out _, out _, out _, out _)); // 缺 taskId 拒绝
    }

    [Fact]
    public void MapResource_MissingRevision_OrUnknownKind_Rejected()
    {
        Assert.Contains("修订号", BgiWorkflowExecutionBoundary.TryMapResource(
            Node("resource.oneDragonConfig", revision: null), out _, out _, out _, out _));
        Assert.NotNull(BgiWorkflowExecutionBoundary.TryMapResource(
            Node("resource.unknown"), out _, out _, out _, out _));
    }

    [Fact]
    public void ExtractExpectedUid_SingleAccount_FullOriginalValue()
    {
        var account = new WorkflowStrategy
        {
            Kind = "prerequisite.account",
            Params = new Dictionary<string, System.Text.Json.JsonElement>
            { ["uid"] = System.Text.Json.JsonSerializer.SerializeToElement("123456789") },
        };
        var err = BgiWorkflowExecutionBoundary.TryExtractExpectedUid(Node("resource.oneDragonConfig", strategies: account),
            out var uid);

        Assert.Null(err);
        Assert.Equal("123456789", uid); // 完整原值进协议（掩码只用于展示）
    }

    [Fact]
    public void ExtractExpectedUid_NoAccount_Null()
    {
        Assert.Null(BgiWorkflowExecutionBoundary.TryExtractExpectedUid(Node("resource.oneDragonConfig"), out var uid));
        Assert.Null(uid);
    }

    [Fact]
    public void ExtractExpectedUid_DuplicateOrBlank_Rejected()
    {
        var account = new WorkflowStrategy
        {
            Kind = "prerequisite.account",
            Params = new Dictionary<string, System.Text.Json.JsonElement>
            { ["uid"] = System.Text.Json.JsonSerializer.SerializeToElement("10001") },
        };
        Assert.NotNull(BgiWorkflowExecutionBoundary.TryExtractExpectedUid(
            Node("resource.oneDragonConfig", strategies: [account, account]), out _)); // 多个 account
        Assert.NotNull(BgiWorkflowExecutionBoundary.TryExtractExpectedUid(
            Node("resource.oneDragonConfig", strategies: new WorkflowStrategy { Kind = "prerequisite.account" }), out _)); // 空白 uid
    }

    [Fact]
    public void InterpretNodeJob_SkippedIsLegalTerminal_WasCancelledWins()
    {
        // D15：skipped 合法终态原词（不吃前置/收尾解释器的协议违例判定）
        var (word, _) = BgiWorkflowExecutionBoundary.InterpretNodeJob(new BgiJobInfo { State = "skipped" });
        Assert.Equal("skipped", word);

        // WasCancelled 优先于成功/失败（与统一解释器同纪律）
        var (w2, _) = BgiWorkflowExecutionBoundary.InterpretNodeJob(
            new BgiJobInfo { State = "succeeded", WasCancelled = true });
        Assert.Equal("cancelled", w2);
    }

    [Fact]
    public void FailureClassification_OnlyPreSideEffectWhitelist_Rejected()
    {
        // R4.10 终审复核（重要6）：副作用前协议/合同/准入拒绝（白名单）→ Rejected；其余 → Unknown（不猜未受理）
        foreach (var code in new[] { "capability_required", "invalid_request", "stale_epoch", "request_expired", "unsupported_operation", "queue_full", "task_busy" })
            Assert.True(BgiWorkflowExecutionBoundary.IsPreSideEffectRejection(code), code);
        foreach (var code in new[] { "result_unknown", "task_start_failed", "timeout", "" })
            Assert.False(BgiWorkflowExecutionBoundary.IsPreSideEffectRejection(code), code);
        Assert.False(BgiWorkflowExecutionBoundary.IsPreSideEffectRejection(null), "<null>");
    }

    [Fact]
    public void InterpretNodeJob_ActiveOrUnknown_NotGuessed()
    {
        Assert.Null(BgiWorkflowExecutionBoundary.InterpretNodeJob(new BgiJobInfo { State = "running" }).Word);
        Assert.Null(BgiWorkflowExecutionBoundary.InterpretNodeJob(new BgiJobInfo { State = "queued" }).Word);
        Assert.Null(BgiWorkflowExecutionBoundary.InterpretNodeJob(new BgiJobInfo { State = "whatever" }).Word);
        Assert.Equal("failed", BgiWorkflowExecutionBoundary.InterpretNodeJob(
            new BgiJobInfo { State = "failed", ErrorCode = "config_changed" }).Word);
        Assert.Equal("rejected", BgiWorkflowExecutionBoundary.InterpretNodeJob(
            new BgiJobInfo { State = "rejected" }).Word);
    }
}