using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 奥黛塔桌宠窗口（v2 帧动画版）。透明无边框、不进任务栏不抢焦点。
/// 播放器：双层 Grid（各持两帧 Image）+ 30ms 手动驱动引擎——
///   呼吸运动 = 单一相位驱动（吸气=微涨+上浮、呼气=回落），幅度 barely noticeable；
///   帧切换 = 锚定呼吸极值点（每半周期一换），以 FadeMs 交叉淡化叠入（smoothstep 缓动）；
///   状态切换 = 整层 StateFadeMs 交叉淡化；档位（周期/幅度）随状态渐变，无跳变。
/// 引擎全部在 UI 线程（并发契约），无 Completed 回调竞态。
/// </summary>
public partial class PetWindow : Window
{
    private readonly PetViewModel _vm;
    private readonly DispatcherTimer _engine;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
    private bool _allowPositionChange;
    private bool _positionLocked;
    private int _lockedX;
    private int _lockedY;
    private bool _restoreQueued;
    private bool _positionRestoreQueued;

    // 播放器状态（仅 UI 线程）
    private PetAnimSet? _seq;
    private int _frameIdx;
    private enum Phase { Hold, Fade, LayerFade, Done }
    private Phase _phase = Phase.Done;
    private DateTime _phaseStart;
    private DateTime _lastTick;

    public PetWindow(PetViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        _engine = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
        _engine.Tick += OnEngineTick;

        SourceInitialized += OnSourceInitialized;
        vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= OnVmPropertyChanged;
            _hwndSource?.RemoveHook(WindowProc);
            _hwndSource = null;
            _engine.Stop();
        };

        _stateTask = vm.IsTaskExecution;
        _stateSleep = vm.IsSleepingState;

        // 初始直接落位（无淡化）
        ApplySequenceImmediate(vm.DisplayKey);
        ApplySize();
        _engine.Start();
    }

    // ========== 原生窗口行为（穿透 + 防 Win+D 改位/最小化） ==========

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const int WM_SIZE = 0x0005;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;
    private const int SIZE_MINIMIZED = 1;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr HwndInsertAfter;
        public int X;
        public int Y;
        public int Cx;
        public int Cy;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WindowProc);

        // WPF 的 Left/Top 是 DIP，而锁定值取自 GetWindowRect 的物理像素。
        // 先允许初始恢复，待布局完成后再记录物理坐标，避免非 100% 缩放下混用坐标系。
        _allowPositionChange = true;
        try
        {
            RestorePosition();
        }
        finally
        {
            _allowPositionChange = false;
        }

        ApplyClickThrough(_vm.ClickThrough);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, CaptureLockedPosition);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGING when _positionLocked && !_allowPositionChange:
            {
                // Win+D / Show Desktop 在部分 DPI 组合下会让透明工具窗口收到异常位置。
                // 始终把非用户拖拽产生的移动改回最后一次真实物理像素位置；尺寸和 Z 序仍放行。
                var pos = Marshal.PtrToStructure<WindowPos>(lParam);
                pos.X = _lockedX;
                pos.Y = _lockedY;
                Marshal.StructureToPtr(pos, lParam, false);
                break;
            }

            case WM_WINDOWPOSCHANGED when _positionLocked && !_allowPositionChange:
            {
                // SWP_NOSENDCHANGING 可绕过上面的预移动消息；事后检查保证这条路径也会归位。
                var pos = Marshal.PtrToStructure<WindowPos>(lParam);
                if ((pos.Flags & SWP_NOMOVE) == 0 && (pos.X != _lockedX || pos.Y != _lockedY))
                    QueuePositionRestore();
                break;
            }

            case WM_SYSCOMMAND when ((long)wParam & 0xFFF0) == SC_MINIMIZE:
                // 桌宠没有“最小化”语义；阻止 Win+D 把它送入最小化坐标。
                handled = true;
                return IntPtr.Zero;

            case WM_SIZE when (long)wParam == SIZE_MINIMIZED:
                QueueRestoreFromSystemMinimize();
                break;
        }

        return IntPtr.Zero;
    }

    private void CaptureLockedPosition()
    {
        if (_hwnd != IntPtr.Zero && GetWindowRect(_hwnd, out var rect))
        {
            _lockedX = rect.Left;
            _lockedY = rect.Top;
            _positionLocked = true;
        }
    }

    private void QueuePositionRestore()
    {
        if (_positionRestoreQueued || !_vm.Enabled) return;
        _positionRestoreQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            _positionRestoreQueued = false;
            if (_hwnd == IntPtr.Zero || !_positionLocked || !_vm.Enabled) return;
            SetWindowPos(_hwnd, IntPtr.Zero, _lockedX, _lockedY, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        });
    }

    private void QueueRestoreFromSystemMinimize()
    {
        if (_restoreQueued || !_vm.Enabled) return;
        _restoreQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            _restoreQueued = false;
            if (_hwnd == IntPtr.Zero || !_vm.Enabled) return;
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            if (_positionLocked)
            {
                SetWindowPos(_hwnd, IntPtr.Zero, _lockedX, _lockedY, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
        });
    }

    /// <summary>应用/解除整窗点击穿透（VM 侧统一入口 ApplyClickThrough）。</summary>
    internal void ApplyClickThrough(bool enabled)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex = enabled ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    private void RestorePosition()
    {
        var (x, y, _) = _vm.GetRestorePosition();
        Left = x;
        Top = y;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PetViewModel.DisplayKey))
            Dispatcher.BeginInvoke(() => BeginLayerFade(_vm.DisplayKey));
        else if (e.PropertyName == nameof(PetViewModel.SizePx))
            Dispatcher.BeginInvoke(ApplySize);
        else if (e.PropertyName == nameof(PetViewModel.IsTaskExecution))
            _stateTask = _vm.IsTaskExecution;
        else if (e.PropertyName == nameof(PetViewModel.IsSleepingState))
            _stateSleep = _vm.IsSleepingState;
    }

    private void ApplySize()
    {
        var b = new Binding("SizePx") { Source = _vm };
        foreach (var img in new[] { FrontImgA, FrontImgB, BackImgA, BackImgB })
        {
            img.SetBinding(WidthProperty, b);
            img.SetBinding(HeightProperty, b);
        }
        // 光晕直径≈本体1.42倍，透明内边距随之扩展——否则光晕超出窗口被裁成方形
        var px = _vm.SizePx;
        SpriteHost.Margin = new Thickness(px * 0.16 + 8);
        // 下方留白用负 margin 把状态条拉回来（状态条覆盖光晕淡出尾，间距恢复原样）
        ChipHost.Margin = new Thickness(0, -(px * 0.11), 0, 2);
    }

    // ========== 交互 ==========

    private void OnMouseEnter(object sender, MouseEventArgs e) => _vm.NotifyHovered();

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 双击唤出主窗口；单击进入拖拽（顺带"被抓起"惊表情）
        if (e.ClickCount == 2)
        {
            _vm.OpenMainWindowCommand.Execute(null);
            return;
        }
        _vm.NotifyDragStarted();
        _allowPositionChange = true;
        try { DragMove(); }
        catch (InvalidOperationException) { /* 状态竞争时 WPF 抛出，忽略本次拖拽 */ }
        finally
        {
            _allowPositionChange = false;
            CaptureLockedPosition();
        }
        _vm.NotifyDragged(Left, Top);
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        => _vm.AdjustSize(e.Delta > 0 ? 1 : -1);

    // ========== 播放引擎（30ms 手动驱动） ==========

    /// <summary>状态切换：整层交叉淡化到新套图。</summary>
    private void BeginLayerFade(string key)
    {
        var set = PetAnimCatalog.Instance.Get(key);
        if (set == null) return;
        if (key == _seq?.Key) return;
        if (_seq == null || PetAnimCatalog.Instance.StateFadeMs <= 0)
        {
            ApplySequenceImmediate(key);
            return;
        }

        // 后层装新套图第一帧，从 0 淡入；前层 1→0
        SetFrame(BackImgA, set.Frames[0]);
        BackImgB.Opacity = 0;
        BackImgA.Opacity = 1;
        _seq = set;
        _frameIdx = 0;
        _phase = Phase.LayerFade;
        _phaseStart = _lastTick = DateTime.UtcNow;
    }

    private void ApplySequenceImmediate(string key)
    {
        var set = PetAnimCatalog.Instance.Get(key);
        if (set == null) return;
        _seq = set;
        _frameIdx = 0;
        SetFrame(FrontImgA, set.Frames[0]);
        FrontImgB.Opacity = 0;
        FrontImgA.Opacity = 1;
        LayerFront.Opacity = 1;
        LayerBack.Opacity = 0;
        _phase = Phase.Hold;
        _phaseStart = _lastTick = DateTime.UtcNow;
    }

    private void OnEngineTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastTick).TotalMilliseconds;
        _lastTick = now;

        // —— 呼吸相位推进（先于帧相位；_seq 为 null 也持续呼吸）——
        UpdateBreath(dt);

        if (_seq == null) return;

        switch (_phase)
        {
            case Phase.Hold:
                // 帧切换锚定呼吸极值点（相位每 π 一个锚点=半个呼吸周期），
                // 表情变化"骑"在呼吸上，运动有因果——这是"活"与"抖"的分界
                if (_seq.Frames.Count >= 2 && _breathPhase >= _nextSwapPhase)
                {
                    SetFrame(BackImgA, NextFrame());
                    BackImgA.Opacity = 0;
                    _phase = Phase.Fade;
                    _phaseStart = now;
                    _nextSwapPhase += Math.PI;
                }
                break;

            case Phase.Fade:
            {
                var t = (now - _phaseStart).TotalMilliseconds / Math.Max(1, _seq.FadeMs);
                if (t >= 1)
                {
                    // 提交：前层主图换帧、辅图归零
                    SetFrame(FrontImgA, _seq.Frames[_frameIdx]);
                    FrontImgA.Opacity = 1;
                    BackImgA.Opacity = 0;
                    _phase = Phase.Hold;
                    // 淡化吃掉锚点间距时（极慢呼吸），下一锚点推到未来避免连爆
                    if (_breathPhase >= _nextSwapPhase)
                        _nextSwapPhase = Math.Ceiling(_breathPhase / Math.PI) * Math.PI;
                    _phaseStart = now;
                }
                else
                {
                    // smoothstep 缓动（两端慢中间快），替代线性透明度的生硬感
                    BackImgA.Opacity = SmoothStep(t);
                }
                break;
            }

            case Phase.LayerFade:
            {
                var fadeMs = PetAnimCatalog.Instance.StateFadeMs;
                var t = (now - _phaseStart).TotalMilliseconds / Math.Max(1, fadeMs);
                if (t >= 1)
                {
                    // 提交：后层成为前层
                    SetFrame(FrontImgA, _seq.Frames[_frameIdx]);
                    FrontImgA.Opacity = 1;
                    FrontImgB.Opacity = 0;
                    LayerFront.Opacity = 1;
                    LayerBack.Opacity = 0;
                    _phase = _seq.Frames.Count >= 2 ? Phase.Hold : Phase.Done;
                    _phaseStart = now;
                }
                else
                {
                    var s = SmoothStep(t);
                    LayerBack.Opacity = s;
                    LayerFront.Opacity = 1 - s;
                }
                break;
            }
        }
    }

    // ========== 呼吸运动：单一相位驱动的"吸气涨+上浮 / 呼气回落" ==========
    // 设计依据（动画十二原则·次级动作 + idle 设计指南）：一个主循环，幅度 barely noticeable，
    // 周期 2–4s；状态只改这套循环的周期（干活喘得促、睡眠放得缓），不加额外振荡器。
    // 帧切换锚定在呼吸极值（相位每 π），换帧发生在"呼吸停顿"的瞬间，有因果不突兀。

    /// <summary>呼吸缩放满幅（吸气最大时相对 1.0 涨 1.4%）。</summary>
    private const double LifeBreathAmp = 0.014;
    /// <summary>吸气压满时的上浮位移（DIP）。</summary>
    private const double LifeRiseAmp = 2.0;
    /// <summary>基础呼吸周期（毫秒，idle）。周期即帧循环：每半周期在呼吸极值处换一帧。</summary>
    private const double LifeIdlePeriodMs = 3200;
    /// <summary>任务类状态呼吸周期：变促不变形——幅度不变，只是"喘"。</summary>
    private const double LifeTaskPeriodMs = 2400;
    /// <summary>睡眠状态：周期放长、幅度收轻。</summary>
    private const double LifeSleepPeriodMs = 4000;
    private const double LifeSleepAmpScale = 0.6;

    /// <summary>状态动效档位（幅度乘数，周期毫秒）。</summary>
    /// <summary>节奏档位按语义状态（而非显示的表情 key）：任务快、睡眠慢、其余空闲。
    /// 表情 key 会因爆发态/备片轮换临时变化，语义状态才是稳定事实。</summary>
    private (double Amp, double Period) BreathProfile() => _stateSleep
        ? (LifeSleepAmpScale, LifeSleepPeriodMs)
        : _stateTask ? (1.0, LifeTaskPeriodMs) : (1.0, LifeIdlePeriodMs);

    /// <summary>呼吸相位（弧度，持续累加；周期渐变时相位连续不跳变）。</summary>
    private double _breathPhase;
    /// <summary>下一个换帧锚点相位（呼吸极值处，每 π 一个）。</summary>
    private double _nextSwapPhase = Math.PI;
    /// <summary>当前平滑档位：状态切换按 tick 渐变（每 tick 4%，约 0.8s 收敛），幅度节奏无跳变。</summary>
    private double _lifeAmp = 1.0;
    private double _lifePeriod = LifeIdlePeriodMs;
    /// <summary>语义状态镜像（UI 线程写入，随 VM 属性更新）。</summary>
    private bool _stateTask;
    private bool _stateSleep;

    private void UpdateBreath(double dtMs)
    {
        var (targetAmp, targetPeriod) = BreathProfile();
        _lifeAmp += (targetAmp - _lifeAmp) * 0.04;
        _lifePeriod += (targetPeriod - _lifePeriod) * 0.04;

        _breathPhase += dtMs * 2 * Math.PI / _lifePeriod;

        // b∈[0,1]：0=呼气压底（极值锚点），1=吸气压顶（另一极值锚点）
        var b = (1 - Math.Cos(_breathPhase)) / 2;
        LifeScale.ScaleX = 1 + LifeBreathAmp * _lifeAmp * b;
        LifeScale.ScaleY = 1 + LifeBreathAmp * _lifeAmp * b;
        LifeTranslate.Y = -LifeRiseAmp * _lifeAmp * b;

        // 光晕：任务态渐显（每 tick 4% 约 0.8s），透明度与缩放随呼吸相位同步涨落——
        // 吸气时晕圈微微变亮变大、呼气时收敛，观感是"光随呼吸"，非常驻不闪烁
        var glowTarget = _stateTask ? 1.0 : 0.0;
        _glowLevel += (glowTarget - _glowLevel) * 0.04;
        if (_glowLevel < 0.001) _glowLevel = 0;
        TaskGlow.Opacity = _glowLevel * (0.65 + 0.35 * b);
        var glowScale = 1.32 + 0.05 * b;
        GlowScale.ScaleX = glowScale;
        GlowScale.ScaleY = glowScale;
    }

    /// <summary>光晕当前强度（0=不可见，1=任务态满强度；状态切换按 tick 渐变）。</summary>
    private double _glowLevel;

    /// <summary>smoothstep 缓动：t²(3−2t)，两端收敛中间平滑。</summary>
    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private Uri NextFrame()
    {
        _frameIdx = (_frameIdx + 1) % _seq!.Frames.Count;
        return _seq.Frames[_frameIdx];
    }

    private static void SetFrame(Image img, Uri source)
    {
        img.Source = new BitmapImage(source);
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Fant);
    }
}
