#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
validate-art-spec.py —— 贴图校验器（确定性判定器）

读 art-spec.json + mod 目录，对每张期望的贴图做确定性检查，输出：
  1) 人类可读清单（stdout）
  2) 机器可读 JSON 报告（--json-out）
  3) 生成循环用的待办清单（--todo-out，模型下一步该重画什么）
  4) 明确退出码（有 error → 1）

核心原则：**模型永远不当裁判**。本脚本只报实测数字与分级，不猜艺术质量。

检查项（编号与 docs/贴图流水线-设计.md 一致）：
  A1 存在性          error   missing_file / missing_variant
  A2 多余文件        warn    orphan_texture
  A3 texPath 映射    error   texpath_out_of_textures（texPath 写错前缀/扩展名）
  B4 PNG 真实性      error   not_png_size_zero / not_png_decode_fail / masquerading_jpeg / masquerading_gif / extension_not_png
  B5 alpha 通道      warn    no_alpha_channel
  C6 尺寸            warn    size_mismatch（--strict-size 时升 error）
  C7 尺寸数学性质    warn    dim_not_power_of_two / dim_not_multiple_of_4
  D  透明像素亮度    warn    white_fringe_background_carried（>阈值）/ measure（仅测量）
  E8 占位符残留      error   placeholder_residue
  F9 撞原版          error   collision_vanilla
  F10 撞其它 mod     info    collision_mod（同名后加载者胜，需要人确认谁是"作者"）
  G11 方向后缀       error   missing_north_no_fallback / warn missing_direction_with_fallback
      PNG 元数据     info    has_hi_res_metadata（建议发布前剥掉）

不判定：艺术质量、美术风格、贴图内容/朝向是否正确。

用法：
  python tools/validate-art-spec.py --spec <art-spec.json> --mod <mod根目录>
        [--json-out report.json] [--todo-out todo.json]
        [--strict-size] [--allow-size-mismatch]
        [--px-per-cell N]           # 自检：按这个 px/格 重算 targetPixels 与契约对照
        [--transparent-lum-threshold 0.8]
        [--white-fringe-error-threshold 0.5]
        [--manifest-dir <RimWorld/Data>]   # 原版 DLC manifest，权威撞名依据
        [--advisory-core-dir <RimWorld/Data/Core>]  # Core 无 manifest，用 Defs 的 texPath 做辅证
        [--mods-dir <RimWorld/Mods>] [--workshop-dir <workshop/content/294100>]
        [--no-collision-scan] [--no-workshop-scan] [--no-advisory-scan]
        [--quiet] [--top N]

退出码：0 无 error；1 有 error；2 输入/配置错误
"""

import argparse
import collections
import json
import os
import re
import sys

try:
    from lxml import etree
except ImportError:  # pragma: no cover
    sys.stderr.write("需要 lxml（捆绑 Python 自带），不联网安装。\n")
    sys.exit(2)

try:
    from PIL import Image, features as pilfeatures
except ImportError:  # pragma: no cover
    sys.stderr.write("需要 Pillow（捆绑 Python 自带），不联网安装。\n")
    sys.exit(2)

Image.MAX_IMAGE_PIXELS = None  # 大图不做炸弹保护，本地可信输入

REPORT_VERSION = 1
DIR_SUFFIXES = ["_north", "_east", "_south", "_west"]
PNG_SIG = b"\x89PNG\r\n\x1a\n"
JPEG_SIG = b"\xff\xd8\xff"
GIF_SIG = (b"GIF87a", b"GIF89a")
BMP_SIG = b"BM"
WEBP_SIG_RIFF = b"RIFF"
ZIP_SIG = b"PK\x03\x04"

LEVEL_ORDER = {"error": 0, "warning": 1, "measure": 2, "info": 3}


# ----------------------------------------------------------------------------- 基础
 
def die(msg, code=2):
    sys.stderr.write("validate-art-spec: " + msg + "\n")
    sys.exit(code)


def norm(p):
    return p.replace("\\", "/")


def local_name(el):
    return etree.QName(el).localname


def direct_child_text(el, name):
    for c in el:
        if isinstance(c.tag, str) and local_name(c) == name:
            return (c.text or "").strip()
    return None


class Issue:
    __slots__ = ("level", "code", "subject", "message", "evidence", "fixHint")

    def __init__(self, level, code, subject, message, evidence=None, fix_hint=None):
        self.level = level
        self.code = code
        self.subject = subject
        self.message = message
        self.evidence = evidence or {}
        self.fixHint = fix_hint

    def to_json(self):
        d = {
            "level": self.level,
            "code": self.code,
            "subject": self.subject,
            "message": self.message,
            "evidence": self.evidence,
        }
        if self.fixHint:
            d["fixHint"] = self.fixHint
        return d


def sniff_format(head):
    """按魔数判定真实格式。返回 (format, note)。只读前若干字节。"""
    if head.startswith(PNG_SIG):
        return "png", "PNG 魔数正确"
    if head.startswith(JPEG_SIG):
        return "jpeg", "JPEG 魔数（FF D8 FF）"
    for s in GIF_SIG:
        if head.startswith(s):
            return "gif", "GIF 魔数（%s）" % s.decode("ascii")
    if head.startswith(WEBP_SIG_RIFF) and head[8:12] == b"WEBP":
        return "webp", "RIFF/WEBP 魔数"
    if head.startswith(BMP_SIG):
        return "bmp", "BMP 魔数（BM）"
    if head.startswith(ZIP_SIG):
        return "zip", "ZIP 魔数（PK\\x03\\x04，可能是 docx/xlsx 或压缩包）"
    return "unknown", "未知魔数：%s" % head[:8].hex(" ")


# ----------------------------------------------------------------------------- 路径

def read_load_folders(mod_root, game_version):
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
        if m.group(1).split(".")[0] != str(game_version).split(".")[0]:
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


def active_roots(mod_root, game_version):
    roots = []
    for f in read_load_folders(mod_root, game_version):
        base = mod_root if f in ("/", ".", "") else os.path.join(mod_root, f)
        if os.path.isdir(base):
            roots.append(base)
    if not roots:
        roots = [mod_root]
    # 去掉被其它 root 覆盖的嵌套 root（父目录优先），避免重复统计
    uniq = []
    for r in roots:
        rn = os.path.normcase(os.path.abspath(r))
        if any(rn == os.path.normcase(os.path.abspath(u)) for u in uniq):
            continue
        uniq.append(r)
    return uniq


def scan_textures(roots):
    """返回 dict: 相对 texPath（小写，斜杠分隔）→ {'abs','rel','sizeBytes','root','ext'}"""
    found = {}
    files_all = []
    for root in roots:
        tdir = os.path.join(root, "Textures")
        if not os.path.isdir(tdir):
            continue
        for dp, dn, fn in os.walk(tdir):
            dn.sort()
            for f in sorted(fn):
                ap = os.path.join(dp, f)
                rel = os.path.relpath(ap, tdir).replace("\\", "/")
                key = rel.lower()
                try:
                    sz = os.path.getsize(ap)
                except OSError:
                    sz = -1
                rel_no_ext = os.path.splitext(rel)[0]
                files_all.append((key, ap, rel, sz, root, rel_no_ext))
                if key not in found:
                    found[key] = {
                        "abs": ap, "rel": rel, "sizeBytes": sz, "root": root,
                        "ext": os.path.splitext(f)[1].lower(), "texPathOnDisk": rel_no_ext,
                    }
    files_all.sort(key=lambda x: x[0])
    return found, files_all


# ----------------------------------------------------------------------------- PNG

def analyze_png(path, lum_threshold, pixel_scan=True):
    """读一张 PNG：真伪 + alpha + 尺寸 + 透明像素亮度。

    pixel_scan=False 时只做便宜的头部检查（魔数/IHDR/尺寸/模式），不解像素 ——
    注意**真伪判定永远要做**，它只读 64 字节，不能因为"快跑"就跳过。
    """
    info = {
        "isPngMagic": False,
        "actualFormat": None,
        "actualFormatNote": None,
        "headerWidth": None,
        "headerHeight": None,
        "headerBitDepth": None,
        "headerColorType": None,
        "pragma": {},
        "width": None,
        "height": None,
        "mode": None,
        "hasAlphaChannel": None,
        "transparentPixelCount": None,
        "transparentPixelRatio": None,
        "transparentHighLuminanceCount": None,
        "transparentHighLuminanceRatio": None,
        "transparentMeanMaxChannel": None,
        "transparentMeanLuminance": None,
        "opaquePixelRatio": None,
        "luminanceMetric": "max(r,g,b)/255（按 Pillow RGBA 解出的线性前值）",
        "decodeError": None,
    }
    with open(path, "rb") as f:
        head = f.read(64)
    fmt, note = sniff_format(head)
    info["actualFormat"] = fmt
    info["actualFormatNote"] = note
    info["isPngMagic"] = (fmt == "png")

    if not info["isPngMagic"]:
        return info, None  # 调用方按 sniff 结果报错，不做像素分析

    # IHDR 直接从文件头解析（独立于 Pillow 的第二个证据源）
    if len(head) >= 29:
        import struct
        w, h = struct.unpack(">II", head[16:24])
        info["headerWidth"], info["headerHeight"] = w, h
        info["headerBitDepth"] = head[24]
        info["headerColorType"] = head[25]

    try:
        with Image.open(path) as im:
            im.load()
            info["pragma"] = {k: str(v) for k, v in sorted((im.info or {}).items())}
            info["width"], info["height"] = im.size
            info["mode"] = im.mode
            info["hasAlphaChannel"] = ("A" in im.getbands()) or (im.mode == "P" and "transparency" in im.info)
            if im.mode == "P":
                rgba = im.convert("RGBA")
            elif im.mode == "LA":
                rgba = im.convert("RGBA")
            elif im.mode == "L":
                rgba = im.convert("RGBA")
            else:
                rgba = im.convert("RGBA")
            aw, ah = rgba.size
            a = rgba.getchannel("A")
            hist = a.histogram()
            total = float(aw * ah)
            transparent = float(hist[0])
            info["transparentPixelCount"] = int(transparent)
            info["transparentPixelRatio"] = round(transparent / total, 6) if total else None
            info["opaquePixelRatio"] = round(float(hist[255]) / total, 6) if total else None

            if transparent > 0 and pixel_scan:
                px = rgba.load()
                hi = 0
                sum_max = 0
                sum_lum = 0
                n = 0
                for y in range(ah):
                    for x in range(aw):
                        r, g, b, al = px[x, y]
                        if al != 0:
                            continue
                        n += 1
                        m = r if r >= g and r >= b else (g if g >= b else b)
                        lum = (0.2126 * r + 0.7152 * g + 0.0722 * b)
                        sum_max += m
                        sum_lum += lum
                        if (m / 255.0) > lum_threshold:
                            hi += 1
                info["transparentHighLuminanceCount"] = hi
                info["transparentHighLuminanceRatio"] = round(hi / n, 6) if n else 0.0
                info["transparentMeanMaxChannel"] = round(sum_max / n / 255.0, 6)
                info["transparentMeanLuminance"] = round(sum_lum / n / 255.0, 6)
    except Exception as e:
        info["decodeError"] = "%s: %s" % (type(e).__name__, e)
    return info, None


def white_fringe_level(ratio):
    if ratio is None:
        return "unknown"
    if ratio > 0.5:
        return "high"
    if ratio > 0.1:
        return "medium"
    if ratio > 0.0:
        return "low"
    return "none"


# ----------------------------------------------------------------------------- 原版索引

def load_dlc_manifest_index(data_root):
    """权威索引：每个 DLC 的 resources_<dlc>.manifest 纯文本，逐行 split。

    返回 (index, paths_set, manifests)：
      index     : texPath(去扩展名,小写) → {dlc, manifest, entry}      # 路径级比对用
      paths_set : 实际 png 相对路径(小写,含方向后缀) → {dlc, manifest, entry}  # 文件级比对用
    """
    index = {}
    paths_set = {}
    manifests = []
    if not os.path.isdir(data_root):
        return index, paths_set, manifests
    dlcs = sorted([d for d in os.listdir(data_root) if os.path.isdir(os.path.join(data_root, d))],
                  key=str.lower)
    for dlc in dlcs:
        ab = os.path.join(data_root, dlc, "AssetBundles")
        if not os.path.isdir(ab):
            continue
        for f in sorted(os.listdir(ab)):
            if not f.lower().endswith(".manifest"):
                continue
            mp = os.path.join(ab, f)
            n_tex = 0
            with open(mp, "rb") as fh:
                raw = fh.read()
            # 注意：manifest 末尾可能没有换行 → 按行 split，不做整读正则
            for line in raw.split(b"\n"):
                try:
                    s = line.decode("utf-8-sig").strip()
                except UnicodeDecodeError:
                    continue
                if not s.startswith("- Assets/") or "/Textures/" not in s:
                    continue
                rel = s[2:].strip().split("/Textures/", 1)[1]
                base, ext = os.path.splitext(rel)
                if ext.lower() != ".png":
                    continue  # .psd 是源文件占位，游戏不会把它当松散 PNG 加载
                rec = {"dlc": dlc, "manifest": mp, "entry": s[2:].strip()}
                index.setdefault(base.lower(), rec)
                paths_set[rel.lower()] = dict(rec, rel=rel)
                n_tex += 1
            manifests.append({"path": norm(mp), "dlc": dlc, "pngTexPaths": n_tex,
                              "lines": len(raw.split(b"\n"))})
    return index, paths_set, manifests


def load_core_advisory_index(core_dir):
    """辅证索引：Core 没有 .manifest（贴图在 Unity resources.assets 里，无明文路径），
    退而求其次扫描 Core 的 Defs，取所有 <texPath>。**这不是权威依据**，只做辅证。"""
    index = {}
    if not os.path.isdir(core_dir):
        return index, {"scanned": 0, "distinct": 0}
    defs = os.path.join(core_dir, "Defs")
    if not os.path.isdir(defs):
        return index, {"scanned": 0, "distinct": 0}
    n = 0
    for dp, dn, fn in os.walk(defs):
        dn.sort()
        for f in sorted(fn):
            if not f.lower().endswith(".xml"):
                continue
            p = os.path.join(dp, f)
            n += 1
            try:
                with open(p, "rb") as fh:
                    root = etree.fromstring(fh.read())
            except Exception:
                continue
            for el in root.iter():
                if not isinstance(el.tag, str):
                    continue
                if local_name(el) in ("texPath",) and el.text and el.text.strip():
                    index.setdefault(el.text.strip().lower(), norm(p))
    return index, {"scanned": n, "distinct": len(index)}


# ----------------------------------------------------------------------------- 主流程

def main():
    ap = argparse.ArgumentParser(description="贴图校验器：读 art-spec.json，做确定性检查")
    ap.add_argument("--spec", required=True, help="art-spec.json 路径")
    ap.add_argument("--mod", required=True, help="mod 根目录")
    ap.add_argument("--json-out", default=None, help="机器可读报告输出路径（默认 stdout 摘要 + 无 JSON）")
    ap.add_argument("--todo-out", default=None, help="生成循环待办 JSON（模型下一步重画什么）")
    ap.add_argument("--px-per-cell", type=int, default=None,
                    help="自检用：按这个 px/格 重算 targetPixels，与 spec 里冻结的值对照，"
                         "不一致时明确标注（不静默替换契约）")
    ap.add_argument("--strict-size", action="store_true", help="尺寸不符判 error（默认 warning）")
    ap.add_argument("--allow-size-mismatch", action="store_true",
                    help="显式接受尺寸差异，连 warning 也不报（只留 measure）")
    ap.add_argument("--transparent-lum-threshold", type=float, default=0.8,
                    help="透明像素判为『亮』的 max(r,g,b)/255 阈值，默认 0.8")
    ap.add_argument("--white-fringe-error-threshold", type=float, default=0.5,
                    help="白边高危阈值：亮透明像素占比超过它 → warning（默认 0.5）")
    ap.add_argument("--manifest-dir", default=r"D:\SteamLibrary\steamapps\common\RimWorld\Data",
                    help="原版 Data 目录（读 DLC .manifest）")
    ap.add_argument("--advisory-core-dir", default=None,
                    help="辅证：Core 目录（无 manifest，扫 Defs 的 texPath）。传 'auto' 用 manifest-dir/Core")
    ap.add_argument("--mods-dir", default=r"D:\SteamLibrary\steamapps\common\RimWorld\Mods",
                    help="本地 Mods 目录（撞名交叉检查）")
    ap.add_argument("--workshop-dir", default=r"D:\SteamLibrary\steamapps\workshop\content\294100",
                    help="创意工坊目录（撞名交叉检查）")
    ap.add_argument("--no-collision-scan", action="store_true", help="跳过撞原版检查")
    ap.add_argument("--no-advisory-scan", action="store_true", help="跳过 Core Defs 辅证")
    ap.add_argument("--no-workshop-scan", action="store_true", help="跳过其它 mod 撞名检查")
    ap.add_argument("--placeholder-list", default=None,
                    help="显式占位符清单（每行一个 texPath，# 开头为注释）：这些是**已知有意**"
                         "引用原版贴图，降级为 info，不计入占位符残留数")
    ap.add_argument("--no-pixel-scan", action="store_true",
                    help="只查存在性/撞名，不解码像素（大工程快跑）")
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args()

    spec_path = os.path.abspath(args.spec)
    mod_root = os.path.abspath(args.mod)
    if not os.path.isfile(spec_path):
        die("spec 不存在：%s" % spec_path, 2)
    if not os.path.isdir(mod_root):
        die("mod 目录不存在：%s" % mod_root, 2)

    with open(spec_path, "rb") as f:
        spec = json.loads(f.read().decode("utf-8-sig"))

    game_version = str(spec.get("gameVersion") or "1.6")
    px_per_cell = int(spec.get("pxPerCell") or 128)
    override_ppc = args.px_per_cell
    if override_ppc is not None and override_ppc != px_per_cell:
        # 自检：按另一个 pxPerCell 重算 targetPixels，与 spec 里冻结的值对照
        print("[自检] --px-per-cell=%d 与 spec 内 pxPerCell=%d 不同；"
              "下面的 targetPixels 差异会被明确标注，不静默替换契约。"
              % (override_ppc, px_per_cell), file=sys.stderr)

    roots = active_roots(mod_root, game_version)
    disk, all_files = scan_textures(roots)

    issues = []
    A = issues.append

    # ---------------- A1/A3：期望的贴图逐条核对 ----------------
    items_out = []
    expected_keys = set()
    any_missing = 0

    for it in sorted(spec.get("items", []),
                     key=lambda x: (str(x.get("sourceXml", "")).lower(),
                                    str(x.get("elementPath", "")),
                                    str(x.get("texPath", "")).lower())):
        tex_path = str(it.get("texPath") or "").strip()
        gc = it.get("graphicClass")
        required = list(it.get("requiredSuffixes") or [])
        target = it.get("targetPixels")
        entry = {
            "texPath": tex_path,
            "graphicClass": gc,
            "defName": it.get("defName"),
            "sourceXml": it.get("sourceXml"),
            "elementPath": it.get("elementPath"),
            "drawSize": it.get("drawSize"),
            "drawSizeProvenance": it.get("drawSizeProvenance"),
            "targetPixels": target,
            "requiredSuffixes": required,
            "variants": [],
            "verdict": "ok",
            "placeholderSuspect": False,
        }

        # A3：texPath 映射规则 —— 不带 Textures/ 前缀、不带扩展名、用 / 分隔
        bad_prefix = tex_path.replace("\\", "/").lower().startswith("textures/")
        bad_ext = os.path.splitext(tex_path)[1].lower() in (".png", ".psd", ".jpg", ".jpeg", ".dds", ".tga")
        bad_sep = "\\" in tex_path
        if bad_prefix or bad_ext or bad_sep:
            why = []
            if bad_prefix:
                why.append("多了 Textures/ 前缀")
            if bad_ext:
                why.append("带了扩展名 %s" % os.path.splitext(tex_path)[1])
            if bad_sep:
                why.append("用了反斜杠分隔符")
            A(Issue("error", "texpath_out_of_textures", tex_path,
                    "texPath 写法不合规：%s（defName=%s）" % ("；".join(why), it.get("defName")),
                    {"texPath": tex_path, "sourceXml": it.get("sourceXml"),
                     "rule": "texPath 不带 Textures/ 前缀、不带扩展名、用 / 分隔"},
                    "改 Defs XML 里的 <texPath>，不是改文件"))
            entry["verdict"] = "error"

        key_base = tex_path.lower()
        disk_base = disk.get(key_base + ".png")
        candidates = []
        if required:
            for s in required:
                candidates.append(s)
        else:
            candidates.append("")

        missing_suffixes = []
        present_suffixes = []
        base_present_any = False

        for suf in candidates:
            k = (key_base + suf + ".png")
            f = disk.get(k)
            vname = (tex_path + suf + ".png") if suf else (tex_path + ".png")
            vout = {"suffix": suf, "file": vname, "exists": bool(f)}
            if f:
                base_present_any = True
                present_suffixes.append(suf)
                vout["sizeBytes"] = f["sizeBytes"]
                expected_keys.add(k)
                if f["ext"] != ".png":
                    A(Issue("warning", "extension_not_png", vname,
                            "mod 自己的 Textures 下有非 .png 文件（扩展名 %s）—— "
                            "游戏只加载 .png，这个文件不会被用到" % f["ext"],
                            {"file": norm(f["abs"]), "extension": f["ext"]},
                            "删掉，或改成 .png 重新导出"))
                info, _ = analyze_png(f["abs"], args.transparent_lum_threshold,
                                      pixel_scan=not args.no_pixel_scan)
                vout["format"] = info["actualFormat"]
                vout["actualFormatNote"] = info["actualFormatNote"]
                vout["png"] = {k2: info[k2] for k2 in (
                    "headerWidth", "headerHeight", "headerBitDepth", "headerColorType",
                    "mode", "hasAlphaChannel", "transparentPixelCount", "transparentPixelRatio",
                    "transparentHighLuminanceCount", "transparentHighLuminanceRatio",
                    "transparentMeanMaxChannel", "transparentMeanLuminance", "opaquePixelRatio",
                    "pragma", "decodeError", "luminanceMetric")}
                vout["pixelScanPerformed"] = not args.no_pixel_scan
                # B4：真实格式
                if not info["isPngMagic"]:
                    if info["actualFormat"] == "jpeg":
                        A(Issue("error", "masquerading_jpeg", vname,
                                "扩展名是 .png，但文件魔数是 JPEG（FF D8 FF）—— 游戏解码会失败并回退紫黑格",
                                {"file": norm(f["abs"]), "actualFormat": "jpeg",
                                 "headHex": open(f["abs"], "rb").read(8).hex(" "),
                                 "sizeBytes": f["sizeBytes"]},
                                "用图像工具真正导出为 PNG（不是改扩展名）"))
                    elif info["actualFormat"] in ("gif", "webp", "bmp", "zip"):
                        A(Issue("error", "masquerading_%s" % info["actualFormat"], vname,
                                "扩展名是 .png，但真实格式是 %s —— 游戏会解码失败" % info["actualFormat"],
                                {"file": norm(f["abs"]), "actualFormat": info["actualFormat"],
                                 "actualFormatNote": info["actualFormatNote"]},
                                "重新导出为真 PNG"))
                    else:
                        A(Issue("error", "not_png_magic", vname,
                                "扩展名是 .png，但文件没有 PNG 魔数（%s）" % info["actualFormatNote"],
                                {"file": norm(f["abs"]), "actualFormat": info["actualFormat"]},
                                "确认导出格式；损坏文件请重新导出"))
                    entry["verdict"] = "error"
                elif info["decodeError"]:
                    A(Issue("error", "not_png_decode_fail", vname,
                            "PNG 魔数正确但 Pillow 解码失败：%s" % info["decodeError"],
                            {"file": norm(f["abs"]), "decodeError": info["decodeError"]},
                            "文件可能截断/损坏，重新导出"))
                    entry["verdict"] = "error"
                else:
                    # B5 alpha
                    if info["hasAlphaChannel"] is False:
                        A(Issue("warning", "no_alpha_channel", vname,
                                "该 PNG 没有 alpha 通道（mode=%s）—— 需要透明的地方一定是错的"
                                % info["mode"],
                                {"file": norm(f["abs"]), "mode": info["mode"],
                                 "headerColorType": info["headerColorType"]},
                                "导出时保留透明通道（RGBA / 带 tRNS 的调色板）"))
                    # C6 尺寸
                    if target and info["width"] and info["height"]:
                        if (info["width"], info["height"]) != (target["w"], target["h"]):
                            lvl = "warning"
                            code = "size_mismatch"
                            if args.strict_size:
                                lvl, code = "error", "size_mismatch"
                            elif args.allow_size_mismatch:
                                lvl, code = "measure", "size_mismatch"
                            ev = {"file": norm(f["abs"]),
                                  "actual": {"w": info["width"], "h": info["height"]},
                                  "target": target, "pxPerCell": px_per_cell,
                                  "engineEnforced": False,
                                  "note": "引擎按 drawSize 缩放，像素尺寸不是引擎要求；"
                                          "不符只会糊或超框"}
                            if override_ppc is not None and override_ppc != px_per_cell \
                                    and it.get("drawSize"):
                                ev["recomputedTargetAtOverridePxPerCell"] = {
                                    "pxPerCell": override_ppc,
                                    "target": {
                                        "w": int(round(it["drawSize"][0] * override_ppc)),
                                        "h": int(round(it["drawSize"][1] * override_ppc)),
                                    },
                                    "contractFrozenAt": px_per_cell,
                                }
                            A(Issue(lvl, code, vname,
                                    "实测 %dx%d，契约 targetPixels 是 %dx%d（drawSize=%s × %d px/格）"
                                    % (info["width"], info["height"], target["w"], target["h"],
                                       it.get("drawSize"), px_per_cell),
                                    ev,
                                    "按 targetPixels 重出，或调整契约 pxPerCell 并统一全项目"))
                            if lvl == "error":
                                entry["verdict"] = "error"
                        # C7 尺寸数学性质
                        if info["width"] and info["height"]:
                            def pow2(v):
                                return v > 0 and (v & (v - 1)) == 0
                            if not (pow2(info["width"]) and pow2(info["height"])):
                                A(Issue("warning", "dim_not_power_of_two", vname,
                                        "边长不是 2 的幂：%dx%d（老 GPU 会强制缩放，可能模糊）"
                                        % (info["width"], info["height"]),
                                        {"actual": {"w": info["width"], "h": info["height"]}},
                                        "如需兼容老 GPU，出成 2 的幂"))
                            if info["width"] % 4 or info["height"] % 4:
                                A(Issue("warning", "dim_not_multiple_of_4", vname,
                                        "边长不是 4 的倍数：%dx%d（DXT/压缩管线对非 4 倍数不友好）"
                                        % (info["width"], info["height"]),
                                        {"actual": {"w": info["width"], "h": info["height"]}},
                                        "出成 4 的倍数"))
                        # D 透明像素亮度
                        ratio = info["transparentHighLuminanceRatio"]
                        if info["transparentPixelCount"]:
                            lvl = white_fringe_level(ratio)
                            msg = ("alpha=0 像素 %d 个（占全图 %.2f%%），其中 max(r,g,b)>%.2f 的有 %d 个"
                                   "（占透明像素 %.2f%%），透明像素平均 max(r,g,b)=%.4f，风险=%s"
                                   % (info["transparentPixelCount"],
                                      (info["transparentPixelRatio"] or 0) * 100,
                                      args.transparent_lum_threshold,
                                      info["transparentHighLuminanceCount"], (ratio or 0) * 100,
                                      info["transparentMeanMaxChannel"] or 0, lvl))
                            ev = {"file": norm(f["abs"]),
                                  "transparentLuminanceThreshold": args.transparent_lum_threshold,
                                  "transparentPixelCount": info["transparentPixelCount"],
                                  "transparentPixelRatio": info["transparentPixelRatio"],
                                  "transparentHighLuminanceCount": info["transparentHighLuminanceCount"],
                                  "transparentHighLuminanceRatio": ratio,
                                  "transparentMeanMaxChannel": info["transparentMeanMaxChannel"],
                                  "transparentMeanLuminance": info["transparentMeanLuminance"],
                                  "whiteFringeRisk": lvl,
                                  "metric": info["luminanceMetric"],
                                  "rootCause": "松散 PNG 会被内存压缩，邻接采样把透明像素的残留亮色渗进边缘 → 白边"}
                            if lvl == "high" and ratio is not None and ratio > args.white_fringe_error_threshold:
                                A(Issue("warning", "white_fringe_background_carried", vname, msg, ev,
                                        "导出前把全透明区域的 RGB 清零（例如 Photoshop 的『修边/移去白色杂边』，"
                                        "或脚本里 alpha=0 的像素 RGB 全设 0）"))
                            else:
                                A(Issue("measure", "transparent_luminance_measured", vname, msg, ev))
                        else:
                            A(Issue("measure", "no_transparent_pixels", vname,
                                    "全图没有 alpha=0 的像素（完全无透明），白边风险不适用",
                                    {"file": norm(f["abs"]),
                                     "transparentPixelCount": 0,
                                     "opaquePixelRatio": info["opaquePixelRatio"]}))
                        # 元数据
                        if info["pragma"]:
                            A(Issue("info", "png_metadata_present", vname,
                                    "PNG 里带元数据：%s" % ", ".join(sorted(info["pragma"].keys())),
                                    {"pragma": info["pragma"]},
                                    "发布前可剥掉元数据（体积/隐私），非必须"))
            else:
                missing_suffixes.append(suf)
            entry["variants"].append(vout)

        # G11：方向后缀的真实语义
        if missing_suffixes:
            if required:
                has_north = "_north" in present_suffixes
                fallbacks = [s for s in ("_south", "_east", "_west") if s in present_suffixes]
                if not base_present_any:
                    A(Issue("error", "missing_file", tex_path,
                            "契约要求 %d 个方向变体，磁盘上一个都没有（defName=%s）"
                            % (len(required), it.get("defName")),
                            {"expectDir": norm(os.path.join(roots[0], "Textures")),
                             "expected": [(tex_path + s + ".png") for s in required],
                             "defName": it.get("defName"), "sourceXml": it.get("sourceXml")},
                            "按契约出图：%s" % ", ".join(tex_path + s + ".png" for s in required)))
                    entry["verdict"] = "error"
                elif not has_north and not fallbacks:
                    A(Issue("error", "missing_north_no_fallback", tex_path,
                            "缺 _north 且没有任何回退候选（_south/_east/_west 都没有，也没有无后缀单图）"
                            "—— 引擎渲染回退链会走到尽头（defName=%s）" % it.get("defName"),
                            {"expected": tex_path + "_north.png",
                             "presentSuffixes": present_suffixes,
                             "engineFallbackChain": ["_north", "_south(+180)", "_east(-90)",
                                                     "_west(+90)", "<无后缀单图>"]},
                            "至少补一张 %s_north.png" % tex_path))
                    entry["verdict"] = "error"
                else:
                    detail = []
                    if not has_north:
                        detail.append("缺 _north（但可用 %s 回退，引擎会显示错朝向）"
                                      % "/".join(fallbacks or ["无后缀单图"]))
                    other = [s for s in missing_suffixes if s != "_north"]
                    if other:
                        detail.append("缺 %s（有回退，warning）" % "/".join(other))
                    A(Issue("warning", "missing_direction_with_fallback", tex_path,
                            "方向变体不全：%s" % "；".join(detail),
                            {"expected": [(tex_path + s + ".png") for s in required],
                             "present": present_suffixes, "missing": missing_suffixes,
                             "engineSemantics": "只有 _north 是必需的；缺 north 依次回退 south(+180°) → "
                                                "east(-90°) → west(+90°) → 无后缀单图",
                             "defName": it.get("defName")},
                            "补齐 %s 让朝向正确" % "/".join(missing_suffixes)))
                    if entry["verdict"] == "ok":
                        entry["verdict"] = "warning"
            else:
                if not base_present_any:
                    A(Issue("error", "missing_file", tex_path,
                            "契约要求这张贴图，磁盘上没有 %s.png（defName=%s, graphicClass=%s）"
                            % (tex_path, it.get("defName"), gc),
                            {"expectDir": norm(os.path.join(roots[0], "Textures")),
                             "expected": tex_path + ".png",
                             "sourceXml": it.get("sourceXml"),
                             "elementPath": it.get("elementPath")},
                            "生成 %s.png（目标 %s）" % (
                                tex_path,
                                "%dx%d" % (target["w"], target["h"]) if target else "尺寸见契约")))
                    entry["verdict"] = "error"

        if any(v["exists"] for v in entry["variants"]) and entry["verdict"] == "ok":
            entry["verdict"] = "ok"
        if not any(v["exists"] for v in entry["variants"]):
            any_missing += 1
        items_out.append(entry)

    # ---------------- A2：多余文件 ----------------
    orphans = []
    for key, ap, rel, sz, root, rel_no_ext in all_files:
        if key in expected_keys:
            continue
        orphans.append({
            "texPath": rel_no_ext,
            "file": rel, "sizeBytes": sz, "abs": norm(ap),
        })
    for o in sorted(orphans, key=lambda x: x["file"].lower()):
        ext = os.path.splitext(o["file"])[1].lower()
        if ext != ".png":
            A(Issue("warning", "orphan_non_png", o["file"],
                    "Textures/ 下有非 .png 文件，spec 里也没有任何 Def 引用它（扩展名 %s）"
                    % (ext or "(无)"),
                    {"file": o["abs"], "sizeBytes": o["sizeBytes"], "extension": ext},
                    "游戏只加载 .png；确认后删除"))
            continue
        A(Issue("warning", "orphan_texture", o["file"],
                "Textures/ 下有多余文件，spec 里没有任何 Def 引用它（改名/删 Def 后的残留）",
                {"file": o["abs"], "sizeBytes": o["sizeBytes"]},
                "确认后删除，或把 Def 的 texPath 指回来"))

    # ---------------- E/F：原版索引 ----------------
    vanilla_index, vanilla_paths, manifests = ({}, {}, [])
    if not args.no_collision_scan:
        vanilla_index, vanilla_paths, manifests = load_dlc_manifest_index(args.manifest_dir)

    core_index, core_stat = ({}, {"scanned": 0, "distinct": 0})
    core_dir = args.advisory_core_dir
    if core_dir == "auto":
        core_dir = os.path.join(args.manifest_dir, "Core")
    if core_dir and not args.no_advisory_scan:
        core_index, core_stat = load_core_advisory_index(core_dir)

    # F9：撞原版（DLC manifest，权威）
    # 关键：撞名判定必须**文件级**——Manifest 里 Graphic_Multi 是一个方向一个文件
    # （BioferriteGenerator_north.png 等），所以 mod 的每个实际文件名都要逐个比对。
    collisions_vanilla = []
    for it in items_out:
        keys = []
        if it["variants"]:
            for v in it["variants"]:
                keys.append(v["file"])
        else:
            keys.append(it["texPath"] + ".png")
        for fk in keys:
            rec = vanilla_paths.get(fk.lower())
            if not rec:
                continue
            collisions_vanilla.append({
                "texPath": it["texPath"],
                "collidingFile": rec.get("rel", fk),
                "collidesAsExactFile": True,
                "dlc": rec["dlc"],
                "manifestEntry": rec["entry"],
                "manifest": norm(rec["manifest"]),
                "defName": it["defName"],
            })
        # 路径级（去方向后缀）也要报：manifest 里若只有无后缀单图，同样撞
        rec2 = vanilla_index.get(it["texPath"].lower())
        if rec2 and not any(c["texPath"] == it["texPath"] for c in collisions_vanilla):
            collisions_vanilla.append({
                "texPath": it["texPath"],
                "collidingFile": it["texPath"] + ".png",
                "collidesAsExactFile": True,
                "dlc": rec2["dlc"],
                "manifestEntry": rec2["entry"],
                "manifest": norm(rec2["manifest"]),
                "defName": it["defName"],
            })
    for c in sorted(collisions_vanilla, key=lambda x: (x["texPath"].lower(), x["collidingFile"])):
        A(Issue("error", "collision_vanilla", c["collidingFile"],
                "贴图路径与原版 DLC(%s) 资源撞名 —— 同名路径后加载者胜，可能覆盖原版贴图" % c["dlc"],
                {"manifest": c["manifest"], "manifestEntry": c["manifestEntry"],
                 "defName": c["defName"], "texPath": c["texPath"],
                 "authority": "DLC .manifest（纯文本，权威）",
                 "compareUnit": "实际文件相对路径（含方向后缀）"},
                "换一个 mod 专属命名空间（例如加 <ModName>/ 前缀），或确认你就是想覆盖"))

    # E8：占位符残留 —— mod 自己没出图，texPath 却命中原版资源
    explicitly_allowed = set()
    if args.placeholder_list:
        pl = os.path.abspath(args.placeholder_list)
        if not os.path.isfile(pl):
            die("占位符清单不存在：%s" % pl, 2)
        with open(pl, "rb") as f:
            for line in f.read().decode("utf-8-sig").splitlines():
                s = line.strip()
                if not s or s.startswith("#"):
                    continue
                explicitly_allowed.add(s.lower())
    placeholders = []
    for e in items_out:
        k = e["texPath"].lower()
        shipped = bool(disk.get(k + ".png")) or any(
            disk.get(k + s + ".png") for s in (e.get("requiredSuffixes") or []))
        if shipped:
            continue
        if k in vanilla_index:
            placeholders.append({"texPath": e["texPath"], "kind": "vanilla_dlc",
                                 "evidence": vanilla_index[k]["entry"],
                                 "defName": e["defName"],
                                 "explicitlyAllowed": k in explicitly_allowed})
        elif any((k + s) in vanilla_index for s in DIR_SUFFIXES):
            hit = next(s for s in DIR_SUFFIXES if (k + s) in vanilla_index)
            placeholders.append({"texPath": e["texPath"], "kind": "vanilla_dlc",
                                 "evidence": "%s（同族：%s）"
                                             % (vanilla_index[k + hit]["entry"], k + hit + ".png"),
                                 "defName": e["defName"],
                                 "explicitlyAllowed": k in explicitly_allowed})
        elif k in core_index:
            placeholders.append({"texPath": e["texPath"], "kind": "vanilla_core_advisory",
                                 "evidence": core_index[k], "defName": e["defName"],
                                 "explicitlyAllowed": k in explicitly_allowed})
    suspect = set(p["texPath"] for p in placeholders)
    for e in items_out:
        e["placeholderSuspect"] = e["texPath"] in suspect
    for p in sorted(placeholders, key=lambda x: x["texPath"].lower()):
        if p["explicitlyAllowed"]:
            A(Issue("info", "placeholder_allowlisted", p["texPath"],
                    "已知有意引用原版贴图（在占位符清单里），未出图 —— 不计入发布门禁",
                    {"evidence": p["evidence"], "kind": p["kind"], "defName": p["defName"]},
                    None))
            continue
        lvl = "error" if p["kind"] == "vanilla_dlc" else "warning"
        A(Issue(lvl, "placeholder_residue", p["texPath"],
                "占位符残留：mod 自己 Textures/ 下没有这张图，但 texPath 命中原版资源（%s）"
                "—— 开发期允许，发布前必须为 0" % p["kind"],
                {"evidence": p["evidence"], "kind": p["kind"], "defName": p["defName"],
                 "rule": "发布门禁：占位符残留 = 0"},
                "要么出图放到 mod 的 Textures/ 下，要么把 texPath 改成指向 mod 自己的资源"))

    # F10：撞其它 mod
    collisions_mod = []
    if not args.no_workshop_scan or args.mods_dir:
        other = {}
        scan_dirs = []
        if args.mods_dir and os.path.isdir(args.mods_dir):
            scan_dirs.append(("mods", args.mods_dir, 1))
        if not args.no_workshop_scan and args.workshop_dir and os.path.isdir(args.workshop_dir):
            scan_dirs.append(("workshop", args.workshop_dir, 1))
        for kind, base, depth in scan_dirs:
            entries = sorted([d for d in os.listdir(base)
                              if os.path.isdir(os.path.join(base, d))], key=str.lower)
            for d in entries:
                mod_dir = os.path.join(base, d)
                if os.path.normcase(os.path.abspath(mod_dir)) == os.path.normcase(mod_root):
                    continue
                for sr in (mod_dir,):
                    td = os.path.join(sr, "Textures")
                    if not os.path.isdir(td):
                        continue
                    for dp, dn, fn in os.walk(td):
                        dn.sort()
                        for f in sorted(fn):
                            if not f.lower().endswith(".png"):
                                continue
                            rel = os.path.relpath(os.path.join(dp, f), td).replace("\\", "/")
                            key = rel[:-4].lower()
                            other.setdefault(key, {"mod": d, "kind": kind,
                                                   "abs": norm(os.path.join(dp, f))})
        for it in items_out:
            k = it["texPath"].lower()
            if k in other:
                collisions_mod.append({"texPath": it["texPath"], "otherMod": other[k]["mod"],
                                       "kind": other[k]["kind"], "otherFile": other[k]["abs"],
                                       "defName": it["defName"]})
        for c in sorted(collisions_mod, key=lambda x: (x["texPath"].lower(), x["otherMod"])):
            A(Issue("info", "collision_mod", c["texPath"],
                    "与已装 mod(%s) 的贴图路径同名 —— 后加载者胜，需要人确认是否互相覆盖"
                    % c["otherMod"],
                    {"otherMod": c["otherMod"], "offendingFile": c["otherFile"],
                     "defName": c["defName"], "scope": c["kind"]},
                    "若非有意覆盖，改 mod 专属命名空间"))

    # ---------------- 汇总 ----------------
    counts = collections.Counter(i.level for i in issues)
    by_code = collections.Counter(i.code for i in issues)
    issues_sorted = sorted(issues, key=lambda i: (LEVEL_ORDER.get(i.level, 9), i.code, i.subject.lower()))
    ok = counts.get("error", 0) == 0

    report = {
        "reportVersion": REPORT_VERSION,
        "validator": "tools/validate-art-spec.py",
        "spec": {
            "path": norm(spec_path),
            "specVersion": spec.get("specVersion"),
            "modName": spec.get("modName"),
            "packageId": spec.get("packageId"),
            "gameVersion": game_version,
            "pxPerCell": px_per_cell,
            "pxPerCellNote": spec.get("pxPerCellNote"),
            "inputDigestSha256": spec.get("inputDigestSha256"),
            "itemCount": len(spec.get("items", [])),
        },
        "mod": {
            "root": norm(mod_root),
            "activeTextureRoots": [norm(os.path.join(r, "Textures")) for r in roots
                                   if os.path.isdir(os.path.join(r, "Textures"))],
            "textureFilesOnDisk": len(all_files),
        },
        "config": {
            "transparentLuminanceThreshold": args.transparent_lum_threshold,
            "whiteFringeWarnThreshold": args.white_fringe_error_threshold,
            "strictSize": bool(args.strict_size),
            "allowSizeMismatch": bool(args.allow_size_mismatch),
            "pixelScan": not args.no_pixel_scan,
        },
        "vanillaIndex": {
            "dlcManifests": manifests,
            "dlcManifestTexPaths": len(vanilla_index),
            "coreAdvisory": core_stat,
            "coreAdvisoryNote": ("Core 没有 .manifest（贴图在 Unity resources.assets 里，无明文路径），"
                                 "这里用 Core/Defs 的 <texPath> 做**辅证**，不是权威依据，"
                                 "对 UI 贴图/程序化路径会漏检"),
        },
        "summary": {
            "pass": ok,
            "errors": counts.get("error", 0),
            "warnings": counts.get("warning", 0),
            "measures": counts.get("measure", 0),
            "infos": counts.get("info", 0),
            "itemsTotal": len(items_out),
            "itemsMissingAllVariants": any_missing,
            "orphanFiles": len(orphans),
            "placeholderResidue": len([p for p in placeholders if not p["explicitlyAllowed"]]),
            "placeholderAllowlisted": len([p for p in placeholders if p["explicitlyAllowed"]]),
            "collisionsVanilla": len([c for c in collisions_vanilla]),
            "collisionsOtherMods": len(collisions_mod),
            "byCode": dict(sorted(by_code.items())),
        },
        "gate": {
            "placeholderResidue": len([p for p in placeholders if not p["explicitlyAllowed"]]),
            "collisionsVanilla": len(collisions_vanilla),
            "releaseBlocked": (len([p for p in placeholders if not p["explicitlyAllowed"]]) > 0
                               or len(collisions_vanilla) > 0
                               or counts.get("error", 0) > 0),
            "rules": ["占位符残留 = 0", "撞原版 = 0", "error = 0"],
        },
        "items": items_out,
        "orphans": sorted(orphans, key=lambda x: x["file"].lower()),
        "placeholderResidue": sorted(placeholders, key=lambda x: x["texPath"].lower()),
        "collisionsVanilla": sorted(collisions_vanilla, key=lambda x: x["collidingFile"].lower()),
        "collisionsOtherMods": sorted(collisions_mod, key=lambda x: (x["texPath"].lower(), x["otherMod"])),
        "issues": [i.to_json() for i in issues_sorted],
    }

    # 生成循环待办：机器可读，模型下一步照着重画
    todo = {"reportVersion": REPORT_VERSION, "tool": "tools/validate-art-spec.py",
            "generatedFrom": norm(spec_path), "targets": []}
    seen_todo = set()
    for i in issues_sorted:
        if i.level not in ("error", "warning"):
            continue
        tk = (i.subject, i.code)
        if tk in seen_todo:
            continue
        seen_todo.add(tk)
        todo["targets"].append({
            "texPathOrFile": i.subject,
            "code": i.code,
            "severity": i.level,
            "expected": i.evidence.get("expected") or i.evidence.get("target"),
            "actual": i.evidence.get("actual"),
            "evidence": i.evidence,
            "fixHint": i.fixHint,
        })
    todo["errorCount"] = counts.get("error", 0)
    todo["warningCount"] = counts.get("warning", 0)
    todo["done"] = counts.get("error", 0) == 0
    todo["roundHint"] = ("错误清单为空时不需要重生成；warning 可按需处理（例如尺寸约定、"
                         "方向补齐、白边清理）。发布门禁另见报告 gate 字段。")
    todo["convergenceRule"] = "gate.releaseBlocked == false 且 summary.errors == 0"
    todo["byCode"] = dict(sorted(collections.Counter(
        t["code"] for t in todo["targets"]).items()))
    todo["targetsByTexPath"] = {}
    for t in todo["targets"]:
        todo["targetsByTexPath"].setdefault(t["texPathOrFile"], []).append({
            "code": t["code"], "severity": t["severity"],
            "expected": t["expected"], "actual": t["actual"], "fixHint": t["fixHint"]})

    if args.json_out:
        p = os.path.abspath(args.json_out)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "w", encoding="utf-8", newline="\n") as f:
            f.write(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    if args.todo_out:
        p = os.path.abspath(args.todo_out)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "w", encoding="utf-8", newline="\n") as f:
            f.write(json.dumps(todo, ensure_ascii=False, indent=2) + "\n")

    # ---------------- 人类可读 ----------------
    if not args.quiet:
        print("=" * 78)
        print("贴图校验报告   mod=%s" % norm(mod_root))
        print("spec=%s  items=%d  pxPerCell=%d" % (norm(spec_path), len(items_out), px_per_cell))
        print("active Textures 根: %s" % (", ".join(norm(os.path.join(r, "Textures")) for r in roots
                                                   if os.path.isdir(os.path.join(r, "Textures")))
                                          or "(无 Textures 目录)"))
        print("磁盘上 PNG 文件数: %d" % len(all_files))
        print("原版 DLC manifest texPaths: %d   Core Defs 辅证 texPaths: %d"
              % (len(vanilla_index), core_stat.get("distinct", 0)))
        print("-" * 78)
        print("结论: %s   error=%d warning=%d measure=%d info=%d"
              % ("PASS" if ok else "FAIL", counts.get("error", 0), counts.get("warning", 0),
                 counts.get("measure", 0), counts.get("info", 0)))
        print("发布门禁: 占位符残留=%d  撞原版=%d  → %s"
              % (report["gate"]["placeholderResidue"], len(collisions_vanilla),
                 "BLOCKED" if report["gate"]["releaseBlocked"] else "CLEAR"))
        print("-" * 78)
        print("契约逐项:")
        for e in items_out:
            mark = {"ok": "OK  ", "warning": "WARN", "error": "ERR "}.get(e["verdict"], "    ")
            tgt = "%dx%d" % (e["targetPixels"]["w"], e["targetPixels"]["h"]) if e["targetPixels"] else "-"
            ex = "".join("%s:%s " % (v["suffix"] or "(base)", "有" if v["exists"] else "缺")
                         for v in e["variants"])
            print("  [%s] %-46s target=%-9s %s" % (mark, e["texPath"][:46], tgt, ex))
            for v in e["variants"]:
                if v.get("png"):
                    pi = v["png"]
                    print("         %s: %s %sx%s mode=%s alpha=%s 透明像素=%.2f%% 亮透明占比=%s 白边风险=%s"
                          % (v["suffix"] or "(base)", v["format"],
                             pi.get("headerWidth"), pi.get("headerHeight"), pi.get("mode"),
                             pi.get("hasAlphaChannel"),
                             (pi.get("transparentPixelRatio") or 0) * 100,
                             ("%.4f" % pi["transparentHighLuminanceRatio"])
                             if pi.get("transparentHighLuminanceRatio") is not None else "-",
                             white_fringe_level(pi.get("transparentHighLuminanceRatio"))))
        if orphans:
            print("")
            print("多余文件 (orphan) %d 个:" % len(orphans))
            for o in report["orphans"]:
                print("  - %s (%d bytes)" % (o["file"], o["sizeBytes"]))
        if placeholders:
            print("")
            print("占位符残留 %d 个:" % report["gate"]["placeholderResidue"])
            for p in report["placeholderResidue"]:
                if p.get("explicitlyAllowed"):
                    continue
                print("  - %s  [%s]" % (p["texPath"], p["kind"]))
        if collisions_vanilla:
            print("")
            print("撞原版 %d 个:" % len(collisions_vanilla))
            for c in report["collisionsVanilla"]:
                print("  - %s  (DLC=%s)" % (c["collidingFile"], c["dlc"]))
        if collisions_mod:
            print("")
            print("与其它 mod 同名 %d 个:" % len(collisions_mod))
            for c in report["collisionsOtherMods"]:
                print("  - %s  (mod=%s)" % (c["texPath"], c["otherMod"]))
        print("")
        print("-" * 78)
        print("问题清单 (error → warning → measure → info):")
        for i in issues_sorted:
            print("  [%-7s] %-34s %s" % (i.level, i.code, i.subject))
            print("             %s" % i.message)
        print("-" * 78)
        print("退出码: %d      (JSON: %s)" % (0 if ok else 1, args.json_out or "(未指定)"))
        print("=" * 78)

    if args.json_out:
        sys.stderr.write("JSON 报告: %s\n" % norm(os.path.abspath(args.json_out)))
    if args.todo_out:
        sys.stderr.write("待办清单: %s\n" % norm(os.path.abspath(args.todo_out)))

    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
