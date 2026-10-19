#!/usr/bin/env python3
"""Replay preserved research tools offline, without changing original evidence.

The preserved bytes stay unchanged. Only their runtime __file__ root is mapped
back to this checkout so the original relative input paths retain their meaning.
Current acquisition tools never import this archive.
"""
from __future__ import annotations
import importlib.abc
import importlib.machinery
import importlib.util
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
ORIGINALS = ROOT / "research/migrations/drop-coloros-2026-10-18/originals"
TOOLS = ROOT / "tools/research"

def run(relative: str) -> None:
    target = (ORIGINALS / relative).resolve()
    if not target.is_relative_to(ORIGINALS.resolve()) or not target.is_file():
        raise SystemExit("Preserved research tool is unavailable; see the migration archive.")
    if any(arg in {"--online", "--probe", "--known-defaults"} for arg in sys.argv[1:]):
        raise SystemExit("Historical replay is offline only; current acquisition supports HeyMelody.")
    records = json.loads((ORIGINALS.parent / "preservation-manifest.json").read_text(encoding="utf-8"))["originals"]
    fingerprints = {r["path"]: r for r in records}

    def archived(location):
        path = Path(location).resolve()
        if path.is_relative_to(TOOLS.resolve()):
            candidate = ORIGINALS / path.relative_to(ROOT)
            if candidate.is_file(): return candidate
        return path

    class Loader(importlib.machinery.SourceFileLoader):
        def get_code(self, fullname):
            path = Path(self.path).resolve()
            logical = ROOT / path.relative_to(ORIGINALS.resolve())
            raw = path.read_bytes()
            record = fingerprints[path.relative_to(ORIGINALS.resolve()).as_posix()]
            if len(raw) != record["bytes"] or hashlib.sha256(raw).hexdigest() != record["sha256"]:
                raise RuntimeError("Preserved research tool hash mismatch")
            source = raw.decode("utf-8-sig")
            source = source.replace("Path(__file__).resolve()", "Path(" + repr(str(logical)) + ").resolve()")
            return compile(source, str(path), "exec")

    original_spec = importlib.util.spec_from_file_location
    def specification(name, location, *args, **kwargs):
        path = archived(location)
        if path.is_relative_to(ORIGINALS.resolve()) and path.suffix == ".py":
            return original_spec(name, str(path), loader=Loader(name, str(path)))
        return original_spec(name, location, *args, **kwargs)
    importlib.util.spec_from_file_location = specification

    class Finder(importlib.abc.MetaPathFinder):
        def find_spec(self, fullname, path=None, target=None):
            spec = importlib.machinery.PathFinder.find_spec(fullname, path)
            if spec and spec.origin and spec.origin.endswith(".py"):
                location = archived(spec.origin)
                if location.is_relative_to(ORIGINALS.resolve()):
                    return specification(fullname, location)
            return None
    sys.meta_path.insert(0, Finder())
    sys.path.insert(0, str(ROOT / Path(relative).parent))

    created = set()
    def protect(event, args):
        if event == "subprocess.Popen":
            command = args[1]
            scripts = [Path(arg).resolve() for arg in command[1:] if isinstance(arg, str) and arg.endswith(".py")]
            if Path(args[0]).resolve() != Path(sys.executable).resolve() or len(scripts) != 1 or not scripts[0].is_relative_to(TOOLS.resolve()) or any(arg in {"--online", "--probe", "--known-defaults"} for arg in command):
                raise RuntimeError("Historical replay permits only isolated Python research verification")
        if event.startswith("socket.") or event == "os.system":
            raise RuntimeError("Historical replay forbids network and external processes")
        if event == "open" and isinstance(args[0], (str, bytes)):
            mode = args[1]
            flags = args[2]
            writing = (isinstance(mode, str) and any(c in mode for c in "wax+")) or (isinstance(flags, int) and flags & 0x643)
            if writing:
                destination = Path(args[0]).resolve()
                if not destination.is_relative_to((ROOT / "research").resolve()) or (destination.exists() and destination not in created):
                    raise RuntimeError("Historical replay writes only new files under research/")
                created.add(destination)
        if event in {"os.remove", "os.rename"}:
            affected = args[:2] if event == "os.rename" else args[:1]
            for value in affected:
                if isinstance(value, (str, bytes)):
                    path = Path(value).resolve()
                    if path.exists() and path not in created:
                        raise RuntimeError("Historical replay cannot replace or remove existing evidence")
    sys.addaudithook(protect)
    sys.dont_write_bytecode = True
    namespace = {"__name__": "__main__", "__file__": str(target), "__package__": None}
    exec(Loader("__main__", str(target)).get_code("__main__"), namespace)

if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit("Usage: historical_replay.py tools/research/<preserved-tool.py> [offline arguments]")
    selected = sys.argv.pop(1)
    run(selected)
