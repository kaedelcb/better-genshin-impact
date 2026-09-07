using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 槲寄生 · 调度器页面代码后置。
/// 只承担两件事（MVVM 不便纯 XAML 表达的部分）：
/// 1. 节点卡片的拖拽重排（DragDrop）——支持同链换位与跨链移动（主链 ↔ 条件的是/否子链），
///    落点在卡片上按上下半区换算插入位次，落点在链空白区（分支列/主链面板）追加到链尾；
///    环检测（条件节点拖进自己的子链）在 ViewModel 的 MoveStepTo 里拒绝。
/// 2. 「浏览…」文件选择框与卡片点击展开/收起编辑器。
/// 拖拽只发生在节点手柄（⠿）上，卡片其余区域点击 = 展开/收起参数编辑器。
/// </summary>
public partial class MistletoePage : UserControl
{
    private MistletoeViewModel? Vm => DataContext as MistletoeViewModel;

    private Point _dragStartPoint;
    private StartupStepViewModel? _dragCandidate;

    public MistletoePage()
    {
        InitializeComponent();
    }

    // ================= 拖拽重排 =================

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = (sender as FrameworkElement)?.DataContext as StartupStepViewModel;
        _dragStartPoint = e.GetPosition(null);
        // 标记预览事件已处理，抑制配对的冒泡 MouseLeftButtonDown 到达节点卡（否则会误触发展开/收起）
        e.Handled = true;
    }

    private void DragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStartPoint.X) < 6 && Math.Abs(pos.Y - _dragStartPoint.Y) < 6) return;

        var dragged = _dragCandidate;
        _dragCandidate = null;
        if (sender is FrameworkElement handle)
        {
            DragDrop.DoDragDrop(handle, new DataObject(typeof(StartupStepViewModel), dragged), DragDropEffects.Move);
        }
    }

    private void NodeCard_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(StartupStepViewModel)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    /// <summary>落在节点卡上：插入到目标卡片前/后（按落点上下半区）。</summary>
    private void NodeCard_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(StartupStepViewModel))) return;
        if (e.Data.GetData(typeof(StartupStepViewModel)) is not StartupStepViewModel dragged) return;
        if ((sender as FrameworkElement)?.DataContext is not StartupStepViewModel target) return;
        if (Vm == null) return;

        var card = (FrameworkElement)sender;
        if (ReferenceEquals(dragged, target))
        {
            e.Handled = true;
            return;
        }

        var dropPos = e.GetPosition(card);
        var insertAfter = dropPos.Y > card.ActualHeight / 2;

        var targetChain = target.OwnerChain;
        var targetIndex = targetChain.Steps.IndexOf(target);
        if (targetIndex < 0) return;
        if (insertAfter) targetIndex++;

        // 同链前移的位次换算在 MoveStepTo 内统一处理
        Vm.MoveStepTo(dragged, targetChain, targetIndex);
        e.Handled = true;
    }

    /// <summary>链空白区（分支列 / 主链面板）悬停：接受本页节点。</summary>
    private void Chain_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(StartupStepViewModel)))
        {
            e.Effects = DragDropEffects.Move;
            // 不设 Handled：让落点更深处的节点卡优先处理
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    /// <summary>落在链空白区：追加到该链末尾（sender 的 DataContext 是 StepChainViewModel）。</summary>
    private void Chain_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(StartupStepViewModel))) return;
        if (e.Data.GetData(typeof(StartupStepViewModel)) is not StartupStepViewModel dragged) return;
        if ((sender as FrameworkElement)?.DataContext is not StepChainViewModel chain) return;
        if (Vm == null) return;

        // 拖拽项已在该链末尾时无操作
        if (ReferenceEquals(dragged.OwnerChain, chain) && chain.Steps.Count > 0
            && ReferenceEquals(chain.Steps[^1], dragged))
        {
            e.Handled = true;
            return;
        }

        Vm.MoveStepTo(dragged, chain, chain.Steps.Count);
        e.Handled = true;
    }

    // ================= 卡片点击展开/收起编辑器 =================

    private void NodeCard_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StartupStepViewModel vm) return;
        Vm?.ToggleSelectCommand.Execute(vm);
        e.Handled = true;
    }

    // ================= 程序路径浏览 =================

    private void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StartupStepViewModel vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "可执行程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            Title = "选择要启动的程序",
        };
        if (dlg.ShowDialog() == true)
        {
            vm.Path = dlg.FileName;
        }
    }
}
