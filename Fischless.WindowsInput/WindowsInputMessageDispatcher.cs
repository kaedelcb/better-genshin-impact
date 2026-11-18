using System.Runtime.InteropServices;
using Vanara.PInvoke;

namespace Fischless.WindowsInput;

internal class WindowsInputMessageDispatcher : IInputMessageDispatcher
{
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

        // 模拟键鼠发送失败时（如窗口被最小化/锁屏/UAC 安全桌面导致 SendInput 被拦截），自动等待后重试整个输入批次。
        // 成功路径零变化；仅当 SendInput 确实未全部插入输入队列时才重试（重发原子批次安全）。
        // 重试预算必须覆盖真实的焦点/桌面恢复耗时：2026-11-18 实测失焦恢复为 14.4s、17.6s、20.4s，
        // 旧的 10 秒预算会在恢复前最后一次重试失败，把可恢复故障误判成线路失败。
        // 30 秒后仍失败才抛异常，避免在多开/最小化/权限拦截场景下无限挂起。
        const int RetryCount = 30;
        const int RetryDelayMs = 1000;
        uint num = 0;
        for (int attempt = 0; attempt <= RetryCount; attempt++)
        {
            num = User32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(User32.INPUT)));
            if (num == (ulong)(long)inputs.Length)
            {
                break;
            }
            if (attempt < RetryCount)
            {
                System.Threading.Thread.Sleep(RetryDelayMs);
            }
        }

        if (num != (ulong)(long)inputs.Length)
        {
            throw new WindowsInputBlockedException(
                "模拟键鼠消息发送失败！常见原因：1.你未以管理员权限运行程序；2.存在安全软件拦截（比如360）");
        }
    }
}
