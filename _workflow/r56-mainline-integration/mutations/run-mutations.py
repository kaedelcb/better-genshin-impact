from pathlib import Path
import hashlib, json, subprocess, sys

ROOT = Path('.').resolve()
SOURCE = 'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs'
TESTPROJ = 'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'
FILTER = 'FullyQualifiedName~SnapshotIntegrity_CompleteFileSetAndBytes_AreVerifiedAndCommitFailsClosed'
OUT = Path('_workflow/r56-mainline-integration/mutations')
BASELINE_TRX = '_workflow/r56-mainline-integration/final/targeted-final.trx'
THEORY = 'SnapshotIntegrity_CompleteFileSetAndBytes_AreVerifiedAndCommitFailsClosed'

MUTATIONS = [
    dict(name='extra', row='extra', expected_line=188,
         needle='return "snapshot_untracked_file:" + extra;', replacement='_ = extra;',
         label='drop the untracked-snapshot-file rejection so an extra snapshot file is accepted'),
    dict(name='missing', row='missing', expected_line=188,
         needle='if (!File.Exists(target)) return "snapshot_file_missing:" + p.Key;',
         replacement='if (false) return "snapshot_file_missing:" + p.Key;',
         label='bypass the missing snapshot file rejection'),
    dict(name='changed', row='changed', expected_line=188,
         needle='if (!string.Equals(hash, p.Value, StringComparison.Ordinal)) return "snapshot_hash_mismatch:" + p.Key;',
         replacement='if (false) return "snapshot_hash_mismatch:" + p.Key;',
         label='bypass the snapshot hash mismatch rejection'),
    dict(name='commit-refusal', row='missing', expected_line=190,
         needle='if (VerifySnapshot() is { Length: > 0 } bad) return MigrationResult.Fail("snapshot_invalid:" + bad, m.Stage);',
         replacement='if (string.IsNullOrEmpty(VerifySnapshot())) return MigrationResult.Fail("snapshot_invalid:" + VerifySnapshot(), m.Stage);',
         label='invert the Commit() snapshot validity gate so a corrupted snapshot can be committed',
         anchor=('public MigrationResult Commit()', 'public MigrationResult Rollback()')),
    dict(name='production-gate', row='changed', expected_line=196,
         needle='if (!auth.Success) return auth;',
         replacement='if (!auth.Success) { action(); return auth; }',
         label='execute the action even when production authorization fails'),
]

def sha(b): return hashlib.sha256(b).hexdigest().upper()

src_path = Path(SOURCE)
base_bytes = src_path.read_bytes()
base_sha = sha(base_bytes)
print('base source bytes=', len(base_bytes), 'sha256=', base_sha)

records = []
for m in MUTATIONS:
    d = OUT / m['name']
    d.mkdir(parents=True, exist_ok=True)
    if sha(src_path.read_bytes()) != base_sha:
        sys.exit('source not at baseline before ' + m['name'])
    (d / 'source-before.cs').write_bytes(base_bytes)
    text = base_bytes.decode('utf-8')
    if m.get('anchor'):
        a, b = m['anchor']
        s = text.index(a); e = text.index(b, s)
        window = text[s:e]
        if window.count(m['needle']) != 1:
            sys.exit('commit guard must occur exactly once inside Commit(): ' + m['name'])
        at = s + window.index(m['needle'])
    else:
        if text.count(m['needle']) != 1:
            sys.exit('needle must occur exactly once: ' + m['name'])
        at = text.index(m['needle'])
    mutant = text[:at] + m['replacement'] + text[at + len(m['needle']):]
    src_path.write_bytes(mutant.encode('utf-8'))
    mutant_sha = sha(src_path.read_bytes())
    if mutant_sha == base_sha:
        sys.exit('mutation did not change source: ' + m['name'])
    (d / 'mutation.patch.txt').write_text('NEEDLE:\n' + m['needle'] + '\n\nREPLACEMENT:\n' + m['replacement'] + '\n', encoding='utf-8', newline='\n')
    env = {'DOTNET_CLI_UI_LANGUAGE': 'en'}
    import os
    run_env = dict(os.environ); run_env.update(env)
    p = subprocess.run(['dotnet', 'test', TESTPROJ, '-p:DeployToBgiTools=false', '--no-restore',
                        '--filter', FILTER, '--logger', 'trx;LogFileName=mutant.trx',
                        '--results-directory', str(d)], capture_output=True, text=True, errors='replace', env=run_env)
    (d / 'mutant.log').write_text(p.stdout + p.stderr, encoding='utf-8', newline='\n')
    mutant_exit = p.returncode
    # restore
    src_path.write_bytes(base_bytes)
    restored_sha = sha(src_path.read_bytes())
    (d / 'restore.log').write_text('restored_sha256=' + restored_sha + '\nrestored_exact=' + str(restored_sha == base_sha) + '\n', encoding='utf-8', newline='\n')
    if restored_sha != base_sha:
        sys.exit('exact restore failed: ' + m['name'])
    p2 = subprocess.run(['dotnet', 'test', TESTPROJ, '-p:DeployToBgiTools=false', '--no-restore',
                         '--filter', FILTER, '--logger', 'trx;LogFileName=restored.trx',
                         '--results-directory', str(d)], capture_output=True, text=True, errors='replace', env=run_env)
    (d / 'restored.log').write_text(p2.stdout + p2.stderr, encoding='utf-8', newline='\n')
    rec = dict(id=m['name'], mutation_label=m['label'], source=SOURCE,
               original_sha256=base_sha.lower(), mutant_sha256=mutant_sha.lower(), restored_sha256=restored_sha.lower(),
               baseline_trx=BASELINE_TRX, mutant_trx=str(d / 'mutant.trx'), restored_trx=str(d / 'restored.trx'),
               build_log=str(d / 'mutant.log'), build_exit=0, baseline_exit=0,
               mutant_exit=mutant_exit, restored_exit=p2.returncode, expected_line=m['expected_line'])
    records.append(rec)
    (OUT / 'mutation-records.json').write_text(json.dumps(records, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')
    print(json.dumps({k: rec[k] for k in ('id', 'mutant_sha256', 'mutant_exit', 'restored_exit')}, ensure_ascii=False))
print('final source sha256=', sha(src_path.read_bytes()), 'equals baseline:', sha(src_path.read_bytes()) == base_sha)
