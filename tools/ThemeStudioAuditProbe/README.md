# ThemeStudioAuditProbe

Theme Studio 的可重复人工探针。探针写入隔离的
`observations.json`，覆盖目标切换、属性聚焦、预览点击、GPU 背景选择、
暗色模式、阻塞保存、fallback/GPU 导出、隐藏停帧、FPS 更新和父子控件状态等观察点。

## 用法

```powershell
# 参数是父目录。探针会新建唯一的 run-<UTC>-<GUID> 子目录，
# 并在其中创建 settings/resources/drafts 等隔离目录。
dotnet run --project tools/ThemeStudioAuditProbe -- .tmp\theme-studio-audit
```

探针不清理、覆盖或删除父目录，也不把历史运行目录当作金标准。当前历史缺陷
证据位于 `.evidence/theme-studio/`，只能用于对比和回归，不能直接改成
`expected-observations.json`。

每次运行至少产出：

- `observations.json`：观察值、分类汇总、安全门和能力门；
- `run-metadata.json`：HEAD、候选 index tree、真实 index 前后 SHA、状态摘要哈希、DPI 和程序集 SHA-256；
- `fallback-export.png`、`gpu-horizontal-export.png`：实际导出的 PNG 和 SHA-256。

## Git index 隔离

探针先解析并哈希真实 Git index，再复制到自己的临时文件。`status` 和
`write-tree` 都只使用该副本，并设置 `GIT_OPTIONAL_LOCKS=0`。运行前后真实
index SHA 必须一致；无法解析、复制、匹配或复核时，探针以非零状态失败，
不会回退到真实 index。

父目录只接收探针新建的 `run-*` 结果。探针只删除自己的临时 index，
不递归清理用户目录。

## 安全门与能力门

安全门检查所有返回成功的导出都对应存在、非空、可读取且尺寸正确的 PNG。
能力门只在具备 `Desktop,Gpu` 环境时要求 `Route=Gpu` 且横排文件为
400x120。

横排 GPU 导出的分类为：

- `expected`：成功、GPU 路线且尺寸正确；
- `defect`：成功但文件缺失、损坏、尺寸错误或路线冒充 GPU；
- `not-proven`：严格 GPU 导出安全失败，例如无 GPU、启动失败或能力缺失。

Auto fallback 的安全成功不能替代 GPU 能力证明。`gpu_probe_exception` 表示
GPU 观察没有执行，不等于通过。`probe_exit_code=0` 表示探针流程正常结束；
是否验收仍需结合 `classification_summary`、安全门和能力门判断。

## TRX 汇总

`summarize.py` 接受格式化输入，默认只读取候选 TRX，并可写入来源清单摘要。

```powershell
python tools/ThemeStudioAuditProbe/summarize.py `
  --trx-dir .tmp/theme-studio-audit/results `
  --manifest .tmp/source-manifest.json `
  --artifact .tmp/theme-studio-audit/LiteMonitor++.dll `
  --output .tmp/theme-studio-audit/summary.json
```

`--manifest` 和 `--artifact` 可以省略；不指定 `--output` 时 JSON 写到 stdout。

## 构建约束

- 程序集名称为 `ThemeStudioAuditProbe`，不能并入 `LiteMonitor.Tests` 测试程序集。
- 它依赖 `LiteMonitor` 的 internal 测试接缝
  （`SettingsHelper.PathOverride`、`ThemeDocumentStore.SetThemeDirForTests` /
  `SetRecoveryDirForTests`、`SettingsThemeManager.Initialize` 等）。
- 它不在 `LiteMonitor.sln` 内，避免污染日常 CI 的测试计数。
- GPU 观察需要真实 GPU 和交互式桌面会话；GPU 不可用时必须记录
  `gpu_probe_exception`，不能把其余观察视为 GPU 能力已通过。

## DPI 观察

`docs/development/measure-dpi.ps1` 会打印显示器有效 DPI 与窗口 DPI，
供人工核对缩放场景。
