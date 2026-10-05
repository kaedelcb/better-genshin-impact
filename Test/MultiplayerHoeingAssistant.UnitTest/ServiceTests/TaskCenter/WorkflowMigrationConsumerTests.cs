using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class WorkflowMigrationConsumerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "migration-consumer-" + Guid.NewGuid().ToString("N"));
    private readonly WorkflowStore _store;
    public WorkflowMigrationConsumerTests() => _store = new(Path.Combine(_root,"flows"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root,true); }
    private string Seed()
    {
        var doc=new WorkflowDocument {Name="正常迁移",Activation=new() {Status="candidate-ready"},
            Nodes=[new() {NodeId="n1",Kind="resource.oneDragonConfig",Ref=new() {Config="正常标准资源",Revision="aabb"},
                ExtensionData=new() {["legacyFiltered"]=JsonSerializer.SerializeToElement(true)}},
                new() {NodeId="n2",Kind="resource.oneDragonConfig",Ref=new() {Config="正常标准资源",Revision="aabb"}}],
            ExtensionData=new() {["watermark"]=JsonSerializer.SerializeToElement(new {entryNodeId="n2",once=true}),
                ["future"]=JsonSerializer.SerializeToElement(new {keep=7})}};
        _store.Save(doc,null);return doc.WorkflowId!;
    }

    [Fact]
    public async Task OriginalHostActivatesAndRollsBackExactCandidateKeepingIdentityFiltersAndWatermark()
    {
        var id=Seed();var file=_store.List().Single().FilePath;var before=File.ReadAllBytes(file);
        var host=new TaskCenterHost(Path.Combine(_root,"flows"),Path.Combine(_root,"runs"),Path.Combine(_root,"cache.json"),()=>null,()=>true,()=>null);
        host.ResourceEditorTransportForTest=new Transport();
        var result=await host.ActivateMigrationCandidateAsync(id);
        Assert.True(result.Ok,result.Message);
        var active=_store.LoadSnapshot(id).Document;
        Assert.Equal(id,active.WorkflowId);Assert.Equal("active",active.Activation!.Status);
        Assert.Equal(new[] {"n1","n2"},active.Nodes.Select(n=>n.NodeId));
        Assert.True(active.Nodes[0].ExtensionData!["legacyFiltered"].GetBoolean());
        Assert.True(active.ExtensionData!["watermark"].GetProperty("once").GetBoolean());
        Assert.Equal("n2",active.ExtensionData["watermark"].GetProperty("entryNodeId").GetString());
        Assert.Equal("AABB",active.Nodes[1].Ref!.Revision);
        var panel=new MultiplayerHoeingAssistant.ViewModels.TaskCenterPanelViewModel(host,autoRefresh:false);
        var share=Path.Combine(_root,"share.json");panel.ExportFlowToFile(id,share);
        using (var shared=JsonDocument.Parse(File.ReadAllBytes(share)))
        {
            Assert.False(shared.RootElement.TryGetProperty(WorkflowMigrationConsumer.TransactionField,out _));
            Assert.Equal("active",shared.RootElement.GetProperty("activation").GetProperty("status").GetString());
        }
        Assert.True(_store.LoadSnapshot(id).Document.ExtensionData!.ContainsKey(WorkflowMigrationConsumer.TransactionField));
        var rollback=await host.RollbackMigrationAsync(id);
        Assert.True(rollback.Ok,rollback.Message);Assert.Equal(before,File.ReadAllBytes(file));
        Assert.Equal("candidate-ready",_store.LoadSnapshot(id).Document.Activation!.Status);
    }

    private async Task<string> WriteInterruptedAsync(string id, bool activated, bool foreignTransaction = false)
    {
        var prepared = await WorkflowMigrationConsumer.PrepareAsync(_store, id, new Transport());
        var transactionId = "interrupted-" + Guid.NewGuid().ToString("N");
        prepared.Document.ExtensionData![WorkflowMigrationConsumer.TransactionField] =
            JsonSerializer.SerializeToElement(foreignTransaction ? "foreign-transaction" : transactionId);
        var relative = id + ".flow.json";
        using var tx = _store.OpenMigration(id);
        Assert.True(tx.BeginTransaction(transactionId).Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new() { Path = relative, Kind = ChangeKind.Modified }]).Success);
        Assert.True(tx.ApplyReferenceUpdate(new([new(relative, ChangeKind.Modified,
            NewContent: JsonSerializer.Serialize(prepared.Document), ExpectedContentHash: prepared.Revision)])).Success);
        if (activated)
            Assert.True(tx.ActivateCandidate(new(relative, "candidate-ready", "active",
                tx.LoadValidated()!.ReferenceWriteSet[relative])).Success);
        Assert.NotEqual(MigrationStage.Committed, tx.LoadValidated()!.Stage);
        return transactionId;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalHostRollbackRecoversUncommittedNativeWriteAndPreservesExecutionQuarantine(bool activated)
    {
        var id = Seed(); var file = _store.List().Single().FilePath; var original = File.ReadAllBytes(file);
        var transactionId = await WriteInterruptedAsync(id, activated);
        Assert.Throws<WorkflowQuarantinedException>(() => _store.LoadSnapshot(id));
        var host = new TaskCenterHost(Path.Combine(_root,"flows"),Path.Combine(_root,"runs"),
            Path.Combine(_root,"cache.json"),()=>null,()=>true,()=>null);
        host.ResourceEditorTransportForTest = new Transport();
        if (activated)
        {
            var panel = new MultiplayerHoeingAssistant.ViewModels.TaskCenterPanelViewModel(host, autoRefresh: false);
            panel.RollbackMigrationCommand.Execute(panel.Flows.Single(f => f.WorkflowId == id));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (panel.StatusMessage?.StartsWith("迁移已回退", StringComparison.Ordinal) != true && clock.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(10);
            Assert.False(panel.StatusIsError, panel.StatusMessage);
            Assert.StartsWith("迁移已回退", panel.StatusMessage);
        }
        else
        {
            var result = await host.RollbackMigrationAsync(id);
            Assert.True(result.Ok, result.Message);
        }
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.Equal("candidate-ready", _store.LoadSnapshot(id).Document.Activation!.Status);
        var repeated = await host.RollbackMigrationAsync(id);
        Assert.True(repeated.Ok, repeated.Message); Assert.Equal(original, File.ReadAllBytes(file));
        using var tx = _store.OpenMigration(id);
        Assert.Equal(transactionId, tx.LoadValidated()!.TransactionId);
        Assert.Equal(MigrationStage.RolledBack, tx.LoadValidated()!.Stage);
    }

    [Fact]
    public async Task RecoveryWithForeignNativeTransactionIsRejectedWithoutAnyRewrite()
    {
        var id = Seed(); await WriteInterruptedAsync(id, activated: false, foreignTransaction: true);
        var file = _store.List().Single().FilePath; var bytes = File.ReadAllBytes(file);
        var host = new TaskCenterHost(Path.Combine(_root,"flows"),Path.Combine(_root,"runs"),
            Path.Combine(_root,"cache.json"),()=>null,()=>true,()=>null);
        host.ResourceEditorTransportForTest = new Transport();
        var result = await host.RollbackMigrationAsync(id);
        Assert.False(result.Ok); Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Throws<WorkflowQuarantinedException>(() => _store.LoadSnapshot(id));
    }

    [Fact]
    public async Task RecoveryDoesNotOverwriteLaterEditToUncommittedFlow()
    {
        var id = Seed(); await WriteInterruptedAsync(id, activated: true);
        var file = _store.List().Single().FilePath;
        var edit = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(file))!;
        edit["name"] = "keep-later-edit"; File.WriteAllText(file, edit.ToJsonString());
        var bytes = File.ReadAllBytes(file);
        var host = new TaskCenterHost(Path.Combine(_root,"flows"),Path.Combine(_root,"runs"),
            Path.Combine(_root,"cache.json"),()=>null,()=>true,()=>null);
        host.ResourceEditorTransportForTest = new Transport();
        var result = await host.RollbackMigrationAsync(id);
        Assert.False(result.Ok); Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task ResourceMismatchRejectsBeforeAnyTransactionAndPreservesSource()
    {
        var id=Seed();var before=File.ReadAllBytes(_store.List().Single().FilePath);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>WorkflowMigrationConsumer.PrepareAsync(_store,id,new Transport {Revision="other"}));
        Assert.Equal(before,File.ReadAllBytes(_store.List().Single().FilePath));Assert.False(Directory.Exists(_store.MigrationRootFor(id)));
    }

    [Fact]
    public async Task CandidateChangedAfterPrepareIsNotOverwritten()
    {
        var id=Seed();var prepared=await WorkflowMigrationConsumer.PrepareAsync(_store,id,new Transport());
        var changed=_store.LoadSnapshot(id);changed.Document.Name="保留新编辑";_store.Save(changed.Document,changed.Revision);
        var before=File.ReadAllBytes(_store.List().Single().FilePath);
        Assert.Throws<WorkflowRevisionConflictException>(()=>WorkflowMigrationConsumer.Activate(_store,id,prepared));
        Assert.Equal(before,File.ReadAllBytes(_store.List().Single().FilePath));
    }

    [Fact]
    public void ActiveLabelWithoutValidTransactionCannotBeLoadedAsExecutable()
    {
        var id=Seed();var doc=_store.LoadSnapshot(id);doc.Document.Activation!.Status="active";
        doc.Document.ExtensionData![WorkflowMigrationConsumer.TransactionField]=JsonSerializer.SerializeToElement("uncommitted");
        _store.Save(doc.Document,doc.Revision);
        Assert.Throws<WorkflowQuarantinedException>(()=>_store.LoadSnapshot(id));
    }

    [Fact]
    public void ReplacementPortRejectsWrongInputVersionAndConfirmsExactBytes()
    {
        var input=System.Text.Encoding.UTF8.GetBytes("{\"old\":true}");var effects=new WorkflowFileMigrationEffectService();
        var target=new MigrationReferenceWriteTarget("wf-a.flow.json",ChangeKind.Modified,NewContent:"{\"new\":true}",ExpectedContentHash:"wrong");
        Assert.False(effects.TryPrepareReference(target,input,out _,out _));
        target=target with {ExpectedContentHash=WorkflowFileMigrationEffectService.Sha256Hex(input)};
        Assert.True(effects.TryPrepareReference(target,input,out var output,out _));
        Directory.CreateDirectory(_root);File.WriteAllBytes(Path.Combine(_root,target.Path),output);
        Assert.True(effects.TryReadReferenceState(_root,target,out _));
        File.WriteAllBytes(Path.Combine(_root,target.Path),input);Assert.False(effects.TryReadReferenceState(_root,target,out _));
    }

    private sealed class Transport : IResourceCatalogTransport
    {
        public string Revision="AABB";public bool IsReady=>true;public bool HasCapability(string name)=>true;
        public Task<string?> SendAsync(string operation,object payload,CancellationToken ct)
            =>Task.FromResult<string?>(JsonSerializer.Serialize(new {configRevision=Revision,tasks=Array.Empty<object>()}));
    }
}
