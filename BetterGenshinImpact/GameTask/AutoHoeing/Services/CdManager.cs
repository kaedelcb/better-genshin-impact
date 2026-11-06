using BetterGenshinImpact.GameTask.AutoHoeing.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>
/// CD时间管理器：精英怪路线24h CD，小怪路线12h CD
/// </summary>
public class CdManager
{
    private static readonly ILogger Logger = App.GetLogger<CdManager>();
    private Dictionary<string, CdRecord> _records = new();
    private string _filePath = "";

    public bool LoadSucceeded { get; private set; } = true;

    public void Load(string dataDir, string accountName)
    {
        LoadSucceeded = true;
        _filePath = Path.Combine(dataDir, "records", $"{accountName}.json");
        if (!File.Exists(_filePath))
        {
            _records = new();
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<CdRecord>>(json) ?? new();
            _records = list.ToDictionary(r => r.FileName, r => r);
        }
        catch (Exception ex)
        {
            LoadSucceeded = false;
            Logger.LogWarning("加载CD记录失败: {Msg}", ex.Message);
            _records = new();
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var list = _records.Values.ToList();
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            Logger.LogError("保存CD记录失败: {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// 确保账户 CD 记录文件存在（hoeing-multiplayer-account-name-config）：
    /// 任务启动时调用，若 records/{accountName}.json 不存在则立即创建一个空记录文件，
    /// 使新账户名一经运行即可见对应文件，不依赖路线是否跑完 / 房主成员角色。
    /// 已存在则不改动其内容。须在 Load 之后调用（Load 会设置 _filePath）。
    /// </summary>
    public void EnsureFileExists()
    {
        if (string.IsNullOrEmpty(_filePath)) return;
        if (File.Exists(_filePath)) return;
        Save();   // 当前 _records 为空则写出空数组，同时创建 records 目录
    }

    /// <summary>
    /// 判断路线是否在CD中
    /// </summary>
    public bool IsOnCooldown(RouteInfo route)
    {
        if (!_records.TryGetValue(route.FileName, out var record))
            return false;

        return DateTime.Now < record.CdTime;
    }

    internal bool IsOnCooldownAt(RouteInfo route, DateTime at)
        => _records.TryGetValue(route.FileName, out var record) && at < record.CdTime;

    /// <summary>
    /// 初始化路线的CD和运行记录
    /// </summary>
    public void InitializeRoute(RouteInfo route)
    {
        if (_records.TryGetValue(route.FileName, out var record))
        {
            route.CdTime = record.CdTime;
            route.Records = record.Records?.Take(7).ToList() ?? new();
            route.PickupHistory = record.Items != null
                ? new HashSet<string>(record.Items)
                : new();
        }
    }

    /// <summary>
    /// 记录路线执行完成
    /// </summary>
    public void RecordCompletion(RouteInfo route, double duration)
    {
        // 计算CD时间
        var now = DateTime.Now;
        var cdTime = CalculateCdTime(now, route.NormalMonsterCount > 0 && route.EliteMonsterCount == 0);

        route.CdTime = cdTime;
        route.Records.Add(duration);
        if (route.Records.Count > 7)
            route.Records = route.Records.TakeLast(7).ToList();

        // 更新记录
        _records[route.FileName] = new CdRecord
        {
            FileName = route.FileName,
            Tags = route.Tags,
            EstimatedTime = route.AdjustedTime,
            CdTime = cdTime,
            Records = route.Records.Where(v => v > 0).ToList(),
            Items = route.PickupHistory.Take(20).ToList()
        };
    }

    /// <summary>
    /// 计算CD时间：精英24h（下一个UTC 20:00即北京凌晨4点），小怪12h
    /// </summary>
    private static DateTime CalculateCdTime(DateTime now, bool isMonsterOnly)
    {
        // 下一个UTC 20:00（北京时间凌晨4点）
        var utcNow = now.ToUniversalTime();
        var nextReset = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, 20, 0, 0, DateTimeKind.Utc);
        if (nextReset <= utcNow)
            nextReset = nextReset.AddDays(1);

        var cdTime = nextReset.ToLocalTime();

        // 小怪路线：至少12小时
        if (isMonsterOnly)
        {
            var plus12H = now.AddHours(12);
            if (cdTime < plus12H)
                cdTime = plus12H;
        }

        return cdTime;
    }

    /// <summary>
    /// 清除所有CD记录
    /// </summary>
    public void ClearAll()
    {
        _records.Clear();
        Save();
    }

    /// <summary>
    /// 保存所有路线的当前状态（仅更新有CD记录的路线，保留文件中已有的其他记录）
    /// </summary>
    public void UpdateAllRecords(List<RouteInfo> routes)
    {
        // 先把内存中的路线数据合并到已有记录中
        foreach (var route in routes)
        {
            // 只有有效的CD时间才更新（避免覆盖JS写入的有效数据）
            if (route.CdTime > DateTime.MinValue || route.Records.Any(v => v > 0))
            {
                _records[route.FileName] = new CdRecord
                {
                    FileName = route.FileName,
                    Tags = route.Tags,
                    EstimatedTime = route.AdjustedTime,
                    CdTime = route.CdTime,
                    Records = route.Records.Where(v => v > 0).ToList(),
                    Items = route.PickupHistory.Take(20).ToList()
                };
            }
            else if (!_records.ContainsKey(route.FileName))
            {
                // 新路线，写入基本信息但不覆盖已有记录
                _records[route.FileName] = new CdRecord
                {
                    FileName = route.FileName,
                    Tags = route.Tags,
                    EstimatedTime = route.AdjustedTime,
                    Records = new(),
                    Items = new()
                };
            }
        }
        Save();
    }
}

/// <summary>
/// CD记录持久化模型 - 属性名与JS脚本保持一致以实现数据互通
/// </summary>
public class CdRecord
{
    [System.Text.Json.Serialization.JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("标签")]
    public List<string> Tags { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("预计用时")]
    public object EstimatedTimeRaw { get; set; } = 0;

    [System.Text.Json.Serialization.JsonIgnore]
    public double EstimatedTime
    {
        get => EstimatedTimeRaw is string s ? double.TryParse(s, out var v) ? v : 0 : Convert.ToDouble(EstimatedTimeRaw);
        set => EstimatedTimeRaw = value.ToString("F2");
    }

    [System.Text.Json.Serialization.JsonPropertyName("cdTime")]
    public string CdTimeStr { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime CdTime
    {
        get => DateTime.TryParse(CdTimeStr, out var dt) ? dt : DateTime.MinValue;
        set => CdTimeStr = value.ToString("yyyy/M/d HH:mm:ss");
    }

    [System.Text.Json.Serialization.JsonPropertyName("records")]
    public List<double> Records { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("items")]
    public List<string> Items { get; set; } = new();
}
