#!/usr/bin/env python3
"""Read a raw image and Ghidra exports locally, producing bounded triage indexes.

The indexes identify analysis candidates, not confirmed implementations. Nothing
is flashed, executed on a device, downloaded, or unpacked by this tool.
"""

from __future__ import annotations

import argparse
import bisect
from collections import Counter, defaultdict, deque
import hashlib
import json
from pathlib import Path
import re
import struct


BASES = (0x0C010000, 0x2C010000)
MODULES = {
    "control_spp": re.compile(r"spp|enco[_ .]*link|codec.*capab|capab.*codec|tlv|control.*packet", re.I),
    "bluetooth": re.compile(r"a2dp|avrcp|hfp|l2cap|btif|btdrv|tws|ibrt|ble_", re.I),
    "rtos_tasks": re.compile(r"rtx|osThread|osMutex|osTimer|osSemaphore|thread.*stack|task.*prio", re.I),
    "update_boot": re.compile(r"ota|upgrade|bootloader|firmware|image.*crc|flash.*erase|flash.*write", re.I),
    "audio_dsp": re.compile(r"audio|dsp|anc|equaliz|iir|fir|eq_|sample.rate|codec", re.I),
    "power_hardware": re.compile(r"pmu|battery|charging|voltage|gpio|i2c|spi_|watchdog", re.I),
}


def read_jsonl(path: Path):
    if not path.exists():
        return
    with path.open(encoding="utf-8") as stream:
        for line in stream:
            if line.strip():
                yield json.loads(line)


def write_json(path: Path, value):
    with path.open("x", encoding="utf-8") as stream:
        stream.write(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


def write_jsonl(path: Path, rows):
    with path.open("x", encoding="utf-8") as stream:
        for row in rows:
            stream.write(json.dumps(row, ensure_ascii=False) + "\n")


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def analyze(raw: Path, exported: Path, output: Path):
    # Reserve the output before reading inputs; an existing path is never reused.
    output.mkdir(parents=True, exist_ok=False)
    data = raw.read_bytes()
    strings = [
        {"raw_offset": match.start(), "length": len(match.group()), "value": match.group().decode("ascii")}
        for match in re.finditer(rb"[\x20-\x7e]{4,}", data)
    ]
    write_jsonl(output / "raw-strings.jsonl", strings)
    starts = [row["raw_offset"] for row in strings]
    functions = {int(row["entry"], 16): row for row in read_jsonl(exported / "functions.jsonl")}
    incoming = defaultdict(list)
    reference_count = 0
    for row in read_jsonl(exported / "references.jsonl"):
        try:
            target = int(row["to"], 16)
            source = int(row["from"], 16)
        except ValueError:
            continue
        incoming[target].append((source, int(row["function"], 16) if row.get("function") else None, "ghidra"))
        reference_count += 1
    pointer_rows = []
    string_pointers = defaultdict(list)
    for offset in range(0, len(data) - 3, 4):
        value = struct.unpack_from("<I", data, offset)[0]
        for base in BASES:
            target_offset = (value & ~1) - base
            if not 0 <= target_offset < len(data):
                continue
            row = {"raw_offset": offset, "value": hex(value), "target_raw_offset": target_offset, "thumb_bit": value & 1}
            pointer_rows.append(row)
            # Alias pointers are literal hypotheses until code use confirms them.
            for locator_base in BASES:
                incoming[value & ~1].append((locator_base + offset, None, "raw_aligned_pointer"))
                if value & 1:
                    incoming[value].append((locator_base + offset, None, "raw_aligned_pointer"))
            index = bisect.bisect_right(starts, value - base) - 1
            if index >= 0:
                string = strings[index]
                if string["raw_offset"] <= value - base < string["raw_offset"] + string["length"]:
                    string_pointers[index].append(row)
    write_jsonl(output / "raw-address-pointers.jsonl", pointer_rows)

    def owners(addresses):
        queue = deque((address, 0, []) for address in addresses)
        visited = set()
        result = {}
        while queue:
            address, depth, chain = queue.popleft()
            if address in visited or depth > 3:
                continue
            visited.add(address)
            for source, function, kind in incoming.get(address, []):
                next_chain = chain + [{"from": hex(source), "to": hex(address), "kind": kind}]
                if function in functions:
                    result.setdefault(function, next_chain)
                elif depth < 3:
                    queue.append((source, depth + 1, next_chain))
        return result

    function_strings = defaultdict(list)
    module_rows = defaultdict(list)
    control_rows = []
    for index, row in enumerate(strings):
        modules = [name for name, pattern in MODULES.items() if pattern.search(row["value"])]
        if not modules:
            continue
        addresses = [base + row["raw_offset"] for base in BASES]
        related = owners(addresses)
        for pointer in string_pointers.get(index, []):
            related.update(owners([base + pointer["raw_offset"] for base in BASES]))
        detail = dict(row, modules=modules, function_xrefs=[{"entry": hex(entry), "chain": chain} for entry, chain in sorted(related.items())])
        for module in modules:
            module_rows[module].append(detail)
        for entry in related:
            function_strings[entry].append({"raw_offset": row["raw_offset"], "value": row["value"], "modules": modules})
        if "control_spp" in modules:
            control_rows.append(detail)
    write_json(output / "strings-by-module.json", module_rows)
    candidates = []
    for entry, matched in function_strings.items():
        row = functions[entry]
        counts = Counter(module for string in matched for module in string["modules"])
        candidates.append({"entry": hex(entry), "name": row["name"], "raw_offset": row["raw_offset"], "module_counts": dict(counts),
                           "strings": matched, "calls": row["calls"], "callers": row["callers"],
                           "decompiled_file": row.get("decompiled_file"), "assembly_file": row.get("assembly_file")})
    candidates.sort(key=lambda row: (-(row["module_counts"].get("control_spp", 0)), row["raw_offset"]))
    write_json(output / "function-module-candidates.json", candidates)
    write_json(output / "control-trace-candidates.json", {
        "classification": "Candidates from strings, raw aligned pointers and up to three reference hops; manual confirmation required",
        "strings": control_rows,
        "functions": [row for row in candidates if row["module_counts"].get("control_spp")],
    })
    summary = {
        "raw_path": str(raw), "raw_bytes": len(data), "raw_sha256": sha256(raw),
        "export_directory": str(exported), "raw_ascii_strings": len(strings), "aligned_image_pointers": len(pointer_rows),
        "ghidra_function_count": len(functions), "ghidra_reference_count": reference_count,
        "module_string_counts": {name: len(rows) for name, rows in module_rows.items()},
        "module_candidate_function_counts": dict(Counter(module for row in candidates for module in row["module_counts"])),
        "control_candidates": sum(bool(row["module_counts"].get("control_spp")) for row in candidates),
        "boundary": "Indexes are triage evidence; raw words, proximity and string labels do not prove executable function semantics.",
    }
    write_json(output / "triage-summary.json", summary)
    print(json.dumps(summary, ensure_ascii=False))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("raw", type=Path)
    parser.add_argument("export_directory", type=Path)
    parser.add_argument("output_directory", type=Path)
    args = parser.parse_args()
    analyze(args.raw, args.export_directory, args.output_directory)
