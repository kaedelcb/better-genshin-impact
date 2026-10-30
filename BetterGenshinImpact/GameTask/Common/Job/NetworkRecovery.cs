using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using System.Linq;
using BetterGenshinImpact.Core.Recognition;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.GameTask.Common;

namespace BetterGenshinImpact.GameTask.Common.Job;

public class NetworkRecovery
{
    public string Name => "断网恢复";
    
    private static RecognitionObject GetConfirmRa(bool isOcrMatch = false,params string[] targetText)
    {
        using var screenArea = CaptureToRectArea();
        var x = (int)(screenArea.Width * 0.3);
        var y = (int)(screenArea.Height * 0.1);
        var width = (int)(screenArea.Width * 0.65);
        var height = (int)(screenArea.Height * 0.87);
        
        return isOcrMatch ? RecognitionObject.OcrMatch(x, y, width, height, targetText) : 
            RecognitionObject.Ocr(x, y, width, height);
    }
    
    //完成任务标志
    public static bool RecoveryNetworkDone = false;
    
    public static async Task Start(CancellationToken ct, bool verifyMainUi = false, bool clearSuspend = true)
    {
        ct.ThrowIfCancellationRequested();
        RecoveryNetworkDone = false;
        
        var aa =await NewRetry.WaitForElementDisappear(
            GetConfirmRa(true,"连接超时","连接已断开","网络错误","无法登录服务器","提示","通知"),
            screen => { 
                var confirm =
                    screen.FindMulti(GetConfirmRa());
                var confirmDone = confirm.LastOrDefault(t =>
                    Regex.IsMatch(t.Text, "确认") || Regex.IsMatch(t.Text, "点击进入"));
                if (confirmDone != null)
                {
                    Logger.LogWarning("点击: {confirmDone.Text}",confirmDone.Text);
                    confirmDone.Click();
                    confirmDone.Dispose();
                }
            },
            ct,
            3,
            1000
        );

        // if (!aa)
        // {
        //     return;
        // }
        
        await Task.Delay(1000, ct);
        
        await NewRetry.WaitForElementDisappear(
            GetConfirmRa(true,"连接超时","连接已断开","网络错误","无法登录服务器","提示","通知"),
            screen => { 
                var confirm =
                    screen.FindMulti(GetConfirmRa());
                var confirmDone = confirm.LastOrDefault(t =>
                    Regex.IsMatch(t.Text, "确认"));
                if (confirmDone != null)
                {
                    confirmDone.Click();
                    confirmDone.Dispose();
                }
            },
            ct,
            3,
            1000
        );
        
        var mainUiAppeared = await NewRetry.WaitForElementAppear(
            ElementAssets.Instance.PaimonMenuRo,
            ()  => {  
                
                if (RecoveryNetworkDone && !IsSuspendedByWindow)
                {
                    Logger.LogWarning("回到主页-1:{RecoveryNetworkDone} - {IsSuspendedByWindow}",RecoveryNetworkDone,IsSuspendedByWindow);
                    return;//回到主页一次后，后续异步执行的所有操作都取消
                }
                IsSuspendedByNetwork = true;
                Logger.LogWarning("尝试恢复中-1...");
                
                using var ra = CaptureToRectArea();
                
                var tips = ra.Find(ElementAssets.Instance.MiMenuRo);
                if (tips.IsExist())
                {
                    tips.Click();
                    tips.Dispose();
                }
                
                var enter =
                    ra.FindMulti(GetConfirmRa());
                var enterDone = enter.FirstOrDefault(t =>
                    Regex.IsMatch(t.Text, "^确认$")  || Regex.IsMatch(t.Text, "^取消$") || 
                    Regex.IsMatch(t.Text, "点击进入"));
                if (enterDone != null)
                {
                    enterDone.Click();
                    enterDone.Dispose();
                }
                
                var enterG = enter.FirstOrDefault(t =>
                    Regex.IsMatch(t.Text, "登录其他账号"));
                if (enterG != null)
                {
                    enterG.ClickTo(0,-enterG.Height);
                    enterG.Dispose();
                }                

                var exit = enter.FirstOrDefault(t =>
                    Regex.IsMatch(t.Text, "忘记密码"));
                if (exit != null)
                {
                    GameCaptureRegion.GameRegion1080PPosClick(1257,258); 
                }
            },
            ct,
            60,
            1000
        );
        
        ct.ThrowIfCancellationRequested();
        if (verifyMainUi && !mainUiAppeared) return;
        Logger.LogWarning("回到主页-2");
        await new ReturnMainUiTask().Start(ct);
        ct.ThrowIfCancellationRequested();
        if (verifyMainUi)
        {
            using var screen = CaptureToRectArea();
            if (!BetterGenshinImpact.GameTask.Common.BgiVision.Bv.IsInMainUi(screen)) return;
        }
        if (!clearSuspend) return; // 快速拖动模式由真实开图/关图响应确认后统一解除。
        GameCaptureRegion.GameRegion1080PPosClick(960, 540);
        IsSuspendedByNetwork = false;
        RecoveryNetworkDone = true;
        IsSuspendedByWindow = false;
    }
}
