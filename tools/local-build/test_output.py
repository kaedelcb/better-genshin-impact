"""输出路径和解压安全继承的定向检查。

通过共享存储限制的隔离入口运行：
    python -B tools/mistletoe/validate_command.py --root . -- \
        python -B tools/local-build/test_output.py --artifacts "{artifacts}"
"""
from __future__ import annotations

import argparse
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import windows_security as SECURITY

SPEC = importlib.util.spec_from_file_location('bgi_local_build_output', HERE / 'build.py')
BUILD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BUILD)
import storage_reclaim as RECLAIM
from review_support import lock as storage_lock
ARTIFACTS = None


class IsolatedDirectoryTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='output-test-', dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)


class FakeResponse:
    headers = {'Content-Length': '3'}

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False

    def read(self, _size):
        return b''


class NoopGuard:
    def check(self, *_args, **_kwargs):
        return 0


class OutputRootValidationTests(IsolatedDirectoryTest):
    def setUp(self):
        super().setUp()
        self.source = self.root / 'source-repository'
        self.source.mkdir()
        self.allowed = self.root / 'outside-output'
        self.allowed.mkdir()
        self.user_file = self.allowed / 'existing-user-file.txt'
        self.user_file.write_bytes(b'preserve existing user data')

    def test_repository_root_and_its_child_are_rejected(self):
        child = self.source / 'nested-output'
        child.mkdir()
        with mock.patch.object(BUILD, 'ROOT', self.source):
            for candidate in (self.source, child):
                with self.subTest(candidate=candidate):
                    with self.assertRaises((OSError, RuntimeError, ValueError)):
                        BUILD.validate_output_root(candidate)
        self.assertTrue(child.is_dir())
        self.assertEqual(self.user_file.read_bytes(), b'preserve existing user data')

    def test_path_containing_parent_traversal_is_rejected(self):
        # The normalized destination is an existing outside directory, so a
        # successful normalization must not silently hide the original '..'.
        candidate = self.allowed / '..' / self.allowed.name
        self.assertIn('..', candidate.parts)
        with mock.patch.object(BUILD, 'ROOT', self.source):
            with self.assertRaises((OSError, RuntimeError, ValueError)):
                BUILD.validate_output_root(candidate)
        self.assertEqual(self.user_file.read_bytes(), b'preserve existing user data')

    def test_existing_directory_outside_repository_is_allowed(self):
        with mock.patch.object(BUILD, 'ROOT', self.source):
            with mock.patch.object(SECURITY, 'verify_inheritance'):
                BUILD.validate_output_root(self.allowed)
        self.assertTrue(self.allowed.is_dir())
        self.assertEqual(self.user_file.read_bytes(), b'preserve existing user data')


class ArchiveOutputTests(IsolatedDirectoryTest):
    def test_default_output_is_not_inside_source_tree(self):
        output = BUILD.default_output_root().resolve()
        source = BUILD.ROOT.resolve()
        self.assertFalse(output == source or output.is_relative_to(source),
                         '默认压缩包目录不能位于源码树内')

    def test_finalize_moves_only_the_new_archive(self):
        partial = self.root / 'release.partial'
        final = self.root / 'BetterGI.7z'
        partial.write_bytes(b'new archive bytes')
        BUILD.finalize_archive(partial, final)
        self.assertFalse(partial.exists())
        self.assertEqual(final.read_bytes(), b'new archive bytes')

    def test_existing_target_is_not_overwritten_and_partial_is_retained(self):
        partial = self.root / 'release.partial'
        final = self.root / 'BetterGI.7z'
        partial.write_bytes(b'new archive bytes')
        final.write_bytes(b'existing user archive')
        with self.assertRaises((FileExistsError, OSError, RuntimeError, ValueError)):
            BUILD.finalize_archive(partial, final)
        self.assertEqual(final.read_bytes(), b'existing user archive')
        self.assertEqual(partial.read_bytes(), b'new archive bytes')

    def test_existing_target_hardlink_does_not_damage_original(self):
        original = self.root / 'original-user-file'
        final = self.root / 'BetterGI.7z'
        partial = self.root / 'release.partial'
        original.write_bytes(b'original user data')
        os.link(original, final)
        partial.write_bytes(b'new archive bytes')
        with self.assertRaises((FileExistsError, OSError, RuntimeError, ValueError)):
            BUILD.finalize_archive(partial, final)
        self.assertEqual(original.read_bytes(), b'original user data')
        self.assertEqual(final.read_bytes(), b'original user data')
        self.assertEqual(partial.read_bytes(), b'new archive bytes')

    def test_different_destination_directory_is_rejected(self):
        other = self.root / 'other'
        other.mkdir()
        partial = self.root / 'release.partial'
        final = other / 'BetterGI.7z'
        partial.write_bytes(b'new archive bytes')
        with self.assertRaises((OSError, RuntimeError, ValueError)):
            BUILD.finalize_archive(partial, final)
        self.assertEqual(partial.read_bytes(), b'new archive bytes')
        self.assertFalse(final.exists())


class OwnedTemporaryFilesTests(IsolatedDirectoryTest):
    def test_download_does_not_overwrite_or_delete_existing_destination(self):
        dest = self.root / 'existing-user-file.bin'
        dest.write_bytes(b'user content to preserve')
        with mock.patch.object(BUILD, 'request', return_value=FakeResponse()):
            with self.assertRaises(FileExistsError):
                BUILD.download('https://unused.invalid/file', dest, NoopGuard())
        self.assertEqual(dest.read_bytes(), b'user content to preserve')

    def test_download_preserves_destination_created_during_request(self):
        dest = self.root / 'concurrent-writer.bin'

        class RaceGuard:
            def check(self, *_args, **_kwargs):
                if not dest.exists():
                    dest.write_bytes(b'concurrent user content')
                return 0

        with mock.patch.object(BUILD, 'request', return_value=FakeResponse()):
            with self.assertRaises(FileExistsError):
                BUILD.download('https://unused.invalid/file', dest, RaceGuard())
        self.assertEqual(dest.read_bytes(), b'concurrent user content')

    def test_changed_file_contents_are_retained_even_when_size_matches(self):
        path = self.root / 'temporary-file.bin'
        path.write_bytes(b'first')
        identity, digest, size = BUILD.remember_owned_file(path)
        path.write_bytes(b'other')
        with self.assertRaises((OSError, RuntimeError)):
            BUILD.remove_owned_file(path, identity, digest, size)
        self.assertEqual(path.read_bytes(), b'other')

    def test_replaced_file_is_retained_even_when_contents_match(self):
        path = self.root / 'temporary-file.bin'
        replacement = self.root / 'replacement.bin'
        path.write_bytes(b'same bytes')
        replacement.write_bytes(b'same bytes')
        identity, digest, size = BUILD.remember_owned_file(path)
        os.replace(replacement, path)
        self.assertNotEqual(BUILD.owned_identity(path, file=True), identity,
                            '测试必须真正替换文件身份')
        with self.assertRaises((OSError, RuntimeError)):
            BUILD.remove_owned_file(path, identity, digest, size)
        self.assertEqual(path.read_bytes(), b'same bytes')

    def test_file_gaining_a_hardlink_is_not_deleted(self):
        path = self.root / 'temporary-file.bin'
        other = self.root / 'another-link.bin'
        path.write_bytes(b'preserved shared content')
        identity, digest, size = BUILD.remember_owned_file(path)
        try:
            os.link(path, other)
        except OSError as error:
            self.skipTest(f'测试目录不支持创建硬链接: {error}')
        with self.assertRaises((OSError, RuntimeError)):
            BUILD.remove_owned_file(path, identity, digest, size)
        self.assertEqual(path.read_bytes(), b'preserved shared content')
        self.assertEqual(other.read_bytes(), b'preserved shared content')

    def test_nonempty_owned_directory_is_not_recursively_deleted(self):
        directory = self.root / 'temporary-directory'
        directory.mkdir()
        identity = BUILD.owned_identity(directory)
        user_file = directory / 'another-writer-file.txt'
        user_file.write_bytes(b'other writer')
        with self.assertRaises((OSError, RuntimeError)):
            BUILD.remove_owned_directory(directory, identity)
        self.assertEqual(user_file.read_bytes(), b'other writer')

    def test_replaced_owned_directory_is_not_deleted(self):
        directory = self.root / 'temporary-directory'
        moved = self.root / 'original-directory'
        directory.mkdir()
        identity = BUILD.owned_identity(directory)
        directory.rename(moved)
        directory.mkdir()
        user_file = directory / 'another-writer-file.txt'
        user_file.write_bytes(b'other writer')
        with self.assertRaises((OSError, RuntimeError)):
            BUILD.remove_owned_directory(directory, identity)
        self.assertTrue(moved.is_dir())
        self.assertEqual(user_file.read_bytes(), b'other writer')


class ReclaimSourceSnapshotTests(IsolatedDirectoryTest):
    def test_reclaim_generated_exe_preserves_untracked_source_snapshot_exe(self):
        repo = self.root / 'isolated-repository'
        template = self.root / 'empty-git-template'
        repo.mkdir()
        template.mkdir()
        subprocess.run(['git', '-c', f'init.templateDir={template}', 'init', '--quiet', str(repo)],
                       check=True, capture_output=True)

        output = repo / 'Build' / 'isolated-run' / 'artifacts'
        snapshot_exe = output / 'input-snapshot' / '源码.exe'
        generated_exe = output / 'bin' / 'generated.exe'
        snapshot_exe.parent.mkdir(parents=True)
        generated_exe.parent.mkdir(parents=True)
        source_bytes = b'MZ source snapshot to preserve'
        generated_bytes = b'MZ disposable generated output'
        snapshot_exe.write_bytes(source_bytes)
        generated_exe.write_bytes(generated_bytes)

        # The input copy is deliberately not tracked: protection must come from
        # the snapshot boundary, not accidentally from Git's tracked-file guard.
        tracked = subprocess.check_output(['git', '-C', str(repo), 'ls-files', '-z'])
        self.assertEqual(tracked, b'')
        control = BUILD.storage.control_dir(repo)
        control.mkdir(parents=True, exist_ok=True)
        with storage_lock(control):
            plan = RECLAIM.prepare(repo, control, [output], '验证只回收生成文件并保留源码快照')
            planned = {Path(item['path']) for item in plan['files']}
            self.assertIn(generated_exe, planned)
            self.assertNotIn(snapshot_exe, planned)
            result = RECLAIM.apply(control, plan)

        self.assertEqual(result['state'], 'complete')
        self.assertFalse(generated_exe.exists(), '已结束使用的生成 exe 应被回收')
        self.assertEqual(snapshot_exe.read_bytes(), source_bytes, '源码快照不得被回收或改写')
        self.assertEqual(result['unlinked_bytes'], len(generated_bytes))


class AlternateDataStreamTests(IsolatedDirectoryTest):
    @unittest.skipUnless(os.name == 'nt', 'Zone.Identifier 是 Windows 文件流')
    def test_path_exists_detects_zone_identifier_on_own_temporary_file(self):
        owner = self.root / 'temporary-ads-probe.bin'
        owner.write_bytes(b'owned temporary test file')
        stream = Path(str(owner) + ':Zone.Identifier')
        self.assertFalse(stream.exists(), '新建探针不应带已有的下载标记')

        # The file and its stream are created only inside this test's unique
        # artifacts directory; no existing user file is inspected or changed.
        stream.write_bytes(b'[ZoneTransfer]\r\nZoneId=3\r\n')

        self.assertTrue(stream.exists(), '必须能实际发现新建的 Zone.Identifier 文件流')
        self.assertEqual(stream.read_text(encoding='ascii').splitlines(), ['[ZoneTransfer]', 'ZoneId=3'])
        self.assertEqual(owner.read_bytes(), b'owned temporary test file')


class SecurityInheritanceTests(IsolatedDirectoryTest):
    def test_normal_probe_cleans_only_its_own_files(self):
        user_file = self.root / 'existing-user-file.txt'
        user_file.write_bytes(b'unchanged personal content')
        seen = []

        def medium(path):
            path = Path(path)
            seen.append(path)
            self.assertTrue(path == self.root or path.is_relative_to(self.root))
            return 8192

        with mock.patch.object(SECURITY, 'integrity_rid', side_effect=medium):
            SECURITY.verify_inheritance(self.root)
        self.assertGreaterEqual(len(seen), 3, '应检查探针目录及其内的新建对象')
        self.assertEqual({item.name for item in self.root.iterdir()}, {user_file.name})
        self.assertEqual(user_file.read_bytes(), b'unchanged personal content')

    def test_low_inherited_file_is_rejected_and_probe_is_removed(self):
        user_file = self.root / 'existing-user-file.txt'
        user_file.write_bytes(b'unchanged personal content')
        seen_files = []

        def low_file(path):
            path = Path(path)
            if path.is_file():
                seen_files.append(path)
                return 4096
            return 8192

        with mock.patch.object(SECURITY, 'integrity_rid', side_effect=low_file):
            with self.assertRaisesRegex(RuntimeError, '低完整性'):
                SECURITY.verify_inheritance(self.root)
        self.assertTrue(seen_files, '必须实际检查新创建的文件，不能只看父目录')
        self.assertEqual({item.name for item in self.root.iterdir()}, {user_file.name})
        self.assertEqual(user_file.read_bytes(), b'unchanged personal content')

    def test_probe_exception_leaves_existing_files_untouched(self):
        user_file = self.root / 'existing-user-file.txt'
        user_file.write_bytes(b'unchanged personal content')
        with mock.patch.object(SECURITY, 'integrity_rid', side_effect=OSError('test read failure')):
            with self.assertRaises((OSError, RuntimeError)):
                SECURITY.verify_inheritance(self.root)
        self.assertEqual({item.name for item in self.root.iterdir()}, {user_file.name})
        self.assertEqual(user_file.read_bytes(), b'unchanged personal content')

    def test_unexpected_file_is_not_recursively_deleted(self):
        unexpected = []

        def inject_file(path):
            path = Path(path)
            if path.is_dir() and path.parent == self.root and not unexpected:
                child = path / 'another-writer-file.txt'
                child.write_bytes(b'other writer')
                unexpected.append(child)
            return 8192

        with mock.patch.object(SECURITY, 'integrity_rid', side_effect=inject_file):
            with self.assertRaises((OSError, RuntimeError)):
                SECURITY.verify_inheritance(self.root)
        self.assertEqual(len(unexpected), 1, '必须模拟探针内出现其他写者的文件')
        self.assertEqual(unexpected[0].read_bytes(), b'other writer')

    def test_missing_probe_directory_is_rejected_without_creating_it(self):
        missing = self.root / 'not-created'
        with self.assertRaises((OSError, RuntimeError, ValueError)):
            SECURITY.verify_inheritance(missing)
        self.assertFalse(missing.exists())

    @unittest.skipUnless(os.name == 'nt', 'Windows 重解析点检查仅在 Windows 上验证')
    def test_symlink_probe_root_is_rejected_without_touching_target(self):
        target = self.root / 'user-directory'
        target.mkdir()
        user_file = target / 'existing-user-file.txt'
        user_file.write_bytes(b'unchanged personal content')
        link = self.root / 'linked-directory'
        try:
            link.symlink_to(target, target_is_directory=True)
        except OSError as error:
            self.skipTest(f'当前系统不允许在测试目录创建符号链接: {error}')
        with self.assertRaises((OSError, RuntimeError, ValueError)):
            SECURITY.verify_inheritance(link)
        self.assertTrue(link.is_symlink())
        self.assertEqual(user_file.read_bytes(), b'unchanged personal content')

    @unittest.skipUnless(os.name == 'nt', 'Windows 完整性标签仅在 Windows 上验证')
    def test_actual_low_integrity_directory_is_rejected(self):
        low = self.root / 'low-inheritance'
        low.mkdir()
        result = subprocess.run(['icacls', str(low), '/setintegritylevel', '(OI)(CI)L'],
                                capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 0, f'无法创建测试用 Low 目录: {result.stderr}')
        self.assertEqual(SECURITY.integrity_rid(low), 4096, '测试目录必须确实具有 Low 标签')
        user_file = low / 'existing-user-file.txt'
        user_file.write_bytes(b'unchanged personal content')
        with self.assertRaisesRegex(RuntimeError, '低完整性'):
            SECURITY.verify_inheritance(low)
        self.assertEqual({item.name for item in low.iterdir()}, {user_file.name})
        self.assertEqual(user_file.read_bytes(), b'unchanged personal content')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--artifacts', required=True, type=Path)
    args, unittest_args = parser.parse_known_args()
    ARTIFACTS = args.artifacts.resolve()
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    unittest.main(argv=[__file__, *unittest_args])
