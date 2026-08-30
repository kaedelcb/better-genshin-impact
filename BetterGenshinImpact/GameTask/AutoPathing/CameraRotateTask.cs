using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.Common.Map;
using BetterGenshinImpact.GameTask.Model.Area;
using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using Logger = Serilog.Core.Logger;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.AutoPathing;

public class CameraRotateTask(CancellationToken ct)
{
    private readonly double _dpi = TaskContext.Instance().DpiScale;

    private static volatile object _rLock = new object(); 
    
    /// <summary>
    /// 向目标角度旋转
    /// </summary>
    /// <param name="targetOrientation"></param>
    /// <param name="imageRegion"></param>
    /// <returns></returns>
    public float? RotateToApproach(float targetOrientation, ImageRegion imageRegion)
    {
        if (Monitor.TryEnter(_rLock))
        {
            try
            {
                var cao = CameraOrientation.Compute(imageRegion.SrcMat);
                var diff = (cao - targetOrientation + 180) % 360 - 180;
                diff += diff < -180 ? 360 : 0;
                if (diff == 0)
                {
                    return diff;
                }

                // 平滑的旋转视角
                // todo dpi 和分辨率都会影响转动速度

                double controlRatio = 1;
                if (Math.Abs(diff) > 90)
                {
                    controlRatio = 4;
                }
                else if (Math.Abs(diff) > 30)
                {
                    controlRatio = 3;
                }
                else if (Math.Abs(diff) > 5)
                {
                    controlRatio = 2;
                }

                var moveX = (int)Math.Round(-controlRatio * diff * _dpi);
                Simulation.SendInput.Mouse.MoveMouseBy(moveX, 0);
                return diff; 
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                TaskControl.Logger.LogWarning("转动视角发生异常，停止转动-1111 {e}", e);
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

    private static volatile object _zLock = new object(); 
    /// <summary>
    /// 转动视角到目标角度
    /// </summary>
    /// <param name="targetOrientation">目标角度</param>
    /// <param name="maxDiff">最大误差</param>
    /// <param name="maxTryTimes">最大尝试次数（超时时间）</param>
    /// <returns></returns>
    public async Task<bool> WaitUntilRotatedTo(int targetOrientation, int maxDiff, int maxTryTimes = 50)
    {
        bool isSuccessful = false;
        int count = 0;
        while (!ct.IsCancellationRequested)
        {
            using var screen = CaptureToRectArea();
            // null = 本轮未真实测量到角度（_zLock 抢锁失败 或 RotateToApproach 抢 _rLock 失败/异常返回 null）
            float? measuredDiff = null;
            if (Monitor.TryEnter(_zLock))
            {
                try
                {
                    var raw = RotateToApproach(targetOrientation, screen);
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
