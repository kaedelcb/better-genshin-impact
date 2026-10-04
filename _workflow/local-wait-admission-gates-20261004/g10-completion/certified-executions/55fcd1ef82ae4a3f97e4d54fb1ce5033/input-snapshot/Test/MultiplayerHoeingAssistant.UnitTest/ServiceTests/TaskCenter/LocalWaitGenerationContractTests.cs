using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5 批次 20 Wave2（D-E2=① 代际载体＋C5/IW-04 消费前复核＋义务 i/ii）夹具**。
/// 突变验证标注见各夹具注释（M48-M51，突变实测结果已逐条登记本批台账（§24.120 收口时逐条收录 M48-M63））。
/// </summary>
[Collection("LocalWaitSnapshotProbe")]
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

    private static LocalWaitItem GenItem(long generation = 0)
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

    private void WriteLegacyGenerationItem(LocalWaitItem item, long generation, int version = 3)
    {
        var json = $$"""
        {
          "version": {{version}},
          "items": [
            {
              "itemId": "{{item.ItemId}}",
              "stableIdentity": "{{item.StableIdentity}}",
              "generation": {{generation}},
              "tier": {{(int)item.Tier}},
              "priority": {{item.Priority}}
            }
          ]
        }
        """;
        File.WriteAllText(new LocalWaitQueueStore(_dir).FilePath, json);
    }

    private void WriteV4Queue(string directory, params LocalWaitItem[] items)
    {
        var highWater = items.Length == 0 ? -1 : items.Max(item => item.Generation);
        WriteV4Queue(directory, highWater, items);
    }

    private void WriteV4Queue(string directory, long highWater, params LocalWaitItem[] items)
    {
        Directory.CreateDirectory(directory);
        var queueFile = new LocalWaitQueueFile
        {
            Version = LocalWaitQueueFile.CurrentVersion,
            Items = items.ToList(),
            GenerationHighWater = highWater,
        };
        File.WriteAllText(new LocalWaitQueueStore(directory).FilePath,
            JsonSerializer.Serialize(queueFile, new JsonSerializerOptions { WriteIndented = true }));
    }

    private LocalWaitConsumptionDecision ConsumeUsingStore(
        LocalWaitReevaluationRequest request, LocalWaitItem? currentItem)
    {
        var directory = Path.Combine(_dir, "consume-" + Guid.NewGuid().ToString("N"));
        var store = new LocalWaitQueueStore(directory);
        if (currentItem is null)
            WriteV4Queue(directory);
        else
            WriteV4Queue(directory, currentItem);
        return LocalWaitReevaluationConsumer.Consume(request, store);
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
    /// 输入负代际响亮拒绝；当前 Store 同时由非负形状校验与“登记输入必须为 0”两条守卫阻止它。
    /// </summary>
    [Fact]
    public void Upsert_NegativeGeneration_Rejected()
    {
        var store = new LocalWaitQueueStore(Path.Combine(_dir, "neg"));
        var item = GenItem(0);
        item.Generation = -1;
        Assert.Throws<MultiplayerHoeingAssistant.Services.LocalWaitQueueCorruptException>(() => store.Upsert(item));
    }

    /// <summary>【实现前红夹具：R2 重要-3】登记请求不能自报非零代际。</summary>
    [Fact]
    public void Upsert_NonzeroCallerGeneration_Rejected()
    {
        var store = new LocalWaitQueueStore(_dir);
        var item = GenItem(1);

        Assert.Throws<MultiplayerHoeingAssistant.Services.LocalWaitQueueCorruptException>(() => store.Upsert(item));
        Assert.Empty(store.Load());
    }

#if DEBUG
    /// <summary>【实现前红夹具：R2 重要-3】Store 持久化私有物化副本，不得二次读取可变来件。</summary>
    [Fact]
    public void Upsert_MutableCallerObjectCannotRewriteAllocatedGenerationBeforePersist()
    {
        var store = new LocalWaitQueueStore(_dir);
        var item = GenItem();
        var previous = LocalWaitQueueStore.WriteSnapshotProbeMutator;
        LocalWaitQueueStore.WriteSnapshotProbeMutator = _ => item.Generation = 99;
        try
        {
            Assert.True(store.Upsert(item));
        }
        finally
        {
            LocalWaitQueueStore.WriteSnapshotProbeMutator = previous;
        }

        Assert.Equal(0L, Assert.Single(store.Load()).Generation);
        Assert.Equal(0L, System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.FilePath))!["generationHighWater"]!.GetValue<long>());
    }
#endif

    /// <summary>【实现前红夹具：R2 重要-3】v4 根 H 与项代际必须严格匹配，不得按 legacy 回退。</summary>
    [Fact]
    public void Load_V4MissingOrInvalidHighWaterAndItemGeneration_FailsClosed()
    {
        var item = $$"""
        { "itemId": "wait-0123456789abcdef", "stableIdentity": "run-a|n1|0|0", "tier": 2, "priority": 0, "state": 0 }
        """;
        var itemWithGeneration = item.Replace("\"state\": 0", "\"generation\": 0, \"state\": 0", StringComparison.Ordinal);
        var itemWithNullGeneration = itemWithGeneration.Replace("\"generation\": 0", "\"generation\": null", StringComparison.Ordinal);
        var itemWithStringGeneration = itemWithGeneration.Replace("\"generation\": 0", "\"generation\": \"0\"", StringComparison.Ordinal);
        var cases = new[]
        {
            ("missing-root", "{ \"version\": 4, \"items\": [] }", "generationHighWater"),
            ("null-root", "{ \"version\": 4, \"generationHighWater\": null, \"items\": [] }", "generationHighWater"),
            ("string-root", "{ \"version\": 4, \"generationHighWater\": \"0\", \"items\": [] }", "generationHighWater"),
            ("negative-root", "{ \"version\": 4, \"generationHighWater\": -2, \"items\": [] }", "generationHighWater"),
            ("fractional-root", "{ \"version\": 4, \"generationHighWater\": 1.5, \"items\": [] }", "generationHighWater"),
            ("overflow-root", "{ \"version\": 4, \"generationHighWater\": 9223372036854775808, \"items\": [] }", "generationHighWater"),
            ("unallocated-with-item", $"{{ \"version\": 4, \"generationHighWater\": -1, \"items\": [{itemWithGeneration}] }}", "generationHighWater"),
            ("root-below-cancelled-item", $"{{ \"version\": 4, \"generationHighWater\": 0, \"items\": [{{ \"itemId\": \"wait-0123456789abcdef\", \"stableIdentity\": \"run-a|n1|0|0\", \"generation\": 1, \"tier\": 2, \"priority\": 0, \"state\": 1 }}] }}", "generationHighWater"),
            ("missing-item-generation", $"{{ \"version\": 4, \"generationHighWater\": 0, \"items\": [{item}] }}", "generation"),
            ("null-item-generation", $"{{ \"version\": 4, \"generationHighWater\": 0, \"items\": [{itemWithNullGeneration}] }}", "generation"),
            ("string-item-generation", $"{{ \"version\": 4, \"generationHighWater\": 0, \"items\": [{itemWithStringGeneration}] }}", "generation"),
            ("negative-item-generation", "{ \"version\": 4, \"generationHighWater\": 0, \"items\": [{ \"itemId\": \"wait-0123456789abcdef\", \"stableIdentity\": \"run-a|n1|0|0\", \"generation\": -1, \"tier\": 2, \"priority\": 0, \"state\": 1 }] }", "generation"),
            ("fractional-item-generation", "{ \"version\": 4, \"generationHighWater\": 0, \"items\": [{ \"itemId\": \"wait-0123456789abcdef\", \"stableIdentity\": \"run-a|n1|0|0\", \"generation\": 0.5, \"tier\": 2, \"priority\": 0, \"state\": 1 }] }", "generation"),
            ("overflow-item-generation", "{ \"version\": 4, \"generationHighWater\": 9223372036854775807, \"items\": [{ \"itemId\": \"wait-0123456789abcdef\", \"stableIdentity\": \"run-a|n1|0|0\", \"generation\": 9223372036854775808, \"tier\": 2, \"priority\": 0, \"state\": 1 }] }", "generation"),
        };

        foreach (var (name, json, expectedMarker) in cases)
        {
            var dir = Path.Combine(_dir, name);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "wait-queue.json");
            File.WriteAllText(path, json);
            var originalBytes = File.ReadAllBytes(path);
            var exception = Record.Exception(() => new LocalWaitQueueStore(dir).Load());
            Assert.True(exception is MultiplayerHoeingAssistant.Services.LocalWaitQueueCorruptException,
                $"{name}: expected LocalWaitQueueCorruptException, actual={exception?.GetType().Name ?? "no exception"}");
            var corruptException = (MultiplayerHoeingAssistant.Services.LocalWaitQueueCorruptException)exception!;
            Assert.True(corruptException.Message.Contains(expectedMarker, StringComparison.OrdinalIgnoreCase),
                $"{name}: expected diagnostic marker '{expectedMarker}', actual '{corruptException.Message}'");
            Assert.Equal(Convert.ToHexString(originalBytes), Convert.ToHexString(File.ReadAllBytes(path)));
        }
    }

    [Fact]
    public void Load_V4ItemGenerationOverflowWithValidHighWater_FailsClosedWithoutMutation()
    {
        const string json = "{ \"version\": 4, \"generationHighWater\": 9223372036854775807, \"items\": [{ \"itemId\": \"wait-0123456789abcdef\", \"stableIdentity\": \"run-a|n1|0|0\", \"generation\": 9223372036854775808, \"tier\": 2, \"priority\": 0, \"state\": 0 }] }";
        var path = Path.Combine(_dir, "wait-queue.json");
        File.WriteAllText(path, json);
        var originalBytes = File.ReadAllBytes(path);

        var exception = Assert.Throws<MultiplayerHoeingAssistant.Services.LocalWaitQueueCorruptException>(
            () => new LocalWaitQueueStore(_dir).Load());

        Assert.Contains("generation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("generationHighWater", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Convert.ToHexString(originalBytes), Convert.ToHexString(File.ReadAllBytes(path)));
    }

    [Fact]
    public void QueueFileVersion_AdvancesForPersistedHighWater()
    {
        Assert.Equal(4, LocalWaitQueueFile.CurrentVersion);
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
            var r = ConsumeUsingStore(Req(item.ItemId, bad), item);
            Assert.False(r.Valid, $"非规范整数形 '{bad}' 按映射合同必须过期");
        }
        Assert.True(ConsumeUsingStore(Req(item.ItemId, "1"), item).Valid);
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
            var r = ConsumeUsingStore(Req(item.ItemId, bad), item);
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
        var r = ConsumeUsingStore(Req(item.ItemId, "1"), impostor);
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

    /// <summary>【实现前红夹具：R2 重要-5】修剪必须接受规范 long 代际，不能把 int 范围外代际当作无效。</summary>
    [Fact]
    public void PruneHandledExceptGeneration_SupportsLongGeneration()
    {
        var trigger = new LocalWaitReevaluationTrigger(_ => true);
        var items = new[] { GenItem(0) };
        var oldGeneration = "2147483648";
        var liveGeneration = "2147483649";

        Assert.Single(trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, items,
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: oldGeneration).Requests);
        Assert.Single(trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, items,
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: liveGeneration).Requests);

        Assert.Equal(1, trigger.PruneHandledExceptGeneration("run-gen|n1|0|0", liveGeneration));
        Assert.Empty(trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, items,
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: liveGeneration).Requests);
    }

    [Fact]
    public void LongGeneration_RoundTripsThroughStoreTriggerConsumerAndPruning()
    {
        const string oldGeneration = "2147483648";
        var first = GenItem(2_147_483_648L);
        WriteV4Queue(_dir, first);
        var store = new LocalWaitQueueStore(_dir);
        var currentFirst = Assert.Single(store.Load());
        Assert.Equal(2_147_483_648L, currentFirst.Generation);

        var other = GenItem();
        other.StableIdentity = "run-other|n1|0|0";
        other.ItemId = LocalWaitQueuePolicy.DeriveItemId(other.StableIdentity);
        var trigger = new LocalWaitReevaluationTrigger(_ => true, stateScope: "epoch-a");
        var migration = trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, new[] { currentFirst, other },
            DateTimeOffset.UnixEpoch, CancellationToken.None);
        Assert.Equal(2, migration.Requests.Count);
        var oldBatch = trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, new[] { currentFirst },
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: oldGeneration);
        Assert.Single(oldBatch.Requests);
        var oldRequest = Assert.Single(oldBatch.Requests.Where(request => request.ItemId == currentFirst.ItemId));
        Assert.True(LocalWaitReevaluationConsumer.Consume(oldRequest, store).Valid);

        // Each Trigger batch carries one explicit generation. Keep the unrelated gen0 item in its
        // own batch so this test never labels it with currentFirst's long generation.
        var otherGeneration = "0";
        Assert.Single(trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, new[] { other },
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: otherGeneration).Requests);

        Assert.True(store.Cancel(currentFirst.ItemId, "advance long generation", DateTimeOffset.UtcNow));
        Assert.True(store.Upsert(GenItem()));
        var current = Assert.Single(store.Load());
        Assert.Equal(2_147_483_649L, current.Generation);
        var liveGeneration = current.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var liveRequest = Assert.Single(trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded,
            new[] { current }, DateTimeOffset.UnixEpoch, CancellationToken.None, generation: liveGeneration).Requests);

        Assert.False(LocalWaitReevaluationConsumer.Consume(oldRequest, store).Valid);
        Assert.True(LocalWaitReevaluationConsumer.Consume(liveRequest, store).Valid);
        Assert.Equal(1, trigger.PruneHandledExceptGeneration(current.StableIdentity, liveGeneration));
        Assert.Empty(trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, new[] { current },
            DateTimeOffset.UnixEpoch, CancellationToken.None).Requests); // 保留无代际迁移键
        Assert.Empty(trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, new[] { current },
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: liveGeneration).Requests); // 保留当前键
        Assert.Empty(trigger.Decide(LocalWaitReevaluationTriggerPoint.OccupancyEnded, new[] { other },
            DateTimeOffset.UnixEpoch, CancellationToken.None, generation: otherGeneration).Requests); // 保留其他身份键
    }

    [Fact]
    public void LongMaxValue_LastAllocationSucceeds_ThenRejectsNewGenerationsWithoutChangingFile()
    {
        WriteV4Queue(_dir, long.MaxValue - 1);
        var store = new LocalWaitQueueStore(_dir);
        var registration = GenItem();

        Assert.True(store.Upsert(registration));
        var current = Assert.Single(store.Load());
        Assert.Equal(long.MaxValue, current.Generation);
        var atMaximumBytes = File.ReadAllBytes(store.FilePath);

        Assert.False(store.Upsert(GenItem())); // Waiting 幂等操作不需要新代际
        Assert.Equal(Convert.ToHexString(atMaximumBytes), Convert.ToHexString(File.ReadAllBytes(store.FilePath)));
        Assert.True(store.Cancel(current.ItemId, "maximum reached", DateTimeOffset.UtcNow));
        var afterCancel = File.ReadAllBytes(store.FilePath);
        var reactivation = Record.Exception(() => store.Upsert(GenItem()));
        Assert.IsType<LocalWaitGenerationExhaustedException>(reactivation);
        Assert.Equal(Convert.ToHexString(afterCancel), Convert.ToHexString(File.ReadAllBytes(store.FilePath)));

        Assert.True(store.Remove(current.ItemId));
        var emptyAtMaximum = File.ReadAllBytes(store.FilePath);
        Assert.Empty(store.Load());
        var newItem = Record.Exception(() => store.Upsert(GenItem()));
        Assert.IsType<LocalWaitGenerationExhaustedException>(newItem);
        Assert.Equal(Convert.ToHexString(emptyAtMaximum), Convert.ToHexString(File.ReadAllBytes(store.FilePath)));
        Assert.Equal(long.MaxValue, System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.FilePath))!["generationHighWater"]!.GetValue<long>());
    }

    [Fact]
    public void LongMaxValue_CleanupCancelAndPruneRemainAvailableWithoutLoweringHighWater()
    {
        var item = GenItem(long.MaxValue);
        WriteV4Queue(_dir, item);
        var store = new LocalWaitQueueStore(_dir, TimeSpan.FromMinutes(1));
        var now = DateTimeOffset.Parse("2026-09-27T00:00:00+00:00");

        var cancelled = store.PersistCleanup(_ => "max generation cleanup", now);
        Assert.Equal(1, cancelled.Cancelled);
        Assert.Equal(long.MaxValue, Assert.Single(store.Load()).Generation);

        var pruned = store.PersistCleanup(_ => null, now.AddMinutes(2));
        Assert.Equal(1, pruned.Pruned);
        Assert.Empty(store.Load());
        Assert.Equal(long.MaxValue,
            System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.FilePath))!["generationHighWater"]!.GetValue<long>());
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
        var ok = ConsumeUsingStore(Req(item.ItemId, "2"), item);
        Assert.True(ok.Valid, ok.Reason);
        Assert.False(ok.Expired);

        var aba = ConsumeUsingStore(Req(item.ItemId, "1"), item);
        Assert.False(aba.Valid);
        Assert.True(aba.Expired);
        Assert.Contains("代际", aba.Reason);
    }

    /// <summary>【突变验证 ✔（M50：状态/存在检查移除 ⇒ 红）】【C5】项不存在／非 Waiting ⇒ 过期。</summary>
    [Fact]
    public void Consume_MissingOrNotWaiting_IsExpired()
    {
        var item = GenItem(1);
        Assert.False(ConsumeUsingStore(Req(item.ItemId, "1"), null).Valid);

        var cancelled = GenItem(1);
        cancelled.State = LocalWaitItemState.Cancelled;
        Assert.False(ConsumeUsingStore(Req(item.ItemId, "1"), cancelled).Valid);
    }

    /// <summary>【C5】未知 ItemId 因 Store 查无此项而过期；已找到的项若请求无代际也过期。</summary>
    [Fact]
    public void Consume_UnknownItemId_And_NoGeneration_AreExpired()
    {
        var item = GenItem(1);
        Assert.False(ConsumeUsingStore(Req("wait-deadbeefdeadbeef", "1"), item).Valid); // Store 按未知 ItemId 查无此项
        Assert.False(ConsumeUsingStore(Req(item.ItemId, null), item).Valid); // 已找到项，但请求无代际
    }

    [Fact]
    public void Consume_CorruptStoreFailsLoudly()
    {
        var store = new LocalWaitQueueStore(_dir);
        File.WriteAllText(store.FilePath, "{ corrupt queue");

        Assert.Throws<LocalWaitQueueCorruptException>(() =>
            LocalWaitReevaluationConsumer.Consume(Req("wait-unknown", "0"), store));
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
            var r = ConsumeUsingStore(Req(item.ItemId, bad), item);
            Assert.False(r.Valid, $"代际串 '{bad}' 不可解析必须过期（fail-closed）");
        }

        // fail-open 核心形态：Generation=0 的项＋不可解析串 ⇒ TryParse 失败 reqGen=0 == 0 ⇒ 旧实现放行
        var zeroItem = GenItem(0);
        var r0 = ConsumeUsingStore(Req(zeroItem.ItemId, "gen-1"), zeroItem);
        Assert.False(r0.Valid, "Generation=0 的项对不可解析代际串同样必须过期（fail-open 核心形态）");
    }

    /// <summary>
    /// 【红夹具（实现前预期失败）：BO-10/BO-12】Remove 是终局，但不能把 Store 文件内已发代际退回 gen0。
    /// 反例：gen0 请求在途 → Remove → 同身份新登记；新项必须有更高代际，旧请求必须过期。
    /// </summary>
    [Fact]
    public void RemoveThenReregister_SameIdentity_AdvancesGenerationAndExpiresOldRequest()
    {
        var store = new LocalWaitQueueStore(_dir);
        var first = GenItem();
        store.Upsert(first);
        var oldRequest = Req(first.ItemId, "0");

        Assert.Equal(0L, Assert.Single(store.Load()).Generation);
        Assert.True(store.Remove(first.ItemId));

        var afterRestart = new LocalWaitQueueStore(_dir);
        Assert.True(afterRestart.Upsert(GenItem()));
        var current = Assert.Single(afterRestart.Load());
        Assert.Equal(1L, current.Generation);

        // Use the same reopened Store that read the persisted generation; C5 must reject the old
        // decision against durable state and accept only the current lifecycle request.
        var stale = LocalWaitReevaluationConsumer.Consume(oldRequest, afterRestart);
        Assert.False(stale.Valid);
        Assert.True(stale.Expired);
        Assert.Contains("代际", stale.Reason);

        var currentRequest = Req(current.ItemId, current.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var currentDecision = LocalWaitReevaluationConsumer.Consume(currentRequest, afterRestart);
        var decisions = new[] { stale, currentDecision };
        Assert.Equal(1, decisions.Count(decision => decision.Valid));
        Assert.Equal(1, decisions.Count(decision => decision.Expired));
    }

    /// <summary>
    /// 【红夹具（实现前预期失败）：BO-12】24h 墓碑裁剪移除记录后仍须保存高水位，避免同身份重登回绕。
    /// </summary>
    [Fact]
    public void PrunedTombstoneThenReregister_SameIdentity_AdvancesGenerationAndExpiresOldRequest()
    {
        var store = new LocalWaitQueueStore(_dir, TimeSpan.FromMinutes(10));
        var item = GenItem();
        store.Upsert(item);
        var oldRequest = Req(item.ItemId, "0");
        var cancelledAt = DateTimeOffset.UtcNow;

        Assert.True(store.Cancel(item.ItemId, "fixture cancel", cancelledAt));
        var cleanup = store.PersistCleanup(_ => null, cancelledAt.AddMinutes(11));
        Assert.Equal(1, cleanup.Pruned);
        Assert.Empty(store.Load());

        var afterRestart = new LocalWaitQueueStore(_dir, TimeSpan.FromMinutes(10));
        Assert.True(afterRestart.Upsert(GenItem()));
        var current = Assert.Single(afterRestart.Load());
        Assert.Equal(1L, current.Generation);

        // Count C5 outcomes from the reopened, persisted queue after pruning/re-registration.
        var stale = LocalWaitReevaluationConsumer.Consume(oldRequest, afterRestart);
        Assert.False(stale.Valid);
        Assert.True(stale.Expired);

        var currentRequest = Req(current.ItemId, current.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var currentDecision = LocalWaitReevaluationConsumer.Consume(currentRequest, afterRestart);
        var decisions = new[] { stale, currentDecision };
        Assert.Equal(1, decisions.Count(decision => decision.Valid));
        Assert.Equal(1, decisions.Count(decision => decision.Expired));
    }

    /// <summary>
    /// 【实现前红夹具：R1 重要-1】Cleanup 回调重入另一同路径 Store 写入后，外层旧快照不得覆盖新项。
    /// </summary>
    [Fact]
    public async Task Cleanup_ReentrantSamePathUpsert_IsNotLostByOuterSnapshot()
    {
        var store = new LocalWaitQueueStore(_dir);
        var otherStore = new LocalWaitQueueStore(_dir);
        var existing = GenItem();
        store.Upsert(existing);

        var nested = GenItem();
        nested.StableIdentity = "run-nested|n1|0|0";
        nested.ItemId = LocalWaitQueuePolicy.DeriveItemId(nested.StableIdentity);

        using var releaseCallback = new ManualResetEventSlim(false);
        var callbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupTask = Task.Run(() => store.PersistCleanup(_ =>
        {
            callbackEntered.TrySetResult(true);
            if (!releaseCallback.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test did not release the cleanup callback barrier.");
            return "outer cleanup decision";
        }, DateTimeOffset.UtcNow));

        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var innerWrite = Task.Run(() => otherStore.Upsert(nested));
        var innerFinishedWhileCallbackActive = await Task.WhenAny(innerWrite, Task.Delay(TimeSpan.FromSeconds(5))) == innerWrite;
        byte[]? innerCommittedBytes = innerFinishedWhileCallbackActive ? File.ReadAllBytes(store.FilePath) : null;
        releaseCallback.Set();

        var innerWriteException = await Record.ExceptionAsync(async () => await innerWrite);
        var exception = await Record.ExceptionAsync(async () => await cleanupTask);

        Assert.True(innerFinishedWhileCallbackActive,
            "another same-path Store write must complete while the external cleanup callback is still active");
        Assert.Null(innerWriteException);
        Assert.Equal("LocalWaitQueueConcurrentUpdateException", exception?.GetType().Name);
        Assert.NotNull(innerCommittedBytes);
        Assert.Equal(Convert.ToHexString(innerCommittedBytes!), Convert.ToHexString(File.ReadAllBytes(store.FilePath)));
        var persisted = store.Load();
        Assert.Equal(LocalWaitItemState.Waiting, Assert.Single(persisted.Where(item => item.ItemId == existing.ItemId)).State);
        Assert.Equal(1L, Assert.Single(persisted.Where(item => item.ItemId == nested.ItemId)).Generation);
        Assert.Equal(1L, System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.FilePath))!["generationHighWater"]!.GetValue<long>());
    }

    [Fact]
    public void Cleanup_CallbackMutationCannotAlterPersistedIdentityOrGeneration()
    {
        var store = new LocalWaitQueueStore(_dir);
        var item = GenItem();
        store.Upsert(item);
        var originalId = item.ItemId;
        var originalIdentity = item.StableIdentity;

        store.PersistCleanup(callbackItem =>
        {
            callbackItem.ItemId = "wait-mutated-by-callback";
            callbackItem.StableIdentity = "run-mutated-by-callback";
            callbackItem.Generation = 99;
            callbackItem.State = LocalWaitItemState.Cancelled;
            return "fixture invalidation";
        }, DateTimeOffset.UtcNow);

        var persisted = Assert.Single(store.Load());
        Assert.Equal(originalId, persisted.ItemId);
        Assert.Equal(originalIdentity, persisted.StableIdentity);
        Assert.Equal(0L, persisted.Generation);
        Assert.Equal(LocalWaitItemState.Cancelled, persisted.State);
    }

    [Fact]
    public void Cleanup_RejectsRawByteChangeEvenWhenQueueStillParsesTheSame()
    {
        var store = new LocalWaitQueueStore(_dir);
        var item = GenItem();
        store.Upsert(item);
        byte[]? externallyChangedBytes = null;

        var exception = Record.Exception(() => store.PersistCleanup(_ =>
        {
            File.AppendAllText(store.FilePath, Environment.NewLine + " ");
            externallyChangedBytes = File.ReadAllBytes(store.FilePath);
            return "outer cleanup must detect any raw snapshot change";
        }, DateTimeOffset.UtcNow));

        Assert.IsType<LocalWaitQueueConcurrentUpdateException>(exception);
        Assert.NotNull(externallyChangedBytes);
        Assert.Equal(Convert.ToHexString(externallyChangedBytes!), Convert.ToHexString(File.ReadAllBytes(store.FilePath)));
        Assert.Equal(LocalWaitItemState.Waiting, Assert.Single(store.Load()).State);
    }

    [Fact]
    public void Persist_ReplaceFailurePreservesPreviousQueueBytes()
    {
        var store = new LocalWaitQueueStore(_dir);
        var item = GenItem();
        store.Upsert(item);
        var before = File.ReadAllBytes(store.FilePath);

        Exception? writeFailure;
        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            writeFailure = Record.Exception(() => store.Cancel(item.ItemId, "replace failure probe", DateTimeOffset.UtcNow));
        }

        Assert.NotNull(writeFailure);
        Assert.Equal(Convert.ToHexString(before), Convert.ToHexString(File.ReadAllBytes(store.FilePath)));
        Assert.Equal(LocalWaitItemState.Waiting, Assert.Single(store.Load()).State);
    }

#if DEBUG
    [Fact]
    public void Persist_PartialTemporaryWriteFailurePreservesPreviousQueueBytesAndCleansTemp()
    {
        var store = new LocalWaitQueueStore(_dir);
        var item = GenItem();
        store.Upsert(item);
        var before = File.ReadAllBytes(store.FilePath);
        var previous = LocalWaitQueueStore.BeforeTemporaryFileWriteProbe;
        LocalWaitQueueStore.BeforeTemporaryFileWriteProbe = (tempPath, payload) =>
        {
            File.WriteAllBytes(tempPath, payload[..Math.Max(1, payload.Length / 2)]);
            throw new IOException("injected partial temporary-file write failure");
        };

        try
        {
            Assert.Throws<IOException>(() => store.Cancel(item.ItemId, "partial write failure", DateTimeOffset.UtcNow));
        }
        finally
        {
            LocalWaitQueueStore.BeforeTemporaryFileWriteProbe = previous;
        }

        Assert.Equal(Convert.ToHexString(before), Convert.ToHexString(File.ReadAllBytes(store.FilePath)));
        Assert.Empty(Directory.GetFiles(_dir, ".wait-queue.*.tmp"));
        Assert.Equal(LocalWaitItemState.Waiting, Assert.Single(store.Load()).State);
    }
#endif

    /// <summary>
    /// 【实现前红夹具：R1 重要-2】迁移必须预留所有 legacy int 代际；只看仍存项会重用已删除项历史代际。
    /// </summary>
    [Fact]
    public void LegacyV3WithDeletedHigherGeneration_ReservesWholeIntRange()
    {
        var legacyRemaining = GenItem();
        legacyRemaining.StableIdentity = "run-legacy-remains|n1|0|0";
        legacyRemaining.ItemId = LocalWaitQueuePolicy.DeriveItemId(legacyRemaining.StableIdentity);
        WriteLegacyGenerationItem(legacyRemaining, 9);

        var store = new LocalWaitQueueStore(_dir);
        var newLifecycle = GenItem();
        // 反例历史可为已删除 A(10)，而文件只剩 B(9)；H 不能仅从当前存项最大值恢复。
        var oldDeletedRequest = Req(newLifecycle.ItemId, "10");
        var oldMaximumRequest = Req(newLifecycle.ItemId, int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(store.Upsert(newLifecycle));

        var reopened = new LocalWaitQueueStore(_dir);
        var newPersisted = Assert.Single(reopened.Load().Where(item => item.ItemId == newLifecycle.ItemId));
        var deletedRequestDecision = LocalWaitReevaluationConsumer.Consume(oldDeletedRequest, reopened);
        Assert.False(deletedRequestDecision.Valid);
        Assert.Contains("代际", deletedRequestDecision.Reason);
        var oldRequestDecision = LocalWaitReevaluationConsumer.Consume(oldMaximumRequest, reopened);
        Assert.False(oldRequestDecision.Valid);
        Assert.True(oldRequestDecision.Expired);
        Assert.Contains("代际", oldRequestDecision.Reason);
        Assert.True(newPersisted.Generation > int.MaxValue,
            "旧文件曾删除的代际可能高于当前最大项；新分配必须越过 legacy int 全范围。");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void NonEmptyLegacyV1V2_PreservesGen0RequestAndReservesRangeForNewLifecycle(int version)
    {
        var legacyItem = GenItem(0);
        var legacyJson = $$"""
        {
          "version": {{version}},
          "items": [{
            "itemId": "{{legacyItem.ItemId}}",
            "stableIdentity": "{{legacyItem.StableIdentity}}",
            "tier": {{(int)legacyItem.Tier}},
            "priority": {{legacyItem.Priority}},
            "state": 0
          }]
        }
        """;
        var path = new LocalWaitQueueStore(_dir).FilePath;
        File.WriteAllText(path, legacyJson);
        var originalBytes = File.ReadAllBytes(path);
        var store = new LocalWaitQueueStore(_dir);
        var oldRequest = Req(legacyItem.ItemId, "0");

        Assert.Equal(0L, Assert.Single(store.Load()).Generation);
        Assert.Equal(Convert.ToHexString(originalBytes), Convert.ToHexString(File.ReadAllBytes(path)));
        Assert.True(LocalWaitReevaluationConsumer.Consume(oldRequest, store).Valid);

        var newItem = GenItem();
        newItem.StableIdentity = $"run-v{version}-new|n1|0|0";
        newItem.ItemId = LocalWaitQueuePolicy.DeriveItemId(newItem.StableIdentity);
        Assert.True(store.Upsert(newItem));
        var reopened = new LocalWaitQueueStore(_dir);
        Assert.Equal((long)int.MaxValue + 1,
            Assert.Single(reopened.Load().Where(item => item.ItemId == newItem.ItemId)).Generation);
        Assert.True(LocalWaitReevaluationConsumer.Consume(oldRequest, reopened).Valid);
    }

    [Fact]
    public void LegacyMigration_RemoveHighestItemOnFirstWrite_PreservesMigratedHighWater()
    {
        var item = GenItem();
        WriteLegacyGenerationItem(item, int.MaxValue);
        var path = new LocalWaitQueueStore(_dir).FilePath;
        var beforeLoad = File.ReadAllBytes(path);
        var store = new LocalWaitQueueStore(_dir);

        Assert.Equal((long)int.MaxValue, Assert.Single(store.Load()).Generation);
        Assert.Equal(Convert.ToHexString(beforeLoad), Convert.ToHexString(File.ReadAllBytes(path)));
        Assert.True(store.Remove(item.ItemId));

        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(4, root["version"]!.GetValue<int>());
        Assert.Equal((long)int.MaxValue, root["generationHighWater"]!.GetValue<long>());
        Assert.Empty(root["items"]!.AsArray());
    }

    [Fact]
    public void LegacyMigration_PruneLastTombstoneOnFirstWrite_PreservesMigratedHighWater()
    {
        var item = GenItem();
        var json = $$"""
        {
          "version": 3,
          "items": [{
            "itemId": "{{item.ItemId}}",
            "stableIdentity": "{{item.StableIdentity}}",
            "generation": 7,
            "tier": {{(int)item.Tier}},
            "priority": {{item.Priority}},
            "state": 1,
            "cancelledAtUtc": "2020-01-01T00:00:00+00:00"
          }]
        }
        """;
        File.WriteAllText(new LocalWaitQueueStore(_dir).FilePath, json);
        var store = new LocalWaitQueueStore(_dir, TimeSpan.FromMinutes(1));

        var result = store.PersistCleanup(_ => null, DateTimeOffset.Parse("2020-01-01T00:02:00+00:00"));

        Assert.Equal(1, result.Pruned);
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.FilePath))!;
        Assert.Equal(4, root["version"]!.GetValue<int>());
        Assert.Equal((long)int.MaxValue, root["generationHighWater"]!.GetValue<long>());
        Assert.Empty(root["items"]!.AsArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EmptyLegacyFile_ReservesLegacyIntRangeWithoutLoadMutation(int version)
    {
        File.WriteAllText(new LocalWaitQueueStore(_dir).FilePath, $$"""
        { "version": {{version}}, "items": [] }
        """);
        var beforeLoad = File.ReadAllBytes(new LocalWaitQueueStore(_dir).FilePath);
        var store = new LocalWaitQueueStore(_dir);

        Assert.Empty(store.Load());
        Assert.Equal(Convert.ToHexString(beforeLoad), Convert.ToHexString(File.ReadAllBytes(store.FilePath)));
        Assert.True(store.Upsert(GenItem()));
        Assert.Equal((long)int.MaxValue + 1, Assert.Single(store.Load()).Generation);
    }

    /// <summary>
    /// 【红夹具（实现前预期失败）：代际不应在 int.MaxValue 处回绕为负数；v3 读入后须安全扩展。
    /// </summary>
    [Fact]
    public void ReactivationAfterIntMaxGeneration_DoesNotWrap()
    {
        var item = GenItem();
        WriteLegacyGenerationItem(item, int.MaxValue);
        var store = new LocalWaitQueueStore(_dir);
        var now = DateTimeOffset.UtcNow;

        Assert.Equal((long)int.MaxValue, Assert.Single(store.Load()).Generation);
        Assert.True(store.Cancel(item.ItemId, "fixture cancel", now));

        var reactivationRequest = Assert.Single(store.Load()); // 保留同载荷快照，但调用方不得自报已分配代际。
        reactivationRequest.Generation = 0;
        Assert.Equal(0, reactivationRequest.Generation);
        var exception = Record.Exception(() => store.Upsert(reactivationRequest));
        Assert.Null(exception);
        Assert.Equal(2_147_483_648L, Assert.Single(store.Load()).Generation);
    }

    /// <summary>
    /// 【实现前红夹具：R1 重要-4】单独证明仅 Remove 后把旧缓存项传给旧 API 仍会放行；不依赖重登代际断言。
    /// </summary>
    [Fact]
    public void Consume_RemovedCachedSnapshot_IsExpired()
    {
        var store = new LocalWaitQueueStore(_dir);
        var item = GenItem();
        store.Upsert(item);
        var request = Req(item.ItemId, "0");
        var cachedSnapshot = Assert.Single(store.Load());

        Assert.True(store.Remove(item.ItemId));
        Assert.Equal(item.ItemId, cachedSnapshot.ItemId);
        var decision = LocalWaitReevaluationConsumer.Consume(request, store);
        Assert.False(decision.Valid);
        Assert.True(decision.Expired);
        Assert.DoesNotContain(
            typeof(LocalWaitReevaluationConsumer).GetMethods()
                .Where(method => method.Name == nameof(LocalWaitReevaluationConsumer.Consume))
                .SelectMany(method => method.GetParameters()),
            parameter => parameter.ParameterType == typeof(LocalWaitItem));
    }
}
