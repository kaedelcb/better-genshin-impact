namespace MultiplayerHoeingAssistant.Services;

/// <summary>An immutable, process-local capability produced only by successful acquisition.</summary>
public sealed class LeaseOwnerCapability
{
    internal LeaseOwnerCapability(ArbitrationLeaseStore store, string leaseId, string ownerEpoch)
    {
        Store = store;
        LeaseId = leaseId;
        OwnerEpoch = ownerEpoch;
    }

    internal ArbitrationLeaseStore Store { get; }
    public string LeaseId { get; }
    public string OwnerEpoch { get; }
    internal bool Matches(string leaseId, string ownerEpoch)
        => LeaseId == leaseId && OwnerEpoch == ownerEpoch;
}
