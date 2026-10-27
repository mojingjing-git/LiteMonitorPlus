# T03 历史格式与写入接管

- 日期：2026-10-27
- 范围：长期趋势 v2 按天分段 JSONL、manifest 增量导入、损坏行跳过、容量/保留期淘汰、`HardwareHistoryLogger` 写入接管。
- 结论：**核心自动化通过；真实 7 天数据、磁盘满/断电、稳定设备身份和统计口径对照仍未验证**，不能据此宣称 T03 全量验收完成。

## 实现

- `src/Core/Trends/TrendHistoryV2Store.cs`
  - `TrendHistoryPointRecord`：时间、指标、稳定设备 ID、值、质量、样本年龄。
  - 按本地日期写 `trend-yyyy-MM-dd.jsonl`；manifest 记录版本、最后导入时间、总点数、更新时间。
  - `Append` 追加并更新 manifest；`ReadSince` 按时间过滤、跳过坏行并计数；`EnforceBytes` 优先删除最旧分段；`TrimToNow` 执行 7 天保留。
  - `ImportV1` 只读旧 `HardwareHistory.json`，按 `LastImportedTimestampUtc` 增量导入，重复调用不重复追加。
- `src/Core/HardwareHistoryLogger.cs`
  - `Load` 创建/读取 v2；首次启动从旧文件导入，之后优先读 v2，失败回退旧数据。
  - `FlushCurrentBucketUnsafe` 将分钟点追加到 v2。
  - `SaveCore` 只调用 v2 `TrimToNow`，不再写旧 `HardwareHistory.json`，避免双写。
- 测试：`tests/LiteMonitor.Tests/TrendHistoryV2StoreTests.cs`
  - `AppendAndReadSince_AcrossDailyFiles`
  - `ImportV1_IsIncrementalAndIdempotent`
  - `ReadSince_SkipsCorruptLineAndCountsIt`
  - `EnforceBytes_DropsOldestSegmentsFirst`（测试上限按单条约 123 B 调整为 400 B，确保真的触发淘汰）

## 验证

```text
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~TrendHistoryV2StoreTests"
=> 4 passed, 0 failed, 0 skipped

dotnet build LiteMonitor.csproj -c Release --no-restore \
  -p:TreatWarningsAsErrors=true \
  -p:WarningsNotAsErrors=...
=> 0 warnings, 0 errors
```

日志：`t03-tests.log`、`t03-build.log`。

## 未闭合

1. 未用真实 7 天历史做保留/容量/统计口径对照。
2. 未做磁盘写满、断电、manifest 写失败后的恢复演练；`ImportV1` 在“文件已追加但 manifest 未落盘”极端情况下仍可能重复导入，需在 T03 收口前补幂等键或导入批次记录。
3. v2 点当前 `StableDeviceId=null`，多设备稳定身份尚未接入。
4. T04 趋势窗口快捷入口/分辨率/游标、T05 告警引擎、T06 通知接线、T07 长时资源验收未开始。
