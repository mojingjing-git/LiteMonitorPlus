using System.Globalization;

namespace AppearanceMaterialLab;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        ApplicationConfiguration.Initialize();

        LabOptions options;
        try
        {
            options = LabOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Appearance Material Lab", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (options.Help)
        {
            MessageBox.Show(LabOptions.HelpText, "Appearance Material Lab", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (options.FixtureDumpPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.FixtureDumpPath) ?? ".");
            FixedBackground.Create().SavePng(options.FixtureDumpPath);
            return;
        }

        Application.Run(new LabForm(options));
    }
}

internal sealed record LabOptions(
    LabMode Mode,
    bool LiveCapture,
    bool ExcludeFromCapture,
    bool ClickThrough,
    PresentationRoute PresentationRoute,
    float Opacity,
    bool CaptureAfterReady,
    string? CapturePath,
    string? ScreenCapturePath,
    string? FixtureDumpPath,
    int ExitAfterMilliseconds)
{
    public const string HelpText =
        "AppearanceMaterialLab\n\n" +
        "--mode glass|cyber\n" +
        "--live                 Use Desktop Duplication instead of the fixed fixture\n" +
        "--exclude-from-capture Apply WDA_EXCLUDEFROMCAPTURE to the lab window\n" +
        "--click-through        Add WS_EX_TRANSPARENT while presenting\n" +
        "--presentation ROUTE   hwnd (default) or child presentation host\n" +
        "--opacity VALUE        Form.Opacity from 0.1 to 1.0\n" +
        "--capture PATH         Save a deterministic backbuffer PNG after warm-up\n" +
        "--screen-capture PATH  Save an external OS capture of the sample bounds\n" +
        "--dump-fixture PATH    Save the deterministic background without a GPU window\n" +
        "--exit-after MS        Close after a bounded duration\n\n" +
        "Keys: 1 glass, 2 cyber, L live/fixed, E exclusion, C PNG, Esc close.";

    public bool Help { get; init; }

    public static LabOptions Parse(string[] args)
    {
        LabMode mode = LabMode.Glass;
        bool live = false;
        bool exclude = false;
        bool clickThrough = false;
        PresentationRoute presentationRoute = PresentationRoute.Hwnd;
        float opacity = 1.0f;
        string? capturePath = null;
        string? screenCapturePath = null;
        string? fixtureDumpPath = null;
        int exitAfter = 0;
        bool help = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--help":
                case "-h":
                case "/?":
                    help = true;
                    break;
                case "--mode":
                    EnsureValue(args, i, arg);
                    mode = args[++i].ToLowerInvariant() switch
                    {
                        "glass" => LabMode.Glass,
                        "cyber" => LabMode.Cyber,
                        var value => throw new ArgumentException($"Unknown mode '{value}'. Use glass or cyber.")
                    };
                    break;
                case "--live":
                    live = true;
                    break;
                case "--exclude-from-capture":
                    exclude = true;
                    break;
                case "--click-through":
                    clickThrough = true;
                    break;
                case "--presentation":
                    EnsureValue(args, i, arg);
                    presentationRoute = args[++i].ToLowerInvariant() switch
                    {
                        "hwnd" => PresentationRoute.Hwnd,
                        "child" => PresentationRoute.Child,
                        var value => throw new ArgumentException(
                            $"Unknown presentation route '{value}'. Use hwnd or child.")
                    };
                    break;
                case "--opacity":
                    EnsureValue(args, i, arg);
                    if (!float.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out opacity) ||
                        opacity < 0.1f ||
                        opacity > 1.0f)
                    {
                        throw new ArgumentException("--opacity must be between 0.1 and 1.0.");
                    }
                    break;
                case "--capture":
                    EnsureValue(args, i, arg);
                    capturePath = Path.GetFullPath(args[++i]);
                    break;
                case "--screen-capture":
                    EnsureValue(args, i, arg);
                    screenCapturePath = Path.GetFullPath(args[++i]);
                    break;
                case "--dump-fixture":
                    EnsureValue(args, i, arg);
                    fixtureDumpPath = Path.GetFullPath(args[++i]);
                    break;
                case "--exit-after":
                    EnsureValue(args, i, arg);
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out exitAfter) ||
                        exitAfter < 250 ||
                        exitAfter > 600_000)
                    {
                        throw new ArgumentException("--exit-after must be between 250 and 600000 milliseconds.");
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}'.\n\n{HelpText}");
            }
        }

        return new LabOptions(
            mode,
            live,
            exclude,
            clickThrough,
            presentationRoute,
            opacity,
            capturePath is not null,
            capturePath,
            screenCapturePath,
            fixtureDumpPath,
            exitAfter)
        {
            Help = help
        };
    }

    private static void EnsureValue(string[] args, int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} requires a value.");
        }
    }
}

internal enum LabMode
{
    Glass,
    Cyber
}

internal enum PresentationRoute
{
    Hwnd,
    Child
}
