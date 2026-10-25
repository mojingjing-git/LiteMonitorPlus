# ============================================================================
# LiteMonitor++ 取证场景矩阵（P-F15 / 上游 issue #416）
#
# ★ 用途：为「CPU Usage (System)」火焰图 + DPC/ISR 采样提供**分场景开关清单**。
#   每个场景只改**一个**变量，其余保持默认，便于把 System 进程的内核 CPU 归因到具体开关。
#
# ★ 原则：
#   1. 场景只写 `settings.json` 里**真实存在**的字段（见 src/Core/Settings.cs）。
#      产品没有的开关，在这里显式记 `lever: none` + 说明，**不臆造**配置项。
#   2. `launchApp=false` 的场景用于取「应用完全关闭时 System 进程」的基线。
#   3. 每个场景都是**独立**的一次采集；跨场景比较前先看 manifest 里的环境字段是否一致。
#   4. 采集命令：pwsh tools/forensics/wpa-capture.ps1 -Scenario <id>
#
# ★ 字段说明：
#   launchApp         是否启动 LiteMonitor++（false = 应用关闭基线）
#   settings          写进**沙箱** settings.json 的覆盖项（$null = 用默认配置）
#   captureMinutes    本场景建议采集时长（分钟）
#   warmupMinutes     启动后到开始采集之间的静默期（分钟）—— 让预热/首轮建图完成
#   levers            本场景实际动到的产品开关 + 代码位置
#   nonLevers         **产品没有**对应开关、无法用配置表达的部分（诚实标注）
#   purpose           这个场景想隔离掉/放大什么内核成本
# ============================================================================

@{
    # ── 基线 0：应用完全关闭 ────────────────────────────────────────────────
    'app-closed' = @{
        title            = '应用完全关闭（System 进程基线）'
        launchApp        = $false
        settings         = $null
        captureMinutes   = 30
        warmupMinutes    = 0
        purpose          = '取"机器本身的" System 进程内核 CPU 基线（驱动 DPC/ISR、后台服务等）。'
        levers           = @('（不启动应用，无配置）')
        nonLevers        = @('—')
        compareWith      = @('default')
    }

    # ── 基线 1：默认配置 ────────────────────────────────────────────────────
    'default' = @{
        title            = '默认配置'
        launchApp        = $true
        settings         = $null
        captureMinutes   = 30
        warmupMinutes    = 2
        purpose          = '与 app-closed 的差值 = LiteMonitor++ 造成的**全部**系统侧增量。'
        levers           = @('（不覆盖任何字段，走 src/Core/Settings.cs 的属性默认值）')
        nonLevers        = @('—')
        compareWith      = @('app-closed')
    }

    # ── SMB 共享速率计数器 ──────────────────────────────────────────────────
    'smb-on' = @{
        title            = 'SMB 共享速率计数器开启'
        launchApp        = $true
        settings         = @{ IgnoreSmbTraffic = $false }
        captureMinutes   = 30
        warmupMinutes    = 2
        purpose          = '隔离「每轮采集读 4 个 SMB 共享速率性能计数器」的内核成本（默认关闭该扣除）。'
        levers           = @(
            'IgnoreSmbTraffic=false  → src/Core/Settings.cs:153',
            '  生效点：src/System/HardwareServices/NetworkManager.cs:545（开启且计数器就绪时建立 smbEstimator）'
        )
        nonLevers        = @('SMB 计数器是 Windows 性能计数器，不能再细分到"每个共享"级别')
        compareWith      = @('default')
    }

    'smb-off' = @{
        title            = 'SMB 共享速率计数器关闭（显式写出，默认值）'
        launchApp        = $true
        settings         = @{ IgnoreSmbTraffic = $true }
        captureMinutes   = 30
        warmupMinutes    = 2
        purpose          = '与 smb-on 成对，确认 SMB 扣除带来的差值可复现。'
        levers           = @('IgnoreSmbTraffic=true → src/Core/Settings.cs:153')
        nonLevers        = @('—')
        compareWith      = @('smb-on')
    }

    # ── FPS（PresentMon / 外部采集服务）────────────────────────────────────
    'fps-on' = @{
        title            = 'FPS 采集开启'
        launchApp        = $true
        settings         = @{ WebFpsEnabled = $true }
        captureMinutes   = 30
        warmupMinutes    = 3
        purpose          = '隔离「FPS 采集服务（外部 PresentMon / ETW 会话）」带来的内核 ETW 成本。'
        levers           = @(
            'WebFpsEnabled=true → src/Core/Settings.cs:269（网页端产生 FPS 采集需求）',
            '面板 FPS 监控项：Settings.MonitorItems 中的 "FPS" 项'
        )
        nonLevers        = @(
            '★ FPS 采集**依赖外部程序**（resources/assets 下的 LiteMonitorPlusFPS.exe / PresentMon），',
            '  需要用户先安装并在设置页完成组件检查；配置本身拉不起它。',
            '  ⇒ 采集前请确认 FPS 服务真的在跑（面板 FPS 有数值），否则本场景退化为 default。'
        )
        compareWith      = @('default')
    }

    'fps-off' = @{
        title            = 'FPS 采集关闭（显式写出）'
        launchApp        = $true
        settings         = @{ WebFpsEnabled = $false }
        captureMinutes   = 30
        warmupMinutes    = 2
        purpose          = '与 fps-on 成对，量化 FPS/ETW 的内核开销。'
        levers           = @('WebFpsEnabled=false → src/Core/Settings.cs:269')
        nonLevers        = @('—')
        compareWith      = @('fps-on')
    }

    # ── GPU 采样 ────────────────────────────────────────────────────────────
    'gpu-all' = @{
        title            = '监控全部显卡（GPU 采样面最大）'
        launchApp        = $true
        settings         = @{ MonitoredGpuCount = 4 }
        captureMinutes   = 30
        warmupMinutes    = 3
        purpose          = '隔离「每轮 hw.Update() 触发的显示驱动调用」的量级（多卡机上按实际卡数再调大 MonitoredGpuCount）。'
        levers           = @(
            'MonitoredGpuCount=N → src/Core/Settings.cs:291',
            '  生效点：src/System/HardwareServices/SensorMap.cs:126-146（SelectMonitoredGpus）'
        )
        nonLevers        = @(
            '★ **产品没有"完全关闭 GPU 传感器采样"的开关**（MonitoredGpuCount 下限被夹到 1，',
            '  见 src/Core/GpuItemSync.cs:43-48）。⇒ "GPU 采样为零"只能靠 **app-closed** 场景近似。',
            '★ 程序启动参数 --safe 只关 GPU **渲染**路线，不关传感器采样（src/System/Program.cs:12-16、',
            '  src/Rendering/Configuration/RenderingRoutePolicy.cs:68-71）——不要误用。'
        )
        compareWith      = @('gpu-one')
    }

    'gpu-one' = @{
        title            = '只监控 1 张显卡'
        launchApp        = $true
        settings         = @{ MonitoredGpuCount = 1 }
        captureMinutes   = 30
        warmupMinutes    = 2
        purpose          = '与 gpu-all 成对，量化"多一张卡"的边际 GPU 采样成本。'
        levers           = @('MonitoredGpuCount=1 → src/Core/Settings.cs:291')
        nonLevers        = @('同上：无"完全关闭 GPU 采样"的开关')
        compareWith      = @('gpu-all')
    }

    # ── ETW 排除（按 IP 排除实时网速）──────────────────────────────────────
    'etw-exclusion-on' = @{
        title            = '按 IP 排除实时网速（ETW 事件源开启）'
        launchApp        = $true
        settings         = @{
            IpTrafficExclusionEnabled = $true
            ExcludedTrafficIps        = @('192.0.2.10')
        }
        captureMinutes   = 30
        warmupMinutes    = 3
        purpose          = '隔离「ETW 网络事件源（内核态逐包处理）」带来的内核 CPU。'
        levers           = @(
            'IpTrafficExclusionEnabled=true → src/Core/Settings.cs:72',
            '  生效点：src/System/HardwareMonitor.cs:799-805、src/System/NetworkTraffic/NetworkTrafficExclusionService.cs:138',
            'ExcludedTrafficIps → src/Core/Settings.cs（精确 IPv4/IPv6 列表，最多 256 条）'
        )
        nonLevers        = @(
            '★ 192.0.2.10 是 RFC 5737 文档保留地址，**不会命中任何真实流量**——',
            '  目的只是把 ETW 事件源打开（排除列表非空 ⇒ 事件源生效）。',
            '  若要测"排除真的生效"的额外成本，请换成报告者机器上的真实 IP。'
        )
        compareWith      = @('default')
    }

    # ── 刷新率 ──────────────────────────────────────────────────────────────
    'low-refresh' = @{
        title            = '低刷新率（5s）'
        launchApp        = $true
        settings         = @{ RefreshMs = 5000 }
        captureMinutes   = 30
        warmupMinutes    = 2
        purpose          = '隔离「采集频率」对内核成本的影响（采集频率越低，驱动调用越少）。'
        levers           = @('RefreshMs=5000 → src/Core/Settings.cs:24')
        nonLevers        = @('—')
        compareWith      = @('default')
    }

    'high-refresh' = @{
        title            = '高刷新率（200ms）'
        launchApp        = $true
        settings         = @{ RefreshMs = 200 }
        captureMinutes   = 30
        warmupMinutes    = 2
        purpose          = '与 low-refresh 成对，量化采集频率的放大效应。'
        levers           = @('RefreshMs=200 → src/Core/Settings.cs:24')
        nonLevers        = @(
            '★ 产品的采集门限（src/System/HardwareServices/HardwareSamplingCoordinator）会让过密的',
            '  UI Tick 被跳过 ⇒ 200ms 不等于"真的每秒采 5 次"。跨场景比较前请读 manifest。'
        )
        compareWith      = @('low-refresh')
    }

    # ── Windows 性能计数器 ─────────────────────────────────────────────────
    'no-win-counters' = @{
        title            = 'Windows 性能计数器关闭（全部走 LHM 直读）'
        launchApp        = $true
        settings         = @{ UseWinPerCounters = $false }
        captureMinutes   = 30
        warmupMinutes    = 2
        purpose          = '隔离「Windows 性能计数器读取」这条路径（默认开启）相对 LHM 直读的成本差。'
        levers           = @(
            'UseWinPerCounters=false → src/Core/Settings.cs:150',
            '  生效点：src/System/HardwareServices/HardwareValueProvider.cs:554、:627'
        )
        nonLevers        = @('—')
        compareWith      = @('default')
    }
}
