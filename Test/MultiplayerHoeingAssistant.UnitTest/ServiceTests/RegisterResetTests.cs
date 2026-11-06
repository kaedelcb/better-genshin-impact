using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class RegisterResetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "register-reset-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_root, "registers.json");
    private DateTimeOffset _now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private readonly RegisterBankService _service;
    public RegisterResetTests() => _service = new(FilePath, () => _now, startTimer: false);

    private void Configure(int number, string mode)
    {
        var entry = _service.GetSnapshot().Entries[number - 1];
        _service.Configure(new RegisterConfigurationChange
        {
            Number = number, Name = "保留名称 " + number, ResetMode = mode,
            ResetHour = 4, ResetMinute = 30, DelaySeconds = 3600,
            ExpectedSettingsRevision = entry.SettingsRevision
        });
    }
    private static object Settings(RegisterEntry e) => new
    { e.Number, e.Name, e.ResetMode, e.ResetHour, e.ResetMinute, e.DelaySeconds, e.SettingsRevision };
    private void SetAllOne() => _service.Set(new() { Values = Enumerable.Repeat<int?>(1, RegisterBank.Count).ToList() });

    [Fact]
    public void ManualResetOnePreservesOtherValuesNamesRulesAndRevisions()
    {
        Configure(1, RegisterBank.AfterChange); Configure(2, RegisterBank.Daily); SetAllOne();
        var before = _service.GetSnapshot(); _now = _now.AddSeconds(5);
        var after = _service.Reset(1);
        Assert.True(after.Available);
        Assert.Equal(0, after.Entries[0].Value);
        Assert.Null(after.Entries[0].NextResetAtUtc);
        Assert.Equal(JsonSerializer.Serialize(Settings(before.Entries[0])), JsonSerializer.Serialize(Settings(after.Entries[0])));
        Assert.Equal(JsonSerializer.Serialize(before.Entries.Skip(1)), JsonSerializer.Serialize(after.Entries.Skip(1)));
        using var reopened = new RegisterBankService(FilePath, () => _now, startTimer: false);
        Assert.Equal(0, reopened.GetSnapshot().Entries[0].Value);
        Assert.All(reopened.GetSnapshot().Entries.Skip(1), e => Assert.Equal(1, e.Value));
    }

    [Fact]
    public void ResetAllClearsTenValuesAndDeadlinesPersistentlyWithoutChangingConfiguration()
    {
        Configure(1, RegisterBank.AfterChange); Configure(2, RegisterBank.Daily); Configure(10, RegisterBank.Never);
        SetAllOne(); var before = _service.GetSnapshot();
        var after = _service.Reset();
        Assert.True(after.Available);
        Assert.All(after.Entries, e => { Assert.Equal(0, e.Value); Assert.Null(e.NextResetAtUtc); });
        Assert.Equal(JsonSerializer.Serialize(before.Entries.Select(Settings)), JsonSerializer.Serialize(after.Entries.Select(Settings)));
        using var reopened = new RegisterBankService(FilePath, () => _now, startTimer: false);
        Assert.All(reopened.GetSnapshot().Entries, e => Assert.Equal(0, e.Value));
        // A manual reset must not invalidate an already open settings editor's revision.
        var original = before.Entries[0];
        _service.Configure(new() { Number = 1, Name = original.Name, ResetMode = original.ResetMode,
            ResetHour = original.ResetHour, ResetMinute = original.ResetMinute, DelaySeconds = original.DelaySeconds,
            ExpectedSettingsRevision = original.SettingsRevision });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public void InvalidSingleNumberDoesNotTurnIntoAnAllReset(int number)
    {
        SetAllOne(); var before = File.ReadAllBytes(FilePath);
        Assert.ThrowsAny<ArgumentException>(() => _service.Reset(number));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void ContendedAllResetDoesNotPartiallyClearValues()
    {
        SetAllOne(); var before = File.ReadAllBytes(FilePath);
        using (var competingWriter = new FileStream(Path.Combine(_root, ".nexus-config-write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => _service.Reset());
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.All(_service.GetSnapshot().Entries, e => Assert.Equal(1, e.Value));
    }

    [Fact]
    public async Task BankUsesSelectedEndpointDelegateAndUnavailableStateCannotClaimZero()
    {
        var calls = new List<int?>();
        var bank = new RegisterBankViewModel(_ => throw new InvalidOperationException(), n =>
        { calls.Add(n); return Task.FromResult(_service.Reset(n)); });
        SetAllOne(); bank.Apply(_service.GetSnapshot());
        Assert.True(bank.CanReset);
        await bank.ResetAsync(4);
        Assert.Equal("0", bank.Cells[3].ValueText);
        Assert.Equal("1", bank.Cells[0].ValueText);
        await bank.ResetAsync();
        Assert.Equal(new int?[] { 4, null }, calls);
        Assert.All(bank.Cells, c => Assert.Equal("0", c.ValueText));
        bank.Apply(null);
        Assert.False(bank.CanReset);
        Assert.All(bank.Cells, c => Assert.Equal("?", c.ValueText));
    }

    [Fact]
    public async Task FailedOrUnknownRemoteResetDoesNotInventSuccessfulValues()
    {
        SetAllOne();
        var bank = new RegisterBankViewModel(_ => throw new InvalidOperationException(), _ =>
            Task.FromException<RegisterBankSnapshot>(new InvalidOperationException("执行端归零结果未确认")));
        bank.Apply(_service.GetSnapshot());
        await Assert.ThrowsAsync<InvalidOperationException>(() => bank.ResetAsync());
        Assert.False(bank.IsBusy);
        Assert.DoesNotContain(bank.Cells, c => c.ValueText == "0");
        Assert.All(_service.GetSnapshot().Entries, e => Assert.Equal(1, e.Value));
        var unknown = new RegisterBankViewModel(_ => throw new InvalidOperationException(), _ =>
            Task.FromResult(new RegisterBankSnapshot { Available = false, Error = "执行端状态未知" }));
        unknown.Apply(_service.GetSnapshot());
        await Assert.ThrowsAsync<InvalidOperationException>(() => unknown.ResetAsync(1));
        Assert.All(unknown.Cells, c => Assert.Equal("?", c.ValueText));
    }

    [Fact]
    public async Task InFlightResetDisablesAnotherResetAndUsesOneRequest()
    {
        var pending = new TaskCompletionSource<RegisterBankSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var bank = new RegisterBankViewModel(_ => throw new InvalidOperationException(), _ =>
        { requests++; return pending.Task; });
        SetAllOne(); bank.Apply(_service.GetSnapshot());
        var first = bank.ResetAsync();
        Assert.True(bank.IsBusy); Assert.False(bank.CanReset);
        try { await bank.ResetAsync(1); } catch (InvalidOperationException) { }
        Assert.Equal(1, requests);
        pending.SetResult(_service.Reset()); await first;
        Assert.False(bank.IsBusy); Assert.True(bank.CanReset);
        Assert.All(bank.Cells, c => Assert.Equal("0", c.ValueText));
    }

    public void Dispose()
    { _service.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
