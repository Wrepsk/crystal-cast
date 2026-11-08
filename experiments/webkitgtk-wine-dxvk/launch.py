#!/usr/bin/env python3
"""One-command entry point for the precompiled Wine/DXVK test release."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

import run

ROOT = Path(__file__).resolve().parent


def preflight(wine):
    problems = []
    runtime, missing = run.native_report.dependencies(ROOT / "native/bin/gtk-frame-probe")
    problems.extend(missing)
    try:
        manifest = json.loads((ROOT / "package.json").read_text())
        for name, digest in manifest["sha256"].items():
            path = Path(name)
            if path.is_absolute() or ".." in path.parts or hashlib.sha256((ROOT / path).read_bytes()).hexdigest() != digest:
                problems.append("Package integrity check failed. Download and extract a fresh complete package.")
                break
    except (OSError, ValueError, KeyError):
        problems.append("A required test file or package manifest is missing; extract the complete download.")
    version = run.native_report.command([wine, "--version"]) if wine else None
    wine_version = run.native_report.safe_text(version.stdout.strip()) if version and version.returncode == 0 else None
    if not wine_version:
        problems.append("Wine was not found or cannot run. Use an existing Linux Wine installation, or pass --wine /full/path/to/wine64. Proton-only launcher setups are not supported by this test.")
    return {"environment": run.native_report.environment(), "runtime": runtime, "wine_version": wine_version,
            "dependency_problems": problems, "tests": {},
            "scope": "Availability check only; no frame delivery or Vulkan/DXVK initialization has been tested.",
            "privacy": "Local allowlisted hardware/runtime information only. Nothing uploaded."}


def main():
    parser = argparse.ArgumentParser(description=__doc__, epilog="Other options are forwarded to the harness, including --overlay, --mode, --seconds and --warmup.")
    parser.add_argument("--wine", help="Optional path to an existing Wine executable")
    parser.add_argument("--output", type=Path, default=Path.cwd() / "wine-dxvk-results")
    parser.add_argument("--check", action="store_true", help="Write an availability report without starting Wine or opening windows")
    args, extra = parser.parse_known_args()
    if args.output.exists() and any(args.output.iterdir()):
        parser.error("Choose an empty results directory with --output; earlier reports are preserved")
    if args.wine:
        wine = shutil.which(args.wine) or str(Path(args.wine).resolve())
    else:
        wine = next((x for x in (shutil.which("wine"), shutil.which("wine64"), "/usr/lib/wine/wine64") if x and Path(x).is_file()), None)
    report = preflight(wine)
    if args.check or report["dependency_problems"]:
        args.output.mkdir(parents=True, exist_ok=True)
        (args.output / "results.json").write_text(json.dumps(report, indent=2) + "\n")
        lines = ["CrystalCast Wine/DXVK test: availability report", report["scope"],
                 "Wine: " + (report["wine_version"] or "unavailable")]
        lines += report["dependency_problems"] or ["Basic runtime checks passed. Run without --check to test actual playback and DXVK frame delivery."]
        lines += ["No system packages or Wine prefixes were changed. Nothing uploaded.",
                  "Send summary.txt and results.json from this results folder."]
        (args.output / "summary.txt").write_text("\n".join(lines) + "\n")
        print("\n".join(lines))
        print("Results folder: " + str(args.output.resolve()))
        return 2 if report["dependency_problems"] else 0
    command = [sys.executable, str(ROOT / "run.py"), "--wine", wine,
               "--dxvk-dir", str(ROOT / "dxvk/x64"), "--native-binary", str(ROOT / "native/bin/gtk-frame-probe"),
               "--fixture", str(ROOT / "native/data/video.mp4"), "--output", str(args.output), *extra]
    os.execv(sys.executable, command)


if __name__ == "__main__":
    raise SystemExit(main())
