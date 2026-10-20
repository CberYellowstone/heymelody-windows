"""Native XLSX links, round-trip review fields, and structural checks (stdlib only).

The workbook itself is authored with @oai/artifact-tool. This helper adds native
hyperlinks, which avoids HYPERLINK formula-engine discrepancies, and reads review
cells without requiring Excel or modifying the user's reviewed workbook.
"""
import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
import zipfile
import xml.etree.ElementTree as ET
import re

S = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
R = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
P = 'http://schemas.openxmlformats.org/package/2006/relationships'
ET.register_namespace('', S)
ET.register_namespace('r', R)


def displayed(value):
    return re.sub(r'[\x00-\x08\x0b\x0c\x0e-\x1f\ufffe\uffff]', lambda m: '\\u%04x' % ord(m[0]), value).lstrip("'")


def read_rows(archive, sheet=1):
    shared = []
    if 'xl/sharedStrings.xml' in archive.namelist():
        shared = [''.join(n.itertext()) for n in ET.fromstring(archive.read('xl/sharedStrings.xml')).findall(f'{{{S}}}si')]
    root = ET.fromstring(archive.read(f'xl/worksheets/sheet{sheet}.xml'))
    rows = {}
    for row in root.findall(f'{{{S}}}sheetData/{{{S}}}row'):
        cells = {}
        for c in row.findall(f'{{{S}}}c'):
            v = c.find(f'{{{S}}}v')
            value = v.text if v is not None else ''
            if c.get('t') == 's':
                value = shared[int(value)] if value else ''
            elif c.get('t') == 'inlineStr':
                value = ''.join(c.find(f'{{{S}}}is').itertext())
            cells[''.join(ch for ch in c.get('r') if ch.isalpha())] = value or ''
        rows[int(row.get('r'))] = cells
    return rows, root


def merge_reviews(data, rows):
    result = copy.deepcopy(data)
    existing = {r['id']: r for r in result['records']}
    seen = set()
    assert rows[6]['A'] == '文本 ID' and rows[6]['J'] == '措辞结论', 'Unexpected review workbook schema'
    fields = {'H': 'traceConfirmed', 'I': 'sceneConfirmed', 'J': 'review', 'K': 'suggestion', 'L': 'note'}
    for n, cells in rows.items():
        if n <= 6 or not cells.get('A'):
            continue
        key = cells['A']
        if key in seen or key not in existing:
            raise ValueError(f'Duplicate or unknown text ID: {key}')
        seen.add(key)
        old = existing[key]
        if cells.get('D', '').lstrip("'") != displayed(old['text']):
            raise ValueError(f'Original text changed for {key}; write suggestions in column K')
        for col, field in fields.items():
            old[field] = cells.get(col, '')
        if old['review'] not in ('未审阅', '通过', '需修改', '需讨论'):
            raise ValueError(f'Invalid review state: {key}')
        for field in ('traceConfirmed', 'sceneConfirmed'):
            if old[field] not in ('待核实', '已核实', '不适用'):
                raise ValueError(f'Invalid evidence state: {key}')
    if seen != existing.keys():
        raise ValueError('Missing rows: sorting is supported; deleting IDs is not')
    return result


def add_links(workbook, links):
    with zipfile.ZipFile(workbook) as source:
        parts = {n: source.read(n) for n in source.namelist()}
    for sheet in range(4):
        sheet_name = f'xl/worksheets/sheet{sheet+1}.xml'
        rel_name = f'xl/worksheets/_rels/sheet{sheet+1}.xml.rels'
        tree = ET.fromstring(parts[sheet_name])
        node = tree.find(f'{{{S}}}hyperlinks')
        if node is not None:
            tree.remove(node)
        node = ET.Element(f'{{{S}}}hyperlinks')
        rels = ET.fromstring(parts[rel_name]) if rel_name in parts else ET.Element(f'{{{P}}}Relationships')
        used = {r.get('Id') for r in rels}
        for i, link in enumerate(x for x in links if x['sheet'] == sheet):
            attr = {'ref': link['cell']}
            if 'location' in link:
                attr['location'] = link['location']
            else:
                rid = f'auditLink{i}'
                while rid in used:
                    rid += '_'
                used.add(rid)
                attr[f'{{{R}}}id'] = rid
                ET.SubElement(rels, f'{{{P}}}Relationship', {'Id': rid, 'Type': R+'/hyperlink',
                    'Target': link['target'], 'TargetMode': 'External'})
            ET.SubElement(node, f'{{{S}}}hyperlink', attr)
        after = {'printOptions', 'pageMargins', 'pageSetup', 'headerFooter', 'rowBreaks', 'colBreaks', 'customProperties',
                 'cellWatches', 'ignoredErrors', 'smartTags', 'drawing', 'legacyDrawing', 'legacyDrawingHF',
                 'picture', 'oleObjects', 'controls', 'webPublishItems', 'tableParts', 'extLst'}
        index = next((i for i, child in enumerate(tree) if child.tag.split('}')[-1] in after), len(tree))
        tree.insert(index, node)
        parts[sheet_name] = ET.tostring(tree, encoding='utf-8', xml_declaration=True)
        parts[rel_name] = ET.tostring(rels, encoding='utf-8', xml_declaration=True)
    temporary = workbook.with_suffix('.links.tmp')
    if temporary.exists():
        raise FileExistsError(temporary)
    with zipfile.ZipFile(temporary, 'x', zipfile.ZIP_DEFLATED) as dest:
        for name, content in parts.items():
            dest.writestr(name, content)
    os.replace(temporary, workbook)


def validate(folder):
    workbook = folder/'界面文本审计.xlsx'
    data = json.loads((folder/'audit-data.json').read_text(encoding='utf-8'))
    expected = data['records']
    with zipfile.ZipFile(workbook) as archive:
        assert archive.testzip() is None
        # All XML parts, including table, validation and relationship markup, must parse.
        for name in archive.namelist():
            if name.endswith(('.xml', '.rels')):
                ET.fromstring(archive.read(name))
        rows, main = read_rows(archive)
        assert len(rows) >= len(expected)+1
        actual = {cells['A']: cells for n, cells in rows.items() if n > 6 and cells.get('A')}
        assert len(actual) == len(expected)
        for row in expected:
            assert actual[row['id']]['D'].lstrip("'") == displayed(row['text'])
        assert len(main.findall(f'{{{S}}}dataValidations/{{{S}}}dataValidation')) >= 2
        pane = main.find(f'{{{S}}}sheetViews/{{{S}}}sheetView/{{{S}}}pane')
        assert pane is not None and float(pane.get('xSplit', 0)) == 4 and float(pane.get('ySplit', 0)) == 6
        for i in range(1, 5):
            _, tree = read_rows(archive, i)
            assert not tree.findall(f'.//{{{S}}}c[@t="e"]'), 'Stored Excel error cells'
        assert len(main.findall(f'{{{S}}}hyperlinks/{{{S}}}hyperlink')) == 2*len(expected)
        merged = merge_reviews(data, rows)
        assert len(merged['records']) == len(expected)
        for link in json.loads((folder/'links.json').read_text(encoding='utf-8')):
            if 'target' in link:
                assert (folder/link['target']).is_file(), f'Missing target: {link["target"]}'
        for table in ('table1.xml', 'table2.xml', 'table3.xml', 'table4.xml'):
            root = ET.fromstring(archive.read('xl/tables/'+table))
            assert root.find(f'{{{S}}}autoFilter') is not None
    result = {'success': True, 'records': len(expected), 'candidateCount': data['counts']['candidates'],
              'screenshots': len(data['screenshots']), 'nativeExcelVerified': False,
              'checks': ['XML parse and ZIP CRC', 'ID and full original text round-trip', '4 filter tables',
                         'frozen original-text columns and header', 'review dropdowns', 'native hyperlinks and target files',
                         'formula error-cell scan', 'review reimport'],
              'xlsxSha256': hashlib.sha256(workbook.read_bytes()).hexdigest()}
    receipt = folder/'validation.json'
    if receipt.exists():
        raise FileExistsError(receipt)
    receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False))


class RoundTripTests(unittest.TestCase):
    def test_reviews_follow_id_after_sorting(self):
        data = {'records': [{'id': 'a', 'text': '原文'}, {'id': 'b', 'text': '另文'}]}
        rows = {6: {'A': '文本 ID', 'J': '措辞结论'},
                7: {'A': 'b', 'D': '另文', 'H': '待核实', 'I': '已核实', 'J': '需修改', 'K': '=literal suggestion'},
                8: {'A': 'a', 'D': '原文', 'H': '已核实', 'I': '待核实', 'J': '通过', 'L': '保留备注'}}
        result = merge_reviews(data, rows)
        self.assertEqual(result['records'][0]['note'], '保留备注')
        self.assertEqual(result['records'][1]['suggestion'], '=literal suggestion')
        self.assertEqual(data['records'][0], {'id': 'a', 'text': '原文'})
        rows[8]['A'] = 'b'
        with self.assertRaises(ValueError):
            merge_reviews(data, rows)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest='command', required=True)
    final = sub.add_parser('finalize')
    final.add_argument('folder', type=Path)
    previous = sub.add_parser('reviews')
    previous.add_argument('workbook', type=Path)
    previous.add_argument('data', type=Path)
    previous.add_argument('output', type=Path)
    sub.add_parser('self-test')
    args = parser.parse_args()
    if args.command == 'self-test':
        unittest.main(argv=['workbook_io.py'])
    elif args.command == 'reviews':
        with zipfile.ZipFile(args.workbook) as archive:
            rows, _ = read_rows(archive)
        data = json.loads(args.data.read_text(encoding='utf-8'))
        result = merge_reviews(data, rows)
        with args.output.open('x', encoding='utf-8') as stream:
            json.dump(result, stream, ensure_ascii=False, indent=2)
        print(json.dumps({'reviews': len(result['records']), 'output': str(args.output)}))
    else:
        folder = args.folder.resolve()
        add_links(folder/'界面文本审计.xlsx', json.loads((folder/'links.json').read_text(encoding='utf-8')))
        validate(folder)
