using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiteMonitor.Rendering.Configuration;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// ★ 独立复核：<c>RenderingRoutePolicy.Decide</c> 的"改造前逐位相同"对照夹具。
    /// <para>本文件**故意不引用** <c>WindowIsLayered</c>（它在 23a6c5c 上还不存在），
    /// 因此**同一份源码**可以被复制进 23a6c5c 的 worktree 编译并运行，
    /// 两边产出同一份规范化文本 ⇒ 直接比对字节/SHA-256 即可判定"输出逐位相同"。</para>
    /// <para>输出路径由环境变量 <c>VERIFY2_DUMP_PATH</c> 指定；未设置时只断言、不落盘。</para>
    /// </summary>
    public sealed class VerifyLm045IndependentRenderingDump
    {
        [Fact]
        public void 决策矩阵快照_逐位可比对()
        {
            string dump = BuildDump();

            // 自洽性：同一份输入两次跑出来的文本必须完全一致
            Assert.Equal(dump, BuildDump());

            string path = Environment.GetEnvironmentVariable("VERIFY2_DUMP_PATH");
            if (!string.IsNullOrWhiteSpace(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllText(path, dump, new UTF8Encoding(false));
            }

            // 顺带把 SHA-256 打印出来，便于跨 worktree 人工比对
            using var sha = SHA256.Create();
            string hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(dump))).ToLowerInvariant();
            Console.WriteLine("VERIFY2_DUMP_SHA256=" + hash);
            Console.WriteLine("VERIFY2_DUMP_LENGTH=" + dump.Length);
        }

        private static string BuildDump()
        {
            var sb = new StringBuilder();

            foreach (var cfg in Configurations())
            {
                foreach (bool gpu in new[] { false, true })
                foreach (bool live in new[] { false, true })
                foreach (bool wda in new[] { false, true })
                foreach (bool safe in new[] { false, true })
                {
                    var request = new RenderingRouteRequest(
                        cfg.Value,
                        GpuEffectsEnabled: gpu,
                        LiveBackgroundEnabled: live,
                        WdaCapturePolicyAccepted: wda,
                        SafeMode: safe);

                    RenderingRouteDecision d = RenderingRoutePolicy.Decide(request);

                    sb.Append(cfg.Key).Append('|')
                      .Append(gpu).Append('|')
                      .Append(live).Append('|')
                      .Append(wda).Append('|')
                      .Append(safe).Append('|')
                      .Append(d.Route).Append('|')
                      .Append(d.EffectiveBackend).Append('|')
                      .Append(d.EffectiveMaterial).Append('|')
                      .Append(d.CaptureRequested).Append('|')
                      .Append(d.FallbackCode ?? "<null>").Append('|')
                      .Append(string.Join(";", d.Errors ?? Array.Empty<string>()))
                      .Append('\n');
                }
            }

            return sb.ToString();
        }

        /// <summary>覆盖 null / legacy / 两种 GPU 材质 / 多种非法配置的决策输入矩阵。</summary>
        private static IEnumerable<KeyValuePair<string, ThemeRenderingConfig?>> Configurations()
        {
            yield return new KeyValuePair<string, ThemeRenderingConfig?>("null", null);

            yield return new KeyValuePair<string, ThemeRenderingConfig?>("legacy", new ThemeRenderingConfig
            {
                Backend = ThemeRenderingConfig.LegacyBackend,
                Material = ThemeRenderingConfig.LegacyMaterial
            });

            yield return new KeyValuePair<string, ThemeRenderingConfig?>("liquid_live", Gpu(ThemeRenderingConfig.LiquidGlassMaterial, true));
            yield return new KeyValuePair<string, ThemeRenderingConfig?>("liquid_nolive", Gpu(ThemeRenderingConfig.LiquidGlassMaterial, false));
            yield return new KeyValuePair<string, ThemeRenderingConfig?>("liquid_noliveparam", GpuNoLiveParam());
            yield return new KeyValuePair<string, ThemeRenderingConfig?>("cyber", Gpu(ThemeRenderingConfig.CyberPunkMaterial, false));
            yield return new KeyValuePair<string, ThemeRenderingConfig?>("cyber_live", Gpu(ThemeRenderingConfig.CyberPunkMaterial, true));
            yield return new KeyValuePair<string, ThemeRenderingConfig?>("bad_version", BadVersion());
            yield return new KeyValuePair<string, ThemeRenderingConfig?>("bad_backend", BadBackend());
            yield return new KeyValuePair<string, ThemeRenderingConfig?>("bad_material", BadMaterial());
            yield return new KeyValuePair<string, ThemeRenderingConfig?>("bad_number", BadNumber());
            yield return new KeyValuePair<string, ThemeRenderingConfig?>("gpu_legacy_material", GpuLegacyMaterial());
        }

        private static ThemeRenderingConfig Gpu(string material, bool liveBackground) =>
            new ThemeRenderingConfig
            {
                Backend = ThemeRenderingConfig.GpuBackend,
                Material = material,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["liveBackground"] = JsonSerializer.SerializeToElement(liveBackground),
                    ["cornerRadius"] = JsonSerializer.SerializeToElement(24),
                    ["primaryCut"] = JsonSerializer.SerializeToElement(14)
                }
            };

        private static ThemeRenderingConfig GpuNoLiveParam() =>
            new ThemeRenderingConfig
            {
                Backend = ThemeRenderingConfig.GpuBackend,
                Material = ThemeRenderingConfig.LiquidGlassMaterial,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["cornerRadius"] = JsonSerializer.SerializeToElement(24)
                }
            };

        private static ThemeRenderingConfig BadVersion()
        {
            var c = Gpu(ThemeRenderingConfig.LiquidGlassMaterial, true);
            c.Version = 99;
            return c;
        }

        private static ThemeRenderingConfig BadBackend()
        {
            var c = Gpu(ThemeRenderingConfig.LiquidGlassMaterial, true);
            c.Backend = "not-a-backend";
            return c;
        }

        private static ThemeRenderingConfig BadMaterial()
        {
            var c = Gpu(ThemeRenderingConfig.LiquidGlassMaterial, true);
            c.Material = "not-a-material";
            return c;
        }

        private static ThemeRenderingConfig BadNumber()
        {
            var c = Gpu(ThemeRenderingConfig.LiquidGlassMaterial, true);
            c.Parameters["cornerRadius"] = JsonSerializer.SerializeToElement(999);
            return c;
        }

        private static ThemeRenderingConfig GpuLegacyMaterial() =>
            new ThemeRenderingConfig
            {
                Backend = ThemeRenderingConfig.GpuBackend,
                Material = ThemeRenderingConfig.LegacyMaterial,
                Parameters = new Dictionary<string, JsonElement>()
            };
    }
}