using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// R5.2 B2-γ 第 3 步（节点后继提交改道仲裁面）两项「一键可跑」验收夹具（owner 0 点击，场景施工方内置）：
/// ①**路径启用门**——`_admissionWired`（E1/E2 入口接线）**不等于**节点改道启用；生产构造恒不启用，只有
///   内部接缝显式 opt-in 两个门才启用（设计稿 §12.3「施工阻断：第 3 步尚不得启用相关路径」/§13.11a）。
/// ②**三态映射**——门面结论 → `BoundarySubmitResult` 必须按「结果确定性」映射：门面确定结论→Rejected，
///   事实不可考（含 `Error`，即 sender 可能已 Accepted、关闭/接管阶段抛异常）→Unknown，绝不反转成确定拒绝。
///   **本夹具只证明分类，不构成「三态完整链路已验收」**（Accepted 回执读取与「Accepted 后关闭异常」交错归 G6 欠项）。
/// </summary>
public class TaskCenterSuccessorPathGateTests
{
    private static TaskCenterHost NewHost(string root, bool admissionWired, bool successorAdmissionWired)
        => new(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
            () => null, log: null, runnerFactory: null, readinessOverride: () => (true, null),
            admissionWired: admissionWired, successorAdmissionWired: successorAdmissionWired);

    /// <summary>生产构造（public ctor）必须**不**启用节点改道：E1/E2 已接线，但第 3 步路径门关闭。</summary>
    [Fact]
    public void ProductionCtor_DoesNotEnableSuccessorPathGate()
    {
        var root = Path.Combine(Path.GetTempPath(), "tcgate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var host = new TaskCenterHost(
                Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
                () => null, () => true, () => null);

            Assert.False(host.SuccessorAdmissionWiredForTest);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SuccessorPathGate_RequiresBothSwitches()
    {
        var root = Path.Combine(Path.GetTempPath(), "tcgate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            Assert.False(NewHost(root, admissionWired: false, successorAdmissionWired: false).SuccessorAdmissionWiredForTest);
            Assert.False(NewHost(root, admissionWired: true, successorAdmissionWired: false).SuccessorAdmissionWiredForTest);
            Assert.False(NewHost(root, admissionWired: false, successorAdmissionWired: true).SuccessorAdmissionWiredForTest);
            Assert.True(NewHost(root, admissionWired: true, successorAdmissionWired: true).SuccessorAdmissionWiredForTest);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    // 门面给出**确定结论** → Rejected（本层不自行断言「一定没发送」）
    [InlineData(AdmissionResultKind.TerminalRejected, false)]
    [InlineData(AdmissionResultKind.RetryableRejected, false)] // 曾错误映射为 Unknown（按「可否重试」而非「结果确定性」）
    [InlineData(AdmissionResultKind.NotSelected, false)]
    [InlineData(AdmissionResultKind.F11Blocked, false)]
    [InlineData(AdmissionResultKind.NeedPreemptConfirm, false)]
    // 事实不可考 → Unknown（不猜成功、也不猜失败；含 Error＝sender 可能已 Accepted、随后关闭/接管抛异常）
    [InlineData(AdmissionResultKind.NeedReconcile, true)]
    [InlineData(AdmissionResultKind.Reconciling, true)]
    [InlineData(AdmissionResultKind.Error, true)] // 曾落入 `_ => Rejected` 兜底＝事实反转
    public void MapAdmissionResultToBoundary_ByResultCertainty(AdmissionResultKind kind, bool expectUncertain)
    {
        var result = new AdmissionResult
        {
            Kind = kind,
            ReasonCode = "rc-1",
            Detail = "detail-1",
        };

        var mapped = TaskCenterHost.MapAdmissionResultToBoundary(result);

        Assert.Equal(expectUncertain, mapped.Uncertain);
        Assert.False(mapped.Accepted);
        Assert.Null(mapped.JobId);
        Assert.Contains("rc-1", mapped.RejectReason);
        Assert.Contains("detail-1", mapped.RejectReason);
    }
}
