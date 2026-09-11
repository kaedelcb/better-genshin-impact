using System.Windows;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 槲寄生 · 流程预览弹窗（只读）：把一条节点子链的内容平铺成缩进树展示，
/// 供「定时中的触发器」「盯梢中的电子狗」行的「流程」按钮查看挂载时到底会执行什么。
/// 摘要口径复用 <see cref="StartupStepViewModel.BuildSummary"/>（与启动中心卡片一致）。
/// </summary>
public partial class FlowPreviewWindow : Window
{
    /// <summary>一行展示项：缩进 + 图标 + 节点名 + 摘要后缀。</summary>
    private sealed record FlowLine(Thickness IndentMargin, string Icon, string Name, string Suffix);

    private FlowPreviewWindow(string title, IReadOnlyList<StartupStep> steps, Window? owner)
    {
        InitializeComponent();
        Owner = owner;
        WindowStartupLocation = owner == null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        TitleText.Text = title;

        var lines = new List<FlowLine>();
        AddChain(steps, 0, lines);
        Lines.ItemsSource = lines;
    }

    /// <summary>递归平铺一条链：条件节点展开是/否子链，定时器/电子狗展开「触发执行」子链。</summary>
    private static void AddChain(IReadOnlyList<StartupStep> steps, int depth, List<FlowLine> lines)
    {
        if (steps.Count == 0)
        {
            lines.Add(Line(depth, "·", "（空）", ""));
            return;
        }
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var name = StartupFlowRunner.DisplayName(s, i) + (s.Enabled ? "" : "（已禁用）");
            var summary = StartupStepViewModel.BuildSummary(s);
            lines.Add(Line(depth, StartupStepKinds.Find(s.Kind)?.Icon ?? "▶", name,
                string.IsNullOrEmpty(summary) ? "" : $"  —  {summary}"));

            if (s.NodeType == "condition")
            {
                lines.Add(Line(depth + 1, "✓", "是分支", ""));
                AddChain(s.TrueSteps, depth + 2, lines);
                lines.Add(Line(depth + 1, "✗", "否分支", ""));
                AddChain(s.FalseSteps, depth + 2, lines);
            }
            if (s.Kind is StartupStepKinds.TimerTrigger or StartupStepKinds.Watchdog or StartupStepKinds.LogTrigger)
            {
                lines.Add(Line(depth + 1, "➤", s.Kind == StartupStepKinds.TimerTrigger ? "到点执行" : "触发执行", ""));
                AddChain(s.FireSteps, depth + 2, lines);
            }
        }
    }

    private static FlowLine Line(int depth, string icon, string name, string suffix) =>
        new(new Thickness(depth * 18, 2, 0, 2), icon, name, suffix);

    /// <summary>弹窗展示一条子链的流程（模态）。托盘/静默场景主窗口不可见时无 Owner 居中屏幕显示。</summary>
    public static void Show(string title, IReadOnlyList<StartupStep> steps, Window? owner = null)
    {
        var o = owner ?? Application.Current?.MainWindow;
        if (o is not { IsLoaded: true, IsVisible: true }) o = null;
        new FlowPreviewWindow(title, steps, o).ShowDialog();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
