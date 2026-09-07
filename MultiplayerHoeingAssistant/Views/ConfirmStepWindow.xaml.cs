using System.Windows;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 槲寄生·启动中心「人工确认」节点的确认弹窗（仿 RemoteConfigGroupSelectWindow 风格，本工程无 ThemedMessageBox）。
/// 显示提示内容 + 是/否两条分支的后续动作预览，供人判断；
/// 支持超时倒计时，超时（或直接关窗未做选择）自动走节点配置的默认分支。
/// </summary>
public partial class ConfirmStepWindow : Window
{
    private readonly DispatcherTimer _timer;
    private int _remaining;
    private readonly bool _timeoutGoTrue;
    private bool? _choice;
    private bool _timedOut;

    private ConfirmStepWindow(StartupStep step)
    {
        InitializeComponent();
        _timeoutGoTrue = step.ConfirmTimeoutGoTrue;

        MessageText.Text = string.IsNullOrWhiteSpace(step.ConfirmMessage)
            ? $"节点「{StartupFlowRunner.DisplayName(step, 0)}」等待人工确认："
            : step.ConfirmMessage;

        TruePreview.Text = BuildPreview(step.TrueSteps);
        FalsePreview.Text = BuildPreview(step.FalseSteps);

        _remaining = Math.Max(0, step.ConfirmTimeoutSeconds);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        if (_remaining > 0)
        {
            UpdateCountdownText();
            _timer.Start();
        }
        else
        {
            CountdownText.Text = "不限时，请手动选择「是」或「否」。";
        }
    }

    /// <summary>分支预览文本：逐行列出节点（图标+显示名），最多 6 行，超出折叠计数。</summary>
    private static string BuildPreview(IReadOnlyList<StartupStep> steps)
    {
        if (steps.Count == 0) return "（空：直接回到主流程）";
        const int maxLines = 6;
        var lines = steps.Take(maxLines)
            .Select((s, i) => $"{StartupStepKinds.Find(s.Kind)?.Icon ?? "▶"} {StartupFlowRunner.DisplayName(s, i)}{(s.Enabled ? "" : "（已禁用）")}");
        var text = string.Join("\n", lines);
        return steps.Count > maxLines ? text + $"\n…共 {steps.Count} 个节点" : text;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            _timer.Stop();
            _timedOut = true;
            _choice = _timeoutGoTrue;
            DialogResult = true;
            return;
        }
        UpdateCountdownText();
    }

    private void UpdateCountdownText()
    {
        CountdownText.Text = $"{_remaining} 秒后未选择将自动走「{(_timeoutGoTrue ? "是" : "否")}」分支。";
    }

    /// <summary>
    /// 弹出人工确认对话框。返回 (走向, 判断依据描述)：true=是分支，false=否分支。
    /// 超时与未做选择直接关窗都按节点配置的默认分支走（描述里注明原因）。
    /// </summary>
    public static (bool passed, string desc) ShowConfirm(StartupStep step, Window? owner = null)
    {
        var dialog = new ConfirmStepWindow(step)
        { Owner = owner ?? Application.Current?.MainWindow };
        dialog.ShowDialog();

        var choice = dialog._choice ?? dialog._timeoutGoTrue;
        var desc = dialog._timedOut
            ? $"超时（{step.ConfirmTimeoutSeconds} 秒）未选择，自动走「{(choice ? "是" : "否")}」"
            : dialog._choice == null
                ? $"未做选择（窗口被关闭），按默认走向「{(choice ? "是" : "否")}」"
                : $"人工选择「{(choice ? "是" : "否")}」";
        return (choice, desc);
    }

    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _choice = true;
        DialogResult = true;
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _choice = false;
        DialogResult = true;
    }
}
