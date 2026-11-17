using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexusBGI.RoutePackages;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>
/// BGI 自有整包安装器。只写独立受管根；持久化事务负责两次目录改名之间的恢复。
/// HttpClient、数据根和精确客户端版本均可注入，网络失败/取消绝不等同安装成功。
/// </summary>
public sealed class AutoHoeingRouteUpdater(HttpClient http, string managedRoot, string clientVersion)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    private const string Owner = "BetterGI.RoutePackages.v1";
    private readonly string _root = Path.GetFullPath(managedRoot);
    public string CurrentDirectory => Path.Combine(_root, "current");

    public sealed class Lease : IDisposable, IAsyncDisposable
    {
        internal readonly string Root;
        internal readonly SemaphoreSlim Gate = new(1, 1);
        private FileStream? _file;
        internal Lease(string root, FileStream file) { Root = root; _file = file; }
        internal bool IsAlive => _file != null;
        internal bool AllowsInstall { get; private set; } = true;

        /// <summary>准备结束后永久封存；同一租约上的异步配置也不能重装执行中的包。</summary>
        public async Task SealForExecutionAsync(CancellationToken ct = default)
        {
            await Gate.WaitAsync(ct);
            try
            {
                if (!IsAlive) throw new ObjectDisposedException(nameof(Lease));
                AllowsInstall = false;
            }
            finally { Gate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await Gate.WaitAsync();
            try { _file?.Dispose(); _file = null; }
            finally { Gate.Release(); }
        }

        public void Dispose()
        {
            Gate.Wait();
            try { _file?.Dispose(); _file = null; }
            finally { Gate.Release(); }
        }
    }

    /// <summary>整个联机任务持有租约，独立更新任务/另一进程不能替换正在读取或执行的线路。</summary>
    public Lease AcquireLease()
    {
        RequireNoLinks(_root);
        Directory.CreateDirectory(_root);
        var ownerPath = Path.Combine(_root, ".managed");
        if (!File.Exists(ownerPath))
        {
            if (Directory.EnumerateFileSystemEntries(_root).Any())
                throw new IOException("线路根未标记为 BGI 受管目录，拒绝覆盖：" + _root);
            try
            {
                using var marker = new FileStream(ownerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                marker.Write(System.Text.Encoding.UTF8.GetBytes(Owner));
                marker.Flush(true);
            }
            catch (IOException) when (File.Exists(ownerPath)) { }
        }
        RequireNoLinks(ownerPath);
        if (File.ReadAllText(ownerPath) != Owner) throw new IOException("线路受管根标记无效");
        var lockPath = Path.Combine(_root, ".lock");
        RequireNoLinks(lockPath);
        try { return new Lease(_root, new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException ex) { throw new IOException("线路正在由另一个任务或 BGI 进程使用，不能更新", ex); }
    }

    public async Task<string> EnsureAsync(string serverAddress, string? packageId, CancellationToken ct,
        bool forceRepair = false, Lease? lease = null, bool allowInstall = true)
    {
        ct.ThrowIfCancellationRequested();
        var server = RequireServerUri(serverAddress);
        if (!string.IsNullOrEmpty(packageId) && !RoutePackageArchive.IsValidId(packageId))
            throw new InvalidDataException("房主线路包身份无效");
        using var owned = lease == null ? AcquireLease() : null;
        lease ??= owned!;
        if (!lease.IsAlive || !string.Equals(lease.Root, _root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("线路租约不属于本受管根或已经释放");
        await lease.Gate.WaitAsync(ct);
        try
        {
            if (!lease.IsAlive) throw new ObjectDisposedException(nameof(Lease));
            // 执行期不能以恢复/修复为由换目录；未完成事务必须先停止线路再恢复。
            if (!lease.AllowsInstall && File.Exists(Path.Combine(_root, "transaction.json")))
                throw new IOException("执行中的线路存在未完成安装事务，请停止任务后重新准备");
            // 恢复不依赖网络或调用方令牌；恢复后仍检查停止，不能把已停止报成成功。
            await RecoverAsync();
            ct.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            var token = deadline.Token;
            var policy = await GetJsonAsync<Policy>(Endpoint(server, "policy"), 16 * 1024, token);
            // 服务端未配置要求版本时不限制客户端版本；配置了才必须与本机完整版本一致。
            var versionPinned = !string.IsNullOrWhiteSpace(policy.RequiredClientVersion);
            if (policy.SchemaVersion != 1
                || (versionPinned && (string.IsNullOrWhiteSpace(clientVersion)
                    || !string.Equals(policy.RequiredClientVersion, clientVersion, StringComparison.Ordinal))))
                throw new InvalidDataException("协调服务要求的 BGI 完整版本与本机不一致，请升级后重试");
            RoutePackageDescriptor target;
            if (string.IsNullOrEmpty(packageId))
            {
                var current = await GetJsonAsync<RoutePackagePublication>(Endpoint(server, "current"), 8 * 1024 * 1024, token);
                if (current.Revision <= 0) throw new InvalidDataException("线路发布序号无效");
                target = current.Package;
            }
            else target = await GetJsonAsync<RoutePackageDescriptor>(Endpoint(server, "packages/" + packageId), 8 * 1024 * 1024, token);
            RequireDescriptor(target, requireCurrentVersion: versionPinned);
            if (packageId != null && packageId.Length > 0 && target.Id != packageId)
                throw new InvalidDataException("协调服务返回了不同的线路包");
            var installed = ReadInstalled();
            if (!forceRepair && installed?.Id == target.Id && SameManifest(installed.Files, target.Files)
                && await MatchesAsync(CurrentDirectory, target.Files, token))
            {
                token.ThrowIfCancellationRequested();
                return target.Id;
            }
            if (!lease.AllowsInstall)
                throw new IOException("线路已进入执行阶段，禁止替换或修复；请停止后重试本轮锁定包");
            if (!allowInstall)
                throw new InvalidDataException("自动更新线路已关闭，本地未完整安装目标包 " + target.Id + "；请先执行“更新联机锄地线路”或开启自动更新");
            await InstallAsync(server, target, installed, token);
            ct.ThrowIfCancellationRequested();
            return target.Id;
        }
        finally { lease.Gate.Release(); }
    }

    /// <summary>
    /// 线路服务地址：联机助手与 BGI 共用同一个“协调服务器地址”配置，常见写法是 http://主机:8080 或 http://主机:8080/hub。
    /// 线路只通过 HTTPS 提供（8080 是明文联机端口），所以按同一主机名改用 https://主机（默认 443 端口）；
    /// 本机回环开发地址保持原样。已经是 https 的地址原样使用；带账号、查询或锚点的地址仍然拒绝。
    /// </summary>
    /// <summary>官方线路服务地址：BGI 联机设置里没有填写有效地址时使用。</summary>
    public const string OfficialServerAddress = "https://www.autobgi.cn";

    /// <summary>
    /// 设置里没填、或仍是默认示例文字（不是 http/https 地址）时使用官方地址；
    /// 填了有效地址就用它（含非法的账号/查询部分，交给 RequireServerUri 明确拒绝，不悄悄换成官方地址）。
    /// </summary>
    public static string ResolveServerAddress(string? configured)
    {
        var text = (configured ?? "").Trim();
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host)
            ? text : OfficialServerAddress;
    }

    public static Uri RequireServerUri(string address)
    {
        var text = (address ?? "").Trim().TrimEnd('/');
        if (text.EndsWith("/hub", StringComparison.OrdinalIgnoreCase)) text = text[..^4].TrimEnd('/');
        if (!Uri.TryCreate(text + "/", UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidDataException("线路服务地址无效，请在联机设置里填写协调服务器地址，例如 https://www.autobgi.cn");
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            uri = new UriBuilder(Uri.UriSchemeHttps, uri.Host).Uri;
        return uri;
    }

    private static Uri Endpoint(Uri server, string path) => new(server, "api/hoeing-routes/" + path);

    private async Task<T> GetJsonAsync<T>(Uri uri, int limit, CancellationToken ct)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        RequireResponse(response, uri);
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(await response.Content.ReadAsStreamAsync(ct), buffer, limit, ct);
        return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), Json)
            ?? throw new InvalidDataException("协调服务返回空线路元数据");
    }

    private static void RequireResponse(HttpResponseMessage response, Uri requested)
    {
        response.EnsureSuccessStatusCode();
        // 默认 HttpClient 禁用重定向；注入客户端也不得悄悄切到不同源/不同 URL。
        if (response.RequestMessage?.RequestUri is { } actual && actual != requested)
            throw new InvalidDataException("线路服务不能重定向请求");
    }

    private void RequireDescriptor(RoutePackageDescriptor? target, bool requireCurrentVersion = true)
    {
        if (target == null || target.SchemaVersion != 1 || !RoutePackageArchive.IsValidId(target.Id)
            || !IsHash(target.Sha256) || target.SizeBytes <= 0 || target.SizeBytes > RoutePackageArchive.MaxArchiveBytes
            || (requireCurrentVersion && target.RequiredClientVersion != clientVersion) || target.State is not ("published" or "archived")
            || target.Files == null || target.FileCount != target.Files.Count || target.FileCount is <= 0
            || target.FileCount > RoutePackageArchive.MaxFiles || target.RouteCount <= 0)
            throw new InvalidDataException("线路包清单、发布状态或精确客户端版本无效");
        long total = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in target.Files)
        {
            if (file == null || !IsPackagePath(file.Path) || !paths.Add(file.Path)
                || file.SizeBytes < 0 || file.SizeBytes > RoutePackageArchive.MaxExpandedBytes || !IsHash(file.Sha256))
                throw new InvalidDataException("线路包文件清单无效");
            total = checked(total + file.SizeBytes);
            if (total > RoutePackageArchive.MaxExpandedBytes) throw new InvalidDataException("线路包展开总大小超限");
        }
        if (!paths.Any(p => p.StartsWith("builtin/", StringComparison.Ordinal) && p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("线路包缺少官方线路");
    }

    private async Task InstallAsync(Uri server, RoutePackageDescriptor target, RoutePackageDescriptor? previous, CancellationToken ct)
    {
        var transaction = new Transaction { Id = Guid.NewGuid().ToString("N"), Target = target, Previous = previous, HadCurrent = Directory.Exists(CurrentDirectory) };
        var staging = Path.Combine(_root, "staging-" + transaction.Id);
        var archive = Path.Combine(_root, "download-" + transaction.Id + ".zip");
        var journal = Path.Combine(_root, "transaction.json");
        RequireNoLinks(CurrentDirectory);
        WriteDurable(journal, transaction);
        try
        {
            var uri = Endpoint(server, "packages/" + target.Id + "/download");
            using (var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                RequireResponse(response, uri);
                if (response.Content.Headers.ContentLength is { } length && length != target.SizeBytes)
                    throw new InvalidDataException("下载的线路 ZIP 大小与清单不符");
                await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await CopyBoundedAsync(await response.Content.ReadAsStreamAsync(ct), output, target.SizeBytes, ct);
                await output.FlushAsync(ct);
                output.Flush(true);
                if (output.Length != target.SizeBytes) throw new InvalidDataException("线路 ZIP 下载不完整");
            }
            await using (var input = File.OpenRead(archive))
                if (Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant() != target.Sha256)
                    throw new InvalidDataException("线路 ZIP SHA256 校验失败");
            var actualFiles = await RoutePackageArchive.ValidateAsync(archive, ct);
            if (!SameManifest(actualFiles, target.Files)) throw new InvalidDataException("线路 ZIP 实际内容与完整清单不一致");
            // ExtractAsync 要求目标不存在，由共享校验器创建独占目录。
            await RoutePackageArchive.ExtractAsync(archive, staging, ct);
            if (!await MatchesAsync(staging, target.Files, ct)) throw new InvalidDataException("线路暂存目录校验失败");
            ct.ThrowIfCancellationRequested();
            // 此提交窗口无 await/取消点；即使进程被终止，下次也按 journal 完成或回退。
            if (transaction.HadCurrent) Directory.Move(CurrentDirectory, Path.Combine(_root, "backup-" + transaction.Id));
            Directory.Move(staging, CurrentDirectory);
            WriteDurable(Path.Combine(_root, "installed.json"), target);
            Multiplayer.RouteVariantScanner.InvalidateUnderRoot(CurrentDirectory);
            BetterGenshinImpact.Core.Script.Utils.JsonMerger.InvalidateUnderRoot(CurrentDirectory);
            await RecoverAsync();
            ct.ThrowIfCancellationRequested();
        }
        catch
        {
            await RecoverAsync();
            throw;
        }
    }

    private async Task RecoverAsync()
    {
        var journal = Path.Combine(_root, "transaction.json");
        RequireNoLinks(journal);
        if (!File.Exists(journal)) return;
        if (new FileInfo(journal).Length > 16 * 1024 * 1024) throw new InvalidDataException("线路恢复记录超限");
        var transaction = JsonSerializer.Deserialize<Transaction>(File.ReadAllText(journal), Json)
            ?? throw new InvalidDataException("线路恢复记录为空");
        if (!RoutePackageArchive.IsValidId(transaction.Id)) throw new InvalidDataException("线路恢复事务身份无效");
        RequireDescriptor(transaction.Target, requireCurrentVersion: false);
        if (transaction.Previous != null) RequireDescriptor(transaction.Previous, requireCurrentVersion: false);
        var staging = Path.Combine(_root, "staging-" + transaction.Id);
        var backup = Path.Combine(_root, "backup-" + transaction.Id);
        var archive = Path.Combine(_root, "download-" + transaction.Id + ".zip");
        RequireNoLinks(CurrentDirectory);
        RequireNoLinks(staging);
        RequireNoLinks(backup);
        RequireNoLinks(archive);
        if (await MatchesAsync(CurrentDirectory, transaction.Target.Files, CancellationToken.None))
        {
            WriteDurable(Path.Combine(_root, "installed.json"), transaction.Target);
            Multiplayer.RouteVariantScanner.InvalidateUnderRoot(CurrentDirectory);
            BetterGenshinImpact.Core.Script.Utils.JsonMerger.InvalidateUnderRoot(CurrentDirectory);
            DeleteOwnedDirectory(backup);
        }
        else if (!Directory.Exists(CurrentDirectory) && Directory.Exists(backup))
        {
            Directory.Move(backup, CurrentDirectory);
            Multiplayer.RouteVariantScanner.InvalidateUnderRoot(CurrentDirectory);
            BetterGenshinImpact.Core.Script.Utils.JsonMerger.InvalidateUnderRoot(CurrentDirectory);
            RestorePreviousState(transaction.Previous);
        }
        else if (Directory.Exists(backup))
            throw new IOException("线路事务当前目录内容异常；已保留旧完整 backup，拒绝覆盖未知数据");
        else if (!transaction.HadCurrent && Directory.Exists(CurrentDirectory))
            throw new IOException("线路事务出现未知 current 目录，拒绝覆盖");
        // 尚未改名时原 current 原样保留；只清理本事务明确拥有的文件。
        DeleteOwnedDirectory(staging);
        if (File.Exists(archive)) File.Delete(archive);
        File.Delete(journal);
    }

    private void RestorePreviousState(RoutePackageDescriptor? previous)
    {
        var installed = Path.Combine(_root, "installed.json");
        if (previous != null) WriteDurable(installed, previous);
        else if (File.Exists(installed)) File.Delete(installed);
    }

    private RoutePackageDescriptor? ReadInstalled()
    {
        var path = Path.Combine(_root, "installed.json");
        RequireNoLinks(path);
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 8 * 1024 * 1024) return null;
            var result = JsonSerializer.Deserialize<RoutePackageDescriptor>(File.ReadAllText(path), Json);
            RequireDescriptor(result, requireCurrentVersion: false);
            return result;
        }
        catch (JsonException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    private static bool SameManifest(IReadOnlyCollection<RoutePackageFile> left, IReadOnlyCollection<RoutePackageFile> right)
        => left.Count == right.Count && left.OrderBy(f => f.Path, StringComparer.Ordinal)
            .Zip(right.OrderBy(f => f.Path, StringComparer.Ordinal))
            .All(pair => pair.First.Path == pair.Second.Path && pair.First.SizeBytes == pair.Second.SizeBytes && pair.First.Sha256 == pair.Second.Sha256);

    private static async Task<bool> MatchesAsync(string directory, List<RoutePackageFile> files, CancellationToken ct)
    {
        if (!Directory.Exists(directory)) return false;
        RequireNoLinks(directory);
        var actual = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
        if (actual.Length != files.Count) return false;
        var actualPaths = actual.Select(p => Path.GetRelativePath(directory, p).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsPackagePath(file.Path) || !actualPaths.Contains(file.Path)) return false;
            var path = Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (new FileInfo(path).Length != file.SizeBytes) return false;
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant() != file.Sha256) return false;
        }
        return true;
    }

    private static bool IsHash(string? hash) => hash?.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsPackagePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.Contains('\\') || path.Contains(':')
            || !(path.StartsWith("builtin/", StringComparison.Ordinal) || path.StartsWith("pathing/", StringComparison.Ordinal))) return false;
        try { RoutePackageArchive.ValidatePath(path); }
        catch (InvalidDataException) { return false; }
        var parts = path.Split('/');
        if (parts.Length < (parts[0] == "builtin" ? 3 : 2)) return false;
        var variantDescription = parts[^1] == "变体说明.txt" && parts.Length >= 4
            && parts[^2] is "A变体" or "B变体" or "C变体" or "D变体";
        return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || parts[^1] == "control.json5" || parts[^1] == "README.md" || variantDescription;
    }

    private static async Task CopyBoundedAsync(System.IO.Stream input, System.IO.Stream output, long limit, CancellationToken ct)
    {
        await using (input)
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) != 0)
            {
                total = checked(total + read);
                if (total > limit) throw new InvalidDataException("线路服务响应超过大小限制");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
    }

    private static void WriteDurable<T>(string path, T value)
    {
        RequireNoLinks(path);
        var temp = path + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value, Json);
                file.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void DeleteOwnedDirectory(string path)
    {
        RequireNoLinks(path);
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    /// <summary>检查祖先和所有已存在后代，拒绝 junction/symlink；递归前检查避免跟随链接。</summary>
    internal static void RequireNoLinks(string path)
    {
        var full = Path.GetFullPath(path);
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("线路目录不能包含链接或重解析点：" + current);
        if (!Directory.Exists(full)) return;
        var pending = new Stack<string>();
        pending.Push(full);
        while (pending.Count > 0)
            foreach (var item in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("线路目录包含链接：" + item);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(item);
            }
    }

    private sealed class Policy
    {
        public int SchemaVersion { get; set; }
        public string RequiredClientVersion { get; set; } = "";
    }

    private sealed class Transaction
    {
        public string Id { get; set; } = "";
        public bool HadCurrent { get; set; }
        public RoutePackageDescriptor Target { get; set; } = new();
        public RoutePackageDescriptor? Previous { get; set; }
    }
}
