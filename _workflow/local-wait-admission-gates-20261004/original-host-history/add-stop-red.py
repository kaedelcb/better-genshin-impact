from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs')
b=p.read_bytes(); s=b.decode('utf-8'); nl='\r\n' if b'\r\n' in b else '\n'
s=s.replace('    [InlineData("settle")]'+nl+'    public async Task OriginalHost_', '    [InlineData("settle")]'+nl+'    [InlineData("stop-exited")]'+nl+'    [InlineData("stop-missing")]'+nl+'    public async Task OriginalHost_',1)
a='''                    var queriesBefore = port.OriginalReconcileQueries;'''
z='''                    if (scenario.StartsWith("stop-", StringComparison.Ordinal))
                    {
                        // Same real host Stop entry, with the controlled server's original request projection.
                        var client = (BgiExternalClient)((Func<BgiExternalClient?>)typeof(TaskCenterHost)
                            .GetField("_clientAccessor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                            .GetValue(host)!)()!;
                        typeof(BgiExternalClient).GetProperty("State")!.SetValue(client, BgiExternalLinkState.Ready);
                        var job = port.OriginalReconcileSnapshot.Jobs[0];
                        job.Epoch = port.ServerEpoch;
                        if (scenario == "stop-exited")
                        {
                            job.State = "cancelled"; job.ExecutionExitConfirmed = true;
                            job.ExecutionExitDisposition = "execution_exited";
                            port.OriginalStatusJob = job;
                        }
                        var stop = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
                        var after = runs.Load(run.RunId)!;
                        Assert.True(after.StopRequested);
                        Assert.Equal(1, port.SendCount);
                        Assert.Equal(scenario == "stop-exited" ? WorkflowRunState.Cancelled : WorkflowRunState.Unknown, after.State);
                        if (scenario == "stop-exited")
                        {
                            Assert.Equal(HostActionStatus.Effective, stop.Status);
                            Assert.Equal("cancelled", after.CurrentSubmission!.ObservedTerminal);
                            Assert.True(after.CurrentSubmission.ExecutionExitConfirmed);
                            Assert.False(RunStore.HasUnresolvedTerminalResponsibility(after));
                            Assert.True(TerminalReleaseEvidence.ValidRunSeal(after));
                            var second = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
                            Assert.Equal(HostActionStatus.Effective, second.Status);
                            Assert.Equal(after.TerminalRelease, runs.Load(run.RunId)!.TerminalRelease);
                        }
                        else
                        {
                            Assert.Equal(HostActionStatus.Unavailable, stop.Status);
                            Assert.True(RunStore.HasUnresolvedTerminalResponsibility(after));
                            Assert.Null(after.TerminalRelease);
                        }
                        return;
                    }
                    var queriesBefore = port.OriginalReconcileQueries;'''.replace('\n',nl)
assert s.count(a)==1;s=s.replace(a,z)
a='        public Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(string jobId, CancellationToken ct)'+nl+'        {'
z='        public BgiJobInfo? OriginalStatusJob { get; set; }'+nl+a+nl+'            if (OriginalStatusJob?.JobId == jobId)'+nl+'                return Task.FromResult<(string?, BgiJobInfo?)>((OriginalStatusJob.State, OriginalStatusJob));'
assert s.count(a)==1;s=s.replace(a,z)
p.write_bytes(s.encode('utf-8'))
