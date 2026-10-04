from pathlib import Path
import subprocess, hashlib, json, os, xml.etree.ElementTree as ET

root = Path.cwd()
out = Path(__file__).parent
source = root / 'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationConsistency.cs'
original = source.read_bytes()
original_sha = hashlib.sha256(original).hexdigest()
old = b'ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or'
new = b'ex is IOException or UnauthorizedAccessException or InvalidOperationException or'
assert original.count(old) == 1
products = root / '_workflow/local-wait-admission-gates-20261004/g10-completion/products'
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}

def run(name, args):
    with (out / (name + '.log')).open('wb') as log:
        code = subprocess.run(args, stdout=log, stderr=subprocess.STDOUT, timeout=600).returncode
    (out / (name + '-exit.txt')).write_text(str(code), encoding='utf-8')
    print(name, code, flush=True)
    return code

def build(name):
    return run(name, ['dotnet', 'build', 'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj',
        '-t:Rebuild', '-p:DeployToBgiTools=false', '-o', str(products), '--nologo'])

def test(name):
    return run(name, ['dotnet', 'vstest', str(products / 'MultiplayerHoeingAssistant.UnitTest.dll'),
        '/TestCaseFilter:FullyQualifiedName~R56RootFactsRefusalTests|FullyQualifiedName~PhysicalConfigRoot_SecondArtifactRootCannotBypassExclusiveAuthority',
        '/Logger:trx;LogFileName=' + name + '.trx', '/ResultsDirectory:' + str(out)])

def atomic(data):
    tmp = source.with_name(source.name + '.rootfacts-restore-tmp')
    tmp.write_bytes(data)
    os.replace(tmp, source)

observation = {'kind': 'ordinary P/F/P, not authenticated receipt or independent acceptance',
    'source': str(source.relative_to(root)), 'original_sha256': original_sha,
    'patch': 'remove InvalidDataException from root-facts observation filter'}
try:
    assert run('root-facts-pfp-baseline-build', ['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj',
        '-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo']) == 0
    assert test('root-facts-pfp-baseline') == 0
    atomic(original.replace(old, new))
    observation['mutant_sha256'] = hashlib.sha256(source.read_bytes()).hexdigest()
    assert build('root-facts-pfp-mutant-build') == 0
    assert test('root-facts-pfp-mutant') != 0
    failures = []
    for row in ET.parse(out / 'root-facts-pfp-mutant.trx').findall('.//t:UnitTestResult', ns):
        if row.get('outcome') == 'Failed':
            failures.append({'test_id': row.get('testId'), 'name': row.get('testName'),
                'message': row.findtext('t:Output/t:ErrorInfo/t:Message', '', ns),
                'stack': row.findtext('t:Output/t:ErrorInfo/t:StackTrace', '', ns)})
    targets = [f for f in failures if 'InvalidObservedRootFacts_' in f['name'] and
        'Assert.Null() Failure' in f['message'] and 'InvalidDataException' in f['message']]
    assert len(targets) == 3
    observation['target_failures'] = targets
finally:
    atomic(original)
    observation['restored_sha256'] = hashlib.sha256(source.read_bytes()).hexdigest()
    assert observation['restored_sha256'] == original_sha
    (out / 'root-facts-mutation-observation.json').write_text(json.dumps(observation, indent=2), encoding='utf-8')

assert build('root-facts-pfp-restored-build') == 0
assert test('root-facts-pfp-restored') == 0
observation['passed_failed_passed'] = True
(out / 'root-facts-mutation-observation.json').write_text(json.dumps(observation, indent=2), encoding='utf-8')
