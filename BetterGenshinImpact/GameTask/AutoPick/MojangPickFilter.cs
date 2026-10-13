using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BetterGenshinImpact.Core.Config;

namespace BetterGenshinImpact.GameTask.AutoPick;

/// <summary>模板名单独立于公版 OCR；组黑名单、组白名单、全局黑名单依次生效。</summary>
public sealed class MojangPickFilter
{
    public const string BlackListPath = @"User\mojang_black_lists.txt";
    private static readonly object FileGate = new();
    private static readonly HashSet<string> DefaultBlackList = ["调查", "烹饪", "观察点", "七天神像"];
    private HashSet<string> _globalBlackList = [];
    private HashSet<string> _groupWhiteList = [];
    private HashSet<string> _groupBlackList = [];
    private bool _loaded;
    public string? CurrentGroupName { get; private set; }

    public void Init() { _globalBlackList = Load(); _loaded = true; }

    public void SetCurrentGroup(string? groupName, AutoPickGroupConfig? groupConfig)
    {
        CurrentGroupName = groupName;
        _groupWhiteList = groupConfig?.WhiteList ?? [];
        _groupBlackList = groupConfig?.BlackList ?? [];
    }

    public bool ShouldPick(string name)
    {
        if (!_loaded || _groupBlackList.Contains(name)) return false;
        if (_groupWhiteList.Contains(name)) return true;
        return !_globalBlackList.Contains(name) && !DefaultBlackList.Contains(name);
    }

    public void AddToBlackList(IEnumerable<string> names) { Update(names, []); Init(); }
    public static HashSet<string> Load() { lock (FileGate) return LoadCore(); }

    private static HashSet<string> LoadCore()
    {
        var text = Global.ReadAllTextIfExist(BlackListPath);
        return string.IsNullOrEmpty(text) ? []
            : new HashSet<string>(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
    }

    /// <summary>编辑差异合并最新名单，保留自动添加和暂时没有模板的旧条目。</summary>
    public static void Update(IEnumerable<string> additions, IEnumerable<string> removals)
    {
        lock (FileGate)
        {
            var latest = LoadCore();
            latest.ExceptWith(removals);
            latest.UnionWith(additions);
            SaveCore(latest);
        }
        AutoPickTrigger.ReloadLists();
    }

    public static void Save(IEnumerable<string> names)
    {
        lock (FileGate) SaveCore(names);
        AutoPickTrigger.ReloadLists();
    }

    private static void SaveCore(IEnumerable<string> names)
    {
        var path = Global.Absolute(BlackListPath);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, string.Join(Environment.NewLine, names.OrderBy(n => n, StringComparer.Ordinal)), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
