using System;
using System.IO;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// <see cref="ThemeManager.IsGpuTheme"/> 的容错回归（R3 §4.2-1/2）。
    /// <para>★ helper 必须 <b>JsonDocument 轻读</b>（不得调用 <see cref="ThemeManager.Load"/>，
    /// 那会换掉 <see cref="ThemeManager.Current"/>）；坏 JSON / 缺文件 / 缺键一律归类 legacy，
    /// 不得抛到 UI 层。</para>
    /// <para>测试文件写入测试输出目录的 <c>resources/themes/</c>（<see cref="ThemeManager.ThemeDir"/>
    /// 基于 AppContext.BaseDirectory），用例后清理；文件名用 zz- 前缀避免与真实主题冲突。</para>
    /// </summary>
    public class ThemeRouteHelperTests : IDisposable
    {
        private const string Prefix = "zz-routetest-";

        private static string ThemePath(string name) =>
            Path.Combine(ThemeManager.ThemeDir, name + ".json");

        public void Dispose()
        {
            foreach (string file in Directory.GetFiles(ThemeManager.ThemeDir, Prefix + "*.json"))
            {
                try { File.Delete(file); } catch { /* 清理失败不影响判定 */ }
            }
        }

        [Fact]
        public void GpuBackendReturnsTrue()
        {
            File.WriteAllText(ThemePath(Prefix + "gpu"),
                """{ "name": "t", "rendering": { "version": 1, "backend": "gpu", "material": "liquid_glass" } }""");
            Assert.True(ThemeManager.IsGpuTheme(Prefix + "gpu"));
        }

        [Fact]
        public void GpuBackendIsCaseInsensitive()
        {
            File.WriteAllText(ThemePath(Prefix + "upper"),
                """{ "name": "t", "rendering": { "backend": "GPU" } }""");
            Assert.True(ThemeManager.IsGpuTheme(Prefix + "upper"));
        }

        [Fact]
        public void LegacyBackendReturnsFalse()
        {
            File.WriteAllText(ThemePath(Prefix + "legacy"),
                """{ "name": "t", "rendering": { "backend": "legacy", "material": "legacy" } }""");
            Assert.False(ThemeManager.IsGpuTheme(Prefix + "legacy"));
        }

        [Fact]
        public void MissingRenderingBlockReturnsFalse()
        {
            // 绝大多数存量主题根本没有 rendering 块 ⇒ 必须归类 legacy。
            File.WriteAllText(ThemePath(Prefix + "nogpu"),
                """{ "name": "t", "color": { "background": "#202225" } }""");
            Assert.False(ThemeManager.IsGpuTheme(Prefix + "nogpu"));
        }

        [Fact]
        public void BrokenJsonReturnsFalseInsteadOfThrowing()
        {
            File.WriteAllText(ThemePath(Prefix + "broken"), "{ not valid json !!!");
            Assert.False(ThemeManager.IsGpuTheme(Prefix + "broken"));
        }

        [Fact]
        public void MissingFileReturnsFalse()
        {
            Assert.False(ThemeManager.IsGpuTheme(Prefix + "does-not-exist"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void EmptyNameReturnsFalse(string? name)
        {
            Assert.False(ThemeManager.IsGpuTheme(name!));
        }
    }
}
