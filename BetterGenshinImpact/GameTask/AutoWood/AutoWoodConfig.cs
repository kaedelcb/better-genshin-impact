using CommunityToolkit.Mvvm.ComponentModel;
using System;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoWood.Assets;
using BetterGenshinImpact.GameTask.AutoWood.Utils;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Genshin.Settings;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator.Extensions;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using static Vanara.PInvoke.User32;
using GC = System.GC;
using OpenCvSharp;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.GameTask.Model.Area;

namespace BetterGenshinImpact.GameTask.AutoWood;

/// <summary>
/// 自动伐木配置
/// </summary>
[Serializable]
public partial class AutoWoodConfig : ObservableObject
{
    /// <summary>
    /// 使用小道具后的额外延迟（毫秒）
    /// </summary>
    [ObservableProperty]
    private int _afterZSleepDelay = 0;

    /// <summary>
    /// 木材数量OCR是否启用
    /// </summary>
    [ObservableProperty]
    private bool _woodCountOcrEnabled = false;
    
    /// <summary>    
    /// 上限识别类型列表
    /// </summary>
    [ObservableProperty]
    private List<string> _maxWoodTypeList =
    [
        "总数上限", "指定木材上限","任意一种木材上限"
    ];
    
    /// <summary>    
    /// 上限统计类型
    /// </summary>
    [ObservableProperty]
    private string _maxWoodType = "总数上限";
    
    /// <summary>
    /// 木材选择
    /// </summary>
    [ObservableProperty]
    private List<string> _existWoods =
    [
        "椴木","枫木","萃华木","垂香木", "竹节","桦木","杉木","松木","却砂木", "证悟木", "梦见木",  "御伽木",
        "业果木", "辉木", "白梣木", "炬木", "香柏木","悬铃木","燃爆木", "白栗栎木"  , "灰灰楼林木" , "桃椰子木"
        , "柽木" , "刺葵木","孔雀木","桤木","夏栎木","榛木","银冷杉木"
    ];
    
    /// <summary>
    /// 上限统计类型单中材料上限木材名称
    /// </summary>
    [ObservableProperty]
    private string _singleWoodLimit = "椴木";

    /// <summary>
    /// 使用进出千星奇域刷新CD
    /// </summary>
    [ObservableProperty]
    private bool _useWonderlandRefresh = true;

    // /// <summary>
    // /// 按下两次ESC键，原因见：
    // /// https://github.com/babalae/better-genshin-impact/issues/235
    // /// </summary>
    // [ObservableProperty]
    // private bool _pressTwoEscEnabled = false;
}
