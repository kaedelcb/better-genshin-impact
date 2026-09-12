using System;
using System.Collections.Generic;
using OpenCvSharp;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using Serilog.Core;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>
/// 独立理由（R4.2）：消费方 AutoFightTask.cs 与 Model/Avatar.cs 均为与公版同名的高冲突文件
/// （茶包战斗引擎改造的主战场），本类把散落的像素状态判定集中为独立静态工具类，
/// 零冲突、可整体 PR；单机/联机共用。
/// 战斗中的血量和药品状态检测辅助类：集中管理所有像素检测逻辑，使用合理的颜色容差。
/// 覆盖：红血/契/绿血/营养袋/复活药/派蒙可见/角色死亡槽位/槽位血量与出战状态。
/// </summary>
public static class CombatHealthDetector
{
    #region 坐标常量 (基于 1920x1080)

    // 红血检测区域 - 血条中心
    private const int RedBloodX = 808;
    private const int RedBloodY = 1009;
    private const int RedBloodW = 3;
    private const int RedBloodH = 3;

    // 绿血检测点
    private const int GreenBloodX = 814;
    private const int GreenBloodY = 1010;

    // 营养袋检测区域
    private const int NutritionBagX = 1817;
    private const int NutritionBagY = 781;
    private const int NutritionBagW = 4;
    private const int NutritionBagH = 14;

    // 派蒙头冠检测点
    private const int PaimonX = 67;
    private const int PaimonY = 32;

    // 复活药检测点
    private const int ResurrectionDrugX = 1818;
    private const int ResurrectionDrugY = 785;

    // 角色头像死亡检测 (右侧4个角色槽位)
    private const int DeathCheckX = 1797;
    private const int DeathCheckBaseY = 249;
    private const int DeathCheckSlotSpacing = 96;
    private const int DeathCheckW = 8;
    private const int DeathCheckH = 3;

    // EndBloodCheck 中的血量检测区域
    private const int SlotBloodX = 1694;
    private const int SlotBloodBaseY = 267;
    private const int SlotBloodW = 3;
    private const int SlotBloodH = 24;

    // EndBloodCheck 中的出战角色检测
    private const int SlotActiveX = 1859;
    private const int SlotActiveBaseY = 264;
    private const int SlotActiveW = 3;
    private const int SlotActiveH = 3;

    #endregion

    #region 颜色常量与容差

    // 红血 BGR: (250, 90, 89) ±12
    private static readonly Scalar RedBloodLower = new Scalar(255, 90, 89);
    private static readonly Scalar RedBloodUpper = new Scalar(255, 91, 90);

    // 绿血 BGR: (34, 215, 150) ±15
    private const int GreenBloodB = 34;
    private const int GreenBloodG = 215;
    private const int GreenBloodR = 150;
    private const int GreenBloodTolerance = 15;

    // 营养袋绿色块 BGR: (192, 233, 102) ±8
    private static readonly Scalar NutritionBagLower = new Scalar(184, 225, 94);
    private static readonly Scalar NutritionBagUpper = new Scalar(200, 241, 110);

    // 派蒙头冠 BGR: (143, 196, 233) ±10
    private const int PaimonB = 143;
    private const int PaimonG = 196;
    private const int PaimonR = 233;
    private const int PaimonTolerance = 10;

    // 角色槽位血量 (EndBloodCheck) BGR 绿色范围
    private static readonly Scalar SlotBloodLower = new Scalar(145, 210, 30);
    private static readonly Scalar SlotBloodUpper = new Scalar(165, 225, 65);

    #endregion

    /// <summary>
    /// 检测当前出战角色是否为红血状态
    /// 使用连通性检测血条区域的红色像素
    /// </summary>
    public static bool IsRedBlood(ImageRegion ra)
    {
        using var bloodRect = ra.DeriveCrop(RedBloodX, RedBloodY, RedBloodW, RedBloodH);
        using var mask = OpenCvCommonHelper.Threshold(bloodRect.SrcMat, RedBloodLower, RedBloodUpper);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);
        return numLabels > 1;
    }
    
    /// <summary>
    /// 检测当前出战角色血条是否为绿色（正常血量）
    /// </summary>
    public static bool IsGreenBlood(ImageRegion ra)
    {
        var pixel = ra.SrcMat.At<Vec3b>(GreenBloodY, GreenBloodX);
        return Math.Abs(pixel[0] - GreenBloodB) <= GreenBloodTolerance &&
               Math.Abs(pixel[1] - GreenBloodG) <= GreenBloodTolerance &&
               Math.Abs(pixel[2] - GreenBloodR) <= GreenBloodTolerance;
    }

    /// <summary>
    /// 检测是否装备了营养袋（小道具栏上方的绿色标识）
    /// </summary>
    public static bool HasNutritionBag(ImageRegion ra)
    {
        using var mRect = ra.DeriveCrop(NutritionBagX, NutritionBagY, NutritionBagW, NutritionBagH);
        using var mask = OpenCvCommonHelper.Threshold(mRect.SrcMat, NutritionBagLower, NutritionBagUpper);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);
        return numLabels > 1;
    }

    /// <summary>
    /// 检测指定槽位的角色是否死亡（头像变灰）
    /// slotIndex: 0-3 对应 4 个角色槽位
    /// </summary>
    public static bool IsCharacterDead(ImageRegion ra, int slotIndex)
    {
        int y = DeathCheckBaseY + DeathCheckSlotSpacing * slotIndex;
        using var croppedImage = ra.DeriveCrop(DeathCheckX, y, DeathCheckW, DeathCheckH).SrcMat;

        for (int i = 0; i < croppedImage.Rows; i++)
        {
            for (int j = 0; j < croppedImage.Cols; j++)
            {
                Vec3b pixel = croppedImage.At<Vec3b>(i, j);
                // 灰度判断：三通道值相等
                if (pixel[0] != pixel[1] || pixel[1] != pixel[2])
                {
                    return false;
                }
                // 额外亮度范围验证：排除纯黑和纯白（可能是特效）
                if (pixel[0] < 30 || pixel[0] > 200)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// 检测当前小道具是否为复活药（白色图标）
    /// </summary>
    public static bool IsResurrectionDrug(ImageRegion ra)
    {
        var pixel = ra.SrcMat.At<Vec3b>(ResurrectionDrugY, ResurrectionDrugX);
        return pixel[0] == 255 && pixel[1] == 255 && pixel[2] == 255;
    }

    /// <summary>
    /// 检测派蒙头像是否可见（主界面标识）
    /// </summary>
    public static bool IsPaimonVisible(ImageRegion ra)
    {
        var pixel = ra.SrcMat.At<Vec3b>(PaimonY, PaimonX);
        return Math.Abs(pixel[0] - PaimonB) <= PaimonTolerance &&
               Math.Abs(pixel[1] - PaimonG) <= PaimonTolerance &&
               Math.Abs(pixel[2] - PaimonR) <= PaimonTolerance;
    }

    /// <summary>
    /// 检测指定角色槽位是否有绿血（EndBloodCheck 用）。
    /// 命名说明：原实现名 IsSlotRedBlood 名实不符——实现检测的是"槽位血条绿色存在"
    /// （绿血=健康态），调用方语义也是 hasGreenBlood，故按实际语义改名。
    /// slotIndex: 0-3 对应 4 个角色槽位
    /// </summary>
    public static bool IsSlotGreenBlood(ImageRegion ra, int slotIndex)
    {
        int y = SlotBloodBaseY + DeathCheckSlotSpacing * slotIndex;
        using var bloodRect = ra.DeriveCrop(SlotBloodX, y, SlotBloodW, SlotBloodH);
        using var mask = OpenCvCommonHelper.Threshold(bloodRect.SrcMat, SlotBloodLower, SlotBloodUpper);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);
        return numLabels > 1;   // 绿色血条像素成连通块 → 有绿血（健康）
    }

    /// <summary>
    /// 检测指定角色槽位是否为出战状态（EndBloodCheck 用）
    /// slotIndex: 0-3 对应 4 个角色槽位
    /// </summary>
    public static bool IsSlotActive(ImageRegion ra, int slotIndex)
    {
        int y = SlotActiveBaseY + DeathCheckSlotSpacing * slotIndex;
        using var activeRect = ra.DeriveCrop(SlotActiveX, y, SlotActiveW, SlotActiveH);
        using var mask = OpenCvCommonHelper.Threshold(activeRect.SrcMat, new Scalar(255, 255, 255));
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);
        return numLabels > 1;
    }

    /// <summary>
    /// 比较两个像素是否相似（各通道差值均在阈值内）
    /// </summary>
    public static bool IsPixelSimilar(Vec3b p1, Vec3b p2, int threshold = 10)
    {
        return Math.Abs(p1[0] - p2[0]) <= threshold &&
               Math.Abs(p1[1] - p2[1]) <= threshold &&
               Math.Abs(p1[2] - p2[2]) <= threshold;
    }

    /// <summary>
    /// 检测是否有任何角色死亡（遍历4个槽位）
    /// 返回死亡的槽位索引列表
    /// </summary>
    public static List<int> GetDeadCharacterSlots(ImageRegion ra)
    {
        var deadSlots = new List<int>();
        for (int h = 0; h < 4; h++)
        {
            if (IsCharacterDead(ra, h))
            {
                deadSlots.Add(h);
            }
        }
        return deadSlots;
    }
}
