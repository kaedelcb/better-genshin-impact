using System;
using BetterGenshinImpact.Service.Execution;

namespace BetterGenshinImpact.Service.ExternalInterface;

/// <summary>收尾效果与执行体退出分开：仅独立观察确认的效果发布成功，未知不补发。</summary>
internal static class TerminalCompletionEffect
{
    internal static bool? ProbeGameExited(Func<bool> anyGameAlive)
    {
        try { return !anyGameAlive(); }
        catch { return null; }
    }

    internal static void Publish(JobRegistry registry, Guid handle, string? action, string? detail)
    {
        if (action == "closeGame" && detail == "confirmed:closeGame")
            registry.TryMarkTerminal(handle, JobState.Succeeded, null, detail, false);
        else
            registry.TryMarkUnknown(handle, "result_unknown", detail);
    }
}
