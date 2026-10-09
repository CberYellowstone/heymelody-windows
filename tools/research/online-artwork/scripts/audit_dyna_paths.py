"""Offline, bounded audit of the two saved APK appearance request chains."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import re
import runpy
import struct
import sys
import zipfile

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT / 'tools/research/earbud-artwork/scripts'))
from research_runtime import research_path
reader = runpy.run_path(str(ROOT / 'tools/research/event_dex_audit.py'), run_name='dex_audit_import')
SAFE_STRING = re.compile(r'(?i)(resource|fetch|control|platform|android|version|channel|model|module|06C010|068C10|color|https?://|productId|firmware|headset|com\.oplus\.melody|EarphoneUtils)')
REFERENCES = re.compile(r'(?i)(getSupport3DModel|setModelFilePath|getModel3D|getModelWebp|vfxms|fetch14_|06C010|068C10|resource/(fetch|control)|resource/\{type\})')
SOURCES = [('H', '欢律.apk'), ('O', '无线耳机_17.6.3.apk')]

def audit(owners: set[str], refs_only: bool):
    report = []
    for label, apk_name in SOURCES:
        apk_path = ROOT / 'research/apps' / apk_name
        source = {'label': label, 'apk': str(apk_path.relative_to(ROOT)), 'apk_sha256': hashlib.sha256(apk_path.read_bytes()).hexdigest(), 'dex': []}
        with zipfile.ZipFile(apk_path) as archive:
            source['model_member_candidates'] = [n for n in archive.namelist() if re.search(r'(?i)(\.glb$|\.gltf$|\.vfxms$|\.ktx2?$|free4|06c010|068c10|\.upk$)', n)]
            for dex_name in archive.namelist():
                if not dex_name.endswith('.dex'): continue
                raw = archive.read(dex_name)
                strings, types, method_ids, methods = reader['read_dex'](raw)
                field_count, field_offset = struct.unpack_from('<II', raw, 80)
                fields = []
                for index in range(field_count):
                    owner_index, type_index, name_index = struct.unpack_from('<HHI', raw, field_offset + index * 8)
                    fields.append([types[owner_index], strings[name_index], types[type_index]])
                found = []
                for owner, name, proto, code in methods:
                    insns = []
                    hits = []
                    for offset, op, words in reader['instructions'](raw, code):
                        item = {'offset': hex(offset), 'opcode': hex(op), 'words': [hex(w) for w in words]}
                        if 0x6e <= op <= 0x72 or 0x74 <= op <= 0x78:
                            target = list(method_ids[words[1]])
                            item['invoke'] = target
                            if REFERENCES.search(' '.join(target)): hits.append(item)
                        elif 0x52 <= op <= 0x6d:
                            item['field'] = fields[words[1]]
                        elif op in (0x1a, 0x1b):
                            index = words[1] if op == 0x1a else words[1] + (words[2] << 16)
                            value = strings[index]
                            if len(value) <= 200 and (SAFE_STRING.search(value) or value in ('1', '2')): item['string'] = value
                            if REFERENCES.search(value): hits.append(item)
                        insns.append(item)
                    selected = owner in owners or any(owner.startswith(prefix[:-1]) for prefix in owners if prefix.endswith('*'))
                    if selected or (refs_only and hits):
                        found.append({'owner': owner, 'method': name + proto, 'code_offset': hex(code), 'instructions': insns if selected else hits})
                if found:
                    source['dex'].append({'name': dex_name, 'sha256': hashlib.sha256(raw).hexdigest(), 'methods': found})
        report.append(source)
    return report

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--owner', action='append', default=[])
    parser.add_argument('--refs', action='store_true')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    report = audit(set(args.owner), args.refs)
    text = json.dumps(report, ensure_ascii=False, indent=2)
    if args.output:
        path = research_path(args.output)
        if not path.is_relative_to((ROOT / 'research/online-artwork').resolve()): raise ValueError('Output must remain under online-artwork')
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open('x', encoding='utf-8') as file: file.write(text + '\n')
        print(json.dumps({'output': str(path.relative_to(ROOT)), 'sources': len(report)}, ensure_ascii=False))
    else:
        print(text)
