using System;
using System.Collections.Generic;
using System.Globalization;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>
/// 战斗结束检测配置（由 AutoFightParam.FightFinishDetectConfig 解析而来）
/// 供 AutoFightEndDetection / AutoFightJsonTask / AutoFightTask 共用
/// </summary>
internal class TaskFightFinishDetectConfig
{
    public int DelayTime = 1500;
    public int DetectDelayTime = 450;
    public int FastCheckDelay = 100;
    public Dictionary<string, int> DelayTimes = new();
    public double CheckTime = 5;
    public List<string> CheckNames = new();
    public bool FastCheckEnabled;
    public bool RotateFindEnemyEnabled = false;
    public bool RotationMode = true;
    public bool EndModel = true;
    public bool PaimonEndModel = false;
    public bool DoubleEndEnbled = false;
    public int DoubleEndDelay = 750;
    public int FightWaitNotEndTime = 0;
    public int GoDistance = 500;

    public TaskFightFinishDetectConfig(AutoFightParam.FightFinishDetectConfig finishDetectConfig)
    {
        FastCheckEnabled = finishDetectConfig.FastCheckEnabled;
        ParseCheckTimeString(finishDetectConfig.FastCheckParams, out CheckTime, CheckNames);
        ParseFastCheckEndDelayString(finishDetectConfig.CheckEndDelay, out DelayTime, DelayTimes);
        DetectDelayTime =
            (int)((double.TryParse(finishDetectConfig.BeforeDetectDelay, out var result) ? result : 0.45) * 1000);
        FastCheckDelay = (int)Math.Round(finishDetectConfig.FastCheckDelay * 1000);
        RotateFindEnemyEnabled = finishDetectConfig.RotateFindEnemyEnabled;
        RotationMode = finishDetectConfig.RotationMode;
        EndModel = finishDetectConfig.EndModel;
        PaimonEndModel = finishDetectConfig.PaimonEndModel;
        DoubleEndEnbled = finishDetectConfig.DoubleEndEnbled;
        DoubleEndDelay = finishDetectConfig.DoubleEndDelay;
        FightWaitNotEndTime = finishDetectConfig.FightWaitNotEndTime;
        GoDistance = finishDetectConfig.GoDistance;
    }

    public static void ParseCheckTimeString(
        string input,
        out double checkTime,
        List<string> names)
    {
        checkTime = 5;
        if (string.IsNullOrEmpty(input))
        {
            return;
        }

        var uniqueNames = new HashSet<string>();

        var segments = input.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            var trimmedSegment = segment.Trim();

            if (double.TryParse(trimmedSegment, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double number))
            {
                checkTime = number;
            }
            else if (!uniqueNames.Contains(trimmedSegment))
            {
                uniqueNames.Add(trimmedSegment);
            }
        }

        names.AddRange(uniqueNames);
    }

    public static void ParseFastCheckEndDelayString(
        string input,
        out int delayTime,
        Dictionary<string, int> nameDelayMap)
    {
        delayTime = 1500;

        if (string.IsNullOrEmpty(input))
        {
            return;
        }

        var segments = input.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            var parts = segment.Split(',');

            if (parts.Length == 1)
            {
                if (double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double number))
                {
                    delayTime = (int)(number * 1000);
                }
            }
            else if (parts.Length == 2)
            {
                string name = parts[0].Trim();
                if (double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double value))
                {
                    nameDelayMap[name] = (int)(value * 1000);
                }
            }
        }
    }
}
