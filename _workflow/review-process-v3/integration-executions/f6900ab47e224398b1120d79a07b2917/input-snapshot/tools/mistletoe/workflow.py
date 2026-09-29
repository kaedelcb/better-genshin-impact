"""Evidence preflight with a write-once opening receipt; no product writes or review dispatch."""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import subprocess
import sys
from datetime import datetime, timezone
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
PROTECTED = {"user", "bin", "obj", ".git", ".kiro"}
LIMIT = 524288
RISK_DIMENSIONS = {"state", "concurrency", "fault"}
# The already-running SB21-4 batch keeps its v1 manifest. New code batches use v2.
LEGACY_CODE_REVIEWS = {("_workflow/sb21-4/manifest.json",
                        "SB21-4 BO-13 R5 formal re-review; BO-6/7 not started")}


class EvidenceError(ValueError):
    pass


def need(condition, message):
    if not condition:
        raise EvidenceError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def input_path(root, relative):
    need(isinstance(relative, str) and relative.strip(), "empty path")
    p = Path(relative)
    need(not p.is_absolute() and ".." not in p.parts, "relative path without '..' required")
    need(not PROTECTED.intersection(x.casefold() for x in p.parts), "protected path")
    resolved = (root / p).resolve()
    need(resolved.is_relative_to(root.resolve()), "path escapes root")
    need(not PROTECTED.intersection(x.casefold() for x in resolved.relative_to(root.resolve()).parts),
         "resolved protected path")
    return resolved


def read_file(root, relative):
    p = input_path(root, relative)
    need(p.is_file(), f"missing file: {relative}")
    return p.read_bytes()


def load_json(data):
    def pairs(items):
        obj = {}
        for k, v in items:
            need(k not in obj, f"duplicate JSON key: {k}")
            obj[k] = v
        return obj
    return json.loads(data.decode("utf-8-sig"), object_pairs_hook=pairs)


def parse_trx(data):
    """Preserve every execution. Names alone are never identity keys."""
    root = ET.fromstring(data)
    need(root.tag == "{" + NS["t"] + "}TestRun", "unsupported TRX namespace/root")
    summary = root.find("t:ResultSummary", NS)
    counters = root.find("t:ResultSummary/t:Counters", NS)
    results = root.find("t:Results", NS)
    definitions = root.find("t:TestDefinitions", NS)
    need(all(x is not None for x in (summary, counters, results, definitions)), "incomplete TRX")
    defs = {}
    for d in definitions:
        ident = d.get("id")
        need(ident and ident not in defs, "missing/duplicate TRX definition identity")
        method = d.find("t:TestMethod", NS)
        need(method is not None, "missing TestMethod")
        defs[ident] = {"class": method.get("className"), "method": method.get("name")}
    rows, executions = [], set()
    for r in results:
        need(r.tag == "{" + NS["t"] + "}UnitTestResult", "unsupported nested/result type")
        tid, eid, name, outcome = (r.get(k) for k in ("testId", "executionId", "testName", "outcome"))
        need(tid in defs and eid and name and outcome, "missing identity/outcome/definition")
        need(eid not in executions, "duplicate execution identity")
        executions.add(eid)
        need(outcome in {"Passed", "Failed", "NotExecuted"}, f"unhandled outcome: {outcome}")
        rows.append({"test_id": tid, "execution_id": eid, "name": name, "outcome": outcome,
                     "definition": defs[tid],
                     "message": r.findtext("t:Output/t:ErrorInfo/t:Message", default="", namespaces=NS),
                     "stack": r.findtext("t:Output/t:ErrorInfo/t:StackTrace", default="", namespaces=NS)})
    need(rows, "TRX contains no executions")
    counts = Counter(r["outcome"] for r in rows)
    for key, actual in (("total", len(rows)), ("passed", counts["Passed"]),
                        ("failed", counts["Failed"]), ("executed", counts["Passed"] + counts["Failed"])):
        need(counters.get(key) is not None and int(counters.get(key)) == actual,
             f"TRX counters disagree: {key}")
    # VSTest xUnit can report NotExecuted results with notExecuted counter zero.
    skipped = int(counters.get("notExecuted", "0"))
    need(skipped in (0, counts["NotExecuted"]), "TRX skipped counters disagree")
    for k, v in counters.attrib.items():
        if k not in {"total", "passed", "failed", "executed", "notExecuted"}:
            need(int(v) == 0, f"nonzero unhandled TRX counter: {k}")
    need(summary.get("outcome") in {"Completed", "Passed", "Failed"}, "incomplete/aborted run")
    names = Counter(r["name"] for r in rows)
    return {"total": len(rows), "passed": counts["Passed"], "failed": counts["Failed"],
            "skipped": counts["NotExecuted"], "rows": rows,
            "duplicate_names": {k: v for k, v in names.items() if v > 1},
            "run_outcome": summary.get("outcome")}


def successful(trx):
    return trx["failed"] == 0 and trx["passed"] > 0 and trx["run_outcome"] in {"Completed", "Passed"}


def compare_trx(baseline, final):
    """testId-based structural comparison; never classify removals as acceptable."""
    groups = []
    for report in (baseline, final):
        g = defaultdict(list)
        for r in report["rows"]:
            g[r["test_id"]].append((r["name"], r["definition"]["class"],
                                   r["definition"]["method"], r["outcome"]))
        groups.append({k: sorted(v) for k, v in g.items()})
    a, b = groups
    shared = a.keys() & b.keys()
    return {"identity_basis": "testId; provider changes require manual reconciliation",
            "added_ids": sorted(b.keys() - a.keys()), "removed_ids": sorted(a.keys() - b.keys()),
            "changed_ids": sorted(k for k in shared if a[k] != b[k]),
            "unchanged_ids": sorted(k for k in shared if a[k] == b[k])}


def mutation_check(record, read, trx):
    for key in ("id", "source", "original_sha256", "restored_sha256", "baseline_trx",
                "mutant_trx", "restored_trx", "target_test_id", "target_name", "failure_contains",
                "assertion_contains", "build_log", "build_exit", "baseline_exit", "mutant_exit", "restored_exit"):
        need(key in record, f"mutation missing {key}")
    need(all(type(record[k]) is int for k in ("build_exit", "baseline_exit", "restored_exit")),
         "exit codes must be integers")
    need(record["build_exit"] == record["baseline_exit"] == record["restored_exit"] == 0,
         "mutation build/baseline/restore did not succeed")
    need(isinstance(record["mutant_exit"], int) and not isinstance(record["mutant_exit"], bool)
         and record["mutant_exit"] > 0, "missing failing mutant exit")
    need(record["failure_contains"] and record["assertion_contains"], "missing expected assertion markers")
    read(record["build_log"])
    sha = digest(read(record["source"]))
    need(record["original_sha256"] == record["restored_sha256"] == sha,
         "mutation source not restored byte-for-byte")
    paths = [record[k] for k in ("baseline_trx", "mutant_trx", "restored_trx")]
    need(len(set(paths)) == 3 and len({digest(read(p)) for p in paths}) == 3,
         "mutation reports must be distinct")
    reports = [trx(p) for p in paths]
    need(successful(reports[0]) and successful(reports[2]), "mutation baseline/restore not green")
    matched = []
    for report in reports:
        rows = [r for r in report["rows"] if r["test_id"] == record["target_test_id"]
                and r["name"] == record["target_name"]]
        need(len(rows) == 1, "mutation target missing or ambiguous")
        matched.append(rows[0])
    need([r["outcome"] for r in matched] == ["Passed", "Failed", "Passed"],
         "expected assertion was not killed and restored")
    need(record["failure_contains"] in matched[1]["message"]
         and record["assertion_contains"] in matched[1]["stack"], "wrong failure/earlier assertion")
    return {"id": record["id"], "mechanical_status": "ok", "source_sha256": sha,
            "scope": "recorded target assertion only; supplied exit codes/build provenance require review"}


def git(root, *args):
    p = subprocess.run(["git", "-C", str(root), *args], capture_output=True)
    need(p.returncode == 0, "git capture failed: " + p.stderr.decode("utf-8", errors="replace"))
    return p.stdout


def risk_rows(matrix, batch, allow_planned):
    need(matrix.get("schema_version") == 1 and matrix.get("batch") == batch,
         "risk matrix schema/batch mismatch")
    rows = matrix.get("rows")
    need(isinstance(rows, list) and rows, "risk matrix rows required")
    found, dimensions = set(), set()
    for row in rows:
        need(isinstance(row, dict), "invalid risk matrix row")
        for key in ("id", "dimension", "scenario", "expected"):
            need(isinstance(row.get(key), str) and row[key].strip(), f"risk row missing {key}")
        need(row["id"] not in found, "duplicate risk row id")
        found.add(row["id"])
        need(row["dimension"] in RISK_DIMENSIONS, "invalid risk dimension")
        dimensions.add(row["dimension"])
        need(type(row.get("critical")) is bool, "risk row needs explicit critical flag")
        allowed = {"planned", "covered", "not_applicable"} if allow_planned else {"covered", "not_applicable"}
        need(row.get("status") in allowed, "risk row not resolved for review")
        if row["status"] == "not_applicable":
            need(isinstance(row.get("reason"), str) and row["reason"].strip(),
                 "not-applicable risk needs a reason")
    need(dimensions == RISK_DIMENSIONS, "state/concurrency/fault dimensions all required")
    return rows


def begin(root, manifest_path):
    """Freeze the opening matrix and scope before implementation; no product action."""
    manifest_bytes = read_file(root, manifest_path)
    manifest = load_json(manifest_bytes)
    need(manifest.get("schema_version") == 2 and manifest.get("mode") in {"code", "documents"},
         "begin requires a v2 code/document manifest")
    batch = manifest.get("batch")
    need(isinstance(batch, str) and batch.strip(), "missing batch")
    sources = manifest.get("sources")
    need(isinstance(sources, list) and sources and len(set(sources)) == len(sources),
         "opening source scope required")
    matrix_path, opening_path = manifest.get("risk_matrix"), manifest.get("opening_snapshot")
    need(isinstance(opening_path, str), "opening snapshot path required")
    opening = input_path(root, opening_path)
    need(Path(opening_path).parts[0] == "_workflow" and not opening.exists(),
         "opening snapshot must be a new _workflow/ file")
    matrix_bytes, rows = b"", []
    if manifest["mode"] == "code":
        need(isinstance(matrix_path, str), "code batch risk matrix path required")
        matrix_bytes = read_file(root, matrix_path)
        rows = risk_rows(load_json(matrix_bytes), batch, allow_planned=True)
    source_bytes = {s: read_file(root, s) for s in sources}
    head = git(root, "rev-parse", "HEAD").decode().strip()
    branch = git(root, "branch", "--show-current").decode().strip()
    status = git(root, "status", "--porcelain=v1").decode("utf-8", errors="replace")
    need(read_file(root, manifest_path) == manifest_bytes, "opening manifest changed during capture")
    if matrix_path:
        need(read_file(root, matrix_path) == matrix_bytes, "opening matrix changed during capture")
    need(all(read_file(root, s) == data for s, data in source_bytes.items()),
         "opening source changed during capture")
    need(git(root, "rev-parse", "HEAD").decode().strip() == head, "opening HEAD changed")
    receipt = {"schema_version": 1, "batch": batch, "created_utc": datetime.now(timezone.utc).isoformat(),
               "head": head, "branch": branch, "sources": sources,
               "source_hashes": {s: digest(data) for s, data in source_bytes.items()},
               "opening_status": status, "risk_matrix": matrix_path,
               "initial_rows": [{k: row[k] for k in ("id", "dimension", "scenario", "expected", "critical")}
                                for row in rows]}
    if manifest["mode"] == "code":
        import review_process as rp
        rp.check_new(root, manifest)
        receipt["review_process_required"] = True
    opening.parent.mkdir(parents=True, exist_ok=True)
    with opening.open("x", encoding="utf-8") as output:
        output.write(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n")
        output.flush()
        __import__("os").fsync(output.fileno())
    return {"mechanical_status": "ok", "quality_verdict": "NOT PROVIDED", "opening_snapshot": opening_path,
            "risk_rows": len(rows)}


def common_acceleration(control, opening, indexed):
    need(isinstance(control, dict), "v2 review control required")
    reuse = control.get("existing_results")
    need(isinstance(reuse, dict) and reuse.get("decision") in {"reused", "none"}
         and isinstance(reuse.get("reason"), str) and reuse["reason"].strip(),
         "existing-results reuse decision and reason required")
    if reuse["decision"] == "reused":
        refs = reuse.get("evidence_ids")
        need(isinstance(refs, list) and refs and set(refs) <= indexed.keys(),
             "reused results need indexed evidence")
    sub = control.get("subagents")
    need(isinstance(sub, dict) and sub.get("decision") in {"used", "not_used"}
         and isinstance(sub.get("reason"), str) and sub["reason"].strip(),
         "subagent use decision and reason required")
    tasks = sub.get("tasks", [])
    need(isinstance(tasks, list), "invalid subagent tasks")
    if sub["decision"] == "not_used":
        need(not tasks, "unused subagents cannot claim tasks")
    else:
        need(1 <= len(tasks) <= 2, "use one or two bounded read-only subagents")
        for task in tasks:
            need(isinstance(task, dict) and task.get("read_only") is True
                 and isinstance(task.get("question"), str) and task["question"].strip()
                 and task.get("fixed_ref") == opening["head"]
                 and task.get("opening_source_hashes") == opening["source_hashes"]
                 and task.get("report_evidence_id") in indexed
                 and indexed[task["report_evidence_id"]]["level"] in {"document", "consult"},
                 "subagent task needs read-only scope, opening source hashes, and indexed report")
    round_no = control.get("review_round")
    findings = control.get("prior_findings")
    need(type(round_no) is int and round_no >= 1 and isinstance(findings, list),
         "review round/prior findings required")
    if round_no > 1:
        need(findings and isinstance(control.get("repair_batch_id"), str)
             and control["repair_batch_id"].strip(), "re-review needs batched findings disposition")
    seen = set()
    for finding in findings:
        need(isinstance(finding, dict) and isinstance(finding.get("id"), str) and finding["id"].strip()
             and finding["id"] not in seen, "invalid/duplicate prior finding")
        seen.add(finding["id"])
        need(finding.get("severity") in {"must", "important", "suggestion"}, "invalid finding severity")
        need(finding.get("source_review_evidence_id") in indexed
             and indexed[finding["source_review_evidence_id"]]["level"] == "consult",
             "prior finding source review must be indexed consultation")
        if finding["severity"] in {"must", "important"}:
            refs = finding.get("repair_evidence_ids")
            need(finding.get("disposition") == "candidate_fixed"
                 and isinstance(refs, list) and refs and set(refs) <= indexed.keys(),
                 "must/important finding lacks batched repair evidence")
        else:
            need(finding.get("disposition") in {"candidate_fixed", "accepted", "rejected"}
                 and isinstance(finding.get("reason"), str) and finding["reason"].strip(),
                 "suggestion needs a reasoned disposition")
    return {"subagents": sub["decision"], "existing_results": reuse["decision"],
            "review_round": round_no, "prior_findings": len(findings)}


def document_readiness(manifest, read, entries):
    path = manifest.get("opening_snapshot")
    need(isinstance(path, str), "v2 document review needs opening snapshot")
    opening = load_json(read(path))
    need(opening.get("schema_version") == 1 and opening.get("batch") == manifest["batch"]
         and opening.get("sources") == manifest["sources"] and opening.get("risk_matrix") is None,
         "document opening snapshot does not match batch/scope")
    common = common_acceleration(manifest.get("review_control"), opening, {e["id"]: e for e in entries})
    return {"opening_snapshot": path, "opening_head": opening["head"],
            "quality_verdict": "NOT PROVIDED", **common}, manifest["review_control"]


def review_readiness(manifest, read, entries, tests, parsed, mutations):
    """Check explicit coverage and provenance, not semantic completeness."""
    batch = manifest["batch"]
    matrix_path, opening_path = manifest.get("risk_matrix"), manifest.get("opening_snapshot")
    need(isinstance(matrix_path, str) and isinstance(opening_path, str),
         "v2 review needs risk matrix and opening snapshot")
    matrix = load_json(read(matrix_path))
    opening = load_json(read(opening_path))
    rows = risk_rows(matrix, batch, allow_planned=False)
    need(any(row["status"] == "covered" for row in rows),
         "all risk rows cannot be not-applicable")
    need(opening.get("schema_version") == 1 and opening.get("batch") == batch
         and opening.get("risk_matrix") == matrix_path and opening.get("sources") == manifest["sources"],
         "opening snapshot does not match current batch/scope")
    initial = {r["id"]: r for r in opening.get("initial_rows", [])}
    need(initial and len(initial) == len(opening["initial_rows"]), "invalid opening matrix rows")
    current = {r["id"]: r for r in rows}
    need(initial.keys() <= current.keys(), "opening risk row removed")
    for ident, old in initial.items():
        need(all(current[ident].get(k) == old.get(k) for k in ("id", "dimension", "scenario", "expected", "critical")),
             "opening risk row changed: " + ident)
    indexed = {e["id"]: e for e in entries}
    mutation_ids = {m["id"] for m in mutations}
    for row in rows:
        if row["status"] == "not_applicable":
            continue
        refs = row.get("counterexample_ids")
        need(isinstance(refs, list) and refs and len(set(refs)) == len(refs),
             "covered risk needs distinct counterexample evidence ids")
        need(all(ref in indexed and indexed[ref]["level"] in {"test", "runtime"} for ref in refs),
             "risk counterexample missing indexed test/runtime evidence")
        test_refs = [ref for ref in refs if indexed[ref]["level"] == "test"]
        if test_refs:
            test_ids = row.get("test_ids")
            need(isinstance(test_ids, list) and test_ids and all(isinstance(x, str) and x for x in test_ids),
                 "test-backed risk needs named test ids")
            current_green = {t["path"] for t in tests if t["expect_success"] is True}
            need(all(indexed[ref].get("binding", "current") == "current"
                     and indexed[ref]["path"] in current_green for ref in test_refs),
                 "risk test evidence needs current green indexed TRX execution")
            passed = {r["test_id"] for ref in test_refs
                      for r in parsed[indexed[ref]["path"]]["rows"] if r["outcome"] == "Passed"}
            need(set(test_ids) <= passed, "risk test id absent from passed TRX execution")
        if row["critical"]:
            mids = row.get("mutation_ids")
            need(isinstance(mids, list) and mids and set(mids) <= mutation_ids,
                 "critical risk needs verified mutation ids")
    control = manifest.get("review_control")
    common = common_acceleration(control, opening, indexed)
    if not any(row["critical"] for row in rows):
        need(isinstance(control.get("criticality_reason"), str) and control["criticality_reason"].strip(),
             "no critical risks requires an explicit criticality assessment")
    return {"opening_snapshot": opening_path, "opening_head": opening["head"],
            "risk_matrix": matrix_path, "risk_rows": len(rows), **common,
            "quality_verdict": "NOT PROVIDED"}, matrix, control


def inspect_manifest(root, manifest, stage):
    need(manifest.get("schema_version") in {1, 2}, "unsupported manifest schema")
    need(isinstance(manifest.get("batch"), str) and manifest["batch"].strip(), "missing batch")
    mode = manifest.get("mode")
    if mode == "code" and stage in {"review", "closeout"} and manifest.get("schema_version") == 2:
        import review_process as rp
        read_file(root, manifest["opening_snapshot"])
        if rp.required(root, manifest):
            rp.gate(root, manifest, stage)
        else:
            need(rp.legacy_allowed(root, manifest), "unregistered legacy opening; adopt before review")
    need(mode in {"code", "documents", "historical"}, "invalid mode")
    sources = manifest.get("sources")
    need(isinstance(sources, list) and len(set(sources)) == len(sources), "invalid/duplicate sources")
    need(mode == "historical" or sources, "source scope required")
    need(mode != "historical" or stage == "evidence", "historical evidence cannot qualify for review/closeout")
    files = {}

    def read(path):
        if path not in files:
            files[path] = read_file(root, path)
        return files[path]

    for s in sources:
        read(s)
    source_hashes = {s: digest(files[s]) for s in sources}
    entries = manifest.get("evidence")
    need(isinstance(entries, list) and entries, "evidence list required")
    identifiers = set()
    evidence_paths = set()
    for item in entries:
        for k in ("id", "path", "purpose", "level", "conditions"):
            need(isinstance(item.get(k), str) and item[k].strip(), f"evidence missing {k}")
        need(item["level"] in {"build", "test", "component", "runtime", "consult", "document"},
             "invalid evidence level")
        need(item["id"] not in identifiers, "duplicate evidence id")
        identifiers.add(item["id"])
        evidence_paths.add(item["path"])
        read(item["path"])
        if mode == "code":
            binding = item.get("binding", "current")
            need(binding in {"current", "historical"}, "invalid evidence binding")
            hashes = item.get("source_sha256")
            need(isinstance(hashes, dict) and hashes
                 and all(isinstance(v, str) and len(v) == 64 and all(c in "0123456789abcdef" for c in v)
                         for v in hashes.values()), "invalid/missing execution source hashes")
            if binding == "current":
                need(hashes == source_hashes, "code evidence lacks exact source bindings (or inputs changed)")
    tests = manifest.get("tests", [])
    need(isinstance(tests, list), "invalid tests list")
    if mode == "code":
        need(tests, "code mode requires test evidence")
        if stage in {"review", "closeout"}:
            need(any(t.get("expect_success") is True for t in tests), "current green regression required")
    if mode == "documents":
        need(isinstance(manifest.get("validation_reason"), str) and manifest["validation_reason"].strip(),
             "document-only validation needs a reason")
        need(all(Path(s).suffix.lower() in {".md", ".txt", ".json"} for s in sources),
             "document mode cannot hide executable source")
    parsed = {}

    def trx(p):
        if p not in parsed:
            parsed[p] = parse_trx(read(p))
        return parsed[p]

    for test in tests:
        need(test.get("path") in evidence_paths and isinstance(test.get("expect_success"), bool),
             "test needs indexed evidence and explicit expectation")
        if test["expect_success"]:
            if mode == "code":
                need(any(e["path"] == test["path"] and e.get("binding", "current") == "current" for e in entries),
                     "historical test cannot prove current regression success")
            need(successful(trx(test["path"])), "expected green TRX is not green")
        else:
            trx(test["path"])
    comparison = None
    if manifest.get("comparison"):
        cmp = manifest["comparison"]
        need(cmp["baseline"] in evidence_paths and cmp["final"] in evidence_paths,
             "comparison reports not indexed")
        comparison = compare_trx(trx(cmp["baseline"]), trx(cmp["final"]))
    mutations = [mutation_check(m, read, trx) for m in manifest.get("mutations", [])]
    need(isinstance(manifest.get("mutation_scope"), str) and manifest["mutation_scope"].strip(),
         "declare mutation scope, omissions, or non-applicability")
    readiness = None
    readiness_packet = b""
    if manifest["schema_version"] == 2 and mode == "code" and stage in {"review", "closeout"}:
        readiness, matrix, control = review_readiness(manifest, read, entries, tests, parsed, mutations)
        readiness_packet = ("\n## Review readiness (mechanical claims, not quality verdict)\n"
                            + json.dumps({"opening": readiness, "risk_matrix": matrix,
                                          "review_control": control}, ensure_ascii=False, indent=2)
                            + "\n").encode("utf-8")
    if manifest["schema_version"] == 2 and mode == "documents" and stage in {"review", "closeout"}:
        readiness, control = document_readiness(manifest, read, entries)
        readiness_packet = ("\n## Document-task acceleration assessment (mechanical only)\n"
                            + json.dumps({"opening": readiness, "review_control": control},
                                         ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    packet = b""
    local_review = bool(manifest.get("review_process"))
    if stage in {"review", "closeout"}:
        packet_entries = manifest.get("packet")
        need(isinstance(packet_entries, list) and packet_entries, "review packet required")
        roles = {e.get("role") for e in packet_entries}
        need({"objective", "findings", "budget"}.issubset(roles), "missing objective/findings/budget")
        covered = set()
        for entry in packet_entries:
            raw = read(entry["path"])
            content = raw.decode("utf-8-sig")
            lines = content.splitlines(keepends=True)
            start, end = entry.get("start_line", 1), entry.get("end_line", len(lines))
            need(type(start) is int and type(end) is int and 1 <= start <= end <= len(lines),
                 "invalid/empty excerpt range")
            text = "".join(lines[start - 1:end])
            if start != 1 or end != len(lines):
                need(isinstance(entry.get("coverage_notes"), str) and entry["coverage_notes"].strip(),
                     "excerpt needs semantic coverage notes")
            packet += (f"\n## {entry['role']}: {entry['path']} L{start}-L{end} SHA256={digest(raw)}\n"
                       + entry.get("coverage_notes", "") + "\n" + ("[complete bytes in local snapshot]" if local_review else text) + "\n").encode("utf-8")
            if entry["path"] in sources:
                covered.add(entry["path"])
        need(covered == set(sources), "review packet omits scope files")
        packet += readiness_packet
    return {"files": files, "source_hashes": source_hashes, "tests": parsed,
            "comparison": comparison, "mutations": mutations, "packet": packet,
            "review_readiness": readiness}


def audit(root, manifest_path, out, stage):
    """Freeze explicit inputs, create one index and an exact local packet. Never send it."""
    manifest_bytes = read_file(root, manifest_path)
    manifest = load_json(manifest_bytes)
    if stage in {"review", "closeout"} and manifest.get("mode") in {"code", "documents"} and manifest.get("schema_version") == 1:
        need((manifest_path.replace("\\", "/"), manifest.get("batch")) in LEGACY_CODE_REVIEWS,
             "new batch reviews require v2 manifest and opening assessment")
    out_path = input_path(root, out)
    need(Path(out).parts[0] == "_workflow", "outputs must be under _workflow/")
    need(out_path.relative_to(root.resolve()).parts[0] == "_workflow", "resolved output outside _workflow/")
    # Refuse existing output and any directory containing inputs, before creating files.
    need(not out_path.exists(), "output already exists; choose a fresh snapshot")
    inspected = inspect_manifest(root, manifest, stage)
    inspected["files"][manifest_path] = manifest_bytes
    need(all(not input_path(root, p).is_relative_to(out_path) for p in inspected["files"]),
         "output would contain inputs")
    head = git(root, "rev-parse", "HEAD").decode().strip()
    need(Path(git(root, "rev-parse", "--show-toplevel").decode().strip()).resolve() == root.resolve(),
         "root must be the repository top-level")
    branch = git(root, "branch", "--show-current").decode().strip()
    status = git(root, "status", "--porcelain=v1")
    sources = manifest["sources"]
    unstaged = git(root, "diff", "--no-ext-diff", "--no-textconv", "--", *sources) if sources else b""
    staged = git(root, "diff", "--cached", "--no-ext-diff", "--no-textconv", "--", *sources) if sources else b""
    packet = inspected["packet"]
    if stage in {"review", "closeout"}:
        statistics = {p: {k: t[k] for k in ("total", "passed", "failed", "skipped", "duplicate_names")}
                      for p, t in inspected["tests"].items()}
        overview = {"tests": statistics, "comparison": inspected["comparison"], "mutations": inspected["mutations"],
                    "evidence": [{**e, "file_sha256": digest(inspected["files"][e["path"]])}
                                 for e in manifest["evidence"]], "quality_verdict": "NOT PROVIDED"}
        packet += ("\n## Generated evidence overview (mechanical only)\n"
                   + json.dumps(overview, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
        packet += (b"\n## git status --porcelain (all changes; ownership requires manual classification)\n"
                   + status + b"\n## scoped unstaged diff\n" + unstaged + b"\n## scoped staged diff\n" + staged)
        need(isinstance(manifest.get("outside_changes"), str) and manifest["outside_changes"].strip(),
             "explicit outside-changes disposition required")
        packet += ("\n## materials outside this batch\n" + manifest["outside_changes"] + "\n").encode("utf-8")
        limit = manifest.get("packet_limit_bytes", LIMIT)
        need(type(limit) is int and 0 < limit <= LIMIT, "invalid local packet limit")
        if not manifest.get("review_process"):
            need(len(packet) <= limit, f"packet exceeds limit: {len(packet)} > {limit}; no automatic truncation")
    # Detect a writer changing captured scope/materials during the read.
    for path, original in inspected["files"].items():
        need(read_file(root, path) == original, f"input changed during capture: {path}")
    need(git(root, "rev-parse", "HEAD").decode().strip() == head, "HEAD changed during capture")
    need(git(root, "branch", "--show-current").decode().strip() == branch, "branch changed during capture")
    if sources:
        need(git(root, "diff", "--no-ext-diff", "--no-textconv", "--", *sources) == unstaged
             and git(root, "diff", "--cached", "--no-ext-diff", "--no-textconv", "--", *sources) == staged,
             "scope diff changed during capture")
    report = {"schema_version": 1, "batch": manifest["batch"], "stage": stage, "mode": manifest["mode"],
              "manifest_path": manifest_path, "manifest_sha256": digest(manifest_bytes),
              "manifest_digest": digest(json.dumps(manifest, sort_keys=True, ensure_ascii=False).encode("utf-8")),
              "created_utc": datetime.now(timezone.utc).isoformat(), "root": str(root.resolve()),
              "head": head, "branch": branch, "sources": sources,
              "mechanical_status": "ok", "quality_verdict": "NOT PROVIDED",
              "source_hashes": inspected["source_hashes"],
              "inputs": [{"path": p, "bytes": len(b), "sha256": digest(b)} for p, b in inspected["files"].items()],
              "tests": inspected["tests"], "comparison": inspected["comparison"],
              "mutations": inspected["mutations"], "packet_bytes": len(packet),
              "review_readiness": inspected["review_readiness"],
              "manual_checks": ["authority requirements and omitted evidence", "test execution source/conditions provenance",
                                "excerpt semantic completeness and outside changes", "consultation budget and findings disposition",
                                "runtime evidence and production/owner gates"],
              "artifacts": {"packet.md": digest(packet), "git-status.txt": digest(status),
                            "scoped-unstaged.diff": digest(unstaged), "scoped-staged.diff": digest(staged)}}
    out_path.mkdir(parents=True, exist_ok=False)
    for name, data in (("packet.md", packet), ("git-status.txt", status),
                       ("scoped-unstaged.diff", unstaged), ("scoped-staged.diff", staged)):
        (out_path / name).write_bytes(data)
    index = [f"# {manifest['batch']} evidence index", "", "Mechanical checks only; no quality/reuse verdict.", ""]
    for item in manifest["evidence"]:
        index.append(f"- {item['id']} | {item['level']} | {item['path']} | SHA256={digest(inspected['files'][item['path']])}")
        index.append(f"  Purpose: {item['purpose']}; conditions: {item['conditions']}")
    (out_path / "index.md").write_text("\n".join(index) + "\n", encoding="utf-8")
    report["artifacts"]["index.md"] = digest((out_path / "index.md").read_bytes())
    (out_path / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return {"mechanical_status": "ok", "quality_verdict": "NOT PROVIDED", "snapshot": out,
            "packet_bytes": len(packet), "test_reports": len(inspected["tests"]), "mutations": len(inspected["mutations"])}


def verify(root, snapshot):
    report = load_json(read_file(root, str(Path(snapshot) / "report.json")))
    need(report.get("schema_version") == 1 and report.get("root") == str(root.resolve()), "snapshot root/schema mismatch")
    need(report.get("mechanical_status") == "ok", "snapshot not successful")
    need(git(root, "rev-parse", "HEAD").decode().strip() == report["head"], "snapshot HEAD drift")
    need(git(root, "branch", "--show-current").decode().strip() == report["branch"], "snapshot branch drift")
    for item in report["inputs"]:
        need(digest(read_file(root, item["path"])) == item["sha256"], "snapshot input drift: " + item["path"])
    for name, sha in report["artifacts"].items():
        need(digest(read_file(root, str(Path(snapshot) / name))) == sha, "snapshot artifact drift: " + name)
    if report["sources"]:
        for name, options in (("scoped-unstaged.diff", []), ("scoped-staged.diff", ["--cached"])):
            current = git(root, "diff", *options, "--no-ext-diff", "--no-textconv", "--", *report["sources"])
            need(digest(current) == report["artifacts"][name], "snapshot scope diff drift: " + name)
    return {"mechanical_status": "ok", "quality_verdict": "NOT PROVIDED",
            "note": "input hashes/HEAD/branch match; unrelated workspace changes need fresh manual classification"}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".")
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("audit")
    p.add_argument("--manifest", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--stage", choices=["evidence", "review", "closeout"], required=True)
    p = sub.add_parser("begin")
    p.add_argument("--manifest", required=True)
    p = sub.add_parser("verify")
    p.add_argument("--snapshot", required=True)
    p = sub.add_parser("trx")
    p.add_argument("--final", required=True)
    p.add_argument("--baseline")
    args = parser.parse_args(argv)
    root = Path(args.root).resolve()
    try:
        if args.command == "begin":
            result = begin(root, args.manifest)
        elif args.command == "audit":
            result = audit(root, args.manifest, args.out, args.stage)
        elif args.command == "verify":
            result = verify(root, args.snapshot)
        else:
            final = parse_trx(read_file(root, args.final))
            result = {"final": final, "quality_verdict": "NOT PROVIDED"}
            if args.baseline:
                result["comparison"] = compare_trx(parse_trx(read_file(root, args.baseline)), final)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    except (EvidenceError, OSError, ValueError, TypeError, KeyError, ET.ParseError) as e:
        print(json.dumps({"mechanical_status": "blocked", "quality_verdict": "NOT PROVIDED", "error": str(e)}, ensure_ascii=False))
        return 2


if __name__ == "__main__":
    sys.exit(main())
