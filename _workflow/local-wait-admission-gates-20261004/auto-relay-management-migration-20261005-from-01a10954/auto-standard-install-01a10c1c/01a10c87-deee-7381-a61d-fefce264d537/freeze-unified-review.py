from pathlib import Path
import os,sys,json,hashlib,uuid
root=Path.cwd().resolve();base=Path(__file__).resolve().parent
sys.path.insert(0,str(root/'tools/mistletoe'))
import native_review as nr
import storage_limits as storage
old=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-terminal-candidate-20261004-from-01a10716'
rid=uuid.uuid4().hex;snapshotBase=Path('E:/CodexReviewSnapshots/terminal-candidate-20261004');out=snapshotBase/rid
roots=['BetterGenshinImpact','MultiplayerHoeingAssistant','BgiCoordinatorServer','BgiCoordinatorServer.Tests','AutoHoeingUpdater','Fischless.GameCapture','Fischless.HotkeyCapture','Fischless.WindowsInput','Test','Build','tools','Docs']
extensions={'.cs','.xaml','.csproj','.props','.targets','.sln','.slnx','.json','.py','.ps1','.js','.ts','.html','.css','.md','.xml','.resx','.config','.txt','.yml','.yaml'}
with storage.Session(root,'unified-final-comprehensive-review-snapshot') as budget:
    files={};excluded=[]
    for name in roots:
        for directory,dirs,names in os.walk(root/name,followlinks=False):
            for child in list(dirs):
                p=Path(directory)/child
                if child.casefold() in nr.EXCLUDED_DIRS:
                    dirs.remove(child);excluded.append(dict(path=p.relative_to(root).as_posix(),reason='protected/generated directory'))
                elif p.is_symlink() or getattr(p.lstat(),'st_file_attributes',0)&1024:raise RuntimeError('reparse:'+str(p))
            for name in names:
                p=Path(directory)/name
                if p.suffix.lower() not in extensions or name.casefold() in nr.SECRET_NAMES or name.casefold().startswith('.env.'):continue
                b=nr.regular(p)
                try:b.decode('utf-8-sig')
                except UnicodeError:
                    excluded.append(dict(path=p.relative_to(root).as_posix(),reason='non UTF8; relevant omission must remain unknown'));continue
                files[p.relative_to(root).as_posix()]=dict(sha256=nr.sha(b),bytes=len(b))
    for p in root.iterdir():
        if p.is_file() and p.suffix.lower() in extensions:
            b=nr.regular(p)
            try:b.decode('utf-8-sig')
            except UnicodeError:continue
            files[p.name]=dict(sha256=nr.sha(b),bytes=len(b))
    prior=nr.load(old/'all-original-prior-readback.json')
    previous=nr.load(old/'comprehensive-review-1-original-final.txt')
    refs=set(prior['sources'])|set(nr.load(old/'all-original-prior-artifacts.json'))
    refs.update(str(p.relative_to(root)).replace('\\','/') for p in old.glob('comprehensive-review-1-*') if p.suffix in ['.json','.txt'])
    refs.add(str((old/'owner-implementation-grant.json').relative_to(root)).replace('\\','/'))
    for family in ['_workflow/local-wait-admission-gates-20261004','_workflow/usable-delivery-20261003']:
        for name in ['CURRENT-HANDOFF.md','plan.json','manifest.json','owner-policy.json','DELIVERY-FIRST-POLICY.md','DELIVERY-COVERAGE.md']:
            p=root/family/name
            if p.is_file():refs.add(p.relative_to(root).as_posix())
    for nav in [root/'_workflow/local-wait-admission-gates-20261004/auto-relay-management-migration-20261005-from-01a10954',base.parent]:
        for p in nav.iterdir():
            if p.is_file() and p.suffix in ['.md','.json','.txt','.py']:refs.add(p.relative_to(root).as_posix())
    for directory,dirs,names in os.walk(base):
        dirs[:]=[d for d in dirs if d not in ['private-user-backup']]
        for name in names:
            p=Path(directory)/name
            if p.suffix in ['.json','.md','.txt','.trx','.log','.py']:refs.add(p.relative_to(root).as_posix())
    contractRows={}
    for rel in sorted(refs):
        p=Path(rel) if Path(rel).is_absolute() else root/rel
        if not p.is_file():excluded.append(dict(path=rel,reason='missing preserved reference; remain unknown'));continue
        b=nr.regular(p);targetRel=rel if not Path(rel).is_absolute() else '_external/'+p.name
        contractRows[targetRel]=dict(origin=str(p),sha256=nr.sha(b),bytes=len(b))
    storage.preflight_objects(snapshotBase,[(v['sha256'],v['bytes']) for v in list(files.values())+list(contractRows.values())])
    budget.track(out);budget.track(base/'review-snapshot-observation.json');budget.check(location=out)
    for rel,row in files.items():storage.immutable(snapshotBase,out/'source'/rel,(root/rel).read_bytes())
    for rel,row in contractRows.items():storage.immutable(snapshotBase,out/'contracts'/rel,Path(row['origin']).read_bytes())
    for rel,row in files.items():assert nr.sha(nr.regular(root/rel))==row['sha256'],'current source drift'
    git=nr.git_identity(root);storage.write(out/'git-identity.json',nr.encode(git))
    for i,(metadata,patch) in enumerate(nr.git_patches(root)):storage.immutable(snapshotBase,out/'git'/(metadata['phase']+'-'+str(i)+'.patch'),patch)
    storage.write(out/'prior.json',nr.encode(prior));storage.write(out/'previous-report.json',nr.encode(previous))
    meta=dict(kind='equivalent immutable full textual source snapshot; ordinary provenance; no forged native permit/receipt',request_id=rid,source=str(out/'source'),contracts=str(out/'contracts'),source_root=str(root),files=files,contract_files=contractRows,excluded=excluded,git=git,model='gpt-6.1-sol',effort='high',stage='implementation',original_prior_sha256=nr.sha(nr.encode(prior)),previous_report_sha256=nr.sha(nr.encode(previous)),scope='Autonomous whole product/test/caller/dependency navigation. Binary assets have candidate SHA manifests; binaries/User/generated output are not source. Relevant unverified dependencies explicitly bounded.')
    storage.write(out/'snapshot.json',nr.encode(meta))
    observation=dict(request_id=rid,snapshot=str(out),snapshot_sha256=nr.sha(nr.encode(meta)),source_files=len(files),contract_files=len(contractRows),source_bytes=sum(v['bytes'] for v in files.values()),contract_bytes=sum(v['bytes'] for v in contractRows.values()),new_dispatches=0,gate_limitation='native prepare requires cap_disabled=true contrary to current finite grant; no owner policy alteration; bundle drift retained; final review uses already authorized equivalent source')
    storage.write(base/'review-snapshot-observation.json',nr.encode(observation));print(json.dumps(observation),flush=True)
    budget.check(measure=True)
