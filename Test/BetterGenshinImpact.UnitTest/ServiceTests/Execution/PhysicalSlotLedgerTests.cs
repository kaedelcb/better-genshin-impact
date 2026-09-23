using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using BetterGenshinImpact.Service.Execution;
using Xunit;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Execution;

public sealed class PhysicalSlotLedgerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "bgi-r5-slot-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CurrentSessionFactory_SelectsSameStableNamespace()
    {
        var a = PhysicalSlotLedger.ForCurrentWindowsSession();
        var b = PhysicalSlotLedger.ForCurrentWindowsSession();
        Assert.Equal(a.LockPath, b.LockPath);
        Assert.Equal(a.RecordPath, b.RecordPath);
        Assert.Contains("R5PhysicalSlot", a.RecordPath);
    }

    [Fact]
    public void SameSlot_IsExclusive_AndReacquireAdvancesGeneration()
    {
        var ledger = new PhysicalSlotLedger(_directory, "session-1-genshin");
        var first = ledger.TryAcquire((101, 111));
        Assert.Equal(PhysicalSlotAcquireStatus.Acquired, first.Status);
        using var firstLease = Assert.IsType<PhysicalSlotLease>(first.Lease);
        Assert.Equal(1, firstLease.Generation);

        var contender = new PhysicalSlotLedger(_directory, "session-1-genshin").TryAcquire((202, 222));
        Assert.Equal(PhysicalSlotAcquireStatus.Busy, contender.Status);
        Assert.Null(contender.Lease);

        firstLease.Dispose();
        var successor = ledger.TryAcquire((202, 222));
        Assert.Equal(PhysicalSlotAcquireStatus.Acquired, successor.Status);
        using var successorLease = Assert.IsType<PhysicalSlotLease>(successor.Lease);
        Assert.Equal(2, successorLease.Generation);
        Assert.NotEqual(firstLease.Nonce, successorLease.Nonce);
        Assert.Equal((202, 222), successorLease.OwnerEpoch);
    }

    [Fact]
    public void MissingOrCorruptRecordAfterPriorUse_FailsClosed()
    {
        var ledger = new PhysicalSlotLedger(_directory, "session-1-genshin");
        using (var first = Assert.IsType<PhysicalSlotLease>(ledger.TryAcquire((101, 111)).Lease)) { }
        File.WriteAllText(ledger.RecordPath, "{bad json");

        var corrupt = ledger.TryAcquire((202, 222));
        Assert.Equal(PhysicalSlotAcquireStatus.Uncertain, corrupt.Status);
        Assert.Null(corrupt.Lease);

        File.Delete(ledger.RecordPath);
        var missing = ledger.TryAcquire((202, 222));
        Assert.Equal(PhysicalSlotAcquireStatus.Uncertain, missing.Status);
        Assert.Null(missing.Lease);
    }

    [Fact]
    public void InterruptedPublishResidue_BlocksReacquisition()
    {
        var ledger = new PhysicalSlotLedger(_directory, "session-1-genshin");
        using (var first = Assert.IsType<PhysicalSlotLease>(ledger.TryAcquire((101, 111)).Lease)) { }
        File.WriteAllText(ledger.RecordPath + ".tmp-crash", "partial");

        var result = ledger.TryAcquire((202, 222));
        Assert.Equal(PhysicalSlotAcquireStatus.Uncertain, result.Status);
        Assert.Null(result.Lease);
    }

    [Fact]
    public void ExhaustedGeneration_IsRejectedWithoutChangingExistingRecord()
    {
        var ledger = new PhysicalSlotLedger(_directory, "session-1-genshin");
        using (var first = Assert.IsType<PhysicalSlotLease>(ledger.TryAcquire((101, 111)).Lease)) { }
        var record = JsonNode.Parse(File.ReadAllText(ledger.RecordPath))!;
        record["Generation"] = long.MaxValue;
        File.WriteAllText(ledger.RecordPath, record.ToJsonString());
        var before = File.ReadAllBytes(ledger.RecordPath);

        var result = ledger.TryAcquire((202, 222));
        Assert.Equal(PhysicalSlotAcquireStatus.Uncertain, result.Status);
        Assert.Equal(before, File.ReadAllBytes(ledger.RecordPath));
    }

    [Fact]
    public void LockHandle_ExcludesAnotherWindowsProcess()
    {
        var ledger = new PhysicalSlotLedger(_directory, "session-1-genshin");
        using (var first = Assert.IsType<PhysicalSlotLease>(ledger.TryAcquire((101, 111)).Lease))
            Assert.Equal("busy", ProbeFromChildProcess(ledger.LockPath));
        Assert.Equal("acquired", ProbeFromChildProcess(ledger.LockPath));
    }

    [Fact]
    public void AbortedLockHolder_ReleasesOsHandle_AndNextAcquisitionAdvancesGeneration()
    {
        var ledger = new PhysicalSlotLedger(_directory, "session-1-genshin");
        using (var initial = Assert.IsType<PhysicalSlotLease>(ledger.TryAcquire((101, 111)).Lease)) { }
        var path64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ledger.LockPath));
        var script = "$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + path64 + "'));" +
                     "$s=[IO.File]::Open($p,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);" +
                     "[Console]::WriteLine('held');[Console]::Out.Flush();Start-Sleep -Seconds 60";
        using var child = Process.Start(CreatePowerShellStart(script))!;
        try
        {
            Assert.Equal("held", child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8))
                .GetAwaiter().GetResult());
            Assert.Equal(PhysicalSlotAcquireStatus.Busy, ledger.TryAcquire((202, 222)).Status);
            child.Kill(entireProcessTree: true);
            Assert.True(child.WaitForExit(5000));
            var after = ledger.TryAcquire((303, 333));
            Assert.Equal(PhysicalSlotAcquireStatus.Acquired, after.Status);
            using var lease = Assert.IsType<PhysicalSlotLease>(after.Lease);
            Assert.Equal(2, lease.Generation);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
    }

    private static string ProbeFromChildProcess(string lockPath)
    {
        var path64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(lockPath));
        var script = "$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + path64 + "'));" +
                     "try{$s=[IO.File]::Open($p,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);" +
                     "$s.Dispose();[Console]::Write('acquired')}catch [IO.IOException]{" +
                     "$c=$_.Exception.HResult -band 0xffff;if($c -eq 32 -or $c -eq 33){[Console]::Write('busy')}else{throw}}";
        var start = CreatePowerShellStart(script);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("子进程锁探针超时");
        }
        Assert.Equal(0, process.ExitCode);
        return process.StandardOutput.ReadToEnd();
    }

    private static ProcessStartInfo CreatePowerShellStart(string script)
    {
        var command64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(command64);
        return start;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
