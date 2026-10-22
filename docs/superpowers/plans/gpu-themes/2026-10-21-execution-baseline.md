# GPU 材质主题执行基线（G0）

> 任务：R00  
> 执行模型：DeepSeek V4.1 Flash  
> 基线时间：2026-10-21T21:27:44+08:00  
> 工作区：`G:/TOOL/LiteMonitor-fork`  
> 上游计划：`docs/superpowers/plans/2026-10-21-gpu-material-themes-plan.md`

## 1. 代码与工作区

| 项目 | 锁定值 |
|---|---|
| 分支 | `master` |
| HEAD | `694a8c57d27b628e0edba803e5f24b3ca53dbc5f` |
| 上游 | `master...origin/master`，无 ahead/behind 标记 |
| 已跟踪差异 | 开始时为空 |
| 计划 SHA-256 | `4C7CEE7FB259CEB6605D7C66FA63B20CBC4AC4065AF79414912DD5A3ADACF8E5` |
| M2 合同 SHA-256 | `9B58934D0448A8B244CAB22C4FAAE31E864465809C5C0B0EA3020580E6826CC1` |

`docs/`、`tests/`、`scripts/`、根目录 `start.cmd` 被当前 `.gitignore` 忽略。它们作为本轮本地材料使用，不修改忽略规则，不强制批量加入 Git。本轮不另建 clone 或 worktree；如果后续建立隔离副本，必须显式复制计划、M2/A15/A16/A17 合同、相关测试/脚本和固定输入，并记录源/目标哈希。

## 2. 用户约束

1. 保留现有采样、旧主题、GDI+ 渲染路径和 M2 合同。
2. 不覆盖 `resources/themes/CyberPunk_Mech.json` 与 `resources/themes/LiquidGlass.json`，新主题使用新 ID。
3. 不执行关机、重启、外部消息、发布或推送。
4. 桌面捕获实验只使用专用演示背景；默认不保存真实桌面画面。
5. G1 呈现和捕获分别取证，随后做同一宿主、同一场景的联合验证。
6. G2 必须由用户分别确认玻璃和赛博画面；等待或超时不等于通过。

## 3. 参考机器

| 项目 | 实测值 |
|---|---|
| OS | Windows 11 Pro，10.0.26300，x64 |
| 主板/机型 | MSI MS-7C35 |
| CPU | AMD Ryzen 7 5800X，8 核 16 线程，3.8 GHz 标称 |
| 内存 | 68,637,044,736 bytes |
| 主显示适配器 | NVIDIA GeForce RTX 3080 Ti，驱动 `32.0.16.1088`，3840x2160 @ 159 Hz |
| 第二适配器 | NVIDIA GeForce RTX 3080，驱动 `32.0.16.1088` |
| 虚拟显示 | Meta Virtual Monitor、GameViewer、MuMu、ToDesk 虚拟适配器均存在 |
| .NET SDK | 8.0.426 |
| .NET Desktop Runtime | 8.0.31、8.0.32 |
| Windows SDK 着色器工具 | `10.0.22621.0/x64/fxc.exe` |

虚拟适配器导致适配器枚举不能按名称或枚举顺序选择。原型必须按承载目标窗口的 DXGI Output 选择同一适配器，并把跨适配器列为不支持或未验证。

## 4. 原功能基线

本轮开始时的 Release 主工程构建命令：

```powershell
dotnet build LiteMonitor.csproj -c Release --nologo
```

结果为成功：0 errors，233 warnings。警告为当前代码基线已有问题，本轮不以全量清理掩盖功能证据。

旧主题原始 SHA-256 已记录在 `docs/reviews/evidence/gpu-themes/694a8c5/R00/baseline/theme-hashes.sha256`。其中：

| 主题 | SHA-256 |
|---|---|
| `CyberPunk_Mech.json` | `F537514F2C75CFA34E5762AC4FA3B3BBEA4FB9F5395AAF4D5C6D59A07BB975BE` |
| `LiquidGlass.json` | `4C96861D96A93AEE1FA984E985E228BFA84DCD8923B1F53E8322F1C5EDB0B10F` |

原功能矩阵沿用以下已冻结/已验收材料，执行时需重新核对当前代码，不把历史通过数当作本轮结果：

- M2 捕获生命周期、帧所有权、代次、取消和测量口径：`docs/superpowers/plans/2026-10-20-appearance-m2-contract.md`
- A14 旧主题像素和 CPU 口径：`docs/superpowers/plans/2026-10-20-appearance-a14-acceptance.md`
- A15/A16 捕获与接线：`docs/superpowers/plans/2026-10-20-appearance-a15-acceptance.md`、`2026-10-20-appearance-a16-acceptance.md`
- A17 资源压力：`docs/superpowers/plans/2026-10-21-appearance-a17-release-pressure.md`
- 分叉修复：`docs/superpowers/plans/2026-10-21-full-fork-remediation.md`

## 5. 原型编译隔离

主项目默认递归收集 `.cs`。R00 已增加以下隔离：

```xml
<Compile Remove="tools\**" />
<EmbeddedResource Remove="tools\**" />
<None Remove="tools\**" />
```

原型位于独立 `tools/AppearanceMaterialLab/AppearanceMaterialLab.csproj`，不得由主工程编译或把入口带进生产可执行文件。最终发布打包仍需 R23 owner 明确声明所需源、shader、预编译产物和依赖。

## 6. 性能协议

草案门槛保持计划中的值，待 R03/R04/G1 有实测证据后冻结；本基线不提前放宽：

- 普通档新增进程 CPU 增量不超过 40 ms CPU/秒；大档不超过 80 ms CPU/秒。
- GPU 材质加前景 p95：普通档不超过 4 ms/帧，大档不超过 8 ms/帧。
- 目标 30 FPS 时实际呈现 p95 帧间隔不超过 40 ms。
- 玻璃背景年龄 p95 不超过 100 ms。
- 新模块常驻 GPU 资源普通档不超过 128 MiB，大档不超过 256 MiB。
- 私有内存增量普通档不超过 64 MiB，大档不超过 128 MiB。
- 赛博朋克主题的捕获调用数必须为 0。

测量必须使用 Release、固定场景、同一进程交替 A/B、预热 30 秒、每轮采样至少 120 秒，并保存原始数据。历史数据、模拟后端和单张任务管理器截图不能作为通过证据。

## 7. 证据索引

基线证据目录：

```text
docs/reviews/evidence/gpu-themes/694a8c5/
  R00/baseline/
  R03/presentation/
  R04/capture/
  R04/joint/
  R06/glass-sample/
  R07/cyber-sample/
```

证据 manifest 必须记录命令、产物哈希、窗口/宿主参数、适配器、显示拓扑、固定场景、时间、通过/失败/跳过状态和限制。

## 8. 当前结论

- G0 代码、机器、构建入口、隔离要求和证据结构已建立。
- 当前只证明基线可构建；旧探针、单元测试和实机视觉尚未由本轮全部重跑。
- 不进入生产接线；先完成 R01、R02、R03、R04 和联合验证。
