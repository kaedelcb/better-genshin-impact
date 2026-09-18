using System;
using System.IO;

namespace BetterGenshinImpact.Service;

/// <summary>
/// 配置组改名的文件操作（会诊第五轮 #1、第六轮 #1/#2、第七轮 #1/#2，2026-09-18）。
/// 改名流程为「引用更新 → 文件移动 → 内容保存」，跨多文件无法原子化。
/// 设计纪律：本助手**绝不删除文件**——先删后存在补保存失败时会丢失较新副本；
/// 无法可靠判定内容新旧的状态（两文件并存）一律冲突拒绝、保留现场，交人工核对。
/// 半成品判定绑定身份：仅当新路径文件的内部 name == 旧名，才认定它来自上次移动。
/// </summary>
internal static class ScriptGroupFileRename
{
    /// <summary>
    /// 执行或跳过组文件移动。true=已执行移动；false=可验证半成品/同文件别名（跳过移动，调用方继续内容保存）。
    /// 冲突（两文件并存无法判定、目标身份不符/不可解析）抛 IOException 且不改动任何文件；
    /// 源缺失且目标不存在抛 FileNotFoundException——失败均可见。
    /// </summary>
    public static bool MoveForRename(string scriptGroupDirectory, string oldName, string newName)
    {
        var oldPath = Path.Combine(scriptGroupDirectory, oldName + ".json");
        var newPath = Path.Combine(scriptGroupDirectory, newName + ".json");

        // 同一路径别名（Windows 不区分大小写：a.json 与 A.json 是同一文件）按单文件处理
        var alias = string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase);
        var oldExists = File.Exists(oldPath);
        var newExists = File.Exists(newPath);

        if (alias && oldExists)
        {
            // 纯大小写改名：移动无意义；身份匹配则跳过移动，由调用方保存内容
            // （文件名大小写是否随内容保存变化取决于文件系统，属 cosmetic 局限）
            var aliasName = TryReadInternalName(newPath);
            if (string.Equals(aliasName, oldName, StringComparison.Ordinal))
            {
                return false;
            }
            throw new IOException(
                $"改名冲突：{newName}.json 内部名（{aliasName ?? "不可解析"}）与「{oldName}」不符，请手工核对 User/ScriptGroup 目录");
        }

        if (oldExists && !newExists)
        {
            File.Move(oldPath, newPath);
            return true;
        }

        if (!oldExists && !newExists)
        {
            File.Move(oldPath, newPath); // 保持原生 FileNotFoundException（失败可见）
            return true;                 // 不可达
        }

        var targetInternalName = TryReadInternalName(newPath);
        var verifiedHalfDone = string.Equals(targetInternalName, oldName, StringComparison.Ordinal);

        if (!oldExists)
        {
            // 旧文件不在、新文件已在：唯一可信的半成品证据是「目标内部名 == 旧名」（移动后未保存）；
            // 冒名占用/不可解析一律冲突拒绝，绝不覆盖来历不明的文件。
            if (verifiedHalfDone)
            {
                return false;
            }
            throw new IOException(
                $"改名冲突：{newName}.json 已存在且不属于组「{oldName}」（内部名: {targetInternalName ?? "不可解析"}），请手工核对 User/ScriptGroup 目录");
        }

        // 两文件并存（半成品期间本机 UI 按内部名保存重建了旧文件；或重载后存在重复内存对象分别保存）：
        // 新旧内容无法可靠判定，不删除任何文件——保留现场，交人工核对（会诊第七轮 #1/#2）。
        throw new IOException(
            $"改名冲突：{oldName}.json 与 {newName}.json 同时存在，无法判定保留哪份内容；请手工核对 User/ScriptGroup 目录、删除其一后重试");
    }

    /// <summary>读取组文件的内部 name（大小写不敏感）；解析失败返回 null（视为身份不可判定）。</summary>
    private static string? TryReadInternalName(string path)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("name", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}