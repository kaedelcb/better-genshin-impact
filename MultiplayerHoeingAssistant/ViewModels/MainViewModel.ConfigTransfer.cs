using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Views;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private readonly object _configTransferGate = new();
    private volatile bool _configTransferInProgress;
    private volatile bool _configTransferRecoveryRequired;
    private volatile bool _configTransferDeferred;
    private int _configTransferOperations;
    private IDisposable? _retainedConfigTransferHostAdmission;
    public bool CanEditConfiguration => !_configTransferInProgress;
    public string ConfigurationRecoveryMessage => _configTransferRecoveryRequired
        ? "设置迁移恢复尚未完成，配置编辑与新任务已暂停。停止仍可使用；请退出后重新打开助手以恢复原设置。" : "";
    public string ConfigurationDeferredMessage => _configTransferDeferred
        ? "迁移已完成，连接设置待重新打开助手后生效。新任务和配置编辑已暂停；停止与退出仍可使用。" : "";
    public string ConfigurationScheduledOnlineMessage => _config?.ScheduledOnlineRequiresConfirmation == true
        ? "导入的定时上线尚未启用，重新打开助手也不会自动上线。请在本机成员卡片的“定时上线”中确认时间后启用；其他功能可照常使用。" : "";
    public string ConfigurationTransferStatusMessage => _configTransferRecoveryRequired
        ? ConfigurationRecoveryMessage : _configTransferDeferred ? ConfigurationDeferredMessage : ConfigurationScheduledOnlineMessage;
    private string ConfigTransferBlockedMessage => _configTransferRecoveryRequired
        ? ConfigurationRecoveryMessage : _configTransferDeferred ? ConfigurationDeferredMessage
            : "正在迁移设置，本次命令未发送，请稍后重试";

    internal sealed class ConfigTransferScope : IDisposable
    {
        private MainViewModel? _owner;
        internal ConfigTransferScope(MainViewModel owner) => _owner = owner;
        internal void RetainForRecovery()
        {
            var owner = _owner ?? throw new ObjectDisposedException(nameof(ConfigTransferScope));
            owner._configTransferRecoveryRequired = true;
            owner.OnPropertyChanged(nameof(ConfigurationRecoveryMessage));
            owner.OnPropertyChanged(nameof(ConfigurationTransferStatusMessage));
        }
        internal void RetainForDeferredBindings()
        {
            var owner = _owner ?? throw new ObjectDisposedException(nameof(ConfigTransferScope));
            owner._configTransferDeferred = true;
            owner.OnPropertyChanged(nameof(ConfigurationDeferredMessage));
            owner.OnPropertyChanged(nameof(ConfigurationTransferStatusMessage));
        }
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null || owner._configTransferRecoveryRequired || owner._configTransferDeferred) return;
            lock (owner._configTransferGate) owner._configTransferInProgress = false;
            owner.OnPropertyChanged(nameof(CanEditConfiguration));
        }
    }

    private sealed class ConfigTransferOperation : IDisposable
    {
        private MainViewModel? _owner;
        internal ConfigTransferOperation(MainViewModel owner) => _owner = owner;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null) lock (owner._configTransferGate) owner._configTransferOperations--;
        }
    }

    internal ConfigTransferScope BeginConfigTransferScope()
    {
        lock (_configTransferGate)
        {
            if (_configTransferInProgress) throw new InvalidOperationException(ConfigTransferBlockedMessage);
            if (_configTransferOperations != 0)
                throw new InvalidOperationException("仍有命令正在发送或执行，暂不能导入设置");
            _configTransferInProgress = true;
        }
        OnPropertyChanged(nameof(CanEditConfiguration));
        return new ConfigTransferScope(this);
    }

    // 停止不领取写入许可，迁移暂停和恢复失败均不得阻断停止。
    internal bool TryEnterConfigTransferOperation(string command, out IDisposable? operation)
    {
        operation = null;
        if (command == "stop") return true;
        lock (_configTransferGate)
        {
            if (_configTransferInProgress) return false;
            if (command == "scheduled_online" && _config?.ScheduledOnlineRequiresConfirmation == true) return false;
            _configTransferOperations++;
            operation = new ConfigTransferOperation(this);
            return true;
        }
    }

    // Shared by the explicit local dialog and an explicitly received schedule command.
    // Other settings saves cannot clear this persisted import authorization boundary.
    internal void SetConfirmedScheduledOnlineTime(string time)
    {
        var config = _config ?? throw new InvalidOperationException("助手配置尚未初始化");
        config.ScheduledOnlineTime = time;
        config.ScheduledOnlineRequiresConfirmation = false;
        OnPropertyChanged(nameof(ConfigurationScheduledOnlineMessage));
        OnPropertyChanged(nameof(ConfigurationTransferStatusMessage));
    }

    private CommandResult ConfigTransferRejectedResult() => new()
    {
        Status = "failed", ErrorCode = _configTransferRecoveryRequired
            ? "config_transfer_recovery_required" : _configTransferDeferred
                ? "config_transfer_deferred" : "config_transfer_in_progress",
        Message = ConfigTransferBlockedMessage,
    };

    private void ExportAllSettings()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出助手设置",
            Filter = "NexusBGI 助手设置包 (*.zip)|*.zip",
            FileName = $"NexusBGI助手设置_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var result = ExportSettingsWithRuntime(dialog.FileName);
            MessageBox.Show(result.Message, result.Success ? "导出成功" : "导出失败",
                MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败：" + ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal ConfigTransferService.ExportResult ExportSettingsWithRuntime(string zipPath)
    {
        var window = RequireConfigTransferWindow();
        window.Mistletoe.FlushConfigTransferEdits();
        window.Pet.FlushConfigTransferEdits();
        var snapshots = window.Dodoco.ExportConfigTransferSnapshots();
        foreach (var snapshot in window.Mistletoe.ExportConfigTransferSnapshots())
            snapshots[snapshot.Key] = snapshot.Value;
        snapshots["pet_settings.json"] = window.Pet.ExportConfigTransferSnapshot();
        snapshots["assistant-config.json"] = JsonSerializer.SerializeToUtf8Bytes(_config
            ?? throw new InvalidOperationException("助手配置尚未初始化"));
        return _configTransfer.Export(zipPath, snapshots);
    }

    private void ImportAllSettings()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入助手设置",
            Filter = "NexusBGI 助手设置包 (*.zip)|*.zip",
        };
        if (dialog.ShowDialog() != true) return;
        if (MessageBox.Show("导入将覆盖包内对应的助手配置和槲寄生计划；原配置自动备份。运行记录、本机实例身份不迁移。\n确定继续吗？",
                "导入助手设置", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var result = ImportSettingsWithRuntime(dialog.FileName);
            if (!result.Success)
            {
                MessageBox.Show(result.Message + (result.RecoveryRequired ? "\n\n" + ConfigurationRecoveryMessage : ""),
                    "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var restartMessage = _configTransferDeferred
                ? "\n\n现在重启助手以应用新的连接设置吗？"
                : "\n\n设置页面和槲寄生计划已重载，本次导入不会启动任务。现在重启助手吗？";
            if (MessageBox.Show(result.Message + restartMessage,
                    "导入成功", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                RestartAssistant();
        }
        catch (Exception ex)
        {
            MessageBox.Show("导入未完成：" + ex.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private MainWindow RequireConfigTransferWindow()
        => Application.Current?.MainWindow is MainWindow window && ReferenceEquals(window.ViewModel, this)
            ? window : throw new InvalidOperationException("助手主窗口尚未完成初始化");

    internal ConfigTransferService.ImportResult ImportSettingsWithRuntime(string zipPath)
    {
        var window = RequireConfigTransferWindow();
        bool reloadRules;
        using (var package = ZipFile.OpenRead(zipPath))
            reloadRules = package.Entries.Any(entry => entry.FullName == "dodoco_watch_rules.json");
        EnsureConfigTransferIdle(window);
        // 对话框返回后先结清已有编辑，避免旧防抖或草稿稍后覆盖新文件；失败则完全不开始导入。
        window.Mistletoe.FlushConfigTransferEdits();
        window.Pet.FlushConfigTransferEdits();
        EnsureConfigTransferIdle(window);
        TaskCenterHost host;
        lock (_taskCenterHostGate)
            host = _taskCenterHost ?? throw new InvalidOperationException("任务中心尚未初始化");
        using var transferScope = BeginConfigTransferScope();
        var originalBindings = CaptureConfigTransferRuntimeBindings(_config
            ?? throw new InvalidOperationException("助手配置尚未初始化"));
        EnsureConfigTransferIdle(window);
        var admission = host.TryBeginConfigTransfer();
        KeywordWatchService.ConfigTransferState rules;
        try { rules = window.Dodoco.BeginConfigTransfer(); }
        catch { admission.Dispose(); throw; }
        _configManager?.SetConfigTransferActive(true);
        _cacheManager?.SetConfigTransferActive(true);
        window.Pet.SetConfigTransferActive(true);
        window.Mistletoe.SetConfigTransferActive(true);
        var recoveryRequired = false;
        var runtimeRestored = false;
        var rulesEnded = false;
        var restoreRules = true;
        try
        {
            var additionalTargets = rules.SharedRulesTargetPath is { } shared
                ? new Dictionary<string, string> { ["dodoco_watch_rules.json"] = shared } : null;
            var result = _configTransfer.Import(zipPath, () =>
            {
                ReloadConfigTransferRuntime(window, reloadRules);
            }, additionalTargets, () =>
            {
                // A conflict may precede our first publication. The current disk can still
                // contain a legitimate peer save; never resume an older in-memory copy.
                ReloadConfigTransferRuntime(window, reloadRules);
                runtimeRestored = true;
            });
            if (result.Success)
            {
                restoreRules = false;
                if (DeferChangedConfigTransferRuntimeBindings(transferScope, originalBindings))
                    return result with { Message = result.Message + "\n\n" + ConfigurationDeferredMessage };
            }
            else if (!result.RecoveryRequired)
            {
                try
                {
                    if (!runtimeRestored) ReloadConfigTransferRuntime(window, reloadRules);
                    if (_config is { } current)
                        current.ScheduledOnlineRequiresConfirmation |= ConfigTransferSchedulePolicy.RequiresConfirmationAfterImport(originalBindings, current);
                    if (DeferChangedConfigTransferRuntimeBindings(transferScope, originalBindings))
                        result = result with { Message = result.Message + "\n\n" + ConfigurationDeferredMessage };
                }
                catch (Exception ex)
                {
                    result = result with { RecoveryRequired = true,
                        Message = result.Message + "\n当前落盘设置的运行时恢复未完成，继续暂停写入：" + ex.Message };
                }
            }
            recoveryRequired = result.RecoveryRequired;
            if (!recoveryRequired && !_configTransferDeferred)
            {
                try
                {
                    window.Dodoco.EndConfigTransfer(rules, restoreRules);
                    rulesEnded = true;
                }
                catch (Exception ex)
                {
                    recoveryRequired = true;
                    result = result with { RecoveryRequired = true,
                        Message = result.Message + "\n有效监控规则未能恢复，继续暂停写入：" + ex.Message };
                }
            }
            return result;
        }
        finally
        {
            if (recoveryRequired || !rulesEnded && !_configTransferDeferred)
            {
                transferScope.RetainForRecovery();
                _retainedConfigTransferHostAdmission = admission;
                AddLog(ConfigurationRecoveryMessage);
            }
            else if (_configTransferDeferred)
            {
                // 文件已成功提交；保留旧连接的停止能力，等待用户正常重新打开后读取新绑定。
                _retainedConfigTransferHostAdmission = admission;
                AddLog(ConfigurationDeferredMessage);
            }
            else
            {
                window.Pet.SetConfigTransferActive(false);
                window.Mistletoe.SetConfigTransferActive(false);
                _configManager?.SetConfigTransferActive(false);
                _cacheManager?.SetConfigTransferActive(false);
                admission.Dispose();
            }
        }
    }

    private static AssistConfig CaptureConfigTransferRuntimeBindings(AssistConfig config)
        => JsonSerializer.Deserialize<AssistConfig>(JsonSerializer.SerializeToUtf8Bytes(config))
            ?? throw new InvalidDataException("无法保存导入前的运行绑定快照");

    internal static bool ConfigTransferRuntimeBindingsChanged(AssistConfig previous, AssistConfig imported)
        => previous.ServerUrl != imported.ServerUrl || previous.ControlRoomPassword != imported.ControlRoomPassword
            || previous.PlayerUid != imported.PlayerUid || previous.ClientInstanceId != imported.ClientInstanceId
            || previous.ObserverMode != imported.ObserverMode || previous.StandaloneMode != imported.StandaloneMode
            || previous.BgiPath != imported.BgiPath || previous.GuardBgi != imported.GuardBgi
            || previous.BypassSystemProxy != imported.BypassSystemProxy
            || !previous.TeamUids.OrderBy(uid => uid, StringComparer.Ordinal)
                .SequenceEqual(imported.TeamUids.OrderBy(uid => uid, StringComparer.Ordinal));

    internal bool DeferChangedConfigTransferRuntimeBindings(ConfigTransferScope scope, AssistConfig previous)
    {
        if (_config is not { } imported || !ConfigTransferRuntimeBindingsChanged(previous, imported)) return false;
        scope.RetainForDeferredBindings();
        // These are the identities still owned by the existing connection/executor. Keep them in
        // memory for stop routing only; all configuration writers remain suspended, so new disk
        // values cannot be overwritten by the old runtime.
        imported.ServerUrl = previous.ServerUrl;
        imported.ControlRoomPassword = previous.ControlRoomPassword;
        imported.PlayerUid = previous.PlayerUid;
        imported.ClientInstanceId = previous.ClientInstanceId;
        imported.TeamUids = [.. previous.TeamUids];
        imported.ObserverMode = previous.ObserverMode;
        imported.StandaloneMode = previous.StandaloneMode;
        imported.BgiPath = previous.BgiPath;
        imported.GuardBgi = previous.GuardBgi;
        imported.BypassSystemProxy = previous.BypassSystemProxy;
        try
        {
            _processMonitor?.HoldAutomaticRestartForUserStop();
            _processMonitor?.Stop();
            StopSignalRRecovery();
            _retryTimer?.Dispose(); _retryTimer = null;
            _onlineTimer?.Dispose(); _onlineTimer = null;
            _statusTimer?.Dispose(); _statusTimer = null;
        }
        catch (Exception ex) { AddLog("迁移已完成，旧运行绑定暂停时发生异常：" + ex.Message); }
        try
        {
            RoomCode = AssistConfigManager.GenerateControlRoomCode(imported.TeamUids);
            RefreshSetupBindings();
            OnPropertyChanged(nameof(Config));
        }
        catch (Exception ex) { AddLog("迁移已完成，旧连接界面刷新未完成：" + ex.Message); }
        return true;
    }

    private void EnsureConfigTransferIdle(MainWindow window)
    {
        if (_disposing || Volatile.Read(ref _refreshRunning) != 0
            || HasLocalCoordinatedExecutionResponsibility || _intentLifecycle.IsOnlineReady
            || Volatile.Read(ref _ordinaryStopInProgress) != 0
            || _pendingCommandResults.Count != 0 || !_pendingCenterReplies.IsEmpty
            || _centerRequestReplies.Values.Any(reply => !reply.Completion.Task.IsCompleted)
            || _centerOperations.Values.Any(reply => reply.Status == "accepted")
            || LatestLocalStatus is { TaskRunning: true } or { AutoHoeingRunning: true })
            throw new InvalidOperationException("当前有任务、上线等待、命令回执或连接更新尚未结束，暂不能导入设置");
        window.Mistletoe.EnsureConfigTransferIdle();
    }

    private void ReloadConfigTransferRuntime(MainWindow window, bool reloadRules)
    {
        var previous = _config;
        var config = JsonSerializer.Deserialize<AssistConfig>(_configManager?.ReadRawJson()
                ?? throw new InvalidOperationException("助手配置管理器尚未初始化"),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("助手主配置为空");
        _config = config;
        if (previous?.AutoLaunchOnBoot != config.AutoLaunchOnBoot
            || previous?.AutoLaunchOnBootMinimized != config.AutoLaunchOnBootMinimized)
        {
            var registration = AutoStartupApply(config.AutoLaunchOnBoot, config.AutoLaunchOnBootMinimized, false);
            if (!registration.Success)
                throw new IOException("开机自启动设置未能同步：" + registration.Message);
        }
        RefreshSetupBindings();
        OnPropertyChanged(nameof(Config));
        RoomCode = AssistConfigManager.GenerateControlRoomCode(config.TeamUids);
        window.Mistletoe.ReloadConfigTransferSettings();
        window.Dodoco.ReloadConfigTransferSettings(reloadRules);
        window.Pet.ReloadConfigTransferSettings();
        var cached = _cacheManager?.Load();
        if (cached != null && Members.FirstOrDefault(m => m.IsSelf) is { } self)
        {
            self.ConfigGroups = cached.ConfigGroups;
            self.OneClickConfigs = cached.OneClickConfigs;
            self.ConfigGroupTasksWithStatus = cached.ConfigGroupTasksWithStatus;
            self.OneClickTasksWithStatus = cached.OneClickTasksWithStatus;
            self.Hotkeys = cached.Hotkeys;
        }
    }
}
