---
name: git-commit-message
description: 写 git 提交信息时套固定模板：`type[scope]: 中文短句` 标题 + 三段人话正文（为什么改 / 怎么改 / 现在的效果）。要提交、amend、拆票、自查提交信息时使用。
---

# git 提交信息模板

**用途**：任何提交都套这一套模板；判据只有一条——玩家/非技术读者能读懂，技术细节去看代码。

## 一、标题：`type[scope]: 中文短句`

| 项 | 规则 |
|---|---|
| type | `fix` 玩家能感知的问题；`feat` 新功能；`docs` 只改文档；`build` 只重编译；`deploy` 发版；`chore` 杂务 |
| scope | **只能用仓库历史里出现过的词**，不造新词 |
| 短句 | 中文，祈使或陈述皆可，**不带句号**，**整条 ≤60 字符**（中文按 1 字符算）；括号一律方括号 `[]` |
| 示例 | `fix[cli]: 启动子进程的工作目录改为游戏根` |

scope 拿不准先统计（PowerShell，PS 5.1 可用）：

```powershell
git log --format=%s | ForEach-Object { if ($_ -match '^[a-z]+[\[(]([^\]\)]+)[\]\)]:') { $matches[1] } } | Group-Object | Sort-Object Count -Descending
```

词表（`git log` 实测于 2026-10-25，作起点；仍以实测为准，读不到就自己统计）：

- 本工程 `RimWorld_auto_test`：`cli` `skills` `status` `pr流程` `gitignore` `mods` `回归`
- 作者仓库 `RimworldMods/`：`rhah` `events` `iris-menus` `ratkin-recycle` `irismenus` `prisonernames` `ui`
- 复合 scope 用 `/` 连：`rhah/events`（两半都得是历史里出现过的词）

## 二、正文：三段人话（每段 1–3 行，段间空一行）

```text
为什么要改：<玩家/使用者能感知的问题，不写实现细节>

怎么改：<人话描述改了什么，允许一句技术要点>

现在的效果：<改完后的表现 + 验证范围：哪个版本、哪些场景跑过>
```

**人话铁律**（违反即返工）：

1. 正文不出现类名、方法名、变量名、字段名、框架名、文件名、分支名、坐标、性能数字、tick 数、HP 数值。
2. 用自然语言指代对象：「来客 / 门 / 墙 / 设置项 / 商队 / 访客」；不许出现 `Pawn`（类名）、`ModSettings`（字段名）、`Harmony`（框架名）这类词。
3. 只允许版本号（1.6 / 1.7）和「实跑验证过」这类结论；没跑过就写「只过了构建」。

## 三、一票一提交与 amend

一个 ticket 对应一个提交。唯一允许拆分的理由：每组改动能**各自通过构建**（便于 bisect）。

```powershell
$m="$env:TEMP\commit-msg.txt"   # ① 用 write 工具写 message：UTF-8 无 BOM、纯 LF
git add -A                      # ② 暂存这次的全部改动
git commit -F $m                # ③ 提交：不用 -m（PS 5.1 传中文会写坏编码）
git log -1 --format=%B          # ④ 校验：三段齐全、无黑话
git log --oneline -1            #    校验：标题 ≤60 字符、scope 在词表里
Remove-Item $m                  #    收工删临时文件
```

改历史只走 amend、不新增提交：message 重写到 `$m`，再 `git commit --amend -F $m`，之后同样删掉 `$m`。

## 四、自检清单（5 条全 yes 才提交）

1. 标题整条 ≤60 字符？
2. scope 是历史出现过的词？
3. 三段齐全、段间空一行？
4. 正文无黑话、无数字堆砌？
5. 一个 ticket 一个提交？

## 五、判定失败的典型

- `fix: 修了个 bug` —— 无 scope、无信息。
- 正文用 `Why / What / Now` 英文小标题 —— 模板段名被换掉，检索与一致性都断。
- 正文堆类名、字段名与一堆数字 —— 非技术读者读不懂，等于没写。

## 六、范例与反例

本 skill 目录下 `docs/范例集-20261025.md`：真实正例、短正例、三条反例及判词。拿不准就照抄结构，不现场发挥。
