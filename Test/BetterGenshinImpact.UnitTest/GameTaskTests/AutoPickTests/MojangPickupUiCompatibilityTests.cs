using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.GameTask.AutoPick;
using BetterGenshinImpact.ViewModel.Pages.View;
using BetterGenshinImpact.ViewModel.Windows;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPickTests;

[Collection("GlobalUserRoot")]
public sealed class MojangPickupUiCompatibilityTests
{
    private static readonly MojangTemplateInfo[] Catalog =
    [
        new("薄荷", "白", 2),
        new("可取消项", "绿", 4),
    ];

    [Fact]
    public void GlobalEditor_PreservesUncataloguedNamesAndLaterBagFullAdditions()
    {
        using var isolation = new IsolatedUserRoot();
        MojangPickFilter.Save(["未收录旧项", "可取消项"]);

        RunSta(() =>
        {
            var vm = new MojangBlackListConfigViewModel(templates: Catalog);
            var uncatalogued = Assert.Single(vm.GlobalList.Items.Where(item => item.Name == "未收录旧项"));
            Assert.True(uncatalogued.IsChecked);
            Assert.Equal("未收录", uncatalogued.Color);
            Assert.Equal(0, vm.InitialTabIndex);

            // 窗口快照之后新增的禁拾项不得被保存旧窗口覆盖。
            MojangPickFilter.Update(["背包满新增项"], []);
            vm.GlobalList.Items.Single(item => item.Name == "可取消项").IsChecked = false;
            vm.GlobalList.Items.Single(item => item.Name == "薄荷").IsChecked = true;
            bool? closeResult = null;
            vm.CloseRequested += result => closeResult = result;

            vm.SaveCommand.Execute(null);

            Assert.Equal<bool?>(true, closeResult);
            Assert.True(MojangPickFilter.Load().SetEquals(["未收录旧项", "背包满新增项", "薄荷"]));
        });

        Assert.Equal(isolation.UserRoot, Global.UserRoot);
        Assert.False(Directory.Exists(Path.Combine(isolation.PackageRoot, "User")));
    }

    [Fact]
    public void RemoteEditor_SaveUpdatesOnlyRemoteMemoryAndKeepsLocalFilesUnchanged()
    {
        using var isolation = new IsolatedUserRoot();
        var localGroupDirectory = Path.Combine(isolation.UserRoot, "ScriptGroup");
        Directory.CreateDirectory(localGroupDirectory);
        File.WriteAllText(Path.Combine(localGroupDirectory, "同名组.json"),
            "{\"name\":\"同名组\",\"config\":{\"autoPickConfig\":{\"whiteList\":[\"本机白名单\"],\"blackList\":[\"本机黑名单\"]}}}",
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(isolation.UserRoot, "mojang_black_lists.txt"), "本机全局禁拾", new UTF8Encoding(false));
        var before = isolation.SnapshotFiles();
        var remoteGroup = new ScriptGroup { Name = "同名组" };
        remoteGroup.Config.AutoPickConfig.WhiteList = ["远端未收录白名单"];
        remoteGroup.Config.AutoPickConfig.BlackList = ["远端未收录黑名单"];

        RunSta(() =>
        {
            var vm = new MojangBlackListConfigViewModel(remoteGroup, isRemoteEdit: true, templates: Catalog);
            Assert.True(vm.IsGroupScope);
            Assert.False(vm.IsGlobalScope);
            Assert.Equal(1, vm.InitialTabIndex);
            Assert.Contains("保存并回传", vm.SaveHint);
            vm.GroupWhiteList.Items.Single(item => item.Name == "薄荷").IsChecked = true;
            vm.GroupBlackList.Items.Single(item => item.Name == "可取消项").IsChecked = true;
            bool? closeResult = null;
            vm.CloseRequested += result => closeResult = result;

            vm.SaveCommand.Execute(null);

            Assert.Equal<bool?>(true, closeResult);
            Assert.True(remoteGroup.Config.AutoPickConfig.WhiteList.SetEquals(["远端未收录白名单", "薄荷"]));
            Assert.True(remoteGroup.Config.AutoPickConfig.BlackList.SetEquals(["远端未收录黑名单", "可取消项"]));
        });

        var after = isolation.SnapshotFiles();
        Assert.Equal(before.Keys.OrderBy(path => path), after.Keys.OrderBy(path => path));
        foreach (var entry in before)
        {
            Assert.Equal(entry.Value, after[entry.Key]);
        }
        Assert.False(Directory.Exists(Path.Combine(isolation.PackageRoot, "User")));
    }

    [Fact]
    public void GroupSettingsConstructor_KeepsOriginalRemoteStrategyParametersBeforeNewOptions()
    {
        // 仅检查已编译接口，不启动 App/DI，也不让默认策略目录触发本机扫描。
        var constructor = typeof(ScriptGroupConfigViewModel).GetConstructor(
        [
            typeof(AllConfig), typeof(ScriptGroupConfig),
            typeof(IEnumerable<string>), typeof(IEnumerable<string>),
            typeof(ScriptGroup), typeof(bool),
        ]);
        Assert.NotNull(constructor);
        var parameters = constructor!.GetParameters();
        Assert.Equal("strategyOverride", parameters[2].Name);
        Assert.Equal("combatStrategyOverride", parameters[3].Name);
        Assert.True(parameters[2].IsOptional);
        Assert.True(parameters[3].IsOptional);
        Assert.Null(parameters[2].DefaultValue);
        Assert.Null(parameters[3].DefaultValue);
        Assert.Equal("group", parameters[4].Name);
        Assert.True(parameters[4].IsOptional);
        Assert.Null(parameters[4].DefaultValue);
        Assert.Equal("isRemoteEdit", parameters[5].Name);
        Assert.True(parameters[5].IsOptional);
        Assert.Equal(false, parameters[5].DefaultValue);
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private sealed class IsolatedUserRoot : IDisposable
    {
        private readonly string _originalStartUpPath = Global.StartUpPath;
        private readonly string _temporaryRoot = Path.Combine(Path.GetTempPath(), "mojang-ui-compat-" + Guid.NewGuid().ToString("N"));
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
            Assert.Equal(UserRoot, Global.UserRoot);
        }

        public Dictionary<string, byte[]> SnapshotFiles() => Directory.GetFiles(_temporaryRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(_temporaryRoot, path), File.ReadAllBytes);

        public void Dispose()
        {
            Global.StartUpPath = _originalStartUpPath;
            var root = Path.GetFullPath(_temporaryRoot);
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(root).StartsWith("mojang-ui-compat-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("测试隔离目录未通过清理边界检查。");
            }
            Directory.Delete(root, recursive: true);
        }
    }
}
