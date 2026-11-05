using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Input.Backends.Win32;
using BetterGenshinImpact.Core.Simulator.Extensions;
using System.Collections.Generic;
using System.Linq;
using Vanara.PInvoke;
using System;
using System.Threading;

namespace BetterGenshinImpact.Core.Input;

/// <summary>
/// 模拟输入的统一入口。
/// 业务代码只通过这里取通道，不直接使用 Simulation / PostMessageSimulator 等原始实现。
/// <para>
/// 通道引用只能在单个任务内持有；跨任务存活的对象（静态字段、DI 单例、Trigger 等）必须每次从这里取，
/// 否则后端切换后引用会过期。
/// </para>
/// </summary>
public static class InputHub
{
    private static readonly object AttachLock = new();
    private static readonly AsyncLocal<InputOperationScope?> Operation = new();

    /// <summary>
    /// 未 Attach 时：未绑定窗口的 Win32 后端，前台可用，后台 warn
    /// </summary>
    private static IInputBackend _backend = new Win32InputBackend(IntPtr.Zero);

    public static IInputBackend Backend => Volatile.Read(ref _backend);

    public static IInputChannel Foreground => Operation.Value?.Foreground ?? Backend.Foreground;

    /// <summary>
    /// 后台通道。WebSdk / Gamepad 后端下与 <see cref="Foreground"/> 相同
    /// </summary>
    public static IInputChannel Background => Operation.Value?.Background ?? Backend.Background;

    /// <summary>
    /// 替换当前后端并释放旧后端。按键释放由停止流程显式处理，避免绑定新后端时释放全局按键。
    /// 只在没有任务运行时调用。
    /// (a,b)、c、d 之间的切换都通过它完成
    /// </summary>
    public static void Attach(IInputBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        lock (AttachLock)
        {
            var old = Backend;
            if (ReferenceEquals(old, backend))
            {
                return;
            }

            Volatile.Write(ref _backend, backend);
            old.Dispose();
        }
    }

    /// <summary>
    /// 释放当前后端所有通道中处于按下状态的按键
    /// </summary>
    public static void ReleaseAll()
    {
        if (Operation.Value is { } operation) operation.ReleaseAll();
        else Backend.ReleaseAll();
    }

    /// <summary>仅在当前异步调用链内提供可取消输入；退出后旧后台调用也不能再发输入。</summary>
    internal static InputOperationScope BeginOperation(CancellationToken token,
        IInputChannel? foreground = null, IInputChannel? background = null)
    {
        var parent = Operation.Value;
        var scope = new InputOperationScope(token,
            InputOperationScope.Unwrap(foreground ?? parent?.OriginalForeground ?? Backend.Foreground),
            InputOperationScope.Unwrap(background ?? (foreground != null ? foreground : parent?.OriginalBackground ?? Backend.Background)),
            parent, previous => Operation.Value = previous);
        Operation.Value = scope;
        return scope;
    }

    internal static CancellationToken? CurrentOperationToken =>
        Operation.Value is { IsOpen: true } operation ? operation.Token : null;

    internal static void ThrowIfOperationCancelled() => Operation.Value?.ThrowIfCancelled();

    internal static void WaitInOperation(int millisecondsTimeout, CancellationToken token = default)
    {
        if (Operation.Value is { } operation) operation.Wait(millisecondsTimeout, token);
        else if (token.CanBeCanceled)
        {
            token.ThrowIfCancellationRequested();
            if (token.WaitHandle.WaitOne(millisecondsTimeout)) token.ThrowIfCancellationRequested();
        }
        else Thread.Sleep(millisecondsTimeout);
    }

    internal static async System.Threading.Tasks.Task DelayInOperation(int millisecondsTimeout, CancellationToken token = default)
    {
        using var linked = CombineOperationToken(token, out var combined);
        try
        {
            ThrowIfOperationCancelled();
            await System.Threading.Tasks.Task.Delay(millisecondsTimeout, combined);
            ThrowIfOperationCancelled();
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            ThrowIfOperationCancelled();
            token.ThrowIfCancellationRequested();
            throw;
        }
    }

    internal static CancellationTokenSource? CombineOperationToken(CancellationToken token, out CancellationToken combined)
    {
        if (Operation.Value is not { IsOpen: true } operation || token == operation.EffectiveToken)
        {
            combined = token;
            return null;
        }

        if (!token.CanBeCanceled)
        {
            combined = operation.EffectiveToken;
            return null;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(token, operation.EffectiveToken);
        combined = linked.Token;
        return linked;
    }
}


/// <summary>输入所有权和取消只属于一次有范围的操作，不改变普通输入通道的行为。</summary>
internal sealed class InputOperationScope : IDisposable
{
    private readonly InputOperationScope? _parent;
    private readonly Action<InputOperationScope?> _restore;
    private readonly CancellationTokenSource? _combined;
    private readonly CancellationTokenRegistration _registration;
    private readonly OperationChannel _foreground;
    private readonly OperationChannel _background;
    private readonly object _failureGate = new();
    private readonly List<Exception> _releaseFailures = [];
    private int _closed;

    internal InputOperationScope(CancellationToken token, IInputChannel foreground, IInputChannel background,
        InputOperationScope? parent, Action<InputOperationScope?> restore)
    {
        Token = token;
        OriginalForeground = foreground;
        OriginalBackground = background;
        _parent = parent;
        _restore = restore;
        if (parent != null && parent.EffectiveToken != token)
            _combined = CancellationTokenSource.CreateLinkedTokenSource(token, parent.EffectiveToken);
        EffectiveToken = _combined?.Token ?? token;
        _foreground = new OperationChannel(this, foreground);
        _background = ReferenceEquals(foreground, background) ? _foreground : new OperationChannel(this, background);
        _registration = EffectiveToken.Register(static state => ((InputOperationScope)state!).ReleaseOwned(), this);
    }

    internal static IInputChannel Unwrap(IInputChannel channel)
        => channel is OperationChannel operation ? Unwrap(operation.Inner) : channel;

    internal bool IsOpen => Volatile.Read(ref _closed) == 0;
    private bool IsInputTerminated => !IsOpen || Token.IsCancellationRequested || (_parent?.IsInputTerminated ?? false);
    internal CancellationToken Token { get; }
    internal CancellationToken EffectiveToken { get; }
    internal IInputChannel OriginalForeground { get; }
    internal IInputChannel OriginalBackground { get; }
    internal IInputChannel Foreground => _foreground;
    internal IInputChannel Background => _background;

    internal void ThrowIfCancelled()
    {
        _parent?.ThrowIfCancelled();
        Token.ThrowIfCancellationRequested();
    }

    private void ThrowIfClosedOrCancelled()
    {
        _parent?.ThrowIfClosedOrCancelled();
        ThrowIfCancelled();
        if (!IsOpen) throw new OperationCanceledException("输入作用域已经结束", Token);
    }

    internal void Wait(int millisecondsTimeout, CancellationToken token = default)
    {
        ThrowIfCancelled();
        token.ThrowIfCancellationRequested();
        // 正常完成后只读后台任务仍能沿用原等待；它持有的输入包装器始终保持封闭。
        if (!IsOpen)
        {
            if (token.CanBeCanceled)
            {
                if (token.WaitHandle.WaitOne(millisecondsTimeout)) token.ThrowIfCancellationRequested();
            }
            else Thread.Sleep(millisecondsTimeout);
            return;
        }

        using var linked = token.CanBeCanceled && token != EffectiveToken
            ? CancellationTokenSource.CreateLinkedTokenSource(token, EffectiveToken) : null;
        var combined = linked?.Token ?? EffectiveToken;
        if (combined.WaitHandle.WaitOne(millisecondsTimeout))
        {
            ThrowIfCancelled();
            token.ThrowIfCancellationRequested();
            combined.ThrowIfCancellationRequested();
        }
        ThrowIfCancelled();
        token.ThrowIfCancellationRequested();
    }

    private void RecordReleaseFailure(Exception error)
    {
        lock (_failureGate) _releaseFailures.Add(error);
    }

    internal void ReleaseAll()
    {
        ThrowIfClosedOrCancelled();
        ReleaseOwned();
    }

    // CancellationToken 回调只收集释放错误；Dispose 在正常执行栈中报告，避免回调抛错覆盖取消。
    internal void ReleaseOwned()
    {
        _foreground.ReleaseOwned();
        if (!ReferenceEquals(_foreground, _background)) _background.ReleaseOwned();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try
        {
            _registration.Dispose();
            ReleaseOwned();
        }
        finally
        {
            _restore(_parent);
            _combined?.Dispose();
        }

        lock (_failureGate)
        {
            if (_releaseFailures.Count != 0)
                throw new AggregateException("操作收尾时释放按键失败", _releaseFailures);
        }
    }

    private sealed class OperationChannel(InputOperationScope scope, IInputChannel inner)
        : IInputChannel, IKeyboardInput, IMouseInput
    {
        private readonly object _gate = new();
        private readonly HashSet<User32.VK> _owned = [];
        private readonly HashSet<User32.VK> _atomicPresses = [];
        private readonly HashSet<User32.VK> _downsInFlight = [];

        internal IInputChannel Inner => inner;
        public IKeyboardInput Keyboard => this;
        public IMouseInput Mouse => this;

        private void Send(Action operation)
        {
            lock (_gate)
            {
                scope.ThrowIfClosedOrCancelled();
                operation();
                scope.ThrowIfClosedOrCancelled();
            }
        }

        private void Down(User32.VK key, Action operation)
        {
            lock (_gate)
            {
                scope.ThrowIfClosedOrCancelled();
                if (!inner.IsKeyDown(key)) _owned.Add(key);
                // 取消可由底层发送中的同步回调触发。必须等 Down 实际完成后再松键，
                // 否则重入的取消回调先发 Up，随后生效的 Down 会留下按住状态。
                _downsInFlight.Add(key);
                try
                {
                    operation();
                }
                finally
                {
                    _downsInFlight.Remove(key);
                    if (scope.IsInputTerminated) ReleaseOwned(key);
                }
                scope.ThrowIfClosedOrCancelled();
            }
        }

        private void Up(User32.VK key, Action operation)
        {
            lock (_gate)
            {
                scope.ThrowIfClosedOrCancelled();
                try
                {
                    operation();
                    _owned.Remove(key);
                }
                catch (Exception error)
                {
                    scope.RecordReleaseFailure(error);
                    throw;
                }
            }
        }

        private void Press(User32.VK key, Action operation)
        {
            lock (_gate)
            {
                scope.ThrowIfClosedOrCancelled();
                if (!inner.IsKeyDown(key)) _owned.Add(key);
                _atomicPresses.Add(key);
                try
                {
                    operation();
                    _owned.Remove(key); // 原生单击包含 Down/Up，保持原有按键语义。
                }
                finally
                {
                    _atomicPresses.Remove(key);
                }
                scope.ThrowIfClosedOrCancelled();
            }
        }

        internal void ReleaseOwned(User32.VK? onlyKey = null)
        {
            lock (_gate)
            {
                foreach (var key in _owned.Where(key => !_atomicPresses.Contains(key) && !_downsInFlight.Contains(key)
                    && (!onlyKey.HasValue || key == onlyKey.Value)).ToArray())
                {
                    try
                    {
                        inner.Keyboard.KeyUp(key);
                        _owned.Remove(key);
                    }
                    catch (Exception error)
                    {
                        scope.RecordReleaseFailure(error);
                    }
                }
            }
        }

        public bool IsKeyDown(User32.VK key) => inner.IsKeyDown(key);

        public IKeyboardInput KeyDown(User32.VK key) { Down(key, () => inner.Keyboard.KeyDown(key)); return this; }
        public IKeyboardInput KeyUp(User32.VK key) { Up(key, () => inner.Keyboard.KeyUp(key)); return this; }
        public IKeyboardInput KeyPress(User32.VK key) { Press(key, () => inner.Keyboard.KeyPress(key)); return this; }

        public IInputChannel SimulateAction(GIActions action, KeyType type = KeyType.KeyPress)
        {
            var key = action.ToActionKey();
            if (key is KeyId.None or KeyId.Unknown)
            {
                Send(() => inner.SimulateAction(action, type));
                return this;
            }
            var vk = key.ToVK();
            if (type == KeyType.KeyDown) Down(vk, () => inner.SimulateAction(action, type));
            else if (type == KeyType.KeyUp) Up(vk, () => inner.SimulateAction(action, type));
            else if (type == KeyType.Hold)
            {
                Down(vk, () => inner.SimulateAction(action, KeyType.KeyDown));
                try
                {
                    scope.Wait(1000);
                    Up(vk, () => inner.SimulateAction(action, KeyType.KeyUp));
                }
                catch
                {
                    ReleaseOwned(vk);
                    throw;
                }
            }
            else Press(vk, () => inner.SimulateAction(action, type));
            return this;
        }

        public IInputChannel Sleep(int ms) { scope.Wait(ms); return this; }
        IKeyboardInput IKeyboardInput.Sleep(int ms) { scope.Wait(ms); return this; }
        IMouseInput IMouseInput.Sleep(int ms) { scope.Wait(ms); return this; }
        public void ReleaseAll()
        {
            scope.ThrowIfClosedOrCancelled();
            ReleaseOwned();
        }

        public IMouseInput MoveMouseBy(int dx, int dy) { Send(() => inner.Mouse.MoveMouseBy(dx, dy)); return this; }
        public IMouseInput MoveMouseTo(double x, double y) { Send(() => inner.Mouse.MoveMouseTo(x, y)); return this; }
        public IMouseInput VerticalScroll(int amount) { Send(() => inner.Mouse.VerticalScroll(amount)); return this; }
        public IMouseInput LeftButtonDown() { Down(User32.VK.VK_LBUTTON, () => inner.Mouse.LeftButtonDown()); return this; }
        public IMouseInput LeftButtonUp() { Up(User32.VK.VK_LBUTTON, () => inner.Mouse.LeftButtonUp()); return this; }
        public IMouseInput LeftButtonClick() { Press(User32.VK.VK_LBUTTON, () => inner.Mouse.LeftButtonClick()); return this; }
        public IMouseInput RightButtonDown() { Down(User32.VK.VK_RBUTTON, () => inner.Mouse.RightButtonDown()); return this; }
        public IMouseInput RightButtonUp() { Up(User32.VK.VK_RBUTTON, () => inner.Mouse.RightButtonUp()); return this; }
        public IMouseInput RightButtonClick() { Press(User32.VK.VK_RBUTTON, () => inner.Mouse.RightButtonClick()); return this; }
        public IMouseInput MiddleButtonDown() { Down(User32.VK.VK_MBUTTON, () => inner.Mouse.MiddleButtonDown()); return this; }
        public IMouseInput MiddleButtonUp() { Up(User32.VK.VK_MBUTTON, () => inner.Mouse.MiddleButtonUp()); return this; }
        public IMouseInput MiddleButtonClick() { Press(User32.VK.VK_MBUTTON, () => inner.Mouse.MiddleButtonClick()); return this; }

        private static User32.VK? XButton(int buttonId) => buttonId switch
        {
            1 => User32.VK.VK_XBUTTON1, 2 => User32.VK.VK_XBUTTON2, _ => null
        };

        public IMouseInput XButtonDown(int buttonId)
        {
            if (XButton(buttonId) is { } vk) Down(vk, () => inner.Mouse.XButtonDown(buttonId));
            else Send(() => inner.Mouse.XButtonDown(buttonId));
            return this;
        }
        public IMouseInput XButtonUp(int buttonId)
        {
            if (XButton(buttonId) is { } vk) Up(vk, () => inner.Mouse.XButtonUp(buttonId));
            else Send(() => inner.Mouse.XButtonUp(buttonId));
            return this;
        }
        public IMouseInput XButtonClick(int buttonId)
        {
            if (XButton(buttonId) is { } vk) Press(vk, () => inner.Mouse.XButtonClick(buttonId));
            else Send(() => inner.Mouse.XButtonClick(buttonId));
            return this;
        }
    }
}
