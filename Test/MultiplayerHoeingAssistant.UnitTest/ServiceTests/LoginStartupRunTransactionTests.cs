using System.Reflection;
using Microsoft.Win32;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public class LoginStartupRunTransactionTests
{
    private static readonly string ExecutablePath = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(), "汐 & BGI", "Tools", "MultiplayerHoeingAssistant.exe"));

    [Fact]
    public void Apply_MigratesBothOldEntriesToTheSessionLauncherAndPreservesOtherValues()
    {
        using var fixture = new TemporaryRegistryKey();
        var key = fixture.Key;
        key.SetValue(AutoStartupRegistration.ValueName, "old-assistant", RegistryValueKind.String);
        key.SetValue(AutoStartupRegistration.LegacyValueName, "legacy-assistant", RegistryValueKind.String);
        key.SetValue("UnrelatedApplication", "keep", RegistryValueKind.String);
        var change = Snapshot(key);

        WindowsAutoStartupRegistration.ApplyRunValues(key, change, enabled: true, ExecutablePath);

        Assert.Equal(AutoStartupRegistration.BuildLauncherCommand(ExecutablePath), key.GetValue(AutoStartupRegistration.ValueName));
        Assert.Equal(RegistryValueKind.String, key.GetValueKind(AutoStartupRegistration.ValueName));
        Assert.Null(key.GetValue(AutoStartupRegistration.LegacyValueName));
        Assert.Equal("keep", key.GetValue("UnrelatedApplication"));
    }

    [Fact]
    public void Restore_PreservesUnexpandedOriginalValuesAndTheirRegistryTypes()
    {
        using var fixture = new TemporaryRegistryKey();
        var key = fixture.Key;
        const string originalCommand = @"%USERPROFILE%\old-assistant.exe";
        byte[] originalLegacy = [0, 7, 128, 255];
        key.SetValue(AutoStartupRegistration.ValueName, originalCommand, RegistryValueKind.ExpandString);
        key.SetValue(AutoStartupRegistration.LegacyValueName, originalLegacy, RegistryValueKind.Binary);
        var change = Snapshot(key);
        WindowsAutoStartupRegistration.ApplyRunValues(key, change, enabled: true, ExecutablePath);

        WindowsAutoStartupRegistration.RestoreRunValues(key, change);

        Assert.Equal(originalCommand, key.GetValue(AutoStartupRegistration.ValueName, null,
            RegistryValueOptions.DoNotExpandEnvironmentNames));
        Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind(AutoStartupRegistration.ValueName));
        Assert.Equal(originalLegacy, Assert.IsType<byte[]>(key.GetValue(AutoStartupRegistration.LegacyValueName)));
        Assert.Equal(RegistryValueKind.Binary, key.GetValueKind(AutoStartupRegistration.LegacyValueName));
    }

    [Fact]
    public void Restore_AFirstRegistrationRemovesOnlyTheNewLauncherValue()
    {
        using var fixture = new TemporaryRegistryKey();
        var key = fixture.Key;
        key.SetValue("UnrelatedApplication", 73, RegistryValueKind.DWord);
        var change = Snapshot(key);
        WindowsAutoStartupRegistration.ApplyRunValues(key, change, enabled: true, ExecutablePath);
        Assert.NotNull(key.GetValue(AutoStartupRegistration.ValueName));

        WindowsAutoStartupRegistration.RestoreRunValues(key, change);

        Assert.Empty(WindowsAutoStartupRegistration.ReadOwnedRunValues(key));
        Assert.Equal(73, key.GetValue("UnrelatedApplication"));
        Assert.Equal(RegistryValueKind.DWord, key.GetValueKind("UnrelatedApplication"));
    }

    [Fact]
    public void Restore_AConcurrentNewValueIsPreservedAndReportedWhileOtherOriginalValuesAreRestored()
    {
        using var fixture = new TemporaryRegistryKey();
        var key = fixture.Key;
        key.SetValue(AutoStartupRegistration.ValueName, "old-assistant", RegistryValueKind.String);
        key.SetValue(AutoStartupRegistration.LegacyValueName, "legacy-assistant", RegistryValueKind.String);
        var change = Snapshot(key);
        WindowsAutoStartupRegistration.ApplyRunValues(key, change, enabled: true, ExecutablePath);
        key.SetValue(AutoStartupRegistration.ValueName, "new-concurrent-command", RegistryValueKind.String);

        Assert.Throws<InvalidOperationException>(() => WindowsAutoStartupRegistration.RestoreRunValues(key, change));

        Assert.Equal("new-concurrent-command", key.GetValue(AutoStartupRegistration.ValueName));
        Assert.Equal("legacy-assistant", key.GetValue(AutoStartupRegistration.LegacyValueName));
    }

    [Fact]
    public void Apply_AValueChangedSinceSnapshotIsNeverOverwritten()
    {
        using var fixture = new TemporaryRegistryKey();
        var key = fixture.Key;
        key.SetValue(AutoStartupRegistration.ValueName, "old-assistant", RegistryValueKind.String);
        var change = Snapshot(key);
        key.SetValue(AutoStartupRegistration.ValueName, "new-concurrent-command", RegistryValueKind.String);

        Assert.Throws<InvalidOperationException>(() => WindowsAutoStartupRegistration.ApplyRunValues(
            key, change, enabled: true, ExecutablePath));

        Assert.Equal("new-concurrent-command", key.GetValue(AutoStartupRegistration.ValueName));
    }

    [Fact]
    public void Apply_DisablingRemovesOwnedEntriesAndLeavesOtherApplicationsIntact()
    {
        using var fixture = new TemporaryRegistryKey();
        var key = fixture.Key;
        key.SetValue(AutoStartupRegistration.ValueName, AutoStartupRegistration.BuildLauncherCommand(ExecutablePath));
        key.SetValue(AutoStartupRegistration.LegacyValueName, "legacy-assistant");
        key.SetValue("UnrelatedApplication", "keep");

        WindowsAutoStartupRegistration.ApplyRunValues(key, Snapshot(key), enabled: false, ExecutablePath);

        Assert.Empty(WindowsAutoStartupRegistration.ReadOwnedRunValues(key));
        Assert.Equal("keep", key.GetValue("UnrelatedApplication"));
    }

    [Fact]
    public void Verify_ACommandUnderOnlyTheLegacyAliasDoesNotConfirmTheNewLoginEntry()
    {
        using var fixture = new TemporaryRegistryKey();
        fixture.Key.SetValue(AutoStartupRegistration.LegacyValueName,
            AutoStartupRegistration.BuildLauncherCommand(ExecutablePath));
        var verify = typeof(WindowsAutoStartupRegistration).GetMethod("VerifyRunState",
            BindingFlags.NonPublic | BindingFlags.Static, null,
            [typeof(RegistryKey), typeof(bool), typeof(string)], null)!;

        var error = Assert.Throws<TargetInvocationException>(() =>
            verify.Invoke(null, [fixture.Key, true, ExecutablePath]));

        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Null(fixture.Key.GetValue(AutoStartupRegistration.ValueName));
    }

    private static WindowsAutoStartupRegistration.RegistrationChange Snapshot(RegistryKey key)
        => new(null, WindowsAutoStartupRegistration.ReadOwnedRunValues(key));

    private sealed class TemporaryRegistryKey : IDisposable
    {
        // This is deliberately outside the Windows Run key; each test owns one unique disposable key.
        private readonly string _path = @"Software\NexusBGI.Tests\LoginStartupRunTransaction\" + Guid.NewGuid().ToString("N");
        internal RegistryKey Key { get; }

        internal TemporaryRegistryKey()
            => Key = Registry.CurrentUser.CreateSubKey(_path, writable: true)
                ?? throw new InvalidOperationException("无法创建隔离的注册表测试键。");

        public void Dispose()
        {
            Key.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
        }
    }
}
