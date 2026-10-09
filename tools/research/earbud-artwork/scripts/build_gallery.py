#!/usr/bin/env python3
"""Build the self-contained, offline artwork gallery from assets.json.

Requires Pillow. SVG/Android-vector raster previews additionally use a locally
installed ImageMagick (magick); no downloads or APK execution are performed.
Run with Python -B to avoid writing interpreter caches. On this research host,
the existing uv Pillow cache can be selected with EARBUD_PIL_PATH.

All output is restricted to the directory containing assets.json. Input files
are retained, and assets.json is updated atomically only for derived previews.
"""

from __future__ import annotations

import argparse
import collections
import html
import io
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from research_runtime import research_path


def load_pillow():
    try:
        from PIL import Image, ImageDraw, ImageFont
    except ImportError:
        requested = os.environ.get("EARBUD_PIL_PATH")
        candidates = [Path(requested)] if requested else []
        cache = Path(os.environ.get("LOCALAPPDATA", "")) / "uv/cache/archive-v0"
        candidates.extend(p.parent for p in cache.glob("*/PIL"))
        for directory in candidates:
            sys.path.insert(0, str(directory))
            try:
                from PIL import Image, ImageDraw, ImageFont
                break
            except (ImportError, OSError):
                sys.path.pop(0)
        else:
            raise SystemExit("需要本地 Pillow；设置 EARBUD_PIL_PATH 指向已有安装即可。")
    return Image, ImageDraw, ImageFont


Image, ImageDraw, ImageFont = load_pillow()
ANDROID = "{http://schemas.android.com/apk/res/android}"
SVG = "http://www.w3.org/2000/svg"
ET.register_namespace("", SVG)

CATEGORIES = {
    "device_appearance": "型号专用整体外观",
    "case": "型号专用充电盒",
    "capsule": "型号专用连接卡片",
    "earpiece": "型号专用单耳外观",
    "config": "型号资源包配置",
    "device_default": "通用双耳与单耳外观",
    "device_ows": "开放式与耳夹外观",
    "device_neck": "颈挂外观",
    "pairing": "配对与开盒示意",
    "control": "操控与手势引导",
    "hearing": "听力与佩戴检测",
    "translation": "翻译功能插图",
    "account_link": "跨设备连接示意",
    "scanning": "设备扫描示意",
    "diagnosis": "诊断示意",
    "widget": "桌面组件图标",
    "resource_config": "外观资源配置",
    "video_shader": "透明视频着色器",
    "unknown_binary": "内容待确认",
    "feature_audio_quality": "功能美术 · 音质与 Hi-Res",
    "feature_noise_control": "功能美术 · 降噪与通透",
    "feature_equalizer": "功能美术 · 均衡器",
    "feature_spatial": "功能美术 · 空间音频与杜比",
    "feature_gaming": "功能美术 · 游戏模式",
    "feature_controls": "功能美术 · 操控与手势",
    "feature_battery": "功能美术 · 电量与充电",
    "feature_connection": "功能美术 · 连接与查找",
    "feature_general": "功能美术 · 其他功能",
}
KINDS = {
    "bitmap": "位图", "embedded_bitmap": "内嵌位图", "svg": "SVG",
    "android_vector": "Android 矢量", "android_animated_vector": "Android 动画矢量",
    "lottie": "Lottie 动画", "config": "配置", "shader": "着色器",
    "upk": "组件资源包", "unknown_binary": "未知二进制",
    "android_drawable": "Android 组合绘制配置",
    "android_color": "Android 颜色与渐变配置",
}
FEATURES = {
    "hi_res": "Hi-Res / 高解析音频",
    "audio_quality": "高音质 / 无损音频",
    "noise_cancellation": "降噪控制",
    "adaptive_noise": "自适应降噪",
    "transparency": "通透模式",
    "voice_listening": "人声 / 聆听",
    "noise_off": "关闭降噪",
    "equalizer": "均衡器 / EQ",
    "spatial_audio": "空间音频",
    "dolby": "杜比音效 / Dolby",
    "game_mode": "游戏模式",
    "gestures": "操控与手势",
    "battery": "电量与充电",
    "connection": "设备连接",
    "find_device": "查找耳机",
    "hearing": "听力 / 佩戴",
    "ai_translation": "AI 摘要 / 翻译",
    "health": "健康 / 睡眠",
    "relax_sounds": "白噪音 / 放松音效",
    "other_features": "其他功能",
}
FEATURE_ALIASES = {
    "hi_res": ["hi-res", "hi res", "hires", "high resolution", "高解析", "高清音频"],
    "audio_quality": ["高音质", "无损", "lossless", "audio quality"],
    "noise_cancellation": ["降噪", "降噪控制", "主动降噪", "anc", "noise cancellation", "noise control"],
    "adaptive_noise": ["自适应", "自适应降噪", "adaptive noise", "adaptive anc"],
    "transparency": ["通透", "透传", "环境声", "transparency", "ambient"],
    "voice_listening": ["人声", "聆听", "voice", "listening"],
    "noise_off": ["关闭降噪", "降噪关闭", "noise off"],
    "equalizer": ["均衡器", "eq", "equalizer", "equaliser"],
    "spatial_audio": ["空间音频", "空间音效", "spatial audio"],
    "dolby": ["杜比", "dolby", "atmos"],
    "game_mode": ["游戏", "低延迟", "game mode", "gaming"],
    "gestures": ["手势", "触控", "操控", "gesture", "touch control"],
    "battery": ["电量", "电池", "充电", "battery", "charging"],
    "connection": ["连接", "蓝牙", "多设备", "connection", "bluetooth"],
    "find_device": ["查找", "寻找耳机", "find device", "find earbuds"],
    "hearing": ["听力", "佩戴", "hearing", "wearing"],
    "ai_translation": ["ai", "摘要", "翻译", "translation", "summary"],
    "health": ["健康", "睡眠", "health", "sleep"],
    "relax_sounds": ["白噪音", "放松", "white noise", "relax"],
    "other_features": ["其他功能"],
}
STATUS = {"generic": "通用资源，未绑定型号", "unknown": "型号未确认", "confirmed": "型号已确认"}
SVG_RENDERER = "rsvg-direct-480-v3-local-paints"


def inside(root: Path, relative: str) -> Path:
    path = (root / relative).resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError(f"索引路径越界：{relative}")
    return path


def atomic_text(path: Path, content: str):
    path.parent.mkdir(parents=True, exist_ok=True)
    stage = path.with_name(path.name + ".tmp")
    stage.write_text(content, encoding="utf-8", newline="\n")
    stage.replace(path)


def attrs(element):
    return {key.rsplit("}", 1)[-1]: value for key, value in element.attrib.items()}


def tag(element):
    return element.tag.rsplit("}", 1)[-1]


def number(value, default=0):
    if value is None:
        return default
    match = re.match(r"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?", str(value))
    return float(match.group()) if match else default


def color(value, fallback="#65778d"):
    """Android eight-digit literals are AARRGGBB, unlike SVG's RRGGBBAA."""
    if not value:
        return fallback, 1, False
    if value == "@android:color/transparent":
        return "#000000", 0, False
    if value.startswith("#"):
        raw = value[1:]
        if len(raw) == 8:
            return "#" + raw[2:], int(raw[:2], 16) / 255, False
        if len(raw) == 4:
            return "#" + "".join(x * 2 for x in raw[1:]), int(raw[0] * 2, 16) / 255, False
        if len(raw) in (3, 6):
            return value, 1, False
    if value == "0":
        return "#000000", 0, False
    return fallback, 1, True


def parse_xml(path):
    content = path.read_bytes()
    if b"<!DOCTYPE" in content.upper() or b"<!ENTITY" in content.upper():
        raise ValueError("不处理包含外部实体定义的 XML")
    return ET.fromstring(content)


def validate_svg(element):
    for child in element.iter():
        if tag(child) in {"script", "foreignObject"}:
            raise ValueError("SVG 含不适合离线静态预览的元素")
        if child.text and ("@import" in child.text or re.search(r"url\s*\(\s*['\"]?(?:https?:|file:|//)", child.text, re.I)):
            raise ValueError("SVG 样式引用外部资源，跳过栅格预览")
        for name, value in child.attrib.items():
            if name.rsplit("}", 1)[-1] == "href" and not value.startswith(("#", "data:image/")):
                raise ValueError("SVG 引用外部资源，跳过栅格预览")
            if re.search(r"url\s*\(\s*['\"]?(?:https?:|file:|//)", value, re.I):
                raise ValueError("SVG 引用外部资源，跳过栅格预览")


def vector_to_svg(element, paint_resolver=None):
    root_attrs = attrs(element)
    vw = number(root_attrs.get("viewportWidth"), number(root_attrs.get("width"), 24))
    vh = number(root_attrs.get("viewportHeight"), number(root_attrs.get("height"), 24))
    if vw <= 0 or vh <= 0:
        raise ValueError("矢量视口尺寸无效")
    root = ET.Element(f"{{{SVG}}}svg", {
        "viewBox": f"0 0 {vw:g} {vh:g}", "width": f"{vw:g}", "height": f"{vh:g}",
    })
    defs = ET.SubElement(root, f"{{{SVG}}}defs")
    warnings = set()
    sequence = [0]
    top = ET.SubElement(root, f"{{{SVG}}}g", {"opacity": str(number(root_attrs.get("alpha"), 1))})
    if root_attrs.get("tint"):
        warnings.add("矢量含 Android tint；静态预览保留路径原色，未应用主题 tint。")

    def resolve_color(value):
        c, alpha, unresolved = color(value)
        if unresolved and paint_resolver:
            sequence[0] += 1
            resolved = paint_resolver(value, prefix="paint" + str(sequence[0])) or {}
            for note in resolved.get("notes", []):
                warnings.add(note)
            if resolved.get("paint"):
                definition = resolved.get("defs_svg")
                if definition:
                    container = ET.fromstring("<defs xmlns=\"" + SVG + "\">" + definition + "</defs>")
                    validate_svg(container)
                    for child in container:
                        if tag(child) == "defs":
                            defs.extend(list(child))
                        else:
                            defs.append(child)
                return resolved["paint"], number(resolved.get("opacity"), 1), False
        return c, alpha, unresolved

    def gradient(path, name):
        for child in path:
            if tag(child) == "attr" and child.attrib.get("name", "").endswith(name):
                for specification in child:
                    if tag(specification) != "gradient":
                        continue
                    values = attrs(specification)
                    sequence[0] += 1
                    gid = "gradient" + str(sequence[0])
                    radial = values.get("type") in {"radial", "1"}
                    if values.get("type") in {"sweep", "2"}:
                        warnings.add("静态预览不支持 Android sweep 渐变；使用中性占位色。")
                        return None
                    if radial:
                        g = ET.SubElement(defs, f"{{{SVG}}}radialGradient", {
                            "id": gid, "gradientUnits": "userSpaceOnUse",
                            "cx": str(number(values.get("centerX"))),
                            "cy": str(number(values.get("centerY"))),
                            "r": str(number(values.get("gradientRadius"), 1)),
                        })
                    else:
                        g = ET.SubElement(defs, f"{{{SVG}}}linearGradient", {
                            "id": gid, "gradientUnits": "userSpaceOnUse",
                            "x1": str(number(values.get("startX"))), "y1": str(number(values.get("startY"))),
                            "x2": str(number(values.get("endX"))), "y2": str(number(values.get("endY"))),
                        })
                    stops = [(number(attrs(item).get("offset")), attrs(item).get("color"))
                             for item in specification if tag(item) == "item"]
                    if not stops:
                        stops = [(0, values.get("startColor")), (1, values.get("endColor"))]
                        if values.get("centerColor"):
                            stops.insert(1, (0.5, values["centerColor"]))
                    for offset, value in stops:
                        c, alpha, unresolved = resolve_color(value)
                        if unresolved:
                            warnings.add("Android 主题/资源颜色未解析，静态预览使用中性占位色。")
                        ET.SubElement(g, f"{{{SVG}}}stop", {
                            "offset": str(offset), "stop-color": c, "stop-opacity": str(alpha),
                        })
                    return "url(#" + gid + ")"
        return None

    def convert(source, destination):
        current = destination
        for child in source:
            values = attrs(child)
            kind = tag(child)
            if kind == "group":
                px, py = number(values.get("pivotX")), number(values.get("pivotY"))
                tx, ty = number(values.get("translateX")), number(values.get("translateY"))
                rotation = number(values.get("rotation"))
                sx, sy = number(values.get("scaleX"), 1), number(values.get("scaleY"), 1)
                group = ET.SubElement(current, f"{{{SVG}}}g", {
                    "transform": f"translate({tx+px:g} {ty+py:g}) rotate({rotation:g}) scale({sx:g} {sy:g}) translate({-px:g} {-py:g})",
                })
                convert(child, group)
            elif kind in {"clip-path", "clipPath"}:
                sequence[0] += 1
                cid = "clip" + str(sequence[0])
                clip = ET.SubElement(defs, f"{{{SVG}}}clipPath", {"id": cid})
                ET.SubElement(clip, f"{{{SVG}}}path", {"d": values.get("pathData", "")})
                current = ET.SubElement(current, f"{{{SVG}}}g", {"clip-path": "url(#" + cid + ")"})
            elif kind == "path":
                target = {"d": values.get("pathData", ""), "fill": "none"}
                for android_name, svg_name in [("fillColor", "fill"), ("strokeColor", "stroke")]:
                    paint = gradient(child, android_name)
                    if paint:
                        target[svg_name] = paint
                        target[svg_name + "-opacity"] = str(number(values.get(svg_name + "Alpha"), 1))
                    elif android_name in values:
                        c, alpha, unresolved = resolve_color(values[android_name])
                        target[svg_name] = c
                        if unresolved:
                            warnings.add("Android 主题/资源颜色未解析，静态预览使用中性占位色。")
                        target[svg_name + "-opacity"] = str(alpha * number(values.get(svg_name + "Alpha"), 1))
                for android_name, svg_name in [("strokeWidth", "stroke-width"), ("strokeMiterLimit", "stroke-miterlimit")]:
                    if android_name in values:
                        target[svg_name] = str(number(values[android_name]))
                caps = {"0": "butt", "1": "round", "2": "square"}
                joins = {"0": "miter", "1": "round", "2": "bevel"}
                if "strokeLineCap" in values:
                    target["stroke-linecap"] = caps.get(values["strokeLineCap"], values["strokeLineCap"])
                if "strokeLineJoin" in values:
                    target["stroke-linejoin"] = joins.get(values["strokeLineJoin"], values["strokeLineJoin"])
                target["fill-rule"] = "evenodd" if values.get("fillType") in {"evenOdd", "1"} else "nonzero"
                if any(key.startswith("trimPath") for key in values):
                    warnings.add("矢量含 trimPath；预览显示完整路径，未还原裁剪进度。")
                ET.SubElement(current, f"{{{SVG}}}path", target)

    convert(element, top)
    return ET.tostring(root, encoding="unicode"), sorted(warnings)


def resource_lookup(assets):
    lookup = {}
    for asset in assets:
        for origin in asset.get("origins", []):
            package = origin.get("package", "")
            for key in (origin.get("resource_id"), origin.get("android_name")):
                if key:
                    lookup[(package, str(key).lower().lstrip("@"))] = asset
    return lookup


def static_vector(asset, root, lookup):
    source = inside(root, asset.get("decoded_path") or asset["original_path"])
    element = parse_xml(source)
    if tag(element) == "vector":
        return element, []
    if tag(element) != "animated-vector":
        raise ValueError("没有可读取的 vector XML")
    for child in element.iter():
        if tag(child) == "vector":
            return child, ["动画矢量仅展示基础矢量静帧，未播放动画或变形。"]
    reference = attrs(element).get("drawable", "").lower().lstrip("@")
    packages = {origin.get("package", "") for origin in asset.get("origins", [])}
    for package in packages:
        target = lookup.get((package, reference))
        if target and target.get("decoded_path"):
            element = parse_xml(inside(root, target["decoded_path"]))
            if tag(element) == "vector":
                return element, ["动画矢量仅展示基础矢量静帧，未播放动画或变形。"]
    raise ValueError("动画矢量的基础 drawable 尚未解析")


def render_previews(root, assets):
    from local_resource_paints import configure, resolve_paint
    configure(root, assets)
    magick = shutil.which("magick")
    log = []
    lookup = resource_lookup(assets)
    for asset in assets:
        kind = asset.get("kind")
        if kind not in {"bitmap", "embedded_bitmap", "svg", "android_vector", "android_animated_vector"}:
            continue
        if (asset.get("preview_path") and inside(root, asset["preview_path"]).exists()
                and (kind in {"bitmap", "embedded_bitmap"} or asset.get("preview_renderer") == SVG_RENDERER)):
            continue
        identifier = re.sub(r"[^a-zA-Z0-9_-]", "_", str(asset.get("sha256") or asset["id"]))
        if kind in {"bitmap", "embedded_bitmap"}:
            directory = root / "previews/bitmap"
            directory.mkdir(parents=True, exist_ok=True)
            png_path = directory / (identifier + ".png")
            stage = png_path.with_name(png_path.stem + ".tmp.png")
            try:
                with Image.open(inside(root, asset["original_path"])) as original:
                    original.convert("RGBA").save(stage, "PNG")
                stage.replace(png_path)
                asset["preview_path"] = png_path.relative_to(root).as_posix()
                log.append({"id": asset["id"], "kind": kind, "status": "rendered", "notes": []})
            except Exception as error:
                log.append({"id": asset["id"], "kind": kind, "status": "unavailable", "reason": str(error)})
            continue
        directory = root / "previews/vector"
        directory.mkdir(parents=True, exist_ok=True)
        svg_path = directory / (identifier + ".svg")
        png_path = directory / (identifier + ".png")
        try:
            if kind == "svg":
                source = inside(root, asset["original_path"])
                element = parse_xml(source)
                validate_svg(element)
                svg_text = ET.tostring(element, encoding="unicode")
                warnings = []
            else:
                element, warnings = static_vector(asset, root, lookup)
                def paint_resolver(value, *, prefix):
                    failures = set()
                    for origin in asset.get("origins", []):
                        result = resolve_paint(value, origin, prefix=prefix)
                        if result.get("paint"):
                            return result
                        failures.update(result.get("notes", []))
                    return {"paint": None, "notes": sorted(failures)}
                svg_text, additional = vector_to_svg(element, paint_resolver)
                warnings.extend(additional)
            # Render the vectors at their final resolution. Rendering a 14dp
            # icon at 28 pixels and subsequently enlarging it blurs its edges.
            normalized = ET.fromstring(svg_text)
            viewbox = normalized.attrib.get("viewBox", "").replace(",", " ").split()
            if len(viewbox) == 4:
                width, height = float(viewbox[2]), float(viewbox[3])
            else:
                width = number(normalized.attrib.get("width"), 24)
                height = number(normalized.attrib.get("height"), 24)
                normalized.set("viewBox", f"0 0 {width:g} {height:g}")
            if width <= 0 or height <= 0:
                raise ValueError("SVG 视口尺寸无效")
            # RSVG/ImageMagick may truncate a 1px divider canvas to zero. Keep
            # a small canvas margin; SVG's default meet sizing retains aspect.
            normalized.set("width", str(max(2, round(width / max(width, height) * 480))))
            normalized.set("height", str(max(2, round(height / max(width, height) * 480))))
            svg_text = ET.tostring(normalized, encoding="unicode")
            atomic_text(svg_path, svg_text + "\n")
            asset["preview_svg_path"] = svg_path.relative_to(root).as_posix()
            if not magick:
                raise ValueError("本地 magick 不可用；SVG 预览已生成")
            stage = png_path.with_name(png_path.stem + ".tmp.png")
            result = subprocess.run([
                magick, "-background", "none", "-density", "72", str(svg_path),
                "-resize", "480x480", "PNG32:" + str(stage),
            ], capture_output=True, text=True, timeout=30)
            if result.returncode:
                raise ValueError(result.stderr.strip()[:500])
            with Image.open(stage) as check:
                check.verify()
            stage.replace(png_path)
            asset["preview_path"] = png_path.relative_to(root).as_posix()
            asset["preview_renderer"] = SVG_RENDERER
            old_fallback = "Android 主题/资源颜色未解析，静态预览使用中性占位色。"
            if old_fallback not in warnings:
                asset["notes"] = [note for note in asset.get("notes", []) if note != old_fallback]
            for note in warnings:
                if note not in asset.setdefault("notes", []):
                    asset["notes"].append(note)
            log.append({"id": asset["id"], "kind": kind, "status": "rendered", "notes": warnings})
        except Exception as error:
            log.append({"id": asset["id"], "kind": kind, "status": "unavailable", "reason": str(error)})
    return log


def font(size, bold=False):
    directory = Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts"
    for name in (["msyhbd.ttc", "msyh.ttc"] if bold else ["msyh.ttc", "simhei.ttf"]):
        path = directory / name
        if path.exists():
            return ImageFont.truetype(str(path), size)
    return ImageFont.load_default()


def wrapped_lines(draw, value, selected_font, max_width, max_lines=2):
    lines = []
    line = ""
    for character in str(value):
        trial = line + character
        if draw.textlength(trial, font=selected_font) > max_width and line:
            lines.append(line)
            line = character
        else:
            line = trial
    if line:
        lines.append(line)
    if len(lines) > max_lines:
        lines = lines[:max_lines]
        final = lines[-1]
        while final and draw.textlength(final + "…", font=selected_font) > max_width:
            final = final[:-1]
        lines[-1] = final + "…"
    return lines


def checkerboard(size, step=16):
    canvas = Image.new("RGBA", size, "#e8edf3")
    draw = ImageDraw.Draw(canvas)
    for y in range(0, size[1], step):
        for x in range(0, size[0], step):
            if (x // step + y // step) % 2:
                draw.rectangle((x, y, x+step-1, y+step-1), fill="#d6dfe9")
    return canvas


def asset_label(asset):
    names = sorted({origin.get("android_name") for origin in asset.get("origins", []) if origin.get("android_name")})
    return names[0] if names else asset.get("title") or Path(asset["original_path"]).name


def sort_assets(assets):
    order = {category: i for i, category in enumerate(CATEGORIES)}
    return sorted(assets, key=lambda a: (0 if a.get("model_status") == "confirmed" else 1,
                                        order.get(a.get("category"), 99), asset_label(a), a["id"]))


def model_lookup(root):
    """Accept either the flat model inventory or a package-grouped inventory."""
    path = root / "models.json"
    result = {}
    if not path.exists():
        return result
    def visit(value):
        if isinstance(value, dict):
            identifier = value.get("product_id", value.get("id"))
            if identifier is not None and value.get("name"):
                result.setdefault(str(identifier), {"name": value["name"], "brand": value.get("brand")})
            for nested in value.values():
                if isinstance(nested, (dict, list)):
                    visit(nested)
        elif isinstance(value, list):
            for nested in value:
                visit(nested)
    visit(json.loads(path.read_text(encoding="utf-8")))
    return result


def contact_sheets(root, assets, confirmed=False, models=None, features=False):
    visual = [asset for asset in sort_assets(assets)
              if asset.get("preview_path") and inside(root, asset["preview_path"]).exists()]
    if features:
        visual = [asset for asset in visual if asset.get("feature_tags") or str(asset.get("category", "")).startswith("feature_")]
        category_order = {category: i for i, category in enumerate(CATEGORIES) if category.startswith("feature_")}
        feature_order = {feature: i for i, feature in enumerate(FEATURES)}
        visual.sort(key=lambda asset: (category_order.get(asset.get("category"), 99),
                                       min((feature_order.get(feature, 99) for feature in asset.get("feature_tags", [])), default=99),
                                       asset_label(asset), asset["id"]))
    if confirmed:
        visual = [asset for asset in visual if asset.get("model_status") == "confirmed"]
        def representative_rank(asset):
            description = json.dumps(asset.get("origins", []), ensure_ascii=False) + asset.get("title", "")
            return (0 if "img_detail" in description else 1,
                    0 if asset.get("category") == "device_appearance" else 1,
                    asset_label(asset), asset["id"])
        by_model = collections.defaultdict(list)
        for asset in visual:
            for model in asset.get("model_ids", []):
                by_model[model].append(asset)
        representatives = []
        for model in sorted(by_model):
            selected = dict(min(by_model[model], key=representative_rank))
            selected["_display_model"] = model
            selected["_display_model_name"] = (models or {}).get(model, {}).get("name", model)
            representatives.append(selected)
        # The same authoritative image may represent several models. Retain one
        # labelled representative per model even when the content hash repeats.
        used = {asset["id"] for asset in representatives}
        visual = representatives + [asset for asset in sorted(visual, key=representative_rank) if asset["id"] not in used]
    directory = root / "contact-sheets"
    directory.mkdir(parents=True, exist_ok=True)
    pages = []
    cell_w, cell_h, margin, title_h = 340, 322, 24, 110
    total = math.ceil(len(visual) / 20)
    f_title, f_label, f_small = font(27, True), font(17), font(15)
    for offset in range(0, len(visual), 20):
        page = offset // 20 + 1
        canvas = Image.new("RGB", (cell_w * 4 + margin * 2, title_h + cell_h * 5 + margin), "#f5f7fa")
        draw = ImageDraw.Draw(canvas)
        heading = "功能美术接触表" if features else "型号外观接触表" if confirmed else "资源接触表"
        draw.text((margin, 20), f"耳机美术研究 · {heading} {page}/{total}", font=f_title, fill="#1c2a3c")
        subtitle = ("Hi-Res、降噪等本地功能美术；保留原始配色，未解析的动态主题色与静帧限制见 index.html。"
                    if features else "优先展示各型号整体外观；型号关联来自产品 ID 与资源包配置，配色代码不自动解释为颜色名称。"
                    if confirmed else "默认外观与示意插图不代表已确认型号；矢量预览为静帧。完整来源见 index.html。")
        draw.text((margin, 61), subtitle, font=f_small, fill="#526276")
        for i, asset in enumerate(visual[offset:offset+20]):
            x = margin + (i % 4) * cell_w
            y = title_h + (i // 4) * cell_h
            draw.rounded_rectangle((x, y, x+cell_w-12, y+cell_h-12), radius=8, fill="white", outline="#d9e1ea")
            background = checkerboard((cell_w-30, 190))
            try:
                with Image.open(inside(root, asset["preview_path"])) as image:
                    image = image.convert("RGBA")
                    image.thumbnail((cell_w-44, 178), Image.Resampling.LANCZOS)
                    background.alpha_composite(image, ((background.width-image.width)//2, (background.height-image.height)//2))
            except (OSError, ValueError):
                pass
            canvas.paste(background.convert("RGB"), (x+9, y+9))
            if asset.get("_display_model"):
                members = [Path(origin.get("zip_member") or origin.get("apk_entry") or "").name
                           for origin in asset.get("origins", []) if str(origin.get("product_id")) == asset["_display_model"]]
                member_label = next((member for member in members if "img_detail" in member), members[0] if members else asset_label(asset))
                label = asset["_display_model_name"] + " · " + member_label
            elif confirmed and asset.get("model_names"):
                names = asset["model_names"]
                label = (names[0] if isinstance(names, list) else next(iter(names.values()), "")) + " · " + asset_label(asset)
            else:
                label = asset_label(asset)
            label_lines = wrapped_lines(draw, label, f_label, cell_w-32, 2)
            for j, line in enumerate(label_lines):
                draw.text((x+10, y+207+j*23), line, font=f_label, fill="#1e2a3b")
            dimensions = f"{asset.get('width') or '—'} × {asset.get('height') or '—'}"
            rid = sorted({o.get("resource_id") for o in asset.get("origins", []) if o.get("resource_id")})
            identity = ("型号 " + (asset.get("_display_model") or ",".join(asset.get("model_ids", []))[:29])
                        if confirmed else rid[0] if rid else str(asset["id"])[:20])
            draw.text((x+10, y+258), f"{identity}  ·  {dimensions}", font=f_small, fill="#536276")
            footer = (" / ".join(FEATURES.get(feature, feature) for feature in asset.get("feature_tags", []))
                      if features else STATUS.get(asset.get("model_status"), "型号未确认"))
            footer = wrapped_lines(draw, footer, f_small, cell_w-32, 1)
            if footer:
                draw.text((x+10, y+282), footer[0], font=f_small, fill="#7a5a22")
        prefix = "features" if features else "confirmed-models" if confirmed else "page"
        path = directory / f"{prefix}-{page:03d}.png"
        canvas.save(path, "PNG")
        pages.append({"path": path.relative_to(root).as_posix(),
                      "asset_ids": [a["id"] for a in visual[offset:offset+20]],
                      "representative_models": [a["_display_model"] for a in visual[offset:offset+20] if a.get("_display_model")]})
    return pages


HTML = r'''<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>耳机美术资源 · 离线研究图册</title>
<style>
:root{color-scheme:light;--ink:#1b2b40;--muted:#607087;--line:#dce4ed;--blue:#1767ac}*{box-sizing:border-box}body{margin:0;background:#f4f7fa;color:var(--ink);font:15px/1.6 "Microsoft YaHei",system-ui,sans-serif}header{padding:28px max(24px,calc((100vw - 1440px)/2));background:#fff;border-bottom:1px solid var(--line)}h1{margin:0 0 8px;font-size:29px}header p{max-width:1040px;margin:6px 0;color:var(--muted)}.summary{display:flex;flex-wrap:wrap;gap:10px;margin-top:18px}.summary span{padding:5px 12px;background:#eef4fa;border-radius:8px}.links{margin-top:16px}a{color:var(--blue);text-decoration:none}a:hover{text-decoration:underline}main{max-width:1488px;margin:auto;padding:24px}.filters{display:grid;grid-template-columns:minmax(200px,2fr) repeat(4,minmax(130px,1fr));gap:12px}.filters label{display:block;font-size:12px;color:var(--muted)}input,select{display:block;width:100%;font:inherit;color:var(--ink);background:white;border:1px solid #ccd8e4;border-radius:7px;padding:9px 11px}.result{margin:16px 0;color:var(--muted)}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(288px,1fr));gap:18px}.card{background:white;border:1px solid var(--line);border-radius:12px;overflow:hidden}.image{display:flex;align-items:center;justify-content:center;min-height:222px;height:222px;background-color:#e8edf3;background-image:linear-gradient(45deg,#d6dfe9 25%,transparent 25%),linear-gradient(-45deg,#d6dfe9 25%,transparent 25%),linear-gradient(45deg,transparent 75%,#d6dfe9 75%),linear-gradient(-45deg,transparent 75%,#d6dfe9 75%);background-size:28px 28px;background-position:0 0,0 14px,14px -14px,-14px 0}.image img{display:block;max-height:200px;max-width:92%;object-fit:contain}.image.empty{background:#edf2f7;color:var(--muted);font-size:13px}.body{padding:15px}.title{font-size:15px;margin:0 0 7px;overflow-wrap:anywhere;line-height:1.55}.tags{display:flex;flex-wrap:wrap;gap:6px;margin-bottom:10px}.tag{font-size:11px;background:#eef3f8;border-radius:5px;padding:2px 7px;color:#53647b}.tag.feature{background:#e6f1fc;color:#22669b}.tag.status{background:#fff2d9;color:#7e5c22}.tag.confirmed{background:#e4f4e8;color:#256738}.meta{font-size:12px;color:var(--muted);margin:4px 0;overflow-wrap:anywhere}.origin{border-top:1px solid var(--line);margin-top:10px;padding-top:9px}.actions{display:flex;flex-wrap:wrap;gap:13px;font-size:12px;margin-top:12px}details{font-size:12px;color:var(--muted);margin-top:10px}summary{cursor:pointer}.note{margin:7px 0;overflow-wrap:anywhere}.path{font-family:Consolas,monospace;font-size:11px}.nothing{padding:40px;background:white;border-radius:12px;color:var(--muted)}footer{padding:30px 0;color:var(--muted);font-size:12px}@media(max-width:760px){.filters{grid-template-columns:1fr 1fr}header{padding:22px}main{padding:18px}h1{font-size:24px}}
</style></head><body>
<header><h1>耳机美术资源 · 离线研究图册</h1>
<p>原始 APK 与公开下载资源包的美术及派生预览。型号专图依据产品 ID 与配置关联；默认双耳、颈挂、耳夹与开盒示意仍标为通用资源，外形相似不能证明型号。</p>
<p>本页可直接双击打开，搜索和筛选完全离线。新增功能美术来自本地 APK，可按 Hi-Res、降噪等功能筛选；资源渐变按原件还原，未解析主题色与动画静帧限制列在说明中。</p>
<div class="summary" id="summary"></div><div class="links"><a href="assets.json">机器可读资源索引</a>　·　<a href="README.md">研究报告</a>　·　<span id="modellinks"></span><span id="sheetlinks"></span></div></header>
<main><div class="filters"><label>搜索名称、路径、ID、型号或功能<input id="search" type="search" placeholder="例如：Hi-Res、降噪、自适应、通透"></label><label>功能<select id="feature"><option value="">全部功能</option></select></label><label>类别<select id="category"><option value="">全部类别</option></select></label><label>来源<select id="source"><option value="">全部来源</option></select></label><label>资源类型<select id="kind"><option value="">全部类型</option></select></label></div>
<p class="result" id="result" aria-live="polite"></p><div class="grid" id="grid"></div><footer>本图册用于资源研究，原件与派生预览保留来源和哈希。此目录的资源尚未应用到项目界面。</footer></main>
<script id="manifest" type="application/json">__DATA__</script>
<script>
"use strict";
const data=JSON.parse(document.getElementById("manifest").textContent),assets=data.assets;
const categories=data.category_labels,kinds=data.kind_labels,statuses=data.status_labels,features=data.feature_labels,aliases=data.feature_aliases;
const q=id=>document.getElementById(id),url=path=>path.split("/").map(encodeURIComponent).join("/");
const node=(tag,text,cls)=>{const e=document.createElement(tag);if(text!==undefined)e.textContent=text;if(cls)e.className=cls;return e};
const link=(label,path)=>{const a=node("a",label);a.href=url(path);a.target="_blank";a.rel="noopener";return a};
const isRemote=o=>Boolean(o.remote_archive||o.source_type&&/remote|download/.test(o.source_type)||o.download_url||o.resource_url||o.zip_sha256||o.resource_zip_sha256||o.parent_entry&&/\.zip$/i.test(o.parent_entry));
const packages=a=>[...new Set(a.origins.map(o=>isRemote(o)?"remote_zip":o.package||"unknown"))];
const labels=a=>[...new Set(a.origins.map(o=>o.android_name).filter(Boolean))];
const firstLabel=a=>labels(a)[0]||a.title||a.id;
const translatedNote=value=>({"Binary Android XML is authoritative; decoded.xml is a research conversion.":"Android 二进制 XML 为原始证据；decoded.xml 是研究用的可读转换。","The paired shader samples RGB and alpha from video halves; it is not a 3D mesh.":"这组着色器从视频左右半帧分别取得 RGB 与透明度，不是 3D 网格。","Opaque binary; no established mesh, texture, or model format. Not counted as a 3D model.":"未知二进制数据，尚未确认网格、纹理或模型格式，不计为 3D 模型。"}[value]||value);
for(const [id,map,values] of [["feature",features,assets.flatMap(a=>a.feature_tags||[])],["category",categories,assets.map(a=>a.category)],["kind",kinds,assets.map(a=>a.kind)],["source",{"com.oplus.melody":"无线耳机 · com.oplus.melody","com.heytap.headset":"欢律 · com.heytap.headset","remote_zip":"公开下载资源 ZIP","unknown":"来源待确认"},assets.flatMap(packages)]]){const keys=new Set(values);for(const key of [...Object.keys(map).filter(key=>keys.has(key)),...[...keys].filter(key=>!(key in map)).sort()]){const option=node("option",map[key]||key);option.value=key;q(id).append(option)}}
for(const value of [assets.length+" 项独立资源",assets.filter(a=>a.preview_path).length+" 项图像预览",assets.filter(a=>a.model_status==="confirmed"&&["bitmap","svg","android_vector"].includes(a.kind)).length+" 项已确认型号外观图",assets.filter(a=>(a.feature_tags||[]).length).length+" 项功能资源"]){q("summary").append(node("span",value))}
for(const path of data.model_files||[]){q("modellinks").append(link(path.endsWith(".csv")?"型号目录 CSV":"型号目录 JSON",path),document.createTextNode("　·　"))}
if(data.contact_sheets.length){q("sheetlinks").append(link("资源接触表（"+data.contact_sheets.length+" 页）",data.contact_sheets[0].path));const details=node("details");details.append(node("summary","全部接触表"));for(const [i,p] of data.contact_sheets.entries()){details.append(link("第 "+(i+1)+" 页",p.path),document.createTextNode("　"))}q("sheetlinks").append(details)}
if(data.confirmed_contact_sheets.length){const details=node("details");details.append(node("summary","型号专图接触表（"+data.confirmed_contact_sheets.length+" 页）"));for(const [i,p] of data.confirmed_contact_sheets.entries())details.append(link("型号专图第 "+(i+1)+" 页",p.path),document.createTextNode("　"));q("sheetlinks").append(details)}
if(data.feature_contact_sheets.length){const details=node("details");details.append(node("summary","功能美术接触表（"+data.feature_contact_sheets.length+" 页）"));for(const [i,p] of data.feature_contact_sheets.entries())details.append(link("功能美术第 "+(i+1)+" 页",p.path),document.createTextNode("　"));q("sheetlinks").append(details)}
const searchable=new Map(assets.map(a=>[a.id,[JSON.stringify(a),a.category.startsWith("feature_")?"":categories[a.category]||"",kinds[a.kind]||"",...(a.feature_tags||[]).flatMap(feature=>[features[feature]||feature,...aliases[feature]||[]])].join(" ").toLowerCase()]));
function card(a){const article=node("article",undefined,"card"),area=node("div",undefined,"image"),body=node("div",undefined,"body");
 const preview=a.preview_path||a.preview_svg_path;if(preview){const image=node("img");image.src=url(preview);image.alt=firstLabel(a);image.loading="lazy";image.addEventListener("error",()=>{area.replaceChildren(node("span","预览不可用，请查看原件"));area.classList.add("empty")});area.append(image)}else{area.classList.add("empty");area.append(node("span",a.kind==="lottie"?"Lottie 原件 · 动画未在本页播放":a.kind==="android_drawable"?"组合绘制配置，子图已单列":a.kind==="android_color"?"颜色 / 渐变配置 · 供依赖图标还原原色":"原件已保留 · 无图像预览"))}
 body.append(node("h2",firstLabel(a),"title"));const tags=node("div",undefined,"tags");tags.append(node("span",categories[a.category]||a.category,"tag"),node("span",kinds[a.kind]||a.kind,"tag"),node("span",statuses[a.model_status]||"型号未确认","tag status "+(a.model_status==="confirmed"?"confirmed":"")));for(const feature of a.feature_tags||[])tags.append(node("span",features[feature]||feature,"tag feature"));body.append(tags);
 body.append(node("p",(a.width||"—")+" × "+(a.height||"—")+"　透明通道："+(a.alpha===true?"有":a.alpha===false?"无":"待确认"),"meta"));
 body.append(node("p","资源 ID："+a.id,"meta path"));if(a.model_ids.length)body.append(node("p","型号标识："+a.model_ids.join("、"),"meta"));if(a.model_names){const names=Array.isArray(a.model_names)?a.model_names:Object.values(a.model_names);body.append(node("p","型号名称："+names.join("、"),"meta"))}if(a.color!==null&&a.color!==undefined)body.append(node("p","配色代码："+a.color,"meta"));
 body.append(node("p","原件："+a.original_path,"meta path"));if((a.dependencies||[]).length)body.append(node("p","引用的子图 / 颜色资源 ID："+a.dependencies.join("、"),"meta path"));
 const source=node("div",undefined,"origin");source.append(node("p","来源："+packages(a).map(p=>p==="remote_zip"?"公开下载资源 ZIP":p).join("、"),"meta"));const details=node("details");details.append(node("summary","来源路径、哈希与说明（"+a.origins.length+" 条来源）"));details.append(node("p","SHA-256："+a.sha256,"note path"));for(const o of a.origins){details.append(node("p",(isRemote(o)?"公开资源 ZIP":o.package||"来源待确认")+" "+(o.version_name||"")+" · "+(o.zip_member||o.apk_entry||o.zip_entry||o.entry||"")+(o.parent_entry?"（父包："+o.parent_entry+"）":""),"note path"));if(o.android_name)details.append(node("p",o.android_name+" · "+(o.resource_id||"")+" · "+(Array.isArray(o.qualifiers)?o.qualifiers.join(", "):JSON.stringify(o.qualifiers||"")),"note path"));if(isRemote(o))details.append(node("p",JSON.stringify(o),"note path"))}for(const note of a.notes||[])details.append(node("p",translatedNote(note),"note"));source.append(details);body.append(source);
 const actions=node("div",undefined,"actions");actions.append(link("打开原件",a.original_path));if(a.decoded_path)actions.append(link("可读 XML",a.decoded_path));if(a.preview_svg_path)actions.append(link("SVG 静态预览",a.preview_svg_path));if(a.preview_path)actions.append(link("PNG 预览",a.preview_path));body.append(actions);article.append(area,body);return article}
function render(){const text=q("search").value.trim().toLowerCase(),feature=q("feature").value,category=q("category").value,source=q("source").value,kind=q("kind").value;const selected=assets.filter(a=>(!text||searchable.get(a.id).includes(text))&&(!feature||(a.feature_tags||[]).includes(feature))&&(!category||a.category===category)&&(!source||packages(a).includes(source))&&(!kind||a.kind===kind));q("result").textContent="显示 "+selected.length+" / "+assets.length+" 项资源；默认外观和功能图标不自动绑定型号。";const fragment=document.createDocumentFragment();for(const a of selected)fragment.append(card(a));if(!selected.length)fragment.append(node("div","没有匹配资源，请调整搜索或筛选。","nothing"));q("grid").replaceChildren(fragment)}
for(const id of ["search","feature","category","source","kind"])q(id).addEventListener(id==="search"?"input":"change",render);render();
</script></body></html>
'''


def build(root):
    root = research_path(root)
    manifest_path = root / "assets.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    assets = manifest["assets"]
    for asset in assets:
        inside(root, asset["original_path"])
    preview_log = render_previews(root, assets)
    pages = contact_sheets(root, assets)
    confirmed_pages = contact_sheets(root, assets, confirmed=True, models=model_lookup(root))
    feature_pages = contact_sheets(root, assets, features=True)
    atomic_text(manifest_path, json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
    payload = {"assets": sort_assets(assets), "category_labels": CATEGORIES, "kind_labels": KINDS,
               "status_labels": STATUS, "feature_labels": FEATURES, "feature_aliases": FEATURE_ALIASES,
               "contact_sheets": pages, "confirmed_contact_sheets": confirmed_pages, "feature_contact_sheets": feature_pages,
               "model_files": [name for name in ("models.csv", "models.json") if (root / name).exists()]}
    # Escape HTML-sensitive characters so an APK string cannot close the data tag.
    data = json.dumps(payload, ensure_ascii=False).replace("&", "\\u0026").replace("<", "\\u003c").replace(">", "\\u003e")
    atomic_text(root / "index.html", HTML.replace("__DATA__", data))
    report = {"asset_count": len(assets), "preview_count": sum(bool(a.get("preview_path")) for a in assets),
              "contact_sheets": pages, "confirmed_contact_sheets": confirmed_pages, "feature_contact_sheets": feature_pages,
              "feature_asset_count": sum(bool(a.get("feature_tags")) for a in assets), "vector_previews": preview_log,
              "notes": ["本次生成过程完全离线；未执行 APK。", "动画矢量仅展示静帧；颜色或路径限制见各资源 notes。"]}
    atomic_text(root / "gallery-build.json", json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({"assets": len(assets), "previews": report["preview_count"], "pages": len(pages), "confirmed_pages": len(confirmed_pages), "feature_pages": len(feature_pages),
                      "previews_generated": sum(p["status"] == "rendered" for p in preview_log),
                      "previews_unavailable": sum(p["status"] != "rendered" for p in preview_log)}, ensure_ascii=False))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[4] / "research/earbud-artwork",
                        help="研究输出目录（含已完成提取的 assets.json）")
    args = parser.parse_args()
    build(args.root)


if __name__ == "__main__":
    main()
