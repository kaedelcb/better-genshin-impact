using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MultiplayerHoeingAssistant.Services;

public sealed record MigrationRootEntry(string Path, MigrationFileVersion Version, string? BlobPath);
public sealed record MigrationRootWitness(string WitnessId, string InstanceSessionId, string OperationNonce,
    string Phase, Guid SnapshotId, Guid SnapshotSetId, Guid ProviderId, string VolumeGuid,
    MigrationFileVersion ConfigIdentity, MigrationFileVersion ArtifactIdentity,
    DateTimeOffset CaptureStarted, DateTimeOffset CaptureCompleted,
    IReadOnlyList<MigrationRootEntry> Entries, string AppliedChainDigest, string ManifestHash,
    string Integrity = "")
{
    public IReadOnlyList<MigrationRootEntry> JournalEvidence { get; init; } = [];
}

public interface IMigrationConsistency : IDisposable
{
    bool HoldsRootAuthority { get; }
    MigrationRootWitness Capture(string operationNonce, string phase, string appliedChainDigest);
}

public sealed record MigrationRootAuthorityBinding(MigrationFileVersion ConfigIdentity,
    MigrationFileVersion ArtifactIdentity, string ArtifactRoot, string? PendingTransactionId,
    string Integrity = "");

internal sealed record MigrationRootPersistentFacts(
    string ArtifactRoot, MigrationFileVersion? ConfigIdentity, MigrationFileVersion? ArtifactIdentity,
    MigrationInputBytes? BindingInput, MigrationRootAuthorityBinding? Binding,
    bool RootLockExists, bool ArtifactRootExists, string? Error);

public sealed record MigrationControlledRootWitness(string WitnessId, string TransactionId, string SessionId,
    int Generation, Guid LeaseId, int ProcessId, DateTimeOffset LeaseAcquiredAtUtc,
    DateTimeOffset CaptureStartedAtUtc, DateTimeOffset CaptureEndedAtUtc,
    MigrationFileVersion ConfigIdentity, MigrationFileVersion ArtifactIdentity,
    IReadOnlyList<MigrationRootEntry> Entries, string AppliedChainDigest, string ManifestHash,
    string WriterDomain = "registered-root-authority", string Integrity = "")
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ResolutionChainDigest { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MigrationStage? SuccessfulStage { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? OperationNonce { get; init; }
}

/// <summary>Full-root observation while registered writers are excluded by the actual root lease.</summary>
public static class MigrationControlledRootCapture
{
    public static MigrationControlledRootWitness Capture(string configRoot, string artifactRoot,
        MigrationRootAuthority authority, string transactionId, string sessionId, int generation, string chain,
        string? resolutionChain = null, MigrationStage? successfulStage = null)
    {
        if (!authority.IsHeld) throw new InvalidOperationException("migration_controlled_window_not_held");
        var start = DateTimeOffset.UtcNow;
        var store = new WindowsTxfMigrationVersionStore(configRoot, artifactRoot);
        if (!authority.ConfigIdentity.Matches(store.ConfigIdentity) || !authority.ArtifactIdentity.Matches(store.ArtifactIdentity))
            throw new InvalidOperationException("migration_controlled_window_root_mismatch");
        var entries = new List<MigrationRootEntry>();
        var pending = new Stack<string>();
        pending.Push(configRoot);
        while (pending.Count != 0)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop(), "*", new EnumerationOptions
                { AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Encrypted)) != 0)
                    throw new InvalidDataException("migration_controlled_root_unsupported_entry");
                var relative = Path.GetRelativePath(configRoot, path).Replace('\\', '/');
                var directory = (attributes & FileAttributes.Directory) != 0;
                var version = directory ? store.ReadVersion(relative, MigrationEntryKind.Directory) : store.ReadInput(relative).Version;
                entries.Add(new(relative, version, null));
                if (directory) pending.Push(path);
            }
        }
        if (!authority.IsHeld || entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            throw new InvalidDataException("migration_controlled_window_lost_or_alias_collision");
        entries.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        var witness = new MigrationControlledRootWitness(Guid.NewGuid().ToString("N"), transactionId, sessionId, generation,
            authority.LeaseId, Environment.ProcessId, authority.AcquiredAtUtc, start, DateTimeOffset.UtcNow,
            store.ConfigIdentity, store.ArtifactIdentity, entries, chain,
            MigrationOperationJournal.Hash(entries.Select(e => new { e.Path, e.Version }).ToArray()));
        witness = witness with { ResolutionChainDigest = resolutionChain, SuccessfulStage = successfulStage,
            OperationNonce = witness.WitnessId };
        return witness with { Integrity = MigrationOperationJournal.Hash(witness with { Integrity = "" }) };
    }
}

/// <summary>One physical config root has one lock and durable artifact location, across threads.</summary>
public sealed class MigrationRootAuthority : IDisposable
{
    private readonly List<SafeFileHandle> _pins = [];
    private FileStream? _lock;
    private readonly string _bindingPath;
    private readonly string _artifactRoot;
    private readonly MigrationFileVersion _configIdentity;
    private readonly MigrationFileVersion _artifactIdentity;
    private readonly IMigrationVersionStore _bindingStore;
    private readonly string _bindingName;
    public bool IsHeld => _lock is { SafeFileHandle.IsClosed: false };
    public Guid LeaseId { get; } = Guid.NewGuid();
    public DateTimeOffset AcquiredAtUtc { get; private set; }
    public MigrationFileVersion ConfigIdentity => _configIdentity;
    public MigrationFileVersion ArtifactIdentity => _artifactIdentity;

    public MigrationRootAuthority(string configRoot, string artifactRoot) : this(configRoot, artifactRoot, false) { }

    internal MigrationRootAuthority(string configRoot, string artifactRoot, bool verifiedLegacyImport)
    {
        var config = WindowsTxfMigrationVersionStore.CanonicalDirectory(configRoot);
        _artifactRoot = WindowsTxfMigrationVersionStore.CanonicalDirectory(artifactRoot);
        var parent = Path.GetDirectoryName(config) ?? throw new InvalidOperationException("migration_root_parent_unavailable");
        var identityStore = new WindowsTxfMigrationVersionStore(config, _artifactRoot);
        _configIdentity = identityStore.ConfigIdentity;
        _artifactIdentity = identityStore.ArtifactIdentity;
        var key = RootKey(_configIdentity);
        _bindingName = ".mistletoe-root-" + key + ".json";
        _bindingPath = Path.Combine(parent, _bindingName);
        _bindingStore = WindowsTxfMigrationVersionStore.ForRootBinding(config, parent, _bindingName);
        try
        {
            PinChain(config);
            PinChain(_artifactRoot);
            var lockPath = Path.Combine(parent, ".mistletoe-root-" + key + ".lock");
            var previousLock = File.Exists(lockPath);
            var previousBinding = File.Exists(_bindingPath);
            var history = File.Exists(Path.Combine(_artifactRoot, "migration-manifest.json")) ||
                Directory.EnumerateDirectories(_artifactRoot, "snapshot-*", SearchOption.TopDirectoryOnly).Any() ||
                Directory.Exists(Path.Combine(_artifactRoot, "operations")) &&
                Directory.EnumerateFileSystemEntries(Path.Combine(_artifactRoot, "operations"), "*", SearchOption.TopDirectoryOnly).Any();
            if (!previousBinding && (previousLock || history && !verifiedLegacyImport))
                throw new InvalidOperationException("migration_existing_authority_binding_missing");
            _lock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            AcquiredAtUtc = DateTimeOffset.UtcNow;
            if (File.Exists(_bindingPath))
            {
                var binding = DecodeBinding();
                if (!binding.ConfigIdentity.Matches(_configIdentity) || !binding.ArtifactIdentity.Matches(_artifactIdentity) ||
                    !string.Equals(binding.ArtifactRoot, _artifactRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("migration_root_bound_to_other_artifact_root");
            }
            else
            {
                var binding = Seal(new(_configIdentity, _artifactIdentity, _artifactRoot, null));
                _bindingStore.WriteArtifacts([new(_bindingName, MigrationOperationJournal.Encode(binding))]);
            }
        }
        catch { Dispose(); throw; }
    }

    private static string RootKey(MigrationFileVersion identity)
        => MigrationFileVersion.Hash(Encoding.UTF8.GetBytes(identity.VolumeGuid!.ToLowerInvariant() + "|" +
            identity.VolumeSerial + "|" + identity.FileId));

    internal static FileAttributes? ObserveAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    // Observation must never reserve a root, create a lock, or initialize a binding.
    internal static MigrationRootPersistentFacts ObservePersistentFacts(string configRoot, string artifactRoot)
    {
        var requestedArtifact = Path.GetFullPath(artifactRoot);
        MigrationFileVersion? configIdentity = null;
        MigrationFileVersion? artifactIdentity = null;
        MigrationInputBytes? bindingInput = null;
        MigrationRootAuthorityBinding? binding = null;
        var rootLockExists = false;
        var artifactExists = false;
        try
        {
            var config = WindowsTxfMigrationVersionStore.CanonicalDirectory(configRoot);
            var parent = Path.GetDirectoryName(config) ?? throw new InvalidOperationException("migration_root_parent_unavailable");
            var reader = WindowsTxfMigrationVersionStore.ForRootBinding(config, parent, ".mistletoe-observer-placeholder.json");
            configIdentity = reader.ConfigIdentity;
            var key = RootKey(configIdentity);
            var bindingName = ".mistletoe-root-" + key + ".json";
            var lockAttributes = ObserveAttributes(Path.Combine(parent, ".mistletoe-root-" + key + ".lock"));
            if (lockAttributes is { } la)
            {
                rootLockExists = true;
                if ((la & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Encrypted)) != 0)
                    throw new InvalidDataException("migration_root_lock_shape_invalid");
            }
            bindingInput = reader.ReadArtifactInput(bindingName);
            if (bindingInput.Version.Exists) binding = DecodeBinding(bindingInput.Bytes);
            var attributes = ObserveAttributes(requestedArtifact);
            if (attributes is { } aa)
            {
                artifactExists = true;
                if ((aa & FileAttributes.Directory) == 0 || (aa & (FileAttributes.ReparsePoint | FileAttributes.Encrypted)) != 0)
                    throw new InvalidDataException("migration_artifact_root_shape_invalid");
                requestedArtifact = WindowsTxfMigrationVersionStore.CanonicalDirectory(requestedArtifact);
                var actual = new WindowsTxfMigrationVersionStore(config, requestedArtifact);
                artifactIdentity = actual.ArtifactIdentity;
                if (!configIdentity.Matches(actual.ConfigIdentity))
                    throw new InvalidDataException("migration_observer_config_root_changed");
            }
            if (binding is not null && (binding.ConfigIdentity is null || binding.ArtifactIdentity is null ||
                artifactIdentity is null || !binding.ConfigIdentity.Matches(configIdentity) ||
                !binding.ArtifactIdentity.Matches(artifactIdentity) ||
                !string.Equals(binding.ArtifactRoot, requestedArtifact, StringComparison.OrdinalIgnoreCase) ||
                binding.PendingTransactionId is { } pending && !MigrationSwitchTransaction.IsSafeTransactionId(pending)))
                throw new InvalidDataException("migration_observer_binding_root_or_pending_mismatch");
            return new(requestedArtifact, configIdentity, artifactIdentity, bindingInput, binding, rootLockExists, artifactExists, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or NotSupportedException or Win32Exception or System.Text.Json.JsonException)
        {
            return new(requestedArtifact, configIdentity, artifactIdentity, bindingInput, binding, rootLockExists,
                artifactExists, "migration_root_facts_unverifiable:" + ex.GetType().Name);
        }
    }

    public void SetPending(string? transactionId)
    {
        if (transactionId is null) throw new InvalidOperationException("migration_pending_clear_requires_expected_identity");
        var expected = PendingTransactionId;
        if (expected is not null && expected != transactionId)
            throw new InvalidOperationException("migration_root_has_pending_transaction");
        SetPending(expected, transactionId);
    }

    public void SetPending(string? expectedTransactionId, string? transactionId)
        => _bindingStore.WriteArtifacts([PreparePendingWrite(expectedTransactionId, transactionId)]);

    internal WindowsTxfMigrationVersionStore BindingStore => (WindowsTxfMigrationVersionStore)_bindingStore;

    internal MigrationArtifactWrite PreparePendingWrite(string? expectedTransactionId, string? transactionId)
    {
        if (!IsHeld) throw new InvalidOperationException("migration_root_authority_not_held");
        if (transactionId is not null && !MigrationSwitchTransaction.IsSafeTransactionId(transactionId))
            throw new InvalidOperationException("migration_pending_identity_invalid");
        var raw = File.ReadAllBytes(_bindingPath);
        var binding = DecodeBinding(raw);
        if (!binding.ConfigIdentity.Matches(_configIdentity) || !binding.ArtifactIdentity.Matches(_artifactIdentity) ||
            !string.Equals(binding.ArtifactRoot, _artifactRoot, StringComparison.OrdinalIgnoreCase) ||
            binding.PendingTransactionId != expectedTransactionId)
            throw new InvalidOperationException("migration_pending_identity_compare_failed");
        var next = Seal(binding with { PendingTransactionId = transactionId });
        return new(_bindingName, MigrationOperationJournal.Encode(next), MigrationFileVersion.Hash(raw));
    }


    public string? PendingTransactionId => DecodeBinding().PendingTransactionId;

    private MigrationRootAuthorityBinding DecodeBinding() => DecodeBinding(File.ReadAllBytes(_bindingPath));

    private static MigrationRootAuthorityBinding DecodeBinding(byte[] raw)
    {
        var binding = System.Text.Json.JsonSerializer.Deserialize<MigrationRootAuthorityBinding>(raw,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })
            ?? throw new InvalidDataException("migration_root_binding_null");
        if (binding.Integrity != MigrationOperationJournal.Hash(binding with { Integrity = "" }))
            throw new InvalidDataException("migration_root_binding_integrity_invalid");
        return binding;
    }

    private static MigrationRootAuthorityBinding Seal(MigrationRootAuthorityBinding binding)
        => binding with { Integrity = MigrationOperationJournal.Hash(binding with { Integrity = "" }) };

    private void PinChain(string path)
    {
        var stack = new Stack<string>();
        for (var part = path; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part)) stack.Push(part);
        while (stack.Count > 0)
        {
            var handle = Native.CreateFileW(stack.Pop(), 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "migration_root_pin_failed"); }
            _pins.Add(handle);
            if (!Native.GetFileInformationByHandle(handle, out var info) || (info.Attributes & (0x400u | 0x4000u)) != 0)
                throw new InvalidOperationException("migration_root_ancestor_link_or_encryption");
        }
    }

    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
        foreach (var pin in _pins) pin.Dispose();
        _pins.Clear();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Information
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information info);
    }
}
