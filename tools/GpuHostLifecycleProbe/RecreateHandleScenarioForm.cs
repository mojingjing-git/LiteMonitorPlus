using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace GpuHostLifecycleProbe;

internal enum RecreateScenario
{
    /// <summary>S1: the formal production renderer on a plain probe window.</summary>
    S1FormalRenderer,

    /// <summary>S2: the MainForm HWND shape (tool window, top-most, click-through, region, opacity).</summary>
    S2MainFormShape
}

/// <summary>
/// The probe window used by the T01 A/B experiment.
///
/// S1 uses a plain window. S2 reproduces the MainForm HWND binding shape
/// (T00 baseline §3): WS_EX_TOOLWINDOW, WS_EX_TOPMOST according to the
/// configuration, WS_EX_TRANSPARENT + WS_EX_LAYERED for click-through, the
/// window region and the top-most Z order, all restored from HandleCreated the
/// way MainForm does (MainForm_Transparent.cs:266-274).
/// </summary>
internal sealed class RecreateHandleScenarioForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;

    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int LwaAlpha = 0x00000002;
    private static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", EntryPoint = "SetLayeredWindowAttributes")]
    private static extern bool SetLayeredWindowAttributes(
        IntPtr hWnd,
        uint crKey,
        byte bAlpha,
        uint dwFlags);

    private readonly RecreateScenario _scenario;
    private readonly bool _topmost;
    private readonly bool _clickThrough;
    private readonly int _timeoutMs;
    private readonly CancellationTokenSource _cancellation;

    public RecreateHandleScenarioForm(
        string output,
        RecreateScenario scenario,
        bool topmost,
        bool clickThrough,
        int timeoutMs)
    {
        _scenario = scenario;
        _topmost = topmost;
        _clickThrough = clickThrough;
        _timeoutMs = timeoutMs;
        _cancellation = new CancellationTokenSource(timeoutMs);

        Text = $"LiteMonitor GPU T01 probe ({scenario})";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = false;
        ClientSize = new Size(420, 300);
        Location = new Point(80, 80);
        BackColor = Color.Black;
        DoubleBuffered = false;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint,
            true);
    }

    public GdiRecoveryExperiment? Experiment { get; set; }

    public int ExitCode { get; private set; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            // Never steal the focus (GPU plan §7.3).
            parameters.ExStyle |= WsExNoActivate;

            if (_scenario == RecreateScenario.S2MainFormShape)
            {
                parameters.ExStyle |= WsExToolWindow;
                parameters.ExStyle &= ~WsExAppWindow;
                if (_topmost)
                {
                    parameters.ExStyle |= WsExTopmost;
                }

                if (_clickThrough)
                {
                    parameters.ExStyle |= WsExTransparent;
                    parameters.ExStyle |= WsExLayered;
                }
            }

            return parameters;
        }
    }

    /// <summary>Exposes the protected Control.RecreateHandle to the experiment.</summary>
    public void RecreateHandleForProbe() => RecreateHandle();

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (_scenario != RecreateScenario.S2MainFormShape)
        {
            return;
        }

        // Mirror the production restore path (MainForm_Transparent.cs:266-274):
        // the region and the top-most Z order are re-applied on the new handle.
        ApplyRoundedRegion();
        if (_topmost)
        {
            ReinsertTopMost();
        }

        if (_clickThrough)
        {
            // The layered window must be fully opaque, otherwise the desktop
            // pixel is a blend of the GDI frame and the wallpaper and the
            // exact-colour criterion cannot be applied.
            SetLayeredWindowAttributes(Handle, 0, 255, LwaAlpha);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_scenario == RecreateScenario.S2MainFormShape)
        {
            try
            {
                // Mirrors WindowAppearanceController.OnHandleDestroyed, which
                // drops the native region before the handle disappears.
                Region = null;
            }
            catch (Exception)
            {
                // The handle is already gone; nothing to release.
            }
        }

        base.OnHandleDestroyed(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Experiment is null)
        {
            return;
        }

        Experiment.NotePaint();
        FramePainter.Draw(e.Graphics, Experiment.CurrentFrameColor, Experiment.CurrentFrameIndex);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (Experiment is null)
        {
            base.OnPaintBackground(e);
            return;
        }

        using SolidBrush brush = new(Experiment.CurrentFrameColor);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            if (Experiment is null)
            {
                throw new InvalidOperationException("The experiment was not attached to the probe window.");
            }

            await Experiment.RunAsync(_cancellation.Token);
            ExitCode = SafeWriteEvidence(0, null);
            Close();
        }
        catch (OperationCanceledException)
        {
            ExitCode = SafeWriteEvidence(3, "The run exceeded its own deadline.");
            Close();
        }
        catch (Exception exception)
        {
            ExitCode = SafeWriteEvidence(1, exception.ToString());
            try
            {
                File.WriteAllText(Path.Combine(Experiment!.OutputDirectory, "failure.log"), exception.ToString());
            }
            catch (Exception)
            {
                // The evidence file is the important artefact.
            }

            Close();
        }
    }

    private int SafeWriteEvidence(int exitCode, string? failure)
    {
        try
        {
            return Experiment?.WriteEvidence(_scenario.ToString(), exitCode, failure) ?? exitCode;
        }
        catch (Exception)
        {
            return exitCode == 0 ? 1 : exitCode;
        }
    }

    private void ApplyRoundedRegion()
    {
        try
        {
            Rectangle bounds = new(0, 0, ClientSize.Width, ClientSize.Height);
            using GraphicsPath path = new();
            int radius = 18;
            path.AddArc(bounds.X, bounds.Y, radius, radius, 180, 90);
            path.AddArc(bounds.Right - radius, bounds.Y, radius, radius, 270, 90);
            path.AddArc(bounds.Right - radius, bounds.Bottom - radius, radius, radius, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - radius, radius, radius, 90, 90);
            path.CloseFigure();
            Region = new Region(path);
        }
        catch (Exception)
        {
            // A missing region does not invalidate the GDI verdict.
        }
    }

    private void ReinsertTopMost()
    {
        try
        {
            SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }
        catch (Exception)
        {
            // Recorded through the window state snapshot either way.
        }
    }
}
