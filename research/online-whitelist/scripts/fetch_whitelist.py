#!/usr/bin/env python3
"""Reproduce APK whitelist requests without running Android or modifying the app.

Python 3.11+, cryptography from the existing research environment. Authentication
material and local cache keys remain in memory. Network access requires --online.
"""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import argparse
import base64
import gzip
import hashlib
import hmac
import importlib.util
import io
import json
import math
import os
from pathlib import Path
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[1]
RESEARCH = ROOT.parent
ENDPOINT = "v1/earphone/new/latestWhiteList"
API_BASE = "https://iot-earbuds-cn.heytapmobi.com/"
MAX_METADATA_BYTES = 1024 * 1024
MAX_CONFIG_BYTES = 8 * 1024 * 1024
SPECS = {
    "com.heytap.headset": {
        "auth_dex": "classes.dex", "auth_class": "Ll4/a;", "auth_method": "d",
        "builtin_member": "res/30.json", "metadata_version": 17006004,
        "builtin_effective_version": 117006004,
    },
    "com.oplus.melody": {
        "auth_dex": "classes2.dex", "auth_class": "LC7/a;", "auth_method": "c",
        "builtin_member": "res/AQ.json", "metadata_version": 17006002,
        "builtin_effective_version": 17006002,
    },
}


def load_helper(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError("Research helper could not be loaded")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


legacy = load_helper("whitelist_artwork_helpers", RESEARCH / "earbud-artwork/scripts/probe_resources.py")
android = load_helper("whitelist_android_helpers", RESEARCH / "earbud-artwork/scripts/android_resources.py")
AESGCM, pkcs7, Encoding, InvalidTag = legacy.crypto_modules()


def sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def checked(path: Path) -> Path:
    path = path.resolve()
    if not path.is_relative_to(ROOT):
        raise ValueError("Output must remain under research/online-whitelist")
    return path


def save_bytes(path: Path, value: bytes) -> None:
    path = checked(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_bytes(value)
    temporary.replace(path)


def save_json(path: Path, value) -> None:
    save_bytes(path, (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))


def gzip_decode(value: bytes, limit: int = MAX_CONFIG_BYTES) -> bytes:
    with gzip.GzipFile(fileobj=io.BytesIO(value)) as stream:
        decoded = stream.read(limit + 1)
    if len(decoded) > limit:
        raise ValueError("Expanded content exceeds research size limit")
    return decoded


def strict_json(value: bytes):
    def pairs(items):
        result = {}
        for key, item in items:
            if key in result:
                raise ValueError("Duplicate JSON member")
            result[key] = item
        return result

    def constant(_):
        raise ValueError("Non-finite JSON number")

    def finite_float(text):
        number = float(text)
        if not math.isfinite(number):
            raise ValueError("Non-finite JSON number")
        return number

    result = json.loads(value.decode("utf-8-sig"), object_pairs_hook=pairs,
                        parse_constant=constant, parse_float=finite_float)
    if not isinstance(result, dict):
        raise ValueError("Expected JSON object")
    return result


def certificates(archive):
    for member in archive.namelist():
        if member.startswith("META-INF/") and member.endswith((".RSA", ".DSA")):
            for certificate in pkcs7.load_der_pkcs7_certificates(archive.read(member)):
                digest = hashlib.sha256(certificate.public_bytes(Encoding.DER)).digest()
                yield member, digest


def decrypt_certificate_bound(archive, blob: bytes):
    if len(blob) < 28:
        raise ValueError("AES-GCM envelope is too short")
    for certificate_member, digest in certificates(archive):
        try:
            value = AESGCM(digest).decrypt(blob[:12], blob[12:], None)
            return value, {"certificate_member": certificate_member,
                           "aes_gcm_tag_verified": True,
                           "nonce_bytes": 12, "tag_bytes": 16}
        except InvalidTag:
            continue
    raise ValueError("No APK certificate authenticated the AES-GCM envelope")


def authenticate_material(archive, spec):
    dex = legacy.Dex(archive.read(spec["auth_dex"]))
    class_data = dex.classes[spec["auth_class"]]
    for index, _, code in class_data["methods"]:
        if f'->{spec["auth_method"]}()Lkotlin/Pair;' not in dex.methods[index]:
            continue
        for pc, op, raw, _ in dex.insns(code):
            if op not in (0x1a, 0x1b):
                continue
            literal_index = raw[1] if op == 0x1a else raw[1] | (raw[2] << 16)
            literal = dex.strs[literal_index]
            if len(literal) < 60:
                continue
            try:
                blob = base64.b64decode(literal, validate=True)
            except ValueError:
                continue
            try:
                material, evidence = decrypt_certificate_bound(archive, blob)
                material.decode("utf-8")
            except (ValueError, UnicodeDecodeError):
                continue
            evidence.update({"dex": spec["auth_dex"], "dex_sha256": sha256(dex.b),
                             "method": dex.methods[index], "code_offset": hex(code),
                             "literal_instruction_offset": hex(code + 16 + 2 * pc),
                             "material_persisted": False})
            return material, evidence
    raise ValueError("APK request authentication material was not recovered")


def manifest_info(archive, package: str) -> dict:
    _, xml = android.decode_xml(archive.read("AndroidManifest.xml"))
    manifest = ET.fromstring(xml)
    ns = "{http://schemas.android.com/apk/res/android}"
    if manifest.get("package") != package:
        raise ValueError("Manifest package mismatch")
    application = manifest.find("application")
    versions = [x.get(ns + "value") for x in application.findall("meta-data")
                if x.get(ns + "name") == "versionCode"] if application is not None else []
    if len(versions) != 1:
        raise ValueError("Expected one application meta-data.versionCode")
    meta = int(versions[0], 0)
    if meta != SPECS[package]["metadata_version"]:
        raise ValueError("APK application metadata version changed")
    return {"package": package, "manifest_version_name": manifest.get(ns + "versionName"),
            "manifest_version_code": int(manifest.get(ns + "versionCode")),
            "app_metadata_version_code": meta,
            "builtin_effective_content_version": meta + (100000000 if package == "com.heytap.headset" else 0),
            "request_version_source": "application/meta-data[@android:name='versionCode']/@android:value"}


def cache_roundtrip(raw: bytes, directory: Path, prefix: str) -> dict:
    key, nonce = os.urandom(32), os.urandom(12)
    ciphertext = nonce + AESGCM(key).encrypt(nonce, gzip.compress(raw, compresslevel=6, mtime=0), None)
    path = directory / (prefix + ".local-cache.encrypted.gz")
    save_bytes(path, ciphertext)
    stored = path.read_bytes()
    restored = gzip_decode(AESGCM(key).decrypt(stored[:12], stored[12:], None))
    if restored != raw:
        raise ValueError("Local cache roundtrip changed content")
    save_bytes(directory / (prefix + ".cache-decrypted.json"), restored)
    tampered = stored[:-1] + bytes([stored[-1] ^ 1])
    wrong_key = bytes([key[0] ^ 1]) + key[1:]
    rejected = []
    for test_key, test_blob in [(key, tampered), (wrong_key, stored)]:
        try:
            AESGCM(test_key).decrypt(test_blob[:12], test_blob[12:], None)
            rejected.append(False)
        except InvalidTag:
            rejected.append(True)
    if not all(rejected):
        raise ValueError("AES-GCM negative check failed")
    return {"format": "12-byte nonce || AES-256-GCM(gzip(JSON)) || 16-byte tag",
            "file": path.name, "encrypted_sha256": sha256(stored),
            "decrypted_sha256": sha256(restored), "original_sha256": sha256(raw),
            "byte_identical": True, "tamper_rejected": rejected[0],
            "wrong_key_rejected": rejected[1], "key_persisted": False,
            "key_scope": "Generated for this research process, not an Android installation cache key"}


def prepare(package: str, directory: Path):
    spec = SPECS[package]
    with legacy.verified_apk(package) as archive:
        info = manifest_info(archive, package)
        material, auth = authenticate_material(archive, spec)
        encrypted = archive.read(spec["builtin_member"])
        compressed, evidence = decrypt_certificate_bound(archive, encrypted)
        builtin = gzip_decode(compressed)
        strict_json(builtin)
        save_bytes(directory / "builtin.encrypted.bin", encrypted)
        save_bytes(directory / "builtin.json", builtin)
        source = {**info, "apk_file": legacy.APK_SPEC[package]["filename"],
                  "apk_sha256": legacy.APK_SPEC[package]["sha256"],
                  "request_body": {"platform": "android", "channel": legacy.APK_SPEC[package]["channel"],
                                   "versionCode": str(info["app_metadata_version_code"])},
                  "authentication": auth,
                  "builtin": {**evidence, "apk_member": spec["builtin_member"],
                              "encrypted_sha256": sha256(encrypted), "plaintext_sha256": sha256(builtin),
                              "plaintext_bytes": len(builtin), "json_valid": True},
                  "builtin_cache_roundtrip": cache_roundtrip(builtin, directory, "builtin")}
    return material, source


def make_opener(proxy: str | None):
    mapping = {} if proxy is None else {"http": proxy, "https": proxy}
    return urllib.request.build_opener(urllib.request.ProxyHandler(mapping), legacy.NoRedirect())


def read_http(opener, request, limit: int) -> tuple[bytes, dict]:
    legacy.network_pace()
    try:
        response = opener.open(request, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = legacy.bounded_read(response, limit)
        info = {"http_status": response.status, "bytes": len(raw), "sha256": sha256(raw),
                "content_type": response.headers.get("Content-Type"),
                "content_encoding": response.headers.get("Content-Encoding")}
    return raw, info


def http_payload(raw: bytes, info: dict, limit: int) -> bytes:
    encoding = (info.get("content_encoding") or "identity").lower().strip()
    if encoding in ("", "identity"):
        return raw
    if encoding == "gzip":
        return gzip_decode(raw, limit)
    raise ValueError("Unsupported HTTP content encoding")


def approved_download(url: str) -> bool:
    parsed = urllib.parse.urlsplit(url)
    return (parsed.scheme == "https" and parsed.hostname is not None
            and (parsed.hostname == "heytapimage.com" or parsed.hostname.endswith(".heytapimage.com")
                 or parsed.hostname == "iot-earbuds-cn.heytapmobi.com")
            and parsed.port in (None, 443) and not parsed.username and not parsed.password)


def public_url_evidence(url: str) -> dict:
    parsed = urllib.parse.urlsplit(url)
    return {"scheme": parsed.scheme, "host": parsed.hostname,
            "path_sha256": sha256(parsed.path.encode()), "query_present": bool(parsed.query),
            "full_url_persisted": False}


def fetch(package: str, material: bytes, source: dict, directory: Path, opener) -> dict:
    result = {"endpoint": ENDPOINT, "http_method": "POST", "started_at": datetime.now(timezone.utc).isoformat(),
              "authentication_headers_persisted": False, "request_attempted": False}
    body = json.dumps(source["request_body"], ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    headers = {"Content-Type": "application/json", "appid": "earphone", "sv": "v1",
               "ts": str(int(time.time() * 1000)), "nonce": str(uuid.uuid4()),
               "sign": hmac.new(material, body, hashlib.sha1).hexdigest()}
    request = urllib.request.Request(API_BASE + ENDPOINT, data=body, headers=headers, method="POST")
    try:
        result["request_attempted"] = True
        raw, info = read_http(opener, request, MAX_METADATA_BYTES)
        result["metadata_http"] = info
        parsed = strict_json(http_payload(raw, info, MAX_METADATA_BYTES))
        result["metadata_json_valid"] = True
        result["business_code"] = parsed.get("code")
        result["business_message"] = legacy.safe_message(parsed.get("msg", parsed.get("message")))
        result["response_root_keys"] = sorted(parsed)
        if info["http_status"] != 200 or parsed.get("code") not in (0, 200):
            result["status"] = "metadata_rejected"
            return result
        data = parsed.get("data")
        if not isinstance(data, dict):
            result["status"] = "no_whitelist_metadata"
            result["data_type"] = type(data).__name__
            return result
        result["data_keys"] = sorted(data)
        record = {k: data[k] for k in ("fileName", "id", "size", "version")
                  if k in data and isinstance(data[k], (str, int, float, bool, type(None)))}
        url = data.get("downloadUrl")
        if not isinstance(url, str) or not url:
            result["status"] = "no_download_url"
            result["whitelist_metadata"] = record
            return result
        record["download_url"] = public_url_evidence(url)
        record["download_url_approved"] = approved_download(url)
        result["whitelist_metadata"] = record
        save_json(directory / "cloud.metadata.redacted.json", {"code": parsed.get("code"), "data": record})
        if not record["download_url_approved"]:
            result["status"] = "unapproved_download_origin"
            return result
        downloaded, download_info = read_http(opener, urllib.request.Request(url, method="GET"), MAX_CONFIG_BYTES)
        result["download_http"] = download_info
        if download_info["http_status"] != 200:
            result["status"] = "download_rejected"
            return result
        save_bytes(directory / "cloud.download.bin", downloaded)
        payload = http_payload(downloaded, download_info, MAX_CONFIG_BYTES)
        cloud = strict_json(payload)
        effective_list = cloud.get("compatWhiteList")
        if effective_list is None:
            effective_list = cloud.get("whiteList")
        if not isinstance(effective_list, list):
            raise ValueError("Downloaded JSON has no recognized whitelist array")
        result["cloud_format"] = "UTF-8 JSON" if payload == downloaded else "HTTP gzip encoded UTF-8 JSON"
        result["cloud_root_keys"] = sorted(cloud)
        result["cloud_bytes"] = len(payload)
        result["cloud_sha256"] = sha256(payload)
        result["server_expected_hash_present"] = isinstance(data.get("sha256"), str)
        if result["server_expected_hash_present"]:
            expected = data["sha256"]
            result["server_expected_hash_scope"] = "HTTP-decoded file payload"
            result["server_expected_hash_matches"] = hmac.compare_digest(sha256(payload), expected.lower())
            if not result["server_expected_hash_matches"]:
                raise ValueError("Server download hash mismatch")
        advertised = data.get("size")
        if isinstance(advertised, str) and advertised.isascii() and advertised.isdigit():
            advertised = int(advertised)
        if isinstance(advertised, int) and not isinstance(advertised, bool):
            result["advertised_size_bytes"] = advertised
            result["advertised_size_matches_payload"] = advertised == len(payload)
        save_bytes(directory / "cloud.json", payload)
        result["cloud_cache_roundtrip"] = cache_roundtrip(payload, directory, "cloud")
        result["status"] = "verified"
        return result
    except Exception as error:
        # Exception text and Request repr can contain credentials or signed URLs.
        result["status"] = "failed"
        result["error_type"] = type(error).__name__
        return result
    finally:
        headers.clear()
        request = None


def contract() -> dict:
    return {"schema_version": 1, "api_base": API_BASE, "endpoint": ENDPOINT,
            "method": "POST", "request_fields": ["platform", "channel", "versionCode"],
            "request_version_source": "Android application meta-data.versionCode, not manifest versionCode",
            "authentication": {"header_names": ["Content-Type", "appid", "ts", "nonce", "sv", "sign"],
                               "appid": "earphone", "sv": "v1",
                               "sign": "HMAC-SHA1 over exact serialized UTF-8 body, lowercase hex",
                               "material": "AES-GCM decrypted APK literal, key = SHA256(signer certificate DER)",
                               "material_persisted": False},
            "cloud": {"metadata_fields": ["downloadUrl", "fileName", "id", "size", "version"],
                      "expected_payload": "JSON, encrypted only when written to Android private cache",
                      "metadata_version_comparison": "Download when different or cache missing, not strictly greater"},
            "cache": {"format": "nonce12 || AES-GCM(gzip(JSON)) || tag16",
                      "key": "Android creates a local random 32-byte key stored in SharedPreferences",
                      "research_key": "Ephemeral independent key, kept only during validation"},
            "selection": {"startup": "Highest content versionCode; builtin wins equal version",
                          "refresh": "Only adopt when new content versionCode is strictly greater",
                          "compat": "Non-null compatVersionCode/compatWhiteList replace formal fields",
                          "provisional": "Only add IDs missing from selected official content"},
            "corrections_to_previous_research": [
                "Request versionCode is application metadata: H17006004, O17006002",
                "H loader opcode 0xb0 is add-int/2addr: builtin effective version 117006004",
                "O builtin effective version is17006002; package manifest versions remain unchanged"],
            "research_limits": {"metadata_bytes": MAX_METADATA_BYTES, "config_bytes": MAX_CONFIG_BYTES,
                                "redirects": "blocked", "tls": "normal Python certificate verification",
                                "vendor_tls_pinning_reproduced": False,
                                "these_limits_are_official_app_rules": False}}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--online", action="store_true", help="Request one whitelist per chosen App channel")
    mode.add_argument("--offline", action="store_true", help="Only verify APK materials/builtins/local cache")
    parser.add_argument("--packages", nargs="+", choices=tuple(SPECS), default=list(SPECS))
    parser.add_argument("--proxy", help="Explicit HTTP proxy, e.g. http://127.0.0.1:59991")
    args = parser.parse_args()
    if args.proxy:
        proxy = urllib.parse.urlsplit(args.proxy)
        if proxy.scheme != "http" or proxy.username or proxy.password:
            parser.error("Use an HTTP proxy without credentials")
    run = checked(ROOT / "runs" / datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ"))
    run.mkdir(parents=True, exist_ok=False)
    save_json(ROOT / "evidence/static-contract.json", contract())
    report = {"schema_version": 1, "started_at": datetime.now(timezone.utc).isoformat(),
              "run": run.relative_to(ROOT).as_posix(), "online": args.online,
              "proxy_used": bool(args.proxy), "python_version": sys.version.split()[0],
              "no_android_execution": True, "no_account_or_phone_identifiers": True, "sources": {}}
    opener = make_opener(args.proxy)
    stopped = False
    for package in args.packages:
        directory = run / package
        try:
            material, source = prepare(package, directory)
            report["sources"][package] = source
            if args.online and not stopped:
                source["online"] = fetch(package, material, source, directory, opener)
                statuses = [source["online"].get(k, {}).get("http_status") for k in ("metadata_http", "download_http")]
                if any(code in (403, 429) for code in statuses):
                    stopped = True
                    report["stop_reason"] = "HTTP 403 or 429; no further network requests"
            elif args.online:
                source["online"] = {"status": "skipped_after_stop", "request_attempted": False}
            material = None
        except Exception as error:
            report["sources"][package] = {"error_type": type(error).__name__, "status": "offline_validation_failed"}
        save_json(run / "results.json", report)
    report["finished_at"] = datetime.now(timezone.utc).isoformat()
    save_json(run / "results.json", report)
    save_json(ROOT / "latest-run.json", {"run": report["run"]})
    summaries = {package: {"builtin_authenticated": s.get("builtin", {}).get("aes_gcm_tag_verified", False),
                           "material_authenticated": s.get("authentication", {}).get("aes_gcm_tag_verified", False),
                           "request_body": s.get("request_body"),
                           "online_status": s.get("online", {}).get("status", "not_requested"),
                           "http_status": s.get("online", {}).get("metadata_http", {}).get("http_status"),
                           "error_type": s.get("error_type", s.get("online", {}).get("error_type"))}
                 for package, s in report["sources"].items()}
    print(json.dumps({"run": str(run), "sources": summaries}, ensure_ascii=False, indent=2))
    ok = all(s.get("builtin", {}).get("aes_gcm_tag_verified") and
             (not args.online or s.get("online", {}).get("status") == "verified")
             for s in report["sources"].values())
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
