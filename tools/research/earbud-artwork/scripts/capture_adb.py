"""Capture bounded, read-only ADB evidence. No app launch or phone filesystem writes."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess

from research_runtime import checked_output

PACKAGES = ("com.oplus.melody", "com.heytap.headset")
PRIVATE_PATHS = (
    "/data/user_de/0/com.oplus.melody/files/melody-model-download",
    "/data/user/0/com.oplus.melody/files/melody-model-download",
    "/data/user/0/com.heytap.headset/files/melody-model-download",
)
EXPECTED = {
    "com.oplus.melody": "c1149df9e24ed85bf84f7c81c712cee794299d2b95057c6e221fb547f2717059",
    "com.heytap.headset": "eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4",
}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--transport", required=True, type=int, help="An already connected and authorized adb transport ID")
    parser.add_argument("--output", type=Path, default=Path(__file__).resolve().parents[4] / "research/earbud-artwork/evidence")
    args = parser.parse_args()
    output = checked_output(args.output)
    adb = shutil.which("adb")
    if not adb:
        raise RuntimeError("adb must be available; this script never installs or connects a device")
    prefix = [adb, "-t", str(args.transport)]

    def read(*arguments: str, timeout: int = 15) -> dict:
        result = subprocess.run(prefix + list(arguments), capture_output=True, timeout=timeout)
        return {"command": "adb -t <connected-transport> " + " ".join(arguments),
                "exit_code": result.returncode,
                "stdout": result.stdout.decode("utf-8", errors="replace").strip(),
                "stderr": result.stderr.decode("utf-8", errors="replace").strip()}

    evidence = {"schema_version": 1, "captured_at_utc": datetime.now(timezone.utc).isoformat(),
                "read_only": True, "device": {}, "packages": [], "cache_access": [], "logs": {}}
    for prop in ("ro.product.model", "ro.build.version.release", "ro.build.version.sdk"):
        evidence["device"][prop] = read("shell", "getprop", prop)["stdout"]
    evidence["shell_identity"] = read("shell", "id")
    evidence["selinux"] = read("shell", "getenforce")
    for package in PACKAGES:
        paths = read("shell", "pm", "path", package)
        item = {"package": package, "run_as_identity_check": read("shell", "run-as", package, "id")}
        apk_paths = [s.removeprefix("package:") for s in paths["stdout"].splitlines() if s.startswith("package:")]
        item["apk_path_count"] = len(apk_paths)
        if len(apk_paths) == 1:
            result = subprocess.run(prefix + ["exec-out", "cat", apk_paths[0]], capture_output=True, timeout=30)
            if result.returncode == 0:
                digest = hashlib.sha256(result.stdout).hexdigest()
                item.update(apk_bytes=len(result.stdout), apk_sha256=digest,
                            identical_to_research_input=digest == EXPECTED[package])
            else:
                item["apk_read_error"] = result.stderr.decode(errors="replace").strip()
        pid = read("shell", "pidof", package)["stdout"]
        item["running_process_count"] = len(pid.split()) if pid else 0
        if pid:
            logs = read("logcat", "-d", "-v", "brief", "--pid=" + pid.split()[0], timeout=20)
            # Store only counts, not unrelated personal application logs or headers.
            keywords = re.compile(r"https?://|fetch14|moduleId|model3D|modelWebp|detail_model\.vfxms|resourceUrl", re.I)
            lines = logs["stdout"].splitlines()
            evidence["logs"][package] = {"command": logs["command"], "exit_code": logs["exit_code"],
                                         "line_count": len(lines), "resource_candidate_line_count": sum(bool(keywords.search(s)) for s in lines),
                                         "raw_logs_saved": False}
        evidence["packages"].append(item)
    for path in PRIVATE_PATHS:
        evidence["cache_access"].append(read("shell", "ls", "-ld", path))
    shared = "/sdcard/Android/data/com.oplus.melody/cache"
    found = read("shell", "find", shared, "-maxdepth", "4", "-type", "f")
    evidence["shared_cache"] = {"command": found["command"], "exit_code": found["exit_code"],
                                 "file_count": len(found["stdout"].splitlines()) if found["stdout"] else 0,
                                 "stderr": found["stderr"], "filenames_saved": False}
    for package in ("com.heytap.headset",):
        evidence["cache_access"].append(read("shell", "ls", "-ld", "/sdcard/Android/data/" + package))
    system_paths = read("shell", "pm", "path", "com.oplus.wirelesssettings")
    system_item = {"package": "com.oplus.wirelesssettings"}
    paths = [s.removeprefix("package:") for s in system_paths["stdout"].splitlines() if s.startswith("package:")]
    if len(paths) == 1:
        result = subprocess.run(prefix + ["exec-out", "cat", paths[0]], capture_output=True, timeout=30)
        if result.returncode == 0:
            import io
            import zipfile
            archive = zipfile.ZipFile(io.BytesIO(result.stdout))
            system_item.update(apk_bytes=len(result.stdout), apk_sha256=hashlib.sha256(result.stdout).hexdigest(),
                               member_count=len(archive.namelist()),
                               png_count=sum(n.endswith(".png") for n in archive.namelist()),
                               webp_count=sum(n.endswith(".webp") for n in archive.namelist()),
                               apk_saved=False)
    evidence["additional_system_component"] = system_item
    evidence["not_performed"] = ["root", "app launch/force-stop", "provider/service calls", "log clearing", "log configuration", "phone file writes", "APK installation"]
    target = output / "adb-readonly.json"
    target.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"evidence": str(target), "apk_matches": [p.get("identical_to_research_input") for p in evidence["packages"]], "phone_writes": 0}))


if __name__ == "__main__":
    main()
