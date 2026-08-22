using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoFriendship;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoSwitchRoles;
using BetterGenshinImpact.GameTask.OcrSwitchWeapon;
using BetterGenshinImpact.GameTask.Shell;
using BetterGenshinImpact.GameTask.AutoOnline;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask;

/// <summary>
/// 独立任务注册表，用于配置组中按名称创建独立任务实例
/// </summary>
public static class SoloTaskRegistry
{
    /// <summary>
    /// 可在配置组中使用的独立任务名称列表
    /// </summary>
    public static readonly List<string> AvailableTasks =
    [
        "锄地一条龙（联机）",
        "好感任务自动完成",
        "OCR切换武器",
        "配对界面切换角色",
        "更新联机锄地线路",
        "联机锄地上线"
    ];

    /// <summary>
    /// 根据名称创建独立任务实例
    /// </summary>
    public static ISoloTask? CreateTask(string name, PathingPartyConfig? partyConfig,
        Dictionary<string, object?>? settings = null, string? groupName = null)
    {
        return name switch
        {
            // 新显示名 + 旧名（向后兼容：旧配置组已保存的任务名仍为"锄地一条龙"）
            "锄地一条龙（联机）" or "锄地一条龙" => new AutoHoeingTask(partyConfig, settings, groupName),
            "好感任务自动完成" => new AutoFriendshipTask(TaskContext.Instance().Config.AutoFriendshipConfig, partyConfig, settings, partyConfig?.AutoFightConfig),
            "OCR切换武器" => new OcrSwitchWeaponTask(partyConfig, settings, groupName),
            "配对界面切换角色" => new AutoSwitchRolesTask(partyConfig, settings, groupName),
            "更新联机锄地线路" => new ShellTask(ShellTaskParam.BuildFromConfig(
                @"Tools\AutoHoeingUpdater\AutoHoeingUpdater.exe --silent --target ""%CD%"" --force-download",
                new ShellConfig { Timeout = 120, NoWindow = true, Output = true })),
            "联机锄地上线" => new NotifyOnlineTask(),
            _ => null
        };
    }

    /// <summary>
    /// 获取独立任务的可配置参数定义
    /// </summary>
    public static List<SoloTaskSettingItem> GetSettingItems(string taskName)
    {
        return taskName switch
        {
            "锄地一条龙（联机）" or "锄地一条龙" => AutoHoeingTask.GetSettingDefinitions(),
            "好感任务自动完成" => AutoFriendshipTask.GetSettingDefinitions(),
            "OCR切换武器" => OcrSwitchWeaponTask.GetSettingDefinitions(),
            "配对界面切换角色" => AutoSwitchRolesTask.GetSettingDefinitions(),
            _ => new()
        };
    }
}

/// <summary>
/// 独立任务配置项定义
/// </summary>
public class SoloTaskSettingItem
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "text"; // text, number, select, bool
    public object? DefaultValue { get; set; }
    public List<string>? Options { get; set; }
}
