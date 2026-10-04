import hashlib, json, os, pathlib, subprocess, xml.etree.ElementTree as ET

root = pathlib.Path.cwd()
base = root / '_workflow/local-wait-admission-gates-20261004/auto-relay-owner-fence-20261004-from-01a10731'
dotnet = 'C:/Program Files/dotnet/dotnet.exe'
mutations = [
    ('responsibility-capture', root / 'MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs',
     b'private LeaseOwnerCapability? ResponsibleOwner => _ownerResponsibility.Value?.Ownership;',
     b'private LeaseOwnerCapability? ResponsibleOwner => Volatile.Read(ref _ownership);',
     'SameFacade_ReacquisitionDoesNotUpgradeTerminalAlreadyWaiting', 'Expected: Error'),
    ('publication-owner-fence', root / 'MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationLeaseStore.cs',
     b'            || !owner.Matches(lease.LeaseId, lease.OwnerEpoch) || IsOwnerExpired(lease))',
     b'            )',
     'TwoActualProcesses_ReleasedOriginalOwnerCannotPublishFreshRevisionAfterSuccessor',
     'Released original process published over successor using a fresh revision.'),
]
records = json.loads((base / "owner-fence-pfp-first-attempt-observation.json").read_text())["records"]
def execute(name, leg, source, target, assertion):
    out = base / ('owner-pfp-r2-' + name + '-' + leg)
    out.mkdir(exist_ok=False)
    build = [dotnet, 'build', str(root / 'Test/R56ControlledWriterProbe/R56ControlledWriterProbe.csproj'),
             '--no-incremental', '--disable-build-servers', '-nodeReuse:false', '-p:UseSharedCompilation=false',
             '-p:DeployToBgiTools=false', '-p:RestoreLockedMode=true', '-t:Rebuild', '-maxcpucount:1', '-o', str(out / 'products')]
    with (out / 'build.log').open('wb') as log:
        build_exit = subprocess.run(build, stdout=log, stderr=subprocess.STDOUT).returncode
    assert build_exit == 0, (name, leg, build_exit)
    run = [dotnet, 'vstest', str(out / 'products/MultiplayerHoeingAssistant.UnitTest.dll'),
           '--TestCaseFilter:FullyQualifiedName~' + target, '--logger:trx;LogFileName=result.trx', '--ResultsDirectory:' + str(out)]
    with (out / 'run.log').open('wb') as log:
        run_exit = subprocess.run(run, stdout=log, stderr=subprocess.STDOUT).returncode
    results = ET.parse(out / 'result.trx').getroot().find('{*}Results')
    rows = [r for r in results if target in r.attrib.get('testName', '')]
    assert len(rows) == 1
    expected = 'Failed' if leg == 'negative' else 'Passed'
    assert rows[0].attrib['outcome'] == expected, (name, leg, rows[0].attrib)
    if leg == 'negative':
        message = rows[0].find('{*}Output/{*}ErrorInfo/{*}Message').text
        assert assertion in message, message
    else:
        assert run_exit == 0
    records.append(dict(mutation=name, leg=leg, source=str(source.relative_to(root)),
        source_sha256=hashlib.sha256(source.read_bytes()).hexdigest(), build_argv=build,
        build_exit=build_exit, run_argv=run, run_exit=run_exit, test_id=rows[0].attrib['testId'],
        outcome=rows[0].attrib['outcome'], expected_assertion=assertion, trx=str((out / 'result.trx').relative_to(root))))

for name, source, needle, replacement, target, assertion in mutations:
    if name != "publication-owner-fence":
        continue
    original = source.read_bytes()
    assert hashlib.sha256(original).hexdigest() == "eb35f99f1baaaff6a79d1d4cee60d5ec1112919b384a20435e700e30947b47fd"
    assert original.count(needle) == 1, name
    try:
        temporary = source.with_name(source.name + '.owner-fence-mutant.tmp')
        temporary.write_bytes(original.replace(needle, replacement))
        os.replace(temporary, source)
        execute(name, 'negative', source, target, assertion)
    finally:
        temporary = source.with_name(source.name + '.owner-fence-restore.tmp')
        temporary.write_bytes(original)
        os.replace(temporary, source)
        assert source.read_bytes() == original
        (base / 'owner-fence-pfp-observation.json').write_text(json.dumps(dict(
            kind='ordinary causal P/F/P; not authenticated mutation receipt', records=records,
            restored_source=str(source.relative_to(root)), restored_sha256=hashlib.sha256(original).hexdigest()), indent=2), encoding='utf-8')
    execute(name, 'restored', source, target, assertion)
    (base / 'owner-fence-pfp-observation.json').write_text(json.dumps(dict(
        kind='ordinary causal P/F/P; not authenticated mutation receipt', records=records,
        restored_source=str(source.relative_to(root)), restored_sha256=hashlib.sha256(original).hexdigest()), indent=2), encoding='utf-8')
