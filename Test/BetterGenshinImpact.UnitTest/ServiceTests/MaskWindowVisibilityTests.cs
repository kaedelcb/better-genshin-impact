using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Mask;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.View.Mask;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BetterGenshinImpact.UnitTest.ServiceTests;

public class MaskWindowVisibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MinimizeHidesVisibleMaskAndRestoreShowsIt(bool editLayout)
    {
        var host = CreateHost(editLayout);
        Assert.True(Evaluate(host, new(true, true, false, true, default)));
        Assert.True(DesiredVisible(host));

        // 即使前台仍属于自身或启用了布局编辑，最小化也必须隐藏。
        Assert.True(Evaluate(host, new(true, false, true, true, default)));
        Assert.False(DesiredVisible(host));
        Assert.False(Evaluate(host, new(true, false, true, true, default)));
        Assert.False(DesiredVisible(host));

        Assert.True(Evaluate(host, new(true, true, false, true, default)));
        Assert.True(DesiredVisible(host));
    }

    [Fact]
    public void EditLayoutCannotRevealMinimizedMask()
    {
        var host = CreateHost(false);
        typeof(MaskWindowHost).GetField("_attached", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host, true);
        host.ReportGameWindow(new(true, false, true, false, default));
        var config = (MaskWindowConfig)typeof(MaskWindowHost)
            .GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        config.OverlayLayoutEditEnabled = true;
        Assert.False(DesiredVisible(host));
        Assert.False(host.State.IsVisible);
    }

    [Fact]
    public void ForegroundOwnershipAndCaptureStopKeepExistingBehavior()
    {
        var host = CreateHost(false);
        Evaluate(host, new(true, true, false, true, default));
        Evaluate(host, new(true, false, false, true, default));
        Assert.True(DesiredVisible(host));

        Evaluate(host, new(true, false, false, false, default));
        Assert.False(DesiredVisible(host));
        Evaluate(host, new(true, false, false, true, default));
        Assert.False(DesiredVisible(host));

        Evaluate(host, new(true, true, false, true, default));
        Evaluate(host, new(false, true, false, true, default));
        Assert.False(DesiredVisible(host));
    }

    private static MaskWindowHost CreateHost(bool editLayout)
    {
        // 只构建内存中的遮罩配置，不启动 App、读取 User 或创建游戏运行环境。
        var config = (AllConfig)RuntimeHelpers.GetUninitializedObject(typeof(AllConfig));
        config.MaskWindowConfig = new MaskWindowConfig { OverlayLayoutEditEnabled = editLayout };
        var service = new Mock<IConfigService>();
        service.Setup(x => x.Get()).Returns(config);
        return new MaskWindowHost(() => throw new InvalidOperationException("策略测试不应创建窗口"),
            service.Object, NullLogger<MaskWindowHost>.Instance);
    }

    private static bool Evaluate(MaskWindowHost host, GameWindowState state)
        => (bool)typeof(MaskWindowHost).GetMethod("EvaluateLocked",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, new object[] { state })!;

    private static bool DesiredVisible(MaskWindowHost host)
        => (bool)typeof(MaskWindowHost).GetField("_desiredVisible",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
}
