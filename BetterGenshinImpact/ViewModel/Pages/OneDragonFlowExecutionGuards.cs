using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.ViewModel.Pages;

/// <summary>
/// 一条龙配置重命名/批次委派的纯判定逻辑（ASTRA 会诊二轮：从 VM 抽出以便夹具验证数据丢失路径）。
/// </summary>
internal enum OneDragonRenameDecision
{
    /// <summary>允许执行重命名。</summary>
    Allow,
    /// <summary>与列表中其他配置重名（Windows 不区分大小写，按 OrdinalIgnoreCase 判定）。</summary>
    NameConflict,
    /// <summary>仅大小写不同的重命名——Windows 上新旧路径指向同一物理文件，写后删旧等于删新，拒绝。</summary>
    CaseOnlyRenameRejected,
    /// <summary>目标路径已被磁盘上的其他文件占用（不在配置列表内的孤立文件），拒绝覆盖。</summary>
    DiskFileConflict,
}

internal static class OneDragonFlowExecutionGuards
{
    /// <summary>
    /// 重命名前判定。调用方保证：newName 非空、与旧名不Ordinal相等、目标路径受保护检查已另行完成。
    /// </summary>
    /// <param name="oldFilePath">旧配置文件完整路径</param>
    /// <param name="newFilePath">新配置文件完整路径</param>
    /// <param name="newName">新名称</param>
    /// <param name="otherConfigNames">列表中除被改名项外的其他配置名</param>
    /// <param name="newFileExistsOnDisk">目标路径在磁盘上是否已存在文件</param>
    public static OneDragonRenameDecision EvaluateRename(
        string oldFilePath,
        string newFilePath,
        string newName,
        IEnumerable<string> otherConfigNames,
        bool newFileExistsOnDisk)
    {
        // Windows 文件系统不区分大小写：列表重名与路径等价一律按 OrdinalIgnoreCase
        if (otherConfigNames.Any(n => string.Equals(n, newName, StringComparison.OrdinalIgnoreCase)))
        {
            return OneDragonRenameDecision.NameConflict;
        }

        if (string.Equals(oldFilePath, newFilePath, StringComparison.OrdinalIgnoreCase))
        {
            // 仅大小写变化的重命名：新旧路径是同一物理文件，"写新删旧"会删掉刚写入的文件（ASTRA P0 反例）
            return OneDragonRenameDecision.CaseOnlyRenameRejected;
        }

        if (newFileExistsOnDisk)
        {
            // 目标路径被列表外孤立文件占用，拒绝静默覆盖
            return OneDragonRenameDecision.DiskFileConflict;
        }

        return OneDragonRenameDecision.Allow;
    }

    /// <summary>
    /// 新增配置名称判定（ASTRA 三轮阻断项）：与重命名同一威胁模型——Windows 不区分大小写，
    /// 列表查重 Ordinal 时 "alpha" 可绕过 "Alpha" 并覆盖同一物理文件；列表外孤立原生文件同样不得静默覆盖。
    /// </summary>
    public static OneDragonRenameDecision EvaluateNewConfigName(
        string newName,
        IEnumerable<string> existingNames,
        bool newFileExistsOnDisk)
    {
        if (existingNames.Any(n => string.Equals(n, newName, StringComparison.OrdinalIgnoreCase)))
        {
            return OneDragonRenameDecision.NameConflict;
        }

        if (newFileExistsOnDisk)
        {
            return OneDragonRenameDecision.DiskFileConflict;
        }

        return OneDragonRenameDecision.Allow;
    }

    /// <summary>
    /// 批次委派跳过判定。单项显式执行（Descriptor.TaskId != null）绝不适用批次委派——
    /// 外部驱动者正是通过单项执行来运行被委派的组，跳过会造成"未执行却报成功"（ASTRA 二轮 P1）。
    /// </summary>
    public static bool ShouldSkipForBatchDelegation(
        bool isSingleTaskExecution,
        IReadOnlyCollection<string>? batchGroupNames,
        string taskName,
        bool isDefaultTask)
    {
        if (isSingleTaskExecution)
        {
            return false;
        }
        return batchGroupNames is { Count: > 0 } && batchGroupNames.Contains(taskName) && !isDefaultTask;
    }
}
