using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.Model;
using BetterGenshinImpact.Service.Interface;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Wpf.Ui;

namespace BetterGenshinImpact.ViewModel.Pages.View;

public partial class AutoFightViewModel : ObservableObject, IViewModel
{
    public AllConfig Config { get; set; }

    /// <summary>
    /// 远程编辑模式（remote-config-group-edit §4.2）：非 null 时跳过 LoadCustomScript 磁盘扫描，
    /// 直接使用注入的（对方机器的）策略清单；下拉刷新也不再回扫本机磁盘。
    /// </summary>
    private readonly IEnumerable<string>? _strategyOverride;
    private readonly IEnumerable<string>? _combatStrategyOverride;

    public AutoFightViewModel(
        IEnumerable<string>? strategyOverride = null,
        IEnumerable<string>? combatStrategyOverride = null)
    {
        Config = TaskContext.Instance().Config;
        _strategyOverride = strategyOverride;
        _combatStrategyOverride = combatStrategyOverride;
        _strategyList = strategyOverride?.ToArray()
                        ?? LoadCustomScript(Global.Absolute(@"User\AutoGeniusInvokation"));
        _combatStrategyList = combatStrategyOverride is null
            ? ["根据队伍自动选择", .. LoadCustomScript(Global.Absolute(@"User\AutoFight"))]
            : ["根据队伍自动选择", .. combatStrategyOverride];
    }

    public AutoFightViewModel(
        AllConfig config,
        IEnumerable<string>? strategyOverride = null,
        IEnumerable<string>? combatStrategyOverride = null)
    {
        Config = config;
        _strategyOverride = strategyOverride;
        _combatStrategyOverride = combatStrategyOverride;
        _strategyList = strategyOverride?.ToArray()
                        ?? LoadCustomScript(Global.Absolute(@"User\AutoGeniusInvokation"));
        _combatStrategyList = combatStrategyOverride is null
            ? ["根据队伍自动选择", .. LoadCustomScript(Global.Absolute(@"User\AutoFight"))]
            : ["根据队伍自动选择", .. combatStrategyOverride];
    }

    [ObservableProperty]
    private string[] _combatStrategyList;

    [ObservableProperty]
    private string[] _strategyList;

    private string[] LoadCustomScript(string folder)
    {
        Directory.CreateDirectory(folder);
        var files = Directory.GetFiles(folder, "*.*",
            SearchOption.AllDirectories);

        // 同时扫描 TXT 与 JSON 策略，均去扩展名显示；路径解析由 AutoFightParam.ResolveStrategyPath 按文件存在性判断
        var count = 0;
        foreach (var file in files)
        {
            var extLower = Path.GetExtension(file).ToLowerInvariant();
            if (extLower == ".txt" || extLower == ".json")
                count++;
        }

        var strategyList = new string[count];
        var idx = 0;
        foreach (var file in files)
        {
            string? ext = null;
            var extLower = Path.GetExtension(file).ToLowerInvariant();
            if (extLower == ".txt")
            {
                ext = ".txt";
            }
            else if (extLower == ".json")
            {
                ext = ".json";
            }

            if (ext != null)
            {
                var relativePath = Path.GetRelativePath(folder, file);
                var strategyName = Path.ChangeExtension(relativePath, null);
                if (strategyName.StartsWith('\\') || strategyName.StartsWith('/'))
                {
                    strategyName = strategyName[1..];
                }

                strategyList[idx++] = strategyName;
            }
        }

        return strategyList;
    }

    [RelayCommand]
    public void OnStrategyDropDownOpened(string type)
    {
        // 远程编辑模式：策略清单来自对方机器，不回扫本机磁盘
        if (_strategyOverride != null || _combatStrategyOverride != null)
        {
            return;
        }

        switch (type)
        {
            case "Combat":
                CombatStrategyList = ["根据队伍自动选择", .. LoadCustomScript(Global.Absolute(@"User\AutoFight"))];
                break;

            case "GeniusInvocation":
                StrategyList = LoadCustomScript(Global.Absolute(@"User\AutoGeniusInvokation"));
                break;
        }
    }

    [RelayCommand]
    public void OnOpenLocalScriptRepo()
    {
        Config.ScriptConfig.ScriptRepoHintDotVisible = false;
        ScriptRepoUpdater.Instance.OpenScriptRepoWindow();
    }

    [RelayCommand]
    public void OnOpenFightFolder()
    {
        Process.Start("explorer.exe", Global.Absolute(@"User\AutoFight\"));
    }
}