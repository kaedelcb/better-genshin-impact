using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class LegacyMigrationCandidateServiceTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"legacy-prepare-"+Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root,true); }

    [Fact]
    public void OldTuplePreparationPreservesDuplicatesSourceAndOnceAndReusesSameIdentity()
    {
        var user=Path.Combine(_root,"oldUser");Directory.CreateDirectory(Path.Combine(user,"OneDragon"));
        var source=Path.Combine(user,"OneDragon","配置A.json");
        var text="""
        {"Name":"配置A","Version":1,"ScheduleName":"计划A","IndexId":1,
         "AccountBinding":true,"GenshinUid":"123456789","NextConfiguration":true,"NextTaskIndex":2,
         "TaskEnabledList":{"5":{"Item1":true,"Item2":"领取邮件"},"2":{"Item1":true,"Item2":"领取邮件"}}}
        """;
        File.WriteAllText(source,text);var original=File.ReadAllBytes(source);
        var store=new WorkflowStore(Path.Combine(_root,"flows"));var candidateRoot=Path.Combine(_root,"candidates");
        var first=LegacyMigrationCandidateService.Prepare(user,candidateRoot,store);
        Assert.False(first.Reused);Assert.Single(first.WorkflowIds);Assert.Equal(original,File.ReadAllBytes(source));
        var standard=JsonNode.Parse(File.ReadAllBytes(Path.Combine(first.CandidateDirectory,"standard","OneDragon","配置A.json")))!.AsObject();
        var order=standard["TaskOrder"]!.AsArray().Select(x=>x!.GetValue<string>()).ToArray();
        Assert.Equal(2,order.Length);Assert.NotEqual(order[0],order[1]);Assert.Equal(order[1],standard["NextTaskId"]!.GetValue<string>());
        var flow=store.LoadSnapshot(first.WorkflowIds[0]).Document;
        Assert.Equal("candidate-ready",flow.Activation!.Status);
        Assert.True(flow.ExtensionData!["watermark"].GetProperty("once").GetBoolean());
        var repeated=LegacyMigrationCandidateService.Prepare(user,candidateRoot,store);
        Assert.True(repeated.Reused);Assert.Equal(first.CandidateDirectory,repeated.CandidateDirectory);
        Assert.Equal(first.WorkflowIds,repeated.WorkflowIds);Assert.Single(store.List());Assert.Equal(original,File.ReadAllBytes(source));
    }

    [Fact]
    public void SameNameConflictProducesReportWithoutImportOrOverwrite()
    {
        var user=Path.Combine(_root,"oldUser");Directory.CreateDirectory(Path.Combine(user,"OneDragon"));
        File.WriteAllText(Path.Combine(user,"OneDragon","one.json"),"{\"Name\":\"same\",\"TaskEnabledList\":{\"领取邮件\":true}}");
        File.WriteAllText(Path.Combine(user,"OneDragon","two.json"),"{\"Name\":\"same\",\"TaskEnabledList\":{\"领取邮件\":false}}");
        var store=new WorkflowStore(Path.Combine(_root,"flows"));
        Assert.Throws<InvalidOperationException>(()=>LegacyMigrationCandidateService.Prepare(user,Path.Combine(_root,"candidates"),store));
        Assert.Empty(store.List());Assert.True(Directory.GetFiles(Path.Combine(_root,"candidates"),"report.md",SearchOption.AllDirectories).Length>0);
    }
}
