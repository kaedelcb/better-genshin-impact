using System;
using System.Diagnostics;
using System.Threading;
using BetterGenshinImpact.Core.Script;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common;

/// <summary>
/// 【临时诊断】捕获/卡顿问题定位工具。
///
/// 目的：定位"卡住不动 / 变慢 / 按 Win 才动一帧"的根因层。它把分散在
/// 截图层、调度器 Tick、SoloTask 取帧循环、焦点/暂停 的运行时状态，
/// 每秒汇成一条 [Diag] 心跳日志。卡住时这一条日志就能区分：
///   - 截图循环是否还在跑（CapRate）
///   - 截到的画面是否在变（StaleMs / 画面指纹）
///   - 调度器 Tick 是否被锁跳过（TickSkip）
///   - 是否卡在暂停/焦点恢复（Suspend/Focus）
///   - GC / 单帧耗时是否异常（LastCapMs / GcMs）
///
/// 排查完成后可整体删除本文件及其调用点（搜索 CaptureDiagnostics）。
/// </summary>
public static class CaptureDiagnostics
{
    /// <summary>诊断总开关。默认开；排查完把它设 false 或删除调用即可。</summary>
    public static bool Enabled { get; set; } = true;

    // 静态类不能作泛型类型参数，借用 TaskControl 作为日志类别名（日志前缀统一带 [Diag]）。
    private static readonly ILogger Logger = App.GetLogger<TaskControl>();
    private static readonly object _sync = new();

    // ---- 截图统计 ----
    private static long _captureCallCount;          // Capture() 被调用总次数
    private static long _captureSuccessCount;        // 返回非空帧次数
    private static long _captureNullCount;           // 返回 null 次数
    private static readonly Stopwatch _sinceLastCapture = Stopwatch.StartNew(); // 距上次成功截图
    private static double _lastCaptureCostMs;        // 上一次 Capture() 耗时

    // ---- 画面指纹（判断"帧是否在变"）----
    private static long _lastFingerprint = -1;
    private static readonly Stopwatch _sinceFrameChanged = Stopwatch.StartNew(); // 画面内容距上次变化
    private static long _staleStreak;                // 连续多少帧指纹相同

    // ---- 调度器 Tick 统计 ----
    private static long _tickRunCount;               // Tick 真正执行次数
    private static long _tickSkipLockCount;          // 因抢不到 _locker 跳过
    private static long _tickSkipBranchCount;        // 因某分支 return 跳过（未截图）
    private static string _lastTickBranch = "-";     // 上一次 Tick 走到的分支/终点
    private static readonly Stopwatch _sinceTickRun = Stopwatch.StartNew();

    // ---- GC ----
    private static double _lastGcCostMs;

    // ---- 心跳线程 ----
    private static Timer? _heartbeat;
    private static long _lastHeartbeatCaptureCount;

    public static void EnsureStarted()
    {
        if (!Enabled) return;
        if (_heartbeat != null) return;
        lock (_sync)
        {
            if (_heartbeat != null) return;
            _heartbeat = new Timer(_ => Heartbeat(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            Logger.LogInformation("[Diag] 捕获诊断心跳已启动（每秒一条）");
        }
    }

    /// <summary>
    /// 每次调用 IGameCapture.Capture() 后调用（无论成功失败）。
    /// costMs = 本次 Capture() 耗时；frame = 返回帧（可空）。
    /// 会计数、计算画面指纹、更新"距上次成功截图/画面变化"计时。
    /// </summary>
    public static void NotifyCapture(double costMs, Mat? frame)
    {
        if (!Enabled) return;
        EnsureStarted();
        try
        {
            Interlocked.Increment(ref _captureCallCount);
            _lastCaptureCostMs = costMs;

            if (frame == null || frame.Empty())
            {
                Interlocked.Increment(ref _captureNullCount);
                return;
            }

            Interlocked.Increment(ref _captureSuccessCount);
            _sinceLastCapture.Restart();

            var fp = ComputeFingerprint(frame);
            lock (_sync)
            {
                if (fp == _lastFingerprint)
                {
                    _staleStreak++;
                }
                else
                {
                    _lastFingerprint = fp;
                    _staleStreak = 0;
                    _sinceFrameChanged.Restart();
                }
            }
        }
        catch (Exception ex)
        {
            // 诊断代码本身绝不能影响主流程；仅告警，不抛。
            Logger.LogWarning(ex, "[Diag] NotifyCapture 异常（已忽略）");
        }
    }

    /// <summary>
    /// 稀疏采样计算画面指纹。避免对整帧 1080P 做哈希（60fps 太贵）。
    /// 取约 12x12=144 个网格点的灰度和，足以区分"画面有没有变"。
    /// </summary>
    private static long ComputeFingerprint(Mat frame)
    {
        const int grid = 12;
        long sum = 0;
        int rows = frame.Rows, cols = frame.Cols, ch = frame.Channels();
        if (rows == 0 || cols == 0) return 0;
        for (int gy = 0; gy < grid; gy++)
        {
            int y = (int)((gy + 0.5) * rows / grid);
            if (y >= rows) y = rows - 1;
            for (int gx = 0; gx < grid; gx++)
            {
                int x = (int)((gx + 0.5) * cols / grid);
                if (x >= cols) x = cols - 1;
                // 只取第 0 通道一个字节，够用且极快
                sum = sum * 31 + frame.At<byte>(y, x * ch);
            }
        }
        return sum;
    }

    public static void NotifyTickRun(string branch)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _tickRunCount);
        _lastTickBranch = branch;
        _sinceTickRun.Restart();
    }

    public static void NotifyTickSkipLock()
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _tickSkipLockCount);
    }

    public static void NotifyTickSkipBranch(string branch)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _tickSkipBranchCount);
        _lastTickBranch = branch;
    }

    public static void NotifyGc(double costMs)
    {
        if (!Enabled) return;
        _lastGcCostMs = costMs;
    }

    private static void Heartbeat()
    {
        try
        {
            var capCount = Interlocked.Read(ref _captureCallCount);
            var capRate = capCount - _lastHeartbeatCaptureCount; // 最近 1 秒的 Capture 调用数
            _lastHeartbeatCaptureCount = capCount;

            var sinceCap = (long)_sinceLastCapture.Elapsed.TotalMilliseconds;
            var sinceChange = (long)_sinceFrameChanged.Elapsed.TotalMilliseconds;
            var sinceTick = (long)_sinceTickRun.Elapsed.TotalMilliseconds;

            var suspend = RunnerContext.Instance.IsSuspend;
            var byCapture = TaskControl.IsSuspendedByCapture;
            var byNetwork = TaskControl.IsSuspendedByNetwork;
            var cancel = CancellationContext.Instance.IsCancellationRequested;

            bool active;
            try { active = SystemControl.IsGenshinImpactActive(); }
            catch { active = false; }

            // 缓冲池统计（BitBlt 专属，其它捕获方式为 0）
            var liveBuf = Interlocked.Read(ref Fischless.GameCapture.BitBlt.BitBltSession.DiagLiveBufferCount);
            var totalAlloc = Interlocked.Read(ref Fischless.GameCapture.BitBlt.BitBltSession.DiagTotalBufferAllocations);
            var gdiMs = Fischless.GameCapture.BitBlt.BitBltSession.DiagLastGdiBlitMs;

            // 一行汇总。重点字段含义见文件头注释。
            Logger.LogInformation(
                "[Diag] CapRate={CapRate}/s 距上次截图={SinceCap}ms 单帧={CapMs:F1}ms GDI={GdiMs:F1}ms | 画面{StaleFlag} 距变化={SinceChange}ms 连续同帧={Stale} | Tick执行={TickRun} 距上次={SinceTick}ms 锁跳过={SkipLock} 分支跳过={SkipBranch} 末端={Branch} | 存活缓冲={LiveBuf} 累计分配={TotalAlloc} | Suspend={Suspend}(cap={ByCap}/net={ByNet}) Cancel={Cancel} 前台原神={Active} GC={GcMs:F0}ms",
                capRate,
                sinceCap,
                _lastCaptureCostMs,
                gdiMs,
                _staleStreak > 5 ? "卡住?" : "OK",
                sinceChange,
                Interlocked.Read(ref _staleStreak),
                Interlocked.Read(ref _tickRunCount),
                sinceTick,
                Interlocked.Read(ref _tickSkipLockCount),
                Interlocked.Read(ref _tickSkipBranchCount),
                _lastTickBranch,
                liveBuf,
                totalAlloc,
                suspend,
                byCapture,
                byNetwork,
                cancel,
                active,
                _lastGcCostMs);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Diag] 心跳异常（已忽略）");
        }
    }
}
