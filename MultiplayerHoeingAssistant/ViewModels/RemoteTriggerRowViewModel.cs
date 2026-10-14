using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>原触发器行的远程数据源；三类卡片仍使用执行端原有模板。</summary>
public sealed class RemoteTriggerRowViewModel : ViewModelBase
{
    private RemoteTriggerInfo _info;
    private readonly MistletoeViewModel _owner;
    private bool _cancelInFlight;

    public RemoteTriggerRowViewModel(RemoteTriggerInfo info, MistletoeViewModel owner)
    {
        _info = info;
        _owner = owner;
    }

    public string RuntimeId => _info.RuntimeId;
    public StartupStep? Step => _info.Step;
    public string StatusLine => _info.Text;
    public string LastCheckNote => _info.Note ?? "";
    public string StatusNote => _info.Note ?? "";
    public RelayCommand LocateCommand => new(_ => _owner.LocateRemoteStep(_info.Step?.Id));
    public RelayCommand ViewFlowCommand => new(_ => _owner.ViewFlow(
        _info.Step?.FireSteps ?? [], $"执行端触发器「{_info.Text}」的触发流程"));
    public RelayCommand CancelCommand => new(async _ =>
    {
        if (_cancelInFlight) return;
        _cancelInFlight = true;
        try { await _owner.CancelRemoteTriggerAsync(RuntimeId); }
        finally { _cancelInFlight = false; }
    });

    public void Update(RemoteTriggerInfo info)
    {
        _info = info;
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(LastCheckNote));
        OnPropertyChanged(nameof(StatusNote));
    }
}
