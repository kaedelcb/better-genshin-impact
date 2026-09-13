using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing;

// =====================================================================
// 相机旋转域（A11/B12 新原则首例：包装层修复，PR 失败零残留，R4.6）：
// 决策（IsRotationArrived）+ 两级抢锁执行体（TryRotateToApproach / WaitUntilRotatedTo），同域单文件。
// 独立理由（R4.2）：消费方 PathExecutor.cs 是与公版同名的高冲突文件——旋转域逻辑收拢进
// 零冲突新文件后，PathExecutor 仅 2 处一行调用（含其私有包装内 1 处）。
//
// 包装层修复（R4.6，PR 失败零残留）：公版 CameraRotateTask.cs 逐字节保持公版原样
// （RotateToApproach 返回 float、无锁——"精确朝向(diff=0)"与"本轮未测量"无法用 float 区分是公版缺陷），
// 本服务的 TryRotateToApproach 在公版方法外面包裹两级抢锁与异常转 null，
// WaitUntilRotatedTo 提供 null 判定循环与超时 ESC×3 恢复主界面兜底。
// PathExecutor 改指本服务（2 处一行改动）。效果三全：PR = 纯新增文件零冲突零修改；
// PR 被拒 = 零残留（公版文件没动过，teabag 自行路由到包装层继续用）；
// 优选 = CameraRotateTask.cs 逐字节零冲突。
// =====================================================================

/// <summary>
/// 旋转到位判定纯函数（原独立文件 CameraRotateDecisions.cs 并入，类型名/命名空间不变）。
/// 详见 spec fight-return-to-point-seek-rotation-conflict-fix（已归档）改动 7 / Property 2 / 3。
/// </summary>
public static class CameraRotateDecisions
{
    /// <summary>
    /// 判定本轮是否"已转到目标角度"。
    /// measuredDiff == null 表示本轮未真实测量到角度（抢锁失败 / 异常）——绝不判到位（返回 false）。
    /// measuredDiff != null 时沿用原判据：|measuredDiff| &lt; maxDiff + count / 2。
    /// 纯函数：无外部依赖，PBT 友好。
    /// </summary>
    public static bool IsRotationArrived(float? measuredDiff, int maxDiff, int count)
    {
        if (measuredDiff is null)
        {
            return false;
        }
        return System.Math.Abs(measuredDiff.Value) < maxDiff + count / 2.0f;
    }
}

/// <summary>
/// 相机旋转执行体（自 CameraRotateTask 收拢；公版 CameraRotateTask.cs 逐字节保持公版原样，
/// 本服务以包装层形式提供茶包修复：两级抢锁 + float? null 语义 + 超时 ESC 兜底）。
/// </summary>
public static class CameraRotateService
{
    // 引用别名（E2 同款手法）：静态类不能作 GetLogger<T> 类型参数，复用 TaskControl.Logger
    private static ILogger Logger => TaskControl.Logger;

    // 两级抢锁（自 CameraRotateTask 收拢）：_zLock 守护判定循环、_rLock 守护单次旋转，锁序 _zLock → _rLock
    private static readonly object _rLock = new object();
    private static readonly object _zLock = new object();

    /// <summary>
    /// 包装层：抢 _rLock 后调用公版原方法 <see cref="CameraRotateTask.RotateToApproach"/>（float 返回、逐字节原样），
    /// 异常转 null。语义与收拢前内联实现（TryEnter + try/catch + null）逐项一致。
    /// </summary>
    public static float? TryRotateToApproach(CameraRotateTask rotateTask, float targetOrientation, ImageRegion imageRegion)
    {
        if (Monitor.TryEnter(_rLock))
        {
            try
            {
                return rotateTask.RotateToApproach(targetOrientation, imageRegion);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Logger.LogWarning("转动视角发生异常，停止转动-1111 {e}", e);
                // 异常 = 本轮未真实测量到角度，返回 null（而非 0）让调用方区分"未测量"与"误差=0"
                return null;
            }
            finally
            {
                Monitor.Exit(_rLock);
            }
        }
        // 抢 _rLock 失败 = 本轮未真实测量到角度，返回 null（而非 0），避免调用方误判为"已到位"
        return null;
    }

    /// <summary>
    /// 转动视角到目标角度（自 CameraRotateTask.WaitUntilRotatedTo 收拢）：
    /// null 判定循环 + IsRotationArrived + 超时 ESC×3 恢复主界面兜底。
    /// </summary>
    public static async Task<bool> WaitUntilRotatedTo(CameraRotateTask rotateTask, CancellationToken ct, int targetOrientation, int maxDiff, int maxTryTimes = 50)
    {
        bool isSuccessful = false;
        int count = 0;
        while (!ct.IsCancellationRequested)
        {
            using var screen = TaskControl.CaptureToRectArea();
            // null = 本轮未真实测量到角度（_zLock 抢锁失败 或 TryRotateToApproach 抢 _rLock 失败/异常返回 null）
            float? measuredDiff = null;
            if (Monitor.TryEnter(_zLock))
            {
                try
                {
                    var raw = TryRotateToApproach(rotateTask, targetOrientation, screen);
                    measuredDiff = raw.HasValue ? Math.Abs(raw.Value) : (float?)null;
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
                finally
                {
                    Monitor.Exit(_zLock);
                }
            }
            // 注意：_zLock TryEnter 失败时 measuredDiff 保持 null —— 未测量（null）绝不判到位。

            // 仅当本轮真实测量到角度时才判定是否到位；未测量（null）绝不判到位。
            if (CameraRotateDecisions.IsRotationArrived(measuredDiff, maxDiff, count))
            {
                isSuccessful = true;
                break;
            }

            if (count > maxTryTimes)
            {
                // 超时只停止转动，不再朝固定方向甩视角（对齐公版行为，避免误甩到错误方向）
                TaskControl.Logger.LogWarning("视角转动到目标角度超时，停止转动");

                // 兜底：超时后尝试恢复主界面（多发几次 ESC + 等待界面关闭）
                // 防止因弹窗/未关闭界面导致持续卡死
                for (int i = 0; i < 3; i++)
                {
                    await Delay(300, ct);
                    Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                    await Delay(500, ct);
                }

                break;
            }

            // 未到位且未超时（含本轮未测量）：跳过本轮，等下一轮再尝试（Q2=a：不累加成功判定、不提前 break）
            // TaskControl.Logger.LogWarning("转动视角到目标角度中，当前角度误差-{aa}，尝试次数-{count}", measuredDiff, count);
            await Delay(50 - count / 2, ct);
            count++;
        }

        return isSuccessful;
    }
}
