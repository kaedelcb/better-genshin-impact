#nullable enable
using System.Globalization;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 构造万叶聚物同步 syncKey 的纯函数（hoeing-kazuha-collect-drop-terminal-signal）。
/// 与 KazuhaCollectSyncCoordinator.WaitAtFightPointAsync / PathExecutor 内联构造同式：
/// "{routeIndex}:{X:R}:{Y:R}"，用 InvariantCulture 保证跨 locale 浮点格式一致。
/// 抽出为纯函数供 PBT-2 守住"syncKey 恒非空非 null"（服务端删 CurrentSyncKey fallback 的安全前提）。
/// kazuha-collect-synckey-rerun-isolation：新增可选 <paramref name="rerunSuffix"/>（如 "_rerun_r3"），
/// 轮末统一重跑期间追加，避免命中上一轮残留的聚物点缓存；null/空 → 不追加，首次跑线逐字节不变。
/// </summary>
public static class KazuhaSyncKeyBuilder
{
    public static string BuildSyncKey(int routeIndex, double x, double y, string? rerunSuffix = null)
    {
        var baseKey = $"{routeIndex}:{x.ToString("R", CultureInfo.InvariantCulture)}:{y.ToString("R", CultureInfo.InvariantCulture)}";
        return string.IsNullOrEmpty(rerunSuffix) ? baseKey : baseKey + rerunSuffix;
    }
}
