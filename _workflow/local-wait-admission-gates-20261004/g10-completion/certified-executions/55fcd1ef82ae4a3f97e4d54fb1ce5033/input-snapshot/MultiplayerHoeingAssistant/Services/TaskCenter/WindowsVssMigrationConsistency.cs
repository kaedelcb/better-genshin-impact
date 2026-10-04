using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Alphaleonis.Win32.Vss;
using Microsoft.Win32.SafeHandles;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>Fresh whole-root VSS witnesses on an explicitly verified, owned isolated volume.</summary>
public sealed class WindowsVssMigrationConsistency : IMigrationConsistency
{
    private readonly string _config;
    private readonly string _artifacts;
    private readonly string _volume;
    private readonly string _ownership;
    private readonly MigrationRootAuthority _authority;
    private readonly MigrationFileVersion _configIdentity;
    private readonly MigrationFileVersion _artifactIdentity;
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly object _sync = new();
    private bool _disposed;
    private static readonly Guid SystemProvider = new("b5946137-7b9f-4925-af80-51abd60b20d5");
    public bool HoldsRootAuthority => !_disposed && _authority.IsHeld;
    public MigrationRootAuthority Authority => _authority;

    public WindowsVssMigrationConsistency(string configRoot, string artifactRoot, string ownedVolumeIdentityFile)
    {
        _config = WindowsTxfMigrationVersionStore.CanonicalDirectory(configRoot);
        _artifacts = WindowsTxfMigrationVersionStore.CanonicalDirectory(artifactRoot);
        var versions = new WindowsTxfMigrationVersionStore(_config, _artifacts);
        _configIdentity = versions.ConfigIdentity;
        _artifactIdentity = versions.ArtifactIdentity;
        _volume = _configIdentity.VolumeGuid!;
        _ownership = Path.GetFullPath(ownedVolumeIdentityFile);
        VerifyOwnedVolume();
        _authority = new MigrationRootAuthority(_config, _artifacts);
    }

    public MigrationRootWitness Capture(string operationNonce, string phase, string appliedChainDigest)
    {
        lock (_sync)
        {
            if (!HoldsRootAuthority) throw new InvalidOperationException("migration_consistency_not_held");
            if (string.IsNullOrWhiteSpace(operationNonce) || string.IsNullOrWhiteSpace(phase))
                throw new ArgumentException("migration_witness_request_identity_missing");
            VerifyOwnedVolume();
            MigrationRootWitness? result = null;
            Exception? error = null;
            var thread = new Thread(() =>
            {
                var initialized = false;
                try
                {
                    Marshal.ThrowExceptionForHR(Native.CoInitializeEx(IntPtr.Zero, 0));
                    initialized = true;
                    var security = Native.CoInitializeSecurity(IntPtr.Zero, -1, IntPtr.Zero, IntPtr.Zero, 6, 3, IntPtr.Zero, 0, IntPtr.Zero);
                    if (security != unchecked((int)0x80010119)) Marshal.ThrowExceptionForHR(security);
                    result = CaptureOnComThread(operationNonce, phase, appliedChainDigest);
                }
                catch (Exception ex) { error = ex; }
                finally { if (initialized) Native.CoUninitialize(); }
            }) { IsBackground = true, Name = "Mistletoe VSS witness" };
            thread.Start();
            thread.Join();
            if (error is not null) throw new InvalidOperationException("migration_vss_capture_failed", error);
            return result ?? throw new InvalidOperationException("migration_vss_capture_missing");
        }
    }

    private MigrationRootWitness CaptureOnComThread(string nonce, string phase, string chain)
    {
        var factory = VssFactoryProvider.Default.GetVssFactory();
        using var components = factory.CreateVssBackupComponents();
        components.InitializeForBackup(null);
        components.SetContext(VssSnapshotContext.FileShareBackup);
        components.SetBackupState(false, false, VssBackupType.Full, false);
        if (!components.IsVolumeSupported(_volume, SystemProvider))
            throw new PlatformNotSupportedException("migration_vss_volume_unsupported");
        var setId = components.StartSnapshotSet();
        var snapshotId = components.AddToSnapshotSet(_volume, SystemProvider);
        var started = DateTimeOffset.UtcNow;
        components.DoSnapshotSet();
        var finished = DateTimeOffset.UtcNow;
        var properties = components.GetSnapshotProperties(snapshotId);
        if (properties.SnapshotId != snapshotId || properties.SnapshotSetId != setId || properties.ProviderId != SystemProvider ||
            !string.Equals(properties.OriginalVolumeName, _volume, StringComparison.OrdinalIgnoreCase) || properties.Status.ToString() != "Created")
            throw new InvalidDataException("migration_vss_properties_mismatch");
        var shadowConfig = ShadowPath(properties.SnapshotDeviceObject, _config);
        var shadowArtifacts = ShadowPath(properties.SnapshotDeviceObject, _artifacts);
        if (!_configIdentity.Matches(ReadVersion(shadowConfig, MigrationEntryKind.Directory)) ||
            !_artifactIdentity.Matches(ReadVersion(shadowArtifacts, MigrationEntryKind.Directory)))
            throw new InvalidDataException("migration_vss_root_identity_mismatch");
        var witnessId = Guid.NewGuid().ToString("N");
        var destination = Path.Combine(_artifacts, "witnesses", witnessId);
        Directory.CreateDirectory(destination);
        var evidence = new List<MigrationRootEntry>();
        if (File.Exists(Path.Combine(shadowArtifacts, "operation-journal.json")))
        {
            var journal = new MigrationOperationJournal(shadowArtifacts,
                new WindowsTxfMigrationVersionStore(_config, _artifacts));
            var captured = journal.ReadValidatedEvidence();
            if (!string.Equals(captured.Digest, chain, StringComparison.Ordinal))
                throw new InvalidDataException("migration_shadow_journal_chain_mismatch");
            foreach (var item in captured.Files.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var version = ReadVersion(WindowsTxfMigrationVersionStore.Resolve(shadowArtifacts, item.Key), MigrationEntryKind.File);
                if (version.Sha256 != MigrationFileVersion.Hash(item.Value))
                    throw new InvalidDataException("migration_shadow_journal_bytes_mismatch");
                var blob = "witnesses/" + witnessId + "/journal/" + item.Key;
                var output = WindowsTxfMigrationVersionStore.Resolve(_artifacts, blob);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(item.Value);
                stream.Flush(true);
                evidence.Add(new(item.Key, version, blob));
            }
        }
        else if (chain != MigrationOperationJournal.EmptyChainDigest || File.Exists(Path.Combine(shadowArtifacts, "migration-manifest.json")))
            throw new InvalidDataException("migration_shadow_journal_missing_or_legacy_adoption_required");
        var entries = new List<MigrationRootEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(shadowConfig, "*", new EnumerationOptions
                 { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Encrypted)) != 0)
                throw new InvalidDataException("migration_vss_unsupported_entry");
            var relative = Path.GetRelativePath(shadowConfig, path).Replace('\\', '/');
            var kind = (attributes & FileAttributes.Directory) != 0 ? MigrationEntryKind.Directory : MigrationEntryKind.File;
            var version = ReadVersion(path, kind);
            string? blob = null;
            if (kind == MigrationEntryKind.File)
            {
                blob = "witnesses/" + witnessId + "/data/" + relative;
                var output = WindowsTxfMigrationVersionStore.Resolve(_artifacts, blob);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                var bytes = File.ReadAllBytes(path);
                if (MigrationFileVersion.Hash(bytes) != version.Sha256) throw new IOException("migration_shadow_bytes_changed");
                using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(bytes);
                stream.Flush(true);
            }
            entries.Add(new(relative, version, blob));
        }
        if (entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            throw new InvalidDataException("migration_vss_case_alias_collision");
        entries.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        var manifestHash = MigrationOperationJournal.Hash(entries.Select(e => new { e.Path, e.Version }).ToArray());
        var witness = new MigrationRootWitness(witnessId, _session, nonce, phase, snapshotId, setId, SystemProvider, _volume,
            _configIdentity, _artifactIdentity, started, finished, entries, chain, manifestHash)
            { JournalEvidence = evidence };
        witness = witness with { Integrity = MigrationOperationJournal.Hash(witness with { Integrity = "" }) };
        var raw = MigrationOperationJournal.Encode(witness);
        using (var stream = new FileStream(Path.Combine(destination, "witness.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(raw); stream.Flush(true); }
        return witness;
    }

    private string ShadowPath(string device, string ordinaryPath)
    {
        var mount = new StringBuilder(32768);
        if (!Native.GetVolumePathNameW(ordinaryPath, mount, mount.Capacity)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var relative = Path.GetRelativePath(mount.ToString(), ordinaryPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) throw new InvalidDataException("migration_shadow_root_outside_volume");
        return device.TrimEnd('\\') + "\\" + relative;
    }

    private MigrationFileVersion ReadVersion(string path, MigrationEntryKind kind)
    {
        using var handle = Native.CreateFileW(path, 0x80000000, 7, IntPtr.Zero, 3,
            kind == MigrationEntryKind.Directory ? 0x02200000u : 0x00200080u, IntPtr.Zero);
        if (handle.IsInvalid || !Native.GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "migration_shadow_entry_identity");
        if ((info.Attributes & (0x400u | 0x4000u)) != 0 || info.Links != 1)
            throw new InvalidDataException("migration_shadow_link_or_encryption");
        var bytes = kind == MigrationEntryKind.File ? File.ReadAllBytes(path) : null;
        return new(true, kind, _volume, info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow, info.Links,
            bytes?.LongLength, bytes is null ? null : MigrationFileVersion.Hash(bytes));
    }

    private void VerifyOwnedVolume()
    {
        using var identity = JsonDocument.Parse(File.ReadAllBytes(_ownership));
        var data = identity.RootElement;
        if (!data.GetProperty("owned").GetBoolean() || !string.Equals(data.GetProperty("volume_path").GetString(), _volume, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("migration_vss_owned_volume_identity_required");
        var image = Path.GetFullPath(data.GetProperty("image_path").GetString()!);
        if (!File.Exists(image) || !image.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("migration_owned_image_missing");
        // Read-only mapping check: no attach, format, resize, or deletion here.
        var literal = image.Replace("'", "''");
        var script = "$ErrorActionPreference='Stop'; $i=Get-DiskImage -ImagePath '" + literal + "'; $d=$i|Get-Disk; " +
            "if(!$i.Attached -or $d.IsBoot -or $d.IsSystem){throw 'owned image mismatch'}; " +
            "$v=@(Get-Partition -DiskNumber $d.Number|Get-Volume|Where-Object {$_.FileSystem -eq 'NTFS'}); " +
            "$s=@(Get-CimInstance Win32_ShadowCopy|Where-Object VolumeName -eq '" + _volume.Replace("'", "''") + "'); " +
            "[ordered]@{volumes=@($v|ForEach-Object {$_.Path}); shadows=$s.Count}|ConvertTo-Json -Compress";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("migration_owned_volume_query_failed");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("migration_owned_volume_query_failed:" + error);
        using var result = JsonDocument.Parse(output);
        if (result.RootElement.GetProperty("shadows").GetInt32() != 0 ||
            !result.RootElement.GetProperty("volumes").EnumerateArray().Any(e => string.Equals(e.GetString(), _volume, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("migration_owned_volume_mapping_or_existing_shadow_invalid");
    }

    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; _authority.Dispose(); }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Information
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")] internal static extern int CoInitializeSecurity(IntPtr descriptor, int count, IntPtr services, IntPtr reserved, uint level, uint impersonation, IntPtr auth, uint capabilities, IntPtr reserved2);
        [DllImport("ole32.dll")] internal static extern void CoUninitialize();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetVolumePathNameW(string path, StringBuilder root, int size);
    }
}
