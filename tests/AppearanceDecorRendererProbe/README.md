# AppearanceDecorRendererProbe（A07 机能装饰 / 进度条自证探针）

A07 的**独立 console 自证工程**：在离屏画布上验证 `src/UI/Decor/DecorRenderer.cs` 与
`src/UI/Decor/BarRenderer.cs` 的行为契约，并输出 JSON 证据。

## 用法

```powershell
# 构建（输出到本项目自己的 output\ 目录）
dotnet build tests/AppearanceDecorRendererProbe/AppearanceDecorRendererProbe.csproj -c Release

# 运行（证据默认落在 output\evidence\decor-renderer-evidence.json）
dotnet tests/AppearanceDecorRendererProbe/output/AppearanceDecorRendererProbe.dll --out tests/AppearanceDecorRendererProbe/output/evidence
```

退出码：`0` = 全部用例通过；`1` = 有用例失败；`2` = 探针自身异常。

## 约束

- **不新增任何 NuGet 依赖**：只 `ProjectReference` 主工程 + BCL。
- **不创建 HardwareMonitor、不 Open 硬件、不做任何屏幕捕获**：全部在离屏 `Bitmap` 上完成。
- **不读也不写用户常用配置目录**：探针自己 `new Settings()` 并注入 `Settings._instance` 单例。
- 输出目录固定为本项目 `output\`，不污染用户实例目录。

## 用例

| # | 用例 | 验证内容 |
|---|---|---|
| 1 | `closed_state_zero_side_effects` | 全部效果关闭 → 0 像素被绘、缓存条目 0、创建资源 0、位图字节 0、零计时器 |
| 2 | `idempotent_100_paints` | 同一 options 连续 Paint 100 次 → 像素哈希完全一致（铺底板 / 不清理两种变体） |
| 3 | `layer_order_text_not_covered` | 扫描线 / 网格不覆盖数值文字（像素采样：实心笔画像素逐一比对） |
| 4 | `bounds_edge_cases` | 1×1 / 零宽高 / 窄窗 160px / FrameWidth=8+FrameLength=64 / 切角超半宽 → 不抛异常、客户区外 0 像素被改动 |
| 5 | `legacy_themes_pixel_identical` | 10 个旧主题 → 新旧路径逐像素一致（哈希相同、差异像素 0） |
| 6 | `horizontal_layout_reuse` | 横屏复用底板 / 底纹 / 外框，不新增组框分组，无条指标不凭空出现条 |
| 7 | `bar_styles_clipping` | 旧条（style=0）与改造前逐像素一致；渐变条 / 纹理条在 0% / 50% / 100% 的裁剪边界正确 |
| 8 | `progress_cache_platform` | 10,000 次持续变化的进度值 → 缓存条目进入有界平台、淘汰 0 |
| 9 | `frame_cost_budget` | 每帧成本（普通档 / 4 卡 + 两倍物理尺寸档），对照 v4 §7.4 初始预算 |

## 说明

- 用例 3 的"文字像素"分成两类：**实心笔画像素**（像素值恰为文字色）必须与"无底纹"时**完全一致**；
  **抗锯齿边缘像素**由 GDI 与底色混合，底纹会改变混合结果（任何背景都会如此，**不是"覆盖文字"**）。
- 用例 9 的装饰层成本用"同一帧、只差开不开装饰层"的方式测（不画文字，避免 GDI 文本绘制的抖动淹没增量）。
  `double_480x1576`（含辉光）超出 §7.4 初始预算时会打印 `[WARN]` 并在 JSON 里写明 `withinBudget=false`
  与 `overageMs`；同档位关闭辉光的实测值见 `double_480x1576_no_glow`。
