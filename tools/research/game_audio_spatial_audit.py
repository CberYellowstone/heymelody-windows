"""Archive and audit the pinned game-audio/Hi-Res/spatial evidence offline.

No network or device access. Defaults to a new research/ run; never overwrites a
previous run. Original DEX instructions, unchanged historical helper versions,
source/config snapshots and input preservation receipts are kept together.
"""
from __future__ import annotations

import argparse
import contextlib
import datetime as dt
import hashlib
import importlib.util
import io
import json
import re
from pathlib import Path
import shutil
import struct
import sys
import zipfile

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
PREVIOUS = ROOT / "research/calibration-reassessment-20261009"
PINNED = {
    "research/apps/无线耳机_17.6.3.apk": "c1149df9e24ed85bf84f7c81c712cee794299d2b95057c6e221fb547f2717059",
    "research/apps/欢律.apk": "eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4",
    "research/OPPO_Enco_X4_v116_reconstruction/binary/ap.bin": "0924bfe293f7d0b40dffb012eb4fdf4014374e4cc60c85b32a36fe7716802f24",
    "research/OPPO_Enco_X4_v116_reconstruction/binary/1_all_116_0.bin": "e34a6f0982f45b855b2d7f3372080664dd1c47bbc9918f4dae2b818acd3b4680",
}
PACKAGES = [
    {"key": "wireless", "apk": "research/apps/无线耳机_17.6.3.apk", "package": "com.oplus.melody", "version": "17.6.3", "version_code": 17006003,
     "ui_member": "classes2.dex", "switch": "Lma/c;", "mutex": "Lma/m;", "utility": "Lcom/oplus/melody/common/util/O;"},
    {"key": "heymelody", "apk": "research/apps/欢律.apk", "package": "com.heytap.headset", "version": "17.6.5", "version_code": 117006005,
     "ui_member": "classes3.dex", "switch": "LAb/x;", "mutex": "LKb/y;", "utility": "Lcom/oplus/melody/common/util/S;"},
]
SHARED_SOURCES = [
    "com/oplus/melody/ui/component/detail/gamemode/GameSetFragment.java",
    "com/oplus/melody/ui/component/detail/gamemode/GameSetViewModel.java",
    "com/oplus/melody/ui/component/detail/gamemode/GameSetItem.java",
    "com/oplus/melody/ui/component/detail/tonequality/HiQualityAudioItem.java",
    "com/oplus/melody/ui/component/detail/spatialaudio/SpatialAudioItem.java",
    "com/oplus/melody/ui/component/detail/spatialaudio/BaseSpatialAudioVM.java",
    "com/oplus/melody/ui/component/detail/spatialaudio/d.java",
    "com/oplus/melody/ui/component/detail/hearingoptimize/HearingOptimizeItem.java",
    "com/oplus/melody/btsdk/manager/service/BluetoothService.java",
    "com/oplus/melody/btsdk/multidevice/HeadsetCoreService.java",
    "com/oplus/melody/btsdk/protocol/commands/d.java",
    "com/oplus/melody/common/data/WhitelistConfigDTO.java",
]
SPECIFIC_SOURCES = {
    "wireless": ["Ma/C1325c.java", "Ma/C1335m.java", "H6/C1151e.java", "ca/r.java", "e9/v.java", "c9/H.java", "N8/a.java", "r7/C1489b.java", "com/oplus/melody/common/util/O.java", "com/oplus/melody/btsdk/protocol/commands/n.java"],
    "heymelody": ["Ab/C0401x.java", "Ab/Z.java", "Kb/y.java", "r7/c.java", "r7/d.java", "wa/a.java", "Ma/D.java", "U8/C0539b.java", "bc/DialogInterfaceOnClickListenerC0856a.java", "com/oplus/melody/common/util/S.java", "com/oplus/melody/btsdk/protocol/commands/m.java"],
}


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_json(path: Path, value) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def load_module(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, help="new output directory under research/")
    args = parser.parse_args()
    started = dt.datetime.now(dt.timezone.utc)
    output = (args.output or ROOT / "research/game-audio-spatial-20261010/runs" / started.strftime("%Y%m%dT%H%M%S.%fZ")).resolve()
    if not output.is_relative_to((ROOT / "research").resolve()):
        parser.error("output must be inside this repository's research/")
    if output.exists():
        parser.error("output already exists; use a new directory to retain historical evidence")
    output.mkdir(parents=True)
    receipts = []
    missing = []

    def preserve(path: Path, category="input", required=True):
        if not path.is_file():
            missing.append({"path": str(path.relative_to(ROOT)), "required": required})
            if required:
                raise FileNotFoundError(path)
            return None
        relative = path.relative_to(ROOT)
        archive = output / "originals" / relative
        archive.parent.mkdir(parents=True, exist_ok=True)
        before = digest(path)
        if str(relative).replace("\\", "/") in PINNED and before != PINNED[relative.as_posix()]:
            raise ValueError(f"Pinned hash differs: {relative}")
        shutil.copyfile(path, archive)
        copied = digest(archive)
        if copied != before:
            raise ValueError(f"Copy hash differs: {relative}")
        receipts.append({"original_path": relative.as_posix(), "archive_path": archive.relative_to(output).as_posix(), "category": category,
                         "bytes": path.stat().st_size, "sha256": before, "source_mtime_utc": dt.datetime.fromtimestamp(path.stat().st_mtime, dt.timezone.utc).isoformat(), "copy_sha256": copied})
        return archive

    for path in PINNED:
        preserve(ROOT / path, "pinned-original")
    preserve(ROOT / "research/OPPO_Enco_X4_v116_reconstruction/metadata/regions.json", "firmware-address-map")
    for path in [Path(__file__), ROOT / "tools/research/equalizer_dex_audit.py", ROOT / "tools/research/event_dex_audit.py",
                 PREVIOUS / "evidence/firmware/inspect_firmware.py", PREVIOUS / "evidence/input-manifest.json"]:
        preserve(path, "tool-or-provenance")
    dependencies = PREVIOUS / "tools/python-packages"
    for dependency in [dependencies / "capstone", *sorted(dependencies.glob("capstone-*.dist-info"))]:
        for path in sorted(dependency.rglob("*")):
            if path.is_file() and "__pycache__" not in path.parts:
                preserve(path, "firmware-tool-dependency")
    for relative in ["metadata/manual_symbols.json", "metadata/decompiler_updates/functions/main/100b0b10.json", "src/decompiled/main/100b0b10.c", "disassembly/main/100b0b10.asm"]:
        preserve(ROOT / "research/OPPO_Enco_X4_v116_reconstruction" / relative, "historical-unreviewed-annotation", required=False)
    for package in PACKAGES:
        base = PREVIOUS / "decompiled" / package["key"]
        for relative in SHARED_SOURCES + SPECIFIC_SOURCES[package["key"]]:
            preserve(base / "sources" / relative, "jadx-source", required=False)
        for relative in ["res/values-zh-rCN/strings.xml", "AndroidManifest.xml"]:
            preserve(base / "resources" / relative, "apk-decoded-resource")
        manifest = (base / "resources/AndroidManifest.xml").read_text(encoding="utf-8")
        if f'package="{package["package"]}"' not in manifest or f'android:versionName="{package["version"]}"' not in manifest or f'android:versionCode="{package["version_code"]}"' not in manifest:
            raise ValueError(f"Decoded manifest identity mismatch for {package['key']}")
    excerpts = []
    keywords = re.compile(r"param_game_sound_type|setGameSound|gameSoundMutex|getGameSoundMutex|getSelectType|getRealSupportTypes|isPackagesEmpty|melody_ui_game_sound_type|dealSwitchNoticeDialog|switchSpatial|highToneQuality|HighToneQuality|isHiQualityV2|setHiResSoundEnable|spatialHiResMutex|SpatialHiResMutex|isPhoneSupportSpatial|com\.heytap\.headset|case 1077|1059|Q0\(27")
    for receipt in receipts:
        if receipt["category"] not in {"jadx-source", "apk-decoded-resource"}:
            continue
        lines = (ROOT / receipt["original_path"]).read_text(encoding="utf-8").splitlines()
        selected = set()
        for index, line in enumerate(lines):
            if len(line) < 1000 and keywords.search(line):
                selected.update(range(max(0, index - 3), min(len(lines), index + 4)))
        if not selected:
            continue
        excerpts.extend(["", f"SOURCE {receipt['original_path']} SHA256 {receipt['sha256']}"])
        last = -2
        for index in sorted(selected):
            if index != last + 1:
                excerpts.append("...")
            excerpts.append(f"{index+1:5}: {lines[index]}")
            last = index
    target = output / "evidence/source-excerpts.txt"
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text("\n".join(excerpts) + "\n", encoding="utf-8")

    # Both compatibility and normal lists are real inputs. Preserve source list
    # and index rather than silently merging duplicate model IDs.
    config_paths = [ROOT / "src/HeyMelody.Resources/Bundles/heymelody.json", ROOT / "src/HeyMelody.Resources/Bundles/wireless.json"]
    config_paths += sorted((PREVIOUS / "evidence/apk-config").glob("*.whitelist.json"))
    historical = ROOT / "research/online-whitelist/runs/20261008T051557.800877Z"
    config_paths += sorted(historical.glob("com.*/builtin.json")) + sorted(historical.glob("com.*/cloud.json"))
    for path in sorted(historical.rglob("*")):
        if path.is_file() and path not in config_paths:
            preserve(path, "historical-cloud-run")
    matrix = []
    keys = ["gameMode", "gameSoundList", "gameSoundMutexes", "highToneQuality", "spatialHiResMutex", "spatialTypes", "spatialDescriptionType", "bassEngineSupport", "hearingOptimize", "equalizer"]
    for path in config_paths:
        preserve(path, "configuration")
        config = json.loads(path.read_text(encoding="utf-8-sig"))
        for group in ["compatWhiteList", "whiteList"]:
            for index, model in enumerate(config.get(group) or []):
                function = model.get("function") or {}
                if not any(function.get(k) for k in ["gameSoundList", "gameSoundMutexes", "spatialHiResMutex"]):
                    continue
                matrix.append({"source_path": path.relative_to(ROOT).as_posix(), "config_sha256": digest(path), "config_versionCode": config.get("versionCode"),
                               "config_compatVersionCode": config.get("compatVersionCode"), "list": group, "index": index, "id": model.get("id"), "name": model.get("name"),
                               "minVersion": model.get("minVersion"), "functions": {key: function[key] for key in keys if key in function}})
    write_json(output / "evidence/configuration-matrix.json", matrix)
    x4 = [row for row in matrix if row["id"] == "06EC10"]
    if len(x4) != len(config_paths) or any([entry["type"] for entry in row["functions"]["gameSoundList"]] != [3, 0] or row["functions"].get("gameSoundMutexes") != [1, 3] for row in x4):
        raise ValueError("Expected one X4 [3,0]/[1,3] record in each current configuration input")
    write_json(output / "evidence/x4-configurations.json", x4)
    summary = ["# Configuration scan", "", "Historical cloud run: 20261008T051557.800877Z; no new request.", "", "| Source | Config version | Models in scope | Game types/mutex patterns |", "| --- | --- | --- | --- |"]
    for path in config_paths:
        rows = [row for row in matrix if row["source_path"] == path.relative_to(ROOT).as_posix()]
        patterns = sorted({str(([x["type"] for x in row["functions"].get("gameSoundList", [])], row["functions"].get("gameSoundMutexes"))) for row in rows if row["functions"].get("gameSoundList")})
        summary.append(f"| {path.relative_to(ROOT).as_posix()} | {rows[0]['config_versionCode'] if rows else None} | {len(rows)} | {'; '.join(patterns)} |")
    (output / "evidence/configuration-summary.md").write_text("\n".join(summary) + "\n", encoding="utf-8")

    sys.path.insert(0, str(ROOT / "tools/research"))
    from equalizer_dex_audit import audit, read_dex
    dex_index = []
    for package in PACKAGES:
        apk = ROOT / package["apk"]
        members = [package["ui_member"]]
        if "classes.dex" not in members:
            members.append("classes.dex")
        for member in members:
            with zipfile.ZipFile(apk) as archive:
                raw = archive.read(member)
            strings, types, methods, definitions = read_dex(raw)
            selections = set()
            for owner, name, signature, code in definitions:
                relevant = owner in {package["switch"], package["mutex"], package["utility"], "Lcom/oplus/melody/ui/component/detail/gamemode/a;"}
                relevant |= owner.startswith("Lcom/oplus/melody/ui/component/detail/tonequality/HiQualityAudioItem")
                relevant |= owner in {"Lcom/oplus/melody/ui/component/detail/spatialaudio/d;", "Lcom/oplus/melody/ui/component/detail/spatialaudio/SpatialAudioItem;", "Lcom/oplus/melody/ui/component/detail/gamemode/GameSetViewModel;", "Lcom/oplus/melody/ui/component/detail/gamemode/GameSetItem;", "Le9/v;"}
                relevant |= owner in {"LH6/e;", "Lr7/d;", "Lc9/H;", "Lr7/c;", "LPa/n;", "Lbc/a;", "LMa/D;"}
                relevant |= owner in {"Lcom/oplus/melody/btsdk/multidevice/HeadsetCoreService;", "Lcom/oplus/melody/btsdk/protocol/commands/n;", "Lcom/oplus/melody/btsdk/protocol/commands/m;", "Lcom/oplus/melody/btsdk/protocol/commands/d;", "Lcom/oplus/melody/btsdk/manager/service/BluetoothService;"} and name in {"Q0", "M", "c", "onStartCommand"}
                if relevant and code:
                    selections.add((owner, name))
                    dex_index.append({"package": package["package"], "member": member, "member_sha256": hashlib.sha256(raw).hexdigest(), "owner": owner, "method": name, "signature": signature, "code_offset": f"0x{code:X}"})
            buffer = io.StringIO()
            with contextlib.redirect_stdout(buffer):
                audit(str(apk), member, selections)
            target = output / "evidence/dex" / f"{package['key']}-{member}.txt"
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(buffer.getvalue(), encoding="utf-8")
    write_json(output / "evidence/dex-method-index.json", dex_index)
    expected = {
        ("com.oplus.melody", "classes2.dex", "Lma/c;", "h"): "0x34FF08",
        ("com.oplus.melody", "classes2.dex", "Lcom/oplus/melody/ui/component/detail/gamemode/GameSetViewModel;", "h"): "0x26ED8C",
        ("com.oplus.melody", "classes2.dex", "Lcom/oplus/melody/ui/component/detail/gamemode/a;", "D"): "0x26DC80",
        ("com.heytap.headset", "classes3.dex", "LAb/x;", "h"): "0x11A9A4",
        ("com.heytap.headset", "classes3.dex", "Lcom/oplus/melody/ui/component/detail/gamemode/GameSetViewModel;", "i"): "0x326B98",
        ("com.heytap.headset", "classes3.dex", "Lcom/oplus/melody/ui/component/detail/gamemode/a;", "C"): "0x3259FC",
    }
    actual = {(row["package"], row["member"], row["owner"], row["method"]): row["code_offset"] for row in dex_index}
    for key, offset in expected.items():
        if actual.get(key) != offset:
            raise ValueError(f"Original DEX method mismatch: {key} expected {offset}, actual {actual.get(key)}")

    # Use the old read-only helper unchanged; its archive copy is separately
    # hashed. This intentionally retains its existing Capstone/address mapping.
    firmware = load_module("game_audio_firmware_evidence", PREVIOUS / "evidence/firmware/inspect_firmware.py")
    import capstone
    ranges = {"game-get": (0x100AF1D0, 0x100AF274), "game-set": (0x100AF960, 0x100AFB30),
              "game-switch": (0x100AF7A0, 0x100AF890), "feature-dispatch": (0x100B0B10, 0x100B0D50),
              "game-callback-wrappers": (0x100B0FF0, 0x100B1010), "spp-dispatch": (0x10067C3C, 0x100694A0)}
    ranges.update({"012B-dispatch-entry": (0x10068248, 0x1006828A), "0423-dispatch-entry": (0x100690E8, 0x10069108), "0403-dispatch-entry": (0x1006905C, 0x100690AC)})
    ranges.update({"callback-table-registration": (0x100B21E8, 0x100B21F4), "callback-runtime-pointer-store": (0x10062380, 0x1006238C)})
    fw_dir = output / "evidence/firmware"
    fw_dir.mkdir(parents=True)
    for label, (start, end) in ranges.items():
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            firmware.disasm(start, end)
        (fw_dir / f"{label}.asm").write_text(buffer.getvalue(), encoding="utf-8")
    dispatch = []
    for command, base, index in [(0x012B, 0x10067C8E, 0x2B), (0x0403, 0x10067F7A, 3), (0x0423, 0x10067F7A, 0x23)]:
        table = base + 2 * index
        value = struct.unpack_from("<H", firmware.data, firmware.offset(table))[0]
        dispatch.append({"command": f"{command:04X}", "table_entry_va": f"0x{table:X}", "raw_bytes": firmware.data[firmware.offset(table):firmware.offset(table)+2].hex(), "halfword": value, "target_va": f"0x{base+2*value:X}"})
    write_json(fw_dir / "raw-command-table.json", dispatch)
    callback_slots = []
    for displacement, expected_pointer in [(0x160, 0x100B0B11), (0x1EC, 0x100B0FF1), (0x1F0, 0x100B0FFD)]:
        address = 0x201AA0D4 + displacement
        pointer = firmware.u32(address)
        if pointer != expected_pointer:
            raise ValueError(f"Callback pointer mismatch at 0x{address:X}")
        callback_slots.append({"initialized_table_va": "0x201AA0D4", "runtime_pointer_va": "0x201EE3C4", "slot_va": f"0x{address:X}", "offset": f"0x{displacement:X}",
                               "ap_offset": f"0x{firmware.offset(address):X}", "raw_bytes": firmware.data[firmware.offset(address):firmware.offset(address)+4].hex(), "thumb_pointer": f"0x{pointer:X}", "handler_va": f"0x{pointer & ~1:X}"})
    write_json(fw_dir / "callback-table-raw.json", callback_slots)

    # Confirm every source remained untouched throughout this audit.
    for receipt in receipts:
        receipt["source_after_sha256"] = digest(ROOT / receipt["original_path"])
        if receipt["source_after_sha256"] != receipt["sha256"]:
            raise ValueError(f"Input changed during audit: {receipt['original_path']}")
    write_json(output / "input-manifest.json", {"started_utc": started.isoformat(), "finished_utc": dt.datetime.now(dt.timezone.utc).isoformat(), "python": sys.version,
               "packages": PACKAGES, "capstone_loaded_version": capstone.__version__, "inputs": receipts, "missing_optional_sources": missing, "scope": "offline static analysis; no hardware, Android runtime or network", "configuration_records": len(matrix), "x4_records": len(x4)})
    print(json.dumps({"output": str(output), "preserved_inputs": len(receipts), "configuration_records": len(matrix), "x4_records": len(x4), "dex_methods": len(dex_index), "optional_missing": len(missing), "all_input_hashes_unchanged": True}, ensure_ascii=False))


if __name__ == "__main__":
    main()
