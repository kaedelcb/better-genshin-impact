using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Job;
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
/// 料理使用服务：在路线间自动打开背包搜索并使用料理
/// </summary>
public class CookingService
{
    private static readonly ILogger Logger = App.GetLogger<CookingService>();

    private DateTime _lastBuffTime = DateTime.MinValue;
    private string _currentFood = "";
    private const int BuffIntervalSeconds = 300;

    // 模板图片
    private RecognitionObject? _filterRo1;
    private RecognitionObject? _filterRo2;
    private RecognitionObject? _resetRo;
    private RecognitionObject? _searchRo;
    private RecognitionObject? _searchClickRo;
    private RecognitionObject? _confirmFilterRo;
    private RecognitionObject? _useRo;
    private RecognitionObject? _foodType1Ro;
    private RecognitionObject? _foodType2Ro;

    public void LoadTemplates(string assetsDir)
    {
        _filterRo1 = LoadRo(assetsDir, "筛选1.png");
        _filterRo2 = LoadRo(assetsDir, "筛选2.png");
        _resetRo = LoadRo(assetsDir, "重置.png");
        _searchRo = LoadRo(assetsDir, "搜索.png");
        _searchClickRo = LoadRo(assetsDir, "搜索成功点击.png");
        _confirmFilterRo = LoadRo(assetsDir, "确认筛选.png");
        _useRo = LoadRo(assetsDir, "使用.png");
        _foodType1Ro = LoadRo(assetsDir, "背包界面/食物1.png");
        _foodType2Ro = LoadRo(assetsDir, "背包界面/食物2.png");
    }

    private static RecognitionObject? LoadRo(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return null;
        var mat = Cv2.ImRead(path, ImreadModes.Color);
        return RecognitionObject.TemplateMatch(mat).InitTemplate();
    }

    /// <summary>
    /// 尝试使用料理buff
    /// </summary>
    public async Task TryUseCooking(string cookingNames, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cookingNames)) return;
        if ((DateTime.Now - _lastBuffTime).TotalSeconds < BuffIntervalSeconds) return;

        var foods = cookingNames.Split('，')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();

        if (foods.Count == 0) return;

        // 优先使用上次成功的食物
        if (!string.IsNullOrEmpty(_currentFood) && foods.Contains(_currentFood))
        {
            foods.Remove(_currentFood);
            foods.Insert(0, _currentFood);
        }

        try
        {
            // 返回主界面
            await ReturnMainUi(ct);

            // 打开背包 (B键)
            InputHub.Foreground.Keyboard.KeyPress(User32.VK.VK_B);
            await Task.Delay(1000, ct);

            // 点击食物分类
            await FindAndClick(_foodType1Ro, _foodType2Ro, ct);

            foreach (var food in foods)
            {
                if (_currentFood != food)
                {
                    await Task.Delay(300, ct);
                    // 点击筛选
                    await FindAndClick(_filterRo1, _filterRo2, ct);
                    // 点击重置
                    await FindAndClick(_resetRo, null, ct);
                    await Task.Delay(300, ct);
                    // 点击搜索
                    await FindAndClick(_searchRo, null, ct);
                    await Task.Delay(300, ct);
                    // 点击搜索输入框
                    await FindAndClick(_searchClickRo, null, ct);

                    // 输入食物名称
                    Logger.LogInformation("搜索料理: {Food}", food);
                    Simulation.SendInput.Keyboard.TextEntry(food);
                    await Task.Delay(500, ct);

                    // 确认筛选
                    await FindAndClick(_confirmFilterRo, null, ct);
                    await Task.Delay(500, ct);

                    _currentFood = food;
                }

                // 点击使用
                await FindAndClick(_useRo, null, ct);
                await Task.Delay(500, ct);

                // 点击确认对话框中的"确认"按钮
                using (var confirmRegion = CaptureToRectArea())
                {
                    if (Bv.ClickWhiteConfirmButton(confirmRegion))
                    {
                        Logger.LogInformation("料理使用确认: {Food}", food);
                        await Task.Delay(500, ct);
                    }
                }
            }

            // 返回主界面
            await ReturnMainUi(ct);
            _lastBuffTime = DateTime.Now;
        }
        catch (Exception ex)
        {
            Logger.LogWarning("使用料理失败: {Msg}", ex.Message);
            await ReturnMainUi(ct);
        }
    }

    private static async Task ReturnMainUi(CancellationToken ct)
    {
        var task = new ReturnMainUiTask();
        await task.Start(ct);
    }

    private static async Task<bool> FindAndClick(RecognitionObject? ro1, RecognitionObject? ro2,
        CancellationToken ct, int timeout = 3000)
    {
        if (ro1 == null && ro2 == null) return false;

        var start = DateTime.Now;
        while ((DateTime.Now - start).TotalMilliseconds < timeout && !ct.IsCancellationRequested)
        {
            using var region = CaptureToRectArea();

            if (ro1 != null)
            {
                using var result = region.Find(ro1);
                if (result.IsExist())
                {
                    await Task.Delay(50, ct);
                    result.Click();
                    await Task.Delay(50, ct);
                    return true;
                }
            }

            if (ro2 != null)
            {
                using var result = region.Find(ro2);
                if (result.IsExist())
                {
                    await Task.Delay(50, ct);
                    result.Click();
                    await Task.Delay(50, ct);
                    return true;
                }
            }

            await Task.Delay(50, ct);
        }
        return false;
    }
}
