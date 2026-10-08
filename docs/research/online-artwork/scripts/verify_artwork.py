#!/usr/bin/env python3
"""Verify saved appearance packages offline; --output preserves RUN/verification.json."""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import argparse
from datetime import datetime, timezone
import gzip
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import stat
import struct
import zipfile

DOCUMENTATION_ROOT = Path(__file__).resolve().parents[2]
ROOT = Path(__file__).resolve().parents[4] / "research/online-artwork"
sys.path.insert(0, str(DOCUMENTATION_ROOT / "earbud-artwork/scripts"))
from research_runtime import research_path
MAX_DOWNLOAD = 64 * 1024 * 1024
MAX_MEMBER = 64 * 1024 * 1024
MAX_EXPANDED = 256 * 1024 * 1024
MAX_MEMBERS = 2048
SOURCES = {
    "com.heytap.headset": ("欢律.apk", "eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4", "2", "17006004"),
    "com.oplus.melody": ("无线耳机_17.6.3.apk", "c1149df9e24ed85bf84f7c81c712cee794299d2b95057c6e221fb547f2717059", "1", "17006002"),
}
TARGETS = {"067410": "OPPO Enco X3", "068C10": "OPPO Enco Free4", "06C010": "OPPO Enco Free4（丹拿版）"}


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def scoped(base, relative):
    path = research_path(relative, base)
    if not path.is_relative_to(base):
        raise ValueError("Path escaped its verification scope")
    return path


def read(base, relative, limit=MAX_DOWNLOAD):
    path = scoped(base, relative)
    if path.stat().st_size > limit:
        raise ValueError("Verification input exceeds limit")
    raw = path.read_bytes()
    if len(raw) > limit:
        raise ValueError("Verification input exceeds limit")
    return raw


def json_value(raw):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("Duplicate JSON member")
            result[key] = value
        return result
    def reject(_):
        raise ValueError("Non-finite JSON number")
    return json.loads(raw.decode("utf-8-sig"), object_pairs_hook=pairs, parse_constant=reject)


def http_payload(raw, info):
    encoding = (info.get("content_encoding") or "identity").strip().lower()
    if encoding in ("", "identity"):
        return raw
    if encoding != "gzip":
        raise ValueError("Unsupported captured HTTP encoding")
    with gzip.GzipFile(fileobj=io.BytesIO(raw)) as stream:
        payload = stream.read(MAX_DOWNLOAD + 1)
    if len(payload) > MAX_DOWNLOAD:
        raise ValueError("HTTP gzip expansion exceeds limit")
    return payload


def safe_member(info):
    # ZipInfo.filename silently truncates at NUL; inspect the original too.
    name = info.orig_filename
    if name != info.filename or not name or "\\" in name or "\x00" in name:
        raise ValueError("Unsafe original ZIP name")
    parts = (name[:-1] if info.is_dir() else name).split("/")
    reserved = re.compile(r"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", re.I)
    for part in parts:
        if (part in ("", ".", "..") or part.endswith((".", " ")) or reserved.match(part)
                or any(ord(c) < 32 or c in '<>:"|?*' for c in part)):
            raise ValueError("Unsafe Windows ZIP path")
    kind = stat.S_IFMT(info.external_attr >> 16)
    if kind not in (0, stat.S_IFREG, stat.S_IFDIR) or info.flag_bits & 1:
        raise ValueError("Unsupported ZIP member kind or encryption")
    return "/".join(parts)


def zip_members(raw):
    with zipfile.ZipFile(io.BytesIO(raw)) as archive:
        infos = archive.infolist()
        if len(infos) > MAX_MEMBERS:
            raise ValueError("Too many ZIP members")
        paths, total = {}, 0
        for info in infos:
            name = safe_member(info)
            key = name.casefold()
            if key in paths:
                raise ValueError("Case-insensitive ZIP collision")
            paths[key] = info.is_dir()
            total += info.file_size
            if not 0 <= info.file_size <= MAX_MEMBER or total > MAX_EXPANDED:
                raise ValueError("ZIP size limit exceeded")
            if info.is_dir() and info.file_size:
                raise ValueError("Nonempty ZIP directory")
        for name in paths:
            for parent in PurePosixPath(name).parents:
                if str(parent) in paths and not paths[str(parent)]:
                    raise ValueError("ZIP file/directory conflict")
        files = {}
        for info in infos:
            if info.is_dir():
                continue
            with archive.open(info) as stream:
                data = stream.read(MAX_MEMBER + 1)
            if len(data) != info.file_size or len(data) > MAX_MEMBER:
                raise ValueError("Actual ZIP size differs from its declaration")
            files[safe_member(info)] = data
    return files, len(infos), total


def glb_info(raw):
    if len(raw) < 12 or struct.unpack_from("<4sII", raw) != (b"glTF", 2, len(raw)):
        raise ValueError("Invalid GLB header")
    offset, chunks = 12, []
    while offset < len(raw):
        if offset + 8 > len(raw):
            raise ValueError("Truncated GLB chunk header")
        length, kind = struct.unpack_from("<II", raw, offset)
        end = offset + 8 + length
        if length % 4 or end > len(raw):
            raise ValueError("Invalid GLB chunk bound/alignment")
        data = raw[offset + 8:end]
        chunks.append({"type": hex(kind), "bytes": length, "sha256": digest(data)})
        if len(chunks) == 1:
            if kind != 0x4E4F534A:
                raise ValueError("GLB first chunk is not JSON")
            scene = json_value(data)
            if not isinstance(scene, dict) or scene.get("asset", {}).get("version") != "2.0":
                raise ValueError("Invalid glTF asset version")
        offset = end
    if not chunks:
        raise ValueError("GLB contains no chunks")
    return {"format": "GLB2", "bytes": len(raw), "sha256": digest(raw), "chunks": chunks}


def texture_info(raw):
    ktx1 = b"\xabKTX 11\xbb\r\n\x1a\n"
    ktx2 = b"\xabKTX 20\xbb\r\n\x1a\n"
    if raw.startswith(ktx1):
        if len(raw) < 64:
            raise ValueError("Truncated KTX1 header")
        marker = struct.unpack_from("<I", raw, 12)[0]
        order = "<" if marker == 0x04030201 else ">" if marker == 0x01020304 else None
        if order is None:
            raise ValueError("Invalid KTX1 endianness")
        fields = struct.unpack_from(order + "13I", raw, 12)
        if not fields[6] or fields[10] not in (1, 6) or 64 + fields[12] > len(raw):
            raise ValueError("Invalid KTX1 dimensions/metadata bound")
        return {"format": "KTX1", "bytes": len(raw), "sha256": digest(raw),
                "width": fields[6], "height": fields[7], "faces": fields[10], "mip_levels": fields[11]}
    if raw.startswith(ktx2) and len(raw) >= 80:
        return {"format": "KTX2", "bytes": len(raw), "sha256": digest(raw), "validation": "Magic and minimum header only"}
    raise ValueError("Texture block is not a recognized KTX container")


def vfxms_info(raw):
    if len(raw) < 40:
        raise ValueError("Truncated VFXMS header")
    head = struct.unpack_from(">10i", raw)
    names = ("config", "glb", "ibl", "skybox", "animation")
    offset, blocks = 40, {}
    for index, name in enumerate(names):
        start, length = head[index * 2:index * 2 + 2]
        if start < 0 or length < 0 or length > MAX_MEMBER or (length and (start < 40 or start + length > len(raw))):
            raise ValueError("Invalid VFXMS block bound")
        if length and start != offset:
            raise ValueError("VFXMS header differs from APK sequential layout")
        data = raw[start:start + length] if length else b""
        row = {"start": start, "bytes": length, "sha256": digest(data)}
        if name == "config":
            if not isinstance(json_value(data), dict):
                raise ValueError("VFXMS config is not a JSON object")
            row["format"] = "UTF-8 JSON"
        elif length and name == "glb":
            row.update(glb_info(data))
        elif length and name in ("ibl", "skybox"):
            row.update(texture_info(data))
        blocks[name] = row
        offset += length
    if offset != len(raw):
        raise ValueError("Unaccounted VFXMS trailing bytes")
    return {"format": "VFXMS scene container", "header_endian": "big", "header_bytes": 40,
            "bytes": len(raw), "sha256": digest(raw), "blocks": blocks,
            "scope": "Structural byte inspection only; no rendering or native code execution"}


class Checks:
    def __init__(self):
        self.rows = []
    def add(self, name, condition):
        self.rows.append({"name": name, "passed": bool(condition)})
    @property
    def passed(self):
        return all(row["passed"] for row in self.rows)


def metadata_records(value):
    found = []
    if isinstance(value, dict):
        if "resourceUrl" in value or "cartoonUrl" in value:
            found.append(value)
        for item in value.values():
            found.extend(metadata_records(item))
    elif isinstance(value, list):
        for item in value:
            found.extend(metadata_records(item))
    return found


def verify_artifact(run, artifact, owner, expected):
    path = scoped(ROOT, artifact)
    if not path.is_relative_to(run / "packages"):
        raise ValueError("Artifact belongs to a different run")
    raw = read(path, "resource.zip")
    transport = read(path, "download.transport.bin")
    inventory = json_value(read(path, "inventory.json"))
    checks = Checks()
    checks.add("resource_zip_expected_sha256", digest(raw) == expected == inventory.get("sha256") == path.name)
    checks.add("transport_hash_http_size_and_decoded_payload", digest(transport) == owner["download_http"].get("sha256")
               == inventory.get("transport_sha256") and len(transport) == owner["download_http"].get("bytes")
               and http_payload(transport, owner["download_http"]) == raw)
    files, count, total = zip_members(raw)
    checks.add("zip_inventory_counts_and_sizes", len(raw) == inventory.get("bytes") and count == inventory.get("zip_member_count")
               and len(files) == inventory.get("file_count") and total == inventory.get("expanded_bytes"))
    rows = inventory.get("members", [])
    recorded = {row["path"]: row for row in rows}
    checks.add("inventory_member_set_exact", len(rows) == len(recorded) and set(files) == set(recorded))
    checked_files, formats = [], {}
    for name, data in files.items():
        row = recorded.get(name, {})
        checks.add("member:" + name, row.get("sha256") == digest(data) and row.get("bytes") == len(data)
                   and read(path / "extracted", name, MAX_MEMBER) == data)
        checked_files.append({"path": name, "bytes": len(data), "sha256": digest(data)})
        if name.lower().endswith(".vfxms"):
            formats[name] = vfxms_info(data)
    extracted = scoped(path, "extracted")
    disk_files = {p.relative_to(extracted).as_posix() for p in extracted.rglob("*") if p.is_file()}
    checks.add("extracted_file_set_exact", disk_files == set(files))
    config = json_value(files["config.json"])
    referenced = []
    for field in ("model3D", "modelWebp"):
        resource = config.get(field) if isinstance(config, dict) else None
        if resource is not None:
            name = resource.get("path") if isinstance(resource, dict) else None
            checks.add("config_reference:" + field, isinstance(name, str) and name in files)
            referenced.append({"field": field, "path": name})
    return {"passed": checks.passed, "checks": checks.rows, "zip_sha256": digest(raw), "zip_bytes": len(raw),
            "zip_members": count, "files": checked_files, "all_file_crc_and_actual_bytes_read": True,
            "config_references": referenced, "formats": formats}


def negative_tests():
    checks = Checks()
    for name in ("../escape", "/absolute", "C:/drive", "foo:stream", "CON.txt", "dir./x", "dir\\x", "foo\x00bar"):
        rejected = False
        try:
            safe_member(zipfile.ZipInfo(name))
        except ValueError:
            rejected = True
        checks.add("unsafe_name:" + name.replace("\x00", "[NUL]"), rejected)
    for names in (("x", "X"), ("x", "x/y")):
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, "w") as archive:
            for name in names:
                archive.writestr(name, b"x")
        rejected = False
        try:
            zip_members(stream.getvalue())
        except ValueError:
            rejected = True
        checks.add("collision:" + ",".join(names), rejected)
    return {"passed": checks.passed, "checks": checks.rows}


def verify_exports(run, artifacts):
    raw_report = read(run, "model-inspection/report.json")
    inspected = json_value(raw_report)
    checks, models = Checks(), []
    checks.add("inspection_report_validated_without_execution", inspected.get("status") == "validated"
               and inspected.get("network_used") is False and inspected.get("rendered") is False)
    expected_sources = set()
    for artifact, result in artifacts.items():
        relative = scoped(ROOT, artifact).relative_to(run).as_posix()
        for member in result.get("files", []):
            if member["path"].lower().endswith(".vfxms"):
                expected_sources.add(relative + "/extracted/" + member["path"])
    actual_sources = [model["source"] for model in inspected.get("models", [])]
    checks.add("inspection_model_set_exact", len(actual_sources) == len(set(actual_sources))
               and set(actual_sources) == expected_sources)
    for model in inspected.get("models", []):
        source = scoped(run, model["source"])
        if not source.is_relative_to(run / "packages"):
            raise ValueError("Inspection source escaped packages")
        original = read(run, model["source"], MAX_MEMBER)
        checks.add("inspection_source:" + model["source"], len(original) == model.get("size")
                   and digest(original) == model.get("sha256") and model.get("status") == "validated")
        # Derive slices independently from the original container, rather than
        # relying on the inspector's reported section offsets or hashes.
        header = struct.unpack_from(">10i", original)
        expected = {}
        for index, field in enumerate(("config", "glb", "ibl", "skybox")):
            start, length = header[index * 2:index * 2 + 2]
            if not length:
                continue
            block = original[start:start + length]
            name = ("scene-config.json" if field == "config" else "model.glb" if field == "glb"
                    else field + (".ktx2" if block.startswith(b"\xabKTX 20\xbb\r\n\x1a\n") else ".ktx"))
            expected[name] = block
        rows = model.get("exports", [])
        names = [Path(row["path"]).name for row in rows]
        checks.add("inspection_export_set:" + model["source"], len(names) == len(set(names))
                   and set(names) == set(expected))
        exports = []
        for row in rows:
            path = scoped(run, row["path"])
            if not path.is_relative_to(run / "model-inspection"):
                raise ValueError("Derived export escaped model-inspection")
            name = path.name
            saved = read(run, row["path"], MAX_MEMBER)
            block = expected.get(name)
            checks.add("export_slice:" + row["path"], block is not None and saved == block
                       and len(saved) == row.get("size") and digest(saved) == row.get("sha256"))
            exports.append({"file": name, "bytes": len(saved), "sha256": digest(saved),
                            "original_container_slice_byte_identical": saved == block})
        models.append({"source": model["source"], "exports": exports})
    return {"passed": checks.passed, "checks": checks.rows, "inspection_report_sha256": digest(raw_report),
            "models": models, "scope": "All reported exports compared byte-for-byte with independently sliced original containers"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", required=True, type=Path)
    parser.add_argument("--output", type=Path, help="New report below repository artifacts; refuses an existing file")
    args = parser.parse_args()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    run = args.run
    if not run.is_absolute() and run.parts and run.parts[0] == "runs":
        run = ROOT / run
    run = research_path(run)
    if not run.is_relative_to(ROOT / "runs") or run == ROOT / "runs" or not run.is_dir():
        parser.error("Run must be an existing research/online-artwork/runs subdirectory")
    output = scoped(run, "verification.json")
    if args.output is not None:
        output = args.output.resolve()
        artifacts = (ROOT.parents[1] / "artifacts").resolve()
        if output == artifacts or not output.is_relative_to(artifacts):
            parser.error("Output must be a file below repository artifacts")
        if output.exists():
            parser.error("Output already exists; choose a new report path")
    results_raw = read(run, "results.json")
    results = json_value(results_raw)
    report = {"schema_version": 1, "verified_at": datetime.now(timezone.utc).isoformat(),
              "run": run.relative_to(ROOT).as_posix(), "network_requested": False, "rendering_attempted": False,
              "keys_or_authentication_headers_written": False, "results_sha256": digest(results_raw),
              "sources": {}, "cases": [], "artifacts": {}}
    for package, source in results["sources"].items():
        checks = Checks()
        filename, expected, _, version = SOURCES[package]
        current = digest((ROOT.parent / "apps" / filename).read_bytes())
        checks.add("current_apk_hash_unchanged", current == expected == source.get("apk_sha256"))
        checks.add("application_metadata_version", str(source.get("app_metadata_version_code")) == version)
        report["sources"][package] = {"passed": checks.passed, "checks": checks.rows, "apk_sha256": current}
    owners = {case["package_artifact"]: case for case in results["cases"]
              if case.get("status") == "verified" and case.get("download_reused") is False}
    for case in results["cases"]:
        checks = Checks()
        package, body = case["package"], case["request_body"]
        _, _, channel, version = SOURCES[package]
        pid, color = body["productId"], body["color"]
        checks.add("request_endpoint_and_fields", case.get("endpoint") == "v1/earphone/resource/fetch"
                   and body.get("platform") == "android" and body.get("channel") == channel
                   and body.get("versionCode") == version and body.get("moduleId") == "14"
                   and pid in TARGETS and case.get("target_name") == TARGETS.get(pid))
        saved = json_value(read(run, f"requests/{package}_{pid}_{color}/metadata.redacted.json"))
        checks.add("saved_metadata_matches_case", saved == case.get("response_redacted"))
        records = metadata_records(saved.get("data"))
        checks.add("metadata_record_count", len(records) == case.get("resource_records_found"))
        acquired = case.get("status") == "verified"
        artifact = case.get("package_artifact")
        if acquired:
            checks.add("successful_metadata_and_unique_record", case.get("metadata_http", {}).get("http_status") == 200
                       and saved.get("code") in (0, 200) and len(records) == 1)
            record = records[0]
            expected = str(record.get("sha256", "")).lower()
            checks.add("metadata_expected_hash_and_attribution", bool(re.fullmatch(r"[0-9a-f]{64}", expected))
                       and expected == case.get("expected_sha256") and
                       (case.get("attribution") == "request_only" if "productId" not in record
                        else str(record["productId"]).upper() == pid and case.get("attribution") == "request_and_response_productId"))
            filename = record.get("fileName", "")
            match = re.fullmatch(r"android_all_([0-9A-Fa-f]{6})_(-?\d+)_14\.zip", filename)
            checks.add("metadata_filename_request_mapping", match is not None and match[1].upper() == pid and match[2] == color)
            if artifact not in report["artifacts"]:
                try:
                    report["artifacts"][artifact] = verify_artifact(run, artifact, owners[artifact], expected)
                except Exception as error:
                    report["artifacts"][artifact] = {"passed": False, "error_type": type(error).__name__}
            checks.add("referenced_artifact_verified", report["artifacts"][artifact]["passed"])
        elif case.get("status") == "no_resource_returned":
            checks.add("no_resource_status_matches_metadata", not records and not artifact and saved.get("code") in (0, 200))
        else:
            checks.add("unverified_case_has_no_acquired_artifact", not artifact)
        report["cases"].append({"package": package, "productId": pid, "color": color, "status": case.get("status"),
                                "acquired": acquired, "passed": checks.passed, "checks": checks.rows,
                                "package_artifact": artifact})
    report["negative_path_checks"] = negative_tests()
    try:
        report["derived_export_checks"] = verify_exports(run, report["artifacts"])
    except Exception as error:
        report["derived_export_checks"] = {"passed": False, "error_type": type(error).__name__}
    report["unique_verified_packages"] = len(report["artifacts"])
    report["unique_count_matches_acquisition"] = len(report["artifacts"]) == results.get("unique_verified_packages")
    report["limitations"] = ["Original metadata HTTP bodies were not retained; recorded transport hashes cannot be recomputed",
                              "ResourceFetchDO lacks productId; request-only attribution does not prove cross-model compatibility",
                              "No-resource responses apply only to the recorded App channel, product, color and time"]
    report["passed"] = report["unique_count_matches_acquisition"] and report["negative_path_checks"]["passed"] and report["derived_export_checks"]["passed"] and all(
        row["passed"] for group in (report["sources"].values(), report["cases"], report["artifacts"].values()) for row in group)
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("xb" if args.output is not None else "wb") as stream:
        stream.write((json.dumps(report, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))
    print(json.dumps({"passed": report["passed"], "run": report["run"], "unique_packages": report["unique_verified_packages"],
                      "acquired_cases": sum(case["acquired"] for case in report["cases"]),
                      "case_integrity_passed": sum(case["passed"] for case in report["cases"]),
                      "artifact_errors": [row.get("error_type") for row in report["artifacts"].values() if not row["passed"]]}, ensure_ascii=False))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
