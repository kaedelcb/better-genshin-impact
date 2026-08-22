using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using System.IO;
using System.Text;
using BetterGenshinImpact.Service;
using CommunityToolkit.Mvvm.ComponentModel;
using JsonSerializer = System.Text.Json.JsonSerializer;
namespace BetterGenshinImpact.Core.Script.Group;

/// <summary>
/// 调度器 配置组
/// </summary>
public partial class ScriptGroup : ObservableObject
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

    public int Index { get; set; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private ScriptGroupConfig _config = new();

    [ObservableProperty]
    private ObservableCollection<ScriptGroupProject> _projects = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public bool NextFlag
    {
        get => _nextFlag;
        set => SetProperty(ref _nextFlag, value);
    }
    private bool _nextFlag;

    public ScriptGroup()
    {
        Projects.CollectionChanged += ProjectsCollectionChanged;
    }

    private void ProjectsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Projects));
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, ConfigService.JsonOptions);
    }

    public void WriteToFileAtomically(string filePath)
    {
        var json = ToJson();
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new ArgumentException("配置组文件路径无效", nameof(filePath));
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, json, Utf8WithoutBom);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    public static ScriptGroup FromJson(string json)
    {
        var group = JsonSerializer.Deserialize<ScriptGroup>(json, ConfigService.JsonOptions) ?? throw new Exception("解析配置组JSON配置失败");
        ResetGroupInfo(group);
        NormalizeSoloTaskSettings(group);
        return group;
    }

    public static void ResetGroupInfo(ScriptGroup group)
    {
        foreach (var project in group.Projects)
        {
            project.GroupInfo = group;
        }
    }

    /// <summary>
    /// 将 SoloTask 项目中 SoloTaskSettingsObject 里的 JsonElement 值转换为 CLR 原生类型。
    /// System.Text.Json 反序列化 Dictionary&lt;string, object?&gt; 时会将值保留为 JsonElement，
    /// 导致 UI 层的模式匹配（如 v is true）失败。此方法在反序列化后立即规范化这些值。
    /// </summary>
    private static void NormalizeSoloTaskSettings(ScriptGroup group)
    {
        foreach (var project in group.Projects)
        {
            if (project.SoloTaskSettingsObject == null || project.SoloTaskSettingsObject.Count == 0)
                continue;

            try
            {
                var keys = new List<string>(project.SoloTaskSettingsObject.Keys);
                foreach (var key in keys)
                {
                    var value = project.SoloTaskSettingsObject[key];
                    if (value is JsonElement element)
                    {
                        project.SoloTaskSettingsObject[key] = ConvertJsonElement(element);
                    }
                }
            }
            catch
            {
                // 规范化失败不影响正常使用，保留原始值
            }
        }
    }

    /// <summary>
    /// 将 JsonElement 转换为对应的 CLR 类型
    /// </summary>
    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => ConvertJsonNumber(element),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null => null,
            _ => element // Array/Object 保留为 JsonElement
        };
    }

    /// <summary>
    /// 将 JsonElement 数值转换为最合适的 CLR 数值类型。
    /// 由于 UI 中 NumberBox.Value 为 double? 类型，保存时存储的是 double，
    /// 因此优先尝试保持为 double 以避免类型不匹配。
    /// </summary>
    private static object ConvertJsonNumber(JsonElement element)
    {
        // 如果是整数且在 int 范围内，检查是否有小数部分
        // JSON 中 3 和 3.0 都可能表示同一个值，但 NumberBox 保存的是 double
        // 为了兼容性，统一转为 double
        return element.GetDouble();
    }

    public void AddProject(ScriptGroupProject project)
    {
        project.GroupInfo = this;
        Projects.Add(project);
    }
}
