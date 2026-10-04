using System.Reflection;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.Instance;
using MultiplayerHoeingAssistant.Services;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Execution;

public class OriginalRequestFingerprintTests
{
    [Theory]
    [InlineData("{\"idempotencyKey\":\"old-key\",\"configName\":\"中文<&>\\\"\\n\",\"taskId\":null}")]
    [InlineData("{\"expiresAtUtc\":\"2026-10-04T03:00:00.1234567+00:00\",\"bgiEpoch\":{\"startTicksUtc\":638999999999999999,\"processId\":4321}}")]
    [InlineData("{\"expiresAtUtc\":\"2026-10-04T11:00:00.1000000+08:00\",\"suppressConfigCompletionAction\":true}")]
    [InlineData("{\"z\":[null,false,1,{\"b\":\"\u2028\",\"a\":\"/\\\\\"}],\"a\":-1.25}")]
    [InlineData("{\"attempt\":1,\"occurrence\":0,\"iteration\":12,\"expectedConfigRevision\":\"ABC\",\"takeoverTicket\":null}")]
    public void AssistantFingerprint_EqualsUnchangedServerCanonicalContract(string wireJson)
    {
        var request = new InstanceIpcEnvelope { Operation = "ext.task.start", Data = JObject.Parse(wireJson) };
        var expected = ExecutionRequestContract.Fingerprint(request);
        var type = typeof(BgiExternalClient).Assembly.GetType("MultiplayerHoeingAssistant.Services.BgiOriginalRequestFingerprint");
        Assert.NotNull(type);
        var actual = type!.GetMethod("Compute", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(null, [request.Operation, wireJson]);
        Assert.Equal(expected, actual);
        var differentOperation = new InstanceIpcEnvelope { Operation = "ext.prerequisite.account", Data = request.Data };
        Assert.NotEqual(expected, ExecutionRequestContract.Fingerprint(differentOperation));
    }
}
