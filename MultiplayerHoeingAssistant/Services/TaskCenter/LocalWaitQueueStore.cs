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
/// 枚举必须是已定义值，缺失/非法一律响亮拒绝；②**单写者（口径限同进程）**——写路径对**同一路径**取
/// **进程级**锁（`PathLocks` 为进程内静态表）⇒ **同进程**并发写者不会互相覆盖；**跨进程不保证**：
/// 两个进程各自持锁、互不相知，Upsert 的 Load→改→Persist 读改写序列在跨进程并发下会 lost update
/// （原子 `File.Move` 只保证文件不撕裂，后写者整篇覆盖前者）⇒ **跨进程访问须由调用方保证单写者**
/// （[批次 20／Wave4 C9] 合同口径，R17 建议-3 提前落地）；原子写＝临时文件 + `File.Move(overwrite)`，
/// 写失败不破坏原文件；
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

#if DEBUG
    /// <summary>
    /// [批次 16／D2 第二轮会诊 #2 判别力取证专用] 测试探针：在 <see cref="Persist"/> **校验之前**调用。
    /// 仅存在于 DEBUG 构建（生产 Release 构建下该字段与调用点均被编译剔除）。默认 null ⇒ 无行为。
    /// </summary>
    internal static Action<List<LocalWaitItem>>? WriteSnapshotProbeMutator;
#endif
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
        //
        // [批次 16／D2 第二轮会诊 #2 必改] **不得只校验一次可变对象**：`item` 是调用方持有的**可变对象**，
        // 若在「校验之后、序列化之前」被改写为非法形状，仍会写出 `Load` 拒读的文件（写入成功、重启即损坏）。
        // 因此①此处只做**快速失败**（尽早响亮拒绝，零副作用）；②**真正**的守卫是在持锁、读盘之后，
        // 对「即将写入的那个对象」**在写盘前最后一次**校验（见下 `ValidatePersistableItems`）——
        // 该点在 `Persist` 之前且不可被其它线程插入，故不变量为「**已写出的文件必定可被 `Load` 读回**」。
        ValidatePrerequisiteReferenceShape(item.PrerequisiteReference, item.ItemId);
        // [批次 20／C3] admissionIdentity 同口径快速失败（R3-F6 建议采纳：拒绝时点提前到取锁前；
        // 真正守卫仍是 Persist 前的 MaterializeAndValidatePayload，不变量不变）。
        ValidateAdmissionIdentityShape(item.AdmissionIdentity, item.ItemId);

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
            // [批次 20／Wave2／D-E2=①] 重激活＝新代际：取消→同载荷重登记时代际**递增**（取消→重激活
            // 产生 N 个代际 ⇒ _handled 按代际修剪有界；消费前复核按代际判 ABA 过期）。**重激活分支的
            // 代际由 Store 递增收敛（调用方不可自报）**；**新登记分支**以调用方值为准（生产唯一调用方
            // TryRegisterLocalWait 恒为默认 0；[Wave2 R33 建议-1] 文档收窄）。
            existing.Generation = existing.Generation + 1;
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
           // [Wave1 R11 F-D] 非空注解字段按归一值比较（null ≡ ""）：与读取侧 ParseItem 的 `?? ""`
           // 及写侧物化归一同一口径，否则内存 null vs 读回 "" 会误判「载荷不同」破坏幂等登记。
           && string.Equals(left.CandidateId ?? "", right.CandidateId ?? "", StringComparison.Ordinal)
           // [批次 20／C4①·Wave1 会诊 F3] 准入绑定属登记载荷：补全/漂移都必须响亮冲突，
           // 不得被无声吞掉（重新激活只重置状态，不改写载荷）。
           && string.Equals(left.AdmissionIdentity, right.AdmissionIdentity, StringComparison.Ordinal)
           && string.Equals(left.Namespace ?? "", right.Namespace ?? "", StringComparison.Ordinal)
           && string.Equals(left.WorkflowId ?? "", right.WorkflowId ?? "", StringComparison.Ordinal)
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
            // [批次 20／C3/C4①] 准入面稳定身份（9 元组）：键**存在**即必须是**非空字符串**
            // （形状非法一律损坏，与前置引用同口径）；缺字段 ⇒ null（版本 1/2 旧文件读兼容 ⇒
            // 登记例外：永不参选＋显式标注，C4②/D-E3=(c)）。
            AdmissionIdentity = ParseAdmissionIdentity(itemObject, itemId),
            Generation = ParseGeneration(itemObject, itemId),
        };
    }

    /// <summary>
    /// [批次 20／C3/C4①] 严格解析**准入面稳定身份**（9 元组字符串；与
    /// <see cref="ParsePrerequisiteReference"/> 完全同口径）。
    /// 缺字段／显式 null ⇒ null（版本 1/2 旧文件读兼容 ⇒ 登记例外：永不参选，不得默认可翻译）；
    /// 键存在且为**非字符串值**（数字／对象／数组）或**空白串** ⇒ 响亮拒绝，不降级解析。
    /// **显式 JSON null ⇒ null（R9-F2 口径，fail-closed 非「损坏」）**：当前格式（v3）文件中
    /// null 绑定是**合法持久化形态**——C4② 合同前存量项经重激活路径在 v3 文件中保留 null 绑定
    /// （<c>Upsert_V1LegacyReactivation</c> 夹具钉死；owner 裁决 D-E3=(c) 要求存量留存且永不参选）。
    /// 若判 v3 显式 null 为损坏，重激活路径即被破坏。代价：篡改/半写坏只能把项**降级**为
    /// 「永不参选」（保守方向），不会产生发送面后果——这是无 MAC 存储的信任边界内正确取向。
    /// </summary>
    private static string? ParseAdmissionIdentity(JsonObject itemObject, string itemId)
    {
        if (!itemObject.ContainsKey("admissionIdentity")) return null;
        if (itemObject["admissionIdentity"] is null) return null;
        if (itemObject["admissionIdentity"] is not JsonValue node || !node.TryGetValue<string>(out var value))
            throw new LocalWaitQueueCorruptException(
                $"等待项 {itemId} 的 admissionIdentity 不是字符串（原件保留）。");
        if (string.IsNullOrWhiteSpace(value))
            throw new LocalWaitQueueCorruptException(
                $"等待项 {itemId} 的 admissionIdentity 为空字符串（原件保留）。");

        return value;
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
    /// [批次 20／Wave2／D-E2=①] 代际解析：缺字段 ⇒ 0（D-E2 引入前登记的项，合法形态）；
    /// 键存在必须是非负整数（负数／非整数 ⇒ 响亮拒绝，与 state 同口径）。
    /// </summary>
    private static int ParseGeneration(JsonObject itemObject, string itemId)
    {
        if (!itemObject.ContainsKey("generation")) return 0;
        int value;
        // [Wave2 R39 M-2] TryGetValue<int> 对字符串 JSON 种类的行为（返回 false 或抛 InvalidOperationException/
        // FormatException）已探针实证并统一包装为合同类型——非 Number 种类一律响亮 LocalWaitQueueCorruptException。
        try
        {
            if (itemObject["generation"] is not JsonValue node || !node.TryGetValue<int>(out value))
                throw new LocalWaitQueueCorruptException($"等待项 {itemId} 的 generation 不是整数（原件保留）。");
        }
        catch (LocalWaitQueueCorruptException) { throw; }
        catch (Exception ex)
        {
            throw new LocalWaitQueueCorruptException($"等待项 {itemId} 的 generation 不是整数（原件保留）：{ex.Message}");
        }
        if (value < 0)
            throw new LocalWaitQueueCorruptException($"等待项 {itemId} 的 generation 为负数（原件保留）。");
        return value;
    }

    /// <summary>
    /// [批次 20／C3] <see cref="LocalWaitItem.AdmissionIdentity"/> 的写侧形状快速失败：
    /// 与读取侧 <c>ParseAdmissionIdentity</c>／物化校验同口径——null 放行（合同前存量形状），
    /// 空白串响亮拒绝（该形状读取侧判损坏，不得写出）。
    /// </summary>
    private static void ValidateAdmissionIdentityShape(string? admissionIdentity, string itemId)
    {
        if (admissionIdentity is null) return;
        if (string.IsNullOrWhiteSpace(admissionIdentity))
            throw new LocalWaitQueueCorruptException(
                $"等待项 {itemId} 的 admissionIdentity 为空字符串：写入侧拒绝（该形状读取侧判损坏，不得写出）。");
    }

    /// <summary>
    /// [批次 16／D2] 严格解析**持久化稳定前置引用**（稳定引用串）。
    /// 缺字段／显式 JSON null ⇒ null（**保守按不可判定**，不得默认就绪；v1 旧文件即走此路径）。
    /// 键存在且为非字符串值（数字／对象／数组）或**空串／空白串** ⇒ 响亮拒绝，不降级解析。
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

    /// <summary>
    /// [批次 16／D2 第二轮会诊 #2 必改／第三轮会诊 #1 必改] **写盘前按 <see cref="Load"/> 的同一形状规则
    /// 校验（并物化）即将写出的记录**。
    ///
    /// 不变量：**已写出的文件必定可被 <see cref="Load"/> 读回**。为此必须满足两点：
    /// ①**全字段校验**——`Load` 判为损坏的所有形状，在写盘**之前**都必须同样响亮拒绝：
    ///   `itemId`／`stableIdentity` 非空（<see cref="RequiredString"/>）；`tier` 已定义；`state` 已定义；
    ///   `prerequisiteReference` 形状合法；`itemId` **唯一**（去重按 ordinal）。**只校验
    ///   `PrerequisiteReference` 是不够的**。
    /// ②**校验的必须是「即将被序列化的那份对象」**——具体做法是**边校验、边物化**：
    ///   每个字段只读一次，**先校验读到的那个值**，**再把同一个值**放进副本，后续只序列化副本。
    ///   因此不存在「先整体复制、再校验副本」的时间窗：任何在物化前一刻发生的改写都会被这一遍读取
    ///   读到并当场拒绝；而副本内的值全部来自**已通过校验的同一次读取**，故副本必定满足 `Load` 的形状规则。
    ///
    /// 拒绝一律发生在**任何**写盘动作之前，故「拒绝即零副作用，原文件逐字节不变」仍然成立。
    /// </summary>
    private static List<LocalWaitItem> MaterializeAndValidatePayload(List<LocalWaitItem> items)
    {
        var snapshot = new List<LocalWaitItem>(items.Count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            // [Wave1 R12 建议-3] null 元素**响亮拒绝**（与读取侧「非对象元素判损坏」对称；写侧不得静默裁剪
            // 等待项）。现有三个 Persist 调用点（Upsert/Remove/PersistCleanup）的列表均来自 Load()
            // （ParseItem 产非空对象）或单条已空检的 Upsert 入参 ⇒ 该分支当前不可达，仅作未来写者护栏。
            if (item is null)
                throw new LocalWaitQueueCorruptException(
                    "等待队列包含 null 元素：写入侧拒绝（该形状读取侧判损坏，不得静默裁剪）。");
            // ①一次读取 → ②校验该值 → ③把**该值**放进副本；后续写盘只用副本
            var itemId = item.ItemId;
            var stableIdentity = item.StableIdentity;
            var reference = item.PrerequisiteReference;
            var admissionIdentity = item.AdmissionIdentity;
            var generation = item.Generation;
            var tier = item.Tier;
            var state = item.State;
            // [Wave1 R11 F-D] 非空注解字段 null→"" 归一（与读取侧 ParseItem 的 `?? ""` 同口径）：
            // 否则内存对象持 null、落盘 JSON null、读回归一 "" ⇒ 同身份重登记时载荷比较 "" vs null
            // 判不等 ⇒ 响亮冲突，「同身份重复登记复用同一条」的幂等合同对该形状不成立。
            var candidateId = item.CandidateId ?? string.Empty;
            var itemNamespace = item.Namespace ?? string.Empty;
            var workflowId = item.WorkflowId ?? string.Empty;

            ValidatePersistableItemShape(itemId, stableIdentity, tier, state, reference, admissionIdentity);
            if (generation < 0)
                throw new LocalWaitQueueCorruptException(
                    $"等待项 {itemId} 的 generation 为负数：写入侧拒绝（该形状读取侧判损坏，不得写出）。");
            if (!seenIds.Add(itemId ?? string.Empty))
                throw new LocalWaitQueueCorruptException(
                    $"等待队列存在重复 itemId：{itemId}（写入侧拒绝：该形状读取侧判损坏，不得写出）。");

            snapshot.Add(new LocalWaitItem
            {
                ItemId = itemId ?? string.Empty,
                StableIdentity = stableIdentity ?? string.Empty,
                CandidateId = candidateId,
                Namespace = itemNamespace,
                WorkflowId = workflowId,
                Tier = tier,
                Priority = item.Priority,
                IsHoeingHighest = item.IsHoeingHighest,
                ScheduledAt = item.ScheduledAt,
                PrerequisiteReference = reference,
                AdmissionIdentity = admissionIdentity,
                Generation = generation,
                HasTrustedIdentity = item.HasTrustedIdentity,
                EnqueuedAtUtc = item.EnqueuedAtUtc,
                CancelledAtUtc = item.CancelledAtUtc,
                State = state,
                Reason = item.Reason,
            });
        }
        return snapshot;
    }

    /// <summary>
    /// [批次 16／D2 第三轮会诊 #1 必改] **写盘前全字段形状校验**：与 <see cref="Load"/> 同口径。
    /// 「读取侧判损坏」的形状必须在这里**响亮拒绝**，否则会写出自己随后读不回来的文件。
    /// 校验的是**已物化的值**（而非调用方对象），故不受之后发生的改写影响。
    /// </summary>
    private static void ValidatePersistableItemShape(
        string? itemId,
        string? stableIdentity,
        ArbitrationTier tier,
        LocalWaitItemState state,
        string? reference,
        string? admissionIdentity)
    {
        if (string.IsNullOrEmpty(itemId))
            throw new LocalWaitQueueCorruptException(
                "等待项缺少非空 itemId：写入侧拒绝（该形状读取侧判损坏，不得写出）。");
        if (string.IsNullOrEmpty(stableIdentity))
            throw new LocalWaitQueueCorruptException(
                $"等待项 {itemId} 缺少非空 stableIdentity：写入侧拒绝（该形状读取侧判损坏，不得写出）。");
        if (!Enum.IsDefined(typeof(ArbitrationTier), tier))
            throw new LocalWaitQueueCorruptException(
                $"等待项 {itemId} 的 tier={(int)tier} 不是已定义取值：写入侧拒绝（该形状读取侧判损坏，不得写出）。");
        if (!Enum.IsDefined(typeof(LocalWaitItemState), state))
            throw new LocalWaitQueueCorruptException(
                $"等待项 {itemId} 的 state={(int)state} 不是已定义取值：写入侧拒绝（该形状读取侧判损坏，不得写出）。");
        ValidatePrerequisiteReferenceShape(reference, itemId);
        // [批次 20／C3/C4①] 准入身份形状：null（合同前存量读兼容）或非空字符串；空白串＝读取侧判损坏。
        if (admissionIdentity is not null && string.IsNullOrWhiteSpace(admissionIdentity))
            throw new LocalWaitQueueCorruptException(
                $"等待项 {itemId} 的 admissionIdentity 为空字符串：写入侧拒绝（该形状读取侧判损坏，不得写出）。");
    }

    private void Persist(List<LocalWaitItem> items)
    {
#if DEBUG
        // 测试专用探针（Release／生产构建下**编译期不存在**）：用于证明「写盘前物化」确实生效。
        // 会诊第二轮 #2 的判别力取证需要确定性复现「校验通过后、序列化前改写调用方实例」这一窗口；
        // 该窗口无法从公开 API 之外触发，故以 DEBUG 专用钩子取证，而不是新增生产可见接缝。
        WriteSnapshotProbeMutator?.Invoke(items);
#endif
        var payloadItems = MaterializeAndValidatePayload(items);

        var directory = Path.GetDirectoryName(_file)!;
        Directory.CreateDirectory(directory);
        var payload = Utf8NoBom.GetBytes(JsonSerializer.Serialize(new LocalWaitQueueFile { Items = payloadItems }, JsonOptions));
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

