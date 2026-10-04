using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace MultiplayerHoeingAssistant.Services;

public enum MigrationEntryKind { File, Directory }

/// <summary>Exact version, including namespace identity; absence never grants ownership.</summary>
public sealed record MigrationFileVersion(bool Exists, MigrationEntryKind Kind, string? VolumeGuid,
    uint? VolumeSerial, ulong? FileId, uint? LinkCount, long? Length, string? Sha256)
{
    public static MigrationFileVersion Absent(MigrationEntryKind kind = MigrationEntryKind.File)
        => new(false, kind, null, null, null, null, null, null);

    public bool Matches(MigrationFileVersion other) => Exists == other.Exists && Kind == other.Kind &&
        (!Exists || (string.Equals(VolumeGuid, other.VolumeGuid, StringComparison.OrdinalIgnoreCase) &&
        VolumeSerial == other.VolumeSerial && FileId == other.FileId && LinkCount == other.LinkCount &&
        Length == other.Length && string.Equals(Sha256, other.Sha256, StringComparison.Ordinal)));

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed record MigrationDirectoryEffect(string Path, MigrationFileVersion Input, MigrationFileVersion Output);
public sealed record MigrationInputBytes(MigrationFileVersion Version, byte[] Bytes);
public sealed record MigrationArtifactWrite(string Path, byte[] Bytes, string? ExpectedSha256 = null)
{
    internal MigrationInputBytes? ExpectedInput { get; init; }
    internal MigrationMainOwnership? MainOwnership { get; init; }
}

internal sealed class MigrationMainOwnership
{
    internal MigrationInputBytes Input { get; private set; }
    internal MigrationMainOwnership(MigrationInputBytes input)
        => Input = new(input.Version, (byte[])input.Bytes.Clone());
    // Output was captured from the actual TxF handle before confirmed commit.
    internal void Confirm(MigrationInputBytes output) => Input = output;
}

internal sealed class MigrationMainOwnershipException : IOException
{
    internal MigrationMainOwnershipException(string reason) : base(reason) { }
}

/// <summary>Prepared callback must publish intent before returning this immutable data instruction.</summary>
public sealed record MigrationWriteInstruction(byte[]? Bytes, bool Delete,
    Func<MigrationFileVersion, IReadOnlyList<MigrationDirectoryEffect>, IReadOnlyList<MigrationArtifactWrite>> AppliedArtifacts);

public sealed record MigrationVersionMutationResult(bool Success, string Reason, bool CommitAttempted,
    bool CommitConfirmed, MigrationFileVersion? Output, IReadOnlyList<MigrationDirectoryEffect> Directories);

public interface IMigrationVersionStore
{
    MigrationFileVersion ReadVersion(string relativePath, MigrationEntryKind kind = MigrationEntryKind.File);
    MigrationVersionMutationResult ExecuteFile(string relativePath, MigrationFileVersion input,
        Func<byte[], MigrationFileVersion, IReadOnlyList<MigrationDirectoryEffect>, MigrationWriteInstruction> prepare,
        IReadOnlyDictionary<string, MigrationFileVersion>? parents = null, Action<string>? fault = null,
        Action<Guid>? transactionCreated = null);
    MigrationVersionMutationResult RemoveDirectory(string relativePath, MigrationFileVersion input,
        Func<MigrationFileVersion, IReadOnlyList<MigrationArtifactWrite>> appliedArtifacts, Action<string>? fault = null,
        Action<Guid>? transactionCreated = null, IReadOnlyDictionary<string, MigrationFileVersion>? parents = null);
    void WriteArtifacts(IReadOnlyList<MigrationArtifactWrite> writes);
}
