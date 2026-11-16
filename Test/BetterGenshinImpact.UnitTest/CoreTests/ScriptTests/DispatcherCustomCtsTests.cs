using System.Runtime.CompilerServices;
using System.Text.Json;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Factory;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using Microsoft.ClearScript.V8;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public class DispatcherCustomCtsTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(".json", true)]
    [InlineData(".json", false)]
    [InlineData(".txt", true)]
    public async Task JavaScriptCustomFightDoesNotDisableTheNextRoute(string extension, bool enabled)
    {
        var config = new PathingPartyConfig { Enabled = true, AutoFightEnabled = true };
        config.AutoFightConfig.FightFinishDetectEnabled = enabled;
        var fight = new FightProbe(config, "strategy" + extension);
        using var engine = new V8ScriptEngine(
            V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding | V8ScriptEngineFlags.EnableTaskPromiseConversion);
        engine.AddHostObject("fight", fight);

        var script = engine.Evaluate("""
            (async () => {
                await fight.runCustomFight();
                fight.runNextRoute();
            })()
            """);
        await Assert.IsAssignableFrom<Task>(script).WaitAsync(WaitLimit);

        Assert.False(fight.CustomFinishDetect);
        Assert.Equal(enabled, fight.RouteFinishDetect);
        Assert.Equal(enabled, config.AutoFightConfig.FightFinishDetectEnabled);
        Assert.Equal(extension == ".json" ? typeof(JsonCombatTaskFactory) : typeof(TxtCombatTaskFactory), fight.RouteFactory);
        Assert.False(Dispatcher.IsCustomCts);
    }

    [Fact]
    public async Task NestedEarlyReturnRestoresParentContext()
    {
        await Dispatcher.RunTaskInContextAsync(true, async () =>
        {
            var result = await Dispatcher.RunTaskInContextAsync<object?>(false, () =>
            {
                Assert.False(Dispatcher.IsCustomCts);
                return Task.FromResult<object?>(null);
            });
            Assert.Null(result);
            Assert.True(Dispatcher.IsCustomCts);
            return 0;
        });
        Assert.False(Dispatcher.IsCustomCts);
    }

    [Fact]
    public async Task MultipleFlowsAndDetachedChildKeepTheirOwnContext()
    {
        var regularEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? detached = null;
        var custom = Dispatcher.RunTaskInContextAsync(true, async () =>
        {
            detached = Task.Run(async () =>
            {
                await release.Task.WaitAsync(WaitLimit);
                return Dispatcher.IsCustomCts;
            });
            await regularEntered.Task.WaitAsync(WaitLimit);
            return Dispatcher.IsCustomCts;
        });

        var regular = Dispatcher.RunTaskInContextAsync(false, async () =>
        {
            Assert.False(Dispatcher.IsCustomCts);
            regularEntered.SetResult();
            Assert.True(await custom.WaitAsync(WaitLimit));
            return Dispatcher.IsCustomCts;
        });
        Assert.False(await regular.WaitAsync(WaitLimit));
        Assert.False(Dispatcher.IsCustomCts);
        // 已捕获上下文的子任务仍属于旧战斗；它迟到完成也不能改掉当前路线的状态。
        release.SetResult();
        Assert.True(await detached!.WaitAsync(WaitLimit));
        Assert.False(Dispatcher.IsCustomCts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationDoesNotLeakToCaller(bool preCancelled)
    {
        using var cts = new CancellationTokenSource();
        if (preCancelled)
        {
            cts.Cancel();
        }

        var running = Dispatcher.RunTaskInContextAsync(true, async () =>
        {
            Assert.True(Dispatcher.IsCustomCts);
            await Task.Delay(Timeout.Infinite, cts.Token);
            return 0;
        });
        Assert.Equal(preCancelled, running.IsCompleted);
        if (!preCancelled)
        {
            cts.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(WaitLimit));
        Assert.False(Dispatcher.IsCustomCts);
    }

    [Fact]
    public async Task InitializationAndParseExceptionsDoNotLeakToCaller()
    {
        // 真实入口的参数校验先于 WPF、配置服务和游戏初始化。
        var dispatcher = (Dispatcher)RuntimeHelpers.GetUninitializedObject(typeof(Dispatcher));
        await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.RunTask(null!, CancellationToken.None));
        Assert.False(Dispatcher.IsCustomCts);

        await Assert.ThrowsAnyAsync<JsonException>(() => Dispatcher.RunTaskInContextAsync(true, async () =>
        {
            await Task.Yield();
            Assert.True(Dispatcher.IsCustomCts);
            return JsonSerializer.Deserialize<JsonElement>("{");
        }));
        Assert.False(Dispatcher.IsCustomCts);
    }

    public sealed class FightProbe(PathingPartyConfig config, string strategyPath)
    {
        public bool? CustomFinishDetect { get; private set; }
        public bool? RouteFinishDetect { get; private set; }
        public Type? RouteFactory { get; private set; }

        public async Task RunCustomFight()
        {
            CustomFinishDetect = await Dispatcher.RunTaskInContextAsync(true, () => Task.Run(GetFinishDetection));
        }

        public void RunNextRoute()
        {
            RouteFactory = CombatTaskFactoryProvider.GetFactory(strategyPath).GetType();
            RouteFinishDetect = GetFinishDetection();
        }

        private bool GetFinishDetection()
        {
            // 只准备处理器要修改的参数，跳过会读取游戏语言及启动 WPF 宿主的构造函数。
            var param = (AutoFightParam)RuntimeHelpers.GetUninitializedObject(typeof(AutoFightParam));
            param.FightFinishDetectEnabled = config.AutoFightConfig.FightFinishDetectEnabled;
            AutoFightHandler.ApplyCustomCtsFightFinishPolicy(param, NullLogger.Instance);
            return param.FightFinishDetectEnabled;
        }
    }
}
