using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 奥黛塔桌宠窗口（v2 帧动画版）。透明无边框、不进任务栏不抢焦点。
/// 播放器：双层 Grid（各持两帧 Image）+ 30ms 手动驱动引擎——
///   帧切换 = 帧保持 HoldMs 后，下一帧以 FadeMs 交叉淡化叠入（消除硬切生硬感）；
///   状态切换 = 整层 StateFadeMs 交叉淡化；单帧套图回退为轻微呼吸缩放。
/// 引擎全部在 UI 线程（并发契约），无 Completed 回调竞态。
/// </summary>
public partial class PetWindow : Window
{
    private readonly PetViewModel _vm;
    private readonly DispatcherTimer _engine;

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
        SourceInitialized += (_, _) => RestorePosition();
        vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= OnVmPropertyChanged;
            _engine.Stop();
        };

        _engine = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
        _engine.Tick += OnEngineTick;

        // 初始直接落位（无淡化）
        ApplySequenceImmediate(vm.DisplayKey);
        ApplySize();
        SourceInitialized += (_, _) => ApplyClickThrough(vm.ClickThrough);
        _engine.Start();
    }

    // ========== 免打扰穿透模式（WS_EX_TRANSPARENT：连双击都穿过去） ==========

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

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
    }

    private void ApplySize()
    {
        var b = new Binding("SizePx") { Source = _vm };
        foreach (var img in new[] { FrontImgA, FrontImgB, BackImgA, BackImgB })
        {
            img.SetBinding(WidthProperty, b);
            img.SetBinding(HeightProperty, b);
        }
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
        try { DragMove(); }
        catch (InvalidOperationException) { /* 状态竞争时 WPF 抛出，忽略本次拖拽 */ }
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
        ResetBreathing();
        if (set.Frames.Count < 2) StartBreathing(FrontScale);
        _phase = Phase.Hold;
        _phaseStart = _lastTick = DateTime.UtcNow;
    }

    private void OnEngineTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastTick).TotalMilliseconds;
        _lastTick = now;
        if (_seq == null) return;

        switch (_phase)
        {
            case Phase.Hold:
                if (_seq.Frames.Count >= 2 && dt >= _seq.HoldMs)
                {
                    // 下一帧装入后层 Image，开始交叉淡化
                    SetFrame(BackImgA, NextFrame());
                    BackImgA.Opacity = 0;
                    _phase = Phase.Fade;
                    _phaseStart = now;
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
                    _phaseStart = now;
                }
                else
                {
                    BackImgA.Opacity = t; // 下一帧 0→1 叠在前帧上（交叉淡化）
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
                    ResetBreathing();
                    if (_seq.Frames.Count < 2) StartBreathing(FrontScale);
                    _phase = _seq.Frames.Count >= 2 ? Phase.Hold : Phase.Done;
                    _phaseStart = now;
                }
                else
                {
                    LayerBack.Opacity = t;
                    LayerFront.Opacity = 1 - t;
                }
                break;
            }
        }
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

    // —— 单帧套图的呼吸微动（避免死图） ——

    private void StartBreathing(ScaleTransform scale)
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(1, 1.025, TimeSpan.FromMilliseconds(900))
        {
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void ResetBreathing()
    {
        FrontScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        FrontScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        FrontScale.ScaleX = 1;
        FrontScale.ScaleY = 1;
        BackScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BackScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }
}
