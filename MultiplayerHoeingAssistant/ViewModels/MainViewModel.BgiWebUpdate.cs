using System.IO;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private const string BgiWebUpdatePrefix = "bgi.update.";
    private readonly Dictionary<string, WebBgiPackageTicket> _webBgiTickets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WebBgiUpdateJob> _webBgiJobs = new(StringComparer.Ordinal);

    private sealed record WebBgiPackageTicket(string Owner, string Instance, string Executable, string DownloadRoot,
        string[] Excludes, BgiUpdatePackage Package, DateTimeOffset Expires);

    private sealed class WebBgiUpdateJob(string id, string owner, string ticketId, bool install)
    {
        public string Id { get; } = id;
        public string Owner { get; } = owner;
        public string TicketId { get; } = ticketId;
        public bool Install { get; } = install;
        public bool CloseBgi { get; init; }
        public string State { get; set; } = "downloading";
        public string Message { get; set; } = "正在准备安装包…";
        public string? SavedPath { get; set; }
        public bool Done { get; set; }
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public CancellationTokenSource Cancel { get; } = new();
    }

    // 网页只持有票据编号，不得提交本机路径、下载 URL 或修改排除/备份设置。
    private async Task<object> ExecuteBgiWebUpdateAsync(string operation, JsonObject request, bool local, AssistConfig config)
    {
        var instance = config.ClientInstanceId;
        if (!IsExecutorMode || _disposing)
            return WebReply("rejected", "更新 BGI 只在执行端可用。", instanceId: instance);
        var browserId = local ? null : request["browserId"]?.GetValue<string>();
        var secret = local ? null : request["browserSecret"]?.GetValue<string>();
        var owner = local ? "local" : "remote:" + browserId;
        var initialClient = _signalRClient;
        var initialRoom = RoomCode;
        var epoch = Interlocked.Read(ref _mistletoeWebEpoch);
        bool StillAuthorized() => !_disposing && IsExecutorMode && _config?.ClientInstanceId == instance
            && _config?.PlayerUid == config.PlayerUid && (local || (ReferenceEquals(initialClient, _signalRClient)
                && RoomCode == initialRoom && epoch == Interlocked.Read(ref _mistletoeWebEpoch) && WebOwners.Verify(browserId, secret)));

        var payload = request["payload"] as JsonObject;
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _webBgiTickets.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray())
            _webBgiTickets.Remove(key);
        foreach (var key in _webBgiJobs.Where(p => p.Value.Done && now - p.Value.UpdatedAt > TimeSpan.FromMinutes(30)).Select(p => p.Key).ToArray())
            _webBgiJobs.Remove(key);

        object Reply(string status, string message, object? data = null) => WebReply(status, message, data, instanceId: instance);
        switch (operation)
        {
            case "bgi.update.hint":
            {
                // 只读取助手已有的启动检查结果，不从网页发起网络查包。
                var sourceId = AvailableBgiUpdateSourceId;
                return Reply("ok", "", new { available = sourceId != null, sourceId });
            }
            case "bgi.update.catalog":
            {
                if (!CanEditConfiguration) return Reply("rejected", ConfigTransferBlockedMessage);
                var sources = BgiUpdateSources.List(config);
                var sourceId = payload?["sourceId"]?.GetValue<string>();
                var source = sources.Find(s => s.Id == (sourceId ?? config.BgiUpdateSourceId)) ?? sources[0];
                var executable = FullBgiExecutable(config.BgiPath);
                var downloadRoot = string.IsNullOrWhiteSpace(config.BgiPackageDir)
                    ? (executable == null ? "" : Path.GetDirectoryName(executable)!)
                    : Path.GetFullPath(config.BgiPackageDir);
                List<BgiUpdatePackage> packages;
                string? message = null;
                if (source.Kind == BgiUpdateSourceKind.Local)
                    packages = BgiPackageUpdateService.ScanPackages(config.BgiPackageDir, config.BgiPickedPackages);
                else
                {
                    var remote = await BgiRemoteUpdateService.FetchSourceAsync(source);
                    packages = remote.Packages.Select(p => new BgiUpdatePackage(p.FileName, p.Url,
                        p.SizeBytes ?? 0, p.UpdatedAt ?? DateTime.MinValue, p.NameInfo, BgiPackageSource.Network, p, source)).ToList();
                    message = remote.Message;
                }
                if (!StillAuthorized()) return Reply("denied", "执行端或网页授权已经变化，请重新选择。");

                var visible = packages.Take(50).ToArray();
                foreach (var key in _webBgiTickets.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToArray())
                    _webBgiTickets.Remove(key);
                if (_webBgiTickets.Count + visible.Length > 400)
                    return Reply("rejected", "查询过于频繁，请稍后再试。");
                var rows = visible.Select(package =>
                {
                    var id = Guid.NewGuid().ToString("N");
                    _webBgiTickets[id] = new(owner, instance, executable ?? "", downloadRoot,
                        WebBgiExcludes(config, executable), package, DateTimeOffset.UtcNow.AddMinutes(10));
                    return new { id, fileName = package.FileName, size = package.SizeText, canDownload = package.Remote != null && downloadRoot.Length != 0 };
                }).ToArray();

                return Reply("ok", "", new
                {
                    sources = sources.Select(s => new { id = s.Id, name = s.Label }),
                    sourceId = source.Id,
                    currentVersion = InstalledBgiVersion,
                    targetDirectory = executable == null ? "" : Path.GetDirectoryName(executable),
                    downloadDirectory = downloadRoot,
                    canInstall = executable != null,
                    packageMessage = message,
                    packages = rows,
                    job = _webBgiJobs.Values.Where(j => j.Owner == owner).OrderByDescending(j => j.UpdatedAt).Select(JobView).FirstOrDefault()
                });
            }
            case "bgi.update.status":
            case "bgi.update.cancel":
            {
                var id = payload?["id"]?.GetValue<string>() ?? "";
                if (!_webBgiJobs.TryGetValue(id, out var job) || job.Owner != owner)
                    return Reply("unknown", "执行端找不到该操作，结果未确认。");
                if (operation == "bgi.update.cancel" && !job.Done && job.State == "downloading")
                {
                    job.State = "cancelling";
                    job.Message = "正在取消下载…";
                    job.Cancel.Cancel();
                }
                return Reply("ok", "", JobView(job));
            }
            case "bgi.update.start":
            {
                var id = payload?["id"]?.GetValue<string>() ?? "";
                var ticketId = payload?["ticket"]?.GetValue<string>() ?? "";
                var install = payload?["install"]?.GetValue<bool>() == true;
                var closeBgi = install && payload?["closeBgi"]?.GetValue<bool>() == true;
                if (!Guid.TryParseExact(id, "N", out _)) return Reply("rejected", "操作编号无效。");
                if (_webBgiJobs.TryGetValue(id, out var existing))
                    return existing.Owner == owner && existing.TicketId == ticketId && existing.Install == install
                        ? Reply("ok", "", JobView(existing)) : Reply("rejected", "操作编号已经用于其他请求。");

                if (!_webBgiTickets.TryGetValue(ticketId, out var ticket) || ticket.Owner != owner || ticket.Instance != instance)
                    return Reply("rejected", "安装包选择已过期，请刷新列表后重新选择。");
                if (!install && (ticket.Package.Remote == null || ticket.DownloadRoot.Length == 0))
                    return Reply("rejected", "请选择网络安装包，并在助手中设置安装包目录。");
                if (install && !MatchesBgiTarget(ticket.Executable))
                    return Reply("rejected", "请确认助手配置了 BGI 路径。可先仅下载安装包。");
                if (install && !closeBgi && !IsWebBgiTargetStopped(ticket.Executable))
                    return Reply("rejected", "执行端的 BGI 尚未退出。请先退出 BGI，或勾选“更新前自动关闭 BGI”。");
                if (_webBgiJobs.Count >= 64) return Reply("rejected", "更新操作过于频繁，请稍后再试。");
                if (!TryEnterConfigTransferOperation("bgi.update", out var configLease))
                    return Reply("rejected", ConfigTransferBlockedMessage);
                var packageLease = BgiPackageUpdateService.TryBeginPackageOperation();
                if (packageLease == null)
                {
                    configLease?.Dispose();
                    return Reply("rejected", "另一个 BGI 下载或更新正在进行。");
                }

                _webBgiTickets.Remove(ticketId);
                var job = new WebBgiUpdateJob(id, owner, ticketId, install) { CloseBgi = closeBgi };
                _webBgiJobs.Add(id, job);
                string[] backupDirs = install && config.BgiUpdateBackupBeforeUpdate
                    ? (config.BgiUpdateBackupDirs ?? AssistConfig.DefaultBgiUpdateBackupDirs()).ToArray() : [];
                _ = RunWebBgiUpdateAsync(job, ticket, backupDirs, StillAuthorized, packageLease, configLease);
                return Reply("ok", "", JobView(job));
            }
            default:
                return Reply("rejected", "不支持此更新操作。");
        }
    }

    private async Task RunWebBgiUpdateAsync(WebBgiUpdateJob job, WebBgiPackageTicket ticket, string[] backupDirs,
        Func<bool> stillAuthorized, IDisposable packageLease, IDisposable? configLease)
    {
        using (packageLease)
        using (configLease)
        using (job.Cancel)
        try
        {
            // 发起请求的网关只等待受理，不阻塞到几百 MB 的下载或解压结束。
            await Task.Yield();
            var cancel = job.Cancel.Token;
            if (!stillAuthorized()) throw new UnauthorizedAccessException("网页授权或执行端已变化，未更新。");
            var package = ticket.Package;
            var path = package.FullPath;
            if (package.Remote is { } remote && package.DownloadSource is { } source)
            {
                if (ticket.DownloadRoot.Length == 0) throw new InvalidDataException("请先在助手设置安装包目录。");
                var directory = source.DownloadDirectory(ticket.DownloadRoot, remote.ActionsRunId);
                path = await BgiRemoteUpdateService.PreparePackageAsync(remote, directory,
                    new Progress<string>(message => { if (job.State == "downloading") job.Message = message; }), cancel);
            }
            else
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length != package.LengthBytes || file.LastWriteTime != package.ModifiedTime)
                    throw new IOException("本地安装包已变化，请刷新并重新选择。");
            }
            cancel.ThrowIfCancellationRequested();
            if (!stillAuthorized()) throw new UnauthorizedAccessException("网页授权或执行端已变化，未更新。");
            job.SavedPath = path;
            if (package.Remote != null && _config is { } current)
            {
                current.BgiPickedPackages = (current.BgiPickedPackages ?? []).Append(path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                SaveConfigToDisk();
            }
            if (job.Install)
            {
                var target = Path.GetDirectoryName(ticket.Executable)!;
                job.State = "installing";
                job.Message = "正在通知本机所有助手关闭 BGI…";
                AddLog($"[更新BGI][网页端] 安装包={ticket.Package.FileName}；目标={Path.GetFullPath(target)}；排除={string.Join("、", ticket.Excludes)}；完成后自动重启");
                // 同机协作：所有使用这个 BGI 目录的助手（执行端、监控端、任何 Windows 账户）各自关闭自己的 BGI。
                var coordination = await BgiUpdateCoordination.BeginAsync(target, job.CloseBgi,
                    message => { if (job.State == "installing") job.Message = message; });
                var completed = false;
                try
                {
                    using var reservation = MatchesBgiTarget(ticket.Executable)
                        ? BgiPackageUpdateService.TryReserveStoppedWebInstall(ticket.Executable) : null;
                    if (reservation == null)
                        throw new IOException("BGI 正在运行、路径已变化或无法确认进程状态。已保留下载包，已恢复原来运行的 BGI。");
                    job.Message = "正在更新 BGI…";
                    var result = await BgiPackageUpdateService.ApplyAsync(path, target, ticket.Excludes, backupDirs,
                        new Progress<string>(message => { if (job.State == "installing") job.Message = message; }),
                        beforeOverwrite: coordination.BeginInstallation);
                    NotifyRemoteUpdateStateChanged();
                    AddLog($"[更新BGI][网页端] 覆盖结果：成功={result.Success}，覆盖={result.ExtractedFiles}，改名腾位={result.RenamedAside}，跳过={result.SkippedFiles.Count}");
                    if (!result.Success || result.SkippedFiles.Count > 0)
                    {
                        var restartBlocked = result.RestartBlocked || coordination.MustKeepRestartBlocked;
                        if (restartBlocked)
                        {
                            coordination.BlockRestart();
                            completed = true; // 这是失败终态；finally 不得改成允许恢复的普通失败。
                        }
                        throw new IOException((result.Error ?? $"有 {result.SkippedFiles.Count} 个文件未覆盖。") +
                            (result.BackupDir == null ? "" : $" 备份：{result.BackupDir}") +
                            (restartBlocked ? " 更新未完整完成，BGI 保持关闭，未重启助手。" : " 已恢复原来运行的 BGI，未重启助手。"));
                    }
                    job.Message = $"更新完成，已覆盖 {result.ExtractedFiles} 个文件。" +
                        (result.BackupDir == null ? "" : $" 备份：{result.BackupDir}") +
                        (result.BackupCleanupWarning == null ? "" : $" {result.BackupCleanupWarning}") +
                        " 约 6 秒后本机所有助手（执行端和监控端）将自动重启，原来在运行的 BGI 也会重新启动；网页会短暂断线后自动重连。";
                    // 给网页留时间读到“完成”，再让所有助手（包括本助手）一起重启。
                    coordination.Complete(TimeSpan.FromSeconds(6));
                    completed = true;
                }
                finally { if (!completed) coordination.Fail(); }
            }
            else job.Message = "安装包已保存，可稍后更新。";
            job.State = "done";
        }
        catch (OperationCanceledException) when (job.Cancel.IsCancellationRequested)
        {
            job.State = "cancelled";
            job.Message = "已取消下载，未安装。";
        }
        catch (Exception ex)
        {
            job.State = "failed";
            job.Message = ex.Message;
        }
        finally
        {
            job.Done = true;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static object JobView(WebBgiUpdateJob job) => new
    {
        id = job.Id, state = job.State, message = job.Message, savedPath = job.SavedPath, done = job.Done,
        canCancel = !job.Done && job.State == "downloading"
    };

    private static string? FullBgiExecutable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return null;
        var path = Path.GetFullPath(value);
        return Path.GetFileName(path).Equals("BetterGI.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    // 网页使用配置快照中的排除项并额外保护 User；电脑端使用窗口当前的排除项。
    // 两端都不自动排除助手目录，未被用户排除的助手文件经共用覆盖器更新，完成后重启生效。
    private static string[] WebBgiExcludes(AssistConfig config, string? executable)
    {
        var paths = new HashSet<string>(config.BgiUpdateExcludeDirs ?? [], StringComparer.OrdinalIgnoreCase) { "User" };
        return paths.ToArray();
    }

    /// <summary>BGI 更新完成后的统一收尾（PC 端与网页端共用）：先按需重新启动 BGI，再退出并重启助手，使新文件生效。
    /// 延时给网页留出读取"更新完成"状态的时间；重启后网页会自动重连。</summary>
    internal void RelaunchAfterBgiUpdate(bool startBgi, TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                if (startBgi)
                {
                    AddLog("[更新BGI] 更新完成，正在重新启动 BGI");
                    if (!StartCoordinatedBgi())
                        AddLog("[更新BGI] BGI 未能自动启动，请稍后手动启动");
                    await Task.Delay(1500);
                }
                AddLog("[更新BGI] 正在重启助手以应用新版本");
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null) await dispatcher.InvokeAsync(RestartAssistant);
            }
            catch (Exception ex) { AddLog("[更新BGI] 自动重启失败，请手动重启助手：" + ex.Message); }
        });
    }

    private bool MatchesBgiTarget(string executable) => executable.Length > 0 &&
        string.Equals(FullBgiExecutable(_config?.BgiPath), executable, StringComparison.OrdinalIgnoreCase);

    private static bool IsWebBgiTargetStopped(string executable)
    {
        if (executable.Length == 0) return false;
        var processes = BgiPackageUpdateService.FindBgiProcessesUnder(Path.GetDirectoryName(executable)!);
        try { return processes.Count == 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
