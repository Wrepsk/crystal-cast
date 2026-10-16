#!/usr/bin/env python3
"""Collect exact distribution source packages for every bundled runtime owner.

Publication must include these archives, the WPE tarball, our source/patches and
build instructions. A list of upstream links alone is not a source release.
Run on the build host with matching deb-src indices, never on an end-user host.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import re
import tarfile

parser = argparse.ArgumentParser()
parser.add_argument("--runtime", required=True)
parser.add_argument("--work", default="/var/tmp/crystalcast-runtime-build")
parser.add_argument("--output", required=True)
parser.add_argument("--check-only", action="store_true")
parser.add_argument("--bundle-output")
args = parser.parse_args()
root, output = Path(args.runtime), Path(args.output)
output.mkdir(parents=True, exist_ok=True)
inventory = json.loads((root / "component-inventory.json").read_text())
components = sorted({(item["source"], item["sourceVersion"]) for item in inventory})
if any(not name or not version for name, version in components):
    raise RuntimeError("A bundled component has no exact corresponding source version")
indices = subprocess.run(["apt-cache", "showsrc", *sorted({name for name, _ in components})], text=True, capture_output=True, check=True).stdout
available = set()
for record in indices.split("\n\n"):
    name, version = re.search(r"^Package: (.+)$", record, re.M), re.search(r"^Version: (.+)$", record, re.M)
    if name and version: available.add((name[1], version[1]))
missing = [item for item in inventory if (item["source"], item["sourceVersion"]) not in available]
if missing:
    raise RuntimeError("Exact sources are absent from the enabled build-host indices. Refresh these build dependencies and rebuild the package before publication: " + json.dumps(missing))
if args.check_only:
    print(f"Exact sources are indexed for all {len(components)} components")
    raise SystemExit(0)
# One acquisition transaction reuses connections and avoids a fresh DNS/HTTP
# setup for every component. apt verifies each source archive against its index.
subprocess.run(["apt-get", "source", "--download-only", *[f"{name}={version}" for name, version in components]], cwd=output, check=True)
work = Path(args.work)
shutil.copy2(work / "wpewebkit-2.54.1.tar.xz", output)
shutil.copy2(work / "runtime-bootstrap.o", output)
(output / "STATIC-BOOTSTRAP-RELINK.txt").write_text("""The native bootstrap statically links the build host's glibc and GCC support.
Their exact corresponding sources and license notices accompany this release.
runtime-bootstrap.o is the application's relinkable object. Its complete source
is runtime_launcher.c in runtime-packaging-recipes.tar.gz. Rebuild with:
  cc -c -Os -Wall -Wextra -Werror runtime_launcher.c -o runtime-bootstrap.o
  cc -static -s runtime-bootstrap.o -o crystalcast-browser
Use your rebuilt compatible C runtime/startup libraries when relinking, rather
than replacing the user's system libc. The browser/codec shared libraries remain
separate. component-inventory.json/source-index.json record exact source versions.
""")
repo = Path(__file__).resolve().parents[2]
subprocess.run(["git", "-c", f"safe.directory={repo}", "archive", "--format=tar.gz", "HEAD", "-o", str(output / "crystalcast-source.tar.gz")], cwd=repo, check=True)
# Include the current build/relocation recipes even when testing uncommitted work.
shutil.make_archive(str(output / "runtime-packaging-recipes"), "gztar", Path(__file__).parent)
helper_sources = output / "helper-source"
helper_sources.mkdir(exist_ok=True)
for name in ("helper.c", "bridge.c", "bridge.h", "Makefile", "README.md"):
    shutil.copy2(repo / "NativeLinuxBrowser" / name, helper_sources)
shutil.make_archive(str(output / "native-helper-source"), "gztar", helper_sources)
shutil.rmtree(helper_sources)
records = []
for file in sorted(output.iterdir()):
    if file.is_file() and file.name != "source-index.json":
        with file.open("rb") as stream:
            records.append(dict(file=file.name, bytes=file.stat().st_size, sha256=hashlib.file_digest(stream, "sha256").hexdigest()))
(output / "source-index.json").write_text(json.dumps(dict(components=inventory, archives=records), indent=2) + "\n")
if args.bundle_output:
    bundles = Path(args.bundle_output)
    bundles.mkdir(parents=True, exist_ok=True)
    # The binary packager names its staging directory runtime-<version>.
    version = root.name.removeprefix("runtime-")
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version): raise RuntimeError("Invalid runtime staging version")
    groups, current, size = [], [], 0
    for file in sorted(output.iterdir()):
        if not file.is_file(): continue
        count = file.stat().st_size + 2048
        if count > 1800 * 1024**2: raise RuntimeError("A source archive exceeds the release bundle limit")
        if current and size + count > 1800 * 1024**2:
            groups.append(current); current, size = [], 0
        current.append(file); size += count
    if current: groups.append(current)
    bundle_records = []
    for index, group in enumerate(groups, 1):
        archive = bundles / f"CrystalCast-LinuxBrowser-{version}-sources-{index}.tar"
        # Most inputs are already xz/gz compressed; avoid recompressing them.
        with tarfile.open(archive, "w") as tar:
            for file in group: tar.add(file, arcname=file.name, recursive=False)
        with archive.open("rb") as stream:
            bundle_records.append(dict(file=archive.name, bytes=archive.stat().st_size, sha256=hashlib.file_digest(stream, "sha256").hexdigest()))
    shutil.copy2(output / "source-index.json", bundles)
    (bundles / "source-bundles.json").write_text(json.dumps(dict(version=version, bundles=bundle_records), indent=2) + "\n")
print(f"Collected corresponding source for {len(components)} distribution components and WPE")
