using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// **真实副作用结果类别**（R5.6 A 项）：成功必须与「确定拒绝／写后不明／取消」**可区分**；
/// 未知一律 fail-closed；`CompletedWrites` 只统计**已实际落盘**的文件数，失败方不得虚报为 0 以外的机会。
/// </summary>
public enum MigrationEffectOutcome
{
    Succeeded = 0,
    Rejected = 1,
    Unknown = 2,
    Cancelled = 3,
}

/// <summary>真实副作用结果。`Reason` 非空即结构化原因码（供事务落盘与取证）。</summary>
public sealed record MigrationEffectResult(MigrationEffectOutcome Outcome, string Reason, int CompletedWrites)
{
    public static MigrationEffectResult Ok(int writes) => new(MigrationEffectOutcome.Succeeded, "", writes);
    public static MigrationEffectResult Rejected(string reason, int writes = 0) => new(MigrationEffectOutcome.Rejected, reason, writes);
    public static MigrationEffectResult Unknown(string reason, int writes = 0) => new(MigrationEffectOutcome.Unknown, reason, writes);
    public static MigrationEffectResult Cancelled(string reason, int writes = 0) => new(MigrationEffectOutcome.Cancelled, reason, writes);
}

/// <summary>
/// 一条真实引用写入目标。两种**真实**语义：
/// - `Added` + `NewContent`：在配置根内**新建**文件（迁移产生的新候选文件）；
/// - `Modified` + `RenameFrom`/`RenameTo`：对既有文件的**引用重命名**（与 BGI 侧 W4
///   `OneDragonConfigReferenceService.RenameGroupReferences` 同语义；助手侧流程文件的对应面是 `nodes[].ref.config`）。
/// </summary>
public sealed record MigrationReferenceWriteTarget(
    string Path,
    ChangeKind Kind,
    string? NewContent = null,
    string? RenameFrom = null,
    string? RenameTo = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? ExpectedContentHash = null);

/// <summary>真实引用更新计划（一次事务内的完整写集声明）。</summary>
public sealed record MigrationReferenceUpdatePlan(IReadOnlyList<MigrationReferenceWriteTarget> Targets);

/// <summary>
/// 真实 candidate→active 激活请求（D13 三态：只接受 `candidate-ready` 候选）。
/// `ExpectedContentHash` ＝ **本次写入所依据的字节版本**（事务的已确认写集哈希）：端口在写入前必须核对盘上字节哈希，
/// 不符即拒绝——防止「检查之后、写入之前」的锁外改动被连同激活一起合法化（会诊第 2 轮 MUST-3）。
/// </summary>
public sealed record MigrationActivationRequest(string Path, string ExpectedBeforeStatus, string TargetStatus,
    string? ExpectedContentHash = null);

/// <summary>
/// **真实副作用端口**（R5.6 A 项接线面）。事务只依赖本接口；生产用下方
/// <see cref="WorkflowFileMigrationEffectService"/>（真实文件写入），夹具用可注入故障的实现。
/// </summary>
public interface IMigrationEffectService
{
    /// <summary>执行真实引用更新（逐文件）；返回类别、原因与**已落盘文件数**。</summary>
    MigrationEffectResult ApplyReferenceUpdate(string configRoot, MigrationReferenceUpdatePlan plan);

    /// <summary>执行真实激活（读改写 + 原子替换）；返回类别、原因与已落盘文件数。</summary>
    MigrationEffectResult Activate(string configRoot, MigrationActivationRequest request);

    /// <summary>语义读回：引用是否已全部由 `RenameFrom` 改为 `RenameTo`（`Added` 目标恒 false，交由字节读回）。</summary>
    bool TryReadReferenceState(string configRoot, MigrationReferenceWriteTarget target, out string detail);

    /// <summary>语义读回：文件的 `activation.status` 现值（读不到时 status 为空且 detail 说明原因）。</summary>
    bool TryReadActivationStatus(string configRoot, string relPath, out string status, out string detail);

    /// <summary>
    /// **预测**：若把 `path` 的状态由 `fromStatus` 迁移到 `toStatus`，写入后的文件字节哈希（不落盘）。
    /// 事务用它**在写入之前**持久化撤销证据，从而消除「已写入但证据未持久化」的中断窗口（会诊第 4 轮 MUST-1）。
    /// 不可预测（文件不存在/形状不可用/状态不符）时返回 false。
    /// </summary>
    bool TryComputeStatusTransitionHash(string configRoot, string relPath, string fromStatus, string toStatus,
        string expectedInputHash, out string hash);
}

/// <summary>
/// **R5.6 真实引用写入／激活实现（助手侧，隔离根内）**。
/// 纪律：只写配置根内**已声明**目标（相对路径安全 + 根内）；逐文件**临时文件 + 同目录替换**；
/// 保留原文件 BOM 形态；解析失败即隔离跳过（绝不回空覆盖）；每次写入都重新读盘校验前置条件（拒绝过期写入）。
/// 本实现**不**接触真实 `User` 目录：根由调用方传入，事务已保证根隔离与静止窗口。
/// </summary>
public interface IPreparedMigrationEffectService
{
    bool TryPrepareReference(MigrationReferenceWriteTarget target, byte[]? input,
        out byte[] output, out string reason);
    bool TryPrepareActivation(MigrationActivationRequest request, byte[] input,
        out byte[] output, out bool alreadyTarget, out string reason);
}

public sealed class WorkflowFileMigrationEffectService : IMigrationEffectService, IPreparedMigrationEffectService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>真实写入用序列化选项：保留人类可读的非 ASCII（不被转义为 `\uXXXX`），便于写入后人工比对与评审。</summary>
    private static readonly System.Text.Json.JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Pure transforms: the version store supplies bytes from its checked target handle.
    // These methods never re-open a path or perform a filesystem effect.
    public bool TryPrepareReference(MigrationReferenceWriteTarget target, byte[]? input,
        out byte[] output, out string reason)
    {
        output = [];
        reason = "";
        if (target is null || !MigrationSwitchTransaction.IsSafeRelativePath(target.Path))
        { reason = "unsafe_reference_target"; return false; }
        if (target.Kind == ChangeKind.Added)
        {
            if (input is not null) { reason = "added_target_already_exists:" + target.Path; return false; }
            if (target.NewContent is null) { reason = "added_target_without_content:" + target.Path; return false; }
            output = Utf8NoBom.GetBytes(target.NewContent);
            return true;
        }
        if (target.Kind != ChangeKind.Modified)
        { reason = "unsupported_change_kind:" + target.Path; return false; }
        if (input is null) { reason = "modified_target_missing:" + target.Path; return false; }
        if (target.NewContent is not null)
        {
            if (target.RenameFrom is not null || target.RenameTo is not null ||
                string.IsNullOrEmpty(target.ExpectedContentHash) || Sha256Hex(input) != target.ExpectedContentHash)
            { reason = "replacement_input_hash_mismatch:" + target.Path; return false; }
            if (!TryParseDocument(target.NewContent, out _, out reason)) return false;
            output = EncodeText(target.NewContent, HasUtf8Bom(input));
            return true;
        }
        if (string.IsNullOrEmpty(target.RenameFrom) || string.IsNullOrEmpty(target.RenameTo))
        { reason = "modified_target_without_rename:" + target.Path; return false; }
        try
        {
            if (!TryRenameReferences(DecodeText(input), target.RenameFrom, target.RenameTo,
                    out var renamed, out var text))
            { reason = "reference_document_unusable:" + target.Path; return false; }
            if (renamed == 0) { reason = "no_reference_match:" + target.Path; return false; }
            output = EncodeText(text!, HasUtf8Bom(input));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or ArgumentException)
        { reason = "reference_document_unusable:" + target.Path + ":" + ex.GetType().Name; return false; }
    }

    public bool TryPrepareActivation(MigrationActivationRequest request, byte[] input,
        out byte[] output, out bool alreadyTarget, out string reason)
    {
        output = [];
        alreadyTarget = false;
        reason = "";
        if (request is null || !MigrationSwitchTransaction.IsSafeRelativePath(request.Path))
        { reason = "unsafe_activation_target"; return false; }
        if (input is null) { reason = "activation_target_missing"; return false; }
        if (!string.IsNullOrEmpty(request.ExpectedContentHash)
            && !string.Equals(Sha256Hex(input), request.ExpectedContentHash, StringComparison.Ordinal))
        { reason = "activation_content_hash_mismatch:" + request.Path; return false; }
        try
        {
            if (!TrySetActivationStatus(DecodeText(input), request.ExpectedBeforeStatus, request.TargetStatus,
                    out reason, out var text, out alreadyTarget)) return false;
            output = alreadyTarget ? input.ToArray() : EncodeText(text!, HasUtf8Bom(input));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or ArgumentException)
        { reason = "activation_document_unusable:" + ex.GetType().Name; return false; }
    }

    public MigrationEffectResult ApplyReferenceUpdate(string configRoot, MigrationReferenceUpdatePlan plan)
    {
        var root = Path.GetFullPath(configRoot);
        var writes = 0;
        foreach (var target in plan.Targets)
        {
            var target_ = target ?? throw new ArgumentNullException(nameof(plan));
            if (!TryResolve(root, target_.Path, out var full, out var reason))
                return MigrationEffectResult.Rejected(reason, writes);
            switch (target_.Kind)
            {
                case ChangeKind.Added:
                {
                    if (target_.NewContent is null)
                        return MigrationEffectResult.Rejected("added_target_without_content:" + target_.Path, writes);
                    if (File.Exists(full))
                        return MigrationEffectResult.Rejected("added_target_already_exists:" + target_.Path, writes);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                        // **新文件不得覆盖**：竞争窗口内被他人创建 ⇒ 本次写入失败（不静默覆盖他人文件）
                        AtomicWrite(full, Utf8NoBom.GetBytes(target_.NewContent), hasBom: false, overwrite: false);
                    }
                    catch (IOException) when (File.Exists(full))
                    {
                        return MigrationEffectResult.Rejected("added_target_already_exists:" + target_.Path, writes);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 写失败：`CompletedWrites` 如实反映此前**已落盘**的文件数（不得假报 0）
                        return MigrationEffectResult.Unknown("reference_write_io_failed:" + target_.Path + ":" + ex.GetType().Name, writes);
                    }
                    writes++;
                    break;
                }
                case ChangeKind.Modified:
                {
                    if (string.IsNullOrEmpty(target_.RenameFrom) || string.IsNullOrEmpty(target_.RenameTo))
                        return MigrationEffectResult.Rejected("modified_target_without_rename:" + target_.Path, writes);
                    if (!File.Exists(full))
                        return MigrationEffectResult.Rejected("modified_target_missing:" + target_.Path, writes);
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(full); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return MigrationEffectResult.Unknown("reference_read_io_failed:" + target_.Path + ":" + ex.GetType().Name, writes);
                    }
                    var hasBom = HasUtf8Bom(bytes);
                    bool renamedOk;
                    int renamed;
                    string? text;
                    try
                    {
                        renamedOk = TryRenameReferences(DecodeText(bytes), target_.RenameFrom!, target_.RenameTo!, out renamed, out text);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or ArgumentException)
                    {
                        return MigrationEffectResult.Rejected("reference_document_unusable:" + target_.Path + ":" + ex.GetType().Name, writes);
                    }
                    if (!renamedOk)
                        return MigrationEffectResult.Rejected("reference_document_unusable:" + target_.Path, writes);   // 坏文件隔离：不覆盖
                    if (renamed == 0)
                        return MigrationEffectResult.Rejected("no_reference_match:" + target_.Path, writes);            // 无可更新引用 ⇒ 确定拒绝
                    // **写前复检**：读后被锁外写方改动 ⇒ 放弃本次写入（不静默覆盖）
                    try
                    {
                        if (!File.ReadAllBytes(full).AsSpan().SequenceEqual(bytes))
                            return MigrationEffectResult.Unknown("reference_target_changed_after_read:" + target_.Path, writes);
                        AtomicWrite(full, EncodeText(text!, hasBom), hasBom);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return MigrationEffectResult.Unknown("reference_write_io_failed:" + target_.Path + ":" + ex.GetType().Name, writes);
                    }
                    writes++;
                    break;
                }
                default:
                    return MigrationEffectResult.Rejected("unsupported_change_kind:" + target_.Path, writes);
            }
        }
        return MigrationEffectResult.Ok(writes);
    }

    public MigrationEffectResult Activate(string configRoot, MigrationActivationRequest request)
    {
        var root = Path.GetFullPath(configRoot);
        if (!TryResolve(root, request.Path, out var full, out var reason))
            return MigrationEffectResult.Rejected(reason, 0);
        if (!File.Exists(full))
            return MigrationEffectResult.Rejected("activation_target_missing:" + request.Path, 0);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MigrationEffectResult.Unknown("activation_read_io_failed:" + request.Path + ":" + ex.GetType().Name, 0);
        }
        if (!string.IsNullOrEmpty(request.ExpectedContentHash)
            && !string.Equals(Sha256Hex(bytes), request.ExpectedContentHash, StringComparison.Ordinal))
            return MigrationEffectResult.Rejected("activation_content_hash_mismatch:" + request.Path, 0);   // 版本绑定（MUST-3）
        var hasBom = HasUtf8Bom(bytes);
        if (!TrySetActivationStatus(DecodeText(bytes), request.ExpectedBeforeStatus, request.TargetStatus,
                out var reasonText, out var text, out var alreadyTarget))
            return MigrationEffectResult.Rejected(reasonText, 0);
        if (alreadyTarget) return MigrationEffectResult.Ok(0);         // 已是目标状态：真实生效无需二次写入
        try
        {
            var preWrite = File.ReadAllBytes(full);
            if (!preWrite.AsSpan().SequenceEqual(bytes))
                return MigrationEffectResult.Unknown("activation_target_changed_after_read:" + request.Path, 0);
            if (!string.IsNullOrEmpty(request.ExpectedContentHash)
                && !string.Equals(Sha256Hex(preWrite), request.ExpectedContentHash, StringComparison.Ordinal))
                return MigrationEffectResult.Rejected("activation_content_hash_mismatch:" + request.Path, 0);
            AtomicWrite(full, EncodeText(text!, hasBom), hasBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MigrationEffectResult.Unknown("activation_write_io_failed:" + request.Path + ":" + ex.GetType().Name, 0);
        }
        return MigrationEffectResult.Ok(1);
    }

    public bool TryReadReferenceState(string configRoot, MigrationReferenceWriteTarget target, out string detail)
    {
        detail = "";
        var root = Path.GetFullPath(configRoot);
        if (!TryResolve(root, target.Path, out var full, out detail)) return false;
        if (!File.Exists(full)) { detail = "target_missing"; return false; }
        if (target.Kind == ChangeKind.Modified && target.NewContent is not null)
        {
            try
            {
                var bytes = File.ReadAllBytes(full);
                if (!bytes.AsSpan().SequenceEqual(EncodeText(target.NewContent, HasUtf8Bom(bytes))))
                { detail = "replacement_content_mismatch"; return false; }
                detail = "replacement_content_confirmed";
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { detail = "read_io_failed:" + ex.GetType().Name; return false; }
        }
        if (target.Kind == ChangeKind.Added)
        {
            // 新增目标的**写回确认**＝内容逐字节等于声明内容（不适用「引用改写」语义）
            if (target.NewContent is null) { detail = "added_target_without_content"; return false; }
            try
            {
                var expected = Utf8NoBom.GetBytes(target.NewContent);
                if (!File.ReadAllBytes(full).AsSpan().SequenceEqual(expected)) { detail = "added_content_mismatch"; return false; }
                detail = "added_content_confirmed";
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                detail = "read_io_failed:" + ex.GetType().Name;
                return false;
            }
        }
        if (target.Kind != ChangeKind.Modified) { detail = "not_a_rename_target"; return false; }
        try
        {
            var text = DecodeText(File.ReadAllBytes(full));
            if (!TryParseDocument(text, out var doc, out detail)) return false;
            var stale = CountMatchingReferences(doc!, target.RenameFrom!);
            if (stale is null) { detail = "document_shape_unusable"; return false; }
            if (stale > 0) { detail = "stale_reference_remaining:" + stale; return false; }
            var updated = CountMatchingReferences(doc!, target.RenameTo!);
            if (updated is null) { detail = "document_shape_unusable"; return false; }
            if (updated == 0) { detail = "renamed_reference_absent"; return false; }
            detail = "renamed_reference=" + updated;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            detail = "read_io_failed:" + ex.GetType().Name;
            return false;
        }
    }

    public bool TryComputeStatusTransitionHash(string configRoot, string relPath, string fromStatus, string toStatus,
        string expectedInputHash, out string hash)
    {
        hash = "";
        var root = Path.GetFullPath(configRoot);
        if (!TryResolve(root, relPath, out var full, out _)) return false;
        if (!File.Exists(full)) return false;
        try
        {
            var bytes = File.ReadAllBytes(full);
            // **输入字节绑定（会诊第 6 轮 MUST）**：预测必须基于调用方**已核验的那一份字节**；
            // 读到的内容与之不符（读盘前被锁外改动）⇒ 拒绝预测，绝不把他方内容作为撤销证据。
            if (string.IsNullOrEmpty(expectedInputHash)
                || !string.Equals(Sha256Hex(bytes), expectedInputHash, StringComparison.Ordinal)) return false;
            var hasBom = HasUtf8Bom(bytes);
            if (!TrySetActivationStatus(DecodeText(bytes), fromStatus, toStatus, out _, out var text, out var alreadyTarget))
                return false;
            hash = Sha256Hex(alreadyTarget ? bytes : EncodeText(text!, hasBom));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool TryReadActivationStatus(string configRoot, string relPath, out string status, out string detail)
    {
        status = "";
        detail = "";
        var root = Path.GetFullPath(configRoot);
        if (!TryResolve(root, relPath, out var full, out detail)) return false;
        if (!File.Exists(full)) { detail = "target_missing"; return false; }
        try
        {
            var text = DecodeText(File.ReadAllBytes(full));
            if (!TryParseDocument(text, out var doc, out detail)) return false;
            var value = doc!["activation"]?["status"];
            if (value is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrEmpty(s))
            {
                detail = "activation_status_absent";
                return false;
            }
            status = s;
            detail = "activation_status=" + s;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            detail = "read_io_failed:" + ex.GetType().Name;
            return false;
        }
    }

    /// <summary>相对路径安全解析（拒绝绝对/`..`/控制字符/越根；父链无链接由事务层另行保证）。</summary>
    private static bool TryResolve(string root, string? rel, out string full, out string reason)
    {
        full = "";
        reason = "";
        if (!MigrationSwitchTransaction.IsSafeRelativePath(rel)) { reason = "unsafe_path:" + rel; return false; }
        var rootFull = MigrationSwitchTransaction.EnsureTrailingSeparator(root);
        full = Path.GetFullPath(Path.Combine(rootFull,
            MigrationSwitchTransaction.NormalizePath(rel).Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) { reason = "path_outside_root:" + rel; return false; }
        return true;
    }

    private static void AtomicWrite(string full, byte[] bytes, bool hasBom, bool overwrite = true)
    {
        var dir = Path.GetDirectoryName(full)!;
        var tmp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(tmp, bytes);
        try { File.Move(tmp, full, overwrite: overwrite); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private static bool HasUtf8Bom(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static string DecodeText(byte[] bytes)
        => Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');

    private static byte[] EncodeText(string text, bool hasBom)
    {
        var payload = Utf8NoBom.GetBytes(text);
        if (!hasBom) return payload;
        var withBom = new byte[payload.Length + 3];
        withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
        payload.CopyTo(withBom, 3);
        return withBom;
    }

    private static bool TryParseDocument(string text, out JsonObject? doc, out string reason)
    {
        doc = null;
        reason = "";
        try
        {
            doc = JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            reason = "json_invalid";
            return false;
        }
        if (doc is null) { reason = "json_shape_invalid"; return false; }
        return true;
    }

    private static bool TryRenameReferences(string text, string from, string to, out int renamed, out string? output)
    {
        renamed = 0;
        output = null;
        if (!TryParseDocument(text, out var doc, out _)) return false;
        var count = RenameReferences(doc!, from, to);
        if (count is null) return false;                 // 形状不可用 ⇒ 隔离跳过（不写回、不改变工件形状）
        renamed = count.Value;
        output = doc!.ToJsonString(WriteOptions);
        return true;
    }

    /// <summary>
    /// 真实引用重命名：遍历 `nodes[].ref.config`（助手侧流程文件的资源引用面）。
    /// **形状异常即拒绝**（标量节点、非对象 `ref` 等）：助手侧 `WorkflowStore` 对 `nodes` 非数组/元素非对象按隔离处理，
    /// 本服务不得把无法安全解析的文档改写后写回（会改变工件形状）；返回 null 表示文档形状不可用。
    /// </summary>
    private static int? RenameReferences(JsonObject doc, string from, string to)
    {
        var renamed = 0;
        if (doc["nodes"] is not JsonArray nodes) return 0;
        foreach (var node in nodes)
        {
            if (node is not JsonObject nodeObject) return null;         // 标量/数组元素 ⇒ 形状不可用
            if (!nodeObject.TryGetPropertyValue("ref", out var refNode) || refNode is null) continue;
            if (refNode is not JsonObject reference) return null;       // ref 非对象 ⇒ 形状不可用
            if (!reference.TryGetPropertyValue("config", out var configNode) || configNode is null) continue;
            if (configNode is not JsonValue value) return null;
            if (!value.TryGetValue<string>(out var current) || !string.Equals(current, from, StringComparison.Ordinal)) continue;
            reference["config"] = to;
            renamed++;
        }
        return renamed;
    }

    private static int? CountMatchingReferences(JsonObject doc, string name)
    {
        var count = 0;
        if (doc["nodes"] is not JsonArray nodes) return 0;
        foreach (var node in nodes)
        {
            if (node is not JsonObject nodeObject) return null;
            if (!nodeObject.TryGetPropertyValue("ref", out var refNode) || refNode is null) continue;
            if (refNode is not JsonObject reference) return null;
            if (!reference.TryGetPropertyValue("config", out var configNode) || configNode is null) continue;
            if (configNode is not JsonValue value) return null;
            if (value.TryGetValue<string>(out var current) && string.Equals(current, name, StringComparison.Ordinal)) count++;
        }
        return count;
    }

    private static bool TrySetActivationStatus(string text, string expectedBefore, string targetStatus,
        out string reason, out string? output, out bool alreadyTarget)
    {
        reason = "";
        output = null;
        alreadyTarget = false;
        if (!TryParseDocument(text, out var doc, out var parseReason)) { reason = "activation_document_unusable:" + parseReason; return false; }
        if (doc!["activation"] is not JsonObject activation) { reason = "activation_block_missing"; return false; }
        if (activation["status"] is not JsonValue value || !value.TryGetValue<string>(out var current) || string.IsNullOrEmpty(current))
        {
            reason = "activation_status_absent";
            return false;
        }
        if (string.Equals(current, targetStatus, StringComparison.Ordinal)) { alreadyTarget = true; return true; }
        if (!string.Equals(current, expectedBefore, StringComparison.Ordinal)) { reason = "activation_precondition_mismatch:" + current; return false; }
        activation["status"] = targetStatus;
        output = doc.ToJsonString(WriteOptions);
        return true;
    }

    internal static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
