# 派发单 P-B4 —— 指标三拆（样本/纯规则/显示状态）

| 批次 | 前置 | 分支 | 覆盖 |
|---|---|---|---|
| B4-1（本轮） | B3 完成（`171ff2d`）；fixture 181+22 在位 | 主工作树直接提交 | Plan B §4.3/§5 B4 行（第一增量） |
| B4-2（后续） | B4-1 验收 | 待派 | MetricUtils 深度净化（6 处全局读取点：Settings×4/InfoService×1/静态发布器×1） |

## 范围与设计决定（B4-1）

1. **MetricItemFormatter（纯编排器，新文件 `src/Core/MetricItemFormatter.cs`）**：GetFormattedText 的全部决策逻辑提为纯函数——缓存失效阈值（0.005）、电池电源态强制刷新、TextValue 智能补单位、自定义任务栏格式→不做数值压缩、插件色优先/阈值兜底、双上下文（Panel/Taskbar）文本组装。单位/数值字符串经**注入委托**（适配层传 MetricUtils 方法组）获取——编排器自身零全局引用（InfoService/Settings/LanguageManager 均不可见）。
2. **MetricItem 适配化**：删除 `using InfoService`；插件色与配置兜底改经 `DefaultPluginColorResolver`/`DefaultConfigLookup` **静态组合根委托**（UIController 构造时接线：`k => InfoService.Instance.GetValue(k)`、`k => Settings.Load().MonitorItems.FirstOrDefault(...)`——全局查询移到应用层 lambda，Plan B §4.3"插件颜色由应用层取得后作为样式输入"）。静态默认保证 ThemeEditor 预览项等 BoundConfig==null 路径行为逐字不变。
3. **护栏测试**：源码扫描 MetricItem/MetricItemFormatter 无 `InfoService.Instance`、MetricItem 无 `Settings.Load()`、Formatter 无任何全局服务符号——§6.2"纯规则不查询全局服务"可执行化。
4. **行为保持**：181 条格式 fixture + 22 条 MetricItem 组装 fixture 全绿为准绳；新增纯函数单测钉缓存阈值边界/电池刷新/补单位/插件色优先级。

## 明确不做（记录待裁决）

- **P-B0b 报告 10 项疑似缺陷**（compact 形参失效、`{u}/s` 双后缀、大小写敏感 RecordedMax 等）：B4-1 **固化现状**（fixture 期望不改），修/不修由用户逐项裁决后另立任务——混入重构提交会污染行为保持证据。
- MetricUtils 深度净化 → B4-2（GetValueStr/GetUnitStr 内部的 Settings/InfoService/静态发布器读取参数化，MetricUtils 变薄壳）。
- Web 端消费纯规则、历史读原始样本 → B4-2/B5 随消费面接入。

## 验收

构建 0 错误；全量测试不低于 2412 发现数且 0 失败；architecture-assert 12/12（A-2 预期 6→5：MetricItem 移除 InfoService using，带史下调）；护栏测试绿；fixture 全绿。
