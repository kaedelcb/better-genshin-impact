using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    var application = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
    application.InitializeComponent();
    try
    {
        var vm = new MainViewModel();
        typeof(MainViewModel).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, new AssistConfig { StandaloneMode = true, ObserverMode = true, DisclaimerAccepted = true });
        var window = new MainWindow(vm) { Height = 820, Width = 980, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        window.Show();
        // 本机网页入口已移到设置页，顶栏只保留设置与退出。
        if (window.FindName("HeaderLocalWebButton") != null) throw new InvalidOperationException("顶栏不应再出现本机网页按钮");
        foreach (var width in new[] { 980d, 1180d, 1440d })
        {
            window.Width = width;
            window.UpdateLayout();
            var settings = (FrameworkElement)window.FindName("HeaderSettingsButton");
            var exit = (FrameworkElement)window.FindName("HeaderExitButton");
            var room = (FrameworkElement)window.FindName("RoomCodeCopyBorder");
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
            Console.WriteLine($"width={width} settings={s} exit={x} room={r} refresh={f} starHost={h} star={t}");
            if (Math.Abs(s.Left-r.Left)>1 || Math.Abs(s.Right-r.Right)>1 || Math.Abs(x.Right-f.Right)>1 ||
                s.Right>x.Left || x.Right>window.ActualWidth ||
                (star.IsVisible && (t.Left<h.Left || t.Right>h.Right)))
                throw new InvalidOperationException($"头部控件在 {width} 宽度出现遮挡或错位");
        }
        vm.OpenSettingsCommand.Execute(null); window.UpdateLayout();
        var page = FindChild<SettingsPage>(window) ?? throw new InvalidOperationException("未找到设置页");
        var open = (Button?)page.FindName("OpenLocalWebButton") ?? throw new InvalidOperationException("设置页缺少本机网页入口");
        if (!open.IsVisible || open.ActualWidth < 40) throw new InvalidOperationException("设置页本机网页按钮不可见");
        Console.WriteLine($"settings local web button={open.ActualWidth}x{open.ActualHeight}");
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
