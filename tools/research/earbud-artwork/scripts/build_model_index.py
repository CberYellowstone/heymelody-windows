"""Authenticate APK model lists and export only artwork-relevant model metadata."""
from __future__ import annotations

import argparse
import csv
import gzip
import hashlib
import io
import json
from pathlib import Path
import zipfile

from research_runtime import bootstrap, checked_output, research_path

INPUTS = (
    ("无线耳机_17.6.3.apk", "com.oplus.melody", "17.6.3", "17006003", "res/AQ.json", "whiteList"),
    ("欢律.apk", "com.heytap.headset", "17.6.5", "117006005", "res/30.json", "compatWhiteList"),
)
EXPECTED = {
    "com.oplus.melody": "c1149df9e24ed85bf84f7c81c712cee794299d2b95057c6e221fb547f2717059",
    "com.heytap.headset": "eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4",
}


def decrypt_model_list(archive: zipfile.ZipFile, member: str) -> tuple[dict, dict]:
    bootstrap("cryptography")
    from cryptography.exceptions import InvalidTag
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    from cryptography.hazmat.primitives.serialization import Encoding, pkcs7

    encrypted = archive.read(member)
    for name in archive.namelist():
        if not name.startswith("META-INF/") or not name.endswith((".RSA", ".DSA")):
            continue
        for cert in pkcs7.load_der_pkcs7_certificates(archive.read(name)):
            certificate_hash = hashlib.sha256(cert.public_bytes(Encoding.DER)).digest()
            try:
                plaintext = AESGCM(certificate_hash).decrypt(encrypted[:12], encrypted[12:], None)
            except InvalidTag:
                continue
            with gzip.GzipFile(fileobj=io.BytesIO(plaintext)) as stream:
                decoded = stream.read(8 * 1024 * 1024 + 1)
            if len(decoded) > 8 * 1024 * 1024:
                raise ValueError("Model configuration exceeds its limit")
            return json.loads(decoded), {
                "archive_member": member,
                "certificate_member": name,
                "certificate_sha256": certificate_hash.hex(),
                "plaintext_sha256": hashlib.sha256(decoded).hexdigest(),
                "authenticated": True,
            }
    raise ValueError("Model configuration failed certificate-bound AES-GCM authentication")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input-dir", type=Path, default=Path(__file__).resolve().parents[4] / "research/apps")
    parser.add_argument("--output", type=Path, default=Path(__file__).resolve().parents[4] / "research/earbud-artwork")
    args = parser.parse_args()
    output = checked_output(args.output)
    merged: dict[str, dict] = {}
    evidence = []
    for filename, package, version, code, member, list_key in INPUTS:
        path = research_path(args.input_dir) / filename
        original_hash = hashlib.sha256(path.read_bytes()).hexdigest()
        if original_hash != EXPECTED[package]:
            raise ValueError(f"Unexpected input APK hash for {package}; re-investigate its resource mapping")
        with zipfile.ZipFile(path) as archive:
            payload, provenance = decrypt_model_list(archive, member)
        models = payload[list_key]
        for model in models:
            pid = model["id"]
            item = merged.setdefault(pid, {
                "product_id": pid,
                "name": model.get("name"),
                "brand": model.get("brand"),
                "type": model.get("type"),
                "aliases": [],
                "in_earphone_scope": model.get("type") != "S",
                "sources": [],
                "artwork_status": "not_obtained",
                "artwork_asset_ids": [],
                "status_reason": "APK内未发现型号专属映射；通用外观不绑定产品ID。",
            })
            item["aliases"] = sorted(set(item["aliases"]) | {
                c["name"] for c in model.get("children", []) if c.get("name")
            })
            item["sources"].append({
                "package": package, "version_name": version, "version_code": code,
                "apk_sha256": original_hash,
                "whitelist_entry": member,
                "support_3d_declared": model.get("function", {}).get("support3DModel") == 1,
                "default_color_code": model.get("defaultColor"),
                "default_color_name": None,
            })
            if model.get("type") == "S":
                item["artwork_status"] = "out_of_scope"
                item["status_reason"] = "配置为音箱；保留索引，未列入耳机美术提取范围。"
        end_hash = hashlib.sha256(path.read_bytes()).hexdigest()
        if end_hash != original_hash:
            raise RuntimeError("Input APK changed during the research")
        evidence.append({
            "package": package, "version_name": version, "version_code": code,
            "apk_sha256": original_hash, "after_sha256": end_hash,
            "model_count": len(models), "whitelist": provenance,
        })
    items = sorted(merged.values(), key=lambda v: (v["brand"], v["name"], v["product_id"]))
    document = {
        "schema_version": 1,
        "scope": "OPPO, OnePlus, realme; exclude speaker artwork",
        "models": items,
        "sources": evidence,
        "notes": [
            "default_color_code为配置中的数字，未推断颜色名称。",
            "support_3d_declared是厂商配置声明，并非已取得3D网格的证据。",
            "独立产品ID保留；市场别名不会合并成未经证实的共享资源映射。",
        ],
    }
    (output / "models.json").write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    with (output / "models.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["product_id", "name", "brand", "type", "aliases", "in_earphone_scope", "sources", "default_color_codes", "support_3d_declared", "artwork_status", "status_reason"])
        for m in items:
            writer.writerow([
                m["product_id"], m["name"], m["brand"], m["type"], " | ".join(m["aliases"]),
                m["in_earphone_scope"], " | ".join(s["package"] for s in m["sources"]),
                " | ".join(sorted({str(s["default_color_code"]) for s in m["sources"] if s["default_color_code"] is not None})),
                any(s["support_3d_declared"] for s in m["sources"]), m["artwork_status"], m["status_reason"],
            ])
    print(json.dumps({"models": len(items), "earphone_models": sum(m["in_earphone_scope"] for m in items), "source_hashes_unchanged": True}))


if __name__ == "__main__":
    main()
