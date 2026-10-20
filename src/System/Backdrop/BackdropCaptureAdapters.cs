using System;
using System.Drawing;

namespace LiteMonitor.src.SystemServices.Backdrop
{
    // ================================================================
    // A15 适配器：把「捕获引擎」接到「捕获服务」上（合同 §13.2 第 3 条）
    //
    // ★ 架构边界（合同 §13）：
    // 1. BackdropCaptureService 是**唯一**的后台循环与帧来源。
    // 2. ScreenCaptureFrameSource 降级为**捕获引擎**：只提供"单次抓屏"原语
    //    （TryCaptureFrame），自身**不驱动循环**。
    // 3. 两者由本文件的两个适配器连接 ⇒ 全进程**只有一个**后台捕获循环。
    //    （两个循环同时抓屏会重复抓屏，直接击穿合同 §6.3 的后台 CPU 预算。）
    //
    // ★ 所有权链（单帧）：
    //    引擎创建位图 → 所有权转移给适配器 → 再转移给服务 → 服务转给 UI 租约。
    //    全链**只转移、不复制、不重复释放**；任一失败点都在本文件内释放"已创建
    //    但未交付"的位图（合同 §4.2 第 6 条）。
    // ================================================================

    /// <summary>
    /// 捕获后端适配器：把 <see cref="BackdropCaptureService"/> 的服务侧请求
    /// 映射成捕获引擎的请求，并调用引擎的**单次抓屏**原语。
    ///
    /// ★ 与服务侧的接口约定：
    /// 1. <see cref="TryCapture"/> 成功时返回的位图，其所有权**转移给调用方**（服务）——
    ///    本适配器**不复制**、**不释放**它（引擎已经把所有权交出来了）。
    /// 2. 失败时返回 false、位图为 null，并把引擎给出的**可读中文原因**带出来
    ///    （合同 §7「降级不静默」）。
    /// 3. 本适配器**不驱动任何循环**：抓屏的时机由服务决定。
    /// </summary>
    public sealed class ScreenCaptureBackendAdapter : IBackdropCaptureBackend
    {
        private readonly ScreenCaptureFrameSource _engine;

        /// <summary>保护"最近一次请求 / 最近一次失败原因"这两个诊断字段。</summary>
        private readonly object _sync = new();

        private BackdropCaptureRequest? _lastEngineRequest;
        private string _lastError = string.Empty;

        /// <summary>
        /// 创建捕获后端适配器。
        /// </summary>
        /// <param name="engine">捕获引擎（<see cref="ScreenCaptureFrameSource"/>）。
        /// <para>★ **不要**调用引擎的 <c>Start()</c>：那会启动引擎自带的捕获循环，
        ///   与服务侧的循环重复抓屏（合同 §13.2 第 2 条）。本适配器只调用单次抓屏入口。</para>
        /// </param>
        public ScreenCaptureBackendAdapter(ScreenCaptureFrameSource engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        /// <summary>
        /// 本适配器提供的永远是**实时捕获帧**，不是预览固定图（合同 §2.2 第 1 条）。
        /// </summary>
        public bool IsPreview => false;

        /// <summary>
        /// 引擎是否可作为**单次抓屏原语**使用。
        /// <para>
        /// ★ **不直接转发** <c>engine.IsReady</c>：引擎的那个属性绑定的是
        ///   **它自己的后台循环**（state == Running）。按合同 §13.2 第 3 条，
        ///   M2 路径下引擎的循环**不启动** ⇒ 直接转发会永远是 false，
        ///   服务将永远判定"后端未就绪"、一帧都抓不到。
        /// </para>
        /// <para>
        /// ★ 这里判定的是三件事：
        ///   ① 引擎自带的循环**没有**在跑（跑着就说明接线错了 ⇒ 两个循环 ⇒ 拒绝）；
        ///   ② 引擎没有处于原语级故障（请求无效 / 捕获失败 / 句柄失效）；
        ///   ③ 已提交的请求自身有效（几何、句柄、帧率、降采样比例）。
        /// </para>
        /// </summary>
        public bool IsReady
        {
            get
            {
                switch (_engine.State)
                {
                    // ★ 引擎自带循环在跑 ⇒ 会与服务的循环重复抓屏（击穿 §6.3 的 CPU 预算）
                    case BackdropCaptureState.Running:
                    case BackdropCaptureState.InvalidRequest:
                    case BackdropCaptureState.CaptureFailed:
                    case BackdropCaptureState.HandleInvalid:
                        return false;
                }

                lock (_sync)
                {
                    // ★ 引擎的请求是 readonly struct ⇒ 用 { } 模式解包（Nullable<T> 不能直接取成员）
                    if (_lastEngineRequest is { } req && !req.IsValid) return false;
                }

                return true;
            }
        }

        /// <summary>
        /// 不可用原因（人类可读）。<see cref="IsReady"/> 为 false 时必定非空，
        /// 该文本会进入降级诊断（合同 §7「降级不静默」）。
        /// </summary>
        public string UnavailableReason
        {
            get
            {
                switch (_engine.State)
                {
                    case BackdropCaptureState.Running:
                        return "捕获引擎自带的捕获循环仍在运行：会出现两个后台循环同时抓屏"
                             + "（重复抓屏，击穿合同 §6.3 的后台 CPU 预算）。"
                             + "M2 路径下只应使用单次抓屏入口。";
                    case BackdropCaptureState.InvalidRequest:
                        return "捕获引擎的请求无效：" + _engine.UnavailableReason;
                    case BackdropCaptureState.CaptureFailed:
                        return "捕获引擎上次捕获失败：" + _engine.UnavailableReason;
                    case BackdropCaptureState.HandleInvalid:
                        return "窗口句柄已失效：" + _engine.UnavailableReason;
                }

                lock (_sync)
                {
                    if (_lastEngineRequest is { } req && !req.IsValid)
                    {
                        return "捕获请求无效：" + req.InvalidReason;
                    }

                    if (!string.IsNullOrEmpty(_lastError)) return _lastError;
                }

                string reason = _engine.UnavailableReason;
                return string.IsNullOrEmpty(reason) ? "捕获引擎不可用。" : reason;
            }
        }

        /// <summary>最近一次抓屏失败的原因（可读中文；成功后清空）。诊断用。</summary>
        public string LastError
        {
            get { lock (_sync) return _lastError; }
        }

        /// <summary>最近一次映射出来的引擎请求（诊断用；未抓过屏时为 null）。</summary>
        public BackdropCaptureRequest? LastEngineRequest
        {
            get { lock (_sync) return _lastEngineRequest; }
        }

        /// <summary>
        /// 抓取一帧：把服务侧请求映射成引擎请求，调用引擎的**单次抓屏**原语。
        /// <para>
        /// ★ 所有权：成功时 <paramref name="frame"/> 是**调用方（服务）的新所有权** ——
        ///   引擎把所有权交给本适配器，本适配器原样转交，**不复制、不释放**。
        /// ★ 失败时返回 false、位图为 null；引擎在失败路径上不遗留位图，
        ///   万一交出了位图，本适配器也会释放它（合同 §4.2 第 6 条）。
        /// ★ 引擎的单次抓屏**不做滤波** —— 滤波由 <see cref="BackdropBlurProcessorAdapter"/> 负责。
        /// </para>
        /// </summary>
        /// <param name="request">服务侧的不可变捕获请求。</param>
        /// <param name="frame">抓到的位图（所有权转移给调用方）。</param>
        /// <param name="timing">分段耗时（经过时间口径）。</param>
        public bool TryCapture(BackdropServiceRequest request, out Image? frame, out BackdropServiceTiming timing)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));

            frame = null;
            timing = BackdropServiceTiming.Empty;

            BackdropCaptureRequest engineRequest = MapRequest(request);

            // ★ 把映射后的请求推给引擎：引擎的 RestoreDisplayAffinity() 依赖它**内部**保存的
            //   HWND（引擎从不启动自己的循环 ⇒ 内部请求只能由这里喂）。
            //   少了这一步，取消/失败时的亲和性恢复会找不到窗口（窗口在录屏里永久消失）。
            PushToEngine(engineRequest);

            Image? captured = null;
            BackdropCaptureTiming engineTiming = default;
            string? error = null;
            bool ok;

            try
            {
                ok = _engine.TryCaptureFrame(in engineRequest, out captured, out engineTiming, out error);
            }
            catch (Exception ex)
            {
                // 引擎抛异常：它内部的 finally 已还原显示亲和性；这里只保证不漏位图
                captured?.Dispose();
                captured = null;
                ok = false;
                error = "抓屏异常：" + ex.Message;
            }

            timing = MapTiming(in engineTiming);

            if (!ok || captured is null)
            {
                // ★ 失败路径：释放"已创建但未交付"的位图（合同 §4.2 第 6 条）
                captured?.Dispose();

                string reason = string.IsNullOrEmpty(error)
                    ? "抓屏失败：捕获引擎没有给出可用帧。"
                    : error!;

                lock (_sync) { _lastError = reason; }
                return false;
            }

            // ★ 所有权转移：引擎 → 本适配器 → 服务（不复制、不释放）
            frame = captured;

            lock (_sync) { _lastError = string.Empty; }
            return true;
        }

        /// <summary>
        /// 恢复窗口的显示亲和性（把 <c>WDA_EXCLUDEFROMCAPTURE</c> 排除属性还原）。
        /// <para>
        /// ★ 合同 §5.2 第 3 条：取消 / 失败后**必须**调用，否则窗口会在录屏里永久消失。
        /// ★ 转发给引擎的同名方法（它对窗口失效是静默的，且**幂等**）。
        /// </para>
        /// </summary>
        public void RestoreDisplayAffinity()
        {
            try
            {
                _engine.RestoreDisplayAffinity();
            }
            catch
            {
                // 恢复失败无法再做什么；不抛异常，避免打断停止流程（合同 §5.2 第 3 条）
            }
        }

        /// <summary>
        /// 提前把几何 / 句柄 / 代次推给引擎（可选）。
        /// <para>
        /// ★ 正常情况下不必调用：<see cref="TryCapture"/> 每次都会推一次。
        ///   这里暴露出来是为了让"窗口句柄重建 / 尺寸变化"这类时机**不必等下一次抓屏**
        ///   就能让引擎知道新的 HWND（亲和性恢复要用）。
        /// </para>
        /// </summary>
        /// <param name="request">服务侧的不可变捕获请求。</param>
        public void UpdateRequest(BackdropServiceRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            PushToEngine(MapRequest(request));
        }

        // ------------------------------------------------------------
        // 内部实现
        // ------------------------------------------------------------

        /// <summary>
        /// 服务侧请求 → 引擎侧请求的字段映射（一个字段都不丢）。
        /// <para>
        /// ★ 代次是唯一的窄化点：服务侧是 64 位，引擎侧是 32 位。
        ///   这里做**饱和**投影（超界就停在 int.MaxValue / int.MinValue），
        ///   **绝不回绕** —— 回绕会让旧代次重新等于当前代次，从而放过过期帧。
        ///   权威的代次校验仍在服务侧用完整 64 位做（合同 §3.1 / §4.2 第 4 条），
        ///   引擎侧这份只是粗粒度投影（引擎在 M2 路径下不再发布帧，其内部代次账本已停用）。
        /// </para>
        /// </summary>
        private static BackdropCaptureRequest MapRequest(BackdropServiceRequest request)
        {
            return new BackdropCaptureRequest(
                request.WindowHandle,          // → WindowHandle
                request.PhysicalClientBounds,  // → ScreenBounds（屏幕物理矩形，允许负坐标）
                request.DeviceDpi,             // → DeviceDpi
                request.UserScale,             // → UserScale
                request.BlurPx,                // → BlurPx（物理像素，不再乘缩放）
                request.CaptureScale,          // → CaptureScale（降采样比例）
                request.TargetFps,             // → Fps
                request.ThemeGeneration,       // → ThemeGeneration
                SaturateToInt(request.HandleGeneration), // → HandleGeneration（饱和，不回绕）
                SaturateToInt(request.SizeGeneration));  // → SizeGeneration（饱和，不回绕）
        }

        /// <summary>64 位代次 → 32 位的**饱和**投影（不回绕，避免代次碰撞）。</summary>
        private static int SaturateToInt(long value)
        {
            if (value > int.MaxValue) return int.MaxValue;
            if (value < int.MinValue) return int.MinValue;
            return (int)value;
        }

        /// <summary>
        /// 引擎侧耗时 → 服务侧耗时。
        /// <para>
        /// ★ 口径：全部是**经过时间**（墙钟），与 CPU 口径严格分开（合同 §6.1）。
        /// ★ <c>BackendFilterMs</c> 恒为 0：引擎的单次抓屏**不做滤波**，
        ///   滤波段由服务在调用处理器适配器时自行测量。
        /// </para>
        /// </summary>
        private static BackdropServiceTiming MapTiming(in BackdropCaptureTiming engineTiming)
        {
            return new BackdropServiceTiming(
                excludeWindowMs: engineTiming.ExcludeWindowMs,
                backendCaptureMs: engineTiming.CaptureMs,
                backendFilterMs: 0d);
        }

        /// <summary>把映射后的请求喂给引擎（引擎内部保存它，供亲和性恢复使用）。</summary>
        private void PushToEngine(BackdropCaptureRequest engineRequest)
        {
            lock (_sync) { _lastEngineRequest = engineRequest; }

            try
            {
                // ★ 只更新请求、不启动循环：引擎的 UpdateRequest 没有任何线程副作用
                _engine.UpdateRequest(engineRequest);
            }
            catch (ObjectDisposedException)
            {
                // 引擎已释放：后续抓屏会失败，这里不抛异常打断服务
            }
        }
    }

    // ================================================================
    // 图像处理适配器
    // ================================================================

    /// <summary>
    /// 图像处理适配器：把服务侧请求翻译成**物理模糊合同**，交给
    /// <see cref="BackdropImageProcessor"/> 完成"降采样 + 物理模糊半径滤波"。
    ///
    /// ★ 所有权合同（与 <see cref="IBackdropFrameProcessor"/> 一致）：
    /// 1. 输入 <c>source</c> 是**借用**：本适配器**绝不释放**它（所有权始终在服务手里）。
    /// 2. 输出 <c>processed</c> 是**调用方的新所有权**（引擎的处理器总是返回**新位图**，
    ///    连 σ = 0 的等尺寸转换也是副本 ⇒ 不存在"输出与输入同一实例"的情形）。
    /// 3. 失败时返回 false、输出为 null，且**不释放输入**（由服务按失败路径释放，
    ///    合同 §4.2 第 6 条）。
    /// </summary>
    public sealed class BackdropBlurProcessorAdapter : IBackdropFrameProcessor
    {
        private readonly bool _resampleWithoutBlur;
        private readonly object _sync = new();

        private BackdropProcessMetrics _lastMetrics;
        private string _lastFailureReason = string.Empty;

        /// <summary>
        /// 创建图像处理适配器。
        /// </summary>
        /// <param name="resampleWithoutBlur">
        /// σ = 0（模糊半径为 0）时是否仍然降采样/升采样。
        /// <para>默认 false：σ = 0 ⇒ 既不滤波也不重采样（严格满足"Blur = 0 不做滤波"）。</para>
        /// </param>
        public BackdropBlurProcessorAdapter(bool resampleWithoutBlur = false)
        {
            _resampleWithoutBlur = resampleWithoutBlur;
        }

        /// <summary>最近一次处理的分段计量（诊断用）。</summary>
        public BackdropProcessMetrics LastMetrics
        {
            get { lock (_sync) return _lastMetrics; }
        }

        /// <summary>最近一次失败的原因（可读中文；成功后清空）。诊断用。</summary>
        public string LastFailureReason
        {
            get { lock (_sync) return _lastFailureReason; }
        }

        /// <summary>
        /// 处理一帧：降采样 + 物理模糊半径滤波。
        /// <para>
        /// ★ 模糊合同：<c>BackdropBlurContract.Create(request.BlurPx, request.CaptureScale)</c>
        ///   —— σ 直接用服务请求里的**物理像素**模糊半径（不再乘缩放），
        ///   降采样比例用它把 σ 换算到降采样缓冲空间。
        /// ★ 失败：返回 false、<c>processed</c> 为 null，且**不释放**输入。
        /// </para>
        /// </summary>
        /// <param name="request">服务侧的不可变捕获请求。</param>
        /// <param name="source">输入帧（**借用**，本方法绝不释放它）。</param>
        /// <param name="processed">输出帧（所有权转移给调用方）。</param>
        public bool TryProcess(BackdropServiceRequest request, Image source, out Image? processed)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (source is null) throw new ArgumentNullException(nameof(source));

            processed = null;

            BackdropBlurContract contract = BackdropBlurContract.Create(
                request.BlurPx,
                request.CaptureScale,
                _resampleWithoutBlur);

            Bitmap? result;
            BackdropProcessMetrics metrics;
            try
            {
                result = BackdropImageProcessor.Process(source, in contract, out metrics);
            }
            catch (Exception ex)
            {
                // ★ 失败不释放输入：所有权仍在服务手里（合同 §4.2 第 6 条）
                lock (_sync)
                {
                    _lastMetrics = default;
                    _lastFailureReason = "图像处理异常：" + ex.Message;
                }
                return false;
            }

            lock (_sync) { _lastMetrics = metrics; }

            if (result is null)
            {
                string reason = string.IsNullOrEmpty(metrics.FailureReason)
                    ? "图像处理失败：没有得到可用的结果。"
                    : metrics.FailureReason!;

                lock (_sync) { _lastFailureReason = reason; }
                return false;
            }

            // ★ 输出是**新位图** ⇒ 所有权转移给调用方（服务）；输入由服务按"输出≠输入"释放
            processed = result;

            lock (_sync) { _lastFailureReason = string.Empty; }
            return true;
        }
    }
}
