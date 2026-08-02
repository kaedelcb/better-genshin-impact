using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>
/// 模板匹配拾取服务：识别F图标 → 模板匹配物品 → 黑名单过滤 → 按键拾取
/// </summary>
public class TemplatePickupService
{
    private static readonly ILogger Logger = App.GetLogger<TemplatePickupService>();

    /// <summary>
    /// 联机按线路切角色期间的「后台子任务输入抑制」开关（hoeing-multiplayer-per-route-switch-roles）。
    /// 置 true 时：拾取循环跳过滚轮翻页与按 F；异常检测循环跳过空格/ESC 等输入。
    /// 避免在配队/筛选/角色选择界面误操作（滚走元素图标、误按 ESC 关界面）。
    /// 由 PathExecutor 在换角色 SwitchAsync 外置位/复位，换角色结束后复位 false。其它路径不感知（默认 false）。
    /// </summary>
    public static volatile bool SuppressPickupInput = false;

    private RecognitionObject? _fIconRo;
    private readonly List<TargetItem> _targetItems = new();

    /// <summary>
    /// 获取已加载的物品模板列表（供黑名单检测使用）
    /// </summary>
    public IReadOnlyList<TargetItem> TargetItems => _targetItems;

    /// <summary>
    /// 加载拾取模板图片
    /// </summary>
    public void LoadTemplates(string assetsDir, string pickupMode)
    {
        // 加载F图标模板（使用JS脚本自带的F_Dialogue.png，ROI与JS一致）
        var fIconPath = Path.Combine(assetsDir, "F_Dialogue.png");
        if (File.Exists(fIconPath))
        {
            var fMat = Cv2.ImRead(fIconPath, ImreadModes.Color);
            var scale = TaskContext.Instance().SystemInfo.AssetScale;
            _fIconRo = new RecognitionObject
            {
                Name = "AutoHoeing_F",
                RecognitionType = RecognitionTypes.TemplateMatch,
                TemplateImageMat = fMat,
                RegionOfInterest = new Rect(
                    (int)(1102 * scale), (int)(335 * scale),
                    (int)(34 * scale), (int)(400 * scale)),
                Threshold = 0.95
            }.InitTemplate();
        }

        // 加载物品模板
        string targetItemPath;
        if (pickupMode.Contains("狗粮和怪物材料"))
            targetItemPath = Path.Combine(assetsDir, "targetItems");
        else if (pickupMode.Contains("拾取狗粮") || pickupMode.Contains("只拾取狗粮"))
            targetItemPath = Path.Combine(assetsDir, "targetItems", "其他");
        else
            return;

        if (!Directory.Exists(targetItemPath)) return;

        var pngFiles = Directory.GetFiles(targetItemPath, "*.png", SearchOption.AllDirectories);
        foreach (var file in pngFiles)
        {
            try
            {
                var mat = Cv2.ImRead(file, ImreadModes.Color);
                var itemName = Path.GetFileNameWithoutExtension(file);
                var ro = RecognitionObject.TemplateMatch(mat);
                ro.Threshold = 0.9;
                ro.InitTemplate();

                _targetItems.Add(new TargetItem
                {
                    ItemName = itemName,
                    Ro = ro,
                    FullPath = file,
                    Enabled = true,
                    IsMonsterMaterial = file.Contains("怪物掉落材料")
                });
            }
            catch (Exception ex)
            {
                Logger.LogWarning("加载模板图片失败 {File}: {Msg}", file, ex.Message);
            }
        }

        Logger.LogInformation("已加载 {Count} 个拾取模板", _targetItems.Count);
    }

    /// <summary>
    /// 拾取主循环
    /// </summary>
    public async Task RunPickupLoop(
        Func<bool> isRunning,
        HashSet<string> blacklist,
        int pickupDelay,
        int rollingDelay,
        int scrollCycle,
        int findFInterval,
        CancellationToken ct)
    {
        if (_fIconRo == null || _targetItems.Count == 0)
        {
            Logger.LogWarning("拾取循环未启动：F模板={F}, 物品模板数={C}",
                _fIconRo != null ? "已加载" : "未加载", _targetItems.Count);
            return;
        }

        int lastCenterYF = 0;
        string lastItemName = "";
        var lastRollTime = DateTime.Now;
        var checkDelay = Math.Max(8, findFInterval / 2);
        var timeMoveUp = (int)(scrollCycle * 0.45);
        var timeMoveDown = (int)(scrollCycle * 0.55);
        int thisMoveUpTime = 0;
        int lastMoveDown = 0;

        while (isRunning() && !ct.IsCancellationRequested)
        {
            try
            {
                // 联机按线路切角色期间抑制拾取输入：跳过本轮所有滚轮/按键，避免干扰配队筛选界面
                if (SuppressPickupInput)
                {
                    await Task.Delay(checkDelay, ct);
                    continue;
                }

                using var region = CaptureToRectArea();

                // 识别F图标
                using var fResult = region.Find(_fIconRo);
                if (fResult.IsEmpty())
                {
                    // 未识别到F图标时，仅在非地图界面下执行滚轮下翻
                    if ((DateTime.Now - lastRollTime).TotalMilliseconds >= 200
                        && !Bv.IsInBigMapUi(region))
                    {
                        lastRollTime = DateTime.Now;
                        Simulation.SendInput.Mouse.VerticalScroll(-1);
                    }
                    await Task.Delay(checkDelay, ct);
                    continue;
                }

                var centerYF = (int)(fResult.Y + fResult.Height / 2.0);

                // 模板匹配物品
                string? itemName = PerformTemplateMatch(region, centerYF);

                if (itemName != null)
                {
                    // 重复检测：相同物品且Y坐标差≤20
                    if (Math.Abs(lastCenterYF - centerYF) <= 20 && lastItemName == itemName)
                    {
                        await Task.Delay(160, ct);
                        lastCenterYF = -20;
                        lastItemName = "";
                        continue;
                    }

                    if (blacklist.Contains(itemName))
                    {
                        // 黑名单物品，跳过拾取，继续滚轮翻页
                    }
                    else
                    {
                        // 拾取
                        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_F);
                        Logger.LogInformation("交互或拾取：{Name}", itemName);
                        lastCenterYF = centerYF;
                        lastItemName = itemName;
                        await Task.Delay(pickupDelay, ct);
                    }
                }
                else
                {
                    lastItemName = "";
                }

                // 滚轮上下翻页
                var currentTime = Environment.TickCount;
                if (currentTime - lastMoveDown > timeMoveUp)
                {
                    Simulation.SendInput.Mouse.VerticalScroll(-1);
                    if (thisMoveUpTime == 0) thisMoveUpTime = currentTime;
                    if (currentTime - thisMoveUpTime >= timeMoveDown)
                    {
                        lastMoveDown = currentTime;
                        thisMoveUpTime = 0;
                    }
                }
                else
                {
                    Simulation.SendInput.Mouse.VerticalScroll(1);
                }
                await Task.Delay(rollingDelay, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.LogDebug("拾取循环异常: {Msg}", ex.Message);
                await Task.Delay(50, ct);
            }
        }
    }

    private string? PerformTemplateMatch(ImageRegion region, int centerYF)
    {
        var scale = TaskContext.Instance().SystemInfo.AssetScale;

        // 对F图标右侧区域进行物品模板匹配
        foreach (var item in _targetItems)
        {
            if (!item.Enabled) continue;

            try
            {
                var cnLen = Math.Min(item.ItemName.Count(c => c >= '\u4e00' && c <= '\u9fff'), 5);
                var w = (int)((12 + 28 * cnLen + 2) * scale);
                var cropX = (int)(1219 * scale);
                var cropH = (int)(30 * scale);
                var cropY = centerYF - cropH / 2;
                var cropRegion = region.DeriveCrop(cropX, cropY, w, cropH);

                try
                {
                    using var result = cropRegion.Find(item.Ro);
                    if (result.IsExist())
                        return item.ItemName;
                }
                finally
                {
                    cropRegion.Dispose();
                }
            }
            catch { /* 单个模板匹配失败不影响其他 */ }
        }
        return null;
    }

    /// <summary>
    /// 设置仅使用路线相关怪物材料
    /// </summary>
    public void SetRouteRelatedMaterials(Dictionary<string, int> monsterInfo, HashSet<string> pickupHistory)
    {
        foreach (var item in _targetItems)
        {
            if (item.IsMonsterMaterial)
            {
                item.Enabled = false;
                if (pickupHistory.Contains(item.ItemName))
                {
                    item.Enabled = true;
                }
            }
            else
            {
                item.Enabled = true;
            }
        }
    }

    public void ResetAllEnabled()
    {
        foreach (var item in _targetItems)
            item.Enabled = true;
    }
}

public class TargetItem
{
    public string ItemName { get; set; } = "";
    public RecognitionObject Ro { get; set; } = null!;
    public string FullPath { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool IsMonsterMaterial { get; set; }
}
