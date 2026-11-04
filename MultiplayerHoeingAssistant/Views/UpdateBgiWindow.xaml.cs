using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// "更新BGI"弹窗（成员卡 UID 右侧版本徽章点击，仅自机）。
/// 选择下载来源后，仅列出该来源的安装包；本地目录和手动指定包共用一个来源。
/// 更新 = 把选中的茶包版 .7z 解压覆盖到 BGI 目录：
///   弹窗前置检查（BetterGI.exe 存在、BGI 运行中则经确认后关闭）→ 风险确认弹窗（需求硬约束）→
///   可选先备份指定目录 → 解压覆盖（排除目录整目录跳过、被占用文件改名腾位）→ 结果汇总 + 刷新版本显示。
/// 包目录/排除目录/备份设置在窗口关闭时持久化到配置。
/// </summary>
public partial class UpdateBgiWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ObservableCollection<string> _excludeDirs = [];
    private readonly ObservableCollection<string> _backupDirs = [];
    private readonly List<string> _pickedPackagePaths;
    private readonly List<BgiCustomUpdateSource> _customSources;
    private CancellationTokenSource? _sourceCheck;
    private CancellationTokenSource? _downloadCancellation;
    private bool _sourcesReady;
    private bool _busy;
    private bool _closeAfterDownload;

    public UpdateBgiWindow(MainViewModel vm)
    {
        InitializeComponent();
        MultiplayerHoeingAssistant.Helpers.DpiAwarenessController.EnsureDpiAware();
        _vm = vm;
        _pickedPackagePaths = vm.Config?.BgiPickedPackages?.ToList() ?? [];
        _customSources = vm.Config?.BgiCustomUpdateSources?.ToList() ?? [];
        var exePath = vm.Config?.BgiPath;
        BgiDirBox.Text = string.IsNullOrWhiteSpace(exePath) ? "" : (Path.GetDirectoryName(exePath) ?? "");
        PackageDirBox.Text = vm.Config?.BgiPackageDir ?? "";
        foreach (var dir in vm.Config?.BgiUpdateExcludeDirs ?? []) _excludeDirs.Add(dir);
        ExcludeList.ItemsSource = _excludeDirs;
        foreach (var dir in vm.Config?.BgiUpdateBackupDirs ?? AssistConfig.DefaultBgiUpdateBackupDirs()) _backupDirs.Add(dir);
        BackupList.ItemsSource = _backupDirs;
        BackupCheckBox.IsChecked = vm.Config?.BgiUpdateBackupBeforeUpdate ?? true;
        RefreshCurrentVersion();
        BgiDirBox.TextChanged += (_, _) => RefreshCurrentVersion();
        RebuildSourceChoices(vm.AvailableBgiUpdateSourceId ?? vm.Config?.BgiUpdateSourceId ?? BgiUpdateSources.LocalId);
        Closed += (_, _) =>
        {
            _sourceCheck?.Cancel();
            _sourceCheck = null;
            PersistConfig();
        };
        Closing += (_, e) =>
        {
            if (!_busy) return;
            e.Cancel = true;
            if (_downloadCancellation == null) return;
            _closeAfterDownload = true;
            CancelDownload();
        };
    }

    private void RefreshCurrentVersion()
    {
        // 更新目标可能不是助手当前连接的实例，版本以目标目录里的程序为准。
        var targetDir = BgiDir;
        var version = targetDir.Length == 0 ? null
            : BgiVersionResolver.FromExecutable(Path.Combine(targetDir, "BetterGI.exe"));
        CurrentVersionText.Text = version ?? "未知";
    }

    private string BgiDir => BgiDirBox.Text.Trim();
    private string PackageDir => PackageDirBox.Text.Trim();

    // ===== 扫描 =====

    private async void RefreshPackagesButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshSourceAsync(preserveSelection: true);
    }

    private void RebuildSourceChoices(string? selectedId)
    {
        _sourcesReady = false;
        var sources = BgiUpdateSources.List(_vm.Config, _customSources);
        SourceBox.ItemsSource = sources;
        SourceBox.SelectedItem = sources.Find(s => s.Id == selectedId) ?? sources[0];
        _sourcesReady = true;
        _ = RefreshSourceAsync();
    }

    private async void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sourcesReady) await RefreshSourceAsync();
    }

    private async Task RefreshSourceAsync(bool preserveSelection = false)
    {
        if (_busy || SourceBox.SelectedItem is not BgiUpdateSource source) return;
        _sourceCheck?.Cancel();
        _sourceCheck = null;
        var selected = preserveSelection ? PackageList.SelectedItem as BgiUpdatePackage : null;
        PickPackageFileButton.Visibility = source.Kind == BgiUpdateSourceKind.Local ? Visibility.Visible : Visibility.Collapsed;
        RemoveSourceButton.IsEnabled = source.Kind == BgiUpdateSourceKind.Custom;
        if (source.Kind == BgiUpdateSourceKind.Local)
        {
            try
            {
                var packages = BgiPackageUpdateService.ScanPackages(PackageDir, _pickedPackagePaths);
                SetPackageList(packages, packages.Count == 0 ? "没有找到符合茶包版命名的本地 .7z 安装包。" : $"共 {packages.Count} 个本地安装包。");
                if (selected != null) PackageList.SelectedItem = packages.Find(p => p.FullPath == selected.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetPackageList([], "无法读取安装包目录，请检查路径和访问权限。");
            }
            return;
        }

        using var check = new CancellationTokenSource();
        _sourceCheck = check;
        SetPackageList([], $"正在检查 {source.Label}…");
        try
        {
            var result = await BgiRemoteUpdateService.FetchSourceAsync(source, check.Token).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                // 慢源返回时，用户可能已经切换了来源；只有当前请求可以改列表。
                if (_sourceCheck != check || check.IsCancellationRequested) return;
                _sourceCheck = null;
                var packages = result.Packages.Select(p => new BgiUpdatePackage(p.FileName, p.Url,
                    p.SizeBytes ?? 0, p.UpdatedAt?.ToLocalTime() ?? DateTime.MinValue, p.NameInfo, BgiPackageSource.Network, p, source)).ToList();
                SetPackageList(packages, result.Message ?? $"{source.Label} · {packages.Count} 个可下载包。选中安装包后点「更新」。");
                if (selected?.Remote != null) PackageList.SelectedItem = packages.Find(p => p.Remote == selected.Remote);
            }, DispatcherPriority.Normal, check.Token).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (check.IsCancellationRequested || Dispatcher.HasShutdownStarted) { }
    }

    private string EffectiveDownloadDir() => PackageDir.Length > 0 ? PackageDir : BgiDir;

    private void AddSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!BgiUpdateSources.TryCustom(SourceNameBox.Text, SourceUrlBox.Text, out var source))
        {
            SourceEditorStatus.Text = "请填写有效的 HTTPS 清单网址，名称不能包含控制字符。";
            return;
        }
        _customSources.RemoveAll(x => x != null && BgiUpdateSources.TryCustom(x.Label, x.ManifestUrl, out var existing) && existing!.Id == source!.Id);
        _customSources.Add(new(SourceNameBox.Text.Trim(), source!.Endpoint!.AbsoluteUri));
        RebuildSourceChoices(source.Id);
        SourceNameBox.Clear();
        SourceUrlBox.Clear();
        SourceEditorStatus.Text = "已保存并切换到此来源。";
        SourceEditor.IsExpanded = false;
        PersistConfig();
    }

    private void RemoveSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (SourceBox.SelectedItem is not BgiUpdateSource { Kind: BgiUpdateSourceKind.Custom } source) return;
        _customSources.RemoveAll(x => x != null && BgiUpdateSources.TryCustom(x.Label, x.ManifestUrl, out var existing) && existing!.Id == source.Id);
        RebuildSourceChoices(BgiUpdateSources.LocalId);
        SourceEditorStatus.Text = "已移除该来源。";
        PersistConfig();
    }

    private void SetPackageList(List<BgiUpdatePackage> packages, string message)
    {
        PackageList.ItemsSource = packages;
        PackageListHint.Text = message;
    }

    /// <summary>不经目录扫描，弹窗直接指定一个 .7z 安装包。同样过名字校验（不合法不允许用于更新），
    /// 通过后按版本序插入列表并选中，更新流程与列表选择完全一致。</summary>
    private void PickPackageFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择安装包（.7z）",
            Filter = "茶包版安装包 (*.7z)|*.7z|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(PackageDir) ? PackageDir : DefaultBrowseRoot(),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        var fileName = Path.GetFileName(dlg.FileName);
        if (!BgiUpdateDecisions.TryParse(fileName, out var info) || info is null)
        {
            MessageBox.Show(this,
                $"文件名不符合茶包版命名规范，不允许用于更新：\n{fileName}\n\n仅允许茶包版安装包（名称必须含 +lcb. 段，如 BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z），防止误升级到其他版本 BGI。",
                "更新BGI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var packages = PackageList.ItemsSource as List<BgiUpdatePackage> ?? [];
        var picked = packages.FirstOrDefault(p => string.Equals(p.FullPath, dlg.FileName, StringComparison.OrdinalIgnoreCase));
        if (picked == null)
        {
            var fi = new FileInfo(dlg.FileName);
            picked = new BgiUpdatePackage(fi.Name, fi.FullName, fi.Length, fi.LastWriteTime, info!, BgiPackageSource.PickedManually);
            packages.Add(picked);
            packages = packages
                .OrderByDescending(p => p.NameInfo, Comparer<BgiArchiveName>.Create(BgiUpdateDecisions.CompareNewer))
                .ToList();
            _pickedPackagePaths.Add(fi.FullName);
            SetPackageList(packages, $"共 {packages.Count} 个本地安装包。");
            PersistConfig(); // 立即持久化：崩溃/直接关窗也不丢
        }
        PackageList.SelectedItem = picked;
        PackageList.ScrollIntoView(picked);
    }

    // ===== 目录浏览 =====

    private void BrowseBgiDirButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择 BGI 目录（解压覆盖目标）",
            InitialDirectory = Directory.Exists(BgiDir) ? BgiDir : DefaultBrowseRoot(),
        };
        if (dlg.ShowDialog(this) == true) BgiDirBox.Text = dlg.FolderName;
    }

    private void BrowsePackageDirButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择更新安装包目录",
            InitialDirectory = Directory.Exists(PackageDir) ? PackageDir : DefaultBrowseRoot(),
        };
        if (dlg.ShowDialog(this) == true)
        {
            PackageDirBox.Text = dlg.FolderName;
            if (SourceBox.SelectedItem is BgiUpdateSource { Kind: BgiUpdateSourceKind.Local })
                _ = RefreshSourceAsync();
        }
    }

    private string DefaultBrowseRoot()
        => Directory.Exists(BgiDir) ? BgiDir : (Path.GetDirectoryName(_vm.Config?.BgiPath) is { Length: > 0 } p ? p : "");

    private void AddExcludeButton_Click(object sender, RoutedEventArgs e)
        => AddDirectories(_excludeDirs, "排除目录", "选择不参与覆盖的目录");

    private void AddBackupButton_Click(object sender, RoutedEventArgs e)
        => AddDirectories(_backupDirs, "备份目录", "选择更新前备份的目录");

    private void AddDirectories(ObservableCollection<string> directories, string label, string title)
    {
        var bgiDir = BgiDir;
        if (!Directory.Exists(bgiDir))
        {
            MessageBox.Show(this, $"请先填写有效的 BGI 目录，再添加{label}。", "更新BGI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var root = Path.GetFullPath(bgiDir);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var dlg = new OpenFolderDialog
        {
            Title = title + "（可按住 Ctrl 多选）",
            InitialDirectory = root,
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        int rejected = 0;
        foreach (var folder in dlg.FolderNames)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(full, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            {
                rejected++;
                continue;
            }
            var rel = Path.GetRelativePath(root, full);
            if (BgiUpdateDecisions.IsExcludedPath(rel, [BgiPackageUpdateService.BackupRootDirName]))
            {
                rejected++;
                continue;
            }
            if (!directories.Any(d => string.Equals(d.Replace('\\', '/'), rel.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                directories.Add(rel);
        }
        if (rejected > 0)
            MessageBox.Show(this, $"已忽略 {rejected} 个不能添加的目录。{label}必须是 BGI 的子目录，且不能位于 _update_backup 中。",
                "更新BGI", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void RemoveDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ObservableCollection<string> directories, DataContext: string dir })
            directories.Remove(dir);
    }

    // ===== 更新 =====

    private void PackageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sourcesReady) RefreshDownloadAction();
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCancellation != null)
        {
            CancelDownload();
            return;
        }
        if (_busy || PackageList.SelectedItem is not BgiUpdatePackage { Remote: { } remote, DownloadSource: { } source }) return;
        if (PackageDir.Length == 0 && !Directory.Exists(BgiDir))
        {
            StatusText.Text = "请先填写保存文件的安装包目录。";
            return;
        }

        using var operation = BgiPackageUpdateService.TryBeginPackageOperation();
        if (operation == null) { StatusText.Text = "另一个 BGI 下载或更新正在进行。"; return; }

        SetBusy(true);
        try
        {
            var directory = source.DownloadDirectory(EffectiveDownloadDir(), remote.ActionsRunId);
            var path = await PrepareDownloadAsync(remote, directory);
            if (path == null) return;
            // 也可从本地来源找到这份文件，稍后在没有网络的情况下安装。
            var fullPath = Path.GetFullPath(path);
            if (!_pickedPackagePaths.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                _pickedPackagePaths.Add(fullPath);
            PersistConfig();
            StatusText.Text = $"安装包已保存：{fullPath}。可在「本地安装包」中稍后更新。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"下载失败：{ex.Message}";
        }
        finally { EndOperation(); }
    }

    private async Task<string?> PrepareDownloadAsync(BgiRemoteUpdateInfo remote, string directory)
    {
        using var cancellation = new CancellationTokenSource();
        _downloadCancellation = cancellation;
        RefreshDownloadAction();
        StatusText.Text = "正在准备下载…";
        var progress = new Progress<string>(message =>
        {
            if (_downloadCancellation == cancellation && !cancellation.IsCancellationRequested)
                StatusText.Text = message;
        });
        try
        {
            var path = await BgiRemoteUpdateService.PreparePackageAsync(remote, directory, progress, cancellation.Token);
            // 文件刚就绪时也可能收到取消；先确认再进入不可取消的覆盖阶段。
            cancellation.Token.ThrowIfCancellationRequested();
            return path;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusText.Text = "已取消下载，未开始更新。";
            return null;
        }
        finally
        {
            _downloadCancellation = null;
            RefreshDownloadAction();
        }
    }

    private void CancelDownload()
    {
        if (_downloadCancellation is not { IsCancellationRequested: false } cancellation) return;
        StatusText.Text = "正在取消下载…";
        DownloadButton.IsEnabled = false;
        cancellation.Cancel();
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (PackageList.SelectedItem is not BgiUpdatePackage package)
        {
            MessageBox.Show(this, "请先在列表中选择一个安装包。", "更新BGI", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var targetDir = BgiDir;
        if (targetDir.Length == 0 || !Directory.Exists(targetDir))
        {
            MessageBox.Show(this, "请先填写有效的 BGI 目录。", "更新BGI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // 包名二次校验：不合法绝不解压覆盖（与扫描过滤双保险，需求硬约束）
        if (!BgiUpdateDecisions.TryParse(package.FileName, out _))
        {
            MessageBox.Show(this, $"安装包文件名不合法，已拒绝更新：\n{package.FileName}", "更新BGI", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (!File.Exists(Path.Combine(targetDir, "BetterGI.exe")))
        {
            var proceed = MessageBox.Show(this,
                $"目标目录下没有找到 BetterGI.exe：\n{targetDir}\n\n确定仍要解压到该目录吗？",
                "更新BGI", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (proceed != MessageBoxResult.Yes) return;
        }

        // 固定用户选中的包及其下载源；刷新或出现同名包也不会改用其他来源。
        var remoteInfo = package.Remote;
        var source = package.DownloadSource;
        var downloadDirectory = source?.DownloadDirectory(EffectiveDownloadDir(), remoteInfo?.ActionsRunId);

        // 运行中的 BGI 会锁住 exe/DLL：先在风险确认里说明，经确认后自动关闭
        var running = BgiPackageUpdateService.FindBgiProcessesUnder(targetDir);
        string backupSummary = BackupCheckBox.IsChecked != true ? "已关闭" :
            _backupDirs.Count == 0 ? "未选择目录" : string.Join("、", _backupDirs);
        string confirmText = "更新采用解压覆盖。只有与安装包路径相同的文件会被覆盖，不会删除其他个人文件。\n\n"
            + $"安装包：{package.FileName}\n目标目录：{targetDir}\n"
            + $"排除目录：{(_excludeDirs.Count == 0 ? "无" : string.Join("、", _excludeDirs))}"
            + (_excludeDirs.Count == 0 ? "" : "\n请确认排除列表不含 BGI 需要更新的目录，否则部分功能可能无法使用。")
            + $"\n更新前备份：{backupSummary}"
            + (remoteInfo != null ? $"\n下载来源：{source!.Label}（{source.Endpoint!.Host}）\n将复用已校验的安装包，或下载到 {downloadDirectory}（{remoteInfo.SizeText}）。安装包就绪后才关闭目标目录中正在运行的 BGI。" : "")
            + (remoteInfo == null && running.Count > 0 ? $"\n\n检测到 {running.Count} 个 BGI 正在运行，点击「确定」将自动关闭后继续。" : "")
            + "\n\n确定继续更新？";
        if (MessageBox.Show(this, confirmText, "更新BGI", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        await ApplyConfirmedUpdateAsync(package, targetDir, downloadDirectory);
    }

    private async Task ApplyConfirmedUpdateAsync(BgiUpdatePackage package, string targetDir, string? downloadDirectory)
    {
        using var operation = BgiPackageUpdateService.TryBeginPackageOperation();
        if (operation == null) { StatusText.Text = "另一个 BGI 下载或更新正在进行。"; return; }
        SetBusy(true, installing: true);
        try
        {
            // 网络来源包：先下载到安装目录（.part 临时文件，成功后改名覆盖），再走本地解压覆盖流程
            var archivePath = package.FullPath;
            if (package.Remote is { } remoteInfo)
            {
                var prepared = await PrepareDownloadAsync(remoteInfo, downloadDirectory!);
                if (prepared == null) return;
                archivePath = prepared;
            }
            var running = BgiPackageUpdateService.FindBgiProcessesUnder(targetDir);
            if (running.Count > 0 && !TryKillBgiProcesses(running))
            {
                MessageBox.Show(this, "BGI 进程关闭失败，已中止更新（请手动关闭后重试）。", "更新BGI", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var progress = new Progress<string>(s => StatusText.Text = $"正在更新… {s}");
            var result = await BgiPackageUpdateService.ApplyAsync(archivePath, targetDir,
                _excludeDirs.ToList(), BackupCheckBox.IsChecked == true ? _backupDirs.ToList() : [], progress);

            if (result.Success)
            {
                var lines = new List<string> { $"更新完成：覆盖 {result.ExtractedFiles} 个文件。" };
                if (result.ExcludedFiles > 0) lines.Add($"排除目录跳过 {result.ExcludedFiles} 个文件（未触碰）。");
                if (result.RenamedAside > 0) lines.Add($"{result.RenamedAside} 个被占用文件已改名腾位后覆盖。");
                if (result.SkippedFiles.Count > 0) lines.Add($"{result.SkippedFiles.Count} 个文件被跳过：\n{string.Join("\n", result.SkippedFiles)}");
                if (result.BackupDir != null) lines.Add($"备份已保存到：{result.BackupDir}");
                if (result.BackupCleanupWarning != null) lines.Add(result.BackupCleanupWarning);
                lines.Add("请重启 BGI 使新版本生效。");
                MessageBox.Show(this, string.Join("\n\n", lines), "更新BGI", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshCurrentVersion();
                // 本机版本已变，重算启动检查提示点。
                _vm.NotifyRemoteUpdateStateChanged();
                StatusText.Text = $"更新完成（{result.ExtractedFiles} 个文件）。";
            }
            else
            {
                StatusText.Text = "更新失败。";
                MessageBox.Show(this, $"更新失败：{result.Error}\n\n（备份位置：{result.BackupDir ?? "未生成"}）",
                    "更新BGI", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "更新失败。";
            MessageBox.Show(this, $"更新失败：{ex.Message}", "更新BGI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { EndOperation(); }
    }

    private bool TryKillBgiProcesses(List<Process> processes)
    {
        foreach (var proc in processes)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"关闭 BGI 进程（PID {proc.Id}）失败：{ex.Message}";
            }
        }
        foreach (var proc in processes)
        {
            try
            {
                proc.WaitForExit(10_000);
            }
            catch
            {
                // 已退出后再等会抛 InvalidOperationException：视为已关闭
            }
        }
        return processes.All(p => p.HasExited);
    }

    private void SetBusy(bool busy, bool installing = false)
    {
        _busy = busy;
        UpdateButton.IsEnabled = !busy;
        RefreshPackagesButton.IsEnabled = !busy;
        SourceBox.IsEnabled = !busy;
        SourceEditor.IsEnabled = !busy;
        PackageDirBox.IsEnabled = !busy;
        BgiDirBox.IsEnabled = !busy;
        BrowseBgiDirButton.IsEnabled = !busy;
        BrowsePackageDirButton.IsEnabled = !busy;
        AddExcludeButton.IsEnabled = !busy;
        ExcludeList.IsEnabled = !busy;
        AddBackupButton.IsEnabled = !busy;
        BackupList.IsEnabled = !busy;
        BackupCheckBox.IsEnabled = !busy;
        PackageList.IsEnabled = !busy;
        PickPackageFileButton.IsEnabled = !busy;
        UpdateButton.Content = busy && installing ? "更新中…" : "更新";
        RefreshDownloadAction();
    }

    private void RefreshDownloadAction()
    {
        DownloadButton.Content = _downloadCancellation != null ? "取消下载" : "仅下载";
        DownloadButton.IsEnabled = _downloadCancellation is { IsCancellationRequested: false }
            || (!_busy && PackageList.SelectedItem is BgiUpdatePackage { Remote: not null, DownloadSource: not null });
        CloseButton.IsEnabled = !_busy || _downloadCancellation != null;
    }

    private void EndOperation()
    {
        SetBusy(false);
        if (!_closeAfterDownload) return;
        _closeAfterDownload = false;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void PersistConfig()
    {
        if (_vm.Config == null) return;
        _vm.Config.BgiPackageDir = PackageDir;
        _vm.Config.BgiUpdateExcludeDirs = _excludeDirs.ToList();
        _vm.Config.BgiUpdateBackupDirs = _backupDirs.ToList();
        _vm.Config.BgiUpdateBackupBeforeUpdate = BackupCheckBox.IsChecked == true;
        _vm.Config.BgiPickedPackages = _pickedPackagePaths.Concat(_vm.Config.BgiPickedPackages ?? [])
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _vm.Config.BgiUpdateSourceId = (SourceBox.SelectedItem as BgiUpdateSource)?.Id;
        _vm.Config.BgiCustomUpdateSources = _customSources.ToList();
        _vm.SaveConfigToDisk();
    }
}
