"""Use already installed or locally cached research dependencies; never install."""
from __future__ import annotations

import importlib
import os
from pathlib import Path
import sys

sys.dont_write_bytecode = True


def bootstrap(*modules: str) -> None:
    missing = []
    for name in modules:
        try:
            importlib.import_module(name)
        except ImportError:
            missing.append(name)
    if not missing:
        return
    cache = Path(os.environ.get("LOCALAPPDATA", "")) / "uv/cache/archive-v0"
    candidates: list[Path] = []
    if cache.is_dir():
        # Existing uv environments contain matching native dependencies together.
        candidates.extend(sorted(cache.glob("*/Lib/site-packages")))
        for name in (*missing, "cffi"):
            candidates.extend(p.parent.parent for p in sorted(cache.glob(f"*/{name}/__init__.py")))
        candidates.extend(p.parent for p in sorted(cache.glob("*/_cffi_backend*.pyd")))
    for path in reversed(list(dict.fromkeys(candidates))):
        if str(path) not in sys.path:
            sys.path.insert(0, str(path))
    for name in missing:
        try:
            importlib.import_module(name)
        except ImportError as exc:
            raise RuntimeError(
                f"Research dependency {name!r} is unavailable. Use a Python environment "
                "with Pillow and cryptography installed; this script does not install packages."
            ) from exc


def checked_output(path: Path) -> Path:
    research = Path(__file__).resolve().parents[2]
    resolved = path.resolve()
    if resolved != research and research not in resolved.parents:
        raise ValueError("All research output must remain inside the research directory")
    resolved.mkdir(parents=True, exist_ok=True)
    return resolved
