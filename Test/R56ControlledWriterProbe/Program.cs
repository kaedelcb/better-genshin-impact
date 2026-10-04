using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Text.Json;
using MultiplayerHoeingAssistant.Services;

if (args.Length == 5 && args[0] == "--runstore-publish")
{
    return MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.RunStoreProcessFenceTests.Actor(args[1], args[2], args[3], args[4]);
}

if (args.Length == 6 && args[0] == "--migration-fault")
{
    void FaultLog(string state) => File.AppendAllText(args[5], JsonSerializer.Serialize(new
    { state, pid = Environment.ProcessId, transaction = args[3], utc = DateTimeOffset.UtcNow }) + Environment.NewLine);
    using var transaction = new MigrationSwitchTransaction(args[1], args[2], requireQuiescence: false,
        effectService: new WorkflowFileMigrationEffectService(), referenceWriteFault: station =>
        {
            if (station != args[4]) return;
            FaultLog("reached:" + station);
            Environment.Exit(71);
        });
    if (!transaction.BeginTransaction(args[3]).Success || !transaction.TakeSnapshot().Success ||
        !transaction.RecordChanges([new ChangeRecord { Path = "flows/plan.flow.json", Kind = ChangeKind.Modified }]).Success) return 3;
    var reference = transaction.ApplyReferenceUpdate(new([new("flows/plan.flow.json", ChangeKind.Modified,
        RenameFrom: "old", RenameTo: "new")]));
    if (!reference.Success) return 4;
    if (args[4].StartsWith("activate:") || args[4].StartsWith("undo:") || args[4].StartsWith("restore:"))
    {
        var manifest = transaction.LoadValidated()!;
        var activation = transaction.ActivateCandidate(new("flows/plan.flow.json", "candidate-ready", "active",
            manifest.ReferenceWriteSet["flows/plan.flow.json"]));
        if (!activation.Success) return 5;
        if (args[4].StartsWith("undo:") || args[4].StartsWith("restore:")) transaction.Rollback();
    }
    FaultLog("requested-station-not-reached");
    return 6;
}

if (args.Length != 4) return 2;
void Log(string state) => File.AppendAllText(args[3], JsonSerializer.Serialize(new
{ state, actor = args[2], pid = Environment.ProcessId, utc = DateTimeOffset.UtcNow }) + Environment.NewLine);
Log("started");
var timer = Stopwatch.StartNew();
var blocked = false;
while (timer.Elapsed < TimeSpan.FromSeconds(15))
{
    MigrationRootAuthority authority;
    try { authority = new(args[0], args[1]); }
    catch (IOException)
    {
        if (!blocked) { Log("blocked"); blocked = true; }
        Thread.Sleep(20);
        continue;
    }
    using (authority)
    {
        Log("acquired");
        File.WriteAllText(Path.Combine(args[0], "a.json"), args[2]);
        Thread.Sleep(30);
        File.WriteAllText(Path.Combine(args[0], "b.json"), args[2]);
        Log("pair-written");
    }
    return 0;
}
Log("timeout");
return 1;
