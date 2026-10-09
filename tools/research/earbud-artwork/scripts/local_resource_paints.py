"""Resolve local Android resource paints for offline SVG research previews.

Call configure(research_root, assets), then resolve_paint(value, origin,
prefix="unique-id"). No output files, network access, or APK execution are used.
Extracted XML is preferred; APK resource tables supply scalar colors and aliases.
Unresolved theme attributes and unsupported paint types return paint=None.
"""
from __future__ import annotations

from collections import defaultdict
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET
import zipfile

from android_resources import decode_xml, qualifiers, string_pool, typed_value, u16, u32

SVG = "http://www.w3.org/2000/svg"


def resource_values(data: bytes, wanted_types=None) -> list[dict]:
    """Read scalar ARSC values, including reference aliases omitted by file maps."""
    result = []
    strings = []
    pos = u16(data, 2)
    while pos < len(data):
        typ, size = u16(data, pos), u32(data, pos + 4)
        if size < 8:
            raise ValueError("Invalid ARSC chunk")
        if typ == 1:
            strings = string_pool(data, pos)
        elif typ == 0x200:
            package_id = u32(data, pos + 8)
            package = data[pos + 12:pos + 268].decode("utf-16le").split("\0")[0]
            types = string_pool(data, pos + u32(data, pos + 268))
            keys = string_pool(data, pos + u32(data, pos + 276))
            type_offset = u32(data, pos + 284) if u16(data, pos + 2) >= 288 else 0
            cur = pos + u16(data, pos + 2)
            while cur < pos + size:
                child_type, child_size = u16(data, cur), u32(data, cur + 4)
                if child_size < 8:
                    raise ValueError("Invalid package child chunk")
                if child_type == 0x201:
                    header, tid, flags = u16(data, cur + 2), data[cur + 8], data[cur + 9]
                    type_name = types[tid - 1]
                    if wanted_types is not None and type_name not in wanted_types:
                        cur += child_size
                        continue
                    count, entries = u32(data, cur + 12), cur + u32(data, cur + 16)
                    config_size = u32(data, cur + 20)
                    config = data[cur + 20:cur + 20 + config_size]
                    qs = qualifiers(config)
                    qs["sdk_version"] = u16(config, 24) if config_size >= 28 else 0
                    for i in range(count):
                        if flags & 1:
                            index = u16(data, cur + header + i * 4)
                            off = 4 * u16(data, cur + header + i * 4 + 2)
                        elif flags & 2:
                            index, compact = i, u16(data, cur + header + i * 2)
                            if compact == 0xFFFF:
                                continue
                            off = compact * 4
                        else:
                            index, off = i, u32(data, cur + header + i * 4)
                        if off == 0xFFFFFFFF:
                            continue
                        entry = entries + off
                        entry_size, entry_flags, key = u16(data, entry), u16(data, entry + 2), u32(data, entry + 4)
                        if entry_flags & 1:
                            continue  # Complex style/array bags are not scalar paints.
                        value_type = data[entry + entry_size + 3]
                        value_data = u32(data, entry + entry_size + 4)
                        rid = (package_id << 24) | ((tid + type_offset) << 16) | index
                        result.append({"package": package, "android_type": type_name,
                                       "android_name": keys[key], "resource_id": f"0x{rid:08X}",
                                       "qualifiers": dict(qs), "value_type": value_type,
                                       "value_data": value_data,
                                       "value": typed_value(value_type, value_data, strings)})
                cur += child_size
        pos += size
    return result


def _attrs(element):
    return {k.rsplit("}", 1)[-1]: v for k, v in element.attrib.items()}


def _tag(element):
    return element.tag.rsplit("}", 1)[-1]


def _literal(value):
    if value == "0" or value == "@android:color/transparent":
        return "#000000", 0.0
    if value == "@android:color/black":
        return "#000000", 1.0
    if value == "@android:color/white":
        return "#FFFFFF", 1.0
    if not isinstance(value, str) or not re.fullmatch(r"#[0-9a-fA-F]+", value):
        return None
    raw = value[1:]
    if len(raw) == 8:
        return "#" + raw[2:], int(raw[:2], 16) / 255
    if len(raw) == 4:
        return "#" + "".join(c * 2 for c in raw[1:]), int(raw[0] * 2, 16) / 255
    if len(raw) in (3, 6):
        return value, 1.0
    return None


def _key(value):
    value = value.lstrip("@").lower()
    if re.fullmatch(r"(?:0x)?[0-9a-f]{8}", value):
        return "0x" + value.removeprefix("0x")
    return value


def _variant(candidates, origin):
    requested = origin.get("qualifiers") or {}
    ranked = []
    for candidate in candidates:
        qs = candidate.get("qualifiers") or {}
        if qs.get("language") and qs.get("language") != requested.get("language"):
            continue
        if qs.get("country") and qs.get("country") != requested.get("country"):
            continue
        if qs.get("night") is True and requested.get("night") is not True:
            continue
        if qs.get("night") is False and requested.get("night") is True:
            continue
        score = (qs.get("night") == requested.get("night"),
                 qs.get("density", 0) == requested.get("density", 0),
                 not qs.get("language"), -qs.get("sdk_version", 0))
        ranked.append((score, candidate))
    return max(ranked, key=lambda pair: pair[0])[1] if ranked else None


class PaintResolver:
    def __init__(self, root: Path, assets: list[dict]):
        self.root = Path(root).resolve()
        self.index = defaultdict(list)
        self.apks = {}
        self.xml_cache = {}
        self.arsc_loaded = False
        for asset in assets:
            for origin in asset.get("origins", []):
                if origin.get("remote_archive"):
                    continue
                item = {**origin, "asset": asset}
                for name in (origin.get("resource_id"),
                             (origin.get("android_type") or "drawable") + "/" + (origin.get("android_name") or "")):
                    if name:
                        self.index[(origin.get("package"), _key(name))].append(item)

    def _inside(self, relative):
        path = (self.root / relative).resolve()
        if not path.is_relative_to(self.root):
            raise ValueError("Extracted paint path leaves the research directory")
        return path

    def _load_arsc(self):
        if self.arsc_loaded:
            return
        source_path = self.root / "sources.json"
        document = json.loads(source_path.read_text(encoding="utf-8"))
        for source in document["sources"]:
            from research_runtime import research_path
            path = research_path(source["source_path"])
            if not path.is_relative_to(self.root.parent / "apps") or path.suffix.lower() != ".apk":
                raise ValueError("Paint sources must be APKs inside research/apps/")
            h = hashlib.sha256()
            with path.open("rb") as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    h.update(block)
            if h.hexdigest() != source["apk_sha256"]:
                raise ValueError("Paint source APK hash changed")
            package = source["package"]
            self.apks[package] = path
            with zipfile.ZipFile(path) as archive:
                values = resource_values(archive.read("resources.arsc"), {"color", "drawable", "mipmap"})
            for item in values:
                for name in (item["resource_id"], item["android_type"] + "/" + item["android_name"]):
                    self.index[(package, _key(name))].append(item)
        self.arsc_loaded = True

    def _lookup(self, value, origin):
        key = (origin.get("package"), _key(value))
        # Loading scalars also adds variants omitted from the selected manifest.
        self._load_arsc()
        return _variant(self.index.get(key, []), origin)

    def _xml(self, item):
        asset = item.get("asset")
        if asset:
            path = self._inside(asset.get("decoded_path") or asset["original_path"])
            cache_key = str(path)
            if cache_key not in self.xml_cache:
                raw = path.read_bytes()
                self.xml_cache[cache_key] = self._decode(raw)
            return self.xml_cache[cache_key]
        entry = item["value"]
        cache_key = (item["package"], entry)
        if cache_key not in self.xml_cache:
            if not entry.startswith("res/") or not entry.endswith(".xml"):
                return None
            with zipfile.ZipFile(self.apks[item["package"]]) as archive:
                self.xml_cache[cache_key] = self._decode(archive.read(entry))
        return self.xml_cache[cache_key]

    @staticmethod
    def _decode(raw):
        if b"<!DOCTYPE" in raw.upper() or b"<!ENTITY" in raw.upper():
            raise ValueError("External XML entity definitions are not accepted")
        return ET.fromstring(decode_xml(raw)[1])

    def resolve(self, value, origin, *, prefix="paint"):
        definitions, notes = [], []
        safe_id = re.sub(r"[^A-Za-z0-9_.-]", "_", prefix)
        if not safe_id or not safe_id[0].isalpha():
            safe_id = "paint_" + safe_id
        try:
            paint, opacity = self._resolve(str(value), origin, safe_id, set(), definitions, notes)
        except (ValueError, KeyError, OSError, ET.ParseError) as error:
            paint, opacity = None, 1.0
            notes.append("本地 Android 颜色未解析：" + str(error))
        return {"paint": paint, "opacity": opacity,
                "defs_svg": "".join(ET.tostring(d, encoding="unicode") for d in definitions),
                "notes": list(dict.fromkeys(notes))}

    def _resolve(self, value, origin, prefix, seen, definitions, notes):
        literal = _literal(value)
        if literal:
            return literal
        if value.startswith("?"):
            notes.append("Android 主题属性未确定；静态预览不推测主题颜色：" + value)
            return None, 1.0
        if not value.startswith("@"):
            notes.append("不是已识别的 Android paint：" + value)
            return None, 1.0
        key = (origin.get("package"), _key(value))
        if key in seen:
            notes.append("Android paint 引用循环：" + value)
            return None, 1.0
        seen = seen | {key}
        item = self._lookup(value, origin)
        if not item:
            notes.append("本地资源表中未找到 paint：" + value)
            return None, 1.0
        if "asset" not in item and item.get("value_type") != 3:
            return self._resolve(item["value"], origin, prefix, seen, definitions, notes)
        element = self._xml(item)
        if element is None:
            notes.append("引用资源不是可用的颜色 XML：" + value)
            return None, 1.0
        return self._xml_paint(element, origin, prefix, seen, definitions, notes)

    def _xml_paint(self, element, origin, prefix, seen, definitions, notes):
        kind, values = _tag(element), _attrs(element)
        if kind == "selector":
            items = [e for e in element if _tag(e) == "item"]
            unconditional = [e for e in items if not any(k.startswith("state_") for k in _attrs(e))]
            if not items:
                return None, 1.0
            chosen = unconditional[0] if unconditional else items[0]
            if not unconditional:
                notes.append("颜色 selector 无无条件分支；静态采用首项，未还原交互状态。")
            attrs = _attrs(chosen)
            if attrs.get("color"):
                paint, opacity = self._resolve(attrs["color"], origin, prefix, seen, definitions, notes)
                return paint, opacity * float(attrs.get("alpha", 1))
            if attrs.get("drawable"):
                return self._resolve(attrs["drawable"], origin, prefix, seen, definitions, notes)
            if len(chosen):
                return self._xml_paint(chosen[0], origin, prefix, seen, definitions, notes)
        if kind == "shape":
            for child in element:
                if _tag(child) == "gradient":
                    return self._xml_paint(child, origin, prefix, seen, definitions, notes)
                if _tag(child) == "solid":
                    return self._resolve(_attrs(child).get("color", ""), origin, prefix, seen, definitions, notes)
        if kind != "gradient":
            notes.append("该 drawable 不能作为 SVG paint：" + kind)
            return None, 1.0
        gradient_type = values.get("type", "0")
        if gradient_type in {"sweep", "2"}:
            notes.append("SVG 静态预览不支持 Android sweep 渐变，未猜测替代渐变。")
            return None, 1.0
        if gradient_type not in {"radial", "1"} and "angle" in values and not any(
                key in values for key in ("startX", "startY", "endX", "endY")):
            notes.append("shape 线性渐变需要绘制边界来还原 angle；未推测坐标。")
            return None, 1.0
        gid = prefix + "_" + str(len(definitions) + 1)
        spread = {"0": "pad", "clamp": "pad", "1": "repeat", "repeat": "repeat",
                  "2": "reflect", "mirror": "reflect"}.get(values.get("tileMode", "0"), "pad")
        attrs = {"id": gid, "gradientUnits": "userSpaceOnUse", "spreadMethod": spread}
        if gradient_type in {"radial", "1"}:
            radius = float(values.get("gradientRadius", 0))
            if radius <= 0:
                raise ValueError("Radial gradient has no positive radius")
            attrs.update(cx=values.get("centerX", "0"), cy=values.get("centerY", "0"), r=f"{radius:g}")
            gradient = ET.Element(f"{{{SVG}}}radialGradient", attrs)
        else:
            attrs.update(x1=values.get("startX", "0"), y1=values.get("startY", "0"),
                         x2=values.get("endX", "0"), y2=values.get("endY", "0"))
            gradient = ET.Element(f"{{{SVG}}}linearGradient", attrs)
        stops = [(_attrs(e).get("offset", "0"), _attrs(e).get("color", ""))
                 for e in element if _tag(e) == "item"]
        if not stops:
            stops = [("0", values.get("startColor", "")), ("1", values.get("endColor", ""))]
            if values.get("centerColor"):
                stops.insert(1, ("0.5", values["centerColor"]))
        for offset, value in stops:
            paint, opacity = self._resolve(value, origin, prefix + "_stop", seen, definitions, notes)
            if paint is None or paint.startswith("url("):
                notes.append("渐变 stop 颜色未解析，未输出猜测色渐变。")
                return None, 1.0
            ET.SubElement(gradient, f"{{{SVG}}}stop", {"offset": offset,
                          "stop-color": paint, "stop-opacity": f"{opacity:g}"})
        definitions.append(gradient)
        return "url(#" + gid + ")", 1.0


_resolver = None


def configure(root, assets):
    global _resolver
    _resolver = PaintResolver(Path(root), assets)
    return _resolver


def resolve_paint(value, origin, *, prefix="paint"):
    if _resolver is None:
        return {"paint": None, "opacity": 1.0, "defs_svg": "",
                "notes": ["本地 paint resolver 尚未 configure。"]}
    return _resolver.resolve(value, origin, prefix=prefix)
