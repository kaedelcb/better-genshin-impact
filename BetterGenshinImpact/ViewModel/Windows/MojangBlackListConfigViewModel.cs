using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.ViewModel.Pages;
using BetterGenshinImpact.GameTask.AutoPick;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;

namespace BetterGenshinImpact.ViewModel.Windows;

public partial class MojangItemViewModel : ObservableObject
{
    public required string Name { get; init; }

    public required string Color { get; init; }

    public int ColorIndex { get; init; }

    /// <summary>是否命中当前编辑的名单（勾选语义由所在列表上下文决定：黑名单/白名单）。</summary>
    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>
/// 单个名单的编辑上下文（列表 + 搜索/筛选 + 全选/清除），供本体模板拾取名单窗口的每个页签使用。
/// </summary>
public partial class MojangListViewModel : ObservableObject
{
    /// <summary>颜色分组展示顺序，与莫版模板颜色一致</summary>
    private static readonly string[] ColorOrder = ["灰", "绿", "蓝", "紫", "白", "未收录"];

    /// <summary>批量勾选/取消期间挂起视图刷新，避免逐项触发 ItemsView.Refresh。</summary>
    private bool _suppressRefresh;

    public string[] StatusFilters { get; } = ["全部", "已勾选", "未勾选"];

    public string[] ColorFilters { get; } = ["全部", "灰", "绿", "蓝", "紫", "白", "未收录"];

    public ObservableCollection<MojangItemViewModel> Items { get; } = [];

    public ICollectionView ItemsView { get; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _statusFilter = "全部";

    [ObservableProperty]
    private string _colorFilter = "全部";

    public MojangListViewModel(IEnumerable<MojangTemplateInfo> templates, IEnumerable<string> targetNames)
    {
        var checkedNames = new HashSet<string>(targetNames, StringComparer.Ordinal);
        foreach (var info in templates)
        {
            var item = new MojangItemViewModel
            {
                Name = info.Name,
                Color = info.Color,
                ColorIndex = System.Array.IndexOf(ColorOrder, info.Color),
                IsChecked = checkedNames.Contains(info.Name),
            };
            item.PropertyChanged += OnItemPropertyChanged;
            Items.Add(item);
        }

        // 模板被移除或尚未收录时，旧名单项仍可见且保持原勾选状态。
        var displayedNames = Items.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in checkedNames.Except(displayedNames).OrderBy(name => name, StringComparer.Ordinal))
        {
            var item = new MojangItemViewModel
            {
                Name = name,
                Color = "未收录",
                ColorIndex = ColorOrder.Length - 1,
                IsChecked = true,
            };
            item.PropertyChanged += OnItemPropertyChanged;
            Items.Add(item);
        }

        ItemsView = new ListCollectionView(Items)
        {
            Filter = FilterItem,
            CustomSort = new MojangItemComparer(),
        };
        ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MojangItemViewModel.Color)));
    }

    /// <summary>当前勾选的交互名集合（用于保存）。</summary>
    public HashSet<string> GetCheckedNames()
    {
        return new HashSet<string>(Items.Where(i => i.IsChecked).Select(i => i.Name));
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_suppressRefresh && e.PropertyName == nameof(MojangItemViewModel.IsChecked))
        {
            ItemsView.Refresh();
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        ItemsView.Refresh();
    }

    partial void OnStatusFilterChanged(string value)
    {
        ItemsView.Refresh();
    }

    partial void OnColorFilterChanged(string value)
    {
        ItemsView.Refresh();
    }

    private bool FilterItem(object item)
    {
        if (item is not MojangItemViewModel m)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText)
            && !m.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (StatusFilter == "已勾选" && !m.IsChecked)
        {
            return false;
        }

        if (StatusFilter == "未勾选" && m.IsChecked)
        {
            return false;
        }

        if (ColorFilter != "全部" && m.Color != ColorFilter)
        {
            return false;
        }

        return true;
    }

    [RelayCommand]
    private void SelectAll()
    {
        _suppressRefresh = true;
        try
        {
            foreach (var item in ItemsView.Cast<MojangItemViewModel>().ToList())
            {
                item.IsChecked = true;
            }
        }
        finally
        {
            _suppressRefresh = false;
        }

        ItemsView.Refresh();
    }

    [RelayCommand]
    private void ClearAll()
    {
        _suppressRefresh = true;
        try
        {
            foreach (var item in ItemsView.Cast<MojangItemViewModel>().ToList())
            {
                item.IsChecked = false;
            }
        }
        finally
        {
            _suppressRefresh = false;
        }

        ItemsView.Refresh();
    }

    private sealed class MojangItemComparer : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = (MojangItemViewModel)x!;
            var b = (MojangItemViewModel)y!;
            var c = a.ColorIndex.CompareTo(b.ColorIndex);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        }
    }
}

/// <summary>
/// 本体模板拾取名单配置窗口。
/// 全局作用域（从触发器设置页打开）：仅编辑全局黑名单；
/// 配置组作用域（从配置组打开）：编辑该配置组的白名单/黑名单。
/// 判定优先级：配置组黑名单 > 配置组白名单 > 全局黑名单 > 有图即拾取。
/// 背包满自动加入的是全局黑名单。
/// </summary>
public partial class MojangBlackListConfigViewModel : AutoPickConfigWindowViewModelBase
{
    private readonly ScriptGroup? _group;
    private readonly bool _isRemoteEdit;
    private readonly HashSet<string> _initialGlobalBlackList;

    /// <summary>全局黑名单页签（全局作用域时为唯一页签）。</summary>
    public MojangListViewModel GlobalList { get; }

    /// <summary>配置组白名单页签（仅配置组作用域显示）。</summary>
    public MojangListViewModel GroupWhiteList { get; }

    /// <summary>配置组黑名单页签（仅配置组作用域显示）。</summary>
    public MojangListViewModel GroupBlackList { get; }

    /// <summary>是否全局作用域（从设置页打开）。</summary>
    public bool IsGlobalScope => _group is null;

    /// <summary>是否配置组作用域（从配置组打开）。</summary>
    public bool IsGroupScope => _group is not null;

    /// <summary>当前配置组名。</summary>
    public string? GroupName => _group?.Name;

    public int InitialTabIndex => IsGlobalScope ? 0 : 1;

    public string SaveHint => _isRemoteEdit
        ? "仅编辑远程名单；随远程配置的“保存并回传”提交。"
        : "保存后刷新本机拾取名单。";

    public MojangBlackListConfigViewModel(
        ScriptGroup? group = null,
        bool isRemoteEdit = false,
        IReadOnlyList<MojangTemplateInfo>? templates = null)
    {
        if (isRemoteEdit && group is null)
        {
            throw new ArgumentException("远程名单编辑必须指定配置组", nameof(group));
        }
        _group = group;
        _isRemoteEdit = isRemoteEdit;

        // 测试可注入纯内存目录；正式窗口未传入时仍加载真实模板。
        var templateInfos = templates ?? (MojangMatch.IsLoaded
            ? MojangMatch.Instance.GetTemplateInfos()
            : Task.Run(() => MojangMatch.Instance.GetTemplateInfos()).GetAwaiter().GetResult());

        _initialGlobalBlackList = IsGlobalScope ? MojangPickFilter.Load() : [];
        var groupWhite = group?.Config.AutoPickConfig.WhiteList;
        var groupBlack = group?.Config.AutoPickConfig.BlackList;

        GlobalList = new MojangListViewModel(templateInfos, _initialGlobalBlackList);
        GroupWhiteList = new MojangListViewModel(templateInfos, groupWhite ?? []);
        GroupBlackList = new MojangListViewModel(templateInfos, groupBlack ?? []);
    }

    [RelayCommand]
    private void Save()
    {
        SaveAndClose(() =>
        {
            if (IsGlobalScope)
            {
                // 合并打开窗口后产生的新名单，只提交用户本次明确增删的项目。
                var checkedNames = GlobalList.GetCheckedNames();
                MojangPickFilter.Update(
                    checkedNames.Except(_initialGlobalBlackList).ToArray(),
                    _initialGlobalBlackList.Except(checkedNames).ToArray());
            }
            else
            {
                var config = _group!.Config.AutoPickConfig;
                var previousWhiteList = config.WhiteList;
                var previousBlackList = config.BlackList;
                config.WhiteList = GroupWhiteList.GetCheckedNames();
                config.BlackList = GroupBlackList.GetCheckedNames();
                try
                {
                    if (!_isRemoteEdit)
                    {
                        SaveGroup(_group);
                    }
                }
                catch
                {
                    config.WhiteList = previousWhiteList;
                    config.BlackList = previousBlackList;
                    throw;
                }
            }
        });
    }

    /// <summary>保存配置组 JSON（与 ScriptService 保存配置组方式一致）。</summary>
    private static void SaveGroup(ScriptGroup group)
    {
        var scriptControl = App.GetService<ScriptControlViewModel>();
        if (scriptControl is not null)
        {
            if (!scriptControl.TryWriteScriptGroupToDisk(group, out var error))
            {
                throw new IOException(error ?? "保存配置组失败");
            }
            return;
        }

        group.WriteToFileAtomically(Path.Combine(Global.Absolute(@"User\ScriptGroup"), $"{group.Name}.json"));
    }
}
