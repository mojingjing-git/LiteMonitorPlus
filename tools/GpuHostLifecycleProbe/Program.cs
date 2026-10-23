using System.Text.Json;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Presentation;
using LiteMonitor.Rendering.Scene;

namespace GpuHostLifecycleProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? output = null;
        string scenario = "lifecycle";
        bool topmost = false;
        bool clickThrough = false;
        int timeoutMs = 50000;

        foreach (string argument in args)
        {
            if (argument.StartsWith("--scenario=", StringComparison.Ordinal))
            {
                scenario = argument["--scenario=".Length..];
            }
            else if (argument == "--topmost")
            {
                topmost = true;
            }
            else if (argument == "--click-through")
            {
                clickThrough = true;
            }
            else if (argument.StartsWith("--timeout-ms=", StringComparison.Ordinal))
            {
                if (!int.TryParse(argument["--timeout-ms=".Length..], out timeoutMs))
                {
                    return Fail(output, $"Invalid --timeout-ms value in '{argument}'.");
                }
            }
            else if (!argument.StartsWith("--", StringComparison.Ordinal) && output is null)
            {
                output = argument;
            }
            else
            {
                return Fail(output, $"Unknown argument '{argument}'.");
            }
        }

        output ??= Path.Combine(AppContext.BaseDirectory, "evidence");
        Directory.CreateDirectory(output);
        ApplicationConfiguration.Initialize();

        // A probe must die rather than block on a modal crash dialog: the
        // external watchdog has to stay the only authority over the deadline.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.ThreadException += (sender, e) =>
        {
            File.WriteAllText(
                Path.Combine(output, "probe-args-error.log"),
                e.Exception.ToString());
            Environment.Exit(70);
        };

        File.WriteAllText(
            Path.Combine(output, "probe-args.json"),
            JsonSerializer.Serialize(
                new
                {
                    scenario,
                    topmost,
                    clickThrough,
                    timeoutMs,
                    baseDirectory = AppContext.BaseDirectory,
                    processId = Environment.ProcessId,
                    startedAt = DateTimeOffset.Now
                },
                new JsonSerializerOptions { WriteIndented = true }));

        CandidateHashGuard? guard = CandidateHashGuard.TryCreate(AppContext.BaseDirectory);

        switch (scenario)
        {
            case "lifecycle":
            {
                using ProbeForm form = new(output);
                Application.Run(form);
                return form.ExitCode;
            }

            case "s1":
            case "s2":
            {
                RecreateScenario kind = scenario == "s1"
                    ? RecreateScenario.S1FormalRenderer
                    : RecreateScenario.S2MainFormShape;
                using RecreateHandleScenarioForm form = new(output, kind, topmost, clickThrough, timeoutMs);
                GdiRecoveryExperiment experiment = new(form, form.RecreateHandleForProbe, output, scenario, guard);
                form.Experiment = experiment;
                Application.Run(form);
                return form.ExitCode;
            }

            case "mainform":
                return RealMainFormScenario.Run(output, timeoutMs, guard);

            case "startup":
                // ★ R01：只验证"生产 MainForm 能不能构造出来"，刻意不跑消息循环。
                //   退出码契约：0 = 构造成功；1 = 构造抛异常；2 = 环境不具备条件。
                return RealMainFormScenario.RunStartupCheck(output);

            default:
                return Fail(
                    output,
                    $"Unknown scenario '{scenario}'. Known scenarios: lifecycle, s1, s2, mainform, startup.");
        }
    }

    private static int Fail(string? output, string message)
    {
        string target = output ?? Path.Combine(AppContext.BaseDirectory, "evidence");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "probe-args-error.log"), message);
        return 64;
    }
}
