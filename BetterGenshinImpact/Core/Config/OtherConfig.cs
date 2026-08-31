using System;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Model;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterGenshinImpact.Core.Config;


[Serializable]
public partial class OtherConfig : ObservableObject
{
    //调度器任务和部分独立任务，失去焦点，自动激活游戏窗口
    [ObservableProperty]
    private bool _restoreFocusOnLostEnabled = false;
    //窗口类名优先检测：原版仅靠进程名+MainWindowHandle 查找游戏窗口，该句柄为 0 或指向错误窗口时会找不到。开启后优先按窗口类名枚举检测，未命中再按进程枚举取客户区最大的可见窗口，仍不命中才回退原版方式。即时生效（每次查找窗口时读取），默认关闭，关闭时行为与旧版完全一致
    [ObservableProperty]
    private bool _windowClassDetectPreferred = false;
    //自动领取派遣任务城市
    [ObservableProperty]
    private string _autoFetchDispatchAdventurersGuildCountry = "无";
    //服务器时区偏移量
    [ObservableProperty]
    private TimeSpan _serverTimeZoneOffset = TimeSpan.FromHours(8);
    [ObservableProperty]
    private AutoRestart _autoRestartConfig = new();
    //锄地规划
    [ObservableProperty]
    private FarmingPlan _farmingPlanConfig = new();
    
    [ObservableProperty]
    private Miyoushe _miyousheConfig = new();
    //OCR配置
    [ObservableProperty]
    private Ocr _ocrConfig = new();
    
    //网络检测
    [ObservableProperty]
    private bool _networkDetectionConfig = false;
    
    //网络检测网址
    [ObservableProperty]
    private string _networkDetectionUrl = "www.baidu.com";
    
    //网络检测间隔时间
    [ObservableProperty]
    private int _networkDetectionInterval = 5;

    // 自定义角色配置
    [ObservableProperty]
    private CustomAvatarConfig _customAvatarConfigOut = new CustomAvatarConfig();
    
    [ObservableProperty]
    private int _setTime = 7;
    
    public partial class CustomAvatarConfig : ObservableObject
    {
        //自定义角色开关
        public  bool CustomAvatarEnabled { set; get; } = false;
        
        // 自定义角色1名称,初始化用于举例
        public string CustomAvatar1Name { get; set; } = "申鹤";
        public string CustomAvatar1Name2 { get; set; } = string.Empty;
        public string CustomAvatar1Name3 { get; set; } = string.Empty;
    
        // 自定义角色1假装名称
        public string CustomAvatar1DisplayName { get; set; } = "哥伦比娅";
    
        // 自定义角色2名称
        public string CustomAvatar2Name { get; set; } = "申鹤";
        public string CustomAvatar2Name2 { get; set; } = "甘雨";
        public string CustomAvatar2Name3 { get; set; } = "琴";
    
        // 自定义角色2假装名称
        public string CustomAvatar2DisplayName { get; set; } = "琴";
    
        // 自定义置信度1
        public double CustomAvatar1Confidence { get; set; } = 0.8;
    
        // 自定义置信度2
        public double CustomAvatar2Confidence { get; set; } = 0.8;
        
        //识别错误后强制使用角色
        public string CustomAvatarForceUseList { get; set; } = "钟离, 纳西妲, 雷电将军, 芙宁娜";
    }
    
    public partial class AutoRestart : ObservableObject
    {
        [ObservableProperty]
        private bool _enabled = false;
        
        //调度器任务连续异常退出几次任务自动重启
        [ObservableProperty]
        private int _failureCount = 5;
        
        //是否同时重启游戏，需开启首页启动配置：同时启动原神、自动进入游戏，此配置才会生效
        [ObservableProperty]
        private bool _restartGameTogether = false;
        
        //锄地脚本，如果打架次数不一致，则判定任务失败。
        [ObservableProperty]
        private bool _isFightFailureExceptional = false;
        
        //任何追踪任务，未走完全路径结束，视为失败。
        [ObservableProperty]
        private bool _isPathingFailureExceptional = false;
        
    }
    
    public partial class Miyoushe : ObservableObject
    {

        //cookie
        [ObservableProperty]
        private string _cookie = "";
        
        //与调度器日志处相互同步cookie
        [ObservableProperty]
        private bool _logSyncCookie = true;
        
    }
    public partial class MiyousheDataSupport : ObservableObject
    {
        [ObservableProperty]
        private bool _enabled = false;
        
        //日精英上限
        [ObservableProperty]
        private int _dailyEliteCap = 400;
        
        //日小怪上限
        [ObservableProperty]
        private int _dailyMobCap = 2000;
    }
    public partial class FarmingPlan : ObservableObject
    {


        [ObservableProperty]
        private MiyousheDataSupport _miyousheDataConfig = new();

        [ObservableProperty]
        private bool _enabled = false;
        
        //日精英上限
        [ObservableProperty]
        private int _dailyEliteCap = 400;
        
        //日小怪上限
        [ObservableProperty]
        private int _dailyMobCap = 2000;
        
    }
    
    public partial class Ocr : ObservableObject
    {
        /// <summary>
        ///     PaddleOCR模型配置
        /// </summary>
        [ObservableProperty]
        private PaddleOcrModelConfig _paddleOcrModelConfig = PaddleOcrModelConfig.V4Auto;
    }
    
    //public partial class OtherConfig : ObservableObject
    
    /// <summary>
    /// 游戏语言名称
    /// </summary>
    [ObservableProperty]
    private string _gameCultureInfoName = "zh-Hans";

    /// <summary>
    /// BGI界面语言名称
    /// </summary>
    [ObservableProperty]
    private string _uiCultureInfoName = "zh-Hans";

    /// <summary>
    /// GitHub Personal Access Token（可选，用于提高 GitHub API 速率限制）
    /// </summary>
    [ObservableProperty]
    private string _gitHubToken = string.Empty;
}
