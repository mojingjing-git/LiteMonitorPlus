# 非性能实现集成复核（进行中）

计划已由两位独立 reviewer 通过。本记录追踪执行中的源码问题，不代表 N13 终审通过。

## 本轮源码复核发现

| ID | 位置与路径 | 用户影响 | 处置与关闭条件 |
| --- | --- | --- | --- |
| NPF01 | `GpuPresentationHostController.Start` 的 live 捕获失败会恢复 affinity=None；`Resume` 的成功重试路径没有先重新应用 WDA | 首次捕获失败后隐藏/恢复，若后续捕获成功，可能未排除自身窗口而出现反馈 | 已交宿主 owner 排入 N05。需要“首次失败 → 隐藏 → 恢复成功”的顺序验证，明确 WDA 应用先于捕获；失败时正确恢复原 affinity |
| NPF02 | `GpuPresentationHostController.StopCore` 忽略 `PresentationSession.CompleteStop` 的恢复失败，并丢弃 presentation 引用 | 恢复失败状态和原因可能丢失，无法可靠表明截图排除已撤销 | 已交 N05。验证恢复失败诊断、可重试/清理路径，不能在失败时报告恢复成功 |
| NPF03 | 上一轮 `stress-30m-v8/window-policy-audit-start.json` 记录 targetOwnsForeground=true，而 topMostWindowCount=0 | 测试窗口首次启动仍可能短暂抢焦点，单独不置顶不等于不激活 | QA owner 处理探针专用窗口行为，产品正常交互不改变；以启动和周期审计验证，不再跑性能长测 |

## 基线事实修订

N00 初稿误将 v4 thickness 记为 0.25。实际独立样板常量为 `Theme1=(30,8,8,0.16)`，其中 `theme1.y` 是边缘厚度 **8px**。已通知 owner 在 N01 前更正，避免将旧材质同名参数混用。

## 既有几何路径事实

`UiSceneSnapshotAdapter` 接收已布局的物理 Bounds；`SceneSnapshotBuilder` 除以 DPI 存为逻辑 Bounds；正式 material/foreground 再乘 DPI 恢复。用户缩放已包含在原布局，不能再次乘到组矩形。新材质参数的 DIP 转换应与此分开。已将该事实交给合同 owner。

## 尚待复核

- N01 新参数、输入模式、前景、ABI 及资源所有权。
- 新样式实际图像、固定/实时采样、真实前景与编辑器。
- QA manifest 的性能排除、必需功能与失败阻断。
- 最终快照、真实成品运行和独立终审。

本轮未运行性能测试。发现项需要对应源改动与定向有效证据后才能关闭。
