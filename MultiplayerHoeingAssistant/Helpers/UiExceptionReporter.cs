namespace MultiplayerHoeingAssistant.Helpers;

/// <summary>保留 UI 异常证据，同一故障在本次运行中只弹一次，弹窗期间不再叠窗。</summary>
internal sealed class UiExceptionReporter(Action<string> writeLog, Action<string> showError)
{
    private readonly Dictionary<string, long> _occurrences = new(StringComparer.Ordinal);
    private bool _showingError;

    internal void Report(Exception error)
    {
        // 包含调用栈，避免把不同入口的同文案异常合并。
        var signature = error.ToString();
        _occurrences.TryGetValue(signature, out var count);
        _occurrences[signature] = ++count;
        // 首次留完整堆栈；重复故障按 2、4、8…次汇总，避免持续刷新刷满日志。
        if ((count & (count - 1)) == 0)
            writeLog($"[UI线程异常] 累计 {count} 次\n{signature}");

        if (count > 1 || _showingError) return;
        _showingError = true;
        try
        {
            showError($"UI线程异常: {error.Message}\n\n详细信息已记录到运行日志。同一异常在本次运行中不再重复弹窗。");
        }
        catch (Exception notificationError)
        {
            writeLog("[UI异常提示失败] " + notificationError);
        }
        finally { _showingError = false; }
    }
}
