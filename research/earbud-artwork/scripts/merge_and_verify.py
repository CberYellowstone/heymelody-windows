"""Merge verified remote artwork and validate the research without network or ADB."""
from __future__ import annotations

import argparse
import base64
from collections import Counter
from contextlib import ExitStack
import csv
from datetime import datetime, timezone
import hashlib
import io
import json
from pathlib import Path
import re
import zipfile

from research_runtime import bootstrap

ROOT = Path(__file__).resolve().parents[1]
INPUTS = {
    "com.heytap.headset": ("欢律.apk", "eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4"),
    "com.oplus.melody": ("无线耳机_17.6.3.apk", "c1149df9e24ed85bf84f7c81c712cee794299d2b95057c6e221fb547f2717059"),
}
VISUAL_KINDS = {"bitmap", "svg", "android_vector", "android_animated_vector", "lottie", "video", "model"}


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def local_path(relative: str) -> Path:
    candidate = Path(relative)
    if candidate.is_absolute():
        raise ValueError("Manifest paths must be relative")
    result = (ROOT / candidate).resolve()
    if not result.is_relative_to(ROOT):
        raise ValueError("Manifest path leaves the research directory")
    return result


def load(relative: str) -> dict:
    return json.loads(local_path(relative).read_text(encoding="utf-8"))


def save(relative: str, value: dict) -> None:
    path = local_path(relative)
    stage = path.with_name(path.name + ".tmp")
    stage.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    stage.replace(path)


def unique(values: list) -> list:
    result = []
    for value in values:
        if value not in result:
            result.append(value)
    return result


def merge_assets(current: dict, remote: dict) -> dict:
    merged = {asset["sha256"]: asset for asset in current["assets"]}
    if len(merged) != len(current["assets"]):
        raise ValueError("Main asset manifest contains duplicate hashes")
    for incoming in remote.get("assets", []):
        sha = incoming["sha256"]
        if sha not in merged:
            merged[sha] = incoming
            continue
        item = merged[sha]
        item["origins"] = unique(item["origins"] + incoming["origins"])
        item["model_ids"] = sorted(set(item.get("model_ids", [])) | set(incoming.get("model_ids", [])))
        item["notes"] = unique(item.get("notes", []) + incoming.get("notes", []))
        if incoming["model_status"] == "confirmed":
            item["model_status"] = "confirmed"
    for item in merged.values():
        colors = sorted({origin["color"] for origin in item["origins"] if origin.get("color") is not None})
        item["color_codes"] = colors
        item["color"] = colors[0] if len(colors) == 1 else None
    current["assets"] = sorted(merged.values(), key=lambda a: (a["category"], a["title"], a["id"]))
    current["asset_count"] = len(current["assets"])
    current["merged_at"] = datetime.now(timezone.utc).isoformat()
    return current


def update_models(document: dict, assets: list, evidence: dict) -> None:
    names = {m["product_id"]: m for m in document["models"]}
    for asset in assets:
        asset["model_names"] = [names[pid]["name"] for pid in asset.get("model_ids", [])]
        asset["model_brands"] = sorted({names[pid]["brand"] for pid in asset.get("model_ids", [])})
    for model in document["models"]:
        pid = model["product_id"]
        associated = [a for a in assets if a["model_status"] == "confirmed" and pid in a.get("model_ids", [])]
        visual = [a for a in associated if a["kind"] in VISUAL_KINDS]
        model["resource_asset_ids"] = sorted(a["id"] for a in associated)
        model["artwork_asset_ids"] = sorted(a["id"] for a in visual)
        probes = [p for p in evidence.get("probes", []) if
                  p.get("request_body", {}).get("productId", p.get("productId")) == pid]
        model["resource_probe_cases"] = unique([p.get("case_id") for p in probes if p.get("case_id")])
        control = [p for p in probes if p.get("endpoint") == "v1/earphone/resource/control"]
        model["queried_color_codes"] = sorted({int(p.get("request_body", {}).get("color", p.get("color")))
                                               for p in control})
        model["artwork_color_codes"] = sorted({o["color"] for a in visual for o in a["origins"]
                                              if o.get("product_id") == pid and o.get("color") is not None})
        if not model["in_earphone_scope"]:
            model["artwork_status"] = "out_of_scope"
            model["status_reason"] = "配置为音箱；保留索引，未列入耳机美术提取范围。"
        elif visual:
            model["artwork_status"] = "confirmed"
            model["status_reason"] = "服务端响应产品ID吻合、资源包SHA256通过；仅确认已取得的数字配色。"
        elif control:
            model["artwork_status"] = "not_obtained"
            if any(p.get("http_status") == 200 and p.get("code") == 0 for p in control):
                model["status_reason"] = "已查询有证据的默认配色；未取得可通过型号和哈希校验的外观包。"
            else:
                model["status_reason"] = "默认配色请求未成功；保留响应证据，未扩大请求范围。"
        else:
            model["artwork_status"] = "not_obtained"
            model["status_reason"] = "APK内无型号专属映射；未查询未知默认配色，通用外观不绑定产品ID。"
    document["updated_at"] = datetime.now(timezone.utc).isoformat()


def save_model_csv(document: dict) -> None:
    fields = ["product_id", "name", "brand", "type", "aliases", "in_earphone_scope", "sources",
              "default_color_codes", "support_3d_declared", "artwork_status", "artwork_color_codes",
              "queried_color_codes", "artwork_asset_count", "status_reason"]
    with local_path("models.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        for model in document["models"]:
            row = {f: model.get(f, "") for f in fields}
            row.update(aliases=" | ".join(model["aliases"]),
                       sources=" | ".join(s["package"] for s in model["sources"]),
                       default_color_codes=" | ".join(sorted({str(s["default_color_code"]) for s in model["sources"]
                                                              if s["default_color_code"] is not None})),
                       support_3d_declared=any(s["support_3d_declared"] for s in model["sources"]),
                       artwork_color_codes=" | ".join(map(str, model.get("artwork_color_codes", []))),
                       queried_color_codes=" | ".join(map(str, model.get("queried_color_codes", []))),
                       artwork_asset_count=len(model.get("artwork_asset_ids", [])))
            writer.writerow(row)


def validate(assets: list, models: dict, check_gallery: bool) -> dict:
    bootstrap("PIL")
    from PIL import Image

    model_map = {m["product_id"]: m for m in models["models"]}
    asset_map = {a["id"]: a for a in assets}
    if len(model_map) != len(models["models"]) or len(asset_map) != len(assets):
        raise ValueError("Duplicate model or asset identifiers")
    counts = Counter()
    probe_archives = {}
    for record in load("evidence/server-probes.json").get("probes", []):
        relative = record.get("resource_get", {}).get("saved_package")
        if relative:
            relative = relative.replace("\\", "/").removeprefix("research/earbud-artwork/")
            probe_archives.setdefault(relative, []).append(record)
    with ExitStack() as stack:
        apks = {}
        for package, (filename, expected) in INPUTS.items():
            content = (ROOT.parent / filename).read_bytes()
            if digest(content) != expected:
                raise ValueError("Supplied APK changed: " + filename)
            apks[package] = stack.enter_context(zipfile.ZipFile(io.BytesIO(content)))
        archives = {}
        archive_hashes = {}
        for asset in assets:
            dependencies = asset.get("dependencies", [])
            if (len(dependencies) != len(set(dependencies)) or asset["id"] in dependencies
                    or not set(dependencies).issubset(asset_map)):
                raise ValueError("Invalid local drawing dependency: " + asset["id"])
            if len(asset.get("feature_tags", [])) != len(set(asset.get("feature_tags", []))):
                raise ValueError("Duplicate feature tags: " + asset["id"])
            counts["verified_dependency_links"] += len(dependencies)
            raw = local_path(asset["original_path"]).read_bytes()
            if digest(raw) != asset["sha256"] or asset["id"] != asset["sha256"]:
                raise ValueError("Original asset hash mismatch: " + asset["id"])
            for key in ("preview_path", "decoded_path", "preview_svg_path"):
                if asset.get(key) and not local_path(asset[key]).is_file():
                    raise ValueError("Missing derived artifact: " + asset[key])
            if asset["kind"] == "bitmap":
                image = Image.open(io.BytesIO(raw))
                image.load()
                if image.size != (asset["width"], asset["height"]):
                    raise ValueError("Image dimensions mismatch")
                alpha = "A" in image.getbands() or "transparency" in image.info
                if alpha != asset["alpha"]:
                    raise ValueError("Image alpha metadata mismatch")
                counts["decoded_bitmaps"] += 1
            if asset.get("preview_path"):
                preview = Image.open(local_path(asset["preview_path"]))
                preview.load()
                counts["decoded_previews"] += 1
            proven_model_ids = set()
            for origin in asset["origins"]:
                if origin["apk_sha256"] != INPUTS[origin["package"]][1]:
                    raise ValueError("Origin APK identity mismatch")
                if origin.get("remote_archive"):
                    archive_path = origin["remote_archive"]
                    if archive_path not in archives:
                        data = local_path(archive_path).read_bytes()
                        if digest(data) != origin["package_sha256"]:
                            raise ValueError("Remote archive hash mismatch")
                        archive_hashes[archive_path] = digest(data)
                        archives[archive_path] = stack.enter_context(zipfile.ZipFile(io.BytesIO(data)))
                    if archive_hashes[archive_path] != origin["package_sha256"]:
                        raise ValueError("Conflicting archive hashes in origins")
                    member = archives[archive_path].read(origin["zip_member"])
                    if digest(member) != origin["member_sha256"]:
                        raise ValueError("Remote ZIP member hash mismatch")
                    if not asset.get("derived_redacted") and member != raw:
                        raise ValueError("Remote extraction changed original bytes")
                    if origin["product_id"] not in model_map or origin["product_id"] not in asset["model_ids"]:
                        raise ValueError("Remote model association mismatch")
                    if asset["model_status"] == "confirmed":
                        matching_proof = [p for p in probe_archives.get(archive_path, []) if
                                          p.get("model_attribution") == "request_and_response_productId" and
                                          p.get("http_status") == 200 and p.get("code") in (0, 200) and
                                          p.get("request_body", {}).get("productId") == origin["product_id"] and
                                          int(p["request_body"]["color"]) == origin["color"] and
                                          p.get("endpoint") == origin["endpoint"] and
                                          p["resource_get"].get("sha256_matches") and
                                          p["resource_get"].get("sha256") == archive_hashes[archive_path]]
                        if not matching_proof:
                            raise ValueError("Confirmed model association lacks successful response evidence")
                        proven_model_ids.add(origin["product_id"])
                    counts["remote_origins"] += 1
                else:
                    archive = apks[origin["package"]]
                    parent = origin.get("parent_entry")
                    if not parent:
                        member = archive.read(origin["apk_entry"])
                    else:
                        parent_bytes = archive.read(parent)
                        if zipfile.is_zipfile(io.BytesIO(parent_bytes)):
                            with zipfile.ZipFile(io.BytesIO(parent_bytes)) as nested:
                                member = nested.read(origin["apk_entry"])
                        else:
                            lottie = json.loads(parent_bytes)
                            image_id = Path(origin["apk_entry"]).stem
                            embedded = next(a for a in lottie["assets"] if a.get("id") == image_id)
                            member = base64.b64decode(embedded["p"].split(",", 1)[1], validate=True)
                    if member != raw:
                        raise ValueError("Local extraction changed original bytes")
                    counts["local_origins"] += 1
            if asset["model_status"] == "generic" and asset.get("model_ids"):
                raise ValueError("Generic resource bound to a model")
            if asset["model_status"] == "confirmed":
                if not asset.get("model_ids") or not any(o.get("remote_archive") for o in asset["origins"]):
                    raise ValueError("Confirmed association has no remote evidence")
                if set(asset["model_ids"]) != proven_model_ids:
                    raise ValueError("Asset model IDs differ from its verified response associations")
        counts["verified_remote_archives"] = len(archives)
    visual_map = {a["id"]: a for a in assets if a["model_status"] == "confirmed" and a["kind"] in VISUAL_KINDS}
    for model in models["models"]:
        for asset_id in model.get("artwork_asset_ids", []):
            if asset_id not in visual_map or model["product_id"] not in visual_map[asset_id]["model_ids"]:
                raise ValueError("Model index association mismatch")
    tagged = [a for a in assets if a.get("feature_tags")]
    if tagged:
        feature_evidence = load("evidence/feature-extraction.json")
        feature_counts = dict(Counter(tag for a in tagged for tag in a["feature_tags"]))
        if (feature_evidence["asset_count"] != len(assets)
                or feature_evidence["tagged_asset_count"] != len(tagged)
                or feature_evidence["feature_counts"] != feature_counts
                or feature_evidence["tagged_kind_counts"] != dict(Counter(a["kind"] for a in tagged))
                or feature_evidence["new_unique_assets_total"] != len(assets) - feature_evidence["initial_asset_count"]):
            raise ValueError("Local feature evidence statistics are stale")
        for selection in feature_evidence["selected_records"]:
            asset = asset_map.get(selection["asset_id"])
            if asset is None or not set(selection["feature_tags"]).issubset(asset.get("feature_tags", [])):
                raise ValueError("Local feature selection differs from asset tags")
            if not any(not origin.get("remote_archive")
                       and origin["package"] == selection["package"]
                       and origin["apk_entry"] == selection["apk_entry"]
                       and origin.get("parent_entry") == selection.get("parent_entry") for origin in asset["origins"]):
                raise ValueError("Local feature selection lacks its original APK source")
        counts["verified_feature_selections"] = len(feature_evidence["selected_records"])
    if check_gallery:
        page = local_path("index.html").read_text(encoding="utf-8")
        match = re.search(r'<script[^>]+id="gallery-data"[^>]*>([\s\S]*?)</script>', page)
        if not match:
            # Support a differently named inline application/json data block.
            match = re.search(r'<script[^>]+type="application/json"[^>]*>([\s\S]*?)</script>', page)
        if not match:
            raise ValueError("Offline gallery has no embedded manifest")
        embedded = json.loads(match.group(1))
        embedded_assets = embedded["assets"] if isinstance(embedded, dict) else embedded
        if {a["id"]: a for a in embedded_assets} != {a["id"]: a for a in assets}:
            raise ValueError("Gallery asset metadata is stale")
        if re.search(r'<script[^>]+src=', page) or re.search(r'\bfetch\s*\(', page):
            raise ValueError("Gallery unexpectedly depends on an external script or request")
        gallery = load("gallery-build.json")
        feature_sheets = gallery.get("feature_contact_sheets", [])
        sheets = gallery["contact_sheets"] + gallery.get("confirmed_contact_sheets", []) + feature_sheets
        for sheet in sheets:
            Image.open(local_path(sheet["path"])).load()
            if not set(sheet["asset_ids"]).issubset(asset_map):
                raise ValueError("Contact sheet references absent assets")
            if any(not asset_map[asset_id].get("preview_path") for asset_id in sheet["asset_ids"]):
                raise ValueError("Contact sheet references an asset without a preview")
            for asset_id, pid in zip(sheet["asset_ids"], sheet.get("representative_models", [])):
                if asset_id not in visual_map or pid not in visual_map[asset_id]["model_ids"]:
                    raise ValueError("Contact sheet representative uses the wrong model image")
        representatives = {pid for sheet in gallery.get("confirmed_contact_sheets", [])
                           for pid in sheet.get("representative_models", [])}
        if representatives != {m["product_id"] for m in models["models"] if m["artwork_status"] == "confirmed"}:
            raise ValueError("Confirmed contact sheets do not cover every acquired model")
        feature_visuals = {a["id"] for a in assets if a.get("preview_path") and
                           (a.get("feature_tags") or str(a.get("category", "")).startswith("feature_"))}
        featured = [asset_id for sheet in feature_sheets for asset_id in sheet["asset_ids"]]
        if len(featured) != len(set(featured)) or set(featured) != feature_visuals:
            raise ValueError("Feature contact sheets do not cover the local feature previews exactly")
        counts["decoded_contact_sheets"] = len(sheets)
        counts["feature_contact_sheets"] = len(feature_sheets)
        counts["feature_previews_in_contact_sheets"] = len(featured)
        counts["offline_gallery_verified"] = 1
    return {
        "schema_version": 1, "validated_at": datetime.now(timezone.utc).isoformat(),
        "passed": True, "input_apk_hashes_unchanged": True,
        "asset_count": len(assets), "model_count": len(models["models"]),
        "earphone_model_count": sum(m["in_earphone_scope"] for m in models["models"]),
        "confirmed_model_count": sum(m["artwork_status"] == "confirmed" for m in models["models"]),
        "confirmed_model_counts_by_brand": dict(Counter(m["brand"] for m in models["models"] if m["artwork_status"] == "confirmed")),
        "asset_kinds": dict(Counter(a["kind"] for a in assets)),
        "asset_statuses": dict(Counter(a["model_status"] for a in assets)),
        "tagged_asset_count": sum(bool(a.get("feature_tags")) for a in assets),
        "feature_counts": dict(Counter(tag for a in assets for tag in a.get("feature_tags", []))),
        "confirmed_visual_asset_count": len(visual_map),
        "checks": dict(counts), "gallery_checked": check_gallery,
        "network_requests": 0, "adb_operations": 0,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--merge", action="store_true", help="Merge remote-assets.json and update model links before validation")
    parser.add_argument("--check-gallery", action="store_true", help="Also check final embedded gallery data and contact sheets")
    args = parser.parse_args()
    document = load("assets.json")
    models = load("models.json")
    if args.merge:
        document = merge_assets(document, load("remote/remote-assets.json"))
        update_models(models, document["assets"], load("evidence/server-probes.json"))
    validation = validate(document["assets"], models, args.check_gallery)
    if args.merge:
        save("assets.json", document)
        save("models.json", models)
        save_model_csv(models)
    save("evidence/validation.json", validation)
    print(json.dumps(validation, ensure_ascii=False))


if __name__ == "__main__":
    main()
