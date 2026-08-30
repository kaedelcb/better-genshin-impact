using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoPick.Assets;
using BetterGenshinImpact.GameTask.Common.Map;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Service.Notification;
using BetterGenshinImpact.View.Drawable;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.Service.Notification.Model.Enum;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using static Vanara.PInvoke.Kernel32;
using static Vanara.PInvoke.User32;
using Microsoft.Extensions.Localization;
using System.Globalization;
using System.Text.RegularExpressions;
using BetterGenshinImpact.GameTask.AutoArtifactSalvage;
using System.Collections.ObjectModel;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.GameTask.AutoDomain.Model;
using BetterGenshinImpact.GameTask.Common;
using Compunet.YoloSharp;
using Microsoft.Extensions.DependencyInjection;
using BetterGenshinImpact.Core.Config;
using OpenCvSharp.Extensions;
using BetterGenshinImpact.GameTask.AutoFight;
using OfficialAutoFightRouter = BetterGenshinImpact.GameTask.AutoFightOfficial.OfficialAutoFightRouter;
using OfficialParamAdapter = BetterGenshinImpact.GameTask.AutoFightOfficial.OfficialParamAdapter;
using OfficialJsonTask = BetterGenshinImpact.GameTask.AutoFightOfficial.AutoFightJsonTask;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoPick.Assets;
using BetterGenshinImpact.GameTask.Common.Map;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Service.Notification;
using BetterGenshinImpact.View.Drawable;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.Service.Notification.Model.Enum;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using static Vanara.PInvoke.Kernel32;
using static Vanara.PInvoke.User32;
using Microsoft.Extensions.Localization;
using System.Globalization;
using System.Text.RegularExpressions;
using BetterGenshinImpact.GameTask.AutoArtifactSalvage;
using System.Collections.ObjectModel;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.GameTask.AutoDomain.Model;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Reward;
using Compunet.YoloSharp;
using Microsoft.Extensions.DependencyInjection;
using BetterGenshinImpact.GameTask.AutoFight;

namespace BetterGenshinImpact.GameTask.AutoDomain;

public class AutoDomainTask : ISoloTask<Dictionary<string, int>>
{
    public string Name => "自动秘境";
    
    private AllConfig AllConfig { get; set; } = TaskContext.Instance().Config;

    private readonly AutoDomainParam _taskParam;

    private readonly BgiYoloPredictor _predictor;

    private readonly AutoDomainConfig _config;

    private readonly CombatScriptBag? _combatScriptBag;
    // JSON 战斗策略路径（非空表示走 AutoFightJsonTask，TXT 脚本包不解析）
    private readonly string? _jsonCombatStrategyPath;
    private readonly Dictionary<string, int> _rewardSummary = new();

    private CancellationToken _ct;

    private readonly Rect _captureRect = TaskContext.Instance().SystemInfo.ScaleMax1080PCaptureRect;

    private ObservableCollection<OneDragonFlowConfig> ConfigList = [];
    
    private readonly ReturnMainUiTask _returnMainUiTask = new();
    
    private readonly string challengeCompletedLocalizedString;
    private readonly string autoLeavingLocalizedString;
    private readonly string skipLocalizedString;
    private readonly string leyLineDisorderLocalizedString;
    private readonly string clickanywheretocloseLocalizedString;
    private readonly string matchingChallengeString;
    private readonly string rapidformationString;
    private readonly string ancientTreeString;
    private readonly string skipAnimationString;
    private readonly string replenishString;
    private readonly string limitedFullyString;
    private readonly string limitedFullyAllString;

    private int condensedResinUsedCount = 0;
    private int originalResinUsedCount = 0;
    private int fragileResinUsedCount = 0;
    private int momentResinUsedCount = 0;
    
    private List<ResinUseRecord> _resinPriorityListWhenSpecifyUse;

    public AutoDomainTask(AutoDomainParam taskParam)
    {
        _taskParam = taskParam;
        _predictor = App.ServiceProvider.GetRequiredService<BgiOnnxFactory>().CreateYoloPredictor(BgiOnnxModel.BgiTree);

        _config = TaskContext.Instance().Config.AutoDomainConfig;

        // JSON 策略：跳过 TXT 脚本包解析，改由 AutoFightJsonTask 处理
        if (_taskParam.CombatStrategyPath != null
            && _taskParam.CombatStrategyPath.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase))
        {
            _jsonCombatStrategyPath = _taskParam.CombatStrategyPath;
            _combatScriptBag = null;
        }
        else
        {
            _combatScriptBag = CombatScriptParser.ReadAndParse(_taskParam.CombatStrategyPath);
        }

        _resinPriorityListWhenSpecifyUse = ResinUseRecord.BuildFromDomainParam(taskParam);

        IStringLocalizer<AutoDomainTask> stringLocalizer =
            App.GetService<IStringLocalizer<AutoDomainTask>>() ?? throw new NullReferenceException();
        CultureInfo cultureInfo = new CultureInfo(TaskContext.Instance().Config.OtherConfig.GameCultureInfoName);
        this.challengeCompletedLocalizedString = stringLocalizer.WithCultureGet(cultureInfo, "挑战达成");
        this.autoLeavingLocalizedString = stringLocalizer.WithCultureGet(cultureInfo, "自动退出");
        this.skipLocalizedString = stringLocalizer.WithCultureGet(cultureInfo, "跳过");
        this.leyLineDisorderLocalizedString = stringLocalizer.WithCultureGet(cultureInfo, "地脉异常");
        this.clickanywheretocloseLocalizedString = stringLocalizer.WithCultureGet(cultureInfo, "点击任意位置关闭");
        this.matchingChallengeString = stringLocalizer.WithCultureGet(cultureInfo, "匹配挑战");
        this.rapidformationString = stringLocalizer.WithCultureGet(cultureInfo, "快速编队");
        this.ancientTreeString = stringLocalizer.WithCultureGet(cultureInfo, "石化古树");
        this.skipAnimationString = stringLocalizer.WithCultureGet(cultureInfo, "自动跳过领奖动画");
        this.replenishString = stringLocalizer.WithCultureGet(cultureInfo, "补充");
        this.limitedFullyString = stringLocalizer.WithCultureGet(cultureInfo, "限时全部开放");
        this.limitedFullyAllString = stringLocalizer.WithCultureGet(cultureInfo, "限时开放");
    }
    
    private static RecognitionObject GetConfirmRa(params string[] targetText)
    {
        using var screenArea = CaptureToRectArea();
        return RecognitionObject.OcrMatch(
            (int)(screenArea.Width * 0.5),
            (int)(screenArea.Height * 0.5),
            (int)(screenArea.Width * 0.5),
            (int)(screenArea.Height * 0.5),
            targetText
        );
    }

    Task ISoloTask.Start(CancellationToken ct) => Start(ct);

    public async Task<Dictionary<string, int>> Start(CancellationToken ct)
    {
        _ct = ct;
        _rewardSummary.Clear();

        Init();
        Notify.Event(NotificationEvent.DomainStart).Success("自动秘境启动");

        // 复活重试
        for (var i = 0; i < _config.ReviveRetryCount; i++)
        {
            try
            {
                await DoDomain();
                // 其他场景不重试
                break;
            }
            catch (RetryException e)
            {
                // 只有选择了秘境的时候才会重试
                if (!string.IsNullOrEmpty(_taskParam.DomainName))
                {
                    var msg = e.Message;
                    if (msg.Contains("复活"))
                    {
                        msg = "存在角色死亡，复活后重试秘境...";
                    }

                    Logger.LogWarning("自动秘境：{Text}", msg);
                    await Delay(2000, ct);
                    Notify.Event(NotificationEvent.DomainRetry).Error(msg);
                    continue;
                }

                throw;
            }
        }
        await Delay(2000, ct);
        await Bv.WaitForMainUi(_ct, 30);
        await Delay(2000, ct);

        await ArtifactSalvage();
        Notify.Event(NotificationEvent.DomainEnd).Success("自动秘境结束");
        return new Dictionary<string, int>(_rewardSummary);
    }
    
    private async Task DoDomain()
    {
        //显示树脂使用模式
        Logger.LogInformation("树脂使用模式：{ResinMode}", _taskParam.SpecifyResinUse? "按以下配置使用树脂类型和数量" : "先用浓缩，再用原粹，其他不使用");
        if (AllConfig.AutoDomainEnable)
        {
            if (_taskParam.SpecifyResinUse)
            {
                _taskParam.ResinCount = new Dictionary<string, int>() {
                    { "浓缩树脂", _taskParam.CondensedResinUseCount },
                    { "原粹树脂", _taskParam.OriginalResinUseCount },
                    { "脆弱树脂", _taskParam.FragileResinUseCount },
                    { "须臾树脂", _taskParam.TransientResinUseCount }
                };
            }
            else
            {
                _taskParam.ResinCount = new Dictionary<string, int>() {
                    { "浓缩树脂", _taskParam.CondensedResinUseCount },
                    { "原粹树脂", _taskParam.OriginalResinUseCount }
                };
            }
        }
            
        if (_taskParam.SpecifyResinUse)
        {
            Logger.LogInformation("树脂类型和次数：{ResinCount}", _taskParam.ResinCount);
        }

        // while (true)
        // {
        //     GetRemainResinStatus();
        //     await Delay(500, _ct);
        // }
        // 传送到秘境
        await TpDomain();
        
        // 切换队伍
        // await SwitchParty(_taskParam.PartyName);

        // 前置进入秘境
        await EnterDomain();
        
        var combatScenes = new CombatScenes();
        for (var i = 0; i < _taskParam.DomainRoundNum; i++)
        {
            // 0. 关闭秘境提示
            Logger.LogDebug("0. 关闭秘境提示");
            await CloseDomainTip();
            
            //0.5. 初始化队伍，只执行一次
            if (i == 0)
            {
                combatScenes = new CombatScenes().InitializeTeam(CaptureToRectArea());   
            }

            RetryTeamInit(combatScenes); // 队伍没初始化成功则重试

            // 0. 切换到第一个角色（JSON 策略由 AutoFightJsonTask 自行识别与切换）
            var combatCommands = _jsonCombatStrategyPath != null
                ? new List<CombatCommand>()
                : FindCombatScriptAndSwitchAvatar(combatScenes);

            // 1. 走到钥匙处启动
            Logger.LogInformation("自动秘境：{Text}", "1. 走到钥匙处启动");
            await WalkToPressF();

            // 2. 执行战斗（战斗线程、视角线程、检测战斗完成线程）
            Logger.LogInformation("自动秘境：{Text}", "2. 执行战斗策略");
            await StartFight(combatScenes, combatCommands);
            combatScenes.AfterTask();
            EndFightWait();

            // 3. 寻找石化古树 并左右移动直到石化古树位于屏幕中心
            Logger.LogInformation("自动秘境：{Text}", "3. 寻找石化古树");
            await FindPetrifiedTree();

            // 4. 走到石化古树处
            Logger.LogInformation("自动秘境：{Text}", "4. 走到石化古树处");
            await WalkToPressF();
            
            // 5. 快速领取奖励并判断是否有下一轮
            Logger.LogInformation("自动秘境：{Text}", "5. 领取奖励");
            if (!GettingTreasure(_taskParam.DomainRoundNum == 9999, i == _taskParam.DomainRoundNum - 1,i))
            {
                if (i == _taskParam.DomainRoundNum - 1)
                {
                    Logger.LogInformation("配置的{Cnt}轮秘境已经完成，结束自动秘境", _taskParam.DomainRoundNum);
                }
                else
                {
                    Logger.LogInformation("体力已经耗尽，结束自动秘境");
                }

                break;
            }

            Notify.Event(NotificationEvent.DomainReward).Success("自动秘境奖励领取");
        }
    }

    private void Init()
    {
        LogScreenResolution();
        if (_config.AutoEat)
        {
            TaskTriggerDispatcher.Instance().AddTrigger("AutoEat", null);
        }
        if (_taskParam.DomainRoundNum == 9999)
        {
            Logger.LogInformation("→ {Text} 用尽所有体力后结束", "自动秘境，");
        }
        else
        {
            Logger.LogInformation("→ {Text} 设置总次数：{Cnt}", "自动秘境，", _taskParam.DomainRoundNum);
        }
    }

    private void LogScreenResolution()
    {
        var gameScreenSize = SystemControl.GetGameScreenRect(TaskContext.Instance().GameHandle);
        if (gameScreenSize.Width * 9 != gameScreenSize.Height * 16)
        {
            Logger.LogError("游戏窗口分辨率不是 16:9 ！当前分辨率为 {Width}x{Height} , 非 16:9 分辨率的游戏无法正常使用自动秘境功能 !",
                gameScreenSize.Width, gameScreenSize.Height);
            throw new Exception("游戏窗口分辨率不是 16:9");
        }

        if (gameScreenSize.Width < 1920 || gameScreenSize.Height < 1080)
        {
            Logger.LogWarning("游戏窗口分辨率小于 1920x1080 ！当前分辨率为 {Width}x{Height} , 小于 1920x1080 的分辨率的游戏可能无法正常使用自动秘境功能 !",
                gameScreenSize.Width, gameScreenSize.Height);
        }
    }

    private void RetryTeamInit(CombatScenes combatScenes)
    {
        if (!combatScenes.CheckTeamInitialized())
        {
            combatScenes.InitializeTeam(CaptureToRectArea());
            if (!combatScenes.CheckTeamInitialized())
            {
                throw new Exception("识别队伍角色失败，请在较暗背景下重试，比如游戏时间调整成夜晚。或者直接使用强制指定当前队伍角色的功能。");
            }
        }
    }

    private async Task TpDomain()
    {
        // 传送到秘境
        if (!string.IsNullOrEmpty(_taskParam.DomainName))
        {
            if (MapLazyAssets.Get().DomainPositionMap.TryGetValue(_taskParam.DomainName, out var domainPosition))
            {
                Logger.LogInformation("自动秘境：传送到秘境{Text}", _taskParam.DomainName);
                await new TpTask(_ct).Tp(domainPosition.X, domainPosition.Y);
                await Delay(1000, _ct);
                await Bv.WaitForMainUi(_ct);

                var menuFound = false;
                AutoPickAssets pickAssets;
                using (var gameCaptureRegion = CaptureToRectArea())
                {
                    pickAssets = AutoPickAssets.Get(gameCaptureRegion, TaskContext.Instance().Config.AutoPickConfig.PickKey);
                }
                if ("芬德尼尔之顶".Equals(_taskParam.DomainName))
                {
                    menuFound = await NewRetry.WaitForElementAppear(
                        pickAssets.PickRo,
                        () => Simulation.SendInput.SimulateAction(GIActions.MoveBackward, KeyType.KeyDown),
                        _ct,
                        20,
                        500
                    );
                    Simulation.SendInput.SimulateAction(GIActions.MoveBackward, KeyType.KeyUp);
                }
                else if ("无妄引咎密宫".Equals(_taskParam.DomainName))
                {
                    
                    Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                    Thread.Sleep(500);
                    Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);

                    menuFound = await NewRetry.WaitForElementAppear(
                        pickAssets.PickRo,
                        () => Simulation.SendInput.SimulateAction(GIActions.MoveLeft, KeyType.KeyDown),
                        _ct,
                        20,
                        500
                    );
                    Simulation.SendInput.SimulateAction(GIActions.MoveLeft, KeyType.KeyUp);
                    
                }
                else if ("太山府".Equals(_taskParam.DomainName))
                {
                    menuFound = await NewRetry.WaitForElementAppear(
                        pickAssets.PickRo,
                        () => { },
                        _ct,
                        20,
                        500
                    );
                }
                else
                {
                    menuFound = await NewRetry.WaitForElementAppear(
                        pickAssets.PickRo,
                        () => Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown),
                        _ct,
                        20,
                        500
                    );
                    Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                }
                
                if (!menuFound)
                {
                    throw new Exception("请检查是否在秘境门前");
                }  
                
                // var menu = await NewRetry.WaitForElementAppear(
                //     GetConfirmRa("单人挑战"),
                //     null,//只等待,不执行操作
                //     _ct,
                //     20,
                //     500
                // );
                // if (!menu)
                // {
                //     throw new Exception("请检查是否已进入秘境页面");
                // }
                
            }
            else
            {
                Logger.LogError("自动秘境：未找到对应的秘境{Text}的传送点", _taskParam.DomainName);
                throw new Exception($"未找到对应的秘境{_taskParam.DomainName}的传送点");
            }
        }
    }

    /// <summary>
    /// 切换队伍
    /// </summary>
    /// <param name="partyName"></param>
    /// <returns></returns>
    private async Task<bool> SwitchParty(string? partyName)
    {
        if (!string.IsNullOrEmpty(partyName))
        {
            var b = await new SwitchPartyTask().Start(partyName, _ct);
            await Delay(500, _ct);
            return b;
        }

        return true;
    }

    private async Task EnterDomain()
    {
        AutoFightAssets fightAssets;
        AutoPickAssets pickAssets;
        using (var gameCaptureRegion = CaptureToRectArea())
        {
            fightAssets = AutoFightAssets.Get(gameCaptureRegion);
            pickAssets = AutoPickAssets.Get(gameCaptureRegion, TaskContext.Instance().Config.AutoPickConfig.PickKey);
        }

        await NewRetry.WaitForElementDisappear(
            pickAssets.PickRo,
            () => Simulation.SendInput.Keyboard.KeyPress(pickAssets.PickVk),
            _ct,
            20,
            500
        );
        var menuFound = await NewRetry.WaitForElementAppear(
            GetConfirmRa("单人挑战"),
            null,//只等待,不执行操作
            _ct,
            20,
            500
        );
        if (!menuFound)
        {
            Logger.LogWarning("单人挑战 按键未出现，请检查是否已进入秘境页面");
        }

        using var limitedFullyStringRa = CaptureToRectArea();
        var limitedFullyStringRaocrList =
            limitedFullyStringRa.FindMulti(RecognitionObject.Ocr(0, 0, limitedFullyStringRa.Width * 0.5,
                limitedFullyStringRa.Height));
        var limitedFullyStringRaocrListdone = limitedFullyStringRaocrList.LastOrDefault(t =>
            Regex.IsMatch(t.Text, this.limitedFullyString) || Regex.IsMatch(t.Text, this.limitedFullyAllString));
        // 检测是否为限时全开秘境
        if (limitedFullyStringRaocrListdone != null)
        {
            Logger.LogInformation("自动秘境：{Text}", "检测到秘境限时全开");
        }

        var serverTime = ServerTimeHelper.GetServerTimeNow();
        if (serverTime is { DayOfWeek: DayOfWeek.Sunday, Hour: >= 4 } || serverTime is { DayOfWeek: DayOfWeek.Monday, Hour: < 4 } || limitedFullyStringRaocrListdone != null)
        {
            using var ra0 = CaptureToRectArea();
            using var artifactArea = ra0.Find(RecognitionAssets.Get("AutoFight", "ArtifactArea", ra0)); //检测是否为圣遗物副本
            if (artifactArea.IsEmpty())
            {
                if (int.TryParse(_taskParam.SundaySelectedValue, out int sundaySelectedValue))
                {
                    if (sundaySelectedValue > 0)
                    {
                        Logger.LogInformation(limitedFullyStringRaocrListdone != null ? "自动秘境：限时全开秘境奖励序号 {sundaySelectedValue}" : "自动秘境：周日设置了秘境奖励序号 {sundaySelectedValue}", sundaySelectedValue);
                        using var abnormalscreenRa = CaptureToRectArea();
                        GlobalMethod.MoveMouseTo(abnormalscreenRa.Width / 4, abnormalscreenRa.Height / 2); //移到左侧
                        for (var i = 0; i < 100; i++)
                        {
                            Simulation.SendInput.Mouse.VerticalScroll(-1);
                            await Delay(10, _ct);
                        }

                        await Delay(400, _ct);

                        using var abnormalRa = CaptureToRectArea();
                        var ocrList =
                            abnormalRa.FindMulti(RecognitionObject.Ocr(0, 0, abnormalRa.Width * 0.5,
                                abnormalRa.Height));
                        var done = ocrList.LastOrDefault(t =>
                            Regex.IsMatch(t.Text, this.leyLineDisorderLocalizedString));
                        if (done != null)
                        {
                            await Delay(300, _ct);

                            switch (sundaySelectedValue)
                            {
                                case 1:
                                    GlobalMethod.Click(done.X, done.Y - abnormalRa.Height / 5);
                                    break;
                                case 2:
                                    GlobalMethod.Click(done.X, done.Y - abnormalRa.Height / 10);
                                    break;
                                case 3:
                                    GlobalMethod.Click(done.X, done.Y);
                                    break;
                                default:
                                    Logger.LogWarning("无效的 sundaySelectedValue 值: {sundaySelectedValue}",
                                        sundaySelectedValue);
                                    break;
                            }
                        }
                    }
                    else
                    {
                        Logger.LogInformation(limitedFullyStringRaocrListdone != null ? "自动秘境：限时全开秘境未设置特定秘境奖励" : "自动秘境：周日秘境未设置特定秘境奖励");
                    }
                }
                else
                {
                    Logger.LogWarning(_taskParam.SundaySelectedValue == "" ? "未设置秘境奖励序号" : "设置秘境奖励序号错误，请检查配置页面");
                }
            }

            await Delay(300, _ct);
        }
        
        // 点击单人挑战确认并等待队伍界面--使用图像模版匹配的方法，也可以使用文字OCR的方法识别“单人挑战”直到消失
        await NewRetry.WaitForElementAppear(
            ElementRecognition.Get("PartyBtnChooseView"),
            () =>
            {
                using var ra = CaptureToRectArea();
                var ra2 = ra.Find(RecognitionAssets.Get("AutoFight", "Confirm", ra));
                if (!ra2.IsEmpty())
                {
                    ra2.Click();
                    ra2.Dispose();
                    Logger.LogInformation("自动秘境：点击 {Text}", "单人挑战");
                }

                using var confirmRectArea2 = ra.Find(RecognitionObject.Ocr(ra.Width * 0.263, ra.Height * 0.32,
                    ra.Width - ra.Width * 0.263 * 2, ra.Height - ra.Height * 0.32 - ra.Height * 0.353));
                if (confirmRectArea2.IsExist() && confirmRectArea2.Text.Contains("是否仍要挑战该秘境"))
                {
                    Logger.LogWarning("自动秘境：检测到树脂不足提示：{Text}", confirmRectArea2.Text);
                    throw new Exception("当前树脂不足，自动秘境停止运行。");
                }
            },
            _ct,
            10,
            1000
        );
        
        // 等待队伍选择界面出现
        var teamUiFound = await NewRetry.WaitForElementAppear(
            ElementRecognition.Get("PartyBtnChooseView"),
            () => { Logger.LogInformation("自动秘境：进入 {Text}", "队伍选择界面"); },
            _ct,
            10,
            1000
        );
        if (!teamUiFound)
        {
            Logger.LogWarning("队伍选择界面未出现，跳过切换队伍。");
        }
        else
        {
            await SwitchParty(_taskParam.PartyName);
        }
        
        // 点击开始挑战确认并等待“开始挑战”文字消失
        var startFightFound = await NewRetry.WaitForElementDisappear(
            GetConfirmRa("开始挑战"),
            screen =>
            {
                screen.Find(RecognitionAssets.Get("AutoFight", "Confirm", screen), ra =>
                {
                    ra.Click();
                    ra.Dispose();
                    Logger.LogInformation("自动秘境：点击 {Text}", "开始挑战");
                });
            },
            _ct,
            10,
            1000
        );
        if (!startFightFound)
        {
            Logger.LogWarning("开始挑战按钮未出现或未能点击。");
            //可能卡在秘境里，尝试退出秘境，按ESC，看有没有确认按键
            if (await NewRetry.WaitForElementAppear(
                    GetConfirmRa("确认"),
                    () => Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE),
                    _ct,
                    3,
                    2000)) 
            {
                // 点击确认并等待“确认”文字消失
                await NewRetry.WaitForElementDisappear(
                    GetConfirmRa("确认"),
                    screen => {  // 接收当前截图作为参数
                        // 截图并查找元素
                        var confirm =
                            screen.FindMulti(RecognitionObject.Ocr(screen.Width * 0.5,screen.Height * 0.5, screen.Width * 0.5,
                                screen.Height* 0.5));
                        var confirmDone = confirm.LastOrDefault(t =>
                            Regex.IsMatch(t.Text, "确认"));
                        if (confirmDone != null)
                        {
                            confirmDone.Click();
                            confirmDone.Dispose();
                        }
                    },
                    _ct,
                    20,
                    500);  
            }
            else
            {
                throw new Exception("可能在秘境里内，尝试退出秘境失败。");
            }
        }

        // 载入
        await Delay(1000, _ct);
    }

    private async Task CloseDomainTip()
    {
        //先等待秘境提示出现,如果直接出现Enter也属于完成条件
        var domainTipFound = await NewRetry.WaitForAction(() =>
        {
            using var ra = CaptureToRectArea();
            
            var ocrList = ra.FindMulti(RecognitionObject.Ocr(0, ra.Height * 0.2, ra.Width, ra.Height * 0.6));
            var ocrListLeft = ra.Find(RecognitionAssets.Get("AutoFight", "AbnormalIcon", ra));
            return (ocrList.Any(t => t.Text.Contains(leyLineDisorderLocalizedString) ||
                                     t.Text.Contains(clickanywheretocloseLocalizedString))) || ocrListLeft.IsExist();
        }, _ct, 40, 500);
        if (!domainTipFound)
        {
            Logger.LogWarning("秘境提示未出现或未能点击。");
        }

        //持续点击，直到左下角出现目标文字
        var leftBottomFound = await NewRetry.WaitForAction(() =>
        {
            using var ra = CaptureToRectArea();
            var ocrList = ra.FindMulti(RecognitionObject.Ocr(0, ra.Height * 0.2, ra.Width, ra.Height * 0.6));
            // 查找目标文字
            var done = ocrList.FirstOrDefault(t =>
                Regex.IsMatch(t.Text, this.leyLineDisorderLocalizedString) ||
                Regex.IsMatch(t.Text, this.clickanywheretocloseLocalizedString));
            if (done != null)
            {
                done.Click();
                done.Dispose();
                Logger.LogInformation("自动秘境：点击 {Text}", done.Text);
            }
            // 检查左下角区域是否还存在目标文字，消失则继续，存在则结束
            using var leftBottom = CaptureToRectArea();
            var leftBottomOcr = leftBottom.Find(RecognitionAssets.Get("AutoFight", "AbnormalIcon", leftBottom));
            return leftBottomOcr.IsExist();
        }, _ct, 20, 500);
        if (!leftBottomFound)
        {
            //尝试随意点击一下右下角
            GameCaptureRegion.GameRegion1080PPosClick(1515, 892);
            Logger.LogWarning("秘境提示未出现或未能点击。");
        }

        await Delay(500, _ct);
    }

    private List<CombatCommand> FindCombatScriptAndSwitchAvatar(CombatScenes combatScenes)
    {
        var combatCommands = _combatScriptBag.FindCombatScript(combatScenes.GetAvatars());
        var avatar = combatScenes.SelectAvatar(combatCommands[0].Name);
        avatar?.SwitchWithoutCts();
        Sleep(200);
        return combatCommands;
    }

    /// <summary>
    /// 走到钥匙处启动
    /// </summary>
    private async Task WalkToPressF()
    {
        if (_ct.IsCancellationRequested)
        {
            return;
        }

        await Task.Run((Action)(() =>
        {
            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
            Sleep(30, _ct);
            // 组合键好像不能直接用 postmessage
            if (!_config.WalkToF)
            {
                Simulation.SendInput.SimulateAction(GIActions.SprintKeyboard, KeyType.KeyDown);
            }

            try
            {
                var startTime = DateTime.Now;
                while (!_ct.IsCancellationRequested)
                {
                    using var gameCaptureRegion = Common.TaskControl.CaptureToRectArea();
                    var pickAssets = AutoPickAssets.Get(gameCaptureRegion, TaskContext.Instance().Config.AutoPickConfig.PickKey);
                    using var fRectArea = gameCaptureRegion.Find(pickAssets.PickRo);
                    if (fRectArea.IsEmpty())
                    {
                        Sleep(100, _ct);
                    }
                    else
                    {
                        Logger.LogInformation("检测到交互键");
                        Simulation.SendInput.Keyboard.KeyPress(pickAssets.PickVk);
                        break;
                    }

                    // 超时直接放弃整个秘境
                    if (DateTime.Now - startTime > TimeSpan.FromSeconds(60))
                    {
                        Logger.LogWarning("自动秘境：{Text}", "前往目标位置处超时，如果选择了秘境名称，将在传送后重试秘境！");
                        Avatar.TpForRecover(_ct, new RetryException("前往目标位置处超时，先传送到七天神像，然后重试秘境"));
                    }
                }
            }
            finally
            {
                Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                Sleep(50);
                if (!_config.WalkToF)
                {
                    Simulation.SendInput.SimulateAction(GIActions.SprintKeyboard, KeyType.KeyUp);
                }
            }
        }), _ct);
    }

    private Task StartFight(CombatScenes combatScenes, List<CombatCommand> combatCommands)
    {
        CancellationTokenSource cts = new();
        _ct.Register(cts.Cancel);
        combatScenes.BeforeTask(cts.Token);
        // 战斗操作
        var combatTask = new Task(() =>
        {
            try
            {
                AutoFightTask.FightStatusFlag = true;

                // JSON 策略：委托给 AutoFightJsonTask（秘境结束检测仍由 domainEndTask 接管，故关闭其内部结束检测）
                // official-autofight-parallel-engine spec §4.3(E4)：JSON 分支按全局开关路由（非联机）；
                // 非 JSON 分支（下方 combatCommands 直驱）无公版对应，保持茶包版。
                if (_jsonCombatStrategyPath != null)
                {
                    var jsonParam = new AutoFightParam(_jsonCombatStrategyPath, TaskContext.Instance().Config.AutoFightConfig)
                    {
                        FightFinishDetectEnabled = false
                    };
                    if (OfficialAutoFightRouter.UseOfficial(TaskContext.Instance().Config.AutoFightConfig, false))
                    {
                        var officialParam = OfficialParamAdapter.FromTeapot(jsonParam, TaskContext.Instance().Config.AutoFightOfficialConfig);
                        new OfficialJsonTask(officialParam).Start(cts.Token).Wait(cts.Token);
                    }
                    else
                    {
                        new AutoFightJsonTask(jsonParam).Start(cts.Token).Wait(cts.Token);
                    }
                }
                else
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        // 通用化战斗策略
                        foreach (var command in combatCommands)
                        {
                            command.Execute(combatScenes);
                        }
                    }
                }
            }
            catch (NormalEndException e)
            {
                Logger.LogInformation("战斗操作中断：{Msg}", e.Message);
            }
            catch (OperationCanceledException)
            {
                // 秘境结束检测触发的正常取消，静默处理
            }
            catch (Exception e)
            {
                Logger.LogWarning(e.Message);
                throw;
            }
            finally
            {
                Logger.LogInformation("自动战斗线程结束");
                Simulation.ReleaseAllKey();
                AutoFightTask.FightStatusFlag = false;
            }
        }, cts.Token);

        // 对局结束检测
        var domainEndTask = DomainEndDetectionTask(cts);
        // 自动吃药
        // var autoEatRecoveryHpTask = AutoEatRecoveryHpTask(cts.Token);
        combatTask.Start();
        domainEndTask.Start();
        // autoEatRecoveryHpTask.Start();
        return Task.WhenAll(combatTask, domainEndTask);
    }

    private void EndFightWait()
    {
        if (_ct.IsCancellationRequested)
        {
            return;
        }

        var s = TaskContext.Instance().Config.AutoDomainConfig.FightEndDelay;
        if (s > 0)
        {
            Logger.LogInformation("战斗结束后等待 {Second} 秒", s);
            Sleep((int)(s * 1000), _ct);
        }
    }

    /// <summary>
    /// 对局结束检测
    /// </summary>
    private Task DomainEndDetectionTask(CancellationTokenSource cts)
    {
        return new Task(async () =>
        {
            try
            {
                while (!_ct.IsCancellationRequested)
                {
                    if (IsDomainEnd())
                    {
                        await cts.CancelAsync();
                        break;
                    }

                    await Delay(1000, cts.Token);
                }
            }
            catch
            {
            }
        }, cts.Token);
    }

    private bool IsDomainEnd()
    {
        using var ra = CaptureToRectArea();

        var fightAssets = AutoFightAssets.Get(ra);
        var endTipsRect = ra.DeriveCrop(fightAssets.EndTipsUpperRect);
        var text = OcrFactory.Paddle.Ocr(endTipsRect.SrcMat);
        if (Regex.IsMatch(text, this.challengeCompletedLocalizedString))
        {
            Logger.LogInformation("检测到秘境结束提示(挑战达成)，结束秘境");
            return true;
        }

        endTipsRect = ra.DeriveCrop(fightAssets.EndTipsRect);
        text = OcrFactory.Paddle.Ocr(endTipsRect.SrcMat);
        if (Regex.IsMatch(text, this.autoLeavingLocalizedString))
        {
            Logger.LogInformation("检测到秘境结束提示(xxx秒后自动退出)，结束秘境");
            return true;
        }

        return false;
    }

    private Task AutoEatRecoveryHpTask(CancellationToken ct)
    {
        return new Task(async () =>
        {
            if (!_config.AutoEat)
            {
                return;
            }

            if (!IsTakeFood())
            {
                Logger.LogInformation("未装备 “{Tool}”，不启用红血自动吃药功能", "便携营养袋");
                return;
            }

            try
            {
                while (!_ct.IsCancellationRequested)
                {
                    using var capture = CaptureToRectArea();
                    if (Bv.CurrentAvatarIsLowHp(capture))
                    {
                        // 模拟按键 "Z"
                        Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget);
                        Logger.LogInformation("检测到红血，按Z吃药");
                        // TODO 吃饱了会一直吃
                    }

                    await Delay(500, ct);
                }
            }
            catch (Exception e)
            {
                Logger.LogDebug(e, "红血自动吃药检测时发生异常");
            }
        }, ct);
    }

    private bool IsTakeFood()
    {
        // 获取图像
        using var ra = CaptureToRectArea();
        // 识别道具图标下是否是数字
        var s = TaskContext.Instance().SystemInfo.AssetScale;
        var countArea = ra.DeriveCrop(1800 * s, 845 * s, 40 * s, 20 * s);
        var count = OcrFactory.Paddle.OcrWithoutDetector(countArea.CacheGreyMat);
        return int.TryParse(count, out _);
    }

    /// <summary>
    /// 旋转视角后寻找石化古树
    /// </summary>
    private Task FindPetrifiedTree()
    {
        CancellationTokenSource treeCts = new();
        _ct.Register(treeCts.Cancel);
        // 中键回正视角
        Simulation.SendInput.Mouse.MiddleButtonClick();
        Sleep(900, _ct);

        // 左右移动直到石化古树位于屏幕中心任务
        var moveAvatarTask = MoveAvatarHorizontallyTask(treeCts);

        // 锁定东方向视角线程
        var lockCameraToEastTask = LockCameraToEastTask(treeCts, moveAvatarTask);
        lockCameraToEastTask.Start();
        return Task.WhenAll(moveAvatarTask, lockCameraToEastTask);
    }

    private Task MoveAvatarHorizontallyTask(CancellationTokenSource treeCts)
    {
        return new Task(() =>
        {
            var keyConfig = TaskContext.Instance().Config.KeyBindingsConfig;
            var moveLeftKey = keyConfig.MoveLeft.ToVK();
            var moveRightKey = keyConfig.MoveRight.ToVK();
            var moveForwardKey = keyConfig.MoveForward.ToVK();
            var captureArea = TaskContext.Instance().SystemInfo.ScaleMax1080PCaptureRect;
            var middleX = captureArea.Width / 2;
            var leftKeyDown = false;
            var rightKeyDown = false;
            var noDetectCount = 0;
            var prevKey = moveLeftKey;
            var backwardsAndForwardsCount = 0;
            while (!_ct.IsCancellationRequested)
            {
                using var capture = CaptureToRectArea();
                var treeRect = DetectTree(capture);
                if (treeRect != default)
                {
                    var treeMiddleX = treeRect.X + treeRect.Width / 2;
                    if (treeRect.X + treeRect.Width < middleX && !_config.ShortMovement)
                    {
                        backwardsAndForwardsCount = 0;
                        // 树在左边 往左走
                        Debug.WriteLine($"树在左边 往左走 {treeMiddleX}  {middleX}");
                        if (rightKeyDown)
                        {
                            // 先松开D键
                            Simulation.SendInput.Keyboard.KeyUp(moveRightKey);
                            rightKeyDown = false;
                        }

                        if (!leftKeyDown)
                        {
                            Simulation.SendInput.Keyboard.KeyDown(moveLeftKey);
                            leftKeyDown = true;
                        }
                    }
                    else if (treeRect.X > middleX && !_config.ShortMovement)
                    {
                        backwardsAndForwardsCount = 0;
                        // 树在右边 往右走
                        Debug.WriteLine($"树在右边 往右走 {treeMiddleX}  {middleX}");
                        if (leftKeyDown)
                        {
                            // 先松开A键
                            Simulation.SendInput.Keyboard.KeyUp(moveLeftKey);
                            leftKeyDown = false;
                        }

                        if (!rightKeyDown)
                        {
                            Simulation.SendInput.Keyboard.KeyDown(moveRightKey);
                            rightKeyDown = true;
                        }
                    }
                    else
                    {
                        // 树在中间 松开所有键
                        if (rightKeyDown)
                        {
                            Simulation.SendInput.Keyboard.KeyUp(moveRightKey);
                            prevKey = moveRightKey;
                            rightKeyDown = false;
                        }

                        if (leftKeyDown)
                        {
                            Simulation.SendInput.Keyboard.KeyUp(moveLeftKey);
                            prevKey = moveLeftKey;
                            leftKeyDown = false;
                        }

                        // 松开按键后使用小碎步移动
                        if (treeMiddleX < middleX)
                        {
                            if (prevKey == moveRightKey)
                            {
                                backwardsAndForwardsCount++;
                            }

                            Simulation.SendInput.Keyboard.KeyDown(moveLeftKey);
                            Sleep(60);
                            Simulation.SendInput.Keyboard.KeyUp(moveLeftKey);
                            prevKey = moveLeftKey;
                        }
                        else if (treeMiddleX > middleX)
                        {
                            if (prevKey == moveLeftKey)
                            {
                                backwardsAndForwardsCount++;
                            }

                            Simulation.SendInput.Keyboard.KeyDown(moveRightKey);
                            Sleep(60);
                            Simulation.SendInput.Keyboard.KeyUp(moveRightKey);
                            prevKey = moveRightKey;
                        }
                        else
                        {
                            Simulation.SendInput.Keyboard.KeyDown(moveForwardKey);
                            Sleep(60);
                            Simulation.SendInput.Keyboard.KeyUp(moveForwardKey);
                            Sleep(500, _ct);
                            treeCts.Cancel();
                            break;
                        }
                    }
                }
                else
                {
                    backwardsAndForwardsCount = 0;
                    // 左右巡逻
                    noDetectCount++;
                    if (noDetectCount > 40)
                    {
                        if (leftKeyDown)
                        {
                            Simulation.SendInput.Keyboard.KeyUp(moveLeftKey);
                            leftKeyDown = false;
                        }

                        if (!rightKeyDown)
                        {
                            Simulation.SendInput.Keyboard.KeyDown(moveRightKey);
                            rightKeyDown = true;
                        }
                    }
                    else
                    {
                        if (rightKeyDown)
                        {
                            Simulation.SendInput.Keyboard.KeyUp(moveRightKey);
                            rightKeyDown = false;
                        }

                        if (!leftKeyDown)
                        {
                            Simulation.SendInput.Keyboard.KeyDown(moveLeftKey);
                            leftKeyDown = true;
                        }
                    }
                }

                if (backwardsAndForwardsCount >= _config.LeftRightMoveTimes)
                {
                    // 左右移动5次说明已经在树中心了
                    Simulation.SendInput.Keyboard.KeyDown(moveForwardKey);
                    Sleep(60);
                    Simulation.SendInput.Keyboard.KeyUp(moveForwardKey);
                    Sleep(500, _ct);
                    treeCts.Cancel();
                    break;
                }

                Sleep(60, _ct);
            }

            VisionContext.Instance().DrawContent.ClearAll();
        });
    }

    private Rect DetectTree(ImageRegion region)
    {
        var result = _predictor.Predictor.Detect(region.CacheImage);
        var list = new List<RectDrawable>();
        foreach (var box in result)
        {
            var rect = new Rect(box.Bounds.X, box.Bounds.Y, box.Bounds.Width, box.Bounds.Height);
            list.Add(region.ToRectDrawable(rect, "tree"));
        }

        VisionContext.Instance().DrawContent.PutOrRemoveRectList("TreeBox", list);

        if (list.Count > 0)
        {
            var box = result[0];
            return new Rect(box.Bounds.X, box.Bounds.Y, box.Bounds.Width, box.Bounds.Height);
        }

        return default;
    }

    private Task LockCameraToEastTask(CancellationTokenSource cts, Task moveAvatarTask)
    {
        return new Task(() =>
        {
            var continuousCount = 0; // 连续东方向次数
            var started = false;
            while (!cts.Token.IsCancellationRequested)
            {
                using var captureRegion = CaptureToRectArea();
                var angle = CameraOrientation.Compute(captureRegion.SrcMat);
                CameraOrientation.DrawDirection(captureRegion, angle);
                if (angle is >= 356 or <= 4)
                {
                    // 算作对准了
                    continuousCount++;
                    // 360 度 东方向视角
                    if (continuousCount > 5)
                    {
                        if (!started && moveAvatarTask.Status != TaskStatus.Running)
                        {
                            started = true;
                            moveAvatarTask.Start();
                        }
                    }
                }
                else
                {
                    continuousCount = 0;
                }

                if (angle <= 180)
                {
                    // 左移视角
                    var moveAngle = (int)Math.Round(angle);
                    if (moveAngle > 2)
                    {
                        moveAngle *= 2;
                    }

                    Simulation.SendInput.Mouse.MoveMouseBy(-moveAngle, 0);
                }
                else if (angle is > 180 and < 360)
                {
                    // 右移视角
                    var moveAngle = 360 - (int)Math.Round(angle);
                    if (moveAngle > 2)
                    {
                        moveAngle *= 2;
                    }

                    Simulation.SendInput.Mouse.MoveMouseBy(moveAngle, 0);
                }

                Sleep(100, _ct);
            }

            Logger.LogInformation("锁定东方向视角线程结束");
            VisionContext.Instance().DrawContent.ClearAll();
        });
    }

    /// <summary>
    /// 领取奖励
    /// </summary>
    /// <param name="recognizeResin">是否识别树脂</param>
    /// <param name="isLastTurn">是否最后一轮</param>
    private bool GettingTreasure(bool recognizeResin, bool isLastTurn,int fihgtCount)
    {
        //移开鼠标
        GlobalMethod.MoveMouseTo(100,100);
        Sleep(800, _ct);
        
        var resinType = _taskParam.ResinCount.Keys.ToList();
        //默认模式下移除脆弱树脂和须臾树脂
        if (!_taskParam.SpecifyResinUse)
        {
            //默认模式下移除脆弱树脂和须臾树脂
            resinType = resinType.Where(t => t != "须臾树脂" && t != "脆弱树脂").ToList();
            Logger.LogInformation("自动秘境：可使用的树脂类型：{ResinType}", resinType);
        }
        // 创建一个字典来映射树脂类型到对应的使用次数变量
        var resinUsedCountMap = new Dictionary<string, int>
        {
            { "浓缩树脂", condensedResinUsedCount },
            { "原粹树脂", originalResinUsedCount },
            { "须臾树脂", momentResinUsedCount },
            { "脆弱树脂", fragileResinUsedCount },
        };
        
        // 等待窗口弹出
        var retryTimes = 0;
        while (true)
        {
            retryTimes++;
            if (retryTimes > 4) //情况1：首次和没找到石化古树的情况
            {
                for (int i = 0; i < 2; i++) //防止卡顿
                {
                    Simulation.ReleaseAllKey();
                    TaskContext.Instance().PostMessageSimulator.SimulateAction(GIActions.OpenPaimonMenu);
                    Sleep(980, _ct);
                    var exitRara1 = CaptureToRectArea();
                    var exitRectArea1 = exitRara1.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).BlackConfirmRa);
                    if (!exitRectArea1.IsEmpty())
                    {                    
                        Logger.LogInformation("没有可选择的树脂了，退出自动秘境");
                        exitRectArea1.Click();
                        Sleep(1500, _ct);
                        var exitRara2 = CaptureToRectArea();
                        var exitRectArea2 = exitRara2.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).BlackConfirmRa);
                        if (exitRectArea2.IsEmpty())
                        {
                            Logger.LogInformation("自动秘境结束");
                            return false;
                        }
                        else
                        {
                            exitRectArea1.Click();
                        }
                    }
                    if (retryTimes > 60)
                    {
                        Logger.LogError("领取奖励失败,没有找到退出确认按钮，自动秘境结束");
                        return false;
                    }
                }
            }
            
            using (var ra = CaptureToRectArea())
            {
                var ancientTreeStringArea = ra.FindMulti(RecognitionObject.Ocr(ra.Width * 0.4, ra.Height * 0.2,
                    ra.Width * 0.2, ra.Height * 0.2));
                var done = ancientTreeStringArea.LastOrDefault(t =>
                    Regex.IsMatch(t.Text, this.ancientTreeString));
                if (done != null) // 
                {
                    Logger.LogInformation("自动秘境：检测到石化古树，领取奖励...");
                    
                    var useCondensedResinRa = ra.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).UseCondensedResinRa); 
                    var useOriginalResinRa = ra.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).UseOriginalResinRa); 
                    var useMomentResinRa = ra.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).UseMomentResinRa); 
                    var useFragileResinRa = ra.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).UseFragileResinRa);

                    Sleep(100, _ct);
                    
                    //第一次使用树脂转换40按键
                    if (fihgtCount == 0)
                    {
                        var use40ResinRa = ra.FindMulti(RecognitionObject.Ocr(ra.Width *0.34, ra.Height * 0.43, ra.Width*0.13, ra.Height*0.084));
                        var use40Resin = use40ResinRa.LastOrDefault(t =>
                            Regex.IsMatch(t.Text, "使用20个原粹树脂"));
                        if (use40Resin != null)
                        {
                            Logger.LogInformation("使用20个原粹树脂");
                            use40Resin.ClickTo(ra.Width / 4, 10);
                            Sleep(200, _ct);
                        }  
                    }
                    
                    done.Click();//移开鼠标
                    Sleep(100, _ct);

                    //如果设定包含了脆弱树脂和须臾树脂，如果存在须臾树脂，脆弱树脂将无法使用，用LOG提示
                    if (!useMomentResinRa.IsEmpty() && resinType.Contains("脆弱树脂"))
                    {
                        Logger.LogWarning("自动秘境：须臾树脂存在，脆弱树脂将无法使用");
                    }
                    
                    var replenishStringArea = ra.FindMulti(RecognitionObject.Ocr(ra.Width * 0.5, ra.Height * 0.3,
                        ra.Width * 0.25, ra.Height * 0.3));
                    var replenishStringdone = replenishStringArea.LastOrDefault(t =>
                        Regex.IsMatch(t.Text, this.replenishString));//补充原粹树脂按钮文字

                    // Logger.LogInformation("自动秘境：树脂使用顺序：{ResinType}", resinType);

                    if (resinType.Count > 0 && resinType[0] == "浓缩树脂" && useCondensedResinRa.IsEmpty())
                    {
                        Logger.LogInformation("没有找到 \"浓缩树脂\" 按键");
                        resinType.Remove("浓缩树脂");   
                    }

                    if (resinType.Count > 0 && resinType[0] == "原粹树脂"  && useOriginalResinRa.IsEmpty() || replenishStringdone != null)
                    {
                        Logger.LogInformation("没有找到 \"原粹树脂\" 按键");
                        resinType.Remove("原粹树脂");
                    }

                    if (resinType.Count > 0 && resinType[0] == "须臾树脂" && useMomentResinRa.IsEmpty())
                    {
                        Logger.LogInformation("没有找到 \"须臾树脂\" 按键");
                        resinType.Remove("须臾树脂");
                    }
                    
                    if (resinType.Count > 0 && resinType[0] == "脆弱树脂" && useFragileResinRa.IsEmpty())
                    {
                        Logger.LogInformation("没有找到 \"脆弱树脂\" 按键");
                        resinType.Remove("脆弱树脂");
                    }

                    // Logger.LogInformation("自动秘境：可使用的树脂类型：{ResinType}", resinType);
                    
                    if (resinType.Count == 0) //情况2：第一次打完，没找到树脂的情况
                    {   
                        Simulation.ReleaseAllKey();
                        for (int i = 0; i < 62; i++) //防止卡顿
                        {
                            TaskContext.Instance().PostMessageSimulator.SimulateAction(GIActions.OpenPaimonMenu);
                            Sleep(980, _ct);
                            var exitRara1 = CaptureToRectArea();
                            var exitRectArea1 = exitRara1.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).BlackConfirmRa);
                            if (!exitRectArea1.IsEmpty())
                            {
                                Logger.LogInformation("自动秘境：没有可选择的树脂了，退出自动秘境");
                                exitRectArea1.Click();
                                Sleep(1500, _ct);
                                var exitRara2 = CaptureToRectArea();
                                var exitRectArea2 = exitRara2.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).BlackConfirmRa);
                                if (exitRectArea2.IsEmpty())
                                {
                                    Logger.LogInformation("自动秘境结束");
                                    return false;
                                }
                                else
                                {
                                    exitRectArea1.Click();
                                }
                            }
                            if (i > 60)
                            {
                                Logger.LogError("自动秘境：没有找到退出确认按钮，自动秘境结束");
                                return false;
                            }
                        }
                    }

                    //自定义模式下，检查树脂使用次数
                    if (_taskParam.SpecifyResinUse)
                    {
                        // 显示所有树脂类型的使用次数
                        Logger.LogInformation("自动秘境：{ResinType0} {Condensed}/{CondensedLimit} , {ResinType1} {Original}/{OriginalLimit}",
                            "浓缩树脂", condensedResinUsedCount, _taskParam.ResinCount["浓缩树脂"],
                            "原粹树脂", originalResinUsedCount, _taskParam.ResinCount["原粹树脂"]);
                        Logger.LogInformation("自动秘境：{ResinType2} {Moment}/{MomentLimit} , {ResinType3} {Fragile}/{FragileLimit}",
                            "须臾树脂", momentResinUsedCount, _taskParam.ResinCount["须臾树脂"],
                            "脆弱树脂", fragileResinUsedCount, _taskParam.ResinCount["脆弱树脂"]);
                        
                        // 厉遍检查每种树脂的使用次数
                        foreach (var resin in resinUsedCountMap)
                        {
                            if (resin.Value >= _taskParam.ResinCount[resin.Key])
                            {
                                Logger.LogInformation("自动秘境：{ResinType} 使用次数已达上限", resin.Key);
                                resinType.Remove(resin.Key);
                            }
                        }

                        if (resinType.Count == 0)
                        {
                            for (int i = 0; i < 62; i++) //防止卡顿
                            {
                                TaskContext.Instance().PostMessageSimulator.SimulateAction(GIActions.OpenPaimonMenu);
                                Sleep(980, _ct);
                                var exitRara1 = CaptureToRectArea();
                                var exitRectArea1 = exitRara1.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).BlackConfirmRa);
                                if (!exitRectArea1.IsEmpty())
                                {
                                    Logger.LogInformation("自动秘境：没有可用树脂或次数到达限制，退出自动秘境");
                                    exitRectArea1.Click();
                                    Sleep(1500, _ct);
                                    var exitRara2 = CaptureToRectArea();
                                    var exitRectArea2 = exitRara2.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).BlackConfirmRa);
                                    if (exitRectArea2.IsEmpty())
                                    {
                                        Logger.LogInformation("自动秘境结束");
                                        return false;
                                    }
                                    else
                                    {
                                        exitRectArea1.Click();
                                    }
                                }
                                if (i > 60)
                                {
                                    Logger.LogError("自动秘境：没有找到退出确认按钮，自动秘境结束");
                                    return false;
                                }
                            }
                        }
                    }
                    
                    // 根据树脂类型进行领取奖励
                    if (resinType[0] == "浓缩树脂" && !useCondensedResinRa.IsEmpty() && ((condensedResinUsedCount < _taskParam.ResinCount["浓缩树脂"]) || !_taskParam.SpecifyResinUse))
                    {
                        Logger.LogInformation("使用浓缩树脂");
                        condensedResinUsedCount++;
                        resinUsedCountMap["浓缩树脂"] = condensedResinUsedCount;
                        useCondensedResinRa.ClickTo(ra.Width / 3, useCondensedResinRa.Height / 2); //ra.Width / 3 要进行确认
                        Sleep(100, _ct);
                        useCondensedResinRa.ClickTo(ra.Width / 3, useCondensedResinRa.Height / 2);
                        break;
                    }

                    if (resinType[0] == "原粹树脂" && !useOriginalResinRa.IsEmpty() && ((originalResinUsedCount < _taskParam.ResinCount["原粹树脂"]) || !_taskParam.SpecifyResinUse))
                    {
                        Logger.LogInformation("使用原粹树脂");
                        originalResinUsedCount++;
                        resinUsedCountMap["原粹树脂"] = originalResinUsedCount; 
                        useOriginalResinRa.ClickTo(ra.Width / 3, useOriginalResinRa.Height / 2);
                        Sleep(100, _ct);
                        useOriginalResinRa.ClickTo(ra.Width / 3, useOriginalResinRa.Height / 2);
                        break;
                    }

                    if (resinType[0] == "须臾树脂" && !useMomentResinRa.IsEmpty() && momentResinUsedCount < _taskParam.ResinCount["须臾树脂"])
                    {
                        Logger.LogInformation("使用须臾树脂");
                        momentResinUsedCount++;
                        resinUsedCountMap["须臾树脂"] = momentResinUsedCount; 
                        useMomentResinRa.ClickTo(ra.Width / 3, useMomentResinRa.Height / 2);
                        Sleep(100, _ct);
                        useMomentResinRa.ClickTo(ra.Width / 3, useMomentResinRa.Height / 2);
                        break;
                    }
                    
                    if (resinType[0] == "脆弱树脂" && !useFragileResinRa.IsEmpty() && fragileResinUsedCount < _taskParam.ResinCount["脆弱树脂"])
                    {
                        Logger.LogInformation("使用脆弱树脂");
                        fragileResinUsedCount++;
                        resinUsedCountMap["脆弱树脂"] = fragileResinUsedCount; 
                        useFragileResinRa.ClickTo(ra.Width / 3, useFragileResinRa.Height / 2);
                        Sleep(100, _ct);
                        useFragileResinRa.ClickTo(ra.Width / 3, useFragileResinRa.Height / 2);
                        break;
                    }
                }
            }
            Sleep(900, _ct);    
        }
        
        bool shouldExit2 = false;
        //再检验厉遍检查每种树脂的使用次数，如果全部树脂使用次数都达到了上限，则退出秘境
        if (_taskParam.SpecifyResinUse)
        {
            foreach (var resin in resinUsedCountMap)
            {
                Logger.LogInformation("自动秘境：{ResinType} 使用次数：{Count}", resin.Key, resin.Value);
                if (resin.Value >= _taskParam.ResinCount[resin.Key])
                {
                    Logger.LogInformation("自动秘境：{ResinType} 使用次数已达上限", resin.Key);
                    resinType.Remove(resin.Key);
                }
            }

            if (resinType.Count == 0)
            {
                shouldExit2 = true;
            }
        }

        Sleep(1000, _ct);
        TryRecognizeRewardResult();

        for (var i = 0; i < 30; i++)
        {
            using var ra = CaptureToRectArea();
            // 优先点击继续
            using var confirmRectArea = ra.Find(RecognitionAssets.Get("AutoFight", "Confirm", ra));
            if (!confirmRectArea.IsEmpty())
            {
                var skipAnimationStringArea = ra.FindMulti(RecognitionObject.Ocr(0, 0,
                    ra.Width * 0.2, ra.Height * 0.1));
                var done = skipAnimationStringArea.LastOrDefault(t =>
                    Regex.IsMatch(t.Text, this.skipAnimationString));//跳过动画按钮文字
                
                var innerConfirmRectArea = ra.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).ConfirmRa);//继续按键
                
                if (!innerConfirmRectArea.IsEmpty() && done != null) //顶部树脂显示和确认按键不同步，双层确认
                {
                    Sleep(1050, _ct);
                    if (isLastTurn)
                    {
                        // 最后一回合 退出
                        Logger.LogInformation("最后一回合，退出秘境");
                        var exitRectArea = ra.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).ExitRa);
                        if (!exitRectArea.IsEmpty())
                        {
                            exitRectArea.Click();
                            return false;
                        }
                    }

                    if (!recognizeResin)
                    {
                        Logger.LogInformation("领取奖励完成，退出秘境");
                        innerConfirmRectArea.Click();
                        return true;
                    }
                    
                    var (condensedResinCount, originalResinCount,momentResinCount,fragileResinCount) = GetRemainResinStatus();
          
                    // 根据 _taskParam.ResinOrder 中是否有对应的树脂类型，判断是否有体力
                    bool shouldExit = true;

                    if (resinType.Contains("浓缩树脂") && ((condensedResinUsedCount < _taskParam.ResinCount["浓缩树脂"]) || !_taskParam.SpecifyResinUse))
                    {
                        shouldExit &= (condensedResinCount == 0);
                    }

                    if (resinType.Contains("原粹树脂") && (originalResinUsedCount < _taskParam.ResinCount["原粹树脂"] || !_taskParam.SpecifyResinUse))
                    {
                        shouldExit &= (originalResinCount < 20);
                    }

                    if (resinType.Contains("脆弱树脂") && fragileResinUsedCount < _taskParam.ResinCount["脆弱树脂"])
                    { 
                        shouldExit &= (fragileResinCount == 0);
                    }
                    
                    if (resinType.Contains("须臾树脂") && momentResinCount < _taskParam.ResinCount["须臾树脂"])
                    { 
                        shouldExit &= (momentResinCount == 0);
                    }
                    
                    //根据_taskParam.ResinOrder中是否有对应的树脂类型，判断是否有体力，//情况3：领奖后体力不足
                    if (shouldExit || shouldExit2) {
                        // 没有体力了退出秘境
                        for (int j = 0; j < 4; j++) //防止卡顿
                        {
                            Simulation.ReleaseAllKey();
                            var exitRara = CaptureToRectArea();
                            var exitRectArea = exitRara.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).ExitRa);
                            if (!exitRectArea.IsEmpty())
                            {
                                Logger.LogInformation("自动秘境：树脂不足或次数到达限制，退出秘境");
                                exitRectArea.Click();
                                Sleep(1500, _ct);
                                var exitRara2 = CaptureToRectArea();
                                var exitRectArea2 = exitRara2.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).BlackConfirmRa);
                                if (exitRectArea2.IsEmpty())
                                {
                                    Logger.LogInformation("自动秘境结束");
                                    return false;
                                }
                                else
                                {
                                    exitRectArea.Click();
                                }
                            }
                            Sleep(1000, _ct);
                        }
                        Logger.LogInformation("自动秘境：没有找到确认按钮");
                        return false;
                    } 
                    
                    // var skipAnimationRa = ra.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).SkipanimationRa); //检测是否打开跳过动画
                    // if (skipAnimationRa.IsEmpty())
                    // {
                    //     Logger.LogInformation("检测到跳过动画未启动，启用跳过");
                    //     Sleep(1000, _ct);
                    //     GameCaptureRegion.GameRegion1080PPosClick(66, 50);//点击屏幕(66,50);
                    //     Sleep(1000, _ct);
                    // }
                    
                    // 有体力继续
                    Logger.LogInformation("自动秘境：还有树脂，继续执行自动秘境");
                    confirmRectArea.Click();
                    return true;
                }
            }
            Sleep(300, _ct);
        }

        throw new NormalEndException("未检测到秘境结束，可能是背包物品已满。");
    }

    private void TryRecognizeRewardResult()
    {
        if (!_taskParam.RewardRecognitionEnabled)
        {
            return;
        }

        try
        {
            // 使用多页识别（自动检测是否需要翻页）
            Logger.LogInformation("自动秘境：开始奖励识别");
            var rewards = RewardResultRecognizer.Instance.RecognizeMultiPage();

            RewardResultRecognizer.MergeIntoSummary(_rewardSummary, rewards);

            if (rewards.Count > 0)
            {
                Logger.LogInformation("自动秘境：本轮奖励识别结果 {Rewards}",
                    string.Join(", ", rewards.Select(r => $"{r.Key} x{r.Value}")));
            }
            else
            {
                Logger.LogWarning("自动秘境：本轮奖励识别结果为空");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Logger.LogWarning(e, "自动秘境：奖励识别失败，已跳过本轮奖励汇总");
        }
    }
    
    /// <summary>
    /// 获取剩余树脂状态
    /// </summary>
    private (int, int, int, int) GetRemainResinStatus()
    {
        var condensedResinCount = 0; //浓缩树脂
        var originalResinCount = 0; //原粹树脂
        var fragileResinCount = 0; //脆弱树脂
        var momentResinCount = 0; //须臾树脂
        var autoFightAssets = AutoFightAssets.Get(_captureRect.Width, _captureRect.Height);

        using (var ra = CaptureToRectArea())
        {
            // 浓缩树脂，//可以识别 √
            var condensedResinCountRa = ra.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).CondensedResinCountRa);
            if (!condensedResinCountRa.IsEmpty())
            {
                // 图像右侧就是浓缩树脂数量
                using (var countArea = ra.DeriveCrop(condensedResinCountRa.X + condensedResinCountRa.Width,
                           condensedResinCountRa.Y,
                           condensedResinCountRa.Width*2, condensedResinCountRa.Height))
                {
                    for (var i = 0; i < 6; i++)
                    {
                        var countResult = countArea.Find(autoFightAssets.InitializeCondensedResin(i));
                        if (countResult.IsEmpty())
                        {
                            if (i == 5)
                            {
                                Logger.LogInformation("浓缩树脂数量识别失败，尝试使用OCR识别");
                                var countG = OcrFactory.Paddle.OcrWithoutDetector(countArea.SrcMat);
                                condensedResinCount = StringUtils.TryParseInt(countG);
                            }
                            continue;
                        }
                        condensedResinCount = i;
                        break;
                    }
                    
                    // Logger.LogInformation("测试LOG：提取到的浓缩树脂数量：MUB{CondensedResinCount} OCR{CondensedResinCountG}", condensedResinCount, condensedResinCountG);
                }
            }
            else
            {
                Logger.LogInformation("未检测到浓缩树脂数量");
            }

            // 原粹树脂
            var originalResinCountRa = ra.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).OriginalResinCountRa);
            if (!originalResinCountRa.IsEmpty())
            {
                // Logger.LogInformation("测试LOG：检测到原粹树脂图标");
                // 图像右侧就是原粹树脂数量
                using (var countArea = ra.DeriveCrop(originalResinCountRa.X + originalResinCountRa.Width,
                    originalResinCountRa.Y,
                    (int)(originalResinCountRa.Width * 4), originalResinCountRa.Height))
                {
                    bool extracted = false;

                    for (int i = 0; i < 2 && !extracted; i++)
                    {
                        string count;
                        if (i == 0)
                        {
                            count = OcrFactory.Paddle.OcrWithoutDetector(countArea.SrcMat);
                            Logger.LogInformation("第一次识别原粹树脂数量：{Count}", count);
                        }
                        else
                        {
                            count = OcrFactory.Paddle.Ocr(countArea.SrcMat);
                            Logger.LogInformation("第二次识别原粹树脂数量：{Count}", count);
                        }

                        // 使用正则表达式提取 1 或 / 前面的纯数值
                        var match = System.Text.RegularExpressions.Regex.Match(count, @"(\d+)\s*[/17]\s*(2|20|200)");
                        // var match = System.Text.RegularExpressions.Regex.Match(count, @"(\d+)\s*[/1]\s*200");
                        if (match.Success)
                        {
                            var numericPart = match.Groups[1].Value;
                            originalResinCount = StringUtils.TryParseInt(numericPart);
                            Logger.LogInformation("提取到的原粹树脂数量：{OriginalResinCount}", originalResinCount);
                            extracted = true;
                        }
                    }

                    if (!extracted)
                    {
                        Logger.LogInformation("两次识别都无法提取原粹树脂数量，设置识别值");
                        originalResinCount = 0; // 或者其他默认值
                    }
                }
            }

            if (originalResinCount == 0)
            {
                var replenishStringArea = ra.FindMulti(RecognitionObject.Ocr(ra.Width * 0.5, ra.Height * 0.3,
                    ra.Width * 0.25, ra.Height * 0.3));
                var replenishStringdone = replenishStringArea.LastOrDefault(t =>
                    Regex.IsMatch(t.Text, this.replenishString));//补充原粹树脂按钮文字
                if (replenishStringdone != null)
                {
                    Logger.LogInformation("检测到补充原粹树脂按钮,原粹树脂不足");
                    originalResinCount = 0;
                }else
                {
                    Logger.LogInformation("未检测到补充原粹树脂按钮,强制设定树脂数量 0");
                    originalResinCount = 0; // 强制设定树脂数量 20
                }
            }
            

            // 脆弱树脂 //可以识别 √
            var fragileResinCountRa = ra.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).FragileResinCountRa); 
            if (!fragileResinCountRa.IsEmpty())
            {
                // Logger.LogInformation("测试LOG：检测到脆弱树脂图标");
                using (var countArea = ra.DeriveCrop(fragileResinCountRa.X + fragileResinCountRa.Width, fragileResinCountRa.Y,
                    (int)(fragileResinCountRa.Width * 3), fragileResinCountRa.Height))
                {
                    var count = OcrFactory.Paddle.OcrWithoutDetector(countArea.SrcMat);
                    fragileResinCount = StringUtils.TryParseInt(count);
                }
            }
            else
            {
                // Logger.LogInformation("测试LOG：未检测到脆弱树脂图标");
                // 须臾树脂
                var momentResinCountRa = ra.Find(AutoFightAssets.Get(_captureRect.Width, _captureRect.Height).MomentResinCountRa); 
                if (!momentResinCountRa.IsEmpty()) {
                    Logger.LogInformation("测试LOG：检测到须臾树脂图标");
                    using (var countArea = ra.DeriveCrop(momentResinCountRa.X + momentResinCountRa.Width, momentResinCountRa.Y,
                               (int)(momentResinCountRa.Width * 3), momentResinCountRa.Height))
                    {
                        var count = OcrFactory.Paddle.OcrWithoutDetector(countArea.SrcMat);
                        momentResinCount = StringUtils.TryParseInt(count);
                        Logger.LogInformation("强制设定脆弱脂数量：{Count}", 1);  
                        fragileResinCount = 1;
                    }
                }
                else
                {
                    Logger.LogInformation("测试LOG：未检测到须臾树脂图标");  
                }
            }
        }
        Logger.LogInformation("剩余：浓缩树脂 {CondensedResinCount} 原粹树脂 {OriginalResinCount} 须臾树脂 {MomentResinCount} 脆弱树脂 {FragileResinCount}  ", condensedResinCount,originalResinCount,
            momentResinCount,fragileResinCount);
        return (condensedResinCount , originalResinCount , momentResinCount, fragileResinCount);

    }

    private static bool IsHeightOverlap(Region region1, Region region2)
    {
        int region1Top = region1.Y;
        int region1Bottom = region1.Y + region1.Height;
        int region2Top = region2.Y;
        int region2Bottom = region2.Y + region2.Height;

        // 检查区域是否在垂直方向上重叠
        return (region1Top <= region2Bottom && region1Bottom >= region2Top);
    }

    private async Task ArtifactSalvage()
    {
        if (!_taskParam.AutoArtifactSalvage)
        {
            return;
        }

        if (!int.TryParse(_taskParam.MaxArtifactStar, out var star))
        {
            star = 4;
        }

        await new AutoArtifactSalvageTask(new AutoArtifactSalvageTaskParam(star, javaScript: null, artifactSetFilter: null, maxNumToCheck: null, recognitionFailurePolicy: null)).Start(_ct);
    }
    
    public static (bool, int) PressUseResin(ImageRegion ra, string resinName, string logPrefix = "自动秘境")
    {
        var regionList = ra.FindMulti(RecognitionObject.Ocr(ra.Width * 0.25, ra.Height * 0.2, ra.Width * 0.5, ra.Height * 0.6));
        return PressUseResin(regionList, resinName, logPrefix);
    }

    public static (bool, int) PressUseResin(List<Region> regionList, string resinName, string logPrefix = "自动秘境")
    {
        var resinKey = regionList.FirstOrDefault(t => t.Text.Contains(resinName));
        if (resinKey != null)
        {
            // 找到树脂名称对应的按键，关键词为使用，是同一行的（高度相交）
            var useList = regionList.Where(t => t.Text.Contains("使用")).ToList();
            if (useList.Count != 0)
            {
                // 找到使用按键
                var useKey = useList.FirstOrDefault(t => t.X > TaskContext.Instance().SystemInfo.ScaleMax1080PCaptureRect.Width / 2
                                                         && IsHeightOverlap(t, resinKey));
                if (useKey != null)
                {
                    // 点击使用
                    useKey.Click();
                    // 解决水龙王按下左键后没松开，然后后续点击按下就没反应了。使用双击
                    Sleep(60);
                    useKey.Click();
                    var num = GetResinNum(resinKey, resinName, logPrefix);
                    Logger.LogInformation("{LogPrefix}：使用 {ResinName}, 数量：{Num}", logPrefix, resinName, num);
                    return (true, num);
                }
                else
                {
                    Logger.LogWarning("{LogPrefix}：未找到 {ResinName} 的使用按键", logPrefix, resinName);
                }
            }
            else
            {
                Logger.LogWarning("{LogPrefix}：未找到 {ResinName} 的使用按键", logPrefix, resinName);
            }
        }

        return (false, 0);
    }

     private bool SwitchOriginalResinType(int expectedNum, CancellationToken ct)
    {
        return NewRetry.Do(() =>
        {
            using var ra0 = CaptureToRectArea();
            var regionList = ra0.FindMulti(RecognitionObject.Ocr(ra0.Width * 0.25, ra0.Height * 0.2, ra0.Width * 0.5, ra0.Height * 0.6));
            var has20 = regionList.Any(t => t.Text.Contains("20"));
            var has40 = regionList.Any(t => t.Text.Contains("40"));
            if (expectedNum == 20 && has20)
            {
                Logger.LogInformation("自动秘境：已切换到使用20原粹树脂");
                return true;
            }

            if (expectedNum == 40 && has40)
            {
                Logger.LogInformation("自动秘境：已切换到使用40原粹树脂");
                return true;
            }

            //切换20/40原粹树脂的按钮是亮的
            var clickable = ra0.Find(RecognitionAssets.Get("AutoDomain", "ResinSwitchBtn", ra0.Width, ra0.Height));
            if (clickable.IsExist())
            {
                Logger.LogDebug("自动秘境：切换原粹树脂使用数量");
                clickable.Click();
            }

            //切换20/40原粹树脂的按钮是暗的
            var disabled = ra0.Find(RecognitionAssets.Get("AutoDomain", "ResinSwitchBtnNoActive", ra0.Width, ra0.Height));
            if (disabled.IsExist())
            {
                Logger.LogWarning("自动秘境：切换原粹树脂的使用数量失败，可能是体力不足，当前目标：{Num}", expectedNum);
                return false; // 不可点击  
            }

            throw new RetryException("未检测到按钮"); // 继续重试  
        }, TimeSpan.FromMilliseconds(500), 10);
    }

    private static int GetResinNum(Region region, string resinName, string logPrefix)
    {
        if (resinName == "原粹树脂")
        {
            if (region.Text.Contains("20"))
            {
                return 20;
            }
            else if (region.Text.Contains("40"))
            {
                return 40;
            }
            else
            {
                Logger.LogWarning("自动秘境：未识别到原粹树脂消耗体力数量，默认按20计算");
                return 20;
            }
        }
        else if (resinName == "浓缩树脂" || resinName == "脆弱树脂" || resinName == "须臾树脂")
        {
            return 1;
        }
        else
        {
            throw new ArgumentException("未知的树脂名称");
        }
    }
    
    public static (bool, int) PressUseResin(ImageRegion ra, string resinName)
    {
        var regionList = ra.FindMulti(RecognitionObject.Ocr(ra.Width * 0.25, ra.Height * 0.2, ra.Width * 0.5, ra.Height * 0.6));
        return PressUseResin(regionList, resinName);
    }
}