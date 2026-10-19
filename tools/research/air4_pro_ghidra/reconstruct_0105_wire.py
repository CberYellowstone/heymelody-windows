#!/usr/bin/env python3
"""Reconstruct the verified v125 nine-tuple 0x8105 layout for offline tests.

The chosen peer/box state is illustrative. These bytes are not device captures.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import struct


RAW_SHA256 = "168c1a32fb4dc46e2b274accb4a2c539cab8ae2ffd388aaa6b9b00bacb583fbd"


def nul_string(data: bytes, offset: int):
    return data[offset:data.index(0, offset)].decode("ascii")


def leb(value: int):
    output = bytearray()
    while True:
        byte = value & 127
        value >>= 7
        output.append(byte | (128 if value else 0))
        if not value:
            return bytes(output)


def reconstruct(raw: Path, output: Path, box_version: int):
    # Reserve the output before reading inputs; an existing path is never reused.
    output.mkdir(parents=True, exist_ok=False)
    data = raw.read_bytes()
    if hashlib.sha256(data).hexdigest() != RAW_SHA256:
        raise ValueError("This reconstruction is limited to the preserved v125 image")
    expected_formats = {
        0x130DC7: "%d,%d,%03d,%d,%d,%s,%d,%d,%s,%d,%d,%03d,%d,%d,%s,%d,%d,%s",
        0x130E01: ",%d,%d,%03d,%d,%d,%03d,%d,%d,%03d",
        0x12A704: "0.1.2.5",
    }
    for offset, expected in expected_formats.items():
        if nul_string(data, offset) != expected:
            raise ValueError(f"Format/build string mismatch at {offset:#x}")
    if not 0 <= box_version <= 999:
        raise ValueError("Illustrative box version must fit the observed three-digit examples")
    examples = []
    for local in (1, 2):
        peer = 2 if local == 1 else 1
        versions = {local: 125, peer: 0, 3: box_version}
        tuples = [(device, kind, 111 if kind == 1 else versions[device]) for device in (1, 2, 3) for kind in (1, 2, 3)]
        ascii_value = ",".join(f"{device},{kind},{version:03d}" for device, kind, version in tuples)
        payload = bytes([0, len(tuples)]) + ascii_value.encode("ascii")
        inner = struct.pack("<HBH", 0x8105, 0x5A, len(payload)) + payload
        wire = b"\xaa" + leb(len(inner) + 2) + bytes([0, 0]) + inner
        stem = f"0105-local-{local}-125-peer-{peer}-000-box-{box_version:03d}"
        with (output / f"{stem}.ascii.txt").open("x", encoding="ascii") as stream:
            stream.write(ascii_value)
        with (output / f"{stem}.payload.bin").open("xb") as stream:
            stream.write(payload)
        with (output / f"{stem}.wire.bin").open("xb") as stream:
            stream.write(wire)
        examples.append({"local_device_type": local, "peer_device_type": peer, "illustrative_box_version": box_version,
                         "ascii": ascii_value, "tuple_count": len(tuples), "payload_bytes": len(payload),
                         "inner_bytes": len(inner), "wire_bytes": len(wire), "wire_hex": wire.hex(" "),
                         "payload_file": f"{stem}.payload.bin", "wire_file": f"{stem}.wire.bin"})
    report = {
        "classification": "Static reconstruction of a verified firmware formatter, not headset capture or real state",
        "raw_sha256": RAW_SHA256,
        "source": "0c05e4a4.c:1044-1118; 0c05e4a4.asm:0105 handler; 05fdc8/05fd7c/05fb3c helpers",
        "wire_layout": "0x8105, echoed request seq, LE payload length, status0, tupleCount9, ASCII nine triplets without NUL",
        "version_fields": "DeviceType1/2: VersionType1 fixed111; VersionType2 and3 local125/peer current global; DeviceType3 fixed111 plus dynamic box version twice",
        "peer_zero_condition": "peer global20013924==0 and TWS-connected helper false leaves000; disconnection does not itself force a previously known peer to zero",
        "peer_fallback_condition": "when TWS helper is true and peer global zero, both local and peer are formatted125",
        "box_boundary": "Box firmware comes from053d18; example000 is chosen, not asserted as live box firmware",
        "examples": examples,
    }
    with (output / "0105-static-reconstruction.json").open("x", encoding="utf-8") as stream:
        stream.write(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({"raw_sha256": RAW_SHA256, "examples": [{key: row[key] for key in ("ascii", "tuple_count", "payload_bytes", "wire_bytes", "payload_file")} for row in examples]}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("raw", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--box-version", type=int, default=0)
    args = parser.parse_args()
    reconstruct(args.raw, args.output, args.box_version)
