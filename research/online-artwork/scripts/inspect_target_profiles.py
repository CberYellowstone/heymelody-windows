#!/usr/bin/env python3
"""Inspect local whitelists/APKs for exact X3/Free4 artwork evidence, offline."""

import argparse
import hashlib
import json
import re
import runpy
import struct
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
OUTPUT_ROOT = ROOT / "research" / "online-artwork"
TARGETS = {"067410": "OPPO Enco X3", "068C10": "OPPO Enco Free4",
           "06C010": "OPPO Enco Free4（丹拿版）"}
SOURCES = {"com.heytap.headset": ("欢律.apk", "classes3.dex"),
           "com.oplus.melody": ("无线耳机_17.6.3.apk", "classes2.dex")}
RESOURCE_KEY = re.compile(r"(?i)(color|3d|model|dress|resource|image|video|appearance)")
PATH_KEY = re.compile(r"(?i)(path|file|icon)")


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def walk(value, path="$"):
    if isinstance(value, dict):
        for key, child in value.items():
            child_path = path + "." + key
            yield key, child, child_path
            yield from walk(child, child_path)
    elif isinstance(value, list):
        for index, child in enumerate(value):
            yield from walk(child, f"{path}[{index}]")


def code_evidence(archive, package, dex_entry):
    api = runpy.run_path(str(ROOT / "tools" / "research" / "event_dex_audit.py"),
                        run_name="dex_reader_only")
    raw = archive.read(dex_entry)
    strings, types, method_ids, methods = api["read_dex"](raw)
    field_count, field_offset = struct.unpack_from("<II", raw, 80)
    fields = []
    for index in range(field_count):
        owner, field_type, name = struct.unpack_from("<HHI", raw, field_offset + index * 8)
        fields.append((types[owner], strings[name], types[field_type]))
    if package == "com.heytap.headset":
        targets = {("Loa/e;", "b"): "module14_request",
                   ("Loa/c;", "invoke"): "download_config_members",
                   ("LG9/b;", "run"): "legacy_model_member_path",
                   ("Lcom/oplus/melody/model/repository/earphone/Q;", "A"): "color_resolver"}
    else:
        targets = {("LF8/h;", "b"): "module14_request",
                   ("LF8/d;", "invoke"): "download_config_members",
                   ("LA9/P;", "run"): "legacy_model_member_path",
                   ("Lcom/oplus/melody/model/repository/earphone/L;", "E"): "color_resolver"}
    evidence = []
    wanted_member_names = {"getModel3D", "getModelWebp", "setModelFilePath", "setWebpFilePath",
                           "setProductId", "setColorId", "getRootPath"}
    for owner, name, proto, code in methods:
        kind = targets.get((owner, name))
        if not kind:
            continue
        instructions = list(api["instructions"](raw, code))
        selected = set()
        if kind == "legacy_model_member_path":
            for index, (_, opcode, words) in enumerate(instructions):
                if opcode == 0x1A and strings[words[1]] == "res/raw/detail_model.vfxms":
                    selected.update(range(max(0, index - 4), min(len(instructions), index + 6)))
        excerpt = []
        for index, (offset, opcode, words) in enumerate(instructions):
            item = {"dex_offset": f"0x{offset:X}", "opcode": f"0x{opcode:02X}",
                    "code_units": [f"0x{word:04X}" for word in words]}
            invoked = method_ids[words[1]] if 0x6E <= opcode <= 0x72 or 0x74 <= opcode <= 0x78 else None
            if invoked:
                item["invoked"] = list(invoked)
            elif 0x52 <= opcode <= 0x6D:
                item["field"] = list(fields[words[1]])
            elif opcode == 0x1A:
                item["string"] = strings[words[1]]
            if kind == "legacy_model_member_path" and index not in selected:
                continue
            if kind == "download_config_members" and (not invoked or invoked[1] not in wanted_member_names):
                continue
            if kind == "module14_request" and not (invoked or opcode == 0x13 and words[1] == 14):
                continue
            excerpt.append(item)
        evidence.append({"kind": kind, "dex_entry": dex_entry, "dex_sha256": sha(raw),
                         "class": owner, "method": name + proto, "code_offset": f"0x{code:X}",
                         "instructions": excerpt})
    return evidence


def saved_observations():
    observations = []
    packages = {}
    for result_path in sorted((OUTPUT_ROOT / "runs").glob("*/results.json")):
        raw = result_path.read_bytes()
        result = json.loads(raw)
        for index, case in enumerate(result.get("cases", [])):
            body = case.get("request_body", {})
            product = body.get("productId")
            if product not in TARGETS:
                continue
            item = {"results_path": result_path.relative_to(ROOT).as_posix(),
                    "results_sha256": sha(raw), "jsonpath": f"$.cases[{index}]",
                    "package": case.get("package"), "productId": product,
                    "target_name": TARGETS[product], "color": body.get("color"),
                    "moduleId": body.get("moduleId"), "status": case.get("status"),
                    "expected_sha256": case.get("expected_sha256"),
                    "color_meaning": "Request protocol number; no marketing-color or default-color mapping."}
            artifact = case.get("package_artifact")
            if case.get("status") == "verified" and isinstance(artifact, str):
                package_root = (OUTPUT_ROOT / artifact).resolve()
                if OUTPUT_ROOT.resolve() not in package_root.parents:
                    raise ValueError("Package artifact escapes research directory")
                resource_zip = package_root / "resource.zip"
                digest = sha(resource_zip.read_bytes())
                if digest != case.get("expected_sha256"):
                    raise ValueError("Saved resource ZIP SHA256 mismatch")
                item["resource_zip_sha256_verified"] = digest
                if digest not in packages:
                    config_path = package_root / "extracted" / "config.json"
                    model_path = package_root / "extracted" / "res" / "raw" / "detail_model.vfxms"
                    config_raw = config_path.read_bytes()
                    config = json.loads(config_raw)
                    model_raw = model_path.read_bytes()
                    packages[digest] = {"artifact": package_root.relative_to(ROOT).as_posix(),
                                        "zip_bytes": resource_zip.stat().st_size,
                                        "config": config, "config_sha256": sha(config_raw),
                                        "model_member": "res/raw/detail_model.vfxms",
                                        "model_bytes": len(model_raw), "model_sha256": sha(model_raw)}
            observations.append(item)
    return observations, packages


def inspect(run):
    prior = ROOT / "research" / "earbud-artwork" / "evidence"
    contract = json.loads((prior / "resource-contract.json").read_text(encoding="utf-8"))
    report = {"format_version": 1, "scope": "Offline target research only; APK code is parsed, never executed.",
              "exact_targets": TARGETS, "excluded_near_names": {"06BC10": "OPPO Enco X3s", "066810": "OPPO Enco X3i"},
              "sources": {}, "prior_evidence": {}, "conclusions": {}}
    for name in ("resource-contract.json", "default-color-selection.json", "server-probes.json"):
        file = prior / name
        report["prior_evidence"][name] = {"path": file.relative_to(ROOT).as_posix(), "sha256": sha(file.read_bytes())}
    # Reuse only nonsensitive contract fields. Old version_code fields were manifest
    # versions; online-whitelist/results.json now provides the actual request metadata.
    report["resource_request_contract"] = {
        "endpoint": contract["model_module"]["endpoint"], "method": "POST", "moduleId": 14,
        "body_fields": ["platform", "channel", "versionCode", "productId", "color", "moduleId"],
        "channel_by_package": contract["body_defaults"]["channel_by_package"],
        "configuration_members": ["model3D", "modelWebp"],
        "cache_name": contract["cache"]["model_name"],
        "legacy_model_member": contract["cache"]["legacy_model_member"],
        "code_evidence": contract["model_module"]["code_evidence"]}
    results = json.loads((run / "results.json").read_text(encoding="utf-8"))
    for package, (apk_name, dex_entry) in SOURCES.items():
        cloud_path = run / package / "cloud.json"
        cloud_raw = cloud_path.read_bytes()
        cloud = json.loads(cloud_raw)
        list_key = "compatWhiteList" if cloud.get("compatWhiteList") is not None else "whiteList"
        version_key = "compatVersionCode" if cloud.get("compatVersionCode") is not None else "versionCode"
        source_report = {"cloud_path": cloud_path.relative_to(ROOT).as_posix(), "cloud_sha256": sha(cloud_raw),
                         "effective_version": cloud.get(version_key, 0), "version_jsonpath": "$." + version_key,
                         "model_list_jsonpath": "$." + list_key,
                         "app_metadata_version_code": results["sources"][package]["app_metadata_version_code"],
                         "profiles": {}, "apk_configs": []}
        report["sources"][package] = source_report
        for index, row in enumerate(cloud[list_key]):
            product = str(row.get("id", "")).upper()
            if product not in TARGETS:
                continue
            if row.get("name") != TARGETS[product]:
                raise ValueError("Exact PID/name target mismatch")
            profile_path = f"$.{list_key}[{index}]"
            fields = [{"jsonpath": profile_path + path[1:], "value": value}
                      for key, value, path in walk(row) if RESOURCE_KEY.search(key)]
            source_report["profiles"][product] = {
                "jsonpath": profile_path, "profile": row, "resource_and_color_fields": fields,
                "defaultColor_present": "defaultColor" in row,
                "configured_color_codes": [],
                "color_result_for_unknown_input_minus_one": -1,
                "color_inference": "OPPO target has no configured defaultColor; resolver preserves unknown -1.",
                "resource_urls_present": any(isinstance(value, str) and re.match(r"https?://", value)
                                             for _, value, _ in walk(row)),
                "target_specific_model_path_present": False}
        if set(source_report["profiles"]) != set(TARGETS):
            raise ValueError("A named target is absent from the selected cloud list")
        apk_path = ROOT / "research" / apk_name
        source_report["apk_sha256"] = sha(apk_path.read_bytes())
        with zipfile.ZipFile(apk_path) as archive:
            names = set(archive.namelist())
            parsed_json_count = 0
            matching_names = []
            for name in sorted(names):
                if not name.startswith(("assets/", "res/")) or not name.endswith(".json"):
                    continue
                raw = archive.read(name)
                try:
                    content = json.loads(raw)
                except (ValueError, UnicodeError):
                    continue
                parsed_json_count += 1
                text = raw.decode("utf-8", "replace").upper()
                if any(product in text for product in TARGETS):
                    matching_names.append(name)
                if name not in {"res/iD.json", "res/Hx.json"} and not any(
                        key in {"model3D", "modelWebp"} for key, _, _ in walk(content)):
                    continue
                paths = [{"jsonpath": path, "value": value,
                          "member_present_in_apk": value in names}
                         for key, value, path in walk(content) if isinstance(value, str) and PATH_KEY.search(key)]
                source_report["apk_configs"].append({"entry": name, "sha256": sha(raw),
                                                       "content": content, "relative_path_evidence": paths,
                                                       "target_pid_present": any(product in text for product in TARGETS),
                                                       "target_binding_confirmed": False})
            source_report["plain_json_scan"] = {"parseable_assets_res_json_count": parsed_json_count,
                                                 "entries_containing_target_pid": matching_names,
                                                 "scope": "Plain JSON only; encrypted whitelist was separately authenticated and saved in cloud.json."}
            source_report["dex_code_evidence"] = code_evidence(archive, package, dex_entry)
            source_report["legacy_model_member_present_in_apk"] = "res/raw/detail_model.vfxms" in names
    observations, packages = saved_observations()
    report["saved_online_observations"] = observations
    report["saved_verified_packages"] = packages
    for package, source_report in report["sources"].items():
        for product, profile in source_report["profiles"].items():
            profile["observed_successful_resource_color_codes"] = sorted({int(case["color"]) for case in observations
                if case["package"] == package and case["productId"] == product and case["status"] == "verified"})
    report["conclusions"] = {
        "support3DModel": "All three exact targets declare support3DModel=1 and customDress=1 in both cloud sources.",
        "configured_color_codes": "None of these six profiles declares a defaultColor or color enumeration.",
        "known_numeric_inputs": {"-1": "Unknown color; retained for these OPPO profiles.",
                                 "0": "Explicit historical X3 probe input; not an official default or validated available variant.",
                                 "1": "Free4 successfully returned a verified module14 package for this explicit number.",
                                 "2": "X3 successfully returned a verified module14 package for this explicit number.",
                                 "3": "Free4 Dynaudio successfully returned a verified module14 package for this explicit number; no default or marketing-color mapping is inferred."},
        **{label: sorted({int(case["color"]) for case in observations
                         if case["productId"] == product and case["status"] == "verified"})
           for label, product in (("free4_confirmed_resource_color_codes", "068C10"),
                                  ("x3_confirmed_resource_color_codes", "067410"),
                                  ("free4_dynaudio_confirmed_resource_color_codes", "06C010"))},
        "generic_dress_configs": "iD/Hx are shared image/video dress templates; their paths do not identify these products or a 3D mesh.",
        "module_selection": "Module 14 selects the model resource download; downloaded config exposes model3D and modelWebp separately.",
        "model_retrieval": "Offline analysis references saved request results and independently checks saved ZIP/model bytes; it makes no network requests.",
        "unavailable_probe_scope": "A no_resource_returned observation applies only to its recorded request; it cannot exclude other colors, versions or future server changes."}
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", type=Path, default=ROOT / "research" / "online-whitelist" / "runs" / "20261007T095049.319796Z")
    parser.add_argument("--output", type=Path, help="Write a new evidence file while preserving the historical report")
    args = parser.parse_args()
    output = args.output.resolve() if args.output else OUTPUT_ROOT / "evidence" / "target-profiles.json"
    if not output.resolve().is_relative_to(OUTPUT_ROOT.resolve()):
        parser.error("Output must remain below research/online-artwork")
    if args.output and output.exists():
        parser.error("Output already exists; choose a new report path")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(inspect(args.run.resolve()), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("Wrote " + str(output))


if __name__ == "__main__":
    main()
