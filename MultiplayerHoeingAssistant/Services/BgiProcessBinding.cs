using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MultiplayerHoeingAssistant.Services;

internal readonly record struct BgiProcessBindingResult(bool Success, string? Message);

internal readonly record struct BgiProcessIdentity(int ProcessId, long StartTicksUtc, int SessionId, string ImagePath);

internal delegate bool BgiPathCanonicalizer(string? path, out string canonical);

/// <summary>Bind the connected process to the configured installation without reading its memory.</summary>
internal static class BgiProcessBinding
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;

    internal static BgiProcessBindingResult Verify(BgiEpoch epoch, string? configuredPath, int localSessionId)
        => VerifyCore(epoch, configuredPath, localSessionId, ReadProcessIdentity,
            PathIdentity.TryCanonicalizeForComparison);

    internal static BgiProcessBindingResult VerifyCore(BgiEpoch epoch, string? configuredPath, int localSessionId,
        Func<int, BgiProcessIdentity> reader, BgiPathCanonicalizer canonicalize)
    {
        if (epoch.ProcessId <= 0 || epoch.StartTicksUtc <= 0)
            return Reject("BGI 连接中的进程身份尚未确认");

        BgiProcessIdentity identity;
        try
        {
            identity = reader(epoch.ProcessId);
        }
        catch (ProcessReadException ex)
        {
            return Reject(ex.Message);
        }
        catch (Win32Exception ex)
        {
            return Reject($"读取 BGI 进程身份失败（Win32 {ex.NativeErrorCode}: {ex.Message}）");
        }
        catch (Exception ex)
        {
            return Reject($"读取 BGI 进程身份失败（{ex.GetType().Name}: {ex.Message}）");
        }

        if (identity.ProcessId != epoch.ProcessId || identity.StartTicksUtc != epoch.StartTicksUtc)
            return Reject("BGI 进程已重启或连接身份已经变化，请等待通道重新确认");
        if (identity.SessionId != localSessionId)
            return Reject($"BGI 位于其他 Windows 会话（BGI：{identity.SessionId}，助手：{localSessionId}）");

        string actual;
        try
        {
            if (!canonicalize(identity.ImagePath, out actual))
                return Reject($"无法规范化正在运行的 BGI 程序路径（实际路径：{identity.ImagePath}）");
        }
        catch (Exception ex)
        {
            return Reject($"规范化正在运行的 BGI 程序路径失败（{Describe(ex)}；实际路径：{identity.ImagePath}）");
        }

        string configured;
        try
        {
            if (!canonicalize(configuredPath, out configured))
                return Reject($"无法规范化 BGI 配置路径（当前配置：{configuredPath ?? "未配置"}）");
        }
        catch (Exception ex)
        {
            return Reject($"规范化 BGI 配置路径失败（{Describe(ex)}；当前配置：{configuredPath ?? "未配置"}）");
        }

        if (!string.Equals(actual, configured, StringComparison.OrdinalIgnoreCase))
            return Reject($"当前连接的 BGI 与配置的程序不一致（实际程序：{actual}；当前配置：{configured}）。请在任务中心的「配置 BGI」中选择正在使用的 BetterGI.exe");

        return new BgiProcessBindingResult(true, null);
    }

    /// <summary>Keep one process handle throughout the read so PID reuse cannot change the image being queried.</summary>
    internal static BgiProcessIdentity ReadProcessIdentity(int processId)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle.IsInvalid)
            throw NativeFailure("打开 BGI 进程");

        var actualProcessId = ReadProcessId(handle);
        var startTicks = ReadRunningStartTicks(handle);
        if (!ProcessIdToSessionId(actualProcessId, out var sessionId))
            throw NativeFailure("读取 BGI 的 Windows 会话");

        var path = new StringBuilder(32768);
        var size = (uint)path.Capacity;
        if (!QueryFullProcessImageNameW(handle, 0, path, ref size))
            throw NativeFailure("读取 BGI 程序路径");
        if (size == 0 || path.Length == 0)
            throw new ProcessReadException("读取 BGI 程序路径失败（Windows 返回了空路径）");

        // The process may exit while its path is being read. Never bind an exited or replaced process.
        if (ReadProcessId(handle) != actualProcessId || ReadRunningStartTicks(handle) != startTicks)
            throw new ProcessReadException("读取期间 BGI 进程身份已经变化，请等待通道重新确认");
        if (!ProcessIdToSessionId(actualProcessId, out var finalSessionId))
            throw NativeFailure("复核 BGI 的 Windows 会话");
        if (sessionId != finalSessionId)
            throw new ProcessReadException("读取期间 BGI 的 Windows 会话已经变化");

        return new BgiProcessIdentity(checked((int)actualProcessId), startTicks, checked((int)sessionId), path.ToString());
    }

    private static uint ReadProcessId(SafeProcessHandle handle)
    {
        var processId = GetProcessId(handle);
        if (processId == 0)
            throw NativeFailure("读取 BGI 进程编号");
        return processId;
    }

    private static long ReadRunningStartTicks(SafeProcessHandle handle)
    {
        if (!GetProcessTimes(handle, out var created, out var exited, out _, out _))
            throw NativeFailure("读取 BGI 进程启动时间");
        if (!GetExitCodeProcess(handle, out var exitCode))
            throw NativeFailure("读取 BGI 进程退出状态");
        // The exit timestamp also detects a process that deliberately exits with code STILL_ACTIVE.
        if (exitCode != StillActive || exited != 0)
            throw new ProcessReadException("BGI 进程已经退出，请等待通道重新连接");
        return DateTime.FromFileTimeUtc(created).Ticks;
    }

    private static ProcessReadException NativeFailure(string stage)
    {
        var error = Marshal.GetLastWin32Error();
        return new ProcessReadException($"{stage}失败（Win32 {error}: {new Win32Exception(error).Message}）");
    }

    private static string Describe(Exception exception)
        => exception is Win32Exception win32
            ? $"Win32 {win32.NativeErrorCode}: {win32.Message}"
            : $"{exception.GetType().Name}: {exception.Message}";

    private static BgiProcessBindingResult Reject(string message) => new(false, message + "；未发送任务");

    private sealed class ProcessReadException(string message) : Exception(message) { }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process,
        out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process,
        uint flags, StringBuilder executableName, ref uint size);
}
