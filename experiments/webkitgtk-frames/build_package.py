#!/usr/bin/env python3
"""Small distro-linked benchmark artifact, not a portable browser runtime."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import zipfile
import benchmark

ROOT = Path(__file__).resolve().parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", default="0.1.0")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/package")
    parser.add_argument("--fixture", type=Path, help="Existing exact fixture (for local rebuilds)")
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", args.version):
        parser.error("Version must be numeric major.minor.patch")
    subprocess.run(["make"], cwd=ROOT, check=True)
    if args.fixture:
        video = args.fixture.resolve(strict=True)
    else:
        generated = ROOT / "artifacts/package-fixture"
        generated.mkdir(parents=True, exist_ok=True)
        video = benchmark.fixture(generated, seconds=20)
    metadata = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=codec_name,width,height,r_frame_rate,nb_frames:format=duration", "-of", "json", str(video)]))
    stream = metadata["streams"][0]
    if (stream["codec_name"], stream["width"], stream["height"], stream["r_frame_rate"]) != ("h264", 1920, 1080, "60/1") or float(metadata["format"]["duration"]) < 20:
        raise SystemExit("Fixture must be the encoded-counter 1920x1080 60fps H.264 test, at least 20 seconds")
    name = f"CrystalCast-WebKitGTK-Benchmark-{args.version}-linux-x64-glibc239"
    destination = args.output / name
    destination.mkdir(parents=True, exist_ok=False)
    for folder in ("bin", "data", "source"):
        (destination / folder).mkdir()
    shutil.copy2(ROOT / "build/gtk-frame-probe", destination / "bin/gtk-frame-probe")
    subprocess.run(["strip", str(destination / "bin/gtk-frame-probe")], check=True)
    for file in ("run-benchmark.sh", "run_benchmark.py", "benchmark.py", "READ-ME.txt"):
        shutil.copy2(ROOT / file, destination / file)
    os.chmod(destination / "run-benchmark.sh", 0o755)
    os.chmod(destination / "bin/gtk-frame-probe", 0o755)
    shutil.copy2(video, destination / "data/video.mp4")
    for file in ("probe.c", "Makefile", "build_package.py", "benchmark.py", "run_benchmark.py", "run-benchmark.sh", "READ-ME.txt"):
        shutil.copy2(ROOT / file, destination / "source" / file)
    shutil.copy2(ROOT.parent.parent / "LICENSE.md", destination / "LICENSE.md")
    for path in destination.rglob('*'):
        if path.is_file():
            os.chmod(path, 0o755 if path.name in ("run-benchmark.sh", "gtk-frame-probe") else 0o644)
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    dirty = bool(subprocess.check_output(["git", "status", "--porcelain", "--", str(ROOT)], cwd=ROOT, text=True).strip())
    # Verify only direct application SONAME dependencies; never copy their closure.
    needed = subprocess.check_output(["readelf", "-d", str(destination / "bin/gtk-frame-probe")], text=True)
    symbols = subprocess.check_output(["readelf", "--version-info", str(destination / "bin/gtk-frame-probe")], text=True)
    glibc = sorted({tuple(map(int, s.split('.'))) for s in re.findall(r"GLIBC_([0-9.]+)", symbols)})
    if glibc and glibc[-1] > (2, 39):
        raise SystemExit("Build is newer than the Ubuntu 24.04 glibc 2.39 target")
    manifest = {"version": args.version, "build_commit": commit, "source_tree_dirty": dirty,
                "target": "x86_64 Linux; glibc >=2.39; GTK >=4.14; WebKitGTK API 6.0; Python >=3.10",
                "direct_dependencies": re.findall(r"Shared library: \[([^]]+)\]", needed),
                "max_glibc_symbol_version": '.'.join(map(str, glibc[-1])) if glibc else None,
                "fixture": {"codec": "H.264", "width": 1920, "height": 1080, "fps": 60,
                            "duration_seconds": float(metadata["format"]["duration"]),
                            "encoded_counter_bits": 10, "audio": False, "generator": "benchmark.fixture, testsrc2, CRF23, veryfast"},
                "sha256": {p.relative_to(destination).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
                           for p in sorted(destination.rglob('*')) if p.is_file()}}
    (destination / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    archive = args.output / (name + ".tar.gz")
    with tarfile.open(archive, "w:gz") as output:
        for path in sorted(destination.rglob('*')):
            info = output.gettarinfo(str(path), str(Path(name) / path.relative_to(destination)))
            info.uid = info.gid = 0
            info.uname = info.gname = ""
            if path.is_file():
                with path.open('rb') as source:
                    output.addfile(info, source)
            else:
                output.addfile(info)
    zipped = args.output / (name + ".zip")
    with zipfile.ZipFile(zipped, "w", zipfile.ZIP_DEFLATED) as output:
        for path in sorted(destination.rglob('*')):
            if path.is_file():
                output.write(path, str(Path(name) / path.relative_to(destination)))
    checksums = "".join(f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.name}\n" for p in (archive, zipped))
    (args.output / "SHA256SUMS.txt").write_text(checksums)
    print(json.dumps({"tar_gz": archive.name, "zip": zipped.name, "tar_bytes": archive.stat().st_size,
                      "executable_bytes": (destination / 'bin/gtk-frame-probe').stat().st_size}))


if __name__ == "__main__":
    main()
