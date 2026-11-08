#!/usr/bin/env python3
"""Copy compact evidence, preserving hashes of the larger ignored raw records."""
import hashlib
import json
from pathlib import Path
import sys
from verify_results import verify

ROOT = Path(__file__).resolve().parent


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


if __name__ == "__main__":
    destination = Path(sys.argv[1])
    destination.mkdir(parents=True, exist_ok=True)
    checks = []
    for arg in sys.argv[2:]:
        path = Path(arg)
        checks.append(verify(path))
        summary = json.loads((path / "summary.json").read_text())
        # Earlier runs started the stop timer after synchronous WebKit launch.
        # Normalize using actual monotonic start/end records, retaining nominal FPS.
        duration = (summary["end"][-1]["time_us"] - summary["environment"][0]["time_us"]) / 1e6 - summary["options"]["warmup"]
        summary["measured_window_seconds"] = duration
        summary["fps_measured_window"] = summary["frames"] / duration
        if summary["marker_audits"] == summary["frames"]:
            summary["changed_frame_fps"] = summary["marker_changes"] / duration
        summary["raw_sha256"] = {name: digest(path / name) for name in
                                 ("summary.json", "events.jsonl", "consumer.jsonl", "stderr.log")}
        (destination / (path.name + ".json")).write_text(json.dumps(summary, indent=2) + "\n")
        (destination / (path.name + ".log")).write_text((path / "stderr.log").read_text())
    record = {"passed": len(checks), "checks": checks,
              "source_sha256": {name: digest(ROOT / name) for name in
                                ("probe.c", "Makefile", "benchmark.py", "run_matrix.py", "verify_results.py")}}
    (destination / "validation.json").write_text(json.dumps(record, indent=2) + "\n")
