using System;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoTrackPath.Model;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterGenshinImpact.View.Controls;

public partial class DomainSelector : UserControl
{
    public DomainSelector()
    {
        InitializeComponent();
        RebuildCountries();
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 重建 Countries 列表：标准国家 + 可选的"事件任务"伪分类 + 可选的"自定义"分类
    /// </summary>
    private void RebuildCountries()
    {
        var countries = MapLazyAssets.Get().CountryToDomains.Keys.Reverse().ToList();

        // 仅在显式开启时追加"事件任务"伪分类（如一条龙秘境选择），单机自动秘境页面默认不显示
        if (ShowEventTasks)
        {
            countries.Add(EventTaskCategory);
        }

        if (CustomDomains != null && CustomDomains.Count > 0)
        {
            countries.Add("自定义");
        }
        Countries = countries;
    }

    /// <summary>
    /// "事件任务"伪分类名称：选中后右列列出"首领讨伐""幽境危战"等独立任务标记。
    /// </summary>
    private const string EventTaskCategory = "事件任务";

    /// <summary>
    /// 控件卸载时清理事件处理器，防止内存泄漏
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        RemoveWindowMouseWheelHandler();
    }

    public List<string> Countries
    {
        get { return (List<string>)GetValue(CountriesProperty); }
        set { SetValue(CountriesProperty, value); }
    }

    public static readonly DependencyProperty CountriesProperty =
        DependencyProperty.Register("Countries", typeof(List<string>), typeof(DomainSelector), new PropertyMetadata(null));

    // Task 1.1: CustomDomains DependencyProperty
    public List<string> CustomDomains
    {
        get => (List<string>)GetValue(CustomDomainsProperty);
        set => SetValue(CustomDomainsProperty, value);
    }

    public static readonly DependencyProperty CustomDomainsProperty =
        DependencyProperty.Register(
            "CustomDomains",
            typeof(List<string>),
            typeof(DomainSelector),
            new PropertyMetadata(null, OnCustomDomainsChanged));

    /// <summary>
    /// 是否在下拉中显示"事件任务"分类（首领讨伐 / 幽境危战）。
    /// 默认 false，保证单机自动秘境等既有调用方行为不变；仅一条龙秘境选择显式开启。
    /// </summary>
    public bool ShowEventTasks
    {
        get => (bool)GetValue(ShowEventTasksProperty);
        set => SetValue(ShowEventTasksProperty, value);
    }

    public static readonly DependencyProperty ShowEventTasksProperty =
        DependencyProperty.Register(
            "ShowEventTasks",
            typeof(bool),
            typeof(DomainSelector),
            new PropertyMetadata(false, OnShowEventTasksChanged));

    private static void OnShowEventTasksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((DomainSelector)d).RebuildCountries();
    }

    private static void OnCustomDomainsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (DomainSelector)d;
        control.RebuildCountries();

        // 如果当前选中的是"自定义"分类，刷新右侧列表
        if (control.SelectedCountry == "自定义")
        {
            OnSelectedCountryChanged(d, new DependencyPropertyChangedEventArgs(
                SelectedCountryProperty, null, "自定义"));
        }
    }

    public string SelectedCountry
    {
        get { return (string)GetValue(SelectedCountryProperty); }
        set { SetValue(SelectedCountryProperty, value); }
    }

    public static readonly DependencyProperty SelectedCountryProperty =
        DependencyProperty.Register("SelectedCountry", typeof(string), typeof(DomainSelector), new PropertyMetadata(null, OnSelectedCountryChanged));

    private static void OnSelectedCountryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (DomainSelector)d;
        var country = (string)e.NewValue;
        if (string.IsNullOrEmpty(country))
        {
            control.FilteredDomains = new List<Tuple<string, string>>();
        }
        else if (country == EventTaskCategory)
        {
            // "事件任务"伪分类：右列列出各独立任务标记（显示名 = 存储值）
            control.FilteredDomains = new List<Tuple<string, string>>
            {
                new(OneDragonFlowConfig.BossEventMarker, OneDragonFlowConfig.BossEventMarker),
                new(OneDragonFlowConfig.StygianEventMarker, OneDragonFlowConfig.StygianEventMarker),
            };
        }
        else if (country == "自定义")
        {
            // Build list from CustomDomains
            if (control.CustomDomains != null && control.CustomDomains.Count > 0)
            {
                control.FilteredDomains = control.CustomDomains
                    .Select(name => new Tuple<string, string>(name, name))
                    .ToList();
            }
            else
            {
                control.FilteredDomains = new List<Tuple<string, string>>();
            }
        }
        else
        {
            if (MapLazyAssets.Get().CountryToDomains.TryGetValue(country, out var domains))
            {
                // Reverse the list for display
                control.FilteredDomains = domains
                    .Select(tp => new Tuple<string, string>(tp.Name! + " | " + string.Join(" ", tp.Rewards), tp.Name!))
                    .Reverse()
                    .ToList();
            }
            else
            {
                control.FilteredDomains = new List<Tuple<string, string>>();
            }
        }
    }

    public List<Tuple<string, string>> FilteredDomains
    {
        get { return (List<Tuple<string, string>>)GetValue(FilteredDomainsProperty); }
        set { SetValue(FilteredDomainsProperty, value); }
    }

    public static readonly DependencyProperty FilteredDomainsProperty =
        DependencyProperty.Register("FilteredDomains", typeof(List<Tuple<string, string>>), typeof(DomainSelector), new PropertyMetadata(null));

    public string SelectedDomain
    {
        get { return (string)GetValue(SelectedDomainProperty); }
        set { SetValue(SelectedDomainProperty, value); }
    }

    public static readonly DependencyProperty SelectedDomainProperty =
        DependencyProperty.Register("SelectedDomain", typeof(string), typeof(DomainSelector), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedDomainChanged));

    private static void OnSelectedDomainChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (DomainSelector)d;
        var domain = (string)e.NewValue;

        if (string.IsNullOrEmpty(domain)) return;

        // 事件任务标记（首领讨伐 / 幽境危战）反查到"事件任务"伪分类
        if (domain == OneDragonFlowConfig.BossEventMarker || domain == OneDragonFlowConfig.StygianEventMarker)
        {
            if (control.SelectedCountry != EventTaskCategory)
            {
                control.SelectedCountry = EventTaskCategory;
            }
            return;
        }

        // First try standard domain reverse lookup
        var country = MapLazyAssets.Get().GetCountryByDomain(domain);
        if (country != null && country != control.SelectedCountry)
        {
            control.SelectedCountry = country;
        }
        // If standard lookup failed, check custom domains
        else if (country == null && control.CustomDomains != null && control.CustomDomains.Contains(domain))
        {
            if (control.SelectedCountry != "自定义")
            {
                control.SelectedCountry = "自定义";
            }
        }
    }

    private void DomainListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainToggle.IsChecked == true)
        {
            MainToggle.IsChecked = false;
        }
    }

    /// <summary>
    /// Popup 打开时添加全局滚轮事件拦截
    /// </summary>
    private void MainPopup_Opened(object sender, EventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window != null)
        {
            window.PreviewMouseWheel -= Window_PreviewMouseWheel;
            window.PreviewMouseWheel += Window_PreviewMouseWheel;
        }
    }

    /// <summary>
    /// Popup 关闭时移除全局滚轮事件拦截
    /// </summary>
    private void MainPopup_Closed(object sender, EventArgs e)
    {
        RemoveWindowMouseWheelHandler();
    }

    /// <summary>
    /// 移除窗口级滚轮事件处理器
    /// </summary>
    private void RemoveWindowMouseWheelHandler()
    {
        var window = Window.GetWindow(this);
        if (window != null)
        {
            window.PreviewMouseWheel -= Window_PreviewMouseWheel;
        }
    }

    /// <summary>
    /// 全局滚轮事件处理，当 Popup 打开时拦截所有滚轮事件
    /// </summary>
    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (MainPopup.IsOpen)
        {
            e.Handled = true;

            var scrollViewer1 = FindScrollViewer(CountriesListView);
            var scrollViewer2 = FindScrollViewer(DomainsListView);

            if (scrollViewer1 != null && scrollViewer1.IsMouseOver)
            {
                scrollViewer1.ScrollToVerticalOffset(scrollViewer1.VerticalOffset - e.Delta / 2.0);
                return;
            }

            if (scrollViewer2 != null && scrollViewer2.IsMouseOver)
            {
                scrollViewer2.ScrollToVerticalOffset(scrollViewer2.VerticalOffset - e.Delta / 2.0);
                return;
            }
        }
    }

    /// <summary>
    /// 处理 Popup 内的鼠标滚轮事件，防止滚动穿透到外部页面
    /// </summary>
    private void PopupBorder_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;

        var scrollViewer1 = FindScrollViewer(CountriesListView);
        var scrollViewer2 = FindScrollViewer(DomainsListView);

        if (scrollViewer1 != null && scrollViewer1.IsMouseOver)
        {
            scrollViewer1.ScrollToVerticalOffset(scrollViewer1.VerticalOffset - e.Delta / 2.0);
            return;
        }

        if (scrollViewer2 != null && scrollViewer2.IsMouseOver)
        {
            scrollViewer2.ScrollToVerticalOffset(scrollViewer2.VerticalOffset - e.Delta / 2.0);
            return;
        }
    }

    /// <summary>
    /// 在视觉树中查找 ScrollViewer
    /// </summary>
    private ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent == null) return null;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            var result = FindScrollViewer(child);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }
}
