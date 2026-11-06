using System.Collections.ObjectModel;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed class RegisterBankViewModel
{
    private readonly Func<RegisterConfigurationChange, Task<RegisterBankSnapshot>> _save;
    public ObservableCollection<RegisterCellViewModel> Cells { get; } = new(
        Enumerable.Range(1, RegisterBank.Count).Select(i => new RegisterCellViewModel(i)));

    public RegisterBankViewModel(Func<RegisterConfigurationChange, Task<RegisterBankSnapshot>> save) => _save = save;

    public void Apply(RegisterBankSnapshot? snapshot)
    {
        var valid = snapshot?.Available == true && snapshot.Entries is { Count: RegisterBank.Count }
            && snapshot.Entries.Select((e, i) => e is not null && e.Number == i + 1 && e.Value is 0 or 1).All(x => x);
        for (var i = 0; i < Cells.Count; i++)
            Cells[i].Apply(valid ? snapshot!.Entries[i] : null,
                snapshot?.Error is { Length: > 0 } error ? error : "执行端尚未提供寄存器状态，请检查连接或版本。");
    }

    public async Task SaveAsync(RegisterConfigurationChange change)
    {
        var snapshot = await _save(change);
        Apply(snapshot);
        if (!snapshot.Available) throw new InvalidOperationException(snapshot.Error);
    }
}

public sealed class RegisterCellViewModel(int number) : ViewModelBase
{
    public RegisterEntry Entry { get; private set; } = new() { Number = number };
    public bool Available { get; private set; }
    private string _error = "尚未读取寄存器";
    public string Label => Entry.Label;
    public string ValueText => Available ? Entry.Value.ToString() : "?";
    public bool IsOne => Available && Entry.Value == 1;
    public string ToolTip => !Available ? $"{Label}\n{_error}" : $"{Label}\n当前状态：{Entry.Value}\n{ResetDescription}\n点击设置名称和重置规则";
    public string ResetDescription => Entry.ResetMode switch
    {
        RegisterBank.Daily => $"每天 {Entry.ResetHour:00}:{Entry.ResetMinute:00} 重置为0",
        RegisterBank.AfterChange => $"状态变化后 {TimeSpan.FromSeconds(Entry.DelaySeconds):d\\天hh\\时mm\\分ss\\秒} 重置为0",
        _ => "不自动重置"
    };

    internal void Apply(RegisterEntry? entry, string error)
    {
        Available = entry is not null;
        if (entry is not null) Entry = entry;
        _error = error;
        OnPropertyChanged(nameof(Available));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(IsOne));
        OnPropertyChanged(nameof(ToolTip));
    }
}

public sealed class RegisterSelectionRowViewModel(RegisterCellViewModel cell, List<int?> values,
    int index, bool condition, Action changed) : ViewModelBase
{
    public RegisterCellViewModel Cell => cell;
    public IReadOnlyList<string> Options { get; } = condition ? ["忽略", "等于0", "等于1"] : ["保持", "置0", "置1"];
    public int SelectedIndex
    {
        get => values[index] is null ? 0 : values[index] == 0 ? 1 : 2;
        set
        {
            if (value is < 0 or > 2 || value == SelectedIndex) return;
            values[index] = value == 0 ? null : value - 1;
            OnPropertyChanged();
            changed();
        }
    }
}
