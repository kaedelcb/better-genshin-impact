#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.Core.Config;

/// <summary>
/// Feature: multiplayer-hoeing-auto-eat-food-by-period 全局 CD 时间戳配置属性测试。
/// 覆盖 MedicineEatCdConfig 的序列化往返一致性与解析失败回退。
/// </summary>
public class MedicineEatCdConfigTests
{
    private static readonly DateTime Base = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static int ToSlot(int seed) => Math.Abs(seed % 4) + 1;

    // ========== Property 12：CD 时间戳序列化往返秒级一致 ==========
    // Feature: multiplayer-hoeing-auto-eat-food-by-period, Property 12
    // Validates: Requirements 9.3
    [Property(MaxTest = 100)]
    public bool Property12_SerializationRoundTrip_SecondLevelConsistent(int[] slotSeeds, int[] offsetSeeds)
    {
        var cfg = new MedicineEatCdConfig();

        // 记录每格最终写入值（重复键以最后一次写入为准，匹配 SetLastEatTime 覆盖语义）
        var expected = new Dictionary<int, DateTime>();
        var count = Math.Min(slotSeeds?.Length ?? 0, offsetSeeds?.Length ?? 0);
        for (int i = 0; i < count; i++)
        {
            var slot = ToSlot(slotSeeds![i]);
            var offset = offsetSeeds![i] % 1_000_000; // 约束避免溢出
            var t = Base.AddSeconds(offset);
            cfg.SetLastEatTime(slot, t);
            expected[slot] = t.ToUniversalTime();
        }

        // 往返：用主项目 ConfigService.JsonOptions
        var json = JsonSerializer.Serialize(cfg, ConfigService.JsonOptions);
        var restored = JsonSerializer.Deserialize<MedicineEatCdConfig>(json, ConfigService.JsonOptions);
        if (restored == null) return false;

        foreach (var kv in expected)
        {
            var got = restored.GetLastEatTime(kv.Key);
            if (got == null) return false;
            var diff = Math.Abs((got.Value.ToUniversalTime() - kv.Value).TotalSeconds);
            if (diff >= 1) return false;
        }
        return true;
    }

    // ========== Property 13：CD 时间戳解析失败回退 null ==========
    // Feature: multiplayer-hoeing-auto-eat-food-by-period, Property 13
    // Validates: Requirements 9.4
    [Property(MaxTest = 100)]
    public bool Property13_ParseFailure_ReturnsNull(int slotSeed, string? garbage)
    {
        var slot = ToSlot(slotSeed);
        var cfg = new MedicineEatCdConfig();
        // 前缀确保恒为非法时间字符串（DateTime.TryParse 无法解析）
        var invalid = "not-a-date-" + (garbage ?? "");
        cfg.LastEatTimeBySlot[slot] = invalid;
        return cfg.GetLastEatTime(slot) == null;
    }

    [Fact]
    public void Property13_FixedInvalidStrings_ReturnNull()
    {
        var cfg = new MedicineEatCdConfig();
        foreach (var (slot, s) in new[] { (1, "not-a-date"), (2, ""), (3, "xyz"), (4, "12:") })
        {
            cfg.LastEatTimeBySlot[slot] = s;
            Assert.Null(cfg.GetLastEatTime(slot));
        }
    }

    [Fact]
    public void GetLastEatTime_MissingSlot_ReturnsNull()
    {
        var cfg = new MedicineEatCdConfig();
        Assert.Null(cfg.GetLastEatTime(1));
    }
}
