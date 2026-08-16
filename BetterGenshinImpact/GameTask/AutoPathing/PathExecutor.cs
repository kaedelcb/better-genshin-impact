using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using BetterGenshinImpact.GameTask.AutoSkip;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Common.Map;
using BetterGenshinImpact.GameTask.Model.Area;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoPathing.Suspend;
using BetterGenshinImpact.GameTask.Common;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using static BetterGenshinImpact.GameTask.SystemControl;
using ActionEnum = BetterGenshinImpact.GameTask.AutoPathing.Model.Enum.ActionEnum;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Exceptions;
using BetterGenshinImpact.GameTask.Common.Map.Maps;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using BetterGenshinImpact.GameTask.Common.Job;

namespace BetterGenshinImpact.GameTask.AutoPathing;

public partial class PathExecutor
{
    private readonly CameraRotateTask _rotateTask;
    private readonly TrapEscaper _trapEscaper;
    private readonly BlessingOfTheWelkinMoonTask _blessingOfTheWelkinMoonTask = new();
    private AutoSkipTrigger? _autoSkipTrigger;
    public int SuccessFight = 0;
    //路径追踪完全走完所有路径结束的标识
    public bool SuccessEnd = false;

    /// <summary>联机模式：请求跳到下一个路线段（成员侧异常恢复，需求 1）</summary>
    public volatile bool SkipToNextSegment;

    /// <summary>联机模式：请求跳过当前路线（传递给 ProcessRoutesByGroup，需求 1）</summary>
    public volatile bool SkipRouteRequested;

    /// <summary>联机模式：跳过原因（先写 reason 再写标志位保证顺序）</summary>
    public string? SkipRouteReason;
    private PathingPartyConfig? _partyConfig;
    private CancellationToken ct;
    private PathExecutorSuspend pathExecutorSuspend;
    private string _hurryOnAvatar = "";
    private bool _MwkFly = true;
    // 玛薇卡挑飞触发距离阈值，从 PartyConfig.MwkFlyJumpDistance 读取（默认 75，0=不挑飞）
    private int _mwkFlyJumpDistance = 75;

    // 玛薇卡摩托状态模板匹配识别对象（战后回点下车检测用）。
    // 参考 AutoFontaineLeyLine/main.js：imageRecognition(mwk,2,0,0,1682,972,35,42,0.7,false)
    // —— 模板 mwk.png（绿底掩膜），ROI (1682,972,35,42)，阈值 0.7。模板文件位于
    // GameTask\AutoFight\Assets\1920x1080\mwk.png（随构建输出）。
    // 首次使用时惰性构建并缓存，避免每次回点重复读盘 / InitTemplate。
    private static RecognitionObject? _mwkMotorcycleRo;     // 另一种摩托图标（小ROI+掩码），命中直接true
    private static RecognitionObject? _mwkMotorcycleRo2;    // 主图标形状匹配（大ROI+掩码）
    private static RecognitionObject? _mwkMotorcycleRoGold; // 摩托金黄模板（无掩码）：验证颜色
    private readonly ReturnMainUiTask _returnMainUiTask = new();

    // 玛薇卡飞行等待期间的脱离飞行检测参数（ms）
    // 起飞瞬间未进入稳定飞行，宽限期内不检测，避免刚起飞就被误判打断
    private const int TakeoffGracePeriod = 400;
    // 宽限期后每隔该间隔截图判定一次运动状态
    private const int DetectInterval = 200;
    
    public PathingPartyConfig PartyConfig
    {
        get => _partyConfig ?? PathingPartyConfig.BuildDefault();
        set => _partyConfig = value;
    }
    
    public PathExecutor(CancellationToken ct)
    {
        _trapEscaper = new(ct);
        _rotateTask = new(ct);
        this.ct = ct;
        pathExecutorSuspend = new PathExecutorSuspend(this);
    }

    /// <summary>
    /// 用目标模板匹配判断玛薇卡是否处于摩托状态（战后回点下车检测用）。
    /// 参考 AutoFontaineLeyLine/main.js: imageRecognition(mwk,...,1682,972,35,42,...)，ROI (1682,972,35,42)。
    /// mwk.png 为绿底 + 白箭头（下车提示图标），故启用掩膜 UseMask=true，
    /// MaskColor 用默认绿 (0,255,0) 把绿底抠掉，只匹配白箭头本体（模板位于
    /// GameTask\AutoFight\Assets\1920x1080\mwk.png，随构建输出）。
    /// 匹配模式用 SqDiffNormed（OpenCV 支持掩膜；默认 CCoeffNormed 配掩膜会算出 NaN）。
    /// 实测 7 帧：SqDiffNormed 原始分 在摩托≤0.0144 / 徒步≥0.123，中间空档 0.11；
    /// C# 侧 isLowerBetter → scoreThreshold = 1 - Threshold，设 Threshold=0.93 即原始分≤0.07 判命中。
    /// 首次调用惰性构建并缓存 RecognitionObject，避免每次回点重复读盘 / InitTemplate。
    /// 传入已截取的当前画面 region，直接在其上 Find（不重复截图）。
    /// </summary>
    internal static bool IsMavuikaOnMotorcycleByTemplate(ImageRegion region)
    {
        if (_mwkMotorcycleRo == null || _mwkMotorcycleRo2 == null || _mwkMotorcycleRoGold == null)
        {
            var matMwk = GameTaskManager.LoadAssetImage("AutoFight", "mwk.png");     // 另一种摩托图标
            var matBai = GameTaskManager.LoadAssetImage("AutoFight", "mwkbai.png");   // 非摩托模板（白/绿背景）
            var matJin = GameTaskManager.LoadAssetImage("AutoFight", "mwkjin.png");   // 摩托金黄模板

            // 另一种摩托图标（小ROI+掩码）：命中直接判定为摩托
            _mwkMotorcycleRo = new RecognitionObject
            {
                Name = "MwkMotorcycle",
                RecognitionType = RecognitionTypes.TemplateMatch,
                TemplateImageMat = matMwk,
                RegionOfInterest = new Rect(1682, 972, 35, 42),
                TemplateMatchMode = TemplateMatchModes.SqDiffNormed,
                UseMask = true,
                Threshold = 0.99,
            }.InitTemplate();

            // 主图标形状匹配（大ROI+掩码）：命中后再用金黄模板验证颜色
            _mwkMotorcycleRo2 = new RecognitionObject
            {
                Name = "MwkMotorcycle2",
                RecognitionType = RecognitionTypes.TemplateMatch,
                TemplateImageMat = matBai,
                RegionOfInterest = new Rect(1662, 953, 76, 74),
                TemplateMatchMode = TemplateMatchModes.SqDiffNormed,
                UseMask = true,
                Use3Channels = true,
                Threshold = 0.99,
            }.InitTemplate();

            // 摩托金黄模板（无掩码）：用 mwkjin.png 的完整颜色验证
            _mwkMotorcycleRoGold = new RecognitionObject
            {
                Name = "MwkMotorcycleGold",
                RecognitionType = RecognitionTypes.TemplateMatch,
                TemplateImageMat = matJin,
                RegionOfInterest = new Rect(1662, 953, 76, 74),
                TemplateMatchMode = TemplateMatchModes.SqDiffNormed,
                UseMask = true,
                Use3Channels = true,
                Threshold = 0.998,
            }.InitTemplate();
        }

        // 1. 另一种摩托图标（小ROI）命中 → 直接判定为摩托
        using var result = region.Find(_mwkMotorcycleRo);
        if (result.IsExist())
            return true;

        // 2. 主图标形状匹配（大ROI）→ 再用金黄模板验证颜色
        using var result2 = region.Find(_mwkMotorcycleRo2);
        if (result2.IsExist())
        {
            using var resultGold = region.Find(_mwkMotorcycleRoGold);
            // Logger.LogWarning("[MwkMotorcycle] 形状匹配命中，金黄模板匹配 score={Score:F4} exist={Exist}",
            //     resultGold.MatchScore ?? -1, resultGold.IsExist());
            if (resultGold.IsExist())
                return true;
        }

        return false;
    }

    /// <summary>
    /// 判断是否中止地图追踪的条件
    /// </summary>
    public Func<ImageRegion, bool>? EndAction { get; set; }

    /// <summary>
    /// 联机协调器，为 null 时以单机模式运行，不引入任何额外逻辑。
    /// </summary>
    public MultiplayerCoordinator? MultiplayerCoordinator { get; set; }

    /// <summary>
    /// 当前线路的同步点（第一个传送点）是否已到达。
    /// 用于判断异常发生在同步点前还是同步点后。
    /// </summary>
    private bool _syncPointReached = false;

    /// <summary>
    /// 按线路切角色 Hook（hoeing-multiplayer-per-route-switch-roles）。默认 null = 不启用。
    /// 由 RouteExecutionEngine 在「该线路配了角色」时注入。承载：本线路是否需切换 +
    /// 「传送完成后执行切角色」的异步委托。null 时所有相关分支短路（§Unchanged Behavior 第 2/3 条）。
    /// </summary>
    public PerRouteSwitchHook? PerRouteSwitchHook { get; set; }

    /// <summary>本线路是否已为「按线路切角色」触发过（每线路 new 新实例，实例级隔离，R4.3）。</summary>
    private bool _perRouteSwitchDone = false;

    /// <summary>
    /// 本次传送点的「按周期吃食物」计划（进程内运行时标记）：传送 loading 间隙由 BuildPlan 算出，
    /// 传送完成执行后清空。单机 / 非联机时恒为 Empty。multiplayer-hoeing-auto-eat-food-by-period spec。
    /// </summary>
    private BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.MedicineEatPlan _pendingMedicineEatPlan
        = BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.MedicineEatPlan.Empty;

    /// <summary>
    /// 异常恢复后，需要在下一个同步点等待前上报 Normal（恢复参与全员判定）。
    /// </summary>
    private bool _needReportNormalBeforeSync = false;

    /// <summary>
    /// 联机模式：检测到"已倒下"色块（队友/自己倒地复苏）信号位。
    /// 由 AnomalyDetector 通过 SignalMultiplayerRevival() 设置；
    /// PathExecutor 在战斗结束钩子 / 脱困入口 / 主循环检查 三处通过 TryConsumeRevivalSignal()
    /// CAS 消费，命中后先 TpStatueOfTheSeven 再抛 RetryException 走"同步点前/后"分流。
    ///
    /// 字段语义：0 = 未触发，1 = 已触发未消费。
    /// 用 int 而非 bool 是因为 Interlocked.CompareExchange 不支持 bool 重载。
    /// volatile 保证多线程下读到最新值（AnomalyDetector 后台线程写、PathExecutor 主线程读）。
    /// </summary>
    private volatile int _multiplayerRevivalDetected = 0;

    /// <summary>
    /// 联机模式：反复复苏升级动作（multi-revival-rapid-recurrence-fallback spec）。
    /// AnomalyDetector → RouteExecutionEngine → SignalMultiplayerRevival(action) 写入；
    /// RetryException catch 块在原"同步点前/后"分流之前先消费此字段。
    /// 0=Continue, 1=SkipSegment, 2=SkipRoute（与 RevivalEscalationAction 枚举对齐）。
    /// </summary>
    private volatile int _pendingRevivalEscalation = 0;

    /// <summary>
    /// 线路重试模式 v2（hoeing-multiplayer-route-retry-mode §0）：RetrySegment 重跑本段时置 true。
    /// 使本段起点传送块跳过"按周期吃食物"和"按线路切角色"两个多余动作
    /// （已回神像满血 + 复苏者战斗中途，重跑时再吃药/换人无意义且浪费时间）。
    /// 段起点切角色块执行后消费即复位。默认 false，非重跑路径永不置位。保留正常同步不受影响。
    /// </summary>
    private bool _rerunSuppressExtrasOnce = false;

    /// <summary>
    /// 线路重试模式 v2（hoeing-multiplayer-route-retry-mode §0）：重跑本段时豁免"该次"落后追赶判定。
    ///
    /// 必要性：重跑是【主动回退重打】，进度低于继续前进的队友是预期且正确的，不是真正的落后。
    /// 若不豁免，重跑刚传回段起点就会被落后追赶判定"落后≥阈值段"→ 抛 LaggingCatchUpSkipException 跳段，
    /// 重跑成果被直接踹走（实测日志：我 4000000 落后大部队 5000000 → 跳下一段 → 整条路线被跳过）。
    ///
    /// 注意：不能复用 _needReportNormalBeforeSync 做守卫——它在落后追赶判定【之前】的
    /// "上报 Normal"块里就已被消费复位，到判定时恒为 false，等于没有豁免（这正是实测 bug 的成因）。
    /// 故用本独立标志：catch RetrySegment 分支置位，落后追赶判定处消费即复位。
    /// 默认 false，非重跑路径永不置位 → 落后追赶逻辑逐字节不变。
    /// </summary>
    private bool _exemptNextLaggingCatchUpOnce = false;

    /// <summary>
    /// 线路重试模式 v2（hoeing-multiplayer-route-retry-mode §0）：本条线路是否命中重试模式白名单。
    /// 由 RouteExecutionEngine 在 ExecuteRoute 构造 PathExecutor 后注入（房主同步的白名单，全员一致）。
    /// 仅 true 时才启用"段出口屏障"（每段结尾全员同步碰头 + 汇总是否有人死过）。
    /// false（含单机、非白名单线路）时屏障整块短路 → 对其他线路的速度和逻辑零影响。
    /// </summary>
    public bool RouteRetryModeEnabled { get; set; } = false;

    /// <summary>
    /// 线路重试模式 v2（§0）：本段是否有成员死过（自己复苏 或 收到队友复苏广播）。
    /// 置位者：本机复苏消费点 / RouteExecutionEngine 收到队友广播后调 MarkSegmentDeathForRerun()。
    /// 消费者：段出口屏障放行后读它决定"全员重跑本段"还是"全员放行"。
    /// 复位：段推进（新段开始）与重跑启动时清零，避免跨段残留。
    /// volatile：AnomalyDetector/SignalR 后台线程写、PathExecutor 主线程读。
    /// </summary>
    private volatile bool _segmentDeathForRerun = false;

    /// <summary>
    /// 线路重试模式 v2（§0）：本段已重跑过一次（"只重试一次"保证）。
    /// 段出口屏障判定时：有人死过 &amp;&amp; 本段未重跑过 → 重跑；已重跑过 → 放行（不再无限重跑）。
    /// 段推进时复位。
    /// </summary>
    private bool _segmentRerunDone = false;

    /// <summary>
    /// 线路重试模式 v2（§0）：复苏者回神像后不立即重跑，而是先到"段出口屏障"与队友碰头。
    /// 复苏消费点在 retry-route 下置位并 break 出 waypoint 循环，直达段出口屏障。
    /// 屏障放行后统一决策。非 retry-route 恒 false → 原"神像 + 抛 RetryException"流程逐字节不变。
    /// </summary>
    private bool _revivedGoToSegExitBarrier = false;

    /// <summary>
    /// 线路重试模式 v2（§0）：供 RouteExecutionEngine 在收到队友复苏广播时标记"本段有人死过"。
    /// 仅 retry-route 生效（调用方已守卫）。线程安全（volatile 写）。
    /// </summary>
    public void MarkSegmentDeathForRerun()
    {
        _segmentDeathForRerun = true;
    }

    /// <summary>
    /// 联机模式专用：外部（AnomalyDetector）检测到联机已倒下界面时调用。
    /// 仅在联机模式下有效，单机模式忽略以保留原有行为。
    /// </summary>
    public void SignalMultiplayerRevival()
        => SignalMultiplayerRevival(BetterGenshinImpact.GameTask.AutoHoeing.Services.RevivalEscalationAction.Continue);

    /// <summary>
    /// 供 AutoFightTask 战斗主循环探测：当前是否"已收到复苏信号 且 待处理 escalation==RetrySegment"。
    /// 仅探测不消费（不改信号位、不改 escalation）。用于让战斗循环在 RetrySegment 场景无视共享战斗配额、
    /// 立即结束战斗，尽快进入重跑（hoeing-multiplayer-route-retry-mode spec, EB-4）。
    /// CurrentActiveInstance==null（单机/无活动实例）时返回 false。
    /// </summary>
    public static bool ShouldAbortFightForRetryRevival()
    {
        var instance = CurrentActiveInstance;
        if (instance == null) return false;
        // 两个字段都是 volatile int，普通读即可见。
        return instance._multiplayerRevivalDetected == 1
            && instance._pendingRevivalEscalation == (int)BetterGenshinImpact.GameTask.AutoHoeing.Services.RevivalEscalationAction.RetrySegment;
    }

    /// <summary>
    /// 联机模式专用：外部（RouteExecutionEngine）检测到反复复苏触发升级动作时调用。
    /// 仅在联机模式下有效，单机模式忽略以保留原有行为。
    /// 升级动作通过 _pendingRevivalEscalation 字段透传到 RetryException catch 块。
    /// </summary>
    public void SignalMultiplayerRevival(BetterGenshinImpact.GameTask.AutoHoeing.Services.RevivalEscalationAction action)
    {
        if (MultiplayerCoordinator == null) return;
        // 写 escalation：用 Interlocked.Exchange 保证写入对所有线程立即可见
        System.Threading.Interlocked.Exchange(ref _pendingRevivalEscalation, (int)action);
        // 写信号位：与 TryConsumeRevivalSignal 的 CAS 配对
        MultiplayerRevivalGate.Signal(ref _multiplayerRevivalDetected);
        Logger.LogWarning("[联机] AnomalyDetector 信号：已倒下检测（escalation={Action}），将在战斗结束钩子 / 脱困入口 / 主循环检查 任一处先消费并去七天神像回血", action);
    }

    /// <summary>
    /// CAS 消费 <see cref="_multiplayerRevivalDetected"/> 信号位。
    /// 联机模式下：原值为 1 时返回 true 并将值置为 0；其他情况返回 false。
    /// 单机模式下：直接返回 false（信号位本就不会被设置，但额外守卫便于调用方写法统一）。
    ///
    /// 调用方典型用法：
    ///     if (TryConsumeRevivalSignal())
    ///     {
    ///         await TpStatueOfTheSeven();
    ///         throw new RetryException("...");
    ///     }
    ///
    /// 详见 design.md §1.3 / bugfix.md 2.5 / 2.11。
    /// </summary>
    private bool TryConsumeRevivalSignal()
    {
        return MultiplayerRevivalGate.TryConsume(ref _multiplayerRevivalDetected, MultiplayerCoordinator != null);
    }

    /// <summary>
    /// 当前 JSON 路线在 ProcessRoutesByGroup 中的索引（路线级别），由外部注入。
    /// 用于计算全局进度值。
    /// </summary>
    public int CurrentJsonRouteIndex { get; set; } = 0;

    /// <summary>
    /// 计算全局进度值：路线索引 × 1,000,000 + 段索引 × 1,000 + waypoint索引
    /// </summary>
    private long ComputeProgress(int segmentIndex, int waypointIndex)
    {
        return (long)CurrentJsonRouteIndex * 1_000_000 + (long)segmentIndex * 1_000 + waypointIndex;
    }

    /// <summary>
    /// 当前线路索引（用于异常上报）。
    /// </summary>
    public int CurrentRouteIndex { get; set; }

    /// <summary>
    /// 世界状态监测器，传送期间用于抑制误报。
    /// </summary>
    public WorldStateMonitor? WorldStateMonitor { get; set; }

    /// <summary>
    /// 静态引用，供 Avatar.TpForRecover 等静态方法在传送时通知 WorldStateMonitor。
    /// </summary>
    public static WorldStateMonitor? CurrentWorldStateMonitor { get; set; }

    /// <summary>
    /// 静态引用，供 AutoFightHandler 在构造 AutoFightParam 时判断"当前是否联机锄地 + 万叶玩家"，
    /// 决定是否显式 set <see cref="AutoFightParam.KazuhaContinuousReturn"/> = true。
    /// 单机模式下值为 null，AutoFightHandler 不读取——零感知。
    /// 由 RouteExecutionEngine 注入、AutoHoeingTask finally 块清理，与 CurrentWorldStateMonitor 同生命周期。
    /// 详见 .kiro/specs/multiplayer-kazuha-pre-cast-positioning/design.md §3.4。
    /// </summary>
    public static MultiplayerCoordinator? CurrentMultiplayerCoordinator { get; set; }

    /// <summary>
    /// 当前活动 PathExecutor 实例，由 Pathing(...) 入口注入、finally 清除。
    /// 供外部静态调用方（如 TpTask）通过 SignalMultiplayerRevivalFromExternal 写信号位。
    ///
    /// 嵌套调用语义：仅由 Pathing(...) 入口管理。Pathing 方法体内进入时保存上一层 previous，
    /// 完成后还原；其他方法（如 MoveTo / TpStatueOfTheSeven）不动此字段，避免被嵌套覆盖。
    ///
    /// 详见 .kiro/specs/multiplayer-tp-revive-prompt-detection/design.md §3.2 / bugfix.md §"R2"。
    /// </summary>
    private static PathExecutor? CurrentActiveInstance { get; set; }

    /// <summary>
    /// 外部（非 PathExecutor 内部代码）通过此方法写复苏信号位。
    /// 等价于 AnomalyDetector → OnMultiplayerDefeatedDetected → SignalMultiplayerRevival 的语义路径。
    ///
    /// 用法：仅由 TpTask.WaitForTeleportCompletion 在传送中检测到复苏弹窗时调用。
    /// 当 CurrentActiveInstance == null（无活动 PathExecutor 实例）时，LogWarning 跳过、
    /// 不抛异常、不阻断主流程。
    ///
    /// 详见 .kiro/specs/multiplayer-tp-revive-prompt-detection/bugfix.md §"EB 2.8" / §"Q1"。
    /// </summary>
    public static void SignalMultiplayerRevivalFromExternal()
    {
        var instance = CurrentActiveInstance;
        if (instance == null)
        {
            // 不直接引用 TaskControl.Logger 静态字段（测试环境下其静态初始化依赖 App._host 会触发 NRE），
            // 改用 Debug.WriteLine 作为兜底诊断，生产/测试环境都安全。
            // 实际应用中此分支不应被触发：TpTask 总是在 PathExecutor.Pathing 调用栈内调用本方法。
            System.Diagnostics.Debug.WriteLine("[联机] SignalMultiplayerRevivalFromExternal 未找到活动 PathExecutor 实例，跳过信号位写入");
            return;
        }
        instance.SignalMultiplayerRevival();
    }

    /// <summary>
    /// fight waypoint 索引 → syncPointId 的映射，由 SyncPointResolver 预计算。
    /// key = waypointListIndex * 10000 + waypointIndex
    /// </summary>
    private Dictionary<int, string?> _syncPointMap = new();

    /// <summary>
    /// 段级缓存（fastsync-redesign-parameter-passing spec / OQ-1=c）：
    /// 当前段内 waypoint 索引 → 抢报 syncId（仅命中 _syncPointMap 的项）。
    /// 段循环开始时构建一次（投影 _syncPointMap 中 listIdx == 当前段 的项），
    /// 下一段重新构建。取代旧的 FastSyncMapKeyResolver 线性扫描——直接 O(1) 查询。
    /// 单机 / 无同步点段：保持 null，所有反查直接短路。
    /// </summary>
    private Dictionary<int, string?>? _wpIdxToSyncIdCache;

    public CombatScenes? _combatScenes;
    // private readonly Dictionary<string, string> _actionAvatarIndexMap = new();

    private DateTime _elementalSkillLastUseTime = DateTime.MinValue;
    private DateTime _useGadgetLastUseTime = DateTime.MinValue;
    /// <summary>
    /// 玛薇卡跳飞上次执行时间，用于限制执行间隔（至少1.5秒）
    /// </summary>
    private DateTime _lastMwkFlyJumpTime = DateTime.MinValue;

    private const int RetryTimes = 2;
    private int _inTrap = 0;

    //记录当前相关点位数组
    public (int, List<WaypointForTrack>) CurWaypoints { get; set; }

    //记录当前点位
    public (int, WaypointForTrack) CurWaypoint { get; set; }

    //记录恢复点位数组
    private (int, List<WaypointForTrack>) RecordWaypoints { get; set; }

    //记录恢复点位
    private (int, WaypointForTrack) RecordWaypoint { get; set; }

    //跳过除走路径以外的操作
    private bool _skipOtherOperations = false;

    // 最近一次获取派遣奖励的时间
    private DateTime _lastGetExpeditionRewardsTime = DateTime.MinValue;

//记录上一个节点
    private WaypointForTrack? _lastWaypoint = null;

    /// <summary>
    /// 联机"基于经验判断停止锄地"当前战斗节点的经验检测器（multiplayer-hoeing-exp-cap-stop）。
    /// 仅 MultiplayerCoordinator.IsExpCapStopEnabled 时创建；战斗节点正常走完在 _lastWaypoint 赋值处
    /// 消费结果 + 释放；创建前防御式释放上一节点残留实例（重试/异常路径泄漏兜底）。
    /// 后台循环 _linkedCts 链接寻路 ct，任务结束 ct 取消即停，无长期泄漏。
    /// </summary>
    private BetterGenshinImpact.GameTask.AutoFight.IExperienceDetector? _expCapDetector = null;
    
    // 朝向标记位
    private bool _faceToMark = false;
    
    //当到达恢复点位
    public void TryCloseSkipOtherOperations()
    {
        // Logger.LogWarning("判断是否跳过地图追踪:" + (CurWaypoint.Item1 < RecordWaypoint.Item1));
        if (RecordWaypoints == CurWaypoints && CurWaypoint.Item1 < RecordWaypoint.Item1)
        {
            return;
        }

        if (_skipOtherOperations)
        {
            Logger.LogWarning("已到达上次点位，地图追踪功能恢复");
        }

        _skipOtherOperations = false;
    }

    //记录点位，方便后面恢复
    public void StartSkipOtherOperations()
    {
        // Logger.LogWarning("记录恢复点位，地图追踪将到达上次点位之前将跳过走路之外的操作 {t} - {t2}",PathingConditionConfig.AutoEatCount,CurWaypoints);
        _skipOtherOperations = true;
        RecordWaypoints = CurWaypoints;
        RecordWaypoint = CurWaypoint;
    }

    public async Task Pathing(PathingTask task)
    {
        var previousInstance = CurrentActiveInstance;
        CurrentActiveInstance = this;
        try
        {
        // SuspendableDictionary;
        const string sdKey = "PathExecutor";
        var sd = RunnerContext.Instance.SuspendableDictionary;
        sd.Remove(sdKey);
        
        RunnerContext.Instance.SuspendableDictionary.TryAdd(sdKey, pathExecutorSuspend);

        if (!task.Positions.Any())
        {
            Logger.LogWarning("没有路径点，寻路结束");
            return;
        }

        // 切换队伍
        if (!await SwitchPartyBefore(task))
        {
            return;
        }

        // 校验路径是否可以执行
        if (!await ValidateGameWithTask(task))
        {
            return;
        }

        InitializePathing(task);
        
        // 转换、按传送点分割路径
        var waypointsList = ConvertWaypointsForTrack(task.Positions, task);

        // 联机模式：预计算所有战斗点的集合点映射
        // key = listIdx * 10000 + syncPointIdx（集合点索引），value = syncPointId
        // route-variant-sync-by-logical-id spec / R2：自动 vs 手动模式分流。
        // 整条路线执行期间不切换模式（R2.4）。
        if (MultiplayerCoordinator != null)
        {
            _syncPointMap = new Dictionary<int, string?>();
            if (PathingTaskHelper.IsManualMode(task))
            {
                BuildSyncPointMapManual(task, waypointsList);   // R3 新拼法
            }
            else
            {
                BuildSyncPointMapAuto(task, waypointsList);     // 现有 SyncPointResolver 逻辑（R4 零回归）
            }
        }

        await Delay(100, ct);
        Navigation.WarmUp(task.Info.MapMatchMethod);
        
        await InitializeAutoEat();//初始化自动吃药
        PathingConditionConfig.PartyConfigBackUp = PartyConfig;
        // Logger.LogError("开始寻路{t1}-{t2}",PathingConditionConfig.PartyConfigBackUp.RecoverAvatarIndex,PartyConfig.RecoverAvatarIndex);

        foreach (var waypoints in waypointsList) // 按传送点分割的路径
        {
            // 重置同步点到达标记（新线路开始）
            _syncPointReached = false;
            // 防御性重置：新段开始时清空复苏信号位，避免跨段残留
            // （正常情况下消费点会清理，但显式重置确保语义清晰）
            MultiplayerRevivalGate.Reset(ref _multiplayerRevivalDetected);

            // === 线路重试模式 v2（§0）：段级标志复位（新段开始）===
            // 每段独立判定"本段有人死过 / 本段已重跑过"，避免跨段残留。
            // 非 retry-route 这些标志恒 false，不参与任何逻辑。
            _segmentDeathForRerun = false;
            _segmentRerunDone = false;
            _revivedGoToSegExitBarrier = false;

            // === 段级抢报缓存构建（fastsync-redesign-parameter-passing spec / OQ-1=c）===
            // 把 _syncPointMap 中属于本段的项投影到 wpIdx 索引，O(1) 查询。
            // 单机 / 无同步点段：保持 null，所有反查直接短路。
            _wpIdxToSyncIdCache = null;
            if (MultiplayerCoordinator != null && _syncPointMap.Count > 0)
            {
                var __segIdx = waypointsList.FindIndex(wps => wps == waypoints);
                var __cache = new Dictionary<int, string?>();
                foreach (var kv in _syncPointMap)
                {
                    var __listIdx = kv.Key / 10000;
                    var __wpIdx = kv.Key % 10000;
                    if (__listIdx == __segIdx) __cache[__wpIdx] = kv.Value;
                }
                _wpIdxToSyncIdCache = __cache;
            }

            // === 集体卡死跳段消费点 2（multiplayer-mutual-wait-collective-skip §8.7 / OQ-6 A）===
            // 段切换前消费一次：避免跨段残留信号位 + 段开始前若已收到跳段请求立即处理。
            // 段切换点不在 MoveForward 持按状态，故无需 KeyUp。
            if (MultiplayerCoordinator != null
                && MultiplayerCoordinator.TryConsumeRemoteSkipSignal(out var segSkipTarget))
            {
                Logger.LogWarning("[联机] 段循环切换点收到大部队跳段请求，target={Target}，前往七天神像回血", segSkipTarget);
                await TpStatueOfTheSeven(requireLoadingScreen: true);
                throw new RetryException("[联机] 大部队请求跳段");
            }

            CurrentRouteIndex = waypointsList.FindIndex(wps => wps == waypoints);

            // === 实时中断检查（multiplayer-abort-and-realign spec）===
            // 在每个路线段开始时检查是否收到中断指令
            if (MultiplayerCoordinator?.IsAbortRequested == true)
            {
                Logger.LogWarning("[联机] 路线段循环中检测到中断指令，停止当前路线执行");
                SkipRouteRequested = true;
                SkipRouteReason = "收到中断重对齐指令";
                break;
            }
            
            // 联机模式成员：上一个段设置了 SkipToNextSegment，重置标志位，继续到本段（自动传送）
            if (SkipToNextSegment)
            {
                SkipToNextSegment = false;
                Logger.LogInformation("[联机] 跳到新路线段（段{Idx}），传送后在同步点前恢复正常状态", waypointsList.FindIndex(wps => wps == waypoints));
                // 标记：本段第一个同步点等待前需要上报 Normal
                _needReportNormalBeforeSync = true;
            }

            // 联机模式房主：SkipRouteRequested 已设置，跳过剩余所有段
            if (SkipRouteRequested)
            {
                Logger.LogInformation("[联机] 房主跳过路线，跳过剩余段");
                break;
            }

            AutoFightTask.IsTpForRecover = false;
            _faceToMark = false;
            CurWaypoints = (waypointsList.FindIndex(wps => wps == waypoints), waypoints);

            for (var i = 0; i < RetryTimes; i++)
            {
                try
                {
                    ResetRecoveryStateForNewAttempt();
                    
                    await ResolveAnomalies(); // 异常场景处理

                    // 如果首个点是非TP点位，强制设置在这个点位附近优先做局部匹配
                    if (waypoints[0].Type != WaypointType.Teleport.Code)
                    {
                        Navigation.SetPrevPosition((float)waypoints[0].X, (float)waypoints[0].Y);
                    }
                    
                    Waypoint? nextWaypoint = null;
                    double? nextDdistance = null;
                    var last2Waypoints = false;
                    foreach (var waypoint in waypoints) // 一条路径
                    {
                        // === 实时中断检查（multiplayer-abort-and-realign spec）===
                        // 在每个路径点迭代时检查是否收到中断指令
                        if (MultiplayerCoordinator?.IsAbortRequested == true)
                        {
                            Logger.LogWarning("[联机] 路径点循环中检测到中断指令，停止当前路线执行");
                            SkipRouteRequested = true;
                            SkipRouteReason = "收到中断重对齐指令";
                            break;
                        }

                        // === 联机模式：已倒下复苏信号（来自 AnomalyDetector 色块检测）===
                        // 把异步事件转为 RetryException，统一走"同步点前/后"异常处理流程：
                        //   - 同步点后 → 上报 Reviving + 跳到下一段
                        //   - 同步点前 → 重试本段，3 次失败后跳下一段
                        // 此为兜底路径：战斗结束钩子和脱困入口未能消费时由这里兜底处理
                        if (TryConsumeRevivalSignal())
                        {
                            bool __retryForce = _pendingRevivalEscalation == (int)BetterGenshinImpact.GameTask.AutoHoeing.Services.RevivalEscalationAction.RetrySegment;
                            Logger.LogWarning("[联机] 主循环兜底路径检测到复苏信号，前往七天神像回血{Force}", __retryForce ? "（重试模式强制去神像）" : "");
                            await TpStatueOfTheSeven(requireLoadingScreen: MultiplayerCoordinator != null, forceStatue: __retryForce);
                            throw new RetryException("联机：主循环检测到已倒下复苏，神像回血后按异常处理");
                        }
                        
                        CurWaypoint = (waypoints.FindIndex(wps => wps == waypoint), waypoint);

                        // return-to-point-stale-prev-position-drift-fix (d) 线路中间节点首帧播种（进入该节点一次，Q8）：
                        // 区别于上方第 0 个 waypoint 首帧播种（waypoints[0] 非 TP 时已 SetPrevPosition）——此处处理 i>=1 的中间节点。
                        // 用"上一个 waypoint"坐标播种（角色刚从那走来，比目标节点 waypoint 更接近真实位置），
                        // 避免中间节点前恰好识别中断时局部匹配/异常 catch 沿用残留（BC3，低概率）。
                        // 当前/上一个 waypoint 任一为 TP 点则跳过（TP 后首帧由 pathexecutor-teleport-fresh-position-fallback-fix 覆盖）。
                        // 仅 SetPrevPosition 覆写 prev，绝不 Navigation.Reset()。识别成功立即用真值刷新 → 单机零回归。
                        {
                            var __curIdx = CurWaypoint.Item1;
                            if (__curIdx >= 1)
                            {
                                var __prevWp = waypoints[__curIdx - 1];
                                // 排除 orientation 朝向点：朝向点不移动角色，角色仍停在上一帧真实位置，
                                // 不能用 waypoint 坐标播种锚点（否则局部匹配锚错到目标点附近，导致坐标漂移、朝向角度跳变走歪）。
                                // 仅对会真实移动角色的节点做中间节点播种。
                                if (waypoint.Type != WaypointType.Teleport.Code
                                    && __prevWp.Type != WaypointType.Teleport.Code
                                    && waypoint.Type != WaypointType.Orientation.Code)
                                {
                                    var __seed = KazuhaCollectPositionGuardDecisions.ComputeMidRouteSeedAnchor(
                                        __prevWp.X, __prevWp.Y, waypoint.X, waypoint.Y);
                                    Navigation.SetPrevPosition((float)__seed.X, (float)__seed.Y);
                                }
                            }
                        }

                        // === 抢报 syncId 反查（fastsync-redesign-parameter-passing spec / OQ-1=c）===
                        // 段级缓存 _wpIdxToSyncIdCache 中查命中的 syncId（None 时为 null）。
                        // 透传给 MoveTo / MoveCloseTo / HandleTeleportWaypoint，函数内部循环
                        // 命中距离阈值时 inline 抢报。单机 / 缓存 null 时整链路短路。
                        string? __fastSyncId = null;
                        if (MultiplayerCoordinator != null && _wpIdxToSyncIdCache != null)
                        {
                            _wpIdxToSyncIdCache.TryGetValue(CurWaypoint.Item1, out __fastSyncId);
                        }

                        // === 段内"下一个还没抢报"的 syncPoint 反查（修复"非传送同步点不提前抢报"）===
                        // 走 wp0/wp1/wp2 时也要让 MoveTo 内部循环用「到 wp4(战斗+sync) 的距离」判定，
                        // 距离收敛到 FastSyncPathingDistance 时抢报 wp4 的 syncId。
                        string? __nextPendingSyncId = null;
                        WaypointForTrack? __nextPendingSyncWaypoint = null;
                        if (MultiplayerCoordinator != null && _wpIdxToSyncIdCache != null && _wpIdxToSyncIdCache.Count > 0)
                        {
                            // 找：当前段内 wpIdx >= CurWaypoint.Item1 && syncId != null && coordinator 还没记为已抢报
                            int __bestWpIdx = int.MaxValue;
                            string? __bestSyncId = null;
                            foreach (var __kv in _wpIdxToSyncIdCache)
                            {
                                if (__kv.Value == null) continue;
                                if (__kv.Key < CurWaypoint.Item1) continue;
                                if (MultiplayerCoordinator.IsFastReported(__kv.Value)) continue;
                                // strict 门控（hoeing-strict-syncpoint-no-lookahead-preclaim spec）：
                                // strict 同步点仅在该节点本身允许 look-ahead，前序节点跳过（继续向后找非 strict 候选，OQ-1 方案 a）。
                                if (!FastSyncDecisions.IsLookAheadAllowedForCandidate(__kv.Value, __kv.Key, CurWaypoint.Item1)) continue;
                                if (__kv.Key < __bestWpIdx)
                                {
                                    __bestWpIdx = __kv.Key;
                                    __bestSyncId = __kv.Value;
                                }
                            }
                            if (__bestSyncId != null && __bestWpIdx < waypoints.Count)
                            {
                                __nextPendingSyncId = __bestSyncId;
                                __nextPendingSyncWaypoint = waypoints[__bestWpIdx];
                            }
                        }

                        //计算下一个节点到当前节点的距离
                        nextWaypoint = waypoint == waypoints.Last() ? null : waypoints[waypoints.IndexOf(waypoint) + 1];
                        if (nextWaypoint != null)
                        {
                           nextDdistance = Navigation.GetDistance(waypoint, new Point2f((float)nextWaypoint.X, (float)nextWaypoint.Y));
                           // if (nextDdistance < 20 && waypoint.MoveMode == MoveModeEnum.Dash.Code)
                           // {
                           //     Logger.LogWarning("测试：下一个节点距离很近，切换到行走");
                           //     waypoint.MoveMode = MoveModeEnum.Walk.Code;
                           // }
                        }
                        
                        TryCloseSkipOtherOperations();

                        // fastsync-preclaim-fires-after-rendezvous-fix（OQ-1=方案甲）：
                        // 形式集合等待块已从此处（迭代顶部、MoveTo 之前）挪到下方非传送分支的
                        // MoveTo/MoveCloseTo 之后、Action 块之前。原因：对「集合点紧跟传送、无前置
                        // 移动段」路线，顶部等待会先于任何对该 syncId 的抢报触发，导致抢报晚于集合完成。
                        // 挪动后玩家先走向集合点（MoveTo 内对 fastSyncWaypoint 的抢报先行），到达后再等全员。

                        await RecoverWhenLowHp(waypoint,PartyConfig.RedBloodSwitchOnly); // 低血量恢复

                        if (waypoint.Type == WaypointType.Teleport.Code)
                        {
                            if (CurWaypoints.Item1 > 0)
                            {
                                var prevWaypoints = waypointsList[CurWaypoints.Item1 - 1];
                                var prevWaypoint = prevWaypoints[prevWaypoints.Count - 1];
                                if (prevWaypoint.Type == WaypointType.Teleport.Code
                                    || prevWaypoint.Action == ActionEnum.Fight.Code
                                    || prevWaypoint.Action == ActionEnum.NahidaCollect.Code
                                    || prevWaypoint.Action == ActionEnum.PickAround.Code)
                                {
                                    // No delay
                                }
                                else
                                {
                                    await Delay(1000, ct);
                                }
                            }
                            // 联机模式：把传送类异常（TpPointNotActivate、tpTask 内部 5 次重试耗尽抛出的 InvalidOperationException）
                            // 转成 RetryException，进入"同步点前/后"异常处理框架（重试 3 次后跳到下一段，而不是跳整个 JSON）。
                            // 单机模式：保留原有行为，让异常自然上抛。
                            //
                            // 抢报 syncId 反查（fastsync-redesign-parameter-passing spec / OQ-1=c）：
                            // 段级缓存中查命中的传送同步点 syncId（None 时为 null），
                            // 透传给 HandleTeleportWaypoint → TpTask.Tp，TpTask.IsLoadingScreen 命中时
                            // 内联抢报。单机 / 缓存 null 时整链路短路。
                            // fastsync-claim-respect-enable-toggle 修复：仅当用户开启"快速同步点抢报"
                            // 开关时才透传抢报 syncId；关闭时置 null → TpTask loading 命中也不 fire-and-forget。
                            // 传送后的严格等待（line ~666 tpSyncId）走独立查询、不受此开关影响。
                            string? __tpFastSyncId = null;
                            if (MultiplayerCoordinator != null
                                && _wpIdxToSyncIdCache != null
                                && MultiplayerCoordinator.EffectiveConfig.FastSyncPointEnabled)
                            {
                                _wpIdxToSyncIdCache.TryGetValue(CurWaypoint.Item1, out __tpFastSyncId);
                            }

                            // 按线路切角色（hoeing-multiplayer-per-route-switch-roles，R10.1/OQ-8）：
                            // 本线路首个 teleport 且配了切角色 → 抑制抢报（syncId 透传 null，先切角色再上报到达）。
                            // PerRouteSwitchHook==null 或本线路无切换时 ResolveFastSyncIdForWaypoint 恒等返回原值，逐字节不变。
                            bool __isFirstTeleportForSwitch = PerRouteSwitchHook is { RouteHasSwitch: true }
                                                              && !_perRouteSwitchDone && !_syncPointReached;
                            __tpFastSyncId = BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.PerRouteSwitchRolesDecisions
                                .ResolveFastSyncIdForWaypoint(__tpFastSyncId,
                                    PerRouteSwitchHook?.RouteHasSwitch ?? false, __isFirstTeleportForSwitch);

                            // 按周期吃食物（multiplayer-hoeing-auto-eat-food-by-period，检测时机=传送 loading 间隙）：
                            // 算本次吃药计划；ShouldEat 时把 __tpFastSyncId 置 null 抑制该传送点快速抢报（等吃完再严格上报）。
                            // 单机 / MultiplayerCoordinator==null 时不计算，plan 保持 Empty，__tpFastSyncId 逐字节不变（单机零回归）。
                            _pendingMedicineEatPlan = BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.MedicineEatPlan.Empty;
                            // 线路重试模式 v2（§0）：重跑本段起点跳过按周期吃食物（已回神像满血，重跑再吃药多余且浪费时间）。
                            // 非重跑路径 _rerunSuppressExtrasOnce 恒 false，吃药逻辑逐字节不变。
                            if (MultiplayerCoordinator != null && !_rerunSuppressExtrasOnce)
                            {
                                var __medCfg = MultiplayerCoordinator.EffectiveConfig;
                                var __medCd = TaskContext.Instance().Config.MedicineEatCdConfig;
                                var __medRouteName = System.IO.Path.GetFileNameWithoutExtension(task.FileName ?? string.Empty);
                                var __medRows = new System.Collections.Generic.List<BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.MedicineEatDecisions.FoodRow>
                                {
                                    new(__medCfg.MedicineFoodSlot1, __medCfg.MedicineFoodPeriod1, __medCfg.MedicineFoodRouteKeywords1),
                                    new(__medCfg.MedicineFoodSlot2, __medCfg.MedicineFoodPeriod2, __medCfg.MedicineFoodRouteKeywords2),
                                    new(__medCfg.MedicineFoodSlot3, __medCfg.MedicineFoodPeriod3, __medCfg.MedicineFoodRouteKeywords3),
                                    new(__medCfg.MedicineFoodSlot4, __medCfg.MedicineFoodPeriod4, __medCfg.MedicineFoodRouteKeywords4),
                                };
                                _pendingMedicineEatPlan = BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.MedicineEatDecisions.BuildPlan(
                                    __medRows,
                                    __slot => __medCd.GetLastEatTime(__slot),
                                    DateTime.UtcNow,
                                    isMultiplayer: __medCfg.MultiplayerEnabled,
                                    isConnected: MultiplayerCoordinator.IsConnected,
                                    currentRouteName: __medRouteName);
                                __tpFastSyncId = BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.MedicineEatDecisions
                                    .ResolveFastSyncIdForMedicine(__tpFastSyncId, _pendingMedicineEatPlan.ShouldEat);
                            }

                            if (MultiplayerCoordinator != null)
                            {
                                try
                                {
                                    await HandleTeleportWaypoint(waypoint, fastSyncId: __tpFastSyncId);
                                }
                                catch (TaskCanceledException) { throw; }
                                catch (NormalEndException) { throw; }
                                catch (HandledException) { throw; }
                                catch (RetryException) { throw; }
                                catch (RetryNoCountException) { throw; }
                                catch (Exception tpEx)
                                {
                                    Logger.LogWarning("[联机] 传送失败，转为 RetryException 进入异常处理流程，原因: {Msg}", tpEx.Message);
                                    throw new RetryException($"传送失败：{tpEx.Message}");
                                }
                            }
                            else
                            {
                                await HandleTeleportWaypoint(waypoint);
                            }
                            
                            // 标记同步点已到达（第一个传送点）
                            if (!_syncPointReached)
                            {
                                _syncPointReached = true;
                            }
                            
                            if (_lastWaypoint == null || waypoint.MapName != _lastWaypoint.MapName)
                            {
                                // Logger.LogInformation("线路切换，强制校验");
                                await ValidateGameWithTask(task,true);
                            }

                            // 按线路切角色（hoeing-multiplayer-per-route-switch-roles，R4.1/R10.3/R10.4）：
                            // 传送完成 → 先切角色 → 切角色成功后再走下方 WaitForAllPlayers 上报到达。
                            // PerRouteSwitchHook==null 或本线路无切换或已切过 → 整块短路，不换角色路径逐字节不变。
                            // 线路重试模式 v2（§0）：重跑本段起点跳过按线路切角色（已切过 / 重跑无需重复换人）。
                            if (PerRouteSwitchHook is { RouteHasSwitch: true } __perRouteHook
                                && !_perRouteSwitchDone
                                && !_rerunSuppressExtrasOnce)
                            {
                                _perRouteSwitchDone = true;   // 每线路一次性（实例级，R4.3）
                                // 换角色期间抑制后台拾取输入（滚轮/按F），避免在配队筛选界面误滚动元素图标
                                BetterGenshinImpact.GameTask.AutoHoeing.Services.TemplatePickupService.SuppressPickupInput = true;
                                // 换角色期间暂停 WorldStateMonitor 被踢出检测，避免选角界面 IsInMultiGame=false 被误判踢出（回主页/退出）
                                WorldStateMonitor?.BeginRoleSwitch();
                                try
                                {
                                    Logger.LogInformation("[联机][按线路切角色] 本线路首个传送点到达，先切角色再上报到达");
                                    await __perRouteHook.SwitchAsync(ct);
                                }
                                catch (OperationCanceledException) { throw; }   // R4.5/R10 取消透传
                                catch (Exception perRouteEx)
                                {
                                    // R4.4/R10.5：切角色失败记警告后仍继续 → 下方 WaitForAllPlayers 仍会上报到达，
                                    // 避免该成员永不上报导致队友无限等待。
                                    Logger.LogWarning(perRouteEx, "[联机][按线路切角色] 切角色失败，继续上报到达避免队友空等");
                                }
                                finally
                                {
                                    WorldStateMonitor?.EndRoleSwitch();
                                    BetterGenshinImpact.GameTask.AutoHoeing.Services.TemplatePickupService.SuppressPickupInput = false;
                                }
                            }

                            // 线路重试模式 v2（§0）：段起点两个多余动作（吃药 build + 切角色）已消费本标志，就地复位。
                            // 放在切角色块之后、吃药执行块之前——吃药执行读的是已被 build 跳过（Empty）的 _pendingMedicineEatPlan，不再读本标志。
                            _rerunSuppressExtrasOnce = false;

                            // 按周期吃食物执行（multiplayer-hoeing-auto-eat-food-by-period，执行时机=传送同步点）：
                            // 传送完成 → 若本次需吃药 → 打开食物页依次吃各到期格 → 仅当食物页成功打开才对每格写全局 CD 时间戳 →
                            // 随后落到下方既有 WaitForAllPlayers 严格上报（抢报已在传送前抑制）。
                            // plan.ShouldEat==false 或单机（plan 恒 Empty）时整块短路，不吃药路径逐字节不变。
                            if (MultiplayerCoordinator != null && _pendingMedicineEatPlan.ShouldEat)
                            {
                                var __medSlots = _pendingMedicineEatPlan.FoodSlots;
                                // 吃药打开食物页期间抑制后台拾取输入（滚轮/按F 会滚动食物格）与异常检测输入（ESC/空格会关背包），
                                // 复用换人块同一静态标志（TemplatePickupService.SuppressPickupInput，AnomalyDetector 也查它）。
                                BetterGenshinImpact.GameTask.AutoHoeing.Services.TemplatePickupService.SuppressPickupInput = true;
                                WorldStateMonitor?.BeginMedicineEat();   // 吃药开背包 IsInMultiGame=false 属合法窗口，抑制被踢误判
                                try
                                {
                                    var __medExecutor = new BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.MedicineEatExecutor();
                                    var __medResult = await __medExecutor.EatFoodAsync(__medSlots, ct);
                                    if (__medResult.Opened)
                                    {
                                        var __medCd2 = TaskContext.Instance().Config.MedicineEatCdConfig;
                                        var __medNow = DateTime.UtcNow;
                                        foreach (var __medSlot in __medSlots) __medCd2.SetLastEatTime(__medSlot, __medNow);
                                        Logger.LogInformation("[按周期吃食物] 已吃 {N} 格食物并更新 CD 时间戳", __medSlots.Count);

                                        // 恢复药格（需选角、不支持）→ 本次运行把对应配置行周期置 0，后续不再吃该格（仅改运行时 EffectiveConfig，不落盘）。
                                        if (__medResult.RecoverySlots.Count > 0)
                                        {
                                            var __medCfgRt = MultiplayerCoordinator.EffectiveConfig;
                                            foreach (var __recSlot in __medResult.RecoverySlots)
                                            {
                                                if (__medCfgRt.MedicineFoodSlot1 == __recSlot) __medCfgRt.MedicineFoodPeriod1 = 0;
                                                if (__medCfgRt.MedicineFoodSlot2 == __recSlot) __medCfgRt.MedicineFoodPeriod2 = 0;
                                                if (__medCfgRt.MedicineFoodSlot3 == __recSlot) __medCfgRt.MedicineFoodPeriod3 = 0;
                                                if (__medCfgRt.MedicineFoodSlot4 == __recSlot) __medCfgRt.MedicineFoodPeriod4 = 0;
                                            }
                                            Logger.LogInformation("[按周期吃食物] {N} 个恢复药格已在本次运行内停用（周期置0）", __medResult.RecoverySlots.Count);
                                        }
                                    }
                                    else
                                    {
                                        Logger.LogWarning("[按周期吃食物] 食物页未成功打开，本次不更新 CD，下个传送点自然重试");
                                    }
                                }
                                catch (OperationCanceledException) { throw; } // 取消透传
                                catch (Exception __medEx)
                                {
                                    // 可恢复：吃药失败不应中断锄地主流程，也不写 CD；落到下方严格上报避免队友空等
                                    Logger.LogWarning(__medEx, "[按周期吃食物] 执行异常，跳过本次吃药，继续上报同步点");
                                }
                                finally
                                {
                                    BetterGenshinImpact.GameTask.AutoHoeing.Services.TemplatePickupService.SuppressPickupInput = false; // 复位抑制（与换人块一致）
                                    WorldStateMonitor?.EndMedicineEat();
                                    _pendingMedicineEatPlan = BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.MedicineEatPlan.Empty; // 清标记
                                }
                            }

                            // 联机模式：传送完成后检查是否需要同步等待
                            // 异常等待点：强制等待，不依赖 SyncAtEveryTeleport 配置
                            // 正常同步点：按配置决定是否等待
                            if (MultiplayerCoordinator != null)
                            {
                                // 异常恢复后，在同步点等待前上报 Normal（恢复参与全员判定）
                                if (_needReportNormalBeforeSync)
                                {
                                    _needReportNormalBeforeSync = false;
                                    Logger.LogInformation("[联机] 异常恢复：在同步点前上报 Normal，恢复全员等待判定");
                                    try { await MultiplayerCoordinator.ReportMemberStatusAsync(MemberStatus.Normal); } catch { }
                                }

                                // 优先检查服务端指令的等待点（multiplayer-abnormal-wait-coordination）
                                if (MultiplayerCoordinator.HasPendingWaitPoint)
                                {
                                    var pendingPoint = MultiplayerCoordinator.GetPendingWaitPoint();
                                    if (pendingPoint != null && pendingPoint.IsForced)
                                    {
                                        var progress = ComputeProgress(CurWaypoints.Item1, CurWaypoint.Item1);
                                        Logger.LogInformation("[联机] 传送完成，检测到服务端指令的统一等待点 {SyncId}，强制等待", pendingPoint.SyncPointId);
                                        await MultiplayerCoordinator.WaitForAllPlayers(pendingPoint.SyncPointId, ct, progress);
                                        Logger.LogInformation("[联机] 服务端指令等待点同步完成，继续前进，syncId={SyncId}", pendingPoint.SyncPointId);
                                        MultiplayerCoordinator.ClearPendingWaitPoint();
                                    }
                                }
                                else
                                {
                                    var tpMapKey = CurWaypoints.Item1 * 10000 + CurWaypoint.Item1;
                                    if (_syncPointMap.TryGetValue(tpMapKey, out var tpSyncId) && tpSyncId != null)
                                    {
                                        var tpProgress = ComputeProgress(CurWaypoints.Item1, CurWaypoint.Item1);
                                        // 检查是否是异常等待点
                                        bool isAbnormalWaitingPoint = MultiplayerCoordinator.IsAbnormalWaitingAtPoint(tpSyncId);
                                        
                                        // 异常等待点：强制同步等待
                                        if (isAbnormalWaitingPoint)
                                        {
                                            Logger.LogInformation("[联机] 传送完成，进入异常等待点 {SyncId}，强制等待，进度={P}", tpSyncId, tpProgress);
                                            await MultiplayerCoordinator.WaitForAllPlayers(tpSyncId, ct, tpProgress);
                                            Logger.LogInformation("[联机] 异常等待点同步完成，继续前进，syncId={SyncId}", tpSyncId);
                                        }
                                        // 正常同步点：统一等待（传送必等待默认启用）
                                        else
                                        {
                                            // === 落后追赶判定（hoeing-multiplayer-lagging-member-catchup spec / 关键问题 1 + BUG-C/D/E）===
                                            // 仅此处（段起点传送点正常同步块=段边界）插入；集合点/异常点/强制点均不插入（BUG-E）。
                                            // 两项本地标志守卫：异常恢复未占用 SkipToNextSegment、本轮无待收尾跳段（关键问题 4）。
                                            // mySeg 用本地实时 ComputeProgress（不读缓存）。判定纯同步读内存、不引入不可取消阻塞。
                                            //
                                            // 线路重试模式 v2（§0）：重跑本段时豁免"该次"判定并消费复位——重跑是主动回退重打，
                                            // 进度低于继续前进的队友是预期的，不是真正落后；不豁免会被直接跳段踹走重跑成果。
                                            if (_exemptNextLaggingCatchUpOnce)
                                            {
                                                _exemptNextLaggingCatchUpOnce = false;
                                                Logger.LogInformation("[联机][重试模式] 重跑本段：豁免本次落后追赶判定（主动回退重打，非真正落后），syncId={SyncId}", tpSyncId);
                                            }
                                            else if (!SkipToNextSegment && !_needReportNormalBeforeSync)
                                            {
                                                long mySeg = ComputeProgress(CurWaypoints.Item1, CurWaypoint.Item1);
                                                if (MultiplayerCoordinator.TryGetLaggingCatchUpDecision(mySeg))
                                                {
                                                    // ① fire-and-forget 上报本段进度（推进服务端 CurrentProgress，避免 BUG-C 大部队空等）
                                                    await MultiplayerCoordinator.FastReportAsync(tpSyncId, tpProgress);
                                                    // ② 抛 LaggingCatchUpSkipException 跳出 try 路点循环，由 catch(RetryException) 最前面的
                                                    //    非异常跳段分支处理（置 SkipToNextSegment + break）。不用 continue/裸 break/throw 普通 RetryException（BUG-D）。
                                                    Logger.LogWarning("[落后追赶] 段同步点 {SyncId} 判定落后，已 fire-and-forget 上报本段进度，抛 LaggingCatchUpSkipException 跳段，mySeg={My}", tpSyncId, mySeg);
                                                    throw new LaggingCatchUpSkipException("[落后追赶] 段级落后，跳段追赶");
                                                }
                                            }

                                            Logger.LogInformation("[联机] 传送完成，等待所有玩家同步，syncId={SyncId}, 进度={P}", tpSyncId, tpProgress);
                                            await MultiplayerCoordinator.WaitForAllPlayers(tpSyncId, ct, tpProgress);
                                            Logger.LogInformation("[联机] 传送同步完成，继续前进，syncId={SyncId}", tpSyncId);
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            await BeforeMoveToTarget(waypoint);
                            
                            // Path不用走得很近，Target需要接近，但都需要先移动到对应位置
                            if (waypoint.Type == WaypointType.Orientation.Code)
                            {
                                // 方位点，只需要朝向&& !(waypoint.Type == "orientation" && _lastWaypoint?.Action == ActionEnum.Fight.Code)
                                // 考虑到方位点大概率是作为执行action的最后一个点，所以放在此处处理，不和传送点一样单独处理
                                if ((_lastWaypoint?.Action == ActionEnum.Fight.Code || last2Waypoints) && nextDdistance < 20  && waypoint.Misidentification.HandlingMode != "mapRecognition")//&& nextWaypoint == null
                                {
                                    last2Waypoints = true;
                                    // Logger.LogWarning("战斗后节点较近！！-1");
                                }
                                else
                                {
                                    last2Waypoints = false;
                                    // Logger.LogWarning("战斗后节点较近！！-1222");
                                    await FaceTo(waypoint);
                                }
                                // await FaceTo(waypoint);
                            }
                            else if (waypoint.Type == WaypointType.ActionOnly.Code)
                            {
                                Logger.LogInformation("执行 {t}","ActionOnly");
                                // 找到当前出战角色
                                 using var ra = CaptureToRectArea();
                                 for (int k = 1; k <= 4; k++)
                                 {
                                     var avatar = _combatScenes?.SelectAvatar(k);
                                     if (avatar != null && avatar.IsActive(ra))
                                     {
                                         Logger.LogInformation("当前出战角色 {t}",avatar.Name);
                                         if (!string.IsNullOrEmpty(waypoint.ActionParams))
                                         {
                                             // waypoint.ActionParams = avatar.Name + " " + waypoint.ActionParams;
                                         }
                                         break;
                                     }
                                 }
                            }
                            else if (waypoint.Action != ActionEnum.UpDownGrabLeaf.Code)
                            {
                                // Logger.LogWarning("测试44：{t}",nextDdistance);
                                if ((_lastWaypoint?.Action == ActionEnum.Fight.Code || last2Waypoints) && nextDdistance < 20 && nextWaypoint == null&& waypoint.Misidentification.HandlingMode != "mapRecognition")
                                {
                                    last2Waypoints = true;
                                    // Logger.LogWarning("战斗后节点较近111！！-2");
                                }
                                else
                                {
                                    // Logger.LogWarning("战斗后节点较近222！！-2");
                                    last2Waypoints = false;
                                    await MoveTo(waypoint,true,task,nextWaypoint,nextDdistance, fastSyncId: __nextPendingSyncId, fastSyncWaypoint: __nextPendingSyncWaypoint);
                                }
                            }
                            
                            await BeforeMoveCloseToTarget(waypoint);

                            if (IsTargetPoint(waypoint))
                            {
                                await MoveCloseTo(waypoint, fastSyncId: __nextPendingSyncId, fastSyncWaypoint: __nextPendingSyncWaypoint);
                            }

                            // === 形式集合等待块（fastsync-preclaim-fires-after-rendezvous-fix / OQ-1=方案甲）===
                            // 挪动后位置：MoveTo/MoveCloseTo（走向集合点）之后、Action 块之前。
                            // 守卫保持 waypoint.Type != Teleport（此分支本就是非传送），__fastSyncId 复用顶部反查。
                            // 语义：玩家已走到集合点（途中 MoveTo 对 fastSyncWaypoint 的抢报先行）→ 在此等全员
                            //       → 再执行本 waypoint 的 Action。形式等待原样保留（仍阻塞等全员，不删不改成非阻塞）。
                            if (MultiplayerCoordinator != null && __fastSyncId != null)
                            {
                                var progress = ComputeProgress(CurWaypoints.Item1, CurWaypoint.Item1);
                                Logger.LogInformation("[联机] 到达集合点，等待所有玩家，syncId={SyncId}, 进度={Progress}", __fastSyncId, progress);
                                await MultiplayerCoordinator.WaitForAllPlayers(__fastSyncId, ct, progress);
                                Logger.LogInformation("[联机] 集合完成，继续前进，syncId={SyncId}", __fastSyncId);
                            }

                            //skipOtherOperations如果重试，则跳过相关操作，
                            if ((!string.IsNullOrEmpty(waypoint.Action) && !_skipOtherOperations) ||
                                waypoint.Action == ActionEnum.CombatScript.Code)
                            {
                                if (waypoint.Action == ActionEnum.Fight.Code)
                                {
                                    // === 基于经验判断停止锄地：战斗节点起点创建经验检测器（multiplayer-hoeing-exp-cap-stop）===
                                    // 创建前先释放上一节点残留实例（重试/异常路径泄漏兜底），保证至多一个存活。
                                    // 检测窗口覆盖：战斗 + 战后回点 + 万叶聚物/拾取，直到 _lastWaypoint 赋值处消费。
                                    if (MultiplayerCoordinator?.IsExpCapStopEnabled == true)
                                    {
                                        _expCapDetector?.Dispose();
                                        _expCapDetector = null;
                                        if (MultiplayerCoordinator.ExpCapDetectAllExp)
                                        {
                                            // 检测所有经验（精英+小怪）：复用好感任务 exp.png 通用模板
                                            _expCapDetector = new BetterGenshinImpact.GameTask.AutoFight.AllExpDetector(ct);
                                            _expCapDetector.Start();
                                        }
                                        else
                                        {
                                            // 仅检测精英经验（57/58/60 数字模板）：复用自动战斗 ExperienceDetector
                                            var expSysInfo = TaskContext.Instance().SystemInfo;
                                            var expCaptureRect = expSysInfo.ScaleMax1080PCaptureRect;
                                            var expAssets = AutoFightAssets.Get(expCaptureRect.Width, expCaptureRect.Height);
                                            var expRos = expAssets.InitializeRecognitionObjects();
                                            if (expRos.Count > 0)
                                            {
                                                _expCapDetector = new BetterGenshinImpact.GameTask.AutoFight.ExperienceDetector(expRos, ct);
                                                _expCapDetector.Start();
                                            }
                                        }
                                    }

                                    AutoFightTask.FightWaypoint = waypoint;
                                    PathingConditionConfig.CombatScenesGoBackUp = _combatScenes;//把地图追踪的战斗CD等同步给战斗节点
                                    
                                    Task.Run( () =>{
                                        var screen2 = CaptureToRectArea();
                                        if ((_lastWaypoint?.Action != MoveModeEnum.Fly.Code && Bv.GetMotionStatus(screen2) == MotionStatus.Fly) || 
                                                screen2.SrcMat.At<Vec3b>(1028, 1584).Item0 == 255 && screen2.SrcMat.At<Vec3b>(1028, 1584).Item1 == 255 && screen2.SrcMat.At<Vec3b>(1028, 1584).Item2 == 255)
                                        {
                                            Logger.LogWarning("战斗状态下落攻击");
                                            Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                                        }
                                    });
                                    
                                }
                                else
                                {
                                    AutoFightTask.FightEndFlag = true;
                                    AutoFightTask.FightWaypoint = null;
                                }
                                // 执行 action11
                                
                                //如果上一节点和当前节点坐标一致，不执行action以避免卡死
                                // 联机模式：战斗节点上报 Fighting 状态（需求 4）
                                if (waypoint.Action == ActionEnum.Fight.Code && MultiplayerCoordinator != null)
                                {
                                    await MultiplayerCoordinator.ReportFightingStatusAsync(true);
                                    try
                                    {
                                        await AfterMoveToTarget(waypoint, nextWaypoint);
                                    }
                                    finally
                                    {
                                        // 无论战斗正常结束还是超时（AutoFightTask 内部处理），都清除 Fighting 状态
                                        await MultiplayerCoordinator.ReportFightingStatusAsync(false);
                                    }
                                }
                                else
                                {
                                    await AfterMoveToTarget(waypoint, nextWaypoint);
                                }
                                
                                if (waypoint.Action == ActionEnum.Fight.Code)
                                {
                                    if (PathingConditionConfig.CombatScenesGoBackUp is not null)
                                    {
                                        if (PathingConditionConfig.CombatScenesGoBackUp.Avatars.Select(avatar => avatar.Name).ToArray()
                                            .SequenceEqual(_combatScenes.Avatars.Select(a => a.Name).ToArray()))
                                        {
                                            Logger.LogInformation("地图追踪：继承自动战斗队伍Cd信息...");
                                            _combatScenes = PathingConditionConfig.CombatScenesGoBackUp;
                                            // foreach (var avatar in _combatScenes.GetAvatars())
                                            // {
                                            //     Logger.LogInformation("队伍角色 {Name} 当前剩余E技能CD：{Cd} 秒",
                                            //         avatar.Name,
                                            //         Math.Round(avatar.GetSkillCdSeconds(), 2));
                                            // }
                                        }
                                    }
                                    // === 联机模式：战斗中触发复苏的统一处理 ===
                                    // 优先级：先消费复苏信号 → 去神像 → 抛 RetryException 跳到下一段汇合
                                    // 设计参考 design.md §3 / bugfix.md 2.1 / 2.2 / 2.9
                                    // 钩子放在最开头：先于其他重置（MainAvatarIndex / AutoEatCount / CombatScenesGoBackUp），
                                    // 避免无谓重置后又抛异常
                                    if (TryConsumeRevivalSignal())
                                    {
                                        // 线路重试模式 v2：RetrySegment 强制去神像（forceStatue）跳过嗑药分级，保证全员真正回神像满血
                                        bool __retryForce = _pendingRevivalEscalation == (int)BetterGenshinImpact.GameTask.AutoHoeing.Services.RevivalEscalationAction.RetrySegment;

                                        // === 线路重试模式 v2（§0）：复苏者去神像前先上报 Reviving，让服务端把段出口屏障超时延到 300s，
                                        // 避免健康队友在屏障等复苏者回神像（~15-20s）时被 60s 超时误放行造成脱节。===
                                        if (__retryForce && RouteRetryModeEnabled && MultiplayerCoordinator != null)
                                        {
                                            _segmentDeathForRerun = true; // 本机复苏 → 本段有人死过
                                            long __revProgress = ComputeProgress(CurWaypoints.Item1, 0);
                                            try { await MultiplayerCoordinator.ReportMemberStatusAsync(MemberStatus.Reviving, __revProgress); } catch { }
                                        }

                                        Logger.LogWarning("[联机] 战斗中曾触发复苏（已倒下色块检测），战斗结束后前往七天神像回血{Force}", __retryForce ? "（重试模式强制去神像）" : "");
                                        await TpStatueOfTheSeven(requireLoadingScreen: MultiplayerCoordinator != null, forceStatue: __retryForce);

                                        // === 线路重试模式 v2（§0）：回神像后不立即重跑，先到"段出口屏障"与队友碰头 ===
                                        // 置"本段有人死过"标志 + 标记走屏障路径 → break 出 waypoint 循环直达段出口屏障。
                                        // 屏障保证复苏者到齐前队友不放行 → 广播必然已送达 → 全员一起重跑（确定性）。
                                        if (__retryForce && RouteRetryModeEnabled)
                                        {
                                            _segmentDeathForRerun = true;
                                            _revivedGoToSegExitBarrier = true;
                                            System.Threading.Interlocked.Exchange(ref _pendingRevivalEscalation, 0); // 消费掉，避免残留影响后续
                                            Logger.LogWarning("[联机][重试模式v2] 复苏者已回神像满血 → 前往段出口屏障与队友碰头（不单独重跑）");
                                            break; // 跳出 waypoint 循环 → 段出口屏障
                                        }

                                        throw new RetryException("联机：战斗中触发复苏，神像回血后跳到下一段汇合");
                                    }

                                    // === 集体卡死跳段消费点 3（multiplayer-mutual-wait-collective-skip §8.7 / OQ-6 A）===
                                    // 战斗结束后消费集体跳段信号位，与上面复苏信号消费点完全独立。
                                    if (MultiplayerCoordinator != null
                                        && MultiplayerCoordinator.TryConsumeRemoteSkipSignal(out var fightSkipTarget))
                                    {
                                        Logger.LogWarning("[联机] 战斗结束收到大部队跳段请求，target={Target}，前往七天神像回血", fightSkipTarget);
                                        await TpStatueOfTheSeven(requireLoadingScreen: true);
                                        throw new RetryException("[联机] 大部队请求跳段");
                                    }

                                    if(!string.IsNullOrEmpty(PartyConfig.MainAvatarIndex)) PartyConfig.MainAvatarIndex = PathingConditionConfig.InitialMainAvatarIndex;
                                    PathingConditionConfig.CombatScenesGoBackUp = null;
                                    ResetRecoveryStateForNewAttempt();

                                    // 联机模式：战斗完成后走回战斗点集合
                                    if (MultiplayerCoordinator != null)
                                    {
                                        // 读 KazuhaCollectSync.IsConfigEnabled（持有 AutoHoeingTask 拷贝/覆盖后的 _config），
                                        // 不能读 TaskContext.Instance().Config.AutoHoeingConfig（全局）：
                                        // 配置组（ScriptGroup）启动时 AutoHoeingTask 在深拷贝上应用 ApplySettingsOverride，
                                        // 全局未被覆盖；若读全局会导致"配置组开了万叶聚物但 PathExecutor 跳过聚物分支"。
                                        // 此处用 IsConfigEnabled（仅判配置）而非 IsEnabled（含 IsConnected）：
                                        // SignalR 临时断开时仍进 WaitAtFightPointAsync，由其内部走兜底 Delay，
                                        // 与原代码 if (config.EnableKazuhaSync) 行为对齐。
                                        //
                                        // 同战斗点连战跳过：当前是 Fight ∧ nextWaypoint 也是 Fight ∧ 两点距离 < 10.0 时
                                        // 跳过整个聚物流程（路线由房主同步，所有客户端 nextWaypoint 一致，决策必然全员对齐，
                                        // 不会出现万叶等其他玩家、其他玩家在等万叶的死锁）。
                                        // 两战斗点距离 ≥ 10.0 时视为独立战斗点，仍正常聚物。
                                        // 详见 PathExecutorDecisions.ShouldSkipKazuhaCollectWhenNextIsFight。
                                        var distanceToNext = nextWaypoint == null
                                            ? double.PositiveInfinity
                                            : Navigation.GetDistance(waypoint, new Point2f((float)nextWaypoint.X, (float)nextWaypoint.Y));
                                        var skipForNextFight = PathExecutorDecisions.ShouldSkipKazuhaCollectWhenNextIsFight(
                                            waypoint.Action, nextWaypoint?.Action, distanceToNext, 10.0, ActionEnum.Fight.Code);
                                        if (MultiplayerCoordinator.KazuhaCollectSync?.IsConfigEnabled == true && !skipForNextFight)
                                        {
                                            Logger.LogInformation("[联机] 战斗完成，走回战斗点集合");
                                            waypoint.Type =  WaypointType.Target.Code;

                                            // 玛薇卡摩托下车（恒开，无需配置）：
                                            // 战后回点全程持续检测摩托状态：第一次在 MoveTo 前检测（等派蒙稳定画面），
                                            // 之后启动后台定时任务每 500ms 检测一次，覆盖 MoveTo、两段 MoveCloseTo、
                                            // 等待广播的整个回点流程，直到 WaitAtFightPointAsync 完成后取消。
                                            // 三层门控保证零误触（队里有玛薇卡 + 当前出战是玛薇卡 + 摩托模板命中），
                                            // 三者缺一即跳过 → 非玛薇卡队伍 / 玛薇卡未出战 / 非摩托态一次都不按 E（不会误骑上车）。
                                            // 摩托判定用目标模板匹配（SqDiffNormed + 掩膜，见 IsMavuikaOnMotorcycleByTemplate）。
                                            // 整块 try-catch 兜底：检测/按键任何异常都不影响后续回点主流程（取消透传）。
                                            CancellationTokenSource? _mavuikaDismountCts = null;
                                            try
                                            {
                                                var __mavuika = _combatScenes?.SelectAvatar("玛薇卡");
                                                if (__mavuika != null)
                                                {
                                                    // 画面稳定门控：战斗刚结束画面未稳定，摩托图标 / 队伍栏尚未渲染，
                                                    // 直接截图会 MISS。识别前先等派蒙菜单图标出现（最多 2 秒：10×200ms），
                                                    // 确认已回到主界面、画面稳定后再截取用于 IsActive + 摩托模板匹配。
                                                    await Bv.WaitUntilFound(
                                                        ElementAssets.Instance.PaimonMenuRo, ct, retryTimes: 10, delayMs: 200);
                                                    
                                                    await Delay(500, ct);

                                                    using var __dismountRegion = CaptureToRectArea();
                                                    var __mavuikaActive = _combatScenes!.AvatarCount <= 1 || __mavuika.IsActive(__dismountRegion);
                                                    if (__mavuikaActive && IsMavuikaOnMotorcycleByTemplate(__dismountRegion))
                                                    {
                                                        Logger.LogInformation("[联机] 战后回点：检测到玛薇卡在摩托上，按 E 下车");
                                                        Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                                                        await Delay(500, ct);
                                                    }

                                                    // 启动后台定时检测任务：每 1000ms 检测一次摩托状态，覆盖整个回点流程
                                                    _mavuikaDismountCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                                    var __dismountCts = _mavuikaDismountCts; // 捕获局部变量供闭包使用
                                                    var __mavuikaRef = __mavuika;
                                                    _ = Task.Run(async () =>
                                                    {
                                                        var __lastDismountTime = DateTime.MinValue; // 上次按 E 下车的时间
                                                        while (!__dismountCts.IsCancellationRequested)
                                                        {
                                                            try
                                                            {
                                                                await Task.Delay(1000, __dismountCts.Token);
                                                                using var __loopRegion = CaptureToRectArea();
                                                                var __loopActive = _combatScenes!.AvatarCount <= 1 || __mavuikaRef.IsActive(__loopRegion);
                                                                if (__loopActive && IsMavuikaOnMotorcycleByTemplate(__loopRegion))
                                                                {
                                                                    // 下车后 1.5 秒静默期：避免重复下车
                                                                    if ((DateTime.UtcNow - __lastDismountTime).TotalMilliseconds < 1500)
                                                                    {
                                                                        continue;
                                                                    }

                                                                    Logger.LogInformation("[联机] 回点中：检测到玛薇卡在摩托上，按 E 下车");
                                                                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                                                                    __lastDismountTime = DateTime.UtcNow;

                                                                    // 启动异步任务在 1.1 秒后做一次确认：下车成功则结束循环，不再自动下车
                                                                    var __confirmCts = __dismountCts;
                                                                    var __confirmMavuika = __mavuikaRef;
                                                                    _ = Task.Run(async () =>
                                                                    {
                                                                        try
                                                                        {
                                                                            await Task.Delay(1200, __confirmCts.Token);
                                                                            using var __confirmRegion = CaptureToRectArea();
                                                                            var __confirmActive = _combatScenes!.AvatarCount <= 1 || __confirmMavuika.IsActive(__confirmRegion);
                                                                            if (!__confirmActive || !IsMavuikaOnMotorcycleByTemplate(__confirmRegion))
                                                                            {
                                                                                // 下车成功，取消循环，后续不再自动下车
                                                                                Logger.LogInformation("[联机] 回点中：下车成功，结束摩托下车检测");
                                                                                __dismountCts.Cancel();
                                                                            }
                                                                            else
                                                                            {
                                                                                Logger.LogInformation("[联机] 回点中：1.1秒后仍在摩托上，再按一次 E 下车");
                                                                                Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                                                                                __lastDismountTime = DateTime.UtcNow;
                                                                            }
                                                                        }
                                                                        catch (OperationCanceledException) { }
                                                                        catch (Exception __confirmEx)
                                                                        {
                                                                            Logger.LogWarning(__confirmEx, "[联机] 回点中：摩托下车二次确认异常");
                                                                        }
                                                                    }, __confirmCts.Token);
                                                                }
                                                            }
                                                            catch (OperationCanceledException) { break; }
                                                            catch (Exception __innerEx)
                                                            {
                                                                Logger.LogWarning(__innerEx, "[联机] 回点中：摩托下车检测异常，跳过");
                                                            }
                                                        }
                                                    }, __dismountCts.Token);
                                                }
                                            }
                                            catch (OperationCanceledException) { throw; }
                                            catch (Exception __dismountEx)
                                            {
                                                Logger.LogWarning(__dismountEx, "[联机] 战后回点：玛薇卡摩托下车检测异常，跳过下车继续回点");
                                            }

                                            // kazuha-collect-fightpoint-position-misrecognition-fix 方案 A（首帧播种）：
                                            // 走回战斗点的 MoveTo/MoveCloseTo 逐帧 GetPosition 之前，用战斗点坐标播种 Navigation 单例锚点，
                                            // 避免沿用上一段远处残留的 _prevX/_prevY 导致局部匹配锚错 / 全局 garbage（BC1/BC2）。
                                            // 仅 SetPrevPosition 覆写 prev，绝不调用 Navigation.Reset()（Navigation 是进程级共享单例，避免副作用）。
                                            var __seed = KazuhaCollectPositionGuardDecisions.ComputeSeedAnchor(waypoint.X, waypoint.Y);
                                            Navigation.SetPrevPosition((float)__seed.X, (float)__seed.Y);

                                            // multiplayer-kazuha-collect-speedup-and-position-fix:
                                            // BC3+BC4: MoveCloseTo 之前 kick off 后台预备（仅万叶玩家+缓存命中时实际工作，
                                            //          否则立即返回 PreparationResult.Skipped）。必须在 MoveCloseTo 之前
                                            //          kick off，才能与走路时间并行。
                                            var prepTask = MultiplayerCoordinator.KazuhaCollectSync.BeginPreparationAsync(ct);

                                            // multiplayer-kazuha-pre-cast-positioning EB1: 二段式接近——长距离 (>4.0) 先用 MoveTo
                                            // 真寻路 (自动赶路 / 卡死回退完整鲁棒) 粗接近，再交给 MoveCloseTo 精接近至 < 1.0；
                                            // 短距离 (<=4.0) 直接 MoveCloseTo 单段快速路径，零额外开销。
                                            // 仅本调用点显式生效，PathExecutor 其它 MoveCloseTo 调用保持原行为。
                                            // 详见 .kiro/specs/multiplayer-kazuha-pre-cast-positioning/design.md §3.6
                                            //
                                            // 注意：用 Navigation.GetPosition（公开 static）直接取小地图坐标，
                                            // 不能用 PathExecutor.GetPosition（默认 isPoint:true 会触发 ResolveAnomalies / 按 ESC）。
                                            // 位置识别失败 → 返回 (0,0) → 跳过 MoveTo 走 MoveCloseTo 单段兜底。
                                            // hoeing-kazuha-return-abnormal-coord-reseed-moveto-fix 路径 B：
                                            // __coordTrusted 门控——坐标 N 次重识别仍异常时置 false，
                                            // 跳过 MoveTo + 两段 MoveCloseTo + 广播二段，仅进 WaitAtFightPointAsync 兜底。
                                            bool __coordTrusted = true;
                                            {
                                                Point2f currentPos;
                                                try
                                                {
                                                    using var screen = CaptureToRectArea();
                                                    currentPos = Navigation.GetPosition(screen, waypoint.MapName, waypoint.MapMatchMethod);
                                                }
                                                catch (OperationCanceledException) { throw; }
                                                catch (Exception ex)
                                                {
                                                    Logger.LogDebug(ex, "[联机] 战后聚物分支距离预判位置识别失败，跳过 MoveTo 走 MoveCloseTo 兜底");
                                                    currentPos = new Point2f(0, 0);
                                                }
                                                // hoeing-kazuha-return-predistance-zero-coord-skip-moveto-fix：
                                                // 距离预判帧 (0,0)（识别失败）不再直接跳过 MoveTo——先在约 2s 时间窗内用
                                                // GetPositionStable（全局匹配）有限重试拿有效坐标。恢复成功则改写 currentPos，
                                                // 自然落入下方既有 if 真分支（守卫 + ShouldPreMoveTo + MoveTo），复用既有逻辑；
                                                // 仍 (0,0) 则 currentPos 不变，落入现状退化路径（__coordTrusted 保持 true，Q3）。
                                                if (currentPos is { X: 0, Y: 0 })
                                                {
                                                    var __preCfg = TaskContext.Instance().Config.AutoHoeingConfig;
                                                    var __preResolve = await KazuhaReturnPreDistanceResolver.ResolveZeroCoordAsync(
                                                        __preCfg.KazuhaReturnPreDistanceZeroRetryTimeoutMs,
                                                        reSampleStable: () =>
                                                        {
                                                            using var s = CaptureToRectArea();
                                                            return Navigation.GetPositionStable(s, waypoint.MapName, waypoint.MapMatchMethod);
                                                        },
                                                        delay: token => Task.Delay(KazuhaReturnReseedGuard.ReseedReSampleDelayMs, token),
                                                        nowMs: () => Environment.TickCount64,
                                                        log: m => Logger.LogInformation("[联机] 战后聚物回点{Msg}", m),
                                                        ct: ct);
                                                    if (__preResolve.Recovered)
                                                    {
                                                        // 恢复出有效坐标（可能仍是远点，交既有守卫块判定）→ 改写 currentPos 落入既有真分支。
                                                        currentPos = __preResolve.Pos;
                                                    }
                                                    // 未恢复：currentPos 仍为 (0,0)，下方 if 为假，退化到现状跳过 MoveTo（行为等价 F）。
                                                }
                                                if (currentPos is not { X: 0, Y: 0 })
                                                {
                                                    // hoeing-kazuha-return-abnormal-coord-reseed-moveto-fix 路径 B：
                                                    // (0,0) 过滤后、ShouldPreMoveTo 之前插入"异常判定 + 重播种 + 重识别重试"helper，
                                                    // 与路径 A 调用同一 KazuhaReturnReseedGuard 保证对称（bugfix §3.9）。
                                                    // 阈值/次数读 AutoHoeingConfig 调试参数（默认 50 / 3，替代旧硬编码 180）。
                                                    var __hoeingCfg = TaskContext.Instance().Config.AutoHoeingConfig;
                                                    var __guardResult = await KazuhaReturnReseedGuard.EvaluateAndReseedAsync(
                                                        currentPos, waypoint.X, waypoint.Y,
                                                        __hoeingCfg.KazuhaReturnAbnormalCoordThreshold,
                                                        __hoeingCfg.KazuhaReturnReseedRetryCount,
                                                        __hoeingCfg.KazuhaReturnZeroCoordStableRetryCount,
                                                        reseedAnchor: () => Navigation.SetPrevPosition((float)waypoint.X, (float)waypoint.Y),
                                                        reSample: () =>
                                                        {
                                                            using var s = CaptureToRectArea();
                                                            return Navigation.GetPosition(s, waypoint.MapName, waypoint.MapMatchMethod);
                                                        },
                                                        // hoeing-kazuha-return-minimap-recognition-fail-getpositionstable-retry-fix：
                                                        // (0,0) 识别失败时改走 GetPositionStable 全局匹配（其内部局部失败/跳跃>150 自动 Reset 全局匹配）。
                                                        reSampleStable: () =>
                                                        {
                                                            using var s = CaptureToRectArea();
                                                            return Navigation.GetPositionStable(s, waypoint.MapName, waypoint.MapMatchMethod);
                                                        },
                                                        delay: token => Task.Delay(KazuhaReturnReseedGuard.ReseedReSampleDelayMs, token),
                                                        // 画面稳定门控（本次修复）：重识别前先派蒙检测。CaptureToRectArea + Bv.IsInMainUi。
                                                        isScreenStable: () =>
                                                        {
                                                            using var s = CaptureToRectArea();
                                                            return Bv.IsInMainUi(s);
                                                        },
                                                        screenStablePollDelay: token => Task.Delay(KazuhaReturnReseedGuard.ScreenStablePollIntervalMs, token),
                                                        log: m => Logger.LogInformation("[联机] 战后聚物回点{Msg}", m),
                                                        ct: ct);

                                                    if (!__guardResult.ShouldMove)
                                                    {
                                                        // N 次重播种+重识别仍异常 → 放弃本轮移动（绝不以 garbage 坐标 MoveTo/MoveCloseTo），
                                                        // 跳过两段 MoveCloseTo + 广播二段，仅进 WaitAtFightPointAsync 参与全队同步兜底。
                                                        Logger.LogWarning("[联机] 战后聚物回点坐标持续异常，放弃本轮移动，交聚物同步兜底");
                                                        __coordTrusted = false;
                                                    }
                                                    else
                                                    {
                                                        currentPos = __guardResult.TrustedPos;
                                                        var preDistance = Navigation.GetDistance(waypoint, currentPos);
                                                        if (PathExecutorDecisions.ShouldPreMoveTo(preDistance, 4.0))
                                                        {
                                                            Logger.LogInformation("[联机] 距战斗点 {Dist:F1} > 4.0，先 MoveTo 粗接近", preDistance);
                                                            // multiplayer-kazuha-pre-cast-positioning: 战后回点全员强制 Walk
                                                            // （避免 fightWaypoint 录制 MoveMode 是 Dash/Run 时全员同时疾跑消耗体力）。
                                                            // 注意：直接改 waypoint.MoveMode 会"污染"原 Waypoint 实例后续被其他调用点使用，
                                                            // 但此场景下 fightWaypoint 是战斗节点，本次 MoveTo 后立即进 MoveCloseTo（其内部不读 MoveMode）
                                                            // 再交给 WaitAtFightPointAsync，路线不会再次复用同一 fightWaypoint 实例做远距离移动，
                                                            // 故对原对象的污染影响可控。
                                                            waypoint.MoveMode = MoveModeEnum.Walk.Code;
                                                            // BC3 调用点③（kazuha-continuous-return-abnormal-coord-and-moveto-distance-fix 改动 3）：
                                                            // isGetOut: true → false 关闭卡死脱困，避免脱困逻辑在战后聚物粗接近期间
                                                            // 随机扭动/跳跃/复苏传送，与战斗主循环/聚物定位抢镜头抢移动。
                                                            // hoeing-return-fightpoint-moveto-stuck-sync-timeout-fix:
                                                            // 仅此回点调用点传入预算 = KazuhaSyncTimeoutSeconds(与 WaitAtFightPointAsync 等待终态同源:
                                                            // MultiplayerCoordinator.EffectiveConfig 与 KazuhaCollectSync 内部 _config 是同一实例)。
                                                            // 单机 MultiplayerCoordinator==null → 传 null → 旧行为(只受 240s)。
                                                            await MoveTo(waypoint, isGetOut: false, task: null, nextWaypoint: null,
                                                                nextDistance: null, retryDis: 4, isPoint: false, escapeClimbOnReturn: true,
                                                                returnMoveBudgetSeconds: MultiplayerCoordinator?.EffectiveConfig?.KazuhaSyncTimeoutSeconds);
                                                        }
                                                    }
                                                }
                                            }

                                            // BC1+BC5: 第一段 MoveCloseTo 显式覆盖：
                                            //   closeDistance: 2.0  — 粗略停下即可（精接近交给二段聚物点 MoveCloseTo）。
                                            //   tailDelayMs: 0      — 去掉到点后硬编码 1s 停顿。
                                            //   maxSteps: 5         — 上限约 0.4s（前面已用 MoveTo 粗接近，5 步内收敛到 2.0 单位足够）。
                                            //   仅本调用点显式传新参数，其它所有 MoveCloseTo 调用保持默认参数 = 原行为，单机零回归。
                                            // 配合下方"非万叶玩家二段 MoveCloseTo 到聚物点 closeDistance:0.5"使用：
                                            //   收到聚物点广播 → 第一段粗停 + 二段精接近，总耗时 ≤ 0.9s
                                            //   未收到广播（退化）→ 仅第一段，停在距战斗点 ≤ 2.0 单位（与原版 closeDistance:2.0 默认行为等价）

                                            // multiplayer-kazuha-collect-point-broadcast: 非万叶玩家"等聚物点广播"与第一段并行。
                                            // ...（守卫四重 + 5s 超时见原注释）
                                            // hoeing-kazuha-return-abnormal-coord-reseed-moveto-fix 路径 B：
                                            // 坐标可信才做两段接近；放弃移动（__coordTrusted=false）时整块跳过，
                                            // 绝不以 garbage 坐标 MoveCloseTo，直接进 WaitAtFightPointAsync 兜底。
                                            if (__coordTrusted)
                                            {
                                                var ks = MultiplayerCoordinator.KazuhaCollectSync;
                                                var collectPointSyncKey = waypoint == null
                                                    ? $"{MultiplayerCoordinator.CurrentRouteIndex}:0:0"
                                                    : $"{MultiplayerCoordinator.CurrentRouteIndex}:{waypoint.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}:{waypoint.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";
                                                Task<bool>? collectPointWaitTask = null;
                                                if (ks != null && !ks.IsCurrentPlayerKazuha)
                                                {
                                                    collectPointWaitTask = ks.TryWaitForCollectPointAsync(collectPointSyncKey, timeoutMs: 5000, ct);
                                                }

                                                // 后台定时任务每 500ms 检测一次，覆盖整个回点流程，此处无需额外调用

                                                await MoveCloseTo(waypoint, closeDistance: 2.0, tailDelayMs: 0, maxSteps: 5, escapeClimbOnReturn: true);

                                                // 第一段走完，await 后台 wait task 看是否拿到广播 → 拿到则二段精接近聚物点。
                                                // 超时未到 → 退化到第二段精接近"战斗点"（与原 spec closeDistance:1.0 单段路径等价的体验）。
                                                // 任意失败：catch + LogWarning，不让二段失败传播到主流程，继续 WaitAtFightPointAsync 兜底。
                                                try
                                                {
                                                    if (ks != null && !ks.IsCurrentPlayerKazuha && collectPointWaitTask != null)
                                                    {
                                                        var got = await collectPointWaitTask;
                                                        double tx, ty;
                                                        if (got && ks.TryGetCollectPointForCurrent(collectPointSyncKey, out var cx, out var cy))
                                                        {
                                                            tx = cx; ty = cy;
                                                            Logger.LogInformation("[联机] 非万叶玩家：收到聚物点广播 ({CX:F1},{CY:F1})，二段精接近", tx, ty);
                                                        }
                                                        else
                                                        {
                                                            // 退化：等聚物点超时 / 未到 → 二段改为精接近"战斗点"，
                                                            // 把停点从距战斗点 < 2.0 缩到 < 0.5，体验等价于原 spec closeDistance:1.0 单段。
                                                            tx = waypoint.X; ty = waypoint.Y;
                                                            Logger.LogDebug("[联机] 非万叶玩家：等聚物点广播超时，退化为精接近战斗点 ({TX:F1},{TY:F1})", tx, ty);
                                                        }

                                                        // 构造临时 WaypointForTrack：MapName / MapMatchMethod 沿用 fightWaypoint，
                                                        // 但 X/Y/MatX/MatY 直接覆盖为 (tx, ty)（已经是小地图坐标系，
                                                        // 与 Navigation.GetPosition 返回值同坐标系，绕过基类构造的坐标系转换）。
                                                        // GameX/GameY 不参与 MoveCloseTo（其内部仅读 X/Y），写 0 即可。
                                                        // 不修改原 waypoint 实例任何字段。
                                                        var tmp = new WaypointForTrack(
                                                            new Waypoint
                                                            {
                                                                X = 0, Y = 0,
                                                                Type = waypoint.Type,
                                                                Action = string.Empty,
                                                                MoveMode = MoveModeEnum.Walk.Code,
                                                            },
                                                            waypoint.MapName,
                                                            waypoint.MapMatchMethod)
                                                        {
                                                            X = tx, Y = ty,
                                                            MatX = tx, MatY = ty,
                                                            GameX = 0, GameY = 0,
                                                        };
                                                        // multiplayer-kazuha-collect-point-broadcast: 二段 maxSteps 改为可配置（KazuhaSecondApproachMaxSteps）。
                                                        var cfgMaxSteps = TaskContext.Instance().Config.AutoHoeingConfig.KazuhaSecondApproachMaxSteps;
                                                        var maxSteps = cfgMaxSteps >= 1 && cfgMaxSteps <= 30 ? cfgMaxSteps : 6;
                                                        await MoveCloseTo(tmp, closeDistance: 0.5, tailDelayMs: 0, maxSteps: maxSteps, escapeClimbOnReturn: true);
                                                    }
                                                }
                                                catch (OperationCanceledException) { throw; }
                                                catch (Exception ex)
                                                {
                                                    Logger.LogWarning(ex, "[联机] 非万叶玩家：二段精接近异常，跳过并进 WaitAtFightPointAsync 兜底");
                                                }
                                            }

                                            // multiplayer-kazuha-collect-sync: 走"万叶分支 / 普通成员分支"的同步流程
                                            // 始终执行（放弃移动也要 join 全队同步，由其内部兜底接管恢复）。
                                            Logger.LogInformation("[联机] 到达战斗点，进入聚物同步流程");
                                            await MultiplayerCoordinator.KazuhaCollectSync.WaitAtFightPointAsync(waypoint, prepTask, ct);

                                            // 回点流程结束，取消后台定时检测任务
                                            if (_mavuikaDismountCts != null)
                                            {
                                                _mavuikaDismountCts.Cancel();
                                                _mavuikaDismountCts.Dispose();
                                                _mavuikaDismountCts = null;
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        // === 基于经验判断停止锄地：战斗节点正常走完，消费经验检测结果（multiplayer-hoeing-exp-cap-stop）===
                        // 仅当本节点创建过检测器（战斗节点 + IsExpCapStopEnabled）才进入。走到此处说明未因
                        // 复苏/跳段 RetryException 中断（那些路径直接跳到 for-i 的 catch，不经过这里）。
                        if (_expCapDetector != null)
                        {
                            var detector = _expCapDetector;
                            _expCapDetector = null;
                            try
                            {
                                await detector.StopAsync();
                                if (!ct.IsCancellationRequested && MultiplayerCoordinator != null)
                                {
                                    await MultiplayerCoordinator.OnFightNodeExpResultAsync(detector.HasDetectedExperience, ct);
                                }
                            }
                            finally
                            {
                                detector.Dispose();
                            }
                        }

                        _lastWaypoint = waypoint;
                    }

                    // ============================================================================
                    // === 段出口屏障（线路重试模式 v2 / hoeing-multiplayer-route-retry-mode §0）===
                    // 目的：让"全员一起重打"成为确定性行为，而非依赖广播时序的概率行为。
                    //
                    // 原理：retry-route 的每段跑完后，所有成员（含已回神像的复苏者）都在这里报到等待。
                    //   - 复苏者在"去神像之前"就已广播复苏（PlayerAnomalyNotify），且被计入屏障人数；
                    //   - 健康队友必须等复苏者到齐才放行 → 放行时广播必然已送达 → 全员的
                    //     _segmentDeathForRerun 状态一致 → 一起重跑或一起放行（消除"队友已跑掉"竞态）。
                    //
                    // 仅 RouteRetryModeEnabled（房主同步白名单命中）+ 联机时生效；
                    // 其他线路/单机整块短路 → 对速度和逻辑零影响（用户明确要求）。
                    // ============================================================================
                    if (RouteRetryModeEnabled
                        && MultiplayerCoordinator != null
                        && !_segmentRerunDone)   // 本段已重跑过 → 不再进屏障（只重试一次，避免二次等待）
                    {
                        var __segIdxForBarrier = CurWaypoints.Item1;
                        // syncId 命名空间必须与 BuildSyncPointMapManual 的 idNamespace 算法【逐字节一致】，
                        // 否则变体线路（成员跑不同变体文件、FileName 不同）会各等在不同 syncId 上 → 屏障死等超时。
                        // 规则：LogicalRouteId 非空用它；否则用 StripBaseNameAnyVariant(FileName) 归一化到统一基名。
                        string __segExitNs = string.IsNullOrEmpty(task.LogicalRouteId)
                            ? BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.RouteVariantNaming.StripBaseNameAnyVariant(task.FileName)
                            : task.LogicalRouteId;
                        var __segExitSyncId = $"{__segExitNs}_segexit_{__segIdxForBarrier}";
                        // 屏障进度用"本段最后一个 waypoint"，保证语义单调（本段已跑完）
                        long __segExitProgress = ComputeProgress(__segIdxForBarrier, Math.Max(0, waypoints.Count - 1));

                        // 复苏者此前上报过 Reviving，放行前先恢复 Normal，避免污染后续全员判定
                        if (_revivedGoToSegExitBarrier)
                        {
                            try { await MultiplayerCoordinator.ReportMemberStatusAsync(MemberStatus.Normal); } catch { }
                        }

                        Logger.LogInformation("[联机][重试模式v2] 到达段出口屏障，等待全员碰头，syncId={SyncId}，本段有人死过={Died}",
                            __segExitSyncId, _segmentDeathForRerun);
                        await MultiplayerCoordinator.WaitForAllPlayers(__segExitSyncId, ct, __segExitProgress);

                        // === 屏障放行后统一决策（此刻全员状态已对齐）===
                        if (_segmentDeathForRerun)
                        {
                            _segmentRerunDone = true;             // 只重试一次
                            _segmentDeathForRerun = false;        // 清标志，重跑期间重新累计
                            _revivedGoToSegExitBarrier = false;
                            _rerunSuppressExtrasOnce = true;      // 重跑段起点跳过吃药/换人（多余）
                            _exemptNextLaggingCatchUpOnce = true; // 重跑是主动回退，豁免落后追赶（否则被踹走）
                            _needReportNormalBeforeSync = true;   // 段起点同步前上报 Normal
                            SkipToNextSegment = false;            // 确保"重跑本段"而非"跳下一段"
                            Logger.LogWarning("[联机][重试模式v2] 段出口屏障：本段有成员死过 → 全员一起重跑本段（第 1 次，仅重试一次）");
                            ResetRecoveryStateForNewAttempt();
                            continue; // 继续 for-i：全员重跑本段（段起点传送 + 正常同步汇合）
                        }

                        _revivedGoToSegExitBarrier = false;
                        Logger.LogInformation("[联机][重试模式v2] 段出口屏障：本段无人死亡{Done} → 全员放行，继续下一段",
                            _segmentRerunDone ? "（或已重跑过）" : "");
                    }

                    if (waypoints == waypointsList.Last())
                    {
                        SuccessEnd = true;
                    }
                    break;
                }
                catch (HandledException handledException)
                {
                    SuccessEnd = true;
                    break;
                }
                catch (NormalEndException normalEndException)
                {
                    Logger.LogInformation(normalEndException.Message);
                    if (!RunnerContext.Instance.isAutoFetchDispatch && RunnerContext.Instance.IsContinuousRunGroup)
                    {
                        throw;
                    }
                    else
                    {
                        break;
                    }
                }
                catch (TaskCanceledException e)
                {
                    if (!RunnerContext.Instance.isAutoFetchDispatch && RunnerContext.Instance.IsContinuousRunGroup)
                    {
                        throw;
                    }
                    else
                    {
                        break;
                    }
                }
                catch (RetryException retryException)
                {
                    // === 落后追赶非异常跳段分支（BUG-D 修复，必须在 escalation 消费 / _syncPointReached 分流 / Reviving 上报之前）===
                    if (retryException is LaggingCatchUpSkipException)
                    {
                        // 落后追赶：Normal 语义跳段，不上报 Reviving、不消费 escalation、不走异常分流。
                        // 进度已在抛异常前 fire-and-forget 上报（BUG-C）。下一段起点经 _needReportNormalBeforeSync 上报 Normal。
                        SkipToNextSegment = true;
                        _needReportNormalBeforeSync = true;
                        Logger.LogInformation("[落后追赶] 跳段追赶：置 SkipToNextSegment，下一段起点上报 Normal，原因: {Msg}", retryException.Message);
                        break; // 出 for-i 重试循环，外层段循环下一段顶部消费 SkipToNextSegment 传送到下一段
                    }

                    // 计算目标进度：跳到下一段开头的传送点
                    // 当前段索引 = CurWaypoints.Item1，下一段 = CurWaypoints.Item1 + 1，传送点是该段的 waypoint 0
                    var nextSegmentIdx = CurWaypoints.Item1 + 1;
                    long targetProgress;
                    if (nextSegmentIdx < waypointsList.Count)
                    {
                        // 跳到当前 JSON 的下一段
                        targetProgress = ComputeProgress(nextSegmentIdx, 0);
                    }
                    else
                    {
                        // 已是最后一段，跳到下一个 JSON 的第一段
                        targetProgress = (long)(CurrentJsonRouteIndex + 1) * 1_000_000;
                    }

                    // === multi-revival-rapid-recurrence-fallback：先消费反复复苏 escalation ===
                    // RouteExecutionEngine 在 OnMultiplayerDefeatedDetected 回调中通过
                    // SignalMultiplayerRevival(action) 写入 _pendingRevivalEscalation。
                    // SkipRoute / SkipSegment 都需要先上报 Reviving + targetProgress 保留信号链路语义。
                    var escalation = (BetterGenshinImpact.GameTask.AutoHoeing.Services.RevivalEscalationAction)
                        System.Threading.Interlocked.Exchange(ref _pendingRevivalEscalation, 0);

                    // === 线路重试模式 v2（hoeing-multiplayer-route-retry-mode spec §0）：最高优先，先于 SkipRoute/SkipSegment ===
                    // 全员回神像重跑本段：消费点已 TpStatueOfTheSeven 回血，这里上报 Reviving（本段开头进度）+ 重跑本段，
                    // 保留正常同步（同步点正常汇合）。不同于普通 Continue 的 _syncPointReached 分流——retry-mode 无论
                    // 同步点是否到达都强制"重跑本段"（不跳下一段），因为战斗点带 sync_point_id 时 _syncPointReached 可能为 true。
                    // 不调 StartSkipOtherOperations()：按原 JSON 完整重跑本段（全员从段起点一起重新拉怪 + 战斗，用户要求"直接按原来的 JSON 执行"）。
                    if (escalation == BetterGenshinImpact.GameTask.AutoHoeing.Services.RevivalEscalationAction.RetrySegment
                        && MultiplayerCoordinator != null)
                    {
                        long retryProgress = ComputeProgress(CurWaypoints.Item1, 0); // 本段开头，让服务端在本段同步点继续等本玩家
                        Logger.LogWarning("[联机][重试模式] RetrySegment：回神像后重跑本段（正常同步），上报 Reviving 本段开头进度={P}，原因: {Msg}",
                            retryProgress, retryException.Message);
                        try
                        {
                            await MultiplayerCoordinator.ReportFightingStatusAsync(false);
                            await MultiplayerCoordinator.ReportMemberStatusAsync(MemberStatus.Reviving, retryProgress);
                        }
                        catch { }
                        SkipToNextSegment = false;   // 显式清掉，确保"重跑本段"而非"跳下一段"
                        _needReportNormalBeforeSync = true; // 重跑到本段同步点前上报 Normal，恢复正常同步语义
                        _rerunSuppressExtrasOnce = true;    // 重跑本段起点：跳过按周期吃食物 + 按线路切角色（已回神像满血，多余）
                        _exemptNextLaggingCatchUpOnce = true; // 重跑本段起点：豁免落后追赶判定（主动回退重打，非真正落后）
                        ResetRecoveryStateForNewAttempt();
                        continue; // 继续 for-i：重跑本段（含段起点传送 + 正常同步汇合）
                    }

                    if (escalation == BetterGenshinImpact.GameTask.AutoHoeing.Services.RevivalEscalationAction.SkipRoute
                        && MultiplayerCoordinator != null)
                    {
                        Logger.LogWarning("[联机] 反复复苏 escalation=SkipRoute → 标记路线跳过，目标进度={Target}，原因: {Msg}",
                            targetProgress, retryException.Message);
                        try
                        {
                            await MultiplayerCoordinator.ReportFightingStatusAsync(false);
                            await MultiplayerCoordinator.ReportMemberStatusAsync(MemberStatus.Reviving, targetProgress);
                        }
                        catch { }
                        SkipRouteReason = "[联机] 路线累计复苏次数达上限（RouteRevivalCap），跳整路线";
                        SkipRouteRequested = true;
                        SkipToNextSegment = false; // 显式清掉，确保走"跳整路线"语义
                        _needReportNormalBeforeSync = true;
                        break;
                    }

                    if (escalation == BetterGenshinImpact.GameTask.AutoHoeing.Services.RevivalEscalationAction.SkipSegment
                        && MultiplayerCoordinator != null)
                    {
                        Logger.LogWarning("[联机] 反复复苏 escalation=SkipSegment → 跳到下一段，目标进度={Target}，原因: {Msg}",
                            targetProgress, retryException.Message);
                        try
                        {
                            await MultiplayerCoordinator.ReportFightingStatusAsync(false);
                            await MultiplayerCoordinator.ReportMemberStatusAsync(MemberStatus.Reviving, targetProgress);
                        }
                        catch { }
                        SkipToNextSegment = true;
                        _needReportNormalBeforeSync = true;
                        break;
                    }

                    // === escalation == Continue：走原"同步点前/后"分流逻辑（preservation §3.4 完全不动）===

                    // 联机模式：同步点后异常 → 上报 Reviving + 目标进度 → 不重试 → 跳到下一段线路
                    if (_syncPointReached && MultiplayerCoordinator != null)
                    {
                        Logger.LogWarning("[联机] 同步点后异常：上报 Reviving，目标进度={Target}，跳到下一段，原因: {Msg}",
                            targetProgress, retryException.Message);
                        try
                        {
                            await MultiplayerCoordinator.ReportFightingStatusAsync(false);
                            await MultiplayerCoordinator.ReportMemberStatusAsync(MemberStatus.Reviving, targetProgress);
                        }
                        catch { }
                        
                        // 跳到下一段线路（下一个传送点），不是跳过整个 JSON
                        SkipToNextSegment = true;
                        // 标记需要在下一个同步点前上报 Normal
                        _needReportNormalBeforeSync = true;
                        break;
                    }

                    // 联机模式：同步点前异常 → 上报 Reviving → 重试本线段（最多 3 次）→ 失败则跳到下一段
                    if (MultiplayerCoordinator != null)
                    {
                        // 关键修复（targetProgress 必须与玩家实际行为一致）：
                        //   - 还有重试机会 → 玩家会重新跑本段开头到结尾，本段那个还没过的同步点会再次到达
                        //                    → 应承诺"本段开头"，让服务端在本段同步点继续等本玩家
                        //   - 重试耗尽段跳 → 玩家会跳到下一段开头，本段同步点不会再到
                        //                    → 应承诺"下一段开头"
                        // 错误案例：重试时也报"下一段开头"，服务端按豁免逻辑放行了本段同步点的等待，
                        // 玩家重跑本段到达同步点时已无人在等，独自卡 60s 超时（测试反馈症状）。
                        bool willRetryCurrentSegment = i < RetryTimes - 1;
                        long progressForReport = willRetryCurrentSegment
                            ? ComputeProgress(CurWaypoints.Item1, 0)   // 本段开头
                            : targetProgress;                          // 下一段开头

                        try
                        {
                            await MultiplayerCoordinator.ReportFightingStatusAsync(false);
                            await MultiplayerCoordinator.ReportMemberStatusAsync(MemberStatus.Reviving, progressForReport);
                        }
                        catch { }

                        // 重试次数耗尽 → 跳到下一段线路
                        if (!willRetryCurrentSegment)
                        {
                            Logger.LogWarning("[联机] 同步点前异常重试耗尽，跳到下一段线路，目标进度={Target}，原因: {Msg}",
                                targetProgress, retryException.Message);
                            SkipToNextSegment = true;
                            _needReportNormalBeforeSync = true;
                            break;
                        }

                        // 还有重试机会，继续重试
                        StartSkipOtherOperations();
                        Logger.LogWarning("[联机] 同步点前异常，重试（{N}/{Max}），目标进度={Target}（本段重跑），原因: {Msg}",
                            i + 1, RetryTimes, progressForReport, retryException.Message);
                        ResetRecoveryStateForNewAttempt();
                        continue; // 继续 for 循环重试
                    }

                    // 单机模式：保持原有重试行为
                    StartSkipOtherOperations();
                    Logger.LogWarning(retryException.Message);
                    ResetRecoveryStateForNewAttempt();
                }
                catch (RetryNoCountException retryException)
                {
                    //特殊情况下，重试不消耗次数
                    i--;
                    StartSkipOtherOperations();
                    Logger.LogWarning(retryException.Message);
                }
                finally
                {
                    // 不管咋样，松开所有按键
                    Simulation.SendInput.Keyboard.KeyUp(User32.VK.VK_W);
                    Simulation.SendInput.Mouse.RightButtonUp();
                    PathingConditionConfig.CombatScenesGoBackUp = null;
                    
                    // using var ra = CaptureToRectArea();
                    // var pixelValue = ra.SrcMat.At<Vec3b>(32, 67);
                    // // 检查每个通道的值是否在允许的范围内
                    // if (!(Math.Abs(pixelValue[0] - 143) <= 10 &&
                    //       Math.Abs(pixelValue[1] - 196) <= 10 &&
                    //       Math.Abs(pixelValue[2] - 233) <= 10))
                    // {
                    //     Logger.LogWarning("检测到可能的游戏卡死，尝试点击屏幕并等待1秒");
                    //     await Delay(1000, ct);
                    // }
                    
                    //回到主界面
                    // await _returnMainUiTask.Start(ct);
                    
                }
            }
        }

        // 联机模式成员：段循环结束后如果 SkipToNextSegment 仍为 true（最后一个段异常），标记路线跳过
        if (SkipToNextSegment)
        {
            SkipToNextSegment = false;
            SkipRouteReason = "成员异常：最后一个路线段，跳到下一条路线";
            SkipRouteRequested = true;
            Logger.LogWarning("[联机] 成员异常发生在最后一个路线段，标记路线跳过");
        }
        }
        finally
        {
            CurrentActiveInstance = previousInstance;
        }
    }
    
    
    // 恢复状态集中收口（autoeat-count-overloaded-sentinel-fix spec）：
    // 取代原散落 4 处 if(AutoEatCount<3) AutoEatCount=0 补丁（457/904-908/1327/1334）。
    // 仅在"进入新一轮寻路重试、恢复流程确已结束"时调用，防止计数残值 + 抑制态跨路径泄漏。
    // 保留 <3 门控：count==3 是"用户关吃药 / 未发现营养袋"的禁用哨兵，禁用态不得被本收口清成启用态。
    private void ResetRecoveryStateForNewAttempt()
    {
        if (PartyConfig.AutoEatEnabled && PathingConditionConfig.AutoEatCount < 3)
        {
            PathingConditionConfig.AutoEatCount = 0;
        }
        PathingConditionConfig.RecoverSuppressed = false;
    }

    private async Task InitializeAutoEat()
    {
        if (!PartyConfig.AutoEatEnabled)
        {
            PathingConditionConfig.AutoEatCount = 3;
            return;
        }
        
        using (var ra = CaptureToRectArea())
        {
            var nutritionBag = ra.Find(AutoFightAssets.Get(ra).NutritionBagRa);
            if (nutritionBag.IsExist())
            {
                nutritionBag.ClickTo(0,-200);
            }
            using var bloodtRect = ra.DeriveCrop(1817, 781, 4, 14);
            using var mask = OpenCvCommonHelper.Threshold(bloodtRect.SrcMat,new Scalar(185, 225, 95), new Scalar(200, 240, 110));//new Scalar(192, 233, 102), new Scalar(193, 233, 103
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();

            var numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
                connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);
            
            if (numLabels <= 1)
            {
                // 首次检测失败，等待派蒙头像出现确认画面加载完成后复检
                Logger.LogInformation("自动吃药：首次检测未发现营养袋，等待派蒙头像出现后复检");
                var paimonFound = await Bv.WaitUntilFound(ElementAssets.Instance.PaimonMenuRo, ct, retryTimes: 10, delayMs: 200); // 2000ms超时
                if (!paimonFound)
                {
                    _returnMainUiTask.Start(ct).Wait(5000,ct);
                    await Delay(1000, ct);
                    Logger.LogWarning("自动吃药：等待派蒙头像超时(2000ms)，继续复检");
                }

                using (var ra2 = CaptureToRectArea())
                {
                    using var recheckRect = ra2.DeriveCrop(1817, 781, 4, 14);
                    using var recheckMask = OpenCvCommonHelper.Threshold(recheckRect.SrcMat, new Scalar(185, 225, 95), new Scalar(200, 240, 110));
                    using var recheckLabels = new Mat();
                    using var recheckStats = new Mat();
                    using var recheckCentroids = new Mat();

                    var recheckNumLabels = Cv2.ConnectedComponentsWithStats(recheckMask, recheckLabels, recheckStats, recheckCentroids,
                        connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);

                    if (recheckNumLabels > 1)
                    {
                        // 复检成功，启用自动吃药
                        Logger.LogInformation("自动吃药：复检成功，发现营养袋，自动吃药{text}", "开启");
                        PathingConditionConfig.AutoEatCount = 0;
                        return;
                    }
                }

                // 复检仍失败，按原有逻辑处理
                Logger.LogInformation("自动吃药：复检仍未发现营养袋");
                if (PathingConditionConfig.RetryAssemblyNum > 0)
                {
                    if (await RetryAssembly())
                    {
                        PathingConditionConfig.AutoEatCount = 0;
                        return;
                    }
                }
                PathingConditionConfig.AutoEatCount = 3;
                Logger.LogInformation("自动吃药：未发现营养袋，自动吃药{text}", "关闭");
            }
            else
            {
                PathingConditionConfig.AutoEatCount = 0;
                // Logger.LogInformation("自动吃药：已发现营养袋，自动吃药{text}", "开启");
            }
        }
    }
    
    private async Task<bool> RetryAssembly()
    { 
        var result = await NewRetry.WaitForAction( () =>
            {
                _returnMainUiTask.Start(ct).Wait(5000,ct);
                using (var ra2 = CaptureToRectArea())
                {
                    var boon = ra2.Find(AutoFightAssets.Get(ra2).NutritionBagRa);
                    if (boon.IsExist())
                    {
                        boon.Click();
                        return true;
                    }
                    Logger.LogWarning("自动吃药：小道具页面复检未发现营养袋1");
                }
                Logger.LogInformation("自动吃药：尝试装配便携式营养袋剩余次数 {t}",PathingConditionConfig.RetryAssemblyNum);
                Delay(1000, ct).Wait();
                Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget, KeyType.KeyDown);
                Delay(1000, ct).Wait();
                Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget, KeyType.KeyUp);
                Delay(1500, ct).Wait();
                using (var ra2 = CaptureToRectArea())
                {
                    var boon = ra2.Find(AutoFightAssets.Get(ra2).NutritionBagRa);
                    if (boon.IsExist())
                    {
                        boon.Click();
                        return true;
                    }
                    Logger.LogWarning("自动吃药：小道具页面未发现营养袋2");
                }
                //点击一下鼠标
                Simulation.SendInput.Mouse.LeftButtonClick();
                return false;
            },
            ct,
            1,
            1000
        );
        
        PathingConditionConfig.RetryAssemblyNum--;
        return result;
    }

    private bool IsTargetPoint(WaypointForTrack waypoint)
    {
        // 方位点不需要接近
        if (waypoint.Type == WaypointType.Orientation.Code || waypoint.Action == ActionEnum.UpDownGrabLeaf.Code)
        {
            return false;
        }


        var action = ActionEnum.GetEnumByCode(waypoint.Action);
        if (action is not null && action.UseWaypointTypeEnum != ActionUseWaypointTypeEnum.Custom)
        {
            // 强制点位类型的 action，以 action 为准
            return action.UseWaypointTypeEnum == ActionUseWaypointTypeEnum.Target;
        }

        // 其余情况和没有action的情况以点位类型为准
        return waypoint.Type == WaypointType.Target.Code;
    }

    private async Task<bool> SwitchPartyBefore(PathingTask task)
    {
        var ra = CaptureToRectArea();

        // 切换队伍前判断是否全队死亡 // 可能队伍切换失败导致的死亡
        if (Bv.ClickIfInReviveModal(ra))
        {
            await Bv.WaitForMainUi(ct); // 等待主界面加载完成
            Logger.LogInformation("复苏完成");
            await Delay(4000, ct);
            // 血量肯定不满，直接去七天神像回血
            await TpStatueOfTheSeven(requireLoadingScreen: MultiplayerCoordinator != null);
        }

        if (PartyConfig.SkipPartySwitch)
        {
            return true;
        }

        var pRaList = ra.FindMulti(RecognitionAssets.Get("AutoFight", "P", ra)); // 判断是否联机
        if (pRaList.Count > 0)
        {
            Logger.LogInformation("处于联机状态下，不切换队伍");
        }
        else
        {
            if (PartyConfig is { Enabled: false })
            {
                // 调度器未配置的情况下，根据地图追踪条件配置切换队伍
                var partyName = FilterPartyNameByConditionConfig(task);
                if (!await SwitchParty(partyName))
                {
                    Logger.LogError("切换队伍失败，无法执行此路径！请检查地图追踪设置！");
                    return false;
                }
            }
            else if (!string.IsNullOrEmpty(PartyConfig.PartyName))
            {
                if (!await SwitchParty(PartyConfig.PartyName))
                {
                    Logger.LogError("切换队伍失败，无法执行此路径！请检查配置组中的地图追踪配置！");
                    return false;
                }
            }
        }

        return true;
    }
    
    private void InitializePathing(PathingTask task)
    {
        LogScreenResolution();
        InitializeTravelMode();
        WeakReferenceMessenger.Default.Send(new PropertyChangedMessage<object>(this,
            "UpdateCurrentPathing", new object(), task));
    }

    private void InitializeTravelMode()
    {
        if (PartyConfig.HurryOnAvatar == "自动" && _combatScenes!= null)
        {
            foreach (var avatar in _combatScenes.GetAvatars())
            {
                if (PartyConfig.HurryOnAvatarList.Contains(avatar.Name))
                {
                    _hurryOnAvatar = avatar.Name;  
                }
            }
        }
        else
        {
            _hurryOnAvatar = PartyConfig.HurryOnAvatar;
        }

        if (string.IsNullOrEmpty(PartyConfig.TravelMode))
        {
            PartyConfig.TravelMode = "精准靠近";
        }
        
        _MwkFly = PartyConfig.MwkFlyEnabled;
        // 一次赋值，供本次赶路执行中两处阈值判定共用，保证两处取值一致（R1.8）
        _mwkFlyJumpDistance = PartyConfig.MwkFlyJumpDistance;
    }

    private void LogScreenResolution()
    {
        var gameScreenSize = SystemControl.GetGameScreenRect(TaskContext.Instance().GameHandle);
        if (gameScreenSize.Width * 9 != gameScreenSize.Height * 16)
        {
            Logger.LogError("游戏窗口分辨率不是 16:9 ！当前分辨率为 {Width}x{Height} , 非 16:9 分辨率的游戏无法正常使用地图追踪功能！",
                gameScreenSize.Width, gameScreenSize.Height);
            throw new Exception("游戏窗口分辨率不是 16:9 ！无法使用地图追踪功能！");
        }

        if (gameScreenSize.Width < 1920 || gameScreenSize.Height < 1080)
        {
            Logger.LogError("游戏窗口分辨率小于 1920x1080 ！当前分辨率为 {Width}x{Height} , 小于 1920x1080 的分辨率的游戏地图追踪的效果非常差！",
                gameScreenSize.Width, gameScreenSize.Height);
            throw new Exception("游戏窗口分辨率小于 1920x1080 ！无法使用地图追踪功能！");
        }
    }

    /// <summary>
    /// 切换队伍
    /// </summary>
    /// <param name="partyName"></param>
    /// <returns></returns>
    private async Task<bool> SwitchParty(string? partyName)
    {
        bool success = true;
        if (!string.IsNullOrEmpty(partyName))
        {
            if (RunnerContext.Instance.PartyName == partyName)
            {
                return success;
            }

            bool forceTp = PartyConfig.IsVisitStatueBeforeSwitchParty;

            if (forceTp) // 强制传送模式
            {
                await new TpTask(ct).TpToStatueOfTheSeven(requireLoadingScreen: MultiplayerCoordinator != null); // fix typos
                success = await new SwitchPartyTask().Start(partyName, ct);
            }
            else // 优先原地切换模式
            {
                try
                {
                    success = await new SwitchPartyTask().Start(partyName, ct);
                }
                catch (PartySetupFailedException)
                {
                    await new TpTask(ct).TpToStatueOfTheSeven(requireLoadingScreen: MultiplayerCoordinator != null);
                    success = await new SwitchPartyTask().Start(partyName, ct);
                }
            }

            if (success)
            {
                RunnerContext.Instance.PartyName = partyName;
                RunnerContext.Instance.ClearCombatScenes();
            }
        }

        return success;
    }


    private static string? FilterPartyNameByConditionConfig(PathingTask task)
    {
        var pathingConditionConfig = TaskContext.Instance().Config.PathingConditionConfig;
        var materialName = task.GetMaterialName();
        var specialActions = task.Positions
            .Select(p => p.Action)
            .Where(action => !string.IsNullOrEmpty(action))
            .Distinct()
            .ToList();
        var partyName = pathingConditionConfig.FilterPartyName(materialName, specialActions);
        return partyName;
    }

    /// <summary>
    /// 校验
    /// </summary>
    /// <param name="task"></param>
    ///  <param name="force">是否强制校验，默认false</param>
    /// <returns></returns>
    private async Task<bool> ValidateGameWithTask(PathingTask task , bool? force = false)
    {
        _combatScenes = await RunnerContext.Instance.GetCombatScenes(ct, force);
        if (_combatScenes == null)
        {
            return false;
        }

        // 没有强制配置的情况下，使用地图追踪内的条件配置
        // 必须放在这里，因为要通过队伍识别来得到最终结果
        var pathingConditionConfig = TaskContext.Instance().Config.PathingConditionConfig;
        var skipPartySwitch = PartyConfig.SkipPartySwitch;
        if (PartyConfig is { Enabled: false })
        {
            PartyConfig = pathingConditionConfig.BuildPartyConfigByCondition(_combatScenes);
            PartyConfig.SkipPartySwitch = skipPartySwitch;
        }

        // 校验角色是否存在
        if (task.HasAction(ActionEnum.NahidaCollect.Code))
        {
            var avatar = _combatScenes.SelectAvatar("纳西妲");
            if (avatar == null)
            {
                Logger.LogError("此路径存在纳西妲收集动作，队伍中没有纳西妲角色，无法执行此路径！");
                return false;
            }

            // _actionAvatarIndexMap.Add("nahida_collect", avatar.Index.ToString());
        }

        // 把所有需要切换的角色编号记录下来
        Dictionary<string, ElementalType> map = new()
        {
            { ActionEnum.HydroCollect.Code, ElementalType.Hydro },
            { ActionEnum.ElectroCollect.Code, ElementalType.Electro },
            { ActionEnum.AnemoCollect.Code, ElementalType.Anemo },
            { ActionEnum.PyroCollect.Code, ElementalType.Pyro }
        };

        foreach (var (action, el) in map)
        {
            if (!ValidateElementalActionAvatarIndex(task, action, el, _combatScenes))
            {
                return false;
            }
        }
        
        return true;
    }

    private bool ValidateElementalActionAvatarIndex(PathingTask task, string action, ElementalType el,
        CombatScenes combatScenes)
    {
        if (task.HasAction(action))
        {
            foreach (var avatar in combatScenes.GetAvatars())
            {
                if (ElementalCollectAvatarConfigs.Get(avatar.Name, el) != null)
                {
                    return true;
                }
            }

            Logger.LogError("此路径存在 {El}元素采集 动作，队伍中没有对应元素角色:{Names}，无法执行此路径！", el.ToChinese(),
                string.Join(",", ElementalCollectAvatarConfigs.GetAvatarNameList(el)));
            return false;
        }
        else
        {
            return true;
        }
    }

    private List<List<WaypointForTrack>> ConvertWaypointsForTrack(List<Waypoint> positions, PathingTask task)
    {
        // 把 X Y 转换为 MatX MatY
        var allList = positions.Select(waypoint =>
        {
            WaypointForTrack wft = new WaypointForTrack(waypoint, task.Info.MapName, task.Info.MapMatchMethod);
            wft.Misidentification=waypoint.PointExtParams.Misidentification;
            wft.MonsterTag = waypoint.PointExtParams.MonsterTag;
            wft.EnableMonsterLootSplit = waypoint.PointExtParams.EnableMonsterLootSplit;
            // 奖励结束检测是点位级参数，转换后必须随对应战斗点传入处理器。
            wft.AutoFight = waypoint.PointExtParams.AutoFight;
            return wft;
        }).ToList();

        // 按照WaypointType.Teleport.Code切割数组
        var result = new List<List<WaypointForTrack>>();
        var tempList = new List<WaypointForTrack>();
        foreach (var waypoint in allList)
        {
            if (waypoint.Type == WaypointType.Teleport.Code)
            {
                if (tempList.Count > 0)
                {
                    result.Add(tempList);
                    tempList = new List<WaypointForTrack>();
                }
            }

            tempList.Add(waypoint);
        }

        result.Add(tempList);

        return result;
    }

    /// <summary>
    /// 自动同步模式（route-variant-sync-by-logical-id spec / R4）：现有 SyncPointResolver + Old_Sync_Id_Format。
    /// 行为与改动前完全一致。
    /// </summary>
    private void BuildSyncPointMapAuto(PathingTask task, List<List<WaypointForTrack>> waypointsList)
    {
        var resolver = new SyncPointResolver();
        var minDist = TaskContext.Instance().Config.AutoHoeingConfig.SyncPointMinDistance;
        int totalFightPoints = 0;
        int mappedSyncPoints = 0;
        for (int listIdx = 0; listIdx < waypointsList.Count; listIdx++)
        {
            var syncResult = resolver.ResolveWithIndex(waypointsList[listIdx], minDist);
            foreach (var (fightIdx, syncPointIdx, syncPoint) in syncResult)
            {
                totalFightPoints++;
                if (syncPoint != null && syncPointIdx >= 0)
                {
                    var key = listIdx * 10000 + syncPointIdx;
                    var syncId = $"{task.FileName}_{listIdx}_{fightIdx}";
                    _syncPointMap[key] = syncId;
                    mappedSyncPoints++;
                    var isImmediate = syncPointIdx == fightIdx;
                    Logger.LogDebug("[联机] 路线段{ListIdx} 战斗点{FightIdx} → 集合点索引{SyncIdx}{Immediate}: {SyncId}",
                        listIdx, fightIdx, syncPointIdx,
                        isImmediate ? "（传送后立即等待）" : "",
                        syncId);
                }
                else
                {
                    Logger.LogDebug("[联机] 路线段{ListIdx} 战斗点{FightIdx} → 无集合点（跳过同步）", listIdx, fightIdx);
                }
            }
        }
        Logger.LogInformation("[联机] 路线 {Name} 预计算完成（自动模式）：{Total} 个战斗点，{Mapped} 个有集合点",
            task.FileName, totalFightPoints, mappedSyncPoints);

        Logger.LogInformation("[联机] 传送必同步已启用（默认），为所有传送点生成同步点");
        int teleportSyncCount = 0;
        for (int listIdx = 0; listIdx < waypointsList.Count; listIdx++)
        {
            for (int wpIdx = 0; wpIdx < waypointsList[listIdx].Count; wpIdx++)
            {
                if (waypointsList[listIdx][wpIdx].Type == "teleport")
                {
                    var key = listIdx * 10000 + wpIdx;
                    if (!_syncPointMap.ContainsKey(key))
                    {
                        var syncId = $"{task.FileName}_tp_{listIdx}_{wpIdx}";
                        _syncPointMap[key] = syncId;
                        teleportSyncCount++;
                        Logger.LogDebug("[联机] 路线段{ListIdx} 传送点{WpIdx} → 传送同步点: {SyncId}",
                            listIdx, wpIdx, syncId);
                    }
                }
            }
        }
        Logger.LogInformation("[联机] 传送点必同步：新增 {Count} 个传送同步点", teleportSyncCount);
    }

    /// <summary>
    /// 手动同步模式（route-variant-sync-by-logical-id spec / R3）：按显式 SyncPointId 标记 + LogicalRouteId 拼 syncId。
    /// 战斗 syncId 不含任何 fightIdx / wpIdx 索引，A、B 变体可任意调整 waypoint 数量与走法。
    /// 传送 syncId 仍按 (listIdx, wpIdx) 顺序自动编号，作者负责 A/B 变体传送序列一致。
    /// </summary>
    private void BuildSyncPointMapManual(PathingTask task, List<List<WaypointForTrack>> waypointsList)
    {
        // R3.6 + hoeing-variant-route 死等修复：LogicalRouteId 为空时的 fallback 命名空间。
        // 旧实现直接用 task.FileName（带 _a/_b 后缀和 .json），导致同一逻辑路线的不同变体
        // 或不同目录布局（变体子文件夹 vs 扁平 pathing 目录）下命名空间不一致 → syncId 永不相等 → 死等。
        // 改用 StripBaseNameAnyVariant 归一化到统一基名，使 fallback 命名空间与
        // BuildFromFilePath 派生的 LogicalRouteId（基名）一致，保证跨玩家/跨变体 syncId 对齐。
        string idNamespace = string.IsNullOrEmpty(task.LogicalRouteId)
            ? BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.RouteVariantNaming.StripBaseNameAnyVariant(task.FileName)
            : task.LogicalRouteId;
        bool isFallback = string.IsNullOrEmpty(task.LogicalRouteId);

        int markedSyncPoints = 0;
        int teleportSyncCount = 0;

        for (int listIdx = 0; listIdx < waypointsList.Count; listIdx++)
        {
            var waypoints = waypointsList[listIdx];
            for (int wpIdx = 0; wpIdx < waypoints.Count; wpIdx++)
            {
                var wp = waypoints[wpIdx];
                var key = listIdx * 10000 + wpIdx;

                if (!string.IsNullOrEmpty(wp.SyncPointId))
                {
                    var syncId = $"{idNamespace}_{wp.SyncPointId}";    // R3.1 / R3.2
                    _syncPointMap[key] = syncId;
                    markedSyncPoints++;
                    Logger.LogDebug("[联机] 路线段{ListIdx} waypoint{WpIdx} 显式同步点: {SyncId}{Fb}",
                        listIdx, wpIdx, syncId, isFallback ? "（fallback FileName 命名）" : "");
                }
                else if (wp.Type == "teleport")
                {
                    if (!_syncPointMap.ContainsKey(key))
                    {
                        var syncId = $"{idNamespace}_tp_{listIdx}_{wpIdx}";   // R3.4
                        _syncPointMap[key] = syncId;
                        teleportSyncCount++;
                        Logger.LogDebug("[联机] 路线段{ListIdx} 传送点{WpIdx} → 传送同步点: {SyncId}{Fb}",
                            listIdx, wpIdx, syncId, isFallback ? "（fallback FileName 命名）" : "");
                    }
                }
            }
        }

        Logger.LogInformation("[联机] 路线 {Name} 预计算完成（手动模式{Mode}）：{Marked} 个显式同步点，{Tp} 个传送同步点",
            task.FileName,
            isFallback ? "/Fallback" : "",
            markedSyncPoints, teleportSyncCount);
    }
    public async Task<bool> TryPartyHealing(CombatScenes? combatScenes = null,PathingPartyConfig? partyConfig = null)
    {
        if (_combatScenes is null)
        {
            if (combatScenes is null)
            {
                Logger.LogWarning("回血失败，未获取到战斗场景");
                return false; 
            }
            _combatScenes = combatScenes;
        }

        if (_combatScenes is null)
        {
            Logger.LogWarning("回血失败，未获取到战斗场景2");
            return false; 
        }

        if (partyConfig is not null)
        {
            PartyConfig = partyConfig;
        }
        
        var avatars = _combatScenes.GetAvatars();

        // 联机回血走独立路径，不进入下方单机检测循环（关注点分离）：
        // 联机最多前台 + 后台两个角色，回血角色由用户通过 RecoverAvatarIndex 指定。
        // 直接切到该角色放 Q（跳过会在联机布局下错位的 AvatarQSkillAsync 检测），
        // fire-and-forget 不阻塞主流程；空放（无 Q 能量）无害。单机完全不受影响。
        var isInMultiGame = _combatScenes.CurrentMultiGameStatus?.IsInMultiGame == true;
        if (MultiplayerRecoverBurstDecisions.ShouldSkipQDetectionAndDirectBurst(isInMultiGame, PartyConfig.RecoverAvatarIndex)
            && int.TryParse(PartyConfig.RecoverAvatarIndex, out var recoverIdx))
        {
            var recoverAvatar = _combatScenes.SelectAvatar(recoverIdx);
            Task.Run(async () =>
            {
                try
                {
                    if (recoverAvatar != null && recoverAvatar.TrySwitch2())
                    {
                        Logger.LogWarning("[联机] 直接切换到指定回血角色 {Name}（索引 {Idx}）放 Q", recoverAvatar.Name, recoverIdx);
                        Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
                        await Delay(2000, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 寻路取消导致的正常中断，无需处理。
                }
                catch (Exception ex)
                {
                    // 异步回血失败不应让主流程崩溃，记录告警便于排查（禁止静默吞）。
                    Logger.LogWarning(ex, "[联机] 直接放Q回血异步执行异常");
                }
            }, ct);
            await Delay(500, ct);
            return true;
        }

        foreach (var avatar in avatars)
        {
            if (avatar.Name == "白术")
            {
                if (avatar.TrySwitch())
                {
                    //1命白术能两次
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    await Delay(800, ct);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    await Delay(800, ct);
                    await SwitchAvatar(PartyConfig.MainAvatarIndex);
                    await Delay(4000, ct);
                    return true;
                }

                break;
            }
            else if (avatar.Name == "希格雯")
            {
                if (avatar.TrySwitch())
                {
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    await Delay(11000, ct);
                    await SwitchAvatar(PartyConfig.MainAvatarIndex);
                    return true;
                }

                break;
            }
            else if (avatar.Name == "珊瑚宫心海")
            {
                if (avatar.TrySwitch())
                {
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    await Delay(500, ct);
                    //尝试Q全队回血
                    Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
                    //单人血只给行走位加血
                    await SwitchAvatar(PartyConfig.MainAvatarIndex);
                    await Delay(5000, ct);
                    return true;
                }
            }
            else if (avatar.Name == "爱可菲" || avatar.Name == "闲云" || avatar.Index.ToString() == PartyConfig.RecoverAvatarIndex)
            {
                // //获取出战角色
                // var avatarCurrent = _combatScenes.CurrentAvatar();
                // //计算出战角色在avatars队伍中的序号
                // var currentIndex = avatars.FirstOrDefault(a => a.Name == avatarCurrent)?.Index;
                // if (currentIndex == null) return false;
               
                var currentIndex = 0;
                using (var bitmap = CaptureToRectArea())
                {
                    if (PartyConfig.RecoverAvatarIndex != null)
                    {
                        var avatarCount = AvatarSwitchIndexDecisions.EffectiveAvatarCount(_combatScenes?.GetAvatars().Count);
                        for (int i = 1; i <= avatarCount; i++)
                        {
                            var avatar2 = _combatScenes.SelectAvatar(i);
                            if (avatar2.IsActive(bitmap))
                            {
                                currentIndex = i;
                            }
                        }
                    
                        // Logger.LogInformation("当前行走角色序号：{Index}", currentIndex);

                        if (currentIndex == 0)
                        {
                            return false;
                        } 
                        var num = _combatScenes.GetAvatars().Count();
                        List<int> useEqList = Enumerable.Range(1, num).ToList();
                        try
                        {
                            var qSkill = await AutoFightSkill.AvatarQSkillAsync(bitmap, useEqList, currentIndex);
                            if (qSkill.Contains(avatar.Index))
                            {
                                if (avatar.TrySwitch())
                                {
                                    Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
                                    await Delay(5000, ct);
                                    await SwitchAvatar(PartyConfig.MainAvatarIndex);
                                    return true;
                                }
                            }
                            else if ((avatar.Name == "爱可菲" || avatar.Name == "闲云") && qSkill.Contains(avatar.Index))
                            {
                                if (avatar.TrySwitch())
                                {
                                    Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
                                    await Delay(5000, ct);
                                    await SwitchAvatar(PartyConfig.MainAvatarIndex);
                                    return true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError("尝试识别元素爆发技能失败，原因：{Ex}", ex.Message);
                        }
                    }
                }
            }
        }

        return false;
    }

    private async Task RecoverWhenLowHp(WaypointForTrack waypoint,bool switchOnly = false)
    {
        var timing = PartyConfig.RecoverTiming;
        if (timing == RecoverTiming.Never)
        {
            return;
        }

        if (timing == RecoverTiming.OnlyTeleport && waypoint.Type != WaypointType.Teleport.Code)
        {
            return;
        }
        using var region = CaptureToRectArea();
        if (Bv.CurrentAvatarIsLowHp(region) && !(await TryPartyHealing() && Bv.CurrentAvatarIsLowHp(region)))
        {
            Logger.LogInformation("当前角色血量过低，去七天神像恢复-1 {t}", PathingConditionConfig.AutoEatCount);
            
            using (var bitmap = CaptureToRectArea())
            {
                var pixel = 0;

                for (int i = 0; i < 2; i++)
                {
                    using (var bitmap2 = CaptureToRectArea())
                    {
                        var pixelValue = bitmap2.SrcMat.At<Vec3b>(1010,814);
                        if (!(Math.Abs(pixelValue[0] - 34) <= 10 &&
                              Math.Abs(pixelValue[1] - 215) <= 10 &&
                              Math.Abs(pixelValue[2] - 150) <= 10))
                        {
                            pixel += 1;
                        }
                        else
                        {
                            pixel = 0;
                        }
                    }
                    await Task.Delay(100, ct);
                }
                
                if (pixel >= 2)
                { 
                    Logger.LogInformation("当前行走角色血量仍过低，尝试切换人-1");
                        
                    if (!string.IsNullOrWhiteSpace(PartyConfig.MainAvatarIndex))
                    {
                        var avatarCount = AvatarSwitchIndexDecisions.EffectiveAvatarCount(_combatScenes?.GetAvatars().Count);
                        var avatarIndex = int.Parse(PartyConfig.MainAvatarIndex);
                        var nextAvatarIndex = AvatarSwitchIndexDecisions.NextAvatarIndex(avatarIndex, avatarCount);
                        if (_combatScenes?.SelectAvatar(nextAvatarIndex).Name == "枫原万叶" && 
                            _combatScenes?.SelectAvatar(PathingConditionConfig.InitialMainAvatarIndex)?.Name != "枫原万叶")
                        {
                            nextAvatarIndex = AvatarSwitchIndexDecisions.NextAvatarIndex(nextAvatarIndex, avatarCount);
                        }
            
                        var avatar = _combatScenes?.SelectAvatar(avatarIndex);
            
                        await Delay(300, ct);
            
                        if (avatar != null && avatar.IsActive(bitmap))
                        {
                            PartyConfig.MainAvatarIndex = nextAvatarIndex.ToString();
                            await SwitchAvatar(nextAvatarIndex.ToString());
                        }
                        else
                        {
                            await SwitchAvatar(PartyConfig.MainAvatarIndex);
                        }
                    }
                    else
                    {
                        var avatarCount = AvatarSwitchIndexDecisions.EffectiveAvatarCount(_combatScenes?.GetAvatars().Count);
                        for (int i = 1; i <= avatarCount; i++)
                        {
                            var avatar = _combatScenes?.SelectAvatar(i);
                            if (avatar != null && avatar.IsActive(bitmap))
                            {
                                var nextAvatarIndex = AvatarSwitchIndexDecisions.NextAvatarIndex(i, avatarCount);
                                if (_combatScenes?.SelectAvatar(nextAvatarIndex).Name == "枫原万叶" && 
                                    _combatScenes?.SelectAvatar(PathingConditionConfig.InitialMainAvatarIndex)?.Name != "枫原万叶")
                                {
                                    nextAvatarIndex = AvatarSwitchIndexDecisions.NextAvatarIndex(nextAvatarIndex, avatarCount);
                                }
                                await SwitchAvatar(nextAvatarIndex.ToString());
                                break;
                            }
                        }
                    }
                }
            }
            
            using (var bitmap = CaptureToRectArea())
            {
                var confirmRectArea = bitmap.Find(AutoFightAssets.Get(bitmap).ConfirmRa);
                if (!confirmRectArea.IsEmpty())
                {
                    Simulation.ReleaseAllKey();
                    confirmRectArea.Click();
                    await Task.Delay(399, ct);
                    confirmRectArea.ClickTo(-100, 0);
                    await Task.Delay(300, ct);
                    Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget); 
                    await Task.Delay(500, ct);
                }
            }
            
            // 联机模式：低血量去七天神像，由后续 RetryException 处理 Reviving 上报（带 targetProgress）
            // 这里不上报，避免覆盖 targetProgress 为 -1
            await TpStatueOfTheSeven(switchOnly, requireLoadingScreen: MultiplayerCoordinator != null);
            if (!AutoEatRecoveryDecisions.ShouldRetryRoute(PathingConditionConfig.AutoEatCount)) return;
            throw new RetryException("回血完成后重试路线-1");
        }
        else if (Bv.ClickIfInReviveModal(region))
        {
            await Bv.WaitForMainUi(ct); // 等待主界面加载完成
            Logger.LogInformation("复苏完成-1");
            await Delay(4000, ct);
            // 联机模式：复苏后去七天神像，由后续 RetryException 处理 Reviving 上报
            await TpStatueOfTheSeven(requireLoadingScreen: MultiplayerCoordinator != null);
            if (!AutoEatRecoveryDecisions.ShouldRetryRoute(PathingConditionConfig.AutoEatCount)) return;
            throw new RetryException("回血完成后重试路线-2");
        }
    }
    
    private async Task TpStatueOfTheSeven(bool switchOnly = false, bool requireLoadingScreen = false, bool forceStatue = false)
    {
        // return-to-point-suspend-during-revival-teleport spec：
        // 进入神像传送即置位标志，使两条"战斗中回点"后台循环 return 终止本场回点循环，
        // 避免把刚传送到神像的角色又拉回战斗点。传送后角色必定不回战斗点，本循环已无意义，
        // 故直接终止；回点能力由下一场战斗重新启动的新循环恢复。finally 复位保证任何退出路径
        //（正常 / RetryException / 取消）都不会让标志永久悬挂。该方法是所有"去七天神像"的唯一
        // 收口点（单机 + 联机共用），故在此置位天然覆盖两种模式。详见 design.md 改动 5 / Property 4。
        AutoFightTask.IsTeleportingToStatue = true;
        try
        {
            await TpStatueOfTheSevenCore(switchOnly, requireLoadingScreen, forceStatue);
        }
        finally
        {
            AutoFightTask.IsTeleportingToStatue = false;
        }
    }

    private async Task TpStatueOfTheSevenCore(bool switchOnly = false, bool requireLoadingScreen = false, bool forceStatue = false)
    {
        // 线路重试模式 v2（hoeing-multiplayer-route-retry-mode §0）：forceStatue=true 时跳过"嗑药分级恢复"
        // （原逻辑第 1 次只嗑小道具就 return，走不到下方传送神像）→ 直接传送神像满血，保证"全员回神像重跑"落地。

        // === 方案 B：有界轮询等强制复活结束、角色恢复可控（再开图传送）===
        // 点复苏后角色被强制复活动画锁定约 5~10 秒，期间按 M 开不了大地图。
        // 判据：能成功打开大地图 = 已可控。轮询按 M 试开图，成功即 ESC 关掉（让下方正常流程重新干净开图），
        // 封顶 12 秒超时也继续（不卡死，由后续 CheckInBigMapUi 重试兜底）。仅 forceStatue（retry-mode 复苏）路径生效。
        if (forceStatue)
        {
            var __waitStart = DateTime.UtcNow;
            const int __maxWaitMs = 12000;
            bool __controllable = false;
            while ((DateTime.UtcNow - __waitStart).TotalMilliseconds < __maxWaitMs)
            {
                using (var __probe = CaptureToRectArea())
                {
                    if (Bv.IsInBigMapUi(__probe)) { __controllable = true; break; }
                }
                Simulation.SendInput.SimulateAction(GIActions.OpenMap);
                await Delay(700, ct);
                using (var __probe2 = CaptureToRectArea())
                {
                    if (Bv.IsInBigMapUi(__probe2)) { __controllable = true; break; }
                }
                // 未开出地图（仍在强制复活动画中）→ 关掉可能的半开状态，等一会再试
                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                await Delay(800, ct);
            }
            if (__controllable)
            {
                Logger.LogInformation("[联机][重试模式] 强制复活结束、角色已可控（大地图可打开），耗时约 {S}s，开始传送神像",
                    (int)(DateTime.UtcNow - __waitStart).TotalSeconds);
                // ESC 关掉大地图，让下方正常流程（复苏弹窗检查 + CheckInBigMapUi）从干净状态重新开图
                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                await Delay(500, ct);
            }
            else
            {
                Logger.LogWarning("[联机][重试模式] 等待强制复活结束超时（{Ms}ms），仍继续尝试传送神像（由开图重试兜底）", __maxWaitMs);
            }
        }

        // Logger.LogInformation("AutoEatCount111 {text}",PathingConditionConfig.AutoEatCount);
        if (!forceStatue && AutoEatRecoveryDecisions.ShouldEnterGadgetPhase(PartyConfig.AutoEatEnabled, PathingConditionConfig.AutoEatCount))
        {
            if (DateTime.UtcNow > PathingConditionConfig.LastEatTime.AddSeconds(1.5))
            {
                Simulation.ReleaseAllKey();
                
                if (!switchOnly)
                {
                    PathingConditionConfig.LastEatTime = DateTime.UtcNow;
                    Logger.LogWarning("自动吃药：尝试使用小道具恢复-2");
                    if(AutoEatRecoveryDecisions.ShouldFireGadgetThisTick(PathingConditionConfig.AutoEatCount))Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget);
                    PathingConditionConfig.AutoEatCount++;
                } 
                
                Logger.LogInformation("自动吃药：检测到红血，尝试恢复-3 {t}", PathingConditionConfig.AutoEatCount);
                
                using (var bitmap = CaptureToRectArea())
                {
                    var confirmRectArea = bitmap.Find(AutoFightAssets.Get(bitmap).ConfirmRa);
                    if (!confirmRectArea.IsEmpty())
                    {
                        Simulation.ReleaseAllKey();
                        confirmRectArea.Click();
                        await Task.Delay(399, ct);
                        confirmRectArea.ClickTo(-100, 0);
                        await Task.Delay(300, ct);
                        Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget); 
                        await Task.Delay(500, ct);
                        // PathingConditionConfig.AutoEatCount++;
                    }
                }
            }
            else
            { 
                using (var bitmap = CaptureToRectArea())
                {
                    if (Bv.IsInRevivePrompt(bitmap))
                    {
                        PathingConditionConfig.AutoEatCount++;
                        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                    }
                }
                Logger.LogWarning("自动吃药：距离上次吃药时间过小，等待重试-3");
            }
            if(!AutoEatRecoveryDecisions.ShouldRetryRoute(PathingConditionConfig.AutoEatCount))return;
        }

        using (var bitmap = CaptureToRectArea())
        {
            if (Bv.IsInRevivePrompt(bitmap))
            {
                Logger.LogInformation("复苏弹窗出现，尝试复苏-4");
                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                await Task.Delay(500, ct);
            }
        }

        // tp 到七天神像回血
        WorldStateMonitor?.BeginTeleportSuppression();
        try
        {
            var tpTask = new TpTask(ct);
            await RunnerContext.Instance.StopAutoPickRunTask(async () => await tpTask.TpToStatueOfTheSeven(requireLoadingScreen), 5);
        }
        finally
        {
            WorldStateMonitor?.EndTeleportSuppression();
        }
        PartyConfig.MainAvatarIndex = PathingConditionConfig.InitialMainAvatarIndex;
        Logger.LogInformation("血量恢复完成。【设置】-【七天神像设置】可以修改回血相关配置-k {t}。",PathingConditionConfig.AutoEatCount);
    }

    /// <summary>
    /// 尝试自动领取派遣奖励，
    /// </summary>
    /// <returns>是否可以领取派遣奖励</returns>
    private async Task<bool> TryGetExpeditionRewardsDispatch(TpTask? tpTask = null)
    {
        if (tpTask == null)
        {
            tpTask = new TpTask(ct);
        }
        
        // 最小5分钟间隔
        if (_combatScenes?.CurrentMultiGameStatus?.IsInMultiGame == true || (DateTime.UtcNow - _lastGetExpeditionRewardsTime).TotalMinutes < 5 || PartyConfig.DisableAutoFetchDispatch)
        {
            return false;
        }

        using (var bitmap = CaptureToRectArea())
        {
            if (bitmap.Find(AutoFightAssets.Get(bitmap).PRa).IsExist())
            {
                return false;
            }
        }

        //打开大地图操作
        await tpTask.OpenBigMapUi();
        bool changeBigMap = false;
        string adventurersGuildCountry =
            TaskContext.Instance().Config.OtherConfig.AutoFetchDispatchAdventurersGuildCountry;
        if (!RunnerContext.Instance.isAutoFetchDispatch && adventurersGuildCountry != "无" && !string.IsNullOrEmpty(adventurersGuildCountry))
        {
            var ra1 = CaptureToRectArea();
            var textRect = new Rect(60, 20, 160, 260);
            var textMat = new Mat(ra1.SrcMat, textRect);
            string text = OcrFactory.Paddle.Ocr(textMat);
            if (text.Contains("探索派遣奖励"))
            {
                changeBigMap = true;
                Logger.LogInformation("开始自动领取派遣任务！");
                try
                {
                    RunnerContext.Instance.isAutoFetchDispatch = true;
                    await RunnerContext.Instance.StopAutoPickRunTask(
                        async () => await new GoToAdventurersGuildTask().Start(adventurersGuildCountry, ct, null, true),
                        5);
                    Logger.LogInformation("自动领取派遣结束，回归原任务！");
                }
                catch (Exception e)
                {
                    Logger.LogInformation("未知原因，发生异常，尝试继续执行任务！");
                }
                finally
                {
                    RunnerContext.Instance.isAutoFetchDispatch = false;
                    _lastGetExpeditionRewardsTime = DateTime.UtcNow; // 无论成功与否都更新时间
                }
            }
        }

        return changeBigMap;
    }

    private async Task HandleTeleportWaypoint(WaypointForTrack waypoint, WaypointForTrack? lastWaypoint = null, string? fastSyncId = null)
    {
        WorldStateMonitor?.BeginTeleportSuppression();
        try
        {
            var forceTp = waypoint.Action == ActionEnum.ForceTp.Code;
            TpTask tpTask = new TpTask(ct);
            await TryGetExpeditionRewardsDispatch(tpTask);
            var (tpX, tpY) = await tpTask.Tp(waypoint.GameX, waypoint.GameY, waypoint.MapName, forceTp, requireLoadingScreen: MultiplayerCoordinator != null, fastSyncId: fastSyncId);
            var (tprX, tprY) = MapManager.GetMap(waypoint.MapName, waypoint.MapMatchMethod)
                .ConvertGenshinMapCoordinatesToImageCoordinates(new Point2f((float)tpX, (float)tpY));
            Navigation.SetPrevPosition(tprX, tprY); // 通过上一个位置直接进行局部特征匹配
            // 同步刷新 PathExecutor 的 prePosition / _prePositionUpdateTime / _prePositionMapKey 三件套，
            // 避免 TP 后第一帧识别失败时 GetPositionAndTime 的 previousDetectedPoint 兜底取到 TP 之前的 stale 坐标。
            // 详见 .kiro/specs/pathexecutor-teleport-fresh-position-fallback-fix/design.md §Fix Implementation 改动 3。
            prePosition = new Point2f(tprX, tprY);
            _prePositionUpdateTime = DateTime.UtcNow;
            _prePositionMapKey = $"{waypoint.MapName}|{waypoint.MapMatchMethod}";
            await Delay(500, ct); // 多等一会
            //如果前后地图不同
        }
        finally
        {
            WorldStateMonitor?.EndTeleportSuppression();
        }
    }

    public async Task FaceTo(WaypointForTrack waypoint)
    {
        var screen = CaptureToRectArea();
        var position = await GetPosition(screen, waypoint);

        // 零坐标防呆（Requirement 4）：开启且本帧 (0,0)（识别失败）时，用上一帧有效坐标算朝向，
        // 避免把 (0,0) 当真实坐标算出"从地图原点指向目标"的错误朝向。默认关闭 = 现状行为。
        var guardEnabled = TaskContext.Instance().Config.MiniMapMatchTuningConfig?.ZeroCoordGuardEnabled
                           ?? MiniMapMatchTuningConfig.DefaultZeroCoordGuardEnabled;
        var (effectivePosition, skip) = ZeroCoordGuard.ResolveOrientationPosition(position, prePosition, guardEnabled);
        if (skip)
        {
            Logger.LogInformation("[零坐标防呆] 本帧坐标(0,0)且无有效上一帧，跳过朝向更新");
            return;
        }

        var targetOrientation = Navigation.GetTargetOrientation(waypoint, effectivePosition);
        Logger.LogDebug("朝向点，位置({x2},{y2})", $"{waypoint.GameX:F1}", $"{waypoint.GameY:F1}");
        await WaitUntilRotatedTo(targetOrientation, 2);
        await Delay(500, ct);
    }

    public DateTime moveToStartTime;

    public async Task MoveTo(WaypointForTrack waypoint,bool isGetOut = true, PathingTask? task = null, Waypoint? nextWaypoint = null,double? nextDistance = null,int retryDis = 4, bool isPoint = true, double? closeDistance = null, string? fastSyncId = null, WaypointForTrack? fastSyncWaypoint = null, bool escapeClimbOnReturn = false, double? returnMoveBudgetSeconds = null)
    {
        // Logger.LogWarning("999");
        bool fastReported = false;  // 抢报一次性短路 bool（fastsync-redesign-parameter-passing spec）
        // 切人
        Task.Run(async () =>
        {
            // 替换位置：在 MoveTo 方法内的类似代码块
            if (!string.IsNullOrEmpty(PartyConfig.MainAvatarIndex) && isPoint)
            {
                var idxStr = PartyConfig.MainAvatarIndex.Trim();
                if (int.TryParse(idxStr, out var idx) && idx >= 1 && idx <= 4)
                {
                    var vk = idx switch
                    {
                        1 => User32.VK.VK_1,
                        2 => User32.VK.VK_2,
                        3 => User32.VK.VK_3,
                        4 => User32.VK.VK_4
                    };
                    Simulation.SendInput.Keyboard.KeyPress(vk);
                }
                // 其它值：不按按键
            }
        }, ct);
        // 战斗结束后小地图可能短暂消失，等待派蒙头像出现（小地图可见的标志）
        {
            using var checkScreen = CaptureToRectArea();
            if (!Bv.IsInMainUi(checkScreen))
            {
                var waitStart = DateTime.UtcNow;
                var recovered = false;
                while ((DateTime.UtcNow - waitStart).TotalMilliseconds < 2000)
                {
                    await Delay(100, ct);
                    using var retryScreen = CaptureToRectArea();
                    if (Bv.IsInMainUi(retryScreen))
                    {
                        recovered = true;
                        break;
                    }
                }
                if (!recovered)
                {
                    Logger.LogWarning("等待主界面恢复超时(2000ms)，继续执行");
                }
            }
        }
        using var screen = CaptureToRectArea();
        var pixelYellowValue = screen.SrcMat.At<Vec3b>(1010, 814);
        var yellowBlood = (Math.Abs(pixelYellowValue[0] - 50) <= 10 &&
                            Math.Abs(pixelYellowValue[1] - 204) <= 10 &&
                            Math.Abs(pixelYellowValue[2] - 255) <= 10);
        if (!yellowBlood && _combatScenes?.GetAvatars().Count > 1)
        {
            await SwitchAvatar(PartyConfig.MainAvatarIndex, false, task, true);
        }
        
        var (position, additionalTimeInMs) = await GetPositionAndTime(screen, waypoint,isPoint);
        var targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
        Logger.LogDebug("粗略接近途经点，位置({x2},{y2})", $"{waypoint.GameX:F1}", $"{waypoint.GameY:F1}");
        // Logger.LogError("324234 {t}",targetOrientation);
        await WaitUntilRotatedTo(targetOrientation, 5);
        moveToStartTime = DateTime.UtcNow;
        var lastPositionRecord = DateTime.UtcNow;
        var fastMode = false;
        var prevPositions = new List<Point2f>();
        var fastModeColdTime = DateTime.MinValue;
        var prevNotTooFarPosition = position;
        int num = 0, distanceTooFarRetryCount = 0, consecutiveRotationCountBeyondAngle = 0;
        var distanceCount = 0;
        var flyDelay = waypoint.MoveMode == MoveModeEnum.Fly.Code;
        bool? hurryOnBool = null;
        bool? runToDash = false;
        double distanceHalf = 0;
        bool dushed = true;

        // 茶包版赶路跨帧状态（原为一批 MoveTo 局部变量，已抽离进 TeapotHurryOnState / TeapotHurryOnHelper.cs，选项1）
        var teapotState = new TeapotHurryOnState();

        // 新版赶路状态（跨帧保持 RotationStableCount）
        var hurryOnState = new HurryOnState();

        string nextAvatarIndexStop = "";
        Avatar? avatar = null;
        if (_combatScenes is not null)
        {
            avatar = _combatScenes.SelectAvatar(_hurryOnAvatar);
            
            var mainAvatarIndex = _combatScenes.SelectAvatar(PartyConfig.MainAvatarIndex);
            if (mainAvatarIndex != null)
            {
                if (mainAvatarIndex.Name == _hurryOnAvatar)
                {
                    nextAvatarIndexStop = (mainAvatarIndex.Index % 4 + 1).ToString(); 
                }
                else
                {
                    nextAvatarIndexStop = mainAvatarIndex.Index.ToString(); 
                }
            }
            else
            {
                 nextAvatarIndexStop = _combatScenes.SelectAvatar(1).Name == _hurryOnAvatar ? "2" : "1";
            }
        }
        
        //测试节点信息
        // Logger.LogWarning("赶路测试log:当前节点:({x2}),动作:({t1}),类型({t2}))", waypoint.Type, waypoint.Action, waypoint.MoveMode);
        // Logger.LogWarning("赶路测试log:Next节点:({x2}),动作:({t1}),间隔距离({x3}),类型({t2}))", nextWaypoint?.Type?? "null", nextWaypoint?.MoveMode ,nextWaypoint?.Action, (int)Math.Round(nextDistance.Value));

        // 按下w，一直走
        if (!flyDelay) Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown); 
        
        while (!ct.IsCancellationRequested)
        {
            if (!Simulation.IsKeyDown(GIActions.MoveForward.ToActionKey().ToVK()))
            {
                if (!flyDelay) Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
            }
            flyDelay = false;

            // === 联机模式：走路途中检测到复苏信号（来自 AnomalyDetector 模板/色块路径）===
            // isPoint==true 仅在普通寻路段触发，战斗 / 聚物 / 回点等 isPoint==false 路径短路。
            // TryConsumeRevivalSignal 单机模式下 MultiplayerCoordinator==null 直接返回 false，零回归。
            // 详见 .kiro/specs/multiplayer-walk-revive-skip-segment/design.md §3.2
            if (isPoint && TryConsumeRevivalSignal())
            {
                Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                Logger.LogWarning("[联机] 走路中检测到复苏信号，跳过本段，前往七天神像回血");
                await TpStatueOfTheSeven(requireLoadingScreen: MultiplayerCoordinator != null);
                throw new RetryException("联机：走路中复苏，神像回血后跳到下一段汇合");
            }

            // === 集体卡死跳段消费点 1（multiplayer-mutual-wait-collective-skip §8.7 / OQ-6 A）===
            // isPoint==true 仅普通寻路段触发；MultiplayerCoordinator==null 单机模式直接 short-circuit。
            // 与上面的复苏信号消费点完全独立（preservation §3.4），抛不同 RetryException 文案便于日志追溯。
            if (isPoint && MultiplayerCoordinator != null
                && MultiplayerCoordinator.TryConsumeRemoteSkipSignal(out var moveSkipTarget))
            {
                Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                Logger.LogWarning("[联机] 走路中收到大部队跳段请求，target={Target}，前往七天神像回血", moveSkipTarget);
                await TpStatueOfTheSeven(requireLoadingScreen: true);
                throw new RetryException("[联机] 大部队请求跳段");
            }

            num++;

            // hoeing-return-fightpoint-moveto-stuck-sync-timeout-fix:
            // 仅回点 MoveTo 粗接近段(调用点显式传 returnMoveBudgetSeconds)启用"聚物同步预算"提前结束。
            // returnMoveBudgetSeconds==null 时 ShouldEndReturnMove 必返回 false → 整段短路,旧行为不变。
            // 与下方 240s 判定的本质区别:这里【不抛异常】,而是发 MoveForward KeyUp 后正常 return,
            // 像到点一样退出 MoveTo,让调用点继续走两段 MoveCloseTo + WaitAtFightPointAsync 兜底。
            if (PathExecutorReturnMoveDecisions.ShouldEndReturnMove(
                    returnMoveBudgetSeconds,
                    (DateTime.UtcNow - moveToStartTime).TotalSeconds))
            {
                Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                Logger.LogInformation(
                    "[联机] 回点 MoveTo 粗接近段达到聚物同步预算 {Budget}s，提前结束移动段并继续后续流程(不放弃整条路径)",
                    returnMoveBudgetSeconds);
                return;
            }

            if ((DateTime.UtcNow - moveToStartTime).TotalSeconds > 240)
            {
                Logger.LogWarning("执行超时，放弃此次追踪");
                throw new RetryException("路径点执行超时，放弃整条路径");
            }

            using var screen2 = CaptureToRectArea();

            EndJudgment(screen2);

            // hoeing-return-fightpoint-climb-detect-drop:
            // 仅回点路径（escapeClimbOnReturn==true）启用攀爬脱离；其它所有调用方默认 false 零感知。
            // 复用本帧 screen2（无副作用纯查询），检测到攀爬即发一次 Drop（取消攀爬键），
            // Delay(500) 冷却避免连发。不做卡死/扭动/跳跃/复苏/NormalAttack。
            if ((escapeClimbOnReturn) && Bv.GetMotionStatus(screen2) == MotionStatus.Climb)
            {
                Logger.LogInformation("[联机] 回点检测到攀爬，发送 Drop 脱离");
                Simulation.SendInput.SimulateAction(GIActions.Drop);
                if (num % 2 == 0)
                {
                    //向左走一下
                    Simulation.SendInput.SimulateAction(GIActions.MoveLeft, KeyType.KeyPress);
                }
                else
                {
                    Simulation.SendInput.SimulateAction(GIActions.MoveRight, KeyType.KeyPress);
                }
            }

             (position, additionalTimeInMs) = await GetPositionAndTime(screen2, waypoint,isPoint);
             if (additionalTimeInMs>0)
             {
                 if (!Simulation.IsKeyDown(GIActions.MoveForward.ToActionKey().ToVK()))
                 {
                     if (!flyDelay)Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                 }

                 additionalTimeInMs = additionalTimeInMs + 1000;//当做起步补偿
             }
            var distance = Navigation.GetDistance(waypoint, position);
            Debug.WriteLine($"接近目标点中，距离为{distance}");

            // === 路径同步点抢报（fastsync-redesign-parameter-passing spec / OQ-7=a）===
            // fastSyncId == null（单机/未启用）→ ShouldFastReportInPathing 短路返回 false
            // fastSyncWaypoint 非 null → 用「到 sync waypoint 的距离」判断（覆盖非 sync waypoint 的"提前"抢报）
            // fastSyncWaypoint == null → 退化为「到当前 waypoint 的距离」（向后兼容）
            var __fastDist = fastSyncWaypoint != null
                ? Navigation.GetDistance(fastSyncWaypoint, position)
                : distance;
            if (FastSyncDecisions.ShouldFastReportInPathing(
                    __fastDist,
                    MultiplayerCoordinator?.EffectiveConfig.FastSyncPathingDistance ?? 10.0,
                    fastSyncId,
                    isMultiplayer: MultiplayerCoordinator != null,
                    isConnected: MultiplayerCoordinator?.IsConnected ?? false,
                    alreadyReported: fastReported,
                    fastSyncEnabled: MultiplayerCoordinator?.EffectiveConfig.FastSyncPointEnabled ?? false))
            {
                fastReported = true;
                var __progress = ComputeProgress(CurWaypoints.Item1, CurWaypoint.Item1);
                try
                {
                    // fire-and-forget：上报后立即返回，不阻塞 MoveTo 主流程；
                    // 后续严格 WaitForAllPlayers 路径会再上报一次走完整等待（服务端 idempotent）
                    await MultiplayerCoordinator!.FastReportAsync(fastSyncId!, __progress);
                    Logger.LogInformation("[联机][FastSync] MoveTo 抢报命中 syncId={SyncId} dist={Dist:F2}", fastSyncId, __fastDist);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "[联机][FastSync] MoveTo 抢报异常，已忽略 syncId={SyncId}", fastSyncId);
                }
            }
            if (!isPoint)
            {
                if(retryDis > 6)
                {
                    if (distance < retryDis || distance > 150)
                    {
                        // Logger.LogError("111{t}",distance);
                        Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                        return;
                    }

                    if(num==1)Logger.LogWarning("检测到离开战斗点 {retryDis}，尝试回到战斗节点",retryDis);
                    
                }
            }
            else
            {
                if (distance > 500 && num > 2)
                {
                    // 检查是否为真实远距离（坐标稳定）还是误识别（坐标跳变）
                    var distToPrevCheck = prevNotTooFarPosition is not { X: 0, Y: 0 } && position is not { X: 0, Y: 0 }
                        ? Math.Sqrt(Math.Pow(position.X - prevNotTooFarPosition.X, 2) + Math.Pow(position.Y - prevNotTooFarPosition.Y, 2))
                        : double.MaxValue;
                    
                    if (distToPrevCheck <= 200 && position is not { X: 0, Y: 0 })
                    {
                        // 坐标稳定，角色确实离目标远，不停止移动，正常继续
                        prevNotTooFarPosition = position;
                    }
                    else
                    {
                        Logger.LogWarning("检测到离开目标点异常，停止移动，距离1：{distance}- {x} - {y}", distance,position.X, position.Y);
                        await Delay(1000, ct);
                        using var screen23 = CaptureToRectArea();
                        (position, additionalTimeInMs) = await GetPositionAndTime(screen23, waypoint,isPoint);
                    if (position is not  { X: 0, Y: 0 })
                    {
                        prePosition = position;
                        _prePositionUpdateTime = DateTime.UtcNow;
                        _prePositionMapKey = $"{waypoint.MapName}|{waypoint.MapMatchMethod}";
                        distance = Navigation.GetDistance(waypoint, position);
                        Logger.LogWarning("重新识别位置成功，距离2：{distance} - {x} - {y}", distance,position.X, position.Y);
                    }
                    else
                    {
                        distance = Navigation.GetDistance(waypoint, position);
                    }
                    
                    if(distance > 500)
                    {
                        Logger.LogWarning("重新识别位置异常，停止移动，距离3：{distance} - {x} - {y}", distance,position.X, position.Y);
                        using var screen233 = CaptureToRectArea();
                        (position, additionalTimeInMs) = await GetPositionAndTime(screen233, waypoint,isPoint);
                        if (position is not  { X: 0, Y: 0 })
                        {
                            prePosition = position;
                            _prePositionUpdateTime = DateTime.UtcNow;
                            _prePositionMapKey = $"{waypoint.MapName}|{waypoint.MapMatchMethod}";
                            distance = Navigation.GetDistance(waypoint, position);
                            Logger.LogWarning("重新识别位置成功，距离4：{distance} - {x} - {y}", distance,position.X, position.Y);
                        }
                        else
                        {
                            using var screen2334 = CaptureToRectArea();
                            (position, additionalTimeInMs) = await GetPositionAndTime(screen2334, waypoint,isPoint);

                            if (position is  { X: 0, Y: 0 })
                            {
                                if ((DateTime.UtcNow - _prePositionUpdateTime).TotalSeconds <= 5)
                                {
                                    position = prePosition;
                                }
                                else
                                {
                                    Logger.LogWarning("prePosition 已过时，触发全局匹配");
                                    Navigation.Reset();
                                    prePosition = default;
                                    _prePositionMapKey = string.Empty;
                                }
                            }
                            
                            distance = Navigation.GetDistance(waypoint, position);
                            Logger.LogWarning("重新识别位置失败，使用上次正常识别的位置，距离5：{distance} - {x} - {y}", distance,prePosition.X, prePosition.Y);
                        }
                    }
                    // Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                    }
                }
            }

            if(runToDash == false && distance > 40 && waypoint.MoveMode == MoveModeEnum.Run.Code && avatar?.Name == "玛薇卡")
            {
                runToDash = true;
                distanceHalf = distance*2/4;
                waypoint.MoveMode = MoveModeEnum.Dash.Code;
            }
            else if (runToDash == true && distance < distanceHalf)
            {
                waypoint.MoveMode = MoveModeEnum.Run.Code;
                Task.Run(async () =>
                {
                    Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyDown);
                    await Delay(1000, ct);
                    Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyUp);  
                }, ct);
                runToDash = null;
            }
            
            hurryOnBool ??= (waypoint.MoveMode == MoveModeEnum.Run.Code ||
                               waypoint.MoveMode == MoveModeEnum.Dash.Code ||
                               ((waypoint.MoveMode == MoveModeEnum.Climb.Code || (waypoint.MoveMode == MoveModeEnum.Fly.Code && distance > 35) 
                                                                              || waypoint.MoveMode == MoveModeEnum.Jump.Code || (waypoint.MoveMode == MoveModeEnum.Climb.Code && distance < 20)) && avatar?.Name == "玛薇卡"));
            

            if (avatar != null && isPoint && !yellowBlood)
            {
                // 新赶路系统启用时跳过旧版赶路逻辑
                bool skipOldHurry = PartyConfig.UseNewHurrySystem;
                
                // 新赶路系统分支
                if (PartyConfig.UseNewHurrySystem)
                {
                    
                    // 初始化赶路配置（首次使用时）
                    if (string.IsNullOrEmpty(_hurryOnAvatar))
                    {
                        InitHurryOnConfig();
                    }

                    // 使用外部创建的 hurryOnState 实例（跨帧保持 RotationStableCount）
                    // diff 在本循环迭代的旋转阶段计算，首次进入时传递 0（旋转尚未发生，视为稳定状态）
                    var hurryOnResult = await TryHurryOnAsync(0, waypoint, distance, screen2, num, hurryOnState);
                    if (hurryOnResult)
                    {
                        // 赶路已处理，跳过当前帧的后续逻辑
                        continue;
                    }
                    
                    // 新版赶路未触发，跳过旧版赶路逻辑，避免两套系统冲突
                }
                else
                {
                    // 茶包版赶路逻辑已抽离到 TeapotHurryOnHelper.cs（选项1：position/additionalTimeInMs 按值传入）。
                    // 返回 true 表示原"赶路靠近超时-2"应 break 主循环。
                    if (await ExecuteTeapotHurryOnAsync(waypoint, nextWaypoint, nextDistance, distance, position, additionalTimeInMs, avatar, screen2, num, isPoint, yellowBlood, skipOldHurry, hurryOnBool, nextAvatarIndexStop, teapotState))
                    {
                        break;
                    }
                }
            
                //接近战斗点，确保行走位不是丝血
                if (waypoint?.Action == ActionEnum.Fight.Code && distance < 30 && _combatScenes?.GetAvatars().Count > 1)
                {
                    using (var bitmap = CaptureToRectArea())
                    {
                        var pixel = 0;

                        for (int i = 0; i < 2; i++)
                        {
                            using (var bitmap2 = CaptureToRectArea())
                            {
                                var pixelValue = bitmap2.SrcMat.At<Vec3b>(1010,814);
                                if (!(Math.Abs(pixelValue[0] - 34) <= 10 &&
                                      Math.Abs(pixelValue[1] - 215) <= 10 &&
                                      Math.Abs(pixelValue[2] - 150) <= 10) && !(Math.Abs(pixelValue[0] - 50) <= 10 &&
                                                                                Math.Abs(pixelValue[1] - 204) <= 10 &&
                                                                                Math.Abs(pixelValue[2] - 255) <= 10))
                                {
                                    pixel += 1;
                                }
                                else
                                {
                                    pixel = 0;
                                }
                            }
                            await Task.Delay(50, ct);
                        }
                    
                        if (pixel >= 2)
                        {
                            if (distance < 10)
                            {
                                // 抬起w键
                                Logger.LogInformation("到达战斗点附近-2");
                                Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                                return;
                            }
                            
                            Logger.LogInformation("当前行走角色血量仍过低，尝试切换人-2");

                            if (!string.IsNullOrWhiteSpace(PartyConfig.MainAvatarIndex))
                            {
                                var avatarCount = AvatarSwitchIndexDecisions.EffectiveAvatarCount(_combatScenes?.GetAvatars().Count);
                                var avatarIndex = int.Parse(PartyConfig.MainAvatarIndex);
                                
                                var nextAvatarIndex = AvatarSwitchIndexDecisions.NextAvatarIndex(avatarIndex, avatarCount);
                                if (_combatScenes?.SelectAvatar(nextAvatarIndex).Name == "枫原万叶" && 
                                    _combatScenes?.SelectAvatar(PathingConditionConfig.InitialMainAvatarIndex)?.Name != "枫原万叶")
                                {
                                    nextAvatarIndex = AvatarSwitchIndexDecisions.NextAvatarIndex(nextAvatarIndex, avatarCount);
                                }
                                
                                var avatar2 = _combatScenes?.SelectAvatar(avatarIndex);

                                await Delay(300, ct);

                                if (avatar2 != null && avatar2.IsActive(bitmap))
                                {
                                    PartyConfig.MainAvatarIndex = nextAvatarIndex.ToString();
                                    await SwitchAvatar(nextAvatarIndex.ToString());
                                }
                                else
                                {
                                    await SwitchAvatar(PartyConfig.MainAvatarIndex);
                                }
                            }
                            else
                            {
                                var avatarCount = AvatarSwitchIndexDecisions.EffectiveAvatarCount(_combatScenes?.GetAvatars().Count);
                                for (int i = 1; i <= avatarCount; i++)
                                {
                                    var avatar2 = _combatScenes?.SelectAvatar(i);
                                    if (avatar2 != null && avatar2.IsActive(bitmap))
                                    {
                                        var nextAvatarIndex = AvatarSwitchIndexDecisions.NextAvatarIndex(i, avatarCount);
                                        if (_combatScenes?.SelectAvatar(nextAvatarIndex).Name == "枫原万叶" && 
                                            _combatScenes?.SelectAvatar(PathingConditionConfig.InitialMainAvatarIndex)?.Name != "枫原万叶")
                                        {
                                            nextAvatarIndex = AvatarSwitchIndexDecisions.NextAvatarIndex(nextAvatarIndex, avatarCount);
                                        }
                                        await SwitchAvatar(nextAvatarIndex.ToString());
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    
                    //防转圈，卡地形
                    if (distance < 15)
                    {
                        distanceCount = distanceCount+2;
                    }
                    else if (distance < 30)
                    {
                        distanceCount = distanceCount+1;
                    }
                    else if (waypoint?.MoveMode == MoveModeEnum.Dash.Code)
                    {
                        if (distance > 4 && distance < 30)distanceCount = distanceCount+1; 
                    }
                    
                    if (distanceCount > 25)
                    {
                        Logger.LogWarning("战斗节点靠近超时-1 {t}",distanceCount);
                        distanceCount = 0;
                        break;
                    }
                                     
                }
            }
            
            var stopThresholdNonPoint = closeDistance.HasValue ? closeDistance.Value : (retryDis > 6 ? 15 : 4);
            if (distance < (!isPoint ? stopThresholdNonPoint : (teapotState.HurryOnLogo? 4 : 6)))
            {
                if (teapotState.HurryOnIn)
                {
                    using (var bitmap2 = CaptureToRectArea())
                    {
                        var pos3 = bitmap2.SrcMat.At<Vec3b>(978, 1692);
                        var pos4 = bitmap2.SrcMat.At<Vec3b>(995, 1702);
                        var colorDifference2 = Math.Sqrt(
                            Math.Pow(pos3.Item0 - pos4.Item0, 2) + // 蓝通道差值的平方
                            Math.Pow(pos3.Item1 - pos4.Item1, 2) + // 绿通道差值的平方
                            Math.Pow(pos3.Item2 - pos4.Item2, 2)   // 红通道差值的平方
                        );
                
                        if (colorDifference2 < 15)
                        {
                            Logger.LogWarning("到达路径点附近-9");
                            await SwitchAvatar(nextAvatarIndexStop); 
                        
                        } 
                    }
                   
                }
                // if(!isPoint)Logger.LogWarning("到达路径点附近tt-{t}",isPoint);
                Logger.LogDebug("到达路径点附近");
                break;
            }

            if (distance > 500)
            {
                if (pathExecutorSuspend.CheckAndResetSuspendPoint() && !TryConsumeRevivalSignal())
                {
                    throw new RetryNoCountException("可能暂停导致路径过远，重试一次此路线！");
                }
                else
                {
                    distanceTooFarRetryCount++;
                    
                    if (position is not { X: 0, Y: 0 })
                    {
                        // 坐标不是(0,0)但距离>500
                        var distToPrev = prevNotTooFarPosition is not { X: 0, Y: 0 } 
                            ? Math.Sqrt(Math.Pow(position.X - prevNotTooFarPosition.X, 2) + Math.Pow(position.Y - prevNotTooFarPosition.Y, 2))
                            : double.MaxValue;
                        
                        var isMisidentification = prevNotTooFarPosition is not { X: 0, Y: 0 } && distToPrev > 200;
                        
                        if (!isMisidentification)
                        {
                            distanceTooFarRetryCount++;
                            // 坐标稳定但距离一直>500，可能是传送后首次识别就错了
                            // 累积超过10次后触发全局匹配重新定位
                            if (distanceTooFarRetryCount > 3)
                            {
                                Logger.LogWarning($"距离持续>500达{distanceTooFarRetryCount}次，触发全局匹配重新定位");
                                Navigation.Reset();
                                prevNotTooFarPosition = default;
                                distanceTooFarRetryCount = 0;
                                await Delay(200, ct);
                                continue;
                            }
                            prevNotTooFarPosition = position;
                        }
                        else
                        {
                            // 坐标跳变了，可能是误识别
                            Logger.LogWarning($"距离异常，识别坐标({position.X},{position.Y})距目标={Math.Round(distance)}，疑似误识别(第{distanceTooFarRetryCount}次)");
                            position = prevNotTooFarPosition;
                            Navigation.SetPrevPosition(prevNotTooFarPosition.X, prevNotTooFarPosition.Y);
                            
                            if (distanceTooFarRetryCount > 20)
                            {
                                if (isPoint)
                                {
                                    Logger.LogWarning($"连续{distanceTooFarRetryCount}次距离异常，放弃此路径点");
                                    throw new HandledException("目标距离持续过远，可能是坐标识别异常，放弃此路径！");
                                }
                                else
                                {
                                    return;
                                }
                            }
                            
                            // 每5次尝试一次ResolveAnomalies检查是否有界面遮挡
                            if (distanceTooFarRetryCount % 5 == 0)
                            {
                                await ResolveAnomalies(screen2);
                            }
                            await Delay(100, ct);
                            continue;
                        }
                    }
                    else
                    {
                        // 坐标是(0,0)，完全识别失败，主动恢复
                        if (distanceTooFarRetryCount > 8)
                        {
                            if (isPoint)
                            {
                                throw new HandledException("重试多次后，当前点位无法被识别，放弃此路径！");
                            }
                            else
                            {
                                return; 
                            }
                        }
                        
                        if (isPoint)
                        {
                            Logger.LogWarning($"坐标识别失败(0,0)，目标({waypoint.X},{waypoint.Y})，第{distanceTooFarRetryCount}次主动恢复");
                        }
                        await ResolveAnomalies(screen2);
                        Navigation.Reset();
                        if (prevNotTooFarPosition is not { X: 0, Y: 0 })
                        {
                            if (isPoint) Logger.LogInformation($"重置到上次正确识别的坐标 ({prevNotTooFarPosition.X},{prevNotTooFarPosition.Y})");
                            Navigation.SetPrevPosition(prevNotTooFarPosition.X, prevNotTooFarPosition.Y);
                        }
                        await Delay(500, ct);
                        continue;
                    }
                }
            } else
            {
                distanceTooFarRetryCount = 0; // 正常距离时重置计数器
                prevNotTooFarPosition = position;
            }

            // 非攀爬状态下，检测是否卡死（脱困触发器）
            if (waypoint?.MoveMode != MoveModeEnum.Climb.Code && isGetOut)
            {
                if ((DateTime.UtcNow - lastPositionRecord).TotalMilliseconds > 1000 + additionalTimeInMs)
                {
                    lastPositionRecord = DateTime.UtcNow;
                    prevPositions.Add(position);
                    if (prevPositions.Count > 8)
                    {
                        var delta = prevPositions[^1] - prevPositions[^8];
                        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 3)
                        {
                            // === 联机模式：路上复苏后卡住的统一处理 ===
                            // 优先于原有随机脱困逻辑：复苏后残血玩家随机扭动 / 跳跃大概率走不动，
                            // 直接传送神像回血 + 抛 RetryException 跳到下一段汇合更可靠
                            // 设计参考 design.md §5 / bugfix.md 2.3
                            // 注意：此处不修改 _inTrap 计数（不增不减），保持隐含语义；段入口段循环重置即可
                            if (TryConsumeRevivalSignal())
                            {
                                bool __retryForce = _pendingRevivalEscalation == (int)BetterGenshinImpact.GameTask.AutoHoeing.Services.RevivalEscalationAction.RetrySegment;
                                Logger.LogWarning("[联机] 路上检测到复苏 + 位置不变（疑似复苏后卡住），跳过随机脱困，前往七天神像回血{Force}", __retryForce ? "（重试模式强制去神像）" : "");
                                await TpStatueOfTheSeven(requireLoadingScreen: MultiplayerCoordinator != null, forceStatue: __retryForce);
                                throw new RetryException("联机：路上复苏 + 卡住，神像回血后跳到下一段汇合");
                            }

                            //停止吃药
                            var autoEatCount = PathingConditionConfig.AutoEatCount;
                            var recoverCount =  AutoFightTask.RecoverCount;
                            PathingConditionConfig.AutoEatCount = 3;
                            AutoFightTask.RecoverCount = 3;
                            
                            if (_inTrap > 0)
                            {
                                throw new RetryException("此路线出现3次卡死，重试一次路线或放弃此路线！");
                            }
                            
                            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                            Simulation.SendInput.SimulateAction(GIActions.Drop);
                            await Delay(1000, ct);
                            Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                            Simulation.SendInput.SimulateAction(GIActions.Jump);
                            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                            
                            if (_lastWaypoint is not null && _inTrap == 0 && !_faceToMark)
                            {
                                _faceToMark = true;
                                Logger.LogWarning("尝试朝向上一个节点...");
                                Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                                Simulation.SendInput.SimulateAction(GIActions.Drop);
                                await Delay(500, ct);
                                Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                                Simulation.SendInput.SimulateAction(GIActions.Jump);
                                
                                await FaceTo(_lastWaypoint);
                                Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                                await Delay(1500, ct);
                                await FaceTo(waypoint);
                                await Delay(500, ct);
                                Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                                Simulation.SendInput.SimulateAction(GIActions.Drop);
                                Simulation.SendInput.SimulateAction(GIActions.Jump);
                                Logger.LogInformation("尝试继续行走...");
                                
                                PathingConditionConfig.AutoEatCount = autoEatCount;
                                AutoFightTask.RecoverCount = recoverCount;
                                continue;
                            }
                            
                            _inTrap++;
                            Logger.LogWarning("疑似卡死，尝试随机脱离...");
                            //调用脱困代码，由TrapEscaper接管移动
                            await _trapEscaper.RotateAndMove();
                            await _trapEscaper.MoveTo(waypoint);
                            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                            Logger.LogInformation("卡死脱离结束");

                            PathingConditionConfig.AutoEatCount = autoEatCount;
                            AutoFightTask.RecoverCount = recoverCount;
                            continue;
                        }
                    }
                }
            }

            // 旋转视角
            targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
            //执行旋转
            var diff = _rotateTask.RotateToApproach(targetOrientation, screen2);
            if (num > 20)
            {
                // diff == null 表示本轮未真实测量到角度（抢锁失败），不累加也不清零卡死计数，保持上一轮状态
                if (diff.HasValue && Math.Abs(diff.Value) > 5)
                {
                    consecutiveRotationCountBeyondAngle++;
                }
                else if (diff.HasValue)
                {
                    consecutiveRotationCountBeyondAngle = 0;
                }

                if (consecutiveRotationCountBeyondAngle > 10)
                {
                    // 松W键，站定好转向，转完重新按下W继续走
                    Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                    await WaitUntilRotatedTo(targetOrientation, 2);
                    Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                }
            }

            // 根据指定方式进行移动
            if (waypoint.MoveMode == MoveModeEnum.Fly.Code)
            {
                var isFlying = Bv.GetMotionStatus(screen2) == MotionStatus.Fly;
                if (!isFlying)
                {
                    Debug.WriteLine("未进入飞行状态，按下空格");
                    Simulation.SendInput.SimulateAction(GIActions.Jump);
                    await Delay(200, ct);
                }

                await Delay(100, ct);
                continue;
            }

            if (waypoint.MoveMode == MoveModeEnum.Jump.Code)
            {
                Simulation.SendInput.SimulateAction(GIActions.Jump);
                await Delay(200, ct);
                continue;
            }

            // 只有设置为run才会一直疾跑
            if (waypoint.MoveMode == MoveModeEnum.Run.Code)
            {
                if (distance > ((waypoint.Action == ActionEnum.Fight.Code ? 5 :20))!= fastMode) // 距离大于20时可以使用疾跑/自由泳
                {
                    if (fastMode)
                    {
                        Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyUp);
                    }
                    else
                    {
                        if (teapotState.SprintMouseLogo)
                        {
                            // Logger.LogInformation("333");
                            Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyDown);
                        }
                    }

                    fastMode = !fastMode;
                }
            }
            else if (waypoint.MoveMode == MoveModeEnum.Dash.Code)
            {
                if (distance > (waypoint.Action == ActionEnum.Fight.Code ? 5 : (!teapotState.HurryOnLogo ? 35 : 20))) // 距离大于25时可以使用疾跑
                {
                    if (Math.Abs((fastModeColdTime - DateTime.UtcNow).TotalMilliseconds) > 1000) //冷却一会
                    {
                        fastModeColdTime = DateTime.UtcNow;
                        if (!teapotState.HurryOnLogo && dushed && teapotState.Mwktiao)
                        {
                            dushed = false;
                            Task.Run(async () =>
                            {
                                Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyDown);
                                await Delay(200, ct);
                                Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyUp);
                                dushed = true;
                            }, ct);
                        }
                        else
                        {
                            Simulation.SendInput.SimulateAction(GIActions.SprintMouse);
                        }
                    }
                }
            }
            else if (waypoint.MoveMode != MoveModeEnum.Climb.Code) //否则自动短疾跑
            {
                // 使用 E 技能
                if (distance > 10 && !string.IsNullOrEmpty(PartyConfig.GuardianAvatarIndex) &&
                    double.TryParse(PartyConfig.GuardianElementalSkillSecondInterval, out var s))
                {
                    if (s < 1)
                    {
                        Logger.LogWarning("元素战技冷却时间设置太短，不执行！");
                        return;
                    }

                    var ms = s * 1000;
                    if ((DateTime.UtcNow - _elementalSkillLastUseTime).TotalMilliseconds > ms)
                    {
                        // 可能刚切过人在冷却时间内
                        if (num <= 5 && (!string.IsNullOrEmpty(PartyConfig.MainAvatarIndex) &&
                                         PartyConfig.GuardianAvatarIndex != PartyConfig.MainAvatarIndex))
                        {
                            await Delay(800, ct); // 总共1s
                        }

                        await UseElementalSkill();
                        _elementalSkillLastUseTime = DateTime.UtcNow;
                    }
                }

                // 自动疾跑
                if (distance > 20 && PartyConfig.AutoRunEnabled)
                {
                    // var dashTime = nextDistance > 90 ? 3500 : 2500;
                    if (Math.Abs((fastModeColdTime - DateTime.UtcNow).TotalMilliseconds) > 2500) //冷却时间2.5s，回复体力用
                    {
                        fastModeColdTime = DateTime.UtcNow;
                        Simulation.SendInput.SimulateAction(GIActions.SprintMouse);
                    }
                }
            }

            // 使用小道具
            if (PartyConfig.UseGadgetIntervalMs > 0)
            {
                if ((DateTime.UtcNow - _useGadgetLastUseTime).TotalMilliseconds > PartyConfig.UseGadgetIntervalMs)
                {
                    Simulation.ReleaseAllKey();
                    Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget);
                    _useGadgetLastUseTime = DateTime.UtcNow;
                }
            }

            await Delay(100, ct);
            
        }
        
        // 抬起w键
        Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
    }

    private async Task UseElementalSkill()
    {
        if (string.IsNullOrEmpty(PartyConfig.GuardianAvatarIndex))
        {
            return;
        }

        await Delay(200, ct);

        // 切人
        Logger.LogInformation("切换盾、回血角色，使用元素战技");
        var avatar = await SwitchAvatar(PartyConfig.GuardianAvatarIndex, true);
        if (avatar == null)
        {
            return;
        }

        // 钟离往身后放柱子
        if (avatar.Name == "钟离")
        {
            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
            await Delay(50, ct);
            Simulation.SendInput.SimulateAction(GIActions.MoveBackward);
            await Delay(200, ct);
        }

        avatar.UseSkill(PartyConfig.GuardianElementalSkillLongPress);

        // 钟离往身后放柱子 后继续走路
        if (avatar.Name == "钟离")
        {
            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
        }
    }

    /// <summary>
    /// 精确接近目标 waypoint。
    /// <paramref name="closeDistance"/> 默认 2.0：等价于原硬编码 `Navigation.GetDistance(...) < 2` 行为；
    /// 联机万叶聚物战后回点分支显式传 1.0，缩小停点误差让非万叶玩家落点覆盖万叶 HoldE 风场拾取半径。
    /// <paramref name="tailDelayMs"/> 默认 null：走原 `_hurryOnAvatar 空 ? 1000 : 300` 三元逻辑；
    /// 联机万叶聚物战后回点分支显式传 0，跳过到点后的硬编码 1s 停顿。
    /// 单机 / 联机其它调用点不传新参数即保持原行为，单机零回归。
    /// <summary>
    /// 精确接近目标 waypoint。
    /// <paramref name="closeDistance"/> 默认 2.0：等价于原硬编码 `Navigation.GetDistance(...) < 2` 行为；
    /// 联机万叶聚物战后回点分支显式传 1.0，缩小停点误差让非万叶玩家落点覆盖万叶 HoldE 风场拾取半径。
    /// <paramref name="tailDelayMs"/> 默认 null：走原 `_hurryOnAvatar 空 ? 1000 : 300` 三元逻辑；
    /// 联机万叶聚物战后回点分支显式传 0，跳过到点后的硬编码 1s 停顿。
    /// <paramref name="maxSteps"/> 默认 25：每步约 60ms + 20ms ≈ 80ms，整体上限约 2 秒（适合长距离最后定位）；
    /// 联机万叶聚物战后回点分支已经先用 MoveTo 粗接近至 < 4，故只需少量精接近步数即可，传 10（约 0.8 秒）即可。
    /// 单机 / 联机其它调用点不传新参数即保持原行为，单机零回归。
    /// </summary>
    public async Task MoveCloseTo(WaypointForTrack waypoint, double closeDistance = 2.0, int? tailDelayMs = null, int maxSteps = 25, string? fastSyncId = null, WaypointForTrack? fastSyncWaypoint = null, bool escapeClimbOnReturn = false)
    {
        ImageRegion screen;
        Point2f position;
        int targetOrientation;
        bool fastReported = false;  // 抢报一次性短路 bool（fastsync-redesign-parameter-passing spec）
        Logger.LogDebug("精确接近目标点，位置({x2},{y2})", $"{waypoint.GameX:F1}", $"{waypoint.GameY:F1}");

        // 战斗结束后小地图可能短暂消失，等待派蒙头像出现（小地图可见的标志）。
        // 与 MoveTo 入口同款机制：避免循环每步 GetPosition 返回 (0,0) 触发 ResolveAnomalies +
        //   targetOrientation 跳动 → 视角剧烈抖动 → maxSteps 耗光超时（实测联机战后回点必现）。
        // 1500ms 超时不阻塞主流程，恢复失败仍然继续走（兜底逻辑沿用 prePosition / 全局匹配）。
        {
            using var checkScreen = CaptureToRectArea();
            if (!Bv.IsInMainUi(checkScreen))
            {
                var waitStart = DateTime.UtcNow;
                var recovered = false;
                while ((DateTime.UtcNow - waitStart).TotalMilliseconds < 2000)
                {
                    await Delay(100, ct);
                    using var retryScreen = CaptureToRectArea();
                    if (Bv.IsInMainUi(retryScreen))
                    {
                        recovered = true;
                        break;
                    }
                }
                if (!recovered)
                {
                    Logger.LogWarning("MoveCloseTo 等待主界面恢复超时(2000ms)，继续执行");
                }
            }
        }

        var stepsTaken = 0;
        while (!ct.IsCancellationRequested)
        {
            stepsTaken++;
            if (stepsTaken > maxSteps)
            {
                Logger.LogWarning("精确接近超时");
                break;
            }

            screen = CaptureToRectArea();

            EndJudgment(screen);

            // hoeing-return-fightpoint-climb-detect-drop:
            // 仅回点路径（escapeClimbOnReturn==true）启用攀爬脱离；其它所有调用方默认 false 零感知。
            // 复用本帧 screen（无副作用纯查询），检测到攀爬即发一次 Drop，Delay(500) 冷却避免连发。
            if (escapeClimbOnReturn && Bv.GetMotionStatus(screen) == MotionStatus.Climb)
            {
                Logger.LogInformation("[联机] 回点检测到攀爬，发送 Drop 脱离");
                Simulation.SendInput.SimulateAction(GIActions.Drop);
                await Delay(500, ct);
            }

            position = await GetPosition(screen, waypoint);
            var distance = Navigation.GetDistance(waypoint, position);

            // === 路径同步点抢报（fastsync-redesign-parameter-passing spec / OQ-7=a）===
            // fastSyncId == null（单机/未启用）→ ShouldFastReportInPathing 短路返回 false
            // fastSyncWaypoint 非 null → 用「到 sync waypoint 的距离」判断
            var __fastDist = fastSyncWaypoint != null
                ? Navigation.GetDistance(fastSyncWaypoint, position)
                : distance;
            if (FastSyncDecisions.ShouldFastReportInPathing(
                    __fastDist,
                    MultiplayerCoordinator?.EffectiveConfig.FastSyncPathingDistance ?? 10.0,
                    fastSyncId,
                    isMultiplayer: MultiplayerCoordinator != null,
                    isConnected: MultiplayerCoordinator?.IsConnected ?? false,
                    alreadyReported: fastReported,
                    fastSyncEnabled: MultiplayerCoordinator?.EffectiveConfig.FastSyncPointEnabled ?? false))
            {
                fastReported = true;
                var __progress = ComputeProgress(CurWaypoints.Item1, CurWaypoint.Item1);
                try
                {
                    // fire-and-forget：上报后立即返回，不阻塞 MoveCloseTo 主流程
                    await MultiplayerCoordinator!.FastReportAsync(fastSyncId!, __progress);
                    Logger.LogInformation("[联机][FastSync] MoveCloseTo 抢报命中 syncId={SyncId} dist={Dist:F2}", fastSyncId, __fastDist);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "[联机][FastSync] MoveCloseTo 抢报异常，已忽略 syncId={SyncId}", fastSyncId);
                }
            }

            if (MoveCloseToDecisions.ShouldStop(distance, closeDistance))
            {
                Logger.LogDebug("已到达路径点");
                break;
            }

            targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
            // Logger.LogError("当前坐标1");
            await WaitUntilRotatedTo(targetOrientation, 2);
            // 小碎步接近
            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
            Thread.Sleep(60);
            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
            // Simulation.SendInput.Keyboard.KeyDown(User32.VK.VK_W).Sleep(60).KeyUp(User32.VK.VK_W);
            await Delay(20, ct);
        }

        Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);

        // 到达目的地后停顿（默认行为：_hurryOnAvatar 空=1000ms / 非空=300ms；显式覆盖：tailDelayMs 非 null 时使用，0 即跳过）
        var tail = MoveCloseToDecisions.ComputeTailDelayMs(tailDelayMs, string.IsNullOrEmpty(_hurryOnAvatar));
        if (tail > 0)
        {
            await Delay(tail, ct);
        }
    }

    private async Task BeforeMoveCloseToTarget(WaypointForTrack waypoint)
    {
        if (waypoint.MoveMode == MoveModeEnum.Fly.Code && waypoint.Action == ActionEnum.StopFlying.Code)
        {
            await ActionFactory.GetBeforeHandler(ActionEnum.StopFlying.Code).RunAsync(ct, waypoint);
        }
    }

    private async Task BeforeMoveToTarget(WaypointForTrack waypoint)
    {
        if (waypoint.Action == ActionEnum.UpDownGrabLeaf.Code)
        {
            Simulation.SendInput.Mouse.MiddleButtonClick();
            await Delay(300, ct);
            var screen = CaptureToRectArea();
            var position = await GetPosition(screen, waypoint);
            var targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
            // Logger.LogError("67858677");
            await WaitUntilRotatedTo(targetOrientation, 10);
            var handler = ActionFactory.GetBeforeHandler(waypoint.Action);
            await handler.RunAsync(ct, waypoint);
        }
        else if (waypoint.Action == ActionEnum.LogOutput.Code)
        {
            Logger.LogInformation(waypoint.LogInfo);
        }
    }

    private async Task AfterMoveToTarget(WaypointForTrack waypoint, Waypoint? nextWaypoint = null)
    {
        if (waypoint.Action == ActionEnum.NahidaCollect.Code
            || waypoint.Action == ActionEnum.PickAround.Code
            || waypoint.Action == ActionEnum.Fight.Code
            || waypoint.Action == ActionEnum.HydroCollect.Code
            || waypoint.Action == ActionEnum.ElectroCollect.Code
            || waypoint.Action == ActionEnum.AnemoCollect.Code
            || waypoint.Action == ActionEnum.PyroCollect.Code
            || waypoint.Action == ActionEnum.CombatScript.Code
            || waypoint.Action == ActionEnum.Mining.Code
            || waypoint.Action == ActionEnum.LinneaMining.Code
            || waypoint.Action == ActionEnum.Fishing.Code
            || waypoint.Action == ActionEnum.ExitAndRelogin.Code
            || waypoint.Action == ActionEnum.EnterAndExitWonderland.Code
            || waypoint.Action == ActionEnum.SetTime.Code
            || waypoint.Action == ActionEnum.UseGadget.Code
            || waypoint.Action == ActionEnum.PickUpCollect.Code)
        {
            var handler = ActionFactory.GetAfterHandler(waypoint.Action);
            await handler.RunAsync(ct, waypoint, PartyConfig);
            
            //统计结束战斗的次数
            if (waypoint.Action == ActionEnum.Fight.Code)
            {
                SuccessFight++;
            }

            if (PartyConfig.QuicklySkip && (_lastWaypoint?.Action == ActionEnum.Fight.Code || waypoint.Action == ActionEnum.Fight.Code || nextWaypoint?.Action == ActionEnum.Fight.Code))
            {
                if (nextWaypoint?.Type != WaypointType.Teleport.Code)
                {
                    return;
                }
                
                await Delay(100, ct);
                return;
            }

            if (waypoint.Action == ActionEnum.CombatScript.Code)
            {
                await Delay(PartyConfig.CombatScriptEndDelayMs>0 ? PartyConfig.CombatScriptEndDelayMs : 1, ct);
            }
            else
            {
                await Delay(895, ct);
            }
        }
    }

    private async Task<Avatar?> SwitchAvatar(string index, bool needSkill = false , PathingTask? pathingTask = null, bool? forceRefresh = false)
    {
        if (string.IsNullOrEmpty(index) && !(int.TryParse(index, out var idx) && _combatScenes?.GetAvatars().Count <= idx))
        {
            return null;
        }

        var avatar = _combatScenes?.SelectAvatar(int.Parse(index));
        if (avatar == null) return null;
        if (needSkill && !avatar.IsSkillReady())
        {
            Logger.LogInformation("角色{Name}技能未冷却，跳过。", avatar.Name);
            return null;
        }
        
        var success = avatar.TrySwitch(15);
        if (success)
        {
            await Delay(100, ct);
            return avatar;
        }

        using (var bitmap = CaptureToRectArea())
        {
            var confirmRectArea = bitmap.Find(AutoFightAssets.Get(bitmap).ConfirmRa);
            if (!confirmRectArea.IsEmpty())
            {
                Simulation.ReleaseAllKey();
                if(PathingConditionConfig.AutoEatCount <2)PathingConditionConfig.AutoEatCount ++;
                Logger.LogInformation("死亡，点击确认-s1 {t}",PathingConditionConfig.AutoEatCount);
                confirmRectArea.Click();
                confirmRectArea.ClickTo(-100, 0);
                Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget);
            }
            
            var pixelValue = bitmap.SrcMat.At<Vec3b>(1010,814);
            // var pixelValue2 = bitmap.DeriveCrop(_combatScenes.SelectAvatar(1).IndexRect).SrcMat.At<Vec3b>(1, 1);
            // var pixelValue22 = bitmap.DeriveCrop(_combatScenes.SelectAvatar(2).IndexRect).SrcMat.At<Vec3b>(1, 1);
            if (pathingTask is not null && forceRefresh == true && !(Math.Abs(pixelValue[0] - 50) <= 10 &&
                                                                    Math.Abs(pixelValue[1] - 204) <= 10 &&
                                                                    Math.Abs(pixelValue[2] - 255) <= 10))
            {
                // Logger.LogInformation("切换失败，尝试识别角色-1{t} {t2} {3}",pixelValue[0],pixelValue[1],pixelValue[2]);
                await ValidateGameWithTask(pathingTask,forceRefresh);
            }
        }
        
        Logger.LogInformation("尝试切换角色{Name}失败！ {t}", avatar.Name,forceRefresh);
        return null;
    }
    
    private async Task<Avatar?> SwitchAvatar2(string index, bool needSkill = false , PathingTask? pathingTask = null, bool? forceRefresh = false)
    {
        if (string.IsNullOrEmpty(index) && !(int.TryParse(index, out var idx) && _combatScenes?.GetAvatars().Count <= idx))
        {
            return null;
        }

        var avatar = _combatScenes?.SelectAvatar(int.Parse(index));
        if (avatar == null) return null;
        if (needSkill && !avatar.IsSkillReady())
        {
            Logger.LogInformation("角色{Name}技能未冷却，跳过。", avatar.Name);
            return null;
        }
        
        var success = avatar.TrySwitch2(5);
        if (success)
        {
            await Delay(100, ct);
            return avatar;
        }

        using (var bitmap = CaptureToRectArea())
        {
            var confirmRectArea = bitmap.Find(AutoFightAssets.Get(bitmap).ConfirmRa);
            if (!confirmRectArea.IsEmpty())
            {
                Simulation.ReleaseAllKey();
                if(PathingConditionConfig.AutoEatCount <2)PathingConditionConfig.AutoEatCount ++;
                Logger.LogInformation("死亡，点击确认-s2 {t}",PathingConditionConfig.AutoEatCount);
                confirmRectArea.Click();
                confirmRectArea.ClickTo(-100, 0);
                Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget);
            }
            
            var pixelValue = bitmap.SrcMat.At<Vec3b>(1010,814);
            // var pixelValue2 = bitmap.DeriveCrop(_combatScenes.SelectAvatar(1).IndexRect).SrcMat.At<Vec3b>(1, 1);
            // var pixelValue22 = bitmap.DeriveCrop(_combatScenes.SelectAvatar(2).IndexRect).SrcMat.At<Vec3b>(1, 1);
            if (pathingTask is not null && forceRefresh == true && !(Math.Abs(pixelValue[0] - 50) <= 10 &&
                                                                    Math.Abs(pixelValue[1] - 204) <= 10 &&
                                                                    Math.Abs(pixelValue[2] - 255) <= 10))
            {
                // Logger.LogInformation("切换失败，尝试识别角色-1{t} {t2} {3}",pixelValue[0],pixelValue[1],pixelValue[2]);
                await ValidateGameWithTask(pathingTask,forceRefresh);
            }
        }
        
        Logger.LogInformation("尝试切换角色{Name}失败！ {t}", avatar.Name,forceRefresh);
        return null;
    }
    
    /// <summary>
    /// 根据时间在两个点之间插值。
    /// </summary>
    /// <param name="startPoint">起点坐标</param>
    /// <param name="endPoint">终点坐标</param>
    /// <param name="startTime">起始时间</param>
    /// <param name="midTime">中间时间</param>
    /// <param name="endTime">结束时间</param>
    /// <returns>中间点坐标</returns>
    public static Point2f InterpolatePointByTime(
        Point2f startPoint,
        Point2f endPoint,
        DateTime startTime,
        DateTime midTime,
        DateTime endTime)
    {
        // 计算时间差
        double totalMillis = (endTime - startTime).TotalMilliseconds;
        double midMillis = (midTime - startTime).TotalMilliseconds;

        // 防止除以0
        if (totalMillis == 0)
            return startPoint;

        // 计算比例
        float t = (float)(midMillis / totalMillis);
        if (t>1.0f)
        {
            t = 1.0f;
        }
        // 插值计算
        float x = startPoint.X + (endPoint.X - startPoint.X) * t;
        float y = startPoint.Y + (endPoint.Y - startPoint.Y) * t;

        return new Point2f(x, y);
    }
    
    private  Point2f prePosition;
    private  DateTime preTime;
    private DateTime _prePositionUpdateTime = DateTime.UtcNow;
    // 与 prePosition / _prePositionUpdateTime 配对：标识 prePosition 所属地图 + 匹配方式，避免跨地图兜底复用。
    // 详见 .kiro/specs/pathexecutor-teleport-fresh-position-fallback-fix/design.md §Glossary / Fix Implementation 改动 2。
    // 任何刷新 prePosition 的位置必须同步刷新此字段；任何失效 prePosition 的位置必须同步置 string.Empty。
    private string _prePositionMapKey = string.Empty;
    //自动构造点位的最大时间
    private int maxAutoPositionTime=10000; 
    private async Task WaitForCloseMap(int maxAttempts, int delayMs)
    {
        await Delay(delayMs, ct);
        for (var i = 0; i < maxAttempts; i++)
        {
            using var capture = CaptureToRectArea();
            if (Bv.IsInMainUi(capture))
            {
                return;
            }

            await Delay(delayMs, ct);
        }
        
    }

    private async Task<Point2f> GetPosition(ImageRegion imageRegion, WaypointForTrack waypoint)
    {
        return (await GetPositionAndTime(imageRegion, waypoint)).point;
    }
    //
    public bool GetPositionAndTimeSuspendFlag = false;
    private async Task<(Point2f point,int additionalTimeInMs)> GetPositionAndTime(ImageRegion imageRegion, WaypointForTrack waypoint,bool isPoint = true)
    {
        var position = Navigation.GetPosition(imageRegion, waypoint.MapName, waypoint.MapMatchMethod);
        int time = 0;
        if (position == new Point2f())
        {
            if (!Bv.IsInMainUi(imageRegion))
            {
                if (isPoint)
                {
                    Logger.LogDebug("小地图位置定位失败，且当前不是主界面，进入异常处理");
                    await ResolveAnomalies(imageRegion);
                    // 异常处理后重新截图并重试获取坐标
                    imageRegion = CaptureToRectArea();
                    position = Navigation.GetPosition(imageRegion, waypoint.MapName, waypoint.MapMatchMethod);
                    if (position == new Point2f())
                    {
                        Logger.LogDebug("异常处理后重试获取坐标仍然失败");
                    }
                    else
                    {
                        Logger.LogInformation("异常处理后重试获取坐标成功: ({x},{y})", position.X, position.Y);
                    }
                }
                else
                {
                    return (position,time);
                }
            }
        }

        var distance = Navigation.GetDistance(waypoint, position);
        //中途暂停过，地图未识别到
        if (position is {X:0,Y:0} && GetPositionAndTimeSuspendFlag && !TryConsumeRevivalSignal())
        {
            GetPositionAndTimeSuspendFlag = false;
            throw new RetryNoCountException("可能暂停导致路径过远，重试一次此路线！");
        }
        //何时处理   pathTooFar  路径过远  unrecognized 未识别
        if ((position is {X:0,Y:0} && waypoint.Misidentification.Type.Contains("unrecognized")) || (distance>500 && waypoint.Misidentification.Type.Contains("pathTooFar")))
        {
            if (waypoint.Misidentification.HandlingMode == "previousDetectedPoint")
            {
                // 三元判定：prePosition 非空 + 5 秒新鲜 + mapKey 一致。
                // 详见 .kiro/specs/pathexecutor-teleport-fresh-position-fallback-fix/design.md §Fix Implementation 改动 4。
                var __mapKey = $"{waypoint.MapName}|{waypoint.MapMatchMethod}";
                var __ageMs = (DateTime.UtcNow - _prePositionUpdateTime).TotalMilliseconds;
                var __sameMapKey = _prePositionMapKey == __mapKey;
                var __fallbackUsable = PrePositionFallbackDecisions.IsFallbackUsable(prePosition == default, __ageMs, __sameMapKey);

                if (__fallbackUsable)
                {
                    position = prePosition;
                    if (isPoint)
                    {
                        imageRegion = CaptureToRectArea();
                        var retryPos = Navigation.GetPosition(imageRegion, waypoint.MapName, waypoint.MapMatchMethod);
                        
                        if (retryPos is not { X: 0, Y: 0 })
                        {
                            position = retryPos;
                            prePosition = retryPos;
                            _prePositionUpdateTime = DateTime.UtcNow;
                            _prePositionMapKey = __mapKey;
                            Logger.LogInformation(@$"未识别到具体路径，重试成功");
                        }
                        else
                        {
                            // 重试失败，保持使用 prePosition
                            Logger.LogDebug("重试失败，使用prePosition=({px},{py})", prePosition.X, prePosition.Y);
                        }
                    }
                }
                else if (prePosition != default)
                {
                    // prePosition 失效（5 秒过期 / mapKey 不一致），重置导航触发全局匹配
                    Logger.LogWarning("prePosition 失效（age={AgeMs:F0}ms / sameMapKey={SameMapKey}），触发全局匹配重新定位",
                        __ageMs, __sameMapKey);
                    Navigation.Reset();
                    prePosition = default;
                    _prePositionMapKey = string.Empty;
                }
            }else if (waypoint.Misidentification.HandlingMode == "mapRecognition"){
                //大地图识别坐标
                DateTime start = DateTime.UtcNow;
                TpTask tpTask = new TpTask(ct);
                await tpTask.OpenBigMapUi(mapName: waypoint.MapName);
                try
                {
                    position =MapManager.GetMap(waypoint.MapName, waypoint.MapMatchMethod).ConvertGenshinMapCoordinatesToImageCoordinates(tpTask.GetPositionFromBigMap(waypoint.MapName));
                }
                catch (Exception e)
                {
                    Logger.LogInformation(@$"地图中心点识别失败！");
                }
               
                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                //Bv.IsInMainUi(imageRegion);
                await WaitForCloseMap(10,200);
                DateTime end = DateTime.UtcNow;
                time=(int)(end - start).TotalMilliseconds;
                Logger.LogInformation(@$"未识别到具体路径，打开地图计算中心点({position.X},{position.Y})");
            }
            
            /*if (prePosition!=default)
            {*/
                //position = InterpolatePointByTime(prePosition,new Point2f((float)waypoint.GameX,(float)waypoint.GameY),preTime,DateTime.Now,preTime.AddMilliseconds(maxAutoPositionTime));
                //Logger.LogInformation(@$"未识别到具体路径，预测其路径为（{position.X},{position.Y}）,开始结束点位为：（{prePosition.X},{prePosition.Y}）（{waypoint.GameX},{waypoint.GameY}）");
                //Point2f GetBigMapCenterPoint(string mapName)

               // Logger.LogInformation(@$"未识别到具体路径，打开地图计算中心点({position.X},{position.Y})");
                //position =prePosition;
           // }

        }
        else
        {
            if (position is not { X: 0, Y: 0 })
            {
                prePosition = position;
                _prePositionUpdateTime = DateTime.UtcNow;
                _prePositionMapKey = $"{waypoint.MapName}|{waypoint.MapMatchMethod}";
            }
            
            preTime = DateTime.UtcNow;
        }

        //Logger.LogDebug("识别到路径："+position.X+","+position.Y);
        return (position,time);
    }

    private async Task WaitUntilRotatedTo(int targetOrientation, int maxDiff)
    {
        // Logger.LogError("旋转视角2");
        if (await _rotateTask.WaitUntilRotatedTo(targetOrientation, maxDiff))
        {
            return;
        }
        await ResolveAnomalies();
        await _rotateTask.WaitUntilRotatedTo(targetOrientation, maxDiff);
    }

    /**
     * 处理各种异常场景
     * 需要保证耗时不能太高
     */
    public async Task ResolveAnomalies(ImageRegion? imageRegion = null)
    {
        if (imageRegion == null)
        {
            imageRegion = CaptureToRectArea();
        }

        // 一些异常界面处理
        var cookRa = imageRegion.Find(RecognitionAssets.Get("AutoSkip", "Cook", imageRegion));
        var closeRa = imageRegion.Find(RecognitionAssets.Get("AutoSkip", "PageCloseMain", imageRegion));
        var closeRa2 = imageRegion.Find(ElementAssets.Instance.PageCloseWhiteRo);
        var closeRa3 = imageRegion.Find(RecognitionAssets.Get("AutoSkip", "PageClose", imageRegion));
        var closeRa4 = imageRegion.Find(AutoFightAssets.Get(imageRegion).ConfirmRa);
        var closeRa5 = imageRegion.Find(ElementRecognition.Get("PartyBtnChooseView", imageRegion));
        var anyFound = cookRa.IsExist() || closeRa.IsExist() || closeRa2.IsExist() || closeRa3.IsExist() || closeRa4.IsExist() ||closeRa5.IsExist();
        if (anyFound)
        {
            // 排除大地图
            if (Bv.IsInBigMapUi(imageRegion))
            {
                return;
            }

            Logger.LogInformation("检测到其他界面，使用ESC关闭界面");
            Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
            await Delay(1000, ct); // 等待界面关闭
        }


        // 处理月卡
        await _blessingOfTheWelkinMoonTask.Start(ct);

        if (PartyConfig.AutoSkipEnabled)
        {
            // 判断是否进入剧情
            await AutoSkip();
        }
    }

    private async Task AutoSkip()
    {
        var ra = CaptureToRectArea();
        var disabledUiButtonRa = ra.Find(RecognitionAssets.Get("AutoSkip", "DisabledUiButton", ra));
        if (disabledUiButtonRa.IsExist())
        {
            Logger.LogWarning("进入剧情，自动点击剧情直到结束");
            if (_autoSkipTrigger == null)
            {
                _autoSkipTrigger = new AutoSkipTrigger(new AutoSkipConfig
                {
                    Enabled = true,
                    QuicklySkipConversationsEnabled = true, // 快速点击过剧情
                    ClosePopupPagedEnabled = true,
                    ClickChatOption = "优先选择最后一个选项",
                });
                _autoSkipTrigger.Init();
            }
            int noDisabledUiButtonTimes = 0;
            while (true)
            {
                ra = CaptureToRectArea();
                disabledUiButtonRa = ra.Find(RecognitionAssets.Get("AutoSkip", "DisabledUiButton", ra));
                if (disabledUiButtonRa.IsExist())
                {
                    _autoSkipTrigger.OnCapture(new CaptureContent(ra));
                    noDisabledUiButtonTimes = 0;
                }
                else
                {
                    noDisabledUiButtonTimes++;
                    if (noDisabledUiButtonTimes > 10)
                    {
                        Logger.LogInformation("自动剧情结束");
                        break;
                    }
                }
                await Delay(210, ct);
            }
        }
    }

    private void EndJudgment(ImageRegion ra)
    {
        if (EndAction != null && EndAction(ra))
        {
            throw new HandledException("达成结束条件，结束地图追踪");
        }
    }
}

/// <summary>
/// MoveCloseTo 判停 + 尾部 Delay 数值化为纯函数，让 PBT-1 / PBT-2 可在不依赖 Navigation / Delay
/// 的前提下守住默认参数下与原硬编码 (`< 2` / `1000` / `300`) 行为完全等价。
/// 详见 design.md §6 PBT-1 / PBT-2。
/// </summary>
public static class MoveCloseToDecisions
{
    /// <summary>判定是否应停止靠近：传入当前距离与可选阈值，返回是否应该 break。</summary>
    public static bool ShouldStop(double distance, double closeDistance) => distance < closeDistance;

    /// <summary>
    /// 计算尾部 Delay 毫秒数：tailDelayMs 非 null 时直接使用（含 0、负数 —— 调用方负责自然数语义）；
    /// null 时走 _hurryOnAvatar 三元逻辑（empty=1000ms / non-empty=300ms）。
    /// </summary>
    public static int ComputeTailDelayMs(int? tailDelayMs, bool hurryOnAvatarEmpty)
    {
        var defaultTail = hurryOnAvatarEmpty ? 1000 : 300;
        return tailDelayMs ?? defaultTail;
    }
}
