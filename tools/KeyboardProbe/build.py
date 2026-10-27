"""Build the standalone probe without building or deploying BetterGI."""
from pathlib import Path
import argparse
import os
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "mistletoe"))
from storage_limits import Session


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists():
        raise SystemExit("Output must be a new directory; existing evidence is preserved.")
    source = Path(__file__).resolve().parent
    compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    with Session(ROOT, "keyboard-probe-build-selftest-package", reserve_bytes=2 * 1024**2) as guard:
        guard.track(output)
        guard.check()
        output.mkdir(parents=True)
        exe = output / "BgiKeyboardProbe.exe"
        result = subprocess.run(
            [str(compiler), "/nologo", "/target:winexe", "/platform:x64", "/optimize+",
             "/codepage:65001", "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll",
             "/r:System.Web.Extensions.dll", "/r:System.IO.Compression.dll", "/r:System.IO.Compression.FileSystem.dll",
             "/win32manifest:" + str(source / "app.manifest"),
             "/out:" + str(exe)] + [str(p) for p in sorted(source.glob("*.cs"))],
            capture_output=True, timeout=60,
        )
        guard.write(output / "build.log", result.stdout + result.stderr)
        print("compiler exit:", result.returncode, flush=True)
        if result.returncode:
            raise RuntimeError("Compilation failed; see build.log")
        guard.write(output / "详细说明.md", (source / "README.md").read_bytes())
        guard.write(output / "使用说明.txt", b"\xef\xbb\xbf" + (source / "QuickStart.txt").read_bytes())
        result = subprocess.run([str(exe), "--self-test", str(output / "self-test.json")], timeout=60)
        print("self-test exit:", result.returncode, flush=True)
        if result.returncode:
            raise RuntimeError("Isolated self-test failed; see self-test.json")
        with zipfile.ZipFile(output / "BGI-按键诊断工具-v2.0.zip", "x", zipfile.ZIP_DEFLATED) as archive:
            archive.write(exe, exe.name)
            archive.write(output / "使用说明.txt", "使用说明.txt")
            archive.write(output / "详细说明.md", "详细说明.md")
        print("package:", output / "BGI-按键诊断工具-v2.0.zip", flush=True)


if __name__ == "__main__":
    main()
