using System.Diagnostics;

namespace AppearanceMaterialLab;

internal sealed class LabForm : Form
{
    private readonly LabOptions _options;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private D3D11Renderer? _renderer;
    private bool _captured;
    private int _renderedFrames;

    public LabForm(LabOptions options)
    {
        _options = options;

        Text = "Glass Toolbar Preview";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(1280, 900);
        MinimumSize = Size.Empty;
        MaximumSize = Size.Empty;
        BackColor = Color.Black;
        DoubleBuffered = true;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.Opaque,
            true);

        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 900);
        Location = new Point(
            workingArea.Left + Math.Max(0, (workingArea.Width - Width) / 2),
            workingArea.Top + Math.Max(0, (workingArea.Height - Height) / 2));

        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += OnTick;
        Shown += OnShown;
        FormClosed += OnFormClosed;
        KeyDown += OnKeyDown;
    }

    public Exception? FatalError { get; private set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        // The DXGI swap chain owns the client surface.
    }

    private void OnShown(object? sender, EventArgs e)
    {
        try
        {
            _renderer = new D3D11Renderer(this, _options);
            _timer.Start();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_renderer is null || FatalError is not null)
        {
            return;
        }

        try
        {
            _renderer.Render();
            _renderedFrames++;

            int elapsed = (int)_clock.ElapsedMilliseconds;
            if (_options.CapturePath is not null && !_captured && _renderedFrames >= 3 && elapsed >= 350)
            {
                _renderer.CaptureBackBuffer(_options.CapturePath);
                _captured = true;
            }

            bool timedOut = _options.ExitAfterMilliseconds > 0 &&
                elapsed >= _options.ExitAfterMilliseconds;
            bool captureCompleteWithoutTimer =
                _options.CapturePath is not null &&
                _options.ExitAfterMilliseconds == 0 &&
                _captured;

            if (timedOut || captureCompleteWithoutTimer)
            {
                Close();
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void Fail(Exception exception)
    {
        FatalError = exception;
        _timer.Stop();
        if (IsHandleCreated)
        {
            BeginInvoke(Close);
        }
        else
        {
            Close();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
        }
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _renderer?.Dispose();
        _renderer = null;
    }
}
