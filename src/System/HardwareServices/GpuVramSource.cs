using System;
using System.Collections.Generic;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 显存数据来源的判定。
    ///
    /// 背景:
    ///   LHM 0.9.6 的 NVIDIA 显存传感器来自 NVAPI 旧版全局接口, 多卡时多张卡拿到同一份数据。
    ///   D3D 通道按适配器统计, 每卡独立, 但口径与厂商通道略有差异 (且不含纯计算型占用)。
    ///   因此不能无条件切到 D3D —— 单卡场景下厂商通道是对的, 切过去反而丢失 CUDA/ROCm 独占显存的可见性。
    ///
    /// 判定依据 ("串卡"要同时满足两条):
    ///   1) 厂商通道报的容量与本卡真实容量对不上;
    ///   2) 且这个报出来的容量**命中了系统里另一块适配器的容量**。
    /// 第 2 条是必须的: 注册表记的是驱动可见容量, 未必等于物理容量
    ///   (实测 Intel Arc A770 = 16256 MB vs 物理 16384 MB, 差 0.78%),
    ///   只看第 1 条会把这类卡误判成串卡, 白白丢掉 CUDA/ROCm 可见性。
    /// </summary>
    public static class GpuVramSource
    {
        /// <summary>容量比较容差: 绝对 1MB 或相对 1%, 取大者。</summary>
        private const float AbsToleranceMB = 1f;
        private const float RelTolerance = 0.01f;

        /// <summary>
        /// 容量下限: 低于这个值的多半是核显的 UMA 窗口或无意义值, 不拿来当基准。
        /// </summary>
        public const float MinPlausibleTotalMB = 512f;

        /// <summary>两个容量是否算"同一个"。</summary>
        public static bool SameCapacity(float a, float b)
        {
            float diff = Math.Abs(a - b);
            float tolerance = Math.Max(AbsToleranceMB, Math.Max(a, b) * RelTolerance);
            return diff <= tolerance;
        }

        /// <summary>
        /// 厂商通道报的容量是否与该卡真实容量不符 (即厂商通道在报告别的卡)。
        /// 任何一项缺失 / 不可信都判为"可信" —— 缺证据时维持旧行为。
        /// </summary>
        public static bool IsVendorChannelForeign(
            float? vendorTotalMB,
            float? realTotalMB,
            IReadOnlyList<float> allAdapterTotalsMB)
        {
            if (!vendorTotalMB.HasValue || !realTotalMB.HasValue) return false;
            if (vendorTotalMB.Value <= 0 || realTotalMB.Value <= 0) return false;

            // ★ 真实容量太小 -> 多半是核显的 UMA 窗口, 不参与判定
            if (realTotalMB.Value < MinPlausibleTotalMB) return false;

            // 本卡对得上 -> 可信
            if (SameCapacity(vendorTotalMB.Value, realTotalMB.Value)) return false;

            // 对不上 -> 必须命中"另一块适配器"的容量才算串卡。
            // (本卡自己的容量已经排除在外了: 上面 SameCapacity 为 false, 所以它命中不了)
            if (allAdapterTotalsMB == null) return false;
            for (int i = 0; i < allAdapterTotalsMB.Count; i++)
            {
                float t = allAdapterTotalsMB[i];
                if (t <= 0) continue;
                if (SameCapacity(vendorTotalMB.Value, t)) return true;
            }

            return false;
        }

        /// <summary>
        /// 选出该卡最终使用的 (used, total), 单位 MB。
        /// useD3D 由调用方决定 (它要先把 G1/G2/G3 三道守卫过一遍)。
        /// </summary>
        public static (float? used, float? total) Select(
            float? vendorUsedMB,
            float? vendorTotalMB,
            float? d3dUsedMB,
            float? d3dTotalMB,
            float? realTotalMB,
            bool useD3D)
        {
            if (useD3D)
            {
                // 厂商通道在报告别的卡 -> 改用 D3D 通道, 容量用注册表真值
                // ★ D3D 通道缺失时宁可无值: 绝不能拿"别的卡的 used"去除"本卡的 total",
                //   那样会算出 6495/20480 = 31.7% 这种又错又怪的值, 比原来更让人误解。
                if (!d3dUsedMB.HasValue) return (null, realTotalMB ?? d3dTotalMB ?? vendorTotalMB);
                float? used = d3dUsedMB;
                float? total = realTotalMB ?? d3dTotalMB ?? vendorTotalMB;
                return (used, total);
            }

            // 厂商通道可信 -> 维持原口径, D3D 只作兜底
            float? usedNormal = vendorUsedMB ?? d3dUsedMB;
            float? totalNormal = vendorTotalMB ?? d3dTotalMB ?? realTotalMB;
            return (usedNormal, totalNormal);
        }
    }
}
