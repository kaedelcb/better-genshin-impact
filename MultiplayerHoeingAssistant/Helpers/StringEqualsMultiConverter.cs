using System.Globalization;
using System.Windows.Data;

namespace MultiplayerHoeingAssistant.Helpers;

/// <summary>比较两个非空字符串是否完全相等（用于多房间列表高亮当前登录房间与当前查看房间）。</summary>
public sealed class StringEqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not { Length: >= 2 }) return false;
        if (values[0] is not string left || values[1] is not string right) return false;
        return !string.IsNullOrEmpty(left) && string.Equals(left, right, StringComparison.Ordinal);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
