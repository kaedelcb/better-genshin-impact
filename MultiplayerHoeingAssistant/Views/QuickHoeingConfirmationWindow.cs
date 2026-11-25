using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.Views;

public sealed record QuickHoeingConfirmationMember(string Uid, string Name, string Bindings,
    CoordinatedCompletionAction? CompletionAction);

/// <summary>仅选择本次是否复用“绑定”中的动作，不创建或保存另一份动作配置。</summary>
public sealed class QuickHoeingConfirmationWindow : Window
{
    private readonly IReadOnlyDictionary<string, CoordinatedCompletionAction> _displayedActions;
    public bool Confirmed { get; private set; }
    public bool UseCompletionActions => CompletionOption.IsEnabled && CompletionOption.IsChecked == true;
    internal IReadOnlyDictionary<string, CoordinatedCompletionAction>? ConfirmedCompletionActions { get; private set; }
    internal CheckBox CompletionOption { get; }
    internal Button ConfirmButton { get; }

    public QuickHoeingConfirmationWindow(IReadOnlyList<QuickHoeingConfirmationMember> members,
        bool supportsCompletionChoice)
    {
        _displayedActions = members.GroupBy(m => m.Uid, StringComparer.Ordinal)
            .Where(g => g.Count() == 1 && g.First().CompletionAction != null)
            .ToDictionary(g => g.Key, g => g.First().CompletionAction!, StringComparer.Ordinal);
        Title = "确认下发一键锄地";
        Width = 620;
        Height = Math.Min(300 + members.Count * 86, 690);
        MaxHeight = Math.Max(350, SystemParameters.WorkArea.Height - 48);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = new FontFamily("HarmonyOS Sans SC, Microsoft YaHei");
        Background = Brush(0x1B, 0x19, 0x43);
        FitToOwnerWorkArea(this);

        var grid = new Grid { Margin = new Thickness(20) };
        foreach (var size in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = size });
        grid.Children.Add(Text($"确认对 {members.Count} 个成员下发「一键锄地」？", 15, Brush(0xE8, 0xC9, 0x6D)));

        var list = new StackPanel();
        foreach (var member in members)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 14), Tag = member.Uid };
            row.Children.Add(Text(member.Name, 13, Brush(0xF4, 0xF2, 0xFA)));
            row.Children.Add(Text("绑定任务：" + (member.Bindings.Length == 0 ? "未绑定" : member.Bindings), 12, Brush(0xB8, 0xB1, 0xD4)));
            row.Children.Add(Text("锄地后动作：" + (member.CompletionAction?.Description ?? "尚未上报（需更新该成员助手）"),
                12, Brush(0xE8, 0xC9, 0x6D)));
            list.Children.Add(row);
        }
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 16, 0, 8) };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        var options = new CompletionActionChoicePanel(supportsCompletionChoice,
            members.Select(m => _displayedActions.GetValueOrDefault(m.Uid)).ToArray());
        CompletionOption = options.CompletionOption;
        Grid.SetRow(options, 2);
        grid.Children.Add(options);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "取消", Width = 90, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        ConfirmButton = new Button { Content = "确认下发", Width = 100, Height = 32, IsDefault = true,
            Background = Brush(0xE8, 0xC9, 0x6D), Foreground = Brush(0x3A, 0x2F, 0x16) };
        ConfirmButton.Click += (_, _) =>
        {
            ConfirmedCompletionActions = UseCompletionActions ? _displayedActions : null;
            Confirmed = true;
            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ConfirmButton);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);
        Content = grid;
    }

    // Both manual confirmation dialogs keep their content scrollable and buttons visible on the owner's screen.
    internal static void FitToOwnerWorkArea(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (window.WindowStartupLocation != WindowStartupLocation.CenterOwner
                || Helpers.DpiAwarenessController.GetLogicalWorkAreaRect(window, window.Owner) is not { } work) return;
            const double margin = 12;
            window.MaxWidth = Math.Min(window.MaxWidth, Math.Max(280, work.Width - margin * 2));
            window.MaxHeight = Math.Max(240, work.Height - margin * 2);
            window.Width = Math.Min(window.Width, window.MaxWidth);
            window.Height = Math.Min(window.Height, window.MaxHeight);
            Helpers.DpiAwarenessController.PlaceWithinWorkArea(window, work, window.Height, margin);
        };
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
    private static TextBlock Text(string value, double size, Brush color) => new()
    {
        Text = value, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 0)
    };
}
