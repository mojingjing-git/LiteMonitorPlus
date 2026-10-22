using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.WebServer;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// #166「CPU 电压」的**显示与兼容**测试：数值格式、单位、来源提示、语言包。
    ///
    /// 覆盖验收硬门槛里的显示类场景：
    ///   1) 1.234 V 在面板 / 任务栏 / 网页上小数、单位、来源完全一致；
    ///   2) 缺失是**缺失状态**（"--"），绝不能显示 0；
    ///   3) 只有 VID 时必须标注为"参考值"，不能冒充实测供电电压；
    ///   4) 用户自定义标签 / 自定义单位都**不能**抹掉来源提示（独立通道）；
    ///   5) 9 个语言包都有必要的文案，且是合法 JSON。
    /// </summary>
    public class CpuVoltagePresentationTests : IDisposable
    {
        private static readonly DateTimeOffset Now =
            new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.FromHours(8));

        private static readonly string[] AllLangs = { "zh", "zh-tw", "en", "ja", "ko", "ru", "de", "es", "fr" };

        public CpuVoltagePresentationTests()
        {
            CpuVoltageSourcePublisher.Clear();
            // ★ 显式加载语言包: 来源文案断言不能依赖"别的测试刚好加载过"（与 GpuGroupTests 同一约定）
            LanguageManager.Load("zh");
        }

        public void Dispose() => CpuVoltageSourcePublisher.Clear();

        // ---------- 解析结果构造辅助 ----------

        private static CpuVoltageResolution Measured(double value)
            => HardwareMonitor.ResolveCpuVoltage(
                new CpuVoltageResolver(),
                new List<CpuVoltageCandidate>
                {
                    new CpuVoltageCandidate
                    {
                        SensorName = "CPU Core",
                        SensorIdentifier = "/intelcpu/0/voltage/0",
                        HardwareIdentifier = "/intelcpu/0",
                        HardwareName = "Intel Core i9",
                        HardwareLevel = CpuVoltageHardwareLevel.Cpu,
                        Value = value,
                        Timestamp = Now,
                    },
                },
                null, 1, Now);

        private static CpuVoltageResolution Vid(double value)
            => HardwareMonitor.ResolveCpuVoltage(
                new CpuVoltageResolver(),
                new List<CpuVoltageCandidate>
                {
                    new CpuVoltageCandidate
                    {
                        SensorName = "Core #1 VID",
                        SensorIdentifier = "/amdcpu/0/voltage/2",
                        HardwareIdentifier = "/amdcpu/0",
                        HardwareName = "AMD Ryzen 7 5800X",
                        HardwareLevel = CpuVoltageHardwareLevel.Cpu,
                        Value = value,
                        Timestamp = Now,
                    },
                },
                null, 1, Now);

        // =========================================================
        // 1) 数值格式：1.234 V → "1.23" + "V"
        // =========================================================

        [Fact]
        public void 一点二三四伏显示为两位小数加伏特()
        {
            CpuVoltageSourcePublisher.Publish(Measured(1.234));

            Assert.Equal("1.23", MetricUtils.GetValueStr("CPU.Voltage", 1.234f));
            Assert.Equal("V", MetricUtils.GetUnitStr("CPU.Voltage", 1.234f, MetricUtils.UnitContext.Panel));
        }

        [Fact]
        public void 面板任务栏与网页的数值单位与来源完全一致()
        {
            CpuVoltageSourcePublisher.Publish(Measured(1.234));

            string panelValue = MetricUtils.GetValueStr("CPU.Voltage", 1.234f, false);
            string panelUnit = MetricUtils.GetUnitStr("CPU.Voltage", 1.234f, MetricUtils.UnitContext.Panel);
            string taskbarValue = MetricUtils.GetValueStr("CPU.Voltage", 1.234f, true);
            string taskbarUnit = MetricUtils.GetUnitStr("CPU.Voltage", 1.234f, MetricUtils.UnitContext.Taskbar);

            // 网页走的是同一套格式化逻辑（面板上下文）
            var state = WebSnapshotValues.ResolveValue(
                "CPU.Voltage", fpsDemand: false, hasFpsData: false,
                readValue: _ => CpuVoltageSourcePublisher.TryGetValue(),
                out string webValue, out float? webRaw);

            Assert.Equal(WebSnapshotValueState.HasData, state);
            Assert.Equal(1.234f, webRaw!.Value, 3);
            Assert.Equal(panelValue, webValue);
            Assert.Equal(panelUnit, MetricUtils.GetUnitStr("CPU.Voltage", webRaw.Value, MetricUtils.UnitContext.Panel));

            // 面板与任务栏的数值一致（单位上下文不同, 但电压单位恒为 V）
            Assert.Equal(panelValue, taskbarValue);
            Assert.Equal("V", panelUnit);
            Assert.Equal("V", taskbarUnit);
        }

        // =========================================================
        // 2) 缺失是缺失状态, 不是 0
        // =========================================================

        [Fact]
        public void 没有电压数据时显示缺失而不是零()
        {
            CpuVoltageSourcePublisher.Clear();

            Assert.Equal("--", MetricUtils.GetValueStr("CPU.Voltage", null));
            Assert.Equal("", MetricUtils.GetUnitStr("CPU.Voltage", null, MetricUtils.UnitContext.Panel));
            Assert.Null(CpuVoltageSourcePublisher.TryGetValue());
        }

        [Fact]
        public void 来源不可用时网页输出缺失占位()
        {
            CpuVoltageSourcePublisher.Clear();

            var state = WebSnapshotValues.ResolveValue(
                "CPU.Voltage", fpsDemand: false, hasFpsData: false,
                readValue: _ => CpuVoltageSourcePublisher.TryGetValue(),
                out string webValue, out float? webRaw);

            Assert.Equal(WebSnapshotValueState.NoData, state);
            Assert.Null(webRaw);
            Assert.Equal("--", webValue);
        }

        // =========================================================
        // 3) VID 必须标注为参考值
        // =========================================================

        [Fact]
        public void 只有VID时单位带上来源标记且提示写明是参考值()
        {
            CpuVoltageSourcePublisher.Publish(Vid(1.55));

            string unit = MetricUtils.GetUnitStr("CPU.Voltage", 1.55f, MetricUtils.UnitContext.Panel);
            Assert.Contains("VID", unit, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("V", unit, StringComparison.Ordinal);

            string hint = MetricUtils.GetCpuVoltageSourceHint();
            Assert.Contains("VID", hint, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("参考值", hint, StringComparison.Ordinal);

            // 数值本身仍然是纯数值, 没有被塞进字符串
            Assert.Equal("1.55", MetricUtils.GetValueStr("CPU.Voltage", 1.55f));
        }

        [Fact]
        public void 实测核心电压不带参考值标记()
        {
            CpuVoltageSourcePublisher.Publish(Measured(1.35));

            string unit = MetricUtils.GetUnitStr("CPU.Voltage", 1.35f, MetricUtils.UnitContext.Panel);
            Assert.Equal("V", unit);
            Assert.DoesNotContain("VID", unit, StringComparison.OrdinalIgnoreCase);
        }

        // =========================================================
        // 4) 来源提示走独立通道：用户标签 / 自定义单位都抹不掉它
        // =========================================================

        [Fact]
        public void 用户自定义标签之后来源提示仍然区分VID()
        {
            CpuVoltageSourcePublisher.Publish(Vid(1.55));

            // 用户把监控项改成了自己的名字
            var item = new MonitorItemConfig { Key = "CPU.Voltage", UserLabel = "我的电压" };
            string userLabel = string.IsNullOrEmpty(item.UserLabel) ? "" : item.UserLabel;
            Assert.Equal("我的电压", userLabel);

            // ★ 来源提示走**独立通道**：与用户标签无关
            string hint = MetricUtils.GetCpuVoltageSourceHint();
            Assert.Contains("VID", hint, StringComparison.OrdinalIgnoreCase);

            string unit = MetricUtils.GetUnitStr("CPU.Voltage", 1.55f, MetricUtils.UnitContext.Panel);
            Assert.Contains("VID", unit, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void 用户自定义单位格式也不能抹掉来源标记()
        {
            CpuVoltageSourcePublisher.Publish(Vid(1.55));

            string unit = MetricUtils.GetUnitStr("CPU.Voltage", 1.55f, MetricUtils.UnitContext.Panel);

            // 自定义单位（不含 {u}）
            Assert.Contains("VID", MetricUtils.GetDisplayUnit("CPU.Voltage", unit, "mV"), StringComparison.OrdinalIgnoreCase);
            // 含 {u} 的自定义单位
            Assert.Contains("VID", MetricUtils.GetDisplayUnit("CPU.Voltage", unit, "{u}!"), StringComparison.OrdinalIgnoreCase);
            // 用户把单位整个隐藏（空串）→ 来源标记仍然在
            Assert.Contains("VID", MetricUtils.GetDisplayUnit("CPU.Voltage", unit, ""), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void 缺失时来源提示是来源不可用()
        {
            CpuVoltageSourcePublisher.Clear();
            string hint = MetricUtils.GetCpuVoltageSourceHint();
            Assert.False(string.IsNullOrEmpty(hint));
            Assert.Contains("不可用", hint, StringComparison.Ordinal);
        }

        [Fact]
        public void 主板Vcore来源的提示披露了刷新速度()
        {
            var resolution = HardwareMonitor.ResolveCpuVoltage(
                new CpuVoltageResolver(),
                new List<CpuVoltageCandidate>
                {
                    new CpuVoltageCandidate
                    {
                        SensorName = "Vcore",
                        SensorIdentifier = "/superio/0/voltage/0",
                        HardwareIdentifier = "/superio/0",
                        HardwareName = "IT87XX",
                        HardwareLevel = CpuVoltageHardwareLevel.SuperIO,
                        Value = 1.28,
                        Timestamp = Now,
                    },
                },
                null, 1, Now);

            CpuVoltageSourcePublisher.Publish(resolution);

            Assert.True(resolution.NeedsMotherboardSlowScan);
            Assert.Equal(3000, CpuVoltageResolver.MotherboardSlowScanIntervalMs);

            string hint = MetricUtils.GetCpuVoltageSourceHint();
            Assert.Contains("Vcore", hint, StringComparison.OrdinalIgnoreCase);
        }

        // =========================================================
        // 5) 语言包
        // =========================================================

        private static string LangPath(string lang)
            => Path.Combine(AppContext.BaseDirectory, "resources", "lang", lang + ".json");

        [Theory]
        [InlineData("zh")]
        [InlineData("zh-tw")]
        [InlineData("en")]
        [InlineData("ja")]
        [InlineData("ko")]
        [InlineData("ru")]
        [InlineData("de")]
        [InlineData("es")]
        [InlineData("fr")]
        public void 九个语言包都有CPU电压来源文案且是合法JSON(string lang)
        {
            string path = LangPath(lang);
            Assert.True(File.Exists(path), "语言包缺失: " + path);

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;

            Assert.True(root.TryGetProperty("CpuVoltage", out JsonElement section), lang + " 缺少 CpuVoltage 节点");
            foreach (string key in new[]
                     {
                         "Auto", "Measured", "MotherboardVcore", "ReferenceOnly",
                         "Ambiguous", "Excluded", "Unavailable", "MarkerReferenceOnly",
                     })
            {
                Assert.True(section.TryGetProperty(key, out JsonElement value), lang + " 缺少 CpuVoltage." + key);
                Assert.False(string.IsNullOrEmpty(value.GetString()), lang + " 的 CpuVoltage." + key + " 是空字符串");
            }

            JsonElement menu = root.GetProperty("Menu");
            Assert.True(menu.TryGetProperty("CpuVoltageSource", out _), lang + " 缺少 Menu.CpuVoltageSource");
            Assert.True(menu.TryGetProperty("CpuVoltageSourceTip", out JsonElement tip), lang + " 缺少 Menu.CpuVoltageSourceTip");
            Assert.False(string.IsNullOrEmpty(tip.GetString()), lang + " 的 Menu.CpuVoltageSourceTip 是空字符串");
        }

        [Fact]
        public void 九个语言包的CPU电压文案键集合完全一致()
        {
            var reference = LoadCpuVoltageKeys("zh");

            foreach (string lang in AllLangs)
            {
                var keys = LoadCpuVoltageKeys(lang);
                var missing = reference.Except(keys, StringComparer.OrdinalIgnoreCase).ToList();
                var extra = keys.Except(reference, StringComparer.OrdinalIgnoreCase).ToList();

                Assert.True(missing.Count == 0, lang + " 缺少 key: " + string.Join(", ", missing));
                Assert.True(extra.Count == 0, lang + " 多出 key: " + string.Join(", ", extra));
            }
        }

        private static HashSet<string> LoadCpuVoltageKeys(string lang)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(LangPath(lang)));
            JsonElement section = doc.RootElement.GetProperty("CpuVoltage");

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty prop in section.EnumerateObject()) keys.Add(prop.Name);
            return keys;
        }
    }
}
