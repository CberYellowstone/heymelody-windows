#!/usr/bin/env python3
"""Fetch official firmware metadata and inspect the observed Air4 Pro legacy OTA.

Online requests require --online. APK authentication material remains in memory;
no device identifier is accepted. Offline inspect/unpack need only Python 3.11+.
The decoder supports the observed FFFFFFFF / BE32 / LZMA-alone block layout,
not OPKG, encrypted packages, arbitrary update formats, repacking, or flashing.
The vendor CRC algorithm/range is unresolved and is never reported as verified.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import hmac
import importlib.util
import json
import lzma
from pathlib import Path
import re
import struct
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zlib

sys.dont_write_bytecode = True

SCHEMA_VERSION = 1
API_URL = "https://iot-earbuds-cn.heytapmobi.com/v1/earphone/firmwareInfo"
MAX_METADATA_BYTES = 1024 * 1024
MAX_PACKAGE_BYTES = 64 * 1024 * 1024
MAX_IMAGE_BYTES = 64 * 1024 * 1024
MAX_BLOCK_OUTPUT_BYTES = 128 * 1024
MAX_BLOCK_COUNT = MAX_IMAGE_BYTES // MAX_BLOCK_OUTPUT_BYTES
LZMA_MEMORY_LIMIT = 128 * 1024 * 1024
TRAILER = re.compile(rb"CRC32_OF_IMAGE=0x([0-9A-Fa-f]{8})\nPRODUCT_ID=0x([0-9A-Fa-f]{6})\n")
PRODUCT_ID = re.compile(r"[0-9A-Fa-f]{6}\Z")
SHA256 = re.compile(r"[0-9A-Fa-f]{64}\Z")
SOURCES = {"heymelody": "com.heytap.headset", "wireless": "com.oplus.melody"}


class FormatError(ValueError):
    """Unsupported layout, invalid bounds, or inconsistent source metadata."""


class NoRedirects(urllib.request.HTTPRedirectHandler):
    """Do not forward signed metadata headers to another endpoint."""

    def redirect_request(self, request, fp, code, message, headers, new_url):
        return None


def require(condition: bool, message: str) -> None:
    if not condition:
        raise FormatError(message)


def sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def tool_provenance() -> dict:
    source = Path(__file__).resolve()
    return {"path": str(source), "sha256": sha256(source.read_bytes()),
            "python_version": sys.version.split()[0]}


def checked_product_id(value: str) -> str:
    require(isinstance(value, str) and PRODUCT_ID.fullmatch(value) is not None,
            "Product ID must contain exactly six hexadecimal characters")
    return value.upper()


def read_bounded(path: Path, limit: int) -> bytes:
    require(path.is_file(), "Input is not a regular file")
    require(path.stat().st_size <= limit, "Input exceeds the configured size limit")
    with path.open("rb") as stream:
        value = stream.read(limit + 1)
    require(len(value) <= limit, "Input grew beyond the configured size limit")
    return value


def write_new(path: Path, value: bytes) -> None:
    with path.open("xb") as stream:
        stream.write(value)


def write_json_new(path: Path, value: object) -> None:
    write_new(path, (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))


def load_metadata(path: Path | None) -> dict | None:
    if path is None:
        return None
    value = json.loads(read_bounded(path, MAX_METADATA_BYTES).decode("utf-8-sig"))
    require(isinstance(value, dict), "Metadata root must be an object")
    return sanitized_metadata(value)


def entry_size(entry: dict) -> int:
    value = entry.get("size")
    require(not isinstance(value, bool) and
            ((isinstance(value, int) and value > 0) or
             (isinstance(value, str) and re.fullmatch(r"[1-9][0-9]*", value) is not None)),
            "Metadata package size is invalid")
    size = int(value)
    require(size <= MAX_PACKAGE_BYTES, "Metadata package exceeds the download size limit")
    return size


def metadata_entry(metadata: dict, name: str | None = None) -> tuple[dict, dict]:
    metadata = sanitized_metadata(metadata)
    require(type(metadata.get("code")) is int and metadata["code"] in (0, 200),
            "Official metadata request was rejected")
    data = metadata.get("data")
    require(isinstance(data, dict), "Metadata data must be an object")
    checked_product_id(data.get("productId"))
    entries = data.get("content")
    require(isinstance(entries, list) and entries, "Metadata contains no firmware packages")
    require(all(isinstance(entry, dict) for entry in entries), "Metadata package entries must be objects")
    selected = entries if name is None else [entry for entry in entries if entry.get("name") == name]
    require(len(selected) == 1, "A single package must be selected by its exact metadata filename")
    entry = selected[0]
    filename = entry.get("name")
    require(isinstance(filename, str) and filename not in ("", ".", "..") and
            Path(filename).name == filename and "/" not in filename and "\\" not in filename,
            "Metadata package filename is unsafe")
    require(filename.casefold() not in ("metadata.sanitized.json", "acquisition.json"),
            "Metadata package filename collides with a reserved acquisition artifact")
    entry_size(entry)
    digest = entry.get("firmwareSHA256")
    require(isinstance(digest, str) and SHA256.fullmatch(digest) is not None,
            "Metadata package SHA-256 is missing or invalid")
    return data, entry


def verify_metadata(metadata: dict, package: bytes, filename: str,
                    product_id: str) -> dict:
    data, entry = metadata_entry(metadata, filename)
    require(checked_product_id(data["productId"]) == product_id,
            "Metadata product ID does not match the package trailer")
    require(entry_size(entry) == len(package), "Metadata package size mismatch")
    require(entry["firmwareSHA256"].lower() == sha256(package), "Metadata package SHA-256 mismatch")
    return {"product_id": product_id, "version": data.get("version"),
            "product_name": data.get("name"), "filename": entry["name"],
            "source_url": entry.get("url"), "size_verified": True,
            "sha256_verified": True}


def image_summary(image: bytes) -> dict:
    fields = {}
    selected = []
    pattern = re.compile(r"^(CHIP=|KERNEL=|SW_VER=|BUILD_DATE=|REV_INFO=)|"
                         r"OPPO Enco Air4 Pro|\[RX\].*spp_receive|\[SPP_MODE\]|"
                         r"codec type:|receive_enco_link_info error")
    for match in re.finditer(rb"[\x20-\x7e]{8,}", image):
        value = match.group().decode("ascii")
        if len(value) <= 240 and pattern.search(value):
            if len(selected) < 64:
                selected.append({"offset": match.start(), "offset_hex": hex(match.start()), "text": value})
            if re.match(r"^(CHIP|KERNEL|SW_VER|BUILD_DATE|REV_INFO)=", value):
                key, item = value.split("=", 1)
                fields[key] = item
    return {"build_fields": fields, "selected_strings": selected,
            "first_128_bytes_hex": image[:128].hex()}


def inspect_bytes(package: bytes, *, expected_product_id: str | None = None,
                  metadata: dict | None = None, filename: str = "firmware.bin") -> tuple[bytes, dict]:
    require(0 < len(package) <= MAX_PACKAGE_BYTES, "Package length exceeds the supported bounds")
    require(package[:4] == b"\xff" * 4,
            "Unsupported package magic; only the observed legacy LZMA layout is supported")
    offset = 4
    raw_offset = 0
    blocks = []
    pieces = []
    while offset < len(package) and not package.startswith(b"CRC32_OF_IMAGE=", offset):
        require(len(blocks) < MAX_BLOCK_COUNT, "Package exceeds the block count limit")
        require(offset + 4 <= len(package), "Truncated block length field")
        compressed_size = struct.unpack_from(">I", package, offset)[0]
        require(13 <= compressed_size <= MAX_PACKAGE_BYTES, "Invalid compressed block length")
        payload_offset = offset + 4
        block_end = payload_offset + compressed_size
        require(block_end <= len(package), "Compressed block extends beyond the package")
        compressed = package[payload_offset:block_end]
        decoder = lzma.LZMADecompressor(format=lzma.FORMAT_ALONE, memlimit=LZMA_MEMORY_LIMIT)
        try:
            raw = decoder.decompress(compressed, max_length=MAX_BLOCK_OUTPUT_BYTES + 1)
        except lzma.LZMAError as error:
            raise FormatError(f"LZMA decoding failed in block {len(blocks)}: {error}") from error
        require(len(raw) <= MAX_BLOCK_OUTPUT_BYTES, "Decompressed block exceeds the 128 KiB limit")
        require(decoder.eof, "LZMA stream did not finish within its declared block")
        require(not decoder.unused_data, "Extra bytes follow the LZMA stream inside its block")
        require(len(raw) > 0, "Empty decompressed blocks are unsupported")
        require(raw_offset + len(raw) <= MAX_IMAGE_BYTES, "Decompressed image exceeds its size limit")
        if blocks:
            require(blocks[-1]["raw_size"] == MAX_BLOCK_OUTPUT_BYTES,
                    "Only the final block may be shorter than 128 KiB")
        blocks.append({"index": len(blocks), "length_field_offset": offset,
                       "compressed_offset": payload_offset, "compressed_size": compressed_size,
                       "compressed_sha256": sha256(compressed), "lzma_properties_hex": compressed[:5].hex(),
                       "lzma_declared_raw_size": struct.unpack_from("<Q", compressed, 5)[0],
                       "raw_offset": raw_offset, "raw_size": len(raw), "raw_sha256": sha256(raw),
                       "lzma_eof_verified": True, "no_unused_block_bytes_verified": True})
        pieces.append(raw)
        raw_offset += len(raw)
        offset = block_end
    require(blocks, "Package contains no compressed blocks")
    trailer = package[offset:]
    trailer_match = TRAILER.fullmatch(trailer)
    require(trailer_match is not None, "Missing, truncated, or unsupported package trailer")
    product_id = checked_product_id(trailer_match.group(2).decode("ascii"))
    if expected_product_id is not None:
        require(product_id == checked_product_id(expected_product_id), "Package product ID mismatch")
    image = b"".join(pieces)
    metadata_result = verify_metadata(metadata, package, filename, product_id) if metadata is not None else None
    report = {"schema_version": SCHEMA_VERSION,
              "format": "observed_legacy_lzma_be32_blocks",
              "scope": "Observed Air4 Pro v125 layout; no claim of support for all models or firmware versions",
              "source": {"filename": filename, "bytes": len(package), "sha256": sha256(package)},
              "metadata": metadata_result, "product_id": product_id,
              "container": {"magic_hex": package[:4].hex(), "length_byte_order": "big",
                            "trailer_offset": offset, "trailer_bytes": len(trailer),
                            "trailer_ascii": trailer.decode("ascii"), "all_bytes_accounted_for": True},
              "block_count": len(blocks), "blocks": blocks,
              "image": {"bytes": len(image), "sha256": sha256(image), **image_summary(image)},
              "vendor_crc": {"declared": "0x" + trailer_match.group(1).decode("ascii").upper(),
                             "status": "unresolved", "verified": False,
                             "reason": "Vendor CRC algorithm and covered byte range are not established",
                             "candidate_zlib_crc32": {
                                 "raw_image": f"0x{zlib.crc32(image) & 0xffffffff:08X}",
                                 "raw_image_without_first_four_bytes": f"0x{zlib.crc32(image[4:]) & 0xffffffff:08X}",
                                 "compressed_container_without_trailer": f"0x{zlib.crc32(package[:offset]) & 0xffffffff:08X}",
                                 "compressed_container_without_magic_or_trailer": f"0x{zlib.crc32(package[4:offset]) & 0xffffffff:08X}"}},
              "limitations": ["This is an OTA image, not a physical flash dump or original source code",
                              "Vendor CRC, signatures, secure boot, repacking and flashing are not verified",
                              "User firmware version and real-device behavior are unknown"]}
    return image, report


def inspect_file(source: Path, expected_product_id: str | None = None,
                 metadata_path: Path | None = None) -> tuple[bytes, dict]:
    image, report = inspect_bytes(read_bounded(source, MAX_PACKAGE_BYTES),
                                  expected_product_id=expected_product_id,
                                  metadata=load_metadata(metadata_path), filename=source.name)
    report["source"]["path"] = str(source.resolve())
    report["metadata_path"] = str(metadata_path.resolve()) if metadata_path else None
    report["tool"] = tool_provenance()
    return image, report


def new_directory(path: Path) -> None:
    path.mkdir(parents=True, exist_ok=False)


def unpack(source: Path, destination: Path, expected_product_id: str | None = None,
           metadata_path: Path | None = None, image_name: str = "image.raw.bin") -> dict:
    require(Path(image_name).name == image_name and image_name not in ("", ".", "..") and
            image_name.casefold() not in ("inspection.json", "blocks.json") and
            "/" not in image_name and "\\" not in image_name, "Unsafe output image filename")
    image, report = inspect_file(source, expected_product_id, metadata_path)
    new_directory(destination)
    write_new(destination / image_name, image)
    report["image"]["path"] = str((destination / image_name).resolve())
    write_json_new(destination / "inspection.json", report)
    write_json_new(destination / "blocks.json", {"schema_version": SCHEMA_VERSION,
                                                "source": report["source"], "image": report["image"],
                                                "blocks": report["blocks"]})
    return report


def approved_download(url: str) -> bool:
    try:
        parsed = urllib.parse.urlsplit(url)
        return (parsed.scheme == "https" and parsed.hostname is not None and
                (parsed.hostname == "heytapcs.com" or parsed.hostname.endswith(".heytapcs.com")) and
                parsed.username is None and parsed.password is None and parsed.port in (None, 443))
    except ValueError:
        return False


def online_helpers():
    helper_path = Path(__file__).resolve().parent / "online-whitelist/scripts/fetch_whitelist.py"
    spec = importlib.util.spec_from_file_location("firmware_online_helpers", helper_path)
    require(spec is not None and spec.loader is not None, "Cannot load existing APK authentication helper")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def http_read(opener, request: urllib.request.Request, limit: int) -> tuple[bytes, dict]:
    try:
        response = opener.open(request, timeout=25)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = response.read(limit + 1)
        require(len(raw) <= limit, "HTTP response exceeds its size limit")
        info = {"url": response.geturl(), "http_status": response.status,
                "content_type": response.headers.get("Content-Type"),
                "content_encoding": response.headers.get("Content-Encoding"),
                "bytes": len(raw), "sha256": sha256(raw)}
    require(info["http_status"] == 200, f"Official HTTP request failed with status {info['http_status']}")
    return raw, info


def sanitized_metadata(parsed: dict) -> dict:
    """Project public firmware fields only; never persist unknown response fields."""
    require(isinstance(parsed, dict) and isinstance(parsed.get("data"), dict), "Invalid official response")
    require(parsed.get("msg") is None or isinstance(parsed["msg"], str),
            "Public response message must be a string or null")
    data = parsed["data"]
    entries = data.get("content")
    require(isinstance(entries, list), "Invalid official package list")
    keep_data = ("name", "productId", "updateInfo", "version")
    keep_entry = ("name", "size", "firmwareSHA256", "updateInfo", "url")
    require(all(isinstance(entry, dict) for entry in entries), "Invalid official package entry")
    require(all(isinstance(data[key], str) for key in keep_data if key in data),
            "Public firmware metadata fields must be strings")
    require(all(isinstance(entry[key], str) for entry in entries for key in keep_entry
                if key != "size" and key in entry), "Public package metadata fields must be strings")
    return {"code": parsed.get("code"), "msg": parsed.get("msg"),
            "data": {**{key: data[key] for key in keep_data if key in data},
                     "content": [{key: entry[key] for key in keep_entry if key in entry} for entry in entries]}}


def fetch(args) -> dict:
    require(args.online, "Online acquisition requires explicit --online")
    require(not args.output_dir.exists(), "Output directory already exists; refusing to overwrite")
    started_at = datetime.now(timezone.utc).isoformat()
    product_id = checked_product_id(args.product_id)
    require(re.fullmatch(r"[0-9]+(?:\.[0-9]+)*", args.software_version) is not None,
            "Software version must contain digits and optional dots")
    if args.proxy:
        proxy = urllib.parse.urlsplit(args.proxy)
        require(proxy.scheme == "http" and proxy.hostname is not None and
                proxy.username is None and proxy.password is None,
                "Proxy must be an HTTP URL without credentials")
    module = online_helpers()
    package = SOURCES[args.source]
    with module.legacy.verified_apk(package) as archive:
        info = module.manifest_info(archive, package)
        material, authentication = module.authenticate_material(archive, module.SPECS[package])
    body = {"platform": "android", "channel": module.legacy.APK_SPEC[package]["channel"],
            "versionCode": str(info["app_metadata_version_code"]), "productId": product_id,
            "language": args.language, "softwareVersion": args.software_version}
    body_bytes = json.dumps(body, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    headers = {"Content-Type": "application/json", "appid": "earphone", "sv": "v1",
               "ts": str(int(time.time() * 1000)), "nonce": str(uuid.uuid4()),
               "sign": hmac.new(material, body_bytes, hashlib.sha1).hexdigest()}
    opener = urllib.request.build_opener(urllib.request.ProxyHandler(
        {} if not args.proxy else {"http": args.proxy, "https": args.proxy}), NoRedirects())
    response, response_info = http_read(opener, urllib.request.Request(
        API_URL, data=body_bytes, headers=headers, method="POST"), MAX_METADATA_BYTES)
    if response_info["content_encoding"] == "gzip":
        response = module.gzip_decode(response, MAX_METADATA_BYTES)
    require(response_info["content_encoding"] in (None, "gzip", "identity"), "Unsupported HTTP encoding")
    parsed = sanitized_metadata(json.loads(response))
    data, entry = metadata_entry(parsed, args.name)
    require(checked_product_id(data["productId"]) == product_id, "Official response product ID mismatch")
    require(isinstance(entry.get("url"), str) and approved_download(entry["url"]),
            "Download URL must belong to an approved HTTPS vendor CDN")
    if args.verified_cache:
        binary = read_bounded(args.verified_cache, MAX_PACKAGE_BYTES)
        download_info = {"source": "metadata_verified_local_cache", "cache_path": str(args.verified_cache.resolve()),
                         "url": entry["url"], "bytes": len(binary), "sha256": sha256(binary)}
    else:
        binary, download_info = http_read(opener, urllib.request.Request(entry["url"], method="GET"),
                                          MAX_PACKAGE_BYTES)
        require(approved_download(download_info["url"]), "Vendor download redirected to an unapproved host")
        require(download_info["content_encoding"] in (None, "identity"), "Encoded binary download is unsupported")
        download_info["source"] = "official_https_cdn_get"
    require(entry_size(entry) == len(binary), "Official package size mismatch")
    require(entry["firmwareSHA256"].lower() == sha256(binary), "Official package SHA-256 mismatch")
    # Validate the selected package before creating the immutable acquisition directory.
    _, inspection = inspect_bytes(binary, expected_product_id=product_id,
                                  metadata=parsed, filename=entry["name"])
    safe_auth = {key: authentication[key] for key in
                 ("dex", "dex_sha256", "method", "code_offset", "material_persisted") if key in authentication}
    record = {"schema_version": SCHEMA_VERSION, "endpoint": API_URL, "method": "POST",
              "started_at": started_at, "completed_at": datetime.now(timezone.utc).isoformat(),
              "tool": tool_provenance(),
              "helper_sources": [{"path": str(Path(helper.__file__).resolve()),
                                  "sha256": sha256(Path(helper.__file__).read_bytes())}
                                 for helper in (module, module.legacy, module.android)],
              "request_body": body, "device_identifier_supplied": False,
              "software_version_input_kind": "explicit_query_parameter_not_device_observation",
              "apk": {**info, "apk_sha256": module.legacy.APK_SPEC[package]["sha256"],
                      "source": args.source, "authentication_evidence": safe_auth},
              "authentication_headers_persisted": False,
              "response": {**response_info, "saved_fields": "public_firmware_projection",
                           "decoded_response_bytes": len(response), "decoded_response_sha256": sha256(response)},
              "download": {**download_info, "size_verified": True, "sha256_verified": True},
              "selected_package": {"name": entry["name"], "version": data.get("version"), "product_id": product_id},
              "legacy_inspection": {"format": inspection["format"], "block_count": inspection["block_count"],
                                    "image_bytes": inspection["image"]["bytes"],
                                    "image_sha256": inspection["image"]["sha256"],
                                    "vendor_crc_verified": False}}
    new_directory(args.output_dir)
    write_new(args.output_dir / entry["name"], binary)
    write_json_new(args.output_dir / "metadata.sanitized.json", parsed)
    write_json_new(args.output_dir / "acquisition.json", record)
    return record


def bounded_summary(value):
    """Bound dynamic strings while keeping complete valid metadata in local reports."""
    if isinstance(value, str):
        return value if len(value) <= 240 else value[:200] + f" [truncated; {len(value)} characters]"
    if isinstance(value, dict):
        return {key: bounded_summary(item) for key, item in list(value.items())[:32]}
    if isinstance(value, list):
        return [bounded_summary(item) for item in value[:16]]
    return value


def bounded_json(value) -> str:
    encoded = json.dumps(bounded_summary(value), ensure_ascii=False)
    if len(encoded) <= 6000:
        return encoded
    return json.dumps({"summary_truncated": True,
                       "details": "See the archived acquisition or inspection report"})


def brief(report: dict) -> dict:
    return bounded_summary({"format": report["format"], "product_id": report["product_id"],
            "source": report["source"], "block_count": report["block_count"],
            "image_bytes": report["image"]["bytes"], "image_sha256": report["image"]["sha256"],
            "image_path": report["image"].get("path"), "build_fields": report["image"]["build_fields"],
            "metadata": report["metadata"], "vendor_crc_verified": False})


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    online = commands.add_parser("fetch", help="Query official metadata and preserve one verified legacy package")
    online.add_argument("--online", action="store_true")
    online.add_argument("--source", choices=SOURCES, default="heymelody")
    online.add_argument("--product-id", required=True)
    online.add_argument("--software-version", default="0")
    online.add_argument("--language", default="zh-CN")
    online.add_argument("--name", help="Exact filename when metadata lists several packages")
    online.add_argument("--proxy")
    online.add_argument("--verified-cache", type=Path, help="Copy this local package after matching fresh official metadata")
    online.add_argument("--output-dir", type=Path, required=True)
    for command in ("inspect", "unpack"):
        item = commands.add_parser(command)
        item.add_argument("--input", type=Path, required=True)
        item.add_argument("--expected-product-id")
        item.add_argument("--metadata", type=Path)
        if command == "inspect":
            item.add_argument("--report", type=Path, help="New complete JSON report; stdout remains bounded")
        else:
            item.add_argument("--output-dir", type=Path, required=True)
            item.add_argument("--image-name", default="image.raw.bin")
    args = parser.parse_args(argv)
    try:
        if args.command == "fetch":
            result = fetch(args)
            summary = {"output_dir": str(args.output_dir.resolve()), "selected_package": result["selected_package"],
                       "download": result["download"], "legacy_inspection": result["legacy_inspection"]}
        elif args.command == "inspect":
            _, result = inspect_file(args.input, args.expected_product_id, args.metadata)
            if args.report:
                write_json_new(args.report, result)
            summary = brief(result)
        else:
            result = unpack(args.input, args.output_dir, args.expected_product_id, args.metadata, args.image_name)
            summary = brief(result)
        print(bounded_json(summary))
        return 0
    except (FormatError, OSError, json.JSONDecodeError, UnicodeError) as error:
        print(bounded_json({"status": "failed", "error_type": type(error).__name__, "message": str(error)}),
              file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
