#!/usr/bin/env python3
"""Inspect downloaded VFXMS containers and copy their original GLB/KTX slices.

Offline, standard-library only: no renderer, subprocess, APK execution, or network.
Input and derived output must remain inside this independent research directory.
Original packages and VFXMS files are never modified.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import struct
import sys

sys.dont_write_bytecode = True
DOCUMENTATION_ROOT = Path(__file__).resolve().parents[2]
RESEARCH = Path(__file__).resolve().parents[4] / "research/online-artwork"
sys.path.insert(0, str(DOCUMENTATION_ROOT / "earbud-artwork/scripts"))
from research_runtime import research_path
MAX_MODEL_BYTES = 256 * 1024 * 1024
MAX_JSON_BYTES = 8 * 1024 * 1024
KTX1 = b"\xabKTX 11\xbb\r\n\x1a\n"
KTX2 = b"\xabKTX 20\xbb\r\n\x1a\n"
PARTS = ("config", "glb", "ibl", "skybox", "animation")


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def inside(path: Path, root: Path) -> Path:
    resolved = research_path(path)
    if resolved == root or root not in resolved.parents:
        raise ValueError("Path must be strictly inside " + str(root))
    return resolved


def bounded_range(start: int, length: int, size: int, label: str) -> None:
    if start < 0 or length < 0 or start > size or length > size - start:
        raise ValueError(label + " range exceeds container bounds")


def strict_json(data: bytes) -> dict:
    if len(data) > MAX_JSON_BYTES:
        raise ValueError("JSON exceeds size limit")

    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("JSON contains duplicate object keys")
            result[key] = value
        return result

    def nonfinite(_):
        raise ValueError("JSON contains a nonfinite numeric constant")

    def finite_float(value):
        number = float(value)
        if not math.isfinite(number):
            raise ValueError("JSON number exceeds finite float range")
        return number

    result = json.loads(data.decode("utf-8"), object_pairs_hook=unique,
                        parse_constant=nonfinite, parse_float=finite_float)
    if not isinstance(result, dict):
        raise ValueError("Expected a JSON object")
    return result


def inspect_glb(data: bytes) -> dict:
    if len(data) < 20:
        raise ValueError("GLB is shorter than its header and JSON chunk")
    magic, version, total = struct.unpack_from("<4sII", data)
    if magic != b"glTF" or version != 2 or total != len(data):
        raise ValueError("Invalid GLB magic, version, or declared length")
    chunks = []
    cursor = 12
    document = None
    while cursor < len(data):
        bounded_range(cursor, 8, len(data), "GLB chunk header")
        length, kind = struct.unpack_from("<II", data, cursor)
        if length % 4:
            raise ValueError("GLB chunk length is not aligned to 4 bytes")
        bounded_range(cursor + 8, length, len(data), "GLB chunk")
        if not chunks and kind != 0x4E4F534A:
            raise ValueError("First GLB chunk must be JSON")
        if kind == 0x4E4F534A:
            if document is not None:
                raise ValueError("GLB contains multiple JSON chunks")
            document = strict_json(data[cursor + 8:cursor + 8 + length].rstrip(b" "))
        chunks.append({"offset": cursor, "length": length,
                       "type_hex": f"0x{kind:08x}",
                       "type": "JSON" if kind == 0x4E4F534A else
                               "BIN" if kind == 0x004E4942 else "unknown"})
        cursor += 8 + length
    if document is None or document.get("asset", {}).get("version") != "2.0":
        raise ValueError("GLB asset.version must be 2.0")
    if sum(c["type"] == "BIN" for c in chunks) > 1:
        raise ValueError("GLB contains multiple BIN chunks")
    return {"format": "GLB", "version": version, "length": total,
            "chunks": chunks, "asset": document.get("asset"),
            "scene_count": len(document.get("scenes", [])),
            "node_count": len(document.get("nodes", [])),
            "mesh_count": len(document.get("meshes", [])),
            "material_count": len(document.get("materials", [])),
            "animation_count": len(document.get("animations", [])),
            "extensions_used": document.get("extensionsUsed", []),
            "external_uri_count": sum(isinstance(item.get("uri"), str)
                                      for key in ("images", "buffers")
                                      for item in document.get(key, [])),
            "validation_scope": "container/chunk bounds and JSON; mesh accessors and codecs are not executed"}


def inspect_ktx(data: bytes) -> dict:
    if data.startswith(KTX1):
        if len(data) < 64:
            raise ValueError("KTX1 is shorter than its header")
        marker = data[12:16]
        endian = "<" if marker == b"\x01\x02\x03\x04" else \
                 ">" if marker == b"\x04\x03\x02\x01" else None
        if endian is None:
            raise ValueError("Invalid KTX1 endianness marker")
        values = struct.unpack_from(endian + "12I", data, 16)
        keys = ("gl_type", "gl_type_size", "gl_format", "gl_internal_format",
                "gl_base_internal_format", "width", "height", "depth",
                "array_elements", "faces", "mipmap_levels", "key_value_bytes")
        result = dict(zip(keys, values))
        if not result["width"] or result["faces"] not in (1, 6):
            raise ValueError("Invalid KTX1 dimensions or face count")
        # This research parser validates ordinary 2D/cubemap samples completely.
        if result["depth"] or result["array_elements"]:
            raise ValueError("KTX1 3D/array layout is not supported by this parser")
        bounded_range(64, result["key_value_bytes"], len(data), "KTX1 metadata")
        cursor = 64 + result["key_value_bytes"]
        levels = []
        count = max(1, result["mipmap_levels"])
        if count > 32:
            raise ValueError("KTX1 mipmap count exceeds research limit")
        for level in range(count):
            bounded_range(cursor, 4, len(data), "KTX1 imageSize")
            image_size = struct.unpack_from(endian + "I", data, cursor)[0]
            cursor += 4
            if image_size == 0:
                raise ValueError("KTX1 mipmap has no image bytes")
            face_ranges = []
            for face in range(result["faces"]):
                bounded_range(cursor, image_size, len(data), "KTX1 face")
                face_ranges.append({"face": face, "offset": cursor, "length": image_size})
                cursor += image_size
                padding = (-image_size) % 4
                bounded_range(cursor, padding, len(data), "KTX1 face padding")
                cursor += padding
            levels.append({"level": level, "image_size_per_face": image_size,
                           "faces": face_ranges})
        if cursor != len(data):
            raise ValueError("KTX1 has unexplained trailing bytes")
        return {"format": "KTX1", "byte_order": "little" if endian == "<" else "big",
                **result, "levels": levels,
                "validation_scope": "2D/cubemap headers and all mipmap/face bounds; pixel codecs are not executed"}
    if data.startswith(KTX2):
        if len(data) < 80:
            raise ValueError("KTX2 is shorter than its header")
        values = struct.unpack_from("<13I2Q", data, 12)
        keys = ("vk_format", "type_size", "width", "height", "depth", "layers",
                "faces", "level_count", "supercompression", "dfd_offset", "dfd_length",
                "kvd_offset", "kvd_length", "sgd_offset", "sgd_length")
        result = dict(zip(keys, values))
        count = result["level_count"]
        if not result["width"] or result["faces"] not in (1, 6) or not 1 <= count <= 32:
            raise ValueError("Invalid KTX2 dimensions, faces, or level count")
        bounded_range(80, count * 24, len(data), "KTX2 level index")
        for name in ("dfd", "kvd", "sgd"):
            bounded_range(result[name + "_offset"], result[name + "_length"], len(data),
                          "KTX2 " + name)
        levels = []
        for level in range(count):
            start, length, unpacked = struct.unpack_from("<3Q", data, 80 + 24 * level)
            bounded_range(start, length, len(data), "KTX2 level")
            if start < 80 + count * 24 or length == 0:
                raise ValueError("Invalid KTX2 level payload range")
            levels.append({"level": level, "offset": start, "length": length,
                           "uncompressed_length": unpacked})
        return {"format": "KTX2", **result, "levels": levels,
                "validation_scope": "header/metadata/level-index bounds; supercompression and pixel codecs are not executed"}
    raise ValueError("Expected KTX1 or KTX2 identifier")


def inspect_container(data: bytes) -> tuple[dict, dict[str, bytes]]:
    if len(data) < 40 or len(data) > MAX_MODEL_BYTES:
        raise ValueError("VFXMS container size is outside research limits")
    values = struct.unpack_from(">10i", data)
    ranges = {}
    for index, name in enumerate(PARTS):
        start, length = values[index * 2:index * 2 + 2]
        bounded_range(start, length, len(data), "VFXMS declared " + name)
        if length and start < 40:
            raise ValueError("VFXMS declared section overlaps its header")
        ranges[name] = {"declared_start": start, "length": length}
    cursor = 40
    slices = {}
    warnings = []
    for name in PARTS[:4]:
        item = ranges[name]
        bounded_range(cursor, item["length"], len(data), "VFXMS sequential " + name)
        item["loader_sequential_start"] = cursor
        item["start_matches_loader"] = not item["length"] or item["declared_start"] == cursor
        if not item["start_matches_loader"]:
            warnings.append(name + " declared start differs from APK sequential loader")
        slices[name] = data[cursor:cursor + item["length"]]
        cursor += item["length"]
    if not slices["config"]:
        raise ValueError("VFXMS has no scene configuration")
    config = strict_json(slices["config"])
    details = {"config": {"encoding": "UTF-8 JSON", "keys": sorted(config),
                           "scene_name": config.get("name")}}
    exports = {"scene-config.json": slices["config"]}
    if slices["glb"]:
        details["glb"] = inspect_glb(slices["glb"])
        exports["model.glb"] = slices["glb"]
    for name in ("ibl", "skybox"):
        if slices[name]:
            details[name] = inspect_ktx(slices[name])
            suffix = ".ktx2" if details[name]["format"] == "KTX2" else ".ktx"
            exports[name + suffix] = slices[name]
    trailing = data[cursor:]
    return {"header_bytes": 40, "header_byte_order": "big",
            "header_signed_int32": list(values), "sections": ranges,
            "extraction_basis": "APK sequential lengths after 40-byte header",
            "details": details, "warnings": warnings,
            "unconsumed_tail": {"offset": cursor, "length": len(trailing),
                                "sha256": digest(trailing) if trailing else None},
            "animation_handling": "header recorded; APK loader does not consume this section; original container retained"}, exports


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=Path, help="Research run containing packages/*/extracted")
    args = parser.parse_args()
    run = inside(args.run, RESEARCH)
    packages = inside(run / "packages", run)
    if not packages.is_dir():
        parser.error("Run has no packages directory")
    output = inside(run / "model-inspection", run)
    output.mkdir(parents=True, exist_ok=True)
    report = {"schema_version": 1, "created_utc": datetime.now(timezone.utc).isoformat(),
              "network_used": False, "rendered": False,
              "input_run": run.relative_to(RESEARCH).as_posix(),
              "limits": {"container_bytes": MAX_MODEL_BYTES, "json_bytes": MAX_JSON_BYTES},
              "models": []}
    failed = False
    for package in sorted(packages.iterdir()):
        if not package.is_dir():
            continue
        extracted = inside(package / "extracted", packages)
        if not extracted.is_dir():
            continue
        for candidate in sorted(extracted.rglob("*.vfxms")):
            entry = {"source": candidate.relative_to(run).as_posix(), "exports": []}
            try:
                source = inside(candidate, extracted)
                size = source.stat().st_size
                if size > MAX_MODEL_BYTES:
                    raise ValueError("Model exceeds byte limit")
                data = source.read_bytes()
                entry.update({"size": len(data), "sha256": digest(data)})
                parsed, exports = inspect_container(data)
                entry.update(parsed)
                destination = inside(output / source.relative_to(packages).with_suffix(""), output)
                destination.mkdir(parents=True, exist_ok=True)
                for name, value in exports.items():
                    target = inside(destination / name, output)
                    target.write_bytes(value)
                    entry["exports"].append({"path": target.relative_to(run).as_posix(),
                                             "size": len(value), "sha256": digest(value)})
                if source.stat().st_size != len(data) or digest(source.read_bytes()) != entry["sha256"]:
                    raise ValueError("Original model changed during inspection")
                entry["status"] = "validated_with_warnings" if entry["warnings"] else "validated"
            except (OSError, ValueError, struct.error, RecursionError, TypeError, AttributeError) as exc:
                entry.update({"status": "failed", "error": str(exc)[:500]})
                failed = True
            report["models"].append(entry)
    if not report["models"]:
        report["error"] = "No .vfxms files found under packages/*/extracted"
        failed = True
    report["status"] = "failed" if failed else "validated"
    target = inside(output / "report.json", output)
    target.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": report["status"], "model_count": len(report["models"]),
                      "report": str(target)}, ensure_ascii=False))
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
