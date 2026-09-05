using System.Windows;
using System.Windows.Controls;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 弹窗式时分秒时间选择控件（嘟嘟可日志浏览·时间范围搜索 / 诊断时间点用）。
/// 点击显示框弹出 时/分/秒 三列滚选列表，点选即回写 <see cref="Value"/>（"HH:mm:ss"，双向绑定）；
/// 「现在」= 当前时间，「完成」= 关闭弹窗并触发 <see cref="Submitted"/>。全程无需键盘输入。
/// </summary>
public partial class TimeOfDayPicker : UserControl
{
    /// <summary>当前值（"HH:mm:ss"）。外部写入时同步三列选中项。</summary>
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(TimeOfDayPicker),
            new FrameworkPropertyMetadata("00:00:00", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnValueChanged));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>「现在」或「完成」点击后触发（宿主一般接"搜索"/"导出"命令）。</summary>
    public event EventHandler? Submitted;

    /// <summary>内部回写/外部写入期间的递归更新守卫。</summary>
    private bool _updating;

    public TimeOfDayPicker()
    {
        InitializeComponent();
        HourList.ItemsSource = MakeRange(24);
        MinuteList.ItemsSource = MakeRange(60);
        SecondList.ItemsSource = MakeRange(60);
        Loaded += (_, _) => SyncSelectionsFromValue();
    }

    private static string[] MakeRange(int count)
    {
        var items = new string[count];
        for (var i = 0; i < count; i++) items[i] = i.ToString("D2");
        return items;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (TimeOfDayPicker)d;
        if (picker._updating) return;
        picker.SyncSelectionsFromValue();
    }

    /// <summary>外部 Value → 三列选中项（非法输入按 0 处理并夹取范围）。</summary>
    private void SyncSelectionsFromValue()
    {
        if (!IsLoaded) return;
        var parts = (Value ?? "").Split(':');
        _updating = true;
        try
        {
            SelectAt(HourList, parts.Length > 0 ? parts[0] : "0", 23);
            SelectAt(MinuteList, parts.Length > 1 ? parts[1] : "0", 59);
            SelectAt(SecondList, parts.Length > 2 ? parts[2] : "0", 59);
        }
        finally { _updating = false; }
    }

    private static void SelectAt(ListBox list, string text, int max)
    {
        if (!int.TryParse(text, out var v)) v = 0;
        list.SelectedIndex = Math.Clamp(v, 0, max);
    }

    /// <summary>任一列点选 → 立即回写 Value（三列都有选中项才回写）。</summary>
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (HourList.SelectedIndex < 0 || MinuteList.SelectedIndex < 0 || SecondList.SelectedIndex < 0) return;
        _updating = true;
        try { Value = $"{HourList.SelectedIndex:D2}:{MinuteList.SelectedIndex:D2}:{SecondList.SelectedIndex:D2}"; }
        finally { _updating = false; }
    }

    /// <summary>弹窗打开时把当前值滚到可见位置。</summary>
    private void PickerPopup_Opened(object? sender, EventArgs e)
    {
        SyncSelectionsFromValue();
        foreach (var list in new[] { HourList, MinuteList, SecondList })
            if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
    }

    /// <summary>「现在」：填入当前时间、关弹窗、通知宿主。</summary>
    private void NowButton_Click(object sender, RoutedEventArgs e)
    {
        Value = DateTime.Now.ToString("HH:mm:ss");
        CloseAndSubmit();
    }

    /// <summary>「完成」：关弹窗并通知宿主。</summary>
    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => CloseAndSubmit();

    private void CloseAndSubmit()
    {
        DropButton.IsChecked = false;
        Submitted?.Invoke(this, EventArgs.Empty);
    }
}
