"""Static Avalonia release checks; hardware evidence is explicit and hash-bound."""
import argparse
import datetime
import hashlib
import json
import pathlib
from pathlib import Path
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


def checked_file(directory, relative):
    path = (directory / relative).resolve()
    assert path.is_relative_to(directory.resolve()) and path.is_file(), str(path)
    return path


def verify_resource_bundle(directory):
    bundle = directory / "ResourceBundles"
    index = checked_file(bundle, "artwork.json")
    rows = json.loads(index.read_text("utf-8"))
    provenance = json.loads(checked_file(bundle, "provenance.json").read_text("utf-8"))
    reference = Path(__file__).resolve().parents[1] / "src/HeyMelody.Resources/Bundles"
    manifests = {"artwork.json", "provenance.json", "supplemental.json"}
    assert {p.name for p in bundle.iterdir() if p.is_file()} == manifests, "Unexpected offline manifest"
    for name in manifests:
        assert sha(checked_file(bundle, name)) == sha(checked_file(reference, name)), "Offline manifest differs from source"
    assert len(provenance) == 1 and provenance[0]["package"] == "com.heytap.headset"
    assert "source" not in provenance[0] and rows, "Invalid HeyMelody provenance"
    reachable = set()
    supplemental = json.loads((bundle / "supplemental.json").read_text("utf-8"))
    assert supplemental and all(row["package"] == "com.heytap.headset" for row in supplemental)
    for row in rows + supplemental:
        assert "source" not in row, "Artwork source selection is no longer supported"
        path = checked_file(bundle, row["path"])
        assert path.stat().st_size == row["length"] and sha(path) == row["sha256"]
        reachable.add(row["path"])
        if row.get("originalPath"):
            assert sha(checked_file(bundle, row["originalPath"])) == row["originalSha256"]
            reachable.add(row["originalPath"])
        if row.get("kind") == "lottie":
            for asset in json.loads(path.read_text("utf-8")).get("assets", []):
                relative = str(asset.get("u", "")) + str(asset.get("p", ""))
                if not relative or relative.startswith("data:"):
                    continue
                assert ":" not in relative and ".." not in Path(relative).parts, "Unsafe animation dependency"
                dependency = checked_file(path.parent, relative)
                name = dependency.relative_to(bundle).as_posix()
                assert sha(dependency) == sha(checked_file(reference, name)), "Animation dependency differs from source"
                reachable.add(name)
    actual = {p.relative_to(bundle).as_posix() for p in (bundle / "data").rglob("*") if p.is_file()}
    assert actual == reachable, "Offline artwork contains missing or unreferenced files"
    checked_file(directory, "HeyMelody.Resources.dll")
    checked_file(directory, "licenses/upstream/Extracted-Earphone-Resources-NOTICE.txt")
    return dict(artworkEntries=len(rows), catalogCount=1, dataFiles=len(reachable), indexSha256=sha(index), allIndexedBytesVerified=True)


def verify(root, package, evidence=None, architectures=("x64", "x86", "arm64")):
    manifest = json.loads((package / "manifest.json").read_text("utf-8-sig"))
    assert manifest["schemaVersion"] == 2 and manifest["version"]
    resource_release = tuple(int(part) for part in manifest["version"].split(".")) >= (0, 8, 0)
    declared = {build["architecture"]: build for build in manifest["builds"]}
    machines = {"x64": 0x8664, "x86": 0x14c, "arm64": 0xaa64}
    requested = set(architectures)
    assert requested and requested.issubset(machines), "Invalid requested architectures"
    expected = {arch: machine for arch, machine in machines.items() if arch in requested}
    assert set(declared) == set(expected)
    receipts = {}
    if evidence is not None:
        assert evidence["version"] == manifest["version"] and evidence["success"]
        assert evidence["schemaVersion"] == 2, "Old release evidence cannot verify this build"
        receipts = {build["architecture"]: build for build in evidence["builds"]}
        assert set(receipts).issubset(expected)
    builds = []
    for arch, machine in expected.items():
        folder = package / arch
        gui, host = folder / "HeyMelody.exe", folder / "backend" / "HeyMelody.Host.exe"
        manager = checked_file(folder, "HeyMelody.DeviceManager.exe")
        gui_assembly = checked_file(folder, "HeyMelody.dll")
        components = [checked_file(folder, name + ".exe") for name in ("HeyMelody.FitTest", "HeyMelody.HearingSetup")] if resource_release else []
        native = [directory / filename for directory in (folder, host.parent)
                  for filename in ("libSkiaSharp.dll", "libHarfBuzzSharp.dll", "av_libglesv2.dll")]
        for path in [gui, manager, host, *components, folder / "coreclr.dll", host.parent / "coreclr.dll", *native]:
            assert PE(path).machine == machine, str(path)
        assert declared[arch]["rid"] == "win-" + arch and declared[arch]["selfContained"] is True
        assert declared[arch]["guiSha256"] == sha(gui) and declared[arch]["hostSha256"] == sha(host)
        assert declared[arch]["deviceManagerSha256"] == sha(manager)
        assert declared[arch]["guiFramework"] == "net10.0" and declared[arch]["hostFramework"] == "net10.0-windows10.0.19041.0"
        assert declared[arch]["deviceManagerFramework"] == "net10.0"
        if resource_release:
            for role, executable in zip(("fitTest", "hearingSetup"), components):
                assert declared[arch][role] == arch + "/" + executable.name
                assert declared[arch][role + "Sha256"] == sha(executable)
                assert declared[arch][role + "Framework"] == "net10.0"
        gui_dependencies = None
        entrypoints = [("HeyMelody", folder), ("HeyMelody.DeviceManager", folder), ("HeyMelody.Host", host.parent)]
        entrypoints += [(path.stem, folder) for path in components]
        runtime_versions = set()
        for name, directory in entrypoints:
            runtime = json.loads((directory / (name + ".runtimeconfig.json")).read_text("utf-8-sig"))["runtimeOptions"]
            assert runtime["tfm"] == "net10.0" and runtime.get("includedFrameworks")
            assert {entry["name"] for entry in runtime["includedFrameworks"]} == {"Microsoft.NETCore.App"}
            runtime_versions.update(entry["version"] for entry in runtime["includedFrameworks"])
            dependencies = json.loads((directory / (name + ".deps.json")).read_text("utf-8-sig"))
            assert dependencies["runtimeTarget"]["name"].endswith("/win-" + arch)
            assert (directory / "LICENSE.txt").is_file() and (directory / "THIRD-PARTY-NOTICES.txt").is_file()
            libraries = dependencies["libraries"]
            assert any(key.startswith("Avalonia/") for key in libraries)
            assert any(key.startswith("FluentAvaloniaUI/") for key in libraries)
            if name == "HeyMelody":
                gui_dependencies = dependencies
            elif directory == folder:
                for name, item in libraries.items():
                    if item["type"] == "package":
                        assert name in gui_dependencies["libraries"] and item["sha512"] == gui_dependencies["libraries"][name]["sha512"]
        assert len(runtime_versions) == 1, "Entrypoints do not share the exact runtime"
        renderer_packages = [key.split("/", 1) for key in gui_dependencies["libraries"]
                             if key.split("/", 1)[0].casefold() == "ava3dcontrol"]
        assert len(renderer_packages) <= 1, "Ambiguous Ava3DControl dependency versions"
        renderer = None
        if renderer_packages:
            renderer_path = checked_file(folder, "Ava3D.dll")
            renderer = dict(package=renderer_packages[0][0], declaredVersion=renderer_packages[0][1],
                            assembly=renderer_path.name, sha256=sha(renderer_path))
        resources = None
        if resource_release:
            assert any(key.startswith("SkiaSharp.Skottie/") for key in gui_dependencies["libraries"])
            resources = dict(gui=verify_resource_bundle(folder), host=verify_resource_bundle(host.parent))
            assert declared[arch]["resourceIndexSha256"] == resources["gui"]["indexSha256"]
        assert not (folder / "PresentationFramework.dll").exists()
        assert not (folder / "LICENSE-WINDOWSDESKTOP.txt").exists()
        for directory in (folder, host.parent):
            notices = json.loads((directory / "PACKAGE-NOTICES.json").read_text("utf-8-sig"))
            assert notices["schemaVersion"] == 1 and notices["packages"]
            assert {"Avalonia", "FluentAvaloniaUI", "SkiaSharp", "HarfBuzzSharp"}.issubset(
                {entry["package"] for entry in notices["packages"]})
            for entry in notices["packages"]:
                assert entry["version"] and checked_file(directory, entry["metadata"])
                for notice in entry["files"]:
                    assert sha(checked_file(directory, notice["path"])) == notice["sha256"]
            assert notices["supplementalNotices"]
            for notice in notices["supplementalNotices"]:
                assert sha(checked_file(directory, notice["path"])) == notice["sha256"]
        icon = verify_icon(gui, root / "src" / "HeyMelody.App" / "Assets" / "HeyMelody.ico")
        receipt = receipts.get(arch, {})
        if receipt:
            assert receipt["guiSha256"] == sha(gui) and receipt["hostSha256"] == sha(host)
            if resource_release:
                assert receipt["deviceManagerSha256"] == sha(manager)
                for role, executable in zip(("fitTest", "hearingSetup"), components):
                    assert receipt[role + "Sha256"] == sha(executable)
        executed = receipt.get("readOnlyBluetoothExecutionVerified", False)
        if executed:
            diagnostics = receipt["diagnostics"]
            assert diagnostics["productId"] == "06EC10" and diagnostics["applicationGetSubmissions"] > 0
            assert diagnostics["applicationSetSubmissions"] == 0 and diagnostics["applicationOtherSubmissions"] == 0
        builds.append(dict(architecture=arch, peMachine=hex(machine), selfContained=True,
                           guiSha256=sha(gui), guiAssemblySha256=sha(gui_assembly),
                           deviceManagerSha256=sha(manager), hostSha256=sha(host), icon=icon,
                           modelRenderer=renderer,
                           calibrationExecutables=[dict(name=path.name, sha256=sha(path)) for path in components],
                           resourceBundles=resources,
                           nativeRenderingAssets=[dict(name=path.name, sha256=sha(path)) for path in native],
                           packageNoticesVerified=True, readOnlyBluetoothExecutionVerified=executed,
                           liveGuiPreviewVerified=receipt.get("liveGuiPreviewVerified", False),
                           realNativeArmHardwareVerified=arch == "arm64" and receipt.get("nativeHardwareVerified", False)))
    return dict(schemaVersion=2, version=manifest["version"], verifiedAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                success=True, builds=builds, supportedModel="capability-gated source catalogs" if resource_release else "OPPO Enco X4 / 06EC10",
                validationScope="static packaging with explicit execution receipts" if evidence else "static packaging only; no hardware execution verified",
                nativeArm64ExecutionVerified=any(build["realNativeArmHardwareVerified"] for build in builds))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--evidence", type=pathlib.Path, help="Optional schema-2 receipt for this version and exact GUI/host hashes")
    parser.add_argument("--architectures", nargs="+", choices=("x64", "x86", "arm64"),
                        default=("x64", "x86", "arm64"),
                        help="Architectures the package must contain; defaults to all three")
    args = parser.parse_args()
    root = pathlib.Path(__file__).resolve().parents[1]
    evidence = json.loads(args.evidence.read_text("utf-8-sig")) if args.evidence else None
    report = verify(root, args.package.resolve(), evidence, args.architectures)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2, ensure_ascii=False)
        stream.write("\n")
    print(json.dumps(report, ensure_ascii=False))
