using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class MistletoeViewModel
{
    private int _automaticCaptureBusy;
    private bool _automaticSaveFailed;

    private void SetAutomaticStartupEnabled(bool enabled)
    {
        var wasEnabled = _config.Enabled;
        var oldAuthorization = _config.AutomaticAuthorization;
        var oldAuthority = _config.AutomaticStopAuthority;
        var source = enabled
            ? (_mainVm.IsExecutorMode ? StartupSourceIntent.Startup() : StartupSourceIntent.Startup(localOnly: true)) : null;
        _config.Enabled = enabled;
        _config.AutomaticAuthorization = source is { LocalOnly: false }
            ? StartupAutomaticAuthorization.FromExplicit(source) : null;
        _config.AutomaticStopAuthority = null;
        try
        {
            _store.SaveConfirmed(_config);
            _automaticSaveFailed = false;
            UseConfiguredStartupSource(source);
            _mainVm.AddLog($"[槲寄生] 启动流程自动执行已{(enabled ? "开启" : "关闭")}并保存");
            if (source is not null) _ = CaptureConfiguredSourceAsync(source);
        }
        catch (Exception ex)
        {
            _config.Enabled = wasEnabled;
            _config.AutomaticAuthorization = oldAuthorization;
            _config.AutomaticStopAuthority = oldAuthority;
            _automaticSaveFailed = true;
            StatusText = "自动启动设置保存失败：" + ex.Message;
            _mainVm.AddLog("[槲寄生] " + StatusText);
        }
        OnPropertyChanged(nameof(FlowEnabled));
    }

    private StartupSourceIntent PrepareStartupSource(bool manual)
    {
        // Enabled 是长期设置；每次新启动使用新意图，绝不继承旧运行的停止标记。
        var source = ResolveAutomaticStartupSource(null, _config.AutomaticStopAuthority,
            _mainVm.IsExecutorMode, _config.AutomaticAuthorization);
        if (_config.Enabled)
        {
            var oldAuthorization = _config.AutomaticAuthorization;
            var oldAuthority = _config.AutomaticStopAuthority;
            _config.AutomaticAuthorization = source.LocalOnly ? null : StartupAutomaticAuthorization.FromExplicit(source);
            _config.AutomaticStopAuthority = null;
            try
            {
                _store.SaveConfirmed(_config);
                _automaticSaveFailed = false;
                if (oldAuthorization?.Revoked == true)
                    _mainVm.AddLog("[槲寄生] 已自动修复旧版自动启动禁用标记，保留原流程和自动执行设置");
            }
            catch
            {
                _config.AutomaticAuthorization = oldAuthorization;
                _config.AutomaticStopAuthority = oldAuthority;
                _automaticSaveFailed = true;
                source.Revoke();
                throw;
            }
        }
        UseConfiguredStartupSource(source);
        return source;
    }

    private void UseConfiguredStartupSource(StartupSourceIntent? source)
    {
        if (!ReferenceEquals(_configuredAutoSource, source) && _configuredAutoSource is { } previous)
        {
            previous.Revoke();
            RevokeAllArmedTriggers();
        }
        if (source is null)
        {
            _runCts?.Cancel();
            RevokeAllArmedTriggers();
        }
        _configuredAutoSource = source;
        _currentExplicitSourceId = source is { LocalOnly: false } ? source.IntentId : null;
    }

    private void RetryPendingAutomaticSource()
    {
        if (_mainVm.IsExecutorMode && _config.Enabled && !_automaticSaveFailed && _config.AutomaticAuthorization?.Revoked != true
            && _configuredAutoSource is { LocalOnly: false, Revoked: false, Authority: null } source)
            _ = CaptureConfiguredSourceAsync(source);
    }

    private void RearmStoppedTimer(ArmedTimerViewModel timer)
    {
        if (!_config.Enabled || _startupClosing || _configTransferPaused
            || (!timer.Step.RepeatDaily && timer.NextFireAt <= DateTime.Now) || !timer.Step.Enabled) return;
        // 重新挂载的是下一次到点计划。已到点/正在运行的旧次数不补跑。
        ArmTimer(timer.Step, StartupSourceIntent.NextScheduledOccurrence(timer.SourceIntent, localOnly: !_mainVm.IsExecutorMode));
    }
}
