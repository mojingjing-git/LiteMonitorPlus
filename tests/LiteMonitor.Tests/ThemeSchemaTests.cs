#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 主题 schema 合同测试（外观改造计划 A01 / v4 §3.1）。
    ///
    /// 覆盖：
    /// - 未知键（对象 / 数组 / 布尔 / null）在各层级的往返保真
    /// - 新增可空已知属性不凭空写出 null
    /// - 新增整数开关缺省为 0 时不凭空写出键
    /// - "font.scale" 只作为扩展数据保留（不存在 Font.Scale 属性）
    /// - rgba() 颜色解析规则
    /// - 重复解析不修改原主题对象
    /// </summary>
    public class ThemeSchemaTests
    {
        /// <summary>合成的未知值样本：每一层都塞了对象 / 数组 / 布尔 / null。</summary>
        private const string SyntheticJson = """
        {
          "version": 3,
          "name": "Synthetic",
          "unknownTopObject": { "a": 1, "b": [1, 2] },
          "unknownTopArray": [1, "x", true],
          "unknownTopBool": true,
          "unknownTopNull": null,
          "layout": {
            "rowHeight": 40,
            "unknownLayoutObject": { "x": 1 },
            "unknownLayoutArray": [1, 2],
            "unknownLayoutBool": false,
            "unknownLayoutNull": null
          },
          "font": {
            "family": "Microsoft YaHei UI",
            "scale": 1.5,
            "unknownFontObject": { "y": 2 },
            "unknownFontNull": null
          },
          "color": {
            "background": "#101010",
            "unknownColorArray": [3, 4],
            "unknownColorNull": null
          },
          "thresholds": {
            "load": { "warn": 10, "crit": 20, "unknownThresholdObject": { "z": 9 }, "unknownThresholdNull": null },
            "unknownThresholdsObject": { "q": 7 }
          },
          "backdrop": {
            "mode": "gradient",
            "unknownBackdropArray": [5, 6],
            "unknownBackdropNull": null
          }
        }
        """;

        // ============================================================
        // 未知值往返保真
        // ============================================================

        [Fact]
        public void 往返_各层未知对象数组布尔都保留()
        {
            var theme = Load(SyntheticJson);
            var again = Load(Save(theme));

            // 顶层
            AssertExtension(theme.ExtensionData, "unknownTopObject", JsonValueKind.Object);
            AssertExtension(theme.ExtensionData, "unknownTopArray", JsonValueKind.Array);
            AssertExtension(theme.ExtensionData, "unknownTopBool", JsonValueKind.True);
            AssertExtension(theme.ExtensionData, "unknownTopNull", JsonValueKind.Null);

            // layout 层
            AssertExtension(theme.Layout.ExtensionData, "unknownLayoutObject", JsonValueKind.Object);
            AssertExtension(theme.Layout.ExtensionData, "unknownLayoutArray", JsonValueKind.Array);
            AssertExtension(theme.Layout.ExtensionData, "unknownLayoutBool", JsonValueKind.False);
            AssertExtension(theme.Layout.ExtensionData, "unknownLayoutNull", JsonValueKind.Null);

            // font 层
            AssertExtension(theme.Font.ExtensionData, "unknownFontObject", JsonValueKind.Object);
            AssertExtension(theme.Font.ExtensionData, "unknownFontNull", JsonValueKind.Null);

            // color 层
            AssertExtension(theme.Color.ExtensionData, "unknownColorArray", JsonValueKind.Array);
            AssertExtension(theme.Color.ExtensionData, "unknownColorNull", JsonValueKind.Null);

            // thresholds 层
            AssertExtension(theme.Thresholds.ExtensionData, "unknownThresholdsObject", JsonValueKind.Object);
            AssertExtension(theme.Thresholds.Load.ExtensionData, "unknownThresholdObject", JsonValueKind.Object);
            AssertExtension(theme.Thresholds.Load.ExtensionData, "unknownThresholdNull", JsonValueKind.Null);

            // backdrop 层
            AssertExtension(theme.Backdrop!.ExtensionData, "unknownBackdropArray", JsonValueKind.Array);
            AssertExtension(theme.Backdrop.ExtensionData, "unknownBackdropNull", JsonValueKind.Null);

            // 往返之后依然在
            AssertExtension(again.ExtensionData, "unknownTopObject", JsonValueKind.Object);
            AssertExtension(again.ExtensionData, "unknownTopArray", JsonValueKind.Array);
            AssertExtension(again.ExtensionData, "unknownTopBool", JsonValueKind.True);
            AssertExtension(again.Layout.ExtensionData, "unknownLayoutObject", JsonValueKind.Object);
            AssertExtension(again.Layout.ExtensionData, "unknownLayoutArray", JsonValueKind.Array);
            AssertExtension(again.Layout.ExtensionData, "unknownLayoutBool", JsonValueKind.False);
            AssertExtension(again.Font.ExtensionData, "unknownFontObject", JsonValueKind.Object);
            AssertExtension(again.Color.ExtensionData, "unknownColorArray", JsonValueKind.Array);
            AssertExtension(again.Thresholds.ExtensionData, "unknownThresholdsObject", JsonValueKind.Object);
            AssertExtension(again.Thresholds.Load.ExtensionData, "unknownThresholdObject", JsonValueKind.Object);
            AssertExtension(again.Backdrop!.ExtensionData, "unknownBackdropArray", JsonValueKind.Array);
        }

        [Fact]
        public void 往返_原有未知null不被删除()
        {
            var theme = Load(SyntheticJson);
            var again = Load(Save(theme));

            AssertExtension(theme.ExtensionData, "unknownTopNull", JsonValueKind.Null);
            AssertExtension(theme.Layout.ExtensionData, "unknownLayoutNull", JsonValueKind.Null);
            AssertExtension(theme.Font.ExtensionData, "unknownFontNull", JsonValueKind.Null);
            AssertExtension(theme.Color.ExtensionData, "unknownColorNull", JsonValueKind.Null);
            AssertExtension(theme.Thresholds.Load.ExtensionData, "unknownThresholdNull", JsonValueKind.Null);
            AssertExtension(theme.Backdrop!.ExtensionData, "unknownBackdropNull", JsonValueKind.Null);

            // 往返之后仍然是 null（而不是被删掉，也不是变成 0 / false / ""）
            AssertExtension(again.ExtensionData, "unknownTopNull", JsonValueKind.Null);
            AssertExtension(again.Layout.ExtensionData, "unknownLayoutNull", JsonValueKind.Null);
            AssertExtension(again.Font.ExtensionData, "unknownFontNull", JsonValueKind.Null);
            AssertExtension(again.Color.ExtensionData, "unknownColorNull", JsonValueKind.Null);
            AssertExtension(again.Thresholds.Load.ExtensionData, "unknownThresholdNull", JsonValueKind.Null);
            AssertExtension(again.Backdrop!.ExtensionData, "unknownBackdropNull", JsonValueKind.Null);
        }

        [Fact]
        public void 往返_未知数组与对象的内部内容不被改写()
        {
            var theme = Load(SyntheticJson);
            var again = Load(Save(theme));

            var arr = again.ExtensionData["unknownTopArray"];
            Assert.Equal(3, arr.GetArrayLength());
            Assert.Equal(1, arr[0].GetInt32());
            Assert.Equal("x", arr[1].GetString());
            Assert.True(arr[2].GetBoolean());

            var obj = again.ExtensionData["unknownTopObject"];
            Assert.Equal(1, obj.GetProperty("a").GetInt32());
            Assert.Equal(2, obj.GetProperty("b").GetArrayLength());
        }

        // ============================================================
        // 新增属性：不该凭空出现
        // ============================================================

        [Fact]
        public void 往返_缺省的可空已知属性_不凭空写出null()
        {
            var theme = Load(SyntheticJson);

            Assert.Null(theme.Color.Border);
            Assert.Null(theme.Color.Shadow);
            Assert.Null(theme.Color.TextSecondary);

            using var doc = JsonDocument.Parse(Save(theme));
            var color = GetProp(doc.RootElement, "color");
            AssertNoProp(color, "border");
            AssertNoProp(color, "shadow");
            AssertNoProp(color, "textSecondary");
        }

        [Fact]
        public void 往返_缺省的整数开关_不凭空写出键()
        {
            var theme = Load(SyntheticJson);

            Assert.Equal(0, theme.Layout.FrameStyle);
            Assert.Equal(0, theme.Layout.FrameWidth);
            Assert.Equal(0, theme.Layout.FrameLength);
            Assert.Equal(0, theme.Layout.Scanline);
            Assert.Equal(0, theme.Layout.Glow);
            Assert.Equal(0, theme.Layout.Grid);
            Assert.Equal(0, theme.Layout.BarStyle);

            using var doc = JsonDocument.Parse(Save(theme));
            var layout = GetProp(doc.RootElement, "layout");
            AssertNoProp(layout, "frameStyle");
            AssertNoProp(layout, "frameWidth");
            AssertNoProp(layout, "frameLength");
            AssertNoProp(layout, "scanline");
            AssertNoProp(layout, "glow");
            AssertNoProp(layout, "grid");
            AssertNoProp(layout, "barStyle");
        }

        [Fact]
        public void 往返_显式关闭的新开关_保存后不留默认键()
        {
            var off = Load("""{"version":3,"layout":{"frameStyle":0,"frameWidth":0,"scanline":0,"glow":0,"grid":0,"barStyle":0}}""");

            using (var doc = JsonDocument.Parse(Save(off)))
            {
                var layout = GetProp(doc.RootElement, "layout");
                AssertNoProp(layout, "frameStyle");
                AssertNoProp(layout, "frameWidth");
                AssertNoProp(layout, "scanline");
                AssertNoProp(layout, "glow");
                AssertNoProp(layout, "grid");
                AssertNoProp(layout, "barStyle");
            }
        }

        [Fact]
        public void 往返_显式开启的新开关_保存后写出且值不变()
        {
            var on = Load("""
            {"version":3,
             "layout":{"frameStyle":1,"frameWidth":2,"frameLength":6,"scanline":30,"glow":40,"grid":50,"barStyle":2},
             "color":{"border":"#112233","shadow":"rgba(0,0,0,0.5)","textSecondary":"#AABBCC"}}
            """);

            using var doc = JsonDocument.Parse(Save(on));
            var layout = GetProp(doc.RootElement, "layout");
            Assert.Equal(1, GetProp(layout, "frameStyle").GetInt32());
            Assert.Equal(2, GetProp(layout, "frameWidth").GetInt32());
            Assert.Equal(6, GetProp(layout, "frameLength").GetInt32());
            Assert.Equal(30, GetProp(layout, "scanline").GetInt32());
            Assert.Equal(40, GetProp(layout, "glow").GetInt32());
            Assert.Equal(50, GetProp(layout, "grid").GetInt32());
            Assert.Equal(2, GetProp(layout, "barStyle").GetInt32());

            var color = GetProp(doc.RootElement, "color");
            Assert.Equal("#112233", GetProp(color, "border").GetString());
            Assert.Equal("rgba(0,0,0,0.5)", GetProp(color, "shadow").GetString());
            Assert.Equal("#AABBCC", GetProp(color, "textSecondary").GetString());
        }

        [Fact]
        public void 往返_没有backdrop配置_不写出backdrop键()
        {
            var theme = Load("""{"version":3,"name":"NoBackdrop"}""");
            Assert.Null(theme.Backdrop);

            using var doc = JsonDocument.Parse(Save(theme));
            AssertNoProp(doc.RootElement, "backdrop");
        }

        [Fact]
        public void Backdrop_存在时按文档默认值落地()
        {
            var theme = Load("""{"version":3,"backdrop":{"mode":"gradient"}}""");

            Assert.NotNull(theme.Backdrop);
            Assert.Equal("gradient", theme.Backdrop!.Mode);
            Assert.Equal(0.75, theme.Backdrop.Opacity);
            Assert.Equal(12, theme.Backdrop.Blur);
            Assert.Equal(90, theme.Backdrop.GradientAngle);
            Assert.Equal(2, theme.Backdrop.Fps);
            Assert.Equal(0, theme.Backdrop.Noise);
            Assert.Equal(0, theme.Backdrop.EdgeGlow);
            Assert.Null(theme.Backdrop.Tint);
            Assert.Null(theme.Backdrop.GradientStart);
            Assert.Null(theme.Backdrop.GradientEnd);
        }

        // ============================================================
        // font.scale：只作为扩展数据保留
        // ============================================================

        [Fact]
        public void FontScale_作为扩展数据保留_且不存在FontScale属性()
        {
            Assert.Null(typeof(FontConfig).GetProperty("Scale"));

            var theme = Load(SyntheticJson);
            Assert.True(theme.Font.ExtensionData.ContainsKey("scale"));
            Assert.Equal(1.5d, theme.Font.ExtensionData["scale"].GetDouble());

            var again = Load(Save(theme));
            Assert.True(again.Font.ExtensionData.ContainsKey("scale"));
            Assert.Equal(1.5d, again.Font.ExtensionData["scale"].GetDouble());
        }

        [Fact]
        public void 旧主题的font_scale_往返后仍然保留()
        {
            var theme = Load("""{"version":3,"font":{"family":"Consolas","scale":1.0}}""");
            Assert.True(theme.Font.ExtensionData.ContainsKey("scale"));

            var again = Load(Save(theme));
            Assert.True(again.Font.ExtensionData.ContainsKey("scale"));
            Assert.Equal(1.0d, again.Font.ExtensionData["scale"].GetDouble());
        }

        // ============================================================
        // 颜色解析
        // ============================================================

        [Theory]
        [InlineData("rgba(0,255,255,0.2)", 51, 0, 255, 255)]     // 0.2*255 = 51
        [InlineData("rgba(0,0,0,0)", 0, 0, 0, 0)]
        [InlineData("rgba(0,0,0,1)", 255, 0, 0, 0)]
        [InlineData("rgba(10,20,30,0.5)", 128, 10, 20, 30)]      // 127.5 → AwayFromZero → 128
        [InlineData("rgba( 10 , 20 , 30 , 0.5 )", 128, 10, 20, 30)]
        [InlineData("rgba(255,255,255,0.1)", 26, 255, 255, 255)] // 25.5 → 26
        public void ParseColor_rgba_按文档规则解析(string text, int a, int r, int g, int b)
        {
            var c = ThemeManager.ParseColor(text);
            Assert.Equal(a, c.A);
            Assert.Equal(r, c.R);
            Assert.Equal(g, c.G);
            Assert.Equal(b, c.B);
        }

        [Fact]
        public void ParseColor_非法rgba值_回退全透明()
        {
            string[] invalid =
            {
                "rgba(256,0,0,1)", "rgba(-1,0,0,1)", "rgba(0,0,0,1.5)", "rgba(0,0,0,-0.1)",
                "rgba(0,0,0,NaN)", "rgba(0,0,0,Infinity)", "rgba(0,0,0,-Infinity)",
                "rgba(0,0,0)", "rgba(0,0,0,1,2)", "rgba(0,0,0,abc)", "rgba(1.5,0,0,1)",
                "rgba()", "rgba(0,0,0,0.5,)", "rgba(0,0,0,1"
            };

            foreach (var text in invalid)
            {
                AssertFullyTransparent(ThemeManager.ParseColor(text), text);
            }
        }

        [Fact]
        public void ParseColor_保留旧六位八位十六进制与颜色名行为()
        {
            Assert.Equal(Color.FromArgb(0x20, 0x22, 0x25).ToArgb(), ThemeManager.ParseColor("#202225").ToArgb());
            Assert.Equal(Color.FromArgb(0x80, 0x20, 0x22, 0x25).ToArgb(), ThemeManager.ParseColor("#80202225").ToArgb());
            Assert.Equal(Color.Red.ToArgb(), ThemeManager.ParseColor("Red").ToArgb());

            // 兜底是"全透明"：Color.Transparent 是透明白(0x00FFFFFF)，
            // Color.FromName(未知名) 是透明黑(0x00000000)，两者 alpha 都是 0，
            // 因此这里断言 alpha 而不是具体 ARGB。
            AssertFullyTransparent(ThemeManager.ParseColor("NotAColorName"), "NotAColorName");
            AssertFullyTransparent(ThemeManager.ParseColor(""), "");
            AssertFullyTransparent(ThemeManager.ParseColor(null!), "null");
            // 3 位 #RGB 依旧不支持（保持旧行为）
            AssertFullyTransparent(ThemeManager.ParseColor("#FFF"), "#FFF");
        }

        [Fact]
        public void ParseColor_非法十六进制_回退全透明()
        {
            AssertFullyTransparent(ThemeManager.ParseColor("#12345"), "#12345");
            AssertFullyTransparent(ThemeManager.ParseColor("#GGGGGG"), "#GGGGGG");
        }

        /// <summary>断言颜色是完全透明（alpha = 0）的兜底值。</summary>
        private static void AssertFullyTransparent(Color c, string text)
        {
            Assert.Equal(0, c.A);
        }

        // ============================================================
        // 重复解析不修改原主题
        // ============================================================

        [Fact]
        public void 重复解析_不修改原主题对象()
        {
            var theme = Load(SyntheticJson);
            var before = Snapshot(theme);

            for (int i = 0; i < 3; i++)
            {
                var again = Load(Save(theme));
                Assert.Equal(before, Snapshot(again));
            }

            Assert.Equal(before, Snapshot(theme));
        }

        [Fact]
        public void 重复序列化_不修改原主题对象()
        {
            var theme = Load(SyntheticJson);
            var before = Snapshot(theme);

            string first = Save(theme);
            string second = Save(theme);

            Assert.Equal(first, second);
            Assert.Equal(before, Snapshot(theme));
        }

        // ============================================================
        // 10 个旧主题的只读往返
        // ============================================================

        /// <summary>
        /// A00 冻结基线里的 10 个旧主题（SHA256 见 A00 基线文档）。
        /// 只有这 10 个适用"旧主题一律没有新外观开关 / 没有 backdrop"的严格断言；
        /// 主题目录里**后来新增**的主题（如 CyberPunk_Mech）本来就会带新字段，
        /// 对它们只做通用的往返一致性断言。
        /// </summary>
        private static readonly string[] BaselineOldThemeNames =
        {
            "AuroraDream_Purple",
            "CyberBlue_Neon",
            "DarkFlat_Classic",
            "DarkNeo_Modern",
            "DeepOcean_Blue",
            "Dracula_Vampire",
            "LightPure_Elegant",
            "MatrixGreen_Terminal",
            "Monokai_Pro",
            "SolarAmber_Warm"
        };

        [Fact]
        public void 旧主题逐个往返_已知语义与未知值都保持()
        {
            var dir = ResolveThemesDir();
            var files = Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(files);

            // 基线旧主题必须一个都不少（防止有人删掉旧主题导致下面的断言空转）
            foreach (var name in BaselineOldThemeNames)
            {
                Assert.True(
                    files.Any(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.Ordinal)),
                    $"主题目录缺少基线旧主题 {name}");
            }

            foreach (var file in files)
            {
                var theme = Load(File.ReadAllText(file));
                var again = Load(Save(theme));

                Assert.Equal(theme.Name, again.Name);
                Assert.Equal(theme.Version, again.Version);
                Assert.Equal(theme.Layout.RowHeight, again.Layout.RowHeight);
                Assert.Equal(theme.Layout.Padding, again.Layout.Padding);
                Assert.Equal(theme.Layout.CornerRadius, again.Layout.CornerRadius);
                Assert.Equal(theme.Color.Background, again.Color.Background);
                Assert.Equal(theme.Color.TextPrimary, again.Color.TextPrimary);

                // 新外观开关：往返后必须保持原值（对目录里的所有主题都成立）
                Assert.Equal(theme.Layout.FrameStyle, again.Layout.FrameStyle);
                Assert.Equal(theme.Layout.FrameWidth, again.Layout.FrameWidth);
                Assert.Equal(theme.Layout.FrameLength, again.Layout.FrameLength);
                Assert.Equal(theme.Layout.Scanline, again.Layout.Scanline);
                Assert.Equal(theme.Layout.Glow, again.Layout.Glow);
                Assert.Equal(theme.Layout.Grid, again.Layout.Grid);
                Assert.Equal(theme.Layout.BarStyle, again.Layout.BarStyle);

                // backdrop：往返后必须保持（没有的仍为 null，有的各字段一致）
                if (theme.Backdrop == null)
                {
                    Assert.Null(again.Backdrop);
                }
                else
                {
                    Assert.NotNull(again.Backdrop);
                    Assert.Equal(theme.Backdrop.Mode, again.Backdrop!.Mode);
                    Assert.Equal(theme.Backdrop.Opacity, again.Backdrop.Opacity);
                    Assert.Equal(theme.Backdrop.Blur, again.Backdrop.Blur);
                    Assert.Equal(theme.Backdrop.Tint, again.Backdrop.Tint);
                    Assert.Equal(theme.Backdrop.GradientStart, again.Backdrop.GradientStart);
                    Assert.Equal(theme.Backdrop.GradientEnd, again.Backdrop.GradientEnd);
                    Assert.Equal(theme.Backdrop.GradientAngle, again.Backdrop.GradientAngle);
                    Assert.Equal(theme.Backdrop.Noise, again.Backdrop.Noise);
                    Assert.Equal(theme.Backdrop.EdgeGlow, again.Backdrop.EdgeGlow);
                    Assert.Equal(theme.Backdrop.Fps, again.Backdrop.Fps);
                }

                // 主题里本来就有的 border/shadow/textSecondary 仍然保留（但不会自动开启效果）
                Assert.Equal(theme.Color.Border, again.Color.Border);
                Assert.Equal(theme.Color.Shadow, again.Color.Shadow);
                Assert.Equal(theme.Color.TextSecondary, again.Color.TextSecondary);

                // ↓ 只有基线旧主题适用下面的严格断言
                string themeName = Path.GetFileNameWithoutExtension(file);
                if (BaselineOldThemeNames.Contains(themeName, StringComparer.Ordinal))
                {
                    // 旧主题一律没有新的效果开关
                    Assert.Equal(0, again.Layout.FrameStyle);
                    Assert.Equal(0, again.Layout.FrameWidth);
                    Assert.Equal(0, again.Layout.FrameLength);
                    Assert.Equal(0, again.Layout.Scanline);
                    Assert.Equal(0, again.Layout.Glow);
                    Assert.Equal(0, again.Layout.Grid);
                    Assert.Equal(0, again.Layout.BarStyle);

                    // 旧主题一律没有 backdrop
                    Assert.Null(again.Backdrop);

                    // font.scale 必须作为扩展数据保留
                    Assert.True(again.Font.ExtensionData.ContainsKey("scale"),
                        $"{Path.GetFileName(file)} 的 font.scale 丢失");
                }
            }
        }

        // ============================================================
        // 辅助
        // ============================================================

        private static Theme Load(string json)
        {
            var theme = JsonSerializer.Deserialize<Theme>(json, ThemeManager.CreateSerializerOptions(false));
            Assert.NotNull(theme);
            ThemeManager.PruneExtensionDataConflicts(theme!);
            return theme!;
        }

        private static string Save(Theme theme)
        {
            return JsonSerializer.Serialize(theme, ThemeManager.CreateSerializerOptions(false));
        }

        private static void AssertExtension(Dictionary<string, JsonElement> ext, string key, JsonValueKind kind)
        {
            Assert.True(ext.ContainsKey(key), $"扩展数据缺少键 {key}");
            Assert.Equal(kind, ext[key].ValueKind);
        }

        private static JsonElement GetProp(JsonElement obj, string name)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    return p.Value;
            }
            Assert.Fail($"JSON 中找不到属性 {name}");
            return default;
        }

        private static void AssertNoProp(JsonElement obj, string name)
        {
            foreach (var p in obj.EnumerateObject())
            {
                Assert.False(string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase),
                    $"不应写出属性 {name}（实际写出 {p.Name}）");
            }
        }

        /// <summary>主题配置状态快照：用于断言"重复解析不修改原主题"。</summary>
        private static string Snapshot(Theme t)
        {
            var sb = new StringBuilder();
            sb.Append(t.Name).Append('|').Append(t.Version).Append('|');
            sb.Append("L:").Append(t.Layout.Width).Append(',').Append(t.Layout.LayoutScale).Append(',')
              .Append(t.Layout.RowHeight).Append(',').Append(t.Layout.Padding).Append(',')
              .Append(t.Layout.CornerRadius).Append(',').Append(t.Layout.GroupRadius).Append(',')
              .Append(t.Layout.FrameStyle).Append(',').Append(t.Layout.FrameWidth).Append(',')
              .Append(t.Layout.FrameLength).Append(',').Append(t.Layout.Scanline).Append(',')
              .Append(t.Layout.Glow).Append(',').Append(t.Layout.Grid).Append(',')
              .Append(t.Layout.BarStyle).Append('|');
            sb.Append("F:").Append(t.Font.Family).Append(',').Append(t.Font.ValueFamily).Append(',')
              .Append(t.Font.Title).Append(',').Append(t.Font.Group).Append(',')
              .Append(t.Font.Item).Append(',').Append(t.Font.Value).Append(',')
              .Append(t.Font.Bold).Append('|');
            sb.Append("C:").Append(t.Color.Background).Append(',').Append(t.Color.TextTitle).Append(',')
              .Append(t.Color.TextGroup).Append(',').Append(t.Color.TextPrimary).Append(',')
              .Append(t.Color.ValueSafe).Append(',').Append(t.Color.ValueWarn).Append(',')
              .Append(t.Color.ValueCrit).Append(',').Append(t.Color.BarBackground).Append(',')
              .Append(t.Color.BarLow).Append(',').Append(t.Color.BarMid).Append(',')
              .Append(t.Color.BarHigh).Append(',').Append(t.Color.GroupBackground).Append(',')
              .Append(t.Color.Border).Append(',').Append(t.Color.Shadow).Append(',')
              .Append(t.Color.TextSecondary).Append('|');
            sb.Append("B:").Append(t.Backdrop?.Mode).Append(',').Append(t.Backdrop?.Opacity).Append(',')
              .Append(t.Backdrop?.Blur).Append(',').Append(t.Backdrop?.Tint).Append(',')
              .Append(t.Backdrop?.GradientStart).Append(',').Append(t.Backdrop?.GradientEnd).Append(',')
              .Append(t.Backdrop?.GradientAngle).Append(',').Append(t.Backdrop?.Noise).Append(',')
              .Append(t.Backdrop?.EdgeGlow).Append(',').Append(t.Backdrop?.Fps).Append('|');
            sb.Append("E:").Append(ExtKeys(t.ExtensionData)).Append('|')
              .Append(ExtKeys(t.Layout.ExtensionData)).Append('|')
              .Append(ExtKeys(t.Font.ExtensionData)).Append('|')
              .Append(ExtKeys(t.Color.ExtensionData)).Append('|')
              .Append(ExtKeys(t.Thresholds.ExtensionData)).Append('|')
              .Append(ExtKeys(t.Thresholds.Load.ExtensionData)).Append('|')
              .Append(ExtKeys(t.Backdrop?.ExtensionData)).Append('|');
            return sb.ToString();
        }

        private static string ExtKeys(Dictionary<string, JsonElement>? ext)
        {
            if (ext == null) return "<null>";
            return string.Join(",", ext.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => k + "=" + ext[k].ValueKind));
        }

        /// <summary>定位内置主题目录（优先仓库内的原始文件，只读）。</summary>
        private static string ResolveThemesDir()
        {
            var candidates = new List<string>();
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                candidates.Add(Path.Combine(dir.FullName, "resources", "themes"));
                dir = dir.Parent;
            }

            foreach (var c in candidates)
            {
                if (Directory.Exists(c) && Directory.GetFiles(c, "*.json").Length > 0)
                    return c;
            }

            throw new DirectoryNotFoundException("找不到内置主题目录");
        }
    }
}
