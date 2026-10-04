using System.IO;
using System.Text.Json;
using System.Windows;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>一套动画（表情）的播放信息。</summary>
public sealed class PetAnimSet
{
    /// <summary>动画 key（如 joy / sleep / act_hoeing）。</summary>
    public string Key { get; init; } = "";
    /// <summary>帧资源 pack URI（按播放顺序）。</summary>
    public IReadOnlyList<Uri> Frames { get; init; } = [];
    /// <summary>单帧停留毫秒（交叉淡化前的保持时长）。</summary>
    public double HoldMs { get; init; } = 500;
    /// <summary>帧间交叉淡化毫秒。0=硬切（不推荐，生硬）。</summary>
    public double FadeMs { get; init; } = 260;
}

/// <summary>
/// 奥黛塔动画目录：读 Assets/Pet/anims.json 清单（由 Tools/Import-OdetteAnims.ps1 生成）。
/// 帧文件位于 Assets/Pet/Anims/{key}/f1.png, f2.png。清单缺失/损坏时退化为仅 tea 单帧兜底。
/// </summary>
public sealed class PetAnimCatalog
{
    private const string BaseUri = "pack://application:,,,/MultiplayerHoeingAssistant;component/Assets/Pet/";

    /// <summary>状态切换时整体交叉淡化毫秒。</summary>
    public double StateFadeMs { get; private set; } = 200;

    private readonly Dictionary<string, PetAnimSet> _sets = new();

    public static PetAnimCatalog Instance { get; } = new();

    private PetAnimCatalog() => Load();

    private void Load()
    {
        double holdMs = 500, fadeMs = 260;
        try
        {
            var uri = new Uri(BaseUri + "anims.json");
            var info = Application.GetResourceStream(uri) ?? throw new FileNotFoundException("anims.json 不存在");
            using var reader = new StreamReader(info.Stream);
            var doc = JsonDocument.Parse(reader.ReadToEnd());
            if (doc.RootElement.TryGetProperty("timing", out var timing))
            {
                if (timing.TryGetProperty("holdMs", out var h)) holdMs = h.GetDouble();
                if (timing.TryGetProperty("fadeMs", out var f)) fadeMs = f.GetDouble();
                if (timing.TryGetProperty("stateFadeMs", out var sf)) StateFadeMs = sf.GetDouble();
            }
            if (doc.RootElement.TryGetProperty("sets", out var sets) && sets.ValueKind == JsonValueKind.Object)
            {
                foreach (var s in sets.EnumerateObject())
                {
                    if (!s.Value.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array)
                        continue;
                    var list = new List<Uri>();
                    foreach (var fr in frames.EnumerateArray())
                    {
                        var name = fr.GetString();
                        if (string.IsNullOrEmpty(name)) continue;
                        list.Add(new Uri(BaseUri + "Anims/" + s.Name + "/" + name));
                    }
                    if (list.Count == 0) continue;
                    _sets[s.Name] = new PetAnimSet
                    {
                        Key = s.Name,
                        Frames = list,
                        HoldMs = holdMs,
                        FadeMs = fadeMs
                    };
                }
            }
        }
        catch
        {
            // 清单缺失/损坏：仅保留兜底集
        }

        if (!_sets.ContainsKey("tea"))
        {
            _sets["tea"] = new PetAnimSet
            {
                Key = "tea",
                Frames = [new Uri(BaseUri + "odette_base.png")],
                HoldMs = 800,
                FadeMs = 0
            };
        }
    }

    /// <summary>取动画集；key 不存在时回退 tea（目录兜底），再不行返回 null。</summary>
    public PetAnimSet? Get(string key)
        => _sets.TryGetValue(key, out var set) ? set
         : _sets.TryGetValue("tea", out var fallback) ? fallback
         : null;
}
