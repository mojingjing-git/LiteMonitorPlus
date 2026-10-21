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
        {
            theme.Layout.Width = (int)(PanelWidthLogical * finalScale);

            string[] groupNames = { "CPU", "MEM", "MOBO", "DISK", "NET", "HOST", "GPU", "SYS", "IO", "TEMP", "PWR", "FAN" };
            string[] itemKeys = { "CPU.LOAD", "MEM.LOAD", "MOBO.TEMP", "CPU.TEMP", "CPU.LOAD", "MEM.LOAD" };
            string[] itemLabels = { "CPU", "MEM", "HOST", "TEMP", "CPU 2", "MEM 2" };

            List<GroupLayoutInfo> groups = new();
            for (int gi = 0; gi < GroupCount; gi++)
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
            using (Graphics g = Graphics.FromImage(canvas))
            {
                if (scene.IsHorizontal)
                    HorizontalRenderer.Render(g, scene.Theme, scene.Columns, scene.Width, appearance);
                else
                    UIRenderer.Render(g, scene.Groups, scene.Theme, appearance);
            }
            return canvas;
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
