using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 带关键字高亮的 TextBlock（日志浏览·搜索结果列表用）。
/// 命中片段以鎏金底色 + 深色文字标出；普通片段保持宿主样式。
/// 纯文本关键字按 OrdinalIgnoreCase 匹配；正则模式用 Regex.Matches（IgnoreCase），非法正则兜底纯文本。
/// </summary>
public class HighlightTextBlock : TextBlock
{
    /// <summary>命中的高亮底色（鎏金）。</summary>
    private static readonly Brush HighlightBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xAF, 0x37));
    /// <summary>命中片段的文字色（深底上反白为深色，保证可读）。</summary>
    private static readonly Brush HighlightFgBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x30));

    static HighlightTextBlock()
    {
        HighlightBrush.Freeze();
        HighlightFgBrush.Freeze();
    }

    public static readonly DependencyProperty SourceTextProperty =
        DependencyProperty.Register(nameof(SourceText), typeof(string), typeof(HighlightTextBlock),
            new FrameworkPropertyMetadata("", OnRebuild));

    /// <summary>要显示的文本（替代 Text；命中部分会高亮）。</summary>
    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public static readonly DependencyProperty HighlightPatternProperty =
        DependencyProperty.Register(nameof(HighlightPattern), typeof(string), typeof(HighlightTextBlock),
            new FrameworkPropertyMetadata("", OnRebuild));

    /// <summary>高亮词（空串=不高亮，直接显示纯文本）。</summary>
    public string HighlightPattern
    {
        get => (string)GetValue(HighlightPatternProperty);
        set => SetValue(HighlightPatternProperty, value);
    }

    public static readonly DependencyProperty IsRegexHighlightProperty =
        DependencyProperty.Register(nameof(IsRegexHighlight), typeof(bool), typeof(HighlightTextBlock),
            new FrameworkPropertyMetadata(false, OnRebuild));

    /// <summary>true=高亮词按正则解释。</summary>
    public bool IsRegexHighlight
    {
        get => (bool)GetValue(IsRegexHighlightProperty);
        set => SetValue(IsRegexHighlightProperty, value);
    }

    private static void OnRebuild(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((HighlightTextBlock)d).Rebuild();

    /// <summary>按当前高亮词重建 Inlines。</summary>
    private void Rebuild()
    {
        var text = SourceText ?? "";
        var pattern = HighlightPattern ?? "";
        Inlines.Clear();
        if (string.IsNullOrEmpty(pattern))
        {
            Inlines.Add(new Run(text));
            return;
        }

        foreach (var (segment, hit) in SplitSegments(text, pattern, IsRegexHighlight))
        {
            if (segment.Length == 0) continue;
            var run = new Run(segment);
            if (hit)
            {
                run.Background = HighlightBrush;
                run.Foreground = HighlightFgBrush;
                run.FontWeight = FontWeights.SemiBold;
            }
            Inlines.Add(run);
        }
        if (Inlines.Count == 0) Inlines.Add(new Run(text));
    }

    /// <summary>把文本切成 (片段, 是否命中) 列表；非法正则/异常兜底为整段不命中。日志浏览查看区渲染也复用。</summary>
    internal static List<(string Segment, bool Hit)> SplitSegments(string text, string pattern, bool isRegex)
    {
        // 空模式必须早退：非正则路径 IndexOf("", idx) 恒返回 idx，idx 零前进 → 死循环，
        // 每次迭代往 List 加一段 → 内存无限膨胀（2026-09-05 打开日志浏览卡死+34GB 内存的根因，
        // 由 hang dump UI 线程调用栈确诊：SplitSegments 内 List.AddWithResize）。
        // Rebuild() 虽已挡空模式，本方法属共用底层，必须在源头挡。
        if (string.IsNullOrEmpty(pattern))
            return new List<(string, bool)> { (text, false) };

        var segments = new List<(string Segment, bool Hit)>();
        try
        {
            if (isRegex)
            {
                var pos = 0;
                foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
                {
                    if (m.Length == 0) continue; // 零宽匹配忽略
                    if (m.Index > pos) segments.Add((text[pos..m.Index], false));
                    segments.Add((m.Value, true));
                    pos = m.Index + m.Length;
                }
                if (pos < text.Length) segments.Add((text[pos..], false));
                return segments;
            }

            var idx = 0;
            while (idx < text.Length)
            {
                var hit = text.IndexOf(pattern, idx, StringComparison.OrdinalIgnoreCase);
                if (hit < 0) break;
                if (hit > idx) segments.Add((text[idx..hit], false));
                segments.Add((text.Substring(hit, pattern.Length), true));
                idx = hit + pattern.Length;
            }
            if (idx < text.Length) segments.Add((text[idx..], false));
            if (segments.Count == 0) segments.Add((text, false));
            return segments;
        }
        catch
        {
            // 正则非法等情况：整段不高亮
            return new List<(string, bool)> { (text, false) };
        }
    }
}
