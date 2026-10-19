using System.Collections.ObjectModel;
using System.Windows.Data;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class LogBrowserViewModel
{
    /// <summary>独立于本地日志的远程文件显示视图；筛选不删除源列表。</summary>
    public ListCollectionView RemoteFilesView { get; }

    public ObservableCollection<string> RemoteDateFilterItems { get; } = new()
    {
        "全部日期", DateTime.Today.ToString("yyyy-MM-dd")
    };

    private string _selectedRemoteDateFilter = DateTime.Today.ToString("yyyy-MM-dd");

    public string SelectedRemoteDateFilter
    {
        get => _selectedRemoteDateFilter;
        set
        {
            // 忽略下拉控件初始化时的空选择，保留当前日期。
            if (string.IsNullOrEmpty(value) || !SetProperty(ref _selectedRemoteDateFilter, value)) return;
            RemoteFilesView.Refresh();
            if (SelectedRemoteFile != null && !RemoteFilesView.Contains(SelectedRemoteFile))
                SelectedRemoteFile = null;
            OnPropertyChanged(nameof(RemoteDateFilterHint));
        }
    }

    public string RemoteDateFilterHint => RemoteFiles.Count > 0 && RemoteFilesView.IsEmpty
        ? "所选日期没有日志，可切换日期或选择全部日期查看。" : "";

    private bool MatchesRemoteDateFilter(MemberLogFileDescriptor file) =>
        SelectedRemoteDateFilter == "全部日期" || file.LastWrite.ToString("yyyy-MM-dd") == SelectedRemoteDateFilter;

    private void RebuildRemoteDateFilterItems()
    {
        // 始终保留今天和当前选择；当天无文件仍显示空列表，刷新及换成员不偷偷回退到全部。
        var dates = RemoteFiles.Select(file => file.LastWrite.ToString("yyyy-MM-dd"))
            .Append(DateTime.Today.ToString("yyyy-MM-dd"))
            .Append(SelectedRemoteDateFilter)
            .Where(date => date != "全部日期")
            .Distinct().OrderByDescending(date => date);
        var items = new[] { "全部日期" }.Concat(dates).ToArray();
        // 原位更新，保留当前选中项，避免新列表到达时下拉框短暂显示为空。
        for (var i = 0; i < items.Length; i++)
        {
            if (i < RemoteDateFilterItems.Count && RemoteDateFilterItems[i] == items[i]) continue;
            var existing = RemoteDateFilterItems.IndexOf(items[i]);
            if (existing >= 0) RemoteDateFilterItems.Move(existing, i);
            else RemoteDateFilterItems.Insert(i, items[i]);
        }
        while (RemoteDateFilterItems.Count > items.Length)
            RemoteDateFilterItems.RemoveAt(RemoteDateFilterItems.Count - 1);
        OnPropertyChanged(nameof(RemoteDateFilterHint));
    }
}
