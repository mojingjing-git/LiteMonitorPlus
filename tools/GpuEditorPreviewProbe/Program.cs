using System.Reflection;
using System.Text.Json;
using LiteMonitor.Rendering.Presentation;
using LiteMonitor.ThemeEditor;
using LiteMonitor.src.Core;

namespace GpuEditorPreviewProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string themeName = args.Length > 0
            ? args[0]
            : "LiquidGlass_GPU";
        string output = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(AppContext.BaseDirectory, "evidence");
        Directory.CreateDirectory(output);
        try
        {
            WriteIsolatedSettings(themeName);
            ApplicationConfiguration.Initialize();
            Theme theme = ThemeManager.Load(themeName);
            using PreviewProbeForm form = new(theme, output, themeName);
            Application.Run(form);
            return form.ExitCode;
        }
        catch (Exception exception)
        {
            File.WriteAllText(
                Path.Combine(output, "failure.log"),
                exception.ToString());
            return 1;
        }
    }

    private static void WriteIsolatedSettings(string themeName)
    {
        object settings = new
        {
            Version = 9,
            Skin = themeName,
            GpuThemeEffectsEnabled = true,
            GpuLiveBackgroundEnabled = false,
            GpuWdaCapturePolicyAccepted = false,
            LiveGlassEnabled = false,
            MonitorItems = Array.Empty<object>()
        };
        File.WriteAllText(
            Path.Combine(AppContext.BaseDirectory, "settings.json"),
            JsonSerializer.Serialize(
                settings,
                new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed class PreviewProbeForm : Form
{
    private readonly Theme _theme;
    private readonly string _output;
    private readonly string _themeName;
    private readonly ThemePreviewControl _preview;
    private readonly System.Windows.Forms.Timer _timer;
    private int _elapsed;
    private bool _captured;

    public int ExitCode { get; private set; }

    public PreviewProbeForm(
        Theme theme,
        string output,
        string themeName)
    {
        _theme = theme;
        _output = output;
        _themeName = themeName;
        Text = "LiteMonitor GPU editor preview probe";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = false;
        ShowInTaskbar = false;
        ClientSize = new Size(525, 720);
        _preview = new ThemePreviewControl { Dock = DockStyle.Fill };
        Controls.Add(_preview);
        _preview.SetTheme(theme);

        _timer = new System.Windows.Forms.Timer { Interval = 100 };
        _timer.Tick += OnTick;
        Shown += (_, _) => _timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExNoActivate = 0x08000000;
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate;
            return parameters;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _elapsed += _timer.Interval;
        if (_captured || _elapsed < 2000)
        {
            return;
        }

        GpuThemePreviewControl? gpu = null;
        GpuSceneRenderer? renderer = null;
        try
        {
            FieldInfo? gpuField = typeof(ThemePreviewControl).GetField(
                "_gpuPreview",
                BindingFlags.Instance | BindingFlags.NonPublic);
            gpu = gpuField?.GetValue(_preview) as GpuThemePreviewControl;
            if (gpu is null || !gpu.IsActive)
            {
                throw new InvalidOperationException(
                    "Editor GPU preview did not activate: " +
                    (gpu?.Error ?? "child was not created"));
            }

            FieldInfo? rendererField = typeof(GpuThemePreviewControl).GetField(
                "_renderer",
                BindingFlags.Instance | BindingFlags.NonPublic);
            renderer = rendererField?.GetValue(gpu) as GpuSceneRenderer;
            if (renderer is null)
            {
                throw new InvalidOperationException(
                    "Editor preview renderer is unavailable.");
            }

            string stem = _themeName.Contains(
                "Cyber",
                StringComparison.OrdinalIgnoreCase)
                ? "cyber-editor"
                : "glass-editor";
            string evidencePath = Path.Combine(
                _output,
                $"{stem}.png");
            renderer.CaptureScenePng(evidencePath);

            object beforeDispose = new
            {
                task = "R18-editor-preview",
                theme = _themeName,
                previewSize = new
                {
                    gpu.Width,
                    gpu.Height,
                    gpu.DeviceDpi
                },
                gpu.IsActive,
                gpu.CaptureSessionsCreated,
                renderer = renderer.Diagnostics(),
                evidenceBounds = "fixed preview sample and scene only",
                capturedAt = DateTimeOffset.Now
            };

            _preview.Dispose();
            object afterDispose = new
            {
                gpu.IsDisposed,
                gpu.IsActive,
                gpu.CaptureSessionsCreated
            };
            File.WriteAllText(
                Path.Combine(_output, $"{stem}.diagnostics.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        beforeDispose,
                        afterDispose
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
            _captured = true;
            ExitCode = 0;
            _timer.Stop();
            Close();
        }
        catch (Exception exception)
        {
            File.WriteAllText(
                Path.Combine(_output, "failure.log"),
                exception.ToString());
            _timer.Stop();
            Close();
            ExitCode = 1;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _preview.Dispose();
        }

        base.Dispose(disposing);
    }
}
