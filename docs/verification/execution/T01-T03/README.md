# T01–T03 趋势数据证据

- 日期：2026-10-27（Asia/Shanghai）
- 范围：T01 快照消费/身份/质量、T02 秒桶环形缓冲、T03 历史 v2 与迁移。
- 结论：**T01/T02 核心合同、容量测试与 1 小时趋势窗展示完成；T03 的 v2 存储/迁移/写入接管核心自动化通过。真实 7 天、磁盘异常、设备身份和统计口径未验证，不能声称趋势功能可交付或 G2 已满足。**

## 1. 已落地

| 工作包 | 交付 | 文件 | 状态 |
|---|---|---|---|
| T01 | 批次去重、乱序拒绝、SessionEpoch 切换清空、StableDeviceId/质量/年龄原样保留、32 序列/3600 桶上限 | `src/Core/Trends/ShortTermTrendBuffer.cs`、`src/UI/UIController.cs` | 核心完成；UI Tick 已消费快照 |
| T02 | 秒桶环形缓冲、容量淘汰、统计（接受/重复/乱序/拒绝/会话重置/丢弃） | 同上 | 核心完成；已接趋势窗 1 小时查询 |
| T03 | v2 按天 JSONL、manifest 增量导入、损坏行跳过、容量/保留期淘汰、`HardwareHistoryLogger` 写入接管 | `src/Core/Trends/TrendHistoryV2Store.cs`、`src/Core/HardwareHistoryLogger.cs` | 核心完成；真实 7 天/磁盘异常/设备身份未验证，见 `T03-README.md` |

## 2. 验证

```text
dotnet build LiteMonitor.csproj -c Release --no-restore
→ 0 warnings, 0 errors

dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-build
  --filter "FullyQualifiedName~ShortTermTrendBufferTests"
→ 7 passed, 0 failed, 0 skipped
```

覆盖点：

- 同一 `BatchId` 重复 100 次只记 1 个点。
- 旧 `BatchId` 拒绝，不改变已有序列。
- `StableDeviceId`、`SampleQuality`、`SampleAgeMs`、观测时刻原样进入桶。
- 容量上限淘汰最旧点并计数。
- `SessionEpoch` 变化清空旧会话，Host 重启后不混入旧数据。
- 32 指标 × 3600 秒 = 115,200 点上限压力用例通过。

## 3. 未完成 / 风险

- `ShortTermTrendBuffer` 已由 `UIController.Tick` 在 `RequestRescanAsync` 后消费 `GetLatestSnapshot` 写入（异常隔离、不触发采样），并已接 `HardwareTrendForm` 的 1 小时查询；更长窗口仍走旧历史。
- 未执行 16 MiB 实测内存曲线；115,200 点是容量上限证据，不等同于内存预算验收。
- T01 的 StableDeviceId 只做透传，尚未处理设备目录换代后的同一物理设备合并/分裂策略。
- T03 的 JSONL/manifest、`LastImportedTimestamp`、v1 只读导入、损坏截断已落地；真实 7 天数据、磁盘写满/断电、manifest 写失败恢复、统计口径对照未验证。
- 不能声称趋势功能已可交付，也不能声称 G2 已满足。

## 4. 下一步

1. 用真实 7 天历史做容量/保留期/统计口径对照，并演练磁盘写满、断电、manifest 写失败恢复。
2. 接入稳定设备身份，补多设备合并/分裂策略。
3. 进入 T04 趋势窗快捷入口/分辨率/游标/主题/DPI 适配，随后 T05 告警引擎。
