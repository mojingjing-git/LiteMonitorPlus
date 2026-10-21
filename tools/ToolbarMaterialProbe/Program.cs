using System.Collections.Immutable;
using System.Text.Json;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Gpu.Device;
using LiteMonitor.Rendering.Materials;
using LiteMonitor.Rendering.Presentation;
using LiteMonitor.src.Core;

namespace ToolbarMaterialProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 0) return 2;
        if (args[0] == "--compare")
        {
            using var reference = new Bitmap(args[1]);
            using var current = new Bitmap(args[2]);
            int checkedPixels = 0, changed = 0, max = 0;
            for (int y = 0; y < reference.Height; y++) for (int x = 0; x < reference.Width; x++)
            {
                if (x >= 448 && x <= 833 && y >= 77 && y <= 758) continue;
                var p = reference.GetPixel(x, y); var q = current.GetPixel(x, y); checkedPixels++;
                int d = Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B)));
                if (d > 0) changed++; max = Math.Max(max, d);
            }
            File.WriteAllText(args[3], JsonSerializer.Serialize(new
            { checkedPixels, changedPixels = changed, maxChannelDifference = max, foregroundExcluded = true }));
            return changed == 0 ? 0 : 1;
        }
        string output = Path.GetFullPath(args[0]);
        string pattern = args.Length > 1 ? args[1] : "v4";
        float blur = args.Length > 2 ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 8;
        using var form = new ProbeForm(output, pattern, blur);
        Application.Run(form);
        return form.Success ? 0 : 1;
    }
}

internal sealed class ProbeForm : Form
{
    private readonly string _output;
    private readonly string _pattern;
    private readonly float _blur;
    private readonly GpuResourceLedger _ledger = new(1024L * 1024 * 1024, 256);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private GpuSceneRenderer? _renderer;
    private readonly RenderGenerations _generations = new(1, 1, 1, 1, 1);
    public bool Success { get; private set; }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000; return p; }
    }

    public ProbeForm(string output, string pattern, float blur)
    {
        _output = output; _pattern = pattern; _blur = blur;
        Text = "LiteMonitor material correctness probe";
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(1280, 900);
        TopMost = false;
        StartPosition = FormStartPosition.Manual;
        var screen = Screen.AllScreens.Last();
        Location = new Point(screen.WorkingArea.Left + 10, screen.WorkingArea.Top + 10);
        Shown += (_, _) => _timer.Start();
        _timer.Tick += (_, _) => RunOnce();
    }

    private void RunOnce()
    {
        _timer.Stop();
        Directory.CreateDirectory(Path.GetDirectoryName(_output)!);
        try
        {
            string themePath = Path.Combine(AppContext.BaseDirectory, "resources", "themes", "LiquidGlass_Toolbar_GPU.json");
            var theme = JsonSerializer.Deserialize<Theme>(File.ReadAllText(themePath), ThemeManager.CreateSerializerOptions())!;
            var settings = MaterialRuntimeSettings.Create(theme.Rendering, MaterialKind.LiquidGlass, theme) with { BlurRadius = _blur };
            if (!GpuSceneRenderer.TryCreate(Handle, _generations, 1280, 900, MaterialKind.LiquidGlass, _ledger,
                    settings, 96, 1, null, out _renderer, out var code, out var message))
                throw new InvalidOperationException(code + ": " + message);
            FixedBackgroundInput input = ToolbarPreviewFixture.CreateInput(1280, 900);
            if (_pattern != "v4")
            {
                byte[] pixels = new byte[1280 * 900 * 4];
                for (int y = 0; y < 900; y++) for (int x = 0; x < 1280; x++)
                {
                    int at = (y * 1280 + x) * 4;
                    byte value = _pattern == "dark" ? (byte)12 : (byte)((x / 4) % 2 == 0 ? 30 : 230);
                    pixels[at] = pixels[at + 1] = pixels[at + 2] = value; pixels[at + 3] = 255;
                }
                if (!FixedBackgroundInput.TryCreate(pixels, 1280, 900, 5120, 96, new(0, 0, 1280, 900),
                        _pattern, out var other, out code, out message)) throw new InvalidOperationException(message);
                input = other!;
            }
            if (!_renderer!.TrySetFixedBackground(input, out code, out message)) throw new InvalidOperationException(message);
            var scene = new SceneSnapshot(RenderingContractVersion.Core, _generations, 1280, 900, 96,
                ImmutableArray<SceneGroup>.Empty, ImmutableArray<SceneNotice>.Empty);
            _renderer.Render(scene, _generations, RectangleToScreen(ClientRectangle), TimeSpan.Zero,
                captureRequested: false, materialRoot: new Rectangle(406, 50, 468, 800), drawForeground: false);
            _renderer.CaptureScenePng(_output);
            var inputDiagnostics = _renderer.CurrentBackgroundInput;
            _renderer.Dispose(); _renderer = null;
            var resources = _ledger.Snapshot();
            Success = resources.ActiveResourceCount == 0 && resources.ActiveBytes == 0;
            File.WriteAllText(Path.ChangeExtension(_output, ".json"), JsonSerializer.Serialize(new
            {
                status = Success ? "PASS" : "FAIL", pattern = _pattern, blur = _blur,
                topMost = TopMost, activationRequested = false, liveCapture = false,
                input = inputDiagnostics, resourcesAfterDispose = resources,
                mainAssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(typeof(GpuSceneRenderer).Assembly.Location)))
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { File.WriteAllText(Path.ChangeExtension(_output, ".error.txt"), ex.ToString()); }
        finally { _renderer?.Dispose(); Close(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _renderer?.Dispose(); }
        base.Dispose(disposing);
    }
}
