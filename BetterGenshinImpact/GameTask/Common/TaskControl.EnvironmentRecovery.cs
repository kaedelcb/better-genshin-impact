using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Job;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.Common;

public partial class TaskControl
{
    private static readonly object NetworkMonitorSync = new();
    private static CancellationTokenSource? _environmentLifetime;
    private static Task _networkMonitorTask = Task.CompletedTask;
    private static bool _environmentStopping;
    private static readonly AsyncLocal<CancellationToken> RecoveryProbeToken = new();
    private static int _environmentPauseDepth;
    private static long _networkRecoveryVersion;
    private static long _lastVerifiedInputTick;
    public static long NetworkRecoveryVersion => Interlocked.Read(ref _networkRecoveryVersion);
    internal static bool HasRecentVerifiedGameResponse => Interlocked.Read(ref _lastVerifiedInputTick) is var tick
        && tick != 0 && Environment.TickCount64 - tick < 60_000;
    private static readonly AsyncLocal<EnvironmentRecoveryWindow?> OperationRecoveryWindow = new();

    // 开图嵌套在传送中时共用预算；独立开图自行持有，正常返回后不污染下次操作。
    public static IDisposable? BeginEnvironmentOperation()
    {
        if (OperationRecoveryWindow.Value != null) return null;
        OperationRecoveryWindow.Value = new EnvironmentRecoveryWindow();
        return new EnvironmentOperationScope();
    }

    private sealed class EnvironmentOperationScope : IDisposable
    {
        public void Dispose() => OperationRecoveryWindow.Value = null;
    }

    // 公版传送不接入新保护。生命周期由 TaskRunner 持有，后台探测不能越过任务结束。
    internal static void BeginEnvironmentRecovery(CancellationToken ct)
    {
        lock (NetworkMonitorSync)
        {
            _environmentStopping = false;
            if (TaskContext.Instance().Config.TpConfig.UseOfficialTeleport) return;
            _environmentLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            IsSuspendedByNetwork = false;
            IsSuspendedByWindow = false;
            _networkFailureCount = 0;
            NetworkRecovery.RecoveryNetworkDone = false;
            Interlocked.Exchange(ref _lastVerifiedInputTick, 0);
        }
    }

    internal static async Task EndEnvironmentRecoveryAsync()
    {
        CancellationTokenSource? lifetime;
        Task monitor;
        lock (NetworkMonitorSync)
        {
            lifetime = _environmentLifetime;
            monitor = _networkMonitorTask;
            if (lifetime != null) _environmentStopping = true;
        }
        if (lifetime == null) return;
        lifetime.Cancel();
        try { await monitor; }
        catch (OperationCanceledException) { }
        finally
        {
            lock (NetworkMonitorSync)
            {
                _environmentLifetime = null;
                _environmentStopping = false;
                IsSuspendedByNetwork = false;
                IsSuspendedByWindow = false;
                NetworkRecovery.RecoveryNetworkDone = false;
            }
            lifetime.Dispose();
        }
    }

    private static void ScheduleNetworkCheck()
    {
        if (_inNetworkRecovery.Value || RunnerContext.Instance.IsSuspend) return;
        lock (NetworkMonitorSync)
        {
            if (_environmentStopping) return;
            if (_environmentLifetime == null && !TaskContext.Instance().Config.TpConfig.UseOfficialTeleport) return;
            if (!_networkMonitorTask.IsCompleted) return;
            var ct = _environmentLifetime?.Token ?? CancellationToken.None;
            if (ct.IsCancellationRequested) return;
            _networkMonitorTask = Task.Run(async () =>
            {
                try { await CheckNetworkStatusAsync(ct); }
                catch (Exception) when (ct.IsCancellationRequested) { }
                catch (Exception ex) { Logger.LogError(ex, "网络检查未完成"); }
            });
        }
    }

    private static CancellationToken GetEnvironmentToken(CancellationToken ct)
    {
        if (RecoveryProbeToken.Value.CanBeCanceled) return RecoveryProbeToken.Value;
        return ct.CanBeCanceled ? ct : _environmentLifetime?.Token ?? CancellationToken.None;
    }

    private static async Task WaitForEnvironmentUserAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        InputHub.ReleaseAll();
        RunnerContext.Instance.IsSuspend = true;
        Logger.LogWarning("环境恢复超过60秒，当前任务和路线保留，未消耗重试次数。请检查游戏后使用暂停/继续快捷键继续，或停止任务；不会自动跳到下一项。");
        while (RunnerContext.Instance.IsSuspend)
            await Task.Delay(100, ct);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>仅失败分支调用；保持当前调用未完成，JS/配置组/独立任务均不能越过当前操作。</summary>
    public static async Task WaitForEnvironmentRecoveryAsync(
        Func<CancellationToken, Task<bool>> probe, CancellationToken ct,
        EnvironmentRecoveryWindow? window = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            ct, _environmentLifetime?.Token ?? CancellationToken.None);
        ct = lifetime.Token;
        ct.ThrowIfCancellationRequested();
        var suspendables = RunnerContext.Instance.SuspendableDictionary.Values.ToArray();
        RunnerContext.Instance.StopAutoPick();
        var acknowledged = false;
        var ownsRecoveryGate = false;
        try
        {
            InputHub.ReleaseAll();
            foreach (var item in suspendables) item.Suspend();
            Interlocked.Increment(ref _environmentPauseDepth);
            acknowledged = true;
            Logger.LogWarning("游戏操作连续无响应，保留当前步骤并检查环境；本次等待不计入任务重试");
            await _networkRecoveryGate.WaitAsync(ct);
            ownsRecoveryGate = true;
            await (window ?? OperationRecoveryWindow.Value ?? new EnvironmentRecoveryWindow()).WaitAsync(async token =>
            {
                var previous = _inNetworkRecovery.Value;
                var previousToken = RecoveryProbeToken.Value;
                try
                {
                    _inNetworkRecovery.Value = true;
                    RecoveryProbeToken.Value = token;
                    if (IsSuspendedByWindow || IsSuspendedByNetwork)
                        await NetworkRecovery.Start(token, verifyMainUi: true, clearSuspend: false);
                    token.ThrowIfCancellationRequested();
                    if (!await probe(token)) return false;
                    token.ThrowIfCancellationRequested();
                    return true;
                }
                catch (RetryException ex) when (!token.IsCancellationRequested)
                {
                    Logger.LogDebug(ex, "环境探测暂未完成，保持当前任务");
                    return false;
                }
                finally
                {
                    RecoveryProbeToken.Value = previousToken;
                    _inNetworkRecovery.Value = previous;
                }
            }, WaitForEnvironmentUserAsync, ct);
            ct.ThrowIfCancellationRequested();
            ClearVerifiedNetworkRecovery();
            Logger.LogInformation("已确认游戏操作恢复，继续原任务步骤");
        }
        finally
        {
            try { InputHub.ReleaseAll(); }
            finally
            {
                if (ownsRecoveryGate) _networkRecoveryGate.Release();
                if (acknowledged) Interlocked.Decrement(ref _environmentPauseDepth);
                foreach (var item in suspendables) item.Resume();
                RunnerContext.Instance.ResumeAutoPick();
            }
        }
    }

    private static void ClearVerifiedNetworkRecovery()
    {
        // 网站/ICMP 持续不可达时，不反复打断已经证明可操作的游戏；断线弹窗和操作失败仍立即保护。
        Interlocked.Exchange(ref _lastVerifiedInputTick, Environment.TickCount64);
        IsSuspendedByNetwork = false;
        IsSuspendedByWindow = false;
        NetworkRecovery.RecoveryNetworkDone = true;
        _networkFailureCount = 0;
        _lastCheckTime = DateTime.UtcNow;
    }

    // 调用方已经持有恢复门。真实开图、关图响应才允许继续原任务，不能仅凭派蒙图标放行。
    private static async Task RecoverNetworkWithProtectionAsync(CancellationToken ct)
    {
        await new EnvironmentRecoveryWindow().WaitAsync(async token =>
        {
            var previousToken = RecoveryProbeToken.Value;
            try
            {
                RecoveryProbeToken.Value = token;
                // 等主任务进入暂停点，避免恢复线程与正在移动/传送的任务同时发输入。
                if (Volatile.Read(ref _environmentPauseDepth) == 0) return false;
                await NetworkRecovery.Start(token, verifyMainUi: true, clearSuspend: false);
                using (var screen = CaptureToRectArea())
                    if (!Bv.IsInMainUi(screen)) return false;
                var tp = new TpTaskFastDrag(token);
                if (!await tp.ProbeMapForRecoveryAsync()) return false;
                if (!await CloseRecoveryMapAsync(token)) return false;
                token.ThrowIfCancellationRequested();
                return true;
            }
            catch (RetryException ex) when (!token.IsCancellationRequested)
            {
                Logger.LogDebug(ex, "网络恢复探测暂未完成");
                return false;
            }
            finally { RecoveryProbeToken.Value = previousToken; }
        }, WaitForEnvironmentUserAsync, ct);
        ct.ThrowIfCancellationRequested();
        // 预算窗口接受结果之后才发布恢复，迟到的探测不能提前放行主任务。
        Interlocked.Increment(ref _networkRecoveryVersion);
        ClearVerifiedNetworkRecovery();
    }

    private static async Task<bool> CloseRecoveryMapAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        InputHub.Foreground.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
        await Delay(500, ct);
        using var restored = CaptureToRectArea();
        return Bv.IsInMainUi(restored);
    }

    /// <summary>寻路即将放弃时复核。能正常开图则保留原错误处理；确有无响应才保护并无计数回段。</summary>
    public static async Task<bool> TryRecoverPathEnvironmentAsync(CancellationToken ct)
    {
        if (TaskContext.Instance().Config.TpConfig.UseOfficialTeleport) return false;
        var unresponsive = false;
        await WaitForEnvironmentRecoveryAsync(async token =>
        {
            try
            {
                var responsive = await new TpTaskFastDrag(token).ProbeMapForRecoveryAsync()
                                 && await CloseRecoveryMapAsync(token);
                unresponsive |= !responsive;
                return responsive;
            }
            catch (RetryException) when (!token.IsCancellationRequested)
            {
                unresponsive = true;
                return false;
            }
        }, ct);
        return unresponsive;
    }
}
