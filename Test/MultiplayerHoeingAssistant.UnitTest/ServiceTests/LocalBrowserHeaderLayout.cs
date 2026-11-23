using System.Reflection;
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
        var targetRoom = new AssistantRoomProfile { Id = "target", Name = "周末联机", RoomCode = "B98765",
            ServerUrl = "https://rooms-b.example.test", ExpectedHoeingPlayers = 3, TeamUids = ["111111111", "222222222", "333333333"],
            Password = "fixture", ScheduledOnlineTime = "20:00", OnlineHoeingGroupNames = ["周末路线"] };
        typeof(MainViewModel).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, new AssistConfig { StandaloneMode = true, ObserverMode = true, DisclaimerAccepted = true,
                PlayerUid = "111111111", ServerUrl = currentRoom.ServerUrl,
                ActiveRoomProfileId = currentRoom.Id, RoomProfiles = [currentRoom, targetRoom] });
        vm.RoomCode = currentRoom.RoomCode;
        var window = new MainWindow(vm) { Height = 820, Width = 980, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        window.Show();
        // 本机网页入口已移到设置页，顶栏只保留设置与退出。
        if (window.FindName("HeaderLocalWebButton") != null) throw new InvalidOperationException("顶栏不应再出现本机网页按钮");
        foreach (var width in new[] { 980d, 1180d, 1440d })
        {
            window.Width = width;
            window.UpdateLayout();
            var settings = (FrameworkElement)window.FindName("HeaderSettingsButton");
            var exit = (FrameworkElement)window.FindName("HeaderExitButton");
            var room = (FrameworkElement)window.FindName("HeaderRoomButton");
            var refresh = (FrameworkElement)window.FindName("HeaderRefreshButton");
            var star = (FrameworkElement)window.FindName("StarBadge");
            var starHost = (FrameworkElement)window.FindName("HeaderStarHost");
            static Rect Bounds(FrameworkElement e, Visual root)
            {
                var point = e.TransformToAncestor(root).Transform(new Point(0, 0));
                return new Rect(point, e.RenderSize);
            }
            var s = Bounds(settings, window); var x = Bounds(exit, window);
            var r = Bounds(room, window); var f = Bounds(refresh, window); var h = Bounds(starHost, window); var t = Bounds(star, window);
            var label = Bounds((FrameworkElement)window.FindName("HeaderRoomLabel"), window);
            Console.WriteLine($"width={width} settings={s} exit={x} room={r} label={label} refresh={f} starHost={h} star={t}");
            if (Math.Abs(s.Left-r.Left)>1 || Math.Abs(s.Right-r.Right)>1 || Math.Abs(x.Right-f.Right)>1 ||
                s.Right>x.Left || x.Right>window.ActualWidth ||
                (star.IsVisible && (t.Left<h.Left || t.Right>h.Right)))
                throw new InvalidOperationException($"头部控件在 {width} 宽度出现遮挡或错位");
            if (label.Left < r.Left + 4 || label.Right > r.Right - 4 || label.Top < r.Top || label.Bottom > r.Bottom)
                throw new InvalidOperationException($"宽字符房间号及箭头在 {width} 宽度未完整留在虚线框内");
        }
        AssertAssistantRoomPopup(window, currentRoom, targetRoom);
        AssertRoomProfilesEditor(application, vm, currentRoom, targetRoom);
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
                if (server.Text != current.ServerUrl || !server.IsReadOnly || !count.IsEnabled)
                    throw new InvalidOperationException("当前档案需显示服务器并允许修改人数");
                profiles.SelectedItem = target;
                if (server.IsReadOnly || server.Text != target.ServerUrl || !Equals(count.SelectedItem, target.ExpectedHoeingPlayers))
                    throw new InvalidOperationException("切换编辑对象后，未加载该房间的地址和开锄人数");
                var details = (TextBlock)dialog.FindName("HoeingSummary");
                if (!details.Text.Contains("周末路线") || !details.Text.Contains("20:00"))
                    throw new InvalidOperationException("缺少档案自身的联机设置信息");
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
                    || saved.RoomProfiles.Single(room => room.Id == target.Id).ExpectedHoeingPlayers != 2)
                    throw new InvalidOperationException("保存开锄人数不得影响另一个房间");
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
        list.SelectedItem = target;
        if (confirm.CommandParameter != target || confirm.Command == null || !confirm.IsEnabled
            || window.ViewModel.ActiveRoomProfile != current)
            throw new InvalidOperationException("选择候选房间只更新切换参数，不得直接替换当前档案");
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
