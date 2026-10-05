from pathlib import Path
import sys,os,json,hashlib,subprocess
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as storage
base=Path(__file__).resolve().parent
live=Path(os.environ['APPDATA'])/'NexusBGI';saved=live.parent/'NexusBGI-preserved-01a10cef';tested=live.parent/'NexusBGI-tested-01a10cef'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
def inventory(p):return {f.relative_to(p).as_posix():sha(f) for f,s in storage.files_under(p)}
with storage.Session(root,'non-game-ui-data-preservation') as budget:
    # Packaged Codex child processes virtualize existing AppData leaf paths.
    # Resolve both existing siblings, then derive the new archive under that exact
    # observed physical parent; never combine a virtual parent and physical leaf.
    if sys.argv[1]=='restore':
        logical=[str(live),str(saved),str(tested)]
        live=live.resolve();saved=saved.resolve()
        assert live.parent==saved.parent
        allowed=Path(os.environ['LOCALAPPDATA'])/'Packages/OpenAI.Codex_2p2nqsd0c76g0/LocalCache/Roaming'
        assert live.parent in [Path(os.environ['APPDATA']).resolve(),allowed.resolve()]
        tested=live.parent/'NexusBGI-tested-01a10cef'
        storage.write(base/'ui-appdata-physical-observation.json',json.dumps(dict(logical=logical,physical=[str(live),str(saved),str(tested)],msix_virtualization=live.parent!=Path(os.environ['APPDATA']).resolve()),indent=2).encode())
    else:assert all(p.resolve().parent==live.parent.resolve() for p in [live,saved,tested])
    if sys.argv[1]=='prepare':
        assert live.is_dir() and not saved.exists() and not tested.exists()
        before=inventory(live)
        storage.write(base/'ui-data-recovery.json',json.dumps(dict(live=str(live),preserved=str(saved),test_archive=str(tested),before=before,restore='after both candidate processes normal exit: rename test NexusBGI to archive, rename preserved NexusBGI back; verify original hashes',private=True),indent=2).encode())
        os.rename(live,saved);assert inventory(saved)==before
        budget.track(live);live.mkdir()
        c=dict(serverUrl='',standaloneMode=True,disclaimerAccepted=True,bgiPath=str(root/'_workflow/runtime-unified-01a10cef/candidate-r2/BetterGI.exe'),observerMode=False,autoLaunchOnBoot=False,autoLaunchWithBgi=False,guardBgi=False,scheduledOnlineTime='')
        storage.write(live/'assistant-config.json',json.dumps(c,indent=2).encode())
        storage.write(live/'startup-flow.json',b'{"enabled":false,"steps":[]}')
        storage.write(base/'ui-safe-prepared.json',json.dumps(dict(original_files=len(before),original_bytes_unchanged=True,temporary_root=str(live),no_server=True,no_auto_task=True)).encode())
    elif sys.argv[1]=='restore':
        record=json.loads((base/'ui-data-recovery.json').read_text())
        assert saved.is_dir() and not tested.exists() and inventory(saved)==record['before']
        os.rename(live,tested);os.rename(saved,live)
        assert inventory(live)==record['before']
        budget.track(tested)
        storage.write(base/'ui-original-data-restored.json',json.dumps(dict(original_files=len(record['before']),all_original_sha_same=True,test_data_preserved=str(tested))).encode())
