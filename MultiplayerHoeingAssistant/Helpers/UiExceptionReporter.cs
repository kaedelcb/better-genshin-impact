namespace MultiplayerHoeingAssistant.Helpers;

/// <summary>同步保留 UI 异常证据，首次异步提示失败；通知不得使用模态窗口。</summary>
internal sealed class UiExceptionReporter(Action<string> writeLog, Action<string> notifyFailure)
{
    private readonly Dictionary<string, long> _occurrences = new(StringComparer.Ordinal);
    private bool _showingError;

    internal void Report(Exception error, string outcome = "本次界面操作失败，未自动恢复；未重放业务操作")
    {
        // 包含调用栈，避免把不同入口的同文案异常合并。
        var signature = outcome + "\n" + error;
        // 长时间运行时限制去重缓存；淘汰后再次发生会重新完整记录。
        if (!_occurrences.ContainsKey(signature) && _occurrences.Count >= 128)
            _occurrences.Remove(_occurrences.Keys.First());
        _occurrences.TryGetValue(signature, out var count);
        _occurrences[signature] = ++count;
        // 首次留完整堆栈；重复故障按 2、4、8…次汇总，避免持续刷新刷满日志。
        if ((count & (count - 1)) == 0)
            writeLog(RuntimeLog.FormatException("UI线程异常", error, false, $"{outcome}；累计 {count} 次"));

        if (count > 1 || _showingError) return;
        _showingError = true;
        try
        {
            notifyFailure($"[界面异常] {outcome}：{error.Message}。完整异常已记录到运行日志。");
        }
        catch (Exception notificationError) when (!RuntimeLog.IsCritical(notificationError))
        {
            writeLog("[UI异常提示失败] " + notificationError);
        }
        finally { _showingError = false; }
    }
}
