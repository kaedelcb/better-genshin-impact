using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Helpers;

namespace MultiplayerHoeingAssistant.Views;

public partial class UpdateBgiWindow
{
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;
    private const double NormalMinWidth = 520;
    private const double NormalMinHeight = 560;
    private Rect _updateWorkArea = Rect.Empty;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var workArea = DpiAwarenessController.GetLogicalWorkAreaRect(this, Owner) ?? SystemParameters.WorkArea;
        ApplyNormalWorkArea(workArea);
        DpiAwarenessController.PlaceWithinWorkArea(this, workArea, MaxHeight, margin: 0);
        // 首次显示时，SizeToContent 与卡片高度绑定会继续安排布局；稳定后只居中一次。
        Dispatcher.InvokeAsync(() =>
        {
            if (WindowState != WindowState.Normal || new WindowInteropHelper(this).Handle == 0)
                return;

            UpdateLayout();
            DpiAwarenessController.PlaceWithinWorkArea(this, _updateWorkArea, ActualHeight, margin: 0);
        }, DispatcherPriority.ApplicationIdle);
        LocationChanged += (_, _) => RefreshWorkArea();
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(() => RefreshWorkArea(force: true), DispatcherPriority.Loaded);
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
                MaxHeight = double.PositiveInfinity;
            else
                RefreshWorkArea(force: true);
        };
        SizeChanged += (_, args) =>
        {
            if (args.HeightChanged)
                KeepVerticalBounds();
        };

        // 只为系统标题栏着色，保留系统原生的标题栏和窗口按钮。
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || SystemParameters.HighContrast)
            return;

        var handle = new WindowInteropHelper(this).Handle;
        uint caption = 0x002F1617; // COLORREF: #17162F
        uint text = 0x00FAF2F4;    // COLORREF: #F4F2FA
        if (DwmSetWindowAttribute(handle, DwmCaptionColor, in caption, sizeof(uint)) == 0)
            _ = DwmSetWindowAttribute(handle, DwmTextColor, in text, sizeof(uint));
    }

    private void RefreshWorkArea(bool force = false)
    {
        if (!IsLoaded || WindowState != WindowState.Normal)
            return;

        var work = DpiAwarenessController.GetLogicalWorkAreaRect(this) ?? SystemParameters.WorkArea;
        if (force || work != _updateWorkArea)
            ApplyNormalWorkArea(work);
    }

    private void ApplyNormalWorkArea(Rect work)
    {
        _updateWorkArea = work;
        var maxWidth = work.Width * 0.95;
        var maxHeight = work.Height * 0.95;
        MinWidth = Math.Min(NormalMinWidth, maxWidth);
        MinHeight = Math.Min(NormalMinHeight, maxHeight);
        Width = Math.Min(Width, maxWidth);
        // 高度随内容变化；超过工作区上限时，只滚动正文，底部按钮留在可视区。
        MaxHeight = maxHeight;
    }

    private void KeepVerticalBounds()
    {
        if (!IsLoaded || WindowState != WindowState.Normal || _updateWorkArea.IsEmpty || double.IsNaN(Top))
            return;

        var top = Math.Clamp(Top, _updateWorkArea.Top, Math.Max(_updateWorkArea.Top, _updateWorkArea.Bottom - ActualHeight));
        if (Top != top)
            Top = top;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, in uint value, int size);
}

/// <summary>按窗口可用宽度切换更新窗口布局。</summary>
public sealed class UpdateBgiWideLayoutConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var minimum = parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
            ? threshold
            : 760;
        return value is double width && width >= minimum;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
