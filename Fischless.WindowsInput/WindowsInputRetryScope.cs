namespace Fischless.WindowsInput;

/// <summary>为当前异步执行链启用有前台目标和取消检查的 SendInput 重试。</summary>
public sealed class WindowsInputRetryScope : IDisposable
{
    private static readonly AsyncLocal<WindowsInputRetryScope?> CurrentScope = new();
    private static readonly AsyncLocal<bool> Cleanup = new();
    private readonly WindowsInputRetryScope? _previous;
    private int _disposed;

    public WindowsInputRetryScope(Func<bool> isTargetForeground, Action checkCancellation, Action<int> wait)
    {
        IsTargetForeground = isTargetForeground;
        CheckCancellation = checkCancellation;
        Wait = wait;
        _previous = CurrentScope.Value;
        CurrentScope.Value = this;
    }

    internal static WindowsInputRetryScope? Current => CurrentScope.Value;
    internal static bool IsCleanup => Cleanup.Value;
    public static bool IsActive => Current != null;
    internal Func<bool> IsTargetForeground { get; }
    internal Action CheckCancellation { get; }
    internal Action<int> Wait { get; }
    internal bool IsOpen => Volatile.Read(ref _disposed) == 0;

    public static void RunCleanup(Action release)
    {
        var previous = Cleanup.Value;
        Cleanup.Value = true;
        try { release(); }
        finally { Cleanup.Value = previous; }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        if (ReferenceEquals(CurrentScope.Value, this)) CurrentScope.Value = _previous;
    }
}
