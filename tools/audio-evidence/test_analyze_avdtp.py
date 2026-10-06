import copy
import json
import unittest
import struct
import tempfile
from pathlib import Path
from analyze_avdtp import analyze, codec, export_fields, find_tshark

TARGET = '12:34:56:78:9a:bc'
OTHER = '98:76:54:32:10:fe'


def row(frame, rx=False, **kw):
    r = {'frame.number': [str(frame)], 'frame.time_relative': [str(frame / 10)],
         'usb.bus_id': ['2'], 'usb.device_address': ['4'],
         'usb.endpoint_address': ['0x81' if rx else '0x02'],
         'usb.usbd_status': ['0'], 'bthci_acl.chandle': ['0x0012']}
    r.update({k: v if isinstance(v, list) else [str(v)] for k, v in kw.items()})
    return r


def connect(frame=1, address=TARGET, status=0):
    return row(frame, True, **{'bthci_evt.code': 3, 'bthci_evt.status': status,
                              'bthci_evt.link_type': 1, 'bthci_evt.connection_handle': '0x12',
                              'bthci_evt.bd_addr': address})


def channel(start=2, scid='0x40', dcid='0x41', ident=1):
    return [row(start, **{'btl2cap.cid': 1, 'btl2cap.cmd_code': 2,
                         'btl2cap.cmd_ident': ident, 'btl2cap.psm': '0x19', 'btl2cap.scid': scid}),
            row(start + 1, True, **{'btl2cap.cid': 1, 'btl2cap.cmd_code': 3,
                                    'btl2cap.cmd_ident': ident, 'btl2cap.scid': scid,
                                    'btl2cap.dcid': dcid, 'btl2cap.result': 0})]


def sig(frame, signal, msg=0, seid=1, label=1, **kw):
    r = row(frame, msg != 0, **{'btl2cap.cid': '0x40' if msg else '0x41',
                              'btavdtp.transaction': label, 'btavdtp.packet_type': 0,
                              'btavdtp.message_type': msg, 'btavdtp.signal_id': signal})
    if msg == 0:
        r['btavdtp.acp_seid'] = [str(seid)]
    r.update({k: v if isinstance(v, list) else [str(v)] for k, v in kw.items()})
    return r


def cfields(value='0412110000', vendor='0x053a'):
    return {'btavdtp.service_category': ['1', '7'],
            'btavdtp.length_of_service_category': ['0', '13'],
            'btavdtp.codec.vendor.vendor_id': vendor,
            'btavdtp.codec.vendor.codec_id': '0x4c35', 'btavdtp.codec.vendor.value': value}


def stream(value='0412110000', reject=False):
    return [connect()] + channel() + [sig(4, 3, **cfields(value)), sig(5, 3, 3 if reject else 2),
        sig(6, 7), sig(7, 7, 2), row(8, **{'btl2cap.cid': '0x41', 'bta2dp.acp_seid': 1,
        'bta2dp.codec.vendor.vendor_id': '0x053a', 'bta2dp.codec.vendor.codec_id': '0x4c35'})]


class EvidenceTests(unittest.TestCase):
    def test_multiple_sep_caps_never_selection(self):
        rows = [connect()] + channel()
        for n, seid, value in [(4, 1, '3016114040'), (6, 2, '3506114040')]:
            rows += [sig(n, 12, seid=seid), sig(n + 1, 12, 2, **cfields(value))]
        result = analyze(rows, TARGET, 96000, 24)
        self.assertEqual([t['seids'] for t in result['transactions']], [[1], [2]])
        self.assertEqual(result['acceptedConfigurations'], [])
        self.assertEqual(result['hostSetterRequested_vsWireObserved']['status'], 'inconclusive')
        self.assertEqual(result['transactions'][1]['responseCodec']['rawCodecLength'], 11)

    def test_accepted_stream_and_falsification(self):
        result = analyze(stream(), TARGET, 96000, 24)
        self.assertEqual(result['mediaAfterMatchingConfigAndStartCount'], 1)
        self.assertEqual(result['hostSetterRequested_vsWireObserved']['status'], 'matchesObservedStream')
        self.assertEqual(analyze(stream('1012110000'), TARGET, 96000, 24)
                         ['hostSetterRequested_vsWireObserved']['status'], 'contradictedByObservedStream')

    def test_reject_configuration(self):
        result = analyze(stream(reject=True), TARGET)
        self.assertEqual(result['transactions'][0]['ackStatus'], 'reject')
        self.assertEqual(result['acceptedConfigurations'], [])
        self.assertEqual(result['mediaAfterMatchingConfigAndStartCount'], 0)

    def test_vendor_incorrect(self):
        rows = stream()
        rows[3].update({k: [str(v)] if not isinstance(v, list) else v for k, v in cfields(vendor='0x1234').items()})
        self.assertEqual(analyze(rows, TARGET)['acceptedConfigurations'], [])

    def test_target_matching(self):
        rows = stream()
        rows[0] = connect(address=OTHER)
        self.assertEqual(analyze(rows, TARGET)['transactions'], [])
        self.assertNotIn(OTHER, json.dumps(analyze(rows, TARGET)))
        self.assertNotIn(TARGET, json.dumps(analyze(stream(), TARGET)))

    def test_failed_connection(self):
        rows = stream()
        rows[0] = connect(status=4)
        self.assertEqual(analyze(rows, TARGET)['transactions'], [])

    def test_missing03_explicit_inference(self):
        rows = stream()[1:]
        self.assertEqual(analyze(rows, TARGET)['targetAttribution'], 'noVerifiedAddress')
        result = analyze(rows, TARGET, 96000, 24, True)
        self.assertEqual(result['targetAttribution'], 'single-active-peer-inference')
        self.assertEqual(result['hostSetterRequested_vsWireObserved']['status'], 'inconclusive')

    def test_multiple_handles_disable_inference(self):
        rows = stream()[1:] + [row(9, **{'bthci_acl.chandle': '0x13'})]
        self.assertEqual(analyze(rows, TARGET, allow_single_peer_inference=True)['transactions'], [])

    def test_handle_reuse_and_disconnect(self):
        rows = stream()[:4] + [connect(5, OTHER), sig(6, 3, 2)]
        result = analyze(rows, TARGET)
        self.assertEqual(result['transactions'][0]['ackStatus'], 'connectionEnded')
        self.assertEqual(result['acceptedConfigurations'], [])

    def test_response_wrong_direction_or_signal(self):
        rows = stream()[:4] + [sig(5, 5, 2)]
        self.assertEqual(analyze(rows, TARGET)['transactions'][0]['ackStatus'], 'missingResponse')

    def test_label_reuse(self):
        rows = stream()[:4] + [sig(5, 12, seid=2), sig(6, 12, 2, **cfields())]
        self.assertEqual([t['ackStatus'] for t in analyze(rows, TARGET)['transactions']],
                         ['supersededLabel', 'accept'])

    def test_fragment_inconclusive(self):
        rows = stream()
        rows[4]['btavdtp.packet_type'] = ['1']
        result = analyze(rows, TARGET)
        self.assertEqual(result['transactions'][0]['ackStatus'], 'fragmentInconclusive')
        self.assertEqual(result['acceptedConfigurations'], [])

    def test_no_dynamic_cid_proof(self):
        self.assertEqual(analyze([connect()] + stream()[3:], TARGET)['transactions'], [])

    def test_bad_losc_and_malformed(self):
        rows = stream()
        rows[3]['btavdtp.length_of_service_category'] = ['0', '12']
        self.assertEqual(analyze(rows, TARGET)['acceptedConfigurations'], [])
        rows = stream()
        rows[4]['_ws.malformed.expert'] = ['1']
        self.assertEqual(analyze(rows, TARGET)['acceptedConfigurations'], [])

    def test_usb_status(self):
        rows = stream()
        rows[4]['usb.usbd_status'] = ['0xc0000001']
        self.assertEqual(analyze(rows, TARGET)['acceptedConfigurations'], [])

    def test_reused_irp_address_does_not_drop_later_transfers(self):
        rows = stream()
        for r in rows:
            r['usb.irp_id'] = ['0xffff012345678000']
            r['usb.irp_info'] = ['1' if int(r['usb.endpoint_address'][0], 0) & 0x80 else '0']
        result = analyze(rows, TARGET, 96000, 24)
        self.assertEqual(result['targetAttribution'], 'verifiedAddress')
        self.assertEqual(result['mediaAfterMatchingConfigAndStartCount'], 1)
        self.assertEqual(result['hostSetterRequested_vsWireObserved']['status'], 'matchesObservedStream')

    def test_usb_submission_completion_only_counts_data_side(self):
        rows = stream()
        doubled = []
        for r in rows:
            direction_bit = int(bool(int(r['usb.endpoint_address'][0], 0) & 0x80))
            wrong = copy.deepcopy(r)
            wrong['usb.irp_info'] = [str(1 - direction_bit)]
            r['usb.irp_info'] = [str(direction_bit)]
            doubled += [wrong, r]
        result = analyze(doubled, TARGET)
        self.assertEqual(result['verifiedConnectionCount'], 1)
        self.assertEqual(result['lhdc5MediaPacketCount'], 1)
        self.assertEqual(len(result['transactions']), 2)

    def test_peer_initiated_l2cap_pending_then_success_reused_irp(self):
        # Anonymized shape from root inspection: peer SCID0x9040, host
        # DCID0x42, incoming request and outgoing pending/success replies.
        rows = [connect(), row(2, True, **{'btl2cap.cid': 1, 'btl2cap.cmd_code': 2,
            'btl2cap.cmd_ident': 3, 'btl2cap.psm': '0x19', 'btl2cap.scid': '0x9040'})]
        for frame, result in [(3, 1), (4, 0)]:
            rows.append(row(frame, **{'btl2cap.cid': 1, 'btl2cap.cmd_code': 3,
                'btl2cap.cmd_ident': 3, 'btl2cap.scid': '0x9040',
                'btl2cap.dcid': '0x42', 'btl2cap.result': result}))
        for r in [sig(5, 12, seid=2), sig(6, 12, 2, **cfields('3506114040')),
                  sig(7, 3, seid=2, **cfields()), sig(8, 3, 2)]:
            rx = bool(int(r['usb.endpoint_address'][0], 0) & 0x80)
            r['btl2cap.cid'] = ['0x42' if rx else '0x9040']
            rows.append(r)
        for r in rows:
            r['usb.irp_id'] = ['0xffff012345678000']
            r['usb.irp_info'] = ['1' if int(r['usb.endpoint_address'][0], 0) & 0x80 else '0']
        report = analyze(rows, TARGET)
        self.assertEqual([r['ackStatus'] for r in report['transactions']], ['accept', 'accept'])
        self.assertEqual(report['transactions'][0]['responseCodec']['sampleRatesHz'],
                         [44100, 48000, 96000, 192000])
        self.assertEqual(report['acceptedConfigurations'][0]['codec']['sampleRatesHz'], [96000])
        self.assertEqual(report['issues'], [])

    def test_get_configuration_independent_response(self):
        rows = [connect()] + channel() + [sig(4, 4, seid=2), sig(5, 4, 2, **cfields())]
        result = analyze(rows, TARGET)
        self.assertEqual(result['transactions'][0]['responseCodec']['sampleRatesHz'], [96000])
        self.assertEqual(result['acceptedConfigurations'], [])

    def test_unknown_bits_kept(self):
        r = sig(4, 3, **cfields('8c12110000'))
        self.assertEqual(codec(r)['unknownRateBitsHex'], '88')

    def test_delay_report_is_valid_metadata_transaction(self):
        request = sig(4, 13, seid=2)
        response = sig(5, 13, 2)
        # Delay report from peer, accept from host, on established CID pair.
        request['usb.endpoint_address'] = ['0x81']
        request['btl2cap.cid'] = ['0x40']
        response['usb.endpoint_address'] = ['0x02']
        response['btl2cap.cid'] = ['0x41']
        result = analyze([connect()] + channel() + [request, response], TARGET)
        self.assertEqual(result['issues'], [])
        self.assertEqual(result['transactions'][0]['signal'], 'DelayReport')
        self.assertEqual(result['transactions'][0]['ackStatus'], 'accept')
        self.assertEqual(result['acceptedConfigurations'], [])

    def test_unsupported_signal_is_not_malformed_or_fragment(self):
        result = analyze([connect()] + channel() + [sig(4, 14), sig(5, 14, 2)], TARGET)
        self.assertEqual([i['code'] for i in result['issues']],
                         ['unsupportedAvdtpSignal', 'unsupportedAvdtpSignal'])
        self.assertEqual(result['transactions'], [])

    def test_installed_tshark_synthetic_pcap_roundtrip(self):
        packets = []
        def usb(data, rx=False, interrupt=False):
            irp = len(packets) + 1
            header = struct.pack('<HQIHBHHBBI', 27, irp, 0, 9, int(rx),
                                 2, 4, 0x81 if rx else 2, 1 if interrupt else 3, len(data))
            packets.append(header + data)
        def acl(data, cid, rx=False):
            l2 = struct.pack('<HH', len(data), cid) + data
            usb(struct.pack('<HH', 0x2012, len(l2)) + l2, rx)
        usb(b'\x03\x0b\x00\x12\x00' + bytes.fromhex('bc9a78563412') + b'\x01\x00', True, True)
        acl(bytes.fromhex('0201040019004000'), 1)
        acl(bytes.fromhex('030108004100400000000000'), 1, True)
        cat = bytes.fromhex('0100070d00ff3a050000354c0412110000')
        acl(bytes.fromhex('100c04'), 0x41)
        acl(bytes.fromhex('120c') + cat, 0x40, True)
        acl(bytes.fromhex('20030408') + cat, 0x41)
        acl(bytes.fromhex('2203'), 0x40, True)
        acl(bytes.fromhex('300704'), 0x41)
        acl(bytes.fromhex('3207'), 0x40, True)
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as temp:
            p = Path(temp)
            capture = p / 'synthetic-only.pcap'
            with capture.open('wb') as stream:
                stream.write(struct.pack('<IHHIIII', 0xa1b2c3d4, 2, 4, 0, 0, 65535, 249))
                for i, packet in enumerate(packets):
                    stream.write(struct.pack('<IIII', 1, i * 100000, len(packet), len(packet)))
                    stream.write(packet)
            decoded = export_fields(find_tshark(), capture, p / 'private' / 'fields.json',
                                    'usb.device==0x00020004,bluetooth')
            result = analyze(decoded, TARGET)
            self.assertEqual(result['targetAttribution'], 'verifiedAddress')
            self.assertEqual([t['ackStatus'] for t in result['transactions']], ['accept'] * 3)
            self.assertEqual(result['transactions'][0]['responseCodec']['rawCodecHex'],
                             '3a050000354c0412110000')
            self.assertEqual(result['acceptedConfigurations'][0]['startAcceptFrame'], 9)


if __name__ == '__main__':
    unittest.main()
