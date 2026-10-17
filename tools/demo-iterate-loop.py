#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
迭代循环演示：一个"故意出坏图"的假模型 + 校验器 + 按结构化失败清单重生成。

用途：证明 docs/贴图流水线-设计.md 里那条循环真的跑得通——
  模型出图（这里用确定性坏图代替）→ 校验器给结构化 JSON → 按 JSON 重生成 → 直到 0 error

产物：
  tmp/holdout/                    假 mod（Defs + Textures）
  tmp/holdout-art-spec.json       契约（由 gen-art-spec.py 生成）
  tmp/holdout-round1-report.json  第 1 轮校验（应 FAIL）
  tmp/holdout-round2-report.json  第 2 轮校验（应 PASS）
  tmp/holdout-LOOP-LOG.txt        两轮的真实命令与关键输出

用法：
  python tools/demo-iterate-loop.py
"""
import json
import os
import shutil
import subprocess
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
WS = os.path.abspath(os.path.join(HERE, ".."))
ROOT = os.path.join(WS, "tmp", "holdout")
PY = sys.executable

# 假 mod 的契约：两张贴图
#   Holdout/Crate          Graphic_Single  1x1 → 128x128，要求透明区 RGB 全 0
#   Holdout/Turret         Graphic_Multi   2x2 → 256x256，四个方向
TARGETS = [
    {"texPath": "Holdout/Crate", "size": (128, 128), "suffixes": []},
    {"texPath": "Holdout/Turret", "size": (256, 256),
     "suffixes": ["_north", "_east", "_south", "_west"]},
]

DEFS = '''<?xml version="1.0" encoding="utf-8"?>
<Defs>
  <ThingDef>
    <defName>Holdout_Crate</defName>
    <graphicData>
      <texPath>Holdout/Crate</texPath>
      <graphicClass>Graphic_Single</graphicClass>
      <drawSize>(1,1)</drawSize>
    </graphicData>
  </ThingDef>
  <ThingDef>
    <defName>Holdout_Turret</defName>
    <graphicData>
      <texPath>Holdout/Turret</texPath>
      <graphicClass>Graphic_Multi</graphicClass>
      <drawSize>(2,2)</drawSize>
    </graphicData>
  </ThingDef>
</Defs>
'''

ABOUT = '''<?xml version="1.0" encoding="utf-8"?>
<ModMetaData>
  <name>Holdout (迭代循环演示)</name>
  <author>dsh-artpipe</author>
  <packageId>dsh.artpipe.holdout</packageId>
  <supportedVersions><li>1.6</li></supportedVersions>
</ModMetaData>
'''


def write_png(rel, w, h, fn, mode="RGBA"):
    p = os.path.join(ROOT, rel)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    im = Image.new(mode, (w, h))
    px = im.load()
    for y in range(h):
        for x in range(w):
            px[x, y] = fn(x, y)
    im.save(p, "PNG")


def round1():
    """假模型的第 1 轮输出：故意犯两类错。
       Crate : 尺寸对但透明区 RGB 残留高亮（白边根因），且带 XMP 元数据
       Turret: 尺寸错（96 而不是 256），且只出 2 个方向
    """
    write_png("Textures/Holdout/Crate.png", 128, 128,
              lambda x, y: (170, 120, 70, 255) if (x - 64) ** 2 + (y - 64) ** 2 < 44 ** 2
              else (250, 248, 255, 0))          # 透明区 RGB 高亮 → 白边风险

    for suf in ("_north", "_west"):
        write_png("Textures/Holdout/Turret%s.png" % suf, 96, 96,
                  lambda x, y: (80, 90, 210, 255) if (x - 48) ** 2 + (y - 48) ** 2 < 30 ** 2
                  else (0, 0, 0, 0))


def round2(todo):
    """按校验器给的 todo 重生成：尺寸取 todo.target，透明区 RGB 清零，补齐方向。"""
    fixes = {t["texPathOrFile"]: t for t in todo.get("targets", [])}

    # Crate：尺寸已对，只需清掉透明区 RGB
    write_png("Textures/Holdout/Crate.png", 128, 128,
              lambda x, y: (170, 120, 70, 255) if (x - 64) ** 2 + (y - 64) ** 2 < 44 ** 2
              else (0, 0, 0, 0))

    # Turret：按 todo 里的 targetPixels 出 256x256，四个方向齐全
    for suf in ("_north", "_east", "_south", "_west"):
        write_png("Textures/Holdout/Turret%s.png" % suf, 256, 256,
                  lambda x, y: (80, 90, 210, 255) if (x - 128) ** 2 + (y - 128) ** 2 < 80 ** 2
                  else (0, 0, 0, 0))
    return fixes


def run(cmd, log):
    log.append("$ " + " ".join('"%s"' % c if " " in c else c for c in cmd))
    r = subprocess.run(cmd, capture_output=True, cwd=WS)
    out = r.stdout.decode("utf-8", "replace")
    err = r.stderr.decode("utf-8", "replace")
    log.append(out.rstrip())
    if err.strip():
        log.append("[stderr] " + err.rstrip())
    log.append("[exit=%d]" % r.returncode)
    log.append("")
    return r.returncode, out, err


def main():
    if os.path.isdir(ROOT):
        shutil.rmtree(ROOT)
    os.makedirs(os.path.join(ROOT, "Defs"), exist_ok=True)
    os.makedirs(os.path.join(ROOT, "About"), exist_ok=True)
    with open(os.path.join(ROOT, "Defs", "Holdout.xml"), "w", encoding="utf-8", newline="\n") as f:
        f.write(DEFS)
    with open(os.path.join(ROOT, "About", "About.xml"), "w", encoding="utf-8", newline="\n") as f:
        f.write(ABOUT)

    log = []
    log.append("=== 迭代循环演示（确定性，无随机）===")
    log.append("")

    # 契约（一次性生成，之后不再变）
    spec = os.path.join(WS, "tmp", "holdout-art-spec.json")
    run([PY, os.path.join(HERE, "gen-art-spec.py"), "--mod", ROOT, "--out", spec, "--quiet"], log)

    # ---- 第 1 轮：模型出一堆坏图 ----
    log.append("--- 第 1 轮：模型出图（故意坏）---")
    round1()
    r1 = os.path.join(WS, "tmp", "holdout-round1-report.json")
    t1 = os.path.join(WS, "tmp", "holdout-round1-todo.json")
    code1, out1, _ = run([PY, os.path.join(HERE, "validate-art-spec.py"),
                          "--spec", spec, "--mod", ROOT,
                          "--strict-size",          # 演示用：把尺寸不符当 error（项目可选）
                          "--json-out", r1, "--todo-out", t1], log)

    with open(r1, encoding="utf-8") as f:
        rep1 = json.load(f)
    log.append("第1轮汇总: error=%d warning=%d  → %s"
               % (rep1["summary"]["errors"], rep1["summary"]["warnings"],
                  "FAIL" if not rep1["summary"]["pass"] else "PASS"))

    with open(t1, encoding="utf-8") as f:
        todo = json.load(f)
    log.append("第1轮待办（机器可读 JSON 的摘要）:")
    for t in todo["targets"]:
        log.append("   [%s] %-16s expected=%s actual=%s"
                   % (t["severity"], t["code"], t["expected"], t["actual"]))
    log.append("")

    # ---- 第 2 轮：按 todo 重生成 ----
    log.append("--- 第 2 轮：按结构化待办重生成 ---")
    round2(todo)
    r2 = os.path.join(WS, "tmp", "holdout-round2-report.json")
    t2 = os.path.join(WS, "tmp", "holdout-round2-todo.json")
    code2, out2, _ = run([PY, os.path.join(HERE, "validate-art-spec.py"),
                          "--spec", spec, "--mod", ROOT,
                          "--strict-size",
                          "--json-out", r2, "--todo-out", t2], log)
    with open(r2, encoding="utf-8") as f:
        rep2 = json.load(f)
    log.append("第2轮汇总: error=%d warning=%d  → %s"
               % (rep2["summary"]["errors"], rep2["summary"]["warnings"],
                  "FAIL" if not rep2["summary"]["pass"] else "PASS"))
    log.append("")
    log.append("=== 循环收敛：第1轮 exit=%d → 第2轮 exit=%d ===" % (code1, code2))
    log.append("")

    # 关键输出片段（避免日志过长）
    def excerpt(text, pats):
        keep = []
        for line in text.splitlines():
            if any(p in line for p in pats):
                keep.append(line.rstrip())
        return keep

    log.append("--- 第 1 轮关键输出 ---")
    log.extend(excerpt(out1, ["结论:", "发布门禁:", "size_mismatch", "白边", "missing_direction",
                             "white_fringe", "png_metadata", "退出码:"]))
    log.append("")
    log.append("--- 第 2 轮关键输出 ---")
    log.extend(excerpt(out2, ["结论:", "发布门禁:", "退出码:"]))
    log.append("")

    lp = os.path.join(WS, "tmp", "holdout-LOOP-LOG.txt")
    with open(lp, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(log) + "\n")
    print("\n".join(log))
    print("日志: %s" % lp.replace("\\", "/"))
    return 0 if code2 == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
