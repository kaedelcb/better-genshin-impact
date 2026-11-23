using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public class AssistConfigManager
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly string _configPath;
    private readonly string _legacyFireDatePath;
    private readonly object _configWriteGate = new();
    private bool _configTransferActive;

    public string? RoomProfileMigrationWarning { get; private set; }
    internal void SetConfigTransferActive(bool active)
    {
        lock (_configWriteGate) _configTransferActive = active;
    }
    /// <summary>旧版配置路径（基于 exe 目录）。用于首次运行时迁移到新的用户隔离目录，防止同电脑不同 Windows 用户共享配置、以及旧配置丢失。</summary>
    private readonly string _legacyConfigPath;

    public AssistConfigManager()
    {
        // 配置路径改为"按 Windows 用户隔离"：不同用户有独立 %APPDATA%，同电脑不同 Windows 用户在各自目录各存一份配置，互不覆盖。
        // 这也是同 UID 双端（执行端 + 遥控端，可能在同一台电脑的不同 Windows 用户）的前提——每端配置独立。
        var dir = AssistantDataDirectory.Root;
        ConfigTransferService.RecoverInterruptedImport(dir);
        System.IO.Directory.CreateDirectory(dir);
        _configPath = System.IO.Path.Combine(dir, "assistant-config.json");
        _legacyFireDatePath = Path.Combine(dir, "assistant-online-fired-date.txt");

        // 旧版基于 exe 目录的配置路径（历史版本）。若存在且新路径无配置，则迁移。
        var appDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
        _legacyConfigPath = System.IO.Path.Combine(appDir, "assistant-config.json");
    }

    /// <summary>首次使用时把旧版 exe 目录配置迁移到新用户隔离路径（迁移后保留旧文件备份，防止迁移出错数据丢失）。</summary>
    private void MigrateIfNeeded()
    {
        try
        {
            using var writing = ConfigFileWriteGate.Acquire(_configPath);
            if (System.IO.File.Exists(_legacyConfigPath) && !System.IO.File.Exists(_configPath))
            {
                // 迁移到新路径
                System.IO.File.Copy(_legacyConfigPath, _configPath);
                // 旧文件改名备份（_bak），避免下次启动再重复迁移；原始旧文件保留一段时间
                var backup = _legacyConfigPath + ".bak";
                if (!System.IO.File.Exists(backup))
                    System.IO.File.Copy(_legacyConfigPath, backup);
            }
        }
        catch
        {
            // 迁移失败不影响加载（Load 会走空配置默认值）；配置迁移是尽力而为
        }
    }

    /// <summary>
    /// [A0 容错 2026-09-13] 配置文件损坏/被占/半截写入（程序崩溃瞬间保存）时：
    /// 备份坏文件为 .corrupt-时间戳（不删原始证据）后回退默认配置，绝不上抛——
    /// 旧实现裸读裸反序列化，一次坏盘写 = 助手永久无法启动（App.OnStartup 弹"初始化失败"并退出）。
    /// </summary>
    public AssistConfig Load()
    {
        RoomProfileMigrationWarning = null;
        if (!AssistantDataDirectory.IsExplicit) MigrateIfNeeded();
        if (!File.Exists(_configPath))
        {
            if (File.Exists(_legacyFireDatePath))
                RoomProfileMigrationWarning = "旧定时上线记录无法确定所属房间，已原样保留，请核对。";
            return new AssistConfig();
        }
        try
        {
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<AssistConfig>(json, ReadOptions) ?? new AssistConfig();
            if (config.EnsureRoomProfiles())
            {
                LoadLegacyScheduledFireDate(config);
                if (!TryStore(config, expectedRaw: json))
                    RoomProfileMigrationWarning = "旧房间档案未能保存，原配置仍保留；可重启重试。 " + RoomProfileMigrationWarning;
            }
            else if (config.ActiveRoomProfile is { } active)
            {
                active.ApplyTo(config);
            }
            else if (config.ActiveRoomProfileId.Length > 0 || (config.RoomProfiles.Count == 0
                && (config.TeamUids is { Count: > 0 } || File.Exists(_legacyFireDatePath))))
            {
                RoomProfileMigrationWarning = "无法确定当前房间，旧配置和历史记录已保留，请在房间管理中核对。";
            }
            return config;
        }
        catch (Exception ex)
        {
            // 留痕 + 备份坏文件（备份失败不掩盖主流程，仅留痕）
            System.Diagnostics.Debug.WriteLine($"[AssistConfig] 配置加载失败，回退默认配置: {ex.Message}");
            try
            {
                var corrupt = _configPath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                System.IO.File.Copy(_configPath, corrupt);
                System.Diagnostics.Debug.WriteLine($"[AssistConfig] 损坏配置已备份: {corrupt}");
            }
            catch (Exception backupEx)
            {
                System.Diagnostics.Debug.WriteLine($"[AssistConfig] 损坏配置备份失败: {backupEx.Message}");
            }
            return new AssistConfig();
        }
    }

    private void LoadLegacyScheduledFireDate(AssistConfig config)
    {
        try
        {
            // 文件保持原样。只有旧配置能唯一定位原房间时才接续此记录。
            var raw = File.ReadAllText(_legacyFireDatePath).Trim();
            if (DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || DateTime.TryParse(raw, out date))
            {
                config.ScheduledOnlineLastFireDate = date.Date;
                return;
            }
        }
        catch (FileNotFoundException) { return; }
        catch (IOException ex) { System.Diagnostics.Debug.WriteLine($"[AssistConfig] 读取旧定时记录失败: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine($"[AssistConfig] 读取旧定时记录失败: {ex.Message}"); }

        if (!string.IsNullOrEmpty(config.ScheduledOnlineTime)) config.ScheduledOnlineRequiresConfirmation = true;
        RoomProfileMigrationWarning = "旧定时上线记录无法读取，原文件已保留，请核对房间的定时设置。";
    }

    /// <summary>普通设置保存沿用失败不抛；切房等须确认落盘的提交使用 TrySave。</summary>
    public void Save(AssistConfig config) => TrySave(config);

    public bool TrySave(AssistConfig config) => TryStore(config);

    private bool TryStore(AssistConfig config, string? expectedRaw = null, bool rawEdit = false)
    {
        lock (_configWriteGate)
        {
            if (_configTransferActive) return false;
            try
            {
                using var writing = ConfigFileWriteGate.Acquire(_configPath);
                var raw = File.Exists(_configPath) ? File.ReadAllText(_configPath) : null;
                if (expectedRaw != null && !string.Equals(raw, expectedRaw, StringComparison.Ordinal)) return false;
                var diskHasRooms = HasStoredRoomProfiles(raw);
                if (rawEdit)
                {
                    // 不能用一份旧格式文本编辑静默覆盖已有的独立档案。
                    if (diskHasRooms && config.RoomProfiles.Count == 0) return false;
                    config.ActiveRoomProfile?.ApplyTo(config);
                }
                config.EnsureRoomProfiles();
                if (config.RoomProfiles.Count > 0 && !diskHasRooms && raw != null && !BackUpBeforeRoomProfiles(raw))
                    return false;
                config.ActiveRoomProfile?.Capture(config);
                return LocalPeerSyncService.WriteFileAtomic(_configPath, JsonSerializer.Serialize(config, WriteOptions));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AssistConfig] 配置保存失败: {ex.Message}");
                return false;
            }
        }
    }

    private bool BackUpBeforeRoomProfiles(string raw)
    {
        var backup = _configPath + $".before-room-profiles-{ConfigRevision(raw)[..12]}.bak";
        if (!File.Exists(backup))
        {
            // 失败不留下半截的正式备份，下一次可重新复制。按原字节保留，不转码。
            var staging = $"{backup}.{Environment.ProcessId}.tmp";
            File.Copy(_configPath, staging, overwrite: true);
            File.Move(staging, backup);
        }
        return string.Equals(raw, File.ReadAllText(backup), StringComparison.Ordinal);
    }

    private static bool HasStoredRoomProfiles(string? raw)
    {
        if (raw == null) return false;
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(property =>
                    string.Equals(property.Name, "roomProfiles", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() > 0);
        }
        catch (JsonException)
        {
            // 允许从原有损坏配置继续使用；建立首个档案前同样保留原文件。
            return false;
        }
    }

    /// <summary>读取 assistant-config.json 的原始 JSON 文本（用于设置弹窗编辑展示）。</summary>
    public string ReadRawJson()
    {
        return File.Exists(_configPath) ? File.ReadAllText(_configPath) : "{}";
    }

    public (string Path, string Revision) ReadBgiPathChecked()
    {
        lock (_configWriteGate)
        {
            var raw = ReadRawJson();
            var config = JsonSerializer.Deserialize<AssistConfig>(raw, ReadOptions)
                ?? throw new InvalidDataException("执行端助手配置无法读取");
            return (config.BgiPath, ConfigRevision(raw));
        }
    }

    public (bool Saved, string? Revision, string Message) SaveBgiPathChecked(
        string path, string? expectedRevision)
    {
        using var writing = ConfigFileWriteGate.Acquire(_configPath);
        lock (_configWriteGate)
        {
            if (_configTransferActive) return (false, null, "设置迁移尚未完成，配置写入已暂停");
            if (string.IsNullOrWhiteSpace(expectedRevision))
                return (false, null, "缺少执行端设置修订，请重新读取");
            var raw = ReadRawJson();
            if (ConfigRevision(raw) != expectedRevision)
                return (false, null, "执行端助手设置已变化，请重新读取后再保存");
            var root = JsonNode.Parse(raw) as JsonObject
                ?? throw new InvalidDataException("执行端助手配置不是 JSON 对象");
            root["bgiPath"] = path;
            var updated = root.ToJsonString(WriteOptions);
            if (File.Exists(_configPath))
            {
                var backup = _configPath + "." + expectedRevision[..8] + ".backup.json";
                if (!File.Exists(backup)) File.Copy(_configPath, backup);
            }
            if (!LocalPeerSyncService.WriteFileAtomic(_configPath, updated))
                return (false, null, "执行端助手设置写入失败，原配置保持");
            var readBack = ReadRawJson();
            if (!string.Equals(readBack, updated, StringComparison.Ordinal))
                return (false, null, "执行端设置写入结果未确认，请重新读取核对");
            return (true, ConfigRevision(readBack), "执行端 BGI 路径已保存；重启执行端助手后生效");
        }
    }

    private static string ConfigRevision(string raw)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    /// <summary>校验并原子保存用户编辑的 JSON，保留文件中的所有档案字段。</summary>
    public bool WriteRawJson(string json)
    {
        try
        {
            var config = JsonSerializer.Deserialize<AssistConfig>(json, ReadOptions);
            return config != null && TryStore(config, rawEdit: true);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string GenerateControlRoomCode(List<string> playerUids)
        => AssistConfig.GenerateLegacyRoomCode(playerUids);
}
