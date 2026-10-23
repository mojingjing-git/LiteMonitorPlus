using System.Net.NetworkInformation;
using System.Reflection;
using LiteMonitor;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.WebServer;

namespace GpuHostLifecycleProbe;

/// <summary>
/// The isolation gate of the real-MainForm arm (LM-087).
/// </summary>
/// <remarks>
/// ★ LM-087：生产 <c>MainForm.OnShown</c> 会在**首次显示**时启动网页服务
///   （<c>src/UI/MainForm_Transparent.cs</c>：<c>if (_cfg.WebServerEnabled)</c> ⇒
///   <c>LiteWebServer.Instance?.Start(...)</c>）。
///   ⇒ 隔离门**不能**只看"此刻有没有人在监听"（服务还没启动 ⇒ 门通过），
///   必须约束**服务会不会被启动**：配置里开着网页服务 ⇒ 生产 OnShown 一定会开监听。
///   ⇒ 判定必须在 <c>Application.Run</c> **之前**求值（消息循环一跑，OnShown 就执行了），
///   这样"拒绝"才真的拦得住副作用。
/// </remarks>
internal sealed record IsolationGateResult(
    bool TrayIconHidden,
    bool WebServerNotListeningBefore,
    bool WebServerDisabledInConfig,
    bool? WebServerNotListeningAfter,
    bool Passed,
    string Reason);

/// <summary>
/// 隔离门的**纯判定**逻辑（LM-087）。
/// <para>抽成纯函数是为了让"拒绝"这条路径可以被真正地断言 ——
/// 而不是只能靠读源码文本猜测。</para>
/// </summary>
internal static class IsolationGate
{
    /// <summary>
    /// Evaluates the gate **before** the message loop starts.
    /// </summary>
    /// <param name="trayIconVisible">托盘图标是否可见（<c>null</c> = 读不到）。</param>
    /// <param name="webServerRunning">此刻是否已经有网页服务在监听。</param>
    /// <param name="webServerEnabledInConfig">
    /// 生产配置里的网页服务开关。★ 这是**决定性**的一项：它为 true 时，
    /// 生产 <c>OnShown</c> 一定会开监听 ⇒ 隔离承诺无法成立 ⇒ 必须拒绝。
    /// </param>
    internal static IsolationGateResult EvaluateBeforeRun(
        bool? trayIconVisible,
        bool webServerRunning,
        bool webServerEnabledInConfig)
    {
        bool trayHidden = trayIconVisible == false;
        bool notListening = !webServerRunning;
        bool disabled = !webServerEnabledInConfig;

        // ★ "配置里开着网页服务"本身就是**拒绝**的理由 ——
        //   原实现只检查"此刻有没有在监听"，于是门通过 → OnShown 真的开了监听。
        bool passed = trayHidden && notListening && disabled;

        List<string> reasons = [];
        if (!trayHidden)
        {
            reasons.Add("The tray icon is visible.");
        }

        if (!notListening)
        {
            reasons.Add("A web server is already listening.");
        }

        if (!disabled)
        {
            reasons.Add(
                "The configuration enables the web server, so the production MainForm.OnShown " +
                "would start a listener as soon as the window is shown.");
        }

        return new IsolationGateResult(
            trayHidden,
            notListening,
            disabled,
            null,
            passed,
            passed
                ? "The isolation gates hold: the tray icon is hidden, no web server is listening " +
                  "and the configuration keeps the production web server disabled."
                : string.Join(" ", reasons));
    }

    /// <summary>
    /// Re-checks the gate **after** the run: the web server must still be down.
    /// </summary>
    internal static IsolationGateResult EvaluateAfterRun(IsolationGateResult before, bool webServerRunningAfter)
    {
        bool notListeningAfter = !webServerRunningAfter;
        bool passed = before.Passed && notListeningAfter;
        return before with
        {
            WebServerNotListeningAfter = notListeningAfter,
            Passed = passed,
            Reason = passed
                ? before.Reason
                : before.Reason + " The web server was still listening after the run."
        };
    }
}

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
///   gate 3 - the configuration must keep the production web server disabled
///             (otherwise MainForm.OnShown starts a real listener);
///   gate 4 - the web server must still be down after the run.
///
/// If a gate fails, the arm stops, records the concrete side effect and the
/// scenario is reported as NOT_VERIFIED instead of being used as evidence.
/// </summary>
internal static class RealMainFormScenario
{
    /// <summary>退出码：隔离门失败（拒绝执行这条 arm）。</summary>
    internal const int IsolationGateExitCode = 4;

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

            // ★ LM-087：隔离门必须在**服务真正启动之前**求值。
            //   生产 MainForm.OnShown 会在窗口首次显示时启动网页服务 ⇒ 消息循环一跑
            //   服务就可能开起来 ⇒ 门必须在此之前判定，并且判定"配置会不会让它开"。
            bool webServerEnabledInConfig = ReadWebServerEnabled(form);
            IsolationGateResult gate = IsolationGate.EvaluateBeforeRun(
                trayVisible,
                webServerRunning,
                webServerEnabledInConfig);

            bool gate1 = gate.TrayIconHidden;
            bool gate2 = gate.WebServerNotListeningBefore;
            bool gate3 = gate.WebServerDisabledInConfig;

            observations.Add(new
            {
                step = "side-effect-observation",
                trayIconVisible = trayVisible,
                webServerRunning,
                webServerPort,
                hardwareSamplerCreated = samplingActive,
                baseDirectory = AppContext.BaseDirectory,
                foregroundWindow = DesktopObservation.GetForegroundWindow().ToInt64(),
                webServerEnabledInConfig,
                isolationGate = gate
            });

            if (!gate.Passed)
            {
                // ★ 拒绝发生在 Application.Run **之前** ⇒ 生产 OnShown 永远不执行
                //   ⇒ 这条 arm 不会真的开出局域网监听。
                File.WriteAllText(
                    Path.Combine(output, "mainform-abort.json"),
                    System.Text.Json.JsonSerializer.Serialize(
                        new
                        {
                            task = "GPU-T01-recreate-handle",
                            scenario = "mainform",
                            verdict = "NOT_VERIFIED",
                            reason = "The isolation gates failed, so the real MainForm arm was abandoned.",
                            gateReason = gate.Reason,
                            observations,
                            gate1_trayIconHidden = gate1,
                            gate2_webServerNotListening = gate2,
                            gate3_webServerDisabledInConfig = gate3
                        },
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return IsolationGateExitCode;
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
                webServerEnabledInConfig,
                isolationGates = new
                {
                    gate1_trayIconHidden = gate1,
                    gate2_webServerNotListening = gate2,
                    gate3_webServerDisabledInConfig = gate3
                }
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
                int code = 1;
                string? failure = null;
                try
                {
                    // The production MainForm fades in from Opacity = 0; the
                    // measurement must not start before the fade has settled.
                    await WaitForFadeAsync(form, TimeSpan.FromSeconds(4)).ConfigureAwait(true);
                    using CancellationTokenSource cancellation = new(timeoutMs);
                    await experiment.RunAsync(cancellation.Token);
                    code = 0;
                }
                catch (OperationCanceledException)
                {
                    code = 3;
                    failure = "The run exceeded its own deadline.";
                }
                catch (Exception exception)
                {
                    code = 1;
                    failure = exception.ToString();
                    File.WriteAllText(Path.Combine(output, "failure.log"), exception.ToString());
                }

                // ★ LM-087：最后一次副作用快照必须写进**这一份**证据文件
                //   （原实现在 WriteEvidence **之后**才追加 ⇒ 主结果里根本没有它）。
                experiment.ExtraObservations.Add(new
                {
                    step = "overlay-errors",
                    errors = _overlayErrors.ToArray(),
                    tcpListeners = ActiveTcpListeners()
                });

                try
                {
                    exitCode = experiment.WriteEvidence("mainform", code, failure);
                }
                catch (Exception exception)
                {
                    exitCode = code == 0 ? 1 : code;
                    try
                    {
                        File.WriteAllText(Path.Combine(output, "failure.log"), exception.ToString());
                    }
                    catch (Exception)
                    {
                        // 证据文件写不出来时，退出码已经是非零 ⇒ 不会被当成通过。
                    }
                }
                finally
                {
                    form.Close();
                }
            };

            Application.Run(form);

            // ★ LM-087：隔离承诺必须覆盖**整个运行期** —— 结束后再查一次。
            //   若生产 OnShown 真的开起了监听，这里必须如实报出来并让退出码非零。
            bool webServerRunningAfter = LiteWebServer.Instance?.IsRunning ?? false;
            IsolationGateResult finalGate = IsolationGate.EvaluateAfterRun(gate, webServerRunningAfter);
            WriteIsolationFinalEvidence(output, finalGate);

            if (!finalGate.Passed)
            {
                // 重写主证据，让**文件里的结论**与**进程退出码**保持一致。
                try
                {
                    exitCode = experiment.WriteEvidence(
                        "mainform",
                        IsolationGateExitCode,
                        "The isolation gate failed after the run: " + finalGate.Reason);
                }
                catch (Exception)
                {
                    exitCode = IsolationGateExitCode;
                }
            }

            return exitCode;
        }
    }

    /// <summary>
    /// 读取生产配置里"网页服务是否开启"的开关。
    /// <para>★ 读不到时**按"可能会启动"处理**（保守）：宁可中止这条 arm，
    /// 也不许真的开一个局域网监听（LM-087）。</para>
    /// </summary>
    private static bool ReadWebServerEnabled(MainForm form)
    {
        object? settings = null;
        try
        {
            FieldInfo? cfgField = typeof(MainForm).GetField("_cfg", BindingFlags.Instance | BindingFlags.NonPublic);
            settings = cfgField?.GetValue(form);
        }
        catch (Exception)
        {
            // 读不到 ⇒ 见 ResolveWebServerEnabled 的保守说明。
        }

        return ResolveWebServerEnabled(ReadWebServerEnabledFrom(settings));
    }

    /// <summary>
    /// 从生产配置对象里读出网页服务开关；读不出来时返回 <c>null</c>（**未知**）。
    /// </summary>
    internal static bool? ReadWebServerEnabledFrom(object? settings)
    {
        if (settings is Settings typed)
        {
            return typed.WebServerEnabled;
        }

        return null;
    }

    /// <summary>
    /// 把"读到的开关"变成"是否必须拒绝这条 arm"。
    /// <para>★ <c>null</c>（读不到）⇒ 按 <c>true</c>（会启动）处理：宁可中止，
    /// 也不许真的开一个局域网监听（LM-087）。</para>
    /// </summary>
    internal static bool ResolveWebServerEnabled(bool? configured) => configured ?? true;

    /// <summary>写出运行结束后的隔离复验结果（独立文件，避免与主证据互相覆盖）。</summary>
    private static void WriteIsolationFinalEvidence(string output, IsolationGateResult gate)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(output, "mainform-isolation-final.json"),
                System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        task = "GPU-T01-recreate-handle",
                        scenario = "mainform",
                        step = "isolation-final",
                        verdict = gate.Passed ? "ISOLATION_HELD" : "NOT_VERIFIED",
                        reason = gate.Reason,
                        gate,
                        tcpListeners = ActiveTcpListeners(),
                        finishedAt = DateTimeOffset.Now
                    },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // 复验记录写不出来不影响退出码判定（退出码由 finalGate.Passed 决定）。
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
