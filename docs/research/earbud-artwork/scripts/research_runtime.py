"""Use already installed or locally cached research dependencies; never install."""
from __future__ import annotations

import importlib
import os
from pathlib import Path
import sys

sys.dont_write_bytecode = True

DOCUMENTATION_ROOT = Path(__file__).resolve().parents[2]
REPOSITORY_ROOT = DOCUMENTATION_ROOT.parents[1]
RESEARCH_ROOT = REPOSITORY_ROOT / "research"
SCRIPT_TOPICS = {"earbud-artwork", "online-artwork", "online-whitelist"}
REPORT_TOPICS = SCRIPT_TOPICS | {"3d-validation"}


def research_path(value: str | Path, base: Path | None = None) -> Path:
    """Resolve migrated files without rewriting paths in historical evidence."""
    candidate = Path(str(value).replace("\\", "/"))
    if not candidate.is_absolute():
        if candidate.parts[:2] == ("docs", "research") or candidate.parts[:1] == ("research",):
            candidate = REPOSITORY_ROOT / candidate
        else:
            candidate = (base or Path.cwd()) / candidate
    candidate = candidate.resolve()
    was_documentation = candidate.is_relative_to(DOCUMENTATION_ROOT)
    if was_documentation:
        relative = candidate.relative_to(DOCUMENTATION_ROOT)
    elif candidate.is_relative_to(RESEARCH_ROOT):
        relative = candidate.relative_to(RESEARCH_ROOT)
    else:
        return candidate
    # Only this project's reproduction code and reports remain in docs;
    # third-party scripts and nested Markdown evidence move with their data.
    topic = relative.parts[0] if relative.parts else None
    is_code = topic in SCRIPT_TOPICS and relative.parts[1:2] == ("scripts",)
    is_report = relative.suffix.lower() == ".md" and (
        (was_documentation and len(relative.parts) == 1)
        or (topic in REPORT_TOPICS and len(relative.parts) == 2)
    )
    if is_code or is_report or topic == "oppopods-comparison-2026-10-08":
        return (DOCUMENTATION_ROOT / relative).resolve()
    if relative.parts[:2] == ("local", "inputs"):
        relative = Path(*relative.parts[2:])
        if not relative.parts:
            return (RESEARCH_ROOT / "apps").resolve()
    elif relative.parts[:2] == ("local", "upstreams"):
        relative = Path("upstreams", *relative.parts[2:])
    elif relative.parts[:1] == ("local",) and relative.name.startswith("migration-"):
        relative = Path("migrations", relative.name)
    if relative.parts[:1] in {("OppoPods1",), ("OppoPods2",), ("SteamTools",)}:
        relative = Path("upstreams") / relative
    elif len(relative.parts) == 1:
        suffix = relative.suffix.lower()
        if suffix == ".apk":
            relative = Path("apps") / relative
        elif suffix in {".msix", ".msixbundle", ".appx", ".appxbundle"}:
            relative = Path("installers") / relative
        elif suffix in {".png", ".jpg", ".jpeg"}:
            relative = Path("screenshots") / relative
    return (RESEARCH_ROOT / relative).resolve()


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
    research = RESEARCH_ROOT
    resolved = research_path(path)
    if resolved != research and research not in resolved.parents:
        raise ValueError("All research output must remain inside the research directory")
    resolved.mkdir(parents=True, exist_ok=True)
    return resolved
