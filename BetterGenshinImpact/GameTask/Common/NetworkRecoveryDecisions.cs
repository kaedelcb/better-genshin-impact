using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.Common;

/// <summary>
/// 网络恢复调度的纯决策函数（PBT 友好，无 IO / 无静态可变状态）。
/// 集中四个决策点：是否发起恢复 / 是否允许启动新恢复 / ping 是否可发起暂停 /
/// ping 成功是否清除暂停，以及快速拖动环境恢复中多次开图失败后的断线弹窗文本判定。
/// 参考 bgi-implementation-patterns.md §1 决策函数纯化模式。
/// spec: network-recovery-statemachine-never-exits-suspend-fix / design.md §Correctness Properties。
/// </summary>
public static class NetworkRecoveryDecisions
{
    private static readonly string[] DisconnectDialogKeywords =
    [
        "连接已断开",
        "连接超时",
        "无法登录服务器",
        "网络错误",
    ];

    private const string LoginPromptKeyword = "点击进入";

    /// <summary>
    /// 多次无法确认地图操作后，是否到达本轮执行断线弹窗 OCR 检查的时机。
    /// 仅在连续失败达到阈值且按阈值步进时检查，不影响正常传送与偶发掉帧。
    /// </summary>
    public static bool ShouldCheckDisconnectPromptOnProbeFailure(int consecutiveProbeFailures, int threshold)
        => threshold > 0 && consecutiveProbeFailures >= threshold && consecutiveProbeFailures % threshold == 0;

    /// <summary>
    /// 从中心区域 OCR 文本中匹配明确的断线提示关键字（不包含宽泛的“提示/通知”）。
    /// 主界面叠加半透明断线弹窗时仅匹配断线关键字；已离开主界面时额外允许匹配登录门“点击进入”。
    /// </summary>
    public static string? MatchDisconnectPromptText(IEnumerable<string?> ocrTexts, bool allowLoginPrompt)
    {
        if (ocrTexts == null) return null;

        string? loginMatch = null;
        foreach (var raw in ocrTexts)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var normalized = string.Concat(raw.Where(c => !char.IsWhiteSpace(c)));
            foreach (var keyword in DisconnectDialogKeywords)
            {
                if (normalized.Contains(keyword, StringComparison.Ordinal))
                    return keyword;
            }
            if (allowLoginPrompt && loginMatch == null
                && normalized.Contains(LoginPromptKeyword, StringComparison.Ordinal))
            {
                loginMatch = LoginPromptKeyword;
            }
        }

        return loginMatch;
    }

    /// <summary>
    /// 决策 A：是否应当进入网络恢复块。只要处于任一挂起态即需要恢复
    /// （与原恢复块入口条件 `IsSuspendedByNetwork || IsSuspendedByWindow` 等价）。
    /// </summary>
    public static bool ShouldEnterRecovery(bool isSuspendedByNetwork, bool isSuspendedByWindow)
        => isSuspendedByNetwork || isSuspendedByWindow;

    /// <summary>
    /// 决策 B（单飞门控）：当前是否允许启动一个**新的** NetworkRecovery.Start。
    /// 仅当没有恢复正在进行时才允许；进行中一律跳过（不重启、不重置 RecoveryNetworkDone）。
    /// 编排层用 `_networkRecoveryGate.Wait(0)` 的结果反推 recoveryInProgress，副作用留在编排层。
    /// </summary>
    public static bool ShouldStartNewRecovery(bool recoveryInProgress)
        => !recoveryInProgress;

    /// <summary>
    /// 决策 C：是否应清除网络暂停（IsSuspendedByNetwork=false 且计数清零）。
    /// 权威依据是"恢复成功"（recoverySucceeded==true，即 RecoveryNetworkDone）；
    /// 或在"本轮未进入恢复"且 ping 明确成功（!pingFailed）时，保留原"ping 成功即清零"语义。
    /// 与 ping 抛异常解耦：恢复成功时无视 ping 结果直接清除。
    /// </summary>
    public static bool ShouldClearNetworkSuspend(bool recoverySucceeded, bool enteredRecovery, bool pingFailed)
        => recoverySucceeded || (!enteredRecovery && !pingFailed);

    /// <summary>
    /// 决策 D：是否累加 ping 失败计数（并在 >=3 时做 www.qq.com 二次判定/回置）。
    /// 仅当"本轮未进入恢复"且 ping 失败时才累加——即 ping 只在**未处于恢复语境**下驱动暂停。
    /// 恢复已成功（recoverySucceeded）或本轮进入过恢复（enteredRecovery）时一律不累加、不回置，
    /// 避免恒失败的 ping 覆盖恢复结果（D-3/D-5 解耦）。
    /// </summary>
    public static bool ShouldCountPingFailure(bool recoverySucceeded, bool enteredRecovery, bool pingFailed)
        => !recoverySucceeded && !enteredRecovery && pingFailed;
}
