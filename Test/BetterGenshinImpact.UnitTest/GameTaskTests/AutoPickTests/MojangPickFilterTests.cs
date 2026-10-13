using System.Text.Json;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoPick;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPickTests;

[Collection("GlobalUserRoot")]
public sealed class MojangPickFilterTests
{
    [Fact]
    public void BlacklistPersistence_UsesExplicitIsolatedUserRoot()
    {
        using var isolation = new IsolatedUserRoot();

        MojangPickFilter.Save(["薄荷", "甜甜花", "薄荷"]);

        Assert.Equal(isolation.UserRoot, Global.UserRoot);
        Assert.True(File.Exists(Path.Combine(isolation.UserRoot, "mojang_black_lists.txt")));
        Assert.True(MojangPickFilter.Load().SetEquals(["薄荷", "甜甜花"]));
        Assert.False(Directory.Exists(Path.Combine(isolation.PackageRoot, "User")));
    }

    [Fact]
    public void IndependentEditorDeltas_PreserveLatestDiskAdditions()
    {
        using var isolation = new IsolatedUserRoot();
        MojangPickFilter.Save(["原有项", "待移除项"]);
        var firstEditor = MojangPickFilter.Load();
        var secondEditor = MojangPickFilter.Load();

        var firstDesired = new HashSet<string>(firstEditor) { "另一个窗口新增项" };
        MojangPickFilter.Update(firstDesired.Except(firstEditor), firstEditor.Except(firstDesired));
        var secondDesired = new HashSet<string>(secondEditor) { "当前窗口新增项" };
        secondDesired.Remove("待移除项");
        MojangPickFilter.Update(secondDesired.Except(secondEditor), secondEditor.Except(secondDesired));

        Assert.True(MojangPickFilter.Load().SetEquals(["原有项", "另一个窗口新增项", "当前窗口新增项"]));
    }

    [Fact]
    public void BagFullAddition_PreservesChangesMadeSinceFilterInit()
    {
        using var isolation = new IsolatedUserRoot();
        MojangPickFilter.Save(["旧全局项"]);
        var filter = new MojangPickFilter();
        filter.Init();
        MojangPickFilter.Update(["界面新增项"], []);

        filter.AddToBlackList(["背包满物品"]);

        Assert.True(MojangPickFilter.Load().SetEquals(["旧全局项", "界面新增项", "背包满物品"]));
        Assert.False(filter.ShouldPick("背包满物品"));
    }

    [Fact]
    public void GroupPriorityAndSwitching_KeepGlobalAndGroupListsSeparate()
    {
        using var isolation = new IsolatedUserRoot();
        MojangPickFilter.Save(["薄荷", "全局禁拾"]);
        var filter = new MojangPickFilter();
        filter.Init();
        var group = new AutoPickGroupConfig
        {
            WhiteList = ["薄荷", "甜甜花"],
            BlackList = ["甜甜花"],
        };
        filter.SetCurrentGroup("采集组", group);

        Assert.Equal("采集组", filter.CurrentGroupName);
        Assert.True(filter.ShouldPick("薄荷"));
        Assert.False(filter.ShouldPick("甜甜花"));
        Assert.False(filter.ShouldPick("全局禁拾"));
        Assert.False(filter.ShouldPick("调查"));
        Assert.True(filter.ShouldPick("日落果"));

        filter.SetCurrentGroup(null, null);

        Assert.Null(filter.CurrentGroupName);
        Assert.False(filter.ShouldPick("薄荷"));
        Assert.True(filter.ShouldPick("甜甜花"));
        Assert.True(MojangPickFilter.Load().SetEquals(["薄荷", "全局禁拾"]));
        Assert.True(group.WhiteList.SetEquals(["薄荷", "甜甜花"]));
        Assert.True(group.BlackList.SetEquals(["甜甜花"]));
    }

    private sealed class IsolatedUserRoot : IDisposable
    {
        private readonly string _originalStartUpPath = Global.StartUpPath;
        private readonly string _temporaryRoot = Path.Combine(Path.GetTempPath(), "mojang-pick-filter-" + Guid.NewGuid().ToString("N"));
        public string PackageRoot { get; }
        public string UserRoot { get; }

        public IsolatedUserRoot()
        {
            PackageRoot = Path.Combine(_temporaryRoot, "package");
            UserRoot = Path.Combine(_temporaryRoot, "isolated-user");
            Directory.CreateDirectory(PackageRoot);
            Directory.CreateDirectory(UserRoot);
            File.WriteAllText(Path.Combine(PackageRoot, "mistletoe-package.json"), JsonSerializer.Serialize(new
            {
                schema = "mistletoe.local-package", schemaVersion = 1, ipcIsolation = true, userRoot = UserRoot,
            }));
            Global.StartUpPath = PackageRoot;
        }

        public void Dispose()
        {
            Global.StartUpPath = _originalStartUpPath;
            var root = Path.GetFullPath(_temporaryRoot);
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(root).StartsWith("mojang-pick-filter-", StringComparison.Ordinal))
                throw new InvalidOperationException("测试隔离目录未通过清理边界检查。");
            Directory.Delete(root, recursive: true);
        }
    }
}
