# AppearanceThemeCheck（A12 主题参数校验）

独立 console 工程，校验 A12 交付的机能风主题 `CyberPunk_Mech` 及其两个可读性夹具。

## 设计约束

- 不新增任何 NuGet 依赖：只引用主工程 `LiteMonitor.csproj` 与 BCL。
- 不参与 `LiteMonitor.sln`（由主集成者按需接入）。
- 全程纯 CPU 判定：**不做屏幕捕获、不移动/隐藏窗口、不改窗口透明度**。
- 输出目录为本项目下的 `output\`（即 `AppContext.BaseDirectory`），往返保存的临时主题落在 `output\roundtrip\`，不污染用户的主题目录。
- 主工程已有 `<Compile Remove="tests\**" />`，本工程不会被主项目编译进去。

## 运行

```powershell
dotnet build tests\AppearanceThemeCheck\AppearanceThemeCheck.csproj -c Release
& '.\tests\AppearanceThemeCheck\output\AppearanceThemeCheck.exe'
```

退出码：`0` = 全部通过；`1` = 有失败项；`2` = 找不到仓库根目录。

## 校验项

| 组 | 内容 |
| --- | --- |
| 基线 | 10 个旧主题的 SHA256 前 16 位与 A00 基线表逐一对齐；主题目录文件集合不越界 |
| 参数 | 磁盘文件里的参数与 `ThemeSpec.cs` 里的期望参数表逐项相等 |
| 归一化 | `ThemeAppearanceOptions.Create` 在 4 组缩放/尺寸下无意外夹紧；归一化不回写原 `Theme` 对象 |
| 背景规则 | 非 `solid` 模式必须显式写 `opacity`；`opacity>0` 必须显式写 `tint`；透明 `tint` 不破坏 glass → gradient → solid 降级链 |
| 往返 | 保存后再加载所有字段值不变；源文件显式写出的键没有丢失；保存结果无重复键 |
| 可读性 | 按最坏合成面计算 WCAG 对比度并对照判据表（文本 ≥ 4.5、非文本 ≥ 3.0、组块可分 ≥ 1.2） |

## 目录

- `AppearanceThemeCheck.csproj` —— 工程定义
- `Program.cs` —— 校验主流程
- `ThemeSpec.cs` —— 期望参数表（与参数说明文档同源，改一边必须改另一边）
- `ContrastModel.cs` —— 承载面合成 + WCAG 对比度模型
- `fixtures/CyberPunk_Mech_Light.json` —— 亮底夹具
- `fixtures/CyberPunk_Mech_Complex.json` —— 复杂背景夹具（渐变 + 染色 + 噪点 + 边缘辉光）
