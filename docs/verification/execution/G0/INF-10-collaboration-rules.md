# INF-10 协作规则（G0 会签版）

- **适用**：本轮执行计划（v2.1）全部任务
- **目的**：在 2 人 + 兼岗集成、共享文件密集的前提下，避免并行写冲突、证据缺失和“假绿”
- **状态**：待 G0 会签

## 1. 分支与合并

| 规则 | 内容 |
|---|---|
| 主干 | `master` 只接受通过 PR 的合并；不直接 push |
| 分支命名 | `fix/Fxx-短描述`、`feat/INF-xx-短描述`、`feat/Dxx-短描述`、`feat/Txx-短描述`、`feat/Sxx-短描述`、`feat/Pxx-短描述` |
| 分支起点 | 从最新 `master` 拉出；不跨 Gate 长分支 |
| 合并方式 | squash merge；提交信息含任务 ID（如 `INF-06: wire settings apply results`） |
| 文档/代码 | 计划、审查、证据文档单独 PR；不与代码混提 |
| 冲突 | 共享文件冲突由该文件 owner 解决；不允许“先合再说” |

## 2. 单 writer 规则

以下文件/目录同一时刻只能有一个 writer；需要第二人改动时，先发消息认领，合入后再改：

- `src/Core/Settings.cs`（A；B 只改 `Settings.Alerts.cs` / `Settings.Power.cs`，A 复核）
- `src/Core/Actions/AppActions.cs`
- `src/Core/Actions/Services/ModuleServices.cs`
- `src/UI/UIController.cs`
- `src/Contracts/SamplingModels.cs`
- `src/Contracts/IpcMessages.cs`
- `resources/lang/*.json`
- `src/ThemeEditor/*` 与 `src/Rendering/Presentation/*`（R1–R3 期间 A 独占）

规则：PR 描述里写“本次 writer”；集成在合并前检查同日是否有第二人改同一文件。

## 3. PR 必填检查单

每个 PR 必须包含：

- [ ] 任务 ID（INF/F/R/D/T/S/P/L）与一句话目标
- [ ] 修改文件清单；共享文件已确认单 writer
- [ ] 对应验收项（引用执行计划行号/表格行）
- [ ] 新增/修改测试；测试命令与结果
- [ ] 证据路径（`docs/verification/execution/<Gate>/...`）
- [ ] 兼容性：旧配置/旧 IPC/旧主题是否可读；降级是否保留未知字段
- [ ] 安全/隐私：无凭据、无真实主机名、无用户数据入库
- [ ] 回滚：开关名/回滚步骤
- [ ] 未验证项与剩余风险

## 4. 证据规则

- 每个 Gate 一个目录：`docs/verification/execution/G0`、`G1`…；不按日期建目录。
- 日志必须含命令、exit code、时间、HEAD；截图必须含窗口/DPI/会话说明。
- 失败证据也保留；不得只保留通过记录。
- 真实 GPU/DPI/电源证据单独命名，注明物理/虚拟会话。
- 证据文件不写密钥、token、真实主机名；必要时脱敏。

## 5. 评审与门禁

| Gate | 评审 | 签字 |
|---|---|---|
| G0 | 集成 + 独立复核 | 两人 |
| G1 | A/B 互审 + 独立复核 | 两人 + 集成 |
| G2/G3/G4 | 功能 owner + 独立复核 | 两人 |
| G5 | 集成 + 独立复核 + 发布 owner | 三人 |

- 合同（C1–C5）变更必须由合同 owner 提案，另一人复核，记录在计划 §更改记录。
- P0/P1 未关闭不得进入下一 Gate；“已知问题”不能代替关闭。
- 任何“跳过测试/放宽阈值/关闭校验”的 PR 直接拒绝。

## 6. 并行与 WIP

- 同时最多 **2 条写代码线**；第三条只做只读审计/测试/文档。
- 共享文件合并窗口：每天固定一次（建议 17:00），其余时间不合并共享文件。
- 每日 15 分钟同步：昨天完成、今天计划、阻塞、共享文件认领。
- 阻塞超过 4 小时必须升级给集成，不静默等待。

## 7. 变更与范围

- 新需求只进 backlog，不插入当前 Slice。
- 估算变化 >20% 或关键路径变化 >2 天，必须重排 Gate 并通知。
- D0 决策变更追加到 `D0-decisions.md` 的更改记录，不覆盖原决策。

## 8. 会签

| 角色 | 姓名 | 日期 | 结论 |
|---|---|---|---|
| 开发 A |  |  | 待签 |
| 开发 B |  |  | 待签 |
| 集成 |  |  | 待签 |
| 独立复核 |  |  | 待签 |
