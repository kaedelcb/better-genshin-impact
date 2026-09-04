using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>
/// 日志浏览 Tab 的 ViewModel（P2 / F2）。
/// 文件列表（BGI 日志 / 助手日志分组）+ 分块查看（滚动到边界按需加载相邻块）
/// + 关键字/正则搜索跳转 + 导出（原样复制 / 筛选结果 .log/.csv）。
/// 所有磁盘操作放后台线程，结果经 Dispatcher 回 UI。
/// </summary>
public sealed class LogBrowserViewModel : ViewModelBase
{
    /// <summary>查看区最多加载行数（超出后从另一端裁剪，保持内存可控）。</summary>
    private const int MaxLoadedLines = 6000;

    private readonly LogFileBrowser _browser;
    /// <summary>并发守卫/过期令牌：每次加载自增，后台任务回贴时校验，丢弃过期结果。</summary>
    private int _loadTicket;
    private CancellationTokenSource? _searchCts;

    public LogBrowserViewModel(LogFileBrowser browser)
    {
        _browser = browser;
    }

    // ========== 文件列表 ==========

    public ObservableCollection<LogFileItem> Files { get; } = new();

    private LogFileItem? _selectedFile;
    public LogFileItem? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value) && value != null)
                LoadInitial(value);
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
        if (selectedPath != null)
        {
            var again = Files.FirstOrDefault(f => f.FullPath == selectedPath);
            if (again != null) SelectedFile = again;
        }
        // 实例数后台扫描（大文件耗时，不阻塞列表显示）
        var snapshot = Files.ToList();
        Task.Run(() =>
        {
            foreach (var f in snapshot)
            {
                try
                {
                    var count = _browser.CountInstances(f.FullPath);
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        f.InstanceCount = count;
                        var idx = Files.IndexOf(f);
                        if (idx >= 0)
                        {
                            var keep = SelectedFile?.FullPath;
                            Files[idx] = f; // 触发列表刷新
                            if (keep != null && SelectedFile?.FullPath != keep)
                                SelectedFile = Files.FirstOrDefault(x => x.FullPath == keep);
                        }
                    });
                }
                catch { /* 单文件统计失败（占用/删除）不影响其它 */ }
            }
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

    // ========== 查看区（分块加载） ==========

    /// <summary>查看区行（带文件偏移）。</summary>
    public ObservableCollection<LogLineItem> Lines { get; } = new();

    private long _viewStart;
    private long _viewEnd;
    private bool _reachedStart;
    private bool _reachedEnd;

    private LogLineItem? _selectedLine;
    /// <summary>当前选中行（搜索/跳转高亮）。</summary>
    public LogLineItem? SelectedLine
    {
        get => _selectedLine;
        set => SetProperty(ref _selectedLine, value);
    }

    /// <summary>请求视图把选中行滚动到可见（跳转/搜索后触发）。</summary>
    public event Action? ScrollToSelectionRequested;

    private string _viewStatus = "请选择左侧日志文件";
    public string ViewStatus { get => _viewStatus; set => SetProperty(ref _viewStatus, value); }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }

    /// <summary>选中文件：从文件尾加载最近一块（排障场景先看最新）。</summary>
    private void LoadInitial(LogFileItem file)
    {
        var ticket = ++_loadTicket;
        IsLoading = true;
        ViewStatus = $"正在加载 {file.Name} …";
        Task.Run(() =>
        {
            try
            {
                var chunk = _browser.ReadChunkBackward(file.FullPath, new FileInfo(file.FullPath).Length);
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (ticket != _loadTicket) return;
                    ApplyChunk(chunk, replace: true, prepend: false);
                    _reachedEnd = true;
                    ViewStatus = $"{file.Name} · {file.SizeText} · 已加载 {Lines.Count} 行（最新块）";
                    IsLoading = false;
                    ScrollToSelectionRequested?.Invoke();
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (ticket != _loadTicket) return;
                    ViewStatus = $"读取失败: {ex.Message}";
                    IsLoading = false;
                });
            }
        });
    }

    /// <summary>滚动接近顶部时加载上一块。</summary>
    public void LoadOlder()
    {
        if (_selectedFile == null || _reachedStart || IsLoading) return;
        var file = _selectedFile;
        var ticket = ++_loadTicket;
        IsLoading = true;
        Task.Run(() =>
        {
            try
            {
                var chunk = _browser.ReadChunkBackward(file.FullPath, _viewStart);
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (ticket != _loadTicket) { IsLoading = false; return; }
                    ApplyChunk(chunk, replace: false, prepend: true);
                    IsLoading = false;
                });
            }
            catch { Application.Current.Dispatcher.BeginInvoke(() => IsLoading = false); }
        });
    }

    /// <summary>滚动接近底部时加载下一块（历史文件翻到尾部时）。</summary>
    public void LoadNewer()
    {
        if (_selectedFile == null || _reachedEnd || IsLoading) return;
        var file = _selectedFile;
        var ticket = ++_loadTicket;
        IsLoading = true;
        Task.Run(() =>
        {
            try
            {
                var chunk = _browser.ReadChunkForward(file.FullPath, _viewEnd);
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (ticket != _loadTicket) { IsLoading = false; return; }
                    ApplyChunk(chunk, replace: false, prepend: false);
                    IsLoading = false;
                });
            }
            catch { Application.Current.Dispatcher.BeginInvoke(() => IsLoading = false); }
        });
    }

    /// <summary>应用一块内容：replace=整页替换；prepend=true 前插 / false 追加；超容量从另一端裁剪。</summary>
    private void ApplyChunk(LogChunk chunk, bool replace, bool prepend)
    {
        if (replace)
        {
            Lines.Clear();
            foreach (var l in chunk.Lines) Lines.Add(l);
            _viewStart = chunk.StartOffset;
            _viewEnd = chunk.EndOffset;
            _reachedStart = chunk.ReachedStart;
            _reachedEnd = chunk.ReachedEnd;
            return;
        }

        if (chunk.Lines.Count == 0)
        {
            if (prepend) _reachedStart = true; else _reachedEnd = true;
            return;
        }

        if (prepend)
        {
            for (var i = chunk.Lines.Count - 1; i >= 0; i--) Lines.Insert(0, chunk.Lines[i]);
            _viewStart = chunk.StartOffset;
            _reachedStart = chunk.ReachedStart;
            // 裁剪尾部
            while (Lines.Count > MaxLoadedLines)
            {
                Lines.RemoveAt(Lines.Count - 1);
                _reachedEnd = false;
            }
            _viewEnd = Lines.Count > 0 ? EndOffsetOf(Lines.Count - 1) : _viewStart;
        }
        else
        {
            foreach (var l in chunk.Lines) Lines.Add(l);
            _viewEnd = chunk.EndOffset;
            _reachedEnd = chunk.ReachedEnd;
            while (Lines.Count > MaxLoadedLines)
            {
                Lines.RemoveAt(0);
                _reachedStart = false;
            }
            _viewStart = Lines.Count > 0 ? Lines[0].Offset : _viewEnd;
        }
    }

    /// <summary>第 index 行的结束偏移（下一行起始或 _viewEnd）。</summary>
    private long EndOffsetOf(int index) =>
        index + 1 < Lines.Count ? Lines[index + 1].Offset : _viewEnd;

    // ========== 跳转 ==========

    private string _jumpLineText = "";
    /// <summary>跳转目标行号（从 1 起）。</summary>
    public string JumpLineText { get => _jumpLineText; set => SetProperty(ref _jumpLineText, value); }

    public RelayCommand JumpToLineCommand => new(_ =>
    {
        if (_selectedFile == null) return;
        if (!long.TryParse(JumpLineText, out var lineNo) || lineNo < 1) return;
        var file = _selectedFile;
        var ticket = ++_loadTicket;
        IsLoading = true;
        ViewStatus = $"正在定位到第 {lineNo} 行…";
        Task.Run(() =>
        {
            try
            {
                var offset = _browser.FindLineOffset(file.FullPath, lineNo);
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    IsLoading = false;
                    if (offset < 0) { ViewStatus = $"行号 {lineNo} 超出文件范围"; return; }
                    JumpTo(file.FullPath, offset);
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                { IsLoading = false; ViewStatus = $"跳转失败: {ex.Message}"; });
            }
        });
    });

    /// <summary>定位到指定文件的指定偏移（异常记录/搜索结果跳转共用入口）。</summary>
    public void JumpTo(string filePath, long offset)
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
                Group = fi.Name.StartsWith("better-genshin-impact") ? "BGI 日志" : "助手日志",
                LastWriteTime = fi.LastWriteTime, Length = fi.Length
            };
            Files.Add(item);
        }
        if (SelectedFile?.FullPath != filePath) _selectedFile = item; // 不走 setter，避免触发 LoadInitial
        OnPropertyChanged(nameof(SelectedFile));

        var ticket = ++_loadTicket;
        IsLoading = true;
        Task.Run(() =>
        {
            try
            {
                // 以目标偏移为中心加载一块（向前退半块，让目标行处于视图中间）
                var from = Math.Max(0, offset - LogFileBrowser.ChunkBytes / 2);
                var chunk = _browser.ReadChunkForward(filePath, from);
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (ticket != _loadTicket) { IsLoading = false; return; }
                    ApplyChunk(chunk, replace: true, prepend: false);
                    IsLoading = false;
                    ViewStatus = $"{item.Name} · 已定位到偏移 {offset}";
                    // 选中目标行（不大于 offset 的最后一行）
                    LogLineItem? target = null;
                    foreach (var l in Lines)
                    {
                        if (l.Offset <= offset) target = l;
                        else break;
                    }
                    SelectedLine = target;
                    ScrollToSelectionRequested?.Invoke();
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                { IsLoading = false; ViewStatus = $"定位失败: {ex.Message}"; });
            }
        });
    }

    // ========== 搜索 ==========

    private string _searchText = "";
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }

    private bool _isRegex;
    /// <summary>搜索模式：false=关键字（不区分大小写），true=正则。</summary>
    public bool IsRegex { get => _isRegex; set => SetProperty(ref _isRegex, value); }

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
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        var file = _selectedFile;
        var pattern = SearchText;
        var isRegex = IsRegex;
        IsLoading = true;
        ViewStatus = $"正在搜索 \"{pattern}\" …";
        Task.Run(() =>
        {
            try
            {
                var results = _browser.Search(file.FullPath, pattern, isRegex, ct);
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
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                { IsLoading = false; ViewStatus = $"搜索失败: {ex.Message}"; });
            }
        }, ct);
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
