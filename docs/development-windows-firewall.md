# 本机开发环境：Windows 防火墙与测试弹窗

> **当前状态（2026-10-25 23:10）**：Windows 防火墙三个配置文件已**临时关闭**
> （Domain / Private / Public 全部 `Enabled=False`），入站默认动作保留 `Block`。
> **作用**：让自动化测试运行时**不再弹「Windows 安全中心警报」**要求人工点确认。
> **恢复**：见 §5 —— 说一声「恢复防火墙」即可，或手动运行恢复脚本。

---

## 1. 弹窗的真正根因（实测）

LiteMonitor 的两类程序会**以入站方式监听端口**：

- `testhost.exe` —— VSTest 测试宿主，跑测试时启动；
- `LiteMonitor++.exe` —— 主程序内置 WebServer（`HttpListener`，见 `src/System/WebServer/LiteWebServer.cs`）。

Windows 的行为是：**当一个可执行文件首次尝试入站监听时，弹出「Windows 安全中心警报」要求人工确认**。
Windows 自动生成的规则 ID 形如：

```
Query User{GUID}G:\tool\litemonitor-fork\tests\litemonitor.tests\bin\release\net8.0-windows\testhost.exe
```

**带 GUID** ⇒ 规则与「具体路径 + 具体端口」绑定。所以：

| 情况 | 是否重新弹窗 |
|---|---|
| 同一路径再次监听 | 否（规则已存在） |
| 换个 bin 目录（Debug ↔ Release ↔ eval-dbg ↔ eval-rel） | **是** |
| 换个 worktree（`.worktrees/f01/...`、`.worktrees/f13/...`） | **是** |
| 换台机器 / 换用户账户 | **是** |

⇒ 这就是"每跑一次测试就要手动点几下、而且永远点不完"的直接原因。

**顺带澄清一个常见误判**：这**不是** Defender 杀毒在拦截。本机实测
`RealTimeProtectionEnabled = False`、`AntivirusEnabled = False`（Defender 本来就是关的），
防火墙日志里也没有任何 `blocked` 记录 —— 弹窗是 Windows 的**入站监听确认提示**，不是拦截。

---

## 2. 走过的弯路：白名单方案为什么失败（重要，别重试）

最初尝试的方案是**保留防火墙开启**，只给测试程序加入站允许规则。
**实测证明这条路在"多 worktree 并行开发"场景下不可行**：

| 尝试 | 实测结果 |
|---|---|
| 按**精确路径**加规则（54 条 → 92 条） | 能解决已存在的路径；**并发会话每新建一个 worktree 就又弹**（实测 1 个 → 9 个 worktree，刚建好 92 条规则后 `f15` 立刻又冒出来） |
| `-Program` 指向**目录**（如 `...\LiteMonitor-fork\.worktrees`） | 规则**创建成功但不生效** —— Windows 的 Program 字段**不递归覆盖子目录**（实测 `f12` 仍弹窗） |
| `-Program` 指向目录 + `\*` 后缀 | **被系统拒绝**：「应用程序包含无效的字符，或具有无效的长度」 |
| `-Program` 指向目录 + `**` 通配 | **被系统拒绝**（同上） |

⇒ **Windows 防火墙的 Program 字段只接受精确的 exe 路径**，没有任何"目录 / 通配"写法。
在并发会话不断新建 worktree 的情况下，按路径维护白名单**注定追不上**。

---

## 3. 现在的方案：临时关闭防火墙

三个配置文件设为 `Enabled=False`，同时**显式保留 `DefaultInboundAction = Block`**。

**安全取舍（务必知情）**：

- 关闭防火墙 **≠** 放开网络。入站默认动作仍是"阻止"，机器不会直接暴露到局域网。
- 但这仍弱于"防火墙开启"的状态。**测试结束后应尽快恢复**（§5）。
- 出站方向不受影响（`DefaultOutboundAction` 未改）。

---

## 4. 关闭前做了什么

1. **完整状态快照** → `.tmp/firewall-state-backup.json`
   （含三个配置文件的 `Enabled` / `DefaultInboundAction` / `DefaultOutboundAction` /
   `LogFileName` / `LogBlocked` / `LogAllowed`），恢复时逐项还原。
2. **删除此前的白名单规则**（92 条，组名 `LiteMonitor Test Allowlist`）——
   防火墙关闭后它们已无意义，留着反而碍眼。
3. **清理弹窗累积规则**（30 条 `testhost` / `testhost.exe` / `WebProbe`）——
   见 §4.1。

### 4.1 你点"允许"时，Windows 自己也在留规则

除了白名单，Windows 在你每次点「允许」后**自己也会生成一条规则**
（`DisplayName` = `testhost` / `testhost.exe`，**Group 为空**）。
这些规则随 worktree 增长而累积 —— 实测已 **30 条**，其中：

| 分类 | 条数 | 说明 |
|---|---|---|
| 指向**已不存在**的程序 | **8** | 指向 2026-10-23 的旧 codex 工作树路径（`~\.codex\worktrees\review-remediation-20261023\...`），程序早被删除 ⇒ **纯垃圾，且是"允许已删程序入站"的宽口子** |
| 指向仍存在的程序 | 22 | 覆盖 f01/f03/f04/f08a/f12/f13/f15/r06-namespace 与主工作树的 `bin/debug`、`bin/eval-dbg`、`bin/release` |

⇒ 清理脚本 `.tmp/cleanup-auto-firewall-rules.ps1` 会删掉全部 30 条；
恢复脚本也已升级为**恢复时一并清理**，避免恢复后规则列表仍然很脏。

---

## 5. 如何恢复

**方式一（推荐）**：对 AI 说「**恢复防火墙**」。

**方式二（手动）**：双击 `<仓库根>/.tmp/restore-windows-firewall.bat`（会弹一次 UAC，点「是」）。
它会读取 §4 的快照**逐项还原**，而不是笼统地"全部打开"。

**方式三（已在本会话的管理员 PowerShell 中）**：

```powershell
pwsh -NoProfile -File '<仓库根>/.tmp/restore-windows-firewall.ps1'
```

> `.tmp/` 已被 `.gitignore` 忽略（`/.tmp/`），这些脚本**不入库**，属于本机脚本。
> 若 `.tmp/` 已被清理，可用快照文件手工还原，或退回保守策略：
> 三个配置文件全部 `Enabled=True` + `DefaultInboundAction=Block`。

**恢复后会看到什么**：防火墙重新启用后，测试进程再次以新路径入站监听时**会重新弹确认提示** ——
这是 Windows 的正常行为，恰好说明防护已恢复生效。

---

## 6. 重新建立白名单（若将来想恢复"防火墙开启 + 不弹窗"）

前提：**必须能接受"每新建一个 worktree 就重跑一次"**。

`<仓库根>/.tmp/add-litemonitor-test-firewall-rules.ps1` 会按**当前磁盘上实际存在的 exe 路径**
枚举并重建精确路径规则，同时清理已失效程序的遗留规则。
（脚本内含 `\*` / `**` 通配探测；若将来 Windows 支持了通配，它会自动改用通配写法。）

---

## 7. 相关文档

- 仓库根 `README.md` / `REBRAND.md` 里出现的"防火墙"字样指的是**产品内**的用户引导文案
  （WebServer 开启时提示用户放行 exe），与本文件描述的**开发机**设置是两件事。
- 复审文档：`docs/reviews/2026-10-24-candidate-worktree-implementation-review.md`
  §11.4 记录了同期发现的历史间歇失败（C-1）及其基线对照实测。
