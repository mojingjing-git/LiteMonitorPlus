using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LiteMonitor.src.Core;

namespace LiteMonitor.AppearanceWiringProbe
{
    /// <summary>
    /// A13 接线探针的夹具与像素工具。
    ///
    /// ★ 隔离：探针自己 <c>new Settings()</c> 并注入 <c>Settings._instance</c> 单例，
    ///   因此<b>不读也不写</b>用户常用配置目录（settings.json）。
    /// ★ 探针不创建 HardwareMonitor、不 Open 硬件、不做任何屏幕捕获。
    /// </summary>
    internal static class WiringFixture
    {
        /// <summary>A00 基线锁定的 10 个旧主题（不可变基线，一个都不能少）。</summary>
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

        /// <summary>A12 新增的机能主题（唯一开启了全部新效果的主题）。</summary>
        public const string MechThemeName = "CyberPunk_Mech";

        /// <summary>面板逻辑宽度（与生产默认值一致）。</summary>
        public const int PanelWidthLogical = 240;

        /// <summary>竖屏组数（与 A04 夹具的 7 组一致）。</summary>
        public const int GroupCount = 7;

        /// <summary>每组项数（与 A04 夹具一致）。</summary>
        public const int ItemsPerGroup = 2;

        /// <summary>注入隔离的 Settings 单例（避免读取用户 settings.json）。</summary>
        public static void InstallSettingsSingleton()
        {
            FieldInfo? field = typeof(Settings).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Settings._instance 字段不存在，无法注入探针隔离配置。");
            field.SetValue(null, new Settings());
        }

        /// <summary>
        /// 加载主题（直接读 JSON）。
        /// <para>
        /// 解析选项与生产 <c>ThemeManager.Load</c> 一致（同样的
        /// <c>PropertyNameCaseInsensitive / IgnoreReadOnlyProperties / AllowTrailingCommas</c>），
        /// 但不走"加载失败回退内置主题"的兜底 —— 避免把回退主题当成旧主题。
        /// </para>
        /// </summary>
        public static Theme LoadTheme(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "resources", "themes", name + ".json");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("主题文件不存在。", path);
            }

            string json = File.ReadAllText(path);
            Theme? theme = JsonSerializer.Deserialize<Theme>(json, new JsonSerializerOptions
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

        // ============================================================
        // 夹具
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

        /// <summary>
        /// 造一个竖屏场景（7 组 × 2 项）。
        /// <para>宽度按 <c>panelWidthLogical × finalScale</c> 设定，与生产 <c>ApplyTheme</c> 同口径。</para>
        /// </summary>
        public static WiringScene BuildPortrait(Theme theme, float finalScale)
            => BuildPortrait(theme, finalScale, GroupCount);

        /// <summary>造一个竖屏场景（指定组数，每组 2 项）。</summary>
        public static WiringScene BuildPortrait(Theme theme, float finalScale, int groupCount)
        {
            theme.Layout.Width = (int)(PanelWidthLogical * finalScale);

            string[] groupNames = { "CPU", "MEM", "MOBO", "DISK", "NET", "HOST", "GPU", "SYS", "IO", "TEMP", "PWR", "FAN" };
            string[] itemKeys = { "CPU.LOAD", "MEM.LOAD", "MOBO.TEMP", "CPU.TEMP", "CPU.LOAD", "MEM.LOAD" };
            string[] itemLabels = { "CPU", "MEM", "HOST", "TEMP", "CPU 2", "MEM 2" };

            List<GroupLayoutInfo> groups = new();
            for (int gi = 0; gi < groupCount; gi++)
            {
                List<MetricItem> items = new();
                for (int ii = 0; ii < ItemsPerGroup; ii++)
                {
                    items.Add(BuildItem(
                        itemKeys[(gi + ii) % itemKeys.Length],
                        itemLabels[(gi + ii) % itemLabels.Length],
                        40f + gi * 3f + ii));
                }
                string groupName = groupNames[gi % groupNames.Length];
                groups.Add(new GroupLayoutInfo(groupName, items) { Label = groupName });
            }

            int height = new UILayout(theme).Build(groups);
            if (height <= 0) height = 1;

            return new WiringScene
            {
                Theme = theme,
                Groups = groups,
                Width = theme.Layout.Width,
                Height = height,
                IsHorizontal = false
            };
        }

        /// <summary>造一个横屏场景（列模型，4 列）。</summary>
        public static WiringScene BuildHorizontal(Theme theme, int width, int height)
        {
            List<Column> columns = new();
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
                columns.Add(column);
            }

            return new WiringScene
            {
                Theme = theme,
                Columns = columns,
                Width = width,
                Height = height,
                IsHorizontal = true
            };
        }

        // ============================================================
        // 渲染
        // ============================================================

        /// <summary>
        /// 渲染一帧。
        /// <para>
        /// ★ <paramref name="appearance"/> 为 null 时走**旧入口**（不含任何新外观层）；
        ///   非 null 时走**新入口**（A13 接线后的生产路径）。
        /// </para>
        /// </summary>
        public static Bitmap Render(WiringScene scene, AppearanceRuntime? appearance)
        {
            Bitmap canvas = new Bitmap(Math.Max(1, scene.Width), Math.Max(1, scene.Height), PixelFormat.Format32bppPArgb);
            RenderOnto(canvas, scene, appearance);
            return canvas;
        }

        /// <summary>
        /// 把一帧渲染到**已有画布**上（不清空）。
        /// <para>用于"同一张画布先后渲染不同布局"的跨帧污染检测。</para>
        /// </summary>
        /// <param name="canvas">目标画布。</param>
        /// <param name="scene">场景。</param>
        /// <param name="appearance">外观运行时（null = 旧入口）。</param>
        /// <param name="clip">
        /// 可选的**调用方裁剪区**（模拟局部失效）。
        /// <para>
        /// ★ 局部失效时 WinForms 会把 Graphics 的可见区限制在失效矩形上，
        ///   绘制层如果按 <c>VisibleClipBounds</c> 定位底板就会画偏 ——
        ///   这个参数用来复现该场景（独立验收 R5 的反例）。
        /// </para>
        /// </param>
        public static void RenderOnto(Bitmap canvas, WiringScene scene, AppearanceRuntime? appearance, Rectangle? clip = null)
        {
            using (Graphics g = Graphics.FromImage(canvas))
            {
                if (clip.HasValue)
                {
                    using Region region = new Region(clip.Value);
                    g.Clip = region;
                }

                if (scene.IsHorizontal)
                    HorizontalRenderer.Render(g, scene.Theme, scene.Columns, scene.Width, appearance);
                else
                    UIRenderer.Render(g, scene.Groups, scene.Theme, appearance);
            }
        }

        /// <summary>
        /// 统计指定矩形内**恰好等于某颜色**的像素数。
        /// <para>
        /// ★ 用途：模拟局部失效时先在画布上预填一个哨兵色（如洋红），
        ///   渲染后若该色仍出现在失效区里，就说明那一块**没有被补底色**。
        /// </para>
        /// </summary>
        public static int CountColorPixelsInRect(Bitmap bitmap, Rectangle rect, Color color)
        {
            int count = 0;
            int x0 = Math.Max(0, rect.Left);
            int y0 = Math.Max(0, rect.Top);
            int x1 = Math.Min(bitmap.Width, rect.Right);
            int y1 = Math.Min(bitmap.Height, rect.Bottom);

            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    if (bitmap.GetPixel(x, y).ToArgb() == color.ToArgb()) count++;
                }
            }
            return count;
        }

        /// <summary>把画布清成全透明（作为"重新开始"的起点）。</summary>
        public static void ClearCanvas(Bitmap canvas)
        {
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.Clear(Color.Transparent);
            }
        }

        // ============================================================
        // 像素工具
        // ============================================================

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
            byte[] hash = SHA256.HashData(GetPixels(bitmap));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// 把画布存成 PNG（**像素级回归证据**）。
        /// <para>
        /// ★ 画布是 32bppPArgb（预乘 alpha），存盘前克隆成 32bppArgb 以解除预乘，
        ///   否则半透明像素的颜色会被还原成错误的值。
        /// ★ 哈希只能回答"变没变"，PNG 才能回答"变在哪"——
        ///   渲染路径优化前后必须像素零变化时，靠它逐像素定位差异区域。
        /// </para>
        /// </summary>
        public static void SavePng(Bitmap bitmap, string path)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using Image image = bitmap.Clone(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height), PixelFormat.Format32bppArgb);
            image.Save(path, ImageFormat.Png);
        }

        /// <summary>
        /// 统计 alpha **小于 255** 的像素数（画布上"没被画满"的像素）。
        /// <para>
        /// ★ 画布是 32bppPArgb（预乘 alpha），alpha 通道仍是真实 alpha。
        /// ★ 用途：证明背景层是否真的把**整个客户区**铺满了 ——
        ///   客户区最外一圈如果 alpha &lt; 255，说明底板 / 渐变没有覆盖到边界。
        /// </para>
        /// </summary>
        public static int CountSemiTransparentPixels(Bitmap bitmap)
        {
            byte[] pixels = GetPixels(bitmap);
            int count = 0;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 255) count++;
            }
            return count;
        }

        /// <summary>
        /// 取某一行的 alpha 统计：最小值 / 最大值 / 不满 255 的像素数。
        /// </summary>
        public static (int Min, int Max, int NotOpaque) GetRowAlphaSummary(Bitmap bitmap, int row)
        {
            if (row < 0 || row >= bitmap.Height) return (0, 0, 0);

            byte[] pixels = GetPixels(bitmap);
            int stride = bitmap.Width * 4;
            int min = 255;
            int max = 0;
            int notOpaque = 0;
            int offset = row * stride;
            for (int x = 0; x < bitmap.Width; x++)
            {
                int alpha = pixels[offset + x * 4 + 3];
                if (alpha < min) min = alpha;
                if (alpha > max) max = alpha;
                if (alpha != 255) notOpaque++;
            }
            return (min, max, notOpaque);
        }

        /// <summary>
        /// 取某一列的 alpha 统计：最小值 / 最大值 / 不满 255 的像素数。
        /// </summary>
        public static (int Min, int Max, int NotOpaque) GetColumnAlphaSummary(Bitmap bitmap, int column)
        {
            if (column < 0 || column >= bitmap.Width) return (0, 0, 0);

            byte[] pixels = GetPixels(bitmap);
            int stride = bitmap.Width * 4;
            int min = 255;
            int max = 0;
            int notOpaque = 0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                int alpha = pixels[y * stride + column * 4 + 3];
                if (alpha < min) min = alpha;
                if (alpha > max) max = alpha;
                if (alpha != 255) notOpaque++;
            }
            return (min, max, notOpaque);
        }

        /// <summary>统计两张画布不同的像素数（尺寸不同返回 -1）。</summary>
        public static int CountDifferentPixels(Bitmap a, Bitmap b)
        {
            if (a.Width != b.Width || a.Height != b.Height) return -1;
            byte[] pa = GetPixels(a);
            byte[] pb = GetPixels(b);
            if (pa.Length != pb.Length) return -1;

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

        /// <summary>取某个像素的 ARGB。</summary>
        public static Color GetArgb(Bitmap bitmap, int x, int y) => bitmap.GetPixel(x, y);
    }

    /// <summary>探针场景（一帧的完整上下文）。</summary>
    internal sealed class WiringScene
    {
        public Theme Theme = null!;
        public List<GroupLayoutInfo> Groups = new();
        public List<Column> Columns = new();
        public int Width;
        public int Height;
        public bool IsHorizontal;
    }
}
