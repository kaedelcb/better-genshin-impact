using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// ResourceCatalogService（R4.3）验收夹具：
/// list/describe 纯解析（真实响应形状）、单项任务绑定所属配置+taskId+修订、
/// capability 门控降级（旧 BGI 响亮留痕不降级重发）、通道未就绪降级、
/// 缓存降级展示（IsFromCache 标记 + 不可执行声明）、部分 describe 失败留痕不拖垮目录。
/// 涉盘用例走临时目录，finally 清理。
/// </summary>
public class ResourceCatalogServiceTests : IDisposable
{
    private readonly string _dir;

    public ResourceCatalogServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rescat-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string CacheFile => Path.Combine(_dir, "catalog-cache.json");

    /// <summary>可编程假传输面。</summary>
    private sealed class FakeTransport : IResourceCatalogTransport
    {
        public bool IsReady { get; set; } = true;
        public HashSet<string> Capabilities { get; } = new(StringComparer.Ordinal) { "config.revision" };
        public Dictionary<string, string> Responses { get; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = new();

        public bool HasCapability(string name) => Capabilities.Contains(name);

        public Task<string?> SendAsync(string operation, object payload, CancellationToken ct)
        {
            Calls.Add(operation + " " + payload);
            return Task.FromResult(Responses.TryGetValue(operation, out var data) ? data : null);
        }
    }

    private const string ListResponse = """
        {
          "configGroups": ["锄地组", "刷本组"],
          "oneClickConfigs": ["日常综合"],
          "hotkeys": []
        }
        """;

    private const string DescribeOneDragon = """
        {
          "configRevision": "rev-abc123",
          "tasks": [
            { "taskId": "t-0001", "name": "领取邮件", "enabled": true, "legacyIndex": null, "schema": "native", "singleExecutionSupported": false },
            { "taskId": "t-0002", "name": "自动秘境", "enabled": false, "legacyIndex": 3, "schema": "native", "singleExecutionSupported": true }
          ],
          "bgiEpoch": { "processId": 4321, "startTicksUtc": 639000000000000000 }
        }
        """;

    private const string DescribeGroup = """
        {
          "configRevision": "rev-group-9",
          "tasks": [],
          "bgiEpoch": { "processId": 4321, "startTicksUtc": 639000000000000000 }
        }
        """;

    [Fact]
    public void ParseList_ExtractsGroupsAndOneDragons()
    {
        var (groups, oneDragons) = ResourceCatalogParser.ParseList(ListResponse);
        Assert.Equal(["锄地组", "刷本组"], groups);
        Assert.Equal(["日常综合"], oneDragons);
    }

    [Fact]
    public void ParseDescribe_OneDragon_ConfigEntryPlusBoundSingleTasks()
    {
        var (config, tasks) = ResourceCatalogParser.ParseDescribe(
            DescribeOneDragon, TaskCenterResourceKind.OneDragonConfig, "日常综合");

        Assert.Equal("onedragon:日常综合", config.StableId);
        Assert.Equal("rev-abc123", config.ConfigRevision);
        Assert.Equal("4321:639000000000000000", config.BgiEpoch);

        Assert.Equal(2, tasks.Count);
        var mail = tasks[0];
        Assert.Equal("single:日常综合:t-0001", mail.StableId); // 绑定所属配置 + 稳定 taskId（D14）
        Assert.Equal("日常综合", mail.OwnerConfig);
        Assert.Equal("rev-abc123", mail.ConfigRevision); // 修订继承所属配置
        Assert.True(mail.Enabled);
        Assert.False(mail.SingleExecutionSupported); // task.single.native=false → 执行预检拒绝（D4）
        var domain = tasks[1];
        Assert.False(domain.Enabled);
        Assert.True(domain.SingleExecutionSupported);
    }

    [Fact]
    public async Task Refresh_RealtimePull_BuildsFullCatalog_AndPersistsCache()
    {
        var transport = new FakeTransport();
        transport.Responses["ext.config.list"] = ListResponse;
        transport.Responses["ext.config.describe"] = DescribeGroup; // 组默认
        var svc = new ResourceCatalogService(() => transport, CacheFile);

        // 一条龙 describe 按 payload 区分：假传输面简化为按调用顺序
        var snapshot = await svc.RefreshAsync();

        Assert.False(snapshot.IsDegraded);
        Assert.Equal("4321:639000000000000000", snapshot.BgiEpoch);
        Assert.Equal(3, snapshot.Entries.Count(e => e.Kind is TaskCenterResourceKind.OneDragonConfig or TaskCenterResourceKind.ConfigGroup));
        Assert.True(File.Exists(CacheFile)); // 缓存已持久化（降级展示用）
    }

    [Fact]
    public async Task Refresh_NotReady_DegradesWithTrace_NoCacheEmptyCatalog()
    {
        var svc = new ResourceCatalogService(() => null, CacheFile);
        var snapshot = await svc.RefreshAsync();
        Assert.True(snapshot.IsDegraded);
        Assert.Contains("未就绪", snapshot.DegradedReason);
        Assert.Empty(snapshot.Entries); // 无缓存 = 空目录，不硬撑
    }

    [Fact]
    public async Task Refresh_MissingCapability_LoudDegrade_NoLegacyFallback()
    {
        var transport = new FakeTransport();
        transport.Capabilities.Clear(); // 旧 BGI：无 config.revision
        var svc = new ResourceCatalogService(() => transport, CacheFile);

        var snapshot = await svc.RefreshAsync();

        Assert.True(snapshot.IsDegraded);
        Assert.Contains("config.revision", snapshot.DegradedReason);
        Assert.DoesNotContain(transport.Calls, c => c.StartsWith("ext.config.describe")); // 不降级重发（R2 矩阵）
    }

    [Fact]
    public async Task DegradedSnapshot_FromCache_MarkedNotExecutable()
    {
        // 先实时拉一次写缓存
        var transport = new FakeTransport();
        transport.Responses["ext.config.list"] = ListResponse;
        transport.Responses["ext.config.describe"] = DescribeGroup;
        var svc = new ResourceCatalogService(() => transport, CacheFile);
        await svc.RefreshAsync();

        // BGI 掉线 → 缓存降级
        var offline = new ResourceCatalogService(() => null, CacheFile);
        var snapshot = await offline.RefreshAsync();

        Assert.True(snapshot.IsDegraded);
        Assert.NotEmpty(snapshot.Entries);
        Assert.All(snapshot.Entries, e => Assert.True(e.IsFromCache)); // 缓存仅展示
        Assert.Contains("不可作为执行依据", snapshot.DegradedReason); // 留痕：缓存不授权执行（D5）
    }

    [Fact]
    public async Task Refresh_PartialDescribeFailure_TracedButCatalogSurvives()
    {
        // 假传输面：list 正常、describe 全部失败
        var transport = new FakeTransport();
        transport.Responses["ext.config.list"] = ListResponse;
        var svc = new ResourceCatalogService(() => transport, CacheFile);

        var snapshot = await svc.RefreshAsync();

        Assert.False(snapshot.IsDegraded); // 实时拉取本身成功
        Assert.Empty(snapshot.Entries);
        Assert.Contains("部分配置 describe 失败", snapshot.DegradedReason); // 留痕
        Assert.Contains("日常综合", snapshot.DegradedReason);
    }
}
