"""Offline resource closure and release-validator regression checks."""
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("verify_single_source_release", ROOT / "tools/verify_release.py")
RELEASE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RELEASE)


class SingleSourceBundleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix="heymelody-bundle-test-")
        cls.directory = Path(cls.temporary.name)
        cls.bundle = cls.directory / "ResourceBundles"
        source = ROOT / "src/HeyMelody.Resources/Bundles"
        cls.bundle.mkdir()
        for name in ("artwork.json", "provenance.json", "supplemental.json"):
            shutil.copyfile(source / name, cls.bundle / name)
        shutil.copytree(source / "data", cls.bundle / "data")
        (cls.directory / "HeyMelody.Resources.dll").write_bytes(b"validator fixture")
        notice = cls.directory / "licenses/upstream/Extracted-Earphone-Resources-NOTICE.txt"
        notice.parent.mkdir(parents=True)
        notice.write_text("validator fixture", encoding="utf-8")

    @classmethod
    def tearDownClass(cls):
        cls.temporary.cleanup()

    def test_current_bundle_is_complete_and_single_source(self):
        receipt = RELEASE.verify_resource_bundle(self.directory)
        self.assertEqual(receipt["artworkEntries"], 365)
        self.assertEqual(receipt["catalogCount"], 1)
        self.assertEqual(receipt["dataFiles"], 635)
        source = ROOT / "src/HeyMelody.Resources/Bundles"
        keys = json.loads((source / "default-keys.json").read_text(encoding="utf-8"))
        self.assertEqual(set(keys), {"HeyMelodyKey"})
        self.assertFalse((source / "wireless.json").exists())

    def test_unreferenced_asset_is_rejected(self):
        path = self.bundle / "data/unexpected.bin"
        try:
            path.write_bytes(b"unexpected")
            with self.assertRaises(AssertionError):
                RELEASE.verify_resource_bundle(self.directory)
        finally:
            path.unlink(missing_ok=True)

    def test_missing_animation_dependency_is_rejected(self):
        rows = json.loads((self.bundle / "artwork.json").read_text(encoding="utf-8"))
        dependency = None
        for row in rows:
            if row["kind"] != "lottie":
                continue
            path = self.bundle / row["path"]
            for asset in json.loads(path.read_text(encoding="utf-8")).get("assets", []):
                relative = str(asset.get("u", "")) + str(asset.get("p", ""))
                if relative and not relative.startswith("data:"):
                    dependency = path.parent / relative
                    break
            if dependency:
                break
        self.assertIsNotNone(dependency)
        raw = dependency.read_bytes()
        try:
            dependency.unlink()
            with self.assertRaises(AssertionError):
                RELEASE.verify_resource_bundle(self.directory)
        finally:
            dependency.write_bytes(raw)

    def test_tampered_scanning_image_is_rejected(self):
        row = json.loads((self.bundle / "supplemental.json").read_text(encoding="utf-8"))[0]
        path = self.bundle / row["path"]
        raw = path.read_bytes()
        self.assertEqual(hashlib.sha256(raw).hexdigest(), row["sha256"])
        try:
            path.write_bytes(raw[:-1] + bytes([raw[-1] ^ 1]))
            with self.assertRaises(AssertionError):
                RELEASE.verify_resource_bundle(self.directory)
        finally:
            path.write_bytes(raw)


if __name__ == "__main__":
    unittest.main()
