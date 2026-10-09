"""Add feature artwork from the two local APKs; preserve the existing gallery assets.

No network, ADB, dependency installation, or APK execution. Original files remain
byte-for-byte copies. Drawable/color XML references are followed within each APK.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict, deque
import csv
from datetime import datetime, timezone
import hashlib
import io
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET
import zipfile

from android_resources import decode_xml, resource_table
from extract_artwork import Extraction, load_pillow
from merge_and_verify import INPUTS

ROOT = Path(__file__).resolve().parents[4] / "research/earbud-artwork"
PREFIXES = ("melody_", "heymelody_")
NOISE_SELECTION = ROOT / "evidence/feature-noise-selection.json"
DRAWABLE_ROOTS = {"selector", "layer-list", "shape", "inset", "ripple", "bitmap", "level-list",
                  "animation-list", "animated-selector", "rotate", "scale", "clip", "transition"}
FEATURE_RULES = [
    (r"hi_?res", "hi_res", "feature_audio_quality"),
    (r"lossless|high_(?:tone_)?quality|lhdc|ldac|codec", "audio_quality", "feature_audio_quality"),
    (r"dolby", "dolby", "feature_audio_quality"),
    (r"dynaudio|equalizer|equailizer", "equalizer", "feature_equalizer"),
    (r"spatial_audio", "spatial_audio", "feature_spatial"),
    (r"game_type|game.*latency|game_mode", "game_mode", "feature_gaming"),
    (r"battery", "battery", "feature_battery"),
    (r"find_?device", "find_device", "feature_general"),
    (r"multi_connect|smart_bluetooth|bind_account|scanning", "connection", "feature_connection"),
    (r"control_guide|gesture|pinch|voice_command|voice_assist|speech_perception|ai_clear_call|"
     r"tutorial_guide_(?:music|play)|drop_detection", "gestures", "feature_controls"),
    (r"gold_hearing|hearing_enhancement|adaptive_hearing|fit_detect", "hearing", "feature_general"),
    (r"ai_summary|ai_translation|hey_translation", "ai_translation", "feature_general"),
    (r"sleep_detection|health_mobility|health_.*(?:calibration|evaluate|exercise)", "health", "feature_general"),
    (r"zen_mode|personal_dress_tone|sound_(?:setting|download|background_mode|card_)", "relax_sounds", "feature_general"),
]


def classification(names: list[str], exact: dict | None = None) -> tuple[set[str], str | None]:
    tags, category = set(), None
    exact = exact or {}
    for name in names:
        if name in exact:
            tags.update(exact[name]["feature_tags"])
            category = category or exact[name]["category"]
            continue
        if not name.startswith(PREFIXES) or "speaker_default" in name:
            continue
        noise = bool(re.search(r"reduction_noise|noise_reduction|noise_child|flamingo_reduction|"
                               r"mini_device_noise|(?:^|_)anc(?:_|$)", name))
        if noise:
            if re.search(r"transparent|transparen|ambient|passthrough", name):
                tags.add("transparency")
            elif re.search(r"adaptive|smart|auto", name):
                tags.add("adaptive_noise")
            elif re.search(r"close|closed|(?:^|_)off(?:_|$)", name):
                tags.add("noise_off")
            else:
                tags.add("noise_cancellation")
            category = category or "feature_noise_control"
        for pattern, tag, assigned in FEATURE_RULES:
            if re.search(pattern, name):
                tags.add(tag)
                category = category or assigned
    return tags, category


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def json_save(path: Path, value: dict) -> None:
    stage = path.with_name(path.name + ".tmp")
    stage.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    stage.replace(path)


def merged_origins(origins: list[dict]) -> list[dict]:
    result = {}
    for origin in origins:
        key = json.dumps({k: v for k, v in origin.items() if k != "feature_tags"}, sort_keys=True)
        if key not in result:
            result[key] = dict(origin)
        elif origin.get("feature_tags"):
            result[key]["feature_tags"] = sorted(set(result[key].get("feature_tags", [])) | set(origin["feature_tags"]))
    return list(result.values())


def references(data: bytes) -> list[str]:
    if not data or (not data.lstrip().startswith(b"<") and not data.startswith(b"\x03\x00")):
        return []
    try:
        _, decoded = decode_xml(data)
        tree = ET.fromstring(decoded)
    except (ET.ParseError, ValueError):
        return []
    return sorted({reference.upper().replace("@0X", "0x") for element in tree.iter()
                   for value in element.attrib.values()
                   for reference in re.findall(r"@0x[0-9A-Fa-f]{8}", value)})


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pillow-path", action="append", default=[])
    args = parser.parse_args()
    manifest = json.loads((ROOT / "assets.json").read_text(encoding="utf-8"))
    inventory = json.loads((ROOT / "inventory.json").read_text(encoding="utf-8"))
    previous_ids = {a["id"] for a in manifest["assets"]}
    previous_report_path = ROOT / "evidence/feature-extraction.json"
    previous_report = json.loads(previous_report_path.read_text(encoding="utf-8")) if previous_report_path.exists() else {}
    initial_count = previous_report.get("initial_asset_count", previous_report.get("previous_asset_count", len(previous_ids)))
    extraction = Extraction(ROOT, load_pillow(args.pillow_path))
    extraction.assets = {a["id"]: a for a in manifest["assets"]}
    old_records = {(r["package"], r["apk_entry"], r.get("parent_entry")): r for r in inventory["entries"]}
    report_sources, report_selected = [], []
    resolved_links = []
    exact = json.loads(NOISE_SELECTION.read_text(encoding="utf-8"))["names"] if NOISE_SELECTION.exists() else {}
    for package, (filename, expected) in INPUTS.items():
        path = ROOT.parent / "apps" / filename
        content = path.read_bytes()
        if sha256(content) != expected:
            raise ValueError("Input APK hash mismatch: " + filename)
        metadata = {"package": package, "version_name": "17.6.5" if package == "com.heytap.headset" else "17.6.3",
                    "apk_sha256": expected}
        source_start = len(extraction.inventory)
        with zipfile.ZipFile(io.BytesIO(content)) as archive:
            mappings = resource_table(archive.read("resources.arsc"))
            filenames = set(archive.namelist())
            by_id = defaultdict(list)
            for entry, origins in mappings.items():
                if entry in filenames:
                    for origin in origins:
                        by_id[origin["resource_id"]].append(entry)
            selection = {}
            queue = deque()
            for entry, origins in mappings.items():
                if entry not in filenames or not all(o["android_type"] in {"drawable", "mipmap", "raw", "color"} for o in origins):
                    continue
                tags, category = classification([o["android_name"] for o in origins], exact)
                if tags:
                    selection[entry] = {"tags": tags, "category": category, "roles": {"named_feature"}}
                    queue.append(entry)
            # A fixed-point pass propagates all feature tags to shared local dependencies.
            processed = {}
            while queue:
                entry = queue.popleft()
                selected = selection[entry]
                state = frozenset(selected["tags"])
                if processed.get(entry) == state:
                    continue
                processed[entry] = state
                raw = archive.read(entry)
                if len(raw) > 64 * 1024 * 1024:
                    raise ValueError("Local resource exceeds its size bound")
                for reference in references(raw) if entry.endswith(".xml") else []:
                    targets = sorted(set(by_id.get(reference, [])))
                    if not targets:
                        continue  # Theme attributes and scalar colors are not standalone artwork.
                    resolved_links.append({"package": package, "from_entry": entry,
                                           "resource_id": reference, "target_entries": targets})
                    for target in targets:
                        target_origins = mappings.get(target, [])
                        if not any(o["android_type"] in {"drawable", "mipmap", "raw", "color"} for o in target_origins):
                            continue
                        current = selection.setdefault(target, {"tags": set(), "category": selected["category"], "roles": set()})
                        before = set(current["tags"])
                        current["tags"].update(selected["tags"])
                        current["roles"].add("local_reference")
                        if current["tags"] != before:
                            queue.append(target)
            for entry, selected in sorted(selection.items()):
                origins = mappings[entry]
                raw = archive.read(entry)
                count_before = len(extraction.inventory)
                handled = False
                if entry.endswith(".xml"):
                    root, decoded = decode_xml(raw)
                    if root in DRAWABLE_ROOTS | {"gradient"}:
                        full_origins = [{**origin, "package": package, "version_name": metadata["version_name"],
                                         "apk_sha256": expected, "apk_entry": entry} for origin in origins]
                        kind = "android_color" if root == "gradient" or all(o["android_type"] == "color" for o in origins) else "android_drawable"
                        item = extraction.asset(raw, kind, selected["category"], origins[0]["android_name"],
                                                full_origins, ".xml", decoded=decoded,
                                                notes=["本地功能引用的颜色或组合绘制配置；子图和颜色依赖单独保留，未将组合静态化为原始图片。"])
                        extraction.inventory.append({"package": package, "version_name": metadata["version_name"],
                            "apk_entry": entry, "parent_entry": None, "kind": kind, "category": selected["category"],
                            "sha256": item["sha256"], "size_bytes": len(raw), "width": None, "height": None,
                            "alpha": None, "frame_count": None, "selected": True,
                            "android_names": [o["android_name"] for o in origins], "origins": full_origins,
                            "asset_id": item["id"], "status": "ok"})
                        handled = True
                if not handled:
                    extraction.inspect(entry, raw, metadata, origins, forced=True, parent_category=selected["category"])
                for record in extraction.inventory[count_before:]:
                    if not record.get("selected"):
                        continue
                    record["feature_tags"] = sorted(selected["tags"])
                    record["feature_roles"] = sorted(selected["roles"])
                    item = extraction.assets[record["asset_id"]]
                    item["feature_tags"] = sorted(set(item.get("feature_tags", [])) | selected["tags"])
                    item["feature_roles"] = sorted(set(item.get("feature_roles", [])) | selected["roles"])
                    for origin in item["origins"]:
                        if origin.get("android_name") in exact:
                            item["notes"] = list(dict.fromkeys(item.get("notes", []) + exact[origin["android_name"]].get("notes", [])))
                    for origin in item["origins"]:
                        if origin["package"] == package and (origin.get("parent_entry") == entry or origin["apk_entry"] == entry):
                            origin["feature_tags"] = sorted(set(origin.get("feature_tags", [])) | selected["tags"])
                    report_selected.append({"package": package, "apk_entry": record["apk_entry"],
                                            "parent_entry": record.get("parent_entry"), "asset_id": item["id"],
                                            "kind": item["kind"], "feature_tags": item["feature_tags"],
                                            "feature_roles": sorted(selected["roles"])})
        if sha256(path.read_bytes()) != expected:
            raise ValueError("Input APK changed during extraction")
        report_sources.append({"package": package, "apk_sha256": expected, "unchanged": True,
                               "selected_records": len(extraction.inventory) - source_start})
    if extraction.errors:
        raise ValueError("Feature extraction failed: " + json.dumps(extraction.errors, ensure_ascii=False))
    for record in extraction.inventory:
        key = record["package"], record["apk_entry"], record.get("parent_entry")
        if key in old_records:
            old = old_records[key]
            old.update({k: v for k, v in record.items() if k != "origins"})
            old["origins"] = merged_origins(old.get("origins", []) + record.get("origins", []))
        else:
            old_records[key] = record
    for link in resolved_links:
        source = old_records.get((link["package"], link["from_entry"], None))
        if source and source.get("asset_id"):
            asset = extraction.assets[source["asset_id"]]
            children = [old_records[(link["package"], target, None)]["asset_id"] for target in link["target_entries"]
                        if (link["package"], target, None) in old_records and old_records[(link["package"], target, None)].get("asset_id")]
            asset["dependencies"] = sorted(set(asset.get("dependencies", [])) | set(children) - {asset["id"]})
    assets = sorted(extraction.assets.values(), key=lambda a: (a["category"], a["title"], a["id"]))
    for asset in assets:
        asset["origins"] = merged_origins(asset["origins"])
    manifest.update(assets=assets, asset_count=len(assets), feature_extracted_at=datetime.now(timezone.utc).isoformat())
    inventory.update(entries=list(old_records.values()), entry_count=len(old_records))
    json_save(ROOT / "assets.json", manifest)
    json_save(ROOT / "inventory.json", inventory)
    sources = json.loads((ROOT / "sources.json").read_text(encoding="utf-8"))
    for source in sources["sources"]:
        records = [r for r in inventory["entries"] if r["package"] == source["package"]]
        source["direct_kind_counts"] = dict(Counter(r["kind"] for r in records if r.get("parent_entry") is None))
        source["nested_kind_counts"] = dict(Counter(r["kind"] for r in records if r.get("parent_entry")))
    sources["feature_scope_updated_at"] = datetime.now(timezone.utc).isoformat()
    json_save(ROOT / "sources.json", sources)
    columns = ["package", "version_name", "apk_entry", "parent_entry", "kind", "category", "sha256",
               "size_bytes", "width", "height", "alpha", "frame_count", "selected", "asset_id", "status", "android_names", "feature_tags"]
    with (ROOT / "inventory.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=columns, extrasaction="ignore")
        writer.writeheader()
        for record in inventory["entries"]:
            row = dict(record)
            row["android_names"] = ";".join(row.get("android_names", []))
            row["feature_tags"] = ";".join(row.get("feature_tags", []))
            writer.writerow(row)
    tagged = [a for a in assets if a.get("feature_tags")]
    report = {"schema_version": 1, "observed_at": datetime.now(timezone.utc).isoformat(),
              "input_sources": report_sources, "network_requests": 0, "adb_operations": 0,
              "previous_asset_count": len(previous_ids), "new_unique_assets": len({a["id"] for a in assets} - previous_ids),
              "initial_asset_count": initial_count, "new_unique_assets_total": len(assets) - initial_count,
              "asset_count": len(assets), "tagged_asset_count": len(tagged),
              "feature_counts": dict(Counter(tag for a in tagged for tag in a["feature_tags"])),
              "tagged_kind_counts": dict(Counter(a["kind"] for a in tagged)),
              "selected_records": report_selected,
              "reference_links": list({json.dumps(v, sort_keys=True): v for v in resolved_links}.values()),
              "notes": ["资源名与本地XML引用是功能标签依据；不声明某个型号或运行状态支持该功能。",
                        "现有资产、远程包和型号关联保留；仅新增本地文件及功能标签。"]}
    json_save(ROOT / "evidence/feature-extraction.json", report)
    print(json.dumps({k: v for k, v in report.items() if k not in {"selected_records", "reference_links", "notes"}}, ensure_ascii=False))


if __name__ == "__main__":
    main()
