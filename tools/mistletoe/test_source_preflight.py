"""Isolated Git reproductions for source_preflight; no product or live User data."""

from contextlib import redirect_stdout
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

import source_preflight


class SourcePreflightTests(unittest.TestCase):
    def setUp(self):
        parent = os.environ.get("MISTLETOE_PREFLIGHT_TEST_ROOT")
        if parent:
            Path(parent).mkdir(parents=True, exist_ok=True)
        fixture_parent = Path(parent).resolve() if parent else Path(tempfile.gettempdir()).resolve()
        if parent:
            # Explicit managed evidence roots retain fixtures, including original failures.
            self.root = Path(tempfile.mkdtemp(prefix="preflight-", dir=fixture_parent)).resolve()
        else:
            self.directory = tempfile.TemporaryDirectory(prefix="preflight-", dir=fixture_parent)
            self.root = Path(self.directory.name).resolve()
            def cleanup_fixture():
                resolved = self.root.resolve()
                if resolved == fixture_parent or not resolved.is_relative_to(fixture_parent):
                    raise RuntimeError("Refusing cleanup outside the owned fixture parent")
                self.directory.cleanup()
            self.addCleanup(cleanup_fixture)
        self.assertTrue(self.root.is_relative_to(fixture_parent))
        self.assertNotEqual(self.root, fixture_parent)
        self.git("init", "--initial-branch=main")
        self.git("config", "user.name", "Isolated preflight test")
        self.git("config", "user.email", "preflight-test@example.invalid")
        self.git("config", "commit.gpgsign", "false")
        self.git("config", "core.autocrlf", "false")

    def git(self, *arguments):
        process = subprocess.run(["git", "-C", str(self.root), *arguments],
                                 capture_output=True, check=True)
        return process.stdout.decode("utf-8").strip()

    def write(self, name, value="test\n"):
        target = self.root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(value, encoding="utf-8")

    def commit(self, message="fixture"):
        self.git("add", "--all")
        self.git("commit", "-m", message)
        return self.git("rev-parse", "HEAD")

    def sparse(self, patterns):
        self.git("sparse-checkout", "init", "--no-cone")
        self.git("sparse-checkout", "set", "--no-cone", *patterns)

    def codes(self, result):
        return {item["code"] for item in result["issues"]}

    def snapshot(self):
        return (self.git("status", "--porcelain=v1"), self.git("rev-parse", "HEAD"),
                (self.root / ".git/index").read_bytes(),
                (self.root / ".git/config").read_bytes())

    def test_new_project_is_included_without_allowlist(self):
        self.write("Main/Main.csproj", '<Project><ItemGroup><ProjectReference Include="../New/New.csproj" /></ItemGroup></Project>')
        self.write("New/New.csproj", "<Project />")
        baseline = self.commit()
        self.assertEqual("complete", source_preflight.inspect(self.root, baseline)["status"])

    def test_allowlist_omits_new_project(self):
        self.write("Main/Main.csproj", '<Project><ItemGroup><ProjectReference Include="../New/New.csproj" /></ItemGroup></Project>')
        self.write("New/New.csproj", "<Project />")
        self.commit()
        self.sparse(["/Main/"])
        result = source_preflight.inspect(self.root)
        self.assertEqual("blocked", result["status"])
        self.assertIn("sparse_required_path", self.codes(result))
        self.assertIn("required_reference_missing", self.codes(result))

    def test_present_tracked_project_excluded_by_rules_is_blocked_before_recheckout(self):
        self.write("Main/Main.csproj", "<Project />")
        self.write("New/New.csproj", "<Project />")
        self.commit()
        self.sparse(["/Main/"])
        self.write("New/New.csproj", "<Project />")
        self.git("update-index", "--no-skip-worktree", "--", "New/New.csproj")
        self.assertEqual("H New/New.csproj", self.git("ls-files", "-v", "--", "New/New.csproj"))
        before = self.snapshot()
        result = source_preflight.inspect(self.root)
        self.assertEqual("blocked", result["status"])
        self.assertIn("sparse_required_path", self.codes(result))
        self.assertTrue(result["coverage"]["sparse_rules_evaluated"])
        self.assertEqual(1, result["coverage"]["sparse_rule_excluded_entries"])
        self.assertEqual(before, self.snapshot())

    def test_user_default_resources_are_required(self):
        self.write("Main/Main.csproj", "<Project />")
        self.write("Main/User/I18n/en.json", "{}")
        self.commit()
        self.sparse(["/*", "!/Main/User/"])
        result = source_preflight.inspect(self.root)
        self.assertEqual("blocked", result["status"])
        self.assertTrue(any(item.get("path") == "Main/User/I18n/en.json" for item in result["issues"]))

    def test_untracked_required_project_reference_missing(self):
        self.write("Main.csproj", '<Project><ItemGroup><ProjectReference Include="Absent.csproj" Condition="Exists(\'Absent.csproj\')" /></ItemGroup></Project>')
        self.commit()
        result = source_preflight.inspect(self.root)
        self.assertIn("required_reference_missing", self.codes(result))

    def test_archives_exempt_but_current_policy_required(self):
        self.write("Main.csproj", "<Project />")
        self.write("_workflow/old/evidence.txt")
        self.write("_batch18/history.txt")
        self.write("_workflow/storage-policy.json", "{}")
        self.write("_workflow/usable-delivery-20261003/DELIVERY-FIRST-POLICY.md")
        self.commit()
        self.sparse(["/*", "!/_workflow/", "!/_batch18/",
                     "/_workflow/storage-policy.json",
                     "/_workflow/usable-delivery-20261003/DELIVERY-FIRST-POLICY.md"])
        self.assertEqual("complete", source_preflight.inspect(self.root)["status"])
        self.sparse(["/*", "!/_workflow/", "!/_batch18/"])
        result = source_preflight.inspect(self.root)
        self.assertEqual("blocked", result["status"])
        self.assertEqual(2, result["coverage"]["archive_entries_exempted"])

    def test_old_branch_baseline_reports_without_mutations(self):
        self.write("Main.csproj", "<Project />")
        old = self.commit("old")
        self.write("new.txt")
        latest = self.commit("new change")
        self.git("checkout", "-b", "old-version", old)
        before = self.snapshot()
        result = source_preflight.inspect(self.root, latest)
        self.assertEqual("blocked", result["status"])
        self.assertEqual("not_included", result["baseline"]["status"])
        self.assertEqual(1, result["baseline"]["missing_commit_count"])
        historical = source_preflight.inspect(self.root, latest, allow_historical=True)
        self.assertEqual("complete", historical["status"])
        self.assertEqual(before, self.snapshot())
        (self.root / "Main.csproj").unlink()
        self.assertEqual("blocked", source_preflight.inspect(self.root, latest, allow_historical=True)["status"])

    def test_unknown_baseline_is_unconfirmed_even_for_historical(self):
        self.write("Main.csproj", "<Project />")
        self.commit()
        before = self.snapshot()
        result = source_preflight.inspect(self.root, "0" * 40, allow_historical=True)
        self.assertEqual("unconfirmed", result["status"])
        self.assertIn("baseline_object_missing", self.codes(result))
        self.assertEqual(before, self.snapshot())

    def test_wrong_repository_baseline_file_is_unconfirmed(self):
        self.write("Main.csproj", "<Project />")
        latest = self.commit()
        baseline_file = self.root / "baseline.json"
        baseline_file.write_text(json.dumps({"repo_common_dir": str(self.root / "other.git"), "baseline_commit": latest}), encoding="utf-8")
        before = self.snapshot()
        result = source_preflight.inspect(self.root, baseline_file=baseline_file)
        self.assertEqual("unconfirmed", result["status"])
        self.assertIn("baseline_repository_mismatch", self.codes(result))
        self.assertEqual(before, self.snapshot())

    def test_matching_baseline_file_and_user_changes(self):
        self.write("Main.csproj", "<Project />")
        latest = self.commit()
        baseline_file = self.root / "baseline.json"
        baseline_file.write_text(json.dumps({"repo_common_dir": str(self.root / ".git"), "baseline_commit": latest}), encoding="utf-8")
        self.write("notes.txt", "uncommitted user work")
        result = source_preflight.inspect(self.root, baseline_file=baseline_file)
        self.assertEqual("complete", result["status"])
        self.assertGreater(result["worktree_changes"]["count"], 0)
        self.assertEqual("uncommitted user work", (self.root / "notes.txt").read_text(encoding="utf-8"))

    def test_literal_import_source_and_resource_missing_but_dynamic_not_evaluated(self):
        self.write("Main.csproj", '<Project><Import Project="Required.props" /><ItemGroup><Compile Include="Missing.cs" /><Resource Include="Missing.ico" /><Content Include="Missing.txt" /><Compile Include="$(Generated)/*.cs" /><None Update="Optional.json" /></ItemGroup><PropertyGroup><ApplicationManifest>app.manifest</ApplicationManifest></PropertyGroup></Project>')
        self.commit()
        result = source_preflight.inspect(self.root)
        self.assertEqual("blocked", result["status"])
        self.assertEqual(5, result["coverage"]["literal_references_checked"])
        self.assertEqual(1, result["coverage"]["dynamic_references_not_evaluated"])
        self.assertEqual(5, sum(item["code"] == "required_reference_missing" for item in result["issues"]))

    def test_uninitialized_gitlink_is_reported_as_submodule(self):
        self.write("Main.csproj", "<Project />")
        latest = self.commit()
        self.git("update-index", "--add", "--cacheinfo", f"160000,{latest},Nested")
        result = source_preflight.inspect(self.root)
        self.assertIn("submodule_not_initialized", self.codes(result))
        self.assertFalse(any(item.get("path") == "Nested" and item["code"] == "tracked_file_missing" for item in result["issues"]))

    def test_tracked_wpf_temporary_project_is_generated_not_a_build_input(self):
        self.write("Main.csproj", "<Project />")
        self.write("Main_old_wpftmp.csproj", '<Project><ItemGroup><Compile Include="missing/generated.cs" /></ItemGroup></Project>')
        self.write("obj/Generated.props", '<Project><Import Project="missing.props" /></Project>')
        self.commit()
        self.sparse(["/*", "!/obj/"])
        result = source_preflight.inspect(self.root)
        self.assertEqual("complete", result["status"])
        self.assertEqual(2, result["coverage"]["generated_entries_exempted"])
        self.assertEqual(1, result["coverage"]["project_files_checked"])
        self.assertEqual(0, result["coverage"]["literal_references_checked"])

    def test_existing_dependency_must_be_in_git_index_to_survive_checkout(self):
        self.write("Main.csproj", '<Project><ItemGroup><ProjectReference Include="Dependency/dep.csproj" /></ItemGroup></Project>')
        self.commit()
        self.write("Dependency/dep.csproj", "<Project />")
        before = (self.root / "Dependency/dep.csproj").read_bytes()
        result = source_preflight.inspect(self.root)
        self.assertEqual("blocked", result["status"])
        self.assertIn("required_reference_untracked", self.codes(result))
        self.assertEqual(before, (self.root / "Dependency/dep.csproj").read_bytes())
        self.git("add", "Dependency/dep.csproj")
        self.assertEqual("complete", source_preflight.inspect(self.root)["status"])

    def test_cli_exit_codes_distinguish_complete_blocked_and_unknown(self):
        self.write("Main.csproj", "<Project />")
        self.commit()
        output = io.StringIO()
        with redirect_stdout(output):
            self.assertEqual(0, source_preflight.main(["--root", str(self.root), "--json"]))
        self.assertEqual("complete", json.loads(output.getvalue())["status"])
        with redirect_stdout(io.StringIO()):
            self.assertEqual(1, source_preflight.main(["--root", str(self.root), "--baseline", "0" * 40]))
        (self.root / "Main.csproj").unlink()
        with redirect_stdout(io.StringIO()):
            self.assertEqual(2, source_preflight.main(["--root", str(self.root)]))


if __name__ == "__main__":
    unittest.main()
