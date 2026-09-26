using System;
using System.Linq;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5 批次 20 Wave2（D-E2=① 代际载体＋C5/IW-04 消费前复核＋义务 i/ii）夹具**。
/// 突变验证标注见各夹具注释（M48-M51，突变实测结果已逐条登记本批台账（§24.120 收口时逐条收录 M48-M63））。
/// </summary>
public sealed class LocalWaitGenerationContractTests : IDisposable
{
    private readonly string _dir;

    public LocalWaitGenerationContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "b20gen-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static LocalWaitItem GenItem(int generation = 0)
    {
        var stable = "run-gen|n1|0|0";
        return new LocalWaitItem
        {
            ItemId = LocalWaitQueuePolicy.DeriveItemId(stable),
            StableIdentity = stable,
            Generation = generation,
            Namespace = "successor",
            WorkflowId = "wf-g",
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            State = LocalWaitItemState.Waiting,
        };
    }

    // ---------- 代际载体落盘（D-E2=① 主裁决） ----------

    /// <summary>
    /// 【突变验证 ✔（M60：重激活代际递增移除 ⇒ 红）】【Wave2 D-E2=① 主裁决核心证据】
    /// 代际随项落盘往返；重激活（取消→同载荷重登记）代际递增——Store 唯一写入口保证权威性。
    /// </summary>
    [Fact]
    public void Upsert_ReactivationIncrementsGeneration_RoundTrips()
    {
        var store = new LocalWaitQueueStore(_dir);
        var item = GenItem(0);
        store.Upsert(item);
        Assert.Equal(0, Assert.Single(store.Load()).Generation);

        store.PersistCleanup(_ => "失效清理（构造取消）", DateTimeOffset.UtcNow);
        Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(store.Load()).State);

        store.Upsert(item); // 同载荷重登记 ⇒ 重激活 ⇒ 代际 +1
        Assert.Equal(1, Assert.Single(store.Load()).Generation);

        store.PersistCleanup(_ => "再次取消", DateTimeOffset.UtcNow);
        store.Upsert(item);
        Assert.Equal(2, Assert.Single(store.Load()).Generation);
    }

    /// <summary>
    /// 【突变验证 ✔（M61：缺 generation 键默认漂移 0→7 ⇒ 红）】【Wave2】
    /// v3 早期文件缺 generation 键 ⇒ 读 0（D-E2 引入前形态，合法）。
    /// </summary>
    [Fact]
    public void Load_MissingGenerationKey_ReadsZero()
    {
        var dir = Path.Combine(_dir, "nokey");
        Directory.CreateDirectory(dir);
        var item = GenItem(0);
        var json = $$"""
        {
          "version": 3,
          "items": [
            {
              "itemId": "{{item.ItemId}}",
              "stableIdentity": "{{item.StableIdentity}}",
              "namespace": "successor",
              "workflowId": "wf-g",
              "tier": 2,
              "priority": 0,
              "isHoeingHighest": false,
              "hasTrustedIdentity": false,
              "enqueuedAtUtc": "2026-09-26T00:00:00+00:00",
              "state": 0
            }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(dir, "wait-queue.json"), json);
        Assert.Equal(0, Assert.Single(new LocalWaitQueueStore(dir).Load()).Generation);
    }

    /// <summary>
    /// 【突变验证 ✔（M63：写侧 generation&lt;0 校验移除 ⇒ 红，独立于读侧 M62）】【Wave2】
    /// 负代际写侧响亮拒绝（读取侧同判损坏）——写侧/读侧为两个独立分支，各自有反向突变证据。
    /// </summary>
    [Fact]
    public void Upsert_NegativeGeneration_Rejected()
    {
        var store = new LocalWaitQueueStore(Path.Combine(_dir, "neg"));
        var item = GenItem(0);
        item.Generation = -1;
        Assert.Throws<MultiplayerHoeingAssistant.Services.LocalWaitQueueCorruptException>(() => store.Upsert(item));
    }

    /// <summary>[R39 M-2 探针] 实证 TryGetValue&lt;int&gt; 对字符串 JSON 值的行为（抛 vs false）。</summary>
    [Fact]
    public void Probe_TryGetValueInt_WithStringJsonKind()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse("\"abc\"");
        var ex = Record.Exception(() =>
        {
            if (node is System.Text.Json.Nodes.JsonValue v)
            {
                var ok = v.TryGetValue<int>(out _);
                Assert.False(ok, "字符串 JSON 值 TryGetValue<int> 应返回 false");
            }
        });
        Assert.True(ex is null || ex is System.InvalidOperationException or System.FormatException,
            $"行为实证：{(ex is null ? "返回 false（不抛）" : "抛 " + ex.GetType().Name)}");
    }

    /// <summary>【Wave2 R39 M-2 闭合】字符串代际 ⇒ 响亮 LocalWaitQueueCorruptException（合同类型）。</summary>
    [Fact]
    public void Load_StringGeneration_RejectedAsCorrupt()
    {
        var dir = Path.Combine(_dir, "strgen");
        Directory.CreateDirectory(dir);
        var item = GenItem(0);
        var json = $$"""
        {
          "version": 3,
          "items": [
            {
              "itemId": "{{item.ItemId}}",
              "stableIdentity": "{{item.StableIdentity}}",
              "generation": "abc",
              "namespace": "successor",
              "workflowId": "wf-g",
              "tier": 2,
              "priority": 0,
              "isHoeingHighest": false,
              "hasTrustedIdentity": false,
              "enqueuedAtUtc": "2026-09-26T00:00:00+00:00",
              "state": 0
            }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(dir, "wait-queue.json"), json);
        Assert.Throws<MultiplayerHoeingAssistant.Services.LocalWaitQueueCorruptException>(
            () => new LocalWaitQueueStore(dir).Load());
    }

    /// <summary>【突变验证 ✔（M62：读侧负数判损坏移除 ⇒ 红）】【Wave2 R37 必改 读侧对称】</summary>
    [Fact]
    public void Load_NegativeGeneration_Rejected()
    {
        var dir = Path.Combine(_dir, "neggen");
        Directory.CreateDirectory(dir);
        var item = GenItem(0);
        var json = $$"""
        {
          "version": 3,
          "items": [
            {
              "itemId": "{{item.ItemId}}",
              "stableIdentity": "{{item.StableIdentity}}",
              "generation": -3,
              "namespace": "successor",
              "workflowId": "wf-g",
              "tier": 2,
              "priority": 0,
              "isHoeingHighest": false,
              "hasTrustedIdentity": false,
              "enqueuedAtUtc": "2026-09-26T00:00:00+00:00",
              "state": 0
            }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(dir, "wait-queue.json"), json);
        Assert.Throws<MultiplayerHoeingAssistant.Services.LocalWaitQueueCorruptException>(
            () => new LocalWaitQueueStore(dir).Load());
    }

    // ---------- 裁决 A 收窄措辞（义务 ii）＋ _handled 按代际修剪（义务 i） ----------

    /// <summary>
    /// 【突变验证 ✔（M48：修剪方法移除 ⇒ 红）】【义务 i】同代际沉默；跨代际（重激活）必重新参选；
    /// 修剪后旧代际键移除、键数有界（SW-02 闭合路径）。
    /// </summary>
    [Fact]
    public void Decide_GenerationScoping_AndPruneKeepsKeysBounded()
    {
        var trigger = new LocalWaitReevaluationTrigger(_ => true);
        var items = new[] { GenItem(0) };

        // 同代际：首次产、再次沉默（裁决 A 收窄措辞的「每代际内每类触发首次」）
        var first = trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, items,
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: "0");
        Assert.Single(first.Requests);
        var second = trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, items,
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: "0");
        Assert.Empty(second.Requests);

        // 跨代际（取消→重激活＝新代际）：必重新参选
        var newGen = trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, items,
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: "1");
        Assert.Single(newGen.Requests);

        // [Wave2 R33 重要-1] 键形字段数钉死（四段：摘要|scope|trigger|generation）——文档漂移可捕获
        var request = Assert.Single(first.Requests);
        Assert.Equal(5, request.ReevaluationKey.Split('|').Length); // 前缀段+四字段（前缀自身含一个 |）
        Assert.StartsWith("reval-key-v2|", request.ReevaluationKey);

        // 修剪（义务 i，[R32 重要-F2 更正为按身份×代际]）：传该身份＋新代际 ⇒ 旧代际键移除
        var removed = trigger.PruneHandledExceptGeneration("run-gen|n1|0|0", "1");
        Assert.Equal(1, removed); // 该身份旧代际 "0" 的键被移除；新代际 "1" 的键保留
        var again = trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, items,
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: "1");
        Assert.Empty(again.Requests); // 当前代际键仍在 ⇒ 不重复产
    }

    /// <summary>
    /// 【突变验证 ✔（M53：键并入触发类别后移除 ⇒ 红）】【Wave2 R32 必改-F1 幂等键口径重裁】
    /// **每代际内每类触发首次**：同代际内 OccupancyEnded 已产请求后，SafetyNet（已到期）首次
    /// 触发**不被抑制**（每类通道独立——键含触发类别段；安全网兜底依赖此独立性）。
    /// </summary>
    [Fact]
    public void Decide_SameGenerationDifferentTrigger_ProducesIndependently()
    {
        var trigger = new LocalWaitReevaluationTrigger(_ => true);
        var items = new[] { GenItem(0) };
        var occ = trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, items,
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: "0");
        Assert.Single(occ.Requests);
        var safety = trigger.Decide(LocalWaitReevaluationTriggerPoint.SafetyNet, items,
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: "0");
        Assert.Single(safety.Requests); // 每类通道独立：异类别首次 ⇒ 产
        var safetyAgain = trigger.Decide(LocalWaitReevaluationTriggerPoint.SafetyNet, items,
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: "0");
        Assert.Empty(safetyAgain.Requests); // 同类再次 ⇒ 沉默
    }

    /// <summary>
    /// 【突变验证 ✔（M54：修剪跨身份越界恢复 ⇒ 红）】【Wave2 R32 重要-F2】
    /// 双项场景：A、B 同代际各产请求；B 重激活至新代际 ⇒ 修剪（B 身份, 新代际）**不得**删除
    /// A 的同代际键（A 下次触发仍应沉默——幂等保持）。
    /// </summary>
    [Fact]
    public void PruneHandledExceptGeneration_DoesNotCrossIdentity()
    {
        var trigger = new LocalWaitReevaluationTrigger(_ => true);
        var stableA = "run-a|n1|0|0";
        var stableB = "run-b|n1|0|0";
        var a = GenItem(0); a.StableIdentity = stableA; a.ItemId = LocalWaitQueuePolicy.DeriveItemId(stableA);
        var b = GenItem(0); b.StableIdentity = stableB; b.ItemId = LocalWaitQueuePolicy.DeriveItemId(stableB);
        trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, new[] { a, b },
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: "1"); // A、B 各产一条（g1）
        var removed = trigger.PruneHandledExceptGeneration(stableB, "2"); // 仅 B 重激活至 g2
        Assert.Equal(1, removed); // 只删 B 的 g1 键
        // A 在同代际 g1 再次触发 ⇒ 仍沉默（幂等保持，未被越界删除）
        var aAgain = trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, new[] { a },
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: "1");
        Assert.Empty(aAgain.Requests);
        // B 在新代际 g2 ⇒ 重新参选
        var bAgain = trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, new[] { b },
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: "2");
        Assert.Single(bAgain.Requests);
    }

    /// <summary>
    /// 【Wave2 R32 重要-F3 对称化钉死】【突变验证 ✔（M55：迁移键保留移除 ⇒ 红，实测记录 R34 段）】
    /// 迁移期键保留：旧形状键（reval-&lt;摘要&gt;）与「v2 形状、代际段 -」（scope 非 null 且 generation null）
    /// 在修剪中**均保留**。
    /// </summary>
    [Fact]
    public void PruneHandledExceptGeneration_PreservesMigrationKeys()
    {
        var trigger = new LocalWaitReevaluationTrigger(_ => true, stateScope: "epoch-a");
        var items = new[] { GenItem(0) };
        trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, items,
            DateTimeOffset.UnixEpoch, CancellationToken.None); // scope 非 null、无代际 ⇒ v2 键代际段 "-"
        var removed = trigger.PruneHandledExceptGeneration("run-gen|n1|0|0", "9");
        Assert.Equal(0, removed); // 迁移期键保留
        var again = trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, items,
            DateTimeOffset.UnixEpoch, CancellationToken.None);
        Assert.Empty(again.Requests); // 幂等保持（未被修剪撤销）
    }

    /// <summary>
    /// 【突变验证 ✔（M56：规范形回环移除 ⇒ 红）】【Wave2 R32 重要-F4】
    /// 可解析但非 ToString 产物的形态（"+1"/"01"/"-0"）按映射合同一律过期。
    /// </summary>
    [Fact]
    public void Consume_NonCanonicalIntegerForms_AreExpired()
    {
        var item = GenItem(1);
        foreach (var bad in new[] { "+1", "01", "-0" })
        {
            var r = LocalWaitReevaluationConsumer.Consume(Req(item.ItemId, bad), item);
            Assert.False(r.Valid, $"非规范整数形 '{bad}' 按映射合同必须过期");
        }
        Assert.True(LocalWaitReevaluationConsumer.Consume(Req(item.ItemId, "1"), item).Valid);
    }

    /// <summary>
    /// 【突变验证 ✔（M57：Normalize Trim 保留 ⇒ 红）】【Wave2 R34 必改-F2】
    /// 首尾空白代际串（" 1"/"1 "）不是 ToString 逐字符产物 ⇒ 过期（Trim 会把格式漂移洗白成合法请求，
    /// 且与触发器按键原值编码形成幂等键别名）。
    /// </summary>
    [Fact]
    public void Consume_WhitespacePaddedGeneration_IsExpired()
    {
        var item = GenItem(1);
        foreach (var bad in new[] { " 1", "1 " })
        {
            var r = LocalWaitReevaluationConsumer.Consume(Req(item.ItemId, bad), item);
            Assert.False(r.Valid, $"带空白代际串 '{bad}' 必须过期（fail-closed）");
        }
    }

    /// <summary>
    /// 【突变验证 ✔（M58：StableIdentity 比对移除 ⇒ 红）】【Wave2 R34 重要-F4】
    /// 同 ItemId、异 StableIdentity 的替换项（Remove→重登记舞步）不得消费在途请求
    /// （StableIdentity 是模型定义的权威身份；ItemId 是派生标识）。
    /// </summary>
    [Fact]
    public void Consume_StableIdentityMismatch_IsExpired()
    {
        var item = GenItem(1);
        var impostor = GenItem(1);
        impostor.StableIdentity = "run-impostor|n1|0|0"; // 同 ItemId、异权威身份
        var r = LocalWaitReevaluationConsumer.Consume(Req(item.ItemId, "1"), impostor);
        Assert.False(r.Valid);
        Assert.Contains("StableIdentity", r.Reason);
    }

    /// <summary>
    /// 【突变验证 ✔（M59：修剪规范形校验移除 ⇒ 红）】【Wave2 R34 必改-F3】
    /// 非规范 liveGeneration（"01"）**不得执行删除**——否则误删当前规范代际 "1" 的幂等键。
    /// </summary>
    [Fact]
    public void PruneHandledExceptGeneration_NonCanonicalGeneration_DoesNotDelete()
    {
        var trigger = new LocalWaitReevaluationTrigger(_ => true);
        var items = new[] { GenItem(0) };
        trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, items,
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: "1");
        var removed = trigger.PruneHandledExceptGeneration("run-gen|n1|0|0", "01"); // 非规范形
        Assert.Equal(0, removed); // 规范代际 "1" 的键必须存活
        var again = trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, items,
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: "1");
        Assert.Empty(again.Requests); // 幂等保持（未被误删）
    }

    // ---------- C5 消费前复核（IW-04 ABA） ----------

    private static LocalWaitReevaluationRequest Req(string itemId, string? generation)
        => new()
        {
            ItemId = itemId,
            StableIdentity = "run-gen|n1|0|0",
            Trigger = LocalWaitReevaluationTriggerPoint.OccupancyEnded,
            Generation = generation,
        };

    /// <summary>【突变验证 ✔（M49：代际比对移除 ⇒ 红）】【C5】代际匹配 ⇒ 有效消费；ABA（代际不同）⇒ 过期。</summary>
    [Fact]
    public void Consume_GenerationMismatch_IsExpired_MatchIsValid()
    {
        var item = GenItem(2);
        var ok = LocalWaitReevaluationConsumer.Consume(Req(item.ItemId, "2"), item);
        Assert.True(ok.Valid, ok.Reason);
        Assert.False(ok.Expired);

        var aba = LocalWaitReevaluationConsumer.Consume(Req(item.ItemId, "1"), item);
        Assert.False(aba.Valid);
        Assert.True(aba.Expired);
        Assert.Contains("代际", aba.Reason);
    }

    /// <summary>【突变验证 ✔（M50：状态/存在检查移除 ⇒ 红）】【C5】项不存在／非 Waiting ⇒ 过期。</summary>
    [Fact]
    public void Consume_MissingOrNotWaiting_IsExpired()
    {
        var item = GenItem(1);
        Assert.False(LocalWaitReevaluationConsumer.Consume(Req(item.ItemId, "1"), null).Valid);

        var cancelled = GenItem(1);
        cancelled.State = LocalWaitItemState.Cancelled;
        Assert.False(LocalWaitReevaluationConsumer.Consume(Req(item.ItemId, "1"), cancelled).Valid);
    }

    /// <summary>【突变验证 ✔（M51：身份错位检查移除 ⇒ 红）＋无代际请求过期】【C5】</summary>
    [Fact]
    public void Consume_ItemIdMismatch_And_NoGeneration_AreExpired()
    {
        var item = GenItem(1);
        Assert.False(LocalWaitReevaluationConsumer.Consume(Req("wait-deadbeefdeadbeef", "1"), item).Valid);
        Assert.False(LocalWaitReevaluationConsumer.Consume(Req(item.ItemId, null), item).Valid);
    }

    /// <summary>
    /// 【突变验证 ✔（M52：fail-closed 分支移除 ⇒ 红，实测见突变记录）】【Wave2 R31 必改-F1】
    /// 不可解析代际串（"gen-1"/"abc"）**不可能等于** int 代际 ⇒ 按合同③必须过期（fail-closed）——
    /// 旧实现 TryParse 失败直接放行＝ABA 防护被格式漂移整体绕过。
    /// </summary>
    [Fact]
    public void Consume_UnparsableGenerationString_IsExpired_FailClosed()
    {
        var item = GenItem(1);
        foreach (var bad in new[] { "gen-1", "abc", "99999999999999999999" })
        {
            var r = LocalWaitReevaluationConsumer.Consume(Req(item.ItemId, bad), item);
            Assert.False(r.Valid, $"代际串 '{bad}' 不可解析必须过期（fail-closed）");
        }

        // fail-open 核心形态：Generation=0 的项＋不可解析串 ⇒ TryParse 失败 reqGen=0 == 0 ⇒ 旧实现放行
        var zeroItem = GenItem(0);
        var r0 = LocalWaitReevaluationConsumer.Consume(Req(zeroItem.ItemId, "gen-1"), zeroItem);
        Assert.False(r0.Valid, "Generation=0 的项对不可解析代际串同样必须过期（fail-open 核心形态）");
    }
}
