"""Read-only ANC evidence for two pinned local APKs; Python 3.11+, stdlib only.

Checks the main/child index call chains, interprets the actual DEX serializer,
and prints payloads. --disassembly also prints the selected instruction bodies.
No APK extraction, Android execution, network, device access or file output.
"""
from __future__ import annotations

import argparse
import hashlib
import struct
import sys
import zipfile
from pathlib import Path

sys.dont_write_bytecode = True
from equalizer_dex_audit import audit, instructions, read_dex


ROOT = Path(__file__).resolve().parents[2]
INFO = "Lcom/oplus/melody/btsdk/api/data/CurrentNoiseModeInfo;"
UI = "Lcom/oplus/melody/ui/component/detail/noisereduction/NoiseReductionItem;"
MODE = "Lcom/oplus/melody/common/data/WhitelistConfigDTO$NoiseReductionMode;"
REPO = "Lcom/oplus/melody/model/repository/earphone/"
CORE = "Lcom/oplus/melody/btsdk/multidevice/HeadsetCoreService;"
SAMPLES = (
    {
        "apk": "research/apps/无线耳机_17.6.3.apk",
        "sha256": "c1149df9e24ed85bf84f7c81c712cee794299d2b95057c6e221fb547f2717059",
        "ui_dex": "classes2.dex", "sdk_dex": "classes.dex",
        "child": "LBa/m;", "repository": REPO + "J;",
        "entry": "v0", "typed": "u0",
        "supplier": "Lcom/oplus/melody/health/repository/q;",
        "supplier_branch": 2, "bit_set": 0x1D9C36,
        "bit_args": [2, 1, 0], "type_args": [2, 0], "true_constant": 0x1012,
        "core_args": [0, 4, 1, 5],
    },
    {
        "apk": "research/apps/欢律.apk",
        "sha256": "eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4",
        "ui_dex": "classes3.dex", "sdk_dex": "classes3.dex",
        "child": "LZb/g;", "repository": REPO + "O;",
        "entry": "m0", "typed": "l0",
        "supplier": REPO + "y;",
        "supplier_branch": 1, "bit_set": 0x2D783E,
        "bit_args": [6, 4, 3], "type_args": [6, 3], "true_constant": 0x1312,
        "core_args": [1, 5, 2, 6],
    },
)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def signed16(value):
    return value if value < 0x8000 else value - 0x10000


def call_registers(opcode, words):
    if 0x6E <= opcode <= 0x72:
        count = words[0] >> 12
        return [(words[2] >> (4 * index)) & 15 for index in range(4)][:count] + (
            [words[0] >> 8 & 15] if count == 5 else [])
    return list(range(words[2], words[2] + (words[0] >> 8)))


class DexImage:
    def __init__(self, data):
        self.data = data
        self.strings, self.types, self.methods, definitions = read_dex(data)
        self.definitions = {(c, n, p): offset for c, n, p, offset in definitions}
        count, offset = struct.unpack_from("<II", data, 80)
        self.fields = []
        for index in range(count):
            owner, field_type, name = struct.unpack_from("<HHI", data, offset + index * 8)
            self.fields.append((self.types[owner], self.strings[name], self.types[field_type]))

    def method(self, owner, name, signature=None):
        matches = [(key, offset) for key, offset in self.definitions.items()
                   if key[0] == owner and key[1] == name and (signature is None or key[2] == signature)]
        require(len(matches) == 1 and matches[0][1] != 0, f"Ambiguous/missing method {owner}->{name}")
        return matches[0]

    def body(self, owner, name, signature=None):
        _, offset = self.method(owner, name, signature)
        return list(instructions(self.data, offset))

    def calls(self, owner, name, signature=None):
        return [(address, self.methods[words[1]], call_registers(op, words))
                for address, op, words in self.body(owner, name, signature)
                if 0x6E <= op <= 0x72 or 0x74 <= op <= 0x78]

    def one_call(self, owner, name, target_owner, target_name):
        hits = [call for call in self.calls(owner, name)
                if call[1][:2] == (target_owner, target_name)]
        require(len(hits) == 1, f"Expected one {target_owner}->{target_name} in {owner}->{name}")
        return hits[0]

    def run_serializer(self, owner, name, values):
        """Bounded interpreter for serializer/bitmask predicate opcodes only.

        Reads the pinned DEX instructions; unexpected instructions/calls fail.
        Values are synthetic Python fields/arrays, never Android objects.
        """
        _, offset = self.method(owner, name, "()V" if name == "<init>" else None)
        count, incoming = struct.unpack_from("<HH", self.data, offset)
        regs = [None] * count
        require(len(values) == incoming, "Serializer argument count differs")
        regs[count - incoming:] = values
        body = {address: (op, words) for address, op, words in instructions(self.data, offset)}
        pc, result = offset + 16, None
        for _ in range(10000):
            require(pc in body, f"Invalid serializer instruction address {pc:x}")
            op, w = body[pc]
            next_pc = pc + 2 * len(w)
            a, b, r = w[0] >> 8 & 15, w[0] >> 12, w[0] >> 8
            if op == 0x01:  # move
                regs[a] = regs[b]
            elif op == 0x0E:
                return None
            elif op in (0x0A, 0x0C):
                regs[r] = result
            elif op in (0x0F, 0x11):
                return regs[r]
            elif op == 0x12:
                regs[a] = b if b < 8 else b - 16
            elif op == 0x13:
                regs[r] = signed16(w[1])
            elif op == 0x21:
                regs[a] = len(regs[b])
            elif op == 0x23:
                require(self.types[w[1]] in ("[B", "[Z"), "Unexpected serializer array type")
                regs[a] = [0] * regs[b]
            elif op in (0x47, 0x4E, 0x4F):  # aget-boolean, aput-boolean/byte
                array, index = w[1] & 255, w[1] >> 8
                if op == 0x47:
                    regs[r] = int(regs[array][regs[index]])
                else:
                    regs[array][regs[index]] = regs[r] & 255
            elif op in (0x52, 0x54):
                field = self.fields[w[1]]
                require(field[0] == INFO, "Unexpected serializer field owner")
                regs[a] = regs[b][field[1]]
            elif op in (0x59, 0x5B):
                field = self.fields[w[1]]
                require(field[0] == INFO, "Unexpected serializer field owner")
                regs[b][field[1]] = regs[a]
            elif op in (0x70, 0x71):
                target = self.methods[w[1]]
                args = [regs[index] for index in call_registers(op, w)]
                if target == (INFO, "isModeBitmaskType", "(I)Z"):
                    result = self.run_serializer(INFO, "isModeBitmaskType", args)
                elif target == (INFO, "growArray", "(I)V"):
                    result = self.run_serializer(INFO, "growArray", args)
                elif target == ("Lcom/oplus/melody/common/data/a;", "<init>", "()V"):
                    result = None  # Parcelable superclass; no fields used by this interpreter.
                elif target == ("Ljava/util/Arrays;", "copyOf", "([ZI)[Z"):
                    original, length = args
                    result = original[:length] + [0] * max(0, length - len(original))
                elif target == ("Ljava/lang/System;", "arraycopy", "(Ljava/lang/Object;ILjava/lang/Object;II)V"):
                    source, start, destination, dest_start, length = args
                    destination[dest_start:dest_start + length] = source[start:start + length]
                    result = None
                else:
                    raise ValueError(f"Unsupported serializer call: {target}")
            elif op == 0x8D:  # int-to-byte
                regs[a] = (regs[b] + 128) % 256 - 128
            elif op == 0x98:  # shl-int
                regs[r] = regs[w[1] & 255] << (regs[w[1] >> 8] & 31)
            elif op in (0xB0, 0xB1, 0xB3, 0xB6):
                left, right = regs[a], regs[b]
                regs[a] = {0xB0: lambda: left + right, 0xB1: lambda: left - right,
                           0xB3: lambda: int(left / right), 0xB6: lambda: left | right}[op]()
            elif op in (0xD8, 0xDA, 0xDB):  # add/mul/div-int/lit8
                literal = w[1] >> 8
                literal = literal if literal < 128 else literal - 256
                value = regs[w[1] & 255]
                regs[r] = (value + literal if op == 0xD8 else
                           value * literal if op == 0xDA else int(value / literal))
            elif op == 0x28:
                jump = r if r < 128 else r - 256
                next_pc = pc + jump * 2
            elif op in (0x32, 0x33, 0x34, 0x35, 0x38):
                condition = (regs[a] == regs[b] if op == 0x32 else
                             regs[a] != regs[b] if op == 0x33 else
                             regs[a] < regs[b] if op == 0x34 else
                             regs[a] >= regs[b] if op == 0x35 else regs[r] == 0)
                if condition:
                    next_pc = pc + signed16(w[1]) * 2
            else:
                raise ValueError(f"Unsupported serializer opcode {op:02x} at {pc:x}")
            pc = next_pc
        raise ValueError("Serializer instruction budget exceeded")


def check_chains(sample, ui, sdk):
    entry = (REPO + "b;", sample["entry"])
    # Both UI paths consume the raw DTO protocol index directly; the main path
    # obtains its DTO from the top-level mNoiseReductionModeList, not children.
    for owner, index_reg, send_args in ((UI[:-1] + "$a;", 4, [5, 4, 3]),
                                       (sample["child"], 1, [2, 1, 0])):
        getter = ui.one_call(owner, "c", MODE, "getProtocolIndex")
        sender = ui.one_call(owner, "c", *entry)
        require(getter[0] < sender[0] and sender[2] == send_args, "Index-to-repository register flow differs")
        tail = [(op, w) for address, op, w in ui.body(owner, "c") if getter[0] < address < sender[0]]
        require(tail[0] == (0x0A, (index_reg << 8 | 0x0A,)), "Protocol index result register differs")
        require(len(tail) == 3 and tail[1][0] == 0x71 and tail[2][0] == 0x0C,
                "Protocol index is transformed before submission")
        require(not any(call[1][:2] == (MODE, "getChildrenMode") for call in ui.calls(owner, "c")),
                "Unexpected child substitution at UI submission")
        print(f"  {owner}->c: protocolIndex @{getter[0]:x} -> {entry[1]} @{sender[0]:x}")
    main_body = ui.body(UI[:-1] + "$a;", "c")
    require(any(op == 0x6E and ui.methods[w[1]][:2] == ("Ljava/util/ArrayList;", "get")
                for _, op, w in main_body), "Main mode list access missing")
    require(any(op == 0x54 and ui.fields[w[1]][:2] == (UI, "mNoiseReductionModeList")
                for _, op, w in ui.body(UI, "s")), "Main list accessor does not return the top-level mode list")
    repository = sample["repository"]
    entry_body = ui.body(repository, sample["entry"])
    require(entry_body[0][1:] == (0x12, (0x1012,)), "Repository default type is not 1")
    require(ui.one_call(repository, sample["entry"], repository, sample["typed"])[2] == [1, 2, 0, 3],
            "Repository index/type argument order differs")
    constructor = ui.one_call(repository, sample["typed"], sample["supplier"], "<init>")
    require(constructor[2] == [0, 4, 3, 5, 1], "Supplier type/index argument order differs")
    supplier_constructor = ui.body(sample["supplier"], "<init>", "(IILjava/lang/String;I)V")
    for register, field in ((1, "b"), (2, "c")):
        require(any(op == 0x59 and w[0] >> 8 & 15 == register and w[0] >> 12 == 0
                    and ui.fields[w[1]] == (sample["supplier"], field, "I")
                    for _, op, w in supplier_constructor), "Supplier constructor changes type/index fields")
    typed_body = ui.body(repository, sample["typed"])
    require(any(op == 0x12 and w == (sample["supplier_branch"] << 12 | 0x112,)
                for _, op, w in typed_body), "Supplier branch selector differs")
    require(any(op == 0x13 and w[1] == 0x0404 for _, op, w in typed_body), "Repository command is not 0404")
    supplier_calls = ui.calls(sample["supplier"], "get")
    bit = [call for call in supplier_calls if call[1][:2] == (INFO, "setCurrentNoiseReductionModeValue")]
    require(len(bit) == 1 and bit[0][0] == sample["bit_set"] and bit[0][2] == sample["bit_args"],
            "Supplier does not set the raw index bit")
    require(ui.one_call(sample["supplier"], "get", INFO, "setType")[2] == sample["type_args"],
            "Supplier type register differs")
    supplier_body = ui.body(sample["supplier"], "get")
    for register, field in ((sample["type_args"][1], "b"), (sample["bit_args"][1], "c")):
        require(any(op == 0x52 and w[0] >> 8 & 15 == register
                    and ui.fields[w[1]] == (sample["supplier"], field, "I")
                    for address, op, w in supplier_body if sample["bit_set"] - 40 <= address < sample["bit_set"]),
                "Supplier does not read the original type/index fields")
    before_bit = [(op, w) for address, op, w in supplier_body if address == sample["bit_set"] - 2]
    require(before_bit == [(0x12, (sample["true_constant"],))], "Supplier does not enable its single index bit")
    core_body = sdk.body(CORE, "I0")
    core_args = sample["core_args"]
    require(any(op == 0x13 and w == (core_args[2] << 8 | 0x13, 0x0404)
                for _, op, w in core_body), "Core command differs")
    data_call = sdk.one_call(CORE, "I0", INFO, "getData")
    packet_call = [call for call in sdk.calls(CORE, "I0")
                   if call[1][1] == "a" and "I[B)" in call[1][2]]
    require(len(packet_call) == 1 and packet_call[0][2] == core_args, "Core payload forwarding differs")
    between = [(op, w) for address, op, w in core_body if data_call[0] < address < packet_call[0][0]]
    require(between[0] == (0x0C, (core_args[3] << 8 | 0x0C,)) and len(between) == 2,
            "Core mutates the serialized payload")
    print(f"  {repository}->{sample['entry']}: type=1; {sample['typed']}: command=0404")
    print(f"  {CORE}->I0: getData @{data_call[0]:x} -> frame @{packet_call[0][0]:x}")


def check_payloads(sdk):
    # The actual serializer is evaluated for all X4 main and child indexes.
    expected = {0: "010101", 1: "010102", 2: "010104", 11: "01010008",
                3: "010108", 4: "010110", 5: "010120", 6: "010140",
                7: "010180", 8: "01010001", 12: "01010010",
                13: "01010020", 14: "01010040"}
    for index, encoded in expected.items():
        state = {}
        sdk.run_serializer(INFO, "<init>", [state])
        sdk.run_serializer(INFO, "setType", [state, 1])
        sdk.run_serializer(INFO, "setCurrentNoiseReductionModeValue", [state, index, 1])
        require(sum(state["mOpenNoiseReductionMode"]) == 1, "Setter enabled more than one bit")
        actual = bytes(sdk.run_serializer(INFO, "getData", [state]))
        require(actual.hex() == encoded, f"DEX payload differs for bit {index}: {actual.hex()}")
        print(f"  bit {index:2d}: {actual.hex(' ').upper()}")
    for value in (0, 1, 2, 5, 6):
        require(bool(sdk.run_serializer(INFO, "isModeBitmaskType", [value])) == (value in (1, 5)),
                "Bitmask type predicate differs")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--disassembly", action="store_true", help="Print selected full DEX instruction bodies")
    args = parser.parse_args()
    for sample in SAMPLES:
        path = ROOT / sample["apk"]
        with path.open("rb") as file:
            digest = hashlib.file_digest(file, "sha256").hexdigest()
        require(digest == sample["sha256"], f"Pinned APK hash differs: {path}")
        with zipfile.ZipFile(path) as archive:
            ui_data, sdk_data = archive.read(sample["ui_dex"]), archive.read(sample["sdk_dex"])
        ui, sdk = DexImage(ui_data), DexImage(sdk_data)
        print(f"\n{sample['apk']} SHA256={digest}")
        print(f"  UI={sample['ui_dex']} SDK={sample['sdk_dex']} SDK_SHA256={hashlib.sha256(sdk_data).hexdigest()}")
        check_chains(sample, ui, sdk)
        check_payloads(sdk)
        if args.disassembly:
            audit(path, sample["ui_dex"], {(UI[:-1] + "$a;", "c"), (sample["child"], "c"),
                  (sample["repository"], sample["entry"]), (sample["repository"], sample["typed"]),
                  (sample["supplier"], "get"), (sample["supplier"], "<init>")})
            audit(path, sample["sdk_dex"], {(INFO, "getData"), (INFO, "isModeBitmaskType"),
                  (INFO, "<init>"), (INFO, "setType"), (INFO, "setCurrentNoiseReductionModeValue"),
                  (INFO, "growArray"), (CORE, "I0")})
    print("\nPASS: two pinned APKs; main/child call chains, type=1, command=0404, unmodified payload forwarding;")
    print("26 payloads evaluated from DEX and 10 bitmask predicate cases checked. No device/audio verification.")


if __name__ == "__main__":
    main()
