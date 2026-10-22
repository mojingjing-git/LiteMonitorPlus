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
