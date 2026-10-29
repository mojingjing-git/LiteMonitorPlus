using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using LiteMonitor;
using LiteMonitor.src.Core;

namespace ThemeSwitchReproProbe
{
    /// <summary>
    /// 主题切换复现探针。
    ///
    /// 用途：复刻用户报障序列 —— "切到液态玻璃主题后玻璃不出来，把窗口拖到另一块屏
    /// 才正常；退出重启后又正常"。
    /// <para>场景 A（复现）：legacy 主题启动 → 运行中切到玻璃主题。</para>
    /// <para>场景 B（对照）：启动即玻璃主题（用户重启后的状态）。</para>
    /// 每次都截图 + 导出 GPU 诊断，两场景对照即可判定"是切换路径的问题还是呈现本身的问题"。
    ///
    /// 用法：ThemeSwitchReproProbe [outDir]
    /// </summary>
    internal static class Program
    {
        private const string GlassTheme = "LiquidGlass_GPU";

        [STAThread]
        private static int Main(string[] args)
        {
            string output = Path.GetFullPath(args.Length > 0 ? args[0] : "theme-switch-repro");
            Directory.CreateDirectory(output);

            // ★ WinForms 初始化只能做一次（第二次会抛 "SetCompatibleTextRenderingDefault ..."）。
            ApplicationConfiguration.Initialize();

            List<object> steps = [];

            steps.Add(RunScenario("A-运行中切主题", output, glassAtStartup: false, live: false));
            steps.Add(RunScenario("B-启动即玻璃", output, glassAtStartup: true, live: false));
            steps.Add(RunScenario("C-运行中切主题+实时背景", output, glassAtStartup: false, live: true));
            steps.Add(RunScenario("D-启动即玻璃+实时背景", output, glassAtStartup: true, live: true));

            // ★★ 逐屏复现：把窗口分别放到每一块显示器上，再做"运行中切玻璃"。
            //   多屏机器上"切不出来、换屏就好"的现象极可能与"窗口所在输出"有关。
            foreach (Screen screen in Screen.AllScreens)
            {
                string safe = new string(screen.DeviceName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                steps.Add(RunScenario(
                    $"屏-{safe}-运行中切玻璃",
                    output,
                    glassAtStartup: false,
                    live: true,
                    targetScreen: screen));
            }

            string json = Path.Combine(output, "theme-switch-repro.json");
            File.WriteAllText(json, JsonSerializer.Serialize(steps, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("saved: " + json);
            return 0;
        }

        private static object RunScenario(string label, string output, bool glassAtStartup, bool live, Screen? targetScreen = null)
        {
            // 全新环境：删掉上一次残留的 settings.json（否则开关/主题会被继承）。
            TryDeleteSettings();

            Settings config = Settings.Load();
            config.UIScale = 1.0f;
            if (glassAtStartup)
            {
                config.Skin = GlassTheme;
            }
            else
            {
                config.Skin = "RefinedDark";
            }

            if (live)
            {
                // 实时桌面背景（WDA + Desktop Duplication）：用户的实际用法。
                config.GpuLiveBackgroundEnabled = true;
                config.GpuWdaCapturePolicyAccepted = true;
            }

            MainForm? form = null;
            try
            {
                form = new MainForm();
                form.Show();
                form.TopMost = true;
                Pump(900);

                if (targetScreen is not null)
                {
                    // ★ 把窗口挪到目标屏（模拟用户"托着窗口换屏"的动作）。
                    //   注意：这是**先挪位置再切主题**，用于验证"窗口所在输出"是否决定成败。
                    form.Location = new Point(
                        targetScreen.Bounds.Right - Math.Min(form.Width, targetScreen.Bounds.Width) - 20,
                        targetScreen.Bounds.Top + 20);
                    Pump(700);
                }

                object ui = RequireUi(form);
                Pump(600);

                string beforeShot = Path.Combine(output, $"{label}-切换前.png");
                Capture(form, beforeShot);
                object before = Diagnostics(ui, form);

                if (!glassAtStartup)
                {
                    // ★ 用户报障的动作：运行中切到液态玻璃主题。
                    ApplyTheme(ui, GlassTheme);
                    Pump(3000);
                }

                string afterShot = Path.Combine(output, $"{label}-切换后.png");
                Capture(form, afterShot);
                object after = Diagnostics(ui, form);

                return new
                {
                    label,
                    glassAtStartup,
                    live,
                    beforeShot,
                    afterShot,
                    before,
                    after,
                };
            }
            catch (Exception ex)
            {
                return new { label, glassAtStartup, live, error = ex.ToString() };
            }
            finally
            {
                try { form?.Close(); } catch { }
                Pump(400);
                TryDeleteSettings();
            }
        }

        // ── 诊断读取 ──────────────────────────────────────────────

        private static object Diagnostics(object ui, Form form)
        {
            object? host = Field(ui, "_gpuPresentation");
            object? renderer = Field(host, "_renderer");

            return new
            {
                gpuActive = host?.GetType().GetProperty("IsActive", BindingFlags.Instance | BindingFlags.Public)?.GetValue(host),
                liveCaptureStarted = host?.GetType().GetProperty("IsLiveCaptureStarted", BindingFlags.Instance | BindingFlags.Public)?.GetValue(host),
                presentAttempts = ReadInt(renderer, "_presentAttempts"),
                presentedFrames = ReadInt(renderer, "_presentedFrames"),
                swapChain = $"{ReadInt(renderer, "_width")}x{ReadInt(renderer, "_height")}",
                errorCode = Field(host, "_lastErrorCode"),
                errorMessage = Field(host, "_lastErrorMessage"),
                route = Field(ui, "_gpuRoute")?.ToString(),
                fallbackCode = Field(ui, "_gpuFallbackCode"),
                windowLayered = form.GetType().GetMethod("IsWindowLayered", BindingFlags.Instance | BindingFlags.Public)?.Invoke(form, null),
                formOpacity = form.Opacity,
            };
        }

        private static object? Field(object? target, string name) =>
            target?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target);

        private static int ReadInt(object? target, string name)
        {
            object? value = Field(target, name);
            return value is int i ? i : -1;
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

        private static void TryDeleteSettings()
        {
            try
            {
                string? path = Path.Combine(AppContext.BaseDirectory, "settings.json");
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
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
