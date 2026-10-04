using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// WorkflowStore（R4.1）验收夹具：
/// v1 往返无损（含未识别字段袋）、坏文件四类隔离（语法坏/数组形状/schema 不符/版本不支持）、
/// 未支持 kind 可预览但检出、修订守卫（无期望拒绝覆盖/过期冲突/正确放行且修改变更）、
/// 原子写无临时残件 + 写前备份、隔离文件读不动字节（绝不读失败回空自动保存）。
/// 涉盘用例走临时目录，finally 清理。
/// </summary>
public class WorkflowStoreTests : IDisposable
{
    private readonly string _dir;

    public WorkflowStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wfstore-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir); // R4.8 二轮：Store 构造零副作用（目录首次写入才建），测试落盘文件自备目录
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>R1 v1 样例形状 + 各级未识别字段（futureField/refExtra），验证往返不丢。</summary>
    private const string SampleFlowJson = """
        {
          "schema": "mistletoe.workflow",
          "schemaVersion": 1,
          "name": "工作日计划",
          "futureField": { "nested": true },
          "nodes": [
            {
              "nodeId": "n-0266298c",
              "kind": "resource.oneDragonConfig",
              "ref": {
                "config": "日常综合",
                "configKey": "日常综合#ade47fd2",
                "revision": "eefe09f40d731dcfa02bc87547bc6be06f7838e6ced0f27bb7f0d6b5636dcf5c",
                "refExtra": "keep-me"
              },
              "strategies": [
                {
                  "kind": "condition.weekdays",
                  "days": ["周一", "周三", "周五"],
                  "dayBoundary": "localMidnight",
                  "note": "旧茶包为本地日期语义，非游戏服4点"
                },
                {
                  "kind": "prerequisite.account",
                  "uid": "100000001",
                  "bindingCode": "13800138000",
                  "sensitive": true
                }
              ]
            }
          ],
          "triggers": [
            { "kind": "trigger.time", "time": "06:30", "missPolicy": "nextDay" }
          ],
          "loop": { "mode": "scheduled", "time": "04:00", "skipAcrossDays": true },
          "terminal": [
            { "kind": "terminal.completionAction", "action": "关闭游戏并关机" }
          ],
          "execution": { "suppressConfigCompletionAction": true },
          "activation": { "status": "candidate-ready" }
        }
        """;

    [Fact]
    public void RoundTrip_V1Sample_PreservesKnownAndUnknownFields()
    {
        var store = new WorkflowStore(_dir);
        var src = WriteTemp("sample.flow.json", SampleFlowJson);

        var revision = store.Import(src);
        Assert.False(string.IsNullOrWhiteSpace(revision));

        var entry = Assert.Single(store.List());
        Assert.Equal(WorkflowFileStatus.Ready, entry.Status);
        Assert.Empty(entry.UnsupportedKinds);
        Assert.StartsWith("wf-", entry.WorkflowId);

        var doc = store.Load(entry.WorkflowId);
        Assert.Equal("工作日计划", doc.Name);
        Assert.Equal("mistletoe.workflow", doc.Schema);
        Assert.Equal(1, doc.SchemaVersion);
        Assert.Equal(entry.WorkflowId, doc.WorkflowId); // 稳定身份已指派并持久化
        var node = Assert.Single(doc.Nodes);
        Assert.Equal("resource.oneDragonConfig", node.Kind);
        Assert.Equal("日常综合#ade47fd2", node.Ref!.ConfigKey);
        Assert.Equal(2, node.Strategies.Count);
        Assert.Equal(["周一", "周三", "周五"], node.Strategies[0].GetStringArray("days"));
        Assert.Equal("localMidnight", node.Strategies[0].GetString("dayBoundary"));
        Assert.Equal("scheduled", doc.Loop!.Mode);
        Assert.True(doc.Execution!.SuppressConfigCompletionAction);
        Assert.Equal("candidate-ready", doc.Activation!.Status);

        // 未识别字段袋往返：文档级/引用级
        Assert.NotNull(doc.ExtensionData);
        Assert.True(doc.ExtensionData!.ContainsKey("futureField"));
        Assert.Equal("keep-me", node.Ref.ExtensionData!["refExtra"].GetString());

        // 保存后未识别字段仍在盘上（序列化框架不变，加法扩展）
        var revision2 = store.Save(doc, revision);
        // 内容未变 → 内容哈希修订幂等不变（修改才生成新修订，锚点 2）
        Assert.Equal(revision, revision2);
        var raw = File.ReadAllText(Path.Combine(_dir, entry.WorkflowId + ".flow.json"));
        using var parsed = JsonDocument.Parse(raw);
        Assert.True(parsed.RootElement.GetProperty("futureField").GetProperty("nested").GetBoolean());
        Assert.Equal("keep-me", parsed.RootElement.GetProperty("nodes")[0].GetProperty("ref").GetProperty("refExtra").GetString());
    }

    [Theory]
    [InlineData("{ 这不是JSON", "jsonInvalid")]
    [InlineData("[1,2,3]", "jsonShapeInvalid")]
    [InlineData("{\"schema\":\"other.workflow\",\"schemaVersion\":1}", "schemaMismatch")]
    [InlineData("{\"schema\":\"mistletoe.workflow\",\"schemaVersion\":2}", "schemaVersionUnsupported")]
    [InlineData("{\"schema\":\"mistletoe.workflow\"}", "schemaVersionUnsupported")]
    public void BadFiles_AreQuarantined_BytesUntouched(string content, string expectedReason)
    {
        var store = new WorkflowStore(_dir);
        var file = Path.Combine(_dir, "wf-bad0001.flow.json");
        File.WriteAllText(file, content);
        var bytesBefore = File.ReadAllBytes(file);

        var entry = Assert.Single(store.List());
        Assert.Equal(WorkflowFileStatus.Quarantined, entry.Status);
        Assert.Equal(expectedReason, entry.QuarantineReason);

        // 隔离文件拒绝加载为可执行文档（绝不回空对象）
        Assert.Throws<WorkflowQuarantinedException>(() => store.Load("wf-bad0001"));

        // 原件字节不动（不自动修复、不自动重写）
        Assert.Equal(bytesBefore, File.ReadAllBytes(file));
    }

    [Fact]
    public void UnsupportedKinds_LoadableForPreview_ButFlagged()
    {
        var store = new WorkflowStore(_dir);
        var json = """
            {
              "schema": "mistletoe.workflow",
              "schemaVersion": 1,
              "name": "未来流程",
              "nodes": [
                { "nodeId": "n-1", "kind": "resource.hologram", "ref": { "config": "X" },
                  "strategies": [ { "kind": "prerequisite.moonCard" } ] }
              ],
              "triggers": [ { "kind": "trigger.sunrise" } ]
            }
            """;
        var src = WriteTemp("future.flow.json", json);
        store.Import(src);

        var entry = Assert.Single(store.List());
        Assert.Equal(WorkflowFileStatus.Ready, entry.Status); // 可加载可预览
        Assert.Equal(["node:resource.hologram", "strategy:prerequisite.moonCard", "trigger:trigger.sunrise"],
            entry.UnsupportedKinds); // 但检出未支持类型（执行层据此阻止，D3 第二级）
    }

    [Fact]
    public void Save_RevisionGuard_RejectsClobberAndStaleOverwrite()
    {
        var store = new WorkflowStore(_dir);
        var doc = new WorkflowDocument { Name = "守卫" };

        // 新建不需要期望修订
        var rev1 = store.Save(doc, null);

        // 覆盖保存必须携带期望修订
        Assert.Throws<WorkflowRevisionConflictException>(() => store.Save(doc, null));

        // 过期修订拒绝（模拟并发/外部修改）
        var doc2 = store.Load(doc.WorkflowId!);
        doc2.Name = "并发改名";
        Assert.Throws<WorkflowRevisionConflictException>(() => store.Save(doc2, "deadbeef"));

        // 正确修订放行，修改变更
        var rev2 = store.Save(doc2, rev1);
        Assert.NotEqual(rev1, rev2);
        Assert.Equal("并发改名", store.Load(doc.WorkflowId!).Name);
    }

    [Fact]
    public void Save_AtomicWrite_NoTempResidue_AndBackupKeepsPriorVersion()
    {
        var store = new WorkflowStore(_dir);
        var doc = new WorkflowDocument { Name = "原子" };
        var rev1 = store.Save(doc, null);

        var loaded = store.Load(doc.WorkflowId!);
        loaded.Name = "原子v2";
        store.Save(loaded, rev1);

        // 无临时残件
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
        Assert.Empty(Directory.EnumerateFiles(_dir, ".*.tmp"));

        // 备份保留上一版内容
        var backupDir = Path.Combine(_dir, "_backup");
        var backup = Assert.Single(Directory.EnumerateFiles(backupDir, doc.WorkflowId + ".*.flow.json"));
        using var parsed = JsonDocument.Parse(File.ReadAllText(backup));
        Assert.Equal("原子", parsed.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void QuarantinedFile_ExplicitOverwrite_RequiresExactCurrentHash()
    {
        var store = new WorkflowStore(_dir);
        var file = Path.Combine(_dir, "wf-bad0002.flow.json");
        File.WriteAllText(file, "{ 坏文件");
        var entry = Assert.Single(store.List());
        Assert.Equal(WorkflowFileStatus.Quarantined, entry.Status);

        // 隔离文件也不能被裸覆盖：必须携带其当前字节哈希（明确的人为决定）
        var replacement = new WorkflowDocument { Name = "替代", WorkflowId = "wf-bad0002" };
        Assert.Throws<WorkflowRevisionConflictException>(() => store.Save(replacement, null));
        var rev = store.Save(replacement, entry.Revision);
        Assert.False(string.IsNullOrWhiteSpace(rev));

        // 原件进备份，可恢复
        var backup = Assert.Single(Directory.EnumerateFiles(Path.Combine(_dir, "_backup"), "wf-bad0002.*.flow.json"));
        Assert.Equal("{ 坏文件", File.ReadAllText(backup));
    }

    private string WriteTemp(string name, string content)
    {
        // 导入源放在 inbox 子目录（List 只扫顶层，源文件不应入目录）
        var inbox = Path.Combine(_dir, "inbox");
        Directory.CreateDirectory(inbox);
        var file = Path.Combine(inbox, name);
        File.WriteAllText(file, content);
        return file;
    }
}
