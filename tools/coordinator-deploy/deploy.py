"""Ship the committed coordinator source to the configured SSH target."""

import argparse
import hashlib
from pathlib import Path
import shlex
import subprocess
import sys


TOOL_DIR = Path(__file__).resolve().parent
REPO = TOOL_DIR.parents[1]
SSH_TARGET = "bgi-coordinator"
SERVER_ROOT = "/opt/bgi-coordinator"
HEALTH_URL = "http://127.0.0.1:8080/health"


def git(*args):
    return subprocess.check_output(["git", "-C", str(REPO), *args])


def main():
    parser = argparse.ArgumentParser(description="更新腾讯云 BetterGI 协调服务器")
    parser.add_argument("--check", action="store_true", help="只读检查连接、现网健康和部署前提")
    args = parser.parse_args()

    script = (TOOL_DIR / "apply.sh").read_text(encoding="utf-8")
    archive = b""
    revision = digest = ""
    print(f"目标：{SSH_TARGET}:{SERVER_ROOT}", flush=True)
    if not args.check:
        revision = git("rev-parse", "--verify", "HEAD^{commit}").decode("ascii").strip()
        print(f"版本：{revision}（当前已提交的协调服务器源码）", flush=True)
        if git("status", "--porcelain", "--untracked-files=normal", "--", "BgiCoordinatorServer"):
            print("检测到未提交的协调服务器改动，本次不会包含。", flush=True)
        archive = git("archive", "--format=tar.gz", f"{revision}:BgiCoordinatorServer")
        digest = hashlib.sha256(archive).hexdigest()

    command = shlex.join([
        "bash", "-c", script, "coordinator-deploy",
        "check" if args.check else "deploy", SERVER_ROOT, HEALTH_URL, revision, digest,
    ])
    result = subprocess.run([
        "ssh", "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
        "-o", "UpdateHostKeys=no", "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=15",
        "-o", "ServerAliveCountMax=3", "-o", "LogLevel=ERROR", SSH_TARGET, command,
    ], input=archive, check=False)
    if result.returncode == 255:
        print("SSH 连接失败或中断，无法确认本次结果。请先运行 --check 核对现网版本。", file=sys.stderr)
    return result.returncode


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, subprocess.CalledProcessError) as error:
        print(f"无法完成操作：{error}", file=sys.stderr)
        sys.exit(1)
