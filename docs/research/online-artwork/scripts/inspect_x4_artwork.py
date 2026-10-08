#!/usr/bin/env python3
"""Verify a saved X4 audit run and inspect WebP timing/frames without network.

Keeps vendor package members unchanged. Produces a new inspection directory,
including a two-frame regression fixture made from original RIFF chunks (only
the enclosing RIFF length changes), independent decoded previews and pixel hashes.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import struct
import sys

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("x4_artwork_helpers", HERE / "fetch_artwork.py")
artwork = importlib.util.module_from_spec(spec)
spec.loader.exec_module(artwork)
sys.path.insert(0, str(HERE.parents[1] / "earbud-artwork/scripts"))
from research_runtime import bootstrap
bootstrap("PIL")
from PIL import Image, ImageDraw


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def riff_chunks(raw):
    if raw[:4] != b"RIFF" or raw[8:12] != b"WEBP" or struct.unpack_from("<I", raw, 4)[0] + 8 != len(raw):
        raise ValueError("Invalid WebP RIFF length")
    offset = 12
    while offset < len(raw):
        size = struct.unpack_from("<I", raw, offset + 4)[0]
        end = offset + 8 + size + (size & 1)
        if end > len(raw):
            raise ValueError("Truncated WebP chunk")
        yield offset, raw[offset:offset+4].decode("ascii"), raw[offset+8:offset+8+size], raw[offset:end]
        offset = end
    if offset != len(raw):
        raise ValueError("WebP boundary mismatch")


def webp_report(raw, directory, fixture=False):
    frames, chunks, kept = [], [], []
    for offset, kind, data, chunk in riff_chunks(raw):
        chunks.append({"offset": offset, "type": kind, "bytes": len(data), "sha256": sha(chunk)})
        if kind == "ANMF":
            values = [int.from_bytes(data[start:start+3], "little") for start in range(0, 15, 3)]
            frames.append({"x": values[0] * 2, "y": values[1] * 2, "width": values[2] + 1,
                           "height": values[3] + 1, "duration_ms": values[4], "flags": data[15]})
            if len(frames) <= 2:
                kept.append(chunk)
        else:
            kept.append(chunk)
    image = Image.open(io.BytesIO(raw))
    report = {"sha256": sha(raw), "bytes": len(raw), "canvas": list(image.size),
              "frame_count": image.n_frames, "loop_count": image.info.get("loop"),
              "background_rgba": image.info.get("background"), "duration_ms": sum(f["duration_ms"] for f in frames),
              "frames": frames, "chunks": chunks, "pixel_samples": []}
    selected = (0, 1) if fixture else (0, 45, 90, 135, 179)
    for frame in selected:
        image.seek(frame)
        rgba = image.convert("RGBA")
        filename = f"frame-{frame:03d}.png"
        rgba.save(directory / filename)
        report["pixel_samples"].append({"frame": frame, "rgba_sha256": sha(rgba.tobytes()),
                                        "bbox": rgba.getbbox(), "duration_ms": image.info.get("duration"),
                                        "timestamp_ms": image.info.get("timestamp"), "preview": filename})
    if not fixture:
        payload = b"WEBP" + b"".join(kept)
        short = b"RIFF" + struct.pack("<I", len(payload)) + payload
        short_dir = directory / "fixtures"
        short_dir.mkdir()
        (short_dir / "x4-first-two-frames.webp").write_bytes(short)
        report["original_chunk_fixture"] = webp_report(short, short_dir, fixture=True)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=Path)
    parser.add_argument("--output", type=Path, help="New evidence directory; must not already exist")
    args = parser.parse_args()
    run = args.run.resolve()
    if not run.is_relative_to(artwork.ROOT.resolve()):
        parser.error("Input run must be under research/online-artwork")
    output = args.output.resolve() if args.output else run / "inspection"
    if not output.is_relative_to(artwork.ROOT.resolve()):
        parser.error("Output must be under research/online-artwork")
    output.mkdir(exist_ok=False)
    results = json.loads((run / "results.json").read_text(encoding="utf-8"))
    report = {"productId": "06EC10", "packages": [], "vendor_originals_modified": False}
    for directory in sorted((run / "packages").iterdir()):
        raw = (directory / "resource.zip").read_bytes()
        if sha(raw) != directory.name:
            raise ValueError("Vendor archive hash mismatch")
        members, inventory = artwork.validate_zip(raw)
        for path, member, _ in members:
            if (directory / "extracted" / path).read_bytes() != member:
                raise ValueError("Extracted original mismatch")
        config = json.loads(next(member for path, member, _ in members if path == "config.json"))
        item = {"sha256": sha(raw), "bytes": len(raw), "member_count": len(members),
                "all_extracted_members_match_archive": True, "config": config, "lottie": []}
        for path, member, _ in members:
            if path.endswith(".json") and path != "config.json":
                parsed = json.loads(member)
                if isinstance(parsed, dict) and "layers" in parsed:
                    item["lottie"].append({"path": path, "sha256": sha(member), "frames": [parsed.get("ip"), parsed.get("op")],
                                           "frame_rate": parsed.get("fr"), "markers": parsed.get("markers"),
                                           "name": parsed.get("nm"), "canvas": [parsed.get("w"), parsed.get("h")]})
        if "modelWebp" in config:
            model_dir = output / "model-webp"
            model_dir.mkdir()
            relative = config["modelWebp"]["path"]
            model = next(member for path, member, _ in members if path == relative)
            item["model_webp"] = webp_report(model, model_dir)
        report["packages"].append(item)
    toy_dir = output / "synthetic-fixtures"
    toy_dir.mkdir()
    first = Image.new("RGBA", (32, 32), (0, 0, 0, 0))
    ImageDraw.Draw(first).rectangle((2, 2, 19, 19), fill=(255, 0, 0, 255))
    second = first.copy()
    ImageDraw.Draw(second).rectangle((8, 8, 14, 14), fill=(0, 200, 180, 255))
    first.save(toy_dir / "two-frame.webp", format="WEBP", save_all=True, append_images=[second],
               duration=[100, 300], loop=0, lossless=True, exact=True)
    report["synthetic_fixture"] = webp_report((toy_dir / "two-frame.webp").read_bytes(), toy_dir, fixture=True)
    report["successful_source_cases"] = [{key: case.get(key) for key in ("package", "request_body", "attribution", "expected_sha256")}
                                          for case in results["cases"] if case["status"] == "verified"]
    with (output / "report.json").open("x", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    print(json.dumps({"output": str(output), "packages_verified": len(report["packages"])}))


if __name__ == "__main__":
    main()
