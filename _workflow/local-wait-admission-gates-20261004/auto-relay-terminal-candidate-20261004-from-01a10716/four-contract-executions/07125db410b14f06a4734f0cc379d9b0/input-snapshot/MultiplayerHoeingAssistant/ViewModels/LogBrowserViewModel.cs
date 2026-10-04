using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using MultiplayerHoeingAssistant.Helpers;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>
/// 日志浏览 Tab 的 ViewModel（P2 / F2）。
/// 文件列表（本机 BGI / 本机助手 / 已下载成员 / 外部导入四分组）+ 整文件全量视图（「记事本式」：一次读入内存，
/// 虚拟化 ListBox 承载，自由滚动不分块加载）+ 按级别多选筛选（错误/警告/信息/Debug 四勾选框，ERR/WRN/INF/DBG）
/// + 按时间范围搜索（自动定位到起始时间点）
/// + 关键字/正则搜索（结果点击精确跳转行）+ 导出（原样复制 / 筛选结果 .log/.csv）。
/// 磁盘读取放后台线程，结果经 Dispatcher 回 UI；搜索/定位全部在内存行列表上完成。
/// </summary>
public sealed class LogBrowserViewModel : ViewModelBase
{
    private readonly LogFileBrowser _browser;
    private readonly MainViewModel _mainVm;
    /// <summary>并发守卫/过期令牌：每次加载自增，后台任务回贴时校验，丢弃过期结果。</summary>
    private int _loadTicket;
    private CancellationTokenSource? _searchCts;

    public LogBrowserViewModel(LogFileBrowser browser, MainViewModel mainVm)
    {
        _browser = browser;
        _mainVm = mainVm;
        // 分组视图：本机 BGI / 本机助手 / 已下载成员 三组分组头显示（顺序由 EnumerateFiles 排序保证）
        FilesView = (ListCollectionView)CollectionViewSource.GetDefaultView(Files);
        FilesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LogFileItem.Group)));
        // 按日期筛选（下拉在「日志文件」标题旁）：只过滤显示，不动 Files 集合本身
        FilesView.Filter = obj => obj is LogFileItem f && MatchesDateFilter(f);
    }

    // ========== 文件列表 ==========

    public ObservableCollection<LogFileItem> Files { get; } = new();

    /// <summary>带分组头的文件列表视图（XAML 绑定此属性而非 Files）。</summary>
    public ListCollectionView FilesView { get; }

    private string _fileListHintText = "";
    /// <summary>文件列表为空时的提示（空串=不显示）。</summary>
    public string FileListHintText { get => _fileListHintText; set => SetProperty(ref _fileListHintText, value); }

    // ========== 按日期筛选（文件列表） ==========

    /// <summary>日期筛选项（"全部日期" + 文件最后修改日期去重倒序）。RefreshFiles 时重建。</summary>
    public ObservableCollection<string> DateFilterItems { get; } = new() { "全部日期" };

    private string _selectedDateFilter = "全部日期";
    /// <summary>文件列表按日期筛选（按文件最后修改日期）。切换时被筛掉的选中文件改选为筛选结果的第一个。</summary>
    public string SelectedDateFilter
    {
        get => _selectedDateFilter;
        set
        {
            if (!SetProperty(ref _selectedDateFilter, value)) return;
            FilesView.Refresh();
            if (_selectedFile == null || !MatchesDateFilter(_selectedFile))
                SelectedFile = FilesView.Cast<LogFileItem>().FirstOrDefault();
        }
    }

    private bool MatchesDateFilter(LogFileItem f) =>
        SelectedDateFilter == "全部日期" || f.LastWriteTime.ToString("yyyy-MM-dd") == SelectedDateFilter;

    /// <summary>首次重建日期下拉时默认选中"今天"（当天无文件则留"全部日期"）；之后刷新保留用户选择。</summary>
    private bool _dateFilterInitialized;

    /// <summary>按当前文件集合重建日期下拉（首次默认今天；之后保留原选中，失效回退"全部日期"）。</summary>
    private void RebuildDateFilterItems()
    {
        var keep = SelectedDateFilter;
        DateFilterItems.Clear();
        DateFilterItems.Add("全部日期");
        foreach (var d in Files.Select(f => f.LastWriteTime.ToString("yyyy-MM-dd")).Distinct().OrderByDescending(d => d))
            DateFilterItems.Add(d);
        // 赋值触发 Filter 刷新；keep 失效时回退"全部日期"（不会隐藏任何文件，安全）
        if (!_dateFilterInitialized)
        {
            _dateFilterInitialized = true;
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            SelectedDateFilter = DateFilterItems.Contains(today) ? today : "全部日期";
            return;
        }
        SelectedDateFilter = DateFilterItems.Contains(keep) ? keep : "全部日期";
    }

    private LogFileItem? _selectedFile;
    public LogFileItem? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value) && value != null)
                LoadInitial(value);
            NotifyViewerHint();
        }
    }

    /// <summary>刷新文件列表（重新枚举两个数据源；实例数后台补齐）。</summary>
    public RelayCommand RefreshFilesCommand => new(_ => RefreshFiles());

    public void RefreshFiles()
    {
        var selectedPath = SelectedFile?.FullPath;
        var files = _browser.EnumerateFiles();
        Files.Clear();
        foreach (var f in files) Files.Add(f);
        FileListHintText = Files.Count == 0
            ? "未发现本机日志文件：请先在主页「设置」中配置 BGI 路径；也可以在下方下载远程成员的日志。"
            : "";
        RebuildDateFilterItems();
        if (selectedPath != null)
        {
            var again = Files.FirstOrDefault(f => f.FullPath == selectedPath);
            if (again != null && MatchesDateFilter(again)) SelectedFile = again;
            else _selectedFile = null; // 文件消失或被日期筛选滤掉：交给下方默认选择重新挑一个
        }
        // 无选中时默认打开最新文件（本机 BGI 日志在最上面；有日期筛选时取筛选结果的第一个），进来即有内容可看
        if (SelectedFile == null && Files.Count > 0)
            SelectedFile = FilesView.Cast<LogFileItem>().FirstOrDefault() ?? Files[0];
        // 实例数后台扫描（大文件耗时，不阻塞列表显示；结果一次性回贴，避免逐文件
        // BeginInvoke + 集合替换把 Dispatcher 队列淹没——文件多时这是打开页面"卡死"的来源之一）
        var snapshot = Files.ToList();
        Task.Run(() =>
        {
            var counts = new List<(LogFileItem Item, int Count)>(snapshot.Count);
            foreach (var f in snapshot)
            {
                try { counts.Add((f, _browser.CountInstances(f.FullPath))); }
                catch { /* 单文件统计失败（占用/删除）不影响其它 */ }
            }
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                // 逐项 INPC 就地刷新（LogFileItem.InstanceCount 自带变更通知，不再整项替换）
                foreach (var (item, count) in counts)
                {
                    var idx = Files.IndexOf(item);
                    if (idx >= 0) Files[idx].InstanceCount = count;
                }
            });
        });
    }

    /// <summary>打开日志目录（explorer，复用"打开日志目录"思路）。</summary>
    public RelayCommand OpenLogFolderCommand => new(_ =>
    {
        try
        {
            var dir = SelectedFile != null
                ? Path.GetDirectoryName(SelectedFile.FullPath)!
                : LogFileBrowser.AssistantLogDir;
            if (Directory.Exists(dir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = "explorer.exe", Arguments = $"\"{dir}\"", UseShellExecute = true });
        }
        catch { /* 打开失败静默 */ }
    });

    /// <summary>导入外部日志：文件选择框（可多选）→ 复制到 log\imported\（重名覆盖需确认）→ 刷新列表并打开首个导入文件。
    /// 覆盖确认在 UI 线程一次问完，复制放后台线程（大文件不冻 UI，与导出同款）；
    /// 选中已在 imported\ 里的文件时跳过复制直接视为导入。</summary>
    public RelayCommand ImportExternalLogsCommand => new(_ =>
    {
        var dlg = new OpenFileDialog
        {
            Title = "导入外部日志",
            Filter = "日志文件|*.log|所有文件|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true || dlg.FileNames.Length == 0) return;

        string dir;
        try
        {
            dir = Path.Combine(LogFileBrowser.AssistantLogDir, "imported");
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            ViewStatus = $"创建导入目录失败: {ex.Message}";
            return;
        }

        var jobs = new List<(string Src, string Dst)>();
        foreach (var src in dlg.FileNames)
        {
            var dst = Path.Combine(dir, Path.GetFileName(src));
            // 同名已存在：覆盖确认（与远程下载重名处理同款；本工程无 ThemedMessageBox，沿用 MessageBox）
            if (File.Exists(dst)
                && !string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)
                && MessageBox.Show($"已存在 {Path.GetFileName(src)}，覆盖导入？", "导入外部日志",
                       MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                continue;
            jobs.Add((src, dst));
        }
        if (jobs.Count == 0) { ViewStatus = "已取消导入"; return; }

        IsLoading = true;
        ViewStatus = "正在导入外部日志…";
        Task.Run(() =>
        {
            var imported = new List<string>();
            var failed = 0;
            foreach (var (src, dst) in jobs)
            {
                if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
                {
                    imported.Add(dst); // 源文件本就在导入目录：无需复制
                    continue;
                }
                try { _browser.CopyFile(src, dst); imported.Add(dst); }
                catch (Exception ex)
                {
                    failed++;
                    RuntimeLog.WriteLine($"[LogBrowser] 外部日志导入失败 {src}: {ex.Message}");
                }
            }
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                IsLoading = false;
                RefreshFiles();
                var first = imported.Count > 0 ? Files.FirstOrDefault(f => f.FullPath == imported[0]) : null;
                if (first != null) SelectedFile = first; // setter 触发 LoadInitial，直接可看
                ViewStatus = failed > 0
                    ? $"已导入 {imported.Count} 个文件，{failed} 个失败（详见运行日志）"
                    : $"已导入 {imported.Count} 个文件到 log\\imported";
            });
        });
    });

    // ========== 查看区（整文件全量视图） ==========

    private IReadOnlyList<LogLineItem> _lines = Array.Empty<LogLineItem>();
    /// <summary>查看区当前显示行（=_allLines 经级别筛选后的视图；筛选为「全部级别」时就是 _allLines 本身）。
    /// 整体替换 + 一次 PropertyChanged：十几万行逐项 Add 到 ObservableCollection 会触发同量级 CollectionChanged 把 UI 线程淹没。
    /// 搜索/定位/「导出筛选·当前视图」都基于本列表，即筛选后只在筛出的级别行里进行。</summary>
    public IReadOnlyList<LogLineItem> Lines { get => _lines; private set => SetProperty(ref _lines, value); }

    /// <summary>整文件全量行（磁盘加载结果原样保留；Lines 是它经级别筛选后的显示视图）。</summary>
    private IReadOnlyList<LogLineItem> _allLines = Array.Empty<LogLineItem>();

    /// <summary>当前 Lines 所属文件路径（点搜索结果/异常记录跳转时已在内存的文件不重读磁盘）。</summary>
    private string? _loadedPath;
    /// <summary>整载时是否被 64MB 上限截断（状态行文案用；级别筛选切换时状态行要重建）。</summary>
    private bool _loadedTruncated;

    /// <summary>请求视图滚动定位：null=滚到末尾看最新（初次加载/刷新）；否则=滚动+选中+居中该行（Lines 下标）。UI 线程触发。</summary>
    public event Action<int?>? NavigateRequested;

    private string _viewerHintText = "← 在左侧选择日志文件开始浏览；远程成员日志在左下角请求下载";
    /// <summary>查看区空态提示（空串=不显示）。</summary>
    public string ViewerHintText { get => _viewerHintText; set => SetProperty(ref _viewerHintText, value); }

    private void NotifyViewerHint()
    {
        ViewerHintText = _selectedFile == null
            ? "← 在左侧选择日志文件开始浏览；远程成员日志在左下角请求下载"
            : Lines.Count == 0 && !IsLoading
                ? (IsLevelFilterActive && _allLines.Count > 0
                    ? "当前文件没有符合级别筛选的日志行"
                    : "该文件暂无日志内容")
                : "";
    }

    // ========== 按级别筛选（查看区，多选） ==========
    // 四个级别各自一个开关，默认全选；取消勾选即在查看区隐藏该级别。
    // 加载时续行已继承所属事件的级别，堆栈/正文不会被单独滤掉；无级别/未知级别按信息档处理。

    private bool _showError = true;
    /// <summary>查看区显示错误（ERR）级日志。</summary>
    public bool ShowError { get => _showError; set => OnLevelFilterChanged(ref _showError, value); }

    private bool _showWarning = true;
    /// <summary>查看区显示警告（WRN）级日志。</summary>
    public bool ShowWarning { get => _showWarning; set => OnLevelFilterChanged(ref _showWarning, value); }

    private bool _showInfo = true;
    /// <summary>查看区显示信息（INF）级日志。</summary>
    public bool ShowInfo { get => _showInfo; set => OnLevelFilterChanged(ref _showInfo, value); }

    private bool _showDebug = true;
    /// <summary>查看区显示 Debug（DBG）级日志。</summary>
    public bool ShowDebug { get => _showDebug; set => OnLevelFilterChanged(ref _showDebug, value); }

    /// <summary>四个级别开关共用的 setter 逻辑：值变化才重建显示列表与状态行。</summary>
    private void OnLevelFilterChanged(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        if (!SetProperty(ref field, value, name)) return;
        ApplyLevelFilter();
        UpdateViewStatus();
    }

    /// <summary>是否有级别被关掉（false=全选=不过滤）。</summary>
    private bool IsLevelFilterActive => !(ShowError && ShowWarning && ShowInfo && ShowDebug);

    /// <summary>行级别是否通过当前筛选（INF 与无级别/未知级别同行，与 LogLevels.Rank 未知按 INF 一致）。</summary>
    private bool PassesLevelFilter(string level) => level switch
    {
        LogLevels.Err => ShowError,
        LogLevels.Wrn => ShowWarning,
        LogLevels.Dbg => ShowDebug,
        _ => ShowInfo
    };

    /// <summary>按当前级别筛选重建查看区显示列表（纯内存过滤，大文件也是毫秒级，直接在 UI 线程做）。</summary>
    private void ApplyLevelFilter()
    {
        Lines = IsLevelFilterActive ? _allLines.Where(l => PassesLevelFilter(l.Level)).ToList() : _allLines;
        NotifyViewerHint();
    }

    /// <summary>复位为全选（跳转目标被滤掉时调用）；逐个走 setter 会多次重建显示列表，这里直接改字段后统一刷新。</summary>
    private void ResetLevelFilter()
    {
        _showError = _showWarning = _showInfo = _showDebug = true;
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowWarning));
        OnPropertyChanged(nameof(ShowInfo));
        OnPropertyChanged(nameof(ShowDebug));
        ApplyLevelFilter();
        UpdateViewStatus();
    }

    /// <summary>状态行：文件信息 + 行数；级别筛选生效时追加筛选后行数。</summary>
    private void UpdateViewStatus()
    {
        var file = _selectedFile;
        // 选中文件与内存中的加载结果不一致（新文件加载失败等）时不覆盖现有状态行，避免拿旧数据配新文件名
        if (file == null || _loadedPath == null || _loadedPath != file.FullPath) return;
        ViewStatus = (_loadedTruncated
            ? $"{file.Name} · {file.SizeText} 超过 64MB 上限，仅加载尾部 {_allLines.Count:N0} 行"
            : $"{file.Name} · {file.SizeText} · 共 {_allLines.Count:N0} 行")
            + (IsLevelFilterActive ? $" · 级别筛选后 {Lines.Count:N0} 行" : "");
    }

    private string _viewStatus = "请选择左侧日志文件";
    public string ViewStatus { get => _viewStatus; set => SetProperty(ref _viewStatus, value); }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }

    /// <summary>选中文件：后台整文件读入内存，完成后滚到末尾（排障场景先看最新）。</summary>
    private void LoadInitial(LogFileItem file)
    {
        var ticket = ++_loadTicket;
        IsLoading = true;
        ViewStatus = $"正在加载 {file.Name} …";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Task.Run(() =>
        {
            try
            {
                var load = _browser.ReadAllLines(file.FullPath);
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (ticket != _loadTicket) return;
                    ApplyFullLoad(file, load);
                    IsLoading = false;
                    // 慢加载诊断：整载耗时 >800ms 时写运行日志（排查"打开页面卡死"类问题用）
                    if (sw.ElapsedMilliseconds > 800)
                        _mainVm.AddLog($"[诊断] 日志浏览整载 {file.Name}（{file.SizeText}）耗时 {sw.ElapsedMilliseconds}ms，{load.Lines.Count} 行");
                    NavigateRequested?.Invoke(null);
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (ticket != _loadTicket) return;
                    ViewStatus = $"读取失败: {ex.Message}";
                    IsLoading = false;
                    NotifyViewerHint();
                });
            }
        });
    }

    /// <summary>整载结果上屏：留存全量行后按当前级别筛选出显示列表（一次 PropertyChanged），状态行标注截断/筛选。</summary>
    private void ApplyFullLoad(LogFileItem file, FullLogLoad load)
    {
        _allLines = load.Lines;
        _loadedPath = file.FullPath;
        _loadedTruncated = load.Truncated;
        ApplyLevelFilter();
        UpdateViewStatus();
    }

    // ========== 时间范围搜索 ==========

    private string _startTimeText = "00:00:00";
    /// <summary>时间范围搜索·开始时间（HH:mm:ss，由时间选择器填写）。</summary>
    public string StartTimeText { get => _startTimeText; set => SetProperty(ref _startTimeText, value); }

    private string _endTimeText = "23:59:59";
    /// <summary>时间范围搜索·结束时间（HH:mm:ss，由时间选择器填写）。</summary>
    public string EndTimeText { get => _endTimeText; set => SetProperty(ref _endTimeText, value); }

    /// <summary>按时间范围搜索：收集行头时间落在 [开始, 结束] 内的日志行，点击结果跳转到对应位置。</summary>
    public RelayCommand SearchTimeRangeCommand => new(_ => StartTimeRangeSearch());

    private void StartTimeRangeSearch()
    {
        if (_selectedFile == null) return;
        if (!TryParseTimeOfDay(StartTimeText, out var start) || !TryParseTimeOfDay(EndTimeText, out var end))
        {
            ViewStatus = "时间格式无效（HH:mm:ss）";
            return;
        }
        if (end < start)
        {
            ViewStatus = "结束时间早于开始时间，请调整";
            return;
        }
        var lines = Lines;
        if (lines.Count == 0) { ViewStatus = "当前文件暂无内容可搜索"; return; }
        // 时间搜索结果不做关键字高亮（清空高亮词，点击结果只做跳转）
        HighlightPattern = "";
        HighlightIsRegex = false;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        IsLoading = true;
        ViewStatus = $"正在搜索 {start:hh\\:mm\\:ss} ~ {end:hh\\:mm\\:ss} 的日志…";
        Task.Run(() =>
        {
            try
            {
                // 内存扫描（行列表已整载）：无行头时间的续行归属上一条日志的时间
                var results = new List<SearchResultItem>();
                var haveTime = false;
                var current = TimeSpan.Zero;
                var firstIndex = -1; // 第一条时间 ≥ start 的行下标（搜索完视图直接定位到起始时间点）
                for (var i = 0; i < lines.Count; i++)
                {
                    if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                    var text = lines[i].Text;
                    if (LogLineTime.TryGetTimeOfDay(text, out var t)) { current = t; haveTime = true; }
                    if (!haveTime) continue;
                    if (firstIndex < 0 && current >= start) firstIndex = i;
                    if (current < start || current > end) continue;
                    var preview = text.Length > 160 ? text[..160] + "…" : text;
                    results.Add(new SearchResultItem
                    { LineNumber = lines[i].LineNumber, Offset = lines[i].Offset, Preview = preview, FullText = text });
                    if (results.Count >= LogFileBrowser.MaxSearchResults) break;
                }
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    SearchResults.Clear();
                    foreach (var r in results) SearchResults.Add(r);
                    ViewStatus = results.Count >= LogFileBrowser.MaxSearchResults
                        ? $"时间范围内超过 {LogFileBrowser.MaxSearchResults} 条，仅显示前 {LogFileBrowser.MaxSearchResults} 条，点击可跳转"
                        : $"时间范围内共 {results.Count} 条，点击可跳转";
                    IsLoading = false;
                    // 搜「3点~4点」就把上方视图直接定位到 3 点整开始处，不让用户自己翻
                    if (firstIndex >= 0) NavigateRequested?.Invoke(firstIndex);
                });
            }
            catch (OperationCanceledException)
            {
                Application.Current.Dispatcher.BeginInvoke(() => IsLoading = false);
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                { IsLoading = false; ViewStatus = $"时间搜索失败: {ex.Message}"; });
            }
        }, ct);
    }

    /// <summary>解析时间输入："HH:mm" / "HH:mm:ss[.fff]"；含空格的完整日期时间取最后一段。须在当天 0–24 点内。</summary>
    internal static bool TryParseTimeOfDay(string text, out TimeSpan result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var token = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (!TimeSpan.TryParse(token, System.Globalization.CultureInfo.InvariantCulture, out result)) return false;
        if (result < TimeSpan.Zero || result >= TimeSpan.FromDays(1)) { result = default; return false; }
        return true;
    }

    /// <summary>定位到指定文件的指定偏移（异常记录/搜索结果/时间定位共用入口）。
    /// matchedLine 非空时从事件行头向后找该原文行精确定位（BGI 一条事件=行头+多行正文，偏移只指到行头）。
    /// 文件已在内存时直接二分定位（点搜索结果高频路径不重读磁盘）；否则后台整载后定位。</summary>
    public void JumpTo(string filePath, long offset, string? matchedLine = null)
    {
        // 确保文件在列表中并被选中
        var item = Files.FirstOrDefault(f => f.FullPath == filePath);
        if (item == null)
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists) { ViewStatus = $"文件已不存在: {filePath}"; return; }
            item = new LogFileItem
            {
                Name = fi.Name, FullPath = fi.FullName,
                Group = fi.Name.StartsWith("better-genshin-impact") ? "本机 · BGI 日志" : "本机 · 助手日志",
                LastWriteTime = fi.LastWriteTime, Length = fi.Length
            };
            Files.Add(item);
        }
        // 日期筛选可能把目标文件滤掉：跳转前先把筛选复位（"全部日期"不隐藏任何文件，复位安全）
        if (!MatchesDateFilter(item)) SelectedDateFilter = "全部日期";
        if (SelectedFile?.FullPath != filePath)
        {
            _selectedFile = item; // 不走 setter，避免触发 LoadInitial（下面按需自己加载）
            OnPropertyChanged(nameof(SelectedFile));
        }
        NotifyViewerHint();

        // 已在内存中的文件：行内直接定位，不重读磁盘（注意按全量行判空：级别筛选可能把显示列表滤成空）
        if (_loadedPath == filePath && _allLines.Count > 0)
        {
            EnsureTargetVisible(offset);
            NavigateToOffset(item.Name, offset, matchedLine);
            return;
        }

        var ticket = ++_loadTicket;
        IsLoading = true;
        ViewStatus = $"正在加载 {item.Name} …";
        Task.Run(() =>
        {
            try
            {
                var load = _browser.ReadAllLines(filePath);
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (ticket != _loadTicket) { IsLoading = false; return; }
                    ApplyFullLoad(item, load);
                    IsLoading = false;
                    EnsureTargetVisible(offset);
                    NavigateToOffset(item.Name, offset, matchedLine);
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                { IsLoading = false; ViewStatus = $"定位失败: {ex.Message}"; });
            }
        });
    }

    /// <summary>级别筛选可能把跳转目标行滤掉：目标偏移不在当前显示视图时复位为全选（与日期筛选复位同理）。
    /// 搜索结果点击的目标行必在筛选视图内（二分精确命中即返回），不会触发复位丢失筛选上下文。</summary>
    private void EnsureTargetVisible(long offset)
    {
        if (!IsLevelFilterActive) return;
        var lines = Lines;
        int lo = 0, hi = lines.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (lines[mid].Offset == offset) return; // 目标行在当前筛选视图内，保持筛选
            if (lines[mid].Offset < offset) lo = mid + 1;
            else hi = mid - 1;
        }
        ResetLevelFilter();
    }

    /// <summary>按字节偏移定位：Offset 升序二分，取最后一个 Offset ≤ offset 的行，滚动+选中+居中。
    /// matchedLine 非空时，从该行（事件行头）向后扫描正文行（遇下一个行头/超 200 行止），
    /// 找到原文含 matchedLine 的行精确命中——异常记录的关键词往往在正文行而非行头。</summary>
    private void NavigateToOffset(string fileName, long offset, string? matchedLine = null)
    {
        var lines = Lines;
        if (lines.Count == 0) return;
        int lo = 0, hi = lines.Count - 1, target = 0;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (lines[mid].Offset <= offset) { target = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (!string.IsNullOrEmpty(matchedLine))
        {
            var needle = matchedLine.Trim();
            for (var i = target; i < lines.Count && i <= target + 200; i++)
            {
                // 越过下一个行头=出了本条事件，停止（防止误中后面无关日志的同文本行）
                if (i > target && LogLineTime.TryGetTimeOfDay(lines[i].Text, out _)) break;
                if (lines[i].Text.Contains(needle, StringComparison.Ordinal))
                {
                    target = i;
                    break;
                }
            }
        }
        ViewStatus = $"{fileName} · 已定位到第 {lines[target].LineNumber}/{lines.Count} 行";
        NavigateRequested?.Invoke(target);
    }

    // ========== 搜索 ==========

    private string _searchText = "";
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }

    private bool _isRegex;
    /// <summary>搜索模式：false=关键字（不区分大小写），true=正则。</summary>
    public bool IsRegex { get => _isRegex; set => SetProperty(ref _isRegex, value); }

    private string _highlightPattern = "";
    /// <summary>搜索结果高亮词（关键字搜索时=搜索词；时间范围搜索时为空）。</summary>
    public string HighlightPattern { get => _highlightPattern; set => SetProperty(ref _highlightPattern, value); }

    private bool _highlightIsRegex;
    /// <summary>高亮词是否按正则解释（跟随搜索时的模式）。</summary>
    public bool HighlightIsRegex { get => _highlightIsRegex; set => SetProperty(ref _highlightIsRegex, value); }

    public ObservableCollection<SearchResultItem> SearchResults { get; } = new();

    private SearchResultItem? _selectedResult;
    /// <summary>选中搜索结果 → 跳转到对应位置并高亮。</summary>
    public SearchResultItem? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (SetProperty(ref _selectedResult, value) && value != null && _selectedFile != null)
                JumpTo(_selectedFile.FullPath, value.Offset);
        }
    }

    public RelayCommand SearchCommand => new(_ => StartSearch());

    private void StartSearch()
    {
        if (_selectedFile == null || string.IsNullOrWhiteSpace(SearchText)) return;
        if (IsRegex)
        {
            try { _ = new System.Text.RegularExpressions.Regex(SearchText); }
            catch (Exception ex) { ViewStatus = $"正则无效: {ex.Message}"; return; }
        }
        var lines = Lines;
        if (lines.Count == 0) { ViewStatus = "当前文件暂无内容可搜索"; return; }
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        var pattern = SearchText;
        var isRegex = IsRegex;
        // 结果列表与查看区的高亮词跟随本次搜索（正则模式按正则高亮，否则按纯文本关键字高亮；
        // 查看区每行 HighlightTextBlock 绑定此属性，变更即自动重绘可见行）
        HighlightPattern = pattern;
        HighlightIsRegex = isRegex;
        IsLoading = true;
        ViewStatus = $"正在搜索 \"{pattern}\" …";
        Task.Run(() =>
        {
            try
            {
                // 内存扫描（行列表已整载，不碰磁盘）；大文件正则耗时仍在后台线程
                var regex = isRegex
                    ? new System.Text.RegularExpressions.Regex(pattern,
                        System.Text.RegularExpressions.RegexOptions.Compiled
                        | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    : null;
                var results = new List<SearchResultItem>();
                for (var i = 0; i < lines.Count; i++)
                {
                    if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                    var text = lines[i].Text;
                    var hit = regex != null
                        ? regex.IsMatch(text)
                        : text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
                    if (!hit) continue;
                    var preview = text.Length > 160 ? text[..160] + "…" : text;
                    results.Add(new SearchResultItem
                    { LineNumber = lines[i].LineNumber, Offset = lines[i].Offset, Preview = preview, FullText = text });
                    if (results.Count >= LogFileBrowser.MaxSearchResults) break;
                }
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    SearchResults.Clear();
                    foreach (var r in results) SearchResults.Add(r);
                    ViewStatus = results.Count >= LogFileBrowser.MaxSearchResults
                        ? $"匹配超过 {LogFileBrowser.MaxSearchResults} 条，仅显示前 {LogFileBrowser.MaxSearchResults} 条"
                        : $"共 {results.Count} 条匹配";
                    IsLoading = false;
                });
            }
            catch (OperationCanceledException)
            {
                Application.Current.Dispatcher.BeginInvoke(() => IsLoading = false);
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                { IsLoading = false; ViewStatus = $"搜索失败: {ex.Message}"; });
            }
        }, ct);
    }

    // ========== 远程成员日志下载 ==========
    // 协议：RequestMemberLogFiles → MemberLogFileList（按 requestId 认领）；
    //       RequestMemberLogDownload → MemberLogFileChunk（gzip+base64 分块，seq 重组）；
    //       totalChunks=0 且 done=true 是"对方正忙/拒绝/文件超限"标记块。
    // 重组/解压/落盘全部后台线程，进度经 Dispatcher（SignalR 回调本身就在非 UI 线程，先切回 UI 更新状态）。

    /// <summary>列表请求/块超时（秒）：超时提示失败并清理半成品。</summary>
    private const int RemoteTimeoutSeconds = 30;

    private SignalRClient? _hooked;
    /// <summary>待认领的文件列表请求 Id（null=无在途请求）。</summary>
    private string? _pendingListRequestId;
    private DateTime _listRequestedAtUtc;
    /// <summary>在途下载（null=空闲；同时间只允许一个）。</summary>
    private RemoteDownloadState? _download;
    /// <summary>超时巡检（列表请求与下载共用，1s 节拍）。</summary>
    private DispatcherTimer? _remoteTimeoutTimer;

    /// <summary>远程成员下拉项（在线成员，排除自己；由 DodocoViewModel 在成员变化时驱动刷新）。</summary>
    public ObservableCollection<RemoteMemberOption> RemoteMembers { get; } = new();

    private RemoteMemberOption? _selectedRemoteMember;
    public RemoteMemberOption? SelectedRemoteMember
    {
        get => _selectedRemoteMember;
        set => SetProperty(ref _selectedRemoteMember, value);
    }

    /// <summary>选中成员的远程文件列表（点"获取文件列表"后填充）。</summary>
    public ObservableCollection<MemberLogFileDescriptor> RemoteFiles { get; } = new();

    private MemberLogFileDescriptor? _selectedRemoteFile;
    public MemberLogFileDescriptor? SelectedRemoteFile
    {
        get => _selectedRemoteFile;
        set => SetProperty(ref _selectedRemoteFile, value);
    }

    private string _remoteStatus = "";
    /// <summary>远程区状态行（请求/下载进度与结果提示）。</summary>
    public string RemoteStatus { get => _remoteStatus; set => SetProperty(ref _remoteStatus, value); }

    private double _downloadProgress;
    /// <summary>下载进度 0..1（ProgressBar Maximum=1）。</summary>
    public double DownloadProgress { get => _downloadProgress; set => SetProperty(ref _downloadProgress, value); }

    /// <summary>旧服务端不支持远程日志下载（UI 标注"需新版服务端"用；成员刷新时一并刷新绑定）。</summary>
    public bool RemoteDownloadUnsupported => _mainVm.SignalR?.LogFileUnsupported == true;

    /// <summary>重建远程成员下拉（在线且非自己；保留原选中）。UI 线程调用。</summary>
    public void RefreshRemoteMembers()
    {
        var keep = SelectedRemoteMember?.Uid;
        RemoteMembers.Clear();
        // 单机模式：远程下载成员墙留空（不发起任何成员日志文件请求）
        if (!_mainVm.IsStandaloneMode)
            foreach (var m in _mainVm.Members)
            {
                if (!m.Online || m.IsSelf) continue;
                RemoteMembers.Add(new RemoteMemberOption(m.PlayerUid, m.PlayerName));
            }
        SelectedRemoteMember = RemoteMembers.FirstOrDefault(o => o.Uid == keep);
        // 成员刷新顺带刷新"需新版服务端"标注与状态行
        OnPropertyChanged(nameof(RemoteDownloadUnsupported));
        if (RemoteDownloadUnsupported && string.IsNullOrEmpty(RemoteStatus))
            RemoteStatus = "当前服务端不支持远程日志下载（需新版服务端）";
    }

    /// <summary>SignalR 懒绑定（SignalRClient 实例可能晚于本 VM 创建/被重连替换；由 DodocoViewModel 200ms 节拍驱动）。</summary>
    public void EnsureSignalRHooked()
    {
        var client = _mainVm.SignalR;
        if (ReferenceEquals(client, _hooked)) return;
        if (_hooked != null)
        {
            _hooked.OnMemberLogFileList -= HandleFileList;
            _hooked.OnMemberLogFileChunk -= HandleFileChunk;
        }
        _hooked = client;
        if (_hooked != null)
        {
            _hooked.OnMemberLogFileList += HandleFileList;
            _hooked.OnMemberLogFileChunk += HandleFileChunk;
        }
    }

    /// <summary>获取选中成员的远程文件列表。</summary>
    public RelayCommand FetchRemoteFilesCommand => new(_ =>
    {
        var member = SelectedRemoteMember;
        var client = _mainVm.SignalR;
        if (member == null) { RemoteStatus = "请先选择成员"; return; }
        if (client?.IsConnected != true) { RemoteStatus = "未连接到房间"; return; }
        _pendingListRequestId = Guid.NewGuid().ToString("N");
        _listRequestedAtUtc = DateTime.UtcNow;
        RemoteFiles.Clear();
        RemoteStatus = $"正在向 {member.Name} 请求文件列表…";
        EnsureTimeoutTimer();
        _ = client.RequestMemberLogFilesAsync(member.Uid, _pendingListRequestId);
    });

    /// <summary>下载选中的远程文件（重名覆盖需确认；同时间只允许一个下载）。</summary>
    public RelayCommand DownloadRemoteFileCommand => new(_ =>
    {
        var member = SelectedRemoteMember;
        var file = SelectedRemoteFile;
        var client = _mainVm.SignalR;
        if (member == null || file == null) { RemoteStatus = "请先获取文件列表并选择文件"; return; }
        if (client?.IsConnected != true) { RemoteStatus = "未连接到房间"; return; }
        if (_download != null) { RemoteStatus = "已有下载进行中，请等待完成"; return; }
        // 本地复检文件名白名单（与服务端/目标端同款）：列表数据也是跨端来的，落盘前不信任
        if (!MemberLogShareService.FileNameRegex.IsMatch(file.Name))
        {
            RemoteStatus = $"文件名不合白名单，拒绝下载: {file.Name}";
            return;
        }

        // 落盘目标：<助手目录>\log\remote_downloads\{成员名}\{文件名}
        var dir = Path.Combine(LogFileBrowser.AssistantLogDir, "remote_downloads", SanitizeDirName(member.Name));
        string targetPath;
        try
        {
            Directory.CreateDirectory(dir);
            targetPath = Path.Combine(dir, file.Name);
        }
        catch (Exception ex)
        {
            RemoteStatus = $"创建下载目录失败: {ex.Message}";
            return;
        }
        // 重复下载同文件：覆盖确认（本工程无 ThemedMessageBox，沿用项目现有 MessageBox 用法）
        if (File.Exists(targetPath)
            && MessageBox.Show($"已存在 {file.Name}，覆盖下载？", "远程日志下载",
                   MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            RemoteStatus = "已取消（保留已有文件）";
            return;
        }

        var requestId = Guid.NewGuid().ToString("N");
        _download = new RemoteDownloadState
        {
            RequestId = requestId,
            FileName = file.Name,
            MemberName = member.Name,
            TargetPath = targetPath,
            LastProgressAtUtc = DateTime.UtcNow
        };
        DownloadProgress = 0;
        RemoteStatus = $"正在请求下载 {file.Name}…";
        EnsureTimeoutTimer();
        _ = client.RequestMemberLogDownloadAsync(member.Uid, requestId, file.Name);
    });

    /// <summary>文件列表应答（SignalR 线程）：先在本线程判 requestId 归属，非本 VM 请求直接丢，不切 UI。</summary>
    private void HandleFileList(MemberLogFileList list)
    {
        // 回调线程预过滤（string 引用读写原子；UI 线程内再复核一次防竞态）
        if (list.RequestId != _pendingListRequestId) return;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (list.RequestId != _pendingListRequestId) return; // 非本次请求（别的观众/过期应答）
            _pendingListRequestId = null;
            RemoteFiles.Clear();
            foreach (var f in list.Files) RemoteFiles.Add(f);
            RemoteStatus = list.Files.Count == 0
                ? "对方没有可分享的日志文件（或对方关闭了「共享日志文件」）"
                : $"共 {list.Files.Count} 个文件，选中后点「下载」";
            StopTimeoutTimerIfIdle();
        });
    }

    /// <summary>文件分块（SignalR 线程）：先在本线程判 requestId 归属，非本次下载直接丢，不切 UI；
    /// 认领后切 UI 重组，解码后的字节进缓冲，完成后后台落盘。</summary>
    private void HandleFileChunk(MemberLogFileChunk chunk)
    {
        // 回调线程预过滤：服务端已按 requestId 单播，这里是双保险（本地引用读原子，过期引用顶多误判放行一次，
        // UI 线程内会再复核）
        if (_download == null || chunk.RequestId != _download.RequestId) return;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var dl = _download;
            if (dl == null || chunk.RequestId != dl.RequestId) return; // 非本次下载
            dl.LastProgressAtUtc = DateTime.UtcNow;

            // 忙/拒绝/失败标记块（协议：done=true, totalChunks=0）
            if (chunk.TotalChunks == 0)
            {
                FailDownload("对方正忙、拒绝共享或文件超过 200MB 上限");
                return;
            }

            // 首块初始化缓冲；totalChunks 不一致视为异常批直接失败
            if (dl.Chunks == null)
            {
                dl.Chunks = new byte[chunk.TotalChunks][];
                dl.TotalChunks = chunk.TotalChunks;
            }
            else if (dl.TotalChunks != chunk.TotalChunks)
            {
                FailDownload("块总数前后不一致，下载中止");
                return;
            }
            if (chunk.Seq < 0 || chunk.Seq >= dl.TotalChunks) return; // 越界块丢弃

            if (dl.Chunks[chunk.Seq] == null)
            {
                try { dl.Chunks[chunk.Seq] = Convert.FromBase64String(chunk.ChunkBase64); }
                catch { FailDownload("块解码失败，下载中止"); return; }
                dl.Received++;
                dl.ReceivedBytes += dl.Chunks[chunk.Seq]!.Length;
                DownloadProgress = (double)dl.Received / dl.TotalChunks;
                RemoteStatus = $"正在下载 {dl.FileName}：{dl.Received}/{dl.TotalChunks} 块 · {dl.ReceivedBytes / 1024.0:F0} KB";
            }

            if (chunk.Done)
            {
                if (dl.Received == dl.TotalChunks) FinishDownload(dl);
                else FailDownload($"块不完整（{dl.Received}/{dl.TotalChunks}），下载失败");
            }
        });
    }

    /// <summary>全部块到齐：后台拼接 → gzip 解压 → 落盘，完成后刷新列表并直接打开。</summary>
    private void FinishDownload(RemoteDownloadState dl)
    {
        _download = null; // 先清在途标记（落盘期间允许发起新下载）
        StopTimeoutTimerIfIdle();
        RemoteStatus = $"下载完成，正在解压落盘 {dl.FileName}…";
        Task.Run(() =>
        {
            try
            {
                var totalLen = dl.Chunks!.Sum(c => c?.Length ?? 0);
                var compressed = new byte[totalLen];
                var pos = 0;
                foreach (var c in dl.Chunks!)
                {
                    if (c == null) throw new InvalidDataException("存在缺失块");
                    Buffer.BlockCopy(c, 0, compressed, pos, c.Length);
                    pos += c.Length;
                }
                using var input = new MemoryStream(compressed);
                using var gz = new GZipStream(input, CompressionMode.Decompress);
                using var output = new FileStream(dl.TargetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                gz.CopyTo(output);
                output.Flush();

                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    RemoteStatus = $"已下载到 {dl.TargetPath}";
                    DownloadProgress = 1;
                    // 落盘后刷新日志浏览列表（remote_downloads 已纳入枚举），并直接打开下载的文件
                    RefreshFiles();
                    var item = Files.FirstOrDefault(f => f.FullPath == dl.TargetPath);
                    if (item != null) SelectedFile = item; // setter 触发 LoadInitial，直接可看
                });
            }
            catch (Exception ex)
            {
                // 半成品清理
                try { if (File.Exists(dl.TargetPath)) File.Delete(dl.TargetPath); } catch { }
                Application.Current.Dispatcher.BeginInvoke(() =>
                    RemoteStatus = $"落盘失败: {ex.Message}");
            }
        });
    }

    /// <summary>下载失败：提示并清理在途状态（半成品文件只在落盘阶段才创建，这里无需删文件）。</summary>
    private void FailDownload(string reason)
    {
        _download = null;
        StopTimeoutTimerIfIdle();
        DownloadProgress = 0;
        RemoteStatus = $"下载失败：{reason}";
    }

    /// <summary>超时巡检（1s）：列表请求 30s 无应答 / 下载 30s 无新块 → 失败清理。</summary>
    private void EnsureTimeoutTimer()
    {
        if (_remoteTimeoutTimer != null) { _remoteTimeoutTimer.Start(); return; }
        _remoteTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _remoteTimeoutTimer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            if (_pendingListRequestId != null
                && now - _listRequestedAtUtc > TimeSpan.FromSeconds(RemoteTimeoutSeconds))
            {
                _pendingListRequestId = null;
                RemoteStatus = "请求超时（对方可能不在线、关闭了共享或为旧版助手）";
            }
            if (_download != null
                && now - _download.LastProgressAtUtc > TimeSpan.FromSeconds(RemoteTimeoutSeconds))
            {
                FailDownload("超过 30 秒未收到新块（对方可能已断线）");
            }
            StopTimeoutTimerIfIdle();
        };
        _remoteTimeoutTimer.Start();
    }

    private void StopTimeoutTimerIfIdle()
    {
        if (_pendingListRequestId == null && _download == null)
            _remoteTimeoutTimer?.Stop();
    }

    /// <summary>成员名转安全目录名（路径非法字符替换为下划线）。</summary>
    private static string SanitizeDirName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        var s = new string(chars).Trim();
        if (string.IsNullOrEmpty(s)) return "成员";
        // 特判：纯点号序列（"." / ".." / "..."）在 Windows 上有相对路径语义，替换为下划线
        if (s.All(c => c == '.')) return "_";
        return s;
    }

    /// <summary>在途下载状态（UI 线程独占访问，无需锁）。</summary>
    private sealed class RemoteDownloadState
    {
        public string RequestId { get; set; } = "";
        public string FileName { get; set; } = "";
        public string MemberName { get; set; } = "";
        public string TargetPath { get; set; } = "";
        public int TotalChunks { get; set; }
        public int Received { get; set; }
        public long ReceivedBytes { get; set; }
        public byte[]?[]? Chunks { get; set; }
        public DateTime LastProgressAtUtc { get; set; }
    }

    /// <summary>解绑 SignalR 事件并停超时巡检（DodocoViewModel.Dispose 时调用；在途下载随之放弃）。</summary>
    public void Dispose()
    {
        _remoteTimeoutTimer?.Stop();
        _remoteTimeoutTimer = null;
        _download = null;
        _pendingListRequestId = null;
        if (_hooked != null)
        {
            _hooked.OnMemberLogFileList -= HandleFileList;
            _hooked.OnMemberLogFileChunk -= HandleFileChunk;
            _hooked = null;
        }
    }

    // ========== 导出 ==========

    /// <summary>原样导出：共享读复制整个文件（正在写入的当天文件同样可复制）。
    /// 复制放后台线程（中危8）：大文件复制数百 MB 时不再冻结 UI。</summary>
    public RelayCommand ExportRawCommand => new(_ =>
    {
        if (_selectedFile == null || IsLoading) return;
        var dlg = new SaveFileDialog
        {
            Title = "导出日志文件",
            FileName = _selectedFile.Name,
            Filter = "日志文件|*.log|所有文件|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        var src = _selectedFile.FullPath;
        var dst = dlg.FileName;
        IsLoading = true;
        ViewStatus = "正在导出…";
        Task.Run(() =>
        {
            try
            {
                _browser.CopyFile(src, dst);
                Application.Current.Dispatcher.BeginInvoke(() =>
                { IsLoading = false; ViewStatus = $"已导出到 {dst}"; });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                { IsLoading = false; ViewStatus = $"导出失败: {ex.Message}"; });
            }
        });
    });

    /// <summary>筛选结果导出：有搜索结果时导出搜索命中行，否则导出当前已加载视图。</summary>
    public RelayCommand ExportFilteredCommand => new(_ =>
    {
        IEnumerable<string> lines;
        string defaultName;
        if (SearchResults.Count > 0)
        {
            lines = SearchResults.Select(r => r.FullText);
            defaultName = "筛选结果";
        }
        else if (Lines.Count > 0)
        {
            lines = Lines.Select(l => l.Text);
            defaultName = "当前视图";
        }
        else return;

        var dlg = new SaveFileDialog
        {
            Title = "导出筛选结果",
            FileName = $"{defaultName}_{DateTime.Now:yyyyMMdd_HHmmss}",
            Filter = "日志文件|*.log|CSV 文件|*.csv"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _browser.ExportLines(lines, dlg.FileName,
                asCsv: dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
            ViewStatus = $"已导出到 {dlg.FileName}";
        }
        catch (Exception ex)
        {
            ViewStatus = $"导出失败: {ex.Message}";
        }
    });
}
