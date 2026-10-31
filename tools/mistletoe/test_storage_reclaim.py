import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import storage_limits as s
import storage_reclaim as reclaim
from review_support import Blocked, encode, load, lock


class ReclamationTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name).resolve()
        subprocess.run(['git','init','-q',str(self.root)],check=True)
        p=self.root/'_workflow/storage-policy.json'; p.parent.mkdir()
        p.write_bytes(encode(dict(operation_bytes=4096,retained_bytes=16384,min_free_bytes=1)))

    def test_close_removes_binary_and_preserves_user_logs_and_source(self):
        with s.Session(self.root,'test') as guard:
            out=guard.temporary_output()
            for name in ('app.dll','app.pdb','run.log','result.trx','source.cs','User/plugin.dll','data/save.dll'):
                guard.write(out/name,b'content')
        self.assertFalse((out/'app.dll').exists())
        for name in ('run.log','result.trx','source.cs','User/plugin.dll','data/save.dll'):
            self.assertEqual((out/name).read_bytes(),b'content')
        self.assertEqual(load(guard.control/'ledger.json')['entries'][-1]['state'],'retained')

    def test_repeated_and_nested_roots_count_once_without_rewalking(self):
        out=self.root/'Build/old/artifacts'; out.mkdir(parents=True)
        (out/'a.dll').write_bytes(b'abcd')
        with patch.object(s,'files_under', wraps=s.files_under) as walk:
            self.assertEqual(s.size([out,out,out/'a.dll',out.parent]),4)
        self.assertEqual(walk.call_count,1)

    @unittest.skipUnless(os.name == 'nt', 'Windows retained process handles')
    def test_exited_process_with_open_handle_does_not_hold_lock(self):
        from review_support import _process_alive
        self.assertTrue(_process_alive(os.getpid()))
        proc=subprocess.Popen([sys.executable,'-c','pass'])
        proc.wait(timeout=10)
        try:
            self.assertFalse(_process_alive(proc.pid))
        finally:
            proc._handle.Close()

    def test_failure_keeps_failed_state_and_original_log(self):
        with self.assertRaisesRegex(RuntimeError,'test failed'):
            with s.Session(self.root,'test') as guard:
                out=guard.temporary_output()
                guard.write(out/'app.dll',b'output'); guard.write(out/'failure.log',b'original failure')
                raise RuntimeError('test failed')
        self.assertFalse((out/'app.dll').exists())
        self.assertEqual((out/'failure.log').read_bytes(),b'original failure')
        self.assertEqual(load(guard.control/'ledger.json')['entries'][-1]['state'],'failed')

    def test_unknown_process_preserves_binary(self):
        with s.Session(self.root,'test') as guard:
            out=guard.temporary_output(); guard.write(out/'app.dll',b'keep')
            guard.process_unknown=True
        self.assertTrue((out/'app.dll').exists())

    def test_changed_or_tracked_files_are_kept(self):
        out=self.root/'Build/old/artifacts'; out.mkdir(parents=True)
        (out/'a.dll').write_bytes(b'old'); (out/'b.dll').write_bytes(b'keep')
        control=s.control_dir(self.root)
        with lock(control):
            plan=reclaim.prepare(self.root,control,[out],'explicit old outputs')
            (out/'a.dll').write_bytes(b'new')
            subprocess.run(['git','-C',str(self.root),'add','Build/old/artifacts/b.dll'],check=True)
            reclaim.apply(control,plan)
        self.assertEqual((out/'a.dll').read_bytes(),b'new')
        self.assertEqual((out/'b.dll').read_bytes(),b'keep')

    def test_root_and_user_rejected(self):
        for p in (self.root,self.root/'Build',self.root/'Build/task',self.root/'Build/task/User',
                  self.root/'Build/task/artifacts/../../../outside'):
            with self.assertRaises(Blocked):reclaim.output_path(self.root,p)

    def test_receipt_bound_output_is_not_deleted(self):
        out=self.root/'Build/task/artifacts'; out.mkdir(parents=True)
        (out/'receipt.json').write_text('{}'); (out/'a.dll').write_bytes(b'keep')
        with self.assertRaisesRegex(Blocked,'receipt-bound'):
            with lock(s.control_dir(self.root)):
                reclaim.prepare(self.root,s.control_dir(self.root),[out],'old output')
        self.assertTrue((out/'a.dll').exists())

    def test_pending_cleanup_runs_before_budget_check(self):
        with s.Session(self.root,'old') as guard:
            out=self.root/'Build/old/artifacts'; guard.write(out/'a.dll',b'x'*4096)
        with lock(guard.control):
            reclaim.prepare(self.root,guard.control,[out],'reviewed disposable output')
        (self.root/'_workflow/storage-policy.json').write_bytes(encode(
            dict(operation_bytes=4096,retained_bytes=4096,min_free_bytes=1)))
        with s.Session(self.root,'new') as new:
            new.write(self.root/'new.log',b'ok')
        self.assertFalse((out/'a.dll').exists())

    def test_hardlink_retained_copy_is_unchanged(self):
        out=self.root/'Build/old/artifacts'; out.mkdir(parents=True)
        keep=self.root/'keep.dll'; keep.write_bytes(b'keep')
        os.link(keep,out/'copy.dll')
        control=s.control_dir(self.root)
        with lock(control):
            plan=reclaim.prepare(self.root,control,[out],'duplicate')
            reclaim.apply(control,plan)
        self.assertEqual(keep.read_bytes(),b'keep')
        self.assertEqual(plan['released_unique_bytes'],0)

    def test_symlink_rejected(self):
        out=self.root/'Build/task/artifacts'; out.mkdir(parents=True)
        target=self.root/'keep.dll'; target.write_bytes(b'keep')
        try:(out/'link.dll').symlink_to(target)
        except OSError:self.skipTest('symlink unavailable')
        with self.assertRaises(Blocked):
            with lock(s.control_dir(self.root)):
                reclaim.prepare(self.root,s.control_dir(self.root),[out],'not safe')
        self.assertEqual(target.read_bytes(),b'keep')

    def test_completed_output_recovered_after_interrupted_cleanup(self):
        with patch.object(reclaim,'prepare',side_effect=OSError('interrupted')):
            with s.Session(self.root,'old') as guard:
                out=guard.temporary_output(); guard.write(out/'a.dll',b'old')
        self.assertTrue((out/'a.dll').exists())
        with s.Session(self.root,'next'):pass
        self.assertFalse((out/'a.dll').exists())

    @unittest.skipUnless(os.name == 'nt', 'Windows Job Object entry')
    def test_actual_validation_entry_pass_and_fail_cleanup(self):
        import validate_command
        (self.root/'_workflow/storage-policy.json').write_bytes(encode(
            dict(operation_bytes=1024*1024,retained_bytes=4*1024*1024,min_free_bytes=1)))
        for code in (0,7):
            argv=[sys.executable,'-c',
                "from pathlib import Path; import sys; p=Path(sys.argv[1]); "
                "(p/'test.dll').write_bytes(b'generated'); (p/'proof.log').write_text('proof'); "
                "print('real runner'); sys.exit(int(sys.argv[2]))", '{artifacts}',str(code)]
            if code:
                with self.assertRaises(validate_command.ValidationFailed) as failure:
                    validate_command.run(self.root,argv,15)
                self.assertEqual(failure.exception.code,7)
                records=failure.exception.records
            else:
                records=validate_command.run(self.root,argv,15)
            self.assertFalse((records.parent/'artifacts/test.dll').exists())
            self.assertEqual((records.parent/'artifacts/proof.log').read_text(),'proof')
            self.assertEqual(load(records/'result.json')['exit_code'],code)
            self.assertIn('real runner',(records/'validation-stdout.log').read_text())


if __name__=='__main__':unittest.main()
