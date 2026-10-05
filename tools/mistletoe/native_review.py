"""Native Codex review evidence adapter. Does not launch models or count requests.

The host creates an independent reviewer. This module freezes the full project,
captures its actual local rollout, and checks evidence identity and stage gates.
It is not an operating-system sandbox or a semantic correctness oracle.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import shlex
import stat
import subprocess
import uuid

from review_support import Blocked, encode, load, require, sha
import review_support as support
import storage_limits as storage

EXCLUDED_DIRS = {'.git', '.kiro', '.codex', '.serena', 'user', 'bin', 'obj', 'node_modules',
                 '__pycache__', '_workflow'}
EXCLUDED_DIRS |= storage.GENERATED_DIRS
SECRET_NAMES = {'.env', 'auth.json', 'credentials.json', 'secrets.json'}
HISTORICAL_OUTPUTS = {'_batch18', '_batch19', '_batch21', '_ev1', '_ev2', '_ev3'}
SEVERITIES = {'suggestion': 0, 'important': 1, 'must': 2}
DIMENSIONS = {'scope', 'state', 'concurrency', 'fault', 'impact_chain',
              'change_scale', 'uncertainty', 'prior_findings'}


def publish(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    if Path(path).name in {'receipt.json','snapshot.json'} and storage.ACTIVE.get() is not None:
        storage.ACTIVE.get().check(measure=True)
    storage.write(path, encode(value))


def persist_same(path, content, allow_prefix=False):
    """Complete interrupted publication only with exactly the same source."""
    path = Path(path)
    if path.exists():
        old = regular(path)
        if old != content and allow_prefix and content.startswith(old):
            storage.write(path, content[len(old):], 'ab')
            return
        require(old == content, 'conflicting capture artifact: ' + path.name)
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    storage.write(path, content)


def atomic_same(path, content):
    """Publish a complete small binding without replacing an existing file."""
    path = Path(path); path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        require(regular(path) == content, 'conflicting atomic binding: ' + path.name)
        return
    temporary = path.parent / ('.' + path.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        storage.write(temporary, content)
        try: os.link(temporary, path)
        except FileExistsError: require(regular(path) == content, 'conflicting atomic binding')
    finally:
        # Only this invocation's newly created, exact sibling temporary file.
        if temporary.exists(): temporary.unlink()


def regular(path):
    s = path.lstat()
    require(stat.S_ISREG(s.st_mode) and not path.is_symlink()
            and not getattr(s, 'st_file_attributes', 0) & 1024, 'nonregular input: ' + str(path))
    return path.read_bytes()


def relative(root, rel):
    p = Path(rel)
    require(isinstance(rel, str) and rel and not p.is_absolute()
            and '..' not in p.parts and ':' not in rel, 'unsafe relative input')
    require('mistletoe-storage-control' not in {part.casefold() for part in p.parts},
            'storage coordination/scratch cannot be evidence input')
    q = root / p
    for part in [q, *q.parents]:
        if part == root.parent: break
        require(not part.is_symlink() and not (part.exists() and
                getattr(part.lstat(), 'st_file_attributes', 0) & 1024), 'input link/reparse path')
    require(q.resolve().is_relative_to(root.resolve()), 'input escapes project')
    return q


def inventory(root, excluded_records=None):
    """All project paths, including ignored/untracked source; outputs never input."""
    root = Path(root).resolve(); result = {}
    for directory, dirs, names in os.walk(root, followlinks=False):
        for name in list(dirs):
            p = Path(directory) / name
            require(not p.is_symlink() and not getattr(p.lstat(), 'st_file_attributes', 0) & 1024,
                    'project contains directory link/reparse: ' + str(p))
            reason = ('protected/generated directory' if name.casefold() in EXCLUDED_DIRS else
                      'named historical output directory' if p.parent == root and name in HISTORICAL_OUTPUTS else None)
            if reason:
                dirs.remove(name)
                if excluded_records is not None:
                    excluded_records.append({'path': p.relative_to(root).as_posix(), 'reason': reason})
        for name in names:
            if name.casefold() in SECRET_NAMES | EXCLUDED_DIRS or name.casefold().startswith('.env.'):
                if excluded_records is not None:
                    excluded_records.append({'path': (Path(directory)/name).relative_to(root).as_posix(),
                                             'reason': 'protected metadata/credential name'})
                continue
            p = Path(directory) / name
            b = regular(p)
            result[p.relative_to(root).as_posix()] = {'sha256': sha(b), 'bytes': len(b)}
    require(result, 'empty full-project input')
    return result


def source_path_allowed(rel):
    parts = Path(rel).parts
    return (not any(p.casefold() in EXCLUDED_DIRS for p in parts)
            and parts[0] not in HISTORICAL_OUTPUTS
            and parts[-1].casefold() not in SECRET_NAMES
            and not parts[-1].casefold().startswith('.env.'))


def git_patches(root):
    rows = []
    for phase, options in [('staged', ['--cached']), ('unstaged', [])]:
        for status, names in support._git_change_records(root, options):
            selected = tuple(p for p in names if source_path_allowed(p))
            if not selected: continue
            # Bound per change, including cross-boundary rename/delete, using
            # the already validated argument-budget implementation.
            record = (status, selected)
            patch = support._git_change_patch(root, options, record)
            rows.append(({'phase': phase, 'status': status, 'paths': list(selected), 'sha256': sha(patch)}, patch))
    return rows


def git_identity(root):
    def run(*args):
        p = subprocess.run(['git', '-C', str(root), *args], capture_output=True)
        require(p.returncode == 0, 'Git capture failed')
        return p.stdout
    return {'head': run('rev-parse', 'HEAD').decode().strip(),
            'branch': run('branch', '--show-current').decode().strip(),
            'status': run('status', '--porcelain=v1', '--untracked-files=no').decode('utf-8'),
            'tracked': run('ls-files', '-z').decode('utf-8').split('\0'),
            'staged_names': run('diff', '--cached', '--name-status', '-z').decode('utf-8'),
            'unstaged_names': run('diff', '--name-status', '-z').decode('utf-8'),
            'staged_index_sha256': sha(run('ls-files', '--stage', '-z')),
            'source_diffs': [item for item, _ in git_patches(root)]}


def full_identity(root, extra=()):
    excluded = []; files = inventory(root, excluded)
    refs = {p: sha(regular(relative(Path(root), p))) for p in extra}
    return {'files': files, 'git': git_identity(root), 'extra': refs,
            'excluded_directories': sorted(EXCLUDED_DIRS),
            'excluded_sensitive_names': sorted(SECRET_NAMES),
            'excluded_root_artifacts': sorted(HISTORICAL_OUTPUTS),
            'actual_excluded_paths': sorted(excluded, key=lambda x: x['path'])}


def prior_json(root, ref):
    """Known JSON artifacts must fail as Blocked, never as an empty ledger."""
    try:
        value = load(relative(root, ref))
    except (OSError, UnicodeDecodeError, ValueError) as exc:
        raise Blocked("invalid prior artifact: " + ref) from exc
    require(isinstance(value, dict), "non-object prior artifact: " + ref)
    return value


def collect_prior(root, state, seed_refs):
    """Import original reports; authenticate, preserve and expand derived ledgers."""
    root = Path(root).resolve()
    reports = list(seed_refs)
    preserved = []
    ledgers = []

    def ref(path):
        path = Path(path).resolve()
        require(path.is_relative_to(root), "prior source outside project")
        return path.relative_to(root).as_posix()

    def ordinal(stem):
        return (stem.isascii() and stem.isdecimal() and int(stem) > 0
                and stem == str(int(stem)))

    for attempt in sorted((state / "requests").glob("*")):
        require(attempt.is_dir() and not attempt.is_symlink(),
                "invalid prior request directory")
        # Preserve the request ledger even when no report was returned.
        ledger_ref = ref(attempt / "prior.json")
        ledgers.append(ledger_ref)
        preserved.append(ledger_ref)
        if (attempt / "report.json").is_file():
            reports.append(ref(attempt / "report.json"))

        base = attempt / "checkpoints"
        if not base.is_dir():
            continue

        units, derived, bindings = {}, {}, {}
        for path in sorted(base.glob("*.json")):
            stem = path.stem
            if ordinal(stem):
                units[int(stem)] = path
            elif stem.endswith("-prior") and ordinal(stem[:-6]):
                derived[int(stem[:-6])] = path
            elif stem.endswith("-source") and ordinal(stem[:-7]):
                bindings[int(stem[:-7])] = path
            else:
                raise Blocked("unrecognised checkpoint JSON: " + ref(path))

        require(set(units) == set(derived) == set(bindings),
                "checkpoint unit/prior/source mismatch")
        require(set(units) == set(range(1, len(units) + 1)),
                "checkpoint ordinal gap")

        expected = {
            f"{number}{suffix}"
            for number in units
            for suffix in (".json", "-prior.json",
                           "-source.json", "-native-rollout.jsonl")
        }
        require(all(path.is_file() and path.name in expected
                    for path in base.iterdir()),
                "unrecognised/orphan checkpoint artifact")

        if not units:
            continue

        request_ref = ref(attempt / "request.json")
        snapshot_ref = ref(attempt / "snapshot.json")
        request = prior_json(root, request_ref)
        snapshot = prior_json(root, snapshot_ref)
        require(sha(encode(snapshot)) == request["snapshot_hash"],
                "prior checkpoint snapshot identity drift")

        # Reuse the existing producer/completion/parent-chain authentication.
        checkpoint_chain(attempt, len(units), request, snapshot)

        for number, path in units.items():
            saved = prior_json(root, ref(path))
            binding = prior_json(root, ref(bindings[number]))
            raw_ref = ref(base / f"{number}-native-rollout.jsonl")
            raw = regular(relative(root, raw_ref))
            require(
                binding.get("request_sha256")
                    == sha(regular(relative(root, request_ref)))
                and binding.get("ordinal") == number
                and binding.get("rollout_bytes") == len(raw)
                and binding.get("rollout_sha256") == sha(raw)
                and binding.get("checkpoint_sha256")
                    == sha(encode(saved["checkpoint"])),
                "checkpoint source binding drift",
            )
            reports.append(ref(path))
            ledgers.append(ref(derived[number]))
            preserved.extend([
                ref(path), ref(derived[number]),
                ref(bindings[number]), raw_ref,
            ])
        preserved.extend([request_ref, snapshot_ref])

    loaded_ledgers = []
    for ledger_ref in dict.fromkeys(ledgers):
        ledger = prior_json(root, ledger_ref)
        require(all(isinstance(ledger.get(name), dict)
                    for name in ("findings", "unknowns", "sources")),
                "invalid derived prior structure: " + ledger_ref)
        for source_ref, digest in ledger["sources"].items():
            require(isinstance(source_ref, str) and isinstance(digest, str),
                    "invalid derived prior source")
            try:
                actual = sha(regular(relative(root, source_ref)))
            except OSError as exc:
                raise Blocked("derived prior source missing: " + source_ref) from exc
            require(actual == digest,
                    "derived prior source SHA mismatch: " + source_ref)
            reports.append(source_ref)
        loaded_ledgers.append((ledger_ref, ledger))

    reports = list(dict.fromkeys(reports))
    prior = prior_register(root, reports)

    for ledger_ref, ledger in loaded_ledgers:
        for category in ("findings", "unknowns"):
            for key, row in ledger[category].items():
                require(isinstance(row, dict) and row.get("key") == key
                        and "original" in row,
                        "invalid derived original key: " + ledger_ref)
                original = row["original"]
                if category == "findings":
                    require(isinstance(original, dict),
                            "invalid derived original finding")
                    expected_key = sha(encode(original))
                else:
                    require(isinstance(original, str) and original.strip(),
                            "invalid derived original unknown")
                    expected_key = sha(original.encode("utf-8"))
                require(key == expected_key
                        and key in prior[category]
                        and prior[category][key]["original"] == original,
                        "derived original omitted/changed: "
                        + category + ":" + key)

                # Preserve recorded origin metadata; do not silently replace it.
                origin = row.get("source")
                require(isinstance(origin, str) and origin,
                        "derived original source missing")
                origin_path = Path(origin)
                origin_ref = ref(origin_path) if origin_path.is_absolute() else origin
                require(origin_ref in prior["sources"],
                        "derived original source not imported: " + origin_ref)

    return prior, list(dict.fromkeys(preserved))

def prior_register(root, refs):
    """Keep original objects by hash. Same ID does not discard distinct originals."""
    findings = {}; unknowns = {}; sources = {}
    for rel in refs:
        b = regular(relative(Path(root), rel))
        sources[rel] = sha(b)
        try: doc = json.loads(b.decode('utf-8-sig'))
        except ValueError:
            require(Path(rel).name != 'report.json', 'required report is malformed: ' + rel)
            continue  # Keep raw history documents in contracts for autonomous review.
        if not isinstance(doc, dict):
            require(Path(rel).name != "report.json",
                    "required report is not an object")
            continue
        body = doc.get("checkpoint", doc)
        require(isinstance(body, dict), "invalid checkpoint/report object")
        for field in ("findings", "prior_findings", "unknowns"):
            require(field not in body or isinstance(body[field], list),
                    "unsupported prior report field: " + rel + ":" + field)

        for f in [*body.get("findings", []), *body.get("prior_findings", [])]:
            require(isinstance(f, dict)
                    and f.get("severity") in SEVERITIES
                    and f.get("id") and f.get("obligation"),
                    "invalid original finding")
            key = sha(encode(f))
            findings[key] = {"key": key, "original": f, "source": rel}
        for text in body.get('unknowns', []):
            require(isinstance(text, str) and text.strip(), 'invalid original unknown')
            key = sha(text.encode('utf-8')); unknowns[key] = {'key': key, 'original': text, 'source': rel}
    return {'findings': findings, 'unknowns': unknowns, 'sources': sources}


def legacy_binding(root, config):
    """Discover immutable existing-batch identity and required originals."""
    if not config.get('manifest'): return {'kind': 'new', 'sources': []}
    manifest = load(relative(root, config['manifest']))
    require(manifest['batch'] == config['batch'], 'authoritative manifest batch mismatch')
    opening_ref = manifest['opening_snapshot']; opening = regular(relative(root, opening_ref))
    require(json.loads(opening.decode('utf-8-sig'))['batch'] == config['batch'], 'opening batch mismatch')
    local_ref = (Path(opening_ref).parent / 'review-process/registration.json').as_posix()
    local = relative(root, local_ref)
    import review_process as rp
    shared = rp.location(root, manifest) / 'registration.json'
    require(not shared.is_file() or local.is_file(), 'existing shared registration requires original local import')
    if not local.is_file():
        return {'kind': 'new', 'opening': opening_ref, 'opening_sha256': sha(opening), 'sources': [opening_ref]}
    registration = load(local)
    require(not shared.is_file() or sha(regular(shared)) == sha(regular(local)),
            'original local/shared registration mismatch')
    require(registration['batch'] == config['batch'] and registration['opening'] == opening_ref
            and registration['opening_sha256'] == sha(opening), 'original registration/opening drift')
    sources = {opening_ref, local_ref}
    for rel, digest in registration.get('history_hashes', {}).items():
        require(sha(regular(relative(root, rel))) == digest, 'original history missing or changed: ' + rel)
        sources.add(rel)
    # Non-gating failed reports still contain obligations; discover rather than
    # relying on a human to list request001 or its 26 findings/10 unknowns.
    for p in (local.parent/'requests').glob('*/report.json'):
        sources.add(p.relative_to(root).as_posix())
    return {'kind': 'adopt', 'opening': opening_ref, 'opening_sha256': sha(opening),
            'registration': local_ref, 'registration_sha256': sha(regular(local)), 'sources': sorted(sources)}


def assessment(value):
    require(value.get('model') == 'gpt-6.1-sol' and value.get('effort') in {'medium', 'high'},
            'native model policy mismatch')
    require(set(value.get('dimensions', {})) == DIMENSIONS and
            all(isinstance(v, str) and v.strip() for v in value['dimensions'].values()),
            'eight-dimensional assessment missing')
    require(isinstance(value.get('reason'), str) and value['reason'].strip(), 'effort rationale missing')
    require(not value.get('risk_unresolved') or value['effort'] == 'high',
            'unresolved risk requires high effort')


def execution_sources(root, manifest):
    """Reuse existing authentication and freeze the evidence actually consumed."""
    import execution_evidence as ee
    ee.validate_manifest(root, manifest)
    refs = set(manifest['execution_evidence'])
    for mutation in manifest.get('mutations', []):
        refs.update(mutation[key] for key in ('baseline_execution', 'mutant_execution', 'restored_execution'))
    for rel in sorted(refs):
        record = ee.validate(root, rel); base = Path(rel).parent
        refs.add(record['recipe_path'])
        for field in ('results', 'logs', 'products'):
            refs.update((base/name).as_posix() for name in record[field])
        refs.update((base/'input-snapshot'/name).as_posix() for name in record['inputs'])
    for mutation in manifest.get('mutations', []):
        if mutation.get('patch'): refs.add(mutation['patch'])
    refs.update(item['path'] for item in manifest.get('evidence', []))
    refs.update(item['path'] for item in manifest.get('packet', []))
    refs.update(manifest[key] for key in ('risk_matrix', 'opening_snapshot') if manifest.get(key))
    return sorted(refs)


def implementation_proofs(root, manifest, report):
    """Require real named tests/mutations, without claiming semantic proof."""
    import execution_evidence as ee
    import workflow
    ee.validate_manifest(root, manifest)
    green = {}
    declared_tests = {test['path'] for test in manifest.get('tests', [])}
    for ref in manifest['execution_evidence']:
        execution = ee.validate(root, ref)
        if execution['purpose'] != 'current_regression' or execution['exit_codes']['run'] != 0: continue
        ids = set()
        for rel in execution['results']:
            result_path = (Path(ref).parent/rel).as_posix()
            if result_path not in declared_tests:
                continue
            result = workflow.parse_trx(regular(relative(root, result_path)))
            ids.update(row['test_id'] for row in result['rows'] if row['outcome'] == 'Passed')
        green[ref] = ids
    def read(rel): return regular(relative(root, rel))
    def trx(rel): return workflow.parse_trx(read(rel))
    mutations = {m['id']: m for m in manifest.get('mutations', [])}
    for mutation in mutations.values(): workflow.mutation_check(mutation, read, trx)
    proofs = {p['finding_id']: p for p in report.get('implementation_proofs', [])}
    closing = {f['id'] for f in report['findings'] if f['obligation'] == 'implementation'
               and f['status'] == 'closed' and f['severity'] != 'suggestion'}
    closing.update(d['id'] for d in report['prior_dispositions'] if d.get('obligation') == 'implementation'
                   and d.get('disposition') == 'resolved_with_evidence' and d.get('severity') != 'suggestion')
    for ident in closing:
        require(ident in proofs, 'closed implementation finding lacks authenticated test proof')
        proof = proofs[ident]; ref = proof.get('execution_receipt'); ids = proof.get('test_ids', [])
        require(ref in green and isinstance(ids, list) and ids and set(ids) <= green[ref],
                'implementation proof names no current passed test')
        mids = proof.get('mutation_ids', [])
        require(isinstance(mids, list) and mids and set(mids) <= mutations.keys(),
                'implementation proof has no validated critical mutation')
        require(any(mutations[mid].get('target_test_id') in ids for mid in mids), 'mutation not tied to proof test')


@storage.operation('native-prepare')
def prepare(root, config_path, stage, assessment_path, agent_path):
    root = Path(root).resolve(); config = load(relative(root, config_path))
    require(isinstance(config.get('write_paths'), list) and config['write_paths']
            and all(isinstance(x, str) and x.strip() for x in config['write_paths']),
            'explicit planned write paths required')
    require(stage in {'plan', 'implementation'}, 'invalid review stage')
    a = load(relative(root, assessment_path)); assessment(a)
    policy = load(relative(root, config['owner_policy']))
    require(policy.get('request_cap_stop_disabled_for_this_goal') is True
            and policy.get('source_thread') == config['parent_thread'], 'missing current owner policy')
    if stage == 'implementation':
        require(config.get('manifest'), 'implementation review requires authoritative manifest')
        manifest = load(relative(root, config['manifest']))
        gate(root, manifest, 'implement')
        authenticated = execution_sources(root, manifest)
    else:
        authenticated = []
    state = relative(root, config['state']); state.mkdir(parents=True, exist_ok=True)
    rid = uuid.uuid4().hex; out = state / 'requests' / rid
    legacy = legacy_binding(root, config)
    # Complete interrupted same-source capture before importing obligations.
    for attempt in (state / 'requests').glob('*'):
        if (attempt/'capture-source.json').is_file() and not (attempt/'receipt.json').is_file():
            journal = load(attempt/'capture-source.json')
            original = Path(journal.get('rollout_source_path', str(attempt/'native-rollout.jsonl')))
            try: capture(attempt, original)
            except Blocked:
                # A fully preserved invalid report remains non-gating and is
                # carried forward, rather than blocking its repair review.
                require((attempt/'report.json').is_file() and
                        isinstance(load(attempt/'report.json'), dict), 'capture not recoverable from original source')
    prior, prior_artifacts = collect_prior(
        root, state, [*config.get('prior_files', []), *legacy['sources']])
    extra = list(dict.fromkeys([config_path, assessment_path, config['plan'], config['owner_policy'],
                                *([config['manifest']] if config.get('manifest') else []),
                                *config.get('extra_files', []), *prior['sources'], *prior_artifacts, *authenticated]))
    before = full_identity(root, extra)
    base = Path(config['snapshot_base']).resolve()
    require(not base.is_relative_to(root) and not root.is_relative_to(base),
            'snapshot base must be independent of project tree')
    patches = git_patches(root)
    storage.preflight_objects(base,
        [(h['sha256'],h['bytes']) for h in before['files'].values()]
        +[(h,relative(root,rel).stat().st_size) for rel,h in before['extra'].items()]
        +[(sha(content),len(content)) for _,content in patches])
    source = base / rid / 'source'
    storage.ACTIVE.get().track(source.parent)
    storage.ACTIVE.get().track(out)
    storage.ACTIVE.get().check(location=base)
    source.mkdir(parents=True, exist_ok=False)
    for rel, h in before['files'].items():
        b = regular(relative(root, rel)); require(sha(b) == h['sha256'], 'source changed during copy')
        p = source / rel; storage.immutable(base, p, b)
    # Explicit contracts/history are separate from freely searchable project source.
    contracts = source.parent / 'contracts'
    for rel, h in before['extra'].items():
        b = regular(relative(root, rel)); require(sha(b) == h, 'contract changed during copy')
        p = contracts / rel; storage.immutable(base, p, b)
    require(full_identity(root, extra) == before and inventory(source) == before['files'],
            'full input changed during capture')
    require(git_patches(root)==patches,'Git diff changed during copy')
    git_files = {}
    require([item for item, _ in patches] == before['git']['source_diffs'], 'Git diff changed during publication')
    for index, (item, content) in enumerate(patches):
        name = f"{item['phase']}-{index:04d}.patch"; target = source.parent / 'git' / name
        storage.immutable(base, target, content); git_files[name] = sha(content)
    out.mkdir(parents=True, exist_ok=False)
    snapshot = {'source': str(source), 'contracts': str(contracts), 'input': before,
                'complete': True, 'snapshot_id': rid, 'git_files': git_files}
    publish(out / 'snapshot.json', snapshot); publish(out / 'prior.json', prior)
    request = {'version': 1, 'request_id': rid, 'stage': stage, 'batch': config['batch'],
               'parent_thread': config['parent_thread'], 'agent_path': agent_path,
               'snapshot_hash': sha(encode(snapshot)), 'prior_hash': sha(encode(prior)),
               'assessment': a, 'created_utc': datetime.now(timezone.utc).isoformat(),
               'native_call': {'fork_turns': 'none', 'model': a['model'], 'reasoning_effort': a['effort']},
               'source_root': str(root), 'config_path': config_path,
               'config_hash': sha(regular(relative(root, config_path))), 'legacy_binding': legacy}
    if stage == 'implementation':
        approved = latest(root, config, 'plan')
        request['approved_plan'] = {'request_id': load(approved/'request.json')['request_id'],
                                   'receipt_sha256': sha(regular(approved/'receipt.json')),
                                   'permit_sha256': sha(regular(approved/'permit.json')),
                                   'manifest_sha256': sha(regular(relative(root, config['manifest'])))}
    publish(out / 'request.json', request)
    publish(state / 'latest' / (stage + '-' + rid + '.json'),
            {'request': str(out.relative_to(root)), 'created_utc': request['created_utc']})
    return {'request': str(out), 'snapshot': str(source), 'request_id': rid,
            'snapshot_hash': request['snapshot_hash']}


def verify_input(out, current=False):
    out = Path(out); q = load(out / 'request.json'); s = load(out / 'snapshot.json')
    require(s.get('complete') and sha(encode(s)) == q['snapshot_hash'], 'snapshot identity drift')
    require(inventory(Path(s['source'])) == s['input']['files'], 'full frozen tree drift')
    for rel, h in s['input']['extra'].items():
        require(sha(regular(relative(Path(s['contracts']), rel))) == h, 'frozen contract drift')
    for name, h in s.get('git_files', {}).items():
        require(Path(name).name == name and sha(regular(Path(s['source']).parent/'git'/name)) == h,
                'frozen readable Git diff drift')
    require(sha((out / 'prior.json').read_bytes()) == q['prior_hash'], 'prior register drift')
    if current:
        require(full_identity(Path(q['source_root']), s['input']['extra']) == s['input'],
                'current project/contract/Git identity drift')
    return q, s


def json_report(text):
    value = text.strip()
    if value.startswith('```json\n') and value.endswith('\n```'):
        value = value[8:-4]
    return json.loads(value)


def successful_source_read(item, snapshot):
    """Authenticate supported native command-read events, not keyword presence."""
    if item.get('type') != 'CommandExecution' or item.get('status') != 'completed' or item.get('exit_code') != 0:
        return False
    if item.get('stderr') or not (item.get('stdout') or item.get('aggregated_output')):
        return False
    from urllib.parse import unquote, urlparse
    cwd = item.get('cwd', '')
    if cwd.startswith('file:///'): cwd = unquote(urlparse(cwd).path.lstrip('/'))
    args = item.get('command', [])
    script = args[-1] if isinstance(args, list) and args else str(args)
    script = script.replace('\\\\', '/').replace('\\', '/')
    try:
        lexer = shlex.shlex(script, posix=True, punctuation_chars=';|(),')
        lexer.whitespace_split = True; tokens = list(lexer)
    except ValueError:
        return False
    # Native rg proof uses its actual filename:line output and the frozen map,
    # not a keyword found in a failed command or in protocol-only metadata.
    import re
    direct_python = isinstance(args, list) and args and Path(args[0]).name.casefold() in {'python', 'python.exe', 'python3', 'py', 'py.exe'}
    if direct_python: tokens = [arg.replace('\\', '/') for arg in args]
    rg_at = next((i for i, token in enumerate(tokens) if token == 'rg' and
                  (i == 0 or tokens[i-1] in {';', '|', '('})), None)
    if rg_at is not None and '--files' not in tokens[rg_at:] and ('-n' in tokens[rg_at:] or '--line-number' in tokens[rg_at:]):
        text = item.get('stdout') or item.get('aggregated_output') or ''
        for line in text.splitlines():
            match = re.match(r'^(.+?):\d+:', line)
            if not match: continue
            path = Path(match.group(1)); path = path if path.is_absolute() else Path(cwd)/path
            path = path.resolve()
            for area, files in [('source', snapshot['input']['files']), ('contracts', snapshot['input']['extra'])]:
                base = Path(snapshot[area]).resolve()
                if path.is_relative_to(base) and path.relative_to(base).as_posix() in files: return True
    invocation = tokens[1:] if tokens and tokens[0] == '&' else tokens
    if invocation and Path(invocation[0]).name.casefold() in {'python', 'python.exe', 'python3', 'py', 'py.exe'} and not any(t in {';', '|', '(', ')'} for t in invocation):
        try: result = json.loads(item.get('stdout') or item.get('aggregated_output'))
        except (ValueError, TypeError): result = {}
        proof = result.get('_native_read', {})
        invoked_helper = next((Path(t) if Path(t).is_absolute() else Path(cwd)/t for t in invocation[1:]
                               if Path(t).name == 'serena_read.py'), None)
        expected_helper = Path(snapshot['source'])/'tools/mistletoe/serena_read.py'
        structured = result.get('structuredContent') or {}
        content = structured.get('result') if isinstance(structured, dict) else structured
        if content is None:
            content = '\n'.join(item.get('text', '') for item in result.get('content', [])
                                if item.get('type') == 'text')
        if isinstance(content, str) and proof.get('tool') != 'read_file':
            try: content = json.loads(content)
            except ValueError: content = content.strip()
        def known_path(value):
            return isinstance(value, str) and value.replace('\\', '/').removeprefix('./') in snapshot['input']['files']
        def has_data(value):
            if isinstance(value, dict): return any(has_data(v) for v in value.values())
            if isinstance(value, list): return any(has_data(v) for v in value)
            return bool(value.strip()) if isinstance(value, str) else bool(value)
        tool = proof.get('tool')
        if tool == 'find_symbol':
            content = isinstance(content, list) and any(isinstance(node, dict) and node.get('name_path')
                        and known_path(node.get('relative_path')) for node in content)
        elif tool in {'search_for_pattern', 'find_referencing_symbols'}:
            content = isinstance(content, dict) and any(known_path(rel) and has_data(value)
                                                        for rel, value in content.items())
        elif tool == 'get_symbols_overview':
            content = known_path(proof.get('relative_path')) and isinstance(content, (dict, list)) and has_data(content)
        elif tool == 'read_file':
            content = known_path(proof.get('relative_path')) and isinstance(content, str) and bool(content.strip())
        else:
            content = False
        if (invoked_helper is not None and invoked_helper.resolve() == expected_helper.resolve() and
                proof.get('helper_path') and Path(proof['helper_path']).resolve() == expected_helper.resolve() and
                proof.get('snapshot_hash') == sha(encode(snapshot)) and content and
                result.get('isError') is False and proof.get('source_root') == snapshot['source'] and
                proof.get('tool') in {'read_file', 'search_for_pattern', 'get_symbols_overview', 'find_symbol', 'find_referencing_symbols'}):
            helper = snapshot['input']['files'].get('tools/mistletoe/serena_read.py', {})
            if proof.get('helper_sha256') == helper.get('sha256'): return True
    for i, token in enumerate(tokens):
        if token.casefold() != 'get-content' or (i and tokens[i-1] not in {';', '|', '('}): continue
        rest = tokens[i+1:]; targets = []
        for j, t in enumerate(rest):
            if t in {';', '|', ')'}: break
            if t.casefold() in {'-literalpath', '-path'} and j+1 < len(rest): targets.append(rest[j+1])
        if not targets and rest and not rest[0].startswith('-'): targets = [rest[0]]
        for target in targets:
            if '$' in target: continue
            p = Path(target); p = p if p.is_absolute() else Path(cwd) / p
            p = p.resolve()
            for area, files in [('source', snapshot['input']['files']), ('contracts', snapshot['input']['extra'])]:
                base = Path(snapshot[area]).resolve()
                if p.is_relative_to(base) and p.relative_to(base).as_posix() in files:
                    return True
    return False


def native_final(raw, request, checkpoint_ordinal=None, snapshot=None):
    require(snapshot is not None, 'full snapshot required for native source authentication')
    events = [json.loads(line) for line in raw.decode('utf-8').splitlines() if line.strip()]
    meta = next((e['payload'] for e in events if e['type'] == 'session_meta'), {})
    source = meta.get('source', {})
    spawn = source.get('subagent', {}).get('thread_spawn', {}) if isinstance(source, dict) else {}
    require(spawn.get('parent_thread_id') == request['parent_thread'] and
            spawn.get('agent_path') == request['agent_path'] and spawn.get('depth') == 1,
            'native parent/agent/depth mismatch')
    contexts = [e['payload'] for e in events if e['type'] == 'turn_context']
    require(contexts, 'native turn context missing')
    matches = []
    for i, e in enumerate(events):
        p = e['payload']
        if e['type'] != 'response_item' or p.get('type') != 'message' or p.get('role') != 'assistant' or p.get('phase') != 'final_answer':
            continue
        text = '\n'.join(c['text'] for c in p['content'] if c.get('type') == 'output_text')
        try: report = json_report(text)
        except (ValueError, TypeError): continue
        if report.get('request_id') != request['request_id']: continue
        if checkpoint_ordinal is None and report.get('kind') == 'checkpoint': continue
        if checkpoint_ordinal is not None and (report.get('kind') != 'checkpoint' or
                                               report.get('ordinal') != checkpoint_ordinal): continue
        start = max(j for j, x in enumerate(events[:i]) if x['type'] == 'turn_context')
        current_context = events[start]['payload']
        turn_id = current_context.get('turn_id')
        require(turn_id, 'native turn identity missing')
        turn_start = next(j for j, x in enumerate(events[:i]) if x['type'] == 'turn_context'
                          and x['payload'].get('turn_id') == turn_id)
        timestamp = events[turn_start].get('timestamp')
        require(timestamp and datetime.fromisoformat(timestamp.replace('Z', '+00:00')) >=
                datetime.fromisoformat(request['created_utc']), 'native turn predates request dispatch boundary')
        require(current_context.get('model') == request['assessment']['model'] and
                current_context.get('effort') == request['assessment']['effort'],
                'native configured model/effort mismatch')
        end = next((j for j in range(i+1, len(events)) if events[j]['type'] == 'turn_context' or
                    (events[j]['type'] == 'event_msg' and events[j]['payload'].get('type') == 'task_started')), len(events))
        tails = events[i+1:end]
        completion = next((x['payload'] for x in tails if x['type'] == 'event_msg' and
                           x['payload'].get('type') == 'task_complete' and
                           x['payload'].get('turn_id') == turn_id), None)
        require(completion and request['request_id'] in completion.get('last_agent_message', ''),
                'native final has no matching completed turn')
        unit = [x['payload'] for x in events[turn_start:i] if x['type'] == 'event_msg' and
                x['payload'].get('type') == 'item_completed' and x['payload'].get('turn_id') == turn_id]
        require(any(x.get('item', {}).get('type') == 'AgentMessage' and
                    x['item'].get('id') == p.get('id') for x in unit), 'final message belongs to another turn')
        require(any(successful_source_read(x.get('item', {}), snapshot) for x in unit),
                'no successful same-turn frozen source read')
        matches.append((text, report, completion, current_context))
    require(len(matches) == 1, 'native report missing or replayed')
    text, report, complete, c = matches[0]
    return (text, report, complete), {'session_id': meta['id'], 'spawn': spawn,
                       'observed_config': [{'model': c['model'], 'effort': c['effort']}]}


def validate_report(report, request, prior, snapshot=None):
    require(all(report.get(k) == request[k] for k in ('request_id', 'stage', 'snapshot_hash')),
            'report request/stage/snapshot mismatch')
    require(report.get('verdict') in {'pass', 'blocked'}, 'invalid verdict')
    require(isinstance(report.get('coverage'), dict) and report['coverage'] and
            isinstance(report.get('read_paths'), list) and report['read_paths'], 'missing review coverage/read paths')
    findings = report.get('findings'); unknowns = report.get('unknowns')
    require(isinstance(findings, list) and isinstance(unknowns, list), 'missing findings/unknowns')
    seen = set()
    for f in findings:
        require(f.get('id') and f['id'] not in seen and f.get('severity') in SEVERITIES
                and f.get('obligation') in {'plan', 'implementation'} and
                f.get('status') in {'open', 'closed'}, 'invalid/duplicate finding')
        seen.add(f['id'])
        require(all(f.get(k) for k in ('root_cause', 'counterexample', 'paths', 'repair_steps', 'tests'))
                and isinstance(f.get('closure_evidence'), list), 'incomplete finding')
        require(f['status'] != 'closed' or f['closure_evidence'], 'closed finding has no evidence')
        require(not (request['stage'] == 'plan' and f['obligation'] == 'implementation'
                     and f['status'] == 'closed'), 'plan cannot close new implementation finding')
    current = {f['id']: f for f in findings}
    dispositions = report.get('prior_dispositions')
    require(isinstance(dispositions, list), 'missing prior dispositions')
    by_key = {x.get('key'): x for x in dispositions}
    require(len(by_key) == len(dispositions) and set(by_key) == set(prior['findings']) | set(prior['unknowns']),
            'prior finding/unknown omitted, duplicated or fabricated')
    for key, original in prior['findings'].items():
        o = original['original']; d = by_key[key]
        require(d.get('id') == o['id'] and d.get('severity') in SEVERITIES and
                SEVERITIES[d['severity']] >= SEVERITIES[o['severity']] and
                d.get('obligation') == o['obligation'], 'original grade/identity/obligation changed')
        require(d.get('disposition') in {'retained_open', 'repair_planned', 'resolved_with_evidence', 'boundary_retained'}
                and d.get('reason'), 'incomplete original disposition')
        if d['disposition'] == 'resolved_with_evidence':
            require(d.get('evidence'), 'original finding closed without evidence')
            require(not (request['stage'] == 'plan' and o['obligation'] == 'implementation'),
                    'plan cannot close implementation obligation')
        if d['disposition'] == 'boundary_retained':
            require(d.get('boundary_contract') and d.get('production_gates_closed') is True,
                    'boundary lacks original contract and closed gates')
        if o['id'] in current:
            f = current[o['id']]
            require(SEVERITIES[f['severity']] >= SEVERITIES[o['severity']] and
                    f['obligation'] == o['obligation'], 'report downgraded original finding')
            require((f['status'] == 'closed') == (d['disposition'] == 'resolved_with_evidence'),
                    'report and original disposition disagree')
    for key, original in prior['unknowns'].items():
        d = by_key[key]
        require(d.get('original') == original['original'] and d.get('reason'), 'original unknown text changed')
        require(d.get('disposition') in {'retained_open', 'resolved_with_evidence', 'boundary_retained'},
                'invalid unknown disposition')
        if d['disposition'] == 'resolved_with_evidence': require(d.get('evidence'), 'unknown closed without evidence')
        if d['disposition'] == 'boundary_retained':
            require(d.get('boundary_contract') and d.get('production_gates_closed') is True,
                    'unknown boundary lacks contract/closed gates')
    if report['verdict'] == 'pass':
        require(not unknowns, 'pass has unresolved current unknowns; limitations belong in coverage')
        require(not any(f['severity'] != 'suggestion' and f['status'] == 'open' and
                        (request['stage'] == 'implementation' or f['obligation'] == 'plan') for f in findings),
                'pass has open current-stage important findings')
        for key, original in prior['findings'].items():
            o = original['original']; d = by_key[key]
            if o['severity'] != 'suggestion' and (request['stage'] == 'implementation' or o['obligation'] == 'plan'):
                require(d['disposition'] in {'resolved_with_evidence', 'boundary_retained'},
                        'prior important current-stage obligation remains open')
        require(all(by_key[k]['disposition'] != 'retained_open' for k in prior['unknowns']),
                'original unknown unresolved')
    if snapshot is not None:
        import re
        allowed = set(snapshot['input']['files']) | set(snapshot['input']['extra'])
        def exists(ref):
            require(isinstance(ref, str), 'evidence/read path is not a string')
            name = re.sub(r':\d+(?:[-–]\d+)?$', '', ref).replace('\\', '/')
            require(name in allowed, 'evidence/read path outside complete snapshot: ' + ref)
        for ref in report['read_paths']: exists(ref)
        for f in findings:
            for ref in f['paths'] + f['closure_evidence']: exists(ref)
        for d in dispositions:
            for ref in d.get('evidence', []): exists(ref)
    return report


@storage.operation('native-capture', request=True)
def capture(out, rollout):
    out = Path(out); request, snapshot = verify_input(out)
    raw = regular(Path(rollout))
    journal = out / 'capture-source.json'
    if journal.is_file():
        bound = load(journal)
        require(len(raw) >= bound['rollout_bytes'] and sha(raw[:bound['rollout_bytes']]) == bound['rollout_sha256'],
                'conflicting original capture source')
        raw = raw[:bound['rollout_bytes']]
    (text, report, completion), identity = native_final(raw, request, snapshot=snapshot)
    require(report.get('kind') != 'checkpoint', 'checkpoint cannot become final review receipt')
    # Preserve invalid semantic reports too, but never publish a valid receipt for them.
    binding = bound if journal.is_file() else {'request_sha256': sha(regular(out/'request.json')),
                 'rollout_sha256': sha(raw), 'rollout_bytes': len(raw), 'rollout_source_path': str(Path(rollout).resolve())}
    require(binding['request_sha256'] == sha(regular(out/'request.json')), 'capture request binding drift')
    atomic_same(journal, encode(binding))
    persist_same(out / 'native-rollout.jsonl', raw, allow_prefix=True)
    persist_same(out / 'raw-final.txt', text.encode('utf-8'), allow_prefix=True)
    persist_same(out / 'report.json', encode(report), allow_prefix=True)
    final_prior, checkpoint_hashes = checkpoint_obligations(out, request, snapshot)
    validate_report(report, request, final_prior, snapshot)
    if request['stage'] == 'implementation' and report['verdict'] == 'pass':
        config = load(relative(Path(request['source_root']), request['config_path']))
        implementation_proofs(Path(request['source_root']),
                              load(relative(Path(request['source_root']), config['manifest'])), report)
    persist_same(out / 'receipt.json', encode({'request_sha256': sha((out / 'request.json').read_bytes()),
            'snapshot_hash': request['snapshot_hash'], 'rollout_sha256': sha(raw),
            'raw_final_sha256': sha(text.encode('utf-8')), 'report_sha256': sha(encode(report)),
            'native_identity': identity, 'turn_id': completion['turn_id'],
            'checkpoint_hashes': checkpoint_hashes,
            'permission_scope': 'declared batch only; no production authorization'}), allow_prefix=True)
    return {'request_id': request['request_id'], 'verdict': report['verdict']}


def checkpoint_chain(out, ordinal, request, snapshot):
    out = Path(out); values = []; previous_hash = None; grades = {}
    for index in range(1, ordinal + 1):
        path = out/'checkpoints'/f'{index}.json'
        require(path.is_file(), 'checkpoint chain missing saved unit')
        saved = load(path)
        raw = regular(out/'checkpoints'/f'{index}-native-rollout.jsonl')
        (_, unit, complete), identity = native_final(raw, request, index, snapshot)
        require(unit == saved['checkpoint'] and sha(raw) == saved['rollout_sha256']
                and identity == saved['native_identity'] and complete['turn_id'] == saved['turn_id']
                and unit.get('parent_checkpoint_sha256') == previous_hash,
                'checkpoint chain/source drift')
        for f in unit['findings']:
            require(f['status'] == 'open', 'checkpoint cannot close findings')
            old = grades.get(f['id'])
            require(not old or (SEVERITIES[f['severity']] >= SEVERITIES[old['severity']]
                    and f['obligation'] == old['obligation']), 'checkpoint downgraded or reclassified finding')
            grades[f['id']] = f
        previous_hash = sha(path.read_bytes()); values.append((path, saved))
    return values


def checkpoint_obligations(out, request, snapshot, ordinal=None):
    """Retain saved units for both same-agent finals and new-context recovery."""
    out = Path(out).resolve(); prior = load(out/'prior.json')
    base = out/'checkpoints'
    ordinals = sorted(int(p.stem) for p in base.glob('*.json') if p.stem.isdigit())
    bindings = {int(p.name.split('-')[0]) for p in base.glob('*-source.json')}
    if ordinal is not None:
        ordinals = [value for value in ordinals if value <= ordinal]
        bindings = {value for value in bindings if value <= ordinal}
    require(bindings <= set(ordinals), 'checkpoint source bound but completed unit missing')
    chain = checkpoint_chain(out, max(ordinals), request, snapshot) if ordinals else []
    require(ordinals == list(range(1, len(chain)+1)), 'checkpoint completed-unit gap')
    checkpoint_hashes = {}
    for path, entry in chain:
        checkpoint_hashes[str(entry['checkpoint']['ordinal'])] = sha(path.read_bytes())
        for finding in entry['checkpoint']['findings']:
            key = sha(encode(finding))
            prior['findings'][key] = {'key': key, 'original': finding,
                                      'source': str(path), 'checkpoint_hash': sha(path.read_bytes())}
        for text in entry['checkpoint']['unknowns']:
            key = sha(text.encode('utf-8'))
            prior['unknowns'][key] = {'key': key, 'original': text, 'source': str(path)}
    return prior, checkpoint_hashes


@storage.operation('native-checkpoint', request=True)
def checkpoint(out, rollout, ordinal=1):
    """Persist a completed reading unit; never publish a permit or final receipt."""
    out = Path(out); q, snapshot = verify_input(out)
    raw = regular(Path(rollout)); base = out / 'checkpoints'
    require(not (out/'receipt.json').exists() or (base/(str(ordinal)+'.json')).is_file(),
            'cannot add checkpoint after final review receipt')
    source_binding = base / (str(ordinal) + '-source.json')
    bound = load(source_binding) if source_binding.is_file() else None
    if bound is not None:
        require(bound['request_sha256'] == sha(regular(out/'request.json')) and
                bound.get('ordinal', ordinal) == ordinal, 'checkpoint request/ordinal binding drift')
        require(len(raw) >= bound['rollout_bytes'] and
                sha(raw[:bound['rollout_bytes']]) == bound['rollout_sha256'],
                'conflicting checkpoint original source')
        raw = raw[:bound['rollout_bytes']]
    (text, value, complete), identity = native_final(raw, q, ordinal, snapshot)
    require(bound is None or bound['checkpoint_sha256'] == sha(encode(value)),
            'checkpoint object binding drift')
    require(value.get('kind') == 'checkpoint' and value.get('snapshot_hash') == q['snapshot_hash'],
            'checkpoint snapshot/kind mismatch')
    require(type(value.get('ordinal')) is int and value['ordinal'] > 0 and
            all(k in value for k in ('findings', 'unknowns', 'coverage', 'read_paths', 'remaining_queue')),
            'checkpoint content incomplete')
    ordinal = value['ordinal']; base = out / 'checkpoints'; dest = base / (str(ordinal) + '.json')
    previous_units = checkpoint_chain(out, ordinal - 1, q, snapshot) if ordinal > 1 else []
    require(all(f.get('status') == 'open' for f in value['findings']), 'checkpoint cannot close findings')
    previous_findings = [x['original'] for x in load(out/'prior.json')['findings'].values()]
    previous_findings += [f for _, entry in previous_units for f in entry['checkpoint']['findings']]
    for f in value['findings']:
        for old in previous_findings:
            if f['id'] == old['id']:
                require(SEVERITIES.get(f.get('severity'), -1) >= SEVERITIES[old['severity']]
                        and f.get('obligation') == old['obligation'], 'checkpoint downgraded or reclassified finding')
    validate_report(dict(value, stage=q['stage'], verdict='blocked', prior_dispositions=[]), q,
                    {'findings': {}, 'unknowns': {}, 'sources': {}}, snapshot)
    previous = base / (str(ordinal - 1) + '.json')
    expected = sha(previous.read_bytes()) if ordinal > 1 and previous.is_file() else None
    require((ordinal == 1 or previous.is_file()) and value.get('parent_checkpoint_sha256') == expected,
            'checkpoint chain missing or mismatched')
    evidence = {'checkpoint': value, 'native_identity': identity, 'turn_id': complete['turn_id'],
                'rollout_sha256': sha(raw), 'raw_final_sha256': sha(text.encode('utf-8'))}
    if dest.exists():
        require(load(dest) == evidence, 'conflicting checkpoint cannot overwrite')
        prior, _ = checkpoint_obligations(out, q, snapshot, ordinal)
        persist_same(base/(str(ordinal)+'-prior.json'), encode(prior), allow_prefix=True)
        return {'checkpoint': str(dest), 'obligations': str(base/(str(ordinal)+'-prior.json')),
                'permission': False, 'idempotent': True}
    base.mkdir(parents=True, exist_ok=True)
    binding = bound if bound is not None else {'request_sha256': sha(regular(out/'request.json')),
        'ordinal': ordinal, 'rollout_sha256': sha(raw), 'rollout_bytes': len(raw),
        'checkpoint_sha256': sha(encode(value))}
    atomic_same(source_binding, encode(binding))
    persist_same(base / (str(ordinal) + '-native-rollout.jsonl'), raw, allow_prefix=True)
    atomic_same(dest, encode(evidence))
    checkpoint_chain(out, ordinal, q, snapshot)
    prior, _ = checkpoint_obligations(out, q, snapshot, ordinal)
    persist_same(base/(str(ordinal)+'-prior.json'), encode(prior), allow_prefix=True)
    return {'checkpoint': str(dest), 'obligations': str(base/(str(ordinal)+'-prior.json')), 'permission': False}


@storage.operation('native-resume', request=True)
def resume(out, agent_path, status_evidence, ordinal):
    """New independent context resumes saved work, never unreturned reasoning.

Status evidence is the host's saved native list_agents observation. It is an
executor attestation, not a cryptographic service signature. The old completed
unit and full input are independently checked below; no model is launched here.
"""
    out = Path(out); q, snapshot = verify_input(out, current=True)
    status = load(Path(status_evidence))
    require(status.get('source') == 'collaboration.list_agents' and
            status.get('parent_thread') == q['parent_thread'], 'missing native status observation')
    observed = datetime.fromisoformat(status['observed_utc'])
    require(0 <= (datetime.now(timezone.utc) - observed).total_seconds() <= 60,
            'native status observation stale')
    row = next((x for x in status.get('agents', []) if x.get('agent_name') == q['agent_path']), None)
    observed_status = row.get('agent_status') if row else None
    if isinstance(observed_status, dict) and len(observed_status) == 1:
        observed_status = next(iter(observed_status))
    require(row and observed_status in {'completed', 'idle', 'interrupted'},
            'old native agent still live or state unknown')
    chain = checkpoint_chain(out, ordinal, q, snapshot)
    cp, saved = chain[-1]; unit = saved['checkpoint']
    root = Path(q['source_root']); config = load(relative(root, q['config_path']))
    state = relative(root, config['state']); rid = uuid.uuid4().hex
    dest = state / 'requests' / rid; dest.mkdir(parents=True, exist_ok=False)
    prior = load(out / 'prior.json')
    for path, entry in chain:
        for f in entry['checkpoint']['findings']:
            key = sha(encode(f)); prior['findings'][key] = {'key': key, 'original': f,
                                                   'source': str(path), 'checkpoint_hash': sha(path.read_bytes())}
        for text in entry['checkpoint']['unknowns']:
            require(isinstance(text, str) and text.strip(), 'checkpoint unknown invalid')
            key = sha(text.encode('utf-8')); prior['unknowns'][key] = {'key': key, 'original': text, 'source': str(path)}
    new = dict(q, request_id=rid, agent_path=agent_path, prior_hash=sha(encode(prior)),
               created_utc=datetime.now(timezone.utc).isoformat(),
               resumed_from={'request_sha256': sha((out / 'request.json').read_bytes()),
                             'checkpoint_sha256': sha(cp.read_bytes()), 'previous_request': str(out),
                             'remaining_queue': unit['remaining_queue'],
                             'status_observation_sha256': sha(regular(Path(status_evidence)))})
    publish(dest / 'request.json', new); publish(dest / 'snapshot.json', snapshot); publish(dest / 'prior.json', prior)
    publish(dest / 'resumed-checkpoint.json', saved); publish(dest / 'agent-status.json', status)
    publish(dest / 'resumed-chain.json', [{'path': str(path), 'sha256': sha(path.read_bytes()), 'saved': entry}
                                         for path, entry in chain])
    publish(state / 'latest' / (q['stage'] + '-' + rid + '.json'),
            {'request': str(dest.relative_to(root)), 'created_utc': new['created_utc']})
    return {'request': str(dest), 'request_id': rid, 'snapshot_hash': q['snapshot_hash'],
            'permission': False, 'remaining_queue': unit['remaining_queue']}


def receipt(out, current=False):
    out = Path(out); q, snapshot = verify_input(out, current)
    r = load(out / 'receipt.json')
    for name, key in [('request.json', 'request_sha256'), ('native-rollout.jsonl', 'rollout_sha256'),
                      ('raw-final.txt', 'raw_final_sha256'), ('report.json', 'report_sha256')]:
        require(sha((out / name).read_bytes()) == r[key], 'native receipt artifact drift')
    (_, original, complete), identity = native_final((out / 'native-rollout.jsonl').read_bytes(), q, snapshot=snapshot)
    require(original == load(out / 'report.json') and identity == r['native_identity']
            and complete['turn_id'] == r['turn_id'], 'native final/receipt mismatch')
    final_prior, checkpoint_hashes = checkpoint_obligations(out, q, snapshot)
    require(r.get('checkpoint_hashes', {}) == checkpoint_hashes, 'native receipt checkpoint chain drift')
    report = validate_report(original, q, final_prior, snapshot)
    if q['stage'] == 'implementation' and report['verdict'] == 'pass':
        config = load(relative(Path(q['source_root']), q['config_path']))
        implementation_proofs(Path(q['source_root']), load(relative(Path(q['source_root']), config['manifest'])), report)
    return q, report


def latest(root, config, stage):
    state = relative(Path(root), config['state'])
    candidates = [load(p) for p in (state / 'latest').glob(stage + '-*.json')]
    require(candidates, 'missing native ' + stage + ' request')
    selected = max(candidates, key=lambda x: x['created_utc'])
    return relative(Path(root), selected['request'])


def permit(root, manifest):
    value = gate(root, manifest, 'create-permit')
    config = load(relative(Path(root), manifest['native_review']))
    request = latest(root, config, 'plan')
    q, report = receipt(request)
    value.update(kind='repair-only' if any(f['status'] == 'open' and f['severity'] != 'suggestion'
                                        for f in report['findings']) else 'implementation',
                 plan_request=q['request_id'], plan_receipt_hash=sha((request / 'receipt.json').read_bytes()))
    dest = request / 'permit.json'
    if dest.exists(): require(load(dest) == value, 'native permit cannot be overwritten')
    else: publish(dest, value)
    return value


def validate_scope_extension(root, manifest, original_sources):
    """Allow planned new files without rewriting the original opening."""
    config = load(relative(Path(root), manifest['native_review']))
    plan = latest(root, config, 'plan'); request, report = receipt(plan)
    require(report['verdict'] == 'pass', 'scope extension has no independent approved plan')
    extension = manifest.get('native_scope_extension', {})
    require(extension.get('plan_request') == request['request_id'] and
            extension.get('plan_receipt_sha256') == sha(regular(plan/'receipt.json')),
            'scope extension not bound to approved plan')
    old = set(original_sources); current = set(manifest['sources']); added = current - old
    require(old <= current and set(extension.get('paths', [])) == added and
            added <= set(config['write_paths']) and
            all(regular(relative(Path(root), name)) is not None for name in added),
            'scope extension removed original or added unapproved source')
    return True


def gate(root, manifest, stage):
    root = Path(root).resolve(); config = load(relative(root, manifest['native_review']))
    require(config['batch'] == manifest['batch'], 'native batch mismatch')
    p = latest(root, config, 'plan'); q, report = receipt(p, current=False)
    require(q['source_root'] == str(root) and q['batch'] == config['batch'] and
            q['config_path'] == manifest['native_review'], 'native plan belongs to another root/batch/config')
    require(report['verdict'] == 'pass' and q['config_hash'] == sha(regular(relative(root, manifest['native_review']))),
            'no valid native plan permission/config drift')
    require(q.get('legacy_binding') == legacy_binding(root, config), 'original batch lineage changed')
    plan_snapshot = load(p / 'snapshot.json')
    stable = [config['plan'], config['owner_policy'], *config.get('extra_files', []), *config.get('prior_files', [])]
    require(all(ref in plan_snapshot['input']['extra'] and
                sha(regular(relative(root, ref))) == plan_snapshot['input']['extra'][ref] for ref in stable),
            'approved plan/policy/history contract drift')
    now = inventory(root); before = plan_snapshot['input']['files']
    changed = {name for name in set(now) | set(before) if now.get(name) != before.get(name)}
    require(changed <= set(config['write_paths']), 'implementation changed unapproved project paths')
    current_git = git_identity(root)
    require(current_git['head'] == plan_snapshot['input']['git']['head'], 'plan HEAD drift')
    require(current_git['branch'] == plan_snapshot['input']['git']['branch'], 'plan branch drift')
    def unapproved_diffs(identity):
        return [row for row in identity['source_diffs']
                if not set(row['paths']) <= set(config['write_paths'])]
    require(unapproved_diffs(current_git) == unapproved_diffs(plan_snapshot['input']['git']),
            'plan unapproved Git input drift')
    if stage != 'create-permit':
        require((p / 'permit.json').is_file(), 'explicit native implementation permit missing')
        saved = load(p / 'permit.json')
        require(saved.get('plan_request') == q['request_id'] and
                saved.get('plan_receipt_hash') == sha((p / 'receipt.json').read_bytes()) and
                saved.get('production_authorization') is False, 'native implementation permit drift')
    if stage == 'closeout':
        approved = {'request_id': q['request_id'], 'receipt_sha256': sha(regular(p/'receipt.json')),
                    'permit_sha256': sha(regular(p/'permit.json')),
                    'manifest_sha256': sha(regular(relative(root, config['manifest'])))}
        p = latest(root, config, 'implementation'); q, report = receipt(p, current=True)
        require(q.get('approved_plan') == approved, 'implementation belongs to an older plan/permit/manifest')
        require(q['source_root'] == str(root) and q['batch'] == config['batch'] and
                q['config_path'] == manifest['native_review'] and
                q['config_hash'] == sha(regular(relative(root, manifest['native_review']))),
                'native implementation root/batch/config drift')
        require(report['verdict'] == 'pass', 'native implementation review blocked')
    if stage in {'review', 'closeout'}:
        from execution_evidence import validate_manifest
        validate_manifest(root, manifest)
    return {'mechanical_status': 'ok', 'channel': 'native-agent', 'production_authorization': False}


def main():
    p = argparse.ArgumentParser(description=__doc__); s = p.add_subparsers(dest='command', required=True)
    a = s.add_parser('prepare'); a.add_argument('--root', required=True); a.add_argument('--config', required=True)
    a.add_argument('--stage', choices=['plan', 'implementation'], required=True)
    a.add_argument('--assessment', required=True); a.add_argument('--agent-path', required=True)
    a = s.add_parser('capture'); a.add_argument('--request', required=True); a.add_argument('--rollout', required=True)
    a = s.add_parser('checkpoint'); a.add_argument('--request', required=True); a.add_argument('--rollout', required=True)
    a.add_argument('--ordinal', type=int, required=True)
    a = s.add_parser('resume'); a.add_argument('--request', required=True); a.add_argument('--agent-path', required=True)
    a.add_argument('--status-evidence', required=True); a.add_argument('--ordinal', type=int, required=True)
    a = s.add_parser('validate'); a.add_argument('--request', required=True); a.add_argument('--current', action='store_true')
    a = p.parse_args()
    from workflow import EvidenceError
    try:
        if a.command == 'prepare': value = prepare(a.root, a.config, a.stage, a.assessment, a.agent_path)
        elif a.command == 'capture': value = capture(a.request, a.rollout)
        elif a.command == 'checkpoint': value = checkpoint(a.request, a.rollout, a.ordinal)
        elif a.command == 'resume': value = resume(a.request, a.agent_path, a.status_evidence, a.ordinal)
        else:
            q, r = receipt(a.request, a.current); value = {'request_id': q['request_id'], 'verdict': r['verdict']}
        print(json.dumps(value, ensure_ascii=False, indent=2)); return 0
    except (Blocked, EvidenceError, OSError, ValueError, KeyError, TypeError) as e:
        print(json.dumps({'blocked': str(e)}, ensure_ascii=False)); return 2


if __name__ == '__main__':
    raise SystemExit(main())
