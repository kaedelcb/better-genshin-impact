"""Counterexamples for evidence tooling. Product tests are not modified."""
import contextlib
import copy
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

import workflow as w


def trx(rows, outcome=None):
    root = ET.Element("TestRun", xmlns=w.NS["t"])
    results = ET.SubElement(root, "Results")
    defs = ET.SubElement(root, "TestDefinitions")
    defined = set()
    for i, row in enumerate(rows):
        tid, name, result = row[:3]
        r = ET.SubElement(results, "UnitTestResult", testId=tid, testName=name,
                          executionId=f"exec-{i}", outcome=result)
        if len(row) > 3:
            error = ET.SubElement(ET.SubElement(r, "Output"), "ErrorInfo")
            ET.SubElement(error, "Message").text = row[3]
            ET.SubElement(error, "StackTrace").text = row[4]
        if tid not in defined:
            d = ET.SubElement(defs, "UnitTest", id=tid)
            ET.SubElement(d, "TestMethod", className="Fixture", name="Method")
            defined.add(tid)
    passed = sum(r[2] == "Passed" for r in rows)
    failed = sum(r[2] == "Failed" for r in rows)
    summary = ET.SubElement(root, "ResultSummary", outcome=outcome or ("Failed" if failed else "Completed"))
    ET.SubElement(summary, "Counters", total=str(len(rows)), executed=str(passed + failed),
                  passed=str(passed), failed=str(failed), notExecuted="0", error="0", aborted="0")
    return ET.tostring(root, encoding="utf-8")


class TrxChecks(unittest.TestCase):
    def test_duplicate_display_name_cannot_hide_failure(self):
        report = w.parse_trx(trx([("a", "same", "Failed"), ("b", "same", "Passed")], "Completed"))
        self.assertEqual((report["total"], report["failed"]), (2, 1))
        self.assertEqual(report["duplicate_names"], {"same": 2})
        self.assertFalse(w.successful(report))

    def test_same_test_id_multiple_executions_preserved(self):
        report = w.parse_trx(trx([("a", "same", "Passed"), ("a", "same", "Passed")]))
        self.assertEqual(len(report["rows"]), 2)

    def test_wrong_counter_rejected(self):
        data = trx([("a", "case", "Failed")]).replace(b'failed="1"', b'failed="0"')
        with self.assertRaises(w.EvidenceError): w.parse_trx(data)

    def test_duplicate_execution_rejected(self):
        data = trx([("a", "same", "Passed"), ("b", "same", "Passed")]).replace(b'exec-1', b'exec-0')
        with self.assertRaises(w.EvidenceError): w.parse_trx(data)

    def test_aborted_run_rejected(self):
        with self.assertRaises(w.EvidenceError): w.parse_trx(trx([("a", "case", "Passed")], "Aborted"))

    def test_skipped_xunit_counter_convention(self):
        report = w.parse_trx(trx([("a", "case", "Passed"), ("b", "skip", "NotExecuted")]))
        self.assertEqual(report["skipped"], 1)

    def test_all_skipped_cannot_be_green(self):
        self.assertFalse(w.successful(w.parse_trx(trx([("a", "skip", "NotExecuted")]))))

    def test_test_identity_diff_not_name_diff(self):
        a = w.parse_trx(trx([("a", "same", "Passed"), ("b", "same", "Passed")]))
        b = w.parse_trx(trx([("a", "same", "Passed")]))
        self.assertEqual(w.compare_trx(a, b)["removed_ids"], ["b"])

    def test_unknown_outcome_and_malformed_xml_rejected(self):
        with self.assertRaises(w.EvidenceError): w.parse_trx(trx([("a", "case", "Error")]))
        with self.assertRaises(ET.ParseError): w.parse_trx(b"<truncated")


class MutationChecks(unittest.TestCase):
    def setUp(self):
        self.files = {"source.cs": b"original", "build.log": b"Build succeeded",
                      "before.trx": trx([("a", "target", "Passed")]),
                      "bad.trx": trx([("a", "target", "Failed", "expected reason", "Fixture.cs:line 42")]),
                      "after.trx": trx([("a", "target", "Passed"), ("b", "other", "Passed")])}
        self.record = dict(id="guard", source="source.cs", original_sha256=w.digest(b"original"),
                           restored_sha256=w.digest(b"original"), build_log="build.log", build_exit=0,
                           baseline_exit=0, mutant_exit=1, restored_exit=0, baseline_trx="before.trx",
                           mutant_trx="bad.trx", restored_trx="after.trx", target_test_id="a", target_name="target",
                           failure_contains="expected reason", assertion_contains="Fixture.cs:line 42")

    def check(self):
        return w.mutation_check(self.record, self.files.__getitem__, lambda p: w.parse_trx(self.files[p]))

    def test_valid_assertion_kill(self):
        self.assertEqual(self.check()["mechanical_status"], "ok")

    def test_build_failure_not_kill(self):
        self.record["build_exit"] = 1
        with self.assertRaises(w.EvidenceError): self.check()

    def test_wrong_assertion_not_kill(self):
        self.record["assertion_contains"] = "line 99"
        with self.assertRaises(w.EvidenceError): self.check()

    def test_unrestored_source_not_kill(self):
        self.files["source.cs"] = b"mutated"
        with self.assertRaises(w.EvidenceError): self.check()

    def test_target_skipped_not_kill(self):
        self.files["bad.trx"] = trx([("a", "target", "NotExecuted")])
        with self.assertRaises(w.EvidenceError): self.check()

    def test_reused_trx_not_kill(self):
        self.record["restored_trx"] = "before.trx"
        with self.assertRaises(w.EvidenceError): self.check()


class SnapshotChecks(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name).resolve()
        subprocess.run(["git", "init", "-q", str(self.root)], check=True)
        (self.root / "fixture.txt").write_text("baseline", encoding="utf-8")
        subprocess.run(["git", "-C", str(self.root), "add", "--", "fixture.txt"], check=True)
        subprocess.run(["git", "-C", str(self.root), "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                        "commit", "--only", "-qm", "baseline", "--", "fixture.txt"], check=True)
        for name, text in (("scope.md", "source contract\n"), ("context.md", "goal, findings, consultation budget\n")):
            (self.root / name).write_text(text, encoding="utf-8")
        self.manifest = {"schema_version": 2, "batch": "fixture", "mode": "documents", "sources": ["scope.md"],
                         "opening_snapshot": "_workflow/fixture/opening-doc.json",
                         "review_control": {"existing_results": {"decision": "none", "reason": "fixture only"},
                                            "review_round": 1, "prior_findings": [],
                                            "subagents": {"decision": "not_used", "reason": "fixture only", "tasks": []}},
                         "validation_reason": "documents only", "mutation_scope": "not applicable to product",
                         "evidence": [dict(id="doc", path="scope.md", purpose="contract", level="document", conditions="fixture")],
                         "outside_changes": "context is this fixture; no other writer",
                         "packet": [dict(path="scope.md", role="source")] +
                                   [dict(path="context.md", role=r) for r in ("objective", "findings", "budget")]}
        (self.root / "manifest.json").write_text(json.dumps(self.manifest), encoding="utf-8")
        with patch("review_process.check_new"):
            w.begin(self.root, "manifest.json")
        opening_file = self.root / self.manifest["opening_snapshot"]
        old_opening = json.loads(opening_file.read_text(encoding="utf-8-sig"))
        old_opening.pop("review_process_required", None)
        opening_file.write_text(json.dumps(old_opening), encoding="utf-8")
        legacy = patch("review_process.legacy_allowed", return_value=True)
        legacy.start()
        self.addCleanup(legacy.stop)

    def audit(self, stage="review"):
        (self.root / "manifest.json").write_text(json.dumps(self.manifest), encoding="utf-8")
        return w.audit(self.root, "manifest.json", "_workflow/snapshot", stage)

    def test_snapshot_and_verify(self):
        self.audit()
        self.assertEqual(w.verify(self.root, "_workflow/snapshot")["mechanical_status"], "ok")

    def test_document_batch_cannot_bypass_opening_or_acceleration_assessment(self):
        self.manifest["opening_snapshot"] = "_workflow/fixture/missing.json"
        with self.assertRaises(w.EvidenceError): self.audit()
        self.manifest["opening_snapshot"] = "_workflow/fixture/opening-doc.json"
        del self.manifest["review_control"]["existing_results"]
        with self.assertRaises(w.EvidenceError): self.audit()

    def test_document_batch_cannot_revert_to_v1(self):
        self.manifest["schema_version"] = 1
        with self.assertRaises(w.EvidenceError): self.audit()

    def test_document_re_review_needs_batched_disposition(self):
        self.manifest["review_control"]["review_round"] = 2
        with self.assertRaises(w.EvidenceError): self.audit()

    def test_packet_over_limit_no_output_or_truncation(self):
        self.manifest["packet_limit_bytes"] = 10
        with self.assertRaises(w.EvidenceError): self.audit()
        self.assertFalse((self.root / "_workflow/snapshot").exists())

    def test_excerpt_cannot_hide_origin_or_coverage(self):
        (self.root / "scope.md").write_text("line one\nline two\n", encoding="utf-8")
        self.manifest["packet"][0]["end_line"] = 1
        with self.assertRaises(w.EvidenceError): self.audit()
        self.manifest["packet"][0]["coverage_notes"] = "Second line outside fixture contract; manually reviewed"
        self.audit()
        self.assertIn("L1-L1 SHA256=", (self.root / "_workflow/snapshot/packet.md").read_text(encoding="utf-8"))

    def test_head_change_invalidates_snapshot(self):
        self.audit()
        (self.root / "fixture.txt").write_text("next", encoding="utf-8")
        subprocess.run(["git", "-C", str(self.root), "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                        "commit", "--only", "-qm", "next", "--", "fixture.txt"], check=True)
        with self.assertRaises(w.EvidenceError): w.verify(self.root, "_workflow/snapshot")

    def test_missing_role_or_scope_rejected(self):
        for remove in ("budget", "source"):
            original = copy.deepcopy(self.manifest)
            self.manifest["packet"] = [p for p in self.manifest["packet"] if p["role"] != remove]
            with self.assertRaises(w.EvidenceError): self.audit()
            self.manifest = original

    def test_missing_evidence_rejected(self):
        self.manifest["evidence"][0]["path"] = "missing.txt"
        with self.assertRaises(w.EvidenceError): self.audit()

    def test_source_drift_invalidates_snapshot(self):
        self.audit()
        (self.root / "scope.md").write_text("changed", encoding="utf-8")
        with self.assertRaises(w.EvidenceError): w.verify(self.root, "_workflow/snapshot")

    def test_packet_drift_invalidates_snapshot(self):
        self.audit()
        (self.root / "_workflow/snapshot/packet.md").write_text("omitted", encoding="utf-8")
        with self.assertRaises(w.EvidenceError): w.verify(self.root, "_workflow/snapshot")

    def test_staging_drift_invalidates_snapshot(self):
        self.audit()
        subprocess.run(["git", "-C", str(self.root), "add", "--", "scope.md"], check=True)
        with self.assertRaises(w.EvidenceError): w.verify(self.root, "_workflow/snapshot")

    def test_existing_output_never_overwritten(self):
        self.audit()
        old = (self.root / "_workflow/snapshot/report.json").read_bytes()
        with self.assertRaises(w.EvidenceError): self.audit()
        self.assertEqual((self.root / "_workflow/snapshot/report.json").read_bytes(), old)

    def test_historical_cannot_pass_closeout(self):
        self.manifest["mode"] = "historical"
        with self.assertRaises(w.EvidenceError): self.audit("closeout")

    def test_code_without_bindings_or_tests_rejected(self):
        self.manifest["mode"] = "code"
        with self.assertRaises(w.EvidenceError): self.audit()

    def code_manifest(self):
        (self.root / "scope.cs").write_text("source", encoding="utf-8")
        (self.root / "final.trx").write_bytes(trx([("a", "target", "Passed")]))
        hashes = {"scope.cs": w.digest(b"source")}
        self.manifest["mode"] = "code"
        self.manifest["sources"] = ["scope.cs"]
        self.manifest["evidence"] = [dict(id="final", path="final.trx", purpose="regression", level="test",
                                          conditions="fixture execution", source_sha256=hashes)]
        self.manifest["tests"] = [dict(path="final.trx", expect_success=True)]
        self.manifest["packet"][0]["path"] = "scope.cs"
        self.manifest["schema_version"] = 2
        self.manifest["risk_matrix"] = "_workflow/code/risk-matrix.json"
        self.manifest["opening_snapshot"] = "_workflow/code/opening.json"
        self.manifest["review_control"] = {"review_round": 1, "prior_findings": [],
                                            "criticality_reason": "synthetic fixture only",
                                            "existing_results": {"decision": "none", "reason": "fixture only"},
                                            "subagents": {"decision": "not_used",
                                                          "reason": "synthetic fixture has no parallel audit",
                                                          "tasks": []}}
        matrix = {"schema_version": 1, "batch": "fixture", "rows": [
            {"id": kind, "dimension": kind, "scenario": kind + " fixture path",
             "expected": "safe", "critical": False, "status": "planned"}
            for kind in ("state", "concurrency", "fault")]}
        matrix_path = self.root / self.manifest["risk_matrix"]
        matrix_path.parent.mkdir(parents=True, exist_ok=True)
        matrix_path.write_text(json.dumps(matrix), encoding="utf-8")
        (self.root / "manifest.json").write_text(json.dumps(self.manifest), encoding="utf-8")
        with patch("review_process.check_new"):
            w.begin(self.root, "manifest.json")
        opening_file = self.root / self.manifest["opening_snapshot"]
        old_opening = json.loads(opening_file.read_text(encoding="utf-8-sig"))
        old_opening.pop("review_process_required", None)
        opening_file.write_text(json.dumps(old_opening), encoding="utf-8")
        legacy = patch("review_process.legacy_allowed", return_value=True)
        legacy.start()
        self.addCleanup(legacy.stop)
        for row in matrix["rows"]:
            row.update(status="covered", counterexample_ids=["final"], test_ids=["a"])
        matrix_path.write_text(json.dumps(matrix), encoding="utf-8")

    def test_current_code_bindings_and_green_trx(self):
        self.code_manifest()
        self.audit()
        self.assertEqual(w.verify(self.root, "_workflow/snapshot")["mechanical_status"], "ok")

    def test_stale_execution_binding_rejected(self):
        self.code_manifest()
        self.manifest["evidence"][0]["source_sha256"]["scope.cs"] = "0" * 64
        with self.assertRaises(w.EvidenceError): self.audit()

    def test_historical_baseline_allowed_for_comparison(self):
        self.code_manifest()
        (self.root / "baseline.trx").write_bytes(trx([("b", "old", "Passed")]))
        self.manifest["evidence"].append(dict(id="baseline", path="baseline.trx", purpose="before fix",
                                             level="test", conditions="old version", binding="historical",
                                             source_sha256={"scope.cs": "0" * 64}))
        self.manifest["comparison"] = dict(baseline="baseline.trx", final="final.trx")
        self.audit()

    def test_historical_evidence_cannot_claim_current_green(self):
        self.code_manifest()
        self.manifest["evidence"][0]["binding"] = "historical"
        with self.assertRaises(w.EvidenceError): self.audit()

    def test_only_red_fixture_not_current_regression(self):
        self.code_manifest()
        self.manifest["tests"][0]["expect_success"] = False
        with self.assertRaises(w.EvidenceError): self.audit()

    def test_document_mode_cannot_hide_source_code(self):
        (self.root / "source.cs").write_text("code", encoding="utf-8")
        self.manifest["sources"] = ["source.cs"]
        with self.assertRaises(w.EvidenceError): self.audit()

    def test_capture_detects_concurrent_input_change(self):
        original = w.read_file
        reads = 0
        def read(root, relative):
            nonlocal reads
            if relative == "scope.md":
                reads += 1
                if reads == 2: return b"concurrent edit"
            return original(root, relative)
        with patch.object(w, "read_file", read):
            with self.assertRaises(w.EvidenceError): self.audit()

    def test_unsafe_paths(self):
        for path in ("../escape", "User/config.json", "x/bin/log", ".kiro/a", str(self.root / "scope.md")):
            with self.assertRaises(w.EvidenceError): w.input_path(self.root, path)

    def test_duplicate_json_key_rejected(self):
        with self.assertRaises(w.EvidenceError): w.load_json(b'{"a":1,"a":2}')

    def test_cli_error_nonzero(self):
        with contextlib.redirect_stdout(io.StringIO()) as output:
            code = w.main(["--root", str(self.root), "trx", "--final", "missing.trx"])
        self.assertEqual(code, 2)
        self.assertIn('"mechanical_status": "blocked"', output.getvalue())


if __name__ == "__main__":
    unittest.main()
