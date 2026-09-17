using BetterGenshinImpact.Core.Script;

namespace BetterGenshinImpact.UnitTest.CoreTests;

// ScriptRouteProgress / ScriptTaskProgressAdapter 是进程级静态状态，必须与脚本进度观察测试串行执行，
// 否则并行用例之间会互相清空适配器状态（曾导致本测试的"限时剩余"断言随机失败）。
[Collection("ScriptRouteProgressState")]
public class ScriptTaskProgressAdapterTests
{
    [Fact]
    public void HostAdapter_GeneratesProgressForFourScripts_WithoutScriptInjection()
    {
        try
        {
            ScriptRouteProgress.BeginProject("采集cd管理", new Dictionary<string, object?>
            {
                ["maxRuntimeMinutes"] = 120
            });
            Assert.Contains("准备采集", ScriptRouteProgress.ProgressText);
            Assert.Contains("限时剩余", ScriptRouteProgress.ProgressText);
            ScriptRouteProgress.StartRoute("assets/采集路线/甜甜花.json");
            Assert.Contains("采集路线执行中", ScriptRouteProgress.ProgressText);
            Assert.Contains("第 1 条主要路线", ScriptRouteProgress.ProgressText);

            ScriptRouteProgress.Clear();
            ScriptRouteProgress.BeginProject("AAA-Artifacts-Bulk-Supply", null);
            ScriptRouteProgress.StartRoute("assets/furina/强制白芙.json");
            Assert.DoesNotContain("主要路线", ScriptRouteProgress.ProgressText);
            ScriptRouteProgress.CompleteRoute("assets/furina/强制白芙.json");
            ScriptRouteProgress.StartRoute("assets/ArtifactsPath/优先收尾路线/执行/01.json");
            Assert.Contains("收尾路线", ScriptRouteProgress.ProgressText);
            Assert.Contains("第 1 条主要路线", ScriptRouteProgress.ProgressText);

            ScriptRouteProgress.Clear();
            ScriptRouteProgress.BeginProject("ArtifactsGroupPurchasing", null);
            ScriptRouteProgress.StartRoute("assets/ArtifactsPath/2P/占位/02.json");
            Assert.Contains("成员占位路线", ScriptRouteProgress.ProgressText);

            ScriptRouteProgress.Clear();
            ScriptRouteProgress.BeginProject("AutoFishingTeyvat", null);
            ScriptRouteProgress.StartRoute("assets/pathing/蒙德-白天-望风山地.json");
            Assert.Contains("前往钓鱼点", ScriptRouteProgress.ProgressText);
            Assert.Contains("第 1 个钓鱼点", ScriptRouteProgress.ProgressText);
            Assert.Equal("蒙德-白天-望风山地.json", ScriptRouteProgress.CurrentRouteName);
            ScriptRouteProgress.CompleteRoute("assets/pathing/蒙德-白天-望风山地.json");
            Assert.Contains("垂钓与点位处理", ScriptRouteProgress.ProgressText);

            ScriptRouteProgress.SetProgressText("脚本显式状态");
            Assert.Contains("垂钓与点位处理", ScriptRouteProgress.ProgressText);
            Assert.Contains("脚本状态 脚本显式状态", ScriptRouteProgress.ProgressText);
            ScriptRouteProgress.SetProgressText(null);
            Assert.Contains("垂钓与点位处理", ScriptRouteProgress.ProgressText);

            ScriptRouteProgress.Clear();
            ScriptRouteProgress.BeginProject("UnadaptedScript", null);
            Assert.Null(ScriptRouteProgress.ProgressText);
        }
        finally
        {
            ScriptRouteProgress.Clear();
        }
    }
}
