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
| `bar_texture_tile_diagonal_continuity` | 进度条纹理瓦片的斜纹形态：周期必须整除边长、内部（去掉 2 像素边缘）必须有斜纹、斜线必须属于同一相位族（`(x+y) mod 周期` 落在窄窗口内）、平铺 3×3 后沿 (x+1, y−1) 方向断链数为 0 |
| `texture_resource_ownership` | 纹理资源所有权：**缓存取出的画刷必须真的能画出内容**（`TextureBrush` 不复制源位图，源位图必须与画刷同生共死）、8 条纹理淘汰后缓存计数与字节归零、创建数 = 释放数、淘汰后的画刷已失效 |
| `texture_brush_failure_containment` | 纹理画刷的**异常安全**：①用 internal 接缝注入"画刷构造失败"（**不靠耗尽内存**）——源位图必须已释放（释放后再访问抛异常）、条目数/字节数/创建数/创建字节/释放数/淘汰数全部**回到调用前**、同一 key 再次请求必须成功；②注入"入表失败"——**已创建的画刷与源位图都必须被释放**（画刷画不出像素或填充抛异常）、计数同样回到调用前、重试成功；③纹理路径的条目数/字节上限与"超大条目优先淘汰"例外仍然成立 |

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

## 故障注入接缝（internal）

`texture_brush_failure_containment` 用到主工程 `DecorResourceCache` 上的两个 **internal** 测试接缝
（生产路径上**没有**可被外部随意改写的公开开关）：

| 接缝 | 作用 |
|---|---|
| `TextureBrushFactory` | 替换"从源位图创建纹理画刷"这一步（默认 `new TextureBrush(source)`），用来稳定触发**画刷构造失败** |
| `TextureBrushPreInsertHook` | 在"画刷已创建、条目尚未入表"这一步注入异常，用来触发**入表失败** |

本探针工程当前没有 `InternalsVisibleTo` 授权，因此通过
`tests/AppearanceDecorProbe/DecorCacheFailureHooks.cs` **用反射安装**这两个接缝（接缝名固定）。
主工程在 `LiteMonitor.csproj` 补上 `<InternalsVisibleTo Include="AppearanceDecorProbe" />`
之后即可改为直接调用，接缝名字保持不变。
