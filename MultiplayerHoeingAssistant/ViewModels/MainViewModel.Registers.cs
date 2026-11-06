using System.IO;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private RegisterBankService? _registerBank;
    private readonly object _registerBankGate = new();

    internal RegisterBankService Registers
    {
        get
        {
            lock (_registerBankGate)
            {
                ObjectDisposedException.ThrowIf(_disposing, this);
                return _registerBank ??= new RegisterBankService(
                    Path.Combine(AssistantDataDirectory.Root, "mistletoe-registers.json"), log: AddLog);
            }
        }
    }

    private void DisposeRegisters()
    {
        lock (_registerBankGate) { _registerBank?.Dispose(); _registerBank = null; }
    }
}
