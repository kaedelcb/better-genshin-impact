#nullable enable

using BetterGenshinImpact.GameTask.AutoTrackPath;
using FsCheck;
using FsCheck.Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

/// <summary>
/// TeleportCdCompensationDecisions 的 PBT 属性测试。
/// **Validates: Requirements BC-1 / BC-2 (design.md §6)**
///
/// 覆盖列：
///   - 属性 1 ShouldCompensate：补偿判定与"是否单人世界"恒等（单机才补偿，联机不补偿）。
///     平凡性质守卫，防止未来误改决策逻辑。
/// </summary>
public class TeleportCdCompensationDecisionsTest
{
    /// <summary>
    /// 属性 1：ShouldCompensate(isSoloWorld) == isSoloWorld。
    /// 单人世界返回 true（补偿），联机世界返回 false（不补偿）。
    ///
    /// **Validates: Requirements BC-1 / BC-2**
    /// </summary>
    [Property(MaxTest = 1000)]
    public Property ShouldCompensate_ReturnsSoloWorld(bool isSoloWorld)
    {
        var actual = TeleportCdCompensationDecisions.ShouldCompensate(isSoloWorld);
        return (actual == isSoloWorld).ToProperty();
    }
}