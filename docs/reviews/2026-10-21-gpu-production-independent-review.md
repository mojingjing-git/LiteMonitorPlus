# GPU 双主题生产接线：独立进行中审查

日期：2026-10-21。状态：初审问题已交执行 Agent 修复，尚未复验收口。

## 审查边界与授权

- 执行模型继续为 DeepSeek V4.1 Flash，Agent `01a15ea3-3690-72f0-bb0c-e5cf5a6e1417`。
- 通过应用的执行聊天读取到真实用户消息：`玻璃 v16：通过 / 赛博 v3：通过 / WDA：接受`；不重复请求已取得的确认。
- 仓库 HEAD 仍为 `694a8c5`；生产改动在工作区。下表针对审阅时的代码，不把基线提交当作已包含新增实现。
- 执行 Agent 是实现与测试文件 owner；本审查只读代码、核对证据，在独立文档中记录，不并发修改生产文件或运行 GPU 压测。
- `R22` 的 8 秒进程采样不能证明 G4 的配对 CPU、呈现 p95、GPU 成本和资源压力已通过。

审阅期间的源文件快照和 SHA256 清单位于 `docs/reviews/evidence/gpu-themes/independent-review-20261021/initial-source/`。由于执行 Agent 正并行修复，具体位置以快照和函数名为准；这是审阅期间快照，不是新提交。

## 待复验问题

| ID | 优先级 | 位置与初审结论 | 必须提供的修复/验证 |
|---|---|---|---|
| I01 | P1 | `RenderingRoutePolicy.Decide` 只有 `parameters.liveBackground=true` 才允许实时路由；内置玻璃该值为 false，设置页仅改 `GpuLiveBackgroundEnabled` | 用内置主题及真实设置入口证明开/关确实创建/停止抓屏；不能只由 probe 改主题绕过 |
| I02 | P2 | `SceneMaterialLayoutBuilder.Build` 的 Theme0–3 是硬编码，Host/Renderer 未消费主题材质参数及 motion 开关/FPS | 参数从主题到快照、shader、调度、预览贯通；默认映射维持已确认画面；无效/未知参数行为明确 |
| I03 | P1 | `Program` 仅有手动 `--safe`；未找到异常 GPU 会话标记和自动安全启动保护 | 原生崩溃后下次启动不会反复自动进 GPU；按安装身份隔离，状态不可写时安全回退，旧路径不依赖 GPU 初始化 |
| I04 | P1 | `GpuSceneRenderer` 构造阶段 reserve/创建资源后读 shader，异常路径只释放 device service；捕获源构造失败也可能遗留 output/duplication 等引用 | 分阶段初始化失败注入，资源账本与原生引用正确释放；缺 shader 后仍能旧模式启动 |
| I05 | P2 | `PresentationSession` 不读原 affinity，停止写 None；更换 HWND 不归还旧窗口状态，恢复捕获失败时状态处理不完整 | 保存/恢复本模块修改的真实原状态，固定模式不覆盖外部状态；恢复失败、重建、退出均验证 |
| I06 | P2 | 捕获时间戳来自 `CopyResource` 后的本地时钟，`SupportsTimestamp=false`、AdapterLuid 为 0 | 真实源帧时间与适配器信息；端到端背景年龄和接收后年龄分开，无法测量时不得标 G4 通过 |
| I07 | P1 | `GpuSceneRenderer.Resize` 在 `ResizeBuffers` 前未释放由 backbuffer 创建的 `_renderTargetView` | 实际 HWND 连续尺寸变化成功，不仅状态机通过；释放应用与 context 持有的所有 backbuffer 引用 |
| I08 | P1 | Resize 增加 renderer Size 代次，但现有 capture source 保留旧代次，之后 `Acquire` 拒绝新请求 | 实时玻璃尺寸/DPI 改变后持续取得有效真帧，无永久 StaleGeneration 或静态回退 |
| I09 | P2 | 前景字体缩放传入 `height/800`，字号随整窗高度变化，而不是以 DPI/用户缩放为依据 | 同 DPI 下增删分组不改变既有文字字号；横竖与 DPI/用户缩放一致；字体来源/替代规则完整 |
| I10 | P1 | 竖排前景按 `floor(remainingHeight / 34)` 截断指标；DPI96、高96、两个普通指标可只生成一个 | 每个已选源指标必须有对应前景输出；按真实行型处理，不按组名猜双列，不无提示丢数据 |

I10 的静态例子：普通组高 96，标题/分隔线后可用高约 58，行容量计算为 1，两个普通指标只遍历第一个。这与计划的指标完整性要求不符；需用现有布局构造的真实场景补回归。

以上为源码路径和已有证据的审查结论，尚未由本审查者独立运行原生复现。执行 Agent 的新测试必须对应每一条；复验后逐项更新状态，不用新增测试总数代替关闭问题。

## API 依据

- [Microsoft：ResizeBuffers](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-resizebuffers)：调用前必须释放 backbuffer 的直接和间接引用，包括仍持有的 view；解除 context 绑定不等于释放应用自己的 view。
- [Microsoft：DXGI_OUTDUPL_FRAME_INFO](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_frame_info)：`LastPresentTime` 是桌面图像更新时间的 QPC 值；仅鼠标更新可能为零，需与接收时刻区分。

## 后续工作

1. 执行 Agent 先修 I01–I10 并提交对应证据，避免对已知错误版本执行长时测量。
2. 对实际尺寸/句柄变化、失败初始化、后台恢复做真实窗口验证。
3. 串行完成正式性能与资源实验、最终 ZIP 验证和 R24 文档。
4. 独立复核修复与证据，再判断 R25；不继承本轮前的 targeted-pass 作为全量验收通过。
