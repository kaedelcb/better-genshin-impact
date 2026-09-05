using System.Windows;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>
/// 锄地数据 Tab 的 ViewModel（F5 精简版 + §5-A 卡死心跳）。
/// 成员实时状态墙（直接绑定主 VM 的 Members 集合，10s 状态流零成本）
/// + 每日运行日报（<see cref="DailyReport"/>，解析 BGI 按天日志，由 DodocoViewModel 注入）。
/// 卡死心跳：本地任务运行中（复用主 VM 最近状态快照，不新起 IPC）但日志超 N 分钟无新行 →
/// SuspectedStall=true（红色横幅）+ 走嘟嘟可告警通道（红点+提示音+托盘），恢复有日志自动解除。
/// </summary>
public sealed class HoeingStatsViewModel : ViewModelBase, IDisposable
{
    /// <summary>卡死心跳检测间隔。</summary>
    private static readonly TimeSpan DisplayRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly MainViewModel _mainVm;
    private readonly BgiLogTailService _tail;
    private readonly DodocoSettingsService _settings;
    /// <summary>告警通道（嘟嘟可导航红点 + 提示音 + 托盘气泡，由 DodocoViewModel 提供，内部尊重全部静音）。
    /// 仅作卡死注入通道缺失时的回退。</summary>
    private readonly Action<string, string> _raiseAlert;
    /// <summary>卡死注入通道（可选）：非空时卡死改走异常库（RecordExternal → 异常记录列表 + 告警 + 事发快照），
    /// 由 DodocoViewModel 注入 RecordStallIncident。</summary>
    private readonly Action<string, string>? _raiseIncident;
    private readonly DispatcherTimer _refreshTimer;
    /// <summary>本次卡死 episode 是否已告警（一次卡死只告警一次，恢复后重置）。</summary>
    private bool _stallAlerted;

    public HoeingStatsViewModel(HoeingStatsService stats, MainViewModel mainVm,
        BgiLogTailService tail, Action<string, string> raiseAlert, DodocoSettingsService settings,
        Action<string, string>? raiseIncident = null)
    {
        _mainVm = mainVm;
        _tail = tail;
        _raiseAlert = raiseAlert;
        _raiseIncident = raiseIncident;
        _settings = settings;

        _refreshTimer = new DispatcherTimer { Interval = DisplayRefreshInterval };
        _refreshTimer.Tick += (_, _) => CheckStall();
        _refreshTimer.Start();
    }

    // ========== 成员实时状态墙（直接复用主 VM 成员集合） ==========

    /// <summary>成员列表（含 TaskRunning/CurrentTaskGroupName/CurrentRouteDisplay/AutoHoeingProgress）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<MemberViewModel> Members => _mainVm.Members;

    // ========== 每日运行日报 ==========

    /// <summary>每日运行日报（按天解析 BGI 日志；由 DodocoViewModel 在构造后注入）。</summary>
    public DailyReportViewModel DailyReport { get; set; } = null!;

    // ========== 卡死心跳检测（§5-A） ==========

    private bool _suspectedStall;
    /// <summary>疑似卡死：任务运行中但日志超时无新行。</summary>
    public bool SuspectedStall
    {
        get => _suspectedStall;
        set { SetProperty(ref _suspectedStall, value); OnPropertyChanged(nameof(StallBannerText)); }
    }

    /// <summary>卡死判定阈值（分钟）：日志超过该时长无新行且任务运行中 → 疑似卡死。
    /// 默认 3 分钟，持久化到 dodoco_settings.json（P4 统一设置收口）。</summary>
    public int StallThresholdMinutes
    {
        get => _settings.Current.StallThresholdMinutes;
        set
        {
            if (value < 1) return;
            _settings.Update(s => s.StallThresholdMinutes = value);
            OnPropertyChanged();
        }
    }

    /// <summary>红色横幅文本。</summary>
    public string StallBannerText =>
        SuspectedStall
            ? $"⚠ 疑似卡死：任务运行中，但 BGI 日志已超过 {StallThresholdMinutes} 分钟没有新行（最后日志 {LastLogAgoText}）"
            : "";

    /// <summary>最后一条日志距今的显示文本。</summary>
    public string LastLogAgoText
    {
        get
        {
            var last = _tail.LastEntryTime;
            return last == DateTime.MinValue ? "从未收到" : $"{(int)(DateTime.Now - last).TotalMinutes} 分钟前";
        }
    }

    /// <summary>心跳检测：任务运行中（主 VM 最近状态快照）且日志超时无新行 → 疑似卡死。</summary>
    private void CheckStall()
    {
        OnPropertyChanged(nameof(LastLogAgoText));
        var status = _mainVm.LatestLocalStatus;
        var taskRunning = status is { TaskRunning: true } or { AutoHoeingRunning: true };
        var last = _tail.LastEntryTime;
        var silent = last != DateTime.MinValue && DateTime.Now - last > TimeSpan.FromMinutes(StallThresholdMinutes);

        if (taskRunning && silent)
        {
            if (!SuspectedStall) SuspectedStall = true;
            if (!_stallAlerted)
            {
                _stallAlerted = true;
                var detail = $"任务运行中但日志超过 {StallThresholdMinutes} 分钟无新行，BGI 疑似卡死";
                // 优先走异常库注入（落盘 + 异常记录列表 + 告警 + 事发快照）；无注入通道时回退直告
                if (_raiseIncident != null)
                    _raiseIncident("嘟嘟可卡死心跳", detail);
                else
                    _raiseAlert("嘟嘟可卡死心跳", detail);
            }
        }
        else if (SuspectedStall && (!taskRunning || (last != DateTime.MinValue && DateTime.Now - last <= TimeSpan.FromMinutes(StallThresholdMinutes))))
        {
            // 恢复：有新日志或任务停止 → 解除卡死状态
            SuspectedStall = false;
            _stallAlerted = false;
        }
        else if (!taskRunning)
        {
            _stallAlerted = false;
        }
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
    }
}
