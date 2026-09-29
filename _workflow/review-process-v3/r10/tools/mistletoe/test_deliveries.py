import copy
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import deliveries


class DeliveryTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        folder = self.root / "_r61_integration"
        folder.mkdir()
        self.report = folder / "report.md"
        self.report.write_text("candidate only", encoding="utf-8")
        self.row = {"id": "r61", "worktree": str(self.root), "head": "a" * 40,
                    "report": "_r61_integration/report.md", "integration_state": "blocked",
                    "target_batch": "R6.1", "blockers": ["resources"],
                    "files": [{"path": "_r61_integration/report.md",
                               "sha256": deliveries.sha(self.report)}]}

    def run_inspect(self, rows=None, head=None, dirty=False):
        def fake_git(root, *args):
            if args[0] == "rev-parse":
                return head or "a" * 40
            if args[0] == "status":
                return " M report.md" if dirty else ""
            return "tracked"
        with patch.object(deliveries, "git", side_effect=fake_git), \
                patch.object(deliveries, "worktrees", return_value=[self.root]):
            return deliveries.inspect(self.root, {"schema_version": 1,
                                                  "deliveries": rows if rows is not None else [self.row]})

    def test_complete_record_stays_blocked_without_merge(self):
        result = self.run_inspect()
        self.assertTrue(result["ok"])
        self.assertFalse(result["automatic_merge"])
        self.assertEqual("blocked", result["queue"][0]["integration_state"])

    def test_head_drift_is_not_silently_consumed(self):
        self.assertIn("HEAD drift", " ".join(self.run_inspect(head="b" * 40)["errors"]))

    def test_report_drift_is_detected(self):
        self.report.write_text("new unreviewed material", encoding="utf-8")
        self.assertIn("file drift", " ".join(self.run_inspect()["errors"]))

    def test_uncommitted_material_is_detected(self):
        self.assertIn("uncommitted", " ".join(self.run_inspect(dirty=True)["errors"]))

    def test_missing_report_is_detected(self):
        self.report.unlink()
        self.assertIn("missing delivery", " ".join(self.run_inspect()["errors"]))

    def test_new_report_cannot_be_ignored(self):
        extra = self.root / "_r62_bgi_guard"
        extra.mkdir()
        (extra / "report.md").write_text("unknown outcome", encoding="utf-8")
        result = self.run_inspect()
        self.assertFalse(result["ok"])
        self.assertEqual("_r62_bgi_guard/report.md", result["unregistered_reports"][0]["report"])

    def test_duplicate_ids_are_detected(self):
        self.assertIn("duplicate", " ".join(self.run_inspect([self.row, copy.deepcopy(self.row)])["errors"]))

    def test_integrated_label_requires_receipt(self):
        row = copy.deepcopy(self.row)
        row["integration_state"] = "integrated"
        self.assertIn("receipt", " ".join(self.run_inspect([row])["errors"]))

    def test_bad_state_rejected(self):
        row = copy.deepcopy(self.row)
        row["integration_state"] = "ready-to-release"
        self.assertFalse(self.run_inspect([row])["ok"])

    def test_parent_traversal_rejected(self):
        with self.assertRaises(ValueError):
            deliveries.safe_file(self.root, "_r61_integration/../report.md")

    def test_user_path_rejected(self):
        with self.assertRaises(ValueError):
            deliveries.safe_file(self.root, "User/report.md")

    def test_workflow_checkpoint_can_be_registered(self):
        self.report.unlink()
        report = self.root / "_workflow" / "wave3-bo6-bo7" / "owner-checkpoint.md"
        report.parent.mkdir(parents=True)
        report.write_text("independent batch checkpoint", encoding="utf-8")
        row = copy.deepcopy(self.row)
        row["report"] = "_workflow/wave3-bo6-bo7/owner-checkpoint.md"
        row["files"] = [{"path": row["report"], "sha256": deliveries.sha(report)}]
        self.assertTrue(self.run_inspect([row])["ok"])


if __name__ == "__main__":
    unittest.main()
