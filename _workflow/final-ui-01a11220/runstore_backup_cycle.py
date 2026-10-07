"""Source-bound backup-growth red/fix/negative/restored cycle; inherited Goal and budget."""
from pathlib import Path
import hashlib, json, os, subprocess, sys, time, xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[2]
BASE = Path(__file__).resolve().parent / 'runstore-backup'
SOURCE = 'MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs'
TEST = 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/RunStoreTests.cs'
sys.path.insert(0, str(ROOT / 'tools/mistletoe'))
sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
import process_runner as p
sha = lambda data: hashlib.sha256(data).hexdigest()
original_size = s.size
scan_retries = []
generated = [ROOT / 'MultiplayerHoeingAssistant/obj', ROOT / 'Test/MultiplayerHoeingAssistant.UnitTest/obj']
def stable_size(roots):
    for attempt in range(5):
        try: return original_size(roots)
        except FileNotFoundError as error:
            missing = Path(error.filename).absolute()
            if attempt == 4 or not any(missing.is_relative_to(folder) for folder in generated): raise
            scan_retries.append(dict(path=str(missing), attempt=attempt + 1, complete_collection_retried=True))
            time.sleep(.1)
s.size = stable_size
OLD = '                    File.Copy(file, Path.Combine(_backupDir, $"{rec.RunId}.{current?.RecordRevision ?? 0}.run.json"), overwrite: true);'
NEW = '''                    // Two future write-before copies; existing numbered history is untouched.
                    var backup = Path.Combine(_backupDir, $"{rec.RunId}.previous-{(current?.RecordRevision ?? 0) % 2}.run.json");
                    var backupTmp = Path.Combine(_backupDir, $".{rec.RunId}.{Guid.NewGuid():N}.backup.tmp");
                    try
                    {
                        File.Copy(file, backupTmp, overwrite: false);
                        ThrowIfFileFaultInjected("backup-publish");
                        File.Move(backupTmp, backup, overwrite: true);
                    }
                    finally
                    {
                        try { if (File.Exists(backupTmp)) File.Delete(backupTmp); }
                        catch (IOException) { /* Unpublished temporary stays outside the record set. */ }
                        catch (UnauthorizedAccessException) { /* Main failure remains authoritative. */ }
                    }'''
ADDITION = '''    [Fact]
    public void Persist_ManyGrowingUpdates_BoundsFutureCopiesAndKeepsNumberedHistory()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-backup-growth", "revision");
        var file = Path.Combine(_dir, rec.RunId + ".run.json");
        var folder = Path.Combine(_dir, "_backup");
        Directory.CreateDirectory(folder);
        var legacy = Path.Combine(folder, rec.RunId + ".1.run.json");
        var original = File.ReadAllBytes(file);
        File.WriteAllBytes(legacy, original);
        var lastPrior = new Queue<byte[]>();
        for (var index = 0; index < 128; index++)
        {
            lastPrior.Enqueue(File.ReadAllBytes(file));
            if (lastPrior.Count > 2) lastPrior.Dequeue();
            rec.Note = "arrival-" + index + new string('x', index * 128);
            store.Update(rec);
        }
        var future = Directory.GetFiles(folder, rec.RunId + ".*.run.json")
            .Where(path => path != legacy).ToArray();
        Assert.Equal(2, future.Length);
        foreach (var expected in lastPrior)
            Assert.Single(future.Where(path => File.ReadAllBytes(path).SequenceEqual(expected)));
        Assert.Equal(original, File.ReadAllBytes(legacy));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        Assert.Equal(rec.RecordRevision, store.Load(rec.RunId)!.RecordRevision);
        Assert.Equal(rec.Note, store.Load(rec.RunId)!.Note);
    }

    [Fact]
    public void Persist_BackupPublishFailure_KeepsPrimaryAndPreviousCopies()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-backup-failure", "revision");
        for (var index = 0; index < 3; index++) { rec.Note = "published-" + index; store.Update(rec); }
        var file = Path.Combine(_dir, rec.RunId + ".run.json");
        var folder = Path.Combine(_dir, "_backup");
        var primary = File.ReadAllBytes(file);
        var backups = Directory.GetFiles(folder, "*.run.json").ToDictionary(path => path, File.ReadAllBytes);
        var revision = rec.RecordRevision;
        var hits = 0;
        store.FileFaultForTest = tag => tag == "backup-publish" ? (++hits > 0 ? new IOException("backup publication denied") : null) : null;
        rec.Note = "must not publish";
        Assert.Throws<IOException>(() => store.Update(rec));
        Assert.True(hits > 1);
        Assert.Equal(revision, rec.RecordRevision);
        Assert.Equal(primary, File.ReadAllBytes(file));
        foreach (var pair in backups) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

'''
def atomic(path, data):
    temp = path.with_name(path.name + '.own-01a114ee.tmp')
    assert not temp.exists()
    with temp.open('xb') as stream: stream.write(data); stream.flush(); os.fsync(stream.fileno())
    os.replace(temp, path)
    assert path.read_bytes() == data and not temp.exists()
def replace(relative, old, new):
    path = ROOT / relative; data = path.read_bytes(); ending = '\r\n' if b'\r\n' in data else '\n'
    needle = old.replace('\n', ending).encode(); addition = new.replace('\n', ending).encode()
    assert data.count(needle) == 1, relative
    atomic(path, data.replace(needle, addition))
with s.Session(ROOT, 'own-root-01a114ee-C08-backup-amplification-causal') as budget:
    roots = budget.old_roots[:]; budget.old_roots = list(dict.fromkeys(roots)); assert set(roots) == set(budget.old_roots)
    assert not BASE.exists(); budget.track(BASE)
    carrier = ROOT / '_workflow/runtime-unified-01a10cef/single-tests/assistant'; budget.track(carrier)
    for folder in generated: budget.track(folder)
    def write(path, value): s.write(path, json.dumps(value, ensure_ascii=False, indent=2).encode('utf-8'))
    before = {relative: (ROOT / relative).read_bytes() for relative in [SOURCE, TEST]}
    for relative, data in before.items(): s.write(BASE / 'opening/before' / relative, data)
    write(BASE / 'opening/identity.json', {relative: dict(sha256=sha(data), bytes=len(data), lines=len(data.splitlines()), bom=data.startswith(b'\xef\xbb\xbf'), crlf=data.count(b'\r\n')) for relative, data in before.items()})
    s.write(BASE / 'opening/git-status.txt', subprocess.check_output(['git', '-c', 'core.longpaths=true', 'status', '--porcelain=v1'], cwd=ROOT))
    write(BASE / 'opening/admission.json', dict(function='C08 immediate loop and durable stop/restart', finding='OWN-ROOT-C08-BACKUP-GROWTH-IMPORTANT-1', original_grade='important', evidence='../own-runtime/twelfth-viewport-control/failure-terminal-recovery.json', change='Two atomic future write-before copies; every existing numbered archive remains', matrix=['first and repeated publication retain exact last two source records', 'pre-existing numbered archive remains byte-identical', 'backup publication failure leaves primary and both copies unchanged', 'cold interrupted loop recovery and explicit UI stop after refreshed modules'], review_requests_new=0, review_budget_remaining=0, independent_review=False, product_complete=False))
    def stage(name, targeted):
        out = BASE / name; out.mkdir(parents=True, exist_ok=False)
        hashes = {file.relative_to(ROOT).as_posix(): sha(file.read_bytes()) for folder in ['MultiplayerHoeingAssistant', 'Test/MultiplayerHoeingAssistant.UnitTest'] for file in (ROOT / folder).rglob('*') if file.suffix in ['.cs', '.xaml', '.csproj'] and '_wpftmp' not in file.name and not any(part in file.parts for part in ['bin', 'obj'])}
        write(out / 'source-hashes.json', hashes)
        env = os.environ.copy(); env.update(NEXUSBGI_DATA_ROOT=str(out / 'own-data'), TEMP=str(out / 'temp'), TMP=str(out / 'temp'))
        Path(env['TEMP']).mkdir()
        build = ['C:/Program Files/dotnet/dotnet.exe', 'build', str(ROOT / 'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'), '--disable-build-servers', '-nodeReuse:false', '-p:UseSharedCompilation=false', '-p:DeployToBgiTools=false', '-t:Rebuild', '-maxcpucount:1', '-o', str(carrier)]
        result = dict(stage=name)
        result['build'], _, _ = p.run(build, cwd=ROOT, env=env, directory=out, recovery_directory=out, phase='build', timeout=1200)
        if result['build'] == 0:
            filt = 'FullyQualifiedName~Persist_ManyGrowingUpdates|FullyQualifiedName~Persist_BackupPublishFailure' if targeted else 'FullyQualifiedName~RunStore|FullyQualifiedName~WorkflowRunnerTests|FullyQualifiedName~FormalPathTests|FullyQualifiedName~TaskCenterHostTests|FullyQualifiedName~TaskCenterPanelViewModelTests'
            test = ['C:/Program Files/dotnet/dotnet.exe', 'vstest', str(carrier / 'MultiplayerHoeingAssistant.UnitTest.dll'), '--TestCaseFilter:' + filt, '--logger:trx;LogFileName=backup.trx', '--ResultsDirectory:' + str(out)]
            result['test'], _, _ = p.run(test, cwd=ROOT, env=env, directory=out, recovery_directory=out, phase='test', timeout=600)
            rows = ET.parse(out / 'backup.trx').findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult')
            for outcome in ['Passed', 'Failed', 'NotExecuted']: result[outcome] = [row.get('testName') for row in rows if row.get('outcome') == outcome]
        result['source_drift'] = [relative for relative, digest in hashes.items() if not (ROOT / relative).is_file() or sha((ROOT / relative).read_bytes()) != digest]
        write(out / 'result.json', result)
        print(json.dumps(dict(stage=name, build=result['build'], passed=len(result.get('Passed', [])), failed=result.get('Failed'), drift=result['source_drift']), ensure_ascii=False), flush=True)
        assert result['build'] == 0 and not result['source_drift'], result
        return result
    replace(TEST, '    [Fact]\n    public void Persist_AtomicWrite_BackupKeepsPriorRecordRevision()', ADDITION + '    [Fact]\n    public void Persist_AtomicWrite_BackupKeepsPriorRecordRevision()')
    red = stage('red', True); assert len(red['Failed']) == 2 and not red['Passed'], red
    replace(SOURCE, OLD, NEW)
    replace(TEST, 'var backupPath = Path.Combine(_dir, "_backup", $"{rec.RunId}.{revisionBeforeBackup}.run.json");', '''var backupPath = Assert.Single(Directory.EnumerateFiles(Path.Combine(_dir, "_backup"), rec.RunId + ".*.run.json")
            .Where(path => System.Text.Json.JsonSerializer.Deserialize<WorkflowRunRecord>(File.ReadAllText(path))!.RecordRevision == revisionBeforeBackup));''')
    replace(TEST, '// 改为断言「该修订号的备份文件存在」**且其内容＝发布前盘上修订**（修订号单调不回退 ⇒ 该名由本步产生）。', '// 按内容修订定位最近写前备份；内容必须等于发布前盘上记录，不依赖旧数字文件名。')
    replace(SOURCE, '`create-backup-dir`／`cleanup-tmp`', '`create-backup-dir`／`backup-publish`／`cleanup-tmp`')
    fixed = {relative: (ROOT / relative).read_bytes() for relative in [SOURCE, TEST]}
    green = stage('green', False)
    assert all(any(token in name for name in green['Passed']) for token in ['Persist_ManyGrowingUpdates', 'Persist_BackupPublishFailure']), green
    try:
        atomic(ROOT / SOURCE, before[SOURCE])
        negative = stage('negative', True)
        assert len(negative['Failed']) == 2 and not negative['Passed'], negative
    finally:
        for relative, data in fixed.items(): atomic(ROOT / relative, data)
        write(BASE / 'recovery.json', dict(source_restored=all((ROOT / relative).read_bytes() == data for relative, data in fixed.items()), jobs_pending=False, source_hashes={relative: sha(data) for relative, data in fixed.items()}))
    restored = stage('restored', False)
    assert set(green['Failed']) == set(restored['Failed']) and set(green['NotExecuted']) == set(restored['NotExecuted'])
    write(BASE / 'result.json', dict(phases=[red, green, negative, restored], source_restored=True, scan_retries=scan_retries, independent_review=False, runtime_acceptance=False, product_complete=False))
