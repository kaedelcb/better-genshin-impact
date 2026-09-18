using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Service.Execution;

namespace BetterGenshinImpact.Service.OneDragon;

/// <summary>
/// R4.6 E2-9 期望 UID 闸门：在执行权已取得（ExecutionScope 已建立）、首个资源副作用之前复验当前账号。
/// 不符即抛 <see cref="AccountMismatchException"/>（作业终态 account_mismatch）；expectedUid 为空 = 不校验。
/// 诚实边界：闸门覆盖受协调入口（ext/恢复/页面启动的描述符路径），直接人工操作游戏不在保护边界内。
/// </summary>
internal static class ExpectedUidGate
{
    public static async Task VerifyOrThrowAsync(string? expectedUid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedUid)) return;
        // I6：每次校验新建能力实例，临时状态不跨执行共享
        var result = await new OneDragonAccountCapability().VerifyUidStrictAsync(expectedUid, ct);
        if (!result.Matched)
            throw new AccountMismatchException(result.Reason ?? "当前账号 UID 与期望不符");
    }
}
