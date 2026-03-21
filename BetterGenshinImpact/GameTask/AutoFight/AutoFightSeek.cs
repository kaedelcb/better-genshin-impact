using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using OpenCvSharp;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using System;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using System.Linq;
using System.Collections.Generic;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Core.Script.Dependence;
// using Serilog.Core;
using SDPoint = System.Drawing.Point;
using System.Drawing;
using System.Windows.Forms;
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

namespace BetterGenshinImpact.GameTask.AutoFight
{
    public static  class MoveForwardTask
    {
        // 新增：通用异步按键保持方法，确保在取消/异常时释放按键
        private static async Task HoldKeysAsync((GIActions action, KeyType type)[] holdKeys, int delayMs, CancellationToken ct)
        {
            // 按下所有按键
            foreach (var k in holdKeys)
            {
                Simulation.SendInput.SimulateAction(k.action, k.type);
            }

            try
            {
                // 使用 await 等待，可响应 ct 取消
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消时吞掉异常，后续 finally 确保释放按键
            }
            finally
            {
                // 释放对应按键：用 KeyUp 或不带类型的 SimulateAction（保持兼容原调用）
                foreach (var k in holdKeys)
                {
                    // 如果原先按下是 KeyType.KeyDown，则释放用 KeyType.KeyUp
                    // 这里统一使用 KeyUp 释放
                    Simulation.SendInput.SimulateAction(k.action, KeyType.KeyUp);
                }
            }
        }

        public static Task<bool?> MoveForwardAsync(Scalar scalarLower, Scalar scalarHigher, ILogger logger, CancellationToken ct,int distance = 1000)
        {
            using var image2 = CaptureToRectArea();
            using Mat mask2 = OpenCvCommonHelper.Threshold(
                image2.DeriveCrop(0, 0, image2.Width * 1570 / 1920, image2.Height * 970 / 1080).SrcMat,
                scalarLower,
                scalarHigher
            );

            using Mat labels2 = new Mat();
            using Mat stats2 = new Mat();
            using Mat centroids2 = new Mat();

            int numLabels2 = Cv2.ConnectedComponentsWithStats(mask2, labels2, stats2, centroids2, connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);

            // logger.LogInformation("检测数量：{numLabels2}", numLabels2 - 1);

            if (numLabels2 > 1)
            {
                // 获取第一个连通对象的统计信息（标签1）
                using Mat firstRow = stats2.Row(1); // 获取第1行（标签1）的数据
                int[] stats;
                bool success = firstRow.GetArray(out stats); // 使用 out 参数来接收数组数据

                if (success)
                {
                    int x = stats[0];
                    int y = stats[1];
                    // int width = stats[2];
                    int height = stats[3];

                    SDPoint firstPixel = new SDPoint(x, y);
                    logger.LogInformation("敌人位置: ({firstPixel.X}, {firstPixel.Y})，血量高度: {height}", firstPixel.X, firstPixel.Y, height);
                    
                    if (firstPixel.X < 580 || firstPixel.X > 1315 || firstPixel.Y > 800)
                    {
                        // 非中心区域的处理逻辑
                        if (firstPixel.X < 500 && firstPixel.Y < 800)
                        {
                            // 左上区域
                            if (height <= 6)
                            {
                                logger.LogInformation("敌人在左上，向前加向左移动");
                                Task.Run(async () =>
                                {
                                    await HoldKeysAsync(
                                        new (GIActions, KeyType)[] { (GIActions.MoveForward, KeyType.KeyDown), (GIActions.MoveLeft, KeyType.KeyDown) },
                                        distance,
                                        ct
                                    ).ConfigureAwait(false);
                                }, ct);
                            }
                        }
                        else if (firstPixel.X > 1315 && firstPixel.Y < 800)
                        {
                            // 右上区域
                            if (height <= 6)
                            {
                                logger.LogInformation("敌人在右上，向前加向右移动");
                                Task.Run(async () =>
                                {
                                    await HoldKeysAsync(
                                        new (GIActions, KeyType)[] { (GIActions.MoveForward, KeyType.KeyDown), (GIActions.MoveRight, KeyType.KeyDown) },
                                        distance,
                                        ct
                                    ).ConfigureAwait(false);
                                }, ct);
                            }
                        }
                        else if (firstPixel.X < 500 && firstPixel.Y > 800)
                        {
                            // 左下区域
                            if (height <= 6)
                            {
                                logger.LogInformation("敌人在左下，向后加向左移动");
                                Task.Run(async () =>
                                {
                                    await HoldKeysAsync(
                                        new (GIActions, KeyType)[] { (GIActions.MoveBackward, KeyType.KeyDown), (GIActions.MoveLeft, KeyType.KeyDown) },
                                        distance,
                                        ct
                                    ).ConfigureAwait(false);
                                }, ct);
                            }
                        }
                        else if (firstPixel.X > 1315 && firstPixel.Y > 800)
                        {
                            // 右下区域
                            if (height <= 6)
                            {
                                logger.LogInformation("敌人在右下，向后加向右移动");
                                Task.Run(async () =>
                                {
                                    await HoldKeysAsync(
                                        new (GIActions, KeyType)[] { (GIActions.MoveBackward, KeyType.KeyDown), (GIActions.MoveRight, KeyType.KeyDown) },
                                        distance,
                                        ct
                                    ).ConfigureAwait(false);
                                }, ct);
                            }
                        }
                        else if (firstPixel.Y < 800)
                        {
                            // 上方区域
                            if (height <= 6)
                            {
                                logger.LogInformation("敌人在上方，向前移动");
                                Task.Run(async () =>
                                {
                                    await HoldKeysAsync(
                                        new (GIActions, KeyType)[] { (GIActions.MoveForward, KeyType.KeyDown), (GIActions.MoveForward, KeyType.KeyDown) },
                                        distance,
                                        ct
                                    ).ConfigureAwait(false);
                                }, ct);
                            }
                        }
                        else if (firstPixel.Y > 800)
                        {
                            // 下方区域
                            if (height <= 6)
                            {
                                logger.LogInformation("敌人在下方，向后移动");
                                Task.Run(async () =>
                                {
                                    await HoldKeysAsync(
                                        new (GIActions, KeyType)[] { (GIActions.MoveBackward, KeyType.KeyDown), (GIActions.MoveBackward, KeyType.KeyDown) },
                                        distance,
                                        ct
                                    ).ConfigureAwait(false);
                                }, ct);
                            }
                        }
                        else
                        {
                            // 非上述区域且非中心区域，判断左右
                            if (firstPixel.X < 920 && height > 6)
                            {
                                Simulation.SendInput.SimulateAction(GIActions.MoveBackward);
                                logger.LogInformation("敌人在左侧，不移动");
                            }
                            else if (firstPixel.X > 920 && height > 6)
                            {
                                Simulation.SendInput.SimulateAction(GIActions.MoveBackward);
                                logger.LogInformation("敌人在右侧，不移动");
                            }
                        }
                    }
                    else // 中心区域
                    {
                        if (height > 6)
                        {
                            Simulation.SendInput.SimulateAction(GIActions.MoveBackward);
                            logger.LogInformation("敌人在中心且高度大于6，不移动");
                        }
                        else if (firstPixel.X < 1315 && firstPixel.X > 500 && firstPixel.Y < 800 && height > 2)
                        {
                            logger.LogInformation("敌人在上方，向前移动");
                            Task.Run(async () =>
                            {
                                await HoldKeysAsync(
                                    new (GIActions, KeyType)[] { (GIActions.MoveForward, KeyType.KeyDown), (GIActions.MoveForward, KeyType.KeyDown) },
                                    distance,
                                    ct
                                ).ConfigureAwait(false);
                            }, ct);
                        }
                        else if (firstPixel.X < 1315 && firstPixel.X > 500 && firstPixel.Y > 800 && height > 2)
                        {
                            logger.LogInformation("敌人在下方，向后移动");
                            Task.Run(async () =>
                            {
                                await HoldKeysAsync(
                                    new (GIActions, KeyType)[] { (GIActions.MoveBackward, KeyType.KeyDown), (GIActions.MoveBackward, KeyType.KeyDown) },
                                    distance,
                                    ct
                                ).ConfigureAwait(false);
                            }, ct);
                        }
                        else if (height < 3)
                        {
                            Simulation.SendInput.SimulateAction(GIActions.MoveBackward);
                            logger.LogInformation("敌人血量高度小于3，不移动");
                        }
                        else
                        {
                            Simulation.SendInput.SimulateAction(GIActions.MoveBackward);
                            logger.LogInformation("不移动");
                        }
                    }
                }
                else
                {
                    logger.LogError("无法获取统计信息数组");
                }
            }
            
            return Task.FromResult<bool?>(null);
        }
    }
    

    public class AutoFightSeek
    {
        public static int RotationCount = 0;
        
        private static readonly IntPtr GameHandle=TaskContext.Instance().GameHandle;
        private static readonly Rectangle GameScreenBounds = Screen.FromHandle(GameHandle).Bounds;
        private  static readonly int RetryCountReset = GameScreenBounds.Width > 1920 ? 0 : -6;
        
        
        private static readonly Dictionary<int, int> RotaryFactorMapping = new Dictionary<int, int> //旋转因子映射表
        {
            { 1, 40 }, { 2, 35 }, { 3, 30}, { 4, 25 }, { 5, 20}, { 6,15 },
            { 7, 10 }, { 8, 5 }, { 9, 1 }, { 10, -5 }, { 11,-10 }, { 12,-50 }, { 13, -60 }
        };
        
        public static async Task<bool?> SeekAndFightAsync(ILogger logger, int detectDelayTime,int delayTime,CancellationToken ct,bool isEndCheck = false,int rotaryFactor = 6,Avatar? avatar = null,int distance = 1000)
        {
            if (rotaryFactor == 1) return null;;
            
            var bloodLower = new Scalar(255, 90, 90);

            var adjustedX = RotaryFactorMapping[rotaryFactor];
            var adjustedDivisor = rotaryFactor<=12 ? 2 : 1.3;
            var delay = 50 + (int)(adjustedX / adjustedDivisor);

            var rotationCount6 = RotationCount % 7;
            
            // Logger.LogInformation("开始寻找敌人 {Text} ...",adjustedX);
            
            int retryCount = isEndCheck? 1 : 0;

            while (retryCount < 25+(int)(adjustedX / 5)+RetryCountReset)
            {
                using var image = CaptureToRectArea();

                if (retryCount == 1)
                {
                    using var confirmRectArea = image.Find(AutoFightAssets.Instance.ConfirmRa);
                    if (confirmRectArea.IsExist() || ct.IsCancellationRequested)
                    {
                        Logger.LogWarning("旋转寻敌：{t} 停止旋转", "页面错误");
                        return null;
                    } 
                }

                using Mat mask = OpenCvCommonHelper.Threshold(image.DeriveCrop(0, 0, 1500, 900).SrcMat, bloodLower);
                
                using Mat labels = new Mat();
                using Mat stats = new Mat();
                using Mat centroids = new Mat();
                
                int numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
                    connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);
                // if (retryCount == 0) logger.LogInformation("敌人初检数量： {numLabels}", numLabels - 1);

                if (numLabels > 1)
                {
                    // logger.LogInformation("检测画面内疑似有敌人，继续战斗...");

                    using Mat firstRow = stats.Row(1);
                    int[] statsArray;
                    bool success = firstRow.GetArray(out statsArray); 
                    int height = statsArray[3];
                    int x = statsArray[0];
                    // Logger.LogInformation("敌人位置: ({x}，血量高度: {height}", x, height);
                    
                    if (success)
                    {
                        if (isEndCheck) 
                        {
                            await Task.Run(() =>
                            {
                                Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                                Task.Delay(100, ct).Wait();;
                                Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                            }, ct);
                        }
                        else
                        {
                            Task.Run(() =>
                            {
                                Simulation.SendInput.SimulateAction(GIActions.MoveForward);
                                Simulation.SendInput.SimulateAction(GIActions.MoveForward);
                            }, ct);
                        }
                        
                        if (height > 2 && height < 7)
                        {
                            // logger.LogInformation("画面内有找到敌人，尝试移动...");
                            Task.Run(() => { MoveForwardTask.MoveForwardAsync(bloodLower, bloodLower, logger, ct,distance); }, ct);
                            return false;
                        }

                        if (height > 6 && height < 25)
                        {
                            // Logger.LogInformation("画面内有找到敌人，{t1} - {t2}",x,height);
                            if ((x == 758 || x == 721) && (height ==7 || height == 8))//固定血条的怪物，尝试旋转寻找
                            {
                                Task.Run(() =>
                                {
                                    // Simulation.SendInput.Mouse.MoveMouseBy(960, 0);
                                    // Task.Delay(100, ct).Wait();
                                    Simulation.SendInput.SimulateAction(GIActions.MoveRight);
                                    Task.Delay(100, ct).Wait();
                                    Simulation.SendInput.Mouse.MiddleButtonClick();
                                    Task.Delay(100, ct).Wait();
                                }, ct);
                            }
                            // logger.LogInformation("画面内有找到敌人，继续战斗...");
                            return false;
                        }

                        if (height < 3 || height > 25)
                        {
                            return  null;
                        }
                    }
                }
                
                if (retryCount == 0 && !Dispatcher.IsCustomCts)
                {
                    await Delay(delayTime,ct);
                    // Logger.LogInformation("打开编队界面检查战斗是否结束，延时{detectDelayTime}毫秒检查", detectDelayTime);
                    Logger.LogInformation("打开编队界面检查战斗是否结束");
                    Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
                    await Delay(detectDelayTime, ct);
                    var ra3 = CaptureToRectArea();
                    var b33 = ra3.SrcMat.At<Vec3b>(50, 790); // 进度条颜色
                    var whiteTile3 = ra3.SrcMat.At<Vec3b>(50, 768); // 白块
                    Simulation.SendInput.SimulateAction(GIActions.Drop);
                    ra3.Dispose();
                
                    if (IsWhite(whiteTile3.Item2, whiteTile3.Item1, whiteTile3.Item0) &&
                        IsYellow(b33.Item2, b33.Item1, b33.Item0))
                    {
                        logger.LogInformation("识别到战斗结束-s");
                        Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
                        return true;
                    }
                }

                if ((rotationCount6 == 3 || rotationCount6 == 1)&& retryCount == 0)
                {
                    Simulation.SendInput.Mouse.MiddleButtonClick();
                    await Task.Delay(rotationCount6 == 3 ?500:200, ct);
                }
                
                if (retryCount <= 2)
                {
                    (int x, int y)[] offsets;
                    if (GameScreenBounds.Width > 1920)
                    {
                         offsets = new (int x, int y)[] {
                            (image.Width / 6, image.Height / 7), 
                            (image.Width / 6, 0),                 
                            (image.Width / 6, -image.Height / 5),
                            (image.Width / 6, -image.Height),  
                        };
                    }
                    else
                    {
                         offsets = new (int x, int y)[] {
                            (image.Width / 12, image.Height / 7), 
                            (image.Width / 12, 0),                 
                            (image.Width / 12, -image.Height / 5),
                            (image.Width / 12, -image.Height),  
                        };
                    }

                    var offsetIndex = rotationCount6 < 2 ? 0 : (rotationCount6 == 2) ? 1 : (rotationCount6 >= 3) ? 2 : 3;
                    Simulation.SendInput.Mouse.MoveMouseBy(offsets[offsetIndex].x, offsets[offsetIndex].y);
                }
                else
                {
                    Simulation.SendInput.Mouse.MoveMouseBy(image.Width / 6, 0);
                }
                
                await Task.Delay(Math.Max(delay, 1), ct);
                // await Task.Delay(50,ct);

                using var image2 = CaptureToRectArea();
                using Mat mask2 = OpenCvCommonHelper.Threshold(image2.DeriveCrop(0, 0, 1500, 900).SrcMat, bloodLower);
                using Mat labels2 = new Mat();
                using Mat stats2 = new Mat();
                using Mat centroids2 = new Mat();

                 numLabels = Cv2.ConnectedComponentsWithStats(mask2, labels2, stats2, centroids2,
                    connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);

                if (numLabels > 1)
                {
                    // logger.LogInformation("检测敌人第 {retryCount} 次： {numLabels}", retryCount + 1, numLabels - 1);
                    using Mat firstRow2 = stats2.Row(1); // 获取第1行（标签1）的数据
                    int[] statsArray2;
                    bool success2 = firstRow2.GetArray(out statsArray2); // 使用 out 参数来接收数组数据
                    int height2 = statsArray2[3];
                    // logger.LogInformation("敌人血量 ：{height2}", height2);

                    if (success2)
                    {
                        if (isEndCheck) await Task.Run(() =>
                        {
                            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                            Task.Delay(100, ct).Wait();
                            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                        }, ct);
                        
                        if (height2 > 2 && height2 < 7)
                        {
                            // logger.LogInformation("画面内有找到敌人，尝试移动...");
                            Task.Run(() => { MoveForwardTask.MoveForwardAsync(bloodLower, bloodLower, logger, ct,distance); }, ct);
                            return false;
                        }

                        if (height2 > 6 && height2 < 25)
                        {
                            // logger.LogInformation("画面内有找到敌人，继续战斗...");
                            return false;
                        }

                        if (height2 < 3 || height2 > 25)
                        {
                            return null;
                        }
                    }
                }
                
                retryCount++;
            }
            
            logger.LogInformation("寻找敌人：{Text}", "无");

            if (avatar?.Name == "玛薇卡" &&  RotationCount >= 1)
            {
                using var region2 = CaptureToRectArea();
                // 获取两个点的颜色值
                var pos = region2.SrcMat.At<Vec3b>(978, 1692);
                var pos2 = region2.SrcMat.At<Vec3b>(995, 1702);
                double colorDifference = Math.Sqrt(
                    Math.Pow(pos.Item0 - pos2.Item0, 2) + // 蓝通道差值的平方
                    Math.Pow(pos.Item1 - pos2.Item1, 2) + // 绿通道差值的平方
                    Math.Pow(pos.Item2 - pos2.Item2, 2)   // 红通道差值的平方
                );

                if (colorDifference < 15)
                {
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill); 
                }  
            }

            return null;
        }
        
        private static bool IsYellow(int r, int g, int b)
        {
            //Logger.LogInformation($"IsYellow({r},{g},{b})");
            // 黄色范围：R高，G高，B低
            return (r >= 200 && r <= 255) &&
                   (g >= 200 && g <= 255) &&
                   (b >= 0 && b <= 100);
        }

        private static bool IsWhite(int r, int g, int b)
        {
            //Logger.LogInformation($"IsWhite({r},{g},{b})");
            // 白色范围：R高，G高，B低
            return (r >= 240 && r <= 255) &&
                   (g >= 240 && g <= 255) &&
                   (b >= 240 && b <= 255);
        }
    }

    public class AutoFightSkill
    {
        public static async Task EnsureGuardianSkill(Avatar guardianAvatar, CombatCommand command, string lastFightName,
            string guardianAvatarName, bool guardianAvatarHold, int retryCount, CancellationToken ct,bool guardianCombatSkip = false,
            bool burstEnabled = false)
        {
            int attempt = 0;

            if (guardianAvatar.IsSkillReady())
            {
                while (attempt < retryCount)
                {
                    if (guardianAvatar.TrySwitch(14, false))
                    {
                        guardianAvatar.ManualSkillCd = -1;
                        if (await AvatarSkillAsync(Logger, guardianAvatar, false, 1, ct))
                        {
                            var cd1 = guardianAvatar.AfterUseSkill();
                            if (cd1 > 0)
                            {
                                Logger.LogInformation("优先第 {text} 盾奶位 {GuardianAvatar} 战技Cd检测：{cd} 秒", guardianAvatarName,
                                    guardianAvatar.Name, cd1);
                                guardianAvatar.ManualSkillCd = -1;
                                return;
                            }
                        }
            
                        guardianAvatar.UseSkill(guardianAvatarHold);
                        var imageAfterUseSkill = CaptureToRectArea();
                        var retry = 100;

                        try
                        {
                            while (!(await AvatarSkillAsync(Logger, guardianAvatar, false, 1, ct,
                                       imageAfterUseSkill)) && retry > 0)
                            {
                                Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                                Simulation.ReleaseAllKey();

                                // 防止在纳塔飞天或爬墙
                                if (retry % 3 == 0)
                                {
                                    Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                                    Simulation.SendInput.SimulateAction(GIActions.Drop);
                                }

                                // 释放旧的截图资源
                                imageAfterUseSkill.Dispose();

                                // 获取新的截图
                                imageAfterUseSkill = CaptureToRectArea();

                                await Task.Delay(30, ct);
                                retry -= 1;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "优先第 {text} 盾奶位 {GuardianAvatar} 战技释放异常", guardianAvatarName, guardianAvatar.Name);
                        }
                        finally
                        {
                            // 确保最终释放资源
                            imageAfterUseSkill.Dispose();
                        }

                        
                        if (retry > 0)
                        {
                            Logger.LogInformation("优先第 {text} 盾奶位 {GuardianAvatar} 释放战技：{t}",
                                guardianAvatarName, guardianAvatar.Name,"成功");
                            guardianAvatar.LastSkillTime = DateTime.UtcNow;
                            guardianAvatar.ManualSkillCd = -1;
                            return;
                        }
                        
                        Logger.LogInformation("优先第 {text} 盾奶位 {GuardianAvatar} 释放战技：失败重试 {attempt} 次",
                            guardianAvatarName, guardianAvatar.Name, attempt + 1);
                        guardianAvatar.ManualSkillCd = 0;
                        guardianAvatar.UseSkill(guardianAvatarHold);
                        //防止在纳塔飞天或
                        Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                        Simulation.SendInput.SimulateAction(GIActions.Drop);
                    }
                    
                    attempt++;
                }
            }
            else if (burstEnabled)
            {
                using var image = CaptureToRectArea();
                if (!guardianAvatar.IsActive(image))
                {
                    var skillArea = AutoFightAssets.Instance.AvatarQRectListMap[guardianAvatar.Index - 1];//Q技能区域
                    // 首先对图像进行预处理，转为灰度图
                    using var grayImage = image.DeriveCrop(skillArea).SrcMat.CvtColor(ColorConversionCodes.BGR2GRAY);
                
                    //调试用
                    // grayImage.SaveImage("D:\\Images\\grayImage.png");
                    // Cv2.ImShow("灰度图像", grayImage);
                    
                    // 计算图像的平均亮度
                    var meanBrightness = Cv2.Mean(grayImage);
                    var avgBrightness = meanBrightness.Val0;
                    // 根据平均亮度动态调整Canny边缘检测的阈值
                    var threshold1 = avgBrightness * 0.9;
                    var threshold2 = avgBrightness * 2;
                
                    // Logger.LogInformation("角色{i} 平均亮度 {avgBrightness}", i, avgBrightness);
                
                    Cv2.Canny(grayImage, grayImage, threshold1: (float)threshold1, threshold2: (float)threshold2); // 边缘检测
                    
                    // 使用霍夫变换检测圆形
                    var circles = Cv2.HoughCircles(grayImage, HoughModes.Gradient, dp: 1.2, minDist: 20,
                        param1: 70, param2: 20, minRadius: 25, maxRadius: 34);

                    // if (circles != null && circles.Length > 0)
                    // {
                    //     // 假设我们只取第一个检测到的圆
                    //     var firstCircle = circles[0];
                    //     var centerX = firstCircle.Center.X;
                    //     var centerY = firstCircle.Center.Y;
                    //     var radius = firstCircle.Radius;
                    //
                    //     Logger.LogInformation("圆圈数量 {circles} 圆圈半径 {radius} ", circles.Length, radius);
                    // }

                    if (circles.Length > 0)
                    {
                        Logger.LogInformation("优先第 {text} 盾奶位 {GuardianAvatar} 元素爆发状态：{attempt}，尝试释放",
                            guardianAvatarName, guardianAvatar.Name, "就绪");
                        
                        if (guardianAvatar.TrySwitch(14, false))
                        {
                            Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
                            Sleep(500, ct);
                            Simulation.ReleaseAllKey();
                        
                            //普攻一下，防止在纳塔飞天
                            Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                            using var imageAfterBurst = CaptureToRectArea();
                            if (AvatarSkillAsync(Logger, guardianAvatar, true, 1, ct).Result 
                                 || !Bv.IsInMainUi(imageAfterBurst)) //Q技能CD（冷却检测）或者不在主界面（大招动画播放中）
                            {
                                guardianAvatar.IsBurstReady = false;
                            }
                            else
                            {
                                Sleep(500, ct);
                                Simulation.SendInput.SimulateAction(GIActions.NormalAttack);//普攻一下，防止在纳塔飞天
                                Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);//尝试再放一次,不检查
                                guardianAvatar.IsBurstReady = true;
                            }
                            Logger.LogInformation("优先第 {guardianAvatarName} 盾奶位 {GuardianAvatar} 释放元素爆发：{text}",
                                guardianAvatarName, guardianAvatar.Name, !guardianAvatar.IsBurstReady ? "成功" : "失败");
                        }
                    }
                }
            }
        }
        
        //新方法，备用，非OCR识别，判断色块进行，速度更快
        //检测技能图标中释放含有白色色块，检测前进行角色切换的确认，skills：false为E技能，true为Q技能
        /// <summary>
        /// 检测角色技能冷却状态
        /// </summary>
        /// <param name="logger">日志记录器</param>
        /// <param name="guardianAvatar">角色对象</param>
        /// <param name="skills">技能类型，false为E技能，true为Q技能</param>
        /// <param name="retryCount">重试次数</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="image">图像对象</param>
        /// <param name="needLog">是否需要日志输出</param>
        /// <param name="isResetCd">是否重置技能冷却状态</param>
        /// <returns>技能是否就绪</returns>
        // csharp
        public static async Task<bool> AvatarSkillAsync(ILogger logger, Avatar guardianAvatar, bool skills, int retryCount,
            CancellationToken ct, ImageRegion? image = null, bool needLog = false, bool isResetCd = false)
        {
            if (!guardianAvatar.TrySwitch())
            {
                if (!isResetCd)
                    return false;
        
                if (skills) guardianAvatar.IsBurstReady = false;
                else guardianAvatar.AfterUseSkill();
        
                return false;
            }
        
            Scalar bloodLower = new Scalar(255, 255, 255);
            int attempt = 0;
            var model = image is null;
        
            while (attempt < retryCount)
            {
                if (ct.IsCancellationRequested) return false;
        
                ImageRegion? image2 = null;
                try
                {
                    image2 = model ? CaptureToRectArea() : image ?? CaptureToRectArea();
        
                    var skillAra = !skills
                        ? new Rect(image2.Width * 1688 / 1920, image2.Height * 988 / 1080,
                            image2.Width * 22 / 1920, image2.Height * 12 / 1080) //E技能区域
                        : new Rect(image2.Width * 1809 / 1920, image2.Height * 968 / 1080,
                            image2.Width * 30 / 1920, image2.Height * 15 / 1080); //Q技能区域
        
                    using var mask2 = OpenCvCommonHelper.Threshold(
                        image2.DeriveCrop(skillAra).SrcMat,
                        bloodLower,
                        bloodLower
                    );
        
                    using var labels2 = new Mat();
                    using var stats2 = new Mat();
                    using var centroids2 = new Mat();
        
                    int numLabels2 = Cv2.ConnectedComponentsWithStats(mask2, labels2, stats2, centroids2,
                        connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);
        
                    if (needLog) logger.LogInformation("技能状态：{Name} - {Skill} 状态 {Text}",
                        guardianAvatar.Name, skills ? "Q技能" : "E技能", numLabels2 > 1 ? "冷却中" : "就绪");
        
                    if (numLabels2 > 2)
                    {
                        if (!isResetCd) return true;
        
                        if (skills) guardianAvatar.IsBurstReady = true;
                        else guardianAvatar.ManualSkillCd = 0;
        
                        return true;
                    }
        
                    attempt++;
        
                    if (retryCount > 1)
                    {
                        try
                        {
                            await Task.Delay(100, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return false;
                        }
                    }
                }
                finally
                {
                    if (model && image2 != null)
                    {
                        image2.Dispose();
                    }
                }
            }
        
            if (!isResetCd) return false;
        
            if (skills) guardianAvatar.IsBurstReady = false;
            else guardianAvatar.AfterUseSkill();
        
            return false;
        }
        
        public static Task<bool> MedicinalCdAsync(ILogger logger, bool skills, int retryCount,
            CancellationToken ct, ImageRegion? image = null)
        {
            Scalar bloodLower = new Scalar(255, 255, 255);
            int attempt = 0;
            var model = image is null;
        
            while (attempt < retryCount)
            {
                if (ct.IsCancellationRequested) return Task.FromResult(false);
        
                ImageRegion? image2 = null;
                try
                {
                    image2 = model ? CaptureToRectArea() : image ?? CaptureToRectArea(); //1800,817/1836,834
        
                    var skillAra = !skills
                        ? new Rect(image2.Width * 1800 / 1920, image2.Height * 817 / 1080,
                            image2.Width * 36 / 1920, image2.Height * 17 / 1080) //药物区域
                        : new Rect(image2.Width * 928 / 1920, image2.Height * 1006 / 1080,
                            image2.Width * 58 / 1920, image2.Height * 8 / 1080); //血条数字 986 1014
        
                    using var mask2 = OpenCvCommonHelper.Threshold(
                        image2.DeriveCrop(skillAra).SrcMat,
                        bloodLower,
                        bloodLower
                    );
        
                    using var labels2 = new Mat();
                    using var stats2 = new Mat();
                    using var centroids2 = new Mat();
        
                    int numLabels2 = Cv2.ConnectedComponentsWithStats(mask2, labels2, stats2, centroids2,
                        connectivity: skills? PixelConnectivity.Connectivity4:PixelConnectivity.Connectivity8, ltype: MatType.CV_32S);
        
                    logger.LogInformation("药物状态：{Skill} 状态 {Text} ：{n}",
                         skills ? "结束状态" : "复活药", numLabels2 > 2 ? "冷却中" : "就绪", numLabels2);
        
                    if (numLabels2 > 2)
                    {
                        //识别到文字
                        if (skills)
                        {
                            // Logger.LogWarning("测试1：{t},{t2}",numLabels2,skills);
                            return Task.FromResult(true);
                        }
                        else
                        {
                            // Logger.LogWarning("测试2：{t},{t2}",numLabels2,skills);
                            var replenishStringArea = image2.FindMulti(RecognitionObject.Ocr(skillAra));
                            //如果replenishStringArea为空
                            if (replenishStringArea.Count != 0) return Task.FromResult(true);
                        }
                    }
        
                    attempt++;
                    
                }
                finally
                {
                    if (model && image2 != null)
                    {
                        image2.Dispose();
                    }
                }
            }
            
            return Task.FromResult(false);
        }
        
        public static Task<List<int>> AvatarQSkillAsync(ImageRegion image, List<int>? useEqList = null,int? avatarCurrent = null)
        {
            image.SrcMat.ConvertTo(image.SrcMat, MatType.CV_8UC3, alpha: 2, beta: -200); // 增加亮度和对比度
            var useMedicine = new List<int>();
            var eqList = useEqList ?? new List<int> { 1, 2, 3, 4 };
        
            foreach (var i in eqList)
            {
                var skillArea = i != avatarCurrent ? AutoFightAssets.Instance.AvatarQRectListMap[i - 1]: new Rect(1762, 915, 114, 111);

                using var grayImage = image.DeriveCrop(skillArea).SrcMat.CvtColor(ColorConversionCodes.BGR2GRAY);
        
                var meanBrightness = Cv2.Mean(grayImage);
                var avgBrightness = meanBrightness.Val0;
                var threshold1 = avgBrightness * 0.9;
                var threshold2 = avgBrightness * 2;
        
                Cv2.Canny(grayImage, grayImage, threshold1: (float)threshold1, threshold2: (float)threshold2);
        
                var circles = Cv2.HoughCircles(grayImage, HoughModes.Gradient, dp: 1.2, minDist: 20,
                    param1: 90, param2: i != avatarCurrent ? 20 : 50, minRadius: i != avatarCurrent ? 25 : 50, maxRadius:i != avatarCurrent ? 34 : 60);
        
                if (circles.Length > 0)
                {
                    useMedicine.Add(i);
                }
            }
            
            if (useMedicine.Count > 0)
            {
                Logger.LogInformation("元素爆发 {text} 的角色序号：{useMedicine}", "就绪", useMedicine);
                return Task.FromResult(useMedicine);
            }
        
            GC.Collect();//释放内存
            GC.WaitForPendingFinalizers();//释放内存
            return Task.FromResult(new List<int>());
        }
    }

}
