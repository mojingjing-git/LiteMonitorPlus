# 独立复核用例归档

本目录保存 **独立复核（verify）批次产出的测试代码**。这些代码在复核分支上完成过编写，
但**从未整合进主线**（原提交信息即为「chore(archive): 保全独立复核用例与证据文件（未整合进主线）」），
因此它们不在 `master` 的 `tests/` 目录里，也**不参与编译与测试执行**。

## 为什么放在 `docs/` 而不是 `tests/`

主工程 `LiteMonitor.csproj` 明确排除了 `docs\**`：

```xml
<Compile Remove="docs\**" />
```

所以本目录下的 `.cs` 文件**不会被任何工程编译**，也**不会改变测试发现总数**
（CI 门禁 `MIN_TEST_TOTAL: 1862` 不受影响）。

这样做的理由是：这批用例**从未与当前主线对齐**。它们引用的类型、字段与内部接缝
在后续主线演进中可能已经改名或删除，直接放回 `tests/` 极可能编译失败并拖垮 CI。
把它们放在归档区，既保住证据，又不引入回归。

## 文件清单与来源

| 文件 | 来源分支 | 来源提交 |
| --- | --- | --- |
| `LiteMonitor.Tests/IndependentBatchAAppearanceVerificationTests.cs` | `verify/batch-a` | `3000ad1` |
| `LiteMonitor.Tests/IndependentBatchACapturePolicyVerificationTests.cs` | `verify/batch-a` | `3000ad1` |
| `LiteMonitor.Tests/VerifierGpuClosureTests.cs` | `verify/gpu-t03-t05` | `b559a9c` |
| `LiteMonitor.Tests/VerifyLm045IndependentRenderingDump.cs` | `verify/lm045-ownerA` | `0843d06` |
| `LiteMonitor.Tests/VerifyLm045IndependentRenderingTests.cs` | `verify/lm045-ownerA` | `0843d06` |
| `LiteMonitor.Tests/VerifyLm045IndependentTests.cs` | `verify/lm045-ownerA` | `0843d06` |
| `LiteMonitor.Tests/VerifierIl.cs` | `verify/p1-batch-audit` | `ba5267a` |
| `LiteMonitor.Tests/VerifierLm065Tests.cs` | `verify/p1-batch-audit` | `ba5267a` |
| `LiteMonitor.Tests/VerifierLm066Tests.cs` | `verify/p1-batch-audit` | `ba5267a` |
| `LiteMonitor.Tests/VerifierLm067Tests.cs` | `verify/p1-batch-audit` | `ba5267a` |
| `LiteMonitor.Tests/VerifierLm068ParityTests.cs` | `verify/p1-batch-audit` | `ba5267a` |
| `LiteMonitor.Tests/VerifierLm068Tests.cs` | `verify/p1-batch-audit` | `ba5267a` |
| `LiteMonitor.Tests/VerifierLm069Tests.cs` | `verify/p1-batch-audit` | `ba5267a` |
| `LiteMonitor.Updater.Tests/VerifierP1UpdaterAuditTests.cs` | `verify/p1-batch-audit` | `ba5267a` |

子目录名沿用原测试工程名（`LiteMonitor.Tests` / `LiteMonitor.Updater.Tests`），
便于对照回填。

## 如何回填到 `tests/`

把文件复制回对应测试工程目录即可，例如：

```powershell
Copy-Item `
  'docs\reviews\archive\independent-verification\LiteMonitor.Tests\VerifierLm065Tests.cs' `
  'tests\LiteMonitor.Tests\VerifierLm065Tests.cs'
```

回填后必须跑一次完整构建与测试，确认这批用例与当前主线对齐；
若出现编译错误，说明它依赖的内部接缝已经演进，按需改写或继续留在归档区。

## 原始来源仍可从归档包恢复

上述 4 条复核分支已按"归档后删除"的处置方式清理。本地全量归档包
`G:\TOOL\LiteMonitor-archive-20261024.bundle`（含 104 个 ref 的完整历史）仍然保留，
需要原始 blob 时可直接取出：

```powershell
git -C G:\TOOL\LiteMonitor-fork cat-file blob "3000ad1:tests/LiteMonitor.Tests/IndependentBatchAAppearanceVerificationTests.cs"
```

> 同一批归档提交里还有约 85 个 `.artifacts-*` 变异测试临时产物
> （备份 `.bak`、构建日志、变异规格等）。那些是**过程垃圾**，
> 已被有意丢弃，仅保留在归档包中，不在此目录落地。
