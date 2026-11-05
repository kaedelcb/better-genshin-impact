"""Build the GitHub Actions distribution in an isolated, accounted workspace."""
from __future__ import annotations

import json
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
sys.path.insert(0, str(ROOT / 'tools' / 'mistletoe'))
import process_runner
import storage_limits as storage

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
        try:
            with request(url) as response:
                length = response.headers.get('Content-Length')
                if length:
                    guard.check(int(length), measure=True, location=dest)
                with dest.open('xb') as output:
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
                        output.write(chunk)
                if length and dest.stat().st_size != int(length):
                    raise OSError('下载长度不完整')
            break
        except (OSError, urllib.error.URLError) as exc:
            if dest.exists():
                dest.unlink()
            if attempt == 4:
                raise
            print(f'下载失败，准备重试 ({attempt}/4): {exc}', flush=True)
            time.sleep(attempt * 3)
    guard.check(measure=True)


def asset(release, pattern):
    matches = [x for x in release['assets'] if re.fullmatch(pattern, x['name'])]
    if len(matches) != 1:
        raise RuntimeError(f"{release.get('tag_name')} 中未找到唯一的 {pattern}")
    return matches[0]['browser_download_url']


def latest(repo):
    return api(f'https://api.github.com/repos/{repo}/releases/latest')


def capture(args, cwd=ROOT):
    return subprocess.check_output(args, cwd=cwd)


def snapshot(source, target, guard):
    # Worktree bytes include edits to tracked code. Never copy untracked runtime data.
    paths = capture(['git', '-C', str(source), 'ls-files', '-z']).split(b'\0')
    existing = [source / Path(os.fsdecode(raw)) for raw in paths if raw]
    guard.check(sum(p.stat().st_size for p in existing if p.is_file()), measure=True, location=target)
    for raw in paths:
        if not raw:
            continue
        rel = Path(os.fsdecode(raw))
        src, dst = source / rel, target / rel
        if not src.is_file():  # sparse-checkout intentionally omits historical files
            continue
        if src.is_symlink() or not src.resolve().is_relative_to(source.resolve()):
            raise RuntimeError(f'源码包含链接，无法安全复制: {rel}')
        dst.parent.mkdir(parents=True, exist_ok=True)
        # Published defaults come from Git, not potentially private local User edits.
        if rel.parts[:2] == ('BetterGenshinImpact', 'User'):
            data = capture(['git', '-C', str(source), 'show', 'HEAD:' + rel.as_posix()])
            dst.write_bytes(data)
        else:
            shutil.copyfile(src, dst)
    guard.check(measure=True)


def untracked_sources():
    dirs = ['BetterGenshinImpact', 'MultiplayerHoeingAssistant', 'BgiCoordinatorServer',
            'Mistletoe.Shared', 'AutoHoeingUpdater', 'Test/OneDragonMigration/Core']
    output = capture(['git', '-C', str(ROOT), 'ls-files', '--others', '--exclude-standard', '-z', '--', *dirs])
    return [os.fsdecode(x) for x in output.split(b'\0') if x]


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


def main(version, reuse_web=None):
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
    output = ROOT / 'Build' / 'local-packages' / ident
    with storage.Session(ROOT, 'local-release-package', reserve_bytes=512 * 1024**2) as guard:
        work = guard.temporary_output()
        guard.track(output)
        output.mkdir(parents=True, exist_ok=False)
        marker = output / '构建尚未完成.txt'
        guard.write(marker, '构建尚未完成。请查看构建窗口中的错误信息。'.encode('utf-8'))
        # Recovery requests must be under the validation run, next to artifacts.
        records = work.parent / 'records'
        guard.track(records)
        records.mkdir()
        recovery = records / 'recovery'
        guard.entry['recovery_directories'] = [str(recovery)]
        guard.save()
        temp_dir = work / 'temp'
        temp_dir.mkdir()
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

        try:
            print('构建提交:', commit, '\n结果目录:', output, flush=True)
            source = work / 'source'
            source.mkdir()
            snapshot(ROOT, source, guard)
            project = source / 'BetterGenshinImpact' / 'BetterGenshinImpact.csproj'
            contents = project.read_text(encoding='utf-8-sig')
            contents, count = re.subn(r'<Version>.*?</Version>', f'<Version>{version}</Version>', contents, count=1)
            if count != 1:
                raise RuntimeError('未找到主程序版本号')
            project.write_text(contents, encoding='utf-8')
            seven = shutil.which('7z')
            if not seven:
                seven = str(work / '7zr.exe')
                download(asset(latest('ip7z/7zip'), r'7zr\.exe'), Path(seven), guard)
            builder = work / 'kachina-builder.exe'
            download(asset(latest('YuehaiTeam/kachina-installer'), r'kachina-builder\.exe'), builder, guard)
            web = []
            if reuse_web is None:
                node_index = api('https://nodejs.org/dist/index.json')
                node_version = next(x['version'] for x in node_index if x['version'].startswith('v20.'))
                node_zip = work / 'node.zip'
                download(f'https://nodejs.org/dist/{node_version}/node-{node_version}-win-x64.zip', node_zip, guard)
                unzip_safe(node_zip, work / 'node')
                node_zip.unlink()
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
            archive = work / f'BetterGI_v{version}.7z'
            run('生成 7z 压缩包', [seven, 'a', archive, 'BetterGI', '-t7z', '-mx=5', '-mf=BCJ2', '-r', '-y'], dist)
            run('检查压缩包完整性', [seven, 't', archive], work)
            (output / '构建信息.json').write_text(json.dumps({'version': version, 'source_commit': commit, 'node': node_version, 'web_revisions': web_revisions, 'tracked_worktree_edits_included': True, 'untracked_files_included': False}, ensure_ascii=False, indent=2), encoding='utf-8')
            os.replace(archive, output / archive.name)
            marker.unlink()
            print('\n压缩包构建完成，文件位置:', output, flush=True)
        finally:
            # Only remove this invocation's fresh workspace after all owned children ended.
            if not guard.process_unknown and not recovery.joinpath('inflight.json').exists():
                original_error = sys.exc_info()[0] is not None
                try:
                    remove_owned_workspace(work)
                except OSError as cleanup_error:
                    print(f'临时目录清理失败，已保留在 {work}: {cleanup_error}', file=sys.stderr, flush=True)
                    if not original_error:
                        raise


if __name__ == '__main__':
    try:
        value = sys.argv[1] if len(sys.argv) > 1 else input('请输入版本号（例如 0.66.3-alpha.1）: ').strip()
        main(value)
    except Exception as exc:
        print('\n构建未完成:', exc, file=sys.stderr, flush=True)
        sys.exit(1)
