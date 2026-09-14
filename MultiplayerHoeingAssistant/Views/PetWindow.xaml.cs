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
///   呼吸运动 = 单一相位驱动（吸气=微涨+上浮、呼气=回落），幅度 barely noticeable；
///   帧切换 = 锚定呼吸极值点（每半周期一换），以 FadeMs 交叉淡化叠入（smoothstep 缓动）；
///   状态切换 = 整层 StateFadeMs 交叉淡化；档位（周期/幅度）随状态渐变，无跳变。
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
    private static (double Amp, double Period) BreathProfile(string? key) => key switch
    {
        "act_hoeing" or "act_artifact" or "act_gather" => (1.0, LifeTaskPeriodMs),
        "sleep" => (LifeSleepAmpScale, LifeSleepPeriodMs),
        _ => (1.0, LifeIdlePeriodMs)
    };

    /// <summary>是否任务执行类状态（光晕只在任务态渐显）。
    /// 注意 interact 是空闲轮换备片，绝不能入列——否则空闲时光晕误亮，观感"状态反了"。</summary>
    private static bool IsTaskStateKey(string? key) =>
        key is "act_hoeing" or "act_artifact" or "act_gather";

    /// <summary>呼吸相位（弧度，持续累加；周期渐变时相位连续不跳变）。</summary>
    private double _breathPhase;
    /// <summary>下一个换帧锚点相位（呼吸极值处，每 π 一个）。</summary>
    private double _nextSwapPhase = Math.PI;
    /// <summary>当前平滑档位：状态切换按 tick 渐变（每 tick 4%，约 0.8s 收敛），幅度节奏无跳变。</summary>
    private double _lifeAmp = 1.0;
    private double _lifePeriod = LifeIdlePeriodMs;

    private void UpdateBreath(double dtMs)
    {
        var (targetAmp, targetPeriod) = BreathProfile(_seq?.Key);
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
        var glowTarget = IsTaskStateKey(_seq?.Key) ? 1.0 : 0.0;
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
