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
