# GPU 非性能收口计划独立架构复审

结论：**PASS**。

审查日期：2026-10-22。审查对象：`docs/superpowers/plans/2026-10-22-gpu-nonperformance-completion-plan.md`。本次通过所绑定的计划 SHA256：`96360DAC3B1F5EDB54B0C007C081EDC90656F85727C25053778F742EF3812240`。

本次仅定向复核上一版报告 R1–R4 与新增条款的一致性，沿用初审对 MaterialRuntimeSettings、ThemeRenderingConfig、MaterialFrameConstants、SceneMaterial.hlsl、GpuSceneRenderer、Direct2DForegroundPainter、GpuThemePreviewControl 及必要上下游的只读结论，未重复完整源码审查。

| 原阻断 | 最新计划对应条款 | 定向结论 |
| --- | --- | --- |
| R1 固定背景输入及依赖 | §4.5，第78–85行；§6，第136–141行 | 已明确实际固定纹理入口、模式、尺寸/原点/覆盖矩形、资源所有权与释放，保留旧程序背景缺省行为；生产与预览共用入口，N03/N04集成依赖N02接线，固定输入不创建捕获或改变affinity。闭环。 |
| R2 几何、阴影与旧缩放兼容 | §4.6，第87–93行 | 新旧几何分支分离，新样式单次缩放且已物理化bounds不再缩放，Region与shader同源；生产单窗系统外影与固定回放参考阴影明确区分，允许有记录的无外影降级，不依靠AlphaMode.Ignore伪透明padding。验收包含DPI、UIScale、窄尺寸及往返。闭环。 |
| R3 颜色与透明度 | §4.7，第95–100行 | 已冻结SDR编码空间、BGRA8、前景预乘与合成公式、输出alpha=1及Region裁切；fog/tint与Form.Opacity分离，整体透明度只应用一次；有已知颜色合成对照及不同窗口透明度实测，预览宿主差异明示。闭环。 |
| R4 文件所有权与交接 | §6，第132–145行 | 具体文件表优先于目录概括；renderer/painter及前期csproj/shader构建归渲染owner，设置入口归编辑器owner，宿主在N03交回renderer后才修改；交接记录合同版本、调用点、哈希和下一owner，N02交付匹配ABI的CSO。闭环。 |

§4.1继续以独立新主题和显式glassStyle选择v4，旧主题ID、用户选择和缺省路径保留；§4.3的主题font/color真实映射与§4.4的CPU/HLSL ABI验证要求继续有效。上述补充与R1–R4修订可共同实施，并有可检查的任务出口。未发现本次定向范围内仍需阻断计划通过的事项。

本PASS仅代表该版本计划的架构复审通过；实现、实窗、功能和成品是否通过，仍由N00–N14的执行证据决定。

本次只写入本报告，未修改计划或实现，未运行测试、构建或启动GUI；保持独立，不参与实现。
