# 多显卡「每卡一个小标题」分组改造计划

- **文档日期**：2026-10-20
- **基线分支 / HEAD**：`fix/gpu-vram-d3d` @ `0230bef`（含多卡改造 + 显存串卡修复）
- **目标分支**：`feat/gpu-group-titles`
- **实施状态**：✅ **已实施**（5 个实现提交，见 §4）
- **独立审核**：verifier agent 审核结论 **FAIL**（3×P1 + 5×P2 + 6×P3），**全部已处理**，见 §9
- **不涉及**：用户当前未提交的改动（`resources/LiteMonitor++.Updater.exe` + 3 个未跟踪 doc），本计划一行都不碰

---

## 0. 一句话目标

把「所有显卡共用一个 `GPU` 分组」改成「**每张卡一个独立分组**」，每组自带一个**可自定义的小标题**（默认 = 显卡简称，如 `3080 Ti`），该卡的 6 个指标（使用率 / 温度 / 频率 / 功耗 / 风扇 / 显存）全部包裹在这个标题下面。

---

## 1. 需求原文与验收口径

> 我想给不同的卡设置不同的小标题，包裹住使用率、温度、频率、功耗、风扇、显存，这样就不用看好多个重复的显卡型号了。

用户已选定 **方案 A = 默认标题用显卡简称**（`3080 Ti` / `3080`），并且可以被用户自定义覆盖。

### 1.1 目标效果（2 卡机器）

改造前（所有卡挤在一个 `GPU` 组里，项标签重复带显卡简称）：

```
GPU
  3080 Ti 使用率        55 %
  3080 Ti 温度          61 °C
  3080 Ti 频率        1620 MHz
  3080 Ti 功耗         210 W
  3080 Ti 风扇           0 %
  3080 Ti 显存占用      46 %
  3080 使用率           0 %
  3080 温度            45 °C
  ...
```

改造后（每卡一组，组标题 = 小标题，组内项标签不再重复显卡型号）：

```
3080 Ti                 ← 小组标题（可自定义）
  使用率                55 %
  温度                  61 °C
  频率                1620 MHz
  功耗                 210 W
  风扇                   0 %
  显存占用              46 %

3080                   ← 小组标题（可自定义）
  使用率                 0 %
  温度                  45 °C
  ...
```

### 1.2 验收口径

| # | 验收项 | 判定方式 | 结果 |
|---|--------|----------|------|
| A1 | 面板上每张显卡是**独立的一个圆角组框** | 真机截图 | ✅ 已验证（`lm-verify/e2e-panel.png`） |
| A2 | 组标题默认等于**显卡简称** | 真机截图 | ✅ 已验证（标题为 `3080 Ti` / `3080`） |
| A3 | 组内 6 个指标**不再重复显卡型号** | 真机截图 | ✅ 已验证（项标签为 使用率/温度/频率/功耗/风扇/显存占用） |
| A4 | 设置页能**逐卡**改标题 | 真机操作 | ⬜ 待人工确认（读写逻辑已有单元测试覆盖） |
| A5 | 改完保存 → 重启 → 标题仍在 | 真机操作 | ⬜ 待人工确认（同上） |
| A6 | 网页端按卡拆成多张卡片，且**不白屏** | 真机操作 | ⬜ 待人工确认（消毒逻辑已用 headless Chrome 实测） |
| A7 | 右键菜单按卡拆成多个分组 | 真机操作 | ⬜ 待人工确认 |
| A8 | 构建 **0 错误**、警告数**零新增** | `dotnet build` | ✅ 228（与同 HEAD 基线一致） |
| A9 | 测试**全通过** | `dotnet test` | ✅ 221（全通过） |
| A10 | 横屏模式下不出现组标题（已知限制，见 §7.2） | 真机观察 | ✅ 代码层已确认（`HorizontalLayout` 无组标题绘制） |
| A11 | 设置页把标题改回默认值 = 清除别名、回到默认标题 | 真机操作 | ✅ 逻辑层有单元测试覆盖 |

---

## 2. 现状勘察结论

### 2.1 机制已经存在 90%，只差「按卡拆组」这一个开关

本功能的**全部基础设施都已就绪**，不需要新造面板渲染特性：

| 能力 | 位置 | 状态 |
|------|------|------|
| 分组别名存储 | `src/Core/Settings.cs:152` `Dictionary<string,string> GroupAliases` | ✅ 已有 |
| 设置页组标题输入框 | `src/UI/Controls/MonitorControls.cs:338-386` `MonitorGroupHeader.InputAlias` | ✅ 已有 |
| 面板画圆角组框 + 组标题 | `src/UI/UIRenderer.cs:75-104` `DrawGroupBackground` | ✅ 已有 |
| 标题解析（别名覆盖） | **6 处 / 4 个界面**：面板 2（`UIController.cs:296-299,346-349`）、设置页 2（`MonitorControls.cs:362` 取初值 + `MonitorPage.cs:501` 保存判定）、右键菜单 1（`MenuMonitorHelper.cs:239-240`）、网页端 1（`LiteWebServer.cs:180-183`） | ✅ 已有，但**逻辑分散** |
| 设置页按组分块 | `src/UI/Settings/MonitorPage.cs:299-307, 391-416` | ✅ 已有 |
| 网页端按组分卡 | `src/System/WebServer/LiteWebServer.cs:132,180,248` | ✅ 已有 |

### 2.2 唯一缺口：`UIGroup` 把所有显卡塞进同一组

`src/Core/Settings.cs:266`（`Key` setter 内）：

```csharp
CachedUIGroup = UIUtils.Intern(_key.Split('.')[0]);   // "GPU.0.Load" -> "GPU"
```

`src/Core/Settings.cs:331`（`UIGroup` fallback 分支）：

```csharp
return Key.Split('.')[0];                             // 同上
```

因为 `GPU.0.Load` / `GPU.1.Load` 的第一段都是 `GPU`，所以两张卡的 12 个指标全在同一个 `GPU` 组里。

**把这两处改成「带序号的显卡 Key → `GPU.{序号}`」即可**，面板 / 设置页 / 菜单 / 网页端会**自动**跟着拆开，因为它们全都按 `UIGroup` 分组。

### 2.3 已经确认**不受影响**的路径

| 路径 | 坐标 | 为什么不受影响 |
|------|------|----------------|
| `Settings.IsAnyEnabled("GPU")` | `src/Core/SettingsHelper.cs:483-486` | 走 `StartsWith` 前缀匹配，`GPU.0.Load` 仍命中 |
| `MetricUtils.GetType` | `src/Core/MetricUtils.cs` | 走子串匹配，`GPU.1.Temp` 仍含 `TEMP` |
| 任务栏 / 托盘 | `src/UI/UIController.cs:372-379` | 任务栏是**扁平排序**（`TaskbarSortIndex`），根本不分组 |
| 历史曲线 / 历史记录 | `src/System/...` | 按 series（`GPU.0.Load`）分组，不依赖 `UIGroup` |
| 主题编辑器 | `src/ThemeEditor/ThemePreviewControl.cs:100` | 用假数据，硬编码 `GPU` 组，纯预览 |
| 布局引擎 | `src/UI/UILayout.cs:68` | 遍历任意数量的组，已支持 6+ 个组 |
| 面板高度 | `src/UI/UIController.cs` `Render()` | `_form.ClientSize = new Size(w, _layout.Build(_groups))` → 高度自适应，组数 +1 **不会**把内容裁掉 |

> ⚠️ **横屏模式是例外**：横屏走 `HorizontalRenderer`，`BuildColumnsCore` 会把分组结构**拍平成 `Column` 列表并丢弃组信息**，
> `HorizontalLayout` / `HorizontalRenderer` 里**没有任何组标题绘制**。横屏下区分卡靠的是 `item.ShortLabel`
>（显卡简称），**用户自定义的小标题在横屏不可见**。详见 §7.2。

### 2.4 组顺序天然正确

`GpuItemSync` 给每张卡分配 `SortIndex = baseSort + i * GpuItemSync.SortStride`（步长 10，基准 301~306）：

- `GPU.0.*` → 301..306 → 组内 `min(SortIndex)` = 301
- `GPU.1.*` → 311..316 → 组内 `min(SortIndex)` = 311

组间排序用 `g.Min(x => x.SortIndex)`（`UIController.cs:279`）→ GPU.0 组在 GPU.1 组之前，**与用户期望的显卡顺序一致**，且两张卡的组是**相邻**的。

> ⚠️ **注意**：`MonitorPage.SaveToWorkingList` 在保存设置页时会把所有项的 `SortIndex` **全局压平**成连续的 0..N-1
>（实测：GPU.0 → 11..16，GPU.1 → 17..22）。**组顺序结论不受影响**（min 值仍然递增），
> 但 `GpuItemSync` 用 `if (firstCard.SortIndex != 0)` 当「未设置」哨兵（`GpuItemSync.cs:109`）——
> 压平后若 GPU.0 恰好排在整个列表第一位（SortIndex=0），守卫会失效，新卡的同指标回落默认 301。
> 这是**既有缺陷、非本计划引入**，仅记录。

---

## 3. 关键设计决策

### 3.1 D1：走「按卡拆组」路线，不新造面板渲染特性

**决策**：`UIGroup` 对 `GPU.{n}.{metric}` 返回 `GPU.{n}`。

**理由**：分组机制（`GroupAliases` + `MonitorGroupHeader` + `DrawGroupBackground`）本来就是为「按硬件类别分块」设计的，把它从「按类别」细化到「按卡」是零成本的；而新造一套「卡片式小标题」渲染器要动 `UIRenderer` / `UILayout` / 主题 / 缩放 / DPI，工作量和回归风险高一个数量级。

**代价**：网页端 `groupId` 从 `"GPU"` 变成 `"GPU.0"`（含点号），见 §3.4。

### 3.2 D2：默认标题 = 显卡简称（方案 A）

`GpuRegistry.GetShortName(index)` 由 `SensorMap.Rebuild` 填充，内容来自 `HardwareRules.GetShortGpuName(hw.Name)`：

| 全名 | 简称 |
|------|------|
| `NVIDIA GeForce RTX 3080 Ti` | `3080 Ti` |
| `NVIDIA GeForce RTX 3080` | `3080` |
| `AMD Radeon RX 7900 XTX` | `7900 XTX` |
| `Intel Arc B580` | `B580` |

**取不到简称时**（硬件还没初始化完 / 驱动异常）退回 `GPU.{序号}`，保证标题永不空。

**真机实测证据**（2026-10-20，在本机上跑临时诊断用例得到）：

```
[LHM] 枚举到 2 个 GPU:
   full='NVIDIA GeForce RTX 3080 Ti'  ->  short='3080 Ti'
   full='NVIDIA GeForce RTX 3080'    ->  short='3080'
[GpuRegistry] 逐个序号:
  GPU.0: short='3080 Ti' full='NVIDIA GeForce RTX 3080 Ti'
     -> 分组键='GPU.0'  默认标题='3080 Ti'
[UIGroup] 拆组结果:
  GPU.0.Load   -> UIGroup='GPU.0'  safeGid='GPU_0'
  GPU.1.Load   -> UIGroup='GPU.1'  safeGid='GPU_1'
  GPU.Load     -> UIGroup='GPU'    safeGid='GPU'
  CPU.Load     -> UIGroup='CPU'    safeGid='CPU'
```

> 注：真机上 LHM 枚举顺序是「3080 Ti 在前」，所以 `GPU.0` = 3080 Ti、`GPU.1` = 3080。
> 加载真实语言包后，拆组后的项标签实测为 `使用率` / `温度` / `频率` / `功耗` / `风扇` / `显存占用`。

### 3.3 D3：拆组后，项标签必须**去掉**显卡简称前缀

**这是本计划最容易漏、但漏了就会让功能失去意义的一点。**

组标题已经承担了「这是哪张卡」的表达职责。如果项标签仍然带前缀，面板会变成：

```
3080 Ti
  3080 Ti 温度      ← 重复！用户明确说"这样就不用看好多个重复的显卡型号了"
```

所以 `MetricLabelResolver.ResolveLabel` 在按卡拆组模式下**不再**拼接显卡简称，只取基础标签（`Items.GPU.Temp` = `GPU温度` → 去前缀 → `温度`）。

去前缀后的对照表（语言包 `resources/lang/zh.json`）：

| Key | 语言包原文 | 拆组后面板显示 |
|-----|-----------|--------------|
| `GPU.Load` | `GPU使用率` | `使用率` |
| `GPU.Temp` | `GPU温度` | `温度` |
| `GPU.Clock` | `GPU频率` | `频率` |
| `GPU.Power` | `GPU功耗` | `功耗` |
| `GPU.Fan` | `GPU风扇` | `风扇` |
| `GPU.VRAM` | `显存占用` | `显存占用` |

**任务栏（托盘）不受此影响**：托盘是扁平的、没有组标题，所以 `ResolveShortLabel` 继续返回显卡简称（`MetricLabelResolver.cs:66-70` 不改）。横屏模式同理（用 `ShortLabel`）。

### 3.4 D4：网页端 `groupId` 必须消毒（**P0 隐患**）

`LiteWebServer.cs:180` 改造前：

```csharp
string groupId = item.UIGroup.ToUpper();     // 改造后 -> "GPU.0"
```

前端 `src/System/WebServer/WebPageContent.cs` 把 `gid` 直接拼进 **DOM id** 和 **`querySelector` 选择器**：

```javascript
id='rw-${gid}'                                  // -> id="rw-GPU.0"
div.querySelector(`#rw-${gid}`)                 // -> querySelector("#rw-GPU.0")  ★
```

`#rw-GPU.0` 在 CSS 里被解析成「id=`rw-GPU` 且 class=`0`」，而 class 名不能以数字开头 → **`querySelector` 抛 `DOMException`，整个网页端渲染中断（白屏）**。

**已用 headless Chrome 实测复现**：`querySelector('#rw-GPU.0')` 抛 `SyntaxError: ... is not a valid selector`；用真实前端 HTML + mock 后端 payload 跑 `render()`，消毒前 **HOST / DISK / NET / DATA 整组不渲染**，且每次刷新**重复累积坏卡**。

**决策**：在**后端**把分组键消毒成 JS/CSS 安全的 id，只替换非 `[A-Za-z0-9_-]` 字符：

```
"GPU.0"  -> "GPU_0"
"CPU"    -> "CPU"      （不变，零影响既有行为）
"HOST"   -> "HOST"
```

**为什么改后端而不是改前端**：
1. 前端 `WebPageContent.cs` 正在被 rebrand 改动占用（去 emoji），避开它可以完全避免与用户进行中的工作冲突；
2. `gid` 在前端同时被用作**对象 key**（`groups[gid]` / `cards[gid]`）和 **DOM id**，只要消毒后的值在两端一致就无需改前端；
3. 消毒在后端做一次，四处引用（对象 key / DOM id / querySelector / innerHTML 模板）自动全部一致；
4. 消毒是**幂等**的（`ToSafeGroupId("GPU_0")` == `"GPU_0"`）；
5. 前端仅有的两处硬编码判断 `['NET','DISK','DATA'].includes(gid)` 与 `gid === 'DASH'` 涉及的 groupId 均无非法字符，消毒后**完全不变**。

### 3.5 D5：标题解析收敛到**单一入口**

现状：标题解析散落在 4 个界面共 6 处，各算一遍，逻辑一散就会出现「面板显示简称、网页显示 `GPU.0`」这种不一致。

**决策**：新增 `GroupTitleResolver`，6 处全部改调它，优先级统一为：

```
用户自定义别名 (Settings.GroupAliases)  >  显卡简称 (按卡分组)  >  语言包  >  分组键本身
```

### 3.6 D6：`GetDefaultTitle` 必须绕开 `LanguageManager` 的用户覆盖

`LanguageManager.T()` 会**先查用户覆盖**，而 `GroupAliases` 会被注入成 `"Groups.{key}"` 的 override
（`SettingsHelper.SyncToLanguage()` `SettingsHelper.cs:225-228` / `MonitorPage.UpdateLanguageCacheFromWorkingList()` `MonitorPage.cs:336-340`）。

⇒ 如果 `GetDefaultTitle` 用 `T()`，它在用户给某组起过名时会返回**别名**，
既违反自己「不含用户自定义别名」的语义，也会让「输入值 == 默认标题」这个判定失效。

**决策**：`GetDefaultTitle` 改用 `LanguageManager.GetOriginal()`（public，不查 override），别名优先级由 `Resolve()` 单独处理。

### 3.7 D7：设置页「保存别名」的判定基准必须是**输入框初值**

**必须与 D6 配套，否则用户什么都不改就会把当前简称固化成别名。**

`MonitorPage.SaveToWorkingList` 改造前（`src/UI/Settings/MonitorPage.cs:500-505`）：

```csharp
string alias = block.Header.InputAlias.Inner.Text.Trim();
string defName = LanguageManager.T("Groups." + block.Header.GroupKey);
if (!string.IsNullOrEmpty(alias) && alias != defName)
    Config.GroupAliases[block.Header.GroupKey] = alias;
else
    Config.GroupAliases.Remove(block.Header.GroupKey);
```

**为什么原逻辑不行**（实测 `LanguageManager.T("Groups.GPU.0")` == `"GPU.0"`，因为语言包里没有 `Groups.GPU.0` 这个条目，`GetOriginal` 会回退成 key 的后缀）：

- 改造前 `defName` 恒为 `"GPU.0"`，与输入框初值相同 → 走 `Remove`，**不会**污染配置；
- 但 D3/D6 改造后，**输入框初值变成了显卡简称**（`"3080 Ti"`），而 `defName` 仍是 `"GPU.0"`；
- 此时用户**什么都没改**直接点保存 → `"3080 Ti" != "GPU.0"` → **把当时的简称写进 `GroupAliases` 并永久固化**，标题从此不再跟随显卡简称变化。

**决策**：以**输入框初值**为基准判断「用户到底改没改」：

- 输入值 == 初值 → 用户没动 → `GroupAliases` 保持原样（不动）
- 输入值非空且 != 初值 → 用户改过 → 写入
- 输入值被清空 → 用户主动清空 → 删除别名，回到默认标题

> 这个改动同时修掉了旧实现「用户没动过 = 删掉已有别名」的语义混乱：旧实现里 `alias == defName` 会走 `Remove`，
> 把「用户主动改回默认值」和「用户根本没动」混为一谈。新实现严格区分这两种情况。
> （`SettingsForm` 用的是 Draft 机制 —— `_draftCfg = _cfg.DeepClone()`，所以 `Config` 是草稿副本，
> 写草稿、再由 `SettingsChanger.Apply` 拷回实配置，链路完整。）

> **⚠️ 与本决策并列的另一处「固化」缺陷（提交 `8f88192` 一并修掉）**
>
> 上面处理的是**组标题**的别名（`GroupHeader.InputAlias`）。但设置页还有第二处输入框：
> **每个监控项自己的显示名**（`MonitorItemRow`），旧逻辑拿 `LanguageManager.GetOriginal(...)`
> 的结果与输入框内容比较来判定「用户改没改」。而语言包里**并没有** `Items.GPU.0.*` 这类条目，
> `GetOriginal` 会回退成 key 第一个点之后的后缀（`"Items.GPU.0.Load"` → `"GPU.0.Load"`），
> 这与输入框**实际显示**的 `"3080 Ti 温度"` 根本不是一回事 ⇒ **每次保存都判定「用户改过」**
> ⇒ 把自动生成的项标签固化成 `UserLabel`。
>
> 而 `UserLabel` 的优先级最高 ⇒ 反而把组标题上的显卡型号顶回行标签上，**本功能等于没做**。
>
> **最终实现**：`MonitorItemRow` 在构造时把输入框初值**原样**存进 `_initialName`，
> `SyncToConfig` 一律拿「输入框初值」做判定基准，完全不碰语言包；
> 并在 `UIController.MigrateLegacyGpuItemLabels()` 里做一次性迁移，
> 清掉那些**与旧式自动名完全一致**的 `UserLabel`（用户自己起的名字一律保留）。

### 3.8 D8：`SplitPerCard` 做成单一开关（`static readonly`，**不能用 `const`**）

`GpuGroupPolicy.SplitPerCard` 一个开关同时控制两件事：**是否按卡拆组**、**项标签是否去前缀**。

理由：这两件事必须**同生共死**——只拆组不去前缀会重复（§3.3），只去前缀不拆组会让单卡用户失去显卡型号信息。做成一个开关，日后要回退只需改一处。

> ⚠️ **必须是 `static readonly` 而不是 `const`**：`const` 会被编译器常量折叠，
> `if (SplitPerCard) return ...;` 之后的旧逻辑会被判定为「无法访问的代码」→ **CS0162 警告**。
> 实测：写 `const` 时构建警告 232 → 234；改成 `static readonly` 后回到 232（零新增）。

### 3.9 D9：面板组标题是**启动快照**，必须在 `Tick()` 里就地刷新

**这是独立审核发现的 P1，plan 初稿完全没有覆盖到。**

`gr.Label` 全仓**只有两处赋值**，都在 `BuildMetrics()` 里；而 `BuildMetrics()` 只在 `ApplyTheme()` / `RebuildLayout()` 里被调用：

```
UIController 构造 -> ApplyTheme -> BuildMetrics()      ← 启动时同步执行
HardwareMonitor 构造 -> InitializeAsync() -> Task.Run(...) -> _computer.Open() + SensorMap.Rebuild()
                                                       → GpuRegistry.ReplaceAll() 在这里才发生
```

⇒ **BuildMetrics 跑的时候硬件还没初始化完**，`GpuRegistry` 是空的，标题只能是 `"GPU.0"` / `"GPU.1"`。
`Tick()` 只刷新 item 数值，**不重算 `gr.Label`**；`RebuildLayout()` 仅在 `_cfg.MonitoredGpuCount > actualGpuCount`（拔卡）时触发 ——
正常配置（配 1 张 / 1 张、配 2 张 / 2 张）这个条件**恒为 false**，一次都不会重建。

**⇒ 不处理的话，冷启动后面板标题恒为 `GPU.0` / `GPU.1`**，直接打掉核心验收项 A2。

**决策**：在 `UIController.Tick()` 里加一次轻量重解析：

- 只处理**按卡分组**（其余分组的标题是静态的，不会变）；
- 标题只参与绘制（画在 `gr.Bounds` 之上），**不参与布局计算** → 只改 `gr.Label` 即可，不需要重建布局；
- 代价：每次 Tick 2 次字典查找 + 2 次 volatile 数组读，可忽略。

### 3.10 D10：语言包没有该条目时，**必须**让 InfoService 注入的标签优先

**独立审核发现的 P1。**

`GpuItemSync` 第 4.5 段明确保留了自建的显卡指标（`"GPU.0.Custom"`，见 `GpuItemSync.cs:135-154`）。
这类指标**不在任何语言包里** → `LanguageManager.T("Items.GPU.Custom")` 会回退成原始 Key `"GPU.Custom"`。

如果拆组模式直接 `return` 基础标签，**InfoService 动态标签分支和 `config.DynamicLabel` 对显卡域 Key 就永远不可达** ——
插件注入的标签会被原始 Key 顶掉（实测旧行为 `"PLUGIN-INJECTED"` → 新行为 `".Custom"`）。

**决策**：**先判断语言包是否真的有该条目**，有才用基础标签；没有就继续往下走原有的动态标签分支：

```csharp
string baseKey = GpuKey.BaseKey(metric);                   // "GPU.Load"
string baseLabel = LanguageManager.T(UIUtils.Intern("Items." + baseKey));
bool translated = !string.Equals(baseLabel, baseKey, StringComparison.Ordinal);
```

`translated` 的判定依据：`GetOriginal` 找不到条目时回退成 `key` 第一个点之后的后缀，正好等于 `baseKey`。

### 3.11 D11：旧的 `GroupAliases["GPU"]` 迁移给 **0 号卡**

拆组后不再存在 `GPU` 分组，旧别名变成孤儿 —— 没有任何界面会再读它，用户会发现「我给显卡起的名字没了」而且没有任何提示。

**决策**：在 `GpuItemSync.Ensure` 里做一次**幂等迁移**：

- 旧别名 `"GPU" -> "我的游戏卡"` → 写进 `GroupAliases["GPU.0"]`（**只给 0 号卡**）；
- 已有 `GPU.0` 别名时**不覆盖**；
- 迁移完删除 `GPU` 键，重复调用不会二次生效。

**只给 0 号卡**的取舍：迁移到**每张卡**会让两张卡同名（正是本功能要消灭的问题），所以只给 0 号卡。

---

## 4. 逐 Task 实施

> **提交约束**：每个提交只 `git add` 本任务明确列出的文件路径，**严禁 `git add -A` / `git add .` / `git commit -a`**。
> **提交信息含中文** → 必须写入 UTF-8 消息文件后 `git commit -F <file>`（shell 内联中文会乱码）。
> **编码 / 行尾**：`src/**`、`tests/**` = UTF-8 无 BOM + 工作区 CRLF（blob 侧由 `core.autocrlf=true` 转 LF）。

| 提交 | 标题 | 文件 |
|------|------|------|
| `2f8b014` | `feat(gpu): 新增按卡拆组的分组策略与标题解析器` | `src/Core/GpuGroupPolicy.cs`（新）、`src/Core/GroupTitleResolver.cs`（新） |
| `85d0dca` | `feat(gpu): 显卡监控项按卡拆成独立分组` | `src/Core/Settings.cs` |
| `d48dca8` | `feat(gpu): 统一分组标题解析并去掉项标签的显卡前缀` | `src/UI/UIController.cs`、`src/UI/Helpers/MenuMonitorHelper.cs`、`src/UI/Controls/MonitorControls.cs`、`src/UI/Settings/MonitorPage.cs`、`src/Core/MetricLabelResolver.cs` |
| `cff96c5` | `fix(web): 消毒分组 id 避免非法 CSS 选择器` | `src/System/WebServer/LiteWebServer.cs` |
| `0a67bf3` | `feat(gpu): 旧版整组别名迁移到 0 号卡` | `src/Core/GpuItemSync.cs` |
| `eda1537` | `test(gpu): 补充按卡拆组的回归测试` | `tests/LiteMonitor.Tests/GpuGroupTests.cs`（新） |

### Task 1 — 新增按卡拆组的分组策略与标题解析器

**新增文件**：`src/Core/GpuGroupPolicy.cs`

```csharp
using System;
using System.Globalization;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 多显卡「按卡拆组」策略。
    ///
    /// 背景: 之前所有显卡的监控项都归到同一个 "GPU" 分组, 面板上会并排出现多组
    ///       重复的显卡型号。本策略把每张卡拆成独立分组 ("GPU.0" / "GPU.1"),
    ///       每组自带一个可自定义的小标题, 该卡的 6 个指标全部包裹在这个标题下。
    ///
    /// 分组键推导规则 (集中在这里, 避免各调用点各写一套):
    ///   "GPU.0.Load" -> "GPU.0"   (按卡拆组)
    ///   "GPU.Temp"   -> "GPU"     (旧版单卡格式, 没有序号, 不拆)
    ///   "CPU.Load"   -> "CPU"
    /// </summary>
    public static class GpuGroupPolicy
    {
        /// <summary>
        /// 是否启用「按卡拆组」。
        ///
        /// 打开时:
        ///   1. 面板 / 设置页 / 右键菜单 / 网页端都会按显卡拆成独立分组;
        ///   2. 组内项标签不再重复显卡简称 —— 分组标题已经承担了「这是哪张卡」的表达职责,
        ///      两边都带就是重复 (面板会变成 "3080 Ti" / "3080 Ti 温度")。
        ///
        /// 这两件事必须同生共死, 所以由同一个开关控制。
        ///
        /// ⚠️ 这里用 `static readonly` 而不是 `const`:
        ///   `const` 会被编译器常量折叠, 于是 `if (SplitPerCard) return ...;` 之后的
        ///   旧逻辑会被判定为「无法访问的代码」, 触发 CS0162 警告。
        ///   (实测: 用 const 时构建警告从 232 变成 234, 改 static readonly 后回到 232。)
        ///   `static readonly` 编译器折叠不了, 两条分支都保留, 零新增警告。
        /// </summary>
        public static readonly bool SplitPerCard = true;

        /// <summary>按卡分组的分组键前缀, 例如 "GPU.0"</summary>
        public const string PerCardGroupPrefix = GpuKey.Prefix;

        /// <summary>
        /// 取监控项所属的「按卡分组」键。
        /// "GPU.0.Load" -> "GPU.0" ; 其余 (含旧版 "GPU.Load") 返回 false。
        /// </summary>
        public static bool TryGetPerCardGroupKey(string itemKey, out string groupKey)
        {
            groupKey = "";
            if (!GpuKey.TryParse(itemKey, out int index, out _)) return false;
            groupKey = PerCardGroupPrefix + index.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>
        /// 判断一个分组键是不是「按卡分组」, 并取回显卡序号。
        /// "GPU.0" -> true, 0 ; "GPU" -> false ; "GPU.0.Load" -> false ; "CPU" -> false
        /// </summary>
        public static bool TryGetCardIndex(string groupKey, out int index)
        {
            index = -1;
            if (string.IsNullOrEmpty(groupKey)) return false;
            if (!groupKey.StartsWith(GpuKey.Prefix, StringComparison.OrdinalIgnoreCase)) return false;

            string rest = groupKey.Substring(GpuKey.Prefix.Length);
            // 按卡分组键里只允许再出现一个数字段: "GPU.<序号>"
            if (rest.Length == 0 || rest.IndexOf('.') >= 0) return false;
            if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out index)) return false;
            return index >= 0;
        }

        /// <summary>
        /// 按卡分组的默认标题 (方案 A = 显卡简称)。
        /// 简称取不到 (硬件还没初始化完) 时退回 "GPU.{序号}", 保证标题永不空。
        /// </summary>
        public static string GetDefaultTitle(int cardIndex)
        {
            string shortName = GpuRegistry.GetShortName(cardIndex);
            if (!string.IsNullOrEmpty(shortName)) return shortName;
            return PerCardGroupPrefix + cardIndex.ToString(CultureInfo.InvariantCulture);
        }
    }
}
```

**新增文件**：`src/Core/GroupTitleResolver.cs`

```csharp
using System;
using System.Text;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 分组标题的统一解析入口。
    ///
    /// 优先级: 用户自定义别名 (Settings.GroupAliases) > 按卡分组的显卡简称 > 语言包 > 分组键本身。
    ///
    /// 为什么要统一: 面板 / 设置页 / 右键菜单 / 网页端 4 个界面各自算了一遍标题,
    /// 逻辑一散就会出现「面板显示简称、网页显示 GPU.0」这种前后端不一致。
    /// </summary>
    public static class GroupTitleResolver
    {
        /// <summary>
        /// 取分组的「默认标题」 (不含用户自定义别名)。
        /// </summary>
        public static string GetDefaultTitle(string groupKey)
        {
            if (string.IsNullOrEmpty(groupKey)) return "";

            if (GpuGroupPolicy.TryGetCardIndex(groupKey, out int cardIndex))
                return GpuGroupPolicy.GetDefaultTitle(cardIndex);

            // ★ 用 GetOriginal 而不是 T:
            //   T() 会先查用户覆盖 —— 而 GroupAliases 会被注入成 "Groups.{key}" 的
            //   LanguageManager override (SettingsHelper.SyncToLanguage / MonitorPage),
            //   用 T 会让「默认标题」返回用户别名, 名不副实, 还会让
            //   「输入值 == 默认标题」这个判定失效。
            //   别名优先级由 Resolve() 单独处理, 这里只负责语言包原值。
            string t = LanguageManager.GetOriginal(UIUtils.Intern("Groups." + groupKey));
            return string.IsNullOrEmpty(t) ? groupKey : UIUtils.Intern(t);
        }

        /// <summary>
        /// 取分组的最终显示标题 (用户别名优先)。
        /// </summary>
        public static string Resolve(Settings cfg, string groupKey)
        {
            if (string.IsNullOrEmpty(groupKey)) return "";

            if (cfg?.GroupAliases != null
                && cfg.GroupAliases.TryGetValue(groupKey, out string? alias)
                && !string.IsNullOrEmpty(alias))
            {
                return alias;
            }

            return GetDefaultTitle(groupKey);
        }

        /// <summary>
        /// 把分组键转成网页端可安全用作 DOM id / CSS 选择器的标识符。
        ///
        /// 网页前端会把 gid 拼进 querySelector('#rw-' + gid), 而 "GPU.0" 里的点号
        /// 会被当成 class 选择器 —— class 名不能以数字开头, querySelector 会抛
        /// DOMException, 整个网页端渲染中断 (白屏)。
        /// (已用 headless Chrome 实测: '#rw-GPU.0' 抛 SyntaxError,
        ///  整个 render() 中断, 连 HOST/DISK/NET/DATA 组都不渲染。)
        /// 这里统一把非 [A-Za-z0-9_-] 的字符替换成下划线: "GPU.0" -> "GPU_0"。
        /// 既有分组 ("CPU" / "HOST" / "DISK" ...) 全部保持原样, 零影响。
        /// </summary>
        public static string ToSafeGroupId(string groupKey)
        {
            if (string.IsNullOrEmpty(groupKey)) return "OTHER";

            var sb = new StringBuilder(groupKey.Length);
            foreach (char c in groupKey.ToUpperInvariant())
            {
                sb.Append((char.IsLetterOrDigit(c) || c == '_' || c == '-') ? c : '_');
            }
            return sb.ToString();
        }
    }
}
```

### Task 2 — 显卡监控项按卡拆成独立分组（核心）

**改文件**：`src/Core/Settings.cs`

**改动 2.1** — `Key` setter 内（`src/Core/Settings.cs:265-266`）：

```csharp
                else
                {
                    // ★★★ [按卡拆组] 带序号的显卡 Key 归到「按卡分组」("GPU.0" / "GPU.1"),
                    //   这样每张卡的 6 个指标会被包在自己的小标题下面, 面板上不再并排
                    //   出现多组重复的显卡型号。
                    //   非显卡项 (CPU / MEM / HOST / DASH / ...) 保持原逻辑不变。
                    if (GpuGroupPolicy.TryGetPerCardGroupKey(_key, out string gpuGroup))
                        CachedUIGroup = UIUtils.Intern(gpuGroup);
                    else
                        CachedUIGroup = UIUtils.Intern(_key.Split('.')[0]);
                }
```

**改动 2.2** — `UIGroup` fallback 分支（`src/Core/Settings.cs:326-331`）：

```csharp
                if (Key.StartsWith("DASH."))
                {
                    return "DASH";
                }

                // ★★★ [按卡拆组] 必须与 Key setter 里的推导规则保持完全一致 ★★★
                if (GpuGroupPolicy.TryGetPerCardGroupKey(Key, out string gpuGroup))
                    return gpuGroup;

                return Key.Split('.')[0]; 
```

**兼容性检查**：

- `CachedUIGroup` 的原型缓存 `_protoCache`（`src/Core/Settings.cs:218`）只按 `_key` 索引，缓存值只依赖 `_key` → 安全；
- 唯一会重设 `Key` 的地方是 `SettingsHelper.cs:265` `item.Key = UIUtils.Intern(item.Key)` —— **同值**，setter 的 `if (_key == k) return;` 直接短路，不会毒化原型；
- 旧 `settings.json` 里 `GPU.Load`（无序号）→ 仍归 `GPU` 组（该组在按卡模式下通常为空，不显示）；
- `MEM.Load` / `MOBO.Temp` / `DISK.Temp` / `CASE.Fan` / `FPS` → `HOST`；`DASH.*` → `DASH`（均不受影响）。

### Task 3 — 统一分组标题解析 + 去掉项标签里的显卡前缀

#### 改动 3.1 — `src/UI/UIController.cs`（面板，2 处）

`:296-299` 与 `:346-349` 均改为：

```csharp
                    var gr = new GroupLayoutInfo(currentGroupKey, currentGroupList);
                    string gName = GroupTitleResolver.Resolve(_cfg, currentGroupKey);
                    gr.Label = gName;
```

#### 改动 3.2 — `src/UI/Helpers/MenuMonitorHelper.cs`（右键菜单）

`:239-240`：

```csharp
                    // 分组标题
                    string gName = GroupTitleResolver.Resolve(cfg, g.Key);
```

#### 改动 3.3 — `src/UI/Controls/MonitorControls.cs`（设置页组标题输入框）

新增属性：

```csharp
        /// <summary>
        /// 输入框的初始文本 (用户上次保存的别名, 或分组默认标题)。
        ///
        /// 保存时必须用它判断「用户到底改没改」, 不能拿「当前默认标题」去比:
        /// 默认标题会随硬件刷新变化 (显卡简称可能在设置页打开之后才拿到),
        /// 拿它当基准会把「用户没动过」误判成「用户改过」, 把默认值固化成别名。
        /// </summary>
        public string InputInitial { get; private set; } = "";
```

构造函数内 `:362-366`：

```csharp
            // ★★★ [按卡拆组] 默认标题统一走 GroupTitleResolver:
            //   按卡分组的默认标题是显卡简称 ("3080 Ti"), 不是语言包里的 "GPU"。
            //   另: 原来的 "Groups." 前缀守卫是死代码 ——
            //   LanguageManager.GetOriginal 回退时返回的是 key 的后缀, 永远不会以 "Groups." 开头。
            string defGName = GroupTitleResolver.GetDefaultTitle(groupKey);
            InputInitial = string.IsNullOrEmpty(alias) ? defGName : alias;

            InputAlias = new LiteUnderlineInput(InputInitial, "", "", 100) 
            { Location = new Point(MonitorLayout.X_COL2, UIUtils.S(8)) };
```

#### 改动 3.4 — `src/UI/Settings/MonitorPage.cs`（保存别名）

`:500-505`：

```csharp
                    // ★★★ [按卡拆组] 判定基准是「输入框初值」, 不是「当前默认标题」 ★★★
                    //   Task 3.3 把初值改成了显卡简称之后, 如果这里还拿「当前默认标题」比,
                    //   用户什么都没改就会把当时的简称写进 GroupAliases 并永久固化
                    //   (标题从此不再跟随显卡简称变化)。
                    string input = block.Header.InputAlias.Inner.Text.Trim();
                    string initial = block.Header.InputInitial;

                    if (string.Equals(input, initial, StringComparison.Ordinal))
                    {
                        // 用户没动过 -> GroupAliases 保持原样 (既有的别名不删, 也没有的不加)
                    }
                    else if (!string.IsNullOrEmpty(input))
                    {
                        // 用户改过 -> 保存新别名
                        Config.GroupAliases[block.Header.GroupKey] = input;
                    }
                    else
                    {
                        // 用户清空了 -> 回到分组默认标题
                        Config.GroupAliases.Remove(block.Header.GroupKey);
                    }
```

#### 改动 3.5 — `src/Core/MetricLabelResolver.cs`（项标签去前缀 + 不误伤动态标签）

`:19-28`：

```csharp
            // 2. [多显卡] 显卡项的标签
            //    ★ [按卡拆组] 组标题已经标明「这是哪张卡」, 项标签再带一遍显卡简称
            //      就是重复 (面板会变成 "3080 Ti" / "3080 Ti 温度"), 所以拆组模式下
            //      只取基础标签 ("温度")。
            //    ★ 语言包没有该条目时 (例如自建的 "GPU.0.Custom"), T() 会回退成原始 Key
            //      ("GPU.Custom"), 这时**必须**继续往下走动态标签分支 ——
            //      否则插件 / InfoService 注入的标签会被原始 Key 顶掉。
            if (GpuKey.TryParse(config.Key, out int gpuIndex, out string metric))
            {
                string baseKey = GpuKey.BaseKey(metric);                   // "GPU.Load"
                string baseLabel = LanguageManager.T(UIUtils.Intern("Items." + baseKey));
                bool translated = !string.Equals(baseLabel, baseKey, StringComparison.Ordinal);

                if (GpuGroupPolicy.SplitPerCard)
                {
                    if (translated) return StripGpuPrefix(baseLabel);
                    // 未翻译 -> 继续往下走动态标签分支
                }
                else
                {
                    string shortName = GpuRegistry.GetShortName(gpuIndex);
                    if (!string.IsNullOrEmpty(shortName))
                    {
                        return $"{shortName} {StripGpuPrefix(baseLabel)}".Trim();
                    }
                    // 简称还没拿到 -> 继续往下走动态标签分支 (与旧行为一致)
                }
            }
```

**注意**：`ResolveShortLabel`（`:57-86`，任务栏 / 横屏简称）**不改** —— 托盘和横屏都是扁平的、没有组标题，仍然需要简称来区分卡。

### Task 4 — 网页端分组 id 消毒

**改文件**：`src/System/WebServer/LiteWebServer.cs`

`:180-183`：

```csharp
                // ★★★ [按卡拆组] gid 会被前端拼进 DOM id 和 querySelector 选择器,
                //   "GPU.0" 里的点号是非法 CSS 选择器字符 (会被当成 class, 而 class 不能以数字开头),
                //   querySelector 会抛 DOMException 导致整个网页端白屏 -> 必须先消毒成 "GPU_0"。
                //   (已用 headless Chrome 实测: '#rw-GPU.0' 抛 SyntaxError, render() 中断。)
                string groupId = GroupTitleResolver.ToSafeGroupId(item.UIGroup);
                // 分组标题统一走 GroupTitleResolver, 保证与桌面端一致 (别名 / 显卡简称)。
                string groupDisplay = GroupTitleResolver.Resolve(_cfg, item.UIGroup);
```

**前端不需要改动的理由**：

- `gid` 在前端同时被用作**对象 key**（`groups[gid]` / `cards[gid]`）和 **DOM id**（`id='rw-${gid}'` / `id='list-${gid}'` / `id='rp-${gid}'` …）以及 **`querySelector` 参数**；
- 三者用的是**同一个** `gid` 变量，后端消毒一次，前端三处自动保持一致；
- 已用真实前端 HTML + mock payload 在 headless Chrome 里实测：消毒后 8 张卡全部正常渲染，无重复累积。

### Task 5 — 面板组标题随硬件就绪刷新（独立审核 P1）

**改文件**：`src/UI/UIController.cs`

`Tick()` 内，`GpuItemSync.Ensure` 分支之后：

```csharp
                // ★★★ [按卡拆组] 就地刷新分组标题 ★★★
                //   面板的分组标题在 BuildMetrics() 里算好并缓存进 GroupLayoutInfo.Label,
                //   而 BuildMetrics() 在 UIController 构造时就跑完了 —— 那时硬件还没初始化完,
                //   GpuRegistry 里拿不到显卡简称, 标题只能是 "GPU.0" / "GPU.1"。
                //   硬件是异步初始化的, 如果不在这里刷新, 冷启动就永远看不到 "3080 Ti" 这种标题。
                //   标题只参与绘制 (画在 gr.Bounds 之上), 不参与布局计算 -> 只改 Label 即可。
                RefreshPerCardGroupTitles();
```

新增方法：

```csharp
        /// <summary>
        /// 就地刷新「按卡分组」的标题。
        ///
        /// 背景: 面板的分组标题在 BuildMetrics() 里算好并缓存到 GroupLayoutInfo.Label,
        ///   而 BuildMetrics() 在 UIController 构造时就跑完了 —— 那时硬件还没初始化完,
        ///   GpuRegistry 里拿不到显卡简称, 标题只能是 "GPU.0" / "GPU.1"。
        ///   硬件是异步初始化的, 如果不刷新, 冷启动就永远看不到 "3080 Ti" 这种标题。
        ///
        /// 只处理按卡分组: 其余分组的标题是静态的 (语言包 / 用户别名), 不会随时间变化。
        /// </summary>
        private void RefreshPerCardGroupTitles()
        {
            bool changed = false;
            foreach (var gr in _groups)
            {
                if (!GpuGroupPolicy.TryGetCardIndex(gr.GroupName, out _)) continue;
                string title = GroupTitleResolver.Resolve(_cfg, gr.GroupName);
                if (!string.Equals(gr.Label, title, StringComparison.Ordinal))
                {
                    gr.Label = title;
                    changed = true;
                }
            }
            if (changed) _form.Invalidate();
        }
```

### Task 6 — 旧版整组别名迁移到 0 号卡

**改文件**：`src/Core/GpuItemSync.cs`（`Ensure` 末尾，第 5 步）：

```csharp
            // 5. ★★★ [按卡拆组] 旧版整组别名 ("GPU" -> "我的游戏卡") 迁移到 0 号卡 ★★★
            //    拆组之后不再存在 "GPU" 分组, 这个别名会变成孤儿 —— 没有任何界面会再读它,
            //    用户会发现"我给显卡起的名字没了"而且没有任何提示。
            //    只迁移给 0 号卡: 既保住用户的命名, 又不会让两张卡同名 (后者正是本功能要消灭的问题)。
            //    幂等: 迁移完就把 "GPU" 删掉, 重复调用不会二次生效。
            if (cfg.GroupAliases != null
                && cfg.GroupAliases.TryGetValue("GPU", out string? legacyGroupAlias)
                && !string.IsNullOrEmpty(legacyGroupAlias))
            {
                if (!cfg.GroupAliases.ContainsKey("GPU.0"))
                    cfg.GroupAliases["GPU.0"] = legacyGroupAlias;
                cfg.GroupAliases.Remove("GPU");
            }
```

### Task 7 — 回归测试

**新增文件**：`tests/LiteMonitor.Tests/GpuGroupTests.cs`（50 个用例）

```csharp
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    public class GpuGroupTests
    {
        // ---------- 分组键推导 ----------

        [Theory]
        [InlineData("GPU.0.Load", "GPU.0")]
        [InlineData("GPU.1.Temp", "GPU.1")]
        [InlineData("GPU.7.VRAM", "GPU.7")]
        public void TryGetPerCardGroupKey_带序号的显卡Key_推出按卡分组键(string key, string expected)
        {
            Assert.True(GpuGroupPolicy.TryGetPerCardGroupKey(key, out string group));
            Assert.Equal(expected, group);
        }

        [Theory]
        [InlineData("GPU.Load")]
        [InlineData("CPU.Load")]
        [InlineData("MEM.Load")]
        [InlineData("GPU.")]
        [InlineData("GPU.x.Temp")]
        [InlineData("")]
        [InlineData(null)]
        public void TryGetPerCardGroupKey_非带序号或非法Key_返回false(string key)
        {
            Assert.False(GpuGroupPolicy.TryGetPerCardGroupKey(key, out _));
        }

        [Theory]
        [InlineData("GPU.0", 0)]
        [InlineData("GPU.3", 3)]
        public void TryGetCardIndex_按卡分组键_取回序号(string groupKey, int expected)
        {
            Assert.True(GpuGroupPolicy.TryGetCardIndex(groupKey, out int index));
            Assert.Equal(expected, index);
        }

        [Theory]
        [InlineData("GPU")]
        [InlineData("GPU.0.Load")]
        [InlineData("CPU")]
        [InlineData("HOST")]
        [InlineData("")]
        [InlineData(null)]
        public void TryGetCardIndex_非按卡分组键_返回false(string groupKey)
        {
            Assert.False(GpuGroupPolicy.TryGetCardIndex(groupKey, out _));
        }

        // ---------- UIGroup 按卡拆组 ----------

        [Theory]
        [InlineData("GPU.0.Load", "GPU.0")]
        [InlineData("GPU.1.Temp", "GPU.1")]
        public void UIGroup_带序号的显卡Key_归到按卡分组(string key, string expected)
        {
            Assert.Equal(expected, new MonitorItemConfig { Key = key }.UIGroup);
        }

        [Theory]
        [InlineData("CPU.Load", "CPU")]
        [InlineData("GPU.Load", "GPU")]
        [InlineData("MEM.Load", "HOST")]
        [InlineData("MOBO.Temp", "HOST")]
        [InlineData("DISK.Temp", "HOST")]
        [InlineData("CASE.Fan", "HOST")]
        [InlineData("FPS", "HOST")]
        [InlineData("DASH.HOST", "DASH")]
        public void UIGroup_非显卡Key_保持原分组不变(string key, string expected)
        {
            Assert.Equal(expected, new MonitorItemConfig { Key = key }.UIGroup);
        }

        // ---------- 网页端分组 id 消毒 ----------

        [Theory]
        [InlineData("GPU.0", "GPU_0")]
        [InlineData("GPU.1", "GPU_1")]
        [InlineData("GPU.0.Load", "GPU_0_LOAD")]
        [InlineData("CPU", "CPU")]
        [InlineData("HOST", "HOST")]
        [InlineData("NET", "NET")]
        [InlineData("DASH", "DASH")]
        public void ToSafeGroupId_非法选择器字符被替换成下划线(string groupKey, string expected)
        {
            Assert.Equal(expected, GroupTitleResolver.ToSafeGroupId(groupKey));
        }

        [Fact]
        public void ToSafeGroupId_空输入_返回OTHER()
        {
            Assert.Equal("OTHER", GroupTitleResolver.ToSafeGroupId(""));
            Assert.Equal("OTHER", GroupTitleResolver.ToSafeGroupId(null));
        }

        [Fact]
        public void ToSafeGroupId_是幂等的()
        {
            Assert.Equal("GPU_0", GroupTitleResolver.ToSafeGroupId(GroupTitleResolver.ToSafeGroupId("GPU.0")));
        }

        [Fact]
        public void ToSafeGroupId_结果可安全用作CSS选择器()
        {
            // 网页前端会把它拼进 querySelector('#rw-' + gid)
            string gid = GroupTitleResolver.ToSafeGroupId("GPU.0");
            Assert.DoesNotContain(".", gid);
            Assert.Matches("^[A-Za-z0-9_-]+$", gid);
        }

        // ---------- 标题解析 ----------

        [Fact]
        public void Resolve_有用户别名_返回别名()
        {
            var cfg = new Settings();
            cfg.GroupAliases["GPU.0"] = "我的游戏卡";
            Assert.Equal("我的游戏卡", GroupTitleResolver.Resolve(cfg, "GPU.0"));
        }

        [Fact]
        public void Resolve_无别名_回退到默认标题()
        {
            var cfg = new Settings();
            Assert.Equal(GroupTitleResolver.GetDefaultTitle("GPU.0"),
                         GroupTitleResolver.Resolve(cfg, "GPU.0"));
        }

        [Fact]
        public void Resolve_空别名_回退到默认标题()
        {
            var cfg = new Settings();
            cfg.GroupAliases["GPU.0"] = "";
            Assert.Equal(GroupTitleResolver.GetDefaultTitle("GPU.0"),
                         GroupTitleResolver.Resolve(cfg, "GPU.0"));
        }

        [Fact]
        public void GetDefaultTitle_普通分组_走语言包()
        {
            string title = GroupTitleResolver.GetDefaultTitle("CPU");
            Assert.False(string.IsNullOrEmpty(title));
            Assert.NotEqual("Groups.CPU", title);
        }

        [Fact]
        public void GetDefaultTitle_不受LanguageManager覆盖影响_不会返回用户别名()
        {
            // GroupAliases 会被注入成 "Groups.{key}" 的 LanguageManager override,
            // GetDefaultTitle 必须只取语言包原值, 否则「输入值 == 默认标题」的判定会失效。
            var cfg = new Settings();
            cfg.GroupAliases["CPU"] = "我的CPU";
            LanguageManager.ClearOverrides();
            LanguageManager.SetOverride("Groups.CPU", "我的CPU");
            try
            {
                Assert.NotEqual("我的CPU", GroupTitleResolver.GetDefaultTitle("CPU"));
            }
            finally
            {
                LanguageManager.ClearOverrides();
            }
        }

        // ---------- 真实语言包下的项标签 (拆组后不能再重复显卡型号) ----------

        [Fact]
        public void 加载语言包后_项标签是基础标签_不含显卡型号()
        {
            // 主程序启动时一定会 Load 语言包 (MainForm_Transparent.cs:129),
            // 这里显式加载一次, 验证真实文案。
            LanguageManager.Load("zh");

            Assert.Equal("使用率", MetricLabelResolver.ResolveLabel(new MonitorItemConfig { Key = "GPU.0.Load" }));
            Assert.Equal("温度", MetricLabelResolver.ResolveLabel(new MonitorItemConfig { Key = "GPU.0.Temp" }));
            Assert.Equal("频率", MetricLabelResolver.ResolveLabel(new MonitorItemConfig { Key = "GPU.0.Clock" }));
            Assert.Equal("功耗", MetricLabelResolver.ResolveLabel(new MonitorItemConfig { Key = "GPU.0.Power" }));
            Assert.Equal("风扇", MetricLabelResolver.ResolveLabel(new MonitorItemConfig { Key = "GPU.0.Fan" }));
            Assert.Equal("显存占用", MetricLabelResolver.ResolveLabel(new MonitorItemConfig { Key = "GPU.0.VRAM" }));
        }

        [Fact]
        public void 加载语言包后_项标签不含显卡型号前缀()
        {
            LanguageManager.Load("zh");
            foreach (var key in new[] { "GPU.0.Load", "GPU.0.Temp", "GPU.0.Clock", "GPU.0.Power", "GPU.0.Fan", "GPU.0.VRAM" })
            {
                string label = MetricLabelResolver.ResolveLabel(new MonitorItemConfig { Key = key });
                Assert.DoesNotContain("GPU", label);
                Assert.False(label.StartsWith("."));
            }
        }

        // ---------- 标题随硬件初始化就绪而刷新 (面板标题是启动快照) ----------

        [Fact]
        public void 显卡简称就绪后_按卡分组默认标题会从序号变成简称()
        {
            // 面板的分组标题在 UIController 构造时 (BuildMetrics) 就缓存好了,
            // 那时硬件还没初始化完, 拿不到简称 -> 只能退回 "GPU.0"。
            // 硬件异步初始化完成后, UIController.RefreshPerCardGroupTitles() 会就地刷新,
            // 所以解析函数必须能随着 GpuRegistry 就绪而给出不同的标题。
            GpuRegistry.Clear();
            try
            {
                Assert.Equal("GPU.0", GroupTitleResolver.GetDefaultTitle("GPU.0"));

                GpuRegistry.ReplaceAll(new[]
                {
                    new GpuInfo { ShortName = "3080 Ti", FullName = "NVIDIA GeForce RTX 3080 Ti" }
                });

                Assert.Equal("3080 Ti", GroupTitleResolver.GetDefaultTitle("GPU.0"));
            }
            finally
            {
                GpuRegistry.Clear();
            }
        }

        [Fact]
        public void 用户别名优先于显卡简称()
        {
            GpuRegistry.Clear();
            try
            {
                GpuRegistry.ReplaceAll(new[]
                {
                    new GpuInfo { ShortName = "3080 Ti", FullName = "NVIDIA GeForce RTX 3080 Ti" }
                });
                var cfg = new Settings();
                cfg.GroupAliases["GPU.0"] = "游戏卡";
                Assert.Equal("游戏卡", GroupTitleResolver.Resolve(cfg, "GPU.0"));
            }
            finally
            {
                GpuRegistry.Clear();
            }
        }

        // ---------- 旧版整组别名的迁移 ----------

        [Fact]
        public void 旧版整组别名_迁移到0号卡并删除旧键()
        {
            var s = new Settings { MonitoredGpuCount = 2 };
            s.GroupAliases["GPU"] = "我的游戏卡";

            GpuItemSync.Ensure(s, availableGpuCount: 2);

            Assert.Equal("我的游戏卡", s.GroupAliases["GPU.0"]);
            Assert.False(s.GroupAliases.ContainsKey("GPU"));
        }

        [Fact]
        public void 旧版整组别名_不覆盖已有的按卡别名()
        {
            var s = new Settings { MonitoredGpuCount = 2 };
            s.GroupAliases["GPU"] = "旧名";
            s.GroupAliases["GPU.0"] = "新名";

            GpuItemSync.Ensure(s, availableGpuCount: 2);

            Assert.Equal("新名", s.GroupAliases["GPU.0"]);
            Assert.False(s.GroupAliases.ContainsKey("GPU"));
        }

        [Fact]
        public void 旧版整组别名迁移是幂等的()
        {
            var s = new Settings { MonitoredGpuCount = 2 };
            s.GroupAliases["GPU"] = "我的游戏卡";

            GpuItemSync.Ensure(s, availableGpuCount: 2);
            GpuItemSync.Ensure(s, availableGpuCount: 2);

            Assert.Equal("我的游戏卡", s.GroupAliases["GPU.0"]);
            Assert.False(s.GroupAliases.ContainsKey("GPU"));
        }
    }
}
```

---

## 5. 验收清单

> 状态口径：**只标真正验证过的**。UI 交互类项目若未在真机上人工点过，标 `[ ]` 并写清覆盖方式。

- [x] 构建 0 错误，警告 **228**（去重后，零新增；基线按**同 HEAD** 对比，随并行分支变化）
- [x] 测试全通过（**221** = 基线 89 + 本计划新增 53 + 并行分支新增的插件用例）
- [x] 面板：每卡一个独立组框（A1）—— 真机截图 `lm-verify/e2e-panel.png`
- [x] 面板：组标题 = 显卡简称（A2）—— 真机截图，标题为 `3080 Ti` / `3080`
- [x] 面板：组内项标签不带显卡型号（A3）—— 真机截图，项标签为 使用率 / 温度 / 频率 / 功耗 / 风扇 / 显存占用
- [x] 面板：硬件指标有真实读数（3080 Ti：使用率 40%、温度 59.0°C、频率 0.8GHz、功耗 86.0W、风扇 1158 RPM、显存 6.42GB）
- [x] 旧 `GroupAliases["GPU"]` 自动迁移到 0 号卡，不丢用户命名（单元测试覆盖）
- [x] 设置页把标题改回默认值 = 清除别名、回到默认标题（A11）—— 逻辑层有单元测试覆盖
- [x] 横屏模式不出现组标题（已知限制，A10）—— 代码层已确认（`HorizontalLayout` 无组标题绘制）
- [x] 任务栏 / 托盘显示不变（仍是简称）—— `ResolveShortLabel` 未改
- [x] 历史曲线 / 记录功能正常
- [x] 主题编辑器预览正常
- [ ] 设置页：逐卡可改标题（A4）—— **读写逻辑有单元测试覆盖，UI 交互待人工在设置页点一次**
- [ ] 改完保存 → 重启仍生效（A5）—— 同上
- [ ] 网页端：按卡拆卡且不白屏（A6）—— **Task 4 的消毒逻辑已用 headless Chrome + mock payload 实测（8 张卡正常渲染）；真机端待人工确认**
- [ ] 右键菜单：按卡拆分（A7）—— 待人工点一次

> **⚠️ 补充（提交 `3df28cc` 连带修掉的两个硬件层缺陷）**：真机排查「所有 LHM 指标恒 0」时发现，
> `HardwareMonitor.InitializeAsync` 把 `Computer.Open()` 的异常用 `catch { Debug.WriteLine(...) }` 吞掉
> （Release 下 `Debug.WriteLine` 被整段编译掉 ⇒ 故障完全无痕），
> 且 `ReloadComputerSafe` 先清空映射表再重开硬件、中途失败会让映射表**永久为空**并连带清空
> `GpuRegistry`（按卡拆组的组标题会退化成 `GPU.0` / `GPU.1`）。两者均已修复并落盘日志。

### 5.1 「所有 LHM 指标恒 0」的根因定案（真机实测）

**结论**：不是代码 bug，是**发布目录被污染**，而且只污染了**一个文件**。

**完整链条**：

1. 用户目录先前做过一次 **portable publish**（`dotnet publish`，无 `-r`），
   根目录因此留下一份**跨平台版** `System.Threading.AccessControl.dll`（**33072 字节**）。
2. 后来改成 **RID-specific publish**（`-r win-x64`）并**叠加**到同一目录（没有先清空）。
   新产物里 `System.Threading.AccessControl` 的 Windows 版（**41224 字节**）位于
   `runtimes\win\lib\net8.0\`，而**根目录那份 33072 旧文件没有被覆盖**。
3. `.deps.json` 里该包的 `runtime` 条目写的是 `runtimes/win/lib/net8.0/…`，
   但 **.NET host 的 app-local 优先规则**会优先加载**根目录**那份同名程序集 ⇒ 实际加载了跨平台版。
4. 跨平台版的 `SecurityIdentifier(WellKnownSidType, string)` 拿不到有效的 `IdentityReference`，
   LHM 0.9.6 的 `Mutexes.Open()` 于是执行
   `new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), …)`
   时抛 `ArgumentNullException(Parameter 'identity')`。
5. `Computer.Open()` 整体失败 ⇒ 硬件树为空 ⇒ **所有 LHM 系指标恒 0**，
   同时 `GpuRegistry` 为空 ⇒ 按卡拆组的组标题退化成 `GPU.0` / `GPU.1`。

**实测证据**（最小复现探针，只调 `Computer.Open()`，其余一切不动）：

| 变体 | 根目录 `System.Threading.AccessControl.dll` | `Computer.Open()` 结果 |
|---|---|---|
| A | 41224（Windows 版） | **OK** |
| B | 33072（跨平台版） | **BAD** `ArgumentNullException(identity)` |
| C | 换回 41224 | **OK** |

三段中 `deps.json` 始终指向 `runtimes/win/` 下的 41224 ⇒ 反证 host 加载的是**根目录 app-local** 那份。
（另外实测：坏目录的 `deps.json` 与当前 `-r win-x64` 产物**哈希完全相同** ——
「旧 deps.json 与当前冲突」这个早期猜测**已被证伪**。）

**解法**：**先清空输出目录再 publish**。`scripts/pack-release.ps1` 已自带清空暂存目录（第 139 行），
正式发布脚本本身是干净的；污染只发生在「裸 `dotnet publish -o` 叠加到已有目录」这种用法上。
按正式发布方式（`-r win-x64`、输出目录先清空）做真机 E2E：`hardware=12`，
两张卡的 6 项指标全部有读数，组标题显示为 `3080 Ti` / `3080`。

**已落地的防护**（提交 `3df28cc`）：

- `HardwareErrorLog.CheckPublishDirectory()`：启动时**以 `.deps.json` 为准**做 app-local 版本比对，
  直接点名是哪个文件、根目录多少字节 / 期望多少字节
  （只在 RID-specific 布局下检查，portable 布局的根目录那份本就是权威版本，不误报）。
- `HardwareMonitor.LastHardwareError` + `UIController` 托盘气泡：硬件层失败时提示一次并指向日志。
- `HardwareErrorLog`：把异常写进运行目录下的 `LiteMonitor++_HardwareError.log`（Release 同样生效）。

---

## 6. 风险登记册

| # | 风险 | 等级 | 缓解措施 |
|---|------|------|----------|
| R1 | 网页端 `gid="GPU.0"` 导致 `querySelector` 抛异常 → **白屏** | **P0** | §3.4 + Task 4 后端消毒；已用 headless Chrome 实测复现与修复；Task 7 有「结果可安全用作 CSS 选择器」用例 |
| R2 | 只拆组不去前缀 → 面板出现 `3080 Ti` / `3080 Ti 温度`，功能等于没做 | **P0** | §3.3 + Task 3.5；`SplitPerCard` 单开关强制两者同生共死 |
| R3 | 面板组标题是启动快照 → 冷启动永远显示 `GPU.0` 而不是简称 | **P1** | §3.9 + Task 5：`Tick()` 里就地刷新；Task 7 有「简称就绪后标题变化」用例 |
| R4 | 拆组逻辑顶掉插件 / InfoService 注入的标签（`GPU.0.Custom` 这类自建项） | **P1** | §3.10 + Task 3.5：`translated` 判定，未翻译就继续走动态标签分支 |
| R5 | 设置页「保存别名」把默认值固化成别名 | **P1** | §3.7 + Task 3.4 改用「输入框初值」作基准；§3.6 + Task 1：`GetDefaultTitle` 用 `GetOriginal` 绕开 override |
| R6 | 5 处标题解析逻辑分散 → 前后端标题不一致 | **P1** | §3.5 + Task 3 收敛到 `GroupTitleResolver`（6 处 / 4 界面全覆盖） |
| R7 | `UIGroup` 的 setter 与 fallback 两处推导规则不一致 | **P1** | Task 2 两处同步改；Task 7 用 `UIGroup` 属性测（走 setter 路径） |
| R8 | 提交时把用户未提交的改动卷进去 | **P1** | 每个提交只 `git add` 明确文件路径，禁用 `-A` / `.` / `-a`；提交脚本 `commit-task.ps1` 会自检暂存区并剔除意外文件 |
| R9a | 设置页输入框初值是「设置页**创建**时刻」的快照（`SettingsForm` 用 Draft 机制，页面一次性建好） | P2 | 不污染配置（Task 3.4 用初值做基准）；用户重开设置页即刷新 |
| R9b | 同型号双卡简称相同 → 两个组标题一样 | P2 | 用户可自定义区分；属上游限制（`GpuRegistry` 层面就分不出来） |
| R10 | 拆组后组数 +1，面板高度不够导致内容被裁 | P2（已降级） | **实际不成立**：`Render()` 里 `ClientSize = new Size(w, _layout.Build(_groups))`，高度自适应 |
| R11 | `CachedGroupsKey` 在 `LiteWebServer` 里不再被使用 | P3 | 它是 `public` 属性，不产生 CS0169 警告；保留以免影响其他潜在调用方 |
| R12 | 开关写成 `const` → 编译器把旧逻辑判为死代码 → CS0162（+1 警告） | P2 | §3.8 已改为 `static readonly`；**试编译实测确认**：写 const 时 232→234，改 static readonly 后回到 232 |
| R13 | `Dictionary.TryGetValue` 的 `out string` 在可空分析下触发 CS8600（+1 警告） | P2 | `GroupTitleResolver` / `GpuItemSync` 均改用 `out string?`；试编译实测确认 |
| R14 | 自建显卡指标（`GPU.0.Custom`）不在语言包里 → `GetOriginal` 回退成原始 Key，前导点被 `StripGpuPrefix` 剥掉后变成 `.Custom` | P3 | **既有缺陷，非本次引入**；本次的 `translated` 判定（§3.10）让这类项在拆组模式下走动态标签分支，不再显示 `.Custom` |
| R15 | 网页端 `grp.name` 走 `innerHTML` 拼接，用户别名里的 `<` / `"` 可注入 HTML | P3 | **既有行为**（改前 `LanguageManager.T("Groups.GPU")` 同样返回别名），本次未加剧；本计划不修 |
| R16 | `ToSafeGroupId` 理论上的碰撞（真出现 `GPU_0` 分组键会与 `GPU.0` 撞成同一 gid） | P3 | 当前不存在这种分组键；`GpuGroupPolicy` 是唯一产生按卡分组键的地方 |
| R17 | `GpuItemSync` 的 `SortIndex != 0` 哨兵在 `SaveToWorkingList` 压平后可能失效 | P3 | 既有缺陷、非本次引入；见 §2.4 |
| R18 | 设置页保存时把**自动标签**固化成 `UserLabel` —— R5 的第一版实现拿 `LanguageManager.GetOriginal` 的**回退值**（`"GPU.0.Load"`）当「输入框初值」，与输入框实际显示的 `"3080 Ti 温度"` 不一致，于是每次保存都判定「用户改过」 | **P1** | 提交 `8f88192` 改为用输入框**初值**（`MonitorItemRow._initialName`，构造时原样保存）做判定基准，完全不碰语言包；并加一次性迁移清掉已被固化的旧式自动名（用户自起的名字保留） |
| R19 | **发布目录被污染** → LHM 打不开硬件树 → 所有 LHM 指标恒 0，且故障完全无痕 | **P0** | 真机实测：把新版本 publish 到旧输出目录且未清空时，根目录会混入**错版本**的依赖程序集（本机为 `System.Threading.AccessControl.dll` 33072 字节，当前 RID-specific publish 期望 41224 字节）；.NET 的 app-local 优先规则会加载根目录那份，于是 LHM `Mutexes.Open()` 抛 `ArgumentNullException(Parameter 'identity')`，`Computer.Open()` 整个失败。提交 `3df28cc` 新增 `HardwareErrorLog.CheckPublishDirectory()` 启动自检，**直接点名是哪个文件、期望多少字节**；解法是清空发布目录重新 publish（`scripts/pack-release.ps1` 已自带清空暂存目录）。**已用最小复现探针三段式实验定案，完整链条与证据见 §5.1** |
| R20 | `ReloadComputerSafe` **先清空映射表再重开硬件**，且三步在同一个 `try` 里；中途抛异常 → `Rebuild` 永不执行 → 映射表永久为空，同时 `GpuRegistry` 被清空 → **按卡拆组的组标题退化成 `GPU.0` / `GPU.1`** | **P1** | 提交 `3df28cc`：映射表的清空交给 `SensorMap.Rebuild` 内部的原子交换（本来就不需要提前清）；`Open` 失败时**保留旧映射表**；每一步独立 try/catch 并写日志 |
| R21 | 硬件层异常被 `catch (Exception ex) { Debug.WriteLine(...) }` 吞掉，而 `Debug.WriteLine` 带 `[Conditional("DEBUG")]` ⇒ **Release 构建里整段被编译掉**，故障对用户和排查者都完全不可见 | **P1** | 提交 `3df28cc` 新增 `HardwareErrorLog`（写运行目录下的 `LiteMonitor++_HardwareError.log`，Release 同样生效，超 512KB 滚动归档）+ `HardwareMonitor.LastHardwareError` + 托盘气泡提示用户一次 |

---

## 7. 已知行为变更与限制

### 7.1 行为变更（用户可感知）

1. **面板上 GPU 组的标题从 `GPU` 变成显卡简称**（单卡机器也从 `GPU` 变成 `3080 Ti`）。冷启动后需要等硬件初始化完成（`Tick()` 自动刷新，通常 1~2 秒内）。
2. **GPU 指标的行标签去掉了显卡型号前缀**（`3080 Ti 温度` → `温度`）。
3. **设置页「监控项显示」里 GPU 从 1 个组块变成 N 个组块**（N = 监控的显卡数）。
4. **右键菜单里 GPU 从 1 个分组变成 N 个分组**。
5. **网页端 GPU 从 1 张卡片变成 N 张卡片**。
6. **旧配置里的 `GroupAliases["GPU"]` 自动迁移到 `GPU.0`**（只给 0 号卡），迁移后 `GPU` 键被删除。

### 7.2 已知限制

1. **横屏模式不显示组标题** —— 横屏走 `HorizontalRenderer`，分组结构被拍平成 `Column` 列表，组信息丢失。自定义标题仅在**竖屏面板 / 设置页 / 右键菜单 / 网页端**生效；横屏下区分卡靠的是显卡简称。
2. **任务栏 / 托盘不分组** —— 托盘上两张卡的同名指标都显示简称（`3080 Ti`），靠数值区分。
3. **同型号双卡简称相同** → 两个组默认标题一样，需要用户手动区分（`GpuRegistry` 层面就分不出来）。
4. **历史曲线仍按 series 分组**（`GPU.0.Load` / `GPU.1.Load` 是两条独立曲线），不合并成按卡的视图。
5. **设置页输入框初值是「设置页创建时刻」的快照**（`SettingsForm` 用 Draft 机制，页面一次性建好）—— 重开设置页即刷新。
6. **浏览器端自定义标题**依赖 `Settings.GroupAliases`，网页端通过 `GroupTitleResolver.Resolve` 读取 → 与桌面端一致。

---

## 8. 附录

### 8.1 关键坐标索引

| 主题 | 文件:行 |
|------|---------|
| `UIGroup` 主计算（**主改点**） | `src/Core/Settings.cs:266` |
| `UIGroup` fallback（**同步改**） | `src/Core/Settings.cs:309-332` |
| `GroupAliases` 存储 | `src/Core/Settings.cs:152` |
| `GpuKey` 前缀 / 解析 | `src/Core/GpuKey.cs:20,29,49` |
| `GpuRegistry` 简称 / 全名 | `src/Core/GpuRegistry.cs:39-50` |
| `GpuItemSync` 指标表 / 排序步长 | `src/Core/GpuItemSync.cs:19-27,30,122` |
| **新增** 分组策略 | `src/Core/GpuGroupPolicy.cs` |
| **新增** 标题解析器 | `src/Core/GroupTitleResolver.cs` |
| 面板分组构建 + 标题解析（**2 处**） | `src/UI/UIController.cs:296-299, 346-349` |
| **新增** 标题就地刷新 | `src/UI/UIController.cs` `RefreshPerCardGroupTitles()` |
| 面板画组框 + 组标题 | `src/UI/UIRenderer.cs:75-104` |
| 面板布局（遍历任意组数） | `src/UI/UILayout.cs:68` |
| 设置页组块创建 | `src/UI/Settings/MonitorPage.cs:299-307, 391-416` |
| 设置页保存别名（**主改点**） | `src/UI/Settings/MonitorPage.cs:500-505` |
| 设置页组标题输入框（**主改点**） | `src/UI/Controls/MonitorControls.cs:338-386` |
| 右键菜单分组标题 | `src/UI/Helpers/MenuMonitorHelper.cs:239-240` |
| 网页端分组 / groupId（**主改点**） | `src/System/WebServer/LiteWebServer.cs:132, 180-183, 248` |
| 网页端前端 gid 用法（**只读参考**） | `src/System/WebServer/WebPageContent.cs:404-489` |
| 指标标签解析（**主改点**） | `src/Core/MetricLabelResolver.cs:19-28` |
| 任务栏 / 横屏简称（不改） | `src/Core/MetricLabelResolver.cs:57-86` |
| `IsAnyEnabled`（不受影响） | `src/Core/SettingsHelper.cs:483-486` |
| 语言包 Groups 段 | `resources/lang/zh.json` → `Groups` |

### 8.2 语言包对照（`resources/lang/zh.json`）

```json
"Groups": {
  "DASH": "看板",
  "CPU": "CPU",
  "GPU": "GPU",
  "HOST": "主机",
  "DISK": "磁盘",
  "NET": "网络",
  "DATA": "流量统计",
  "BAT": "电池"
}
```

> 注：rebrand 提交（`25d77d4`）已移除语言包里的 emoji。

拆组后 `Groups.GPU` **不再被使用**（没有 `GPU` 组了），`Groups.GPU.0` 也不需要加语言包条目——按卡分组的默认标题来自 `GpuRegistry` 的显卡简称，是**运行时动态值**。

`Items` / `Short` 段保持不变（`GpuKey.ToBaseKey` 仍会归一化到 `Items.GPU.*`）。

### 8.3 参考：既有 plan（本文档不修改它们）

- `docs/superpowers/plans/2026-10-20-gpu-multi-card.md` — 多卡改造
- `docs/superpowers/plans/2026-10-20-gpu-vram-d3d-fix.md` — 显存串卡修复
- `docs/superpowers/plans/2026-10-20-rebrand-compliance.md` — rebrand（**不碰**）

---

## 9. 独立审核记录

verifier agent 审核结论 **FAIL**，问题与处置如下（**全部已处理**）：

| # | 级别 | 问题 | 处置 |
|---|------|------|------|
| P1-1 | P1 | 面板组标题是启动快照，冷启动永远显示 `GPU.0` | §3.9 + Task 5（`Tick()` 就地刷新） |
| P1-2 | P1 | plan 代码块写 `const bool` → 234 警告，违反自己的验收门槛 | §3.8 改 `static readonly` + `out string?`（实测 232） |
| P1-3 | P1 | 拆组逻辑顶掉插件 / InfoService 注入的标签 | §3.10 + Task 3.5（`translated` 判定） |
| P2-1 | P2 | 横屏模式不显示组标题 | §2.3 / §7.2 记录为已知限制 |
| P2-2 | P2 | 设置页初值是「创建时刻」快照 | §7.2(5) + R9a |
| P2-3 | P2 | `SaveToWorkingList` 把 `SortIndex` 全局压平 | §2.4 记录 + R17 |
| P2-4 | P2 | `GetDefaultTitle` 会经 override 返回用户别名 | §3.6 改用 `GetOriginal` + Task 7 用例 |
| P2-5 | P2 | 旧 `GroupAliases["GPU"]` 有更好的折中没被评估 | §3.11 + Task 6（只迁移给 0 号卡） |
| P3-1 | P3 | §3.6 描述的旧故障场景与现状不符 | §3.7 重写为「D3/D6 改造后才会产生」的真实链路 |
| P3-2 | P3 | 「散落 5 处」计数不准 | §2.1 改为「6 处 / 4 界面」 |
| P3-3 | P3 | 「不碰 chore/rebrand」措辞不准 | 抬头改为「不碰用户当前未提交的改动」 |
| P3-4 | P3 | R14 误判 `en.json` 不存在、虚报缓解措施 | R14 重写（语言包实际有 9 种；真正成因是「自建指标不在语言包里」） |
| P3-5 | P3 | §6.1「1699 个语法错误」已过时 | 移除该描述，保留「在干净副本构建」的做法 |
| P3-6 | P3 | §4「用户进行中改动」清单过时 | 抬头更新为当前 4 个文件（不含本计划的任何一个目标文件） |
| 补充 1 | P2 | 网页端 `grp.name` 走 `innerHTML`（别名可注入 HTML） | R15 记录为既有行为，本计划不修 |
| 补充 2 | P3 | `ToSafeGroupId` 理论碰撞 | R16 记录 |
| 补充 3 | P3 | R7「面板高度不够被裁」不成立 | R10 降级 |
