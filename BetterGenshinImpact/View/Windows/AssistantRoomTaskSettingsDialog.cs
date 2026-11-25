using BetterGenshinImpact.GameTask.AutoOnline;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using RoomDialog = Wpf.Ui.Controls.MessageBox;
using DialogText = Wpf.Ui.Controls.TextBlock;

namespace BetterGenshinImpact.View.Windows;

internal static class AssistantRoomTaskSettingsDialog
{
    internal sealed record RoomChoice(string? RoomName, string Label);

    internal static (RoomDialog Dialog, ComboBox Selector) Create(IReadOnlyList<string> rooms, string? currentRoomName, Window? owner, string taskName)
    {
        var allowCurrentRoom = taskName == NotifyOnlineTask.TaskName;
        var choices = rooms.Select(name => new RoomChoice(name, name)).ToList();
        if (allowCurrentRoom)
            choices.Insert(0, new RoomChoice(null, "当前登录房间（不切换）"));

        var dialog = new RoomDialog
        {
            Title = $"修改独立任务配置 - {taskName}",
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 440,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消"
        };
        var selector = new ComboBox
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(RoomChoice.Label),
            SelectedItem = choices.FirstOrDefault(choice => string.Equals(choice.RoomName,
                string.IsNullOrWhiteSpace(currentRoomName) ? null : currentRoomName, StringComparison.OrdinalIgnoreCase)),
            IsEnabled = choices.Count > 0
        };
        AutomationProperties.SetName(selector, "目标房间");
        dialog.IsPrimaryButtonEnabled = selector.SelectedItem is RoomChoice;

        var form = new StackPanel { Margin = new Thickness(10) };
        form.Children.Add(new DialogText { Text = "目标房间", Margin = new Thickness(0, 0, 0, 6) });
        form.Children.Add(selector);
        var hint = rooms.Count == 0
            ? "没有可选房间。请在联机助手的「管理房间」中保存房间，再打开此窗口。"
            : selector.SelectedItem is null && !string.IsNullOrEmpty(currentRoomName)
                ? $"原目标「{currentRoomName}」不在房间列表中，请重新选择。"
                : null;
        var status = new DialogText
        {
            Text = hint,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible
        };
        form.Children.Add(status);
        selector.SelectionChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = selector.SelectedItem is RoomChoice;
            status.Visibility = hint is not null && !dialog.IsPrimaryButtonEnabled ? Visibility.Visible : Visibility.Collapsed;
        };
        dialog.Content = form;
        return (dialog, selector);
    }
}
