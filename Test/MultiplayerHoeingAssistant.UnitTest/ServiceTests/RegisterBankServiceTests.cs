using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class RegisterBankServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "register-bank-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_root, "registers.json");
    private DateTimeOffset _now = DateTimeOffset.Now;
    private readonly RegisterBankService _service;
    public RegisterBankServiceTests() => _service = new(FilePath, () => _now, startTimer: false);

    private static RegisterWriteSettings Write(params (int number, int value)[] entries)
    {
        var settings = new RegisterWriteSettings();
        foreach (var (number, value) in entries) settings.Values[number - 1] = value;
        return settings;
    }

    private RegisterConfigurationChange Configuration(int number, string mode = RegisterBank.Never, int delay = 1800)
        => new()
        {
            Number = number, ResetMode = mode, DelaySeconds = delay,
            ExpectedSettingsRevision = _service.GetSnapshot().Entries[number - 1].SettingsRevision
        };

    [Fact]
    public void NewBankHasExactlyTenZeroBitsAndReadingDoesNotCreateData()
    {
        var snapshot = _service.GetSnapshot();
        Assert.True(snapshot.Available);
        Assert.Equal(Enumerable.Range(1, 10), snapshot.Entries.Select(e => e.Number));
        Assert.All(snapshot.Entries, e => Assert.Equal(0, e.Value));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void AllTenBitsPersistAndTwoServiceInstancesMergeInsteadOfOverwriting()
    {
        _service.Set(Write((1, 1), (4, 1), (10, 1)));
        using var second = new RegisterBankService(FilePath, () => _now, startTimer: false);
        second.Set(Write((2, 1), (4, 0)));
        _service.Set(Write((9, 1)));
        Assert.Equal(new[] { 1, 1, 0, 0, 0, 0, 0, 0, 1, 1 }, second.GetSnapshot().Entries.Select(e => e.Value));
    }

    [Fact]
    public async Task ConcurrentLogActionsOnDifferentBitsAreNotLost()
    {
        await Task.WhenAll(Enumerable.Range(1, 10).Select(n => Task.Run(() => _service.Set(Write((n, 1))))));
        Assert.All(_service.GetSnapshot().Entries, e => Assert.Equal(1, e.Value));
    }

    [Fact]
    public async Task BackgroundClockResetsExpiredBitWithoutAnyPageOrJudgment()
    {
        using var timer = new RegisterBankService(FilePath, () => _now);
        timer.Configure(Configuration(1, RegisterBank.AfterChange, 1));
        timer.Set(Write((1, 1)));
        _now = _now.AddSeconds(2);
        var end = DateTime.UtcNow.AddSeconds(5);
        int StoredValue()
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            return json.RootElement.GetProperty("entries")[0].GetProperty("value").GetInt32();
        }
        while (StoredValue() != 0 && DateTime.UtcNow < end) await Task.Delay(30);
        Assert.Equal(0, StoredValue());
    }

    [Fact]
    public void ExpiredJudgmentIsReadOnlyAndRuleChangesCannotResurrectAnExpiredFlag()
    {
        _service.Configure(Configuration(1, RegisterBank.AfterChange, 10));
        _service.Set(Write((1, 1)));
        var before = File.ReadAllBytes(FilePath);
        _now = _now.AddSeconds(11);
        Assert.False(_service.Evaluate(new() { Values = Write((1, 1)).Values }).Passed);
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        _service.Configure(Configuration(1, RegisterBank.Never));
        Assert.Equal(0, _service.GetSnapshot().Entries[0].Value);
        _service.Set(Write((1, 1)));
        Assert.Equal(1, _service.GetSnapshot().Entries[0].Value);
    }

    [Fact]
    public void DelayCountsFromActualChangeAndRepeatedWriteOrRenameDoesNotExtendIt()
    {
        var config = Configuration(1, RegisterBank.AfterChange, 60);
        config.Name = "副本完成";
        _service.Configure(config);
        _service.Set(Write((1, 1)));
        var due = _service.GetSnapshot().Entries[0].NextResetAtUtc;
        _now = _now.AddSeconds(40);
        _service.Set(Write((1, 1)));
        config = Configuration(1, RegisterBank.AfterChange, 60);
        config.Name = "改名后的副本完成";
        _service.Configure(config);
        Assert.Equal(due, _service.GetSnapshot().Entries[0].NextResetAtUtc);
        _now = _now.AddSeconds(21);
        _service.ProcessDueResets();
        Assert.Equal(0, _service.GetSnapshot().Entries[0].Value);
        Assert.Equal("改名后的副本完成", _service.GetSnapshot().Entries[0].Name);
    }

    [Fact]
    public void ReturningToOneStartsANewCountdownAndKeepDoesNotTouchOtherBits()
    {
        _service.Configure(Configuration(10, RegisterBank.AfterChange, 60));
        _service.Set(Write((10, 1)));
        _now = _now.AddSeconds(20);
        _service.Set(Write((10, 0)));
        Assert.Null(_service.GetSnapshot().Entries[9].NextResetAtUtc);
        _service.Set(Write((10, 1)));
        Assert.Equal(_now.AddSeconds(60), _service.GetSnapshot().Entries[9].NextResetAtUtc);
        _service.Set(Write((1, 1)));
        Assert.Equal(_now.AddSeconds(60), _service.GetSnapshot().Entries[9].NextResetAtUtc);
    }

    [Fact]
    public void DailyResetUsesTheSelectedLocalTimeAndOnlyResetsThatBit()
    {
        var local = new DateTime(2026, 11, 6, 3, 55, 0, DateTimeKind.Unspecified);
        _now = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        _service.Configure(Configuration(4, RegisterBank.Daily));
        _service.Set(Write((1, 1), (4, 1)));
        Assert.Equal(4, _service.GetSnapshot().Entries[3].NextResetAtUtc!.Value.ToLocalTime().Hour);
        _now = _now.AddMinutes(5);
        _service.ProcessDueResets();
        Assert.Equal(1, _service.GetSnapshot().Entries[0].Value);
        Assert.Equal(0, _service.GetSnapshot().Entries[3].Value);
        _service.Set(Write((4, 1)));
        Assert.Equal(_now.ToLocalTime().Date.AddDays(1), _service.GetSnapshot().Entries[3].NextResetAtUtc!.Value.ToLocalTime().Date);
    }

    [Fact]
    public void RestartSettlesExpiredCountdownAndKeepsNamesAndNeverResetBits()
    {
        var config = Configuration(1, RegisterBank.AfterChange, 10); config.Name = "事件已命中";
        _service.Configure(config);
        _service.Set(Write((1, 1), (2, 1)));
        _now = _now.AddSeconds(11);
        using var restarted = new RegisterBankService(FilePath, () => _now, startTimer: false);
        Assert.Equal("事件已命中", restarted.GetSnapshot().Entries[0].Name);
        Assert.Equal(0, restarted.GetSnapshot().Entries[0].Value);
        Assert.Equal(1, restarted.GetSnapshot().Entries[1].Value);
    }

    [Theory]
    [InlineData("all", false)]
    [InlineData("any", true)]
    public void JudgmentOnlyReadsAndNamesDoNotChangeTheReferencedNumber(string match, bool expected)
    {
        var config = Configuration(1); config.Name = "任意名称"; _service.Configure(config);
        _service.Set(Write((1, 1), (4, 1)));
        var before = File.ReadAllBytes(FilePath);
        var condition = new RegisterConditionSettings { Match = match, Values = Write((1, 1), (4, 0)).Values };
        var result = _service.Evaluate(condition);
        Assert.Equal(expected, result.Passed);
        Assert.Contains("任意名称", result.Description);
        Assert.Equal(before, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void InvalidVectorsAndCancelledWritesNeverPartiallyUpdateTheBank()
    {
        _service.Set(Write((1, 1)));
        var before = File.ReadAllBytes(FilePath);
        Assert.Throws<ArgumentException>(() => _service.Set(new()));
        Assert.Throws<ArgumentException>(() => _service.Set(new() { Values = [1] }));
        var invalid = Write((1, 0), (10, 2));
        Assert.Throws<ArgumentException>(() => _service.Set(invalid));
        Assert.Throws<ArgumentException>(() => _service.Evaluate(new() { Values = Write((1, 1)).Values, Match = "unknown" }));
        Assert.Throws<OperationCanceledException>(() => _service.Set(Write((1, 0)), new CancellationToken(true)));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void ConflictingSettingsAreRejectedButConcurrentValueChangesArePreserved()
    {
        var first = Configuration(1); var stale = Configuration(1);
        _service.Set(Write((1, 1)));
        first.Name = "新名称"; _service.Configure(first);
        Assert.Equal(1, _service.GetSnapshot().Entries[0].Value);
        stale.Name = "旧窗口";
        Assert.Throws<InvalidOperationException>(() => _service.Configure(stale));
        Assert.Equal("新名称", _service.GetSnapshot().Entries[0].Name);
        var zeroDelay = Configuration(1, RegisterBank.AfterChange, 0);
        Assert.Throws<ArgumentException>(() => _service.Configure(zeroDelay));
    }

    [Fact]
    public void CorruptDataIsPreservedAndNeverUsedAsAllZeroOrOverwritten()
    {
        Directory.CreateDirectory(_root); File.WriteAllText(FilePath, "{broken source");
        Assert.False(_service.GetSnapshot().Available);
        Assert.Throws<InvalidDataException>(() => _service.Evaluate(new() { Values = Write((1, 0)).Values }));
        Assert.Throws<JsonException>(() => _service.Set(Write((1, 1))));
        Assert.Equal("{broken source", File.ReadAllText(FilePath));
    }

    [Fact]
    public async Task StartupRunnerWritesThenJudgesAndExecutesOnlyTheChosenBranch()
    {
        var reports = new List<(string Id, NodeRunState State)>();
        var runner = Runner(reports);
        var set = StartupStepKinds.Create(StartupStepKinds.SetRegister);
        set.RegisterWrite = Write((1, 1), (10, 1));
        // A write node must not read a condition or execute branches even if stray data is present.
        set.RegisterCondition = new() { Values = Write((1, 0)).Values };
        var stray = Wait(); set.TrueSteps.Add(stray);
        var condition = StartupStepKinds.Create(StartupStepKinds.JudgeRegister);
        condition.RegisterCondition!.Values = Write((1, 1), (10, 1)).Values;
        condition.RegisterWrite = Write((1, 0));
        var yes = Wait(); var no = Wait(); condition.TrueSteps.Add(yes); condition.FalseSteps.Add(no);
        await runner.RunAsync([set, condition], CancellationToken.None);
        Assert.Contains((yes.Id, NodeRunState.Success), reports);
        Assert.DoesNotContain(reports, r => r.Id == no.Id || r.Id == stray.Id);
        Assert.Equal(1, _service.GetSnapshot().Entries[0].Value);
        Assert.Contains((condition.Id, NodeRunState.CondTrue), reports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidRegisterNodeStopsWithoutExecutingLaterTasks(bool wrongNodeType)
    {
        var reports = new List<(string Id, NodeRunState State)>(); var runner = Runner(reports);
        var set = StartupStepKinds.Create(StartupStepKinds.SetRegister);
        if (wrongNodeType) { set.NodeType = "condition"; set.RegisterWrite = Write((1, 1)); }
        var later = Wait();
        await runner.RunAsync([set, later], CancellationToken.None);
        Assert.Contains((set.Id, NodeRunState.Failed), reports);
        Assert.DoesNotContain(reports, r => r.Id == later.Id);
    }

    [Fact]
    public void OldStartupConfigurationDoesNotGainRegisterNodesOrParameters()
    {
        var config = JsonSerializer.Deserialize<StartupFlowConfig>("{\"steps\":[{\"kind\":\"wait\",\"waitSeconds\":5}]}")!;
        Assert.Single(config.Steps); Assert.Null(config.Steps[0].RegisterWrite); Assert.Null(config.Steps[0].RegisterCondition);
        var json = JsonSerializer.Serialize(config);
        Assert.DoesNotContain("registerWrite", json); Assert.DoesNotContain("registerCondition", json);
    }

    private static StartupStep Wait() => new() { Kind = StartupStepKinds.Wait, WaitSeconds = 0 };
    private StartupFlowRunner Runner(List<(string Id, NodeRunState State)> reports)
    {
        var runner = new StartupFlowRunner((_, _) => throw new InvalidOperationException("No BGI call expected"),
            (_, _) => throw new InvalidOperationException("No handoff expected"), _ => { },
            (_, _) => Task.FromResult((true, "")), _ => { }, () => null, _ => { }, registerBank: _service);
        runner.NodeStateSink = (step, state, _) => reports.Add((step.Id, state));
        return runner;
    }

    public void Dispose()
    {
        _service.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
