# AppearanceResourceProbe —— A17「释放压力」实测用的资源采样器

本工程给 A17 的判据「**无句柄/内存持续增长**」提供测量手段。

> A17 此前没有任何可用的测量手段 —— 这条判据只能靠人工看任务管理器。
> 本采样器把「句柄 / GDI 对象 / USER 对象 / 内存」变成一条可复核的时间序列。

## 它做什么

给定一个**正在运行**的 LiteMonitor 进程，按固定间隔采样：

| 指标 | 来源 |
|---|---|
| 句柄数 | `Process.HandleCount` |
| GDI 对象数 | `GetGuiResources(hProcess, GR_GDIOBJECTS)` |
| USER 对象数 | `GetGuiResources(hProcess, GR_USEROBJECTS)` |
| 私有内存 / 工作集 | `Process.PrivateMemorySize64` / `WorkingSet64` |

输出：时间序列（CSV + JSON）+ **后半段斜率**（个/分钟）。

> ★ 斜率只看**后半段**，避开启动/预热瞬态 ——
> 这与 A15 探针「CPU 按处理器时间、p95 按经过时间」是同一个思路：**只看稳定态**。

## 用法

```powershell
# 采样一个正在运行的 LiteMonitor（60 秒，间隔 1 秒）
tests/AppearanceResourceProbe/output/AppearanceResourceProbe.exe `
    --name LiteMonitor --label "空闲基线" --duration-sec 60 `
    --out docs/reviews/evidence/a17-release/idle

# 或按 pid
tests/AppearanceResourceProbe/output/AppearanceResourceProbe.exe --pid 12345 --label "换肤 x30"
```

| 参数 | 说明 |
|---|---|
| `--pid` | 目标进程 id |
| `--name` | 目标进程名（取内存占用最大的那个实例，避免选到更新器之类的辅助进程） |
| `--label` | 场景标签，写进证据（默认 `unspecified`） |
| `--interval-ms` | 采样间隔，默认 1000 |
| `--duration-sec` | 采样时长，默认 60 |
| `--out` | 证据目录，默认 `<可执行文件目录>\evidence` |

退出码：`0` = 采样完成；`1` = 参数/环境错误；`2` = 采样过程中出错。

证据：

- `resource-samples.csv` —— 时间序列（index / elapsed_ms / handle_count / gdi_objects / user_objects / private_bytes / working_set_bytes）
- `resource-samples.json` —— 样本 + 起末值 + 各指标的斜率 + 阈值说明

## 判读阈值

| 指标 | 阈值 | 含义 |
|---|---|---|
| 句柄后半段斜率 | **≤ 5 个/分钟** | 超过即判「句柄持续增长」 |
| GDI 后半段斜率 | **≤ 5 个/分钟** | 超过即判「GDI 持续增长」 |
| USER 后半段斜率 | **≤ 5 个/分钟** | 超过即判「USER 持续增长」 |
| 单场景私存增量 | **≤ 30 MB** | 超过即判「内存持续增长」 |
| **空闲基线**斜率 | **必须为 0** | 空闲时任何持续增长都是 bug |

> 空闲基线是**最灵敏**的一项：什么都不做时如果计数还在涨，不用再分场景排查，直接判不通过。

## 两个必须知道的限制

1. **权限**：LiteMonitor 主工程是 `requireAdministrator`。目标进程以管理员身份运行时，
   采样器**也必须以管理员身份运行**，否则 `OpenProcess` 拿不到进程句柄，
   读不到 GDI/USER 计数。
   探针在这种情况下会明确报「OpenProcess 失败（错误码 N）」并提示提权 ——
   **不会静默给出假数据**。
2. ★ 本工程**故意不引用主工程**（只用 BCL + 三个 Win32 API）：
   - 编译不碰主工程的 `obj/` ⇒ **可以与其它会话的 build/test 并发执行**而不争抢；
   - 跑起来不加载 LiteMonitor 的任何类型 ⇒ 不碰硬件、不抓屏；
   - 只**读取**目标进程的计数，不注入、不修改目标进程。

## 已知边界

- 本工具**只测量，不判定业务语义**。「这个增长是不是 bug」需要人工结合场景判断 ——
  场景清单、靶点与判定标准见
  `docs/superpowers/plans/2026-10-21-appearance-a17-release-pressure.md`。
- 采样器本身**未**验证「目标进程以管理员身份运行」这一路径
  （自测目标是普通权限的 `explorer.exe`）。实测时若报 `OpenProcess 失败`，用管理员身份重跑即可。
- 采样期间目标进程必须**保持运行**；进程退出后采样器会保留已采到的样本并继续（读数可能为 0）。
- 本工程**不进 sln**，由集成者串行构建后运行；本探针自身不负责构建。
