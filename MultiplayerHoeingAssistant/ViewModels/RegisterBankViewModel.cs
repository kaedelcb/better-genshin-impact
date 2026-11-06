using System.Collections.ObjectModel;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed class RegisterBankViewModel : ViewModelBase
{
    private readonly Func<RegisterConfigurationChange, Task<RegisterBankSnapshot>> _save;
    private readonly Func<int?, Task<RegisterBankSnapshot>>? _reset;
    private readonly Func<int, Task<RegisterBankSnapshot>>? _setOne;
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanReset));
            OnPropertyChanged(nameof(CanSetOne));
        }
    }
    public bool CanReset => _reset is not null && !IsBusy && Cells.All(c => c.Available);
    public bool CanSetOne => _setOne is not null && !IsBusy && Cells.All(c => c.Available);
    private string _setOneStatus = "";
    public string SetOneStatus { get => _setOneStatus; private set => SetProperty(ref _setOneStatus, value); }
    private string _resetStatus = "确认后将 R1–R10 全部归零，保留名称和自动重置规则。";
    public string ResetStatus { get => _resetStatus; private set => SetProperty(ref _resetStatus, value); }
    public ObservableCollection<RegisterCellViewModel> Cells { get; } = new(
        Enumerable.Range(1, RegisterBank.Count).Select(i => new RegisterCellViewModel(i)));

    public RegisterBankViewModel(Func<RegisterConfigurationChange, Task<RegisterBankSnapshot>> save,
        Func<int?, Task<RegisterBankSnapshot>>? reset = null,
        Func<int, Task<RegisterBankSnapshot>>? setOne = null) { _save = save; _reset = reset; _setOne = setOne; }

    public void Apply(RegisterBankSnapshot? snapshot)
    {
        var valid = snapshot?.Available == true && snapshot.Entries is { Count: RegisterBank.Count }
            && snapshot.Entries.Select((e, i) => e is not null && e.Number == i + 1 && e.Value is 0 or 1).All(x => x);
        for (var i = 0; i < Cells.Count; i++)
            Cells[i].Apply(valid ? snapshot!.Entries[i] : null,
                snapshot?.Error is { Length: > 0 } error ? error : "执行端尚未提供寄存器状态，请检查连接或版本。");
        OnPropertyChanged(nameof(CanReset));
        OnPropertyChanged(nameof(CanSetOne));
    }

    public async Task SaveAsync(RegisterConfigurationChange change)
    {
        if (IsBusy) throw new InvalidOperationException("寄存器操作正在执行，请等待完成。");
        IsBusy = true;
        try
        {
            var snapshot = await _save(change);
            Apply(snapshot);
            if (!snapshot.Available) throw new InvalidOperationException(snapshot.Error);
        }
        finally { IsBusy = false; }
    }

    public async Task ResetAsync(int? number = null)
    {
        if (number is < 1 or > RegisterBank.Count) throw new ArgumentOutOfRangeException(nameof(number));
        if (_reset is null) throw new InvalidOperationException("当前执行端未提供手动归零，请检查连接或版本。");
        if (!CanReset) throw new InvalidOperationException(IsBusy ? "寄存器操作正在执行，请等待完成。" : "寄存器状态尚未确认，不能归零。");
        IsBusy = true;
        try
        {
            ResetStatus = "正在执行归零，请等待执行端确认。";
            var snapshot = await _reset(number);
            Apply(snapshot);
            if (!snapshot.Available || Cells.Any(c => !c.Available))
                throw new InvalidOperationException(snapshot.Error is { Length: > 0 } error ? error : "重置结果未确认，请读取执行端最新状态。");
            var targets = Cells.Where(c => number is null || c.Entry.Number == number).ToArray();
            ResetStatus = targets.All(c => c.Entry.Value == 0)
                ? number is null ? "R1–R10 已全部归零。" : $"R{number} 已归零。"
                : "手动归零已确认，随后状态发生更新；以最新状态为准。";
        }
        catch (Exception ex) { ResetStatus = "归零未确认：" + ex.Message; throw; }
        finally { IsBusy = false; }
    }

    public async Task SetOneAsync(int number)
    {
        if (number is < 1 or > RegisterBank.Count) throw new ArgumentOutOfRangeException(nameof(number));
        if (_setOne is null) throw new InvalidOperationException("当前执行端未提供手动归一，请检查连接或版本。");
        if (!CanSetOne) throw new InvalidOperationException(IsBusy ? "寄存器操作正在执行，请等待完成。" : "寄存器状态尚未确认，不能归一。");
        IsBusy = true;
        try
        {
            SetOneStatus = "正在执行归一，请等待执行端确认。";
            var snapshot = await _setOne(number);
            Apply(snapshot);
            if (!snapshot.Available || Cells.Any(c => !c.Available))
                throw new InvalidOperationException(snapshot.Error is { Length: > 0 } error ? error : "归一结果未确认，请读取执行端最新状态。");
            SetOneStatus = Cells[number - 1].Entry.Value == 1 ? $"R{number} 已归一。"
                : "手动归一已确认，随后状态发生更新；以最新状态为准。";
        }
        catch (Exception ex) { SetOneStatus = "归一未确认：" + ex.Message; throw; }
        finally { IsBusy = false; }
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
