"""Read only the user's K-column suggestions; never modify the reviewed workbook."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

from workbook_io import displayed, read_rows


def suggestions(workbook, baseline):
    data = json.loads(baseline.read_text(encoding='utf-8'))
    records = {record['id']: record for record in data['records']}
    with zipfile.ZipFile(workbook) as archive:
        if archive.testzip() is not None:
            raise ValueError('Workbook snapshot ZIP CRC failed')
        rows, _ = read_rows(archive)
    if rows[6].get('A') != '文本 ID' or rows[6].get('K') != '建议文案':
        raise ValueError('Unexpected first-sheet layout')
    result = {'workbookSha256': hashlib.sha256(workbook.read_bytes()).hexdigest(),
              'baselineSha256': hashlib.sha256(baseline.read_bytes()).hexdigest(),
              'commit': data['baseline']['commit'], 'suggestions': [], 'ignored': [], 'errors': []}
    seen = set()
    for row, cells in sorted(rows.items()):
        if row <= 6 or not cells.get('K', '').strip():
            continue
        key = cells.get('A', '')
        if key in seen or key not in records:
            result['errors'].append({'row': row, 'id': key, 'reason': '重复或未知文本 ID'})
            continue
        seen.add(key)
        record = records[key]
        item = {'row': row, **record, 'suggestion': cells['K']}
        if cells.get('D', '').lstrip("'") != displayed(record['text']):
            result['errors'].append({'row': row, 'id': key, 'reason': '原文列与基线不同'})
            continue
        if record['kind'] == '配置设备名称' or '/Bundles/' in record['path']:
            result['ignored'].append({**item, 'reason': '欢律配置/资源原文，按用户要求忽略'})
        elif cells['K'] == displayed(record['text']):
            result['ignored'].append({**item, 'reason': '建议与原文相同'})
        else:
            result['suggestions'].append(item)
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('workbook', type=Path)
    parser.add_argument('baseline', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    result = suggestions(args.workbook, args.baseline)
    with args.output.open('x', encoding='utf-8') as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)
    print(json.dumps({'suggestions': len(result['suggestions']), 'ignored': len(result['ignored']),
                      'errors': result['errors'], 'output': str(args.output)}, ensure_ascii=False))
