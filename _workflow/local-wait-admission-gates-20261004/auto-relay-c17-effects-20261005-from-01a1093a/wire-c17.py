from edit_c17_support import edit,snap,base
import json
edit('BetterGenshinImpact/Service/ExternalInterface/ExternalInterfaceProtocol.cs','["terminal.completionAction"] = true,','["terminal.completionAction"] = true,\n                ["terminal.effect.receipt.v1"] = true,')
edit('BetterGenshinImpact/Service/ExternalInterface/ExternalInterfacePrerequisitePlane.cs','''            case "closeSoftware":
                await Application.Current.Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
                break;
            case "closeGameAndSoftware":
                SystemControl.CloseGame();
                await Application.Current.Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
                break;
            case "shutdown":
                SystemControl.CloseGame();
                SystemControl.Shutdown();
                break;''','''            case "closeSoftware":
            case "closeGameAndSoftware":
            case "shutdown":
                var progress = TerminalEffectDispatch.Prepare(data, handle);
                var gameExited = action == "closeSoftware";
                if (!gameExited)
                {
                    SystemControl.CloseGame();
                    gameExited = await ConfirmGameProcessExitedAsync(ct);
                    if (!gameExited) return "game_exit_unconfirmed:" + action;
                }
                ct.ThrowIfCancellationRequested();
                if (action == "shutdown")
                {
                    if (progress is null) SystemControl.Shutdown();
                    else await TerminalEffectDispatch.RequestShutdownAsync(progress, ct);
                }
                else
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (progress is not null) TerminalEffectDispatch.RecordRequest(progress, "software_exit_requested", gameExited);
                        ct.ThrowIfCancellationRequested();
                        Application.Current.Shutdown();
                    });
                }
                break;''')
edit('MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs','''public sealed class PendingCompletionRecord
{''','''public sealed class PendingCompletionRecord
{
    [JsonPropertyName("terminalEffectToken")] public string? TerminalEffectToken { get; set; }
    [JsonPropertyName("terminalRequestFingerprint")] public string? TerminalRequestFingerprint { get; set; }
    [JsonPropertyName("terminalEffectProofJson")] public string? TerminalEffectProofJson { get; set; }''')
edit('MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowObservationPersistence.cs','''string? Ticket, string Expires, int StrategyIndex, string Kind, string? AccountKey, string? ActionId, string? Action)''','''string? Ticket, string Expires, int StrategyIndex, string Kind, string? AccountKey, string? ActionId, string? Action,
        string? TerminalEffectToken = null, string? TerminalRequestFingerprint = null)''')
edit('MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowObservationPersistence.cs','''0, r.Kind, null, r.ActionId, r.Action);''','''0, r.Kind, null, r.ActionId, r.Action, r.TerminalEffectToken, r.TerminalRequestFingerprint);''')
(base/'source-wired.json').write_text(json.dumps(snap(),indent=2),encoding='utf-8')
