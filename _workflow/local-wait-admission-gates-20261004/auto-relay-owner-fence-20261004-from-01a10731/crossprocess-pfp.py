import hashlib, json, os, pathlib, subprocess

root = pathlib.Path.cwd()
base = root / '_workflow/local-wait-admission-gates-20261004/auto-relay-owner-fence-20261004-from-01a10731'
source = root / 'MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs'
original = source.read_bytes()
needle = b'        using var publicationLock = AcquirePublicationLock();\r\n'
assert original.count(needle) == 1
dotnet = 'C:/Program Files/dotnet/dotnet.exe'
observations = []
def execute(leg):
    out = base / ('crossprocess-pfp-' + leg)
    out.mkdir(exist_ok=False)
    build = [dotnet, 'build', str(root / 'Test/R56ControlledWriterProbe/R56ControlledWriterProbe.csproj'),
             '--no-incremental', '--disable-build-servers', '-nodeReuse:false', '-p:UseSharedCompilation=false',
             '-p:DeployToBgiTools=false', '-p:RestoreLockedMode=true', '-t:Rebuild', '-maxcpucount:1', '-o', str(out / 'products')]
    with (out / 'build.log').open('wb') as log:
        code = subprocess.run(build, stdout=log, stderr=subprocess.STDOUT).returncode
    assert code == 0, (leg, code)
    run = [dotnet, 'vstest', str(out / 'products/MultiplayerHoeingAssistant.UnitTest.dll'),
           '--TestCaseFilter:FullyQualifiedName~RunStoreProcessFenceTests', '--logger:trx;LogFileName=result.trx', '--ResultsDirectory:' + str(out)]
    with (out / 'run.log').open('wb') as log:
        code = subprocess.run(run, stdout=log, stderr=subprocess.STDOUT).returncode
    observations.append(dict(leg=leg, build_argv=build, run_argv=run, run_exit=code,
                             source_sha256=hashlib.sha256(source.read_bytes()).hexdigest()))
    assert code == (1 if leg == 'negative' else 0), (leg, code)
    if leg == 'negative':
        assert b'Second same-revision process overwrote the committed original writer.' in (out / 'result.trx').read_bytes()

def restore():
    temporary = source.with_name(source.name + '.owner-fence-restore.tmp')
    temporary.write_bytes(original)
    os.replace(temporary, source)
try:
    execute('baseline')
    source.write_bytes(original.replace(needle, b''))
    execute('negative')
finally:
    restore()
    assert source.read_bytes() == original
    (base / 'crossprocess-pfp-observation.json').write_text(json.dumps(dict(
        kind='ordinary causal P/F/P; not authenticated mutation receipt', observations=observations,
        restored_sha256=hashlib.sha256(source.read_bytes()).hexdigest()), indent=2), encoding='utf-8')
execute('restored')
(base / 'crossprocess-pfp-observation.json').write_text(json.dumps(dict(
    kind='ordinary causal P/F/P; not authenticated mutation receipt', observations=observations,
    restored_sha256=hashlib.sha256(source.read_bytes()).hexdigest()), indent=2), encoding='utf-8')
