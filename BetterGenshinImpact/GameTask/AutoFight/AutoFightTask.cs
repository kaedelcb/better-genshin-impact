using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.GameTask.Common.Job;
using OpenCvSharp;
using BetterGenshinImpact.Helpers;
using Vanara;
using Microsoft.Extensions.DependencyInjection;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.View.Drawable;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Vanara.PInvoke;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPick.Assets;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using System.Text.RegularExpressions;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.GameTask.AutoPathing;

namespace BetterGenshinImpact.GameTask.AutoFight;

public class AutoFightTask : ISoloTask
{
    public string Name => "自动战斗";

    private readonly AutoFightParam _taskParam;

    private readonly CombatScriptBag _combatScriptBag;
    
    private readonly CombatScriptBag _combatScriptBagSecond;

    private CancellationToken _ct;

    private readonly BgiYoloPredictor _predictor;

    private DateTime _lastFightFlagTime = DateTime.UtcNow; // 战斗标志最近一次出现的时间

    private readonly double _dpi = TaskContext.Instance().DpiScale;

    private static OtherConfig Config { get; set; } = TaskContext.Instance().Config.OtherConfig;
    
    private static AutoFightConfig FightConfig { get; set; } = TaskContext.Instance().Config.AutoFightConfig;
    
    public static bool FightStatusFlag = false;
    
    public static int SwitchTryCount = 0;
    
    public static volatile  bool FightEndFlag = false;
    
    private static volatile bool _isExperiencePickup = false;

    public static bool IsTpForRecover {get; set;} = false;
    
    public static volatile  bool FightEndTotoly = false;

    // 战斗点位
    public static WaypointForTrack? FightWaypoint  {get; set;} = null;
    
    private static readonly object PickLock = new object(); 
    
    private static readonly object ZLock = new object(); 
    
    private readonly double _assetScale = TaskContext.Instance().SystemInfo.AssetScale;
    
    private readonly ReturnMainUiTask _returnMainUiTask = new();
    
    private class TaskFightFinishDetectConfig
    {
        public int DelayTime = 1500;
        public int DetectDelayTime = 450;
        public int FastCheckDelay = 150;
        public Dictionary<string, int> DelayTimes = new();
        public double CheckTime = 5;
        public List<string> CheckNames = new();
        public bool FastCheckEnabled;
        public bool RotateFindEnemyEnabled = false;

        public TaskFightFinishDetectConfig(AutoFightParam.FightFinishDetectConfig finishDetectConfig)
        {
            FastCheckEnabled = finishDetectConfig.FastCheckEnabled;
            ParseCheckTimeString(finishDetectConfig.FastCheckParams, out CheckTime, CheckNames);
            ParseFastCheckEndDelayString(finishDetectConfig.CheckEndDelay, out DelayTime, DelayTimes);
            BattleEndProgressBarColor =
                ParseStringToTuple(finishDetectConfig.BattleEndProgressBarColor, (95, 235, 255));
            BattleEndProgressBarColorTolerance =
                ParseSingleOrCommaSeparated(finishDetectConfig.BattleEndProgressBarColorTolerance, (6, 6, 6));
            DetectDelayTime = (int)((double.TryParse(finishDetectConfig.BeforeDetectDelay, out var result) ? result : 0.45) * 1000);
            FastCheckDelay = (int)Math.Round(finishDetectConfig.FastCheckDelay * 1000);
            RotateFindEnemyEnabled = finishDetectConfig.RotateFindEnemyEnabled;
        }

        public (int, int, int) BattleEndProgressBarColor { get; }
        public (int, int, int) BattleEndProgressBarColorTolerance { get; }

        public static void ParseCheckTimeString(
            string input,
            out double checkTime,
            List<string> names)
        {
            checkTime = 5;
            if (string.IsNullOrEmpty(input))
            {
                return; // 直接返回
            }

            var uniqueNames = new HashSet<string>(); // 用于临时去重的集合

            // 按分号分割字符串
            var segments = input.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                var trimmedSegment = segment.Trim();

                // 如果是纯数字部分
                if (double.TryParse(trimmedSegment, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double number))
                {
                    checkTime = number; // 更新 CheckTime
                }
                else if (!uniqueNames.Contains(trimmedSegment)) // 如果是非数字且不重复
                {
                    uniqueNames.Add(trimmedSegment); // 添加到集合
                }
            }

            names.AddRange(uniqueNames); // 将集合转换为列表
        }

        public static void ParseFastCheckEndDelayString(
            string input,
            out int delayTime,
            Dictionary<string, int> nameDelayMap)
        {
            delayTime = 1500;

            if (string.IsNullOrEmpty(input))
            {
                return; // 直接返回
            }

            // 分割字符串，以分号为分隔符
            var segments = input.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                var parts = segment.Split(',');

                // 如果是纯数字部分
                if (parts.Length == 1)
                {
                    if (double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                            out double number))
                    {
                        delayTime = (int)(number * 1000); // 更新 delayTime
                    }
                }
                // 如果是名字,数字格式
                else if (parts.Length == 2)
                {
                    string name = parts[0].Trim();
                    if (double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                            out double value))
                    {
                        nameDelayMap[name] = (int)(value * 1000); // 更新字典，取最后一个值
                    }
                }
                // 其他格式，跳过不处理
            }
        }


        static bool IsSingleNumber(string input, out int result)
        {
            return int.TryParse(input, out result);
        }

        static (int, int, int) ParseSingleOrCommaSeparated(string input, (int, int, int) defaultValue)
        {
            // 如果是单个数字
            if (IsSingleNumber(input, out var singleNumber))
            {
                return (singleNumber, singleNumber, singleNumber);
            }

            return ParseStringToTuple(input, defaultValue);
        }

        static (int, int, int) ParseStringToTuple(string input, (int, int, int) defaultValue)
        {
            // 尝试按逗号分割字符串
            var parts = input.Split(',');
            if (parts.Length == 3 &&
                int.TryParse(parts[0], out var num1) &&
                int.TryParse(parts[1], out var num2) &&
                int.TryParse(parts[2], out var num3))
            {
                return (num1, num2, num3);
            }

            // 如果解析失败，返回默认值
            return defaultValue;
        }
    }

    private TaskFightFinishDetectConfig _finishDetectConfig;

    public AutoFightTask(AutoFightParam taskParam)
    {
        _taskParam = taskParam;
        
        var combatScriptBagAll = CombatScriptParser.ReadAndParse(_taskParam.CombatStrategyPath);
        
        _combatScriptBagSecond= combatScriptBagAll;
        
        #region 指定国家战斗脚本解析

        var isAutoSelectTeam = FightConfig.StrategyName.Contains("根据队伍自动选择");
        
        var isSelectAuto = _taskParam.CountryName.Contains("自动");
        
        if (isAutoSelectTeam)
        {
            var countryNamesList = FightConfig.CountryNamesList;
            
            // 对combatScriptBagAll进行重新排序，把含国家脚步名称排后面
            combatScriptBagAll.CombatScripts = combatScriptBagAll.CombatScripts
                .OrderBy(script => countryNamesList.Any(country => script.Name.Contains(country)))
                .ThenBy(script => countryNamesList.FirstOrDefault(country => script.Name.Contains(country)) ?? "")
                .ToList();
            
            var filteredCombatScripts = combatScriptBagAll.CombatScripts
                .Where(script => 
                    _taskParam.CountryName.Length >= 2 
                        ? _taskParam.CountryName.All(country => country != null && script.Name.Contains(country))
                        : _taskParam.CountryName.Any(country => country != null && script.Name.Contains(country)))
                .ToList();
            
            if (filteredCombatScripts.Count == 0)
            {
                //可能在 _taskParam.CountryName.Length >= 2 可能是因为没有符合条件的脚本，尝试Any
                filteredCombatScripts = combatScriptBagAll.CombatScripts
                    .Where(script => _taskParam.CountryName.Any(country => country != "精英" && country != "小怪" && script.Name.Contains(country)))
                    .ToList();
                if (filteredCombatScripts.Count == 0)
                {
                    filteredCombatScripts = combatScriptBagAll.CombatScripts
                        .Where(script => _taskParam.CountryName.Any(country => country != null && script.Name.Contains(country)))
                        .ToList();
                }
            }
            
            // 如果没有找到对应国家的脚本，则使用所有脚本
            if (filteredCombatScripts.Count == 0 && isAutoSelectTeam && isSelectAuto)
            {
                TaskControl.Logger.LogWarning("没有找到符合 {CountryName} 的战斗脚本，将使用所有策略进行匹配", string.Join(", ", _taskParam.CountryName));
                filteredCombatScripts = combatScriptBagAll.CombatScripts;
            }
            
            var combatScriptBagByCountry = new CombatScriptBag(filteredCombatScripts.Count == 0 ?combatScriptBagAll.CombatScripts : filteredCombatScripts);
            
            _combatScriptBag = isSelectAuto || combatScriptBagAll.CombatScripts.Count <= 1 ? combatScriptBagAll : combatScriptBagByCountry;
            
        }
        #endregion

        else
        {
            _combatScriptBag = combatScriptBagAll;
        }

        if (_taskParam.FightFinishDetectEnabled)
        {
            _predictor = App.ServiceProvider.GetRequiredService<BgiOnnxFactory>().CreateYoloPredictor(BgiOnnxModel.BgiWorld);
        }

        _finishDetectConfig = new TaskFightFinishDetectConfig(_taskParam.FinishDetectConfig);
        
    }
    public CombatScenes GetCombatScenesWithRetry()
    {
        const int maxRetries = 5;
        var retryDelayMs = 1000; // 可选：重试间隔，单位毫秒

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            using var ra = CaptureToRectArea();
            var combatScenes = new CombatScenes().InitializeTeam(ra);
            if (combatScenes.CheckTeamInitialized())
            {
                return combatScenes;
            }
        
            if (attempt < maxRetries)
            {
                Thread.Sleep(retryDelayMs); // 可选：延迟再试
            }
        }

        if (!Config.CustomAvatarConfigOut.CustomAvatarEnabled) throw new Exception("识别队伍角色失败（已重试 5 次）");
        
        return new CombatScenes().InitializeTeamForced(Config.CustomAvatarConfigOut.CustomAvatarForceUseList);
    }
    // 方法1：判断是否是单个数字

    /*public int delayTime=1500;
    public Dictionary<string, int> delayTimes = new();
    public double checkTime = 5;
    public List<string> checkNames = new();*/
    public async Task Start(CancellationToken ct)
    {
        _ct = ct;

        LogScreenResolution();
        
        var combatScenes = GetCombatScenesWithRetry();
        
        if (_taskParam.AutoCombatEq && PathingConditionConfig.CombatScenesGoBackUp is not null && 
            PathingConditionConfig.CombatScenesGoBackUp.Avatars.Select(avatar => avatar.Name).ToArray()
                .SequenceEqual(combatScenes.Avatars.Select(a => a.Name).ToArray()))
        {
            Logger.LogInformation("自动战斗：继承地图追踪队伍Cd信息...");
            combatScenes = PathingConditionConfig.CombatScenesGoBackUp;
        }
        
        /*var combatScenes = new CombatScenes().InitializeTeam(CaptureToRectArea());
        if (!combatScenes.CheckTeamInitialized())
        {
            throw new Exception("识别队伍角色失败");
        }*/

        // var actionSchedulerByCd = ParseStringToDictionary(_taskParam.ActionSchedulerByCd);
    var combatCommands = _combatScriptBag.FindCombatScript(combatScenes.GetAvatars(),
        FightConfig.StrategyName.Contains("根据队伍自动选择")) ??
                         _combatScriptBagSecond.FindCombatScript(combatScenes.GetAvatars());
        
        var bandList = (_taskParam.AutoCombatEq && !string.IsNullOrWhiteSpace(_taskParam.UseEqList))
            ? _taskParam.UseEqList.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(s => s.Contains("C", StringComparison.OrdinalIgnoreCase)) // 检查是否包含'C'
                .Select(s => int.TryParse(s.TrimEnd('C'), out var n) ? n : 0) // 去掉'C'并尝试解析数字
                .Where(n => n >= 1 && n <= combatScenes.GetAvatars().Count) // 保证序号在队伍
                .ToList()
            : new List<int>(); // 如果没有指定AutoCombatEq，默认情况下bandList为空

        var bandAvatarsName = _taskParam.AutoCombatEq ? combatScenes.GetAvatars().Where(a => bandList.Contains(a.Index)).Select(a => a.Name).ToList() : new List<string>();
        // Logger.LogError("当前禁用角色：{CombatScriptName}", bandAvatarsName);
        
        // 命令用到的角色名 筛选交集
        var commandAvatarNames = combatCommands.Select(c => c.Name).Distinct()
            .Select(n => combatScenes.SelectAvatar(n)?.Name)
            .WhereNotNull().ToList();
        commandAvatarNames = commandAvatarNames.Except(bandAvatarsName).ToList();
        
        // 过滤不可执行的脚本，Task里并不支持"当前角色"。
        combatCommands = combatCommands 
            .Where(c => commandAvatarNames.Contains(c.Name))
            .ToList();
        
        if (commandAvatarNames.Count <= 0)
        {
            throw new Exception("没有可用战斗脚本");
        }

        // 新的取消token
        var cts2 = new CancellationTokenSource();
        ct.Register(cts2.Cancel);

        combatScenes.BeforeTask(cts2.Token);
        TimeSpan fightTimeout = TimeSpan.FromSeconds(_taskParam.Timeout); // 战斗超时时间
        Stopwatch timeoutStopwatch = Stopwatch.StartNew();

        Stopwatch checkFightFinishStopwatch = Stopwatch.StartNew();
        TimeSpan checkFightFinishTime = TimeSpan.FromSeconds(_finishDetectConfig.CheckTime); //检查战斗超时时间的超时时间


        //战斗前检查，可做成配置
        // if (await CheckFightFinish()) {
        //     return;
        // }
        // var FightEndFlag = false;
        FightEndFlag = false;
        SwitchTryCount = 0;
        var fightEndFlag = false;
        var timeOutFlag = false;
        string lastFightName = "";

        //统计切换人打架次数
        var countFight = 0;
        
        // 可以跳过的角色名,配置中有的和命令中有的取交
        var canBeSkippedAvatarNames = combatScenes.UpdateActionSchedulerByCd(_taskParam.ActionSchedulerByCd)
            .Where(s => commandAvatarNames.Contains(s)).WhereNotNull().ToList();
        
        //所有角色是否都可被跳过
        var allCanBeSkipped = commandAvatarNames.All(a => canBeSkippedAvatarNames.Contains(a));
        
        var delayTime = _finishDetectConfig.DelayTime;
        var detectDelayTime = _taskParam.FinishDetectConfig.EndModel ? _finishDetectConfig.FastCheckDelay : _finishDetectConfig.DetectDelayTime;

        Avatar? guardianAvatar = null;
        if (!string.IsNullOrWhiteSpace(_taskParam.GuardianAvatar))
        {
            // Logger.LogInformation("盾奶优先功能角色预处理开始..{aq}-{aa}.",_taskParam.GuardianAvatar,combatScenes.GetAvatars().Count);
            if (int.Parse(_taskParam.GuardianAvatar) <= combatScenes.GetAvatars().Count) //确保序号在队伍内
            {
                guardianAvatar = combatScenes.SelectAvatar(int.Parse(_taskParam.GuardianAvatar));
            }
            else
            {
                Logger.LogWarning("盾奶优先功能角色预处理失败，请检查盾奶优先功能角色配置是否正确。");
                if (combatScenes.SelectAvatar(_taskParam.GuardianAvatar) is not null)
                {
                    guardianAvatar = combatScenes.SelectAvatar(int.Parse(_taskParam.GuardianAvatar));
                }
            }
        }

        AutoFightSeek.RotationCount= 0; // 重置旋转次数

        ImageRegion image = null;

        var useEqList = (_taskParam.AutoCombatEq && !string.IsNullOrWhiteSpace(_taskParam.UseEqList))
            ? _taskParam.UseEqList.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var n) ? n : 0)
                .Where(n => n >= 1 && n <= combatScenes.GetAvatars().Count) // 保证序号在队伍
                .ToList()
            : new List<int> { 1, 2, 3, 4 }
                .Where(n => n >= 1 && n <= combatScenes.GetAvatars().Count) // 添加此行以处理默认值
                .ToList();
        
        var useSkillList = new List<int>();
        var useSkillListWithH = new List<int>();
        var useSkillListWithF = 0;
        var useSkillListWithA = new Dictionary<int, int>();

        if (_taskParam.AutoCombatEq && !string.IsNullOrWhiteSpace(_taskParam.UseSkillList))
        {
            var skillParts = _taskParam.UseSkillList.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in skillParts)
            {
                var trimmedPart = part.Trim();
                // 使用正则表达式移除A及其后面的括号和内容
                var skillNumberStr = trimmedPart.Replace("H", "").Replace("F", "").Trim();
                var match = Regex.Match(skillNumberStr, @"(\d+)A(\(\d+\))?");
                skillNumberStr = System.Text.RegularExpressions.Regex.Replace(skillNumberStr, @"A\(\d+\)|A", "");
                
                if (match.Success)
                {
                    // 提取以A结尾的数字前面的数字
                    if (int.TryParse(match.Groups[1].Value, out int skillNumber2))
                    {
                        // 提取括号中的数字，如果存在的话
                        if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value.Trim('(', ')'), out int value))
                        {
                            useSkillListWithA.Add(skillNumber2, value);
                        }
                        else
                        {
                            useSkillListWithA.Add(skillNumber2,600);
                        }
                    }
                }
                
                var skillNumber = int.TryParse(skillNumberStr, out var n) ? n : 0;

                if (skillNumber >= 1 && skillNumber <= combatScenes.GetAvatars().Count) //保证序号在队伍
                {
                    useSkillList.Add(skillNumber); // 添加到全部技能列表

                    if (trimmedPart.Contains('H'))
                    {
                        useSkillListWithH.Add(skillNumber); // 添加到带H的技能列表
                    }
                    if (trimmedPart.Contains('F') && useSkillListWithF == 0) // 只记录第一个F
                    {
                        useSkillListWithF = skillNumber; // 记录第一个带F的技能序号
                    }
                }
            }
            foreach (var kvp in useSkillListWithA)
            {
                Logger.LogError($"{{ {kvp.Key}, {kvp.Value} }}");
            }
        }
        else
        {
            useSkillList = new List<int> { 1, 2, 3, 4 };
            useSkillListWithH = new List<int>();
            // useSkillListWithF = 0;
        }

        var predefinedlist = new List<string>() { "枫原万叶" ,"希诺宁"};
        
        //旋转次数
        var rotationLimit = _taskParam.RotaryFactor == 1 ? 500 : _taskParam.FinishDetectConfig.RotationMode ? 50 : 6;
        
        // 战斗操作
        var fightTask = Task.Run(async () =>
        {
            #region 基于战斗检测经验值开关万叶拾取功能同步任务
            
            if (_taskParam.ExpKazuhaPickup) FindExp(cts2.Token);
            
            #endregion
            
            #region 自动吃药功能同步任务

            if (_taskParam.TakeMedicineEnabled)
            {
                IsTpForRecover = true;
                TakeMedicine(cts2.Token);
            }
            else
            {
                IsTpForRecover = false;
            }
            
            #endregion
            
            try
            {
                FightStatusFlag = true;
                FightEndTotoly = false;
                
                while (!cts2.Token.IsCancellationRequested && !FightEndTotoly)
                {
                   // 所有战斗角色都可以被取消
                    #region 本次战斗的跳过战斗判定

                    //如果所有角色都可以被跳过，且没有任何一个cd大于0的(技能都还没好)
                    //则强制等待，因为不等待的话什么都不能做，而且会造成刷屏
                    if (allCanBeSkipped)
                    {
                        //获取最低cd
                        var minCoolDown = commandAvatarNames.Select(a => combatScenes.SelectAvatar(a)).WhereNotNull()
                            .Select(a => a.GetSkillCdSeconds()).Min();
                        if (minCoolDown > 0)
                        {
                            TaskControl.Logger.LogInformation("队伍中所有角色的技能都在冷却中,等待{MinCoolDown}秒后继续。", Math.Round(minCoolDown, 2));
                            await Delay((int)Math.Ceiling(minCoolDown * 1000), cts2.Token);
                        }
                    }

                    var skipFightName = "";

                    #endregion
                    
                    for (var i = 0; i < combatCommands.Count; i++)
                    {
                        var command = combatCommands[i];
                        var lastCommand = i == 0 ? command : combatCommands[i - 1];
                        
                        #region 盾奶位技能优先和自动EQ功能
                        
                        var skipModel = guardianAvatar != null && lastFightName != command.Name;
                        
                        if (skipModel && guardianAvatar is not null) {
                            
                            image = CaptureToRectArea();
                            
                            await AutoFightSkill.EnsureGuardianSkill(guardianAvatar,lastCommand,lastFightName,
                            _taskParam.GuardianAvatar,_taskParam.GuardianAvatarHold,5,cts2.Token,_taskParam.GuardianCombatSkip,_taskParam.BurstEnabled);
                            
                            if (_taskParam.AutoCombatEq && guardianAvatar.ManualSkillCd == 0 && !cts2.Token.IsCancellationRequested)
                            {
                                if (timeoutStopwatch.Elapsed > fightTimeout)
                                {
                                    fightEndFlag = true;
                                    timeOutFlag = true;
                                    break;
                                }

                                if(i>0)i--;
                                continue;     
                                
                            }

                            if (_taskParam.AutoCombatEq)
                            {
                                var useEq = new List<int>();
                                for (var h = 1; h <= combatScenes.GetAvatars().Count; h++)
                                {
                                    if (!combatScenes.SelectAvatar(h).IsActive(image))
                                    {
                                        continue;
                                    }

                                    try
                                    {
                                        useEq = await AutoFightSkill.AvatarQSkillAsync(image, useEqList, h);
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.LogError("自动EQ战斗：角色 {name} 识别异常 {ex}", h, ex.Message);
                                        fightEndFlag = true;
                                        throw;
                                    }
                                    
                                    break;
                                }
                                
                                if (useSkillListWithF>0 && combatScenes.SelectAvatar(useSkillListWithF).IsSkillReady()) //自定义序号首位先放E，只执行一次
                                {
                                    Logger.LogInformation("自动EQ战斗：执行序号 {name} 首E技能", useSkillListWithF);
                                    var avatarFirst = combatScenes.SelectAvatar(useSkillListWithF);
                                    
                                    // if (Bv.GetMotionStatus(image) == MotionStatus.Fly)
                                    // {
                                    //     Logger.LogWarning("自动EQ战斗：进入飞行状态，尝试下落攻击");
                                    //     Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                                    // }
                                
                                    // 先尝试切换角色，成功再单独 await 技能执行结果
                                    if (avatarFirst.TrySwitch(15))
                                    {
                                        var skillSucceeded = await AutoFightSkill.AvatarSkillAsync(Logger, avatarFirst, false, 1, cts2.Token);
                                        if (!skillSucceeded)
                                        {
                                            // 原有在条件不满足时的处理逻辑
                                            avatarFirst.UseSkill(useSkillListWithH.Contains(useSkillListWithF), 1);
                                            var useA = useSkillListWithA.ContainsKey(useSkillListWithF) && useSkillListWithA[useSkillListWithF] > 0;
                                            if (useA)
                                            {
                                                Logger.LogInformation("自动EQ战斗：执行序号 {name} 角色首E技能后普攻 {time} ms", useSkillListWithF, useSkillListWithA[useSkillListWithF]);
                                                avatarFirst.Attack(useSkillListWithA[useSkillListWithF]);
                                            }
                                        }
                                    }
                                    useSkillListWithF = 0;
                                }

                                if (useEq.Count > 0)
                                {
                                    foreach (var num in useEq) 
                                    {
                                        Logger.LogInformation("自动EQ战斗：使用序号 {name} 角色技能", num);
                                        var avatarQ = combatScenes.SelectAvatar(num);
                                        var useE = useSkillList.Contains(num);
                                        var avatarQHold = useSkillListWithH.Contains(num);
                                        var usePre = predefinedlist.Contains(avatarQ.Name);
                                        var useAContainsKey = useSkillListWithA.ContainsKey(num);
                                        var useA = (useAContainsKey && useSkillListWithA[num] > 0) || usePre;
                                        if (avatarQ.TrySwitch(15))
                                        {
                                            lastFightName = avatarQ.Name;
                                            countFight++;
                                            if (useE && !await AutoFightSkill.AvatarSkillAsync(Logger, avatarQ, false, 1, cts2.Token))
                                            {
                                                avatarQ.UseSkill(avatarQHold);
                                                if (useA)
                                                {
                                                    if (!useAContainsKey)
                                                    {
                                                        useSkillListWithA.Add(num,avatarQHold?700:600);
                                                    }
                                                    Logger.LogInformation("自动EQ战斗：执行序号 {name} 角色普攻 {time} ms", num, useSkillListWithA[num]);
                                                    avatarQ.Attack(useSkillListWithA[num]); 
                                                }
                                                
                                                var imageAfterUseSkill = CaptureToRectArea();
                                                var retry = 30;
                                                try
                                                {
                                                    while (!await AutoFightSkill.AvatarSkillAsync(Logger, avatarQ,
                                                               false, 1, cts2.Token, imageAfterUseSkill) && retry > 0)
                                                    {
                                                        Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                                                        Simulation.ReleaseAllKey();

                                                        // 防止在纳塔飞天或爬墙
                                                        if (retry % 4 == 0)
                                                        {
                                                            Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                                                            Simulation.SendInput.SimulateAction(GIActions.Drop);
                                                        }

                                                        // 释放旧的截图资源
                                                        imageAfterUseSkill.Dispose();

                                                        // 获取新的截图
                                                        imageAfterUseSkill = CaptureToRectArea();

                                                        await Task.Delay(30, cts2.Token);
                                                        retry -= 1;
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    Logger.LogError("自动EQ战斗：角色 {name} 释放技能异常 {ex}", avatarQ.Name, ex.Message);
                                                    fightEndFlag = true;
                                                    throw;
                                                }
                                                finally
                                                {
                                                    // 确保最终释放资源
                                                    imageAfterUseSkill.Dispose();
                                                }
                                            }
                                            
                                            fightEndFlag = await CheckFightFinish(0, detectDelayTime, cts2.Token,avatarQ);
                                            if (!fightEndFlag)
                                            { 
                                                Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
                                                var imageAfterBurst = CaptureToRectArea();
                                                var ms = 30; // 初始化计数器

                                                try
                                                {
                                                    while (imageAfterBurst.Find(ElementAssets.Instance.PaimonMenuRo).IsExist() && ms > 0)
                                                    {
                                                        var skillSucceeded = await AutoFightSkill.AvatarSkillAsync(Logger, avatarQ, true, 1, cts2.Token, imageAfterBurst, false);

                                                        if (skillSucceeded)
                                                        {
                                                            break;
                                                        }

                                                        // 原逻辑：触发一次大招并等待，再更新截图重试
                                                        Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
                                                        await Task.Delay(50, cts2.Token);

                                                        imageAfterBurst.Dispose();
                                                        imageAfterBurst = CaptureToRectArea();

                                                        ms -= 1;
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    Logger.LogError("自动EQ战斗：角色 {name} 释放技能异常 {ex}", avatarQ.Name, ex.Message);
                                                    fightEndFlag = true;
                                                    throw;
                                                }
                                                finally
                                                {
                                                    // 确保最终释放资源
                                                    imageAfterBurst.Dispose();
                                                }

                                            }
                                            else
                                            {
                                                break;
                                            }
                                            if (guardianAvatar.IsSkillReady())
                                            {
                                                break;
                                            }
                                        }
                                    }
                                }
                                useEq.Clear(); 
                                if (guardianAvatar.IsSkillReady() && !cts2.Token.IsCancellationRequested)
                                {
                                    if(i>0)i--;
                                    continue;
                                }
                            }
                            image.Dispose();
                        }
                        
                        if (fightEndFlag)break;
                        
                        var avatar = combatScenes.SelectAvatar(command.Name);
                        
                        #endregion
                        
                        #region 初始寻敌处理

                        if ( _finishDetectConfig.RotateFindEnemyEnabled && i == 0 && _taskParam.IsFirstCheck)
                        {
                            try
                            {
                                await AutoFightSeek.SeekAndFightAsync(TaskControl.Logger, detectDelayTime, delayTime,
                                    cts2.Token, true, _taskParam.RotaryFactor,avatar,_taskParam.FinishDetectConfig.GoDistance);
                            }
                            catch (Exception ex)
                            {
                                fightEndFlag = true;
                                Logger.LogError("初始寻敌异常 {ex}", ex.Message);
                                throw;
                            }
                        }
                        
                        #endregion
                        
                        if (avatar is null || (avatar.Name == guardianAvatar?.Name && (_taskParam.GuardianCombatSkip || _taskParam.BurstEnabled)))
                        {
                            Logger.LogDebug("跳过角色{command.Name} - {avatar.Name}", command.Name,avatar?.Name);
                            continue;
                        }
                        if (_taskParam.AutoCombatEq)avatar?.TrySwitch(15);
                        #region 每个命令的跳过战斗判定

                        // 判断是否满足跳过条件:
                        // 1.上一次成功执行命令的最后执行角色不是这次的执行角色
                        // 2.这次执行的角色包含在可跳过的角色列表中
                        if (!
                                //上次命令的执行角色和这次相同
                                (lastFightName == command.Name &&
                                 // 且未跳过(成功执行)了,则不进行跳过判定
                                 skipFightName == "")
                            &&
                            // 且这次执行的角色包含在可跳过的角色列表中
                            (allCanBeSkipped || canBeSkippedAvatarNames.Contains(command.Name))
                           )
                        {
                            var cd = avatar.GetSkillCdSeconds();
                            if (cd > 0)
                            {
                                // 如果上一次该角色已经被跳过，则不进行log输出，以免刷屏
                                if (skipFightName != command.Name)
                                {
                                    var manualSkillCd = avatar.ManualSkillCd;
                                    if (manualSkillCd > 0)
                                    {
                                        TaskControl.Logger.LogInformation("{commandName}cd冷却为{skillCd}秒,剩余{Cd}秒,跳过此次行动",
                                            command.Name,
                                            manualSkillCd, Math.Round(cd, 2));
                                    }
                                    else
                                    {
                                        TaskControl.Logger.LogInformation("{CommandName}cd冷却剩余{Cd}秒,跳过此次行动", command.Name,
                                            Math.Round(cd, 2));
                                    }
                                }

                                // 避免重复log提示
                                skipFightName = command.Name;
                                continue;
                            }

                            // 表示这次执行命令没有跳过
                            skipFightName = "";
                        }

                        #endregion
                        
                        if (timeoutStopwatch.Elapsed > fightTimeout || AutoFightSeek.RotationCount >= rotationLimit)
                        {
                            TaskControl.Logger.LogInformation(AutoFightSeek.RotationCount >= rotationLimit ? "旋转次数达到上限，战斗结束" : "战斗超时结束");
                            fightEndFlag = true;
                            timeOutFlag = true;
                            FightEndTotoly  = true;
                            break;
                        }

                        #region Q前寻敌处理
                        if (_finishDetectConfig.RotateFindEnemyEnabled && _taskParam.CheckBeforeBurst && (command.Method == Method.Burst || command.Args.Contains("q") || command.Args.Contains("Q")))
                        {
                            fightEndFlag = await CheckFightFinish(0, detectDelayTime, cts2.Token,avatar);
                        }
                        #endregion

                        command.Execute(combatScenes, lastCommand);
                        //统计战斗人次
                        if (i == combatCommands.Count - 1 || command.Name != combatCommands[i + 1].Name)
                        {
                            countFight++;
                        }

                        #region check动作触发战斗结束检测
                        if (command.Method == Method.Check)
                        {
                            fightEndFlag = await CheckFightFinish(delayTime, detectDelayTime,cts2.Token,avatar);
                        }
                        #endregion

                        lastFightName = command.Name;
                        if (!fightEndFlag && _taskParam is { FightFinishDetectEnabled: true })
                        {
                            //处于最后一个位置，或者当前执行人和下一个人名字不一样的情况，满足一定条件(开启快速检查，并且检查时间大于0或人名存在配置)检查战斗
                            if (i == combatCommands.Count - 1
                                || (
                                    _finishDetectConfig.FastCheckEnabled &&
                                    command.Name != combatCommands[i + 1].Name &&
                                    ((_finishDetectConfig.CheckTime > 0 &&
                                      checkFightFinishStopwatch.Elapsed > checkFightFinishTime)
                                     || _finishDetectConfig.CheckNames.Contains(command.Name))
                                ))
                            {
                                checkFightFinishStopwatch.Restart();
                               
                                if (_finishDetectConfig.DelayTimes.TryGetValue(command.Name, out var time))
                                {
                                    delayTime = time;
                                    // Logger.LogInformation($"{command.Name}结束后，延时检查为{delayTime}毫秒");
                                }
                                else
                                {
                                    // Logger.LogInformation($"延时检查为{delayTime}毫秒");
                                }

                                fightEndFlag = await CheckFightFinish(delayTime, detectDelayTime,cts2.Token,avatar);
                            }
                        }

                        if (fightEndFlag)
                        {
                            break;
                        }
                    }


                    if (fightEndFlag)
                    {
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
                Debug.WriteLine(e.StackTrace);
                throw;
            }
            finally
            {
                Simulation.ReleaseAllKey();
                FightStatusFlag = false;
                image?.Dispose();
                GC.Collect();//释放内存
                GC.WaitForPendingFinalizers();//释放内存
                Dispatcher.IsCustomCts = false;
            }
        }, cts2.Token);

        await fightTask;

        if (_taskParam.KazuhaPickupEnabled && _taskParam.ExpKazuhaPickup && !_isExperiencePickup)
        {
            TaskControl.Logger.LogInformation("基于怪物经验判断：{text} 经验值显示","等待");
            await Delay(1000, ct);
        }
        FightEndFlag = true; 

        if ((_taskParam.BattleThresholdForLoot >= 2 && countFight < _taskParam.BattleThresholdForLoot) && (!_taskParam.ExpKazuhaPickup || !_isExperiencePickup))
        {
            TaskControl.Logger.LogInformation($"战斗人次（{countFight}）低于配置人次（{_taskParam.BattleThresholdForLoot}），跳过此次拾取！");
            
            if (_taskParam.EndBloodCheackEnabled)
            {
                //防止检测战斗结束时，派蒙头冠消失
                using var ra = CaptureToRectArea();
                var pixelValue = ra.SrcMat.At<Vec3b>(32, 67);
                ra.Dispose();
                // 检查每个通道的值是否在允许的范围内
                if (!(Math.Abs(pixelValue[0] - 143) <= 10 &&
                      Math.Abs(pixelValue[1] - 196) <= 10 &&
                      Math.Abs(pixelValue[2] - 233) <= 10))
                {
                    await Delay(_finishDetectConfig.RotateFindEnemyEnabled?2000:1000, ct);
                }
            
                await EndBloodCheck(ct,combatScenes);
            }
            
            return;
        }
      
        if(_taskParam.KazuhaPickupEnabled && _taskParam.ExpKazuhaPickup) TaskControl.Logger.LogInformation("基于怪物经验判断：{text} 万叶拾取", _isExperiencePickup? "执行" : "不执行");
        
        if (_taskParam.KazuhaPickupEnabled && (!_taskParam.ExpKazuhaPickup || _isExperiencePickup))
        {
            // Logger.LogInformation("开始 _isExperiencePickup：{_isExperiencePickup}",_isExperiencePickup);
            // 队伍中存在万叶的时候使用一次长E
            var picker = combatScenes.SelectAvatar("枫原万叶") ?? combatScenes.SelectAvatar("琴");
            
            string? oldPartyName = null;
            if (RunnerContext.Instance.PartyName is not null)
            {
                 oldPartyName = RunnerContext.Instance.PartyName;
            }
            else if(picker is null && !string.IsNullOrEmpty(_taskParam.KazuhaPartyName))
            {
                Logger.LogWarning("换队拾取：当前队伍名称为空，尝试读取");
                await Delay(1000, ct);
                await _returnMainUiTask.Start(ct);

                for( int attempt = 0; attempt < 6; attempt++)
                {
                    Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
                    var enterGameAppear = await NewRetry.WaitForElementAppear(
                        ElementAssets.Instance.PartyBtnChooseView,
                        () => { },
                        ct,
                        15,
                        500
                    );
                    if (enterGameAppear)
                    {
                        Logger.LogInformation("换队拾取：成功打开队伍界面");
                        break;
                    }
                    
                    if(attempt == 5 && !enterGameAppear)
                    {
                        Logger.LogWarning("换队拾取：读取队伍名称失败，跳过换队拾取步骤");
                    }
                }
                
                await Delay(1000, ct);
                
                //等待寻找2秒队伍按钮出现
                var timeWaitStart = 0;
                while(timeWaitStart < 6000)
                {
                    using var ra = CaptureToRectArea();
                    var partyViewBtn = ra.Find(ElementAssets.Instance.PartyBtnChooseView);
                    if (partyViewBtn.IsExist())
                    {
                        // OCR 当前队伍名称（无法单字，中间禁止空格）
                      // 读取OCR原始识别文本
                      var rawPartyName = ra.Find(new RecognitionObject
                      {
                          RecognitionType = RecognitionTypes.Ocr,
                          RegionOfInterest = new Rect(partyViewBtn.Right, partyViewBtn.Top, (int)(350 * _assetScale),
                              partyViewBtn.Height)
                      }).Text;
                      
                      // 核心处理逻辑：1.空值兜底 2.去首尾空白 3.移除末尾的“口”字（仅最后一个是口才删）
                      if (string.IsNullOrWhiteSpace(rawPartyName))
                      {
                          oldPartyName = string.Empty;
                      }
                      else
                      {
                          var tempName = rawPartyName
                              .Replace("\"", "")        // 移除所有双引号（核心新增，解决日志里的""问题）
                              .Replace("\r\n", "")      // 清理Windows换行符
                              .Replace("\r", "");   // 先清理所有双引号，避免引号干扰后续处理
                              
                              // 核心逻辑：找到第一个换行符(\n)的位置，截断并删除换行+后面所有字符
                              int firstNewLineIndex = tempName.IndexOf('\n');
                              if (firstNewLineIndex != -1) // 存在换行符，截取到换行符前
                              {
                                  tempName = tempName.Substring(0, firstNewLineIndex);
                              }
                          
                              // 最后统一去首尾所有空白（空格、制表符、回车符\r等），得到纯净队伍名
                              oldPartyName = tempName.Trim();
                      }
                      
                      // 后续原有逻辑不变
                      Logger.LogInformation("换队拾取：当前队伍名称读取为：{oldPartyName}", oldPartyName);
                      // 加在rawPartyName赋值后，打印原始文本的“原始形态”（转义符会显示）
                      Logger.LogDebug("OCR原始识别文本（含转义）：{rawPartyName}", rawPartyName);
                      RunnerContext.Instance.PartyName = oldPartyName;
                        // await _returnMainUiTask.Start(ct);
                        break;
                    }
                    await Delay(200, ct);
                    timeWaitStart += 200;
                }
            }
            
            var switchPartyFlag = false;
            if (picker == null && !timeOutFlag &&!string.IsNullOrEmpty(_taskParam.KazuhaPartyName) && oldPartyName != _taskParam.KazuhaPartyName)
            {
                try
                {
                    TaskControl.Logger.LogInformation($"切换为拾取队伍：{_taskParam.KazuhaPartyName}");
                    var success = await new SwitchPartyTask().Start(_taskParam.KazuhaPartyName, ct);
                    if (success)
                    {
                        TaskControl.Logger.LogInformation($"成功切换队伍为{_taskParam.KazuhaPartyName}");
                        switchPartyFlag = true;
                        RunnerContext.Instance.PartyName = _taskParam.KazuhaPartyName;
                        RunnerContext.Instance.ClearCombatScenes();
                        var cs = await RunnerContext.Instance.GetCombatScenes(ct);
                        picker = cs.SelectAvatar("枫原万叶") ?? cs.SelectAvatar("琴");
                    }
                }
                catch (Exception e)
                {
                    TaskControl.Logger.LogInformation("切换队伍异常，跳过此步骤！");
                }

            }
            
            if (picker != null)
            {
                if (picker.Name == "枫原万叶")
                {
                    var time = TimeSpan.FromSeconds(picker.GetSkillCdSeconds());
                    // 如果配置了二次拾取，或者不满足跳过条件（上次是万叶且冷却时间>3秒），则执行拾取
                    bool shouldSkip = lastFightName == picker.Name && time.TotalSeconds > 3;
                    bool forcePickup = _taskParam.QinDoublePickUp;
                    
                    if (forcePickup || !shouldSkip)
                    {
                        TaskControl.Logger.LogInformation("使用 枫原万叶-长E 拾取掉落物");
                        await Delay(200, ct);
                        if (picker.TrySwitch(10))
                        {
                            await Delay(50, ct);
                            if (await AutoFightSkill.AvatarSkillAsync(Logger, picker, false, 1, ct))
                            {
                                await picker.WaitSkillCd(ct);
                            }
                            picker.UseSkill(true);
                            await Delay(50, ct);
                            Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                            await Delay(1500, ct);
                        }
                    }
                    else
                    {
                        TaskControl.Logger.LogInformation("距最近一次万叶出招，时间过短，跳过此次万叶拾取！");
                    }
                }
                else if (picker.Name == "琴")
                {
                    TaskControl.Logger.LogInformation("使用 琴-长E 拾取掉落物");
                    
                    var actionsToUse = PickUpCollectHandler.PickUpActions
                        .Where(action => action.StartsWith("琴-长E" + " ", StringComparison.OrdinalIgnoreCase))
                        .Select(action => action.Replace("琴-长E","琴", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    var find = _taskParam.QinDoublePickUp;
                    await Delay(150, ct);
                    if (picker.TrySwitch(10))
                    {
                        if (await AutoFightSkill.AvatarSkillAsync(Logger, picker, false, 1, ct))//有祭礼情况下可能CD已经好了
                        {
                            await picker.WaitSkillCd(ct);
                        }
                        foreach (var miningActionStr in actionsToUse)
                        {
                            var pickUpAction = CombatScriptParser.ParseContext(miningActionStr);

                            for (int i = 0; i < 2; i++)
                            {
                                foreach (var command in pickUpAction.CombatCommands)
                                {
                                    command.Execute(combatScenes);
                                    //异步执行，防止卡顿
                                    Task.Run(() =>
                                    {
                                        if (Monitor.TryEnter(PickLock))
                                        {
                                            try
                                            {
                                                if (find)
                                                {
                                                    using (var imagePick = CaptureToRectArea())
                                                    {
                                                        if (imagePick.Find(AutoPickAssets.Instance.PickRo).IsExist())
                                                        {
                                                            find = false;
                                                        }
                                                    }
                                                }
                                            }
                                            catch (Exception e)
                                            {
                                                Logger.LogError(e, "琴拾取物品异常");
                                                find = false;
                                                throw;
                                            }
                                            finally
                                            {
                                                GC.Collect();//释放内存
                                                GC.WaitForPendingFinalizers();//释放内存
                                                Monitor.Exit(PickLock);
                                            }
                                        }
                                        // 后面没代码了，不用写return？
                                    });
                                }

                                if (!find)
                                {
                                    break;
                                }

                                if (i == 0)
                                {
                                    Logger.LogInformation("自动拾取；尝试再次执行 琴-长E 拾取");
                                    await picker.WaitSkillCd(ct);
                                }
                                else
                                {
                                    break;
                                }
                            }
                            
                            Simulation.ReleaseAllKey();
                        }
                    }
                }
            }
            //切换过队伍的，需要再切回来
            if (switchPartyFlag && !string.IsNullOrEmpty(oldPartyName))
            {
                try
                {
                    TaskControl.Logger.LogInformation($"切换为原队伍：{oldPartyName}");
                    var success = await new SwitchPartyTask().Start(oldPartyName, ct);
                    if (success)
                    {
                        TaskControl.Logger.LogInformation($"切换为原队伍{oldPartyName}");
                        switchPartyFlag = true;
                        RunnerContext.Instance.PartyName = oldPartyName;
                        RunnerContext.Instance.ClearCombatScenes();
                        await RunnerContext.Instance.GetCombatScenes(ct);
    
                    }
                }
                catch (Exception e)
                {
                    TaskControl.Logger.LogInformation("恢复原队伍失败，跳过此步骤！");
                }
                    
            }
        }
        
        if (_taskParam is { PickDropsAfterFightEnabled: true } )
        {
            // 执行扫描掉落物光柱并靠近的功能
            await new ScanPickTask().Start(ct);
        }

        if (_taskParam.EndBloodCheackEnabled)
        {
            // if(!Bv.IsInBigMapUi(CaptureToRectArea()))
            //防止检测战斗结束时，派蒙头冠消失
            var pixelValue = CaptureToRectArea().SrcMat.At<Vec3b>(32, 67);
            // 检查每个通道的值是否在允许的范围内
            if (!(Math.Abs(pixelValue[0] - 143) <= 10 &&
                  Math.Abs(pixelValue[1] - 196) <= 10 &&
                  Math.Abs(pixelValue[2] - 233) <= 10))
            {
                await Delay(1000, ct);
            }
            
            await EndBloodCheck(ct,combatScenes);
        }
    }

    private void LogScreenResolution()
    {
        AssertUtils.CheckGameResolution("自动战斗");
    }

    static bool AreDifferencesWithinBounds((int, int, int) a, (int, int, int) b, (int, int, int) c)
    {
        // 计算每个位置的差值绝对值并进行比较
        return Math.Abs(a.Item1 - b.Item1) < c.Item1 &&
               Math.Abs(a.Item2 - b.Item2) < c.Item2 &&
               Math.Abs(a.Item3 - b.Item3) < c.Item3;
    }

    public async Task<bool> CheckFightFinish(int delayTime = 1500, int detectDelayTime = 450,CancellationToken ct = default,Avatar? avatar = null)
    {
        using var captureToRectArea = CaptureToRectArea();
        var pixelValue = captureToRectArea.SrcMat.At<Vec3b>(32, 67); 
        var paiMon = (Math.Abs(pixelValue[0] - 143) <= 10 &&
                      Math.Abs(pixelValue[1] - 196) <= 10 &&
                      Math.Abs(pixelValue[2] - 233) <= 10);
        if (!paiMon)
        {
            return false;
        }

        if(Dispatcher.IsCustomCts) return false;
        if (_finishDetectConfig.RotateFindEnemyEnabled)
        {
            bool? result = null;
            try
            {
                if (_taskParam.FinishDetectConfig.RotationMode)
                {
                    Task.Run(async () =>
                    {
                        result = await AutoFightSeek.SeekAndFightAsync(TaskControl.Logger, detectDelayTime, delayTime, ct,false,_taskParam.RotaryFactor,avatar,_taskParam.FinishDetectConfig.GoDistance); 
                        AutoFightSeek.RotationCount = (result == null) ? 
                            AutoFightSeek.RotationCount + 1 :  0;
                    }, ct);  
                    
                }
                else
                {
                    result = await AutoFightSeek.SeekAndFightAsync(TaskControl.Logger, detectDelayTime, delayTime, ct,false,_taskParam.RotaryFactor,avatar,_taskParam.FinishDetectConfig.GoDistance); 
                    AutoFightSeek.RotationCount = (result == null) ? 
                        AutoFightSeek.RotationCount + 1 :  0;
                }
            }
            catch (Exception ex)
            {
                TaskControl.Logger.LogError(ex, "SeekAndFightAsync 方法发生异常");
                return true;
            }
            
            if (result != null)
            {
                return result.Value;
            }
        }

        if (!_finishDetectConfig.RotateFindEnemyEnabled)await Delay(delayTime, _ct);
        
        Logger.LogInformation("打开编队界面检查战斗是否结束");

        Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
        await Delay(detectDelayTime, _ct);

        // await Delay(80, _ct);
        using var ra = CaptureToRectArea();
        Simulation.SendInput.SimulateAction(GIActions.Drop);

        Vec3b pixelValue2;
        var paiMon2 = false;
        if (_taskParam.FinishDetectConfig.EndModel)
        {
            pixelValue2 = ra.SrcMat.At<Vec3b>(32, 67); //派蒙
            paiMon2 = (Math.Abs(pixelValue2[0] - 143) <= 10 &&
                       Math.Abs(pixelValue2[1] - 196) <= 10 &&
                       Math.Abs(pixelValue2[2] - 233) <= 10);
        }
        else
        {
            pixelValue2 = ra.SrcMat.At<Vec3b>(50, 790); //进度条颜色
            var whiteTile = ra.SrcMat.At<Vec3b>(50, 768); //白块
            paiMon2 = !(IsWhite(whiteTile.Item2, whiteTile.Item1, whiteTile.Item0) &&
                       IsYellow(pixelValue2.Item2, pixelValue2.Item1,
                           pixelValue2.Item0));
        }

        var aa = AutoFightSkill.MedicinalCdAsync(Logger, true, 1, ct).Result;
        
        if (!paiMon2 && !aa)
        {
            // Logger.LogWarning("测试3：{t},{t2}",paiMon2,aa);
            TaskControl.Logger.LogInformation("{t}：识别到战斗结束",_taskParam.FinishDetectConfig.EndModel? "快速模式" : "默认模式");
            //取消正在进行的换队
            FightEndTotoly  = true;
            Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
            return true;
        }
        
        if(_taskParam.RotaryFactor != 1) Logger.LogInformation("{t}：未识别到战斗结束",_taskParam.FinishDetectConfig.EndModel? "快速模式" : "默认模式");

        if (_finishDetectConfig.RotateFindEnemyEnabled)
        {
            try
            {
                Task.Run(() =>
                {
                    Scalar bloodLower = new Scalar(255, 90, 90);
                    MoveForwardTask.MoveForwardAsync(bloodLower, bloodLower, TaskControl.Logger, _ct,_taskParam.FinishDetectConfig.GoDistance);
                }, _ct);
            }
            catch (Exception ex)
            {
                TaskControl.Logger.LogError($"任务运行时发生异常: {ex.Message}");
            }
        }
        
        _lastFightFlagTime = DateTime.UtcNow;
        return false;
    }

    bool IsYellow(int r, int g, int b)
    {
        //Logger.LogInformation($"IsYellow({r},{g},{b})");
        // 黄色范围：R高，G高，B低
        return (r >= 200 && r <= 255) &&
               (g >= 200 && g <= 255) &&
               (b >= 0 && b <= 100);
    }

    bool IsWhite(int r, int g, int b)
    {
        //Logger.LogInformation($"IsWhite({r},{g},{b})");
        // 白色范围：R高，G高，B低
        return (r >= 240 && r <= 255) &&
               (g >= 240 && g <= 255) &&
               (b >= 240 && b <= 255);
    }
    
    //基于万叶经验值判断是否拾取
    private static Task FindExp(CancellationToken cts2)
    {
        var autoFightAssets = AutoFightAssets.Instance;

        try  
        {
            Task.Run(() =>
            {
                _isExperiencePickup = false;
                var expLogo = false;
                
                var experienceRas = new[]
                {
                   autoFightAssets.InitializeRecognitionObject(60), 
                   autoFightAssets.InitializeRecognitionObject(58), 
                   autoFightAssets.InitializeRecognitionObject(57),
                };
                
                while (!(_isExperiencePickup || FightEndFlag) && !cts2.IsCancellationRequested)
                {
                    try
                    {
                        cts2.ThrowIfCancellationRequested();

                        var result = NewRetry.WaitForAction(() =>
                        {
                            using (var ra = CaptureToRectArea())
                            {
                                _isExperiencePickup = experienceRas.Any(experienceRa => 
                                {
                                    var isExist = ra.Find(experienceRa);
                                    if (!isExist.IsExist())
                                    {
                                        return false;
                                    }
                
                                    var pixelValue1 = ra.SrcMat.At<Vec3b>(isExist.Y, isExist.X - 147); //经验值图标，在2K以上时匹配度0.6，这个经验值颜色尤为重要
                                    expLogo = pixelValue1[0] == 253 && pixelValue1[1] == 247 && pixelValue1[2] == 172;

                                    return expLogo;
                                });
                            }
                            return _isExperiencePickup;
                        }, cts2, 1, 100).Result;
                    }
                    catch (OperationCanceledException ex)
                    {
                        Console.WriteLine($"检测经验发生异常: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        // Console.WriteLine($"检测怪物经验发生异常: {ex.Message}");
                    }
                    
                    if (_isExperiencePickup) Logger.LogInformation("基于怪物经验判断：识别到 {text1} 经验值，{text2} 万叶拾取","精英","启用" );

                }
                
                cts2.ThrowIfCancellationRequested();
                
            }, cts2); 
        }
        catch (OperationCanceledException ex)
        {
            Console.WriteLine($"检测经验发生异常: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"检测怪物经验发生异常: {ex.Message}");
        }
        finally
        {
            VisionContext.Instance().DrawContent.ClearAll();
            GC.Collect();//释放内存
            GC.WaitForPendingFinalizers();//释放内存
            // FightEndFlag = true; 
        }
        
        return Task.CompletedTask;
    }
    
    public static int RecoverCount = 0; // 吃复活药次数
    
    private Task TakeMedicine(CancellationToken cts2,bool endBloodCheck = false)
    {
        RecoverCount = 0; // 吃复活药次数
        IsTpForRecover = true; //检查复活关闭
        var resurrectionCount = 0; // 吃复活药次数
        var tolerance = 10;// 定义容错范围
        var greenBlood = 0; // 绿血标记
        int finalCheckInterval = Math.Max(_taskParam.CheckInterval - 150, 1);
        //吃药间隔时间
        var medicineInterval = 20;
        DateTime lastMedicineTime = DateTime.MinValue;

        try
        {
            Task.Run(() =>
            {
                using (var ra = CaptureToRectArea())
                {
                    using var mRect = ra.DeriveCrop(1817, 781, 4, 14);
                    using var mask = OpenCvCommonHelper.Threshold(mRect.SrcMat, new Scalar(192, 233, 102),
                        new Scalar(193, 233, 103));
                    using var labels = new Mat();
                    using var stats = new Mat();
                    using var centroids = new Mat();

                    var numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
                        connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);

                    // Logger.LogInformation("自动吃药：检测到{numLabels}", numLabels);

                    if (!(numLabels > 1)) //判断是否带营养袋，连通性检测药品上方的绿色块
                    {
                        IsTpForRecover = true;
                        TaskControl.Logger.LogInformation("自动吃药：未发现营养袋，自动吃药关闭");
                        return;
                    }
                    else
                    {
                        if (!endBloodCheck)
                            TaskControl.Logger.LogInformation(
                                "自动吃药：检测间隔{checkInterval}，吃药间隔{medicineInterval}，吃药上限{recoverMaxCount}，结束吃药{endBloodCheck}",
                                _taskParam.CheckInterval, _taskParam.MedicineInterval, _taskParam.RecoverMaxCount,
                                _taskParam.EndBloodCheackEnabled ? "开" : "关");
                    }
                }

                while (!FightEndFlag && !cts2.IsCancellationRequested)
                {
                    var gray = false;
                    var redBlood = false;
                    // ImageRegion? bb = null;
                    
                    try
                    {
                        cts2.ThrowIfCancellationRequested();

                        var cheack = NewRetry.WaitForAction(() =>
                        {
                            using (var ra = CaptureToRectArea())
                            {
                                var pixelValue = ra.SrcMat.At<Vec3b>(32, 67); //派蒙头冠颜色，比模板匹配快，在开大或其他页面不进行检查
                                var paiMon = (Math.Abs(pixelValue[0] - 143) <= tolerance &&
                                              Math.Abs(pixelValue[1] - 196) <= tolerance &&
                                              Math.Abs(pixelValue[2] - 233) <= tolerance);
                                if (!paiMon)
                                {
                                    //延时_taskParam.CheckInterval毫秒再次检查
                                    Sleep(finalCheckInterval, _ct);
                                    return true;
                                }

                                using var bloodtRect = ra.DeriveCrop(808, 1009, 3, 3);
                                // bb = bloodtRect;
                                using var mask = OpenCvCommonHelper.Threshold(bloodtRect.SrcMat,
                                    new Scalar(250, 90, 89),
                                    new Scalar(250, 91, 89));
                                using var labels = new Mat();
                                using var stats = new Mat();
                                using var centroids = new Mat();

                                var numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
                                    connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);

                                //死亡检查
                                for (int h = 0; h < 4; h++)
                                {
                                    using var croppedImage = ra.DeriveCrop(1797, 249 + 96 * h, 8, 1).SrcMat;

                                    var isGrayscale = true;
                                    for (int i = 0; i < croppedImage.Rows; i++)
                                    {
                                        for (int j = 0; j < croppedImage.Cols; j++)
                                        {
                                            Vec3b pixel = croppedImage.At<Vec3b>(i, j);
                                            if (pixel[0] != pixel[1] || pixel[1] != pixel[2]) //转为灰度后，是否和现在的灰度一样，即可判断死亡
                                            {
                                                isGrayscale = false;
                                                break;
                                            }
                                        }

                                        if (!isGrayscale)
                                        {
                                            break;
                                        }
                                    }

                                    if (isGrayscale)
                                    {
                                        gray = true;
                                    }
                                }

                                if (numLabels > 1) //红血检查
                                {
                                    pixelValue = ra.SrcMat.At<Vec3b>(785, 1818);
                                    if (pixelValue[0] == 255 && pixelValue[1] == 255 && pixelValue[2] == 255)
                                    {
                                        if (resurrectionCount >= 0)
                                        {
                                            Logger.LogInformation("自动吃药：检测到复活药，{text} 吃回复药", "不执行");
                                            resurrectionCount = -1;
                                        }

                                        redBlood = false;
                                    }
                                    else
                                    {
                                        redBlood = true;
                                    }
                                }
                                else
                                {
                                    pixelValue = ra.SrcMat.At<Vec3b>(1010, 814); //在丝血时，连通性和颜色判断都检测不到，直接检测是否为绿色累计3次
                                    if (!(Math.Abs(pixelValue[0] - 34) <= tolerance &&
                                          Math.Abs(pixelValue[1] - 215) <= tolerance &&
                                          Math.Abs(pixelValue[2] - 150) <= tolerance))
                                    {
                                        greenBlood++;
                                        if (greenBlood > 3 || endBloodCheck && greenBlood > 0)
                                        {
                                            pixelValue = ra.SrcMat.At<Vec3b>(785, 1818);
                                            if (pixelValue[0] == 255 && pixelValue[1] == 255 && pixelValue[2] == 255)
                                            {
                                                if (resurrectionCount >= 0)
                                                {
                                                    Logger.LogInformation("自动吃药：检测到复活药，{text} 吃回复药", "不执行");
                                                    resurrectionCount = -1;
                                                }

                                                redBlood = false;
                                            }
                                            else
                                            {
                                                bool isSimilar = IsPixelSimilar(bloodtRect.SrcMat.At<Vec3b>(1, 1), bloodtRect.SrcMat.At<Vec3b>(2, 2), 3);
                                                // Logger.LogWarning("自动吃药：检测到血量颜色异常，是否与之前的血量颜色相似：{isSimilar}", isSimilar);
                                                if (isSimilar)
                                                {
                                                    redBlood = false;
                                                }
                                                else
                                                {
                                                    redBlood = true;
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        greenBlood = 0;
                                    }

                                }

                                return redBlood || gray;
                            }
                        }, cts2, 1, _taskParam.CheckInterval - 150).Result;

                        if ((redBlood || gray) &&
                            (DateTime.UtcNow - PathingConditionConfig.LastEatTime).TotalMilliseconds >
                            Math.Max(_taskParam.MedicineInterval, 1500))
                        {
                            var shouldRecover = (redBlood && resurrectionCount < _taskParam.RecoverMaxCount) ||
                                                (gray && RecoverCount < 3); //判断吃药上限

                            if (Monitor.TryEnter(ZLock))
                            {
                                try
                                {
                                    if (shouldRecover)
                                    {
                                        try
                                        {

                                            if (!AutoFightSkill.MedicinalCdAsync(Logger, false, 1, cts2).Result)
                                            {
                                                Simulation.SendInput.SimulateAction(GIActions
                                                    .QuickUseGadget); //1800,816 1838,835
                                                Simulation.ReleaseAllKey();
                                            }
                                        }
                                        catch (OperationCanceledException ex)
                                        {
                                            Console.WriteLine($"自动结束吃药12：{ex.Message}");
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"自动结束吃药发生异常12: {ex.Message}");
                                        }

                                        if (redBlood) resurrectionCount++;
                                        if (gray) RecoverCount++;
                                        TaskControl.Logger.LogInformation("自动吃药：{text} " + "使用小道具",
                                            redBlood ? "发现红血" : "发现角色死亡");
                                        
                                        // //LOG显示bloodtRect区域的颜色值
                                        // var aa = bb.SrcMat.At<Vec3b>(1, 1);
                                        // Logger.LogError("自动吃药：检测到红血，血量颜色值BGR1({b},{g},{r})",aa[0], aa[1], aa[2]);
                                        // var aaa = bb.SrcMat.At<Vec3b>(2, 2);
                                        // Logger.LogError("自动吃药：检测到红血，血量颜色值BGR2({b},{g},{r})",aaa[0], aaa[1], aaa[2]);
                                        
                                        PathingConditionConfig.LastEatTime = DateTime.UtcNow;
                                        redBlood = false;
                                        gray = false;
                                        if (endBloodCheck && (resurrectionCount >= 1 || RecoverCount >= 1))
                                            return; //单次检测复用
                                    }
                                    else
                                    {
                                        resurrectionCount = 0;
                                        RecoverCount = 0;
                                        TaskControl.Logger.LogInformation("自动吃药：{text}", "吃药数量超额退出！");
                                        IsTpForRecover = false; // 吃完药品后，打开复活检测
                                        return;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    TaskControl.Logger.LogError($"自动吃药异常: {ex.Message}");
                                }
                                finally
                                {
                                    Monitor.Exit(ZLock);
                                }
                            }
                        }

                        using (var bitmap = CaptureToRectArea()) //复活界面检测，自动战斗期间，不进行BGI的复活检测，超出吃药上限后才会检测
                        {
                            var pixelValue =
                                bitmap.SrcMat.At<Vec3b>(785, 1818); //
                            if (pixelValue[0] == 255 && pixelValue[1] == 255 && pixelValue[2] == 255 && (DateTime.Now - lastMedicineTime).TotalSeconds > medicineInterval)
                            {
                                try
                                {
                                    if (!AutoFightSkill.MedicinalCdAsync(Logger, false, 1, cts2).Result)
                                    {
                                        lastMedicineTime = DateTime.Now;
                                        Logger.LogInformation("自动吃药：{text} " + "使用小道具", "发现复活药");
                                        Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget); //1800,816 1838,835
                                        Simulation.ReleaseAllKey();
                                    }
                                }
                                catch (OperationCanceledException ex)
                                {
                                    Console.WriteLine($"自动结束吃药12：{ex.Message}");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"自动结束吃药发生异常12: {ex.Message}");
                                }
                            }
                            
                            var confirmRa = bitmap.Find(AutoFightAssets.Instance.ConfirmRa);
                            if (confirmRa.IsExist())
                            {
                                confirmRa.Click();
                                RecoverCount++;
                                Task.Delay(500, cts2).Wait(500);
                                using var bitmap2 = CaptureToRectArea();
                                var okRa = bitmap2.Find(AutoFightAssets.Instance.ConfirmRa);
                                {
                                    if (okRa.IsExist())
                                    {
                                        Logger.LogInformation("自动吃药：{text} 退出复活界面", "点击");
                                        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                                        Task.Delay(500, cts2).Wait(500);
                                    }
                                }
                            }

                            if (RecoverCount > 2)
                            {
                                TaskControl.Logger.LogInformation("自动吃药：{text}", "吃复活药数量超额退出！-2");
                                resurrectionCount = 0;
                                RecoverCount = 0;
                                IsTpForRecover = false; // 吃完药品后，打开复活检测
                                return; // 吃完药品后，退出
                            }
                        }
                    }
                    catch (OperationCanceledException ex)
                    {
                        Console.WriteLine($"自动吃药发生异常: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        // Console.WriteLine($"自动吃药发生异常: {ex.Message}");
                    }
                }

            }, cts2);
        }
        catch (OperationCanceledException ex)
        {
            Console.WriteLine($"自动吃药发生异常: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"自动吃药发生异常: {ex.Message}");
        }
        finally
        {
            using (var bitmap = CaptureToRectArea()) //复活界面检测，自动战斗期间，不进行BGI的复活检测，超出吃药上限后才会检测
            {
                var confirmRa = bitmap.Find(AutoFightAssets.Instance.ConfirmRa);
                if (confirmRa.IsExist())
                {
                    confirmRa.Click();
                    Task.Delay(500, cts2).Wait(500);
                    using var bitmap2 = CaptureToRectArea();
                    var okRa = bitmap2.Find(AutoFightAssets.Instance.ConfirmRa);
                    {
                        if (okRa.IsExist())
                        {
                            Logger.LogInformation("自动吃药：{text} 复活界面", "退出");
                            Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                            Task.Delay(500, cts2).Wait(1000);
                            try
                            {

                                if (!AutoFightSkill.MedicinalCdAsync(Logger, false, 1, cts2).Result)
                                {
                                    Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget); //1800,816 1838,835
                                    Simulation.ReleaseAllKey();
                                }
                            }
                            catch (OperationCanceledException ex)
                            {
                                Console.WriteLine($"自动结束吃药123：{ex.Message}");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"自动结束吃药发生异常123: {ex.Message}");
                            }
                        }
                    }
                }
            }
        }
        
        return Task.CompletedTask;
    }
    
    static bool IsPixelSimilar(Vec3b p1, Vec3b p2, int threshold)
    {
        // 注意：OpenCV的Vec3b[0]是B通道，[1]是G通道，[2]是R通道（不是RGB而是BGR！）
        int diffB = Math.Abs(p1[0] - p2[0]); // 蓝色通道差值
        int diffG = Math.Abs(p1[1] - p2[1]); // 绿色通道差值
        int diffR = Math.Abs(p1[2] - p2[2]); // 红色通道差值

        // 所有通道的差值都≤阈值才返回true
        return diffB <= threshold && diffG <= threshold && diffR <= threshold;
    }

    //定义按键，用于结束吃药的切换人
    private static readonly GIActions[] MemberActions = new GIActions[]
    {
        GIActions.SwitchMember1,
        GIActions.SwitchMember2,
        GIActions.SwitchMember3,
        GIActions.SwitchMember4
    };

    private async Task EndBloodCheck(CancellationToken ct, CombatScenes? combatScenes = null)
    {
        IsTpForRecover = true; // 复活检测关闭
        var ms = 2500;  //检测区域是否有红血，没有发现红血，则退出
        var useMedicine = new List<int> { 1, 2, 3, 4 };
        var endBloodCheck = false;//血量复检标志位

        try
        {
            await TakeMedicine(ct, true); //尝试吃药和复活角色

            while (ms > 0)
            {
                using (var ra = CaptureToRectArea())
                {
                    var pixelValue =
                        ra.SrcMat.At<Vec3b>(785, 1818); //TakeMedicine后，不会有死亡角色，如出现死亡角色导致变复活药，说明死超过2位角色，也不用执行了
                    if (pixelValue[0] == 255 && pixelValue[1] == 255 && pixelValue[2] == 255)
                    {
                        if(!_taskParam.QRecoverAvatar) return;
                        Logger.LogInformation("自动结束吃药：检测到复活药，{text} 结束吃恢复药", "不执行");
                        Logger.LogInformation("自动结束吃药：{text} 执行技能恢复", "尝试执行");
                    }
                    else
                    {
                        // 非复活药前提下再识别营养袋，优化效率
                        using var mRect = ra.DeriveCrop(1817, 781, 4, 14);
                        using var mask = OpenCvCommonHelper.Threshold(mRect.SrcMat, new Scalar(192, 233, 102),
                            new Scalar(193, 233, 103));
                        using var labels = new Mat();
                        using var stats = new Mat();
                        using var centroids = new Mat();

                        var numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
                            connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);

                        // Logger.LogInformation("自动吃药：检测到{numLabels}", numLabels);

                        if (!(numLabels > 1)) //判断是否带营养袋，连通性检测药品上方的绿色块
                        {
                            Logger.LogInformation("自动结束吃药：{t} 营养袋，结束吃药关闭", "未发现");
                            return;
                        }
                    }

                    for (var h = 0; h < 4; h++)
                    {
                        using var bloodtRect = ra.DeriveCrop(1694, 267 + h * 96, 3, 24);
                        using var mask = OpenCvCommonHelper.Threshold(bloodtRect.SrcMat, new Scalar(150, 215, 34),
                            new Scalar(161, 220, 60));
                        using var labels = new Mat();
                        using var stats = new Mat();
                        using var centroids = new Mat();

                        var numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
                            connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S); //非出战角色，右侧头像血量检查

                        using var bloodtRect2 = ra.DeriveCrop(1859, 264 + h * 96, 3, 3);
                        using var mask2 = OpenCvCommonHelper.Threshold(bloodtRect2.SrcMat, new Scalar(255, 255, 255));
                        using var labels2 = new Mat();
                        using var stats2 = new Mat();
                        using var centroids2 = new Mat();

                        var numLabels2 = Cv2.ConnectedComponentsWithStats(mask2, labels2, stats2, centroids2,
                            connectivity: PixelConnectivity.Connectivity4,
                            ltype: MatType.CV_32S); //出战代表没有死亡，如果红血，会在开头的TakeMedicine恢复

                        // Logger.LogInformation("自动结束吃药：{text} 血量检查，{text4}", h + 1, numLabels);
                        if (numLabels > 1 || !(numLabels2 > 1))
                        {
                            ms = 1; 
                            useMedicine.Remove(h + 1);
                            // Logger.LogInformation("自动结束吃药：{text} 发现红血 {t4}", h + 1,useMedicine);
                        }
                    }
                }
                
                // Logger.LogWarning("自动结束吃药：{text} 个", useMedicine.Count);

                if (useMedicine.Count > 0 && !endBloodCheck) //发现红血角色，可能因为游泳等误判，进行复检
                {
                    endBloodCheck = true;
                    TaskControl.Logger.LogInformation("自动结束吃药：检测到红血角色，{text} 结束吃药，进行复检", useMedicine);
                    ms = 100; // 设置100会再次检测
                    useMedicine = new List<int> { 1, 2, 3, 4 };
                    await Task.Delay(500, ct);
                }

                await Task.Delay(100, ct);
                ms -= 95;
            }

            using var swimming = CaptureToRectArea();
            if (useMedicine.Count > 0 && !Avatar.SwimmingConfirm(swimming))
            {
                if (_taskParam.QRecoverAvatar && PathingConditionConfig.PartyConfigBackUp.RecoverAvatarIndex is not null)
                {
                    var pathExecutor = new PathExecutor(ct);
                    Logger.LogWarning("自动结束吃药：{text} 结束吃药，执行技能恢复 {text2}", useMedicine,PathingConditionConfig.PartyConfigBackUp.RecoverAvatarIndex);
                    await pathExecutor.TryPartyHealing(combatScenes,PathingConditionConfig.PartyConfigBackUp);
                    // if (_taskParam.QRecoverAvatar) return; 
                    return;
                }

                //计算2上次吃药时间到现在是否超过2秒，未超过就等待
                if ((DateTime.UtcNow - PathingConditionConfig.LastEatTime).TotalMilliseconds < 1500)
                {
                    await Task.Delay(
                        1500 - (int)(DateTime.UtcNow - PathingConditionConfig.LastEatTime).TotalMilliseconds, ct);
                }

                PathingConditionConfig.LastEatTime = DateTime.UtcNow;
                TaskControl.Logger.LogInformation("自动结束吃药：发现红血角色，执行吃药 {text} 编号", useMedicine);
                //通过编号切换角色补血,不进行确认是否吃上
                foreach (var num in useMedicine)
                {
                    Simulation.ReleaseAllKey();
                    await Task.Delay(700, ct);
                    Simulation.SendInput.SimulateAction(MemberActions[num - 1]);
                    await Task.Delay(800, ct);

                    using (var bitmap = CaptureToRectArea())
                    {
                        if (Bv.IsInRevivePrompt(bitmap)) //如果在复活界面，说明没复活药了
                        {
                            Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                            await Task.Delay(500, ct);
                        }
                    }

                    try
                    {
                        if (!AutoFightSkill.MedicinalCdAsync(Logger, false, 1,ct).Result)
                        {
                            Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget);
                        } 
                    }
                    catch (OperationCanceledException ex)
                    {
                        Console.WriteLine($"自动结束吃药1：{ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"自动结束吃药发生异常1: {ex.Message}");
                    }
                    
                    await Task.Delay(700, ct);
                }
            }
            else
            {
                TaskControl.Logger.LogInformation("自动结束吃药：检测未发现红血角色，{text} 结束吃药", "不执行");
            }

            IsTpForRecover = false; // 复活检测打开
        }
        catch (OperationCanceledException ex)
        {
            Console.WriteLine($"战斗结束血量检测发生异常: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"战斗结束血量检测发生异常: {ex.Message}");
        }
        finally
        {
            // PathingConditionConfig.PartyConfigBackUp = new PathingPartyConfig();
        }
    }

    static double FindMax(double[] numbers)
    {
        if (numbers == null || numbers.Length == 0)
        {
            throw new ArgumentException("The array is empty or null.");
        }

        double max = numbers[0] > 10000 ? 0 : numbers[0];
        foreach (var num in numbers)
        {
            var cpnum = numbers[0] > 10000 ? 0 : num;
            max = Math.Max(max, num);
        }

        return max;
    }

    [Obsolete]
    private static Dictionary<string, double> ParseStringToDictionary(string input, double defaultValue = -1)
    {
        var dictionary = new Dictionary<string, double>();

        if (string.IsNullOrEmpty(input))
        {
            return dictionary; // 返回空字典
        }

        string[] pairs = input.Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var pair in pairs)
        {
            var parts = pair.Split(',', StringSplitOptions.TrimEntries);

            if (parts.Length > 0)
            {
                string name = parts[0];
                double value = defaultValue;

                if (parts.Length > 1 && double.TryParse(parts[1], out var parsedValue))
                {
                    value = parsedValue;
                }

                dictionary[name] = value;
            }
        }

        return dictionary;
    }

    private bool HasFightFlagByYolo(ImageRegion imageRegion)
    {
        // if (RuntimeHelper.IsDebug)
        // {
        //     imageRegion.SrcMat.SaveImage(Global.Absolute(@"log\fight\" + $"{DateTime.UtcNow:yyyyMMdd_HHmmss_ffff}.png"));
        // }
        var dict = _predictor.Detect(imageRegion);
        return dict.ContainsKey("health_bar") || dict.ContainsKey("enemy_identify");
    }

    // 无用
    // [Obsolete]
    // private bool HasFightFlagByGadget(ImageRegion imageRegion)
    // {
    //     // 小道具位置 1920-133,800,60,50
    //     var gadgetMat = imageRegion.DeriveCrop(AutoFightAssets.Instance.GadgetRect).SrcMat;
    //     var list = ContoursHelper.FindSpecifyColorRects(gadgetMat, new Scalar(225, 220, 225), new Scalar(255, 255, 255));
    //     // 要大于 gadgetMat 的 1/2
    //     return list.Any(r => r.Width > gadgetMat.Width / 2 && r.Height > gadgetMat.Height / 2);
    // }
}
