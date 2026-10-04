from pathlib import Path

root = Path.cwd()
path = root / 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs'
data = path.read_bytes()
text = data.decode('utf-8')
nl = '\r\n'
needle = '    private async Task ProbeTerminalShutdownAsync(bool handoff, bool beforeSeal)'
replacement = '''    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task OriginalHost_RepeatedShutdownWaitsForSameCompleteLifecycle(bool handoff, bool beforeSeal)
        => ProbeTerminalShutdownAsync(handoff, beforeSeal, repeated: true);

    private async Task ProbeTerminalShutdownAsync(bool handoff, bool beforeSeal, bool repeated = false)'''.replace('\n', nl)
assert text.count(needle) == 1
text = text.replace(needle, replacement)
needle = '                    var returnedBeforeWriteback = shutdown.IsCompleted;'
replacement = needle + nl + '''                    var secondShutdown = repeated ? host.ShutdownAsync() : shutdown;
                    var secondReturnedBeforeWriteback = secondShutdown.IsCompleted;'''.replace('\n', nl)
assert text.count(needle) == 1
text = text.replace(needle, replacement)
needle = '                    await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));'
text = text.replace(needle, '                    await secondShutdown.WaitAsync(TimeSpan.FromSeconds(10));' + nl + needle, 1)
needle = '                    Assert.False(returnedBeforeWriteback, "Shutdown returned while original terminal writeback was held");'
text = text.replace(needle, needle + nl + '                    Assert.False(secondReturnedBeforeWriteback, "Repeated Shutdown returned while original terminal lifecycle was held");', 1)
path.write_bytes(text.encode('utf-8'))

path = root / 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs'
text = path.read_bytes().decode('utf-8')
needle = '    [Fact]\n    public async Task TerminalSeal_PublishFailureRetainsAcceptedResponsibility_ThenRetriesSameSeal()'
addition = '''    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TerminalStop_MissingAcceptedLeaseNeverProvesNoMapping(bool finalWindow, bool residue)
    {
        var (host, _, run) = await StartAdmissionWiredParkedRun();
        var leasePath = Path.Combine(_root, "arbitration", "arbitration-lease.json");
        var savedLease = File.ReadAllBytes(leasePath);
        var hiddenLease = leasePath + ".missing-for-test";
        var residuePath = leasePath + ".fixture.tmp";
        void HideLease()
        {
            File.Move(leasePath, hiddenLease);
            if (residue) File.WriteAllText(residuePath, "{ unresolved-fixture-residue");
        }
        try
        {
            if (!finalWindow) HideLease();
            else host.AdmissionTerminalReadFaultForTest = attempt =>
            {
                // First terminal write sees complete Accepted; all terminal operations are then
                // attempted, and the second read is the final confirmation of their release.
                if (attempt == 2) HideLease();
                return null;
            };
            var stop = await host.RequestRunActionAsync(run.RunId, WorkflowRunAction.Stop);
            Assert.Equal(HostActionStatus.Unavailable, stop.Status);
            Assert.False(File.Exists(leasePath), "Stop reconstructed a missing Accepted ledger");
            Assert.NotNull(host.Runs.Load(run.RunId));
            if (residue) Assert.Equal("{ unresolved-fixture-residue", File.ReadAllText(residuePath));
        }
        finally
        {
            host.AdmissionTerminalReadFaultForTest = null;
            // Only this fixture's fault is undone; do not claim this restores a product failure.
            if (File.Exists(hiddenLease)) File.Move(hiddenLease, leasePath, true);
            if (File.Exists(residuePath)) File.Delete(residuePath);
            await host.ShutdownAsync();
        }
    }

'''
assert text.count(needle) == 1
text = text.replace(needle, addition + needle)
path.write_bytes(text.encode('utf-8'))
