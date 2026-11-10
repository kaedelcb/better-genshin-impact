using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
///     原神启动配置
/// </summary>
[Serializable]
public partial class GenshinStartConfig : ObservableObject
{
    // /// <summary>
    // ///     自动点击月卡
    // /// </summary>
    // [ObservableProperty]
    // private bool _autoClickBlessingOfTheWelkinMoonEnabled;

    /// <summary>
    ///     自动进入游戏（开门）
    /// </summary>
    [ObservableProperty]
    private bool _autoEnterGameEnabled = true;

    /// <summary>
    ///     原神启动参数
    /// </summary>
    [ObservableProperty]
    private string _genshinStartArgs = "";

    /// <summary>
    ///     原神安装路径
    /// </summary>
    [ObservableProperty]
    private string _installPath = "";

    /// <summary>
    ///     联动启动原神本体
    /// </summary>
    [ObservableProperty]
    private bool _linkedStartEnabled = true;

    /// <summary>
    ///     使用Starward同步记录时间
    /// </summary>
    [ObservableProperty]
    private bool _recordGameTimeEnabled = false;

    [ObservableProperty]
    private bool _startGameWithCmd = false;

    /// <summary>
    ///     启动前自动关闭原神 HDR（删除原神 HDR 对应注册表键）
    /// </summary>
    [ObservableProperty]
    private bool _autoDisableGenshinHdrEnabled = true;

    /// <summary>
    ///     联机锄地组队阶段：游戏长时间进不了主界面时，自动关闭并按 1920x1080 窗口化参数重启一次
    /// </summary>
    [ObservableProperty]
    private bool _restartGameWhenNotReadyInMultiplayer = true;

    /// <summary>
    ///     上述自动重启前的等待秒数
    /// </summary>
    [ObservableProperty]
    private int _multiplayerRestartGameAfterSeconds = 150;
}
