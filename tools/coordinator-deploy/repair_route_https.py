"""本次现网 HTTPS 修复入口；只用于已确认拓扑，拒绝覆盖未知状态。

证书和无口令备份须先完成。脚本不申请证书、不猜测正式客户端版本。
默认不执行；--apply 明确执行一次，失败保留证据并尝试恢复原服务配置。
"""
import argparse
from contextlib import contextmanager
import hashlib
import http.cookiejar
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import shlex
import socket
import subprocess
import sys
import time
import urllib.request

ROOT = Path('/opt/bgi-coordinator')
BACKUP = ROOT / 'backups/route-https-20261117-01'
PROXY = 'nginx-app-1'
CONTAINER = 'bgi-coordinator-coordinator-1'
ORIGIN = 'https://www.autobgi.cn'
CUSTOM = '/data/nginx/custom/'
FILES = ['bgi-coordinator-proxy.inc', 'bgi-coordinator-https.conf', 'http.conf']
@contextmanager
def deployment_lock():
    import fcntl
    descriptor = os.open(ROOT, os.O_RDONLY)
    try:
        fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield
    finally:
        os.close(descriptor)


def run(args, *, data=None, env=None, timeout=60):
    result = subprocess.run(args, input=data, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, env=env, timeout=timeout)
    if result.returncode:
        # 某些 Compose 报错包含展开后的变量，不把未知输出写入聊天或记录。
        raise RuntimeError(f'{args[0]} failed (exit {result.returncode}); output withheld')
    return result.stdout


def container_info():
    return json.loads(run(['docker', 'inspect', CONTAINER]))[0]


def current_environment(info):
    return dict(line.split('=', 1) for line in info['Config']['Env'] if '=' in line)


def admin_client(password, origin):
    client = urllib.request.build_opener(urllib.request.ProxyHandler({}),
        urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    request = urllib.request.Request(origin + '/api/admin/login',
        data=json.dumps({'password': password}).encode(),
        headers={'Content-Type': 'application/json'})
    with client.open(request, timeout=10) as response:
        if response.status != 204:
            raise RuntimeError('Administrator authentication not confirmed')
    return client


def assert_idle(password):
    client = admin_client(password, 'http://127.0.0.1:8080')
    with client.open('http://127.0.0.1:8080/api/admin/rooms', timeout=10) as response:
        snapshot = json.load(response)
    running = snapshot['startedRoomCount'] or any(
        room['autoHoeingRunning'] or any(member['autoHoeingRunning']
            for member in room['members']) for room in snapshot['controlRooms'])
    if running:
        raise RuntimeError('A hoeing task is running; maintenance deferred without restart')
    print('No active hoeing tasks; connected assistant members:',
          snapshot['connectedControlMemberCount'], flush=True)


def backend_connections():
    table = run(['docker', 'exec', CONTAINER, 'cat', '/proc/net/tcp', '/proc/net/tcp6']).decode()
    result = set()
    for line in table.splitlines():
        fields = line.split()
        if len(fields) >= 4 and fields[0] != 'sl':
            local, remote, state = fields[1:4]
            if int(local.split(':')[1], 16) == 80 and state == '01':
                result.add((local, remote))
    return result


def actual_proxy_address():
    # Docker userland-proxy 会重写源端口，不能用宿主源端口匹配容器连接。
    # 先取基线，再保持一个已收到健康响应的连接；新增连接的来源须唯一，否则停止。
    before = backend_connections()
    with socket.create_connection(('127.0.0.1', 8080), timeout=5) as connection:
        connection.sendall(b'GET /health HTTP/1.1\r\nHost: localhost\r\nConnection: keep-alive\r\n\r\n')
        if not connection.recv(4096).startswith(b'HTTP/1.1 200 '):
            raise RuntimeError('Direct backend probe did not return a healthy response')
        candidates = set()
        for _, remote in backend_connections() - before:
            raw = bytes.fromhex(remote.split(':')[0])
            if len(raw) == 4:
                observed = ipaddress.IPv4Address(raw[::-1])
            elif len(raw) == 16:
                observed = ipaddress.IPv6Address(b''.join(raw[i:i+4][::-1] for i in range(0, 16, 4)))
                observed = observed.ipv4_mapped or observed
            else:
                raise RuntimeError('Unrecognized direct proxy address format')
            candidates.add(str(observed))
        if len(candidates) != 1:
            raise RuntimeError('Cannot establish the actual direct proxy source IP')
        return candidates.pop()


def nginx_write(name, text, *, replace=False):
    if name not in FILES:
        raise ValueError('Unmanaged nginx path')
    if replace:
        raise ValueError('Replacing an existing nginx file is not supported')
    # 完整写入独占临时文件，再以硬链接无覆盖提交；中断不会留下半份有效配置。
    command = ('umask 022; mkdir -p /data/nginx/custom || exit 20; '
        'tmp=$(mktemp "$1.bgi-XXXXXXXX") || exit 21; '
        'trap \'rm -f -- "$tmp"\' EXIT; '
        'cat > "$tmp" || exit 22; chmod 644 "$tmp" || exit 23; '
        'ln -- "$tmp" "$1" || exit 24')
    run(['docker', 'exec', '-i', PROXY, 'sh', '-c', command, 'bgi-route-config', CUSTOM + name],
        data=text.encode())


def nginx_remove_owned(owned):
    present = []
    for name, text in owned.items():
        if name not in FILES:
            raise ValueError('Unmanaged nginx path')
        # 写入前失败的文件可以不存在；只删除内容仍精确属于本次的文件。
        actual = run(['docker', 'exec', PROXY, 'sh', '-c',
            'if test -e "$1" || test -L "$1"; then cat -- "$1"; else printf missing; fi',
            'bgi-rollback', CUSTOM + name])
        if actual == b'missing':
            continue
        if hashlib.sha256(actual).digest() != hashlib.sha256(text.encode()).digest():
            raise RuntimeError('Managed nginx config changed; refusing automatic removal')
        present.append(name)
    for name in present:
        run(['docker', 'exec', PROXY, 'rm', '--', CUSTOM + name])
    run(['docker', 'exec', PROXY, 'nginx', '-t'])
    run(['docker', 'exec', PROXY, 'nginx', '-s', 'reload'])


def manifest(path):
    result = {}
    for item in path.rglob('*'):
        if item.is_symlink():
            raise RuntimeError('Route storage contains a link; manual review required')
        if item.is_file():
            result[item.relative_to(path).as_posix()] = hashlib.sha256(item.read_bytes()).hexdigest()
    return result


def dotenv_value(text, key):
    matches = re.findall(r'^[ \t]*(?:export[ \t]+)?' + re.escape(key)
        + r'[ \t]*=[ \t]*([^\r\n]*)$', text.replace('\r\n', '\n'), re.M)
    if len(matches) > 1:
        raise RuntimeError('Duplicate route setting in .env')
    if not matches:
        return None
    words = shlex.split(matches[0], comments=True)
    if len(words) > 1:
        raise RuntimeError('Ambiguous route setting in .env')
    return words[0] if words else ''


def wait_health(origin):
    client = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    for _ in range(25):
        try:
            with client.open(origin + '/health', timeout=3) as response:
                if json.load(response).get('status') == 'BgiCoordinatorServer running':
                    return
        except (OSError, ValueError):
            pass
        time.sleep(1)
    raise RuntimeError('Health did not recover within the maintenance interval')


def apply(payload):
    with deployment_lock():
        state_path = BACKUP / 'repair-state.json'
        state = json.loads(state_path.read_text())
        if state.get('activationStarted'):
            raise RuntimeError('Previous activation exists; inspect its result before retrying')
        if not state.get('certificateIssued') or not state.get('backupVerified'):
            raise RuntimeError('Verified backup and certificate are required')
        original = (BACKUP / 'docker-compose.override.yml').read_bytes()
        override = ROOT / 'docker-compose.override.yml'
        if override.read_bytes() != original:
            raise RuntimeError('Compose override changed since backup')
        if (ROOT / 'docker-compose.yml').read_bytes() != (BACKUP / 'docker-compose.yml').read_bytes():
            raise RuntimeError('Compose base changed since backup')
        info = container_info()
        if info['Image'] != state['sourceImage']:
            raise RuntimeError('Running source image changed since backup')
        environment = current_environment(info)
        password = environment['ADMIN_MONITOR_PASSWORD']
        assert_idle(password)
        direct_proxy = actual_proxy_address()
        print('Verified direct proxy address:', direct_proxy, flush=True)
        with socket.socket() as probe:
            probe.bind(('127.0.0.1', 18080))
        run(['docker', 'exec', PROXY, 'nginx', '-t'])
        for name in FILES:
            run(['docker', 'exec', PROXY, 'sh', '-c', 'test ! -e "$1"', 'bgi-check', CUSTOM + name])
        storage = ROOT / 'route-packages'
        if storage.exists():
            raise RuntimeError('Persistent route destination already exists; refusing overwrite')
        storage.mkdir(mode=0o700)
        run(['docker', 'cp', CONTAINER + ':/app/data/route-packages/.', str(storage)])
        saved_manifest = manifest(BACKUP / 'route-packages-before')
        if manifest(storage) != saved_manifest:
            raise RuntimeError('Live route data differs from preserved backup; no restart performed')

        env_path = ROOT / '.env'
        if env_path.is_symlink():
            raise RuntimeError('Unexpected .env link; refusing credential changes')
        env_text = env_path.read_text() if env_path.exists() else ''
        token = dotenv_value(env_text, 'ROUTE_UPLOAD_TOKEN')
        if token is not None and len(token) < 32:
            raise RuntimeError('Existing upload token is too short; refusing silent replacement')
        add_token = token is None
        if add_token:
            token = secrets.token_urlsafe(48)
        if token == password:
            raise RuntimeError('Upload-only credential must differ from administrator credential')
        required = dotenv_value(env_text, 'HOEING_REQUIRED_CLIENT_VERSION') or ''
        # 不拿助手版本、源码默认值冒充正式 BGI 产物版本。
        print('Exact released BGI version:', required or 'not yet identified', flush=True)
        updated = original.decode('utf-8')
        if updated.count('"8080:80"') != 1 or re.search(r'^\s*environment\s*:', updated, re.M):
            raise RuntimeError('Unexpected override layout; refusing broad rewrite')
        updated = updated.replace('"8080:80"', '"127.0.0.1:18080:80"')
        updated = updated.rstrip() + '\n      - ./route-packages:/app/data/route-packages\n' + (
            '    environment:\n'
            '      ROUTE_PACKAGE_ROOT: /app/data/route-packages\n'
            '      ROUTE_UPLOAD_TOKEN: ${ROUTE_UPLOAD_TOKEN:?Upload-only credential is required}\n'
            '      HOEING_REQUIRED_CLIENT_VERSION: ${HOEING_REQUIRED_CLIENT_VERSION:-}\n'
            f'      ROUTE_TRUSTED_PROXY_IPS: "{direct_proxy}"\n')
        owned = {}
        compose_env = os.environ.copy()
        compose_env['ADMIN_MONITOR_PASSWORD'] = password
        compose_env['ROUTE_UPLOAD_TOKEN'] = token
        compose_env['HOEING_REQUIRED_CLIENT_VERSION'] = required
        compose = ['docker', 'compose', '--project-directory', str(ROOT)]
        switched = False
        try:
            owned['bgi-coordinator-proxy.inc'] = payload['location']
            nginx_write('bgi-coordinator-proxy.inc', payload['location'])
            owned['bgi-coordinator-https.conf'] = payload['proxy']
            nginx_write('bgi-coordinator-https.conf', payload['proxy'])
            owned['http.conf'] = 'include /data/nginx/custom/bgi-coordinator-https.conf;\n'
            nginx_write('http.conf', owned['http.conf'])
            run(['docker', 'exec', PROXY, 'nginx', '-t'])
            override.write_bytes(updated.encode('utf-8'))
            run(compose + ['config', '-q'], env=compose_env)
            assert_idle(password)
            if (env_path.read_text() if env_path.exists() else '') != env_text:
                raise RuntimeError('.env changed during preparation; refusing credential changes')
            if add_token:
                flags = os.O_WRONLY | os.O_APPEND | os.O_CREAT | os.O_NOFOLLOW
                with os.fdopen(os.open(env_path, flags, 0o600), 'a') as stream:
                    stream.write(('\n' if env_text and not env_text.endswith('\n') else '')
                        + '# BGI route HTTPS repair: generated upload-only credential\n'
                        + 'ROUTE_UPLOAD_TOKEN=' + token + '\n')
                    stream.flush()
                    os.fsync(stream.fileno())
                env_path.chmod(0o600)
                state['uploadTokenAdded'] = True
            state['activationStarted'] = True
            state['directProxyIp'] = direct_proxy
            state_path.write_text(json.dumps(state, indent=2) + '\n')
            switched = True
            run(compose + ['up', '-d', '--no-deps', '--no-build', '--pull', 'never',
                           '--force-recreate', 'coordinator'], env=compose_env, timeout=150)
            wait_health('http://127.0.0.1:18080')
            run(['docker', 'exec', PROXY, 'nginx', '-s', 'reload'])
            wait_health(ORIGIN)
            wait_health('http://127.0.0.1:8080')
            client = admin_client(password, ORIGIN)
            with client.open(ORIGIN + '/api/admin/routes', timeout=10) as response:
                catalog = json.load(response)
            print('HTTPS administrator route catalog verified; packages:', len(catalog['packages']), flush=True)
            if container_info()['Image'] != state['sourceImage']:
                raise RuntimeError('Image changed unexpectedly')
            state['activated'] = True
            state['requiredClientVersion'] = required
            state['publicOrigin'] = ORIGIN
            state_path.write_text(json.dumps(state, indent=2) + '\n')
            state_path.chmod(0o600)
            print('HTTPS and persistent route storage activated; no package was published', flush=True)
        except Exception:
            # 先释放代理新占用的 8080，再恢复旧容器的同一端口，避免回退端口冲突。
            print('Activation not confirmed; attempting configuration rollback', flush=True)
            try:
                nginx_remove_owned(owned)
                override.write_bytes(original)
                if switched:
                    run(compose + ['up', '-d', '--no-deps', '--no-build', '--pull', 'never',
                                   '--force-recreate', 'coordinator'], env=compose_env, timeout=150)
                    wait_health('http://127.0.0.1:8080')
                state['rollbackConfirmed'] = True
                print('Original coordinator health restored', flush=True)
            except Exception:
                state['rollbackConfirmed'] = False
                print('Rollback not confirmed; retain backup and inspect before retrying', flush=True)
            state_path.write_text(json.dumps(state, indent=2) + '\n')
            state_path.chmod(0o600)
            raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--remote', action='store_true', help=argparse.SUPPRESS)
    args = parser.parse_args()
    if not args.apply:
        parser.error('必须显式指定 --apply；本入口不会自动修复或覆盖未知部署')
    if args.remote:
        apply(json.load(sys.stdin))
        return 0
    here = Path(__file__).resolve().parent
    source = Path(__file__).read_text(encoding='utf-8')
    # Windows 本机没有 fcntl；只在 Linux 远端 apply 内导入。
    payload = {'proxy': (here / 'route-proxy.conf.example').read_text(encoding='utf-8'),
        'location': (here / 'route-proxy-location.conf.example').read_text(encoding='utf-8')}
    # Windows OpenSSH 的 argv 引号处理不适合长 -c 源码；固定命令，源码及非秘密参数走 stdin。
    wrapper = ('scope={"__name__":"route_https_repair"}\n'
        + 'exec(compile(' + repr(source) + ', "route_https_repair", "exec"),scope)\n'
        + 'try:\n    scope["apply"](' + repr(payload) + ')\n'
        + 'except Exception as error:\n'
        + '    print("Repair did not complete:", type(error).__name__, str(error), file=__import__("sys").stderr)\n'
        + '    raise SystemExit(1)\n')
    return subprocess.run(['ssh', '-T', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes',
        '-o', 'UpdateHostKeys=no', '-o', 'ConnectTimeout=10', '-o', 'ServerAliveInterval=15',
        '-o', 'ServerAliveCountMax=3', '-o', 'LogLevel=ERROR', 'bgi-coordinator', 'python3 -I -B -'],
        input=wrapper.encode(), check=False).returncode


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except Exception as error:
        print('Repair did not complete:', type(error).__name__, str(error), file=sys.stderr)
        raise SystemExit(1)
