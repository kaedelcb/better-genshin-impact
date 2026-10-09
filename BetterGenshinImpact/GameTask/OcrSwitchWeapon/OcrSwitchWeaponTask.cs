using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.OcrSwitchWeapon.Data;
using BetterGenshinImpact.GameTask.OcrSwitchWeapon.Recognition;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.OcrSwitchWeapon;

/// <summary>
/// OCR切换武器 - BetterGI 原生 C# 独立任务。
/// 1:1 复刻 JS 脚本 <c>User/JsScript/OCR切换武器/main.js</c> 的流程（坐标 / sleep / 阈值 / 优先级）。
/// 通过 OCR 在「角色 - 武器」界面识别并切换指定角色到指定武器。单机独立任务，不涉及联机。
/// </summary>
public class OcrSwitchWeaponTask : ISoloTask
{
    public string Name => "OCR切换武器";

    private readonly ILogger<OcrSwitchWeaponTask> _logger = App.GetLogger<OcrSwitchWeaponTask>();
    private OcrSwitchWeaponConfig _config = null!;
    private CancellationToken _ct;

    /// <summary>配置组传入的地图追踪配置（接口对齐，本任务暂不使用）。</summary>
    private readonly PathingPartyConfig? _partyConfig;

    /// <summary>配置组传入的独立任务配置覆盖，为 null 时使用全局 OcrSwitchWeaponConfig。</summary>
    private readonly Dictionary<string, object?>? _settingsOverride;

    /// <summary>配置组名称。</summary>
    private readonly string? _groupName;

    // 数据/识别组件
    private CombatAvatarRepository _avatarRepo = null!;
    private WeaponNameRepository _weaponNameRepo = null!;
    private WeaponPositionStore _positionStore = null!;
    private OcrWeaponReader _reader = null!;

    /// <summary>武器类型编码 → 武器类型中文名（R10.2，对齐 JS weaponTypeMap）。</summary>
    private static readonly Dictionary<string, string> WeaponTypeMap = new()
    {
        ["1"] = "单手剑",
        ["11"] = "双手剑",
        ["12"] = "弓箭",
        ["10"] = "法器",
        ["13"] = "长枪",
    };

    public OcrSwitchWeaponTask(PathingPartyConfig? partyConfig = null,
        Dictionary<string, object?>? settings = null, string? groupName = null)
    {
        _partyConfig = partyConfig;
        _settingsOverride = settings;
        _groupName = groupName;
    }

    public async Task Start(CancellationToken ct)
    {
        _ct = ct;
        _config = TaskContext.Instance().Config.OcrSwitchWeaponConfig;

        // 配置组传入覆盖时，在全局配置深拷贝上应用，避免污染全局状态
        if (_settingsOverride is { Count: > 0 })
        {
            var json = JsonSerializer.Serialize(_config);
            _config = JsonSerializer.Deserialize<OcrSwitchWeaponConfig>(json) ?? _config;
            ApplySettingsOverride();
        }

        if (!string.IsNullOrEmpty(_groupName))
        {
            _logger.LogInformation("OCR切换武器任务启动 [配置组: {Group}]", _groupName);
        }
        else
        {
            _logger.LogInformation("OCR切换武器任务启动");
        }

        try
        {
            // 资产加载（R10.4：任一缺失/解析失败 → LogError 终止，不抛出未处理异常）
            if (!LoadResources()) return;
            await CharacterPathAsync();
        }
        catch (OperationCanceledException)
        {
            // R1.3：取消必须透传，停止后续操作
            _logger.LogInformation("OCR切换武器任务被取消");
            throw;
        }
        catch (Exception ex)
        {
            // R1.5：非取消异常记结构化日志后结束，不向上抛导致配置组中断
            _logger.LogError(ex, "OCR切换武器任务异常终止");
        }
    }

    /// <summary>
    /// 加载数据资产（R10.4）：任一缺失或解析失败 → LogError 后返回 false，由 Start 终止任务。
    /// WeaponPositionStore.Load 内部已消化异常（不抛），与另两个仓库放一起无副作用。
    /// </summary>
    private bool LoadResources()
    {
        try
        {
            _avatarRepo = CombatAvatarRepository.Load();
            _weaponNameRepo = WeaponNameRepository.Load();
            _positionStore = WeaponPositionStore.Load();
            _reader = new OcrWeaponReader(_ct);
            return true;
        }
        catch (Exception ex)
        {
            // 资产缺失/解析失败属不可恢复的前置错误，记 LogError 后终止本次任务（不抛未处理异常）
            _logger.LogError(ex, "加载数据资产失败");
            return false;
        }
    }

    /// <summary>
    /// 将配置组传入的覆盖值应用到当前配置。键名与 JS settings.json 的 name 一致（注意大小写）。
    /// </summary>
    private void ApplySettingsOverride()
    {
        if (_settingsOverride == null) return;

        T Get<T>(string key, T fallback)
        {
            if (_settingsOverride.TryGetValue(key, out var val) && val != null)
            {
                try
                {
                    // 处理 JsonElement 类型（System.Text.Json 反序列化 Dictionary<string, object?> 时的默认类型）
                    if (val is JsonElement jsonElement)
                    {
                        val = ConvertJsonElement(jsonElement);
                        if (val == null) return fallback;
                    }

                    // 处理 double -> int 转换时的 NaN/Infinity 和精度问题
                    if (typeof(T) == typeof(int) && val is double d)
                    {
                        if (double.IsNaN(d) || double.IsInfinity(d))
                            return fallback;
                        if (Math.Abs(d - Math.Round(d)) < 1e-9)
                            return (T)(object)Convert.ToInt32(Math.Round(d));
                        return (T)(object)Convert.ToInt32(d);
                    }
                    return (T)Convert.ChangeType(val, typeof(T));
                }
                catch { return fallback; }
            }
            return fallback;
        }

        // 将 JsonElement 转换为实际的值类型
        static object? ConvertJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => element.GetRawText()
            };
        }

        _config.Character = Get("Character", _config.Character);
        _config.Weapon = Get("Weapon", _config.Weapon);
        _config.Element = Get("Element", _config.Element);
        _config.QuickMode = Get("quickMode", _config.QuickMode);
        _config.GridPosition = Get("gridPosition", _config.GridPosition);
        _config.PageScrollCount = Get("pageScrollCount", _config.PageScrollCount);
    }

    /// <summary>
    /// 配置组可配置参数定义，顺序严格与 JS settings.json 一致：
    /// Character → Weapon → Element → quickMode → gridPosition → pageScrollCount。
    /// </summary>
    public static List<SoloTaskSettingItem> GetSettingDefinitions()
    {
        var config = TaskContext.Instance().Config.OcrSwitchWeaponConfig;
        return new List<SoloTaskSettingItem>
        {
            new() { Name = "Character", Label = "角色（如 草神、奶奶)", Type = "text", DefaultValue = config.Character },
            new() { Name = "Weapon", Label = "武器（如 金珀、祭礼)", Type = "text", DefaultValue = config.Weapon },
            new() { Name = "Element", Label = "选元素缩范围 非必填\n元素（默认'物'=不选)", Type = "select",
                DefaultValue = config.Element, Options = new() { "物", "火", "水", "草", "雷", "风", "冰", "岩" } },
            new() { Name = "quickMode", Label = "快速模式", Type = "bool", DefaultValue = config.QuickMode },
            new() { Name = "gridPosition", Label = "武器位置行列(非必要不指定)\n例(73)为第七行第三列", Type = "text", DefaultValue = config.GridPosition },
            new() { Name = "pageScrollCount", Label = "默认：四行武器为一页\n最大滑页次数（默认2)", Type = "text", DefaultValue = config.PageScrollCount },
        };
    }

    // ====================== 输入封装（1080P 基准坐标，自动缩放） ======================

    /// <summary>点击 1080P 基准坐标（移动 + 左键单击）。</summary>
    private static void Click(double x, double y) => GameCaptureRegion.GameRegion1080PPosClick(x, y);

    /// <summary>移动到 1080P 基准坐标。</summary>
    private static void MoveMouseTo(double x, double y) => GameCaptureRegion.GameRegion1080PPosMove(x, y);

    /// <summary>相对移动鼠标。</summary>
    private static void MoveMouseBy(int dx, int dy) => InputHub.Foreground.Mouse.MoveMouseBy(dx, dy);

    private static void LeftButtonDown() => InputHub.Foreground.Mouse.LeftButtonDown();

    private static void LeftButtonUp() => InputHub.Foreground.Mouse.LeftButtonUp();

    private static void KeyPress(User32.VK vk) => InputHub.Foreground.Keyboard.KeyPress(vk);

    // ====================== 主流程 CharacterPath ======================

    /// <summary>
    /// 主流程，对应 JS <c>CharacterPath</c>：进入角色界面 → 元素筛选 → 角色定位 →
    /// 武器界面 → 当前武器预判 → 替换入口 + 重置滑条 → QuickScan/NormalScan 优先级分流 → 返回主界面。
    /// </summary>
    private async Task CharacterPathAsync()
    {
        // 运行期默认回退（对齐 JS settings.Character || "纳西妲" 等）
        var character = string.IsNullOrEmpty(_config.Character) ? "纳西妲" : _config.Character;
        var weapon = string.IsNullOrEmpty(_config.Weapon) ? "试作金珀" : _config.Weapon;
        var element = string.IsNullOrEmpty(_config.Element) ? "物" : _config.Element;
        var pageScrollCount = OcrSwitchWeaponDecisions.ClampPageScrollCount(_config.PageScrollCount);

        // 1. 返回主界面
        await new ReturnMainUiTask().Start(_ct);

        // 2. 打开角色界面（按 1 切换到首位角色，再按 C 打开角色详情）
        KeyPress(User32.VK.VK_1);
        await Delay(500, _ct);
        KeyPress(User32.VK.VK_C);
        await Delay(1000, _ct);

        // 3. 元素筛选（缩小角色范围）
        await SelectElementAsync(element);

        // 4. 角色定位
        if (!await SelectCharacterAsync(character))
        {
            _logger.LogError("角色筛选失败，退出脚本");
            return;
        }

        // 5. 进入武器详情入口
        Click(125, 225);
        await Delay(1000, _ct);

        // 6. 当前武器预判（仅当未手动指定 GridPosition，R5.2）
        if (string.IsNullOrEmpty(_config.GridPosition))
        {
            var currentWeapon = await _reader.RecognizeAndCombineWeaponNameAsync();
            if (!string.IsNullOrEmpty(currentWeapon))
            {
                var (weaponName1, weaponNames) = GetTargetWeaponName(weapon, character);
                var matched = OcrSwitchWeaponDecisions.FuzzyMatch(currentWeapon, weaponNames, 1.0) ?? currentWeapon;
                if (OcrSwitchWeaponDecisions.MatchRatio(weaponName1, matched) >= 0.8)
                {
                    _logger.LogInformation("当前已装备目标武器「{Weapon}」，跳过更换", weapon);
                    await new ReturnMainUiTask().Start(_ct);
                    return;
                }
            }
        }

        // 7. 替换武器入口
        Click(1600, 1005);
        await Delay(1000, _ct);

        // 8. 重置滑条到顶端
        await ResetSliderAsync();

        // 9. 优先级分流（手动 GridPosition > 已记录位置 > 普通扫描）
        var manual = OcrSwitchWeaponDecisions.ParseGridPosition(_config.GridPosition);
        int? stored = _positionStore.TryGet(weapon);

        int effectiveGridPos = manual.Ok
            ? manual.Row * 10 + manual.Col
            : (stored ?? 0);
        bool hasEffective = manual.Ok || stored.HasValue;

        bool weaponFound;
        if (_config.QuickMode && hasEffective)
        {
            weaponFound = await QuickScanAsync(effectiveGridPos, weapon, character);
            if (!weaponFound)
            {
                _logger.LogInformation("快速扫描未命中，重置滑条后回退普通扫描");
                await ResetSliderAsync();
                weaponFound = await NormalScanAsync(weapon, character, pageScrollCount);
            }
        }
        else
        {
            weaponFound = await NormalScanAsync(weapon, character, pageScrollCount);
        }

        // 10. 未找到提示
        if (!weaponFound)
        {
            _logger.LogWarning("未找到 {Weapon}", weapon);
        }

        // 11. 返回主界面
        await new ReturnMainUiTask().Start(_ct);
    }

    /// <summary>
    /// 重置武器列表滑条到顶端，对应 JS 重置滑条序列。
    /// </summary>
    private async Task ResetSliderAsync()
    {
        MoveMouseTo(605, 140);
        await Delay(200, _ct);
        LeftButtonDown();
        await Delay(300, _ct);
        LeftButtonUp();
        await Delay(200, _ct);
    }

    /// <summary>
    /// 求目标武器正式名与候选列表，对应 JS <c>getTargetWeaponName</c>。
    /// 返回 (weaponName1, weaponNames)。
    /// </summary>
    private (string WeaponName1, IReadOnlyList<string> WeaponNames) GetTargetWeaponName(string weapon, string character)
    {
        var formalName = _avatarRepo.AliasToName.GetValueOrDefault(character, character);
        _avatarRepo.NameToWeaponType.TryGetValue(formalName, out var weaponTypeCode);
        var weaponType = weaponTypeCode != null ? WeaponTypeMap.GetValueOrDefault(weaponTypeCode) : null;

        var weaponNames = (weaponType != null && _weaponNameRepo.ByWeaponType.TryGetValue(weaponType, out var list))
            ? new List<string>(list)
            : new List<string>();

        if (weaponNames.Count == 0)
        {
            weaponNames.Add(weapon);
        }

        var weaponName1 = OcrSwitchWeaponDecisions.FuzzyMatch(weapon, weaponNames, 0.9) ?? weapon;
        return (weaponName1, weaponNames);
    }

    // ====================== 元素筛选 SelectElement ======================

    /// <summary>
    /// 元素筛选，对应 JS <c>selectElement</c>。Element=="物" 跳过（R4.3）。
    /// 内部常量 elements 顺序仅用于定位 ElementClickX。
    /// </summary>
    private async Task SelectElementAsync(string element)
    {
        if (element == "物")
        {
            return;
        }

        var elements = new[] { "火", "水", "草", "雷", "风", "冰", "岩", "物" };
        var idx = Array.IndexOf(elements, element);
        if (idx < 0)
        {
            return;
        }

        var elementClickX = (int)Math.Round(787 + idx * 57.5);

        Click(960, 45);
        await Delay(100, _ct);
        LeftButtonDown();
        for (int j = 0; j < 10; j++)
        {
            MoveMouseBy(15, 0);
            await Delay(10, _ct);
        }
        await Delay(500, _ct);
        LeftButtonUp();
        await Delay(500, _ct);
        Click(elementClickX, 130);
        await Delay(500, _ct);
        Click(540, 45);
        await Delay(200, _ct);
    }

    // ====================== 角色定位 SelectCharacter ======================

    /// <summary>
    /// 角色定位，对应 JS <c>selectCharacter</c>。最大切换次数 99（R4.4）。
    /// </summary>
    private async Task<bool> SelectCharacterAsync(string character)
    {
        var targetFormal = _avatarRepo.AliasToName.GetValueOrDefault(character, character);

        for (int i = 0; i < 99; i++)
        {
            if (await _reader.RecognizeCharacterAsync(targetFormal, _avatarRepo.FormalNames, _avatarRepo.AliasToName))
            {
                return true;
            }
            // 切换到下一个角色
            Click(1840, 540);
            await Delay(200, _ct);
        }

        _logger.LogWarning("未找到 {Character}", character);
        return false;
    }

    // ====================== 普通扫描 NormalScan ======================

    /// <summary>
    /// 普通扫描，对应 JS <c>scanWeapons</c>。每页 5 行 4 列，逐页前置识别 + 逐格点击 OCR 匹配。
    /// </summary>
    private async Task<bool> NormalScanAsync(string weapon, string character, int pageScrollCount)
    {
        const double startX = 99.5;
        const double startY = 213.5;
        const double rowHeight = 167;
        const double columnWidth = 141;
        const int maxRows = 5;
        const int maxColumns = 4;
        const int rowsPerScreen = 5;

        var (weaponName1, weaponNames) = GetTargetWeaponName(weapon, character);

        // 注意：scroll 闭区间 <= pageScrollCount（对齐 JS scroll <= pageScrollCount）
        for (int scroll = 0; scroll <= pageScrollCount; scroll++)
        {
            // 前置识别（R7.2）
            var pre = await _reader.RecognizeAndCombineWeaponNameAsync();
            if (!string.IsNullOrEmpty(pre))
            {
                var pre2 = OcrSwitchWeaponDecisions.FuzzyMatch(pre, weaponNames, 1.0) ?? pre;
                if (OcrSwitchWeaponDecisions.MatchRatio(weaponName1, pre2) >= 0.8)
                {
                    Click(1600, 1005);
                    await Delay(1000, _ct);
                    Click(1320, 755);
                    await Delay(1000, _ct);
                    return true;
                }
            }

            // 逐格扫描（R7.3）
            for (int row = 0; row < maxRows; row++)
            {
                for (int col = 0; col < maxColumns; col++)
                {
                    var clickX = (int)Math.Round(startX + col * columnWidth);
                    var clickY = (int)Math.Round(startY + row * rowHeight);
                    Click(clickX, clickY);
                    await Delay(120, _ct);

                    var name2 = await _reader.RecognizeAndCombineWeaponNameAsync();
                    if (string.IsNullOrEmpty(name2))
                    {
                        continue;
                    }

                    var w2 = OcrSwitchWeaponDecisions.FuzzyMatch(name2, weaponNames, 1.0) ?? name2;
                    if (OcrSwitchWeaponDecisions.MatchRatio(weaponName1, w2) >= 0.8)
                    {
                        var absoluteRow = scroll * maxRows + row + 1;
                        var gridPos = absoluteRow * 10 + (col + 1);
                        _positionStore.Record(weapon, gridPos);

                        Click(1600, 1005);
                        await Delay(1000, _ct);
                        Click(1320, 755);
                        await Delay(1000, _ct);
                        return true;
                    }
                }
            }

            // 未命中且未达最大滑页次数 → 滚动一页（R7.5）
            if (scroll < pageScrollCount)
            {
                await ScrollPageAsync(rowsPerScreen * rowHeight, 10, 10);
            }
        }

        _logger.LogWarning("普通扫描未找到 {Weapon}", weapon);
        return false;
    }

    // ====================== 快速扫描 QuickScan ======================

    /// <summary>
    /// 快速扫描，对应 JS <c>scanWeaponsQuick</c>。按格子位置直接定位行列。
    /// 注意：QuickScan rowHeight=164，与 NormalScan 的 167 不同，按 JS 原样保留。
    /// </summary>
    private async Task<bool> QuickScanAsync(int effectiveGridPos, string weapon, string character)
    {
        const double startX = 99.5;
        const double startY = 213.5;
        const double rowHeight = 164;
        const double columnWidth = 141;
        const int rowsPerScreen = 5;

        // effectiveGridPos 已是 行*10+列 形式
        int row = effectiveGridPos / 10;
        int col = effectiveGridPos % 10;
        if (row < 1 || row > 99 || col < 1 || col > 4)
        {
            _logger.LogWarning("快速扫描格子位置非法（row={Row}, col={Col}），回退普通扫描", row, col);
            return false;
        }

        // R8.1 分批滚动：行号大于每屏行数时分批（每批最多 5 行）直到目标行进入屏幕第 5 行
        if (row > rowsPerScreen)
        {
            var remaining = row - rowsPerScreen;
            while (remaining > 0)
            {
                var batch = Math.Min(remaining, rowsPerScreen);
                await ScrollPageAsync(batch * rowHeight, 10, 10);
                await Delay(300, _ct);
                remaining -= batch;
            }
            row = rowsPerScreen;
        }

        var targetRow = row - 1;
        var targetCol = col - 1;
        var clickX = (int)Math.Round(startX + targetCol * columnWidth);
        var clickY = (int)Math.Round(startY + targetRow * rowHeight);
        Click(clickX, clickY);
        await Delay(200, _ct);

        var recognized = await _reader.RecognizeAndCombineWeaponNameAsync();
        if (string.IsNullOrEmpty(recognized))
        {
            _logger.LogWarning("快速扫描 OCR 识别为空，回退普通扫描");
            return false;
        }

        var (weaponName1, weaponNames) = GetTargetWeaponName(weapon, character);
        var matched = OcrSwitchWeaponDecisions.FuzzyMatch(recognized, weaponNames, 1.0) ?? recognized;
        var ratio = OcrSwitchWeaponDecisions.MatchRatio(weaponName1, matched);

        if (ratio >= 0.8)
        {
            _positionStore.Record(weapon, effectiveGridPos);
            Click(1600, 1005);
            await Delay(1000, _ct);
            Click(1320, 755);
            await Delay(1000, _ct);
            return true;
        }

        _logger.LogWarning("快速扫描位置武器不匹配（识别={Recognized}, 匹配占比={Ratio}），回退普通扫描", recognized, ratio);
        return false;
    }

    // ====================== 翻页滚动 ScrollPage ======================

    /// <summary>
    /// 武器列表翻页滚动，对应 JS <c>scrollPage</c>。
    /// </summary>
    private async Task ScrollPageAsync(double totalDistance, int stepDistance = 10, int delayMs = 10)
    {
        MoveMouseTo(525, 920);
        await Delay(500, _ct);
        LeftButtonDown();

        var steps = (int)Math.Ceiling(totalDistance / stepDistance);
        for (int j = 0; j < steps; j++)
        {
            var remaining = totalDistance - j * stepDistance;
            var move = remaining < stepDistance ? remaining : stepDistance;
            MoveMouseBy(0, -(int)Math.Round(move));
            await Delay(delayMs, _ct);
        }

        await Delay(700, _ct);
        LeftButtonUp();
        await Delay(100, _ct);
    }
}
