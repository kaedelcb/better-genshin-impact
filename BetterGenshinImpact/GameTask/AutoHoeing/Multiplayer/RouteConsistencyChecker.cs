#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

public class RouteConsistencyChecker
{
    private readonly ILogger<RouteConsistencyChecker> _logger = App.GetLogger<RouteConsistencyChecker>();

    /// <summary>计算整个 pathing 目录下所有路线文件的 MD5</summary>
    public List<RouteHash> ComputeLocalHashes(string pathingDir)
    {
        var result = new List<RouteHash>();

        if (!Directory.Exists(pathingDir))
            return result;

        foreach (var filePath in Directory.EnumerateFiles(pathingDir, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var bytes = File.ReadAllBytes(filePath);
                var hashBytes = MD5.HashData(bytes);
                var md5 = Convert.ToHexString(hashBytes).ToLowerInvariant();
                result.Add(new RouteHash
                {
                    FileName = Path.GetFileName(filePath),
                    Md5 = md5
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "计算文件 MD5 失败，跳过: {FilePath}", filePath);
            }
        }

        return result;
    }

    /// <summary>计算指定路线文件列表的 MD5（只验证本次要跑的路线）</summary>
    public List<RouteHash> ComputeHashesForRoutes(IEnumerable<string> fullPaths)
    {
        var result = new List<RouteHash>();
        foreach (var filePath in fullPaths)
        {
            try
            {
                var bytes = File.ReadAllBytes(filePath);
                var hashBytes = MD5.HashData(bytes);
                var md5 = Convert.ToHexString(hashBytes).ToLowerInvariant();
                result.Add(new RouteHash
                {
                    FileName = Path.GetFileName(filePath),
                    Md5 = md5
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "计算文件 MD5 失败，跳过: {FilePath}", filePath);
            }
        }
        return result;
    }

    public async Task<bool?> VerifyAsync(CoordinatorClient client, string pathingDir, CancellationToken ct)
    {
        var hashes = ComputeLocalHashes(pathingDir);
        _logger.LogInformation("[路线验证] 上报全部路线，共 {Count} 条", hashes.Count);
        return await VerifyHashesAsync(client, hashes, ct);
    }

    /// <summary>只验证本次要跑的路线（推荐使用）
    /// 返回值：true=验证通过，false=路线不一致，null=超时未收到结果</summary>
    public async Task<bool?> VerifyRoutesAsync(CoordinatorClient client, IEnumerable<string> fullPaths, CancellationToken ct)
    {
        var hashes = ComputeHashesForRoutes(fullPaths);
        _logger.LogInformation("[路线验证] 上报本次路线，共 {Count} 条", hashes.Count);
        return await VerifyHashesAsync(client, hashes, ct);
    }

    private async Task<bool?> VerifyHashesAsync(CoordinatorClient client, List<RouteHash> hashes, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Action? onPassed = null;
        Action<List<string>>? onDiff = null;

        onPassed = () => tcs.TrySetResult(true);
        onDiff = diff =>
        {
            _logger.LogWarning("[路线验证] 差异文件: {Files}", string.Join(", ", diff));
            tcs.TrySetResult(false);
        };

        // 先注册监听，再上报，避免服务器在注册前就广播导致错过事件
        client.RouteVerificationPassed += onPassed;
        client.RouteDiffReceived += onDiff;

        try
        {
            await client.ReportRouteListAsync(hashes, ct);
            // 超时时间延长到90秒，覆盖房主准备时间
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var externalReg = ct.Register(() => tcs.TrySetResult(null));
            using var timeoutReg = timeoutCts.Token.Register(() =>
            {
                _logger.LogWarning("[路线验证] 等待超时（90秒），视为超时");
                tcs.TrySetResult(null);
            });

            return await tcs.Task;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[路线验证] 发生异常");
            return null;
        }
        finally
        {
            client.RouteVerificationPassed -= onPassed;
            client.RouteDiffReceived -= onDiff;
        }
    }
}
