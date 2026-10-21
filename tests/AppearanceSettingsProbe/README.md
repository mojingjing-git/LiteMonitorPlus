# AppearanceSettingsProbe —— W3「设置页意外回写」缺陷包的防回退探针

本工程验证 **W3 缺陷包**（`docs/superpowers/plans/2026-10-21-appearance-w3-writeback-fix.md`）的防回退门槛，分两组：

| 组别 | 覆盖 | 判定方式 |
|---|---|---|
| **运行时** | 回写抑制作用域的语义；立即绑定在抑制作用域内**不**回写、在作用域外**确实**回写 | 真实构造 WinForms 控件，触发真实事件 |
| **源码级** | 8 个 `Add*` 的抑制守卫、3 个刷新体的作用域包裹、`MonitorPage` 的 `_linkHorizontalEdited` 三元、`SystemHardwarPage` 的 6 个 `AddComboPair`、`LiteThresholdRow` 的守卫 | 源码文本扫描（花括号配平取方法体） |

> ⚠️ 本工程**不进 sln**（与既有 `AppearanceM2Probe` / `AppearanceWiringProbe` 的惯例一致）。
> ⚠️ 不新增任何 NuGet 依赖，只引用主项目与 BCL。
> ⚠️ 探针**只构造内存中的 WinForms 控件与读取源码文本**：不创建 `HardwareMonitor`、不 Open 硬件、
> 不做任何屏幕捕获、不读也不写用户配置目录（`Settings` 用默认构造，不触碰磁盘）。

## 为什么需要这个探针

W3 修的是「设置页的控件自己把用户的配置改掉了」这类缺陷。它们的共同形态是：

> 控件绑定是**立即绑定**（控件一变就写回 draft），
> 而**程序化赋值**（加载时同步、异步刷新选项、清空后重填）触发的是同一批事件。

这种缺陷**不会让程序崩**，只会在用户没做任何事时悄悄改掉配置 —— 靠人工点一遍界面很难稳定复现。
所以必须用机器断言把「程序化改动不许回写」钉死，并且**能证明这些断言不是摆设**。

## 运行时用例

### 一、抑制作用域的语义（6 条）

`SettingsPageBase.BeginSuppressWriteBack()` 返回 `IDisposable`，内部用深度计数支持嵌套。

| 用例 | 判定 |
|---|---|
| 作用域外未被抑制 | `IsWriteBackSuppressed == false` |
| 作用域内已抑制 | `== true` |
| 嵌套作用域内已抑制 | `== true` |
| 内层释放后仍在抑制 | 外层还在 ⇒ 仍 `true` |
| 最外层释放后恢复 | `== false` |
| 作用域重复释放安全 | 重复 `Dispose` 后深度不越界，仍 `== false` |

### 二、立即绑定在抑制作用域内不回写（9 条）

每条都成对出现：**抑制内不回写** + **作用域外确实回写**。
成对的意义是排除「回写通道根本没挂上」这种假通过。

| 用例 | 覆盖的构造器 |
|---|---|
| `AddCombo` 构造本身不回写 | 构造期的选中项赋值早于绑定挂载 |
| `AddCombo` 抑制内改选中项不回写 | W3-2 |
| `AddCombo` 作用域外改选中项会回写 | 对照组 |
| `AddCombo` 选中项为空时不写空串 | 选中项 `-1` 时不得把空串写进配置 |
| `AddToggle` 抑制内改不回写 / 作用域外改会回写 | W3-2 |
| `AddComboPair` 抑制内改不回写 / 作用域外改会回写 | W3-2 |
| `AddInt` 抑制内改文本不回写 | W3-2 |
| `AddInput` 抑制内改文本不回写 | W3-2 |

### 三、刷新体不改原值（2 条）—— W3-2 的核心场景

`AddCombo` 的刷新体在「当前值不在候选列表里」时会把选中项落到第一项（「自动」）。
那是**程序化改动**，绝不能当成用户的选择写回配置。

| 用例 | 做法 |
|---|---|
| `SetContext 触发刷新体不改原值` | 原值 `"Z"`（不在候选 `["A","B"]` 里）⇒ 刷新后原值必须仍是 `"Z"` |
| `OnShow 刷新不改原值` | 同上，走 `OnShow` 路径 |

### 四、负向对照（2 条）—— 鉴别力自证

**零覆盖不等于有效覆盖。** 下面两条断言的是「如果没有防护会发生什么」：

| 用例 | 判定 |
|---|---|
| 负向对照：无守卫的绑定在抑制作用域内确实会回写 | 故意挂一个**不带**守卫的 `SelectedIndexChanged` ⇒ 在抑制作用域内改选中项**必须**回写。若这条不成立，说明前面那批「不回写」断言是恒真的摆设 |
| 负向对照：非法输入经 `ValueDouble` 会变成 0 | `LiteNumberInput.ValueDouble` 在 `double.TryParse` 失败时返回 `0.0` ⇒ 这正是阈值被静默改成 0 的路径，解释了为什么阈值行不能直接用 `ValueDouble` 回写 |

> ★ 负向对照**不修改主工程源码**：它在探针内部现造一个等价的「无守卫绑定」来证明鉴别力成立。
> 这比改源码做变异验证更安全 —— 多会话共用同一工作树时，主工程源码的临时改动有被他人
> 提交卷进去的风险。

## 源码级门槛（6 组）

防止将来有人改坏时**无声退化**（比如「顺手优化」时删掉一行守卫）。

| 门槛 | 检查内容 |
|---|---|
| `SettingsUIBuilder` 八个 `Add*` 都有抑制守卫 | 8/8：`AddToggle` `AddInput` `AddInt` `AddDouble` `AddColor` `AddCombo` `AddComboIndex` `AddComboPair` |
| 三种 ComboBox 的刷新体都包进作用域 | 3/3：`AddCombo` `AddComboIndex` `AddComboPair` |
| `MonitorPage` 复选框有抑制守卫 | `_chkLinkHorizontal.CheckedChanged` 内含 `IsWriteBackSuppressed` |
| `MonitorPage.Save` 用 `_linkHorizontalEdited` 决定提交值 | 用户没改过时提交配置原值，而不是不可见控件的残留值 |
| `MonitorPage` 同步复选框走作用域 | `SyncLinkHorizontalFromConfig` 内含 `BeginSuppressWriteBack` |
| `FillSync` / `FillGpuSync` 重填走作用域 | 2/2：重填期间的选中变化不回写 |
| 六个设备偏好下拉用 `AddComboPair` | 6/6：`_cbDisk` `_cbNet` `_cbMobo` `_cbFanCpu` `_cbFanPump` `_cbFanCase` |
| `LiteThresholdRow` 立即绑定有抑制守卫 | 阈值行也是立即绑定，不能漏 |
| `LiteThresholdRow` 刷新体走作用域 | 同上 |
| `LiteThresholdRow` 解析失败时不写回 | 用 `double.TryParse` 而非 `ValueDouble` |
| 探针工程不进 sln | 自查 |

### 方法体提取口径

源码级门槛用「从标记处取其后第一个花括号块」的方式截取方法体。配平花括号时逐字符处理状态：
行注释、块注释、普通字符串、逐字字符串 `@"..."`、字符字面量 —— 全部跳过，
避免注释或字符串里的花括号破坏配平。

## 用法

```powershell
# 由集成者串行构建后运行（本探针自身不负责构建）
tests/AppearanceSettingsProbe/output/AppearanceSettingsProbe.exe [--repo-root <仓库根>] [--out <证据目录>]
```

- `--repo-root <路径>`：显式指定仓库根（默认从可执行文件位置向上逐级查找 `LiteMonitor.csproj`）。
- `--out <路径>`：证据输出目录（默认 `output\evidence\`）。

退出码：`0` = 全部通过；`1` = 有用例失败；`2` = 探针自身异常。

证据 JSON：`output\evidence\appearance-settings-probe.json`

```jsonc
{
  "probe": "AppearanceSettingsProbe",
  "purpose": "W3 设置页「意外回写」缺陷包的防回退门槛",
  "total": 33,
  "passed": 33,
  "failed": 0,
  "cases": [
    { "name": "AddCombo 抑制内改选中项不回写", "result": "PASS", "detail": "程序化改动不得写回；实际=keep" }
  ]
}
```

## 已知边界

- 本探针**不能**替代实机端到端复现。W3-1（不可见的「跟随任务栏」复选框）与
  W3-3（设备偏好原值不可用）都需要真实的设置页交互才看得见端到端效果，
  那部分清单在 `docs/superpowers/plans/2026-10-20-appearance-a14-live-checklist.md`。
- `LiteThresholdRow` 的输入框是构造函数的**局部变量**、没有暴露为字段，
  因此「粘贴非法文本不得把阈值改成 0」这条只能靠源码级门槛保证，
  运行时无法直接构造该场景。
- 本工程**不进 sln**，由集成者串行构建后运行；本探针自身不负责构建。
