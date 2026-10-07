"""Read Android ARSC/AXML without executing APK code or third party parsers."""
from __future__ import annotations

import struct
import xml.etree.ElementTree as ET
from collections import defaultdict


def u16(data: bytes, offset: int) -> int:
    return struct.unpack_from("<H", data, offset)[0]


def u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def string_pool(data: bytes, offset: int) -> list[str]:
    header = u16(data, offset + 2)
    count = u32(data, offset + 8)
    utf8 = bool(u32(data, offset + 16) & 0x100)
    start = offset + u32(data, offset + 20)

    def length(pos: int) -> tuple[int, int]:
        value = data[pos] if utf8 else u16(data, pos)
        pos += 1 if utf8 else 2
        flag = 0x80 if utf8 else 0x8000
        if value & flag:
            value = ((value & (flag - 1)) << (8 if utf8 else 16)) | (
                data[pos] if utf8 else u16(data, pos)
            )
            pos += 1 if utf8 else 2
        return value, pos

    result = []
    for i in range(count):
        pos = start + u32(data, offset + header + i * 4)
        size, pos = length(pos)
        if utf8:
            size, pos = length(pos)
        result.append(data[pos:pos + size * (1 if utf8 else 2)].decode(
            "utf-8" if utf8 else "utf-16le", errors="replace"))
    return result


def qualifiers(config: bytes) -> dict:
    size = u32(config, 0)
    density = u16(config, 14) if size >= 16 else 0
    night = (config[29] & 0x30) if size >= 32 else 0
    language = config[8:10].replace(b"\x00", b"").decode("ascii", "replace")
    country = config[10:12].replace(b"\x00", b"").decode("ascii", "replace")
    parts = []
    if language:
        parts.append(language + ("-r" + country if country else ""))
    if night:
        parts.append("night" if night == 0x20 else "notnight")
    names = {120: "ldpi", 160: "mdpi", 213: "tvdpi", 240: "hdpi",
             320: "xhdpi", 480: "xxhdpi", 640: "xxxhdpi",
             65534: "anydpi", 65535: "nodpi"}
    if density:
        parts.append(names.get(density, str(density) + "dpi"))
    return {"qualifier": "-".join(parts), "density": density,
            "night": None if not night else night == 0x20,
            "language": language or None, "country": country or None,
            "config_hex": config.hex()}


def resource_table(data: bytes) -> dict[str, list[dict]]:
    """Map physical ZIP entry to every direct string-valued resource origin."""
    result = defaultdict(list)
    global_strings = []
    pos = u16(data, 2)
    while pos < len(data):
        typ, chunk_size = u16(data, pos), u32(data, pos + 4)
        if chunk_size < 8:
            raise ValueError("Invalid ARSC chunk")
        if typ == 1:
            global_strings = string_pool(data, pos)
        elif typ == 0x200:
            package_id = u32(data, pos + 8)
            package = data[pos + 12:pos + 268].decode("utf-16le").split("\x00")[0]
            types = string_pool(data, pos + u32(data, pos + 268))
            keys = string_pool(data, pos + u32(data, pos + 276))
            offset = u32(data, pos + 284) if u16(data, pos + 2) >= 288 else 0
            cur = pos + u16(data, pos + 2)
            while cur < pos + chunk_size:
                child_type, child_size = u16(data, cur), u32(data, cur + 4)
                if child_size < 8:
                    raise ValueError("Invalid package child chunk")
                if child_type == 0x201:
                    header, type_id, flags = u16(data, cur + 2), data[cur + 8], data[cur + 9]
                    count, entries = u32(data, cur + 12), cur + u32(data, cur + 16)
                    config_size = u32(data, cur + 20)
                    qs = qualifiers(data[cur + 20:cur + 20 + config_size])
                    for i in range(count):
                        if flags & 1:
                            index, entry_offset = u16(data, cur + header + 4 * i), 4 * u16(data, cur + header + 4 * i + 2)
                        elif flags & 2:
                            index, compact = i, u16(data, cur + header + 2 * i)
                            if compact == 0xFFFF:
                                continue
                            entry_offset = compact * 4
                        else:
                            index, entry_offset = i, u32(data, cur + header + 4 * i)
                        if entry_offset == 0xFFFFFFFF:
                            continue
                        entry = entries + entry_offset
                        entry_size, entry_flags, key = u16(data, entry), u16(data, entry + 2), u32(data, entry + 4)
                        if entry_flags & 1:
                            continue
                        value_type = data[entry + entry_size + 3]
                        value = u32(data, entry + entry_size + 4)
                        if value_type == 3:
                            path = global_strings[value]
                            rid = (package_id << 24) | ((type_id + offset) << 16) | index
                            result[path].append({"package": package, "android_name": keys[key],
                                "android_type": types[type_id - 1], "resource_id": f"0x{rid:08X}",
                                "qualifiers": qs})
                cur += child_size
        pos += chunk_size
    return dict(result)


def typed_value(typ: int, value: int, strings: list[str]) -> str:
    if typ == 3:
        return strings[value]
    if typ == 1:
        return f"@0x{value:08X}"
    if typ == 2:
        return f"?0x{value:08X}"
    if typ == 0x12:
        return "true" if value else "false"
    if typ == 0x10:
        return str(value if value < 0x80000000 else value - 0x100000000)
    if typ == 0x11:
        return f"0x{value:08X}"
    if 0x1C <= typ <= 0x1F:
        return f"#{value:08X}"
    if typ == 4:
        return str(struct.unpack("<f", struct.pack("<I", value))[0])
    if typ in (5, 6):
        mantissa = value & 0xFFFFFF00
        if mantissa & 0x80000000:
            mantissa -= 0x100000000
        number = mantissa * (1 / 256, 1 / 32768, 1 / 8388608, 1 / 2147483648)[(value >> 4) & 3]
        if typ == 5:
            return f"{number:g}" + ("px", "dp", "sp", "pt", "in", "mm")[value & 15]
        return f"{number * 100:g}" + ("%", "%p")[value & 15]
    return str(value)


def decode_xml(data: bytes) -> tuple[str, bytes]:
    """Return root tag and equivalent readable XML; keep binary as authoritative."""
    if data.lstrip().startswith(b"<"):
        root = ET.fromstring(data)
        return root.tag.split("}")[-1], data
    if u16(data, 0) != 3:
        raise ValueError("Not Android binary XML")
    strings, stack, root = [], [], None
    pos = u16(data, 2)
    while pos < len(data):
        typ, size = u16(data, pos), u32(data, pos + 4)
        if size < 8:
            raise ValueError("Invalid AXML chunk")
        if typ == 1:
            strings = string_pool(data, pos)
        elif typ == 0x100:
            prefix, uri = u32(data, pos + 16), u32(data, pos + 20)
            try:
                ET.register_namespace("" if prefix == 0xFFFFFFFF else strings[prefix], strings[uri])
            except ValueError:
                pass
        elif typ == 0x102:
            namespace, name = u32(data, pos + 16), u32(data, pos + 20)
            tag = strings[name] if namespace == 0xFFFFFFFF else "{" + strings[namespace] + "}" + strings[name]
            element = ET.Element(tag)
            attr_start, attr_size, count = u16(data, pos + 24), u16(data, pos + 26), u16(data, pos + 28)
            for i in range(count):
                a = pos + 16 + attr_start + i * attr_size
                ns, key, raw = u32(data, a), u32(data, a + 4), u32(data, a + 8)
                attr = strings[key] if ns == 0xFFFFFFFF else "{" + strings[ns] + "}" + strings[key]
                element.set(attr, strings[raw] if raw != 0xFFFFFFFF else typed_value(data[a + 15], u32(data, a + 16), strings))
            if stack:
                stack[-1].append(element)
            else:
                root = element
            stack.append(element)
        elif typ == 0x103:
            stack.pop()
        elif typ == 0x104 and stack:
            text = strings[u32(data, pos + 16)]
            stack[-1].text = (stack[-1].text or "") + text
        pos += size
    if root is None:
        raise ValueError("AXML has no root")
    ET.indent(root, space="  ")
    return root.tag.split("}")[-1], ET.tostring(root, encoding="utf-8", xml_declaration=True)


def apk_manifest(data: bytes) -> dict:
    _, xml = decode_xml(data)
    root = ET.fromstring(xml)
    ns = "{http://schemas.android.com/apk/res/android}"
    return {"package": root.attrib["package"], "version_name": root.get(ns + "versionName"),
            "version_code": root.get(ns + "versionCode")}
