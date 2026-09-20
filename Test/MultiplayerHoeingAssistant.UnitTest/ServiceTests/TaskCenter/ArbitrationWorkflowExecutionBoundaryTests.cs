using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

// R5.2 B2-gamma step2: delegation contract fixtures for the successor-submission decorator.
// Pins two things: (1) only SubmitAsync is rerouted to the admission face;
// (2) capabilities / terminal observation / remote cancel delegate verbatim to inner.
public class ArbitrationWorkflowExecutionBoundaryTests
{
    private sealed class StubBoundary : IWorkflowExecutionBoundary
    {
        public int SubmitCalls;
        public int AwaitCalls;
        public int CancelCalls;
        public bool SingleNativeSupported { get; set; } = true;
        public bool SuppressConfigCompletionSupported { get; set; }

        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            SubmitCalls++;
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("inner-job"));
        }

        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
        {
            AwaitCalls++;
            return Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
        }

        public Task RequestCancelAsync(string jobId, CancellationToken ct)
        {
            CancelCalls++;
            return Task.CompletedTask;
        }
    }

    private static WorkflowSubmitRequest DummyRequest()
    {
        var run = new WorkflowRunRecord { RunId = "run-x", WorkflowId = "wf-x" };
        var node = new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig" };
        return new WorkflowSubmitRequest(run, new WorkflowNodeOccurrence("n-1", 0, 0, 0), node, true);
    }

    [Fact]
    public async Task SubmitAsync_RoutesToAdmission_NotToInner()
    {
        var inner = new StubBoundary();
        var admitted = 0;
        var decorator = new ArbitrationWorkflowExecutionBoundary(inner, (req, ct) =>
        {
            admitted++;
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("admitted-job"));
        });

        var result = await decorator.SubmitAsync(DummyRequest(), default);

        Assert.Equal(1, admitted);
        Assert.Equal(0, inner.SubmitCalls);
        Assert.Equal("admitted-job", result.JobId);
    }

    [Fact]
    public void Capabilities_MirrorInner()
    {
        var inner = new StubBoundary { SingleNativeSupported = false, SuppressConfigCompletionSupported = true };
        var decorator = new ArbitrationWorkflowExecutionBoundary(inner, (_, _) => throw new InvalidOperationException("must not be called"));

        Assert.False(decorator.SingleNativeSupported);
        Assert.True(decorator.SuppressConfigCompletionSupported);
    }

    [Fact]
    public async Task AwaitAndCancel_DelegateToInner()
    {
        var inner = new StubBoundary();
        var decorator = new ArbitrationWorkflowExecutionBoundary(inner, (_, _) => throw new InvalidOperationException("must not be called"));

        var terminal = await decorator.AwaitTerminalAsync("job-1", default);
        await decorator.RequestCancelAsync("job-1", default);

        Assert.Equal("succeeded", terminal.Terminal);
        Assert.Equal(1, inner.AwaitCalls);
        Assert.Equal(1, inner.CancelCalls);
    }
}
