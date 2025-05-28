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
    
    // 配置名
    [ObservableProperty]
    private string _name = string.Empty;
    
    // 配置序号
    [ObservableProperty]
    private int _indexId = 1;
    
    // 配置执行周期
    [ObservableProperty] private string _period = "每日";
    
    private readonly ObservableCollection<string> _selectedPeriodList = new()
    {
        "每日",
        "周一",
        "周二",
        "周三",
        "周四",
        "周五",
        "周六",
        "周日"
    };
    public ReadOnlyObservableCollection<string> SelectedPeriodList => new(_selectedPeriodList);
    
   // 计划表
   [ObservableProperty] private string _scheduleName = "默认计划表";
   
    /// <summary>
    /// 所有任务的开关状态
    /// </summary>
    public Dictionary<int,(bool,string)> TaskEnabledList { get; set; } = new();
    
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
    
    // 领取每日奖励的好感队伍名称
    [ObservableProperty]
    private string _dailyRewardPartyName = string.Empty;
    
    // 合成浓缩后保留原粹树脂的数量
    [ObservableProperty]
    private int _minResinToKeep = 0;
    
    // 领取每日奖励的好感数量
    [ObservableProperty]
    private string _sundayEverySelectedValue = "0";
    
    // 领取每日奖励的好感数量
    [ObservableProperty]
    private string _sundaySelectedValue = "0";
    
    // 尘歌壶洞天购买商品
    [ObservableProperty]
    private List<string> _secretTreasureObjects = new();
    
    
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
    
    // public int IndexID { get; set; }
    
    private bool? _accountBinding  = false;
    public bool? AccountBinding
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
    
    #region 每周秘境配置

    //周一
    [ObservableProperty]
    private string _mondayPartyName = string.Empty;
    
    [ObservableProperty]
    private string _mondayDomainName = string.Empty;
    
    
    //周二
    [ObservableProperty]
    private string _tuesdayPartyName = string.Empty;
    
    [ObservableProperty]
    private string _tuesdayDomainName = string.Empty;
    
    //周三
    [ObservableProperty]
    private string _wednesdayPartyName = string.Empty;
    
    [ObservableProperty]
    private string _wednesdayDomainName = string.Empty;
    
    //周四
    [ObservableProperty]
    private string _thursdayPartyName = string.Empty;
    
    [ObservableProperty]
    private string _thursdayDomainName = string.Empty;
    
    //周五
    [ObservableProperty]
    private string _fridayPartyName = string.Empty;
    
    [ObservableProperty]
    private string _fridayDomainName = string.Empty;
    
    //周六
    [ObservableProperty]
    private string _saturdayPartyName = string.Empty;
    
    [ObservableProperty]
    private string _saturdayDomainName = string.Empty;
    
    //周日
    [ObservableProperty]
    private string _sundayPartyName = string.Empty;

    [ObservableProperty]
    private string _sundayDomainName = string.Empty;

    // 单次执行配置单完成后操作
    [ObservableProperty]
    private string _completionAction = string.Empty;
    
    // 通过当天是哪一天来返回配置
    public (string partyName, string domainName, string sundaySelectedValue) GetDomainConfig()
    {
        if (WeeklyDomainEnabled)
        {
            var dayOfWeek = DateTime.Now.DayOfWeek;
            return dayOfWeek switch
            {
                DayOfWeek.Monday => (MondayPartyName, MondayDomainName,SundaySelectedValue),
                DayOfWeek.Tuesday => (TuesdayPartyName, TuesdayDomainName,SundaySelectedValue),
                DayOfWeek.Wednesday => (WednesdayPartyName, WednesdayDomainName,SundaySelectedValue),
                DayOfWeek.Thursday => (ThursdayPartyName, ThursdayDomainName,SundaySelectedValue),
                DayOfWeek.Friday => (FridayPartyName, FridayDomainName,SundaySelectedValue),
                DayOfWeek.Saturday => (SaturdayPartyName, SaturdayDomainName,SundaySelectedValue),
                DayOfWeek.Sunday => (SundayPartyName, SundayDomainName,SundaySelectedValue),
                _ => (PartyName, DomainName,SundaySelectedValue)
            };
        }
        else
        {
            return (PartyName, DomainName,SundayEverySelectedValue);
        }
    }

    #endregion
}