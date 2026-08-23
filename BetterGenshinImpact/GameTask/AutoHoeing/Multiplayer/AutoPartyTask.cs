#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 联机锄地自动组队：房主在 F2 界面等待并按 Y 同意 / 成员搜索房主 UID 申请加入
/// </summary>
public class AutoPartyTask
{
    private readonly ILogger _logger = App.GetLogger<AutoPartyTask>();

    // 确认按钮模板（comfirm_btn1.png）
    private static readonly RecognitionObject ConfirmBtnRo = new RecognitionObject
    {
        Name = "CoOpConfirmBtn",
        RecognitionType = RecognitionTypes.TemplateMatch,
        TemplateImageMat = GameTaskManager.LoadAssetImage("AutoSkip", "comfirm_btn1.png"),
        Threshold = 0.7,
        DrawOnWindow = false
    }.InitTemplate();

    // 1080P 坐标常量（多人游戏界面）
    private const double UidInputX = 222, UidInputY = 120;
    private const double SearchBtnX = 1676, SearchBtnY = 123;
    private const double ApplyBtnX = 1625, ApplyBtnY = 245;

    // F2 玩家行 1080P 坐标（玩家名 OCR 区域）
    // 用户实测：2P 起点 (417, 307)，3P 起点 (417, 429)，4P 起点 (417, 555)
    // 行间距约 122-126px，本身有踢出按钮 → 用按钮 Y 中心反推 OCR 行更稳，但作为兜底保留这套常量
    private const double PlayerNameX = 417;
    private const double PlayerNameW = 400;
    private const double PlayerNameH = 40;
    // 已知 2P~4P 名字起点 Y（1080P）
    private static readonly double[] PlayerNameY1080P = [307, 429, 555];

    /// <summary>
    /// UID 脱敏：保留前 3 位和后 3 位，中间用 *** 代替（用于日志输出）
    /// </summary>
    public static string MaskUid(string? uid)
    {
        if (string.IsNullOrEmpty(uid)) return "";
        return uid!.Length > 6 ? $"{uid[..3]}***{uid[^3..]}" : uid;
    }

    /// <summary>
    /// 成员流程：搜索房主 UID，申请加入，等待进入世界
    /// 申请后按钮倒数 10 秒，倒数结束后可再次点击。房主同意后直接加载。
    /// </summary>
    public async Task<bool> JoinHostWorldAsync(string hostUid, CancellationToken ct)
    {
        _logger.LogInformation("[自动组队-成员] 开始，房主 UID: {Uid}", MaskUid(hostUid));

        // 1. 尝试回到主界面
        _logger.LogInformation("[自动组队-成员] 尝试回到主界面");
        try
        {
            await new BetterGenshinImpact.GameTask.Common.Job.ReturnMainUiTask().Start(ct);
        }
        catch { /* 忽略 */ }
        await Delay(500, ct);

        if (!await WaitForMainUi(ct, 10))
        {
            _logger.LogError("[自动组队-成员] 回到主界面失败");
            return false;
        }

        // 2. 打开 F2 多人游戏界面
        if (!await OpenCoOpScreen(ct))
        {
            _logger.LogError("[自动组队-成员] 打开多人游戏界面失败");
            return false;
        }

        // 3. 输入房主 UID 并搜索，OCR 验证搜索结果（最多 10 次）
        // 粘贴可能异常导致搜索失败，需要检测"申请加入"按钮数量：
        //   == 1 → 搜索成功；== 0 或 > 1 → 失败重试
        const int maxSearchRetries = 10;
        bool searchOk = false;
        for (int searchAttempt = 1; searchAttempt <= maxSearchRetries; searchAttempt++)
        {
            ct.ThrowIfCancellationRequested();
            await InputUidAndSearch(hostUid, ct);
            // 等待搜索结果渲染稳定
            await Delay(800, ct);

            var btnCount = CountApplyButtons();
            if (btnCount == 1)
            {
                _logger.LogInformation("[自动组队-成员] 搜索成功（第 {N} 次尝试）", searchAttempt);
                searchOk = true;
                break;
            }

            _logger.LogWarning("[自动组队-成员] 搜索结果异常（\"申请加入\" 按钮数={Count}），第 {Attempt}/{Max} 次重试",
                btnCount, searchAttempt, maxSearchRetries);
        }

        if (!searchOk)
        {
            _logger.LogError("[自动组队-成员] {Max} 次搜索仍未定位到房主，退出 F2 结束任务", maxSearchRetries);
            Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
            await Delay(500, ct);
            return false;
        }

        // 4. 循环申请加入，最多 30 次
        for (int attempt = 1; attempt <= 30; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("[自动组队-成员] 第 {N}/30 次申请加入", attempt);

            // 点击"申请加入"按钮（点两次确保响应）
            GameCaptureRegion.GameRegion1080PPosClick(ApplyBtnX, ApplyBtnY);
            await Delay(300, ct);
            GameCaptureRegion.GameRegion1080PPosClick(ApplyBtnX, ApplyBtnY);
            await Delay(1000, ct);

            // 等待：按钮倒数 10 秒 + 可能的加载时间
            // 每秒检测一次是否进入了加载（派蒙消失 = 可能在加载）
            for (int wait = 0; wait < 15; wait++)
            {
                await Delay(1000, ct);

                // 检测是否已进入"房主世界"：派蒙可见 + 联机状态 + 不是房主
                // 仅靠派蒙不够，自己世界也能看到派蒙（被拒绝/F2 切换中间帧）会导致误判
                using var ra = CaptureToRectArea();
                if (await IsInHostWorldAsync(ra, ct))
                {
                    _logger.LogInformation("[自动组队-成员] 检测到已进入房主世界（派蒙可见且为成员视角）");
                    return true;
                }
            }

            // 15 秒后还没进入世界，可能申请被忽略或拒绝
            // 二次判定：派蒙可见且仍在自己世界 → 视为被拒绝，重新搜索
            using var checkRa = CaptureToRectArea();
            // 先复用一次"已进入房主世界"判定，覆盖刚好这一次截图捕到加载完成的情况
            if (await IsInHostWorldAsync(checkRa, ct))
            {
                _logger.LogInformation("[自动组队-成员] 等待结束时检测到已进入房主世界");
                return true;
            }
            if (Bv.IsInMainUi(checkRa))
            {
                // 派蒙可见但不在房主世界 → 申请被拒绝/超时，回到自己世界
                // 重新打开 F2 并搜索（搜索失败时同样会重试 10 次）
                _logger.LogInformation("[自动组队-成员] 回到自己世界，重新打开 F2 搜索");
                if (!await OpenCoOpScreen(ct)) continue;

                bool reSearchOk = false;
                for (int searchAttempt = 1; searchAttempt <= 10; searchAttempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    await InputUidAndSearch(hostUid, ct);
                    await Delay(800, ct);
                    if (CountApplyButtons() == 1)
                    {
                        reSearchOk = true;
                        break;
                    }
                    _logger.LogWarning("[自动组队-成员] 重新搜索失败，第 {N}/10 次重试", searchAttempt);
                }
                if (!reSearchOk)
                {
                    _logger.LogError("[自动组队-成员] 重新搜索 10 次失败，结束任务");
                    Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                    await Delay(500, ct);
                    return false;
                }
            }
            // 否则还在 F2 界面，按钮倒数结束，可以再次点击申请
        }

        _logger.LogError("[自动组队-成员] 30 次尝试后仍未加入，放弃");
        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
        await Delay(500, ct);
        return false;
    }

    /// <summary>
    /// 房主流程：在 F2 界面等待成员加入，按 Y 同意申请。
    /// 返回值：-1=失败，0=超时，>0=实际就绪人数（含房主）
    /// 房主可按回车跳过等待，以当前人数开始。
    /// </summary>
    public async Task<int> WaitForMembersAsync(
        int expectedCount,
        string[]? whitelist,
        CoordinatorClient client,
        int timeoutSeconds,
        CancellationToken ct)
    {
        _logger.LogInformation("[自动组队-房主] 开始，期望人数: {N}", expectedCount);

        // 1. 尝试回到主界面
        _logger.LogInformation("[自动组队-房主] 尝试回到主界面");
        try
        {
            await new BetterGenshinImpact.GameTask.Common.Job.ReturnMainUiTask().Start(ct);
        }
        catch { /* 忽略 */ }
        await Delay(500, ct);

        if (!await WaitForMainUi(ct, 10))
        {
            _logger.LogError("[自动组队-房主] 回到主界面失败");
            return -1;
        }

        // 2. 打开 F2
        if (!await OpenCoOpScreen(ct))
        {
            _logger.LogError("[自动组队-房主] 打开多人游戏界面失败");
            return -1;
        }

        // 设置等待状态为 true
        AutoHoeingTask.IsWaitingForParty = true;

        // 用 UID 集合做"自己人"判定（成员加入 BGI 房间时上报 UID 和 PlayerName）
        // 名字也准备一份用于 OCR 容错匹配
        // 跨轮次维护每个"疑似陌生人"身份键的连续不匹配计数（连续 2 次才踢，吸收单帧 OCR 抽风）。
        // 局部变量：方法退出即释放，不用静态字段避免跨会话污染（显式依赖原则）。
        var consecutiveMissByIdentity = new Dictionary<string, int>(StringComparer.Ordinal);
        // F2 诊断日志节流状态（策略 D：变化立即输出 + 无变化每 5s 兜底心跳）
        F2DiagnosticLogThrottle.F2Metrics? lastEmittedF2Metrics = null;
        DateTime lastF2DiagnosticEmitTime = DateTime.MinValue;

        try
        {
            var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
            bool isInF2Screen = true; // 追踪当前是否在 F2 界面
            while (DateTime.Now < deadline)
            {
                ct.ThrowIfCancellationRequested();
                _logger.LogDebug("[自动组队-房主] 循环检测，剩余时间: {Sec}s，当前人数: {Count}", (int)(deadline - DateTime.Now).TotalSeconds, client.CurrentRoomPlayerCount);

                // 循环级缓存：最近一轮 F2 诊断算出的权威人数（= kickCount + 1）。
                // 决策块只在 !inMainUi 时更新它；周期扫描仅在 isInF2Screen 时消费，与 F2 轮次对应，值有效。
                // 供块外周期性踢陌生人扫描复用同一份权威人数，避免引入第二人数来源（见 design §改动 2）。
                int lastF2Count = 0;

                // 检测"立即开始"标志（房主在 BGI UI 点击了立即开始按钮）
                if (AutoHoeingTask.SkipPartyWait)
                {
                    AutoHoeingTask.SkipPartyWait = false;
                    var currentCount = client.CurrentRoomPlayerCount;
                    _logger.LogInformation("[自动组队-房主] 收到立即开始信号，以当前 {N} 人开始锄地", currentCount);
                    Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                    await Delay(500, ct);
                    await WaitForMainUi(ct, 5);
                    return currentCount > 0 ? currentCount : 1;
                }

                // 核心修改：始终检测申请弹窗（无论在主界面还是 F2 界面）
                // 弹窗可能在主界面出现（第一个成员加入后触发加载，回到主界面时新申请弹窗出现）
                using (var checkRa = CaptureToRectArea())
                {
                    var hasPopup = checkRa.Find(ConfirmBtnRo).IsExist();
                    if (hasPopup)
                    {
                        var shouldAccept = true;
                        if (whitelist != null && whitelist.Length > 0)
                        {
                            var applicantName = OcrApplicantName();
                            if (!string.IsNullOrEmpty(applicantName))
                            {
                                shouldAccept = IsInWhitelist(applicantName, whitelist);
                                _logger.LogInformation("[自动组队-房主] OCR 识别申请者: {Name}，白名单匹配: {Match}",
                                    applicantName, shouldAccept);
                            }
                            else
                            {
                                _logger.LogWarning("[自动组队-房主] OCR 识别失败，跳过本次申请");
                                shouldAccept = false;
                            }
                        }

                        if (shouldAccept)
                        {
                            ClickConfirmButton();
                            await Delay(300, ct);
                            ClickConfirmButton();
                            await Delay(700, ct);
                            _logger.LogDebug("[自动组队-房主] 已点击确认，等待加载...");

                            // 处理完弹窗后继续检测，可能还有更多申请
                            continue;
                        }
                        else
                        {
                            ClickRejectButton();
                            await Delay(500, ct);
                            continue;
                        }
                    }
                }

                // 检测当前 UI 状态：主界面 vs F2
                // 在两种界面上都尝试满员判定，互为保险，避免被任一信号源卡死
                using (var checkRa = CaptureToRectArea())
                {
                    var inMainUi = Bv.IsInMainUi(checkRa);
                    var signalRCount = client.CurrentRoomPlayerCount;

                    // F2 踢出按钮扫描仅在非主界面时执行（主界面下 F2 关闭，f2Count 不可观测）。
                    // 主界面分支不读取 f2Count，传 0 即可——决策函数已约束 InMainUi=true 时不读 F2Count。
                    var f2Count = 0;
                    var kickCount = 0;
                    if (!inMainUi)
                    {
                        // 不在主界面：派蒙不可见，多半在 F2 页面
                        // 信号 B：F2 页面 → 数右侧红色"踢出"按钮（房主自己没有，每个成员对应 1 个）
                        // 实际进入世界人数 = 踢出按钮数量 + 1
                        // 用局部克隆改 ROI，绝不触碰共享单例 KickBtnRa（消除跨调用污染根因，见 design §改动 3）。
                        for (var i = 4; i > 0; i--)
                        {
                            var aa = RecognitionObject.Ocr(checkRa.Width * 0.5, checkRa.Height * 0.61 - 125 * (4 - i), checkRa.Width * 0.5, checkRa.Height * 0.4 - 30);
                            var probe = AutoFightAssets.Get(checkRa).KickBtnRa.Clone();
                            probe.RegionOfInterest = aa.RegionOfInterest;
                            if (checkRa.Find(probe).IsExist())
                            {
                                kickCount = i;
                                break;
                            }
                        }
                        f2Count = kickCount + 1;
                        lastF2Count = f2Count;   // 回写循环级缓存，供块外周期扫描复用同一份权威人数
                        // 诊断日志：策略 D 节流——metrics 变化立即输出，无变化每 5s 兜底心跳一条，避免刷屏淹没其他日志
                        var currentF2Metrics = new F2DiagnosticLogThrottle.F2Metrics(kickCount, f2Count, expectedCount, signalRCount);
                        var nowForF2Diag = DateTime.Now;
                        if (F2DiagnosticLogThrottle.ShouldEmit(
                                currentF2Metrics, lastEmittedF2Metrics, lastF2DiagnosticEmitTime,
                                nowForF2Diag, F2DiagnosticLogThrottle.DefaultHeartbeatInterval))
                        {
                            _logger.LogInformation("[自动组队-房主][F2诊断] 踢出按钮={Kick}，实际人数={F2Count}/{Expected}，SignalR人数={SignalR}",
                                kickCount, f2Count, expectedCount, signalRCount);
                            lastEmittedF2Metrics = currentF2Metrics;
                            lastF2DiagnosticEmitTime = nowForF2Diag;
                        }
                    }

                    var decision = HostPartyReadinessDecisions.Decide(new HostPartyReadinessInput(
                        InMainUi: inMainUi,
                        SignalRCount: signalRCount,
                        F2Count: f2Count,
                        ExpectedCount: expectedCount,
                        IsInF2Screen: isInF2Screen));

                    switch (decision.Kind)
                    {
                        case HostPartyDecisionKind.StartHoeing:
                            // 仅 InMainUi=false 路径会到达这里（F2 信号 B 满员 + 通过陌生人交叉校验）
                            _logger.LogInformation("[自动组队-房主] F2 检测到实际进入世界人数已满 {Count}/{Expected}（踢出按钮={Kick}），主动关闭 F2 开始锄地",
                                decision.ReturnedCount, expectedCount, kickCount);
                            Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                            await Delay(500, ct);
                            await WaitForMainUi(ct, 10);
                            return decision.ReturnedCount;

                        case HostPartyDecisionKind.KickStrangers:
                            // 满员触发逐行成分校验。防误踢由名字校验 + 连续 2 次确认接管，
                            // 不再受时间保护期 / 扫描节流门控（组队放人阶段不需等待）。
                            var kickResult = await KickStrangersAsync(client, consecutiveMissByIdentity, expectedCount, f2Count, whitelist, ct);
                            if (kickResult == StrangerKickScanResult.AllAllowed)
                            {
                                // 满员且逐行校验全为名单成员 ⇒ 开锄收敛出口
                                _logger.LogInformation("[自动组队-房主] F2 满员且逐行校验全为名单成员，开始锄地 {Count}/{Expected}",
                                    f2Count, expectedCount);
                                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                                await Delay(500, ct);
                                await WaitForMainUi(ct, 10);
                                return f2Count;
                            }
                            // Kicked 或 PendingStranger（陌生人尚未连续 2 次）或 NotFull → 继续下一轮
                            await Delay(1000, ct);
                            continue;

                        case HostPartyDecisionKind.ReopenF2WithLoadDelay:
                            // 上一轮在 F2，本轮被弹回主界面 → 多半是有玩家加入触发了加载
                            // 注意：signalRCount 是否达标都走这条路径——区别只在日志措辞
                            if (signalRCount >= expectedCount)
                            {
                                _logger.LogInformation("[自动组队-房主] 主界面检测到 SignalR 人数已达 {N}/{Expected}，切回 F2 复核游戏世界实际人数",
                                    signalRCount, expectedCount);
                            }
                            else
                            {
                                _logger.LogInformation("[自动组队-房主] 检测到主界面（玩家加入触发加载），人数: {Count}/{Expected}",
                                    signalRCount, expectedCount);
                            }
                            isInF2Screen = false;
                            await Delay(2000, ct);
                            await WaitForMainUi(ct, 10);
                            if (!await OpenCoOpScreen(ct, whitelist))
                            {
                                _logger.LogWarning("[自动组队-房主] 重新打开 F2 失败，重试");
                                await Delay(2000, ct);
                                continue;
                            }
                            isInF2Screen = true;
                            continue;

                        case HostPartyDecisionKind.ReopenF2NoDelay:
                            // 一直在主界面（上一轮也没在 F2）→ F2 没开成功
                            if (signalRCount >= expectedCount)
                            {
                                _logger.LogInformation("[自动组队-房主] 主界面检测到 SignalR 人数已达 {N}/{Expected}，但 F2 未打开，重新打开 F2 复核",
                                    signalRCount, expectedCount);
                            }
                            else
                            {
                                _logger.LogDebug("[自动组队-房主] 在主界面但 F2 未打开，尝试打开 F2");
                            }
                            if (!await OpenCoOpScreen(ct, whitelist))
                            {
                                await Delay(1000, ct);
                            }
                            else
                            {
                                isInF2Screen = true;
                            }
                            continue;

                        case HostPartyDecisionKind.WaitInF2:
                            // 落到循环底部的"周期性踢陌生人扫描 + 按 Y 触发申请弹窗"逻辑
                            break;
                    }
                }

                // 周期性踢陌生人扫描：F2 页面下每轮自然节拍扫描，不加人为节流。
                // 满员触发 + 逐行名字校验 + 连续 2 次确认在 KickStrangersAsync 内部完成。
                if (isInF2Screen)
                {
                    if (await KickStrangersAsync(client, consecutiveMissByIdentity, expectedCount, lastF2Count, whitelist, ct) == StrangerKickScanResult.Kicked)
                    {
                        // 踢人会触发弹窗 / UI 变化，下一轮重新评估
                        continue;
                    }
                }

                // 在 F2 界面，持续按 Y 触发申请弹窗（弹窗本身由循环顶部 ConfirmBtnRo 持续检测捕获）
                // 设计：按 Y 与弹窗检测解耦——Y 不停按让游戏尽快弹出申请，识别由顶部统一处理。
                // 间隔 250ms（按 Y 后给游戏足够响应时间但不过度等待），让弹窗 10s 倒计时窗口内
                // 能容纳更多次顶部检测机会（约 25+ 次），减少漏识。
                if (isInF2Screen)
                {
                    Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_Y);
                    await Delay(250, ct);
                }
                else
                {
                    // 不在 F2（理论上不应该出现，主界面分支会补开 F2），保底休眠避免空转
                    await Delay(500, ct);
                }
            }

            // 超时：返回 0，由调用方根据 PartyTimeoutAction 决定
            _logger.LogWarning("[自动组队-房主] 等待超时 ({Timeout}s)，当前 {N} 人", timeoutSeconds, client.CurrentRoomPlayerCount);
            Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
            await Delay(500, ct);
            return 0;
        }
        finally
        {
            // 重置等待状态
            AutoHoeingTask.IsWaitingForParty = false;
        }
    }

    /// <summary>在 F2 界面输入 UID 并搜索</summary>
    private async Task InputUidAndSearch(string uid, CancellationToken ct)
    {
        // 点击 UID 输入框
        GameCaptureRegion.GameRegion1080PPosClick(UidInputX, UidInputY);
        await Delay(300, ct);
        // 再点一次确保输入框获得焦点
        GameCaptureRegion.GameRegion1080PPosClick(UidInputX, UidInputY);
        await Delay(1000, ct);

        // Ctrl+A 全选
        Simulation.SendInput.Keyboard.KeyDown(false, User32.VK.VK_CONTROL);
        await Delay(20, ct);
        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_A);
        await Delay(20, ct);
        Simulation.SendInput.Keyboard.KeyUp(false, User32.VK.VK_CONTROL);
        await Delay(50, ct);

        // Ctrl+V 粘贴 UID
        UIDispatcherHelper.Invoke(() => Clipboard.SetDataObject(uid));
        await Delay(50, ct);
        Simulation.SendInput.Keyboard.KeyDown(false, User32.VK.VK_CONTROL);
        await Delay(20, ct);
        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_V);
        await Delay(20, ct);
        Simulation.SendInput.Keyboard.KeyUp(false, User32.VK.VK_CONTROL);
        await Delay(300, ct);

        // 点击搜索（点两次确保响应）
        GameCaptureRegion.GameRegion1080PPosClick(SearchBtnX, SearchBtnY);
        await Delay(300, ct);
        GameCaptureRegion.GameRegion1080PPosClick(SearchBtnX, SearchBtnY);
        await Delay(1500, ct);

        _logger.LogInformation("[自动组队] 已输入 UID {Uid} 并搜索", MaskUid(uid));
    }

    /// <summary>
    /// OCR 整页搜索"申请加入"按钮的数量。
    /// == 1：成功定位到唯一房主；== 0 或 > 1：搜索结果异常（粘贴失败 / 多结果 / 渲染未完成）。
    /// </summary>
    private int CountApplyButtons()
    {
        try
        {
            using var ra = CaptureToRectArea();
            //只识别ra的右半部分（申请加入按钮在右边），提高效率和准确率
            var width = ra.SrcMat.Width;
            var height = ra.SrcMat.Height;
            var roiRect = new OpenCvSharp.Rect(width / 2, 0, width / 2, height);
            using var roi = new OpenCvSharp.Mat(ra.SrcMat, roiRect);
            var result = OcrFactory.Paddle.OcrResult(roi);
            int count = 0;
            foreach (var region in result.Regions)
            {
                var t = region.Text;
                if (string.IsNullOrEmpty(t)) continue;
                // 容错匹配："申请加入" / "申请加入 (10)" 等倒数文案
                if (t.Contains("申请加入") || (t.Contains("申请") && t.Contains("加入")))
                {
                    count++;
                }
            }
            _logger.LogDebug("[自动组队-成员] OCR 检测到 \"申请加入\" 按钮数: {Count}", count);
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[自动组队-成员] OCR 检测申请加入按钮异常");
            return 0;
        }
    }

    /// <summary>打开 F2 多人游戏界面，重试 3 次。每次重试间隙检测申请弹窗并处理。</summary>
    private async Task<bool> OpenCoOpScreen(CancellationToken ct, string[]? whitelist = null)
    {
        for (int i = 0; i < 3; i++)
        {
            Simulation.SendInput.SimulateAction(GIActions.OpenCoOpScreen);
            await Delay(1500, ct);

            // 派蒙消失 = 界面打开了
            using var ra = CaptureToRectArea();
            if (!Bv.IsInMainUi(ra))
            {
                _logger.LogInformation("[自动组队] F2 界面已打开");
                return true;
            }

            // F2 未打开，检测是否有申请弹窗（加载中主界面也可见弹窗）
            var popupFound = ra.Find(ConfirmBtnRo).IsExist();
            if (popupFound)
            {
                _logger.LogInformation("[自动组队] 打开 F2 失败但检测到申请弹窗，处理中");
                var shouldAccept = true;
                if (whitelist != null && whitelist.Length > 0)
                {
                    var applicantName = OcrApplicantName();
                    if (!string.IsNullOrEmpty(applicantName))
                    {
                        shouldAccept = IsInWhitelist(applicantName, whitelist);
                        _logger.LogInformation("[自动组队] OCR 识别申请者: {Name}，白名单匹配: {Match}", applicantName, shouldAccept);
                    }
                    else
                    {
                        _logger.LogWarning("[自动组队] OCR 识别失败，跳过本次申请");
                        shouldAccept = false;
                    }
                }
                if (shouldAccept)
                {
                    ClickConfirmButton();
                    await Delay(300, ct);
                    ClickConfirmButton();
                    await Delay(700, ct);
                }
                else
                {
                    ClickRejectButton();
                    await Delay(500, ct);
                }
                // 处理完弹窗后继续尝试打开 F2（不计入重试次数，直接进入下一次循环）
                i--; // 不消耗重试次数
                if (i < -1) i = -1; // 防止无限递减
                continue;
            }

            _logger.LogWarning("[自动组队] 打开 F2 失败，重试 {N}/3", i + 1);
            await Delay(500, ct);
        }
        return false;
    }

    /// <summary>等待主界面出现（派蒙可见）</summary>
    private async Task<bool> WaitForMainUi(CancellationToken ct, int maxSeconds)
    {
        for (int i = 0; i < maxSeconds * 2; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var ra = CaptureToRectArea();
            if (Bv.IsInMainUi(ra))
                return true;
            await Delay(500, ct);
        }
        return false;
    }

    /// <summary>
    /// 判断成员当前是否已进入"房主世界"。
    /// 仅靠派蒙可见无法区分自己世界 / 房主世界（被拒绝、F2 切换中间帧都会让派蒙短暂可见）。
    /// 真正进入房主世界的判定条件：派蒙可见 + 处于联机状态 + 不是房主（IsHost == false）。
    ///
    /// 进入房主世界后右侧角色 HUD 渲染需要短暂时间，因此在派蒙首次可见时额外等 1.2 秒
    /// 让 HUD 稳定，再做一次截图判定，避免抓到中间帧。
    /// </summary>
    private async Task<bool> IsInHostWorldAsync(ImageRegion currentRa, CancellationToken ct)
    {
        if (!Bv.IsInMainUi(currentRa))
            return false;

        // 等待右侧 HUD 渲染稳定后再判定（避免加载完成瞬间右侧 P 图标尚未渲染）
        await Delay(1200, ct);

        try
        {
            using var stableRa = CaptureToRectArea();
            if (!Bv.IsInMainUi(stableRa))
                return false;

            var status = PartyAvatarSideIndexHelper.DetectedMultiGameStatus(
                stableRa, AutoFightAssets.Get(stableRa), NullLogger.Instance);
            return status.IsInMultiGame && !status.IsHost;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[自动组队-成员] 检测房主世界状态异常，按未进入处理");
            return false;
        }
    }

    /// <summary>
    /// 判定当前是否已回到自己单人世界。
    /// 与 <see cref="IsInHostWorldAsync"/> 同源 pattern：派蒙可见 + 1.2s HUD 稳定 +
    /// <c>DetectedMultiGameStatus.IsInMultiGame == false</c> 三重判据，避免中间帧 / 加载误判。
    /// 自己单人世界包含两种语义：
    ///   - 房主关房后回到单人（IsInMultiGame=false）
    ///   - 成员退出 / 被踢回到单人（IsInMultiGame=false）
    /// 用 false 作为强判据，比"开 F2 是否成功"这种脏代理可靠。
    /// </summary>
    private async Task<bool> IsBackInOwnWorldAsync(ImageRegion currentRa, CancellationToken ct)
    {
        if (!Bv.IsInMainUi(currentRa))
            return false;

        // 等待右侧 HUD 渲染稳定（与 IsInHostWorldAsync 同源问题）
        await Delay(1200, ct);

        try
        {
            using var stableRa = CaptureToRectArea();
            if (!Bv.IsInMainUi(stableRa))
                return false;

            // applyAuthoritativeCrossValidation: false —— 退世界检测只信纯视觉结论，
            // 绕过第 2 层协调器交叉校验。退世界阶段协调器 CurrentRoomPlayerCount 滞留旧房间人数，
            // 交叉校验会把"已回到单人世界"(IsInMultiGame=false) 误翻转为 true，导致 5 次重试全失败。
            var status = PartyAvatarSideIndexHelper.DetectedMultiGameStatus(
                stableRa, AutoFightAssets.Get(stableRa), NullLogger.Instance,
                applyAuthoritativeCrossValidation: false);
            return !status.IsInMultiGame;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[自动组队] 检测自己世界状态异常，按未回到处理");
            return false;
        }
    }

    /// <summary>模板匹配确认按钮并点击</summary>
    private void ClickConfirmButton()
    {
        try
        {
            using var ra = CaptureToRectArea();
            var found = ra.Find(ConfirmBtnRo);
            if (found.IsExist())
            {
                found.Click();
                _logger.LogInformation("[自动组队] 点击了确认/接受按钮");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[自动组队] 模板匹配确认按钮失败");
        }
    }

    /// <summary>点击拒绝按钮（接受按钮 X 轴左移 150）</summary>
    private void ClickRejectButton()
    {
        try
        {
            using var ra = CaptureToRectArea();
            var found = ra.Find(ConfirmBtnRo);
            if (found.IsExist())
            {
                // 拒绝按钮在接受按钮左边约 150 像素（1080P 坐标）
                var scale = TaskContext.Instance().SystemInfo.ScaleTo1080PRatio;
                var rejectX = found.X - (int)(150 * scale);
                var rejectY = found.Y;
                if (rejectX > 0)
                {
                    GameCaptureRegion.GameRegion1080PPosClick(rejectX / scale, rejectY / scale);
                    _logger.LogInformation("[自动组队] 点击了拒绝按钮");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[自动组队] 点击拒绝按钮失败");
        }
    }

    /// <summary>OCR 识别申请弹窗中的玩家名称（1080P 区域: x=702, y=512, w=400, h=50）</summary>
    private string OcrApplicantName()
    {
        try
        {
            using var ra = CaptureToRectArea();
            // 按 1080P 比例裁剪名称区域，x 向左偏移 10px 避免首字被截断
            var scale = TaskContext.Instance().SystemInfo.ScaleTo1080PRatio;
            var x = (int)(682 * scale);
            var y = (int)(512 * scale);
            var w = (int)(420 * scale);
            var h = (int)(50 * scale);

            // 边界检查
            if (x + w > ra.SrcMat.Width) w = ra.SrcMat.Width - x;
            if (y + h > ra.SrcMat.Height) h = ra.SrcMat.Height - y;
            if (w <= 0 || h <= 0) return "";

            using var roi = new OpenCvSharp.Mat(ra.SrcMat, new OpenCvSharp.Rect(x, y, w, h));
            var text = OcrFactory.Paddle.Ocr(roi);
            _logger.LogInformation("[自动组队] OCR 原始结果: {Text}", text);
            return text?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[自动组队] OCR 识别异常");
            return "";
        }
    }

    /// <summary>
    /// 扫描 F2 页面中各玩家名字，将不在 BGI 房间名单（同 UID 上报的成员）的玩家踢出。
    /// 流程：
    ///   1. FindMulti 拿到所有红色"踢出"按钮位置（房主自己那行没有，每个其他成员一个）
    ///   2. 按按钮 Y 坐标排序，逐行 OCR 同行玩家名（X 固定 417，Y 跟着按钮中心走）
    ///   3. 玩家名与 client.CurrentPlayerList 容错匹配，匹配失败 → 视为陌生人
    ///   4. 点对应踢出按钮 → 处理弹出的二次确认弹窗
    /// 返回三态 StrangerKickScanResult：NotFull（未满员/空名单/无按钮）、AllAllowed（满员且全为名单成员）、
    /// PendingStranger（有陌生人但连续不匹配未达阈值）、Kicked（本轮踢出一个）。
    /// 详见 .kiro/specs/hoeing-party-stranger-kick-race-nodelay-fix/design.md §改动 4。
    /// </summary>
    private async Task<StrangerKickScanResult> KickStrangersAsync(
        CoordinatorClient client,
        Dictionary<string, int> consecutiveMissByIdentity,
        int expectedCount,
        int worldCount,
        string[]? whitelist,
        CancellationToken ct)
    {
        try
        {
            // 1. 截图并定位所有踢出按钮
            using var ra = CaptureToRectArea();
            // 合法名单 = 成员加入 BGI 房间上报名单 ∪ 房间白名单（命中任一即保留）。
            // 成员可能用"思"加入房间（上报名），但游戏世界显示备注名"思姐姐"（OCR 名），
            // 若白名单同时含"思"和"思姐姐"，仅比上报名单会误踢——故并入白名单一起比。
            // 白名单为空时退回只用上报名单（现状不变）。
            var reportedNames = client.CurrentPlayerList?
                .Where(p => !string.IsNullOrEmpty(p?.PlayerName))
                .Select(p => p.PlayerName) ?? Enumerable.Empty<string>();
            var allowedNames = KickWhitelistDecisions.MergeAllowedNames(
                reportedNames, whitelist);

            // FindMulti 定位所有踢出按钮（ROI 已在 §改动 3/4 治本消除污染，天然覆盖完整右半屏 1P~4P 行）。
            var kickRegions = ra.FindMulti(AutoFightAssets.Get(ra).KickBtnRa);

            // 无踢出按钮 = F2 内除房主外无其他成员（含单人房 / 只有房主自己），世界里没有陌生人。
            //   - 已满员（如单人 期望=1，1/1）→ 返回 AllAllowed，让主循环收敛开锄；
            //   - 未满员（如期望=4 但暂时只有房主）→ 返回 NotFull 继续等人（不打满员日志，避免刷屏）。
            if (kickRegions.Count == 0)
            {
                if (StrangerKickDecisions.ShouldTriggerCompositionCheck(worldCount, expectedCount))
                {
                    _logger.LogInformation("[踢陌生人] 世界满员 {World}/{Expected} 且无其他成员，无陌生人，可开始", worldCount, expectedCount);
                    return StrangerKickScanResult.AllAllowed;
                }
                return StrangerKickScanResult.NotFull;
            }

            // 有其他成员（踢出按钮 > 0）才需要名单校验。
            // BGI 房间名单可能为空（成员还没注册），保守起见跳过踢人（Preservation 3.4）。
            if (allowedNames.Length == 0)
            {
                _logger.LogDebug("[踢陌生人] BGI 房间名单为空，跳过本次扫描");
                return StrangerKickScanResult.NotFull;
            }

            // 满员判定用外层传入的权威人数（= kickCount + 1），本方法不再自己数人数判满员
            // （消除对被污染共享 ROI 的依赖，见 design §改动 1/2）。
            // 未满员时不做校验（Preservation 3.5：未满员的陌生人等满员那一刻再踢）。
            if (!StrangerKickDecisions.ShouldTriggerCompositionCheck(worldCount, expectedCount))
            {
                _logger.LogDebug("[踢陌生人] 世界未满员 {World}/{Expected}，不触发成分校验", worldCount, expectedCount);
                return StrangerKickScanResult.NotFull;
            }

            _logger.LogInformation("[踢陌生人] 世界满员 {World}/{Expected}，开始逐行成分校验", worldCount, expectedCount);

            // 按 Y 坐标排序：从上到下对应 2P / 3P / 4P
            var sorted = kickRegions.OrderBy(r => r.Y).ToList();
            var scale = TaskContext.Instance().SystemInfo.ScaleTo1080PRatio;

            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            bool anyStranger = false;
            foreach (var btn in sorted)
            {
                ct.ThrowIfCancellationRequested();

                // OCR 同行玩家名：X 固定 417 (1080P)，宽 400，高 40
                // Y 用按钮中心反推：1080P 下名字起点 Y = 按钮中心 Y - 25 左右
                // 按钮在游戏截图中的 Y 已是当前分辨率，需要转回 1080P 再换算
                var btnCenterY1080P = (btn.Y + btn.Height / 2.0) / scale;
                var nameY1080P = btnCenterY1080P - 25;

                var nameX = (int)(PlayerNameX * scale);
                var nameY = (int)(nameY1080P * scale);
                var nameW = (int)(PlayerNameW * scale);
                var nameH = (int)(PlayerNameH * scale);

                // 边界检查
                if (nameX < 0 || nameY < 0 || nameW <= 0 || nameH <= 0) continue;
                if (nameX + nameW > ra.SrcMat.Width) nameW = ra.SrcMat.Width - nameX;
                if (nameY + nameH > ra.SrcMat.Height) nameH = ra.SrcMat.Height - nameY;
                if (nameW <= 0 || nameH <= 0) continue;

                string playerName;
                try
                {
                    using var nameRoi = new OpenCvSharp.Mat(ra.SrcMat, new OpenCvSharp.Rect(nameX, nameY, nameW, nameH));
                    playerName = (OcrFactory.Paddle.Ocr(nameRoi) ?? "").Trim();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[踢陌生人] OCR 玩家名异常，跳过此行");
                    continue;
                }

                if (string.IsNullOrEmpty(playerName))
                {
                    _logger.LogDebug("[踢陌生人] OCR 玩家名为空，跳过此行（可能渲染未稳定）");
                    continue;
                }

                // 身份归一（仅用于跨轮次连续不匹配计数归属，不参与踢不踢判定）
                var identityKey = StrangerKickDecisions.NormalizeIdentityKey(playerName);
                if (string.IsNullOrEmpty(identityKey)) continue;
                seenKeys.Add(identityKey);

                // 复用现有白名单匹配（70% 容错，支持括号备注）——踢不踢的唯一判据
                var isAllowed = IsInWhitelist(playerName, allowedNames);
                var misses = StrangerKickDecisions.NextConsecutiveMiss(
                    consecutiveMissByIdentity.GetValueOrDefault(identityKey), isAllowed);
                consecutiveMissByIdentity[identityKey] = misses;

                if (isAllowed)
                {
                    _logger.LogDebug("[踢陌生人] 玩家 [{Name}] 在 BGI 房间名单中，保留（连续不匹配计数清零）", playerName);
                    continue;
                }

                // 名字不在名单 = 疑似陌生人，但需连续 2 次不匹配才踢（吸收单帧 OCR 抽风）
                anyStranger = true;
                if (!StrangerKickDecisions.ShouldKickAfterMisses(misses))
                {
                    _logger.LogInformation("[踢陌生人] 疑似陌生人 [{Name}] 连续不匹配 {N}/{T} 次，暂不踢（吸收单帧抽风）",
                        playerName, misses, StrangerKickDecisions.DefaultMissThreshold);
                    continue;
                }

                _logger.LogWarning("[踢陌生人] 陌生人 [{Name}] 连续 {N} 次不匹配，BGI 房间名单: [{Allowed}]，踢出",
                    playerName, misses, string.Join(", ", allowedNames));

                // 点击踢出按钮中心
                btn.Click();
                await Delay(800, ct);

                // 处理"确认踢出"二次确认弹窗
                bool confirmed = false;
                for (int i = 0; i < 5; i++)
                {
                    using var confirmRa = CaptureToRectArea();
                    if (confirmRa.Find(ConfirmBtnRo).IsExist())
                    {
                        ClickConfirmButton();
                        await Delay(500, ct);
                        confirmed = true;
                        break;
                    }
                    await Delay(300, ct);
                }
                if (!confirmed)
                {
                    _logger.LogWarning("[踢陌生人] 未检测到踢出二次确认弹窗，可能踢出失败");
                }
                else
                {
                    _logger.LogInformation("[踢陌生人] 已踢出陌生人 [{Name}]", playerName);
                }

                // 踢掉后该键下轮因玩家消失会被清理，这里先移除避免残留
                consecutiveMissByIdentity.Remove(identityKey);

                // 一次只踢一个，避免按钮位置变化导致后续点错；下一轮循环会再扫
                await Delay(800, ct);
                return StrangerKickScanResult.Kicked;
            }

            // 清理本轮未出现的陈旧键（玩家已离开/被踢）：合法成员离开又回来时从 0 起算
            var staleKeys = consecutiveMissByIdentity.Keys.Where(k => !seenKeys.Contains(k)).ToList();
            foreach (var k in staleKeys) consecutiveMissByIdentity.Remove(k);

            // 满员 + 逐行校验完成：有陌生人待确认 → PendingStranger；全是自己人 → AllAllowed
            return anyStranger ? StrangerKickScanResult.PendingStranger : StrangerKickScanResult.AllAllowed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[踢陌生人] 扫描异常，忽略本次");
            return StrangerKickScanResult.NotFull;
        }
    }

    /// <summary>
    /// 检查 OCR 识别的名称是否在白名单中。
    /// 支持原始名和括号内备注名匹配，容错率 70%（允许 OCR 错 1-2 个字）。
    /// </summary>
    private static bool IsInWhitelist(string ocrText, string[] whitelist)
    {
        // 白名单条目侧与 OCR 文本侧对称拆括号，候选 × 候选笛卡尔积匹配。
        // 纯逻辑抽到 WhitelistMatchDecisions 便于单测直接引用
        // （feature: hoeing-whitelist-entry-bracket-remark-not-split-fix）。
        return WhitelistMatchDecisions.IsInWhitelist(ocrText, whitelist);
    }

    /// <summary>
    /// 退出多人游戏，回到自己的世界。
    /// 操作：确保在主界面 → 打开 F2 → 点击"离开队伍"坐标(1600,1020) → 等待加载回到自己世界
    /// 持续重试直到成功回到自己世界或超时
    /// </summary>
    public async Task<bool> LeaveWorldAsync(CancellationToken ct)
    {
        _logger.LogInformation("[自动组队] 开始退出多人游戏，回到自己的世界");

        // 最多尝试 5 次
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("[自动组队] 退出尝试 {Attempt}/5", attempt);

            // 确保在主界面
            try { await new BetterGenshinImpact.GameTask.Common.Job.ReturnMainUiTask().Start(ct); }
            catch { /* 忽略 */ }
            await Delay(500, ct);

            if (!await WaitForMainUi(ct, 10))
            {
                _logger.LogWarning("[自动组队] 退出前回到主界面失败，重试");
                continue;
            }

            // 短路检查：若已经在自己单人世界（入参即为自己世界 / 上一轮副作用已生效），直接成功返回
            using (var probeRa = CaptureToRectArea())
            {
                if (await IsBackInOwnWorldAsync(probeRa, ct))
                {
                    _logger.LogInformation("[自动组队] 当前已在自己单人世界，无需执行退出流程");
                    return true;
                }
            }

            // 打开 F2
            if (!await OpenCoOpScreen(ct))
            {
                _logger.LogWarning("[自动组队] 打开 F2 失败，重试");
                await Delay(1000, ct);
                continue;
            }

            // 点击"离开队伍/回到自己世界"按钮（1080P 坐标 1600,1020）
            _logger.LogInformation("[自动组队] 点击离开队伍按钮 (1600,1020)");
            GameCaptureRegion.GameRegion1080PPosClick(1600, 1020);

            // 处理"退回世界确认"弹窗（房主双确认 / 成员单确认），轮询 250ms × ~8s 总预算
            await ClickLeaveWorldConfirmPopupsAsync(ct);

            // 等待加载完成（最多 10 秒），见到派蒙即为回到自己世界候选
            _logger.LogInformation("[自动组队] 等待回到自己的世界...");
            if (!await WaitForMainUi(ct, 10))
            {
                _logger.LogWarning("[自动组队] 等待加载超时，重试");
                continue;
            }

            // 用 IsBackInOwnWorldAsync 复核：1.2s HUD 稳定 + IsInMultiGame == false 双重判据
            // 避免原实现"开 F2 是否成功"这种脏代理在按键被吞 / 加载中间帧时误判
            using (var verifyRa = CaptureToRectArea())
            {
                if (await IsBackInOwnWorldAsync(verifyRa, ct))
                {
                    _logger.LogInformation("[自动组队] 已成功回到自己的世界");
                    return true;
                }
            }

            _logger.LogWarning("[自动组队] 检测到仍未回到自己单人世界，继续重试");
        }

        _logger.LogError("[自动组队] 5 次尝试后仍未回到自己的世界");
        return false;
    }

    /// <summary>
    /// 给定一张已处于 F2 联机界面的截图，统计右侧红色"踢出"按钮数量（0-4）。
    /// = 仍在房主世界的其他成员数（房主自己没有按钮）。
    /// 复用 AutoPartyTask.WaitForMembersAsync 的现有扫描方式，不引入新识别模板。
    /// </summary>
    internal static int CountKickButtons(ImageRegion checkRa)
    {
        var kickCount = 0;
        // 用局部克隆改 ROI，绝不触碰共享单例 KickBtnRa（消除跨调用污染根因，见 design §改动 4）。
        for (var i = 4; i > 0; i--)
        {
            var aa = RecognitionObject.Ocr(
                checkRa.Width * 0.5,
                checkRa.Height * 0.61 - 125 * (4 - i),
                checkRa.Width * 0.5,
                checkRa.Height * 0.4 - 30);
            var probe = AutoFightAssets.Get(checkRa).KickBtnRa.Clone();
            probe.RegionOfInterest = aa.RegionOfInterest;
            if (checkRa.Find(probe).IsExist())
            {
                kickCount = i;
                break;
            }
        }
        return kickCount;
    }

    /// <summary>
    /// 房主退世界等待阶段：回主界面 → 打开 F2 → 截图 → 数踢出按钮，
    /// 返回仍在房主世界的其他成员数（0-4）。
    /// 任一步失败（未回到主界面 / 未打开 F2 / 截图异常）返回 -1（本轮不可观测）。
    /// </summary>
    public async Task<int> CountMembersRemainingInHostWorldAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // 1) 回主界面（加载中先收敛到稳定起点，对齐 Open Question 4）
            try { await new BetterGenshinImpact.GameTask.Common.Job.ReturnMainUiTask().Start(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 可恢复：本轮回主界面失败按"不可观测"处理，下一轮重试
                _logger.LogDebug(ex, "[自动组队-房主][提前退出] 回主界面失败，本轮记为不可观测");
                return -1;
            }
            await Delay(500, ct);
            if (!await WaitForMainUi(ct, 10))
            {
                _logger.LogDebug("[自动组队-房主][提前退出] 等待主界面超时，本轮记为不可观测");
                return -1;
            }

            // 2) 打开 F2（OpenCoOpScreen 内含 3 次重试 + 弹窗处理）
            if (!await OpenCoOpScreen(ct))
            {
                _logger.LogDebug("[自动组队-房主][提前退出] 打开 F2 失败，本轮记为不可观测");
                return -1;
            }

            // 3) 截图 + 计数
            using var checkRa = CaptureToRectArea();
            var kickCount = CountKickButtons(checkRa);
            _logger.LogInformation("[自动组队-房主][提前退出] F2 踢出按钮={Kick}（仍在房主世界成员数）", kickCount);
            return kickCount;
        }
        catch (OperationCanceledException)
        {
            throw; // 取消语义不吞（Requirement 5.2）
        }
        catch (Exception ex)
        {
            // 可恢复：截图/识别瞬时异常按"不可观测"处理，不中断轮询
            _logger.LogWarning(ex, "[自动组队-房主][提前退出] 检测踢出按钮异常，本轮记为不可观测");
            return -1;
        }
    }

    /// <summary>
    /// 房主退世界等待阶段（方向 B）：停留 F2 的单轮轻量检测。
    /// 仅截图 + 数踢出按钮，不每轮回主界面/重开 F2（单轮成本 ~1s）。
    /// 若发现意外掉出 F2（已回到主界面），调一次 OpenCoOpScreen 重开再截图（Open Question 3 方案 a）。
    /// 返回仍在房主世界的其他成员数（0-4）；重开仍失败 / 截图异常返回 -1（本轮不可观测）。
    /// 首轮调用时通常在主界面（编排层已回主界面建立起点），会触发一次 OpenCoOpScreen 打开 F2；
    /// 之后各轮停留 F2 走截图热路径。
    /// </summary>
    public async Task<int> CountKickButtonsInF2Async(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // 1) 截图判断当前是否仍在 F2（OpenCoOpScreen 用 !IsInMainUi 判定 F2 已打开，
            //    故 IsInMainUi == true 视为已掉出 F2）。
            using (var ra = CaptureToRectArea())
            {
                if (!Bv.IsInMainUi(ra))
                {
                    // 仍在 F2：直接数按钮（热路径，单轮 ~1s）
                    var kick = CountKickButtons(ra);
                    _logger.LogInformation("[自动组队-房主][提前退出] F2 踢出按钮={Kick}（仍在房主世界成员数）", kick);
                    return kick;
                }
            }

            // 2) 意外掉出 F2（已在主界面）：重开一次（方案 a，不回主界面，OpenCoOpScreen 内含 3 次重试 + 弹窗处理）
            _logger.LogDebug("[自动组队-房主][提前退出] 当前不在 F2（已回主界面），尝试重开 F2");
            if (!await OpenCoOpScreen(ct))
            {
                _logger.LogDebug("[自动组队-房主][提前退出] 重开 F2 失败，本轮记为不可观测");
                return -1;
            }

            // 3) 重开后重新截图 + 计数
            using var checkRa = CaptureToRectArea();
            var kickCount = CountKickButtons(checkRa);
            _logger.LogInformation("[自动组队-房主][提前退出] 重开 F2 后踢出按钮={Kick}（仍在房主世界成员数）", kickCount);
            return kickCount;
        }
        catch (OperationCanceledException)
        {
            throw; // 取消语义不吞（bugfix.md 3.3）
        }
        catch (Exception ex)
        {
            // 可恢复：截图/识别瞬时异常按"不可观测"处理，不中断轮询（task-execution-discipline.md §10）
            _logger.LogWarning(ex, "[自动组队-房主][提前退出] 停留 F2 检测踢出按钮异常，本轮记为不可观测");
            return -1;
        }
    }

    /// <summary>
    /// 在点击 F2 上的"离开队伍"按钮 (1600,1020) 之后，处理可能出现的"退回世界确认"弹窗。
    /// 实现方式：250ms 节拍轮询 ConfirmBtnRo，分三阶段：
    ///   1) 第一段轮询（最多 5s）：找到第一弹窗 → 点击
    ///   2) 等弹窗消失（最多 1.5s）：等到 Find miss 才进入下一段（避免误点同一弹窗两次）
    ///   3) 第二段轮询（最多 5s）：找到第二弹窗 → 点击；超时未出现视为成员单确认场景，静默退出
    /// 整段总预算 ~8s。任何阶段超总预算后立即退出，不阻塞外层 5 轮重试。
    /// </summary>
    private async Task ClickLeaveWorldConfirmPopupsAsync(CancellationToken ct)
    {
        var phase = LeaveWorldConfirmPopupPhase.FirstPolling;
        var phaseStart = Environment.TickCount;
        var totalStart = Environment.TickCount;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var elapsedInPhase = Environment.TickCount - phaseStart;
            var totalElapsed = Environment.TickCount - totalStart;

            bool popupVisible;
            using (var ra = CaptureToRectArea())
            {
                popupVisible = ra.Find(ConfirmBtnRo).IsExist();
            }

            var decision = LeaveWorldConfirmPopupDecisions.Decide(phase, elapsedInPhase, totalElapsed, popupVisible);

            switch (decision)
            {
                case LeaveWorldConfirmPopupTickDecision.ClickFirstConfirm:
                    _logger.LogInformation("[自动组队] 第一弹窗出现，点击确认（首次）");
                    ClickConfirmButton();
                    phase = LeaveWorldConfirmPopupPhase.WaitingForFirstGone;
                    phaseStart = Environment.TickCount;
                    break;

                case LeaveWorldConfirmPopupTickDecision.WaitFirstPopupGone:
                    // 继续等
                    break;

                case LeaveWorldConfirmPopupTickDecision.EnterSecondPolling:
                    phase = LeaveWorldConfirmPopupPhase.SecondPolling;
                    phaseStart = Environment.TickCount;
                    break;

                case LeaveWorldConfirmPopupTickDecision.ClickSecondConfirm:
                    _logger.LogInformation("[自动组队] 第二弹窗出现，点击确认（二次）");
                    ClickConfirmButton();
                    return;

                case LeaveWorldConfirmPopupTickDecision.Idle:
                    // 当前 tick 无事可做
                    break;

                case LeaveWorldConfirmPopupTickDecision.SecondPollingTimeoutNoFault:
                    _logger.LogDebug("[自动组队] 第二弹窗未出现（成员单确认场景），结束确认段");
                    return;

                case LeaveWorldConfirmPopupTickDecision.SegmentTimeout:
                    _logger.LogWarning("[自动组队] 确认弹窗段超时 ({TotalMs}ms)，交回主循环由复核判据决定后续",
                        totalElapsed);
                    return;

                case LeaveWorldConfirmPopupTickDecision.BothConfirmsClicked:
                    return;
            }

            await Delay(LeaveWorldConfirmPopupDecisions.TickIntervalMs, ct);
        }
    }

    /// <summary>模糊匹配：两个字符串的相同字符比例 >= threshold</summary>
    private static bool FuzzyMatch(string a, string b, double threshold)
    {
        if (a == b) return true;
        var longer = a.Length >= b.Length ? a : b;
        var shorter = a.Length >= b.Length ? b : a;
        if (longer.Length == 0) return false;

        int matchCount = 0;
        var used = new bool[longer.Length];
        foreach (var c in shorter)
        {
            for (int i = 0; i < longer.Length; i++)
            {
                if (!used[i] && longer[i] == c)
                {
                    used[i] = true;
                    matchCount++;
                    break;
                }
            }
        }

        var ratio = (double)matchCount / longer.Length;
        return ratio >= threshold;
    }
}
