using System.Globalization;

namespace AppearanceMaterialLab;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
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
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(LabOptions.HelpText);
            return 0;
        }

        using LabForm form = new(options);
        Application.Run(form);

        if (form.FatalError is not null)
        {
            string errorPath = options.CapturePath is not null
                ? Path.ChangeExtension(options.CapturePath, ".error.txt")
                : Path.Combine(AppContext.BaseDirectory, "GlassToolbarPreview.error.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(errorPath) ?? ".");
            File.WriteAllText(errorPath, form.FatalError + Environment.NewLine);
            Console.Error.WriteLine(form.FatalError);
            return 1;
        }

        return 0;
    }
}

internal sealed record LabOptions(
    string? CapturePath,
    int ExitAfterMilliseconds)
{
    public const string HelpText =
        "GlassToolbarPreview\n\n" +
        "--capture PATH   Save the rendered 1280x900 GPU backbuffer as PNG.\n" +
        "--exit-after MS  Close after a bounded duration (250..600000 ms).\n" +
        "--diagnostic MODE  legacy, legacy-no-shadow, no-shadow, or no-refraction.\n" +
        "--help           Show this text.";

    public bool Help { get; init; }
    public string Diagnostic { get; init; } = "current";

    public static LabOptions Parse(string[] args)
    {
        string? capturePath = null;
        int exitAfter = 0;
        bool help = false;
        string diagnostic = "current";

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
                case "--capture":
                    EnsureValue(args, i, arg);
                    capturePath = Path.GetFullPath(args[++i]);
                    break;
                case "--diagnostic":
                    EnsureValue(args, i, arg);
                    diagnostic = args[++i].ToLowerInvariant();
                    if (diagnostic is not ("legacy" or "legacy-no-shadow" or "no-shadow" or "no-refraction"))
                        throw new ArgumentException("Unknown diagnostic mode.");
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

        return new LabOptions(capturePath, exitAfter)
        {
            Help = help,
            Diagnostic = diagnostic
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
