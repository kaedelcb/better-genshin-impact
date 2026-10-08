using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoFriendship.Model;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using BetterGenshinImpact.Core.Config;

namespace BetterGenshinImpact.GameTask.AutoFriendship.Assets;

/// <summary>
/// 好感任务资源加载器
/// 优先从 BGI 内置资源目录加载，若不存在则回退到 JS 脚本目录
/// </summary>
public class AutoFriendshipResourceLoader
{
    private static readonly ILogger _logger = App.GetLogger<AutoFriendshipResourceLoader>();

    /// <summary>
    /// JS 脚本 assets 子目录名称
    /// </summary>
    public const string AssetsFolderName = "assets";

    /// <summary>
    /// AutoPath 子目录名称
    /// </summary>
    public const string AutoPathFolderName = "AutoPath";

    /// <summary>
    /// BGI 内置 Assets 根目录（编译输出位置）—— 当前权威路径
    /// 对应仓库 BetterGenshinImpact/GameTask/AutoFriendship/Assets/，由 csproj 拷贝到输出
    /// </summary>
    private static string GetBgiInternalAssetsRoot()
    {
        return Path.Combine(AppContext.BaseDirectory, "GameTask", "AutoFriendship", "Assets");
    }

    /// <summary>
    /// JS 旧脚本资源根目录（兼容回退路径）
    /// 对应 User/JsScript/AutoFriendshipFight/assets/
    /// </summary>
    private static string GetBgiResourceRoot()
    {
        return Path.Combine(Global.UserRoot, "JsScript", "AutoFriendshipFight", AssetsFolderName);
    }

    /// <summary>
    /// 获取 AutoPath 目录路径
    /// 优先 BGI 内置 Assets/AutoPath，未部署时回退 JS 旧目录 assets/AutoPath
    /// </summary>
    public static string GetAutoPathFolder()
    {
        var bgiAutoPath = Path.Combine(GetBgiInternalAssetsRoot(), AutoPathFolderName);
        if (Directory.Exists(bgiAutoPath))
        {
            return bgiAutoPath;
        }
        return Path.Combine(GetBgiResourceRoot(), AutoPathFolderName);
    }

    /// <summary>
    /// 检查资源是否可用（BGI 内置目录或 JS 旧目录任一存在即视为可用）
    /// </summary>
    public static bool IsResourceAvailable()
    {
        return Directory.Exists(GetBgiInternalAssetsRoot()) || Directory.Exists(GetBgiResourceRoot());
    }

    /// <summary>
    /// 加载指定敌人类型的路径配置
    /// </summary>
    public static EnemyPathConfig LoadPathConfigForEnemy(EnemyType enemyType)
    {
        var config = new EnemyPathConfig();
        var pathFileNames = GetPathFileNames(enemyType);

        try
        {
            var autoPathFolder = GetAutoPathFolder();
            if (!Directory.Exists(autoPathFolder))
            {
                _logger.LogWarning("AutoPath 目录不存在: {Path}", autoPathFolder);
                return config;
            }

            // 加载触发点路径
            var triggerPath = LoadPathFromFile(Path.Combine(autoPathFolder, pathFileNames.Trigger));
            if (triggerPath.Count > 0) config.TriggerPath = triggerPath;

            // 加载战斗点路径
            var combatPath = LoadPathFromFile(Path.Combine(autoPathFolder, pathFileNames.Combat));
            if (combatPath.Count > 0) config.CombatPath = combatPath;

            // 加载准备点路径
            var preparePath = LoadPathFromFile(Path.Combine(autoPathFolder, pathFileNames.Prepare));
            if (preparePath.Count > 0) config.PreparePath = preparePath;

            // 加载失败返回路径（默认复用准备点）
            var failReturnPath = LoadPathFromFile(Path.Combine(autoPathFolder, pathFileNames.FailReturn));
            if (failReturnPath.Count > 0) config.FailReturnPath = failReturnPath;
            else if (preparePath.Count > 0) config.FailReturnPath = preparePath;

            // 加载战后路径
            if (!string.IsNullOrEmpty(pathFileNames.PostFight))
            {
                var postFightPath = LoadPathFromFile(Path.Combine(autoPathFolder, pathFileNames.PostFight));
                if (postFightPath.Count > 0) config.PostFightPath = postFightPath;
            }

            // 加载战后对话路径
            if (!string.IsNullOrEmpty(pathFileNames.PostFightDialogue))
            {
                var postFightDialoguePath = LoadPathFromFile(Path.Combine(autoPathFolder, pathFileNames.PostFightDialogue));
                if (postFightDialoguePath.Count > 0) config.PostFightDialoguePath = postFightDialoguePath;
            }

            // 设置特殊敌人的参数
            SetEnemySpecialParams(enemyType, config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载 {EnemyType} 路径配置失败", enemyType);
        }

        return config;
    }

    /// <summary>
    /// 获取敌人类型对应的路径文件名
    /// </summary>
    private static PathFileNames GetPathFileNames(EnemyType enemyType)
    {
        var name = enemyType switch
        {
            EnemyType.Fatui => "愚人众",
            EnemyType.HilichurlBrigade => "盗宝团",
            EnemyType.Crocodile => "鳄鱼",
            EnemyType.Fungus => "蕈兽",
            EnemyType.ElectroMage => "雷萤术士",
            _ => "未知"
        };

        return new PathFileNames
        {
            Trigger = $"{name}-触发点.json",
            Combat = $"{name}-战斗点.json",
            Prepare = $"{name}-准备.json",
            FailReturn = $"{name}-准备.json",
            PostFight = enemyType == EnemyType.Crocodile ? $"{name}-拾取.json" : "",
            PostFightDialogue = enemyType == EnemyType.Fungus ? $"{name}-对话.json" : ""
        };
    }

    /// <summary>
    /// 决策路径文件实际位置：BGI 内置 AutoPath 优先，未命中回退 JS 旧目录 AutoPath；都缺返回 null。
    /// 抽出为 internal 便于 property-based test 直接覆盖（不依赖 AppContext.BaseDirectory）。
    /// </summary>
    internal static string? ResolvePathFile(string fileName, string bgiInternalRoot, string jsLegacyRoot)
    {
        var bgiCandidate = Path.Combine(bgiInternalRoot, AutoPathFolderName, fileName);
        if (File.Exists(bgiCandidate)) return bgiCandidate;
        var jsCandidate = Path.Combine(jsLegacyRoot, AutoPathFolderName, fileName);
        if (File.Exists(jsCandidate)) return jsCandidate;
        return null;
    }

    /// <summary>
    /// 从 JSON 文件加载路径点
    /// 优先级：BGI 内置 Assets/AutoPath > JS 旧目录 assets/AutoPath
    /// </summary>
    private static List<Waypoint> LoadPathFromFile(string filePath)
    {
        var waypoints = new List<Waypoint>();
        var fileName = Path.GetFileName(filePath);

        var bgiInternalRoot = GetBgiInternalAssetsRoot();
        var jsLegacyRoot = GetBgiResourceRoot();
        var resolved = ResolvePathFile(fileName, bgiInternalRoot, jsLegacyRoot);

        if (resolved == null)
        {
            var bgiCandidate = Path.Combine(bgiInternalRoot, AutoPathFolderName, fileName);
            var jsCandidate = Path.Combine(jsLegacyRoot, AutoPathFolderName, fileName);
            _logger.LogWarning("路径文件不存在: {BgiPath} 或 {JsPath}", bgiCandidate, jsCandidate);
            return waypoints;
        }

        try
        {
            var json = File.ReadAllText(resolved);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var data = JsonSerializer.Deserialize<PathJsonData>(json, options);

            if (data?.Positions != null)
            {
                foreach (var pos in data.Positions)
                {
                    waypoints.Add(new Waypoint
                    {
                        X = pos.X,
                        Y = pos.Y,
                        Type = ConvertPathType(pos.Type),
                        MoveMode = ConvertMoveMode(pos.MoveMode)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载路径文件失败: {Path}", resolved);
        }

        return waypoints;
    }

    private static string ConvertPathType(string? type) => type switch
    {
        "teleport" => "teleport",
        "target" => "target",
        "orientation" => "orientation",
        "action_only" => "action_only",
        _ => "path"
    };

    private static string ConvertMoveMode(string? mode) => mode switch
    {
        "walk" => "walk",
        "dash" => "dash",
        "fly" => "fly",
        "run" => "run",
        _ => "walk"
    };

    /// <summary>
    /// 设置特定敌人的特殊参数
    /// </summary>
    private static void SetEnemySpecialParams(EnemyType enemyType, EnemyPathConfig config)
    {
        switch (enemyType)
        {
            case EnemyType.Crocodile:
                config.InitialDelayMs = 5000;
                config.FailWaitTimeSeconds = 5;
                break;
            case EnemyType.Fungus:
                config.InitialDelayMs = 0;
                break;
        }
    }

    /// <summary>
    /// 加载经验值掉落模板
    /// </summary>
    public static Mat? LoadExpTemplate()
    {
        return LoadTemplate("exp.png");
    }

    /// <summary>
    /// 加载摩拉掉落模板
    /// </summary>
    public static Mat? LoadMoraTemplate()
    {
        return LoadTemplate("mora.png");
    }

    /// <summary>
    /// 加载模板图像
    /// 优先级：BGI 内置 Assets 目录 > JS 脚本 assets 目录
    /// </summary>
    private static Mat? LoadTemplate(string templateName)
    {
        // 尝试 BGI 内置 Assets 目录（编译输出位置）
        var bgiAssetsPath = Path.Combine(AppContext.BaseDirectory, "GameTask", "AutoFriendship", "Assets", templateName);
        if (File.Exists(bgiAssetsPath))
        {
            return LoadTemplateFromFile(bgiAssetsPath);
        }

        // 回退到 JS 脚本目录
        var jsScriptPath = Path.Combine(GetBgiResourceRoot(), templateName);
        if (File.Exists(jsScriptPath))
        {
            return LoadTemplateFromFile(jsScriptPath);
        }

        _logger.LogWarning("模板文件不存在: {BgiPath} 或 {JsPath}", bgiAssetsPath, jsScriptPath);
        return null;
    }

    private static Mat? LoadTemplateFromFile(string path)
    {
        try
        {
            var mat = Cv2.ImRead(path, ImreadModes.Color);
            if (mat.Empty())
            {
                _logger.LogWarning("模板文件为空: {Path}", path);
                return null;
            }
            // _logger.LogInformation("已加载模板: {Path}", path);
            return mat;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载模板失败: {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// 获取 JS 脚本定义的 OCR 关键词
    /// </summary>
    public static List<string> LoadOcrKeywordsForEnemy(EnemyType enemyType)
    {
        return enemyType switch
        {
            EnemyType.Fatui => new List<string> { "买卖", "不成", "正义存", "愚人众", "禁止", "危险", "运输", "打倒", "盗宝团", "丘丘人", "今晚", "伙食", "所有人" },
            EnemyType.HilichurlBrigade => new List<string> { "岛上", "无贼", "消灭", "鬼鬼祟祟", "盗宝团" },
            EnemyType.Crocodile => new List<string> { "张牙", "舞爪", "恶党", "鳄鱼", "打倒", "所有", "鳄鱼" },
            EnemyType.Fungus => new List<string> { "实验家", "变成", "实验品", "击败", "所有", "魔物" },
            EnemyType.ElectroMage => new List<string> { "雷萤", "术士", "圆滚滚", "不可食用", "威撼", "攀岩", "消灭", "准备", "打倒", "所有", "魔物", "盗宝团", "击败", "成员", "盗亦无道" },
            _ => new List<string> { "突发", "任务", "打倒", "消灭", "敌人", "所有" }
        };
    }

    private struct PathFileNames
    {
        public string Trigger { get; set; }
        public string Combat { get; set; }
        public string Prepare { get; set; }
        public string FailReturn { get; set; }
        public string PostFight { get; set; }
        public string PostFightDialogue { get; set; }
    }

    private class PathJsonData
    {
        public PathInfo? Info { get; set; }
        public List<PathPosition>? Positions { get; set; }
    }

    private class PathInfo
    {
        public string? Name { get; set; }
        public string? MapName { get; set; }
    }

    private class PathPosition
    {
        public double X { get; set; }
        public double Y { get; set; }
        public string? Type { get; set; }
        public string? MoveMode { get; set; }
        public string? Action { get; set; }
        public string? ActionParams { get; set; }
    }
}
