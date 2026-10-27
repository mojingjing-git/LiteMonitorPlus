# 安全一致性修复（v1.1）执行证据

对应计划：`docs/plans/2026-10-27-confirmed-safety-consistency-repair-plan.md`（v1.1）
执行日期：2026-10-28。全部改动基于会话起点工作区（HEAD 4de25d3 + 未提交改动），未提交。

## 包状态总览

| 包 | 状态 | 核心产物 | 专项测试 |
|----|------|----------|----------|
| S00 基线 | 已通过 | 基线构建 0 错误 44 警告（既有 xUnit 分析器噪音）；基线测试 2768 通过 / 0 失败 / 8 跳过 | — |
| S01 凭据保护失败停止持久化 | 已通过 | `src/Core/Settings.cs`（转换器用途化 + 接缝）、`src/Core/SettingsHelper.cs`（`SettingsSaveOutcome`）、`src/UI/SettingsForm.cs`、9 语言文件 | `SecretPersistenceSafetyTests` 9/9 |
| S02a 目标授权 + 地址预检 | 已通过 | `src/Plugins/PluginAddressPolicy.cs`、`PluginAddressPreflight.cs`、`PluginLocalNetworkAuthorizer.cs`、`PluginProxyAuthorizer.cs`（新）；`PluginProcessor.cs`、`PluginExecutor.cs`、`PluginPage.cs`、`PluginManager.cs` | `PluginAddressPolicyTests` 65/65 |
| S03 事务化卸载 + 恢复对账 | 已通过 | `src/Plugins/PluginMarket.cs`（`Operation` 字段 + `ReconcileUninstall`）、`src/Plugins/PluginMarketUninstallCore.cs`（五边界 + 提交点）、`tools/MarketUninstallCrashProbe`（新） | 进程内 12/12 + 子进程 6/6 |
| S04 统一颜色校验 | 已通过 | `src/Core/ThemeColorParser.cs`（新）；`ThemeManager`、`LegacyFallbackStyleResolver`、`ThemeEditorForm`、`ThemeMarketValidator`、`ThemeExportService` | `ThemeColorParserTests` 36/36 |
| S05 阈值只读 + 会话清理 | 已通过 | `ThemePropertyCatalog.cs`、`ThemeDocumentSession.cs`、`ThemeDocumentHistory.cs`、`PropertyInspector.cs`、`ThemeStudioForm.cs` | `ThemeThresholdsCleanupTests` 10/10 |
| S06 GPU 手动恢复 | 已通过（代码层） | `RenderingRouteStatusBuilder.cs`、`RenderingRoutePage.cs`、`SettingsForm.cs`、`GpuThemePreviewControl.cs`、9 语言文件 | `RenderingRouteStatusBuilderTests` 16/16 |

## 各包验收要点

### S01
- DPAPI 保护失败 → `SecretProtectionFailureException` → 整次保存取消（`CredentialProtectionFailed`），主文件与备份字节不变、无临时文件残留；错误信息含参数名、不含凭据值。
- `DeepClone`/`CloneForDiff` 走 MemoryClone 转换器：0 次 Protect/Unprotect 调用（计数器断言）。
- 旧明文配置可读，下次保存迁移为密文；不可读密文保留原文并停用实例。
- 凭据保存失败时市场记录变更回滚（`TryChangeMarketRecord` 返回 false + 内存列表回滚）——9.2 交叉检查通过。

### S02a
- 「允许发请求」与「允许带凭据」两个独立检查：未声明未授权目标（无凭据）首跳即拒（`RequestTargetNotAllowed`），带凭据维持 origin 授权。
- 直连发送前 DNS 解析整组判定：任一禁用类地址（未指定/链路本地/云元数据/组播保留）→ 拒；本机/私网需实例级本地网络授权；解析失败拒。
- IP 字面量分类覆盖 IPv4/IPv6、v4-mapped、NAT64、6to4 变体（65 个分类用例）。
- 插件显式代理需实例级确认（`PluginProxyNotAuthorized`）；授权/撤权变更推进 `PluginNetworkPolicy.Version` → 客户端池与步骤缓存失效。
- 系统代理默认可用，未新增弹窗；代理路径跳过本地 DNS 解析（企业代理不断网），IP 字面量仍判。
- 生产网络请求覆盖面：插件 HTTP/原生步骤（`PluginExecutor`）+ 市场数据侧独立 HTTP 通道（`CryptoNative.FetchAsync` 逐跳预检，`instanceId` 贯穿）。`CityCodeResolver` 仅访问内建固定公网主机（无用户可控目标），未接入预检，记录为已知限制。
- **已知限制**：系统代理场景 `SocketsHttpHandler` 默认走系统代理，`FetchRawAsync` 无法从 handler 区分直连与系统代理转发；插件显式代理路径已按计划 4.3.7 处理（跳过本地解析、保留目标授权），系统代理与 PAC 的实网行为见「9.2 待办 4」。
- **残余窗口（发布口径）**：预检通过后到实际连接之间仍存在 DNS 重绑定时间差；本包不承诺连接 IP 已绑定（S02b 后续）。

### S03
- 卸载事务五边界：意图写入 / 备份完成 / 载荷删除 / 记录提交（提交点）/ 清理；进程内模拟强杀 + 独立子进程强杀（`MarketUninstallCrashProbe`）各覆盖。
- 提交点前中断 → 恢复载荷 + 记录保留；提交点后中断 → 完成卸载；每场景跑两次验证幂等。
- 最危险窗口（`File.Delete` 后、记录移除前）子进程强杀 → 重启后载荷恢复且摘要一致。
- 记录在对账期间被改动 → 保留待处理（不伪造成功）。
- §5.3 边界用例：卸载前载荷已缺失（完成可证明卸载、不合成）、备份损坏（保留待处理、不伪造载荷）、记录不可用（拒绝且无副作用）、目标被外部替换（以备份真载荷恢复）；文件占用由 `MarketFileIo.MoveAtomic` 有界重试与回滚路径既有合同覆盖。

### S04
- 合法输入（#RRGGBB / #AARRGGBB / rgba / 命名色 / 空白回落）精确保持原义，`#80FF0000` alpha=128 在前。
- 非法输入 15 类明确失败并携带可定位诊断；失败与合法透明在缓存与结果结构中严格区分。
- 工作室保存前校验阻止 + 市场下载校验（`theme_color_invalid`）+ 投稿材料校验，三端同一校验器。
- 运行时 `ParseColor` 保持兜底语义（失败回透明、不抛），诊断输出到调试通道。

### S05
- 目录不再暴露 thresholds 可编辑属性；工作室显示「状态阈值来自当前生效设置；主题负责颜色」只读说明与当前生效摘要。
- 会话级清理覆盖：普通覆盖、大小写变体、子树、删除标记、撤销 / 重做历史、恢复快照（clean 分支）；冲突副本保留不猜原值。
- 源文件已有 thresholds 块原样读写保留；源无块时不合成。

### S06
- 保守状态在渲染状态页可见（原因码转文案，原码保留在详情）；提供「重试 GPU 渲染」申请按钮。
- 一次申请一次尝试：确认弹窗 → 申请标记 → 「应用」消费并确认；刷新 / 离页 / Tick 不产生新尝试；失败可再次手动申请。
- 存储不可用时重试被拒并如实提示，不删标记绕过；首帧实际呈现才清除守卫（既有链路未改）。
- 生产守卫与预览守卫作用域分别说明（主面板提示 + 预览错误面板说明）。
- **未验证**：GPU 实机首帧呈现（需真实 GPU 环境，见下）。

## 全量回归与构建

- 主项目构建（Debug，`bin\eval-dbg`）：0 警告 0 错误（每包完成后各验证一轮）。
- 全量测试：执行后汇总（见文末更新记录）。
- Release 构建 + 完整回归 + 冒烟：见「9.2 待办」。

## 9.2 待办与未验证项

1. GPU 实机验证（S06 首帧恢复链路、GPU 主题外观）：本环境无独立 GPU 会话，**未验证**，不在本批宣称。
2. 同一候选的 Release 构建与完整回归：随本批执行（见文末更新记录）。
3. 系统代理 / PAC 环境的 S02a 实网冒烟（单元层已覆盖策略逻辑，实网代理行为未验证）。

## 9.3 发版口径（约束）

- S02a 是分阶段风险缓解，**不是**完整 SSRF / DNS 重绑定防护；完整防护（连接 IP 绑定、代理终点约束）登记为 S02b。
- 市场索引签名保持开放；本批不关闭该事项。
- 不得宣称「三项 P0 全部关闭」；各包状态以本文件为准。
- 未自动提交 / 推送 / 发布。

## 更新记录

- 2026-10-28（集成验收）：
  - S03 §5.3 边界用例补齐（进程内 12/12：五边界 + 记录变更 + 记录移除失败 + 载荷缺失 + 备份损坏 + 记录不可用 + 目标被替换）。
  - 全量回归初跑发现挂死：S02a 地址预检正确拦截沙箱回归的 127.0.0.1 回环测试服务器（本地目标需用户授权）。修复方式：测试基础设施新增 `LocalNetworkAuthGrant` / `PluginProxyAuthGrant` 作用域与程序集级 `TestModuleInitializer`（授权查询放行，地址分类与整组判定仍完整执行）；预检语义测试自身保持真实授权路径并显式自护。
  - S02a 生产补强：`CryptoNative`（市场数据侧独立 HTTP 通道）接入逐跳地址预检；`EnsureCredentialTargetAllowed` 先于 `EnsureRequestTargetAllowed`（保持既有凭据原因码合同）。
  - 排查第二轮回归失败：测试用回环/链路本地地址（fe80::1）与新预检冲突 → 改用 2001:db8::1 文档前缀；环境 DNS 拦截（任意域名解析到 fe80::1）→ `ResolverForTests` 接缝默认 1.1.1.1（策略语义测试自护保持真实路径）；`ReviewPluginTransport.Install` 注入 mock 后对齐策略版本位，避免池重建覆盖 mock。
  - 排查第三轮 `ReviewPluginReloadRegressionTests` 顺序性挂死（bisect 定位）：`PluginAddressPolicyTests` 构造置空 `ResolverForTests` 后未恢复，泄漏给后续类 → 改为 `IDisposable` 保存/恢复。修复后组合 116/116 全绿。
  - **最终全量回归（同候选）**：Debug 2912 通过 / 0 失败 / 8 跳过（10m30s）；Release 2912 通过 / 0 失败 / 8 跳过（10m48s）。六包实现 + 回归全部通过。
