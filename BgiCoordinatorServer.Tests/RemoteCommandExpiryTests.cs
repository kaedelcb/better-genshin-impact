using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class RemoteCommandExpiryTests
{
    [Fact]
    public void LegacyCommandReceivesBoundedExpiry()
    {
        var manager = new RoomManager(); var command = new RemoteCommand();
        var before = DateTimeOffset.UtcNow;
        manager.CachePendingCommand("fixture", command);
        Assert.InRange(command.ExpiresAtUtc!.Value, before.AddMinutes(2), DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.Single(manager.GetAndClearPendingCommands("fixture"));
        Assert.Empty(manager.GetAndClearPendingCommands("fixture"));
    }

    [Fact]
    public void ExpiredCommandNeverEntersPendingDelivery()
    {
        var manager = new RoomManager();
        manager.CachePendingCommand("fixture", new RemoteCommand { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
        Assert.Empty(manager.GetAndClearPendingCommands("fixture"));
    }

    [Fact]
    public void ExpiryIsCheckedAgainOnReconnect()
    {
        var manager = new RoomManager(); var delayed = new RemoteCommand { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) };
        manager.CachePendingCommand("fixture", delayed);
        // Deterministic clock simulation via this fixture's own object; no live server or waiting.
        delayed.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var valid = new RemoteCommand { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) };
        manager.CachePendingCommand("fixture", valid);
        Assert.Same(valid, Assert.Single(manager.GetAndClearPendingCommands("fixture")));
    }

    [Fact]
    public void RevisionReceiptPreservesEpochTicksAsString()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var json = System.Text.Json.JsonSerializer.Serialize(new RemoteCommandResult {
            ConfigRevision = "revision", TargetProcessId = 3, TargetStartTicksUtc = "638937012345678901" }, options);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(System.Text.Json.JsonValueKind.String, doc.RootElement.GetProperty("targetStartTicksUtc").ValueKind);
        Assert.Equal("638937012345678901", doc.RootElement.GetProperty("targetStartTicksUtc").GetString());
    }
}
