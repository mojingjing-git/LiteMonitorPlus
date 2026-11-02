# LiteMonitor++ 文档

本目录是 LiteMonitor++ 的架构与规格文档集。全部文档**只描述当前状态**：讲清楚这个系统现在是怎么组织的、每块的行为规格是什么、哪些限制是客观存在的。

文档不记录开发过程、计划、批次、评审结论与变更历史——那类信息属于版本控制历史，不属于文档。

---

## 目录结构

| 目录 | 内容 |
|---|---|
| `architecture/` | **架构说明书**：模块边界、依赖方向、跨进程协议、状态机、契约与错误码。回答「系统是怎么搭的、为什么这样搭」。 |
| `specs/` | **行为规格**：采集、派生指标、主题、趋势报警、情景、诊断、低功耗的行为定义、字段语义与边界条件。回答「每块功能具体怎么表现」。 |
| `design/` | **视觉与交互设计资源**：GPU 主题的视觉规范与令牌、主题工作室的设计约束与参考素材。 |
| `development/` | **本地开发环境说明**：开发调试所需的系统配置与操作步骤。 |

---

## 架构文档（`architecture/`）

| 文档 | 内容 |
|---|---|
| [overview.md](architecture/overview.md) | 程序集与进程拓扑、分层依赖图、跨进程数据流、配置与状态持久化位置 |
| [layering.md](architecture/layering.md) | 每一层的职责边界与「不应拥有的对象」、依赖方向约束、指标三拆模型、设置投影模型 |
| [process-isolation.md](architecture/process-isolation.md) | 硬件采集子进程的隔离目标、管道传输协议、身份约束、故障监督状态机、降级原则 |
| [hardware-services.md](architecture/hardware-services.md) | 硬件服务层的组件划分：采集主控、快照发布、传感器映射、设备身份、探测策略、网络与基础设施 |
| [rendering.md](architecture/rendering.md) | 三条渲染路线的判定顺序、GPU 渲染栈、材质参数体系、共享合同、两条背景捕获链的分工 |
| [rendering-transition.md](architecture/rendering-transition.md) | GPU 与 GDI+ 路线的切换合同、错误码表、句柄代次规则、失败分级与恢复预算 |
| [background-frame.md](architecture/background-frame.md) | 实时背景帧合同：帧源选择、代次、所有权与借用、取消与生命周期、测量口径 |
| [plugin-sandbox.md](architecture/plugin-sandbox.md) | 声明式插件的执行模型、沙箱配额、网络访问闸门、地址分类、失败回退 |
| [market.md](architecture/market.md) | 插件市场与主题市场的信任边界、索引校验、安装事务、路径安全、主题包规范 |
| [web-session.md](architecture/web-session.md) | Web 主会话与短期票据模型、凭据传递、来源检查、代理策略、失败语义 |
| [inflight-lease.md](architecture/inflight-lease.md) | 在途请求租约模型、请求身份、并发合并、超时与取消的关系、所有权 |
| [network-traffic.md](architecture/network-traffic.md) | ETW 网络流量采集的后端选型、计数事件集合口径、载荷布局、按地址排除 |
| [update-chain.md](architecture/update-chain.md) | 更新源、元数据与签名契约、事务替换与回滚、用户数据保护、展开安全 |
| [engineering-rules.md](architecture/engineering-rules.md) | 数据与错误语义不变量、异常模型、可空与警告门禁、异步命令语义、持久化规则、验证分层 |
| [known-gaps.md](architecture/known-gaps.md) | 已知缺口与限制（架构侧与产品侧） |

## 规格文档（`specs/`）

| 文档 | 内容 |
|---|---|
| [hardware-sampling.md](specs/hardware-sampling.md) | 采样节拍、缺失与零的分离、指标键规范、多显卡分组标题、显存择源、磁盘探测档位、设备选择 |
| [derived-metrics.md](specs/derived-metrics.md) | 派生指标定义、整机总功耗求和口径、已提交内存、CPU 电压、阈值与峰值记录规则 |
| [theme-system.md](specs/theme-system.md) | 主题文件 schema、颜色与布局规则、GPU 材质扩展段、加载顺序、导出契约、校验规则 |
| [trends-alerts.md](specs/trends-alerts.md) | 趋势的两层存储、快照消费、规则化报警模型、求值节拍、旧阈值迁移 |
| [scenario-profiles.md](specs/scenario-profiles.md) | 情景档案的字段白名单、预检、应用协调、运行时投影、版本化 |
| [diagnostics-center.md](specs/diagnostics-center.md) | 诊断事件模型、事件存储、诊断 Hub、诊断包收集、扩展提供程序、隐私边界 |
| [low-power-mode.md](specs/low-power-mode.md) | 低功耗三档对采集与显示的影响矩阵、Auto 触发条件、策略边界 |

## 设计与开发资源

| 文档 | 内容 |
|---|---|
| [design/gpu-themes/visual-spec.md](design/gpu-themes/visual-spec.md) | GPU 材质主题的视觉规范：材质语义、参数含义与取值范围、颜色与光照约定 |
| [design/gpu-themes/tokens.json](design/gpu-themes/tokens.json) | 视觉令牌的可机读定义（材质参数默认值、配色、圆角与模糊基准） |
| [design/theme-studio/architecture.md](design/theme-studio/architecture.md) | 主题工作室的组件划分、属性目录模型、预览机制、编辑态隔离、导出契约 |
| [design/theme-studio/TE00-capability-contract.md](design/theme-studio/TE00-capability-contract.md) | 主题工作室的能力边界契约 |
| [design/theme-studio/apple-windows-reference.png](design/theme-studio/apple-windows-reference.png) | 主题工作室的视觉参考素材 |
| [design/theme-studio/probe-screenshots/](design/theme-studio/probe-screenshots/) | 主题工作室在默认档与紧凑档下的布局参考图 |
| [development/windows-firewall.md](development/windows-firewall.md) | 本地开发调试所需的防火墙与权限配置 |
| [development/testing.md](development/testing.md) | 五档测试入口、环境能力、构建复用与 CI 门禁 |
| [development/measure-dpi.ps1](development/measure-dpi.ps1) | 实机 DPI 测量脚本 |
| [REBRAND.md](REBRAND.md) | 品牌与程序集命名约定、发布打包规则、版权合规指引 |

---

## 阅读路径建议

- **第一次接触这个项目** → [overview.md](architecture/overview.md) → [layering.md](architecture/layering.md) → [engineering-rules.md](architecture/engineering-rules.md)
- **要动渲染相关代码** → [rendering.md](architecture/rendering.md) → [rendering-transition.md](architecture/rendering-transition.md) → [background-frame.md](architecture/background-frame.md)
- **要动采集或指标** → [process-isolation.md](architecture/process-isolation.md) → [hardware-sampling.md](specs/hardware-sampling.md) → [derived-metrics.md](specs/derived-metrics.md)
- **要动插件或市场** → [plugin-sandbox.md](architecture/plugin-sandbox.md) → [market.md](architecture/market.md)
- **要动主题** → [theme-system.md](specs/theme-system.md) → [design/theme-studio/architecture.md](design/theme-studio/architecture.md)
- **要动更新或网络服务** → [update-chain.md](architecture/update-chain.md) → [web-session.md](architecture/web-session.md) → [network-traffic.md](architecture/network-traffic.md)
