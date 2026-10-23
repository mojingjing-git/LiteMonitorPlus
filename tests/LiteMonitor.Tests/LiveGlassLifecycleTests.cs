using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.Backdrop;
using Xunit;

namespace LiteMonitor.Tests
{
    [CollectionDefinition("LiveGlass STA", DisableParallelization = true)]
    public sealed class LiveGlassStaCollection
    {
    }

    [Collection("LiveGlass STA")]
    public sealed class LiveGlassLifecycleTests
    {
        [Fact]
        public void PaintKeepsCurrentLeaseUntilAReplacementFrameIsHandedOff()
        {
            RunOnSta(() =>
            {
                var backend = new SyntheticBackdropBackend();
                LiveGlassController? controller = null;
                try
                {
                    using Form form = CreateOffscreenForm();
                    ThemeAppearanceOptions options = CreateOptions(form);
                    var service = new BackdropCaptureService(backend);
                    controller = new LiveGlassController(form, () => options, () => service);

                    controller.SetEnabled(true);
                    Assert.True(backend.WaitForCaptureStarted(1, TimeSpan.FromSeconds(5)));
                    long firstGeneration = Assert
                        .IsType<BackdropServiceRequest>(controller.CurrentRequest)
                        .SizeGeneration;
                    backend.QueueFrame(20, firstGeneration);
                    Assert.True(backend.WaitForCaptureCompleted(1, TimeSpan.FromSeconds(5)));
                    Assert.True(WaitForPendingFrame(service));

                    controller.BeginPaint();
                    BackdropFrameLease firstLease = Assert.IsType<BackdropFrameLease>(controller.CurrentFrameLease);
                    Assert.True(firstLease.IsValid);

                    controller.BeginPaint();
                    Assert.Same(firstLease, controller.CurrentFrameLease);
                    Assert.True(firstLease.IsValid);

                    long replacementGeneration = Assert
                        .IsType<BackdropServiceRequest>(controller.CurrentRequest)
                        .SizeGeneration;
                    backend.QueueFrame(40, replacementGeneration);
                    Assert.True(backend.WaitForCaptureCompleted(2, TimeSpan.FromSeconds(5)));
                    Assert.True(WaitForPendingFrame(service));
                    controller.BeginPaint();
                    BackdropFrameLease secondLease = Assert.IsType<BackdropFrameLease>(controller.CurrentFrameLease);

                    Assert.NotSame(firstLease, secondLease);
                    Assert.True(firstLease.IsDisposed);
                    Assert.Throws<ObjectDisposedException>(() => _ = firstLease.Frame);
                    Assert.True(secondLease.IsValid);
                    Assert.Equal(40, Assert.IsType<Bitmap>(secondLease.Frame).GetPixel(0, 0).R);
                }
                finally
                {
                    backend.StopProducing();
                    controller?.Dispose();
                }
            });
        }

        [Fact]
        public void LocationChangeRefreshesBoundsAndDropsAnInFlightOldFrame()
        {
            RunOnSta(() =>
            {
                var backend = new SyntheticBackdropBackend();
                LiveGlassController? controller = null;
                try
                {
                    using Form form = CreateOffscreenForm();
                    ThemeAppearanceOptions options = CreateOptions(form);
                    var service = new BackdropCaptureService(backend);
                    controller = new LiveGlassController(form, () => options, () => service);

                    controller.SetEnabled(true);
                    Assert.True(backend.WaitForCaptureStarted(1, TimeSpan.FromSeconds(5)));
                    BackdropCaptureService originalService = controller.CaptureService;
                    BackdropServiceRequest before = Assert.IsType<BackdropServiceRequest>(controller.CurrentRequest);

                    form.Location = new Point(form.Location.X + 80, form.Location.Y + 60);
                    controller.OnLocationChanged();

                    BackdropServiceRequest after = Assert.IsType<BackdropServiceRequest>(controller.CurrentRequest);
                    Assert.Same(originalService, controller.CaptureService);
                    Assert.True(after.SizeGeneration > before.SizeGeneration);
                    Assert.NotEqual(before.PhysicalClientBounds, after.PhysicalClientBounds);

                    backend.QueueFrame(20, before.SizeGeneration);
                    backend.QueueFrame(40, after.SizeGeneration);
                    Assert.True(backend.WaitForCaptureCompleted(1, TimeSpan.FromSeconds(5)));
                    Assert.True(backend.WaitForCaptureCompleted(2, TimeSpan.FromSeconds(5)));
                    Assert.True(WaitForPendingFrame(service));

                    controller.BeginPaint();
                    BackdropFrameLease lease = Assert.IsType<BackdropFrameLease>(controller.CurrentFrameLease);
                    Assert.True(lease.IsValid);
                    Assert.Equal(40, Assert.IsType<Bitmap>(lease.Frame).GetPixel(0, 0).R);
                    Assert.True(service.Metrics.FramesDroppedStale >= 1);
                    Assert.Equal(after.SizeGeneration, backend.LastRequest.SizeGeneration);
                }
                finally
                {
                    backend.StopProducing();
                    controller?.Dispose();
                }
            });
        }

        [Fact]
        public void GeometryPauseAndStopReleaseEveryHeldLease()
        {
            RunOnSta(() =>
            {
                var backend = new SyntheticBackdropBackend();
                LiveGlassController? controller = null;
                try
                {
                    using Form form = CreateOffscreenForm();
                    ThemeAppearanceOptions options = CreateOptions(form);
                    var service = new BackdropCaptureService(backend);
                    controller = new LiveGlassController(form, () => options, () => service);

                    controller.SetEnabled(true);
                    Assert.True(backend.WaitForCaptureStarted(1, TimeSpan.FromSeconds(5)));
                    long firstGeneration = Assert
                        .IsType<BackdropServiceRequest>(controller.CurrentRequest)
                        .SizeGeneration;
                    backend.QueueFrame(20, firstGeneration);
                    Assert.True(backend.WaitForCaptureCompleted(1, TimeSpan.FromSeconds(5)));
                    Assert.True(WaitForPendingFrame(service));
                    controller.BeginPaint();
                    BackdropFrameLease movedLease = Assert.IsType<BackdropFrameLease>(controller.CurrentFrameLease);

                    form.Location = new Point(form.Location.X - 70, form.Location.Y + 30);
                    controller.OnLocationChanged();
                    Assert.Null(controller.CurrentFrameLease);
                    Assert.True(movedLease.IsDisposed);

                    long replacementGeneration = Assert
                        .IsType<BackdropServiceRequest>(controller.CurrentRequest)
                        .SizeGeneration;
                    backend.QueueFrame(40, replacementGeneration);
                    Assert.True(backend.WaitForCaptureCompleted(2, TimeSpan.FromSeconds(5)));
                    Assert.True(WaitForPendingFrame(service));
                    controller.BeginPaint();
                    BackdropFrameLease suspendedLease = Assert.IsType<BackdropFrameLease>(controller.CurrentFrameLease);

                    controller.OnVisibilityChanged(false);
                    Assert.Null(controller.CurrentFrameLease);
                    Assert.True(suspendedLease.IsDisposed);
                    controller.BeginPaint();
                    Assert.Null(controller.CurrentFrameLease);

                    backend.StopProducing();
                    controller.SetEnabled(false);
                    Assert.Null(controller.CaptureService);
                    Assert.Null(controller.CurrentFrameLease);
                }
                finally
                {
                    backend.StopProducing();
                    controller?.Dispose();
                }
            });
        }

        // ================================================================
        // 【LM-072 / R08】受控重建后按配置恢复实时玻璃
        // ================================================================

        /// <summary>
        /// ① 句柄销毁（受控重建）是**临时停机**，不是用户关闭：
        /// 配置态的开关必须留着，服务才可能被恢复。
        /// <para>★ 修复前：句柄销毁走的是 <c>SetEnabled(false)</c> ⇒
        /// <c>Enabled</c> 被清成 false ⇒ 重建完成后没有任何东西会把它拉回来。</para>
        /// </summary>
        [Fact]
        public void HandleRecreateTemporaryStopKeepsTheConfiguredSwitchOn()
        {
            RunOnSta(() =>
            {
                var factory = new RecordingServiceFactory();
                LiveGlassController? controller = null;
                try
                {
                    using Form form = CreateOffscreenForm();
                    ThemeAppearanceOptions options = CreateOptions(form);
                    controller = new LiveGlassController(form, () => options, factory.Create);

                    controller.SetEnabled(true);
                    Assert.True(controller.Enabled);
                    Assert.NotNull(controller.CaptureService);
                    Assert.True(factory.Backends[0].WaitForCaptureStarted(1, TimeSpan.FromSeconds(5)));

                    string statusWhileRunning = controller.StatusText;

                    // 模拟句柄销毁（受控重建）：捕获必须停，但配置态的开关必须留着。
                    factory.Backends[0].StopProducing();
                    controller.StopForHandleRecreate();

                    // ★ LM-072：临时停机 ≠ 用户关闭 —— Enabled 必须仍然是 true。
                    Assert.True(controller.Enabled);
                    Assert.Null(controller.CaptureService);
                    Assert.False(controller.IsRunning);

                    // ★ 裁决 5：状态行必须反映**真实**状态（服务停了就要说停了）。
                    Assert.NotEqual(statusWhileRunning, controller.StatusText);
                    Assert.DoesNotContain("未启用", controller.StatusText);
                }
                finally
                {
                    factory.StopAll();
                    controller?.Dispose();
                }
            });
        }

        /// <summary>
        /// ② 配置为"开"但服务已经停了 ⇒ <c>SetEnabled(true)</c> 必须真的把服务拉回来。
        /// <para>★ 修复前：<c>SetEnabled</c> 的 <c>if (Enabled == enabled) return;</c>
        /// 早退把重启整个挡掉 ⇒ "设置页显示已开启，捕获却只能手动重开"。</para>
        /// </summary>
        [Fact]
        public void SetEnabledTrueRestartsTheServiceWhenTheConfiguredSwitchStaysOn()
        {
            RunOnSta(() =>
            {
                var factory = new RecordingServiceFactory
                {
                    // 第一次交出一个**已释放**的服务 ⇒ 启动必然失败
                    // ⇒ 落到"配置为开、服务为空"的真实状态（修复前唯一的此类入口）。
                    PoisonNextService = true,
                };
                LiveGlassController? controller = null;
                try
                {
                    using Form form = CreateOffscreenForm();
                    ThemeAppearanceOptions options = CreateOptions(form);
                    controller = new LiveGlassController(form, () => options, factory.Create);

                    controller.SetEnabled(true);

                    // 启动失败：配置仍为开，但服务已经停了。
                    Assert.True(controller.Enabled);
                    Assert.Null(controller.CaptureService);
                    Assert.False(controller.IsRunning);

                    // ★ LM-072：配置没变（仍是开）⇒ 必须按配置重启服务。
                    controller.SetEnabled(true);

                    Assert.NotNull(controller.CaptureService);
                    Assert.True(controller.IsRunning);
                    Assert.True(factory.Backends[1].WaitForCaptureStarted(1, TimeSpan.FromSeconds(5)));
                }
                finally
                {
                    factory.StopAll();
                    controller?.Dispose();
                }
            });
        }

        /// <summary>
        /// ③ 回归护栏：用户**显式关闭** ⇒ <c>Enabled == false</c>，且"按配置恢复"不得把它拉起来。
        /// </summary>
        [Fact]
        public void ExplicitUserCloseStaysOffAndIsNeverRestoredAutomatically()
        {
            RunOnSta(() =>
            {
                var factory = new RecordingServiceFactory();
                LiveGlassController? controller = null;
                try
                {
                    using Form form = CreateOffscreenForm();
                    ThemeAppearanceOptions options = CreateOptions(form);
                    controller = new LiveGlassController(form, () => options, factory.Create);

                    controller.SetEnabled(true);
                    Assert.True(controller.Enabled);
                    Assert.True(factory.Backends[0].WaitForCaptureStarted(1, TimeSpan.FromSeconds(5)));

                    factory.Backends[0].StopProducing();
                    controller.SetEnabled(false);

                    Assert.False(controller.Enabled);
                    Assert.Null(controller.CaptureService);
                    Assert.False(controller.IsRunning);
                    Assert.Contains("未启用", controller.StatusText);

                    // ★ 按配置恢复：配置已经是"关" ⇒ 绝不允许把服务拉起来。
                    controller.RestoreIfEnabled();

                    Assert.False(controller.Enabled);
                    Assert.Null(controller.CaptureService);
                    Assert.Single(factory.Backends);
                }
                finally
                {
                    factory.StopAll();
                    controller?.Dispose();
                }
            });
        }

        /// <summary>
        /// ④ "按配置恢复"的入口存在且行为正确：配置为开且服务已停 ⇒ 恢复后服务重新起来。
        /// </summary>
        [Fact]
        public void RestoreIfEnabledBringsTheServiceBackAfterATemporaryStop()
        {
            RunOnSta(() =>
            {
                var factory = new RecordingServiceFactory();
                LiveGlassController? controller = null;
                try
                {
                    using Form form = CreateOffscreenForm();
                    ThemeAppearanceOptions options = CreateOptions(form);
                    controller = new LiveGlassController(form, () => options, factory.Create);

                    controller.SetEnabled(true);
                    BackdropCaptureService first = controller.CaptureService;
                    Assert.NotNull(first);
                    Assert.True(factory.Backends[0].WaitForCaptureStarted(1, TimeSpan.FromSeconds(5)));

                    factory.Backends[0].StopProducing();
                    controller.StopForHandleRecreate();
                    Assert.Null(controller.CaptureService);
                    Assert.True(controller.Enabled);

                    string statusWhilePaused = controller.StatusText;

                    // ★ 按配置恢复。
                    controller.RestoreIfEnabled();

                    BackdropCaptureService second = controller.CaptureService;
                    Assert.NotNull(second);
                    Assert.NotSame(first, second);
                    Assert.True(controller.IsRunning);
                    Assert.True(factory.Backends[1].WaitForCaptureStarted(1, TimeSpan.FromSeconds(5)));

                    // 恢复之后状态行必须回到"服务在跑"，不再是"已暂停"。
                    Assert.NotEqual(statusWhilePaused, controller.StatusText);
                }
                finally
                {
                    factory.StopAll();
                    controller?.Dispose();
                }
            });
        }

        /// <summary>
        /// 【LM-095】受控重建**失败**（句柄已失效）⇒ 状态行**不得**继续承诺
        /// "重建完成后自动恢复" —— 那句承诺永远不会兑现（重建已经失败了）。
        /// <para>★ 后果（修复前）：玻璃静默失效、设置页复选框仍显示"开"，
        /// 而状态行骗用户说"会自动恢复" ⇒ 用户一直等不到恢复。</para>
        /// </summary>
        [Fact]
        public void 重建失败且句柄已失效_状态行不得再承诺自动恢复()
        {
            RunOnSta(() =>
            {
                var factory = new RecordingServiceFactory();
                LiveGlassController? controller = null;
                try
                {
                    using Form form = CreateOffscreenForm();
                    ThemeAppearanceOptions options = CreateOptions(form);
                    controller = new LiveGlassController(form, () => options, factory.Create);

                    controller.SetEnabled(true);
                    Assert.True(controller.Enabled);
                    Assert.NotNull(controller.CaptureService);

                    // 句柄销毁（受控重建）⇒ 临时停机
                    factory.Backends[0].StopProducing();
                    controller.StopForHandleRecreate();
                    Assert.Null(controller.CaptureService);
                    Assert.Contains("重建完成后自动恢复", controller.StatusText);

                    // ★ 重建失败（句柄已失效）⇒ 必须如实收尾。
                    controller.HandleRecreateFailed(handleAlive: false);

                    // 不得再承诺"自动恢复"。
                    Assert.DoesNotContain("自动恢复", controller.StatusText);
                    // 必须说清楚"重建失败"。
                    Assert.Contains("重建失败", controller.StatusText);
                    // 句柄已失效 ⇒ 不得对着无效句柄抓屏 ⇒ 服务保持停止。
                    Assert.Null(controller.CaptureService);
                    Assert.False(controller.IsRunning);
                    // 配置态开关不变（用户没关它）。
                    Assert.True(controller.Enabled);
                }
                finally
                {
                    factory.StopAll();
                    controller?.Dispose();
                }
            });
        }

        /// <summary>
        /// 【LM-095】受控重建**失败**但句柄仍然有效 ⇒ 临时停机的语义依然成立
        /// ⇒ 必须按配置把实时玻璃拉回来（而不是让用户手动重开）。
        /// </summary>
        [Fact]
        public void 重建失败但句柄仍在_必须按配置把实时玻璃拉回来()
        {
            RunOnSta(() =>
            {
                var factory = new RecordingServiceFactory();
                LiveGlassController? controller = null;
                try
                {
                    using Form form = CreateOffscreenForm();
                    ThemeAppearanceOptions options = CreateOptions(form);
                    controller = new LiveGlassController(form, () => options, factory.Create);

                    controller.SetEnabled(true);
                    BackdropCaptureService first = controller.CaptureService;
                    Assert.NotNull(first);

                    factory.Backends[0].StopProducing();
                    controller.StopForHandleRecreate();
                    Assert.Null(controller.CaptureService);

                    // ★ 重建失败，但句柄还在（重建请求被拒 / 旧句柄未被销毁）⇒ 可以恢复。
                    controller.HandleRecreateFailed(handleAlive: true);

                    BackdropCaptureService second = controller.CaptureService;
                    Assert.NotNull(second);
                    Assert.NotSame(first, second);
                    Assert.True(controller.IsRunning);
                    Assert.True(factory.Backends[1].WaitForCaptureStarted(1, TimeSpan.FromSeconds(5)));
                    // 恢复之后状态行不再说"已暂停"，也不说"重建失败"。
                    Assert.DoesNotContain("重建失败", controller.StatusText);
                    Assert.DoesNotContain("自动恢复", controller.StatusText);
                }
                finally
                {
                    factory.StopAll();
                    controller?.Dispose();
                }
            });
        }

        /// <summary>
        /// 【LM-095】用户本就关着实时玻璃时，重建失败的收尾**不得**把它拉起来，
        /// 也不得把状态改成"重建失败"（用户没在用它）。
        /// </summary>
        [Fact]
        public void 重建失败时用户已关闭的实时玻璃不得被拉起()
        {
            RunOnSta(() =>
            {
                var factory = new RecordingServiceFactory();
                LiveGlassController? controller = null;
                try
                {
                    using Form form = CreateOffscreenForm();
                    ThemeAppearanceOptions options = CreateOptions(form);
                    controller = new LiveGlassController(form, () => options, factory.Create);

                    controller.SetEnabled(true);
                    controller.SetEnabled(false);
                    Assert.False(controller.Enabled);

                    controller.HandleRecreateFailed(handleAlive: true);

                    Assert.False(controller.Enabled);
                    Assert.Null(controller.CaptureService);
                    Assert.Contains("未启用", controller.StatusText);
                    Assert.Single(factory.Backends);
                }
                finally
                {
                    factory.StopAll();
                    controller?.Dispose();
                }
            });
        }

        [Fact]
        public void RequestIdentityRejectsBoundsOrClientGeometryCounterChanges()
        {
            var original = new BackdropServiceRequest(
                new IntPtr(42),
                new Rectangle(10, 20, 320, 180),
                1f,
                1f,
                7,
                11,
                13,
                8,
                targetFps: 6,
                captureScale: 0.25);

            var movedWithoutBumpedGeneration = new BackdropServiceRequest(
                new IntPtr(42),
                new Rectangle(90, 80, 320, 180),
                1f,
                1f,
                7,
                11,
                13,
                8,
                targetFps: 6,
                captureScale: 0.25);

            var movedWithBumpedGeneration = new BackdropServiceRequest(
                new IntPtr(42),
                new Rectangle(90, 80, 320, 180),
                1f,
                1f,
                7,
                11,
                14,
                8,
                targetFps: 6,
                captureScale: 0.25);

            Assert.False(original.MatchesGeneration(movedWithoutBumpedGeneration));
            Assert.False(original.MatchesGeneration(movedWithBumpedGeneration));
            Assert.False(movedWithoutBumpedGeneration.MatchesGeneration(movedWithBumpedGeneration));
        }

        private static Form CreateOffscreenForm()
        {
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-30000, -28000),
                ClientSize = new Size(96, 64)
            };
            _ = form.Handle;
            return form;
        }

        private static ThemeAppearanceOptions CreateOptions(Form form)
        {
            using var runtime = new AppearanceRuntime();
            var theme = new Theme { Name = "LiveGlassLifecycle", Version = 4 };
            theme.Backdrop = new BackdropConfig
            {
                Mode = ThemeAppearanceOptions.BackdropModeGlass,
                Opacity = 0,
                Blur = 8,
                Fps = 6
            };
            return runtime.Update(theme, 1f, 1f, form.ClientRectangle);
        }

        private static bool WaitForPendingFrame(BackdropCaptureService service)
        {
            return SpinWait.SpinUntil(
                () => service.PendingFrameCount > 0,
                TimeSpan.FromSeconds(5));
        }

        private static void RunOnSta(Action action)
        {
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            })
            {
                IsBackground = true,
                Name = "LiveGlassLifecycleTest"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA live-glass test timed out.");
            if (error is not null)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }
        }

        /// <summary>
        /// 【LM-072 / R08】可注入的捕获服务工厂。
        /// <para>★ 每次调用都返回**新的** <see cref="BackdropCaptureService"/>（旧的已被
        /// <c>StopService</c> 释放过），并把对应的合成后端记录下来供断言使用。</para>
        /// </summary>
        private sealed class RecordingServiceFactory
        {
            private readonly List<SyntheticBackdropBackend> _backends = new();

            /// <summary>已创建出来的合成后端（按创建顺序）。</summary>
            public IReadOnlyList<SyntheticBackdropBackend> Backends => _backends;

            /// <summary>
            /// 下一次创建时交出一个**已释放**的服务 ⇒ <c>Start()</c> 必然抛
            /// <see cref="ObjectDisposedException"/> ⇒ 落到"配置为开、服务为空"的真实状态。
            /// </summary>
            public bool PoisonNextService { get; set; }

            public BackdropCaptureService Create()
            {
                var backend = new SyntheticBackdropBackend();
                _backends.Add(backend);

                var service = new BackdropCaptureService(backend);
                if (PoisonNextService)
                {
                    PoisonNextService = false;
                    service.Dispose();
                }

                return service;
            }

            /// <summary>让所有合成后端立刻停止产出（避免停服务时等满超时）。</summary>
            public void StopAll()
            {
                foreach (SyntheticBackdropBackend backend in _backends)
                {
                    backend.StopProducing();
                }
            }
        }

        private sealed class SyntheticBackdropBackend : IBackdropCaptureBackend
        {
            private readonly object _sync = new();
            private readonly Queue<SyntheticFrame> _frames = new();
            private int _captureStartedCount;
            private int _captureCompletedCount;
            private bool _stopped;
            private BackdropServiceRequest? _lastRequest;

            public bool IsReady => true;

            public string UnavailableReason => string.Empty;

            public BackdropServiceRequest LastRequest
            {
                get
                {
                    lock (_sync)
                    {
                        return _lastRequest
                            ?? throw new InvalidOperationException("No capture request has been observed.");
                    }
                }
            }

            public bool TryCapture(
                BackdropServiceRequest request,
                out Image? frame,
                out BackdropServiceTiming timing)
            {
                int red;
                lock (_sync)
                {
                    _captureStartedCount++;
                    _lastRequest = request;
                    Monitor.PulseAll(_sync);

                    while (!_stopped && _frames.Count == 0)
                    {
                        if (!Monitor.Wait(_sync, TimeSpan.FromSeconds(5)))
                        {
                            frame = null;
                            timing = BackdropServiceTiming.Empty;
                            return false;
                        }
                    }

                    if (_stopped)
                    {
                        frame = null;
                        timing = BackdropServiceTiming.Empty;
                        return false;
                    }

                    SyntheticFrame next = _frames.Peek();
                    if (next.SizeGeneration != request.SizeGeneration)
                    {
                        frame = null;
                        timing = BackdropServiceTiming.Empty;
                        return false;
                    }

                    _frames.Dequeue();
                    red = next.Red;
                }

                var bitmap = new Bitmap(8, 6, PixelFormat.Format32bppArgb);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.FromArgb(255, red, 200, 30));
                }

                frame = bitmap;
                timing = new BackdropServiceTiming(0, 1, 1);
                lock (_sync)
                {
                    _captureCompletedCount++;
                    Monitor.PulseAll(_sync);
                }
                return true;
            }

            public void RestoreDisplayAffinity()
            {
            }

            public bool WaitForCaptureStarted(int captureNumber, TimeSpan timeout)
            {
                return WaitForCount(started: true, captureNumber, timeout);
            }

            public bool WaitForCaptureCompleted(int captureNumber, TimeSpan timeout)
            {
                return WaitForCount(started: false, captureNumber, timeout);
            }

            public void QueueFrame(int red, long sizeGeneration)
            {
                lock (_sync)
                {
                    if (_stopped)
                    {
                        throw new InvalidOperationException("The synthetic backend has stopped.");
                    }

                    _frames.Enqueue(new SyntheticFrame(red, sizeGeneration));
                    Monitor.PulseAll(_sync);
                }
            }

            public void StopProducing()
            {
                lock (_sync)
                {
                    _stopped = true;
                    _frames.Clear();
                    Monitor.PulseAll(_sync);
                }
            }

            private bool WaitForCount(bool started, int captureNumber, TimeSpan timeout)
            {
                DateTime deadline = DateTime.UtcNow + timeout;
                lock (_sync)
                {
                    while ((started ? _captureStartedCount : _captureCompletedCount) < captureNumber)
                    {
                        TimeSpan remaining = deadline - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero)
                        {
                            return false;
                        }

                        Monitor.Wait(_sync, remaining);
                    }
                }

                return true;
            }

            private sealed class SyntheticFrame
            {
                public SyntheticFrame(int red, long sizeGeneration)
                {
                    Red = red;
                    SizeGeneration = sizeGeneration;
                }

                public int Red { get; }

                public long SizeGeneration { get; }
            }
        }
    }
}
