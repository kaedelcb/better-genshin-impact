// 仅替换截图、键鼠和应用全局环境；保护编排使用生产 TaskControl.EnvironmentRecovery.cs 原文件。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vanara.PInvoke { public static class User32 { public enum VK { VK_ESCAPE } } }
namespace BetterGenshinImpact.Core.Input
{
    public static class InputHub
    {
        public static int Releases;
        public static void ReleaseAll() => Interlocked.Increment(ref Releases);
        public static Input Foreground { get; } = new();
        public class Input { public Keyboard Keyboard { get; } = new(); }
        public class Keyboard
        {
            public static Action? PressForTest;
            public void KeyPress(Vanara.PInvoke.User32.VK key) => PressForTest?.Invoke();
        }
    }
}
namespace BetterGenshinImpact.GameTask
{
    public sealed class RunnerContext
    {
        public static RunnerContext Instance { get; } = new();
        public bool IsSuspend { get; set; }
        public int AutoPickTriggerStopCount;
        public Dictionary<string, Suspendable> SuspendableDictionary { get; } = new();
        public void StopAutoPick() => Interlocked.Increment(ref AutoPickTriggerStopCount);
        public void ResumeAutoPick() => Interlocked.Decrement(ref AutoPickTriggerStopCount);
    }
    public class Suspendable
    {
        public int Suspends, Resumes;
        public void Suspend() => Suspends++;
        public void Resume() => Resumes++;
    }
    public static class SystemControl
    {
        public static bool ActiveForTest = true;
        public static Action? FocusForTest;
        public static bool IsGenshinImpactActiveByProcess() => ActiveForTest;
        public static void FocusWindow(IntPtr _) => FocusForTest?.Invoke();
    }
    public class TaskContext
    {
        private static readonly TaskContext Current = new();
        public static TaskContext Instance() => Current;
        public IntPtr GameHandle => IntPtr.Zero;
        public RuntimeData? Runtime { get; set; }
        public class RuntimeData { public WindowData Window { get; } = new(); }
        public class WindowData { public bool RequiresForeground { get; set; } = true; }
        public ConfigData Config { get; } = new();
        public class ConfigData { public TpConfigData TpConfig { get; } = new(); }
        public class TpConfigData { public bool UseOfficialTeleport { get; set; } }
    }
}
namespace BetterGenshinImpact.GameTask.Common
{
    public class Screen : IDisposable
    {
        public static bool IsMapForTest;
        public Screen Find(object _) => new();
        public bool IsExist() => IsMapForTest;
        public void Dispose() { }
    }
    public partial class TaskControl
    {
        public static ILogger Logger { get; } = NullLogger.Instance;
        private static readonly SemaphoreSlim _networkRecoveryGate = new(1, 1);
        private static readonly AsyncLocal<bool> _inNetworkRecovery = new();
        private static int _networkFailureCount;
        private static DateTime _lastCheckTime;
        public static bool IsSuspendedByNetwork { get; set; }
        public static bool IsSuspendedByWindow { get; set; }
        public static Screen CaptureToRectArea() => new();
        public static Func<int, CancellationToken, Task>? DelayForTest;
        public static Task Delay(int ms, CancellationToken ct) => DelayForTest?.Invoke(ms, ct) ?? Task.Delay(ms, ct);
        public static Func<CancellationToken, Task> Monitor = _ => Task.CompletedTask;
        public static Func<CancellationToken, Task<bool>> ReachableForTest = _ => Task.FromResult(false);
        private static Task<bool> IsEnvironmentNetworkReachableAsync(CancellationToken ct) => ReachableForTest(ct);
        private static Task CheckNetworkStatusAsync(CancellationToken ct) => Monitor(ct);
        public static void TriggerMonitor() => ScheduleNetworkCheck();
        public static async Task RunNetworkRecoveryForTest(CancellationToken ct)
        {
            _environmentPauseDepth = 1; // 模拟主任务已在暂停点释放输入。
            _inNetworkRecovery.Value = true;
            try { await RecoverNetworkWithProtectionAsync(ct); }
            finally { _environmentPauseDepth = 0; _inNetworkRecovery.Value = false; }
        }
    }
}
namespace BetterGenshinImpact.GameTask.Common.BgiVision
{
    public static class Bv
    {
        public static Func<bool> MainUiForTest = () => true;
        public static bool IsInMainUi(Common.Screen _) => MainUiForTest();
    }
}
namespace BetterGenshinImpact.GameTask.Common.Job
{
    public static class NetworkRecovery
    {
        public static bool RecoveryNetworkDone;
        public static Func<CancellationToken, Task>? StartForTest;
        public static Task Start(CancellationToken ct, bool verifyMainUi = false, bool clearSuspend = true,
            int mainUiAttempts = 60)
            => StartForTest?.Invoke(ct) ?? Task.CompletedTask;
    }
}
namespace BetterGenshinImpact.GameTask.AutoTrackPath
{
    public sealed class TpTaskFastDragAssets
    {
        public object MapScaleButtonRo { get; } = new();
        public static TpTaskFastDragAssets Get(Common.Screen _) => new();
    }
    public class TpTaskFastDrag(CancellationToken ct)
    {
        public static Func<CancellationToken, Task<bool>>? ProbeForTest;
        public static int ProbeCalls;

        public Task<bool> ProbeMapForRecoveryAsync(string? mapName = null)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref ProbeCalls);
            return ProbeForTest?.Invoke(ct) ?? Task.FromResult(true);
        }
    }
}
