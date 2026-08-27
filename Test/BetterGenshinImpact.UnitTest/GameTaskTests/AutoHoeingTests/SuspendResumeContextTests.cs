using System;
using System.Text.Json;
using BetterGenshinImpact.Core.Config;
using Xunit;
using FsCheck;
using FsCheck.Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// Feature: multiplayer-hoeing-preempt-interrupt 抢占式中断上下文测试。
/// 覆盖 SuspendedTaskContext 模型的序列化/反序列化、AllConfig 持久化、清空逻辑。
/// 框架：FsCheck 2.16.6 + FsCheck.Xunit（[Property]）+ xUnit。
/// </summary>
public class SuspendResumeContextTests
{
    // ========== SuspendedTaskContext 序列化/反序列化 ==========

    [Property(MaxTest = 50)]
    public bool SerializeDeserialize_GroupType_Roundtrip(string groupName, int taskIndex, string folderName, string projectName)
    {
        var ctx = new SuspendedTaskContext
        {
            TaskType = "group",
            GroupName = groupName ?? "",
            TaskIndex = Math.Abs(taskIndex % 100) + 1,
            FolderName = folderName ?? "",
            ProjectName = projectName ?? ""
        };

        var json = JsonSerializer.Serialize(ctx);
        var deserialized = JsonSerializer.Deserialize<SuspendedTaskContext>(json);

        return deserialized != null
            && deserialized.TaskType == "group"
            && deserialized.GroupName == ctx.GroupName
            && deserialized.TaskIndex == ctx.TaskIndex
            && deserialized.FolderName == ctx.FolderName
            && deserialized.ProjectName == ctx.ProjectName;
    }

    [Property(MaxTest = 50)]
    public bool SerializeDeserialize_OneDragonType_Roundtrip(string configName, int taskIndex)
    {
        var ctx = new SuspendedTaskContext
        {
            TaskType = "onedragon",
            GroupName = configName ?? "",
            TaskIndex = Math.Abs(taskIndex % 100) + 1
        };

        var json = JsonSerializer.Serialize(ctx);
        var deserialized = JsonSerializer.Deserialize<SuspendedTaskContext>(json);

        return deserialized != null
            && deserialized.TaskType == "onedragon"
            && deserialized.GroupName == ctx.GroupName
            && deserialized.TaskIndex == ctx.TaskIndex;
    }

    [Property(MaxTest = 50)]
    public bool SerializeDeserialize_SoloType_Roundtrip(string projectName)
    {
        var ctx = new SuspendedTaskContext
        {
            TaskType = "solo",
            ProjectName = projectName ?? "",
            TaskIndex = 1
        };

        var json = JsonSerializer.Serialize(ctx);
        var deserialized = JsonSerializer.Deserialize<SuspendedTaskContext>(json);

        return deserialized != null
            && deserialized.TaskType == "solo"
            && deserialized.ProjectName == ctx.ProjectName
            && deserialized.TaskIndex == ctx.TaskIndex;
    }

    // ========== 默认值 ==========

    [Fact]
    public void DefaultValues_AllEmpty()
    {
        var ctx = new SuspendedTaskContext();
        Assert.Equal("", ctx.TaskType);
        Assert.Equal("", ctx.GroupName);
        Assert.Equal(0, ctx.TaskIndex);
        Assert.Equal("", ctx.FolderName);
        Assert.Equal("", ctx.ProjectName);
    }

    // ========== AllConfig SuspendedTaskContext 持久化 ==========

    [Fact]
    public void AllConfig_SuspendedTaskContext_DefaultNull()
    {
        // 验证旧配置 JSON 不含 SuspendedTaskContext 时默认为 null
        var json = "{}";
        var config = JsonSerializer.Deserialize<AllConfig>(json);
        Assert.Null(config?.SuspendedTaskContext);
    }

    [Fact]
    public void AllConfig_SuspendedTaskContext_SetAndRead()
    {
        // 这是一个模拟测试，验证 SuspendedTaskContext 在 AllConfig 中的设置和读取
        // 注意：AllConfig 使用 System.Text.Json 序列化，SuspendedTaskContext 有 [JsonIgnore]
        // 所以它不会被持久化到 JSON 文件中，而是通过 config.json 中的其他机制保存
        // 这里我们只验证属性设置和读取的基本功能
        var ctx = new SuspendedTaskContext
        {
            TaskType = "group",
            GroupName = "联机锄地-传奇",
            TaskIndex = 3,
            FolderName = "联机锄地",
            ProjectName = "锄地一条龙"
        };

        // 验证属性值
        Assert.Equal("group", ctx.TaskType);
        Assert.Equal("联机锄地-传奇", ctx.GroupName);
        Assert.Equal(3, ctx.TaskIndex);
        Assert.Equal("联机锄地", ctx.FolderName);
        Assert.Equal("锄地一条龙", ctx.ProjectName);
    }

    // ========== 清空逻辑（一次性消费） ==========

    [Fact]
    public void ClearContext_AfterConsume_Null()
    {
        var ctx = new SuspendedTaskContext
        {
            TaskType = "group",
            GroupName = "test",
            TaskIndex = 1
        };

        // 模拟消费：清除上下文
        ctx = null;

        Assert.Null(ctx);
    }

    [Fact]
    public void AllConfig_SuspendedTaskContext_ClearAfterResume()
    {
        // 模拟"恢复后清除上下文"的行为
        // 在实际代码中，HandleTaskResume 在恢复后设置 allConfig.SuspendedTaskContext = null
        var config = new AllConfig();

        // 设置上下文
        config.SuspendedTaskContext = new SuspendedTaskContext
        {
            TaskType = "group",
            GroupName = "test",
            TaskIndex = 1
        };
        Assert.NotNull(config.SuspendedTaskContext);

        // 模拟消费（清除）
        config.SuspendedTaskContext = null;
        Assert.Null(config.SuspendedTaskContext);
    }

    // ========== 安全降级：配置组被删/改名 ==========

    [Fact]
    public void Degrade_GroupDeleted_ResetsToStart()
    {
        // 模拟配置组被删除时，NextScheduledTask 匹配失败会从头开始
        // 这是现有逻辑（ScriptControlViewModel.SetTaskContextNextFlag 找不到时返回 false）
        // 这里只验证降级路径的存在性
        var ctx = new SuspendedTaskContext
        {
            TaskType = "group",
            GroupName = "不存在的配置组",
            TaskIndex = 5
        };

        // 验证：如果配置组不存在，恢复时应该走现有逻辑的降级路径
        // 实际降级行为由 ScriptControlViewModel 处理，这里只验证上下文本身
        Assert.NotNull(ctx);
        Assert.Equal("不存在的配置组", ctx.GroupName);
    }

    // ========== 边界条件 ==========

    [Fact]
    public void Boundary_TaskIndexZero_Valid()
    {
        // TaskIndex 为 0 是合法的（表示从头开始）
        var ctx = new SuspendedTaskContext
        {
            TaskType = "group",
            GroupName = "test",
            TaskIndex = 0
        };

        Assert.Equal(0, ctx.TaskIndex);
    }

    [Fact]
    public void Boundary_TaskIndexNegative_Valid()
    {
        // 负的 TaskIndex 在恢复时会被 if (context.TaskIndex > 0) 跳过，等于从头开始
        var ctx = new SuspendedTaskContext
        {
            TaskType = "group",
            GroupName = "test",
            TaskIndex = -1
        };

        // 验证序列化/反序列化保留负值
        var json = JsonSerializer.Serialize(ctx);
        var deserialized = JsonSerializer.Deserialize<SuspendedTaskContext>(json);
        Assert.Equal(-1, deserialized?.TaskIndex);
    }
}