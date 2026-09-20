using MultiplayerHoeingAssistant.Models;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.3.3④（D8：前置复验身份绑定）**：前置事实的复用必须按**节点出现／attempt／策略-类型／账号标识**分别绑定，
/// 任一维度变化都不得复用旧事实。**范围如实（小节会诊收窄）**：`Epoch` 在本夹具中只证明「**独立字段且可区分**、
/// 不参与身份五段比对」——**不证明**其持久化链路、*会话等价*判定或「跨会话事实⇒Unknown」的消费实现
/// （后者属恢复集成复验，见 §18.1 R5.3.3④残余）。纯组件夹具，owner 0 点击。
/// </summary>
public class R53PrerequisiteBindingTests
{
    private static PrerequisiteActionRecord Record() => new()
    {
        NodeId = "n-1",
        Occurrence = 0,
        LoopIteration = 0,
        Attempt = 1,
        StrategyIndex = 0,
        Kind = "prerequisite.account",
        AccountKey = "acct-a",
        Epoch = "100:200",
        IdempotencyKey = "key-1",
        State = PrerequisiteActionState.Intent,
    };

    [Fact]
    public void Binding_ExactIdentity_Matches()
        => Assert.True(Record().Matches("n-1", 0, 0, 1, 0, "prerequisite.account", "acct-a"));

    [Theory]
    [InlineData("n-2", 0, 0, 1, 0, "prerequisite.account", "acct-a")]     // 节点变化
    [InlineData("n-1", 1, 0, 1, 0, "prerequisite.account", "acct-a")]     // 出现序号变化
    [InlineData("n-1", 0, 1, 1, 0, "prerequisite.account", "acct-a")]     // 循环轮次变化
    [InlineData("n-1", 0, 0, 2, 0, "prerequisite.account", "acct-a")]     // attempt 变化
    [InlineData("n-1", 0, 0, 1, 1, "prerequisite.account", "acct-a")]     // 策略索引变化
    [InlineData("n-1", 0, 0, 1, 0, "prerequisite.redeemCode", "acct-a")]  // 操作类型变化
    [InlineData("n-1", 0, 0, 1, 0, "prerequisite.account", "acct-b")]     // 账号标识变化
    public void Binding_EachDimensionMismatch_DoesNotMatch(
        string nodeId, int occurrence, int loopIteration, int attempt, int strategyIndex, string kind, string accountKey)
        => Assert.False(Record().Matches(nodeId, occurrence, loopIteration, attempt, strategyIndex, kind, accountKey));

    [Fact]
    public void Binding_AbsentAccountKey_DoesNotReuseFactBoundToAccount()
        => Assert.False(Record().Matches("n-1", 0, 0, 1, 0, "prerequisite.account", null));

    [Fact]
    public void Binding_EpochIsSeparateField_NotPartOfIdentityMatch()
    {
        var a = Record();
        var b = Record();
        b.Epoch = "100:999";

        Assert.True(a.Matches("n-1", 0, 0, 1, 0, "prerequisite.account", "acct-a"));
        Assert.True(b.Matches("n-1", 0, 0, 1, 0, "prerequisite.account", "acct-a")); // 纪元不参与身份比对
        Assert.NotEqual(a.Epoch, b.Epoch);                                             // 会话维度仍可区分（供恢复对账判别）
    }
}
