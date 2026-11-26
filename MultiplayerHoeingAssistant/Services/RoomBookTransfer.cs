using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

internal sealed record RoomBookEntry(string Name, string Note, string ServerUrl, string RoomCode,
    IReadOnlyList<string> TeamUids, int ExpectedHoeingPlayers);

internal sealed record RoomBookMergeResult(int ImportedCount, int SkippedCount, IReadOnlyList<AssistantRoomProfile> Profiles);

/// <summary>桌面和网页共用的房间簿剪贴板格式，只包含房间元数据。</summary>
internal static class RoomBookTransfer
{
    internal const int MaxProfiles = 64;
    internal const int MaxUtf8Bytes = 256 * 1024;
    private const string Kind = "nexusbgi-room-book";
    private const int Version = 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private sealed record RoomBookDocument(string Kind, int Version, IReadOnlyList<RoomBookEntry> Rooms);

    internal static string Export(AssistConfig config)
    {
        CheckCount(config.RoomProfiles.Count);
        var active = config.ActiveRoomProfile;
        var entries = config.RoomProfiles.Select(profile =>
        {
            if (profile is null) throw new InvalidDataException("本机房间档案不完整，请先核对。");
            var isActive = ReferenceEquals(active, profile);
            return Validate(new RoomBookEntry(profile.Name, profile.Note ?? "", EffectiveServer(config, profile),
                profile.RoomCode, (isActive ? config.TeamUids : profile.TeamUids) ?? [],
                isActive ? config.ExpectedHoeingPlayers : profile.ExpectedHoeingPlayers));
        }).ToArray();
        var text = JsonSerializer.Serialize(new RoomBookDocument(Kind, Version, entries), WriteOptions);
        CheckSize(text);
        return text;
    }

    internal static IReadOnlyList<RoomBookEntry> Parse(string text)
    {
        if (text is null) throw new InvalidDataException("请粘贴有效的房间簿 JSON。");
        CheckSize(text);
        try
        {
            using var document = JsonDocument.Parse(text);
            var book = ReadFields(document.RootElement, "kind", "version", "rooms");
            if (ReadString(book["kind"]) != Kind || !IsNumber(book["version"], Version, Version, out _))
                throw new InvalidDataException("房间簿类型或版本不受支持。");
            var rooms = book["rooms"];
            if (rooms.ValueKind != JsonValueKind.Array) throw new InvalidDataException("房间簿列表格式无效。");
            CheckCount(rooms.GetArrayLength());
            var entries = new List<RoomBookEntry>();
            foreach (var room in rooms.EnumerateArray())
            {
                var fields = ReadFields(room, "name", "note", "serverUrl", "roomCode", "teamUids", "expectedHoeingPlayers");
                var uids = fields["teamUids"];
                if (uids.ValueKind != JsonValueKind.Array || uids.GetArrayLength() > MaxProfiles)
                    throw new InvalidDataException("成员 UID 列表格式无效或人数超过 64。");
                if (!IsNumber(fields["expectedHoeingPlayers"], 1, 4, out var expected))
                    throw new InvalidDataException("预期开锄人数必须为 1–4 的整数。");
                entries.Add(Validate(new RoomBookEntry(ReadString(fields["name"]), ReadString(fields["note"]),
                    ReadString(fields["serverUrl"]), ReadString(fields["roomCode"]),
                    uids.EnumerateArray().Select(ReadString).ToArray(), expected)));
            }
            return entries;
        }
        catch (JsonException)
        {
            throw new InvalidDataException("剪贴板内容不是有效的房间簿 JSON。");
        }
        catch (InvalidOperationException)
        {
            throw new InvalidDataException("房间簿文本字段格式无效。");
        }
    }

    internal static RoomBookMergeResult Merge(AssistConfig config, string text,
        Func<IReadOnlyList<AssistantRoomProfile>, bool> persist)
    {
        var entries = Parse(text);
        if (config.ActiveRoomProfile is null && !string.IsNullOrEmpty(config.EffectiveRoomCode))
            throw new InvalidOperationException("原有房间档案尚未接续，请重新打开助手核对；本次未合并。");
        var original = config.RoomProfiles;
        var names = new HashSet<string>(original.Where(p => p is not null).Select(p => p.Name?.Trim() ?? ""), StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(original.Where(p => p is not null).Select(p => p.Id), StringComparer.Ordinal);
        var known = new HashSet<(string Server, string Code)>();
        foreach (var profile in original)
            if (profile is not null && TryNormalizeServer(EffectiveServer(config, profile), out var server))
                known.Add((server, profile.RoomCode));

        var added = new List<AssistantRoomProfile>();
        foreach (var entry in entries)
        {
            if (!known.Add((entry.ServerUrl, entry.RoomCode))) continue;
            string id;
            do { id = Guid.NewGuid().ToString("N"); } while (!ids.Add(id));
            added.Add(new AssistantRoomProfile
            {
                Id = id, Name = UniqueName(entry.Name, names), Note = entry.Note, ServerUrl = entry.ServerUrl,
                RoomCode = entry.RoomCode, PreserveRoomCode = true, TeamUids = [.. entry.TeamUids],
                ExpectedHoeingPlayers = entry.ExpectedHoeingPlayers
            });
        }
        if (added.Count == 0) return new(0, entries.Count, original.ToArray());
        if (original.Count + added.Count > MaxProfiles)
            throw new InvalidDataException($"本机最多保存 {MaxProfiles} 个房间档案，本次合并将超过上限，未导入任何房间。");

        // Hold the caller's config write gate across disk commit and publication of the new list.
        // The existing profile objects and all runtime fields are left in place, including on failure.
        List<AssistantRoomProfile> merged = [.. original, .. added];
        var result = new RoomBookMergeResult(added.Count, entries.Count - added.Count, merged.ToArray());
        if (!persist(added)) throw new IOException("房间簿未能保存，原档案保留。");
        config.RoomProfiles = merged;
        return result;
    }

    // Detect a stale room list before appending to the latest on-disk config. Other saved fields
    // stay in the file as they were, so an independent task or credential change cannot be lost.
    internal static bool SameSavedBook(AssistConfig current, AssistConfig saved)
    {
        if (current.ActiveRoomProfileId != saved.ActiveRoomProfileId || current.LocalRoomProfileId != saved.LocalRoomProfileId
            || current.PlayerUid != saved.PlayerUid || current.ObserverMode != saved.ObserverMode
            || current.ClientInstanceId != saved.ClientInstanceId || current.StandaloneMode != saved.StandaloneMode
            || current.RoomProfiles.Count != saved.RoomProfiles.Count) return false;
        for (var i = 0; i < current.RoomProfiles.Count; i++)
        {
            var local = current.RoomProfiles[i];
            var disk = saved.RoomProfiles[i];
            if (local is null || disk is null || local.Id != disk.Id || local.Name != disk.Name || local.RoomCode != disk.RoomCode
                || !RoomServerAddress.Same(EffectiveServer(current, local), EffectiveServer(saved, disk))) return false;
        }
        return true;
    }

    internal static bool IsValidRoomCode(string? code) => code is { Length: >= 1 and <= 128 }
        && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    internal static bool UsesExactRoomCode(AssistantRoomProfile? profile) => profile is not null && IsValidRoomCode(profile.RoomCode)
        && (profile.PreserveRoomCode || profile.RoomCode.Length != 6
            || profile.RoomCode.Any(c => !char.IsAsciiDigit(c) && !char.IsAsciiLetterUpper(c)));

    private static string EffectiveServer(AssistConfig config, AssistantRoomProfile profile)
        => ReferenceEquals(config.ActiveRoomProfile, profile) ? config.ServerUrl : config.GetRoomServerUrl(profile);

    private static RoomBookEntry Validate(RoomBookEntry room)
    {
        if (string.IsNullOrWhiteSpace(room.Name) || room.Name.Length > 64)
            throw new InvalidDataException("房间名称必须为 1–64 个字符。");
        if (room.Note is null || room.Note.Length > 256) throw new InvalidDataException("房间备注不能超过 256 个字符。");
        if (!TryNormalizeServer(room.ServerUrl, out var server))
            throw new InvalidDataException("房间服务器地址无效，须为不含账号、查询参数或片段的 HTTP 或 HTTPS 地址。");
        if (!IsValidRoomCode(room.RoomCode))
            throw new InvalidDataException("房间号须为 1–128 位字母、数字、下划线或短横线。");
        if (room.TeamUids.Count > MaxProfiles || room.TeamUids.Any(uid => uid is not { Length: >= 1 and <= 16 } || !uid.All(char.IsAsciiDigit)))
            throw new InvalidDataException("成员 UID 须为 1–16 位数字字符串，最多 64 项。");
        if (room.ExpectedHoeingPlayers is < 1 or > 4) throw new InvalidDataException("预期开锄人数必须为 1–4 的整数。");
        // Check field strings as well as their encoded JSON to reject invalid surrogate pairs.
        CheckSize(room.Name);
        CheckSize(room.Note);
        if (room.ServerUrl is not null) CheckSize(room.ServerUrl);
        return room with { Name = room.Name.Trim(), ServerUrl = server };
    }

    private static bool TryNormalizeServer(string? address, out string server)
    {
        server = "";
        var value = address?.Trim() ?? "";
        var prefixLength = value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7
            : value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8 : 0;
        if (prefixLength == 0 || value.Length > 2048 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or '?' or '#')) return false;
        var authority = value[prefixLength..].Split('/', 2)[0];
        if (authority.Length == 0 || authority.Contains('@') || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        // Clipboard room books use the same base-address aliases as the browser room book.
        // Strip path segments, never a hostname such as http://gateway. Original profiles are not rewritten.
        var path = uri.AbsolutePath.TrimEnd('/');
        while (path.EndsWith("/gateway", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/hub", StringComparison.OrdinalIgnoreCase))
            path = path[..path.LastIndexOf('/')].TrimEnd('/');
        try
        {
            var host = uri.IdnHost;
            if (uri.HostNameType == UriHostNameType.IPv6 && !host.StartsWith('[')) host = "[" + host + "]";
            server = uri.Scheme + "://" + host + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture)) + path;
            return true;
        }
        catch (UriFormatException) { return false; }
    }

    private static string UniqueName(string name, HashSet<string> names)
    {
        if (names.Add(name)) return name;
        for (var index = 2; ; index++)
        {
            var suffix = " (" + index.ToString(CultureInfo.InvariantCulture) + ")";
            var length = Math.Min(name.Length, 64 - suffix.Length);
            if (length < name.Length && char.IsHighSurrogate(name[length - 1])) length--;
            var candidate = name[..length].TrimEnd() + suffix;
            if (names.Add(candidate)) return candidate;
        }
    }

    private static Dictionary<string, JsonElement> ReadFields(JsonElement element, params string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("房间簿对象格式无效。");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
            if (!required.Contains(field.Name, StringComparer.Ordinal) || !fields.TryAdd(field.Name, field.Value))
                throw new InvalidDataException("房间簿含有不允许或重复的字段。");
        if (fields.Count != required.Length) throw new InvalidDataException("房间簿缺少必填字段。");
        return fields;
    }

    private static string ReadString(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString()!
            : throw new InvalidDataException("房间簿文本字段必须为字符串。");

    private static bool IsNumber(JsonElement element, int min, int max, out int result)
    {
        result = 0;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var number)
            || number < min || number > max || number != Math.Truncate(number)) return false;
        result = (int)number;
        return true;
    }

    private static void CheckCount(int count)
    {
        if (count > MaxProfiles) throw new InvalidDataException($"每个房间簿最多包含 {MaxProfiles} 个房间。");
    }

    private static void CheckSize(string text)
    {
        if (text.Length > MaxUtf8Bytes) throw new InvalidDataException("房间簿超过 256 KiB 大小限制。");
        try
        {
            if (Utf8.GetByteCount(text) > MaxUtf8Bytes) throw new InvalidDataException("房间簿超过 256 KiB 大小限制。");
        }
        catch (EncoderFallbackException)
        {
            throw new InvalidDataException("房间簿包含无效的文本字符。");
        }
    }
}
