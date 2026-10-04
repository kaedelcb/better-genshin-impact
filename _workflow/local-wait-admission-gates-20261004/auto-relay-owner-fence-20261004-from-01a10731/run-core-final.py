import hashlib, json, pathlib, subprocess, xml.etree.ElementTree as ET

root = pathlib.Path.cwd()
base = root / '_workflow/local-wait-admission-gates-20261004/auto-relay-owner-fence-20261004-from-01a10731'
out = base / 'core-owner-fence-final-r2'
out.mkdir(exist_ok=False)
recipe = json.loads((root / '_workflow/local-wait-admission-gates-20261004/auto-relay-terminal-candidate-20261004-from-01a10716/four-contract-current-recipe.json').read_text(encoding='utf-8-sig'))
paths = list(recipe['input_files'])
paths += ['MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LeaseOwnerCapability.cs',
          'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/AdmissionOwnerCapabilityTests.cs',
          'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/RunStoreProcessFenceTests.cs']
paths = sorted(set(paths))
def observe():
    rows = []
    for path in paths:
        data = (root / path).read_bytes()
        rows.append(dict(path=path, sha256=hashlib.sha256(data).hexdigest(), bytes=len(data),
                         lines=len(data.splitlines()), bom=data[:3].hex(), crlf=b'\r\n' in data))
    return rows
before = observe()
(out / 'source-before.json').write_text(json.dumps(before, indent=2), encoding='utf-8')
dotnet = 'C:/Program Files/dotnet/dotnet.exe'
build = [dotnet, 'build', str(root / 'Test/R56ControlledWriterProbe/R56ControlledWriterProbe.csproj'),
         '--no-incremental', '--disable-build-servers', '-nodeReuse:false', '-p:UseSharedCompilation=false',
         '-p:DeployToBgiTools=false', '-p:RestoreLockedMode=true', '-t:Rebuild', '-maxcpucount:1', '-o', str(out / 'products')]
with (out / 'build.log').open('wb') as log:
    build_exit = subprocess.run(build, stdout=log, stderr=subprocess.STDOUT).returncode
assert build_exit == 0
run = [dotnet, 'vstest', str(out / 'products/MultiplayerHoeingAssistant.UnitTest.dll'),
       '--TestCaseFilter:FullyQualifiedName~ServiceTests.TaskCenter|FullyQualifiedName~StartupFlowSchemeStoreTests',
       '--logger:trx;LogFileName=final.trx', '--ResultsDirectory:' + str(out)]
with (out / 'run.log').open('wb') as log:
    run_exit = subprocess.run(run, stdout=log, stderr=subprocess.STDOUT).returncode
after = observe()
(out / 'source-after.json').write_text(json.dumps(after, indent=2), encoding='utf-8')
assert before == after, 'declared source inputs changed during execution'
results = ET.parse(out / 'final.trx').getroot().find('{*}Results')
counts = {}
for row in results:
    counts[row.attrib['outcome']] = counts.get(row.attrib['outcome'], 0) + 1
products = []
for p in sorted((out / 'products').rglob('*')):
    if p.is_file():
        data = p.read_bytes()
        products.append(dict(path=p.relative_to(out).as_posix(), bytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
(out / 'product-observation.json').write_text(json.dumps(products, indent=2), encoding='utf-8')
(base / 'core-final-observation.json').write_text(json.dumps(dict(
    kind='ordinary fresh declared-input/product regression; not authenticated execution receipt or complete dependency closure',
    build_argv=build, build_exit=build_exit, run_argv=run, run_exit=run_exit, counts=counts,
    declared_inputs=len(before), inputs_equal=before == after, products=len(products),
    source_before=str((out / 'source-before.json').relative_to(root)),
    source_after=str((out / 'source-after.json').relative_to(root)),
    trx=str((out / 'final.trx').relative_to(root))), indent=2), encoding='utf-8')
print(json.dumps(dict(build_exit=build_exit, run_exit=run_exit, counts=counts, declared_inputs=len(before))))
assert run_exit == 0 and counts.get('Failed', 0) == 0
