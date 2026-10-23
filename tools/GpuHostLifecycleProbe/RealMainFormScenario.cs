using System.Net.NetworkInformation;
using System.Reflection;
using LiteMonitor;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.WebServer;

namespace GpuHostLifecycleProbe;

/// <summary>
/// The real-MainForm arm of the T01 experiment.
///
/// Instantiating the production MainForm has side effects (tray icon,
/// background sampler, web-server object). The brief requires that arm to be
/// abandoned as soon as the isolation cannot be proven, so this arm runs
/// behind explicit gates:
///
///   gate 1 - the tray icon must not be visible;
///   gate 2 - the web server must not be listening;
///   gate 3 - no file may be written outside the probe's own base directory.
///
/// If a gate fails, the arm stops, records the concrete side effect and the
/// scenario is reported as NOT_VERIFIED instead of being used as evidence.
/// </summary>
internal static class RealMainFormScenario
{
    private static readonly List<string> _overlayErrors = [];

    private static readonly MethodInfo? RecreateHandleMethod = typeof(Form).GetMethod(
        "RecreateHandle",
        BindingFlags.Instance | BindingFlags.NonPublic);

    public static int Run(string output, int timeoutMs, CandidateHashGuard? guard)
    {
        string[] filesBefore = ListBaseDirectoryFiles();

        MainForm form;
        try
        {
            form = new MainForm();
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(output, "mainform-construction-error.log"), exception.ToString());
            return 1;
        }

        using (form)
        {
            List<object> observations = [];
            bool? trayVisible = ObserveTrayIcon(form);
            bool webServerRunning = LiteWebServer.Instance?.IsRunning ?? false;
            int webServerPort = LiteWebServer.Instance?.CurrentRunningPort ?? -1;
            bool samplingActive = HardwareMonitor.Instance is not null;

            observations.Add(new
            {
                step = "side-effect-observation",
                trayIconVisible = trayVisible,
                webServerRunning,
                webServerPort,
                hardwareSamplerCreated = samplingActive,
                baseDirectory = AppContext.BaseDirectory,
                foregroundWindow = DesktopObservation.GetForegroundWindow().ToInt64()
            });

            bool gate1 = trayVisible == false;
            bool gate2 = !webServerRunning;

            if (!gate1 || !gate2)
            {
                File.WriteAllText(
                    Path.Combine(output, "mainform-abort.json"),
                    System.Text.Json.JsonSerializer.Serialize(
                        new
                        {
                            task = "GPU-T01-recreate-handle",
                            scenario = "mainform",
                            verdict = "NOT_VERIFIED",
                            reason = "The isolation gates failed, so the real MainForm arm was abandoned.",
                            observations,
                            gate1_trayIconHidden = gate1,
                            gate2_webServerNotListening = gate2
                        },
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return 4;
            }

            GdiRecoveryExperiment experiment = new(
                form,
                () => RecreateViaReflection(form),
                output,
                "mainform",
                guard);

            experiment.ExtraObservations.Add(new
            {
                step = "side-effect-observation",
                trayIconVisible = trayVisible,
                webServerRunning,
                webServerPort,
                hardwareSamplerCreated = samplingActive,
                baseDirectory = AppContext.BaseDirectory,
                foregroundWindow = DesktopObservation.GetForegroundWindow().ToInt64(),
                isolationGates = new { gate1_trayIconHidden = gate1, gate2_webServerNotListening = gate2 }
            });

            form.Paint += (sender, e) =>
            {
                experiment.NotePaint();
                FramePainter.Draw(e.Graphics, experiment.CurrentFrameColor, experiment.CurrentFrameIndex);
            };

            // MainForm.OnPaint calls base.OnPaint (which raises the Paint event)
            // and only then _ui.Render, so the production legacy render covers
            // whatever the Paint event drew. The dynamic frame is therefore
            // repainted straight onto the window device context after the
            // host has finished painting.
            experiment.AfterPaintOverlay = () =>
            {
                try
                {
                    using Graphics graphics = Graphics.FromHwnd(form.Handle);
                    FramePainter.Draw(graphics, experiment.CurrentFrameColor, experiment.CurrentFrameIndex);
                }
                catch (Exception exception)
                {
                    _overlayErrors.Add(exception.Message);
                }
            };

            int exitCode = 1;
            form.Shown += async (sender, e) =>
            {
                try
                {
                    // The production MainForm fades in from Opacity = 0; the
                    // measurement must not start before the fade has settled.
                    await WaitForFadeAsync(form, TimeSpan.FromSeconds(4)).ConfigureAwait(true);
                    using CancellationTokenSource cancellation = new(timeoutMs);
                    await experiment.RunAsync(cancellation.Token);
                    exitCode = experiment.WriteEvidence("mainform", 0, null);
                }
                catch (OperationCanceledException)
                {
                    exitCode = experiment.WriteEvidence("mainform", 3, "The run exceeded its own deadline.");
                }
                catch (Exception exception)
                {
                    exitCode = experiment.WriteEvidence("mainform", 1, exception.ToString());
                    File.WriteAllText(Path.Combine(output, "failure.log"), exception.ToString());
                }
                finally
                {
                    experiment.ExtraObservations.Add(new
                    {
                        step = "overlay-errors",
                        errors = _overlayErrors.ToArray(),
                        tcpListeners = ActiveTcpListeners()
                    });
                    form.Close();
                }
            };

            Application.Run(form);
            return exitCode;
        }
    }

    /// <summary>
    /// 生产 MainForm 的**启动检查**（R01 专用）。
    /// <para>只做一件事：真的把生产 <see cref="MainForm"/> 构造出来，看它会不会在构造期抛异常。
    /// 刻意**不跑消息循环**（不触发 <c>OnShown</c>）⇒ 网页服务不会开始监听，
    /// 托盘图标构造后立即隐藏 ⇒ 隔离门只作为<strong>观测项</strong>记录，不作为失败条件
    /// （托盘图标在构造期出现是生产既有行为，见 <see cref="Run"/> 的 gate1）。
    /// 这正是 R01 的暴露面：<c>MainForm..ctor → UIController..ctor → ApplyTheme →
    /// ApplyGpuRenderingRoute → MainForm.IsWindowLayered()</c> 这条链在构造期就会读
    /// <c>_winHelper</c>。</para>
    /// <para><b>退出码契约：</b>0 = 构造成功；1 = 构造抛异常（真实缺陷）；2 = 环境不具备
    /// 创建窗口的条件（NOT_VERIFIED_ENV，**不得**当成通过）。</para>
    /// </summary>
    public static int RunStartupCheck(string output)
    {
        MainForm form;
        try
        {
            form = new MainForm();
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(output, "startup-error.log"), exception.ToString());
            return 1;
        }

        using (form)
        {
            bool? trayVisible = ObserveTrayIcon(form);
            bool webServerRunning = LiteWebServer.Instance?.IsRunning ?? false;

            try
            {
                HideTrayIcon(form);
            }
            catch (Exception)
            {
                // 隐藏托盘只是收尾动作，失败不影响"构造成功"这个结论。
            }

            File.WriteAllText(
                Path.Combine(output, "startup-ok.json"),
                System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        task = "mainform-startup-check",
                        scenario = "startup",
                        verdict = "PASS",
                        note = "The production MainForm was constructed without throwing.",
                        trayIconVisibleAtConstruction = trayVisible,
                        webServerRunning,
                        webServerPort = LiteWebServer.Instance?.CurrentRunningPort ?? -1,
                        hardwareSamplerCreated = HardwareMonitor.Instance is not null,
                        baseDirectory = AppContext.BaseDirectory,
                        finishedAt = DateTimeOffset.Now
                    },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
    }

    private static void HideTrayIcon(MainForm form)
    {
        FieldInfo? helperField = typeof(MainForm).GetField("_bizHelper", BindingFlags.Instance | BindingFlags.NonPublic);
        object? helper = helperField?.GetValue(form);
        FieldInfo? trayField = helper?.GetType().GetField("_tray", BindingFlags.Instance | BindingFlags.NonPublic);
        if (trayField?.GetValue(helper) is NotifyIcon tray)
        {
            tray.Visible = false;
        }
    }

    private static void RecreateViaReflection(Form target)
    {
        if (RecreateHandleMethod is null)
        {
            throw new InvalidOperationException("Control.RecreateHandle was not found through reflection.");
        }

        // The protected method is invoked the same way tools/GpuUiProbe does it
        // (Program.cs:469-473). Production code must not use reflection; this
        // is probe-only.
        RecreateHandleMethod.Invoke(target, []);
    }

    private static async Task WaitForFadeAsync(Form form, TimeSpan limit)
    {
        DateTime deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (form.Opacity >= 0.999d)
            {
                return;
            }

            await Task.Delay(100);
        }
    }

    private static bool? ObserveTrayIcon(MainForm form)
    {
        try
        {
            FieldInfo? helperField = typeof(MainForm).GetField("_bizHelper", BindingFlags.Instance | BindingFlags.NonPublic);
            object? helper = helperField?.GetValue(form);
            FieldInfo? trayField = helper?.GetType().GetField("_tray", BindingFlags.Instance | BindingFlags.NonPublic);
            if (trayField?.GetValue(helper) is NotifyIcon tray)
            {
                return tray.Visible;
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    private static string[] ListBaseDirectoryFiles()
    {
        try
        {
            return Directory
                .EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}resources{Path.DirectorySeparatorChar}shaders{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(path => path.Substring(AppContext.BaseDirectory.Length))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>TCP listeners currently active on the machine, used to prove the web server stayed down.</summary>
    public static object[] ActiveTcpListeners()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(endpoint => new
                {
                    address = endpoint.Address.ToString(),
                    endpoint.Port
                })
                .Cast<object>()
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
