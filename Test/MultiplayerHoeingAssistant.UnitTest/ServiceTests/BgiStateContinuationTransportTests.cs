using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>Exercises the actual SDK on a private named pipe, including frozen start payloads and retained result frames.</summary>
[Collection("TaskCenterPipeTransport")]
public sealed class BgiStateContinuationTransportTests : IAsyncLifetime
{
    private readonly BgiInstancePipeDouble _pipe = new() { StateContinuationForTest = true };
    private readonly DateTimeOffset _expiry = DateTimeOffset.UtcNow.AddHours(1);
    private string? _previousPipe;
    private BgiExternalClient _client = null!;
    private BgiEpoch Epoch => _client.ServerEpoch!;

    public async Task InitializeAsync()
    {
        _previousPipe = BgiExternalClient.PipeNameOverrideForTest;
        BgiExternalClient.PipeNameOverrideForTest = _pipe.PipeName;
        _pipe.Start();
        _client = new BgiExternalClient();
        Assert.Equal(BgiExternalLinkState.Ready, await _client.StartAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(_client.HasCapability(BgiExternalClient.StateContinuationCapability));
        Assert.NotNull(_client.ServerEpoch);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _pipe.DisposeAsync();
        BgiExternalClient.PipeNameOverrideForTest = _previousPipe;
    }

    private object Status(long stopVersion) => new
    {
        running = false, slotOccupied = false, stopVersion,
        bgiEpoch = new { processId = Epoch.ProcessId, startTicksUtc = Epoch.StartTicksUtc }
    };

    private Task<BgiTaskSubmitResult> Start(string key, string config = "收尾任务", string batch = "batch-A",
        IReadOnlyList<string>? participants = null, long? expectedStopVersion = null, BgiEpoch? targetEpoch = null)
    {
        var original = targetEpoch ?? Epoch;
        return _client.SubmitTaskStartAsync(null, config, 0, 43,
            idempotencyKey: key, expectedConfigRevision: "revision-frozen",
            bgiEpoch: new { processId = original.ProcessId, startTicksUtc = original.StartTicksUtc },
            expiresAtUtc: _expiry, coordinatedHoeing: true, hoeingBatchId: batch,
            hoeingParticipantUids: participants ?? ["100000001", "100000002"], retainUntilAcknowledged: true,
            expectedStopVersion: expectedStopVersion);
    }

    private JsonElement[] Starts() => _pipe.Received.Where(r => r.Operation == "ext.task.start")
        .Select(r => r.Data!.Value).ToArray();

    [Fact]
    public async Task PreparedRecoveryRecreatesLostSignalOnlyFromOriginalProcessAndFrozenStopFence()
    {
        var original = new OnlineHoeingBatch(new("prepared-signal", 9, 0, 0, 1, "preparing", "")
        {
            Protocol = "hoeing.start.v2", PriorityRecovery = true, StateContinuation = true,
            ParticipantUids = ["self"], Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
                { ["self"] = [new("original", "group")] }
        }, "self", new()) { ExecutionEpoch = Epoch, StateContinuationStopVersion = 7, Prepared = true };
        original.EnsureRecoveryStopSignal();
        var snapshot = original.CaptureRecovery();
        original.RecoveryStopSignal!.Dispose(); // Prepared, no BGI holder yet: the old assistant has exited.
        var restored = OnlineHoeingBatch.Restore(snapshot);
        Assert.Null(restored.RecoveryStopSignal);
        Assert.Throws<IOException>(restored.EnsureRecoveryStopSignal);
        _pipe.BodyForTest = (op, _) => op == "ext.task.status" ? (true, null, null, Status(7)) : null;
        await _client.EnsureCoordinatedRecoveryStopAsync(restored, CancellationToken.None);
        Assert.NotNull(restored.RecoveryStopSignal);
        Assert.NotEqual(snapshot.RecoveryStopSignalName, restored.RecoveryStopSignal!.Name);
        using var bgiHolder = Mistletoe.Shared.CoordinatedStopSignal.Open(restored.RecoveryStopSignal.Name);
        Assert.NotNull(bgiHolder);
        var name = restored.RecoveryStopSignal.Name;
        restored.RecoveryStopSignal.Dispose();
        using var afterExit = Mistletoe.Shared.CoordinatedStopSignal.Open(name);
        Assert.NotNull(afterExit);
        var stopped = OnlineHoeingBatch.Restore(snapshot);
        _pipe.BodyForTest = (op, _) => op == "ext.task.status" ? (true, null, null, Status(8)) : null;
        await Assert.ThrowsAsync<BgiStateContinuationStoppedException>(() =>
            _client.EnsureCoordinatedRecoveryStopAsync(stopped, CancellationToken.None));
        Assert.Null(stopped.RecoveryStopSignal);
    }

    [Fact]
    public async Task RestoredReleasedCompletionObservesLiveJob_ThenRetriesWithoutOldTicket()
    {
        var oldJob = Guid.NewGuid().ToString("N");
        var newJob = Guid.NewGuid().ToString("N");
        var starts = 0; var oldReads = 0;
        var original = new OnlineHoeingBatch(new("restored-completion", 9, 0, 0, 1, "completed", "")
        {
            Protocol = "hoeing.start.v2", PriorityRecovery = true, StateContinuation = true,
            ParticipantUids = ["self"], Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
                { ["self"] = [new("original", "group")] }
        }, "self", new() { Policy = Models.TaskConflictPolicy.RunSpecified, SpecifiedTaskName = "after" })
        {
            StateContinuationEnabled = true, ExecutionEpoch = Epoch, StateContinuationStopVersion = 0,
            TakeoverTicket = "released-ticket", PolicyTicketReleased = true, PolicyStartAttempted = true,
            PolicySubmitPayload = new Dictionary<string, object?> { ["name"] = "after", ["oneDragon"] = false, ["revision"] = "revision" }
        };
        original.MarkLocalSuccess(Epoch);
        original.RecordPolicyExecution(new(oldJob, Epoch, true));
        var batch = OnlineHoeingBatch.Restore(original.CaptureRecovery());
        var executor = new CommandExecutor(null!, "unused", () => _client);
        executor.RestoreCoordinatedTakeover(batch);
        Assert.Null(_client.TakeoverTicket);
        _pipe.BodyForTest = (op, data) =>
        {
            if (op == "ext.task.status") return (true, null, null, Status(0));
            if (op == "ext.task.start")
            {
                Assert.True(oldReads >= 3);
                if (data!.Value.TryGetProperty("takeoverTicket", out var ticket) && ticket.ValueKind != JsonValueKind.Null)
                    return (false, "takeover_conflict", "released ticket", null);
                starts++;
                return (true, null, null, new { status = "queued", taskHandle = newJob });
            }
            if (op == "ext.task.queueStatus")
            {
                if (starts == 0) oldReads++;
                return (true, null, null, new { taskHandle = starts == 0 ? oldJob : newJob,
                    idempotencyKey = batch.PolicyRequestKey, status = starts == 0 ? oldReads < 3 ? "running" : "failed" : "completed",
                    executionExitConfirmed = starts > 0 || oldReads >= 3,
                    bgiEpoch = new { processId = Epoch.ProcessId, startTicksUtc = Epoch.StartTicksUtc } });
            }
            if (op == "ext.task.acknowledge") return (true, null, null, new { status = "acknowledged" });
            return null;
        };
        await ViewModels.MainViewModel.RunCompletionContinuationAsync(batch, new()
        {
            RequireAuthority = () => { }, RetainedResult = true, Prepare = _ => Task.FromResult(true),
            Start = ct => executor.StartRetainedCompletionActionAsync(batch, ct),
            Query = ct => _client.QueryTaskQueueStatusByKeyAsync(batch.PolicyRequestKey, ct),
            Reconcile = _ => Task.FromResult(true),
            Acknowledge = (result, ct) => _client.AcknowledgeTaskResultAsync(result.IdempotencyKey!, result.TaskHandle!, result.Epoch!, ct),
            Delay = ct => Task.Delay(1, ct)
        }, batch.Cts.Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, starts);
        Assert.Equal(newJob, batch.PolicyExecution!.JobId);
        Assert.Single(batch.FailedPolicyAttempts);
        Assert.Null(executor.TakeoverTicketSnapshot);
    }

    [Fact]
    public async Task RecoveryCheckpointTravelsOnlyOnObservation_NotOnFrozenStartFingerprint()
    {
        _pipe.BodyForTest = (op, _) => op == "ext.task.status" ? (true, null, null, Status(0)) : null;
        _client.CoordinatedRecoveryCheckpointJson = "{\"Revision\":1}";
        await _client.SendCommandAsync("ext.task.status");
        Assert.Contains(_pipe.Received, frame => frame.Operation == "ext.task.status"
            && frame.Data!.Value.TryGetProperty("assistantRecovery", out var value)
            && value.GetProperty("Revision").GetInt32() == 1);
        Assert.True((await Start("same-fingerprint")).Success);
        _client.CoordinatedRecoveryCheckpointJson = "{\"Revision\":2}";
        Assert.True((await Start("same-fingerprint")).Success);
        var starts = Starts();
        Assert.Equal(2, starts.Length);
        Assert.False(starts[0].TryGetProperty("assistantRecovery", out _));
        Assert.Equal(starts[0].GetRawText(), starts[1].GetRawText());
    }

    [Fact]
    public async Task RepeatedKeyKeepsOriginalStopFenceAndTicketInsteadOfAdoptingANewerStopState()
    {
        var stopVersion = 7L;
        var statusReads = 0;
        _pipe.BodyForTest = (operation, _) =>
        {
            if (operation != "ext.task.status") return null;
            Interlocked.Increment(ref statusReads);
            return (true, null, null, Status(Volatile.Read(ref stopVersion)));
        };
        _client.TakeoverTicket = "original-ticket";
        Assert.True((await Start("same-start")).Success);
        Volatile.Write(ref stopVersion, 8);
        _client.TakeoverTicket = "later-ticket";
        Assert.True((await Start("same-start")).Success);

        var frames = Starts();
        Assert.Equal(2, frames.Length);
        Assert.Equal(1, statusReads);
        foreach (var frame in frames)
        {
            Assert.Equal(7, frame.GetProperty("expectedStopVersion").GetInt64());
            Assert.Equal("original-ticket", frame.GetProperty("takeoverTicket").GetString());
            Assert.Equal("same-start", frame.GetProperty("idempotencyKey").GetString());
            Assert.True(frame.GetProperty("retainUntilAcknowledged").GetBoolean());
        }
        Assert.Equal(frames[0].GetRawText(), frames[1].GetRawText());
    }

    [Fact]
    public async Task FailedFirstStopStateReadSendsNoStartAndCanRetryTheOriginalRequestLater()
    {
        var reads = 0;
        _pipe.BodyForTest = (operation, _) =>
        {
            if (operation != "ext.task.status") return null;
            return Interlocked.Increment(ref reads) == 1
                ? (false, "temporarily_unavailable", "status reply unavailable", null)
                : (true, null, null, Status(9));
        };
        var error = await Assert.ThrowsAsync<BgiNotSentException>(() => Start("status-retry"));
        Assert.Equal(BgiNotSentException.ChannelNotReady, error.EvidenceCode);
        Assert.Empty(Starts());
        Assert.True((await Start("status-retry")).Success);
        var frame = Assert.Single(Starts());
        Assert.Equal("status-retry", frame.GetProperty("idempotencyKey").GetString());
        Assert.Equal(9, frame.GetProperty("expectedStopVersion").GetInt64());
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("batch")]
    [InlineData("participants")]
    public async Task SameKeyCannotSendChangedTaskOrCoordinatedIdentity(string changed)
    {
        _pipe.BodyForTest = (operation, _) => operation == "ext.task.status"
            ? (true, null, null, Status(7)) : null;
        Assert.True((await Start("frozen-content")).Success);

        var error = await Assert.ThrowsAsync<BgiNotSentException>(() => Start("frozen-content",
            config: changed == "task" ? "另外一个任务" : "收尾任务",
            batch: changed == "batch" ? "batch-B" : "batch-A",
            participants: changed == "participants" ? ["100000001", "100000003"] : null));
        Assert.Equal(BgiNotSentException.LocalRequestRejected, error.EvidenceCode);
        Assert.Single(Starts());
        Assert.Equal(1, _pipe.CountOf("ext.task.status"));
    }

    [Fact]
    public async Task ANewKeyAfterManualStopCannotReplaceTheBatchsOriginalStopFence()
    {
        var currentFence = 7L;
        _pipe.BodyForTest = (operation, _) => operation == "ext.task.status"
            ? (true, null, null, Status(Volatile.Read(ref currentFence))) : null;
        Assert.True((await Start("original-item", expectedStopVersion: 7)).Success);
        Volatile.Write(ref currentFence, 8);
        await Assert.ThrowsAsync<BgiStateContinuationStoppedException>(() => Start("next-item", expectedStopVersion: 7));
        Assert.Single(Starts());
        Assert.Equal(2, _pipe.CountOf("ext.task.status"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewBgiHandshakeCannotRetargetAnOriginalBatchRequest(bool sentBeforeReconnect)
    {
        var original = Epoch;
        _pipe.BodyForTest = (operation, _) => operation == "ext.task.status" ? (true, null, null, Status(7)) : null;
        if (sentBeforeReconnect) Assert.True((await Start("original-process", expectedStopVersion: 7, targetEpoch: original)).Success);
        var initialStarts = Starts().Length;
        var initialStatusReads = _pipe.CountOf("ext.task.status");
        _client.Dispose();
        using var replacement = await ReplacementBgiPipe.StartAsync();
        BgiExternalClient.PipeNameOverrideForTest = replacement.PipeName;
        _client = new BgiExternalClient();
        try
        {
            Assert.Equal(BgiExternalLinkState.Ready, await _client.StartAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotEqual(original.ProcessId, Epoch.ProcessId);
            Assert.Equal(replacement.ProcessId, Epoch.ProcessId);
            Assert.Equal(replacement.StartTicksUtc, Epoch.StartTicksUtc);
            await Assert.ThrowsAsync<BgiNotSentException>(() => Start("original-process", expectedStopVersion: 7, targetEpoch: original));
            Assert.Equal(initialStarts, Starts().Length);
            Assert.Equal(initialStatusReads, _pipe.CountOf("ext.task.status"));
            Assert.Equal(0, replacement.CountOf("ext.task.start"));
            Assert.Equal(0, replacement.CountOf("ext.task.status"));
        }
        finally
        {
            _client.Dispose();
            BgiExternalClient.PipeNameOverrideForTest = _pipe.PipeName;
        }
    }

    [Fact]
    public async Task AStatusReplyFromADifferentProcessCannotSupplyTheOriginalRequestsStopFence()
    {
        var original = Epoch;
        _pipe.BodyForTest = (operation, _) => operation == "ext.task.status" ? (true, null, null, new
        {
            stopVersion = 7L,
            bgiEpoch = new { processId = original.ProcessId, startTicksUtc = original.StartTicksUtc + 1 }
        }) : null;
        await Assert.ThrowsAsync<BgiNotSentException>(() => Start("status-process-change", expectedStopVersion: 7, targetEpoch: original));
        Assert.Empty(Starts());
        Assert.Equal(1, _pipe.CountOf("ext.task.status"));
    }

    [Fact]
    public async Task QueueLookupAndAcknowledgementCarryTheExactOriginalKeyHandleAndProcess()
    {
        const string key = "post-hoeing/batch-A/shark";
        var job = Guid.NewGuid().ToString("N");
        _pipe.BodyForTest = (operation, data) => operation switch
        {
            "ext.task.queueStatus" => (true, null, null, new
            {
                status = "completed", taskHandle = job, idempotencyKey = key, executionExitConfirmed = true,
                bgiEpoch = new { processId = Epoch.ProcessId, startTicksUtc = Epoch.StartTicksUtc },
                hoeingEvidence = new { started = 1, completed = 1, active = 0, @sealed = true, jobResult = "Ran" }
            }),
            "ext.task.acknowledge" => (true, null, null, new { status = "acknowledged" }),
            _ => null
        };

        var observed = await _client.QueryTaskQueueStatusByKeyAsync(key);
        Assert.NotNull(observed);
        Assert.Equal(job, observed.TaskHandle); Assert.Equal(key, observed.IdempotencyKey);
        Assert.Equal("completed", observed.Status); Assert.True(observed.ExecutionExitConfirmed);
        Assert.Equal(Epoch.ProcessId, observed.Epoch!.ProcessId);
        Assert.Equal(Epoch.StartTicksUtc, observed.Epoch.StartTicksUtc);
        Assert.Equal(1, observed.HoeingEvidence!.Completed);
        var query = Assert.Single(_pipe.Received.Where(r => r.Operation == "ext.task.queueStatus")).Data!.Value;
        Assert.Equal(key, query.GetProperty("idempotencyKey").GetString());
        Assert.False(query.TryGetProperty("taskHandle", out _));

        Assert.True(await _client.AcknowledgeTaskResultAsync(key, job, observed.Epoch));
        var acknowledgement = Assert.Single(_pipe.Received.Where(r => r.Operation == "ext.task.acknowledge")).Data!.Value;
        Assert.Equal(key, acknowledgement.GetProperty("submissionKey").GetString());
        Assert.Equal(job, acknowledgement.GetProperty("taskHandle").GetString());
        Assert.Equal("ack/" + key, acknowledgement.GetProperty("idempotencyKey").GetString());
        Assert.Equal(Epoch.ProcessId, acknowledgement.GetProperty("bgiEpoch").GetProperty("processId").GetInt32());
        Assert.Equal(Epoch.StartTicksUtc, acknowledgement.GetProperty("bgiEpoch").GetProperty("startTicksUtc").GetInt64());
        Assert.Empty(Starts());
    }

    // The SDK compares hello identity with the OS pipe-server identity. A replacement therefore
    // needs a real second process, rather than fabricated start ticks on this testhost's pipe.
    private sealed class ReplacementBgiPipe : IDisposable
    {
        private readonly Process _process;
        private readonly ConcurrentQueue<string> _operations = new();
        private readonly Task<string> _errors;
        private Task? _readOperations;
        internal string PipeName { get; }
        internal int ProcessId { get; }
        internal long StartTicksUtc { get; }

        private ReplacementBgiPipe(string pipeName, Process process)
        {
            PipeName = pipeName; _process = process;
            ProcessId = process.Id; StartTicksUtc = process.StartTime.ToUniversalTime().Ticks;
            _errors = process.StandardError.ReadToEndAsync();
        }

        internal int CountOf(string operation) => _operations.Count(value => value == operation);

        internal static async Task<ReplacementBgiPipe> StartAsync()
        {
            var name = "Codex.StateContinuationReplacement." + Guid.NewGuid().ToString("N");
            var script = """
                $ErrorActionPreference = 'Stop'
                $server = [System.IO.Pipes.NamedPipeServerStream]::new('PIPE_NAME_TOKEN', [System.IO.Pipes.PipeDirection]::InOut, 1)
                try {
                    [Console]::WriteLine('READY')
                    [Console]::Out.Flush()
                    $server.WaitForConnection()
                    $process = [System.Diagnostics.Process]::GetCurrentProcess()
                    $epoch = @{ processId = $process.Id; startTicksUtc = $process.StartTime.ToUniversalTime().Ticks }
                    function Read-Exactly([byte[]] $buffer) {
                        $offset = 0
                        while ($offset -lt $buffer.Length) {
                            $count = $server.Read($buffer, $offset, $buffer.Length - $offset)
                            if ($count -eq 0) { throw 'Private client disconnected' }
                            $offset += $count
                        }
                    }
                    while ($server.IsConnected) {
                        $prefix = [byte[]]::new(4)
                        Read-Exactly $prefix
                        $length = [BitConverter]::ToInt32($prefix, 0)
                        if ($length -lt 1 -or $length -gt 1048576) { throw 'Invalid private frame length' }
                        if ($server.ReadByte() -ne 1) { throw 'Invalid private payload kind' }
                        $body = [byte[]]::new($length)
                        Read-Exactly $body
                        $request = [Text.Encoding]::UTF8.GetString($body) | ConvertFrom-Json
                        $operation = [string] $request.operation
                        [Console]::WriteLine($operation)
                        [Console]::Out.Flush()
                        if ($operation -eq 'ext.hello') {
                            $data = @{
                                bgiVersion = 'replacement-fixture'; sessionId = 'replacement-private-session'
                                windowsSessionId = $process.SessionId; processId = $process.Id; bgiEpoch = $epoch
                                capabilities = @{ 'event.push' = $true; 'task.queue' = $true; 'task.stateContinuation.v1' = $true }
                            }
                        } elseif ($operation -eq 'ext.task.status') {
                            $data = @{ running = $false; slotOccupied = $false; stopVersion = 7; bgiEpoch = $epoch }
                        } elseif ($operation -eq 'ext.task.start') {
                            $data = @{ status = 'queued'; taskHandle = [Guid]::NewGuid().ToString('N'); queuePosition = 0 }
                        } else { $data = @{} }
                        $response = @{ version = 2; requestId = $request.requestId; operation = 'response'; success = $true; data = $data }
                        $json = $response | ConvertTo-Json -Depth 12 -Compress
                        $bytes = [Text.Encoding]::UTF8.GetBytes($json)
                        $header = [BitConverter]::GetBytes([int] $bytes.Length)
                        $server.Write($header, 0, $header.Length)
                        $server.WriteByte(1)
                        $server.Write($bytes, 0, $bytes.Length)
                        $server.Flush()
                    }
                } finally { $server.Dispose() }
                """.Replace("PIPE_NAME_TOKEN", name, StringComparison.Ordinal);
            var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            var helper = new ReplacementBgiPipe(name, Process.Start(start)
                ?? throw new InvalidOperationException("Cannot start the owned private pipe fixture"));
            try
            {
                var ready = await helper._process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("READY", ready);
                helper._readOperations = Task.Run(async () =>
                {
                    while (await helper._process.StandardOutput.ReadLineAsync() is { } operation)
                        helper._operations.Enqueue(operation);
                });
                return helper;
            }
            catch { helper.Dispose(); throw; }
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    Assert.Equal(ProcessId, _process.Id);
                    Assert.Equal(StartTicksUtc, _process.StartTime.ToUniversalTime().Ticks);
                    try { _process.Kill(); }
                    catch (InvalidOperationException) when (_process.HasExited) { }
                    catch (System.ComponentModel.Win32Exception) when (_process.HasExited) { }
                }
                Assert.True(_process.WaitForExit(5000), "Owned replacement pipe process must exit");
                _readOperations?.GetAwaiter().GetResult();
                _ = _errors.GetAwaiter().GetResult();
            }
            finally { _process.Dispose(); }
        }
    }
}
