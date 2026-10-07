"""Read-only instruction evidence for the pinned APK EQ-selection branches.

Run from the repository root; no APK extraction, network request or output file is used.
"""
import struct
import zipfile
import hashlib
import sys
from pathlib import Path

from event_dex_audit import instructions, read_dex


def audit(apk, member, selections):
    with zipfile.ZipFile(apk) as archive:
        data = archive.read(member)
    strings, types, methods, definitions = read_dex(data)
    count, offset = struct.unpack_from("<II", data, 80)
    fields = []
    for index in range(count):
        owner, field_type, name = struct.unpack_from("<HHI", data, offset + index * 8)
        fields.append((types[owner], strings[name], types[field_type]))
    for owner, name, signature, code in definitions:
        if (owner, name) not in selections:
            continue
        print(f"\n{apk} {member} code=0x{code:x} {owner} {name} {signature}")
        for address, opcode, words in instructions(data, code):
            position = (address - code - 16) // 2
            a, b, register = words[0] >> 8 & 15, words[0] >> 12, words[0] >> 8
            detail = ""
            if 0x6e <= opcode <= 0x72:
                registers = [(words[2] >> index * 4) & 15 for index in range(4)] + [a]
                detail = f"{registers[:b]} {methods[words[1]]}"
            elif 0x74 <= opcode <= 0x78:
                detail = f"{list(range(words[2], words[2] + register))} {methods[words[1]]}"
            elif 0x52 <= opcode <= 0x5f:
                detail = f"v{a},v{b} {fields[words[1]]}"
            elif 0x60 <= opcode <= 0x6d:
                detail = f"v{register} {fields[words[1]]}"
            elif 0x32 <= opcode <= 0x3d:
                jump = position + struct.unpack("<h", struct.pack("<H", words[1]))[0]
                detail = f"v{a},v{b} ->{jump}" if opcode <= 0x37 else f"v{register} ->{jump}"
            elif opcode == 0x28:
                detail = f"->{position + struct.unpack('b', bytes([register]))[0]}"
            elif opcode == 0x29:
                detail = f"->{position + struct.unpack('<h', struct.pack('<H', words[1]))[0]}"
            elif opcode in (0x1a, 0x1b):
                index = words[1] if opcode == 0x1a else words[1] + (words[2] << 16)
                detail = f"v{register} {strings[index]!r}"
            elif opcode in (0x1c, 0x1f, 0x22):
                detail = f"v{register} {types[words[1]]}"
            elif opcode == 0x12:
                detail = f"v{a}={b if b < 8 else b - 16}"
            elif opcode in (0x13, 0x16):
                detail = f"v{register}={struct.unpack('<h', struct.pack('<H', words[1]))[0]}"
            print(f"{position:03d} 0x{address:x} op={opcode:02x} " + ",".join(f"{word:04x}" for word in words) + " " + detail)


if __name__ == "__main__":
    function = "Lcom/oplus/melody/common/data/WhitelistConfigDTO$Function;"
    for apk, digest, dto_member, ui_member, ui_owner, utility, getter, initializer in [
        ("research/无线耳机_17.6.3.apk", "c1149df9e24ed85bf84f7c81c712cee794299d2b95057c6e221fb547f2717059", "classes.dex", "classes2.dex", "Lca/F;", "Lcom/oplus/melody/common/util/O;", "g", "p"),
        ("research/欢律.apk", "eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4", "classes3.dex", "classes3.dex", "LAb/y0;", "Lcom/oplus/melody/common/util/S;", "i", "r"),
    ]:
        with Path(apk).open("rb") as file:
            if hashlib.file_digest(file, "sha256").hexdigest() != digest:
                raise ValueError("Pinned APK hash differs")
        audit(apk, dto_member, {(function, "getEqualizerModeByColorId"), (function, "getEqualizerModeByColorId$lambda$4")})
        audit(apk, ui_member, {(ui_owner, "refreshEqModelList")})
        if "--app-markers" in sys.argv:
            audit(apk, dto_member, {(utility, getter), (utility, initializer)})
