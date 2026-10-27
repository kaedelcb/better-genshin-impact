using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using BetterGenshinImpact.View;
using Xunit;

namespace BetterGenshinImpact.UnitTest.ServiceTests;

public class HtmlMaskLifetimeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TaskCleanupPreservesPersistentWindowAndVisibility(bool visible)
    {
        RunSta(() =>
        {
            var windows = (ConcurrentDictionary<string, HtmlMaskWindow>)typeof(HtmlMaskWindow)
                .GetField("_windows", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Assert.Empty(windows);
            try
            {
                var persistent = CreateWindow(windows, "persistent", true);
                if (visible) persistent.Show();

                // 两次任务结束均保留同一常驻窗口；失焦时已隐藏的窗口也不能被强制显示。
                for (var task = 0; task < 2; task++)
                {
                    var temporary = CreateWindow(windows, "script-" + task, false);
                    var temporaryClosed = false;
                    temporary.Closed += (_, _) => temporaryClosed = true;
                    HtmlMaskWindow.CloseTaskWindows();
                    Assert.True(temporaryClosed);
                    Assert.Single(windows);
                    Assert.Same(persistent, windows["persistent"]);
                    Assert.Equal(visible, persistent.IsVisible);
                }

                // 截图器停止所用的全量清理仍然释放常驻窗口。
                var persistentClosed = false;
                persistent.Closed += (_, _) => persistentClosed = true;
                HtmlMaskWindow.CloseAll();
                Assert.True(persistentClosed);
                Assert.Empty(windows);
            }
            finally
            {
                HtmlMaskWindow.CloseAll();
            }
        });
    }

    private static HtmlMaskWindow CreateWindow(
        ConcurrentDictionary<string, HtmlMaskWindow> windows, string id, bool persistent)
    {
        var window = (HtmlMaskWindow)Activator.CreateInstance(typeof(HtmlMaskWindow),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { "about:blank", id, Path.GetTempPath(), persistent }, null)!;

        // 只验证真实 WPF 窗口生命周期，不初始化浏览器、不读取用户配置、不操作游戏焦点。
        var loaded = (RoutedEventHandler)typeof(HtmlMaskWindow)
            .GetMethod("OnLoaded", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!
            .CreateDelegate(typeof(RoutedEventHandler), window);
        window.Loaded -= loaded;
        window.Content = null;
        window.ShowActivated = false;
        window.Topmost = false;
        window.Left = -20000;
        window.Width = window.Height = 1;
        windows[id] = window;
        window.Closed += (_, _) =>
        {
            windows.TryRemove(id, out _);
            typeof(HtmlMaskWindow).GetMethod("DisposeWebView", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
        };
        return window;
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
