# 关键事实（不是状态板）

> 只放**不会自己变**的事实与指针：进度看票、环境看 `env-check`、历史看 `git log`。
> 往这里贴「现在做到哪 / 端口 / mod 数 / 某进程在不在」= 把它变回垃圾（`git log -p -- STATUS.md` 里有 58 次）。

## 票与流程（唯一待办真相）

- 票：`E:\2026.7以后的新工程\DSH\RHAH-wayfinder\tickets\`（**独立 git 仓**），每票的 `## 状态` 说话
- 闸门：同仓 `EXECUTION-PLAN.md` §3；**一次只实施一票**
- 环境事实：`node src/cli.js env-check --profile <p>`（永不写死数字）

## 四仓关系

- 本目录＝测试工程仓；**自带公开远端** `https://github.com/xtzhyhydwl/RimWorld_auto_test`（本仓的 PR 直接在这个仓开，例如 PR #1）
- 模组仓的 git 顶层＝`RimworldMods\`；票仓＝`..\RHAH-wayfinder`（独立仓，不在本仓里）
- 模组仓的 `origin`＝作者仓库（只读），push 与 PR 一律走 `fork`（`xtzhyhydwl`）；**这条只管模组仓，别套到本仓头上**

## 会重复咬人的（经验，不是状态）

- 游戏 / 工坊目录里的二进制属于玩家：替换后必须还原并比 hash；备份路径写进当轮的 `runs\` 报告，不写这里
- 工作区的 `*.dll` 是构建产物，永不提交
- 1.6 并行安装在 `D:\RimWorld-1.6`；构建与切版见 skill `rimworld-switch-test-version`
- 踩过的坑在 skill：`.dsh\skills\rimworld-mod-pipeline\环境陷阱.md`、`.dsh\skills\rimworld-auto-test-closed-loop\docs\测试SOP\失败判读.md`（新坑先进 skill，不回到本文件）

## 线索引（各自的 STATUS 在各自目录）

> 下列各线目录**只在磁盘上，未入库**（2026-10-26 分离时移出跟踪）⇒ 在公开仓里这些链接点不开，本机才有效。

| 线 | 是什么 |
| --- | --- |
| [自动化测试](自动化测试/STATUS.md) | 测试机器：启动器 / 桥接 / 调试殖民地 / UI 驱动 / 断言 |
| [pilot](自动化测试/pilot/STATUS.md) | 在测试机器之上加自主决策 |
| [共享单车](共享单车/STATUS.md) | 一个 mod 的设计 |
| [快照捕捉](快照捕捉/STATUS.md) | 只读录制殖民地状态 |
| [副官语音 v2](语音副官/STATUS.md) | 把屏幕提示播报出来 |
