namespace BetterGenshinImpact.Helpers;

/// <summary>
/// 敏感文本脱敏（R4.6 ASTRA 三轮 I3 处置）：UID 等账号标识的日志/持久化统一出口。
/// 规则：null/空 → 占位；长度 ≤5 全遮盖（前三后二在 5 位 UID 下仍全露）；其余 前3***后2。
/// 绑定码等密钥类不适用本函数——一律不得输出到任何日志/持久化面。
/// </summary>
public static class SensitiveTextMask
{
    public static string MaskUid(string? uid)
    {
        if (string.IsNullOrEmpty(uid)) return "(空)";
        if (uid.Length <= 5) return "***";
        return uid[..3] + "***" + uid[^2..];
    }
}
