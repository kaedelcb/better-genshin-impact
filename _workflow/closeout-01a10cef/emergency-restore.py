from pathlib import Path
import os,sys,json,hashlib
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as storage
base=Path(__file__).resolve().parent
record=json.loads((base/'ui-data-recovery.json').read_text())
logical=Path(record['live']);live=logical.resolve();saved=Path(record['preserved']).resolve()
allowed=Path(os.environ['LOCALAPPDATA'])/'Packages/OpenAI.Codex_2p2nqsd0c76g0/LocalCache/Roaming'
assert live.parent==saved.parent and live.parent in [Path(os.environ['APPDATA']).resolve(),allowed.resolve()]
tested=live.parent/'NexusBGI-tested-01a10cef'
assert saved.is_dir() and not tested.exists()
def inventory(p):return {f.relative_to(p).as_posix():hashlib.sha256(f.read_bytes()).hexdigest() for f,_ in storage.files_under(p)}
assert inventory(saved)==record['before']
# Safety unwind only: two exact directory renames, zero copied bytes, no deletion.
os.rename(live,tested);os.rename(saved,live)
assert inventory(live)==record['before']
storage.failure_record(base/'ui-emergency-restored.json',dict(reason='storage retained+reserved budget exceeded; safety unwind only',original_files=len(record['before']),all_original_sha_same=True,logical=str(logical),physical=str(live),test_data_preserved=str(tested),candidate_apps_already_normal_exit=True,product_sources_unmodified=True,not_receipt=True))
print(json.dumps(dict(original_files=len(record['before']),all_original_sha_same=True,physical_root=str(live),test_archive=str(tested))),flush=True)
