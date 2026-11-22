namespace BgiCoordinatorServer.Services;

public sealed record HostWorldSnapshot(string SessionId, string HostUid, IReadOnlyList<string> ExecutorUids,
    string Phase, IReadOnlyList<string>? RouteNames, string? Reason, string? ManifestDigest = null,
    IReadOnlyList<string>? StandbyUids = null);

// The room lock protects all transitions. The terminal snapshot is immutable across close and retries.
internal sealed class HostWorldExecution(string sessionId, string hostUid, bool hostStandby)
{
    internal HostWorldSnapshot Snapshot { get; private set; } = new(sessionId, hostUid, [], "waiting", null, null);
    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
    private bool _usesStandby = hostStandby;
    internal bool UsesStandby => System.Threading.Volatile.Read(ref _usesStandby);
    internal void NoteStandbyRequest() => System.Threading.Volatile.Write(ref _usesStandby, true);

    internal bool IsTerminal => Snapshot.Phase is "completed" or "noRoutes" or "expCap" or "aborted";

    internal static bool IsValidUid(string uid) => !string.IsNullOrWhiteSpace(uid) && uid.Length <= 128 && uid == uid.Trim();

    internal bool IsExecutor(string uid, bool requestedStandby) => Snapshot.Phase == "waiting"
        ? !requestedStandby : Snapshot.ExecutorUids.Contains(uid, StringComparer.Ordinal);

    internal void Start(IEnumerable<string> executors, string[] standbyUids)
    {
        if (Snapshot.Phase != "waiting") return;
        if (standbyUids.Length == 0)
        {
            Snapshot = Snapshot with { Phase = "regular" };
            System.Threading.Volatile.Write(ref _usesStandby, false);
            return;
        }
        var eligible = executors.ToArray();
        Snapshot = Snapshot with
        {
            Phase = eligible.Length == 0 ? "aborted" : "ready",
            ExecutorUids = Array.AsReadOnly(eligible), StandbyUids = Array.AsReadOnly(standbyUids),
            Reason = eligible.Length == 0 ? "全员选择不跟跑，本场不开跑" : null,
        };
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
        if (IsTerminal || Snapshot.Phase == "regular") return false;
        Snapshot = Snapshot with { Phase = "aborted", Reason = reason };
        return true;
    }
}
