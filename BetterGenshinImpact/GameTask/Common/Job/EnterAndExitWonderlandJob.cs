using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.GameTask.AutoTrackPath;

namespace BetterGenshinImpact.GameTask.Common.Job;

public class EnterAndExitWonderlandJob
{
    public async Task Start(CancellationToken ct)
    {
        TpTaskFastDrag tpTaskFastDrag = new TpTaskFastDrag(ct);
        
        Logger.LogInformation("进入千星奇域");
        SystemControl.FocusWindow(TaskContext.Instance().GameHandle);

        await tpTaskFastDrag.OpenBigMapUi();

        await tpTaskFastDrag.SwitchArea("千星奇域");
        
        // // 点击大厅按钮并等待公共大厅按钮出现
        // await NewRetry.WaitForElementAppear(
        //     _assets.WonderlandEnter,
        //     () =>
        //     {
        //         using var ra = CaptureToRectArea();
        //         Bv.FindAndClick(ra, _assets.EscWonderlandHome);
        //     },
        //     ct,
        //     5,
        //     1000
        // );
        //
        // // 点击公共大厅按钮并等待确认弹窗出现
        // await NewRetry.WaitForElementAppear(
        //     _assets.BtnBlackConfirm,
        //     () => 
        //     {
        //         using var ra = CaptureToRectArea();
        //         Bv.FindAndClick(ra, _assets.WonderlandEnter);
        //     },
        //     ct,
        //     5,
        //     800
        // );
        
        // 点击前往大厅并等待弹窗消失
        await NewRetry.WaitForElementDisappear(
            ElementRecognition.Get("BtnBlackConfirm"),
            screen =>
            {
                // 接收当前截图作为参数
                screen.Find(ElementRecognition.Get("BtnBlackConfirm", screen), ra =>
                {
                    ra.Click();
                    ra.Dispose();
                });
            },
            ct,
            5,
            1000
        );
        await Delay(1000, ct);

        // 等待主界面出现
        var mainUiFound1 = await NewRetry.WaitForElementAppear(
            ElementRecognition.Get("PaimonMenu"),
            () => { },
            ct,
            120,
            300
        );

        if (mainUiFound1)
        {
            Logger.LogInformation("已进入千星奇域大厅，准备返回提瓦特");
        }
        else
        {
            Logger.LogWarning("未检测到主界面，可能未处于千星奇域");
        }

        await Delay(500, ct);
        
        // 等待菜单界面出现
        await NewRetry.WaitForElementAppear(
            ElementRecognition.Get("BtnBackTeyvat"),
            () => Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE),
            ct,
            20,
            800
        );
        
        // 点击返回提瓦特按钮并等待确认弹窗出现
        await NewRetry.WaitForElementAppear(
            ElementRecognition.Get("BtnBlackConfirm"),
            () => 
            {
                using var ra = CaptureToRectArea();
                Bv.FindAndClick(ra, ElementRecognition.Get("BtnBackTeyvat", ra));
            },
            ct,
            5,
            300
        );
        
        // 点击确认并等待确认弹窗消失
        await NewRetry.WaitForElementDisappear(
            ElementRecognition.Get("BtnBlackConfirm"),
            screen =>
            {
                // 接收当前截图作为参数
                screen.Find(ElementRecognition.Get("BtnBlackConfirm", screen), ra =>
                {
                    ra.Click();
                    ra.Dispose();
                });
            },
            ct,
            5,
            500
        );
        await Delay(1000, ct);
        
        // 等待主界面出现
        var mainUiFound2 = await NewRetry.WaitForElementAppear(
            ElementRecognition.Get("PaimonMenu"),
            () => { },
            ct,
            120,
            500
        );

        if (mainUiFound2)
        {
            Logger.LogInformation("已返回提瓦特");
        }
        else
        {
            Logger.LogWarning("未检测到主界面，可能未处于提瓦特");
        }

        await Delay(500, ct);
    }
}
