using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.UseRedeemCode.Model;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Helpers.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BetterGenshinImpact.GameTask.UseRedeemCode;

/// <summary>
/// 自动检查并兑换兑换码服务
/// </summary>
public class AutoRedeemCodeChecker
{
    private static readonly ILogger _logger = App.GetLogger<AutoRedeemCodeChecker>();
    private readonly AutoRedeemCodeConfig _config;
    private readonly RedeemCodeHistoryStore _historyStore;

    public AutoRedeemCodeChecker()
    {
        _config = TaskContext.Instance().Config.AutoRedeemCodeConfig;
        _historyStore = new RedeemCodeHistoryStore(_config);
    }

    /// <summary>
    /// 检查是否需要自动兑换，如果有新兑换码则执行
    /// </summary>
    /// <param name="uid">当前游戏账号UID</param>
    /// <param name="ct">取消令牌</param>
    public async Task CheckAndRedeemIfNeeded(string uid, CancellationToken ct)
    {
        // 检查开关是否启用
        if (!_config.AutoRedeemCodeCheckEnabled)
        {
            _logger.LogDebug("自动兑换码检查已禁用");
            return;
        }

        // 检查是否是当天首次启动一条龙（按UID独立判断）
        if (!IsFirstOneDragonToday(uid))
        {
            _logger.LogDebug("UID {Uid} 今日已检查过兑换码，跳过", SensitiveTextMask.MaskUid(uid));
            return;
        }

        try
        {
            _logger.LogInformation("UID {Uid} 开始自动检查兑换码...", SensitiveTextMask.MaskUid(uid));

            // 入口处清理过期/兜底 TTL 已超的已兑换记录（design.md "清理策略选择" 章节）。
            // 由 IsFirstOneDragonToday(uid) 保证当日只跑一次，不依赖网络成功。
            var today = ServerTimeHelper.GetServerTimeNow().ToString("yyyy-MM-dd");
            _historyStore.Cleanup(today, DateTime.Now);

            // 获取最新兑换码列表
            var codeList = await FetchLatestRedeemCodesAsync();

            if (codeList == null || codeList.Count == 0)
            {
                _logger.LogInformation("UID {Uid} 当前没有可用的兑换码", SensitiveTextMask.MaskUid(uid));
                UpdateLastCheckDate(uid);
                return;
            }

            // 过滤掉已过期的兑换码
            var validCodeList = FilterExpiredCodes(uid, codeList);
            if (validCodeList.Count == 0)
            {
                _logger.LogInformation("UID {Uid} 当前没有未过期的兑换码（{ExpiredCount} 个已过期）", SensitiveTextMask.MaskUid(uid), codeList.Count);
                UpdateLastCheckDate(uid);
                return;
            }
            // 执行兑换
            _logger.LogInformation("UID {Uid} 发现 {Count} 个可兑换码，开始自动兑换...", SensitiveTextMask.MaskUid(uid), validCodeList.Count);
            var task = new UseRedemptionCodeTask(validCodeList, uid);
            await task.Start(ct);

            UpdateLastCheckDate(uid);

            // 发送通知
            _logger.LogInformation("UID {Uid} 自动兑换码检查完成，已处理 {Count} 个兑换码", SensitiveTextMask.MaskUid(uid), validCodeList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UID {Uid} 自动兑换码检查失败", SensitiveTextMask.MaskUid(uid));
            // 不抛出异常，避免阻塞一条龙执行
            UpdateLastCheckDate(uid);
        }
    }

    /// <summary>
    /// R4.6 D8/E2-4' 严格路径兑换检查结果（受控词表；cancelled 不经结果表达——OCE 直接传播）。
    /// 状态词表：disabled / alreadyCheckedToday / noNewCodes / redeemed / failed。
    /// SubmittedCount 语义遵循 OQ-1 锚点（提交即终态）：候选中已进入已兑换记录（=已提交）的码数。
    /// </summary>
    public sealed record RedeemCheckResult(string Status, int SubmittedCount, int CandidateCount, string? Reason)
    {
        public static RedeemCheckResult Of(string status, int submitted = 0, int candidates = 0, string? reason = null)
            => new(status, submitted, candidates, reason);
    }

    /// <summary>
    /// R4.6 D8/E2-4' 严格路径：真实结果 + 取消传播 + 失败不写当日标记 + 会话缓存按 UID 隔离。
    /// 与 <see cref="CheckAndRedeemIfNeeded"/>（原生容错语义，逐字不动）并存；流程前置动作只走本路径。
    /// </summary>
    public async Task<RedeemCheckResult> CheckAndRedeemIfNeededStrict(string uid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(uid))
            return RedeemCheckResult.Of("failed", reason: "UID 为空，严格路径拒绝执行");
        var masked = SensitiveTextMask.MaskUid(uid);

        if (!_config.AutoRedeemCodeCheckEnabled)
            return RedeemCheckResult.Of("disabled");
        if (!IsFirstOneDragonToday(uid))
            return RedeemCheckResult.Of("alreadyCheckedToday");

        var today = ServerTimeHelper.GetServerTimeNow().ToString("yyyy-MM-dd");
        _historyStore.Cleanup(today, DateTime.Now);

        List<RedeemCode> codeList;
        try
        {
            codeList = await FetchLatestRedeemCodesAsync(ct);
        }
        catch (OperationCanceledException) { throw; } // 取消传播，不写当日标记
        catch (Exception ex)
        {
            // 网络获取失败发生在任何游戏副作用之前，可安全重投（I4 传输重投语义）；不写当日标记
            _logger.LogWarning(ex, "UID {Uid} 兑换码列表获取失败（副作用前，可安全重投）", masked);
            return RedeemCheckResult.Of("failed", reason: "兑换码列表获取失败");
        }

        var candidates = FilterExpiredCodesStrict(uid, codeList);
        if (candidates.Count == 0)
        {
            UpdateLastCheckDate(uid); // 检查动作本身成功完成
            return RedeemCheckResult.Of("noNewCodes");
        }

        _logger.LogInformation("UID {Uid} 严格路径兑换 {Count} 个候选码", masked, candidates.Count);
        try
        {
            await new UseRedemptionCodeTask(candidates, uid).Start(ct);
        }
        catch (OperationCanceledException) { throw; } // 取消传播，不写当日标记
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UID {Uid} 严格路径兑换执行失败（不写当日标记，允许重查）", masked);
            return RedeemCheckResult.Of("failed", candidates: candidates.Count, reason: "兑换执行失败");
        }

        UpdateLastCheckDate(uid);
        var submitted = candidates.Count(c => _historyStore.IsRedeemed(uid, c.Code));
        return RedeemCheckResult.Of("redeemed", submitted, candidates.Count);
    }

    /// <summary>
    /// 严格路径过滤（E2-4'/I6）：持久化历史按 UID 键控；<b>不使用不分 UID 的会话成功缓存</b>——
    /// 严格路径必持显式 UID，历史记录在 OQ-1 提交锚点即时持久化，足以防同会话重复提交；
    /// 跳过会话成功缓存避免账号 A 的兑换事实抑制账号 B。
    /// </summary>
    private List<RedeemCode> FilterExpiredCodesStrict(string uid, List<RedeemCode> codeList)
    {
        var today = ServerTimeHelper.GetServerTimeNow().ToString("yyyy-MM-dd");
        return codeList.Where(code =>
        {
            if (_historyStore.IsRedeemed(uid, code.Code)) return false;
            if (RedeemCodeCache.IsRecentlyFailed(code.Code)) return false;
            if (string.IsNullOrEmpty(code.Valid)) return true;
            var isValid = string.Compare(code.Valid, today, StringComparison.Ordinal) >= 0;
            if (!isValid) RedeemCodeCache.MarkAsFailed(code.Code);
            return isValid;
        }).ToList();
    }
    /// <summary>
    /// 判断是否是当天首次启动一条龙（按UID独立判断）
    /// </summary>
    private bool IsFirstOneDragonToday(string uid)
    {
        var today = ServerTimeHelper.GetServerTimeNow().ToString("yyyy-MM-dd");
        if (_config.LastRedeemCodeCheckDates.TryGetValue(uid, out var lastDate))
        {
            return lastDate != today;
        }
        return true; // 没有记录，视为首次
    }

    /// <summary>
    /// 更新上次检查日期（按UID独立记录）
    /// </summary>
    private void UpdateLastCheckDate(string uid)
    {
        var today = ServerTimeHelper.GetServerTimeNow().ToString("yyyy-MM-dd");
        // 转调到 AutoRedeemCodeConfig 的 public 方法以显式触发 OnPropertyChanged → ConfigService.Save，
        // 避免依赖"别的属性变更顺手把 AllConfig 写盘"的隐式行为（design.md 风险 1）。
        _config.UpdateLastCheckDate(uid, today);
    }

    /// <summary>
    /// 过滤已过期的兑换码和已知不可用的兑换码（按 UID 维度）。
    /// 排除条件按短路顺序 OR：
    /// <list type="number">
    /// <item>持久化的已兑换记录（跨进程 UID 维度，覆盖主 bug）</item>
    /// <item>会话级成功 short-circuit（不分 UID，剪贴板兜底，design.md 风险 5）</item>
    /// <item>会话级失败 short-circuit（保留既有 1 天 TTL）</item>
    /// <item>显式 Valid &lt; today（既有过期分支，保持不动）</item>
    /// </list>
    /// </summary>
    private List<RedeemCode> FilterExpiredCodes(string uid, List<RedeemCode> codeList)
    {
        var today = ServerTimeHelper.GetServerTimeNow().ToString("yyyy-MM-dd");

        return codeList.Where(code =>
        {
            // 1) 跨进程 UID 维度：该 UID 历史上已成功兑换过该码
            if (_historyStore.IsRedeemed(uid, code.Code))
            {
                _logger.LogDebug("兑换码 {Code} 已在 UID {Uid} 历史记录中，跳过", code.Code, SensitiveTextMask.MaskUid(uid));
                return false;
            }

            // 2) 会话级、不分 UID 的成功 short-circuit（覆盖剪贴板路径）
            if (RedeemCodeCache.IsRecentlySucceededInSession(code.Code))
            {
                _logger.LogDebug("兑换码 {Code} 已在本会话内成功兑换，跳过", code.Code);
                return false;
            }

            // 3) 会话级失败 short-circuit
            if (RedeemCodeCache.IsRecentlyFailed(code.Code))
            {
                _logger.LogDebug("兑换码 {Code} 在本会话内已知失败，跳过", code.Code);
                return false;
            }

            // 4) 如果没有过期日期设置，则认为有效
            if (string.IsNullOrEmpty(code.Valid))
                return true;

            // 比较日期：只有在过期日期 >= 今天时才认为有效
            var isValid = string.Compare(code.Valid, today, StringComparison.Ordinal) >= 0;

            if (!isValid)
            {
                _logger.LogDebug("兑换码 {Code} 已过期（有效期至 {Valid}）", code.Code, code.Valid);
                RedeemCodeCache.MarkAsFailed(code.Code);
            }

            return isValid;
        }).ToList();
    }

    /// <summary>
    /// 从远程源获取最新的兑换码列表
    /// </summary>
    private Task<List<RedeemCode>> FetchLatestRedeemCodesAsync() => FetchLatestRedeemCodesAsync(CancellationToken.None);

    /// <summary>R4.6 B5：严格路径全链可取消版本（旧签名保留委托，语义不变）。</summary>
    private async Task<List<RedeemCode>> FetchLatestRedeemCodesAsync(CancellationToken ct)
    {
        const string codesJsonUrl = "https://cnb.cool/bettergi/genshin-redeem-code/-/git/raw/main/codes.json";
        using var httpClient = HttpClientFactory.GetCommonSendClient();
        var request = new HttpRequestMessage(HttpMethod.Get, codesJsonUrl);
        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        // 直接解析 codes.json，里面已有 Valid 日期
        var feedItems = JsonConvert.DeserializeObject<List<RedeemCodeFeedItem>>(json) ?? [];

        return feedItems
            .Where(f => f.Codes is { Count: > 0 })
            .SelectMany(f => f.Codes.Select(c => new RedeemCode(c, f.Content ?? f.Title, f.Valid)))
            .ToList();
    }

    /// <summary>
    /// codes.json 的 FeedItem 结构
    /// </summary>
    private class RedeemCodeFeedItem
    {
        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("content")]
        public string? Content { get; set; }

        [JsonProperty("codes")]
        public List<string>? Codes { get; set; }

        [JsonProperty("valid")]
        public string? Valid { get; set; }
    }

    /// <summary>
    /// 重置所有UID的检查状态。
    /// </summary>
    /// <remarks>
    /// 注意：此处直接 mutate <c>_config.LastRedeemCodeCheckDates</c>，
    /// 字典内部 mutation 不会触发 <c>[ObservableProperty]</c> setter，因此**不会**触发
    /// <c>OnPropertyChanged → ConfigService.Save</c>，属于既有行为（面向用户主动 Reset 操作，
    /// 通常会伴随其它属性变更顺手写盘）。本次 bugfix 未修，参见 design.md 风险 1 附带说明。
    /// </remarks>
    public void ResetAllCheckStatus()
    {
        _config.LastRedeemCodeCheckDates.Clear();
    }

    /// <summary>
    /// 重置指定UID的检查状态。
    /// </summary>
    /// <remarks>
    /// 注意：此处直接 mutate <c>_config.LastRedeemCodeCheckDates</c>，
    /// 字典内部 mutation 不会触发 <c>[ObservableProperty]</c> setter，因此**不会**触发
    /// <c>OnPropertyChanged → ConfigService.Save</c>，属于既有行为（面向用户主动 Reset 操作，
    /// 通常会伴随其它属性变更顺手写盘）。本次 bugfix 未修，参见 design.md 风险 1 附带说明。
    /// </remarks>
    public void ResetCheckStatus(string uid)
    {
        _config.LastRedeemCodeCheckDates.Remove(uid);
    }
}
