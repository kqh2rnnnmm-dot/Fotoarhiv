"""Tests of evidence/packaging controls, not Windows application tests."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile
from windows_ci import package_publish, run_step


class EvidenceTests(unittest.TestCase):
    def test_failed_command_is_preserved_and_raises(self):
        with tempfile.TemporaryDirectory() as folder:
            evidence = Path(folder)
            report = {"steps": []}
            with self.assertRaises(RuntimeError):
                run_step("failed", [sys.executable, "-c", "print('intentional failure'); raise SystemExit(7)"], evidence, report)
            actual = json.loads((evidence / "run.json").read_text())
            self.assertEqual(actual["steps"][0]["exit_code"], 7)
            self.assertEqual(actual["steps"][0]["status"], "FAIL")
            self.assertIn("intentional failure", (evidence / "failed.log").read_text())

    def test_no_archive_without_all_required_checks(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            with self.assertRaises(RuntimeError):
                package_publish(root, root / "program", root, {"steps": []})
            self.assertFalse((root / "program.zip").exists())

    def test_incomplete_publish_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            with self.assertRaises(RuntimeError):
                package_publish(root, root / "program", root, self.passed())
            self.assertFalse((root / "program.zip").exists())

    def test_full_synthetic_publish_is_archived(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            publish = root / "publish"
            publish.mkdir()
            for name in ["Fotoarhiv.exe", "e_sqlite3.dll", "coreclr.dll", "wpfgfx_cor3.dll", "extra.runtime.json"]:
                (publish / name).write_bytes(b"synthetic packaging fixture, not executable")
            (publish / "sub").mkdir()
            (publish / "sub" / "dependency.dll").write_bytes(b"dependency fixture")
            archive = package_publish(publish, root / "program", root, self.passed())
            with zipfile.ZipFile(archive) as z:
                self.assertIsNone(z.testzip())
                self.assertIn("sub/dependency.dll", z.namelist())
                self.assertIn("extra.runtime.json", z.namelist())
            manifest = json.loads((root / "publish-manifest.json").read_text())
            self.assertEqual(len(manifest), 6)

    def test_no_archive_when_p1_gui_did_not_pass(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            report = self.passed()
            report['steps'] = [step for step in report['steps'] if step['name'] != 'p1_gui']
            with self.assertRaises(RuntimeError):
                package_publish(root, root / 'program', root, report)
            self.assertFalse((root / 'program.zip').exists())

    @staticmethod
    def passed():
        return {"steps": [{"name": name, "status": "PASS"} for name in ["restore", "build", "xunit", "publish", "startup", "gui", "p1_gui"]]}


if __name__ == "__main__":
    unittest.main()
