#nullable enable

using System;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// WaitPointReport 数据结构测试（skip-route-wait-point-report spec）
/// 测试 WaitPointReport 数据结构的创建、验证、序列化、过期检查等功能
/// </summary>
public class WaitPointReportTests
{
    // ========== Property 1: WaitPointReport 创建与验证 ==========
    
    [Property(MaxTest = 200)]
    public bool WaitPointReport_Creation_IsValidWithRequiredFields(
        PositiveInt player, NonNegativeInt segment, NonNegativeInt worldRound)
    {
        var playerUid = $"player-{player.Get}";
        var routeId = $"Route_{segment.Get}";
        var syncPointId = $"map_route_{segment.Get}_1";
        var report = new WaitPointReport(playerUid, routeId, syncPointId, worldRound.Get);

        return report.PlayerUid == playerUid
            && report.RouteId == routeId
            && report.SyncPointId == syncPointId
            && report.WorldRound == worldRound.Get
            && report.ExpiryTime > report.ReportedTime
            && report.IsValid();
    }

    [Property(MaxTest = 200)]
    public bool WaitPointReport_ExpiryCheck_WorksCorrectly(int secondsToAdd)
    {
        var initial = DateTime.UnixEpoch;
        var report = new WaitPointReport("test", "Route_1", "map_route_1_1", 1) { ExpiryTime = initial };

        report.ExtendExpiry(TimeSpan.FromSeconds(secondsToAdd));

        return report.ExpiryTime == initial.AddSeconds(secondsToAdd);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WaitPointReport_ExpiryCheck_UsesTheDeadline(bool expired)
    {
        var report = new WaitPointReport("test", "Route_1", "map_route_1_1", 1)
        {
            ExpiryTime = expired ? DateTime.MinValue : DateTime.MaxValue
        };

        Assert.Equal(expired, report.IsExpired());
        Assert.Equal(!expired, report.IsValid());
    }

    [Property(MaxTest = 200)]
    public bool WaitPointReport_ExtractRouteIndex_HandlesVariousFormats(NonNegativeInt routeIndex)
    {
        var index = routeIndex.Get;
        var suffix = new WaitPointReport("test", $"Area12_{index}", "map_route_1_1", 1);
        var embedded = new WaitPointReport("test", $"Path_{index}_segment", "map_route_1_1", 1);
        var withoutIndex = new WaitPointReport("test", "Unknown_route", "map_route_1_1", 1);

        return suffix.ExtractRouteIndex() == index
            && embedded.ExtractRouteIndex() == index
            && withoutIndex.ExtractRouteIndex() == null;
    }

    [Property(MaxTest = 200)]
    public bool WaitPointReport_SyncPointIdValidation_WorksCorrectly(NonNegativeInt segment, NonNegativeInt fight)
    {
        var valid = new WaitPointReport("test", "Route_1", $"map_route_{segment.Get}_{fight.Get}", 1);
        var missingPart = new WaitPointReport("test", "Route_1", $"route_{segment.Get}_{fight.Get}", 1);

        return valid.IsValidSyncPointId() && !missingPart.IsValidSyncPointId();
    }

    // ========== Property 5: WaitPointReport 克隆功能 ==========
    
    [Property(MaxTest = 200)]
    public bool WaitPointReport_Clone_CreatesDeepCopy(
        string playerUid, string routeId, string syncPointId, int worldRound)
    {
        if (string.IsNullOrEmpty(playerUid) || string.IsNullOrEmpty(routeId) || string.IsNullOrEmpty(syncPointId))
            return true;
        
        var original = new WaitPointReport(playerUid, routeId, syncPointId, worldRound);
        var clone = original.Clone();
        
        // 验证克隆是深拷贝
        var isDeepCopy = clone.PlayerUid == original.PlayerUid
            && clone.RouteId == original.RouteId
            && clone.SyncPointId == original.SyncPointId
            && clone.WorldRound == original.WorldRound
            && clone.ReportedTime == original.ReportedTime
            && clone.ExpiryTime == original.ExpiryTime;
        
        // 修改克隆不应影响原始对象
        clone.PlayerUid = "modified";
        clone.RouteId = "modified";
        clone.SyncPointId = "modified";
        clone.WorldRound = 999;
        
        var originalUnchanged = original.PlayerUid == playerUid
            && original.RouteId == routeId
            && original.SyncPointId == syncPointId
            && original.WorldRound == worldRound;
        
        return isDeepCopy && originalUnchanged;
    }
    
    // ========== 基础单元测试 ==========
    
    [Fact]
    public void WaitPointReport_DefaultConstructor_SetsDefaultValues()
    {
        var report = new WaitPointReport();
        
        Assert.Equal(string.Empty, report.PlayerUid);
        Assert.Equal(string.Empty, report.RouteId);
        Assert.Equal(string.Empty, report.SyncPointId);
        Assert.Equal(0, report.WorldRound);
        Assert.True(report.ReportedTime <= DateTime.UtcNow);
        Assert.NotNull(report.ExpiryTime);
        Assert.True(report.ExpiryTime!.Value > report.ReportedTime);
    }
    
    [Fact]
    public void WaitPointReport_ParameterizedConstructor_SetsAllProperties()
    {
        var report = new WaitPointReport("player123", "Route_1", "sync_1_2_3", 2);
        
        Assert.Equal("player123", report.PlayerUid);
        Assert.Equal("Route_1", report.RouteId);
        Assert.Equal("sync_1_2_3", report.SyncPointId);
        Assert.Equal(2, report.WorldRound);
        Assert.True(report.ReportedTime <= DateTime.UtcNow);
        Assert.NotNull(report.ExpiryTime);
    }
    
    [Fact]
    public void WaitPointReport_IsValid_ReturnsTrueForValidReport()
    {
        var report = new WaitPointReport("player123", "Route_1", "sync_1_2_3", 1);
        
        Assert.True(report.IsValid());
    }
    
    [Fact]
    public void WaitPointReport_IsValid_ReturnsFalseForInvalidReport()
    {
        var report1 = new WaitPointReport("", "Route_1", "sync_1_2_3", 1);
        var report2 = new WaitPointReport("player123", "", "sync_1_2_3", 1);
        var report3 = new WaitPointReport("player123", "Route_1", "", 1);
        var report4 = new WaitPointReport("player123", "Route_1", "sync_1_2_3", -1);
        
        Assert.False(report1.IsValid());
        Assert.False(report2.IsValid());
        Assert.False(report3.IsValid());
        Assert.False(report4.IsValid());
    }
    
    [Fact]
    public void WaitPointReport_IsExpired_ReturnsFalseForNewReport()
    {
        var report = new WaitPointReport("player123", "Route_1", "sync_1_2_3", 1);
        
        Assert.False(report.IsExpired());
    }
    
    [Fact]
    public void WaitPointReport_ExtendExpiry_ExtendsCorrectly()
    {
        var report = new WaitPointReport("player123", "Route_1", "sync_1_2_3", 1);
        var originalExpiry = report.ExpiryTime;
        
        report.ExtendExpiry(TimeSpan.FromMinutes(5));
        
        Assert.NotNull(report.ExpiryTime);
        Assert.True(report.ExpiryTime!.Value > originalExpiry!.Value);
    }
    
    [Fact]
    public void WaitPointReport_ExtractRouteIndex_ExtractsCorrectly()
    {
        var report1 = new WaitPointReport("player123", "Route_1", "sync_1_2_3", 1);
        var report2 = new WaitPointReport("player123", "Path_3_segment", "sync_1_2_3", 1);
        var report3 = new WaitPointReport("player123", "InvalidRoute", "sync_1_2_3", 1);
        
        Assert.Equal(1, report1.ExtractRouteIndex());
        Assert.Equal(3, report2.ExtractRouteIndex());
        Assert.Null(report3.ExtractRouteIndex());
    }
    
    [Fact]
    public void WaitPointReport_IsValidSyncPointId_ValidatesCorrectly()
    {
        var validReport = new WaitPointReport("player123", "Route_1", "sync_1_2_3", 1);
        var invalidReport1 = new WaitPointReport("player123", "Route_1", "sync", 1);
        var invalidReport2 = new WaitPointReport("player123", "Route_1", "sync_1", 1);
        var invalidReport3 = new WaitPointReport("player123", "Route_1", "sync_1_2", 1);
        
        Assert.True(validReport.IsValidSyncPointId());
        Assert.False(invalidReport1.IsValidSyncPointId());
        Assert.False(invalidReport2.IsValidSyncPointId());
        Assert.False(invalidReport3.IsValidSyncPointId());
    }
    
    [Fact]
    public void WaitPointReport_ToString_ReturnsNonEmptyString()
    {
        var report = new WaitPointReport("player123", "Route_1", "sync_1_2_3", 1);
        
        var str = report.ToString();
        
        Assert.False(string.IsNullOrEmpty(str));
        Assert.Contains("player123", str);
        Assert.Contains("Route_1", str);
        Assert.Contains("sync_1_2_3", str);
    }
    
    [Fact]
    public void WaitPointReport_Clone_CreatesIdenticalCopy()
    {
        var original = new WaitPointReport("player123", "Route_1", "sync_1_2_3", 1);
        var clone = original.Clone();
        
        Assert.Equal(original.PlayerUid, clone.PlayerUid);
        Assert.Equal(original.RouteId, clone.RouteId);
        Assert.Equal(original.SyncPointId, clone.SyncPointId);
        Assert.Equal(original.WorldRound, clone.WorldRound);
        Assert.Equal(original.ReportedTime, clone.ReportedTime);
        Assert.Equal(original.ExpiryTime, clone.ExpiryTime);
        
        // 验证是深拷贝
        clone.PlayerUid = "modified";
        Assert.Equal("player123", original.PlayerUid);
        Assert.Equal("modified", clone.PlayerUid);
    }
}
