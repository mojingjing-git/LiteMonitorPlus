using System.Diagnostics;
using System.Drawing.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AppearanceMaterialLab;

internal sealed class LabForm : Form
{
    private readonly LabOptions _options;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DemoBackdropForm? _demoBackdrop;
    private Form _presentationHost;
    private D3D11Renderer? _renderer;
    private LabMode _mode;
    private bool _live;
    private bool _excludeFromCapture;
    private bool _captured;
    private bool _screenCaptured;
    private Exception? _fatalError;

    public LabForm(LabOptions options)
    {
        _options = options;
        _mode = options.Mode;
        _live = options.LiveCapture;
        _excludeFromCapture = options.ExcludeFromCapture;
        _presentationHost = this;

        Text = "Appearance Material Lab";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(1280, 800);
        BackColor = Color.Black;
        DoubleBuffered = true;
        TopMost = true;
        Opacity = options.Opacity;
        KeyPreview = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.Opaque |
            ControlStyles.ResizeRedraw,
            true);

        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(
            workingArea.Left + Math.Max(0, (workingArea.Width - Width) / 2),
            workingArea.Top + Math.Max(0, (workingArea.Height - Height) / 2));

        _timer = new System.Windows.Forms.Timer
        {
            Interval = 16
        };
        _timer.Tick += OnTick;

        Shown += OnShown;
        FormClosed += OnFormClosed;
        Resize += OnResize;
        KeyDown += OnKeyDown;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // The DXGI swap chain owns the client surface.
    }

    private void OnShown(object? sender, EventArgs e)
    {
        try
        {
            ApplyWindowPolicy();
            _presentationHost = CreatePresentationHost();
            _renderer = new D3D11Renderer(_presentationHost, _mode, _live);
            _renderer.SetExcludedFromCapture(Handle, _excludeFromCapture);
            UpdateDemoBackdrop();
            _timer.Start();
        }
        catch (Exception ex)
        {
            _fatalError = ex;
            _timer.Stop();
            MessageBox.Show(
                this,
                ex.ToString(),
                "Appearance Material Lab initialization failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_renderer is null || _fatalError is not null)
        {
            return;
        }

        try
        {
            _renderer.Mode = _mode;
            _renderer.LiveCapture = _live;
            _renderer.Seconds = _clock.Elapsed.TotalSeconds;
            _renderer.Render();

            int elapsed = (int)_clock.ElapsedMilliseconds;
            if (_options.CapturePath is not null && !_captured && elapsed >= 1500)
            {
                _renderer.CaptureBackBuffer(_options.CapturePath);
                _captured = true;
            }

            if (_options.ScreenCapturePath is not null && !_screenCaptured && elapsed >= 1500)
            {
                CaptureScreenBounds(_options.ScreenCapturePath);
                _screenCaptured = true;
            }

            if (_options.ExitAfterMilliseconds > 0 && elapsed >= _options.ExitAfterMilliseconds)
            {
                Close();
            }
        }
        catch (Exception ex)
        {
            _fatalError = ex;
            _timer.Stop();
            MessageBox.Show(
                this,
                ex.ToString(),
                "Appearance Material Lab render failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OnResize(object? sender, EventArgs e)
    {
        _renderer?.Resize(ClientSize);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.D1:
                _mode = LabMode.Glass;
                break;
            case Keys.D2:
                _mode = LabMode.Cyber;
                break;
            case Keys.L:
                _live = !_live;
                UpdateDemoBackdrop();
                break;
            case Keys.E:
                _excludeFromCapture = !_excludeFromCapture;
                _renderer?.SetExcludedFromCapture(Handle, _excludeFromCapture);
                break;
            case Keys.C:
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                    $"AppearanceMaterialLab-{_mode}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                _renderer?.CaptureBackBuffer(path);
                MessageBox.Show(this, path, "Captured backbuffer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
            case Keys.Escape:
                Close();
                break;
        }
    }

    private void ApplyWindowPolicy()
    {
        if (_options.ClickThrough)
        {
            NativeMethods.SetExtendedStyleBits(
                Handle,
                NativeMethods.WsExLayered | NativeMethods.WsExTransparent,
                0);
        }
    }

    private Form CreatePresentationHost()
    {
        if (_options.PresentationRoute == PresentationRoute.Hwnd)
        {
            return this;
        }

        Form child = new()
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            TopLevel = false,
            Dock = DockStyle.Fill,
            BackColor = Color.Black
        };
        Controls.Add(child);
        child.CreateControl();
        child.Show();
        return child;
    }

    private void UpdateDemoBackdrop()
    {
        if (_live)
        {
            if (_demoBackdrop is null || _demoBackdrop.IsDisposed)
            {
                _demoBackdrop = new DemoBackdropForm();
                _demoBackdrop.Show();
            }
            else if (!_demoBackdrop.Visible)
            {
                _demoBackdrop.Show();
            }
            _presentationHost.BringToFront();
        }
        else if (_demoBackdrop is not null)
        {
            _demoBackdrop.Hide();
        }
    }

    private void CaptureScreenBounds(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using Bitmap bitmap = new(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(PointToScreen(Point.Empty), Point.Empty, ClientSize);
        bitmap.Save(path, ImageFormat.Png);
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        _timer.Stop();
        _renderer?.Dispose();
        _renderer = null;
        if (_demoBackdrop is not null && !_demoBackdrop.IsDisposed)
        {
            _demoBackdrop.Close();
        }
        _demoBackdrop = null;

        if (NativeMethods.IsWindow(Handle))
        {
            NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WdaNone);
        }
    }
}
