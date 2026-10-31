"""Explicit generated-output reclamation; callers must hold the storage-domain lock."""
from pathlib import Path
import hashlib
import json
import os
import stat
import ctypes
import subprocess
import time
import uuid
from review_support import require, encode

BINARY_SUFFIXES = {'.dll', '.pdb', '.exe'}
RESOURCE_SUFFIXES = {'.resources', '.onnx', '.png', '.webp', '.zip', '.dylib',
                     '.so', '.bin', '.aar', '.mp3', '.jpg', '.jpeg', '.ico'}
PROTECTED = {'user', 'data', 'userdata', '.git', '.kiro', '.storage-objects',
             'input-snapshot', 'source-before', 'snapshot', 'snapshots', 'node_modules'}


def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()


def unlink_readonly_alias(path):
    """Remove one Windows name without changing shared inode attributes/content.

    Uses DELETE | FORCE_IMAGE_SECTION_CHECK | IGNORE_READONLY_ATTRIBUTE, not
    POSIX deletion of an in-use image. Unsupported systems fail without fallback.
    """
    require(os.name == 'nt', 'readonly alias removal requires Windows')
    k=ctypes.WinDLL('kernel32',use_last_error=True)
    k.CreateFileW.argtypes=[ctypes.c_wchar_p,ctypes.c_uint32,ctypes.c_uint32,
                           ctypes.c_void_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p]
    k.CreateFileW.restype=ctypes.c_void_p
    k.SetFileInformationByHandle.argtypes=[ctypes.c_void_p,ctypes.c_int,ctypes.c_void_p,ctypes.c_uint32]
    k.SetFileInformationByHandle.restype=ctypes.c_int
    k.CloseHandle.argtypes=[ctypes.c_void_p]
    handle=k.CreateFileW(str(path),0x10000,7,None,3,0x200000,None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        flags=ctypes.c_uint32(0x15)
        if not k.SetFileInformationByHandle(handle,21,ctypes.byref(flags),ctypes.sizeof(flags)):
            raise ctypes.WinError(ctypes.get_last_error())
    finally:
        k.CloseHandle(handle)


def output_path(root, path):
    import storage_limits as storage
    root = Path(root).resolve()
    require('..' not in Path(path).parts, 'cleanup path traversal forbidden')
    path = storage.regular_tree(Path(path).absolute())
    require(path.is_relative_to(root), 'cleanup output must be inside repository')
    relative = path.relative_to(root).parts
    build_output = len(relative) >= 3 and relative[0] == 'Build'
    test_output = (len(relative) >= 4 and relative[0] == 'Test'
                   and relative[2] in {'bin', 'obj'})
    require(build_output or test_output, 'cleanup requires isolated Build or Test bin/obj output')
    parts = relative[1:]
    require(not any(p.lower() in PROTECTED for p in parts),
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


def generated_resources(output, files):
    """MSBuild's explicit output list distinguishes copied assets from screenshots/data."""
    result = {}
    for manifest, _ in files:
        if manifest.name.endswith('.deps.json'):
            try:
                data = json.loads(manifest.read_text(encoding='utf-8-sig'))
            except (OSError, UnicodeError, json.JSONDecodeError):
                continue  # A failed build's empty/malformed record is not deletion evidence.
            targets = data.get('targets', {}) if isinstance(data, dict) else {}
            if not isinstance(targets, dict):
                continue
            for target in targets.values():
                if not isinstance(target, dict):
                    continue
                for library in target.values():
                    if not isinstance(library, dict):
                        continue
                    for section in ('runtimeTargets', 'native'):
                        items = library.get(section, {})
                        if not isinstance(items, dict):
                            continue
                        for name in items:
                            relative = Path(name)
                            if relative.is_absolute() or '..' in relative.parts:
                                continue
                            p = manifest.parent / relative
                            if p.is_relative_to(output) and p.suffix.lower() in RESOURCE_SUFFIXES:
                                result[os.path.normcase(str(p))] = str(manifest)
            continue
        if not manifest.name.endswith('.FileListAbsolute.txt'):
            continue
        try:
            lines = manifest.read_text(encoding='utf-8-sig').splitlines()
        except (OSError, UnicodeError):
            continue
        for line in lines:
            p = Path(line.strip())
            if (p.is_absolute() and '..' not in p.parts and p.is_relative_to(output)
                    and p.suffix.lower() in RESOURCE_SUFFIXES
                    and any(part.lower() in {'bin', 'obj'} for part in p.parts)):
                result[os.path.normcase(str(p))] = str(manifest)
    return result


def prepare(root, control, paths, reason):
    """Freeze exact generated binary identities before removing anything. No directory deletion."""
    import storage_limits as storage
    require(bool(reason.strip()), 'cleanup reason required')
    outputs = [output_path(root, p) for p in paths]
    indexed = tracked(root)
    records = {}
    output_files = {output: list(storage.files_under(output)) for output in outputs}
    generation_lists = [(p, st) for files in output_files.values() for p, st in files
                        if p.name.endswith(('.FileListAbsolute.txt', '.deps.json'))]
    for output in outputs:
        # Refuse a whole output containing an authenticated execution receipt.
        files = output_files[output]
        require(not any(p.name.lower() in {'receipt.json', 'snapshot.json', 'request.json'}
                        for p, _ in files), 'receipt-bound output must remain available')
        manifests = generation_lists
        relative = output.relative_to(Path(root).resolve()).parts
        if relative[0] == 'Test' and relative[2] == 'bin':
            # Default MSBuild outputs keep their generation lists in sibling obj.
            manifests = generation_lists + list(storage.files_under(Path(root)/'Test'/relative[1]/'obj'))
        generated = generated_resources(output, manifests)
        manifest_hashes = {p: digest(Path(p)) for p in set(generated.values())}
        for p, st in files:
            if ((p.suffix.lower() not in BINARY_SUFFIXES and os.path.normcase(str(p)) not in generated)
                    or any(part.lower() in PROTECTED for part in p.relative_to(output).parts)
                    or os.path.normcase(str(p)) in indexed):
                continue
            records[str(p)] = dict(path=str(p), size=st.st_size, sha256=digest(p),
                                   device=st.st_dev, inode=st.st_ino, status='pending')
            if p.suffix.lower() not in BINARY_SUFFIXES:
                records[str(p)]['generated_by'] = generated[os.path.normcase(str(p))]
                records[str(p)]['generated_by_sha256'] = manifest_hashes[records[str(p)]['generated_by']]
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
    checked_manifests = {}
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
        require(any(p.is_relative_to(o) for o in outputs)
                and (p.suffix.lower() in BINARY_SUFFIXES or
                     (p.suffix.lower() in RESOURCE_SUFFIXES and record.get('generated_by'))),
                'cleanup manifest path outside approved output')
        require(not any(part.lower() in PROTECTED for part in p.relative_to(root).parts),
                'protected cleanup path')
        if record.get('generated_by'):
            manifest = storage.regular_tree(Path(record['generated_by']))
            same_test_obj = (p.is_relative_to(root/'Test') and len(p.relative_to(root).parts)>=4
                             and manifest.is_relative_to(root/'Test'/p.relative_to(root).parts[1]/'obj'))
            require(any(manifest.is_relative_to(o) for o in outputs) or same_test_obj,
                    'output manifest outside cleanup scope')
            if str(manifest) not in checked_manifests:
                checked_manifests[str(manifest)] = digest(manifest) if manifest.is_file() else None
            if checked_manifests[str(manifest)] != record.get('generated_by_sha256'):
                record['status'] = 'changed-kept'
                continue
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
            # Never change shared inode attributes. Independent generated readonly
            # copies can be removed without changing any retained source copy.
            readonly = bool(getattr(st, 'st_file_attributes', 0) & 1)
            if readonly and st.st_nlink > 1:
                unlink_readonly_alias(p)
            else:
                if readonly:
                    p.chmod(st.st_mode | stat.S_IWRITE)
                p.unlink()
        except OSError as error:
            if p.exists() and st.st_nlink == 1:
                p.chmod(st.st_mode)
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
