using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 槲寄生 · 启动中心「方案」快照的读写存储（命名保存/恢复整份启动流程配置）。
/// 路径：%APPDATA%/NexusBGI/startup-flow-schemes.json（与 startup-flow.json 同目录，按 Windows 用户隔离）。
/// 纪律与 StartupFlowStore 一致：JSON 损坏/反序列化失败 → 落回空列表，绝不让助手启动失败。
/// </summary>
public class StartupFlowSchemeStore
{
    private readonly string _schemesPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public StartupFlowSchemeStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "NexusBGI");
        Directory.CreateDirectory(dir);
        _schemesPath = Path.Combine(dir, "startup-flow-schemes.json");
    }

    public List<StartupFlowScheme> Load()
    {
        try
        {
            if (!File.Exists(_schemesPath)) return [];
            var json = File.ReadAllText(_schemesPath);
            return JsonSerializer.Deserialize<List<StartupFlowScheme>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            // 方案库损坏不炸启动：落回空列表（不影响主配置 startup-flow.json）
            System.Diagnostics.Debug.WriteLine($"[StartupFlowSchemeStore] 方案读取失败，已落回空列表: {ex.Message}");
            return [];
        }
    }

    public void SaveAll(IReadOnlyList<StartupFlowScheme> schemes)
    {
        try
        {
            var json = JsonSerializer.Serialize(schemes, JsonOptions);
            File.WriteAllText(_schemesPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupFlowSchemeStore] 方案保存失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 导出一份方案到文件：内容是单个 StartupFlowScheme 的 JSON，可用 ImportSchemes 读回。
    /// 与 Load/SaveAll 的「落回空」纪律不同——导出/导入是用户主动操作，失败要让用户看见，
    /// 异常一律上抛，由调用方记日志提示。
    /// </summary>
    public static void ExportScheme(StartupFlowScheme scheme, string filePath)
    {
        File.WriteAllText(filePath, JsonSerializer.Serialize(scheme, JsonOptions));
    }

    /// <summary>
    /// 从文件导入方案：兼容单方案对象（{ }）与方案库数组（[ ]）两种文件形状（按首字符区分，
    /// 数组里剔除 null 项），同一导入口覆盖「传方案给其他机器」与「整库迁移」两种场景。
    /// 异常上抛由调用方记日志提示；空文件返回空列表。
    /// </summary>
    public static List<StartupFlowScheme> ImportSchemes(string filePath)
    {
        var json = File.ReadAllText(filePath).TrimStart();
        if (json.Length == 0) return [];
        if (json[0] == '[')
        {
            var list = JsonSerializer.Deserialize<List<StartupFlowScheme?>>(json, JsonOptions) ?? [];
            return list.Where(s => s is not null).Select(s => s!).ToList();
        }
        return JsonSerializer.Deserialize<StartupFlowScheme>(json, JsonOptions) is { } single ? [single] : [];
    }

    /// <summary>
    /// 导入方案的重名处理（纯函数）：空名补默认名；与现有方案重名则追加「-导入MMdd HHmm」后缀，
    /// 后缀仍重名再补序号。保存命令的「同名覆盖」语义不受影响——改名只发生在导入这一步，
    /// 导入进来的方案不自动接管当前配置，同名方案静默改名比弹窗打断更合适（批量导入时尤其如此）。
    /// </summary>
    public static string MakeImportedNameUnique(string? name, IReadOnlyCollection<string> existingNames, DateTime now)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? $"导入方案 {now:MMdd HHmm}" : name.Trim();
        if (!existingNames.Contains(baseName)) return baseName;
        var stamped = $"{baseName}-导入{now:MMdd HHmm}";
        var n = 2;
        while (existingNames.Contains(stamped))
            stamped = $"{baseName}-导入{now:MMdd HHmm}({n++})";
        return stamped;
    }

    /// <summary>配置的深拷贝（JSON 往返）：保存快照与恢复快照都用它，保证方案与当前配置互不影响。</summary>
    public static StartupFlowConfig Clone(StartupFlowConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        return JsonSerializer.Deserialize<StartupFlowConfig>(json, JsonOptions) ?? new StartupFlowConfig();
    }
}
