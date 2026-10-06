"""Run only enumerate/connect/refresh/diagnostics/disconnect through the shipped host.

The peer address and opaque UI identity never enter the written report. Request
counts describe application submissions, not an independent Bluetooth capture.
"""
import argparse
import datetime
import json
import pathlib
import queue
import subprocess
import threading


def run(host, output):
    command = ["dotnet", str(host)] if host.suffix.lower() == ".dll" else [str(host)]
    process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.DEVNULL, text=True, encoding="utf-8",
                               creationflags=subprocess.CREATE_NO_WINDOW)
    records = queue.Queue()

    def read():
        try:
            while True:
                record = process.stdout.readline(524290)
                records.put(record)
                if not record or len(record) > 524289:
                    break
        except Exception:
            records.put("")

    threading.Thread(target=read, daemon=True).start()
    next_id = 0

    def request(operation, **values):
        nonlocal next_id
        assert operation in {"enumerate", "connect", "refresh", "diagnostics", "disconnect"}
        next_id += 1
        process.stdin.write(json.dumps(dict(schemaVersion=1, id=next_id, operation=operation, **values)) + "\n")
        process.stdin.flush()
        record = records.get(timeout=90)
        if not record.endswith("\n") or len(record) > 524289:
            raise RuntimeError("Incomplete or oversized host response")
        result = json.loads(record)
        if result.get("schemaVersion") != 1 or result.get("id") != next_id or result.get("success") is not True:
            raise RuntimeError("Host operation unavailable")
        return result["data"]

    def safe_snapshot(value):
        return {key: data for key, data in value.items() if key not in {"identity", "equalizerEntries"}}

    try:
        devices = request("enumerate")
        peers = [value for value in devices if value.get("name") == "OPPO Enco X4" and value.get("connected") is True]
        if len(peers) != 1:
            raise RuntimeError("A unique connected OPPO Enco X4 is required")
        before = request("connect", identity=peers[0]["identity"])
        after = request("refresh")
        diagnostics = request("diagnostics")
        report = dict(schemaVersion=1, sampledAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                      operation="daily-readonly", snapshots=[safe_snapshot(before), safe_snapshot(after)],
                      diagnostics=diagnostics,
                      success=after.get("supportedModel") is True and after.get("connected") is True
                      and not after.get("failure") and diagnostics.get("applicationSetSubmissions") == 0)
        request("disconnect")
        output.parent.mkdir(parents=True, exist_ok=True)
        with output.open("x", encoding="utf-8") as stream:
            json.dump(report, stream, indent=2, ensure_ascii=False)
            stream.write("\n")
        print(json.dumps(dict(success=report["success"], output=str(output),
                              snapshots=report["snapshots"], diagnostics=diagnostics), ensure_ascii=False))
        return 0 if report["success"] else 1
    finally:
        if process.poll() is None:
            try:
                process.stdin.close()
                process.wait(timeout=5)
            except Exception:
                process.kill()
                process.wait(timeout=5)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    args = parser.parse_args()
    raise SystemExit(run(args.host.resolve(), args.output.resolve()))
