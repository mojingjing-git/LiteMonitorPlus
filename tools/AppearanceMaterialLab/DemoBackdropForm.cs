using System.Drawing.Drawing2D;

namespace AppearanceMaterialLab;

internal sealed class DemoBackdropForm : Form
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly DateTime _started = DateTime.UtcNow;

    public DemoBackdropForm()
    {
        Text = "Appearance Material Lab Demo Backdrop";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        ControlBox = false;
        TopMost = true;
        Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        BackColor = Color.Black;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);

        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += (_, _) => Invalidate();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return parameters;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = ClientRectangle;
        double seconds = (DateTime.UtcNow - _started).TotalSeconds;
        using LinearGradientBrush background = new(
            bounds,
            Color.FromArgb(10, 34, 52),
            Color.FromArgb(19, 69, 70),
            35f);
        graphics.FillRectangle(background, bounds);

        DrawMovingBlock(
            graphics,
            bounds,
            Color.FromArgb(230, 63, 66),
            0.10f + 0.03f * MathF.Sin((float)seconds * 0.4f),
            0.10f,
            0.26f,
            0.21f);
        DrawMovingBlock(
            graphics,
            bounds,
            Color.FromArgb(42, 169, 216),
            0.60f,
            0.12f + 0.025f * MathF.Sin((float)seconds * 0.31f + 1.3f),
            0.28f,
            0.18f);
        DrawMovingBlock(
            graphics,
            bounds,
            Color.FromArgb(235, 207, 134),
            0.07f,
            0.61f,
            0.40f,
            0.23f);
        DrawMovingBlock(
            graphics,
            bounds,
            Color.FromArgb(92, 211, 128),
            0.63f,
            0.59f,
            0.27f,
            0.24f);

        using Pen stripePen = new(Color.FromArgb(120, 16, 22, 25), 13f);
        int stripeOffset = (int)(seconds * 22) % 44;
        for (int x = bounds.Left - bounds.Height + stripeOffset; x < bounds.Right; x += 44)
        {
            graphics.DrawLine(stripePen, x, bounds.Bottom, x + bounds.Height, bounds.Top);
        }

        using Font titleFont = new("Segoe UI Variable", 22f, System.Drawing.FontStyle.Bold);
        using Font monoFont = new("Cascadia Mono", 13f);
        using SolidBrush labelBrush = new(Color.FromArgb(228, 247, 250));
        graphics.DrawString("DEDICATED CAPTURE DEMO", titleFont, labelBrush, 72, 56);
        graphics.DrawString(
            "Synthetic background only / no private desktop evidence is written",
            monoFont,
            labelBrush,
            76,
            96);
    }

    private static void DrawMovingBlock(
        Graphics graphics,
        Rectangle bounds,
        Color color,
        float x,
        float y,
        float width,
        float height)
    {
        RectangleF rectangle = new(
            x * bounds.Width,
            y * bounds.Height,
            width * bounds.Width,
            height * bounds.Height);
        using SolidBrush brush = new(color);
        graphics.FillRectangle(brush, rectangle);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
