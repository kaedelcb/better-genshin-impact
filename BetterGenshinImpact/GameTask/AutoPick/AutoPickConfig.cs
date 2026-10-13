using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Text.Json.Serialization;

namespace BetterGenshinImpact.GameTask.AutoPick
{
    public enum AutoPickMode
    {
        Blacklist,
        Whitelist
    }

    /// <summary>
    /// 非16:9分辨率下可能无法正常工作
    /// </summary>
    [Serializable]
    public partial class AutoPickConfig : ObservableObject
    {
        /// <summary>
        /// 触发器是否启用
        /// </summary>
        [ObservableProperty] private bool _enabled = true;

        /// <summary>
        /// 1080p下拾取文字左边的起始偏移
        /// </summary>
        [ObservableProperty] private int _itemIconLeftOffset = 60;

        /// <summary>
        /// 1080p下拾取文字的起始偏移
        /// </summary>
        [ObservableProperty] private int _itemTextLeftOffset = 115;

        /// <summary>
        /// 1080p下拾取文字的终止偏移
        /// </summary>
        [ObservableProperty] private int _itemTextRightOffset = 400;

        /// <summary>
        /// 文字识别引擎
        /// - Paddle
        /// - Yap
        /// </summary>
        [ObservableProperty]
        private string _ocrEngine = PickOcrEngineEnum.Paddle.ToString();

        /// <summary>
        /// 急速模式
        /// 无视文字识别结果，直接拾取
        /// </summary>

        [ObservableProperty] private bool _fastModeEnabled = false;

        /// <summary>
        /// 自定义按键拾取
        /// </summary>
        [ObservableProperty] private string _pickKey = "F";

        /// <summary>
        /// 可选本体模板匹配；默认保留原公版 OCR 拾取
        /// </summary>
        [ObservableProperty] private bool _mojangMatchEnabled = false;

        /// <summary>
        /// 测试自动拾取：勾选后不执行按F，仅输出本次识别各环节耗时
        /// </summary>
        [ObservableProperty] private bool _testModeEnabled = false;

        /// <summary>
        /// 自动截图稳定次数：连续 N 次识别为未知且最近 N 次截图互相匹配度达标时，
        /// 对识别区域 OCR 并保存截图（按颜色分目录、去重）。0 表示关闭自动截图。
        /// </summary>
        [ObservableProperty] private int _autoScreenshotStreak = 0;

        /// <summary>
        /// 莫版匹配阈值（匹配度低于该值视为未识别到）
        /// </summary>
        [ObservableProperty] private double _mojangMatchThreshold = 0.85;

        /// <summary>
        /// 莫版拾取日志级别：0=精简（仅交互时输出） 1=常规（黑名单/无结果节流输出） 2=调试（完整耗时位置匹配度，不节流）
        /// </summary>
        [ObservableProperty] private int _pickLogLevel = 1;

        /// <summary>
        /// 交互后延迟（毫秒）
        /// </summary>
        [ObservableProperty] private int _interactionDelay = 64;

        /// <summary>
        /// 连点F间隔（毫秒，0 表示不应用连点）
        /// 非0时，当前物品需要交互时向下扫描，从当前行开始连续 n 个都未需要交互则间隔该值连点 n 次 F
        /// </summary>
        [ObservableProperty] private int _repeatFInterval = 0;

        /// <summary>
        /// 二次识别间隔（毫秒，0 表示不启用二次识别）
        /// 非0时按 F 前重新截图确认，两次识别结果一致才交互
        /// </summary>
        [ObservableProperty] private int _confirmInterval = 0;

        /// <summary>
        /// 单次滚动距离（滚轮 delta 单位）
        /// </summary>
        [ObservableProperty] private int _scrollDistance = 360;

        /// <summary>
        /// 滚动间隔（毫秒，0 表示每轮最多滚动一次）
        /// </summary>
        [ObservableProperty] private int _scrollInterval = 0;

        /// <summary>
        /// 滚轮后间隔（滚轮后额外等待毫秒）
        /// </summary>
        [ObservableProperty] private int _scrollAfterInterval = 16;

        /// <summary>
        /// 自动拾取名单模式
        /// </summary>
        [ObservableProperty]
        [property: JsonConverter(typeof(JsonStringEnumConverter<AutoPickMode>))]
        private AutoPickMode _mode = AutoPickMode.Whitelist;

        // 黑名单模式的拾取规则启用状态
        [ObservableProperty]
        private bool _blacklistModePickEnabled = false;

        // 白名单模式的不拾取规则启用状态
        [ObservableProperty]
        private bool _whitelistModeDoNotPickEnabled = true;

        /// <summary>
        /// 兼容旧版白名单开关，读取后迁移到黑名单模式的拾取规则。
        /// </summary>
        [JsonPropertyName("whiteListEnabled")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? LegacyWhiteListEnabled { get; set; }

        public void MigrateLegacyConfig()
        {
            if (LegacyWhiteListEnabled is null)
            {
                return;
            }

            Mode = AutoPickMode.Blacklist;
            BlacklistModePickEnabled = LegacyWhiteListEnabled.Value;
            LegacyWhiteListEnabled = null;
        }
    }
}
