using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;

namespace MultiplayerHoeingAssistant.Views;

public partial class UpdateBgiWindow
{
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 只为系统标题栏着色，保留系统原生的标题栏和窗口按钮。
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || SystemParameters.HighContrast)
            return;

        var handle = new WindowInteropHelper(this).Handle;
        uint caption = 0x002F1617; // COLORREF: #17162F
        uint text = 0x00FAF2F4;    // COLORREF: #F4F2FA
        if (DwmSetWindowAttribute(handle, DwmCaptionColor, in caption, sizeof(uint)) == 0)
            _ = DwmSetWindowAttribute(handle, DwmTextColor, in text, sizeof(uint));
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
