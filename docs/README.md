# LiteMonitor++ 文档导航中心 (Docs Index)

欢迎来到 LiteMonitor++ 的文档与设计中心。  
本项目从原上游派生并进行了深度的稳定性改造、多显卡支持、GPU 液态玻璃渲染及进程隔离架构。  
完整变更历史与特性清单请查阅根目录 [**`CHANGELOG.md`**](../CHANGELOG.md)。

---

## 🟢 当前活跃文档（Active Living Docs）

以下文档代表项目的当前正在推进的架构设计与实现进度，开发中请以这些文档为准：

| 领域 | 核心文档 | 状态 / 目标 |
|---|---|---|
| **主题工作室** | [Theme Studio 下一阶段计划](plans/2026-10-26-theme-studio-next-stage-plan.md) | **✅ 已交付**：原生窗口、多形态预览与实机验收（Phase A–E 整改已并入 master） |
| **主题工作室** | [Theme Studio 重做总方案](plans/2026-10-26-theme-studio-rebuild-plan.md) | **✅ 已交付**：苹果风格工作区与属性目录规范 |
| **主题工作室** | [Theme Studio 优化意向储备](plans/2026-10-26-theme-studio-optimization-intent-plan.md) | 后续功能储备清单 |
| **进程隔离** | [Plan A：硬件采集进程隔离](plans/2026-10-26-hardware-process-isolation-plan.md) | **I0~I2 已并入 master**，HardwareHost 独立采样子进程 |
| **架构解耦** | [Plan B：架构解耦与职责整理](plans/2026-10-26-architecture-decoupling-plan.md) | **B0~B3 已并入 master**，B4 推进中 |
| **工程质量** | [工程改进与测试收口计划](plans/2026-10-26-post-review-engineering-improvement-plan.md) | E00~E08 测试套件与集成基线 |
| **代码质量** | [可空引用与编译警告治理计划](plans/2026-10-26-nullable-warnings-remediation-plan.md) | **✅ 已全量合入 master**：分批消除全部警告，nullable-gate 已进 CI 门禁 |
| **本地环境** | [Windows 防火墙与权限配置](development-windows-firewall.md) | Web 模块与网络采集本地开发调试说明 |

---

## ⚪ 已完成里程碑与技术规格（Milestones & Specs）

| 规范 / 方案 | 路径 | 说明 |
|---|---|---|
| **视觉与 GPU 路线** | [`APPEARANCE_PLAN.md`](APPEARANCE_PLAN.md) | GPU 材质渲染规范与三条渲染路线标准 |
| **品牌合规手册** | [`REBRAND.md`](REBRAND.md) | 品牌解耦、程序集重命名与版权合规指引 |
| **38 项 Bug 修复总表** | [`verification/2026-10-25-SUMMARY-MASTER-TABLE.md`](verification/2026-10-25-SUMMARY-MASTER-TABLE.md) | 针对上游 38 项 Bug 的核实总证据表 |
| **上游 Bug 修复总案** | [`plans/2026-10-25-verified-bugs-remediation-plan.md`](plans/2026-10-25-verified-bugs-remediation-plan.md) | F01~F17 专项修复母计划（已全量交付） |

---

## 📁 目录结构说明

- **`plans/`**：核心设计与重构实施计划，以及 `dispatch/` 并行派工单。
- **`design/`**：UI/UX 设计资源、Tokens 配置、主题工作室截图与设计规范。
- **`verification/`**：自动化门禁、性能基线探针结果、以及 `acceptance/` 逐项任务的最终验收报告。
- **`reviews/`**：多轮独立审查报告与测试日志。
- **`superpowers/`**：A00~A17 早期冲刺工作包与契约文件（供单元测试和工程引用保留）。
- **`archive/`**（已在 `.gitignore` 排除）：收纳早期的摸底问卷、上游调研快照与已过时的替代计划。
