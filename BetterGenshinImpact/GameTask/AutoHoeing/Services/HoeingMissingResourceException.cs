using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>Required hoeing input is absent; this is a failed task, not a filtered route or normal skip.</summary>
internal sealed class HoeingMissingResourceException(string message) : InvalidOperationException(message) { }

internal static class HoeingMissingResourceBoundary
{
    internal static void RequireScriptDirectory(string directory, CancellationToken ct, Action? checkOwner = null)
    {
        CheckStop(ct, checkOwner);
        if (!Directory.Exists(directory) || !Directory.Exists(Path.Combine(directory, "pathing")))
            throw new HoeingMissingResourceException(
                "锄地一条龙缺少必要脚本资源：" + directory + "。请在脚本仓库安装 AutoHoeingOneDragon 后重试。");
    }

    internal static void RequireFixedRoutes(int count, CancellationToken ct, Action? checkOwner = null)
    {
        CheckStop(ct, checkOwner);
        if (count <= 0)
            throw new HoeingMissingResourceException("固定锄地线路没有 JSON 文件，请选择已有线路目录或安装线路资源后重试。");
    }

    /// <summary>Preserve the existing handling of other native exceptions; only this missing-input failure must escape cleanup.</summary>
    internal static void RethrowAfterCleanup(Exception? failure, CancellationToken ct, Action? checkOwner = null)
    {
        if (failure is not HoeingMissingResourceException) return;
        CheckStop(ct, checkOwner);
        ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void CheckStop(CancellationToken ct, Action? checkOwner)
    {
        ct.ThrowIfCancellationRequested();
        checkOwner?.Invoke();
    }
}
