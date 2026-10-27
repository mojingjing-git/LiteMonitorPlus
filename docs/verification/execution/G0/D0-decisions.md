# G0 D0 决策记录

- **日期**：2026-10-27
- **状态**：已记录，待 G0 会签
- **决策人**：产品/技术 owner（用户）；执行记录：集成
- **规则**：D0 决策只固定本轮执行假设，不改变五份源计划的产品目标；变更需在 §更改记录 追加，不覆盖原行。

## 1. 决策表

| ID | 决策 | 依据 | 状态 | 影响/约束 |
|---|---|---|---|---|
| D0-1 | 本轮按 **2 名开发 A/B + 兼岗集成** 规划；A 负责渲染/主题/设置与合同，B 负责诊断/趋势/场景/低功耗数据；共享文件单 writer | 执行计划容量与文件所有权 | 采纳（编制假设，实际人员待确认） | 若实际只有 1 人，按 §14.1 降为串行，不压缩 Gate 验收 |
| D0-2 | **Slice 1 单独发布**：R0–R5 先出修复版，新功能开关全关 | 修复不应被新功能拖住 | 采纳 | G1 后即可发 Release A；D/T/S/P 不阻塞 |
| D0-3 | 历史 v2 默认 **按天分段 JSONL + manifest**；单文件 64 MiB JSON 需书面特批并 +2 人日 | 降低原子重写峰值/损坏恢复成本 | 采纳 | T03 按分段实现；迁移/截断/备份需测试 |
| D0-4 | 诊断首版只做 **“上次运行是否正常退出”标记**，不做崩溃转储/异常现场 | 进程内事件无法在崩溃后保留；避免过度承诺 | 采纳 | D02 实现启动/正常退出标记；D06 文案明确边界 |
| D0-5 | 功能开关默认 **全部关闭**；诊断只读 beta 可单独开启 | 防止新功能误影响旧用户 | 采纳 | C5/INF-08 落地；Release A 全关 |
| D0-6 | R5 使用 **真实 GPU 机器**；无 GPU 不签收 F03，Release A 不得声称修复 GPU 门禁 | F03 是真实资源门禁，模拟不能替代 | 采纳（本机已确认 RTX 3080 / 3080 Ti，驱动 32.0.16.1088；须物理显示会话，排除虚拟显示器/RDP） | 测试时禁用 GameViewer/Meta/MuMu/ToDesk 虚拟显示适配器；记录 GPU 型号/驱动/会话 |
| D0-7 | `resources/plugins/vps_traffic.json` 按 **用户本地模板** 处理，本轮不提交；若产品化，另建脱敏模板 + 测试夹具 | 文件未跟踪，含具体主机名；不能把用户配置当产品资源 | 采纳 | F10 回归用测试夹具覆盖 `X-API-Token`，不依赖该文件入库 |
| D0-8 | 范围冻结：本轮只做五份计划内事项；新增需求进 backlog，不插队 | 防止范围蔓延 | 采纳 | 任何范围变更走变更记录并重估 |
| D0-9 | `LanguageManager` 常驻 **en.json 英文底表**；当前语言缺 key 先回退英文，再返回 key | 9 语言资源必须可回退 | 采纳 | INF-09 改造；CI 用 de/ja 缺 key 用例验证 |
| D0-10 | Settings 拆分 **`Settings.Alerts.cs` / `Settings.Power.cs`**；A 为 `Settings.cs` 唯一 writer，B 只改分部类，A 复核合并 | 降低单 writer 冲突 | 采纳 | 分部类不改变序列化形状；`[JsonExtensionData]` 保留未知字段 |

## 2. D0-6 机器证据

```text
Get-CimInstance Win32_VideoController
NVIDIA GeForce RTX 3080    32.0.16.1088  OK
NVIDIA GeForce RTX 3080 Ti 32.0.16.1088  OK
GameViewer Virtual Display Adapter / Meta Virtual Monitor /
MuMu Virtual Display Adapter / Todesk Virtual Display Adapter  OK
```

结论：具备真实 NVIDIA GPU；虚拟显示适配器必须排除，R5 证据记录物理显示器、分辨率、DPI、驱动版本和会话类型。

## 3. D0-7 证据

```text
git ls-files resources/plugins
→ 不含 vps_traffic.json
git check-ignore -v resources/plugins/vps_traffic.json
→ 无输出（未被忽略，只是未跟踪）
```

结论：该文件是本地模板/用户配置，不纳入产品资源；F10 的回归夹具单独创建，使用占位主机与 `X-API-Token` 头。

## 4. G0 会签含义

D0-1～D0-10 记录后，G0 的决策条件满足；仍需：

1. INF-00/INF-01 证据复核（已完成）。
2. C1–C3 合同与测试复核（已完成，13/13）。
3. INF-05 审计复核（已完成）。
4. 集成与独立复核签字（本表与 G0 README 的签收栏）。
5. INF-10 协作规则签字（PR 模板/证据规则）。

## 5. 更改记录

| 日期 | 决策 | 变更 | 原因 |
|---|---|---|---|
| 2026-10-27 | D0-1～D0-10 | 初始记录 | G0 开工 |
