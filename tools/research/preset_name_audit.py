"""Read-only, bounded symbolic evaluation of the two pinned EQ name mappers.

Only the mapper's constant, equality, switch and getString operations are supported;
any unfamiliar instruction or call fails. No APK is loaded into an Android runtime.
"""
import hashlib
from pathlib import Path
import struct
import sys
import zipfile

from event_dex_audit import instructions, read_dex

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools/research/earbud-artwork/scripts"))
from android_resources import resource_table


def mapper(apk, member, owner):
    with zipfile.ZipFile(apk) as archive:
        data = archive.read(member)
        resources = resource_table(archive.read("resources.arsc"))
    strings, _, methods, definitions = read_dex(data)
    code = next(code for c, name, _, code in definitions if c == owner and name == "d")
    operations = {(address - code - 16) // 2: (opcode, words) for address, opcode, words in instructions(data, code)}
    chinese = {int(entry["resource_id"], 16): (text, entry["android_name"]) for text, entries in resources.items()
               for entry in entries if entry["qualifiers"]["qualifier"] == "zh-rCN"}

    def resolve(mode, product_name, equalizer_version):
        registers = [None] * struct.unpack_from("<H", data, code)[0]
        registers[-3:] = ["context", mode, product_name]
        position, result = 0, None
        for _ in range(500):
            opcode, words = operations[position]
            next_position = position + len(words)
            a, b, register = words[0] >> 8 & 15, words[0] >> 12, words[0] >> 8
            if opcode == 0x12:
                registers[a] = b if b < 8 else b - 16
            elif opcode == 0x14:
                registers[register] = words[1] | words[2] << 16
            elif opcode == 0x1a:
                registers[register] = strings[words[1]]
            elif opcode in (0x0a, 0x0c):
                registers[register] = result
            elif opcode == 0x11:
                return registers[register]
            elif opcode in (0x38, 0x39):
                if bool(registers[register]) == (opcode == 0x39):
                    next_position = position + struct.unpack("<h", struct.pack("<H", words[1]))[0]
            elif opcode in (0x32, 0x33):
                if (registers[a] == registers[b]) == (opcode == 0x32):
                    next_position = position + struct.unpack("<h", struct.pack("<H", words[1]))[0]
            elif opcode == 0x28:
                next_position = position + struct.unpack("b", bytes([register]))[0]
            elif opcode == 0x2b:
                payload = operations[position + (words[1] | words[2] << 16)][1]
                index = registers[register] - (payload[2] | payload[3] << 16)
                if 0 <= index < payload[1]:
                    next_position = position + (payload[4 + index * 2] | payload[5 + index * 2] << 16)
            elif opcode in (0x6e, 0x71):
                indices = [(words[2] >> i * 4) & 15 for i in range(4)] + [a]
                values = [registers[index] for index in indices[:b]]
                target = methods[words[1]]
                if target == ("Landroid/content/Context;", "getString", "(I)Ljava/lang/String;"):
                    result = values[1]
                elif target == ("Ljava/lang/String;", "equals", "(Ljava/lang/Object;)Z"):
                    result = values[0] == values[1]
                elif target == (owner, "f", "(Ljava/lang/String;)I"):
                    result = equalizer_version
                else:
                    raise ValueError(f"Unproven call: {target}")
            else:
                raise ValueError(f"Unproven instruction: {opcode:x} at {position}")
            position = next_position
        raise ValueError("Mapper exceeded instruction budget")

    print(apk, member, owner, f"code=0x{code:x}")
    for mode in range(1, 47):
        default = resolve(mode, "Other", 1)
        print(mode, f"0x{default:08x}", *chinese[default], sep=" | ")
        for name, equalizer in (("Other", 2), ("OPPO Enco R", 1), ("OPPO Enco Air2", 1)):
            selected = resolve(mode, name, equalizer)
            if selected != default:
                print("  ", name, f"equalizer={equalizer}", f"0x{selected:08x}", *chinese[selected], sep=" | ")


if __name__ == "__main__":
    for apk, digest, member, owner in [
        ("research/apps/无线耳机_17.6.3.apk", "c1149df9e24ed85bf84f7c81c712cee794299d2b95057c6e221fb547f2717059", "classes2.dex", "Lca/y;"),
        ("research/apps/欢律.apk", "eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4", "classes3.dex", "LAb/h0;"),
    ]:
        with Path(apk).open("rb") as file:
            if hashlib.file_digest(file, "sha256").hexdigest() != digest:
                raise ValueError("Pinned APK hash differs")
        mapper(apk, member, owner)
