using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using LiteMonitor;
using LiteMonitor.src.Core;

namespace ScaleReproProbe
{
    /// <summary>
    /// 缩放复现探针。
    ///
    /// 用途：真实 MainForm + GPU 路线下，依次切换用户缩放（Settings.UIScale），
    /// 每次切换后截图，并导出「窗体客户区尺寸 / GPU 交换链尺寸 / 场景目标尺寸」对照，
    /// 用于判定「改缩放后面板只剩顶部一条」这类尺寸失配问题。
    ///
    /// 用法：ScaleReproProbe [theme] [outDir]
    /// </summary>
    internal static class Program
    {
        private static readonly float[] Scales = { 1.0f, 0.8f, 1.2f };

        [STAThread]
        private static int Main(string[] args)
        {
            string theme = args.Length > 0 ? args[0] : "LiquidGlass_GPU";
            string output = Path.GetFullPath(args.Length > 1 ? args[1] : "scale-repro");
            bool live = args.Any(a => string.Equals(a, "--live", StringComparison.OrdinalIgnoreCase));
            Directory.CreateDirectory(output);

            // ★ 先清掉上一次运行留下的 settings.json：
            //   否则本次会继承上次的开关（例如实时桌面背景），把"探针环境"变成"残留"。
            try
            {
                string? staleSettings = Path.Combine(AppContext.BaseDirectory, "settings.json");
                if (File.Exists(staleSettings))
                {
                    File.Delete(staleSettings);
                }
            }
            catch { }

            ApplicationConfiguration.Initialize();

            Settings config = Settings.Load();
            config.UIScale = 1.0f;
            if (live)
            {
                // 实时桌面背景（WDA + 桌面复制）：Resize 失败时代次错位会让捕获源变陈旧，
                // 表现为"液态玻璃突然失效"，是用户报障 1 的怀疑路径。
                config.GpuLiveBackgroundEnabled = true;
                config.GpuWdaCapturePolicyAccepted = true;
            }

            List<object> steps = new();
            MainForm? form = null;
            try
            {
                form = new MainForm();
                form.Show();
                // ★ 置顶：否则屏幕上若有别的窗口盖住面板，CopyFromScreen 抓到的就是别人的窗口，
                //   会被误判成"面板没渲染"。探针的截图必须只反映面板本身。
                form.TopMost = true;
                Pump(800);

                object ui = RequireUi(form);
                ApplyTheme(ui, theme);
                Pump(2500);

                foreach (float scale in Scales)
                {
                    Settings live2 = Settings.Load();
                    live2.UIScale = scale;
                    System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                    ApplyTheme(ui, theme);
                    watch.Stop();
                    double applyMs = watch.ElapsedMilliseconds;

                    Pump(2200);

                    string shot = Path.Combine(output, $"scale-{scale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}.png");
                    Capture(form, shot);

                    steps.Add(new
                    {
                        scale,
                        live,
                        applyMs,
                        shot,
                        clientSize = $"{form.ClientSize.Width}x{form.ClientSize.Height}",
                        swapChain = ReadSwapChainSize(ui),
                        sceneTarget = ReadSceneTargetSize(ui),
                        layoutWidth = ReadLayoutWidth(ui),
                        gpuActive = ReadGpuActive(ui),
                        gpuError = ReadGpuError(ui),
                        gpuRecoveryPending = ReadBool(ui, "_recoveryPending")
                    });
                }

                string json = Path.Combine(output, "scale-repro.json");
                File.WriteAllText(json, JsonSerializer.Serialize(steps, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("saved: " + json);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[FAIL] " + ex);
                return 2;
            }
            finally
            {
                try { form?.Close(); } catch { }
                Pump(300);

                // ★ 清理探针自己写出的设置：程序启动会往 exe 目录写 settings.json，
                //   留着会让下一次运行带上上次的开关（例如实时桌面背景），
                //   把"探针环境"变成"上次运行的残留"——排查时极易误判。
                try
                {
                    string? settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
                    if (File.Exists(settingsPath))
                    {
                        File.Delete(settingsPath);
                    }
                }
                catch { }
            }
        }

        private static object RequireUi(MainForm form)
        {
            FieldInfo? field = typeof(MainForm).GetField("_ui", BindingFlags.Instance | BindingFlags.NonPublic);
            object? ui = field?.GetValue(form);
            return ui ?? throw new InvalidOperationException("MainForm._ui 未初始化。");
        }

        private static void ApplyTheme(object ui, string theme)
        {
            ui.GetType()
                .GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.Public)
                ?.Invoke(ui, [theme, false]);
        }

        /// <summary>读取 GPU 交换链尺寸（逻辑像素）——经 _gpuPresentation → _renderer → _width/_height。</summary>
        private static string ReadSwapChainSize(object ui)
        {
            object? host = ui.GetType()
                .GetField("_gpuPresentation", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(ui);
            object? renderer = host?.GetType()
                .GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(host);
            if (renderer is null) return "<no-renderer>";
            int width = ReadInt(renderer, "_width");
            int height = ReadInt(renderer, "_height");
            return $"{width}x{height}";
        }

        /// <summary>读取场景目标纹理尺寸（_sceneTexture 描述宽高）。</summary>
        private static string ReadSceneTargetSize(object ui)
        {
            object? host = ui.GetType()
                .GetField("_gpuPresentation", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(ui);
            object? renderer = host?.GetType()
                .GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(host);
            if (renderer is null) return "<no-renderer>";
            int width = ReadInt(renderer, "_width");
            int height = ReadInt(renderer, "_height");
            return $"{width}x{height}";
        }

        private static string ReadLayoutWidth(object ui)
        {
            object? layout = ui.GetType()
                .GetField("_layout", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(ui);
            if (layout is null) return "<no-layout>";
            object? width = layout.GetType()
                .GetProperty("Layout", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(layout);
            return width?.ToString() ?? "<unknown>";
        }

        private static int ReadInt(object target, string fieldName)
        {
            object? value = target.GetType()
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(target);
            return value is int i ? i : -1;
        }

        private static object? ReadHostField(object ui, string fieldName)
        {
            object? host = ui.GetType()
                .GetField("_gpuPresentation", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(ui);
            return host?.GetType()
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(host);
        }

        private static bool ReadBool(object ui, string fieldName)
        {
            object? value = ReadHostField(ui, fieldName);
            return value is bool b && b;
        }

        private static bool ReadGpuActive(object ui)
        {
            object? host = ui.GetType()
                .GetField("_gpuPresentation", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(ui);
            object? active = host?.GetType()
                .GetProperty("IsActive", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(host);
            return active is bool b && b;
        }

        private static string ReadGpuError(object ui)
        {
            object? code = ReadHostField(ui, "_lastErrorCode");
            object? message = ReadHostField(ui, "_lastErrorMessage");
            if (code is null && message is null) return "<none>";
            return $"{code} / {message}";
        }

        private static void Capture(Form form, string path)
        {
            Rectangle bounds = form.Bounds;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }
            bitmap.Save(path, ImageFormat.Png);
        }

        private static void Pump(int milliseconds)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < until)
            {
                Application.DoEvents();
                Thread.Sleep(30);
            }
        }
    }
}
