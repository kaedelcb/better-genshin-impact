from pathlib import Path
import hashlib,json
root=Path.cwd();base=Path(__file__).resolve().parent
path=root/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowMigrationConsumerTests.cs'
before=json.loads((base/'recovery-fix/source-before.json').read_text(encoding='utf-8'))
data=path.read_bytes();assert hashlib.sha256(data).hexdigest()==before[path.relative_to(root).as_posix()]['sha256']
nl=b'\r\n' if b'\r\n' in data else b'\n'
addition='''    private async Task<string> WriteInterruptedAsync(string id, bool activated, bool foreignTransaction = false)
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
        var result = await host.RollbackMigrationAsync(id);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.Equal("candidate-ready", _store.LoadSnapshot(id).Document.Activation!.Status);
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

'''.replace('\n',nl.decode()).encode('utf-8')
needle=b'    [Fact]'+nl+b'    public async Task ResourceMismatchRejectsBeforeAnyTransactionAndPreservesSource()'
assert data.count(needle)==1;new=data.replace(needle,addition+needle);path.write_bytes(new)
print(json.dumps(dict(before_bytes=len(data),after_bytes=len(new),crlf=new.count(b'\r\n'),sha256=hashlib.sha256(new).hexdigest())))
