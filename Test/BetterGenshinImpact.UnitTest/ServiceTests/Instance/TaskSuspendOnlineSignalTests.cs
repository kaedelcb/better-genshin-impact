using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoOnline;
using BetterGenshinImpact.Service.Instance.MessageHandlers;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

/// <summary>
/// 「联机锄地上线」信号任务的挂起识别：HandleTaskSuspend 不保存信号任务的中断上下文（避免恢复后重复触发上线、无限循环）。
/// 识别按项目内容（任务注册名 + 独立任务恒为空的 FolderName），不按组名。
/// </summary>
public class TaskSuspendOnlineSignalTests
{
    [Fact]
    public void IsOnlineSignalTask_SoloTaskProject_ReturnsTrue()
    {
        // 配置组形态：独立任务项目由 BuildSoloTaskProject 构造（FolderName 恒为空）
        var project = BetterGenshinImpact.Core.Script.Group.ScriptGroupProject.BuildSoloTaskProject(NotifyOnlineTask.TaskName);
        Assert.Equal("SoloTask", project.Type);
        Assert.True(InstanceRequestHandler.IsOnlineSignalTask(project.Name, project.FolderName));
    }

    [Fact]
    public void IsOnlineSignalTask_OtherSoloTask_ReturnsFalse()
    {
        // 同为空 FolderName 的其它独立任务（如"锄地一条龙（联机）"）不是信号任务，必须照常保存上下文
        Assert.False(InstanceRequestHandler.IsOnlineSignalTask("锄地一条龙（联机）", ""));
    }

    [Fact]
    public void IsOnlineSignalTask_JavascriptProjectWithSameName_ReturnsFalse()
    {
        // JS/Pathing/KeyMouse 项目 FolderName 非空，即使撞名也不误判
        Assert.False(InstanceRequestHandler.IsOnlineSignalTask(NotifyOnlineTask.TaskName, "someFolder"));
    }

    [Fact]
    public void IsOnlineSignalTask_NullOrEmptyName_ReturnsFalse()
    {
        Assert.False(InstanceRequestHandler.IsOnlineSignalTask(null, null));
        Assert.False(InstanceRequestHandler.IsOnlineSignalTask("", ""));
    }

    [Fact]
    public void SignalTaskName_MatchesSoloTaskRegistry()
    {
        // 识别名与注册名一致：NotifyOnlineTask.TaskName 必须在 SoloTaskRegistry 注册且能创建实例
        Assert.Contains(NotifyOnlineTask.TaskName, SoloTaskRegistry.AvailableTasks);
        var task = SoloTaskRegistry.CreateTask(NotifyOnlineTask.TaskName, null);
        Assert.IsType<NotifyOnlineTask>(task);
        Assert.Equal(NotifyOnlineTask.TaskName, task!.Name);
    }
}
