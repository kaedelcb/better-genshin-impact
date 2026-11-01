"""Publish verified BetterGI Actions artifacts; never build or execute their contents."""
from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
from urllib.parse import quote
import zipfile

REPOSITORY = "kaedelcb/better-genshin-impact"
WORKFLOW = ".github/workflows/publish.yml"
VERSION = r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?"
MAX_FILE = 2 * 1024**3
KINDS = {"BetterGI_7z": ("BetterGI_v{version}.7z", b"7z\xbc\xaf\x27\x1c"),
         "BetterGI_Install": ("BetterGI.Install.{version}.exe", b"MZ")}


class ReleaseError(RuntimeError):
    pass


class NotFound(ReleaseError):
    pass


def require(condition, message):
    if not condition:
        raise ReleaseError(message)


class GitHub:
    """Use the runner's gh and ephemeral GH_TOKEN, without creating a login."""
    def __init__(self):
        self.env = os.environ.copy()
        self.env.pop("GH_DEBUG", None)
        self.env["GH_NO_UPDATE_NOTIFIER"] = "1"

    def command(self, args, *, data=None, timeout=1800):
        return subprocess.run(["gh", *args], input=data, stdout=subprocess.PIPE,
                              stderr=subprocess.PIPE, env=self.env, timeout=timeout)

    def request(self, method, path, payload=None):
        args = ["api", path, "--method", method, "--header", "Accept: application/vnd.github+json",
                "--header", "X-GitHub-Api-Version: 2022-11-28"]
        data = None
        if payload is not None:
            args += ["--input", "-"]
            data = json.dumps(payload).encode("utf-8")
        # Only read requests are retried. A failed mutation is never resubmitted.
        for attempt in range(3 if method == "GET" else 1):
            result = self.command(args, data=data, timeout=120)
            if not result.returncode:
                return json.loads(result.stdout) if result.stdout.strip() else None
            error = result.stderr.decode("utf-8", "replace")
            if "HTTP 404" in error:
                raise NotFound("GitHub resource not found")
            transient = any(s in error.lower() for s in ("eof", "timeout", "timed out", "connection reset"))
            if method != "GET" or not transient or attempt == 2:
                # No raw request headers, credentials, or signed URLs enter logs.
                raise ReleaseError(f"GitHub {method} failed (exit {result.returncode}); inspect server state before retrying writes")
            time.sleep(2 * (attempt + 1))
        raise ReleaseError("GitHub read failed")

    def get(self, path):
        return self.request("GET", path)

    def write(self, method, path, payload):
        return self.request(method, path, payload)

    def pages(self, path, key=None):
        for page in range(1, 101):
            value = self.get(f"{path}{'&' if '?' in path else '?'}per_page=100&page={page}")
            items = value[key] if key else value
            require(isinstance(items, list), "Invalid paginated GitHub response")
            yield from items
            if len(items) < 100:
                return
        raise ReleaseError("GitHub pagination exceeded the safety limit")

    def download(self, artifact, destination):
        process = subprocess.Popen(["gh", "api", f"repos/{REPOSITORY}/actions/artifacts/{artifact['id']}/zip"],
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=self.env)
        digest, count = hashlib.sha256(), 0
        try:
            with destination.open("xb") as output:
                while chunk := process.stdout.read(1024**2):
                    count += len(chunk)
                    require(count <= artifact["size_in_bytes"], "Artifact exceeds its declared ZIP size")
                    digest.update(chunk)
                    output.write(chunk)
            process.stderr.read()  # Discard raw CLI errors; they may include signed URLs.
            require(process.wait(timeout=30) == 0, "Artifact download failed")
            require(count == artifact["size_in_bytes"], "Artifact ZIP was truncated")
            require("sha256:" + digest.hexdigest() == artifact["digest"], "Artifact ZIP SHA256 mismatch")
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=10)

    def upload(self, tag, path):
        return self.command(["release", "upload", tag, str(path), "--repo", REPOSITORY]).returncode


@dataclass(frozen=True)
class Build:
    run_id: int
    attempt: int
    sha: str
    version: str
    artifacts: tuple
    requested_draft: bool = False


def run_id(value):
    text = str(value)
    require(re.fullmatch(r"[1-9][0-9]{0,18}", text) is not None, "构建编号必须是 Actions 地址末尾的正整数")
    return int(text)


def inspect_build(api, selected_id):
    selected_id = run_id(selected_id)
    repo = api.get(f"repos/{REPOSITORY}")
    workflow = api.get(f"repos/{REPOSITORY}/actions/workflows/publish.yml")
    run = api.get(f"repos/{REPOSITORY}/actions/runs/{selected_id}")
    require(isinstance(run.get("head_repository"), dict) and
            run["repository"]["id"] == repo["id"] == run["head_repository"]["id"], "拒绝其他仓库或 fork 的构建")
    require(run["workflow_id"] == workflow["id"] and run["path"] == WORKFLOW, "必须选择本仓库 BetterGI Publish 构建")
    require(run["event"] == "workflow_dispatch", "拒绝非受信任的构建触发来源")
    require(run["status"] == "completed" and run["conclusion"] == "success", "构建必须已完成且成功")
    require(re.fullmatch(r"main(?:-OldTeaBag-B[0-9]+)?", run["head_branch"] or "") is not None, "构建分支不在可信名单中")
    require(re.fullmatch(r"[0-9a-f]{40}", run["head_sha"] or "") is not None, "构建源提交无效")
    title = run.get("display_title") or run.get("name") or ""
    match = re.fullmatch(r"BetterGI (" + VERSION + r")", title)
    require(match is not None, "构建标题中的版本号不符合发布约定")
    version = match.group(1)
    attempt = run["run_attempt"]
    require(type(attempt) is int and attempt > 0, "构建尝试编号无效")
    jobs = list(api.pages(f"repos/{REPOSITORY}/actions/runs/{selected_id}/attempts/{attempt}/jobs", "jobs"))
    for name in ("build_dist", "build_installer"):
        found = [j for j in jobs if j["name"] == name]
        require(len(found) == 1 and found[0]["conclusion"] == "success", f"必需任务 {name} 未成功")
    artifacts = list(api.pages(f"repos/{REPOSITORY}/actions/runs/{selected_id}/artifacts", "artifacts"))
    chosen = []
    for name in KINDS:
        found = [a for a in artifacts if a["name"] == name]
        require(len(found) == 1, f"构建必须恰好包含一个 {name} 产物")
        artifact = found[0]
        require(not artifact["expired"], f"{name} 已过期，请重新构建")
        require(type(artifact["id"]) is int and artifact["id"] > 0, "产物编号无效")
        require(0 < artifact["size_in_bytes"] <= MAX_FILE, "产物 ZIP 超出允许大小")
        require(re.fullmatch(r"sha256:[0-9a-f]{64}", artifact.get("digest") or "") is not None, "缺少可靠的产物 SHA256")
        require(artifact["workflow_run"]["id"] == selected_id and artifact["workflow_run"]["head_sha"] == run["head_sha"], "产物不属于所选构建提交")
        chosen.append(artifact)
    requested_draft = any(j["name"] == "create_release" and j["conclusion"] == "success" for j in jobs)
    return Build(selected_id, attempt, run["head_sha"], version, tuple(chosen), requested_draft)


def tag_commit(api, tag):
    try:
        obj = api.get(f"repos/{REPOSITORY}/git/ref/tags/{quote(tag, safe='')}")["object"]
    except NotFound:
        return None
    for _ in range(5):
        if obj["type"] == "commit":
            return obj["sha"]
        require(obj["type"] == "tag", "版本标签不是提交或附注标签")
        obj = api.get(f"repos/{REPOSITORY}/git/tags/{obj['sha']}")["object"]
    raise ReleaseError("版本标签嵌套过深")


def payload_names(build):
    return {template.format(version=build.version) for template, _ in KINDS.values()}


def existing_release(api, build):
    releases = [r for r in api.pages(f"repos/{REPOSITORY}/releases") if r["tag_name"] == build.version]
    require(len(releases) <= 1, "发现同版本的多个 Release，请人工检查")
    if releases:
        release = releases[0]
        require(not release["draft"], "同版本草稿已存在：保留现状，请人工检查，不覆盖或续传")
        require(tag_commit(api, build.version) == build.sha, "已有版本标签不属于所选构建提交")
        require(release["prerelease"] is ("-" in build.version), "已有版本预发布标记不一致，不自动修改")
        assets = release["assets"]
        require(payload_names(build).issubset({a["name"] for a in assets}), "已有 Release 附件不完整，不自动修改")
        for asset in assets:
            if asset["name"] in payload_names(build):
                require(sum(a["name"] == asset["name"] for a in assets) == 1, "已有版本附件重复")
                require(asset["state"] == "uploaded" and asset["size"] > 0 and
                        re.fullmatch(r"sha256:[0-9a-f]{64}", asset.get("digest") or ""), "已有 Release 附件状态不完整")
        return release
    require(tag_commit(api, build.version) is None, "同版本标签已存在但没有完整公开 Release，不自动占用或覆盖")
    return None


def stage_payload(api, artifact, build, directory):
    archive = directory / f"{artifact['id']}.zip"
    api.download(artifact, archive)
    # Independently verify even when a different adapter supplied the download.
    require(archive.stat().st_size == artifact["size_in_bytes"], "产物 ZIP 大小不一致")
    with archive.open("rb") as source:
        digest = hashlib.file_digest(source, "sha256").hexdigest()
    require("sha256:" + digest == artifact["digest"], "产物 ZIP SHA256 不一致")
    template, magic = KINDS[artifact["name"]]
    filename = template.format(version=build.version)
    with zipfile.ZipFile(archive) as bundle:
        entries = bundle.infolist()
        require(len(entries) == 1, "产物必须只包含一个原始发布附件")
        info = entries[0]
        require(info.filename == filename and not info.is_dir(), "产物内部文件名或版本不匹配")
        require(not info.flag_bits & 1 and ((info.external_attr >> 16) & 0o170000) != 0o120000, "拒绝加密文件或符号链接")
        require(0 < info.file_size <= MAX_FILE, "发布附件大小无效")
        target, count, inner_hash = directory / filename, 0, hashlib.sha256()
        with bundle.open(info) as source, target.open("xb") as output:
            while chunk := source.read(1024**2):
                count += len(chunk)
                require(count <= info.file_size, "发布附件超过 ZIP 声明大小")
                inner_hash.update(chunk)
                output.write(chunk)
        require(count == info.file_size, "发布附件被截断")
    with target.open("rb") as source:
        require(source.read(len(magic)) == magic, "发布附件文件格式不匹配")
    return {"name": filename, "path": target, "size": count, "digest": "sha256:" + inner_hash.hexdigest()}


def verify_remote(release, build, files, *, public=False):
    require(release["tag_name"] == build.version and release["target_commitish"] == build.sha, "Release 源提交或标签被改变")
    require(release["prerelease"] is ("-" in build.version), "Release 预发布标记不正确")
    expected = {f["name"]: f for f in files}
    assets = release["assets"]
    require(len(assets) == len(expected), "服务器附件数量不正确")
    require({a["name"] for a in assets} == set(expected), "服务器附件名称不正确")
    for asset in assets:
        local = expected[asset["name"]]
        require(asset["state"] == "uploaded" and asset["size"] == local["size"] and asset.get("digest") == local["digest"], "服务器附件大小或 SHA256 不一致")
    require(release["draft"] is (not public), "Release 草稿状态不正确")


def publish(api, build, directory, notes=""):
    require(isinstance(notes, str) and len(notes.encode("utf-8")) <= 32000, "更新说明无效或过长")
    prior = existing_release(api, build)
    if prior:
        return "already-published", prior
    files = [stage_payload(api, artifact, build, directory) for artifact in build.artifacts]
    # Recheck after download, then reserve the exact tag atomically; never move a ref.
    prior = existing_release(api, build)
    if prior:
        return "already-published", prior
    current_run = api.get(f"repos/{REPOSITORY}/actions/runs/{build.run_id}")
    require(current_run["status"] == "completed" and current_run["conclusion"] == "success" and
            current_run["run_attempt"] == build.attempt and current_run["head_sha"] == build.sha,
            "下载后构建状态或尝试编号已变化")
    api.write("POST", f"repos/{REPOSITORY}/git/refs", {"ref": "refs/tags/" + build.version, "sha": build.sha})
    require(tag_commit(api, build.version) == build.sha, "标签提交核对失败")
    body = notes.strip()
    require(len(body.encode("utf-8")) <= 32000, "更新说明过长")
    body += ("\n\n" if body else "") + f"构建来源：[Actions #{build.run_id}](https://github.com/{REPOSITORY}/actions/runs/{build.run_id})\n\n源提交：`{build.sha}`。附件与该构建原始产物一致。"
    release = api.write("POST", f"repos/{REPOSITORY}/releases", {
        "tag_name": build.version, "target_commitish": build.sha,
        "name": "BetterGI v" + build.version, "body": body,
        "draft": True, "prerelease": "-" in build.version, "make_latest": "false"})
    release_id = release["id"]
    for item in files:
        result = api.upload(build.version, item["path"])
        current = api.get(f"repos/{REPOSITORY}/releases/{release_id}")
        found = [a for a in current["assets"] if a["name"] == item["name"]]
        require(len(found) == 1 and found[0]["state"] == "uploaded" and found[0]["size"] == item["size"] and found[0].get("digest") == item["digest"], f"附件上传未确认（exit {result}），保留草稿，不重传或覆盖")
    current = api.get(f"repos/{REPOSITORY}/releases/{release_id}")
    verify_remote(current, build, files)
    require(tag_commit(api, build.version) == build.sha, "公开前标签提交被改变，保留草稿")
    api.write("PATCH", f"repos/{REPOSITORY}/releases/{release_id}", {
        "draft": False, "prerelease": "-" in build.version,
        "make_latest": "false" if "-" in build.version else "legacy"})
    current = api.get(f"repos/{REPOSITORY}/releases/{release_id}")
    verify_remote(current, build, files, public=True)
    require(tag_commit(api, build.version) == build.sha, "公开后标签提交核对失败")
    return "published", current


def select_run(event_name, event):
    if event_name == "workflow_run":
        return run_id(event["workflow_run"]["id"]), ""
    require(event_name == "workflow_dispatch", "不支持的触发入口")
    inputs = event.get("inputs") or {}
    notes = inputs.get("notes") or ""
    require(isinstance(notes, str) and len(notes.encode("utf-8")) <= 32000, "更新说明无效或过长")
    return run_id(inputs.get("run_id", "")), notes


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("inspect", "publish"))
    mode = parser.parse_args().mode
    require(os.environ.get("GITHUB_ACTIONS") == "true", "真实发布仅允许在 GitHub Actions runner 中执行")
    require(os.environ.get("GITHUB_REPOSITORY") == REPOSITORY, "仓库不受信任")
    require(bool(os.environ.get("GH_TOKEN")), "缺少临时 GITHUB_TOKEN")
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
    event_name = os.environ["GITHUB_EVENT_NAME"]
    selected, notes = select_run(event_name, event)
    api = GitHub()
    repo = api.get(f"repos/{REPOSITORY}")
    require(os.environ["GITHUB_REF"] == "refs/heads/" + repo["default_branch"], "发布控制器必须从默认分支运行")
    build = inspect_build(api, selected)
    should_publish = not (event_name == "workflow_run" and build.requested_draft)
    if mode == "inspect":
        values = {"version": build.version, "sha": build.sha, "attempt": str(build.attempt),
                  "publish": str(should_publish).lower()}
        with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as output:
            for key, value in values.items():
                output.write(f"{key}={value}\n")
        message = "源构建和两项产物元数据已核实。" if should_publish else "源构建明确选择了创建草稿，自动入口保留该选择，不公开。"
    else:
        require(should_publish, "源构建选择保留草稿")
        require(build.version == os.environ["EXPECTED_VERSION"] and build.sha == os.environ["EXPECTED_SHA"] and
                str(build.attempt) == os.environ["EXPECTED_ATTEMPT"], "检查后构建身份发生变化")
        with tempfile.TemporaryDirectory(prefix="bettergi-release-", dir=os.environ["RUNNER_TEMP"]) as folder:
            status, release = publish(api, build, Path(folder), notes)
        message = f"{'已发布' if status == 'published' else '同版本已完整发布，未做修改'}：[BetterGI v{build.version}]({release['html_url']})"
    print(message)
    with Path(os.environ["GITHUB_STEP_SUMMARY"]).open("a", encoding="utf-8") as summary:
        summary.write(message + "\n")


if __name__ == "__main__":
    try:
        main()
    except (ReleaseError, KeyError, ValueError, OSError, zipfile.BadZipFile, subprocess.TimeoutExpired) as error:
        print(f"::error::{type(error).__name__}: {error}")
        raise SystemExit(1)
