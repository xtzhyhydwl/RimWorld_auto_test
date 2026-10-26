# rhah profile `ModsConfig.xml` packageId 小写化（2026-10-25）

目标文件：`profiles\rhah\Config\ModsConfig.xml`（RimWorld 1.7.4985 rev1269 测试 profile `rhah`）

## 结论

1. 共改 **3 条** `activeMods` id，**只改大小写**：`erdelf.HumanoidAlienRaces`→`erdelf.humanoidalienraces`、`Solaris.RatkinRaceMod`→`solaris.ratkinracemod`、`Nanaloveyuki.IrisMenus`→`nanaloveyuki.irismenus`；`knownExpansions` 5 条本来就全小写，未动。
2. 校验 **全过**：XML 可解析、21 条 `<li>` 全部匹配 `^[a-z0-9._]+$`、条目数与顺序与改前完全一致（全文仅大小写不同）。
3. 何时生效：**需要重启游戏**——`ModsConfig.xml` 在启动时被读入内存，运行中的实例不会因为改文件而把模组变成「已激活」（此点为推断，未实测热重载路径）。

## 改前 / 改后对照

| 行 | 改前（原文） | 改后 |
|-|-|-|
| 16 | `erdelf.HumanoidAlienRaces` | `erdelf.humanoidalienraces` |
| 17 | `Solaris.RatkinRaceMod` | `solaris.ratkinracemod` |
| 18 | `Nanaloveyuki.IrisMenus` | `nanaloveyuki.irismenus` |

`knownExpansions`（第 23–27 行）5 条均为 `ludeon.rimworld.*`，原本就是全小写，无改动。

## 校验结果

| 项 | 结果 |
|-|-|
| `[xml]` / `XmlDocument.LoadXml` | 通过；`version` 仍为 `1.7.4985 rev1269`（未改） |
| `<li>` 总数 | 21（activeMods 16 + knownExpansions 5），与改前相同 |
| 非全小写 `<li>` 数 | 0（全部匹配 `^[a-z0-9._]+$`） |
| 顺序 / 集合（`Compare-Object … -SyncWindow 0`，两边先 `ToLowerInvariant`） | 差异 0 条 ⇒ 顺序不变、无增删 |
| 全文 `ToLowerInvariant()` 相等 | True ⇒ 除大小写外无任何字符变化 |
| 字节层面 | 长度 933→933；UTF-8 BOM `EF BB BF` 保留；LF-only（CR=0，LF=28）；末尾无换行 |

其他：vanilla 的 `C:\Users\<用户>\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\ModsConfig.xml`（1.6.4871 rev591，只读核对）**348 条 `<li>` 全小写，0 条非小写** —— 与「游戏自己写全小写」一致。

⚠️ 一处与任务前提不符（实测）：该文件**原本有 UTF-8 BOM**（`EF BB BF`），并非「无 BOM」。`edit` 工具保存时会丢 BOM（933→930 字节），已用字节级写回补上 BOM，恢复为 933 字节；除此之外无任何字节被改。

SHA256：

- 改前（= 备份）：`BEB0FC408C1768E2BF2A4A3C30D201B81E62EDA8EC4F2F74F80240B77537123E`
- 改后：`A716E96F6BF82FB9C921586A76B66D694E8CA720307D43CA6B84EE845CCC0321`

## 三条 mod 的 `About.xml` 原文 packageId（只读核对）

| 模组 | 工坊 id | About.xml 原文 | 位置 |
|-|-|-|-|
| Humanoid Alien Races | 839005762 | `erdelf.HumanoidAlienRaces` | `D:\SteamLibrary\steamapps\workshop\content\294100\839005762\About\About.xml:17` |
| NewRatkinPlus | 1578693166 | `Solaris.RatkinRaceMod` | `…\294100\1578693166\About\About.xml:10` |
| IrisMenus | 3798502474 | `Nanaloveyuki.IrisMenus` | `…\294100\3798502474\About\About.xml:5` |

本地副本同 id：`RimworldMods\iris_menus\About\About.xml:5` 也是 `Nanaloveyuki.IrisMenus`。

旁证（说明小写化符合生态习惯）：`Ratkin Hair Plus`（工坊 3523307344）自己的 `modDependencies` 里把 NewRatkinPlus 写成 **`solaris.ratkinracemod`（小写）**，而同一文件 `loadAfter` 写 **`Solaris.RatkinRaceMod`（原文大小写）** —— 两种写法被当作同一个包使用。

## 备份路径与回滚

- 备份：`runs\modsconfig-fix-20261025\ModsConfig.before.xml`（SHA256 同「改前」：`BEB0FC40…37123E`）
- 回滚：把备份复制回 `profiles\rhah\Config\ModsConfig.xml`，再核对 SHA256 应回到 `BEB0FC40…37123E`。
  `Copy-Item -LiteralPath 'runs\modsconfig-fix-20261025\ModsConfig.before.xml' -Destination 'profiles\rhah\Config\ModsConfig.xml' -Force`
- 注意：若游戏正在运行，退出时可能把内存里的 mod 列表覆写回该文件，回滚要在游戏关闭时做。

## 未验证

- **未重启/未加载游戏**（按要求不碰进程）：没有确认 `[RHAH] IrisMenus is not active` 消失，也没有确认 RHAH 设置页恢复多页。
- 没有独立读 1.7 源码验证 `ModsConfig.IsActive` 的大小写敏感实现 —— 该结论沿用任务背景，本次只做了文件层修复与校验。
- 没有验证三个模组在小写化后仍能正常加载 Def（需进游戏看日志）。
- 没有核对其它 profile 是否有同样的混合大小写问题。
- 没有验证游戏退出时是否会重写该文件（若重写，BOM/大小写形式以游戏输出为准）。
