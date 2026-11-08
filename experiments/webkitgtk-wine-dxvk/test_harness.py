"""Failure-sensitive report tests; a submitted texture must not become verified delivery."""
import copy
import io
import unittest
import run


def evidence(mode="verify"):
    events = [{"kind": "measurement_start", "time_us": 0},
              {"kind": "media", "data": {"width": 1920, "height": 1080, "error": None}}]
    delivered = []
    for i in range(720):
        timestamp = 1000000 + i * 1000000 / 60
        events.append({"kind": "frame", "seq": i + 1, "marker": i % 1024, "time_us": timestamp,
                       "sample_hash": 42, "bars_valid": True, "widget_width": 1920, "widget_height": 1080,
                       "total_us": 1000, "download_us": 800, "texture": "GdkDmabufTexture"})
        delivered.append({"kind": "consumer_frame", "seq": i + 1, "marker": i % 1024, "sample_hash": 42,
                          "source_valid": True, "gpu_audited": mode == "verify" or i % 60 == 0,
                          "gpu_valid": True, "gpu_marker": i % 1024, "presented": True,
                          "reply_time_us": timestamp + 5000,
                          **{key: 100 for key in ("source_check_us", "update_api_us", "gpu_copy_completion_us",
                              "gpu_readback_validation_us", "present_api_us", "consumer_total_us")}})
    events.append({"kind": "end", "time_us": 13000000, "drops": 0})
    return events, delivered


def report(events, delivered, mode="verify", version="v2.4.1", errors=None):
    return run.analyze(events, delivered, {}, errors or [], [0, 0], 12, 1, mode, version)


class HarnessReports(unittest.TestCase):
    def test_complete_gpu_verified_delivery(self):
        r = report(*evidence())
        self.assertTrue(r["sustained_verified_1080p60"])
        self.assertEqual(r["complete_path_verified_unique_fps"], 60)

    def test_sparse_audits_never_claim_gpu_verified_fps(self):
        r = report(*evidence("upload"), mode="upload")
        self.assertEqual(r["status"], "ok")
        self.assertIsNone(r["complete_path_verified_unique_fps"])
        self.assertFalse(r["sustained_verified_1080p60"])

    def test_missing_delivery_or_wrong_pixels_fail(self):
        for mutation in ("missing", "hash", "gpu", "present", "counter", "native", "duplicate"):
            with self.subTest(mutation=mutation):
                events, frames = evidence()
                if mutation == "missing": frames.pop()
                if mutation == "hash": frames[3]["sample_hash"] = 43
                if mutation == "gpu": frames[3]["gpu_valid"] = False
                if mutation == "present": frames[3]["presented"] = False
                if mutation == "counter": frames[3]["gpu_marker"] = 1023
                if mutation == "native": events[3]["bars_valid"] = False
                if mutation == "duplicate": frames[3] = copy.copy(frames[2])
                r = report(events, frames)
                self.assertFalse(r["integrity_passed"])
                self.assertFalse(r["sustained_verified_1080p60"])

    def test_wined3d_is_not_dxvk(self):
        self.assertFalse(report(*evidence(), version=None)["integrity_passed"])

    def test_tail_delivery_counts_toward_fps(self):
        events, frames = evidence()
        frames[-1]["reply_time_us"] += 1000000
        r = report(events, frames)
        self.assertLess(r["complete_path_verified_unique_fps"], 59)
        self.assertFalse(r["sustained_verified_1080p60"])

    def test_incomplete_window_does_not_sustain(self):
        events, frames = evidence()
        events[-1]["time_us"] = 12900000
        self.assertFalse(report(events, frames)["sustained_verified_1080p60"])

    def test_arrival_stall_cannot_hide_behind_source_cadence(self):
        events, frames = evidence()
        # All input markers arrive eventually, but none is delivered in the first second.
        for frame in frames[:60]:
            frame["reply_time_us"] += 1000000
        self.assertFalse(report(events, frames)["sustained_verified_1080p60"])

    def test_broken_protocol_fails_even_with_good_frames(self):
        self.assertFalse(report(*evidence(), errors=["Protocol error"])["integrity_passed"])

    def test_receive_rejects_truncated_or_oversized_or_nonobject(self):
        for data in (b'{"seq":1}', b'"value"\n', b'{' + b' ' * 4096 + b'}\n'):
            with self.subTest(data_length=len(data)):
                with self.assertRaises(ValueError):
                    run.receive_line(io.BytesIO(data))


if __name__ == "__main__":
    unittest.main()
