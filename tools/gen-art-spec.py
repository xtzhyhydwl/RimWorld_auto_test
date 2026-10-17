#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
gen-art-spec.py —— 贴图规格契约生成器

从 mod 的 Defs XML **机械导出**「这个 mod 需要哪些贴图、每张的硬规格」，
输出机器可读的 art-spec.json。人**不手写**这个文件。

设计原则（见 docs/贴图流水线-设计.md）：
  - 生成是随机的（模型出图），判定必须是确定的（本脚本 + validate-art-spec.py）
  - 契约只从 XML 事实导出；任何"推断"都单独标记 provenance，不冒充事实

抽取规则（严格、可复核）：
  - 只取每个 Def 根节点子树下的 <graphicData> / <bodyGraphicData>
    （含 building/、weapon/、renderTree/ 等常见容器）
  - defName 取 Def 根的直接子 <defName>
  - def 自身没写 texPath、而 Def 带 ParentName 指向 Abstract="True" 父模板 → 记为 inherited，不产出条目
  - graphicClass 决定方向需求：Graphic_Single(含 Graphic_Shadow/Graphic_Blend) → 无后缀；
    其它 Graphic_Multi* → ["_north","_east","_south","_west"]（小写，IL 已确证）
  - targetPixels = round(drawSize × pxPerCell)，pxPerCell 是**项目约定值，非引擎要求**

用法：
  python tools/gen-art-spec.py --mod <mod根目录> [--out art-spec.json]
                              [--px-per-cell 128] [--game-version 1.6]
                              [--render-world-objects] [--quiet]

退出码：0 成功；1 输入不合法（mod 目录不存在等）；2 XML 解析失败
"""

import argparse
import hashlib
import json
import os
import re
import sys

try:
    from lxml import etree
except ImportError:  # pragma: no cover
    sys.stderr.write("需要 lxml（捆绑 Python 自带），不联网安装。\n")
    sys.exit(1)

SPEC_VERSION = 1

# 方向后缀：小写，IL 已确证（Graphic_Multi 用 "_north" 等）
DIR_SUFFIXES = ["_north", "_east", "_south", "_west"]

# 这些 graphicClass 是单图，不需要方向后缀
SINGLE_GRAPHIC_CLASSES = {
    "Graphic_Single",
    "Graphic_Shadow",
    "Graphic_Blend",          # 未见于 1.6，保底：按单图处理并打分
}

# Def 根下允许出现 graphicData 的容器（depth>=1 也算有效命中）
CONTAINER_NAMES = {
    "building", "weapon", "renderTree", "graphicData", "bodyGraphicData",
    "doorTopGraphic", "doorTopGraphicOpen", "uiIconPath", "equipment",
}

# 命名后缀 → 格数 的启发式表，仅用于「XML 没写尺寸」时给一个**被标记为推断的**默认值
SUFFIX_DIMS = {
    "a": (1, 1), "b": (2, 2), "c": (3, 3), "d": (4, 4), "e": (5, 5),
    "f": (6, 6), "g": (7, 7), "h": (8, 8), "i": (9, 9), "j": (10, 10),
    "2x1": (2, 1), "3x1": (3, 1), "3x1b": (3, 1), "4x1": (4, 1),
    "1x1": (1, 1), "1x2": (1, 2), "5x5": (5, 5), "7x7": (7, 7),
    "9x9": (9, 9), "11x11": (11, 11), "13x13": (13, 13),
}

TUPLE_RE = re.compile(r"^\(\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*\)$")


def die(msg, code=1):
    sys.stderr.write("gen-art-spec: " + msg + "\n")
    sys.exit(code)


def sha256_of_files(paths):
    h = hashlib.sha256()
    for p in sorted(paths, key=lambda x: x.replace("\\", "/").lower()):
        h.update(p.replace("\\", "/").encode("utf-8"))
        with open(p, "rb") as f:
            h.update(f.read())
    return h.hexdigest()


def parse_vec2(text):
    """解析 '(1,1)' / '1.5' / '(3.2, 3)' → (x, y)。失败返回 None。"""
    if text is None:
        return None
    s = text.strip()
    if not s:
        return None
    m = TUPLE_RE.match(s)
    if m:
        try:
            return (float(m.group(1)), float(m.group(2)))
        except ValueError:
            return None
    try:
        v = float(s)
        return (v, v)
    except ValueError:
        return None


def local_name(el):
    return etree.QName(el).localname


def direct_child(el, name):
    for c in el:
        if isinstance(c.tag, str) and local_name(c) == name:
            return c
    return None


def direct_child_text(el, name):
    c = direct_child(el, name)
    if c is None:
        return None
    return (c.text or "").strip()


def read_load_folders(mod_root, game_version):
    """按 LoadFolders.xml 决定哪些子目录是活跃的 → 返回相对路径列表。"""
    lf = os.path.join(mod_root, "LoadFolders.xml")
    if not os.path.isfile(lf):
        return ["/"]
    try:
        root = etree.parse(lf).getroot()
    except etree.XMLSyntaxError:
        return ["/"]
    folders = ["/"]
    for node in root:
        if not isinstance(node.tag, str):
            continue
        tag = local_name(node)
        m = re.match(r"^v?([\d.]+)$", tag)
        if not m:
            continue
        v = m.group(1)
        if v.split(".")[0] != str(game_version).split(".")[0]:
            continue
        for li in node:
            if isinstance(li.tag, str) and local_name(li) == "li" and (li.text or "").strip():
                folders.append((li.text or "").strip())
    seen, out = set(), []
    for f in folders:
        if f not in seen:
            seen.add(f)
            out.append(f)
    return out


def collect_xml(base_root):
    out = []
    if not os.path.isdir(base_root):
        return out
    for dp, dn, fn in os.walk(base_root):
        dn.sort()
        for f in sorted(fn):
            if f.lower().endswith(".xml"):
                out.append(os.path.join(dp, f))
    return out


def harvest_defs(xml_paths):
    """返回 (defs, parse_errors)。defs: list of dict。"""
    defs = []
    errors = []
    for xp in xml_paths:
        try:
            with open(xp, "rb") as f:
                tree = etree.parse(f)
        except Exception as e:
            errors.append({"sourceXml": xp, "error": str(e)})
            continue
        root = tree.getroot()
        if root is None or local_name(root) != "Defs":
            continue
        for d in root:
            if not isinstance(d.tag, str):
                continue
            tag = local_name(d)
            if tag in ("Defs", "LoadFolders", "ModMetaData"):
                continue
            nameEl = direct_child(d, "defName")
            defs.append({
                "defType": tag,
                "defName": (nameEl.text or "").strip() if nameEl is not None and nameEl.text else None,
                "parentName": (d.get("ParentName") or "").strip() or None,
                "abstract": (d.get("Abstract") or "").strip().lower() == "true",
                "name": (d.get("Name") or "").strip() or None,
                "sizeText": direct_child_text(d, "size"),
                "uiIconPath": direct_child_text(d, "uiIconPath"),
                "texturePath": direct_child_text(d, "texturePath"),
                "sourceXml": xp,
                "element": d,
            })
    return defs, errors


def resolve_size(defobj, by_defname, by_name, seen=None):
    """返回 (sizeTuple, provenance)。provenance ∈ {def_size, parent_size, unknown}"""
    seen = seen or set()
    st = defobj["sizeText"]
    if st:
        v = parse_vec2(st)
        if v:
            return v, "def_size"
    key = defobj["parentName"]
    if key and key not in seen:
        seen.add(key)
        parent = by_defname.get(key) or by_name.get(key)
        if parent is not None:
            return resolve_size(parent, by_defname, by_name, seen)
    return None, "unknown"


def infer_size_from_suffix(tex_path):
    """按 texPath 的命名后缀猜格数（纯启发式，返回值必须被标记为 inferred）。"""
    base = tex_path.rsplit("/", 1)[-1]
    if "_" not in base:
        return None
    suffix = base.rsplit("_", 1)[-1].lower()
    return SUFFIX_DIMS.get(suffix)


def harvest_graphics(defobj):
    """在 Def 子树里找 graphicData / bodyGraphicData（含容器），返回 [(el, path)]"""
    found = []
    root = defobj["element"]

    def walk(el, path, depth):
        for c in el:
            if not isinstance(c.tag, str):
                continue
            tag = local_name(c)
            child_path = tag if not path else path + "/" + tag
            if tag in ("graphicData", "bodyGraphicData"):
                found.append((c, child_path))
                # graphicData 内部不再找（嵌套 graphicData 不是渲染入口）
                continue
            if depth < 3 and tag in CONTAINER_NAMES:
                walk(c, child_path, depth + 1)

    walk(root, "", 0)
    return found


def main():
    ap = argparse.ArgumentParser(description="从 Defs XML 机械导出贴图规格契约 art-spec.json")
    ap.add_argument("--mod", required=True, help="mod 根目录（含 About/Defs）")
    ap.add_argument("--out", default=None, help="输出路径，默认 <mod>/art-spec.json；'-' 表示打到 stdout")
    ap.add_argument("--px-per-cell", type=int, default=128,
                    help="每格像素数（**项目约定值，非引擎要求**），默认 128")
    ap.add_argument("--game-version", default="1.6", help="用于解析 LoadFolders.xml，默认 1.6")
    ap.add_argument("--defs-root", default=None, help="只扫这个目录（默认按 LoadFolders 自动定位）")
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args()

    mod_root = os.path.abspath(args.mod)
    if not os.path.isdir(mod_root):
        die("mod 目录不存在：%s" % mod_root, 1)
    if args.px_per_cell <= 0:
        die("--px-per-cell 必须为正整数", 1)

    if args.defs_root:
        defs_bases = [os.path.abspath(args.defs_root)]
    else:
        folders = read_load_folders(mod_root, args.game_version)
        defs_bases = []
        for f in folders:
            base = mod_root if f in ("/", ".", "") else os.path.join(mod_root, f)
            cand = os.path.join(base, "Defs")
            if os.path.isdir(cand):
                defs_bases.append(cand)
        if not defs_bases:
            cand = os.path.join(mod_root, "Defs")
            if os.path.isdir(cand):
                defs_bases.append(cand)

    xml_paths = []
    for b in defs_bases:
        xml_paths.extend(collect_xml(b))
    xml_paths = sorted(set(xml_paths), key=lambda x: x.replace("\\", "/").lower())

    defs, parse_errors = harvest_defs(xml_paths)
    by_defname = {d["defName"]: d for d in defs if d["defName"]}
    by_name = {d["name"]: d for d in defs if d["name"]}

    # 视觉顺序：先按文件，再按文档顺序
    items = []
    skipped = []
    diagnostics = []

    for d in defs:
        if d["uiIconPath"]:
            diagnostics.append({
                "kind": "unparsed_texture_ref",
                "element": "uiIconPath",
                "texPath": d["uiIconPath"],
                "defName": d["defName"],
                "sourceXml": d["sourceXml"],
                "note": "不在 graphicData/bodyGraphicData 契约内（契约只覆盖渲染入口），未生成条目",
            })
        if d["texturePath"]:
            diagnostics.append({
                "kind": "unparsed_texture_ref",
                "element": "texturePath",
                "texPath": d["texturePath"],
                "defName": d["defName"],
                "sourceXml": d["sourceXml"],
                "note": "不在 graphicData/bodyGraphicData 契约内（契约只覆盖渲染入口），未生成条目",
            })

        for gel, gpath in harvest_graphics(d):
            tex_path = direct_child_text(gel, "texPath")
            graphic_class = direct_child_text(gel, "graphicClass")
            draw_size_text = direct_child_text(gel, "drawSize")
            draw_size = parse_vec2(draw_size_text)
            size_prov = "graphicData_drawSize" if draw_size else None

            # Abstract="True" 的模板 Def 永远不会被实例化 → 它的 texPath **不是**贴图需求
            # （子 Def 继承了它、自己又不写 texPath 时，引擎会直接用父模板的 texPath，
            #   那是"复用已有贴图"，同样不产生新需求）
            if d["abstract"]:
                skipped.append({
                    "defName": d["defName"],
                    "defType": d["defType"],
                    "parentName": d["parentName"],
                    "defAbstract": True,
                    "sourceXml": d["sourceXml"],
                    "elementPath": gpath,
                    "texPath": tex_path,
                    "reason": "abstract_template",
                    "inferredFrom": None,
                    "note": "Abstract=\"True\" 模板 Def 不被实例化，不作为贴图需求（仅记录纹理引用）",
                })
                if tex_path:
                    diagnostics.append({
                        "kind": "abstract_template_texture",
                        "element": gpath,
                        "texPath": tex_path,
                        "defName": d["defName"],
                        "sourceXml": d["sourceXml"],
                        "note": "抽象模板引用的贴图，不作为契约条目；若该模板被具名 Def 继承则沿用同一张图",
                    })
                continue

            if not tex_path:
                why = "inherited_from_parent" if d["parentName"] else "no_texPath"
                skipped.append({
                    "defName": d["defName"],
                    "defType": d["defType"],
                    "parentName": d["parentName"],
                    "defAbstract": d["abstract"],
                    "sourceXml": d["sourceXml"],
                    "elementPath": gpath,
                    "reason": why,
                    "inferredFrom": d["parentName"] if d["parentName"] else None,
                    "note": ("Def 自身未写 texPath，继承自父模板 —— 无需贴图条目"
                             if d["parentName"] else "既无 texPath 也无父模板，无法定位贴图"),
                })
                continue

            cls = graphic_class or "(未指定)"
            if cls.startswith("Graphic_Multi"):
                required = list(DIR_SUFFIXES)
                dir_need = "multi"
            elif cls in SINGLE_GRAPHIC_CLASSES:
                required = []
                dir_need = "single"
            else:
                # 未知 graphicClass：按"可能多方向"处理并留诊断，让校验器继续找文件
                required = list(DIR_SUFFIXES)
                dir_need = "unknown"

            if draw_size is None:
                ds, prov = resolve_size(d, by_defname, by_name)
                if ds is not None:
                    draw_size, size_prov = ds, "def_size" if prov == "def_size" else "parent_size"
            if draw_size is None:
                guess = infer_size_from_suffix(tex_path)
                if guess is not None:
                    draw_size, size_prov = guess, "inferred_from_naming_suffix"
                else:
                    size_prov = "unknown"

            target = None
            if draw_size is not None:
                target = {
                    "w": int(round(draw_size[0] * args.px_per_cell)),
                    "h": int(round(draw_size[1] * args.px_per_cell)),
                }

            item = {
                "texPath": tex_path,
                "graphicClass": graphic_class,
                "drawSize": [draw_size[0], draw_size[1]] if draw_size else None,
                "drawSizeProvenance": size_prov,
                "requiredSuffixes": required,
                "defName": d["defName"],
                "defType": d["defType"],
                "sourceXml": d["sourceXml"].replace("\\", "/"),
                "elementPath": gpath,
                "targetPixels": target,
                "targetPixelsBasis": "project_convention" if target else "undetermined",
            }
            if dir_need == "unknown":
                item["graphicClassUnknown"] = True
            items.append(item)

    # 去重（同一 texPath + 同一 graphicClass 可能被多个 Def 引用）：保留首见，记录 co_referenced
    uniq, index = [], {}
    for it in items:
        key = (it["texPath"], it["graphicClass"])
        if key in index:
            uniq[index[key]]["coReferencedBy"].append(it["defName"])
            continue
        it["coReferencedBy"] = []
        index[key] = len(uniq)
        uniq.append(it)
    items = uniq

    def src_key(it):
        return (it["sourceXml"].lower(), it["elementPath"], it["texPath"])
    items.sort(key=src_key)
    skipped.sort(key=lambda s: (s["sourceXml"].lower(), s["defName"] or "", s["elementPath"]))
    diagnostics.sort(key=lambda s: (s["sourceXml"].lower(), s["defName"] or "", s["element"] or ""))

    about = os.path.join(mod_root, "About", "About.xml")
    mod_name, package_id = None, None
    if os.path.isfile(about):
        try:
            r = etree.parse(about).getroot()
            n = direct_child_text(r, "name")
            p = direct_child_text(r, "packageId")
            mod_name, package_id = n, p
        except Exception:
            pass

    spec = {
        "specVersion": SPEC_VERSION,
        "generator": "tools/gen-art-spec.py",
        "modRoot": mod_root.replace("\\", "/"),
        "modName": mod_name,
        "packageId": package_id,
        "gameVersion": str(args.game_version),
        "pxPerCell": args.px_per_cell,
        "pxPerCellNote": ("targetPixels = round(drawSize x pxPerCell)。pxPerCell 是**项目约定值**，"
                          "不是引擎要求：drawSize 的单位是格子，引擎按 drawSize 缩放，"
                          "像素尺寸不参与加载，因此尺寸不符只会糊/超框，不会加载失败。"),
        "defsRoots": [b.replace("\\", "/") for b in defs_bases],
        "xmlFilesScanned": [p.replace("\\", "/") for p in xml_paths],
        "inputDigestSha256": sha256_of_files(xml_paths) if xml_paths else None,
        "counts": {
            "xmlFiles": len(xml_paths),
            "defs": len(defs),
            "items": len(items),
            "skippedGraphicData": len(skipped),
            "diagnostics": len(diagnostics),
        },
        "items": items,
        "skipped": skipped,
        "diagnostics": diagnostics,
        "parseErrors": parse_errors,
    }

    text = json.dumps(spec, ensure_ascii=False, indent=2, sort_keys=False) + "\n"
    if args.out == "-":
        sys.stdout.write(text)
    else:
        out = args.out or os.path.join(mod_root, "art-spec.json")
        out = os.path.abspath(out)
        os.makedirs(os.path.dirname(out), exist_ok=True)
        with open(out, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        if not args.quiet:
            sys.stderr.write("已写出 %s\n" % out)

    if not args.quiet:
        print("=== art-spec 生成结果 ===")
        print("mod            : %s" % mod_root)
        print("扫描 XML       : %d 个" % len(xml_paths))
        print("Def 总数       : %d" % len(defs))
        print("贴图契约条目   : %d" % len(items))
        print("跳过(继承模板) : %d" % len(skipped))
        print("未纳入的贴图引用: %d" % len(diagnostics))
        print("inputDigest    : %s" % (spec["inputDigestSha256"] or "-"))
        if parse_errors:
            print("!! XML 解析失败 %d 个:" % len(parse_errors))
            for e in parse_errors:
                print("   %s : %s" % (e["sourceXml"], e["error"]))
        print("")
        print("%-52s %-22s %-9s %-8s %s" % ("texPath", "graphicClass", "drawSize", "target", "suffixes"))
        for it in items:
            tgt = "%dx%d" % (it["targetPixels"]["w"], it["targetPixels"]["h"]) if it["targetPixels"] else "-"
            ds = "%gx%g" % tuple(it["drawSize"]) if it["drawSize"] else "-"
            print("%-52s %-22s %-9s %-8s %s" % (
                it["texPath"][:52], (it["graphicClass"] or "-")[:22], ds, tgt,
                ",".join(it["requiredSuffixes"]) or "(none)"))
        skipped_items = [s for s in skipped]
        if skipped_items:
            print("")
            print("--- 跳过（无自身 texPath）---")
            for s in skipped_items:
                print("   def=%-28s parent=%-16s xml=%s" % (
                    s["defName"], s["parentName"], os.path.basename(s["sourceXml"])))
        if diagnostics:
            print("")
            print("--- 未纳入契约的贴图引用（仅记录，便于人工确认）---")
            for s in diagnostics:
                print("   %-14s %-34s def=%s" % (s["element"], s["texPath"][:34], s["defName"]))

    if parse_errors:
        sys.exit(2)
    sys.exit(0)


if __name__ == "__main__":
    main()
