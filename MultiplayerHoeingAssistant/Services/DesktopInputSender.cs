using System.ComponentModel;
using System.Runtime.InteropServices;
using BgiCoordinatorServer.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>一次完整的鼠标/白名单按键操作。没有全局键盘钩子，不启动或暂停 BGI。</summary>
public class DesktopInputSender
{
    internal const uint KeyUp = 0x0002, ExtendedKey = 0x0001;
    internal const uint MouseMove = 0x0001, LeftDown = 0x0002, LeftUp = 0x0004,
        RightDown = 0x0008, RightUp = 0x0010, VirtualDesk = 0x4000, Absolute = 0x8000;
    private readonly IDesktopInputNative _native;
    public DesktopInputSender() : this(new DesktopInputNative()) { }
    internal DesktopInputSender(IDesktopInputNative native) => _native = native;

    public virtual void Send(DesktopControlCommand command, DesktopCaptureGeometry geometry,
        CancellationToken cancellationToken, Action? beforeInput = null)
    {
        if (!DesktopControlProtocol.IsValid(command) || !geometry.IsValid) throw new ArgumentException("输入参数无效");
        using var dpi = _native.EnterDpi();
        _native.RequireInteractiveDesktop();
        if (_native.GetGeometry() != geometry) throw new InvalidOperationException("显示布局已变化，请刷新画面");
        var keys = command.Action == "key" ? GetKeys(command.Key) : [];
        // 不释放用户自己按住的键；避免把一次 F/左键叠成其他组合键。
        ushort[] modifiers = [0x10, 0x11, 0x12, 0x5B, 0x5C];
        if (modifiers.Concat(keys).Distinct().Any(_native.IsKeyDown)
            || (command.Action != "key" && (_native.IsKeyDown(0x01) || _native.IsKeyDown(0x02))))
            throw new InvalidOperationException("本机有按住的按键或鼠标，请松开后刷新画面");
        var point = command.Action == "key" ? (X: 0, Y: 0)
            : ToAbsolute(geometry, command.X, command.Y, _native.VirtualBounds());
        var held = new List<DesktopInputPacket>();
        Exception? failure = null;
        try
        {
            if (command.Action == "key")
            {
                foreach (var key in keys)
                {
                    var packet = DesktopInputPacket.Keyboard(key, IsExtended(key) ? ExtendedKey : 0);
                    Down(packet);
                }
                foreach (var packet in held.ToArray().Reverse()) Up(packet);
            }
            else
            {
                Write(DesktopInputPacket.Mouse(point.X, point.Y, MouseMove | Absolute | VirtualDesk));
                _native.Pause(40);
                var down = command.Action == "right" ? RightDown : LeftDown;
                var packet = DesktopInputPacket.Mouse(0, 0, down);
                Down(packet);
                Up(packet);
                if (command.Action == "double")
                {
                    _native.Pause(60);
                    Down(packet);
                    Up(packet);
                }
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            // 只释放本次合成且尚未成功抬起的键。取消/部分发送失败仍执行；不受授权开关阻挡清理。
            foreach (var packet in held.ToArray().Reverse())
            {
                try { WriteRaw(Release(packet)); held.Remove(packet); }
                catch (Exception ex) { failure = failure == null ? ex : new AggregateException(failure, ex); }
            }
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return;

        void Write(DesktopInputPacket packet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeInput?.Invoke();
            _native.RequireInteractiveDesktop();
            WriteRaw(packet);
        }
        void WriteRaw(DesktopInputPacket packet)
        {
            if (_native.Send([packet]) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "操作系统未接受完整输入（可能被权限或安全桌面阻止）");
        }
        void Down(DesktopInputPacket packet)
        {
            // 原生调用抛异常时也执行清理；返回零不会被误判为完整操作成功。
            cancellationToken.ThrowIfCancellationRequested();
            beforeInput?.Invoke();
            _native.RequireInteractiveDesktop();
            held.Add(packet);
            WriteRaw(packet);
            _native.Pause(40);
        }
        void Up(DesktopInputPacket packet)
        {
            Write(Release(packet));
            held.Remove(packet);
            _native.Pause(40);
        }
    }

    private static DesktopInputPacket Release(DesktopInputPacket packet)
        => packet.Type == 1 ? packet with { Flags = packet.Flags | KeyUp }
            : packet with { Flags = packet.Flags == RightDown ? RightUp : LeftUp };

    private static bool IsExtended(ushort key) => key is >= 0x25 and <= 0x28 or 0x5B or 0x5C;
    private static ushort[] GetKeys(string key) => key switch
    {
        "Esc" => [0x1B], "Enter" => [0x0D], "Space" => [0x20], "Tab" => [0x09], "F" => [0x46],
        "W" => [0x57], "S" => [0x53], "A" => [0x41], "D" => [0x44],
        "F11" => [0x7A], "F12" => [0x7B], "Alt+F4" => [0x12, 0x73], "Win+D" => [0x5B, 0x44],
        _ => throw new ArgumentException("不支持的键盘按钮")
    };

    internal static (int X, int Y) ToAbsolute(DesktopCaptureGeometry geometry, double x, double y,
        (int Left, int Top, int Width, int Height) bounds)
    {
        if (!geometry.IsValid || !double.IsFinite(x) || !double.IsFinite(y)
            || x is < 0 or > 1 || y is < 0 or > 1 || bounds.Width <= 1 || bounds.Height <= 1
            || geometry.Left < bounds.Left || geometry.Top < bounds.Top
            || (long)geometry.Left + geometry.Width > (long)bounds.Left + bounds.Width
            || (long)geometry.Top + geometry.Height > (long)bounds.Top + bounds.Height)
            throw new ArgumentException("画面不属于当前物理桌面");
        var physicalX = (long)geometry.Left + (long)Math.Round(x * (geometry.Width - 1));
        var physicalY = (long)geometry.Top + (long)Math.Round(y * (geometry.Height - 1));
        return ((int)Math.Round((physicalX - bounds.Left) * 65535d / (bounds.Width - 1)),
            (int)Math.Round((physicalY - bounds.Top) * 65535d / (bounds.Height - 1)));
    }
}

internal readonly record struct DesktopInputPacket(uint Type, int X, int Y, ushort Key, uint Flags)
{
    internal static DesktopInputPacket Keyboard(ushort key, uint flags) => new(1, 0, 0, key, flags);
    internal static DesktopInputPacket Mouse(int x, int y, uint flags) => new(0, x, y, 0, flags);
}

internal interface IDesktopInputNative
{
    IDisposable EnterDpi();
    void RequireInteractiveDesktop();
    DesktopCaptureGeometry? GetGeometry();
    (int Left, int Top, int Width, int Height) VirtualBounds();
    bool IsKeyDown(ushort key);
    uint Send(DesktopInputPacket[] packets);
    void Pause(int milliseconds);
}

internal sealed class DesktopInputNative : IDesktopInputNative
{
    public IDisposable EnterDpi() => DesktopDisplayLayout.EnterPerMonitorV2();
    public void RequireInteractiveDesktop() => DesktopDisplayLayout.RequireInteractiveDesktop();
    public DesktopCaptureGeometry? GetGeometry() => DesktopDisplayLayout.GetGeometry();
    public (int Left, int Top, int Width, int Height) VirtualBounds() => DesktopDisplayLayout.VirtualBounds();
    public bool IsKeyDown(ushort key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    public void Pause(int milliseconds) => Thread.Sleep(milliseconds);
    public uint Send(DesktopInputPacket[] packets)
    {
        var inputs = packets.Select(packet => new Input
        {
            Type = packet.Type,
            Data = packet.Type == 1 ? new InputUnion { Keyboard = new KeyboardInput { VirtualKey = packet.Key, Flags = packet.Flags } }
                : new InputUnion { Mouse = new MouseInput { X = packet.X, Y = packet.Y, Flags = packet.Flags } }
        }).ToArray();
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
