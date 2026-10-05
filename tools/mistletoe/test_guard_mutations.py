"""Break key guards in a disposable tool copy and require a specific test to fail."""
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


class GuardDiscrimination(unittest.TestCase):
    def test_bad_guard_implementations_are_detected(self):
        here = Path(__file__).resolve().parent
        original = (here / "workflow.py").read_text(encoding="utf-8")
        cases = [
            ("green", 'return trx["failed"] == 0 and trx["passed"] > 0',
             'return trx["passed"] > 0', "TrxChecks.test_duplicate_display_name_cannot_hide_failure"),
            ("identity", '"rows": rows,', '"rows": list({r["name"]: r for r in rows}.values()),',
             "TrxChecks.test_same_test_id_multiple_executions_preserved"),
            ("packet", 'need(len(packet) <= limit, f"packet exceeds limit: {len(packet)} > {limit}; no automatic truncation")',
             'packet = packet[:limit]', "SnapshotChecks.test_packet_over_limit_no_output_or_truncation"),
            ("source", 'need(digest(read_file(root, item["path"])) == item["sha256"],',
             'need(True,', "SnapshotChecks.test_source_drift_invalidates_snapshot"),
            ("restore", 'need(record["original_sha256"] == record["restored_sha256"] == sha,',
             'need(True,', "MutationChecks.test_unrestored_source_not_kill"),
            ("assertion", 'and record["assertion_contains"] in matched[1]["stack"],',
             ',', "MutationChecks.test_wrong_assertion_not_kill"),
            ("risk-dimensions", 'need(dimensions == RISK_DIMENSIONS, "state/concurrency/fault dimensions all required")',
             'need(True, "state/concurrency/fault dimensions all required")',
             "ReviewGateChecks.test_all_three_dimensions_required"),
            ("v1-bypass", 'need((manifest_path.replace("\\\\", "/"), manifest.get("batch")) in LEGACY_CODE_REVIEWS,',
             'need(True,', "ReviewGateChecks.test_new_code_batch_cannot_revert_to_v1"),
            ("important-disposition", 'finding.get("disposition") == "candidate_fixed"',
             'True', "ReviewGateChecks.test_important_finding_cannot_be_marked_unresolved"),
        ]
        for label, old, new, target in cases:
            with self.subTest(guard=label), tempfile.TemporaryDirectory() as temp:
                self.assertEqual(original.count(old), 1, "mutation anchor must be unique")
                candidate = original.replace(old, new)
                compile(candidate, "workflow.py", "exec")  # Syntax errors never count as detection.
                dest = Path(temp)
                (dest / "workflow.py").write_text(candidate, encoding="utf-8")
                for support in ("storage_limits.py", "review_process.py", "review_support.py", "execution_evidence.py", "snapshot_reader.py", "legacy-openings.json"):
                    (dest / support).write_bytes((here / support).read_bytes())
                (dest / "test_workflow.py").write_bytes((here / "test_workflow.py").read_bytes())
                (dest / "test_review_gate.py").write_bytes((here / "test_review_gate.py").read_bytes())
                module = "test_review_gate" if target.startswith("ReviewGateChecks.") else "test_workflow"
                run = subprocess.run([sys.executable, "-B", "-m", "unittest", module + "." + target],
                                     cwd=dest, capture_output=True, text=True)
                self.assertNotEqual(run.returncode, 0)
                self.assertIn("FAIL:", run.stderr)
                self.assertNotIn("ERROR:", run.stderr)


if __name__ == "__main__":
    unittest.main()
