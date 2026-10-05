using System.Diagnostics;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
public sealed class StopFenceWireCompatibilityTests
{
    private static BgiExternalResponse Response(long version,string? timestampField=null)=>new(){Success=true,Data=
        "{\"bgiEpoch\":{\"processId\":42,\"startTicksUtc\":99},\"stopVersion\":"+version+
        ",\"monotonicFrequency\":"+Stopwatch.Frequency+(timestampField is null?"":",\"lastManualStopTimestamp\":"+timestampField)+"}"};
    [Fact]
    public void FreshBgiWireOmitsNullTimestamp_AcceptsExplicitVersionZero()
    {
        var authority=WorkflowStopAuthority.Parse(Response(0),"42:99","42:99");
        Assert.NotNull(authority);Assert.Equal(0,authority.Version);Assert.Null(authority.LastManualStopTimestamp);
        var intent=WorkflowStopAuthority.FromExplicitIntent(authority,"fresh-ui-intent",Stopwatch.GetTimestamp());
        Assert.Equal("42:99",intent.Epoch);Assert.Equal(0,intent.Version);
    }
    [Fact]
    public void MissingTimestampAfterStopOrEpochChange_RemainsUnknown()
    {
        Assert.Null(WorkflowStopAuthority.Parse(Response(7),"42:99","42:99"));
        Assert.Null(WorkflowStopAuthority.Parse(Response(0),"42:99","42:100"));
        Assert.Null(WorkflowStopAuthority.Parse(Response(0),null,"42:99"));
    }
    [Fact]
    public void ZeroVersionWithContradictoryTimestamp_RemainsUnknown()
        =>Assert.Null(WorkflowStopAuthority.Parse(Response(0,"123"),"42:99","42:99"));
}
