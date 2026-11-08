import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import launch


class LauncherTests(unittest.TestCase):
    def test_missing_runtime_and_wine_have_actionable_report(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "package.json").write_text(json.dumps({"sha256": {}}))
            with patch.object(launch, "ROOT", root), \
                 patch.object(launch.run.native_report, "dependencies", return_value=(None, ["Missing WebKitGTK runtime"])), \
                 patch.object(launch.run.native_report, "environment", return_value={}):
                report = launch.preflight(None)
            self.assertEqual(len(report["dependency_problems"]), 2)
            self.assertIn("--wine", report["dependency_problems"][-1])
            self.assertEqual(report["tests"], {})

    def test_changed_file_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "file").write_text("changed")
            (root / "package.json").write_text(json.dumps({"sha256": {"file": "0" * 64}}))
            with patch.object(launch, "ROOT", root), \
                 patch.object(launch.run.native_report, "dependencies", return_value=({}, [])), \
                 patch.object(launch.run.native_report, "environment", return_value={}):
                report = launch.preflight(None)
            self.assertTrue(any("integrity" in x for x in report["dependency_problems"]))

    def test_check_writes_files_without_executing_or_initializing_wine(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "reports"
            report = {"scope": "Availability only", "wine_version": None, "dependency_problems": ["Wine unavailable"], "tests": {}}
            with patch.object(sys, "argv", ["launch.py", "--check", "--output", str(output)]), \
                 patch.object(launch, "preflight", return_value=report), patch.object(launch.os, "execv") as execute:
                self.assertEqual(launch.main(), 2)
                execute.assert_not_called()
            self.assertEqual(json.loads((output / "results.json").read_text())["tests"], {})
            self.assertIn("Wine unavailable", (output / "summary.txt").read_text())

    def test_existing_reports_are_preserved(self):
        with tempfile.TemporaryDirectory() as temporary:
            existing = Path(temporary) / "summary.txt"
            existing.write_text("previous result")
            with patch.object(sys, "argv", ["launch.py", "--output", temporary]), self.assertRaises(SystemExit) as error:
                launch.main()
            self.assertEqual(error.exception.code, 2)
            self.assertEqual(existing.read_text(), "previous result")


if __name__ == "__main__":
    unittest.main()
