using System;
using System.IO;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.Instance;
using Mistletoe.Shared;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BetterGenshinImpact.UnitTest.ServiceTests.ExternalInterface;

public class TerminalEffectDispatchTests
{
    [Fact]
    public void SenderFingerprint_EqualsActualIpcDecodedContract_WithExpiryOffsetAndNullTicket()
    {
        var data = JObject.FromObject(new { idempotencyKey = "key", expiresAtUtc = "2026-10-05T00:22:29.8033481+00:00",
            takeoverTicket = (string?)null, terminalEffectToken = new string('a', 64), action = "shutdown" });
        var envelope = System.Text.Json.JsonSerializer.Serialize(new { operation = ExternalInterfaceOperations.TerminalCompletionAction,
            data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(data.ToString()) });
        var received = InstanceIpcProtocol.ReadJson(new InstanceIpcFrame(InstanceIpcPayloadType.Utf8Json, System.Text.Encoding.UTF8.GetBytes(envelope)));
        Assert.Equal(ExecutionRequestContract.Fingerprint(received), TerminalEffectJournal.Fingerprint(received.Operation, data));
    }

    [Fact]
    public void OriginalProgress_BindsExactContractHash_AndCannotBeReusedForAnotherAction()
    {
        var root = Path.Combine(Path.GetTempPath(), "c17-dispatch-" + Guid.NewGuid().ToString("N"));
        var data = JObject.FromObject(new { terminalEffectToken = new string('a', 64), idempotencyKey = "key",
            workflowRunId = Guid.NewGuid().ToString("N"), action = "closeSoftware", nodeId = "$flow", iteration = 0, occurrence = 0, attempt = 1 });
        var handle = Guid.NewGuid();
        var progress = TerminalEffectDispatch.Prepare(data, handle, root)!;
        Assert.Equal(handle.ToString("N"), progress.JobId);
        Assert.Equal("prepared", progress.Stage);
        Assert.Equal(ExecutionRequestContract.Fingerprint(new InstanceIpcEnvelope { Operation = ExternalInterfaceOperations.TerminalCompletionAction, Data = data }), progress.RequestFingerprint);
        Assert.Equal(progress, TerminalEffectJournal.Read<TerminalEffectProgress>(root, progress.Token));
        data["action"] = "shutdown";
        Assert.Throws<InvalidOperationException>(() => TerminalEffectDispatch.Prepare(data, handle, root));
        Assert.Equal(progress, TerminalEffectJournal.Read<TerminalEffectProgress>(root, progress.Token));
    }

    [Fact]
    public void ProgressPersistenceFailureOccursBeforeAnyDestructiveAction()
    {
        var path = Path.Combine(Path.GetTempPath(), "c17-blocked-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "original");
        var data = JObject.FromObject(new { terminalEffectToken = new string('b', 64), idempotencyKey = "key", workflowRunId = "run", action = "shutdown" });
        Assert.Throws<IOException>(() => TerminalEffectDispatch.Prepare(data, Guid.NewGuid(), path));
        Assert.Equal("original", File.ReadAllText(path));
    }
}
