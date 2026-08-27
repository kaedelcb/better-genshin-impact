#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// Feature: sync-roomwhitelist-to-roomconfig 决策层属性测试。
/// 覆盖 KickWhitelistDecisions.MergeAllowedNames（踢人判定白名单合并）。
/// 守护 EB-2：配置白名单每一项都保留（容错名不丢失）；Preservation：去重不破坏、空白名单仍保留上报名单。
/// 框架：FsCheck 2.16.6 + FsCheck.Xunit（[Property]）+ xUnit。
/// </summary>
public class KickWhitelistDecisionsTest
{
    // ========== PBT-1：配置白名单每一项都应被保留（EB-2 守护：容错名不丢失）==========
    [Property(MaxTest = 300)]
    public bool MergeAllowedNames_IncludesAllConfiguredAndReported(string a, string b, string c)
    {
        var reported = new[] { a, b };
        var configured = new[] { b, c };
        var result = KickWhitelistDecisions.MergeAllowedNames(reported, configured);

        // 配置白名单中的每一项都应被保留（忽略大小写、空串除外）
        return configured.All(x =>
            string.IsNullOrEmpty(x) || result.Contains(x, StringComparer.OrdinalIgnoreCase));
    }

    // ========== PBT-2：去重不破坏（Preservation：OrdinalIgnoreCase 去重）==========
    [Property(MaxTest = 300)]
    public bool MergeAllowedNames_DedupsCaseInsensitive(string a)
    {
        if (string.IsNullOrEmpty(a)) return true;
        var lower = a.ToLowerInvariant();
        var result = KickWhitelistDecisions.MergeAllowedNames(
            new[] { a }, new[] { lower });

        // 同名不同大小写只保留一个
        return result.Length == 1;
    }

    // ========== PBT-3：配置白名单为空时，仍保留上报名单（降级路径守护）==========
    [Property(MaxTest = 300)]
    public bool MergeAllowedNames_EmptyConfiguredStillKeepsReported(string a)
    {
        var result = KickWhitelistDecisions.MergeAllowedNames(
            new[] { a }, Array.Empty<string>());

        return string.IsNullOrEmpty(a) || result.Contains(a);
    }

    // ========== PBT-4：空上报名单 + 空配置白名单 → 空数组（边界）==========
    [Fact]
    public void MergeAllowedNames_BothEmpty_ReturnsEmptyArray()
    {
        var result = KickWhitelistDecisions.MergeAllowedNames(
            Array.Empty<string>(), Array.Empty<string>());
        Assert.Empty(result);
    }
}