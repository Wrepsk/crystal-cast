import copy
import unittest
import run_benchmark as runner


def evidence(count=600):
    frames = [{"kind": "frame", "seq": n + 1, "time_us": 3000000 + n * 1000000 / 60,
               "marker": n % 1024, "sample_hash": n * 17, "bars_valid": True, "overlay_valid": True,
               "downloaded": True, "download_us": 100, "widget_width": 1920, "widget_height": 1080,
               "browser_dmabuf": 1, "browser_gl": 0, "browser_memory": 0,
               "browser_cairo": 0, "uninspected_nodes": 0} for n in range(count)]
    summary = {"exit_code": 0, "observations": [{"kind": "render_context", "gl_renderer": "AMD Radeon test GPU"}],
               "measured_window_seconds": 10, "measurement_start_us": 0, "options": {"warmup": 3, "mode": "shm"},
               "environment": [{"renderer": "GskGLRenderer", "display": "GdkWaylandDisplay", "gtk": "4.14.5", "webkit": "2.52.6"}],
               "consumer_errors": [], "output_textures": {"GdkDmabufTexture": count}, "direct_frames": count,
               "end": [{"drops": 0}], "extraction_us": {"total_us": {"mean": 200, "p95": 300}},
               "unaudited_total_us": None, "consumer_copy_us": None, "consumer_age_us": None,
               "tree_cpu_percent": 100, "tree_rss_peak_mib": 300}
    consumer = [{"seq": f["seq"], "marker": f["marker"], "sample_hash": f["sample_hash"]} for f in frames]
    media = [{"kind": "media", "time_us": 3000000, "data": {"error": None, "dropped": 0}},
             {"kind": "media", "time_us": 13000000, "data": {"error": None, "dropped": 0}}]
    return summary, frames + media, consumer


class ReportTests(unittest.TestCase):
    def test_verified_sixty(self):
        result = runner.analyze(*evidence())
        self.assertEqual(result["verified_unique_fps"], 60)
        self.assertTrue(result["sustained_1080p60_verified"])

    def test_fast_texture_submission_does_not_prove_delivery(self):
        summary, events, _ = evidence()
        summary["options"]["mode"] = "texture"
        for i, event in enumerate(events[:600]):
            event["downloaded"] = i % 30 == 0
        result = runner.analyze(summary, events, [])
        self.assertIsNone(result["verified_unique_fps"])
        self.assertFalse(result["sustained_1080p60_verified"])
        self.assertIsNone(result["skipped_source_frames"])

    def test_repeated_and_skipped_counted_separately(self):
        summary, events, consumer = evidence()
        events[2]["marker"] = consumer[2]["marker"] = 1
        result = runner.analyze(summary, events, consumer)
        self.assertEqual(result["repeated_frames"], 1)
        self.assertEqual(result["skipped_source_frames"], 1)

    def test_reordering_is_not_a_thousand_skipped_frames(self):
        frames = [{"marker": n, "time_us": i * 17000} for i, n in enumerate((100, 101, 90, 102))]
        _, repeats, skipped, backwards, ambiguous = runner.frame_sequence(frames)
        self.assertEqual(backwards, 1)
        self.assertIsNone(skipped)
        self.assertFalse(ambiguous)

    def test_modulo_wrap_and_long_gap(self):
        frames = [{"marker": n, "time_us": i * 17000} for i, n in enumerate((1022, 1023, 0, 1))]
        positions, repeats, skipped, backwards, ambiguous = runner.frame_sequence(frames)
        self.assertEqual(positions, [0, 1, 2, 3])
        self.assertEqual((repeats, skipped, backwards, ambiguous), (0, 0, 0, False))
        frames[3]["time_us"] = 12000000
        self.assertTrue(runner.frame_sequence(frames)[4])

    def test_torn_slot_never_passes(self):
        summary, events, consumer = evidence()
        consumer[50]["sample_hash"] += 1
        result = runner.analyze(summary, events, consumer)
        self.assertEqual(result["status"], "invalid")
        self.assertIsNone(result["verified_unique_fps"])
        self.assertFalse(result["sustained_1080p60_verified"])

    def test_missing_independent_consumer_is_invalid(self):
        summary, events, _ = evidence()
        self.assertEqual(runner.analyze(summary, events, [])["status"], "invalid")

    def test_native_failure_with_pixels_is_invalid_not_unavailable(self):
        summary, events, consumer = evidence()
        summary["exit_code"] = 1
        self.assertEqual(runner.analyze(summary, events, consumer)["status"], "invalid")

    def test_wrong_resolution_or_channel_order_fails(self):
        for key, value in (("widget_width", 960), ("bars_valid", False)):
            summary, events, consumer = evidence()
            events[20][key] = value
            self.assertFalse(runner.analyze(summary, events, consumer)["integrity_passed"])

    def test_overlay_fails_independently(self):
        summary, events, consumer = evidence()
        events[20]["overlay_valid"] = False
        self.assertFalse(runner.analyze(summary, events, consumer, overlay=True)["integrity_passed"])

    def test_direct_unavailable_has_no_performance_numbers(self):
        summary, _, _ = evidence()
        result = runner.analyze(summary, [{"kind": "unavailable", "reason": "No exact texture"}], [])
        self.assertEqual(result["status"], "unavailable")
        self.assertNotIn("texture_submission_fps", result)

    def test_cairo_does_not_inherit_probe_gpu_classification(self):
        classification = runner.acceleration([{"kind": "graphics", "gl_renderer": "AMD GPU"}],
                                             {"browser_cairo": 60}, "GskCairoRenderer")
        self.assertIn("CPU GSK", classification)
        self.assertNotIn("hardware GTK", classification)

    def test_capabilities_do_not_prove_browser_dmabuf_or_active_gpu(self):
        summary, events, consumer = evidence()
        summary["observations"] = [{"kind": "graphics", "gl_renderer": "AMD GPU", "egl_dmabuf_import": True}]
        for event in events[:600]:
            event["browser_dmabuf"] = 0
        result = runner.analyze(summary, events, consumer)
        self.assertEqual(result["browser_node_frames"]["browser_dmabuf"], 0)
        self.assertIn("unverified", result["acceleration_classification"])

    def test_private_strings_are_not_retained(self):
        text = runner.safe_text("error /home/alice/private/video 127.0.0.1 alice@host 01234567-1234-1234-1234-0123456789ab")
        for identifier in ("/home", "alice", "127.0.0.1", "alice@host", "01234567"):
            self.assertNotIn(identifier, text)

    def test_unknown_logs_are_discarded_but_error_category_survives(self):
        result = runner.diagnostic_categories("/home/alice/secret: bwrap: Permission denied on 192.168.1.25")
        self.assertEqual(result, ["sandbox_or_permissions_problem"])


if __name__ == "__main__":
    unittest.main()
