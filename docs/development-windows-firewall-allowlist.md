# 本机开发环境：Windows 防火墙白名单说明

> **状态**：2026-10-25 已在本机生效（54 条规则，组名 `LiteMonitor Test Allowlist`）。
> **作用**：让自动化测试运行时**不再弹「Windows 安全中心警报」**要求人工点确认。
> **不影响**：防火墙的三个配置文件（Domain / Private / Public）**全程保持启用**，本方案从未关闭过防火墙。

---

## 1. 为什么需要它（实测根因）

LiteMonitor 的两类程序会**以入站方式监听端口**：

- `testhost.exe` —— VSTest 测试宿主，跑测试时启动；
- `LiteMonitor++.exe` —— 主程序内置 WebServer（`HttpListener`，见 `src/System/WebServer/LiteWebServer.cs`）。

Windows 的行为是：**当一个可执行文件首次尝试入站监听时，弹出「Windows 安全中心警报」要求人工确认**。
而 Windows 自动生成的规则 ID 形如：

```
Query User{GUID}G:\tool\litemonitor-fork\tests\litemonitor.tests\bin\release\net8.0-windows\testhost.exe
```

**带 GUID** ⇒ 规则与「具体路径 + 具体端口」绑定。所以：

| 情况 | 是否重新弹窗 |
|---|---|
| 同一路径再次监听 | 否（规则已存在） |
| 换个 bin 目录（Debug ↔ Release ↔ eval-dbg ↔ eval-rel） | **是** |
| 换个 worktree（`.worktrees/r06-namespace/...`） | **是** |
| 换台机器 / 换用户账户 | **是** |

⇒ 这就是"每跑一次测试就要手动点几下、而且永远点不完"的直接原因。

**顺带澄清一个常见误判**：这**不是** Defender 杀毒在拦截。本机实测
`RealTimeProtectionEnabled = False`、`AntivirusEnabled = False`（Defender 本来就是关的），
防火墙日志里也没有任何 `blocked` 记录 —— 弹窗是 Windows 的**入站监听确认提示**，不是拦截。

---

## 2. 为什么用白名单而不是直接关防火墙

直接关防火墙（`Set-NetFirewallProfile -Enabled False`）确实也能让弹窗消失，代价是：

- **整机入站防护全停** —— 任何本机程序都能被局域网访问；
- 本机的 LiteMonitor **WebServer 正是给手机 / 副屏远程看硬件指标用的** ⇒ 等于把该服务直接暴露到局域网；
- 且该操作需要管理员权限，事后还原容易漏（尤其分 Domain/Private/Public 三档）。

白名单方案只对**这一个测试程序**开口，弹窗同样消失，防护面小得多。

---

## 3. 规则规格

| 项 | 值 |
|---|---|
| 组名 | `LiteMonitor Test Allowlist` |
| 规则数 | 54 条 = 27 个程序路径 × TCP/UDP |
| 方向 | `Inbound`（出站默认即放行，测试联网不受影响） |
| 操作 | `Allow` |
| 协议 | `TCP` + `UDP` |
| 配置文件 | `Any`（Domain / Private / Public 全生效） |
| 本地地址 | `127.0.0.1` ⇒ **只对本地 IPv4 回环生效，局域网其他机器连不进来** |
| 边缘穿越 | `Block`（不开放穿越边缘的入站） |
| 程序 | 18 个 `testhost.exe`（覆盖所有 Debug/Release/eval-* 与 R06 worktree）+ 9 个主程序/辅助 exe |

> **为什么只绑 `127.0.0.1` 不加 `::1`**：Windows 拒绝 IPv6 回环地址写入防火墙规则
> （报错「已指定未指定的多播、广播或环回 IPv6 地址」）。测试用的 `HttpListener`
> 绑定的是 `127.0.0.1`，不受影响。

### 覆盖的程序清单

- `tests/LiteMonitor.{Tests,Updater.Tests,GpuNonperformance.Tests}/bin/{Debug,Release,eval-dbg,eval-rel}/**/testhost.exe`
- `.worktrees/r06-namespace/tests/LiteMonitor.{Tests,Updater.Tests,GpuNonperformance.Tests}/bin/{Debug,Release}/**/testhost.exe`
- `bin/{Debug,Release,eval-dbg,eval-rel}/**/LiteMonitor++.exe`
- `bin/**/resources/LiteMonitor++.Updater.exe`、`bin/**/resources/LiteMonitorPlusFPS.exe`

---

## 4. 如何还原

删掉本组规则即可，**防火墙配置文件不受影响**（本来就一直是开的）。

**方式一（推荐，会弹一次 UAC，点「是」）**

```powershell
Start-Process -FilePath 'C:\Program Files\PowerShell\7\pwsh.exe' -Verb RunAs -Wait `
  -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File', `
  '<仓库根>/.tmp/remove-litemonitor-test-firewall-rules.ps1'
```

或直接双击 `<仓库根>/.tmp/remove-fw-rules.bat`。

**方式二（已在管理员 PowerShell 中）**

```powershell
Get-NetFirewallRule -Group 'LiteMonitor Test Allowlist' | Remove-NetFirewallRule
```

> `.tmp/` 目录已被 `.gitignore` 忽略（`/.tmp/`），这些脚本**不入库**，属于本机脚本。
> 若日后 `.tmp/` 被清理，可用上面的**方式二**一条命令还原，无需脚本文件。

---

## 5. 重新建立白名单

若换了 bin 目录或新增 worktree 导致又弹窗，重新运行
`<仓库根>/.tmp/add-litemonitor-test-firewall-rules.ps1`（或 `<仓库根>/.tmp/run-fw-rules.bat`）即可 ——
脚本会**按当前磁盘上实际存在的 exe 路径**重新枚举并重建，同时清理已失效程序的遗留规则。

---

## 6. 遗留规则清理记录

建立白名单时顺带清除了 **8 条失效程序遗留规则**（`DisplayName` 为 `testhost` / `WebProbe`，
但其 `Program` 指向的 exe 已不存在，例如并发会话期间的 `.tmp/verification/artifacts-upstream/.../webprobe.exe`）。
这类规则是纯垃圾 —— 路径没了，永远不会再触发，却仍占着规则列表。

---

## 7. 相关文档

- 仓库根 `README.md` / `REBRAND.md` 里出现的"防火墙"字样指的是**产品内**的用户引导文案
  （WebServer 开启时提示用户放行 exe），与本文件描述的**开发机**白名单是两件事。
- 复审文档：`docs/reviews/2026-10-24-candidate-worktree-implementation-review.md`
  §11.4 记录了同期发现的历史间歇失败（C-1）及其基线对照实测。
