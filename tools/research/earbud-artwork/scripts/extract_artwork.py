#!/usr/bin/env python3
"""Inventory APK artwork and extract local research copies; never execute APK code.

Usage: python extract_artwork.py --apk research/apps/欢律.apk
Pillow is required for decoding bitmaps. --pillow-path accepts an existing local
site-packages directory; no packages are installed and no network is contacted.
"""
from __future__ import annotations

import argparse
import base64
import collections
import csv
import datetime
import hashlib
import importlib
import io
import json
import re
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

sys.dont_write_bytecode = True
from android_resources import apk_manifest, decode_xml, resource_table
from research_runtime import research_path

MAX_ENTRY = 64 * 1024 * 1024
MEDIA_SUFFIXES = {".png", ".webp", ".jpg", ".jpeg", ".gif", ".avif", ".svg",
                  ".glb", ".gltf", ".obj", ".fbx", ".ktx", ".ktx2", ".dds",
                  ".mp4", ".webm", ".mov", ".upk", ".zip", ".glsl", ".bin"}
ART_NAME = re.compile(
    r"image_ear|detail_default_img|neck_headset|connect_tips|"
    r"hearing_enhancement_analysis|translation.*headset|bind_account|"
    r"control_guide|fit_detect|scanning|diagnos|manual_check|"
    r"(?:headset|earphone).*(?:default|image|picture)|(?:image|picture).*headset|"
    r"custom_dress_(?:image|video)_config|gold_hearing_detecting_guide", re.I)
EXCLUDED_ART_NAME = re.compile(r"speaker_default", re.I)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def load_pillow(paths: list[str]):
    candidates = [Path(p) for p in paths]
    candidates += [Path("C:/Users/LOZAP/AppData/Local/uv/cache/archive-v0/DL5bA3S7FfM8xwkI")]
    try:
        from PIL import Image
        return Image
    except ImportError:
        pass
    for candidate in candidates:
        if (candidate / "PIL").is_dir():
            sys.path.insert(0, str(candidate))
            importlib.invalidate_caches()
            from PIL import Image
            return Image
    raise SystemExit("Pillow is unavailable; pass --pillow-path for an existing installation.")


def category_for(name: str, kind: str, nested: bool) -> str:
    name = name.lower()
    if nested:
        return "widget"
    if kind == "shader":
        return "video_shader"
    if kind == "unknown_binary":
        return "unknown_binary"
    if kind == "config":
        return "resource_config"
    for pattern, category in [
        ("diagnos|manual_check", "diagnosis"),
        ("connect_tips|pair", "pairing"),
        ("bind_account", "account_link"),
        ("scanning", "scanning"),
        ("translation.*headset", "translation"),
        ("hearing|fit_detect", "hearing"),
        ("control_guide", "control"),
        ("ows", "device_ows"),
        ("neck", "device_neck"),
        ("ear|headset|speaker_default|detail_default_img", "device_default"),
    ]:
        if re.search(pattern, name):
            return category
    return "unrelated"


class Extraction:
    def __init__(self, output: Path, image):
        self.output = output
        self.Image = image
        self.assets = {}
        self.inventory = []
        self.sources = []
        self.errors = []

    def write(self, relative: str, data: bytes) -> str:
        path = (self.output / relative).resolve()
        if not path.is_relative_to(self.output):
            raise ValueError("Output path escaped research destination")
        path.parent.mkdir(parents=True, exist_ok=True)
        if not path.exists() or path.read_bytes() != data:
            path.write_bytes(data)
        return path.relative_to(self.output).as_posix()

    def asset(self, data: bytes, kind: str, category: str, title: str, origins: list[dict],
              suffix: str, width=None, height=None, alpha=None, decoded=None, notes=None) -> dict:
        digest = sha256(data)
        if digest not in self.assets:
            original = self.write(f"originals/{digest}/original{suffix}", data)
            item = {"id": digest, "sha256": digest, "kind": kind, "category": category,
                    "title": title, "model_status": "generic", "model_ids": [], "color": None,
                    "original_path": original, "preview_path": None, "width": width,
                    "height": height, "alpha": alpha, "origins": [], "notes": notes or []}
            if kind == "unknown_binary":
                item["model_status"] = "unknown"
            if decoded is not None:
                item["decoded_path"] = self.write(f"originals/{digest}/decoded.xml", decoded)
                item["notes"].append("Binary Android XML is authoritative; decoded.xml is a research conversion.")
            if kind == "bitmap":
                image = self.Image.open(io.BytesIO(data)).convert("RGBA")
                png = io.BytesIO()
                image.save(png, format="PNG")
                item["preview_path"] = self.write(f"previews/{digest}.png", png.getvalue())
            self.assets[digest] = item
        item = self.assets[digest]
        for origin in origins:
            if origin not in item["origins"]:
                item["origins"].append(origin)
        return item

    def inspect(self, name: str, data: bytes, metadata: dict, mappings: list[dict],
                parent_entry: str | None = None, forced: bool = False, parent_category=None):
        suffix = Path(name).suffix.lower()
        if suffix not in MEDIA_SUFFIXES | {".xml", ".json", ".config"}:
            return
        origins = []
        for mapping in mappings or [{}]:
            origin = {"package": metadata["package"], "version_name": metadata["version_name"],
                      "apk_sha256": metadata["apk_sha256"], "apk_entry": name,
                      "android_name": mapping.get("android_name"),
                      "android_type": mapping.get("android_type"),
                      "resource_id": mapping.get("resource_id"),
                      "qualifiers": mapping.get("qualifiers", {"qualifier": "", "density": None, "night": None})}
            if parent_entry:
                origin["parent_entry"] = parent_entry
            origins.append(origin)
        names = [m.get("android_name", "") for m in mappings]
        semantic = " ".join(names + [name])
        title = next((n for n in names if n), name)
        kind = None
        decoded = None
        width = height = alpha = frames = None
        parsed = None
        nested = parent_entry is not None
        selected = forced or (nested and suffix in {".svg", ".png", ".webp"}) or bool(ART_NAME.search(semantic))
        notes = []
        try:
            if suffix in {".png", ".webp", ".jpg", ".jpeg", ".gif", ".avif"} or data.startswith(b"\x89PNG\r\n\x1a\n"):
                kind = "bitmap"
                image = self.Image.open(io.BytesIO(data))
                image.load()
                width, height = image.size
                frames = getattr(image, "n_frames", 1)
                alpha = "A" in image.getbands() or "transparency" in image.info
                if suffix not in {".png", ".webp", ".jpg", ".jpeg", ".gif", ".avif"}:
                    suffix = ".png" if image.format == "PNG" else ".webp"
            elif suffix == ".xml":
                try:
                    root, decoded = decode_xml(data)
                except ET.ParseError:
                    if data.lstrip().startswith(b"<") and not data.lstrip().startswith(b"<vector"):
                        kind = "unknown_config"
                        selected = False
                        notes.append("Non-artwork text XML is malformed in the original APK; retained in inventory only.")
                        root = "non_artwork_config"
                    else:
                        raise
                if root not in {"vector", "animated-vector"}:
                    if kind != "unknown_config":
                        return
                else:
                    kind = "android_vector" if root == "vector" else "android_animated_vector"
                    element = ET.fromstring(decoded)
                    ns = "{http://schemas.android.com/apk/res/android}"
                    for attribute, target in [("viewportWidth", "width"), ("viewportHeight", "height")]:
                        value = element.get(ns + attribute)
                        if value and re.fullmatch(r"[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?", value):
                            if target == "width":
                                width = float(value)
                            else:
                                height = float(value)
                        elif value:
                            notes.append(f"{attribute} references {value}; numeric size is unresolved.")
            elif suffix == ".svg":
                kind = "svg"
                root = ET.fromstring(data)
                view_box = re.split(r"[\s,]+", root.get("viewBox", "").strip())
                if len(view_box) == 4:
                    width, height = float(view_box[2]), float(view_box[3])
                else:
                    for attribute in ("width", "height"):
                        match = re.match(r"[\d.]+", root.get(attribute, ""))
                        if match:
                            if attribute == "width":
                                width = float(match[0])
                            else:
                                height = float(match[0])
            elif suffix in {".json", ".config"}:
                try:
                    parsed = json.loads(data)
                except (UnicodeDecodeError, json.JSONDecodeError):
                    kind = "encrypted_config" if "whitelist" in semantic else "unknown_config"
                    selected = False
                else:
                    kind = "lottie" if isinstance(parsed, dict) and "layers" in parsed and "v" in parsed else "config"
                    if kind == "lottie":
                        width, height = parsed.get("w"), parsed.get("h")
            elif suffix == ".glsl":
                kind = "shader"
                selected = True
                notes.append("The paired shader samples RGB and alpha from video halves; it is not a 3D mesh.")
            elif suffix in {".upk", ".zip"} or data.startswith(b"PK\x03\x04"):
                kind = "upk" if suffix == ".upk" else "archive"
                selected = suffix == ".upk"
            elif suffix == ".bin":
                kind = "unknown_binary"
                selected = name == "assets/melody.bin"
                notes.append("Opaque binary; no established mesh, texture, or model format. Not counted as a 3D model.")
            elif suffix in {".glb", ".gltf", ".obj", ".fbx", ".ktx", ".ktx2", ".dds"}:
                kind = "model_candidate"
                selected = True
            elif suffix in {".mp4", ".webm", ".mov"}:
                kind = "video"
                selected = True
            if kind is None:
                return
            if EXCLUDED_ART_NAME.search(semantic):
                # Keep speaker appearances in the scan inventory, outside selection.
                selected = False
            category = parent_category or category_for(semantic, kind, nested)
            if kind == "upk":
                category = "widget"
            if category == "unrelated" and selected:
                category = "unknown_binary" if kind == "unknown_binary" else "control"
            record = {"package": metadata["package"], "version_name": metadata["version_name"],
                      "apk_entry": name, "parent_entry": parent_entry, "kind": kind,
                      "category": category, "sha256": sha256(data), "size_bytes": len(data),
                      "width": width, "height": height, "alpha": alpha, "frame_count": frames,
                      "selected": selected, "android_names": names, "origins": origins,
                      "asset_id": None, "status": "ok"}
            if selected:
                item = self.asset(data, kind, category, title, origins, suffix, width, height, alpha, decoded, notes)
                record["asset_id"] = item["id"]
            self.inventory.append(record)
            if kind == "lottie":
                for asset in parsed.get("assets", []):
                    image_path = asset.get("p", "")
                    if not image_path.startswith("data:image/"):
                        continue
                    match = re.match(r"data:image/([^;]+);base64,(.*)", image_path, re.S)
                    if not match:
                        continue
                    embedded = base64.b64decode(match[2], validate=True)
                    ext = ".jpg" if match[1] == "jpeg" else "." + match[1]
                    inner = "embedded/" + str(asset.get("id", "image")) + ext
                    self.inspect(inner, embedded, metadata, mappings, parent_entry=(parent_entry + "!" if parent_entry else "") + name,
                                 forced=selected, parent_category=category)
            if kind in {"upk", "archive"}:
                with zipfile.ZipFile(io.BytesIO(data)) as inner:
                    for entry in inner.infolist():
                        if entry.is_dir():
                            continue
                        if entry.file_size > MAX_ENTRY:
                            raise ValueError("Nested archive member too large")
                        self.inspect(entry.filename, inner.read(entry), metadata, [], parent_entry=name,
                                     forced=entry.filename in {"config.json", "assets/card-config.json", "pages/index.json"})
        except Exception as error:
            record = {"package": metadata["package"], "apk_entry": name, "parent_entry": parent_entry,
                      "kind": kind or "unknown", "sha256": sha256(data), "size_bytes": len(data),
                      "selected": False, "status": "error", "error": f"{type(error).__name__}: {error}"}
            self.inventory.append(record)
            self.errors.append(record)

    def apk(self, apk: Path):
        before = sha256(apk.read_bytes())
        start_index = len(self.inventory)
        with zipfile.ZipFile(apk) as archive:
            entries = archive.infolist()
            if len(entries) != len({entry.filename for entry in entries}):
                raise ValueError("Ambiguous archive: duplicate entry names")
            metadata = apk_manifest(archive.read("AndroidManifest.xml"))
            if metadata.get("package") != "com.heytap.headset":
                raise ValueError("Current artwork extraction accepts only HeyMelody APKs")
            metadata["apk_sha256"] = before
            mapping = resource_table(archive.read("resources.arsc"))
            for entry in entries:
                if entry.is_dir():
                    continue
                suffix = Path(entry.filename).suffix.lower()
                if suffix not in MEDIA_SUFFIXES | {".xml", ".json", ".config"}:
                    continue
                if entry.file_size > MAX_ENTRY:
                    raise ValueError(f"Archive member too large: {entry.filename}")
                self.inspect(entry.filename, archive.read(entry), metadata, mapping.get(entry.filename, []))
        after = sha256(apk.read_bytes())
        if before != after:
            raise ValueError("Source APK changed during research")
        records = self.inventory[start_index:]
        direct = [r for r in records if r.get("parent_entry") is None]
        metadata.update({"source_path": str(apk), "sha256_before": before,
                         "sha256_after": after, "unchanged": before == after,
                         "zip_entry_count": len(entries),
                         "direct_kind_counts": dict(collections.Counter(r["kind"] for r in direct)),
                         "nested_kind_counts": dict(collections.Counter(r["kind"] for r in records if r.get("parent_entry"))),
                         "direct_bitmap_formats": dict(collections.Counter(Path(r["apk_entry"]).suffix.lower() for r in direct if r["kind"] == "bitmap"))})
        self.sources.append(metadata)

    def finish(self):
        timestamp = datetime.datetime.now(datetime.timezone.utc).isoformat()
        assets = sorted(self.assets.values(), key=lambda a: (a["category"], a["title"], a["id"]))
        for item in assets:
            item["origins"].sort(key=lambda o: (o["package"], o.get("parent_entry", ""), o["apk_entry"], o["qualifiers"].get("qualifier", "")))
        def json_file(name, payload):
            self.write(name, (json.dumps(payload, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))
        json_file("assets.json", {"schema_version": 1, "generated_at": timestamp,
                                  "asset_count": len(assets), "assets": assets})
        json_file("inventory.json", {"schema_version": 1, "generated_at": timestamp,
                                     "entry_count": len(self.inventory), "entries": self.inventory,
                                     "errors": self.errors})
        json_file("sources.json", {"schema_version": 1, "generated_at": timestamp,
                                   "sources": self.sources, "source_files_unchanged": all(s["unchanged"] for s in self.sources),
                                   "scope": "Local artwork research only; no project integration or redistribution."})
        columns = ["package", "version_name", "apk_entry", "parent_entry", "kind", "category", "sha256",
                   "size_bytes", "width", "height", "alpha", "frame_count", "selected", "asset_id", "status", "android_names"]
        stream = io.StringIO(newline="")
        writer = csv.DictWriter(stream, fieldnames=columns, extrasaction="ignore")
        writer.writeheader()
        for record in self.inventory:
            row = dict(record)
            row["android_names"] = ";".join(row.get("android_names", []))
            writer.writerow(row)
        self.write("inventory.csv", stream.getvalue().encode("utf-8-sig"))
        return {"asset_count": len(assets), "inventory_count": len(self.inventory), "error_count": len(self.errors),
                "sources": [{"package": s["package"], "counts": s["direct_kind_counts"], "unchanged": s["unchanged"]} for s in self.sources]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apk", type=Path, action="append", required=True)
    parser.add_argument("--output", type=Path, default=Path(__file__).resolve().parents[4] / "research/earbud-artwork")
    parser.add_argument("--pillow-path", action="append", default=[])
    args = parser.parse_args()
    research = Path(__file__).resolve().parents[4] / "research"
    output = research_path(args.output)
    if output == research or not output.is_relative_to(research):
        parser.error("Output must be a dedicated directory below research/.")
    apks = [research_path(p) for p in args.apk]
    for apk in apks:
        if not apk.is_relative_to(research) or not apk.is_file() or apk.suffix.lower() != ".apk":
            parser.error("Input APKs must be existing files below research/.")
    image = load_pillow(args.pillow_path)
    extraction = Extraction(output, image)
    for apk in apks:
        extraction.apk(apk)
    summary = extraction.finish()
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    if extraction.errors:
        raise SystemExit(1)


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    main()
