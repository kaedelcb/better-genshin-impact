using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoWood.Assets;
using BetterGenshinImpact.GameTask.AutoWood.Utils;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.UseRedeemCode;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Service.Notification;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using Rect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.Service.OneDragon;

/// <summary>
/// 一条龙账号原子能力（R3.1 从 OneDragonFlowViewModel 调度区迁出，参数化改造）。
/// 茶版 UID 校验/切账号/兑换码链路随原生恢复从执行链移除；此处保留动作能力本体，
/// 作为 R4 槲寄生账号策略的执行端原料。所有账号上下文走显式参数，不读 VM/配置单。
/// 公版原生执行链不调用本类。
/// </summary>
public class OneDragonAccountCapability
{
    private static readonly ILogger _logger = App.GetLogger<OneDragonAccountCapability>();
    private readonly BlessingOfTheWelkinMoonTask _blessingOfTheWelkinMoonTask = new();
    private readonly AutoRedeemCodeChecker _autoRedeemCodeChecker = new();

// ---- 粘贴板读取（原茶版 VM 2809）----
    private string GetClipboardText()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                return Clipboard.GetText();
            }
            else
            {
                _logger.LogWarning("读取不到游戏UID");
                return string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取游戏UID时发生错误。");
            return string.Empty;
        }
    }

// ---- 兑换码/UID 验证（原茶版 VM 2938/2953，参数化）----
    /// 检查并自动兑换兑换码（如果启用）
    /// </summary>
    public async Task CheckAndRedeemCodeAsync(string? uid, CancellationToken cts = default)
    {
        try
        {
            // 使用配置的UID，如果没有配置则使用默认值 "default"
            uid = string.IsNullOrEmpty(uid) ? "default" : uid;
            await _autoRedeemCodeChecker.CheckAndRedeemIfNeeded(uid, cts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "自动兑换码检查失败，不阻塞一条龙执行");
        }
    }

    //UID验证
    public async Task<bool> VerifyUidAsync(string configName, string? genshinUid, bool accountBinding, CancellationToken cts)  
    {
        if (string.IsNullOrEmpty(configName))
        {
            return false;
        }
        
        if (accountBinding)
        {
            await new ReturnMainUiTask().Start(cts);
            Clipboard.Clear();
            Simulation.SendInput.Keyboard.KeyPress(VK.VK_ESCAPE);
            
            for (int i = 0; i < 10; i++)
            {
                using var closeRa = CaptureToRectArea().Find(RecognitionAssets.Get("AutoSkip", "PageCloseMain"));
                if (!closeRa.IsEmpty())
                {
                    closeRa.ClickTo(closeRa.X + closeRa.Width * 3, closeRa.X + closeRa.Height * 4);
                    await new ReturnMainUiTask().Start(cts);
                    break;
                }
                await Task.Delay(500);
            }
            
            string clipboardContent = GetClipboardText();
            if (string.IsNullOrEmpty(clipboardContent))
            {
                _logger.LogError("UID读取失败，退出执行");
                return false;
            }else
            {
                if (clipboardContent.Contains(genshinUid))
                {
                    _logger.LogInformation("UID验证: {text} 绑定 {text}，完成",configName,genshinUid);
                    return true;
                }
                else
                {
                    _logger.LogWarning(clipboardContent.Length == 9 && clipboardContent.All(char.IsNumber) ? 
                        $"UID验证: 失败 {configName} ,绑定 {genshinUid}，验证 {clipboardContent}" : "UID验证:失败");
                    return false;
                }
            }
        }
        else
        {
            _logger.LogInformation("未绑定UID，不执行UID验证");
            return true;
        }
    }
  
    private static RecognitionObject GetConfirmRa(params string[] targetText)
    {
        var screenArea = CaptureToRectArea();
        return RecognitionObject.OcrMatch(
            (int)(screenArea.Width * 0.2),
            (int)(screenArea.Height * 0.5),
            (int)(screenArea.Width * 0.5),
            (int)(screenArea.Height * 0.5),
            targetText
        );
    }
// ---- 切换账号（原茶版 VM 3020，参数化）----
    //切换账号
    private readonly AutoWoodAssets _assets = AutoWoodAssets.Instance;
    private readonly Login3rdParty _login3RdParty = new();
    private int _exitPhoneCount = 3; //,账号数为图标数量-1，默认记录2个账号
    public async Task<bool> SwitchAccountAsync(string? genshinUid, string? bindingCode, CancellationToken cts, int switchTime = 1) //基于重新登录函数ExitAndReloginJob改造
    {
        //回到主页
        await new ReturnMainUiTask().Start(cts);
        
        //月卡检测
        await _blessingOfTheWelkinMoonTask.Start(cts);
        
        //============== 退出游戏流程 ==============
        Logger.LogInformation("退出至登录页面");
        SystemControl.FocusWindow(TaskContext.Instance().GameHandle);
        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
        await Delay(800, cts);
        
        // 菜单界面验证（带重试机制）
        try
        {
            NewRetry.Do(() => 
            {
                using var contentRegion = CaptureToRectArea();
                using var ra = contentRegion.Find(_assets.MenuBagRo);
                if (ra.IsEmpty())
                {
                    // 未检测到菜单时再次发送ESC
                    Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                    throw new RetryException("菜单界面验证失败");
                }
            }, TimeSpan.FromSeconds(1.2), 5);  // 1.2秒内重试5次
        }
        catch
        {
            // 即使失败也继续退出流程
        }

        // 点击退出按钮
        GameCaptureRegion.GameRegionClick((size, scale) => (50 * scale, size.Height - 50 * scale));
        await Delay(500, cts);

        // 确认退出
        using var cr = CaptureToRectArea();
        cr.Find(_assets.ConfirmRo, ra =>
        {
            ra.Click();
            ra.Dispose();
        });
            
        await Delay(1000, cts);  // 等待退出完成
        
        //============== 重新登录流程 ==============
        // 0第三方登录（如果启用）
     
        _login3RdParty.RefreshAvailabled();
        if (_login3RdParty is { Type: Login3rdParty.The3rdPartyType.Bilibili, IsAvailabled: true })
        {
            await Delay(1, cts);
            _login3RdParty.Login(cts);
            Logger.LogInformation("退出重登启用 B 服模式");
        }
        
        // 1点击账号切换按钮
        Logger.LogInformation("点击 {text} 按钮", "账号切换");
        var exitSwitchClickCnt = 0;
        for (var i = 0; i < 20; i++)
        {
            await Delay(1, cts);
            using var contentRegion = CaptureToRectArea();
            using var ra = contentRegion.Find(_assets.ExitSwitchRo);
            if (!ra.IsEmpty())
            {
                await Delay(500, cts);
                ra.Click();
                await Delay(500, cts);//两次确认，防止卡顿
                ra.Click();
                await Delay(1000, cts);  
                break;
            }
            else
            {
                exitSwitchClickCnt++;   
                if (exitSwitchClickCnt > 2)
                {
                    await Delay(1000, cts);
                }
            }
            await Delay(2000, cts);  
        }
        
        // 2点击“退出”按钮
        Logger.LogInformation("点击 {text} 按钮", "退出");
        var exitClickCnt = 0;
        for (var i = 0; i < 20; i++)
        {
            await Delay(1, cts);
            var ra = CaptureToRectArea();
            var list = ra.FindMulti(new RecognitionObject
            {
                RecognitionType = RecognitionTypes.Ocr,
                RegionOfInterest = new Rect(ra.Width/2, ra.Height *11/20, ra.Width/5, ra.Height/8)
            });
            Region? exitClickCntIcon = list.FirstOrDefault(r => r.Text.Contains("退出"));
            if (exitClickCntIcon != null)
            {
                await Delay(500, cts);
                exitClickCntIcon.Click();
                await Delay(1000, cts);  
                break;
            }
            else
            {
                exitClickCnt++;
                if (exitClickCnt > 2)
                {
                    await Delay(1000, cts); 
                }
            }
            await Delay(1000, cts);  
        }
        
        // 3点击账号选择按钮
        var exitPhoneClickCnt = 0;
        for (var i = 0; i < 20; i++)
        {
            await Delay(1, cts);
            
            var mainRegion= await NewRetry.WaitForElementAppear(
                GetConfirmRa("进入游戏"),
                () => {},
                cts,
                20,
                500
            );
            if (mainRegion)
            {
                Logger.LogInformation("执行 {text} 动作","选择账号");
                await NewRetry.WaitForElementDisappear(
                    GetConfirmRa("进入游戏"),
                    () => {GameCaptureRegion.GameRegion1080PPosClick(1100,494);},
                    cts,
                    5,
                    1500
                );
                
                await Delay(300, cts);
                
                var capturedArea = CaptureToRectArea();
                bool isAccountBinding = false;
                var phoneList = capturedArea.FindMulti(RecognitionObject.Ocr(new Rect(760 , 455 , 330, 390)));
                if (phoneList.Count > 0 && !string.IsNullOrEmpty(bindingCode))
                {
                    _exitPhoneCount = phoneList.Count(p => p.Text.Any(c => c == '*'));
                    Logger.LogInformation("当前记录账号数量: {count}", _exitPhoneCount-1);
                    
                    if (_exitPhoneCount < 3 || _exitPhoneCount > 4)
                    {
                        Logger.LogWarning("请检查账号数量是否正确，数量应为2或3");
                    }

                    foreach (var phone in phoneList)
                    {
                        if (phone.Text.Any( c => c == '*'))
                        {
                            string text = phone.Text;
                            if (text.Length < 2)
                            {
                                Logger.LogWarning("字符长度不足2位");
                            }

                            int index = text.Length - 1;
                            string comfirmWord = "";

                            while (index >= 0 && comfirmWord.Length < 2)
                            {
                                char currentChar = text[index];
                                if (char.IsDigit(currentChar))
                                {
                                    comfirmWord = currentChar + comfirmWord;
                                }
                                index--;
                            }

                            if (comfirmWord.Length != 2)
                            {
                                index = 0;
                                string firstTwoChars = "";

                                while (index < text.Length && firstTwoChars.Length < 2)
                                {
                                    char currentChar = text[index];
                                    if (char.IsLetterOrDigit(currentChar))
                                    {
                                        firstTwoChars += currentChar;
                                    }
                                    index++;
                                }

                                if (firstTwoChars.Length == 2)
                                {
                                    comfirmWord = firstTwoChars;
                                }
                            }
                            
                            if (comfirmWord == bindingCode)
                            {
                               // 如果账号绑定成功，点击该账号
                                Logger.LogInformation("UID: {0} 已绑定 {1}", genshinUid, bindingCode);
                                phone.Click();
                                isAccountBinding = true;
                                await Delay(500, cts);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    Logger.LogWarning(string.IsNullOrEmpty(bindingCode) ? "UID为绑定码为空，重新绑定UID可设置绑定码" : "未检测到账号列表");
                }
                
                //识别识别后用旧办法
                if (isAccountBinding == false)
                {
                    Logger.LogWarning("未检测到账号列表匹配的绑定码，重新绑定UID可设置绑定码");
                    Logger.LogWarning("尝试使用轮切方式切换账号...");
                    Notify.Event("未检测到账号列表匹配的绑定码，重新绑定UID可设置绑定码");
                    
                    await Delay(500, cts);
                    if (_exitPhoneCount == 3 || (_exitPhoneCount == 4 && switchTime == 1))
                    {
                        GameCaptureRegion.GameRegion1080PPosClick(732,670);;//如果只有两账号，固定选另一个
                    }
                    else if (_exitPhoneCount == 4 && switchTime >= 2)
                    {
                        GameCaptureRegion.GameRegion1080PPosClick(735,742);;//如果有三个账号，切换到第3个
                    }
                    
                }
                
                await Delay(1000, cts);     
                GameCaptureRegion.GameRegion1080PPosClick(1158,626);;//进入游戏
                await Delay(1000, cts);  
                GameCaptureRegion.GameRegion1080PPosClick(1158,626);;//进入游戏
                await Delay(1000, cts);   
                break;
            }
            else
            {
                exitPhoneClickCnt++; 
                if (exitPhoneClickCnt > 2)
                {
                    await Delay(1000, cts);
                    break;
                }
            }
            await Delay(1000, cts);  
        }

        // 4进入游戏检测
        var clickCnt = 0;
        for (var i = 0; i < 50; i++)
        {
            await Delay(1, cts);

            using var contentRegion = CaptureToRectArea();
            using var ra = contentRegion.Find(_assets.EnterGameRo);
            if (!ra.IsEmpty())
            {
                clickCnt++;
                GameCaptureRegion.GameRegion1080PPosClick(955, 656);
                GameCaptureRegion.GameRegion1080PPosClick(1660, 282);//非凌晨4点，点击屏幕
            }
            else
            {
                if (clickCnt > 2)
                {
                    await Delay(5000, cts);
                    break;
                }
            }
            await Delay(1000, cts);  
        }
        
        if (clickCnt == 0)
        {
            throw new RetryException("未检测进入游戏界面");
        }

        for (var i = 0; i < 50; i++)
        {
            if (Bv.IsInMainUi(CaptureToRectArea()))
            {
                Logger.LogInformation("执行 {text} 操作结束","更换账号");
                break;
            }
            else
            {
                await new BlessingOfTheWelkinMoonTask().Start(CancellationContext.Instance.Cts.Token);
                GameCaptureRegion.GameRegion1080PPosClick(955, 656);//非凌晨4点，点击屏幕
                GameCaptureRegion.GameRegion1080PPosClick(1660, 282);//非凌晨4点，点击屏幕
            }
            
            await Delay(1000, cts);
            
           if (i == 49)
           {
               Logger.LogWarning("更换账号失败");
               await Delay(500, cts);
               return false;
           }
           
        }
        await Delay(500, cts);
        return true;
    }
}
