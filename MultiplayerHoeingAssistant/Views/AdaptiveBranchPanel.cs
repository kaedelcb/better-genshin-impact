using System.Windows;
using System.Windows.Controls;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 条件节点「是/否」双分支自适应面板：
/// 两条子链按内容实际宽度并排（是左否右，不再强制等宽对半分——空分支/窄分支只占自己需要的宽度，
/// 把空间让给内容多的分支）；两条链的自然宽度加起来放不下时，自动改为上下堆叠（是上否下），
/// 各自占满整宽。任何情况下子链都不会被压缩到内容截断，也不会溢出可视区。
/// 只取前两个子元素参与并排判断，其余子元素始终竖排追加（当前模板只用两个）。
/// </summary>
public class AdaptiveBranchPanel : Panel
{
    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(nameof(Gap), typeof(double), typeof(AdaptiveBranchPanel),
            new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>两列/两行之间的间距。</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    // 最近一次 Measure 决定的排布方式与列宽，Arrange 沿用，保证同一布局回合内一致
    private bool _stacked = true;
    private double _w0;
    private double _w1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var kids = InternalChildren;
        if (kids.Count == 0)
        {
            return new Size();
        }

        // 宽度无限（宿主给了横向滚动）或只有一个子元素：直接竖排
        if (kids.Count == 1 || double.IsInfinity(availableSize.Width))
        {
            _stacked = true;
            return MeasureStacked(availableSize);
        }

        var first = kids[0];
        var second = kids[1];

        // 先各自按整宽测量，取「内容自然宽度」（卡片不拉伸，返回值=内容实际需要宽度；
        // WrapPanel/换行文本会在整宽内自适应，返回值不会超过可用宽度）
        first.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        second.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        double c0 = first.DesiredSize.Width;
        double c1 = second.DesiredSize.Width;

        if (c0 + Gap + c1 <= availableSize.Width)
        {
            // 自然宽度放得下 → 按内容宽度并排，剩余空间留空也不挤压任何一边
            _stacked = false;
            _w0 = c0;
            _w1 = c1;
            // 按最终列宽重新测量，拿到换行后的正确高度（列宽 ≤ 上次测量宽度，不会反向变宽）
            first.Measure(new Size(_w0, double.PositiveInfinity));
            second.Measure(new Size(_w1, double.PositiveInfinity));
            return new Size(availableSize.Width,
                Math.Max(first.DesiredSize.Height, second.DesiredSize.Height));
        }

        // 并排必然截断 → 上下堆叠，各自用满整宽（嵌套层级越深越倾向堆叠，宽度不再逐层减半）
        _stacked = true;
        return MeasureStacked(availableSize);
    }

    private Size MeasureStacked(Size availableSize)
    {
        double h = 0, w = 0;
        foreach (UIElement kid in InternalChildren)
        {
            kid.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            h += kid.DesiredSize.Height;
            w = Math.Max(w, kid.DesiredSize.Width);
        }

        h += Gap * (InternalChildren.Count - 1);
        return new Size(double.IsInfinity(availableSize.Width) ? w : availableSize.Width, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var kids = InternalChildren;
        if (kids.Count == 0)
        {
            return finalSize;
        }

        if (!_stacked && kids.Count >= 2)
        {
            // 防御：最终宽度比测量时还小（理论上不会），列宽等比收缩
            double scale = finalSize.Width >= _w0 + Gap + _w1
                ? 1.0
                : Math.Max(0.0, (finalSize.Width - Gap) / Math.Max(1.0, _w0 + _w1));
            double w0 = _w0 * scale;
            double w1 = _w1 * scale;
            double h = Math.Max(kids[0].DesiredSize.Height, kids[1].DesiredSize.Height);
            kids[0].Arrange(new Rect(0, 0, w0, h));
            kids[1].Arrange(new Rect(w0 + Gap, 0, w1, h));
            return finalSize;
        }

        double y = 0;
        foreach (UIElement kid in kids)
        {
            kid.Arrange(new Rect(0, y, finalSize.Width, kid.DesiredSize.Height));
            y += kid.DesiredSize.Height + Gap;
        }

        return finalSize;
    }
}
