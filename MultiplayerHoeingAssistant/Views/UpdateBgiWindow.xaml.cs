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
///   可选先备份排除目录 → 解压覆盖（排除目录整目录跳过、被占用文件改名腾位）→ 结果汇总 + 刷新版本显示。
/// 包目录/排除目录/备份勾选在窗口关闭时持久化到配置。
/// </summary>
public partial class UpdateBgiWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ObservableCollection<string> _excludeDirs = [];
    private readonly List<string> _pickedPackagePaths;
    private readonly List<BgiCustomUpdateSource> _customSources;
    private CancellationTokenSource? _sourceCheck;
    private bool _sourcesReady;
    private bool _updating;

    public UpdateBgiWindow(MainViewModel vm)
    {
        InitializeComponent();
        // 屏幕工作区适配：尺寸压到所在屏 95% 内并居中钳位，防止高缩放/小屏下窗口被截（与 MainWindow/SettingsWindow 同款）
        MultiplayerHoeingAssistant.Helpers.DpiAwarenessController.Initialize(this);
        _vm = vm;
        _pickedPackagePaths = vm.Config?.BgiPickedPackages?.ToList() ?? [];
        _customSources = vm.Config?.BgiCustomUpdateSources?.ToList() ?? [];
        var exePath = vm.Config?.BgiPath;
        BgiDirBox.Text = string.IsNullOrWhiteSpace(exePath) ? "" : (Path.GetDirectoryName(exePath) ?? "");
        PackageDirBox.Text = vm.Config?.BgiPackageDir ?? "";
        foreach (var dir in vm.Config?.BgiUpdateExcludeDirs ?? []) _excludeDirs.Add(dir);
        ExcludeList.ItemsSource = _excludeDirs;
        BackupExcludeCheckBox.IsChecked = vm.Config?.BgiUpdateBackupBeforeUpdate ?? true;
        RefreshCurrentVersion();
        RebuildSourceChoices(vm.Config?.BgiUpdateSourceId ??
            (vm.RemoteUpdateAvailable ? BgiUpdateSources.CoordinatorId : BgiUpdateSources.LocalId));
        Closed += (_, _) =>
        {
            _sourceCheck?.Cancel();
            _sourceCheck = null;
            PersistConfig();
        };
        Closing += (_, e) => e.Cancel = _updating;
    }

    private void RefreshCurrentVersion()
    {
        // 更新完成后 exe 已换新、外部通道已死，Resolve 会自然落到读 exe 的兜底分支
        CurrentVersionText.Text = _vm.ResolveLocalBgiVersion() ?? "未知（未找到 BetterGI.exe）";
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
        if (_updating || SourceBox.SelectedItem is not BgiUpdateSource source) return;
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

    // ===== 排除目录 =====

    private void AddExcludeButton_Click(object sender, RoutedEventArgs e)
    {
        if (BgiDir.Length == 0 || !Directory.Exists(BgiDir))
        {
            MessageBox.Show(this, "请先填写有效的 BGI 目录，再添加排除目录。", "更新BGI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dlg = new OpenFolderDialog
        {
            Title = "选择不参与覆盖的目录（可按住 Ctrl 多选）",
            InitialDirectory = BgiDir,
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        int added = 0, rejected = 0;
        foreach (var full in dlg.FolderNames)
        {
            // 排除目录按 BGI 目录内相对路径记录；选到 BGI 目录外没有意义，拒收
            if (!full.StartsWith(Path.GetFullPath(BgiDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                rejected++;
                continue;
            }
            var rel = Path.GetRelativePath(BgiDir, full);
            if (_excludeDirs.Any(d => string.Equals(d.Replace('\\', '/'), rel.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                continue; // 已存在，去重
            if (string.Equals(rel, BgiPackageUpdateService.BackupRootDirName, StringComparison.OrdinalIgnoreCase))
                continue; // 备份目录自身不排除（否则备份无意义）
            _excludeDirs.Add(rel);
            added++;
        }
        if (rejected > 0)
            MessageBox.Show(this, $"有 {rejected} 个目录在 BGI 目录之外，已忽略（排除目录必须是 BGI 目录的子目录）。", "更新BGI", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void RemoveExcludeButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string dir) _excludeDirs.Remove(dir);
    }

    // ===== 更新 =====

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
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
        string confirmText = "更新有风险，建议先备份数据（用户配置、脚本仓库等重要目录可用下方「排除目录+备份」保护）。\n\n"
            + $"安装包：{package.FileName}\n目标目录：{targetDir}\n"
            + $"排除目录：{(_excludeDirs.Count == 0 ? "无" : string.Join("、", _excludeDirs))}"
            + (BackupExcludeCheckBox.IsChecked == true && _excludeDirs.Count > 0 ? "\n（更新前先备份排除目录）" : "")
            + (remoteInfo != null ? $"\n下载来源：{source!.Label}（{source.Endpoint!.Host}）\n将先下载到 {downloadDirectory}（{remoteInfo.SizeText}），校验后才关闭目标目录中正在运行的 BGI。" : "")
            + (remoteInfo == null && running.Count > 0 ? $"\n\n检测到 {running.Count} 个 BGI 正在运行，点击「确定」将自动关闭后继续。" : "")
            + "\n\n确定继续更新？";
        if (MessageBox.Show(this, confirmText, "更新BGI", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        _updating = true;
        SetBusy(true);
        try
        {
            // 网络来源包：先下载到安装目录（.part 临时文件，成功后改名覆盖），再走本地解压覆盖流程
            var archivePath = package.FullPath;
            if (remoteInfo != null)
            {
                var dlProgress = new Progress<string>(s => StatusText.Text = s);
                archivePath = await BgiRemoteUpdateService.DownloadAsync(remoteInfo, downloadDirectory!, dlProgress);
                running = BgiPackageUpdateService.FindBgiProcessesUnder(targetDir);
            }
            if (running.Count > 0 && !TryKillBgiProcesses(running))
            {
                MessageBox.Show(this, "BGI 进程关闭失败，已中止更新（请手动关闭后重试）。", "更新BGI", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var progress = new Progress<string>(s => StatusText.Text = $"正在更新… {s}");
            var result = await BgiPackageUpdateService.ApplyAsync(archivePath, targetDir,
                _excludeDirs.ToList(), BackupExcludeCheckBox.IsChecked == true, progress);

            if (result.Success)
            {
                var lines = new List<string> { $"更新完成：覆盖 {result.ExtractedFiles} 个文件。" };
                if (result.ExcludedFiles > 0) lines.Add($"排除目录跳过 {result.ExcludedFiles} 个文件（未触碰）。");
                if (result.RenamedAside > 0) lines.Add($"{result.RenamedAside} 个被占用文件已改名腾位后覆盖。");
                if (result.SkippedFiles.Count > 0) lines.Add($"{result.SkippedFiles.Count} 个文件被跳过：\n{string.Join("\n", result.SkippedFiles)}");
                if (result.BackupDir != null) lines.Add($"排除目录已备份到：{result.BackupDir}");
                lines.Add("请重启 BGI 使新版本生效。");
                MessageBox.Show(this, string.Join("\n\n", lines), "更新BGI", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshCurrentVersion();
                // 本机版本已变：立即重算"服务器有新版本"判定（金色版本号/提醒条及时熄灭，不等下个 5 分钟轮询）
                _vm.NotifyRemoteUpdateStateChanged();
                StatusText.Text = $"更新完成（{result.ExtractedFiles} 个文件）。";
            }
            else
            {
                StatusText.Text = "更新失败。";
                MessageBox.Show(this, $"更新失败：{result.Error}\n\n（排除目录如有备份，位于：{result.BackupDir ?? "未生成"}）",
                    "更新BGI", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "更新失败。";
            MessageBox.Show(this, $"更新失败：{ex.Message}", "更新BGI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _updating = false;
            SetBusy(false);
        }
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

    private void SetBusy(bool busy)
    {
        UpdateButton.IsEnabled = !busy;
        RefreshPackagesButton.IsEnabled = !busy;
        SourceBox.IsEnabled = !busy;
        SourceEditor.IsEnabled = !busy;
        PackageDirBox.IsEnabled = !busy;
        BgiDirBox.IsEnabled = !busy;
        BrowseBgiDirButton.IsEnabled = !busy;
        BrowsePackageDirButton.IsEnabled = !busy;
        AddExcludeButton.IsEnabled = !busy;
        PickPackageFileButton.IsEnabled = !busy;
        UpdateButton.Content = busy ? "更新中…" : "更新";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void PersistConfig()
    {
        if (_vm.Config == null) return;
        _vm.Config.BgiPackageDir = PackageDir;
        _vm.Config.BgiUpdateExcludeDirs = _excludeDirs.ToList();
        _vm.Config.BgiUpdateBackupBeforeUpdate = BackupExcludeCheckBox.IsChecked == true;
        _vm.Config.BgiPickedPackages = _pickedPackagePaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _vm.Config.BgiUpdateSourceId = (SourceBox.SelectedItem as BgiUpdateSource)?.Id;
        _vm.Config.BgiCustomUpdateSources = _customSources.ToList();
        _vm.SaveConfigToDisk();
    }
}
