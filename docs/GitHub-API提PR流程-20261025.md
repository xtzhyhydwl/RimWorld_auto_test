# 用 GitHub API 提 PR 的标准流程（2026-10-25 实测）

> 上游 `Nanaloveyuki/RimworldMods`（第三方作者仓库），在 fork `xtzhyhydwl/RimworldMods` 上开发；本机最近一次成功实例：**PR #9** —— 16 files / +171 / −27，无 DLL、无 Def。

## 前置条件
- **token**：GitHub **classic PAT**，scope 只勾 `public_repo`；`setx` 存在**用户级环境变量**里，当前进程读不到，必须显式取：
```powershell
$tok = [Environment]::GetEnvironmentVariable('GITHUB_TOKEN','User')
```
- **SSH key**：已加进 GitHub 账号，`ssh -T git@github.com` 通过（推送走 SSH，不走 HTTPS+PAT）。
- **remote**：`fork` = `git@github.com:xtzhyhydwl/RimworldMods.git`；`origin` = 上游（**没有 push 权限**）。
- **代理**：`api.github.com` 走本机 `http://127.0.0.1:7897`（`-Proxy` 或 `$env:HTTPS_PROXY`）。

## 步骤 1：确认要推什么
```powershell
git log --oneline -3; git status --short      # 构建产物（.dll）、本地配置不该在改动里
git diff --stat origin/main..HEAD             # 记下 files / +N / −N，步骤 4 用它核对 PR
```

## 步骤 2：推分支到 fork
```powershell
git push fork <branch>
git ls-remote fork <branch>                   # 远端 hash 必须等于本地 git rev-parse HEAD
```

## 步骤 3：建 PR
```powershell
$h = @{ Authorization = "Bearer $tok"; 'User-Agent' = 'dsh-agent'; Accept = 'application/vnd.github+json' }
$json = @{
  title = '<标题>'; head = 'xtzhyhydwl:<branch>'; base = 'main'
  body  = (Get-Content -Raw -Encoding UTF8 '<body文件>.md'); maintainer_can_modify = $true
} | ConvertTo-Json -Depth 5
Invoke-RestMethod -Method Post -Uri 'https://api.github.com/repos/Nanaloveyuki/RimworldMods/pulls' `
  -Headers $h -ContentType 'application/json; charset=utf-8' `
  -Body ([Text.Encoding]::UTF8.GetBytes($json)) -Proxy 'http://127.0.0.1:7897'   # 返回 number / html_url
```
PS 5.1 用 `-Body <字符串>` 发中文可能按本地代码页编码，故先转 UTF-8 字节。

## 步骤 4：自检（查 PR 真实内容，不靠页面印象）
```powershell
$api = 'https://api.github.com/repos/Nanaloveyuki/RimworldMods/pulls/<n>'
Invoke-RestMethod "$api/files?per_page=100" -Headers $h -Proxy 'http://127.0.0.1:7897' | Select-Object filename
Invoke-RestMethod $api -Headers $h -Proxy 'http://127.0.0.1:7897' | Select-Object number,base,head,mergeable,changed_files
```
**自检清单（6 条，全过才算提完）**
1. `changed_files` 等于 `git diff --stat` 的 files 数（本次 16）。
2. 文件清单里**没有 `.dll`**（构建产物不能进 PR）。
3. 没有不该改的 Def / 无关文件（本次两份 `RHAH_Duties.xml` 不在 diff 里）。
4. `base` = `main`，`head` = `xtzhyhydwl:<branch>`，不是反的也不是空。
5. `mergeable` 不是 `false`；是 `null` 就等几秒重查（GitHub 还在算）。
6. `body` 里没有本机绝对路径、没有 token、没有中文外壳标题。

## 步骤 5：收尾
把 PR 号、`html_url`、`mergeable` 记进 `docs/` 或 `STATUS.md`；作者合入前**不要 force-push**，要改就追加提交、重推同一分支。

## 排错表
| 现象 | 怎么办 |
|---|---|
| `403` / rate limit | 出口 IP 限流：挂代理重发；配额真用完就等一小时，别反复重试。 |
| `422 Unprocessable` | `head` 少了 `<user>:`、或该 head→base 已有 PR：`GET /pulls?head=xtzhyhydwl:<branch>&state=all` 查。 |
| `401 Bad credentials` | token 没读到或已过期：重取用户级变量，必要时重新生成。 |
| 连接超时 / TLS 失败 | 没走代理：加 `-Proxy 'http://127.0.0.1:7897'`；`api.github.com` 直连不稳。 |
| `Permission denied (publickey)` | SSH key 没加载：`ssh -T git@github.com` 复测，`ssh-add` 后重推。 |

## 安全
- token **只存在注册表**（用户级环境变量）：不写进文件、不打印、不进命令行参数（会留历史与进程列表）。
- 不贴进对话/截图/issue，不进 git（`git status` 里出现就该删）；到期轮换后 `setx` 新的并 revoke 旧的。

本文档不含真实 token —— `$tok` 一律运行时从环境变量读取。
