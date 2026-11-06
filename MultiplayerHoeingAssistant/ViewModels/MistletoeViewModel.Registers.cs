using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class MistletoeViewModel
{
    public RegisterBankViewModel Registers { get; private set; } = null!;
    private RegisterBankService? _localRegisterService;
    internal void ReportUnsupportedRegisters() => StatusText = "当前执行端不支持寄存器节点，未添加；请使用支持寄存器的助手版本。";
    private bool _remoteSupportsRegisters;
    internal bool CanUseRegisters => !IsRemoteMode || _remoteSupportsRegisters;
    private static bool ContainsRegisterNodes(IEnumerable<StartupStep> steps) => steps.Any(step =>
        step.Kind is StartupStepKinds.SetRegister or StartupStepKinds.JudgeRegister
        || ContainsRegisterNodes(step.TrueSteps) || ContainsRegisterNodes(step.FalseSteps) || ContainsRegisterNodes(step.FireSteps));

    private void InitializeRegisters(RegisterBankSnapshot? remoteSnapshot)
    {
        _remoteSupportsRegisters = remoteSnapshot is not null;
        if (!IsRemoteMode) _localRegisterService = _mainVm.Registers;
        Registers = new RegisterBankViewModel(async change =>
        {
            if (_remoteTarget is null) return _localRegisterService!.Configure(change);
            var reply = await _mainVm.RequestRemoteCenterAsync(_remoteTarget, "registers.configure", change);
            if (reply.Status != "ok" || string.IsNullOrEmpty(reply.PayloadJson))
                throw new InvalidOperationException(reply.Status == "unknown"
                    ? "保存结果未确认，请等待读取执行端最新状态后重新打开。" + reply.Message : reply.Message);
            return JsonSerializer.Deserialize<RegisterBankSnapshot>(reply.PayloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("执行端寄存器回复为空，结果未确认。");
        }, async number =>
        {
            if (_remoteTarget is null) return _localRegisterService!.Reset(number);
            var reply = await _mainVm.RequestRemoteCenterAsync(_remoteTarget, "registers.reset",
                new RegisterResetRequest(number, All: number is null));
            if (reply.Status != "ok" || string.IsNullOrEmpty(reply.PayloadJson))
                throw new InvalidOperationException(reply.Status == "unknown"
                    ? "手动归零结果未确认，请读取执行端最新状态后再操作。" + reply.Message : reply.Message);
            return JsonSerializer.Deserialize<RegisterBankSnapshot>(reply.PayloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("执行端未提供归零后的状态，结果未确认。");
        });
        Registers.Apply(IsRemoteMode ? remoteSnapshot : _localRegisterService!.GetSnapshot());
    }
}

public sealed partial class StartupStepViewModel
{
    public bool IsRegisterNode => Kind is StartupStepKinds.SetRegister or StartupStepKinds.JudgeRegister;
    public bool IsRegisterCondition => Kind == StartupStepKinds.JudgeRegister;
    public IReadOnlyList<RegisterSelectionRowViewModel> RegisterRows { get; private set; } = [];
    public int RegisterMatchIndex
    {
        get => Model.RegisterCondition?.Match == "any" ? 1 : 0;
        set
        {
            if (Model.RegisterCondition is null || value is < 0 or > 1) return;
            Model.RegisterCondition.Match = value == 1 ? "any" : "all";
            Changed();
        }
    }

    private void InitializeRegisterRows(RegisterBankViewModel bank)
    {
        if (!IsRegisterNode) return;
        var values = IsRegisterCondition
            ? (Model.RegisterCondition ??= new()).Values
            : (Model.RegisterWrite ??= new()).Values;
        if (values is null || values.Count != RegisterBank.Count) return;
        RegisterRows = Enumerable.Range(0, RegisterBank.Count).Select(i =>
            new RegisterSelectionRowViewModel(bank.Cells[i], values, i, IsRegisterCondition, () => Changed())).ToArray();
    }

    internal static string RegisterSummary(IReadOnlyList<int?>? values, string separator, bool condition)
        => values is null || values.Count != RegisterBank.Count ? "寄存器参数无效"
            : values.All(v => v is null) ? condition ? "请选择判断条件（忽略的位不参与判断）" : "请选择要设置的位（其余保持）"
            : string.Join(separator, values.Select((v, i) => v is null ? null : $"R{i + 1}{(condition ? "=" : "←")}{v}")
                .Where(text => text is not null));
}
