# AppearanceDecorProbe —— A05 装饰几何 / 资源缓存自证探针

本工程是外观改造计划 **A05（装饰几何与资源缓存）** 的独立自证程序。

## 它验证什么

| 用例 | 内容 |
|---|---|
| `disabled_features_no_side_effect` | 关闭新能力（FrameStyle/Glow/Scanline/Grid/BarStyle 全 0）时不产生任何绘制、不创建任何位图、缓存条目与字节都为 0 |
| `bounds_edge_cases` | 1×1、0 宽、0 高、零尺寸、窄窗 160px、极端半径（FrameWidth=8 / FrameLength=64）、切角边长超过半宽（60×40 窗口用 64 切角）、2×2、全息样式 1×1 —— 均不抛异常，路径不越界 |
| `no_paint_outside_client` | 在带哨兵色的画布上绘制外框，断言**客户区外 0 个像素**被改动（切角 / 圆角 / 全息三种样式） |
| `progress_10000_cache_and_memory_stable` | 30,000 次持续变化的进度值（分 3 个 10,000 区块）：缓存条目数、缓存字节、创建资源数全程恒定；托管内存增量收敛；工作集增量逐块收敛（14.2MB → 6.5MB → 0.2MB）进入平台期 |
| `cache_limits` | 300 个不同尺寸的槽位 → 条目数 ≤ 128；64 个 4MiB 槽位 → 位图总字节 ≤ 16MiB |
| `invalidation_entry_points` | 主题代次 / DPI / 用户缩放 / 尺寸变化的显式失效入口 |
| `gradient_orientation_evidence` | 渐变角度约定取证（0°=左→右，90°=上→下） |

## 怎么跑

```powershell
dotnet build tests/AppearanceDecorProbe/AppearanceDecorProbe.csproj -c Release
dotnet tests/AppearanceDecorProbe/output/AppearanceDecorProbe.dll --out tests/AppearanceDecorProbe/output/evidence
```

* 退出码：`0` = 全部通过，`1` = 有用例失败，`2` = 探针自身异常。
* 证据 JSON：`tests/AppearanceDecorProbe/output/evidence/decor-evidence.json`。

## 约束

* 不新增任何 NuGet 包；只引用主工程与 BCL。
* 输出固定在 `tests/AppearanceDecorProbe/output/`（`AppContext.BaseDirectory`），不污染用户目录。
* 不创建 HardwareMonitor、不 Open 硬件、不做屏幕捕获、不读写用户配置目录；全部在离屏位图上完成。
* 离屏渲染**不包含** DWM / 窗口呈现 / 桌面合成 / 真实 DPI 窗口行为，
  不能替代 v4 §7.2 要求的真实高 DPI 窗口测试。
