#!/usr/bin/env python3
"""Offline audit of Free4 identities, color decoding, and exact resource keys.

Parses APK DEX as data; never executes APK code and never sends requests.
The generated report contains no authentication material or temporary URLs.
"""

import argparse
import hashlib
import json
import re
import runpy
import struct
import zipfile
from pathlib import Path
import sys


sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT / "docs/research/earbud-artwork/scripts"))
from research_runtime import research_path
TARGETS = {"068C10": "OPPO Enco Free4", "06C010": "OPPO Enco Free4（丹拿版）"}
API = runpy.run_path(str(ROOT / "tools/research/event_dex_audit.py"), run_name="dex_reader_only")
SOURCES = {
    "com.heytap.headset": "欢律.apk",
    "com.oplus.melody": "无线耳机_17.6.3.apk",
}


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def walk(value, path="$"):
    yield path, value
    if isinstance(value, dict):
        for key, child in value.items():
            yield from walk(child, path + "." + key)
    elif isinstance(value, list):
        for index, child in enumerate(value):
            yield from walk(child, f"{path}[{index}]")


def switch_targets(raw, offset, opcode, words):
    if opcode not in (0x2B, 0x2C):
        return {}
    relative = struct.unpack("<i", struct.pack("<HH", *words[1:3]))[0]
    payload = offset + relative * 2
    _, count = struct.unpack_from("<HH", raw, payload)
    if opcode == 0x2B:
        first = struct.unpack_from("<i", raw, payload + 4)[0]
        keys = range(first, first + count)
        position = payload + 8
    else:
        keys = struct.unpack_from("<" + "i" * count, raw, payload + 4)
        position = payload + 4 + count * 4
    values = struct.unpack_from("<" + "i" * count, raw, position)
    return {f"0x{key:X}": f"0x{offset + value * 2:X}" for key, value in zip(keys, values)}


def dex_evidence(archive, package):
    shared = {
        ("Lcom/oplus/melody/btsdk/multidevice/HeadsetCoreService$e;", "handleMessage"): "color_message_to_device_info",
    }
    if package == "com.heytap.headset":
        wanted = {
            ("Lcom/oplus/melody/btsdk/protocol/commands/m;", "M"): "identity_and_color_ack",
            ("Ln7/d;", "d"): "unsigned_color_helper",
            ("Lcom/oplus/melody/model/repository/earphone/Q;", "A"): "resource_color_resolver",
            ("Loa/e;", "b"): "module14_request",
            ("Lsa/a;", "d"): "resource_identity_forwarding",
            ("Lcom/heytap/headset/component/scan/ScanViewModel;", "f"): "scan_color_forwarding",
        }
    else:
        wanted = {
            ("Lcom/oplus/melody/btsdk/protocol/commands/n;", "M"): "identity_and_color_ack",
            ("Lz9/a;", "q"): "unsigned_color_helper",
            ("LY6/b;", "q"): "unsigned_ble_helper",
            ("Lcom/oplus/melody/model/scan/k;", "c"): "current_98xx_ble_color",
            ("Lcom/oplus/melody/model/repository/earphone/L;", "E"): "resource_color_resolver",
            ("LF8/h;", "b"): "module14_request",
            ("LJ8/a;", "e"): "resource_identity_forwarding",
            ("Lw7/f;", "<clinit>"): "diagnosis_pid_allowlist",
            ("Lw7/f;", "b"): "diagnosis_serial_filter",
        }
    wanted.update(shared)
    evidence, literal_references = [], []
    for entry in archive.namelist():
        if not re.fullmatch(r"classes\d*\.dex", entry):
            continue
        raw = archive.read(entry)
        strings, types, method_ids, methods = API["read_dex"](raw)
        field_count, field_offset = struct.unpack_from("<II", raw, 80)
        fields = []
        for index in range(field_count):
            owner, field_type, name = struct.unpack_from("<HHI", raw, field_offset + index * 8)
            fields.append((types[owner], strings[name], types[field_type]))
        for owner, name, proto, code in methods:
            kind = wanted.get((owner, name))
            ins = list(API["instructions"](raw, code))
            for offset, opcode, words in ins:
                if opcode in (0x1A, 0x1B):
                    index = words[1] if opcode == 0x1A else words[1] | words[2] << 16
                    if strings[index] in TARGETS:
                        literal_references.append({"dex": entry, "class": owner, "method": name + proto,
                                                   "offset": f"0x{offset:X}", "literal": strings[index]})
            if not kind:
                continue
            selected = set(range(len(ins)))
            switches = {}
            if kind == "identity_and_color_ack":
                selected = set()
                for index, (offset, opcode, words) in enumerate(ins):
                    targets = switch_targets(raw, offset, opcode, words)
                    for key in ("0x8103", "0x810B"):
                        if key in targets:
                            switches[key] = targets[key]
                            address = int(targets[key], 16)
                            start = next(i for i, instruction in enumerate(ins) if instruction[0] == address)
                            selected.update(range(start, min(len(ins), start + 42)))
                    if targets:
                        selected.add(index)
            elif kind in ("color_message_to_device_info", "current_98xx_ble_color"):
                selected = set()
                setter = "setColorId" if kind == "color_message_to_device_info" else "setColor"
                for index, (_, opcode, words) in enumerate(ins):
                    if (0x6E <= opcode <= 0x72 or 0x74 <= opcode <= 0x78) and method_ids[words[1]][1] == setter:
                        selected.update(range(max(0, index - 27), min(len(ins), index + 10)))
            excerpt = []
            for index in sorted(selected):
                offset, opcode, words = ins[index]
                item = {"offset": f"0x{offset:X}", "opcode": f"0x{opcode:02X}",
                        "words": [f"0x{word:04X}" for word in words]}
                if 0x6E <= opcode <= 0x72 or 0x74 <= opcode <= 0x78:
                    item["invoke"] = list(method_ids[words[1]])
                elif 0x52 <= opcode <= 0x6D:
                    item["field"] = list(fields[words[1]])
                elif opcode in (0x1A, 0x1B):
                    string_index = words[1] if opcode == 0x1A else words[1] | words[2] << 16
                    item["string"] = strings[string_index]
                # Module-14 request parameters are enough; omit unrelated implementation noise.
                if kind == "module14_request" and "invoke" not in item and not (opcode == 0x13 and words[1] == 14):
                    continue
                excerpt.append(item)
            evidence.append({"kind": kind, "dex": entry, "dex_sha256": sha(raw), "class": owner,
                             "method": name + proto, "code_offset": f"0x{code:X}",
                             "ack_switch_targets": switches, "instructions": excerpt})
    return evidence, literal_references


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--whitelist-run", default="research/online-whitelist/runs/20261008T051557.800877Z")
    parser.add_argument("--artwork-run", default="research/online-artwork/runs/20261008T051703.741040Z")
    parser.add_argument("--output", default="research/online-artwork/evidence/free4-identity-color-audit.json")
    args = parser.parse_args()
    report = {"format_version": 1, "scope": "Offline static APK/config audit; no API probes, no APK execution.",
              "targets": TARGETS, "sources": {}, "observed_resource_requests": [], "conclusions": {
                  "exact_dynaudio_product_id": "06C010",
                  "ordinary_product_id": "068C10",
                  "base_alias_mapping": "Not found in target profiles or module14 request path. OEM hardcoded target list is DiagnosisUtils serial trimming, not an artwork alias.",
                  "color_source": "GET 0x010B / ACK 0x810B decodes unsigned payload[1]. Current OEM 98xx BLE parser reads an unsigned color byte when flags & 0x0020. Neither path remaps colors or restricts them to 0..2.",
                  "unknown_color": "For these OPPO target profiles no defaultColor is configured; resource resolver preserves -1. Supplied nonnegative color is preserved.",
                  "verified_artwork_key": {"productId": "06C010", "color": "3", "moduleId": "14"},
                  "prior_probe_limitation": "Earlier no-resource results only covered -1,0,1,2; they omitted 3 and do not prove model absence.",
                  "hardware_limit": "Color 3 is now a verified official resource key. No captured Free4 Dynaudio BLE/ACK packet was supplied, so static APK code and store color names do not independently prove the physical device emits 3.",
                  "root_or_avd_required": False}}
    for package, apk_name in SOURCES.items():
        source = {"apk_path": "research/apps/" + apk_name, "apk_sha256": sha((ROOT / "research/apps" / apk_name).read_bytes()),
                  "profile_evidence": []}
        for kind in ("builtin", "cloud"):
            path = research_path(args.whitelist_run, ROOT) / package / (kind + ".json")
            raw = path.read_bytes()
            for jsonpath, value in walk(json.loads(raw)):
                if isinstance(value, dict) and str(value.get("id", "")).upper() in TARGETS:
                    identity = str(value["id"]).upper()
                    if value.get("name") != TARGETS[identity]:
                        raise ValueError("Target identity/name mismatch")
                    keys = sorted({p.rsplit(".", 1)[-1] for p, _ in walk(value)})
                    source["profile_evidence"].append({"path": path.relative_to(ROOT).as_posix(), "sha256": sha(raw),
                                                       "jsonpath": jsonpath, "profile": value,
                                                       "defaultColor_present": "defaultColor" in value,
                                                       "alias_or_resource_key_fields": [k for k in keys if re.search(r"alias|baseId|resourceId|modelId|parentId", k, re.I)]})
        with zipfile.ZipFile(ROOT / "research/apps" / apk_name) as archive:
            source["code_evidence"], source["target_pid_literal_references"] = dex_evidence(archive, package)
        report["sources"][package] = source
    results_path = research_path(args.artwork_run, ROOT) / "results.json"
    results_raw = results_path.read_bytes()
    result = json.loads(results_raw)
    for index, case in enumerate(result.get("cases", [])):
        body = case.get("request_body", {})
        if body.get("productId") != "06C010":
            continue
        observed = {"results_path": results_path.relative_to(ROOT).as_posix(), "results_sha256": sha(results_raw),
                    "jsonpath": f"$.cases[{index}]", "package": case.get("package"), "request_body": body,
                    "status": case.get("status"), "expected_sha256": case.get("expected_sha256")}
        artifact = case.get("package_artifact")
        if case.get("status") == "verified" and artifact:
            zip_path = research_path(artifact, ROOT / "research/online-artwork") / "resource.zip"
            raw = zip_path.read_bytes()
            if sha(raw) != case.get("expected_sha256"):
                raise ValueError("Verified resource SHA256 mismatch")
            observed.update({"resource_zip_bytes": len(raw), "resource_zip_sha256_verified": sha(raw)})
        report["observed_resource_requests"].append(observed)
    if not any(o["status"] == "verified" and o["request_body"].get("color") == "3" for o in report["observed_resource_requests"]):
        raise ValueError("The supplied run has no verified Dynaudio color3 package")
    output = research_path(args.output, ROOT)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({"output": output.relative_to(ROOT).as_posix(), "sources": len(report["sources"]),
                      "verified_dynaudio_packages": sum(o["status"] == "verified" for o in report["observed_resource_requests"])}, ensure_ascii=False))


if __name__ == "__main__":
    main()
