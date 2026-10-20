using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Decor;

namespace LiteMonitor.AppearanceDecorRendererProbe
{
    /// <summary>
    /// 探针夹具：主题加载、指标构造、离屏画布与像素工具。
    ///
    /// ★ 隔离：探针自己 <c>new Settings()</c> 并注入 <c>Settings._instance</c> 单例，
    ///   因此<b>不读也不写</b>用户常用配置目录（settings.json）。
    /// ★ 探针不创建 HardwareMonitor、不 Open 硬件、不做任何屏幕捕获。
    /// </summary>
    internal static class ProbeFixture
    {
        /// <summary>探针使用的 10 个旧主题（与 resources/themes 一一对应）。</summary>
        public static readonly string[] LegacyThemeNames =
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

        // ============================================================
        // 隔离与主题
        // ============================================================

        /// <summary>注入隔离的 Settings 单例（避免读取用户 settings.json）。</summary>
        public static void InstallSettingsSingleton()
        {
            FieldInfo? field = typeof(Settings).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Settings._instance 字段不存在，无法注入探针隔离配置。");
            field.SetValue(null, new Settings());
        }

        /// <summary>
        /// 加载旧主题（直接读 JSON，解析选项与生产 <c>ThemeManager.Load</c> 一致，
        /// 但不走"加载失败回退内置主题"的兜底，避免把回退主题当成旧主题）。
        /// </summary>
        public static Theme LoadLegacyTheme(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "resources", "themes", name + ".json");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("主题文件不存在。", path);
            }

            string json = File.ReadAllText(path);
            Theme? theme = System.Text.Json.JsonSerializer.Deserialize<Theme>(json, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                IgnoreReadOnlyProperties = true,
                AllowTrailingCommas = true
            });
            if (theme == null)
            {
                throw new InvalidOperationException($"主题 {name} 解析失败。");
            }

            theme.BuildFonts();
            return theme;
        }

        /// <summary>
        /// 造一个"机能风"主题（用于开启全部新效果）。
        /// </summary>
        public static Theme BuildMechTheme(
            string name,
            int frameStyle = 1,
            int frameWidth = 2,
            int frameLength = 12,
            int scanline = 40,
            int glow = 50,
            int grid = 20,
            int barStyle = 1)
        {
            Theme theme = LoadLegacyTheme("CyberBlue_Neon");
            theme.Name = name;
            theme.Version = 4;
            theme.Layout.FrameStyle = frameStyle;
            theme.Layout.FrameWidth = frameWidth;
            theme.Layout.FrameLength = frameLength;
            theme.Layout.Scanline = scanline;
            theme.Layout.Glow = glow;
            theme.Layout.Grid = grid;
            theme.Layout.BarStyle = barStyle;
            return theme;
        }

        // ============================================================
        // 指标与布局
        // ============================================================

        /// <summary>构造一个指标项（不依赖硬件、不触碰 InfoService）。</summary>
        public static MetricItem BuildItem(string key, string label, float value)
        {
            MonitorItemConfig config = new MonitorItemConfig
            {
                Key = key,
                UserLabel = label,      // 短路 MetricLabelResolver，避免触碰 InfoService
                TaskbarLabel = label
            };

            MetricItem item = new MetricItem { Key = key, BoundConfig = config };
            item.Label = label;
            item.ShortLabel = label;
            item.DisplayValue = value;
            item.GetFormattedText(false);   // 填充 CachedPercent / CachedColorState / 文本缓存
            return item;
        }

        /// <summary>竖屏夹具（默认两组，各两项）。</summary>
        public static ProbeFrame BuildPortrait(Theme theme, int width) => BuildPortrait(theme, width, 2, 2);

        /// <summary>竖屏夹具（groupCount 组，每组 itemsPerGroup 项）。</summary>
        public static ProbeFrame BuildPortrait(Theme theme, int width, int groupCount, int itemsPerGroup)
        {
            theme.Layout.Width = width;

            ProbeFrame frame = new ProbeFrame { Theme = theme, Width = width, IsHorizontal = false };

            string[] groupNames = { "CPU", "MEM", "MOBO", "DISK", "NET", "HOST", "GPU", "SYS", "IO", "TEMP", "PWR", "FAN" };
            string[] itemKeys = { "CPU.LOAD", "MEM.LOAD", "MOBO.TEMP", "CPU.TEMP", "CPU.LOAD", "MEM.LOAD" };
            string[] itemLabels = { "CPU", "MEM", "HOST", "TEMP", "CPU 2", "MEM 2" };

            for (int gi = 0; gi < groupCount; gi++)
            {
                List<MetricItem> items = new();
                for (int ii = 0; ii < itemsPerGroup; ii++)
                {
                    items.Add(BuildItem(
                        itemKeys[(gi + ii) % itemKeys.Length],
                        itemLabels[(gi + ii) % itemLabels.Length],
                        40f + gi * 3f + ii));
                }
                string groupName = groupNames[gi % groupNames.Length];
                frame.Groups.Add(new GroupLayoutInfo(groupName, items) { Label = groupName });
            }

            UILayout layout = new UILayout(theme);
            frame.Height = layout.Build(frame.Groups);

            foreach (GroupLayoutInfo group in frame.Groups)
            {
                frame.GroupRegions.Add(new DecorGroupRegion(group.Bounds, theme.Layout.GroupRadius));
                foreach (MetricItem item in group.Items)
                {
                    frame.TextRects.Add(item.LabelRect);
                    frame.TextRects.Add(item.ValueRect);
                    if (item.BarRect.Width > 0 && item.BarRect.Height > 0) frame.BarRects.Add(item.BarRect);
                }
            }
            return frame;
        }

        /// <summary>横屏夹具（列模型，<b>本来就没有进度条</b>）。</summary>
        public static ProbeFrame BuildHorizontal(Theme theme, int width, int height)
        {
            ProbeFrame frame = new ProbeFrame { Theme = theme, Width = width, Height = height, IsHorizontal = true };

            for (int i = 0; i < 4; i++)
            {
                Column column = new Column
                {
                    ColumnWidth = width / 4,
                    Bounds = new Rectangle(i * (width / 4), 0, width / 4, height),
                    Top = BuildItem("CPU.LOAD", "CPU", 40f + i),
                    Bottom = BuildItem("CPU.TEMP", "TEMP", 50f + i)
                };
                column.BoundsTop = new Rectangle(column.Bounds.X, 2, column.Bounds.Width, height / 2 - 4);
                column.BoundsBottom = new Rectangle(column.Bounds.X, height / 2 + 2, column.Bounds.Width, height / 2 - 4);
                frame.Columns.Add(column);
            }
            return frame;
        }

        // ============================================================
        // 像素工具
        // ============================================================

        /// <summary>新建离屏画布（32bpp ARGB）。</summary>
        public static Bitmap CreateCanvas(int width, int height)
        {
            if (width <= 0 || height <= 0) return new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
            return new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        }

        /// <summary>取画布的原始像素字节（BGRA，32bpp）。</summary>
        public static byte[] GetPixels(Bitmap bitmap)
        {
            Rectangle rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                byte[] buffer = new byte[Math.Abs(data.Stride) * data.Height];
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
                return buffer;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        /// <summary>像素哈希（SHA256，十六进制小写）。</summary>
        public static string HashPixels(Bitmap bitmap)
        {
            byte[] pixels = GetPixels(bitmap);
            byte[] hash = SHA256.HashData(pixels);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>统计 alpha &gt; 0 的像素数。</summary>
        public static int CountVisiblePixels(Bitmap bitmap)
        {
            byte[] pixels = GetPixels(bitmap);
            int count = 0;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0) count++;
            }
            return count;
        }

        /// <summary>
        /// 统计客户区外被改动的像素数（哨兵色比对）。
        /// </summary>
        public static int CountChangedOutside(Bitmap canvas, Rectangle client, Color sentinel)
        {
            byte[] pixels = GetPixels(canvas);
            int changed = 0;
            int width = canvas.Width;
            byte sr = sentinel.R;
            byte sg = sentinel.G;
            byte sb = sentinel.B;
            byte sa = sentinel.A;

            for (int y = 0; y < canvas.Height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool inside = x >= client.Left && x < client.Right && y >= client.Top && y < client.Bottom;
                    if (inside) continue;

                    int offset = y * width * 4 + x * 4;
                    if (pixels[offset + 3] != sa || pixels[offset] != sb || pixels[offset + 1] != sg || pixels[offset + 2] != sr)
                    {
                        changed++;
                    }
                }
            }
            return changed;
        }

        /// <summary>取某个像素的 ARGB。</summary>
        public static Color GetArgb(Bitmap bitmap, int x, int y) => bitmap.GetPixel(x, y);

        /// <summary>两张画布是否逐像素一致。</summary>
        public static bool SamePixels(Bitmap a, Bitmap b)
        {
            if (a.Width != b.Width || a.Height != b.Height) return false;
            byte[] pa = GetPixels(a);
            byte[] pb = GetPixels(b);
            if (pa.Length != pb.Length) return false;
            for (int i = 0; i < pa.Length; i++)
            {
                if (pa[i] != pb[i]) return false;
            }
            return true;
        }

        /// <summary>统计两张画布不同的像素数。</summary>
        public static int CountDifferentPixels(Bitmap a, Bitmap b)
        {
            if (a.Width != b.Width || a.Height != b.Height) return -1;
            byte[] pa = GetPixels(a);
            byte[] pb = GetPixels(b);
            int different = 0;
            for (int i = 0; i < pa.Length; i += 4)
            {
                if (pa[i] != pb[i] || pa[i + 1] != pb[i + 1] || pa[i + 2] != pb[i + 2] || pa[i + 3] != pb[i + 3])
                {
                    different++;
                }
            }
            return different;
        }
    }

    /// <summary>探针夹具（一帧的完整上下文）。</summary>
    internal sealed class ProbeFrame
    {
        public Theme Theme = null!;
        public List<GroupLayoutInfo> Groups = new();
        public List<Column> Columns = new();
        public List<DecorGroupRegion> GroupRegions = new();
        public List<Rectangle> TextRects = new();
        public List<Rectangle> BarRects = new();
        public int Width;
        public int Height;
        public bool IsHorizontal;
    }
}
