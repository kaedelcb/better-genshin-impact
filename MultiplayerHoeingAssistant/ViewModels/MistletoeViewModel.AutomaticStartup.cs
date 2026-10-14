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
            ? (_mainVm.IsExecutorMode ? StartupSourceIntent.Explicit() : StartupSourceIntent.LocalOnlyExplicit()) : null;
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

    private StartupSourceIntent? PrepareStartupSource(bool manual)
    {
        StartupSourceIntent? source;
        if (manual)
        {
            source = _mainVm.IsExecutorMode ? StartupSourceIntent.Explicit() : StartupSourceIntent.LocalOnlyExplicit();
            if (_config.Enabled)
            {
                var oldAuthorization = _config.AutomaticAuthorization;
                var oldAuthority = _config.AutomaticStopAuthority;
                _config.AutomaticAuthorization = source.LocalOnly ? null : StartupAutomaticAuthorization.FromExplicit(source);
                _config.AutomaticStopAuthority = null;
                try { _store.SaveConfirmed(_config); _automaticSaveFailed = false; }
                catch
                {
                    _config.AutomaticAuthorization = oldAuthorization;
                    _config.AutomaticStopAuthority = oldAuthority;
                    _automaticSaveFailed = true;
                    throw;
                }
            }
        }
        else
        {
            if (_automaticSaveFailed) throw new InvalidOperationException("自动启动设置尚未保存成功");
            // 已保存的明确开启授权可一次性升级。只有 enabled 的旧配置仍需用户明确确认一次。
            if (_mainVm.IsExecutorMode && _config.AutomaticAuthorization is null
                && _config.AutomaticStopAuthority is { } saved)
            {
                _config.AutomaticAuthorization = new(saved.IntentId, saved.IntentTimestamp);
                try { _store.SaveConfirmed(_config); }
                catch { _config.AutomaticAuthorization = null; _automaticSaveFailed = true; throw; }
            }
            source = ResolveAutomaticStartupSource(_configuredAutoSource, _config.AutomaticStopAuthority,
                _mainVm.IsExecutorMode, _config.AutomaticAuthorization);
        }
        UseConfiguredStartupSource(source);
        return source;
    }

    private void UseConfiguredStartupSource(StartupSourceIntent? source)
    {
        _configuredAutoSource = source;
        _currentExplicitSourceId = source is { LocalOnly: false } ? source.IntentId : null;
    }

    private void RetryPendingAutomaticSource()
    {
        if (_config.Enabled && !_automaticSaveFailed && _config.AutomaticAuthorization?.Revoked != true
            && _configuredAutoSource is { LocalOnly: false, Revoked: false, Authority: null } source)
            _ = CaptureConfiguredSourceAsync(source);
    }

    private void RevokeCurrentAutomaticAuthorization(StartupSourceIntent source)
    {
        if (!_config.Enabled || source.IntentId != _currentExplicitSourceId
            || _config.AutomaticAuthorization is not { Revoked: false } authorization) return;
        _config.AutomaticAuthorization = authorization with { Revoked = true };
        try { _store.SaveConfirmed(_config); }
        catch { _automaticSaveFailed = true; throw; }
    }
}
