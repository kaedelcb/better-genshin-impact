using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Service.Execution;

namespace BetterGenshinImpact.Service.OneDragon;

/// <summary>任务中心账号前置的 UID 核对顺序；纯调度逻辑，不触碰游戏或剪贴板。</summary>
internal static class AccountUidSequence
{
    // 旧茶包版每个账号最多验证三次，最多切换两次；匹配即停止，不制造第二次复制点击。
    internal static async Task<bool> EnsureAsync(
        Func<CancellationToken, Task<OneDragonAccountCapability.UidVerifyResult>> verify,
        Func<int, CancellationToken, Task<bool>> switchAccount, CancellationToken ct)
    {
        OneDragonAccountCapability.UidVerifyResult? last = null;
        for (var switchCount = 0; switchCount <= 2; switchCount++)
        {
            for (var verifyCount = 0; verifyCount < 3; verifyCount++)
            {
                ct.ThrowIfCancellationRequested();
                last = await verify(ct);
                if (last.Matched) return switchCount > 0;
                if (last.Status == OneDragonAccountCapability.UidVerifyStatus.InvalidExpected)
                    throw new AccountMismatchException(last.Reason ?? "期望 UID 无效");
            }

            if (switchCount == 2) break;
            // 切号结果可能不明；此时不执行下一次切号，保留 Unknown 供人工核对。
            if (!await switchAccount(switchCount + 1, ct))
                throw new PrerequisiteFailedException(JobErrorCodes.PrerequisiteOutcomeUnknown,
                    "切号完成确认失败（结果不明，不自动重试）");
        }

        if (last?.Status == OneDragonAccountCapability.UidVerifyStatus.ReadFailed)
            throw new PrerequisiteFailedException(JobErrorCodes.PrerequisiteOutcomeUnknown,
                "UID 读取失败，切号后仍不能确认当前账号");
        throw new AccountMismatchException(last?.Reason ?? "切号后 UID 仍与流程声明不符");
    }
}
