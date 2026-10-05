"""Bound new tooling artifacts. No background service or historical garbage collection."""
from __future__ import annotations
import argparse
import hashlib
from contextlib import contextmanager
from contextvars import ContextVar
from functools import wraps
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import uuid
from review_support import Blocked, encode, load, lock, require, sha

GIB = 1024 ** 3
DEFAULTS = dict(operation_bytes=4*GIB, retained_bytes=16*GIB, min_free_bytes=8*GIB)
ACTIVE = ContextVar('mistletoe_storage', default=None)
GENERATED_DIRS = {'_build_tmp', '_buildcheck_tmp', 'testresults', 'codexreviewsnapshots'}

def regular_tree(root):
    """Never traverse a reparse point, including a replaced ancestor."""
    root = Path(root).absolute()
    for p in [root, *root.parents]:
        if p.exists() or p.is_symlink():
            s = p.lstat()
            require(not stat.S_ISLNK(s.st_mode) and not getattr(s, 'st_file_attributes', 0)&1024,
                    'storage link/reparse forbidden: '+str(p))
    return root

def files_under(root):
    root = regular_tree(root)
    if not root.exists(): return
    stack = [root]
    while stack:
        p = stack.pop(); s = p.lstat()
        require(not stat.S_ISLNK(s.st_mode) and not getattr(s,'st_file_attributes',0)&1024,
                'storage link/reparse forbidden: '+str(p))
        if stat.S_ISDIR(s.st_mode):
            with os.scandir(p) as entries: stack.extend(Path(e.path) for e in entries)
        else:
            require(stat.S_ISREG(s.st_mode), 'irregular storage object')
            yield p, s

def size(roots):
    seen = set(); total = 0
    for root in roots:
        for _, s in files_under(root):
            key = (s.st_dev,s.st_ino)
            if key not in seen: total += s.st_size; seen.add(key)
    return total

def control_dir(root):
    root = Path(root).resolve()
    p = subprocess.run(['git','-C',str(root),'rev-parse','--path-format=absolute','--git-common-dir'],
                       capture_output=True, text=True)
    base = Path(p.stdout.strip()) if p.returncode == 0 else root/'_workflow'
    return regular_tree(base/'mistletoe-storage-control')

def policy(root):
    p = regular_tree(Path(root)/'_workflow/storage-policy.json')
    value = load(p) if p.exists() else dict(DEFAULTS)
    require(isinstance(value,dict) and set(value)==set(DEFAULTS), 'invalid storage policy fields')
    require(all(type(n) is int and n>0 for n in value.values()), 'storage limits must be positive integers')
    require(value['retained_bytes']>=value['operation_bytes'], 'storage total smaller than operation')
    return value

def validate_ledger(control,ledger):
    require(ledger.get('version')==1 and ledger.get('domain')==str(control)
            and isinstance(ledger.get('entries'),list),'storage ledger damaged')
    entries=ledger['entries']; ids=[e.get('id') for e in entries]
    require(all(isinstance(i,str) and len(i)==32 and all(c in '0123456789abcdef' for c in i) for i in ids)
            and len(set(ids))==len(ids),'storage reservation identities damaged')
    markers={}
    for p in regular_tree(control/'reservations').glob('*.json'):
        regular_tree(p); marker=load(p)
        require(marker.get('id')==p.stem,'storage reservation marker damaged')
        markers[p.stem]=marker
    require(set(markers)==set(ids),'storage ledger reservation history missing or reset')
    for e in entries:
        require(markers[e['id']]=={'id':e['id'],'purpose':e['purpose'],'reserved_bytes':e['reserved_bytes']},
                'storage reservation ownership drift')
        require(e.get('state') in {'retained','failed','cleaned'} and isinstance(e.get('roots'),list),
                'unresolved storage reservation')

class Session:
    def __init__(self, root, purpose):
        self.root=Path(root).resolve(); self.control=control_dir(root)
        authority=self.control.parent.parent if self.control.parent.name=='.git' else self.root
        self.policy=policy(authority)
        self.id=uuid.uuid4().hex; self.purpose=purpose; self.roots=[]; self.written=0
        self.process_unknown=False

    def __enter__(self):
        require(ACTIVE.get() is None,'storage domain locked; nested entry must use operation wrapper')
        self.lock=lock(self.control); self.lock.__enter__()
        try:
            ledger=self.control/'ledger.json'
            if ledger.exists():
                self.ledger=load(ledger)
            else:
                require(not (self.control/'initialized.json').exists(), 'missing initialized storage ledger')
                self.ledger=dict(version=1,domain=str(self.control),entries=[])
            validate_ledger(self.control,self.ledger)
            self.old_roots=[p for e in self.ledger['entries'] for p in e['roots']]
            self.baseline=size(self.old_roots)
            require(len(self.ledger['entries'])<4096,'storage ledger entry limit reached; audited maintenance required')
            self.limit=self.policy['operation_bytes']
            require(self.baseline+self.limit<=self.policy['retained_bytes'], 'storage retained+reserved budget exceeded')
            self.entry=dict(id=self.id,purpose=self.purpose,state='reserved',reserved_bytes=self.limit,
                            roots=[],cleanup_eligible=False)
            marker=self.control/'reservations'/(self.id+'.json')
            marker.parent.mkdir(parents=True,exist_ok=True)
            with marker.open('xb') as f:
                f.write(encode({'id':self.id,'purpose':self.purpose,'reserved_bytes':self.limit}))
                f.flush(); os.fsync(f.fileno())
            self.ledger['entries'].append(self.entry); self.save()
            init=self.control/'initialized.json'
            if not init.exists(): init.write_bytes(encode({'version':1,'domain':str(self.control)}))
            self.token=ACTIVE.set(self)
            return self
        except BaseException:
            self.lock.__exit__(*__import__('sys').exc_info()); raise

    def save(self):
        p=self.control/'ledger.json'; tmp=self.control/(self.id+'.tmp')
        regular_tree(p); regular_tree(tmp)
        with tmp.open('xb') as f: f.write(encode(self.ledger)); f.flush(); os.fsync(f.fileno())
        os.replace(tmp,p)

    def track(self,p):
        p=regular_tree(p)
        owned_scratch=(self.purpose=='scratch' and self.entry.get('cleanup_eligible') is True
                       and p.is_relative_to(self.control/'scratch'/self.id))
        require(p != self.control and (not p.is_relative_to(self.control) or owned_scratch),
                'artifact overlaps storage control')
        if any(p==Path(q) or p.is_relative_to(Path(q)) for q in self.roots): return
        self.roots=[q for q in self.roots if not Path(q).is_relative_to(p)]
        if str(p) not in self.roots:
            self.roots.append(str(p)); self.entry['roots']=self.roots[:]; self.save()

    def check(self, additional=0, measure=False, location=None):
        require(type(additional) is int and additional>=0, 'invalid storage reservation bytes')
        used=max(self.written, size(self.old_roots+self.roots)-self.baseline) if measure else self.written
        if measure: self.written=used
        require(used+additional<=self.limit, 'storage operation budget exceeded')
        locations=[Path(p) for p in self.roots]
        if location is not None: locations.append(Path(location))
        if not locations: locations=[self.root]
        volumes=set()
        for p in locations:
            while not p.exists(): p=p.parent
            volume=p.stat().st_dev
            if volume in volumes: continue
            volumes.add(volume)
            # Operations hold the common-dir lock throughout, so no other operation
            # in this domain has an outstanding reservation. Other projects are not a quota domain.
            require(shutil.disk_usage(p).free>=self.policy['min_free_bytes']+(self.limit-used),
                    'storage free-space reserve insufficient')
        return used

    def write(self,p,data,mode='xb'):
        p=regular_tree(p); require(isinstance(data,bytes), 'storage write requires bytes')
        self.track(p)
        previous=p.stat().st_size if p.exists() else 0
        delta=len(data) if 'a' in mode else max(0,len(data)-previous)
        self.check(delta,location=p)
        p.parent.mkdir(parents=True,exist_ok=True)
        with p.open(mode) as f:
            f.write(data); f.flush(); os.fsync(f.fileno())
        self.written+=delta

    def scratch(self):
        p=self.control/'scratch'/self.id
        # Scratch is a separate, unpublished namespace and never a source for receipts.
        require(self.purpose=='scratch' and not self.roots,'scratch must be standalone')
        owner=encode({'nonce':self.id,'unpublished':True})
        self.check(len(owner),location=p)
        p.mkdir(parents=True,exist_ok=False)
        self.roots=[str(p)]; self.entry['roots']=self.roots[:]
        self.entry['cleanup_eligible']=True
        (p/'owner.json').write_bytes(owner); self.written+=len(owner)
        self.save(); return p

    def immutable(self,base,target,data):
        """Pool only independent byte copies, never live source/User inode aliases."""
        pool=regular_tree(Path(base)/'.storage-objects'/sha(str(self.control).encode()))
        obj=regular_tree(pool/sha(data))
        self.track(pool); self.track(target)
        if obj.exists():
            require(obj.is_file() and obj.read_bytes()==data, 'immutable storage object damaged')
        else: self.write(obj,data)
        target=regular_tree(target); target.parent.mkdir(parents=True,exist_ok=True)
        # Pool and destination must be on one volume. No silent copy fallback.
        os.link(obj,target)

    def __exit__(self,kind,error,tb):
        try:
            if kind is None:
                try: self.check(measure=True)
                except BaseException as failure:
                    self.entry['state']='failed'
                    self.entry['failure']=str(failure)
                    self.entry['actual_retained_bytes']=size(self.old_roots+self.roots)-self.baseline
                    self.save(); raise
            if self.process_unknown:
                (self.control/'recovery-required.json').write_bytes(encode({'id':self.id,'reason':'unknown owned process tree'}))
                self.entry['state']='unknown'
            else:
                self.entry['state']='failed' if kind else 'retained'
            self.entry['actual_retained_bytes']=size(self.old_roots+self.roots)-self.baseline
            self.save()
        finally:
            ACTIVE.reset(self.token); self.lock.__exit__(kind,error,tb)

def operation(purpose,root_argument=0,request=False):
    def decorate(fn):
        @wraps(fn)
        def wrapper(*args,**kwargs):
            root=args[root_argument] if len(args)>root_argument else kwargs['root' if not request else 'out']
            if request: root=load(Path(root)/'request.json')['source_root']
            current=ACTIVE.get()
            if current is not None:
                require(current.control==control_dir(root),'cross-domain nested storage operation')
                return fn(*args,**kwargs)
            with Session(root,purpose): return fn(*args,**kwargs)
        return wrapper
    return decorate

def write(p,data,mode='xb'):
    s=ACTIVE.get()
    if s is None:
        # Legacy read-only verification helpers also publish tiny bookkeeping.
        p=Path(p); p.parent.mkdir(parents=True,exist_ok=True)
        with p.open(mode) as f: f.write(data); f.flush(); os.fsync(f.fileno())
    else: s.write(p,data,mode)

def immutable(base,target,data):
    s=ACTIVE.get(); require(s is not None,'immutable copy requires active storage reservation')
    s.immutable(base,target,data)

def preflight_objects(base,records):
    """Reject a known oversized capture before creating any snapshot/pool bytes."""
    current=ACTIVE.get(); require(current is not None,'missing storage reservation')
    pool=regular_tree(Path(base)/'.storage-objects'/sha(str(current.control).encode()))
    unseen={}; needed=0
    for digest,byte_count in records:
        require(isinstance(digest,str) and len(digest)==64 and all(c in '0123456789abcdef' for c in digest)
                and type(byte_count) is int and byte_count>=0,'invalid snapshot input size')
        if digest in unseen:
            require(unseen[digest]==byte_count,'inconsistent snapshot object size'); continue
        unseen[digest]=byte_count; obj=regular_tree(pool/digest)
        if obj.exists():
            require(obj.is_file() and obj.stat().st_size==byte_count,'immutable storage object damaged')
            h=hashlib.sha256()
            with obj.open('rb') as f:
                for chunk in iter(lambda:f.read(1024*1024),b''): h.update(chunk)
            require(h.hexdigest()==digest,'immutable storage object damaged')
        else: needed+=byte_count
    current.check(needed,location=base)
    return needed

def failure_record(p,value):
    """Small terminal/recovery proof must survive an already oversized child output.

    It remains charged at session close; this is not permission to publish a receipt.
    """
    data=encode(value); require(len(data)<=16384,'oversize emergency storage record')
    p=regular_tree(p); current=ACTIVE.get()
    if current is not None: current.track(p)
    p.parent.mkdir(parents=True,exist_ok=True)
    with p.open('xb') as f: f.write(data); f.flush(); os.fsync(f.fileno())

def prune_scratch(root,entry_id):
    """Only explicit unpublished scratch. Evidence and failed artifacts cannot downgrade."""
    control=control_dir(root)
    with lock(control):
        ledger=load(control/'ledger.json')
        validate_ledger(control,ledger)
        matches=[e for e in ledger['entries'] if e['id']==entry_id]
        require(len(matches)==1,'unknown storage entry')
        e=matches[0]
        require(e.get('purpose')=='scratch' and e.get('state')=='retained' and e.get('cleanup_eligible') is True,
                'published/failed/unknown evidence is not scratch')
        expected=regular_tree(control/'scratch'/entry_id)
        require(e['roots']==[str(expected)],'scratch ownership mismatch')
        owned=load(expected/'owner.json')
        require(owned=={'nonce':entry_id,'unpublished':True},'scratch ownership drift')
        files=list(files_under(expected))
        require(all(s.st_nlink==1 for _,s in files),'scratch has external hardlinks')
        require(not any(p.name in {'receipt.json','snapshot.json','request.json'} for p,_ in files),'scratch contains evidence')
        for p,_ in files: p.unlink()
        for directory,dirs,_ in os.walk(expected,topdown=False):
            for name in dirs: Path(directory,name).rmdir()
        expected.rmdir(); e['state']='cleaned'; e['roots']=[]
        tmp=control/(entry_id+'.cleanup.tmp')
        tmp.write_bytes(encode(ledger)); os.replace(tmp,control/'ledger.json')

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__); p.add_argument('--root',required=True)
    p.add_argument('--prune-scratch',required=True)
    a=p.parse_args(); prune_scratch(a.root,a.prune_scratch)
