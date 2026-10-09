#!/usr/bin/env python3
"""Bounded X4 artwork research: verified original APKs, no Android execution.

Offline by default. --online queries only 06EC10 colors 0/1 and control/fetch14
for the two known sources. Writes a new evidence directory; never updates the
historical latest-run pointer. Credentials and full download URLs stay in memory.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import hmac
import importlib.util
import json
from pathlib import Path
import re
import sys
import time
import urllib.request
import uuid

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("x4_artwork_helpers", HERE / "fetch_artwork.py")
artwork = importlib.util.module_from_spec(spec)
spec.loader.exec_module(artwork)
whitelist, legacy = artwork.whitelist, artwork.legacy


def request_case(opener, material, package, app, color, module, run, downloaded):
    endpoint = "v1/earphone/resource/" + ("fetch" if module else "control")
    body = {"platform": "android", "channel": legacy.APK_SPEC[package]["channel"],
            "versionCode": str(app["app_metadata_version_code"]), "productId": "06EC10", "color": str(color)}
    if module:
        body["moduleId"] = "14"
    result = {"package": package, "endpoint": endpoint, "request_body": body,
              "observed_at": datetime.now(timezone.utc).isoformat(),
              "color_evidence": "Bounded explicit query, not a default or marketing-color assertion",
              "authentication_headers_persisted": False, "full_download_url_persisted": False}
    request_dir = run / "requests" / f'{package}_{color}_{"fetch14" if module else "control"}'
    encoded = json.dumps(body, separators=(",", ":")).encode()
    headers = {"Content-Type": "application/json", "appid": "earphone", "sv": "v1",
               "ts": str(int(time.time() * 1000)), "nonce": str(uuid.uuid4()),
               "sign": hmac.new(material, encoded, hashlib.sha1).hexdigest()}
    try:
        raw, info = whitelist.read_http(opener, urllib.request.Request(whitelist.API_BASE + endpoint,
            data=encoded, headers=headers, method="POST"), whitelist.MAX_METADATA_BYTES)
        result["metadata_http"] = info
        if info["http_status"] in (403, 429):
            result.update(status="network_stop", stop_network=True)
            return result
        parsed = whitelist.strict_json(whitelist.http_payload(raw, info, whitelist.MAX_METADATA_BYTES))
        result["business_code"] = parsed.get("code")
        result["response_redacted"] = legacy.sanitize(parsed)
        artwork.save_json(request_dir / "metadata.redacted.json", result["response_redacted"])
        if info["http_status"] != 200 or parsed.get("code") not in (0, 200):
            result["status"] = "metadata_rejected"
            return result
        records = legacy.response_records(parsed.get("data"))
        if not records:
            result["status"] = "no_resource_returned"
            return result
        if len(records) != 1:
            result["status"] = "ambiguous_resource_records"
            return result
        record = records[0]
        if "productId" in record and str(record["productId"]).upper() != "06EC10":
            result["status"] = "response_product_mismatch"
            return result
        result["attribution"] = "request_and_response_productId" if "productId" in record else "request_only"
        expected, url = record.get("sha256"), record.get("resourceUrl" if module else "cartoonUrl")
        if not isinstance(expected, str) or not re.fullmatch(r"[a-fA-F0-9]{64}", expected):
            result["status"] = "no_expected_sha256"
            return result
        if not isinstance(url, str) or not legacy.vendor_cdn(url):
            result["status"] = "no_approved_resource_url"
            return result
        expected = expected.lower()
        result["expected_sha256"] = expected
        result["download_url"] = whitelist.public_url_evidence(url)
        if expected in downloaded:
            result.update(status="verified", package_artifact=downloaded[expected], download_reused=True)
            return result
        transport, get_info = whitelist.read_http(opener, urllib.request.Request(url, method="GET"), artwork.MAX_DOWNLOAD)
        result["download_http"] = get_info
        if get_info["http_status"] in (403, 429):
            result.update(status="network_stop", stop_network=True)
            return result
        if get_info["http_status"] != 200:
            result["status"] = "download_rejected"
            return result
        payload = whitelist.http_payload(transport, get_info, artwork.MAX_DOWNLOAD)
        if not hmac.compare_digest(artwork.digest(payload), expected):
            result["status"] = "download_hash_mismatch"
            return result
        members, inventory = artwork.validate_zip(payload)
        artifact = run / "packages" / expected
        artwork.save_bytes(artifact / "resource.zip", payload)
        artwork.save_bytes(artifact / "download.transport.bin", transport)
        for path, member, _ in members:
            artwork.save_bytes(artifact / "extracted" / path, member)
        inventory.update(sha256=expected, bytes=len(payload), vendor_metadata_sha256_matches=True)
        artwork.save_json(artifact / "inventory.json", inventory)
        relative = artifact.relative_to(artwork.ROOT).as_posix()
        downloaded[expected] = relative
        result.update(status="verified", package_artifact=relative, download_reused=False,
                      resource_bytes=len(payload), file_count=len(members))
    except Exception as error:
        result.update(status="error", error_type=type(error).__name__)
    finally:
        headers.clear()
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--online", action="store_true")
    parser.add_argument("--proxy")
    args = parser.parse_args()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    run = artwork.ROOT / "x4-artwork-audit" / datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    run.mkdir(parents=True, exist_ok=False)
    report = {"schema_version": 1, "sources": {}, "cases": [], "online": args.online,
              "network_stopped": False, "proxy": whitelist.public_url_evidence(args.proxy) if args.proxy else None}
    opener, downloaded = whitelist.make_opener(args.proxy), {}
    for package in whitelist.SPECS:
        with legacy.verified_apk(package) as archive:
            app = whitelist.manifest_info(archive, package)
            material, auth = whitelist.authenticate_material(archive, whitelist.SPECS[package])
        report["sources"][package] = {**app, "authentication": auth,
                                     "apk_sha256": legacy.APK_SPEC[package]["sha256"]}
        if args.online and not report["network_stopped"]:
            for color in (0, 1):
                for module in (False, True):
                    case = request_case(opener, material, package, app, color, module, run, downloaded)
                    report["cases"].append(case)
                    artwork.save_json(run / "results.json", report)
                    print(json.dumps({key: case[key] for key in ("package", "endpoint", "request_body", "status")}, ensure_ascii=False), flush=True)
                    if case.get("stop_network"):
                        report["network_stopped"] = True
                        break
                if report["network_stopped"]:
                    break
        material = None
    report["unique_verified_packages"] = len(downloaded)
    artwork.save_json(run / "results.json", report)
    print(json.dumps({"run": str(run.relative_to(artwork.ROOT.parent.parent)), "unique_verified_packages": len(downloaded)}, ensure_ascii=False))
    return int(report["network_stopped"] or any(case["status"] == "error" for case in report["cases"]))


if __name__ == "__main__":
    raise SystemExit(main())
