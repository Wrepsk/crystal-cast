#!/usr/bin/env python3
"""Assemble the tested programs and fixture; never builds or bundles a browser engine."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parent
NATIVE_SHA = "701f244260eb4dd06efae46d12d46eac546a5eb30a643140214f57fa6fff0c76"
DXVK_SHA = "d85ce7c79f57ecd765aaa1b9e7007cb875e6fde9f6d331df799bce73d513ce87"
LICENSE_SHA = "a5cb1a6ded7d2d7e92d550ba28edd21be2d1d4044662b399887351023e30ce64"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native-archive", type=Path, required=True)
    parser.add_argument("--dxvk-archive", type=Path, required=True)
    parser.add_argument("--dxvk-license", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/release")
    args = parser.parse_args()
    for path, expected in ((args.native_archive, NATIVE_SHA), (args.dxvk_archive, DXVK_SHA), (args.dxvk_license, LICENSE_SHA)):
        if digest(path) != expected:
            raise SystemExit("An input differs from the reviewed native benchmark / DXVK 2.7.1 inputs")
    consumer = ROOT / "build/wine-frame-consumer.exe"
    if not consumer.is_file():
        raise SystemExit("Build the x64 Windows consumer with make first")
    name = "CrystalCast-WineDXVK-FrameTest-0.1.0-linux-x64-glibc239"
    args.output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as temporary:
        staging = Path(temporary) / name
        (staging / "native").mkdir(parents=True)
        (staging / "build").mkdir()
        (staging / "dxvk/x64").mkdir(parents=True)
        with tarfile.open(args.native_archive) as archive:
            for member in archive.getmembers():
                if not member.isfile():
                    continue
                relative = Path(*Path(member.name).parts[1:])
                if not relative.parts or relative.is_absolute() or ".." in relative.parts:
                    raise SystemExit("Unsafe native archive path")
                target = staging / "native" / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(archive.extractfile(member).read())
        with tarfile.open(args.dxvk_archive) as archive:
            for dll in ("d3d11.dll", "dxgi.dll"):
                (staging / "dxvk/x64" / dll).write_bytes(archive.extractfile("dxvk-2.7.1/x64/" + dll).read())
        shutil.copy2(args.dxvk_license, staging / "dxvk/LICENSE.txt")
        shutil.copy2(consumer, staging / "build/wine-frame-consumer.exe")
        for file in ("run.py", "launch.py", "run-wine-dxvk.sh", "READ-ME.txt", "README.md", "MEASUREMENTS.md",
                     "consumer.c", "Makefile", "test_harness.py", "test_launch.py", "build_release.py"):
            shutil.copy2(ROOT / file, staging / file)
        shutil.copy2(ROOT.parent.parent / "LICENSE.md", staging / "LICENSE.md")
        for path in staging.rglob("*"):
            if path.is_file():
                os.chmod(path, 0o755 if path.name in ("run-wine-dxvk.sh", "run-benchmark.sh", "gtk-frame-probe") else 0o644)
        commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
        dirty = bool(subprocess.check_output(["git", "status", "--porcelain", "--", str(ROOT)], cwd=ROOT, text=True).strip())
        manifest = {"version": "0.1.0", "build_commit": commit, "source_tree_dirty": dirty,
                    "native_benchmark_archive_sha256": NATIVE_SHA, "dxvk_upstream_archive_sha256": DXVK_SHA,
                    "dxvk_source": "https://github.com/doitsujin/dxvk/releases/tag/v2.7.1",
                    "native_build": "GitHub Actions native benchmark 0.1.0; unchanged binary/fixture",
                    "consumer_build": "MinGW x64 C, Ubuntu 24.04 developer container; not a GitHub Actions build",
                    "sha256": {p.relative_to(staging).as_posix(): digest(p) for p in staging.rglob("*") if p.is_file()}}
        (staging / "package.json").write_text(json.dumps(manifest, indent=2) + "\n")
        tarball = args.output / (name + ".tar.gz")
        with tarfile.open(tarball, "w:gz") as output:
            for path in sorted(staging.rglob("*")):
                info = output.gettarinfo(str(path), str(Path(name) / path.relative_to(staging)))
                info.uid = info.gid = 0
                info.uname = info.gname = ""
                if path.is_file():
                    with path.open("rb") as source:
                        output.addfile(info, source)
                else:
                    output.addfile(info)
        zipped = args.output / (name + ".zip")
        with zipfile.ZipFile(zipped, "w", zipfile.ZIP_DEFLATED) as output:
            for path in sorted(staging.rglob("*")):
                if path.is_file():
                    output.write(path, str(Path(name) / path.relative_to(staging)))
        (args.output / "SHA256SUMS.txt").write_text("".join(digest(p) + "  " + p.name + "\n" for p in (tarball, zipped)))
        print(json.dumps({"files": [p.name for p in (tarball, zipped)], "tar_bytes": tarball.stat().st_size, "commit": commit, "source_tree_dirty": dirty}))


if __name__ == "__main__":
    main()
