---
name: github-pr-by-api
description: 没有 gh CLI 时用 GitHub REST API 把本地分支推成 PR 并自检：fork → push → create → verify。向第三方作者的公开仓库提 PR、需要用 classic PAT 调 GitHub API、或要核对一个 PR 里到底改了哪些文件时使用。
---

# 无 gh CLI 时用 GitHub API 提 PR
**用途**：本机没有 `gh` CLI 时，用 GitHub REST API 把本地分支推成对第三方公开仓库的 PR。
**四步**：fork → push → create → verify，每步都给可核对的完成判据。

## 前置检查（4 项全过再开工）
| 检查 | 命令 | 通过判据 |
| --- | --- | --- |
| token 可取到 | 按步骤 1 的三级回退读取，看 `$tok.Length` | 非空；只报长度或前 4 位 |
| fork 已存在 | `git ls-remote fork <branch>` | 列得出 ref；报 `Repository not found` 即未建 |
| 远端 SSH 可用 | `ssh -T git@github.com` | 输出 `successfully authenticated`（exit 1 属正常，无 shell 权限） |
| 分支已 rebase 到目标 base | `git log --oneline origin/<base>..HEAD` | 只剩自己的 commit |

## 步骤 1：取 token
PAT 是 classic、scope 仅 `public_repo`。`setx` 只写注册表，**已运行的进程环境里读不到**，所以要三级回退：

```powershell
$tok = $env:GITHUB_TOKEN
if (-not $tok) { $tok = [Environment]::GetEnvironmentVariable('GITHUB_TOKEN','User') }
if (-not $tok) { $tok = [Environment]::GetEnvironmentVariable('GITHUB_TOKEN','Machine') }
if (-not $tok) { $tok = (Get-Content 'C:\Users\MikanseiP\.dsh\secrets\github-token.txt' -Raw).Trim() }
"len=$($tok.Length) head=$($tok.Substring(0,4))"
```

## 步骤 2：推分支（SSH，token 不进 remote URL 与 `.git/config`）
```powershell
git remote add fork git@github.com:<user>/<repo>.git      # 已有 fork 则改用 set-url
git push fork <branch>
git ls-remote fork <branch>                                # 远端 hash
git rev-parse HEAD                                         # 本地 hash，两者必须一致
```

## 步骤 3：建 PR（`<owner>/<repo>` 是上游作者的仓库，不是自己的 fork）
```powershell
$json = @{ title='<标题>'; head='<user>:<branch>'; base='<目标分支>'; body='<正文>'; maintainer_can_modify=$true } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri 'https://api.github.com/repos/<owner>/<repo>/pulls' `
  -Headers @{ Authorization="Bearer $tok"; 'User-Agent'='dsh-pr' } -Body $json -ContentType 'application/json'
```

## 步骤 4：自检 PR
```powershell
$h = @{ Authorization="Bearer $tok"; 'User-Agent'='dsh-pr' }
$f = Invoke-RestMethod -Headers $h 'https://api.github.com/repos/<owner>/<repo>/pulls/<n>/files?per_page=100'
"files=$($f.Count) +$(($f|Measure-Object additions -Sum).Sum) -$(($f|Measure-Object deletions -Sum).Sum)"
($f | Where-Object { $_.filename -match '\.(dll|pdb|zip)$' }).filename     # 期望空
$p = Invoke-RestMethod -Headers $h 'https://api.github.com/repos/<owner>/<repo>/pulls/<n>'
"base=$($p.base.ref) head=$($p.head.label) mergeable=$($p.mergeable) state=$($p.mergeable_state)"
```

## 自检清单（全 yes 才算完成）
- [ ] 文件数符合预期，我能逐个说出每个文件的用途
- [ ] 无构建产物（`.dll` / `.pdb` / `.zip` / `obj/` / `bin/`）
- [ ] `base.ref` 是目标分支，`head.label` 是 `<user>:<branch>`
- [ ] `mergeable` 不是 `false`（`null` 是 GitHub 还在算，稍后重查）
- [ ] PR body 无本机绝对路径（`E:\`、`C:\Users\`）

## 排错表
| 现象 | 怎么办 |
| --- | --- |
| `403` | 多为出口 IP 限流，等几分钟重试；仍不行走代理 |
| `422` | head 分支名不对（缺 `<user>:` 前缀）、分支没推上去、或同 head/base 的 PR 已存在 |
| `401` | token 无效 / 过期 / scope 不含 `public_repo` |
| HTTPS 连不上 | 走代理 `http://127.0.0.1:7897`：`Invoke-RestMethod -Proxy ...` 或设 `$env:HTTPS_PROXY` |
| `git push` 报 `Repository not found` | fork 还没建，请用户在自己账号下点 Fork |

## 安全
- token 只从 `$env:GITHUB_TOKEN` / 注册表 / 本地 secrets 文件取，只留在内存变量 `$tok` 里。
- 只报长度或前 4 位；token 不进文件、不进对话、不进命令历史、不进 `git config`。
- PAT 有期限，到期前轮换并同步更新用户级环境变量。

## 示例（本机实测）
分支 `fix/0008-can-dig-exit` 推到 `xtzhyhydwl/RimworldMods`，对 `Nanaloveyuki/RimworldMods` 的 `main` 开 PR #9：`16 files / +171 / −27`，无二进制文件，`base.ref=main`。

## 相关文档
- 完整流程与本机实测记录：`docs/提PR流程-20261025.md`（在本 skill 目录内）
