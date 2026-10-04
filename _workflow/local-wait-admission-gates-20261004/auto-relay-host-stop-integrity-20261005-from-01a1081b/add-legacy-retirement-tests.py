from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs')
s=p.read_bytes().decode('utf-8').replace('\r\n','\n')
marker='    [Fact]\n    public async Task ParkedDriveCompletion_DoesNotStartTerminalReconciliation()'
test='''    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalStop_LegacyParkedRunWithoutNewMappingAnchorCannotClaimMissingLedgerAsNoMapping(bool reopen)
    {
        var (host, _, run) = await StartAdmissionWiredParkedRun();
        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var leasePath = Path.Combine(_root, "arbitration", "arbitration-lease.json");
        var originalLease = File.ReadAllBytes(leasePath);
        try
        {
            if (reopen) await host.ShutdownAsync();
            var legacyRun = JsonNode.Parse(File.ReadAllBytes(runPath))!.AsObject();
            Assert.True(legacyRun.Remove("admissionMappings"));
            File.WriteAllBytes(runPath, JsonSerializer.SerializeToUtf8Bytes(legacyRun));
            var lostLedger = JsonNode.Parse(File.ReadAllBytes(leasePath))!.AsObject();
            lostLedger["handoff"]!["operations"] = new JsonArray();
            lostLedger["handoff"]!["archivedOperations"] = new JsonArray();
            lostLedger["handoff"]!["preObservations"] = new JsonArray();
            File.WriteAllBytes(leasePath, JsonSerializer.SerializeToUtf8Bytes(lostLedger));
            var read = new ArbitrationLeaseStore(Path.Combine(_root, "arbitration")).Read();
            Assert.True(read.Status is ArbitrationLeaseStatus.Valid or ArbitrationLeaseStatus.Absent, read.Detail);
            if (reopen) host = MakeWaitParkingHost(admissionWired: true);
            Assert.Null(host.Runs.Load(run.RunId)!.AdmissionMappings);
            var stopped = await host.RequestRunActionAsync(run.RunId, WorkflowRunAction.Stop);
            Assert.Equal(HostActionStatus.Unavailable, stopped.Status);
            Assert.Empty(ReadAdmissionOperationsForRun(run.RunId));
        }
        finally
        {
            File.WriteAllBytes(leasePath, originalLease);
            await host.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TerminalStop_LegitimateTombstoneAndArchiveRemainOriginalMappings(bool archive, bool reopen)
    {
        var (host, _, run) = await StartAdmissionWiredParkedRun();
        try
        {
            Assert.Equal(HostActionStatus.Effective, (await host.RequestRunActionAsync(run.RunId, WorkflowRunAction.Stop)).Status);
            var leaseStore = new ArbitrationLeaseStore(Path.Combine(_root, "arbitration"));
            var read = leaseStore.Read();
            var lease = read.File!.Lease!;
            var moved = leaseStore.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
            {
                foreach (var operation in file.Handoff!.Operations.Where(o => o.RunBinding == run.RunId).ToList())
                {
                    Assert.Equal(OperationRequestState.TerminalCompleted, operation.RequestState);
                    operation.Zone = OperationZone.Tombstone;
                    operation.UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-25);
                    if (!archive) continue;
                    file.Handoff.Operations.Remove(operation);
                    file.Handoff.ArchivedOperations.Add(new ArchivedOperationRecord
                        { Operation = operation, ArchivedAtUtc = DateTimeOffset.UtcNow });
                }
                return null;
            });
            Assert.True(moved.Success, moved.Reason);
            var handoff = leaseStore.Read().File!.Handoff!;
            var retired = handoff.Operations.Concat(handoff.ArchivedOperations.Select(a => a.Operation))
                .Where(o => o.RunBinding == run.RunId).ToList();
            var originalBytes = JsonSerializer.SerializeToUtf8Bytes(retired);
            var runBytes = File.ReadAllBytes(Path.Combine(_runsDir, run.RunId + ".run.json"));
            if (reopen)
            {
                await host.ShutdownAsync();
                host = MakeWaitParkingHost(admissionWired: true);
            }
            Assert.Equal(HostActionStatus.Effective, (await host.RequestRunActionAsync(run.RunId, WorkflowRunAction.Stop)).Status);
            var after = leaseStore.Read().File!.Handoff!;
            Assert.Equal(originalBytes, JsonSerializer.SerializeToUtf8Bytes(after.Operations
                .Concat(after.ArchivedOperations.Select(a => a.Operation)).Where(o => o.RunBinding == run.RunId).ToList()));
            Assert.Equal(runBytes, File.ReadAllBytes(Path.Combine(_runsDir, run.RunId + ".run.json")));
        }
        finally { await host.ShutdownAsync(); }
    }

'''
assert s.count(marker)==1
p.write_bytes(s.replace(marker,test+marker).replace('\n','\r\n').encode('utf-8'))
