#nullable enable
using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 外观 options 合同测试（外观改造计划 A03 / v4 §3.1、§3.2）。
    ///
    /// 覆盖：
    /// - 未知样式 / 极端尺寸的归一化
    /// - 归一化只作用于运行时副本，不回写用户 JSON
    /// - 每个新尺寸只缩放一次（LayoutConfig.Scale 不碰新字段）
    /// - 路径几何夹紧到当前客户区
    /// - 使用调用方传入的窗口 DPI，而不是全局 UIUtils.ScaleFactor
    /// - 布局副本（Clone / CreatePreviewCopy）复制全部字段与扩展数据
    /// - 快照不持有 Theme / Font / Brush / Pen / Control
    /// </summary>
    public class AppearanceOptionsTests
    {
        private static readonly Rectangle Bounds = new Rectangle(0, 0, 480, 1576);

        // ============================================================
        // 归一化
        // ============================================================

        [Fact]
        public void Create_未知样式与极端尺寸_按文档范围归一化()
        {
            var theme = new Theme();
            theme.Layout.FrameStyle = 99;
            theme.Layout.FrameWidth = 999;
            theme.Layout.FrameLength = -5;
            theme.Layout.Scanline = 5000;
            theme.Layout.Glow = -20;
            theme.Layout.Grid = 1000;
            theme.Layout.BarStyle = 7;
            theme.Backdrop = new BackdropConfig
            {
                Mode = "unknown",
                Opacity = 5.0,
                Blur = 999.0,
                Fps = 0,
                Noise = 400,
                EdgeGlow = -3
            };

            var opt = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, Bounds);

            // 未知样式 → 关闭该效果
            Assert.Equal(0, opt.FrameStyle);
            Assert.Equal(0, opt.BarStyle);
            Assert.False(opt.FrameEnabled);
            Assert.False(opt.BarStyleEnabled);

            // 范围夹紧
            Assert.Equal(8, opt.FrameWidth);
            Assert.Equal(0, opt.FrameLength);
            Assert.Equal(100, opt.Scanline);
            Assert.Equal(0, opt.Glow);
            Assert.Equal(100, opt.Grid);

            // 未知背景模式 → 退回 solid
            Assert.Equal("solid", opt.BackdropMode);
            Assert.Equal(1.0, opt.BackdropOpacity);
            Assert.Equal(32.0, opt.BackdropBlur);
            Assert.Equal(1, opt.BackdropFps);
            Assert.Equal(100, opt.BackdropNoise);
            Assert.Equal(0, opt.BackdropEdgeGlow);
        }

        [Fact]
        public void Create_归一化只作用于运行时副本_不回写用户JSON()
        {
            var theme = new Theme();
            theme.Layout.FrameStyle = 99;
            theme.Layout.FrameWidth = 999;
            theme.Layout.FrameLength = -5;
            theme.Layout.Glow = 500;
            theme.Layout.BarStyle = 7;
            theme.Backdrop = new BackdropConfig
            {
                Mode = "unknown",
                Opacity = 5.0,
                Blur = 999.0,
                Fps = 0
            };

            var opt = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, Bounds);

            // 运行时副本是归一化后的值
            Assert.Equal(0, opt.FrameStyle);
            Assert.Equal(8, opt.FrameWidth);

            // 原主题完全没被动过
            Assert.Equal(99, theme.Layout.FrameStyle);
            Assert.Equal(999, theme.Layout.FrameWidth);
            Assert.Equal(-5, theme.Layout.FrameLength);
            Assert.Equal(500, theme.Layout.Glow);
            Assert.Equal(7, theme.Layout.BarStyle);
            Assert.Equal("unknown", theme.Backdrop!.Mode);
            Assert.Equal(5.0, theme.Backdrop.Opacity);
            Assert.Equal(999.0, theme.Backdrop.Blur);
            Assert.Equal(0, theme.Backdrop.Fps);
        }

        [Fact]
        public void Create_重复解析不修改原主题()
        {
            var theme = new Theme();
            theme.Layout.FrameStyle = 1;
            theme.Layout.FrameWidth = 4;
            theme.Layout.FrameLength = 12;
            theme.Backdrop = new BackdropConfig { Mode = "gradient", Opacity = 0.5 };

            var first = ThemeAppearanceOptions.Create(theme, 1.5f, 1.0f, Bounds, 1);
            var second = ThemeAppearanceOptions.Create(theme, 1.5f, 1.0f, Bounds, 1);
            var third = ThemeAppearanceOptions.Create(theme, 1.5f, 1.0f, Bounds, 1);

            Assert.Equal(first.FrameWidthPx, second.FrameWidthPx);
            Assert.Equal(first.FrameLengthPx, third.FrameLengthPx);
            Assert.Equal(first.BackdropOpacity, third.BackdropOpacity);
            Assert.Equal(1, first.FrameStyle);
            Assert.Equal(4, theme.Layout.FrameWidth);
            Assert.Equal(12, theme.Layout.FrameLength);
            Assert.Equal(0.5, theme.Backdrop!.Opacity);
        }

        [Fact]
        public void Create_客户区无有效宽高_标记为不可绘制()
        {
            var theme = new Theme();
            var opt = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, Rectangle.Empty);
            Assert.False(opt.HasValidBounds);
        }

        // ============================================================
        // 只缩放一次
        // ============================================================

        [Fact]
        public void 新尺寸只缩放一次_布局缩放不碰新字段_由options换算物理像素()
        {
            var theme = new Theme();
            theme.Layout.FrameWidth = 4;
            theme.Layout.FrameLength = 10;
            theme.Layout.RowHeight = 40;

            // 现有布局字段的缩放行为保持原样
            theme.Layout.Scale(2.0f);
            Assert.Equal(80, theme.Layout.RowHeight);

            // 新字段保持 96 DPI 逻辑值（没有被布局缩放处理）
            Assert.Equal(4, theme.Layout.FrameWidth);
            Assert.Equal(10, theme.Layout.FrameLength);

            var opt = ThemeAppearanceOptions.Create(theme, 2.0f, 1.0f, Bounds);
            Assert.Equal(4, opt.FrameWidth);      // 逻辑值
            Assert.Equal(8, opt.FrameWidthPx);    // 物理像素 = 4 * 2
            Assert.Equal(10, opt.FrameLength);
            Assert.Equal(20, opt.FrameLengthPx);
        }

        [Fact]
        public void ThemeScale_不改变新字段_新尺寸仍只被options缩放一次()
        {
            var theme = new Theme();
            theme.Layout.FrameWidth = 3;
            theme.Layout.FrameLength = 6;

            try
            {
                theme.Scale(2.0f, 1.0f);

                Assert.Equal(3, theme.Layout.FrameWidth);
                Assert.Equal(6, theme.Layout.FrameLength);

                var opt = ThemeAppearanceOptions.Create(theme, 2.0f, 1.0f, Bounds);
                Assert.Equal(6, opt.FrameWidthPx);
                Assert.Equal(12, opt.FrameLengthPx);
            }
            finally
            {
                theme.DisposeFonts();
            }
        }

        [Fact]
        public void Create_使用调用方传入的窗口DPI_而不是全局UIUtilsScaleFactor()
        {
            float original = UIUtils.ScaleFactor;
            try
            {
                UIUtils.ScaleFactor = 3.0f;

                var theme = new Theme();
                theme.Layout.FrameWidth = 2;

                var opt = ThemeAppearanceOptions.Create(theme, 1.5f, 1.0f, Bounds);

                Assert.Equal(1.5f, opt.DpiScale);
                Assert.Equal(144, opt.DeviceDpi);
                Assert.Equal(1.5f, opt.EffectiveScale);
                Assert.Equal(3, opt.FrameWidthPx);   // 2 * 1.5，而不是 2 * 3
            }
            finally
            {
                UIUtils.ScaleFactor = original;
            }
        }

        [Fact]
        public void Create_用户缩放与DPI相乘()
        {
            var theme = new Theme();
            theme.Layout.FrameWidth = 2;

            var opt = ThemeAppearanceOptions.Create(theme, 1.5f, 2.0f, Bounds);
            Assert.Equal(3.0f, opt.EffectiveScale);
            Assert.Equal(6, opt.FrameWidthPx);
        }

        // ============================================================
        // 路径几何夹紧
        // ============================================================

        [Fact]
        public void Create_路径几何夹紧到当前客户区()
        {
            var theme = new Theme();
            theme.Layout.FrameWidth = 8;
            theme.Layout.FrameLength = 64;

            var opt = ThemeAppearanceOptions.Create(theme, 2.0f, 1.0f, new Rectangle(0, 0, 20, 30));

            Assert.Equal(10, opt.FrameWidthPx);   // min(16, 20 / 2)
            Assert.Equal(15, opt.FrameLengthPx);  // min(128, 30 / 2)
        }

        [Fact]
        public void Create_客户区为空时不按bounds夹紧()
        {
            var theme = new Theme();
            theme.Layout.FrameWidth = 8;
            theme.Layout.FrameLength = 64;

            var opt = ThemeAppearanceOptions.Create(theme, 2.0f, 1.0f, Rectangle.Empty);
            Assert.Equal(16, opt.FrameWidthPx);
            Assert.Equal(128, opt.FrameLengthPx);
        }

        // ============================================================
        // 颜色与开关
        // ============================================================

        [Fact]
        public void 旧主题已有描边与阴影色_不自动开启描边与辉光()
        {
            var theme = new Theme();
            theme.Color.Background = "#0A0F1C";
            theme.Color.Border = "#1C2A40";
            theme.Color.Shadow = "rgba(0,255,255,0.2)";
            theme.Color.TextSecondary = "#66FFFF";

            var opt = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, Bounds);

            Assert.False(opt.FrameEnabled);
            Assert.False(opt.GlowEnabled);

            // 颜色本身仍然解析正确，供特效开启时使用
            Assert.Equal(Color.FromArgb(0x1C, 0x2A, 0x40).ToArgb(), opt.BorderColor.ToArgb());
            Assert.Equal(51, opt.ShadowColor.A);
            Assert.Equal(255, opt.ShadowColor.G);
            Assert.Equal(Color.FromArgb(0x66, 0xFF, 0xFF).ToArgb(), opt.TextSecondaryColor.ToArgb());
        }

        [Fact]
        public void Create_显式开启时开关为真()
        {
            var theme = new Theme();
            theme.Layout.FrameStyle = 1;
            theme.Layout.Glow = 30;
            theme.Layout.Scanline = 20;
            theme.Layout.Grid = 10;
            theme.Layout.BarStyle = 2;
            theme.Backdrop = new BackdropConfig { Mode = "glass" };

            var opt = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, Bounds);

            Assert.True(opt.FrameEnabled);
            Assert.True(opt.GlowEnabled);
            Assert.True(opt.ScanlineEnabled);
            Assert.True(opt.GridEnabled);
            Assert.True(opt.BarStyleEnabled);
            Assert.True(opt.GlassRequested);
            Assert.Equal("glass", opt.BackdropMode);
        }

        [Fact]
        public void Create_渐变两端缺省取背景与组块背景()
        {
            var theme = new Theme();
            theme.Color.Background = "#111111";
            theme.Color.GroupBackground = "#222222";
            theme.Backdrop = new BackdropConfig { Mode = "gradient" };

            var opt = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, Bounds);

            Assert.Equal(Color.FromArgb(0x11, 0x11, 0x11).ToArgb(), opt.GradientStartColor.ToArgb());
            Assert.Equal(Color.FromArgb(0x22, 0x22, 0x22).ToArgb(), opt.GradientEndColor.ToArgb());
            Assert.Equal(Color.FromArgb(0x11, 0x11, 0x11).ToArgb(), opt.TintColor.ToArgb());
        }

        [Fact]
        public void Create_显式渐变色覆盖缺省()
        {
            var theme = new Theme();
            theme.Color.Background = "#111111";
            theme.Color.GroupBackground = "#222222";
            theme.Backdrop = new BackdropConfig
            {
                Mode = "gradient",
                Tint = "#333333",
                GradientStart = "#444444",
                GradientEnd = "#555555"
            };

            var opt = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, Bounds);

            Assert.Equal(Color.FromArgb(0x33, 0x33, 0x33).ToArgb(), opt.TintColor.ToArgb());
            Assert.Equal(Color.FromArgb(0x44, 0x44, 0x44).ToArgb(), opt.GradientStartColor.ToArgb());
            Assert.Equal(Color.FromArgb(0x55, 0x55, 0x55).ToArgb(), opt.GradientEndColor.ToArgb());
        }

        [Fact]
        public void Create_缺失backdrop时退回旧背景路径()
        {
            var theme = new Theme();
            var opt = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, Bounds);

            Assert.Null(theme.Backdrop);
            Assert.Equal("solid", opt.BackdropMode);
            Assert.False(opt.GlassRequested);
            Assert.Equal(0.75, opt.BackdropOpacity);
            Assert.Equal(12.0, opt.BackdropBlur);
            Assert.Equal(2, opt.BackdropFps);
        }

        [Fact]
        public void Create_携带主题代次与客户区()
        {
            var theme = new Theme { Name = "X", Version = 4 };
            var opt = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, Bounds, 42);

            Assert.Equal(42, opt.ThemeGeneration);
            Assert.Equal(4, opt.ThemeVersion);
            Assert.Equal("X", opt.ThemeName);
            Assert.Equal(Bounds, opt.ClientBounds);
            Assert.True(opt.HasValidBounds);
        }

        // ============================================================
        // 布局副本
        // ============================================================

        [Fact]
        public void LayoutClone_复制全部属性与扩展数据_且不共享字典()
        {
            var theme = Load("""{"version":3,"layout":{"rowHeight":44,"frameStyle":2,"frameWidth":3,"customThing":{"a":1}}}""");
            var src = theme.Layout;

            var clone = src.Clone();

            // 反射逐个属性核对：以后新增 Layout 属性也不会漏拷贝
            foreach (var p in typeof(LayoutConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.Name == nameof(LayoutConfig.ExtensionData)) continue;
                Assert.Equal(p.GetValue(src), p.GetValue(clone));
            }

            Assert.Equal(44, clone.RowHeight);
            Assert.Equal(2, clone.FrameStyle);
            Assert.Equal(3, clone.FrameWidth);
            Assert.True(clone.ExtensionData.ContainsKey("customThing"));
            Assert.NotSame(src.ExtensionData, clone.ExtensionData);

            // 改副本不影响原布局
            clone.RowHeight = 99;
            clone.FrameWidth = 8;
            clone.ExtensionData["added"] = clone.ExtensionData["customThing"];

            Assert.Equal(44, src.RowHeight);
            Assert.Equal(3, src.FrameWidth);
            Assert.False(src.ExtensionData.ContainsKey("added"));
        }

        [Fact]
        public void LayoutClone_新字段也被复制()
        {
            var src = new LayoutConfig
            {
                FrameStyle = 3,
                FrameWidth = 5,
                FrameLength = 7,
                Scanline = 11,
                Glow = 13,
                Grid = 17,
                BarStyle = 1
            };

            var clone = src.Clone();

            Assert.Equal(3, clone.FrameStyle);
            Assert.Equal(5, clone.FrameWidth);
            Assert.Equal(7, clone.FrameLength);
            Assert.Equal(11, clone.Scanline);
            Assert.Equal(13, clone.Glow);
            Assert.Equal(17, clone.Grid);
            Assert.Equal(1, clone.BarStyle);
        }

        [Fact]
        public void 预览副本_只缩放一次_不累计()
        {
            var src = new LayoutConfig { RowHeight = 40, FrameWidth = 4 };

            var copy1 = src.CreatePreviewCopy(2.0f);
            Assert.Equal(80, copy1.RowHeight);
            Assert.Equal(2.0f, copy1.LayoutScale);
            Assert.Equal(4, copy1.FrameWidth);   // 新字段保持逻辑值

            var copy2 = src.CreatePreviewCopy(2.0f);
            Assert.Equal(80, copy2.RowHeight);   // 与第一次相同，不累计
            Assert.Equal(2.0f, copy2.LayoutScale);

            // 原布局没被改
            Assert.Equal(40, src.RowHeight);
            Assert.Equal(1.0f, src.LayoutScale);
        }

        [Fact]
        public void 预览副本_源已缩放过时不再重复缩放()
        {
            var src = new LayoutConfig { RowHeight = 40 };
            var scaled = src.CreatePreviewCopy(2.0f);
            Assert.Equal(80, scaled.RowHeight);

            var again = scaled.CreatePreviewCopy(2.0f);
            Assert.Equal(80, again.RowHeight);   // 没有变成 160
        }

        [Fact]
        public void 预览副本_复制扩展数据()
        {
            var theme = Load("""{"version":3,"layout":{"customThing":{"a":1}}}""");
            var copy = theme.Layout.CreatePreviewCopy(1.0f);
            Assert.True(copy.ExtensionData.ContainsKey("customThing"));
            Assert.NotSame(theme.Layout.ExtensionData, copy.ExtensionData);
        }

        // ============================================================
        // 快照本身的约束
        // ============================================================

        [Fact]
        public void Options_不持有Theme字体画刷画笔与控件()
        {
            Type[] forbidden = { typeof(Theme), typeof(Font), typeof(Brush), typeof(Pen), typeof(Control) };

            foreach (var p in typeof(ThemeAppearanceOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var t in forbidden)
                {
                    Assert.False(t.IsAssignableFrom(p.PropertyType),
                        $"{p.Name} 不应持有 {t.Name}");
                }
            }
        }

        [Fact]
        public void Options_所有属性只读_不可变快照()
        {
            foreach (var p in typeof(ThemeAppearanceOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.True(p.CanRead, $"{p.Name} 必须可读");
                Assert.Null(p.SetMethod);
            }
        }

        [Fact]
        public void Options_不持有整个主题对象()
        {
            var theme = new Theme();
            var opt = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, Bounds);

            Assert.DoesNotContain(
                typeof(ThemeAppearanceOptions).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                f => f.FieldType == typeof(Theme));
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
    }
}
