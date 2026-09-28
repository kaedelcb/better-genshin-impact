"""Counterexamples for the future-batch review readiness gate."""
import copy
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

import workflow as w
from test_workflow import trx


class ReviewGateChecks(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name).resolve()
        subprocess.run(["git", "init", "-q", str(self.root)], check=True)
        (self.root / "baseline.txt").write_text("baseline", encoding="utf-8")
        subprocess.run(["git", "-C", str(self.root), "add", "--", "baseline.txt"], check=True)
        subprocess.run(["git", "-C", str(self.root), "-c", "user.name=Test",
                        "-c", "user.email=test@example.invalid", "commit", "--only", "-qm",
                        "baseline", "--", "baseline.txt"], check=True)
        (self.root / "scope.cs").write_text("source", encoding="utf-8")
        (self.root / "final.trx").write_bytes(trx([("a", "target", "Passed")]))
        (self.root / "context.md").write_text("objective, findings, budget\n", encoding="utf-8")
        self.matrix = {"schema_version": 1, "batch": "next-batch", "rows": [
            {"id": dimension, "dimension": dimension, "scenario": dimension + " path",
             "expected": "safe result", "critical": False, "status": "planned"}
            for dimension in ("state", "concurrency", "fault")]}
        self.manifest = {"schema_version": 2, "batch": "next-batch", "mode": "code",
                         "sources": ["scope.cs"], "risk_matrix": "_workflow/next-batch/risk-matrix.json",
                         "opening_snapshot": "_workflow/next-batch/opening.json",
                         "evidence": [{"id": "final", "path": "final.trx", "purpose": "current regression",
                                       "level": "test", "conditions": "fixture execution",
                                       "source_sha256": {"scope.cs": w.digest(b"source")}}],
                         "tests": [{"path": "final.trx", "expect_success": True}],
                         "mutation_scope": "no critical assertions in this synthetic fixture",
                         "mutations": [], "outside_changes": "only fixture files",
                         "packet": [{"path": "scope.cs", "role": "source"}]
                                   + [{"path": "context.md", "role": role}
                                      for role in ("objective", "findings", "budget")],
                         "review_control": {"review_round": 1, "prior_findings": [],
                                            "criticality_reason": "synthetic fixture has no critical product state",
                                            "existing_results": {"decision": "none",
                                                                 "reason": "no previous delivery applies"},
                                            "subagents": {"decision": "not_used",
                                                          "reason": "no independent useful read-only task",
                                                          "tasks": []}}}
        self.write_matrix()
        self.write_manifest()
        w.begin(self.root, "manifest.json")
        for row in self.matrix["rows"]:
            row.update(status="covered", counterexample_ids=["final"], test_ids=["a"])
        self.write_matrix()

    def write_manifest(self):
        (self.root / "manifest.json").write_text(json.dumps(self.manifest), encoding="utf-8")

    def write_matrix(self):
        path = self.root / "_workflow/next-batch/risk-matrix.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(self.matrix), encoding="utf-8")

    def audit(self):
        self.write_manifest()
        self.write_matrix()
        return w.audit(self.root, "manifest.json", "_workflow/next-batch/review", "review")

    def rejects(self, fragment):
        with self.assertRaises(w.EvidenceError) as caught:
            self.audit()
        self.assertIn(fragment, str(caught.exception))
        self.assertFalse((self.root / "_workflow/next-batch/review").exists())

    def test_complete_batch_snapshot_and_verify(self):
        self.audit()
        report = w.load_json((self.root / "_workflow/next-batch/review/report.json").read_bytes())
        self.assertEqual(report["review_readiness"]["risk_rows"], 3)
        packet = (self.root / "_workflow/next-batch/review/packet.md").read_text(encoding="utf-8")
        self.assertIn('"dimension": "fault"', packet)
        self.assertEqual(w.verify(self.root, "_workflow/next-batch/review")["mechanical_status"], "ok")

    def test_begin_never_overwrites_opening_receipt(self):
        path = self.root / "_workflow/next-batch/opening.json"
        original = path.read_bytes()
        with self.assertRaises(w.EvidenceError):
            w.begin(self.root, "manifest.json")
        self.assertEqual(path.read_bytes(), original)

    def test_new_code_batch_cannot_revert_to_v1(self):
        self.manifest["schema_version"] = 1
        self.rejects("new batch reviews require v2")

    def test_in_progress_sb21_4_exact_v1_identity_remains_accepted(self):
        self.manifest["schema_version"] = 1
        self.manifest["batch"] = "SB21-4 BO-13 R5 formal re-review; BO-6/7 not started"
        path = self.root / "_workflow/sb21-4/manifest.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(self.manifest), encoding="utf-8")
        w.audit(self.root, "_workflow/sb21-4/manifest.json", "_workflow/sb21-4/legacy-review", "review")

    def test_missing_reuse_assessment_rejected(self):
        del self.manifest["review_control"]["existing_results"]
        self.rejects("existing-results reuse decision")

    def test_missing_opening_snapshot_rejected(self):
        self.manifest["opening_snapshot"] = "_workflow/next-batch/missing.json"
        self.rejects("missing file")

    def test_all_three_dimensions_required(self):
        self.matrix["rows"].pop()
        self.rejects("state/concurrency/fault")

    def test_duplicate_row_id_rejected(self):
        self.matrix["rows"][1]["id"] = "state"
        self.rejects("duplicate risk row id")

    def test_unresolved_row_cannot_be_sent_as_ready(self):
        self.matrix["rows"][0]["status"] = "planned"
        self.rejects("not resolved for review")

    def test_not_applicable_requires_reason(self):
        self.matrix["rows"][0]["status"] = "not_applicable"
        self.rejects("needs a reason")

    def test_all_not_applicable_cannot_claim_risk_coverage(self):
        for row in self.matrix["rows"]:
            row.update(status="not_applicable", reason="fixture")
        self.rejects("all risk rows cannot be not-applicable")

    def test_no_critical_rows_requires_explicit_assessment(self):
        del self.manifest["review_control"]["criticality_reason"]
        self.rejects("no critical risks requires")

    def test_opening_scenario_cannot_be_silently_rewritten(self):
        self.matrix["rows"][0]["scenario"] = "narrower path"
        self.rejects("opening risk row changed")

    def test_missing_counterexample_rejected(self):
        self.matrix["rows"][0]["counterexample_ids"] = ["nonexistent"]
        self.rejects("missing indexed test/runtime evidence")

    def test_claimed_test_id_must_exist_in_trx(self):
        self.matrix["rows"][0]["test_ids"] = ["invented"]
        self.rejects("test id absent from passed TRX")

    def test_test_level_counterexample_must_be_executed_trx(self):
        (self.root / "unexecuted.txt").write_text("claimed test", encoding="utf-8")
        self.manifest["evidence"].append({"id": "unexecuted", "path": "unexecuted.txt",
                                          "purpose": "claimed test", "level": "test",
                                          "conditions": "no execution", "source_sha256": {"scope.cs": w.digest(b"source")}})
        self.matrix["rows"][0]["counterexample_ids"] = ["unexecuted"]
        self.rejects("needs current green indexed TRX execution")

    def test_historical_trx_cannot_claim_current_risk_coverage(self):
        (self.root / "old.trx").write_bytes(trx([("old", "historical", "Passed")]))
        self.manifest["evidence"].append({"id": "old", "path": "old.trx", "purpose": "historical baseline",
                                          "level": "test", "conditions": "old source", "binding": "historical",
                                          "source_sha256": {"scope.cs": "0" * 64}})
        self.manifest["tests"].append({"path": "old.trx", "expect_success": False})
        self.matrix["rows"][0].update(counterexample_ids=["old"], test_ids=["old"])
        self.rejects("needs current green indexed TRX execution")

    def test_failed_trx_cannot_claim_risk_coverage(self):
        (self.root / "failed.trx").write_bytes(trx([("bad", "failed", "Failed")]))
        self.manifest["evidence"].append({"id": "failed", "path": "failed.trx", "purpose": "red fixture",
                                          "level": "test", "conditions": "current source",
                                          "source_sha256": {"scope.cs": w.digest(b"source")}})
        self.manifest["tests"].append({"path": "failed.trx", "expect_success": False})
        self.matrix["rows"][0].update(counterexample_ids=["failed"], test_ids=["bad"])
        self.rejects("needs current green indexed TRX execution")

    def test_skipped_test_id_cannot_claim_risk_coverage(self):
        (self.root / "skipped.trx").write_bytes(trx([("other", "passed", "Passed"),
                                                       ("skip", "skipped", "NotExecuted")]))
        self.manifest["evidence"].append({"id": "skipped", "path": "skipped.trx", "purpose": "mixed fixture",
                                          "level": "test", "conditions": "current source",
                                          "source_sha256": {"scope.cs": w.digest(b"source")}})
        self.manifest["tests"].append({"path": "skipped.trx", "expect_success": True})
        self.matrix["rows"][0].update(counterexample_ids=["skipped"], test_ids=["skip"])
        self.rejects("test id absent from passed TRX execution")

    def test_critical_added_row_needs_mutation(self):
        self.matrix["rows"].append({"id": "new-critical", "dimension": "fault",
                                    "scenario": "late failure", "expected": "safe result",
                                    "critical": True, "status": "covered",
                                    "counterexample_ids": ["final"], "test_ids": ["a"]})
        self.rejects("critical risk needs verified mutation ids")

    def test_subagent_use_must_have_fixed_read_only_report(self):
        self.manifest["review_control"]["subagents"] = {
            "decision": "used", "reason": "independent check", "tasks": [
                {"question": "check fault paths", "read_only": True,
                 "fixed_ref": "wrong-ref", "report_evidence_id": "final"}]}
        self.rejects("subagent task needs read-only")

    def test_re_review_needs_batched_findings(self):
        self.manifest["review_control"]["review_round"] = 2
        self.rejects("re-review needs batched findings")

    def test_important_finding_cannot_be_marked_unresolved(self):
        (self.root / "previous.md").write_text("IMPORTANT finding", encoding="utf-8")
        self.manifest["evidence"].append({"id": "prior", "path": "previous.md", "purpose": "previous review",
                                          "level": "consult", "conditions": "fixed source",
                                          "source_sha256": {"scope.cs": w.digest(b"source")}})
        self.manifest["review_control"].update(review_round=2, repair_batch_id="fix-r1",
                                                 prior_findings=[{"id": "F1", "severity": "important",
                                                                  "source_review_evidence_id": "prior",
                                                                  "disposition": "open",
                                                                  "repair_evidence_ids": ["final"]}])
        self.rejects("must/important finding lacks batched repair evidence")

    def test_matrix_drift_invalidates_verified_snapshot(self):
        self.audit()
        self.matrix["rows"][0]["reason"] = "later change"
        self.write_matrix()
        with self.assertRaises(w.EvidenceError):
            w.verify(self.root, "_workflow/next-batch/review")


if __name__ == "__main__":
    unittest.main()
