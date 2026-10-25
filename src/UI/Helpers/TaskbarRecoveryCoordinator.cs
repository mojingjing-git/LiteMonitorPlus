using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 任务栏宿主生命周期状态（P-F04 / 母计划 F04 第 1 步）。
    /// <para>状态语义：</para>
    /// <list type="bullet">
    /// <item><see cref="WaitingForTaskbar"/>：目标任务栏**尚未就绪**（副屏任务栏没起来 / 目标显示器不在）。
    /// 此时**保持等待**，绝不静默改绑到主屏。</item>
    /// <item><see cref="Attached"/>：面板已经**确实**挂在目标任务栏上（根句柄 + 内层子句柄 +
    /// 实际父子关系 + 目标显示器 + 本窗句柄代次 全部校验通过）。</item>
    /// <item><see cref="Recovering"/>：有恢复请求在排队，或正在执行一次恢复动作。</item>
    /// <item><see cref="Closing"/>：应用正在关闭 —— 取消排队、注销事件、**禁止复活**窗口。</item>
    /// </list>
    /// </summary>
    public enum TaskbarHostState
    {
        WaitingForTaskbar = 0,
        Attached = 1,
        Recovering = 2,
        Closing = 3,
    }

    /// <summary>
    /// 触发任务栏恢复的原因。多个来源**合并**到同一条恢复请求上（统一去抖），
    /// 便于诊断"这次恢复是被谁触发的"。
    /// </summary>
    public enum TaskbarRecoveryReason
    {
        /// <summary>启动时首次挂载。</summary>
        Startup = 0,

        /// <summary>任务栏根句柄或面板句柄失效（轮询发现）。</summary>
        HostInvalid = 1,

        /// <summary>面板自身的 HWND 被外部销毁（父任务栏窗口被销毁 ⇒ 子窗口一并销毁）。</summary>
        HandleDestroyed = 2,

        /// <summary>Explorer 重启（TaskbarCreated 广播）。</summary>
        ExplorerRestarted = 3,

        /// <summary>电源恢复 / 睡眠唤醒（WM_POWERBROADCAST）。</summary>
        PowerResume = 4,

        /// <summary>锁屏解锁 / 会话变化（WM_WTSSESSION_CHANGE）。</summary>
        SessionChange = 5,

        /// <summary>显示拓扑或分辨率变化（WM_DISPLAYCHANGE）。</summary>
        DisplayChange = 6,

        /// <summary>DPI 变化（WM_DPICHANGED / OnDpiChanged）。</summary>
        DpiChanged = 7,

        /// <summary>显示设置变化（WM_SETTINGCHANGE：任务栏大小/位置/自动隐藏）。</summary>
        SettingChange = 8,
    }

    /// <summary>
    /// 显示器的**值类型**快照。
    /// <para>★ 之所以不直接用 <see cref="Screen"/>：<see cref="Screen"/> 没有公开构造器，
    /// 单元测试无法构造 ⇒ 显示器拓扑相关的判定就无法被真实调用验证。这里用值类型 + 接口
    /// 把"显示器拓扑"变成可注入的接缝，生产实现仍然来自 <see cref="Screen"/>。</para>
    /// </summary>
    public readonly struct TaskbarMonitorInfo
    {
        public TaskbarMonitorInfo(string deviceName, Rectangle bounds, Rectangle workingArea, bool isPrimary)
        {
            DeviceName = deviceName ?? string.Empty;
            Bounds = bounds;
            WorkingArea = workingArea;
            IsPrimary = isPrimary;
        }

        /// <summary>设备名（如 <c>\\.\DISPLAY1</c>）。</summary>
        public string DeviceName { get; }

        /// <summary>屏幕边界（物理像素）。</summary>
        public Rectangle Bounds { get; }

        /// <summary>工作区（物理像素，已扣除任务栏等占位）。</summary>
        public Rectangle WorkingArea { get; }

        /// <summary>是否主屏。</summary>
        public bool IsPrimary { get; }
    }

    /// <summary>
    /// 显示器拓扑来源。生产实现见 <see cref="WinFormsTaskbarScreenProvider"/>；
    /// 测试可注入**假拓扑**，从而在**不依赖真实多屏/RDP**的前提下验证
    /// "副屏未就绪 ⇒ 保持等待、不静默绑定主屏"。
    /// </summary>
    public interface ITaskbarScreenProvider
    {
        /// <summary>当前全部显示器。</summary>
        IReadOnlyList<TaskbarMonitorInfo> GetMonitors();

        /// <summary>指定窗口所在显示器（找不到返回 null）。</summary>
        TaskbarMonitorInfo? GetMonitorForHandle(IntPtr hWnd);

        /// <summary>与指定矩形相交最多的显示器（找不到返回 null）。</summary>
        TaskbarMonitorInfo? GetMonitorForRectangle(Rectangle rect);
    }

    /// <summary>
    /// 基于 WinForms <see cref="Screen"/> 的默认显示器拓扑来源（生产路径）。
    /// </summary>
    public sealed class WinFormsTaskbarScreenProvider : ITaskbarScreenProvider
    {
        /// <summary>进程内共享实例（显示器拓扑是全局事实，不需要每个 helper 一份）。</summary>
        public static readonly WinFormsTaskbarScreenProvider Instance = new WinFormsTaskbarScreenProvider();

        public IReadOnlyList<TaskbarMonitorInfo> GetMonitors()
        {
            var list = new List<TaskbarMonitorInfo>();
            try
            {
                foreach (var screen in Screen.AllScreens)
                {
                    list.Add(ToInfo(screen));
                }
            }
            catch
            {
                // 显示器枚举失败（会话切换中）⇒ 返回空表，调用方按"目标不存在"处理。
            }
            return list;
        }

        public TaskbarMonitorInfo? GetMonitorForHandle(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return null;
            try
            {
                var screen = Screen.FromHandle(hWnd);
                return screen == null ? null : ToInfo(screen);
            }
            catch
            {
                return null;
            }
        }

        public TaskbarMonitorInfo? GetMonitorForRectangle(Rectangle rect)
        {
            try
            {
                var screen = Screen.FromRectangle(rect);
                return screen == null ? null : ToInfo(screen);
            }
            catch
            {
                return null;
            }
        }

        private static TaskbarMonitorInfo ToInfo(Screen screen)
        {
            return new TaskbarMonitorInfo(screen.DeviceName, screen.Bounds, screen.WorkingArea, screen.Primary);
        }
    }

    /// <summary>
    /// 一次"目标任务栏宿主解析"的结果。
    /// <para>★ 关键语义：<b>目标显示器缺失时不得回退到主屏</b>。旧实现回退主屏 ⇒
    /// 拿到的句柄**有效** ⇒ <c>IsTaskbarValid()</c> 恒真 ⇒ 永不重解析 ⇒
    /// 开机竞态（#573）与 RDP 后错屏（#583）永远锁死在错误屏幕上。</para>
    /// </summary>
    public readonly struct TaskbarHostResolution
    {
        public TaskbarHostResolution(
            IntPtr root,
            IntPtr inner,
            bool targetDeviceSpecified,
            bool targetMonitorPresent,
            bool rootAlive,
            bool innerAlive)
        {
            Root = root;
            Inner = inner;
            TargetDeviceSpecified = targetDeviceSpecified;
            TargetMonitorPresent = targetMonitorPresent;
            RootAlive = rootAlive;
            InnerAlive = innerAlive;
        }

        /// <summary>任务栏根窗口句柄（<c>Shell_TrayWnd</c> / <c>Shell_SecondaryTrayWnd</c>）。</summary>
        public IntPtr Root { get; }

        /// <summary>内层子句柄（主屏的托盘窗口等）；副屏没有内层时为 <see cref="IntPtr.Zero"/>。</summary>
        public IntPtr Inner { get; }

        /// <summary>用户是否显式指定了目标显示器。</summary>
        public bool TargetDeviceSpecified { get; }

        /// <summary>目标显示器当前是否存在。</summary>
        public bool TargetMonitorPresent { get; }

        /// <summary>根句柄当前是否仍是有效窗口。</summary>
        public bool RootAlive { get; }

        /// <summary>内层子句柄当前是否仍是有效窗口（没有内层时恒为 true）。</summary>
        public bool InnerAlive { get; }

        /// <summary>是否可以尝试挂载。</summary>
        public bool IsReady => TargetMonitorPresent && RootAlive && Root != IntPtr.Zero;
    }

    /// <summary>
    /// 任务栏宿主接缝：恢复协调器**只**依赖这个接口。
    /// <para>★ 生产实现由 <c>TaskbarForm</c> / <c>TaskbarBizHelper</c> 提供；
    /// 测试用**真实 Win32 窗口**实现同一接口，从而验证真实的父/子窗口销毁语义，
    /// 而不是"复制一份算法自证"。</para>
    /// </summary>
    public interface ITaskbarHostBridge
    {
        /// <summary>解析目标任务栏宿主；<b>不得</b>静默回退到主屏。</summary>
        TaskbarHostResolution ResolveTarget();

        /// <summary>
        /// 面板当前是否**确实**挂在解析结果上：根句柄有效 + 内层子句柄有效 +
        /// 实际父子关系正确 + 位于目标显示器 + 本窗句柄代次未变。
        /// </summary>
        bool IsPanelAttached(TaskbarHostResolution resolution);

        /// <summary>执行一次挂载（必要时重建面板自身的 HWND）。</summary>
        void AttachToTarget(TaskbarHostResolution resolution);
    }

    /// <summary>
    /// 任务栏窗口查找来源（可注入 ⇒ 解析逻辑能在**真实 HWND** 上被验证）。
    /// <para>★ 生产实现走 Win32；测试实现把"任务栏根 / 内层子窗口"映射到测试宿主自己创建的窗口，
    /// 从而**不触碰**真实的 <c>Shell_TrayWnd</c>。</para>
    /// </summary>
    public interface ITaskbarWindowSource
    {
        /// <summary>按窗口类名查找顶层窗口。</summary>
        IntPtr FindWindow(string className, string? windowName);

        /// <summary>按窗口类名查找子窗口。</summary>
        IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? windowName);
    }

    /// <summary>基于 Win32 API 的默认窗口查找来源（生产路径）。</summary>
    public sealed class Win32TaskbarWindowSource : ITaskbarWindowSource
    {
        /// <summary>进程内共享实例。</summary>
        public static readonly Win32TaskbarWindowSource Instance = new Win32TaskbarWindowSource();

        public IntPtr FindWindow(string className, string? windowName)
        {
            return NativeMethods.FindWindow(className, windowName);
        }

        public IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? windowName)
        {
            return NativeMethods.FindWindowEx(parent, after, className, windowName);
        }
    }

    /// <summary>
    /// 面板 HWND 的受控重建器（P-F04）。
    /// <para>★ 任务栏根窗口被销毁 ⇒ 作为其子窗口的面板**一并被销毁**（Windows 语义，已实测）。
    /// 这里负责把面板的 HWND 重建出来，随后由 <see cref="ITaskbarHostBridge"/> 重新挂载。</para>
    /// <para>★ 重建**不在销毁回调里发生**：销毁回调只投递恢复请求，由 UI 消息循环里的
    /// 恢复流程调用本重建器（母计划 F04 第 3 步：避免递归重建）。</para>
    /// </summary>
    public sealed class TaskbarPanelHandleKeeper
    {
        private readonly Form _form;
        private readonly Action? _destroyStaleHandle;

        /// <param name="form">面板窗体。</param>
        /// <param name="destroyStaleHandle">
        /// "丢弃已失效但 WinForms 仍认为存在的句柄"的动作。
        /// <para>★ <c>Control.DestroyHandle()</c> 是 protected ⇒ 由宿主（Form 子类）提供，
        /// 本类只决定**什么时候**需要丢弃。</para>
        /// </param>
        public TaskbarPanelHandleKeeper(Form form, Action? destroyStaleHandle = null)
        {
            _form = form ?? throw new ArgumentNullException(nameof(form));
            _destroyStaleHandle = destroyStaleHandle;
        }

        /// <summary>
        /// 确保面板 HWND 存在；返回 true 表示现在持有一个**有效**的 HWND。
        /// </summary>
        public bool EnsureHandle()
        {
            if (_form.IsHandleCreated && NativeMethods.IsWindow(_form.Handle)) return true;

            try
            {
                if (_form.IsHandleCreated)
                {
                    // 句柄还在、但窗口已经被销毁（WinForms 没收到销毁通知）
                    // ⇒ 受控地丢弃这个失效句柄，再重建。
                    _destroyStaleHandle?.Invoke();
                }

                _form.CreateControl();
            }
            catch
            {
                // 重建失败 ⇒ 交给下一轮恢复重试（协调器保证不永久停止）。
            }

            return _form.IsHandleCreated && NativeMethods.IsWindow(_form.Handle);
        }
    }

    /// <summary>
    /// 面板可见性闸门：<b>只有确实挂在目标任务栏上的面板才允许可见</b>。
    /// <para>★ 为什么需要它：目标屏（或副屏任务栏）还没就绪时，面板尚未被
    /// <c>SetParent</c> 到任何任务栏上 —— 此时若把它显示出来，它会以**顶层窗口**的形态
    /// 浮在桌面上（一个用户可见的缺陷）。正确语义是"暂不可用" ⇒ <b>不显示</b>，
    /// 等挂载成功后再显示。</para>
    /// <para>★ 这条闸门也解释了"目标屏缺失时不静默绑定主屏"的**可见**后果：
    /// 面板会暂时消失（而不是跑到主屏），这正是 #583 报告者要求的行为。</para>
    /// </summary>
    public sealed class TaskbarPanelVisibilityGate
    {
        private bool _attached;
        private bool _showRequested;

        /// <summary>当前是否已确认挂载成功。</summary>
        public bool IsAttached => _attached;

        /// <summary>是否有被推迟的显示请求。</summary>
        public bool HasPendingShow => _showRequested;

        /// <summary>挂载成功后调用：此后显示请求立即兑现。</summary>
        public void ConfirmAttached()
        {
            _attached = true;
        }

        /// <summary>是否应当抑制这次"显示"（尚未挂载 ⇒ 抑制）。</summary>
        public bool ShouldSuppressShow => !_attached;

        /// <summary>记录一次被抑制的显示请求（挂载成功后补一次）。</summary>
        public void RequestShow()
        {
            _showRequested = true;
        }

        /// <summary>
        /// 挂载成功后取一次"应当显示"的请求。
        /// <para>★ 只兑现一次，避免重复 <c>Show()</c>。</para>
        /// </summary>
        public bool TryConsumeShowRequest()
        {
            if (!_attached || !_showRequested) return false;
            _showRequested = false;
            return true;
        }
    }

    /// <summary>
    /// 任务栏恢复协调器（P-F04 新增，母计划 F04 第 1/3/4/5 步的实现体）。
    /// <para>★ 设计约束（拆解计划 §1.2）：</para>
    /// <list type="number">
    /// <item><b>恢复必须有界</b>：恢复期每 2 秒一次、持续 10 秒；期后低频退避
    /// （5s → 10s → 20s → 40s → 60s 封顶）持续等待目标重现，**不永久停止**，
    /// 也不新增高频轮询线程。</item>
    /// <item><b>单次在途</b>：同一时刻只允许一次恢复动作在执行。</item>
    /// <item><b>UI 线程只投递恢复请求</b>：<see cref="RequestRecovery"/> 只记录请求；
    /// 真正的解析/挂载发生在 <see cref="Pump"/> 里（由 UI 定时器驱动）。</item>
    /// <item><b>时间一律 TimeProvider</b>：不读墙上时间，测试可注入假时钟，不需要 Sleep。</item>
    /// <item><b>关闭优先</b>：<see cref="BeginClosing"/> 之后所有请求被丢弃，永不复活窗口。</item>
    /// </list>
    /// </summary>
    public sealed class TaskbarRecoveryCoordinator
    {
        /// <summary>合并去抖窗口：短时间内的多个事件（显示变化 + 设置变化 + 电源事件）只触发一次恢复。</summary>
        public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);

        /// <summary>恢复期内的重试间隔（母计划 F04 第 4 步：每 2 秒一次）。</summary>
        public static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(2);

        /// <summary>恢复期长度（母计划 F04 第 4 步：持续 10 秒）。</summary>
        public static readonly TimeSpan RecoveryWindow = TimeSpan.FromSeconds(10);

        /// <summary>恢复期之后的退避基数。</summary>
        public static readonly TimeSpan BackoffBase = TimeSpan.FromSeconds(5);

        /// <summary>退避上限（到达后保持此频率等待目标重现，不永久停止）。</summary>
        public static readonly TimeSpan BackoffMax = TimeSpan.FromSeconds(60);

        private readonly ITaskbarHostBridge _bridge;
        private readonly TimeProvider _time;
        private readonly object _gate = new object();

        private TaskbarHostState _state = TaskbarHostState.WaitingForTaskbar;
        private TaskbarRecoveryReason _lastReason = TaskbarRecoveryReason.Startup;
        private bool _pending;
        private bool _inFlight;
        private long _windowEnd;
        private long _nextAttemptAt;
        private bool _hasNextAttempt;
        private TimeSpan _backoff = BackoffBase;
        private int _attemptCount;

        /// <param name="bridge">任务栏宿主接缝（生产实现 = TaskbarForm）。</param>
        /// <param name="timeProvider">可注入的单调时钟，默认 <see cref="TimeProvider.System"/>。</param>
        public TaskbarRecoveryCoordinator(ITaskbarHostBridge bridge, TimeProvider? timeProvider = null)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _time = timeProvider ?? TimeProvider.System;
        }

        /// <summary>当前状态。</summary>
        public TaskbarHostState State
        {
            get { lock (_gate) return _state; }
        }

        /// <summary>最近一次恢复请求的原因（诊断用）。</summary>
        public TaskbarRecoveryReason LastReason
        {
            get { lock (_gate) return _lastReason; }
        }

        /// <summary>是否还有排队的恢复请求。</summary>
        public bool IsPending
        {
            get { lock (_gate) return _pending; }
        }

        /// <summary>已执行的恢复尝试次数。</summary>
        public int AttemptCount
        {
            get { lock (_gate) return _attemptCount; }
        }

        /// <summary>
        /// 投递一次恢复请求（**只投递**，不执行任何窗口动作）。
        /// <para>★ 关闭过程中一律忽略。</para>
        /// </summary>
        /// <param name="reason">触发原因（多个来源会合并到同一条请求上）。</param>
        /// <param name="immediate">
        /// 是否**立即**到期（首次挂载用）。默认走统一去抖：短时间内的多个事件
        /// （显示变化 + 设置变化 + 电源事件）只触发一次恢复。
        /// </param>
        public void RequestRecovery(TaskbarRecoveryReason reason, bool immediate = false)
        {
            lock (_gate)
            {
                if (_state == TaskbarHostState.Closing) return;

                _lastReason = reason;
                _pending = true;
                _state = TaskbarHostState.Recovering;

                long now = _time.GetTimestamp();
                _windowEnd = now + ToTicks(RecoveryWindow);

                if (immediate)
                {
                    _nextAttemptAt = now;
                    _hasNextAttempt = true;
                    return;
                }

                // 去抖：多个来源的事件合并到"最早的一次到期"。
                long due = now + ToTicks(DebounceDelay);
                if (!_hasNextAttempt || due < _nextAttemptAt)
                {
                    _nextAttemptAt = due;
                    _hasNextAttempt = true;
                }
            }
        }

        /// <summary>
        /// 轻量巡检请求：只有在**当前没有排队的恢复**时才安排一次。
        /// <para>★ 用途：UI 定时 Tick 里的廉价存活检查（根句柄 / 面板句柄是否还有效）。</para>
        /// <para>★ 它**不得**抢占已经排定的退避节奏，也**不得**延长恢复窗口 ——
        /// 否则"恢复期 2 秒×10 秒、期后低频退避"会被拉回高频轮询，
        /// 违反拆解计划 §1.2 第 4 条（恢复必须有界：不新增高频轮询）。</para>
        /// </summary>
        public void EnsureRecoveryScheduled(TaskbarRecoveryReason reason)
        {
            lock (_gate)
            {
                if (_state == TaskbarHostState.Closing) return;

                // 已有排队的恢复（可能正处于退避等待）⇒ 不抢占、不延长窗口。
                if (_pending) return;

                _lastReason = reason;
                _pending = true;
                _state = TaskbarHostState.Recovering;

                long now = _time.GetTimestamp();

                // 新的一段恢复期：2 秒×10 秒。
                _windowEnd = now + ToTicks(RecoveryWindow);

                _nextAttemptAt = now + ToTicks(DebounceDelay);
                _hasNextAttempt = true;
            }
        }

        /// <summary>
        /// 在 UI 消息循环里执行一次（到期的）恢复尝试。
        /// <para>返回 true 表示"本次真的执行了一次恢复动作"。</para>
        /// </summary>
        public bool Pump()
        {
            lock (_gate)
            {
                if (_state == TaskbarHostState.Closing) return false;
                if (!_pending) return false;
                if (_inFlight) return false;           // ★ 单次在途

                long now = _time.GetTimestamp();
                if (!_hasNextAttempt || now < _nextAttemptAt) return false;

                _inFlight = true;
                _pending = false;
                _attemptCount++;
                _state = TaskbarHostState.Recovering;
            }

            bool attached = false;
            try
            {
                var resolution = _bridge.ResolveTarget();

                // ★ 目标未就绪 ⇒ 保持等待，**不静默绑定主屏**（#573 / #583）。
                if (resolution.IsReady)
                {
                    if (!_bridge.IsPanelAttached(resolution))
                    {
                        _bridge.AttachToTarget(resolution);
                    }
                    attached = _bridge.IsPanelAttached(resolution);
                }
            }
            catch
            {
                // ★ 恢复失败**不得**让 UI 定时器抛出（会中断整个应用的消息循环）。
                //   这里只保证"本次尝试失败 ⇒ 按退避继续重试"，不冒充原生边界修复。
                attached = false;
            }
            finally
            {
                lock (_gate) { _inFlight = false; }
            }

            lock (_gate)
            {
                // 恢复过程中可能已经进入关闭流程 ⇒ 不再复活。
                if (_state == TaskbarHostState.Closing) return true;

                if (attached)
                {
                    _state = TaskbarHostState.Attached;
                    _backoff = BackoffBase;
                    _hasNextAttempt = false;
                    _nextAttemptAt = 0;
                }
                else
                {
                    _state = TaskbarHostState.WaitingForTaskbar;
                    ScheduleNextAttemptLocked();
                }
            }
            return true;
        }

        /// <summary>
        /// 关闭流程开始：取消排队、禁止复活。
        /// <para>★ 关闭拥有最高优先级 —— 关闭过程中不得再新建/重建任何 HWND。</para>
        /// </summary>
        public void BeginClosing()
        {
            lock (_gate)
            {
                _state = TaskbarHostState.Closing;
                _pending = false;
                _hasNextAttempt = false;
                _nextAttemptAt = 0;
                _inFlight = false;
            }
        }

        private void ScheduleNextAttemptLocked()
        {
            long now = _time.GetTimestamp();

            if (now < _windowEnd)
            {
                // 恢复期：每 2 秒一次，持续 10 秒。
                _nextAttemptAt = now + ToTicks(RecoveryInterval);
            }
            else
            {
                // 恢复期结束：低频退避等待目标重现（不永久停止）。
                _nextAttemptAt = now + ToTicks(_backoff);
                TimeSpan next = _backoff + _backoff;
                _backoff = next > BackoffMax ? BackoffMax : next;
            }

            _hasNextAttempt = true;
            _pending = true;
        }

        private long ToTicks(TimeSpan span)
        {
            return (long)(span.TotalSeconds * _time.TimestampFrequency);
        }
    }
}
