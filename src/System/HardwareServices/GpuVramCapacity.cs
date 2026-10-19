using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 每张显卡的真实显存总容量 (从 Windows 注册表读取)。
    ///
    /// 为什么需要它:
    ///   LHM 0.9.6 的 NVIDIA 显存传感器走的是 NVAPI 的旧版全局接口
    ///   (NvAPI_GPU_GetMemoryInfo, 接口号 0x774AA982, 参数是 DisplayHandle),
    ///   多卡机器上多张 NVIDIA 卡会拿到同一份显存数据 —— 也就是"第 2 张卡显示第 1 张卡的显存"。
    ///   注册表里每个显示适配器都单独记着自己的显存容量, 可以当作"这张卡到底有多少显存"的参照:
    ///     1) 用它判断厂商通道是不是在报告别的卡 (容量命中另一块适配器就是串了)
    ///     2) 串了的时候用它当正确的分母
    ///
    /// 数据来源:
    ///   HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\&lt;NNNN&gt;
    ///     DriverDesc                      -> 适配器名 (与 LHM 的 hw.Name 对得上)
    ///     HardwareInformation.qwMemorySize -> 显存字节数 (QWORD, 真机实测为 Int64)
    ///
    /// 注意: 注册表记的是"驱动可见容量", 不一定等于物理容量
    ///   (实测 Intel Arc A770 = 16256 MB, 物理 16 GB = 16384 MB, 差 0.78%),
    ///   所以判定时必须配"交叉证据", 不能只看"对不上"。
    /// </summary>
    public static class GpuVramCapacity
    {
        private const string ClassSubKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private const long MinPlausibleBytes = 64L * 1024 * 1024;   // 小于 64MB 的当噪声丢掉

        private static readonly object _lock = new object();
        private static Dictionary<string, float> _byNormalizedName = new(StringComparer.Ordinal);   // 归一化名 -> 容量 (MB)
        private static List<float> _allTotalsMB = new();                                          // 全部适配器的容量 (MB)
        private static bool _loaded;

        /// <summary>
        /// 归一化显卡名, 便于和注册表 DriverDesc 对比。
        /// 去掉 (R)/(TM)/®/™ , 压缩空白, 忽略大小写。
        /// "NVIDIA GeForce RTX 3080" / "NVIDIA GeForce RTX 3080 "  ->  同一个 key
        /// </summary>
        public static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string n = name
                .Replace("(R)", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("(TM)", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("®", " ")
                .Replace("™", " ");
            n = string.Join(" ", n.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return n.ToUpperInvariant();
        }

        /// <summary>
        /// 取某张卡 (用 LHM 的 hw.Name) 的真实显存容量, 单位 MB。找不到返回 null。
        /// 同名多卡 (同型号) 时, 只要各条记录的容量一致就返回该值 —— 同型号容量本来就一样。
        /// 同名但容量不一致 (无法判定) 时返回 null, 宁可不用也不猜。
        /// </summary>
        public static float? GetTotalMB(string gpuName)
        {
            string key = NormalizeName(gpuName);
            if (key.Length == 0) return null;

            EnsureLoaded();
            lock (_lock)
            {
                return _byNormalizedName.TryGetValue(key, out float mb) ? mb : (float?)null;
            }
        }

        /// <summary>
        /// 注册表里**全部**显示适配器的显存容量 (MB)。
        /// 用来做"串卡交叉证据": 厂商通道报的容量如果命中了这里的某个值,
        /// 而且那个值不是本卡自己的容量, 就说明它在报告别的卡。
        /// 必须取全部适配器 (而不是只取被监控的卡), 否则"只监控 1 张卡且正好选到串的那张"
        /// 会因为"另一张卡不在监控集合里"而失去证据。
        /// </summary>
        public static IReadOnlyList<float> GetAllTotalsMB()
        {
            EnsureLoaded();
            lock (_lock)
            {
                return _allTotalsMB.ToArray();
            }
        }

        private static void EnsureLoaded()
        {
            lock (_lock)
            {
                if (_loaded) return;
                _loaded = true;
                _byNormalizedName.Clear();
                _allTotalsMB.Clear();

                try
                {
                    using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using RegistryKey? classKey = root.OpenSubKey(ClassSubKey);
                    if (classKey == null) return;

                    // 先按名字收集所有候选 (同型号可能有多条)
                    var candidates = new Dictionary<string, List<float>>(StringComparer.Ordinal);

                    foreach (string subName in classKey.GetSubKeyNames())
                    {
                        // ★ 这个类键下不只有适配器, 还有 "Configuration" / "Properties" 等系统子键。
                        //   "Properties" 是受保护的系统键, OpenSubKey 会抛 UnauthorizedAccessException
                        //   ("Requested registry access is not allowed.") —— 真机已实测。
                        //   所以这里既只认纯数字的适配器键, 又给每个子键单独兜一层异常,
                        //   免得一个打不开的子键把整张表都废掉 (外层 catch 会清空全部结果)。
                        if (!IsAllDigits(subName)) continue;

                        RegistryKey? adapterKey;
                        try
                        {
                            adapterKey = classKey.OpenSubKey(subName);
                        }
                        catch
                        {
                            continue;
                        }
                        if (adapterKey == null) continue;

                        using (adapterKey)
                        {
                            string? desc = adapterKey.GetValue("DriverDesc") as string;
                            object? sizeObj = adapterKey.GetValue("HardwareInformation.qwMemorySize");
                            if (string.IsNullOrWhiteSpace(desc) || sizeObj == null) continue;

                            if (!TryToInt64(sizeObj, out long bytes) || bytes < MinPlausibleBytes) continue;

                            string norm = NormalizeName(desc);
                            if (norm.Length == 0) continue;

                            float mb = (float)(bytes / (1024.0 * 1024.0));
                            _allTotalsMB.Add(mb);

                            if (!candidates.TryGetValue(norm, out List<float>? list))
                            {
                                list = new List<float>();
                                candidates[norm] = list;
                            }
                            list.Add(mb);
                        }
                    }

                    foreach (var kv in candidates)
                    {
                        // 同名多条: 容量一致才敢用
                        float min = kv.Value.Min();
                        float max = kv.Value.Max();
                        if (Math.Abs(max - min) < 0.5f) _byNormalizedName[kv.Key] = min;
                    }
                }
                catch
                {
                    // 读不到就当没有, 判定规则会退化成"厂商通道可信", 即维持旧行为
                    _byNormalizedName = new Dictionary<string, float>(StringComparer.Ordinal);
                    _allTotalsMB = new List<float>();
                }
            }
        }

        /// <summary>是否全为数字 (用来筛出类键下的适配器子键, 排除 Configuration / Properties)</summary>
        private static bool IsAllDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        private static bool TryToInt64(object value, out long result)
        {
            switch (value)
            {
                case long l:
                    result = l;
                    return true;
                case int i:
                    result = i;
                    return true;
                case uint ui:
                    result = ui;
                    return true;
                case ulong ul when ul <= long.MaxValue:
                    result = (long)ul;
                    return true;
                case byte[] bytes when bytes.Length == 8:
                    result = BitConverter.ToInt64(bytes, 0);
                    return true;
                default:
                    return long.TryParse(
                        Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out result);
            }
        }
    }
}
