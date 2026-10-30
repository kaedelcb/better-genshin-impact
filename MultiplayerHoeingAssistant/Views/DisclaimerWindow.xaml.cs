using System.Windows;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 免责声明弹窗。首次启动时展示，用户勾选风险知悉后继续使用；GPL 的接收与运行权利不受此勾选限制。
/// 点击“退出”则关闭程序；设置中的回看入口不改变首次知悉状态。
/// </summary>
public partial class DisclaimerWindow : Window
{
    /// <summary>免责声明文本。用字符串数组逐行声明，避免长字符串中的引号/转义导致编译错误。</summary>
    private static readonly string DisclaimerContent = string.Join("\n", new[]
    {
        "一、功能与社区项目说明",
        "",
        "本助手为 BetterGI 茶包版提供联机协调、启动流程、任务与网页控制功能。",
        "本助手为社区项目，非 BetterGI 官方项目，不表示获得 BetterGI、米哈游或",
        "HoYoverse 的隶属、赞助或认可。新增与修改部分版权属于茶包及各自贡献者，",
        "源自 BetterGI 和第三方项目的部分保留原版权与许可声明。",
        "",
        "二、开源许可",
        "",
        "本助手代码采用 GNU GPL v3 授权，明确另有声明的第三方组件和资源除外。",
        "您可以在遵守许可证的条件下使用、修改和再分发相应代码，包括商业使用。",
        "软件按许可证规定不提供担保，具体权利及责任限制以许可证全文为准。",
        "接收或运行软件不要求您接受 GPL；修改和分发相应代码时须遵守其条件。",
        "字体、图片和其他第三方素材按各自授权提供，不统一改为 GPL 授权。",
        "本助手使用 HarmonyOS Sans 字体，Copyright 2021 Huawei Device Co., Ltd.。",
        "可点击下方“开源许可”查看随包全文，或在“设置 → 关于与开源许可”中",
        "查看第三方声明、字体协议和源码入口。声明中尚待确认的授权事项仍然保留。",
        "",
        "三、使用风险",
        "",
        "1.【游戏账号风险】自动化操作可能违反游戏使用规则，存在账号被限制或封禁的风险。",
        "请自行了解适用的游戏规则，谨慎选择是否使用。",
        "",
        "2.【远程操作风险】获授权的远程指令可能启动或停止任务、关闭游戏或程序，",
        "对目标电脑和游戏产生直接影响。请仅在获得目标用户同意后操作。",
        "",
        "3.【任务与数据风险】停止任务、关闭程序或强制终止进程可能中断执行，",
        "造成未保存的进度或数据丢失。操作前请确认目标与任务状态。",
        "",
        "4.【网络与授权风险】房间密码和浏览器授权凭据泄露可能导致未经授权的访问。",
        "请妥善保管凭据，仅向可信用户开放相应权限。",
        "",
        "5.【操作选择风险】错误的任务、成员或起始位置可能导致不符合预期的操作。",
        "请在执行前检查目标及配置。",
        "",
        "四、风险知悉",
        "",
        "下方勾选仅表示您已阅读并了解使用风险，不要求您放弃 GPL 授予的权利，",
        "也不扩大第三方素材的授权范围。无担保及责任限制以许可证和适用法律为准。",
        "如果暂不希望使用，可关闭本助手；以后可在设置中重新查看本说明。",
    });

    private readonly bool _reviewOnly;

    private DisclaimerWindow(bool reviewOnly = false)
    {
        InitializeComponent();
        _reviewOnly = reviewOnly;
        // 屏幕工作区适配：尺寸压到所在屏 95% 内并居中钳位，防止高缩放/小屏下窗口被截（与 MainWindow/SettingsWindow 同款）
        MultiplayerHoeingAssistant.Helpers.DpiAwarenessController.Initialize(this);
        DisclaimerText.Text = DisclaimerContent;
        AcceptButton.IsEnabled = reviewOnly;
        if (reviewOnly)
        {
            AgreeCheckBox.Visibility = Visibility.Collapsed;
            RejectButton.Visibility = Visibility.Collapsed;
            AcceptButton.Content = "关闭";
        }
    }

    /// <summary>
    /// 显示免责声明弹窗。返回 true 表示用户已阅读并了解风险，false 表示退出。
    /// </summary>
    public static bool ShowDisclaimer(Window? owner = null)
    {
        var dialog = new DisclaimerWindow { Owner = owner ?? Application.Current?.MainWindow };
        return dialog.ShowDialog() == true;
    }

    public static void ShowRiskInformation(Window? owner = null)
        => new DisclaimerWindow(true) { Owner = owner }.ShowDialog();

    private void OpenLicenses_Click(object sender, RoutedEventArgs e)
        => LegalDocumentWindow.ShowNotices(this);

    private void AgreeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        AcceptButton.IsEnabled = _reviewOnly || AgreeCheckBox.IsChecked == true;
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void RejectButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "您选择退出助手。\n\n是否确认退出？",
            "确认退出",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            DialogResult = false;
        }
    }
}