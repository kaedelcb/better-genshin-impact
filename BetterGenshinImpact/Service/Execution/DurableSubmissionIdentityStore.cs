using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BetterGenshinImpact.Service.Execution;

/// <summary>
/// R5 受理前身份账。只登记 Prepared 身份和原编号；不授予入队、物理槽或业务执行权。
/// 真正 Accepted 必须将身份、责任容量和物理槽许可合入同一权威事务后另行接线。
/// </summary>
internal sealed class DurableSubmissionIdentityStore
{
    private const int FormatVersion = 1;
    private const int FingerprintVersion = 1;
    private const int HeaderLength = 5 + 4 + 32;
    private const int ManifestLength = 5 + 4 + 32 + 16;
    private const int MaxPayloadLength = 16 * 1024;
    private const int MaxRecords = 4096;
    private static readonly byte[] Magic = "R5PI1"u8.ToArray();
    private static readonly byte[] ManifestMagic = "R5PM1"u8.ToArray();
    private readonly string _directory;
    private readonly int _capacity;
    private readonly Action<PreparedIdentityFaultPoint>? _fault;
    private string ManifestPath => Path.Combine(_directory, "index.lock");

    internal DurableSubmissionIdentityStore(string directory, int capacity = 4096,
        Action<PreparedIdentityFaultPoint>? fault = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (capacity is < 1 or > MaxRecords) throw new ArgumentOutOfRangeException(nameof(capacity));
        _directory = Path.GetFullPath(directory);
        _capacity = capacity;
        _fault = fault;
    }

    /// <summary>仅供受控空库配置；运行中的查询／登记绝不隐式重建丢失的存储域。</summary>
    internal bool InitializeFreshForProvisioning()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            if (Directory.EnumerateFiles(_directory, "*.prepared").Any()) return false;
            using var stream = new FileStream(ManifestPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 4096, FileOptions.WriteThrough);
            WriteManifest(stream, new Manifest(Guid.NewGuid(), 0, HashNames([])));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    internal string RecordPathFor((int ProcessId, long StartTicksUtc) targetEpoch, string key)
    {
        var identity = targetEpoch.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n"
            + targetEpoch.StartTicksUtc.ToString(CultureInfo.InvariantCulture) + "\n" + key;
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(_directory, name + ".prepared");
    }

    internal PreparedIdentityResult TryPrepare((int ProcessId, long StartTicksUtc) targetEpoch,
        string key, string payloadFingerprint, string operation)
    {
        if (!ValidIdentity(targetEpoch, key) || !ValidFingerprint(payloadFingerprint)
            || string.IsNullOrWhiteSpace(operation) || operation.Length > 128 || !ValidUnicode(operation))
            return new(PreparedIdentityStatus.Invalid, null, "invalid_identity");

        payloadFingerprint = payloadFingerprint.ToUpperInvariant();
        try
        {
            using var exclusive = OpenLock();
            var manifest = ReadManifest(exclusive);
            var recordsAtScan = ScanAndValidate(manifest);
            var count = recordsAtScan.Count;
            var path = RecordPathFor(targetEpoch, key);
            _fault?.Invoke(PreparedIdentityFaultPoint.AfterHistoryScan);
            var existing = ReadRecord(path);
            var existedAtScan = recordsAtScan.TryGetValue(Path.GetFileName(path), out var scanned);
            if (existedAtScan != (existing is not null) || (existedAtScan && scanned != existing))
                return new(PreparedIdentityStatus.Uncertain, null, "validated_record_changed");
            if (existing is not null)
            {
                if (existing.TargetProcessId != targetEpoch.ProcessId
                    || existing.TargetStartTicksUtc != targetEpoch.StartTicksUtc
                    || !string.Equals(existing.Key, key, StringComparison.Ordinal))
                    return new(PreparedIdentityStatus.Uncertain, null, "identity_collision_or_corruption");
                return string.Equals(existing.PayloadFingerprint, payloadFingerprint, StringComparison.Ordinal)
                       && string.Equals(existing.Operation, operation, StringComparison.Ordinal)
                    ? new(PreparedIdentityStatus.ExistingObserved, existing, null)
                    : new(PreparedIdentityStatus.Conflict, null, "idempotency_conflict");
            }

            if (count >= _capacity)
                return new(PreparedIdentityStatus.Capacity, null, "identity_capacity");

            var created = new PreparedSubmissionIdentity(FormatVersion, targetEpoch.ProcessId,
                targetEpoch.StartTicksUtc, key, FingerprintVersion, payloadFingerprint, operation,
                Guid.NewGuid(), DateTimeOffset.UtcNow, "Prepared");
            var bytes = Encode(created);
            // CreateNew 先占唯一文件名。写入失败留下残件，后续读取会保守停驻而不补发。
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            _fault?.Invoke(PreparedIdentityFaultPoint.AfterRecordFlush);
            var names = Directory.EnumerateFiles(_directory, "*.prepared")
                .Select(Path.GetFileName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Length != count + 1) throw new InvalidDataException("受理前记录计数在提交期间漂移");
            WriteManifest(exclusive, new Manifest(manifest.Incarnation, names.Length, HashNames(names)));
            _fault?.Invoke(PreparedIdentityFaultPoint.AfterManifestFlush);
            return new(PreparedIdentityStatus.Prepared, created, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OverflowException)
        {
            return new(PreparedIdentityStatus.Uncertain, null, ex.GetType().Name);
        }
    }

    internal PreparedIdentityLookupResult Query((int ProcessId, long StartTicksUtc) targetEpoch, string key)
    {
        if (!ValidIdentity(targetEpoch, key))
            return new(PreparedIdentityLookupStatus.Uncertain, null, "invalid_identity");
        try
        {
            using var exclusive = OpenLock();
            var manifest = ReadManifest(exclusive);
            var recordsAtScan = ScanAndValidate(manifest);
            var path = RecordPathFor(targetEpoch, key);
            var record = ReadRecord(path);
            var existedAtScan = recordsAtScan.TryGetValue(Path.GetFileName(path), out var scanned);
            if (existedAtScan != (record is not null) || (existedAtScan && scanned != record))
                return new(PreparedIdentityLookupStatus.Uncertain, null, "validated_record_changed");
            if (record is null)
                return new(PreparedIdentityLookupStatus.NotFound, null, null);
            if (record.TargetProcessId != targetEpoch.ProcessId
                || record.TargetStartTicksUtc != targetEpoch.StartTicksUtc
                || !string.Equals(record.Key, key, StringComparison.Ordinal))
                return new(PreparedIdentityLookupStatus.Uncertain, null, "identity_collision_or_corruption");
            return new(PreparedIdentityLookupStatus.Observed, record, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OverflowException)
        {
            return new(PreparedIdentityLookupStatus.Uncertain, null, ex.GetType().Name);
        }
    }

    private FileStream OpenLock() => new(Path.Combine(_directory, "index.lock"),
        FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    private sealed record Manifest(Guid Incarnation, int Count, byte[] NamesHash);

    private static byte[] HashNames(string[] names)
        => SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", names)));

    private static Manifest ReadManifest(FileStream stream)
    {
        if (stream.Length != ManifestLength) throw new InvalidDataException("受理前清单长度无效");
        var bytes = new byte[ManifestLength];
        stream.Position = 0;
        stream.ReadExactly(bytes);
        if (!bytes.AsSpan(0, 5).SequenceEqual(ManifestMagic))
            throw new InvalidDataException("受理前清单头无效");
        var count = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(5, 4));
        var incarnation = new Guid(bytes.AsSpan(41, 16));
        if (count < 0 || incarnation == Guid.Empty)
            throw new InvalidDataException("受理前清单字段无效");
        return new Manifest(incarnation, count, bytes.AsSpan(9, 32).ToArray());
    }

    private static void WriteManifest(FileStream stream, Manifest manifest)
    {
        var bytes = new byte[ManifestLength];
        ManifestMagic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(5, 4), manifest.Count);
        manifest.NamesHash.CopyTo(bytes, 9);
        manifest.Incarnation.TryWriteBytes(bytes.AsSpan(41, 16));
        stream.Position = 0;
        stream.Write(bytes);
        stream.SetLength(ManifestLength);
        stream.Flush(flushToDisk: true);
    }

    private Dictionary<string, PreparedSubmissionIdentity> ScanAndValidate(Manifest manifest)
    {
        var paths = Directory.EnumerateFiles(_directory, "*.prepared")
            .Take(MaxRecords + 1).OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length > MaxRecords) throw new InvalidDataException("受理前历史记录过多");
        if (paths.Length != manifest.Count || !CryptographicOperations.FixedTimeEquals(
                HashNames(paths.Select(Path.GetFileName).ToArray()), manifest.NamesHash))
            throw new InvalidDataException("受理前清单与历史文件不一致");
        var records = new Dictionary<string, PreparedSubmissionIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var record = ReadRecord(path) ?? throw new InvalidDataException("受理前记录扫描期间消失");
            if (!string.Equals(Path.GetFullPath(path), RecordPathFor(
                    (record.TargetProcessId, record.TargetStartTicksUtc), record.Key),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("受理前记录文件名与身份不一致");
            if (!records.TryAdd(Path.GetFileName(path), record))
                throw new InvalidDataException("受理前记录文件名重复");
        }
        return records;
    }

    private static bool ValidIdentity((int ProcessId, long StartTicksUtc) epoch, string key)
        => epoch.ProcessId > 0 && epoch.StartTicksUtc > 0
           && !string.IsNullOrWhiteSpace(key) && key.Length <= 512 && ValidUnicode(key);

    private static bool ValidUnicode(string value)
    {
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    private static bool ValidFingerprint(string fingerprint)
        => fingerprint is { Length: 64 } && fingerprint.All(Uri.IsHexDigit);

    private static byte[] Encode(PreparedSubmissionIdentity record)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(record);
        if (payload.Length > MaxPayloadLength) throw new InvalidDataException("受理前记录超长");
        var output = new byte[HeaderLength + payload.Length];
        Magic.CopyTo(output, 0);
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(5, 4), payload.Length);
        SHA256.HashData(payload).CopyTo(output, 9);
        payload.CopyTo(output, HeaderLength);
        return output;
    }

    private static PreparedSubmissionIdentity? ReadRecord(string path)
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < HeaderLength || stream.Length > HeaderLength + MaxPayloadLength)
                throw new InvalidDataException("受理前记录长度超界");
            bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("受理前记录出现尾随字节");
        }
        catch (FileNotFoundException) { return null; }
        if (bytes.Length < HeaderLength || !bytes.AsSpan(0, 5).SequenceEqual(Magic))
            throw new InvalidDataException("受理前记录头损坏");
        var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(5, 4));
        if (length < 1 || length > MaxPayloadLength || bytes.Length != HeaderLength + length)
            throw new InvalidDataException("受理前记录长度损坏");
        var payload = bytes.AsSpan(HeaderLength, length);
        var hash = SHA256.HashData(payload);
        if (!CryptographicOperations.FixedTimeEquals(hash, bytes.AsSpan(9, 32)))
            throw new InvalidDataException("受理前记录校验失败");
        var record = JsonSerializer.Deserialize<PreparedSubmissionIdentity>(payload);
        if (record is null || record.Version != FormatVersion || record.FingerprintVersion != FingerprintVersion
            || !ValidIdentity((record.TargetProcessId, record.TargetStartTicksUtc), record.Key)
            || !ValidFingerprint(record.PayloadFingerprint) || string.IsNullOrWhiteSpace(record.Operation)
            || !ValidUnicode(record.Operation)
            || record.Operation.Length > 128 || record.Handle == Guid.Empty
            || record.CreatedAtUtc == default || record.State != "Prepared")
            throw new InvalidDataException("受理前记录字段无效");
        return record;
    }
}

internal enum PreparedIdentityStatus { Prepared, ExistingObserved, Conflict, Capacity, Invalid, Uncertain }
internal enum PreparedIdentityLookupStatus { Observed, NotFound, Uncertain }
internal enum PreparedIdentityFaultPoint { AfterHistoryScan, AfterRecordFlush, AfterManifestFlush }

internal sealed record PreparedSubmissionIdentity(int Version, int TargetProcessId, long TargetStartTicksUtc,
    string Key, int FingerprintVersion, string PayloadFingerprint, string Operation,
    Guid Handle, DateTimeOffset CreatedAtUtc, string State);

internal readonly record struct PreparedIdentityResult(
    PreparedIdentityStatus Status, PreparedSubmissionIdentity? Record, string? Reason);
internal readonly record struct PreparedIdentityLookupResult(
    PreparedIdentityLookupStatus Status, PreparedSubmissionIdentity? Record, string? Reason);
