using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// StartupFlowSchemeStore 导出/导入（启动中心方案文件进出）单测：
/// 往返深相等（含条件/动作分支树、电子狗触发链、旧版遗留字段）、
/// 单对象/整库数组两种文件形状、空文件落空、损坏文件上抛（由 VM 层记日志提示）、
/// 数组 null 项剔除、导入重名改名纯函数（MakeImportedNameUnique）。
/// 纯逻辑测试不碰 UI 线程；涉盘用例走临时目录，finally 清理。
/// </summary>
public class StartupFlowSchemeStoreTests
{
    /// <summary>锚点时间：重名后缀「-导入0913 1530」由此推出。</summary>
    private static readonly DateTime Stamp = new(2026, 9, 13, 15, 30, 0);

    /// <summary>带嵌套分支与电子狗触发链的富配置，覆盖序列化树的全部形态。</summary>
    private static StartupFlowScheme MakeRichScheme(string name) => new()
    {
        Name = name,
        SavedAt = new DateTime(2026, 9, 13, 10, 0, 0),
        Config = new StartupFlowConfig
        {
            Enabled = true,
            DelaySeconds = 7,
            AutoExpandBranch = false,
            Steps =
            [
                new StartupStep
                {
                    NodeType = "condition",
                    Kind = StartupStepKinds.TimeRange,
                    Name = "夜间窗口",
                    TimeStart = "23:00",
                    TimeEnd = "06:00",
                    TrueSteps =
                    [
                        new StartupStep
                        {
                            NodeType = "action",
                            Kind = StartupStepKinds.Watchdog,
                            WatchKind = StartupStepKinds.BgiTaskRunning,
                            ExpectRunning = false,
                            WatchIntervalSeconds = 15,
                            WatchRepeat = false,
                            WatchConfirmSeconds = 2,
                            WatchConfirmTimes = 3,
                            FireSteps =
                            [
                                new StartupStep { NodeType = "action", Kind = StartupStepKinds.RunCmd, Arguments = "echo hi" },
                            ],
                        },
                    ],
                    FalseSteps =
                    [
                        new StartupStep { NodeType = "action", Kind = StartupStepKinds.Wait, WaitSeconds = 3 },
                    ],
                },
                new StartupStep
                {
                    NodeType = "action",
                    Kind = StartupStepKinds.StartBgi,
                    Path = @"C:\BGI\BetterGenshinImpact.exe",
                    Arguments = "--fast",
                    KillBeforeStart = true,
                    StatusSource = StartupStatusSource.UserName,
                    StatusTargetUser = "tea",
                    TaskName = "旧版遗留字段",
                },
            ],
        },
    };

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mha-scheme-store-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteFile(string dir, string fileName, string content)
    {
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Export_Import_RoundTrip_PreservesDeepConfig()
    {
        var dir = NewTempDir();
        try
        {
            var path = WriteFile(dir, "scheme.json", "");
            var scheme = MakeRichScheme("夜班流程");

            StartupFlowSchemeStore.ExportScheme(scheme, path);
            var imported = StartupFlowSchemeStore.ImportSchemes(path);

            var item = Assert.Single(imported);
            // 深相等：两侧用同一序列化器重新序列化后逐字节比对（嵌套分支/触发链/遗留字段全在内）
            var options = new JsonSerializerOptions { WriteIndented = true };
            Assert.Equal(JsonSerializer.Serialize(scheme, options), JsonSerializer.Serialize(item, options));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Import_AcceptsLibraryArrayShape_AndKeepsOrder()
    {
        var dir = NewTempDir();
        try
        {
            var path = WriteFile(dir, "lib.json",
                """
                [
                  { "name": "甲", "savedAt": "2026-09-13T10:00:00", "config": { "enabled": true, "delaySeconds": 5, "steps": [] } },
                  { "name": "乙", "savedAt": "2026-09-13T11:00:00", "config": { "steps": [] } }
                ]
                """);

            var imported = StartupFlowSchemeStore.ImportSchemes(path);

            Assert.Equal(2, imported.Count);
            Assert.Equal(["甲", "乙"], imported.Select(s => s.Name));
            Assert.True(imported[0].Config.Enabled);
            Assert.Equal(5, imported[0].Config.DelaySeconds);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Import_AcceptsSingleObjectShape()
    {
        var dir = NewTempDir();
        try
        {
            var path = WriteFile(dir, "one.json",
                """{ "name": "单人", "savedAt": "2026-09-13T10:00:00", "config": { "steps": [] } }""");

            var imported = StartupFlowSchemeStore.ImportSchemes(path);

            var item = Assert.Single(imported);
            Assert.Equal("单人", item.Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Import_EmptyFile_ReturnsEmptyList()
    {
        var dir = NewTempDir();
        try
        {
            var path = WriteFile(dir, "empty.json", "   \n ");

            Assert.Empty(StartupFlowSchemeStore.ImportSchemes(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Import_CorruptJson_Throws()
    {
        var dir = NewTempDir();
        try
        {
            var path = WriteFile(dir, "broken.json", "{ 这不是 JSON");

            // 纪律与 Load/SaveAll 不同：导入是用户主动操作，损坏要上抛让调用方记日志，不能静默落空
            Assert.ThrowsAny<JsonException>(() => StartupFlowSchemeStore.ImportSchemes(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Import_ArrayWithNullEntries_FiltersNulls()
    {
        var dir = NewTempDir();
        try
        {
            var path = WriteFile(dir, "withnull.json",
                """
                [ null, { "name": "有效", "savedAt": "2026-09-13T10:00:00", "config": { "steps": [] } } ]
                """);

            var item = Assert.Single(StartupFlowSchemeStore.ImportSchemes(path));
            Assert.Equal("有效", item.Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MakeImportedNameUnique_NoConflict_KeepsTrimmedName()
    {
        Assert.Equal("甲", StartupFlowSchemeStore.MakeImportedNameUnique("  甲  ", ["乙"], Stamp));
    }

    [Fact]
    public void MakeImportedNameUnique_Conflict_AppendsImportStamp()
    {
        Assert.Equal("甲-导入0913 1530",
            StartupFlowSchemeStore.MakeImportedNameUnique("甲", ["甲", "乙"], Stamp));
    }

    [Fact]
    public void MakeImportedNameUnique_StampConflict_AppendsSequence()
    {
        Assert.Equal("甲-导入0913 1530(2)",
            StartupFlowSchemeStore.MakeImportedNameUnique("甲", ["甲", "甲-导入0913 1530"], Stamp));
    }

    [Fact]
    public void MakeImportedNameUnique_EmptyName_FallsBackToDefault()
    {
        Assert.Equal("导入方案 0913 1530",
            StartupFlowSchemeStore.MakeImportedNameUnique("  ", [], Stamp));
    }
}
