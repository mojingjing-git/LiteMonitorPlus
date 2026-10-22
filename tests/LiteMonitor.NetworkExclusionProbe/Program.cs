using LiteMonitor.NetworkExclusionProbe.Probes;
using LiteMonitor.NetworkExclusionProbe.Support;

namespace LiteMonitor.NetworkExclusionProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        string command = args[0];
        string outDir = Path.Combine(AppContext.BaseDirectory, "evidence");
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) { outDir = args[++i]; }
        }
        Directory.CreateDirectory(outDir);

        string logPath = Path.Combine(outDir, command + ".log");
        var ev = new Evidence(logPath);
        ev.Log("command=" + command + " outDir=" + outDir);
        ev.Log("administrator=" + ProbeSupport.IsAdministrator());

        int exit;
        try
        {
            switch (command)
            {
                case "env":
                case "capability":
                    exit = await CapabilityProbe.Run(ev, outDir);
                    break;
                case "traffic":
                    exit = await TrafficProbe.Run(ev, outDir);
                    break;
                case "sizelayer":
                    exit = await SizeLayerProbe.Run(ev, outDir);
                    break;
                case "perf":
                    exit = await PerformanceProbe.Run(ev, outDir);
                    break;
                case "perf-control":
                    // 对照组：同样的流量形状，但**不创建 ETW 会话**。
                    exit = await PerformanceProbe.Run(ev, outDir, withEtw: false);
                    break;
                case "calibrate":
                    exit = await CalibrationProbe.Run(ev, outDir);
                    break;
                case "all":
                    exit = await CapabilityProbe.Run(ev, outDir);
                    exit |= await TrafficProbe.Run(ev, outDir);
                    exit |= await SizeLayerProbe.Run(ev, outDir);
                    exit |= await PerformanceProbe.Run(ev, outDir);
                    break;
                default:
                    PrintUsage();
                    return 2;
            }
        }
        catch (Exception ex)
        {
            ev.Log("FATAL " + ProbeSupport.DescribeException(ex));
            ev.Set("fatal", ProbeSupport.DescribeException(ex));
            exit = 1;
        }

        string jsonPath = Path.Combine(outDir, command + ".json");
        ev.WriteJson(jsonPath);
        ev.WriteLog();
        ev.Log("evidence written: " + jsonPath);
        return exit;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("LiteMonitor.NetworkExclusionProbe - #610 IP traffic exclusion N0 feasibility probe");
        Console.WriteLine();
        Console.WriteLine("usage: LiteMonitor.NetworkExclusionProbe <command> [--out <dir>]");
        Console.WriteLine();
        Console.WriteLine("commands:");
        Console.WriteLine("  env         environment, provider, decoder library and session facts");
        Console.WriteLine("  capability  provider enable attempt (admin / non-admin), failure codes, session lifecycle");
        Console.WriteLine("  traffic     controlled TCP/UDP IPv4/IPv6 + multicast traffic, attribution and exclusion");
        Console.WriteLine("  sizelayer   ETW size layer vs native NIC counters in the same window");
        Console.WriteLine("  perf        CPU / allocation / memory / event loss under 4 load shapes");
        Console.WriteLine("  perf-control  same 4 load shapes WITHOUT any ETW session (control group for CPU attribution)");
        Console.WriteLine("  calibrate   locate the provider user data inside the ETW record (zero-copy decode offset)");
        Console.WriteLine("  all         every scenario above");
        Console.WriteLine();
        Console.WriteLine("NOTE: 'capability', 'traffic', 'sizelayer' and 'perf' need an elevated process.");
        Console.WriteLine("      Start elevated with:  Start-Process -Verb RunAs -FilePath <exe> -ArgumentList 'traffic --out <dir>'");
    }
}
