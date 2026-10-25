using System;
using System.Drawing;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 任务栏**停靠方向**（★ P-F05 新增显式枚举）。
    /// <para>★ 旧实现没有方向枚举：Win10 靠"任务栏宽高比"猜垂直/水平
    /// （<c>rcBar.Height &gt; rcBar.Width</c>），业务层靠"任务栏矩形宽高比"猜
    /// （<c>_taskbarRect.Height &gt; _taskbarRect.Width</c>）⇒ 高 DPI、多行任务栏、
    /// 第三方任务栏下都会误判（#568）。</para>
    /// <para>★ 现在方向由**任务栏相对其所属显示器的物理矩形**解析（见
    /// <see cref="TaskbarDockResolver"/>），不再依赖任何宽高比启发式。</para>
    /// </summary>
    public enum TaskbarDockDirection
    {
        /// <summary>方向未知（几何缺失 / 失败 / 过期 —— 三种情形各有各的来源，见解析器注释）。</summary>
        Unknown = 0,

        /// <summary>停靠在显示器**顶部**。</summary>
        Top = 1,

        /// <summary>停靠在显示器**底部**（Windows 10 / 11 原生默认）。</summary>
        Bottom = 2,

        /// <summary>停靠在显示器**左侧**（原生 Windows 不提供 ⇒ 第三方任务栏）。</summary>
        Left = 3,

        /// <summary>停靠在显示器**右侧**（原生 Windows 不提供 ⇒ 第三方任务栏）。</summary>
        Right = 4,
    }

    /// <summary>
    /// 任务栏**兼容性**判定（母计划 F05 第 5 步）。
    /// <para>★ 原生 Windows 10 / 11 的任务栏只提供"底部"停靠；
    /// 左 / 右 / 顶部方向一律来自第三方任务栏（StartAllBack、ExplorerPatcher 等），
    /// 本仓**未验证** ⇒ 必须显式标注为"兼容性待验"，不得声称原生支持。</para>
    /// </summary>
    public enum TaskbarCompatibility
    {
        /// <summary>原生支持（当前系统 + 当前停靠方向都由系统自身提供）。</summary>
        Native = 0,

        /// <summary>第三方任务栏方向 ⇒ **兼容性待验**（本仓尚未验证）。</summary>
        ThirdPartyPending = 1,

        /// <summary>方向未知 ⇒ 无法判定兼容性。</summary>
        Unknown = 2,
    }

    /// <summary>
    /// 任务栏几何快照（★ P-F05）。
    /// <para>★ <b>坐标空间契约</b>：本结构内所有矩形一律为<b>物理屏幕像素</b>
    /// （Per-Monitor-V2 感知进程下 <c>GetWindowRect</c> / <c>Screen</c> 给出的物理像素）。
    /// 需要落到父窗口（任务栏 / ReBar）客户区坐标时，必须<b>显式转换</b>
    /// （见 <c>NativeMethods.ScreenToClient</c>），不得把屏幕坐标直接当客户区坐标用。</para>
    /// </summary>
    public readonly struct TaskbarGeometry
    {
        /// <summary>空快照（几何缺失）。</summary>
        public static readonly TaskbarGeometry Empty = new TaskbarGeometry();

        public TaskbarGeometry(
            Rectangle taskbarRect,
            TaskbarDockDirection direction,
            Rectangle screenBounds,
            Rectangle workingArea,
            uint dpi,
            string deviceName,
            int panelWidth,
            int panelHeight,
            bool alignLeft,
            int manualOffset,
            bool centerAligned,
            int widgetsWidth,
            TaskbarCompatibility compatibility)
        {
            TaskbarRect = taskbarRect;
            Direction = direction;
            ScreenBounds = screenBounds;
            WorkingArea = workingArea;
            Dpi = dpi;
            DeviceName = deviceName ?? string.Empty;
            PanelWidth = panelWidth;
            PanelHeight = panelHeight;
            AlignLeft = alignLeft;
            ManualOffset = manualOffset;
            CenterAligned = centerAligned;
            WidgetsWidth = widgetsWidth;
            Compatibility = compatibility;
        }

        /// <summary>任务栏矩形（物理屏幕像素）。</summary>
        public Rectangle TaskbarRect { get; }

        /// <summary>停靠方向（显式枚举）。</summary>
        public TaskbarDockDirection Direction { get; }

        /// <summary>所属显示器边界（物理屏幕像素）。</summary>
        public Rectangle ScreenBounds { get; }

        /// <summary>所属显示器工作区（物理屏幕像素）。</summary>
        public Rectangle WorkingArea { get; }

        /// <summary>任务栏窗口 DPI（96 = 100%）。</summary>
        public uint Dpi { get; }

        /// <summary>所属显示器设备名（如 <c>\\.\DISPLAY1</c>）。</summary>
        public string DeviceName { get; }

        /// <summary>面板宽度（物理像素）。</summary>
        public int PanelWidth { get; }

        /// <summary>面板高度（物理像素）。</summary>
        public int PanelHeight { get; }

        /// <summary>面板是否靠左（有效对齐 = 用户设置 ∧ 系统居中）。</summary>
        public bool AlignLeft { get; }

        /// <summary>手动偏移（物理像素）。</summary>
        public int ManualOffset { get; }

        /// <summary>系统任务栏是否居中对齐（注册表 <c>TaskbarAl</c>）。</summary>
        public bool CenterAligned { get; }

        /// <summary>系统小组件区宽度（物理像素，Win11 专属）。</summary>
        public int WidgetsWidth { get; }

        /// <summary>兼容性判定（原生 / 第三方待验 / 未知）。</summary>
        public TaskbarCompatibility Compatibility { get; }

        /// <summary>是否为垂直停靠（左/右）。</summary>
        public bool IsVertical => Direction == TaskbarDockDirection.Left || Direction == TaskbarDockDirection.Right;

        /// <summary>是否为水平停靠（上/下）。</summary>
        public bool IsHorizontal => Direction == TaskbarDockDirection.Top || Direction == TaskbarDockDirection.Bottom;
    }

    /// <summary>
    /// 几何缓存键（★ P-F05 / #539）。
    /// <para>★ 任一分量变化 ⇒ <b>整体失效</b>，不得只更新其中一部分。
    /// 分量清单（与母计划 F05 第 3 步一一对应）：</para>
    /// <list type="bullet">
    /// <item><b>根 / 子 HWND 代次</b>：<see cref="RootHandle"/>（任务栏根）与
    /// <see cref="InnerHandle"/>（策略内部子宿主：Win10 = ReBarWindow32）。
    /// 句柄值本身就是代次标识 —— Explorer 重建子层时句柄必变。</item>
    /// <item><b>目标显示器</b>：<see cref="DeviceName"/>。</item>
    /// <item><b>屏幕 / 工作区矩形</b>：<see cref="ScreenBounds"/> / <see cref="WorkingArea"/>。</item>
    /// <item><b>DPI</b>：<see cref="Dpi"/>。</item>
    /// <item><b>方向</b>：<see cref="Direction"/>。</item>
    /// <item><b>面板尺寸</b>：<see cref="PanelWidth"/> / <see cref="PanelHeight"/>。</item>
    /// <item><b>相关任务栏设置</b>：<see cref="AlignLeft"/> / <see cref="ManualOffset"/> /
    /// <see cref="CenterAligned"/> / <see cref="WidgetsWidth"/>。</item>
    /// </list>
    /// </summary>
    public readonly struct TaskbarGeometryKey : IEquatable<TaskbarGeometryKey>
    {
        public TaskbarGeometryKey(
            IntPtr rootHandle,
            IntPtr innerHandle,
            string deviceName,
            Rectangle screenBounds,
            Rectangle workingArea,
            uint dpi,
            TaskbarDockDirection direction,
            int panelWidth,
            int panelHeight,
            bool alignLeft,
            int manualOffset,
            bool centerAligned,
            int widgetsWidth)
        {
            RootHandle = rootHandle;
            InnerHandle = innerHandle;
            DeviceName = deviceName ?? string.Empty;
            ScreenBounds = screenBounds;
            WorkingArea = workingArea;
            Dpi = dpi;
            Direction = direction;
            PanelWidth = panelWidth;
            PanelHeight = panelHeight;
            AlignLeft = alignLeft;
            ManualOffset = manualOffset;
            CenterAligned = centerAligned;
            WidgetsWidth = widgetsWidth;
        }

        /// <summary>任务栏根窗口句柄（代次标识）。</summary>
        public IntPtr RootHandle { get; }

        /// <summary>策略内部子宿主句柄（代次标识）。</summary>
        public IntPtr InnerHandle { get; }

        /// <summary>目标显示器设备名。</summary>
        public string DeviceName { get; }

        /// <summary>显示器边界（物理像素）。</summary>
        public Rectangle ScreenBounds { get; }

        /// <summary>显示器工作区（物理像素）。</summary>
        public Rectangle WorkingArea { get; }

        /// <summary>任务栏 DPI。</summary>
        public uint Dpi { get; }

        /// <summary>停靠方向。</summary>
        public TaskbarDockDirection Direction { get; }

        /// <summary>面板宽度。</summary>
        public int PanelWidth { get; }

        /// <summary>面板高度。</summary>
        public int PanelHeight { get; }

        /// <summary>有效对齐（靠左）。</summary>
        public bool AlignLeft { get; }

        /// <summary>手动偏移。</summary>
        public int ManualOffset { get; }

        /// <summary>系统任务栏居中对齐。</summary>
        public bool CenterAligned { get; }

        /// <summary>系统小组件区宽度。</summary>
        public int WidgetsWidth { get; }

        public bool Equals(TaskbarGeometryKey other)
        {
            return RootHandle == other.RootHandle
                && InnerHandle == other.InnerHandle
                && string.Equals(DeviceName, other.DeviceName, StringComparison.Ordinal)
                && ScreenBounds == other.ScreenBounds
                && WorkingArea == other.WorkingArea
                && Dpi == other.Dpi
                && Direction == other.Direction
                && PanelWidth == other.PanelWidth
                && PanelHeight == other.PanelHeight
                && AlignLeft == other.AlignLeft
                && ManualOffset == other.ManualOffset
                && CenterAligned == other.CenterAligned
                && WidgetsWidth == other.WidgetsWidth;
        }

        public override bool Equals(object? obj) => obj is TaskbarGeometryKey other && Equals(other);

        public override int GetHashCode()
        {
            // ★ HashCode.Combine 最多 8 个参数 ⇒ 这里分两段组合（13 个分量）。
            int head = HashCode.Combine(
                RootHandle,
                InnerHandle,
                DeviceName,
                ScreenBounds,
                WorkingArea,
                Dpi);
            int tail = HashCode.Combine(
                (int)Direction,
                PanelWidth,
                PanelHeight,
                AlignLeft,
                ManualOffset,
                CenterAligned,
                WidgetsWidth);
            return HashCode.Combine(head, tail);
        }

        public static bool operator ==(TaskbarGeometryKey left, TaskbarGeometryKey right) => left.Equals(right);

        public static bool operator !=(TaskbarGeometryKey left, TaskbarGeometryKey right) => !left.Equals(right);
    }

    /// <summary>
    /// 停靠方向解析器（★ P-F05，**纯函数** ⇒ 可被单元测试直接调用）。
    /// <para>★ 判定依据：任务栏矩形**相对其所属显示器**的物理关系，
    /// <b>不使用任何宽高比启发式</b>（旧实现的
    /// <c>rcBar.Height &gt; rcBar.Width</c> 会在高 DPI / 多行任务栏 / 第三方任务栏下误判）。</para>
    /// <para>★ 三级判定（先几何、后工作区、最后才放弃）：</para>
    /// <list type="number">
    /// <item><b>几何</b>：任务栏紧贴显示器某条边，且在垂直于该边的方向上覆盖了
    /// 显示器该方向的 90% 以上 ⇒ 该边即停靠边。
    /// <para>（覆盖不全 ⇒ 说明它是"浮动任务栏"或第三方任务栏 ⇒ 不走这一级。）</para></item>
    /// <item><b>工作区</b>：显示器工作区在某一侧被"预留"出了条带 ⇒ 该侧即停靠边。
    /// <para>（自动隐藏任务栏的工作区 == 屏幕边界 ⇒ 四侧都没有预留 ⇒ 这一级也判不出。）</para></item>
    /// <item>两者都判不出 ⇒ <see cref="TaskbarDockDirection.Unknown"/>。
    /// <para>★ <b>0 是有效数据</b>：Unknown 表示"缺失 / 失败 / 过期"，
    /// 调用方必须显式处理，不得回退到宽高比猜测。</para></item>
    /// </list>
    /// </summary>
    public static class TaskbarDockResolver
    {
        /// <summary>判定"任务栏在该方向上是否覆盖了显示器该方向的绝大部分"。</summary>
        private const double SpanCoverageRatio = 0.9;

        /// <summary>
        /// 解析停靠方向。
        /// </summary>
        /// <param name="taskbarRect">任务栏矩形（物理屏幕像素）。</param>
        /// <param name="monitorBounds">任务栏**所属显示器**的边界（物理屏幕像素，可为负原点）。</param>
        /// <param name="workingArea">所属显示器的工作区（物理屏幕像素）。</param>
        public static TaskbarDockDirection Resolve(Rectangle taskbarRect, Rectangle monitorBounds, Rectangle workingArea)
        {
            if (taskbarRect.Width <= 0 || taskbarRect.Height <= 0) return TaskbarDockDirection.Unknown;
            if (monitorBounds.Width <= 0 || monitorBounds.Height <= 0) return TaskbarDockDirection.Unknown;

            // ① 几何判定：紧贴哪条边 + 垂直于该边是否覆盖了显示器的绝大部分
            int tolerance = Math.Max(2, Math.Min(taskbarRect.Width, taskbarRect.Height) / 2);
            bool coversWidth = taskbarRect.Width >= monitorBounds.Width * SpanCoverageRatio;
            bool coversHeight = taskbarRect.Height >= monitorBounds.Height * SpanCoverageRatio;

            bool top = coversWidth && taskbarRect.Top <= monitorBounds.Top + tolerance;
            bool bottom = coversWidth && taskbarRect.Bottom >= monitorBounds.Bottom - tolerance;
            bool left = coversHeight && taskbarRect.Left <= monitorBounds.Left + tolerance;
            bool right = coversHeight && taskbarRect.Right >= monitorBounds.Right - tolerance;

            int candidates = (top ? 1 : 0) + (bottom ? 1 : 0) + (left ? 1 : 0) + (right ? 1 : 0);
            if (candidates == 1)
            {
                if (top) return TaskbarDockDirection.Top;
                if (bottom) return TaskbarDockDirection.Bottom;
                if (left) return TaskbarDockDirection.Left;
                return TaskbarDockDirection.Right;
            }

            // ② 工作区判定：哪一侧被预留出了条带
            if (workingArea.Width > 0 && workingArea.Height > 0)
            {
                int reservedTop = workingArea.Top - monitorBounds.Top;
                int reservedBottom = monitorBounds.Bottom - workingArea.Bottom;
                int reservedLeft = workingArea.Left - monitorBounds.Left;
                int reservedRight = monitorBounds.Right - workingArea.Right;

                int max = Math.Max(Math.Max(reservedTop, reservedBottom), Math.Max(reservedLeft, reservedRight));
                if (max > 0)
                {
                    int count = (reservedTop == max ? 1 : 0) + (reservedBottom == max ? 1 : 0)
                              + (reservedLeft == max ? 1 : 0) + (reservedRight == max ? 1 : 0);
                    if (count == 1)
                    {
                        if (reservedTop == max) return TaskbarDockDirection.Top;
                        if (reservedBottom == max) return TaskbarDockDirection.Bottom;
                        if (reservedLeft == max) return TaskbarDockDirection.Left;
                        return TaskbarDockDirection.Right;
                    }
                }
            }

            // ③ 几何候选多于一个（退化矩形）⇒ 保持 Unknown，交由调用方显式处理
            return TaskbarDockDirection.Unknown;
        }

        /// <summary>
        /// 兼容性判定：原生 Windows 只提供"底部"停靠；
        /// 左 / 右 / 顶部方向一律来自第三方任务栏 ⇒ <b>兼容性待验</b>。
        /// </summary>
        public static TaskbarCompatibility ResolveCompatibility(TaskbarDockDirection direction)
        {
            switch (direction)
            {
                case TaskbarDockDirection.Bottom:
                    return TaskbarCompatibility.Native;
                case TaskbarDockDirection.Top:
                case TaskbarDockDirection.Left:
                case TaskbarDockDirection.Right:
                    return TaskbarCompatibility.ThirdPartyPending;
                default:
                    return TaskbarCompatibility.Unknown;
            }
        }
    }
}
