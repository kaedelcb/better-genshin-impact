"""Read-only pre-implementation consultation triage for a new mistletoe batch.

The executor supplies semantic evidence for every risk factor. This tool checks
that the assessment is complete and bound to the frozen opening sources, then
derives the consultation decision. It never invokes a model or edits product code.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


FACTORS = (
    "authority_transition",
    "durable_migration_or_rollback",
    "cross_process_or_generation",
    "cross_component_production_gate",
    "safety_contract_or_open_findings",
)


def _need(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def _file(root: Path, relative: str) -> Path:
    _need(isinstance(relative, str) and relative.strip(), "missing relative path")
    path = Path(relative)
    _need(not path.is_absolute() and ".." not in path.parts, "path leaves workspace")
    resolved = (root / path).resolve()
    _need(resolved.is_relative_to(root), "path leaves workspace")
    return resolved


def _text(value: object, label: str) -> str:
    _need(isinstance(value, str) and bool(value.strip()), f"missing {label}")
    return value.strip()


def assess(root: Path, manifest: dict, opening: dict, assessment: dict) -> dict:
    root = root.resolve()
    _need(assessment.get("schema_version") == 1, "unsupported assessment schema")
    batch = _text(manifest.get("batch"), "manifest batch")
    _need(assessment.get("batch") == batch == opening.get("batch"), "batch mismatch")
    _need(manifest.get("schema_version") == 2, "triage requires a v2 manifest")
    _need(manifest.get("opening_snapshot") == assessment.get("opening_snapshot"),
          "assessment must name the opening snapshot")
    opening_hashes = opening.get("source_hashes")
    _need(isinstance(opening_hashes, dict) and opening_hashes,
          "opening source hashes missing")
    _need(set(opening_hashes) == set(manifest.get("sources", [])),
          "opening source scope differs from manifest")
    for relative, expected in opening_hashes.items():
        actual = hashlib.sha256(_file(root, relative).read_bytes()).hexdigest()
        _need(actual == expected, f"opening source drift: {relative}")

    factors = assessment.get("factors")
    _need(isinstance(factors, dict) and set(factors) == set(FACTORS),
          "all five risk factors must be assessed exactly once")
    hits = []
    for name in FACTORS:
        item = factors[name]
        _need(isinstance(item, dict) and type(item.get("applies")) is bool,
              f"{name} requires applies=true/false")
        _text(item.get("evidence"), f"{name} evidence")
        _text(item.get("consequence_or_exclusion"),
              f"{name} consequence or exclusion reason")
        if item["applies"]:
            _text(item.get("entry_or_contract"), f"{name} entry or contract")
            hits.append(name)

    design_path = None
    design_hash = None
    if hits:
        design_relative = _text(assessment.get("design_contract_path"), "design contract path")
        _need(Path(design_relative).parts[:2] == ("_workflow", batch)
              and Path(design_relative).suffix.lower() == ".md",
              "design contract must be a markdown file in this batch's _workflow directory")
        design_file = _file(root, design_relative)
        _need(design_file.is_file() and design_file.stat().st_size > 0,
              "design contract missing or empty")
        design_path = design_relative
        design_hash = hashlib.sha256(design_file.read_bytes()).hexdigest()

    previous = assessment.get("equivalent_review")
    _need(isinstance(previous, dict) and type(previous.get("available")) is bool,
          "equivalent_review.available required")
    _text(previous.get("reason"), "equivalent review reason")
    if previous["available"]:
        report = _file(root, _text(previous.get("report_path"), "equivalent review report"))
        _need(report.is_file(), "equivalent review report missing")
        _need(previous.get("source_hashes") == opening_hashes,
              "equivalent review source hashes differ from opening")
        _text(previous.get("same_scope_and_contract"),
              "equivalent review same-scope-and-contract evidence")

    if not hits:
        decision = "no_listed_risk_hit"
    elif previous["available"]:
        decision = "equivalence_needs_assessment"
    else:
        decision = "risk_attention_required"
    return {
        "schema_version": 1,
        "batch": batch,
        "opening_snapshot": assessment["opening_snapshot"],
        "opening_source_hashes": opening_hashes,
        "risk_hits": hits,
        "design_contract_path": design_path,
        "design_contract_sha256": design_hash,
        "decision": decision,
        "model_selection": "requires_current_per_request_assessment",
        "quality_verdict": "NOT PROVIDED",
        "note": "Semantic truth and review equivalence require human verification; this tool sends no request.",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".")
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--assessment", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    root = Path(args.root).resolve()
    try:
        manifest = json.loads(_file(root, args.manifest).read_text(encoding="utf-8-sig"))
        opening = json.loads(_file(root, manifest["opening_snapshot"]).read_text(encoding="utf-8-sig"))
        assessment = json.loads(_file(root, args.assessment).read_text(encoding="utf-8-sig"))
        result = assess(root, manifest, opening, assessment)
        out = _file(root, args.out)
        _need("_workflow" in out.relative_to(root).parts[:1],
              "output must be under _workflow")
        out.parent.mkdir(parents=True, exist_ok=True)
        with out.open("x", encoding="utf-8", newline="\n") as stream:
            json.dump(result, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
        print(json.dumps({"decision": result["decision"], "risk_hits": result["risk_hits"],
                          "out": args.out}, ensure_ascii=False))
        return 0
    except (OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
        print(json.dumps({"mechanical_status": "blocked", "reason": str(exc)}, ensure_ascii=False))
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
