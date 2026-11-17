using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using BetterGenshinImpact.GameTask.Common;

namespace BetterGenshinImpact.GameTask.AutoHoeing;

/// <summary>保留已保存任务名称；正式托管任务直接等待同一个安装器，不启动第三方进程。</summary>
public sealed class AutoHoeingRouteUpdateTask : ISoloTask
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = Timeout.InfiniteTimeSpan };
    public string Name => "更新联机锄地线路";
    internal static AutoHoeingRouteUpdater CreateUpdater()
        => new(Http, AutoHoeingRoutePaths.ManagedRoot, Global.Version)
        {
            // 在 BGI 日志里直接能看到当前线路版本，不用打开文件夹。
            VersionLog = message => Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(TaskControl.Logger, "{Message}", message)
        };

    public async Task Start(CancellationToken ct)
    {
        var config = TaskContext.Instance().Config.AutoHoeingConfig;
        await CreateUpdater().EnsureAsync(AutoHoeingRouteUpdater.ResolveServerAddress(config.CoordinatorServerUrl), null, ct);
        ct.ThrowIfCancellationRequested();
    }
}
