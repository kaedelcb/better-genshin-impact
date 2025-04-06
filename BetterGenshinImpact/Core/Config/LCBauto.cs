using CommunityToolkit.Mvvm.ComponentModel;
using System;


namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// 自动钓鱼配置
/// </summary>
[Serializable]
public partial class LCBautoConfig : ObservableObject
{

    /// </summary>
    [ObservableProperty] private bool _enabled = false;

}