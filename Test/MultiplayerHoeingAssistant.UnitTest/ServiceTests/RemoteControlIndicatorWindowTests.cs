using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class RemoteControlIndicatorWindowTests
{
    [Fact]
    public async Task IndicatorWindowIsPassiveTransparentAndInsidePrimaryWorkArea()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Application? application = null;
            try
            {
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Dispatcher.BeginInvoke(new Action(() =>
                {
                    RemoteControlIndicatorWindow? window = null;
                    try
                    {
                        window = new RemoteControlIndicatorWindow();
                        ((IRemoteControlIndicator)window).Show();
                        var handle = new WindowInteropHelper(window).Handle;
                        Assert.NotEqual(IntPtr.Zero, handle);
                        const long required = 0x20L | 0x08000000L | 0x80L;
                        Assert.Equal(required, GetWindowLongPtr(handle, -20).ToInt64() & required);
                        Assert.True(window.IsVisible);
                        Assert.False(window.IsActive);
                        Assert.False(window.ShowInTaskbar);
                        Assert.False(window.IsHitTestVisible);
                        Assert.Equal(new IntPtr(-1), SendMessage(handle, 0x0084, IntPtr.Zero, IntPtr.Zero));
                        Assert.Equal(new IntPtr(3), SendMessage(handle, 0x0021, IntPtr.Zero, IntPtr.Zero));
                        // 模拟系统最小化消息（Win+D 同一路径），不发送实体按键或鼠标输入。
                        SendMessage(handle, 0x0112, new IntPtr(0xF020), IntPtr.Zero);
                        Assert.False(IsIconic(handle));
                        Assert.True(window.IsVisible);
                        var work = SystemParameters.WorkArea;
                        Assert.InRange(window.Left, work.Left, work.Right - window.Width);
                        Assert.InRange(window.Top, work.Top, work.Bottom - window.Height);
                        Assert.InRange(work.Right - (window.Left + window.Width), 0, 17);
                        Assert.InRange(work.Bottom - (window.Top + window.Height), 0, 17);
                        window.Close();
                        window = null;
                        done.TrySetResult();
                    }
                    catch (Exception error) { done.TrySetException(error); }
                    finally { window?.Close(); application.Shutdown(); }
                }), DispatcherPriority.Normal);
                application.Run();
            }
            catch (Exception error) { done.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
}
