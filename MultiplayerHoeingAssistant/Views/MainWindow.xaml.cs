using System.Windows;
using System.Reflection;
using MultiplayerHoeingAssistant.Helpers;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    // 标签区折叠：标签按钮全部由代码动态生成到普通 WrapPanel（Tag="group"/"oneclick"）中，
    // "更多"按钮作为流式布局的普通子元素紧跟第 maxLines 行末尾（不单独占一行、不增加高度）。
    // _tagFoldBusy：ApplyTagFold 执行期间（含 UpdateLayout）忽略 SizeChanged，防重入；
    // _foldActiveMap：折叠成功后置位，折叠导致的高度变化触发的 SizeChanged 一律忽略（防振荡），
    //   直到窗口缩放（MainWindow_SizeChanged）或成员刷新（DataContextChanged）才解锁重算。
    private bool _tagFoldBusy;
    private readonly System.Collections.Generic.Dictionary<System.Windows.Controls.WrapPanel, bool> _foldActiveMap = new();
    private readonly System.Collections.Generic.HashSet<System.Windows.Controls.WrapPanel> _tagRebuildPending = new();
    private readonly System.Collections.Generic.HashSet<MemberViewModel> _memberHooked = new();

    private bool IsFoldActive(System.Windows.Controls.WrapPanel wp) => _foldActiveMap.TryGetValue(wp, out var v) && v;

    private void SetFoldActive(System.Windows.Controls.WrapPanel wp, bool v) => _foldActiveMap[wp] = v;

    public MainWindow(MainViewModel viewModel)
    {
        DpiAwarenessController.Initialize(this);
        TagLog($"[app start] {System.DateTime.Now:HH:mm:ss.fff}");
        ViewModel = viewModel;
        DataContext = ViewModel;
        InitializeComponent();
        // 动态设置标题：Nexus-BGI · 版本号
        Title = GetVersionedTitle();
        // 用户缩放窗口 → 行数可能变化 → 解锁并重新计算折叠
        SizeChanged += MainWindow_SizeChanged;
        // 首次内容渲染完成后强制重建标签（Loaded 触发太早：此时 TagWrap 宽度仍为 0，
        // rebuild 的按钮在真实布局前不可见；ContentRendered 保证首帧渲染完成、宽度定型后再重建）
        ContentRendered += MainWindow_ContentRendered;
        // 兜底：成员集合一到就强制重建标签区（成员来自 SignalR 异步推送，开窗首帧 members=0；
        // 成员到达后靠 DataContextChanged 重建有时序缺口，改窗大小(SizeChanged)才显示——这里直接监听集合变化）
        ViewModel.Members.CollectionChanged += (_, _) =>
        {
            _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                new System.Action(() =>
                {
                    var wraps = FindTagWrapPanels(this);
                    TagLog($"[MembersChanged] rebuild wraps={wraps.Count}");
                    foreach (var wp in wraps)
                    {
                        SetFoldActive(wp, false);
                        ScheduleTagRebuild(wp);
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
        };
        // 刷新完成后重建标签区 + 强制刷新成员卡片 UI，实现"重新加载页面"效果
        ViewModel.RefreshCompleted += () =>
        {
            _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                new System.Action(RefreshTagUI), System.Windows.Threading.DispatcherPriority.Background);
        };
    }

    /// <summary>重建所有标签 WrapPanel + 刷新成员卡片 UI。</summary>
    private void RefreshTagUI()
    {
        // 强制触发每个成员全属性通知，刷新状态徽章/绑定（即使内容未变也强制重建）
        foreach (var member in ViewModel.Members)
        {
            member.NotifyAllPropertiesChanged();
        }
        // 重建所有标签区（解锁折叠 + 调度重建）
        var wraps = FindTagWrapPanels(this);
        TagLog($"[RefreshCompleted] rebuild wraps={wraps.Count} members={ViewModel.Members.Count}");
        foreach (var wp in wraps)
        {
            SetFoldActive(wp, false);
            ScheduleTagRebuild(wp);
        }
    }

    private void MainWindow_ContentRendered(object? sender, System.EventArgs e)
    {
        var wraps = FindTagWrapPanels(this);
        TagLog($"[ContentRendered] members={ViewModel.Members.Count} wraps={wraps.Count} loaded={IsLoaded}");
        foreach (var wp in wraps)
        {
            SetFoldActive(wp, false);
            ScheduleTagRebuild(wp);
        }
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        foreach (var wp in FindTagWrapPanels(this))
        {
            SetFoldActive(wp, false);
            ScheduleTagRebuild(wp);
        }
    }

    private void TagWrap_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        if (_tagFoldBusy) return;
        if (sender is System.Windows.Controls.WrapPanel wp && wp.Tag is string)
        {
            if (IsFoldActive(wp)) return; // 折叠态：忽略自身高度变化，防振荡
            ScheduleTagRebuild(wp);
        }
    }

    private void TagWrap_DataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        // 成员列表刷新（OnPlayersUpdated 每次重建 MemberViewModel）→ DataContext 变化 → 解锁并重建标签
        if (sender is System.Windows.Controls.WrapPanel wp && wp.Tag is string)
        {
            SetFoldActive(wp, false);
            ScheduleTagRebuild(wp);
        }
    }

    private void ScheduleTagRebuild(System.Windows.Controls.WrapPanel wp)
    {
        if (!_tagRebuildPending.Add(wp)) return; // 防抖去重
        System.Windows.Threading.DispatcherTimer? timer = null;
        timer = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer = null;
            _tagRebuildPending.Remove(wp);
            RebuildTagButtons(wp);
        };
        timer.Start();
    }

    private static string TagLogPath =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "assist_tag_debug.log");
    private static void TagLog(string msg)
    {
        try { System.IO.File.AppendAllText(TagLogPath, $"{System.DateTime.Now:HH:mm:ss.fff} {msg}{System.Environment.NewLine}"); }
        catch { }
    }

    private void RebuildTagButtons(System.Windows.Controls.WrapPanel wp)
    {
        TagLog($"RebuildTagButtons enter tag={wp.Tag} dcNull={wp.DataContext == null} w={wp.ActualWidth:F0} busy={_tagFoldBusy}");
        if (_tagFoldBusy) return;
        if (wp.Tag is not string type) return;
        if (wp.DataContext is not MemberViewModel member) return;
        _tagFoldBusy = true;
        try
        {
            SetFoldActive(wp, false); // 重建即从展开态开始，折叠结果由 ApplyTagFold 决定
            // 成员配置列表变化（增量更新时属性替换）→ 只重建对应标签区，不重建整卡
            if (_memberHooked.Add(member))
            {
                member.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(MemberViewModel.ConfigGroups)
                        && e.PropertyName != nameof(MemberViewModel.OneClickConfigs)) return;
                    foreach (var w in FindTagWrapPanels(this))
                    {
                        if (w.DataContext != member || w.Tag is not string t) continue;
                        bool isOne = t == "oneclick";
                        bool match = isOne
                            ? e.PropertyName == nameof(MemberViewModel.OneClickConfigs)
                            : e.PropertyName == nameof(MemberViewModel.ConfigGroups);
                        if (match)
                        {
                            SetFoldActive(w, false);
                            ScheduleTagRebuild(w);
                        }
                    }
                };
            }
            bool isOneClick = type == "oneclick";
            var items = isOneClick ? member.OneClickConfigs : member.ConfigGroups;
            int maxLines = isOneClick ? 2 : 3;

            wp.Children.Clear();
            if (items.Count == 0) { TagLog($"  -> items=0 type={type}"); return; }

            var buttons = new System.Collections.Generic.List<System.Windows.Controls.Button>();
            var tagStyle = (System.Windows.Style)FindResource(isOneClick ? "TagOneClickPill" : "TagGroupPill");
            foreach (var name in items)
            {
                var b = new System.Windows.Controls.Button
                {
                    Content = name,
                    Style = tagStyle,
                    Tag = member,
                    Margin = new System.Windows.Thickness(0, 0, 5, 5)
                };
                b.Click += (_, _) =>
                {
                    if (isOneClick) _ = ViewModel.StartOneClickFromConfigAsync(member, name);
                    else _ = ViewModel.StartGroupFromConfigAsync(member, name);
                };
                buttons.Add(b);
                wp.Children.Add(b);
            }

            // 同步布局并立即折叠：整个"生成→测量→折叠"在同一个同步块内完成，
            // 渲染时已经是折叠态，不会出现"全部展开"的中间帧（BeginInvoke 异步折叠会闪）
            wp.UpdateLayout();
            FoldTagButtons(wp, buttons, maxLines, isOneClick, member);
        }
        finally
        {
            _tagFoldBusy = false;
        }
    }

    private void FoldTagButtons(System.Windows.Controls.WrapPanel wp,
        System.Collections.Generic.List<System.Windows.Controls.Button> buttons, int maxLines, bool isOneClick, MemberViewModel member)
    {
        // 在 RebuildTagButtons 的 _tagFoldBusy 块内同步调用，无需重入检查
            // 1. 测量每个标签的行位置
            var rows = new System.Collections.Generic.List<(System.Windows.Controls.Button b, double top, double h)>();
            foreach (var b in buttons)
            {
                var top = b.TranslatePoint(new System.Windows.Point(0, 0), wp).Y;
                var h = b.ActualHeight;
                rows.Add((b, top, h));
            }
            double lineHeight = rows.Max(r => r.h) + 5; // 标签 Margin 底部 5
            if (lineHeight <= 0) lineHeight = 26;
            double maxHeight = maxLines * lineHeight;

            // 2. 确定第 maxLines 行内能保留的标签数
            int keepCount = rows.Count;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].top + lineHeight > maxHeight + 1) { keepCount = i; break; }
            }
            TagLog($"FoldTagButtons w={wp.ActualWidth:F0} buttons={buttons.Count} rows={rows.Count} maxHeight={maxHeight:F0} keepCount={keepCount}");
            if (keepCount >= rows.Count) { TagLog($"  -> 未超行，无需折叠"); return; } // 未超行，无需折叠

            // 3. 移除超出的标签
            for (int i = keepCount; i < buttons.Count; i++) wp.Children.Remove(buttons[i]);

            // 4. 添加"更多"按钮，保证它落在第 maxLines 行内末尾：
            //    第 maxLines 行放不下时，逐次回收该行最后一个标签为按钮腾位置（不额外占行高）
            while (keepCount >= 0)
            {
                var moreBtn = CreateMoreButton(buttons.Count, member, isOneClick);
                wp.Children.Add(moreBtn);
                wp.UpdateLayout();
                var moreTop = moreBtn.TranslatePoint(new System.Windows.Point(0, 0), wp).Y;
                // 判断"按钮在第 maxLines 行内"：第 maxLines+1 行的 top 恰好等于 maxHeight，
                // 必须用严格小于（不带 +1 容差），否则按钮掉到第 4 行（top == maxHeight）会被误判为已放下
                if (moreTop < maxHeight || keepCount == 0) break; // 放得下，或已无可回收的标签
                wp.Children.Remove(moreBtn);
                keepCount--;
                if (keepCount > 0) wp.Children.Remove(buttons[keepCount - 1]); // 回收最后一个可见标签
            }
            SetFoldActive(wp, true); // 进入折叠态：后续自身高度变化触发的 SizeChanged 忽略，防振荡
    }

    private System.Windows.Controls.Button CreateMoreButton(int total, MemberViewModel member, bool isOneClick)
    {
        var moreBtn = new System.Windows.Controls.Button
        {
            Content = $"更多({total})",
            Style = (System.Windows.Style)FindResource("TagMorePill"),
            Tag = "tag_more_marker",
            Margin = new System.Windows.Thickness(0, 0, 5, 5)
        };
        string type = isOneClick ? "oneclick" : "group";
        moreBtn.Click += (s, e2) => ShowTagMoreDialog(member, type);
        return moreBtn;
    }

    private static System.Collections.Generic.List<System.Windows.Controls.WrapPanel> FindTagWrapPanels(System.Windows.DependencyObject root)
    {
        var result = new System.Collections.Generic.List<System.Windows.Controls.WrapPanel>();
        if (root is System.Windows.Controls.WrapPanel wp && wp.Tag is string tag && (tag == "group" || tag == "oneclick"))
            result.Add(wp);
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            result.AddRange(FindTagWrapPanels(System.Windows.Media.VisualTreeHelper.GetChild(root, i)));
        return result;
    }

    private void ShowTagMoreDialog(MemberViewModel? member, string type)
    {
        if (member == null) return;
        bool isOneClick = type == "oneclick";
        var items = isOneClick ? member.OneClickConfigs : member.ConfigGroups;
        string label = isOneClick ? "一条龙" : "配置组";
        var dlg = new System.Windows.Window
        {
            Title = $"{label}列表", Width = 480, Height = 420,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Owner = this,
            WindowStyle = System.Windows.WindowStyle.SingleBorderWindow,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            FontFamily = new System.Windows.Media.FontFamily("HarmonyOS Sans SC, Microsoft YaHei"),
            Background = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 0),
                EndPoint = new System.Windows.Point(0.5, 1),
                GradientStops =
                {
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x14, 0x15, 0x34), 0),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x22, 0x1F, 0x4E), 0.6),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x1B, 0x19, 0x43), 1)
                }
            }
        };
        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(18) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = $"「{member.PlayerName}」共 {items.Count} 个{label}", FontSize = 14, FontWeight = System.Windows.FontWeights.SemiBold, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D)), Margin = new System.Windows.Thickness(0, 0, 0, 10) });
        var wrap2 = new System.Windows.Controls.WrapPanel();
        var pillStyle = (System.Windows.Style)FindResource(isOneClick ? "TagOneClickPill" : "TagGroupPill");
        foreach (var item in items)
        {
            var btn2 = new System.Windows.Controls.Button
            {
                Content = item,
                Style = pillStyle,
                FontSize = 13,
                Margin = new System.Windows.Thickness(0, 0, 6, 6),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF2, 0xFA)),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0))
            };
            btn2.Click += (_, _) => { dlg.Close(); if (isOneClick) _ = ViewModel.StartOneClickFromConfigAsync(member, item); else _ = ViewModel.StartGroupFromConfigAsync(member, item); };
            wrap2.Children.Add(btn2);
        }
        panel.Children.Add(new System.Windows.Controls.ScrollViewer { Content = wrap2, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, MaxHeight = 300 });
        var closeBtn = new System.Windows.Controls.Button { Content = "关闭", Width = 80, Height = 30, Margin = new System.Windows.Thickness(0, 12, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xE6)),
            BorderThickness = new System.Windows.Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0)),
            Cursor = System.Windows.Input.Cursors.Hand };
        closeBtn.Click += (_, _) => dlg.Close();
        panel.Children.Add(closeBtn);
        dlg.Content = panel;
        dlg.ShowDialog();
    }

    /// <summary>
    /// 获取带版本号的窗口标题，如 "Nexus-BGI · 0.7.9"。
    /// 版本号来自 csproj 中定义的 &lt;Version&gt; 属性（通过反射读取 AssemblyInformationalVersion）。
    /// </summary>
    private static string GetVersionedTitle()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return $"Nexus-BGI · {version ?? "0.0.0"}";
    }
}
