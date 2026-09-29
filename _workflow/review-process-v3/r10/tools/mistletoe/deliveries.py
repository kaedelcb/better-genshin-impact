"""Read-only discovery of parallel deliveries; never merges or grants acceptance."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys

PREFIX = re.compile(r"_r[0-9]+[a-z0-9_-]*\Z")
ALLOWED_REPORT_ROOT = re.compile(r"(?:_r[0-9]+[a-z0-9_-]*|_workflow)\Z")
STATES = {"pending", "blocked", "integrated", "verified"}


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def git(root: Path, *args: str) -> str:
    run = subprocess.run(["git", "-C", str(root), *args], capture_output=True,
                         text=True, encoding="utf-8", errors="replace")
    if run.returncode:
        raise ValueError(f"git {args[0]} failed: {run.stderr.strip()}")
    return run.stdout.strip()


def safe_file(root: Path, relative: str) -> Path:
    part = Path(relative)
    if part.is_absolute() or not part.parts or not ALLOWED_REPORT_ROOT.fullmatch(part.parts[0]):
        raise ValueError(f"invalid delivery path: {relative}")
    if ".." in part.parts:
        raise ValueError(f"parent traversal: {relative}")
    candidate = root / part
    candidate.resolve().relative_to(root.resolve())
    for item in [candidate, *candidate.parents]:
        if item == root:
            break
        if item.is_symlink() or getattr(item, "is_junction", lambda: False)():
            raise ValueError(f"linked delivery path: {relative}")
    if not candidate.is_file():
        raise ValueError(f"missing delivery file: {relative}")
    return candidate


def worktrees(root: Path) -> list[Path]:
    return [Path(line[len("worktree "):]) for line in
            git(root, "worktree", "list", "--porcelain").splitlines()
            if line.startswith("worktree ")]


def inspect(root: Path, registry: dict) -> dict:
    errors: list[str] = []
    queue: list[dict] = []
    known: set[Path] = set()
    seen: set[str] = set()
    if registry.get("schema_version") != 1 or not isinstance(registry.get("deliveries"), list):
        raise ValueError("invalid registry schema")
    for row in registry["deliveries"]:
        identity = row.get("id", "")
        if not identity or identity in seen:
            errors.append(f"duplicate/missing delivery id: {identity}")
        seen.add(identity)
        try:
            if row.get("integration_state") not in STATES:
                raise ValueError("invalid integration state")
            checkout = Path(row["worktree"])
            if not checkout.is_absolute():
                raise ValueError("checkout must be absolute")
            checkout = checkout.resolve()
            report = safe_file(checkout, row["report"])
            known.add(report.resolve())
            observed_head = git(checkout, "rev-parse", "HEAD")
            if observed_head != row["head"]:
                raise ValueError(f"HEAD drift: {observed_head}")
            files = row.get("files", [])
            if not files or row["report"] not in [f.get("path") for f in files]:
                raise ValueError("report not bound to file hashes")
            for item in files:
                file = safe_file(checkout, item["path"])
                if sha(file) != item["sha256"].upper():
                    raise ValueError(f"file drift: {item['path']}")
                git(checkout, "ls-files", "--error-unmatch", "--", item["path"])
                if git(checkout, "status", "--porcelain", "--", item["path"]):
                    raise ValueError(f"uncommitted delivery material: {item['path']}")
            if row["integration_state"] in {"integrated", "verified"}:
                receipt = row.get("integration_receipt", {})
                if not receipt.get("commit") or not receipt.get("evidence"):
                    raise ValueError("integration state lacks commit/evidence receipt")
            queue.append({"id": identity, "target_batch": row["target_batch"],
                          "integration_state": row["integration_state"],
                          "blockers": row.get("blockers", []),
                          "covered_by": row.get("covered_by"),
                          "report": str(report), "head": observed_head,
                          "next_action": "executor must audit semantic gates and integrate at target batch"})
        except (KeyError, OSError, ValueError) as exc:
            errors.append(f"{identity}: {exc}")
    discoveries: list[dict] = []
    for checkout in worktrees(root):
        if not checkout.is_dir():
            continue
        try:
            for directory in checkout.iterdir():
                if not PREFIX.fullmatch(directory.name):
                    continue
                relative = f"{directory.name}/report.md"
                if not (directory / "report.md").exists():
                    continue
                report = safe_file(checkout, relative)
                if report.resolve() not in known:
                    discoveries.append({"worktree": str(checkout), "report": relative,
                                        "sha256": sha(report),
                                        "head": git(checkout, "rev-parse", "HEAD"),
                                        "state": "unregistered; inspect before classifying"})
        except (OSError, ValueError) as exc:
            errors.append(f"discovery {checkout}: {exc}")
    return {"schema_version": 1, "quality_verdict": "NOT PROVIDED",
            "automatic_merge": False, "queue": queue,
            "unregistered_reports": discoveries, "errors": errors,
            "ok": not errors and not discoveries,
            "task_status": "not inferred from Git; executor must query thread status"}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--registry", type=Path,
                        default=Path("Docs/design/mistletoe-parallel-deliveries.json"))
    args = parser.parse_args()
    try:
        root = args.root.resolve()
        registry = args.registry if args.registry.is_absolute() else root / args.registry
        result = inspect(root, json.loads(registry.read_text(encoding="utf-8-sig")))
    except (OSError, ValueError) as exc:
        result = {"ok": False, "errors": [str(exc)], "automatic_merge": False,
                  "quality_verdict": "NOT PROVIDED"}
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result["ok"] else 2


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
