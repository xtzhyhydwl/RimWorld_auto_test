#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
坏样本生成器（只用来验证校验器能不能抓住问题）

在 tmp/brokensample/ 下造一个假 mod，每个缺陷都是**故意**的、**确定性**的（无随机）。
所有像素由纯数学生成，重跑结果完全一致。

样本清单见 tmp/brokensample/README.txt
"""
import os
from PIL import Image

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tmp", "brokensample")
ROOT = os.path.abspath(ROOT)


def ensure(p):
    os.makedirs(os.path.dirname(p), exist_ok=True)


def write_png(rel, w, h, fn, mode="RGBA"):
    p = os.path.join(ROOT, rel)
    ensure(p)
    im = Image.new(mode, (w, h))
    px = im.load()
    for y in range(h):
        for x in range(w):
            px[x, y] = fn(x, y)
    im.save(p, "PNG")
    return p


def main():
    # 幂等：先清掉本脚本此前的产物，避免上一轮的残留影响"确定性"
    stale1 = os.path.join(ROOT, "Textures", "Things", "Building", "BioferriteRefinery")
    if os.path.isdir(stale1):
        for f in sorted(os.listdir(stale1)):
            os.remove(os.path.join(stale1, f))
        os.rmdir(stale1)

    # --- S1 ok_door：干净样本。128x128，透明区 RGB 全 0，边缘干净 ---
    def s1(x, y):
        cx, cy = 64, 64
        inside = (x - cx) ** 2 + (y - cy) ** 2 < 50 ** 2
        return (180, 140, 90, 255) if inside else (0, 0, 0, 0)

    write_png("Textures/Sample/OkDoor/OkDoor.png", 128, 128, s1)

    # --- S2 jpg_masquerade：真 JPEG，扩展名 .png（游戏解码失败 → 紫黑格）---
    p = os.path.join(ROOT, "Textures/Sample/JpgMasquerade/JpgMasquerade.png")
    ensure(p)
    im = Image.new("RGB", (128, 128))
    px = im.load()
    for y in range(128):
        for x in range(128):
            px[x, y] = ((x * 2) % 256, (y * 2) % 256, 128)
    im.save(p, "JPEG", quality=90)

    # --- S3 multi_no_north：只有 _south/_east/_west（无 _north 无回退）→ error ---
    def s3(x, y):
        return (90, 90, 200, 255) if (x // 16 + y // 16) % 2 == 0 else (0, 0, 0, 0)

    for suf in ("_south", "_east", "_west"):
        write_png("Textures/Sample/MultiNoNorth/MultiNoNorth%s.png" % suf, 64, 64, s3)

    # --- S4 multi_partial：只有 _north → warning（其它方向可回退）---
    write_png("Textures/Sample/MultiPartial/MultiPartial_north.png", 64, 64, s3)

    # --- S5 white_fringe：透明像素 RGB 残留高亮（160..255）→ warning ---
    def s5(x, y):
        if (x - 64) ** 2 + (y - 64) ** 2 < 40 ** 2:
            return (200, 160, 120, 255)
        return (240 + (x % 16), 235 + (y % 16), 250, 0)  # 全透明但 RGB 很亮

    write_png("Textures/Sample/WhiteFringe/WhiteFringe.png", 128, 128, s5)

    # --- S6 no_alpha：RGB 无 alpha 通道 ---
    write_png("Textures/Sample/NoAlpha/NoAlpha.png", 128, 128,
              lambda x, y: ((x * 3) % 256, (y * 3) % 256, 60), mode="RGB")

    # --- S7 wrong_size：63x63（非 2 的幂、非 4 倍数、与 128 契约不符）---
    write_png("Textures/Sample/WrongSize/WrongSize.png", 63, 63,
              lambda x, y: (60, 120, 180, 255))

    # --- S8 orphan：spec 里没有任何 Def 引用它 ---
    write_png("Textures/Sample/LegacyOrphan/LegacyOrphan.png", 64, 64,
              lambda x, y: (255, 0, 0, 255))

    # --- S8b 非 png 干扰文件：Textures 下不该有 .txt ---
    p = os.path.join(ROOT, "Textures/Sample/LegacyOrphan/readme.txt")
    ensure(p)
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write("这是故意放的非 png 干扰文件，用来验证 orphan_non_png。\n")

    # --- S9 missing：Defs 里引用了，但磁盘上没有 ---
    # （见 Defs/BrokenSamples.xml 的 MissingTexture）

    # --- S10 vanilla_collide：撞原版 DLC manifest（Anomaly 的 BioferriteGenerator，真存在于 manifest）---
    # 磁盘上**故意**放一张同名图，证明"撞名"与"是否出图"无关
    remote = "Things/Building/BioferriteGenerator/BioferriteGenerator"
    write_png("Textures/%s_east.png" % remote, 128, 128,
              lambda x, y: (30, 30, 30, 255))
    for suf in ("_north", "_south", "_west"):
        write_png("Textures/%s%s.png" % (remote, suf), 128, 128,
                  lambda x, y: (30, 30, 30, 255))

    # --- S11 core_placeholder：texPath 指向 Core/Defs 里存在的原版贴图，自己没出图 ---
    # （见 Defs/BrokenSamples.xml 的 CorePlaceholderDoor，指向 Things/Building/Door/DoorSimple_Mover）

    # --- S12 loadfolder：放在 1.6/Textures 里，验证 LoadFolders 多根发现 ---
    write_png("1.6/Textures/Sample/VersionedDoor/VersionedDoor.png", 128, 128,
              lambda x, y: (120, 200, 120, 255))

    # --- S13 dlc_placeholder：texPath 指向原版 Anomaly 贴图，自己**没出图** ---
    # （见 Defs/BrokenSamples.xml 的 S_DlcPlaceholder，指向 Things/Building/BioferriteRefinery/BioferriteRefinery）
    # 磁盘上明确不放文件 → 期望 error: placeholder_residue (vanilla_dlc)
    legacy = os.path.join(ROOT, "Textures/Things/Building/BioferriteRefinery")
    if os.path.isdir(legacy):
        for f in os.listdir(legacy):
            os.remove(os.path.join(legacy, f))

    print("坏样本已生成：%s" % ROOT.replace("\\", "/"))


if __name__ == "__main__":
    main()
