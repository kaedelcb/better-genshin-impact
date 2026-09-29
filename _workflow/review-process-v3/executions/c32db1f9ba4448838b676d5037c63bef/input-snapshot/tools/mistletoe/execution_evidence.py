"""Capture execution provenance separately from pass/fail; fresh products/results only."""
from __future__ import annotations
import argparse
import difflib
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import uuid
from review_support import (Blocked, collect, encode, hashes, load, path, publish,
                            require, sha, verify_bundle, lock)

PURPOSES = {'current_regression', 'negative_test', 'historical_baseline', 'comparison', 'mutation'}

def inputs(root, recipe):
    return hashes(collect(root, recipe['input_roots'], recipe['input_files'], ['_workflow']))

def product_hashes(out, names):
    result = {}
    for name in names:
        p = path(out, name, protected=False)
        require(p.is_file(), 'missing execution product/result: ' + name)
        result[name] = sha(p.read_bytes())
    return result

def command(argv, root, out, resolve_executable=True):
    require(isinstance(argv, list) and argv and all(isinstance(a, str) and a for a in argv), 'invalid argv')
    expanded = [a.replace('{out}', str(out)).replace('{root}', str(root)) for a in argv]
    if resolve_executable:
        executable = shutil.which(expanded[0])
        require(executable and Path(executable).is_file(), 'executable unavailable')
        expanded[0] = str(Path(executable).resolve())
    return expanded

def capture(root, recipe_path, output_parent):
    root = Path(root).resolve()
    require(Path(output_parent).parts[0] == '_workflow', 'execution output must be _workflow/')
    with lock(path(root, output_parent)):
        return _capture(root, recipe_path, output_parent)

def _capture(root, recipe_path, output_parent):
    bundle = verify_bundle()
    root = Path(root).resolve()
    recipe_bytes = path(root, recipe_path).read_bytes()
    recipe = load(path(root, recipe_path))
    require(recipe.get('purpose') in PURPOSES and recipe.get('conditions'), 'purpose/conditions required')
    require(recipe.get('results') and len(set(recipe['results'])) == len(recipe['results']), 'results required')
    parent = path(root, output_parent)
    require(Path(output_parent).parts[0] == '_workflow', 'execution output must be _workflow/')
    out = parent / uuid.uuid4().hex
    before = inputs(root, recipe)
    out.mkdir(parents=True, exist_ok=False)
    for rel, h in before.items():
        b = path(root, rel).read_bytes()
        require(sha(b) == h, 'input changed before execution')
        target = path(out / 'input-snapshot', rel); target.parent.mkdir(parents=True, exist_ok=True); target.write_bytes(b)
    started = time.time_ns()
    build = command(recipe['build_argv'], root, out) if recipe.get('build_argv') else None
    run = command(recipe['run_argv'], root, out, False)
    products = recipe.get('products', [])
    require(not set(recipe['results']).intersection({'receipt.json', 'run.log', 'build.log', *products}),
            'results must be distinct from products and runner artifacts')
    require(not any(Path(n).parts[0] == 'input-snapshot' for n in products + recipe['results']), 'reserved input snapshot directory')
    require(all(not path(out, n, protected=False).exists() for n in products + recipe['results']), 'stale outputs')
    if build:
        require(products and any('{out}' in a for a in recipe['build_argv']), 'build must generate fresh products')
        require(any(Path(a) == path(out, p, protected=False) for p in products for a in run), 'run must reference fresh product')
    else:
        require(not products and any(Path(a) == path(root, p) for p in before for a in run), 'run must reference bound source')
    execution_env = os.environ.copy()
    overrides = recipe.get('environment', {})
    require(isinstance(overrides, dict) and all(isinstance(k, str) and isinstance(v, str) for k, v in overrides.items()), 'invalid execution environment')
    execution_env.update(overrides)
    environment_identity = {k: sha(v.encode('utf-8')) for k, v in sorted(execution_env.items())}
    binaries = {}
    logs, exits = {}, {}
    timeout = recipe.get('timeout_seconds', 1800)
    require(type(timeout) is int and 1 <= timeout <= 14400, 'invalid execution timeout')
    # Timeouts preserve partial evidence and block certification. No implicit retry.
    for phase, argv in [('build', build), ('run', run)]:
        if argv is None:
            continue
        if phase == 'run':
            built = product_hashes(out, products)
            require(all(not path(out, n, protected=False).exists() for n in recipe['results']), 'results predate run')
            run = command(recipe['run_argv'], root, out)
            argv = run
        binaries[argv[0]] = sha(Path(argv[0]).read_bytes())
        log = out / (phase + '.log')
        import process_runner
        exit_code, stdout, stderr = process_runner.run(argv, cwd=root, env=execution_env, directory=out,
                recovery_directory=parent, phase=phase, timeout=timeout)
        log.write_bytes(stdout.read_bytes() + stderr.read_bytes())
        logs[phase + '.log'] = sha(log.read_bytes())
        for artifact in out.glob(phase + '-*'):
            if artifact.is_file(): logs[artifact.name] = sha(artifact.read_bytes())
        exits[phase] = exit_code
        if phase == 'build':
            require(exit_code == 0, 'build failed; cannot certify test execution')
    require(inputs(root, recipe) == before, 'execution input drift')
    require(path(root, recipe_path).read_bytes() == recipe_bytes, 'execution recipe drift')
    require(product_hashes(out, products) == built, 'execution product drift')
    require(product_hashes(out / 'input-snapshot', list(before)) == before, 'captured input bytes changed')
    require(all(sha(Path(p).read_bytes()) == h for p, h in binaries.items()), 'executor drift')
    receipt = {'version': 3, 'execution_id': out.name, 'bundle': bundle, 'root': str(root),
               'recipe': recipe, 'recipe_sha256': sha(recipe_bytes), 'recipe_path': recipe_path,
               'inputs': before, 'products': built, 'results': product_hashes(out, recipe['results']),
               'logs': logs, 'executables': binaries, 'build_argv': build, 'run_argv': run,
               'exit_codes': exits, 'started_ns': started, 'completed_ns': time.time_ns(),
               'platform': sys.platform, 'purpose': recipe['purpose'], 'provenance': 'complete',
               'environment_identity': environment_identity,
               'verdict': 'NOT PROVIDED'}
    publish(out / 'receipt.json', receipt)
    return (out / 'receipt.json').relative_to(root).as_posix()

def validate(root, receipt_path, expected_inputs=None, purpose=None):
    root = Path(root).resolve()
    p = path(root, receipt_path)
    r = load(p)
    require(r.get('version') == 3 and r.get('provenance') == 'complete' and r.get('root') == str(root),
            'invalid execution provenance')
    require(r['bundle'] == verify_bundle(), 'execution bundle drift')
    require(r['execution_id'] == p.parent.name and r['started_ns'] < r['completed_ns'], 'execution identity invalid')
    require(r['recipe_sha256'] == sha(path(root, r['recipe_path']).read_bytes()), 'execution recipe changed')
    require(r['recipe'] == load(path(root, r['recipe_path'])), 'execution recipe mismatch')
    require(product_hashes(p.parent / 'input-snapshot', list(r['inputs'])) == r['inputs'], 'execution input snapshot drift')
    for field in ('products', 'results', 'logs'):
        require(product_hashes(p.parent, list(r[field])) == r[field], 'execution artifact drift: ' + field)
    require(r['logs'] and r['results'] and r['exit_codes'].get('build', 0) == 0, 'incomplete execution')
    if expected_inputs is not None:
        require(r['inputs'] == expected_inputs, 'execution identity mismatch')
    if purpose == 'current_regression':
        require(r['purpose'] == purpose and r['exit_codes']['run'] == 0, 'not current green execution')
        require(inputs(root, r['recipe']) == r['inputs'], 'current execution source drift')
    elif purpose is not None:
        require(purpose == r['purpose'], 'wrong execution purpose')
    return r

def conditions(receipt, receipt_path):
    out = Path(receipt['root']) / Path(receipt_path).parent
    normalize = lambda argv: [a.replace(str(out), '{out}') for a in argv] if argv else None
    return {'build': normalize(receipt['build_argv']), 'run': normalize(receipt['run_argv']),
            'toolchain': {p: h for p, h in receipt['executables'].items() if not Path(p).is_relative_to(out)},
            'platform': receipt['platform'], 'environment': receipt['environment_identity'],
            'conditions': receipt['recipe']['conditions'], 'products': sorted(receipt['products']),
            'results': sorted(receipt['results'])}

def validate_manifest(root, manifest):
    """Authenticate every current test and all B/M/B legs before old semantic gates."""
    import workflow as w
    refs = manifest.get('execution_evidence', [])
    require(refs, 'execution_evidence required')
    indexed = {}
    for ref in refs:
        r = validate(root, ref)
        if r['purpose'] != 'historical_baseline':
            require(r['inputs'] == inputs(root, r['recipe']), 'current test execution input drift')
            require(set(manifest['sources']) <= r['inputs'].keys(), 'execution omits manifest source')
        for rel in r['results']:
            name = (Path(ref).parent / rel).as_posix()
            require(name not in indexed, 'duplicate execution result')
            indexed[name] = (r, ref)
    current_green = False
    for test in manifest['tests']:
        require(test['path'] in indexed, 'test lacks execution provenance')
        r, _ = indexed[test['path']]
        trx = w.parse_trx(path(root, test['path']).read_bytes())
        if test.get('expect_success', True):
            require(r['purpose'] == 'current_regression' and r['exit_codes']['run'] == 0 and w.successful(trx),
                    'nonzero/negative execution cannot prove green')
            current_green = True
        else:
            require(r['purpose'] in {'negative_test', 'comparison', 'historical_baseline'}, 'negative/historical evidence purpose required')
            if r['purpose'] == 'negative_test':
                target = r['recipe'].get('expected_failure', {})
                require(r['exit_codes']['run'] > 0 and any(row['test_id'] == target.get('test_id')
                        and row['outcome'] == 'Failed' and target.get('assertion_contains')
                        and target['assertion_contains'] in row['stack'] for row in trx['rows']),
                        'negative test did not hit expected assertion')
    require(current_green, 'current green regression required')
    if manifest.get('comparison'):
        comparison = manifest['comparison']
        require(comparison['baseline'] in indexed and comparison['final'] in indexed, 'comparison result lacks execution provenance')
        baseline, baseline_ref = indexed[comparison['baseline']]
        final, final_ref = indexed[comparison['final']]
        require(baseline['purpose'] == 'historical_baseline' and final['purpose'] in {'current_regression', 'comparison'},
                'comparison execution purposes invalid')
        require(baseline['completed_ns'] < final['started_ns'] and
                conditions(baseline, baseline_ref) == conditions(final, final_ref), 'comparison execution conditions differ')
    for record in manifest.get('mutations', []):
        chain = [validate(root, record[k]) for k in ('baseline_execution', 'mutant_execution', 'restored_execution')]
        b, m, restored = chain
        refs = [record[k] for k in ('baseline_execution', 'mutant_execution', 'restored_execution')]
        require(conditions(b, refs[0]) == conditions(m, refs[1]) == conditions(restored, refs[2]), 'mutation execution conditions differ')
        require(b['inputs'] == restored['inputs'] == inputs(root, restored['recipe']), 'incomplete mutation restore')
        expected = dict(b['inputs']); expected[record['source']] = record['mutant_sha256']
        require(m['inputs'] == expected and b['inputs'][record['source']] == record['original_sha256'], 'wrong mutation inputs')
        require(b['completed_ns'] < m['started_ns'] and m['completed_ns'] < restored['started_ns'], 'mutation ordering')
        for key, receipt, field in zip(('baseline', 'mutant', 'restored'), chain,
                                       ('baseline_execution', 'mutant_execution', 'restored_execution')):
            require(record[key + '_trx'] in [(Path(record[field]).parent / n).as_posix() for n in receipt['results']],
                    'mutation result replay')
            require(record[key + '_exit'] == receipt['exit_codes']['run'], 'mutation exit mismatch')
        require(record.get('patch') and sha(path(root, record['patch']).read_bytes()) == record.get('patch_sha256'),
                'mutation patch missing/drift')
        original_bytes = path(path(root, refs[0]).parent / 'input-snapshot', record['source']).read_bytes()
        mutant_bytes = path(path(root, refs[1]).parent / 'input-snapshot', record['source']).read_bytes()
        canonical_patch = ''.join(difflib.unified_diff(original_bytes.decode('utf-8').splitlines(keepends=True),
                            mutant_bytes.decode('utf-8').splitlines(keepends=True), fromfile=record['source'], tofile=record['source'])).encode('utf-8')
        require(path(root, record['patch']).read_bytes() == canonical_patch and canonical_patch,
                'mutation patch does not describe executed bytes')

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--root', default='.')
    p.add_argument('--recipe', required=True)
    p.add_argument('--out', required=True)
    a = p.parse_args()
    try:
        print(capture(Path(a.root), a.recipe, a.out))
    except (ValueError, OSError, KeyError, subprocess.SubprocessError) as exc:
        print('BLOCKED: ' + str(exc)); return 2
    return 0

if __name__ == '__main__':
    sys.exit(main())
