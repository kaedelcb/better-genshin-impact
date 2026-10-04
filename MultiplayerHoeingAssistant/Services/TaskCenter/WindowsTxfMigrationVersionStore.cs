using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>Concrete TxF CAS. Data and its Applied metadata share one kernel transaction.</summary>
public sealed class WindowsTxfMigrationVersionStore : IMigrationVersionStore
{
    private readonly string _configRoot;
    private readonly string _artifactRoot;
    private readonly string _volume;
    private readonly MigrationFileVersion _configIdentity;
    private readonly MigrationFileVersion _artifactIdentity;
    private readonly string? _bindingOnlyName;
    private const uint ReadWrite = 0xC0000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint BackupSemantics = 0x02000000;
    private const uint Normal = 128;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint NoDeleteSharing = 3;
    internal MigrationFileVersion ConfigIdentity => _configIdentity;
    internal MigrationFileVersion ArtifactIdentity => _artifactIdentity;

    public WindowsTxfMigrationVersionStore(string configRoot, string artifactRoot) : this(configRoot, artifactRoot, null) { }

    internal static WindowsTxfMigrationVersionStore ForRootBinding(string configRoot, string parentRoot, string bindingName)
        => new(configRoot, parentRoot, bindingName);

    private WindowsTxfMigrationVersionStore(string configRoot, string artifactRoot, string? bindingOnlyName)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("migration_txf_requires_windows_x64");
        _configRoot = CanonicalDirectory(configRoot);
        _artifactRoot = CanonicalDirectory(artifactRoot);
        _bindingOnlyName = bindingOnlyName;
        if (bindingOnlyName is null && (Within(_configRoot, _artifactRoot) || Within(_artifactRoot, _configRoot)))
            throw new ArgumentException("migration_roots_overlap");
        _volume = VolumeOf(_configRoot);
        if (!string.Equals(_volume, VolumeOf(_artifactRoot), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("migration_roots_not_same_volume");
        _configIdentity = DirectoryVersion(_configRoot);
        _artifactIdentity = DirectoryVersion(_artifactRoot);
    }

    public MigrationFileVersion ReadVersion(string relativePath, MigrationEntryKind kind = MigrationEntryKind.File)
    {
        if (_bindingOnlyName is not null) throw new InvalidOperationException("migration_binding_store_has_no_data_access");
        using var roots = PinRoots();
        var path = Resolve(_configRoot, relativePath);
        if (!File.Exists(path) && !Directory.Exists(path)) return MigrationFileVersion.Absent(kind);
        using var handle = Native.CreateFileW(path, 0x80000000, 1, IntPtr.Zero, 3,
            (kind == MigrationEntryKind.Directory ? BackupSemantics : Normal) | OpenReparsePoint, IntPtr.Zero);
        Valid(handle, "read_version");
        VerifyNamedHandle(handle, path, _configRoot, kind);
        return Version(handle, kind);
    }

    public MigrationInputBytes ReadInput(string relativePath)
    {
        if (_bindingOnlyName is not null) throw new InvalidOperationException("migration_binding_store_has_no_data_access");
        using var roots = PinRoots();
        using var handle = Native.CreateFileW(Resolve(_configRoot, relativePath), 0x80000000, 1, IntPtr.Zero, 3,
            Normal | OpenReparsePoint, IntPtr.Zero);
        Valid(handle, "read_input");
        VerifyNamedHandle(handle, Resolve(_configRoot, relativePath), _configRoot, MigrationEntryKind.File);
        var version = Version(handle, MigrationEntryKind.File);
        var bytes = Read(handle);
        if (version.Length != bytes.LongLength || version.Sha256 != MigrationFileVersion.Hash(bytes))
            throw new IOException("migration_input_readback_changed");
        return new(version, bytes);
    }

    internal MigrationInputBytes ReadArtifactInput(string relativePath)
    {
        using var roots = PinRoots();
        var path = Resolve(_artifactRoot, relativePath);
        using var handle = Native.CreateFileW(path, 0x80000000, 1, IntPtr.Zero, 3,
            Normal | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 2) return new(MigrationFileVersion.Absent(), []);
            throw new MigrationMainOwnershipException("migration_main_input_open_failed:" + error);
        }
        VerifyNamedHandle(handle, path, _artifactRoot, MigrationEntryKind.File,
            ownedMain: relativePath == "migration-manifest.json");
        var version = Version(handle, MigrationEntryKind.File);
        var bytes = Read(handle);
        if (version.Length != bytes.LongLength || version.Sha256 != MigrationFileVersion.Hash(bytes))
            throw new MigrationMainOwnershipException("migration_main_input_readback_changed");
        return new(version, bytes);
    }

    public MigrationVersionMutationResult ExecuteFile(string relativePath, MigrationFileVersion input,
        Func<byte[], MigrationFileVersion, IReadOnlyList<MigrationDirectoryEffect>, MigrationWriteInstruction> prepare,
        IReadOnlyDictionary<string, MigrationFileVersion>? parents = null, Action<string>? fault = null,
        Action<Guid>? transactionCreated = null)
    {
        if (_bindingOnlyName is not null) throw new InvalidOperationException("migration_binding_store_has_no_data_access");
        var directories = new List<MigrationDirectoryEffect>();
        var pins = new List<SafeFileHandle>();
        var mainOutputs = new List<(MigrationMainOwnership Owner, MigrationInputBytes Output)>();
        MigrationFileVersion? output = null;
        var attempted = false;
        var confirmed = false;
        using var transaction = Begin();
        try
        {
            Check(Native.GetTransactionId(transaction, out var transactionId), "get_transaction_id");
            transactionCreated?.Invoke(transactionId);
            pins.AddRange(PinRoots().Detach());
            var path = Resolve(_configRoot, relativePath);
            PinAndCreateParents(transaction, relativePath, parents, directories, pins);
            using (var handle = Native.CreateFileTransactedW(path, ReadWrite | DeleteAccess, NoDeleteSharing, IntPtr.Zero,
                       input.Exists ? 3u : 1u, Normal | OpenReparsePoint, IntPtr.Zero, transaction, IntPtr.Zero, IntPtr.Zero))
            {
                Valid(handle, "open_version");
                VerifyNamedHandle(handle, path, _configRoot, MigrationEntryKind.File);
                var observed = input.Exists ? Version(handle, MigrationEntryKind.File) : MigrationFileVersion.Absent();
                if (!input.Matches(observed)) throw new InvalidOperationException("migration_input_version_mismatch:" + relativePath);
                var bytes = input.Exists ? Read(handle) : [];
                fault?.Invoke("after_open");
                VerifyPinnedDirectories(pins);
                VerifyNamedHandle(handle, path, _configRoot, MigrationEntryKind.File);
                var instruction = prepare(bytes, observed, directories);
                fault?.Invoke("after_prepared");
                VerifyPinnedDirectories(pins);
                VerifyNamedHandle(handle, path, _configRoot, MigrationEntryKind.File);
                if (instruction.Delete)
                {
                    if (!input.Exists) throw new InvalidOperationException("migration_delete_without_owned_input");
                    byte disposition = 1;
                    Check(Native.SetFileInformationByHandle(handle, 4, ref disposition, 1), "delete_disposition");
                    output = MigrationFileVersion.Absent();
                }
                else
                {
                    var expected = instruction.Bytes ?? throw new InvalidOperationException("migration_output_missing");
                    Write(handle, expected);
                    output = Version(handle, MigrationEntryKind.File);
                    if (output.Length != expected.LongLength || output.Sha256 != MigrationFileVersion.Hash(expected))
                        throw new IOException("migration_output_readback_mismatch");
                }
                fault?.Invoke("after_data");
                VerifyPinnedDirectories(pins);
                if (!instruction.Delete) VerifyNamedHandle(handle, path, _configRoot, MigrationEntryKind.File);
                WriteArtifacts(transaction, instruction.AppliedArtifacts(output, directories), pins, mainOutputs);
                fault?.Invoke("after_receipt");
                VerifyPinnedDirectories(pins);
                if (!instruction.Delete) VerifyNamedHandle(handle, path, _configRoot, MigrationEntryKind.File);
            }
            foreach (var handle in pins) handle.Dispose();
            pins.Clear();
            fault?.Invoke("after_handles");
            attempted = true;
            Check(Native.CommitTransaction(transaction), "commit_transaction");
            confirmed = true;
            ConfirmMainOutputs(mainOutputs);
            fault?.Invoke("after_commit");
            return new(true, "", attempted, confirmed, output, directories);
        }
        catch (Exception ex)
        {
            return new(false, Describe(ex), attempted, confirmed, output, directories);
        }
        finally
        {
            foreach (var handle in pins) handle.Dispose();
            if (!confirmed) Native.RollbackTransaction(transaction);
        }
    }

    public MigrationVersionMutationResult RemoveDirectory(string relativePath, MigrationFileVersion input,
        Func<MigrationFileVersion, IReadOnlyList<MigrationArtifactWrite>> appliedArtifacts, Action<string>? fault = null,
        Action<Guid>? transactionCreated = null, IReadOnlyDictionary<string, MigrationFileVersion>? parents = null)
    {
        if (_bindingOnlyName is not null) throw new InvalidOperationException("migration_binding_store_has_no_data_access");
        var attempted = false;
        var confirmed = false;
        using var transaction = Begin();
        using var roots = PinRoots();
        var artifactPins = new List<SafeFileHandle>();
        var mainOutputs = new List<(MigrationMainOwnership Owner, MigrationInputBytes Output)>();
        try
        {
            Check(Native.GetTransactionId(transaction, out var transactionId), "get_directory_transaction_id");
            transactionCreated?.Invoke(transactionId);
            var path = Resolve(_configRoot, relativePath);
            if (parents is not null && parents.Values.Any(p => !p.Exists)) throw new InvalidOperationException("migration_delete_parent_missing");
            PinAndCreateParents(transaction, relativePath, parents, [], artifactPins);
            using (var handle = Native.CreateFileTransactedW(path, 0x10086, NoDeleteSharing, IntPtr.Zero, 3,
                       BackupSemantics | OpenReparsePoint, IntPtr.Zero, transaction, IntPtr.Zero, IntPtr.Zero))
            {
                Valid(handle, "open_directory");
                VerifyNamedHandle(handle, path, _configRoot, MigrationEntryKind.Directory);
                if (!input.Matches(Version(handle, MigrationEntryKind.Directory)))
                    throw new InvalidOperationException("migration_directory_identity_mismatch");
                fault?.Invoke("before_directory_delete");
                VerifyPinnedDirectories(artifactPins);
                VerifyNamedHandle(handle, path, _configRoot, MigrationEntryKind.Directory);
                // Same validated DELETE-access handle; NTFS rejects nonempty directories.
                byte disposition = 1;
                Check(Native.SetFileInformationByHandle(handle, 4, ref disposition, 1),
                    "remove_owned_empty_directory_handle");
            } // TxF deletions require closing the marked handle before commit.
            WriteArtifacts(transaction, appliedArtifacts(MigrationFileVersion.Absent(MigrationEntryKind.Directory)), artifactPins, mainOutputs);
            foreach (var pin in artifactPins) pin.Dispose();
            artifactPins.Clear();
            fault?.Invoke("after_receipt");
            attempted = true;
            Check(Native.CommitTransaction(transaction), "commit_directory_transaction");
            confirmed = true;
            ConfirmMainOutputs(mainOutputs);
            fault?.Invoke("after_commit");
            return new(true, "", attempted, confirmed, MigrationFileVersion.Absent(MigrationEntryKind.Directory), []);
        }
        catch (Exception ex) { return new(false, Describe(ex), attempted, confirmed, null, []); }
        finally
        {
            foreach (var pin in artifactPins) pin.Dispose();
            if (!confirmed) Native.RollbackTransaction(transaction);
        }
    }

    // The only cross-root metadata publish is the held authority's single binding file.
    // No caller-supplied path or generic kernel-transaction callback is accepted.
    internal void WriteArtifactsWithAuthority(IReadOnlyList<MigrationArtifactWrite> writes,
        MigrationRootAuthority authority, string? expectedPending, string? proposedPending)
    {
        if (!authority.IsHeld || !authority.ConfigIdentity.Matches(ConfigIdentity) ||
            !authority.ArtifactIdentity.Matches(ArtifactIdentity))
            throw new InvalidOperationException("migration_authority_publish_root_mismatch");
        var binding = authority.PreparePendingWrite(expectedPending, proposedPending);
        var bindingStore = authority.BindingStore;
        if (!bindingStore.ConfigIdentity.Matches(ConfigIdentity))
            throw new InvalidOperationException("migration_authority_publish_config_mismatch");
        using var transaction = Begin();
        using var roots = PinRoots();
        using var bindingRoots = bindingStore.PinRoots();
        var pins = new List<SafeFileHandle>();
        var mainOutputs = new List<(MigrationMainOwnership Owner, MigrationInputBytes Output)>();
        var confirmed = false;
        try
        {
            bindingStore.WriteArtifacts(transaction, [binding], pins, mainOutputs);
            WriteArtifacts(transaction, writes, pins, mainOutputs);
            foreach (var pin in pins) pin.Dispose();
            pins.Clear();
            Check(Native.CommitTransaction(transaction), "commit_authority_and_artifacts");
            confirmed = true;
            ConfirmMainOutputs(mainOutputs);
        }
        finally
        {
            foreach (var pin in pins) pin.Dispose();
            if (!confirmed) Native.RollbackTransaction(transaction);
        }
    }


    public void WriteArtifacts(IReadOnlyList<MigrationArtifactWrite> writes)
    {
        using var transaction = Begin();
        using var roots = PinRoots();
        var pins = new List<SafeFileHandle>();
        var mainOutputs = new List<(MigrationMainOwnership Owner, MigrationInputBytes Output)>();
        var confirmed = false;
        try
        {
            WriteArtifacts(transaction, writes, pins, mainOutputs);
            foreach (var pin in pins) pin.Dispose();
            pins.Clear();
            Check(Native.CommitTransaction(transaction), "commit_artifacts");
            confirmed = true;
            ConfirmMainOutputs(mainOutputs);
        }
        finally
        {
            foreach (var pin in pins) pin.Dispose();
            if (!confirmed) Native.RollbackTransaction(transaction);
        }
    }

    public static bool IsProducerTransactionTerminated(Guid transactionId)
    {
        if (transactionId == Guid.Empty) return false;
        using var handle = Native.OpenTransaction(1, ref transactionId);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 6715) return true; // ERROR_TRANSACTION_NOT_FOUND, not an arbitrary query failure.
            throw new Win32Exception(error, "migration_query_old_transaction");
        }
        Check(Native.GetTransactionInformation(handle, out var outcome, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero),
            "query_old_transaction_outcome");
        return outcome == 3; // A live/undetermined or committed transaction cannot justify cancellation.
    }

    private void WriteArtifacts(TransactionHandle transaction, IReadOnlyList<MigrationArtifactWrite> writes,
        List<SafeFileHandle> pins,
        List<(MigrationMainOwnership Owner, MigrationInputBytes Output)> mainOutputs)
    {
        foreach (var write in writes)
        {
            if (_bindingOnlyName is not null && write.Path != _bindingOnlyName)
                throw new InvalidOperationException("migration_binding_store_path_not_allowed");
            var path = Resolve(_artifactRoot, write.Path);
            PinArtifactParents(transaction, write.Path, pins);
            VerifyPinnedDirectories(pins);
            var expected = write.ExpectedInput;
            var exists = expected?.Version.Exists ?? (write.ExpectedSha256 is not null);
            using var handle = Native.CreateFileTransactedW(path, ReadWrite, NoDeleteSharing, IntPtr.Zero,
                exists ? 3u : 1u, Normal | OpenReparsePoint, IntPtr.Zero, transaction, IntPtr.Zero, IntPtr.Zero);
            if (handle.IsInvalid && write.MainOwnership is not null)
                throw new MigrationMainOwnershipException("migration_main_input_not_owned:open:" + Marshal.GetLastWin32Error());
            Valid(handle, "open_artifact");
            VerifyNamedHandle(handle, path, _artifactRoot, MigrationEntryKind.File, write.MainOwnership is not null);
            if (expected is not null)
            {
                if (expected.Version.Exists)
                {
                    var actual = Version(handle, MigrationEntryKind.File);
                    var actualBytes = Read(handle);
                    if (!expected.Version.Matches(actual) || !expected.Bytes.AsSpan().SequenceEqual(actualBytes))
                        throw new MigrationMainOwnershipException("migration_main_input_not_owned:version");
                }
                else if (expected.Bytes.Length != 0)
                    throw new MigrationMainOwnershipException("migration_main_absent_input_has_bytes");
            }
            else if (write.ExpectedSha256 is not null && MigrationFileVersion.Hash(Read(handle)) != write.ExpectedSha256)
                throw new InvalidOperationException("migration_artifact_input_mismatch:" + write.Path);
            if (write.MainOwnership is not null && expected is null)
                throw new MigrationMainOwnershipException("migration_main_expected_input_missing");
            VerifyNamedHandle(handle, path, _artifactRoot, MigrationEntryKind.File, write.MainOwnership is not null);
            Write(handle, write.Bytes);
            var output = Version(handle, MigrationEntryKind.File);
            var outputBytes = Read(handle);
            if (output.Length != write.Bytes.LongLength || output.Sha256 != MigrationFileVersion.Hash(write.Bytes) ||
                !outputBytes.AsSpan().SequenceEqual(write.Bytes))
                throw new IOException("migration_artifact_readback_mismatch:" + write.Path);
            if (write.MainOwnership is { } owner)
                mainOutputs.Add((owner, new(output, (byte[])outputBytes.Clone())));
        }
    }

    private static void ConfirmMainOutputs(List<(MigrationMainOwnership Owner, MigrationInputBytes Output)> outputs)
    {
        foreach (var item in outputs) item.Owner.Confirm(item.Output);
    }

    private static string NamespacePath(string value)
    {
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("migration_remote_namespace_unsupported");
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal)) value = value[4..];
        var full = Path.GetFullPath(value);
        var root = Path.GetPathRoot(full)!;
        // Preserve drive-root separator: E:\ must never become drive-relative E:.
        return full.Length > root.Length ? full.TrimEnd('\\', '/') : root;
    }

    private static void NamespaceFailure(string reason, bool ownedMain)
    {
        if (ownedMain) throw new MigrationMainOwnershipException(reason);
        throw new InvalidOperationException(reason);
    }

    private static void VerifyNamedHandle(SafeFileHandle handle, string expectedPath, string expectedRoot,
        MigrationEntryKind kind, bool ownedMain = false)
    {
        if (!Native.GetFileInformationByHandle(handle, out var info))
            NamespaceFailure("migration_handle_information_failed:" + Marshal.GetLastWin32Error(), ownedMain);
        if ((info.Attributes & (0x400u | 0x4000u)) != 0 || info.Links != 1 ||
            (((info.Attributes & 0x10u) != 0) != (kind == MigrationEntryKind.Directory)))
            NamespaceFailure("migration_handle_reparse_kind_or_links_rejected", ownedMain);
        var buffer = new StringBuilder(32768);
        var count = Native.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity)
            NamespaceFailure("migration_handle_final_name_unverifiable:" + Marshal.GetLastWin32Error(), ownedMain);
        string actual;
        try { actual = NamespacePath(buffer.ToString()); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            NamespaceFailure("migration_handle_final_namespace_rejected", ownedMain);
            throw; // Definite assignment only; NamespaceFailure always throws.
        }
        var expected = NamespacePath(expectedPath);
        var root = NamespacePath(expectedRoot);
        if (!Within(actual, root) || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            NamespaceFailure("migration_handle_namespace_mismatch", ownedMain);
    }

    private void VerifyPinnedDirectories(List<SafeFileHandle> pins)
    {
        // All pins in this store are directory handles. Namespace rename is denied
        // by their sharing mode; detect reparse/attribute conversion after callbacks.
        foreach (var pin in pins) Version(pin, MigrationEntryKind.Directory);
    }

    private void PinArtifactParents(TransactionHandle tx, string relativePath, List<SafeFileHandle> pins)
    {
        var components = relativePath.Replace('\\', '/').Split('/');
        for (var i = 1; i < components.Length; i++)
        {
            var relative = string.Join('/', components.Take(i));
            var path = Resolve(_artifactRoot, relative);
            // A previous artifact in this kernel transaction may have created this
            // directory. Reuse its pinned identity instead of creating it again.
            var alreadyPinned = false;
            foreach (var pin in pins)
            {
                var name = new StringBuilder(32768);
                var length = Native.GetFinalPathNameByHandleW(pin, name, (uint)name.Capacity, 0);
                if (length == 0 || length >= name.Capacity)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "pinned_artifact_parent_name");
                if (!string.Equals(NamespacePath(name.ToString()), NamespacePath(path), StringComparison.OrdinalIgnoreCase))
                    continue;
                VerifyNamedHandle(pin, path, _artifactRoot, MigrationEntryKind.Directory);
                Version(pin, MigrationEntryKind.Directory);
                alreadyPinned = true;
                break;
            }
            if (alreadyPinned) continue;
            var handle = Native.CreateFileTransactedW(path, 0x80, NoDeleteSharing, IntPtr.Zero, 3,
                BackupSemantics | OpenReparsePoint, IntPtr.Zero, tx, IntPtr.Zero, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error is not (2 or 3)) throw new Win32Exception(error, "pin_artifact_parent");
                // Atomic FILE_CREATE; a raced existing entry is refusal, not adoption.
                handle = CreateDirectoryInTransaction(tx, path, _artifactRoot);
            }
            pins.Add(handle);
            VerifyNamedHandle(handle, path, _artifactRoot, MigrationEntryKind.Directory);
            Version(handle, MigrationEntryKind.Directory);
        }
    }

    private void PinAndCreateParents(TransactionHandle tx, string relativePath,
        IReadOnlyDictionary<string, MigrationFileVersion>? expected, List<MigrationDirectoryEffect> created, List<SafeFileHandle> pins)
    {
        var components = relativePath.Replace('\\', '/').Split('/');
        for (var i = 1; i < components.Length; i++)
        {
            var rel = string.Join('/', components.Take(i));
            var path = Resolve(_configRoot, rel);
            var version = expected?.GetValueOrDefault(rel);
            if (version is { Exists: false })
            {
                // Obtain identity from the creation handle, not a second name lookup.
                var h = CreateDirectoryInTransaction(tx, path, _configRoot);
                pins.Add(h);
                created.Add(new(rel, version, Version(h, MigrationEntryKind.Directory)));
            }
            else
            {
                var h = Native.CreateFileW(path, 0x80, NoDeleteSharing, IntPtr.Zero, 3,
                    BackupSemantics | OpenReparsePoint, IntPtr.Zero);
                Valid(h, "pin_existing_parent");
                pins.Add(h);
                VerifyNamedHandle(h, path, _configRoot, MigrationEntryKind.Directory);
                var actual = Version(h, MigrationEntryKind.Directory);
                if (version is not null && !version.Matches(actual))
                    throw new InvalidOperationException("migration_parent_identity_mismatch:" + rel);
            }
        }
    }

    private MigrationFileVersion Version(SafeFileHandle handle, MigrationEntryKind kind)
    {
        Check(Native.GetFileInformationByHandle(handle, out var info), "file_identity");
        if ((info.Attributes & (0x400u | 0x4000u)) != 0 || info.Links != 1)
            throw new InvalidOperationException("migration_unsupported_link_or_encryption");
        if (((info.Attributes & 0x10) != 0) != (kind == MigrationEntryKind.Directory))
            throw new InvalidOperationException("migration_entry_kind_mismatch");
        var bytes = kind == MigrationEntryKind.File ? Read(handle) : null;
        return new(true, kind, _volume, info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow, info.Links,
            bytes?.LongLength, bytes is null ? null : MigrationFileVersion.Hash(bytes));
    }

    private MigrationFileVersion DirectoryVersion(string path)
    {
        using var handle = Native.CreateFileW(path, 0x80, 3, IntPtr.Zero, 3,
            BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        Valid(handle, "directory_identity");
        VerifyNamedHandle(handle, path, path, MigrationEntryKind.Directory);
        return Version(handle, MigrationEntryKind.Directory);
    }

    private PinSet PinRoots()
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            PinExistingPath(_configRoot, handles);
            PinExistingPath(_artifactRoot, handles);
            if (!_configIdentity.Matches(DirectoryVersion(_configRoot)) ||
                !_artifactIdentity.Matches(DirectoryVersion(_artifactRoot)))
                throw new InvalidOperationException("migration_root_identity_changed");
            return new PinSet(handles);
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            throw;
        }
    }

    private void PinExistingPath(string path, List<SafeFileHandle> pins)
    {
        var ancestors = new Stack<string>();
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            ancestors.Push(current);
        while (ancestors.Count > 0)
        {
            var current = ancestors.Pop();
            var handle = Native.CreateFileW(current, 0x80, 3, IntPtr.Zero, 3,
                BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            Valid(handle, "pin_ancestor");
            pins.Add(handle);
            VerifyNamedHandle(handle, current, current, MigrationEntryKind.Directory);
            Version(handle, MigrationEntryKind.Directory);
        }
    }

    private sealed class PinSet(List<SafeFileHandle> handles) : IDisposable
    {
        public List<SafeFileHandle> Detach()
        {
            var result = handles;
            handles = [];
            return result;
        }
        public void Dispose() { foreach (var handle in handles) handle.Dispose(); }
    }

    private static byte[] Read(SafeFileHandle handle)
    {
        using var alias = new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false);
        using var stream = new FileStream(alias, FileAccess.Read);
        stream.Position = 0;
        using var result = new MemoryStream();
        stream.CopyTo(result);
        return result.ToArray();
    }

    private static SafeFileHandle CreateDirectoryInTransaction(TransactionHandle transaction, string path, string expectedRoot)
    {
        var nativePath = "\\??\\" + path;
        var buffer = Marshal.StringToHGlobalUni(nativePath);
        var namePointer = IntPtr.Zero;
        var previous = Native.RtlGetCurrentTransaction();
        SafeFileHandle? returned = null;
        var added = false;
        try
        {
            var name = new Native.UnicodeString
            {
                Length = checked((ushort)(nativePath.Length * 2)),
                MaximumLength = checked((ushort)(nativePath.Length * 2 + 2)), Buffer = buffer
            };
            namePointer = Marshal.AllocHGlobal(Marshal.SizeOf<Native.UnicodeString>());
            Marshal.StructureToPtr(name, namePointer, false);
            var attributes = new Native.ObjectAttributes
            {
                Length = Marshal.SizeOf<Native.ObjectAttributes>(), ObjectName = namePointer, Attributes = 0x40
            };
            transaction.DangerousAddRef(ref added);
            if (!Native.RtlSetCurrentTransaction(transaction.DangerousGetHandle()))
                throw new InvalidOperationException("migration_transaction_context_unavailable");
            // Creation pins do not need DELETE access; avoiding it permits
            // later attributes-only opens with NoDeleteSharing in this transaction.
            var status = Native.NtCreateFile(out var handle, 0x100087, ref attributes, out _, IntPtr.Zero,
                0x10, NoDeleteSharing, 2, 0x4021 | OpenReparsePoint, IntPtr.Zero, 0);
            returned = handle;
            if (status < 0)
            {
                handle.Dispose();
                throw new Win32Exception((int)Native.RtlNtStatusToDosError(status), "create_owned_directory_handle");
            }
            try
            {
                VerifyNamedHandle(handle, path, expectedRoot, MigrationEntryKind.Directory);
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }
        finally
        {
            var restored = Native.RtlSetCurrentTransaction(previous);
            if (!restored) returned?.Dispose();
            if (added) transaction.DangerousRelease();
            if (namePointer != IntPtr.Zero) Marshal.FreeHGlobal(namePointer);
            Marshal.FreeHGlobal(buffer);
            if (!restored) throw new InvalidOperationException("migration_ambient_transaction_restore_failed");
        }
    }

    private static void Write(SafeFileHandle handle, byte[] bytes)
    {
        using var alias = new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false);
        using var stream = new FileStream(alias, FileAccess.ReadWrite);
        stream.Position = 0;
        stream.Write(bytes);
        stream.SetLength(bytes.LongLength);
        stream.Flush(true);
    }

    internal static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Replace('\\', '/').Split('/').Any(p => p is "" or "." or ".."))
            throw new ArgumentException("migration_unsafe_relative_path");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(path, root)) throw new ArgumentException("migration_path_outside_root");
        return path;
    }

    private static bool Within(string path, string root) => path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase) || string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
    internal static string CanonicalDirectory(string path)
    {
        using var handle = Native.CreateFileW(Path.GetFullPath(path), 0x80, 3, IntPtr.Zero, 3,
            BackupSemantics | 0x00200000, IntPtr.Zero);
        Valid(handle, "canonical_root");
        Check(Native.GetFileInformationByHandle(handle, out var info), "canonical_root_identity");
        if ((info.Attributes & (0x400u | 0x4000u)) != 0 || (info.Attributes & 0x10) == 0)
            throw new InvalidOperationException("migration_unsupported_root");
        var name = new StringBuilder(32768);
        var count = Native.GetFinalPathNameByHandleW(handle, name, (uint)name.Capacity, 0);
        if (count == 0 || count >= name.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error(), "canonical_root_name");
        var result = name.ToString();
        if (result.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("migration_remote_root_unsupported");
        return result.StartsWith("\\\\?\\", StringComparison.Ordinal) ? result[4..] : result;
    }
    internal static string VolumeOf(string path)
    {
        var root = new StringBuilder(1024);
        Check(Native.GetVolumePathNameW(path, root, root.Capacity), "volume_path");
        var volume = new StringBuilder(128);
        Check(Native.GetVolumeNameForVolumeMountPointW(root.ToString(), volume, volume.Capacity), "volume_identity");
        return volume.ToString();
    }
    private static TransactionHandle Begin()
    {
        var handle = Native.CreateTransaction(IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 30000, "Mistletoe version mutation");
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "create_transaction");
        return handle;
    }
    private static void Valid(SafeFileHandle handle, string operation) { if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), operation); }
    private static void Check(bool success, string operation) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), operation); }
    private static string Describe(Exception ex) => ex is Win32Exception native
        ? ex.Message + ":win32=" + native.NativeErrorCode : ex.Message;

    private sealed class TransactionHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public TransactionHandle() : base(true) { }
        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct UnicodeString
        {
            internal ushort Length, MaximumLength;
            internal IntPtr Buffer;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct ObjectAttributes
        {
            internal int Length;
            internal IntPtr RootDirectory, ObjectName;
            internal uint Attributes;
            internal IntPtr SecurityDescriptor, SecurityQualityOfService;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct IoStatusBlock
        {
            internal IntPtr Status;
            internal UIntPtr Information;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Information
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("KtmW32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern TransactionHandle CreateTransaction(IntPtr security, IntPtr id, uint options, uint isolation, uint flags, uint timeout, string description);
        [DllImport("KtmW32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CommitTransaction(TransactionHandle transaction);
        [DllImport("KtmW32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RollbackTransaction(TransactionHandle transaction);
        [DllImport("KtmW32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetTransactionId(TransactionHandle transaction, out Guid id);
        [DllImport("KtmW32.dll", SetLastError = true)] internal static extern TransactionHandle OpenTransaction(uint access, ref Guid id);
        [DllImport("KtmW32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetTransactionInformation(TransactionHandle transaction, out uint outcome,
            IntPtr isolationLevel, IntPtr isolationFlags, IntPtr timeout, uint bufferLength, IntPtr description);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFileTransactedW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template, TransactionHandle transaction, IntPtr version, IntPtr extended);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateDirectoryTransactedW(string? template, string path, IntPtr security, TransactionHandle transaction);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RemoveDirectoryTransactedW(string path, TransactionHandle transaction);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref byte data, uint bytes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetVolumePathNameW(string path, StringBuilder root, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetVolumeNameForVolumeMountPointW(string root, StringBuilder volume, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder name, uint size, uint flags);
        [DllImport("ntdll.dll")] internal static extern IntPtr RtlGetCurrentTransaction();
        [DllImport("ntdll.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool RtlSetCurrentTransaction(IntPtr transaction);
        [DllImport("ntdll.dll")] internal static extern uint RtlNtStatusToDosError(int status);
        [DllImport("ntdll.dll")] internal static extern int NtCreateFile(out SafeFileHandle handle, uint access,
            ref ObjectAttributes attributes, out IoStatusBlock status, IntPtr allocationSize, uint fileAttributes,
            uint share, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);
    }
}
