# 五档测试

本地日常验证使用 `scripts/run-tests.ps1`，默认执行 Release 的前两档。需要 Windows、.NET 8 SDK 和 PowerShell 7。

| 档位 | 名称 | 范围 | 时机 |
|---|---|---|---|
| 1 | Smoke / 冒烟 | 指标、格式、主题解析、渲染路由、验签等代表性契约 | 小改动后 |
| 2 | Component / 快速组件 | 策略、解析、状态机、临时文件边界 | 本地默认 |
| 3 | Integration / 集成 | 回环 HTTP/WS、IPC、Host 子进程、事务及脚本门禁 | PR 双配置 |
| 4 | System / 系统实机 | WinForms/Win32、实际 GPU、真实采样及可选联网 E2E | 实机验证 |
| 5 | Stress / 压力 | 50 次开窗、1000 次编辑、200 次 GPU 切换、100 次 Host 启停及大包边界 | 夜间、手动、发布 |

`-Tier N` 累积运行 1..N；`-ExactTier` 仅运行 N。每个方法只有一个最终档位，参数化方法的所有行使用同一档位。三个测试程序集均纳入发现，包括独立 GPU 非性能程序集。

```powershell
# 默认：前两档，Release
pwsh -File scripts/run-tests.ps1

# 冒烟
pwsh -File scripts/run-tests.ps1 -Tier 1

# PR 级验证
pwsh -File scripts/run-tests.ps1 -Tier 3 -Configuration Debug

# 仅系统档，要求本机桌面、GPU、硬件覆盖
pwsh -File scripts/run-tests.ps1 -Tier 4 -ExactTier `
  -Capabilities Desktop,Gpu,Hardware -RequiredCapabilities Desktop,Gpu,Hardware

# 离线五档，包含全部低档
pwsh -File scripts/run-tests.ps1 -Tier 5 `
  -Capabilities Desktop,Gpu,Hardware -RequiredCapabilities Desktop,Gpu,Hardware
```

## 环境能力

档位和能力独立。提高档位不会自动启用外网或 GPU。

| 能力 | 含义 |
|---|---|
| `Desktop` | 实际 WinForms 窗口及 Win32 桌面接口 |
| `Gpu` | 实际图形设备与呈现；设置 `LITEMONITOR_GPU_TESTS=1` |
| `Hardware` | 真实 LHM 硬件采样 |
| `Network` | 外部市场联网 E2E；设置 `LITEMONITOR_E2E=1` |

`-Capabilities` 声明允许执行的环境用例；`-RequiredCapabilities` 还要求对应能力至少有一个用例入选并通过。必需能力未启用、没有入选验证、发现时仍被 Skip，均失败。实际设备不可用导致的失败不会自动降级。

未启用能力标为 `capability-not-enabled:...`；高档标为 `outside-tier`。两者都属于排除，不算通过或跳过。C03 子 testhost 单列 `harness-only`，由父压力测试运行并检查 sentinel、退出码和超时回收。

五档通过只代表本次所选集合通过，排除原因见 `catalog.json`。联网需显式增加 `Network`。脚本在发现前设置、结束后恢复 GPU/E2E、插件/主题 fixture 与同轮 Host 路径等环境。

三个程序集依次执行，内部沿用串行设置。不要在同一台机器并发运行另一套 LHM/资源压力测试。

## 当前实测

2026-11-02 本机单次 Release 验证，耗时包含 testhost 启停，不含构建与发现；这些数字不是硬预算。

| 范围 | 通过 | 测试命令耗时 |
|---|---:|---:|
| 1 | 93 | 约 3 秒 |
| 1..2（默认） | 2081 | 约 17 秒 |
| 1..3 | 2675 | 约 3 分 17 秒 |
| 仅 4，启用 Desktop/Gpu/Hardware | 370 | 约 30 秒 |
| 仅 5，启用 Desktop/Gpu | 9 | 约 3 分 17 秒 |

后三段并集为 3054 项离线正常测试，全部通过。另有 1 项外网 E2E 未启用、1 个 C03 子入口由父测试验证。Debug 前三档同样通过，约 3 分 20 秒。

## 构建复用

默认构建各工程的 `bin/test-tiers/`，与常规 `bin/Release` 分开。解决方案构建一次，测试全部使用 `--no-build`。

```powershell
# 只构建并记录身份
pwsh -File scripts/run-tests.ps1 -BuildOnly

# 复用同一配置/输出
pwsh -File scripts/run-tests.ps1 -Tier 3 -NoBuild

# 仅发现并列出选择/排除，不声称测试通过
pwsh -File scripts/run-tests.ps1 -Tier 5 -NoBuild -ListOnly
```

`-NoBuild` 检查源码输入摘要、配置、输出目录及关键产物摘要。缺失、陈旧或被替换的产物会拒绝执行；重新运行不带 `-NoBuild` 的命令即可。分类文件和基线在生成清单及结果复核时另行校验。

`-BaseOutputPath bin/<profile>/` 可指定其他相对输出，复用时参数必须一致。CI 使用 `bin/`，先 `-BuildOnly` 后 `-NoBuild`。Host 指向同轮输出，保留 Host/主输出/Contracts 摘要锚点。

## 结果与失败

默认结果在 `TestResults/test-tiers/<Configuration>-<UTC>-<id>/`。`-ResultsDirectory` 指定父目录，每次仍创建独立子目录。

| 文件 | 内容 |
|---|---|
| `catalog.json` | 完整发现身份、分类、选择/排除、输入与程序集摘要 |
| `*.runsettings` | 精确方法过滤器，包含参数化方法的全部行 |
| `results/<assembly>/results.trx` | 本次实际结果 |
| `summary.json` / `summary.md` | 发现、选择、排除、通过、跳过、失败，各档耗时、最慢 20 项 |
| `execution.json` | 测试阶段墙钟时间与失败程序集 |

实际结果必须逐条对应发现的 `(程序集, 类, 方法, 显示名)` 多重集。漏参数行、重复、额外行、零匹配、错误程序集、旧结果、所选用例 Skip/失败，均不通过。总数相同不能代替身份匹配。

各档秒数为 TRX 用例耗时之和；`execution.json` 是测试与结果核验的墙钟时间，不含构建与发现。失败报告保留错误，未执行项目不会算为通过。

## 增加或调整测试

`tests/test-tiers.json` 每类有显式默认档位，混合类按方法覆盖。方法级 `capabilities` 替换类级值；省略则继承，空数组移除继承能力。不设全仓兜底规则。

1. 新类加入对应分类；混合类按实际成本与环境添加方法覆盖。
2. 构建并发现。未知类、重复/失效规则、无效档位或能力都会失败。
3. 审阅测试资产变化后显式更新基线：`pwsh -File scripts/run-tests.ps1 -UpdateBaseline`。
4. 审阅 `tests/test-tier-baseline.json` 差异，再运行相关档位。基线按程序集/类/方法记录参数行数，新增、删除、改名和行数变化均需解释。

正常运行不自动重录基线。不要重录基线掩盖意外缺失。无法预枚举的动态 Theory 被拒绝，需要稳定的数据源。

## CI 与独立探针

- `ci.yml`：Debug/Release 的 1..3 档，保留真实主窗体启动检查、依赖声明和严格警告门禁。
- `extended-tests.yml`：定时与手动触发的**精确档**实机验证（self-hosted SystemValidation runner）。定时只跑 Tier 4；手动可在 Tier 4/5 之间选择。能力固定启用并必需 `Desktop,Gpu,Hardware`，手动勾选联网时追加 `Network`。
- `release.yml`：Debug 1..3 档；Release 精确 Tier 4 必需 `Desktop,Gpu,Hardware`，精确 Tier 5 必需 `Desktop,Gpu`（Tier 5 清单没有 Hardware 能力用例）。runner 不满足要求时失败；仍为人工触发、master 限制和草稿 Release。

`assert-test-tiers.ps1` 要求对应配置目录只有本轮一个清单，再核验产物身份及精确结果。原 `assert-test-counts.ps1` 与旧计数阈值已无 workflow 调用点，仅供手工调用旧全量入口时参考。

直接 `dotnet test` 沿用兼容行为，可能执行重型场景，也不会使用本入口的逐条对账。`scripts/run-probes.ps1`、外观探针和 WebProbe 是额外验证面，五档结果不代表它们已执行。
