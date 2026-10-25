namespace F08b.Spike
{
    /// <summary>
    /// 变体入口（Latest = LHM 0.9.7-pre771 / DiskInfoToolkit 2.2.2 —— F09a 推荐组合）。
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
