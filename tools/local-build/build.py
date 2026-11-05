"""Build the GitHub Actions distribution in an isolated, accounted workspace."""
from __future__ import annotations

import json
import hashlib
import argparse
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import time
import urllib.request
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(ROOT / 'tools' / 'mistletoe'))
import process_runner
import storage_limits as storage
import windows_security

VERSION_RE = re.compile(r'^\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')
HEADERS = {'User-Agent': 'BetterGI-local-build', 'Accept': 'application/vnd.github+json'}


class CommandFailure(RuntimeError):
    pass


def request(url):
    return urllib.request.urlopen(urllib.request.Request(url, headers=HEADERS), timeout=120)


def api(url):
    for attempt in range(1, 5):
        try:
            with request(url) as response:
                return json.load(response)
        except (OSError, urllib.error.URLError) as exc:
            if attempt == 4:
                raise
            print(f'获取下载信息失败，准备重试 ({attempt}/4): {exc}', flush=True)
            time.sleep(attempt * 3)


def download(url, dest, guard):
    print('下载:', url, flush=True)
    for attempt in range(1, 5):
        created = None
        written = 0
        checksum = hashlib.sha256()
        try:
            with request(url) as response:
                length = response.headers.get('Content-Length')
                if length:
                    guard.check(int(length), measure=True, location=dest)
                with dest.open('xb') as output:
                    st = os.fstat(output.fileno())
                    if not st.st_ino or st.st_nlink != 1:
                        raise RuntimeError(f'无法确认下载文件身份，已保留：{dest}')
                    created = (st.st_dev, st.st_ino)
                    since_check = 0
                    while True:
                        chunk = response.read(1024 * 1024)
                        if not chunk:
                            break
                        since_check += len(chunk)
                        if since_check >= 16 * 1024**2:
                            output.flush()
                            guard.check(len(chunk), measure=True, location=dest)
                            since_check = 0
                        count = output.write(chunk)
                        if count != len(chunk):
                            raise RuntimeError(f'下载文件写入不完整，已保留：{dest}')
                        checksum.update(chunk)
                        written += count
                if length and dest.stat().st_size != int(length):
                    raise OSError('下载长度不完整')
            break
        except FileExistsError:
            raise FileExistsError(f'下载目标已存在，不会覆盖或删除：{dest}')
        except (OSError, urllib.error.URLError) as exc:
            if created is not None:
                remove_owned_file(dest, created, checksum.digest(), written)
            if attempt == 4:
                raise
            print(f'下载失败，准备重试 ({attempt}/4): {exc}', flush=True)
            time.sleep(attempt * 3)
    guard.check(measure=True)
    if created is None or owned_identity(dest, file=True) != created or dest.stat().st_size != written or file_digest(dest) != checksum.digest():
        raise RuntimeError(f'下载文件在完成后发生变化，已保留：{dest}')
    return created, checksum.digest(), written


def asset(release, pattern):
    matches = [x for x in release['assets'] if re.fullmatch(pattern, x['name'])]
    if len(matches) != 1:
        raise RuntimeError(f"{release.get('tag_name')} 中未找到唯一的 {pattern}")
    return matches[0]['browser_download_url']


def latest(repo):
    return api(f'https://api.github.com/repos/{repo}/releases/latest')


def capture(args, cwd=ROOT):
    return subprocess.check_output(args, cwd=cwd)


def file_digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').digest()


def owned_identity(path, *, file=False):
    path = Path(path)
    info = path.lstat()
    if path.is_symlink() or getattr(info, 'st_file_attributes', 0) & 0x400 or not info.st_ino:
        raise RuntimeError(f'无法确认临时路径身份，已保留：{path}')
    if file and (not stat.S_ISREG(info.st_mode) or info.st_nlink != 1):
        raise RuntimeError(f'临时文件不再是独占普通文件，已保留：{path}')
    if not file and not stat.S_ISDIR(info.st_mode):
        raise RuntimeError(f'临时目录已发生变化，已保留：{path}')
    return (info.st_dev, info.st_ino)


def remember_owned_file(path):
    identity = owned_identity(path, file=True)
    size = Path(path).stat().st_size
    digest = file_digest(path)
    if owned_identity(path, file=True) != identity or Path(path).stat().st_size != size:
        raise RuntimeError(f'临时文件在检查时发生变化，已保留：{path}')
    return identity, digest, size


def remove_owned_file(path, identity, digest, size):
    path = Path(path)
    if (owned_identity(path, file=True) != identity or path.stat().st_size != size
            or file_digest(path) != digest or owned_identity(path, file=True) != identity):
        raise RuntimeError(f'临时文件已发生变化，未删除：{path}')
    path.unlink()


def remove_owned_directory(path, identity):
    path = Path(path)
    if owned_identity(path) != identity:
        raise RuntimeError(f'临时目录已发生变化，未删除：{path}')
    # rmdir is intentionally nonrecursive and fails if another file appeared.
    path.rmdir()


def is_user_default(path):
    return tuple(part.casefold() for part in path.parts[:2]) == ('bettergenshinimpact', 'user')


def verify_snapshot_source(source, proof):
    commit, tracked, checked = proof
    if (capture(['git', '-C', str(source), 'rev-parse', 'HEAD']).decode().strip() != commit
            or capture(['git', '-C', str(source), 'ls-files', '-z']) != tracked):
        raise RuntimeError('源码分支或已跟踪文件列表发生变化')
    for rel, digest in checked:
        src = source / rel
        if src.is_symlink() or (digest is None and src.exists()):
            raise RuntimeError(f'源码发生变化: {rel}')
        if digest is not None and (not src.is_file() or not src.resolve().is_relative_to(source.resolve()) or file_digest(src) != digest):
            raise RuntimeError(f'源码发生变化: {rel}')
    if (capture(['git', '-C', str(source), 'rev-parse', 'HEAD']).decode().strip() != commit
            or capture(['git', '-C', str(source), 'ls-files', '-z']) != tracked):
        raise RuntimeError('源码分支或已跟踪文件列表发生变化')


def snapshot(source, target, guard, commit=None):
    # Copy tracked worktree edits, but take shipped User defaults from one fixed commit.
    commit = commit or capture(['git', '-C', str(source), 'rev-parse', 'HEAD']).decode().strip()
    if capture(['git', '-C', str(source), 'rev-parse', 'HEAD']).decode().strip() != commit:
        raise RuntimeError('源码分支已发生变化，请重新构建')
    tracked = capture(['git', '-C', str(source), 'ls-files', '-z'])
    paths = [Path(os.fsdecode(raw)) for raw in tracked.split(b'\0') if raw]
    existing = [source / rel for rel in paths if not is_user_default(rel)]
    guard.check(sum(p.stat().st_size for p in existing if p.is_file()), measure=True, location=target)
    checked = []
    for rel in paths:
        if is_user_default(rel):
            continue
        src = source / rel
        if src.is_symlink():
            raise RuntimeError(f'源码包含链接，无法安全复制: {rel}')
        if not src.is_file():  # sparse-checkout intentionally omits historical files
            if src.exists():
                raise RuntimeError(f'源码路径不是文件: {rel}')
            checked.append((rel, None))
            continue
        if not src.resolve().is_relative_to(source.resolve()):
            raise RuntimeError(f'源码包含链接，无法安全复制: {rel}')
        before = file_digest(src)
        checked.append((rel, before))
    for rel, before in checked:
        if before is None:
            continue
        src, dst = source / rel, target / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(src, dst)
        if file_digest(dst) != before:
            raise RuntimeError(f'复制期间源码发生变化: {rel}')
    defaults = capture(['git', '-C', str(source), 'ls-tree', '-r', '-z', '--name-only', commit, '--', 'BetterGenshinImpact/User'])
    for raw in defaults.split(b'\0'):
        if raw:
            rel = Path(os.fsdecode(raw))
            dst = target / rel
            dst.parent.mkdir(parents=True, exist_ok=True)
            dst.write_bytes(capture(['git', '-C', str(source), 'show', commit + ':' + rel.as_posix()]))
    proof = (commit, tracked, checked)
    verify_snapshot_source(source, proof)
    guard.check(measure=True)
    return proof


def untracked_sources(root=ROOT):
    dirs = ['BetterGenshinImpact', 'MultiplayerHoeingAssistant', 'BgiCoordinatorServer',
            'Mistletoe.Shared', 'AutoHoeingUpdater', 'Test/OneDragonMigration/Core']
    output = capture(['git', '-C', str(root), 'ls-files', '--others', '--exclude-standard', '-z', '--', *dirs])
    return [os.fsdecode(x) for x in output.split(b'\0') if x and not is_user_default(Path(os.fsdecode(x)))]


def unzip_safe(archive, dest):
    dest.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive) as z:
        for info in z.infolist():
            path = (dest / info.filename).resolve()
            if not path.is_relative_to(dest.resolve()):
                raise RuntimeError('压缩包路径不安全')
        z.extractall(dest)


def remove_owned_workspace(path):
    """Remove only this run's temporary copy, including Git's read-only pack files."""
    def retry_readonly(function, name, exc_info):
        os.chmod(name, stat.S_IREAD | stat.S_IWRITE)
        function(name)
    shutil.rmtree(path, onerror=retry_readonly)


def default_output_root():
    # The source checkout has a Low integrity label; release output must not inherit it.
    return Path(ROOT.anchor) / 'BetterGI-Builds'


def validate_output_root(value):
    selected = Path(os.path.expandvars(str(value or default_output_root()))).expanduser()
    if not selected.is_absolute() or '..' in selected.parts:
        raise RuntimeError('压缩包保存目录必须是没有上级路径片段的绝对路径')
    storage.regular_tree(selected)
    actual = selected.resolve(strict=False)
    source_root = ROOT.resolve()
    if actual == source_root or actual.is_relative_to(source_root):
        raise RuntimeError('请选择源码仓库外的保存目录')
    for parent in (actual, *actual.parents):
        if parent.name.casefold() == 'user' and any((parent.parent / name).is_file() for name in ('BetterGI.exe', 'MultiplayerHoeingAssistant.exe')):
            raise RuntimeError('不能将压缩包保存到程序的 User 数据目录')
    return selected


def finalize_archive(partial, final):
    partial, final = Path(partial), Path(final)
    if partial.parent != final.parent:
        raise ValueError('临时压缩包必须位于目标文件的同一目录')
    if final.exists() or final.is_symlink():
        raise FileExistsError(f'目标文件已存在，不会覆盖: {final}')
    # On Windows os.rename fails rather than replacing an existing destination.
    if os.name != 'nt':
        raise RuntimeError('此构建工具仅支持 Windows')
    os.rename(partial, final)


def main(version, reuse_web=None, output_root=None):
    if not VERSION_RE.fullmatch(version):
        raise RuntimeError('版本格式不正确，例如 0.66.3-alpha.1')
    missing = [name for name in ('git', 'dotnet') if not shutil.which(name)]
    if missing:
        raise RuntimeError('缺少工具: ' + ', '.join(missing))
    if not any(line.startswith('8.') for line in capture(['dotnet', '--list-sdks']).decode().splitlines()):
        raise RuntimeError('需要安装 .NET 8 SDK')
    if storage.policy(ROOT)['operation_bytes'] < 8 * 1024**3:
        raise RuntimeError('项目的单次临时存储上限不足；实际编译已超过 2.5 GiB，完整打包还需要更多空间。请先调整项目存储配置，再运行本脚本。')
    extra = untracked_sources()
    if extra:
        raise RuntimeError('以下源码目录中有未纳入 Git 的文件，无法确定是否应包含，请先处理:\n' + '\n'.join(extra[:20]))
    commit = capture(['git', '-C', str(ROOT), 'rev-parse', 'HEAD']).decode().strip()
    ident = time.strftime('%Y%m%d-%H%M%S') + '-' + uuid.uuid4().hex[:8]
    selected = validate_output_root(output_root)
    output = selected / ident
    with storage.Session(ROOT, 'local-release-package', reserve_bytes=512 * 1024**2) as guard:
        work = guard.temporary_output()
        guard.check(location=selected)
        selected_created = False
        selected_id = None
        if selected.exists():
            if not selected.is_dir():
                raise RuntimeError('压缩包保存位置不是文件夹')
        elif selected == default_output_root() and selected.parent.is_dir():
            selected.mkdir(exist_ok=False)
            selected_created = True
            selected_id = owned_identity(selected)
        else:
            raise RuntimeError('保存目录不存在，请选择一个已有文件夹')
        output.mkdir(exist_ok=False)
        output_id = owned_identity(output)
        probe = output / ('.integrity-probe-' + uuid.uuid4().hex)
        guard.track(probe)
        try:
            guard.check(4096, measure=True, location=probe)
            if windows_security.integrity_rid(output) < 8192:
                raise RuntimeError('所选保存目录会使新文件继承低完整性标记，请选择其他本地目录')
            probe.mkdir(exist_ok=False)
            probe_id = owned_identity(probe)
            try:
                if windows_security.integrity_rid(probe) < 8192:
                    raise RuntimeError('所选保存目录会使新文件继承低完整性标记，请选择其他本地目录')
                windows_security.verify_inheritance(probe)
            finally:
                # Only remove our own empty directory; unexpected contents are retained.
                prior_error = sys.exc_info()[0] is not None
                try:
                    remove_owned_directory(probe, probe_id)
                except OSError:
                    if not prior_error:
                        raise
        except BaseException:
            # This unique directory is ours and is removed only if still empty.
            try:
                remove_owned_directory(output, output_id)
            except OSError:
                pass
            if selected_created:
                try:
                    remove_owned_directory(selected, selected_id)
                except OSError:
                    pass
            raise
        marker = output / '构建尚未完成.txt'
        guard.write(marker, '构建尚未完成。请查看构建窗口中的错误信息。'.encode('utf-8'))
        marker_info = remember_owned_file(marker)
        # Recovery requests must be under the validation run, next to artifacts.
        records = work.parent / 'records'
        guard.track(records)
        records.mkdir()
        recovery = records / 'recovery'
        guard.entry['recovery_directories'] = [str(recovery)]
        guard.save()
        temp_dir = work / 'temp'
        temp_dir.mkdir()
        tools_dir = output / ('.build-tools-' + uuid.uuid4().hex)
        guard.track(tools_dir)
        tools_dir.mkdir()
        tools_id = owned_identity(tools_dir)
        owned_tools = []
        env = os.environ.copy()
        env.update({'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'NPM_CONFIG_CACHE': str(work / 'npm-cache'),
                    'NPM_CONFIG_REGISTRY': 'https://registry.npmjs.org/', 'NPM_CONFIG_FETCH_RETRIES': '5',
                    'NPM_CONFIG_FETCH_TIMEOUT': '300000', 'DOTNET_CLI_USE_MSBUILD_SERVER': '0',
                    'MSBUILDDISABLENODEREUSE': '1', 'TEMP': str(temp_dir), 'TMP': str(temp_dir)})
        seq = 0

        def run(label, args, cwd, timeout=7200):
            nonlocal seq
            seq += 1
            print(f'[{seq}] {label}，请稍候……', flush=True)
            code, stdout, stderr = process_runner.run([str(x) for x in args], cwd=cwd, env=env,
                directory=records, recovery_directory=recovery, phase=f'{seq:02d}', timeout=timeout)
            if code:
                raise CommandFailure(f'{label}失败（退出码 {code}），日志: {stdout} 和 {stderr}')
            guard.check(measure=True)

        snapshot_proof = None
        # The reclaimer protects this directory if a failed or interrupted run
        # cannot prove that the original worktree still matches this snapshot.
        source = work / 'input-snapshot'
        try:
            print('构建提交:', commit, '\n结果目录:', output, flush=True)
            source.mkdir()
            snapshot_proof = snapshot(ROOT, source, guard, commit)
            extra = untracked_sources()
            if extra:
                raise RuntimeError('复制期间发现未纳入 Git 的源码，请先处理:\n' + '\n'.join(extra[:20]))
            project = source / 'BetterGenshinImpact' / 'BetterGenshinImpact.csproj'
            contents = project.read_text(encoding='utf-8-sig')
            contents, count = re.subn(r'<Version>.*?</Version>', f'<Version>{version}</Version>', contents, count=1)
            if count != 1:
                raise RuntimeError('未找到主程序版本号')
            project.write_text(contents, encoding='utf-8')
            # Tools that create the archive must themselves be on the selected
            # normal-integrity volume, not in the Low-integrity source checkout.
            seven = tools_dir / '7zr.exe'
            seven_info = download(asset(latest('ip7z/7zip'), r'7zr\.exe'), seven, guard)
            owned_tools.append((seven, seven_info))
            builder = tools_dir / 'kachina-builder.exe'
            builder_info = download(asset(latest('YuehaiTeam/kachina-installer'), r'kachina-builder\.exe'), builder, guard)
            owned_tools.append((builder, builder_info))
            if windows_security.integrity_rid(seven) < 8192 or windows_security.integrity_rid(builder) < 8192:
                raise RuntimeError('构建工具继承了低完整性标记，请选择其他保存目录')
            web = []
            if reuse_web is None:
                node_index = api('https://nodejs.org/dist/index.json')
                node_version = next(x['version'] for x in node_index if x['version'].startswith('v20.'))
                node_zip = work / 'node.zip'
                node_zip_info = download(f'https://nodejs.org/dist/{node_version}/node-{node_version}-win-x64.zip', node_zip, guard)
                unzip_safe(node_zip, work / 'node')
                remove_owned_file(node_zip, *node_zip_info)
                node_dir = work / 'node' / f'node-{node_version}-win-x64'
                env['PATH'] = str(node_dir) + os.pathsep + env.get('PATH', '')
                node = node_dir / 'node.exe'
                npm = node_dir / 'node_modules/npm/bin/npm-cli.js'
                web_revisions = {}
                for name, repo in [('map', 'huiyadanli/bettergi-map'), ('scripts', 'zaodonganqi/bettergi-script-web')]:
                    dest = work / name
                    run('下载网页源码 ' + name, ['git', '-c', 'core.longpaths=true', 'clone', '--depth', '1', f'https://github.com/{repo}.git', dest], work)
                    web_revisions[repo] = capture(['git', '-C', str(dest), 'rev-parse', 'HEAD']).decode().strip()
                    lock = dest / 'package-lock.json'
                    if lock.exists():
                        text = lock.read_text(encoding='utf-8')
                        text = re.sub(r'https?://(?:mirrors\.cloud\.tencent\.com/npm|registry\.npmmirror\.com)/', 'https://registry.npmjs.org/', text)
                        lock.write_text(text, encoding='utf-8')
                    for attempt in range(1, 5):
                        try:
                            run('安装网页依赖 ' + name, [node, npm, 'install', '--prefer-offline', '--no-audit', '--no-fund', '--cache', work / 'npm-cache', '--registry', 'https://registry.npmjs.org/'], dest)
                            break
                        except CommandFailure:
                            if attempt == 4:
                                raise
                            print(f'依赖安装失败，准备重试 ({attempt}/4)', flush=True)
                            time.sleep(attempt * 15)
                    run('构建网页 ' + name, [node, npm, 'run', 'build:single'], dest)
                    web.append(dest / 'dist')
            else:
                # Internal continuation: reuse only outputs from a successfully built web run.
                reuse_web = Path(reuse_web).resolve()
                if not reuse_web.is_relative_to(ROOT / 'Build' / 'validation-runs'):
                    raise RuntimeError('网页复用目录不属于本仓库的验证记录')
                previous = reuse_web.parent / 'records'
                for phase in ('03', '06'):
                    if json.loads((previous / f'{phase}-process-result.json').read_text(encoding='utf-8'))['exit_code'] != 0:
                        raise RuntimeError('网页构建记录未成功，不能复用')
                web_revisions = json.loads((previous / 'web-sources.json').read_text(encoding='utf-8'))
                web = [reuse_web / 'web-dist-0', reuse_web / 'web-dist-1']
                if not all((item / 'index.html').is_file() for item in web):
                    raise RuntimeError('已构建的网页文件不完整')
                node_version = '复用已验证的网页构建'
                print('复用刚才已成功构建的网页。', flush=True)
            guard.write(records / 'web-sources.json', json.dumps(web_revisions, ensure_ascii=False, indent=2).encode('utf-8'))
            retained_web = []
            for index, dist_path in enumerate(web):
                target = work / f'web-dist-{index}'
                shutil.copytree(dist_path, target)
                retained_web.append(target)
            for name in ('map', 'scripts', 'node', 'npm-cache'):
                temp = work / name
                if temp.exists():
                    remove_owned_workspace(temp)
            web = retained_web
            run('发布主程序', ['dotnet', 'publish', 'BetterGenshinImpact/BetterGenshinImpact.csproj', '-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false', '-p:PublishProfile=FolderProfile', f'-p:Version={version}', f'-p:SourceRevisionId={commit}'], source)
            run('构建联机助手', ['dotnet', 'build', 'MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj', '-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false', f'-p:SourceRevisionId={commit}'], source)
            pub = source / 'BetterGenshinImpact/bin/x64/Release/net8.0-windows10.0.22621.0/publish/win-x64'
            dist = work / 'dist'
            package = dist / 'BetterGI'
            package.mkdir(parents=True)
            for p in pub.rglob('*'):
                if p.is_file() and (p.suffix.lower() in ('.lib', '.pdb') or ('ffmpeg' in p.name.lower() and p.suffix.lower() == '.dll')):
                    p.unlink()
            shutil.copytree(source / 'MultiplayerHoeingAssistant/bin/Release/net8.0-windows', package / 'Tools/MultiplayerHoeingAssistant', dirs_exist_ok=True, ignore=shutil.ignore_patterns('assistant-config.json'))
            shutil.copytree(source / 'AutoHoeingUpdater', package / 'Tools/AutoHoeingUpdater', dirs_exist_ok=True)
            shutil.copytree(pub, package, dirs_exist_ok=True)
            shutil.copytree(web[0], package / 'Assets/Map/Editor', dirs_exist_ok=True)
            shutil.copytree(web[1], package / 'Assets/Web/ScriptRepo', dirs_exist_ok=True)
            guard.check(measure=True)
            run('生成在线更新器', [builder, 'pack', '-c', source / 'Build/kachina.config.json', '-o', 'BetterGI/BetterGI.update.exe'], dist)
            final_archive = output / f'BetterGI_v{version}.7z'
            archive = output / f'BetterGI_v{version}.{uuid.uuid4().hex}.partial.7z'
            guard.track(archive)
            guard.track(final_archive)
            guard.check(storage.size([package]) + 16 * 1024**2, measure=True, location=archive)
            if owned_identity(output) != output_id or archive.exists() or archive.is_symlink():
                raise RuntimeError('输出目录或临时压缩包已发生变化，不会覆盖现有文件')
            run('生成 7z 压缩包', [seven, 'a', archive, 'BetterGI', '-t7z', '-mx=5', '-mf=BCJ2', '-r', '-y'], dist)
            archive_info = remember_owned_file(archive)
            run('检查压缩包完整性', [seven, 't', archive], work)
            if windows_security.integrity_rid(archive) < 8192:
                raise RuntimeError('压缩包继承了低完整性标记，请选择其他保存目录')
            if Path(str(archive) + ':Zone.Identifier').exists():
                raise RuntimeError('压缩包携带来源标记，不能确认解压后不会出现警告')
            verify_dir = output / ('.extract-check-' + uuid.uuid4().hex)
            guard.track(verify_dir)
            verify_dir.mkdir()
            verify_id = owned_identity(verify_dir)
            extracted_info = None
            extracted_parent_id = None
            try:
                guard.check((package / 'BetterGI.exe').stat().st_size + 2 * 1024**2, measure=True, location=verify_dir)
                run('验证解压后的程序', [seven, 'x', archive, 'BetterGI/BetterGI.exe', '-o' + str(verify_dir), '-y'], work)
                extracted = verify_dir / 'BetterGI' / 'BetterGI.exe'
                extracted_parent_id = owned_identity(extracted.parent)
                extracted_info = remember_owned_file(extracted)
                if extracted_info[1] != file_digest(package / 'BetterGI.exe'):
                    raise RuntimeError('解压后的程序与构建结果不一致')
                if Path(str(extracted) + ':Zone.Identifier').exists():
                    raise RuntimeError('解压后的程序携带来源标记，不能确认安全状态')
                if windows_security.integrity_rid(extracted) < 8192 or windows_security.attachment_policy(extracted) != 0:
                    raise RuntimeError('Windows 仍会对解压后的程序发出安全警告，请选择其他保存目录')
            finally:
                if not guard.process_unknown and not recovery.joinpath('inflight.json').exists():
                    prior_error = sys.exc_info()[0] is not None
                    try:
                        if extracted_info is not None:
                            remove_owned_file(extracted, *extracted_info)
                        if extracted_parent_id is not None:
                            remove_owned_directory(extracted.parent, extracted_parent_id)
                        remove_owned_directory(verify_dir, verify_id)
                    except (OSError, RuntimeError) as cleanup_error:
                        print(f'解压验证目录未能安全清理，已保留：{verify_dir}（{cleanup_error}）', file=sys.stderr, flush=True)
                        if not prior_error:
                            raise
            info = {'version': version, 'source_commit': commit, 'node': node_version, 'web_revisions': web_revisions, 'tracked_worktree_edits_included': True, 'untracked_files_included': False, 'extracted_windows_policy': 'allowed'}
            guard.write(output / '构建信息.json', json.dumps(info, ensure_ascii=False, indent=2).encode('utf-8'))
            if owned_identity(output) != output_id or remember_owned_file(archive) != archive_info:
                raise RuntimeError('输出目录或压缩包已发生变化，未生成最终文件')
        finally:
            # Only remove this invocation's fresh workspace after all owned children ended.
            if not guard.process_unknown and not recovery.joinpath('inflight.json').exists():
                original_error = sys.exc_info()[0] is not None
                can_remove_work = not source.exists()
                if snapshot_proof is not None:
                    try:
                        verify_snapshot_source(ROOT, snapshot_proof)
                        can_remove_work = True
                    except (OSError, RuntimeError, subprocess.SubprocessError) as changed:
                        print(f'源码已变化，保留构建时的隔离副本：{work}（{changed}）', file=sys.stderr, flush=True)
                elif source.exists():
                    print(f'源码快照未完成，保留隔离副本：{work}', file=sys.stderr, flush=True)
                cleanup_errors = []
                try:
                    if can_remove_work:
                        remove_owned_workspace(work)
                except (OSError, RuntimeError) as cleanup_error:
                    cleanup_errors.append(str(cleanup_error))
                try:
                    for path, identity in owned_tools:
                        remove_owned_file(path, *identity)
                    remove_owned_directory(tools_dir, tools_id)
                except (OSError, RuntimeError) as cleanup_error:
                    cleanup_errors.append(str(cleanup_error))
                if cleanup_errors:
                    print('临时目录未能安全清理，已保留用于排查: ' + '；'.join(cleanup_errors), file=sys.stderr, flush=True)
                    if not original_error:
                        raise RuntimeError('构建完成但临时文件清理失败')
        guard.check(measure=True)
        if owned_identity(output) != output_id or remember_owned_file(archive) != archive_info:
            raise RuntimeError('输出目录或压缩包已发生变化，未生成最终文件')
        finalize_archive(archive, final_archive)
        remove_owned_file(marker, *marker_info)
    print('\n压缩包构建完成，文件位置:', output, flush=True)


if __name__ == '__main__':
    try:
        parser = argparse.ArgumentParser(description='本地构建 BetterGI 压缩包')
        parser.add_argument('version', nargs='?', help='构建版本号')
        parser.add_argument('--output-dir', help='压缩包保存目录（绝对路径）')
        args = parser.parse_args()
        value = args.version or input('请输入版本号（例如 0.66.3-alpha.1）: ').strip()
        if not VERSION_RE.fullmatch(value):
            raise RuntimeError('版本格式不正确，例如 0.66.3-alpha.1')
        selected = args.output_dir
        if selected is None and args.version is None:
            selected = input(f'压缩包保存目录（回车使用 {default_output_root()}）: ').strip().strip('"') or None
        main(value, output_root=selected)
    except Exception as exc:
        print('\n构建未完成:', exc, file=sys.stderr, flush=True)
        sys.exit(1)
