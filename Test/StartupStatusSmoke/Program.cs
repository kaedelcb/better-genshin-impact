using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BetterGenshinImpact.Service.Instance;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Models;

static void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}
Check(WindowsSessionIdentity.Matches(@"PC\user", "USER"), "short username case insensitive");
Check(WindowsSessionIdentity.Matches(@"PC\user", @"pc\USER"), "qualified username");
Check(!WindowsSessionIdentity.Matches(@"PC\user", @"OTHER\user"), "domain mismatch");
Check(!WindowsSessionIdentity.Matches(@"PC\user", ""), "empty username rejected");
var old = JsonSerializer.Deserialize<StartupStep>("{\"kind\":\"bgiTaskRunning\"}")!;
Check(old.StatusSource == StartupStatusSource.CurrentSession && old.StatusTargetOrder == 1, "legacy JSON defaults");
var configured = new StartupStep { StatusSource = StartupStatusSource.UserName, StatusTargetUser = @"PC\user" };
Check(JsonSerializer.Deserialize<StartupStep>(JsonSerializer.Serialize(configured))!.StatusTargetUser == configured.StatusTargetUser, "source JSON round trip");
using var self = System.Diagnostics.Process.GetCurrentProcess();
Check(!string.IsNullOrEmpty(WindowsSessionIdentity.GetUserName(self.SessionId)), "query current Windows session identity");
Check(WindowsSessionIdentity.GetSessionIds().Contains(self.SessionId), "enumerate current session");
Check(WindowsSessionIdentity.GetLogonTime(self.SessionId) > 0, "read login lifetime");
var bindings = new StartupSessionBindings();
var a = new SessionLifetime(1, 100);
var b = new SessionLifetime(2, 200);
Check(bindings.Resolve(2, new[] { a, b }) == b, "initial startup order");
Check(bindings.Resolve(2, new[] { b }) == b, "exit never shifts existing slot");
Check(bindings.Resolve(1, new[] { b, a }) == a, "BGI restart does not reorder sessions");
var reused = new SessionLifetime(1, 300);
Check(bindings.Resolve(1, new[] { reused, b }) == a, "reused session id does not replace original lifetime");
Check(bindings.Resolve(3, new[] { reused, b }) == reused, "new login gets new slot");
var pipeName = "BetterGI.status-test-" + Guid.NewGuid().ToString("N");
await using var server = new ReadOnlyStatusPipe(pipeName, () => new { running = true, taskName = "sample" });
server.Start();
async Task<string?> Query(bool split)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await client.ConnectAsync(timeout.Token);
    foreach (var part in split ? new[] { "GET_", "STATUS\n" } : new[] { "GET_STATUS\n" })
        await client.WriteAsync(Encoding.UTF8.GetBytes(part), timeout.Token);
    using var reader = new StreamReader(client);
    return await reader.ReadLineAsync(timeout.Token);
}
Check(JsonDocument.Parse((await Query(true))!).RootElement.GetProperty("running").GetBoolean(), "fragmented request");
using (var stalled = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
{
    await stalled.ConnectAsync(5000);
    var buffer = new byte[1];
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Check(await stalled.ReadAsync(buffer, timeout.Token) == 0, "idle client evicted by deadline");
}
Check((await Query(false))!.Contains("sample"), "listener recovers after timeout");
var actions = 0;
var runner = new StartupFlowRunner((_, _) => { actions++; return Task.FromResult(new CommandResult { Status = "success" }); },
    () => Task.CompletedTask, _ => {}, (_, _) => Task.FromResult((true, "confirmed")), _ => {},
    () => new ControlStatus { TaskRunning = true, CurrentTaskName = "local" }, _ => {},
    (_, _) => Task.FromResult<ControlStatus?>(null));
var remote = new StartupStep { Kind = StartupStepKinds.BgiTaskRunning, NodeType = "condition", StatusSource = StartupStatusSource.StartupOrder, ExpectRunning = false };
Check((await runner.ReadConditionAsync(remote, CancellationToken.None)).passed == null, "failed remote query is unknown not idle");
remote.TrueSteps.Add(new StartupStep { Kind = StartupStepKinds.StartBgi });
remote.FalseSteps.Add(new StartupStep { Kind = StartupStepKinds.StartBgi });
await runner.RunAsync(new[] { remote, new StartupStep { Kind = StartupStepKinds.StartBgi } }, CancellationToken.None);
Check(actions == 0, "unknown terminates flow without either branch or following actions");
Check((await runner.ReadConditionAsync(new StartupStep { Kind = StartupStepKinds.BgiTaskName, TaskName = "local" }, CancellationToken.None)).passed == true, "local snapshot behavior preserved");
using var canceled = new CancellationTokenSource();
canceled.Cancel();
try { await runner.ReadConditionAsync(remote, canceled.Token); throw new Exception("cancellation ignored"); }
catch (OperationCanceledException) { Console.WriteLine("PASS condition cancellation"); }
Console.WriteLine("All startup status smoke tests passed.");
