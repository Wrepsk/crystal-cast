#!/usr/bin/env python3
"""Reproduce the software-only Xvfb comparisons sequentially, avoiding contention."""
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parent
OUT = ROOT / "artifacts" / "matrix"
OUT.mkdir(parents=True, exist_ok=True)
base = os.environ | {"GDK_BACKEND": "x11", "GTK_A11Y": "none", "LIBGL_ALWAYS_SOFTWARE": "1",
                     "LP_NUM_THREADS": "4", "WEBKIT_SKIA_ENABLE_CPU_RENDERING": "1",
                     "WEBKIT_GST_DISABLE_GL_SINK": "1", "WEBKIT_DMABUF_RENDERER_FORCE_SHM": "1"}
for key in ("GALLIUM_DRIVER", "MESA_LOADER_DRIVER_OVERRIDE", "WEBKIT_DISABLE_COMPOSITING_MODE"):
    base.pop(key, None)
cases = [
    ("gl-texture", "gl", ["--mode", "texture", "--audit-every", "30"]),
    ("gl-shm", "gl", ["--mode", "shm"]),
    ("ngl-shm", "ngl", ["--mode", "shm"]),
    ("cairo-shm", "cairo", ["--mode", "shm"]),
    ("cairo-poll60", "cairo", ["--mode", "shm", "--poll-hz", "60"]),
    ("cairo-pause", "cairo", ["--mode", "shm", "--exercise-pause", "--seconds", "12"]),
    ("cairo-hidden", "cairo", ["--mode", "shm", "--hide-after", "3", "--seconds", "8"]),
    ("cairo-slow", "cairo", ["--mode", "shm", "--consumer-delay-ms", "100", "--seconds", "8"]),
]
failed = False
for name, renderer, options in cases:
    print(f"Running {name}...", flush=True)
    output = OUT / name
    output.mkdir(exist_ok=True)
    with (output / "runner.log").open("w") as log:
        result = subprocess.run(["dbus-run-session", "--", "xvfb-run", "-a", "-s",
                                 "-screen 0 1920x1080x24", sys.executable, str(ROOT / "benchmark.py"),
                                 "--seconds", "20", "--output", str(output), *options],
                                env=base | {"GSK_RENDERER": renderer}, stdout=log, stderr=log)
    summary = json.loads((output / "summary.json").read_text())
    print(json.dumps({"name": name, "status": result.returncode,
                      "fps": summary["fps_measured_window"], "changed_fps": summary["changed_frame_fps"],
                      "extraction_us": summary["extraction_us"]["total_us"],
                      "cpu": summary["tree_cpu_percent"], "rss_mib": summary["tree_rss_peak_mib"],
                      "drops": summary["end"], "bar_failures": summary["channel_bar_failures"],
                      "mismatches": summary["consumer_marker_mismatches"]}), flush=True)
    failed |= result.returncode != 0
raise SystemExit(int(failed))
