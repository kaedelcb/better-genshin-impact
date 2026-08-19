using System.Windows;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 免责声明弹窗。首次启动时展示，用户必须勾选"我已阅读并同意"才能点击"同意并继续"。
/// 点击"拒绝并退出"则关闭程序。
/// </summary>
public partial class DisclaimerWindow : Window
{
    /// <summary>免责声明文本。用字符串数组逐行声明，避免长字符串中的引号/转义导致编译错误。</summary>
    private static readonly string DisclaimerContent = string.Join("\n", new[]
    {
        "一、功能性质",
        "",
        "本工具是 BetterGI 联机锄地功能的辅助扩展，提供远程控制队友 BGI 进程的能力。",
        "本工具通过远程网络向队友的 BGI 发送操作指令（停止、启动任务、关闭游戏等），",
        "由队友本机的 BGI 自动执行。本工具不修改游戏文件、不读写游戏内存。",
        "",
        "二、开源声明与使用限制",
        "",
        "1. 本工具为开源项目，源代码公开，仅供参考与学习，不作任何商业用途。",
        "2. 请勿将本工具用于牟利、捆绑销售或任何形式的分发传播。",
        "3. 本工具仅用于个人技术交流与功能演示，请在下载后 24 小时内完成体验并删除。",
        "   请勿长期保留或部署使用。",
        "",
        "三、风险说明",
        "",
        "1.【封号风险】",
        "本工具属于第三方自动化操作软件，违反《原神》用户协议中关于使用第三方软件、",
        "模拟操作的条款。尽管本工具仅通过视觉算法和模拟操作实现，但仍存在被检测和",
        "封禁账号的可能。请低调使用，请勿在任何公开场合提及。",
        "",
        "2.【远程控制风险】",
        "本工具允许控制房间内任意成员远程停止队友 BGI、启动配置组或一条龙、关闭游戏、",
        "执行快捷键等操作。远程命令将对队友的电脑和游戏产生直接影响，请仅在充分信任",
        "的队友之间使用。",
        "",
        "3.【数据丢失风险】",
        "远程停止 BGI 或关闭游戏命令将强制中断队友当前正在运行的任务，可能造成",
        "未保存的进度或数据丢失。使用命令前请与队友沟通确认。",
        "",
        "4.【进程强制终止风险】",
        "当 IPC 通信不可用时，停止 BGI 将回退到杀进程方式。强制终止进程可能导致 BGI",
        "配置损坏或运行异常。",
        "",
        "5.【网络与安全风险】",
        "控制房间通过密码保护，密码由房主首次设置。密码泄露可能导致他人未经授权接入",
        "控制房间。请妥善保管密码，不要使用简单密码。",
        "",
        "6.【操作失误风险】",
        "启动配置组或一条龙命令支持从此处开始执行功能，错误选择起始任务可能导致",
        "队友执行了错误的操作序列。",
        "",
        "四、免责声明",
        "",
        "1. 本工具按现状提供，不提供任何明示或暗示的保证，包括但不限于不侵犯",
        "第三方权利、适销性或特定用途适用性。",
        "",
        "2. 使用本工具导致的任何直接或间接损失（包括但不限于账号封禁、游戏数据丢失、",
        "电脑故障等），开发者不承担任何责任。",
        "",
        "3. 使用者有义务确保：",
        "   a) 已获得被控队友的明确同意，方可对其发送远程命令；",
        "   b) 控制房间密码安全，不会泄露给非授权人员；",
        "   c) 自行承担使用本工具的一切风险。",
        "",
        "4. 如不同意本声明，请立即停止使用并卸载本工具。",
        "",
        "五、其他",
        "",
        "本声明适用中华人民共和国法律。如有争议，协商解决。",
    });

    private DisclaimerWindow()
    {
        InitializeComponent();
        DisclaimerText.Text = DisclaimerContent;
        AcceptButton.IsEnabled = false;
    }

    /// <summary>
    /// 显示免责声明弹窗。返回 true 表示用户已阅读并同意，false 表示拒绝。
    /// </summary>
    public static bool ShowDisclaimer(Window? owner = null)
    {
        var dialog = new DisclaimerWindow { Owner = owner ?? Application.Current?.MainWindow };
        return dialog.ShowDialog() == true;
    }

    private void AgreeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        AcceptButton.IsEnabled = AgreeCheckBox.IsChecked == true;
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void RejectButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "您未同意免责声明，程序将退出。\n\n是否确认退出？",
            "确认退出",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            DialogResult = false;
        }
    }
}