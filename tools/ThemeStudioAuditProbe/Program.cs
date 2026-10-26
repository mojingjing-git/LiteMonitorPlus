using System.Reflection;
using System.Text.Json;
using System.Diagnostics;
using LiteMonitor;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.SettingsPage;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.ThemeEditor;

internal static class AuditProbe
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(object value, string name) =>
        (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    static void Pump(int ms)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        string output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        string settingsDir = Path.Combine(output, "settings");
        string themesDir = Path.Combine(output, "themes");
        Directory.CreateDirectory(settingsDir);
        Directory.CreateDirectory(themesDir);
        SettingsHelper.PathOverride = settingsDir;
        ThemeDocumentStore.SetThemeDirForTests(themesDir);
        ThemeDocumentStore.SetRecoveryDirForTests(Path.Combine(output, "drafts"));
        var cfg = new Settings { GpuThemeEffectsEnabled = false, SettingThemeMode = SettingThemeMode.Dark };
        cfg.Save();
        SettingsThemeManager.Initialize(SettingThemeMode.Dark);
        var results = new Dictionary<string, object?>();
        var theme = new Theme
        {
            Name = "AuditGpu",
            Color = new ColorConfig { Background = "#112233", TextPrimary = "#FFFFFF" },
            Rendering = new ThemeRenderingConfig
            {
                Backend = "gpu",
                Material = "cyber_punk",
                FallbackStyle = new ThemeFallbackStyle
                {
                    Background = "#AABBCC", Text = "#111111", Accent = "#FF0000"
                }
            }
        };
        theme.BuildFonts();
        File.WriteAllText(Path.Combine(themesDir, "AuditGpu.json"),
            JsonSerializer.Serialize(theme, ThemeManager.CreateSerializerOptions()));
        using (var form = new ThemeStudioForm(appSettings: cfg))
        {
            form.Show();
            form.OpenThemeForTesting("AuditGpu");
            Pump(50);
            var preview = form.PreviewControlForTesting!;
            string before = Field<Theme>(preview, "_theme").Color.Background;
            Field<ToolStripComboBox>(form, "_targetCombo").SelectedIndex = 1;
            Pump(200);
            string after = Field<Theme>(preview, "_theme").Color.Background;
            results["target_switch"] = new
            {
                inspector = form.InspectorForTesting.Target.ToString(),
                previewTarget = Field<ThemeEditTarget>(preview, "_target").ToString(),
                before, after, expected = "#AABBCC",
                status = form.RouteStatusTextForTesting
            };
            Field<ListBox>(form, "_libraryList").Focus();
            bool focusReported = form.SelectPropertyByRole(ThemePropertyCatalog.SemanticElementRole.Title);
            results["focus_property"] = new
            {
                focusReported,
                inspectorHasFocus = form.InspectorForTesting.ContainsFocus,
                activeControl = form.ActiveControl?.GetType().Name
            };
            string statusBefore = form.SavedStatusTextForTesting;
            typeof(Control).GetMethod("OnMouseDown", Private)!.Invoke(preview,
                [new MouseEventArgs(MouseButtons.Left, 1, 60, 70, 0)]);
            results["preview_click"] = new
            {
                statusBefore,
                statusAfter = form.SavedStatusTextForTesting,
                inspectorHasFocus = form.InspectorForTesting.ContainsFocus
            };
            form.SelectTargetForTesting(ThemeEditTarget.GpuMain);
            results["gpu_background_selection"] = new
            {
                mapped = ThemePropertyCatalog.ResolvePropertyPath(ThemePropertyCatalog.SemanticElementRole.PanelBackground, ThemeEditTarget.GpuMain),
                focused = form.SelectPropertyByRole(ThemePropertyCatalog.SemanticElementRole.PanelBackground)
            };
            results["dark_mode"] = new
            {
                selected = SettingsThemeManager.EffectiveMode.ToString(),
                paletteIsDark = SettingsThemeManager.CurrentPalette.IsDark,
                actualBack = form.BackColor.ToArgb().ToString("X8"),
                actualInspectorBack = form.InspectorForTesting.BackColor.ToArgb().ToString("X8")
            };
            form.Close();
        }
        Settings.GlobalBlockSave = true;
        Type gatewayType = typeof(ThemeStudioLauncher).GetNestedType("AppActionsThemeApplyGateway", BindingFlags.NonPublic)!;
        var gateway = (ThemeStudioForm.IThemeApplyGateway)Activator.CreateInstance(gatewayType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [cfg, null!, null!], null)!;
        var applyResult = gateway.ApplySavedTheme("AuditGpu", 7);
        results["blocked_save"] = new
        {
            result = applyResult,
            actualStage = applyResult.Stage.ToString(),
            persistedSkin = JsonDocument.Parse(File.ReadAllText(SettingsHelper.FilePath)).RootElement.GetProperty("Skin").GetString(),
            memorySkin = cfg.Skin,
            note = "No UI runtime supplied: advancing to runtime after blocked persistence is the observed defect."
        };
        Settings.GlobalBlockSave = false;
        {
            ThemeDocumentSession session = ThemeDocumentSession.Open("AuditGpu");
            string png = Path.Combine(output, "fallback-export.png");
            var exported = ThemeExportService.ExportEffectImage(session, png, ImageRoutePreference.Classic);
            if (exported.Success)
            {
                using var image = new Bitmap(png);
                results["fallback_export"] = new
                {
                    exported.Success, route = exported.Route.ToString(),
                    bgPixel = image.GetPixel(120, 3).ToArgb().ToString("X8"),
                    rawBackground = session.Draft.Color.Background,
                    fallbackBackground = session.Draft.Rendering!.FallbackStyle!.Background,
                    width = image.Width, height = image.Height
                };
            }
            string horizontalPng = Path.Combine(output, "gpu-horizontal-export.png");
            var exportedHorizontal = ThemeExportService.ExportEffectImage(
                session, horizontalPng, ImageRoutePreference.Gpu, ThemeExportImageOrientation.Horizontal);
            Size horizontalSize = Size.Empty;
            if (File.Exists(horizontalPng))
            {
                using var capturedHorizontal = new Bitmap(horizontalPng);
                horizontalSize = capturedHorizontal.Size;
            }
            results["gpu_horizontal_export"] = new
            {
                exportedHorizontal.Success,
                exportedHorizontal.Error,
                route = exportedHorizontal.Route.ToString(),
                actual = horizontalSize,
                expected = new Size(400, 120)
            };
            session.Draft.DisposeFonts();
        }
        try
        {
            using var gpuHost = new Form { ClientSize = new Size(400, 500) };
            using var gpu = new GpuThemePreviewControl(theme, ThemePreviewControl.CreateMockGroups(),
                new GpuStartupGuard(Path.Combine(output, "gpu-guard"), AppContext.BaseDirectory))
            {
                Size = new Size(300, 420)
            };
            gpuHost.Controls.Add(gpu);
            gpuHost.Show();
            Pump(200);
            results["gpu_start"] = new { gpu.IsActive, gpu.Error, gpu.DeviceCreationCount };
            if (gpu.IsActive)
            {
                var renderer = Field<object>(gpu, "_renderer");
                int beforeHide = Field<int>(renderer, "_presentedFrames");
                gpu.Visible = false;
                Pump(250);
                int afterHide = Field<int>(renderer, "_presentedFrames");
                results["hidden_gpu"] = new { beforeHide, afterHide, framesWhileHidden = afterHide - beforeHide };
                Theme invalid = AppearanceThemeConfig.CloneConfig(theme);
                invalid.Rendering!.Parameters["glow"] = JsonSerializer.SerializeToElement(101);
                bool accepted = gpu.UpdateTheme(invalid);
                results["invalid_update"] = new { accepted, gpu.IsActive, gpu.Error, generation = gpu.CurrentGeneration };
                Theme changedFps = AppearanceThemeConfig.CloneConfig(theme);
                changedFps.Rendering!.Motion["targetFps"] = JsonSerializer.SerializeToElement(2);
                gpu.UpdateTheme(changedFps);
                object scheduler = Field<object>(gpu, "_scheduler");
                results["fps_update"] = new
                {
                    requested = 2,
                    actual = scheduler.GetType().GetProperty("TargetFramesPerSecond")!.GetValue(scheduler)
                };
                cfg.GpuThemeEffectsEnabled = true;
                cfg.Save();
                Settings.Load(forceReload: true);
                using var parent = new ThemePreviewControl { Size = new Size(320, 440) };
                gpuHost.Controls.Add(parent);
                parent.Controls.Add(gpu);
                typeof(ThemePreviewControl).GetField("_gpuPreview", Private)!.SetValue(parent, gpu);
                parent.SetTheme(theme, ThemeEditTarget.GpuMain);
                results["valid_update_parent"] = new { parent.IsGpuPreviewActive, parent.GpuPreviewError, visible = gpu.Visible };
                invalid.Name = "InvalidAudit";
                parent.SetTheme(invalid, ThemeEditTarget.GpuMain);
                results["invalid_update_parent"] = new
                {
                    parent.IsGpuPreviewActive, parent.GpuPreviewError,
                    selectedTheme = Field<Theme>(parent, "_rawTheme").Name,
                    renderedTheme = Field<Theme>(gpu, "_theme").Name
                };
            }
            gpuHost.Close();
        }
        catch (Exception ex) { results["gpu_probe_exception"] = ex.ToString(); }
        string report = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "observations.json"), report);
        Console.WriteLine(report);
        theme.DisposeFonts();
    }
}
