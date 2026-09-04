using System.Windows;
using System.Windows.Input;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 远程配置组编辑的配置组选择窗口：列出目标成员的配置组供单选（仿 PasswordSetupWindow 风格）。
/// 契约见 Docs/远程配置组编辑-实施方案.md §5.2。
/// </summary>
public partial class RemoteConfigGroupSelectWindow : Window
{
    private RemoteConfigGroupSelectWindow(IEnumerable<string> groups, string memberName)
    {
        InitializeComponent();
        Title = $"远程编辑 - {memberName}";
        HintText.Text = $"请选择要远程编辑的「{memberName}」的配置组：";
        GroupList.ItemsSource = groups.ToList();
        if (GroupList.Items.Count > 0) GroupList.SelectedIndex = 0;
    }

    /// <summary>当前选中的配置组名；未选中为 null。</summary>
    public string? SelectedGroup => GroupList.SelectedItem as string;

    /// <summary>
    /// 弹出配置组选择对话框。返回选中的配置组名；取消返回 null。
    /// </summary>
    public static string? ShowSelectDialog(IEnumerable<string> groups, string memberName, Window? owner = null)
    {
        var dialog = new RemoteConfigGroupSelectWindow(groups, memberName)
        { Owner = owner ?? Application.Current?.MainWindow };
        return dialog.ShowDialog() == true ? dialog.SelectedGroup : null;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup == null)
        {
            ErrorText.Text = "请先选择一个配置组";
            return;
        }
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void GroupList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedGroup != null)
        {
            DialogResult = true;
        }
    }
}
