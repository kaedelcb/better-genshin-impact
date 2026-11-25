using System.Reflection;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[Collection("TaskCenterPipeTransport")]
public sealed class RoomOrchestrationResetFenceTests
{
    [Fact]
    public void SameRoomCodeOnAnotherServerDoesNotInheritFenceAndReturningKeepsOriginalBarrier()
    {
        // Exercise the production room-keyed fence, including a stop while disconnected and a
        // late reply carrying the previous control context. No live assistant room is needed.
        Assert.True(AssistantDataDirectory.IsExplicit);
        var vm = new MainViewModel();
        var config = new AssistConfig { PlayerUid = "self", ServerUrl = "https://alpha.example/" };
        Field("_config").SetValue(vm, config);
        Field("_roomCode").SetValue(vm, "ROOM01");
        var alphaContext = Context();
        Advance(1000, alphaContext);
        Assert.Equal(1000, Read());

        config.ServerUrl = "https://beta.example/";
        Assert.Equal(0, Read());
        Advance(1001, alphaContext); // The reply belongs to the previous server.
        Assert.Equal(0, Read());
        Advance(5, Context());
        Assert.Equal(5, Read());

        config.ServerUrl = "https://alpha.example"; // Equivalent normalized address.
        Assert.Equal(1000, Read());
        config.ServerUrl = "https://beta.example";
        Assert.Equal(5, Read());

        string Context() => (string)Method("CurrentControlContext").Invoke(vm, null)!;
        int Read() => (int)Method("ReadCoordinatedResetFence").Invoke(vm, null)!;
        void Advance(int generation, string context) => Method("AdvanceCoordinatedResetFence").Invoke(vm, [generation, context]);
    }

    private static MethodInfo Method(string name) => typeof(MainViewModel)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static FieldInfo Field(string name) => typeof(MainViewModel)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
}
