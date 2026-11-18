using System.Diagnostics;
using System.Runtime.InteropServices;
using Vanara.PInvoke;

namespace Fischless.WindowsInput;

internal class WindowsInputMessageDispatcher : IInputMessageDispatcher
{
    private const string FailureMessage = "模拟键鼠消息发送失败！常见原因：1.你未以管理员权限运行程序；2.存在安全软件拦截（比如360）";
    private readonly Func<User32.INPUT[], uint> _send;
    private readonly Func<long> _milliseconds;

    public WindowsInputMessageDispatcher() : this(
        inputs => User32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(User32.INPUT))),
        () => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)))
    {
    }

    internal WindowsInputMessageDispatcher(Func<User32.INPUT[], uint> send, Func<long> milliseconds)
    {
        _send = send;
        _milliseconds = milliseconds;
    }

    public void DispatchInput(User32.INPUT[] inputs)
    {
        if (inputs == null)
        {
            throw new ArgumentNullException(nameof(inputs));
        }

        if (inputs.Length == 0)
        {
            throw new ArgumentException("The input array was empty", nameof(inputs));
        }

        if (WindowsInputRetryScope.Current is { } scope)
        {
            DispatchScoped(inputs, scope);
            return;
        }

        // 非联机线路继续使用原有发送预算。
        const int LegacyRetryCount = 10;
        for (int attempt = 0; attempt <= LegacyRetryCount; attempt++)
        {
            if (_send(inputs) == inputs.Length) return;
            if (attempt < LegacyRetryCount) Thread.Sleep(1000);
        }

        throw new Exception(FailureMessage);
    }

    private void DispatchScoped(User32.INPUT[] inputs, WindowsInputRetryScope scope)
    {
        if (WindowsInputRetryScope.IsCleanup)
        {
            if (scope.IsOpen && scope.IsTargetForeground() && _send(inputs) == inputs.Length) return;
            throw new WindowsInputBlockedException(FailureMessage);
        }

        const int BudgetMs = 30_000;
        const int RetryDelayMs = 1000;
        long start = _milliseconds();
        while (true)
        {
            if (!scope.IsOpen) throw new OperationCanceledException("线路输入作用域已结束");
            scope.CheckCancellation();
            if (_milliseconds() - start >= BudgetMs) break;

            if (scope.IsTargetForeground())
            {
                scope.CheckCancellation();
                if (!scope.IsOpen) throw new OperationCanceledException("线路输入作用域已结束");
                uint sent = _send(inputs);
                if (sent == inputs.Length) return;
                // 批次部分入队后不能从头重发，否则会重复点击或输入文字。
                if (sent != 0) break;
            }

            long remaining = BudgetMs - (_milliseconds() - start);
            if (remaining <= 0) break;
            scope.Wait((int)Math.Min(remaining, RetryDelayMs));
        }

        scope.CheckCancellation();
        throw new WindowsInputBlockedException(FailureMessage);
    }
}
