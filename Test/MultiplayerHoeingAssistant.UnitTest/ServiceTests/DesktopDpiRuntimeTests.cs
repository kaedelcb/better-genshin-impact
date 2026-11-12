using System.Runtime.InteropServices;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class DesktopDpiRuntimeTests
{
    // 真正调用 Windows DPI API，仅改变专用线程的上下文；不截图、不移动鼠标、不改系统缩放。
    // 假几何测试无法证明 PMv2 API 可用或退出后恢复原上下文，此项补上这一运行时边界。
    [Fact]
    public async Task PerMonitorV2Scope_RestoresTheOriginalThreadContext()
    {
        await Task.Run(() =>
        {
            var before = GetThreadDpiAwarenessContext();
            Assert.NotEqual(IntPtr.Zero, before);
            using (DesktopDisplayLayout.EnterPerMonitorV2())
                Assert.True(AreDpiAwarenessContextsEqual(new IntPtr(-4), GetThreadDpiAwarenessContext()));
            Assert.True(AreDpiAwarenessContextsEqual(before, GetThreadDpiAwarenessContext()));
        });
    }

    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr left, IntPtr right);
}
