using Xunit;

namespace BetterGenshinImpact.UnitTest.ServiceTests;

/// <summary>
/// PBT 测试：验证 `TaskRunningDecisions.IsTaskRunning` 的正确性。
/// 对应 spec `task-status-model-redesign` 的 PBT-A。
/// - 修复前：旧逻辑 `!string.IsNullOrEmpty(taskName) && !isCancelled` 在残留 taskName 下误判 true。
/// - 修复后：`IsTaskRunning(false) == false`（无任务持锁 → 必 false），不受残留 taskName 影响。
/// </summary>
public class TaskRunningDecisionsTest
{
    /// <summary>
    /// 无任务持锁（semaphoreBusy=false）→ 必 false。这是修复的关键：残留 taskName 不再导致 running=true。
    /// </summary>
    [Fact]
    public void IsTaskRunning_NoLock_ReturnsFalse()
    {
        Assert.False(TaskRunningDecisions.IsTaskRunning(false));
    }

    /// <summary>
    /// 有任务持锁（semaphoreBusy=true）→ true。
    /// </summary>
    [Fact]
    public void IsTaskRunning_Locked_ReturnsTrue()
    {
        Assert.True(TaskRunningDecisions.IsTaskRunning(true));
    }

    /// <summary>
    /// 旧逻辑 `!string.IsNullOrEmpty(taskName) && !isCancelled` 在残留 taskName 下会误判 true。
    /// 本测试验证：即使 taskName 非空（残留），`IsTaskRunning(false)` 仍是 false。
    /// 这是 P1 修复保护的回归测试。
    /// </summary>
    [Fact]
    public void IsTaskRunning_ResidualTaskName_NotLocked_ReturnsFalse()
    {
        // 模拟：任务正常结束（isCancelled=false）但 taskName 残留（如"好感度任务"）
        var residualTaskName = "好感度任务";
        var isCancelled = false;
        // 旧逻辑会误判：!string.IsNullOrEmpty(residualTaskName) && !isCancelled == true
        var oldLogicResult = !string.IsNullOrEmpty(residualTaskName) && !isCancelled;
        Assert.True(oldLogicResult, "旧逻辑：残留 taskName + 未取消 → running=true（错误）");

        // 修复后：无任务持锁 → running=false（正确）
        Assert.False(TaskRunningDecisions.IsTaskRunning(false), "修复后：无任务持锁 → running=false（正确，不受残留 taskName 影响）");
    }

    /// <summary>
    /// 任务正常执行中：有锁 → running=true。
    /// </summary>
    [Fact]
    public void IsTaskRunning_NormalExecution_ReturnsTrue()
    {
        // 任务运行中，锁被持有
        Assert.True(TaskRunningDecisions.IsTaskRunning(true));
    }

    /// <summary>
    /// 任务被取消（F11）：有锁但 isCancelled=true → 旧逻辑 false，新逻辑（有锁）true。
    /// 注意：P1 修复后 running 只由锁决定，不再被 isCancelled 影响。
    /// 这是设计决策：F11 取消时任务仍在回收（锁未释放），running 短暂为 true 可接受（10s 轮询必修正）。
    /// </summary>
    [Fact]
    public void IsTaskRunning_CancelledButLockHeld_ReturnsTrue()
    {
        // 有锁（任务在回收）
        Assert.True(TaskRunningDecisions.IsTaskRunning(true));
    }
}