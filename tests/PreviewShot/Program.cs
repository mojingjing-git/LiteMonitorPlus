using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using LiteMonitor;
using LiteMonitor.ThemeEditor;
using LiteMonitor.src.Core;

namespace PreviewShot
{
    /// <summary>
    /// 主题离屏预览：PreviewShot &lt;themeName&gt; [outPath] [width height]
    /// 走与主题工作室预览同源的渲染入口（UILayout + ThemePreviewControl.RenderPreview），
    /// 画面与主窗口一致（渐变背景 / 组块 / 外框 / 辉光 / 渐变条全部生效）。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string themeName = args.Length > 0 ? args[0] : "RefinedDark";
            string outPath = args.Length > 1
                ? args[1]
                : Path.Combine(AppContext.BaseDirectory, "shot-" + themeName + ".png");
            int width = args.Length > 3 ? int.Parse(args[2]) : 280;
            int height = args.Length > 3 ? int.Parse(args[3]) : 980;
            bool horizontal = args.Length > 4 && args[4] == "h";

            string? repoRoot = LocateRepoRoot();
            if (repoRoot is null)
            {
                Console.Error.WriteLine("[FAIL] 找不到仓库根目录（需要 LiteMonitor.csproj 与 resources/themes）。");
                return 2;
            }

            string themeDir = Path.Combine(repoRoot, "resources", "themes");
            Theme theme = ThemeFileService.LoadTheme(themeDir, themeName);
            Theme previewTheme = ThemePreviewControl.CreatePreviewTheme(theme, 1.0f);

            var bounds = new Rectangle(0, 0, width, height);
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                if (horizontal)
                {
                    // 横条 / 任务栏画幅：HorizontalLayout + HorizontalRenderer（生产同源），
                    // 自建预览外观运行时让渐变底 / 描边 / 辉光真实生效。
                    using var appearance = new AppearanceRuntime(new PreviewFrameSource(null));
                    appearance.Update(previewTheme, 1.0f, 1.0f, bounds);

                    var settings = new Settings { HorizontalSingleLine = false };
                    List<Column> columns = ThemePreviewScene.BuildColumns(ThemePreviewScene.CreateMetrics(), singleLine: false);
                    var layout = new HorizontalLayout(previewTheme, Math.Max(1, width), LayoutMode.Horizontal, settings, null);
                    layout.Build(columns);
                    HorizontalRenderer.Render(g, previewTheme, columns, layout.PanelWidth, appearance);
                }
                else
                {
                    List<GroupLayoutInfo> groups = ThemePreviewControl.CreateMockGroups();
                    var layout = new UILayout(previewTheme);
                    layout.Build(groups);
                    ThemePreviewControl.RenderPreview(g, groups, previewTheme, bounds);
                }
            }

            string? dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            bitmap.Save(outPath, ImageFormat.Png);
            Console.WriteLine("saved: " + outPath);
            return 0;
        }

        private static string? LocateRepoRoot()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                bool hasProject = File.Exists(Path.Combine(dir.FullName, "LiteMonitor.csproj"));
                bool hasThemes = Directory.Exists(Path.Combine(dir.FullName, "resources", "themes"));
                if (hasProject && hasThemes) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
