using System.Reflection;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>接续失败不应随界面/远控轮询重复扫描和刷日志；退避后同一输入仍能恢复，不缓存成成功。</summary>
public sealed class LegacyCompatibilityRetryTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "legacy-retry-" + Guid.NewGuid().ToString("N"));
    public LegacyCompatibilityRetryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task PersistentFailure_IsRetainedAndThrottled_ThenSameInputCanRecover()
    {
        var user = Path.Combine(_root, "isolated-user");
        Directory.CreateDirectory(Path.Combine(user, "OneDragon"));
        var source = Path.Combine(user, "OneDragon", "old.json");
        File.WriteAllText(source, """{"Name":"普通旧配置","TaskEnabledList":{"领取邮件":true}}""");
        var original = File.ReadAllBytes(source);
        var indexRoot = Path.Combine(_root, "legacy-compatibility");
        File.WriteAllText(indexRoot, "isolated blocker"); // 让接续无法创建索引目录，故障不触碰任何真实配置。
        var logs = new List<string>();
        object? gate = null;
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"),
            Path.Combine(_root, "catalog.json"), () => null, message =>
            {
                Assert.NotNull(gate);
                Assert.False(Monitor.IsEntered(gate!));
                logs.Add(message);
            }, null, () => (true, null));
        gate = typeof(TaskCenterHost).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host);
        try
        {
            var failed = host.EnsureLegacyCompatibility(user);
            Assert.Contains(failed.Notices, n => n.Contains("旧计划暂时无法接续"));
            Assert.Same(failed, host.LegacyCompatibilityFailure);
            // 源文件被占用时仍返回同一失败对象，证明冷却期间没有重新读取/计算其哈希。
            using (var lockedSource = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
                for (var i = 0; i < 20; i++) Assert.Same(failed, host.EnsureLegacyCompatibility(user));
            Assert.Single(logs);
            Assert.Empty(host.ListFlows());

            AllowNextRetry(host);
            Assert.Equal(failed.Notices, host.EnsureLegacyCompatibility(user).Notices);
            Assert.Single(logs); // 跨重试窗口的同一故障也不重复刷屏。
            File.Delete(indexRoot);
            AllowNextRetry(host);
            var resumed = host.EnsureLegacyCompatibility(user);
            Assert.Equal(1, resumed.Added);
            Assert.Null(host.LegacyCompatibilityFailure);
            Assert.Single(host.ListFlows());
            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.Single(logs.Where(l => l.Contains("旧计划接续已恢复")));
            var count = logs.Count;
            Assert.Equal(0, host.EnsureLegacyCompatibility(user).Added);
            Assert.Equal(count, logs.Count);
        }
        finally { await host.ShutdownAsync(); }
    }

    private static void AllowNextRetry(TaskCenterHost host) =>
        typeof(TaskCenterHost).GetField("_legacyCompatibilityRetryAfter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, 0L);
}
