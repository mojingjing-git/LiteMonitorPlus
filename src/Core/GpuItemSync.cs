using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 显卡监控项同步器。
    ///
    /// 职责: 保证 Settings.MonitorItems 中的显卡监控项与 Settings.MonitoredGpuCount 保持一致。
    ///   - 每个被监控的显卡生成一组 "GPU.{序号}.{指标}" 监控项
    ///   - 从旧版单卡格式 ("GPU.Load") 迁移时继承可见性 / 排序 / 单位, 但不继承用户自定义名称
    ///     (名称改由显卡简称自动生成, 否则两张卡会显示同一个名字)
    ///   - 已存在的同 Key 项保留用户的全部自定义
    /// </summary>
    public static class GpuItemSync
    {
        /// <summary>显卡基础指标定义: (指标名, 排序基准, 主界面默认可见, 任务栏默认可见)</summary>
        public static readonly (string metric, int sort, bool panel, bool taskbar)[] Metrics =
        {
            ("Load",  301, true,  true),
            ("Temp",  302, true,  false),
            ("Clock", 303, false, false),
            ("Power", 304, false, false),
            ("Fan",   305, false, false),
            ("VRAM",  306, true,  false),
        };

        /// <summary>每张卡占用的排序位宽度, 让同一分组内按卡顺序排列</summary>
        public const int SortStride = 10;

        /// <summary>
        /// 同步显卡监控项。可重复调用, 在**同一个 cap** 下幂等。
        /// </summary>
        public static void Ensure(Settings cfg, int availableGpuCount = GpuKey.MaxGpuCount)
        {
            if (cfg?.MonitorItems == null) return;

            // ★★★ [多显卡] 上限 = 实际检测到的卡数 与 配置上限 取小 ★★★
            // 没有这一步, 用户在 2 卡机器上选 8 会生成 6 组永远没有数据的空白监控项。
            int cap = Math.Max(1, Math.Min(GpuKey.MaxGpuCount, availableGpuCount));

            int count = cfg.MonitoredGpuCount;
            if (count < 1) count = 1;
            if (count > cap) count = cap;

            // 夹紧结果回写, 让设置页显示真实生效值
            if (cfg.MonitoredGpuCount != count) cfg.MonitoredGpuCount = count;

            var items = cfg.MonitorItems;

            // ★★★ 先清掉 null 元素 ★★★
            // settings.json 被手改坏 / 插件写坏时, MonitorItems 里可能出现 null 项。
            // 下面每处 it.Key 都会 NRE, 而 Ensure 是在 SettingsHelper.Load() 的**启动路径**上
            // 调用的 —— 一次 NRE 就能让程序起不来。顺带也把"无意义的 null 项"清干净。
            items.RemoveAll(it => it == null);

            // 1. 旧版单卡项作为迁移模板
            var legacy = new Dictionary<string, MonitorItemConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                if (it.Key.StartsWith(GpuKey.Prefix, StringComparison.OrdinalIgnoreCase)
                    && !GpuKey.TryParse(it.Key, out _, out _))
                {
                    legacy[it.Key] = it;
                }
            }

            // 2. 已存在的带序号项 (保留用户自定义)
            var indexed = new Dictionary<string, MonitorItemConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                if (GpuKey.TryParse(it.Key, out _, out _))
                {
                    indexed[it.Key] = it;
                }
            }

            // 3. 清掉全部显卡相关项 (旧格式 + 不再需要的序号)
            items.RemoveAll(it => it.Key.StartsWith(GpuKey.Prefix, StringComparison.OrdinalIgnoreCase));

            // 4. 按当前数量重新生成
            for (int i = 0; i < count; i++)
            {
                foreach (var (metric, sort, panel, taskbar) in Metrics)
                {
                    string key = GpuKey.Build(i, metric);
                    if (indexed.TryGetValue(key, out var existing))
                    {
                        items.Add(existing);
                        continue;
                    }

                    string baseKey = GpuKey.BaseKey(metric);
                    legacy.TryGetValue(baseKey, out var tpl);

                    // ★★★ 排序基准: 取"0 号卡同一指标"的实际排序值 ★★★
                    //   优先用已存在的 GPU.0.{metric} —— 它承载了用户在"监控项显示"里拖过的顺序;
                    //   其次用旧格式项 (首次迁移时 legacy 还在); 最后才用默认值。
                    //   ⚠️ 不能只看 legacy: 第 1 次 Ensure 就把 legacy 项删光了,
                    //   之后把卡数从 1 调到 2 时, 新卡会回落成默认值 (301+10=311),
                    //   而 0 号卡仍保留用户自定义的 500 —— 面板顺序会倒挂成
                    //   "GPU.0.Temp → GPU.1.* → GPU.0.Load"。
                    int baseSort = sort;
                    int baseTaskbarSort = sort;

                    if (indexed.TryGetValue(GpuKey.Build(0, metric), out var firstCard))
                    {
                        if (firstCard.SortIndex != 0) baseSort = firstCard.SortIndex;
                        if (firstCard.TaskbarSortIndex != 0) baseTaskbarSort = firstCard.TaskbarSortIndex;
                    }
                    else if (tpl != null)
                    {
                        if (tpl.SortIndex != 0) baseSort = tpl.SortIndex;
                        if (tpl.TaskbarSortIndex != 0) baseTaskbarSort = tpl.TaskbarSortIndex;
                    }

                    items.Add(new MonitorItemConfig
                    {
                        Key = key,
                        // ★ 排序索引同样继承旧项, 否则用户在"监控项显示"里拖过的顺序会被重置 ★
                        SortIndex = baseSort + i * SortStride,
                        TaskbarSortIndex = baseTaskbarSort + i * SortStride,
                        VisibleInPanel = tpl?.VisibleInPanel ?? panel,
                        VisibleInTaskbar = tpl?.VisibleInTaskbar ?? taskbar,
                        // 任务栏自定义名要继承 (UserLabel 是故意不继承的: 两张卡同名没意义)
                        TaskbarLabel = tpl?.TaskbarLabel,
                        UnitPanel = tpl?.UnitPanel,
                        UnitTaskbar = tpl?.UnitTaskbar,
                    });

                }
            }

            // 4.5 ★★★ 保留非标准指标 (例如用户/插件自建的 "GPU.0.Custom") ★★★
            //     Task 5 Step 2.5 让 InternAllStrings 放行了所有带序号的显卡 Key,
            //     如果这里把它们一起删掉, 那一步放行就白做了 (实测会静默丢失)。
            //     ⚠️ **只保留当前监控数量范围内**的自定义项 (customIndex < count) ——
            //     否则 count=2 时 "GPU.5.Custom" 会被复活, 又冒出计划要消灭的空白监控项,
            //     而且会打破"MonitorItems 里不存在 GPU.{超出卡数}.*"这条不变量。 ★★★
            //     已知限制: 越界的自定义项一旦被裁掉, **之后把数量调大也不会自动找回**
            //     (裁掉就是真删了, 不做历史保留) —— 这是有意为之, 避免空白监控项复活。
            //     另: 非规范大小写的 Key (如 "GPU.0.load") 会**原样保留**, 不规范成 "GPU.0.Load"。
            var standardMetrics = new HashSet<string>(
                Metrics.Select(m => m.metric), StringComparer.OrdinalIgnoreCase);
            foreach (var kv in indexed)
            {
                if (GpuKey.TryParse(kv.Key, out int customIndex, out string customMetric)
                    && customIndex < count
                    && !standardMetrics.Contains(customMetric))
                {
                    items.Add(kv.Value);
                }
            }

            // 5. ★★★ [按卡拆组] 旧版整组别名 ("GPU" -> "我的游戏卡") 迁移到 0 号卡 ★★★
            //    拆组之后不再存在 "GPU" 分组, 这个别名会变成孤儿 —— 没有任何界面会再读它,
            //    用户会发现"我给显卡起的名字没了"而且没有任何提示。
            //    只迁移给 0 号卡: 既保住用户的命名, 又不会让两张卡同名 (后者正是本功能要消灭的问题)。
            //    幂等: 迁移完就把 "GPU" 删掉, 重复调用不会二次生效。
            if (cfg.GroupAliases != null
                && cfg.GroupAliases.TryGetValue("GPU", out string? legacyGroupAlias)
                && !string.IsNullOrEmpty(legacyGroupAlias))
            {
                if (!cfg.GroupAliases.ContainsKey("GPU.0"))
                    cfg.GroupAliases["GPU.0"] = legacyGroupAlias;
                cfg.GroupAliases.Remove("GPU");
            }
        }
    }
}