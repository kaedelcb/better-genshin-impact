"""Focused regression tests for the future-batch consultation triage."""

import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from consultation_triage import FACTORS, assess


class ConsultationTriageTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        source = self.root / "source.cs"
        source.write_text("class Example {}\n", encoding="utf-8")
        self.hashes = {"source.cs": hashlib.sha256(source.read_bytes()).hexdigest()}
        self.manifest = {"schema_version": 2, "batch": "future-batch",
                         "opening_snapshot": "_workflow/future-batch/opening.json",
                         "sources": ["source.cs"]}
        self.opening = {"batch": "future-batch", "source_hashes": self.hashes}
        design = self.root / "_workflow" / "future-batch" / "preflight-design.md"
        design.parent.mkdir(parents=True)
        design.write_text("State and failure matrix for review.\n", encoding="utf-8")
        self.design_hash = hashlib.sha256(design.read_bytes()).hexdigest()
        self.assessment = {
            "schema_version": 1, "batch": "future-batch",
            "opening_snapshot": self.manifest["opening_snapshot"],
            "design_contract_path": "_workflow/future-batch/preflight-design.md",
            "factors": {name: {"applies": False, "evidence": "核对当前入口与合同",
                               "consequence_or_exclusion": "本批未改该入口"}
                        for name in FACTORS},
            "equivalent_review": {"available": False, "reason": "没有同范围前审"},
        }

    def test_no_risk_adds_no_preflight(self):
        result = assess(self.root, self.manifest, self.opening, self.assessment)
        self.assertEqual(result["decision"], "no_listed_risk_hit")
        self.assertEqual(result["model_selection"], "requires_current_per_request_assessment")

    def test_durable_change_requires_fresh_model_assessment(self):
        factor = self.assessment["factors"]["durable_migration_or_rollback"]
        factor.update(applies=True, entry_or_contract="Migration.Commit",
                      consequence_or_exclusion="回滚后错误删除他方文件")
        result = assess(self.root, self.manifest, self.opening, self.assessment)
        self.assertEqual(result["decision"], "risk_attention_required")
        self.assertEqual(result["model_selection"], "requires_current_per_request_assessment")
        self.assertEqual(result["design_contract_sha256"], self.design_hash)

    def test_equivalent_review_must_match_opening_sources(self):
        self.assessment["factors"]["authority_transition"].update(
            applies=True, entry_or_contract="Admission.Commit",
            consequence_or_exclusion="重复执行")
        report = self.root / "review.md"
        report.write_text("Independent review", encoding="utf-8")
        previous = self.assessment["equivalent_review"]
        previous.update(available=True, report_path="review.md",
                        source_hashes={"source.cs": "0" * 64},
                        same_scope_and_contract="同一受理合同", reason="已有前审")
        with self.assertRaisesRegex(ValueError, "hashes differ"):
            assess(self.root, self.manifest, self.opening, self.assessment)
        previous["source_hashes"] = self.hashes
        self.assertEqual(assess(self.root, self.manifest, self.opening,
                                self.assessment)["decision"], "equivalence_needs_assessment")

    def test_missing_factor_or_stale_source_blocks(self):
        del self.assessment["factors"]["cross_process_or_generation"]
        with self.assertRaisesRegex(ValueError, "all five"):
            assess(self.root, self.manifest, self.opening, self.assessment)
        self.assessment["factors"]["cross_process_or_generation"] = {
            "applies": False, "evidence": "核对路径", "consequence_or_exclusion": "未修改"}
        (self.root / "source.cs").write_text("changed", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "source drift"):
            assess(self.root, self.manifest, self.opening, self.assessment)

    def test_hit_requires_nonempty_design_in_same_batch(self):
        self.assessment["factors"]["authority_transition"].update(
            applies=True, entry_or_contract="Admission.Commit",
            consequence_or_exclusion="unauthorized commit")
        self.assessment["design_contract_path"] = "_workflow/other/preflight-design.md"
        with self.assertRaisesRegex(ValueError, "this batch"):
            assess(self.root, self.manifest, self.opening, self.assessment)
        self.assessment["design_contract_path"] = "_workflow/future-batch/preflight-design.md"
        (self.root / self.assessment["design_contract_path"]).write_text("", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "missing or empty"):
            assess(self.root, self.manifest, self.opening, self.assessment)

    def test_cli_writes_decision_once_without_sending_consultation(self):
        work = self.root / "_workflow" / "future-batch"
        work.mkdir(parents=True, exist_ok=True)
        (work / "manifest.json").write_text(json.dumps(self.manifest), encoding="utf-8")
        (work / "opening.json").write_text(json.dumps(self.opening), encoding="utf-8")
        (work / "assessment.json").write_text(json.dumps(self.assessment), encoding="utf-8")
        script = Path(__file__).with_name("consultation_triage.py")
        command = [sys.executable, "-B", str(script), "--root", str(self.root),
                   "--manifest", "_workflow/future-batch/manifest.json",
                   "--assessment", "_workflow/future-batch/assessment.json",
                   "--out", "_workflow/future-batch/decision.json"]
        first = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(first.returncode, 0, first.stdout + first.stderr)
        decision = json.loads((work / "decision.json").read_text(encoding="utf-8"))
        self.assertEqual(decision["decision"], "no_listed_risk_hit")
        second = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(second.returncode, 2, second.stdout + second.stderr)


if __name__ == "__main__":
    unittest.main()
