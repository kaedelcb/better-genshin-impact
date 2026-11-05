"""Targeted checks for local-release source snapshots and private User files.

Run under the project's isolated validation entry point:
    python -B tools/mistletoe/validate_command.py --root . -- \
        python -B tools/local-build/test_snapshot.py --artifacts "{artifacts}"
"""
from __future__ import annotations

import argparse
import importlib.util
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock


BUILD_PATH = Path(__file__).with_name('build.py')
SPEC = importlib.util.spec_from_file_location('bgi_local_build', BUILD_PATH)
BUILD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BUILD)
ARTIFACTS = None


class Guard:
    def check(self, *args, **kwargs):
        return 0


class SourceSnapshotTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='snapshot-test-', dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        base = Path(self.temp.name)
        self.repo = base / 'repo'
        self.target = base / 'snapshot'
        self.repo.mkdir()
        self.target.mkdir()
        self.git('init', '-q')
        self.put('Project/A.cs', 'committed A\n')
        self.put('Project/B.cs', 'committed B\n')
        self.put('BetterGenshinImpact/User/default.txt', 'committed default\n')
        self.git('add', '--', '.')
        self.git('-c', 'user.name=Snapshot Test', '-c', 'user.email=snapshot@example.invalid',
                 '-c', 'commit.gpgsign=false', 'commit', '-qm', 'test fixture')
        self.commit = self.git('rev-parse', 'HEAD').strip()

    def git(self, *args):
        return subprocess.check_output(['git', '-C', str(self.repo), *args],
                                       stderr=subprocess.PIPE).decode('utf-8')

    def put(self, rel, content):
        path = self.repo / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding='utf-8', newline='\n')
        return path

    def snapshot(self):
        return BUILD.snapshot(self.repo, self.target, Guard(), self.commit)

    def test_snapshot_keeps_tracked_edits_but_uses_pinned_user_defaults(self):
        self.put('Project/A.cs', 'local product edit\n')
        original_user = self.put('BetterGenshinImpact/User/default.txt', 'private local edit\n')
        self.put('BetterGenshinImpact/User/private.txt', 'private untracked data\n')
        real_capture = BUILD.capture
        user_reads = []

        def record_capture(args, *rest, **kwargs):
            if 'show' in args:
                user_reads.append(args[-1])
            return real_capture(args, *rest, **kwargs)

        with mock.patch.object(BUILD, 'capture', side_effect=record_capture):
            self.snapshot()

        self.assertEqual((self.target / 'Project/A.cs').read_text(encoding='utf-8'),
                         'local product edit\n')
        self.assertEqual((self.target / 'BetterGenshinImpact/User/default.txt').read_text(encoding='utf-8'),
                         'committed default\n')
        self.assertFalse((self.target / 'BetterGenshinImpact/User/private.txt').exists())
        self.assertEqual(original_user.read_text(encoding='utf-8'), 'private local edit\n')
        self.assertIn(self.commit + ':BetterGenshinImpact/User/default.txt', user_reads)
        self.assertNotIn('HEAD:BetterGenshinImpact/User/default.txt', user_reads)

    def test_untracked_personal_user_file_does_not_block_but_source_does(self):
        self.put('BetterGenshinImpact/User/private.txt', 'personal data\n')
        self.assertEqual(BUILD.untracked_sources(root=self.repo), [])
        self.put('BetterGenshinImpact/NewFeature.cs', 'untracked source\n')
        found = BUILD.untracked_sources(root=self.repo)
        self.assertIn('BetterGenshinImpact/NewFeature.cs', [str(x).replace('\\', '/') for x in found])
        self.assertFalse(any('private.txt' in str(x) for x in found))

    def test_snapshot_rejects_head_change_during_copy(self):
        real_copy = BUILD.shutil.copyfile
        changed = False

        def change_head_after_copy(src, dst, *args, **kwargs):
            nonlocal changed
            result = real_copy(src, dst, *args, **kwargs)
            if not changed:
                changed = True
                self.git('-c', 'user.name=Snapshot Test', '-c', 'user.email=snapshot@example.invalid',
                         '-c', 'commit.gpgsign=false', 'commit', '--allow-empty', '-qm', 'new head')
            return result

        with mock.patch.object(BUILD.shutil, 'copyfile', side_effect=change_head_after_copy):
            with self.assertRaises((RuntimeError, ValueError)):
                self.snapshot()
        self.assertTrue(changed, 'the test must exercise a real HEAD change during copying')

    def test_snapshot_rejects_head_change_during_final_file_verification(self):
        real_digest = BUILD.file_digest
        tracked = self.repo / 'Project/A.cs'
        source_reads = 0
        changed = False

        def change_head_during_recheck(path):
            nonlocal source_reads, changed
            result = real_digest(path)
            if Path(path) == tracked:
                source_reads += 1
                # The final read is after the initial pre-copy hash and the
                # per-file source/destination comparison.
                if source_reads == 2:
                    self.git('-c', 'user.name=Snapshot Test', '-c', 'user.email=snapshot@example.invalid',
                             '-c', 'commit.gpgsign=false', 'commit', '--allow-empty', '-qm', 'late head')
                    changed = True
            return result

        with mock.patch.object(BUILD, 'file_digest', side_effect=change_head_during_recheck):
            with self.assertRaises((RuntimeError, ValueError)):
                self.snapshot()
        self.assertTrue(changed, 'the test must change HEAD during the final tracked-file recheck')

    def test_snapshot_rejects_tracked_source_change_even_if_status_stays_modified(self):
        tracked = self.put('Project/A.cs', 'first local edit\n')
        before_status = self.git('status', '--porcelain', '--', 'Project/A.cs')
        real_copy = BUILD.shutil.copyfile
        changed = False

        def change_source_after_copy(src, dst, *args, **kwargs):
            nonlocal changed
            result = real_copy(src, dst, *args, **kwargs)
            if Path(src) == tracked and not changed:
                changed = True
                tracked.write_text('other local edit\n', encoding='utf-8', newline='\n')
            return result

        with mock.patch.object(BUILD.shutil, 'copyfile', side_effect=change_source_after_copy):
            with self.assertRaises((RuntimeError, ValueError)):
                self.snapshot()
        self.assertTrue(changed, 'the test must exercise a real content change during copying')
        self.assertEqual(before_status, self.git('status', '--porcelain', '--', 'Project/A.cs'))

    def test_snapshot_rejects_change_to_a_tracked_file_not_yet_copied(self):
        later = self.repo / 'Project/B.cs'
        self.put('Project/B.cs', 'first local edit\n')
        before_status = self.git('status', '--porcelain', '--', 'Project/B.cs')
        real_copy = BUILD.shutil.copyfile
        changed = False

        def change_later_source(src, dst, *args, **kwargs):
            nonlocal changed
            result = real_copy(src, dst, *args, **kwargs)
            if Path(src) == self.repo / 'Project/A.cs' and not changed:
                changed = True
                later.write_text('other local edit\n', encoding='utf-8', newline='\n')
            return result

        with mock.patch.object(BUILD.shutil, 'copyfile', side_effect=change_later_source):
            with self.assertRaises((RuntimeError, ValueError)):
                self.snapshot()
        self.assertTrue(changed, 'the later tracked file must change before it is copied')
        self.assertEqual(before_status, self.git('status', '--porcelain', '--', 'Project/B.cs'))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--artifacts', required=True, type=Path)
    args, unittest_args = parser.parse_known_args()
    ARTIFACTS = args.artifacts.resolve()
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    unittest.main(argv=[__file__, *unittest_args])
