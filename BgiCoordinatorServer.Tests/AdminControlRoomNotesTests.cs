using System.Text.Json;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class AdminControlRoomNotesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bgi-control-notes-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void NoteSurvivesServerRestartAndCanBeClearedWhileRoomIsOffline()
    {
        var store = new AdminControlRoomNotes(_root);
        Assert.True(store.TrySet("TEAM01", "甲、乙晚班", knownRoom: true));
        Assert.True(store.TrySet("TEAM02", "白班", knownRoom: true));
        Assert.True(store.TrySet("TEAM01", "甲、丙晚班", knownRoom: true));

        var restartedStore = new AdminControlRoomNotes(_root);
        var snapshot = RoomMonitorSnapshot.Create(new RoomManager(), DateTime.UtcNow, TimeSpan.FromMinutes(2),
            notes: restartedStore.Read());
        Assert.Equal(0, snapshot.ConnectedControlRoomCount);
        Assert.Collection(snapshot.ControlRooms,
            room => { Assert.Equal("TEAM01", room.Code); Assert.Equal("甲、丙晚班", room.Note); },
            room => { Assert.Equal("TEAM02", room.Code); Assert.Equal("白班", room.Note); });

        Assert.True(restartedStore.TrySet("TEAM01", "", knownRoom: false));
        var remaining = Assert.Single(new AdminControlRoomNotes(_root).Read());
        Assert.Equal("TEAM02", remaining.Key);
    }

    [Fact]
    public void NoteBelongsToTheAssistantRoomEvenWhenTheWorldRoomHasTheSameCode()
    {
        var manager = new RoomManager();
        var code = manager.CreateRoom("world", "房主");
        manager.AddToControlRoom("CTRL_" + code, "assistant", "10001", "甲");
        var snapshot = RoomMonitorSnapshot.Create(manager, DateTime.UtcNow, TimeSpan.FromMinutes(2),
            notes: new Dictionary<string, string> { [code] = "助手组" });

        Assert.Equal("助手组", Assert.Single(snapshot.ControlRooms).Note);
        Assert.Equal(code, Assert.Single(snapshot.Rooms).Code);
        using var world = JsonDocument.Parse(JsonSerializer.Serialize(snapshot.Rooms[0], new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.False(world.RootElement.TryGetProperty("note", out _));
    }

    [Fact]
    public void UnknownRoomCannotBeAnnotatedAndDamagedFileIsNotOverwritten()
    {
        var store = new AdminControlRoomNotes(_root);
        Assert.False(store.TrySet("WORLD1", "备注", knownRoom: false));
        Assert.Empty(store.Read());

        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "assistant-room-notes.json");
        const string damaged = "{\"TEAM01\":";
        File.WriteAllText(file, damaged);
        Assert.Throws<JsonException>(() => store.Read());
        Assert.Throws<JsonException>(() => store.TrySet("TEAM01", "新备注", knownRoom: true));
        Assert.Equal(damaged, File.ReadAllText(file));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
