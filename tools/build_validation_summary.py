"""Build a shareable stage 3/4 result from previously recorded receipts; no device access."""
import argparse
import hashlib
import json
from pathlib import Path


def load(root, name, sources):
    path = root / name
    raw = path.read_bytes()
    sources.append({"file": name, "sha256": hashlib.sha256(raw).hexdigest()})
    return json.loads(raw)


def audio_evidence(receipt, rate):
    item = receipt.get("playback", {})
    client = item.get("wasapiClientFormat", {})
    engine = item.get("engineMixFormat", {})
    driver = item.get("driverDuring", {})
    verified = (receipt.get("success") is True and item.get("endpointMatched") is True
                and item.get("started") is True and not item.get("cancelled")
                and client.get("sampleRateHz") == rate and engine.get("sampleRateHz") == rate
                and driver.get("streamState") == 2 and driver.get("reportedTargetSampleRateHz") == rate)
    return {"verified": verified, "clientSampleRateHz": client.get("sampleRateHz"),
            "engineSampleRateHz": engine.get("sampleRateHz"),
            "clientBitsPerSample": client.get("bitsPerSample"),
            "driverDuringSampleRateHz": driver.get("reportedTargetSampleRateHz"),
            "driverDuringBitDepth": driver.get("reportedTargetBitDepth"),
            "driverStreaming": driver.get("streamState") == 2,
            "silentPlaybackMilliseconds": item.get("silentPlaybackMilliseconds")}


def wire_evidence(report, rate):
    configs = [item for item in report.get("acceptedConfigurations", [])
               if item.get("codec", {}).get("sampleRatesHz") == [rate]
               and item.get("codec", {}).get("bitDepths") == [24]
               and item.get("acceptedFrame") is not None and item.get("startAcceptFrame") is not None
               and item.get("mediaPacketCount", 0) > 0]
    verified = (report.get("targetAttribution") == "verifiedAddress" and bool(configs)
                and report.get("hostSetterRequested_vsWireObserved", {}).get("status") == "matchesObservedStream")
    return {"verified": verified, "targetAttribution": report.get("targetAttribution"),
            "sampleRateHz": rate, "bitDepth": 24,
            "configurationAccepted": bool(configs), "startAccepted": bool(configs),
            "mediaPacketCount": sum(item.get("mediaPacketCount", 0) for item in configs),
            "rawCodecHex": configs[-1]["codec"]["rawCodecHex"] if configs else None,
            "captureIssues": report.get("issues", []),
            "decodedPcmMeasured": False, "packetBitrateMeasured": False}


def main(args):
    sources = []
    root = args.artifacts.resolve()
    enable = load(root, "phase3-hires-enable.json", sources)
    before = load(root, "phase34-driver-before.json", sources)["driver"]
    after = load(root, "phase4-driver-after-hires.json", sources)["driver"]
    final_driver = load(root, "phase4-last-driver.json", sources)["driver"]
    final_ear = load(root, "phase34-last-ear-status.json", sources)
    wire192 = load(root, "phase4-192-wire-final.json", sources)
    wire96 = load(root, "phase4-96-wire-continuous.json", sources)
    play192 = load(root, "phase4-last-playback.json", sources)
    play96 = load(root, "phase4-continuous-play-96.json", sources)
    hires = enable["hiRes"]
    final_feature = next(item for item in final_ear["queries"] if item["requestCommand"] == "0x010D")["data"]["hiRes"]
    w192, w96 = wire_evidence(wire192, 192000), wire_evidence(wire96, 96000)
    a192, a96 = audio_evidence(play192, 192000), audio_evidence(play96, 96000)
    available = 96000 in after["sampleRates"] and 192000 in after["sampleRates"]
    closed = (enable.get("success") is True and hires.get("hiResAckAccepted") is True
              and hires.get("finalHiResVerified") is True and hires.get("freshSessionVerified") is True
              and hires["after"].get("spaceMode") == 0 and final_feature is True)
    current = final_driver["reportedTargetSampleRateHz"] == 192000 and final_driver["reportedTargetBitDepth"] == 24
    summary = {
        "schemaVersion": 1, "success": closed and available and current and w192["verified"]
                         and w96["verified"] and a192["verified"] and a96["verified"],
        "stage3": {"closedLoopVerified": closed, "beforeHiRes": hires["before"]["hiRes"],
                   "afterHiRes": hires["after"]["hiRes"], "currentHiRes": final_feature,
                   "beforeSpaceMode": hires["before"]["spaceMode"], "afterSpaceMode": hires["after"]["spaceMode"],
                   "hiResAckAccepted": hires["hiResAckAccepted"], "settingsAttempted": hires["settingsAttempted"],
                   "bluetoothDisconnectObserved": enable["bluetoothDisconnectObserved"],
                   "freshControlSessionVerified": hires["freshSessionVerified"]},
        "stage4": {"beforeDriverRatesHz": before["sampleRates"], "afterDriverRatesHz": after["sampleRates"],
                   "highRatesAvailable": available, "rate96000": {"wire": w96, "audio": a96},
                   "rate192000": {"wire": w192, "audio": a192}},
        "currentState": {"hiRes": final_feature, "driverTargetSampleRateHz": final_driver["reportedTargetSampleRateHz"],
                         "driverBitDepth": final_driver["reportedTargetBitDepth"],
                         "qualityIndex": final_driver["reportedTargetQualityIndex"], "lowLatency": final_driver["lowLatency"]},
        "limits": ["Wire verification concerns negotiated codec configuration and identified media activity.",
                   "Rendered test data was short silence; acoustic output, decoded PCM identity, packet bitrate and lossless proportion were not measured.",
                   "Capture issues are preserved; success requires complete matching configuration, Start ACK and media evidence.",
                   "ARM64 was cross-built only; actual ARM64 Bluetooth and audio operation were not tested."],
        "sources": sources}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(summary, stream, indent=2, ensure_ascii=False)
        stream.write("\n")
    print(json.dumps({"success": summary["success"], "hiRes": final_feature,
                      "wire96Verified": w96["verified"], "wire192Verified": w192["verified"],
                      "currentRateHz": final_driver["reportedTargetSampleRateHz"]}))
    return 0 if summary["success"] else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    raise SystemExit(main(parser.parse_args()))
