"""Exercise production User defaults targets with isolated, real MSBuild fixtures."""

import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


PRODUCTION_TARGETS = (Path(__file__).resolve().parents[2]
                      / "BetterGenshinImpact" / "UserDefaults.targets")


class UserDefaultsTests(unittest.TestCase):
    def setUp(self):
        parent = os.environ.get("MISTLETOE_PREFLIGHT_TEST_ROOT")
        if not parent:
            self.fail("MISTLETOE_PREFLIGHT_TEST_ROOT must identify the isolated test area")
        fixture_parent = Path(parent).resolve()
        fixture_parent.mkdir(parents=True, exist_ok=True)
        # Keep managed fixtures as evidence; IDE file watchers can hold directories open on Windows.
        self.root = Path(tempfile.mkdtemp(prefix="user-defaults-", dir=fixture_parent)).resolve()
        self.assertTrue(self.root.is_relative_to(fixture_parent))
        self.assertNotEqual(self.root, fixture_parent)

        shutil.copyfile(PRODUCTION_TARGETS, self.root / "UserDefaults.targets")
        self.write("User/I18n/en.json", '{"source": "default"}')
        self.write("User/JsScript/default/main.js", "// default script\n")
        self.write("Resources/linked.txt", "explicit linked resource")
        self.write("Fixture.proj", '''<Project>
  <ItemGroup>
    <None Include="User\\**\\*" CopyToOutputDirectory="Always" CopyToPublishDirectory="Always" />
    <None Include="Resources\\linked.txt" Link="User\\Linked\\linked.txt"
          CopyToOutputDirectory="Always" CopyToPublishDirectory="Always" />
  </ItemGroup>
  <Target Name="Build">
    <Error Condition="'%(None.Identity)' != 'Resources\\linked.txt' And
                      ('%(None.CopyToOutputDirectory)' != 'Never' Or '%(None.CopyToPublishDirectory)' != 'Never')"
           Text="A source User default still has automatic copy metadata." />
    <Copy SourceFiles="%(None.Identity)" DestinationFiles="$(TargetDir)%(None.Link)"
          Condition="'$(TargetDir)' != '' And '%(None.CopyToOutputDirectory)' == 'Always'" />
  </Target>
  <Target Name="Publish" DependsOnTargets="Build">
    <Copy SourceFiles="%(None.Identity)" DestinationFiles="$(PublishDir)%(None.Link)"
          Condition="'$(PublishDir)' != '' And '%(None.CopyToPublishDirectory)' == 'Always'" />
  </Target>
  <Import Project="UserDefaults.targets" />
</Project>
''')
        self.output = self.root / "output"
        self.publish = self.root / "publish"

    def write(self, name, content):
        target = self.root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8")
        return target

    def msbuild(self, target="Build", *, empty_output=False, empty_publish=False):
        output = "" if empty_output else str(self.output) + os.sep
        publish = "" if empty_publish else str(self.publish) + os.sep
        process = subprocess.run(
            ["dotnet", "msbuild", str(self.root / "Fixture.proj"), "-nologo", "-v:minimal",
             "-t:" + target, "-p:TargetDir=" + output, "-p:PublishDir=" + publish],
            cwd=self.root, capture_output=True, text=True, encoding="utf-8", errors="replace",
            timeout=90,
        )
        return process

    def assert_succeeded(self, process):
        self.assertEqual(0, process.returncode, process.stdout + process.stderr)

    def snapshot(self, path):
        return path.read_bytes(), path.stat().st_mtime_ns

    def user_file(self, directory, content="user custom translation"):
        path = directory / "User" / "I18n" / "en.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        timestamp = 1_600_000_000_000_000_000
        os.utime(path, ns=(timestamp, timestamp))
        return path

    def assert_defaults(self, directory):
        self.assertEqual('{"source": "default"}', (directory / "User/I18n/en.json").read_text(encoding="utf-8"))
        self.assertEqual("// default script\n", (directory / "User/JsScript/default/main.js").read_text(encoding="utf-8"))
        self.assertEqual("explicit linked resource", (directory / "User/Linked/linked.txt").read_text(encoding="utf-8"))

    def test_fresh_build_seeds_defaults_and_keeps_explicit_links(self):
        self.assert_succeeded(self.msbuild())
        self.assert_defaults(self.output)

    def test_build_preserves_existing_user_file_and_timestamp_across_source_updates(self):
        existing = self.user_file(self.output)
        before = self.snapshot(existing)
        self.assert_succeeded(self.msbuild())
        self.assertEqual(before, self.snapshot(existing))
        self.assertTrue((self.output / "User/JsScript/default/main.js").is_file())
        self.write("User/I18n/en.json", '{"source": "new default"}')
        self.write("User/JsScript/default/main.js", "// updated default script\n")
        script = self.output / "User/JsScript/default/main.js"
        script_before = self.snapshot(script)
        self.assert_succeeded(self.msbuild())
        self.assertEqual(before, self.snapshot(existing))
        self.assertEqual(script_before, self.snapshot(script))

    def test_fresh_publish_seeds_defaults_and_keeps_explicit_links(self):
        self.assert_succeeded(self.msbuild("Publish"))
        self.assert_defaults(self.publish)

    def test_publish_preserves_existing_user_file_and_timestamp_across_source_updates(self):
        existing = self.user_file(self.publish)
        before = self.snapshot(existing)
        self.assert_succeeded(self.msbuild("Publish"))
        self.assertEqual(before, self.snapshot(existing))
        self.assertTrue((self.publish / "User/JsScript/default/main.js").is_file())
        self.write("User/I18n/en.json", '{"source": "new default"}')
        script = self.publish / "User/JsScript/default/main.js"
        script_before = self.snapshot(script)
        self.assert_succeeded(self.msbuild("Publish"))
        self.assertEqual(before, self.snapshot(existing))
        self.assertEqual(script_before, self.snapshot(script))

    def test_empty_build_output_is_rejected_before_copying_defaults(self):
        original = self.snapshot(self.root / "User/I18n/en.json")
        process = self.msbuild(empty_output=True)
        self.assertNotEqual(0, process.returncode)
        self.assertIn("TargetDir", process.stdout + process.stderr)
        self.assertEqual(original, self.snapshot(self.root / "User/I18n/en.json"))
        self.assertFalse((self.root / "User/Linked").exists())

    def test_empty_publish_output_is_rejected_before_copying_defaults(self):
        original = self.snapshot(self.root / "User/I18n/en.json")
        process = self.msbuild("Publish", empty_publish=True)
        self.assertNotEqual(0, process.returncode)
        self.assertIn("PublishDir", process.stdout + process.stderr)
        self.assertEqual(original, self.snapshot(self.root / "User/I18n/en.json"))
        self.assertFalse((self.root / "User/Linked").exists())


if __name__ == "__main__":
    unittest.main()
