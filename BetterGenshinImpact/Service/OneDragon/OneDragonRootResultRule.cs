using BetterGenshinImpact.GameTask;

namespace BetterGenshinImpact.Service.OneDragon;

/// <summary>
/// E9 根/叶结果合同（R4.6 ASTRA 三轮 B7 处置，InternalsVisibleTo 可测）：
/// 整龙跑完且仅有正常跳过（Skipped）→ 根结果按完成标记判定，成功即根作业 Succeeded，
/// 子作业在注册表各自携带 Skipped 分别表达（D15）；失败/取消/抢占/拒绝原样透传。
/// </summary>
internal static class OneDragonRootResultRule
{
    /// <summary>根结果判定：Ran/Skipped 进入完成判定（尾部跑完=成功）；其他结果原样透传。</summary>
    public static TaskRunResult DecideRootResult(TaskRunResult scopeResult, bool finishMark)
        => scopeResult is TaskRunResult.Ran or TaskRunResult.Skipped
            ? finishMark ? TaskRunResult.Ran : TaskRunResult.Failed
            : scopeResult;

    /// <summary>尾部闸门：仅正常跳过不阻断尾部（CheckRewards/通知/CompletionAction 照常，D15）。</summary>
    public static bool ShouldRunTail(TaskRunResult scopeResult)
        => scopeResult is TaskRunResult.Ran or TaskRunResult.Skipped;
}
