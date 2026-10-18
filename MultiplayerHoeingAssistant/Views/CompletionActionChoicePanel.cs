using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>只选择本次是否复用成员已有动作，不保存另一份完成动作配置。</summary>
internal sealed class CompletionActionChoicePanel : StackPanel
{
    internal CheckBox CompletionOption { get; }
    internal bool UseCompletionActions => CompletionOption.IsEnabled && CompletionOption.IsChecked == true;
    private readonly TextBlock _unavailable;

    internal CompletionActionChoicePanel(bool supported, IReadOnlyList<CoordinatedCompletionAction?> actions)
    {
        CompletionOption = new CheckBox
        {
            Content = "执行各成员在“绑定”中设置的锄地后动作",
            IsChecked = false,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF2, 0xFA)),
            FontSize = 13, Margin = new Thickness(0, 8, 0, 6)
        };
        Children.Add(CompletionOption);
        var explanation = Text("不勾选：本次锄完停止。", Color.FromRgb(0xB8, 0xB1, 0xD4));
        CompletionOption.Checked += (_, _) => explanation.Text = "正常完成后执行各成员已有动作；失败或手动停止不触发。";
        CompletionOption.Unchecked += (_, _) => explanation.Text = "不勾选：本次锄完停止。";
        Children.Add(explanation);
        _unavailable = Text("", Color.FromRgb(0xD9, 0xA8, 0x4E));
        _unavailable.FontSize = 11;
        Children.Add(_unavailable);
        UpdateAvailability(supported, actions);
    }

    internal void UpdateAvailability(bool supported, IReadOnlyList<CoordinatedCompletionAction?> actions)
    {
        CompletionOption.IsEnabled = supported && actions.Count > 0 && actions.All(a => a?.IsValid() == true);
        if (!CompletionOption.IsEnabled) CompletionOption.IsChecked = false;
        _unavailable.Text = !supported ? "服务端尚不支持此选项；不勾选仍可按原方式开始。"
            : actions.Count == 0 ? "请先选择已绑定任务的在线成员。"
            : "有成员的动作尚未上报或配置不完整，请检查该成员的“绑定”。";
        _unavailable.Visibility = CompletionOption.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
    }

    private static TextBlock Text(string text, Color color) => new()
    {
        Text = text, FontSize = 12, Foreground = new SolidColorBrush(color),
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
    };
}

internal sealed class QuickCommandBindingWindow(CompletionActionChoicePanel completionChoice) : Window
{
    internal CompletionActionChoicePanel CompletionChoice { get; } = completionChoice;
    internal Button ConfirmButton { get; set; } = null!;
    internal bool UseCompletionActions => CompletionChoice.UseCompletionActions;
}
