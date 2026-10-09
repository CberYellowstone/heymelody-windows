# Offline AVDTP evidence

`analyze_avdtp.py` uses only Python standard library and the installed tshark.
It reads a supplied file; it cannot start capture, access a driver, change
settings, restart a device or play audio. Tests generate anonymous synthetic
traffic and also run the installed tshark against a synthetic USBPcap file.

Root command (PowerShell, repository root; `python` must be Python 3; replace
private paths and bus key). If Python is not on `PATH`, use your Python 3
executable path:

```powershell
python '.\tools\audio-evidence\analyze_avdtp.py' `
  --pcap '.\artifacts\private\phase3.pcap' `
  --target-mac-file '.\artifacts\private\target.txt' `
  --private-fields '.\artifacts\private\phase3.fields.json' `
  --decode-as 'usb.device==0x00020004,bluetooth' `
  --requested-rate 96000 --requested-depth 24 `
  --output '.\artifacts\phase3.wire.json'
```

The private target file contains only the MAC; `TARGET_MAC` environment variable
is an alternative. The script never prints it. USB decode key is
`(actualPcapBus << 16) | allocatedAddress`; bus2/address4 above is an example.
For 192k use `--requested-rate 192000`. Existing private field JSON can be read
with `--fields-json ...` in place of `--pcap/--private-fields/--decode-as`.
Only the final report is safe for sharing. Field JSON and its schema belong in
`artifacts/private`; the capture owner manages ACL and gitignore. Raw PCAP stays private.
Offline analysis needs no administrator rights when file ACL permits reading.

USBPcap IRP addresses can be reused across unrelated transfers. The analyzer
selects OUT/submission (`irp_info` bit0=0) and IN/completion (bit0=1); it does
not deduplicate by IRP address. Reuse and peer-initiated PSM19 connections
with pending then success responses are covered by regression tests.

Historical capture setup, recorded read-only on 2026-10-06: tshark was at
`D:\Installations\Wireshark\tshark.exe`, USBPcap native at
`D:\Installations\Wireshark\extcap\USBPcapCMD.exe`, also native at
`C:\Program Files\USBPcap\USBPcapCMD.exe`. The recorded control device was
`\\.\USBPcap2`, Intel8087/PID0026, allocatedAddress4. These paths and device
values describe that session, not prerequisites verified on the current
machine. That session's native capture command used
`-d '\\.\USBPcap2' --devices 4 -s 65535 -b 1048576 -o ...` in elevated
PowerShell. Its writer retained Bulk plus Interrupt event03/05;
the historical `highrate/capture_bulk.py` helper dropped event03/05 and could
not verify an address. A live capture needs to start before reconnect and
L2CAP PSM0x19 setup; its operator controls duration and stopping. This tool
does not invoke any capture helper.

`verifiedAddress` requires status0, ACL linkType1 ConnectionComplete matching
the supplied address in the same USB scope. Disconnect and handle reuse end
pending transactions. L2CAP request/response must match direction, identifier,
SCID and success before its dual CID is accepted. Request/response matching
also includes connection generation, CID pair, direction, label, signal and a
15-second response window. Missing response, label reuse, reject and general
reject have separate statuses. Multiple fields in a signaling row are treated
as ambiguous rather than combined. Complete ACL reassembly uses tshark `-2`
and `bthci_acl.hci_acl_reassembly:TRUE`; unresolved fragments are flagged.
AVDTP packet_type != single is conservatively inconclusive in this minimum
version and cannot establish acceptance.

DelayReport (signal0x0D) is a metadata transaction with ordinary strict
request/accept/reject association; it does not establish codec selection.
Other syntactically valid signals outside the implemented set are explicitly
reported as unsupported, rather than mislabeled as malformed headers.

Without event03 the default is `noVerifiedAddress` and no target transactions.
`--allow-single-peer-inference` permits analysis only when the entire field
export has one USB-scoped ACL handle and no ConnectionComplete event at all;
the report says `single-active-peer-inference`, and comparison remains
inconclusive. A truncated or incomplete radio trace can invalidate that
single-peer assumption. It never establishes target address verification.

Capabilities are separate from selection. The unknown-vendor field starts
after six vendor/codec ID bytes; this script reconstructs `rawCodecHex` as
`3a050000354c` + the full value. Five value bytes therefore produce raw11.
Media category length must equal 8+value length. Longer values are preserved
as extensions. The rate and depth constants were verified against read-only
`reference/OPPO_Enco_X4_v116_LHDC_HiRes_Evidence.zip`, members
`evidence/lhdc_decoder/evidence.json:24-33` and
`evidence/lhdc_decoder/lhdc_analysis_zh.txt:58-61`: P6 masks20/10/04/01 mean
44100/48000/96000/192000Hz; P7 low3 masks04/02/01 mean16/24/32bit. The
referenced official header itself was not present at its recorded ZIP path.
Other bits remain raw and uninterpreted. Expected capability byte pairs
30/16 (default) and35/06 (HiRes) are comparison values, not selection proof.

Only a single-rate/single-depth LHDC5 SetConfiguration/Reconfigure accept
establishes an accepted configuration. GetConfiguration replies are recorded
independently. Start accept must match that endpoint and initiator direction.
LHDC5 media count uses tshark's vendor053A/codec4C35 media marker on a verified
PSM19 CID. A second count requires the matching accepted config and Start.
`hostSetterRequested_vsWireObserved` compares the supplied phase request to
those observed streams. Each setter phase needs a separate capture for that
comparison.
Wire negotiation and packet activity do not measure decoded PCM sample rate;
actual96/192 audio validation requires separate live measurements outside
this offline analyzer. This is a measurement limitation, not a pending task
for the current refresh work. Actual packet bitrate
is explicitly not measured because codec headers, aggregation and fragments
are not accounted for.

Run the 23 synthetic tests from the repository root with Python 3 and tshark
installed. One test invokes tshark against a generated PCAP; the suite performs
no live capture or device access:

```powershell
$env:PYTHONDONTWRITEBYTECODE='1'
python -m unittest discover -s '.\tools\audio-evidence' -p 'test_*.py' -v
```
