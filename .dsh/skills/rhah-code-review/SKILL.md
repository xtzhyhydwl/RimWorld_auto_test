---
name: rhah-code-review
description: RHAH（鼠族：饥与祸，RimworldMods/ratkin_hunger_and_havoc）改动提交前的审核：四道检查（1.7/1.6 构建、pipeline、分层、命名）+ 一段可直接抄的评审派单。只适用这一个 mod。
---

# RHAH 代码审核

`$WS` = `E:\2026.7以后的新工程\DSH\RimWorld_auto_test`；`$MOD` = `$WS\RimworldMods\ratkin_hunger_and_havoc`

## 一、四道检查（缺一条都不算审过）

| # | 检查 | 怎么做 | 判据 |
|---|---|---|---|
| 1 | 1.7 构建 | 下面命令块前两条 | **0 错误**（警告只与上次比，不追新） |
| 2 | 1.6 构建 | 下面命令块后两条 | **0 错误** |
| 3 | 仓库管线 | `cd "$MOD"; python scripts\rahh_pipeline.py check` | **exit 0**；红了就按它输出的 ` - 原因` 逐条改 |
| 4 | 分层 + 命名 | 派评审子代理（见二） | 报告 B / C 段没有"必修" |

> `dotnet test` 在本机跑不起来（testhost `OpenProcess` 被拒）⇒ 单测只能验到"能编译"，结论里别写"单测绿"。

```powershell
$WS='E:\2026.7以后的新工程\DSH\RimWorld_auto_test'; $RW='D:\SteamLibrary\steamapps\common\RimWorld'
cd "$WS\RimworldMods"                                    # cwd 必须在这：csproj 的相对路径按它找
$c17=@('-c','Release','--nologo','-v','m','-p:RimWorldVersion=1.7',"-p:RimWorldDir=$RW","-p:RimWorldManagedDir=$RW",
  "-p:OxygenDslDir=$WS\RimworldMods\oxygen_dsl_framework","-p:OxygenDslBuildDir=$WS\runs\tmp\odf17\out",
  '-p:HarmonyPath=D:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll',
  "-p:IrisMenusDll=$WS\runs\tmp\iris17\IrisMenus.dll")
dotnet build ratkin_hunger_and_havoc\Source\HungerAndHavoc.csproj @c17
dotnet build ratkin_hunger_and_havoc\Source\Tests\HungerAndHavoc.Tests.csproj @c17

$c16=@('-c','Release','--nologo','-v','m','-p:RimWorldVersion=1.6','-p:RimWorldDir=D:\RimWorld-1.6',
  '-p:RimWorldManagedDir=D:\RimWorld-1.6\RimWorldWin64_Data\Managed',"-p:OxygenDslDir=$WS\RimworldMods\oxygen_dsl_framework",
  "-p:OxygenDslBuildDir=$WS\runs\tmp\odf16\out",
  '-p:HarmonyPath=D:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.6\Assemblies\0Harmony.dll',
  "-p:IrisMenusDll=$WS\runs\tmp\iris16\IrisMenus.dll")    # 1.6 必须 iris16，用 iris17 报 3 个 CS0012
dotnet build ratkin_hunger_and_havoc\Source\HungerAndHavoc.csproj @c16
dotnet build ratkin_hunger_and_havoc\Source\Tests\HungerAndHavoc.Tests.csproj @c16
```

## 二、派评审子代理（照抄，只填 `<>`）

用 **`subagent`**（全新上下文，**不要** `subagent_fork`）。构建和 pipeline 由你在派单前跑完，把结果当既定事实写进去。

```text
你在 $WS 下给 RimworldMods\ratkin_hunger_and_havoc 做只读代码评审。你看不到任何先前对话，以下事实由主会话提供：
- 评审范围：<分支 / HEAD / 未提交改动>；`git status --short` = <列表>
- 已跑：1.7 与 1.6 构建 <0 错误>；`rhah_pipeline.py check` <exit 0>

纪律（违反即作废）：只读；除报告外不改任何文件；不起 RimWorld；不写任何一次性脚本；不跑构建。
判据来源：根 AGENTS.md、RimworldMods\AGENTS.md、该 mod 的 AGENTS.md 与 Source\ 各子目录 AGENTS.md。

只查两件事：
1) 分层：新代码有没有破坏既有模块边界（Source/<领域>/ 各管各的），有没有把上层决策塞进下层、同一概念实现两遍、新增"下层→上层"的反向依赖（先 rg 有没有先例，0 先例就别开）。
2) 命名：新标识符名副其实吗；有没有 Can*/Able* 这种同义重复、一个概念多个名字；**重点抓语义漂移**——迭代中语义变了、名字没跟上的。

产出：写到 $WS\docs\<票号>-代码评审-<日期>.md，六段：A 结论(≤3行) / B 必修·分层 / C 必修·命名 / D 建议 / E 可不改 / F 实测 vs 推测。
每条给：`文件:行` + 现状 + 为什么错 + 建议改法一句 + 是否必修。F 段把"查过的"和"只是推断的"分开列，别混。
回报 ≤200 字：报告路径 + A 段结论。
```

## 三、改与复验

1. 主会话逐条给 **采纳 / 不采纳 / 改判**（产品口径由你定），写进 `$WS\runs\<日期>-<票名>-review\fix-plan.md`。
2. 派**另一个** `subagent`（与评审代理不共享上下文）按 fix-plan 改：只改列出的文件、不许"顺手优化"。
3. **重跑四道检查**，全过才 amend；不过就回第 1 步。提交信息见 skill `git-commit-message`。
