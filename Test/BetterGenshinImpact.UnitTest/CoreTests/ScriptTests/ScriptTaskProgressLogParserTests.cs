using System;
using System.Collections.Generic;
using System.Globalization;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Helpers;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

/// <summary>
/// 第三方 JS 脚本进度日志解析测试。
///
/// 用例文本取自脚本真实输出（采集cd管理、提瓦特自动钓鱼、AAA 狗粮批发、锄地一条龙、
/// 全自动枫丹地脉花、全自动周一、自动幽境危战、AAA 狗粮联机团购），锁定两条硬边界：
/// 1. 真实进度行必须解析成预期文本，且计数位于文本前部（截断不会丢 N/M）；
/// 2. 目录不匹配、数值非法、结构不符、含换行的伪造串必须返回 null（宁可无信息，也不能显示错误进度）。
///
/// 这组测试也同时充当"脚本升级改文案后的失配预警"：任一用例转红即说明脚本文案已变，
/// 需要人工核对后更新正则，而不是放宽匹配。
/// </summary>
public class ScriptTaskProgressLogParserTests
{
    [Theory]
    // 采集cd管理：路径组组内第 N/M 个（本次需求主目标）
    [InlineData("采集cd管理", "当前进度：执行路线 07-小灯草-明冠峡-18个，路径组3 特产 第 28/119 个", "第 28/119 个 · 路径组3 特产")]
    // 路径组标签含空格时仍应匹配（标签来自用户选择的目录名）
    [InlineData("采集cd管理", "当前进度：执行路线 07-小灯草，路径组3 蒙德 特产 第 28/119 个", "第 28/119 个 · 路径组3 蒙德 特产")]
    [InlineData("采集cd管理", "当前进度：执行路线 萃凝晶，剩余优先材料：萃凝晶*40, 甜甜花*10", "剩余优先材料：萃凝晶*40, 甜甜花*10")]
    [InlineData("采集cd管理", "当前进度：执行路线 萃凝晶，剩余一次性优先材料：萃凝晶*40", "剩余一次性优先材料：萃凝晶*40")]
    [InlineData("采集cd管理", "本任务cd信息已更新，下一次可用时间为 2026/9/19 4:00:00", "本任务cd信息已更新，下一次可用时间为 2026/9/19 4:00:00")]
    [InlineData("采集cd管理", "schedule任务CD信息已更新，下一次可用时间为 2026/9/19 4:00:00", "schedule任务CD信息已更新，下一次可用时间为 2026/9/19 4:00:00")]
    // 提瓦特自动钓鱼
    [InlineData("AutoFishingTeyvat", "当前垂钓点: 蒙德-清泉镇北(进度: 7/119)", "第 7/119 个 · 垂钓点 蒙德-清泉镇北")]
    [InlineData("AutoFishingTeyvat", "该垂钓点(白天)处于冷却状态，剩余时间: 2天 1小时 3分钟 4秒", "垂钓点(白天)冷却剩余 2天 1小时 3分钟 4秒")]
    [InlineData("AutoFishingTeyvat", "该垂钓点(夜晚)处于冷却状态，剩余时间: 1小时", "垂钓点(夜晚)冷却剩余 1小时")]
    // AAA 狗粮批发：正常由脚本显式上报覆盖，这里验证兜底解析
    [InlineData("AAA-Artifacts-Bulk-Supply", "当前进度：A001.json为assets/ArtifactsPath/普通98点1号线/执行第3/98个", "路线 第 3/98 个")]
    // 锄地一条龙：正常由脚本显式上报覆盖，这里只保留"已完成"行兜底
    [InlineData("AutoHoeingOneDragon", "当前进度：第 1 组第 3/268 个  A003.json已完成，该组预计剩余: 1 时 20 分 3 秒", "第 1 组 第 3/268 个已完成 · 该组预计剩余 1 时 20 分 3 秒")]
    // 全自动枫丹地脉花
    [InlineData("AutoFontaineLeyLine", "开始执行第 2 线路的第 3/5 朵地脉花...", "第 3/5 朵地脉花 · 第 2 线路")]
    [InlineData("AutoFontaineLeyLine", "树脂状态：浓缩1 原粹20 脆弱3 须臾0", "树脂状态：浓缩1 原粹20 脆弱3 须臾0")]
    // 全自动周一
    [InlineData("AutoMonday", "正在进行第2次秘境挑战", "正在进行第2次秘境挑战")]
    [InlineData("AutoMonday", "战斗成功！当前完成 2 次", "战斗成功！当前完成 2 次")]
    // 自动幽境危战（脚本用 {0} 占位符打印，宿主渲染后为纯文本）
    [InlineData("AutoStygianOnslaught", "进入战斗环境，开始第 2 次战斗", "进入战斗环境，开始第 2 次战斗")]
    [InlineData("AutoStygianOnslaught", "幽境危战：第 2 次领奖...", "幽境危战：第 2 次领奖...")]
    // AAA 狗粮联机团购
    [InlineData("ArtifactsGroupPurchasing", "你的序号是3号，将在第2个执行", "序号 3 · 第 2 个执行")]
    public void TryParse_真实脚本进度行_解析为预期文本(string folder, string message, string expected)
    {
        Assert.Equal(expected, ScriptTaskProgressLogParser.TryParse(folder, message));
    }

    [Theory]
    // 目录与文案不匹配：同一行给错脚本目录一律不认
    [InlineData("AutoMonday", "当前进度：执行路线 07-小灯草，路径组3 特产 第 28/119 个")]
    [InlineData("采集cd管理", "交互或拾取：\"甜甜花\"")]
    // 数值非法：0、分母为 0、分子超过分母
    [InlineData("采集cd管理", "当前进度：执行路线 07-小灯草，路径组3 特产 第 0/119 个")]
    [InlineData("采集cd管理", "当前进度：执行路线 07-小灯草，路径组3 特产 第 28/0 个")]
    [InlineData("采集cd管理", "当前进度：执行路线 07-小灯草，路径组3 特产 第 120/119 个")]
    [InlineData("AutoFishingTeyvat", "当前垂钓点: 蒙德(进度: 0/119)")]
    // 脚本真实文案是「白天/夜晚」，单字写法不是真实输出，不得误认
    [InlineData("AutoFishingTeyvat", "该垂钓点(日)处于冷却状态，剩余时间: 1小时")]
    // 团购：脚本自身限定序号 1-4、执行位次 1-4
    [InlineData("ArtifactsGroupPurchasing", "你的序号是3号，将在第0个执行")]
    [InlineData("ArtifactsGroupPurchasing", "你的序号是3号，将在第5个执行")]
    [InlineData("ArtifactsGroupPurchasing", "你的序号是5号，将在第1个执行")]
    // 锄地一条龙的"开始处理"行在路线 CD 检查之前打印，被跳过的路线没有清除日志，故不观察
    [InlineData("AutoHoeingOneDragon", "开始处理第 1 组第 3/268 个A003蒙德龙脊雪山眠龙谷北.json")]
    // 关键短语只出现在中间：必须整行锚定，不认
    [InlineData("AAA-Artifacts-Bulk-Supply", "日志：当前进度：A001.json第3/98个")]
    // 未纳入观察的脚本目录
    [InlineData("OCR切换武器", "当前进度：执行路线 x，路径组1 特产 第 1/2 个")]
    // 换行拼接的伪造串
    [InlineData("采集cd管理", "当前进度：执行路线 x，路径组3 特产 第 28/119 个\n第二行")]
    // 空与纯空白
    [InlineData("采集cd管理", "")]
    [InlineData("采集cd管理", "   ")]
    [InlineData("", "当前进度：执行路线 x，路径组3 特产 第 28/119 个")]
    public void TryParse_非法或异目录文本_返回null(string folder, string message)
    {
        Assert.Null(ScriptTaskProgressLogParser.TryParse(folder, message));
    }

    [Fact]
    public void TryParse_超长标签时_计数仍保留在文本前部()
    {
        var label = new string('长', 200);
        var message = $"当前进度：执行路线 07-小灯草，路径组3 {label} 第 28/119 个";

        var result = ScriptTaskProgressLogParser.TryParse("采集cd管理", message);

        Assert.NotNull(result);
        Assert.StartsWith("第 28/119 个 · 路径组3 ", result!);
        Assert.True(result!.Length <= 96, $"观察文本应被截断到 96 以内，实际 {result.Length}");
    }

    [Fact]
    public void TryParse_超长文本_直接放弃()
    {
        var message = "当前进度：执行路线 " + new string('x', 4096) + "，路径组3 特产 第 28/119 个";
        Assert.Null(ScriptTaskProgressLogParser.TryParse("采集cd管理", message));
    }

    [Fact]
    public void MayContainProgress_只放行可能承载进度的日志前缀()
    {
        Assert.True(ScriptTaskProgressLogParser.MayContainProgress("当前进度：执行路线 {0}，路径组{1} {2} 第 {3}/{4} 个"));
        Assert.True(ScriptTaskProgressLogParser.MayContainProgress("进入战斗环境，开始第 {0} 次战斗"));
        Assert.True(ScriptTaskProgressLogParser.MayContainProgress("该垂钓点({0})处于冷却状态，剩余时间: {1}"));
        Assert.False(ScriptTaskProgressLogParser.MayContainProgress("交互或拾取：\"{0}\""));
        Assert.False(ScriptTaskProgressLogParser.MayContainProgress("开始处理第 {0} 组第 {1}/{2} 个{3}"));
        Assert.False(ScriptTaskProgressLogParser.MayContainProgress(null));
        Assert.False(ScriptTaskProgressLogParser.MayContainProgress(""));
    }

    [Fact]
    public void IsPlaceholderOnlyTemplate_识别纯占位符模板()
    {
        Assert.True(ScriptTaskProgressLogParser.IsPlaceholderOnlyTemplate("{Message}"));
        Assert.True(ScriptTaskProgressLogParser.IsPlaceholderOnlyTemplate("{0}"));
        Assert.True(ScriptTaskProgressLogParser.IsPlaceholderOnlyTemplate(null));
        Assert.False(ScriptTaskProgressLogParser.IsPlaceholderOnlyTemplate("当前进度：执行路线 {0}"));
        Assert.False(ScriptTaskProgressLogParser.IsPlaceholderOnlyTemplate("进入战斗环境，开始第 {0} 次战斗"));
    }
}

/// <summary>
/// 脚本进度观察日志 Sink 的行为测试：只接受脚本宿主日志类别（SourceContext），
/// 且只有疑似进度行才会进入解析。与 <see cref="ScriptRouteProgressTests"/> 共享进程级静态状态，
/// 故纳入同一串行集合。
/// </summary>
[Collection("ScriptRouteProgressState")]
public class ScriptTaskProgressLogSinkTests
{
    private const string ScriptSourceContext = "BetterGenshinImpact.Core.Script.Dependence.Log";

    [Fact]
    public void Emit_脚本进度的Information_被观察()
    {
        try
        {
            ScriptRouteProgress.BeginProject("采集cd管理", null);
            var sink = new ScriptTaskProgressLogSink();

            sink.Emit(CreateLogEvent(
                LogEventLevel.Information,
                ScriptSourceContext,
                "当前进度：执行路线 07-小灯草-明冠峡-18个，路径组3 特产 第 28/119 个"));

            var text = ScriptRouteProgress.ProgressText;
            Assert.NotNull(text);
            Assert.Contains("日志进度 第 28/119 个 · 路径组3 特产", text!);
        }
        finally
        {
            ScriptRouteProgress.Clear();
        }
    }

    [Fact]
    public void Emit_纯占位符模板的进度日志_渲染后仍能被观察()
    {
        try
        {
            ScriptRouteProgress.BeginProject("采集cd管理", null);
            var sink = new ScriptTaskProgressLogSink();
            var message = "当前进度：执行路线 07-小灯草-明冠峡-18个，路径组3 特产 第 28/119 个";

            // 模拟"真实消息被包装在通用占位符模板里"的情况：模板本身没有中文字面量。
            var logEvent = new LogEvent(
                DateTimeOffset.Now,
                LogEventLevel.Information,
                exception: null,
                new MessageTemplateParser().Parse("{Message}"),
                [
                    new LogEventProperty("SourceContext", new ScalarValue(ScriptSourceContext)),
                    new LogEventProperty("Message", new ScalarValue(message))
                ]);

            // 前提校验：模板确实是"纯占位符型"，Sink 需要放行渲染路径
            Assert.True(ScriptTaskProgressLogParser.IsPlaceholderOnlyTemplate(logEvent.MessageTemplate.Text));

            sink.Emit(logEvent);

            var text = ScriptRouteProgress.ProgressText;
            Assert.NotNull(text);
            Assert.Contains("日志进度 第 28/119 个 · 路径组3 特产", text!);
        }
        finally
        {
            ScriptRouteProgress.Clear();
        }
    }

    [Fact]
    public void Emit_Debug级别或异类别日志_不被观察()
    {
        try
        {
            ScriptRouteProgress.BeginProject("采集cd管理", null);
            var sink = new ScriptTaskProgressLogSink();

            // Debug 级别：当前只观察 Information 及以上
            sink.Emit(CreateLogEvent(
                LogEventLevel.Debug,
                ScriptSourceContext,
                "当前进度：执行路线 07-小灯草，路径组3 特产 第 28/119 个"));
            // 非脚本宿主类别
            sink.Emit(CreateLogEvent(
                LogEventLevel.Information,
                "BetterGenshinImpact.GameTask.AutoHoeing.AutoHoeingTask",
                "当前进度：执行路线 07-小灯草，路径组3 特产 第 28/119 个"));
            // 缺少 SourceContext 属性
            sink.Emit(CreateLogEvent(LogEventLevel.Information, null, "当前进度：执行路线 07-小灯草，路径组3 特产 第 28/119 个"));

            // 采集cd管理 自身即有宿主适配器文本，这里只要求"没有日志观察进度"被写进去。
            Assert.DoesNotContain("日志进度", ScriptRouteProgress.ProgressText ?? string.Empty);
        }
        finally
        {
            ScriptRouteProgress.Clear();
        }
    }

    [Fact]
    public void Emit_非进度前缀的高频日志_不被观察()
    {
        try
        {
            ScriptRouteProgress.BeginProject("采集cd管理", null);
            var sink = new ScriptTaskProgressLogSink();

            sink.Emit(CreateLogEvent(LogEventLevel.Information, ScriptSourceContext, "交互或拾取：\"甜甜花\""));
            sink.Emit(CreateLogEvent(LogEventLevel.Information, ScriptSourceContext, "该路线未刷新，跳过。"));

            Assert.DoesNotContain("日志进度", ScriptRouteProgress.ProgressText ?? string.Empty);
        }
        finally
        {
            ScriptRouteProgress.Clear();
        }
    }

    private static LogEvent CreateLogEvent(LogEventLevel level, string? sourceContext, string message)
    {
        var properties = new List<LogEventProperty>();
        if (sourceContext != null)
        {
            properties.Add(new LogEventProperty("SourceContext", new ScalarValue(sourceContext)));
        }

        return new LogEvent(
            DateTimeOffset.Now,
            level,
            exception: null,
            new MessageTemplate(message, [new TextToken(message)]),
            properties);
    }
}

/// <summary>
/// 脚本进度状态机测试（<see cref="ScriptRouteProgress"/>）。
/// 静态状态 + 日志观察是进程级共享的，因此整组测试串行执行，并在用例结束时清理。
/// </summary>
[CollectionDefinition("ScriptRouteProgressState", DisableParallelization = true)]
public class ScriptRouteProgressStateCollection
{
}

[Collection("ScriptRouteProgressState")]
public class ScriptRouteProgressTests
{
    [Fact]
    public void 日志观察进度_追加在宿主适配器文本之后_并在项目结束时清空()
    {
        try
        {
            ScriptRouteProgress.BeginProject("采集cd管理", null);
            ScriptRouteProgress.ObserveLogMessage("当前进度：执行路线 07-小灯草-明冠峡-18个，路径组3 特产 第 28/119 个");

            var text = ScriptRouteProgress.ProgressText;
            Assert.NotNull(text);
            Assert.Contains("采集CD", text!);
            Assert.Contains("日志进度 第 28/119 个 · 路径组3 特产", text!);
        }
        finally
        {
            ScriptRouteProgress.Clear();
        }

        Assert.Null(ScriptRouteProgress.ProgressText);
    }

    [Fact]
    public void 显式上报存在时_日志观察完全不参与展示()
    {
        try
        {
            ScriptRouteProgress.BeginProject("AAA-Artifacts-Bulk-Supply", null);
            var adapterText = ScriptRouteProgress.ProgressText;
            Assert.NotNull(adapterText);

            ScriptRouteProgress.SetProgressText("第 3/98 条: A001.json");
            ScriptRouteProgress.ObserveLogMessage("当前进度：A001.json为assets/ArtifactsPath/普通98点1号线/执行第1/98个");

            // 与引入观察之前完全一致：适配器文本 + " · 脚本状态 显式文本"，且不含观察值。
            Assert.Equal($"{adapterText} · 脚本状态 第 3/98 条: A001.json", ScriptRouteProgress.ProgressText);
        }
        finally
        {
            ScriptRouteProgress.Clear();
        }
    }

    [Theory]
    [InlineData("采集cd管理")]
    [InlineData("AutoFishingTeyvat")]
    [InlineData("ArtifactsGroupPurchasing")]
    public void 日志不匹配时_适配器文本逐字不变(string folder)
    {
        try
        {
            ScriptRouteProgress.BeginProject(folder, null);
            var before = ScriptRouteProgress.ProgressText;
            Assert.NotNull(before);

            ScriptRouteProgress.ObserveLogMessage("交互或拾取：\"甜甜花\"");
            ScriptRouteProgress.ObserveLogMessage("完全无关的一行日志");

            Assert.Equal(before, ScriptRouteProgress.ProgressText);
        }
        finally
        {
            ScriptRouteProgress.Clear();
        }
    }

    [Fact]
    public void 显式上报清空后_旧日志观察值不会复活()
    {
        try
        {
            ScriptRouteProgress.BeginProject("采集cd管理", null);
            ScriptRouteProgress.ObserveLogMessage("当前进度：执行路线 07-小灯草，路径组3 特产 第 28/119 个");
            ScriptRouteProgress.SetProgressText("显式进度");
            ScriptRouteProgress.SetProgressText("");

            var text = ScriptRouteProgress.ProgressText;
            Assert.NotNull(text);
            Assert.DoesNotContain("日志进度", text!);
        }
        finally
        {
            ScriptRouteProgress.Clear();
        }
    }

    [Fact]
    public void 目录与文案不匹配时_不产生观察进度()
    {
        try
        {
            ScriptRouteProgress.BeginProject("AutoMonday", null);
            ScriptRouteProgress.ObserveLogMessage("当前进度：执行路线 07-小灯草，路径组3 特产 第 28/119 个");

            Assert.Null(ScriptRouteProgress.ProgressText);
        }
        finally
        {
            ScriptRouteProgress.Clear();
        }
    }

    [Fact]
    public void 锄地一条龙的显式上报文本_逐字保持不变()
    {
        const string explicitText = "当前进度：第 1 组第 3/268 条: A003.json，该组预计剩余 1 时 20 分";
        try
        {
            ScriptRouteProgress.BeginProject("AutoHoeingOneDragon", null);
            ScriptRouteProgress.SetProgressText(explicitText);
            // 兜底观察（完成行）不得影响显式文本
            ScriptRouteProgress.ObserveLogMessage("当前进度：第 1 组第 3/268 个  A003.json已完成，该组预计剩余: 1 时 20 分 3 秒");

            Assert.Equal(explicitText, ScriptRouteProgress.ProgressText);
        }
        finally
        {
            ScriptRouteProgress.Clear();
        }
    }

    [Fact]
    public void 项目切换后_上一项目的观察进度不可见()
    {
        try
        {
            ScriptRouteProgress.BeginProject("采集cd管理", null);
            ScriptRouteProgress.ObserveLogMessage("当前进度：执行路线 07-小灯草，路径组3 特产 第 28/119 个");
            Assert.Contains("第 28/119 个", ScriptRouteProgress.ProgressText!);

            ScriptRouteProgress.BeginProject("AutoMonday", null);
            Assert.Null(ScriptRouteProgress.ProgressText);
        }
        finally
        {
            ScriptRouteProgress.Clear();
        }
    }

    [Fact]
    public void 同目录重跑_新一轮不继承上一轮的观察进度()
    {
        try
        {
            ScriptRouteProgress.BeginProject("采集cd管理", null);
            ScriptRouteProgress.ObserveLogMessage("当前进度：执行路线 07-小灯草，路径组3 特产 第 28/119 个");
            Assert.Contains("第 28/119 个", ScriptRouteProgress.ProgressText!);

            // 同名目录的新项目：旧观察值必须消失（观察代次已自增）
            ScriptRouteProgress.BeginProject("采集cd管理", null);
            Assert.DoesNotContain("日志进度", ScriptRouteProgress.ProgressText ?? string.Empty);
        }
        finally
        {
            ScriptRouteProgress.Clear();
        }
    }
}
