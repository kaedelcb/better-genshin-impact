from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/RunStoreTests.cs'); b=p.read_bytes(); s=b.decode('utf-8').replace('\r\n','\n')
old='''        loaded = store.Load(run.RunId)!;
        var next = new WorkflowSubmission { Key = "fabricated-key", NodeId = "next", NodeAdmissionRequired = false };
        loaded.CurrentSubmission = next;
        Assert.Throws<RunRecordConflictException>(() => store.Update(loaded));
        Assert.Equal(before, File.ReadAllBytes(path));
'''
assert s.count(old)==1; s=s.replace(old,'')
needle='    public void Dispose()'
test='''    [Fact]
    public void OriginalNodeRouting_OrdinaryWriterCannotManufactureFirstRouting()
    {
        var store = new RunStore(_dir);
        var run = store.CreateRun("routing-flow", "revision");
        var path = Path.Combine(_dir, run.RunId + ".run.json");
        var before = File.ReadAllBytes(path);
        run.CurrentSubmission = new WorkflowSubmission { Key = "fabricated-key", NodeId = "next",
            Intent = SubmitIntentState.IntentRecorded, NodeAdmissionRequired = false };
        Assert.Throws<RunRecordConflictException>(() => store.Update(run));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

'''
assert s.count(needle)==1; s=s.replace(needle,test+needle); p.write_bytes(s.replace('\n','\r\n'if b'\r\n'in b else '\n').encode('utf-8'))
p=Path('_workflow/local-wait-admission-gates-20261004/auto-relay-stop-contract-source-20261005-from-01a1086d/run-routing-pfp.py'); s=p.read_text(); s=s.replace("kind=sys.argv[1]","kind=sys.argv[1]\nsuffix='-r2'"); s=s.replace("name='historical-routing-'+kind+'-'+leg","name='historical-routing-'+kind+suffix+'-'+leg"); s=s.replace("'routing-pfp-'+kind+'.json'","'routing-pfp-'+kind+suffix+'.json'"); s=s.replace("'routing-restore-'+kind+'.json'","'routing-restore-'+kind+suffix+'.json'"); p.write_text(s,encoding='utf-8')
print('separated manufacture oracle from unrelated history guard')
