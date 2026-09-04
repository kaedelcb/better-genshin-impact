using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 嘟嘟可页面代码后置。只放 MVVM 绑定做不到的滚动交互：
/// - 实时日志：自动跟尾 / 用户上翻暂停跟尾 / 滚回底部恢复；
/// - 日志浏览：滚动接近边界按需加载相邻块、顶部前插时保持视口不跳动、跳转后滚动到选中行。
/// </summary>
public partial class DodocoPage : UserControl
{
    /// <summary>浏览列表上一次的首行引用：用于区分"前插"（需保持视口）与"追加/整页替换"。</summary>
    private LogLineItem? _browserFirstLine;
    private bool _scrollHooked;

    public DodocoPage()
    {
        InitializeComponent();
        // DataContext 由 MainWindow 在 XAML 中绑定注入（DodocoViewModel），Loaded 时挂跳转滚动事件
        Loaded += (_, _) =>
        {
            if (_scrollHooked) return;
            if (DataContext is DodocoViewModel vm)
            {
                vm.Browser.ScrollToSelectionRequested += OnBrowserScrollToSelection;
                _scrollHooked = true;
            }
        };
    }

    // ========== 实时日志：自动跟尾 ==========

    private void RealtimeList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not DodocoViewModel vm) return;
        var sv = e.OriginalSource as ScrollViewer ?? FindScrollViewer(RealtimeList);
        if (sv == null) return;

        if (e.ExtentHeightChange > 0)
        {
            // 新日志到达：跟尾开启时滚到底
            if (vm.AutoFollow) sv.ScrollToEnd();
        }
        else if (e.VerticalChange != 0 || e.ViewportHeightChange != 0)
        {
            // 用户滚动/视口变化：离开底部 → 暂停跟尾；回到底部 → 恢复跟尾
            var atBottom = sv.VerticalOffset >= sv.ScrollableHeight - 2;
            if (vm.AutoFollow != atBottom) vm.AutoFollow = atBottom;
        }
    }

    // ========== 日志浏览：边界加载与视口保持 ==========

    private void BrowserList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not DodocoViewModel vm) return;
        var sv = e.OriginalSource as ScrollViewer ?? FindScrollViewer(BrowserList);
        if (sv == null) return;

        // 顶部前插了一块旧内容：ExtentHeight 增加且首行变化 → 视口下移同等高度，内容不跳
        var first = vm.Browser.Lines.Count > 0 ? vm.Browser.Lines[0] : null;
        if (e.ExtentHeightChange > 0 && sv.VerticalOffset > 0 && !ReferenceEquals(first, _browserFirstLine))
            sv.ScrollToVerticalOffset(sv.VerticalOffset + e.ExtentHeightChange);
        _browserFirstLine = first;

        // 用户滚动接近边界 → 按需加载相邻块
        if (e.VerticalChange != 0)
        {
            if (sv.VerticalOffset < 40) vm.Browser.LoadOlder();
            else if (sv.ScrollableHeight > 0 && sv.VerticalOffset > sv.ScrollableHeight - 40)
                vm.Browser.LoadNewer();
        }
    }

    /// <summary>跳转/搜索后：把选中行滚动到可见；无选中行（整页加载完成）时滚到底部看最新。</summary>
    private void OnBrowserScrollToSelection()
    {
        if (DataContext is not DodocoViewModel vm) return;
        BrowserList.UpdateLayout();
        var sel = vm.Browser.SelectedLine;
        if (sel != null)
        {
            BrowserList.ScrollIntoView(sel);
        }
        else if (vm.Browser.Lines.Count > 0)
        {
            BrowserList.ScrollIntoView(vm.Browser.Lines[^1]);
        }
        _browserFirstLine = vm.Browser.Lines.Count > 0 ? vm.Browser.Lines[0] : null;
    }

    /// <summary>可视树查找 ListBox 内部的 ScrollViewer。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
}
