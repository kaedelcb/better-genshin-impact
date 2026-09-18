using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// 一条龙配置文件形状（R3.0 过渡窗口加载保护，R0 基线 §8 / 开工定案 §3）。
/// </summary>
public enum OneDragonConfigShape
{
    /// <summary>无法可靠区分（如完全空壳）。受保护：不猜、不写、不执行。</summary>
    Unknown = 0,

    /// <summary>公版现行：<c>TaskEnabledList {id:bool}</c> + <c>TaskOrder</c>/<c>TaskDefinitions</c>。唯一可正常加载的形状。</summary>
    PublicCurrent,

    /// <summary>旧 name→bool，无 definitions/order。受保护：待迁移。</summary>
    LegacyNameBool,

    /// <summary>茶包 tuple：<c>{数字键:{Item1,Item2}}</c> + <c>NextTaskIndex</c>。受保护：待迁移。</summary>
    TeabagTuple,

    /// <summary>结构损坏：数组/标量/混合形状/坏 JSON。隔离。</summary>
    StructuralBad,
}

/// <summary>
/// 形状预检结论。哈希绑定实际读取字节（SHA256 十六进制，与 R2 配置修订同族），
/// 供保护状态比对「文件在启动后被外部替换」。
/// </summary>
public sealed record OneDragonConfigShapeVerdict(OneDragonConfigShape Shape, string? Error, string? ContentHash)
{
    /// <summary>受保护：旧格式/歧义/损坏文件——只读识别与报告，任何入口不得写回、不得执行。</summary>
    public bool IsProtected => Shape is not OneDragonConfigShape.PublicCurrent;

    /// <summary>可进入普通加载路径。</summary>
    public bool IsLoadable => Shape == OneDragonConfigShape.PublicCurrent;
}

/// <summary>
/// 一条龙配置形状预检。判型只看 JSON DOM，不反序列化为产品模型。
/// 规则移植自 R1 迁移器 <c>OneDragonMigrationEngine.DetectFormat</c>（Test/OneDragonMigration），
/// 并按 2026-09-18 会诊修订加固：数组/标量/混合形状/空值一律 StructuralBad，不沿用「只看首元素」。
/// </summary>
public static class OneDragonConfigShapePreflight
{
    /// <summary>对文件字节做形状预检（哈希绑定原始字节）。</summary>
    public static OneDragonConfigShapeVerdict InspectBytes(byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        string json;
        try
        {
            json = Encoding.UTF8.GetString(bytes).TrimStart('﻿'); // 去 BOM，避免带 BOM 的合法文件误判 StructuralBad
        }
        catch (Exception ex)
        {
            return new OneDragonConfigShapeVerdict(OneDragonConfigShape.StructuralBad, "UTF-8 解码失败: " + ex.Message, hash);
        }
        return InspectText(json, hash);
    }

    /// <summary>对已读出的文本做形状预检（哈希按 UTF-8 字节计算）。</summary>
    public static OneDragonConfigShapeVerdict InspectText(string json)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return InspectText(json, hash);
    }

    private static OneDragonConfigShapeVerdict InspectText(string json, string hash)
    {
        json = json.TrimStart('﻿'); // 去 BOM
        JsonObject raw;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj)
            {
                return new OneDragonConfigShapeVerdict(OneDragonConfigShape.StructuralBad, "根节点不是 JSON 对象", hash);
            }
            raw = obj;
        }
        catch (JsonException ex)
        {
            return new OneDragonConfigShapeVerdict(OneDragonConfigShape.StructuralBad, "JSON 解析失败: " + ex.Message, hash);
        }

        var shape = Detect(raw);
        return new OneDragonConfigShapeVerdict(shape, shape == OneDragonConfigShape.StructuralBad ? "TaskEnabledList 形状异常" : null, hash);
    }

    /// <summary>对已解析的 JSON DOM 判型（不触发产品模型反序列化）。</summary>
    public static OneDragonConfigShape Detect(JsonObject raw)
    {
        if (raw.ContainsKey("TaskEnabledList") && raw["TaskEnabledList"] is not JsonObject)
        {
            // 数组/标量/null 字面量：结构损坏
            return OneDragonConfigShape.StructuralBad;
        }

        if (raw["TaskEnabledList"] is not JsonObject list || list.Count == 0)
        {
            // 空列表或缺字段：靠标记字段判型。缺 TaskOrder 的公版文件不降级为名称键（R1 加固结论）。
            if (raw.ContainsKey("NextTaskIndex"))
            {
                return OneDragonConfigShape.TeabagTuple;
            }
            if (raw.ContainsKey("TaskOrder") || raw.ContainsKey("TaskDefinitions"))
            {
                return OneDragonConfigShape.PublicCurrent;
            }
            return raw.ContainsKey("TaskEnabledList") ? OneDragonConfigShape.LegacyNameBool : OneDragonConfigShape.Unknown;
        }

        bool? tupleShape = null;
        foreach (var kv in list)
        {
            var isTuple = kv.Value is JsonObject tuple && tuple.ContainsKey("Item1");
            var isBool = IsBoolValue(kv.Value);
            if (!isTuple && !isBool)
            {
                return OneDragonConfigShape.StructuralBad;
            }
            if (tupleShape == null)
            {
                tupleShape = isTuple;
            }
            else if (tupleShape.Value != isTuple)
            {
                // tuple 与 bool 混存：混合形状，隔离
                return OneDragonConfigShape.StructuralBad;
            }
        }

        if (tupleShape == true)
        {
            return OneDragonConfigShape.TeabagTuple;
        }

        var hasDefs = raw["TaskDefinitions"] is JsonObject defs && defs.Count > 0;
        return hasDefs || raw.ContainsKey("TaskOrder") ? OneDragonConfigShape.PublicCurrent : OneDragonConfigShape.LegacyNameBool;
    }

    private static bool IsBoolValue(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return false;
        }
        try
        {
            return value.TryGetValue<bool>(out _);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

