using BetterGenshinImpact.Service.Instance;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Service.Execution;

/// <summary>
/// 整组配置写入合同（R2.2，2026-09-18）。
/// 远程整组编辑（config.apply_group / ext.config.applyGroup）的 revision 验证此前是"携带才校验"的可选语义，
/// 编辑期间文件被第三方改动时会静默覆盖。本合同把 revision 守卫升级为声明式强制：
/// - 请求显式声明写入合同（configWriteContract:1 或 executionContractVersion:1）→ expectedConfigRevision 必须携带且匹配；
/// - 未声明合同的旧请求 → 保持可选（兼容清单条目 W2，见 Docs/design/onedragon-r2-compat-matrix-2026-09-18.md），
///   携带时仍强制校验（既有行为不变）。
/// 版本互操作：新助手只在 pull 响应含 configRevision（新 BGI）时才声明合同；旧 BGI/旧助手组合走兼容路径，
/// 不会出现"新 BGI 拒绝旧助手"的跨版本断裂。
/// </summary>
internal static class GroupConfigWriteContract
{
    /// <summary>当前整组写入合同版本。</summary>
    public const int CurrentVersion = 1;

    /// <summary>请求是否声明了整组写入合同（configWriteContract:1 或 executionContractVersion:1）。</summary>
    public static bool IsDeclared(InstanceIpcEnvelope request)
    {
        var data = request.Data;
        return data?["configWriteContract"] is { } writeContract && writeContract.Value<int?>() == CurrentVersion
               || data?["executionContractVersion"] is { } execContract && execContract.Value<int?>() == 1;
    }

    /// <summary>
    /// 声明合同的整组写入必须携带 expectedConfigRevision；缺失即拒绝（不写盘）。
    /// 未声明的旧请求放行（由 ApplyRemoteGroup 内既有"携带才校验"逻辑兜底）。
    /// </summary>
    public static InstanceIpcEnvelope? ValidateApplyGroup(InstanceIpcEnvelope request)
    {
        if (!IsDeclared(request))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(InstanceIpcProtocol.GetStringOrNull(request.Data, "expectedConfigRevision")))
        {
            return InstanceIpcEnvelope.Failure(request, "invalid_request",
                "整组写入合同要求 expectedConfigRevision（先经 config.pull_group 取得当前修订再应用）");
        }

        return null;
    }

    /// <summary>
    /// 组名合法性守卫：pull_group/apply_group 的 groupName 直接参与拼 User/ScriptGroup 路径，
    /// 必须拒绝路径穿越与非法文件名字符（ASTRA 会诊安全项："..\" 可读到 ScriptGroup 外的任意 JSON）。
    /// </summary>
    public static bool IsValidGroupName(string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return false;
        }

        if (groupName is "." or "..")
        {
            return false;
        }

        return groupName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0
               && groupName.IndexOf('/') < 0
               && groupName.IndexOf('\\') < 0;
    }
}