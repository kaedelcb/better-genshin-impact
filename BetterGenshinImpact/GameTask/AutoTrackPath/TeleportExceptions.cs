using System;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

/// <summary>
/// 传送域异常聚合文件（refactor-feature-inventory §G4.1.4：同域异常必须合并进一个文件）。
/// MapPositionNotRecognizedException 与 TeleportLoadingTimeoutException 均为纯异常类型（无成员），
/// 由 TpTaskFastDrag / TpTaskOfficial 抛与捕获；类名与命名空间保持不变，调用方零感知。
/// </summary>
public class MapPositionNotRecognizedException : Exception
{
    public MapPositionNotRecognizedException(string message) : base(message)
    {
    }

    public MapPositionNotRecognizedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// 阶段 1（传送过渡页观察）超时异常。
///
/// 由 TpTaskFastDrag.WaitForTeleportCompletion 在 requireLoadingScreen=true 且 6s 内未观察到
/// 过渡页时抛出。会被 TpTaskFastDrag.Tp() 的 for (i&lt;3) 重试循环 catch (Exception) 分支捕获
/// → 回主界面 + 1s + 重试（NormalEndException/TaskCanceledException 直接透传不重试）。
///
/// 详见 .kiro/specs/multiplayer-tp-success-via-loading-screen/bugfix.md §"EB 2.9" / §"Open Question Q5"。
/// </summary>
public class TeleportLoadingTimeoutException : Exception
{
    public TeleportLoadingTimeoutException(string message) : base(message) { }

    public TeleportLoadingTimeoutException(string message, Exception innerException)
        : base(message, innerException) { }
}