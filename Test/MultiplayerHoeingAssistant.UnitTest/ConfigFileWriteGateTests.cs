using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest;

public sealed class ConfigFileWriteGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ConfigFileWriteGateTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SameThreadCanNestStoreAndAtomicWriterWithoutDeadlocking()
    {
        var target = Path.Combine(_root, "startup-flow.json");
        using (ConfigFileWriteGate.Acquire(target))
        {
            new StartupFlowStore(target).SaveConfirmed(new StartupFlowConfig());
            using var nested = ConfigFileWriteGate.Acquire(target);
            Assert.True(LocalPeerSyncService.WriteFileAtomic(target, "{}"));
        }
        Assert.Equal("{}", File.ReadAllText(target));
    }

    [Fact]
    public void OtherStoreInstanceCannotPassItsRevisionCheckWhileDirectoryIsOwned()
    {
        var target = Path.Combine(_root, "startup-flow.json");
        var first = new StartupFlowStore(target);
        first.SaveConfirmed(new StartupFlowConfig());
        var second = new StartupFlowStore(target);
        var original = File.ReadAllBytes(target);
        var revision = second.LoadChecked().Revision;
        using (ConfigFileWriteGate.Acquire(target))
        {
            var error = Task.Run(() => Record.Exception(() => second.SaveChecked(new StartupFlowConfig(), revision)))
                .GetAwaiter().GetResult();
            Assert.IsAssignableFrom<IOException>(error);
            Assert.Equal(original, File.ReadAllBytes(target));
        }
        second.SaveChecked(new StartupFlowConfig(), revision);
    }

    [Fact]
    public void RoomBookmarkReadModifyWriteCannotBeginWhileTransferOwnsTheDirectory()
    {
        var target = Path.Combine(_root, "remote-log-rooms.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(target, "[]");
        var history = new RemoteLogRoomHistoryStore(target);
        using (ConfigFileWriteGate.Acquire(target))
        {
            var error = Task.Run(() => Record.Exception(() => history.Forget("https://example.com", "room")))
                .GetAwaiter().GetResult();
            Assert.IsAssignableFrom<IOException>(error);
            Assert.Equal("[]", File.ReadAllText(target));
        }
        history.Forget("https://example.com", "room");
    }

    [Fact]
    public void PhysicalSidecarExcludesAnotherProcessAndBecomesAvailableAfterRelease()
    {
        var target = Path.Combine(_root, "shared", "watch_rules.json");
        var sidecar = Path.Combine(Path.GetDirectoryName(target)!, ConfigFileWriteGate.LockFileName);
        using (ConfigFileWriteGate.Acquire(target)) Assert.Equal(23, TryOpenInAnotherProcess(sidecar));
        Assert.Equal(0, TryOpenInAnotherProcess(sidecar));
    }

    [Fact]
    public void FailedTransferRestoresCurrentForeignRulesInsteadOfTheCapturedMemory()
    {
        using var tail = new BgiLogTailService(() => null);
        using var watch = new KeywordWatchService(tail, null, null, null, _root);
        var state = watch.BeginConfigTransfer();
        var target = Path.Combine(_root, "dodoco_watch_rules.json");
        var foreign = JsonSerializer.Serialize(new WatchConfig
        { Rules = [new WatchRule { Id = "foreign", Name = "外来新规则", Pattern = "new" }] });
        Assert.True(LocalPeerSyncService.WriteFileAtomic(target, foreign));

        watch.EndConfigTransfer(state, restore: true);

        Assert.Equal("foreign", Assert.Single(watch.GetRules()).Id);
        Assert.Equal(foreign, File.ReadAllText(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableOrMissingPersistedRulesKeepThePauseAndPreserveTheCurrentFile(bool missing)
    {
        using var tail = new BgiLogTailService(() => null);
        using var watch = new KeywordWatchService(tail, null, null, null, _root);
        var state = watch.BeginConfigTransfer();
        var target = Path.Combine(_root, "dodoco_watch_rules.json");
        if (missing) File.Delete(target); else File.WriteAllText(target, "{");

        Assert.NotNull(Record.Exception(() => watch.EndConfigTransfer(state, restore: true)));

        Assert.Throws<InvalidOperationException>(() => watch.BeginConfigTransfer());
        watch.SaveRules([new WatchRule { Id = "must-not-persist", Pattern = "test" }]);
        if (missing) Assert.False(File.Exists(target));
        else Assert.Equal("{", File.ReadAllText(target));
    }

    [Fact]
    public void CapturedRulesAreFallbackOnlyWhenNoEffectiveFileOriginallyExisted()
    {
        using var tail = new BgiLogTailService(() => null);
        using var watch = new KeywordWatchService(tail, null, null, null, _root);
        var target = Path.Combine(_root, "dodoco_watch_rules.json");
        var original = watch.GetRules().Select(rule => rule.Id).ToArray();
        File.Delete(target);
        var state = watch.BeginConfigTransfer();

        watch.EndConfigTransfer(state, restore: true);

        Assert.Equal(original, watch.GetRules().Select(rule => rule.Id));
        Assert.False(File.Exists(target));
    }

    private static int TryOpenInAnotherProcess(string sidecar)
    {
        var script = "$ErrorActionPreference='Stop'; try { $stream=[IO.File]::Open('"
            + sidecar.Replace("'", "''")
            + "',[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $stream.Dispose(); exit 0 } catch { exit 23 }";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start)!;
        try
        {
            Assert.True(process.WaitForExit(10000), "隔离写许可探针超时");
            return process.ExitCode;
        }
        finally { if (!process.HasExited) process.Kill(); }
    }

    public void Dispose()
    {
        var absolute = Path.GetFullPath(_root);
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(absolute).StartsWith("ConfigFileWriteGateTests-", StringComparison.Ordinal))
            throw new InvalidOperationException("拒绝清理夹具根以外的目录");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
}
