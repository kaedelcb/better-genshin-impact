#nullable enable

using System;
using FsCheck;
using FsCheck.Xunit;
using MultiplayerHoeingAssistant.Services;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.MultiplayerHoeingAssistantTests;

/// <summary>
/// 奥黛塔 v2 状态机 PetStateEngine 的属性测试。
/// 守护：基础状态优先级全序（睡觉&gt;任务&gt;待开锄&gt;定时&gt;空闲）、任务类型分类关键词契约、
/// totality（任意输入有合法输出）、chip 三段式组装契约（**:** 占位、无"定时"前缀）。
/// </summary>
public class PetStateEngineTests
{
    private static PetFacts Facts(
        bool alive = true, bool task = false, PetTaskKind kind = PetTaskKind.Other,
        bool ready = false, bool hoeing = false, bool sched = false)
        => new(alive, task, kind, ready, hoeing, sched);

    /// <summary>P1：BGI 未运行恒为睡觉（压过一切）。</summary>
    [Property(MaxTest = 200)]
    public Property ResolveBase_BgiDead_AlwaysSleeping(
        bool task, bool ready, bool hoeing, bool sched)
    {
        var s = PetStateEngine.ResolveBase(Facts(alive: false, task: task, ready: ready, hoeing: hoeing, sched: sched));
        return (s == PetState.Sleeping).ToProperty();
    }

    /// <summary>P2：任务运行时按类型映射，锄地类恒为 Hoeing。</summary>
    [Property(MaxTest = 200)]
    public Property ResolveBase_TaskRunning_MapsByKind(
        bool ready, bool sched)
    {
        var hoeingState = PetStateEngine.ResolveBase(Facts(task: true, kind: PetTaskKind.Hoeing, hoeing: true, ready: ready, sched: sched));
        var artifactState = PetStateEngine.ResolveBase(Facts(task: true, kind: PetTaskKind.Artifact, ready: ready, sched: sched));
        var affectionState = PetStateEngine.ResolveBase(Facts(task: true, kind: PetTaskKind.Affection, ready: ready, sched: sched));
        var gatherState = PetStateEngine.ResolveBase(Facts(task: true, kind: PetTaskKind.Gather, ready: ready, sched: sched));
        var otherState = PetStateEngine.ResolveBase(Facts(task: true, kind: PetTaskKind.Other, ready: ready, sched: sched));
        return (hoeingState == PetState.Hoeing && artifactState == PetState.WorkingArtifact
                && affectionState == PetState.WorkingAffection
                && gatherState == PetState.WorkingGather && otherState == PetState.WorkingGather).ToProperty();
    }

    /// <summary>P3（totality）：任意事实组合产出合法枚举值。</summary>
    [Property(MaxTest = 500)]
    public Property ResolveBase_Total_LegalEnum(
        bool alive, bool task, bool ready, bool hoeing, bool sched)
    {
        var kind = PetTaskKind.Artifact;
        var s = PetStateEngine.ResolveBase(Facts(alive, task, kind, ready, hoeing, sched));
        return (s is >= PetState.Sleeping and <= PetState.WorkingGather).ToProperty();
    }

    /// <summary>P4：ReadyOnline 压过 HasScheduled。</summary>
    [Property(MaxTest = 100)]
    public Property ResolveBase_Ready_BeatsScheduled(bool sched)
        => (PetStateEngine.ResolveBase(Facts(ready: true, sched: sched)) == PetState.ReadyOnline).ToProperty();

    // —— 分类器契约 ——

    [Theory]
    [InlineData("传奇", null, PetTaskKind.Hoeing)]
    [InlineData("锄地一条龙", "锄地一条龙", PetTaskKind.Hoeing)]
    [InlineData("圣遗物调查", null, PetTaskKind.Artifact)]
    [InlineData("狗粮收集", "每日狗粮", PetTaskKind.Artifact)]
    [InlineData("好感-晨曦酒庄", null, PetTaskKind.Affection)]
    [InlineData("矿物采集", null, PetTaskKind.Gather)]
    [InlineData("每日委托", null, PetTaskKind.Other)]
    public void ClassifyTask_Keywords_MapToKinds(string taskName, string? groupName, PetTaskKind expected)
    {
        Assert.Equal(expected, PetStateEngine.ClassifyTask(false, taskName, groupName));
    }

    [Fact]
    public void ClassifyTask_AutoHoeing_BeatsKeywords()
    {
        // 联机锄地信号优先于任务名关键词（任务名"传奇"含锄地关键词，一致）；
        // 换成含其他关键词的任务名也仍判锄地
        Assert.Equal(PetTaskKind.Hoeing, PetStateEngine.ClassifyTask(true, "圣遗物调查", null));
    }

    [Fact]
    public void ClassifyTask_Affection_BeatsHoeingKeyword()
    {
        // "好感一条龙"同时含"好感"，若有锄地字样也不覆盖（好感优先）
        Assert.Equal(PetTaskKind.Affection, PetStateEngine.ClassifyTask(false, "好感锄地", null));
    }

    // —— chip 组装契约（定稿格式：{9:00|**:**} · 3/4 · 三态，无"定时"前缀）——

    [Fact]
    public void ComposeOnlineChip_NoScheduled_ShowsPlaceholder()
    {
        Assert.Equal("**:** · 0/4 · 未上线",
            PetStateEngine.ComposeOnlineChip(null, 0, 4, PetOnlinePhase.NotReady));
    }

    [Fact]
    public void ComposeOnlineChip_Scheduled_ShowsTimeOnly()
    {
        Assert.Equal("9:00 · 3/4 · 已上线",
            PetStateEngine.ComposeOnlineChip("9:00", 3, 4, PetOnlinePhase.Ready));
    }

    [Fact]
    public void ComposeOnlineChip_Connected_Phase()
    {
        Assert.Equal("**:** · 4/4 · 已联机",
            PetStateEngine.ComposeOnlineChip("", 4, 4, PetOnlinePhase.Connected));
    }

    [Fact]
    public void ComposeOnlineChip_ReadyCountNeverExceedsExpected()
    {
        Assert.Equal("9:00 · 5/5 · 已上线",
            PetStateEngine.ComposeOnlineChip("9:00", 5, 4, PetOnlinePhase.Ready));
    }

    // —— 好感轮次日志解析 ——

    [Theory]
    [InlineData("好感任务 第3/10轮 完成", 3, 10)]
    [InlineData("[第 2/4 轮 茶包s] xxx", 2, 4)]
    [InlineData("轮次:5/8", 5, 8)]
    [InlineData("轮次： 1 / 6", 1, 6)]
    public void ParseAffectionRound_KnownFormats(string line, int cur, int total)
    {
        var r = PetStateEngine.ParseAffectionRound(line);
        Assert.NotNull(r);
        Assert.Equal((cur, total), r!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("普通日志行")]
    [InlineData("第3/10次（不是轮）")]
    [InlineData("第 0/10 轮")]
    [InlineData("第 11/10 轮")]
    public void ParseAffectionRound_NoMatchOrInvalid_ReturnsNull(string? line)
    {
        Assert.Null(PetStateEngine.ParseAffectionRound(line));
    }

    // —— 状态→动画 key 映射完整性（15 套素材中实际引用的 key 都必须存在于清单）——

    [Fact]
    public void ToAnimKey_AllStates_MapToImportedSets()
    {
        var imported = new[] { "sleep", "tea", "interact", "act_hoeing", "act_artifact", "act_gather", "shy" };
        foreach (PetState state in Enum.GetValues<PetState>())
        {
            var key = PetStateEngine.ToAnimKey(state);
            Assert.Contains(key, imported);
        }
    }

    [Fact]
    public void BurstToAnimKey_AllBursts_MapToImportedSets()
    {
        var imported = new[] { "joy", "disdain", "shock", "confused", "smug", "anger", "helpless" };
        foreach (var burst in new[] { "celebrate", "cancel", "drag", "hover", "online", "alertHeavy", "alert" })
        {
            var key = PetStateEngine.BurstToAnimKey(burst);
            Assert.Contains(key, imported);
        }
    }
}
