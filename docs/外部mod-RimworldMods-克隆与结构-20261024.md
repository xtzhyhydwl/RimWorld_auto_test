# 外部 Mod 仓库 RimworldMods：HTTPS 克隆实测与结构摸底

- 执行时间：2026-10-24（Asia/Shanghai）
- 执行机器：本机 Windows，PowerShell（pwsh）
- 工作目录：`E:\2026.7以后的新工程\DSH\RimWorld_auto_test`
- 克隆落地：`E:\2026.7以后的新工程\DSH\RimWorld_auto_test\RimworldMods`
- 源地址（HTTPS）：`https://github.com/Nanaloveyuki/RimworldMods.git`

本文所有结论均为本机实测；凡是没跑过的在文中标「未验证」。

---

## 1. 核心结论（先看这段）

**HTTPS 克隆成功。** 但有一个关键前提：**本机 shell 里带着本地代理环境变量**（`http_proxy` = `https_proxy` = `http://127.0.0.1:7897`），HTTPS 流量是走这个代理出去的。

把代理变量清空后再访问同一个 HTTPS 地址，**TCP 层直接连不上**：

```
fatal: unable to access 'https://github.com/Nanaloveyuki/RimworldMods.git/': Failed to connect to
github.com port 443 after 21089 ms: Couldn't connect to server
（exit 128）
```

所以准确表述是：

| 场景 | 结果 | 判定 |
| --- | --- | --- |
| HTTPS + 本地代理 127.0.0.1:7897 | **克隆成功**，exit 0，390 个提交 | 可用 |
| HTTPS 不走代理（清空代理变量） | 连不上 `github.com:443` | 被阻断（直连不可用） |

用户「平时用 SSH」的习惯在这台机器上依然是更省事的一条路；HTTPS **能用，但依赖本地代理在跑**。代理不启动时 HTTPS 会失败，且失败不是认证问题、不是证书问题，是**连不上端口**。

---

## 2. 分步实测记录

### 2.1 `git --version`

```
git version 2.41.0.windows.1
```

可用。

### 2.2 HTTPS 克隆（首次即为最终命令）

执行（workdir = 工作目录，未预先建 `RimworldMods` 目录——实测 `Test-Path RimworldMods` = `False`）：

```powershell
$env:GIT_TERMINAL_PROMPT=0
git -c credential.interactive=never clone https://github.com/Nanaloveyuki/RimworldMods.git RimworldMods
```

结果：**成功，`$LASTEXITCODE = 0`**。

输出摘要：

```
Cloning into 'RimworldMods'...
remote: Enumerating objects: ...
Updating files: 100% (2022/2022), done.
```

补充：pwsh 把 git 的 stderr 显示成 `NativeCommandError` 红字（`git : Cloning into ...`），这只是 PowerShell 对 stderr 的渲染方式，**不是错误**；真正的判据是退出码 0，且目录内容完整（见第 3 节）。

- 全程**没有弹出任何用户名/密码提示**（`GIT_TERMINAL_PROMPT=0` + `credential.interactive=never` 都已生效）。
- 本机**没有配置 credential.helper**（`git config --global --get credential.helper` → exit 1，无值），也没有 `url.*` 重写规则（`--get-regexp 'url\.'` → exit 1）。
- ⇒ 这是**匿名公开读**，不需要 GitHub 账号凭据。推送当然仍需要认证（未验证，本次不做任何写操作）。
- 克隆耗时未记录（未加计时）。

### 2.3 失败原因归类

HTTPS 在默认环境（带代理）下**没有失败**，因此不存在需要归类的失败样本。

唯一失败的样本是 2.4 的「去掉代理」对照实验，其类别很明确：

| 报错原文片段 | 类别 | 说明 |
| --- | --- | --- |
| `Failed to connect to github.com port 443 after 21089 ms: Couldn't connect to server` | **网络层阻断 / 连不上** | 不是 401/403（不需要认证），不是 `SSL certificate problem`（不是 TLS 证书问题），是 TCP 443 建不起来 |

即：**典型的直连被墙/不可达表现**，而非凭据或证书问题。DNS 是否也受影响**未单独验证**（没有跑 `Resolve-DnsName github.com`）；从报错里能确定的只是「端口连不上」。

### 2.4 对照实验：清空代理后再试 HTTPS（只读探测，未写盘）

```powershell
$env:http_proxy=''; $env:https_proxy=''; $env:HTTP_PROXY=''; $env:HTTPS_PROXY=''
git -c credential.interactive=never -c http.connectTimeout=10 ls-remote https://github.com/Nanaloveyuki/RimworldMods.git HEAD
```

结果：`exit 128`，报错见第 1 节。

说明：这里用的是 `ls-remote`（只读、不落盘），不是第二次 `clone`——目的是判「HTTPS 本身通不通」，同时遵守「克隆之外不写盘」的约束。因此「无代理时 clone 会失败」属于**由 ls-remote 结果推断**，严格说 clone 本身未在无代理下重跑（**未验证**，但两者走同一条 libcurl 连接路径）。

### 2.5 SSH（未尝试，按任务约定跳过）

任务规定「只在 HTTPS 失败时才尝试 SSH」。HTTPS 成功，故**本次没有执行**：

- `git clone git@github.com:Nanaloveyuki/RimworldMods.git ...` —— **未验证**
- `ssh -T git@github.com` —— **未验证**

仅作为背景记录（这些是**文件名列表**，不是密钥内容，且在任何克隆动作之前就已读取）：

- `~\.ssh` 存在（`C:\Users\<用户>\.ssh`），其中文件名：`config`、`id_rsa`、`id_rsa.pub`、`known_hosts`、`known_hosts.old`、`phone-13pro.pub`
- ⇒ 存在 OpenSSH 私钥/公钥对，**SSH 大概率可用，但本次未实测**。

---

## 3. 仓库基本事实

| 项 | 实测值 |
| --- | --- |
| 远程 | `origin  https://github.com/Nanaloveyuki/RimworldMods.git`（fetch/push 同一 URL） |
| 最新提交 | `6ab44c71ec347930ec73877e698fa0accd4b1906  2026-10-23 23:08:28 +0800  deploy: v1.1.4` |
| 提交总数 | 390（`git rev-list --count HEAD`） |
| 分支 | `main`（本地）、`remotes/origin/main`、`remotes/origin/HEAD -> origin/main`；**只有 main 一条线** |
| 标签 | 无（`git tag` 空输出） |
| 工作区状态 | `git status --porcelain` 0 行——干净，说明克隆完整且未被我改动 |
| 包体积 | `git count-objects -vH`：1 个 pack，**254.47 MiB**（in-pack 11262 对象） |
| 工作区总大小 | **714.82 MB / 2050 个文件**（含 `.git`） |
| 其中 `.git` | 254.8 MB |
| 体积大头 | `.png` 253 个文件共 **444.9 MB**（几乎全在 `ratkin_hunger_and_havoc\Textures\KingdomArc\` 及其 docs 副本）；`.cs` 6.3 MB；`.dll` 4.6 MB；`.xml` 1.9 MB；`.md` 1.1 MB |

最近 8 条提交（`--date=short`）：

```
6ab44c7 2026-10-23 deploy: v1.1.4
11ce377 2026-10-23 fix(iris-menus): restore native blur in v1.1.2
02a7409 2026-10-23 docs: add rimsage mcp to agents.md
772b67c 2026-10-23 deploy: rhah 1.1.3
97b0add 2026-10-23 deploy: ppte2
b061823 2026-10-23 fix: address RHAH labor, settings and demand lifecycle bugs
45abd25 2026-10-23 feat(ratkin-recycle): prefer available food in v0.1.0
72bab67 2026-10-20 feat: add Prison Labor reachability fix
```

仓库活跃度：最近提交就在克隆前一天，属于在维护的项目。

---

## 4. 仓库结构

**这是一个 monorepo（多个 mod 的合集）**，不是单个 mod。根 `README.md` 自述「RimWorld 模组、框架与开发工具的统一源码仓库」，原各独立仓库停止维护，代码/Issue/发布统一到这里。

### 4.1 顶层列表

| 名称 | 类型 | 大小 | 说明 |
| --- | --- | --- | --- |
| `ratkin_hunger_and_havoc/` | mod 目录 | **448.2 MB** / 918 文件 | 鼠族：饥与祸（体积几乎全是贴图） |
| `rim_carbon14/` | mod + 工具 | 2.0 MB | 运行时检查/调试/自动化桥接，含 x64 MCP companion |
| `advanced_rimtalk/` | mod 目录 | 1.9 MB | RimTalk 提示词与编辑器扩展（97 个 .cs，无自己 Defs） |
| `oxygen_dsl_framework/` | 框架 mod | 1.8 MB | Oxygen DSL 语言/适配器/编辑器/离线工具 |
| `rimworld_washingmachine_play/` | mod 目录 | 1.4 MB | 自动洗衣机 |
| `lead_your_pets/` | mod 目录 | 1.0 MB | 牵着你的宠物 Continued |
| `prisoner_pay_to_eat_fork/` | mod 目录 | 1.0 MB | 囚犯付费吃饭 2 Fork |
| `save_your_storage_and_tick_every_minute/` | mod 目录 | 0.7 MB | 仓储 + 低频 Tick 调度优化 |
| `odf_mods/` | 多 mod 容器 | 0.5 MB | Oxygen DSL 消费端内容，`Mods\PrisonerNames\` 是其中一个 |
| `iris_menus/` | mod 目录 | 0.4 MB | 模组菜单/设置/渲染共享接口 |
| `ratkin_recycle/` | mod 目录 | 0.4 MB | 鼠族回收行为 |
| `oh_you_are_so_fast/` | mod 目录 | 0.3 MB | 寻路与 AI 优化 |
| `keep_your_identity/` | mod 目录 | 0.1 MB | 身份继承（仅 1.7） |
| `rim_iris_shaders/` | mod 目录 | 0.1 MB | RimIris Moonlight 着色器与界面 |
| `radius_ui_mission_control_fixes/` | mod 目录 | 0.1 MB | RadiusUI Mission Control 兼容修复 |
| `prison_labor_reach_fix/` | mod 目录 | 0.0 MB / 9 文件 | Prison Labor 可达性修复（仅 1.7） |
| `multi_floor_fork/` | **仅文档** | 0.0 MB / 2 文件 | 只有一份 MultiFloors 机制与兼容性调查报告 + LICENSE，**无源码、无可部署 mod** |
| `docs/` | 文档 | — | `docs/References.md`、`docs/agents/repository-maintenance.md` |
| `scripts/` | 脚本 | 22 文件 | `deploy.sh`、`python/deploy.py`、`deploy/<module>.json`（17 个部署配置）、`carbon/*.ps1` |
| `skills/` | 技能 | 1 文件 | `skills/ratkin-egg-speech/SKILL.md` |
| `.github/` | CI | 3 文件 | 见 4.4 |
| `AGENTS.md` | 文件 | — | monorepo 工程/文档/存储/发布约定 |
| `README.md` | 文件 | — | 项目索引表 + 许可证说明 |
| `LICENSE` / `LICENSE-MIT` / `LICENSE-APACHE` | 文件 | — | 自有贡献 **MIT OR Apache-2.0 双许可**；第三方/fork 保留原许可 |
| `.gitignore` | 文件 | — | 忽略 `**/bin/`、`**/obj/`、`**/tmp/`、`**/artifacts/`、`**/node_modules/`、`__pycache__`、`.vs/` 等 |

### 4.2 目录约定（来自仓库自身 `AGENTS.md` / `README.md`）

- 项目目录名必须**小写英文+下划线**；mod 品牌名与目录名分开维护。
- 各 mod 自己有 `Source/`、`Tests/`、`Textures/`、`Languages/`、`Guard/`、`1.6/`、`1.7/`、`docs/`、`tmp/` 等。
- 版本目录实测存在的有 21 个：`iris_menus/1.6`、`iris_menus/1.7`、`keep_your_identity/1.7`、`lead_your_pets/1.7`、`oh_you_are_so_fast/1.6`+`1.7`、`oxygen_dsl_framework/1.6`+`1.7`、`prisoner_pay_to_eat_fork/1.6`+`1.7`、`prison_labor_reach_fix/1.7`、`ratkin_hunger_and_havoc/1.6`+`1.7`、`ratkin_recycle/1.6`+`1.7`、`rimworld_washingmachine_play/1.7`、`rim_carbon14/1.6`+`1.7`、`rim_iris_shaders/1.7`、`save_your_storage_and_tick_every_minute/1.6`+`1.7`。

### 4.3 每个 mod 的 `About/About.xml`（共 19 份）

`Get-ChildItem -Recurse -Filter About.xml` 测得 **19 份**，逐份解析 XML 取值（不是按目录名猜）：

| # | About.xml 路径（相对仓库根） | `<name>` | `<packageId>` | `<supportedVersions>` | modVersion | 性质 |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | `advanced_rimtalk\About\About.xml` | Advanced RimTalk - Prompt | `advancedrimtalk.prompt` | 1.6, 1.7 | （无） | 正式 |
| 2 | `iris_menus\About\About.xml` | IrisMenus | `Nanaloveyuki.IrisMenus` | 1.6, 1.7 | 1.1.2 | 正式（其他 mod 的共享前置） |
| 3 | `keep_your_identity\About\About.xml` | 保持你的身份 KeepYourIdentity | `nanaloveyuki.keepyouridentity` | **1.7** | （无） | 正式 |
| 4 | `lead_your_pets\About\About.xml` | 牵着你的宠物 Continued | `nanaloveyuki.leadyourpet.continued` | 1.6, 1.7 | 1.8.0（带 `IgnoreIfNoMatchingField="True"` 属性） | 正式（上游授权续作） |
| 5 | `odf_mods\Mods\PrisonerNames\About\About.xml` | ODF 囚犯动态代号 / Dynamic prisoner names | `odf.nanaloveyuki.prisonernames` | 1.6, 1.7 | 0.1.0 | 正式（嵌套在 odf_mods 下） |
| 6 | `oh_you_are_so_fast\About\About.xml` | Oh You Are So Fast | `Nanaloveyuki.OhYouAreSoFast` | 1.6, 1.7 | （无） | 正式 |
| 7 | `oxygen_dsl_framework\About\About.xml` | Oxygen DSL Framework | `nanaloveyuki.oxygendsl` | 1.6, 1.7 | 0.0.1 | 框架 |
| 8 | `oxygen_dsl_framework\Examples\NativeTraits\About\About.xml` | Oxygen native traits example | `nanaloveyuki.oxygen.example.traits` | 1.6, 1.7 | （无） | **示例** |
| 9 | `oxygen_dsl_framework\Examples\RhahPolicy\About\About.xml` | Oxygen Ratkin gate example | `nanaloveyuki.oxygen.example.rhah` | 1.6, 1.7 | （无） | **示例** |
| 10 | `prisoner_pay_to_eat_fork\About\About.xml` | 囚犯付费吃饭2 Fork（Prisoners Pay To Eat 2） | `nanaloveyuki.prisonerspaytoeat2.fork` | 1.6, 1.7 | 2.5.2 | 正式（fork） |
| 11 | `prison_labor_reach_fix\About\About.xml` | Prison Labor Reachability Fix | `nanaloveyuki.prisonlaborreachfix` | **1.7** | （无） | 正式 |
| 12 | `radius_ui_mission_control_fixes\About\About.xml` | Radius UI - Mission Control Fixes | `nanaloveyuki.radiusui.missioncontrol.fixes` | **1.6**（无 1.7） | 1.1.0 | 正式 |
| 13 | `ratkin_hunger_and_havoc\About\About.xml` | 鼠族: 饥与祸 Ratkin: Hunger and Havoc | `nanaloveyuki.ratkin.hungerandhavoc` | 1.6, 1.7 | 1.1.4 | 正式（最大的内容 mod） |
| 14 | `ratkin_recycle\About\About.xml` | 鼠族回收行为 RatkinRecycle | `nanaloveyuki.ratkinrecycle` | 1.6, 1.7 | 0.1.0 | 正式（上游授权接手） |
| 15 | `rimworld_washingmachine_play\About\About.xml` | Washing Machine · 洗衣机 | `nanaloveyuki.washingmachine` | 1.6, 1.7 | 1.1.0 | 正式 |
| 16 | `rim_carbon14\About\About.xml` | RimCarbon14 | `nanaloveyuki.rimcarbon14` | 1.6, 1.7 | （无） | 正式（开发/调试工具 mod） |
| 17 | `rim_carbon14\tests\RimCarbon14.Fixture\About\About.xml` | RimCarbon14 Test Fixture | `nanaloveyuki.rimcarbon14.fixture` | **1.7** | （无） | **测试夹具** |
| 18 | `rim_iris_shaders\About\About.xml` | RimIris Moonlight | `RimIrisShaders.Moonlight` | 1.6, 1.7 | （无） | 正式 |
| 19 | `save_your_storage_and_tick_every_minute\About\About.xml` | Save Your Storage and Tick Every Minute | `Nanaloveyuki.SaveYourStorageAndTickEveryMinute` | 1.6, 1.7 | （无） | 正式 |

**结论：19 份 About.xml = 16 个可部署/正式性质 + 2 个 Oxygen 示例 + 1 个 Carbon 测试夹具。** `multi_floor_fork` 没有 About.xml（不是 mod）。

`<supportedVersions>` 元素在全部 19 份中均存在，写法一致（`<li>` 子元素列表），取值只有 1.6 / 1.7 / 两者。

**mod 依赖（`<modDependencies>`，全部包）**——这是给测试环境排加载顺序用的：

| mod | 依赖 packageId |
| --- | --- |
| advanced_rimtalk | `brrainz.harmony`, `cj.rimtalk` |
| iris_menus | `brrainz.harmony` |
| keep_your_identity | `brrainz.harmony`, `ludeon.rimworld.biotech`, `ludeon.rimworld.ideology` |
| lead_your_pets | `brrainz.harmony`, `Solaris.RatkinRaceMod` |
| PrisonerNames | `brrainz.harmony`, `nanaloveyuki.oxygendsl`, `Nanaloveyuki.IrisMenus` |
| oh_you_are_so_fast | `brrainz.harmony` |
| oxygen_dsl_framework | `brrainz.harmony` |
| NativeTraits（示例） | `nanaloveyuki.oxygendsl` |
| RhahPolicy（示例） | `nanaloveyuki.oxygendsl`, `nanaloveyuki.ratkin.hungerandhavoc` |
| prisoner_pay_to_eat_fork | `brrainz.harmony` |
| prison_labor_reach_fix | `brrainz.harmony`, `avius.prisonlabor` |
| radius_ui_mission_control_fixes | `brrainz.harmony`, `astryl.RadiusUI.Framework`, `astryl.RadiusUI.MissionControl` |
| ratkin_hunger_and_havoc | `Solaris.RatkinRaceMod`, `brrainz.harmony`, `Nanaloveyuki.IrisMenus`, `Ludeon.RimWorld.Biotech`, `nanaloveyuki.oxygendsl` |
| ratkin_recycle | `brrainz.harmony`, `Solaris.RatkinRaceMod` |
| rimworld_washingmachine_play | `brrainz.harmony` |
| rim_carbon14 | `brrainz.harmony` |
| RimCarbon14.Fixture | `brrainz.harmony` |
| rim_iris_shaders | `RimIris.RimIris.Beta` |
| save_your_storage_and_tick_every_minute | `brrainz.harmony` |

⇒ 除 Harmony 外，这批 mod 之间有**内部依赖链**：`IrisMenus` 和 `Oxygen DSL` 是被多个 mod 依赖的共享前置；`Solaris.RatkinRaceMod`（外部鼠族种族）是鼠族系列的前置。

### 4.4 README / LICENSE / .gitignore / CI / 解决方案

| 项 | 实测 |
| --- | --- |
| `README.md` | 有（根），是**项目索引表**，按「框架与开发工具 / 玩法与视觉内容 / 性能优化与兼容修复 / 调查与归档」分类列 17 个条目 |
| `LICENSE` + `LICENSE-MIT` + `LICENSE-APACHE` | 有，**MIT OR Apache-2.0 双许可**（自有贡献）；第三方/fork 保留原许可证与 NOTICE；`Lead-Your-Pets` 上游内容受原作者授权范围约束 |
| `.gitignore` | 有（根），另有 13 个子项目各自 `.gitignore` |
| `AGENTS.md` | 有（根，monorepo 约定）；4 个子项目另有 |
| CI | 有，`.github/workflows/` 两个 workflow + `.github/actionlint.yaml`：<br>・`static.yml`（push/PR，ubuntu-latest）——actionlint 校验 + `python3 scripts/rhah_pipeline.py test --tools-only` + `scripts/rhah_pipeline.py check`（干净检出的结构与内容契约）<br>・`local.yml`（仅 `workflow_dispatch`，跑在 `[self-hosted, Linux, X64, rhah]` + `environment: rhah-local`）——完整门禁/打包/部署/发布，需人工触发 |
| Issues 模板 | **无** `.github/ISSUE_TEMPLATE`（`Test-Path` = `False`） |
| 解决方案 | **无 `.sln`**，但有 3 个 `.slnx`（新版解决方案格式）：`odf_mods\ODF-Mods.slnx`、`oxygen_dsl_framework\Oxygendsl.slnx`、`rim_carbon14\RimCarbon14.slnx` |
| `.csproj` | **41 个**（每个 mod 多个：本体 + Tests + RuntimeChecks/Guard/Api 等） |
| 编译产物 `.dll` | 仓库内已有 **33 个**（发布时直接 deploy 二进制，见 `deploy: vX.X.X` 提交） |
| `About/Preview.png` | **15 个** |

**是 C# 程序集 mod 还是纯 XML defs mod？——绝大多数是 C# 程序集 mod（Harmony 补丁 + 独立 Assembly），XML 只是配套的 Defs/Patches/翻译。** 依据：41 个 csproj、719 个 .cs、几乎每个 mod 都依赖 `brrainz.harmony`。没有任何一个是纯 XML defs mod（`advanced_rimtalk`、`oh_you_are_so_fast`、`rim_carbon14`、`save_your_storage...` 是 `Defs=0 Patches=0` 但全有 C# 源码）。

### 4.5 代码规模（实测命令见文末）

**全仓库：**

| 指标 | 数值 |
| --- | --- |
| `.cs` 文件数 | **719** |
| `.cs` 总行数 | **140,336** |
| `.xml` 文件总数 | **450** |
| 　其中 `\Defs\` 下 | **112** |
| 　其中 `\Patches\` 下 | **16** |
| 　其中 `\Languages\` 下（翻译 DefInjected/Keyed） | **285** |
| 　其余（About / 配置等） | 37 |

**按项目拆分（`Defs`/`Patches` 按路径匹配 `\Defs\` / `\Patches\`）：**

| 项目 | Defs xml | Patches xml | .cs | .cs 行数 |
| --- | --- | --- | --- | --- |
| `ratkin_hunger_and_havoc` | 68 | 10 | **296** | **56,217** |
| `advanced_rimtalk` | 0 | 0 | 97 | 26,027 |
| `oxygen_dsl_framework` | 2 | 0 | 75 | 22,484 |
| `lead_your_pets` | 8 | 0 | 62 | 10,105 |
| `prisoner_pay_to_eat_fork` | 16 | 5 | 43 | 5,930 |
| `iris_menus` | 2 | 0 | 32 | 4,202 |
| `odf_mods` | 0 | 0 | 25 | 3,931 |
| `rim_carbon14` | 0 | 0 | 25 | 3,085 |
| `ratkin_recycle` | 6 | 0 | 19 | 2,525 |
| `save_your_storage_and_tick_every_minute` | 0 | 0 | 16 | 1,905 |
| `oh_you_are_so_fast` | 0 | 0 | 10 | 1,641 |
| `radius_ui_mission_control_fixes` | 0 | 0 | 4 | 760 |
| `rimworld_washingmachine_play` | 9 | 1 | 7 | 735 |
| `keep_your_identity` | 1 | 0 | 3 | 386 |
| `rim_iris_shaders` | 0 | 0 | 3 | 252 |
| `prison_labor_reach_fix` | 0 | 0 | 2 | 151 |
| `multi_floor_fork` | 0 | 0 | 0 | 0 |
| **合计** | **112** | **16** | **719** | **140,336** |

注意：`.cs` 行数包含 `Tests`、`Tools`、`Guard` 等非 mod 本体代码（`ratkin_hunger_and_havoc` 的 296 个 .cs 里含 `Source\Tests\`、`Guard\` 等）。

---

## 5. 已知问题 / TODO / bug 线索（快速 grep，未深挖）

做法：对 `*.cs`、`*.md`、`*.yml`（排除 `Tools\vscode`）grep `\b(TODO|FIXME|HACK|XXX)\b`，以及对 `*.cs/*.md/*.xml/*.json` grep `TODO|FIXME|HACK|已知问题|Known Issue|BUG:`。

结果：**几乎没有真正的代码 TODO 标记**。62 条 `*xxx*` 类命中绝大多数是误报——`{{ art.xxx }}` 占位符文档、RJW 的 `rjw.xxx` 类名等。

实测只有 1 条值得注意的「已知 bug」线索，且是**已修复**的：

- `prisoner_pay_to_eat_fork\README.md:212` —— 记录了一条已修复的严重 bug：「与其他 MOD 共存时可能导致『地图无法生成 / 渲染报错』」。原因：劳作区避让与 Dubs Bad Hygiene 兼容补丁原先在 Mod 构造函数里执行，触碰其他 MOD 的方法会触发对方静态构造器，此时 Defs 尚未加载，凡在静态字段里解析 Def 的 MOD（如 RJW 的 `xxx` 类，约 110 个 Def）会把查找结果缓存成 `null`，随后生成 Pawn 时抛 `NullReferenceException`，世界/地图生成与渲染一并崩溃。现改为 Defs 加载完成后再应用（`[StaticConstructorOnStartup]`）。

  这条对本项目的自动化测试**很有参考价值**：它说明「在 Mod 构造期触碰其他 Mod 类型」是这批 mod 的一类真实故障模式。

其他相关线索（是设计说明，不是 bug 报告）：

- `docs\agents\repository-maintenance.md` 的「发布资料状态」节自述了**资料缺项的补齐情况**与**验收边界**，明确写着：「本次已验证统一 CLI 的 `--help`、`--list`、`--preflight`，并完成 `keep_your_identity`、`advanced_rimtalk` 的 Release build-only 及 `odf_mods/PrisonerNames` 的跨项目构建与正式暂存；**未运行完整测试套件、格式化、游戏内验收或实际写入游戏 Mods**」；另有「未写入游戏 Mods、未上传工坊或进行游戏内验收」。
- `local.yml` 的完整门禁是 `workflow_dispatch` + self-hosted runner，意味着**完整 CI 在 GitHub 托管环境中并不自动跑**——PR 上只有离线静态检查。
- 同一文档提到「现行缺陷跟踪转到各模块的 GitHub Issues 入口」，但仓库里**没有 ISSUE_TEMPLATE**，GitHub Issues 的实际内容**未验证**（本次只克隆了代码，未查网页 Issue 列表）。

`rim_carbon14` 自带 `docs/references/protocol.md`、`docs/references/testing.md`，以及 `scripts/carbon/*.ps1`（`desktop-input.ps1`、`read-tools.ps1`、`test-client.ps1`）——从名字看是本仓库自己的「游戏内自动化测试桥」，与本项目（RimWorld_auto_test）方向高度重合，值得后续单独看。

---

## 6. 未验证 / 存疑

**未验证（本次没跑）：**

1. `git clone git@github.com:Nanaloveyuki/RimworldMods.git` —— SSH 方式**完全没试**（HTTPS 已成功，按任务约定跳过）。`~\.ssh` 里有 `id_rsa`/`id_rsa.pub`，但**密钥是否已注册到 GitHub、是否带 passphrase，未验证**。
2. `ssh -T git@github.com` —— 未执行。
3. **推送/写权限** —— 未验证，本次禁止任何写操作。HTTPS 匿名读成功不等于有推送权限。
4. **无代理情况下 clone 是否失败** —— 只用 `ls-remote` 验证了「无代理时 443 连不上」，没有在无代理下重跑完整 clone（有代理时已成功，无需重跑）。
5. **DNS 层面**是否正常 —— 没跑 `Resolve-DnsName`。报错只证明「443 连不上」，不能区分「DNS 污染」还是「TCP 被阻断」。
6. **能否编译** —— 一个 `.csproj` 都没 build，`.NET SDK` / `RimWorld` 引用程序集是否齐备**未验证**。仓库里已有 33 个预编译 `.dll`，但它们是哪一版、是否与本仓库源码当前 HEAD 对应，**未核对**。
7. **CI 是否真的在跑 / 是否通过** —— 只读了 workflow 文件，没看 GitHub Actions 运行记录。
8. **GitHub Issues 列表内容** —— 未查（需访问网页）。
9. **游戏内可用性** —— 这些 mod 能不能在 RimWorld 1.6/1.7 实际加载、是否有红字，**完全未验证**。
10. **`multi_floor_fork` 之外是否还有别处藏着 mod** —— 本次用 `About.xml` 全盘搜索判定（19 份），判定依据充分；但没有逐个核对「有 C# 但无 About.xml 的独立可部署项目」这种可能。
11. **克隆耗时/带宽** —— 未记录。

**存疑：**

1. **「正式 mod 包」的数量对不上。** `docs/agents/repository-maintenance.md` 说「**14 个**正式 Mod 包均已有 `About/Preview.png`」，而我实测 About.xml 有 19 份（16 份非示例/测试）、`Preview.png` 有 15 个。可能是该文档写作时点较早，或把框架/工具类算作非「包」。**以实测的 19 份 About.xml 为准**；文档里的 14 是历史数字。
2. **`lead_your_pets` 的 `modVersion` 写法特殊**：`<modVersion IgnoreIfNoMatchingField="True">1.8.0</modVersion>`，带了一个 `IgnoreIfNoMatchingField` 属性。这个属性是否是 RimWorld 认可的字段、有没有实际效果，**未验证**（存疑，不影响加载）。
3. **`radius_ui_mission_control_fixes` 只写 `<supportedVersions>1.6</supportedVersions>`**，其余绝大多数是 1.6+1.7。若要在 1.7 测试环境用它，可能被版本检查拦下——**未验证其 1.7 兼容性**。
4. **7 份 About.xml 没有 `<modVersion>`**（advanced_rimtalk、keep_your_identity、oh_you_are_so_fast、oxygen_dsl_framework、rim_carbon14、rim_iris_shaders、save_your_storage...）。RimWorld 不强制该字段，但意味着**无法从 About.xml 判断这些包的发布版本**；版本信息只在 `deploy: vX.X.X` 提交里。
5. 仓库体积 714 MB 中约 445 MB 是 PNG，且 `Textures\KingdomArc\` 与 `docs\references\kingdom-arc\...\background\` 下**存在同名同尺寸的重复大图**（如 `K1-E3-randy-hand-6.png` 2.6 MB 两份）。是否为有意保留的文档副本**未核实**，仅记录现象。

---

## 7. 复现用的命令清单（均为单行，未落地成脚本）

```powershell
git --version
$env:GIT_TERMINAL_PROMPT=0
git -c credential.interactive=never clone https://github.com/Nanaloveyuki/RimworldMods.git RimworldMods
git -C RimworldMods remote -v
git -C RimworldMods log -1 --format='%H %ad %s' --date=iso
git -C RimworldMods rev-list --count HEAD
git -C RimworldMods branch -a
git -C RimworldMods tag
git -C RimworldMods count-objects -vH
git -C RimworldMods status --porcelain
# 结构 / 统计
Get-ChildItem RimworldMods -Recurse -Filter About.xml
Get-ChildItem RimworldMods -Recurse -Filter *.cs -File | Measure-Object
Get-ChildItem RimworldMods -Recurse -Filter *.cs -File | Get-Content | Measure-Object -Line
Get-ChildItem RimworldMods -Recurse -Filter *.xml -File | Where-Object { $_.FullName -match '\\Defs\\' }
Get-ChildItem RimworldMods -Recurse -Filter *.xml -File | Where-Object { $_.FullName -match '\\Patches\\' }
Get-ChildItem RimworldMods -Recurse -Filter *.xml -File | Where-Object { $_.FullName -match '\\Languages\\' }
# 无代理对照（只读）
$env:http_proxy=''; $env:https_proxy=''; $env:HTTP_PROXY=''; $env:HTTPS_PROXY=''
git -c credential.interactive=never -c http.connectTimeout=10 ls-remote https://github.com/Nanaloveyuki/RimworldMods.git HEAD
# 环境事实
git config --global --get credential.helper
git config --global --get-regexp 'url\.'
Get-ChildItem $env:USERPROFILE\.ssh | Select-Object -ExpandProperty Name
```

**本次对仓库的写操作：无。** 只做了 `git clone`（一次）；没有修改仓库内任何文件、没有 commit、没有 push、没有 fetch/pull 之外的写动作；`git status --porcelain` 为 0 行可证。

---

## 附：本次用到的环境事实

| 项 | 值 |
| --- | --- |
| `git --version` | `git version 2.41.0.windows.1` |
| 全局 `credential.helper` | 未配置 |
| 全局 `url.*` 重写 | 无 |
| 全局 `http.proxy` | 未配置（代理靠环境变量） |
| 环境变量 `http_proxy` / `https_proxy` | `http://127.0.0.1:7897` / 同值 —— **HTTPS 成功的关键** |
| git 全局 `user.name` / `user.email` | `xiayuan` / `1137542776@qq.com`（仅记录，本次未提交） |
| `~\.ssh` 文件名 | `config`、`id_rsa`、`id_rsa.pub`、`known_hosts`、`known_hosts.old`、`phone-13pro.pub`（未读内容） |
