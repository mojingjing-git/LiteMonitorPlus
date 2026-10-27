using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// [S04] 统一颜色校验器合同（计划 §6）：
    /// - 合法格式保持原义（#RRGGBB/#AARRGGBB/rgba/命名色），合法透明色有效；
    /// - 非法输入是**独立失败状态**（不与透明混同），诊断可定位；
    /// - 缓存区分成败；未知扩展字段不按颜色解析；
    /// - ThemeManager.ParseColor 渲染兜底语义不变（失败→透明、不抛）。
    /// </summary>
    public class ThemeColorParserTests
    {
        // ---------- 基础解析 ----------

        [Theory]
        [InlineData("#FF0000", 255, 0, 0, 255)]
        [InlineData("#00FF00", 0, 255, 0, 255)]
        [InlineData("#80FF0000", 255, 0, 0, 128)] // 计划验收例：八位 alpha 在最前两位
        [InlineData("#40FFFFFF", 255, 255, 255, 64)]
        public void Parse_Hex6AndHex8_KeepsExactValues(string input, int r, int g, int b, int a)
        {
            var result = ThemeColorParser.TryParse(input);
            Assert.True(result.Success, result.Diagnostic);
            Assert.Equal(a, result.Color.A);
            Assert.Equal(r, result.Color.R);
            Assert.Equal(g, result.Color.G);
            Assert.Equal(b, result.Color.B);
        }

        [Fact]
        public void Parse_NamedColors_KeepsKnownColor()
        {
            var red = ThemeColorParser.TryParse("Red");
            Assert.True(red.Success);
            Assert.Equal(Color.Red.ToArgb(), red.Color.ToArgb());

            var transparent = ThemeColorParser.TryParse("Transparent");
            Assert.True(transparent.Success);
            Assert.Equal(0, transparent.Color.A);
        }

        [Fact]
        public void Parse_Rgba_Valid_KeepsValues_AndIsLocaleInvariant()
        {
            var result = ThemeColorParser.TryParse("rgba(255, 128, 0, 0.5)");
            Assert.True(result.Success, result.Diagnostic);
            Assert.Equal(255, result.Color.R);
            Assert.Equal(128, result.Color.G);
            Assert.Equal(0, result.Color.B);
            Assert.Equal(128, result.Color.A); // 0.5*255 ≈ 127.5，AwayFromZero → 128
        }

        [Fact]
        public void Parse_Blank_FallsBackToTransparent_AsLegalInput()
        {
            foreach (var input in new[] { null, "", "   " })
            {
                var result = ThemeColorParser.TryParse(input);
                Assert.True(result.Success);
                Assert.Equal(Color.Transparent.ToArgb(), result.Color.ToArgb());
                Assert.Null(result.Diagnostic);
            }
        }

        // ---------- 非法输入：独立失败状态 + 可定位诊断 ----------

        [Theory]
        [InlineData("#FF00")]        // 长度不足
        [InlineData("#FF000")]       // 长度 5
        [InlineData("#FF00000")]     // 长度 7
        [InlineData("#FF0000000")]   // 长度 9
        [InlineData("#GG0000")]      // 非法十六进制字符
        [InlineData("#FF00ZZ")]      // 中段非法字符
        [InlineData("#80GG0000")]    // alpha 段非法字符
        [InlineData("notacolor")]    // 未知命名色
        [InlineData("rgba(1,2)")]    // 分段不足
        [InlineData("rgba(1,2,3)")]  // 分段不足
        [InlineData("rgba(256,0,0,1)")]   // r 越界
        [InlineData("rgba(0,0,0,1.5)")]   // alpha 越界
        [InlineData("rgba(0,0,0,-0.1)")]  // alpha 负
        [InlineData("rgba(0,0,0,NaN)")]   // NaN
        [InlineData("rgba(0,0,0,Infinity)")] // Infinity
        public void Parse_InvalidInputs_FailWithDiagnostic_NeverReturnSuccess(string input)
        {
            var result = ThemeColorParser.TryParse(input);
            Assert.False(result.Success);
            Assert.False(string.IsNullOrWhiteSpace(result.Diagnostic));
        }

        [Fact]
        public void Parse_LegalTransparent_IsSuccess_NotConfusedWithFailure()
        {
            var legal = ThemeColorParser.TryParse("rgba(0,0,0,0)");
            Assert.True(legal.Success);
            Assert.Equal(0, legal.Color.A);

            var illegal = ThemeColorParser.TryParse("rgba(0,0,0,1.5)");
            Assert.False(illegal.Success);
        }

        [Fact]
        public void Parse_FailureState_DoesNotMasqueradeAsTransparentColor()
        {
            // 失败结果的颜色值不允许被消费方当"合法透明"使用——诊断必须非空以区分。
            var result = ThemeColorParser.TryParse("#ZZZZZZ");
            Assert.False(result.Success);
            Assert.NotNull(result.Diagnostic);
            Assert.Contains("#ZZZZZZ", result.Diagnostic, StringComparison.Ordinal);
        }

        // ---------- 缓存 ----------

        [Fact]
        public void Cache_FailureAndSuccessAreStableAcrossRepeatedCalls()
        {
            var first = ThemeColorParser.TryParse("#ABCDEF");
            var second = ThemeColorParser.TryParse("#ABCDEF");
            Assert.True(first.Success);
            Assert.True(second.Success);
            Assert.Equal(first.Color.ToArgb(), second.Color.ToArgb());

            var badFirst = ThemeColorParser.TryParse("#NOPE00");
            var badSecond = ThemeColorParser.TryParse("#NOPE00");
            Assert.False(badFirst.Success);
            Assert.False(badSecond.Success);
            Assert.Equal(badFirst.Diagnostic, badSecond.Diagnostic);
        }

        [Fact]
        public void ClearCache_DoesNotChangeOutcomes()
        {
            var before = ThemeColorParser.TryParse("#123456");
            ThemeColorParser.ClearCache();
            var after = ThemeColorParser.TryParse("#123456");
            Assert.True(after.Success);
            Assert.Equal(before.Color.ToArgb(), after.Color.ToArgb());
        }

        // ---------- ParseColor 运行时兜底 ----------

        [Fact]
        public void ThemeManager_ParseColor_FallsBackToTransparent_WithoutThrowing()
        {
            Assert.Equal(Color.Transparent.ToArgb(), ThemeManager.ParseColor("#ZZZZZZ").ToArgb());
            Assert.Equal(Color.Transparent.ToArgb(), ThemeManager.ParseColor("notacolor").ToArgb());
            Assert.Equal(Color.Transparent.ToArgb(), ThemeManager.ParseColor(null).ToArgb());
            // 合法输入保持原义
            Assert.Equal(128, ThemeManager.ParseColor("#80FF0000").A);
        }

        // ---------- ValidateTheme（主题对象） ----------

        private static Theme MakeTheme(Action<ColorConfig>? configureColor = null, Action<BackdropConfig>? configureBackdrop = null)
        {
            var theme = new Theme();
            configureColor?.Invoke(theme.Color);
            if (configureBackdrop != null)
            {
                theme.Backdrop = new BackdropConfig();
                configureBackdrop(theme.Backdrop);
            }
            return theme;
        }

        [Fact]
        public void ValidateTheme_AllLegalFields_Passes()
        {
            var theme = MakeTheme(
                c => { c.Background = "#FF0000"; c.TextTitle = "rgba(10,20,30,0.9)"; c.Border = "Red"; c.Shadow = ""; },
                b => { b.Tint = "#80FFFFFF"; b.GradientStart = "Black"; b.GradientEnd = ""; });
            Assert.Empty(ThemeColorParser.ValidateTheme(theme));
        }

        [Fact]
        public void ValidateTheme_IllegalField_FailsWithFieldPathAndReason()
        {
            var theme = MakeTheme(c => c.Background = "#GG0000");
            var failures = ThemeColorParser.ValidateTheme(theme);
            Assert.Single(failures);
            Assert.Contains("color.Background", failures[0], StringComparison.Ordinal);
        }

        [Fact]
        public void ValidateTheme_BackdropAndFallbackFields_AreValidated()
        {
            var theme = MakeTheme(configureBackdrop: b => b.GradientEnd = "notacolor");
            theme.Rendering = new ThemeRenderingConfig
            {
                FallbackStyle = new ThemeFallbackStyle { Text = "#XYZXYZ" }
            };
            var failures = ThemeColorParser.ValidateTheme(theme);
            Assert.Equal(2, failures.Count);
            Assert.Contains(failures, f => f.StartsWith("backdrop.GradientEnd", StringComparison.Ordinal));
            Assert.Contains(failures, f => f.StartsWith("rendering.fallbackStyle.Text", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateTheme_UnknownExtensionFields_AreNotParsedAsColors()
        {
            var theme = MakeTheme();
            theme.Color.ExtensionData["customFutureField"] = JsonDocument.Parse("\"#GGGGGG\"").RootElement.Clone();
            Assert.Empty(ThemeColorParser.ValidateTheme(theme)); // 未知扩展字段不按颜色解析
        }

        // ---------- ValidateThemeJson（市场载荷） ----------

        private static JsonElement ParseJson(string json) =>
            JsonDocument.Parse(json).RootElement.Clone();

        [Fact]
        public void ValidateThemeJson_BadKnownColorField_Fails()
        {
            var root = ParseJson("{\"color\":{\"Background\":\"#GG0000\",\"TextTitle\":\"#FF0000\"}}");
            var failures = ThemeColorParser.ValidateThemeJson(root);
            Assert.Single(failures);
            Assert.Contains("color.Background", failures[0], StringComparison.Ordinal);
        }

        [Fact]
        public void ValidateThemeJson_MissingNodesAndFields_Pass()
        {
            Assert.Empty(ThemeColorParser.ValidateThemeJson(ParseJson("{}")));
            Assert.Empty(ThemeColorParser.ValidateThemeJson(ParseJson("{\"color\":{}}")));
            Assert.Empty(ThemeColorParser.ValidateThemeJson(ParseJson("{\"color\":{\"TextTitle\":\"#FF0000\"}}")));
        }

        [Fact]
        public void ValidateThemeJson_UnknownExtensionFields_AreNotParsedAsColors()
        {
            var root = ParseJson("{\"color\":{\"customField\":\"#GGGGGG\"}}");
            Assert.Empty(ThemeColorParser.ValidateThemeJson(root));
        }

        [Fact]
        public void ValidateThemeJson_FallbackStyleKnownFields_AreValidated()
        {
            var root = ParseJson("{\"rendering\":{\"fallbackStyle\":{\"text\":\"notacolor\",\"accent\":\"#FF0000\"}}}");
            var failures = ThemeColorParser.ValidateThemeJson(root);
            Assert.Single(failures);
            Assert.Contains("rendering.fallbackStyle.text", failures[0], StringComparison.Ordinal);
        }

        [Fact]
        public void ValidateThemeJson_BackdropKnownFields_AreValidated()
        {
            var root = ParseJson("{\"backdrop\":{\"Tint\":\"rgba(0,0,0,2.0)\",\"Mode\":\"off\"}}");
            var failures = ThemeColorParser.ValidateThemeJson(root);
            Assert.Single(failures);
            Assert.Contains("backdrop.Tint", failures[0], StringComparison.Ordinal);
        }
    }
}
