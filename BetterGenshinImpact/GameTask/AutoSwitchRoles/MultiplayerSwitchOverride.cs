#nullable enable
namespace BetterGenshinImpact.GameTask.AutoSwitchRoles;

/// <summary>
/// 联机切角色覆盖参数。仅当 AutoSwitchRolesTask 由联机执行层注入此对象时生效；
/// 单机调用方传 null → 全部走单机原行为（§Unchanged Behavior 第 1 条）。
/// 注入此对象后，Start 分流走联机专属方法 RunMultiplayerProbeModeAsync（号位动态探测），
/// 单机调用方（null）仍走 RunRecommendedModeAsync（固定号位映射），两条路径互不影响。
/// 覆盖项对应 R5（去切队）/ R6（配队页判定 MapCloseButton）/ R7（号位动态探测）/ R8（检测复用单机）。
/// </summary>
public sealed class MultiplayerSwitchOverride
{
    /// <summary>R5：联机不切队。true=跳过 SwitchPartyTask 且不读 SwitchPartyName。</summary>
    public bool SkipSwitchParty { get; init; } = true;

    /// <summary>
    /// R6/R7：配队页「已打开」判定委托。返回 true=右上角 MapCloseButton 存在=配队页已打开。
    /// 由执行层注入为基于 RecognitionAssets.Get("QuickTeleport","MapCloseButton") 的检测。
    /// 探测时复用同一委托做「点击候选后 MapCloseButton 是否消失」判定。
    /// 联机必注入；防御性 null 检查时按「配队页未打开」处理，触发重试。
    /// </summary>
    public System.Func<bool>? IsPairingPageOpen { get; init; }

    /// <summary>
    /// R7：联机号位候选点击坐标（1080P 基准），默认 = 单机 4 候选 (460,538)/(792,538)/(1130,538)/(1462,538)，从左到右。
    /// 探测从索引 0 开始逐格点击；命中索引 +1 即 2 号位候选。
    /// </summary>
    public (double X, double Y)[] PositionCoordinates { get; init; } =
        MultiplayerSwitchConstants.CandidateCoords;

    /// <summary>R7.8：联机最多处理的号位数（联机最多 2 个可操作角色）。</summary>
    public int MaxPositions { get; init; } = PerRouteSwitchConstantsMaxPositions;

    /// <summary>R7.2：每点击一个候选格子后、检测 MapCloseButton 前的等待毫秒数。</summary>
    public int ProbeClickWaitMs { get; init; } = MultiplayerSwitchConstants.ProbeClickWaitMs;

    /// <summary>
    /// 进入角色选择页后、开始识别头像前的等待毫秒数。默认 1000ms（与单机点号位后等待一致）。
    /// 用于保证角色列表加载完成后再识别，避免第一排角色未渲染就被下滑滚过而漏识别。
    /// </summary>
    public int SelectionPageLoadWaitMs { get; init; } = MultiplayerSwitchConstants.SelectionPageLoadWaitMs;

    /// <summary>R7.6：探测序列最大重试次数（4 候选全未命中算一轮失败，最多重试 2 次）。</summary>
    public int ProbeMaxRetries { get; init; } = MultiplayerSwitchConstants.ProbeMaxRetries;

    private const int PerRouteSwitchConstantsMaxPositions = 2;
}
