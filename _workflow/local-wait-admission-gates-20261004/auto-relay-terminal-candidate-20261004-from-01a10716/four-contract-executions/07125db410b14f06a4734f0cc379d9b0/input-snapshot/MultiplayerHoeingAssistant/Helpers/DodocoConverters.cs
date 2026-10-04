using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MultiplayerHoeingAssistant.Helpers;

/// <summary>
/// 日志级别 → 颜色画刷（嘟嘟可实时日志/异常记录列表用）。
/// DBG=暗紫、INF=白、WRN=金、ERR=橙红（沿用原神夜空色板）。
/// </summary>
public sealed class LevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Dbg = new(Color.FromRgb(0x9C, 0x97, 0xC0));
    private static readonly SolidColorBrush Inf = new(Color.FromRgb(0xF4, 0xF2, 0xFA));
    private static readonly SolidColorBrush Wrn = new(Color.FromRgb(0xD9, 0xA8, 0x4E));
    private static readonly SolidColorBrush Err = new(Color.FromRgb(0xE8, 0x8A, 0x6F));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() switch
        {
            "DBG" => Dbg,
            "WRN" => Wrn,
            "ERR" => Err,
            _ => Inf
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 百分比 → 像素（P5 收益横条 / 甘特时间线用）。
/// values[0] = 容器 ActualWidth（double），values[1] = 百分比 0-100（double）→ 像素宽度/偏移。
/// 容器宽度未就绪（NaN/0）时返回 0。
/// </summary>
public sealed class PctToPixelsMultiConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 ||
            values[0] is not double width || double.IsNaN(width) || width <= 0 ||
            values[1] is not double pct)
            return 0.0;
        return width * Math.Clamp(pct, 0, 100) / 100.0;
    }

    public object?[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
