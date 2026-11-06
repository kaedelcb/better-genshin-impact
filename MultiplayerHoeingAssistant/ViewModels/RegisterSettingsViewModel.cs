using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed class RegisterSettingsViewModel : ViewModelBase
{
    private readonly RegisterEntry _original;
    public RegisterSettingsViewModel(RegisterEntry entry)
    {
        _original = entry;
        _value = entry.Value;
        Name = entry.Name;
        _modeIndex = entry.ResetMode == RegisterBank.Daily ? 1 : entry.ResetMode == RegisterBank.AfterChange ? 2 : 0;
        Hour = entry.ResetHour;
        Minute = entry.ResetMinute;
        var delay = TimeSpan.FromSeconds(entry.DelaySeconds);
        Days = delay.Days; DelayHours = delay.Hours; DelayMinutes = delay.Minutes; DelaySeconds = delay.Seconds;
    }
    public string Title => $"R{_original.Number} · 寄存器设置";
    public int Number => _original.Number;
    private int? _value;
    public string CurrentState => $"当前状态：{(_value is { } value ? value.ToString() : "未确认")}　两个中心共用此位的名称和重置规则";
    public void UpdateValue(int? value) { _value = value; OnPropertyChanged(nameof(CurrentState)); }
    private string _operationStatus = "";
    public string OperationStatus { get => _operationStatus; set => SetProperty(ref _operationStatus, value); }
    public string Name { get; set; }
    public static IReadOnlyList<int> DayChoices { get; } = Enumerable.Range(0, 31).ToArray();
    public static IReadOnlyList<int> HourChoices { get; } = Enumerable.Range(0, 24).ToArray();
    public static IReadOnlyList<int> MinuteChoices { get; } = Enumerable.Range(0, 60).ToArray();
    public int Hour { get; set; }
    public int Minute { get; set; }
    public int Days { get; set; }
    public int DelayHours { get; set; }
    public int DelayMinutes { get; set; }
    public int DelaySeconds { get; set; }
    private int _modeIndex;
    public int ModeIndex
    {
        get => _modeIndex;
        set { if (SetProperty(ref _modeIndex, value)) { OnPropertyChanged(nameof(IsDaily)); OnPropertyChanged(nameof(IsAfterChange)); } }
    }
    public bool IsDaily => ModeIndex == 1;
    public bool IsAfterChange => ModeIndex == 2;
    private string _error = "";
    public string Error { get => _error; set => SetProperty(ref _error, value); }
    public RegisterConfigurationChange ToChange() => new()
    {
        Number = _original.Number, Name = Name, ExpectedSettingsRevision = _original.SettingsRevision,
        ResetMode = ModeIndex == 1 ? RegisterBank.Daily : ModeIndex == 2 ? RegisterBank.AfterChange : RegisterBank.Never,
        ResetHour = Hour, ResetMinute = Minute,
        DelaySeconds = Days * 86400 + DelayHours * 3600 + DelayMinutes * 60 + DelaySeconds
    };
}
