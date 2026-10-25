using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibreHardwareMonitor.Hardware;

// ─────────────────────────────────────────────────────────────────────────────
// LhmIntelReport —— 导出 LHM 的显卡报告（上游 issue #592 取证工具，P-F15）
//
// 用法: LhmIntelReport --out <输出目录> [--verbose]
//
// 产出：
//   lhm-report.txt   人读的完整报告（含每个显卡的 GetReport() 原文）
//   lhm-report.json  机器读的结构化结果
//   sensors.csv      全部传感器的 名称 / 类型 / 值 / 文本值
//
// ★ 本工具**不下结论**：它只导出 LHM 看到的原始数据。
//   "Intel GCL 是否初始化成功"、"#592 是否成立" 由人判读。
//
// ★ 关键点（#592 的判据）：
//   区分「传感器键不存在」与「键存在但值恒 null」——
//   前者在 LHM 报告里根本不会出现该传感器名，后者会出现且 Value 为 null。
//
// ★ API 口径（LibreHardwareMonitorLib 0.9.6，已核对 ref/net8.0 的公开成员）：
//   Computer      —— 不是 IDisposable；有 Open()/Close()，**没有** Groups 属性
//   IHardware     —— Hardware / HardwareType / Identifier / Name / Parent / Sensors / SubHardware / GetReport() / Update()
//   IGroup        —— Hardware / GetReport() / Close()
//   ISensor       —— Name / SensorType / Value / Min / Max / Values（**没有** Type / ValueText）
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class SensorRow
{
    [JsonPropertyName("hardware")] public string Hardware { get; set; } = "";
    [JsonPropertyName("hardwareType")] public string HardwareType { get; set; } = "";
    [JsonPropertyName("identifier")] public string Identifier { get; set; } = "";
    [JsonPropertyName("sensor")] public string Sensor { get; set; } = "";
    [JsonPropertyName("sensorType")] public string SensorType { get; set; } = "";
    [JsonPropertyName("value")] public double? Value { get; set; }
    [JsonPropertyName("valueText")] public string ValueText { get; set; } = "";
}

internal sealed class GpuEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("hardwareType")] public string HardwareType { get; set; } = "";
    [JsonPropertyName("identifier")] public string Identifier { get; set; } = "";
    [JsonPropertyName("groupName")] public string GroupName { get; set; } = "";
    [JsonPropertyName("isIntel")] public bool IsIntel { get; set; }
    [JsonPropertyName("updateError")] public string? UpdateError { get; set; }
    [JsonPropertyName("report")] public string Report { get; set; } = "";
    [JsonPropertyName("gclMentions")] public List<string> GclMentions { get; set; } = new();
    [JsonPropertyName("gclInitializedValue")] public string? GclInitializedValue { get; set; }
    [JsonPropertyName("sensorCount")] public int SensorCount { get; set; }
    [JsonPropertyName("nullValuedSensors")] public List<string> NullValuedSensors { get; set; } = new();
    [JsonPropertyName("groupReport")] public string? GroupReport { get; set; }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        // ★ 子进程的标准输出被父脚本重定向读取；.NET 在重定向时按 UTF-8 解码，
        //   而本机控制台默认代码页是 GBK ⇒ 不显式改成 UTF-8 会出现乱码。
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 某些宿主不允许改 */ }

        string outDir = ".";
        bool verbose = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) { outDir = args[++i]; }
            else if (args[i] == "--verbose") { verbose = true; }
            else if (args[i] == "--help" || args[i] == "-h")
            {
                Console.WriteLine("usage: LhmIntelReport --out <dir> [--verbose]");
                return 0;
            }
        }

        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"无法创建输出目录 {outDir}: {ex.Message}");
            return 3;
        }

        var sb = new StringBuilder();
        sb.AppendLine("LiteMonitor++ LHM 显卡报告（取证导出，非结论）");
        sb.AppendLine($"生成时间(UTC) : {DateTime.UtcNow.ToString("o")}");
        sb.AppendLine($"机器           : {Environment.MachineName}");
        sb.AppendLine($"OS             : {Environment.OSVersion}");
        sb.AppendLine($"LHM 程序集     : {typeof(Computer).Assembly.GetName().Version}");
        sb.AppendLine("");

        var gpus = new List<GpuEntry>();
        var sensors = new List<SensorRow>();
        string? openError = null;
        bool intelPresent = false;
        string? computerReport = null;

        Computer? computer = null;
        try
        {
            computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false,
                IsControllerEnabled = false,
            };

            sb.AppendLine("正在打开 LHM 硬件树（可能需要管理员权限 / PawnIO 驱动）…");
            computer.Open();
            sb.AppendLine("LHM 硬件树已打开。");
            sb.AppendLine("");

            try
            {
                computerReport = computer.GetReport();
                sb.AppendLine("── 整机报告（IComputer.GetReport）──");
                sb.AppendLine("★ 备注：LHM 0.9.6 的 Computer **没有公开 Groups 属性**，");
                sb.AppendLine("  因此「分组级 GetReport()」取不到；这里用**整机报告**作为等价替代，");
                sb.AppendLine("  它包含全部分组的传感器树。");
                sb.AppendLine();
                sb.AppendLine(computerReport);
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"<整机报告获取失败: {ex.GetType().Name}: {ex.Message}>");
                sb.AppendLine();
            }

            foreach (var hw in computer.Hardware)
            {
                var hwType = hw.HardwareType.ToString();
                if (!hwType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase)) { continue; }

                var entry = new GpuEntry
                {
                    Name = hw.Name,
                    HardwareType = hwType,
                    Identifier = hw.Identifier?.ToString() ?? "",
                    GroupName = hw.Parent?.GetType().Name ?? "",
                    IsIntel = hwType.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                              || hw.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                              || hw.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase),
                };
                if (entry.IsIntel) { intelPresent = true; }

                try { hw.Update(); }
                catch (Exception ex) { entry.UpdateError = $"{ex.GetType().Name}: {ex.Message}"; }

                try { entry.Report = hw.GetReport() ?? ""; }
                catch (Exception ex) { entry.Report = $"<GetReport 失败: {ex.GetType().Name}: {ex.Message}>"; }

                foreach (var line in entry.Report.Split('\n'))
                {
                    if (line.Contains("GCL", StringComparison.OrdinalIgnoreCase))
                    {
                        entry.GclMentions.Add(line.Trim());
                    }
                }

                // 解析 "Intel GCL Initialized: True/False" 一类字段（不同 LHM 版本文案可能不同）
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(
                             entry.Report, @"(?i)gcl[^:=\r\n]*[:=]\s*(true|false|yes|no|\d+)"))
                {
                    entry.GclInitializedValue = m.Groups[1].Value;
                }

                try
                {
                    foreach (var sensor in hw.Sensors)
                    {
                        sensors.Add(new SensorRow
                        {
                            Hardware = hw.Name,
                            HardwareType = hwType,
                            Identifier = entry.Identifier,
                            Sensor = sensor.Name,
                            SensorType = sensor.SensorType.ToString(),
                            Value = sensor.Value,
                            ValueText = FormatValue(sensor.SensorType, sensor.Value),
                        });
                        entry.SensorCount++;
                        // ★ #592 的关键区分：键存在但值恒 null
                        if (sensor.Value is null) { entry.NullValuedSensors.Add(sensor.Name); }
                    }
                }
                catch (Exception ex)
                {
                    entry.Report += $"\n<枚举传感器失败: {ex.GetType().Name}: {ex.Message}>";
                }

                // 分组级报告（IGroup.GetReport）
                try
                {
                    if (hw.Parent != null) { entry.GroupReport = hw.Parent.GetReport(); }
                }
                catch (Exception ex)
                {
                    entry.GroupReport = $"<分组 GetReport 失败: {ex.GetType().Name}: {ex.Message}>";
                }

                gpus.Add(entry);
            }
        }
        catch (Exception ex)
        {
            openError = $"{ex.GetType().Name}: {ex.Message}";
            sb.AppendLine("★ LHM 打开硬件树失败：");
            sb.AppendLine(openError);
            sb.AppendLine();
            sb.AppendLine("常见原因：");
            sb.AppendLine("  · 没有管理员权限（LHM 的部分传感器需要）");
            sb.AppendLine("  · PawnIO / Ring0 驱动未安装（LiteMonitor++ 启动时会检查）");
            sb.AppendLine("  · 机器上确实没有显卡");
        }
        finally
        {
            try { computer?.Close(); } catch { /* 关不掉也不能影响取证输出 */ }
        }

        // ── 输出 ────────────────────────────────────────────────────────────
        // GCL 字样可能出现在**整机报告**里（而不是某个硬件的 GetReport 里），两边都扫。
        var computerGclLines = new List<string>();
        if (!string.IsNullOrEmpty(computerReport))
        {
            foreach (var line in computerReport.Split('\n'))
            {
                if (line.Contains("GCL", StringComparison.OrdinalIgnoreCase))
                {
                    computerGclLines.Add(line.Trim());
                }
            }
        }

        sb.AppendLine("── 显卡明细 ──");
        if (gpus.Count == 0)
        {
            sb.AppendLine("（没有枚举到任何显卡硬件）");
        }
        foreach (var g in gpus)
        {
            sb.AppendLine($"[{g.HardwareType}] {g.Name}  ({g.Identifier})  分组={g.GroupName}  Intel={g.IsIntel}");
            if (g.UpdateError != null) { sb.AppendLine($"  Update() 失败: {g.UpdateError}"); }
            sb.AppendLine($"  传感器数: {g.SensorCount}   值为 null 的传感器: {g.NullValuedSensors.Count}");
            if (g.NullValuedSensors.Count > 0)
            {
                sb.AppendLine("    · " + string.Join(", ", g.NullValuedSensors));
            }
            sb.AppendLine($"  GCL 提及: {g.GclMentions.Count} 条" + (g.GclInitializedValue != null ? $"（值: {g.GclInitializedValue}）" : ""));
            foreach (var line in g.GclMentions) { sb.AppendLine("    > " + line); }
            sb.AppendLine("  ---- GetReport() 原文 ----");
            sb.AppendLine(g.Report);
            sb.AppendLine();
        }

        sb.AppendLine("── 全部传感器 ──");
        sb.AppendLine("硬件\t类型\t传感器\t值\t文本值");
        foreach (var s in sensors)
        {
            sb.AppendLine($"{s.Hardware}\t{s.SensorType}\t{s.Sensor}\t{(s.Value?.ToString() ?? "<null>")}\t{s.ValueText}");
        }

        sb.AppendLine();
        sb.AppendLine("── GCL 字样汇总 ──");
        sb.AppendLine($"整机报告中的 GCL 行: {computerGclLines.Count}");
        foreach (var line in computerGclLines) { sb.AppendLine("  > " + line); }
        foreach (var g in gpus)
        {
            sb.AppendLine($"{g.Name} 的 GCL 行: {g.GclMentions.Count}");
            foreach (var line in g.GclMentions) { sb.AppendLine("  > " + line); }
        }
        if (computerGclLines.Count == 0 && gpus.Sum(g => g.GclMentions.Count) == 0)
        {
            sb.AppendLine("（本 LHM 版本（" + typeof(Computer).Assembly.GetName().Version +
                          "）的输出里没有任何 GCL 字样 —— 这只说明**该版本不打印这一项**，");
            sb.AppendLine("  不能据此判定「GCL 未初始化」。判据请见 tools/forensics/B580-CHECKLIST.md §3.1。）");
        }

        var json = new
        {
            schemaVersion = 1,
            tool = "tools/forensics/LhmIntelReport",
            disclaimer = "本文件是 LHM 看到的原始数据，不含任何根因判定。",
            collectedAtUtc = DateTime.UtcNow.ToString("o"),
            machine = Environment.MachineName,
            os = Environment.OSVersion.ToString(),
            lhmVersion = typeof(Computer).Assembly.GetName().Version?.ToString(),
            openError,
            intelGpuPresent = intelPresent,
            gpuCount = gpus.Count,
            computerReport,
            computerGclMentions = computerGclLines,
            groupReportUnavailableNote = "LibreHardwareMonitorLib 0.9.6 的 Computer 未公开 Groups 属性 ⇒ 分组级 GetReport() 不可得；已改用 IComputer.GetReport()（整机报告，含全部分组传感器树）作为等价替代。",
            gpus,
            sensors,
        };

        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(outDir, "lhm-report.json"),
            JsonSerializer.Serialize(json, opts), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "lhm-report.txt"),
            sb.ToString(), new UTF8Encoding(false));

        if (sensors.Count > 0)
        {
            var csv = new StringBuilder("hardware,hardwareType,identifier,sensor,sensorType,value,valueText\n");
            foreach (var s in sensors)
            {
                csv.Append(Csv(s.Hardware)).Append(',')
                   .Append(Csv(s.HardwareType)).Append(',')
                   .Append(Csv(s.Identifier)).Append(',')
                   .Append(Csv(s.Sensor)).Append(',')
                   .Append(Csv(s.SensorType)).Append(',')
                   .Append(s.Value?.ToString() ?? "").Append(',')
                   .Append(Csv(s.ValueText)).Append('\n');
            }
            File.WriteAllText(Path.Combine(outDir, "sensors.csv"), csv.ToString(), new UTF8Encoding(false));
        }

        Console.WriteLine($"输出目录      : {Path.GetFullPath(outDir)}");
        Console.WriteLine($"显卡数        : {gpus.Count}");
        Console.WriteLine($"检测到 Intel  : {(intelPresent ? "是" : "否")}");
        Console.WriteLine($"GCL 提及条数  : {gpus.Sum(g => g.GclMentions.Count)}");
        if (openError != null)
        {
            Console.Error.WriteLine($"★ LHM 打开失败：{openError}");
            return 2;
        }
        if (verbose)
        {
            foreach (var g in gpus)
            {
                Console.WriteLine($"  [{g.HardwareType}] {g.Name} sensors={g.SensorCount} nullValued={g.NullValuedSensors.Count} gcl={g.GclMentions.Count}");
            }
        }
        return 0;
    }

    /// <summary>
    /// ISensor 没有 ValueText（LHM 0.9.6 公开成员里没有），这里按 SensorType 补一个单位。
    /// ★ 只是**显示格式**，不是判据。
    /// </summary>
    private static string FormatValue(SensorType type, float? value)
    {
        if (value is null) { return "<null>"; }
        string unit = type switch
        {
            SensorType.Temperature => " °C",
            SensorType.Load => " %",
            SensorType.Clock => " MHz",
            SensorType.Power => " W",
            SensorType.Data => " GB",
            SensorType.Fan => " RPM",
            SensorType.Voltage => " V",
            SensorType.Current => " A",
            SensorType.Frequency => " MHz",
            SensorType.Level => " %",
            SensorType.Factor => "",
            SensorType.Throughput => " B/s",
            _ => "",
        };
        return value.Value.ToString("0.##") + unit;
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) { return ""; }
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }
}
