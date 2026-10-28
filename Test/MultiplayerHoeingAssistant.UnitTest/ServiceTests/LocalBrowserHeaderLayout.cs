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
        foreach (var width in new[] { 980d, 1180d, 1440d })
        {
            window.Width = width;
            window.UpdateLayout();
            var settings = (FrameworkElement)window.FindName("HeaderSettingsButton");
            var local = (FrameworkElement)window.FindName("HeaderLocalWebButton");
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
            var s = Bounds(settings, window); var b = Bounds(local, window); var x = Bounds(exit, window);
            var r = Bounds(room, window); var f = Bounds(refresh, window); var h = Bounds(starHost, window); var t = Bounds(star, window);
            Console.WriteLine($"width={width} settings={s} local={b} exit={x} room={r} refresh={f} starHost={h} star={t}");
            if (Math.Abs(s.Left-r.Left)>1 || Math.Abs(s.Right-r.Right)>1 || Math.Abs(x.Right-f.Right)>1 ||
                s.Right>b.Left || b.Right>x.Left || x.Right>window.ActualWidth ||
                (star.IsVisible && (t.Left<h.Left || t.Right>h.Right)))
                throw new InvalidOperationException($"头部控件在 {width} 宽度出现遮挡或错位");
        }
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    finally { application.Shutdown(); }
    return 0;
    }
}
