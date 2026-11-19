using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BetterGenshinImpact.GameTask.AutoFightOfficial.Model;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoFight.Model;

internal sealed class ChascaBulletRecognition
{
    private const string ElementChars = "风火水雷冰";
    private const double DefaultThreshold = 0.8;
    private const int SlotCount = 6;
    private static readonly int[][] PackagedElements = [[0], [0, 1, 2, 3, 4], [0, 1, 2, 3, 4], [1, 2, 3, 4], [1, 2, 3, 4], [1, 2, 3, 4]];
    private readonly Dictionary<int, Dictionary<int, FeatureScorerExportData>> _models;

    internal static readonly Lazy<ChascaBulletRecognition?> PackagedModels = new(() =>
    {
        try
        {
            return Load(Path.Combine(AppContext.BaseDirectory, "GameTask", "AutoFight", "Assets", "chasca-bullet-models.json"),
                requireCompletePackage: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            TaskControl.Logger.LogWarning(ex, "无法加载恰斯卡弹槽模型，使用原有花纹识别");
            return null;
        }
    });

    private ChascaBulletRecognition(Dictionary<int, Dictionary<int, FeatureScorerExportData>> models)
    {
        _models = models;
    }

    internal static ChascaBulletRecognition Load(string path, bool requireCompletePackage = false)
    {
        var models = JsonSerializer.Deserialize<ModelFile>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Bullets;
        if (models == null || models.Count == 0 ||
            models.Any(slot => slot.Key < 0 || slot.Key >= SlotCount || slot.Value == null || slot.Value.Count == 0 ||
                slot.Value.Any(element => element.Key < 0 || element.Key >= ElementChars.Length ||
                    element.Value?.Features is not { Count: > 0 } ||
                    element.Value.Features.Any(feature => feature == null || feature.W <= 0 || feature.H <= 0 ||
                        feature.Type is not ("F1" or "F2") || feature.Channel is not ("H" or "S" or "V") ||
                        feature.ProbTable is not { Count: > 0 } ||
                        feature.Type == "F2" && feature.RefHist is not { Count: 8 }))))
        {
            throw new InvalidDataException("恰斯卡弹槽模型内容不完整");
        }

        if (requireCompletePackage && !PackagedElements.Select((elements, slot) =>
            models.TryGetValue(slot, out var slotModels) && elements.SequenceEqual(slotModels.Keys.Order())).All(matches => matches))
        {
            throw new InvalidDataException("恰斯卡弹槽模型包缺少槽位元素组合");
        }

        return new ChascaBulletRecognition(models);
    }

    internal char[] Recognize(Mat frame, double threshold)
    {
        var slots = new char[SlotCount];
        Array.Fill(slots, '空');
        if (frame.Empty())
        {
            return slots;
        }

        threshold = double.IsFinite(threshold) ? Math.Clamp(threshold, 0, 1) : DefaultThreshold;
        foreach (var (slot, models) in _models)
        {
            double bestScore = 0;
            for (int element = 0; element < ElementChars.Length; element++)
            {
                if (!models.TryGetValue(element, out var model))
                {
                    continue;
                }

                var score = ImageFeatureScorer.Score(model, frame);
                if (score >= threshold && score > bestScore)
                {
                    bestScore = score;
                    slots[slot] = ElementChars[element];
                }
            }
        }

        return slots;
    }

    private sealed record ModelFile(Dictionary<int, Dictionary<int, FeatureScorerExportData>> Bullets);
}

/// <summary>只根据近期出现过的第 2–5 槽内容判断装填变化，空槽和风弹同等对待。</summary>
internal sealed class ChascaBulletSequenceHistory(int capacity)
{
    private readonly int _capacity = Math.Clamp(capacity, 1, 5);
    private readonly Queue<string> _sequences = new();

    internal int Count => _sequences.Count;

    internal bool Observe(char[] slots)
    {
        var sequence = new string(slots, 1, 4).Replace('风', '空');
        if (_sequences.Contains(sequence))
        {
            return false;
        }

        if (_sequences.Count == _capacity)
        {
            _sequences.Dequeue();
        }
        _sequences.Enqueue(sequence);
        return true;
    }
}
