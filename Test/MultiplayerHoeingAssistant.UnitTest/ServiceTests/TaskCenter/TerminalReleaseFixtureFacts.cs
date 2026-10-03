using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

// Test ports provide complete simulated frozen facts instead of a bare job handle.
internal static class TerminalReleaseFixtureFacts
{
    internal static void FreezeBody(WorkflowSubmitRequest request)
    {
        var s = request.Run.CurrentSubmission!;
        s.WireRunId = request.Run.WireRunId;
        s.Epoch = request.Run.StopAuthority?.Epoch ?? "42:99";
        s.Fingerprint = "fixture-payload-" + s.Key;
        s.ExpiresAtUtc = "2030-01-01T00:00:00Z";
        s.SendAttempted = true;
    }
}
