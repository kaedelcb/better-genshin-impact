namespace BgiCoordinatorServer.Services;

public sealed record HostWorldSnapshot(string SessionId, string HostUid, IReadOnlyList<string> ExecutorUids,
    string Phase, IReadOnlyList<string>? RouteNames, string? Reason, string? ManifestDigest = null);

// The room lock protects all transitions. The terminal snapshot is immutable across close and retries.
internal sealed class HostWorldExecution(string sessionId, string hostUid)
{
    internal HostWorldSnapshot Snapshot { get; private set; } = new(sessionId, hostUid, [], "waiting", null, null);
    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);

    internal bool IsTerminal => Snapshot.Phase is "completed" or "noRoutes" or "expCap" or "aborted";

    internal static bool IsValidUid(string uid) => !string.IsNullOrWhiteSpace(uid) && uid.Length <= 128 && uid == uid.Trim();

    internal bool IsExecutor(string uid) => uid != Snapshot.HostUid
        && (Snapshot.Phase == "waiting" || Snapshot.ExecutorUids.Contains(uid, StringComparer.Ordinal));

    internal void Start(IEnumerable<string> executors)
    {
        if (Snapshot.Phase == "waiting")
            Snapshot = Snapshot with { Phase = "ready", ExecutorUids = Array.AsReadOnly(executors.ToArray()) };
    }

    internal void Publish(IReadOnlyList<string> routes, string? manifestDigest)
    {
        if (Snapshot.RouteNames is { } existing)
        {
            if (!existing.SequenceEqual(routes, StringComparer.Ordinal) || Snapshot.ManifestDigest != manifestDigest)
                throw new InvalidOperationException("host_world_routes_frozen");
            return;
        }
        if (IsTerminal) return;
        if (Snapshot.Phase != "ready") throw new InvalidOperationException("host_world_not_ready");
        Snapshot = Snapshot with
        {
            Phase = routes.Count == 0 ? "noRoutes" : "running",
            RouteNames = Array.AsReadOnly(routes.ToArray()), ManifestDigest = manifestDigest,
        };
    }

    internal void Finish(string uid)
    {
        if (IsTerminal) return;
        if (Snapshot.Phase != "running") throw new InvalidOperationException("host_world_not_running");
        _finished.Add(uid);
        if (Snapshot.ExecutorUids.All(_finished.Contains)) Snapshot = Snapshot with { Phase = "completed" };
    }

    internal void ReachExpCap()
    {
        if (Snapshot.Phase == "running") Snapshot = Snapshot with { Phase = "expCap" };
    }

    internal bool Abort(string reason)
    {
        if (IsTerminal) return false;
        Snapshot = Snapshot with { Phase = "aborted", Reason = reason };
        return true;
    }
}
