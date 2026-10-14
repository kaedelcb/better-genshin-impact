using System.IO;
using System.Text.Json;
using System.Windows;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class MistletoeViewModel
{
    private string _startupSavedState = "";
    private string _schemesSavedState = "";
    private (bool Enabled, int Mode) _autoLaunchSavedState;
    private readonly Dictionary<string, string> _startupSyncIssues = new();
    private string? _startupReadNotice;
    private bool _remoteStartupRefreshing;
    private bool _remoteRefreshPaused;
    private bool _startupSettingsReloading;
    public bool HasStartupSyncIssue => _startupSyncIssues.Count > 0 || _startupReadNotice is not null;
    public string? StartupSyncMessage => _startupSyncIssues.Count > 0
        ? string.Join("；", _startupSyncIssues.Values) : _startupReadNotice;
    internal Func<bool>? ConfirmReloadStartupSettingsForTest { get; set; }

    private static string StartupState(StartupFlowConfig config)
    {
        var editable = StartupFlowSchemeStore.Clone(config);
        editable.AutomaticStopAuthority = null;
        return JsonSerializer.Serialize(editable);
    }
    private string SchemesState() => JsonSerializer.Serialize(Schemes.Select(item => item.Scheme).ToArray());
    private string StartupEditState() => JsonSerializer.Serialize(new
    { Config = StartupState(_config), Schemes = SchemesState(), _remoteAutoLaunchOnBoot, _remoteAutoLaunchOnBootModeIndex });
    private void InitializeStartupSyncState()
    {
        _startupSavedState = StartupState(_config);
        _schemesSavedState = SchemesState();
        _autoLaunchSavedState = (_remoteAutoLaunchOnBoot, _remoteAutoLaunchOnBootModeIndex);
    }
    private void NotifyStartupSync()
    {
        OnPropertyChanged(nameof(HasStartupSyncIssue));
        OnPropertyChanged(nameof(StartupSyncMessage));
        OnPropertyChanged(nameof(IsNotRunning));
    }
    private void MarkStartupSyncIssue(string domain, string message)
    {
        _startupSyncIssues[domain] = domain + "：" + message + "。本端修改已保留，请读取最新启动设置后重新修改。";
        if (domain == "启动流程") { _remoteSaveFailed = true; _saveDebounce.Stop(); }
        else if (domain == "方案库") _remoteSchemeSaveFailed = true;
        else if (domain == "开机启动") _remoteAutoLaunchSaveFailed = true;
        NotifyStartupSync();
    }
    private void ReplaceSchemes(IReadOnlyList<StartupFlowScheme> schemes)
    {
        var selectedName = SelectedScheme?.Scheme.Name;
        SelectedScheme = null;
        Schemes.Clear();
        foreach (var scheme in schemes) Schemes.Add(new SchemeItemViewModel(scheme));
        SelectedScheme = Schemes.FirstOrDefault(item => item.Scheme.Name == selectedName);
    }
    private void ApplySyncedStartupConfig(StartupFlowConfig config)
    {
        var selectedId = SelectedStep?.Model.Id;
        ApplyConfig(config, save: false);
        StartupStepViewModel? Find(StepChainViewModel chain)
        {
            foreach (var step in chain.Steps)
            {
                if (step.Model.Id == selectedId) return step;
                var found = Find(step.TrueChain) ?? Find(step.FalseChain) ?? Find(step.FireChain);
                if (found is not null) return found;
            }
            return null;
        }
        if (selectedId is not null) SelectedStep = Find(RootChain);
    }

    private async Task RefreshRemoteStartupStatusAsync()
    {
        if (_remoteStartupRefreshing || _remoteAbandoned || _remoteRefreshPaused || _remoteTarget is null) return;
        _remoteStartupRefreshing = true;
        try
        {
            await RefreshRemoteStartupStatusCoreAsync();
            if (!_remoteAbandoned && !_remoteRefreshPaused) await RefreshRemoteStartupSettingsAsync();
        }
        finally { _remoteStartupRefreshing = false; }
    }

    private async Task RefreshRemoteStartupSettingsAsync()
    {
        if (_startupSettingsReloading) return;
        var configRevision = _remoteRevision;
        var schemesRevision = _remoteSchemesRevision;
        var autoLaunchRevision = _remoteAutoLaunchRevision;
        try
        {
            var reply = await _mainVm.RequestRemoteCenterAsync(_remoteTarget!, "startup.get");
            if (_remoteAbandoned || _remoteRefreshPaused) return;
            if (reply.Status != "ok" || string.IsNullOrWhiteSpace(reply.PayloadJson))
                throw new InvalidDataException(reply.Message);
            var snapshot = JsonSerializer.Deserialize<RemoteStartupSnapshot>(reply.PayloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("执行端启动设置为空");
            await OnRemoteUiAsync(() =>
            {
                if (_remoteAbandoned || _remoteRefreshPaused || _startupSettingsReloading) return;
                _startupReadNotice = null;
                // 在途保存完成后可能已推进修订，丢弃读请求发出时的旧快照。
                if (configRevision == _remoteRevision && snapshot.Revision != _remoteRevision && _remoteSaveTail.IsCompleted)
                {
                    if (_remoteSaveFailed || _saveDebounce.IsEnabled || StartupState(_config) != _startupSavedState)
                        MarkStartupSyncIssue("启动流程", "执行端已保存另一份修改");
                    else
                    {
                        ApplySyncedStartupConfig(snapshot.Config);
                        ApplyRemoteNodeRunStates(snapshot.NodeStates);
                        _remoteRevision = snapshot.Revision;
                        _startupSavedState = StartupState(_config);
                    }
                }
                if (schemesRevision == _remoteSchemesRevision && snapshot.SchemesRevision != _remoteSchemesRevision
                    && _remoteSchemeSaveTail.IsCompleted)
                {
                    if (_remoteSchemeSaveFailed || SchemesState() != _schemesSavedState)
                        MarkStartupSyncIssue("方案库", "执行端已保存另一份修改");
                    else
                    {
                        ReplaceSchemes(snapshot.Schemes);
                        _remoteSchemesRevision = snapshot.SchemesRevision;
                        _schemesSavedState = SchemesState();
                    }
                }
                if (autoLaunchRevision == _remoteAutoLaunchRevision && snapshot.AutoLaunchRevision != _remoteAutoLaunchRevision
                    && _remoteAutoLaunchSaveTail.IsCompleted)
                {
                    if (_remoteAutoLaunchSaveFailed
                        || (_remoteAutoLaunchOnBoot, _remoteAutoLaunchOnBootModeIndex) != _autoLaunchSavedState)
                        MarkStartupSyncIssue("开机启动", "执行端已保存另一份修改");
                    else
                    {
                        _remoteAutoLaunchOnBoot = snapshot.AutoLaunchOnBoot;
                        _remoteAutoLaunchOnBootModeIndex = snapshot.AutoLaunchOnBootModeIndex;
                        _remoteAutoLaunchRevision = snapshot.AutoLaunchRevision;
                        _autoLaunchSavedState = (_remoteAutoLaunchOnBoot, _remoteAutoLaunchOnBootModeIndex);
                        OnPropertyChanged(nameof(AutoLaunchOnBoot));
                        OnPropertyChanged(nameof(AutoLaunchOnBootModeIndex));
                    }
                }
                NotifyStartupSync();
            });
        }
        catch (Exception ex)
        {
            await OnRemoteUiAsync(() =>
            {
                if (_remoteAbandoned || _remoteRefreshPaused) return;
                _startupReadNotice = "执行端启动设置暂未同步：" + ex.Message;
                NotifyStartupSync();
            });
        }
    }

    public RelayCommand ReloadLatestStartupSettingsCommand => new(async _ =>
    {
        if (_remoteTarget is null || _remoteAbandoned || _startupSettingsReloading) return;
        var confirmed = ConfirmReloadStartupSettingsForTest?.Invoke() ?? MessageBox.Show(
            "读取最新启动设置会放弃本端尚未保存的启动流程、方案库和开机设置修改，已受理的执行操作不会撤销。是否继续？",
            "读取最新启动设置", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed) return;
        _startupSettingsReloading = true;
        _saveDebounce.Stop();
        var editState = StartupEditState();
        try
        {
            await Task.WhenAll(_remoteSaveTail, _remoteSchemeSaveTail, _remoteAutoLaunchSaveTail);
            var reply = await _mainVm.RequestRemoteCenterAsync(_remoteTarget, "startup.get");
            if (_remoteAbandoned || _remoteRefreshPaused) return;
            if (reply.Status != "ok" || string.IsNullOrWhiteSpace(reply.PayloadJson))
                throw new InvalidDataException(reply.Message);
            var snapshot = JsonSerializer.Deserialize<RemoteStartupSnapshot>(reply.PayloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("执行端启动设置为空");
            if (editState != StartupEditState())
            { MarkStartupSyncIssue("启动流程", "读取期间又出现了新的编辑，草稿已保留"); return; }
            ApplySyncedStartupConfig(snapshot.Config);
            ApplyRemoteNodeRunStates(snapshot.NodeStates);
            ReplaceSchemes(snapshot.Schemes);
            _remoteRevision = snapshot.Revision;
            _remoteSchemesRevision = snapshot.SchemesRevision;
            _remoteAutoLaunchRevision = snapshot.AutoLaunchRevision;
            _remoteAutoLaunchOnBoot = snapshot.AutoLaunchOnBoot;
            _remoteAutoLaunchOnBootModeIndex = snapshot.AutoLaunchOnBootModeIndex;
            InitializeStartupSyncState();
            _remoteSaveFailed = _remoteSchemeSaveFailed = _remoteAutoLaunchSaveFailed = false;
            _startupSyncIssues.Clear(); _startupReadNotice = null;
            OnPropertyChanged(nameof(AutoLaunchOnBoot)); OnPropertyChanged(nameof(AutoLaunchOnBootModeIndex));
            NotifyStartupSync();
        }
        catch (Exception ex) { MarkStartupSyncIssue("启动流程", "读取最新设置失败：" + ex.Message); }
        finally { _startupSettingsReloading = false; }
    });
}
