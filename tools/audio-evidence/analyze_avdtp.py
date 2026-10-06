"""Offline, allowlisted tshark reader. Python standard library only; never captures.

Private field JSON contains target/link identifiers. Only analyze() output is safe
to share. Capabilities, submitted configuration, accepted configuration and media
are distinct evidence. Fragmented AVDTP is deliberately inconclusive.
"""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

# Cross-checked against immutable reference ZIP members:
# evidence/lhdc_decoder/evidence.json:24-33 and
# evidence/lhdc_decoder/lhdc_analysis_zh.txt:58-61. See README.md.
RATES = {0x20: 44100, 0x10: 48000, 0x04: 96000, 0x01: 192000}
DEPTHS = {0x04: 16, 0x02: 24, 0x01: 32}
SIGNALS = {1: 'Discover', 2: 'GetCapabilities', 3: 'SetConfiguration',
           4: 'GetConfiguration', 5: 'Reconfigure', 6: 'Open', 7: 'Start',
           8: 'Close', 9: 'Suspend', 10: 'Abort', 12: 'GetAllCapabilities',
           13: 'DelayReport'}
FIELDS = (
    'frame.number frame.time_relative frame.cap_len frame.len '
    'usb.bus_id usb.device_address usb.endpoint_address usb.usbd_status usb.irp_id usb.irp_info '
    'bthci_evt.code bthci_evt.status bthci_evt.bd_addr bthci_evt.connection_handle bthci_evt.link_type '
    'bthci_acl.chandle bthci_acl.pb_flag bthci_acl.reassembled_in '
    'btl2cap.cid btl2cap.cmd_code btl2cap.cmd_ident btl2cap.psm btl2cap.scid btl2cap.dcid '
    'btl2cap.result btl2cap.status btl2cap.reassembled_in '
    'btavdtp.transaction btavdtp.packet_type btavdtp.message_type btavdtp.signal_id '
    'btavdtp.acp_seid btavdtp.int_seid btavdtp.sep_seid btavdtp.sep_type '
    'btavdtp.service_category btavdtp.length_of_service_category btavdtp.error_code '
    'btavdtp.codec.vendor.vendor_id btavdtp.codec.vendor.codec_id btavdtp.codec.vendor.value '
    'bta2dp.codec.vendor.vendor_id bta2dp.codec.vendor.codec_id bta2dp.acp_seid '
    'bta2dp.stream_start_in_frame _ws.malformed.expert _ws.malformed.reassembly'
).split()


class EvidenceError(Exception):
    """Messages are static; never include paths, command args or stderr."""


def values(row, name):
    v = row.get(name, [])
    return v if isinstance(v, list) else [v]


def number(v):
    s = str(v)
    return int(s, 16) if s.lower().startswith('0x') else int(s)


def one(row, name, default=None):
    v = values(row, name)
    if not v:
        return default
    if len(v) != 1:
        raise ValueError('ambiguous field')
    return number(v[0])


def nums(row, name):
    return [number(v) for v in values(row, name)]


def mac(value):
    text = re.sub('[:-]', '', value.strip()).lower()
    if not re.fullmatch('[0-9a-f]{12}', text):
        raise EvidenceError('Invalid private target address')
    return text


def layers(packet):
    return packet.get('_source', {}).get('layers', packet)


def codec(row):
    vendor = one(row, 'btavdtp.codec.vendor.vendor_id')
    cid = one(row, 'btavdtp.codec.vendor.codec_id')
    if vendor != 0x053a or cid != 0x4c35:
        return None
    raw = values(row, 'btavdtp.codec.vendor.value')
    if len(raw) != 1:
        raise ValueError('missing or multiple codec values')
    data = bytes.fromhex(str(raw[0]).replace(':', '').replace(' ', ''))
    # Unknown-vendor Value excludes the six vendor/codec ID bytes. Five
    # bytes gives an 11-byte LHDC5 codec blob (13-byte media codec category).
    if len(data) < 5 or len(data) > 247:
        raise ValueError('invalid LHDC5 value length')
    cats = nums(row, 'btavdtp.service_category')
    lens = nums(row, 'btavdtp.length_of_service_category')
    if len(cats) != len(lens) or cats.count(7) != 1:
        raise ValueError('ambiguous media category')
    if lens[cats.index(7)] != 8 + len(data):
        raise ValueError('media category length mismatch')
    rate_mask, depth_mask = data[0], data[1] & 7
    return {'vendorId': '0x0000053a', 'codecId': '0x4c35',
            'rawCodecHex': (b'\x3a\x05\x00\x00\x35\x4c' + data).hex(),
            'rawCodecLength': 6 + len(data), 'vendorValueHex': data.hex(),
            'rateFlagsHex': f'{rate_mask:02x}', 'depthFlagsHex': f'{depth_mask:02x}',
            'sampleRatesHz': [hz for bit, hz in RATES.items() if rate_mask & bit],
            'bitDepths': [d for bit, d in DEPTHS.items() if depth_mask & bit],
            'unknownRateBitsHex': f'{rate_mask & ~0x35:02x}',
            'uninterpretedSecondByteBitsHex': f'{data[1] & ~7:02x}',
            'extensionHex': data[5:].hex()}


def selected(c):
    return (c is not None and len(c['sampleRatesHz']) == 1 and
            len(c['bitDepths']) == 1 and c['unknownRateBitsHex'] == '00')


def analyze(packets, target, requested_rate=None, requested_depth=None,
            allow_single_peer_inference=False, transaction_timeout=15):
    target = mac(target)
    rows = [layers(p) for p in packets]
    observed_handles = set()
    has_connection_event = False
    for r in rows:
        try:
            h = one(r, 'bthci_acl.chandle')
            if h is not None:
                observed_handles.add((one(r, 'usb.bus_id'), one(r, 'usb.device_address'), h))
            has_connection_event |= one(r, 'bthci_evt.code') == 3
        except (ValueError, TypeError):
            pass
    inferred = (allow_single_peer_inference and not has_connection_event and
                len(observed_handles) == 1)
    links, l2_pending, channels, pending, active = {}, {}, {}, {}, {}
    transactions, issues, link_events = [], [], []
    generation = 0
    media_count = 0
    media_started_count = 0
    verified_links = 0

    def issue(code, frame):
        issues.append({'code': code, 'frame': frame})

    def finish(req, status, r=None):
        rec = dict(req['safe'])
        rec['ackStatus'] = status
        if r is not None:
            rec['responseFrame'] = one(r, 'frame.number')
            rec['responseTimeSeconds'] = float(values(r, 'frame.time_relative')[0])
            rec['errorCode'] = one(r, 'btavdtp.error_code')
        transactions.append(rec)
        return rec

    for r in rows:
        frame = 0
        try:
            frame = one(r, 'frame.number', 0)
            time = float(values(r, 'frame.time_relative')[0])
            scope = (one(r, 'usb.bus_id'), one(r, 'usb.device_address'))
            ep = one(r, 'usb.endpoint_address')
            if ep is None or None in scope:
                issue('missingUsbScopeOrDirection', frame)
                continue
            direction = 'rx' if ep & 0x80 else 'tx'
            if one(r, 'usb.usbd_status', 0) != 0:
                issue('usbStatusFailure', frame)
                continue
            # IRP ID is an allocation address, reused by later transfers.
            # USBPcap exposes OUT data on submission (info bit0=0) and IN
            # data on completion (bit0=1). Select the data-bearing side;
            # never deduplicate by an IRP pointer across the capture.
            irp_info = one(r, 'usb.irp_info')
            if irp_info is not None and (irp_info & 1) != int(direction == 'rx'):
                continue
            event = one(r, 'bthci_evt.code')
            if event in (3, 5):
                if one(r, 'bthci_evt.status') != 0:
                    continue
                handle = one(r, 'bthci_evt.connection_handle')
                if handle is None:
                    issue('missingEventHandle', frame)
                    continue
                key = scope + (handle,)
                old = links.pop(key, None)
                if old:
                    for pk in list(pending):
                        if pk[0] == old[0]:
                            finish(pending.pop(pk), 'connectionEnded')
                if event == 3:
                    addr = values(r, 'bthci_evt.bd_addr')
                    if len(addr) != 1 or one(r, 'bthci_evt.link_type') != 1:
                        continue
                    generation += 1
                    is_target = mac(str(addr[0])) == target
                    links[key] = (generation, is_target)
                    if is_target:
                        verified_links += 1
                        link_events.append({'frame': frame, 'timeSeconds': time,
                                            'connection': generation, 'status': 'connected'})
                elif old and old[1]:
                    link_events.append({'frame': frame, 'timeSeconds': time,
                                        'connection': old[0], 'status': 'disconnected'})
                continue
            handle = one(r, 'bthci_acl.chandle')
            if handle is None:
                continue
            link_key = scope + (handle,)
            if inferred and link_key not in links:
                generation += 1
                links[link_key] = (generation, True)
            link = links.get(link_key)
            if not link or not link[1]:
                continue
            epoch = link[0]
            if one(r, 'frame.cap_len', 0) < one(r, 'frame.len', 0):
                issue('truncatedFrame', frame)
                continue
            if values(r, '_ws.malformed.expert') or values(r, '_ws.malformed.reassembly'):
                issue('malformedOrReassemblyError', frame)
                continue
            if (one(r, 'bthci_acl.reassembled_in', 0) > frame or
                    one(r, 'btl2cap.reassembled_in', 0) > frame):
                continue
            cid = one(r, 'btl2cap.cid')
            if cid is None:
                if one(r, 'bthci_acl.pb_flag') == 1:
                    issue('unresolvedAclContinuation', frame)
                continue
            if cid == 1:
                cmd = one(r, 'btl2cap.cmd_code')
                ident = one(r, 'btl2cap.cmd_ident')
                if cmd == 2 and one(r, 'btl2cap.psm') == 0x19:
                    scid = one(r, 'btl2cap.scid')
                    if scid is not None and scid >= 0x40:
                        l2_pending[(epoch, direction, ident)] = (scid, time)
                elif cmd == 3:
                    other = 'tx' if direction == 'rx' else 'rx'
                    k = (epoch, other, ident)
                    req = l2_pending.get(k)
                    if req and one(r, 'btl2cap.scid') == req[0]:
                        result = one(r, 'btl2cap.result')
                        if result == 1:  # pending is not a successful connection
                            continue
                        l2_pending.pop(k)
                        dcid = one(r, 'btl2cap.dcid')
                        if (result == 0 and one(r, 'btl2cap.status', 0) == 0 and
                                dcid is not None and dcid >= 0x40 and time - req[1] <= 30):
                            channel = (epoch, other, req[0], dcid)
                            channels[(epoch, other, dcid)] = channel
                            channels[(epoch, direction, req[0])] = channel
                elif cmd in (6, 7):
                    scid, dcid = one(r, 'btl2cap.scid'), one(r, 'btl2cap.dcid')
                    for ck, cv in list(channels.items()):
                        if cv[0] == epoch and {cv[2], cv[3]} == {scid, dcid}:
                            channels.pop(ck)
                            for pk in list(pending):
                                if pk[1] == cv:
                                    finish(pending.pop(pk), 'channelEnded')
                continue
            channel = channels.get((epoch, direction, cid))
            if channel is None:
                if values(r, 'btavdtp.signal_id') or values(r, 'bta2dp.codec.vendor.codec_id'):
                    issue('unverifiedAvdtpChannel', frame)
                continue
            # Media count uses decoded LHDC5 identity, never audio payload.
            mv = one(r, 'bta2dp.codec.vendor.vendor_id')
            mc = one(r, 'bta2dp.codec.vendor.codec_id')
            if mv == 0x053a and mc == 0x4c35:
                media_count += 1
                seid = one(r, 'bta2dp.acp_seid')
                candidates = [v for (e, s, d), v in active.items()
                              if e == epoch and d == direction and (seid is None or s == seid)]
                if len(candidates) == 1 and candidates[0]['started']:
                    media_started_count += 1
                    candidates[0]['mediaPacketCount'] += 1
                continue
            ptype = one(r, 'btavdtp.packet_type')
            label = one(r, 'btavdtp.transaction')
            msg = one(r, 'btavdtp.message_type')
            if ptype is None:
                continue
            if ptype != 0:
                issue('avdtpFragmentUnsupported', frame)
                for pk in list(pending):
                    if pk[:2] == (epoch, channel) and pk[3] == label:
                        finish(pending.pop(pk), 'fragmentInconclusive')
                continue
            signal = one(r, 'btavdtp.signal_id')
            if (signal is None or not 1 <= signal <= 0x3f or
                    msg not in (0, 1, 2, 3) or label is None or not 0 <= label <= 15):
                issue('invalidAvdtpHeader', frame)
                continue
            if signal not in SIGNALS:
                issues.append({'code': 'unsupportedAvdtpSignal', 'frame': frame,
                               'signalId': signal, 'reason': 'Signal is not implemented'})
                continue
            key = (epoch, channel, direction, label)
            if msg == 0:
                if key in pending:
                    finish(pending.pop(key), 'supersededLabel')
                seids = nums(r, 'btavdtp.acp_seid')
                if signal in (7, 9) and (not seids or len(seids) != len(set(seids))):
                    issue('missingOrDuplicateStreamSeids', frame)
                    continue
                if signal in (2, 3, 4, 5, 6, 8, 10, 12, 13) and len(seids) != 1:
                    issue('missingOrAmbiguousRequestSeid', frame)
                    continue
                safe = {'signal': SIGNALS[signal], 'signalId': signal, 'transaction': label,
                        'connection': epoch, 'requestDirection': direction, 'seids': seids,
                        'requestFrame': frame, 'requestTimeSeconds': time}
                if signal in (3, 5):
                    safe['requestedCodec'] = codec(r)
                pending[key] = {'safe': safe, 'signal': signal, 'time': time,
                                'seids': seids, 'direction': direction, 'epoch': epoch}
                continue
            other = 'tx' if direction == 'rx' else 'rx'
            pk = (epoch, channel, other, label)
            req = pending.get(pk)
            if req is None or req['signal'] != signal:
                issue('unmatchedResponse', frame)
                continue
            pending.pop(pk)
            if not 0 <= time - req['time'] <= transaction_timeout:
                finish(req, 'responseOutsideWindow', r)
                continue
            status = {1: 'generalReject', 2: 'accept', 3: 'reject'}[msg]
            rec = finish(req, status, r)
            if msg != 2:
                continue
            if signal == 1:
                rec['discoveredSeids'] = nums(r, 'btavdtp.sep_seid')
            if signal in (2, 12, 4):
                rec['responseCodec'] = codec(r)
            if signal in (3, 5):
                c = rec.get('requestedCodec')
                ak = (epoch, req['seids'][0], req['direction'])
                if selected(c):
                    active[ak] = {'connection': epoch, 'seid': ak[1],
                                  'direction': ak[2], 'codec': c, 'acceptedFrame': frame,
                                  'started': False, 'startAcceptFrame': None, 'mediaPacketCount': 0}
                else:
                    active.pop(ak, None)
                    issue('acceptedConfigNotSingleLhdc5Selection', frame)
            if signal in (7, 8, 9, 10):
                for seid in req['seids']:
                    ak = (epoch, seid, req['direction'])
                    if ak in active:
                        active[ak]['started'] = signal == 7
                        if signal == 7:
                            active[ak]['startAcceptFrame'] = frame
        except (ValueError, TypeError, IndexError, EvidenceError):
            issue('malformedOrAmbiguousFields', frame)
    for req in pending.values():
        finish(req, 'missingResponse')
    configurations = list(active.values())
    observations = [v for v in configurations if v['startAcceptFrame'] is not None and v['mediaPacketCount']]
    requested = {'sampleRateHz': requested_rate, 'bitDepth': requested_depth}
    matches = [v for v in observations
               if (requested_rate is None or v['codec']['sampleRatesHz'] == [requested_rate])
               and (requested_depth is None or v['codec']['bitDepths'] == [requested_depth])]
    comparison = ('notRequested' if requested_rate is None and requested_depth is None
                  else 'inconclusive' if not observations or inferred
                  else 'matchesObservedStream' if matches else 'contradictedByObservedStream')
    return {'schemaVersion': 1,
            'targetAttribution': 'verifiedAddress' if verified_links else
                                 'single-active-peer-inference' if inferred else 'noVerifiedAddress',
            'verifiedConnectionCount': verified_links, 'connectionEvents': link_events,
            'transactions': transactions, 'acceptedConfigurations': configurations,
            'lhdc5MediaPacketCount': media_count, 'mediaAfterMatchingConfigAndStartCount': media_started_count,
            'hostSetterRequested_vsWireObserved': {'requested': requested, 'status': comparison},
            'packetActualBitrate': {'status': 'notMeasured',
                                   'reason': 'Codec media headers, aggregation and fragments are not accounted'},
            'actualAudioSampleRate': {'status': 'notMeasured',
                                      'reason': 'Accepted wire config and media activity do not measure decoded PCM'},
            'issues': issues}


def find_tshark(explicit=None):
    candidates = ([explicit] if explicit else []) + [
        r'D:\Installations\Wireshark\tshark.exe',
        r'C:\Program Files\Wireshark\tshark.exe', shutil.which('tshark')]
    for p in candidates:
        if p and Path(p).is_file():
            return str(p)
    raise EvidenceError('Installed tshark not found')


def run_tshark(command, timeout=120):
    try:
        p = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           timeout=timeout, check=False)
    except (OSError, subprocess.TimeoutExpired):
        raise EvidenceError('Offline tshark execution failed or timed out') from None
    if p.returncode:
        raise EvidenceError('Offline tshark returned a failure status')
    return p.stdout.decode('utf-8-sig')


def export_fields(tshark, pcap, private_output, decode_as=None):
    # No -i/-w/-V/-x, no media/USB/HCI payload fields, no key/name fields.
    catalog = run_tshark([tshark, '-G', 'fields'])
    available = {line.split('\t')[2] for line in catalog.splitlines()
                 if line.startswith('F\t') and len(line.split('\t')) > 2}
    missing = set(FIELDS) - available
    if missing:
        raise EvidenceError('Installed tshark lacks required allowlisted fields')
    private_output.parent.mkdir(parents=True, exist_ok=True)
    field_schema = {'fields': FIELDS, 'format': 'tshark -T json -e allowlist',
                    'private': True, 'hciAclReassembly': True}
    private_output.with_suffix('.schema.json').write_text(json.dumps(field_schema, indent=2), encoding='utf-8')
    command = [tshark, '-2', '-r', str(pcap), '-o', 'bthci_acl.hci_acl_reassembly:TRUE',
               '-Y', 'bthci_evt.code == 3 || bthci_evt.code == 5 || bthci_acl', '-T', 'json']
    if decode_as:
        if not re.fullmatch(r'usb.device==0x[0-9a-fA-F]{8},bluetooth', decode_as):
            raise EvidenceError('Invalid USB Bluetooth decode-as')
        command += ['-d', decode_as]
    for f in FIELDS:
        command += ['-e', f]
    output = run_tshark(command)
    private_output.write_text(output, encoding='utf-8')
    return json.loads(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    inputs = parser.add_mutually_exclusive_group(required=True)
    inputs.add_argument('--pcap', type=Path)
    inputs.add_argument('--fields-json', type=Path)
    parser.add_argument('--target-mac-file', type=Path,
                        help='Private file with target MAC only; alternative TARGET_MAC environment variable')
    parser.add_argument('--tshark')
    parser.add_argument('--decode-as')
    parser.add_argument('--private-fields', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--requested-rate', type=int, choices=list(RATES.values()))
    parser.add_argument('--requested-depth', type=int, choices=list(DEPTHS.values()))
    parser.add_argument('--allow-single-peer-inference', action='store_true')
    args = parser.parse_args()
    try:
        target = (args.target_mac_file.read_text(encoding='utf-8-sig').strip()
                  if args.target_mac_file else os.environ.get('TARGET_MAC', ''))
        mac(target)
        if args.pcap:
            if not args.private_fields:
                raise EvidenceError('PCAP input requires --private-fields in artifacts/private')
            if 'private' not in args.private_fields.parts:
                raise EvidenceError('Field export must be in a private directory')
            packets = export_fields(find_tshark(args.tshark), args.pcap, args.private_fields, args.decode_as)
        else:
            packets = json.loads(args.fields_json.read_text(encoding='utf-8-sig'))
        report = analyze(packets, target, args.requested_rate, args.requested_depth,
                         args.allow_single_peer_inference)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(json.dumps({'status': 'complete', 'targetAttribution': report['targetAttribution'],
                          'transactions': len(report['transactions']),
                          'lhdc5MediaPacketCount': report['lhdc5MediaPacketCount']}))
    except (OSError, ValueError, EvidenceError) as e:
        print(json.dumps({'status': 'failed', 'reason': str(e) if isinstance(e, EvidenceError)
                          else 'Private input or output processing failed'}), file=sys.stderr)
        return 2
    return 0


if __name__ == '__main__':
    sys.exit(main())
