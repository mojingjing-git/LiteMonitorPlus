using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteMonitor.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// P-B4-1：纯格式化编排器（MetricItemFormatter）行为钉子。
    /// 期望值口径 = 改造前 MetricItem.GetFormattedText 的既有分支语义（fixture 22 例同源）。
    /// 解析委托全部用假实现——证明编排器可在无全局服务、无窗体、无硬件时执行（Plan B §6.2 检查4）。
    /// </summary>
    public class MetricItemFormatterTests
    {
        private sealed class FakeResolvers
        {
            public List<string> Calls = new();
            public MetricFormatResolvers Build(
                Func<string, float?, bool, string?, string>? value = null,
                Func<string, float?, MetricFormatContext, string>? unit = null) => new(
                GetValueStr: (k, v, c, f) => { Calls.Add($"value:{k}:{v}:{c}:{f}"); return value?.Invoke(k, v, c, f) ?? (v?.ToString("0.0") ?? "--"); },
                GetUnitStr: (k, v, ctx) => { Calls.Add($"unit:{k}:{ctx}"); return unit?.Invoke(k, v, ctx) ?? "%"; },
                GetDisplayUnit: (k, u, f) => { Calls.Add($"display:{k}:{u}:{f}"); return string.IsNullOrEmpty(f) ? u : f; },
                GetState: (k, v) => { Calls.Add($"state:{k}:{v}"); return v >= 80 ? 2 : v >= 60 ? 1 : 0; },
                GetProgressValue: (k, v) => { Calls.Add($"progress:{k}"); return v; });
        }

        private static MetricFormatterState Prev() => new(); // 全默认：CachedDisplayValue=-99999 ⇒ 首调必刷新

        [Fact]
        public void 首次调用_刷新缓存_组装双上下文文本()
        {
            var fake = new FakeResolvers();
            var result = MetricItemFormatter.Format("CPU.Load", 42f, null, isHorizontal: false,
                userFormatPanel: null, userFormatTaskbar: null, pluginColorOverride: null,
                currentPower: (true, false), prev: Prev(), r: fake.Build());

            Assert.True(result.Text == "42.0%", "calls=[" + string.Join("|", fake.Calls) + "] text=" + result.Text); // Panel = 值+单位
            Assert.Equal(42f, result.State.CachedDisplayValue);
            Assert.Equal("42.0%", result.State.CachedNormalText);
            Assert.Equal("42.0%", result.State.CachedHorizontalText); // Taskbar 同文本（假单位 %）
            Assert.Equal("42.0", result.State.CachedValueText);       // Panel 上下文：纯值部分（单位在 CachedUnitText 分离暴露）
            Assert.Equal("%", result.State.CachedUnitText);
            Assert.False(result.State.HasCustomUnit);
            Assert.Equal(0, result.State.CachedColorState);           // 42 < 60 ⇒ state 0
        }

        [Fact]
        public void 缓存阈值_0点005以内不刷新_超过才刷新()
        {
            var fake = new FakeResolvers();
            var state = MetricItemFormatter.Format("CPU.Load", 42f, null, false, null, null, null, (true, false), Prev(), fake.Build()).State;
            fake.Calls.Clear();

            // Δ=0.004 ≤ 0.005：不刷新（无 value 解析调用），返回缓存文本
            var r1 = MetricItemFormatter.Format("CPU.Load", 42.004f, null, false, null, null, null, (true, false), state, fake.Build());
            Assert.DoesNotContain(fake.Calls, c => c.StartsWith("value:"));
            Assert.Equal("42.0%", r1.Text);
            Assert.Equal(state.CachedDisplayValue, r1.State.CachedDisplayValue);

            // Δ=0.006 > 0.005：刷新
            fake.Calls.Clear();
            var r2 = MetricItemFormatter.Format("CPU.Load", 42.006f, null, false, null, null, null, (true, false), state, fake.Build());
            Assert.Contains(fake.Calls, c => c.StartsWith("value:"));
            Assert.Equal(42.006f, r2.State.CachedDisplayValue);
        }

        [Fact]
        public void 电池项_电源态变化_强制刷新()
        {
            var fake = new FakeResolvers();
            var state = MetricItemFormatter.Format("BAT.Percent", 80f, null, false, null, null, null, (true, false), Prev(), fake.Build()).State;
            fake.Calls.Clear();

            // Δ=0（数值没变）但充电状态翻转 ⇒ 必须刷新
            fake.Calls.Clear();
            var r = MetricItemFormatter.Format("BAT.Percent", 80f, null, false, null, null, null, (false, false), state, fake.Build());
            Assert.Contains(fake.Calls, c => c.StartsWith("value:"));
            Assert.Equal((false, false), r.State.CachedPowerState);

            // 非电池项电源态变化不触发刷新
            fake.Calls.Clear();
            var state2 = MetricItemFormatter.Format("CPU.Load", 50f, null, false, null, null, null, (true, false), Prev(), fake.Build()).State;
            fake.Calls.Clear();
            var r2 = MetricItemFormatter.Format("CPU.Load", 50f, null, false, null, null, null, (false, false), state2, fake.Build());
            Assert.DoesNotContain(fake.Calls, c => c.StartsWith("value:"));
        }

        [Fact]
        public void TextValue路径_智能补单位_已含后缀不重复()
        {
            var fake = new FakeResolvers();

            // 未包含单位 → 拼接
            var r1 = MetricItemFormatter.Format("DASH.X", 0f, "RPM", isHorizontal: false,
                userFormatPanel: null, userFormatTaskbar: null, pluginColorOverride: 0,
                currentPower: (true, false), prev: Prev(), r: fake.Build(unit: (k, v, c) => " RPM"));
            Assert.Equal("RPM RPM", r1.Text); // 假单位 " RPM" 未被 EndsWith 命中（"RPM" 不以 " RPM" 结尾）
            // 已包含单位 → 原样
            var r2 = MetricItemFormatter.Format("DASH.X", 0f, "12 RPM", isHorizontal: false,
                userFormatPanel: null, userFormatTaskbar: null, pluginColorOverride: 0,
                currentPower: (true, false), prev: Prev(), r: fake.Build(unit: (k, v, c) => " RPM"));
            Assert.Equal("12 RPM", r2.Text);
        }

        [Fact]
        public void 插件色优先_非插件走阈值_数值路径()
        {
            var fake = new FakeResolvers();

            // 插件项：override=2 ⇒ 颜色 2（即使值 42 按阈值应为 0）；GetState 不被调用
            var r1 = MetricItemFormatter.Format("DASH.X", 42f, null, false, null, null, pluginColorOverride: 2, (true, false), Prev(), fake.Build());
            Assert.Equal(2, r1.State.CachedColorState);
            Assert.DoesNotContain(fake.Calls, c => c.StartsWith("state:"));

            // 插件项无覆盖（0）⇒ 保持 0 且不查阈值
            fake.Calls.Clear();
            var r2 = MetricItemFormatter.Format("DASH.X", 95f, null, false, null, null, pluginColorOverride: 0, (true, false), Prev(), fake.Build());
            Assert.Equal(0, r2.State.CachedColorState);
            Assert.DoesNotContain(fake.Calls, c => c.StartsWith("state:"));

            // 非插件 ⇒ GetState 决定（95 ≥ 80 ⇒ 2）
            var r3 = MetricItemFormatter.Format("CPU.Load", 95f, null, false, null, null, pluginColorOverride: null, (true, false), Prev(), fake.Build());
            Assert.Equal(2, r3.State.CachedColorState);
        }

        [Fact]
        public void 自定义任务栏格式_不做数值压缩_双上下文格式独立()
        {
            var seen = new List<(bool Compact, string? Fmt)>();
            var resolvers = new MetricFormatResolvers(
                GetValueStr: (k, v, compact, fmt) => { seen.Add((compact, fmt)); return v?.ToString("0.00") ?? "--"; },
                GetUnitStr: (k, v, ctx) => ctx == MetricFormatContext.Taskbar ? "C" : "°C",
                GetDisplayUnit: (k, u, f) => string.IsNullOrEmpty(f) ? u : f,
                GetState: (k, v) => 0,
                GetProgressValue: (k, v) => v);

            var r = MetricItemFormatter.Format("GPU.Temp", 65f, null, isHorizontal: true,
                userFormatPanel: null, userFormatTaskbar: "K", pluginColorOverride: null,
                currentPower: (true, false), prev: Prev(), r: resolvers);

            Assert.Equal("65.00K", r.Text);                            // Taskbar 文本用自定义格式 K
            Assert.Equal("65.00°C", r.State.CachedNormalText);          // Panel 缓存仍是默认单位
            Assert.True(r.State.HasCustomUnit);
            Assert.Contains(seen, s => !s.Compact && s.Fmt == null);    // Panel 侧恒 compact=false（原逻辑）
            Assert.Contains(seen, s => !s.Compact && s.Fmt == "K");     // Taskbar 侧自定义格式同样不压缩
        }
    }
}

// 文件级第二测试类：源码护栏（Plan B §6.2 检查4 的可执行化）
namespace LiteMonitor.Tests
{
    using Xunit;

    /// <summary>
    /// B4-1 源码级护栏：纯规则文件不得引用全局服务（同 WebSamplingDemandTests 的源码扫描手法）。
    /// 变异：往 MetricItemFormatter/MetricItem 加回 InfoService.Instance/Settings.Load ⇒ 本类必须红。
    /// </summary>
    public class B4PureRulesGuardTests
    {
                private static readonly System.Text.RegularExpressions.Regex CommentRx = new(
            @"/\*([\s\S]*?)\*/|//.*?(\r?\n|$)",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Singleline);

        private static string StripComments(string text)
            => CommentRx.Replace(text, m => m.Value.StartsWith("//")
                ? (m.Value.EndsWith("\n") || m.Value.EndsWith("\r") ? "\n" : "")
                : " ");

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LiteMonitor.csproj"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
        }

        [Fact]
        public void 纯编排器_零全局服务引用()
        {
            string code = StripComments(File.ReadAllText(RepoFile("src", "Core", "MetricItemFormatter.cs")));
            Assert.DoesNotContain("InfoService", code);
            Assert.DoesNotContain("Settings.Load", code);
            Assert.DoesNotContain("LanguageManager", code);
            Assert.DoesNotContain("CpuVoltageSourcePublisher", code);
        }

        [Fact]
        public void MetricItem_适配层_不直接查询全局服务()
        {
            string code = StripComments(File.ReadAllText(RepoFile("src", "Core", "MetricItem.cs")));
            Assert.DoesNotContain("InfoService", code);        // 插件色改经组合根委托
            Assert.DoesNotContain("Settings.Load", code);      // 配置兜底改经组合根委托
        }

        [Fact]
        public void 组合根_已接线两个默认委托()
        {
            string code = File.ReadAllText(RepoFile("src", "UI", "UIController.cs"));
            Assert.Contains("MetricItem.DefaultPluginColorResolver ??=", code);
            Assert.Contains("MetricItem.DefaultConfigLookup ??=", code);
        }
    }
}
