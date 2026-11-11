using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class RunStore
{
    // 展示清除单独保存，不改计划修订、运行原始事实或责任释放证据。
    private sealed record RunTraceReset(string WorkflowId, string[] RunIds);

    private string RunTraceResetPath(string workflowId)
        => Path.Combine(_runsDir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workflowId))) + ".trace-reset.json");

    private RunTraceReset? ReadRunTraceReset(string workflowId)
    {
        var text = TryReadAllTextOrNull(RunTraceResetPath(workflowId), "read-trace-reset");
        if (text is null) return null;
        var reset = JsonSerializer.Deserialize<RunTraceReset>(text);
        if (reset?.WorkflowId != workflowId || reset.RunIds is null || reset.RunIds.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("执行路径清除记录无法确认，保留原件。");
        return reset;
    }

    internal void ClearRunTrace(string workflowId, IReadOnlyList<WorkflowRunRecord> records)
    {
        lock (_gate)
        {
            using var ownerFence = _owner?.Store.AcquireOwnerFence(_owner);
            using var publication = AcquirePublicationLock();
            var policy = TryReadAllTextOrNull(OwnerPolicyPath, "read-owner-policy");
            if ((_requireOwnership || policy is not null) && _owner is null
                || policy is not null && policy != OwnerPolicy)
                throw new RunRecordConflictException("运行目录要求原所有者能力，未清除执行路径显示。");
            if (records.Any(run => run.WorkflowId != workflowId))
                throw new InvalidOperationException("执行路径清除不能包含其他计划。");
            var previous = ReadRunTraceReset(workflowId);
            var ids = (previous?.RunIds ?? []).Concat(records.Select(run => run.RunId))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (previous is not null && previous.RunIds.SequenceEqual(ids)) return;
            var path = RunTraceResetPath(workflowId);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new RunTraceReset(workflowId, ids), JsonOptions), Utf8NoBom);
                _owner?.Store.VerifyOwnerFence(_owner);
                WithContentionRetry(() => File.Move(temporary, path, overwrite: true));
                if (ReadRunTraceReset(workflowId) is not { } saved || !saved.RunIds.SequenceEqual(ids))
                    throw new IOException("执行路径清除记录未能读回确认。");
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } }
        }
    }

    internal IReadOnlySet<string> ListClearedRunTraceIds(IEnumerable<WorkflowRunRecord> records)
    {
        lock (_gate)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in records.GroupBy(run => run.WorkflowId, StringComparer.Ordinal))
            {
                var cleared = ReadRunTraceReset(group.Key)?.RunIds.ToHashSet(StringComparer.Ordinal);
                foreach (var run in group)
                    if (cleared?.Contains(run.RunId) == true || ExecutionResetEvidence.Valid(run)) ids.Add(run.RunId);
            }
            return ids;
        }
    }
}
