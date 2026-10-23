using LiteMonitor.Rendering;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using System.Drawing;
using System.Windows.Forms;

namespace LiteMonitor
{
    /// <summary>
    /// 【GPU T03 / T02 合同 §2、§4、§7.4、§8】GPU ↔ legacy 运行时切换协调器。
    /// <para><b>它是"状态与判据"的唯一落点</b>：期望路由、有效路由、切换阶段、身份、
    /// HWND 呈现历史、清理状态、诊断字段全部由本类推进（合同 §2 的 8 个维度）。
    /// Win32 / WinForms 副作用由 <see cref="UIController"/> 与 <see cref="MainForm"/> 执行，
    /// 本类只负责"允许什么、禁止什么、失败归到哪个码"。</para>
    /// <para><b>纯 CLR</b>（不引用 DXGI、不直接碰 HWND）⇒ 全部规则可被确定性单测覆盖，
    /// 不依赖真实桌面 / GPU。</para>
    /// <para>★ <b>句柄代次真相源</b>：本类**不持有**任何 <see cref="WindowHandleGeneration"/> 实例，
    /// 只**只读消费**注入进来的那一个（合同 §7.2 的唯一真相源 / 主审裁决 D-G）。
    /// 生产注入点是 <c>LiteMonitor.Rendering.Configuration.WindowHandleGeneration.Instance</c>
    /// （进程内唯一实例，由类型自身持有）。</para>
    /// </summary>
    internal sealed class RenderingTransitionCoordinator
    {
        // ================================================================
        // 合同常量（原因码取自合同 §5.1 / §5.2 的码值表）
        // ================================================================

        /// <summary>【合同 §8.1 规则 ④】受控重建判别位的**超时兜底**：请求后 5 秒内未被消费即归零。</summary>
        internal static readonly TimeSpan ControlledRecreateTimeout = TimeSpan.FromSeconds(5);

        /// <summary>【合同 §4 步 1】<c>Decide</c> 抛异常。</summary>
        internal const string DecideFailedCode = "gpu_transition_decide_failed";

        /// <summary>【合同 §4 步 2】窗口状态快照失败。</summary>
        internal const string WindowStateSnapshotFailedCode = "gpu_transition_window_state_snapshot_failed";

        /// <summary>【合同 §4 步 4】受控 HWND 重建失败。</summary>
        internal const string HandleRecreateFailedCode = "gpu_presentation_handle_recreate_failed";

        /// <summary>【合同 §8.1 规则 ④】判别位超时未被消费（清理告警，**不是**呈现根因）。</summary>
        internal const string HandleRecreateNotObservedCode = "gpu_presentation_handle_recreate_not_observed";

        /// <summary>【合同 §8.1 规则 ③】判别位残留被清理（清理告警，**不是**呈现根因）。</summary>
        internal const string HandleRecreateStaleFlagCode = "gpu_presentation_handle_recreate_stale_flag";

        /// <summary>【合同 §4 步 5】句柄代次推进失败。</summary>
        internal const string GenerationAdvanceFailedCode = "gpu_transition_generation_advance_failed";

        /// <summary>【合同 §4 步 6】大小 / DPI / 布局恢复失败。</summary>
        internal const string LayoutRestoreFailedCode = "gpu_transition_layout_restore_failed";

        /// <summary>【合同 §4 步 7】透明度 / 穿透 / 置顶 / 可见性恢复失败。</summary>
        internal const string WindowStateRestoreFailedCode = "gpu_transition_window_state_restore_failed";

        /// <summary>【合同 §4 步 8】legacy 首帧超时（**仅可见窗口**）。</summary>
        internal const string LegacyFirstFrameTimeoutCode = "gpu_transition_legacy_first_frame_timeout";

        /// <summary>【合同 §5.1】GPU 恢复预算耗尽（既有码，来自宿主）。</summary>
        internal const string RecoveryExhaustedCode = "gpu_presentation_recovery_exhausted";

        // ================================================================
        // 字段
        // ================================================================

        /// <summary>句柄代次的**唯一真相源**（注入；本类只读消费，绝不自建 —— 合同 §7.2）。</summary>
        private readonly WindowHandleGeneration _handleGeneration;

        /// <summary>时钟（注入；生产用 <see cref="TimeProvider.System"/>，单测用假时钟 ⇒ 确定性）。</summary>
        private readonly TimeProvider _timeProvider;

        private RenderingTransitionState _state;
        private long _transitionId;
        private long _hostGeneration;
        private long _lastAppliedRequestVersion;

        /// <summary>上一次的路由判定结果（幂等短路用，合同 U04）。</summary>
        private RenderingRouteDecision? _lastDecision;

        /// <summary>判别位计数（合同 §8.1）。</summary>
        private int _pendingControlledRecreateCount;

        /// <summary>判别位的请求号（合同 §8.1，配对与诊断用）。</summary>
        private long _pendingControlledRecreateRequestId;

        /// <summary>判别位置位的时刻（超时兜底用，合同 §8.1 规则 ④）。</summary>
        private DateTimeOffset _pendingControlledRecreateRequestedAt;

        /// <summary>判别位请求号自增源。</summary>
        private long _nextControlledRecreateRequestId;

        /// <summary>最近一次被消费的判别位请求号（诊断用）。</summary>
        private long _lastConsumedControlledRecreateRequestId;

        /// <summary>GPU 恢复预算已耗尽（合同 §8.4 条件 3 / U11）：普通 Tick 不得再自动拉起 GPU。</summary>
        private bool _gpuRecoveryExhausted;

        /// <summary>非致命诊断清单（合同 §5.3 规则 1 / §9.5）。</summary>
        private readonly List<string> _nonFatalDiagnostics = new();

        /// <summary>窗体已进入关闭过程（合同 §4 全局约束：关闭拥有最高优先级 / U10）。</summary>
        private bool _closing;

        /// <summary>
        /// 构造协调器。
        /// </summary>
        /// <param name="handleGeneration">句柄代次唯一真相源（**注入**，本类不自建）。</param>
        /// <param name="timeProvider">时钟；<c>null</c> ⇒ <see cref="TimeProvider.System"/>。</param>
        internal RenderingTransitionCoordinator(
            WindowHandleGeneration handleGeneration,
            TimeProvider? timeProvider = null)
        {
            _handleGeneration = handleGeneration ?? throw new ArgumentNullException(nameof(handleGeneration));
            _timeProvider = timeProvider ?? TimeProvider.System;

            _state = new RenderingTransitionState(
                TransitionId: 0,
                HandleGeneration: handleGeneration.Current,
                HostGeneration: 0,
                LastAppliedRequestVersion: 0,
                ExpectedRoute: RenderingRoute.Legacy,
                EffectiveRoute: RenderingRoute.Legacy,
                Phase: RenderingTransitionPhase.Stable,
                BindingState: PresentationBindingState.Unbound,
                CleanupState: CapturePolicyCleanupState.None,
                Trigger: RenderingTransitionTrigger.UserThemeChange,
                FirstFrameOutcome: FirstFrameOutcome.NotAttempted,
                PendingFirstFrame: false,
                FailureCode: null,
                CleanupErrorCode: null,
                CleanupWarningCode: null);
        }

        // ================================================================
        // 只读视图
        // ================================================================

        /// <summary>当前状态快照（合同 §2 的 8 维度）。</summary>
        internal RenderingTransitionState State => _state;

        /// <summary>当前切换阶段。</summary>
        internal RenderingTransitionPhase Phase => _state.Phase;

        /// <summary>期望路由（用户选择对应的路由；合同 §2 维度 2）。</summary>
        internal RenderingRoute ExpectedRoute => _state.ExpectedRoute;

        /// <summary>有效路由（当前实际可呈现的后端；合同 §2 维度 3）。</summary>
        internal RenderingRoute EffectiveRoute => _state.EffectiveRoute;

        /// <summary>HWND 呈现历史（合同 §2 维度 6）。</summary>
        internal PresentationBindingState BindingState => _state.BindingState;

        /// <summary>清理状态（合同 §2 维度 7）。</summary>
        internal CapturePolicyCleanupState CleanupState => _state.CleanupState;

        /// <summary>当前句柄代次（只读消费唯一真相源）。</summary>
        internal long HandleGeneration => _handleGeneration.Current;

        /// <summary>宿主会话代次（每次新建宿主递增；合同 §2 维度 5 的 <c>HostGeneration</c>）。</summary>
        internal long HostGeneration => _hostGeneration;

        /// <summary>当前切换请求号（合同 §2 维度 5 的 <c>TransitionId</c>）。</summary>
        internal long TransitionId => _state.TransitionId;

        /// <summary>窗体是否已进入关闭过程（关闭优先，合同 §4 全局约束 / U10）。</summary>
        internal bool IsClosing => _closing;

        /// <summary>
        /// 【合同 §8.4】是否允许拉起 GPU 呈现。
        /// <para>三个条件缺一不可：① 判别位命中由调用方另行判定；
        /// ② <c>Phase == Stable</c>；③ <c>ExpectedRoute.IsGpu</c>；
        /// ④ 无未终结的 <c>gpu_presentation_recovery_exhausted</c> 记录。</para>
        /// <para>★ 这一判定**取代**既有的"普通 Tick 自动拉起 GPU"口径（合同 §4.4 :188 / U11），
        /// 但**不改变** <c>src/UI/UIController.cs:951</c> 的期望路由口径（合同 D8）。</para>
        /// </summary>
        internal bool CanStartGpuPresentation =>
            _state.Phase == RenderingTransitionPhase.Stable &&
            IsGpuRoute(_state.ExpectedRoute) &&
            !_gpuRecoveryExhausted;

        // ================================================================
        // 合同 §8：HandleCreated 判别位
        // ================================================================

        /// <summary>
        /// 【合同 §8.1】未消费的受控重建请求数。
        /// <para>用 <b>计数</b>而不是 <c>bool</c>：<c>bool</c> 无法表达"已置位但重建未发生"的残留
        /// （合同 §8.1 明文）。</para>
        /// </summary>
        internal int PendingControlledRecreateCount => _pendingControlledRecreateCount;

        /// <summary>【合同 §8.1】当前未消费的受控重建请求号（<c>0</c> = 无待消费请求）。</summary>
        internal long PendingControlledRecreateRequestId => _pendingControlledRecreateRequestId;

        /// <summary>【合同 §8.1】最近一次被消费的受控重建请求号（诊断用；<c>0</c> = 从未消费过）。</summary>
        internal long LastConsumedControlledRecreateRequestId => _lastConsumedControlledRecreateRequestId;

        /// <summary>
        /// 【合同 §8.1 置位者】请求一次受控 HWND 重建。
        /// <para>必须由 <c>MainForm</c> 的内部受控重建薄入口在调用 <c>RecreateHandle()</c>
        /// **之前**调用。</para>
        /// <para>★ 规则 ③ <b>陈旧兜底</b>：本次置位前若发现残留计数 &gt; 0 ⇒ 先归零，
        /// 并记 <c>gpu_presentation_handle_recreate_stale_flag</c>（清理告警，**不清空**呈现根因）。</para>
        /// <para>★ 关闭优先（合同 §4 全局约束）：关闭过程中**不得**重建 HWND ⇒ 直接返回 0。</para>
        /// </summary>
        /// <returns>本次请求号；<c>0</c> = 未置位（关闭中）。</returns>
        internal long RequestControlledRecreate(string reason)
        {
            if (_closing)
            {
                // 关闭拥有最高优先级：关闭过程中不得重建 HWND（合同 §4 全局约束 / U10）。
                return 0;
            }

            // ★ 规则 ③ 陈旧兜底：残留计数必须先归零并留痕，再置位新请求。
            if (_pendingControlledRecreateCount > 0)
            {
                _pendingControlledRecreateCount = 0;
                _pendingControlledRecreateRequestId = 0;
                SetCleanupWarning(HandleRecreateStaleFlagCode);
            }

            _pendingControlledRecreateRequestId = ++_nextControlledRecreateRequestId;
            _pendingControlledRecreateCount = 1;
            _pendingControlledRecreateRequestedAt = _timeProvider.GetUtcNow();
            return _pendingControlledRecreateRequestId;
        }

        /// <summary>
        /// 【合同 §8.1 消费者 / §8.2】尝试消费一次受控重建判别位。
        /// <para>返回 <c>true</c> ⇒ 这次 <c>HandleCreated</c> 来自**受控重建**（走 GPU 重建路径）；
        /// 返回 <c>false</c> ⇒ 来自**构造期首次创建**或**非受控创建**
        /// ⇒ <b>禁止</b>拉起 GPU（计划 §4.2 :147，既有设计必须保持）。</para>
        /// </summary>
        internal bool TryConsumeControlledRecreate(out long requestId)
        {
            if (_pendingControlledRecreateCount <= 0)
            {
                requestId = 0;
                return false;
            }

            // 规则 ①：正常消费 ⇒ 计数 --。
            _pendingControlledRecreateCount--;
            requestId = _pendingControlledRecreateRequestId;
            _lastConsumedControlledRecreateRequestId = requestId;
            if (_pendingControlledRecreateCount <= 0)
            {
                _pendingControlledRecreateRequestId = 0;
            }

            return true;
        }

        /// <summary>
        /// 【合同 §8.1 规则 ②】取消一次受控重建请求（<c>RecreateHandle()</c> 抛异常时立即调用）。
        /// <para>归零语义：<b>计数与请求号一并归零</b>，不留残留。</para>
        /// </summary>
        /// <returns><c>true</c> = 确实取消了一次待消费请求。</returns>
        internal bool CancelControlledRecreate(long requestId)
        {
            if (_pendingControlledRecreateCount <= 0)
            {
                return false;
            }

            if (requestId != 0 && requestId != _pendingControlledRecreateRequestId)
            {
                // 取消的是另一次请求 ⇒ 不得动当前待消费的请求（避免误清）。
                return false;
            }

            _pendingControlledRecreateCount = 0;
            _pendingControlledRecreateRequestId = 0;
            return true;
        }

        /// <summary>
        /// 【合同 §8.1 规则 ④】超时兜底：请求后 5 秒内未被消费 ⇒ 归零并记
        /// <c>gpu_presentation_handle_recreate_not_observed</c>。
        /// <para>由 UI Tick 驱动（不另开定时器）；判定只读时钟，**不做**任何 Win32 调用。</para>
        /// </summary>
        /// <returns><c>true</c> = 本次调用执行了超时归零。</returns>
        internal bool EnforceControlledRecreateTimeout()
        {
            if (_pendingControlledRecreateCount <= 0)
            {
                return false;
            }

            if (_timeProvider.GetUtcNow() - _pendingControlledRecreateRequestedAt < ControlledRecreateTimeout)
            {
                return false;
            }

            _pendingControlledRecreateCount = 0;
            _pendingControlledRecreateRequestId = 0;
            SetCleanupWarning(HandleRecreateNotObservedCode);
            return true;
        }

        // ================================================================
        // 合同 §4：GPU → legacy 的 9 步
        // ================================================================

        /// <summary>
        /// 【合同 §4 步 1】计算新请求 + 进入保护。
        /// <para>调用唯一纯函数 <see cref="RenderingRoutePolicy.Decide"/>（不另建决策函数）。
        /// 失败（抛异常）⇒ 记 <c>gpu_transition_decide_failed</c>、**保持原状态**、不进入 <c>Stopping</c>。</para>
        /// <para>★ 幂等短路（合同 U04）：同一 legacy 请求重复到达且已处于稳定 legacy ⇒
        /// 不再次进入切换、**不重建**、返回相同稳定状态。</para>
        /// </summary>
        /// <returns><c>true</c> = 已进入一次新的切换（<c>Phase = Stopping</c>）。</returns>
        internal bool TryBeginTransition(
            RenderingRouteRequest request,
            RenderingTransitionTrigger trigger,
            out RenderingRouteDecision? decision)
        {
            decision = null;

            if (_closing)
            {
                // 关闭优先：关闭过程中不得再启动一次切换（合同 §4 全局约束 / U10）。
                return false;
            }

            RenderingRouteDecision computed;
            try
            {
                computed = RenderingRoutePolicy.Decide(request);
            }
            catch (Exception)
            {
                // 合同 §4 步 1 失败语义：保持原状态，不进入 Stopping。
                SetFailure(DecideFailedCode);
                return false;
            }

            if (IsIdempotentLegacyRepeat(computed))
            {
                // U04：同一请求重复到达 ⇒ 不再次重建，返回相同稳定状态。
                decision = computed;
                return false;
            }

            _transitionId++;
            _lastAppliedRequestVersion++;
            _lastDecision = computed;
            _state = _state with
            {
                TransitionId = _transitionId,
                LastAppliedRequestVersion = _lastAppliedRequestVersion,
                ExpectedRoute = computed.Route,
                Phase = RenderingTransitionPhase.Stopping,
                Trigger = trigger,
            };

            decision = computed;
            return true;
        }

        /// <summary>
        /// 【合同 §4 步 2】记录窗口状态。
        /// <para>快照失败 ⇒ 记 <c>gpu_transition_window_state_snapshot_failed</c>、<c>Phase = Failed</c>、
        /// <b>不重建</b>（合同 §4 步 2 失败语义）。</para>
        /// </summary>
        internal bool TryCaptureWindowState(RenderingWindowSnapshot? snapshot)
        {
            if (snapshot is null)
            {
                SetFailure(WindowStateSnapshotFailedCode);
                return false;
            }

            _windowSnapshot = snapshot;
            return true;
        }

        /// <summary>【合同 §4 步 2】最近一次记录的窗口状态快照（<c>null</c> = 尚未记录）。</summary>
        internal RenderingWindowSnapshot? WindowSnapshot => _windowSnapshot;

        /// <summary>
        /// 【合同 §4 步 3】记录清理责任的移交结果。
        /// <para>★ 合同 §5.3 规则 2：<see cref="RenderingTransitionState.CleanupErrorCode"/> 与
        /// <see cref="RenderingTransitionState.FailureCode"/> **互不覆盖** ——
        /// 写清理诊断**不得**清空呈现根因。</para>
        /// </summary>
        internal void RecordCleanupState(CapturePolicyCleanupState state, string? cleanupErrorCode)
        {
            _state = _state with { CleanupState = state };
            if (!string.IsNullOrEmpty(cleanupErrorCode))
            {
                SetCleanupError(cleanupErrorCode);
            }
        }

        /// <summary>
        /// 【合同 §4 步 4】进入受控重建阶段。
        /// <para>★ 步 4 的禁止动作（计划 §4.3 :169）：不得在 <c>OnPaint</c> /
        /// <c>WndProc</c> 的销毁回调 / GPU 帧绘制栈内递归执行本步。</para>
        /// </summary>
        internal void EnterRecreatingHandle()
        {
            _state = _state with { Phase = RenderingTransitionPhase.RecreatingHandle };
        }

        /// <summary>
        /// 【合同 §4 步 4 失败语义】重建失败 ⇒ 记 <c>gpu_presentation_handle_recreate_failed</c>、
        /// <c>Phase = Failed</c>，并**保留可控重试入口**（不得自动无限重建）。
        /// </summary>
        internal void FailHandleRecreate()
        {
            SetFailure(HandleRecreateFailedCode);
        }

        /// <summary>
        /// 【合同 §4 步 5】原生生命周期 + 代次推进。
        /// <para>★ 步 5 的禁止动作（计划 §4.3 :172）：**构造期首次创建不得拉起 GPU**
        /// （计划 §4.2 :147，既有设计必须保持）；过渡期间 GPU 启动次数必须为零。</para>
        /// <param name="controlled"><c>true</c> = 判别位命中（受控重建）；<c>false</c> = 构造期首次创建 / 非受控创建。</param>
        /// <param name="newHandleGeneration">推进后的句柄代次。</param>
        internal void OnHandleCreated(bool controlled, long newHandleGeneration)
        {
            _state = _state with { HandleGeneration = newHandleGeneration };

            if (!controlled)
            {
                // 构造期首次创建 / 非受控创建 ⇒ **不进入** GPU 重建路径，也不推进到 Restoring。
                return;
            }

            _state = _state with { Phase = RenderingTransitionPhase.Restoring };
        }

        /// <summary>
        /// 【合同 §4 步 5 失败语义】代次推进失败 ⇒ 记 <c>gpu_transition_generation_advance_failed</c>、
        /// <c>Phase = Failed</c>。
        /// </summary>
        internal void FailGenerationAdvance()
        {
            SetFailure(GenerationAdvanceFailedCode);
        }

        /// <summary>
        /// 【合同 §4 步 6】恢复大小 / DPI + 重算布局的结果。
        /// <para>失败 ⇒ 记 <c>gpu_transition_layout_restore_failed</c>、<c>Phase = Failed</c>，
        /// 快照保留供重试。</para>
        /// </summary>
        internal void RecordLayoutRestore(bool success)
        {
            if (!success)
            {
                SetFailure(LayoutRestoreFailedCode);
            }
        }

        /// <summary>
        /// 【合同 §4 步 7】恢复透明度 / 穿透 / 置顶 / 可见性的结果。
        /// <para>失败 ⇒ 记 <c>gpu_transition_window_state_restore_failed</c>、<c>Phase = Failed</c>。</para>
        /// </summary>
        internal void RecordWindowStateRestore(bool success)
        {
            if (!success)
            {
                SetFailure(WindowStateRestoreFailedCode);
            }
        }

        /// <summary>
        /// 【合同 §4 步 8 / U13】记录 legacy 首帧结果。
        /// <para>隐藏 / 最小化 ⇒ <c>PendingFirstFrame = true</c> + <c>FirstFrameOutcome = NotAttempted</c>
        /// （"已切换、等待显示"）；可见窗口拿到首帧证据 ⇒ <c>Succeeded</c>。</para>
        /// </summary>
        internal void RecordFirstFrame(bool windowVisible, bool legacyFrameObserved)
        {
            if (!windowVisible)
            {
                _state = _state with
                {
                    PendingFirstFrame = true,
                    FirstFrameOutcome = FirstFrameOutcome.NotAttempted,
                };
                return;
            }

            if (legacyFrameObserved)
            {
                _state = _state with
                {
                    PendingFirstFrame = false,
                    FirstFrameOutcome = FirstFrameOutcome.Succeeded,
                };
                return;
            }

            _state = _state with
            {
                PendingFirstFrame = false,
                FirstFrameOutcome = FirstFrameOutcome.Failed,
            };
        }

        /// <summary>
        /// 【合同 §4 步 8 失败语义】可见窗口的 legacy 首帧超时 ⇒
        /// 记 <c>gpu_transition_legacy_first_frame_timeout</c>、<c>Phase = Failed</c>。
        /// </summary>
        internal void FailLegacyFirstFrame()
        {
            SetFailure(LegacyFirstFrameTimeoutCode);
        }

        /// <summary>
        /// 【合同 §4 步 9】发布完成状态。
        /// <para>★ 禁止在未拿到 legacy 首帧证据时显示"已成功恢复"（计划 §4.3 :177-179）：
        /// 隐藏窗口 ⇒ <c>PendingFirstFrame = true</c> + 有效路由 = legacy。</para>
        /// </summary>
        internal void PublishStable(RenderingRoute effectiveRoute)
        {
            _state = _state with
            {
                Phase = RenderingTransitionPhase.Stable,
                EffectiveRoute = effectiveRoute,
            };
        }

        /// <summary>【合同 §4 步 9 / U11】故障回退：有效路由落到 legacy，但**期望路由保留用户选择**。</summary>
        internal void PublishLegacyFallback()
        {
            PublishStable(RenderingRoute.Legacy);
        }

        // ================================================================
        // 其它合同条款
        // ================================================================

        /// <summary>记录 HWND 呈现历史（合同 §2 维度 6；单调推进，不回退）。</summary>
        internal void RecordBindingState(PresentationBindingState bindingState)
        {
            _state = _state with { BindingState = bindingState };
        }

        /// <summary>
        /// 【合同 §4 步 4 / 计划 §4.1 :123-126】受控 HWND 重建的触发依据。
        /// <para>★ **不得**用 <c>newHwnd != oldHwnd</c> 作为唯一重建断言（计划 §4.1 :129）——
        /// 这里依据的是**本代次的呈现历史**（单调推进的绑定状态）。</para>
        /// <para>★ "用户手动关闭 GPU"与"GPU 故障回退"两条路径<b>共用</b>本判据
        /// ⇒ 两条路径的重建决策天然对称（计划 §4.4 :185"按 HWND 历史决定是否重建"）。</para>
        /// </summary>
        internal bool RequiresControlledRecreate() =>
            _state.BindingState switch
            {
                // 该代次成功完成过 flip 呈现 ⇒ 退出到 legacy **必须**重建。
                PresentationBindingState.Presented => true,
                // 首次呈现调用期间发生异常、是否已改变 HWND 状态无法确定 ⇒ 保守重建。
                PresentationBindingState.PresentAttempted => true,
                // 已绑定（交换链已创建）⇒ 保守重建。
                PresentationBindingState.Bound => true,
                // 未创建交换链、未尝试呈现 ⇒ 证据支持时可省略重建。
                _ => false,
            };

        /// <summary>记录宿主会话代次（每次新建宿主递增；合同 §2 维度 5）。</summary>
        internal long NextHostGeneration()
        {
            _hostGeneration++;
            _state = _state with { HostGeneration = _hostGeneration };
            return _hostGeneration;
        }

        /// <summary>
        /// 【合同 §7.4 / U05】三重丢弃判据：<c>TransitionId</c> / <c>HandleGeneration</c> /
        /// <c>HostGeneration</c>，缺一不可。旧切换请求、旧 HWND 代次、旧宿主会话的回调一律丢弃。
        /// </summary>
        internal bool ShouldDiscardCallback(long transitionId, long handleGeneration, long hostGeneration) =>
            transitionId != _state.TransitionId ||
            handleGeneration != _state.HandleGeneration ||
            hostGeneration != _state.HostGeneration;

        /// <summary>
        /// 记录一条**非致命诊断**（合同 §5.3 规则 1 / §9.5）。
        /// <para>★ 非致命诊断**不得**进入主题校验的错误列表，也**不得**把
        /// <see cref="RenderingTransitionState.FailureCode"/> 里已有的**故障根因**覆盖掉
        /// （合同 §5.3 规则 2 的精神：根因优先）。</para>
        /// <para>★ 也**不改变**切换阶段：非致命诊断不是一次切换失败。</para>
        /// <para>全部非致命诊断另外收在 <see cref="NonFatalDiagnostics"/> 里（供设置页 / 诊断面板消费，
        /// 属 owner B 的协调器自有通道，**不**改动 owner A 的
        /// <see cref="RenderingTransitionState"/> 结构）。</para>
        /// </summary>
        internal void RecordNonFatalDiagnostic(string code)
        {
            if (string.IsNullOrEmpty(code)) return;

            _nonFatalDiagnostics.Add(code);

            // 只有在**没有**既有根因时才占用 FailureCode 通道。
            if (_state.FailureCode is null)
            {
                _state = _state with { FailureCode = code };
            }
        }

        /// <summary>全部非致命诊断（按发生顺序）。</summary>
        internal IReadOnlyList<string> NonFatalDiagnostics => _nonFatalDiagnostics;

        /// <summary>
        /// 【R02 / 合同 §4.4 / U11 / U01】故障回退的"步 1"：进入一次 GPU → legacy 切换。
        /// <para>★ 与"用户手动关闭 GPU"路径<b>共用</b>同一套后续步骤（步 2–9），
        /// 区别只在于本方法<b>不</b>调用 <see cref="RenderingRoutePolicy.Decide"/>：
        /// 故障回退**不得**把用户的 GPU 选择悄悄改写（合同 U11"不把用户选择悄悄写回为关闭"）
        /// ⇒ <see cref="RenderingTransitionState.ExpectedRoute"/> 保持不变，
        /// 有效路由在步 9 落到 <c>Legacy</c>。</para>
        /// <para>★ <b>保留失败原因</b>：呈现根因写入 <see cref="RenderingTransitionState.FailureCode"/>，
        /// 不会被"普通 fallback code"顶替（合同 §5.3 规则 2）。</para>
        /// </summary>
        /// <returns><c>true</c> = 已进入一次新的切换（<c>Phase = Stopping</c>）。</returns>
        internal bool TryBeginFailureFallback(
            string? failureCode,
            RenderingTransitionTrigger trigger)
        {
            if (_closing)
            {
                // 关闭优先：关闭过程中不得再启动一次切换（合同 §4 全局约束 / U10）。
                return false;
            }

            _transitionId++;
            _lastAppliedRequestVersion++;
            _state = _state with
            {
                TransitionId = _transitionId,
                LastAppliedRequestVersion = _lastAppliedRequestVersion,
                Phase = RenderingTransitionPhase.Stopping,
                Trigger = trigger,
                // ★ 保留失败原因（合同 U11 / §5.3 规则 2）。
                FailureCode = string.IsNullOrEmpty(failureCode)
                    ? _state.FailureCode
                    : failureCode,
            };

            return true;
        }

        /// <summary>
        /// 【R02 / 合同 §5.3 规则 2 / U11】记录呈现根因。
        /// <para>★ <b>不覆盖</b>已记录的根因（先到先得 ⇒ 最早的根因不被后来的诊断顶替）；
        /// <b>不改变</b>切换阶段（故障上报本身不是一次切换失败）。</para>
        /// </summary>
        internal void RecordPresentationRootCause(string? code)
        {
            if (string.IsNullOrEmpty(code)) return;
            if (_state.FailureCode is not null) return;
            _state = _state with { FailureCode = code };
        }

        /// <summary>
        /// 【合同 §4.4 / §8.4 条件 3 / U11】GPU 恢复预算已耗尽
        /// （<c>gpu_presentation_recovery_exhausted</c>）⇒ 普通 Tick 不得再自动拉起 GPU，
        /// 只能由用户显式重试。
        /// </summary>
        internal void MarkGpuRecoveryExhausted()
        {
            _gpuRecoveryExhausted = true;
            // ★【R02 / 合同 §5.3 规则 2 / U11】恢复预算耗尽**不得**顶替已记录的呈现根因 ——
            //   根因（例如 <c>gpu_presentation_render_failed</c>）与"预算已耗尽"是**两件事**，
            //   两者必须同时可见。
            if (_state.FailureCode is null)
            {
                _state = _state with { FailureCode = RecoveryExhaustedCode };
            }

            // ★ 预算耗尽本身也留痕（诊断通道），便于同时显示"为什么失败"与"为什么不再自动重试"。
            if (!_nonFatalDiagnostics.Contains(RecoveryExhaustedCode))
            {
                _nonFatalDiagnostics.Add(RecoveryExhaustedCode);
            }
        }

        /// <summary>清除"恢复预算耗尽"标记（用户显式重试时；合同 §6.4"用户显式重试"）。</summary>
        internal void ClearGpuRecoveryExhausted()
        {
            _gpuRecoveryExhausted = false;
        }

        /// <summary>恢复预算是否已耗尽。</summary>
        internal bool GpuRecoveryExhausted => _gpuRecoveryExhausted;

        /// <summary>
        /// 【合同 §4 全局约束 / U10】窗体进入关闭过程。
        /// <para>关闭拥有最高优先级：关闭过程中不得重建 HWND、不得恢复 affinity、不得重新创建宿主。</para>
        /// </summary>
        internal void NotifyFormClosing()
        {
            _closing = true;
        }

        /// <summary>
        /// 【合同 §4 全局约束】当前是否**允许**做窗口操作（HWND 重建 / 恢复外观 / 创建宿主）。
        /// <para>关闭过程中一律返回 <c>false</c>。</para>
        /// </summary>
        internal bool AllowsWindowOperations => !_closing;

        // ================================================================
        // 内部
        // ================================================================

        private RenderingWindowSnapshot? _windowSnapshot;

        /// <summary>
        /// U04 幂等判定：已处于稳定 legacy、上一次判定也是 legacy，且两次判定的路由结果**完全一致**。
        /// </summary>
        private bool IsIdempotentLegacyRepeat(RenderingRouteDecision computed)
        {
            if (_state.Phase != RenderingTransitionPhase.Stable)
            {
                return false;
            }

            if (computed.IsGpu || _state.EffectiveRoute != RenderingRoute.Legacy)
            {
                return false;
            }

            RenderingRouteDecision? previous = _lastDecision;
            if (previous is null)
            {
                return false;
            }

            return previous.Route == computed.Route &&
                   previous.EffectiveMaterial == computed.EffectiveMaterial &&
                   previous.EffectiveBackend == computed.EffectiveBackend &&
                   previous.CaptureRequested == computed.CaptureRequested;
        }

        /// <summary>写呈现根因（<c>FailureCode</c>）—— **不触碰**清理诊断字段（合同 §5.3 规则 2）。</summary>
        private void SetFailure(string? code)
        {
            _state = _state with
            {
                FailureCode = code,
                Phase = code is null ? _state.Phase : RenderingTransitionPhase.Failed,
            };
        }

        /// <summary>写清理根因（<c>CleanupErrorCode</c>）—— **不清空**呈现根因（合同 §5.3 规则 2）。</summary>
        private void SetCleanupError(string? code)
        {
            _state = _state with { CleanupErrorCode = code };
        }

        /// <summary>写清理告警（<c>CleanupWarningCode</c>）—— **不清空**呈现根因（合同 §5.3 规则 2）。</summary>
        private void SetCleanupWarning(string? code)
        {
            _state = _state with { CleanupWarningCode = code };
        }

        /// <summary>某个路由是否属于 GPU 路线（<c>Legacy</c> 之外的都是）。</summary>
        private static bool IsGpuRoute(RenderingRoute route) => route != RenderingRoute.Legacy;
    }

    /// <summary>
    /// 【合同 §4 步 2】窗口状态快照。
    /// <para>★ 步 6 的禁止动作（计划 §4.3 :174）：**不得**把旧显示器的已缩放像素布局直接当新布局使用
    /// ⇒ 快照必须带 <see cref="DeviceDpi"/> 与 <see cref="Bounds"/>，恢复时按**当前**屏幕重新求几何。</para>
    /// </summary>
    internal sealed record RenderingWindowSnapshot(
        Rectangle Bounds,
        Size ClientSize,
        int DeviceDpi,
        bool Visible,
        FormWindowState WindowState,
        bool TopMost,
        bool ClickThrough,
        double Opacity);

    /// <summary>
    /// 【GPU T03 / 合同 §4.6 / GPU T05】legacy 回退样式的**派生**桥。
    /// <para>★ 三条硬约束（合同 §4.6 / §9.6）：
    /// ① 样式随主题走（读主题自身的 <c>rendering.fallbackStyle</c>），**不**建"主题文件名 → 样式"映射表；
    /// ② **不修改**主题对象、**不修改** <c>ThemeManager.Current</c>、**不**写盘 ——
    ///    回退值是一份**独立副本**；
    /// ③ 非法值产生诊断并退回默认值，诊断**不**进主题校验的错误列表（合同 §5.3 规则 1）。</para>
    /// <para>本类是 owner B 侧的**纯函数**（不碰 WinForms / Win32）⇒ 可被确定性单测覆盖。</para>
    /// </summary>
    internal static class LegacyFallbackStyleBridge
    {
        /// <summary>
        /// 解析 legacy 回退样式（缺省 / 非法值都退回中性可读默认值）。
        /// </summary>
        /// <param name="configuration">主题的 <c>rendering</c> 配置（只读，不被修改）。</param>
        /// <param name="diagnostics">非致命诊断（当前只含 <c>gpu_fallback_style_invalid</c>）。</param>
        internal static LegacyFallbackStyle Resolve(
            ThemeRenderingConfig? configuration,
            out IReadOnlyList<string> diagnostics)
        {
            LegacyFallbackStyle style = LegacyFallbackStyleResolver.Resolve(configuration);
            diagnostics = style.Diagnostics;
            return style;
        }

        /// <summary>
        /// 用回退样式生成一份**派生**主题副本（源主题**不被修改**）。
        /// <para>★ 复制策略：逐个 public 可写属性做**浅拷贝**，只把 <c>Color</c> 换成派生副本 ——
        /// 其余子配置按引用共享（与既有运行时副本口径一致：Layout 已经是缩放后的同一份）。</para>
        /// <para>★ <c>Accent</c> 落点（**主审裁决 D-H**，见计划 §4.5.11）⇒
        /// <see cref="ColorConfig"/> 没有 accent 色槽，裁决落点是
        /// <see cref="ColorConfig.TextTitle"/>（标题 / 强调前景色）。
        /// <para>★ **不得**占用 <c>ValueSafe/ValueWarn/ValueCrit</c>（告警语义色）与
        /// <c>BarLow/BarMid/BarHigh</c>（进度条分段色）—— 合同 §4.6 明文要求
        /// "保留告警语义和进度值"。</para>
        /// <para>★ 也不占用 <c>TextGroup</c>：分组标题色与 <c>MutedText</c> 同源会塌陷层级。
        /// 正文可读性对比度仍由 <c>TextPrimary</c> 承担（合同 §9.5 的对比度口径针对正文与次要文字）。</para>
        /// </summary>
        internal static Theme BuildDerivedTheme(Theme source, LegacyFallbackStyle style)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(style);

            ColorConfig colors = DeriveConfig(source.Color);
            colors.Background = style.Background;
            colors.GroupBackground = style.PanelBackground;
            colors.Border = style.Border;
            colors.TextPrimary = style.Text;
            colors.TextSecondary = style.MutedText;

            // ★ 裁决 D-H：accent 落到标题前景色。
            colors.TextTitle = style.Accent;

            Theme derived = DeriveConfig(source);
            derived.Color = colors;
            return derived;
        }

        /// <summary>
        /// 复制一个配置对象（逐个 public 可写属性）得到**派生副本**；源对象**不被修改**。
        /// </summary>
        internal static T DeriveConfig<T>(T source) where T : new()
        {
            ArgumentNullException.ThrowIfNull(source);

            T copy = new();
            foreach (System.Reflection.PropertyInfo property in typeof(T).GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!property.CanRead) continue;
                if (property.SetMethod == null || !property.SetMethod.IsPublic) continue;
                property.SetValue(copy, property.GetValue(source));
            }

            return copy;
        }
    }
}
