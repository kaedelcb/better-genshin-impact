using System;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Logging;
using System.Threading;

namespace BetterGenshinImpact.Core.Script.Dependence;

public class AutoPathingScript
{
    private object? _config = null;
    private string _rootPath;
    private readonly LimitedFile _autoPathingFile;

    public AutoPathingScript(string rootPath, object? config)
    {
        _config = config;
        _rootPath = rootPath;
        _autoPathingFile = new LimitedFile(Global.Absolute(@"User\AutoPathing"));
    }

    public async Task Run(string json, CancellationToken ct = default)
    {
        try
        {
            if (ct == default)
            {
                ct = CancellationContext.Instance.Cts.Token;
            }
            else
            {
                TaskControl.Logger.LogWarning("执行地图追踪传入Cts");
                ct = CancellationContext.Instance.Register(ct);
            }

            var task = PathingTask.BuildFromJson(json);
            var pathExecutor = new PathExecutor(ct);
            if (_config != null && _config is PathingPartyConfig patyConfig)
            {
                pathExecutor.PartyConfig = patyConfig;
            }

            await pathExecutor.Pathing(task);
        }
        catch (OperationCanceledException)
        {
            TaskControl.Logger.LogInformation("路径追踪任务被取消");
        }
        catch (ObjectDisposedException e)
        {
            TaskControl.Logger.LogError("访问已释放的对象: {Msg}", e.Message);
        }
        catch (Exception e)
        {
            TaskControl.Logger.LogDebug(e, "执行地图追踪时候发生错误");
            TaskControl.Logger.LogError("执行地图追踪时候发生错误: {Msg}", e.Message);
        }
    }

    public async Task RunFile(string path,CancellationToken ct = default)
    {
        try
        {
            var json = await new LimitedFile(_rootPath).ReadText(path);
            
            PathingConditionConfig.GetCountryName(path);

            // 记录"当前执行线路"：JS 脚本任务名恒为脚本名，只有这里能拿到脚本内部正跑的线路文件名
            // （联机助手成员状态标签 / 嘟嘟可锄地数据成员墙的「路线」显示用）。
            ScriptRouteProgress.SetCurrentRoute(System.IO.Path.GetFileName(path));

            await Run(json,ct);
        }
        catch (Exception e)
        {
            TaskControl.Logger.LogDebug(e,"读取文件时发生错误");
            TaskControl.Logger.LogError("读取文件时发生错误: {Msg}",e.Message);
        }
    }

    /// <summary>
    /// 从已订阅的内容中获取文件
    /// </summary>
    /// <param name="path">在 `\User\AutoPathing` 目录下获取文件</param>
    public async Task RunFileFromUser(string path,CancellationToken ct = default)
    {
        var json = await AutoPathingFile.ReadText(path);
        // 同 RunFile：订阅脚本（User\AutoPathing 下）逐条跑路线时也要能显示线路名
        ScriptRouteProgress.SetCurrentRoute(System.IO.Path.GetFileName(path));
        await Run(json);
    }

    /// <summary>
    /// 判断 AutoPathing 目录下的路径是否存在
    /// </summary>
    /// <param name="subPath">相对于 User\AutoPathing 的路径</param>
    /// <returns>存在返回 true，否则返回 false</returns>
    public bool IsExists(string subPath) => AutoPathingFile.IsExists(subPath);

    /// <summary>
    /// 判断 AutoPathing 目录下的路径是否为文件
    /// </summary>
    /// <param name="subPath">相对于 User\AutoPathing 的路径</param>
    /// <returns>是文件返回 true，否则返回 false</returns>
    public bool IsFile(string subPath) => AutoPathingFile.IsFile(subPath);

    /// <summary>
    /// 判断 AutoPathing 目录下的路径是否为文件夹
    /// </summary>
    /// <param name="subPath">相对于 User\AutoPathing 的路径</param>
    /// <returns>是文件夹返回 true，否则返回 false</returns>
    public bool IsFolder(string subPath) => AutoPathingFile.IsFolder(subPath);

    /// <summary>
    /// 读取 AutoPathing 目录下指定文件夹的内容（非递归方式）
    /// 目录不存在时返回空数组，不会自动创建目录
    /// </summary>
    /// <param name="subPath">相对于 User\AutoPathing 的子目录路径，默认为相对根目录</param>
    /// <returns>文件夹内所有文件和文件夹的相对路径数组，出错时返回空数组</returns>
    public string[] ReadPathSync(string subPath = "./") => AutoPathingFile.ReadPathSync(subPath);

    /// <summary>
    /// 读取 AutoPathing 目录下指定文件的文本内容
    /// </summary>
    /// <param name="subPath">相对于 User\AutoPathing 的文件路径</param>
    /// <returns>文件文本内容，读取失败时返回空字符串</returns>
    public string ReadTextSync(string subPath) => AutoPathingFile.ReadTextSync(subPath);

    /// <summary>
    /// LimitedFile 实例，用于操作 AutoPathing 目录
    /// </summary>
    private LimitedFile AutoPathingFile => _autoPathingFile;
}