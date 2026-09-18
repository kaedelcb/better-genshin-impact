using System.Collections.Generic;
using BetterGenshinImpact.ViewModel.Pages;
using Xunit;

namespace BetterGenshinImpact.UnitTest.ViewModelTests;

/// <summary>
/// ASTRA 会诊二轮夹具：重命名数据丢失路径与批次委派误报成功的纯判定验证。
/// </summary>
public class OneDragonFlowExecutionGuardsTests
{
    private const string Dir = @"E:\Configs\OneDragon";

    [Fact]
    public void Rename_CaseOnlyChange_SamePhysicalFile_Rejected()
    {
        // ASTRA P0 反例：Alpha→alpha，Windows 上同一文件，"写新删旧"会删掉刚写入的目标
        var decision = OneDragonFlowExecutionGuards.EvaluateRename(
            Dir + "\\Alpha.json", Dir + "\\alpha.json", "alpha",
            new List<string>(), newFileExistsOnDisk: true);

        Assert.Equal(OneDragonRenameDecision.CaseOnlyRenameRejected, decision);
    }

    [Fact]
    public void Rename_NameConflict_IsCaseInsensitive()
    {
        var decision = OneDragonFlowExecutionGuards.EvaluateRename(
            Dir + "\\Alpha.json", Dir + "\\BETA.json", "BETA",
            new List<string> { "beta" }, newFileExistsOnDisk: false);

        Assert.Equal(OneDragonRenameDecision.NameConflict, decision);
    }

    [Fact]
    public void Rename_DiskFileConflict_Rejected()
    {
        // 目标路径被列表外孤立文件占用：不静默覆盖
        var decision = OneDragonFlowExecutionGuards.EvaluateRename(
            Dir + "\\Alpha.json", Dir + "\\Beta.json", "Beta",
            new List<string>(), newFileExistsOnDisk: true);

        Assert.Equal(OneDragonRenameDecision.DiskFileConflict, decision);
    }

    [Fact]
    public void Rename_NormalChange_Allowed()
    {
        var decision = OneDragonFlowExecutionGuards.EvaluateRename(
            Dir + "\\Alpha.json", Dir + "\\Beta.json", "Beta",
            new List<string> { "Gamma" }, newFileExistsOnDisk: false);

        Assert.Equal(OneDragonRenameDecision.Allow, decision);
    }

    [Fact]
    public void BatchDelegation_SingleTaskExecution_NeverSkips()
    {
        // ASTRA 二轮 P1：外部驱动者经单项执行跑委派组，跳过即「未执行却报成功」
        var skip = OneDragonFlowExecutionGuards.ShouldSkipForBatchDelegation(
            isSingleTaskExecution: true, new List<string> { "锄地组" }, "锄地组", isDefaultTask: false);

        Assert.False(skip);
    }

    [Fact]
    public void BatchDelegation_WholeDragonDelegatedGroup_Skips()
    {
        var skip = OneDragonFlowExecutionGuards.ShouldSkipForBatchDelegation(
            isSingleTaskExecution: false, new List<string> { "锄地组" }, "锄地组", isDefaultTask: false);

        Assert.True(skip);
    }

    [Fact]
    public void BatchDelegation_DefaultTaskInBatchList_DoesNotSkip()
    {
        var skip = OneDragonFlowExecutionGuards.ShouldSkipForBatchDelegation(
            isSingleTaskExecution: false, new List<string> { "自动秘境" }, "自动秘境", isDefaultTask: true);

        Assert.False(skip);
    }

    [Fact]
    public void BatchDelegation_NoBatchList_DoesNotSkip()
    {
        Assert.False(OneDragonFlowExecutionGuards.ShouldSkipForBatchDelegation(
            isSingleTaskExecution: false, null, "锄地组", isDefaultTask: false));
        Assert.False(OneDragonFlowExecutionGuards.ShouldSkipForBatchDelegation(
            isSingleTaskExecution: false, new List<string>(), "锄地组", isDefaultTask: false));
    }
}
