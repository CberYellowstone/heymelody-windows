#!/usr/bin/env python3
"""Validate v125 Thumb operands and reconstruct its 0x8114 wire layout.

This is static firmware evidence. Reconstructed frames are not captures and do
not demonstrate device connectivity or successful application of any setting.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import struct


RAW_SHA256 = "168c1a32fb4dc46e2b274accb4a2c539cab8ae2ffd388aaa6b9b00bacb583fbd"
EXEC_BASE = 0x0C010000
EXPECTATIONS = {
    0x0C0606EC: ("48f21413", "MOVW r3, #0x8114: response command"),
    0x0C0606F4: ("8b78", "LDRB r3, [r1,#2]: request sequence"),
    0x0C0606F6: ("0222", "MOVS r2,#2: declared payload low byte"),
    0x0C060700: ("8df80f20", "STRB r2,[sp,#15]: declared payload low byte"),
    0x0C060704: ("8df81030", "STRB r3,[sp,#16]: declared payload high byte (r3=0)"),
    0x0C060708: ("8df81130", "STRB r3,[sp,#17]: status byte (r3=0)"),
    0x0C060714: ("adf81230", "STRH r3,[sp,#18]: codec native 0/255 -> reply 1/8"),
    0x0C06071E: ("adf81200", "STRH r0,[sp,#18]: native codec 2 -> reply 2"),
    0x0C060722: ("0822", "MOVS r2,#8: actual inner buffer byte count"),
    0x0C060728: ("fff7acf8", "BL 0x0C05F884: pass all eight inner bytes"),
    0x0C05F88C: ("1746", "MOV r7,r2: preserve inner byte count"),
    0x0C05F8B6: ("3d1d", "ADDS r5,r7,#4: short outer total byte count"),
    0x0C05F8D4: ("bb1c", "ADDS r3,r7,#2: outer length field"),
    0x0C05F94A: ("3a46", "MOV r2,r7: memcpy byte count"),
    0x0C05F950: ("d9f0faf9", "BL 0x0C138D48: memcpy full inner buffer"),
    0x0C05F954: ("2a46", "MOV r2,r5: transport byte count"),
    0x0C05F95A: ("fff797fa", "BL 0x0C05EE8C: transport complete outer frame"),
}


def halves(data: bytes, address: int):
    return struct.unpack_from("<HH", data, address - EXEC_BASE)


def movw_operands(first: int, second: int):
    return {"register": (second >> 8) & 15,
            "immediate": ((first & 15) << 12) | (((first >> 10) & 1) << 11) | (((second >> 12) & 7) << 8) | (second & 255)}


def branch_link_target(first: int, second: int, address: int):
    sign = (first >> 10) & 1
    i1 = 1 ^ (((second >> 13) & 1) ^ sign)
    i2 = 1 ^ (((second >> 11) & 1) ^ sign)
    relative = (sign << 24) | (i1 << 23) | (i2 << 22) | ((first & 1023) << 12) | ((second & 2047) << 1)
    if sign:
        relative -= 1 << 25
    return address + 4 + relative


def verify(raw: Path, output: Path):
    # Reserve the output before reading inputs; an existing path is never reused.
    output.mkdir(parents=True, exist_ok=False)
    data = raw.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if digest != RAW_SHA256:
        raise ValueError("This verifier is limited to the independently preserved Air4 Pro v125 raw SHA256")
    table_entry = 0x0C05E58C + (0x0114 - 0x0100) * 4
    handler_pointer = struct.unpack_from("<I", data, table_entry - EXEC_BASE)[0]
    if handler_pointer != 0x0C05E973:
        raise ValueError("0x0114 dispatch table does not point to the verified Thumb trampoline")
    dispatch_tail_address = 0x0C05E97E
    dispatch_tail_target = branch_link_target(*halves(data, dispatch_tail_address), dispatch_tail_address)
    if dispatch_tail_target != 0x0C0606DC:
        raise ValueError("0x0114 trampoline does not branch to the verified handler")
    evidence = []
    for address, (expected, meaning) in EXPECTATIONS.items():
        value = bytes.fromhex(expected)
        actual = data[address - EXEC_BASE:address - EXEC_BASE + len(value)]
        if actual != value:
            raise ValueError(f"Opcode mismatch at {address:#x}")
        evidence.append({"execution_address": hex(address), "raw_offset": hex(address - EXEC_BASE),
                         "bytes": actual.hex(" "), "manual_meaning": meaning, "verified": True})
    movw = movw_operands(*halves(data, 0x0C0606EC))
    if movw != {"register": 3, "immediate": 0x8114}:
        raise ValueError("Independent MOVW operand decode differs")
    calls = {}
    for address, target in [(0x0C060728, 0x0C05F884), (0x0C05F950, 0x0C138D48), (0x0C05F95A, 0x0C05EE8C)]:
        decoded = branch_link_target(*halves(data, address), address)
        if decoded != target:
            raise ValueError(f"Independent BL target decode differs at {address:#x}")
        calls[hex(address)] = hex(decoded)
    frames = []
    sequence = 0x5A
    for native, reply in [(0, 1), (2, 2), (255, 8)]:
        payload = b"\x00" + struct.pack("<H", reply)
        inner = struct.pack("<HBH", 0x8114, sequence, 2) + payload
        wire = bytes([0xAA, len(inner) + 2, 0, 0]) + inner
        filename = f"0114-native-{native:02x}-sequence-5a-reconstructed.bin"
        with (output / filename).open("xb") as stream:
            stream.write(wire)
        frames.append({"native_a2dp_codec": native, "reply_codec_u16": reply, "sequence": sequence,
                       "declared_payload_bytes": 2, "actual_payload_bytes": len(payload),
                       "actual_inner_bytes": len(inner), "actual_outer_bytes": len(wire),
                       "wire_hex": wire.hex(" "), "file": filename, "sha256": hashlib.sha256(wire).hexdigest()})
    result = {
        "classification": "Static operand verification and reconstruction; frames are not captured from a device",
        "raw_path": str(raw), "raw_bytes": len(data), "raw_sha256": digest,
        "firmware_scope": "PID067C10, full1_all_125_0.bin / build0.1.2.5; other versions unverified",
        "dispatch_table_entry": {"address": hex(table_entry), "command": "0x0114", "thumb_trampoline_pointer": hex(handler_pointer),
                                 "tail_branch_address": hex(dispatch_tail_address), "tail_branch_target": hex(dispatch_tail_target)},
        "verified_instructions": evidence, "independent_movw_decode": movw, "independent_bl_targets": calls,
        "declared_actual_discrepancy": "0x8114 declares payload2 but status0+u16codec occupies3; sender copies inner8 and transports outer12",
        "known_initialized_codec_branches": frames,
        "unknown_branch": "Other native codec bytes skip initialization of the two codec stack bytes; do not infer codec support from them",
        "hardware_validation": "None: no live headset, readback, packet capture, ACK or setting verification",
    }
    with (output / "0114-firmware-wire-evidence.json").open("x", encoding="utf-8") as stream:
        stream.write(json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({"raw_sha256": digest, "verified_opcodes": len(evidence), "independent_bl_targets": calls,
                      "reconstructed_wire": [frame["wire_hex"] for frame in frames]}, ensure_ascii=False))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("raw", type=Path)
    parser.add_argument("output", type=Path)
    arguments = parser.parse_args()
    verify(arguments.raw, arguments.output)
