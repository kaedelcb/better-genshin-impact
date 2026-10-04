using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// Host-owned synchronous source for the pre-intent local wait decision.
/// ContinueAdmission is only a precheck; the async admission boundary must still decide whether submission is allowed.
/// </summary>
public sealed class WaitDecisionSource
{
    private readonly Func<WaitDecisionRequest, LocalWaitDecisionRecord> _decide;

    public WaitDecisionSource(Func<WaitDecisionRequest, LocalWaitDecisionRecord> decide)
        => _decide = decide ?? throw new ArgumentNullException(nameof(decide));

    public LocalWaitDecisionRecord Decide(WaitDecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return _decide(request) ?? Hold(request, "等待判定来源返回空结果");
        }
        catch (Exception ex)
        {
            return Hold(request, "等待判定来源异常（" + ex.GetType().Name + "）：" + ex.Message);
        }
    }

    private static LocalWaitDecisionRecord Hold(WaitDecisionRequest request, string reason)
        => new()
        {
            Kind = LocalWaitDecisionKind.Hold,
            Context = new LocalWaitDecisionContext
            {
                RunId = request.RunId,
                WorkflowId = request.WorkflowId,
                WorkflowRevision = request.WorkflowRevision,
                RecordRevision = request.RecordRevision,
                CursorNodeId = request.CursorNodeId,
                CursorOccurrence = request.CursorOccurrence,
                CursorLoopIteration = request.CursorLoopIteration,
                NodeId = request.NodeId,
                SequenceIndex = request.SequenceIndex,
                Occurrence = request.Occurrence,
                LoopIteration = request.LoopIteration,
                Attempt = request.Attempt,
            },
            Reason = reason,
            NoSendConfirmed = true,
        };
}
