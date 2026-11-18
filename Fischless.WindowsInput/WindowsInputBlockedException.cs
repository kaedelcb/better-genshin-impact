namespace Fischless.WindowsInput;

/// <summary>
/// 模拟键鼠消息在重试预算内仍未被系统接受。
/// 常见于游戏窗口失焦、锁屏、UAC 安全桌面、安全软件或 UIPI 拦截；
/// 这是本地可恢复故障，调用方不应无条件把它升级为不可恢复的任务失败。
/// </summary>
public sealed class WindowsInputBlockedException : Exception
{
    public WindowsInputBlockedException(string message) : base(message)
    {
    }

    public WindowsInputBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
