"""Offline output-preservation checks; no research inputs, Ghidra, UI, or devices."""

import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


TOOLS = Path(__file__).resolve().parents[3] / "tools/research/air4_pro_ghidra"


def load_tool(name):
    spec = importlib.util.spec_from_file_location(name, TOOLS / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


ANALYZE = load_tool("analyze_exports")
VERIFY = load_tool("verify_0114_wire")
RECONSTRUCT = load_tool("reconstruct_0105_wire")


def invoke(module, raw, output):
    if module is ANALYZE:
        module.analyze(raw, raw.parent / "absent-export", output)
    elif module is VERIFY:
        module.verify(raw, output)
    else:
        module.reconstruct(raw, output, 0)


def fixture(module):
    """Synthetic bytes satisfy only the tested tool's local parsing inputs."""
    if module is ANALYZE:
        return b"spp synthetic test\x00audio synthetic test\x00"
    if module is RECONSTRUCT:
        strings = {
            0x130DC7: "%d,%d,%03d,%d,%d,%s,%d,%d,%s,%d,%d,%03d,%d,%d,%s,%d,%d,%s",
            0x130E01: ",%d,%d,%03d,%d,%d,%03d,%d,%d,%03d",
            0x12A704: "0.1.2.5",
        }
        data = bytearray(0x130E80)
        for offset, value in strings.items():
            data[offset:offset + len(value) + 1] = value.encode("ascii") + b"\0"
        return bytes(data)
    data = bytearray(0x50730)
    for address, (value, _) in VERIFY.EXPECTATIONS.items():
        start = address - VERIFY.EXEC_BASE
        data[start:start + len(bytes.fromhex(value))] = bytes.fromhex(value)
    struct.pack_into("<I", data, 0x0C05E5DC - VERIFY.EXEC_BASE, 0x0C05E973)
    # Encode the dispatch trampoline's Thumb B.W displacement independently.
    relative = (0x0C0606DC - (0x0C05E97E + 4)) & 0x1FFFFFF
    sign, i1, i2 = (relative >> 24) & 1, (relative >> 23) & 1, (relative >> 22) & 1
    first = 0xF000 | (sign << 10) | ((relative >> 12) & 1023)
    second = 0x9000 | ((1 ^ i1 ^ sign) << 13) | ((1 ^ i2 ^ sign) << 11) | ((relative >> 1) & 2047)
    struct.pack_into("<HH", data, 0x0C05E97E - VERIFY.EXEC_BASE, first, second)
    return bytes(data)


class PythonPreservationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def test_cli_rejects_previous_evidence_before_reading_inputs(self):
        for name in ("analyze_exports", "verify_0114_wire", "reconstruct_0105_wire"):
            with self.subTest(tool=name):
                output = self.root / name
                output.mkdir()
                sentinel = output / "previous-evidence.json"
                sentinel.write_bytes(b"preserved evidence\n")
                args = [str(self.root / "missing.raw")]
                if name == "analyze_exports":
                    args.append(str(self.root / "missing-export"))
                result = subprocess.run([sys.executable, str(TOOLS / f"{name}.py"), *args, str(output)],
                                        capture_output=True, text=True, timeout=30)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("FileExistsError", result.stderr)
                self.assertEqual(list(output.iterdir()), [sentinel])
                self.assertEqual(sentinel.read_bytes(), b"preserved evidence\n")

    def test_existing_empty_directories_and_files_are_rejected(self):
        for module in (ANALYZE, VERIFY, RECONSTRUCT):
            for directory in (True, False):
                with self.subTest(tool=module.__name__, directory=directory):
                    output = self.root / f"{module.__name__}-{directory}"
                    output.mkdir() if directory else output.write_bytes(b"keep")
                    with self.assertRaises(FileExistsError):
                        invoke(module, self.root / "missing.raw", output)
                    self.assertEqual(list(output.iterdir()), []) if directory else self.assertEqual(output.read_bytes(), b"keep")

    def test_existing_links_are_rejected(self):
        target = self.root / "empty-target"
        target.mkdir()
        for module in (ANALYZE, VERIFY, RECONSTRUCT):
            output = self.root / module.__name__
            try:
                output.symlink_to(target, target_is_directory=True)
            except (OSError, NotImplementedError):
                self.skipTest("Symbolic links are unavailable in this environment")
            with self.assertRaises(FileExistsError):
                invoke(module, self.root / "missing.raw", output)
            self.assertTrue(output.is_symlink())
            self.assertEqual(list(target.iterdir()), [])

    def test_new_outputs_work_and_repeated_runs_preserve_every_file(self):
        for module, expected_count in ((ANALYZE, 6), (VERIFY, 4), (RECONSTRUCT, 7)):
            with self.subTest(tool=module.__name__):
                data = fixture(module)
                raw = self.root / f"{module.__name__}.raw"
                raw.write_bytes(data)
                output = self.root / module.__name__
                digest = hashlib.sha256(data).hexdigest()
                with mock.patch.object(module, "RAW_SHA256", digest, create=True), contextlib.redirect_stdout(io.StringIO()):
                    invoke(module, raw, output)
                    snapshot = {p.name: p.read_bytes() for p in output.iterdir()}
                    self.assertEqual(len(snapshot), expected_count)
                    with self.assertRaises(FileExistsError):
                        invoke(module, raw, output)
                self.assertEqual(snapshot, {p.name: p.read_bytes() for p in output.iterdir()})
                if module is VERIFY:
                    self.assertEqual(snapshot["0114-native-00-sequence-5a-reconstructed.bin"], bytes.fromhex("aa0a000014815a0200000100"))
                if module is RECONSTRUCT:
                    report = json.loads(snapshot["0105-static-reconstruction.json"])
                    self.assertEqual(report["examples"][0]["tuple_count"], 9)

    def test_file_collision_after_directory_reservation_is_not_overwritten(self):
        names = {ANALYZE: "raw-strings.jsonl", VERIFY: "0114-native-00-sequence-5a-reconstructed.bin",
                 RECONSTRUCT: "0105-local-1-125-peer-2-000-box-000.ascii.txt"}
        real_mkdir = Path.mkdir
        for module, first_filename in names.items():
            with self.subTest(tool=module.__name__):
                data = fixture(module)
                raw = self.root / f"{module.__name__}.raw"
                raw.write_bytes(data)
                output = self.root / module.__name__
                def reserve_and_inject(path, *args, **kwargs):
                    real_mkdir(path, *args, **kwargs)
                    if path == output:
                        (output / first_filename).write_bytes(b"concurrent evidence")
                with mock.patch.object(module, "RAW_SHA256", hashlib.sha256(data).hexdigest(), create=True), \
                     mock.patch.object(Path, "mkdir", reserve_and_inject):
                    with self.assertRaises(FileExistsError):
                        invoke(module, raw, output)
                self.assertEqual((output / first_filename).read_bytes(), b"concurrent evidence")
                self.assertEqual(len(list(output.iterdir())), 1)

    def test_json_report_writers_refuse_existing_files(self):
        output = self.root / "report.json"
        output.write_bytes(b"original report")
        for writer in (ANALYZE.write_json, ANALYZE.write_jsonl):
            with self.assertRaises(FileExistsError):
                writer(output, [])
            self.assertEqual(output.read_bytes(), b"original report")


@unittest.skipUnless(shutil.which("javac") and shutil.which("java"), "JDK javac/java are required for Java guard checks")
class JavaPreservationTests(unittest.TestCase):
    def test_actual_export_guard_and_exclusive_writers(self):
        source = (TOOLS / "Air4Export.java").read_text(encoding="utf-8")
        self.assertLess(source.index("OutputFiles.prepareDirectory(output);"), source.index("new DecompInterface()"))
        start = source.index("    static final class OutputFiles {")
        end = source.index("\n    private final Gson", start)
        # Compile the production JDK-only helper, removing only its nested-class modifier.
        helper = source[start:end].replace("static final class OutputFiles", "final class OutputFiles", 1)
        imports = "\n".join(re.findall(r"^import java\.[^;]+;", source, re.M))
        harness = r'''
import java.nio.file.*;
import java.io.*;
public class PreservationHarness {
    interface Action { void run() throws Exception; }
    static void rejected(Action action) throws Exception {
        try { action.run(); } catch (FileAlreadyExistsException expected) { return; }
        throw new AssertionError("Expected exclusive-creation rejection");
    }
    static void keep(Path path, String expected) throws Exception {
        if (!Files.readString(path).equals(expected)) throw new AssertionError("Evidence changed: " + path);
    }
    public static void main(String[] args) throws Exception {
        Path root = Path.of(args[0]);
        Path old = Files.createDirectory(root.resolve("old"));
        Path sentinel = old.resolve("sentinel.json"); Files.writeString(sentinel, "keep");
        rejected(() -> OutputFiles.prepareDirectory(old)); keep(sentinel, "keep");
        if (Files.exists(old.resolve("functions"))) throw new AssertionError("Rejected directory was mutated");
        Path nested = Files.createDirectory(root.resolve("nested")); Files.createDirectory(nested.resolve("child"));
        rejected(() -> OutputFiles.prepareDirectory(nested));
        Path file = root.resolve("file"); Files.writeString(file, "file evidence");
        rejected(() -> OutputFiles.prepareDirectory(file)); keep(file, "file evidence");
        Path empty = Files.createDirectory(root.resolve("empty")); OutputFiles.prepareDirectory(empty);
        rejected(() -> OutputFiles.prepareDirectory(empty));
        Path fresh = root.resolve("new-parent/fresh"); OutputFiles.prepareDirectory(fresh);
        if (!Files.isDirectory(fresh.resolve("functions"))) throw new AssertionError("New export directory failed");
        Path report = fresh.resolve("summary.json"); OutputFiles.writeString(report, "first");
        rejected(() -> OutputFiles.writeString(report, "overwritten")); keep(report, "first");
        Path index = fresh.resolve("functions.jsonl");
        try (BufferedWriter writer = OutputFiles.writer(index)) { writer.write("index evidence"); }
        rejected(() -> { try (BufferedWriter writer = OutputFiles.writer(index)) { writer.write("overwritten"); } });
        keep(index, "index evidence");
        Path link = root.resolve("link");
        try { Files.createSymbolicLink(link, root.resolve("missing-target")); }
        catch (IOException | UnsupportedOperationException unavailable) { System.out.println("Java checks passed; links unavailable"); return; }
        rejected(() -> OutputFiles.prepareDirectory(link));
        rejected(() -> OutputFiles.writeString(link, "overwritten"));
        rejected(() -> { try (BufferedWriter writer = OutputFiles.writer(link)) { writer.write("overwritten"); } });
        if (Files.exists(root.resolve("missing-target"))) throw new AssertionError("Link target was created");
        Path linkDirectory = Files.createDirectory(root.resolve("only-link"));
        Files.createSymbolicLink(linkDirectory.resolve("nested-link"), root.resolve("missing-target"));
        rejected(() -> OutputFiles.prepareDirectory(linkDirectory));
        Path emptyTarget = Files.createDirectory(root.resolve("empty-target"));
        Path directoryLink = root.resolve("directory-link"); Files.createSymbolicLink(directoryLink, emptyTarget);
        rejected(() -> OutputFiles.prepareDirectory(directoryLink));
        if (Files.exists(emptyTarget.resolve("functions"))) throw new AssertionError("Link target was mutated");
        System.out.println("Java output-preservation checks passed");
    }
}
'''
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "OutputFiles.java").write_text(imports + "\n" + helper, encoding="utf-8")
            (root / "PreservationHarness.java").write_text(harness, encoding="utf-8")
            compile_result = subprocess.run([shutil.which("javac"), "OutputFiles.java", "PreservationHarness.java"],
                                            cwd=root, capture_output=True, text=True, timeout=30)
            self.assertEqual(compile_result.returncode, 0, compile_result.stderr)
            run_result = subprocess.run([shutil.which("java"), "-cp", str(root), "PreservationHarness", str(root)],
                                        cwd=root, capture_output=True, text=True, timeout=30)
            self.assertEqual(run_result.returncode, 0, run_result.stderr)


if __name__ == "__main__":
    unittest.main()
