"""Own two-root UI acceptance preservation; never copies or deletes original trees."""
from pathlib import Path
import ctypes, hashlib, json, msvcrt, subprocess, sys, winreg
from ctypes import wintypes as w

ROOT = Path(__file__).resolve().parents[2]
BASE = Path(__file__).resolve().parent / 'r2'
sys.path.insert(0, str(ROOT / 'tools/mistletoe'))
import storage_limits as storage

PRODUCT = ROOT / '_workflow/runtime-unified-01a10e1b/product'
MSIX = Path('C:/Users/Administrator/AppData/Local/Packages/OpenAI.Codex_2p2nqsd0c76g0/LocalCache/Roaming/NexusBGI')
REGULAR = Path('C:/Users/Administrator/AppData/Roaming/NexusBGI')
TAG = '01a11220-r2'
KERNEL = ctypes.WinDLL('kernel32', use_last_error=True)
KERNEL.GetFinalPathNameByHandleW.argtypes = [w.HANDLE, w.LPWSTR, w.DWORD, w.DWORD]
KERNEL.GetFinalPathNameByHandleW.restype = w.DWORD

def companion(path, kind):
    namespace = 'msix' if path == MSIX else 'regular'
    return path.with_name('NexusBGI-' + namespace + '-' + kind + '-' + TAG)

def inventory(root, exact=True):
    result = {}
    for path, _ in storage.files_under(root):
        with path.open('rb') as stream:
            buf = ctypes.create_unicode_buffer(32768)
            count = KERNEL.GetFinalPathNameByHandleW(msvcrt.get_osfhandle(stream.fileno()), buf, len(buf), 0)
            assert count and count < len(buf), 'Unknown file handle path'
            actual = buf.value
            if exact:
                assert actual.lower() == ('\\\\?\\' + str(path)).lower(), 'Redirected physical input: ' + str(path)
            data = stream.read()
        result[path.relative_to(root).as_posix()] = dict(sha256=hashlib.sha256(data).hexdigest(), bytes=len(data), actual=actual)
    return result

def hashes(record):
    return {key: (value['sha256'], value['bytes']) for key, value in record.items()}

def idle():
    result = subprocess.run(['pwsh', '-NoProfile', '-NonInteractive', '-Command',
        '@(Get-Process BetterGI,MultiplayerHoeingAssistant -ErrorAction SilentlyContinue | Select-Object Id,SessionId) | ConvertTo-Json -Compress'], capture_output=True, text=True, check=True)
    assert not result.stdout.strip(), 'BGI/assistant process present; no data move'

def move(source, target):
    assert source.parent == target.parent and source.is_dir() and not target.exists()
    quote = lambda path: "'" + str(path).replace("'", "''") + "'"
    subprocess.run(['pwsh', '-NoProfile', '-NonInteractive', '-Command',
        'Move-Item -LiteralPath ' + quote(source) + ' -Destination ' + quote(target) + ' -ErrorAction Stop'], check=True, capture_output=True)
    assert target.is_dir() and not source.exists(), 'Move outcome unknown'

def write(path, value):
    storage.write(path, json.dumps(value, ensure_ascii=False, indent=2).encode())

phase = sys.argv[1]
idle()
with storage.Session(ROOT, 'final-ui-' + TAG + '-data-' + phase) as budget:
    budget.track(BASE)
    for root in [MSIX, REGULAR]:
        for path in [root, companion(root, 'preserved'), companion(root, 'tested')]:
            budget.track(path)
    if phase == 'external-restore':
        before = json.loads((BASE / 'private/msix-before.json').read_text(encoding='utf-8'))
        regular_before = json.loads((BASE / 'private/regular-before.json').read_text(encoding='utf-8'))
        recovered = MSIX.with_name('NexusBGI-regular-recovery-' + TAG)
        assert hashes(inventory(MSIX)) == hashes(before)
        assert hashes(inventory(recovered)) == hashes(regular_before)
        # An unpackaged process must see the conventional namespace directly.
        # Any redirect or an existing differing target fails without a move.
        if REGULAR.exists():
            assert hashes(inventory(REGULAR)) == hashes(regular_before), 'Existing conventional root differs; refuse overwrite'
            action = 'Existing physical conventional originals verified; recovery copy retained'
        else:
            assert REGULAR == Path('C:/Users/Administrator/AppData/Roaming/NexusBGI')
            quote = lambda path: "'" + str(path).replace("'", "''") + "'"
            subprocess.run(['pwsh','-NoProfile','-NonInteractive','-Command',
                'Move-Item -LiteralPath ' + quote(recovered) + ' -Destination ' + quote(REGULAR) + ' -ErrorAction Stop'],
                check=True,capture_output=True)
            assert not recovered.exists()
            assert hashes(inventory(REGULAR)) == hashes(regular_before)
            action = 'Preserved conventional originals moved back to exact physical original root'
        assert hashes(inventory(MSIX)) == hashes(before)
        write(BASE / 'external-restored.json',dict(action=action, msix_original_files=len(before),
            regular_original_files=len(regular_before), both_physical_originals_sha_same=True,
            exact_file_handle_paths=True, apps_launched=False, acceptance=False))
        print('Physical recovery VERIFIED: MSIX 116/116; conventional Roaming 27/27; no overwritten data',flush=True)
    elif phase == 'recover-overlay':
        before = json.loads((BASE / 'private/msix-before.json').read_text(encoding='utf-8'))
        regular_before = json.loads((BASE / 'private/regular-before.json').read_text(encoding='utf-8'))
        recovered = MSIX.with_name('NexusBGI-regular-recovery-' + TAG)
        budget.track(recovered)
        assert not recovered.exists()
        assert hashes(inventory(MSIX)) == hashes(regular_before)
        assert hashes(inventory(companion(MSIX,'preserved'))) == hashes(before)
        move(MSIX,recovered)
        assert hashes(inventory(recovered)) == hashes(regular_before)
        move(companion(MSIX,'preserved'),MSIX)
        assert hashes(inventory(MSIX)) == hashes(before)
        write(BASE / 'overlay-recovery.json',dict(msix_original_files=len(before), msix_original_sha_same=True,
            regular_original_files=len(regular_before), regular_original_sha_same_in_preserved_location=True,
            regular_preserved=str(recovered), regular_original_location_unverified=True,
            no_test_config_created=True, no_apps_launched=True, acceptance=False,
            blocker='MSIX virtualizes conventional directory moves and handle-relative opens; restore physical conventional root from an unpackaged process'))
        print('MSIX original restored; regular original 27 files retained by SHA; physical location needs unpackaged recovery',flush=True)
    elif phase == 'prepare':
        assert not (BASE / 'private/baseline.json').exists()
        for root in [MSIX, REGULAR]:
            assert root.is_dir() and not companion(root, 'preserved').exists() and not companion(root, 'tested').exists()
        # Package namespace is moved first. Only then can conventional originals
        # be inventoried without the package overlay hiding same-named files.
        original = {}
        original['msix'] = inventory(MSIX)
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\Classes\BetterGI\shell\open\command') as key:
            protocol, kind = winreg.QueryValueEx(key, '')
        write(BASE / 'private/msix-before.json', original['msix'])
        move(MSIX, companion(MSIX, 'preserved'))
        assert hashes(inventory(companion(MSIX, 'preserved'))) == hashes(original['msix'])
        try:
            original['regular'] = inventory(REGULAR)
            write(BASE / 'private/regular-before.json', original['regular'])
            move(REGULAR, companion(REGULAR, 'preserved'))
            assert hashes(inventory(companion(REGULAR, 'preserved'))) == hashes(original['regular'])
        except BaseException:
            if companion(REGULAR, 'preserved').exists() and not REGULAR.exists():
                move(companion(REGULAR, 'preserved'), REGULAR)
            if not MSIX.exists():
                move(companion(MSIX, 'preserved'), MSIX)
            raise
        write(BASE / 'private/baseline.json', dict(original=original, protocol=protocol, protocol_kind=kind))
        for root in [REGULAR, MSIX]:
            root.mkdir()
            write(root / 'assistant-config.json', dict(serverUrl='', standaloneMode=True, disclaimerAccepted=True,
                bgiPath=str(PRODUCT / 'BetterGI.exe'), observerMode=False, autoLaunchOnBoot=False,
                autoLaunchWithBgi=False, guardBgi=False, scheduledOnlineTime=''))
            storage.write(root / 'startup-flow.json', b'{"enabled":false,"steps":[]}')
        write(BASE / 'prepared.json', dict(marker='FINAL-UI-ACCEPT-20261007-FROM-01a10f9b',
            policy='mistletoe-release-first-20261005-v2', storage_policy='mistletoe-storage-limits-20261005-v1',
            original_counts={key:len(value) for key,value in original.items()}, both_original_trees_preserved=True,
            roots=[str(REGULAR),str(MSIX)], servers=False, autostart=False, accepted=False))
        print('Prepared both physical namespaces; original counts', {key:len(value) for key,value in original.items()}, flush=True)
    elif phase == 'restore':
        baseline = json.loads((BASE / 'private/baseline.json').read_text(encoding='utf-8'))
        for key, root in [('msix',MSIX),('regular',REGULAR)]:
            assert hashes(inventory(companion(root,'preserved'))) == hashes(baseline['original'][key])
            assert not companion(root,'tested').exists()
        move(MSIX, companion(MSIX,'tested'))
        # The package overlay is now absent; read and archive the physical root.
        tested_regular = inventory(REGULAR)
        move(REGULAR, companion(REGULAR,'tested'))
        write(BASE / 'private/tested-regular.json', tested_regular)
        move(companion(REGULAR,'preserved'),REGULAR)
        assert hashes(inventory(REGULAR)) == hashes(baseline['original']['regular'])
        move(companion(MSIX,'preserved'),MSIX)
        assert hashes(inventory(MSIX)) == hashes(baseline['original']['msix'])
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\Classes\BetterGI\shell\open\command', 0, winreg.KEY_READ | winreg.KEY_WRITE) as key:
            current, kind = winreg.QueryValueEx(key,'')
            if str(PRODUCT / 'BetterGI.exe').lower() in current.lower():
                winreg.SetValueEx(key,'',0,baseline['protocol_kind'],baseline['protocol'])
            current,kind = winreg.QueryValueEx(key,'')
            assert current == baseline['protocol'] and kind == baseline['protocol_kind']
        write(BASE / 'restored.json',dict(both_physical_originals_sha_same=True, protocol_restored=True,
            tested_roots=[str(companion(REGULAR,'tested')),str(companion(MSIX,'tested'))], accepted=False))
        print('Both originals restored; both test archives retained',flush=True)
    else:
        raise ValueError('Unsupported phase')
