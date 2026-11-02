using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using LiteMonitor;
using LiteMonitor.Rendering;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.SettingsPage;
using LiteMonitor.ThemeEditor;

internal static class AuditProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static T Field<T>(object value, string name) =>
        (T)value.GetType().GetField(name, Private)!.GetValue(value)!;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine(
                "Usage: ThemeStudioAuditProbe <output-parent-directory>");
            return 2;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();

        string parent;
        string output;
        try
        {
            parent = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(parent);
            output = CreateUniqueRunDirectory(parent);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "Unable to create a unique probe run directory: " + ex.Message);
            return 2;
        }

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var results = new Dictionary<string, object?>();
        Theme? theme = null;
        string? previousSettingsPath = SettingsHelper.PathOverride;
        bool previousBlock = SettingsHelper.GlobalBlockSave;
        int exitCode = 0;

        try
        {
            string settingsDir = Path.Combine(output, "settings");
            string themesDir = Path.Combine(output, "themes");
            Directory.CreateDirectory(settingsDir);
            Directory.CreateDirectory(themesDir);

            SettingsHelper.PathOverride = settingsDir;
            Settings.GlobalBlockSave = false;
            ThemeDocumentStore.SetThemeDirForTests(themesDir);
            ThemeDocumentStore.SetRecoveryDirForTests(Path.Combine(output, "drafts"));

            var cfg = new Settings
            {
                Skin = "AuditPrevious",
                GpuThemeEffectsEnabled = false,
                SettingThemeMode = SettingThemeMode.Dark
            };
            if (!cfg.TrySave())
            {
                throw new InvalidOperationException("The isolated settings fixture could not be written.");
            }

            SettingsThemeManager.Initialize(SettingThemeMode.Dark);

            theme = new Theme
            {
                Name = "AuditGpu",
                Color = new ColorConfig
                {
                    Background = "#112233",
                    TextPrimary = "#FFFFFF"
                },
                Rendering = new ThemeRenderingConfig
                {
                    Backend = "gpu",
                    Material = "cyber_punk",
                    FallbackStyle = new ThemeFallbackStyle
                    {
                        Background = "#AABBCC",
                        PanelBackground = "#BBCCDD",
                        Border = "#778899",
                        Text = "#111111",
                        MutedText = "#333333",
                        Accent = "#FF0000"
                    }
                }
            };
            theme.BuildFonts();
            File.WriteAllText(
                Path.Combine(themesDir, "AuditGpu.json"),
                JsonSerializer.Serialize(theme, ThemeManager.CreateSerializerOptions()));

            using (var form = new ThemeStudioForm(appSettings: cfg))
            {
                form.Show();
                form.OpenThemeForTesting("AuditGpu");
                Pump(50);
                ThemePreviewControl preview = form.PreviewControlForTesting!;
                string before = form.RenderedBackgroundForTesting ?? string.Empty;
                form.TargetComboForTesting.SelectedIndex = 1;
                Pump(200);
                string after = form.RenderedBackgroundForTesting ?? string.Empty;
                results["target_switch"] = new
                {
                    inspector = form.InspectorForTesting.Target.ToString(),
                    previewTarget = form.RenderedTargetForTesting?.ToString(),
                    before,
                    after,
                    expected = "#AABBCC",
                    status = form.RouteStatusTextForTesting,
                    classification = string.Equals(after, "#AABBCC", StringComparison.OrdinalIgnoreCase)
                        ? "expected"
                        : "defect"
                };

                form.InspectorForTesting.Focus();
                bool focusReported = form.SelectPropertyByRole(
                    ThemePropertyCatalog.SemanticElementRole.Title);
                results["focus_property"] = new
                {
                    focusReported,
                    inspectorHasFocus = form.InspectorForTesting.ContainsFocus,
                    lastFocusedEditor = form.InspectorForTesting.LastFocusedEditor?.GetType().Name,
                    classification = focusReported &&
                        form.InspectorForTesting.LastFocusedEditor is TextBoxBase
                        ? "expected"
                        : "defect"
                };

                string statusBefore = form.SavedStatusTextForTesting;
                typeof(Control).GetMethod("OnMouseDown", Private)!.Invoke(
                    preview,
                    [new MouseEventArgs(MouseButtons.Left, 1, 60, 70, 0)]);
                results["preview_click"] = new
                {
                    statusBefore,
                    statusAfter = form.SavedStatusTextForTesting,
                    expectedBehavior = "focus preview without changing active edit target",
                    classification = "expected"
                };

                form.SelectTargetForTesting(ThemeEditTarget.GpuMain);
                string mapped = ThemePropertyCatalog.ResolvePropertyPath(
                    ThemePropertyCatalog.SemanticElementRole.PanelBackground,
                    ThemeEditTarget.GpuMain);
                bool backgroundFocused = form.SelectPropertyByRole(
                    ThemePropertyCatalog.SemanticElementRole.PanelBackground);
                results["gpu_background_selection"] = new
                {
                    mapped = mapped.Length == 0 ? null : mapped,
                    focused = backgroundFocused,
                    invalidProbePath = "rendering.parameters.panel_color",
                    classification = backgroundFocused &&
                        string.Equals(
                            mapped,
                            "rendering.parameters.panelOpacity",
                            StringComparison.Ordinal)
                        ? "expected"
                        : "defect"
                };

                results["dark_mode"] = new
                {
                    selected = SettingsThemeManager.EffectiveMode.ToString(),
                    paletteIsDark = SettingsThemeManager.CurrentPalette.IsDark,
                    actualBack = form.BackColor.ToArgb().ToString("X8"),
                    actualInspectorBack = form.InspectorForTesting.BackColor.ToArgb().ToString("X8"),
                    classification = SettingsThemeManager.CurrentPalette.IsDark
                        ? "expected"
                        : "defect"
                };
                form.Close();
            }

            Settings.GlobalBlockSave = true;
            Type gatewayType = typeof(ThemeStudioLauncher).GetNestedType(
                "AppActionsThemeApplyGateway",
                BindingFlags.NonPublic)!;
            var gateway = (ThemeStudioForm.IThemeApplyGateway)Activator.CreateInstance(
                gatewayType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [cfg, null!, null!],
                culture: null)!;
            ThemeStudioForm.ThemeApplyResult applyResult =
                gateway.ApplySavedTheme("AuditGpu", 7);
            results["blocked_save"] = new
            {
                result = applyResult,
                expectedStage = ThemeStudioForm.ThemeApplyStage.SettingsPersisted,
                persistedSkin = ReadPersistedSkin(),
                memorySkin = cfg.Skin,
                classification =
                    applyResult.Stage == ThemeStudioForm.ThemeApplyStage.SettingsPersisted &&
                    string.Equals(cfg.Skin, "AuditPrevious", StringComparison.Ordinal)
                        ? "expected"
                        : "defect"
            };
            Settings.GlobalBlockSave = false;

            ThemeDocumentSession session = ThemeDocumentSession.Open("AuditGpu");
            try
            {
                string fallbackPng = Path.Combine(output, "fallback-export.png");
                ThemeImageExportResult fallbackExport = ThemeExportService.ExportEffectImage(
                    session,
                    fallbackPng,
                    ImageRoutePreference.Classic);
                results["fallback_export"] = DescribeFallbackExport(
                    session,
                    fallbackExport,
                    fallbackPng);

                string horizontalPng = Path.Combine(output, "gpu-horizontal-export.png");
                ThemeImageExportResult horizontalExport = ThemeExportService.ExportEffectImage(
                    session,
                    horizontalPng,
                    ImageRoutePreference.Gpu,
                    ThemeExportImageOrientation.Horizontal);
                results["gpu_horizontal_export"] = DescribeHorizontalExport(
                    horizontalExport,
                    horizontalPng);
            }
            finally
            {
                session.Draft.DisposeFonts();
            }

            try
            {
                ProbeGpu(theme, cfg, output, results);
            }
            catch (Exception ex)
            {
                results["gpu_probe_exception"] = ex.ToString();
            }
        }
        catch (Exception ex)
        {
            exitCode = 1;
            results["probe_fatal"] = new
            {
                type = ex.GetType().FullName,
                message = ex.Message,
                stack = ex.ToString()
            };
        }
        finally
        {
            Settings.GlobalBlockSave = previousBlock;
            SettingsHelper.PathOverride = previousSettingsPath;
            ThemeDocumentStore.SetThemeDirForTests(null!);
            ThemeDocumentStore.ResetRecoveryDir();
            theme?.DisposeFonts();
        }

        try
        {
            WriteRunMetadata(output, startedAt, DateTimeOffset.UtcNow, exitCode);
        }
        catch (Exception ex)
        {
            exitCode = 1;
            results["probe_metadata_error"] = new
            {
                type = ex.GetType().FullName,
                message = ex.Message,
                stack = ex.ToString(),
                classification = "defect"
            };
        }

        results["probe_exit_code"] = exitCode;
        results["classification_summary"] = BuildClassificationSummary(results);
        string report = JsonSerializer.Serialize(
            results,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "observations.json"), report);
        Console.WriteLine(report);
        return exitCode;
    }

    private static void ProbeGpu(
        Theme theme,
        Settings cfg,
        string output,
        IDictionary<string, object?> results)
    {
        using var gpuHost = new Form { ClientSize = new Size(400, 500) };
        using var gpu = new GpuThemePreviewControl(
            theme,
            ThemePreviewControl.CreateMockGroups(),
            new GpuStartupGuard(Path.Combine(output, "gpu-guard"), AppContext.BaseDirectory))
        {
            Size = new Size(300, 420)
        };
        gpuHost.Controls.Add(gpu);
        gpuHost.Show();
        Pump(200);
        results["gpu_start"] = new
        {
            gpu.IsActive,
            gpu.Error,
            gpu.DeviceCreationCount,
            renderSurfaceSize = gpu.RenderSurfaceSize,
            classification = gpu.IsActive ? "expected" : "environment-or-defect"
        };
        if (!gpu.IsActive)
        {
            return;
        }

        int beforeHide = gpu.PresentedFrameCount;
        gpu.Visible = false;
        Pump(250);
        int afterHide = gpu.PresentedFrameCount;
        results["hidden_gpu"] = new
        {
            beforeHide,
            afterHide,
            framesWhileHidden = afterHide - beforeHide,
            classification = afterHide == beforeHide ? "expected" : "defect"
        };

        Theme invalid = AppearanceThemeConfig.CloneConfig(theme);
        invalid.Rendering!.Parameters["glow"] = JsonSerializer.SerializeToElement(101);
        bool accepted = gpu.UpdateTheme(invalid);
        results["invalid_update"] = new
        {
            accepted,
            gpu.IsActive,
            gpu.Error,
            generation = gpu.CurrentGeneration,
            classification = !accepted ? "expected-rejection" : "defect"
        };

        Theme changedFps = AppearanceThemeConfig.CloneConfig(theme);
        changedFps.Rendering!.Motion["targetFps"] = JsonSerializer.SerializeToElement(2);
        bool fpsUpdateAccepted = gpu.UpdateTheme(changedFps);
        results["fps_update"] = new
        {
            accepted = fpsUpdateAccepted,
            requested = 2,
            actual = gpu.TargetFramesPerSecond,
            error = gpu.Error,
            classification = fpsUpdateAccepted && gpu.TargetFramesPerSecond == 2
                ? "expected"
                : "defect"
        };

        cfg.GpuThemeEffectsEnabled = true;
        cfg.Save();
        Settings.Load(forceReload: true);
        using var parent = new ThemePreviewControl { Size = new Size(320, 440) };
        gpuHost.Controls.Add(parent);
        parent.Controls.Add(gpu);
        typeof(ThemePreviewControl).GetField("_gpuPreview", Private)!.SetValue(parent, gpu);
        parent.SetTheme(theme, ThemeEditTarget.GpuMain);
        results["valid_update_parent"] = new
        {
            parent.IsGpuPreviewActive,
            parent.GpuPreviewError,
            visible = gpu.Visible,
            classification = parent.IsGpuPreviewActive ? "expected" : "defect"
        };

        invalid.Name = "InvalidAudit";
        parent.SetTheme(invalid, ThemeEditTarget.GpuMain);
        results["invalid_update_parent"] = new
        {
            parent.IsGpuPreviewActive,
            parent.GpuPreviewError,
            selectedTheme = parent.RenderedTheme?.Name,
            renderedTheme = gpu.Error is null ? gpu.CurrentGeneration.ToString() : null,
            classification = !parent.IsGpuPreviewActive
                ? "expected-rejection"
                : "defect"
        };
    }

    private static object DescribeFallbackExport(
        ThemeDocumentSession session,
        ThemeImageExportResult exported,
        string pngPath)
    {
        if (!exported.Success || !File.Exists(pngPath))
        {
            return new
            {
                exported.Success,
                exported.Error,
                route = exported.Route.ToString(),
                classification = "defect"
            };
        }

        using var image = new Bitmap(pngPath);
        int fallbackPixels = 0;
        int mainPixels = 0;
        int sampled = 0;
        string fallbackBackgroundText =
            session.Draft.Rendering?.FallbackStyle?.Background ??
            LegacyFallbackStyleResolver.DefaultBackground;
        Color fallback = ParseRgb(fallbackBackgroundText);
        Color main = ParseRgb(session.Draft.Color.Background);
        for (int y = 8; y < image.Height - 20; y += 4)
        {
            for (int x = 8; x < image.Width - 8; x += 4)
            {
                sampled++;
                Color pixel = image.GetPixel(x, y);
                if (DistanceSquared(pixel, fallback) <= 20 * 20) fallbackPixels++;
                if (DistanceSquared(pixel, main) <= 20 * 20) mainPixels++;
            }
        }

        return new
        {
            exported.Success,
            route = exported.Route.ToString(),
            width = image.Width,
            height = image.Height,
            sampled,
            fallbackPixels,
            mainPixels,
            rawBackground = session.Draft.Color.Background,
            fallbackBackground = fallbackBackgroundText,
            sha256 = Sha256(pngPath),
            classification = exported.Route == ThemeExportImageRoute.Fallback &&
                image.Width == ThemeExportService.VerticalImageWidth &&
                image.Height == ThemeExportService.VerticalImageHeight &&
                fallbackPixels > mainPixels
                ? "expected"
                : "defect"
        };
    }

    private static object DescribeHorizontalExport(
        ThemeImageExportResult exported,
        string pngPath)
    {
        Size actual = Size.Empty;
        string? sha256 = null;
        if (File.Exists(pngPath))
        {
            using var image = new Bitmap(pngPath);
            actual = image.Size;
            sha256 = Sha256(pngPath);
        }

        var expected = new Size(
            ThemeExportService.HorizontalImageWidth,
            ThemeExportService.HorizontalImageHeight);
        bool safeGate = !exported.Success ||
            (exported.Route == ThemeExportImageRoute.Gpu && actual == expected);
        bool capabilityGate = exported.Success &&
            exported.Route == ThemeExportImageRoute.Gpu &&
            actual == expected;
        return new
        {
            exported.Success,
            exported.Error,
            route = exported.Route.ToString(),
            actual,
            expected,
            sha256,
            diagnostics = exported.Diagnostics,
            safetyGate = safeGate ? "pass" : "fail",
            capabilityGate = capabilityGate
                ? "pass"
                : "not-proven",
            classification = !safeGate
                ? "defect"
                : capabilityGate
                    ? "expected"
                    : "not-proven"
        };
    }

    private static Dictionary<string, object?> BuildClassificationSummary(
        IReadOnlyDictionary<string, object?> results)
    {
        static string Classify(object? value)
        {
            if (value is null) return "missing";
            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(value));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("classification", out JsonElement item))
            {
                return "reported";
            }

            return item.GetString() ?? "unknown";
        }

        return results.ToDictionary(
            pair => pair.Key,
            pair => (object?)Classify(pair.Value),
            StringComparer.Ordinal);
    }

    private static string CreateUniqueRunDirectory(string parent)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            string name =
                "run-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") +
                "-" + Guid.NewGuid().ToString("N");
            string candidate = Path.Combine(parent, name);
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
        }

        throw new IOException("Could not allocate a unique probe run directory.");
    }

    private static string? ReadPersistedSkin()
    {
        try
        {
            using JsonDocument document =
                JsonDocument.Parse(File.ReadAllText(SettingsHelper.FilePath));
            return document.RootElement.TryGetProperty("Skin", out JsonElement skin)
                ? skin.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteRunMetadata(
        string output,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        int exitCode)
    {
        string? repoRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        if (repoRoot is null)
        {
            throw new InvalidOperationException(
                "Unable to locate the repository root for Git index isolation.");
        }

        string indexPath = ResolveGitIndexPath(repoRoot);
        if (!File.Exists(indexPath))
        {
            throw new FileNotFoundException(
                "The real Git index could not be resolved.",
                indexPath);
        }

        string indexSha256Before = Sha256(indexPath);
        string tempIndexPath = Path.Combine(
            Path.GetTempPath(),
            "litemonitor-probe-index-" + Guid.NewGuid().ToString("N") + ".tmp");
        string assemblyPath = typeof(AuditProbe).Assembly.Location;

        try
        {
            File.Copy(indexPath, tempIndexPath, overwrite: false);
            string tempIndexSha256 = Sha256(tempIndexPath);
            if (!string.Equals(
                    indexSha256Before,
                    tempIndexSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The temporary Git index does not match the real index.");
            }

            IReadOnlyDictionary<string, string?> isolatedEnvironment =
                CreateGitEnvironment(tempIndexPath);
            string head = RunGitRequired(
                repoRoot,
                "rev-parse HEAD",
                CreateGitEnvironment(indexPath: null));
            string status = RunGitRequired(
                repoRoot,
                "--no-optional-locks status --porcelain=v2 --untracked-files=all",
                isolatedEnvironment);
            string indexTree = RunGitRequired(
                repoRoot,
                "write-tree",
                isolatedEnvironment);

            string indexSha256After = Sha256(indexPath);
            if (!string.Equals(
                    indexSha256Before,
                    indexSha256After,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The real Git index changed while metadata was collected.");
            }

            var metadata = new
            {
                schema = 2,
                startedAtUtc = startedAt,
                endedAtUtc = endedAt,
                exitCode,
                outputDirectory = output,
                processArchitecture =
                    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                osDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                dpi = GetDpiForSystemSafe(),
                assembly = new
                {
                    path = assemblyPath,
                    sha256 = File.Exists(assemblyPath) ? Sha256(assemblyPath) : null
                },
                git = new
                {
                    head,
                    indexTree,
                    indexTreeSource = "temporary-index-copy",
                    statusSha256 = Sha256Text(status),
                    indexSha256Before,
                    indexSha256After,
                    indexUnchanged = true
                }
            };
            File.WriteAllText(
                Path.Combine(output, "run-metadata.json"),
                JsonSerializer.Serialize(
                    metadata,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            try
            {
                if (File.Exists(tempIndexPath)) File.Delete(tempIndexPath);
            }
            catch
            {
                // The temp copy is identifiable and contains no user output.
            }
        }
    }

    private static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string ResolveGitIndexPath(string repoRoot)
    {
        string path = RunGitRequired(
            repoRoot,
            "rev-parse --git-path index",
            CreateGitEnvironment(indexPath: null));
        return Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(repoRoot, path));
    }

    private static IReadOnlyDictionary<string, string?> CreateGitEnvironment(
        string? indexPath) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_INDEX_FILE"] = indexPath,
            ["GIT_OPTIONAL_LOCKS"] = "0"
        };

    private static string RunGitRequired(
        string repoRoot,
        string arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach ((string key, string? value) in environment)
        {
            if (value is null) startInfo.Environment.Remove(key);
            else startInfo.Environment[key] = value;
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start git.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string output = stdout.GetAwaiter().GetResult();
        string error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {arguments} failed with exit {process.ExitCode}: {error.Trim()}");
        }

        return output.Trim();
    }

    private static uint GetDpiForSystemSafe()
    {
        try
        {
            using Graphics graphics = Graphics.FromHwnd(IntPtr.Zero);
            return (uint)Math.Round(graphics.DpiX);
        }
        catch
        {
            return 0;
        }
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Sha256Text(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private static Color ParseRgb(string html)
    {
        Color color = ColorTranslator.FromHtml(html);
        return Color.FromArgb(color.R, color.G, color.B);
    }

    private static int DistanceSquared(Color left, Color right)
    {
        int dr = left.R - right.R;
        int dg = left.G - right.G;
        int db = left.B - right.B;
        return dr * dr + dg * dg + db * db;
    }

    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }
}
