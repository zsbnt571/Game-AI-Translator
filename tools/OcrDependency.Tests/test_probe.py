"""Offline tests for the metadata probe. Run with an explicit test Python."""
import importlib.util
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import unittest


WORKERS = Path(__file__).resolve().parents[2] / "Source" / "Screenshot" / "workers"


class ProbeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="game-ai-ocr-probe-tests-")
        self.root = Path(self.temporary.name)
        self.site = self.root / "site-packages"
        self.site.mkdir()

    def tearDown(self):
        self.temporary.cleanup()

    def package(self, name, version, suffix=""):
        metadata = self.site / (name + "-" + version + suffix + ".dist-info")
        metadata.mkdir()
        (metadata / "METADATA").write_text("Metadata-Version: 2.1\nName: " + name + "\nVersion: " + version + "\n", encoding="utf-8")
        return metadata

    def probe(self, extra_environment=None):
        environment = os.environ.copy()
        environment.update(extra_environment or {})
        return subprocess.run([sys.executable, "-I", "-S", "-B", str(WORKERS / "ocr_dependency_probe.py"), str(self.site)],
                              capture_output=True, text=True, timeout=8, env=environment)

    def test_exact_metadata_without_importing_packages(self):
        self.package("rapidocr", "3.9.2")
        self.package("onnxruntime", "1.28.0")
        for name in ("rapidocr", "onnxruntime"):
            package = self.site / name
            package.mkdir()
            (package / "__init__.py").write_text("raise RuntimeError('Package must not be imported by metadata check')", encoding="utf-8")
        result = self.probe()
        self.assertEqual(result.returncode, 0, result.stderr)
        data = json.loads(result.stdout)
        self.assertEqual(data["rapidocrVersion"], "3.9.2")
        self.assertEqual(data["onnxruntimeVersion"], "1.28.0")
        self.assertEqual(data["rapidocrLocation"], str(self.site.resolve()))
        self.assertEqual(data["schema"], 1)

    def test_empty_environment_reports_missing_versions(self):
        result = self.probe()
        self.assertEqual(result.returncode, 0, result.stderr)
        data = json.loads(result.stdout)
        self.assertEqual(data["rapidocrVersion"], "")
        self.assertEqual(data["onnxruntimeVersion"], "")

    def test_duplicate_distribution_metadata_is_rejected(self):
        self.package("rapidocr", "3.9.2")
        self.package("rapidocr", "3.9.3")
        result = self.probe()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Duplicate package metadata", result.stderr)

    def test_actual_version_is_reported_without_substitution(self):
        self.package("rapidocr", "3.9.3")
        result = self.probe()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["rapidocrVersion"], "3.9.3")

    def test_pythonpath_sitecustomize_and_pth_are_not_executed(self):
        marker = self.root / "unexpected-hook"
        poison = "from pathlib import Path; Path(" + repr(str(marker)) + ").write_text('unexpected')\n"
        (self.site / "sitecustomize.py").write_text(poison, encoding="utf-8")
        (self.site / "hook.pth").write_text("import sitecustomize\n", encoding="utf-8")
        result = self.probe({"PYTHONPATH": str(self.site), "PYTHONSTARTUP": str(self.site / "sitecustomize.py")})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(marker.exists())

    def test_no_site_packages_search_outside_explicit_directory(self):
        elsewhere = self.root / "other"
        elsewhere.mkdir()
        extra = elsewhere / "rapidocr-3.9.2.dist-info"
        extra.mkdir()
        (extra / "METADATA").write_text("Name: rapidocr\nVersion: 3.9.2\n", encoding="utf-8")
        result = self.probe({"PYTHONPATH": str(elsewhere)})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["rapidocrVersion"], "")

    def test_alpha_network_repair_is_refused(self):
        spec = importlib.util.spec_from_file_location("rapid_health_test", WORKERS / "rapid_health.py")
        health = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(health)
        health.model_root = lambda: self.root / "empty-models"
        result = health.repair(True)
        self.assertEqual(result["code"], "LOCAL_DEPENDENCY_REQUIRED")
        self.assertEqual(result["downloadsAttempted"], 0)
        self.assertFalse((self.root / "empty-models").exists())

    def test_sdk_network_guard_prevents_connection(self):
        spec = importlib.util.spec_from_file_location("rapid_health_guard_test", WORKERS / "rapid_health.py")
        health = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(health)
        original = socket.create_connection
        with health.no_network():
            with self.assertRaisesRegex(RuntimeError, "禁止联网"):
                socket.create_connection(("127.0.0.1", 1))
        self.assertIs(socket.create_connection, original)

    def test_worker_sources_compile_without_importing_dependencies(self):
        for path in WORKERS.glob("*.py"):
            compile(path.read_text(encoding="utf-8-sig"), str(path), "exec")


if __name__ == "__main__":
    unittest.main(verbosity=2)
