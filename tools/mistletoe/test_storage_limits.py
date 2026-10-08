"""Storage counterexamples exercise existing public tooling entry points on small fixtures."""
import os
from pathlib import Path
import subprocess
import sys
import shutil
import stat
import tempfile
import unittest
from unittest.mock import patch
import storage_limits as s
from review_support import Blocked, encode, load
import test_native_review as native_fixture

class StorageTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup)
        self.root=Path(self.tmp.name).resolve()
        self.set_policy(4096,16384)

    def set_policy(self,operation,total):
        p=self.root/'_workflow/storage-policy.json'; p.parent.mkdir(exist_ok=True)
        p.write_bytes(encode(dict(operation_bytes=operation,retained_bytes=total,min_free_bytes=1)))

    def test_write_limit_rejects_before_file_creation_and_preserves_previous(self):
        with s.Session(self.root,'fixture') as guard:
            out=self.root/'out'; guard.track(out)
            guard.write(out/'keep',b'a'*4096)
            with self.assertRaisesRegex(Blocked,'operation budget'): guard.write(out/'excess',b'x')
        self.assertFalse((out/'excess').exists()); self.assertEqual((out/'keep').stat().st_size,4096)

    def test_shared_cross_batch_lock_and_retained_budget(self):
        with s.Session(self.root,'first') as guard:
            with self.assertRaisesRegex(Blocked,'locked'):
                with s.Session(self.root,'second'): pass
            guard.write(self.root/'one',b'a'*4096)
        self.set_policy(4096,4096)
        with self.assertRaisesRegex(Blocked,'retained'):
            with s.Session(self.root,'different-batch'): pass

    def test_free_space_reservation_before_first_artifact(self):
        with patch.object(s.shutil,'disk_usage',return_value=type('Usage',(),{'free':4096})()):
            with self.assertRaisesRegex(Blocked,'free-space'):
                with s.Session(self.root,'fixture') as guard: guard.write(self.root/'no',b'a')
        self.assertFalse((self.root/'no').exists())

    def test_corrupt_or_missing_initialized_ledger_fails_closed(self):
        with s.Session(self.root,'fixture'): pass
        ledger=s.control_dir(self.root)/'ledger.json'; ledger.unlink()
        with self.assertRaisesRegex(Blocked,'missing initialized'):
            with s.Session(self.root,'fixture'): pass

    def test_valid_json_ledger_cannot_silently_drop_old_reservations(self):
        with s.Session(self.root,'fixture') as guard: guard.write(self.root/'keep',b'keep')
        p=s.control_dir(self.root)/'ledger.json'; ledger=load(p); ledger['entries']=[];p.write_bytes(encode(ledger))
        with self.assertRaisesRegex(Blocked,'history missing or reset'):
            with s.Session(self.root,'fixture'): pass
        self.assertEqual((self.root/'keep').read_bytes(),b'keep')

    def test_partial_failure_bytes_remain_charged(self):
        with self.assertRaisesRegex(RuntimeError,'cancel'):
            with s.Session(self.root,'fixture') as guard:
                guard.write(self.root/'partial',b'a'*4096); raise RuntimeError('cancel')
        self.assertEqual(load(s.control_dir(self.root)/'ledger.json')['entries'][0]['state'],'failed')
        self.set_policy(4096,4096)
        with self.assertRaisesRegex(Blocked,'retained'):
            with s.Session(self.root,'retry'): pass

    def test_final_validation_failure_is_failed_and_not_cleanable_scratch(self):
        with self.assertRaisesRegex(Blocked,'operation budget'):
            with s.Session(self.root,'scratch') as guard:
                p=guard.scratch(); ident=guard.id
                (p/'late').write_bytes(b'x'*5000)
        entry=load(s.control_dir(self.root)/'ledger.json')['entries'][-1]
        self.assertEqual(entry['state'],'failed')
        with self.assertRaisesRegex(Blocked,'not scratch'): s.prune_scratch(self.root,ident)

    def test_real_workflow_audit_and_review_snapshot_dispatch_limits(self):
        import test_workflow as wf
        import test_review_process as rf
        import review_process as rp
        f=wf.SnapshotChecks(); f.setUp()
        try:
            (f.root/'_workflow/storage-policy.json').write_bytes(encode(dict(operation_bytes=1,retained_bytes=s.GIB,min_free_bytes=1)))
            with self.assertRaisesRegex(Blocked,'operation budget'): f.audit()
            self.assertFalse((f.root/'_workflow/snapshot/report.json').exists())
        finally:f.doCleanups()
        f=rf.ProcessChecks(); f.setUp()
        try:
            f.write('_workflow/storage-policy.json',dict(operation_bytes=1,retained_bytes=s.GIB,min_free_bytes=1))
            c,_,local,_=rp.registered(f.root,f.m)
            with self.assertRaisesRegex(Blocked,'operation budget'): rp.capture_snapshot(f.root,f.m,c,local)
            assessment=f.assessment()
            with patch.object(rp,'environment',return_value=os.environ.copy()):
                with self.assertRaisesRegex(Blocked,'operation budget'):
                    rp.dispatch(f.root,f.m,'plan',sys.executable,'unused',assessment_path=assessment)
            self.assertFalse(list((local/'requests').glob('*/receipt.json')))
        finally:f.doCleanups()

    def test_pool_reuses_inode_without_aliasing_mutable_source(self):
        src=self.root/'live'; src.write_bytes(b'original')
        with s.Session(self.root,'fixture') as guard:
            guard.immutable(self.root/'snapshots',self.root/'snapshot1/file',src.read_bytes())
        before=s.size([self.root/'snapshots',self.root/'snapshot1'])
        with s.Session(self.root,'fixture') as guard:
            guard.immutable(self.root/'snapshots',self.root/'snapshot2/file',src.read_bytes())
        self.assertEqual(before,s.size([self.root/'snapshots',self.root/'snapshot1',self.root/'snapshot2']))
        self.assertTrue(os.path.samefile(self.root/'snapshot1/file',self.root/'snapshot2/file'))
        self.assertFalse(os.path.samefile(src,self.root/'snapshot1/file'))
        src.write_bytes(b'new'); self.assertEqual((self.root/'snapshot1/file').read_bytes(),b'original')

    def test_damaged_pool_refuses_reuse(self):
        with s.Session(self.root,'fixture') as guard:
            guard.immutable(self.root/'snapshots',self.root/'snapshot1/file',b'original')
        (self.root/'snapshot1/file').write_bytes(b'damaged')
        with self.assertRaisesRegex(Blocked,'damaged'):
            with s.Session(self.root,'fixture') as guard:
                guard.immutable(self.root/'snapshots',self.root/'snapshot2/file',b'original')
        self.assertFalse((self.root/'snapshot2/file').exists())

    def test_scratch_prune_is_exact_and_published_evidence_refused(self):
        with s.Session(self.root,'scratch') as guard:
            p=guard.scratch(); guard.write(p/'file',b'temp'); ident=guard.id
        keep=self.root/'keep'; keep.write_bytes(b'keep')
        s.prune_scratch(self.root,ident)
        self.assertFalse(p.exists()); self.assertEqual(keep.read_bytes(),b'keep')
        with s.Session(self.root,'execution-capture') as guard:
            guard.write(self.root/'evidence/receipt.json',b'{}'); ident=guard.id
        with self.assertRaisesRegex(Blocked,'not scratch'): s.prune_scratch(self.root,ident)

    def test_scratch_rejects_external_hardlink(self):
        with s.Session(self.root,'scratch') as guard:
            p=guard.scratch(); (p/'file').write_bytes(b'temp'); ident=guard.id
        os.link(p/'file',self.root/'external')
        with self.assertRaisesRegex(Blocked,'hardlinks'): s.prune_scratch(self.root,ident)
        self.assertTrue((p/'file').exists())

    def test_scratch_cannot_be_execution_recipe_input(self):
        import execution_evidence as e
        with s.Session(self.root,'scratch') as guard:
            p=guard.scratch(); guard.write(p/'recipe.json',b'{}')
        with self.assertRaisesRegex(Blocked,'protected path'):
            e.capture(self.root,(p/'recipe.json').relative_to(self.root).as_posix(),'_workflow/execution')
        self.assertTrue((p/'recipe.json').exists())

    def test_real_job_quick_exit_excess_never_completes_receipt(self):
        import process_runner
        with self.assertRaisesRegex(Blocked,'operation budget'):
            with s.Session(self.root,'fixture') as guard:
                out=self.root/'job'; out.mkdir(); guard.track(out)
                process_runner.run([sys.executable,'-c',"from pathlib import Path; Path('large').write_bytes(b'x'*20000)"],
                    cwd=out,env=os.environ.copy(),directory=out,recovery_directory=self.root/'recovery',phase='run',timeout=5)
        self.assertFalse((out/'receipt.json').exists())
        self.assertEqual(load(out/'run-cleanup.json')['active_processes'],0)

    def test_actual_execution_capture_rejects_log_duplication_before_receipt(self):
        import execution_evidence as e
        self.set_policy(30000,100000)
        script=self.root/'test.py'
        script.write_text("import sys;from pathlib import Path;print('x'*20000);Path(sys.argv[1]).write_text('<TestRun/>')")
        recipe=dict(purpose='current_regression',conditions='isolated fixture',input_roots=[],input_files=['test.py'],
                    run_argv=[sys.executable,'{root}/test.py','{out}/results.trx'],products=[],results=['results.trx'],timeout_seconds=5)
        (self.root/'_workflow/recipe.json').write_bytes(encode(recipe))
        with self.assertRaisesRegex(Blocked,'operation budget'):
            e.capture(self.root,'_workflow/recipe.json','_workflow/execution')
        self.assertFalse(list((self.root/'_workflow/execution').glob('*/receipt.json')))

    def test_git_worktrees_share_budget_domain(self):
        p=self.root/'repo'; p.mkdir()
        def git(*args): subprocess.run(['git','-C',str(p),*args],check=True,capture_output=True)
        git('init','-q'); (p/'a').write_text('a'); git('add','--','a')
        git('-c','user.name=fixture','-c','user.email=fixture@example.invalid','commit','--only','-qm','fixture','--','a')
        other=self.root/'worktree'; git('worktree','add','--detach',str(other))
        self.assertEqual(s.control_dir(p),s.control_dir(other))
        with s.Session(p,'fixture'):
            command=[sys.executable,'-B','-c',"import sys;sys.path.insert(0,sys.argv[1]);import storage_limits as s;\nwith s.Session(sys.argv[2],'fixture'):pass",str(Path(s.__file__).parent),str(other)]
            result=subprocess.run(command,capture_output=True,text=True)
            self.assertNotEqual(result.returncode,0); self.assertIn('locked',result.stderr)

class ReservationSizingTests(unittest.TestCase):
    setUp=StorageTests.setUp
    set_policy=StorageTests.set_policy

    def test_small_default_fits_where_full_ceiling_does_not(self):
        self.set_policy(16*1024**2,16*1024**2)
        with s.Session(self.root,'seed',reserve_bytes=1) as guard:
            guard.write(self.root/'existing',b'x')
        with s.Session(self.root,'small') as guard:
            self.assertEqual(guard.limit,4*1024**2)
            guard.write(self.root/'small',b'ok')
        entry=load(s.control_dir(self.root)/'ledger.json')['entries'][-1]
        self.assertEqual(entry['reserved_bytes'],4*1024**2)
        with self.assertRaisesRegex(Blocked,'retained'):
            with s.Session(self.root,'full',reserve_bytes=16*1024**2): pass
        self.assertEqual((self.root/'existing').read_bytes(),b'x')

    def test_default_clamps_to_root_ceiling(self):
        with s.Session(self.root,'small') as guard:
            self.assertEqual(guard.limit,4096)

    def test_explicit_request_limits_writes_and_marker(self):
        with s.Session(self.root,'explicit',reserve_bytes=3) as guard:
            guard.write(self.root/'keep',b'abc')
            with self.assertRaisesRegex(Blocked,'operation budget'):
                guard.write(self.root/'excess',b'x')
            self.assertEqual(load(guard.control/'reservations'/(guard.id+'.json'))['reserved_bytes'],3)
        self.assertFalse((self.root/'excess').exists())

    def test_invalid_request_has_no_reservation_side_effects(self):
        control=s.control_dir(self.root)
        for value in [True,False,0,-1,1.5,'2',4097]:
            with self.subTest(value=value),self.assertRaisesRegex(Blocked,'reservation'):
                with s.Session(self.root,'invalid',reserve_bytes=value): pass
            self.assertFalse((control/'ledger.json').exists())
            self.assertFalse((control/'initialized.json').exists())
            self.assertFalse((control/'writer.lock').exists())
            self.assertFalse((control/'reservations').exists())

    def test_legacy_lowered_policy_is_an_explicit_request(self):
        self.set_policy(256*1024**2,512*1024**2)
        for amount in [2*1024**2,8*1024**2,128*1024**2]:
            with self.subTest(amount=amount):
                guard=s.Session(self.root,'legacy')
                guard.policy['operation_bytes']=amount
                with guard:
                    self.assertEqual(guard.limit,amount)
                    guard.write(self.root/str(amount),b'ok')
        entries=load(s.control_dir(self.root)/'ledger.json')['entries']
        self.assertEqual([e['reserved_bytes'] for e in entries],[2*1024**2,8*1024**2,128*1024**2])

    def test_lowered_cap_still_bounds_explicit_request(self):
        guard=s.Session(self.root,'explicit',reserve_bytes=3)
        guard.policy['operation_bytes']=2
        with self.assertRaisesRegex(Blocked,'reservation'):
            with guard: pass
        self.assertFalse((guard.control/'ledger.json').exists())

    def test_legacy_override_cannot_raise_authority_ceiling(self):
        for value in [True,0,-1,1.5,'2',4097]:
            guard=s.Session(self.root,'invalid')
            guard.policy['operation_bytes']=value
            with self.subTest(value=value),self.assertRaisesRegex(Blocked,'ceiling'):
                with guard: pass
            self.assertFalse((guard.control/'ledger.json').exists())

    def test_optional_full_protocol_and_nested_outer_reservation(self):
        self.set_policy(8*1024**2,16*1024**2)
        @s.operation('small')
        def small(root):
            return s.ACTIVE.get().limit
        @s.operation('capture',reserve_full_limit=True)
        def capture(root,name='capture'):
            s.write(root/name,b'x'*(5*1024**2))
        self.assertEqual(small(self.root),4*1024**2)
        capture(self.root)
        self.assertEqual((self.root/'capture').stat().st_size,5*1024**2)
        with s.Session(self.root,'outer',reserve_bytes=4) as guard:
            with self.assertRaisesRegex(Blocked,'operation budget'):
                capture(self.root,'nested-capture')
            self.assertEqual(guard.limit,4)
        self.assertFalse((self.root/'nested-capture').exists())
        self.assertEqual((self.root/'capture').stat().st_size,5*1024**2)

    def test_worktree_full_protocol_uses_shared_authority_policy(self):
        repo=self.root/'repo'; repo.mkdir()
        def git(*args):
            subprocess.run(['git','-C',str(repo),*args],check=True,capture_output=True)
        git('init','-q'); (repo/'a').write_text('a'); git('add','--','a')
        git('-c','user.name=fixture','-c','user.email=fixture@example.invalid','commit','--only','-qm','fixture','--','a')
        other=self.root/'worktree'; git('worktree','add','--detach',str(other))
        for root,ceiling in [(repo,64),(other,128)]:
            p=root/'_workflow/storage-policy.json'; p.parent.mkdir(exist_ok=True)
            p.write_bytes(encode(dict(operation_bytes=ceiling,retained_bytes=256,min_free_bytes=1)))
        @s.operation('capture',reserve_full_limit=True)
        def capture(root):
            return s.ACTIVE.get().limit
        self.assertEqual(capture(other),64)

class NativeStorageEntryTests(unittest.TestCase):
    def setUp(self):
        self.fixture=native_fixture.NativeReviewTests(); self.fixture.setUp()
        base=self.fixture.base
        def cleanup():
            assert base.resolve().parent==Path(tempfile.gettempdir()).resolve()
            assert base.name.startswith('native-review-test-')
            s.regular_tree(base)
            for p,info in s.files_under(base):
                if getattr(info,'st_file_attributes',0)&1:
                    assert info.st_nlink==1
                    os.chmod(p,info.st_mode|stat.S_IWRITE)
            shutil.rmtree(base)
        self.addCleanup(cleanup)

    def test_two_real_prepare_calls_reuse_source_even_when_prior_grows(self):
        f=self.fixture; first=f.prepare(); a=load(first/'snapshot.json')
        before=s.size([f.base/'snapshots'])
        second=f.prepare(); b=load(second/'snapshot.json')
        self.assertNotEqual(a['input']['extra'],b['input']['extra'])
        self.assertTrue(os.path.samefile(Path(a['source'])/'src/A.cs',Path(b['source'])/'src/A.cs'))
        self.assertLess(s.size([f.base/'snapshots'])-before,16384)
        f.write('src/A.cs','class A { public int Run() => 2; }\n'); third=f.prepare(); c=load(third/'snapshot.json')
        self.assertFalse(os.path.samefile(Path(a['source'])/'src/A.cs',Path(c['source'])/'src/A.cs'))
        self.assertIn('B.Value',(Path(a['source'])/'src/A.cs').read_text())

    def test_generated_outputs_excluded_ignored_source_retained(self):
        f=self.fixture; f.write('_build_tmp/large.dll','output'); f.write('ignored/Unique.cs','class Unique{}')
        f.write('.gitignore','ignored/\n_build_tmp/\n')
        out=f.prepare(); snapshot=load(out/'snapshot.json')
        self.assertNotIn('_build_tmp/large.dll',snapshot['input']['files'])
        self.assertIn('ignored/Unique.cs',snapshot['input']['files'])

    def test_real_prepare_insufficient_budget_no_valid_request(self):
        f=self.fixture; f.write('_workflow/storage-policy.json',dict(operation_bytes=1,retained_bytes=16,min_free_bytes=1))
        with self.assertRaisesRegex(Blocked,'operation budget'): f.prepare()
        self.assertFalse(list((f.root/'_workflow/b/native/requests').glob('*/request.json')))

    def test_known_total_rejected_before_any_partial_pool_copy(self):
        f=self.fixture; f.write('src/one.cs','a'*600); f.write('src/two.cs','b'*600)
        f.write('_workflow/storage-policy.json',dict(operation_bytes=1000,retained_bytes=s.GIB,min_free_bytes=1))
        with self.assertRaisesRegex(Blocked,'operation budget'): f.prepare()
        self.assertFalse((f.base/'snapshots/.storage-objects').exists())

    def test_native_resume_actual_entry_obeys_budget(self):
        f=self.fixture; out=f.prepare()
        raw=f.rollout(out,f.report(out,kind='checkpoint',ordinal=1,parent_checkpoint_sha256=None,remaining_queue=['other/B.cs']))
        native_fixture.n.checkpoint(out,raw,1)
        f.write('_workflow/storage-policy.json',dict(operation_bytes=1,retained_bytes=s.GIB,min_free_bytes=1))
        with self.assertRaisesRegex(Blocked,'operation budget'):
            native_fixture.n.resume(out,'/root/recovery',f.terminal_status(),1)

    def test_scratch_cannot_be_native_contract_input(self):
        f=self.fixture
        with s.Session(f.root,'scratch') as guard:
            p=guard.scratch(); guard.write(p/'proof.txt',b'proof')
        f.config['extra_files']=[(p/'proof.txt').relative_to(f.root.resolve()).as_posix()]
        f.write('_workflow/b/config.json',f.config)
        with self.assertRaisesRegex(Blocked,'cannot be evidence input'): f.prepare()
        self.assertTrue((p/'proof.txt').exists())

    def test_native_capture_and_checkpoint_actual_entries_obey_budget(self):
        f=self.fixture; out=f.prepare()
        raw=f.rollout(out,f.report(out))
        f.write('_workflow/storage-policy.json',dict(operation_bytes=1,retained_bytes=s.GIB,min_free_bytes=1))
        with self.assertRaisesRegex(Blocked,'operation budget'): native_fixture.n.capture(out,raw)
        self.assertFalse((out/'receipt.json').exists())
        raw=f.rollout(out,f.report(out,kind='checkpoint',ordinal=1,parent_checkpoint_sha256=None,remaining_queue=['other/B.cs']))
        with self.assertRaisesRegex(Blocked,'operation budget'): native_fixture.n.checkpoint(out,raw,1)
        self.assertFalse((out/'checkpoints/1.json').exists())

class StorageDiscrimination(unittest.TestCase):
    def test_storage_critical_guards_reject_bad_isolated_implementations(self):
        here=Path(s.__file__).parent; original=(here/'storage_limits.py').read_text()
        cases=[
            ('quota','require(used+additional<=self.limit,','require(True,',
             'StorageTests.test_write_limit_rejects_before_file_creation_and_preserves_previous'),
            ('reuse','os.link(obj,target)','target.write_bytes(data)',
             'StorageTests.test_pool_reuses_inode_without_aliasing_mutable_source'),
            ('cleanup','require(all(s.st_nlink==1 for _,s in files),','require(True,',
             'StorageTests.test_scratch_rejects_external_hardlink'),
            ('preflight','current.check(needed,location=base)','current.check(0,location=base)',
             'NativeStorageEntryTests.test_known_total_rejected_before_any_partial_pool_copy')]
        for label,old,new,target in cases:
            with self.subTest(guard=label),tempfile.TemporaryDirectory() as temp:
                self.assertEqual(original.count(old),1)
                mutated=original.replace(old,new); compile(mutated,'storage_limits.py','exec')
                dest=Path(temp); (dest/'storage_limits.py').write_text(mutated)
                for name in ['review_support.py','native_review.py','test_native_review.py','test_storage_limits.py','process_runner.py','execution_evidence.py']:
                    (dest/name).write_bytes((here/name).read_bytes())
                result=subprocess.run([sys.executable,'-B','-m','unittest','test_storage_limits.'+target],cwd=dest,capture_output=True,text=True)
                self.assertNotEqual(result.returncode,0); self.assertIn('FAIL:',result.stderr)
                self.assertNotIn('ERROR:',result.stderr)

    def test_native_scratch_namespace_guard_has_discriminating_assertion(self):
        here=Path(s.__file__).parent; original=(here/'native_review.py').read_text()
        old="require('mistletoe-storage-control' not in {part.casefold() for part in p.parts},"
        self.assertEqual(original.count(old),1)
        mutated=original.replace(old,'require(True,'); compile(mutated,'native_review.py','exec')
        with tempfile.TemporaryDirectory() as temp:
            dest=Path(temp); (dest/'native_review.py').write_text(mutated)
            for name in ['review_support.py','storage_limits.py','test_native_review.py','test_storage_limits.py']:
                (dest/name).write_bytes((here/name).read_bytes())
            result=subprocess.run([sys.executable,'-B','-m','unittest','test_storage_limits.NativeStorageEntryTests.test_scratch_cannot_be_native_contract_input'],cwd=dest,capture_output=True,text=True)
            self.assertNotEqual(result.returncode,0);self.assertIn('FAIL:',result.stderr);self.assertNotIn('ERROR:',result.stderr)

if __name__=='__main__': unittest.main()
