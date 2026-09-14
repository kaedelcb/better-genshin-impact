using System.Windows.Controls;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 助手设置页面（用户控件）。放在 MainWindow 右侧内容区使用。
/// 通过 DataContext 继承 MainWindow 的 MainViewModel。
/// </summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        // 奥黛塔设置区每次进入都强制收起（不记忆展开状态，用户要求）
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) PetSettingsExpander.IsExpanded = false;
        };
    }
}