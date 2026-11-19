using System.Collections.Generic;
using System.Reflection;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoHoeing;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>替换全局 ConfigService.Config 的测试不能与其他测试并行，否则会互相改到对方的全局配置。</summary>
[CollectionDefinition("GlobalConfigService", DisableParallelization = true)]
public sealed class GlobalConfigServiceCollection { }

/// <summary>
/// 协调服务器地址照常可在配置组里填写；空值/旧版示例文字按官方地址处理。
/// 走真实 ApplySettingsOverride，不是模拟逻辑；全局配置用本仓库测试既有的 ConfigService 反射注入方式。
/// </summary>
[Collection("GlobalConfigService")]
public class CoordinatorServerAddressTests
{
    private static AllConfig InjectGlobal(string address)
    {
        var config = new AllConfig();
        config.AutoHoeingConfig.CoordinatorServerUrl = address;
        typeof(BetterGenshinImpact.Service.ConfigService)
            .GetField("<Config>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, config);
        return config;
    }

    private static AutoHoeingConfig RunOverride(Dictionary<string, object?> settings, AutoHoeingConfig working)
    {
        var task = new AutoHoeingTask(settings: settings);
        typeof(AutoHoeingTask).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(task, working);
        typeof(AutoHoeingTask).GetMethod("ApplySettingsOverride", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(task, null);
        return working;
    }

    [Fact]
    public void ConfigGroupAddressStillWorksAndCanBeChanged()
    {
        // 配置组里填的联机地址照常生效（用户可以在锄地一条龙设置里改地址）。
        InjectGlobal("http://www.autobgi.cn:8080");
        var result = RunOverride(new Dictionary<string, object?>
        {
            ["multiplayerEnabled"] = true,
            ["coordinatorServerUrl"] = "http://121.4.78.52:8080"
        }, new AutoHoeingConfig { CoordinatorServerUrl = "http://www.autobgi.cn:8080" });
        Assert.Equal("http://121.4.78.52:8080", result.CoordinatorServerUrl);
    }

    [Fact]
    public void LegacyPlaceholderOrEmptyFallsBackToTheOfficialAddressWithoutRewritingConfig()
    {
        var global = InjectGlobal("举例：https://bgi-sync.example.com(需部署自建服务)");
        var result = RunOverride(new Dictionary<string, object?> { ["multiplayerEnabled"] = true },
            new AutoHoeingConfig { CoordinatorServerUrl = global.AutoHoeingConfig.CoordinatorServerUrl });
        Assert.Equal("https://autohoeing.autobgi.cn", result.CoordinatorServerUrl);
        Assert.Equal("举例：https://bgi-sync.example.com(需部署自建服务)", global.AutoHoeingConfig.CoordinatorServerUrl);
    }

    [Fact]
    public void SavingGroupWithoutOwnAddressKeepsFollowingTheSettingsPage()
    {
        // 配置组没填地址：只打开再保存，不能把设置页当时的地址固定进配置组
        var settings = new Dictionary<string, object?> { ["multiplayerEnabled"] = true };
        var global = new AutoHoeingConfig { CoordinatorServerUrl = "http://www.autobgi.cn:8080" };
        var vm = new BetterGenshinImpact.ViewModel.Pages.View.MultiplayerHoeingSettingsViewModel(settings, global, new[] { "1" }, "1");
        Assert.Equal("https://autohoeing.autobgi.cn", vm.CoordinatorServerUrl);
        vm.WriteMultiplayerSettings(settings);
        Assert.False(settings.ContainsKey("coordinatorServerUrl"));
        Assert.Equal("http://www.autobgi.cn:8080", global.CoordinatorServerUrl);
    }

    [Fact]
    public void GroupAddressIsSavedWhenTheUserChangesItOrTheGroupAlreadyHadOne()
    {
        var changed = new Dictionary<string, object?> { ["multiplayerEnabled"] = true };
        var vm = new BetterGenshinImpact.ViewModel.Pages.View.MultiplayerHoeingSettingsViewModel(changed, new AutoHoeingConfig(), new[] { "1" }, "1");
        vm.CoordinatorServerUrl = "https://my-own.example.org";
        vm.WriteMultiplayerSettings(changed);
        Assert.Equal("https://my-own.example.org", changed["coordinatorServerUrl"]);

        var own = new Dictionary<string, object?> { ["coordinatorServerUrl"] = "https://autohoeing.autobgi.cn/hub" };
        var vm2 = new BetterGenshinImpact.ViewModel.Pages.View.MultiplayerHoeingSettingsViewModel(own, new AutoHoeingConfig(), new[] { "1" }, "1");
        vm2.WriteMultiplayerSettings(own);
        Assert.Equal("https://autohoeing.autobgi.cn/hub", own["coordinatorServerUrl"]);
    }

    [Fact]
    public void TaskSettingDefinitionsStillOfferTheAddress()
    {
        InjectGlobal(CoordinatorServerAddress.RecommendedAddress); // GetSettingDefinitions 读取全局配置作为各项默认值
        Assert.Contains(AutoHoeingTask.GetSettingDefinitions(), item => item.Name == "coordinatorServerUrl");
    }
}
