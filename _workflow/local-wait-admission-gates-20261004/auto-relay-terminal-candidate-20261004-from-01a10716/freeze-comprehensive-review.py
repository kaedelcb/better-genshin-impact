from pathlib import Path
import os, sys, json, hashlib, uuid, subprocess

r = Path.cwd().resolve()
d = Path(__file__).resolve().parent
sys.path.insert(0, str(r / 'tools/mistletoe'))
import native_review as nr

ident = uuid.uuid4().hex
out = Path('E:/CodexReviewSnapshots/terminal-candidate-20261004') / ident
source = out / 'source'
contracts = out / 'contracts'
code_roots = ['BetterGenshinImpact', 'MultiplayerHoeingAssistant', 'BgiCoordinatorServer',
    'BgiCoordinatorServer.Tests', 'AutoHoeingUpdater', 'Fischless.GameCapture',
    'Fischless.HotkeyCapture', 'Fischless.WindowsInput', 'Test', 'Build', 'tools', 'Docs']
extensions = {'.cs', '.xaml', '.csproj', '.props', '.targets', '.sln', '.slnx', '.json',
    '.py', '.ps1', '.js', '.ts', '.html', '.css', '.md', '.xml', '.resx', '.config', '.txt', '.yml', '.yaml'}
files = {}
excluded = []
for name in code_roots:
    base = r / name
    for directory, dirs, names in os.walk(base, followlinks=False):
        for child in list(dirs):
            p = Path(directory) / child
            if child.casefold() in nr.EXCLUDED_DIRS:
                dirs.remove(child)
                excluded.append({'path': p.relative_to(r).as_posix(), 'reason': 'protected/generated'})
            elif p.is_symlink() or getattr(p.lstat(), 'st_file_attributes', 0) & 1024:
                raise RuntimeError('reparse directory: ' + str(p))
        for name in names:
            p = Path(directory) / name
            if p.suffix.lower() not in extensions or name.casefold() in nr.SECRET_NAMES or name.casefold().startswith('.env.'):
                continue
            b = nr.regular(p)
            try: b.decode('utf-8-sig')
            except UnicodeError:
                excluded.append({'path': p.relative_to(r).as_posix(), 'reason': 'non-UTF8 source; reviewer must report relevant missing dependency'})
                continue
            rel = p.relative_to(r).as_posix()
            files[rel] = {'sha256': nr.sha(b), 'bytes': len(b)}
for p in r.iterdir():
    if p.is_file() and p.suffix.lower() in extensions:
        b = nr.regular(p)
        try: b.decode('utf-8-sig')
        except UnicodeError: continue
        files[p.name] = {'sha256': nr.sha(b), 'bytes': len(b)}
source.mkdir(parents=True, exist_ok=False)
for rel, row in files.items():
    b = nr.regular(r / rel)
    assert nr.sha(b) == row['sha256']
    p = source / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_bytes(b)

prior = json.loads((d / 'all-original-prior-readback.json').read_text(encoding='utf-8'))
refs = set(prior['sources'])
refs.update(json.loads((d / 'all-original-prior-artifacts.json').read_text(encoding='utf-8')))
evidence_roots = [d,
    r / '_workflow/local-wait-admission-gates-20261004/auto-relay-migration-terminal-seal-20261004-from-01a106f5',
    r / '_workflow/local-wait-admission-gates-20261004/auto-relay-legacy-seal-source-20261004-from-01a106d9']
for base in evidence_roots:
    for directory, dirs, names in os.walk(base):
        dirs[:] = [n for n in dirs if not any(s in n.lower() for s in ('products', 'input-snapshot', 'snapshot'))]
        for name in names:
            p = Path(directory) / name
            if p.suffix.lower() in {'.json', '.md', '.txt', '.trx', '.log', '.xml', '.py', '.diff', '.patch'}:
                refs.add(p.relative_to(r).as_posix())
refs.update([
    '_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md',
    '_workflow/local-wait-admission-gates-20261004/plan.json',
    '_workflow/local-wait-admission-gates-20261004/manifest.json',
    '_workflow/local-wait-admission-gates-20261004/owner-policy.json',
    '_workflow/local-wait-admission-gates-20261004/original-send-round/current-review-config.json',
    '_workflow/local-wait-admission-gates-20261004/g10-completion/independent-audit-original.md',
    '_workflow/usable-delivery-20261003/DELIVERY-FIRST-POLICY.md',
    '_workflow/usable-delivery-20261003/DELIVERY-COVERAGE.md',
    'C:/Users/Administrator/.codex/skills/mistletoe-independent-review/references/work-package-review.md'])
contract_rows = {}
for rel in sorted(refs):
    p = Path(rel) if Path(rel).is_absolute() else r / rel
    if not p.is_file():
        excluded.append({'path': rel, 'reason': 'missing historical contract/evidence; preserve as unknown'})
        continue
    b = nr.regular(p)
    target_rel = rel if not Path(rel).is_absolute() else '_external/work-package-review.md'
    q = contracts / target_rel
    q.parent.mkdir(parents=True, exist_ok=True)
    q.write_bytes(b)
    contract_rows[target_rel] = {'origin': str(p), 'sha256': nr.sha(b), 'bytes': len(b)}
for rel, row in files.items(): assert nr.sha(nr.regular(r / rel)) == row['sha256']
git = nr.git_identity(r)
(out / 'git-identity.json').write_bytes(nr.encode(git))
for i, (metadata, patch) in enumerate(nr.git_patches(r)):
    p = out / 'git' / (metadata['phase'] + '-' + str(i) + '.patch')
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_bytes(patch)
(out / 'prior.json').write_bytes(nr.encode(prior))
snapshot = {'kind': 'owner-authorized equivalent read-only full textual source snapshot; not native prepare receipt or certified execution',
    'request_id': ident, 'source': str(source), 'contracts': str(contracts), 'source_root': str(r),
    'files': files, 'contract_files': contract_rows, 'excluded': excluded,
    'scope': 'All UTF8 source in all product/test/library/build/tools/docs domains, navigation is not a whitelist. Binary assets/runtime/User excluded; relevant omitted dependency must remain unknown.',
    'git': git, 'prior_sha256': nr.sha(nr.encode(prior)),
    'model': 'gpt-6.1-sol', 'effort': 'high', 'stage': 'implementation',
    'authorization': str(d / 'owner-implementation-grant.json')}
(out / 'snapshot.json').write_bytes(nr.encode(snapshot))
observation = {'request_id': ident, 'snapshot': str(out), 'snapshot_sha256': nr.sha(nr.encode(snapshot)),
    'source_files': len(files), 'source_bytes': sum(v['bytes'] for v in files.values()),
    'contract_files': len(contract_rows), 'new_dispatches': 0,
    'native_prepare_not_invoked': 'Current finite grant cannot truthfully satisfy native prepare cap_disabled=true; equivalent frozen read-only source retained, no tool/policy modification or forged receipt.'}
(d / 'comprehensive-review-snapshot-observation.json').write_bytes(nr.encode(observation))
print(json.dumps(observation, ensure_ascii=False))
