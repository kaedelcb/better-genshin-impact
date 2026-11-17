using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusBGI.RoutePackages;

internal static class RoutePackageJson
{
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 64, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    internal static JsonObject Parse(byte[] bytes, bool json5)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
            if (json5) text = NormalizeJson5(text);
            using var document = JsonDocument.Parse(text, Options);
            CheckProperties(document.RootElement);
            return JsonNode.Parse(text, documentOptions: Options) as JsonObject ?? throw RoutePackageArchive.Invalid("JSON 根必须为对象");
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or ArgumentException)
        { throw RoutePackageArchive.Invalid("无效 JSON: " + ex.Message); }
    }

    private static void CheckProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in element.EnumerateObject())
            {
                if (!keys.Add(p.Name)) throw RoutePackageArchive.Invalid("JSON 属性重复: " + p.Name);
                CheckProperties(p.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckProperties(item);
    }

    internal static void ValidateRoutes(Dictionary<string, JsonObject> nodes)
    {
        var resolved = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        JsonObject Resolve(string path, HashSet<string> seen)
        {
            if (resolved.TryGetValue(path, out var cached)) return cached;
            if (seen.Count >= 64 || !seen.Add(path)) throw RoutePackageArchive.Invalid("control ref 循环或过深");
            var control = nodes[path];
            if (control.TryGetPropertyValue("ref", out var reference) && reference is not null && Text(reference, "ref") is { Length: > 0 } value)
            {
                if (string.IsNullOrWhiteSpace(value) || value.Contains('\\') || value.Contains(':') || value.StartsWith('/')) throw RoutePackageArchive.Invalid("control ref 必须是包内相对路径");
                value = value.TrimEnd('/');
                var parts = path.Split('/').SkipLast(1).ToList();
                foreach (var segment in value.Split('/'))
                {
                    if (segment == ".") continue;
                    if (segment == "..") { if (parts.Count == 0) throw RoutePackageArchive.Invalid("control ref 越界"); parts.RemoveAt(parts.Count - 1); }
                    else { if (segment.Length == 0) throw RoutePackageArchive.Invalid("control ref 路径无效"); parts.Add(segment); }
                }
                var target = string.Join("/", parts);
                RoutePackageArchive.ValidatePath(target);
                if (!nodes.ContainsKey(target)) target += "/control.json5";
                if (!nodes.ContainsKey(target) || !RoutePackageArchive.IsControl(target)) throw RoutePackageArchive.Invalid("control ref 目标不存在或不是控制文件");
                return resolved[path] = Resolve(target, seen);
            }
            if (control["global_cover"] is not null and not JsonObject || control["json_list"] is not null and not JsonArray)
                throw RoutePackageArchive.Invalid("control 覆盖规则类型错误");
            if (control["global_cover"] is JsonObject globalCover) ValidateMergeInstructions(globalCover);
            if (control["json_list"] is JsonArray list)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in list)
                {
                    if (item is not JsonObject rule || rule["cover"] is not JsonObject cover || !names.Add(Text(rule["name"], "name")))
                        throw RoutePackageArchive.Invalid("control json_list 无效/名称重复");
                    ValidateMergeInstructions(cover);
                }
            }
            return resolved[path] = control;
        }
        foreach (var path in nodes.Keys.Where(RoutePackageArchive.IsControl)) Resolve(path, new HashSet<string>(StringComparer.Ordinal));
        var variants = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in nodes.Where(p => !RoutePackageArchive.IsControl(p.Key)))
        {
            var route = (JsonObject)pair.Value.DeepClone();
            var controlPath = pair.Key[..(pair.Key.LastIndexOf('/') + 1)] + "control.json5";
            if (resolved.TryGetValue(controlPath, out var control))
            {
                if (control["global_cover"] is JsonObject global) Merge(global, route);
                if (control["json_list"] is JsonArray list)
                    foreach (var item in list.OfType<JsonObject>())
                        if (Text(item["name"], "name") == Path.GetFileNameWithoutExtension(pair.Key) && item["cover"] is JsonObject cover) { Merge(cover, route); break; }
            }
            var syncs = ValidateRoute(route, pair.Key);
            var parts = pair.Key.Split('/');
            if (parts.Length >= 4 && parts[^2] is "A变体" or "B变体" or "C变体" or "D变体")
            {
                var name = Path.GetFileNameWithoutExtension(parts[^1]);
                var suffix = "_" + char.ToLowerInvariant(parts[^2][0]);
                // 与客户端 RouteVariantNaming.StripBaseName 一致：有后缀就去掉，没有就按原文件名配对。
                // 实际线路目录里的变体文件可以不带 _a/_b 后缀，不能因此拒绝整个线路包。
                var baseName = name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? name[..^suffix.Length] : name;
                var key = string.Join("/", parts.SkipLast(2)) + "/" + baseName;
                if (variants.TryGetValue(key, out var prior) && !prior.SequenceEqual(syncs)) throw RoutePackageArchive.Invalid("变体同步点顺序不一致: " + key);
                variants[key] = syncs;
            }
        }
    }

    private static List<string> ValidateRoute(JsonObject route, string path)
    {
        if (route["positions"] is not JsonArray positions || positions.Count == 0 || positions.Count > 100000) throw RoutePackageArchive.Invalid("线路 positions 为空/超限: " + path);
        if (route["info"] is not null and not JsonObject || route["config"] is not null and not JsonObject) throw RoutePackageArchive.Invalid("线路基本类型错误");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var syncs = new List<string>();
        foreach (var item in positions)
        {
            if (item is not JsonObject point) throw RoutePackageArchive.Invalid("点位必须是对象");
            foreach (var coordinate in new[] { "x", "y" })
                if (point[coordinate] is not JsonValue value || !value.TryGetValue<double>(out var number) || !double.IsFinite(number)) throw RoutePackageArchive.Invalid("点位坐标必须是有限数值");
            foreach (var key in new[] { "type", "move_mode", "action", "action_params", "sync_point_id" })
                if (point[key] is { } field) _ = Text(field, key);
            if (point["sync_point_id"] is { } sync && Text(sync, "sync_point_id") is { Length: > 0 } id)
            {
                if (id.Length > 256 || !ids.Add(id)) throw RoutePackageArchive.Invalid("重复或过长同步点: " + path);
                syncs.Add(id);
            }
        }
        if (route["logical_route_id"] is { } logical) _ = Text(logical, "logical_route_id");
        return syncs;
    }

    private static string Text(JsonNode? node, string name)
        => node is JsonValue v && v.TryGetValue<string>(out var text) ? text : throw RoutePackageArchive.Invalid(name + " 必须是字符串");

    private static void ValidateMergeInstructions(JsonObject source)
    {
        foreach (var instruction in new[] { "_obj_cover", "_arr_add" })
        {
            if (source[instruction] is null) continue;
            if (source[instruction] is not JsonArray names) throw RoutePackageArchive.Invalid("control 合并指令必须是数组");
            foreach (var item in names)
            {
                var name = Text(item, instruction);
                if (name is "_obj_cover" or "_arr_add" || source[name] is not { } value
                    || instruction == "_arr_add" && value is not JsonArray)
                    throw RoutePackageArchive.Invalid("control 合并目标不存在或类型错误");
            }
        }
        foreach (var pair in source)
            if (pair.Value is JsonObject child) ValidateMergeInstructions(child);
    }

    private static void Merge(JsonObject source, JsonObject target)
    {
        var handled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instruction in new[] { "_obj_cover", "_arr_add" })
        {
            if (source[instruction] is null) continue;
            if (source[instruction] is not JsonArray names) throw RoutePackageArchive.Invalid("control 合并指令必须是数组");
            handled.Add(instruction);
            foreach (var nameNode in names)
            {
                var name = Text(nameNode, instruction);
                if (source[name] is not { } value) throw RoutePackageArchive.Invalid("control 合并目标不存在");
                if (instruction == "_arr_add")
                {
                    if (value is not JsonArray array) throw RoutePackageArchive.Invalid("control 数组合并类型错误");
                    var merged = target[name] is JsonArray old ? (JsonArray)old.DeepClone() : new JsonArray();
                    foreach (var element in array)
                        if (!merged.Any(x => JsonNode.DeepEquals(x, element))) merged.Add(element?.DeepClone());
                    target[name] = merged;
                }
                else target[name] = value.DeepClone();
                handled.Add(name);
            }
        }
        foreach (var pair in source)
        {
            if (handled.Contains(pair.Key)) continue;
            if (pair.Value is JsonObject obj && target[pair.Key] is JsonObject prior) Merge(obj, prior);
            else target[pair.Key] = pair.Value?.DeepClone();
        }
    }

    // JSON5 controls use comments, trailing commas, single strings, unquoted keys and hex/signed/fraction numbers.
    // Normalize those lexically, never with regexes which could rewrite strings/ref paths.
    private static string NormalizeJson5(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            { while (i < text.Length && text[i] != '\n') i++; result.Append('\n'); continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw RoutePackageArchive.Invalid("control 注释未结束");
                i = end + 2; result.Append(' '); continue;
            }
            if (c is '\'' or '"')
            {
                var quote = c; i++;
                var value = new StringBuilder();
                var closed = false;
                while (i < text.Length)
                {
                    c = text[i++];
                    if (c == quote) { closed = true; break; }
                    if (c == '\\')
                    {
                        if (i == text.Length) throw RoutePackageArchive.Invalid("control 转义未结束");
                        c = text[i++];
                        if (c == '\n') continue;
                        if (c == '\r') { if (i < text.Length && text[i] == '\n') i++; continue; }
                        if (c is 'u' or 'x')
                        {
                            var length = c == 'u' ? 4 : 2;
                            if (i + length > text.Length || !ushort.TryParse(text.AsSpan(i, length), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var code)) throw RoutePackageArchive.Invalid("control Unicode 转义无效");
                            value.Append((char)code); i += length; continue;
                        }
                        value.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', 'v' => '\v', '0' => '\0', _ => c });
                    }
                    else value.Append(c);
                }
                if (!closed) throw RoutePackageArchive.Invalid("control 字符串未结束");
                result.Append(JsonSerializer.Serialize(value.ToString())); continue;
            }
            if (c is >= '0' and <= '9' or '+' or '-' or '.')
            {
                var start = i++;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && ",]}:/".IndexOf(text[i]) < 0) i++;
                var number = text[start..i];
                var negative = number.StartsWith("-", StringComparison.Ordinal);
                var unsigned = number.Length > 0 && (number[0] is '+' or '-') ? number[1..] : number;
                if (unsigned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (!ulong.TryParse(unsigned.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var integer)) throw RoutePackageArchive.Invalid("control 十六进制数无效");
                    result.Append(negative ? "-" : "").Append(integer.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    // Preserve numeric precision; normalize syntax rather than round through double.
                    if (number.StartsWith("+", StringComparison.Ordinal)) number = number[1..];
                    if (number.StartsWith(".", StringComparison.Ordinal)) number = "0" + number;
                    if (number.StartsWith("-.", StringComparison.Ordinal)) number = "-0" + number[1..];
                    var exponent = number.IndexOfAny(new[] { 'e', 'E' });
                    var mantissa = exponent < 0 ? number : number[..exponent];
                    if (mantissa.EndsWith(".", StringComparison.Ordinal))
                        number = mantissa + "0" + (exponent < 0 ? "" : number[exponent..]);
                    result.Append(number);
                }
                continue;
            }
            if (char.IsLetter(c) || c is '_' or '$')
            {
                var start = i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$')) i++;
                var word = text[start..i];
                var after = i; while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
                result.Append(after < text.Length && text[after] == ':' ? JsonSerializer.Serialize(word) : word); continue;
            }
            result.Append(c); i++;
        }
        return result.ToString();
    }
}
