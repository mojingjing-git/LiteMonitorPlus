using System;
using System.Collections.Generic;
using System.Linq;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>
    /// 一条覆盖层变更加撤销／重做记录：某个 JSON 路径在事务前后的值。
    /// <see cref="Before"/>／<see cref="After"/> 为 null 表示"该路径无键"（删除覆盖）。
    /// </summary>
    /// <param name="Path">主题 JSON 路径（点分，如 <c>color.background</c>）。</param>
    /// <param name="Before">事务前的覆盖层值（null = 无键）。</param>
    /// <param name="After">事务后的覆盖层值（null = 删除键）。</param>
    public sealed record ThemeOverlayChange(string Path, System.Text.Json.Nodes.JsonNode? Before, System.Text.Json.Nodes.JsonNode? After);

    /// <summary>
    /// 一个可撤销事务：连续合并的编辑（如一次滑块拖动）共享同一份
    /// 事务前配置快照与同一组覆盖层变更。
    /// </summary>
    public sealed class ThemeEditTransaction
    {
        internal string CoalesceKey { get; }
        internal DateTime LastEditUtc { get; set; }
        internal Theme BeforeSnapshot { get; }
        internal Theme AfterSnapshot { get; set; }
        internal List<ThemeOverlayChange> Changes { get; } = new();

        /// <summary>快照体量估计（字节），用于历史总量上限淘汰。</summary>
        internal long SnapshotBytes { get; set; }

        internal ThemeEditTransaction(string coalesceKey, Theme beforeSnapshot, DateTime utc)
        {
            CoalesceKey = coalesceKey;
            BeforeSnapshot = beforeSnapshot;
            AfterSnapshot = beforeSnapshot;
            LastEditUtc = utc;
        }

        internal void AddChange(ThemeOverlayChange change)
        {
            // 同一事务内同一路径的多次编辑只保留"事务前 vs 事务后"两端，
            // 中间过程对撤销／重做没有意义。
            for (int i = Changes.Count - 1; i >= 0; i--)
            {
                if (string.Equals(Changes[i].Path, change.Path, StringComparison.OrdinalIgnoreCase))
                {
                    Changes.RemoveAt(i);
                }
            }

            Changes.Add(change);
        }
    }

    /// <summary>
    /// 主题文档编辑历史：有界撤销／重做（重做计划 §5.1）。
    ///
    /// ★ 与旧 <see cref="ThemeEditHistory"/>（32 个快照、无重做、整份 JSON 签名比较）的区别：
    ///   1. 以**事务**为单位：同一路径的连续编辑合并（滑块拖动、一次颜色选择）；
    ///   2. 支持重做；
    ///   3. 容量上限：100 个事务或 8 MiB 快照总量，任一触限淘汰最旧事务；
    ///   4. 保存不清理历史——撤销跨越保存点是合法的（撤销回保存点之前即重新变脏）。
    ///
    /// ★ 数量与容量上限在本文件冻结（重做计划 TE01）：100 事务 / 8 MiB / 800ms 合并窗口。
    /// </summary>
    public class ThemeDocumentHistory
    {
        /// <summary>事务数量上限，触限淘汰最旧事务。</summary>
        public const int MaxTransactions = 100;

        /// <summary>快照总量上限（字节），触限从最旧事务开始淘汰。</summary>
        public const long MaxSnapshotBytes = 8 * 1024 * 1024;

        /// <summary>连续编辑合并窗口：同一路径在该间隔内的编辑并入同一事务。</summary>
        public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(800);

        private readonly List<ThemeEditTransaction> _undo = new();
        private readonly List<ThemeEditTransaction> _redo = new();
        private ThemeEditTransaction? _open;

        /// <summary>可撤销事务数（含未关闭的打开事务）。</summary>
        public int UndoCount => _undo.Count + (_open is null ? 0 : 1);

        /// <summary>可重做事务数。</summary>
        public int RedoCount => _redo.Count;

        /// <summary>
        /// 开始（或并入）一个编辑事务。
        /// 同一键在合并窗口内的连续编辑并入打开事务；否则关闭旧事务并开新事务。
        /// </summary>
        /// <param name="coalesceKey">合并键（通常是规范化 JSON 路径）。</param>
        /// <param name="currentConfig">当前配置快照（调用方保证是事务前状态）。</param>
        internal ThemeEditTransaction BeginOrMerge(string coalesceKey, Theme currentConfig)
        {
            DateTime now = DateTime.UtcNow;

            if (_open is not null &&
                string.Equals(_open.CoalesceKey, coalesceKey, StringComparison.Ordinal) &&
                now - _open.LastEditUtc <= CoalesceWindow)
            {
                _open.LastEditUtc = now;
                return _open;
            }

            CloseOpenTransaction();
            Theme before = AppearanceThemeConfig.CloneConfig(currentConfig);
            _open = new ThemeEditTransaction(coalesceKey, before, now);
            EvictOverflow();
            return _open;
        }

        /// <summary>编辑后调用：把事务后的配置快照与覆盖层变更记入打开事务。</summary>
        internal void RecordEdit(ThemeEditTransaction transaction, Theme afterConfig, ThemeOverlayChange change)
        {
            transaction.AfterSnapshot = AppearanceThemeConfig.CloneConfig(afterConfig);
            transaction.SnapshotBytes = EstimateBytes(transaction.AfterSnapshot);
            transaction.AddChange(change);
        }

        /// <summary>关闭打开事务并入撤销栈（撤销／重做／保存／重置前必须调用）。</summary>
        internal void CloseOpenTransaction()
        {
            if (_open is null) return;

            _undo.Add(_open);
            _open = null;
            _redo.Clear();
            EvictOverflow();
        }

        /// <summary>
        /// 弹出撤销栈顶事务（调用方负责恢复 <see cref="ThemeEditTransaction.BeforeSnapshot"/>
        /// 与各变更的 Before 值）。打开中的事务先关闭。
        /// </summary>
        internal ThemeEditTransaction? PopUndo()
        {
            CloseOpenTransaction();
            if (_undo.Count == 0) return null;

            ThemeEditTransaction txn = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(txn);
            return txn;
        }

        /// <summary>弹出重做栈顶事务（调用方恢复 AfterSnapshot 与 After 值）。</summary>
        internal ThemeEditTransaction? PopRedo()
        {
            if (_redo.Count == 0) return null;

            ThemeEditTransaction txn = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(txn);
            return txn;
        }

        /// <summary>清空历史（打开新文档／另存为切换身份时）。</summary>
        public void Reset()
        {
            _undo.Clear();
            _redo.Clear();
            _open = null;
        }

        /// <summary>
        /// [S05] 从撤销／重做历史中移除"触碰过匹配路径"的整个事务（打开事务先关闭）。
        /// 整事务移除而不是只剔除变更：事务快照里烘着清理前的阈值旧值，半删会让
        /// 撤销/重做通过快照把已清理的阈值编辑复活。返回移除的事务数。
        /// </summary>
        internal int RemoveTransactionsTouching(Func<string, bool> pathMatch)
        {
            CloseOpenTransaction();
            int removed = _undo.RemoveAll(txn => txn.Changes.Any(c => pathMatch(c.Path)));
            removed += _redo.RemoveAll(txn => txn.Changes.Any(c => pathMatch(c.Path)));
            return removed;
        }

        /// <summary>淘汰超出上限的最旧事务（数量或快照总量任一触限；打开中的事务计入总量）。</summary>
        private void EvictOverflow()
        {
            while (_undo.Count + (_open is null ? 0 : 1) > MaxTransactions && _undo.Count > 0)
            {
                _undo.RemoveAt(0);
            }

            long total = 0;
            foreach (ThemeEditTransaction txn in _undo)
            {
                total += txn.SnapshotBytes;
            }

            while (total > MaxSnapshotBytes && _undo.Count > 1)
            {
                total -= _undo[0].SnapshotBytes;
                _undo.RemoveAt(0);
            }
        }

        private static long EstimateBytes(Theme config)
        {
            try
            {
                return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    config, ThemeManager.CreateSerializerOptions()).LongLength;
            }
            catch
            {
                // 序列化失败不影响正确性，只影响淘汰精度
                return 4096;
            }
        }
    }
}
