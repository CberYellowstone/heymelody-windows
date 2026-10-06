"""Offline source-evidence checks only; never imports client/native modules."""
from pathlib import Path
import hashlib
import json
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[1]
REF = ROOT / 'reference/OPPOEncoX4/encox4'

def read(path):
    return path.read_text(encoding='utf-8-sig')

def main():
    labels = {'0': '丹拿原声', '7': '丹拿高解析', '2': '纯享人声', '1': '清亮高音', '3': '澎湃低音'}
    from xml.etree import ElementTree
    strings = {x.attrib['name']: x.text for x in ElementTree.parse(REF / 'apktool/res/values-zh-rCN/strings.xml').getroot().findall('string')}
    expected_resources = {'0': 'melody_ui_equalizer_dynaudio_original_sound', '7': 'melody_ui_equalizer_dynaudio_hd_analyze', '2': 'melody_ui_equalizer_pure_vocals', '1': 'melody_ui_equalizer_clear_treble', '3': 'melody_ui_equalizer_powerful_bass'}
    checks = []
    def check(name, condition):
        checks.append({'name': name, 'pass': bool(condition)})
    for key, resource in expected_resources.items():
        check('official-label-preset-' + key, strings[resource] == labels[key])
    profile = json.loads(read(ROOT / 'src/HeyMelody.Devices/Assets/oppo-enco-x4.json'))
    check('x4-preserves-noncontiguous-ids', [x['protocolIndex'] for x in profile['function']['equalizerMode']] == [0, 7, 2, 1, 3])
    check('x4-ten-frequencies', profile['function']['customEqFrequency'] == [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000])
    check('x4-spatial-hires-mutex', profile['function']['spatialHiResMutex'] == 1)
    check('x4-game-sound-mutex-excludes-spatial', profile['function']['gameSoundMutexes'] == [1, 3])
    constructor = read(REF / 'apktool/smali_classes3/Ab/Z$i.smali')
    check('official-create-gain-plus6', 'const/4 p1, 0x6' in constructor and '->setMaxValue(I)V' in constructor)
    check('official-create-gain-minus6', 'const/4 p1, -0x6' in constructor and '->setMinValue(I)V' in constructor)
    empty = read(REF / 'apktool/smali_classes3/w8.1/b.smali')
    check('official-default-byte-array-empty', 'const/4 v0, 0x0' in empty and 'new-array v0, v0, [B' in empty and 'sput-object v0, Lw8/b;->b:[B' in empty)
    reviewed = ['DailyClient.cs', 'DailySession.cs', 'DailyRefresh.cs', 'DailyApply.cs', 'DailyParsers.cs']
    hashes = {}
    for name in reviewed:
        path = ROOT / 'src/HeyMelody.Daily' / name
        hashes[str(path.relative_to(ROOT)).replace('\\', '/')] = hashlib.sha256(path.read_bytes()).hexdigest()
    report = {
        'schemaVersion': 1,
        'observedAtUtc': datetime.now(timezone.utc).isoformat(),
        'runtimeModel': 'gpt-6.1-sol',
        'runtimeEffort': 'high',
        'evidenceMode': 'offline-reference-and-bounded-static-review',
        'deviceCalls': False, 'settersRun': False, 'privateCaptureRead': False,
        'runtimeArm64Verified': False,
        'testScope': 'source-consistency assertions only; not driver or headset behavior',
        'pass': all(x['pass'] for x in checks),
        'checks': checks,
        'presetLabels': labels,
        'sourceHashes': hashes,
        'resolvedFindings': ['custom-select uses 0418 action2 fullentry instead of 0406 customID', 'kind4 non-ANC masks stay unknown', '0122 official empty request', 'existing EQ names preserved separately from new-name validation'],
        'remainingProtocolUnknowns': ['action1 create auto-selection requires live selected readback', 'official UI maximum name length is not inferred from 255-byte wire bound'],
        'blockingFindingsInReadScope': [],
        'outOfReviewScope': ['GUI', 'Windows native connector', 'release packaging', 'ARM64 native execution', 'OTA', 'recording', 'translation', 'health']
    }
    out = ROOT / 'artifacts/phase5-protocol-evidence.json'
    out.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'pass': report['pass'], 'checks': len(checks), 'output': str(out)}, ensure_ascii=False))
    return 0 if report['pass'] else 1

if __name__ == '__main__':
    raise SystemExit(main())
