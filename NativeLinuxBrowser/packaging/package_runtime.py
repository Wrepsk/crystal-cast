#!/usr/bin/env python3
"""Collect a relocatable Ubuntu-baseline runtime and emit hashed ZIP/manifest.

Developer tool: executes ldd only on trusted local build/distribution binaries.
No proprietary GPU driver or glibc loader is bundled. Mesa EGL/Gallium provides
a fallback when no GPU is visible. ZIP contains regular files
only, avoiding unsafe symlink extraction through a Windows/Wine consumer.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument("--work", default="/var/tmp/crystalcast-runtime-build")
parser.add_argument("--version", required=True)
parser.add_argument("--output", required=True)
args = parser.parse_args()
if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", args.version):
    parser.error("version must be numeric major.minor.patch")
work, output = Path(args.work), Path(args.output)
root = work / ("runtime-" + args.version)
if root.exists():
    raise RuntimeError("Package staging directory already exists; use a fresh build root/version")
root.mkdir(mode=0o700)
output.mkdir(parents=True, exist_ok=True)
stage = work / "stage/crystalcast-linux-browser"
repo = Path(__file__).resolve().parents[1]
shutil.copytree(stage, root, dirs_exist_ok=True, symlinks=False,
                ignore=shutil.ignore_patterns("include", "pkgconfig", "cmake", "*.a", "*.la", "*.gir", "*.typelib"))
pkgconfig = work / "helper-pkgconfig"
pkgconfig.mkdir(exist_ok=True)
for file in (stage / "lib/pkgconfig").glob("*.pc"):
    (pkgconfig / file.name).write_text(file.read_text().replace("/crystalcast-linux-browser", str(stage)))
# Only WPE is staged. A global PKG_CONFIG_SYSROOT_DIR would incorrectly redirect
# system GLib/GStreamer header paths into the WPE install tree.
environment = dict(os.environ, PKG_CONFIG_PATH=str(pkgconfig), LD_LIBRARY_PATH=str(stage / "lib"))
environment.pop("PKG_CONFIG_SYSROOT_DIR", None)
subprocess.run(["make", "-B", "-C", str(repo)], env=environment, stdout=sys.stderr, check=True)
(root / "libexec").mkdir(exist_ok=True)
shutil.copy2(repo / "build/crystalcast-browser-helper", root / "libexec/crystalcast-browser-helper")
subprocess.run(["strip", str(root / "libexec/crystalcast-browser-helper")], check=True)
(root / "bin").mkdir(exist_ok=True)
bootstrap_object = work / "runtime-bootstrap.o"
subprocess.run(["cc", "-c", "-Os", "-Wall", "-Wextra", "-Werror",
                str(repo / "packaging/runtime_launcher.c"), "-o", str(bootstrap_object)], check=True)
subprocess.run(["cc", "-static", "-s", str(bootstrap_object), "-o", str(root / "bin/crystalcast-browser")], check=True)
static_inputs = []
for name in ("libc.a", "libgcc.a", "libgcc_eh.a", "crt1.o", "crti.o", "crtn.o", "crtbeginT.o", "crtend.o"):
    path = Path(subprocess.check_output(["cc", "-print-file-name=" + name], text=True).strip())
    if not path.is_file(): raise RuntimeError(f"Cannot identify static bootstrap input: {name}")
    static_inputs.append(str(path.resolve()))

origins = {}
def copy(source, destination):
    source, destination = Path(source), root / destination
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source.resolve(), destination)
    origins[str(destination.relative_to(root))] = str(source)
    return destination

for file in Path("/usr/lib/x86_64-linux-gnu/gstreamer-1.0").glob("*.so"):
    copy(file, "lib/gstreamer-1.0/" + file.name)
for file in Path("/usr/lib/x86_64-linux-gnu/gio/modules").glob("*.so"):
    copy(file, "lib/gio/modules/" + file.name)
for source, target in [
    ("/usr/bin/bwrap", "libexec/bwrap"), ("/usr/bin/xdg-dbus-proxy", "libexec/xdg-dbus-proxy"),
    ("/usr/lib/x86_64-linux-gnu/gstreamer1.0/gstreamer-1.0/gst-plugin-scanner", "libexec/gstreamer-1.0/gst-plugin-scanner"),
]:
    copy(source, target)
# Rebuild MIME data from the distribution-owned canonical input, rather than
# copying a build host's generated cache (which can include unrelated packages).
copy("/usr/share/mime/packages/freedesktop.org.xml", "share/mime/packages/freedesktop.org.xml")
subprocess.run(["update-mime-database", str(root / "share/mime")], stdout=sys.stderr, check=True)
for file in Path("/usr/share/glib-2.0/schemas").iterdir():
    if file.name.endswith((".xml", ".gschema.override")):
        copy(file, "share/glib-2.0/schemas/" + file.name)
subprocess.run(["glib-compile-schemas", str(root / "share/glib-2.0/schemas")], stdout=sys.stderr, check=True)
for directory in ("/usr/share/fonts/truetype/dejavu", "/usr/share/alsa"):
    destination = root / "share" / Path(directory).relative_to("/usr/share")
    shutil.copytree(directory, destination, symlinks=False)
    for file in Path(directory).rglob("*"):
        if file.is_file(): origins[str((destination / file.relative_to(directory)).relative_to(root))] = str(file)
for file in Path("/usr/lib/x86_64-linux-gnu/pulseaudio").glob("*.so"):
    copy(file, "lib/" + file.name)
# EGL vendors and Gallium are loaded dynamically, so ldd alone misses them.
# Keep a software-rendering fallback for machines without a visible GPU; do not
# bundle proprietary drivers or force LIBGL_ALWAYS_SOFTWARE on real desktops.
copy("/usr/share/glvnd/egl_vendor.d/50_mesa.json", "share/glvnd/egl_vendor.d/50_mesa.json")
copy("/usr/lib/x86_64-linux-gnu/libEGL_mesa.so.0", "lib/libEGL_mesa.so.0")
copy("/usr/lib/x86_64-linux-gnu/libGLESv2.so.2", "lib/libGLESv2.so.2")
copy("/usr/lib/x86_64-linux-gnu/dri/swrast_dri.so", "lib/dri/swrast_dri.so")
for file in Path("/usr/lib/x86_64-linux-gnu").glob("libgallium-*.so"):
    copy(file, "lib/" + file.name)

# Keep host glibc/loader together; new-on-old glibc is explicitly unsupported.
host = re.compile(r"^(ld-linux.*|lib(c|m|mvec|anl|BrokenLocale|thread_db|pthread|dl|rt|resolv|util|nss_.*)\.so(?:\..*)?)$")
pending = [p for p in root.rglob("*") if p.is_file() and p.read_bytes()[:4] == b"\x7fELF"]
visited = set()
environment["LD_LIBRARY_PATH"] = str(root / "lib") + ":" + str(stage / "lib")
while pending:
    file = pending.pop()
    if file in visited: continue
    visited.add(file)
    result = subprocess.run(["ldd", str(file)], env=environment, text=True, capture_output=True)
    if result.returncode and "not a dynamic executable" not in result.stderr + result.stdout:
        raise RuntimeError(result.stderr + result.stdout)
    for line in result.stdout.splitlines():
        if "not found" in line: raise RuntimeError(f"Unresolved dependency for {file}: {line}")
        match = re.search(r"\s*(\S+) => (/\S+)", line)
        if not match: continue
        soname, path = match.groups()
        if host.fullmatch(soname): continue
        target = root / "lib" / soname
        if not target.exists(): pending.append(copy(path, "lib/" + soname))

# Retain each staged shared library only under its DT_SONAME. The ZIP deliberately
# has no symlinks, and copying all .so aliases would duplicate the WebKit engine.
for file in list((root / "lib").glob("*.so*")):
    dynamic = subprocess.run(["readelf", "-d", str(file)], text=True, capture_output=True, check=True).stdout
    match = re.search(r"\(SONAME\).*\[(.*?)\]", dynamic)
    if match and match[1] != file.name and (root / "lib" / match[1]).exists(): file.unlink()

(root / "etc/fonts").mkdir(parents=True)
(root / "etc/fonts/fonts.conf").write_text("""<?xml version="1.0"?>
<!DOCTYPE fontconfig SYSTEM "urn:fontconfig:fonts.dtd">
<fontconfig><dir prefix="relative">../../share/fonts</dir><cachedir prefix="xdg">fontconfig</cachedir></fontconfig>
""")
shutil.copy2(repo.parent / "LICENSE.md", root / "CRYSTALCAST-LICENSE")
packages = set()
def package_owners(result):
    return {line.split(": ", 1)[0] for line in result.stdout.splitlines()
            if re.fullmatch(r"[a-z0-9][a-z0-9+.-]*(?::[a-z0-9-]+)?", line.split(": ", 1)[0])}
for original in set(origins.values()) | set(static_inputs):
    result = subprocess.run(["dpkg-query", "-S", original], text=True, capture_output=True)
    if result.returncode or not package_owners(result):
        # ldd can report /lib aliases on a merged-/usr build host, whereas dpkg
        # records only the real versioned file under /usr/lib.
        result = subprocess.run(["dpkg-query", "-S", str(Path(original).resolve())], text=True, capture_output=True)
    owners = package_owners(result)
    if result.returncode or not owners: raise RuntimeError(f"Cannot determine source/license owner: {original}")
    packages.update(owners)
inventory = []
for package in sorted(packages):
    metadata = subprocess.check_output(["dpkg-query", "-W", "-f=${binary:Package}\t${Version}\t${source:Package}\t${source:Version}", package], text=True)
    binary, version, source, source_version = metadata.split("\t")
    inventory.append(dict(binary=binary, version=version, source=source, sourceVersion=source_version))
    name = binary.split(":")[0]
    copyright = Path("/usr/share/doc") / name / "copyright"
    if not copyright.exists(): raise RuntimeError(f"Missing license notice for {binary}")
    copy(copyright, "licenses/" + name + ".copyright")
shutil.copytree("/usr/share/common-licenses", root / "licenses/common", dirs_exist_ok=True)
(root / "component-inventory.json").write_text(json.dumps(inventory, indent=2))
(root / "WPE-SOURCE-NOTICE.txt").write_text("WPE WebKit 2.54.1, LGPL/BSD and component licenses. See corresponding source release and packaging patches.\n")
# WPE's source archive carries the full set of bundled third-party notices.
wpe = work / "wpewebkit-2.54.1"
for path in wpe.rglob("*"):
    if path.is_file() and (path.name.lower().startswith(("license", "copying")) or path.name == "copyright"):
        target = root / "licenses/wpe" / path.relative_to(wpe)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)

files = []
glibc_versions = set()
for file in sorted(root.rglob("*")):
    if not file.is_file(): continue
    with file.open("rb") as stream:
        elf = stream.read(4) == b"\x7fELF"
    if elf:
        versions = subprocess.run(["readelf", "--version-info", str(file)], text=True, capture_output=True, check=True).stdout
        glibc_versions.update(re.findall(r"GLIBC_([0-9]+\.[0-9]+)", versions))
    executable = bool(file.stat().st_mode & 0o111)
    with file.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    files.append(dict(path=file.relative_to(root).as_posix(), size=file.stat().st_size,
                      sha256=digest, executable=executable))
if any(tuple(map(int, version.split("."))) > (2, 39) for version in glibc_versions):
    raise RuntimeError("The runtime accidentally requires a newer glibc than the declared Ubuntu 24.04 baseline")
manifest = dict(schema=1, version=args.version, architecture="x86_64", minimumGlibc="2.39", protocol=1,
                engine="WPE WebKit 2.54.1", entrypoint="bin/crystalcast-browser", files=files)
manifest_path = output / "runtime-manifest.json"
manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
archive = output / f"CrystalCast-LinuxBrowser-{args.version}-linux-x64-glibc239.zip"
with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as zip:
    for item in files: zip.write(root / item["path"], item["path"])
print(json.dumps(dict(archive=str(archive), bytes=archive.stat().st_size,
    sha256=hashlib.file_digest(archive.open("rb"), "sha256").hexdigest(),
    manifestSha256=hashlib.file_digest(manifest_path.open("rb"), "sha256").hexdigest(), files=len(files)), indent=2))
