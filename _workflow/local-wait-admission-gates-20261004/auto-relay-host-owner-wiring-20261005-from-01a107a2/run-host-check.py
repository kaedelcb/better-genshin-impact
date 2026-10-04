import sys, json, hashlib, subprocess, time
from pathlib import Path

root = Path.cwd()
base = root / '_workflow/local-wait-admission-gates-20261004/auto-relay-host-owner-wiring-20261005-from-01a107a2'
out = base / sys.argv[1]
out.mkdir(exist_ok=False)
previous = root / '_workflow/local-wait-admission-gates-20261004/auto-relay-owner-fence-20261004-from-01a10731/core-owner-fence-final-r2/source-before.json'
paths = [r['path'] for r in json.loads(previous.read_text(encoding='utf-8-sig'))]
def snapshot():
    rows = []
    for relative in paths:
        b = (root / relative).read_bytes()
        rows.append(dict(path=relative, sha256=hashlib.sha256(b).hexdigest(), bytes=len(b), lines=len(b.splitlines()), bom=b[:3].hex(), crlf=b'\r\n' in b))
    return rows
def save(name, value):
    (out / name).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
def run(name, argv):
    with (out / (name + '.log')).open('w', encoding='utf-8') as log:
        p = subprocess.Popen(argv, cwd=root, stdout=log, stderr=subprocess.STDOUT)
        observation = dict(argv=argv, pid=p.pid, started=time.time(), level='ordinary process/input/product observation, not authenticated receipt or complete dependency closure')
        save(name + '-process.json', observation)
        code = p.wait()
        observation.update(exit_code=code, ended=time.time())
        save(name + '-process.json', observation)
        print(name, code, flush=True)
        return code
before = snapshot()
save('source-before.json', before)
dotnet = 'C:/Program Files/dotnet/dotnet.exe'
argv = [dotnet, 'build', str(root / 'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'), '--no-incremental', '--disable-build-servers', '-nodeReuse:false', '-p:UseSharedCompilation=false', '-p:DeployToBgiTools=false', '-p:RestoreLockedMode=true', '-t:Rebuild', '-maxcpucount:1', '-o', str(out / 'products')]
code = 0
if len(sys.argv) == 2:
    assistant = argv.copy()
    assistant[2] = str(root / 'MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj')
    code = run('assistant-build', assistant)
if code == 0:
    code = run('build', argv)
if code == 0 and len(sys.argv) == 2:
    probe = argv.copy()
    probe[2] = str(root / 'Test/R56ControlledWriterProbe/R56ControlledWriterProbe.csproj')
    code = run('probe-build', probe)
if code == 0:
    filter = sys.argv[2] if len(sys.argv) > 2 else 'FullyQualifiedName~ServiceTests.TaskCenter|FullyQualifiedName~StartupFlowSchemeStoreTests'
    code = run('test', [dotnet, 'vstest', str(out / 'products/MultiplayerHoeingAssistant.UnitTest.dll'), '--TestCaseFilter:' + filter, '--logger:trx;LogFileName=results.trx', '--ResultsDirectory:' + str(out)])
after = snapshot()
save('source-after.json', after)
save('input-comparison.json', dict(equal=before == after, declared_inputs=len(paths), differences=[r['path'] for r, a in zip(before, after) if r != a]))
if (out / 'products').exists():
    save('product-observation.json', [dict(path=str(p.relative_to(out)),bytes=p.stat().st_size,sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in sorted((out/'products').rglob('*')) if p.is_file()])
if before != after: raise RuntimeError('declared input drift')
raise SystemExit(code)
