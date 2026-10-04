from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs');b=p.read_bytes();s=b.decode('utf-8');nl='\r\n' if b'\r\n' in b else '\n';s=s.replace('\r\n','\n')
a=s.index('    [Fact]\n    public async Task LegacyTerminalStop_AllNewAnchorsMissingStillRetainsActuallySentResponsibility()');z=s.index('    [Theory]\n    [InlineData("valid")]\n    [InlineData("missing-anchor")]',a)
chunk=s[a:z];needle='                        .Where(o => o.RunBinding == run.RunId).All(o => o.RequestState == OperationRequestState.TerminalCompleted), TimeSpan.FromSeconds(10)));';assert chunk.count(needle)==2
chunk=chunk.replace(needle,needle+'\n                    await WaitForOriginalTerminalObserverAsync(host);')
helper='''    private static async Task WaitForOriginalTerminalObserverAsync(TaskCenterHost host)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var gate = typeof(TaskCenterHost).GetField("_gate", flags)!.GetValue(host)!;
        Task[] pending;
        lock (gate)
        {
            var entries = ((System.Collections.IEnumerable)typeof(TaskCenterHost).GetField("_driveCompletions", flags)!.GetValue(host)!).Cast<object>();
            pending = entries.Select(entry => ((TaskCompletionSource)entry.GetType().GetProperty("Completion")!.GetValue(entry)!).Task).ToArray();
        }
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
    }

'''
p.write_bytes((s[:a]+helper+chunk+s[z:]).replace('\n',nl).encode())
