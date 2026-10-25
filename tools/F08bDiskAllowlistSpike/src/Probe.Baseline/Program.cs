namespace F08b.Spike
{
    /// <summary>
    /// 变体入口（Baseline = LHM 0.9.6 / DiskInfoToolkit 1.1.2 —— 当前生产依赖）。
    /// 每个变体一个独立进程，避免同一进程里加载两版 DiskInfoToolkit。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string scenario = args.Length > 0 ? args[0] : "facts";
            return SpikeRunner.Run(scenario, args);
        }
    }
}
