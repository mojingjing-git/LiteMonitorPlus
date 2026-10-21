# W3 —— 设置页「意外回写」缺陷包交付说明

> 日期：2026-10-21
> 交付 commit：`5add165`（`fix(settings)`，3 个缺陷）→ `7ea421e`（`docs(appearance)`，交付说明）
> → 本 commit（`fix(settings)`，阈值行补齐 + W3 防回退探针，§4.5 / §4.6）
> 性质：跨会话移交缺陷包 —— 由安全会话发现的设置页缺陷，本工作线接收并修复
> 关联：A16 主面板接线（`2026-10-20-appearance-a16-acceptance.md`）、M2 合同（`2026-10-20-appearance-m2-contract.md`）

---

## 1. 背景：设置页为什么会「自己改用户的配置」

`SettingsForm` 已经具备 draft 机制：打开设置页时 `_draftCfg = _cfg.DeepClone()`，各页面只写 draft，
点「应用」才 `SettingsChanger.Merge` + `_cfg.Save()`，点「取消」丢弃 draft。

**所以页面级 `Config` 对象本身就是 draft** —— 页面里改控件等于改 draft。

问题出在控件绑定方式上。`src/UI/Settings/SettingsUIBuilder.cs` 里的 `Add*` 构造器一律采用
**立即绑定**（immediate binding）：

```
控件值变化 → 立刻 set(新值) → 写回 draft
```

这条链路对**用户操作**是正确的，但它对**程序化赋值**同样成立。设置页里有大量程序化赋值：

| 场景 | 代码位置 | 触发的事件 |
|---|---|---|
| 页面加载 / 切标签页时同步控件值 | `SettingsPageBase.RegisterRefresh` 注册的刷新体 | `CheckedChanged` / `TextChanged` / `SelectedIndexChanged` |
| 异步扫描到硬件后重填设备下拉 | `SystemHardwarPage` → `FillSync` / `FillGpuSync` | `SelectedIndexChanged` |
| 刷新按钮重载全部选项 | `SettingsPageBase` 刷新体 | 同上 |

**用户还没点任何东西，配置已经被界面自己改了。** 三个具体缺陷由此派生。

---

## 2. W3-1 不可见的「跟随任务栏」复选框把用户的 `true` 静默改成 `false`

### 现象

用户开了「横向面板跟随任务栏」，进入设置页随便改了点别的（甚至什么都没改），点「应用」，
回到主面板发现横向面板不再跟随任务栏了。

### 根因

`src/UI/Settings/MonitorPage.cs` → `MonitorPage._chkLinkHorizontal` 只在**任务栏标签页**可见
（`SwitchTab` 里 `_chkLinkHorizontal.Visible = _isTaskbarTab`），在主标签页上不可见。

同步逻辑同样被限制在任务栏标签页：

```
if (_isTaskbarTab) _chkLinkHorizontal.Checked = Config.HorizontalFollowsTaskbar;
```

于是当用户停在主标签页时，这个复选框一直停留在**构造时的 `false`**。
而 `MonitorPage.Save()` 无条件提交控件值：

```
SettingsChanger.UpdateMonitorList(Config, _workingList, _chkLinkHorizontal.Checked);
```

⇒ 用户的 `true` 被静默改成 `false`。**用户从未看到过这个控件，也从未改过它。**

### 修法

| 动作 | 实现 |
|---|---|
| 标记「用户真的改过」 | 新增字段 `MonitorPage._linkHorizontalEdited`（默认 `false`） |
| 记录改动 | `MonitorPage` 的 `CheckedChanged` 回调：抑制作用域内直接返回；否则置 `_linkHorizontalEdited = true` 并写 `Config.HorizontalFollowsTaskbar` |
| 同步控件 | 新增 `MonitorPage.SyncLinkHorizontalFromConfig()`：在 `BeginSuppressWriteBack()` 作用域内赋值；**用户改过则不覆盖**。`SwitchTab` 与 `OnShow` 的两处同步都改调它（不再限任务栏标签页） |
| 提交 | `MonitorPage.Save()`：`bool linkHorizontal = _linkHorizontalEdited ? _chkLinkHorizontal.Checked : Config.HorizontalFollowsTaskbar;` |

要点：**只在用户真的改过时才提交控件值**。没改过时提交的是配置原值 ⇒ 不可见的控件不再有话语权。

---

## 3. W3-2 程序化赋值被当成用户选择，把配置写成空串

### 现象

在设置页停留几秒（等待硬件扫描完成），某些下拉框（设备来源、刷新间隔等）的用户原值被清空成「自动」。

### 根因

`src/UI/Settings/SettingsUIBuilder.cs` 的 8 个 `Add*` 构造器的立即绑定回调**无条件回写**：

| 构造器 | 原回调 |
|---|---|
| `AddToggle` | `set(chk.Checked)` |
| `AddInput` | `set(input.Inner.Text)` |
| `AddInt` | `int.TryParse` 成功即 `set(val)` |
| `AddDouble` | `double.TryParse` 成功即 `set(val)` |
| `AddColor` | `set(input.HexValue)` |
| `AddCombo` | `set(cmb.Text)` |
| `AddComboIndex` | 选中项在范围内即 `set(selectedIndex)` |
| `AddComboPair` | `set(cmb.SelectedValue)` |

程序化赋值时它们与用户操作走同一条路。于是：

- `AddCombo` 的刷新体里，`cmb.Items.Contains(current)` 为假时（当前值不在候选列表里）
  会落到 `cmb.SelectedIndex = 0`（「自动」）⇒ 事件把空串写回配置 ⇒ **原值丢失**。
- `AddCombo` 在重填选项的瞬间 `SelectedIndex` 为 `-1`，此时 `cmb.Text` 是空串 ⇒ 同样写空串。

### 修法

**统一机制**（而不是每个调用点各加一个 bool 标志）：

1. 8 个构造器的立即绑定回调全部加 `if (page.IsWriteBackSuppressed) return;`
2. `AddCombo` 额外加 `if (cmb.SelectedIndex < 0) return;` —— 选中项为空时不写
3. 三种 ComboBox 的 `RegisterRefresh` 刷新体全部包进 `using (page.BeginSuppressWriteBack())`
   —— 刷新时选中项被迫落到第一项是**程序化改动**，不能写回配置

---

## 4. W3-3 设备偏好原值不可用时被落到「自动」，偏好就此丢失

### 现象

用户指定了「网速测量的网卡 = 某某网卡」，某天那块网卡改名或换了驱动，偏好没了。

### 根因

`src/UI/Settings/SystemHardwarPage.cs` → `FillSync` 的重填逻辑：

```
combo.Inner.Items.Clear();
foreach (var item in fullList) combo.Inner.Items.Add(item);
if (!string.IsNullOrEmpty(currentVal) && fullList.Contains(currentVal))
    combo.Inner.SelectedItem = currentVal;
else
    combo.Inner.SelectedIndex = 0;      // ← 落到「自动」，事件写回空串
```

原值不在候选里（U 盘没插、网卡改名、GPU 换驱动）⇒ 落到「自动」⇒ 事件把空串写回配置 ⇒
**用户的设备偏好就此消失**。`FillGpuSync` 同理。

### 修法

1. 重填期间整段包进 `using (BeginSuppressWriteBack())` ⇒ 重填过程中的选中变化不回写
2. 原值不可用时**保留原值**，并加一条只读提示项：
   - 标签 = `Menu.PreferenceUnavailable` 格式化（形如「某某网卡（当前不可用）」）
   - **取值仍然是原值** ⇒ 即便用户点回它，写回的也是原值，不会被冲成空串
3. 6 个设备偏好下拉（`_cbDisk` / `_cbNet` / `_cbMobo` / `_cbFanCpu` / `_cbFanPump` / `_cbFanCase`）
   由 `AddCombo` 改为 `AddComboPair`（「自动」`Value=""`，设备 `Value=名称`），
   `get` / `set` 回调改为**值语义**（`Config.PreferredDisk = v ?? ""`）

> `AddComboPair` 的第 3 个参数是「标签/取值」成对的下拉项，这正是「提示项」得以存在的前提。
> 配套的 `LiteComboBox.AddItem(string text, string value)` / `LiteComboBox.SelectValue(string value)`
> 在 `src/UI/Controls/LiteUI.cs` 中已有，无需新增控件能力。

---

## 4.5 W3-4 阈值行：同一类缺陷的漏网之处（写探针时发现）

### 现象

用户配置的告警/严重阈值（如「网速上传 警告 1 MB/s、严重 2 MB/s」）在某次操作后变成 0。

### 根因

`src/UI/Controls/LiteUI.cs` → `LiteThresholdRow` 构造函数的立即绑定**没有** W3 的抑制守卫：

```
inputWarn.Inner.TextChanged += (s, e) => range.Warn = inputWarn.ValueDouble;
inputCrit.Inner.TextChanged += (s, e) => range.Crit = inputCrit.ValueDouble;
```

两个问题叠加：

1. **没有抑制守卫** —— 刷新体 `page.RegisterRefresh(...)` 把文本写回控件时同样触发 `TextChanged`，
   程序化改动被当成用户输入写回配置。
2. **`ValueDouble` 解析失败返回 `0.0`**（`LiteNumberInput.ValueDouble`）——
   `LiteNumberInput` 的 `KeyPress` 只放行数字/`.`/`-`，但**粘贴可以绕过**它。
   粘进一段非法文本后，用户的阈值就被静默改成 **0**。

`ValueRange` 是 **class**（`src/Core/Settings.cs`）⇒ `range.Warn = ...` 改的是共享对象，
这个 0 会真的进配置。

### 修法

与 W3-2 同一口径，另加一条「解析失败不写」：

```
inputWarn.Inner.TextChanged += (s, e) => {
    if (page.IsWriteBackSuppressed) return;
    if (double.TryParse(inputWarn.Inner.Text, out double w)) range.Warn = w;
};
```

- 抑制作用域内不回写；
- 刷新体包进 `using (page.BeginSuppressWriteBack())`；
- **解析失败时保持原值**（不再用 `ValueDouble` 那个"失败即 0"的语义）。

> 这一点在原缺陷清单里没有 —— 是写 W3 防回退探针、准备把"所有立即绑定都要守门"这条口径
> 做成源码级门槛时发现的漏网之处。`LiteUI.cs` 不在安全会话的写集内，未与他人冲突。

---

## 4.6 W3-5 防回退探针（`tests/AppearanceSettingsProbe`）

W3 修的这类缺陷**不会让程序崩**，只会在用户没做任何事时悄悄改掉配置 ——
人工点一遍界面很难稳定复现。因此需要一个机器断言把「程序化改动不许回写」钉死。

探针工程：`tests/AppearanceSettingsProbe/`（**不进 sln**，与既有探针惯例一致；
不新增任何 NuGet 依赖；只构造内存中的 WinForms 控件与读取源码文本）。

### 运行时用例（19 条）

| 组 | 内容 |
|---|---|
| 抑制作用域语义 | 作用域外/内、嵌套、内层释放后仍在抑制、最外层释放后恢复、重复释放安全（6 条） |
| 立即绑定不回写 | `AddCombo`（构造期 / 抑制内 / 作用域外对照 / 选中项为 -1）、`AddToggle`、`AddComboPair`、`AddInt`、`AddInput`（9 条） |
| 刷新体不改原值 | `SetContext` 与 `OnShow` 两条路径下，原值不在候选里时刷新不得把原值冲掉（2 条） |
| **负向对照** | 无守卫的绑定在抑制作用域内**确实会回写**；非法输入经 `ValueDouble` **确实变成 0**（2 条） |

★ 负向对照是**鉴别力自证**：如果"没有守卫的绑定在抑制作用域内也会回写"这条不成立，
那前面那批"不回写"断言就是恒真的摆设、探针毫无价值。
负向对照在探针内部现造等价场景实现，**不修改主工程源码** ——
多会话共用工作树时，主工程源码的临时改动有被他人提交卷进去的风险。

### 源码级门槛（10 组）

防止将来「顺手优化」时删掉一行守卫后**无声退化**：

- `SettingsUIBuilder` 的 8 个 `Add*` 都有抑制守卫（8/8）
- 三种 ComboBox 的刷新体都包进作用域（3/3）
- `MonitorPage` 复选框有守卫 / `Save` 用 `_linkHorizontalEdited` / 同步走作用域
- `FillSync` 与 `FillGpuSync` 重填走作用域（2/2）
- 六个设备偏好下拉用 `AddComboPair`（6/6）
- `LiteThresholdRow` 的守卫、作用域包裹、解析失败不写（3 条）
- 探针工程不进 sln（自查）

方法体提取用「从标记处取其后第一个花括号块」，
配平时逐字符跳过行注释、块注释、普通字符串、逐字字符串 `@"..."`、字符字面量 ——
避免注释或字符串里的花括号破坏配平。

**当前结果：33 / 33 全部通过。**

---

## 4.7 复核后的加固（2026-10-21，安全会话复核后）

安全会话独立复核了 `a9973ff`（三条地基立论逐条重验 + 独立跑探针 33/33），确认修复成立，
并指出两条**残留**与一条**门槛问题**。本节记录这三项的处理。

### 4.7.1 残留 A：阈值解析过宽，粘贴能写入「界面手打不出来」的形式

复核结论：`double.TryParse` 默认样式含 `AllowExponent` / `AllowThousands` / 首尾空白，
于是 `"1e5"`→100000、`"1,000"`→1000、`" 8 "`→8、`"-3"`→-3 全部放行。
`LiteNumberInput` 的 `KeyPress` 只放行数字/`.`/`-`，但**粘贴能绕过**它
⇒ 这几种形式界面上打不出来，却能被写进配置。

> 该行为在 `ValueDouble` 时代本就存在（`ValueDouble` 用的也是默认样式）⇒ **不算回归**。
> 但既然已经把解析收进自己的方法，就该一并收紧。

**修法**（`src/UI/Controls/LiteUI.cs` → `LiteThresholdRow.TryParseThreshold`）：

```
const NumberStyles styles = AllowLeadingSign | AllowDecimalPoint;
```

**只接受 KeyPress 放行的形式**：数字 / 前导负号 / 小数点。
刻意**不**用 `NumberStyles.Float`（含 `AllowExponent`）、**不**用 `AllowThousands`、
**不**放行首尾空白。另加 `double.IsFinite` 拒绝 `NaN` / `Infinity`
（`double.TryParse` 会接受这两个）。

> **上下限钳制没有在通用控件层做** —— 各阈值类型的合理范围由页面（`ThresholdPage`）掌握，
> 在控件层猜一个范围只会误伤用户的合法配置。该项记为 A17 实测时的观察点，不在本轮范围。

### 4.7.2 残留 B：非法文本留在界面上，与配置不一致且无提示

粘进非法文本后：控件仍显示非法文本，配置保持旧值 ⇒ 界面与配置不一致，用户毫无提示。

**修法**：`LiteThresholdRow` 给两个输入框加 `Leave` 处理器 ——
离场时若文本仍不可解析，就把文本回填成配置里的旧值。
回填用 `ToString("0.####", CurrentCulture)`：与界面其它数字同文化，但不带千分位 ⇒ 严格样式可解析。

### 4.7.3 门槛问题：探针不在 sln、无 CI，平时根本不会跑

复核结论很直接：外观探针一律**不进 sln**，`dotnet build` / `dotnet test` 平时不会碰它们
⇒ 「防回退」只在有人想起来手动跑时成立。这条是真门槛问题。

**修法**：新增 `scripts/run-probes.ps1` —— 外观探针的**统一入口**：

- 串行构建 + 串行运行全部探针（并发会争抢主工程 `obj/`）
- 收录清单（命名与范围由外观线定）：
  `AppearanceThemeCheck` / `AppearanceStaticBackdropProbe` / `AppearanceM2Probe` /
  `AppearanceWiringProbe` / `AppearanceDecorProbe` / `AppearanceDecorRendererProbe` /
  `AppearanceWindowShapeProbe` / `AppearanceSettingsProbe`
  + `AppearanceBackdropProbe`（**实机屏幕捕获**，默认跳过，需 `-IncludeBackdrop`）
  + `LiteMonitor.Tests`（`-SkipUnitTests` 可跳过）
- 汇总通过/失败/跳过，失败时打印失败输出并以 exit 1 结束
- 探针输出用 `Process` + `RedirectStandardOutput` 捕获
  （`Console.OutputEncoding` 在 stdout 被重定向时会抛异常，管道直接调用会崩）
- 主工程 exe 被运行中实例锁住导致构建失败时，给出明确提示

```powershell
scripts/run-probes.ps1                                   # 默认：8 个探针 + 单元测试
scripts/run-probes.ps1 -IncludeBackdrop -OutDir D:\ev   # 连实机捕获一起跑，证据写别处
```

### 4.7.4 顺带补齐的字体泄漏面（复核时扩查发现）

复核 B 项时把 `src/UI/Controls/LiteUI.cs` 全量扫了一遍，发现 **19 处** `new Font(...)`
（此前只处理了设置页目录里的 8 处）。其中最严重的一处：

```
src/UI/Controls/LiteUI.cs → LiteNavBtn.OnPaint
    Font = new Font(Font, FontStyle.Bold);   // ★ 每次重绘都 new 一个
```

⇒ **鼠标划过导航按钮就会漏一个 GDI 字体对象**（比设置页的泄漏严重得多，因为它随鼠标移动持续发生）。

**修法**：
- `LiteUI.cs` 的 19 处全部改走 `UIFonts`（含新增 `Underline` / `BoldUnderline` 变体）
- `LiteNavBtn.OnPaint` 改为复用 `UIFonts.Bold(10F)` / `UIFonts.Regular(10F)` 两个固定实例
- `UIFonts` 补 `Underline` / `BoldUnderline`（`FontStyle` 是 Flags，可组合）

**探针门槛同步新增 4 条**（防止将来改回 `new Font`）：
`UIFonts 是进程内缓存` / `LiteUI.cs 内不再有裸 new Font` / `LiteNavBtn 重绘不再新建字体` /
`阈值解析用严格 NumberStyles` / `阈值解析拒绝 NaN / Infinity` / `阈值非法输入离场回填`。

---

## 5. 回写抑制作用域（核心机制）

在 `src/UI/Settings/SettingsBase.cs` → `SettingsPageBase` 引入**作用域**，而不是每页各加 bool 标志：

| 成员 | 作用 |
|---|---|
| `SettingsPageBase._suppressWriteBackDepth` | 抑制深度计数器（支持嵌套） |
| `SettingsPageBase.IsWriteBackSuppressed` | 当前是否处于抑制作用域内 |
| `SettingsPageBase.BeginSuppressWriteBack()` | 进入作用域，返回 `IDisposable`；`Dispose` 时退出 |
| `SettingsPageBase.SuppressWriteBackScope` | 私有句柄类，`Dispose` 时递减深度 |

用法：

```
using (page.BeginSuppressWriteBack())
{
    // 这里的任何控件赋值都不会回写配置
}
```

**为什么用作用域而不是布尔标志**：布尔标志需要「设置 → 操作 → 复位」三步，容易漏掉复位，
且不支持嵌套（本轮 `FillSync` 里 `AddComboPair` 的构造过程本身也会触发事件）。作用域用
`using` 保证释放、深度计数保证嵌套安全。

---

## 6. 降级可见化：配置写不进去时必须让用户看见

`src/Core/SettingsHelper.cs` → `SettingsHelper.GlobalBlockSave` 为真时（配置因 **IO 故障**读不出来、
本次会话禁止自动落盘，避免用默认值覆盖用户磁盘上那份完好的配置），用户在界面上的任何修改
**都不会被保存**。以前这件事完全没有提示 —— 用户改了半天，退出后全没了。

本轮补两处可见化：

| 位置 | 实现 | 说明 |
|---|---|---|
| 七个设置页**顶部** | `SettingsBase.cs` → `SettingsPageBase.UpdateConfigWriteBlockedNotice()` | 「配置暂时无法读取，本次修改不会保存」提示条（`Dock=Top`，追加到 `Controls` **最后** ⇒ 停靠顺序反向 ⇒ 出现在页面最上方） |
| 主面板**左下角** | `src/UI/UIController.cs` → `UIController.DrawConfigWriteBlockedNotice()` | 「配置未保存」提示（`Alignment.Near`），与右下角的「实时玻璃未生效」提示互不重叠 |

**放在基类 `SettingsPageBase` 的好处**：七个设置页全部自动带上，不用逐页加代码 ——
这正是「修一次覆盖全页面」而不是「逐页打补丁」。

> 竖屏与横屏两条渲染路径都调用了 `DrawConfigWriteBlockedNotice`，与
> `DrawLiveGlassNotice` / `DrawEmptyStateNotice` 的处理方式一致。

---

## 7. 新增语言键（9 个语言文件）

`resources/lang/` 下 9 个语言文件（`de` / `en` / `es` / `fr` / `ja` / `ko` / `ru` / `zh-tw` / `zh`）各新增 3 键：

| 键 | 用途 | 中文 |
|---|---|---|
| `Menu.ConfigWriteBlocked` | 设置页顶部提示条全文 | 配置暂时无法读取，本次修改不会保存 |
| `Menu.ConfigWriteBlockedShort` | 主面板左下角短提示 | 配置未保存 |
| `Menu.PreferenceUnavailable` | 设备偏好不可用提示项（`{0}` = 原值） | {0}（当前不可用） |

> 语言文件在索引里是**纯 LF** 且对 git 呈 `-text` —— 写入时必须保持 LF，否则整文件 diff。

---

## 8. 验证记录

| 项 | 命令 / 口径 | 结果 |
|---|---|---|
| 主工程编译 | `dotnet build LiteMonitor.csproj -c Release` | **0 错误 / 233 警告** |
| 单元测试 | `dotnet test tests/LiteMonitor.Tests -c Release` | **604 通过 / 0 失败 / 7 跳过**（总计 611） |
| 主题探针 | `tests/AppearanceThemeCheck` | **442 / 442** |
| 静态背景探针 | `tests/AppearanceStaticBackdropProbe` | **21 / 21** |
| M2 合同探针 | `tests/AppearanceM2Probe` | 全部通过 |
| 接线探针 | `tests/AppearanceWiringProbe` | 全部通过 |
| 装饰探针 | `tests/AppearanceDecorProbe` | 全部通过 |
| 装饰渲染探针 | `tests/AppearanceDecorRendererProbe` | 全部通过 |
| 窗口轮廓探针 | `tests/AppearanceWindowShapeProbe` | 全部通过 |
| 背景验证探针（实机） | `tests/AppearanceBackdropProbe` | exit=0（见 §9） |
| **W3 防回退探针** | `tests/AppearanceSettingsProbe` | **33 / 33** |

> 单元测试总数比上一轮的 593 多 11 个 —— 增量来自安全会话新增的测试文件
> （`WebAuthHardeningTests` / `SettingsRecoveryTests` / `UpdateSignatureTests` 等），**不是**本缺陷包引入。

---

## 9. 附带实机数据：Backdrop 探针测量结果（A17 CPU 预算依据）

本轮顺带跑了 `tests/AppearanceBackdropProbe`（实机屏幕捕获），结果一并记录，
供 A17「后台 CPU 预算」与 A14 实机清单使用：

### 9.1 瞬时排除（`WDA_EXCLUDEFROMCAPTURE`）

| 指标 | 实测 | 判据 |
|---|---|---|
| 排除窗口 | **7.0 ms** | 越短越好（A06 要求瞬时） |
| 捕获 CPU | 0.00 ms | — |
| 立即可见 / 400ms 后可见 | True / True | 必须都可见 |
| 占空比 | **1.39%** | 瞬时排除不得造成持续消失 |

三种显示亲和性（`WDA_NONE` / `WDA_EXCLUDEFROMCAPTURE` / `WDA_MONITOR`）下探针区与探针外场景
均在实时变化 ⇒ 排除区是实时的，不是冻结画面。

### 9.2 模糊半径

`rise(0)=0.00` / `rise(12)=33.00` / `rise(24)=65.00` ⇒ 单调 = True，比值 1.97，**通过**。

阶跃边缘实测上升沿与合同 σ 基本吻合（Blur=12 @scale=1：合同 12.49 / 实测 12.88；
Blur=24 @scale=1：合同 24.49 / 实测 25.36）。

### 9.3 后台 CPU 预算（2fps）

| 场景 | 最佳 captureScale | 合计 CPU | 2fps 占比 | 对 ≤40ms/秒 预算 |
|---|---|---|---|---|
| 生产竖屏 240×788 | 0.25 | 5.21 ms | **0.065%** | 宽裕 |
| 生产竖屏 480×1576 | 0.25 | 19.27 ms | **0.241%** | 宽裕 |
| 动态高面板 240×1576 | 0.25 | 11.98 ms | **0.150%** | 宽裕 |
| 动态高面板 480×3152 | 0.25 | 26.56 ms | **0.332%** | 宽裕 |

**captureScale=1 在 2× 分辨率大面板下 p95 预算超标**（56.7ms / 109.4ms），
0.25 缩放全部通过 ⇒ 生产配置应固定 `captureScale=0.25`。

---

## 10. 未验证项与交接清单

### 10.1 本轮**未**验证（需要人眼或实机交互）

| 项 | 原因 |
|---|---|
| 七个设置页顶部提示条的实际观感与停靠位置 | 需要打开设置页目视确认提示条在页面最上方、且不遮挡内容 |
| 设备偏好「（当前不可用）」提示项的实际显示 | 需要拔设备/改网卡名才能触发 |
| 「配置未保存」提示在主面板左下角的实际观感 | 需要人为制造配置 IO 故障 |
| W3-1 的端到端复现 | 需要实机：开「跟随任务栏」→ 进设置页主标签页 → 改点别的 → 应用 → 确认未被改回 |
| W3-3 的端到端复现 | 需要实机：指定设备偏好 → 使该设备不可用 → 进设置页 → 应用 → 确认偏好未被清空 |

> 建议把这几项并入 `2026-10-20-appearance-a14-live-checklist.md` 一并重做。

### 10.2 明确**不碰**的文件（多会话共用同一工作树）

- `src/UI/SettingsForm.cs` —— 已判给安全会话（页面释放泄漏修复）。本轮刻意绕开该文件，
  提示条改在基类 `SettingsBase.cs` 实现。
- `src/System/WebServer/**`、`src/Core/Actions/**`、`src/UI/Helpers/MainFormBizHelper.cs`、
  `src/UI/UpdateDialog.cs`、`src/Plugins/**` 等 —— 安全会话在改，本工作线不派写任务。

### 10.3 提交纪律

本轮提交用**显式路径** `git add`（14 个文件），未使用 `git add -A`；
安全会话的 34 项改动在工作树中**原样保留、未被卷入**（提交前后 `git status` 已核对）。

---

## 11. 变更清单

| 文件 | 变更 |
|---|---|
| `src/UI/Settings/SettingsBase.cs` | `SettingsPageBase`：回写抑制作用域（`BeginSuppressWriteBack` / `IsWriteBackSuppressed` / `_suppressWriteBackDepth` / `SuppressWriteBackScope`）；`UpdateConfigWriteBlockedNotice()` 提示条 |
| `src/UI/Settings/SettingsUIBuilder.cs` | 8 个 `Add*` 立即绑定回调遵守抑制作用域；`AddCombo` 追加 `-1` 保护；3 个 `RegisterRefresh` 刷新体包进作用域 |
| `src/UI/Settings/MonitorPage.cs` | `_linkHorizontalEdited`；`SyncLinkHorizontalFromConfig()`；`SwitchTab` / `OnShow` 改调它；`Save()` 只在用户改过时提交复选框值 |
| `src/UI/Settings/SystemHardwarPage.cs` | `FillSync` / `FillGpuSync` 包进抑制作用域 + 不可用时保留原值并加提示项；6 个设备偏好下拉改 `AddComboPair` |
| `src/UI/UIController.cs` | `DrawConfigWriteBlockedNotice()`；竖屏 / 横屏两条路径都调用 |
| `resources/lang/*.json` × 9 | 新增 `Menu.ConfigWriteBlocked` / `Menu.ConfigWriteBlockedShort` / `Menu.PreferenceUnavailable` |
| `src/UI/Controls/LiteUI.cs` | `LiteThresholdRow` 立即绑定加抑制守卫 + 刷新体包进作用域 + 解析失败不写（§4.5） |
| `tests/AppearanceSettingsProbe/`（新） | W3 防回退探针：运行时断言 + 源码级门槛 + 负向对照（§4.6），**不进 sln** |
