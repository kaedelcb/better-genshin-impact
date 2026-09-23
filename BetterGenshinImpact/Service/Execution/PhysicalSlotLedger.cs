using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace BetterGenshinImpact.Service.Execution;

/// <summary>
/// R5 物理槽底座：同一槽的跨进程文件句柄排他，并在持锁期间发布新的代际。
/// 这里只授予槽记录写权；业务执行许可、子执行者退出和生产入口接线另行实现。
/// </summary>
internal sealed class PhysicalSlotLedger
{
    private const int FormatVersion = 1;
    private readonly string _directory;
    private readonly string _slotId;

    /// <summary>同一 Windows 用户及登录会话中的 BGI 进程使用同一保守槽。</summary>
    public static PhysicalSlotLedger ForCurrentWindowsSession()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
            throw new InvalidOperationException("无法确定当前用户的 LocalApplicationData，物理槽不可用。");
        var directory = Path.Combine(localData, "BetterGI", "R5PhysicalSlot");
        return new PhysicalSlotLedger(directory, "session-" + Process.GetCurrentProcess().SessionId + "-genshin");
    }

    internal PhysicalSlotLedger(string directory, string slotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotId);
        if (!slotId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            throw new ArgumentException("槽身份只能包含 ASCII 字母、数字、连字符和下划线。", nameof(slotId));
        _directory = Path.GetFullPath(directory);
        _slotId = slotId;
    }

    internal string RecordPath => Path.Combine(_directory, _slotId + ".state.json");
    internal string LockPath => Path.Combine(_directory, _slotId + ".lock");
    internal string ProvisioningPath => Path.Combine(_directory, _slotId + ".provisioning");

    /// <summary>仅供受控首次配置；运行取得不得据空目录自行初始化。</summary>
    internal bool InitializeFreshForProvisioning()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            if (File.Exists(LockPath) || File.Exists(RecordPath) || File.Exists(ProvisioningPath))
                return false;
            using (var marker = new FileStream(ProvisioningPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                marker.WriteByte(1);
                marker.Flush(flushToDisk: true);
            }
            using var heldLock = new FileStream(LockPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 4096, FileOptions.WriteThrough);
            var initial = new SlotRecord(FormatVersion, _slotId, 0, Guid.NewGuid(), 0, 0);
            using var state = new FileStream(RecordPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough);
            JsonSerializer.Serialize(state, initial);
            state.Flush(flushToDisk: true);
            heldLock.Flush(flushToDisk: true);
            File.Delete(ProvisioningPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public PhysicalSlotAcquireResult TryAcquire()
    {
        (int ProcessId, long StartTicksUtc) ownerEpoch;
        try
        {
            using var current = Process.GetCurrentProcess();
            ownerEpoch = (current.Id, current.StartTime.ToUniversalTime().Ticks);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return new(PhysicalSlotAcquireStatus.Uncertain, null, "owner_epoch_unavailable");
        }
        if (ownerEpoch.ProcessId <= 0 || ownerEpoch.StartTicksUtc <= 0)
            return new(PhysicalSlotAcquireStatus.Uncertain, null, "invalid_owner_epoch");

        FileStream? heldLock = null;
        try
        {
            try
            {
                heldLock = new FileStream(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                return new(PhysicalSlotAcquireStatus.Busy, null, "slot_lock_held");
            }

            if (Directory.EnumerateFiles(_directory, Path.GetFileName(ProvisioningPath)).Any())
                return new(PhysicalSlotAcquireStatus.Uncertain, null, "incomplete_provisioning");

            // 旧写入残件意味着提交点不明；不靠猜测重建代际。
            if (Directory.EnumerateFiles(_directory, Path.GetFileName(RecordPath) + ".tmp-*").Any())
                return new(PhysicalSlotAcquireStatus.Uncertain, null, "publish_residue");

            long previousGeneration = 0;
            SlotRecord? previous;
            try
            {
                // File.Exists 会把访问失败也折成 false；只有真正的 FileNotFound 才能初始化。
                var stateJson = File.ReadAllText(RecordPath);
                using var parsed = JsonDocument.Parse(stateJson);
                var root = parsed.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty(nameof(SlotRecord.Generation), out _)
                    || !root.TryGetProperty(nameof(SlotRecord.OwnerProcessId), out _)
                    || !root.TryGetProperty(nameof(SlotRecord.OwnerStartTicksUtc), out _))
                    return new(PhysicalSlotAcquireStatus.Uncertain, null, "incomplete_slot_record");
                previous = JsonSerializer.Deserialize<SlotRecord>(stateJson);
                if (previous is null || previous.Version != FormatVersion || previous.SlotId != _slotId
                    || previous.Generation < 0 || previous.Nonce == Guid.Empty
                    || (previous.Generation == 0 && (previous.OwnerProcessId != 0 || previous.OwnerStartTicksUtc != 0))
                    || (previous.Generation > 0 && (previous.OwnerProcessId <= 0 || previous.OwnerStartTicksUtc <= 0))
                    || previous.Generation == long.MaxValue)
                    return new(PhysicalSlotAcquireStatus.Uncertain, null, "invalid_slot_record");
                previousGeneration = previous.Generation;
            }
            catch (FileNotFoundException)
            {
                return new(PhysicalSlotAcquireStatus.Uncertain, null, "missing_slot_record");
            }

            var next = new SlotRecord(FormatVersion, _slotId, checked(previousGeneration + 1),
                Guid.NewGuid(), ownerEpoch.ProcessId, ownerEpoch.StartTicksUtc);
            var tempPath = RecordPath + ".tmp-" + Guid.NewGuid().ToString("N");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next);
            using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                temp.Write(bytes);
                temp.Flush(flushToDisk: true);
            }
            File.Move(tempPath, RecordPath, overwrite: true);

            var lease = new PhysicalSlotLease(_slotId, next.Generation, next.Nonce, ownerEpoch, heldLock);
            heldLock = null;
            return new(PhysicalSlotAcquireStatus.Acquired, lease, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or OverflowException)
        {
            return new(PhysicalSlotAcquireStatus.Uncertain, null, ex.GetType().Name);
        }
        finally
        {
            heldLock?.Dispose();
        }
    }

    private sealed record SlotRecord(int Version, string SlotId, long Generation, Guid Nonce,
        int OwnerProcessId, long OwnerStartTicksUtc);
}

internal enum PhysicalSlotAcquireStatus { Acquired, Busy, Uncertain }

internal readonly record struct PhysicalSlotAcquireResult(
    PhysicalSlotAcquireStatus Status, PhysicalSlotLease? Lease, string? Reason);

/// <summary>持有锁文件句柄期间才有效；Dispose 只释放本进程锁，不证明子执行树退出。</summary>
internal sealed class PhysicalSlotLease : IDisposable
{
    private FileStream? _heldLock;

    internal PhysicalSlotLease(string slotId, long generation, Guid nonce,
        (int ProcessId, long StartTicksUtc) ownerEpoch, FileStream heldLock)
    {
        SlotId = slotId;
        Generation = generation;
        Nonce = nonce;
        OwnerEpoch = ownerEpoch;
        _heldLock = heldLock;
    }

    public string SlotId { get; }
    public long Generation { get; }
    public Guid Nonce { get; }
    public (int ProcessId, long StartTicksUtc) OwnerEpoch { get; }
    public void Dispose() => Interlocked.Exchange(ref _heldLock, null)?.Dispose();
}
