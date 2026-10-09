#!/usr/bin/env python3
"""Offline APK/cloud whitelist analysis; this script never makes network requests.

Input: RUN/{com.heytap.headset,com.oplus.melody}/{builtin,cloud}.json,
       with optional RUN/results.json containing acquisition metadata.
Output: RUN/analysis.json. Config URLs and secrets are never copied into output.
"""

import argparse
import copy
import hashlib
import json
import math
import re
from collections import Counter
from pathlib import Path
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "earbud-artwork/scripts"))
from research_runtime import research_path


SOURCES = ("com.heytap.headset", "com.oplus.melody")
BUILTIN_METADATA_VERSIONS = {"com.heytap.headset": 17006004, "com.oplus.melody": 17006002}
BUILTIN_CONTENT_VERSIONS = {"com.heytap.headset": 117006004, "com.oplus.melody": 17006002}
APK_CONTENT_COMPAT_OFFSETS = {"com.heytap.headset": "classes3.dex:0x2522D4",
                              "com.oplus.melody": "classes.dex:0x58DB58"}
EQ_DEFAULT_FREQUENCIES = [62, 250, 1000, 4000, 8000, 16000]
EQ_FIELDS = ("equalizer", "customEqualizer", "customEqFrequency", "customEqUiVersion",
             "customEqMax", "equalizerMode", "equalizerModeByVersion", "equalizerModeCompat",
             "highToneQuality", "shareCustomEq")
EQ_TARGETS = {"067410": "OPPO Enco X3", "06EC10": "OPPO Enco X4"}
MAX_FILE_BYTES = 64 * 1024 * 1024
MAX_EVIDENCE = 12
MISSING = object()
SECRET_FIELD = re.compile(r"(?i)(url|uri|password|secret|token|(^|_)key($|_))")
URL_VALUE = re.compile(r"(?i)^(?:https?|socks5?|ftp)://")


def json_type(value):
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "boolean"
    if isinstance(value, int):
        return "integer"
    if isinstance(value, float):
        return "number"
    if isinstance(value, str):
        return "string"
    if isinstance(value, list):
        return "array"
    return "object"


def jsonpath(parent, key):
    if isinstance(key, int):
        return f"{parent}[{key}]"
    if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", key):
        return f"{parent}.{key}"
    return f"{parent}[{json.dumps(key, ensure_ascii=False)}]"


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"),
                      allow_nan=False)


def fingerprint(value):
    return hashlib.sha256(canonical(value).encode("utf-8")).hexdigest()


def safe_value(value, key=""):
    """A defense in depth safeguard for small evidence fragments, not full exports."""
    if SECRET_FIELD.search(key) or (isinstance(value, str) and URL_VALUE.match(value)):
        return {"redacted": True, "type": json_type(value), "sha256": fingerprint(value)}
    if isinstance(value, dict):
        return {k: safe_value(v, k) for k, v in value.items()}
    if isinstance(value, list):
        return [safe_value(v, key) for v in value]
    return value


def read_json(path):
    if path.stat().st_size > MAX_FILE_BYTES:
        raise ValueError(f"JSON input exceeds {MAX_FILE_BYTES} bytes: {path.name}")
    def reject_constant(value):
        raise ValueError(f"Nonfinite JSON number: {value}")
    def finite_float(value):
        number = float(value)
        if not math.isfinite(number):
            raise ValueError("Nonfinite JSON number")
        return number
    raw = path.read_bytes()
    value = json.loads(raw.decode("utf-8-sig"), parse_constant=reject_constant, parse_float=finite_float)
    return value, hashlib.sha256(raw).hexdigest()


def integer_version(value):
    return value if isinstance(value, int) and not isinstance(value, bool) and value >= 0 else None


def source_metadata(results, source):
    """Support the fetcher's list or dictionary result container without inventing data."""
    if isinstance(results, dict):
        if isinstance(results.get(source), dict):
            return results[source], jsonpath("$", source)
        for key in ("sources", "results"):
            child = results.get(key)
            if isinstance(child, dict) and isinstance(child.get(source), dict):
                return child[source], jsonpath(jsonpath("$", key), source)
            if isinstance(child, list):
                for index, row in enumerate(child):
                    if isinstance(row, dict) and source in (row.get("source"), row.get("package"),
                                                           row.get("package_name"), row.get("app")):
                        return row, jsonpath(jsonpath("$", key), index)
    return {}, None


def compat_content(root, source):
    if not isinstance(root, dict):
        raise ValueError("Whitelist JSON root must be an object")
    content = copy.deepcopy(root)
    origins = {}
    for ordinary, compatibility in (("versionCode", "compatVersionCode"),
                                    ("whiteList", "compatWhiteList"),
                                    ("caseNumSite", "compatCaseNumSite"),
                                    ("appMarket", "compatAppMarket")):
        # APK checks null, not emptiness: [] and version 0 still take priority.
        if root.get(compatibility) is not None:
            content[ordinary] = copy.deepcopy(root[compatibility])
            origins[ordinary] = jsonpath("$", compatibility)
        else:
            origins[ordinary] = jsonpath("$", ordinary)
        content.pop(compatibility, None)
    return content, {"implementation": "WhitelistContentDO.compatToContent()",
                     "dex_code_offset": APK_CONTENT_COMPAT_OFFSETS[source],
                     "rule": "Non-null compat fields replace ordinary fields, including [] and 0.",
                     "effective_field_jsonpaths": origins}


def fields_schema(rows, base_path, total=None):
    object_rows = [row for row in rows if isinstance(row, dict)]
    denominator = len(object_rows) if total is None else total
    fields = {}
    for row in object_rows:
        for key, value in row.items():
            entry = fields.setdefault(key, {"present": 0, "types": Counter()})
            entry["present"] += 1
            entry["types"][json_type(value)] += 1
    return {key: {"present": value["present"], "missing": denominator - value["present"],
                  "types": dict(sorted(value["types"].items())),
                  "jsonpath": jsonpath(base_path, key)}
            for key, value in sorted(fields.items())}


def schema_summary(root, rows, list_path):
    roots = {}
    for key, value in sorted(root.items()):
        entry = {"type": json_type(value), "jsonpath": jsonpath("$", key)}
        if isinstance(value, (list, dict)):
            entry["count"] = len(value)
        roots[key] = entry
    functions = [row.get("function") for row in rows if isinstance(row, dict)]
    return {"root_fields": roots, "model_fields": fields_schema(rows, list_path + "[*]"),
            "function_fields": fields_schema(functions, list_path + "[*].function", len(rows))}


def pid(value):
    if isinstance(value, str):
        text = value.strip().upper()
        if text.startswith("0X"):
            text = text[2:]
        return text if re.fullmatch("[0-9A-F]{6}", text) else None
    if isinstance(value, int) and not isinstance(value, bool) and 0 <= value <= 0xFFFFFF:
        return f"{value:06X}"
    return None


def inventory(rows, list_path):
    products = {}
    duplicate_pids = []
    invalid_ids = []
    alias_names = 0
    explicit_children = 0
    duplicate_count = 0

    def add(row, path, inherited=None, parent_pid=None, inherited_paths=None):
        nonlocal alias_names, explicit_children, duplicate_count
        if not isinstance(row, dict):
            invalid_ids.append({"jsonpath": path, "reason": "model entry is not an object"})
            return
        product = pid(row.get("id"))
        effective = copy.deepcopy(inherited or {})
        effective.update(copy.deepcopy(row))
        effective.pop("children", None)
        field_paths = dict(inherited_paths or {})
        field_paths.update({key: jsonpath(path, key) for key in row if key != "children"})
        if product is not None:
            effective["id"] = product
            entry = {"profile": effective, "jsonpath": path, "id_jsonpath": jsonpath(path, "id"),
                     "parent_pid": parent_pid, "field_jsonpaths": field_paths}
            if product in products:
                duplicate_count += 1
                if len(duplicate_pids) < MAX_EVIDENCE:
                    duplicate_pids.append({"pid": product, "first_jsonpath": products[product]["id_jsonpath"],
                                           "second_jsonpath": entry["id_jsonpath"],
                                           "profiles_equal": products[product]["profile"] == effective})
            else:
                products[product] = entry
        elif "id" in row or inherited is None:
            if len(invalid_ids) < MAX_EVIDENCE:
                invalid_ids.append({"jsonpath": jsonpath(path, "id"),
                                    "reason": "missing or invalid explicit 24-bit PID"})
        elif isinstance(row.get("name"), str):
            alias_names += 1
        children = row.get("children")
        if children is None:
            return
        if not isinstance(children, list):
            if len(invalid_ids) < MAX_EVIDENCE:
                invalid_ids.append({"jsonpath": jsonpath(path, "children"), "reason": "not an array"})
            return
        for index, child in enumerate(children):
            if isinstance(child, dict) and pid(child.get("id")) is not None:
                explicit_children += 1
            add(child, jsonpath(jsonpath(path, "children"), index), effective, product or parent_pid, field_paths)

    for index, row in enumerate(rows):
        add(row, jsonpath(list_path, index))
    brands = Counter()
    device_types = Counter()
    categories = Counter()
    protocol_types = Counter()
    for entry in products.values():
        row = entry["profile"]
        brand = str(row.get("brand", "unknown"))
        brands[{"oppo": "OPPO", "oneplus": "OnePlus", "realme": "realme"}.get(brand.casefold(), brand)] += 1
        kind = str(row.get("type", "missing"))
        device_types[kind] += 1
        categories["speaker" if kind == "S" else "earphone" if kind in {"T1", "T2", "O1", "N"}
                   else "unknown"] += 1
        protocol_types[str(row.get("protocolType", "missing"))] += 1
    summary = {"root_model_rows": len(rows), "unique_explicit_pids": len(products),
               "explicit_child_rows": explicit_children, "name_only_alias_rows": alias_names,
               "duplicate_pid_rows": duplicate_count, "duplicate_pid_evidence": duplicate_pids,
               "invalid_id_evidence": invalid_ids, "brands": dict(sorted(brands.items())),
               "device_type_codes": dict(sorted(device_types.items())),
               "categories": dict(sorted(categories.items())),
               "category_rule": "Research grouping: S=speaker; T1/T2/O1/N=earphone; other=unknown.",
               "protocol_type_values": dict(sorted(protocol_types.items())),
               "pid_rule": "Only explicit id fields expand PIDs; name-only children remain aliases.",
               "child_profile_rule": "Explicit child inherits parent fields; child fields override; children omitted."}
    return summary, products


def eq_fragments(products, source):
    result = {}
    for product, label in EQ_TARGETS.items():
        if product not in products:
            result[product] = {"name": label, "present": False}
            continue
        entry = products[product]
        function = entry["profile"].get("function")
        function = function if isinstance(function, dict) else {}
        function_path = entry["field_jsonpaths"].get("function", jsonpath(entry["jsonpath"], "function"))
        configured = {key: {"jsonpath": jsonpath(function_path, key), "value": safe_value(function[key], key)}
                      for key in EQ_FIELDS if key in function}
        frequency = function.get("customEqFrequency")
        if not isinstance(frequency, list) or not frequency:
            frequency = EQ_DEFAULT_FREQUENCIES
            frequency_origin = "compiled APK six-band fallback; not a config field"
        else:
            frequency_origin = jsonpath(function_path, "customEqFrequency")
        result[product] = {"name": label, "present": True, "model_jsonpath": entry["jsonpath"],
                           "configured": configured, "absent_fields": [key for key in EQ_FIELDS if key not in function],
                           "compiled_apk_parameter_interpretation": {
                               "frequencies": frequency, "band_count": len(frequency),
                               "frequency_origin": frequency_origin,
                               "custom_eq_ui_version": function.get("customEqUiVersion", 1),
                               "maximum_custom_presets": function.get("customEqMax", 3),
                               "default_evidence": "LAb/Z.<init>/<clinit>" if source == SOURCES[0] else "Lca/r.<init>/<clinit>",
                               "scope": "Defaults describe the researched APK code; gain bounds/step are not inferred."}}
    return result


def analyze_file(path, source):
    root, digest = read_json(path)
    content, compatibility = compat_content(root, source)
    list_path = compatibility["effective_field_jsonpaths"]["whiteList"]
    rows = content.get("whiteList")
    if not isinstance(rows, list):
        raise ValueError(f"Effective whiteList is not an array in {source}/{path.name}")
    summary, products = inventory(rows, list_path)
    version_raw = content.get("versionCode", 0)
    version = integer_version(version_raw)
    return {"file": str(path.name), "sha256": digest, "compatibility": compatibility,
            "content_version": version, "content_version_jsonpath": compatibility["effective_field_jsonpaths"]["versionCode"],
            "content_version_missing_uses_dto_default_zero": "versionCode" not in content,
            "schema": schema_summary(root, rows, list_path), "inventory": summary,
            "eq_examples": eq_fragments(products, source)}, products, content


def diff_profiles(left, right):
    counts = Counter()
    evidence = []

    def visit(a, b, ap, bp):
        if a is not MISSING and b is not MISSING and type(a) is type(b) and a == b:
            return
        if a is MISSING or b is MISSING:
            change = "added" if a is MISSING else "removed"
        elif json_type(a) != json_type(b):
            change = "type_changed"
        elif isinstance(a, dict) and isinstance(b, dict):
            for key in sorted(set(a) | set(b)):
                visit(a.get(key, MISSING), b.get(key, MISSING), jsonpath(ap, key), jsonpath(bp, key))
            return
        elif isinstance(a, list) and isinstance(b, list):
            for index in range(max(len(a), len(b))):
                visit(a[index] if index < len(a) else MISSING, b[index] if index < len(b) else MISSING,
                      jsonpath(ap, index), jsonpath(bp, index))
            return
        else:
            change = "value_changed"
        counts[change] += 1
        if len(evidence) < MAX_EVIDENCE:
            item = {"change": change, "left_jsonpath": ap, "right_jsonpath": bp}
            for prefix, value in (("left", a), ("right", b)):
                if value is not MISSING:
                    item[prefix + "_type"] = json_type(value)
                    item[prefix + "_sha256"] = fingerprint(value)
            evidence.append(item)
    for key in sorted(set(left["profile"]) | set(right["profile"])):
        visit(left["profile"].get(key, MISSING), right["profile"].get(key, MISSING),
              left["field_jsonpaths"].get(key, jsonpath(left["jsonpath"], key)),
              right["field_jsonpaths"].get(key, jsonpath(right["jsonpath"], key)))
    return counts, evidence


def compare(label, left, right):
    left_only = sorted(set(left) - set(right))
    right_only = sorted(set(right) - set(left))
    same = changed = 0
    counts = Counter()
    evidence = []
    changed_ids = []
    for product in sorted(set(left) & set(right)):
        differences, samples = diff_profiles(left[product], right[product])
        if differences:
            changed += 1
            counts.update(differences)
            if len(changed_ids) < MAX_EVIDENCE:
                changed_ids.append(product)
            for sample in samples:
                if len(evidence) < MAX_EVIDENCE:
                    evidence.append({"pid": product, **sample})
        else:
            same += 1
    def pid_evidence(ids, records):
        return [{"pid": product, "id_jsonpath": records[product]["id_jsonpath"]}
                for product in ids[:MAX_EVIDENCE]]
    return {"comparison": label, "left_pid_count": len(left), "right_pid_count": len(right),
            "left_only_pid_count": len(left_only), "right_only_pid_count": len(right_only),
            "left_only_evidence": pid_evidence(left_only, left), "right_only_evidence": pid_evidence(right_only, right),
            "common_identical_profiles": same, "common_changed_profiles": changed,
            "changed_pid_sample": changed_ids, "field_change_counts": dict(sorted(counts.items())),
            "field_change_evidence": evidence,
            "comparison_rule": "Match normalized explicit PID; compare inherited profiles without children. Arrays preserve order.",
            "evidence_limit": MAX_EVIDENCE}


def analyze_run(run):
    run = research_path(run)
    results_path = run / "results.json"
    results = read_json(results_path)[0] if results_path.is_file() else {}
    report = {"format_version": 1, "scope": "Offline research; does not grant control permissions or alter application modules.",
              "sources": {}, "comparisons": []}
    records = {}
    selected = {}
    for source in SOURCES:
        metadata, metadata_path = source_metadata(results, source)
        app_version = integer_version(metadata.get("app_metadata_version_code"))
        builtin_version = integer_version(metadata.get("builtin_effective_content_version"))
        app_origin = "results.json:" + jsonpath(metadata_path, "app_metadata_version_code") if app_version is not None else "researched APK metadata constant"
        content_origin = "results.json:" + jsonpath(metadata_path, "builtin_effective_content_version") if builtin_version is not None else "researched APK builtin version policy constant"
        if app_version is None:
            app_version = BUILTIN_METADATA_VERSIONS[source]
        if builtin_version is None:
            if integer_version(metadata.get("app_metadata_version_code")) is not None:
                builtin_version = app_version + (100000000 if source == SOURCES[0] else 0)
                content_origin = "derived from results.json app_metadata_version_code using researched APK policy"
            else:
                builtin_version = BUILTIN_CONTENT_VERSIONS[source]
        source_report = {"app_metadata_version_code": app_version, "app_metadata_version_origin": app_origin,
                         "builtin_effective_content_version": builtin_version,
                         "builtin_effective_content_version_origin": content_origin}
        report["sources"][source] = source_report
        for variant in ("builtin", "cloud"):
            path = run / source / (variant + ".json")
            if not path.is_file():
                source_report[variant] = {"available": False}
                continue
            try:
                entry, products, _ = analyze_file(path, source)
            except (ValueError, UnicodeError, json.JSONDecodeError) as error:
                # Do not echo a JSON decoder excerpt that might contain a URL/key.
                source_report[variant] = {"available": True, "valid": False, "error_type": type(error).__name__}
                continue
            source_report[variant] = {"available": True, "valid": True, **entry}
            records[(source, variant)] = products
        cloud = source_report["cloud"]
        usable_builtin = (source, "builtin") in records
        cloud_version = cloud.get("content_version") if cloud.get("valid") else None
        if usable_builtin and cloud_version is not None and cloud_version > builtin_version:
            choice, reason = "cloud", "Cloud content version is strictly greater than builtin effective content version."
        elif usable_builtin:
            choice, reason = "builtin", "Cloud is absent/invalid/older/equal; only a strictly greater valid cloud version replaces builtin."
        else:
            choice, reason = None, "Builtin input is unavailable or invalid; APK coverage cannot be reproduced safely."
        source_report["selection"] = {"selected": choice, "cloud_content_version": cloud_version,
                                      "builtin_effective_content_version": builtin_version,
                                      "rule": "cloud > builtin; equality retains builtin", "reason": reason,
                                      "coverage_pid_count": len(records.get((source, choice), {}))}
        if choice:
            selected[source] = records[(source, choice)]
        if (source, "builtin") in records and (source, "cloud") in records:
            report["comparisons"].append(compare(source + ": builtin -> cloud", records[(source, "builtin")], records[(source, "cloud")]))
    for variant in ("builtin", "cloud"):
        if all((source, variant) in records for source in SOURCES):
            report["comparisons"].append(compare(variant + ": com.heytap.headset -> com.oplus.melody",
                                                 records[(SOURCES[0], variant)], records[(SOURCES[1], variant)]))
    if all(source in selected for source in SOURCES):
        report["comparisons"].append(compare("selected: com.heytap.headset -> com.oplus.melody", selected[SOURCES[0]], selected[SOURCES[1]]))
        left_ids, right_ids = set(selected[SOURCES[0]]), set(selected[SOURCES[1]])
        report["selected_coverage"] = {"union_explicit_pid_count": len(left_ids | right_ids),
                                       "shared_explicit_pid_count": len(left_ids & right_ids),
                                       "note": "PID coverage includes speakers and aliases with explicit child PIDs; it is not an earbud count or protocol support claim."}
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", required=True, type=Path, help="Existing local acquisition run directory")
    args = parser.parse_args()
    run = research_path(args.run).resolve(strict=True)
    research_root = Path(__file__).resolve().parents[4] / "research/online-whitelist"
    if not run.is_relative_to(research_root):
        parser.error("--run must remain under research/online-whitelist")
    if not run.is_dir():
        parser.error("--run must be a directory")
    output = run / "analysis.json"
    output = research_path(output)
    if not output.is_relative_to(research_root):
        parser.error("analysis output must remain under research/online-whitelist")
    output.write_text(json.dumps(analyze_run(run), ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(f"Wrote {output}")


if __name__ == "__main__":
    main()
