# GPU 渲染核心技术合同 v1（R05）

> 状态：**核心与捕获子合同已冻结；WDA 产品策略已由用户接受**  
> 日期：2026-10-21  
> 执行模型：DeepSeek V4.1 Flash  
> Schema：`litemonitor.gpu-render/1`  
> 代码：`src/Rendering/Contracts/RenderContracts.cs`

## 1. 冻结原则

1. 合同只引用不可变场景、数值和描述对象；不得携带 `Control`、可变 `MetricItem`、GDI 画刷、窗口对象或捕获资源。
2. 布局以 96 DPI 逻辑单位描述；提交到物理像素时全链路只做一次缩放。
3. UI 不得等待捕获或 GPU fence；后台不得同步 Invoke UI。
4. 每类代次只增不减；主题、窗口、尺寸、拓扑和设备任一不匹配时，帧不得发布。
5. 新后端关闭时不得创建 GPU 设备、捕获会话、纹理账本或专用动画循环。
6. 旧 GDI+ 路径和旧 M2 合同独立保留；本合同不改变 `Form.Opacity`、`LiveGlassEnabled`、旧 FPS 限制或旧主题文件语义。

## 2. 核心对象

| 对象 | 冻结语义 |
|---|---|
| `RenderGenerations` | 主题、窗口、尺寸、显示拓扑、设备五个代次；必须全部相等 |
| `SceneSnapshot` | 只读布局与指标快照；包含正常、警告、危险、缺失状态和通知 |
| `SceneMetric` | 稳定 ID、显示值/单位、可选归一化值、状态、缺失标记和趋势副本 |
| `GpuOutputDescriptor` | 适配器 LUID、宽高、像素格式、颜色空间、单调时间戳和内容矩形 |
| `IBackgroundFrameLease` | 单次所有权租约；`Dispose` 只能释放一次；预览必须明确 `IsPreview=true` |
| `IBackgroundFrameSource` | 按请求代次获取最新租约；不得在 UI Paint 中启动后台任务 |
| `RenderingConfig` | `version + backend + material + parameters + motion + fallback`；未知嵌套字段由上层持久化合同保留 |
| `RenderDiagnostics` | 区分请求/实际后端、请求/实际材质、捕获状态、适配器、延迟和故障原因 |

## 3. 状态与错误

渲染状态：

```text
Disabled -> Probing -> Initializing -> Running
                         ^              |
                         |              v
                      Recovering <- Suspended
                         |
                         v
                      Fallback
任何活动状态 -> Stopping -> Disabled
```

捕获状态：

```text
Disabled -> Initializing -> Running
                              |
                              v
                         AccessLost
                              |
                              v
                    Initializing / Failed
任何活动状态 -> Stopping -> Disabled
```

错误码必须以稳定字符串返回；不得把渐变回退描述为实时材质。设备恢复最多一次自动尝试，超时与取消能力在 R11/R19 接线前由 owner 细化为具体等待点。

## 4. 捕获子合同

功能冻结值：

- API：DXGI Desktop Duplication。
- 设备与输出绑定：使用 `IDXGISwapChain.GetContainingOutput()` 返回的同一适配器输出。
- 纹理：目标显示器全尺寸 BGRA8 UNorm；应用只采样窗口所需区域。
- 自身排除：`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`。
- 无新帧：保留最后有效帧并记录年龄；不得把等待超时记为故障。
- Access Lost：停止发布旧帧，进入可解释恢复/回退。
- 释放：先停止消费，等待在途提交完成，再归还租约、释放 duplication 和纹理。

产品策略（已接受）：

- WDA 会让本程序针对受支持的截图/录屏整体不可见。
- R04 已证明：排除模式无自反馈；无排除模式产生自身嵌套。
- 用户已于 2026-10-21 明确接受该行为，实时玻璃可以进入生产接线。
- 发布说明、故障状态和兼容列表必须准确披露该行为，不得把它描述为普通界面。

## 5. 线程与所有权

- 一个设备只有一个明确渲染 owner。
- 场景和背景各保留一个最新待处理项；队列有界，旧项丢弃。
- 帧租约从 source 转移到 renderer；source 在转移后不再释放该帧。
- 捕获帧与预览帧不得混用所有权；预览路径不得创建桌面捕获。
- GPU 热路径不得同步回读 Bitmap；证据截图是显式冷路径。

## 6. 验证承接

- `tests/LiteMonitor.Tests/RenderingContractTests.cs` 验证版本、代次和显式捕获策略。
- R06/R07 实际运行样板消费同一视觉 token 和固定输入。
- R08–R18 只能按本版本实现；跨版本修改必须提交 ADR 和迁移说明。
- R12/玻璃生产接线引用用户 2026-10-21 的 WDA 决策；不得擅自扩大排除窗口范围。
