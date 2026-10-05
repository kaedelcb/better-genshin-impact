using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.View.Pages;
using BetterGenshinImpact.ViewModel.Pages;
using Wpf.Ui;

namespace BetterGenshinImpact.Service.ExternalInterface;

internal static class ExternalResourceEditor
{
    internal static async Task<InstanceIpcEnvelope> DispatchAsync(InstanceIpcEnvelope request,
        TaskConfigurationContract? store = null, Action<bool, string, string?>? open = null)
    {
        store ??= TaskConfigurationContract.Default;
        try
        {
            if (ExecutionRequestContract.Validate(request) is { } rejection) return rejection;
            var config = InstanceIpcProtocol.GetStringOrNull(request.Data, "configName");
            var group = InstanceIpcProtocol.GetStringOrNull(request.Data, "groupName");
            var revision = InstanceIpcProtocol.GetStringOrNull(request.Data, "expectedConfigRevision");
            var taskId = InstanceIpcProtocol.GetStringOrNull(request.Data, "taskId");
            if ((config == null) == (group == null) || string.IsNullOrWhiteSpace(revision) || group != null && taskId != null)
                return InstanceIpcEnvelope.Failure(request, "invalid_request", "资源编辑要求唯一配置及引用修订，单项须属于一条龙");
            var name = config ?? group!;
            var snapshot = await store.ReadAsync(name, config != null).ConfigureAwait(false);
            void Check(TaskConfigurationContract.Snapshot current)
            {
                if (ExecutionRequestContract.Validate(request) is { } expired)
                    throw new InvalidOperationException(expired.ErrorCode);
                if (current.Revision != revision) throw new InvalidOperationException("configuration_changed");
                if (taskId != null && !current.Tasks.Any(t => t.TaskId == taskId))
                    throw new InvalidOperationException("task_not_found");
            }
            Check(snapshot);
            if (open != null) open(config != null, name, taskId);
            else
            {
                if (Application.Current == null) throw new InvalidOperationException("editor_unavailable");
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    // 排入UI队列期间可能发生保存；选择前重新读取同一权威配置。
                    Check(store.ReadAsync(name, config != null).GetAwaiter().GetResult());
                    OpenNative(config != null, name, taskId);
                });
            }
            return InstanceIpcEnvelope.Response(request, new { status = "editor_opened", configRevision = snapshot.Revision });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return InstanceIpcEnvelope.Failure(request, ex.Message is "configuration_changed" or "task_not_found" or "stale_epoch"
                ? ex.Message : "editor_unavailable", ex.Message);
        }
    }

    private static void OpenNative(bool oneDragon, string name, string? taskId)
    {
        var window = App.GetService<INavigationWindow>() ?? throw new InvalidOperationException("editor_unavailable");
        if (!window.Navigate(oneDragon ? typeof(OneDragonFlowPage) : typeof(ScriptControlPage)))
            throw new InvalidOperationException("editor_unavailable");
        if (oneDragon)
        {
            var vm = App.GetService<OneDragonFlowViewModel>() ?? throw new InvalidOperationException("editor_unavailable");
            vm.InitConfigList();
            vm.SelectedConfig = vm.ConfigList.Single(c => c.Name == name);
            vm.SetSomeSelectedConfig(vm.SelectedConfig);
            if (taskId != null) vm.SelectedTask = vm.TaskList.Single(t => t.Id == taskId);
        }
        else
        {
            var vm = App.GetService<ScriptControlViewModel>() ?? throw new InvalidOperationException("editor_unavailable");
            vm.ReloadScriptGroups();
            vm.SelectedScriptGroup = vm.ScriptGroups.Single(g => g.Name == name);
        }
        window.ShowWindow();
        if (window is Window native)
        {
            if (native.WindowState == WindowState.Minimized) native.WindowState = WindowState.Normal;
            native.Activate();
        }
    }
}
