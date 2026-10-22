# GPU 玻璃跨屏与横竖切换修复记录

日期：2026-10-22。状态：源码修复及编译完成，实机验收待完成。

## 执行与范围

用户要求尝试 DeepSeek V4.1 Flash，失败时由主 agent 接手。已实际创建指定模型的子 agent，但启动重试耗尽（外层 429，代理最终记录 model_unavailable / per-model cooldown），未产出修改；由主 agent 完成本轮实现。另由独立 reviewer 只读复核。

本轮处理跨屏抓错背景、横竖切换尺寸失配、显示变化拉回旧位置、竖屏旋转及玻璃窗口轮廓。先前质量审查中的前景缓存签名和验收 runner 等其他问题不包含在本轮收口结论内。性能项继续延期。

## 修改

| 文件 | 本轮处理 |
| --- | --- |
| `src/Rendering/Presentation/GpuSceneRenderer.cs` | 根据当前 HWND 对应的 HMONITOR 选择渲染适配器上的 DXGI 输出；换屏释放旧捕获；不复用旧屏幕帧。记录最近尝试的屏幕，失败后不逐帧循环重试；换屏或显式显示变化可以重新尝试。通过常量 State.W 传递旋转，不改变 384-byte ABI。 |
| `src/Rendering/Presentation/GpuPresentationHostController.cs` | 出帧前检查捕获屏幕；显示变化可强制重绑。先设置 WDA 再启动新捕获；失败撤回排除并保留固定材质。 |
| `src/Rendering/Capture/DesktopDuplicationFrameSource.cs` | 按实际获得的纹理尺寸分配资源，不再假设桌面矩形宽高等于原始纹理宽高；尺寸变化安全替换资源，重建 duplication 后更新输出矩形和旋转。 |
| `resources/shaders/SceneMaterial.hlsl`、`ToolbarGlass.hlsli` | 共用显示空间到 DXGI 原始纹理的旋转采样，支持 90/180/270 度；固定输入和未旋转输入保持原坐标路径。已重新编译 VS/PS `.cso`。 |
| `src/UI/UIController.cs` | 布局内部修改 ClientSize 后，在最终提交场景前同步 GPU 尺寸并更新场景代次；隐藏恢复补尺寸同步；显示变化接线；隐藏时新启用 GPU 会暂停；启动失败保留尚未恢复的 WDA 所有权。 |
| `src/UI/HorizontalLayout.cs` | GPU 调用显式使用目标窗体的 Graphics、DeviceDpi 和字体高度进行测量，避免混用桌面 DC；默认 GDI/任务栏调用沿用原测量入口。 |
| `src/UI/Helpers/MainFormBizHelper.cs` | 显示变化时保留当前仍可见的位置，不打断拖动；只在不可见时恢复位置；保存屏幕失配时不再把合法负 X 当作无位置。 |
| `src/UI/MainForm_Transparent.cs`、`Helpers/WindowAppearanceController.cs` | GPU 玻璃显式采用材质对应圆角 Region，避免叠加独立系统圆角。显示变化接线，并在恢复显示后重新应用窗口轮廓。旧 GDI 路径保持默认策略。 |

独立复核发现“隐藏时应用设置后，恢复显示未重新应用材质 Region”，已补 `VisibleChanged` 恢复接线；另发现 GDI 同步绘制生成的横向布局可能被 GPU 复用，已在 GPU 路由应用/退出时清掉该实例，确保重新选择正确的测量来源。

旋转映射依据微软 Desktop Duplication 文档与样例：

- <https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api#rotating-the-desktop-image>
- <https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/DXGIDesktopDuplication/cpp/DisplayManager.cpp>

## 已执行检查

- `pwsh -NoProfile -File scripts/build-gpu-shaders.ps1`：两个着色器编译成功。
- `dotnet build LiteMonitor.csproj -c Release --nologo -v:q`：0 错误 / 234 警告；未发现新增警告数。
- 编译日志：`.review-scratch/display-fix-build.log`。
- 修改前副本：`.review-scratch/display-fix-before/`，完整文件身份：`.review-scratch/display-fix-files.json`。
- 主程序集 SHA256：`B9287B1A12A40028549F9015791A464F5D63B8C020216C8494EAC815C4356FAC`。
- `SceneMaterialPS.cso` 源资源与主工程输出目录哈希一致：`34AA0AA0B02FE55D7C0BE7B89DD3C30A0D4B656CA27189967969A4393C48972C`。

未新增或运行测试，未启动 GUI、抓屏或长测。编译成功不代表实机问题已经消失；现有旧候选的 PASS 不能自动沿用。

## 待实机确认及限制

1. 主屏 → 左侧负 X 副屏 → 主屏：背景取自窗口当前屏幕，不残留上一屏颜色。
2. 上方负 Y 副屏同样往返，桌面细节与窗口下方内容对应；灰雾需在采样正确后再判断，HDR 色彩管理尚未验证。
3. 在两屏分别执行横 → 竖 → 横；字体、画面尺寸、裁切和圆角同步，不依赖再次跨屏才能恢复。
4. 在现有混合 DPI 显示器间往返，验证真实竖屏的旋转方向和图像位置。
5. 隐藏窗口 → 修改设置 → 显示，确认尺寸和轮廓正确。
6. 调整显示拓扑后拖动窗口，确认延迟通知不拉回旧保存位置。
7. 关闭实时背景或捕获失败后，确认系统截图恢复显示面板。

当前仍是单适配器捕获：目标屏幕若不属于渲染适配器，明确回退固定材质，不声称实现跨适配器纹理共享。窗口横跨两个显示器时选择所属输出，不提供两个输出的拼接捕获。未更新发布 ZIP、未部署到日常运行实例、未提交或推送。
