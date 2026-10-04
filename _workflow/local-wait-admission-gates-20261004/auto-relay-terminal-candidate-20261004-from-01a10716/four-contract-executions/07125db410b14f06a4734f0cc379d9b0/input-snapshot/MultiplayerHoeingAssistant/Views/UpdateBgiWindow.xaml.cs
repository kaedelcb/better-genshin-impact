using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// "更新BGI"弹窗（成员卡 UID 右侧版本徽章点击，仅自机）。
/// 三行结构：当前 BGI 版本 / BGI 目录 / 安装包目录（行尾刷新键）→ 包列表 → 排除目录 + 备份勾选 → 更新。
/// 更新 = 把选中的茶包版 .7z 解压覆盖到 BGI 目录：
///   弹窗前置检查（BetterGI.exe 存在、BGI 运行中则经确认后关闭）→ 风险确认弹窗（需求硬约束）→
///   可选先备份排除目录 → 解压覆盖（排除目录整目录跳过、被占用文件改名腾位）→ 结果汇总 + 刷新版本显示。
/// 包目录/排除目录/备份勾选在窗口关闭时持久化到配置。
/// </summary>
public partial class UpdateBgiWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ObservableCollection<string> _excludeDirs = [];
    private bool _updating;

    public UpdateBgiWindow(MainViewModel vm)
    {
        InitializeComponent();
        // 屏幕工作区适配：尺寸压到所在屏 95% 内并居中钳位，防止高缩放/小屏下窗口被截（与 MainWindow/SettingsWindow 同款）
        MultiplayerHoeingAssistant.Helpers.DpiAwarenessController.Initialize(this);
        _vm = vm;
        var exePath = vm.Config?.BgiPath;
        BgiDirBox.Text = string.IsNullOrWhiteSpace(exePath) ? "" : (Path.GetDirectoryName(exePath) ?? "");
        PackageDirBox.Text = vm.Config?.BgiPackageDir ?? "";
        foreach (var dir in vm.Config?.BgiUpdateExcludeDirs ?? []) _excludeDirs.Add(dir);
        ExcludeList.ItemsSource = _excludeDirs;
        BackupExcludeCheckBox.IsChecked = vm.Config?.BgiUpdateBackupBeforeUpdate ?? true;
        RefreshCurrentVersion();
        SetPackageList(BgiPackageUpdateService.ScanPackages(PackageDirBox.Text.Trim(), _vm.Config?.BgiPickedPackages));
        Closed += (_, _) => PersistConfig();
        // 打开弹窗时顺带触发一次网络更新检测；检测到服务器包后重建列表（把"网络"条目带进来，提醒条由绑定自动出现）
        _ = CheckRemoteUpdateAndRescanAsync();
    }

    private void RefreshCurrentVersion()
    {
        // 更新完成后 exe 已换新、外部通道已死，Resolve 会自然落到读 exe 的兜底分支
        CurrentVersionText.Text = _vm.ResolveLocalBgiVersion() ?? "未知（未找到 BetterGI.exe）";
    }

    private string BgiDir => BgiDirBox.Text.Trim();
    private string PackageDir => PackageDirBox.Text.Trim();

    // ===== 扫描 =====

    private void RefreshPackagesButton_Click(object sender, RoutedEventArgs e) => ScanPackages();

    private async Task CheckRemoteUpdateAndRescanAsync()
    {
        try
        {
            var info = await _vm.CheckRemoteUpdateAsync();
            if (info != null && !_updating) ScanPackages();
        }
        catch
        {
            // 网络检测失败静默：弹窗其余功能不受影响
        }
    }

    private void ScanPackages()
        => SetPackageList(BgiRemoteUpdateService.MergeNetworkPackage(
            BgiPackageUpdateService.ScanPackages(PackageDir, _vm.Config?.BgiPickedPackages),
            _vm.RemoteBgiUpdate, EffectiveDownloadDir()));

    /// <summary>网络包下载落盘目录：优先安装包目录，未设置则退 BGI 目录（两者都空时更新流程会拦住提示）。</summary>
    private string EffectiveDownloadDir()
    {
        if (PackageDir.Length > 0) return PackageDir;
        if (BgiDir.Length > 0) return BgiDir;
        return Path.GetTempPath();
    }

    private void SelectNetworkPackageButton_Click(object sender, RoutedEventArgs e)
    {
        ScanPackages(); // 确保网络条目已合并进列表
        var net = (PackageList.ItemsSource as List<BgiUpdatePackage>)?.FirstOrDefault(p => p.Source == BgiPackageSource.Network);
        if (net == null) return;
        PackageList.SelectedItem = net;
        PackageList.ScrollIntoView(net);
    }

    private void SetPackageList(List<BgiUpdatePackage> packages)
    {
        PackageList.ItemsSource = packages;
        UpdatePackageHint(packages);
    }

    private void UpdatePackageHint(List<BgiUpdatePackage> packages)
    {
        PackageListHint.Text = packages.Count == 0
            ? "该目录下没有符合茶包版命名规范的 .7z 安装包（名称必须含 +lcb. 段，仅茶包版可更新）。"
            : $"共 {packages.Count} 个合法茶包版安装包（按版本从新到旧排列）。只列出文件名规范的包（必须含 +lcb. 段），校验不通过的不允许更新。";
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
            SetPackageList(packages);
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
            ScanPackages();
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
        if (BgiDir.Length == 0 || !Directory.Exists(BgiDir))
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
        if (!File.Exists(Path.Combine(BgiDir, "BetterGI.exe")))
        {
            var proceed = MessageBox.Show(this,
                $"目标目录下没有找到 BetterGI.exe：\n{BgiDir}\n\n确定仍要解压到该目录吗？",
                "更新BGI", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (proceed != MessageBoxResult.Yes) return;
        }

        // 网络来源包：解析下载信息（服务器清单可能已刷新，兜底按包名重建下载地址）
        BgiRemoteUpdateInfo? remoteInfo = null;
        if (package.Source == BgiPackageSource.Network)
        {
            remoteInfo = _vm.RemoteBgiUpdate != null && string.Equals(_vm.RemoteBgiUpdate.FileName, package.FileName, StringComparison.OrdinalIgnoreCase)
                ? _vm.RemoteBgiUpdate
                : new BgiRemoteUpdateInfo(package.FileName,
                    BgiRemoteUpdateService.BuildDownloadUrl(_vm.Config?.ServerUrl ?? "", package.FileName),
                    package.LengthBytes > 0 ? package.LengthBytes : null, package.ModifiedTime, package.NameInfo);
        }

        // 运行中的 BGI 会锁住 exe/DLL：先在风险确认里说明，经确认后自动关闭
        var running = BgiPackageUpdateService.FindBgiProcessesUnder(BgiDir);
        string confirmText = "更新有风险，建议先备份数据（用户配置、脚本仓库等重要目录可用下方「排除目录+备份」保护）。\n\n"
            + $"安装包：{package.FileName}\n目标目录：{BgiDir}\n"
            + $"排除目录：{(_excludeDirs.Count == 0 ? "无" : string.Join("、", _excludeDirs))}"
            + (BackupExcludeCheckBox.IsChecked == true && _excludeDirs.Count > 0 ? "\n（更新前先备份排除目录）" : "")
            + (remoteInfo != null ? $"\n该包来自服务器，将先下载到 {EffectiveDownloadDir()}（{remoteInfo.SizeText}）。" : "")
            + (running.Count > 0 ? $"\n\n检测到 {running.Count} 个 BGI 正在运行，点击「确定」将自动关闭后继续。" : "")
            + "\n\n确定继续更新？";
        if (MessageBox.Show(this, confirmText, "更新BGI", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        if (running.Count > 0 && !TryKillBgiProcesses(running))
        {
            MessageBox.Show(this, "BGI 进程关闭失败，已中止更新（请手动关闭后重试）。", "更新BGI", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _updating = true;
        SetBusy(true);
        try
        {
            // 网络来源包：先下载到安装目录（.part 临时文件，成功后改名覆盖），再走本地解压覆盖流程
            var archivePath = package.FullPath;
            if (remoteInfo != null)
            {
                var dlProgress = new Progress<string>(s => StatusText.Text = s);
                archivePath = await BgiRemoteUpdateService.DownloadAsync(remoteInfo, EffectiveDownloadDir(), dlProgress);
            }

            var progress = new Progress<string>(s => StatusText.Text = $"正在更新… {s}");
            var result = await BgiPackageUpdateService.ApplyAsync(archivePath, BgiDir,
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
        // 手动指定的包路径持久化（列表里来源为"指定"的条目）；文件已消失/名字失效的条目在下次打开时自动剔除
        _vm.Config.BgiPickedPackages = (PackageList.ItemsSource as List<BgiUpdatePackage> ?? [])
            .Where(p => p.Source == BgiPackageSource.PickedManually)
            .Select(p => p.FullPath)
            .ToList();
        _vm.SaveConfigToDisk();
    }
}
