using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Presentation;
using LiteMonitor.src.Core;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class FlagshipGpuRenderingTests : IDisposable
{
    private static readonly string[] Themes =
    [
        "CosmicAurora_Nebula_GPU",
        "NixieTube_Brass_GPU",
        "RetroCRT_Phosphor_GPU"
    ];

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "litemonitor-flagship-gpu",
        Guid.NewGuid().ToString("N"));

    public FlagshipGpuRenderingTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public static TheoryData<string> StudioGpuThemes => new()
    {
        "CosmicAurora_Nebula_GPU",
        "CyberPunk_GPU",
        "LiquidGlass_GPU",
        "LiquidGlass_Toolbar_GPU",
        "NixieTube_Brass_GPU",
        "RetroCRT_Phosphor_GPU"
    };

    [Theory]
    [MemberData(nameof(StudioGpuThemes))]
    public void 工作室预览诊断_快照与刷新耗时(string themeName)
    {
        // 诊断探针：导出工作室 GPU 预览的实际渲染帧（供人工比对"预览不对"），
        // 并测量连续 RefreshPreviewNow 的耗时分布（模拟连续编辑的防抖触发）。
        string diagDir = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "TestResults", "studio-preview-diag");
        Directory.CreateDirectory(diagDir);
        string capturePath = Path.Combine(diagDir, "studio-" + themeName + "-preview.png");

        RunSta(() =>
        {
            var phases = new List<string>();
            var phaseWatch = System.Diagnostics.Stopwatch.StartNew();
            string diagPath = Path.Combine(diagDir, themeName + "-diag.txt");

            // 每个阶段即时落盘：STA 线程挂死时，文件里最后一个阶段就是卡点。
            void Report(string stage)
            {
                phases.Add($"{stage}={phaseWatch.ElapsedMilliseconds}ms");
                File.WriteAllText(diagPath, string.Join(Environment.NewLine, phases));
                phaseWatch.Restart();
            }

            // 恢复草稿目录必须隔离：真实用户目录的遗留 recovery 会让 OpenTheme
            // 弹出无人应答的模态对话框，把 STA 探针挂死。
            string sandboxRecovery = Path.Combine(diagDir, "recovery");
            Directory.CreateDirectory(sandboxRecovery);
            ThemeDocumentStore.SetRecoveryDirForTests(sandboxRecovery);

            using var form = new ThemeStudioForm();
            form.Show();
            Report("form-shown");
            form.OpenThemeForTesting(themeName);
            Report("opened");
            form.SelectTargetForTesting(ThemeEditTarget.GpuMain);
            Application.DoEvents();
            Report("target-selected");

            for (int i = 0; i < 100 &&
                form.PreviewControlForTesting?.IsGpuPreviewActive != true; i++)
            {
                Thread.Sleep(20);
                Application.DoEvents();
            }
            Report("activate-wait");

            // 等面板适配尺寸收敛（fit 迭代 + 延迟修正）：连续两轮 Bounds 相同才继续，
            // 否则快照会落在中间态尺寸上。
            ThemePreviewControl? fitShell = form.PreviewControlForTesting;
            for (int i = 0; i < 100 && fitShell is not null; i++)
            {
                Rectangle before = fitShell.Bounds;
                Thread.Sleep(30);
                Application.DoEvents();
                if (before == fitShell.Bounds && i > 0)
                {
                    break;
                }
            }
            Report("fit-settled");

            string beforeCapture =
                $"client={fitShell?.ClientSize} bounds={fitShell?.Bounds} " +
                $"surface={form.PreviewControlForTesting?.PreviewDiagnosticsForTesting}";
            bool captured = form.PreviewCaptureForTesting(capturePath);
            Report($"capture={captured} {beforeCapture}");
            ThemePreviewControl? shell = form.PreviewControlForTesting;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 20; i++)
            {
                form.RefreshPreviewForTesting();
                Application.DoEvents();
            }
            stopwatch.Stop();
            Report("20refresh");

            // 兼容外观目标（GpuFallback）走 GDI 绘制——验证"上半部分吞一点"。
            form.SelectTargetForTesting(ThemeEditTarget.GpuFallback);
            Application.DoEvents();
            Thread.Sleep(200);
            Application.DoEvents();
            string fallbackPath = Path.Combine(diagDir, "studio-" + themeName + "-fallback.png");
            using (Bitmap fallbackShot = new Bitmap(
                form.PreviewControlForTesting!.ClientSize.Width,
                form.PreviewControlForTesting.ClientSize.Height))
            {
                form.PreviewControlForTesting.DrawToBitmap(fallbackShot,
                    new Rectangle(Point.Empty, fallbackShot.Size));
                fallbackShot.Save(fallbackPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            Report("fallback-shot");

            phases.Add(shell?.PreviewDiagnosticsForTesting ?? "<no shell>");
            phases.Add(shell is null ? "<no shell bounds>" :
                $"previewBounds={shell.Bounds} hostClient={shell.Parent?.ClientSize}");
            phases.Add($"20refresh-total={stopwatch.ElapsedMilliseconds}ms");
            File.WriteAllText(diagPath, string.Join(Environment.NewLine, phases));
            ThemeDocumentStore.ResetRecoveryDir();
            Assert.True(captured,
                $"GPU 预览快照失败: {shell?.PreviewDiagnosticsForTesting ?? "<no shell>"}");
            Assert.True(
                stopwatch.ElapsedMilliseconds < 2000,
                $"20 次预览刷新耗时 {stopwatch.ElapsedMilliseconds}ms（每次 {stopwatch.ElapsedMilliseconds / 20.0:F1}ms）");
        });
    }

    [Fact]
    public void StrictGpuExportsUseDistinctThemeSpecificPixels()
    {
        RunSta(() =>
        {
            var hashes = new List<string>();
            foreach (string themeName in Themes)
            {
                string path = Path.Combine(_tempDir, themeName + ".png");
                ThemeDocumentSession session = ThemeDocumentSession.Open(themeName);
                try
                {
                    ThemeImageExportResult result = ThemeExportService.ExportEffectImage(
                        session,
                        path,
                        ImageRoutePreference.Gpu,
                        ThemeExportImageFormFactor.Vertical);
                    Assert.True(result.Success, $"{themeName}: {result.Error}");
                    Assert.Equal(ThemeExportImageRoute.Gpu, result.Route);
                    Assert.True(File.Exists(path));

                    using var bitmap = new Bitmap(path);
                    Assert.Equal(240, bitmap.Width);
                    Assert.Equal(420, bitmap.Height);
                    string decodedHash = DecodedArgbHash(bitmap);
                    hashes.Add(decodedHash);
                    (double red, double green, double blue) = MeanRgb(bitmap);
                    AssertThemeColorSignature(themeName, red, green, blue);
                    AssertGpuStructure(bitmap, themeName);

                    string repeatPath = Path.Combine(_tempDir, themeName + "-repeat.png");
                    ThemeImageExportResult repeat = ThemeExportService.ExportEffectImage(
                        session,
                        repeatPath,
                        ImageRoutePreference.Gpu,
                        ThemeExportImageFormFactor.Vertical);
                    Assert.True(repeat.Success, $"{themeName} repeat: {repeat.Error}");
                    Assert.Equal(ThemeExportImageRoute.Gpu, repeat.Route);
                    using var repeatBitmap = new Bitmap(repeatPath);
                    Assert.Equal(decodedHash, DecodedArgbHash(repeatBitmap));
                }
                finally
                {
                    session.Draft.DisposeFonts();
                }
            }

            Assert.Equal(Themes.Length, hashes.Distinct(StringComparer.Ordinal).Count());
        });
    }

    [Theory]
    [InlineData("CosmicAurora_Nebula_GPU")]
    [InlineData("NixieTube_Brass_GPU")]
    [InlineData("RetroCRT_Phosphor_GPU")]
    public void CpuFallbackExportsMapThemePaletteAndBorderFamily(string themeName)
    {
        RunSta(() =>
        {
            string path = Path.Combine(_tempDir, "fallback-" + themeName + ".png");
            ThemeDocumentSession session = ThemeDocumentSession.Open(themeName);
            try
            {
                ThemeImageExportResult result = ThemeExportService.ExportEffectImage(
                    session,
                    path,
                    ImageRoutePreference.Classic,
                    ThemeExportImageFormFactor.Vertical);

                Assert.True(result.Success, $"{themeName}: {result.Error}");
                Assert.Equal(ThemeExportImageRoute.Fallback, result.Route);
                using var bitmap = new Bitmap(path);
                string firstHash = DecodedArgbHash(bitmap);
                AssertFallbackStructure(bitmap, session.Draft);

                string repeatPath = Path.Combine(_tempDir, "fallback-" + themeName + "-repeat.png");
                ThemeImageExportResult repeat = ThemeExportService.ExportEffectImage(
                    session,
                    repeatPath,
                    ImageRoutePreference.Classic,
                    ThemeExportImageFormFactor.Vertical);
                Assert.True(repeat.Success, $"{themeName} repeat: {repeat.Error}");
                using var repeatBitmap = new Bitmap(repeatPath);
                Assert.Equal(firstHash, DecodedArgbHash(repeatBitmap));
            }
            finally
            {
                session.Draft.DisposeFonts();
            }
        });
    }

    [Fact]
    public void FixedPresetRenderingCreatesNoCaptureSessions()
    {
        RunSta(() =>
        {
            foreach (string themeName in Themes)
            {
                ThemeDocumentSession session = ThemeDocumentSession.Open(themeName);
                try
                {
                    string guardDir = Path.Combine(_tempDir, "guard-" + themeName);
                    string path = Path.Combine(_tempDir, "direct-" + themeName + ".png");
                    using var owner = new Form
                    {
                        ShowInTaskbar = false,
                        StartPosition = FormStartPosition.Manual,
                        Location = new Point(-30000, -30000),
                        Size = new Size(280, 480),
                        FormBorderStyle = FormBorderStyle.None,
                        Opacity = 0d
                    };
                    var guard = new GpuStartupGuard(guardDir, AppContext.BaseDirectory);
                    var host = new GpuThemePreviewControl(
                        session.Draft,
                        ThemePreviewControl.CreateMockGroups(),
                        guard)
                    {
                        Bounds = new Rectangle(10, 10, 240, 420)
                    };
                    owner.Controls.Add(host);
                    _ = owner.Handle;
                    _ = host.Handle;

                    Assert.True(host.TryCaptureOnce(path), $"{themeName}: {host.Error}");
                    Assert.Equal(0, host.CaptureSessionsCreated);
                    Assert.True(new FileInfo(path).Length > 0);
                }
                finally
                {
                    session.Draft.DisposeFonts();
                }
            }
        });
    }

    [Fact]
    public void NamedPresetsCapHostCaptureRequestToFalse()
    {
        RunSta(() =>
        {
            foreach (string themeName in Themes)
            {
                ThemeDocumentSession session = ThemeDocumentSession.Open(themeName);
                try
                {
                    using var form = new Form();
                    MaterialKind material =
                        session.Draft.Rendering!.Material ==
                        ThemeRenderingConfig.LiquidGlassMaterial
                            ? MaterialKind.LiquidGlass
                            : MaterialKind.CyberPunk;
                    using var host = new GpuPresentationHostController(
                        form,
                        material,
                        liveBackgroundRequested: true,
                        renderingConfig: session.Draft.Rendering,
                        theme: session.Draft);

                    Assert.False(host.RequestsLiveBackground);
                }
                finally
                {
                    session.Draft.DisposeFonts();
                }
            }
        });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Temporary export files are best-effort test cleanup.
        }
    }

    private static (double Red, double Green, double Blue) MeanRgb(Bitmap bitmap)
    {
        return MeanRgb(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
    }

    private static (double Red, double Green, double Blue) MeanRgb(
        Bitmap bitmap,
        Rectangle bounds)
    {
        double red = 0;
        double green = 0;
        double blue = 0;
        long count = 0;
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                Color color = bitmap.GetPixel(x, y);
                red += color.R;
                green += color.G;
                blue += color.B;
                count++;
            }
        }

        return (red / count, green / count, blue / count);
    }

    private static void AssertThemeColorSignature(
        string themeName,
        double red,
        double green,
        double blue)
    {
        switch (themeName)
        {
            case "CosmicAurora_Nebula_GPU":
                Assert.True(
                    blue > green * 1.15 && green > red * 1.4,
                    $"Cosmic mean RGB was ({red:F2}, {green:F2}, {blue:F2}).");
                break;
            case "NixieTube_Brass_GPU":
                Assert.True(
                    red > green * 1.2 && red > blue * 1.8,
                    $"Nixie mean RGB was ({red:F2}, {green:F2}, {blue:F2}).");
                break;
            case "RetroCRT_Phosphor_GPU":
                Assert.True(
                    green > red * 1.5 && green > blue * 1.5,
                    $"CRT mean RGB was ({red:F2}, {green:F2}, {blue:F2}).");
                break;
            default:
                Assert.Fail($"Unexpected theme: {themeName}");
                break;
        }
    }

    private static void AssertGpuStructure(Bitmap bitmap, string themeName)
    {
        switch (themeName)
        {
            case "CosmicAurora_Nebula_GPU":
                AssertCosmicStructure(bitmap);
                break;
            case "NixieTube_Brass_GPU":
                AssertNixieStructure(bitmap);
                break;
            case "RetroCRT_Phosphor_GPU":
                AssertCrtStructure(bitmap);
                break;
            default:
                Assert.Fail($"Unexpected theme: {themeName}");
                break;
        }
    }

    private static void AssertCosmicStructure(Bitmap bitmap)
    {
        var bands = new Rectangle[]
        {
            new(8, 50, 72, 360),
            new(80, 50, 80, 360),
            new(160, 50, 72, 360)
        };
        foreach (Rectangle band in bands)
        {
            Assert.True(
                CountPixels(bitmap, band, color =>
                    color.B > 50 && color.G > 40 &&
                    color.B > color.R * 1.45 && color.G > color.R * 1.25) >= 4_000,
                $"Cosmic cyan aurora band was missing in {band}.");
            Assert.True(
                CountPixels(bitmap, band, color =>
                    color.B > 45 && color.B > color.R * 1.30 && color.B > color.G * 1.15) >= 10_000,
                $"Cosmic violet aurora band was missing in {band}.");
        }

        int stars = 0;
        var starBand = new Rectangle(16, 300, 208, 13);
        for (int y = starBand.Top; y < starBand.Bottom; y++)
        {
            for (int x = starBand.Left; x < starBand.Right; x++)
            {
                double luminance = Luminance(bitmap.GetPixel(x, y));
                if (luminance <= 45 ||
                    luminance <= MeanLuminance(bitmap, x, y, radius: 3) + 18)
                {
                    continue;
                }

                stars++;
            }
        }

        Assert.True(stars >= 5, $"Cosmic sparse-star field was not present: {stars} local highlights.");
    }

    private static void AssertNixieStructure(Bitmap bitmap)
    {
        int bands = 0;
        bool inBand = false;
        for (int y = 50; y < 390; y++)
        {
            int warmPixels = 0;
            for (int x = 16; x < 224; x++)
            {
                Color color = bitmap.GetPixel(x, y);
                if (IsBrass(color))
                {
                    warmPixels++;
                }
            }

            bool isBand = warmPixels >= 150;
            if (isBand && !inBand)
            {
                bands++;
            }
            inBand = isBand;
        }

        Assert.True(bands >= 3, $"Nixie brass horizontal bands were incomplete: {bands}.");

        (int X, int Y)[] corners = [(15, 15), (224, 15), (15, 405), (224, 405)];
        foreach ((int x, int y) in corners)
        {
            (double red, double green, double blue) = MeanRgb(bitmap, new Rectangle(x - 3, y - 3, 7, 7));
            Assert.True(
                red > green * 1.20 && green > blue * 1.20,
                $"Nixie corner rivet at ({x}, {y}) was not brass-colored: ({red:F1}, {green:F1}, {blue:F1}).");
        }
    }

    private static void AssertCrtStructure(Bitmap bitmap)
    {
        var sums = new double[3];
        var counts = new int[3];
        for (int y = 120; y < 310; y++)
        {
            int phase = y % 3;
            for (int x = 205; x < 220; x++)
            {
                sums[phase] += Luminance(bitmap.GetPixel(x, y));
                counts[phase]++;
            }
        }

        double[] means = sums.Select((sum, index) => sum / counts[index]).ToArray();
        double max = means.Max();
        double min = means.Min();
        Assert.True(
            (max - min) / max >= 0.08,
            $"CRT 3-pixel scanline contrast was too weak: [{string.Join(", ", means.Select(x => x.ToString("F2")))}].");

        double center = MeanLuminance(bitmap, new Rectangle(80, 160, 80, 100));
        double corners = new[]
        {
            new Rectangle(8, 8, 20, 20),
            new Rectangle(212, 8, 20, 20),
            new Rectangle(8, 392, 20, 20),
            new Rectangle(212, 392, 20, 20)
        }.Average(rect => MeanLuminance(bitmap, rect));
        // 实测基线约 1.12（辉光溢出与抗锯齿受驱动/DWM 波动）——阈值 1.10 保留
        // "中心荧光亮于四角边框"的结构要求，同时给环境波动留余量。
        Assert.True(center >= corners * 1.10, $"CRT center/corner luminance difference was too weak: {center:F2}/{corners:F2}.");
    }

    private static void AssertFallbackStructure(Bitmap bitmap, Theme theme)
    {
        ThemeFallbackStyle fallback = theme.Rendering?.FallbackStyle
            ?? throw new Xunit.Sdk.XunitException("The GPU theme did not define fallbackStyle.");
        Color background = ThemeManager.ParseColor(fallback.Background);
        Color panel = ThemeManager.ParseColor(fallback.PanelBackground);
        Color text = ThemeManager.ParseColor(fallback.Text);
        Color accent = ThemeManager.ParseColor(fallback.Accent);
        Color warning = ThemeManager.ParseColor(theme.Color.ValueWarn);
        Color danger = ThemeManager.ParseColor(theme.Color.ValueCrit);

        var full = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        Assert.True(CountNear(bitmap, full, background, tolerance: 10) >= 1_000,
            "The CPU fallback background did not use the declared background family.");
        Assert.True(CountNear(bitmap, full, panel, tolerance: 2) >= 5_000,
            "The CPU fallback panels did not use rendering.fallbackStyle.panelBackground.");
        Assert.True(CountNear(bitmap, full, text, tolerance: 2) >= 500,
            "The CPU fallback body text did not use rendering.fallbackStyle.text.");
        Assert.True(CountNear(bitmap, full, accent, tolerance: 2) >= 100,
            "The CPU fallback accent did not use rendering.fallbackStyle.accent.");
        Assert.True(CountNear(bitmap, full, warning, tolerance: 2) >= 100,
            "The CPU fallback warning state did not use the theme valueWarn color.");
        Assert.True(CountNear(bitmap, full, danger, tolerance: 2) >= 100,
            "The CPU fallback critical state did not use the theme valueCrit color.");

        Color borderProbe = bitmap.GetPixel(bitmap.Width / 2, 48);
        bool borderFamily = fallback.Border switch
        {
            "#5EE2FF" => borderProbe.B > borderProbe.R * 1.8 &&
                borderProbe.B > borderProbe.G * 1.5,
            "#8A6538" => borderProbe.R > borderProbe.G * 1.2 &&
                borderProbe.G > borderProbe.B * 1.2,
            "#1A5E24" => borderProbe.G > borderProbe.R * 2.0 &&
                borderProbe.G > borderProbe.B * 2.0,
            _ => false
        };
        Assert.True(borderFamily, $"CPU fallback border family did not match {fallback.Border}: {borderProbe}.");
    }

    private static bool IsBrass(Color color) =>
        color.R > 70 &&
        color.R > color.G * 1.15 &&
        color.G > color.B * 1.15;

    private static int CountPixels(Bitmap bitmap, Rectangle bounds, Func<Color, bool> predicate)
    {
        int count = 0;
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                if (predicate(bitmap.GetPixel(x, y)))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int CountNear(Bitmap bitmap, Rectangle bounds, Color expected, int tolerance)
    {
        return CountPixels(bitmap, bounds, color =>
            Math.Abs(color.R - expected.R) <= tolerance &&
            Math.Abs(color.G - expected.G) <= tolerance &&
            Math.Abs(color.B - expected.B) <= tolerance);
    }

    private static double MeanLuminance(Bitmap bitmap, Rectangle bounds)
    {
        (double red, double green, double blue) = MeanRgb(bitmap, bounds);
        return (red + green + blue) / 3d;
    }

    private static double MeanLuminance(Bitmap bitmap, int x, int y, int radius)
    {
        int left = Math.Max(0, x - radius);
        int top = Math.Max(0, y - radius);
        int right = Math.Min(bitmap.Width, x + radius + 1);
        int bottom = Math.Min(bitmap.Height, y + radius + 1);
        return MeanLuminance(bitmap, Rectangle.FromLTRB(left, top, right, bottom));
    }

    private static double Luminance(Color color) => (color.R + color.G + color.B) / 3d;

    private static string DecodedArgbHash(Bitmap bitmap)
    {
        using var normalized = new Bitmap(
            bitmap.Width,
            bitmap.Height,
            PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(normalized))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImageUnscaled(bitmap, 0, 0);
        }

        Rectangle bounds = new(0, 0, normalized.Width, normalized.Height);
        BitmapData data = normalized.LockBits(
            bounds,
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = checked(normalized.Width * 4);
            byte[] pixels = new byte[checked(rowBytes * normalized.Height)];
            for (int y = 0; y < normalized.Height; y++)
            {
                nint source = data.Scan0 + y * data.Stride;
                Marshal.Copy(source, pixels, y * rowBytes, rowBytes);
            }

            return Convert.ToHexString(SHA256.HashData(pixels));
        }
        finally
        {
            normalized.UnlockBits(data);
        }
    }

    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                action();
            }
            catch (Exception exception)
            {
                captured = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(
            thread.Join(TimeSpan.FromMinutes(2)),
            "STA test thread did not terminate within two minutes.");

        if (captured is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }
}
