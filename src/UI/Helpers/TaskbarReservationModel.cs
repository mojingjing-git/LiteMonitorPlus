using System;
using System.Drawing;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>占位的收缩轴（水平 = 宽度方向，垂直 = 高度方向）。</summary>
    public enum TaskbarShrinkAxis
    {
        /// <summary>水平（顶/底停靠）：挤占任务列表的**宽度**。</summary>
        Horizontal = 0,

        /// <summary>垂直（左/右停靠）：挤占任务列表的**高度**。</summary>
        Vertical = 1,
    }

    /// <summary>占位模式。</summary>
    public enum TaskbarShrinkMode
    {
        /// <summary>
        /// 从**起始端**占用：面板在左（上）⇒ 图标整体后移，
        /// 任务列表的起始边被推后 <c>面板尺寸 + 手动偏移</c>。
        /// </summary>
        FromStart = 0,

        /// <summary>
        /// 从**末端**占用：面板在右（下）⇒ 任务列表的末端边锚定到面板的起始边。
        /// </summary>
        FromEnd = 1,
    }

    /// <summary>
    /// 基线身份（★ P-F05）：决定"占位基线"是否仍然有效。
    /// <para>★ 下列任一变化 ⇒ 旧基线作废，必须<b>先撤销旧占位</b>再重新取基线：</para>
    /// <list type="bullet">
    /// <item>父宿主句柄（ReBarWindow32）变化；</item>
    /// <item>任务列表句柄（MSTaskSwWClass）变化；</item>
    /// <item>停靠方向变化（底 → 左 ⇒ 收缩轴都变了）；</item>
    /// <item>父客户区尺寸变化（分辨率 / 缩放变化）；</item>
    /// <item>占位模式变化（靠左 ↔ 靠右）。</item>
    /// </list>
    /// </summary>
    public readonly struct TaskbarReservationToken : IEquatable<TaskbarReservationToken>
    {
        public TaskbarReservationToken(IntPtr parentHandle, IntPtr slotHandle, TaskbarDockDirection direction, int parentExtent, TaskbarShrinkMode mode)
        {
            ParentHandle = parentHandle;
            SlotHandle = slotHandle;
            Direction = direction;
            ParentExtent = parentExtent;
            Mode = mode;
        }

        /// <summary>父宿主句柄（ReBarWindow32）。</summary>
        public IntPtr ParentHandle { get; }

        /// <summary>被占位的任务列表句柄（MSTaskSwWClass）。</summary>
        public IntPtr SlotHandle { get; }

        /// <summary>停靠方向。</summary>
        public TaskbarDockDirection Direction { get; }

        /// <summary>父客户区在收缩轴上的长度（分辨率变化 ⇒ 变化）。</summary>
        public int ParentExtent { get; }

        /// <summary>占位模式。</summary>
        public TaskbarShrinkMode Mode { get; }

        public bool Equals(TaskbarReservationToken other)
        {
            return ParentHandle == other.ParentHandle
                && SlotHandle == other.SlotHandle
                && Direction == other.Direction
                && ParentExtent == other.ParentExtent
                && Mode == other.Mode;
        }

        /// <summary>
        /// 是否"同一套布局"：句柄、方向、父客户区长度都没变（只有对齐方式变了）。
        /// <para>★ 同一套布局下才允许把旧占位**撤销**（还原到基线）；
        /// 句柄 / 方向 / 分辨率变化时，系统的**新布局**已经取代了旧占位，
        /// 此时把旧基线写回新窗口反而会破坏新布局 ⇒ 只丢状态、不写回。</para>
        /// </summary>
        public bool IsSameLayout(TaskbarReservationToken other)
        {
            return ParentHandle == other.ParentHandle
                && SlotHandle == other.SlotHandle
                && Direction == other.Direction
                && ParentExtent == other.ParentExtent;
        }

        public override bool Equals(object? obj) => obj is TaskbarReservationToken other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(ParentHandle, SlotHandle, (int)Direction, ParentExtent, (int)Mode);

        public static bool operator ==(TaskbarReservationToken left, TaskbarReservationToken right) => left.Equals(right);

        public static bool operator !=(TaskbarReservationToken left, TaskbarReservationToken right) => !left.Equals(right);
    }

    /// <summary>
    /// 任务栏**占位（挤占）模型**（★ P-F05 / 母计划 F05 第 4 步）。
    /// <para>★ 模型只维护两件事：</para>
    /// <list type="number">
    /// <item><b>基线</b>（<see cref="Baseline"/>）："未施加本面板影响"的任务列表矩形
    /// （父客户区坐标）。</item>
    /// <item><b>本次占位量</b>（<see cref="Reservation"/>）：本面板当前从基线上扣掉的量。</item>
    /// </list>
    /// <para>★ <b>幂等</b>：观测值与上次施加值一致 ⇒ 不重新推导、不移动窗口 ⇒ 无累计漂移。</para>
    /// <para>★ <b>不得在已缩短的任务列表上继续扣减</b>：系统改了任务列表时，
    /// 先把"本次占位量"从观测值里<b>扣除</b>，得到新的基线，再重新施加 ——
    /// 于是"施加后的尺寸 == 系统给的尺寸"，永远不会被二次压缩（#568 的
    /// "任务列表 1200 → 640、图标右移 600px"正是这条被违反后的产物）。</para>
    /// <para>★ <b>撤销</b>：<see cref="TryTakeBaseline"/> 交回基线（用于禁用 / 退出 / 重挂时
    /// 把 Explorer 的布局原样还回去），随后 <see cref="Reset"/> 清空状态。</para>
    /// </summary>
    public sealed class TaskbarReservationModel
    {
        /// <summary>几何容差（1px）：容忍系统 1px 抖动，避免反复重绘。</summary>
        public const int Tolerance = 1;

        /// <summary>是否已取得基线。</summary>
        public bool HasBaseline { get; private set; }

        /// <summary>基线矩形（父客户区坐标；未施加本面板影响时的任务列表矩形）。</summary>
        public Rectangle Baseline { get; private set; }

        /// <summary>是否已施加过。</summary>
        public bool HasApplied { get; private set; }

        /// <summary>上次施加的矩形（父客户区坐标）。</summary>
        public Rectangle Applied { get; private set; }

        /// <summary>本次占位量（沿收缩轴，实际扣掉的尺寸）。</summary>
        public int Reservation { get; private set; }

        /// <summary>清空全部状态（基线 / 已施加 / 占位量）。</summary>
        public void Reset()
        {
            HasBaseline = false;
            HasApplied = false;
            Baseline = Rectangle.Empty;
            Applied = Rectangle.Empty;
            Reservation = 0;
        }

        /// <summary>
        /// 计算本次应当施加给任务列表的矩形。
        /// </summary>
        /// <param name="observed">当前观测到的任务列表矩形（父客户区坐标）。</param>
        /// <param name="axis">收缩轴。</param>
        /// <param name="panelExtent">面板在收缩轴上的尺寸（宽或高，物理像素）。</param>
        /// <param name="manualOffset">手动偏移（物理像素）。</param>
        /// <param name="parentExtent">父客户区在收缩轴上的长度（物理像素）。</param>
        /// <param name="mode">占位模式。</param>
        /// <param name="applied">输出：应施加给任务列表的矩形。</param>
        /// <param name="panelStart">输出：面板在收缩轴上的起始位置（父客户区坐标）。</param>
        /// <returns>false 表示无法施加（例如剩余空间不足）。</returns>
        public bool TryCompute(
            Rectangle observed,
            TaskbarShrinkAxis axis,
            int panelExtent,
            int manualOffset,
            int parentExtent,
            TaskbarShrinkMode mode,
            out Rectangle applied,
            out int panelStart)
        {
            applied = Rectangle.Empty;
            panelStart = 0;

            if (observed.Width <= 0 || observed.Height <= 0) return false;

            // ① 基线刷新
            if (!HasBaseline)
            {
                // 首次：当前观测值就是"未施加本面板影响"的基线
                Baseline = observed;
                HasBaseline = true;
                HasApplied = false;
                Reservation = 0;
            }
            else if (HasApplied && !SameRect(observed, Applied))
            {
                // 系统改了任务列表 ⇒ 重新推导基线（**先把本次占位量扣掉**）
                Baseline = DeriveBaseline(observed, axis, mode);
                HasApplied = false;
            }

            // ② 由基线 + 占位量推导"应施加的矩形"
            int baseStart = Start(Baseline, axis);
            int baseSize = Size(Baseline, axis);
            int newStart;
            int newSize;

            if (mode == TaskbarShrinkMode.FromStart)
            {
                int reserve = Math.Max(0, panelExtent + manualOffset);
                newStart = baseStart + reserve;
                newSize = baseSize - reserve;
                panelStart = baseStart + manualOffset;
            }
            else
            {
                panelStart = parentExtent - panelExtent - manualOffset;
                newStart = baseStart;
                newSize = panelStart - baseStart;
            }

            if (newSize <= 0) return false;

            applied = axis == TaskbarShrinkAxis.Horizontal
                ? new Rectangle(newStart, observed.Top, newSize, observed.Height)
                : new Rectangle(observed.Left, newStart, observed.Width, newSize);

            return true;
        }

        /// <summary>记录本次已施加的矩形（供下一次 Tick 做幂等比较）。</summary>
        public void Commit(Rectangle applied, TaskbarShrinkAxis axis)
        {
            Applied = applied;
            HasApplied = true;
            Reservation = Math.Max(0, Size(Baseline, axis) - Size(applied, axis));
        }

        /// <summary>
        /// 交回基线（撤销占位用）并清空状态。
        /// <para>★ 调用方负责把基线矩形写回任务列表窗口。</para>
        /// </summary>
        public bool TryTakeBaseline(out Rectangle baseline)
        {
            baseline = Baseline;
            bool ok = HasBaseline;
            Reset();
            return ok;
        }

        /// <summary>
        /// 由"系统改动后的观测值"推导新的基线。
        /// <para>★ 两种情形：</para>
        /// <list type="bullet">
        /// <item><b>在已挤压的基础上又被系统改动</b>（常态）：把本次占位量从观测值里扣掉
        /// ⇒ 新基线 = 观测值 + 占位量（沿收缩轴），起始边按模式回退。
        /// 施加后的尺寸 == 系统给的尺寸 ⇒ <b>不会被二次压缩</b>。</item>
        /// <item><b>系统整体重排</b>（把任务列表搬回了自然位置）：观测值本身就是新基线，
        /// 不再扣除占位量 ⇒ 面板不会漂移。</item>
        /// </list>
        /// </summary>
        private Rectangle DeriveBaseline(Rectangle observed, TaskbarShrinkAxis axis, TaskbarShrinkMode mode)
        {
            int reserve = Math.Max(0, Reservation);
            int observedStart = Start(observed, axis);
            int observedSize = Size(observed, axis);
            int baselineSize = Size(Baseline, axis);
            int appliedStart = Start(Applied, axis);

            bool fullReset =
                (mode == TaskbarShrinkMode.FromStart && observedStart < appliedStart - Tolerance)
                || observedSize >= baselineSize;

            int newStart;
            int newSize;
            if (fullReset)
            {
                newStart = observedStart;
                newSize = observedSize;
            }
            else
            {
                newSize = observedSize + reserve;
                newStart = mode == TaskbarShrinkMode.FromStart ? observedStart - reserve : observedStart;
            }

            return axis == TaskbarShrinkAxis.Horizontal
                ? new Rectangle(newStart, observed.Top, newSize, observed.Height)
                : new Rectangle(observed.Left, newStart, observed.Width, newSize);
        }

        private static int Start(Rectangle rect, TaskbarShrinkAxis axis) => axis == TaskbarShrinkAxis.Horizontal ? rect.Left : rect.Top;

        private static int End(Rectangle rect, TaskbarShrinkAxis axis) => axis == TaskbarShrinkAxis.Horizontal ? rect.Right : rect.Bottom;

        private static int Size(Rectangle rect, TaskbarShrinkAxis axis) => axis == TaskbarShrinkAxis.Horizontal ? rect.Width : rect.Height;

        private static bool SameRect(Rectangle a, Rectangle b)
        {
            return Math.Abs(a.Left - b.Left) <= Tolerance
                && Math.Abs(a.Top - b.Top) <= Tolerance
                && Math.Abs(a.Width - b.Width) <= Tolerance
                && Math.Abs(a.Height - b.Height) <= Tolerance;
        }
    }
}
