using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Violeta.Controls;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Collections.ObjectModel;
using System.Windows;
using BetterGenshinImpact.View.Windows;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Windows;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Xml.Linq;
using System;
using System.Collections.Generic;
using BetterGenshinImpact.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.AutoLeyLineOutcrop;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Notification;
using BetterGenshinImpact.Service.Notification.Model.Enum;
using BetterGenshinImpact.View.Windows;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using Wpf.Ui.Controls;
using Wpf.Ui.Violeta.Controls;
using System.Windows.Controls;
using ABI.Windows.UI.UIAutomation;
using Wpf.Ui;
using StackPanel = Wpf.Ui.Controls.StackPanel;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script.Project;
using BetterGenshinImpact.Service.Interface;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using WinForms = System.Windows.Forms;
using BetterGenshinImpact.Core.Simulator;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using static Vanara.PInvoke.User32;
using BetterGenshinImpact.GameTask.AutoSkip.Assets;
using BetterGenshinImpact.GameTask.AutoWood.Assets;
using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoWood.Assets;
using BetterGenshinImpact.GameTask.AutoWood.Utils;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Common;
using Rect = OpenCvSharp.Rect;
using System.Collections.Generic;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;
using Grid = System.Windows.Controls.Grid;
using System.Windows.Media;
using Button = Wpf.Ui.Controls.Button;
using System.Windows.Media.Animation;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Input;
using System.Collections.Specialized;
using BetterGenshinImpact.ViewModel.Pages;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;


namespace BetterGenshinImpact.Core.Config;

[Serializable]
public partial class OneDragonFlowConfig : ObservableObject
{
     public static AllConfig Config { get; set; } = TaskContext.Instance().Config;

     /// <summary>
     /// 秘境选择器中"首领讨伐"事件任务的标记值。
     /// 当 DomainName 等于此值时，一条龙"自动秘境"会改为委派"自动首领讨伐"独立任务，
     /// 所有参数沿用独立任务自身配置（AutoBoss* 字段 / AutoStygianOnslaughtConfig）。
     /// </summary>
     public const string BossEventMarker = "首领讨伐";

     /// <summary>
     /// 秘境选择器中"幽境危战"事件任务的标记值。
     /// 当 DomainName 等于此值时，一条龙"自动秘境"会改为委派"自动幽境危战"独立任务。
     /// </summary>
     public const string StygianEventMarker = "幽境危战";
    
    //版本号
    [ObservableProperty]
    private int _version = 1;
    
    // 配置名
    [ObservableProperty]
    private string _name = string.Empty;
    
    // 配置序号
    [ObservableProperty]
    private int _indexId = 1;
    
    //下次执行的配置单
    [ObservableProperty]
    private bool _nextConfiguration= false;
    
    //下次执行的任务单
    [ObservableProperty]
    private int _nextTaskIndex = 0;
    
    // 配置执行周期
    [ObservableProperty] private string _period = "每日";
    
    // 配置执行周期列表
    [ObservableProperty] private Dictionary<string, bool> _periodList = new()
    {
        { "每日", true },
        { "周一", false },
        { "周二", false },
        { "周三", false },
        { "周四", false },
        { "周五", false },
        { "周六", false },
        { "周日", false }
    };
    
    // 根据每周7天是否执行，转换成1到7的一个字符串，用逗号分隔
    private string _eEverySelectedValueListConverter = "1,2,3,4,5,6,7";

    public string EverySelectedValueListConverter
    {
        get
        {
            List<int> days = new List<int>();
        
            foreach (var kvp in PeriodList)
            {
                if (kvp.Value)
                {
                    switch (kvp.Key)
                    {
                        case "每日":
                            // 添加1到7
                            for (int i = 1; i <= 7; i++)
                            {
                                days.Add(i);
                            }
                            _eEverySelectedValueListConverter = string.Join("/", days);
                            return _eEverySelectedValueListConverter;
                        case "周一":
                            days.Add(1);
                            break;
                        case "周二":
                            days.Add(2);
                            break;
                        case "周三":
                            days.Add(3);
                            break;
                        case "周四":
                            days.Add(4);
                            break;
                        case "周五":
                            days.Add(5);
                            break;
                        case "周六":
                            days.Add(6);
                            break;
                        case "周日":
                            days.Add(7);
                            break;
                    }
                }
            }
            
            _eEverySelectedValueListConverter = string.Join("/", days);
            return _eEverySelectedValueListConverter;
        }
    }
    
   // 计划表
   [ObservableProperty] private string _scheduleName = "默认计划表";
   
   //自定义秘境名称
   [ObservableProperty] private List<string> _customDomainList = new();
   
    /// <summary>
    /// 所有任务的开关状态
    /// </summary>
    public Dictionary<int,(bool,string)> TaskEnabledList { get; set; } = new();
    
    // 定义旧版本的TaskEnabledList
    [Serializable]
    public class TaskEnabledListOld
    {
        public Dictionary<string, bool> TaskEnabledList { get; set; } = new();
    }
    
    // 合成树脂的国家
    [ObservableProperty]
    private string _craftingBenchCountry = "枫丹";

    // 冒险者协会的国家
    [ObservableProperty]
    private string  _adventurersGuildCountry = "枫丹";

    // 自动战斗配置的队伍名称
    [ObservableProperty]
    private string _partyName = string.Empty;

    // 自动战斗配置的策略名称
    [ObservableProperty]
    private string _domainName = string.Empty;

    [ObservableProperty]
    private bool _weeklyDomainEnabled = false;

    #region 自动首领讨伐配置

    [ObservableProperty]
    private string _autoBossName = string.Empty;

    [ObservableProperty]
    private string _autoBossStrategyName = "根据队伍自动选择";

    [ObservableProperty]
    private string _autoBossTeamName = string.Empty;

    [ObservableProperty]
    private bool _autoBossSpecifyRunCount = false;

    [ObservableProperty]
    private int _autoBossRunCount = 1;

    [ObservableProperty]
    private bool _autoBossUseTransientResin = false;

    [ObservableProperty]
    private bool _autoBossUseFragileResin = false;

    [ObservableProperty]
    private int _autoBossReviveRetryCount = 3;

    [ObservableProperty]
    private bool _autoBossReturnToStatueAfterEachRound = false;

    [ObservableProperty]
    private bool _autoBossRewardRecognitionEnabled = false;
    
    [ObservableProperty]
    private int _autoBossTimeout = 240;
    partial void OnAutoBossSpecifyRunCountChanged(bool value)
    {
        if (value)
        {
            return;
        }

        AutoBossUseTransientResin = false;
        AutoBossUseFragileResin = false;
    }

    partial void OnAutoBossRunCountChanged(int value)
    {
        if (value < 1)
        {
            AutoBossRunCount = 1;
        }
    }

    partial void OnAutoBossReviveRetryCountChanged(int value)
    {
        if (value < 0)
        {
            AutoBossReviveRetryCount = 0;
        }
    }

    #endregion
    
    // 领取每日奖励的好感队伍名称
    [ObservableProperty]
    private string _dailyRewardPartyName = string.Empty;
    
    // 合成浓缩后保留原粹树脂的数量
    [ObservableProperty]
    private int _minResinToKeep = 0;
    
    // 领取每日奖励的好感数量
    [ObservableProperty]
    private string _sundayEverySelectedValue = "0";
    
    // 尘歌壶传送方式，1. 地图传送 2. 尘歌壶道具
    [ObservableProperty]
    private string _sereniteaPotTpType = "地图传送";
    
    // 尘歌壶洞天购买商品
    [ObservableProperty]
    private List<string>? _secretTreasureObjects  = new();
    
    //四种树脂类型的对应数量
    [ObservableProperty] private Dictionary<string, int> _resinCount = new()
    {
        { "浓缩树脂", 0 },
        { "原粹树脂", 0 },
        { "须臾树脂", 0 },
        { "脆弱树脂", 0 }
    };
    
    //是否按树脂类型使用
    [ObservableProperty] private bool _specifyResinUse = false;
    
    private string _genshinUid = string.Empty;
    
    public string GenshinUid
    {
        get => _genshinUid;
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                _accountBinding = false;
                _genshinUid = string.Empty;
                OnPropertyChanged(nameof(AccountBinding));
                OnPropertyChanged(nameof(GenshinUid));
                return; 
            }
            if (value.Length == 9 && int.TryParse(value, out _))
            {
                _genshinUid = value;
                OnPropertyChanged(nameof(GenshinUid));
            }
            else
            {
                Toast.Warning("无效UID，长度 "+value.Length+" 位，请输入 9 位纯数字的UID");
            }
            
        }
    }
    
    //绑定账号的后两位字符
    [ObservableProperty] private string _accountBindingCode = string.Empty;
    
    // public int IndexID { get; set; }
    
    private bool _accountBinding  = false;
    
    public bool AccountBinding
    {
        get => _accountBinding;
        set
        {
            if (value != _accountBinding) // 检查新值是否与当前值不同
            {
                if (value == false)
                {
                    _accountBinding = false;
                    OnPropertyChanged(nameof(AccountBinding));
                }
                else
                {
                    if (string.IsNullOrEmpty(GenshinUid))
                    {
                        Toast.Warning("请输入UID");
                        _accountBinding = false;
                        OnPropertyChanged(nameof(AccountBinding));
                    }
                    else
                    {
                        _accountBinding = true;
                        OnPropertyChanged(nameof(AccountBinding));
                    }
                }
            }
        }
    }
    
    // 地脉花一条龙模式（跳过部分准备流程）
    [ObservableProperty]
    private bool _leyLineOneDragonMode = false;

    // 地脉花运行日期设置
    [ObservableProperty]
    private bool _leyLineRunMonday = true;

    [ObservableProperty]
    private bool _leyLineRunTuesday = true;

    [ObservableProperty]
    private bool _leyLineRunWednesday = true;

    [ObservableProperty]
    private bool _leyLineRunThursday = true;

    [ObservableProperty]
    private bool _leyLineRunFriday = true;

    [ObservableProperty]
    private bool _leyLineRunSaturday = true;

    [ObservableProperty]
    private bool _leyLineRunSunday = true;

    // 地脉花每日类型与国家配置（为空则使用独立任务配置）
    [ObservableProperty]
    private string _leyLineMondayType = string.Empty;

    [ObservableProperty]
    private string _leyLineMondayCountry = string.Empty;

    [ObservableProperty]
    private string _leyLineTuesdayType = string.Empty;

    [ObservableProperty]
    private string _leyLineTuesdayCountry = string.Empty;

    [ObservableProperty]
    private string _leyLineWednesdayType = string.Empty;

    [ObservableProperty]
    private string _leyLineWednesdayCountry = string.Empty;

    [ObservableProperty]
    private string _leyLineThursdayType = string.Empty;

    [ObservableProperty]
    private string _leyLineThursdayCountry = string.Empty;

    [ObservableProperty]
    private string _leyLineFridayType = string.Empty;

    [ObservableProperty]
    private string _leyLineFridayCountry = string.Empty;

    [ObservableProperty]
    private string _leyLineSaturdayType = string.Empty;

    [ObservableProperty]
    private string _leyLineSaturdayCountry = string.Empty;

    [ObservableProperty]
    private string _leyLineSundayType = string.Empty;

    [ObservableProperty]
    private string _leyLineSundayCountry = string.Empty;

    // 地脉花刷取次数（0 表示使用独立任务配置）
    [ObservableProperty]
    private int _leyLineRunCount = 0;

    // 地脉花树脂耗尽模式
    [ObservableProperty]
    private bool _leyLineResinExhaustionMode = false;

    // 地脉花刷取次数取小值（仅耗尽模式生效）
    [ObservableProperty]
    private bool _leyLineOpenModeCountMin = false;

    // 折叠状态
    [ObservableProperty]
    private bool _isCraftingResinExpanded = true;

    [ObservableProperty]
    private bool _isAutoDomainExpanded = true;

    [ObservableProperty]
    private bool _isAutoLeyLineExpanded = true;

    [ObservableProperty]
    private bool _isClaimRewardsExpanded = true;

    [ObservableProperty]
    private bool _isSereniteaPotExpanded = true;

    [ObservableProperty]
    private bool _isCompletionActionExpanded = true;

    [ObservableProperty]
    private bool _isBindUidExpanded = true;

    #region 每周秘境配置

    //周一
    [ObservableProperty]
    private string _mondayPartyName = string.Empty;
    
    [ObservableProperty]
    private string _mondayDomainName = string.Empty;

    // 周一领奖奖励序号，留空则不指定（取默认奖励）
    [ObservableProperty]
    private string _mondaySelectedValue = string.Empty;
    
    
    //周二
    [ObservableProperty]
    private string _tuesdayPartyName = string.Empty;
    
    [ObservableProperty]
    private string _tuesdayDomainName = string.Empty;

    // 周二领奖奖励序号，留空则不指定（取默认奖励）
    [ObservableProperty]
    private string _tuesdaySelectedValue = string.Empty;
    
    //周三
    [ObservableProperty]
    private string _wednesdayPartyName = string.Empty;
    
    [ObservableProperty]
    private string _wednesdayDomainName = string.Empty;

    // 周三领奖奖励序号，留空则不指定（取默认奖励）
    [ObservableProperty]
    private string _wednesdaySelectedValue = string.Empty;
    
    //周四
    [ObservableProperty]
    private string _thursdayPartyName = string.Empty;
    
    [ObservableProperty]
    private string _thursdayDomainName = string.Empty;

    // 周四领奖奖励序号，留空则不指定（取默认奖励）
    [ObservableProperty]
    private string _thursdaySelectedValue = string.Empty;
    
    //周五
    [ObservableProperty]
    private string _fridayPartyName = string.Empty;
    
    [ObservableProperty]
    private string _fridayDomainName = string.Empty;

    // 周五领奖奖励序号，留空则不指定（取默认奖励）
    [ObservableProperty]
    private string _fridaySelectedValue = string.Empty;
    
    //周六
    [ObservableProperty]
    private string _saturdayPartyName = string.Empty;
    
    [ObservableProperty]
    private string _saturdayDomainName = string.Empty;

    // 周六领奖奖励序号，留空则不指定（取默认奖励）
    [ObservableProperty]
    private string _saturdaySelectedValue = string.Empty;
    
    //周日
    [ObservableProperty]
    private string _sundayPartyName = string.Empty;

    [ObservableProperty]
    private string _sundayDomainName = string.Empty;

    // 周日领奖奖励序号，留空则不指定（取默认奖励）
    [ObservableProperty]
    private string _sundayDaySelectedValue = string.Empty;

    // 单次执行配置单完成后操作
    [ObservableProperty]
    private string _completionAction = string.Empty;
    
    // 通过当天（4点起始）是哪一天来返回配置
    public (string partyName, string domainName, string sundaySelectedValue,Dictionary<string, int> ResinCount,bool SpecifyResinUse) GetDomainConfig()
    {
        if (WeeklyDomainEnabled)
        {
            var dayOfWeek = (DateTime.Now.Hour >= 4 ? DateTime.Today : DateTime.Today.AddDays(-1)).DayOfWeek;
            return dayOfWeek switch
            {
                DayOfWeek.Monday => (MondayPartyName, MondayDomainName, MondaySelectedValue, ResinCount, SpecifyResinUse),
                DayOfWeek.Tuesday => (TuesdayPartyName, TuesdayDomainName, TuesdaySelectedValue, ResinCount, SpecifyResinUse),
                DayOfWeek.Wednesday => (WednesdayPartyName, WednesdayDomainName, WednesdaySelectedValue, ResinCount, SpecifyResinUse),
                DayOfWeek.Thursday => (ThursdayPartyName, ThursdayDomainName, ThursdaySelectedValue, ResinCount, SpecifyResinUse),
                DayOfWeek.Friday => (FridayPartyName, FridayDomainName, FridaySelectedValue, ResinCount, SpecifyResinUse),
                DayOfWeek.Saturday => (SaturdayPartyName, SaturdayDomainName, SaturdaySelectedValue, ResinCount, SpecifyResinUse),
                DayOfWeek.Sunday => (SundayPartyName, SundayDomainName, SundayDaySelectedValue, ResinCount, SpecifyResinUse),
                _ => (PartyName, DomainName, string.Empty, ResinCount, SpecifyResinUse)
            };
        }
        else
        {
            return (PartyName, DomainName,SundayEverySelectedValue,ResinCount,SpecifyResinUse);
        }
    }

    public bool ShouldRunLeyLineToday()
    {
        if (!LeyLineRunMonday
            && !LeyLineRunTuesday
            && !LeyLineRunWednesday
            && !LeyLineRunThursday
            && !LeyLineRunFriday
            && !LeyLineRunSaturday
            && !LeyLineRunSunday)
        {
            return true;
        }

        var serverTime = ServerTimeHelper.GetServerTimeNow();
        var dayOfWeek = (serverTime.Hour >= 4 ? serverTime : serverTime.AddDays(-1)).DayOfWeek;
        return dayOfWeek switch
        {
            DayOfWeek.Monday => LeyLineRunMonday,
            DayOfWeek.Tuesday => LeyLineRunTuesday,
            DayOfWeek.Wednesday => LeyLineRunWednesday,
            DayOfWeek.Thursday => LeyLineRunThursday,
            DayOfWeek.Friday => LeyLineRunFriday,
            DayOfWeek.Saturday => LeyLineRunSaturday,
            DayOfWeek.Sunday => LeyLineRunSunday,
            _ => true
        };
    }

    public (string type, string country) GetLeyLineConfigForToday(AutoLeyLineOutcropConfig fallback)
    {
        var serverTime = ServerTimeHelper.GetServerTimeNow();
        var dayOfWeek = (serverTime.Hour >= 4 ? serverTime : serverTime.AddDays(-1)).DayOfWeek;
        var (type, country) = dayOfWeek switch
        {
            DayOfWeek.Monday => (LeyLineMondayType, LeyLineMondayCountry),
            DayOfWeek.Tuesday => (LeyLineTuesdayType, LeyLineTuesdayCountry),
            DayOfWeek.Wednesday => (LeyLineWednesdayType, LeyLineWednesdayCountry),
            DayOfWeek.Thursday => (LeyLineThursdayType, LeyLineThursdayCountry),
            DayOfWeek.Friday => (LeyLineFridayType, LeyLineFridayCountry),
            DayOfWeek.Saturday => (LeyLineSaturdayType, LeyLineSaturdayCountry),
            DayOfWeek.Sunday => (LeyLineSundayType, LeyLineSundayCountry),
            _ => (string.Empty, string.Empty)
        };

        if (string.IsNullOrWhiteSpace(type))
        {
            type = fallback.LeyLineOutcropType;
        }

        if (string.IsNullOrWhiteSpace(country))
        {
            country = fallback.Country;
        }

        return (type, country);
    }

    #endregion
}