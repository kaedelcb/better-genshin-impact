using System.Diagnostics;
using System.IO;
using System.Reflection;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common.SevenZip;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>更新包来源（决定列表条目的来源标签：目录/指定/网络）。</summary>
public enum BgiPackageSource
{
    /// <summary>安装包目录扫描出来的。</summary>
    Directory = 0,
    /// <summary>用户"选择指定压缩包…"手动挑的（可能在任意目录）。</summary>
    PickedManually = 1,
    /// <summary>（预留）从服务器下载的。下载功能后做，先占位统一来源标签体系。</summary>
    Network = 2,
}

/// <summary>扫描出的合法茶包版 BGI 更新包（文件名已通过 BgiUpdateDecisions 校验）。
/// Source 决定列表条目的来源标签；手动指定的可能不在安装包目录内，条目里会额外显示其所在目录。</summary>
public sealed record BgiUpdatePackage(
    string FileName,
    string FullPath,
    long LengthBytes,
    DateTime ModifiedTime,
    BgiArchiveName NameInfo,
    BgiPackageSource Source = BgiPackageSource.Directory)
{
    public string SizeText => LengthBytes switch
    {
        <= 0 => "大小未知",
        >= 1L << 30 => $"{LengthBytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{LengthBytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{LengthBytes / (double)(1L << 10):F0} KB",
        _ => $"{LengthBytes} B"
    };

    /// <summary>包所在目录（手动指定的包在列表里展示用）。</summary>
    public string DirectoryText => Path.GetDirectoryName(FullPath) ?? "";

    /// <summary>来源标签文本（与 BgiPackageSource 一一对应；新来源在此登记文案）。</summary>
    public string SourceTagText => Source switch
    {
        BgiPackageSource.PickedManually => "指定",
        BgiPackageSource.Network => "网络",
        _ => "目录",
    };
}

/// <summary>解压覆盖结果。SkippedFiles 里的条目已带跳过原因；Error 非空 = 整体失败（备份已完成则 BackupDir 仍有效）。</summary>
public sealed record BgiUpdateApplyResult(
    bool Success,
    int ExtractedFiles,
    int RenamedAside,
    int ExcludedFiles,
    List<string> SkippedFiles,
    string? BackupDir,
    string? Error)
{
    public static BgiUpdateApplyResult Fail(string error, string? backupDir = null)
        => new(false, 0, 0, 0, [], backupDir, error);
}

/// <summary>
/// BGI 更新包扫描与解压覆盖（"更新BGI"弹窗后端）。
/// 扫描：目录顶层 *.7z，经 <see cref="BgiUpdateDecisions"/> 校验文件名后按名称排序返回（只列合法包）。
/// 解压：整包覆盖到 BGI 目录——
///   ① 解压前对包名再校验一道（不合法直接拒绝解压，与扫描双保险）；
///   ② 条目相对路径做逃逸防护（7z 版 zip-slip）；
///   ③ 排除目录（用户选定不覆盖的目录，相对 BGI 目录）：可选先整目录备份到
///      BGI\_update_backup\时间戳\，解压时条目一律跳过，绝不触碰；
///      备份目录 _update_backup 自身无条件视为排除目录（硬保护，包内条目永不覆盖备份）；
///   ④ 0 字节条目（7z 空文件无数据流，OpenEntryStream 会抛
///      "File does not have a stream"）直接落一个空文件，不再中断整个更新；
///   ⑤ 文件被占用（助手部署在 BGI 目录内时，包内助手自身文件必被占用）先"改名腾位"再覆盖，
///      仍失败则跳过并记录——助手旧映像继续运行，新文件重启助手后生效。
///   ⑥ 7z 固实包单遍解码：逐条目 OpenEntryStream 会从固实块头重解（O(n²)，526MB 实包需数小时），
///      经反射取共享解码流按序读出（实包 3568 条目 34s）；反射失效自动回退逐条目慢路径。
/// 调用方职责：解压前确保 BGI 已退出（弹窗内经用户确认后关闭），本类不杀进程。
/// </summary>
public static class BgiPackageUpdateService
{
    /// <summary>备份根目录名（固定放 BGI 目录下，时间戳子目录每次更新独立一份）。</summary>
    public const string BackupRootDirName = "_update_backup";

    /// <summary>
    /// 扫描目录顶层（不递归）的合法 .7z 更新包，并合并持久化的"指定压缩包"（用户手动挑的、可能在任意目录）。
    /// 指定包回列表的条件：文件仍存在且名字仍合法；与扫描结果同路径的以扫描条目为准（不重复、不加标记）。
    /// 结果按版本从新到旧排序（大版本 → lcb → 尾段修复号）。
    /// </summary>
    public static List<BgiUpdatePackage> ScanPackages(string? dir, IEnumerable<string>? pinnedPaths = null)
    {
        var list = new List<BgiUpdatePackage>();
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.7z", SearchOption.TopDirectoryOnly))
            {
                if (!BgiUpdateDecisions.TryParse(Path.GetFileName(file), out var info) || info is null) continue;
                var fi = new FileInfo(file);
                list.Add(new BgiUpdatePackage(fi.Name, fi.FullName, fi.Length, fi.LastWriteTime, info));
            }
        }

        if (pinnedPaths != null)
        {
            var scannedPaths = list.Select(p => p.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in pinnedPaths)
            {
                if (string.IsNullOrWhiteSpace(path) || scannedPaths.Contains(path)) continue;
                if (!File.Exists(path)) continue; // 文件已被移动/删除：自动剔除
                if (!BgiUpdateDecisions.TryParse(Path.GetFileName(path), out var info) || info is null) continue; // 名字失效：自动剔除
                var fi = new FileInfo(path);
                list.Add(new BgiUpdatePackage(fi.Name, fi.FullName, fi.Length, fi.LastWriteTime, info, BgiPackageSource.PickedManually));
            }
        }

        return list
            .OrderByDescending(p => p.NameInfo, Comparer<BgiArchiveName>.Create(BgiUpdateDecisions.CompareNewer))
            .ToList();
    }

    /// <summary>BGI 目录下的一级子目录名（按名称排序；不含备份目录自身）。供排除目录浏览对话框设置初始位置等场景。</summary>
    public static List<string> ListFirstLevelSubDirs(string? bgiDir)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(bgiDir) || !Directory.Exists(bgiDir)) return list;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(bgiDir, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (name.Length == 0 || string.Equals(name, BackupRootDirName, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(name);
            }
        }
        catch
        {
            // 目录枚举失败（权限/占用）：返回已收集部分即可，不影响弹窗其余功能
        }
        return list.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static Task<BgiUpdateApplyResult> ApplyAsync(string archivePath, string targetDir,
        IReadOnlyList<string> excludeDirs, bool backupExcludedDirs,
        IProgress<string>? progress, CancellationToken ct = default)
        => Task.Run(() => Apply(archivePath, targetDir, excludeDirs, backupExcludedDirs, progress, ct), ct);

    public static BgiUpdateApplyResult Apply(string archivePath, string targetDir,
        IReadOnlyList<string> excludeDirs, bool backupExcludedDirs,
        IProgress<string>? progress, CancellationToken ct = default)
    {
        // ① 解压前再校验包名：不合法绝不解压覆盖（需求硬约束）。
        // 只允许茶包版：名称必须含 +lcb. 段，防止拿官方版等非茶包包误升级覆盖（2026-09-14 明确）
        if (!BgiUpdateDecisions.TryParse(Path.GetFileName(archivePath), out _))
            return BgiUpdateApplyResult.Fail("安装包文件名不合法（仅允许茶包版：名称必须含 +lcb. 段，如 BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z），已拒绝解压覆盖");
        if (!File.Exists(archivePath)) return BgiUpdateApplyResult.Fail("安装包文件不存在");
        if (string.IsNullOrWhiteSpace(targetDir)) return BgiUpdateApplyResult.Fail("BGI 目录未设置");

        var targetRoot = Path.GetFullPath(targetDir);
        if (!targetRoot.EndsWith(Path.DirectorySeparatorChar)) targetRoot += Path.DirectorySeparatorChar;
        Directory.CreateDirectory(targetRoot);

        var excludes = excludeDirs ?? [];
        // 备份目录硬保护：_update_backup 下的包内条目一律跳过，绝不覆盖已有备份（与用户排除配置无关）
        var extractExcludes = excludes.Append(BackupRootDirName).ToArray();
        string? backupDir = null;
        try
        {
            if (excludes.Count > 0 && backupExcludedDirs)
                backupDir = BackupExcludedDirs(targetRoot, excludes, progress);
        }
        catch (Exception ex)
        {
            // 备份失败 = 安全网缺失，宁可中止也不覆盖
            return BgiUpdateApplyResult.Fail($"更新前备份排除目录失败（已中止，未做任何覆盖）：{ex.Message}");
        }

        int extracted = 0, renamedAside = 0, excluded = 0;
        var skipped = new List<string>();
        try
        {
            using var archive = SevenZipArchive.Open(archivePath);
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            int total = entries.Count, done = 0;
            // 固实单遍解码器（可能为 null = 反射失效，回退逐条目慢路径）
            using var solid = SolidSevenZipReader.TryCreate(entries.FirstOrDefault());
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                progress?.Report($"({done}/{total}) {entry.Key}");
                if (entry.Key is null || NormalizeEntryPath(entry.Key) is not { } rel)
                {
                    skipped.Add($"{entry.Key}（条目路径不安全，已跳过）");
                    DrainEntry(solid, entry);
                    continue;
                }
                var fullTarget = Path.GetFullPath(Path.Combine(targetRoot, rel));
                if (!fullTarget.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add($"{entry.Key}（条目路径逃逸目标目录，已跳过）");
                    DrainEntry(solid, entry);
                    continue;
                }
                if (BgiUpdateDecisions.IsExcludedPath(rel, extractExcludes))
                {
                    excluded++;
                    DrainEntry(solid, entry);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullTarget)!);
                try
                {
                    WriteEntry(entry, fullTarget, solid);
                    extracted++;
                }
                catch (IOException) when (File.Exists(fullTarget))
                {
                    // ⑤ 被占用（助手/BGI 自身文件）：先改名腾位再写一次；仍失败则跳过并记录
                    try
                    {
                        File.Move(fullTarget, fullTarget + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"));
                        renamedAside++;
                        WriteEntry(entry, fullTarget, solid);
                        extracted++;
                    }
                    catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
                    {
                        skipped.Add($"{Path.GetFileName(fullTarget)}（文件被占用）");
                        DrainEntry(solid, entry);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    skipped.Add($"{Path.GetFileName(fullTarget)}（无写入权限）");
                    DrainEntry(solid, entry);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BgiUpdateApplyResult(false, extracted, renamedAside, excluded, skipped, backupDir, $"解压失败：{ex.Message}");
        }
        return new BgiUpdateApplyResult(true, extracted, renamedAside, excluded, skipped, backupDir, null);
    }

    /// <summary>固实单遍模式下，被跳过/排除的条目也要把数据从共享解码流里读掉，保持流与条目同步。</summary>
    private static void DrainEntry(SolidSevenZipReader? solid, IArchiveEntry entry)
    {
        if (solid is null || entry.Size == 0) return;
        var stream = solid.Next(entry);
        var buf = new byte[1 << 16];
        long remain = entry.Size;
        while (remain > 0)
        {
            var r = stream.Read(buf, 0, (int)Math.Min(buf.Length, remain));
            if (r <= 0) return; // 流提前结束：后续条目解码会报错并整体失败，此处不再处理
            remain -= r;
        }
    }

    /// <summary>把存在的排除目录整目录复制到 BGI\_update_backup\时间戳\ 下（相对结构原样保留）。</summary>
    private static string BackupExcludedDirs(string targetRoot, IReadOnlyList<string> excludes, IProgress<string>? progress)
    {
        var backupDir = Path.Combine(targetRoot, BackupRootDirName, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backupDir);
        foreach (var raw in excludes)
        {
            progress?.Report($"备份排除目录 {raw}");
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var rel = raw.Trim().Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
            // GetFullPath + 前缀校验：手滑填 ".."/绝对路径时源目录不在 BGI 内，直接跳过该目录
            var src = Path.GetFullPath(Path.Combine(targetRoot, rel));
            if (!src.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Directory.Exists(src)) continue; // 目录不存在无需备份
            CopyDirectory(src, Path.Combine(backupDir, rel));
        }
        return backupDir;
    }

    private static void CopyDirectory(string srcDir, string dstDir)
    {
        Directory.CreateDirectory(dstDir);
        foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(dstDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var sub in Directory.EnumerateDirectories(srcDir, "*", SearchOption.TopDirectoryOnly))
        {
            CopyDirectory(sub, Path.Combine(dstDir, Path.GetFileName(sub.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
        }
    }

    /// <summary>写条目数据：0 字节条目（7z 空文件无数据流，OpenEntryStream 会抛
    /// InvalidOperationException "File does not have a stream."）直接落一个空文件。
    /// solid 非空时从固实共享解码流按序读（先建好目标文件再读流，File.Create 失败不消费流数据，
    /// 改名腾位重试安全）；解码流中途失败抛 InvalidDataException（不触发改名腾位重试，防流失步）。
    /// solid 为 null 回退逐条目 OpenEntryStream（正确但固实包慢）。</summary>
    internal static void WriteEntry(IArchiveEntry entry, string targetPath, SolidSevenZipReader? solid = null)
    {
        if (entry.Size == 0)
        {
            File.Create(targetPath).Dispose();
            return;
        }
        using var output = File.Create(targetPath);
        if (solid is null)
        {
            using var entryStream = entry.OpenEntryStream();
            entryStream.CopyTo(output);
            return;
        }
        try
        {
            var stream = solid.Next(entry);
            var buf = new byte[1 << 20];
            long remain = entry.Size;
            while (remain > 0)
            {
                int r = stream.Read(buf, 0, (int)Math.Min(buf.Length, remain));
                if (r <= 0) throw new InvalidDataException($"固实解码流提前结束：{entry.Key}");
                output.Write(buf, 0, r);
                remain -= r;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // 读流中途失败 = 固实流位置已不可靠：转成非 IOException，避免外层误走"改名腾位"重试造成失步
            throw new InvalidDataException($"条目 {entry.Key} 从固实解码流读取失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 7z 固实块共享解码流（反射适配 SharpCompress 0.38.0 内部结构）。
    /// 7z 把多个文件打包进一个固实块，SharpCompress 公开 API 只能逐条目从块头重解并跳过
    /// （O(n²)，实测 526MB 实包需数小时）；本类对同一固实块只建一次解码流，条目按
    /// archive.Entries 顺序消费即可顺序读出各自数据（实测同包 34s）。
    /// 任一内部成员反射失败返回 null（调用方回退逐条目路径）；条目必须按 Entries 顺序消费。
    /// </summary>
    internal sealed class SolidSevenZipReader : IDisposable
    {
        private readonly PropertyInfo _filePartProp;
        private readonly PropertyInfo _folderProp;
        private readonly FieldInfo _dbField;
        private readonly FieldInfo _streamField;
        private readonly MethodInfo _getFolderStream;
        private readonly object? _passwordProvider;
        private Stream? _current;
        private object? _currentFolder;

        private SolidSevenZipReader(PropertyInfo filePartProp, PropertyInfo folderProp,
            FieldInfo dbField, FieldInfo streamField, MethodInfo getFolderStream,
            object? passwordProvider)
        {
            _filePartProp = filePartProp;
            _folderProp = folderProp;
            _dbField = dbField;
            _streamField = streamField;
            _getFolderStream = getFolderStream;
            _passwordProvider = passwordProvider;
        }

        /// <summary>解析反射成员；任一缺失（SharpCompress 内部结构变化）返回 null。</summary>
        public static SolidSevenZipReader? TryCreate(IArchiveEntry? anyEntry)
        {
            try
            {
                if (anyEntry is null) return null;
                var bf = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                var filePartProp = typeof(SevenZipEntry).GetProperty("FilePart", bf);
                var part = filePartProp?.GetValue(anyEntry);
                if (filePartProp is null || part is null) return null;
                var partType = part.GetType();
                var folderProp = partType.GetProperty("Folder", bf);
                var dbField = partType.GetField("_database", bf);
                var streamField = partType.GetField("_stream", bf);
                var getFolderStream = dbField?.FieldType.GetMethod("GetFolderStream", bf);
                if (folderProp is null || dbField is null || streamField is null || getFolderStream is null) return null;
                var db = dbField.GetValue(part)!;
                var passwordProvider = db.GetType().GetProperty("PasswordProvider", bf)?.GetValue(db)
                    ?? db.GetType().GetField("PasswordProvider", bf)?.GetValue(db);
                return new SolidSevenZipReader(filePartProp, folderProp, dbField, streamField, getFolderStream, passwordProvider);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>返回可顺序读出当前条目数据的流（条目 Size==0 无数据，调用方不应调用）。
        /// 条目消费顺序必须与 archive.Entries 一致，否则数据错位。</summary>
        public Stream Next(IArchiveEntry entry)
        {
            var part = _filePartProp.GetValue(entry)!;
            var folder = _folderProp.GetValue(part);
            if (!ReferenceEquals(folder, _currentFolder))
            {
                _current?.Dispose();
                var db = _dbField.GetValue(part)!;
                var stream = _streamField.GetValue(part)!;
                _current = (Stream)(_getFolderStream.Invoke(db, new[] { stream, folder, _passwordProvider })
                    ?? throw new InvalidDataException("固实解码流创建失败"));
                _currentFolder = folder;
            }
            return _current!;
        }

        public void Dispose() => _current?.Dispose();
    }

    /// <summary>统一 7z 条目相对路径：'/' 分隔、剥根前缀、拒盘符与 ".." 段。无法安全归一化返回 null。</summary>
    private static string? NormalizeEntryPath(string key)
    {
        var p = key.Trim().Replace('\\', '/').TrimStart('/');
        if (p.Contains(':')) return null;
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Contains("..")) return null;
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    /// <summary>
    /// 找目标目录下正在运行的 BGI 进程（按进程名 BetterGI + 主模块路径前缀匹配）。
    /// 主模块路径读不到（权限/时序）的实例保守计入。返回的进程由调用方在用户确认后 Kill。
    /// </summary>
    public static List<Process> FindBgiProcessesUnder(string targetDir)
    {
        var result = new List<Process>();
        if (string.IsNullOrWhiteSpace(targetDir)) return result;
        string root;
        try
        {
            root = Path.GetFullPath(targetDir);
            if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        }
        catch
        {
            return result;
        }

        foreach (var proc in Process.GetProcessesByName("BetterGI"))
        {
            var underTarget = true;
            try
            {
                var exePath = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    underTarget = exePath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 路径不可读（跨会话/权限）：保守视为目标目录下，避免漏杀导致覆盖失败
            }
            if (underTarget) result.Add(proc);
            else proc.Dispose();
        }
        return result;
    }
}
