using System.Text.Json.Nodes;

namespace LiteMonitor.PerformanceProbe;

/// <summary>
/// 批次级**计数口径声明**（2026-10-20 口径改造）。
///
/// 为什么需要它：同一个字段名在**不同批次**里含义可能不同。
///   - 早先的基线批次（<c>baseline/</c>）："到期轮数" = 旧探针按整数秒取模语义**复算**的值；
///   - 本批次及之后的复跑：同名主字段 = **生产侧真实触发计数**。
/// 两者同名字段、不同含义，直接相减会得出**与事实相反**的结论。
///
/// 所以每个批次输出的 JSON 顶层都必须带 <c>counterSemantics</c>：
/// 任何跨批次比较都必须先读它，确认两边的口径一致再比。
/// </summary>
internal static class ProbeCounterSemantics
{
    /// <summary>口径声明的版本号。语义变更时递增，跨批次比较据此判断。</summary>
    public const string SemanticsVersion = "2026-10-20-d-prep";

    /// <summary>本批次各计数字段的来源声明。</summary>
    public static JsonObject Build() => new JsonObject
    {
        ["semanticsVersion"] = SemanticsVersion,
        ["productionCounterSource"] =
            "生产侧真实计数：HardwareMonitor.SamplingStatistics（HardwareSamplingStatistics 类型），取本批次稳态窗口内的增量。",
        ["recomputedCounterSource"] =
            "旧整数秒取模语义推算值：探针按 HardwareMonitor._secondsCounter 的 %3 / %10 / %60==5 / %180==30 / %300==45 复算。" +
            "生产侧已改为单调截止时间，此值**不代表**当前生产调度语义。",
        ["fieldSources"] = new JsonObject
        {
            ["collectionRounds"] = "生产侧真实计数：SamplingStatistics.CollectionRounds 增量（真实发生的采集轮数）",
            ["collectionRequests"] = "探针自身计数：探针发起的采集请求次数（未到期会被门限跳过，不产生采集）",
            ["collectionGenerations"] = "生产侧真实计数：SamplingStatistics.Generation 增量",
            ["skippedRequests"] = "生产侧真实计数：SamplingStatistics.SkippedRequests 增量（未到期被快速返回）",
            ["sharedRequests"] = "生产侧真实计数：SamplingStatistics.SharedRequests 增量（并发请求复用在途采集）",
            ["rejectedRequests"] = "生产侧真实计数：SamplingStatistics.RejectedRequests 增量（未就绪/已释放）",
            ["forcedCollections"] = "生产侧真实计数：SamplingStatistics.ForcedCollections 增量（例外采集：初始化/重载/设备切换）",
            ["failedCollections"] = "生产侧真实计数：SamplingStatistics.FailedCollections 增量（采集异常）",
            ["valueComputations"] = "生产侧真实计数：SamplingStatistics.ValueComputations 增量（真正执行过一次取值计算）",
            ["productionCollectionDurationMs"] = "生产侧真实计数：SamplingStatistics.TotalCollectionDurationMs 增量",
            ["slowScanTriggerRounds"] = "生产侧真实计数：SamplingStatistics.SlowScanTriggers 增量（慢扫描真实触发次数）",
            ["diskBackgroundScanTriggerRounds"] = "生产侧真实计数：SamplingStatistics.DiskBackgroundScanTriggers 增量（磁盘后台扫描真实触发次数）",
            ["trafficSaveDueRounds"] = "生产侧真实计数：SamplingStatistics.TrafficSaveTriggers 增量（流量保存真实触发次数）",
            ["gcRequestDueRounds"] = "生产侧真实计数：SamplingStatistics.GcRequestTriggers 增量（自动 GC 真实触发次数）",
            ["workingSetTrimDueRounds"] = "生产侧真实计数：SamplingStatistics.WorkingSetTrimTriggers 增量（工作集裁剪真实触发次数）",
            ["productionDeviceUpdates"] = "生产侧真实计数：SamplingStatistics.SnapshotDeviceUpdates() 的逐项增量（全部设备）",
            ["productionCategoryUpdates"] = "生产侧真实计数：SamplingStatistics.SnapshotCategoryTotals() 的逐项增量",
            ["deviceUpdateCounters"] = "LHM 注入代理独立复核：只覆盖可注入分组（显卡/内存），不覆盖 CPU/网卡/磁盘/主板",
            ["trafficFileObservedRewrites"] = "探针观测值：轮询流量文件 LastWriteTime 变化得到的**下界**，不是写入次数本身",
            ["valueReads"] = "探针自身计数：探针发起的 Get() 调用次数（不含应用进程内的 Web 快照读取）",
            ["slowScanTriggerRoundsRecomputed"] = "旧取模语义推算值（%3）：不可与生产计数相减",
            ["diskBackgroundScanTriggerRoundsRecomputed"] = "旧取模语义推算值（%10）：不可与生产计数相减",
            ["trafficSaveDueRoundsRecomputed"] = "旧取模语义推算值（%60==5）：不可与生产计数相减",
            ["gcRequestDueRoundsRecomputed"] = "旧取模语义推算值（%180==30）：不可与生产计数相减",
            ["workingSetTrimDueRoundsRecomputed"] = "旧取模语义推算值（%300==45）：不可与生产计数相减",
        },
        ["recomputedSeriesWarning"] =
            "*Recomputed 系列是按【旧整数秒取模语义】复算的推算值，生产侧已改为单调截止时间。" +
            "它与同名主字段（生产侧真实计数）语义不同：**不可相减、不可跨批次直接比较**。" +
            "探针读不到 HardwareMonitor._secondsCounter 时，这些字段为 null（recomputedCountersStatus = unavailable），不是 0。",
        ["crossBatchWarning"] =
            "跨批次比较前必须先读本字段：早先批次（baseline/ 及更早的复跑）里同名的到期轮数字段是**旧取模复算值**，" +
            "本批次（semanticsVersion = " + SemanticsVersion + "）同名主字段是**生产侧真实计数**。" +
            "两者同名字段、不同含义，直接相减会得出与事实相反的结论。",
        ["dueSemanticsRecomputationAgreesMeaning"] =
            "比较的是「探针按旧取模语义复算的推算值」与「生产侧真实触发计数」是否逐项吻合（|差| ≤ 1）。" +
            "两套语义本就不同：**false 属于正常状态，不再是行为级报警**；true 说明某个触发器的节奏恰好与旧取模规则重合，" +
            "需人工确认该计数究竟来自单调截止时间还是回退到了取模语义。" +
            "真正的行为级报警是 trafficRewriteAgreesWithDueRounds。",
        ["knownDeadCounters"] = new JsonObject
        {
            ["webSocketFramesReceived"] =
                "基线批次里该字段恒为 0，是**死计数器值**（当时根本没计到帧），不携带任何信息；" +
                "**不可**与其它批次的 53 / 159 / 55 等非 0 值相比 —— 那是换了计法，不是性能变化。",
        },
    };
}
