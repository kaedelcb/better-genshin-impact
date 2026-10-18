using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.LogRoom.IntegrationTest;

public sealed partial class RemoteLogRoomTests
{
    /// <summary>复用同 UID 监控端的真实窗口和本地 SignalR，验证实时批次不反复清空正在阅读的文档。</summary>
    private static async Task VerifyRealtimeWindowRefreshAsync(MainWindow window, SignalRClient executor,
        string remoteUid, string suffix)
    {
        var logs = window.Dodoco;
        var page = Find<DodocoPage>(window)!;
        var realtime = Assert.IsType<RichTextBox>(page.FindName("RealtimeLog"));
        var prefix = "实时窗口回归-" + suffix + "-";
        var sequence = 0;
        List<string> Lines(int count, string level = "INF") => Enumerable.Range(0, count)
            .Select(_ => $"[19:12:00.000] [{level}] [Primary:S1:P1:T1] RealtimeRegression | {prefix}{sequence++:D4}")
            .ToList();

        async Task SendBatchAsync(List<string> lines)
        {
            // 正式服务端每秒最多 4 批；保持低于限流的合批节奏，等待的是内容而非渲染耗时阈值。
            await Task.Delay(350);
            await executor.ReportMemberLogBatchAsync(lines, false).WaitAsync(TimeSpan.FromSeconds(5));
            var lastMessage = MemberLogLineCodec.Parse(lines[^1], DateTime.Now, "远程:回归").Message;
            await Until(() => logs.VisibleEntries.Any(e => e.Message == lastMessage)
                && !logs.IsLogLoading && RealtimeMatchesVisible(realtime, logs));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        logs.ClearViewCommand.Execute(null);
        logs.AutoFollow = false;
        await SendBatchAsync(Lines(500));
        Assert.Equal(500, logs.VisibleEntries.Count);
        var document = realtime.Document;
        var originalParagraphs = document.Blocks.Cast<Paragraph>().ToArray();
        var readingParagraph = originalParagraphs[250];
        realtime.Selection.Select(readingParagraph.ContentStart, readingParagraph.ContentEnd);
        var readingText = realtime.Selection.Text;
        realtime.UpdateLayout();
        var scroller = Find<ScrollViewer>(realtime)!;
        Assert.True(scroller.ScrollableHeight > 0, "500 条日志应形成可滚动的真实阅读视窗。");
        scroller.ScrollToVerticalOffset(scroller.ScrollableHeight / 2);
        realtime.UpdateLayout();
        logs.AutoFollow = false;
        var readingTop = readingParagraph.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;

        var loadingStarts = 0;
        void TrackLoading(object? sender, PropertyChangedEventArgs e)
        { if (e.PropertyName == nameof(DodocoViewModel.IsLogLoading) && logs.IsLogLoading) loadingStarts++; }
        logs.PropertyChanged += TrackLoading;
        try
        {
            // 最小反例：旧版在 >8 条批次到达后替换 FlowDocument，先单独锁住这条回归。
            await SendBatchAsync(Lines(12));
            Assert.Same(document, realtime.Document);
            Assert.Equal(0, loadingStarts);

            // 继续三批，覆盖裁剪头部后的重叠段落、选择和关闭跟尾时的阅读状态。
            for (var i = 0; i < 3; i++)
            {
                await SendBatchAsync(Lines(12));
                Assert.Same(document, realtime.Document);
                Assert.Equal(0, loadingStarts);
                Assert.False(logs.AutoFollow);
                Assert.Equal(readingText, realtime.Selection.Text);
                Assert.Contains(readingParagraph, document.Blocks.Cast<Paragraph>());
                realtime.UpdateLayout();
                Assert.InRange(readingParagraph.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top,
                    readingTop - 2, readingTop + 2);
                Assert.True(scroller.VerticalOffset < scroller.ScrollableHeight - 2,
                    "关闭自动滚动后，实时批次不能把阅读位置拉回最新日志。");
            }
            Assert.Equal(originalParagraphs.Skip(48), document.Blocks.Cast<Paragraph>().Take(452));
            Assert.Equal(500, document.Blocks.Count);
            Snapshot(window, Path.Combine(AssistantDataDirectory.Root, "realtime-log-continuous-reading.png"));

            // 显式筛选仍需替换视图；在初始分批排版尚未获得 dispatcher 下一拍时送入实时批次。
            logs.ShowInf = false;
            Assert.Empty(logs.VisibleEntries);
            Assert.Empty(realtime.Document.Blocks.Cast<Paragraph>());
            var beforeInitialLoad = loadingStarts;
            logs.ShowInf = true;
            Assert.True(logs.IsLogLoading);
            var initialDocument = realtime.Document;
            var duringInitialLoad = Lines(12).Select(line =>
                MemberLogLineCodec.Parse(line, DateTime.Now, "远程:初始加载回归")).ToList();
            // 与真实网络解析完成后的同一入口，确定性覆盖“排版期间到新批”，避免依赖网络竞速。
            typeof(DodocoViewModel).GetMethod("AddRemoteEntries", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(logs, [remoteUid, duringInitialLoad]);
            await Until(() => !logs.IsLogLoading && RealtimeMatchesVisible(realtime, logs));
            Assert.Same(initialDocument, realtime.Document);
            Assert.Equal(beforeInitialLoad + 1, loadingStarts);
            Assert.Equal(duringInitialLoad[^1].Message, logs.VisibleEntries[^1].Message);
            Assert.Equal(500, realtime.Document.Blocks.Count);

            // 暂停继续仍按原语义缓冲、一次追平，暂停期间保留原文档与正在阅读的内容。
            logs.IsPaused = true;
            var pausedDocument = realtime.Document;
            var pausedText = new TextRange(pausedDocument.ContentStart, pausedDocument.ContentEnd).Text;
            var received = logs.TotalReceived;
            var pausedLines = Lines(12);
            await Task.Delay(350);
            await executor.ReportMemberLogBatchAsync(pausedLines, false).WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => logs.TotalReceived >= received + pausedLines.Count);
            Assert.Same(pausedDocument, realtime.Document);
            Assert.Equal(pausedText, new TextRange(realtime.Document.ContentStart, realtime.Document.ContentEnd).Text);
            logs.IsPaused = false;
            var resumedMessage = MemberLogLineCodec.Parse(pausedLines[^1], DateTime.Now, "远程:回归").Message;
            await Until(() => !logs.IsLogLoading && RealtimeMatchesVisible(realtime, logs)
                && logs.VisibleEntries[^1].Message == resumedMessage);

            // 筛选和来源切换必须继续生效，不能为保留文档而混入旧来源或被隐藏的级别。
            logs.ShowInf = false;
            Assert.Empty(logs.VisibleEntries);
            var debugEntries = Lines(12, "DBG").Select(line =>
                MemberLogLineCodec.Parse(line, DateTime.Now, "远程:筛选回归")).ToList();
            typeof(DodocoViewModel).GetMethod("AddRemoteEntries", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(logs, [remoteUid, debugEntries]);
            Assert.Empty(logs.VisibleEntries);
            logs.ShowDbg = true;
            await Until(() => !logs.IsLogLoading && RealtimeMatchesVisible(realtime, logs));
            Assert.Equal(12, logs.VisibleEntries.Count);
            Assert.All(logs.VisibleEntries, e => Assert.Equal(LogLevels.Dbg, e.Level));
            logs.SelectedSource = logs.LogSources.Single(s => s.Key == DodocoViewModel.LocalSourceKey);
            await Until(() => !logs.IsLogLoading && RealtimeMatchesVisible(realtime, logs));
            Assert.DoesNotContain(logs.VisibleEntries, e => e.Message.StartsWith(prefix));
            logs.SelectedSource = logs.LogSources.Single(s => s.Key == remoteUid);
            await Until(() => !logs.IsLogLoading && RealtimeMatchesVisible(realtime, logs));
            Assert.Equal(debugEntries.Select(e => e.Message), logs.VisibleEntries.Select(e => e.Message));
            logs.ShowDbg = false;
            logs.ShowInf = true;
            await Until(() => !logs.IsLogLoading && RealtimeMatchesVisible(realtime, logs));
        }
        finally { logs.PropertyChanged -= TrackLoading; }
    }

    /// <summary>追加隔离本机日志文件，实际经过 tail、200ms 合帧和跟尾，不使用远程批次替代本机路径。</summary>
    private static async Task VerifyLocalRealtimeTailAsync(MainWindow window, string logPath, string suffix)
    {
        var logs = window.Dodoco;
        var page = Find<DodocoPage>(window)!;
        var realtime = Assert.IsType<RichTextBox>(page.FindName("RealtimeLog"));
        var tail = (BgiLogTailService)typeof(DodocoViewModel)
            .GetField("_tailService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(logs)!;
        // 文件可能在 VM 订阅 TargetFileChanged 前已打开；直接等待读取服务就绪，再验证真实新增行。
        await Until(() => tail.CurrentFilePath == logPath
            && !logs.IsLogLoading && RealtimeMatchesVisible(realtime, logs));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        logs.AutoFollow = true;
        var document = realtime.Document;
        var prefix = "本机实时跟尾回归-" + suffix + "-";
        var sequence = 0;
        // 长行通过正式 RichTextBox 自动换行，让 13 条日志也形成可滚动视窗，实际检查跟尾位置。
        List<string> Messages(int count) => Enumerable.Range(0, count)
            .Select(_ => prefix + $"{sequence++:D4} " + new string('测', 500)).ToList();
        async Task AppendAndWaitAsync(List<string> messages)
        {
            var text = string.Concat(messages.Select(message =>
                "[19:13:00.000] [INF] [Primary:S1:P1:T1] RealtimeLocalRegression\n" + message + "\n"));
            await File.AppendAllTextAsync(logPath, text).WaitAsync(TimeSpan.FromSeconds(5));
            // 多行事件的末条需等待 tail 的空闲冲刷；沿用有界内容等待，不断言运行耗时。
            await Until(() => logs.VisibleEntries.Any(e => e.Message == messages[^1])
                && !logs.IsLogLoading && RealtimeMatchesVisible(realtime, logs));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            realtime.UpdateLayout();
            Assert.Same(document, realtime.Document);
            Assert.True(logs.AutoFollow);
            var scroller = Find<ScrollViewer>(realtime)!;
            Assert.InRange(scroller.ScrollableHeight - scroller.VerticalOffset, 0, 2);
        }

        var loadingStarts = 0;
        void TrackLoading(object? sender, PropertyChangedEventArgs e)
        { if (e.PropertyName == nameof(DodocoViewModel.IsLogLoading) && logs.IsLogLoading) loadingStarts++; }
        logs.PropertyChanged += TrackLoading;
        try
        {
            var first = Messages(1);
            await AppendAndWaitAsync(first);
            var retainedParagraph = Assert.Single(document.Blocks.Cast<Paragraph>().Where(paragraph =>
                new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Contains(first[0])));
            await AppendAndWaitAsync(Messages(12));
            Assert.Contains(retainedParagraph, document.Blocks.Cast<Paragraph>());
            Assert.Equal(13, logs.VisibleEntries.Count(e => e.Message.StartsWith(prefix)));
            Assert.True(Find<ScrollViewer>(realtime)!.ScrollableHeight > 0,
                "本机长日志应形成可滚动视窗，才能实际验证自动跟尾。");
            Assert.Equal(0, loadingStarts);
        }
        finally { logs.PropertyChanged -= TrackLoading; }
    }

    private static bool RealtimeMatchesVisible(RichTextBox realtime, DodocoViewModel logs)
    {
        var paragraphs = realtime.Document.Blocks.Cast<Paragraph>().ToArray();
        return paragraphs.Length == logs.VisibleEntries.Count && paragraphs.Select((paragraph, i) =>
            new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Contains(logs.VisibleEntries[i].Message))
            .All(matches => matches);
    }
}
