# GPU 运行时切换 · T01 实测报告（`Control.RecreateHandle()` 可用性）

> **任务**：T01 · 判定 "`Control.RecreateHandle()` 在本项目窗口上是否可安全使用"（GPU 计划 §4.1 / §5 T01 / T00 基线 §8）
> **产出时间**：2026-10-23
> **上游**：`docs/superpowers/plans/2026-10-22-gpu-legacy-runtime-transition-plan.md`（REVISION-1）、`docs/superpowers/plans/2026-10-23-gpu-transition-t00-baseline.md`
> **探针**：`tools/GpuHostLifecycleProbe/**`（本次唯一改动的代码目录）
> **证据目录**：`.artifacts/gpu-t01/**`（不入库）

---

## 1. 结论

### **可安全使用**（`RECREATE_HANDLE_SAFE`）

`Control.RecreateHandle()` 在本项目的窗口上**可安全使用**：GPU 出帧后释放交换链，**重建 HWND 能让 GDI 在桌面上重新真实呈现动态画面**；而**不重建 HWND 则 GDI 永远出不来**（桌面停留在冻结的 GPU 帧）。

⇒ 走 GPU 计划 **§4.1 主线**（保留 Form 对象，按需重建 HWND），**不需要** §9 备用路线（独立 GPU 子 HWND）。

> 结论覆盖 4 次独立运行（S1 / S2-置顶 / S2-非置顶 / 真实 `MainForm`），四组结论完全一致。
> 限定条件与未验证项见 §8；T03/T04 必须遵守的接线约束见 §7。

### 三条依据（计划 §4.1 要求）

| # | 依据 | 实测结果 |
|---|---|---|
| **1** | **句柄销毁/创建事件序列 + 单调递增代次** | 4/4 运行均观测到 `HandleDestroyed` → `HandleCreated` 的严格顺序，且**单调代次恰好 +1**（S1/S2：1→2；真实 `MainForm`：0→1）。`recreate.Passed = true`。**判定不使用 `newHwnd != oldHwnd`**；句柄数值仅作记录（例：S1 `5382920 → 5448456`）。 |
| **2** | **窗口状态恢复结果** | 重建前后窗口状态**逐项相同**：可见性、置顶样式、扩展样式、窗口矩形/客户区、Region、分层 alpha、前台窗口句柄全部一致（见 §5 表）。**探针从未成为前台窗口**（`ForegroundIsThisWindow = false`，前台句柄全程恒为 67372）。 |
| **3** | **GDI 动态画面在桌面上的恢复证据** | 桌面像素**回读**（`Graphics.CopyFromScreen` 截窗口所在屏幕区域再回读）：A 组（只停 GPU、保留 HWND）3 帧全部 `0/1024` 命中且三帧颜色完全相同（= 冻结的 GPU 帧颜色）；B 组（停 GPU + 重建 HWND）3 帧全部 `1024/1024` 精确命中且**相邻两帧颜色不同**（活帧）。 |

---

## 2. 结论的直接含义

| 事实 | 证据 | 对方案的影响 |
|---|---|---|
| flip-model 交换链 Present 过之后，**同一 HWND 的 GDI 永久失效** | A 组桌面回读到的颜色与 GPU 出帧时的颜色**逐位相同**（S1 `FFFFC257` = `FFFFC257`） | 证实计划 §2.1 引用的微软结论：**"释放交换链"不等于"窗口恢复 GDI 能力"** ⇒ 必须重建 HWND |
| 重建 HWND 后 GDI 立即恢复 | B 组 3 帧 `1024/1024` 精确命中 + 相邻帧不同 | §4.1 主线成立；`UIController.StopGpuPresentation()`（`src/UI/UIController.cs:360`）不重建 HWND 的缺口**已被实测证实为真实缺陷** |
| 生产 GPU 宿主可被完整释放且不影响重建 | `GpuPresentationHostController.Dispose()` → `StopCore()`（`src/Rendering/Presentation/GpuPresentationHostController.cs:694`）释放 renderer + swap chain + DXGI factory | T03 可在切换协调器里直接复用现有停止路径，无需新增停止 API |

---

## 3. 实验设计

### 3.1 A/B 对照（同进程、同窗口、连续输入）

单次运行内**同一个 `Form` 实例**依次执行：

```
基线 GDI 帧 ×3（GPU 之前）        → 验证"桌面回读管线"本身可信
GPU 启动 → 首次真实 Present 确认   → 记录桌面上的 GPU 帧颜色
A 组：只停 GPU（Dispose 宿主，保留 HWND）→ GDI 动态帧 ×3 → 桌面回读
GPU 再次启动（同一 HWND）→ 首次真实 Present 确认
B 组：只停 GPU → RecreateHandle() → GDI 动态帧 ×3 → 桌面回读
```

- A/B 两组使用**完全相同**的输入、窗口、判据与采样点，唯一差异是 B 组多了一次 HWND 重建。
- 场景切换之间不换进程、不换窗口；窗口测试**串行**执行。

### 3.2 覆盖矩阵

| 场景 | 覆盖内容 | 结论 |
|---|---|---|
| **S1「正式 renderer」** | 生产 `GpuPresentationHostController` + 生产 `GpuSceneRenderer`（flip-model `SwapEffect.FlipSequential`，`src/Rendering/Presentation/GpuSceneRenderer.cs:164`；`Present(1, PresentFlags.None)`，`:644`），宿主 = 探针自己的 `Form` | `RECREATE_HANDLE_SAFE` |
| **S2「MainForm 形态」** | 复刻 T00 §3 的 HWND 绑定形态：`WS_EX_TOOLWINDOW`、按配置 `WS_EX_TOPMOST`、`WS_EX_TRANSPARENT` + `WS_EX_LAYERED`、`Form.Opacity`、窗口 Region、置顶 Z 序；`HandleCreated` 里按生产做法重放 Region 与置顶重插（对应 `MainForm_Transparent.cs:266-274`） | 置顶变体与**非置顶**变体各一次，均 `RECREATE_HANDLE_SAFE` |
| **真实 `MainForm`** | **已尝试并完成对照**（详见 §3.3） | `RECREATE_HANDLE_SAFE` |

### 3.3 真实 `MainForm` 场景：尝试过程与结果

**已尝试并完成**，但过程分三步，如实记录：

1. **第一次尝试（`run-mainform`）→ 隔离门失败，自动中止**。
   `AppContext.BaseDirectory` 取自**可执行文件路径**而非工作目录，因此第一次尝试启动的是仓库里的探针 exe，隔离的 `settings.json` 没被读到 → `HideTrayIcon` 回到默认 `false` → **托盘图标可见** → 隔离门 1 失败 → 探针**立即中止**，不执行 A/B。
   证据：`run-mainform/mainform-abort.json`（`trayIconVisible: true`、`webServerRunning: false`、`hardwareSamplerCreated: true`）。
   *已发生的副作用*：系统托盘图标出现约 1.6 秒，随进程退出消失。
2. **第二次尝试（`run-mainform2`）→ 测量管线不成立，中止**。
   修正为启动隔离目录中的 exe（`BaseDirectory` = 隔离目录），隔离门 1/2 均通过；但 `MainForm.OnPaint`（`src/UI/MainForm_Transparent.cs:402-406`）是 `base.OnPaint(e)` **之后**才 `_ui.Render(...)`，生产 legacy 渲染把探针画的帧覆盖掉 → 位置自检找不到"未被遮挡且是探针颜色"的采样点 → 中止。
   证据：`run-mainform2/failure.log`、`t01-mainform.json`。
3. **第三次尝试（`run-mainform3/4`）→ 成功**。
   探针在宿主完成绘制**之后**把动态帧直接画到窗口 DC 上（`AfterPaintOverlay`），并把隔离设置里的 `RefreshMs` 提到 30000 以避免 1 Hz 周期重绘抢在采样之前。得到完整 A/B 数据。

**隔离措施与残留副作用（真实 `MainForm` 组）**：

| 项 | 结果 | 证据 |
|---|---|---|
| 配置文件/日志写入位置 | 全部落在隔离目录 `.artifacts/gpu-t01/run-mainform4/isolated-run/`（`SettingsHelper.cs:14`、`TrafficLogger.cs:26`、`HardwareHistoryLogger.cs:38` 均为 `AppContext.BaseDirectory` 相对路径） | `run-mainform4/isolation-diff.json` |
| 托盘图标 | **不可见**（隔离设置 `HideTrayIcon = true`），门 1 通过 | `t01-mainform.json` → `extraObservations` |
| 网页服务器 | **未监听**任何端口（`LiteWebServer` 构造函数不调用 `Start()`），门 2 通过 | 同上 |
| **后台硬件采样** | **已启动且无法隔离**：`UIController` 构造函数即 `new HardwareMonitor(cfg)` 并起 UI 定时器 | 同上（`hardwareSamplerCreated: true`） |
| 用户真实目录写入 | 运行前后 `%LOCALAPPDATA%\LiteMonitor++`、`%APPDATA%` 下 LiteMonitor 目录、仓库 `bin/`、探针 `bin/` 四处比对：**新增 0、删除 0** | `run-mainform4/isolation-diff.json` |
| 未终止任何非本次启动的进程 | 是。watchdog 只 `Stop-Process -Id <本次 PID>` | 运行器脚本 |

> **因此**：真实 `MainForm` 组的**唯一残留副作用是进程内的后台硬件采样**（随进程退出消失，不写用户目录、不占端口、不显示托盘）。这不构成 §8 的"污染用户真实配置目录"，但如实登记。

---

## 4. 像素判据（可复现的具体定义）

### 4.1 动态帧颜色编码

第 `n` 帧（`n ≥ 1`）的填充色：

```
R = (n * 53)  mod 256
G = (n * 97)  mod 256
B = (n * 149) mod 256
```

| 帧 | 期望色（ARGB） | 十六进制 |
|---|---|---|
| 1 | (53, 97, 149) | `FF356195` |
| 2 | (106, 194, 42) | `FF6AC22A` |
| 3 | (159, 35, 191) | `FF9F23BF` |
| 4 | (212, 132, 84) | `FFD48454` |
| 5 | (9, 229, 233) | `FF09E5E9` |
| 6 | (62, 70, 126) | `FF3E467E` |
| 7 | (115, 167, 19) | `FF73A713` |
| 8 | (168, 8, 168) | `FFA808A8` |
| 9 | (221, 105, 61) | `FFDD693D` |

相邻帧颜色**必然不同**（上表可验证），因此"颜色变了"就等于"是新的一帧"。

### 4.2 采样与判定

- **采样点**：窗口**客户区中心**的 **32 × 32** 桌面像素块（用 `GetClientRect` + `ClientToScreen` 取**物理像素**坐标；采样全程用原生 API 取物理像素，不受 WinForms 逻辑坐标与 DPI 虚拟化影响）。
- **采集方式**：`Graphics.CopyFromScreen(clientX, clientY, 0, 0, size, CopyPixelOperation.SourceCopy)` **截取桌面**后回读像素 —— 即**看合成器真正显示在屏幕上的内容**。
- **匹配判定**：块内像素逐个与期望色比较，**每通道容差 ≤ 12**；**命中率 ≥ 90%** 记为该帧命中。实际 4 组运行命中帧均为 **1024/1024（100%）**，非命中帧均为 **0/1024（0%）**，不存在中间态。
- **分层窗口补偿**：若窗口带 `WS_EX_LAYERED` 且 `GetLayeredWindowAttributes` 返回的 alpha < 255，期望色按 `alpha·帧色 + (1-alpha)·窗口外侧背景采样` 合成。实际 S2 两组 alpha = 255（不透明），补偿未生效；该分支**未经真实半透明窗口验证**（见 §8）。
- **冻结帧判别**：比较**相邻两帧的实测中心色**。
  - 命中且相邻不同 ⇒ **活的 GDI 帧**（B 组）。
  - 不命中且相邻**完全相同** ⇒ **冻结画面**（A 组，实测与 GPU 出帧时的颜色逐位相同 ⇒ 冻结的是 GPU 帧）。

### 4.3 明确排除的"伪证据"

计划 §7.3 禁止用 `DrawToBitmap` / `PrintWindow` / "GDI 函数被调用" / "offscreen render 成功"单独作为 GDI 恢复证据。本报告**没有**使用它们：所有 GDI 恢复结论都来自**桌面像素回读**。`CaptureScenePng` 仅作为 GPU 出帧的**辅助**证据（它只是场景纹理，不是桌面可见性证据）。

### 4.4 测量管线自身的有效性（前置自检）

- **位置自检**：探针把窗口依次移到若干候选位置，每个位置先画一个**洋红色**探针帧再回读；只有回读确实等于洋红才采用该位置。命中时记录 `positionProbe`。若一个位置都找不到 → **直接判失败**，不出结论（S1/S2/真实 MainForm 均找到；`run-mainform2` 就是因此中止的）。
- **GPU 之前**先测 3 帧基线：必须 3/3 精确命中且相邻帧不同，否则判定"测量管线不可信"并中止。4 组运行全部通过。
- 这两项自检保证了 §7.2 所说的"旧 GPU 冻结帧看上去像 legacy"的误判陷阱被排除。

---

## 5. A/B 对照关键数据（4 组运行）

### 5.1 汇总

| 运行 | 场景 | A 组（只停 GPU，保留 HWND） | B 组（停 GPU + 重建 HWND） | 重建断言 | 候选源文件变化 |
|---|---|---|---|---|---|
| `run-s1` | S1 正式 renderer | **未恢复**：`0/1024` ×3，三帧同为 `FFFFC257`（= GPU 帧色 `FFFFC257`，逐位相同） | **已恢复**：`1024/1024` ×3，相邻帧不同 | 通过（gen 1→2） | 无 |
| `run-s2-topmost` | S2 MainForm 形态（置顶） | **未恢复**：`0/1024` ×3，三帧同为 `FF132F30`（= GPU 帧色 `FF132F30`，逐位相同） | **已恢复**：`1024/1024` ×3，相邻帧不同 | 通过（gen 1→2） | 无 |
| `run-s2-plain` | S2 MainForm 形态（非置顶） | **未恢复**：`0/1024` ×3，三帧同为 `FFFFC257`（= GPU 帧色 `FFFFC257`，逐位相同） | **已恢复**：`1024/1024` ×3，相邻帧不同 | 通过（gen 1→2） | 无 |
| `run-mainform4` | **真实 `MainForm`** | **未恢复**：`0/1024` ×3，三帧同为 `FF132E30`（= GPU 帧色 `FF132E30`，逐位相同） | **已恢复**：`1024/1024` ×3，相邻帧不同 | 通过（gen 0→1） | 无 |

> A 组三帧颜色**逐位等于**该运行 GPU 出帧时桌面中心色 ⇒ A 组看到的是**冻结的 GPU 帧**，不是黑色、不是空白、也不是被遮挡。

### 5.2 逐帧原始数据（S1，代表性；其余三组同构）

```
[基线 · GPU 之前]
 f1 exp=FF356195 obs=FF356195 1024/1024
 f2 exp=FF6AC22A obs=FF6AC22A 1024/1024
 f3 exp=FF9F23BF obs=FF9F23BF 1024/1024
[A 组 · 只停 GPU，保留 HWND]
 f4 exp=FFD48454 obs=FFFFC257 0/1024
 f5 exp=FF09E5E9 obs=FFFFC257 0/1024
 f6 exp=FF3E467E obs=FFFFC257 0/1024
[B 组 · 停 GPU + RecreateHandle]
 f7 exp=FF73A713 obs=FF73A713 1024/1024
 f8 exp=FFA808A8 obs=FFA808A8 1024/1024
 f9 exp=FFDD693D obs=FFDD693D 1024/1024
[GPU 出帧确认] gpu-phase-1 presentCount=9  gpu-phase-2 presentCount=9
```

### 5.3 句柄事件序列（4 组一致）

```
S1:  seq1 HandleCreated  gen=1  hwnd=5382920
     seq2 HandleDestroyed gen=1  hwnd=5382920
     seq3 HandleCreated  gen=2  hwnd=5448456
S2 置顶:  seq1 HandleCreated gen=1 hwnd=12394762
          seq2 HandleDestroyed gen=1 hwnd=12394762
          seq3 HandleCreated gen=2 hwnd=12460298
S2 非置顶: seq1 HandleCreated gen=1 hwnd=6431562
           seq2 HandleDestroyed gen=1 hwnd=6431562
           seq3 HandleCreated gen=2 hwnd=6497098
真实 MainForm: seq1 HandleDestroyed gen=0
              seq2 HandleCreated  gen=1  hwnd=7414572
```

> **真实 `MainForm` 的"构造期首次创建"事件未被探针观测到**（句柄在 `MainForm` 构造函数内就被创建，探针在其之后才订阅事件），故该组代次从 0 起算。这**实证了**计划 §4.2 / U06b 所说的"`HandleCreated` 有两个来源"问题在真实 `MainForm` 上确实存在 —— T03 引入"受控重建判别位"是必需的，不能靠"每次 `HandleCreated` 都处理"。

### 5.4 窗口状态：重建前后对照

| 运行 | 可见 | 置顶样式 | 扩展样式 | 窗口矩形 | Region | 分层 alpha | 前台窗口（是否探针） |
|---|---|---|---|---|---|---|---|
| S1 | True → True | False → False | `0x08010000` → 同 | (-1370,70,420×300) → 同 | 无 → 无 | 不分层 → 不分层 | 67372 → 67372（**否**） |
| S2 置顶 | True → True | True → True | `0x080900A8` → 同 | (70,70,420×300) → 同 | 类型 3 → 类型 3 | 255 → 255 | 67372 → 67372（**否**） |
| S2 非置顶 | True → True | False → False | `0x080900A0` → 同 | (-1370,70,420×300) → 同 | 类型 3 → 类型 3 | 255 → 255 | 67372 → 67372（**否**） |
| 真实 `MainForm` | True → True | True → True | `0x00010088` → 同 | (70,70,420×1373) → 同 | 无 → 无 | 不分层 → 不分层 | 67372 → 67372（**否**） |

**要点**：
- 重建后窗口**仍然可见**、**没有跳位**、**没有丢置顶**、**没有丢 Region**、**没有丢分层 alpha**；探针**全程没有抢到焦点**（`WS_EX_NOACTIVATE` + `ShowWithoutActivation` 生效，前台窗口句柄四组全程恒定）。
- 真实 `MainForm` 组**无需重新 Show**（可见性自动保持），也**没有再次触发淡入**（`Opacity` 读回恒为 1）。

---

## 6. 证据索引

### 6.1 构建

```powershell
cd 'G:\TOOL\LiteMonitor-fork'
dotnet build 'tools\GpuHostLifecycleProbe\GpuHostLifecycleProbe.csproj' -c Debug -v minimal --nologo
# 退出码 0，0 个错误
```

### 6.2 运行（每条都是独立进程 + 外部 watchdog；命令、退出码、耗时）

| # | 命令 | 退出码 | 耗时 | watchdog |
|---|---|---|---|---|
| R1 | `pwsh -NoProfile -File '.artifacts\gpu-t01\run-scenario.ps1' -Scenario s1 -OutputDir '.artifacts\gpu-t01\run-s1' -TimeoutSeconds 90` | **0** | 8.7 s | 未触发（阈值 90 s） |
| R2 | `pwsh -NoProfile -File '.artifacts\gpu-t01\run-scenario.ps1' -Scenario s2 -OutputDir '.artifacts\gpu-t01\run-s2-topmost' -TimeoutSeconds 90 -Topmost -ClickThrough` | **0** | 7.2 s | 未触发 |
| R3 | `pwsh -NoProfile -File '.artifacts\gpu-t01\run-scenario.ps1' -Scenario s2 -OutputDir '.artifacts\gpu-t01\run-s2-plain' -TimeoutSeconds 90 -ClickThrough` | **0** | 8.7 s | 未触发 |
| R4 | `pwsh -NoProfile -File '.artifacts\gpu-t01\run-scenario.ps1' -Scenario mainform -OutputDir '.artifacts\gpu-t01\run-mainform4' -TimeoutSeconds 100 -Isolated -IsolatedSettings '.artifacts\gpu-t01\isolated-settings.json' -ProbeTimeoutMs 45000` | **0** | 8.9 s | 未触发（阈值 100 s） |
| R0-a | 同 R4（第一次，`run-mainform`） | 4（隔离门失败，主动中止） | 1.6 s | 未触发 |
| R0-b | 同 R4（第二次，`run-mainform2`） | 1（测量管线不成立，中止） | 6.2 s | 未触发 |
| R0-c | 同 R4（第三次，`run-mainform3`，叠加绘制已修好但反射调用目标写错） | 1（`TargetException: Non-static method requires a target`，探针缺陷） | 7.8 s | 未触发 |

探针进程内另有**自身截止时间**（`--timeout-ms`，默认 50000 / R4 为 45000），与外部 watchdog 构成双层取消机制。

### 6.3 证据文件

| 路径 | 内容 |
|---|---|
| `.artifacts/gpu-t01/run-s1/t01-S1FormalRenderer.json` | S1 完整证据（判据、逐帧像素、句柄事件、窗口状态、环境、GPU 诊断） |
| `.artifacts/gpu-t01/run-s1/{baseline,roundA,roundB}-*.png` | 桌面回读截图（各阶段逐帧） |
| `.artifacts/gpu-t01/run-s1/gpu-phase-{1,2}-{desktop,scene}.png` | GPU 出帧时的桌面截图 + 场景纹理（辅助证据） |
| `.artifacts/gpu-t01/run-s2-topmost/t01-S2MainFormShape.json` + 同名 PNG | S2 置顶变体 |
| `.artifacts/gpu-t01/run-s2-plain/t01-S2MainFormShape.json` + 同名 PNG | S2 非置顶变体 |
| `.artifacts/gpu-t01/run-mainform4/t01-mainform.json` + 同名 PNG | 真实 `MainForm` 组 |
| `.artifacts/gpu-t01/run-mainform/mainform-abort.json` | 第一次尝试的隔离门失败记录（托盘图标可见） |
| `.artifacts/gpu-t01/run-mainform2/failure.log`、`t01-mainform.json` | 第二次尝试的中止原因 |
| `.artifacts/gpu-t01/*/isolation-diff.json` | 每次运行的隔离比对（4 个范围，新增/删除均为 0） |
| `.artifacts/gpu-t01/run-scenario.ps1` | 运行器（外部 watchdog + 隔离快照） |
| `.artifacts/gpu-t01/isolated-settings.json` | 真实 `MainForm` 组的隔离配置 |
| `.artifacts/gpu-t01-pre-hash.txt` / `.artifacts/gpu-t01-post-hash.txt` | 运行前 / 运行后的候选哈希 |

**图像判读示例**（`run-s1`）：`roundA-...-frame4.png` 显示的仍是 GPU 渲染的 "SYSTEM PULSE / GPU PACKAGE" 场景（左上角可见被裁切的桌面文字，说明采样块在窗口内）；`roundB-...-frame7.png` 显示的是纯色 GDI 帧并带 "GDI 7" 角标。

---

## 7. 对 T03/T04 的约束（由实测直接推出）

1. **必须先完整停止 GPU 再重建**：本实验用的是生产 `Dispose()` 路径（释放 renderer + swap chain + DXGI factory）。任何"只停渲染调度、保留交换链"的做法都落在 A 组行为里（`Suspend()` 不释放交换链 ⇒ GDI 仍不可用）。
2. **重建入口必须是 Form 自己的受控方法**（计划 §4.3 步骤 4）：生产 `src/` 目前 `RecreateHandle` 零命中；本次探针用的是派生类直调（探针形态）与反射（真实 `MainForm`，与 `tools/GpuUiProbe/Program.cs:469-473` 同法），**都不构成生产可用依据**。
3. **重建必须在 UI 队列安全点执行**，且**重建期间要屏蔽自动拉起 GPU**（真实 `MainForm` 的 `HandleCreated` 会调 `NotifyGpuHandleChanged()`，T00 §7 已记录其无"重建/首次创建"判别位 —— 本次实测再次证实该事件在构造期就已发生）。
4. **窗口状态必须在 `HandleCreated` 里按代次重放**：置顶样式与 Z 序、Region、分层 alpha、可见性在本次实测中都能在重建后保持，但前提是生产代码像 `MainForm_Transparent.cs:266-274` 那样在 `HandleCreated` 里重放。
5. **重建不得触发淡入/抢焦点**：本次实测前台窗口句柄全程未变，说明 `WS_EX_NOACTIVATE` + `ShowWithoutActivation` 组合有效，但 `MainForm` 自己的淡入动画（`StartFadeIn`）在重建后是否重跑**未验证**（见 §8）。
6. **`GPU-001`（WDA 恢复失败后永久不可重启）仍然卡死主线**：本实验用 `WindowCapturePolicy.None`，未触发 WDA 失败路径；T02/T04 的"清理责任转交"合同必须先解决它，否则重建路径走不到。

---

## 8. `NOT_VERIFIED` 与未验证项清单（如实登记）

| # | 项目 | 状态 | 原因 / 补测方式 |
|---|---|---|---|
| N1 | **WDA / 显示亲和性恢复**（GPU-003 / U07 / U08 / G10） | `NOT_VERIFIED` | 本次固定用 `WindowCapturePolicy.None`，刻意不触碰 affinity（否则会违反"探针窗口不得被 WDA 排除"）。需在 T07/T09 专项补测：`GetWindowLong(GWL_EXSTYLE)` 读回 + `GetWindowDisplayAffinity` 读回。 |
| N2 | **`MakeWindowAssociation` 残留**（GPU-004） | `NOT_VERIFIED` | 本次 A/B 中 DXGI factory 随 renderer 一起释放，残留行为未被触发。需单独构造"只释放交换链、保留 factory"的变体。 |
| N3 | **半透明（`Opacity < 1`）窗口的 GDI 恢复**（T00 §7 第 5 项） | `NOT_VERIFIED` | S2 两组 alpha = 255（不透明）。真实 `MainForm` 组因 `Opacity` 终值 1.0 也不分层。需补测 `Opacity = 0.85` 的变体，届时像素判据改用 §4.2 的 alpha 合成分支（该分支本身也未在真实半透明窗口上验证过）。 |
| N4 | **隐藏 / 最小化状态下切换**（G05 / U13） | `NOT_VERIFIED` | 本次四个窗口都可见。计划 §4.3 步骤 7 的"隐藏窗口保持隐藏"未测。 |
| N5 | **重建后的淡入动画抑制 / DPI 与布局重算**（计划 §4.3 步骤 6、G06/G08） | `NOT_VERIFIED` | 本次窗口在 100% 缩放的同一块屏上重建，位置/尺寸/Region 全部保持，未触发跨 DPI 或跨屏重算。 |
| N6 | **live / backdrop 捕获链与 GPU live 互斥**（U14 / G10） | `NOT_VERIFIED` | 本次只用 fixed 背景（`liveBackgroundRequested: false`），未触及 `src/System/Backdrop/**`。T00 §1.3 第 4 条：该目录**不在**冻结候选覆盖范围内，若要测须另行冻结。 |
| N7 | **10 次连续往返**（G03） | `NOT_VERIFIED` | 本次每个场景只做 1 次重建（A 一次、B 一次）。计划 §7.2 的 10 次往返组属 T09。 |
| N8 | **真实 `MainForm` 场景的后台硬件采样隔离** | 部分 `NOT_VERIFIED` | 托盘、端口、文件写入均已证明隔离；**进程内后台采样无法隔离**（`UIController` 构造函数即启动）。见 §3.3。 |
| N9 | **探针自身的 `settings.json` 残留** | 已知残留，不影响结论 | 第一次（未隔离）尝试把 `settings.json` 写到了 `tools/GpuHostLifecycleProbe/bin/Debug/net8.0-windows/`。该路径在 `bin/` 下、被 gitignore、**不属于任何哈希组**。尝试用可恢复删除清理时被本机删除策略拒绝（"Windows recoverable-delete plan requires the targets-only trash marker"），**未强行绕过**，故保留。 |
| N10 | **T00 §2.1 的屏幕分辨率已过时** | 环境事实更正 | 本次运行实测：`DISPLAY1 = 3840×2160`、`DISPLAY2 = 1440×2560 @ x=-1440`（WinForms `Screen` 与 DXGI `DesktopCoordinates` **两个独立来源一致**，并经 `Win32_VideoController` 复核 RTX 3080 = 3840×2160）。T00 记录的 `2194×1234 / 1152×2048 @ -1152` **与本次实测不符**。⇒ T09 的 G06/G07 必须以**实机当前值**为准，不得沿用 T00 §2.1。 |

---

## 9. 候选绑定（计划 §3 冻结候选要求 2/3）

聚合算法与 T00 §1.2 一致：组内文件按相对路径排序 → 拼接 `"<相对路径>\0<小写SHA256>\n"` → 对 UTF-8 字节流取 SHA-256。复现脚本 `.artifacts/t00-candidate-hashes.ps1`（未入库）。

| 组 | T00 基线（`e1bd587`） | 运行前（`18d5fab`） | 运行后（`9a032fa`） | 判定 |
|---|---|---|---|---|
| 生产 `src/Rendering/**`（22 文件） | `a39d49ce83d13a7bbba0bbe2536a40583831eb742bf5bef7f0d62e831ba8a8e2` | 同左 | **同左** | ✅ **未变** |
| 生产 `src/UI/**`（57 文件） | `502629b54aded5d78c06540e83813fcceff54e19e80052f45969fe3d51dcb1e7` | 同左 | **同左** | ✅ **未变** |
| 主题 JSON（15 文件） | `76422a86cf1fe20a2b1fafacbfbd24834f201283f388f278fea85f465c483cf6` | 同左 | **同左** | ✅ 未变 |
| 配置与 shader（8 文件） | `1078b22e49b3642afa27bb0784790e3863a4a674a06ee8a102c2851dbb1d9e65` | 同左 | **同左** | ✅ 未变 |
| **两个探针（改后 14 文件）** | `6c49338496b4295ff678c65f82eb3323d08758647d7d0b3a1a3e4bfe8a3b593c` | 同左 | **`f1d0d41b544ba6647470a98487320e458666a748df91e8685a92a8e474c5b006`** | ⚠ 预期变化（T01 必须改探针） |
| └ `tools/GpuHostLifecycleProbe/**`（改后 9 文件） | `5776922684da27813c01affd75c0b16450f9f36e8fdb5d66bca9cbe7e50a349f` | 同左 | **`e97ca6614acbc5b818971328e4468e97402f61feed5d3f847fd58e1c9f04c4aa`** | ⚠ 预期变化 |
| └ `tools/GpuUiProbe/**`（5 文件，**属 T07，未改**） | `9a08019a7e5135a3f3350f8496904f02f667ce01420d458619b5c6f8181079ab` | 同左 | **同左** | ✅ 未变 |

**声明**：本次 T01 **只改动 `tools/GpuHostLifecycleProbe/**`**；生产侧 `src/Rendering/**` 与 `src/UI/**` 的聚合 SHA-256 **运行前后完全一致**，与 T00 基线一致。探针侧哈希变化是 T01 的预期结果（计划 §3 冻结候选要求的第 3 条 / T00 §1.3 第 1 条）。

**运行中源文件漂移检测**：探针在运行开始与结束各计算一次 `src/Rendering/**`、`src/UI/**` 聚合哈希并比对，不一致即把证据标为 `sourceHashChanged = true` 并以退出码 2 作废（计划 §3 冻结候选要求 3 的**强制实现**，非人工声明）。**4 次有效运行全部为 `sourceHashChanged = false`。**

**会话期间的并发提交**：本次会话中 `HEAD` 依次为 `18d5fab`（T00 基线）→ `7214135` → `9a032fa`，均为他人提交的**纯文档**改动（`docs/superpowers/plans/2026-10-22-v3-step-agent-remediation-plan.md`）；`src/Rendering/**`、`src/UI/**` 哈希全程未变，故不影响结论。

---

## 10. 适配器归属

| 项 | 实测值 |
|---|---|
| 探针窗口实际所在显示适配器 | **NVIDIA GeForce RTX 3080**（DXGI `DesktopCoordinates` 与 `EnumDisplayDevices` 两个来源一致） |
| 所在显示器 | S1 / S2 非置顶：`\\.\DISPLAY2`（竖屏副屏，桌面坐标 -1440..0 × 0..2560）；S2 置顶 / 真实 `MainForm`：`\\.\DISPLAY1`（主屏 3840×2160） |
| D3D11 渲染适配器 | **NVIDIA GeForce RTX 3080**（`GpuSceneRendererDiagnostics.AdapterName`，4 组一致） |
| 本机其它适配器 | NVIDIA GeForce RTX 3080 Ti、GameViewer / Meta Virtual Monitor / MuMu / Todesk 四个虚拟显示适配器 —— **探针窗口未落在任何虚拟适配器上** |
| 结论 | 本次四个窗口场景**全部在实体 NVIDIA RTX 3080 上取证**，不涉及虚拟显示适配器；**不能**据此断言虚拟适配器上的 flip-model 行为相同（见 §8 未验证项） |

---

## 11. 窗口纪律遵守情况

| 纪律（计划 §7.3） | 遵守情况 |
|---|---|
| 默认非置顶 | R1/R3/R4 非置顶；**R2（S2 置顶变体）显式置顶约 7.2 秒** —— 该变体按任务要求复刻 `MainForm` 的 `WS_EX_TOPMOST` + Z 序形态，窗口 420×300，未铺满屏幕，已在 §5/§10 标明 |
| 不激活 / 不抢焦点 | `ShowWithoutActivation = true` + `WS_EX_NOACTIVATE`；实测前台窗口句柄 4 组全程恒为 67372，`ForegroundIsThisWindow` 始终为 false |
| 单轮 ≤ 60 秒 | 有效运行 7.2 ~ 9.8 秒 |
| 外部 watchdog + 取消机制 | 运行器 `Wait-Process -Timeout`，超时只终止**本次 PID**；探针内另有 `--timeout-ms` 自身截止。4 次有效运行 watchdog 均未触发 |
| 窗口测试串行 | R1 → R2 → R3 → R4 顺序执行，互不重叠 |
| 只清理本次记录的 PID | 是；未终止任何非本次启动的进程（包括他人的 `testhost` / `mutate.ps1`） |
| WDA 不排除探针窗口 | 全程 `WindowCapturePolicy.None`；`ApplyCapturePolicyCore` 在 `None` 下不施加任何 affinity |
| 独立配置 / 临时目录 | 真实 `MainForm` 组全程在 `.artifacts/gpu-t01/run-mainform4/isolated-run/` |
| 证据目录 | `.artifacts/gpu-t01/**`（被 `.artifacts/.gitignore` 的 `*` 忽略，不入库） |

---

## 12. 改动清单

| 文件 | 说明 |
|---|---|
| `tools/GpuHostLifecycleProbe/Program.cs` | CLI 解析与场景分发（`lifecycle` / `s1` / `s2` / `mainform`）；原有 `lifecycle` 场景行为保持不变 |
| `tools/GpuHostLifecycleProbe/ProbeForm.cs` | **原 `Program.cs` 内的 `ProbeForm` 原样迁出**（内容未改），使分发器与探针窗口解耦 |
| `tools/GpuHostLifecycleProbe/RecreateHandleScenario.cs` | A/B 实验主体：帧色编码、桌面回读与判据、句柄事件与代次、窗口状态快照、证据 JSON |
| `tools/GpuHostLifecycleProbe/RecreateHandleScenarioForm.cs` | 探针窗口（S1 普通形态 / S2 MainForm 形态复刻，含 `WS_EX_NOACTIVATE`、Region、置顶重插、动态 GDI 绘制） |
| `tools/GpuHostLifecycleProbe/DesktopObservation.cs` | 桌面截取/像素比对/窗口状态原生读取（`GetWindowLong`、`GetWindowRgn`、`GetLayeredWindowAttributes`、`ClientToScreen` 等） |
| `tools/GpuHostLifecycleProbe/DisplayAdapters.cs` | DXGI 输出枚举（`DesktopCoordinates`）+ `EnumDisplayDevices` 适配器归属解析 |
| `tools/GpuHostLifecycleProbe/CandidateHashGuard.cs` | 运行期间候选源文件漂移检测（强制实现计划 §3 要求 3） |
| `tools/GpuHostLifecycleProbe/RealMainFormScenario.cs` | 真实 `MainForm` 场景：隔离门、侧效应观测、反射 `RecreateHandle`、生产渲染之后的 DC 叠加绘制 |
| `docs/superpowers/plans/2026-10-23-gpu-transition-t01-report.md` | 本报告 |

**未改动**：`src/**`、`tests/**`、`resources/**`、`tools/GpuUiProbe/**`、任何 `.csproj`（含探针自己的）、其它 `docs/**`、根 `.gitignore`。

---

## 13. 结论摘要

> **`Control.RecreateHandle()` 在本项目窗口上可安全使用**（`RECREATE_HANDLE_SAFE`）。
>
> 4 组独立运行（S1 正式 renderer、S2 MainForm 形态置顶、S2 MainForm 形态非置顶、**真实 `MainForm`**）一致给出：
> **只停 GPU 保留 HWND ⇒ 桌面永远停留在冻结的 GPU 帧（GDI 不可见）；停 GPU 并重建 HWND ⇒ GDI 动态画面立即在桌面真实恢复（逐帧精确命中、相邻帧不同）。**
>
> ⇒ 走计划 §4.1 主线，**不启用** §9 备用路线。T03/T04 必须遵守 §7 的 6 条接线约束；`NOT_VERIFIED` 项（N1~N10）必须交 T07/T09 补齐，其中 **`GPU-001`（WDA 恢复失败后 GPU 永久不可重启）仍然直接卡死主线**，须由 T02 的"清理责任转交"合同先行解决。
