#!/usr/bin/env python3
"""Replay correctness checks against locally recorded benchmark evidence."""
import json
from pathlib import Path
import sys


def read_lines(path):
    return [json.loads(line) for line in path.read_text().splitlines()]


def verify(path):
    summary = json.loads((path / "summary.json").read_text())
    events = read_lines(path / "events.jsonl")
    frames = [e for e in events if e["kind"] == "frame"]
    media = [e for e in events if e["kind"] == "media"]
    assert summary["exit_code"] == 0 and frames
    assert [e["seq"] for e in frames] == list(range(1, len(frames) + 1))
    assert all(b["time_us"] > a["time_us"] for a, b in zip(frames, frames[1:]))
    assert not any(e["data"]["error"] for e in media)
    assert any(e["data"]["time"] > 1 and e["data"]["width"] == 1920 and
               e["data"]["height"] == 1080 for e in media)
    assert summary["marker_changes"] > 0
    result = {"run": path.name, "frames": len(frames), "pixel_updates": summary["marker_changes"]}
    if summary["options"]["mode"] in ("shm", "render-shm"):
        consumer = read_lines(path / "consumer.jsonl")
        assert len(consumer) == len(frames)
        assert all(a["seq"] == b["seq"] and a["marker"] == b["marker"] for a, b in zip(frames, consumer))
        assert summary["channel_bars_ok"] and not summary["consumer_errors"]
        result["consumer_verified"] = len(consumer)
    if summary["options"]["exercise_pause"]:
        pause = next(e["time_us"] for e in media if e["data"]["event"] == "pause")
        resume = next(e["time_us"] for e in media if e["time_us"] > pause and e["data"]["event"] == "playing")
        quiet = [e for e in frames if pause + 250000 < e["time_us"] < resume - 250000]
        assert len({e["marker"] for e in quiet}) <= 1
        after = [e["marker"] for e in frames if e["time_us"] > resume + 250000]
        assert len(set(after)) > 10
        result["pause_interval_frames"] = len(quiet)
        result["resume_distinct_frames"] = len(set(after))
    if summary["options"]["hide_after"]:
        hidden = next(e["time_us"] for e in events if e["kind"] == "hidden")
        after = [e for e in frames if e["time_us"] > hidden + 100000]
        assert not after
        result["frames_after_hiding"] = len(after)
    if summary["options"]["consumer_delay_ms"]:
        assert summary["end"][0]["drops"] > 0
        result["backpressure_drops"] = summary["end"][0]["drops"]
    if summary["options"]["overlay"]:
        assert summary["overlay_ok"]
        result["html_overlay_verified"] = True
    return result


if __name__ == "__main__":
    paths = [Path(arg) for arg in sys.argv[1:]]
    if not paths:
        paths = sorted((Path(__file__).resolve().parent / "artifacts/matrix").iterdir())
    results = [verify(path) for path in paths]
    print(json.dumps({"passed": len(results), "checks": results}, indent=2))
