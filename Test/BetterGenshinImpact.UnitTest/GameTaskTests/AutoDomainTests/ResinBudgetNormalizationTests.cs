using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoDomain;
using BetterGenshinImpact.GameTask.AutoDomain.Model;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoLeyLineOutcrop;
using BetterGenshinImpact.GameTask.AutoStygianOnslaught;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoDomainTests;

/// <summary>
/// R3.3 树脂归一化 + R3.4 D 组逐项的合同夹具。
/// 覆盖：ResinBudget 预算构建（D04 原粹 20/40 分项、公版固定顺序、显式 0 为有效输入、
/// 指定模式全零抛错、幽境重载保持无 20/40）、D07 地脉 JSON 策略路径解析。
/// D05/D09 为游戏画面交互块，按代码块恢复后由差异复核与 ASTRA 会诊承载，不做桩测试。
/// </summary>
public class ResinBudgetNormalizationTests
{
    private static AutoDomainParam NewDomainParam()
    {
        // 绕过调用 TaskContext 的构造器，仅设置预算构建读取的自动属性
        return (AutoDomainParam)RuntimeHelpers.GetUninitializedObject(typeof(AutoDomainParam));
    }

    [Fact]
    public void Budget_NotSpecifyResinUse_ReturnsEmptyRegardlessOfCounts()
    {
        var param = NewDomainParam();
        param.SpecifyResinUse = false;
        param.CondensedResinUseCount = 3;
        param.OriginalResin40UseCount = 2;

        var budget = ResinUseRecord.BuildFromDomainParam(param);

        Assert.Empty(budget);
    }

    [Fact]
    public void Budget_FullCounts_FollowsPublicFixedOrderWithOriginalResin20And40()
    {
        var param = NewDomainParam();
        param.SpecifyResinUse = true;
        param.CondensedResinUseCount = 1;
        param.OriginalResin40UseCount = 2;
        param.OriginalResin20UseCount = 3;
        param.OriginalResinUseCount = 4;
        param.TransientResinUseCount = 5;
        param.FragileResinUseCount = 6;

        var budget = ResinUseRecord.BuildFromDomainParam(param);

        Assert.Equal(
            new[] { "浓缩树脂", "原粹树脂40", "原粹树脂20", "原粹树脂", "须臾树脂", "脆弱树脂" },
            budget.Select(r => r.Name).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, budget.Select(r => r.MaxCount).ToArray());
        Assert.All(budget, r => Assert.Equal(r.MaxCount, r.RemainCount));
    }

    [Fact]
    public void Budget_OnlyOriginalResin20And40_KeepsDistinctBudgetIdentities()
    {
        var param = NewDomainParam();
        param.SpecifyResinUse = true;
        param.OriginalResin20UseCount = 2;
        param.OriginalResin40UseCount = 1;

        var budget = ResinUseRecord.BuildFromDomainParam(param);

        Assert.Equal(new[] { "原粹树脂40", "原粹树脂20" }, budget.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void Budget_SpecifyButAllZero_Throws()
    {
        var param = NewDomainParam();
        param.SpecifyResinUse = true;

        Assert.Throws<Exception>(() => ResinUseRecord.BuildFromDomainParam(param));
    }

    [Fact]
    public void Budget_StygianOverload_StaysWithoutOriginalResin20And40()
    {
        var param = (AutoStygianOnslaughtParam)RuntimeHelpers.GetUninitializedObject(typeof(AutoStygianOnslaughtParam));
        param.SpecifyResinUse = true;
        param.CondensedResinUseCount = 1;
        param.OriginalResinUseCount = 2;
        param.TransientResinUseCount = 3;
        param.FragileResinUseCount = 4;

        var budget = ResinUseRecord.BuildFromDomainParam(param);

        Assert.Equal(
            new[] { "浓缩树脂", "原粹树脂", "须臾树脂", "脆弱树脂" },
            budget.Select(r => r.Name).ToArray());
    }

    /// <summary>D07：地脉策略路径按文件存在性解析 .txt/.json（私静态方法反射调用）。</summary>
    private static string InvokeBuildAutoFightStrategyPath(AutoFightConfig config)
    {
        var method = typeof(AutoLeyLineOutcropTask).GetMethod("BuildAutoFightStrategyPath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object[] { config })!;
    }

    private static IDisposable UseTempStartUpPath(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "bgi-r34-d07-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "User", "AutoFight"));
        var original = Global.StartUpPath;
        Global.StartUpPath = root;
        return new RestoreOnDispose(original, root);
    }

    private sealed class RestoreOnDispose : IDisposable
    {
        private readonly string _original;
        private readonly string _root;

        public RestoreOnDispose(string original, string root)
        {
            _original = original;
            _root = root;
        }

        public void Dispose()
        {
            Global.StartUpPath = _original;
            try { Directory.Delete(_root, true); } catch { /* 临时目录清理失败不影响判定 */ }
        }
    }

    [Fact]
    public void LeyLineStrategyPath_JsonOnlyStrategy_ResolvesJsonInsteadOfBlocking()
    {
        using var _ = UseTempStartUpPath(out var root);
        var jsonPath = Path.Combine(root, "User", "AutoFight", "仅JSON策略.json");
        File.WriteAllText(jsonPath, "{}");
        var config = new AutoFightConfig { StrategyName = "仅JSON策略" };

        var resolved = InvokeBuildAutoFightStrategyPath(config);

        Assert.Equal(jsonPath, resolved);
    }

    [Fact]
    public void LeyLineStrategyPath_TxtExists_PrefersTxt()
    {
        using var _ = UseTempStartUpPath(out var root);
        var txtPath = Path.Combine(root, "User", "AutoFight", "普通策略.txt");
        File.WriteAllText(txtPath, "");
        var config = new AutoFightConfig { StrategyName = "普通策略" };

        var resolved = InvokeBuildAutoFightStrategyPath(config);

        Assert.Equal(txtPath, resolved);
    }

    [Fact]
    public void LeyLineStrategyPath_NeitherExists_Throws()
    {
        using var scope = UseTempStartUpPath(out _);
        var config = new AutoFightConfig { StrategyName = "不存在策略" };

        Assert.Throws<TargetInvocationException>(() => InvokeBuildAutoFightStrategyPath(config));
    }
}
