#!/usr/bin/env python3
"""Read local OPPO APK metadata and authenticated built-in model configuration.

No APK code is executed, no Bluetooth calls are made, and no network is used.
The output directory is local evidence; do not redistribute the original APK or
the full recovered proprietary configuration as an application dependency.
"""

from __future__ import annotations

import argparse
import collections
import csv
import gzip
import hashlib
import io
import json
from pathlib import Path
import zipfile

from androguard.core.axml import ARSCParser, AXMLPrinter
from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.serialization import Encoding, pkcs7
from loguru import logger


MAX_MEMBER_SIZE = 32 * 1024 * 1024
MAX_JSON_SIZE = 8 * 1024 * 1024
ANDROID_NS = "{http://schemas.android.com/apk/res/android}"
WHITELIST_NAMES = ("heymelody_app_whitelist", "melody_app_whitelist")
CAPABILITY_KEYS = (
    "batteryInfo", "noiseReductionMode", "equalizer", "equalizerMode",
    "customEqualizer", "customEqFrequency", "highToneQuality",
    "sendHostCapabilities", "multiDevicesConnect", "spatialTypes",
    "spatialHiResMutex", "gameMode", "leFilterFunctions",
)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def read_member(archive: zipfile.ZipFile, name: str) -> bytes:
    # All entries stay in memory; archive names never become output paths.
    entry = archive.getinfo(name)
    if entry.file_size > MAX_MEMBER_SIZE:
        raise ValueError(f"Archive member exceeds size limit: {name}")
    return archive.read(entry)


def decrypt_whitelist(archive: zipfile.ZipFile, encrypted: bytes):
    if len(encrypted) < 28:
        raise ValueError("Whitelist is too short for a 12-byte nonce and GCM tag")
    for member in archive.namelist():
        if not member.startswith("META-INF/") or not member.endswith((".RSA", ".DSA")):
            continue
        for cert in pkcs7.load_der_pkcs7_certificates(read_member(archive, member)):
            # APK RepackagingDetector hashes the encoded X.509 certificate.
            cert_der = cert.public_bytes(Encoding.DER)
            key = hashlib.sha256(cert_der).digest()
            try:
                compressed = AESGCM(key).decrypt(encrypted[:12], encrypted[12:], None)
            except InvalidTag:
                continue
            with gzip.GzipFile(fileobj=io.BytesIO(compressed)) as stream:
                plaintext = stream.read(MAX_JSON_SIZE + 1)
            if len(plaintext) > MAX_JSON_SIZE:
                raise ValueError("Decompressed whitelist exceeds size limit")
            payload = json.loads(plaintext.decode("utf-8"))
            return plaintext, payload, {
                "certificate_member": member,
                "certificate_sha256": sha256(cert_der),
                "scheme": "AES-256-GCM; nonce[0:12]; ciphertext+tag[12:]; gzip; UTF-8 JSON",
                "authenticated": True,
            }
    raise ValueError("No APK signing certificate authenticated the whitelist")


def inspect(apk: Path, output: Path, include_plaintext: bool, dump_classes: tuple[str, ...] = ()) -> dict:
    original_hash = sha256(apk.read_bytes())
    with zipfile.ZipFile(apk) as archive:
        if len(archive.namelist()) != len(set(archive.namelist())):
            raise ValueError("Ambiguous archive contains duplicate entry names")
        manifest = AXMLPrinter(read_member(archive, "AndroidManifest.xml")).get_xml_obj()
        package = manifest.attrib["package"]
        resources = ARSCParser(read_member(archive, "resources.arsc"))
        candidates = []
        for resource_name in WHITELIST_NAMES:
            rid = resources.get_res_id_by_key(package, "raw", resource_name)
            if rid:
                for config, member in resources.get_resolved_res_configs(rid):
                    candidates.append((resource_name, rid, config.get_qualifier(), member))
        if len(candidates) != 1:
            raise ValueError(f"Expected one built-in whitelist; found {len(candidates)}")
        name, rid, qualifier, member = candidates[0]
        encrypted = read_member(archive, member)
        plaintext, payload, certificate = decrypt_whitelist(archive, encrypted)
        list_key = "compatWhiteList" if payload.get("compatWhiteList") else "whiteList"
        models = payload.get(list_key)
        if not isinstance(models, list) or not all(isinstance(v, dict) for v in models):
            raise ValueError("Missing model configuration list")
        model_ids = [m["id"] for m in models]
        if len(model_ids) != len(set(model_ids)):
            raise ValueError("Model configuration contains duplicate product IDs")
        inventory = [{
            "id": m["id"], "name": m.get("name"), "brand": m.get("brand"),
            "children": m.get("children", []), "type": m.get("type"),
            "supportSpp": m.get("supportSpp"), "uuid": m.get("uuid"),
            "minVersion": m.get("minVersion"),
            "capabilities": {k: m.get("function", {})[k] for k in CAPABILITY_KEYS
                             if k in m.get("function", {})},
        } for m in models]
        result = {
            "source": str(apk.resolve()), "apk_sha256": original_hash,
            "package": package,
            "version_name": manifest.attrib.get(ANDROID_NS + "versionName"),
            "version_code": manifest.attrib.get(ANDROID_NS + "versionCode"),
            "dex_sha256": {n: sha256(read_member(archive, n)) for n in archive.namelist()
                           if n.endswith(".dex")},
            "whitelist": {
                "resource_name": name, "resource_id": f"0x{rid:08X}",
                "qualifier": qualifier, "archive_member": member,
                "encrypted_length": len(encrypted), "encrypted_sha256": sha256(encrypted),
                "plaintext_length": len(plaintext), "plaintext_sha256": sha256(plaintext),
                **certificate, "model_list_key": list_key,
            },
            "model_count": len(inventory),
            "brand_counts": dict(collections.Counter(m.get("brand") for m in models)),
            "model_inventory": inventory,
            "enco_x4": next((m for m in models if m["id"] == "06EC10"), None),
            "le_all_filter_functions": payload.get("leAllFilterFunctions", []),
        }
        output.mkdir(parents=True, exist_ok=True)
        (output / (package + ".evidence.json")).write_text(
            json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        with (output / (package + ".models.csv")).open("w", encoding="utf-8-sig", newline="") as file:
            writer = csv.writer(file)
            writer.writerow(("id", "name", "brand", "supportSpp", "uuid", "minVersion", "capabilities"))
            for m in inventory:
                writer.writerow((m["id"], m["name"], m["brand"], m["supportSpp"], m["uuid"],
                                 m["minVersion"], json.dumps(m["capabilities"], ensure_ascii=False)))
        if include_plaintext:
            (output / (package + ".whitelist.json")).write_bytes(plaintext)
        if dump_classes:
            from androguard.core.dex import DEX
            disassembly = []
            found = set()
            for dex_name in result["dex_sha256"]:
                dex = DEX(read_member(archive, dex_name))
                for cls in dex.get_classes():
                    if cls.get_name() not in dump_classes:
                        continue
                    found.add(cls.get_name())
                    disassembly.append(f"{dex_name}: {cls.get_name()}")
                    for method in cls.get_methods():
                        disassembly.append(method.get_name() + method.get_descriptor())
                        offset = 0
                        for instruction in method.get_instructions():
                            disassembly.append(f"  {offset:06X} {instruction.get_name()} "
                                               + instruction.get_output())
                            offset += instruction.get_length()
            missing = set(dump_classes) - found
            disassembly.append("Requested classes absent from this APK: " + ", ".join(sorted(missing)))
            (output / (package + ".disassembly.txt")).write_text(
                "\n".join(disassembly) + "\n", encoding="utf-8")
    if sha256(apk.read_bytes()) != original_hash:
        raise RuntimeError("APK changed during inspection")
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("apk", type=Path, nargs="+")
    parser.add_argument("--output", type=Path, default=Path("artifacts/apk-research"))
    parser.add_argument("--include-plaintext", action="store_true",
                        help="Also save the complete recovered local whitelist")
    parser.add_argument("--dump-class", action="append", default=[],
                        help="Optionally disassemble a DEX descriptor, e.g. Lcom/example/Class;")
    args = parser.parse_args()
    logger.remove()
    for apk in args.apk:
        result = inspect(apk, args.output, args.include_plaintext, tuple(args.dump_class))
        print(f"{result['package']} {result['version_name']}: "
              f"{result['model_count']} model profiles; GCM authenticated; APK unchanged")


if __name__ == "__main__":
    main()
