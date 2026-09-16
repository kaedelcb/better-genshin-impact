#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>Pull 执行结果（route-anchor · 阶段 4 接线用）。</summary>
public enum RouteAnchorPullExecutionResult
{
    /// <summary>没有待执行命令（无需动作）。</summary>
    NoCommand = 0,

    /// <summary>已成功中断当前路线并确认退出，调用方应把循环变量设为 <see cref="RouteAnchorPullExecution.RequiredLoopIndex"/>。</summary>
    Applied = 1,

    /// <summary>目标非法（越界/向后/等于当前）：已如实回报失败，调用方必须停止本轮。</summary>
    InvalidTarget = 2,

    /// <summary>当前路线无法中断（例如战斗中不可打断）：已如实回报失败，调用方必须停止本轮。</summary>
    AbortFailed = 3,
}

/// <summary>Pull 执行结果与调用方需要的循环变量值。</summary>
public readonly record struct RouteAnchorPullExecution(
    RouteAnchorPullExecutionResult Result,
    int RequiredLoopIndex,
    int TargetRouteIndex)
{
    public bool ShouldStopRound => Result is RouteAnchorPullExecutionResult.InvalidTarget
        or RouteAnchorPullExecutionResult.AbortFailed;
}

/// <summary>
/// Pull 执行编排器（route-anchor · 阶段 4）。
///
/// 目的：把"服务端要求跳路线"的**顺序与失败语义**从路线循环里抽出来，
/// 变成可注入（两个委托）且可单测的单元；路线循环侧只剩十几行薄适配。
///
/// 顺序（方案 §6.2，不可调换）：
///   1. 校验目标（越界/向后/等于当前 → 如实回报失败，禁止静默继续）
///   2. 先中断当前路线子任务并**等待其真正退出**（由调用方通过委托提供并保证）
///   3. 只有中断确认成功后才回报 Applied，并把目标循环变量交给调用方
///
/// 纪律：
///   · 任何失败都必须回报 `success=false`（服务端据此整队停止），不允许吞掉；
///   · 本类**不直接**操作游戏（不传送、不注入按键、不启动第二个执行器）——
///     中断与清理由调用方的委托实现，本类只负责顺序与确认；
///   · 回报失败后**不**抛出异常（调用方据结果停止本轮即可）。
/// </summary>
public sealed class RouteAnchorPullExecutor
{
    private readonly RouteAnchorClient _anchor;

    public RouteAnchorPullExecutor(RouteAnchorClient anchor)
    {
        _anchor = anchor;
    }

    /// <summary>
    /// 执行一次 Pull（若存在待执行命令）。
    /// </summary>
    /// <param name="currentRouteIndex">当前正在执行的路线索引（外层循环变量）。</param>
    /// <param name="startIndex">本轮起始索引（调试起始路线用）。</param>
    /// <param name="routeCount">本轮路线总数。</param>
    /// <param name="abortCurrentRouteAsync">
    /// 中断当前路线子任务并等待其真正退出（含输入清理）。
    /// 返回 false 表示**无法中断**（例如战斗中不可打断），此时本类会回报 PullFailed。
    /// </param>
    /// <param name="ct">会话取消令牌（与"路线子任务令牌"必须区分：只中断当前路线，不取消整个会话）。</param>
    public async Task<RouteAnchorPullExecution> ExecuteIfPendingAsync(
        int currentRouteIndex,
        int startIndex,
        int routeCount,
        Func<CancellationToken, Task<bool>> abortCurrentRouteAsync,
        CancellationToken ct)
    {
        var command = _anchor.TryGetPullCommand();
        if (command == null)
            return new RouteAnchorPullExecution(RouteAnchorPullExecutionResult.NoCommand, currentRouteIndex, -1);

        var target = command.TargetRouteIndex;

        // 1) 目标必须合法（严格向前且在计划范围内）
        if (!RouteAnchorJumpDecisions.CanJump(currentRouteIndex, startIndex, target, routeCount))
        {
            await SafeReportAsync(false, "target-invalid", ct);
            return new RouteAnchorPullExecution(RouteAnchorPullExecutionResult.InvalidTarget, currentRouteIndex, target);
        }

        // 2) 先中断当前路线并确认退出（不可中断时如实回报失败）
        if (RouteAnchorJumpDecisions.ShouldAbortCurrentRoute(currentRouteIndex, target))
        {
            bool aborted;
            try
            {
                aborted = await abortCurrentRouteAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // 会话被取消：不回报成功（服务端会按超时/停止处理），也不吞成"已跳转"
                throw;
            }
            catch (Exception)
            {
                aborted = false;
            }

            if (!aborted)
            {
                await SafeReportAsync(false, "abort-failed", ct);
                return new RouteAnchorPullExecution(RouteAnchorPullExecutionResult.AbortFailed, currentRouteIndex, target);
            }
        }

        // 3) 中断确认成功后才回报 Applied，并把目标循环变量交给调用方
        var reported = await SafeReportAsync(true, "", ct);
        if (!reported)
        {
            // 回报失败（网络问题）：不把结果当成功，交由服务端重播 + 超时兜底
            return new RouteAnchorPullExecution(RouteAnchorPullExecutionResult.AbortFailed, currentRouteIndex, target);
        }

        return new RouteAnchorPullExecution(
            RouteAnchorPullExecutionResult.Applied,
            RouteAnchorJumpDecisions.ResolveLoopIndexForJump(target),
            target);
    }

    /// <summary>回报失败/成功但绝不因回报本身抛出（回报失败不影响"本轮必须停止"的结论）。</summary>
    private async Task<bool> SafeReportAsync(bool success, string reason, CancellationToken ct)
    {
        try
        {
            return await _anchor.ReportPullAppliedAsync(success, reason, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
