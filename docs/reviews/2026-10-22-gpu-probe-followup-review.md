# GPU 探针与截图排除状态：续审

日期：2026-10-22。基线 HEAD 为 `694a8c5`，审查对象是未提交工作区，不代表发布产物通过。

## 本轮发现与处置

| 项目 | 原问题 | 当前状态 |
| --- | --- | --- |
| 长测窗口 | GpuUiProbe 将主窗体和整屏模拟背景置顶，并在显隐循环中 Activate | 已改默认不置顶、不激活，长测拒绝强制置顶/前台参数；已读取实际窗口审计 |
| 测试退出码 | FinalizeStress 对取消、失败、关闭也设置 captured=true，最后返回 0 | 源码已改独立 stressExitCode：完成 0、取消 2、中止 3、失败 1 |
| 中途证据 | 每分钟数据仅留在内存，退出前异常会丢失 | 源码已加原子写入 checkpoint 和 progress；新长测已有 checkpoint-0001 |
| WDA 所有权 | 同会话重复设置 Exclude 会将 changedBySession 清为 false，停止时无法恢复原 affinity | 已删除错误赋值，并新增重复设置恢复及原先已排除的两条定向用例；本审查未亲自执行测试 |
| 另一性能入口 | ProductionRenderProbe 同样硬编码置顶与整屏背景 | 已交 owner 同步修正，尚不据此声称验证完成 |

## 已核对的实际证据

- `evidence/gpu-themes/694a8c5/R22/runner-cancel-v2/watchdog.result.json`：status=cancelled，探针与 runner 退出码均为 2，未强杀；对应诊断记录取消且窗口策略不置顶、不主动激活。
- `evidence/gpu-themes/694a8c5/R22/stress-30m-v2/watchdog.json`：正式新压测已启动，目标 1800 秒，外部时限宽限 120 秒、退出宽限 45 秒，配置取消信号文件。
- 同目录 `window-policy-audit.json`：probeOwnsForeground=false、topMostWindowCount=0，主窗口及背景均非置顶。
- 同目录 `stress.progress.json`：首次 checkpoint 的实际经过时间为 60106ms，已完成 10 次换肤及显隐；此为中途记录，不能代替完整压力测试结论。

旧 `stress-30m-v1` 已标明因干扰用户中止，不算通过。新压力测试仍在执行，G4/G5 未关闭。被遮挡的测量不得作为可见窗口性能通过依据。

新轻雾样板 v4 仍在 `tools/GlassToolbarPreview` 独立目录，尚未替换生产材质。本轮未修改样板视觉，未新增或运行独立测试，仅读取源代码和 owner 生成的运行证据。
