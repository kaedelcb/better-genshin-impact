using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MultiplayerHoeingAssistant.Helpers;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>Edits one saved local profile. The dialog never switches rooms or routes through a member card.</summary>
internal sealed class RoomOrchestrationEditorWindow : Window
{
    private static readonly Brush Gold = FrozenBrush(0xF2, 0xE3, 0xB6);
    private static readonly Brush White = FrozenBrush(0xF4, 0xF2, 0xFA);
    private static readonly Brush Dim = FrozenBrush(0xBC, 0xB7, 0xD8);
    private static readonly Brush Well = FrozenBrush(0x21, 0x1E, 0x41);
    private static readonly Brush Edge = FrozenBrush(0x62, 0x51, 0x6D);
    private readonly MainViewModel _vm;
    private readonly RoomOrchestrationEditorBackend? _remote;
    private bool _saveUnconfirmed;
    private string DirectoryOwner => _remote is null ? "本机" : "执行端";
    private readonly RoomOrchestrationEditSnapshot _opened;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _closedToken;
    private readonly ObservableCollection<RoomOrchestrationBinding> _selected;
    private readonly Dictionary<string, string> _quickCommands;
    private readonly CompletionActionPanel _completionPanel;
    private readonly StackPanel _body = new();
    private readonly TextBox _peopleBox;
    private readonly TextBox _scheduleBox;
    private readonly CheckBox _confirmSchedule;
    private readonly ListBox _bindingList;
    private readonly ComboBox _available;
    private readonly Button _add;
    private readonly Button _up;
    private readonly Button _down;
    private readonly Button _remove;
    private readonly Button _refresh;
    private readonly Button _save;
    private readonly TextBlock _directoryStatus;
    private readonly TextBlock _peerStatus;
    private readonly TextBlock _error;
    private readonly ScrollViewer _errorScroll;
    private readonly ComboBox _quickKey;
    private readonly ComboBox _quickTarget;
    private readonly Button _quickSet;
    private readonly TextBlock _quickSummary;
    private RoomOrchestrationNameCatalog? _names;
    private long _catalogRequest;
    private bool _refreshing;
    private bool _saving;
    private bool _closed;
    private Rect _workArea = Rect.Empty;
    private bool _placing;

    internal RoomOrchestrationEditorWindow(MainViewModel vm, RoomOrchestrationEditSnapshot opened,
        IReadOnlyList<RoomSwitchCompletion> switchChoices, Func<double, ComboBox> createCombo, Window owner,
        RoomOrchestrationEditorBackend? remote = null)
    {
        _vm = vm;
        _remote = remote;
        _opened = opened;
        _closedToken = _lifetime.Token;
        _selected = new(RoomOrchestrationBinding.From(opened.Names, opened.Types));
        _quickCommands = new(opened.QuickCommands);
        Owner = owner;
        Title = "编辑房间配置";
        Width = 600; Height = 740; MinWidth = 380; MinHeight = 300;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = owner.FontFamily;
        FontSize = 13;
        Background = owner.Background;
        Foreground = White;
        UseLayoutRounding = true;
        Resources.MergedDictionaries.Add(owner.Resources);
        if (owner.TryFindResource("GoldScrollViewer") is Style scrollStyle)
            Resources[typeof(ScrollViewer)] = scrollStyle;

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new(0, 0, 0, 10) };
        var headingTitle = Text($"编辑配置 · {opened.Name}", Gold, 17, true);
        headingTitle.MaxHeight = 48; headingTitle.TextTrimming = TextTrimming.CharacterEllipsis; headingTitle.ToolTip = headingTitle.Text;
        heading.Children.Add(headingTitle);
        // The full identity is also available when work-area constraints trim the fixed heading.
        var identity = Text($"房间号 {opened.Key.RoomCode} · {opened.Key.ServerUrl}", Dim, 12);
        identity.TextTrimming = TextTrimming.CharacterEllipsis; identity.MaxHeight = 38;
        identity.ToolTip = identity.Text;
        heading.Children.Add(identity);
        root.Children.Add(heading);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false, Padding = new Thickness(0, 0, 4, 0), Content = _body
        };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        _body.Children.Add(Text(_remote is null
            ? "仅编辑此执行端在该房间的本机配置，不切换房间、不修改其他成员。已受理批次继续使用冻结的原任务。"
            : $"正在编辑执行端：{_remote.ExecutorLabel}。保存至该执行端，不修改监控端配置。已受理批次继续使用冻结的原任务。", Dim, 12));

        var fields = new Grid { Margin = new(0, 4, 0, 8) };
        fields.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var peopleField = new StackPanel { Margin = new(0, 0, 8, 0) };
        peopleField.Children.Add(Text("预期开锄人数（1–4）", Gold));
        _peopleBox = MakeTextBox(opened.ExpectedPlayers.ToString(CultureInfo.InvariantCulture), "预期开锄人数");
        peopleField.Children.Add(_peopleBox); fields.Children.Add(peopleField);
        var scheduleField = new StackPanel { Margin = new(8, 0, 0, 0) };
        scheduleField.Children.Add(Text("定时上线（HH:mm）", Gold));
        _scheduleBox = MakeTextBox(opened.Schedule, "定时上线时间");
        scheduleField.Children.Add(_scheduleBox); Grid.SetColumn(scheduleField, 1); fields.Children.Add(scheduleField);
        _body.Children.Add(fields);
        _body.Children.Add(Text("留空禁用闹钟；显式重设已过时刻不会补触发今天的自动上线。修改其他字段不会解除定时抑制。", Dim, 11.5));
        _confirmSchedule = new CheckBox
        {
            Content = Text("确认重新设置当前定时（即使时间不变）", Gold, 12), Foreground = White,
            Margin = new Thickness(0, 2, 0, 5), IsChecked = false,
            Visibility = string.IsNullOrEmpty(opened.Schedule) ? Visibility.Collapsed : Visibility.Visible
        };
        AutomationProperties.SetName(_confirmSchedule, "明确确认当前定时上线设置");
        _body.Children.Add(_confirmSchedule);
        if (opened.ScheduleRequiresConfirmation || opened.ScheduleSuppressed)
            _body.Children.Add(Text("此档案的定时上线尚未启用或处于抑制状态。只有修改时间或勾选重新设置，才会改变该档案的授权。", Gold, 12));

        var bindingPanel = new StackPanel();
        _body.Children.Add(Section(bindingPanel));
        bindingPanel.Children.Add(Text("联机任务绑定 · 按顺序执行", Gold, 14, true));
        bindingPanel.Children.Add(Text("配置组和一条龙可混合绑定。所有成员在同轮中的项目数量、每个位置的类型和顺序必须一致；任务名称可以不同。", Dim, 11.5));
        _peerStatus = Text("", Dim, 11.5);
        UpdatePeerStatus(); bindingPanel.Children.Add(_peerStatus);
        _directoryStatus = Text($"正在读取{DirectoryOwner} BGI 任务目录…", Dim, 12);
        bindingPanel.Children.Add(_directoryStatus);
        _refresh = MakeButton($"刷新{DirectoryOwner}任务目录");
        _refresh.HorizontalAlignment = HorizontalAlignment.Left;
        _refresh.ToolTip = $"显式刷新会在必要时启动{DirectoryOwner} BGI，再读取名称列表；不会执行任务。";
        _refresh.Click += async (_, _) => await RefreshNamesAsync(true, updateCompletionPanel: true);
        bindingPanel.Children.Add(_refresh);

        _bindingList = new ListBox { ItemsSource = _selected, MinHeight = 56, MaxHeight = 180, Background = Well, Foreground = White,
            BorderBrush = Edge, BorderThickness = new Thickness(1), Margin = new Thickness(0, 7, 0, 4), ItemTemplate = WrappedLabelTemplate() };
        _bindingList.ItemContainerStyle = new Style(typeof(ListBoxItem));
        _bindingList.ItemContainerStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _bindingList.ItemContainerStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 5, 8, 5)));
        ScrollViewer.SetHorizontalScrollBarVisibility(_bindingList, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_bindingList, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(_bindingList, "已选联机任务，按列表顺序执行");
        _bindingList.SelectionChanged += (_, _) => UpdateBindingButtons();
        bindingPanel.Children.Add(_bindingList);
        var reorder = new WrapPanel { Margin = new Thickness(0, 1, 0, 6) };
        _up = MakeButton("上移", () => MoveBinding(-1));
        _down = MakeButton("下移", () => MoveBinding(1));
        _remove = MakeButton("移除", RemoveBinding);
        reorder.Children.Add(_up); reorder.Children.Add(_down); reorder.Children.Add(_remove); bindingPanel.Children.Add(reorder);
        bindingPanel.Children.Add(Text($"添加{DirectoryOwner}任务", Gold, 12));
        _available = createCombo(0); _available.ItemTemplate = WrappedLabelTemplate(380); _available.MinHeight = 32;
        AutomationProperties.SetName(_available, "可添加的本机配置组和一条龙");
        _add = MakeButton("添加", AddBinding);
        var addRow = new Grid(); addRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); addRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        addRow.Children.Add(_available); _add.Margin = new(6, 0, 0, 0); Grid.SetColumn(_add, 1); addRow.Children.Add(_add);
        _available.SelectionChanged += (_, _) => UpdateBindingButtons();
        bindingPanel.Children.Add(addRow);

        _completionPanel = new CompletionActionPanel(opened.Completion, [], [],
            "整轮联机正常完成后执行。切换房间时，原批次先按停止收尾；确认登录目标房间后再执行选定的后续动作。",
            (type, name, workflowId) => ReadCompletionAsync(type, name, workflowId, false, _closedToken),
            createCombo: () => createCombo(0), refreshCatalog: (type, name, workflowId, ct) =>
                ReadCompletionAsync(type, name, workflowId, true, ct), roomSwitchChoices: switchChoices);
        _body.Children.Add(_completionPanel.Root);

        var quickPanel = new StackPanel();
        _body.Children.Add(Section(quickPanel));
        quickPanel.Children.Add(Text("本房间的快捷指令", Gold, 14, true));
        _quickSummary = Text("", Dim, 12); quickPanel.Children.Add(_quickSummary);
        _quickKey = createCombo(0); _quickKey.ItemsSource = _quickCommands.Keys.Concat(AssistConfig.DefaultQuickCommands().Keys)
            .Distinct(StringComparer.Ordinal).ToArray(); _quickKey.MinHeight = 32;
        _quickTarget = createCombo(0); _quickTarget.MinHeight = 32; _quickTarget.ItemTemplate = WrappedLabelTemplate(380);
        AutomationProperties.SetName(_quickKey, "快捷指令"); AutomationProperties.SetName(_quickTarget, "快捷指令的本机任务目标");
        quickPanel.Children.Add(Text($"选择要编辑的指令及{DirectoryOwner}配置组或一条龙：", Dim, 11.5));
        quickPanel.Children.Add(_quickKey);
        _quickTarget.Margin = new Thickness(0, 5, 0, 0); quickPanel.Children.Add(_quickTarget);
        var quickButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        _quickSet = MakeButton("设置绑定", SetQuickBinding);
        quickButtons.Children.Add(_quickSet);
        quickButtons.Children.Add(MakeButton("清除绑定", () =>
        {
            if (_quickKey.SelectedItem is not string key) return;
            _quickCommands[key] = "";
            UpdateQuickSummary(); SelectQuickTarget();
        }));
        _quickKey.SelectionChanged += (_, _) => SelectQuickTarget();
        _quickTarget.SelectionChanged += (_, _) => _quickSet.IsEnabled = _quickTarget.SelectedItem is RoomOrchestrationBinding;
        _quickKey.SelectedIndex = 0;
        UpdateQuickSummary(); quickPanel.Children.Add(quickButtons);

        _error = Text("", Gold, 12);
        _error.Margin = new Thickness(0, 2, 0, 4);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        // Even long validation messages cannot extend above the fixed save/cancel row.
        _errorScroll = new ScrollViewer { MaxHeight = 72, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _error, Visibility = Visibility.Collapsed };
        Grid.SetRow(_errorScroll, 2); root.Children.Add(_errorScroll);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var cancel = MakeButton("取消", () => Close()); cancel.IsCancel = true; cancel.MinWidth = 80;
        _save = MakeButton("保存"); _save.MinWidth = 86; _save.IsDefault = true;
        if (owner.TryFindResource("BtnGold") is Style goldStyle) _save.Style = goldStyle;
        _save.Background = new LinearGradientBrush(Color.FromRgb(0xEF, 0xD6, 0x8A), Color.FromRgb(0xD4, 0xAF, 0x37), new Point(0, 0), new Point(1, 1));
        _save.Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x2F, 0x16));
        _save.Click += async (_, _) => await SaveAsync();
        footer.Children.Add(cancel); footer.Children.Add(_save); Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;
        SourceInitialized += (_, _) => FitToWorkArea(owner);
        LocationChanged += (_, _) => { if (IsLoaded) FitToWorkArea(); };
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(() => FitToWorkArea());
        Loaded += async (_, _) => await RefreshNamesAsync(false, updateCompletionPanel: true);
        Closing += (_, args) => { if (_remote is not null && _saving && DialogResult != true) args.Cancel = true; };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
        UpdateBindingButtons();
    }

    private void FitToWorkArea(Window? monitor = null)
    {
        if (_placing) return;
        var work = DpiAwarenessController.GetLogicalWorkAreaRect(this, monitor) ?? SystemParameters.WorkArea;
        if (work == _workArea) return;
        _placing = true;
        try
        {
            _workArea = work;
            var maxWidth = Math.Max(220, work.Width - 24);
            var maxHeight = Math.Max(220, work.Height - 24);
            MinWidth = Math.Min(380, maxWidth); MinHeight = Math.Min(300, maxHeight);
            MaxWidth = maxWidth; MaxHeight = maxHeight;
            Width = Math.Min(Width, maxWidth); Height = Math.Min(Height, maxHeight);
            _errorScroll.MaxHeight = Math.Min(72, maxHeight * .18);
            DpiAwarenessController.PlaceWithinWorkArea(this, work, Height);
        }
        finally { _placing = false; }
    }

    private void UpdatePeerStatus() => _peerStatus.Text = (_remote?.HasPeerSnapshot ?? _vm.HasRoomOrchestrationPeerSnapshot(_opened.Key.ProfileId))
        ? "保存实际修改的绑定时，将核对该房间当前的成员布局。" : "该房间暂无已确认的成员快照；绑定结构尚未核对，实际开批时仍会校验。";

    private async Task<CompletionTargetCatalog?> ReadCompletionAsync(string type, string name, string workflowId,
        bool explicitRefresh, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _closedToken);
        var revision = explicitRefresh && type is ("group" or "onedragon") ? ++_catalogRequest : 0;
        var result = _remote is null
            ? await _vm.LoadOrchestrationCompletionCatalogAsync(type, name, workflowId, explicitRefresh, linked.Token)
            : await _remote.Catalog(type, name, workflowId, explicitRefresh, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (revision != 0 && revision == _catalogRequest && !_closed)
            InstallNames(MainViewModel.NamesFromCatalog(result));
        // Passive reads also carry the last shared name list to refresh the completion selector
        // after a binding-directory refresh, without starting BGI or choosing a new task.
        return result with { ConfigGroups = _names?.Groups ?? [], OneClickConfigs = _names?.OneClicks ?? [] };
    }

    private async Task RefreshNamesAsync(bool explicitRefresh, bool updateCompletionPanel)
    {
        if (_closed || _refreshing || _saving) return;
        _refreshing = true;
        _refresh.IsEnabled = false;
        _directoryStatus.Text = explicitRefresh ? $"正在刷新{DirectoryOwner}任务目录…" : $"正在读取{DirectoryOwner}任务目录…";
        try
        {
            var revision = ++_catalogRequest;
            var result = await ReadNamesAsync(explicitRefresh, _closedToken);
            if (_closed || revision != _catalogRequest) return;
            InstallNames(result);
            if (updateCompletionPanel) await _completionPanel.RefreshCatalogAsync();
        }
        catch (OperationCanceledException) when (_closedToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed) { InstallNames(null); _directoryStatus.Text += " " + ex.Message; }
        }
        finally
        {
            _refreshing = false;
            if (!_closed) _refresh.IsEnabled = true;
        }
    }

    private Task<RoomOrchestrationNameCatalog?> ReadNamesAsync(bool refresh, CancellationToken ct)
        => _remote is null ? _vm.LoadRoomOrchestrationNamesAsync(refresh, ct) : _remote.Names(refresh, ct);

    private void InstallNames(RoomOrchestrationNameCatalog? names)
    {
        _names = names;
        _directoryStatus.Text = names is null
            ? $"暂未取得{DirectoryOwner} BGI 实时任务目录。未修改的绑定、其他设置仍可保存。"
            : $"已读取{DirectoryOwner} {names.Groups.Count} 个配置组、{names.OneClicks.Count} 个一条龙。";
        var old = _available.SelectedItem as RoomOrchestrationBinding;
        var available = AllNames().Where(b => !_selected.Contains(b)).ToArray();
        _available.ItemsSource = available;
        _available.SelectedItem = old is null ? null : available.FirstOrDefault(x => x == old);
        if (_available.SelectedIndex < 0 && available.Length > 0) _available.SelectedIndex = 0;
        _quickTarget.ItemsSource = AllNames();
        SelectQuickTarget(); UpdateBindingButtons();
    }

    private RoomOrchestrationBinding[] AllNames() => _names is null ? [] : _names.Groups.Select(x => new RoomOrchestrationBinding(x, "group"))
        .Concat(_names.OneClicks.Select(x => new RoomOrchestrationBinding(x, "onedragon"))).ToArray();

    private void AddBinding()
    {
        if (_available.SelectedItem is not RoomOrchestrationBinding binding || _selected.Contains(binding)) return;
        _selected.Add(binding); _bindingList.SelectedItem = binding; InstallNames(_names);
    }

    private void RemoveBinding()
    {
        var index = _bindingList.SelectedIndex;
        if (index < 0) return;
        _selected.RemoveAt(index); _bindingList.SelectedIndex = Math.Min(index, _selected.Count - 1); InstallNames(_names);
    }

    private void MoveBinding(int delta)
    {
        var index = _bindingList.SelectedIndex;
        if (index < 0 || index + delta < 0 || index + delta >= _selected.Count) return;
        _selected.Move(index, index + delta); _bindingList.SelectedIndex = index + delta; UpdateBindingButtons();
    }

    private void UpdateBindingButtons()
    {
        if (_up is null || _down is null || _remove is null || _add is null || _available is null) return;
        _up.IsEnabled = _bindingList.SelectedIndex > 0;
        _down.IsEnabled = _bindingList.SelectedIndex >= 0 && _bindingList.SelectedIndex < _selected.Count - 1;
        _remove.IsEnabled = _bindingList.SelectedIndex >= 0;
        _add.IsEnabled = _names is not null && _available.SelectedItem is RoomOrchestrationBinding;
    }

    private void SelectQuickTarget()
    {
        var key = _quickKey.SelectedItem as string;
        var selected = key is null ? null : MainViewModel.ParseQuickCommandBinding(_quickCommands.GetValueOrDefault(key));
        _quickTarget.SelectedItem = selected is null ? null : _quickTarget.Items.OfType<RoomOrchestrationBinding>().FirstOrDefault(b => b.Name == selected.Name && b.Type == selected.Type);
        _quickSet.IsEnabled = _quickTarget.SelectedItem is RoomOrchestrationBinding;
    }

    private void SetQuickBinding()
    {
        if (_quickKey.SelectedItem is not string key || _quickTarget.SelectedItem is not RoomOrchestrationBinding binding) return;
        _quickCommands[key] = (binding.Type == "onedragon" ? "ONEDRAGON:" : "GROUP:") + binding.Name;
        UpdateQuickSummary();
    }

    private void UpdateQuickSummary()
    {
        _quickSummary.Text = string.Join("\n", _quickKey.Items.Cast<string>().Select(key =>
        {
            var raw = _quickCommands.GetValueOrDefault(key, "");
            var binding = MainViewModel.ParseQuickCommandBinding(raw);
            return $"{key}：" + (binding is null ? string.IsNullOrEmpty(raw) ? "未绑定" : raw + "（待核对）"
                : RoomOrchestrationPresentation.TaskType(binding.Type) + " · " + binding.Name);
        }));
    }

    private async Task SaveAsync()
    {
        if (_saving || _closed || _saveUnconfirmed) return;
        _saving = true;
        _save.IsEnabled = false;
        _body.IsEnabled = false;
        SetError("");
        try
        {
            int? players = null;
            if (_peopleBox.Text.Trim() != _opened.ExpectedPlayers.ToString(CultureInfo.InvariantCulture))
            {
                if (!int.TryParse(_peopleBox.Text.Trim(), out var count) || count is < 1 or > 4)
                    throw new InvalidOperationException("预期开锄人数必须为 1–4 人。");
                players = count;
            }
            var time = _scheduleBox.Text.Trim();
            string? schedule = time != _opened.Schedule || _confirmSchedule.IsChecked == true ? time : null;
            if (schedule is { Length: > 0 } && !TimeSpan.TryParseExact(schedule, @"hh\:mm", CultureInfo.InvariantCulture, out _))
                throw new InvalidOperationException("定时上线时间必须为 HH:mm，或留空禁用。");
            var initial = RoomOrchestrationBinding.From(_opened.Names, _opened.Types);
            var bindings = initial.SequenceEqual(_selected) ? null : _selected.ToArray();
            var quick = _quickCommands.Where(kv => _opened.QuickCommands.GetValueOrDefault(kv.Key, "") != kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            if (!_completionPanel.TryGetChanges(out var completion, out var completionError))
                throw new InvalidOperationException(completionError ?? "完成后动作尚未确认。");
            if (completion is not null && await (_remote is null
                    ? _vm.ValidateRoomOrchestrationCompletionAsync(completion, _closedToken)
                    : _remote.ValidateCompletion(completion, _closedToken)) is { } targetError)
                throw new InvalidOperationException(targetError);
            // The same fresh directory populates the visible candidates and validates the changed
            // references. Completion-only, count-only or schedule-only edits do not require it.
            if (bindings is not null || quick.Values.Any(v => v.Length != 0))
            {
                if (_refreshing) throw new InvalidOperationException("任务目录正在刷新，请稍后保存。");
                var revision = ++_catalogRequest;
                var names = await ReadNamesAsync(false, _closedToken);
                _closedToken.ThrowIfCancellationRequested();
                if (revision != _catalogRequest) throw new InvalidOperationException("任务目录已更新，请重新核对选择。");
                InstallNames(names);
            }
            _closedToken.ThrowIfCancellationRequested();
            UpdatePeerStatus();
            var conflict = _remote is null ? _vm.GetRoomOrchestrationLayoutConflict(_opened.Key.ProfileId, bindings)
                : await _remote.Layout(bindings, _closedToken);
            if (conflict is not null && MessageBox.Show(this, conflict + $"\n\n若正在逐个调整所有成员的绑定，可以确认继续保存{DirectoryOwner}配置。未统一前仍不能开始联机批次。\n是否继续保存？",
                "绑定结构不一致", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            _closedToken.ThrowIfCancellationRequested();
            var changes = new RoomOrchestrationEditChanges(bindings, players, schedule, completion, quick);
            if (_remote is null) _vm.CommitRoomOrchestrationEdit(_opened, changes, _names, conflict);
            else await _remote.Save(changes, _names, conflict, _closedToken);
            DialogResult = true;
        }
        catch (OperationCanceledException) when (_closedToken.IsCancellationRequested) { }
        catch (RoomOrchestrationUnconfirmedException ex) { _saveUnconfirmed = true; if (!_closed) SetError(ex.Message); }
        catch (Exception ex) { if (!_closed) SetError(ex.Message); }
        finally
        {
            _saving = false;
            if (!_closed) { _save.IsEnabled = !_saveUnconfirmed; _body.IsEnabled = !_saveUnconfirmed; }
        }
    }

    private void SetError(string text)
    {
        _error.Text = text;
        _errorScroll.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        _errorScroll.ScrollToTop();
    }

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private Button MakeButton(string label, Action? action = null)
    {
        var button = new Button { Content = label, MinHeight = 32, Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 6, 3), Foreground = White, Background = new SolidColorBrush(Color.FromRgb(0x30, 0x2B, 0x54)),
            BorderBrush = Edge, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, FontSize = 12 };
        if (Owner.TryFindResource("BtnGhost") is Style style) button.Style = style;
        if (action is not null) button.Click += (_, _) => action();
        return button;
    }

    private static TextBox MakeTextBox(string value, string label)
    {
        var box = new TextBox { Text = value, Foreground = White, Background = Well, BorderBrush = Edge,
            BorderThickness = new Thickness(1), Padding = new Thickness(8, 5, 8, 5), MinHeight = 32 };
        AutomationProperties.SetName(box, label);
        return box;
    }

    private static TextBlock Text(string text, Brush brush, double size = 13, bool bold = false) => new()
    {
        Text = text, Foreground = brush, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4)
    };

    private static Border Section(Panel child) => new()
    {
        Child = child, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), BorderBrush = Edge,
        Background = new SolidColorBrush(Color.FromRgb(0x26, 0x23, 0x4E)), Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10)
    };

    private static DataTemplate WrappedLabelTemplate(double maxWidth = double.NaN)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(RoomOrchestrationBinding.Label)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        if (!double.IsNaN(maxWidth)) text.SetValue(MaxWidthProperty, maxWidth);
        text.SetBinding(ToolTipProperty, new Binding(nameof(RoomOrchestrationBinding.Label)));
        return new DataTemplate(typeof(RoomOrchestrationBinding)) { VisualTree = text };
    }
}
