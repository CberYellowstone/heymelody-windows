#!/usr/bin/env python3
"""Build release-owned offline resource bundles from pinned, verified local research.

Never contacts the network or changes original evidence. Authentication material is
intentionally retained for this release; only counts and hashes are printed.
"""
from __future__ import annotations
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import sys
import tempfile
import zipfile

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
DEST = ROOT / "src/HeyMelody.Resources/Bundles"
RUN = ROOT / "research/online-whitelist/runs/20261007T095049.319796Z"
ART = ROOT / "research/earbud-artwork"
MODEL_RUN = ROOT / "research/online-artwork/runs/20261007T101708.195649Z"
SOURCES = {"com.heytap.headset": (0, "heymelody", "欢律.apk", "eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4", 117006004)}

def digest(raw): return hashlib.sha256(raw).hexdigest()
def dump(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
def copy_asset(path):
    raw = path.read_bytes()
    name = digest(raw) + path.suffix.lower()
    target = DEST / "data" / name
    target.parent.mkdir(parents=True, exist_ok=True)
    if not target.exists() or target.read_bytes() != raw: target.write_bytes(raw)
    return "data/" + name, digest(raw), len(raw)

def build():
    spec = importlib.util.spec_from_file_location("bundle_auth", ROOT / "tools/research/online-whitelist/scripts/fetch_whitelist.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    keys, provenance, assets = {}, [], []
    for package, (source, name, apk_name, apk_hash, version) in SOURCES.items():
        apk = ROOT / "research/apps" / apk_name
        if digest(apk.read_bytes()) != apk_hash: raise ValueError("Pinned APK hash mismatch")
        with zipfile.ZipFile(apk) as archive:
            key, _ = module.authenticate_material(archive, module.SPECS[package])
            keys["HeyMelodyKey"] = key.decode("utf-8")
        raw = (RUN / package / "builtin.json").read_bytes()
        (DEST / (name + ".json")).parent.mkdir(parents=True, exist_ok=True)
        (DEST / (name + ".json")).write_bytes(raw)
        provenance.append({"package": package, "apkSha256": apk_hash,
                           "catalogSha256": digest(raw), "contentVersion": version})
    inventory = json.loads((ART / "assets.json").read_text(encoding="utf-8"))
    for entry in inventory["assets"]:
        if entry.get("model_status") == "excluded": continue
        if not any(o.get("package") in SOURCES for o in entry.get("origins", [])): continue
        original = ART / entry["original_path"]
        if not original.is_file(): continue
        # Preserve originals as well as renderable vector previews.
        original_path, original_hash, original_size = copy_asset(original)
        rendered = ART / entry["preview_path"] if entry.get("preview_path") else original
        if not rendered.is_file(): rendered = original
        path, sha, length = copy_asset(rendered)
        kind = entry["kind"]
        if rendered.suffix.lower() in {".png", ".jpg", ".jpeg", ".webp"}: kind = "bitmap"
        for origin in entry.get("origins", []):
            package = origin.get("package")
            if package not in SOURCES: continue
            ids = entry.get("model_ids", [])
            role = {"case": "box", "earpiece": "left" if "left" in str(origin.get("zip_member", entry["title"])).lower() else "right",
                    "capsule": "capsule", "device_appearance": "detail"}.get(entry["category"], entry["category"])
            qualifiers = origin.get("qualifiers", {})
            source_path, source_hash, source_length = path, sha, length
            if entry["kind"] == "lottie":
                # The same animation JSON can reference different image bytes in each APK.
                # Keep dependencies within this source/animation package, not a shared image folder.
                animation_root = DEST / "data/lottie" / str(SOURCES[package][0]) / original_hash
                animation_root.mkdir(parents=True, exist_ok=True)
                animation_file = animation_root / "animation.json"
                animation_file.write_bytes(original.read_bytes())
                source_path = animation_file.relative_to(DEST).as_posix()
                source_hash, source_length = original_hash, original_size
                data = json.loads(original.read_text(encoding="utf-8"))
                with zipfile.ZipFile(ROOT / "research/apps" / SOURCES[package][2]) as archive:
                    for image in data.get("assets", []):
                        relative = str(image.get("u", "")) + str(image.get("p", ""))
                        if not relative or relative.startswith("data:"): continue
                        if ":" in relative or ".." in Path(relative).parts: raise ValueError("Invalid animation image reference")
                        member = next((item for item in [relative, "assets/" + relative] if item in archive.namelist()), None)
                        if member is None: raise ValueError("Missing external animation image")
                        target = animation_root / relative
                        target.parent.mkdir(parents=True, exist_ok=True)
                        target.write_bytes(archive.read(member))
            row = {"productIds": ids, "color": entry.get("color"),
                   "kind": kind, "role": role, "title": entry["title"], "androidName": origin.get("android_name") or "",
                   "night": bool(qualifiers.get("night", False)), "path": source_path, "sha256": source_hash, "length": source_length,
                   "originalPath": original_path, "originalSha256": original_hash}
            if row not in assets: assets.append(row)
    # Verified cloud model packages are preinstalled art, never control-catalog overlays.
    for product, color, package_hash in [("067410", 2, "b4763c5f9f790b0859131807324bf81c4a1855489fc087e715f673de848119bf"),
                                          ("068C10", 1, "068676a8bb633f35e741f2d0dc7eb919afcb03e89f5c6839b41adbec53287bb1")]:
        glb = MODEL_RUN / "model-inspection" / package_hash / "extracted/res/raw/detail_model/model.glb"
        if not glb.is_file():
            glb = ROOT / "research/online-artwork/runs/20261007T101352.199515Z/model-inspection" / package_hash / "extracted/res/raw/detail_model/model.glb"
        if not glb.is_file(): raise ValueError("Verified model missing")
        path, sha, length = copy_asset(glb)
        for source in (0,):
            assets.append({"productIds": [product], "color": color, "kind": "model", "role": "model",
                           "title": product + " official model", "androidName": "", "night": False, "path": path, "sha256": sha, "length": length})
    for name, role in [("x4-pods-with-earphones.png", "detail"), ("x4-both-earphones.png", "capsule")]:
        path, sha, length = copy_asset(ROOT / "research/screenshots" / name)
        for source in (0,):
            assets.append({"productIds": ["06EC10"], "color": None, "kind": "bitmap", "role": role,
                           "title": "OPPO Enco X4", "androidName": "", "night": False, "path": path, "sha256": sha, "length": length})
    scanner_hash = "4b38f26393dffc414b6c9d35f785c1c96ee6f6f56f500fbe09edc52b2b970270"
    scanner = ART / "originals" / scanner_hash / "original.png"
    raw = scanner.read_bytes()
    if digest(raw) != scanner_hash: raise ValueError("Scanner image hash mismatch")
    scanner_path = "data/images/heymelody_app_scanning_blue.png"
    (DEST / scanner_path).parent.mkdir(parents=True, exist_ok=True)
    (DEST / scanner_path).write_bytes(raw)
    dump(DEST / "supplemental.json", [{"package": "com.heytap.headset", "role": "deviceScan",
         "path": scanner_path, "sha256": scanner_hash, "length": len(raw)}])
    dump(DEST / "default-keys.json", keys)
    dump(DEST / "provenance.json", provenance)
    dump(DEST / "artwork.json", assets)
    print(f"Bundled {len(provenance)} catalog and {len(assets)} HeyMelody artwork entries; authentication material not printed.")

def main():
    global DEST
    output = DEST.resolve()
    if not output.is_relative_to(ROOT.resolve()) or output.name != "Bundles":
        raise ValueError("Bundle output escaped the repository")
    # All extraction and validation finishes before replacing the current bundle.
    with tempfile.TemporaryDirectory(prefix="heymelody-bundle-") as temporary:
        DEST = Path(temporary)
        try:
            build()
            for child in output.iterdir():
                if not child.resolve().is_relative_to(output): raise ValueError("Unexpected output link")
                if child.is_dir(): shutil.rmtree(child)
                else: child.unlink()
            shutil.copytree(DEST, output, dirs_exist_ok=True)
        finally:
            DEST = output

if __name__ == "__main__": main()
