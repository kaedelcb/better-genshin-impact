"""Explicit generated-output reclamation; callers must hold the storage-domain lock."""
from pathlib import Path
import hashlib
import json
import os
import subprocess
import time
import uuid
from review_support import require, encode

BINARY_SUFFIXES = {'.dll', '.pdb', '.exe'}
PROTECTED = {'user', 'data', 'userdata', '.git', '.kiro', '.storage-objects',
             'input-snapshot', 'source-before', 'snapshot', 'snapshots', 'node_modules'}


def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()


def output_path(root, path):
    import storage_limits as storage
    root = Path(root).resolve()
    require('..' not in Path(path).parts, 'cleanup path traversal forbidden')
    path = storage.regular_tree(Path(path).absolute())
    require(path.is_relative_to(root / 'Build'), 'cleanup output must be inside repository Build')
    parts = path.relative_to(root / 'Build').parts
    require(len(parts) >= 2 and not any(p.lower() in PROTECTED for p in parts),
            'cleanup requires a specific generated output, not a task or protected directory')
    return path


def save(control, value):
    import storage_limits as storage
    directory = storage.regular_tree(control / 'reclamation')
    directory.mkdir(exist_ok=True)
    target = storage.regular_tree(directory / (value['id'] + '.json'))
    tmp = directory / (uuid.uuid4().hex + '.tmp')
    with tmp.open('xb') as f:
        f.write(encode(value)); f.flush(); os.fsync(f.fileno())
    os.replace(tmp, target)


def tracked(root):
    result = subprocess.run(['git', '-C', str(root), 'ls-files', '-z'], capture_output=True)
    require(result.returncode == 0, 'cleanup requires a readable Git index')
    return {os.path.normcase(str(Path(root) / os.fsdecode(p)))
            for p in result.stdout.split(b'\0') if p}


def prepare(root, control, paths, reason):
    """Freeze exact generated binary identities before removing anything. No directory deletion."""
    import storage_limits as storage
    require(bool(reason.strip()), 'cleanup reason required')
    outputs = [output_path(root, p) for p in paths]
    indexed = tracked(root)
    records = {}
    for output in outputs:
        # Refuse a whole output containing an authenticated execution receipt.
        files = list(storage.files_under(output))
        require(not any(p.name.lower() in {'receipt.json', 'snapshot.json', 'request.json'}
                        for p, _ in files), 'receipt-bound output must remain available')
        for p, st in files:
            if (p.suffix.lower() not in BINARY_SUFFIXES
                    or any(part.lower() in PROTECTED for part in p.relative_to(output).parts)
                    or os.path.normcase(str(p)) in indexed):
                continue
            records[str(p)] = dict(path=str(p), size=st.st_size, sha256=digest(p),
                                   device=st.st_dev, inode=st.st_ino, status='pending')
    value = dict(id=uuid.uuid4().hex, root=str(Path(root).resolve()), reason=reason,
                 created_ns=time.time_ns(), outputs=[str(p) for p in outputs],
                 files=list(records.values()), state='pending', unlinked_bytes=0,
                 released_unique_bytes=0)
    save(control, value)
    return value


def apply(control, value):
    import storage_limits as storage
    root = Path(value['root'])
    require(storage.control_dir(root) == control, 'cleanup domain mismatch')
    indexed = tracked(root)
    outputs = [output_path(root, p) for p in value['outputs']]
    if any(p.name.lower() in {'receipt.json', 'snapshot.json', 'request.json'}
           for output in outputs for p, _ in storage.files_under(output)):
        value['blocked_reason']='output now contains a receipt or published input'
        save(control,value)
        return value
    for record in value['files']:
        if record['status'] != 'pending':
            continue
        require('..' not in Path(record['path']).parts, 'cleanup path traversal forbidden')
        p = storage.regular_tree(Path(record['path']))
        require(any(p.is_relative_to(o) for o in outputs) and p.suffix.lower() in BINARY_SUFFIXES,
                'cleanup manifest path outside approved output')
        require(not any(part.lower() in PROTECTED for part in p.relative_to(root).parts),
                'protected cleanup path')
        if not p.exists():
            record['status'] = 'already-absent'
            continue
        st = p.stat()
        if (os.path.normcase(str(p)) in indexed or st.st_size != record['size']
                or st.st_dev != record['device'] or st.st_ino != record['inode']
                or digest(p) != record['sha256']):
            record['status'] = 'changed-kept'
            continue
        try:
            # Never chmod: a hardlink may share attributes with a retained copy.
            p.unlink()
        except OSError as error:
            record['last_error'] = str(error)
            continue
        record['status'] = 'removed'
        value['unlinked_bytes'] += st.st_size
        if st.st_nlink == 1:
            value['released_unique_bytes'] += st.st_size
    value['state'] = 'pending' if any(f['status'] == 'pending' for f in value['files']) else 'complete'
    save(control, value)
    return value


def pending(root, control):
    """Retry only previously approved, byte-bound files; never discover old data implicitly."""
    import storage_limits as storage
    directory = storage.regular_tree(control / 'reclamation')
    for path in directory.glob('*.json'):
        storage.regular_tree(path)
        value = json.loads(path.read_text(encoding='utf-8'))
        if value.get('state') == 'pending' and value.get('root') == str(Path(root).resolve()):
            apply(control, value)


def recover_outputs(root, control, ledger):
    """Recover terminal sessions interrupted before their cleanup manifest was published."""
    import storage_limits as storage
    for entry in ledger['entries']:
        if (entry.get('state') not in {'retained', 'failed'} or entry.get('reclamation_id')
                or not entry.get('temporary_outputs')):
            continue
        if any((Path(p)/name).exists() for p in entry.get('recovery_directories', [])
               for name in ('inflight.json','recovery-required.json')):
            continue
        # Another worktree shares the ledger; only reclaim this root's outputs here.
        if not all(Path(p).is_relative_to(Path(root)/'Build'/'validation-runs')
                   for p in entry['temporary_outputs']):
            continue
        try:
            plan=prepare(root,control,entry['temporary_outputs'],'recover terminal validation '+entry['id'])
            apply(control,plan)
            entry['reclamation_id']=plan['id']
        except (OSError, storage.Blocked) as error:
            entry['cleanup_pending_reason']=str(error)
