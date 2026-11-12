using BgiCoordinatorServer.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public class DesktopInputSenderTests
{
    [Theory]
    [InlineData("W", 0x57)]
    [InlineData("S", 0x53)]
    [InlineData("A", 0x41)]
    [InlineData("D", 0x44)]
    public void MovementButtonsSendOneMatchingKeyAndRelease(string key, int virtualKey)
    {
        var native = new FakeNative();
        new DesktopInputSender(native).Send(Command("key", key), native.Geometry, default);
        Assert.Equal(new[] { virtualKey, virtualKey }, native.Packets.Select(x => (int)x.Key));
        Assert.Equal(0u, native.Packets[0].Flags & DesktopInputSender.KeyUp);
        Assert.NotEqual(0u, native.Packets[1].Flags & DesktopInputSender.KeyUp);
        Assert.Empty(native.Held);
    }

    [Theory]
    [InlineData("Win+D", 0x5B, 0x44)]
    [InlineData("Alt+F4", 0x12, 0x73)]
    public void ChordsPressAndReleaseInReverseOrder(string key, int modifier, int letter)
    {
        var native = new FakeNative();
        new DesktopInputSender(native).Send(Command("key", key), native.Geometry, default);
        Assert.Equal(new[] { modifier, letter, letter, modifier }, native.Packets.Select(x => (int)x.Key));
        Assert.All(native.Packets.Take(2), x => Assert.Equal(0u, x.Flags & DesktopInputSender.KeyUp));
        Assert.All(native.Packets.Skip(2), x => Assert.NotEqual(0u, x.Flags & DesktopInputSender.KeyUp));
        Assert.Empty(native.Held);
        Assert.True(native.DpiRestored);
        Assert.All(native.Delays, x => Assert.True(x >= 30));
    }

    [Theory]
    [InlineData("Win+D")]
    [InlineData("Alt+F4")]
    public void PartialNativeFailureReportsErrorAndFinallyReleasesSyntheticKeys(string key)
    {
        var native = new FakeNative { FailSend = 2 };
        Assert.ThrowsAny<Exception>(() => new DesktopInputSender(native).Send(Command("key", key), native.Geometry, default));
        Assert.Empty(native.Held);
        Assert.True(native.DpiRestored);
        Assert.NotEqual(0u, native.Packets.Last().Flags & DesktopInputSender.KeyUp);
        Assert.Equal(key == "Win+D" ? 0x5B : 0x12, native.Packets.Last().Key);
    }

    [Fact]
    public void CancellationMidChordReleasesOnlyTheSyntheticModifier()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new FakeNative { OnPause = cancellation.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new DesktopInputSender(native).Send(Command("key", "Win+D"), native.Geometry, cancellation.Token));
        Assert.Equal(new ushort[] { 0x5B, 0x5B }, native.Packets.Select(x => x.Key));
        Assert.Empty(native.Held);
        Assert.True(native.DpiRestored);
    }

    [Fact]
    public void ExistingPhysicalModifierIsNotReleasedOrCombinedWithInput()
    {
        var native = new FakeNative(); native.Held.Add(0x11);
        Assert.Throws<InvalidOperationException>(() =>
            new DesktopInputSender(native).Send(Command("key", "F"), native.Geometry, default));
        Assert.Empty(native.Packets);
        Assert.Equal(new ushort[] { 0x11 }, native.Held);
        Assert.True(native.DpiRestored);
    }

    [Fact]
    public void LockedOrSecureDesktopFailsBeforeAnyInputAndRestoresDpi()
    {
        var native = new FakeNative { SecureDesktop = true };
        Assert.Throws<InvalidOperationException>(() =>
            new DesktopInputSender(native).Send(Command("key", "Win+D"), native.Geometry, default));
        Assert.Empty(native.Packets);
        Assert.True(native.DpiRestored);
    }

    [Fact]
    public void GeometryMustStillMatchInsideNativeDpiScope()
    {
        var native = new FakeNative();
        Assert.Throws<InvalidOperationException>(() =>
            new DesktopInputSender(native).Send(Command("left"), native.Geometry with { LayoutId = "old-layout" }, default));
        Assert.Empty(native.Packets);
        Assert.True(native.DpiRestored);
    }

    [Theory]
    [InlineData("left", 1, false)]
    [InlineData("right", 1, true)]
    [InlineData("double", 2, false)]
    public void MouseInputMovesInPhysicalVirtualDesktopThenSendsCompleteClicks(string action, int clicks, bool right)
    {
        var native = new FakeNative();
        new DesktopInputSender(native).Send(Command(action), native.Geometry, default);
        var first = native.Packets[0];
        var expected = DesktopInputSender.ToAbsolute(native.Geometry, 0.5, 0.5, native.VirtualBounds());
        Assert.Equal(expected.X, first.X);
        Assert.Equal(expected.Y, first.Y);
        Assert.Equal(DesktopInputSender.MouseMove | DesktopInputSender.Absolute | DesktopInputSender.VirtualDesk, first.Flags);
        Assert.Equal(clicks, native.Packets.Count(x => x.Flags == (right ? DesktopInputSender.RightDown : DesktopInputSender.LeftDown)));
        Assert.Equal(clicks, native.Packets.Count(x => x.Flags == (right ? DesktopInputSender.RightUp : DesktopInputSender.LeftUp)));
        Assert.Empty(native.Held);
        Assert.True(native.DpiRestored);
    }

    [Fact]
    public void PhysicalMappingHandlesScaledPrimaryAndNegativeMonitorOrigins()
    {
        var geometry = new DesktopCaptureGeometry { Left = 0, Top = 0, Width = 3840, Height = 2160, DisplayId = "primary", LayoutId = "150%-4k" };
        var bounds = (Left: -1920, Top: -1080, Width: 5760, Height: 3240);
        var topLeft = DesktopInputSender.ToAbsolute(geometry, 0, 0, bounds);
        var bottomRight = DesktopInputSender.ToAbsolute(geometry, 1, 1, bounds);
        Assert.Equal((int)Math.Round(1920 * 65535d / 5759), topLeft.X);
        Assert.Equal((int)Math.Round(1080 * 65535d / 3239), topLeft.Y);
        Assert.Equal(65535, bottomRight.X);
        Assert.Equal(65535, bottomRight.Y);
        Assert.Throws<ArgumentException>(() => DesktopInputSender.ToAbsolute(geometry, 1.01, 0, bounds));
        Assert.Throws<ArgumentException>(() => DesktopInputSender.ToAbsolute(geometry, double.NaN, 0, bounds));
    }

    private static DesktopControlCommand Command(string action, string key = "") => new()
    {
        RequestId = Guid.NewGuid().ToString("N"), TargetUid = "local", FrameId = Guid.NewGuid().ToString("N"),
        ControlId = Guid.NewGuid().ToString("N"), Action = action, Key = key, X = 0.5, Y = 0.5
    };

    private sealed class FakeNative : IDesktopInputNative
    {
        public readonly DesktopCaptureGeometry Geometry = new() { Width = 3840, Height = 2160, DisplayId = "fake-4k", LayoutId = "fake-150%" };
        public readonly List<DesktopInputPacket> Packets = [];
        public readonly HashSet<ushort> Held = [];
        public readonly List<int> Delays = [];
        public int FailSend;
        public bool SecureDesktop;
        public bool DpiRestored;
        public Action? OnPause;
        private int _sends;
        public IDisposable EnterDpi() => new Restore(() => DpiRestored = true);
        public void RequireInteractiveDesktop() { if (SecureDesktop) throw new InvalidOperationException("锁屏或安全桌面"); }
        public DesktopCaptureGeometry? GetGeometry() => Geometry;
        public (int Left, int Top, int Width, int Height) VirtualBounds() => (-1920, -1080, 5760, 3240);
        public bool IsKeyDown(ushort key) => Held.Contains(key);
        public uint Send(DesktopInputPacket[] packets)
        {
            Packets.AddRange(packets);
            if (++_sends == FailSend) return 0;
            foreach (var packet in packets)
            {
                if (packet.Type != 1) continue;
                if ((packet.Flags & DesktopInputSender.KeyUp) == 0) Held.Add(packet.Key);
                else Held.Remove(packet.Key);
            }
            return (uint)packets.Length;
        }
        public void Pause(int milliseconds) { Delays.Add(milliseconds); OnPause?.Invoke(); }
        private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
    }
}
