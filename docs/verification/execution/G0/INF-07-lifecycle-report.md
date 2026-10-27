# INF-07 E04 生命周期收口报告

- **日期**：2026-10-27（Asia/Shanghai）
- **范围**：`MainForm` 退出顺序、Web 服务静态实例、插件管理器停止
- **结论**：代码与回归完成；真实窗口 50 次开关/句柄计数烟测留到 G1 实机执行。

## 1. 收口前的缺口

1. `MainForm.OnFormClosed` 直接按顺序调用 `_cfg.Save()`、`TrafficLogger.Save()`、`HardwareHistoryLogger.Save()`、`LiteWebServer.Instance?.Stop()`、`_ui.StopLiveGlass()` 等；任一调用抛异常会阻断后续清理。
2. `LiteWebServer` 由 `new` 创建但未保存引用；退出时通过静态 `Instance` 查找，且只 `Stop()` 不 `Dispose()`，静态字段仍指向已停止实例。
3. `PluginManager` 在启动时 `Start()`，退出路径没有 `Stop()`；插件计时器/运行时可能残留。
4. 缺少可测试的退出顺序与失败隔离点。

## 2. 本次修改

### 2.1 `src/UI/MainForm_Transparent.cs`

- 新增字段 `private readonly src.WebServer.LiteWebServer? _webServer;`，构造时保存 `new LiteWebServer(_cfg)` 的引用。
- `OnFormClosed` 改为逐项 `SafeShutdown(name, action)`：
  1. `Settings.Save`
  2. `TrafficLogger.Save`
  3. `HardwareHistoryLogger.Save`
  4. `PluginManager.Stop`
  5. `LiteWebServer.Dispose`
  6. `base.OnFormClosed`
  7. `UIController.StopLiveGlass`
  8. `UIController.Dispose`
  9. `MainFormBizHelper.Dispose`
  10. `WindowAppearance.Dispose`
- `SafeShutdown` 捕获异常并写 `Debug` 痕迹，保证后续步骤继续执行。

### 2.2 `src/System/WebServer/LiteWebServer.cs`

- `Dispose()` 在 `Stop()` 后仅当 `ReferenceEquals(Instance, this)` 时清空静态 `Instance`，避免旧实例误清新实例。

## 3. 验证

| 项目 | 结果 | 证据 |
|---|---|---|
| Release Gate 2 等价构建 | exit 0；44 warnings / 0 errors | `inf07-lifecycle-build.log` |
| Web 采样 + 会话协议回归 | 76/76 通过 | `inf07-lifecycle-tests.log` |
| 代码审查 | 退出顺序、异常隔离、静态实例所有权已核对 | 本报告 + `git diff` |

## 4. 未完成/剩余限制

- **未执行**真实窗口 50 次打开/关闭与 GDI/User 句柄计数；需要交互式桌面，归入 G1 手工烟测，不能由单元测试替代。
- `PluginManager` 没有 `IDisposable`，当前使用既有 `Stop()`；若后续插件运行时新增非托管资源，需要再扩展释放接口。
- `SafeShutdown` 的异常痕迹仅进 `Debug` 输出；Release 下若需持久化，应在 INF-10/诊断事件接入后改为统一事件出口，不在本工作包内私自新增日志文件。
