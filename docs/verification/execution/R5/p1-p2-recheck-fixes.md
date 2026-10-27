# P1/P2 复核问题修复证据（N01–N03）

- 日期：2026-10-27（Asia/Shanghai）
- 来源：`docs/reviews/2026-10-27-p1-p2-recheck-review.md`
- 结论：N01/N02/N03 已修复；定向 23/23、C03 父/子 2/2、全量 **2759 通过 / 8 跳过 / 0 失败（2767）**，7m59s（`r5-full-tests-recheck.log`）。

## N01 GPU 压力测试必须真的使用 GPU

- `ThemeStudioResourceTests.RunScenario4Body` 在子进程内设置 `Settings.Load().GpuThemeEffectsEnabled = true`，结束后恢复原值。
- 先等待并断言 `ThemePreviewControl.IsGpuPreviewActive == true`，且 `PresentedFrameCount > 0`；再记录 `DeviceCreationCount`/`PresentedFrameCount`。
- 200 次切换后断言呈现帧数推进、设备创建数不增长；GPU 不可用时直接失败，不把 GDI 回退资源数据当作 GPU 基线。
- 命令：`dotnet test ... --filter "FullyQualifiedName~场景4"`，父/子合计 **2/2 通过，3m12s**；日志 `r5-c03-child.log`。

## N02 子进程验证当前产物且拒绝零匹配

- 子进程由 `dotnet vstest <AppContext.BaseDirectory>/LiteMonitor.Tests.dll --TestCaseFilter:C03=Child` 启动，不再走 `dotnet test <csproj> -c Release` 的默认 `bin/Release`。
- 父测试通过 `LITEMONITOR_C03_SENTINEL` 要求子用例写哨兵文件；零匹配、旧产物无 trait、子进程未执行时直接失败。
- 零匹配探针 `C03=NoMatch`：`dotnet vstest` exit=1，且无 sentinel；父测试不会把零测试当成功。

## N03 脱敏不能误删普通数字

- `DiagnosticRedaction.ContainsIpLiteral` 只在 token 含冒号（IPv6）或恰为 4 段点分（IPv4）时调用 `IPAddress.TryParse`；`0`、`1`、`512`、`1200`、`42.0` 不再被当 IP。
- 同时保留既有 IPv4/IPv6、Windows 任意盘符路径、Unix 绝对路径规则。
- `DiagnosticEventStoreContractTests` **7/7 通过**；新增断言 `IsSafeValue("512"/"42.0"/"1200")`。

## 构建

```powershell
dotnet build LiteMonitor.csproj -c Release --no-restore --nologo
# 0 warnings / 0 errors

dotnet build tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-restore --nologo
# 44 warnings / 0 errors（既有 xUnit 分析器警告）

dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-build --nologo
# 2759 passed / 8 skipped / 0 failed / 2767 total, 7m59s
```
