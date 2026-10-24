# 启动 LiteMonitor++

双击仓库根目录的 `start.cmd`，在 UAC 提示中允许程序启动。

- 默认启动 `bin/Release/net8.0-windows/LiteMonitor++.exe`。
- 已有构建时直接启动；程序不存在时自动构建，并准备运行资源、备份现有配置。
- 修改源码后需要加 `-Build`，否则启动的是已有构建。
- 启动失败时窗口保留错误信息，按任意键关闭。
- 不会结束已有实例；重新构建前，请先从托盘退出同一输出目录的程序。
- 如果配置中隐藏了主面板，可通过托盘图标显示。

## 常用命令

在仓库根目录的 PowerShell 中执行：

```powershell
# 启动已有 Release（没有构建时自动构建）
.\start.cmd

# 重新构建当前源码，再启动 Release
.\start.cmd -Build

# 构建并启动 Debug
.\start.cmd -Build -Configuration Debug

# 构建时指定 .NET SDK
.\start.cmd -Build -Dotnet "G:\TOOL\dotnet-sdk\dotnet.exe"
```

脚本通过自身位置定位仓库，可以从其他工作目录调用，支持路径中的空格。

需要 PowerShell 7；脚本会在 PATH 和 `%ProgramFiles%\PowerShell\7` 中查找。
首次构建或使用 `-Build` 时需要 .NET 8 或兼容 SDK；已有构建的启动不需要 SDK。
程序运行需要对应的 .NET Desktop Runtime，启动时可能请求管理员权限。

启动脚本不执行 DPI 验收。需要实机验收时，使用现有的 `scripts/run-livecheck.ps1`。

## 启动当前源码（绕开被占用的 bin\Release）

`bin\Release` 被正在运行的旧实例锁定、或想确保跑的是**当前源码**时，用：

```powershell
pwsh -File scripts/start-current.ps1                # 构建当前源码 → bin\run\Release 并启动
pwsh -File scripts/start-current.ps1 -ImportSettings # 首次可把 bin\Release 的旧配置带过来
pwsh -File scripts/start-current.ps1 -Restart        # 结束同目录旧实例后再启动
pwsh -File scripts/start-current.ps1 -NoBuild        # 直接启动已有构建
```

构建输出重定向到 `bin\run\{配置}\`，与被锁的 `bin\Release` 互不影响；settings.json 按运行目录隔离（首次为全新默认配置）。不同目录的实例可以并存；同目录重复启动会立刻退出（用 `-Restart`）。提权中的旧实例从普通 shell 停不掉，请从托盘退出。
