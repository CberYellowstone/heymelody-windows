"""Private Intel-radio capture: bulk and connection/disconnection events only."""
import argparse
import json
import struct
import subprocess
import threading
from pathlib import Path


def exact(stream, count):
    parts = []
    while count:
        part = stream.read(count)
        if not part:
            return b"".join(parts)
        parts.append(part)
        count -= len(part)
    return b"".join(parts)


def capture(args):
    output = args.output.resolve()
    directory = output.parent
    directory.mkdir(parents=True, exist_ok=True)
    if output.exists() or (directory / "ready.json").exists():
        raise ValueError("Capture output already exists.")
    stats = dict(recordsSeen=0, bulkRecordsWritten=0, connectionEventsWritten=0,
                 discardedRecords=0, truncatedRecords=0, buses=[])
    process = None
    stopped = threading.Event()
    try:
        with (directory / "capture-error.txt").open("xb") as errors:
            process = subprocess.Popen([
                args.usbpcap, "-d", args.hub, "--devices", str(args.device),
                "-s", "65535", "-b", "1048576", "-o", "-"
            ], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors)

            def stop():
                if stopped.is_set():
                    return
                stopped.set()
                try:
                    if process.poll() is None:
                        process.stdin.write(b"q\n")
                        process.stdin.flush()
                        process.stdin.close()
                        try:
                            process.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            process.terminate()
                except (OSError, ValueError):
                    if process.poll() is None:
                        process.terminate()

            timer = threading.Timer(args.seconds, stop)
            timer.start()

            def watch():
                while not stopped.wait(0.2):
                    if (directory / "stop.request").exists():
                        stop()
                        return

            watcher = threading.Thread(target=watch, daemon=True)
            watcher.start()
            try:
                header = exact(process.stdout, 24)
                if len(header) != 24:
                    raise RuntimeError("Capture did not return a PCAP header.")
                little = header[:4] in (b"\xd4\xc3\xb2\xa1", b"\x4d\x3c\xb2\xa1")
                big = header[:4] in (b"\xa1\xb2\xc3\xd4", b"\xa1\xb2\x3c\x4d")
                if not (little or big):
                    raise RuntimeError("Unsupported PCAP byte order.")
                endian = "<" if little else ">"
                if struct.unpack_from(endian + "I", header, 20)[0] != 249:
                    raise RuntimeError("USBPcap link type is required.")
                with output.open("xb") as stream:
                    stream.write(header)
                    stream.flush()
                    (directory / "ready.json").write_text(json.dumps({
                        "ready": True, "filter": "radio bulk plus HCI events 03/05",
                        "usbDevice": args.device
                    }), encoding="utf-8")
                    while True:
                        record = exact(process.stdout, 16)
                        if not record:
                            break
                        if len(record) != 16:
                            stats["truncatedRecords"] += 1
                            break
                        count = struct.unpack_from(endian + "I", record, 8)[0]
                        if count > 2 * 1024 * 1024:
                            raise RuntimeError("Oversized capture record.")
                        packet = exact(process.stdout, count)
                        if len(packet) != count:
                            stats["truncatedRecords"] += 1
                            break
                        stats["recordsSeen"] += 1
                        if len(packet) < 27 or struct.unpack_from("<H", packet, 19)[0] != args.device:
                            stats["discardedRecords"] += 1
                            continue
                        header_size = struct.unpack_from("<H", packet, 0)[0]
                        data_size = struct.unpack_from("<I", packet, 23)[0]
                        if header_size < 27 or header_size > len(packet) or data_size > len(packet) - header_size:
                            stats["truncatedRecords"] += 1
                            continue
                        transfer = packet[22]
                        keep = transfer == 3
                        if transfer == 1 and packet[21] & 0x80:
                            data = packet[header_size:header_size + data_size]
                            keep = (len(data) == 13 and data[:2] == b"\x03\x0b") or (
                                len(data) == 6 and data[:2] == b"\x05\x04")
                        if not keep:
                            stats["discardedRecords"] += 1
                            continue
                        bus = struct.unpack_from("<H", packet, 17)[0]
                        if bus not in stats["buses"]:
                            stats["buses"].append(bus)
                        stream.write(record)
                        stream.write(packet)
                        stream.flush()
                        key = "bulkRecordsWritten" if transfer == 3 else "connectionEventsWritten"
                        stats[key] += 1
                process.wait(timeout=8)
                stats["captureExitCode"] = process.returncode
            finally:
                timer.cancel()
                stop()
    finally:
        if process is not None and process.poll() is None:
            process.kill()
            process.wait(timeout=5)
        stats["finished"] = True
        (directory / "capture-stats.json").write_text(json.dumps(stats, indent=2), encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--seconds", type=int, default=120)
    parser.add_argument("--hub", default=r"\\.\USBPcap2")
    parser.add_argument("--device", type=int, default=4)
    parser.add_argument("--usbpcap", default=r"D:\Installations\Wireshark\extcap\USBPcapCMD.exe")
    options = parser.parse_args()
    if not 12 <= options.seconds <= 180 or not 1 <= options.device <= 127:
        parser.error("A bounded capture and valid USB address are required.")
    capture(options)
