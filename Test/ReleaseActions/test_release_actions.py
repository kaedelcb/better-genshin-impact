import copy
import hashlib
import io
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import release_actions as release


class FakeGitHub:
    def __init__(self, version="0.66.3+lcb.23.0-NexusBGI-offline-test"):
        self.version = version
        self.repo = {"id": 961, "default_branch": "main"}
        self.workflow = {"id": 161}
        self.run = {"id": 123, "repository": {"id": 961}, "head_repository": {"id": 961},
                    "workflow_id": 161, "path": release.WORKFLOW, "event": "workflow_dispatch",
                    "status": "in_progress", "conclusion": None, "head_branch": "main-OldTeaBag-B185",
                    "head_sha": "a" * 40, "display_title": "BetterGI release:" + version, "run_attempt": 1}
        self.archives = {}
        self.artifacts = []
        self.releases = []
        self.tag = None
        self.writes = []
        self.uploads = []
        self.downloads = []
        self.bad_remote_digest = False
        self.upload_without_asset = False
        self.upload_transport_error = False
        self.change_tag_after_upload = False
        self.tag_race = False
        self.create_failure = False
        self.patch_failure = False
        self.change_attempt_during_download = False
        for number, (kind, (template, magic)) in enumerate(release.KINDS.items(), 1):
            self.set_zip(number, kind, [(template.format(version=version), magic + b"offline-fixture" * 8)])

    def set_zip(self, number, kind, entries, *, symlink=False, compressed=False):
        data = io.BytesIO()
        with zipfile.ZipFile(data, "w", compression=zipfile.ZIP_DEFLATED if compressed else zipfile.ZIP_STORED) as bundle:
            for name, content in entries:
                info = zipfile.ZipInfo(name)
                if symlink:
                    info.external_attr = 0o120777 << 16
                bundle.writestr(info, content)
        raw = data.getvalue()
        self.archives[number] = raw
        artifact = {"id": number, "name": kind, "size_in_bytes": len(raw),
                    "digest": "sha256:" + hashlib.sha256(raw).hexdigest(), "expired": False,
                    "workflow_run": {"id": 123, "head_sha": self.run["head_sha"]}}
        self.artifacts = [a for a in self.artifacts if a["id"] != number] + [artifact]

    def get(self, path):
        base = "repos/" + release.REPOSITORY
        if path == base:
            return copy.deepcopy(self.repo)
        if path == base + "/actions/workflows/publish.yml":
            return copy.deepcopy(self.workflow)
        if path == base + "/actions/runs/123":
            return copy.deepcopy(self.run)
        if "/git/ref/tags/" in path:
            if self.tag is None:
                raise release.NotFound("absent")
            return {"object": {"type": "commit", "sha": self.tag}}
        if path == base + "/releases/50":
            return copy.deepcopy(self.releases[0])
        raise AssertionError("Unexpected read " + path)

    def pages(self, path, key=None):
        if path.endswith("/artifacts"):
            return copy.deepcopy(self.artifacts)
        if path.endswith("/releases"):
            return copy.deepcopy(self.releases)
        raise AssertionError("Unexpected page " + path)

    def download(self, artifact, target):
        self.downloads.append(artifact["id"])
        target.write_bytes(self.archives[artifact["id"]])
        if self.change_attempt_during_download:
            self.run["run_attempt"] = 2

    def write(self, method, path, payload):
        self.writes.append((method, path, copy.deepcopy(payload)))
        if path.endswith("/git/refs"):
            if self.tag_race:
                raise release.ReleaseError("atomic tag reservation conflict")
            assert self.tag is None
            self.tag = payload["sha"]
            return {"object": {"sha": self.tag}}
        if method == "POST" and path.endswith("/releases"):
            self.releases = [dict(payload, id=50, assets=[], html_url="https://example.invalid/offline-release")]
            if self.create_failure:
                raise release.ReleaseError("unknown create response")
            return copy.deepcopy(self.releases[0])
        if method == "PATCH":
            if self.patch_failure:
                raise release.ReleaseError("unknown publication response")
            self.releases[0].update(payload)
            return copy.deepcopy(self.releases[0])
        raise AssertionError("Unexpected mutation " + path)

    def upload(self, tag, path):
        self.uploads.append(path.name)
        if self.upload_without_asset:
            return 1
        digest = "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()
        if self.bad_remote_digest:
            digest = "sha256:" + "0" * 64
        self.releases[0]["assets"].append({"name": path.name, "state": "uploaded", "size": path.stat().st_size, "digest": digest})
        if self.change_tag_after_upload:
            self.tag = "b" * 40
        return 1 if self.upload_transport_error else 0


class SourceTests(unittest.TestCase):
    def setUp(self):
        self.api = FakeGitHub()

    def reject_change(self, container, key, value):
        container[key] = value
        with self.assertRaises(release.ReleaseError):
            release.inspect_build(self.api, 123)
        self.assertEqual([], self.api.writes)

    def test_valid_current_build_and_both_artifacts(self):
        build = release.inspect_build(self.api, "123")
        self.assertEqual((123, 1, "a" * 40, self.api.version), (build.run_id, build.attempt, build.sha, build.version))
        self.assertEqual(2, len(build.artifacts))

    def test_manual_input_cannot_inject_shell_or_url(self):
        for value in ("", "0", "-1", "1/../2", "123?x=y", "1; echo injected", "1\n2", "1" * 30, True):
            with self.subTest(value=value), self.assertRaises(release.ReleaseError):
                release.run_id(value)

    def test_fork_rejected(self):
        self.reject_change(self.api.run, "head_repository", {"id": 999})

    def test_missing_head_repository_rejected(self):
        self.reject_change(self.api.run, "head_repository", None)

    def test_wrong_repository_rejected(self):
        self.reject_change(self.api.run, "repository", {"id": 999})

    def test_wrong_workflow_id_rejected(self):
        self.reject_change(self.api.run, "workflow_id", 999)

    def test_wrong_workflow_path_rejected(self):
        self.reject_change(self.api.run, "path", ".github/workflows/repo-bot.yml")

    def test_pr_trigger_rejected(self):
        self.reject_change(self.api.run, "event", "pull_request")

    def test_completed_build_cannot_be_selected_by_this_workflow(self):
        self.reject_change(self.api.run, "status", "completed")

    def test_untrusted_branches_rejected(self):
        for branch in ("feature/test", "main-OldTeaBag-B185-evil", "main-OldTeaBag-Babc", "main"):
            with self.subTest(branch=branch):
                self.reject_change(self.api.run, "head_branch", branch)

    def test_bad_sha_rejected(self):
        self.reject_change(self.api.run, "head_sha", "main")

    def test_bad_version_rejected(self):
        for title in ("BetterGI release:1.2.3;echo x", "BetterGI release:../1.2.3", "BetterGI release:1.2", "BetterGI 1.2.3"):
            with self.subTest(title=title):
                self.reject_change(self.api.run, "display_title", title)

    def test_missing_duplicate_expired_and_unverifiable_artifacts(self):
        originals = copy.deepcopy(self.api.artifacts)
        variants = [originals[:1], originals + [originals[0]],
                    [dict(originals[0], expired=True), originals[1]],
                    [dict(originals[0], digest=None), originals[1]],
                    [dict(originals[0], workflow_run={"id": 999, "head_sha": "a" * 40}), originals[1]],
                    [dict(originals[0], workflow_run={"id": 123, "head_sha": "b" * 40}), originals[1]]]
        for artifacts in variants:
            with self.subTest(artifacts=artifacts):
                self.api.artifacts = artifacts
                with self.assertRaises(release.ReleaseError):
                    release.inspect_build(self.api, 123)


class PublishTests(unittest.TestCase):
    def setUp(self):
        self.api = FakeGitHub()
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.path = Path(self.folder.name)

    def publish(self):
        return release.publish(self.api, release.inspect_build(self.api, 123), self.path)

    def assert_private(self):
        self.assertFalse(any(m == "PATCH" for m, _, _ in self.api.writes))
        if self.api.releases:
            self.assertTrue(self.api.releases[0]["draft"])

    def test_two_verified_original_files_published_after_uploads(self):
        status, result = self.publish()
        self.assertEqual("published", status)
        self.assertFalse(result["draft"])
        self.assertFalse(result["prerelease"])
        self.assertEqual("a" * 40, self.api.tag)
        self.assertEqual(2, len(self.api.uploads))
        self.assertEqual("legacy", self.api.writes[-1][2]["make_latest"])
        self.assertIn("/actions/runs/123", result["body"])
        self.assertIn("a" * 40, result["body"])

    def test_stable_release_uses_github_latest_version_order(self):
        self.api = FakeGitHub("1.2.3")
        _, result = self.publish()
        self.assertFalse(result["prerelease"])
        self.assertEqual("legacy", self.api.writes[-1][2]["make_latest"])

    def test_alpha_with_teabag_metadata_never_becomes_latest(self):
        self.api = FakeGitHub("0.66.3-alpha.1+lcb.23.0-NexusBGI-fix8")
        _, result = self.publish()
        self.assertTrue(result["prerelease"])
        self.assertEqual("false", self.api.writes[-1][2]["make_latest"])

    def test_published_same_version_is_idempotent_without_download(self):
        _, prior = self.publish()
        self.api.writes.clear()
        self.api.downloads.clear()
        status, found = self.publish()
        self.assertEqual(("already-published", prior["id"]), (status, found["id"]))
        self.assertEqual([], self.api.writes)
        self.assertEqual([], self.api.downloads)

    def test_existing_draft_is_preserved_without_upload(self):
        self.api.releases = [{"tag_name": self.api.version, "draft": True}]
        with self.assertRaises(release.ReleaseError):
            self.publish()
        self.assertEqual([], self.api.writes)
        self.assertEqual([], self.api.uploads)

    def test_existing_tag_is_never_moved(self):
        for sha in ("a" * 40, "b" * 40):
            self.api.tag = sha
            with self.subTest(sha=sha), self.assertRaises(release.ReleaseError):
                self.publish()
        self.assertEqual([], self.api.writes)

    def test_published_wrong_commit_is_not_overwritten(self):
        self.publish()
        self.api.tag = "b" * 40
        self.api.writes.clear()
        with self.assertRaises(release.ReleaseError):
            self.publish()
        self.assertEqual([], self.api.writes)

    def test_zip_digest_mismatch_cannot_create_tag(self):
        self.api.artifacts[0]["digest"] = "sha256:" + "0" * 64
        with self.assertRaises(release.ReleaseError):
            self.publish()
        self.assertEqual([], self.api.writes)

    def test_zip_slip_version_extra_file_symlink_and_bad_magic(self):
        template, magic = release.KINDS["BetterGI_7z"]
        name = template.format(version=self.api.version)
        cases = [([("../" + name, magic)], False), ([("wrong-version.7z", magic)], False),
                 ([(name, magic), ("extra.exe", b"MZ")], False), ([(name, magic)], True), ([(name, b"bad")], False)]
        for entries, symlink in cases:
            with self.subTest(entries=entries, symlink=symlink), tempfile.TemporaryDirectory() as folder:
                api = FakeGitHub()
                api.set_zip(1, "BetterGI_7z", entries, symlink=symlink)
                with self.assertRaises(release.ReleaseError):
                    release.publish(api, release.inspect_build(api, 123), Path(folder))
                self.assertEqual([], api.writes)

    def test_crc_corruption_is_detected_even_with_matching_outer_digest(self):
        artifact = self.api.artifacts[0]
        raw = bytearray(self.api.archives[artifact["id"]])
        offset = 30 + struct.unpack_from("<H", raw, 26)[0] + struct.unpack_from("<H", raw, 28)[0]
        raw[offset] ^= 1
        self.api.archives[artifact["id"]] = bytes(raw)
        artifact["digest"] = "sha256:" + hashlib.sha256(raw).hexdigest()
        with self.assertRaises(zipfile.BadZipFile):
            self.publish()
        self.assertEqual([], self.api.writes)

    def test_rerun_during_download_fails_before_tag_reservation(self):
        self.api.change_attempt_during_download = True
        with self.assertRaises(release.ReleaseError):
            self.publish()
        self.assertEqual([], self.api.writes)

    def test_atomic_tag_race_fails_without_draft(self):
        self.api.tag_race = True
        with self.assertRaises(release.ReleaseError):
            self.publish()
        self.assertEqual(1, len(self.api.writes))
        self.assertEqual([], self.api.releases)

    def test_unknown_create_is_not_retried_or_published(self):
        self.api.create_failure = True
        with self.assertRaises(release.ReleaseError):
            self.publish()
        self.assertEqual(2, len(self.api.writes))
        self.assert_private()

    def test_failed_upload_leaves_private_draft_and_does_not_retry(self):
        self.api.upload_without_asset = True
        with self.assertRaises(release.ReleaseError):
            self.publish()
        self.assertEqual(1, len(self.api.uploads))
        self.assert_private()

    def test_remote_hash_mismatch_leaves_draft(self):
        self.api.bad_remote_digest = True
        with self.assertRaises(release.ReleaseError):
            self.publish()
        self.assert_private()

    def test_confirmed_server_upload_is_not_repeated_after_transport_error(self):
        self.api.upload_transport_error = True
        self.publish()
        self.assertEqual(2, len(self.api.uploads))

    def test_tag_changed_before_publication_leaves_private_draft(self):
        self.api.change_tag_after_upload = True
        with self.assertRaises(release.ReleaseError):
            self.publish()
        self.assert_private()

    def test_unknown_publication_is_not_retried(self):
        self.api.patch_failure = True
        with self.assertRaises(release.ReleaseError):
            self.publish()
        self.assertEqual(1, sum(m == "PATCH" for m, _, _ in self.api.writes))
        self.assertTrue(self.api.releases[0]["draft"])


class WorkflowTests(unittest.TestCase):
    def load(self, name):
        import yaml
        return yaml.load((ROOT / ".github/workflows" / name).read_text(encoding="utf-8"), Loader=yaml.BaseLoader)

    def test_publication_uses_existing_dispatch_and_waits_for_all_packages(self):
        data = self.load("publish.yml")
        self.assertEqual({"workflow_dispatch"}, set(data["on"]))
        self.assertEqual({"version", "kachina-channel", "create-release", "upload-to-steambird"},
                         set(data["on"]["workflow_dispatch"]["inputs"]))
        publish = data["jobs"]["publish_release"]
        self.assertEqual({"validate", "build_dist", "build_installer", "build_setup"}, set(publish["needs"]))
        self.assertIn("needs.validate.outputs.publish == 'true'", publish["if"])
        self.assertIn("!inputs.create-release", publish["if"])
        self.assertNotIn("always()", publish["if"])

    def test_permissions_concurrency_and_trusted_checkout(self):
        publish = self.load("publish.yml")["jobs"]["publish_release"]
        self.assertEqual({"contents": "write", "actions": "read"}, publish["permissions"])
        self.assertIn("needs.validate.outputs.version", publish["concurrency"]["group"])
        self.assertEqual("false", publish["concurrency"]["cancel-in-progress"])
        self.assertEqual("max", publish["concurrency"]["queue"])
        checkout = publish["steps"][0]
        self.assertEqual("${{ github.sha }}", checkout["with"]["ref"])
        self.assertEqual("false", checkout["with"]["persist-credentials"])

    def test_push_validation_cannot_publish_and_has_no_write_token(self):
        data = self.load("release-artifacts-check.yml")
        self.assertEqual({"contents": "read"}, data["permissions"])
        self.assertNotIn("workflow_dispatch", data["on"])
        text = json.dumps(data)
        self.assertNotIn("python -B tools/release_actions.py", text)
        self.assertNotIn("GH_TOKEN", text)

    def test_local_real_entry_is_blocked_before_network(self):
        environment = os.environ.copy()
        environment.pop("GITHUB_ACTIONS", None)
        result = subprocess.run([sys.executable, "-B", str(ROOT / "tools/release_actions.py")],
                                env=environment, capture_output=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("GitHub Actions", result.stdout.decode("utf-8", "replace"))


if __name__ == "__main__":
    unittest.main()
