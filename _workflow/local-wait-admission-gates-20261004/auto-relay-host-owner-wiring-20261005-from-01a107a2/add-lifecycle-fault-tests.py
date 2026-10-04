from pathlib import Path
p = Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterHostTests.cs')
t = p.read_bytes().decode('utf-8')
nl = '\r\n' if '\r\n' in t else '\n'
addition = '''
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_CallbackFaultOrCrossThreadWaitCannotInterruptFullObservation(bool throws)
    {
        var workflowId = Seed("shutdown-callback");
        var (host, boundary) = MakeHost(log: _ => throw new InvalidOperationException("log fault"));
        // Start logs are user callbacks too; only install the throwing log after the run is in
        // its original terminal wait, so this test specifically exercises shutdown observation.
        (host, boundary) = MakeHost();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        boundary.OnAwait = async (_, ct) =>
        {
            using var registration = ct.Register(() =>
            {
                callbackEntered.TrySetResult();
                if (throws) throw new InvalidOperationException("cancel callback fault");
                // A different thread must be able to take the Host gate during cancellation.
                Task.Run(() => host.IsDriving(workflowId)).GetAwaiter().GetResult();
            });
            entered.TrySetResult();
            await release.Task;
            return "cancelled";
        };
        Task? shutdown = null;
        try
        {
            Assert.Equal(HostActionStatus.Registered, (await host.StartWorkflowAsync(workflowId)).Status);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            shutdown = host.ShutdownAsync();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(shutdown.IsCompleted, "cancellation callback bypassed original run observation");
            release.TrySetResult();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(shutdown.IsCompletedSuccessfully, "callback exception escaped shared shutdown");
            Assert.False(host.IsDriving(workflowId));
            Assert.Single(boundary.Submissions);
        }
        finally
        {
            release.TrySetResult();
            if (shutdown is not null) await shutdown.WaitAsync(TimeSpan.FromSeconds(12));
            else await host.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Shutdown_StartSynchronousPrefixAlreadyHasCompleteObservationResponsibility()
    {
        var workflowId = Seed("start-prefix");
        var (host, boundary) = MakeHost();
        var run = host.Runs.CreateRun(workflowId, "prefix-revision");
        run.State = WorkflowRunState.Cancelled;
        host.Runs.Update(run);
        var runner = new WorkflowRunner(host.Workflows, host.Runs, boundary, new NoopPrerequisite(), new NoopTerminal());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var method = typeof(TaskCenterHost).GetMethod("LaunchDrive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Func<CancellationTokenSource, Task<WorkflowRunRecord>> start = cts =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("prefix barrier");
            Assert.True(cts.IsCancellationRequested, "shutdown did not capture pre-registered starter");
            return Task.FromResult(run);
        };
        var launch = Task.Run(() => method.Invoke(host, [workflowId, runner, start, "registered", run.RunId]));
        Task? shutdown = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            shutdown = host.ShutdownAsync();
            Assert.False(shutdown.IsCompleted, "shutdown missed already-entered synchronous start prefix");
            release.Set();
            await launch.WaitAsync(TimeSpan.FromSeconds(5));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(boundary.Submissions);
            Assert.False(host.IsDriving(workflowId));
        }
        finally
        {
            release.Set();
            await launch.WaitAsync(TimeSpan.FromSeconds(12));
            if (shutdown is not null) await shutdown.WaitAsync(TimeSpan.FromSeconds(12));
            else await host.ShutdownAsync();
        }
    }
'''
assert t.rstrip().endswith('}')
at = t.rfind('}')
t = t[:at] + addition.replace('\n', nl) + t[at:]
p.write_bytes(t.encode('utf-8'))
