using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using MultiplayerHoeingAssistant;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;

internal static class LocalBrowserHeaderLayout
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [STAThread]
    private static int Main()
    {
    if (!AssistantDataDirectory.IsExplicit) throw new InvalidOperationException("必须使用隔离 NEXUSBGI_DATA_ROOT");
    // Do not call App.Run(): the production App.OnStartup owns the real single-instance event.
    var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
    try
    {
        // 使用正式资源，但不调用 App.OnStartup；ShowDialog 的消息泵也不会启动第二个助手。
        var source = XDocument.Load(System.IO.Path.Combine(Environment.CurrentDirectory, "MultiplayerHoeingAssistant", "App.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(ns + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            source.Root!.Element(ns + "Application.Resources")!.Elements());
        application.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        var vm = new MainViewModel();
        var currentRoom = new AssistantRoomProfile { Id = "current", Name = "默认房间", RoomCode = "WWWWWW",
            ServerUrl = "https://rooms-a.example.test", TeamUids = ["111111111"], Password = "fixture" };
        var targetRoom = new AssistantRoomProfile { Id = "target", Name = "周末联机超长房间名称", RoomCode = "B98765",
            Note = "阿明、阿华、阿乐、很长的备注用于验证完整房间号不会被挤压",
            ServerUrl = "https://rooms-b.example.test", ExpectedHoeingPlayers = 3, TeamUids = ["111111111", "222222222", "333333333"],
            Password = "fixture", ScheduledOnlineTime = "20:00", OnlineHoeingGroupNames = ["周末路线"] };
        typeof(MainViewModel).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, new AssistConfig { StandaloneMode = true, ObserverMode = true, DisclaimerAccepted = true,
                PlayerUid = "111111111", ServerUrl = currentRoom.ServerUrl,
                ActiveRoomProfileId = currentRoom.Id, LocalRoomProfileId = currentRoom.Id, RoomProfiles = [currentRoom, targetRoom] });
        vm.RoomCode = currentRoom.RoomCode;
        var window = new MainWindow(vm) { Height = 820, Width = 980, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        window.Show();
        // 本机网页入口已移到设置页，顶栏只保留设置与退出。
        if (window.FindName("HeaderLocalWebButton") != null) throw new InvalidOperationException("顶栏不应再出现本机网页按钮");
        var notifyRooms = typeof(MainViewModel).GetMethod("NotifyRoomProfiles", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var headerName = (TextBlock)window.FindName("HeaderRoomNameText");
        var headerCode = (TextBlock)window.FindName("HeaderRoomCodeText");
        var headerArrow = (System.Windows.Shapes.Path)window.FindName("HeaderRoomArrow");
        currentRoom.Name = "测试😀房间名称";
        notifyRooms.Invoke(vm, null);
        window.UpdateLayout();
        if (headerName.Text != "测试😀房" || headerCode.Text != "WWWWWW")
            throw new InvalidOperationException("顶栏应同时显示档案名称前四个完整字符和完整房间号");
        var automationName = System.Windows.Automation.AutomationProperties.GetName((Button)window.FindName("HeaderRoomButton"));
        if (!automationName.Contains("测试😀房间名称") || !automationName.Contains("WWWWWW"))
            throw new InvalidOperationException("房间按钮的无障碍名称应包含完整档案名称和房间号");
        currentRoom.Name = "默认房间";
        notifyRooms.Invoke(vm, null);
        foreach (var width in new[] { 980d, 1180d, 1440d })
        {
            window.Width = width;
            window.UpdateLayout();
            var settings = (Button)window.FindName("HeaderSettingsButton");
            var viewing = (Button)window.FindName("HeaderViewingRoomButton");
            var viewingText = (TextBlock)window.FindName("HeaderViewingRoomText");
            var exit = (FrameworkElement)window.FindName("HeaderExitButton");
            var room = (FrameworkElement)window.FindName("HeaderRoomButton");
            var refresh = (FrameworkElement)window.FindName("HeaderRefreshButton");
            var star = (FrameworkElement)window.FindName("StarBadge");
            var starHost = (FrameworkElement)window.FindName("HeaderStarHost");
            static Rect Bounds(FrameworkElement e, Visual root)
                => e.TransformToAncestor(root).TransformBounds(new Rect(new Point(), e.RenderSize));
            var s = Bounds(settings, window); var v = Bounds(viewing, window); var x = Bounds(exit, window);
            var r = Bounds(room, window); var f = Bounds(refresh, window); var h = Bounds(starHost, window); var t = Bounds(star, window);
            var label = Bounds((FrameworkElement)window.FindName("HeaderRoomLabel"), window);
            Console.WriteLine($"width={width} viewing={v} settings={s} exit={x} room={r} label={label} refresh={f} starHost={h} star={t}");
            if (!Equals(settings.Content, "⚙") || s.Width > 40)
                throw new InvalidOperationException("设置按钮应精简为紧凑齿轮图标");
            if (!viewing.IsVisible || viewingText.Text != "所有房间")
                throw new InvalidOperationException("顶栏应默认显示正在查看「所有房间」标签");
            if (Math.Abs(v.Left-r.Left)>1 || Math.Abs(s.Right-r.Right)>1 || Math.Abs(x.Right-f.Right)>1 ||
                v.Right>s.Left || s.Right>x.Left || x.Right>window.ActualWidth ||
                (star.IsVisible && (t.Left<h.Left || t.Right>h.Right)))
                throw new InvalidOperationException($"头部控件在 {width} 宽度出现遮挡或错位");
            if (label.Left < r.Left + 4 || label.Right > r.Right - 4 || label.Top < r.Top || label.Bottom > r.Bottom)
                throw new InvalidOperationException($"房间信息在 {width} 宽度未完整留在虚线框内");
            var nameBounds = Bounds(headerName, window); var codeBounds = Bounds(headerCode, window); var arrowBounds = Bounds(headerArrow, window);
            if (headerName.Text != "默认房间" || headerCode.Text != "WWWWWW" || headerCode.TextTrimming != TextTrimming.None
                || nameBounds.Left < r.Left + 4 || nameBounds.Right + 2 > codeBounds.Left
                || codeBounds.Right + 2 > arrowBounds.Left || arrowBounds.Right > r.Right - 4)
                throw new InvalidOperationException($"房间名称、完整房间号和箭头在 {width} 宽度发生挤压或越界");
        }
        AssertAssistantRoomPopup(window, currentRoom, targetRoom);
        AssertRoomProfilesEditor(application, vm, currentRoom, targetRoom);
        AssertLegacyRoomNumber(application, vm);
        AssertAssistantRoomPopupAfterProfileChanges(window, currentRoom, expectedCount: 3);
        AssertFixedLocalRoom(application, vm);
        vm.OpenSettingsCommand.Execute(null); window.UpdateLayout();
        var page = FindChild<SettingsPage>(window) ?? throw new InvalidOperationException("未找到设置页");
        // 网页设置现为可折叠卡片，验收入口前先从真实设置页展开。
        if (page.FindName("WebSettingsExpander") is Expander webSettings) webSettings.IsExpanded = true;
        window.UpdateLayout();
        var open = (Button?)page.FindName("OpenLocalWebButton") ?? throw new InvalidOperationException("设置页缺少本机网页入口");
        if (!open.IsVisible || open.ActualWidth < 40) throw new InvalidOperationException("设置页本机网页按钮不可见");
        var hint = (TextBlock?)page.FindName("LocalWebExecutionHint") ?? throw new InvalidOperationException("缺少执行端说明");
        if (!hint.IsVisible || hint.ActualHeight < 15 || !hint.Text.Contains("请先运行执行端助手")
            || hint.Foreground.ToString() != page.FindResource("GoldText").ToString())
            throw new InvalidOperationException("执行端运行前提必须以黄色文字可见显示");
        Console.WriteLine($"settings local web button={open.ActualWidth}x{open.ActualHeight}");
        // 真实选择窗口必须先明确选择，再返回对应实例；关闭窗口不默认选择。
        var handoff = typeof(MainWindow).GetField("_localBrowserHandoff", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        var endpoint = handoff.GetType().GetProperty("Address", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(handoff);
        var targetType = handoff.GetType().GetNestedType("Target", BindingFlags.NonPublic)!;
        var targets = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(targetType))!;
        foreach (var id in new[] { "executor-one", "executor-two" })
            targets.Add(Activator.CreateInstance(targetType, endpoint, "test-uid", id, "test-room", id, 1));
        var choose = typeof(MainWindow).GetMethod("ChooseBrowserExecutor", BindingFlags.NonPublic | BindingFlags.Instance)!;
        application.Dispatcher.BeginInvoke(new Action(() =>
        {
            var dialog = application.Windows.OfType<Window>().Single(w => w.Title == "选择本机网页的执行端");
            var choices = FindChild<ListBox>(dialog)!; var button = FindChild<Button>(dialog)!;
            if (choices.SelectedItem is not null || button.IsEnabled || choices.Items.Count != 2)
                throw new InvalidOperationException("多个执行端不能默认选中或直接打开");
            choices.SelectedIndex = 1;
            if (!button.IsEnabled) throw new InvalidOperationException("选择目标后打开按钮未启用");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (!ReferenceEquals(choose.Invoke(window, [targets]), targets[1]))
            throw new InvalidOperationException("选择窗口返回了错误执行端");
        application.Dispatcher.BeginInvoke(new Action(() =>
            application.Windows.OfType<Window>().Single(w => w.Title == "选择本机网页的执行端").Close()),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (choose.Invoke(window, [targets]) is not null) throw new InvalidOperationException("取消选择不应返回执行端");
        Console.WriteLine("PASS: yellow executor hint; multiple-executor dialog requires explicit selection and supports cancellation.");
        if (Environment.GetEnvironmentVariable("LAYOUT_SNAPSHOT") is { Length: > 0 } shot)
        {
            open.BringIntoView(); window.UpdateLayout();
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(window);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var file = System.IO.File.Create(shot); png.Save(file);
        }
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    finally { application.Shutdown(); }
    return 0;
    }

    private static void AssertRoomProfilesEditor(Application application, MainViewModel vm, AssistantRoomProfile current, AssistantRoomProfile target)
    {
        var manager = new AssistConfigManager();
        typeof(MainViewModel).GetField("_configManager", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, manager);
        Exception? failure = null;
        var checkedEditor = false;
        application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = application.Windows.OfType<AssistantRoomProfilesWindow>().SingleOrDefault();
            try
            {
                if (dialog is null) throw new InvalidOperationException("缺少房间档案窗口");
                var profiles = (ListBox)dialog.FindName("ProfilesList");
                var server = (TextBox)dialog.FindName("ServerBox");
                var count = (ComboBox)dialog.FindName("ExpectedCountBox");
                var currentMarker = RoomMarker(profiles, current);
                var otherMarker = RoomMarker(profiles, target);
                if (!current.IsLocalRoom || !current.IsActiveRoom || currentMarker.Visibility != Visibility.Visible
                    || currentMarker.Text != "当前房间 · 不可删除" || otherMarker.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("未连接时应标识当前房间，并保留本机房间标识");
                vm.IsConnected = true;
                if (currentMarker.Text != "连接中房间 · 不可删除")
                    throw new InvalidOperationException("连接后当前房间标识应即时更新");
                vm.IsConnected = false;
                if (currentMarker.Text != "当前房间 · 不可删除")
                    throw new InvalidOperationException("断开后不能继续显示连接中");
                if (server.Text != current.ServerUrl || !server.IsReadOnly || !count.IsEnabled)
                    throw new InvalidOperationException("当前档案需显示服务器并允许修改人数");
                var remove = (Button)dialog.FindName("RemoveProfileButton");
                if (remove.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("本机设置对应的当前房间不能移除");
                remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!vm.Config!.RoomProfiles.Any(room => room.Id == current.Id))
                    throw new InvalidOperationException("当前房间不应被移除");
                profiles.SelectedItem = target;
                if (server.IsReadOnly || server.Text != target.ServerUrl || !Equals(count.SelectedItem, target.ExpectedHoeingPlayers))
                    throw new InvalidOperationException("切换编辑对象后，未加载该房间的地址和开锄人数");
                var details = (TextBlock)dialog.FindName("HoeingSummary");
                if (!details.Text.Contains("周末路线") || !details.Text.Contains("20:00"))
                    throw new InvalidOperationException("缺少档案自身的联机设置信息");
                var note = (TextBox)dialog.FindName("RoomNoteBox");
                if (note.Text != target.Note) throw new InvalidOperationException("应显示房间自己的备注");
                note.Text = "阿明、阿华、阿乐";
                if (Environment.GetEnvironmentVariable("ROOM_PROFILES_SNAPSHOT") is { Length: > 0 } shot)
                {
                    dialog.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth),
                        (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(dialog);
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output = System.IO.File.Create(shot);
                    png.Save(output);
                }
                count.SelectedItem = 2;
                ((Button)dialog.FindName("SaveProfileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var saved = manager.Load();
                if (saved.ActiveRoomProfileId != current.Id || saved.ExpectedHoeingPlayers != current.ExpectedHoeingPlayers
                    || saved.RoomProfiles.Single(room => room.Id == target.Id).ExpectedHoeingPlayers != 2
                    || saved.RoomProfiles.Single(room => room.Id == target.Id).Note != "阿明、阿华、阿乐"
                    || saved.RoomProfiles.Single(room => room.Id == current.Id).Note != "")
                    throw new InvalidOperationException("保存人数和备注须保留其他房间的设置");
                if (!dialog.IsVisible || ((AssistantRoomProfile)profiles.SelectedItem).ExpectedHoeingPlayers != 2)
                    throw new InvalidOperationException("保存后窗口应保持打开并选中更新后的档案");
                count.SelectedItem = 3;
                note.Text = "周末队友";
                ((Button)dialog.FindName("SaveProfileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var savedAgain = manager.Load().RoomProfiles.Single(room => room.Id == target.Id);
                if (!dialog.IsVisible || savedAgain.ExpectedHoeingPlayers != 3 || savedAgain.Note != "周末队友")
                    throw new InvalidOperationException("同一窗口中应能再次保存档案备注");
                checkedEditor = true;
            }
            catch (Exception ex) { failure = ex; }
            finally { if (dialog is { IsVisible: true }) dialog.Close(); }
        }));
        vm.OpenAssistantRoomsCommand.Execute(null);
        if (failure is not null) throw failure;
        if (!checkedEditor) throw new InvalidOperationException("没有运行档案窗口验证");
        Console.WriteLine("PASS: the real profile editor shows separate addresses, editable player counts, and profile settings.");
    }

    private static void AssertLegacyRoomNumber(Application application, MainViewModel vm)
    {
        var config = vm.Config!;
        var legacy = new AssistantRoomProfile { Id = "legacy-draft", Name = "旧房间示例", ServerUrl = "https://legacy.example.test",
            Password = "fixture", TeamUids = ["111111111", "222222222"] };
        config.RoomProfiles.Add(legacy);
        var expected = AssistConfigManager.GenerateControlRoomCode(legacy.TeamUids);
        Exception? failure = null;
        var persisted = false;
        application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = application.Windows.OfType<AssistantRoomProfilesWindow>().SingleOrDefault();
            try
            {
                if (dialog is null) throw new InvalidOperationException("缺少房间档案窗口");
                var code = (TextBox)dialog.FindName("RoomCodeBox");
                var uids = (TextBox)dialog.FindName("MemberUidsBox");
                var profiles = (ListBox)dialog.FindName("ProfilesList");
                ((Button)dialog.FindName("NewProfileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var note = (TextBox)dialog.FindName("RoomNoteBox");
                if (note.Text.Length != 0) throw new InvalidOperationException("新增房间的备注应为空");
                uids.Text = "222222222，111111111, 222222222";
                if (code.Text != expected) throw new InvalidOperationException("添加档案时应按成员 UID 自动计算旧房间号");
                ((TextBox)dialog.FindName("RoomNameBox")).Text = "新房间示例";
                ((PasswordBox)dialog.FindName("RoomPasswordBox")).Password = "fixture";
                ((Button)dialog.FindName("CreateRoomButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var status = (TextBlock)dialog.FindName("ResultText");
                if (!status.Text.Contains("联网模式")) throw new InvalidOperationException("自动号码不应阻挡建新房流程");
                note.Text = "新增房间队友";
                ((Button)dialog.FindName("SaveProfileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var added = new AssistConfigManager().Load().RoomProfiles.Single(room => room.Name == "新房间示例");
                if (added.Note != "新增房间队友" || !dialog.IsVisible)
                    throw new InvalidOperationException("新增房间时应保存备注并保留窗口");
                profiles.SelectedItem = config.RoomProfiles.Single(room => room.Id == "target");
                if (code.Text != "B98765") throw new InvalidOperationException("加载已有档案时应保留服务器房间号");
                uids.Text = "111111111, 222222222";
                if (code.Text != expected) throw new InvalidOperationException("修改已有档案的成员名单应重新计算房间号");
                profiles.SelectedItem = legacy;
                if (code.Text != expected) throw new InvalidOperationException("加载空房间号的档案时未自动填号");
                uids.Text = "222222222";
                if (code.Text.Length != 0) throw new InvalidOperationException("名单缺少本机 UID 时应清除自动号码");
                uids.Text = "111111111, 333333333";
                var updated = AssistConfigManager.GenerateControlRoomCode(["111111111", "333333333"]);
                if (code.Text != updated || updated == expected) throw new InvalidOperationException("名单变化后未重新计算房间号");
                note.Text = "旧房间队友";
                ((Button)dialog.FindName("SaveProfileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var saved = new AssistConfigManager().Load();
                persisted = saved.RoomProfiles.Single(room => room.Id == legacy.Id).RoomCode == updated
                    && saved.RoomProfiles.Single(room => room.Id == legacy.Id).Note == "旧房间队友"
                    && saved.ActiveRoomProfileId == config.ActiveRoomProfileId;
                if (!dialog.IsVisible) throw new InvalidOperationException("保存后窗口不应关闭");
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += (_, _) =>
                {
                    var confirmation = FindWindow("#32770", "移除本机档案");
                    if (confirmation == IntPtr.Zero) return;
                    GetWindowThreadProcessId(confirmation, out var processId);
                    if (processId != (uint)Environment.ProcessId) return;
                    timer.Stop();
                    PostMessage(confirmation, 0x0111, (IntPtr)6, IntPtr.Zero); // WM_COMMAND, IDYES
                };
                timer.Start();
                try { ((Button)dialog.FindName("RemoveProfileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                finally { timer.Stop(); }
                if (!dialog.IsVisible || config.RoomProfiles.Any(room => room.Id == legacy.Id)
                    || ((AssistantRoomProfile)profiles.SelectedItem).Id != config.ActiveRoomProfileId)
                    throw new InvalidOperationException("移除后窗口应保持打开并选中当前房间");
                if (((Button)dialog.FindName("RemoveProfileButton")).Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("移除后当前房间仍须受到保护");
            }
            catch (Exception ex) { failure = ex; }
            finally { if (dialog is { IsVisible: true }) dialog.Close(); }
        }));
        vm.OpenAssistantRoomsCommand.Execute(null);
        if (failure is not null) throw failure;
        if (!persisted) throw new InvalidOperationException("应保存自动计算的旧号码并保留当前房间");
        Console.WriteLine("PASS: UID edits recalculate room numbers; saving and removing keep the editor open.");
    }

    private static void AssertFixedLocalRoom(Application application, MainViewModel vm)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var configField = typeof(MainViewModel).GetField("_config", flags)!;
        var legacyConfig = new AssistConfig { PlayerUid = "111111111", TeamUids = ["111111111"], ControlRoomPassword = "fixture" };
        if (!legacyConfig.EnsureRoomProfiles() || legacyConfig.LocalRoomProfileId != legacyConfig.ActiveRoomProfileId)
            throw new InvalidOperationException("原始旧格式迁移时应保留本机房间身份");
        var unresolved = new AssistConfig { ActiveRoomProfileId = "missing", RoomProfiles = [new AssistantRoomProfile { Id = "other" }] };
        var ensureLocal = typeof(AssistConfig).GetMethod("EnsureLocalRoomProfile", flags)!;
        if ((bool)ensureLocal.Invoke(unresolved, null)! || unresolved.LocalRoomProfileId.Length != 0)
            throw new InvalidOperationException("原设置没有可唯一确定的当前房间时不得猜测本机房间");
        var original = vm.Config!;
        var home = new AssistantRoomProfile { Id = "home", Name = "本机测试房间", RoomCode = "HOME01",
            ServerUrl = "https://home.example.test", Password = "home-password", TeamUids = ["111111111", "222222222"] };
        var other = new AssistantRoomProfile { Id = "other", Name = "当前测试房间", RoomCode = "OTHER1",
            ServerUrl = "https://other.example.test", Password = "other-password", TeamUids = ["111111111", "333333333"] };
        var config = new AssistConfig { PlayerUid = "111111111", PlayerName = "测试", StandaloneMode = true,
            RoomProfiles = [home, other], ActiveRoomProfileId = other.Id, LocalRoomProfileId = home.Id };
        other.ApplyTo(config);
        configField.SetValue(vm, config);
        try
        {
            Exception? failure = null;
            application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = application.Windows.OfType<AssistantRoomProfilesWindow>().Single();
                try
                {
                    var profiles = (ListBox)dialog.FindName("ProfilesList");
                    var activeMarker = RoomMarker(profiles, other);
                    var localMarker = RoomMarker(profiles, home);
                    vm.IsConnected = true;
                    if (!other.IsActiveRoom || other.IsLocalRoom || activeMarker.Visibility != Visibility.Visible
                        || activeMarker.Text != "连接中房间 · 不可删除" || localMarker.Visibility != Visibility.Collapsed)
                        throw new InvalidOperationException("当前连接与本机房间不同时应分别标识");
                    var activeRemove = (Button)dialog.FindName("RemoveProfileButton");
                    if (activeRemove.Visibility != Visibility.Collapsed) throw new InvalidOperationException("当前连接房间不可删除");
                    activeRemove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (!config.RoomProfiles.Contains(other)) throw new InvalidOperationException("当前连接房间不应被移除");
                    ((TextBox)dialog.FindName("RoomNoteBox")).Text = "当前房间成员";
                    ((Button)dialog.FindName("SaveProfileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    other = (AssistantRoomProfile)profiles.SelectedItem;
                    if (other.Note != "当前房间成员") throw new InvalidOperationException("当前房间备注应可保存");
                    vm.IsConnected = false;
                    profiles.SelectedItem = home;
                    var hint = ((TextBlock)dialog.FindName("EditorHint")).Text;
                    if (!home.IsLocalRoom || !hint.Contains("不可删除") || !hint.Contains("设置页面中的「本机设置」"))
                        throw new InvalidOperationException("本机房间应说明不可删除，并标明在设置页面中的本机设置修改");
                    var remove = (Button)dialog.FindName("RemoveProfileButton");
                    if (remove.Visibility != Visibility.Collapsed) throw new InvalidOperationException("本机房间不应显示移除按钮");
                    remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (!config.RoomProfiles.Contains(home)) throw new InvalidOperationException("切房后本机房间被移除");
                    ((TextBox)dialog.FindName("RoomNoteBox")).Text = "本机房间成员";
                    ((Button)dialog.FindName("SaveProfileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    home = (AssistantRoomProfile)profiles.SelectedItem;
                    var saved = new AssistConfigManager().Load();
                    if (saved.RoomProfiles.Single(room => room.Id == home.Id).Note != "本机房间成员"
                        || saved.RoomProfiles.Single(room => room.Id == other.Id).Note != "当前房间成员")
                        throw new InvalidOperationException("本机与当前房间的备注应分别保存");
                }
                catch (Exception ex) { failure = ex; }
                finally { dialog.Close(); }
            }));
            vm.OpenAssistantRoomsCommand.Execute(null);
            if (failure is not null) throw failure;

            // 旧版“本机设置”编辑的是当前房间；首次加载须自动固定该房间。
            config.LocalRoomProfileId = "";
            config.ActiveRoomProfileId = home.Id;
            home.ApplyTo(config);
            var legacyJson = System.Text.Json.JsonSerializer.SerializeToNode(config)!.AsObject();
            legacyJson.Remove("localRoomProfileId");
            System.IO.File.WriteAllText(System.IO.Path.Combine(AssistantDataDirectory.Root, "assistant-config.json"), legacyJson.ToJsonString());
            var manager = new AssistConfigManager();
            config = manager.Load();
            if (config.LocalRoomProfileId != home.Id || manager.Load().LocalRoomProfileId != home.Id)
                throw new InvalidOperationException("旧配置应自动固定并保存原本机设置中的房间");
            home = config.LocalRoomProfile!;
            other = config.RoomProfiles.Single(room => room.Id == other.Id);
            other.ApplyTo(config);
            config.ActiveRoomProfileId = other.Id;
            if (!manager.TrySave(config)) throw new InvalidOperationException("切房后未能保存隔离配置");
            config = manager.Load();
            home = config.LocalRoomProfile!;
            other = config.ActiveRoomProfile!;
            configField.SetValue(vm, config);
            application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = application.Windows.OfType<AssistantRoomProfilesWindow>().Single();
                try
                {
                    var profiles = (ListBox)dialog.FindName("ProfilesList");
                    profiles.SelectedItem = home;
                    if (dialog.FindName("SetLocalProfileButton") is not null || !home.IsLocalRoom
                        || ((Button)dialog.FindName("RemoveProfileButton")).Visibility != Visibility.Collapsed)
                        throw new InvalidOperationException("自动接续的本机房间应保持标识和保护，不能显示手动指定按钮");
                }
                catch (Exception ex) { failure = ex; }
                finally { dialog.Close(); }
            }));
            vm.OpenAssistantRoomsCommand.Execute(null);
            if (failure is not null) throw failure;

            AssistConfig? savedSettings = null;
            var settings = (SettingsWindow)Activator.CreateInstance(typeof(SettingsWindow), flags, null, [config], null)!;
            try
            {
                if (((PasswordBox)settings.FindName("PasswordBox")).Password != home.Password)
                    throw new InvalidOperationException("本机设置应始终显示本机房间，而不是当前房间");
                ((TextBox)settings.FindName("TeamUidsBox")).Text = "111111111, 444444444";
                var saved = (AssistConfig?)typeof(SettingsWindow).GetMethod("BuildConfig", flags)!.Invoke(settings, null)
                    ?? throw new InvalidOperationException("本机设置未能保存");
                var expected = AssistConfigManager.GenerateControlRoomCode(["111111111", "444444444"]);
                if (saved.LocalRoomProfile?.RoomCode != expected || saved.ActiveRoomProfileId != other.Id
                    || saved.EffectiveRoomCode != other.RoomCode || saved.ControlRoomPassword != other.Password
                    || saved.LocalRoomProfile?.Note != "本机房间成员" || saved.ActiveRoomProfile?.Note != "当前房间成员")
                    throw new InvalidOperationException("本机设置修改应同步房间号，并保留各房间备注");
                var reloaded = System.Text.Json.JsonSerializer.Deserialize<AssistConfig>(System.Text.Json.JsonSerializer.Serialize(saved))!;
                if (reloaded.LocalRoomProfileId != home.Id || reloaded.LocalRoomProfile?.RoomCode != expected)
                    throw new InvalidOperationException("本机房间身份与更新后的房间号必须持久化");
                if (!new AssistConfigManager().TrySave(saved)) throw new InvalidOperationException("本机设置未能写入隔离配置");
                savedSettings = new AssistConfigManager().Load();
            }
            finally { settings.Close(); }
            var connectedConfig = System.Text.Json.JsonSerializer.Deserialize<AssistConfig>(System.Text.Json.JsonSerializer.Serialize(config))!;
            connectedConfig.ActiveRoomProfileId = home.Id;
            connectedConfig.LocalRoomProfile!.ApplyTo(connectedConfig);
            var connectedSettings = (SettingsWindow)Activator.CreateInstance(typeof(SettingsWindow), flags, null, [connectedConfig], null)!;
            try
            {
                ((TextBox)connectedSettings.FindName("TeamUidsBox")).Text = "111111111, 444444444";
                var updated = (AssistConfig?)typeof(SettingsWindow).GetMethod("BuildConfig", flags)!.Invoke(connectedSettings, null)
                    ?? throw new InvalidOperationException("连接本机房间时应允许修改成员 UID");
                var expectedCode = AssistConfigManager.GenerateControlRoomCode(["111111111", "444444444"]);
                if (updated.EffectiveRoomCode != expectedCode || updated.LocalRoomProfile?.RoomCode != expectedCode
                    || !updated.TeamUids.Contains("444444444"))
                    throw new InvalidOperationException("保存本机设置后当前房间和本机档案应同步更新");
            }
            finally { connectedSettings.Close(); }
            configField.SetValue(vm, savedSettings);
            application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = application.Windows.OfType<AssistantRoomProfilesWindow>().Single();
                try
                {
                    var profiles = (ListBox)dialog.FindName("ProfilesList");
                    profiles.SelectedItem = savedSettings!.LocalRoomProfile;
                    var code = ((TextBox)dialog.FindName("RoomCodeBox")).Text;
                    if (code != AssistConfigManager.GenerateControlRoomCode(["111111111", "444444444"]))
                        throw new InvalidOperationException("重新打开房间管理后应显示本机设置更新的房间号");
                }
                catch (Exception ex) { failure = ex; }
                finally { dialog.Close(); }
            }));
            vm.OpenAssistantRoomsCommand.Execute(null);
            if (failure is not null) throw failure;
            Console.WriteLine("PASS: fixed local room stays protected after switching; local settings update its saved room code.");
        }
        finally { configField.SetValue(vm, original); }
    }

    private static void AssertAssistantRoomPopup(MainWindow window, AssistantRoomProfile current, AssistantRoomProfile target)
    {
        var trigger = (Button)window.FindName("HeaderRoomButton");
        var popup = (Popup)window.FindName("AssistantRoomPopup");
        var list = (ListBox)window.FindName("AssistantRoomSelector");
        var confirm = (Button)window.FindName("ConfirmAssistantRoomSwitchButton");
        if (popup.IsOpen || list.IsVisible || popup.PlacementTarget != trigger)
            throw new InvalidOperationException("房间列表不得常驻成员区，须锚定到右上角房间号");
        trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        if (!popup.IsOpen || !list.IsVisible || list.Items.Count != 2 || list.SelectedItem != current)
            throw new InvalidOperationException("点击房间号应打开面板并选中当前档案");
        list.UpdateLayout();
        var item = list.ItemContainerGenerator.ContainerFromItem(target) as ListBoxItem
            ?? throw new InvalidOperationException("房间下拉列表项未生成");
        var presenter = FindChild<ContentPresenter>(item) ?? throw new InvalidOperationException("房间列表内容未生成");
        var template = presenter.ContentTemplate ?? throw new InvalidOperationException("房间列表模板未生成");
        var name = (TextBlock)template.FindName("RoomChoiceName", presenter);
        var note = (TextBlock)template.FindName("RoomChoiceNote", presenter);
        var code = (TextBlock)template.FindName("RoomChoiceCode", presenter);
        static Rect RowBounds(FrameworkElement element, Visual root) => new(element.TransformToAncestor(root).Transform(new Point()), element.RenderSize);
        var nameBounds = RowBounds(name, item); var noteBounds = RowBounds(note, item); var codeBounds = RowBounds(code, item);
        if (note.Text != target.Note || note.ActualWidth <= 0 || name.MaxWidth != 110
            || name.TextTrimming != TextTrimming.CharacterEllipsis || note.TextTrimming != TextTrimming.CharacterEllipsis
            || noteBounds.Left < nameBounds.Right + 7 || codeBounds.Left < noteBounds.Right + 9
            || codeBounds.Right > item.ActualWidth || ((FrameworkElement)popup.Child).Width != 330)
            throw new InvalidOperationException("房间名称、备注和房间号应保持间距、截断长文本且不改变菜单宽度");
        list.SelectedItem = target;
        list.UpdateLayout();
        var currentItem = list.ItemContainerGenerator.ContainerFromItem(current) as ListBoxItem
            ?? throw new InvalidOperationException("当前登录房间列表项未生成");
        var goldText = window.FindResource("GoldText").ToString();
        var whiteText = window.FindResource("White").ToString();
        if (currentItem.Foreground.ToString() != goldText || item.Foreground.ToString() != whiteText)
            throw new InvalidOperationException("点其他未登录房间时，当前登录房间必须保持金黄色，未登录房间必须保持白色");
        if (confirm.CommandParameter != target || confirm.Command == null || !confirm.IsEnabled
            || window.ViewModel.ActiveRoomProfile != current)
            throw new InvalidOperationException("选择候选房间只更新切换参数，不得直接替换当前档案");
        if (window.ViewModel.ViewedRoomProfileId != target.Id)
            throw new InvalidOperationException("点未登录房间应同步切换到该房间的只读查看视图");
        var allRoomsBtn = (Button)window.FindName("AllRoomsViewButton");
        allRoomsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!window.ViewModel.IsViewingAllRooms)
            throw new InvalidOperationException("点击「所有房间」入口应切回所有房间汇总视图");
        if (Environment.GetEnvironmentVariable("ROOM_POPUP_SNAPSHOT") is { Length: > 0 } shot)
        {
            var panel = (FrameworkElement)popup.Child;
            panel.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(panel.ActualWidth + panel.Margin.Left + panel.Margin.Right),
                (int)Math.Ceiling(panel.ActualHeight + panel.Margin.Top + panel.Margin.Bottom), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(panel);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = System.IO.File.Create(shot);
            png.Save(output);
        }
        var source = PresentationSource.FromVisual(list) ?? throw new InvalidOperationException("面板未连接到 WPF 渲染源");
        list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        if (popup.IsOpen) throw new InvalidOperationException("Esc 应关闭房间面板");
        trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        if (!popup.IsOpen || list.SelectedItem != current)
            throw new InvalidOperationException("重开面板不得恢复未确认的候选房间");
        popup.IsOpen = false;
        Console.WriteLine("PASS: header room popup opens, binds the chosen room, closes with Escape and resets selection.");
    }

    private static void AssertAssistantRoomPopupAfterProfileChanges(MainWindow window, AssistantRoomProfile current, int expectedCount)
    {
        var trigger = (Button)window.FindName("HeaderRoomButton");
        var popup = (Popup)window.FindName("AssistantRoomPopup");
        var list = (ListBox)window.FindName("AssistantRoomSelector");
        var confirm = (Button)window.FindName("ConfirmAssistantRoomSwitchButton");
        trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        if (!popup.IsOpen || !list.IsVisible || list.Items.Count != expectedCount
            || list.SelectedItem is not AssistantRoomProfile selected || selected.Id != current.Id)
            throw new InvalidOperationException($"增删改档案后重开房间面板异常：count={list.Items.Count} expected={expectedCount}");
        var added = list.Items.OfType<AssistantRoomProfile>().Last();
        list.SelectedItem = added;
        if (!ReferenceEquals(confirm.CommandParameter, added) || !confirm.IsEnabled)
            throw new InvalidOperationException("增删改档案后选择新房间时，切换按钮未正确绑定目标");
        popup.IsOpen = false;
        Console.WriteLine("PASS: header room popup reopens cleanly after adding/editing room profiles.");
    }

    private static TextBlock RoomMarker(ListBox profiles, AssistantRoomProfile room)
    {
        profiles.UpdateLayout();
        var item = profiles.ItemContainerGenerator.ContainerFromItem(room) as ListBoxItem
            ?? throw new InvalidOperationException("房间列表项未生成");
        var presenter = FindChild<ContentPresenter>(item)
            ?? throw new InvalidOperationException("房间列表内容未生成");
        return presenter.ContentTemplate?.FindName("CurrentRoomMarker", presenter) as TextBlock
            ?? throw new InvalidOperationException("缺少当前房间标识");
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is T nested) return nested;
        }
        return null;
    }
}
