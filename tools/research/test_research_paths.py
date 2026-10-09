"""Offline path regressions for old evidence and current reproduction tools.

Uses only the standard library and temporary synthetic Markdown inputs. No local
research inputs, network, Android, Bluetooth, or application build are required.
"""
from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location(
    "research_runtime_paths", ROOT / "tools/research/earbud-artwork/scripts/research_runtime.py"
)
runtime = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime)


class ResearchPathTests(unittest.TestCase):
    def assert_path(self, historical: str, current: str, base: Path | None = None):
        self.assertEqual(runtime.research_path(historical, base), (ROOT / current).resolve())

    def test_scripts_remain_runnable_from_all_historical_roots(self):
        for topic, filename in (("earbud-artwork", "android_resources.py"),
                                ("online-artwork", "inspect_models.py"),
                                ("online-whitelist", "fetch_whitelist.py")):
            current = f"tools/research/{topic}/scripts/{filename}"
            for prefix in ("docs/research", "research", "tools/research"):
                with self.subTest(topic=topic, prefix=prefix):
                    self.assert_path(f"{prefix}/{topic}/scripts/{filename}", current)
            self.assertTrue((ROOT / current).is_file())

    def test_absolute_and_windows_manifest_paths(self):
        current = "tools/research/online-artwork/scripts/inspect_models.py"
        self.assert_path(str(ROOT / "docs/research/online-artwork/scripts/inspect_models.py"), current)
        self.assert_path(r"docs\research\online-artwork\scripts\inspect_models.py", current)

    def test_old_objects_resolve_to_research(self):
        for relative in ("earbud-artwork/assets.json", "online-artwork/runs/example/results.json",
                         "online-whitelist/evidence/static-contract.json"):
            with self.subTest(relative=relative):
                self.assert_path("docs/research/" + relative, "research/" + relative)

    def test_oppopods_snapshot_and_source_notes_keep_original_archive(self):
        for filename in ("SNAPSHOT.json", "EVIDENCE.md"):
            self.assert_path("docs/research/oppopods-comparison-2026-10-08/" + filename,
                             "research/oppopods-comparison-2026-10-08/originals/" + filename)

    def test_published_reports_stay_in_docs(self):
        for relative in ("calibration-reassessment.md", "earbud-artwork/README.md",
                         "online-artwork/README.md", "3d-validation/README.md"):
            with self.subTest(relative=relative):
                self.assert_path("docs/research/" + relative, "docs/research/" + relative)

    def test_complete_research_readme_and_public_summary_keep_distinct_contents(self):
        with tempfile.TemporaryDirectory(prefix="heymelody-research-paths-") as directory:
            repository = Path(directory).resolve()
            docs = repository / "docs/research"
            research = repository / "research"
            with patch.multiple(runtime, REPOSITORY_ROOT=repository, DOCUMENTATION_ROOT=docs,
                                TOOLS_ROOT=repository / "tools/research", RESEARCH_ROOT=research):
                for topic in ("earbud-artwork", "online-whitelist", "online-artwork", "3d-validation"):
                    full = research / topic / "README.md"
                    summary = docs / topic / "README.md"
                    full.parent.mkdir(parents=True)
                    summary.parent.mkdir(parents=True)
                    full.write_text("complete research and original evidence", encoding="utf-8")
                    summary.write_text("public summary", encoding="utf-8")
                    with self.subTest(topic=topic):
                        self.assertEqual(runtime.research_path(f"research/{topic}/README.md"), full)
                        self.assertEqual(runtime.research_path(full).read_text(encoding="utf-8"),
                                         "complete research and original evidence")
                        self.assertEqual(runtime.research_path(f"docs/research/{topic}/README.md"), summary)
                        self.assertEqual(runtime.research_path(summary).read_text(encoding="utf-8"), "public summary")

    def test_nested_evidence_and_archived_scripts_are_research_objects(self):
        for relative in ("earbud-artwork/evidence/README.md",
                         "earbud-artwork/archive-2026-10-09/scripts/research_runtime.py",
                         "upstreams/OppoPods1/scripts/example.py"):
            with self.subTest(relative=relative):
                self.assert_path("research/" + relative, "research/" + relative)

    def test_local_inputs_and_upstream_legacy_paths(self):
        cases = {
            "docs/research/local/inputs": "research/apps",
            "docs/research/local/inputs/欢律.apk": "research/apps/欢律.apk",
            "docs/research/local/upstreams/OppoPods1/README.md": "research/upstreams/OppoPods1/README.md",
            "docs/research/OppoPods2/example.cs": "research/upstreams/OppoPods2/example.cs",
            "docs/research/local/migration-2026-10-08.json": "research/migrations/migration-2026-10-08.json",
            "docs/research/x4-both-earphones.png": "research/screenshots/x4-both-earphones.png",
            "docs/research/example.msixbundle": "research/installers/example.msixbundle",
        }
        for historical, current in cases.items():
            with self.subTest(historical=historical):
                self.assert_path(historical, current)

    def test_run_relative_paths_use_given_base(self):
        self.assert_path("packages/hash/resource.zip", "research/online-artwork/runs/example/packages/hash/resource.zip",
                         ROOT / "research/online-artwork/runs/example")

    def test_unrelated_files_are_not_remapped(self):
        self.assert_path("src/HeyMelody.Resources/Bundles/artwork.json", "src/HeyMelody.Resources/Bundles/artwork.json", ROOT)

    def test_output_cannot_target_docs_or_tools(self):
        for target in (ROOT / "docs/research/calibration-reassessment.md",
                       ROOT / "tools/research/online-artwork/scripts/inspect_models.py"):
            with self.subTest(target=target):
                with self.assertRaises(ValueError):
                    runtime.checked_output(target)


if __name__ == "__main__":
    unittest.main()
