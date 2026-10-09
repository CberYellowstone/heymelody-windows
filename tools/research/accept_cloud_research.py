#!/usr/bin/env python3
"""Accept saved cloud configuration/artwork offline, preserving historical inputs.

Requires an existing Python 3.11+ environment with cryptography and Pillow.
No network, Android execution, package installation, or application build occurs.
New reports live below research/cloud-acceptance; an explicit historical artifacts
destination is still accepted. Authentication material remains in memory.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import zipfile

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "artifacts"
ACCEPTANCE_ROOT = ROOT / "research/cloud-acceptance"
EVIDENCE_ARCHIVE = ROOT / "research/evidence-archive-2026-10-09"
WHITELIST_RUN = ROOT / "research/online-whitelist/runs/20261007T095049.319796Z"
MODEL_RUNS = {
    "free4": ("20261007T101352.199515Z", "068676a8bb633f35e741f2d0dc7eb919afcb03e89f5c6839b41adbec53287bb1"),
    "x3": ("20261007T101708.195649Z", "b4763c5f9f790b0859131807324bf81c4a1855489fc087e715f673de848119bf"),
}


def sha(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def save(path: Path, value):
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2, allow_nan=False)
        stream.write("\n")


def load_helper(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise ImportError("Research helper unavailable")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def retention_files() -> list[Path]:
    required = [
        ROOT / "research/apps/欢律.apk", ROOT / "research/apps/无线耳机_17.6.3.apk",
        ROOT / "research/online-whitelist", ROOT / "tools/research/online-whitelist/scripts",
        ROOT / "docs/research/online-whitelist/README.md",
        ROOT / "research/online-artwork/runs", ROOT / "tools/research/online-artwork/scripts",
        ROOT / "research/online-artwork/evidence", ROOT / "docs/research/online-artwork/README.md",
        ROOT / "research/earbud-artwork", ROOT / "tools/research/earbud-artwork/scripts",
        ROOT / "docs/research/earbud-artwork/README.md", EVIDENCE_ARCHIVE / "apk-research",
        EVIDENCE_ARCHIVE / "research-handoff-whitelist", EVIDENCE_ARCHIVE / "cloud-research-acceptance",
        ROOT / "src/HeyMelody.App/Assets/Devices/EncoFree4/model.glb",
        ROOT / "tools/research/accept_cloud_research.py", ROOT / "tools/research/event_dex_audit.py",
        ROOT / "tools/research/cloud-research-requirements.txt",
        ROOT / "docs/research/device-adaptation-handoff.md",
        ROOT / "docs/research/cloud-device-configuration.md", ROOT / "docs/research/cloud-device-artwork.md",
    ]
    files = set()
    for target in required:
        target = target.resolve(strict=True)
        if not target.is_relative_to(ROOT):
            raise ValueError("Retention input escaped repository")
        for path in target.rglob("*") if target.is_dir() else (target,):
            if path.suffix == ".pyc" or "__pycache__" in path.parts or not path.is_file():
                continue
            if not path.resolve().is_relative_to(ROOT):
                raise ValueError("Retention member escaped repository")
            files.add(path)
    return sorted(files, key=lambda item: item.relative_to(ROOT).as_posix())


def inventory() -> list[dict]:
    return [{"path": path.relative_to(ROOT).as_posix(), "bytes": path.stat().st_size,
             "sha256": sha(path)} for path in retention_files()]


def check_counts(report: dict) -> dict:
    rows = []

    def visit(value):
        if isinstance(value, dict):
            for key, item in value.items():
                if key == "checks" and isinstance(item, list):
                    rows.extend(item)
                else:
                    visit(item)
        elif isinstance(value, list):
            for item in value:
                visit(item)

    visit(report)
    return {"total": len(rows), "passed": sum(row.get("passed") is True for row in rows)}


def verifier(out: Path, label: str, script: Path, run: Path) -> dict:
    report_path = out / (label + "-verification.json")
    process = subprocess.run([sys.executable, "-B", "-X", "utf8", str(script), "--run", str(run),
                              "--output", str(report_path)], cwd=ROOT, capture_output=True,
                             encoding="utf-8", errors="replace")
    if process.returncode != 0:
        # Tracebacks/decoder excerpts can contain input bytes. Persist no raw
        # failed-process output, and never put exception messages into reports.
        return {"passed": False, "error_type": "VerifierProcessError", "exit_code": process.returncode}
    (out / (label + ".log")).write_text(process.stdout, encoding="utf-8")
    report = read_json(report_path)
    return {"passed": report.get("passed") is True, "report": report_path.relative_to(ROOT).as_posix(),
            "report_sha256": sha(report_path), "checks": check_counts(report),
            "cryptography_version": report.get("cryptography_version"),
            "exit_code": process.returncode}


def authentication_and_analysis(out: Path) -> dict:
    scripts = ROOT / "tools/research/online-whitelist/scripts"
    fetcher = load_helper("accept_whitelist_fetcher", scripts / "fetch_whitelist.py")
    analyzer = load_helper("accept_whitelist_analyzer", scripts / "analyze_whitelist.py")
    results = read_json(WHITELIST_RUN / "results.json")
    rows = {}
    for package, spec in fetcher.SPECS.items():
        apk = ROOT / "research/apps" / fetcher.legacy.APK_SPEC[package]["filename"]
        material = None
        try:
            with zipfile.ZipFile(apk) as archive:
                material, evidence = fetcher.authenticate_material(archive, spec)
                rows[package] = {"passed": bool(material) and evidence == results["sources"][package]["authentication"],
                                 "apk_sha256": sha(apk), "aes_gcm_tag_verified": evidence.get("aes_gcm_tag_verified") is True,
                                 "historical_authentication_evidence_matches": evidence == results["sources"][package]["authentication"],
                                 "authentication_material_written": False}
        finally:
            material = None
    computed = analyzer.analyze_run(WHITELIST_RUN)
    original = read_json(WHITELIST_RUN / "analysis.json")
    canonical = json.dumps(computed, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")
    report = {"passed": all(row["passed"] for row in rows.values()) and computed == original,
              "sources": rows, "analysis_structure_equals_historical": computed == original,
              "recomputed_analysis_canonical_sha256": hashlib.sha256(canonical).hexdigest(),
              "historical_analysis_sha256": sha(WHITELIST_RUN / "analysis.json"),
              "selected_coverage": computed.get("selected_coverage", {}), "network_requests": 0}
    save(out / "authentication-and-analysis.json", report)
    return report


def containers(out: Path) -> dict:
    inspector = load_helper("accept_model_inspector", ROOT / "tools/research/online-artwork/scripts/inspect_models.py")
    models = {}
    for label, (run_name, package_hash) in MODEL_RUNS.items():
        run = ROOT / "research/online-artwork/runs" / run_name
        source = run / "packages" / package_hash / "extracted/res/raw/detail_model.vfxms"
        details, exports = inspector.inspect_container(source.read_bytes())
        exported = run / "model-inspection" / package_hash / "extracted/res/raw/detail_model"
        rows = [{"file": name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(),
                 "saved_export_byte_identical": (exported / name).read_bytes() == data}
                for name, data in sorted(exports.items())]
        app_matches = (ROOT / "src/HeyMelody.App/Assets/Devices/EncoFree4/model.glb").read_bytes() == exports["model.glb"] if label == "free4" else None
        texture_fields = ("format", "width", "height", "faces", "mipmap_levels")
        models[label] = {"passed": not details["warnings"] and details["unconsumed_tail"]["length"] == 0
                                  and all(row["saved_export_byte_identical"] for row in rows)
                                  and (app_matches is None or app_matches),
                         "source": source.relative_to(ROOT).as_posix(), "source_sha256": sha(source),
                         "container_sections": details["sections"], "glb": details["details"].get("glb", {}),
                         "textures": {name: {key: item[key] for key in texture_fields if key in item}
                                      for name, item in details["details"].items() if name in ("ibl", "skybox")},
                         "exports": rows, "ktx_all_face_and_mipmap_bounds_rechecked": True,
                         "app_free4_glb_byte_identical": app_matches}
    report = {"passed": all(model["passed"] for model in models.values()), "models": models,
              "network_requests": 0, "rendering_attempted": False}
    save(out / "model-container-inspection.json", report)
    return report


def gallery(out: Path) -> dict:
    scripts = ROOT / "tools/research/earbud-artwork/scripts"
    sys.path.insert(0, str(scripts))
    module = load_helper("accept_artwork_gallery", scripts / "merge_and_verify.py")
    report = module.validate(module.load("assets.json")["assets"], module.load("models.json"), True)
    save(out / "earbud-artwork-validation.json", report)
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, help="New directory below research/cloud-acceptance (or historical artifacts); existing paths are rejected")
    args = parser.parse_args()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    out = (args.output_dir or ACCEPTANCE_ROOT / ("research-acceptance-" + stamp)).resolve()
    roots = (ACCEPTANCE_ROOT.resolve(), ARTIFACTS.resolve())
    if not any(out != base and out.is_relative_to(base) for base in roots):
        parser.error("Output directory must remain below research/cloud-acceptance or artifacts")
    if out.exists():
        parser.error("Output directory already exists; choose a new path")
    out.mkdir(parents=True, exist_ok=False)
    summary = {"schema_version": 1, "verified_at": datetime.now(timezone.utc).isoformat(),
               "passed": False, "no_network": True, "network_requests": 0, "adb_operations": 0,
               "authentication_material_written": False, "output": out.relative_to(ROOT).as_posix(),
               "checks": {}, "historical_unchanged": False}
    before = None

    def step(name, action):
        try:
            result = action()
        except Exception as error:
            result = {"passed": False, "error_type": type(error).__name__}
            if isinstance(error, (ImportError, RuntimeError)):
                result["dependency_hint"] = "Use an existing Python environment with cryptography and Pillow installed."
        summary["checks"][name] = result

    try:
        before = inventory()
        manifest = {"schema_version": 1, "generated_at": summary["verified_at"],
                    "file_count": len(before), "total_bytes": sum(row["bytes"] for row in before),
                    "files": before, "payload_or_authentication_material_embedded": False}
        save(out / "retention-manifest.json", manifest)
        summary["retention_manifest_sha256"] = sha(out / "retention-manifest.json")
        summary["retention_file_count"] = len(before)
        runtime = load_helper("accept_research_runtime", ROOT / "tools/research/earbud-artwork/scripts/research_runtime.py")
        runtime.bootstrap("cryptography", "PIL")
        step("whitelist", lambda: verifier(out, "whitelist", ROOT / "tools/research/online-whitelist/scripts/verify_run.py", WHITELIST_RUN))
        for label, (run_name, _) in MODEL_RUNS.items():
            step("artwork_" + label, lambda label=label, run_name=run_name: verifier(
                out, "artwork-" + label, ROOT / "tools/research/online-artwork/scripts/verify_artwork.py",
                ROOT / "research/online-artwork/runs" / run_name))
        step("authentication_and_analysis", lambda: authentication_and_analysis(out))
        step("model_containers", lambda: containers(out))
        step("gallery", lambda: gallery(out))
    except Exception as error:
        summary["error_type"] = type(error).__name__
    finally:
        if before is not None:
            try:
                summary["historical_unchanged"] = inventory() == before
            except Exception as error:
                summary["retention_recheck_error_type"] = type(error).__name__
        summary["passed"] = (before is not None and summary["historical_unchanged"]
                             and len(summary["checks"]) == 6
                             and all(result.get("passed") is True for result in summary["checks"].values())
                             and "error_type" not in summary)
        whitelist = summary["checks"].get("whitelist", {})
        summary["whitelist_check_counts"] = whitelist.get("checks", {"total": 0, "passed": 0})
        artwork = summary["checks"].get("gallery", {})
        summary["asset_count"] = artwork.get("asset_count", 0)
        summary["confirmed_2d_model_count"] = artwork.get("confirmed_model_count", 0)
        summary["runtime"] = {"python": sys.version.split()[0],
                              "cryptography": getattr(sys.modules.get("cryptography"), "__version__", None),
                              "pillow": getattr(sys.modules.get("PIL"), "__version__", None)}
        save(out / "summary.json", summary)
    print(json.dumps({"passed": summary["passed"], "summary": (out / "summary.json").relative_to(ROOT).as_posix(),
                      "whitelist_checks": summary["whitelist_check_counts"], "asset_count": summary["asset_count"],
                      "historical_unchanged": summary["historical_unchanged"],
                      "failed_steps": {name: result.get("error_type", "FailedCheck") for name, result in summary["checks"].items()
                                       if result.get("passed") is not True},
                      "error_type": summary.get("error_type")}, ensure_ascii=False))
    return 0 if summary["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
