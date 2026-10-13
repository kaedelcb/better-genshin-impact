using BetterGenshinImpact.Core.Config;
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace BetterGenshinImpact.Core.Script.Group;

[Serializable]
public partial class ScriptGroupConfig : ObservableObject
{
    [ObservableProperty]
    private PathingPartyConfig _pathingConfig = new();

    /// <summary>
    /// Shell 执行配置
    /// </summary>
    [ObservableProperty]
    private ShellConfig _shellConfig = new();
    
    /// <summary>
    /// 是否启用 Shell 执行配置
    /// </summary>
    [ObservableProperty]
    private bool _enableShellConfig;

    /// <summary>本体模板拾取的配置组级白名单/黑名单，随配置组保存。</summary>
    [ObservableProperty]
    private AutoPickGroupConfig _autoPickConfig = new();
}
