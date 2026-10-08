#!/usr/bin/env python3
"""Fetch named vendor appearance packages for research, without Android execution.

Offline by default. --online requests module 14 for explicitly selected products
and colors. Authentication remains in memory; no rendering or app integration.
"""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import argparse
from datetime import datetime, timezone
import hashlib
import hmac
import importlib.util
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import struct
import tempfile
import time
import urllib.request
import uuid
import zipfile

SCRIPTS_ROOT = Path(__file__).resolve().parents[1]
DOCUMENTATION_ROOT = SCRIPTS_ROOT.parent
ROOT = Path(__file__).resolve().parents[4] / "research/online-artwork"
RESEARCH = ROOT.parent
ENDPOINT = "v1/earphone/resource/fetch"
MAX_DOWNLOAD = 64 * 1024 * 1024
MAX_EXPANDED = 256 * 1024 * 1024
MAX_MEMBER = 64 * 1024 * 1024
MAX_MEMBERS = 2048
TARGETS = {
    "x3": {"productId": "067410", "name": "OPPO Enco X3"},
    "free4": {"productId": "068C10", "name": "OPPO Enco Free4"},
    "free4-dyna": {"productId": "06C010", "name": "OPPO Enco Free4（丹拿版）"},
}


def helper(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise ValueError("Research helper unavailable")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


whitelist = helper("artwork_whitelist", DOCUMENTATION_ROOT / "online-whitelist/scripts/fetch_whitelist.py")
legacy = whitelist.legacy


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def checked(path):
    resolved = path.resolve()
    if not resolved.is_relative_to(ROOT):
        raise ValueError("Output escaped research/online-artwork")
    return resolved


def save_bytes(path, raw):
    path = checked(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    staging = checked(ROOT / ".staging")
    staging.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix="write-", dir=staging)
    temporary = checked(Path(name))
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(raw)
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def save_json(path, value):
    save_bytes(path, (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))


def member_path(info):
    name = info.filename
    if not name or info.orig_filename != name or "\\" in name or "\x00" in name:
        raise ValueError("Unsafe ZIP name")
    normalized = name[:-1] if info.is_dir() else name
    components = normalized.split("/")
    if (name.startswith("/") or any(p in ("", ".", "..") for p in components)
            or any(any(ord(c) < 32 or c in '<>:"|?*' for c in p) for p in components)
            or any(p.endswith((".", " ")) for p in components)):
        raise ValueError("Unsafe Windows ZIP path")
    reserved = re.compile(r"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", re.I)
    if any(reserved.match(p) for p in components):
        raise ValueError("Reserved Windows ZIP path")
    kind = stat.S_IFMT(info.external_attr >> 16)
    if kind not in (0, stat.S_IFREG, stat.S_IFDIR) or info.flag_bits & 1:
        raise ValueError("Unsupported ZIP member type or encryption")
    return PurePosixPath(normalized)


def inspect_format(raw, name):
    result = {"extension": PurePosixPath(name).suffix.lower(), "header_hex": raw[:24].hex()}
    if raw.startswith(b"glTF") and len(raw) >= 12:
        _, version, total = struct.unpack_from("<4sII", raw)
        result.update(format="glb", version=version, declared_bytes=total, length_matches=total == len(raw))
    elif raw.startswith(b"\x89PNG\r\n\x1a\n") and len(raw) >= 33:
        width, height = struct.unpack_from(">II", raw, 16)
        result.update(format="png", width=width, height=height)
    elif raw.startswith(b"RIFF") and raw[8:12] == b"WEBP":
        result.update(format="webp")
    elif raw.startswith(b"PK\x03\x04"):
        result.update(format="zip")
    elif raw.startswith(b"\x1f\x8b"):
        result.update(format="gzip")
    elif len(raw) >= 12 and raw[4:8] == b"ftyp":
        result.update(format="iso_bmff", brand=raw[8:12].decode("ascii", "replace"))
    elif result["extension"] in (".json", ".gltf"):
        parsed = json.loads(raw.decode("utf-8-sig"))
        asset = parsed.get("asset") if isinstance(parsed, dict) else None
        result.update(format="gltf" if isinstance(asset, dict) and asset.get("version") else "json",
                      root_type=type(parsed).__name__)
        if isinstance(parsed, dict):
            result["root_keys"] = sorted(parsed)
    else:
        result.update(format="unidentified_binary" if b"\x00" in raw[:512] else "unidentified_content")
    return result


def validate_zip(raw):
    """Validate all names and actual bytes before any member is written."""
    validated = []
    total = 0
    with zipfile.ZipFile(io.BytesIO(raw)) as archive:
        infos = archive.infolist()
        if len(infos) > MAX_MEMBERS:
            raise ValueError("Too many ZIP members")
        paths = {}
        for info in infos:
            path = member_path(info)
            key = str(path).casefold()
            if key in paths:
                raise ValueError("Case-insensitive ZIP collision")
            paths[key] = info.is_dir()
            if info.file_size > MAX_MEMBER or info.file_size < 0:
                raise ValueError("ZIP member exceeds size limit")
            total += info.file_size
            if total > MAX_EXPANDED:
                raise ValueError("ZIP expanded size exceeds limit")
            validated.append((info, path))
        for key in paths:
            for parent in PurePosixPath(key).parents:
                if str(parent) in paths and not paths[str(parent)]:
                    raise ValueError("ZIP file/directory conflict")
        members = []
        for info, path in validated:
            if info.is_dir():
                if info.file_size:
                    raise ValueError("Non-empty ZIP directory")
                continue
            with archive.open(info) as stream:
                data = stream.read(MAX_MEMBER + 1)
            if len(data) > MAX_MEMBER or len(data) != info.file_size:
                raise ValueError("ZIP actual size mismatch")
            members.append((str(path), data, {"path": str(path), "bytes": len(data), "sha256": digest(data),
                                             **inspect_format(data, str(path))}))
    return members, {"zip_member_count": len(infos), "file_count": len(members), "expanded_bytes": total,
                     "all_members_crc_read": True, "members": [m[2] for m in members]}


def fetch_case(opener, material, package, app, target, color, directory, downloaded):
    body_object = {"platform": "android", "channel": legacy.APK_SPEC[package]["channel"],
                   "versionCode": str(app["app_metadata_version_code"]),
                   "productId": target["productId"], "color": str(color), "moduleId": "14"}
    result = {"observed_at": datetime.now(timezone.utc).isoformat(), "package": package,
              "target_name": target["name"], "endpoint": ENDPOINT, "request_body": body_object,
              "color_evidence": "Explicit bounded research query; not a claimed default or marketing color",
              "authentication_headers_persisted": False, "full_download_url_persisted": False}
    body = json.dumps(body_object, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    headers = {"Content-Type": "application/json", "appid": "earphone", "sv": "v1",
               "ts": str(int(time.time() * 1000)), "nonce": str(uuid.uuid4()),
               "sign": hmac.new(material, body, hashlib.sha1).hexdigest()}
    request = urllib.request.Request(whitelist.API_BASE + ENDPOINT, data=body, headers=headers, method="POST")
    try:
        raw, info = whitelist.read_http(opener, request, whitelist.MAX_METADATA_BYTES)
        result["metadata_http"] = info
        if info["http_status"] in (403, 429):
            result.update(status="network_stop", stop_network=True)
            return result
        parsed = whitelist.strict_json(whitelist.http_payload(raw, info, whitelist.MAX_METADATA_BYTES))
        result["business_code"] = parsed.get("code")
        result["business_message"] = legacy.safe_message(parsed.get("msg", parsed.get("message")))
        result["response_redacted"] = legacy.sanitize(parsed)
        save_json(directory / "metadata.redacted.json", result["response_redacted"])
        if info["http_status"] != 200 or parsed.get("code") not in (0, 200):
            result["status"] = "metadata_rejected"
            return result
        records = legacy.response_records(parsed.get("data"))
        result["resource_records_found"] = len(records)
        if not records:
            result["status"] = "no_resource_returned"
            return result
        if len(records) != 1:
            result["status"] = "ambiguous_resource_records"
            return result
        record = records[0]
        if "productId" in record and str(record["productId"]).upper() != target["productId"]:
            result["status"] = "response_product_mismatch"
            return result
        result["attribution"] = "request_and_response_productId" if "productId" in record else "request_only"
        expected = record.get("sha256")
        url = record.get("resourceUrl")
        if not isinstance(url, str) or not legacy.vendor_cdn(url):
            result["status"] = "no_approved_resource_url"
            return result
        if not isinstance(expected, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", expected):
            result["status"] = "no_expected_sha256"
            return result
        expected = expected.lower()
        result["expected_sha256"] = expected
        result["download_url"] = whitelist.public_url_evidence(url)
        if expected in downloaded:
            result.update(status="verified", package_artifact=downloaded[expected], download_reused=True)
            return result
        transport, get_info = whitelist.read_http(opener, urllib.request.Request(url, method="GET"), MAX_DOWNLOAD)
        result["download_http"] = get_info
        if get_info["http_status"] in (403, 429):
            result.update(status="network_stop", stop_network=True)
            return result
        if get_info["http_status"] != 200:
            result["status"] = "download_rejected"
            return result
        payload = whitelist.http_payload(transport, get_info, MAX_DOWNLOAD)
        if not hmac.compare_digest(digest(payload), expected):
            result["status"] = "download_hash_mismatch"
            return result
        members, inventory = validate_zip(payload)
        artifact = checked(directory.parents[1] / "packages" / expected)
        save_bytes(artifact / "download.transport.bin", transport)
        save_bytes(artifact / "resource.zip", payload)
        for path, data, _ in members:
            save_bytes(artifact / "extracted" / path, data)
        inventory.update(sha256=expected, bytes=len(payload), transport_sha256=digest(transport),
                         vendor_metadata_sha256_matches=True, executable_content_run=False)
        save_json(artifact / "inventory.json", inventory)
        relative = artifact.relative_to(ROOT).as_posix()
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
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--online", action="store_true")
    mode.add_argument("--offline", action="store_true")
    parser.add_argument("--packages", nargs="+", choices=list(whitelist.SPECS), default=list(whitelist.SPECS))
    parser.add_argument("--targets", nargs="+", choices=list(TARGETS), default=["x3", "free4"])
    parser.add_argument("--colors", nargs="+", type=int, default=[-1])
    parser.add_argument("--proxy")
    args = parser.parse_args()
    if len(args.colors) > 4 or len(set(args.colors)) != len(args.colors) or any(c < -1 or c > 255 for c in args.colors):
        parser.error("Select at most four distinct explicit colors in -1..255")
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    run = checked(ROOT / "runs" / datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ"))
    report = {"schema_version": 1, "started_at": datetime.now(timezone.utc).isoformat(),
              "online": args.online, "sources": {}, "cases": [], "network_stopped": False,
              "proxy": whitelist.public_url_evidence(args.proxy) if args.proxy else None,
              "limits": {"download_bytes": MAX_DOWNLOAD, "expanded_bytes": MAX_EXPANDED,
                         "member_bytes": MAX_MEMBER, "members": MAX_MEMBERS}}
    opener = whitelist.make_opener(args.proxy)
    downloaded = {}
    for package in args.packages:
        with legacy.verified_apk(package) as archive:
            app = whitelist.manifest_info(archive, package)
            material, authentication = whitelist.authenticate_material(archive, whitelist.SPECS[package])
        report["sources"][package] = {**app, "apk_sha256": legacy.APK_SPEC[package]["sha256"],
                                        "authentication": authentication}
        if args.online and not report["network_stopped"]:
            for key in args.targets:
                for color in args.colors:
                    directory = run / "requests" / f'{package}_{TARGETS[key]["productId"]}_{color}'
                    result = fetch_case(opener, material, package, app, TARGETS[key], color, directory, downloaded)
                    report["cases"].append(result)
                    save_json(run / "results.json", report)
                    print(json.dumps({"package": package, "productId": TARGETS[key]["productId"],
                                      "color": color, "status": result["status"],
                                      "http_status": result.get("metadata_http", {}).get("http_status"),
                                      "business_code": result.get("business_code"),
                                      "resource_bytes": result.get("resource_bytes"),
                                      "error_type": result.get("error_type")}, ensure_ascii=False), flush=True)
                    if result.get("stop_network"):
                        report["network_stopped"] = True
                        break
                if report["network_stopped"]:
                    break
        material = None
    report["finished_at"] = datetime.now(timezone.utc).isoformat()
    report["unique_verified_packages"] = len(downloaded)
    save_json(run / "results.json", report)
    save_json(ROOT / "latest-run.json", {"run": run.relative_to(ROOT).as_posix(), "online": args.online})
    print(json.dumps({"run": run.relative_to(ROOT).as_posix(), "online": args.online,
                      "unique_verified_packages": len(downloaded), "network_stopped": report["network_stopped"]}, ensure_ascii=False))
    return 1 if report["network_stopped"] or any(r["status"] == "error" for r in report["cases"]) else 0


if __name__ == "__main__":
    raise SystemExit(main())
