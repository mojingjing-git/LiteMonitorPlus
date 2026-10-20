# A11 —— Web 配色适配（网页端跟随桌面主题）

> 计划：[2026-10-20-appearance-agent-execution.md](2026-10-20-appearance-agent-execution.md)（v4）
> 基线：[2026-10-20-appearance-a00-baseline.md](2026-10-20-appearance-a00-baseline.md)
> 执行人：Web 线 worker
> 状态：已完成（代码 + 自动化测试全部通过；未做真实浏览器截图验收）

---

## 1. 实际基线

| 项 | 值 |
|---|---|
| HEAD | `97f793dccac6d5ab37120602ecab4f6238a855da`（`merge: performance sampling hardening`） |
| 分支 | `master` |
| 主程序集 SHA256（跑完本包后构建产物） | `8F6463FA46F6A8AED01E3A747AD13983CD1F47C227E1C1EBF6683A9F5CA76A24` |
| `src/System/WebServer/WebThemeCss.cs` SHA256 | `5F75B2F2C5E04AED3099BB241A8CFD7083B571635C4547BBC5065602F32E28B4`（17902 字节） |
| `src/System/WebServer/WebSessionManager.cs` SHA256 | `C6289AEEA0D0AF2CAF6D4D872F5AD8DE2E87C03343F931A7E767D1BC7D029F1D` |
| `src/System/WebServer/WebPageContent.cs` SHA256 | `7EFA50F409B154B73F6EBD473BF09DE42FEFF9F9C3B18A15F0AB4D36B01A3B49` |
| `tests/LiteMonitor.Tests/WebThemeCssTests.cs` SHA256 | `F51A699F34FB9CDBF82FAF810E899EB06EACC1D0494AE0F275B1DBA9519D687D`（24049 字节） |

> **程序集哈希口径**：本机有多个 agent 并行构建，主程序集里同时包含**其它线在写**的
> `src/System/Backdrop/**`、`src/UI/Decor/**` 等未跟踪新文件，
> 因此该哈希**不能**单独用来复现本包；本包的复现依据是下表 4 个文件的内容哈希 + §7 的 diff。
>
> **本包未做的事**：没有 `git add` / `git commit` / `git stash`，没有改 `.gitignore`，
> 没有碰 `LiteMonitor-old-config-20261020.zip`（用户备份，全程未读写），
> 没有改任何 csproj / sln、没有新增 NuGet 依赖，
> 没有改 `src/Core/**`、`src/UI/**`、`src/System/HardwareServices/**`、`src/System/InfoService/**`、
> `src/System/WebServer/LiteWebServer.cs`、`tests/LiteMonitor.Tests/` 里的其它文件、
> `tests/LiteMonitor.PerformanceProbe/**`、`docs/APPEARANCE_PLAN.md`。
> `src/Core/ThemeAppearanceOptions.cs`（A01/A03 已冻结）**未做任何改动**。

---

## 2. 修改文件清单

| 文件 | 性质 | 说明 |
|---|---|---|
| `src/System/WebServer/WebThemeCss.cs` | **新增** | 不可变颜色快照 `WebThemeColorSnapshot` + 严格颜色解析 + CSS 生成 `WebThemeCss` + 发布点 `WebThemeColorPublisher` |
| `src/System/WebServer/WebSessionManager.cs` | 修改 | **仅挂载点**：HTML 组装处多拼一段 CSS 变量块（+10 行注释 +1 行取值 +1 行 Replace） |
| `src/System/WebServer/WebPageContent.cs` | 修改 | **仅挂载点**：新增占位符常量 `ThemeCssPlaceholder` + 模板 `<style>` 里加一行 `{{THEME_CSS}}` |
| `tests/LiteMonitor.Tests/WebThemeCssTests.cs` | **新增** | 15 个 xUnit 用例（规范化 / ARGB / 回退 / 注入 / invariant / 无硬件需求 / 刷新跟随） |
| `docs/superpowers/plans/2026-10-20-appearance-a11-web.md` | **新增** | 本结果文档 |

diff 规模（`git diff --stat src/System/WebServer/`）：**+22 / −2**（两处 −1 是被改写的那两行的旧内容，
新内容见 §7）。

---

## 3. 公共 API 与接线说明

### 3.1 新增类型（全部在 `LiteMonitor.src.WebServer` 命名空间）

#### `WebThemeColorSnapshot`（不可变颜色快照）

| 成员 | 签名 | 说明 |
|---|---|---|
| 工厂 | `static WebThemeColorSnapshot FromTheme(Theme? theme)` | **发布方**用：从主题取颜色、严格解析、非法值走回退色；只读主题，**不改主题一个字节** |
| 工厂 | `static WebThemeColorSnapshot Create(Color background, Color groupBackground, Color border, Color textPrimary, Color textSecondary, Color valueSafe, Color valueWarn, Color valueCrit)` | 供 UI 层直接用已解析好的运行时颜色发布 |
| 解析 | `static bool TryParseThemeColor(string? raw, out Color color)` | 严格解析主题色串；失败返回 false，**绝不回传原文** |
| 只读属性 | `Background` / `GroupBackground` / `Border` / `TextPrimary` / `TextSecondary` / `ValueSafe` / `ValueWarn` / `ValueCrit` | 全部是 `System.Drawing.Color`（值类型 → 天然不可变） |
| 静态 | `Default` | 全回退快照（未发布快照时页面用） |
| 静态 | `FallbackBackground` / `FallbackGroupBackground` / `FallbackBorder` / `FallbackTextPrimary` / `FallbackTextSecondary` / `FallbackValueSafe` / `FallbackValueWarn` / `FallbackValueCrit` | 写死的回退色 |

#### `WebThemeCss`（CSS 生成，消费方）

| 成员 | 签名 | 说明 |
|---|---|---|
| 生成 | `static string BuildCssVariables(WebThemeColorSnapshot? snapshot)` | 输出 `:root { --bg: rgba(...); ... }` 变量块；传 null → 用 `Default` |
| 规范化 | `static string ToCssColor(Color color)` | `Color` → `rgba(r,g,b,a)`；r/g/b 为 0–255 整数，a 为 0–1 小数（最多 3 位），**invariant culture** |

#### `WebThemeColorPublisher`（发布点）

| 成员 | 签名 | 说明 |
|---|---|---|
| 读 | `static WebThemeColorSnapshot? Current` | 当前已发布快照（`Volatile.Read`，线程安全） |
| 写 | `static void Publish(WebThemeColorSnapshot? snapshot)` | UI 层换肤时调用；传 null = 撤销发布 |
| 写 | `static void PublishFromTheme(Theme? theme)` | 一行接法：`Publish(FromTheme(theme))` |
| 写 | `static void Clear()` | 撤销发布 |

#### `WebPageContent` 新增常量

| 成员 | 值 | 说明 |
|---|---|---|
| `public const string ThemeCssPlaceholder` | `"{{THEME_CSS}}"` | 模板里的挂载点常量（模板与替换点用同一个常量，不会写错字面量） |

### 3.2 A13 怎么发布快照（接线说明）

**唯一接法**（在 UI 层换肤路径里，一行）：

```csharp
// 换肤成功后（主题对象已替换完成）
WebThemeColorPublisher.PublishFromTheme(ThemeManager.Current);
```

或显式一点：

```csharp
WebThemeColorPublisher.Publish(WebThemeColorSnapshot.FromTheme(ThemeManager.Current));
```

**要点**：

- Web 层**不会**自己 new 主题管理器、**不会**读主题文件、**不会**轮询颜色；
  `WebThemeColorPublisher.Current` 只在 `WebSessionManager` 生成 HTML 的那一刻被读一次。
- 发布点必须由 **UI 层**（A13 拥有的换肤路径）调用。`LiteWebServer` 创建 `WebSessionManager`，
  两者之间没有实例引用关系，所以这里用**静态发布点**而不是构造函数注入 —— A13 不需要改
  `LiteWebServer.cs`（该文件不在我的写集内，也没有被我改动）。
- 想接到 `ThemeAppearanceOptions`（A03 冻结的运行时快照）也可以：
  `WebThemeColorSnapshot.Create(opt.BackgroundColor, opt.GroupBackgroundColor, opt.BorderColor, …)`。
  但要注意 `ThemeAppearanceOptions` **没有** TextPrimary / ValueSafe / ValueWarn / ValueCrit，
  这四项仍需从 `theme.Color` 取 —— 所以默认推荐 `FromTheme`。
- 换主题后**不推送、不轮询**：网页端的 WebSocket 广播载荷里**没有**主题颜色，
  也没有任何颜色轮询定时器（有用例守住，见 §6 用例 14、15）。用户**刷新浏览器**即取到新配色。

### 3.3 A14 怎么验收

| 验收项 | 怎么看 |
|---|---|
| 配色跟随主题 | 桌面换主题 → 浏览器刷新 → 页面背景/卡片/边框/文字/三色值随之改变（变量：`--bg` `--card` `--border` `--text-main` `--text-sub` `--c-0` `--c-1` `--c-2`） |
| 未发布快照时零变化 | 不调用 `Publish` 时，页面 CSS 里的变量值与改造前的默认值**逐字相同**（`--bg: rgba(9,9,11,1)` = 旧 `#09090b`） |
| 刷新即生效（刻意取舍） | 已打开的页面**不会**自动变色；这是 M1 方案 B 的**刻意产品取舍**，不是缺陷（v4 §7.3 已裁决） |
| 零注入 | 见 §6 用例 9/10：15 组注入载荷全部走回退色，输出 CSS 里不含 `< > " ' / *`，且不出现可执行上下文 |
| 无硬件需求 | 见 §6 用例 12：HTML / favicon 请求后采样入口调用数 = 0，需求状态仍为"无需求" |
| 鉴权/会话/连接零改动 | `git diff src/System/WebServer/`（§7）+ 全量 544 个用例全绿 |

---

## 4. 设计要点（为什么这么写）

1. **不可变快照**：`Color` 是值类型，快照属性全部只读 → 任何线程读到的都是同一份内容；
   发布用 `Volatile.Write`、读取用 `Volatile.Read`。
2. **规范化只作用于运行时副本**：解析结果全部落在新建的快照里，
   `Theme` / `ColorConfig` 对象**不被修改**（有用例守住：非法色值测试后主题字符串不变）。
3. **ARGB 语义不搞反**：主题的 8 位十六进制是 `#AARRGGBB`（alpha 在前），
   快照阶段按 ARGB 拆成 `Color`；CSS 侧只输出 `rgba(r,g,b,a)`，**根本不输出十六进制**，
   所以"把 `#AARRGGBB` 当成 CSS 的 `#RRGGBBAA`"这条错误路径在实现上不存在。
4. **注入面为零**：生成的 CSS 文本只可能包含固定标识符、整数与 `rgba(...)` 字面量；
   主题原文在解析阶段就被拒绝（回退色），**不会**进入 `<style>`。
   → 不需要"转义"就已经安全（转义在这里是多余的，因为原文根本走不到输出）。
5. **alpha 语义统一**：一律 `rgba()`，a 是 0–1 小数；
   `rgba()` 输入的 a×255 按 `AwayFromZero` 舍入（与主题模型 `ThemeManager.ParseColor` 一致）。

---

## 5. 颜色映射表（主题 → 网页变量）

| CSS 变量 | 主题字段 | 回退色（未发布 / 非法） |
|---|---|---|
| `--bg` | `Color.Background` | `rgba(9,9,11,1)` |
| `--card` | `Color.GroupBackground` | `rgba(20,20,23,1)` |
| `--border` | `Color.Border`（可空） | `rgba(39,39,42,1)` |
| `--text-main` | `Color.TextPrimary` | `rgba(244,244,245,1)` |
| `--text-sub` | `Color.TextSecondary`（可空） | `rgba(113,113,122,1)` |
| `--c-0` | `Color.ValueSafe` | `rgba(16,185,129,1)` |
| `--c-1` | `Color.ValueWarn` | `rgba(245,158,11,1)` |
| `--c-2` | `Color.ValueCrit` | `rgba(239,68,68,1)` |

---

## 6. 测试覆盖与结果

测试文件：`tests/LiteMonitor.Tests/WebThemeCssTests.cs`（15 个用例，xUnit）。

| # | 用例 | 覆盖的要求 |
|---|---|---|
| 1 | `正常主题_快照生成正确的CSS变量` | rgba 的 r/g/b/a 数值正确、8 个变量、结构完整 |
| 2 | `颜色名_按已知颜色解析` | 颜色名写法 |
| 3 | `未发布快照_使用写死回退配色_与改造前默认一致` | 未发布 → 页面外观零变化 |
| 4 | `八位十六进制_按ARGB拆解_不是CSS的RRGGBBAA` | `#AARRGGBB` 按 ARGB 拆；显式排除 `rgba(128,17,34,…)` 的错误解读；输出里没有 `#` |
| 5 | `六位十六进制_按RGB解析_不透明` | `#RRGGBB` 正确 |
| 6 | `rgba主题写法_alpha为0到1` | `rgba(r,g,b,a)` 输入 |
| 7 | `非法色值_走明确回退色_不抛异常_不把原文插入` | 14 个非法值（空/空白/非法 hex/位数错/越界/NaN/Infinity/未知名/`url(javascript:)`）→ 回退色 |
| 8 | `非法色值_不修改原主题对象` | 归一化不回写主题 |
| 9 | `注入输入_不会成为注入内容_且变量块结构完整` | 15 组注入载荷（`</style>`、`</style >`、`</STYLE>`、`"><script>alert(1)</script>`、`<script>…`、`</title>`、`<!--`、`/*`、`*/`、`url(javascript:…)`、`javascript:…`、`';--`、`red;}body{display:none`、带 `\r\n` 的闭合样式、带 `\n` 的闭合样式） |
| 10 | `模板_含有主题CSS占位符` | 占位符存在 |
| 11 | `区域设置_小数逗号_输出不变` | `de-DE`（系统无该区域设置时退化为等效自定义设置）下输出逐字相同 |
| 12 | `HTML与图标请求_不产生任何硬件采样需求` | 真实回环 HTTP：`/` → 200 且含主题变量；`/favicon.ico` → 404；**采样入口调用数 = 0**；`HasWebDataDemand` / `AuthenticatedConnectionCount` / `HasHttpLease` 全为"无需求" |
| 13 | `换主题后_重新生成HTML得到新配色` | 刷新即跟随（不推送不轮询） |
| 14 | `快照只在HTML组装处被读取_广播与采样路径不引用它` | 源码级：去注释后 `WebThemeColorPublisher` 全文**只出现 1 次**，且在 HTML 组装分支里、**不在** `/api/snapshot` 分支里 |
| 15 | `新增的CSS代码_没有任何定时器或轮询` | 源码级：新增文件里没有 `Timer` / `Task.Delay` / `Thread.Sleep` |

### 6.1 命令与退出码

```powershell
cd G:\TOOL\LiteMonitor-fork

# 1) 主工程构建（第 3 次尝试成功；前两次因其它线在写文件而失败，见 §8）
dotnet build LiteMonitor.csproj -c Release -v minimal --nologo
# → 0 个警告 / 0 个错误 / 已用时间 00:00:01.21   EXIT=0

# 2) 本包用例
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --filter "FullyQualifiedName~WebThemeCssTests" --nologo -v minimal
# → 已通过! - 失败: 0，通过: 15，已跳过: 0，总计: 15   EXIT=0

# 3) 全量回归（确认鉴权/会话/连接零影响）
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-build --nologo -v minimal
# → 已通过! - 失败: 0，通过: 544，已跳过: 0，总计: 544，持续时间 49 s   EXIT=0
```

### 6.2 注入测试与规范化结论

- **零注入**：15 组注入载荷分别写进 8 个主题颜色字段，生成的 CSS 里
  **不出现** `<` `>` `"` `'` `/` `*` `url(` `javascript`；整页 HTML 里 `</style>` / `<script` / `<!--`
  的数量与基线页面**完全一致**（没有多出任何可执行上下文）；主题原文不出现在输出中。
  → 结论：**注入面为零**（原文走不到输出，不是靠转义兜住的）。
- **规范化正确**：`#80112233` → `rgba(17,34,51,0.502)`（ARGB 拆解正确，
  若按 `#RRGGBBAA` 会得到 `rgba(128,17,34,0.2)`，用例显式排除）；
  `#112233` → `rgba(17,34,51,1)`；输出中**不出现任何十六进制**。
- **invariant**：de-DE 下输出与 invariant 下**逐字相同**（小数点仍是 `.`）。

---

## 7. 改动 diff（自证：鉴权 / 会话代次 / 连接管理零改动）

`git diff --stat src/System/WebServer/` → `+22 / −2`。

`src/System/WebServer/WebSessionManager.cs`（`WebSessionManager` → `HandleHttpRequestAsync` → HTML 分支）：

```diff
                 // 默认返回首页（仅取 HTML：不产生硬件需求）
                 string iconBase64 = WebPageContent.GetAppIconBase64();
                 bool authRequired = !string.IsNullOrEmpty(Settings.Load().WebServerPassword);
-                
+
+                // ★ A11 挂载点：主题颜色快照 → CSS 变量。
+                //   · 只**消费**UI 层发布的不可变快照（WebThemeColorPublisher.Current）：
+                //     这里不解析主题、不读主题文件、不碰硬件；
+                //   · 未发布快照时用写死的回退配色 → 页面外观与改造前一致；
+                //   · 刻意**不推送、不轮询**：换主题后刷新浏览器即取到新配色。
+                string themeCss = WebThemeCss.BuildCssVariables(WebThemeColorPublisher.Current);
+
                 string html = WebPageContent.IndexHtml
                     .Replace("{{FAVICON}}", …)
-                    .Replace("{{AUTH_REQUIRED}}", authRequired.ToString().ToLower());
+                    .Replace("{{AUTH_REQUIRED}}", authRequired.ToString().ToLower())
+                    .Replace(WebPageContent.ThemeCssPlaceholder, themeCss);
```

`src/System/WebServer/WebPageContent.cs`（`WebPageContent` → 类体 + `IndexHtml` 模板）：

```diff
     public static class WebPageContent
     {
+        /// <summary>
+        /// 主题 CSS 变量块的占位符（A11 挂载点）。
+        /// 由 <c>WebSessionManager</c> 在组装 HTML 时替换成
+        /// <c>WebThemeCss.BuildCssVariables(...)</c> 的结果。
+        /// </summary>
+        public const string ThemeCssPlaceholder = "{{THEME_CSS}}";
+
@@  (IndexHtml 模板的 <style> 段内)
             --c-2: #ef4444; /* Red */
         }
 
+        /* --- 运行时主题变量（挂载点：由 WebThemeCss 在服务端注入） ---
+           UI 层发布颜色快照后，这里会被替换成一段 :root 变量块。
+           未发布快照时保持上面那组默认值 → 页面外观与注入前完全一致。 */
+        {{THEME_CSS}}
+
```

**鉴权（`CheckAuth`）、快照租期（`GrantHttpLease`）、共享采样（`RequestSharedSamplingWithTimeoutAsync`）、
会话代次（`_generation` / `connectionGeneration`）、连接登记与回收（`AddClient` / `RemoveClient`）
—— 一行未改。** 上面的 diff 即为全部改动。

---

## 8. 未验证项与已知限制

### 8.1 未验证项

1. **未做真实浏览器渲染验收**：没有启动 Web 服务、没有用浏览器打开页面截图对比。
   自动化测试验证的是"服务端生成的 HTML/CSS 内容"，**不是**浏览器里的最终像素。
   → A14 需要补一次真实浏览器验收（至少 2 个主题 × 亮/暗背景）。
2. **未验证"刷新后跟随主题"的端到端人工路径**：用例 13 验证的是"发布新快照 → 重新生成 HTML → 得到新配色"，
   浏览器层面的"按 F5 生效"由 A14 人工确认。
3. **未做多主题批量遍历**：没有把 10 个旧主题 JSON 逐个灌进快照核对颜色。
   → 建议 A14 用 `WebThemeColorSnapshot.FromTheme(ThemeManager.Load(<主题名>))` 逐个生成，
   对照主题里的色值人工抽查（尤其带 alpha 的主题）。
4. **未验证深色/浅色主题下的对比度**：网页端没有做 WCAG 对比度检查。
5. **未测量性能影响**：HTML 组装多了一段字符串拼接，未做基准测量（预计影响 < 0.1ms/请求）。

### 8.2 已知限制

1. **只跟随"主色"**：网页模板里若干**写死**的次级颜色（`.tag` 背景 `#1f1f22`、
   `.ring-bg` / `.d-bar-bg` 的 `#27272a`、`.inp` 的 `#000`/`#fff`、`.btn` 的 `#000`、
   `.dash-item` 的 `rgba(255,255,255,0.03)`、`.modal-overlay` 的 `rgba(0,0,0,0.8)`）
   **不跟随主题**。改这些属于"改 CSS 规则"，超出本包"仅挂载点"的写集约束，留给 A13/A14 决定。
2. **未知颜色名的行为与桌面略有差异**：主题里写了一个 .NET 不认识的颜色名时，
   桌面渲染为"透明黑"（旧行为），网页端走**明确回退色**。这是 v4 §7.3
   "未知/非法色值用明确回退"的要求，属**有意差异**。
3. **alpha 精度**：`rgba()` 输入的 a 会被量化到字节（a×255），再转回 0–1 时最多 3 位小数。
   例：`rgba(17,34,51,0.5)` → 存为 a=128 → 输出 `0.502`（视觉上无差别，但**不是**逐字 0.5）。
4. **`WebThemeColorPublisher` 是进程级静态状态**：换主题后必须重新发布；
   若 UI 层漏发，网页端会继续用上一次的快照（不会自动回退到默认值）。
   A13 接线时需保证"每次换肤都发布一次"（含启动时的首次发布）。
5. **构建曾因并行占用失败**：第 1、2 次 `dotnet build` 失败，错误全部来自**其它线当时在写**的
   `src/System/Backdrop/BackdropGradient.cs`（`LinearGradientBrush` 找不到）、
   `src/System/Backdrop/BackdropNoiseCache.cs`、`src/UI/Decor/DecorPainter.cs`、
   `src/UI/Decor/DecorTextureFactory.cs`。这些文件**不在本包写集内，我没有改动它们**；
   第 3 次重试时对方已修好，构建通过（0 警告 0 错误）。
   → **建议主集成者串行复跑一次** `dotnet build LiteMonitor.sln -c Release` 做最终确认。

---

## 9. 证据路径

| 证据 | 位置 |
|---|---|
| 新增实现 | `src/System/WebServer/WebThemeCss.cs` |
| 挂载点 1 | `src/System/WebServer/WebSessionManager.cs` → `WebSessionManager` → `HandleHttpRequestAsync`（HTML 分支） |
| 挂载点 2 | `src/System/WebServer/WebPageContent.cs` → `WebPageContent` → `IndexHtml` / `ThemeCssPlaceholder` |
| 自动化测试 | `tests/LiteMonitor.Tests/WebThemeCssTests.cs` → `WebThemeCssTests`（15 个用例） |
| 改动 diff | `git diff -- src/System/WebServer/`（本文件 §7 已摘录） |
| 主题模型参考 | `src/Core/ThemeManager.cs` → `ThemeManager` → `ParseColor` / `ParseRgbaColor`（未改动，仅对齐语义） |
| 冻结合同参考 | `src/Core/ThemeAppearanceOptions.cs`（未改动） |
