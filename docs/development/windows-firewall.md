# 本机开发环境：Windows 防火墙与入站监听确认弹窗

本文说明开发机上「Windows 安全中心警报」弹窗的成因、当前采用的处置方式，以及恢复步骤。

---

## 1. 触发弹窗的程序

下列可执行文件会以**入站**方式监听端口：

| 程序 | 来源 |
|---|---|
| `testhost.exe` | VSTest 测试宿主，跑测试时启动 |
| `LiteMonitor++.exe` | 主程序内置 Web 服务（`HttpListener`，见 `src/System/WebServer/LiteWebServer.cs`） |

Windows 的行为是：当某个可执行文件**首次**尝试入站监听时，弹出「Windows 安全中心警报」要求人工确认。自动生成的规则 ID 形如：

```
Query User{GUID}G:\tool\litemonitor-fork\tests\litemonitor.tests\bin\release\net8.0-windows\testhost.exe
```

带 GUID 表示规则与「具体路径 + 具体端口」绑定。因此弹窗的出现条件是：

| 情况 | 是否重新弹窗 |
|---|---|
| 同一路径再次监听 | 否（规则已存在） |
| 换一个构建输出目录（Debug ↔ Release ↔ 自定义配置） | 是 |
| 换一个工作副本目录 | 是 |
| 换一台机器或换一个 Windows 账户 | 是 |

这解释了「每跑一次测试都要手动点几下、而且永远点不完」的直接原因。

**这不是杀毒软件拦截。** 弹窗是 Windows 的入站监听确认提示，与 Defender 的实时防护状态无关；防火墙日志里也不会出现对应的 `blocked` 记录。

---

## 2. 为什么不能靠入站允许规则解决

Windows 防火墙规则的 `Program` 字段**只接受精确的可执行文件路径**，没有任何目录或通配写法：

| 写法 | 行为 |
|---|---|
| 按精确路径加规则 | 对已存在的路径有效；每新增一个构建输出目录就需要重加一次 |
| `-Program` 指向目录 | 规则创建成功但不生效——`Program` 字段不递归覆盖子目录 |
| `-Program` 指向目录 + `\*` | 被系统拒绝：「应用程序包含无效的字符，或具有无效的长度」 |
| `-Program` 指向目录 + `**` | 被系统拒绝 |

结论：**按路径维护入站允许规则无法覆盖不断变化的构建输出目录**，这条路不作为常规方案。

---

## 3. 当前的处置方式

三个防火墙配置文件（Domain / Private / Public）设为 `Enabled=False`，同时**显式保留 `DefaultInboundAction = Block`**。

安全取舍：

- 关闭防火墙**不等于**放开网络——入站默认动作仍是「阻止」，机器不会因此直接暴露到局域网。
- 该状态仍弱于「防火墙开启」。测试结束后应尽快恢复（见 §5）。
- 出站方向不受影响，`DefaultOutboundAction` 未修改。

---

## 4. 关闭前的准备

1. **完整状态快照**写入 `%LOCALAPPDATA%\LiteMonitor++\windows-firewall\firewall-state-backup.json`，包含三个配置文件的 `Enabled` / `DefaultInboundAction` / `DefaultOutboundAction` / `LogFileName` / `LogBlocked` / `LogAllowed`，恢复时逐项还原。
2. **删除入站允许规则组**（组名 `LiteMonitor Test Allowlist`）——防火墙关闭后它们已无意义。
3. **清理项目拥有的规则**：清理脚本只处理 `LiteMonitor Test Allowlist` 组、LiteMonitor 生成的显示名前缀，或 Program 路径位于当前仓库内的 `testhost` / `WebProbe` 自动规则；名称相同但指向其他项目的规则绝不会删除。先运行 `pwsh -File scripts/windows-firewall/cleanup-auto-firewall-rules.ps1 -WhatIf` 审阅。

---

## 5. 恢复方式

**方式一**：在管理员 PowerShell 终端运行 `scripts/windows-firewall/restore-windows-firewall.bat`。批处理脚本不会自行请求 UAC；可先用 `-WhatIf` 查看恢复计划。它读取 §4 的已验证快照并逐项还原，而不是笼统地全部打开。

**方式二**：在管理员 PowerShell 中执行

```powershell
pwsh -NoProfile -File "<仓库根>\scripts\windows-firewall\restore-windows-firewall.ps1"
```

**方式三（快照已丢失时的保守策略）**：三个配置文件全部 `Enabled=True` + `DefaultInboundAction=Block`。

> 脚本位于 `scripts/windows-firewall/`，需随文档一起纳入版本库；运行需要对应用户的管理员权限。批处理不会自动提权，请从管理员终端启动，并检查退出码。
> 快照文件由 `disable-windows-firewall.ps1` 在关闭时生成，位置为 `%LOCALAPPDATA%\LiteMonitor++\windows-firewall\firewall-state-backup.json`，不入库。

**恢复后会看到什么**：防火墙重新启用后，测试进程以新路径入站监听时会重新弹确认提示。这是 Windows 的正常行为，说明防护已恢复生效。

---

## 6. 重新建立入站允许规则

前提是**接受「每新增一个构建输出目录就要重跑一次」**。

`scripts/windows-firewall/add-litemonitor-test-firewall-rules.ps1` 按当前磁盘上实际存在的可执行文件路径重建仅限 `127.0.0.1` 的精确规则，且只清理可证明属于本仓库的遗留规则。所有脚本都支持 `-WhatIf`。

---

## 7. 与产品内文案的区分

仓库根 `README.md` 与 `REBRAND.md` 中出现的「防火墙」字样指的是**产品内**的用户引导文案（Web 服务开启时提示用户放行可执行文件），与本文件描述的**开发机**设置是两件事。
