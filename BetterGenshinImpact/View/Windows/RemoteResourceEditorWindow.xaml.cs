using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using BetterGenshinImpact.Helpers.Ui;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.Service.RemoteEditor;
using BetterGenshinImpact.View.Pages;
using BetterGenshinImpact.ViewModel.Pages;
using Wpf.Ui.Controls;
using MessageBoxButton = System.Windows.MessageBoxButton;

namespace BetterGenshinImpact.View.Windows;

/// <summary>托管原版配置组/一条龙页面，页面读写只落在本次远程编辑副本。</summary>
public partial class RemoteResourceEditorWindow : FluentWindow
{
    private readonly RemoteResourceEditorLaunch _launch;
    private bool _completed;
    private bool _ready;

    internal RemoteResourceEditorWindow(RemoteResourceEditorLaunch launch)
    {
        _launch = launch;
        InitializeComponent();
        if (launch.Metadata.HiddenForTest)
        {
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Top = -20000;
        }
        var target = launch.Metadata;
        Title = "远程资源编辑 · " + target.ResourceName;
        TargetText.Text = $"{target.TargetName} · {target.TargetUid} · {target.TargetInstanceId[..Math.Min(8, target.TargetInstanceId.Length)]} / {target.ResourceName}";
        NativePage.Content = target.OneDragon
            ? App.GetService<OneDragonFlowPage>() ?? throw new InvalidOperationException("原版一条龙编辑页面不可用")
            : App.GetService<ScriptControlPage>() ?? throw new InvalidOperationException("原版配置组编辑页面不可用");
        SourceInitialized += (_, _) => WindowHelper.TryApplySystemBackdrop(this);
        Loaded += (_, _) => InitializeNativePage();
        Closing += OnClosing;
    }

    private void InitializeNativePage()
    {
        if (_ready) return;
        if (_launch.Metadata.OneDragon)
        {
            var vm = ((OneDragonFlowPage)NativePage.Content).ViewModel;
            vm.InitConfigList();
            vm.SelectedConfig = vm.ConfigList.Single(config => config.Name == _launch.Metadata.ResourceName);
            vm.SetSomeSelectedConfig(vm.SelectedConfig);
            if (_launch.Metadata.TaskId is { Length: > 0 } taskId)
                vm.SelectedTask = vm.TaskList.Single(task => task.Id == taskId);
        }
        else
        {
            var vm = ((ScriptControlPage)NativePage.Content).ViewModel;
            vm.ReloadScriptGroups();
            vm.SelectedScriptGroup = vm.ScriptGroups.Single(group => group.Name == _launch.Metadata.ResourceName);
        }
        App.GetService<IConfigService>()!.Flush();
        _launch.CaptureBaseline();
        _launch.PublishReady();
        _ready = true;
        SaveButton.IsEnabled = true;
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _completed) return;
        SaveButton.IsEnabled = false;
        try
        {
            App.GetService<IConfigService>()!.Flush();
            _launch.PublishResult(saved: true, _launch.CaptureChanges());
            _completed = true;
            Close();
        }
        catch (Exception ex)
        {
            SaveButton.IsEnabled = true;
            ThemedMessageBox.Error("编辑结果未回传，副本已保留：" + ex.Message, "远程资源编辑", MessageBoxButton.OK);
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_completed) return;
        try
        {
            if (_ready)
            {
                var dirty = true;
                var message = "有尚未回传的配置修改。关闭会取消本次回传，执行端配置保持原样。确定关闭吗？";
                try
                {
                    App.GetService<IConfigService>()!.Flush();
                    dirty = _launch.CaptureChanges().Count > 0;
                }
                catch (Exception ex) { message = "编辑副本无法核对：" + ex.Message + "\n关闭将取消本次回传并保留副本。是否关闭？"; }
                if (dirty && ThemedMessageBox.Question(message,
                    "远程资源编辑", MessageBoxButton.YesNo, System.Windows.MessageBoxResult.No)
                    != System.Windows.MessageBoxResult.Yes)
                { e.Cancel = true; return; }
            }
            _launch.PublishResult(saved: false);
            _completed = true;
        }
        catch (Exception ex)
        {
            e.Cancel = true;
            ThemedMessageBox.Error("取消结果无法写入，编辑副本已保留：" + ex.Message, "远程资源编辑", MessageBoxButton.OK);
        }
    }
}
