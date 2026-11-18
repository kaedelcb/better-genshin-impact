using System;
using System.Threading;
using Fischless.WindowsInput;
using Vanara.PInvoke;

namespace BetterGenshinImpact.Core.Input.Backends.Win32;

/// <summary>
/// Windows 键鼠后端：前台 SendInput，后台 PostMessage。两路总是同时可用
/// </summary>
public sealed class Win32InputBackend : IInputBackend
{
    private readonly IntPtr _hWnd;

    /// <param name="hWnd">游戏窗口句柄。为 IntPtr.Zero 时后台通道的所有操作都 warn</param>
    public Win32InputBackend(IntPtr hWnd)
    {
        _hWnd = hWnd;
        Foreground = new SendInputChannel();
        Background = new PostMessageChannel(hWnd);
    }

    internal WindowsInputRetryScope? BeginRouteInputRetry(CancellationToken ct)
    {
        if (_hWnd == IntPtr.Zero) return null;

        return new WindowsInputRetryScope(
            () => ReferenceEquals(InputHub.Backend, this) && (IntPtr)User32.GetForegroundWindow() == _hWnd,
            () =>
            {
                ct.ThrowIfCancellationRequested();
                InputHub.ThrowIfOperationCancelled();
            },
            milliseconds => InputHub.WaitInOperation(milliseconds, ct));
    }

    public InputBackendKind Kind => InputBackendKind.Win32;

    public IInputChannel Foreground { get; }

    public IInputChannel Background { get; }

    public void ReleaseAll()
    {
        Background.ReleaseAll();
        Foreground.ReleaseAll();
    }

    public void Dispose()
    {
    }
}
