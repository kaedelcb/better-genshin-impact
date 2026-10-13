using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.Helpers.Ui;
using BetterGenshinImpact.ViewModel.Windows;
using Wpf.Ui.Controls;

namespace BetterGenshinImpact.View.Windows;

public partial class MojangBlackListConfigWindow : FluentWindow
{
    private readonly MojangBlackListConfigViewModel _viewModel;

    /// <summary>
    /// 创建拾取名单配置窗口。
    /// </summary>
    /// <param name="group">配置组作用域；为 null 时仅编辑全局黑名单（从触发器设置页打开）。</param>
    public MojangBlackListConfigWindow(ScriptGroup? group = null, bool isRemoteEdit = false)
    {
        _viewModel = new MojangBlackListConfigViewModel(group, isRemoteEdit);
        DataContext = _viewModel;
        InitializeComponent();

        _viewModel.CloseRequested += OnCloseRequested;
        SourceInitialized += (_, _) => WindowHelper.TryApplySystemBackdrop(this);
        Closed += (_, _) => _viewModel.CloseRequested -= OnCloseRequested;
    }

    private void OnCloseRequested(bool? dialogResult)
    {
        DialogResult = dialogResult;
    }
}
