using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Reflection;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Helpers;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private LocalBrowserBridge? _localBrowserBridge;
    private string? _localBrowserServerUrl;
    private LocalBrowserHandoff? _localBrowserHandoff;
    private bool _openingLocalBrowser;

    /// <summary>由设置页“本机网页”卡片调用；配对桥随主窗口生命周期复用。</summary>
    internal async void OpenLocalBrowser()
    {
        if (ViewModel.IsRoomFollowing)
        {
            MessageBox.Show(this, "请先关闭只读房间跟随，再按原房间身份打开本机网页。", "本机网页",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_openingLocalBrowser) return;
        _openingLocalBrowser = true;
        try
        {
            string url;
            if (ViewModel.IsObserverMode)
            {
                var caller = ReadBrowserHandoffIdentity();
                IReadOnlyList<LocalBrowserHandoff.Target> targets;
                using (ViewModel.TryEnterLocalBrowserRoomEffect()
                    ?? throw new InvalidOperationException("房间正在切换或等待核对，请稍后打开本机网页。"))
                {
                    targets = await LocalBrowserHandoff.FindExecutorsAsync(caller);
                }
                if (targets.Count == 0)
                    throw new InvalidOperationException("未找到正在运行的同机执行端。请确认两端的服务器、UID、控制房间和房间密码一致。");
                var target = targets.Count == 1 ? targets[0] : ChooseBrowserExecutor(targets);
                if (target is null) return;
                using var roomEffect = ViewModel.TryEnterLocalBrowserRoomEffect();
                if (roomEffect is null || !IsBrowserHandoffIdentityCurrent(caller))
                    throw new InvalidOperationException("监控端配置已变化，请重新打开本机网页。");
                url = await LocalBrowserHandoff.GetPairingUrlAsync(caller, target);
                if (!IsBrowserHandoffIdentityCurrent(caller))
                    throw new InvalidOperationException("监控端配置已变化，请重新打开本机网页。");
            }
            else url = CreateOwnBrowserPairingUrl(ReadBrowserHandoffIdentity());
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ViewModel.AddLog("本机网页打开失败：" + ex.Message);
            MessageBox.Show(this, "无法打开本机网页：" + ex.Message, "本机网页", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _openingLocalBrowser = false; }
    }

    private LocalBrowserHandoff.Identity ReadBrowserHandoffIdentity(bool forHandoff = false)
    {
        var config = ViewModel.Config;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return new(config?.PlayerUid ?? "", config?.PlayerName ?? "", ViewModel.RoomCode,
            config?.ClientInstanceId ?? "", config?.ControlRoomPassword ?? "",
            ViewModel.IsExecutorMode && (!forHandoff || ViewModel.CanUseAssistantRoomMemberActions),
            process.SessionId) { ServerUrl = config?.ServerUrl ?? "" };
    }

    private bool IsBrowserHandoffIdentityCurrent(LocalBrowserHandoff.Identity expected)
    {
        var current = ReadBrowserHandoffIdentity();
        return RoomServerAddress.Same(current.ServerUrl, expected.ServerUrl)
            && (current with { ServerUrl = expected.ServerUrl }) == expected;
    }

    private string CreateOwnBrowserPairingUrl(LocalBrowserHandoff.Identity expected, bool fromHandoff = false)
    {
        using var roomEffect = fromHandoff ? ViewModel.TryEnterLocalBrowserRoomEffect() : null;
        if ((fromHandoff && roomEffect is null) || !IsBrowserHandoffIdentityCurrent(expected) || !expected.IsExecutor)
            throw new InvalidOperationException("执行端房间、模式或身份已变化，请重新打开。");
        if (_localBrowserBridge is null || !RoomServerAddress.Same(_localBrowserServerUrl, expected.ServerUrl))
        {
            var server = expected.ServerUrl;
            var bridge = new LocalBrowserBridge((request, ct) => ExecuteLocalBrowserAsync(request, ct, server),
                Dodoco.LocalBrowserService.ReleaseSession, ViewModel.AddLog);
            _localBrowserBridge?.Dispose();
            _localBrowserBridge = bridge;
            _localBrowserServerUrl = server;
        }
        return _localBrowserBridge.CreatePairingUrl();
    }

    private LocalBrowserHandoff.Target? ChooseBrowserExecutor(IReadOnlyList<LocalBrowserHandoff.Target> targets)
    {
        var dialog = new Window
        {
            Owner = this, Title = "选择本机网页的执行端", Width = 620, Height = 310,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Background = Background, FontFamily = FontFamily, FontSize = FontSize
        };
        dialog.Resources.MergedDictionaries.Add(Resources);
        var panel = new StackPanel { Margin = new Thickness(20) };
        var hint = new TextBlock { Text = "本机有多个匹配执行端，请选择要打开的 Windows 会话：", Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "GoldText");
        var choices = new ListBox { ItemsSource = targets, DisplayMemberPath = "Label", Height = 130 };
        choices.SetResourceReference(BackgroundProperty, "Card");
        choices.SetResourceReference(ForegroundProperty, "White");
        var open = new Button { Content = "打开所选执行端网页", Margin = new Thickness(0, 12, 0, 0), IsEnabled = false };
        open.SetResourceReference(StyleProperty, "BtnGold");
        choices.SelectionChanged += (_, _) => open.IsEnabled = choices.SelectedItem is not null;
        open.Click += (_, _) => dialog.DialogResult = true;
        panel.Children.Add(hint); panel.Children.Add(choices); panel.Children.Add(open); dialog.Content = panel;
        return dialog.ShowDialog() == true ? choices.SelectedItem as LocalBrowserHandoff.Target : null;
    }

    /// <summary>本机网页的同一配对会话同时承载嘟嘟可与槲寄生；槲寄生请求转交执行端中心处理。</summary>
    private async Task<LocalBrowserResult> ExecuteLocalBrowserAsync(LocalBrowserRequest request,
        CancellationToken cancellationToken, string server)
    {
        var isMistletoe = request.Operation is "mistletoe.read" or "mistletoe.request";
        var body = isMistletoe ? request.Args["request"] as System.Text.Json.Nodes.JsonObject : null;
        if (isMistletoe && body is null) return new LocalBrowserResult("error", "槲寄生请求为空");
        // The authenticated bridge can still identify itself and query/cancel/recheck its
        // original Web room switch while its room effects are blocked. Keep the original paired
        // server boundary; unrelated Dodoco/center operations remain blocked as before.
        var readback = request.Operation == "local.identity" || body is not null && MainViewModel.WebRoomRequestCanReadOrRecoverDuringSwitch(body);
        using var roomEffect = readback ? null : ViewModel.TryEnterLocalBrowserRoomEffect();
        if ((!readback && roomEffect is null) || !RoomServerAddress.Same(server, ViewModel.Config?.ServerUrl))
            return new LocalBrowserResult("error", "助手房间正在切换或服务器已变化，请从助手重新打开本机网页");
        if (request.Operation == "local.identity")
            return await Dispatcher.InvokeAsync(() => RoomServerAddress.Same(server, ViewModel.Config?.ServerUrl)
                ? Dodoco.LocalBrowserService.ExecuteAsync(request, cancellationToken)
                : Task.FromResult(new LocalBrowserResult("error", "本机网页配对的服务器已变化，请从助手重新打开"))).Task.Unwrap();
        if (!isMistletoe)
            return await Dodoco.LocalBrowserService.ExecuteAsync(request, cancellationToken);
        try
        {
            var reply = await ViewModel.ExecuteMistletoeLocalAsync((System.Text.Json.Nodes.JsonObject)body!.DeepClone(),
                request.RequestId, server, cancellationToken);
            return new LocalBrowserResult("ok", Data: reply);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new LocalBrowserResult("unknown", "本机槲寄生请求结果未确认：" + ex.Message); }
    }
    private readonly System.Windows.Threading.DispatcherTimer _roomCodeCopyHintTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1.5),
    };

    private bool _copyingRoomCode;

    private void HeaderStarHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 以实际字体和按钮的自然宽度判断，不使用固定阈值；340 DIP 会误隐藏本可放下的文案。
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var requiredWidth = ProjectStarButton.Padding.Left + ProjectStarButton.Padding.Right;
        // StarMessage 折叠时自身 DesiredSize 为零，直接测量其仍为 Visible 的子元素，
        // 确保窗口放宽或切换到宽页面后仍能恢复完整文案。
        foreach (UIElement child in StarMessage.Children)
        {
            child.Measure(unbounded);
            requiredWidth += child.DesiredSize.Width;
        }
        StarBadge.Measure(unbounded);
        requiredWidth += StarBadge.DesiredSize.Width;
        StarMessage.Visibility = e.NewSize.Width >= Math.Ceiling(requiredWidth)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenProjectGitHubClick(object sender, RoutedEventArgs e)
    {
        const string projectUrl = "https://github.com/kaedelcb/better-genshin-impact";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(projectUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ViewModel.AddLog($"打开 GitHub 失败：{ex.Message}");
            MessageBox.Show(this, $"无法打开默认浏览器，请手动访问：\n{projectUrl}",
                "打开 GitHub", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void CompletionActionHintClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { IsVisible: true, Tag: Popup popup })
        {
            popup.IsOpen = !popup.IsOpen;
            e.Handled = true;
        }
    }

    private void CompletionActionHintVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Button { IsVisible: false, Tag: Popup popup }) popup.IsOpen = false;
    }

    private void HeaderRoomClick(object sender, RoutedEventArgs e)
    {
        AssistantRoomPopup.IsOpen = !AssistantRoomPopup.IsOpen;
        e.Handled = true;
    }

    private static CustomPopupPlacement[] PlaceAssistantRoomPopup(Size popupSize, Size targetSize, Point offset) =>
    [
        new(new Point(targetSize.Width - popupSize.Width + 12, targetSize.Height + 6), PopupPrimaryAxis.Horizontal),
        new(new Point(targetSize.Width - popupSize.Width + 12, -popupSize.Height - 6), PopupPrimaryAxis.Horizontal)
    ];

    private bool _syncingAssistantRoomSelector;

    private void AssistantRoomPopupOpened(object? sender, EventArgs e)
    {
        // Reopening always shows the confirmed room as the switch candidate, without altering the currently viewed room.
        _syncingAssistantRoomSelector = true;
        try
        {
            AssistantRoomSelector.SetCurrentValue(Selector.SelectedValueProperty, ViewModel.ActiveRoomProfile?.Id);
        }
        finally
        {
            _syncingAssistantRoomSelector = false;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!AssistantRoomPopup.IsOpen) return;
            if (AssistantRoomSelector.SelectedItem is { } room &&
                AssistantRoomSelector.ItemContainerGenerator.ContainerFromItem(room) is ListBoxItem row && row.Focus()) return;
            if (!AssistantRoomSelector.IsVisible || !AssistantRoomSelector.Focus())
            {
                if (!ManageAssistantRoomsButton.Focus()) RoomFollowEntryButton.Focus();
            }
        }));
    }

    private void AllRoomsViewClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectViewedRoom(AssistConfig.AllRoomsViewId);
        e.Handled = true;
    }

    private void RoomOrchestrationEntryClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanSelectRoomOrchestration) return;
        if (ViewModel.IsShowingRoomOrchestration)
            ViewModel.SelectViewedRoom(ViewModel.ViewedRoomProfileId);
        else
            ViewModel.SelectRoomOrchestration();
        DismissAssistantRoomPopup();
        e.Handled = true;
    }

    private void AssistantRoomSelectorKeyDown(object sender, KeyEventArgs e)
    {
        if (_syncingAssistantRoomSelector || !AssistantRoomPopup.IsOpen || !AssistantRoomSelector.IsEnabled) return;
        // Only real input changes the viewed room. SelectedValue is also driven by confirmed-login changes
        // and ItemsSource refresh, so SelectionChanged is not a user's instruction to leave orchestration.
        if (e.Key is Key.Enter or Key.Space)
        {
            var selected = (e.OriginalSource is DependencyObject source
                ? ItemsControl.ContainerFromElement(AssistantRoomSelector, source) as ListBoxItem : null)?.DataContext
                as AssistantRoomProfile ?? AssistantRoomSelector.SelectedItem as AssistantRoomProfile;
            if (selected is null) return;
            AssistantRoomSelector.SetCurrentValue(Selector.SelectedItemProperty, selected);
            ViewModel.SelectViewedRoom(selected.Id);
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown
            && AssistantRoomSelector.SelectedItem is AssistantRoomProfile profile)
            ViewModel.SelectViewedRoom(profile.Id);
    }

    private void AssistantRoomItemMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_syncingAssistantRoomSelector || !AssistantRoomPopup.IsOpen) return;
        if (sender is ListBoxItem { DataContext: AssistantRoomProfile profile })
        {
            ViewModel.SelectViewedRoom(profile.Id);
        }
    }

    private void AssistantRoomPopupClosed(object? sender, EventArgs e)
    {
        _roomCodeCopyHintTimer.Stop();
        RoomCodeCopyHintText.Visibility = Visibility.Collapsed;
    }

    private void CloseAssistantRoomPopupClick(object sender, RoutedEventArgs e) => DismissAssistantRoomPopup();

    private void AssistantRoomPopupPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DismissAssistantRoomPopup();
        e.Handled = true;
    }

    private void ManageAssistantRoomsClick(object sender, RoutedEventArgs e) => DismissAssistantRoomPopup();

    private void RoomFollowEntryClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanOpenRoomFollow) return;
        DismissAssistantRoomPopup();
        var executor = ViewModel.IsExecutorMode;
        var dialog = new Window
        {
            Owner = this, DataContext = ViewModel, Title = executor ? "共享房间跟随" : "跟随执行端房间",
            Width = 470, Height = executor ? 485 : 450, MinHeight = 300,
            MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 32),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            ResizeMode = ResizeMode.CanResizeWithGrip, Background = Background, FontFamily = FontFamily, FontSize = FontSize
        };
        dialog.Resources.MergedDictionaries.Add(Resources);
        var panel = new StackPanel { Margin = new Thickness(18) };
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        TextBlock Label(string text, string color = "Dim", double size = 12)
        {
            var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            block.SetResourceReference(TextBlock.ForegroundProperty, color);
            return block;
        }
        Button ActionButton(string title, RoutedEventHandler handler, bool primary = false)
        {
            var button = new Button { Content = title, MinHeight = 30, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 3, 7, 5) };
            button.SetResourceReference(StyleProperty, primary ? "BtnGold" : "BtnGhost");
            button.Click += handler;
            return button;
        }
        void StyleInput(Control input)
        {
            input.SetResourceReference(Control.BackgroundProperty, "Card");
            input.SetResourceReference(Control.ForegroundProperty, "White");
            input.SetResourceReference(Control.BorderBrushProperty, "GoldLine");
            input.BorderThickness = new Thickness(1);
            input.Padding = new Thickness(7);
            input.Margin = new Thickness(0, 0, 0, 6);
        }
        void AddToggle(string text, string checkedProperty, string enabledProperty, Func<bool, Task> change)
        {
            var check = new CheckBox { Content = text, FontSize = 13, Margin = new Thickness(0, 2, 0, 10) };
            check.SetResourceReference(StyleProperty, "GuildCheckBox");
            check.SetResourceReference(Control.ForegroundProperty, "White");
            check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(checkedProperty) { Mode = BindingMode.OneWay });
            check.SetBinding(IsEnabledProperty, new Binding(enabledProperty));
            check.Click += async (_, _) =>
            {
                try { await change(check.IsChecked == true); }
                finally { check.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget(); }
            };
            panel.Children.Add(check);
        }
        var title = Label(executor ? "邀请监控端跟随本机房间" : "跟随指定的执行端", "GoldText", 15);
        title.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(title);
        panel.Children.Add(Label(executor
            ? "仅共享房间档案名称、备注、服务器/房间号、成员 UID、当前登录和查看页面等元数据。邀请不包含房间密码，也不授予房间登录或任务执行权限。"
            : "跟随为只读查看：仅对本机已保存、且普通入房鉴权通过的房间自动连接。需要操作或添加房间时，先关闭跟随；关闭后恢复本机原登录和查看页面。"));
        var feedback = Label("", "GoldText", 11);
        if (executor)
        {
            AddToggle("开启房间元数据共享", nameof(MainViewModel.RoomFollowSharingEnabled), nameof(MainViewModel.CanShareRoomFollow),
                ViewModel.SetRoomFollowSharingEnabledAsync);
            panel.Children.Add(Label("本机邀请仅限同一台机器、同一个 Windows 用户。跨机或网页使用 HTTPS 中继邀请。"));
            panel.Children.Add(Label("远程中继 HTTPS 基地址", "GoldText"));
            var relay = new TextBox { Text = ViewModel.RoomFollowDefaultRelay, MaxLength = 2048 };
            StyleInput(relay);
            System.Windows.Automation.AutomationProperties.SetName(relay, "远程跟随 HTTPS 服务地址");
            panel.Children.Add(relay);
            void CopyInvitation(string? invitation)
            {
                if (invitation is null) { feedback.Text = "未获得可复制的邀请。"; return; }
                try { Clipboard.SetText(invitation); feedback.Text = "邀请已复制。请仅交给信任的监控端；可在下方撤销。"; }
                catch { feedback.Text = "剪贴板不可用。邀请仍保存在授权列表中，可再次复制。"; }
            }
            var generate = new WrapPanel();
            foreach (var local in new[] { true, false })
            {
                var button = ActionButton(local ? "生成并复制本机邀请" : "生成并复制 HTTPS 邀请", async (_, _) =>
                {
                    feedback.Text = "";
                    CopyInvitation(await ViewModel.GenerateRoomFollowInvitationAsync(local, relay.Text));
                }, primary: true);
                button.SetBinding(IsEnabledProperty, new Binding(nameof(MainViewModel.CanGenerateRoomFollowGrant)));
                generate.Children.Add(button);
            }
            panel.Children.Add(generate);
            panel.Children.Add(Label("已生成邀请（选择后复制或撤销）", "GoldText"));
            var grants = new ListBox { DisplayMemberPath = nameof(MainViewModel.RoomFollowGrantChoice.Label), Height = 80 };
            StyleInput(grants);
            ScrollViewer.SetHorizontalScrollBarVisibility(grants, ScrollBarVisibility.Disabled);
            grants.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.RoomFollowGrantChoices)));
            panel.Children.Add(grants);
            var grantActions = new WrapPanel();
            var copy = ActionButton("复制所选邀请", (_, _) =>
            {
                try
                {
                    if (grants.SelectedItem is MainViewModel.RoomFollowGrantChoice choice)
                        CopyInvitation(ViewModel.CopyRoomFollowInvitation(choice.Id));
                }
                catch { feedback.Text = "邀请无法读取，请核对授权列表。"; }
            });
            var revoke = ActionButton("撤销所选邀请", async (_, _) =>
            {
                if (grants.SelectedItem is not MainViewModel.RoomFollowGrantChoice choice) return;
                if (MessageBox.Show(dialog, "撤销这份邀请？其跟随端将不能再收到本机房间元数据。", "撤销邀请",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                feedback.Text = "";
                await ViewModel.RevokeRoomFollowGrantAsync(choice.Id);
            });
            copy.IsEnabled = revoke.IsEnabled = false;
            grants.SelectionChanged += (_, _) => copy.IsEnabled = revoke.IsEnabled = grants.SelectedItem is not null && !ViewModel.RoomFollowBusy;
            grantActions.Children.Add(copy);
            grantActions.Children.Add(revoke);
            panel.Children.Add(grantActions);
        }
        else
        {
            var current = Label("", "Water");
            current.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.RoomFollowInvitationSummary)));
            panel.Children.Add(current);
            AddToggle("跟随此执行端（只读）", nameof(MainViewModel.IsRoomFollowing), nameof(MainViewModel.CanEnableRoomFollow),
                ViewModel.SetRoomFollowingEnabledAsync);
            panel.Children.Add(Label("粘贴执行端生成的完整邀请。邀请会在本机加密保存，不写入房间设置。", "GoldText"));
            var invitationBox = new PasswordBox { MaxLength = RoomFollowFormat.MaxInvitationLength };
            StyleInput(invitationBox);
            System.Windows.Automation.AutomationProperties.SetName(invitationBox, "房间跟随邀请");
            panel.Children.Add(invitationBox);
            var invitationActions = new WrapPanel();
            var paste = ActionButton("粘贴邀请", (_, _) =>
            {
                try
                {
                    var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                    if (text.Length == 0 || text.Length > RoomFollowFormat.MaxInvitationLength)
                    { feedback.Text = "剪贴板中没有有效长度的文本邀请。"; return; }
                    invitationBox.Password = text;
                    feedback.Text = "已粘贴，请点击保存或替换。";
                }
                catch { feedback.Text = "剪贴板不可用，请重试或手动粘贴。"; }
            });
            var save = ActionButton("保存/替换", async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(invitationBox.Password)) { feedback.Text = "请先粘贴邀请。"; return; }
                var value = invitationBox.Password;
                invitationBox.Clear();
                feedback.Text = "";
                await ViewModel.ReplaceRoomFollowInvitationAsync(value);
            }, primary: true);
            var remove = ActionButton("移除邀请", async (_, _) =>
            {
                invitationBox.Clear(); feedback.Text = "";
                await ViewModel.ReplaceRoomFollowInvitationAsync("");
            });
            foreach (var button in new[] { paste, save, remove })
            {
                button.SetBinding(IsEnabledProperty, new Binding(nameof(MainViewModel.CanChangeRoomFollowInvitation)));
                invitationActions.Children.Add(button);
            }
            panel.Children.Add(invitationActions);
            dialog.Closed += (_, _) => invitationBox.Clear();
        }
        var status = Label("", "Water");
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.RoomFollowStatus)));
        System.Windows.Automation.AutomationProperties.SetLiveSetting(status, System.Windows.Automation.AutomationLiveSetting.Polite);
        var error = Label("", "GoldText");
        error.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.RoomFollowError)));
        panel.Children.Add(status);
        panel.Children.Add(error);
        panel.Children.Add(feedback);
        panel.Children.Add(ActionButton("关闭面板", (_, _) => dialog.Close()));
        dialog.ShowDialog();
        e.Handled = true;
    }

    private void DismissAssistantRoomPopup()
    {
        AssistantRoomPopup.IsOpen = false;
        HeaderRoomButton.Focus();
    }

    private async void CopyRoomCodeClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_copyingRoomCode) return;
        var roomCode = ViewModel.RoomCode;
        if (string.IsNullOrWhiteSpace(roomCode))
        {
            ShowRoomCodeCopyHint("暂无可复制的房间号。", 3);
            return;
        }

        _copyingRoomCode = true;
        ShowRoomCodeCopyHint("正在复制房间号…", 3);
        try
        {
            // 剪贴板可能被其他程序短暂占用；等待时让 UI 保持响应。
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetText(roomCode);
                    ShowRoomCodeCopyHint("房间号已复制到剪贴板。", 1.5);
                    return;
                }
                catch (System.Runtime.InteropServices.ExternalException ex)
                {
                    if (attempt == 2)
                    {
                        ViewModel.AddLog($"复制房间号失败：剪贴板不可用，请稍后重试。（0x{ex.HResult:X8}）");
                        ShowRoomCodeCopyHint("复制失败：剪贴板不可用，请稍后重试。", 3);
                        return;
                    }
                }
                await Task.Delay(80);
                if (!IsLoaded) return;
            }
        }
        finally
        {
            _copyingRoomCode = false;
        }
    }

    private void ShowRoomCodeCopyHint(string message, double seconds)
    {
        if (!IsLoaded || !AssistantRoomPopup.IsOpen) return;
        _roomCodeCopyHintTimer.Stop();
        RoomCodeCopyHintText.Text = message;
        RoomCodeCopyHintText.Visibility = Visibility.Visible;
        _roomCodeCopyHintTimer.Interval = TimeSpan.FromSeconds(seconds);
        _roomCodeCopyHintTimer.Start();
    }

    /// <summary>嘟嘟可（日志与监控系统）ViewModel。手动组装（参照 App.xaml.cs 风格），
    /// 构造时即启动日志 tail/异常监控后台服务，告警红点不依赖页面打开。</summary>
    public DodocoViewModel Dodoco { get; }

    /// <summary>槲寄生（调度器）ViewModel。手动组装（同 Dodoco）；
    /// 启动中心「助手启动后自动执行流程」挂在 MainViewModel.Initialized 事件上。</summary>
    public MistletoeViewModel Mistletoe { get; }

    public static readonly DependencyProperty ActiveMistletoeProperty = DependencyProperty.Register(
        nameof(ActiveMistletoe), typeof(MistletoeViewModel), typeof(MainWindow));
    public MistletoeViewModel ActiveMistletoe
    {
        get => (MistletoeViewModel)GetValue(ActiveMistletoeProperty);
        private set => SetValue(ActiveMistletoeProperty, value);
    }
    private readonly Dictionary<string, MistletoeViewModel> _parkedRemoteCenters = new(StringComparer.Ordinal);
    private string RemoteKey(RemoteCenterTarget target) => ViewModel.GetRemoteCenterScopeKey(target);

    public async Task<string?> ShowRemoteMistletoeAsync(RemoteCenterTarget target)
    {
        string? pendingError = null;
        var previousRemote = ActiveMistletoe.IsRemoteMode ? ActiveMistletoe : null;
        if (ActiveMistletoe.IsRemoteMode)
        {
            if (ActiveMistletoe.RemoteTarget is { } current
                && RemoteKey(current) == RemoteKey(target)) return null;
            pendingError = await ActiveMistletoe.FlushRemoteWritesAsync();
            ParkActiveRemoteCenter();
        }
        if (_parkedRemoteCenters.TryGetValue(RemoteKey(target), out var parked))
        {
            ActiveMistletoe = parked;
            parked.ResumeRemoteRefresh();
            return pendingError is null ? null : pendingError + "；原目标草稿仍保留在本次助手中";
        }
        var reply = await ViewModel.RequestRemoteCenterAsync(target, "startup.get");
        if (reply.Status != "ok" || string.IsNullOrEmpty(reply.PayloadJson))
        { previousRemote?.ResumeRemoteRefresh(); return reply.Message; }
        RemoteStartupSnapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<RemoteStartupSnapshot>(reply.PayloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("执行端启动中心快照为空");
        }
        catch (Exception ex)
        { previousRemote?.ResumeRemoteRefresh(); return "执行端快照无法读取：" + ex.Message; }
        ActiveMistletoe = new MistletoeViewModel(ViewModel, remoteTarget: target, remoteSnapshot: snapshot);
        return pendingError is null ? null : pendingError + "；原目标草稿仍保留在本次助手中";
    }

    public async Task<string?> ShowLocalMistletoeAsync()
    {
        var pendingError = ActiveMistletoe.IsRemoteMode
            ? await ActiveMistletoe.FlushRemoteWritesAsync() : null;
        ParkActiveRemoteCenter();
        ActiveMistletoe = Mistletoe;
        return pendingError is null ? null : pendingError + "；远程草稿仍保留在本次助手中，重新连接同一执行端可继续处理";
    }

    public async Task<string?> ReloadRemoteMistletoeAsync()
    {
        if (ActiveMistletoe.RemoteTarget is not { } target) return "当前没有选中的执行端";
        await ActiveMistletoe.AbandonRemoteDraftAsync();
        _parkedRemoteCenters.Remove(RemoteKey(target));
        var reply = await ViewModel.RequestRemoteCenterAsync(target, "startup.get");
        if (reply.Status != "ok" || string.IsNullOrEmpty(reply.PayloadJson))
        { ActiveMistletoe = Mistletoe; return reply.Message; }
        RemoteStartupSnapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<RemoteStartupSnapshot>(reply.PayloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("执行端快照为空");
        }
        catch (Exception ex)
        { ActiveMistletoe = Mistletoe; return "执行端快照无法读取：" + ex.Message; }
        ActiveMistletoe = new MistletoeViewModel(ViewModel, remoteTarget: target, remoteSnapshot: snapshot);
        return null;
    }

    private void ParkActiveRemoteCenter()
    {
        if (ActiveMistletoe.RemoteTarget is not { } target) return;
        ActiveMistletoe.StopRemoteRefresh();
        _parkedRemoteCenters[RemoteKey(target)] = ActiveMistletoe;
    }

    /// <summary>奥黛塔桌宠 ViewModel（透明徽章宠物）。手动组装（同 Dodoco）；
    /// 显隐由 pet_settings.json 驱动，独立于主窗口显隐（主窗口缩托盘时桌宠仍常驻）。</summary>
    public PetViewModel Pet { get; }

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
        Dodoco = new DodocoViewModel(viewModel);
        try
        {
            _localBrowserHandoff = new LocalBrowserHandoff(
                () => Dispatcher.Invoke(() => ReadBrowserHandoffIdentity(forHandoff: true)),
                expected => Dispatcher.Invoke(() => CreateOwnBrowserPairingUrl(expected, fromHandoff: true)), ViewModel.AddLog);
        }
        catch (Exception ex) { ViewModel.AddLog("本机网页跨会话入口不可用：" + ex.Message); }
        Application.Current.Exit += (_, _) => { _localBrowserHandoff?.Dispose(); _localBrowserBridge?.Dispose(); };
        // 槲寄生复用嘟嘟可的 BGI 日志 tail（单线程单文件句柄，多消费者订阅），供启动中心「日志触发器」盯新日志
        Mistletoe = new MistletoeViewModel(viewModel, Dodoco.LogTail);
        ActiveMistletoe = Mistletoe;
        // 槲寄生启动中心：初始化全部完成后按配置自动执行启动流程
        viewModel.Initialized += Mistletoe.OnAppInitialized;
        // 奥黛塔桌宠：双击/右键"显示主窗口"经 ShowToForeground 唤出主窗口；
        // 复用嘟嘟可的 BGI 日志尾抓好感任务轮次（单线程单文件句柄多消费者，同槲寄生）
        Pet = new PetViewModel(viewModel, Dodoco, new PetSettingsService(), ShowToForeground, Dodoco.LogTail);
        DataContext = ViewModel;
        InitializeComponent();
        AssistantRoomSelector.AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler(AssistantRoomSelectorKeyDown), handledEventsToo: true);
        AssistantRoomPopup.CustomPopupPlacementCallback = PlaceAssistantRoomPopup;
        _roomCodeCopyHintTimer.Tick += (_, _) =>
        {
            _roomCodeCopyHintTimer.Stop();
            RoomCodeCopyHintText.Visibility = Visibility.Collapsed;
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) AssistantRoomPopup.IsOpen = false; };
        Closed += (_, _) =>
        {
            AssistantRoomPopup.IsOpen = false;
            _roomCodeCopyHintTimer.Stop();
        };
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

    /// <summary>把主窗口从托盘唤出到前台（奥黛塔桌宠双击 / 右键"显示主窗口"用）。</summary>
    public void ShowToForeground()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
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
                    Margin = new System.Windows.Thickness(0, 0, 5, 5),
                    ToolTip = member.IsActiveRoomMember ? null : $"远程启动「{member.RoomLabel}」中 {member.PlayerName} 的{(isOneClick ? "一条龙" : "配置组")}：{name}"
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
                ToolTip = member.IsActiveRoomMember ? null : $"远程启动「{member.RoomLabel}」中 {member.PlayerName} 的{label}：{item}",
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
        return $"Nexus BGI · {version ?? "0.0.0"}";
    }
}
