from pathlib import Path
import subprocess, hashlib, json, os, xml.etree.ElementTree as ET

r = Path.cwd().resolve()
d = Path(__file__).resolve().parent
p = r / 'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs'
original = p.read_bytes()
original_sha = hashlib.sha256(original).hexdigest()
dotnet = 'C:/Program Files/dotnet/dotnet.exe'
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}

def command(out, name, args):
    with (out / (name + '.log')).open('wb') as log:
        result = subprocess.run(args, cwd=r, stdout=log, stderr=subprocess.STDOUT, timeout=600).returncode
    (out / (name + '-exit.txt')).write_text(str(result), encoding='utf-8')
    print(out.name, name, result, flush=True)
    return result

def atomic(data):
    tmp = p.with_name(p.name + '.four-contract-restore-tmp')
    tmp.write_bytes(data)
    os.replace(tmp, p)

def build(out, leg):
    return command(out, leg + '-build', [dotnet, 'build',
        'Test/R56ControlledWriterProbe/R56ControlledWriterProbe.csproj', '-t:Rebuild',
        '-p:DeployToBgiTools=false', '-p:RestoreLockedMode=true', '-p:UseSharedCompilation=false',
        '-nodeReuse:false', '-maxcpucount:1', '--disable-build-servers', '-o', str(out / 'products')])

def test(out, leg, target):
    return command(out, leg, [dotnet, 'vstest', str(out / 'products/MultiplayerHoeingAssistant.UnitTest.dll'),
        '/TestCaseFilter:FullyQualifiedName~' + target,
        '/Logger:trx;LogFileName=' + leg + '.trx', '/ResultsDirectory:' + str(out)])

start = original.index(b'    private bool TryFreezeChanges(')
end = original.index(b'    private bool TryFreezeReferencePlan(', start)
part = original[start:end]
assert part.count(b'frozen = InvokeExternal(() =>') == 1 and part.count(b'            });') == 1
unguarded = part.replace(b'frozen = InvokeExternal(() =>', b'frozen = ((Func<ChangeRecord[]>)(() =>').replace(
    b'            });', b'            }))();')
first_mutant = original[:start] + unguarded + original[end:]
old = b'if (!Monitor.TryEnter(_sync, 10)) continue;'
assert original.count(old) == 1
second_mutant = original.replace(old,
    b'if (!Monitor.TryEnter(_sync, 10)) return new(null, MigrationResult.Fail("mutation_busy", MigrationStage.None));')
cases = [
    ('M1-input-callback-unguarded', first_mutant,
     'CurrentV14_ConcurrencyNoCallbackValidationAndRollback_AreSerialized',
     'input callback entry must refuse without waiting'),
    ('M2-no-callback-early-refusal', second_mutant,
     'InternalNoCallbackMutationAndRollback_AreSerialized',
     'internal mutation must serialize without callback rejection')]
observations = []
for ident, mutant, target, assertion in cases:
    out = d / 'four-contract-mutations' / ident
    out.mkdir(parents=True, exist_ok=False)
    observation = {'id': ident, 'kind': 'ordinary P/F/P, not authenticated receipt or independent closure',
        'source': str(p.relative_to(r)), 'original_sha256': original_sha,
        'mutant_sha256': hashlib.sha256(mutant).hexdigest(), 'target': target, 'assertion': assertion}
    try:
        assert p.read_bytes() == original
        assert build(out, 'baseline') == 0
        assert test(out, 'baseline', target) == 0
        atomic(mutant)
        assert build(out, 'mutant') == 0
        assert test(out, 'mutant', target) != 0
        failures = []
        for row in ET.parse(out / 'mutant.trx').findall('.//t:UnitTestResult', ns):
            if row.get('outcome') == 'Failed':
                failures.append({'test_id': row.get('testId'), 'name': row.get('testName'),
                    'message': row.findtext('t:Output/t:ErrorInfo/t:Message', '', ns)})
        hits = [x for x in failures if target in x['name'] and assertion in x['message']]
        assert len(hits) == 1, failures
        observation['target_failures'] = hits
    finally:
        atomic(original)
        observation['restored_sha256'] = hashlib.sha256(p.read_bytes()).hexdigest()
        assert observation['restored_sha256'] == original_sha
        (out / 'observation.json').write_text(json.dumps(observation, ensure_ascii=False, indent=2), encoding='utf-8')
    assert build(out, 'restored') == 0
    assert test(out, 'restored', target) == 0
    observation['passed_failed_passed'] = True
    (out / 'observation.json').write_text(json.dumps(observation, ensure_ascii=False, indent=2), encoding='utf-8')
    observations.append(observation)
(d / 'four-contract-mutation-observations.json').write_text(json.dumps(observations, ensure_ascii=False, indent=2), encoding='utf-8')
