---
name: rimworld-mod-compat
description: 诊断和修复 RimWorld mod 的加载顺序与兼容性问题 —— 读懂 mod 列表里的红字、顺清依赖链（含 loadAfter 里藏着的真依赖）、按正确顺序排 ModsConfig、区分「真故障」和「纸老虎警告」、功能验证 mod 是否真的生效。当出现 mod 不生效、前置未满足、加载顺序问题、启动报错、红字警告，或需要给测试环境加装 mod 时使用。
---

# RimWorld mod 兼容性与加载顺序

## 仓库位置（绝对路径）

```
E:\2026.7以后的新工程\DSH\RimWorld_auto_test
```

配套工具都在这个目录下（下称 `$WS`），**命令都要先 `cd` 过去执行**。
需要先有跑起来的测试实例：`node src/cli.js start`（隔离存档，不碰主力配置）。

> **最重要的一条经验**：mod 列表里的红色「前置未满足」**不等于 mod 坏了**。
> RimWorld 的依赖校验按 **packageId 精确匹配**，而很多 mod 作者把前置包名大小写写错，
> 就会报这个警告，但功能完全正常。**先功能验证，再决定要不要折腾。**

---

## 一、先读状态，别猜

```bash
node src/cli.js call rimworld/get_mod_configuration_status   # 汇总：几项配置问题/版本警告/排序问题/缺依赖
```

```js
const mods = await call('rimworld/list_mods', { compatibleOnly: false });
```

关键字段（`list_mods` 每一项）：

| 字段 | 看什么 |
| --- | --- |
| `packageId` / `packageIdPlayerFacing` / `packageIdNonUnique` | **前者是小写化的真值**，后两者保留作者写的原始大小写 —— 大小写不一致就是问题源头 |
| `enabled` / `loadedInSession` | 是否启用 / 是否真的进了这次会话 |
| `activeLoadOrder` | 当前顺序（`null` = 没启用） |
| `unsatisfiedDependencies` | 缺失的前置（显示的是 **displayName**，不是 packageId） |
| `hasOrderingIssues` / `loadBefore` / `loadAfter` | 排序 |
| `versionCompatible` / `hasVersionWarning` | 版本 |
| `hasConfigurationWarning` / `configurationWarning` | 配置警告的原文 |
| `rootDir` / `source` | 从哪加载的（`ModsFolder` / `SteamWorkshop`） |

原「按关键词过滤后打印 mod 明细」的脚本 `scripts/check-ratkin.mjs` **已于 `9dbfa33`（2026-10-17）移除**，需要时 `git checkout 9dbfa33^ -- scripts/check-ratkin.mjs` 取回，不要重写。

---

## 二、顺依赖链：`loadAfter` 里藏着真依赖

**只读 `<modDependencies>` 会漏。** 有些 mod 把前置写在 `<loadAfter>` 里。

实例（NewRatkinPlus）：

```
NewRatkinPlus  modDependencies: erdelf.HumanoidAlienRaces, brrainz.harmony
                          loadAfter: brrainz.harmony, Ludeon.RimWorld, erdelf.HumanoidAlienRaces
HAR            modDependencies: （无）
                          loadAfter: imranfish.xmlextensions     ← 真正的前置藏在这里
```

所以完整链是 **Harmony → Core + DLC → XML Extensions → HAR → NewRatkinPlus**。
漏掉 XML Extensions 就可能出问题。

**怎么找 mod 的实际位置**（扫工坊 + 本地 Mods 的 About.xml）：

```powershell
$ws='D:\SteamLibrary\steamapps\workshop\content\294100'
Get-ChildItem $ws -Directory | ForEach-Object {
  $a = Join-Path $_.FullName 'About\About.xml'
  if (Test-Path $a) { $t = Get-Content $a -Raw -Encoding UTF8
    if ($t -match '<关键词>') { "$($_.Name): " + ([regex]::Match($t,'<packageId>([^<]*)</packageId>')).Groups[1].Value } }
}
```
本地 `Mods` 目录也要扫（有些 mod 装在那儿，例如 XML Extensions）。

---

## 三、标准加载顺序

```
1. Harmony                  （brrainz.harmony，永远第一）
2. Core + DLC              （ludeon.rimworld, .royalty, .ideology, .biotech, .anomaly, .odyssey）
3. 基础框架                 （XML Extensions、HugsLib…）
4. 种族/派系框架            （HAR = erdelf.HumanoidAlienRaces）
5. 内容 mod                 （种族本体、武器、服饰…）
6. 补丁 / 兼容 patch        （必须在其目标之后）
7. 工具类                   （RimBridgeServer、Character Editor…）
```

改顺序：原 `scripts/modsconfig.mjs`（`list` / `add` / `remove` / `set` / `baseline` / `copy-main`）**已于 `9dbfa33`（2026-10-17）移除**，需要时 `git checkout 9dbfa33^ -- scripts/modsconfig.mjs` 取回，不要重写。
现在的入口：`node src/cli.js mods --profile <p>`（只读/写「被测 mod 清单」并写回 ModsConfig 对应部分）；**整份顺序仍要直接改 `profiles/<p>/Config/ModsConfig.xml`**。
**改完必须重启游戏**（`node src/cli.js stop` 再 `start`）—— 加载顺序只在启动时生效。

---

## 四、纸老虎 vs 真故障：怎么判

### 本次实测的经典案例

`NewRatkinPlus` 报 `unsatisfiedDependencies: ["Humanoid Alien Races 2.0"]`，红字很吓人。但：

| 检查 | 结果 |
| --- | --- |
| HAR 的 `enabled` / `loadedInSession` | true / true，`activeLoadOrder=8`，排在 Ratkin(9) 前面 |
| HAR 的 `unsatisfiedDependencies` | `[]` |
| `hasOrderingIssues` | false |
| **功能验证**：`Actions\Spawn Pawn...` 里有没有鼠族 | **21 个**（RatkinColonist / RatkinKnight / RatkinNoble…） |
| **功能验证**：实际生成一只 | 成功，地图上出现 `Ratkin` def |
| **日志**（warning 及以上） | **0 条** |

**根因**：作者把前置包名大小写写错了 ——

```
HAR 实际 packageId        = "erdelf.humanoidalienraces"     全小写
NewRatkinPlus 声明的依赖   = "erdelf.HumanoidAlienRaces"     驼峰
```

**结论：这是上游 mod 的打包笔误，不是你的环境问题，也不影响功能。不要为它改加载顺序。**

### 判定流程

```
1. 依赖项 enabled + loadedInSession 都是 true？
   ├─ 否 → 真的缺前置，去找并装上
   └─ 是 → 继续
2. 依赖项排在它前面吗（activeLoadOrder 更小）？
   ├─ 否 → 调顺序，重启再看
   └─ 是 → 继续
3. 功能验证：这个 mod 的内容真的存在吗？
   ├─ 用 debug action 树查（Spawn Pawn... / Spawn thing... 里有没有它的 def）
   ├─ 或者直接生成一个看能不能成功
   └─ 存在 → 警告是纸老虎，忽略
4. 看日志：rimbridge/list_logs minimumLevel=warning
   └─ 有 error → 才是真问题，把报错原文拿去搜
```

**永远不要**只看红字就重排顺序 —— 那是在制造新问题。

---

## 五、功能验证 mod 是否生效（比看警告可靠）

```js
// 1) 这个 mod 加了哪些 pawn 类型
const r = await call('rimworld/get_debug_action',
  { path: 'Actions\\Spawn Pawn...', includeChildren: true, includeHiddenChildren: true });
const mine = (r.children ?? []).filter(k => /关键词/i.test(k.label ?? ''));

// 2) 实际生成一个，看地图上有没有出现
await call('rimworld/execute_debug_action', { path: `Actions\\Spawn Pawn...\\${mine[0].label}`, x, z });

// 3) 回读落点周围
const area = await call('rimworld/get_cells_info', { x: x-3, z: z-3, width: 7, height: 7 });
const defs = [...new Set((area.cells ?? []).flatMap(c => (c.things ?? []).map(t => t.defName)))];
```

同样的思路：`Actions\Spawn thing...` 查物品、`Actions\Add Hediff...` 查 hediff、
`Actions\Add Gene...` 查基因 —— **mod 加的任何 Def 都会出现在对应的参数化动作的叶子列表里**，
这是最直接的存在性证明。

---

## 六、常见坑

| 现象 | 常见原因 |
| --- | --- |
| 红字「前置未满足」但一切正常 | 作者写错了 packageId 大小写 —— **纸老虎，别动** |
| mod 装了但内容不存在 | 真缺前置；或 `LoadFolders.xml` 里该版本目录没配；或版本目录名不匹配 |
| 依赖写在 `loadAfter` 里被漏掉 | 顺依赖链必须同时看 `modDependencies` 和 `loadAfter` |
| 改了 ModsConfig 但没生效 | 没重启游戏；或改错了 profile 的文件 |
| 加载极慢 | 正常现象。实测纯原版 6 秒，加 HAR+Ratkin+CE 后 12-15 秒 |
| 想复刻主力配置排查 | 原 `scripts/modsconfig.mjs copy-main` **已于 `9dbfa33`（2026-10-17）移除**（取回：`git checkout 9dbfa33^ -- scripts/modsconfig.mjs`）；现在直接把主力的 `Config/ModsConfig.xml` 复制到 `profiles/<新名字>/Config/` |

---

## 七、注意

- **一切都在隔离 profile 里做**（`profiles/<name>/Config/ModsConfig.xml`），
  **不要改主力存档的 ModsConfig**。`modsconfig.mjs` 默认只动隔离的那份。
- 换 mod 组合要重启游戏；重启会丢掉当前殖民地（但隔离存档目录和存档文件还在）。
- 判断"某个 mod 有没有生效"时，**先看功能，再看警告**。顺序反了会浪费大量时间。
