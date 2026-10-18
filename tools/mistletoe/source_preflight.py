"""Read-only checkout and commit-baseline checks; never repairs a repository."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET


ARCHIVE_ROOTS = {"_workflow", "_batch18", "_batch19", "_batch21", "_ev1", "_ev2", "_ev3"}
REQUIRED_POLICY = {
    "_workflow/storage-policy.json",
    "_workflow/usable-delivery-20261003/DELIVERY-FIRST-POLICY.md",
}
PROJECT_SUFFIXES = {".csproj", ".props", ".targets"}


class GitError(RuntimeError):
    pass


def git(root: Path, *args: str, allowed=(0,), input_data: bytes | None = None) -> subprocess.CompletedProcess:
    environment = os.environ.copy()
    environment["GIT_OPTIONAL_LOCKS"] = "0"
    process = subprocess.run(
        ["git", "-C", str(root), *args], stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, env=environment, input=input_data, check=False,
    )
    if process.returncode not in allowed:
        detail = process.stderr.decode("utf-8", "replace").strip()
        raise GitError(f"git {' '.join(args)}: {detail or process.returncode}")
    return process


def decoded(data: bytes) -> str:
    return data.decode("utf-8", "surrogateescape")


def is_archive(path: str) -> bool:
    return path not in REQUIRED_POLICY and path.split("/", 1)[0] in ARCHIVE_ROOTS


def is_generated(path: str) -> bool:
    parts = path.replace("\\", "/").lower().split("/")
    return any(part in {"bin", "obj"} for part in parts[:-1]) or parts[-1].endswith("_wpftmp.csproj")


def canonical(path: Path) -> str:
    return os.path.normcase(str(path.resolve()))


def issue(result: dict, code: str, message: str, *, severity="error", **details) -> None:
    result["issues"].append({"code": code, "severity": severity, "message": message, **details})


def check_baseline(root: Path, result: dict, baseline: str | None,
                   baseline_file: Path | None, allow_historical: bool) -> None:
    if baseline_file is not None:
        try:
            config = json.loads(baseline_file.read_text(encoding="utf-8-sig"))
            common_dir = config["repo_common_dir"]
            baseline = config["baseline_commit"]
            if not isinstance(common_dir, str) or not common_dir or not isinstance(baseline, str) or not baseline:
                raise ValueError("repo_common_dir and baseline_commit must be nonempty strings")
            configured_dir = Path(common_dir)
            if not configured_dir.is_absolute():
                raise ValueError("repo_common_dir must be an absolute path")
            if canonical(configured_dir) != canonical(Path(result["repo_common_dir"])):
                issue(result, "baseline_repository_mismatch", "基线配置不属于当前仓库，无法确认提交继承。", severity="unknown")
                result["baseline"] = {"status": "unconfirmed", "source": str(baseline_file)}
                return
        except (OSError, ValueError, KeyError, TypeError) as error:
            issue(result, "invalid_baseline_file", f"无法读取可信基线配置：{error}", severity="unknown")
            result["baseline"] = {"status": "unconfirmed", "source": str(baseline_file)}
            return
    if baseline is None:
        result["baseline"] = {"status": "not_requested"}
        return
    resolved = git(root, "rev-parse", "--verify", "--end-of-options", baseline + "^{commit}", allowed=(0, 128))
    if resolved.returncode:
        issue(result, "baseline_object_missing", "本地没有可确认的基线提交；未联网或自动更改仓库。", severity="unknown", requested=baseline)
        result["baseline"] = {"status": "unconfirmed", "requested": baseline}
        return
    commit = decoded(resolved.stdout).strip()
    ancestor = git(root, "merge-base", "--is-ancestor", commit, result["head"], allowed=(0, 1))
    result["baseline"] = {"status": "included" if ancestor.returncode == 0 else "not_included", "commit": commit}
    if ancestor.returncode:
        entries = decoded(git(root, "log", "--max-count=12", "--format=%h %s", f"{result['head']}..{commit}").stdout).splitlines()
        count = int(decoded(git(root, "rev-list", "--count", f"{result['head']}..{commit}").stdout))
        result["baseline"].update({"missing_commit_count": count, "missing_commits": entries})
        issue(result, "baseline_not_included", "当前分支未包含基线中的部分提交；这不直接证明功能缺失。",
              severity="warning" if allow_historical else "error", commits=entries, count=count)
        if allow_historical:
            result["baseline"]["status"] = "historical_allowed"


def check_references(root: Path, tracked: dict, result: dict) -> None:
    coverage = result["coverage"]
    tracked_keys = {os.path.normcase(name.replace("/", os.sep)) for name in tracked}
    for name in sorted(tracked):
        source = root / name
        if is_archive(name) or is_generated(name) or source.suffix.lower() not in PROJECT_SUFFIXES or not source.is_file():
            continue
        coverage["project_files_checked"] += 1
        try:
            document = ET.parse(source)
        except (OSError, ET.ParseError) as error:
            issue(result, "project_parse_failed", f"无法解析项目文件：{error}", path=name)
            continue
        for element in document.iter():
            tag = element.tag.rsplit("}", 1)[-1]
            value = None
            if tag == "Import" and "Sdk" not in element.attrib:
                value = element.get("Project")
            elif tag in {"ProjectReference", "Compile", "Resource", "Content"}:
                value = element.get("Include")
            elif tag in {"ApplicationIcon", "ApplicationManifest"}:
                value = element.text
            if not value:
                continue
            for raw_path in value.split(";"):
                raw_path = raw_path.strip()
                if not raw_path:
                    continue
                if any(character in raw_path for character in "$@*?") or "://" in raw_path:
                    coverage["dynamic_references_not_evaluated"] += 1
                    continue
                if is_generated(raw_path):
                    coverage["generated_references_exempted"] += 1
                    continue
                coverage["literal_references_checked"] += 1
                target = Path(raw_path.replace("\\", os.sep).replace("/", os.sep))
                if not target.is_absolute():
                    target = source.parent / target
                target = target.resolve()
                try:
                    relative = target.relative_to(root).as_posix()
                except ValueError:
                    issue(result, "external_reference", "项目依赖仓库外的字面量文件，未检查外部文件，无法确认提交可完整取用。",
                          severity="unknown", path=name, reference=raw_path, reference_kind=tag)
                    continue
                if not target.is_file():
                    issue(result, "required_reference_missing", "项目声明的本地必需文件不存在。",
                          path=name, reference=raw_path, reference_kind=tag)
                if os.path.normcase(relative.replace("/", os.sep)) not in tracked_keys:
                    issue(result, "required_reference_untracked", "项目必需输入尚未进入 Git 索引，重新检出提交时不会自带。",
                          path=name, reference=raw_path, reference_kind=tag, required_path=relative)


def inspect(root: Path, baseline: str | None = None, baseline_file: Path | None = None,
            allow_historical: bool = False) -> dict:
    root = root.resolve()
    result = {
        "status": "unconfirmed", "root": str(root), "branch": None, "head": None,
        "baseline": {"status": "unconfirmed"}, "issues": [],
        "coverage": {
            "tracked_entries": 0, "required_entries": 0, "archive_entries_exempted": 0,
            "generated_entries_exempted": 0, "generated_references_exempted": 0,
            "sparse_rules_evaluated": False, "sparse_rule_excluded_entries": 0,
            "project_files_checked": 0, "literal_references_checked": 0,
            "dynamic_references_not_evaluated": 0,
            "limits": "仅检查已追踪文件、字面量项目依赖和指定本地基线；不验证功能或求值 MSBuild 宏。",
        },
    }
    try:
        actual_root = Path(decoded(git(root, "rev-parse", "--show-toplevel").stdout).strip()).resolve()
        if canonical(actual_root) != canonical(root):
            raise GitError("--root 必须是仓库工作树根目录")
        result["head"] = decoded(git(root, "rev-parse", "HEAD").stdout).strip()
        result["branch"] = decoded(git(root, "branch", "--show-current").stdout).strip() or "(detached)"
        common = decoded(git(root, "rev-parse", "--git-common-dir").stdout).strip()
        result["repo_common_dir"] = str((root / common).resolve())
        listing = git(root, "ls-files", "-v", "-z").stdout
        tracked = {decoded(record[2:]): chr(record[0]) for record in listing.split(b"\0") if record}
        staged = git(root, "ls-files", "--stage", "-z").stdout
        modes = {}
        for record in staged.split(b"\0"):
            if not record:
                continue
            metadata, name = record.split(b"\t", 1)
            mode, _, stage = metadata.split()
            modes[decoded(name)] = decoded(mode)
            if stage != b"0":
                issue(result, "unmerged_index", "索引存在未解决的合并冲突。", path=decoded(name))
        coverage = result["coverage"]
        coverage["tracked_entries"] = len(tracked)
        rule_excluded = set()
        sparse = git(root, "config", "--bool", "--get", "core.sparseCheckout", allowed=(0, 1))
        if sparse.returncode == 0 and decoded(sparse.stdout).strip() == "true":
            required_names = {name for name in tracked if not is_archive(name) and not is_generated(name)}
            payload = b"".join(name.encode("utf-8", "surrogateescape") + b"\0" for name in sorted(required_names))
            included = git(root, "sparse-checkout", "check-rules", "-z", input_data=payload).stdout
            rule_excluded = required_names - {decoded(name) for name in included.split(b"\0") if name}
            coverage["sparse_rules_evaluated"] = True
            coverage["sparse_rule_excluded_entries"] = len(rule_excluded)
        for name, flag in sorted(tracked.items()):
            if is_archive(name):
                coverage["archive_entries_exempted"] += 1
                continue
            if is_generated(name):
                coverage["generated_entries_exempted"] += 1
                continue
            coverage["required_entries"] += 1
            path = root / name
            if modes.get(name) == "160000":
                if not path.is_dir() or not (path / ".git").exists():
                    issue(result, "submodule_not_initialized", "必需子模块尚未初始化。", path=name)
                elif flag.upper() == "S" or name in rule_excluded:
                    issue(result, "sparse_required_path", "必需子模块被稀疏检出规则排除。", path=name)
                continue
            if flag.upper() == "S" or name in rule_excluded:
                issue(result, "sparse_required_path", "必需文件被稀疏检出规则排除。", path=name)
            if not path.is_file() and not (path.is_symlink() and path.exists()):
                issue(result, "tracked_file_missing", "已追踪的必需文件未出现在工作树。", path=name)
        check_references(root, tracked, result)
        changes = []
        status = git(root, "status", "--porcelain=v1", "--untracked-files=normal", "-z").stdout.split(b"\0")
        iterator = iter(status)
        for entry in iterator:
            if not entry:
                continue
            changes.append(decoded(entry))
            if entry[:1] in (b"R", b"C") or entry[1:2] in (b"R", b"C"):
                next(iterator, None)
        result["worktree_changes"] = {"count": len(changes), "first_entries": changes[:24], "blocking_by_itself": False}
        check_baseline(root, result, baseline, baseline_file, allow_historical)
    except (GitError, OSError, ValueError) as error:
        issue(result, "inspection_failed", f"预检未能完整执行：{error}", severity="unknown")
    result["status"] = (
        "blocked" if any(item["severity"] == "error" for item in result["issues"])
        else "unconfirmed" if any(item["severity"] == "unknown" for item in result["issues"])
        else "complete"
    )
    return result


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="只读检查源码取齐情况与已交付提交基线。")
    parser.add_argument("--root", type=Path, required=True, help="Git 工作树根目录")
    baseline = parser.add_mutually_exclusive_group()
    baseline.add_argument("--baseline", help="必须包含的本地提交或 ref")
    baseline.add_argument("--baseline-file", type=Path, help="带仓库身份的 JSON 基线配置")
    parser.add_argument("--allow-historical", action="store_true", help="仅在用户明确选历史版本时允许基线差异；仍检查文件齐全")
    parser.add_argument("--json", action="store_true", help="输出结构化结果")
    arguments = parser.parse_args(argv)
    result = inspect(arguments.root, arguments.baseline, arguments.baseline_file, arguments.allow_historical)
    if arguments.json:
        print(json.dumps(result, ensure_ascii=True, indent=2))
    else:
        print(f"源码预检：{result['status']} | {result['branch']} | {result['head']}")
        print(f"必需文件/子模块：{result['coverage']['required_entries']}；基线：{result['baseline']['status']}")
        for item in result["issues"]:
            print(f"[{item['severity']}] {item['code']}: {item.get('path', '')} {item['message']}")
        print(result["coverage"]["limits"])
    return {"complete": 0, "unconfirmed": 1, "blocked": 2}[result["status"]]


if __name__ == "__main__":
    sys.exit(main())
