using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// Deterministic export contracts that do not require a real GPU session.
    /// Real D3D11 route and frame behavior remains covered by Tier 4 tests.
    /// </summary>
    public sealed class ThemeExportContractTests : IDisposable
    {
        private const string GpuThemeJson = """
        {
          "version": 3,
          "name": "ContractGpu",
          "color": {
            "background": "#112233",
            "groupBackground": "#223344",
            "border": "#334455",
            "textPrimary": "#FFFFFF",
            "textSecondary": "#CCCCCC",
            "textTitle": "#EEEEEE"
          },
          "rendering": {
            "version": 1,
            "backend": "gpu",
            "material": "cyber_punk",
            "fallbackStyle": {
              "background": "#AABBCC",
              "panelBackground": "#BBCCDD",
              "border": "#778899",
              "text": "#111111",
              "mutedText": "#333333",
              "accent": "#FF0000"
            },
            "parameters": {
              "liveBackground": false,
              "cornerRadius": 16
            },
            "motion": {
              "targetFps": 30,
              "motionEnabled": false
            }
          }
        }
        """;

        private readonly string _root;

        public ThemeExportContractTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "litemonitor-export-contract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public void EffectExport_AllFormFactors_HaveExactRequestedDimensions()
        {
            RunSta(() =>
            {
                ThemeDocumentSession session = OpenSession();
                try
                {
                    AssertExportSize(
                        session,
                        ThemeExportImageFormFactor.Vertical,
                        ThemeExportService.VerticalImageWidth,
                        ThemeExportService.VerticalImageHeight);
                    AssertExportSize(
                        session,
                        ThemeExportImageFormFactor.Horizontal,
                        ThemeExportService.HorizontalImageWidth,
                        ThemeExportService.HorizontalImageHeight);
                    AssertExportSize(
                        session,
                        ThemeExportImageFormFactor.Taskbar,
                        ThemeExportService.TaskbarImageWidth,
                        ThemeExportService.TaskbarImageHeight);
                }
                finally
                {
                    session.Draft.DisposeFonts();
                }
            });
        }

        [Fact]
        public void EffectExport_OverwritesStaleOrCorruptFileAndChecksActualPng()
        {
            RunSta(() =>
            {
                ThemeDocumentSession session = OpenSession();
                string path = Path.Combine(_root, "overwrite.png");
                try
                {
                    WriteSentinelPng(
                        path,
                        new Size(
                            ThemeExportService.HorizontalImageWidth,
                            ThemeExportService.HorizontalImageHeight),
                        Color.Magenta);
                    string staleSha256 = Sha256File(path);

                    ThemeImageExportResult result = ThemeExportService.ExportEffectImage(
                        session,
                        path,
                        ImageRoutePreference.Classic,
                        ThemeExportImageFormFactor.Horizontal);

                    Assert.True(result.Success, result.Error);
                    using (var actual = new Bitmap(path))
                    {
                        Assert.Equal(ThemeExportService.HorizontalImageWidth, actual.Width);
                        Assert.Equal(ThemeExportService.HorizontalImageHeight, actual.Height);
                        PixelStats stats = MeasureBackgroundPixels(
                            actual,
                            new Rectangle(8, 8, actual.Width - 16, actual.Height - 28));
                        Assert.True(
                            stats.FallbackLike >= 50,
                            $"Stable region must contain fallback pixels after overwrite; fallback={stats.FallbackLike}, main={stats.MainLike}, sampled={stats.Sampled}.");
                        Assert.True(
                            stats.FallbackLike > stats.MainLike,
                            "Fallback pixels must dominate after replacing the sentinel image.");
                    }
                    Assert.NotEqual(staleSha256, Sha256File(path));

                    File.WriteAllBytes(path, Encoding.ASCII.GetBytes("not-a-png"));
                    ThemeImageExportResult afterCorrupt = ThemeExportService.ExportEffectImage(
                        session,
                        path,
                        ImageRoutePreference.Classic,
                        ThemeExportImageFormFactor.Vertical);
                    Assert.True(afterCorrupt.Success, afterCorrupt.Error);
                    using var recovered = new Bitmap(path);
                    Assert.Equal(ThemeExportService.VerticalImageWidth, recovered.Width);
                    Assert.Equal(ThemeExportService.VerticalImageHeight, recovered.Height);
                }
                finally
                {
                    session.Draft.DisposeFonts();
                }
            });
        }

        [Fact]
        public void AutoExport_WhenGpuFails_ReplacesSameSizeStaleImageWithFallback()
        {
            RunSta(() =>
            {
                ThemeDocumentSession session = OpenSession();
                string path = Path.Combine(_root, "auto-fallback.png");
                try
                {
                    WriteSentinelPng(
                        path,
                        new Size(
                            ThemeExportService.HorizontalImageWidth,
                            ThemeExportService.HorizontalImageHeight),
                        Color.Magenta);
                    string staleSha256 = Sha256File(path);

                    ThemeImageExportResult result = ThemeExportService.ExportEffectImage(
                        session,
                        path,
                        ImageRoutePreference.Auto,
                        ThemeExportImageOrientation.Horizontal,
                        (_, _, size, orientation) =>
                        {
                            Assert.Equal(400, size.Width);
                            Assert.Equal(120, size.Height);
                            Assert.Equal(ThemeExportImageOrientation.Horizontal, orientation);
                            return new ThemeExportService.GpuRenderAttempt(
                                Success: false,
                                Error: "forced-gpu-failure");
                        });

                    Assert.True(result.Success, result.Error);
                    Assert.Equal(ThemeExportImageRoute.Fallback, result.Route);
                    Assert.NotEqual(staleSha256, Sha256File(path));
                    Assert.Contains(
                        "forced-gpu-failure",
                        string.Join("\n", result.Diagnostics),
                        StringComparison.Ordinal);

                    using var image = new Bitmap(path);
                    Assert.Equal(ThemeExportService.HorizontalImageWidth, image.Width);
                    Assert.Equal(ThemeExportService.HorizontalImageHeight, image.Height);
                    PixelStats stats = MeasureBackgroundPixels(
                        image,
                        new Rectangle(8, 8, image.Width - 16, image.Height - 28));
                    Assert.True(stats.FallbackLike >= 50);
                    Assert.True(stats.FallbackLike > stats.MainLike);
                }
                finally
                {
                    session.Draft.DisposeFonts();
                }
            });
        }

        [Fact]
        public void StrictGpuExport_WhenRendererFails_DeletesSameSizeStaleImage()
        {
            RunSta(() =>
            {
                ThemeDocumentSession session = OpenSession();
                string path = Path.Combine(_root, "strict-gpu-failure.png");
                try
                {
                    WriteSentinelPng(
                        path,
                        new Size(
                            ThemeExportService.HorizontalImageWidth,
                            ThemeExportService.HorizontalImageHeight),
                        Color.Magenta);

                    ThemeImageExportResult result = ThemeExportService.ExportEffectImage(
                        session,
                        path,
                        ImageRoutePreference.Gpu,
                        ThemeExportImageOrientation.Horizontal,
                        (_, _, _, _) => new ThemeExportService.GpuRenderAttempt(
                            Success: false,
                            Error: "forced-gpu-failure"));

                    Assert.False(result.Success);
                    Assert.False(File.Exists(path));
                    Assert.Contains("forced-gpu-failure", result.Error, StringComparison.Ordinal);
                }
                finally
                {
                    session.Draft.DisposeFonts();
                }
            });
        }

        [Fact]
        public void ImageValidation_RejectsMissingEmptyCorruptAndWrongSizedArtifacts()
        {
            RunSta(() =>
            {
                string missing = Path.Combine(_root, "missing.png");
                Assert.False(ThemeExportService.TryValidateImageArtifact(
                    missing,
                    new Size(400, 120),
                    out string? missingError));
                Assert.Contains("不存在", missingError);

                string empty = Path.Combine(_root, "empty.png");
                File.WriteAllBytes(empty, Array.Empty<byte>());
                Assert.False(ThemeExportService.TryValidateImageArtifact(
                    empty,
                    new Size(400, 120),
                    out string? emptyError));
                Assert.Contains("为空", emptyError);

                string corrupt = Path.Combine(_root, "corrupt.png");
                File.WriteAllText(corrupt, "not-a-png", Encoding.ASCII);
                Assert.False(ThemeExportService.TryValidateImageArtifact(
                    corrupt,
                    new Size(400, 120),
                    out string? corruptError));
                Assert.Contains("无法读取", corruptError);

                string wrongSize = Path.Combine(_root, "wrong-size.png");
                using (var bitmap = new Bitmap(240, 420, PixelFormat.Format32bppArgb))
                {
                    bitmap.Save(wrongSize, ImageFormat.Png);
                }

                Assert.False(ThemeExportService.TryValidateImageArtifact(
                    wrongSize,
                    new Size(400, 120),
                    out string? sizeError));
                Assert.Contains("尺寸不匹配", sizeError);
                Assert.Contains("240x420", sizeError);
                Assert.Contains("400x120", sizeError);
            });
        }

        [Fact]
        public void FallbackExport_UsesFallbackBackgroundInsteadOfGpuMainColor()
        {
            RunSta(() =>
            {
                ThemeDocumentSession session = OpenSession();
                string path = Path.Combine(_root, "fallback.png");
                try
                {
                    ThemeImageExportResult result = ThemeExportService.ExportEffectImage(
                        session,
                        path,
                        ImageRoutePreference.Classic,
                        ThemeExportImageFormFactor.Horizontal);

                    Assert.True(result.Success, result.Error);
                    Assert.Equal(ThemeExportImageRoute.Fallback, result.Route);

                    using var image = new Bitmap(path);
                    Color fallback = ParseRgb("#AABBCC");
                    Color main = ParseRgb("#112233");
                    PixelStats stats = MeasureBackgroundPixels(
                        image,
                        new Rectangle(8, 8, image.Width - 16, image.Height - 28));

                    Assert.True(
                        stats.FallbackLike >= 50,
                        $"Stable panel region must contain fallback background pixels; fallback={stats.FallbackLike}, main={stats.MainLike}, sampled={stats.Sampled}.");
                    Assert.True(
                        stats.FallbackLike > stats.MainLike,
                        $"Fallback background must dominate the main GPU background; fallback={stats.FallbackLike}, main={stats.MainLike}, sampled={stats.Sampled}.");
                }
                finally
                {
                    session.Draft.DisposeFonts();
                }
            });
        }

        private ThemeDocumentSession OpenSession()
        {
            string name = "ContractGpu";
            string path = Path.Combine(_root, name + ".json");
            byte[] bytes = new UTF8Encoding(false).GetBytes(GpuThemeJson);
            File.WriteAllBytes(path, bytes);
            return ThemeDocumentSession.FromSourceBytes(name, path, bytes);
        }

        private void AssertExportSize(
            ThemeDocumentSession session,
            ThemeExportImageFormFactor formFactor,
            int expectedWidth,
            int expectedHeight)
        {
            string path = Path.Combine(_root, formFactor + ".png");
            ThemeImageExportResult result = ThemeExportService.ExportEffectImage(
                session,
                path,
                ImageRoutePreference.Classic,
                formFactor);

            Assert.True(result.Success, result.Error);
            Assert.Equal(ThemeExportImageRoute.Fallback, result.Route);
            using var image = new Bitmap(path);
            Assert.Equal(expectedWidth, image.Width);
            Assert.Equal(expectedHeight, image.Height);
        }

        private static PixelStats MeasureBackgroundPixels(Bitmap image, Rectangle region)
        {
            Color fallback = ParseRgb("#AABBCC");
            Color main = ParseRgb("#112233");
            int fallbackLike = 0;
            int mainLike = 0;
            int sampled = 0;

            for (int y = region.Top; y < region.Bottom; y += 4)
            {
                for (int x = region.Left; x < region.Right; x += 4)
                {
                    Color pixel = image.GetPixel(x, y);
                    sampled++;
                    if (ColorDistanceSquared(pixel, fallback) <= 20 * 20) fallbackLike++;
                    if (ColorDistanceSquared(pixel, main) <= 20 * 20) mainLike++;
                }
            }

            return new PixelStats(sampled, fallbackLike, mainLike);
        }

        private static int ColorDistanceSquared(Color left, Color right)
        {
            int dr = left.R - right.R;
            int dg = left.G - right.G;
            int db = left.B - right.B;
            return dr * dr + dg * dg + db * db;
        }

        private static Color ParseRgb(string html)
        {
            Color color = ColorTranslator.FromHtml(html);
            return Color.FromArgb(color.R, color.G, color.B);
        }

        private static void WriteSentinelPng(string path, Size size, Color color)
        {
            using var bitmap = new Bitmap(
                size.Width,
                size.Height,
                PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(color);
            }
            bitmap.Save(path, ImageFormat.Png);
        }

        private static string Sha256File(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static void RunSta(Action action)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private readonly record struct PixelStats(int Sampled, int FallbackLike, int MainLike);
    }
}
