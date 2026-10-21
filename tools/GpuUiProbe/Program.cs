using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using LiteMonitor;
using LiteMonitor.Rendering.Gpu.Device;
using LiteMonitor.Rendering.Presentation;
using LiteMonitor.src.Core;

namespace GpuUiProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string theme = args.Length > 0 ? args[0] : "LiquidGlass_GPU";
            string output = args.Length > 1
                ? Path.GetFullPath(args[1])
                : Path.Combine(AppContext.BaseDirectory, "evidence");
            bool live = args.Skip(2).Any(argument =>
                string.Equals(argument, "--live", StringComparison.OrdinalIgnoreCase));
            bool horizontal = args.Skip(2).Any(argument =>
                string.Equals(argument, "--horizontal", StringComparison.OrdinalIgnoreCase));
            bool legacy = args.Skip(2).Any(argument =>
                string.Equals(argument, "--legacy", StringComparison.OrdinalIgnoreCase));
            bool recreateHandle = args.Skip(2).Any(argument =>
                string.Equals(argument, "--recreate-handle", StringComparison.OrdinalIgnoreCase));
            bool cycleLifecycle = args.Skip(2).Any(argument =>
                string.Equals(argument, "--cycle-lifecycle", StringComparison.OrdinalIgnoreCase));
            bool explicitTopMost = args.Skip(2).Any(argument =>
                string.Equals(argument, "--topmost", StringComparison.OrdinalIgnoreCase));
            bool explicitForeground = args.Skip(2).Any(argument =>
                string.Equals(argument, "--foreground", StringComparison.OrdinalIgnoreCase));
            int stressMinutes = GetIntegerArgument(args, "--stress-minutes=", 0);
            int stressIntervalMilliseconds = GetIntegerArgument(
                args,
                "--stress-interval=",
                6000);
            int screenIndex = GetIntegerArgument(args, "--screen-index=", 0);
            int captureAfter = GetIntegerArgument(args, "--capture-after-ms=", 0);
            if (captureAfter < 0 || captureAfter > 15000) throw new ArgumentOutOfRangeException(nameof(captureAfter));
            string? cancelFile = GetStringArgument(args, "--cancel-file=");
            if (stressMinutes < 1 && stressMinutes != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stressMinutes),
                    "--stress-minutes must be zero or a positive integer.");
            }

            if (stressIntervalMilliseconds < 3000)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stressIntervalMilliseconds),
                    "--stress-interval must be at least 3000 ms.");
            }
            if (live && legacy)
            {
                throw new ArgumentException(
                    "--live and --legacy cannot be combined.");
            }
            if (stressMinutes > 0 && (explicitTopMost || explicitForeground))
            {
                throw new ArgumentException(
                    "Long stress runs cannot use --topmost or --foreground. " +
                    "Those flags are reserved for an explicitly requested short screenshot.");
            }
            if (explicitTopMost != explicitForeground)
            {
                throw new ArgumentException(
                    "--topmost and --foreground must be supplied together.");
            }
            if (!string.IsNullOrWhiteSpace(cancelFile))
            {
                cancelFile = Path.GetFullPath(cancelFile);
            }

            Directory.CreateDirectory(output);
            ProbeDiagnostics.Directory = output;
            WriteAtomicJson(
                Path.Combine(output, "probe-01-started.json"),
                new { phase = "started", theme, live, output });
            ApplicationConfiguration.Initialize();
            if (screenIndex < 0 || screenIndex >= Screen.AllScreens.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(screenIndex),
                    $"--screen-index must be between 0 and {Screen.AllScreens.Length - 1}.");
            }

            WriteIsolatedSettings(
                theme,
                live,
                horizontal,
                explicitTopMost,
                screenIndex);
            Application.SetUnhandledExceptionMode(
                UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, eventArgs) =>
                WriteFailure(output, eventArgs.Exception);

            MainForm form = new ProbeMainForm(
                showWithoutActivation: !explicitForeground);
            DemoBackdropForm? backdrop = null;
            System.Windows.Forms.Timer timer = new() { Interval = 250 };
            Stopwatch elapsedClock = Stopwatch.StartNew();
            bool captured = false;
            bool handleRecreated = false;
            long originalHandle = 0;
            bool hideChecked = false;
            bool showChecked = false;
            bool cyberChecked = false;
            bool liquidChecked = false;
            List<object> lifecycleSteps = [];
            List<object> stressSamples = [];
            List<StressResourceSample> stressResourceSamples = [];
            int stressCycle = 0;
            int stressThemeSwitches = 0;
            int stressHideCount = 0;
            int stressShowCount = 0;
            int nextStressHide = 1000;
            int nextStressShow = 2000;
            int nextStressTheme = 3000;
            int nextStressCycle = stressIntervalMilliseconds;
            int nextStressRecord = 60000;
            int nextStressResourceSample = 0;
            int stressResourceCheckpointStart = 0;
            string stressTheme = theme;
            bool stressFinalized = false;
            int? stressExitCode = null;
            int targetStressMilliseconds = checked(stressMinutes * 60000);

            void FinalizeStress(string status, string? reason)
            {
                if (stressFinalized)
                {
                    return;
                }

                stressFinalized = true;
                timer.Stop();
                long actualElapsedMilliseconds = elapsedClock.ElapsedMilliseconds;
                GpuPresentationHostController? finalHost = GetGpuHost(form);
                object? resourcesBeforeDispose = finalHost?.ResourceSnapshot;
                GpuResourceSnapshot? resourcesAfterDispose = null;
                finalHost?.Dispose();
                if (finalHost is not null)
                {
                    resourcesAfterDispose = finalHost.ResourceSnapshot;
                }
                ProcessResourceCounters? processAfterDispose =
                    ReadProcessResourceCounters();

                WriteAtomicJson(
                    Path.Combine(output, "stress.diagnostics.json"),
                    new
                    {
                        task = "R22-gpu-mainform-stress",
                        executionModel = "DeepSeek V4.1 Flash",
                        status,
                        reason,
                        live,
                        screenIndex,
                        timingMode = "monotonic Stopwatch wall clock",
                        stressMinutes,
                        targetStressMilliseconds,
                        actualElapsedMilliseconds,
                        stressIntervalMilliseconds,
                        windowPolicy = new
                        {
                            topMost = explicitTopMost,
                            foregroundActivation = explicitForeground,
                            periodicShowActivates = false,
                            periodicShowBringsToFront = false
                        },
                        stressCycle,
                        stressThemeSwitches,
                        stressHideCount,
                        stressShowCount,
                        lifecycleSteps,
                        samples = stressSamples,
                        resourceSamples = stressResourceSamples,
                        resourcesBeforeDispose,
                        resourcesAfterDispose,
                        processAfterDispose,
                        finalResourcesZero =
                            resourcesAfterDispose?.ActiveResourceCount == 0 &&
                            resourcesAfterDispose?.ActiveBytes == 0,
                        evidenceBounds = "dedicated synthetic backdrop only",
                        capturedAt = DateTimeOffset.Now
                    });
                WriteAtomicJson(
                    Path.Combine(output, "stress.resource-trace.json"),
                    new
                    {
                        task = "R22-gpu-mainform-resource-trace",
                        status,
                        screenIndex,
                        sampleIntervalMilliseconds = 5000,
                        sampleCount = stressResourceSamples.Count,
                        samples = stressResourceSamples,
                        capturedAt = DateTimeOffset.Now
                    });
                WriteAtomicJson(
                    Path.Combine(output, "stress.progress.json"),
                    new
                    {
                        task = "R22-gpu-mainform-stress",
                        status,
                        reason,
                        finalized = true,
                        screenIndex,
                        actualElapsedMilliseconds,
                        targetStressMilliseconds,
                        checkpointCount = stressSamples.Count,
                        stressCycle,
                        stressThemeSwitches,
                        stressHideCount,
                        stressShowCount,
                        finalResourcesZero =
                            resourcesAfterDispose?.ActiveResourceCount == 0 &&
                            resourcesAfterDispose?.ActiveBytes == 0,
                        processAfterDispose,
                        resourceTraceCount = stressResourceSamples.Count,
                        diagnosticFile = "stress.diagnostics.json",
                        capturedAt = DateTimeOffset.Now
                    });
                stressExitCode = status switch
                {
                    "completed" => 0,
                    "cancelled" => 2,
                    "aborted" => 3,
                    _ => 1
                };
            }

            if (stressMinutes > 0)
            {
                WriteAtomicJson(
                    Path.Combine(output, "stress.progress.json"),
                    new
                    {
                        task = "R22-gpu-mainform-stress",
                        executionModel = "DeepSeek V4.1 Flash",
                        status = "running",
                        live,
                        screenIndex,
                        timingMode = "monotonic Stopwatch wall clock",
                        stressMinutes,
                        targetStressMilliseconds,
                        stressIntervalMilliseconds,
                        windowPolicy = new
                        {
                            topMost = explicitTopMost,
                            foregroundActivation = explicitForeground,
                            periodicShowActivates = false,
                            periodicShowBringsToFront = false
                        },
                        commandLine = Environment.GetCommandLineArgs(),
                        startedAt = DateTimeOffset.Now,
                        evidenceBounds = "dedicated synthetic backdrop only"
                    });
                form.FormClosing += (_, _) =>
                {
                    if (!stressFinalized)
                    {
                        FinalizeStress(
                            "aborted",
                            "window closed or external shutdown requested");
                    }
                };
            }

            timer.Tick += (_, _) =>
            {
                int elapsed = (int)Math.Min(
                    int.MaxValue,
                    elapsedClock.ElapsedMilliseconds);
                if (stressMinutes > 0)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(cancelFile) &&
                            File.Exists(cancelFile))
                        {
                            FinalizeStress("cancelled", "cancel file observed");
                            form.Close();
                            return;
                        }

                        if (!form.Visible && elapsed >= nextStressShow)
                        {
                            form.Show();
                            stressShowCount++;
                            lifecycleSteps.Add(
                                ReadLifecycleStep(
                                    $"stress-show-{stressShowCount}",
                                    form));
                            nextStressShow += stressIntervalMilliseconds;
                        }
                        else if (form.Visible && elapsed >= nextStressHide)
                        {
                            form.Hide();
                            stressHideCount++;
                            lifecycleSteps.Add(
                                ReadLifecycleStep(
                                    $"stress-hide-{stressHideCount}",
                                    form));
                            nextStressHide += stressIntervalMilliseconds;
                        }

                        if (elapsed >= nextStressTheme)
                        {
                            stressTheme = stressTheme.Contains(
                                "Cyber",
                                StringComparison.OrdinalIgnoreCase)
                                ? "LiquidGlass_GPU"
                                : "CyberPunk_GPU";
                            ApplyProbeTheme(form, stressTheme);
                            stressThemeSwitches++;
                            lifecycleSteps.Add(
                                ReadLifecycleStep(
                                    $"stress-theme-{stressThemeSwitches}",
                                    form));
                            nextStressTheme += stressIntervalMilliseconds;
                        }

                        if (elapsed >= nextStressCycle)
                        {
                            stressCycle++;
                            nextStressCycle += stressIntervalMilliseconds;
                        }

                        if (elapsed >= nextStressResourceSample)
                        {
                            StressResourceSample? resourceSample =
                                ReadStressResourceSample(elapsed, form);
                            if (resourceSample is not null)
                            {
                                stressResourceSamples.Add(resourceSample);
                                WriteAtomicJson(
                                    Path.Combine(output, "stress.resource-trace.json"),
                                    new
                                    {
                                        task = "R22-gpu-mainform-resource-trace",
                                        status = "running",
                                        screenIndex,
                                        sampleIntervalMilliseconds = 5000,
                                        sampleCount = stressResourceSamples.Count,
                                        samples = stressResourceSamples,
                                        capturedAt = DateTimeOffset.Now
                                    });
                            }

                            nextStressResourceSample += 5000;
                        }

                        if (elapsed >= nextStressRecord)
                        {
                            object sample = ReadStressSample(
                                form,
                                elapsed,
                                stressCycle,
                                stressThemeSwitches,
                                stressHideCount,
                                stressShowCount);
                            stressSamples.Add(
                                sample);
                            int checkpointNumber = stressSamples.Count;
                            StressResourceSample[] resourceWindowSamples =
                                stressResourceSamples
                                    .Skip(stressResourceCheckpointStart)
                                    .ToArray();
                            stressResourceCheckpointStart =
                                stressResourceSamples.Count;
                            WriteAtomicJson(
                                Path.Combine(
                                    output,
                                    $"stress.checkpoint-{checkpointNumber:D4}.json"),
                                new
                                {
                                    task = "R22-gpu-mainform-stress-checkpoint",
                                    status = "running",
                                    checkpointNumber,
                                    actualElapsedMilliseconds = elapsed,
                                    stressCycle,
                                    stressThemeSwitches,
                                    stressHideCount,
                                    stressShowCount,
                                    sample,
                                    resourceWindowSamples,
                                    capturedAt = DateTimeOffset.Now
                                });
                            WriteAtomicJson(
                                Path.Combine(output, "stress.progress.json"),
                                new
                                {
                                    task = "R22-gpu-mainform-stress",
                                    status = "running",
                                    actualElapsedMilliseconds = elapsed,
                                    targetStressMilliseconds,
                                    checkpointCount = stressSamples.Count,
                                    stressCycle,
                                    stressThemeSwitches,
                                    stressHideCount,
                                    stressShowCount,
                                    latestCheckpoint = $"stress.checkpoint-{checkpointNumber:D4}.json",
                                    latestSample = sample,
                                    resourceWindowSamples,
                                    capturedAt = DateTimeOffset.Now
                                });
                            nextStressRecord += 60000;
                        }

                        if (elapsed >= targetStressMilliseconds)
                        {
                            FinalizeStress("completed", null);
                            form.Close();
                        }
                    }
                    catch (Exception exception)
                    {
                        WriteFailure(output, exception);
                        FinalizeStress("failed", exception.Message);
                        form.Close();
                    }

                    return;
                }

                if (cycleLifecycle && !hideChecked && elapsed >= 1000)
                {
                    hideChecked = true;
                    form.Hide();
                    lifecycleSteps.Add(ReadLifecycleStep("hidden", form));
                    return;
                }

                if (cycleLifecycle && !showChecked && elapsed >= 1500)
                {
                    showChecked = true;
                    form.Show();
                    lifecycleSteps.Add(ReadLifecycleStep("shown", form));
                    return;
                }

                if (cycleLifecycle && !cyberChecked && elapsed >= 2100)
                {
                    cyberChecked = true;
                    ApplyProbeTheme(form, "CyberPunk_GPU");
                    lifecycleSteps.Add(ReadLifecycleStep("cyber-applied", form));
                    return;
                }

                if (cycleLifecycle && cyberChecked && !liquidChecked && elapsed >= 2900)
                {
                    liquidChecked = true;
                        ApplyProbeTheme(form, theme);
                    lifecycleSteps.Add(ReadLifecycleStep("liquid-restored", form));
                    return;
                }

                if (recreateHandle && !handleRecreated &&
                    elapsed >= (cycleLifecycle ? 3600 : 2000))
                {
                    originalHandle = form.Handle.ToInt64();
                    typeof(Form)
                        .GetMethod(
                            "RecreateHandle",
                            BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.Invoke(form, null);
                    handleRecreated = true;
                    lifecycleSteps.Add(ReadLifecycleStep("handle-recreated", form));
                    return;
                }

                int captureAt = cycleLifecycle
                    ? 5000
                    : recreateHandle
                        ? 3800
                        : 2500;
                captureAt = Math.Max(captureAt, captureAfter);
                if (elapsed < captureAt || captured)
                {
                    return;
                }

                try
                {
                    FieldInfo? uiField = typeof(MainForm).GetField(
                        "_ui",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    object? ui = uiField?.GetValue(form);
                    PropertyInfo? activeProperty = ui?.GetType().GetProperty(
                        "IsGpuPresentationActive",
                        BindingFlags.Instance | BindingFlags.Public);
                    bool active = activeProperty?.GetValue(ui) is true;
                    FieldInfo? hostField = ui!.GetType().GetField(
                        "_gpuPresentation",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    GpuPresentationHostController? host =
                        hostField?.GetValue(ui) as GpuPresentationHostController;
                    if (legacy)
                    {
                        if (active || host is not null)
                        {
                            throw new InvalidOperationException(
                                "Legacy theme unexpectedly entered the GPU route.");
                        }

                        string? fallback = ui?.GetType().GetProperty(
                            "GpuFallbackCode",
                            BindingFlags.Instance | BindingFlags.Public)
                            ?.GetValue(ui) as string;
                        object? route = ui!.GetType().GetField(
                            "_gpuRoute",
                            BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.GetValue(ui);
                        string legacyStem = theme.Contains(
                            "Cyber",
                            StringComparison.OrdinalIgnoreCase)
                            ? "cyber"
                            : "glass";
                        File.WriteAllText(
                            Path.Combine(output, $"{legacyStem}-legacy.diagnostics.json"),
                            JsonSerializer.Serialize(
                                new
                                {
                                    task = "R19-legacy-route",
                                    theme,
                                    legacy,
                                    gpuActive = active,
                                    gpuHostPresent = host is not null,
                                    fallback,
                                    route = route?.ToString(),
                                    captureSessionsCreated = 0,
                                    evidenceBounds = "no desktop capture",
                                    capturedAt = DateTimeOffset.Now
                                },
                                new JsonSerializerOptions { WriteIndented = true }));
                        captured = true;
                        timer.Stop();
                        form.Close();
                        return;
                    }

                    if (!active)
                    {
                        GpuStateSnapshot before = ReadGpuState(form, "before-forced-rebuild");
                        WriteAtomicJson(
                            Path.Combine(output, "gpu-ui-state-before.json"),
                            before);

                        string? fallback = ui?.GetType().GetProperty(
                            "GpuFallbackCode",
                            BindingFlags.Instance | BindingFlags.Public)
                            ?.GetValue(ui) as string;
                        object? route = ui!.GetType().GetField(
                            "_gpuRoute",
                            BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.GetValue(ui);
                        throw new InvalidOperationException(
                            "GPU presentation did not become active: " +
                            fallback +
                            "; route=" +
                            (route?.ToString() ?? "null") +
                            "; themeRendering=" +
                            (ThemeManager.Current.Rendering?.Backend ?? "null") +
                            "/" +
                            (ThemeManager.Current.Rendering?.Material ?? "null") +
                            "; host=" +
                            (host is null ? "null" : "present") +
                            "; code=" +
                            (host?.LastErrorCode ?? "none") +
                            "; message=" +
                            (host?.LastErrorMessage ?? "none") +
                            "; stateBefore=" +
                            JsonSerializer.Serialize(before));
                    }

                    if (host is null)
                    {
                        throw new InvalidOperationException(
                            "The GPU presentation controller is unavailable.");
                    }

                    string stem = theme.Contains("Cyber", StringComparison.OrdinalIgnoreCase)
                        ? "cyber"
                        : "glass";
                    if (horizontal)
                    {
                        stem += "-horizontal";
                    }

                    if (live)
                    {
                        stem += "-live";
                    }

                    bool sceneCaptureWritten =
                        !live || (explicitTopMost && explicitForeground);
                    if (sceneCaptureWritten)
                    {
                        host.CaptureScenePng(
                            Path.Combine(
                                output,
                                $"{stem}-mainform.png"));
                    }
                    bool externalCaptureWritten = false;
                    if (live)
                    {
                        if (explicitTopMost && explicitForeground)
                        {
                            CaptureExternal(
                                form,
                                Path.Combine(output, $"{stem}-external.png"));
                            externalCaptureWritten = true;
                        }
                    }

                    Settings liveSettings = Settings.Load();
                    bool packagedLiveDefault =
                        ThemeManager.Current.Rendering?.Parameters.TryGetValue(
                            "liveBackground",
                            out JsonElement liveDefault) == true &&
                        liveDefault.ValueKind == JsonValueKind.True;
                    GpuSceneRendererDiagnostics? rendererDiagnostics =
                        host.Diagnostics();
                    PresentationDiagnostics? presentationDiagnostics =
                        host.PresentationDiagnostics();
                    GpuResourceSnapshot resourcesBeforeDispose =
                        host.ResourceSnapshot;
                    int captureSessionsCreated = host.CaptureSessionsCreated;
                    var backgroundInput = host.BackgroundInput;
                    host.Dispose();
                    GpuResourceSnapshot resourcesAfterDispose =
                        host.ResourceSnapshot;
                    object payload = new
                    {
                        task = "R19-mainform-route",
                        theme,
                        live,
                        horizontal,
                        cycleLifecycle,
                        lifecycleSteps,
                        windowPolicy = new
                        {
                            topMost = explicitTopMost,
                            foregroundActivation = explicitForeground,
                            periodicShowActivates = false,
                            periodicShowBringsToFront = false
                        },
                        settingsEntry = new
                        {
                            gpuEffectsEnabled =
                                liveSettings.GpuThemeEffectsEnabled,
                            liveBackgroundEnabled =
                                liveSettings.GpuLiveBackgroundEnabled,
                            wdaPolicyAccepted =
                                liveSettings.GpuWdaCapturePolicyAccepted
                        },
                        themeRendering = new
                        {
                            packagedLiveDefault,
                            themeFilePath = Path.Combine(
                                ThemeManager.ThemeDir,
                                theme + ".json")
                        },
                        handleRecreated,
                        originalHandle = $"0x{originalHandle:X}",
                        finalHandle = $"0x{form.Handle.ToInt64():X}",
                        clientSize = new
                        {
                            form.ClientSize.Width,
                            form.ClientSize.Height,
                            form.DeviceDpi
                        },
                        renderer = rendererDiagnostics,
                        presentation = presentationDiagnostics,
                        captureSessionsCreated,
                        backgroundInput,
                        resourcesBeforeDispose,
                        resourcesAfterDispose,
                        finalResourcesZero =
                            resourcesAfterDispose.ActiveResourceCount == 0 &&
                            resourcesAfterDispose.ActiveBytes == 0,
                        externalCapture = new
                        {
                            sceneWritten = sceneCaptureWritten,
                            written = externalCaptureWritten,
                            skippedForPrivacyOrInvalidVisibility =
                                live && !externalCaptureWritten
                        },
                        evidenceBounds = live
                            ? "live desktop capture; images suppressed without explicit short visibility proof"
                            : "fixed material input (see backgroundInput)",
                        capturedAt = DateTimeOffset.Now
                    };
                    File.WriteAllText(
                        Path.Combine(output, $"{stem}-mainform.diagnostics.json"),
                        JsonSerializer.Serialize(
                            payload,
                            new JsonSerializerOptions { WriteIndented = true }));
                    captured = true;
                    timer.Stop();
                    form.Close();
                }
                catch (Exception exception)
                {
                    WriteFailure(output, exception);
                    timer.Stop();
                    form.Close();
                }
            };

            form.Shown += (_, _) =>
            {
                if (live)
                {
                    backdrop = new DemoBackdropForm(
                        form.Location,
                        form.ClientSize,
                        topMost: explicitTopMost);
                    backdrop.Show();
                    if (!explicitTopMost)
                    {
                        backdrop.SendToBackWithoutActivation();
                    }
                }

                if (explicitForeground)
                {
                    form.Activate();
                    form.BringToFront();
                }
                WriteAtomicJson(
                    Path.Combine(output, "gpu-ui-state-shown.json"),
                    ReadGpuState(form, "shown"));
                timer.Start();
            };
            form.FormClosing += (_, eventArgs) =>
            {
                try
                {
                    WriteAtomicJson(
                        Path.Combine(output, "probe-04-closing.json"),
                        new
                        {
                            phase = "closing",
                            reason = eventArgs.CloseReason,
                            elapsed = elapsedClock.ElapsedMilliseconds,
                            captured,
                            state = ReadGpuState(form, "closing")
                        });
                }
                catch
                {
                    // A diagnostic failure must never block the close path.
                }
            };

            WriteAtomicJson(
                Path.Combine(output, "probe-02-before-run.json"),
                new
                {
                    phase = "before-run",
                    state = ReadGpuState(form, "before-run")
                });
            Application.Run(form);
            WriteAtomicJson(
                Path.Combine(output, "probe-03-after-run.json"),
                new
                {
                    phase = "after-run",
                    elapsed = elapsedClock.ElapsedMilliseconds,
                    captured,
                    state = ReadGpuState(form, "after-run")
                });
            backdrop?.Close();
            return stressMinutes > 0
                ? stressExitCode ?? 1
                : captured ? 0 : 1;
        }
        catch (Exception exception)
        {
            string output = args.Length > 1
                ? Path.GetFullPath(args[1])
                : Path.Combine(AppContext.BaseDirectory, "evidence");
            WriteFailure(output, exception);
            return 1;
        }
    }

    private static void WriteIsolatedSettings(
        string theme,
        bool live,
        bool horizontal,
        bool topMost,
        int screenIndex)
    {
        Screen screen = Screen.AllScreens[screenIndex];
        Point initialPosition = new(
            screen.WorkingArea.Left + Math.Min(120, screen.WorkingArea.Width / 8),
            screen.WorkingArea.Top + Math.Min(120, screen.WorkingArea.Height / 10));
        string path = Path.Combine(AppContext.BaseDirectory, "settings.json");
        object settings = new
        {
            Version = 9,
            Skin = theme,
            Language = "zh",
            HideMainForm = false,
            HideTrayIcon = true,
            ShowTaskbar = false,
            TopMost = topMost,
            HorizontalMode = horizontal,
            PanelWidth = horizontal ? 760 : 300,
            UIScale = 1.0,
            Opacity = 0.96,
            GpuThemeEffectsEnabled = true,
            GpuLiveBackgroundEnabled = live,
            GpuWdaCapturePolicyAccepted = live,
            LiveGlassEnabled = false,
            ScreenDevice = screen.DeviceName,
            Position = initialPosition,
            MonitorItems = Array.Empty<object>()
        };
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                settings,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ApplyProbeTheme(MainForm form, string theme)
    {
        FieldInfo? uiField = typeof(MainForm).GetField(
            "_ui",
            BindingFlags.Instance | BindingFlags.NonPublic);
        object? ui = uiField?.GetValue(form);
        ui?.GetType().GetMethod(
                "ApplyTheme",
                BindingFlags.Instance | BindingFlags.Public)
            ?.Invoke(ui, [theme, false]);
    }

    private static int GetIntegerArgument(
        string[] args,
        string prefix,
        int defaultValue)
    {
        foreach (string argument in args.Skip(2))
        {
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return int.Parse(argument[prefix.Length..]);
            }
        }

        return defaultValue;
    }

    private static string? GetStringArgument(
        string[] args,
        string prefix)
    {
        foreach (string argument in args.Skip(2))
        {
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return argument[prefix.Length..];
            }
        }

        return null;
    }

    private static GpuPresentationHostController? GetGpuHost(MainForm form)
    {
        FieldInfo? uiField = typeof(MainForm).GetField(
            "_ui",
            BindingFlags.Instance | BindingFlags.NonPublic);
        object? ui = uiField?.GetValue(form);
        return ui?.GetType().GetField(
                "_gpuPresentation",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(ui) as GpuPresentationHostController;
    }

    /// <summary>
    /// Snapshots the MainForm GPU wiring state so a failed activation can be
    /// traced without guessing. <paramref name="label"/> marks the moment the
    /// snapshot was taken inside the evidence timeline.
    /// </summary>
    internal static GpuStateSnapshot ReadGpuState(MainForm form, string label)
    {
        object? ui = typeof(MainForm).GetField(
            "_ui",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form);
        Type? uiType = ui?.GetType();
        object? route = uiType?.GetField(
            "_gpuRoute",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(ui);
        object? host = uiType?.GetField(
            "_gpuPresentation",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(ui);
        bool hostDisposed = host is not null &&
            host.GetType().GetField(
                "_disposed",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(host) is true;
        bool active = uiType?.GetProperty(
            "IsGpuPresentationActive",
            BindingFlags.Instance | BindingFlags.Public)?.GetValue(ui) is true;
        string? fallback = uiType?.GetProperty(
            "GpuFallbackCode",
            BindingFlags.Instance | BindingFlags.Public)?.GetValue(ui) as string;
        string? runtimeFailure = uiType?.GetProperty(
            "GpuRuntimeFailure",
            BindingFlags.Instance | BindingFlags.Public)?.GetValue(ui) as string;
        return new GpuStateSnapshot(
            label,
            DateTimeOffset.Now,
            form.IsHandleCreated,
            form.Visible,
            uiType?.FullName,
            host is not null,
            hostDisposed,
            active,
            fallback,
            runtimeFailure,
            route?.ToString());
    }

    private static ProcessResourceCounters? ReadProcessResourceCounters()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            return new ProcessResourceCounters(
                WorkingSetBytes: process.WorkingSet64,
                PrivateMemoryBytes: process.PrivateMemorySize64,
                HandleCount: process.HandleCount,
                ThreadCount: process.Threads.Count,
                GdiObjects: GetGuiResources(process.Handle, 0),
                UserObjects: GetGuiResources(process.Handle, 1));
        }
        catch
        {
            return null;
        }
    }

    private static StressResourceSample? ReadStressResourceSample(
        int elapsedMilliseconds,
        MainForm form)
    {
        ProcessResourceCounters? processCounters =
            ReadProcessResourceCounters();
        if (processCounters is null)
        {
            return null;
        }

        GpuPresentationHostController? host = GetGpuHost(form);
        return new StressResourceSample(
            ElapsedMilliseconds: elapsedMilliseconds,
            Visible: form.Visible,
            Theme: ThemeManager.Current.Name,
            Process: processCounters,
            CaptureSessions: host?.CaptureSessionsCreated ?? 0,
            CaptureActive: host?.IsLiveCaptureStarted == true);
    }

    private static object ReadStressSample(
        MainForm form,
        int elapsedMilliseconds,
        int cycle,
        int themeSwitches,
        int hideCount,
        int showCount)
    {
        ProcessResourceCounters processCounters =
            ReadProcessResourceCounters() ??
            throw new InvalidOperationException(
                "Process resource counters could not be read.");
        GpuPresentationHostController? host = GetGpuHost(form);
        return new
        {
            elapsedMilliseconds,
            cycle,
            themeSwitches,
            hideCount,
            showCount,
            process = processCounters,
            gpu = host?.ResourceSnapshot,
            renderer = host?.Diagnostics(),
            captureSessions = host?.CaptureSessionsCreated,
            captureActive = host?.IsLiveCaptureStarted
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    private static object ReadLifecycleStep(string step, MainForm form)
    {
        FieldInfo? uiField = typeof(MainForm).GetField(
            "_ui",
            BindingFlags.Instance | BindingFlags.NonPublic);
        object? ui = uiField?.GetValue(form);
        FieldInfo? hostField = ui?.GetType().GetField(
            "_gpuPresentation",
            BindingFlags.Instance | BindingFlags.NonPublic);
        GpuPresentationHostController? host =
            hostField?.GetValue(ui) as GpuPresentationHostController;
        return new
        {
            step,
            visible = form.Visible,
            handle = $"0x{form.Handle.ToInt64():X}",
            theme = ThemeManager.Current.Name,
            active = host?.IsActive == true,
            captureSessions = host?.CaptureSessionsCreated ?? 0,
            captureState = host?.Diagnostics()?.CaptureState,
            errorCode = host?.LastErrorCode
        };
    }

    private static void CaptureExternal(Form form, string path)
    {
        Rectangle bounds = form.RectangleToScreen(form.ClientRectangle);
        using Bitmap bitmap = new(bounds.Width, bounds.Height);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void WriteFailure(string output, Exception exception)
    {
        Directory.CreateDirectory(output);
        File.WriteAllText(
            Path.Combine(output, "failure.log"),
            exception.ToString());
    }

    private static void WriteAtomicJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, path, overwrite: true);
    }
}

/// <summary>
/// Shared diagnostic directory. It is static on purpose: the probe form is
/// constructed through the base constructor, so a handle can be created
/// before any instance field of the derived type is assigned.
/// </summary>
internal static class ProbeDiagnostics
{
    public static string Directory { get; set; } = string.Empty;
}

internal sealed record ProcessResourceCounters(
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    int HandleCount,
    int ThreadCount,
    uint GdiObjects,
    uint UserObjects);

/// <summary>Snapshot of the MainForm GPU wiring at one instant.</summary>
internal sealed record GpuStateSnapshot(
    string Label,
    DateTimeOffset SampledAt,
    bool IsHandleCreated,
    bool Visible,
    string? UiType,
    bool HostPresent,
    bool HostDisposed,
    bool Active,
    string? Fallback,
    string? RuntimeFailure,
    string? Route);

internal sealed record StressResourceSample(
    int ElapsedMilliseconds,
    bool Visible,
    string Theme,
    ProcessResourceCounters Process,
    int CaptureSessions,
    bool CaptureActive);

internal sealed class ProbeMainForm : MainForm
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    private readonly bool _showWithoutActivation;
    private readonly IntPtr _foregroundBeforeShow;
    private int _handleGeneration;

    public ProbeMainForm(bool showWithoutActivation)
    {
        _showWithoutActivation = showWithoutActivation;
        // Remember who owned the foreground before the probe opens its own
        // window so the probe can hand the foreground back afterwards.
        _foregroundBeforeShow = showWithoutActivation ? GetForegroundWindow() : IntPtr.Zero;
        if (IsHandleCreated) UpdateStyles();
    }

    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; if (_showWithoutActivation) value.ExStyle |= WS_EX_NOACTIVATE; return value; }
    }

    protected override bool ShowWithoutActivation =>
        _showWithoutActivation;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WriteLifecycleSnapshot("handle-created");
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        WriteLifecycleSnapshot("handle-destroyed");
        base.OnHandleDestroyed(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        WriteLifecycleSnapshot("shown");
        RestoreForeground();
    }

    protected override void WndProc(ref Message m)
    {
        // A click on the probe window must never steal the user's foreground.
        if (_showWithoutActivation && m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    private void WriteLifecycleSnapshot(string eventName)
    {
        try
        {
            _handleGeneration++;
            File.WriteAllText(
                Path.Combine(
                    ProbeDiagnostics.Directory,
                    $"probe-lifecycle-{_handleGeneration:D2}-{eventName}.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        eventName,
                        generation = _handleGeneration,
                        at = DateTimeOffset.Now,
                        state = Program.ReadGpuState(this, eventName)
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Diagnostics must never break the probe run.
        }
    }

    private void RestoreForeground()
    {
        if (!_showWithoutActivation) return;
        if (_foregroundBeforeShow == IntPtr.Zero) return;
        if (!IsWindow(_foregroundBeforeShow)) return;
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return;
        // Any window of this probe process may have taken the foreground
        // (the live backdrop window is created after the main form is shown),
        // so hand it back whenever the foreground belongs to this process.
        GetWindowThreadProcessId(foreground, out uint foregroundProcessId);
        if (foregroundProcessId != (uint)Environment.ProcessId) return;
        SetForegroundWindow(_foregroundBeforeShow);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);
}

internal sealed class DemoBackdropForm : Form
{
    private readonly Rectangle _formBounds;
    private readonly System.Windows.Forms.Timer _timer;

    public DemoBackdropForm(
        Point formLocation,
        Size formSize,
        bool topMost)
    {
        Rectangle screenBounds = Screen.FromPoint(formLocation).Bounds;
        _formBounds = new Rectangle(
            formLocation.X - screenBounds.X,
            formLocation.Y - screenBounds.Y,
            formSize.Width,
            formSize.Height);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screenBounds;
        BackColor = Color.FromArgb(8, 28, 34);
        DoubleBuffered = true;
        TopMost = topMost;
        ShowInTaskbar = false;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += (_, _) => Invalidate();
        _timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; }
    }

    public void SendToBackWithoutActivation()
    {
        const uint noSize = 0x0001;
        const uint noMove = 0x0002;
        const uint noActivate = 0x0010;
        SetWindowPos(
            Handle,
            new IntPtr(1),
            0,
            0,
            0,
            0,
            noSize | noMove | noActivate);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode =
            System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using System.Drawing.Drawing2D.LinearGradientBrush background = new(
            ClientRectangle,
            Color.FromArgb(8, 76, 88),
            Color.FromArgb(5, 30, 34),
            30);
        graphics.FillRectangle(background, ClientRectangle);

        float phase =
            (float)(DateTime.Now.TimeOfDay.TotalSeconds % 8) / 8;
        int movingX = _formBounds.Left +
            (int)(_formBounds.Width * (-0.02 + phase * 0.56));
        using SolidBrush cyan =
            new(Color.FromArgb(78, 30, 196, 212));
        using SolidBrush amber =
            new(Color.FromArgb(54, 230, 142, 48));
        graphics.FillRectangle(
            cyan,
            movingX,
            _formBounds.Top + 60,
            Math.Max(160, _formBounds.Width / 2),
            Math.Max(140, _formBounds.Height / 4));
        graphics.FillRectangle(
            amber,
            _formBounds.Left + _formBounds.Width / 3,
            _formBounds.Top + _formBounds.Height / 2,
            _formBounds.Width / 2,
            _formBounds.Height / 3);
        using Pen grid =
            new(Color.FromArgb(34, 168, 220, 226), 1);
        for (int x = -ClientSize.Height; x < ClientSize.Width; x += 54)
        {
            graphics.DrawLine(grid, x, 0, x + ClientSize.Height, ClientSize.Height);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }
}
