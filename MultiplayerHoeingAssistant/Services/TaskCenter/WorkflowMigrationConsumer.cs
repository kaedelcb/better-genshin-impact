using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

internal static class WorkflowMigrationConsumer
{
    internal const string TransactionField = "nativeMigrationTransactionId";

    internal static async Task<WorkflowSnapshot> PrepareAsync(WorkflowStore store, string workflowId,
        IResourceCatalogTransport transport)
    {
        var source = store.LoadSnapshot(workflowId);
        if (source.Document.Activation?.Status != "candidate-ready")
            throw new InvalidOperationException("只有candidate-ready候选可以正式激活");
        if (source.Document.Nodes.Count == 0 || WorkflowKindCatalog.FindUnsupportedKinds(source.Document).Count != 0)
            throw new InvalidOperationException("候选为空或有未支持机制，不能激活");
        var copy = JsonSerializer.Deserialize<WorkflowDocument>(JsonSerializer.Serialize(source.Document))!;
        foreach (var node in copy.Nodes)
        {
            var revision = await WorkflowResourceEditor.ReadRevisionAsync(node, transport);
            if (!string.Equals(revision, node.Ref?.Revision, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"资源「{node.Ref?.Config}」版本与迁移候选不一致；请先安装该候选的标准配置，原件保留");
            // R1使用小写SHA，BGI使用大写；只有同字节资源才归一化到权威读回值。
            node.Ref!.Revision = revision;
        }
        return new WorkflowSnapshot(copy, source.Revision);
    }

    internal static string Activate(WorkflowStore store, string workflowId, WorkflowSnapshot prepared)
        => store.WithMigrationWindow(() =>
        {
            var current = store.LoadSnapshot(workflowId);
            if (current.Revision != prepared.Revision || current.Document.Activation?.Status != "candidate-ready")
                throw new WorkflowRevisionConflictException("候选在核查后已变化，未执行激活");
            var document = prepared.Document;
            if (document.WorkflowId != workflowId) throw new InvalidOperationException("迁移流程身份不一致");
            var transactionId = "activate-" + Guid.NewGuid().ToString("N");
            document.ExtensionData ??= new();
            document.ExtensionData[TransactionField] = JsonSerializer.SerializeToElement(transactionId);
            var relative = workflowId + ".flow.json";
            using var tx = store.OpenMigration(workflowId);
            Require(tx.BeginTransaction(transactionId));
            Require(tx.TakeSnapshot());
            Require(tx.RecordChanges([new() { Path = relative, Kind = ChangeKind.Modified }]));
            Require(tx.ApplyReferenceUpdate(new([new(relative, ChangeKind.Modified,
                NewContent: JsonSerializer.Serialize(document), ExpectedContentHash: current.Revision)])));
            var writes = tx.LoadValidated()?.ReferenceWriteSet ?? throw new InvalidOperationException("迁移引用读回缺失");
            Require(tx.ActivateCandidate(new(relative, "candidate-ready", "active", writes[relative])));
            Require(tx.RehearseRollback());
            Require(tx.Commit());
            return transactionId;
        });

    internal static void Rollback(WorkflowStore store, string workflowId)
        => store.WithMigrationWindow(() =>
        {
            using var tx = store.OpenMigration(workflowId);
            Require(tx.TryAcquireExclusive());
            var manifest = tx.LoadValidated() ?? throw new InvalidOperationException("迁移记录缺失或损坏，原件保留；不能猜测回退");
            if (manifest.ActivationRecord is { } active && active.Path != workflowId + ".flow.json")
                throw new InvalidOperationException("回退流程身份不一致");
            Require(tx.Rollback());
            return true;
        });

    private static void Require(MigrationResult result)
    {
        if (!result.Success) throw new InvalidOperationException("迁移未完成：" + result.Reason + "；保留事务与原件，可从迁移回退恢复");
    }
}
