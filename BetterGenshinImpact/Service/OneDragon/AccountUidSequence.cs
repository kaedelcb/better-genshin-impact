using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Service.Execution;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.OneDragon;

/// <summary>任务中心账号前置的 UID 核对顺序；纯调度逻辑，不触碰游戏或剪贴板。</summary>
internal static class AccountUidSequence
{
    // 连续三次可靠不符才切号；常规核验最多五次，末尾的不符序列最多补两次，最多切换两次。
    internal static async Task<bool> EnsureAsync(
        Func<CancellationToken, Task<OneDragonAccountCapability.UidVerifyResult>> verify,
        Func<int, CancellationToken, Task<bool>> switchAccount, CancellationToken ct, ILogger? logger = null)
    {
        OneDragonAccountCapability.UidVerifyResult? last = null;
        // 跨全部核对和切号累计；本次失焦不能抹掉之前已读取 UID 或已操作游戏的事实。
        var allPreparationsNotStarted = true;
        for (var switchCount = 0; switchCount <= 2; switchCount++)
        {
            var consecutiveMismatches = 0;
            var verifyLimit = 3;
            var completingMismatchStreak = false;
            OneDragonAccountCapability.UidVerifyResult? uncertain = null;
            for (var verifyCount = 0; verifyCount < verifyLimit; verifyCount++)
            {
                ct.ThrowIfCancellationRequested();
                last = await verify(ct);
                ct.ThrowIfCancellationRequested();
                allPreparationsNotStarted &= last.Status == OneDragonAccountCapability.UidVerifyStatus.PreparationNotStarted;
                if (last.Status == OneDragonAccountCapability.UidVerifyStatus.Mismatch)
                    consecutiveMismatches++;
                else
                {
                    consecutiveMismatches = 0;
                    if (!last.Matched && (uncertain is null || string.IsNullOrWhiteSpace(uncertain.Reason) ||
                        (last.Status == OneDragonAccountCapability.UidVerifyStatus.ReadFailed &&
                         uncertain.Status != OneDragonAccountCapability.UidVerifyStatus.ReadFailed)))
                        uncertain = last;
                }
                // 未取得焦点的 PreparationNotStarted 不扩充预算；不能因重试抹掉已操作游戏的事实。
                if (!completingMismatchStreak && last.Status == OneDragonAccountCapability.UidVerifyStatus.ReadFailed)
                    verifyLimit = 5;
                logger?.LogDebug("账号 UID 核验：切号次数 {SwitchCount}，本轮第 {Attempt}/{Limit} 次，状态 {Status}，连续不符 {Mismatches}，原因 {Reason}",
                    switchCount, verifyCount + 1, verifyLimit, last.Status, consecutiveMismatches, last.Reason);
                if (last.Matched) return switchCount > 0;
                if (last.Status == OneDragonAccountCapability.UidVerifyStatus.InvalidExpected)
                    throw new AccountMismatchException(last.Reason ?? "期望 UID 无效");
                if (consecutiveMismatches == 3) break;
                // 最后一次才读到不符时，允许把当前连续证据补齐；补读一旦失败便停止，不滚动续期。
                if (completingMismatchStreak && consecutiveMismatches == 0) break;
                if (!completingMismatchStreak && verifyCount + 1 == verifyLimit && consecutiveMismatches > 0)
                {
                    completingMismatchStreak = true;
                    verifyLimit += 3 - consecutiveMismatches;
                    logger?.LogDebug("UID 已恢复读取，继续确认连续不符；本轮核验上限延长至 {Limit} 次", verifyLimit);
                }
            }

            // 不跨读取/准备失败累加旧证据；补读耗尽仍须保持 Unknown，不能绕过账号核验。
            if (consecutiveMismatches < 3)
            {
                if (allPreparationsNotStarted && switchCount == 0)
                    throw new PrerequisiteFailedException(JobErrorCodes.AccountPreparationNotStarted,
                        last?.Reason ?? "账号前置尚未取得游戏焦点，未发送游戏键鼠");
                throw new PrerequisiteFailedException(JobErrorCodes.PrerequisiteOutcomeUnknown,
                    uncertain?.Reason ?? "UID 未能连续三次可靠核对，不切换账号");
            }
            if (switchCount == 2) break;
            // 切号结果可能不明；此时不执行下一次切号，保留 Unknown 供人工核对。
            allPreparationsNotStarted = false;
            ct.ThrowIfCancellationRequested();
            if (!await switchAccount(switchCount + 1, ct))
                throw new PrerequisiteFailedException(JobErrorCodes.PrerequisiteOutcomeUnknown,
                    "切号完成确认失败（结果不明，不自动重试）");
        }

        throw new AccountMismatchException(last?.Reason ?? "切号后 UID 仍与流程声明不符");
    }
}
