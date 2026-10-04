using System.Diagnostics;
using System.Text.Json;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class RunStoreProcessFenceTests
{
    public static int OwnerActor(string directory, string runId, string actor, string evidence)
    {
        var leaseStore = new ArbitrationLeaseStore(Path.Combine(evidence, "arbitration"));
        var facade = new ArbitrationAdmissionService(leaseStore, new AdmissionHooks());
        var runs = new RunStore(directory);
        void Signal(string name) => File.WriteAllText(Path.Combine(evidence, actor + "." + name),
            JsonSerializer.Serialize(new { actor, name, pid = Environment.ProcessId }));
        void Wait(string name)
        {
            var clock = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(evidence, name)))
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException(name);
                Thread.Sleep(10);
            }
        }
        if (actor == "B") Wait("A.released");
        var acquisition = facade.EnsureOwnership("process-owner-" + actor);
        if (!acquisition.Success) return 3;
        // Reflection keeps the pre-fix actor executable: missing binding on that version
        // must still reach the actual late-write behavior assertion, rather than a compile failure.
        typeof(RunStore).GetMethod("BindOwner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(runs, [acquisition.Ownership]);
        Signal("acquired");
        if (actor == "A")
        {
            Wait("release-owner");
            var owner = leaseStore.Read().File!;
            if (!leaseStore.TryRelease(owner.Lease!.LeaseId, owner.Lease.OwnerEpoch, owner.Revision).Success) return 4;
            Signal("released");
            Wait("B.published");
        }
        var current = runs.Load(runId)!; // fresh revision: the test cannot be saved by the old CAS alone.
        current.Note = actor == "A" ? "late-original-owner" : "successor-owner";
        try { runs.Update(current); Signal("published"); }
        catch (RunRecordConflictException) { Signal("fenced"); }
        return 0;
    }

    [Fact]
    public async Task TwoActualProcesses_ReleasedOriginalOwnerCannotPublishFreshRevisionAfterSuccessor()
    {
        var products = AppContext.BaseDirectory;
        var evidence = Path.Combine(Directory.GetParent(products.TrimEnd(Path.DirectorySeparatorChar))!.FullName,
            "runstore-owner-processes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var directory = Path.Combine(evidence, "runs");
        var run = new RunStore(directory).CreateRun("owner-process", "original-revision");
        Process Start(string actor)
        {
            var start = new ProcessStartInfo(@"C:/Program Files/dotnet/dotnet.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { Path.Combine(products, "ControlledWriterProbe.dll"), "--runstore-owner-fence", directory, run.RunId, actor, evidence }) start.ArgumentList.Add(arg);
            return Process.Start(start)!;
        }
        using var a = Start("A");
        var clock = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(evidence, "A.acquired")))
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), "Original process acquisition not reached.");
            await Task.Delay(10);
        }
        File.WriteAllText(Path.Combine(evidence, "release-owner"), "release original owner");
        using var b = Start("B");
        await Task.WhenAll(a.WaitForExitAsync(), b.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, a.ExitCode);
        Assert.Equal(0, b.ExitCode);
        Assert.True(File.Exists(Path.Combine(evidence, "B.published")));
        Assert.False(File.Exists(Path.Combine(evidence, "A.published")), "Released original process published over successor using a fresh revision.");
        Assert.True(File.Exists(Path.Combine(evidence, "A.fenced")));
        var reader = new RunStore(directory);
        Assert.Equal("successor-owner", reader.Load(run.RunId)!.Note);
        var unowned = reader.Load(run.RunId)!;
        unowned.Note = "unowned direct caller";
        Assert.Throws<RunRecordConflictException>(() => reader.Update(unowned));
        Assert.Equal("successor-owner", reader.Load(run.RunId)!.Note);
    }

    // Called only by the owned probe process; uses the existing actual I/O seam.
    public static int Actor(string directory, string runId, string actor, string evidence)
    {
        var store = new RunStore(directory);
        var run = store.Load(runId)!;
        void Signal(string name) => File.WriteAllText(Path.Combine(evidence, actor + "." + name),
            JsonSerializer.Serialize(new { pid = Environment.ProcessId, revision = run.RecordRevision, actor, name }));
        Signal("loaded");
        store.FileOperationFaultForTest = operation =>
        {
            if (operation == "run-lock-contention") Signal("blocked");
            if (operation == "publish")
            {
                Signal("checked");
                var clock = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(evidence, "release")))
                {
                    if (clock.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("publication barrier not released");
                    Thread.Sleep(10);
                }
            }
            return null;
        };
        run.Note = actor;
        try { store.Update(run); Signal("published"); return 0; }
        catch (RunRecordConflictException) { Signal("conflict"); return 0; }
    }

    [Fact]
    public async Task TwoActualProcesses_SameRevisionPublish_OnlyOneCommits()
    {
        var products = AppContext.BaseDirectory;
        var probe = Path.Combine(products, "ControlledWriterProbe.dll");
        Assert.True(File.Exists(probe), "Fresh controlled writer probe must be built.");
        var evidence = Path.Combine(Directory.GetParent(products.TrimEnd(Path.DirectorySeparatorChar))!.FullName,
            "runstore-process-fence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var directory = Path.Combine(evidence, "runs");
        var store = new RunStore(directory);
        var run = store.CreateRun("process-fence", "revision-original");
        Process Start(string actor)
        {
            var start = new ProcessStartInfo(@"C:/Program Files/dotnet/dotnet.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { probe, "--runstore-publish", directory, run.RunId, actor, evidence }) start.ArgumentList.Add(arg);
            return Process.Start(start)!;
        }
        bool Has(string name) => File.Exists(Path.Combine(evidence, name));
        async Task Wait(Func<bool> ready)
        {
            var clock = Stopwatch.StartNew();
            while (!ready())
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), "Actual process barrier was not reached.");
                await Task.Delay(10);
            }
        }
        using var first = Start("A");
        Process? second = null;
        try
        {
            await Wait(() => Has("A.checked")); // A has passed the real revision check, before File.Move.
            second = Start("B");
            await Wait(() => Has("B.checked") || Has("B.blocked"));
            File.WriteAllText(Path.Combine(evidence, "release"), "release owned actors");
            await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, first.ExitCode);
            Assert.Equal(0, second.ExitCode);
            Assert.True(Has("A.published"));
            Assert.False(Has("B.published"), "Second same-revision process overwrote the committed original writer.");
            Assert.True(Has("B.conflict"));
            Assert.Equal(run.RecordRevision + 1, store.Load(run.RunId)!.RecordRevision);
            Assert.Equal("A", store.Load(run.RunId)!.Note);
            using var a = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "A.loaded")));
            using var b = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "B.loaded")));
            Assert.Equal(a.RootElement.GetProperty("revision").GetInt32(), b.RootElement.GetProperty("revision").GetInt32());
            Assert.NotEqual(a.RootElement.GetProperty("pid").GetInt32(), b.RootElement.GetProperty("pid").GetInt32());
        }
        finally
        {
            File.WriteAllText(Path.Combine(evidence, "release"), "finally release owned actors");
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (second is not null) { await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); second.Dispose(); }
        }
    }
}
