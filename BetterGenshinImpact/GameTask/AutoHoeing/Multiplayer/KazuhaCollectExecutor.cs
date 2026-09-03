using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 万叶聚物动作的阶段标记，由 <see cref="KazuhaCollectExecutor.RunAsync"/> 通过 onProgress 回调
/// 透出，供调用方做日志/事件穿透。
/// </summary>
public enum KazuhaCollectStage
{
    Started,
    Switched,
    SkillCdReady,           // 自然就绪
    SkillCdTimeoutForce,    // 超时直放（区别于自然就绪）
    SkillReleaseConfirmed,
    PlungeAttackDone,
    PickupWaitDone,
    SkillReleaseRetried,    // EB2 重试块开始（双判失败 → 重试一次）
}

/// <summary>
/// 万叶聚物动作执行器（共享 helper）。
/// 不引用 SignalR、不依赖 MultiplayerCoordinator，纯执行 + 视觉验证。
/// 单机版（AutoLeyLineOutcropTask）与联机版（KazuhaCollectSyncCoordinator）共用。
/// </summary>
public static class KazuhaCollectExecutor
{
    /// <summary>
    /// 聚物动作的最终结果。
    /// SkipReason 取值：team_no_kazuha / switch_failed / e_skill_not_released / null（成功时）。
    /// </summary>
    public sealed record Outcome(bool Success, string? SkipReason);

    /// <summary>
    /// 执行万叶聚物动作序列：选万叶 → 切人 → 等 E 技 CD（带上限超时不算失败）→ 长 E（UseSkill）
    /// → AvatarSkillAsync 释放确认（未放出走重试三件套）→ 6 次下落攻击 → 拾取等待。
    /// </summary>
    /// <param name="combatScenes">已识别好的队伍场景</param>
    /// <param name="waitSkillCdSeconds">等 E 技 CD 上限秒数（超时按 SkillCdTimeoutForce 直放，由 OCR + 视觉双判决定成败）</param>
    /// <param name="postPickupWaitMs">下落攻击完成后的拾取等待毫秒（默认 3000ms）</param>
    /// <param name="onProgress">可选阶段回调，供联机版穿透阶段日志/事件</param>
    /// <param name="assumeAlreadySwitched">
    /// 联机快速路径开关：true 时跳过开头 SelectAvatar + Delay(200) + TrySwitch（已由 BeginPreparationAsync 完成预切人）。
    /// 必须同时传 <paramref name="preselectedKazuha"/> 非 null，否则 ArgumentException。默认 false 保持单机原行为。
    /// </param>
    /// <param name="preselectedKazuha">
    /// 预切好的万叶 Avatar 引用，配合 <paramref name="assumeAlreadySwitched"/>=true 使用，用于 WaitSkillCd / AfterUseSkill。
    /// </param>
    /// <param name="ct">取消令牌；触发取消时 finally 仍会释放按键，并向上抛 OperationCanceledException</param>
    public static async Task<Outcome> RunAsync(
        CombatScenes combatScenes,
        int waitSkillCdSeconds,
        int postPickupWaitMs = 3000,
        Action<KazuhaCollectStage>? onProgress = null,
        bool assumeAlreadySwitched = false,
        Avatar? preselectedKazuha = null,
        Func<CancellationToken, Task>? onBeforeHoldE = null,
        CancellationToken ct = default)
    {
        ValidateFastPathArgs(assumeAlreadySwitched, preselectedKazuha);

        try
        {
            Avatar? kazuha;
            if (assumeAlreadySwitched)
            {
                // 快速路径：调用方（KazuhaCollectSyncCoordinator.BeginPreparationAsync）
                // 已完成 SelectAvatar + Delay(200) + TrySwitch；此处直接复用 kazuha 引用，
                // 跳过单机原开头切人序列（节省 ~200ms+ 切人 Delay）。
                kazuha = preselectedKazuha;
                onProgress?.Invoke(KazuhaCollectStage.Started);
                onProgress?.Invoke(KazuhaCollectStage.Switched);
            }
            else
            {
                // 原路径：单机 (AutoLeyLineOutcropTask) 调用方 / 联机兜底分支走这里
                kazuha = combatScenes.SelectAvatar("枫原万叶");
                if (kazuha == null)
                {
                    return new Outcome(false, "team_no_kazuha");
                }

                onProgress?.Invoke(KazuhaCollectStage.Started);

                await Delay(200, ct);

                if (!kazuha.TrySwitch(10))
                {
                    return new Outcome(false, "switch_failed");
                }

                onProgress?.Invoke(KazuhaCollectStage.Switched);
            }

            // 判 CD 前临场稳定（对齐 AutoFightTask.cs L1820-1827）：
            // 切人 + 等画面稳，避免切人/落地动画期间截帧导致 E 图标连通块偏少被误判"就绪"。
            kazuha!.TrySwitch(20);
            await Delay(100, ct);
            using (var raActive = CaptureToRectArea())
            {
                if (!kazuha.IsActive(raActive))
                {
                    // TrySwitch 失败兜底：万叶未真正出战时再切一次，避免被误判为就绪
                    kazuha.TrySwitch(20);
                    await Delay(50, ct);
                }
            }

            // 视觉先判 → CD 中走 WaitSkillCd(ct) 无上限（对齐 AutoFightTask.cs L1822 万叶长 E 拾取样板）。
            // 视觉就绪（AvatarSkillAsync 返回 false）→ 立即触发 SkillCdReady，跳过等待；
            // 视觉看到 CD（AvatarSkillAsync 返回 true）→ 等到 E 的 CD 真正结束再放。
            // retryCount=1：内部不重试只判一次；isResetCd=false：不动 ManualSkillCd 状态机；
            // needLog=true：日志含 CD 状态。

            // [诊断] 判 CD 前记录万叶是否真正出战，排查"切人未完成导致连通块检测假阴性"
            using (var raDiag = CaptureToRectArea())
            {
                Logger.LogInformation("[聚物][诊断] 判CD前: 万叶出战 IsActive={IsActive}, Index={Index}",
                    kazuha!.IsActive(raDiag), kazuha.Index);
            }

            var cdJudgedInCd = await AutoFightSkill.AvatarSkillAsync(
                Logger, kazuha!,
                skills: false, retryCount: 1, ct: ct,
                image: null, needLog: true, isResetCd: false);
            Logger.LogInformation("[聚物][诊断] 判CD结果: connectivityInCd={InCd} → 分支={Branch}",
                cdJudgedInCd, cdJudgedInCd ? "在CD→WaitSkillCd等待" : "就绪→立即放行");

            if (cdJudgedInCd)
            {
                // 残留 CD 修复（kazuha-collect-residual-cd-empty-cast-fix）：
                // 不再用基于时间戳的 WaitSkillCd（残留 CD 场景误判已就绪 → 1ms 返回 → 空放）。
                // 改为 OCR 读屏残留秒数等待 + 100ms/1s 视觉就绪轮询，等待时长贴合屏幕真实 CD。
                // 取消透传：WaitForSkillReadyAsync 内 Delay/AvatarSkillAsync 收 ct，
                // 取消时抛 OperationCanceledException，由 finally 释放按键（bugfix 2.5）。
                await WaitForSkillReadyAsync(kazuha!, waitSkillCdSeconds, ct);
                onProgress?.Invoke(KazuhaCollectStage.SkillCdReady);
            }
            else
            {
                // 视觉就绪 → 立即放行，与原"自然就绪"路径阶段语义保持一致
                onProgress?.Invoke(KazuhaCollectStage.SkillCdReady);
            }

            // multiplayer-kazuha-collect-point-broadcast: HoldE 起手前注入 hook，
            // 联机分支用此 hook 算 (collectX, collectY) 并 fire-and-forget 上报；
            // 单机调用方不传即跳过，行为零差异（任何异常透传 OperationCanceledException
            // 否则吞掉，不让 hook 抛出打断 HoldE 序列）。
            if (onBeforeHoldE != null)
            {
                try { await onBeforeHoldE(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "[联机][聚物] onBeforeHoldE hook 抛异常，已忽略并继续 HoldE");
                }
            }

            // await SimulateHoldElementalSkillAsync(1000, ct);
            // await Delay(200, ct);
            // Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
            // 放长 E：对齐 AutoFightTask.cs L1819-1821 万叶拾取样板，用内建 UseSkill(true) +
            // NormalAttack 触发下落，复用 UseSkill 内建的"放完读 CD 确认 + 自动重试"闭环。
            kazuha!.UseSkill(true);
            await Delay(50, ct);
            Simulation.SendInput.SimulateAction(GIActions.NormalAttack);

            // 释放确认①：对齐原版单判（AutoFightTask.cs L1823）——
            // AvatarSkillAsync 返回 false = E 不在 CD = 没放出 → 重试三件套。
            // 使用 ReleaseConfirmRetryCount 轮询检测：首帧即识别到冷却时立即返回，
            // 不增加正常机器耗时；仅对识别较慢的机器扩大约 400ms 检测窗口。
            var releasedInCd = await AutoFightSkill.AvatarSkillAsync(
                Logger, kazuha!, skills: false, retryCount: ReleaseConfirmRetryCount, ct: ct,
                image: null, needLog: true, isResetCd: false);
            Logger.LogInformation("[聚物][诊断] 释放确认①: releasedInCd={InCd} → {Branch}",
                releasedInCd, releasedInCd ? "已放出(E在CD)" : "未放出→重试三件套");

            if (!releasedInCd)
            {
                // 对齐 AutoFightTask.cs L1824-1834：UseSkill + Hold(800) + 6 连点下落攻击。
                Logger.LogWarning("万叶长E技能未成功释放，尝试再次释放");
                onProgress?.Invoke(KazuhaCollectStage.SkillReleaseRetried);

                kazuha.TrySwitch(20);
                await Delay(50, ct);
                kazuha.UseSkill(true);
                await Delay(50, ct);
                await SimulateHoldElementalSkillAsync(800, ct);
                await SimulateMouseLeftClickLoopAsync(6, ct);

                // 释放确认②（重试后）：联机降级上报路径，原样保留——
                // 重试后仍未放出（AvatarSkillAsync 返回 false）→ return Outcome 联机广播 Skipped。
                // 二次确认同样使用 ReleaseConfirmRetryCount 轮询，给识别慢的机器留足检测窗口。
                var releasedInCd2 = await AutoFightSkill.AvatarSkillAsync(
                    Logger, kazuha, skills: false, retryCount: ReleaseConfirmRetryCount, ct: ct,
                    image: null, needLog: true, isResetCd: false);
                Logger.LogInformation("[聚物][诊断] 释放确认②(重试后): releasedInCd2={InCd} → {Branch}",
                    releasedInCd2, releasedInCd2 ? "已放出(E在CD)" : "仍未放出→Outcome(false, e_skill_not_released)");

                if (!releasedInCd2)
                {
                    // 重试仍失败 → 保持原 reason 码语义不变（联机广播 Skipped）
                    return new Outcome(false, "e_skill_not_released");
                }
            }

            // 标记 E 技 CD（对齐原版 L1846 picker.AfterUseSkill()）。
            kazuha.AfterUseSkill();

            onProgress?.Invoke(KazuhaCollectStage.SkillReleaseConfirmed);

            await SimulateMouseLeftClickLoopAsync(6, ct);
            await Delay(1500, ct);
            onProgress?.Invoke(KazuhaCollectStage.PlungeAttackDone);

            await Delay(postPickupWaitMs, ct);
            onProgress?.Invoke(KazuhaCollectStage.PickupWaitDone);

            return new Outcome(true, null);
        }
        finally
        {
            // BC4 / requirements 3.10：所有路径（含取消、异常、提前 return）都要释放按键
            try
            {
                Simulation.SendInput.Mouse.LeftButtonUp();
            }
            catch
            {
                // finally 内吞掉，避免遮蔽原始异常
            }

            try
            {
                Simulation.ReleaseAllKey();
            }
            catch
            {
                // 同上
            }
        }
    }

    /// <summary>
    /// 联机快速路径参数校验：assumeAlreadySwitched=true 时必须同时传 preselectedKazuha 非 null。
    /// 抽出 public 方法是为了 PBT-4 跨项目直接覆盖参数组合，无需构造完整 CombatScenes / Avatar。
    /// 详见 design.md §6 PBT-4。
    /// </summary>
    public static void ValidateFastPathArgs(bool assumeAlreadySwitched, Avatar? preselectedKazuha)
    {
        if (assumeAlreadySwitched && preselectedKazuha == null)
        {
            throw new ArgumentException(
                "assumeAlreadySwitched=true 时必须传 preselectedKazuha（联机快速路径需要 kazuha 引用用于 WaitSkillCd / AfterUseSkill）",
                nameof(preselectedKazuha));
        }
    }

    /// <summary>
    /// 视觉就绪轮询的间隔与上限常量（kazuha-collect-residual-cd-empty-cast-fix design §Fix Implementation）。
    /// </summary>
    public const int PollIntervalMs = 100;
    public const int PollTimeoutMs = 1000;

    /// <summary>
    /// 残留 CD 总等待硬顶（kazuha-collect-residual-cd-tail-poll-fix）：万叶长 E CD 为 9s，留 3s 余量取 12s。
    /// 仅用于"OCR 明确指示残留超过盲等上限"时尾段视觉轮询的封顶，防 OCR 读出巨大值死等。
    /// </summary>
    public const int HardCeilingMs = 12000;

    /// <summary>
    /// E 技能释放后的冷却检测重试次数。
    /// 正常机器首帧即可识别到 cooldown（numLabels2 &gt; 2），立即返回；
    /// 部分机器截图/渲染较慢，首帧未出现冷却白块，通过多次 100ms 间隔轮询扩大检测窗口，
    /// 避免误判为"未释放"而进入不必要的重试三件套。
    /// retryCount=5 对应最多 4 次 100ms 等待，即额外约 400ms 检测窗口。
    /// </summary>
    public const int ReleaseConfirmRetryCount = 5;

    /// <summary>
    /// 【纯函数】根据 OCR 读到的残留 CD 秒数与上限封顶，计算秒数等待的毫秒数。
    /// ocrSeconds &lt;= 0（读不到有效秒数）→ 返回 0，跳过秒数等待直接进视觉轮询（绝不当作无 CD）。
    /// ocrSeconds &gt; 0 → 返回 min(ocrSeconds, capSeconds) * 1000，防 OCR 读出巨大值死等。
    /// </summary>
    /// <param name="ocrSeconds">OCR 读到的剩余 CD 秒数</param>
    /// <param name="capSeconds">上限封顶秒数（即 waitSkillCdSeconds）</param>
    public static int ComputeOcrWaitMs(double ocrSeconds, int capSeconds)
    {
        if (ocrSeconds <= 0) return 0;
        var cap = capSeconds < 0 ? 0 : capSeconds;
        var capped = Math.Min(ocrSeconds, cap);
        if (capped <= 0) return 0;
        return (int)Math.Ceiling(capped * 1000);
    }

    /// <summary>
    /// 【纯函数】尾段视觉就绪轮询的超时上限（kazuha-collect-residual-cd-tail-poll-fix）。
    /// 背景缺陷：盲等被封顶 capSeconds（默认 5s）截断后，旧代码只轮询 1s 就兜底放 E，
    /// 而万叶长 E CD 为 9s，残留 6~9s 时 E 仍在 CD → 空放，且释放确认看到"E 在 CD"误判"已放出"（假成功）。
    /// 语义：ocrSeconds &lt;= 0（OCR 无效）→ 返回 PollTimeoutMs(1s)，与旧行为逐字节一致；
    /// OCR 有效且指示残留超过已盲等部分 → 轮询覆盖剩余残留（每次视觉判定就绪即提前退出，不超等），
    /// 总等待硬顶 <see cref="HardCeilingMs"/>，防 OCR 巨大值死等。
    /// </summary>
    /// <param name="ocrSeconds">OCR 读到的剩余 CD 秒数</param>
    /// <param name="cappedWaitMs">已执行的盲等毫秒数（ComputeOcrWaitMs 的返回值）</param>
    public static int ComputeVisualPollTimeoutMs(double ocrSeconds, int cappedWaitMs)
    {
        if (ocrSeconds <= 0) return PollTimeoutMs;
        var remainingMs = (int)Math.Ceiling(ocrSeconds * 1000) - cappedWaitMs;
        if (remainingMs <= 0) return PollTimeoutMs;
        var budgetMs = HardCeilingMs - cappedWaitMs;
        if (budgetMs < PollTimeoutMs) budgetMs = PollTimeoutMs;
        return Math.Min(remainingMs, budgetMs);
    }

    /// <summary>
    /// 【纯函数】视觉就绪轮询的停止判定：视觉就绪 或 已达超时上限 → 停止。
    /// </summary>
    /// <param name="visualReady">最近一次 AvatarSkillAsync 是否返回就绪（false=就绪 → 传入 true）</param>
    /// <param name="elapsedMs">轮询已耗时毫秒</param>
    /// <param name="timeoutMs">轮询超时上限毫秒</param>
    public static bool ShouldStopPolling(bool visualReady, int elapsedMs, int timeoutMs)
    {
        return visualReady || elapsedMs >= timeoutMs;
    }

    /// <summary>
    /// 残留 CD 等待环节（替代基于时间戳的 Avatar.WaitSkillCd）：
    /// 1) OCR 读 E 图标剩余秒数 → 等 ComputeOcrWaitMs 决定的时长（封顶 capSeconds）；
    /// 2) 读不到秒数 → 跳过秒数等待直接进轮询（不当作无 CD），轮询上限 1s（旧行为不变）；
    /// 3) 视觉就绪轮询：PollIntervalMs 间隔；上限由 ComputeVisualPollTimeoutMs 决定——
    ///    OCR 指示残留超过盲等部分时覆盖剩余残留（就绪即提前退出），否则 1s；
    /// 4) 轮询超时仍未就绪 → 直接放行（兜底）+ LogWarning。
    /// 取消透传：所有 Delay / AvatarSkillAsync 接收 ct，取消时抛 OperationCanceledException 向上传播。
    /// kazuha-collect-residual-cd-empty-cast-fix 改动 2；kazuha-collect-residual-cd-tail-poll-fix 改动 1。
    /// </summary>
    private static async Task WaitForSkillReadyAsync(Avatar kazuha, int capSeconds, CancellationToken ct)
    {
        // 1) OCR 读残留秒数（无副作用读屏）
        double ocrSeconds;
        using (var raOcr = CaptureToRectArea())
        {
            ocrSeconds = kazuha.ReadSkillCdSecondsByOcr(raOcr);
        }

        var ocrWaitMs = ComputeOcrWaitMs(ocrSeconds, capSeconds);
        Logger.LogInformation("[聚物][等CD] OCR 读残留 CD={Ocr}s，封顶={Cap}s → 秒数等待 {WaitMs}ms",
            Math.Round(ocrSeconds, 2), capSeconds, ocrWaitMs);

        if (ocrWaitMs > 0)
        {
            await Delay(ocrWaitMs, ct);
        }

        // 3) 视觉就绪轮询（100ms 间隔；OCR 指示残留未走完时覆盖剩余残留，否则 1s 上限）
        var pollTimeoutMs = ComputeVisualPollTimeoutMs(ocrSeconds, ocrWaitMs);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var inCd = await AutoFightSkill.AvatarSkillAsync(
                Logger, kazuha, skills: false, retryCount: 1, ct: ct,
                image: null, needLog: false, isResetCd: false);
            var visualReady = !inCd; // AvatarSkillAsync 返回 false = 就绪
            var elapsedMs = (int)sw.ElapsedMilliseconds;

            if (ShouldStopPolling(visualReady, elapsedMs, pollTimeoutMs))
            {
                if (!visualReady)
                {
                    // 4) 超时兜底放行：仅当 OCR 无效或视觉判定持续异常时才可能走到这里
                    Logger.LogWarning("[聚物][等CD] 视觉就绪轮询 {Timeout}ms 超时仍未就绪，兜底放 E（OCR 残留={Ocr}s，已盲等 {WaitMs}ms）",
                        pollTimeoutMs, Math.Round(ocrSeconds, 2), ocrWaitMs);
                }
                else
                {
                    Logger.LogInformation("[聚物][等CD] 视觉就绪（轮询 {Elapsed}ms / 上限 {Timeout}ms）→ 放行", elapsedMs, pollTimeoutMs);
                }
                break;
            }

            await Delay(PollIntervalMs, ct);
        }
    }
}
