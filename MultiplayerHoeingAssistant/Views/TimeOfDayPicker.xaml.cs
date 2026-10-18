using System.Windows;
using System.Windows.Controls;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 弹窗式时间选择控件，供日志范围搜索和任务中心计划设置使用。
/// 默认显示时分秒并点选即回写；计划设置可只显示时分，并在确认后回写 <see cref="Value"/>。
/// 「现在」= 当前时间，「完成」= 关闭弹窗并触发 <see cref="Submitted"/>。全程无需键盘输入。
/// </summary>
public partial class TimeOfDayPicker : UserControl
{
    /// <summary>当前值为时分秒、时分或空字符串；外部写入时同步各列选中项。</summary>
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(TimeOfDayPicker),
            new FrameworkPropertyMetadata("00:00:00", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnValueChanged));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>允许清空；默认关闭，日志搜索维持原有必填时间。</summary>
    public static readonly DependencyProperty AllowEmptyProperty = DependencyProperty.Register(
        nameof(AllowEmpty), typeof(bool), typeof(TimeOfDayPicker), new PropertyMetadata(false));
    public bool AllowEmpty { get => (bool)GetValue(AllowEmptyProperty); set => SetValue(AllowEmptyProperty, value); }

    /// <summary>是否选择秒；计划设置只选择时和分，原日志搜索默认保留秒。</summary>
    public static readonly DependencyProperty ShowSecondsProperty = DependencyProperty.Register(
        nameof(ShowSeconds), typeof(bool), typeof(TimeOfDayPicker), new PropertyMetadata(true));
    public bool ShowSeconds { get => (bool)GetValue(ShowSecondsProperty); set => SetValue(ShowSecondsProperty, value); }

    /// <summary>确认后才回写，避免选择时、分的中间值触发计划自动保存。</summary>
    public static readonly DependencyProperty CommitOnSubmitProperty = DependencyProperty.Register(
        nameof(CommitOnSubmit), typeof(bool), typeof(TimeOfDayPicker), new PropertyMetadata(false));
    public bool CommitOnSubmit { get => (bool)GetValue(CommitOnSubmitProperty); set => SetValue(CommitOnSubmitProperty, value); }

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(TimeOfDayPicker), new PropertyMetadata("未设置"));
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    /// <summary>「现在」「完成」或允许的「清空」点击后触发（宿主可接搜索等动作）。</summary>
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

    /// <summary>默认点选即回写；计划设置启用确认后回写，取消弹窗时保留原值。</summary>
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || CommitOnSubmit) return;
        _updating = true;
        try { CommitSelection(); }
        finally { _updating = false; }
    }

    private void CommitSelection()
    {
        if (HourList.SelectedIndex < 0 || MinuteList.SelectedIndex < 0
            || (ShowSeconds && SecondList.SelectedIndex < 0)) return;
        var value = ShowSeconds
            ? $"{HourList.SelectedIndex:D2}:{MinuteList.SelectedIndex:D2}:{SecondList.SelectedIndex:D2}"
            : $"{HourList.SelectedIndex:D2}:{MinuteList.SelectedIndex:D2}";
        // 打开后直接确认不能改写已有的秒值或历史格式；真正改选时才生成新的时分值。
        if (!ShowSeconds && TimeOnly.TryParse(Value, out var existing)
            && existing.Hour == HourList.SelectedIndex && existing.Minute == MinuteList.SelectedIndex)
            value = Value;
        SetCurrentValue(ValueProperty, value);
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
        SetCurrentValue(ValueProperty, DateTime.Now.ToString(ShowSeconds ? "HH:mm:ss" : "HH:mm"));
        CloseAndSubmit();
    }

    /// <summary>「完成」：关弹窗并通知宿主。</summary>
    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (CommitOnSubmit) CommitSelection();
        CloseAndSubmit();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AllowEmpty) return;
        SetCurrentValue(ValueProperty, string.Empty);
        CloseAndSubmit();
    }

    private void CloseAndSubmit()
    {
        DropButton.IsChecked = false;
        Submitted?.Invoke(this, EventArgs.Empty);
    }
}
