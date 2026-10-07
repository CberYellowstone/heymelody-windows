#!/usr/bin/env python3
"""Independently verify a saved whitelist run; never issue network requests.

By default RUN/verification.json is written; --output preserves that report.
Ephemeral acquisition cache keys were not
retained, so saved cache ciphertext is checked against its recorded hash, not
claimed to have been decrypted again. Fresh cache crypto tests run in memory.
"""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import argparse
import base64
import gzip
import hashlib
import importlib.util
import io
import json
import math
import os
from pathlib import Path
import re
import zipfile
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[1]
RESEARCH = ROOT.parent
MAX_CONFIG_BYTES = 8 * 1024 * 1024
MAX_REPORT_BYTES = 64 * 1024 * 1024
SPECS = {
    "com.heytap.headset": {
        "apk": "欢律.apk",
        "sha256": "eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4",
        "member": "res/30.json", "metadata_version": 17006004,
        "builtin_version": 117006004, "channel": "2",
    },
    "com.oplus.melody": {
        "apk": "无线耳机_17.6.3.apk",
        "sha256": "c1149df9e24ed85bf84f7c81c712cee794299d2b95057c6e221fb547f2717059",
        "member": "res/AQ.json", "metadata_version": 17006002,
        "builtin_version": 17006002, "channel": "1",
    },
}


def load_helper(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise ValueError("Helper unavailable")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


runtime = load_helper("verify_runtime", RESEARCH / "earbud-artwork/scripts/research_runtime.py")
runtime.bootstrap("cryptography")
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.serialization import pkcs7, Encoding
from cryptography.exceptions import InvalidTag


def sha256(raw):
    return hashlib.sha256(raw).hexdigest()


def run_path(value):
    candidate = Path(value)
    if not candidate.is_absolute() and candidate.parts and candidate.parts[0] == "runs":
        candidate = ROOT / candidate
    candidate = candidate.resolve()
    if not candidate.is_relative_to(ROOT / "runs") or candidate == ROOT / "runs":
        raise ValueError("Run must be under research/online-whitelist/runs")
    if not candidate.is_dir():
        raise ValueError("Run does not exist")
    return candidate


def saved_path(run, relative):
    path = (run / relative).resolve()
    if not path.is_relative_to(run):
        raise ValueError("Saved run path escaped its directory")
    return path


def read_saved(run, relative, limit=MAX_CONFIG_BYTES):
    path = saved_path(run, relative)
    if path.stat().st_size > limit:
        raise ValueError("Saved file exceeds verification size limit")
    raw = path.read_bytes()
    if len(raw) > limit:
        raise ValueError("Saved file exceeds verification size limit")
    return raw


def strict_json(raw):
    def pairs(items):
        value = {}
        for key, item in items:
            if key in value:
                raise ValueError("Duplicate JSON member")
            value[key] = item
        return value

    def reject(_):
        raise ValueError("Non-finite JSON number")

    def finite(value):
        number = float(value)
        if not math.isfinite(number):
            raise ValueError("Non-finite JSON number")
        return number

    value = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=pairs,
                       parse_constant=reject, parse_float=finite)
    if not isinstance(value, dict):
        raise ValueError("Expected JSON object")
    return value


def gzip_decode(raw, limit=MAX_CONFIG_BYTES):
    with gzip.GzipFile(fileobj=io.BytesIO(raw)) as stream:
        expanded = stream.read(limit + 1)
    if len(expanded) > limit:
        raise ValueError("Expanded content exceeds verification size limit")
    return expanded


def http_payload(raw, info):
    encoding = (info.get("content_encoding") or "identity").strip().lower()
    if encoding in ("", "identity"):
        return raw
    if encoding == "gzip":
        return gzip_decode(raw)
    raise ValueError("Unsupported captured HTTP content encoding")


def effective(root, name, compat):
    return root[compat] if root.get(compat) is not None else root.get(name)


def integer(value):
    return value if isinstance(value, int) and not isinstance(value, bool) and value >= 0 else None


def decimal_size(value):
    if isinstance(value, str) and re.fullmatch(r"[0-9]+", value):
        return int(value)
    return integer(value)


class Checks:
    def __init__(self):
        self.rows = []

    def add(self, name, condition, **evidence):
        self.rows.append({"name": name, "passed": bool(condition), **evidence})

    @property
    def passed(self):
        return all(row["passed"] for row in self.rows)


def decrypt_builtin(archive, encrypted):
    if len(encrypted) < 28:
        raise ValueError("AES-GCM envelope is too short")
    for member in archive.namelist():
        if not member.startswith("META-INF/") or not member.endswith((".RSA", ".DSA")):
            continue
        for certificate in pkcs7.load_der_pkcs7_certificates(archive.read(member)):
            key = hashlib.sha256(certificate.public_bytes(Encoding.DER)).digest()
            try:
                compressed = AESGCM(key).decrypt(encrypted[:12], encrypted[12:], None)
                return gzip_decode(compressed), member
            except InvalidTag:
                continue
    raise ValueError("No APK certificate authenticated builtin content")


def memory_cache_tests(raw):
    key, nonce = os.urandom(32), os.urandom(12)
    cipher = nonce + AESGCM(key).encrypt(nonce, gzip.compress(raw, mtime=0), None)
    restored = gzip_decode(AESGCM(key).decrypt(cipher[:12], cipher[12:], None))
    negative = []
    tampered = cipher[:-1] + bytes([cipher[-1] ^ 1])
    wrong_key = bytes([key[0] ^ 1]) + key[1:]
    for test_key, test_cipher in ((key, tampered), (wrong_key, cipher)):
        try:
            AESGCM(test_key).decrypt(test_cipher[:12], test_cipher[12:], None)
            negative.append(False)
        except InvalidTag:
            negative.append(True)
    return {"byte_identical": restored == raw, "tamper_rejected": negative[0],
            "wrong_key_rejected": negative[1], "key_persisted": False,
            "scope": "Independent in-memory test with a new ephemeral key"}


def verify_cache(run, package, prefix, original, captured, checks):
    base = f"{package}/{prefix}"
    encrypted = read_saved(run, base + ".local-cache.encrypted.gz")
    decrypted = read_saved(run, base + ".cache-decrypted.json")
    digest = sha256(original)
    checks.add(prefix + ".saved_cache_ciphertext_hash", sha256(encrypted) == captured.get("encrypted_sha256"),
               sha256=sha256(encrypted))
    checks.add(prefix + ".saved_cache_plaintext", decrypted == original and
               sha256(decrypted) == captured.get("decrypted_sha256") == captured.get("original_sha256"),
               sha256=digest, bytes=len(original))
    checks.add(prefix + ".captured_same_process_cache_result",
               all(captured.get(k) is True for k in ("byte_identical", "tamper_rejected", "wrong_key_rejected"))
               and captured.get("key_persisted") is False,
               assurance="Checks the captured flags; does not repeat saved-ciphertext decryption")
    tested = memory_cache_tests(original)
    checks.add(prefix + ".independent_memory_cache_crypto",
               all(tested[k] for k in ("byte_identical", "tamper_rejected", "wrong_key_rejected")), **tested)
    return {"saved_ciphertext_decrypted_again": False,
            "reason": "Acquisition used an ephemeral key which was intentionally not retained",
            "saved_ciphertext_hash_checked": True, "saved_decrypted_file_compared": True,
            "new_in_memory_roundtrip_tested": True}


def semantic_tests(analyzer, fetcher, run):
    checks = Checks()
    ordinary = [{"id": "000001"}]
    fixtures = [
        ("compat_empty_array_and_zero", {"whiteList": ordinary, "compatWhiteList": [],
                                        "versionCode": 9, "compatVersionCode": 0}, [], 0),
        ("compat_null_falls_back", {"whiteList": ordinary, "compatWhiteList": None,
                                   "versionCode": 9, "compatVersionCode": None}, ordinary, 9),
        ("compat_overrides_null_formal", {"whiteList": None, "compatWhiteList": [],
                                         "versionCode": None, "compatVersionCode": 0}, [], 0),
    ]
    for name, root, rows, version in fixtures:
        transformed, _ = analyzer.compat_content(root, "com.heytap.headset")
        checks.add(name, transformed.get("whiteList") == rows and transformed.get("versionCode") == version)

    # Exercise the analyzer's actual selection code using in-memory replacements
    # for reads from already-existing run paths. No synthetic files are written.
    original_read = analyzer.read_json
    try:
        cases = [("older", 99, None, "builtin"), ("equal", 100, None, "builtin"),
                 ("greater", 101, None, "cloud"), ("compat_zero", 101, 0, "builtin"),
                 ("invalid_boolean", True, None, "builtin")]
        for name, version, compat, expected in cases:
            def fake_read(path):
                if path.name == "results.json":
                    value = {"sources": {p: {"app_metadata_version_code": 1,
                                              "builtin_effective_content_version": 100} for p in SPECS}}
                else:
                    value = {"whiteList": [], "versionCode": version if path.name == "cloud.json" else 0}
                    if path.name == "cloud.json" and compat is not None:
                        value["compatVersionCode"] = compat
                return value, "memory-only-fixture"
            analyzer.read_json = fake_read
            result = analyzer.analyze_run(run)
            checks.add("selection_" + name,
                       all(result["sources"][p]["selection"]["selected"] == expected for p in SPECS))
    finally:
        analyzer.read_json = original_read

    exact = b"x" * MAX_CONFIG_BYTES
    oversized = gzip.compress(exact + b"x", mtime=0)
    checks.add("gzip_accepts_exact_8MiB", len(gzip_decode(gzip.compress(exact, mtime=0))) == MAX_CONFIG_BYTES)
    for name, decoder in (("independent", gzip_decode), ("fetcher", fetcher.gzip_decode)):
        rejected = False
        try:
            decoder(oversized)
        except ValueError:
            rejected = True
        checks.add(name + "_gzip_rejects_expansion_over_8MiB", rejected)
    checks.add("fetcher_config_limit_is_8MiB", fetcher.MAX_CONFIG_BYTES == MAX_CONFIG_BYTES)
    return checks


def verify_package(run, package, source, analysis, fetcher):
    checks = Checks()
    spec = SPECS[package]
    apk_raw = (RESEARCH / spec["apk"]).read_bytes()
    apk_digest = sha256(apk_raw)
    checks.add("apk_current_hash_unchanged", apk_digest == spec["sha256"] == source.get("apk_sha256"), sha256=apk_digest)
    if not checks.passed:
        return {"passed": False, "checks": checks.rows}
    with zipfile.ZipFile(io.BytesIO(apk_raw)) as archive:
        info = fetcher.manifest_info(archive, package)
        checks.add("apk_metadata_request_and_builtin_versions",
                   info["app_metadata_version_code"] == source.get("app_metadata_version_code") == spec["metadata_version"]
                   and info["builtin_effective_content_version"] == source.get("builtin_effective_content_version") == spec["builtin_version"]
                   and source.get("request_body") == {"platform": "android", "channel": spec["channel"],
                                                      "versionCode": str(spec["metadata_version"])})
        authentication = source.get("authentication", {})
        checks.add("authentication_dex_hash", sha256(archive.read(authentication["dex"])) == authentication.get("dex_sha256"))
        encrypted = archive.read(spec["member"])
        builtin, certificate_member = decrypt_builtin(archive, encrypted)
    saved_builtin = read_saved(run, f"{package}/builtin.json")
    captured_builtin = source["builtin"]
    checks.add("builtin_certificate_authenticated_byte_identity", builtin == saved_builtin and
               certificate_member == captured_builtin.get("certificate_member") and
               sha256(builtin) == captured_builtin.get("plaintext_sha256") and
               len(builtin) == captured_builtin.get("plaintext_bytes"), sha256=sha256(builtin), bytes=len(builtin))
    checks.add("builtin_encrypted_byte_identity", encrypted == read_saved(run, f"{package}/builtin.encrypted.bin") and
               sha256(encrypted) == captured_builtin.get("encrypted_sha256"), sha256=sha256(encrypted))
    strict_json(builtin)
    cache = {"builtin": verify_cache(run, package, "builtin", builtin, source["builtin_cache_roundtrip"], checks)}

    online = source["online"]
    raw_cloud = read_saved(run, f"{package}/cloud.download.bin")
    payload = http_payload(raw_cloud, online["download_http"])
    cloud_file = read_saved(run, f"{package}/cloud.json")
    cloud = strict_json(payload)
    checks.add("cloud_transport_hash_and_size", sha256(raw_cloud) == online["download_http"].get("sha256") and
               len(raw_cloud) == online["download_http"].get("bytes"), sha256=sha256(raw_cloud), bytes=len(raw_cloud))
    checks.add("cloud_payload_byte_identity_hash_and_size", payload == cloud_file and
               sha256(payload) == online.get("cloud_sha256") and len(payload) == online.get("cloud_bytes"),
               sha256=sha256(payload), bytes=len(payload), http_content_encoding=online["download_http"].get("content_encoding"))
    metadata = strict_json(read_saved(run, f"{package}/cloud.metadata.redacted.json"))
    checks.add("saved_redacted_metadata_matches_result", metadata.get("data") == online.get("whitelist_metadata"))
    size = decimal_size(metadata.get("data", {}).get("size"))
    checks.add("metadata_decimal_size_matches_payload", size is not None and size == len(payload),
               advertised_bytes=size, payload_bytes=len(payload))
    checks.add("effective_cloud_whitelist_is_array", isinstance(effective(cloud, "whiteList", "compatWhiteList"), list))
    cache["cloud"] = verify_cache(run, package, "cloud", payload, online["cloud_cache_roundtrip"], checks)

    selected_version = effective(cloud, "versionCode", "compatVersionCode")
    version = integer(0 if selected_version is None else selected_version)
    selected = "cloud" if version is not None and version > spec["builtin_version"] else "builtin"
    captured_analysis = analysis["sources"][package]
    selection = captured_analysis["selection"]
    checks.add("analysis_strict_cloud_version_selection", selection.get("selected") == selected and
               selection.get("cloud_content_version") == version and
               selection.get("builtin_effective_content_version") == spec["builtin_version"],
               selected=selected, cloud_content_version=version, builtin_effective_content_version=spec["builtin_version"])
    checks.add("analysis_original_file_hashes", captured_analysis["builtin"].get("sha256") == sha256(builtin) and
               captured_analysis["cloud"].get("sha256") == sha256(payload))
    return {"passed": checks.passed, "checks": checks.rows, "cache_assurance": cache,
            "limitations": ["Metadata HTTP body was not retained; its recorded transport hash cannot be independently recomputed",
                            "No server expected SHA256 was returned; cloud verification establishes saved-byte consistency, not a new server signature verification"]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", required=True)
    parser.add_argument("--output", type=Path, help="New report below repository artifacts; refuses an existing file")
    args = parser.parse_args()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    run = run_path(args.run)
    output = saved_path(run, "verification.json")
    if args.output is not None:
        output = args.output.resolve()
        artifacts = (ROOT.parents[1] / "artifacts").resolve()
        if output == artifacts or not output.is_relative_to(artifacts):
            parser.error("Output must be a file below repository artifacts")
        if output.exists():
            parser.error("Output already exists; choose a new report path")
    results_raw = read_saved(run, "results.json", MAX_REPORT_BYTES)
    analysis_raw = read_saved(run, "analysis.json", MAX_REPORT_BYTES)
    results, analysis = strict_json(results_raw), strict_json(analysis_raw)
    fetcher = load_helper("verify_fetcher", ROOT / "scripts/fetch_whitelist.py")
    analyzer = load_helper("verify_analyzer", ROOT / "scripts/analyze_whitelist.py")
    report = {"schema_version": 1, "verified_at": datetime.now(timezone.utc).isoformat(),
              "run": run.relative_to(ROOT).as_posix(), "network_requested": False,
              "keys_or_authentication_headers_written": False, "python_version": sys.version.split()[0],
              "cryptography_version": getattr(sys.modules.get("cryptography"), "__version__", None),
              "input_hashes": {"results.json": sha256(results_raw), "analysis.json": sha256(analysis_raw)},
              "sources": {}}
    for package in SPECS:
        try:
            report["sources"][package] = verify_package(run, package, results["sources"][package], analysis, fetcher)
        except Exception as error:
            report["sources"][package] = {"passed": False, "error_type": type(error).__name__}
    try:
        semantics = semantic_tests(analyzer, fetcher, run)
        report["semantic_boundary_checks"] = {"passed": semantics.passed, "checks": semantics.rows}
    except Exception as error:
        report["semantic_boundary_checks"] = {"passed": False, "error_type": type(error).__name__}
    report["passed"] = all(source["passed"] for source in report["sources"].values()) and report["semantic_boundary_checks"]["passed"]
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("xb" if args.output is not None else "wb") as stream:
        stream.write((json.dumps(report, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))
    counts = {package: {"passed": source["passed"], "checks": len(source.get("checks", [])),
                        "failed_checks": [row["name"] for row in source.get("checks", []) if not row["passed"]],
                        "error_type": source.get("error_type")} for package, source in report["sources"].items()}
    display_root = ROOT.parents[1] if args.output is not None else ROOT
    print(json.dumps({"passed": report["passed"], "verification": output.relative_to(display_root).as_posix(),
                      "sources": counts, "semantic_checks_passed": report["semantic_boundary_checks"]["passed"]}, ensure_ascii=False))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
