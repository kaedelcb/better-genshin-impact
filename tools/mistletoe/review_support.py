"""Shared fail-closed I/O for review/execution evidence; no product behavior."""
from __future__ import annotations
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import uuid
from contextlib import contextmanager

class Blocked(ValueError):
    pass

def require(ok, message):
    if not ok:
        raise Blocked(message)

def sha(data):
    return hashlib.sha256(data).hexdigest()

def encode(value):
    return (json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + '\n').encode('utf-8')

def load(path):
    def pairs(items):
        result = {}
        for k, v in items:
            require(k not in result, 'duplicate JSON key: ' + k)
            result[k] = v
        return result
    return json.loads(Path(path).read_text(encoding='utf-8-sig'), object_pairs_hook=pairs)

DENIED = {'.git', '.kiro', 'user', 'bin', 'obj', 'node_modules', '__pycache__',
          '.codex', '.env', 'auth.json', 'credentials.json', 'secrets.json'}
SECRET = re.compile(rb'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|\bsk-[A-Za-z0-9_-]{24,}|\bgh[pousr]_[A-Za-z0-9]{30,}')

def path(root, rel, *, protected=True):
    require(isinstance(rel, str) and rel and '\\' not in rel, 'use nonempty POSIX relative paths')
    p = Path(rel)
    require(not p.is_absolute() and '..' not in p.parts and ':' not in rel, 'unsafe relative path')
    root = Path(root).resolve()
    current = root
    for part in p.parts:
        require(not protected or (part.casefold() not in DENIED and not part.casefold().startswith('.env.')),
                'protected path: ' + rel)
        current = current / part
        if current.exists() or current.is_symlink():
            attrs = current.lstat()
            require(not stat.S_ISLNK(attrs.st_mode) and not getattr(attrs, 'st_file_attributes', 0) & 1024,
                    'link/reparse path: ' + rel)
    require(current.resolve().is_relative_to(root), 'path escapes root')
    return current

def safe_bytes(root, rel):
    p = path(root, rel)
    require(p.is_file() and stat.S_ISREG(p.stat().st_mode), 'missing/irregular input: ' + rel)
    require(p.stat().st_size <= 16 * 1024 * 1024, 'oversize input: ' + rel)
    b = p.read_bytes()
    b.decode('utf-8-sig')
    require(not SECRET.search(b), 'potential credential in input: ' + rel)
    return b

def collect(root, roots, files=(), excluded=()):
    require(isinstance(roots, list) and isinstance(files, (list, tuple)), 'invalid capture scope')
    result = {}
    exclusions = [path(root, p) for p in excluded]
    for rel in roots:
        base = path(root, rel)
        require(base.is_dir(), 'missing source directory: ' + rel)
        require(not any(e.is_relative_to(base) for e in exclusions), 'output nested in input directory')
        for directory, dirs, names in os.walk(base, followlinks=False):
            for name in list(dirs):
                candidate = Path(directory) / name
                candidate_rel = candidate.relative_to(root).as_posix()
                # Generated/protected directories are never copied, links always rejected.
                path(root, candidate_rel, protected=False)
                if name.casefold() in DENIED:
                    dirs.remove(name)
            for name in names:
                p = (Path(directory) / name).relative_to(root).as_posix()
                result[p] = safe_bytes(root, p)
    for rel in files:
        require(not any(path(root, rel).is_relative_to(e) for e in exclusions), 'output used as input')
        result[rel] = safe_bytes(root, rel)
    require(result, 'empty input scope')
    return dict(sorted(result.items()))

def hashes(files):
    return {p: sha(b) for p, b in files.items()}

def publish(p, value):
    """Exclusive, fsynced file. Partial crash records are deliberately not accepted."""
    p = Path(p)
    p.parent.mkdir(parents=True, exist_ok=True)
    with p.open('xb') as f:
        f.write(encode(value))
        f.flush()
        os.fsync(f.fileno())

def git(root, *args):
    return subprocess.check_output(['git', '-C', str(root), *args])

GIT_PATHSPEC_ARG_LIMIT = 4096

def _git_change_records(root, options):
    raw = git(root, 'diff', *options, '--no-ext-diff', '--no-textconv', '--name-status', '-z', '--')
    fields = raw.decode('utf-8', errors='strict').split('\0')
    records, index = [], 0
    while index < len(fields) and fields[index]:
        status = fields[index]
        index += 1
        count = 2 if status.startswith(('R', 'C')) else 1
        paths = tuple(fields[index:index + count])
        require(len(paths) == count and all(paths), 'malformed Git name-status output')
        records.append((status, paths))
        index += count
    require(index == len(fields) - 1, 'malformed Git name-status terminator')
    return records

def _scope_matches(root, rel, scopes):
    if not scopes:
        return True
    for scope in scopes:
        normalized = Path(scope).as_posix().rstrip('/')
        if normalized in ('', '.') or rel == normalized:
            return True
        if rel.startswith(normalized + '/'):
            return True
    return False

def _git_change_patch(root, options, record):
    pathspecs = [':(literal)' + rel for rel in record[1]]
    argument_size = sum(len(item) + 1 for item in pathspecs)
    require(argument_size <= GIT_PATHSPEC_ARG_LIMIT, 'Git change pathspec exceeds safe argument budget')
    patch = git(root, 'diff', *options, '--no-ext-diff', '--no-textconv', '--', *pathspecs)
    require(patch, 'Git name-status entry has no reproducible diff')
    return patch

def git_identity(root, scopes):
    scopes = list(dict.fromkeys(scopes))
    result = {k: git(root, *args).decode('utf-8', errors='strict') for k, args in {
        'head': ('rev-parse', 'HEAD'), 'branch': ('branch', '--show-current'),
        'status': ('status', '--porcelain=v1'),
    }.items()}
    for key, options in (('unstaged', []), ('staged', ['--cached'])):
        records = []
        for record in _git_change_records(root, options):
            status, changed_paths = record
            matches = [_scope_matches(root, rel, scopes) for rel in changed_paths]
            selected = None
            if status.startswith('R') and len(changed_paths) == 2:
                if all(matches):
                    selected = record
                elif matches[0]:
                    selected = ('D', (changed_paths[0],))
                elif matches[1]:
                    selected = ('A', (changed_paths[1],))
            elif status.startswith('C') and len(changed_paths) == 2:
                if all(matches):
                    selected = record
                elif matches[1]:
                    selected = ('A', (changed_paths[1],))
            elif any(matches):
                selected = record
            if selected is not None:
                for rel in selected[1]:
                    path(root, rel)  # Reject deleted secret/protected paths before capturing old bytes.
                records.append(selected)
        result[key] = b''.join(_git_change_patch(root, options, record) for record in records).decode('utf-8', errors='strict')
    require(not SECRET.search(encode(result)), 'potential credential in Git evidence')
    return result

@contextmanager
def lock(directory):
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    p = directory / 'writer.lock'
    require(not (directory / 'recovery-required.json').exists(), 'process recovery required before writes')
    try:
        publish(p, {'pid': os.getpid(), 'nonce': uuid.uuid4().hex,
                    'recovery': 'Do not remove until process creation identity and descendants are checked; retain evidence.'})
    except FileExistsError:
        raise Blocked('batch locked; no automatic stale-lock takeover')
    try:
        yield
    finally:
        if not (directory / 'recovery-required.json').exists() and not (directory / 'inflight.json').exists():
            p.unlink()  # Only our lock. Crash/uncertain descendants remain fail-closed.

BUNDLE_FILES = ('review_support.py', 'review_process.py', 'native_review.py', 'serena_read.py',
                'skills/mistletoe-independent-review/SKILL.md', 'execution_evidence.py',
                'snapshot_reader.py', 'process_runner.py', 'workflow.py', 'run_suite.py', 'templates/review-plan.json', 'legacy-openings.json',
                'README.md', '../../Docs/design/mistletoe-review-process.md',
                '../../Docs/design/mistletoe-workflow-facilities.md')

def verify_bundle():
    base = Path(__file__).resolve().parent
    doc = load(base / 'review-bundle.json')
    require(doc.get('version') == 3 and set(doc.get('files', {})) == set(BUNDLE_FILES), 'invalid v3 bundle')
    require(all(bundle_file_hash((base / p).read_bytes()) == h for p, h in doc['files'].items()), 'review bundle drift')
    return sha(encode(doc))

def bundle_file_hash(content):
    return sha(content.replace(b'\r\n', b'\n'))
