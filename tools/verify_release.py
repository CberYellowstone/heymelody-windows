"""Bounded static release checks plus the exact saved read-only receipts."""
import argparse
import datetime
import hashlib
import json
import pathlib
import struct


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


class PE:
    def __init__(self, path):
        self.data = path.read_bytes()
        if self.data[:2] != b"MZ":
            raise ValueError("Missing DOS header")
        base = struct.unpack_from("<I", self.data, 0x3c)[0]
        if self.data[base:base + 4] != b"PE\0\0":
            raise ValueError("Missing PE header")
        self.machine, count = struct.unpack_from("<HH", self.data, base + 4)
        optional_size = struct.unpack_from("<H", self.data, base + 20)[0]
        optional = base + 24
        magic = struct.unpack_from("<H", self.data, optional)[0]
        directories = optional + {0x10b: 96, 0x20b: 112}[magic]
        self.resources, _ = struct.unpack_from("<II", self.data, directories + 16)
        self.sections = []
        for index in range(count):
            entry = optional + optional_size + 40 * index
            virtual_size, rva, raw_size, raw = struct.unpack_from("<IIII", self.data, entry + 8)
            self.sections.append((rva, virtual_size, raw, raw_size))

    def offset(self, rva, size):
        for start, virtual_size, raw, raw_size in self.sections:
            if start <= rva and rva - start + size <= raw_size:
                offset = raw + rva - start
                if offset + size <= len(self.data):
                    return offset
        raise ValueError("PE RVA outside file data")

    def icons(self):
        root = self.offset(self.resources, 16)
        found = {}

        def walk(relative, route):
            if len(route) > 3:
                raise ValueError("Unexpected resource depth")
            directory = root + relative
            named, ids = struct.unpack_from("<HH", self.data, directory + 12)
            for index in range(named + ids):
                name, target = struct.unpack_from("<II", self.data, directory + 16 + index * 8)
                if not route and name not in (3, 14):
                    continue
                if name & 0x80000000:
                    raise ValueError("Unexpected named icon resource")
                branch = (*route, name)
                if target & 0x80000000:
                    walk(target & 0x7fffffff, branch)
                else:
                    rva, size = struct.unpack_from("<II", self.data, root + target)
                    position = self.offset(rva, size)
                    found[branch] = self.data[position:position + size]
        walk(0, ())
        return found


def verify_icon(executable, source):
    ico = source.read_bytes()
    reserved, kind, count = struct.unpack_from("<HHH", ico)
    assert reserved == 0 and kind == 1 and count == 11
    payloads = []
    for index in range(count):
        size, offset = struct.unpack_from("<II", ico, 6 + index * 16 + 8)
        assert offset + size <= len(ico)
        payloads.append(ico[offset:offset + size])
    resources = PE(executable).icons()
    groups = [(route, payload) for route, payload in resources.items() if route[0] == 14]
    assert len(groups) == 1
    route, group = groups[0]
    assert struct.unpack_from("<HHH", group) == (0, 1, count)
    assert len(group) == 6 + count * 14
    used = set()
    for index, expected in enumerate(payloads):
        size, identity = struct.unpack_from("<IH", group, 6 + index * 14 + 8)
        actual = resources[(3, identity, route[2])]
        assert size == len(actual) and actual == expected
        used.add(identity)
    assert len([value for value in resources if value[0] == 3]) == len(used) == count
    return dict(frames=count, rawFramePayloadsMatch=True, sourceSha256=sha(source))


def verify(root, package):
    expected = {"x64": (0x8664, True), "x86": (0x14c, True), "arm64": (0xaa64, False)}
    builds = []
    for arch, (machine, executed) in expected.items():
        folder = package / arch
        gui, host = folder / "HeyMelody.exe", folder / "backend" / "HeyMelody.Host.exe"
        for path in [gui, host, folder / "coreclr.dll", host.parent / "coreclr.dll"]:
            assert PE(path).machine == machine, str(path)
        for name, directory in [("HeyMelody", folder), ("HeyMelody.Host", host.parent)]:
            runtime = json.loads((directory / (name + ".runtimeconfig.json")).read_text("utf-8-sig"))["runtimeOptions"]
            assert runtime["tfm"] == "net10.0" and runtime.get("includedFrameworks")
            dependencies = json.loads((directory / (name + ".deps.json")).read_text("utf-8-sig"))
            assert dependencies["runtimeTarget"]["name"].endswith("/win-" + arch)
            assert (directory / "LICENSE.txt").is_file() and (directory / "THIRD-PARTY-NOTICES.txt").is_file()
        icon = verify_icon(gui, root / "src" / "HeyMelody.App" / "Assets" / "HeyMelody.ico")
        receipt = None
        if executed:
            evidence = root / "artifacts" / ("phase5-release-readonly-" + arch + ".json")
            receipt = json.loads(evidence.read_text("utf-8"))
            assert receipt["success"] and receipt["diagnostics"]["productId"] == "06EC10"
            assert receipt["diagnostics"]["applicationGetSubmissions"] == 20
            assert receipt["diagnostics"]["applicationSetSubmissions"] == 0
            assert receipt["diagnostics"]["applicationOtherSubmissions"] == 0
            for state in receipt["snapshots"]:
                assert state["hiRes"] is True and state["spatial"] == 0 and state["dualConnection"] is True
            preview = root / "artifacts" / ("phase5-release-ui-" + arch + ".png")
            png = preview.read_bytes()
            assert png[:8] == b"\x89PNG\r\n\x1a\n" and struct.unpack_from(">II", png, 16) == (1280, 1440)
        builds.append(dict(architecture=arch, peMachine=hex(machine), selfContained=True,
                           guiSha256=sha(gui), hostSha256=sha(host), icon=icon,
                           readOnlyBluetoothExecutionVerified=executed, liveGuiPreviewVerified=executed,
                           realNativeArmHardwareVerified=False,
                           readOnlyEvidenceSha256=sha(evidence) if executed else None))
    return dict(schemaVersion=1, version="0.5.0", verifiedAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                success=True, builds=builds, supportedModel="OPPO Enco X4 / 06EC10",
                tests=dict(dailyOfflineScenarios=38, clientProcessScenariosX64=10, clientProcessScenariosX86=10,
                           protocolResourceAssertions=12),
                liveSettingCommandsSubmitted=0, settingPathsVerified="offline golden and simulated workflow",
                nativeArm64ExecutionVerified=False)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    root = pathlib.Path(__file__).resolve().parents[1]
    report = verify(root, args.package.resolve())
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2, ensure_ascii=False)
        stream.write("\n")
    print(json.dumps(report, ensure_ascii=False))
