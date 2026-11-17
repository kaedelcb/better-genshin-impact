using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NexusBGI.RoutePackages;

/// <summary>Untrusted single-ZIP boundary shared by server and desktop. No platform/UI dependencies.</summary>
public static class RoutePackageArchive
{
    public const long MaxArchiveBytes = 128L * 1024 * 1024;
    public const long MaxExpandedBytes = 512L * 1024 * 1024;
    public const int MaxFiles = 10000;
    private const int MaxFileBytes = 16 * 1024 * 1024;

    public static bool IsValidId(string? id) => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static async Task<List<RoutePackageFile>> ValidateAsync(string zipPath, CancellationToken ct = default)
    {
        await using var file = Open(zipPath);
        return await ValidateCoreAsync(file, ct).ConfigureAwait(false);
    }

    public static async Task ExtractAsync(string zipPath, string destination, CancellationToken ct = default)
    {
        await using var file = Open(zipPath);
        await ValidateCoreAsync(file, ct).ConfigureAwait(false);
        var root = Path.GetFullPath(destination);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("解压目标必须是尚不存在的独占目录。");
        CheckAncestors(Path.GetDirectoryName(root)!);
        Directory.CreateDirectory(root);
        // The caller owns this directory, including cleanup on cancellation. Never overwrite an existing file.
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, true, Encoding.UTF8);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Invalid("路径越界");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CheckAncestors(Path.GetDirectoryName(target)!);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var input = entry.Open();
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
        }
    }

    private static FileStream Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (file.Length is < 22 or > MaxArchiveBytes) { file.Dispose(); throw Invalid("ZIP 大小超限或为空"); }
        return file;
    }

    private static async Task<List<RoutePackageFile>> ValidateCoreAsync(FileStream file, CancellationToken ct)
    {
        var checksums = CheckHeaders(file);
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, true, Encoding.UTF8);
        if (zip.Entries.Count is < 1 or > MaxFiles) throw Invalid("ZIP 条目数超限");
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var files = new List<RoutePackageFile>();
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            var path = directory ? entry.FullName[..^1] : entry.FullName;
            ValidatePath(path);
            var parts = path.Split('/');
            if (parts[0] is not ("builtin" or "pathing") || (!directory && parts.Length < (parts[0] == "builtin" ? 3 : 2)))
                throw Invalid("仅允许 builtin/<线路目录> 和 pathing 下的线路");
            if (!normalizedPaths.Add(path.Normalize(NormalizationForm.FormC))) throw Invalid("重复路径或大小写/Unicode 碰撞: " + path);
            // Keep original spelling too, so implicit parent aliases cannot disappear during normalization.
            paths[path] = directory;
            var mode = (entry.ExternalAttributes >> 16) & 0xf000;
            if (mode != 0 && mode != (directory ? 0x4000 : 0x8000) || (entry.ExternalAttributes & 0x400) != 0)
                throw Invalid("不允许链接或特殊文件");
            if (directory)
            {
                await using var directoryInput = entry.Open();
                if (entry.Length != 0 || checksums[entry.FullName] != 0
                    || await directoryInput.ReadAsync(new byte[1], ct).ConfigureAwait(false) != 0) throw Invalid("目录条目含数据");
                continue;
            }
            var variantDescription = parts[^1] == "变体说明.txt" && parts.Length >= 4
                && parts[^2] is "A变体" or "B变体" or "C变体" or "D变体";
            if (!(path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || parts[^1] == "control.json5" || variantDescription)) throw Invalid("不允许非线路文件: " + path);
            var fileLimit = variantDescription ? 16384 : MaxFileBytes;
            if (entry.Length < 0 || entry.Length > fileLimit || entry.Length == 0 && !variantDescription) throw Invalid("单文件为空或超限");
            await using var input = entry.Open();
            using var bytes = new MemoryStream();
            var buffer = new byte[65536];
            int count;
            uint crc = 0xffffffff;
            while ((count = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
            {
                expanded += count;
                if (expanded > MaxExpandedBytes || bytes.Length + count > fileLimit) throw Invalid("实际解压字节超限");
                for (var i = 0; i < count; i++) crc = CrcTable[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
                bytes.Write(buffer, 0, count);
            }
            if (bytes.Length != entry.Length || (crc ^ 0xffffffff) != checksums[entry.FullName]) throw Invalid("ZIP 长度/CRC 不一致");
            var content = bytes.ToArray();
            if (variantDescription)
            {
                if (content.Length > 16384) throw Invalid("变体说明文本超限");
                var description = new UTF8Encoding(false, true).GetString(content);
                if (description.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) throw Invalid("变体说明须为普通 UTF-8 文本");
            }
            else nodes.Add(path, RoutePackageJson.Parse(content, parts[^1] == "control.json5"));
            files.Add(new RoutePackageFile { Path = path, SizeBytes = content.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant() });
        }
        // Check implicit parents as well: Linux ZIPs must not create Windows file/directory aliases.
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in paths)
        {
            var original = item.Key;
            var segments = original.Split('/');
            for (var i = 1; i <= segments.Length; i++)
            {
                var prefix = string.Join("/", segments.Take(i));
                var normalized = prefix.Normalize(NormalizationForm.FormC);
                if (canonical.TryGetValue(normalized, out var prior) && prior != prefix) throw Invalid("目录大小写/Unicode 碰撞");
                canonical[normalized] = prefix;
                if (i < segments.Length && paths.TryGetValue(prefix, out var isDirectory) && !isDirectory) throw Invalid("文件占用目录路径");
            }
        }
        if (!nodes.Keys.Any(p => p.StartsWith("builtin/", StringComparison.Ordinal) && !IsControl(p))) throw Invalid("builtin 必须包含有效线路");
        RoutePackageJson.ValidateRoutes(nodes);
        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    internal static bool IsControl(string path) => path.Split('/')[^1] == "control.json5";

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 512 || path.Contains('\\') || path.StartsWith('/') || path.Contains(':')) throw Invalid("非法相对路径");
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length is < 1 or > 180 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.Any(c => char.IsControl(c) || "<>\"|?*".Contains(c))) throw Invalid("非法路径段");
            var stem = segment.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³'))) throw Invalid("Windows 保留名称");
        }
    }

    private static void CheckAncestors(string path)
    {
        for (var p = new DirectoryInfo(path); p != null; p = p.Parent)
            if (p.Exists && (p.Attributes & FileAttributes.ReparsePoint) != 0) throw Invalid("解压路径含链接");
    }

    // Read the central and local flags explicitly: ZipArchive doesn't expose encryption/link/encoding flags.
    private static Dictionary<string, uint> CheckHeaders(FileStream file)
    {
        using var reader = new BinaryReader(file, Encoding.UTF8, true);
        var size = (int)Math.Min(file.Length, 65557);
        file.Position = file.Length - size;
        var end = reader.ReadBytes(size);
        var eocd = -1;
        for (var i = size - 22; i >= 0; i--)
            if (BitConverter.ToUInt32(end, i) == 0x06054b50 && i + 22 + BitConverter.ToUInt16(end, i + 20) == size) { eocd = i; break; }
        if (eocd < 0 || BitConverter.ToUInt16(end, eocd + 4) != 0 || BitConverter.ToUInt16(end, eocd + 6) != 0) throw Invalid("不支持分卷 ZIP");
        var entries = BitConverter.ToUInt16(end, eocd + 10);
        var centralSize = BitConverter.ToUInt32(end, eocd + 12);
        var offset = BitConverter.ToUInt32(end, eocd + 16);
        if (entries is 0 or > MaxFiles || entries != BitConverter.ToUInt16(end, eocd + 8)
            || (long)offset + centralSize != file.Length - size + eocd) throw Invalid("ZIP 目录无效（不支持 ZIP64）");
        var result = new Dictionary<string, uint>(StringComparer.Ordinal);
        file.Position = offset;
        var ranges = new List<(long Start, long End)>();
        for (var i = 0; i < entries; i++)
        {
            if (reader.ReadUInt32() != 0x02014b50) throw Invalid("ZIP 中央目录无效");
            reader.ReadUInt16(); reader.ReadUInt16();
            var flags = reader.ReadUInt16(); var method = reader.ReadUInt16();
            if ((flags & ~0x080e) != 0 || method is not (0 or 8)) throw Invalid("仅支持未加密 STORE/DEFLATE");
            reader.ReadUInt32(); var crc = reader.ReadUInt32();
            var compressedBytes = reader.ReadUInt32(); var expandedBytes = reader.ReadUInt32();
            if (expandedBytes > MaxFileBytes || method == 0 && compressedBytes != expandedBytes) throw Invalid("ZIP 长度超限或 STORE 长度错误");
            var nameSize = reader.ReadUInt16(); var extra = reader.ReadUInt16(); var comment = reader.ReadUInt16();
            if (reader.ReadUInt16() != 0) throw Invalid("分卷 ZIP");
            reader.ReadUInt16(); reader.ReadUInt32(); var local = reader.ReadUInt32();
            var nameBytes = reader.ReadBytes(nameSize);
            if ((flags & 0x800) == 0 && nameBytes.Any(b => b > 127)) throw Invalid("中文路径必须使用 UTF-8");
            var name = new UTF8Encoding(false, true).GetString(nameBytes);
            if (!result.TryAdd(name, crc)) throw Invalid("重复 ZIP 条目");
            var next = file.Position + extra + comment;
            if (next > offset + centralSize || local >= offset) throw Invalid("ZIP 偏移无效");
            file.Position = local;
            if (reader.ReadUInt32() != 0x04034b50) throw Invalid("ZIP 本地头无效");
            reader.ReadUInt16();
            if (reader.ReadUInt16() != flags || reader.ReadUInt16() != method) throw Invalid("ZIP 头标志不一致");
            reader.ReadUInt32();
            var localCrc = reader.ReadUInt32(); var localCompressed = reader.ReadUInt32(); var localExpanded = reader.ReadUInt32();
            if ((flags & 8) == 0 && (localCrc != crc || localCompressed != compressedBytes || localExpanded != expandedBytes))
                throw Invalid("ZIP 本地长度/CRC 与中央目录不一致");
            var localName = reader.ReadUInt16(); var localExtra = reader.ReadUInt16();
            if (localName != nameSize || !reader.ReadBytes(localName).SequenceEqual(nameBytes) || file.Position + localExtra + compressedBytes > offset) throw Invalid("ZIP 路径头或数据边界不一致");
            ranges.Add((local, file.Position + localExtra + compressedBytes));
            file.Position = next;
        }
        if (file.Position != offset + centralSize) throw Invalid("ZIP 目录长度不一致");
        var ordered = ranges.OrderBy(r => r.Start).ToArray();
        for (var i = 1; i < ordered.Length; i++)
            if (ordered[i].Start < ordered[i - 1].End) throw Invalid("ZIP 数据条目重叠");
        return result;
    }

    internal static InvalidDataException Invalid(string message) => new("线路包校验失败：" + message);
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();
}
