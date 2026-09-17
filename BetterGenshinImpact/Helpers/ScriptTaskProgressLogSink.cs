using System;
using System.Globalization;
using BetterGenshinImpact.Core.Script;
using Serilog.Core;
using Serilog.Events;

namespace BetterGenshinImpact.Helpers;

/// <summary>
/// 只读观察第三方 JS 脚本自身的进度日志（SourceContext 为 Core.Script.Dependence.Log），
/// 把脚本已经打印出来的阶段计数（如采集cd管理的「路径组3 特产 第 28/119 个」）交给
/// <see cref="ScriptRouteProgress"/> 的观察槽，最终经 IPC task.status.scriptTaskProgress
/// 呈现在联机助手 / 桌宠任务面板。
///
/// 边界：只订阅进程内 Serilog 事件，不读取日志文件、不修改任何脚本源码。
/// 为控制高频拾取日志的开销，先按未渲染模板做前缀白名单过滤；模板若为纯占位符型（无中文字面量，
/// 例如 `{Message}`），则放行渲染并在渲染后重新校验前缀——两道都过才会进入解析。
/// 解析失败或异常一律静默降级（宁可无信息，也不显示错误进度），且绝不在 Sink 内再次写日志管线。
/// </summary>
public sealed class ScriptTaskProgressLogSink : ILogEventSink
{
    /// <summary>JS 脚本宿主日志类别：log.info/warn/error 最终都落在该 SourceContext 上。</summary>
    private const string ScriptLogSourceContext = "BetterGenshinImpact.Core.Script.Dependence.Log";

    public void Emit(LogEvent logEvent)
    {
        try
        {
            if (logEvent.Level < LogEventLevel.Information)
            {
                return;
            }

            if (!IsScriptLog(logEvent))
            {
                return;
            }

            var template = logEvent.MessageTemplate.Text;
            if (!ScriptTaskProgressLogParser.MayContainProgress(template)
                && !ScriptTaskProgressLogParser.IsPlaceholderOnlyTemplate(template))
            {
                return;
            }

            var message = logEvent.RenderMessage(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (!ScriptTaskProgressLogParser.MayContainProgress(message))
            {
                // 占位符型模板渲染时，Serilog 会给字符串属性加引号（除非 format 为 :l）。
                // 去引号后再校验一次前缀；两次都不过就放弃（宁可无信息，也不能误显示）。
                var unquoted = message.Trim('"', ' ');
                if (!ScriptTaskProgressLogParser.MayContainProgress(unquoted))
                {
                    return;
                }

                message = unquoted;
            }

            ScriptRouteProgress.ObserveLogMessage(message);
        }
        catch (Exception ex)
        {
            // 观察失败不得影响日志管线与游戏主流程；这里不使用 Log.*，避免递归写回自身。
            Serilog.Debugging.SelfLog.WriteLine("ScriptTaskProgressLogSink failed: {0}", ex);
        }
    }

    private static bool IsScriptLog(LogEvent logEvent)
    {
        return logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var value)
               && value is ScalarValue { Value: string context }
               && string.Equals(context, ScriptLogSourceContext, StringComparison.Ordinal);
    }
}