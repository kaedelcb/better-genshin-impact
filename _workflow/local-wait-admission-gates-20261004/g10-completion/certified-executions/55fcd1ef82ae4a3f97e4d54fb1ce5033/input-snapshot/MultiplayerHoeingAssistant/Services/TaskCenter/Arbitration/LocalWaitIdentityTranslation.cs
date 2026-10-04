using System;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 身份翻译/重入校验结果（纯函数产物；**不含**任何发送许可、优先级或执行承诺）。
/// </summary>
public sealed class LocalWaitIdentityTranslationResult
{
    /// <summary>翻译/校验是否通过。</summary>
    public bool Ok { get; init; }

    /// <summary>人类可读原因（结构化审计用；不得承载发送许可或成功断言）。</summary>
    public string Reason { get; init; } = "";

    /// <summary>准入面稳定身份（9 元组；仅 Ok 时非空）。</summary>
    public string? AdmissionIdentity { get; init; }

    /// <summary>准入面候选号（<c>cand-</c> 前缀；仅 Ok 时非空）。</summary>
    public string? CandidateId { get; init; }

    /// <summary>
    /// **出现身份（8 段前缀）**：9 元组去掉 attempt 维度后的前缀（编码转义保证 <c>'|'</c> 不出现在
    /// 字段载荷内 ⇒ 按**最后一个** <c>'|'</c> 切分无歧义）。attempt 是独立维度（新尝试＝新身份），
    /// 重入时先比前缀、再显式处理 attempt（见 <see cref="LocalWaitIdentityTranslation.ValidateReentry"/>）。
    /// </summary>
    public string? OccurrenceIdentity { get; init; }

    /// <summary>
    /// **[批次 20／C4②] 合同前存量显式标注**（owner 裁决 D-E3=(c)）：等待项缺准入面身份/前置引用等
    /// 登记载荷（版本 1/2 时期文件或未经登记点载荷合同登记的项）⇒ **永不参选**。
    /// 该标注可由观测面读取，用于区分「不可判定（Undetermined，缺引用的保守态）」与
    /// 「结构性永不参选（登记例外）」。
    /// </summary>
    public bool LegacyPreContract { get; init; }
}

/// <summary>
/// 槲寄生 · R5 批次 20（§24.117 合同草案 v2 **C3**／§24.115 IW-05）：**等待队列身份翻译层**。
///
/// **它解决什么**：队列本地身份空间（<c>LocalWaitItem.StableIdentity</c>＝D1 登记点裸拼 4 段
/// `runId|nodeId|occurrence|loopIteration`，<c>ItemId</c>=`wait-`+摘要）与**准入面身份空间**
/// （<c>ArbitrationOrdering.BuildStableIdentity</c> 的 9 元组 `scope|namespace|workflowId|triggerOccurrenceId|
/// runId|nodeId|occurrence|loopIteration|attempt`，<c>CandidateId</c>=`cand-`+摘要）**不是同一空间**，
/// 且 4 段裸拼**不足以**恢复准入候选 ⇒ D3 重评产物→<c>SubmitAsync</c> 重入在登记点死锁（IW-05）。
///
/// **解法合同（C3）**：身份翻译在**登记时点**完成（唯一同时握有 run 上下文与出现身份的时刻）——
/// D1 登记点用**权威组成函数**（<see cref="BuildAdmissionIdentity"/>，内部只委托
/// <c>ArbitrationOrdering.BuildStableIdentity</c>/<c>DeriveCandidateId</c>，不复制组成规则）求得
/// 准入身份并与队列本地身份**绑定落盘**（<c>LocalWaitItem.AdmissionIdentity</c>/<c>CandidateId</c>）；
/// 消费侧（D3 产物→重入）必须经 <see cref="ValidateReentry"/> 校验绑定与候选一致性，**不得**
/// 从队列本地身份推导准入身份。本组件是**纯函数合同层**：不读时钟、不做 I/O、不产生发送、
/// **不接线**（生产零消费点；调用方属接线批）。
///
/// **C4②（owner 裁决 D-E3=(c)）**：合同前存量项（缺 <c>AdmissionIdentity</c>/<c>CandidateId</c> 绑定）
/// 在 <see cref="Translate"/> 得到 <see cref="LocalWaitIdentityTranslationResult.LegacyPreContract"/>
/// 显式标注＝**永不参选**；零新通道、零新工具、不可经 Upsert 原地补全。
/// </summary>
public static class LocalWaitIdentityTranslation
{
    /// <summary>
    /// 由出现身份各字段组成**准入面稳定身份与候选号**（登记时点的身份翻译入口）。
    /// **单一权威**：组成与派生只委托 <c>ArbitrationOrdering.BuildStableIdentity</c>／
    /// <c>ArbitrationOrdering.DeriveCandidateId</c>（编码转义、整数越界响亮拒绝等规则全在权威侧，
    /// 本层不复制）。namespace/triggerOccurrenceId 的**取值口径**由 <c>TaskCenterHost.BuildSuccessorIdentityCandidate</c>
    /// （共享权威工厂）定义——successor 提交路径与等待登记共用该工厂，两处同改由锚定夹具
    /// （SuccessorCandidateComposition_AnchoredToSharedFactory）机械保证。
    /// </summary>
    public static (string AdmissionIdentity, string CandidateId) BuildAdmissionIdentity(
        ArbitrationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var stable = ArbitrationOrdering.BuildStableIdentity(candidate);
        return (stable, ArbitrationOrdering.DeriveCandidateId(stable));
    }

    /// <summary>
    /// **队列项身份翻译**（C3 消费侧前置合同；纯函数）：
    /// ①队列本地自洽——<c>ItemId == LocalWaitQueuePolicy.DeriveItemId(StableIdentity)</c>（ordinal）；
    /// ②准入绑定——<c>AdmissionIdentity</c> 非空且 <c>CandidateId == DeriveCandidateId(AdmissionIdentity)</c>；
    /// ③合同前存量（②缺）⇒ <see cref="LocalWaitIdentityTranslationResult.LegacyPreContract"/> 标注（C4②）。
    /// **失败不是异常**：返回不通过结果＋结构化原因（调用方据此不参选/响亮上报）。
    /// </summary>
    public static LocalWaitIdentityTranslationResult Translate(LocalWaitItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // ①队列本地自洽（与 D3 产出侧校验同口径的自洽前置）。
        if (!string.Equals(item.ItemId, LocalWaitQueuePolicy.DeriveItemId(item.StableIdentity), StringComparison.Ordinal))
        {
            return new LocalWaitIdentityTranslationResult
            {
                Ok = false,
                LegacyPreContract = false,
                Reason = "队列本地身份自洽破坏：ItemId ≠ DeriveItemId(StableIdentity)（"
                    + "这不是合同前存量，而是载荷被改写或身份漂移——不得参选）。",
            };
        }

        // ②合同前存量（C4②，owner 裁决 D-E3=(c)）：缺准入绑定 ⇒ 永不参选。
        if (string.IsNullOrWhiteSpace(item.AdmissionIdentity))
        {
            return new LocalWaitIdentityTranslationResult
            {
                Ok = false,
                LegacyPreContract = true,
                Reason = "C4② 合同前存量：缺准入面身份绑定（版本 1/2 时期载荷）——"
                    + "登记例外＝永不参选（owner 裁决 D-E3=(c)）；不可经 Upsert 原地补全。",
            };
        }

        // ②'形状校验（Wave1 会诊 F4；R6-F4 加严；R8-F1 再加严）：裸 '|' 计数必为 8（编码转义保证
        // 字段载荷内无裸 '|' ⇒ 9 元组恰 8 个分隔符），且整数段（occurrence/loopIteration/attempt，
        // 第 7/8/9 段）必须为权威 EncodeInt 的 D8 定宽十进制——**逐字符全 ASCII 数字**判定
        // （[R8-F1] uint.TryParse 默认 NumberStyles.Integer 容许前导符号/空白，"+0000002"／" 0000002"
        // 会穿透；TryParse 已撤）。⇒「可翻译」即「规范形」，经权威构造的任何重入候选可逐字符对齐
        // （Ok ≠ 可重入的梯度被消除）。非规范形返回不通过结果——不得让 Ok 路径的切分/解析抛异常，
        // 违反「失败不是异常」合同。
        var shapeParts = item.AdmissionIdentity.Split('|');
        static bool IsD8(string s) => s.Length == 8 && s.All(c => c is >= '0' and <= '9');
        var integersCanonical = shapeParts.Length == 9
            && IsD8(shapeParts[6]) && IsD8(shapeParts[7]) && IsD8(shapeParts[8]);
        if (item.AdmissionIdentity.Count(c => c == '|') != 8 || !integersCanonical)
        {
            return new LocalWaitIdentityTranslationResult
            {
                Ok = false,
                LegacyPreContract = false,
                Reason = "准入身份形状非法：裸 '|' 计数 ≠ 8 或整数段非 D8 定宽十进制"
                    + "（非规范 9 元组；编码合同 §2.1）——不参选，不得重入。",
            };
        }

        // ③准入绑定：候选号必须由持久化准入身份经权威派生得出。
        var expectedCandidateId = ArbitrationOrdering.DeriveCandidateId(item.AdmissionIdentity);
        if (!string.Equals(item.CandidateId, expectedCandidateId, StringComparison.Ordinal))
        {
            return new LocalWaitIdentityTranslationResult
            {
                Ok = false,
                LegacyPreContract = false,
                Reason = "准入绑定破坏：CandidateId ≠ DeriveCandidateId(AdmissionIdentity)"
                    + "（绑定缺失或被改写——不得带着坏绑定重入）。",
            };
        }

        // ②''规范编码回环校验（Wave1 R21 必改-F3）：每个字符串段必须满足
        // `EncodeString(DecodeString(seg)) == seg`——「解码等价但非权威编码」的段（空串未编码为
        // "~"、孤立 '~' 被解码丢弃等）不是 BuildStableIdentity 能产生的产物，在此拦下，
        // 保证「Ok ⇒ 规范形」合同对字符串段同样成立。
        for (var si = 0; si <= 5; si++)
        {
            var seg = shapeParts[si];
            if (!string.Equals(
                    ArbitrationOrdering.ArbitrationIdentityEncoding.EncodeString(
                        ArbitrationOrdering.ArbitrationIdentityEncoding.DecodeString(seg)),
                    seg, StringComparison.Ordinal))
            {
                return new LocalWaitIdentityTranslationResult
                {
                    Ok = false,
                    LegacyPreContract = false,
                    Reason = "准入身份字符串段非权威编码规范形（EncodeString∘DecodeString 回环不一致）"
                        + "——不是权威组成函数的产物，不参选，不得重入。",
                };
            }
        }

        // ③'两空间同源校验（Wave1 R4 重要#1；R7 重要-3 补 workflowId）：从持久化准入身份**解码**出
        // workflowId/runId/nodeId/occurrence/loopIteration，按队列本地空间的裸拼定义**整串重建**
        // （runId + "|" + nodeId + "|" + occurrence + "|" + loopIteration；occurrence/loopIteration
        // 为十进制原文，非定宽）并与 StableIdentity 做 ordinal 整串比对；workflowId 段（第 3 段）
        // 与 <see cref="LocalWaitItem.WorkflowId"/> 单独比对（namespace 段按设计不可比——队列侧
        // Namespace 存的是 workflowId，不是准入命名空间）。不同 workflow 的同名节点是不同出现，
        // 「同一出现」的完整身份含 workflow。队列本地裸拼在字段含 '|' 时**不可逆切分**（D1 已知
        // 弱点），故不做本地段切分——整串重建比对对该形态仍无歧义（比对的是同一确定性拼接产物）。
        // 两个身份空间各自自洽但互不同源（带外改盘/未来写路径缺陷）⇒ 审计面与发送面指向不同出现，
        // 必须在此拦下，不得按 AdmissionIdentity 重入。
        // **当前生产路径构造不出分歧项**（登记点两身份同源派生；Upsert 是唯一写入口且不改写载荷；
        // 反例尝试：登记点/Upsert/PersistCleanup/重激活四路径均不产生分歧——R4 会诊已核），
        // 本校验为带外改盘与未来写者的防护。
        var admissionParts = item.AdmissionIdentity.Split('|');
        // [Wave1 R15 建议-3] 本分支**防御性、经②'后不可达**：②' 已用 IsD8（长度 8 且全 ASCII 数字）
        // 强制规范形，D8 串值域 ⊂ int 值域 ⇒ TryParse 恒成功。保留为纵深防御（②' 若被改动仍不抛）。
        if (!int.TryParse(admissionParts[6], out var decodedOcc)
            || !int.TryParse(admissionParts[7], out var decodedLoop))
        {
            return new LocalWaitIdentityTranslationResult
            {
                Ok = false,
                LegacyPreContract = false,
                Reason = "准入身份的 occurrence/loopIteration 段不是十进制整数（形状非法）——不得重入。",
            };
        }
        var rebuilt = ArbitrationOrdering.ArbitrationIdentityEncoding.DecodeString(admissionParts[4])
            + "|" + ArbitrationOrdering.ArbitrationIdentityEncoding.DecodeString(admissionParts[5])
            + "|" + decodedOcc.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "|" + decodedLoop.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var workflowIdAligned = string.Equals(
            ArbitrationOrdering.ArbitrationIdentityEncoding.DecodeString(admissionParts[2]),
            item.WorkflowId, StringComparison.Ordinal);
        if (!string.Equals(rebuilt, item.StableIdentity, StringComparison.Ordinal) || !workflowIdAligned)
        {
            return new LocalWaitIdentityTranslationResult
            {
                Ok = false,
                LegacyPreContract = false,
                Reason = "两个身份空间不同源：StableIdentity 与 AdmissionIdentity 的同名字段"
                    + "（workflowId/runId/nodeId/occurrence/loopIteration）不一致——审计面与发送面"
                    + "必须指向同一出现，不得重入。",
            };
        }

        return new LocalWaitIdentityTranslationResult
        {
            Ok = true,
            Reason = "身份翻译通过：队列本地身份自洽且准入绑定完好。",
            AdmissionIdentity = item.AdmissionIdentity,
            CandidateId = item.CandidateId,
            OccurrenceIdentity = OccurrencePrefix(item.AdmissionIdentity),
        };
    }

    /// <summary>
    /// **D3 产物→<c>SubmitAsync</c>重入校验**（C3 的死锁防护合同；纯函数）：
    /// ①重评产物取出的 <c>ItemId</c> 与队列项同（ordinal；调用方〔D3 接线批〕从重评产物取键，本层不引用
    ///    重评产物类型——批次 15（D3）的零生产消费点扫描对此有同名硬约束，本组件保持解耦）；
    /// ②队列项通过 <see cref="Translate"/>（身份绑定完好；合同前存量项在此被显式拦下）；
    /// ③重入候选的**权威身份**（<c>ArbitrationOrdering.BuildStableIdentity(candidate)</c>）与持久化
    ///    <c>AdmissionIdentity</c> 逐字符一致（ordinal）；
    /// ④attempt 维度独立处理：出现身份（8 段前缀）一致但 attempt 不同 ⇒ 不算匹配——**新尝试＝新身份**，
    ///    重驱入口必须按新 attempt 显式调 <see cref="BuildAdmissionIdentity"/> 重组身份（并在登记点完成
    ///    新绑定），**不得**复用停驻时快照；出现身份前缀都不一致 ⇒ 响亮失败（死锁防护）。
    /// </summary>
    public static LocalWaitIdentityTranslationResult ValidateReentry(
        string requestItemId, LocalWaitItem item, ArbitrationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(requestItemId);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(candidate);

        // ①重评产物必须指向本队列项（重评产物不得指向另一等待项重入）。
        if (!string.Equals(requestItemId, item.ItemId, StringComparison.Ordinal))
        {
            return new LocalWaitIdentityTranslationResult
            {
                Ok = false,
                Reason = "重入产物-队列项身份不一致：requestItemId ≠ item.ItemId（"
                    + "重评产物不得指向另一等待项重入）。",
            };
        }

        // ②队列项必须身份翻译通过（合同前存量项在此被显式拦下，标注永不参选）。
        var translated = Translate(item);
        if (!translated.Ok)
        {
            return translated;
        }

        // ③重入候选的权威身份与持久化准入身份逐字符一致 ⇒ 匹配（attempt 维度亦同）。
        // [Wave1 R6-F2] 权威组成函数对越界整数（Occurrence/LoopIteration/Attempt <0 或 >99,999,999）
        // 响亮抛 ArgumentOutOfRangeException——本闸是**防不可信输入**的合同层，异常折为
        // 结构化不通过结果（与 Translate「失败不是异常」同一合同口径；越界候选 ≠ 匹配）。
        string candidateIdentity;
        try
        {
            candidateIdentity = ArbitrationOrdering.BuildStableIdentity(candidate);
        }
        // [Wave1 R17 建议-4] 折叠面与权威侧异常合同对齐：BuildStableIdentity → EncodeString 对
        // null/空字段**不抛**（`string.IsNullOrEmpty(v) ⇒ "~"` 占位，ArbitrationOrdering.cs 有据），
        // 故 null 字段候选不会抛 ArgumentNullException；权威侧唯一抛出面＝EncodeInt 的越界
        // （ArgumentOutOfRangeException）。此处另并捕 ArgumentNullException 作纵深防御
        //（若未来权威侧新增 null 抛出契约，仍以结构化不通过结果呈现，不穿透防护闸）。
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or ArgumentNullException)
        {
            return new LocalWaitIdentityTranslationResult
            {
                Ok = false,
                Reason = "重入候选身份字段越界（occurrence/loopIteration/attempt 超出权威编码范围）"
                    + "——不得重入（结构化拒绝，异常不穿透防护闸）。",
            };
        }
        if (string.Equals(candidateIdentity, item.AdmissionIdentity, StringComparison.Ordinal))
        {
            return new LocalWaitIdentityTranslationResult
            {
                Ok = true,
                Reason = "重入校验通过：候选权威身份与持久化准入身份逐字符一致"
                    + "（含 attempt 维度；唯一允许的直接重入形态）。",
                AdmissionIdentity = translated.AdmissionIdentity,
                CandidateId = translated.CandidateId,
                OccurrenceIdentity = translated.OccurrenceIdentity,
            };
        }

        // ④出现身份（8 段前缀）一致但 attempt 不同 ⇒ 不算匹配：新尝试＝新身份。
        if (string.Equals(OccurrencePrefix(candidateIdentity), translated.OccurrenceIdentity, StringComparison.Ordinal))
        {
            return new LocalWaitIdentityTranslationResult
            {
                Ok = false,
                Reason = "出现身份一致但 attempt 不同：新尝试＝新身份——重驱入口必须按新 attempt "
                    + "显式调 BuildAdmissionIdentity 重组身份（并在登记点完成新绑定），"
                    + "不得复用停驻时快照。",
            };
        }

        // ⑤出现身份前缀都不一致 ⇒ 响亮失败（死锁防护：候选不是本等待项的准入身份）。
        return new LocalWaitIdentityTranslationResult
        {
            Ok = false,
            Reason = "重入候选出现身份与持久化准入身份不一致：候选不是本等待项的准入身份"
                + "（IW-05 死锁防护——不得重入）。",
        };
    }

    /// <summary>8 段出现身份前缀（按最后一个 '|' 切分；转义保证字段载荷内无裸 '|'）。</summary>
    private static string? OccurrencePrefix(string? admissionIdentity)
        => string.IsNullOrEmpty(admissionIdentity) ? null : admissionIdentity[..admissionIdentity.LastIndexOf('|')];
}
