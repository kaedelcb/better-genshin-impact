using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>等待队列文件不可读／损坏／版本不支持／契约冲突（响亮拒绝，不降级按空队列处理）。</summary>
public sealed class LocalWaitQueueCorruptException(string message) : Exception(message);

/// <summary>清理结果：因失效被取消的条数 + 因保留期到期被裁剪的墓碑条数。</summary>
public sealed record LocalWaitCleanupResult(int Cancelled, int Pruned);

/// <summary>
/// 槲寄生 · R5 批次 6：本地持久等待队列的落盘载体（独立文件 wait-queue.json）。
/// 边界（如实登记）：不改动冻结的租约格式代，也不新增第二个"责任"权威——本文件只承载**调度意愿**，
/// 不含发送许可、不参与责任判定。
/// 合同：①**严格读取**——`version`/`items` 必需、版本必须在支持范围、元素不得为空、`itemId` 必须唯一且非空、
/// 枚举必须是已定义值，缺失/非法一律响亮拒绝；②**单写者**——写路径对**同一路径**取进程级锁
/// （多实例也不会互相覆盖），原子写＝临时文件 + `File.Move(overwrite)`，写失败不破坏原文件；
/// ③**取消墓碑保留 24h** 后裁剪（防无界增长），保留期内可被同身份同载荷重登记**重新激活**。
/// </summary>
public sealed class LocalWaitQueueStore
{
    /// <summary>取消墓碑保留期（超过即裁剪；可由夹具注入更短值）。</summary>
    public static readonly TimeSpan DefaultCancelledRetention = TimeSpan.FromHours(24);

    private static readonly ConcurrentDictionary<string, object> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync;
    private readonly string _file;

    public LocalWaitQueueStore(string directory, TimeSpan? cancelledRetention = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _file = Path.GetFullPath(Path.Combine(directory, "wait-queue.json"));
        _sync = PathLocks.GetOrAdd(_file, _ => new object());
        CancelledRetention = cancelledRetention ?? DefaultCancelledRetention;
    }

    public string FilePath => _file;

    public TimeSpan CancelledRetention { get; }

    /// <summary>
    /// 读取等待集合。文件确实不存在 ⇒ 空集合；不可读/无法解析/结构或版本非法 ⇒
    /// <see cref="LocalWaitQueueCorruptException"/>（原件保留，不当作空队列放行）。
    /// </summary>
    public IReadOnlyList<LocalWaitItem> Load()
    {
        lock (_sync)
        {
            string text;
            try
            {
                // 只有"文件确实不存在"才是空集合；其余 I/O／权限问题按损坏处理（不得当作空队列放行）。
                text = File.ReadAllText(_file);
            }
            catch (FileNotFoundException)
            {
                return [];
            }
            catch (DirectoryNotFoundException)
            {
                return [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LocalWaitQueueCorruptException($"等待队列文件不可读（原件保留）：{ex.Message}");
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new LocalWaitQueueCorruptException($"等待队列文件 JSON 非法（原件保留）：{ex.Message}");
            }

            if (node is not JsonObject root) throw new LocalWaitQueueCorruptException("等待队列文件根节点必须是对象（原件保留）。");
            if (root["version"] is not JsonValue versionValue || !versionValue.TryGetValue<int>(out var version))
                throw new LocalWaitQueueCorruptException("等待队列文件缺少整数 version 字段（原件保留）。");
            if (version < LocalWaitQueueFile.MinimumSupportedVersion || version > LocalWaitQueueFile.CurrentVersion)
                throw new LocalWaitQueueCorruptException(
                    $"等待队列文件版本 {version} 不在支持范围 [{LocalWaitQueueFile.MinimumSupportedVersion},{LocalWaitQueueFile.CurrentVersion}]：响亮拒绝。");
            if (root["items"] is not JsonArray items)
                throw new LocalWaitQueueCorruptException("等待队列文件缺少 items 数组（原件保留）。");

            var parsed = new List<LocalWaitItem>(items.Count);
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in items)
            {
                if (element is not JsonObject itemObject)
                    throw new LocalWaitQueueCorruptException("等待队列存在非对象元素（原件保留）。");
                var item = ParseItem(itemObject);
                if (!seenIds.Add(item.ItemId))
                    throw new LocalWaitQueueCorruptException($"等待队列存在重复 itemId：{item.ItemId}（原件保留）。");
                parsed.Add(item);
            }

            return parsed;
        }
    }

    /// <summary>
    /// 幂等登记：同 <c>ItemId</c> 且**不可变登记载荷**（身份/候选号/命名空间/流程/级别/优先级/最高级标记/可信标记/计划时刻）
    /// 完全一致 ⇒ 复用（返回 false，不新增）；等待中但载荷不同 ⇒ 响亮冲突；
    /// 已取消的同载荷项 ⇒ **重新激活**（视为同一请求再次到来，返回 true）。
    /// </summary>
    public bool Upsert(LocalWaitItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.ItemId) || string.IsNullOrWhiteSpace(item.StableIdentity))
            throw new ArgumentException("等待项必须携带 ItemId 与 StableIdentity", nameof(item));

        // [批次 16／D2] **写入侧与读取侧同口径**（会诊 #1 必改）：`ParsePrerequisiteReference` 把
        // 「键存在但形状非法（非字符串／空白）」判为损坏，写入侧就必须同样**响亮拒绝**，
        // 否则 `Upsert` 能成功写出自己随后 `Load` 拒读的文件（写入成功、重启即损坏＝把未知结果改写成成功）。
        // 拒绝发生在**取锁与读盘之前**：零副作用，原文件逐字节不变。
        ValidatePrerequisiteReferenceShape(item.PrerequisiteReference, item.ItemId);

        lock (_sync)
        {
            var items = Load().ToList();
            var existing = items.FirstOrDefault(i => string.Equals(i.ItemId, item.ItemId, StringComparison.Ordinal));
            if (existing is null)
            {
                items.Add(item);
                Persist(items);
                return true;
            }

            if (!HasSameRegistrationPayload(existing, item))
            {
                throw new LocalWaitQueueCorruptException(
                    $"等待项 {item.ItemId} 已存在且登记载荷不同：拒绝覆盖（原记录保留）。");
            }

            if (existing.State == LocalWaitItemState.Waiting) return false;

            existing.State = LocalWaitItemState.Waiting;
            existing.Reason = null;
            existing.CancelledAtUtc = null;
            Persist(items);
            return true;
        }
    }

    /// <summary>移除指定等待项（终局：已执行/被取消/失效清理）；不存在返回 false。</summary>
    public bool Remove(string itemId)
    {
        lock (_sync)
        {
            var items = Load().ToList();
            var removed = items.RemoveAll(i => string.Equals(i.ItemId, itemId, StringComparison.Ordinal)) > 0;
            if (removed) Persist(items);
            return removed;
        }
    }

    /// <summary>
    /// 应用失效清理（置 Cancelled 并记录原因/时刻），并按保留期裁剪过期墓碑；有变化才写盘。
    /// 写入失败不会破坏原文件（先写临时文件，成功后才替换）。
    /// </summary>
    public LocalWaitCleanupResult PersistCleanup(Func<LocalWaitItem, string?> invalidationReason, DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            var items = Load().ToList();
            var decisions = LocalWaitQueuePolicy.Cleanup(items, invalidationReason, nowUtc);
            foreach (var decision in decisions)
            {
                var target = items.First(i => ReferenceEquals(i, decision.Item));
                target.State = LocalWaitItemState.Cancelled;
                target.Reason = decision.Reason;
                target.CancelledAtUtc = decision.DecidedAtUtc;
            }

            var cutoff = nowUtc - CancelledRetention;
            var pruned = items.RemoveAll(i => i.State == LocalWaitItemState.Cancelled
                                              && i.CancelledAtUtc is { } cancelledAt
                                              && cancelledAt <= cutoff);

            if (decisions.Count > 0 || pruned > 0) Persist(items);
            return new LocalWaitCleanupResult(decisions.Count, pruned);
        }
    }

    /// <summary>不可变登记载荷逐字段比较（登记时刻、状态与原因属生命周期，不参与）。</summary>
    private static bool HasSameRegistrationPayload(LocalWaitItem left, LocalWaitItem right)
        => string.Equals(left.StableIdentity, right.StableIdentity, StringComparison.Ordinal)
           && string.Equals(left.CandidateId, right.CandidateId, StringComparison.Ordinal)
           && string.Equals(left.Namespace, right.Namespace, StringComparison.Ordinal)
           && string.Equals(left.WorkflowId, right.WorkflowId, StringComparison.Ordinal)
           && left.Tier == right.Tier
           && left.Priority == right.Priority
           && left.IsHoeingHighest == right.IsHoeingHighest
           && left.HasTrustedIdentity == right.HasTrustedIdentity
           && Nullable.Compare(left.ScheduledAt, right.ScheduledAt) == 0
           && HasSamePrerequisiteReference(left.PrerequisiteReference, right.PrerequisiteReference);

    /// <summary>严格解析单条等待项：必需字段缺失、类型错误或枚举未定义一律响亮拒绝（缺失 hasTrustedIdentity ⇒ 保守 false）。</summary>
    private static LocalWaitItem ParseItem(JsonObject itemObject)
    {
        var itemId = RequiredString(itemObject, "itemId");
        var stableIdentity = RequiredString(itemObject, "stableIdentity");
        var tierRaw = RequiredInt(itemObject, "tier");
        if (!Enum.IsDefined(typeof(ArbitrationTier), tierRaw))
            throw new LocalWaitQueueCorruptException($"等待项 {itemId} 的 tier={tierRaw} 不是已定义取值（原件保留）。");

        int stateRaw;
        // 键**存在**即必须是整数：JSON null／字符串／数组都算损坏（不得落到缺省 Waiting）
        if (itemObject.ContainsKey("state"))
        {
            if (itemObject["state"] is not JsonValue stateValue || !stateValue.TryGetValue<int>(out stateRaw))
                throw new LocalWaitQueueCorruptException($"等待项 {itemId} 的 state 不是整数（原件保留）。");
        }
        else
        {
            stateRaw = 0; // 缺省＝Waiting
        }

        if (!Enum.IsDefined(typeof(LocalWaitItemState), stateRaw))
            throw new LocalWaitQueueCorruptException($"等待项 {itemId} 的 state={stateRaw} 不是已定义取值（原件保留）。");

        return new LocalWaitItem
        {
            ItemId = itemId,
            StableIdentity = stableIdentity,
            CandidateId = OptionalString(itemObject, "candidateId") ?? "",
            Namespace = OptionalString(itemObject, "namespace") ?? "",
            WorkflowId = OptionalString(itemObject, "workflowId") ?? "",
            Tier = (ArbitrationTier)tierRaw,
            Priority = RequiredInt(itemObject, "priority"),
            IsHoeingHighest = OptionalBool(itemObject, "isHoeingHighest") ?? false,
            // 缺失可信标记 ⇒ 保守按不可信处理（不得默认可信）
            HasTrustedIdentity = OptionalBool(itemObject, "hasTrustedIdentity") ?? false,
            ScheduledAt = OptionalDateTimeOffset(itemObject, "scheduledAt"),
            EnqueuedAtUtc = OptionalDateTimeOffset(itemObject, "enqueuedAtUtc") ?? default,
            CancelledAtUtc = OptionalDateTimeOffset(itemObject, "cancelledAtUtc"),
            State = (LocalWaitItemState)stateRaw,
            Reason = OptionalString(itemObject, "reason"),
            // [批次 16／D2] 持久化稳定前置引用（稳定引用串）：键**存在**即必须是**非空字符串**
            // （JSON null／数字／对象／数组都算损坏，不得落到「缺字段＝不可判定」这一合法默认上）；
            // 缺字段 ⇒ null（v1 旧文件读兼容，保守按不可判定，不得默认就绪）。
            PrerequisiteReference = ParsePrerequisiteReference(itemObject, itemId),
        };
    }

    /// <summary>
    /// [批次 16／D2] **写入侧形状校验**：与 <see cref="ParsePrerequisiteReference"/> 完全同口径。
    /// 缺省（<c>null</c>）合法＝不可判定（v1 旧文件兼容）；给定则必须**非空白字符串**。
    /// 形状非法一律 <see cref="LocalWaitQueueCorruptException"/>（响亮拒绝，零副作用）。
    /// </summary>
    private static void ValidatePrerequisiteReferenceShape(string? reference, string itemId)
    {
        if (reference is null) return;
        if (string.IsNullOrWhiteSpace(reference))
            throw new LocalWaitQueueCorruptException(
                $"等待项 {itemId} 的 prerequisiteReference 为空字符串：写入侧拒绝（该形状读取侧判损坏，不得写出）。");
    }

    /// <summary>
    /// [批次 16／D2] 严格解析**持久化稳定前置引用**（稳定引用串）。
    /// 缺字段／显式 null ⇒ null（**保守按不可判定**，不得默认就绪；v1 旧文件即走此路径）。
    /// 键存在则其值必须是**非空字符串** —— 形状非法（数字／对象／数组／空串）一律响亮拒绝，不降级解析。
    /// **就绪**永不落盘：本字段只回答「前置是谁」，就绪由只读 evaluator 在读取时求得。
    /// </summary>
    private static string? ParsePrerequisiteReference(JsonObject itemObject, string itemId)
    {
        if (!itemObject.ContainsKey("prerequisiteReference")) return null;
        if (itemObject["prerequisiteReference"] is null) return null;
        if (itemObject["prerequisiteReference"] is not JsonValue node || !node.TryGetValue<string>(out var value))
            throw new LocalWaitQueueCorruptException(
                $"等待项 {itemId} 的 prerequisiteReference 不是字符串（原件保留）。");
        if (string.IsNullOrWhiteSpace(value))
            throw new LocalWaitQueueCorruptException(
                $"等待项 {itemId} 的 prerequisiteReference 为空字符串（原件保留）。");

        return value;
    }

    /// <summary>[批次 16／D2] 前置引用逐字符一致（ordinal；稳定身份口径）；两侧皆缺省视为一致。</summary>
    private static bool HasSamePrerequisiteReference(string? left, string? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static string RequiredString(JsonObject o, string name)
    {
        if (OptionalString(o, name) is { Length: > 0 } value) return value;
        throw new LocalWaitQueueCorruptException($"等待队列记录缺少非空 {name}（原件保留）。");
    }

    private static int RequiredInt(JsonObject o, string name)
    {
        if (o[name] is JsonValue value && value.TryGetValue<int>(out var parsed)) return parsed;
        throw new LocalWaitQueueCorruptException($"等待队列记录缺少整数 {name}（原件保留）。");
    }

    private static string? OptionalString(JsonObject o, string name)
        => o[name] is JsonValue value && value.TryGetValue<string>(out var parsed) ? parsed : null;

    private static bool? OptionalBool(JsonObject o, string name)
        => o[name] is JsonValue value && value.TryGetValue<bool>(out var parsed) ? parsed : null;

    private static DateTimeOffset? OptionalDateTimeOffset(JsonObject o, string name)
        => o[name] is JsonValue value && value.TryGetValue<DateTimeOffset>(out var parsed) ? parsed : null;

    private void Persist(List<LocalWaitItem> items)
    {
        var directory = Path.GetDirectoryName(_file)!;
        Directory.CreateDirectory(directory);
        var payload = Utf8NoBom.GetBytes(JsonSerializer.Serialize(new LocalWaitQueueFile { Items = items }, JsonOptions));
        var tmp = Path.Combine(directory, $".wait-queue.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tmp, payload);
            File.Move(tmp, _file, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (IOException)
            {
                // 残件不影响读取路径（Load 只读固定文件名）
            }
        }
    }
}

