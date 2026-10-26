using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// M1 收口回归：工作室窗体可构造、经典主题端到端闭环（编辑→保存→重开）、
    /// 预览 GPU 启动保护与生产隔离（TE00 第 5 节结论 / 重做计划 §6.3）。
    /// </summary>
    public class ThemeStudioSmokeTests : IDisposable
    {
        private readonly string _dir;

        public ThemeStudioSmokeTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "litemonitor-m1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            ThemeDocumentStore.SetThemeDirForTests(_dir);
            ThemeDocumentStore.SetRecoveryDirForTests(Path.Combine(_dir, "recovery"));
        }

        public void Dispose()
        {
            ThemeDocumentStore.SetThemeDirForTests(null);
            ThemeDocumentStore.ResetRecoveryDir();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private const string ClassicJson = @"{
  ""version"": 3,
  ""name"": ""经典蓝"",
  ""layout"": { ""rowHeight"": 40, ""padding"": 12 },
  ""font"": { ""family"": ""Microsoft YaHei UI"", ""value"": 14.0 },
  ""color"": { ""background"": ""#3C3C3C"", ""textTitle"": ""#FFFFFF"" },
  ""x-unknown"": { ""keep"": [1, 2] }
}";

        [Fact]
        public void 工作室窗体_可构造_原生窗口行为由系统管理()
        {
            Application.EnableVisualStyles();

            using var form = new ThemeStudioForm();

            Assert.NotNull(form);
            Assert.True(form.IsHandleCreated);
            // 系统非客户区：不设自定义边框，标题栏由 Windows 绘制
            Assert.Equal(FormBorderStyle.Sizable, form.FormBorderStyle);
            Assert.False(form.MaximizeBox == false); // 最大化可用
            Assert.True(form.MinimizeBox);
        }

        [Fact]
        public void 经典主题_编辑保存重开_内容一致且未知键保留()
        {
            File.WriteAllText(Path.Combine(_dir, "经典蓝.json"), ClassicJson, new UTF8Encoding(false));

            // 第一会话：编辑并保存
            ThemeDocumentSession first = ThemeDocumentSession.Open("经典蓝");
            ThemePropertyDescriptor rowHeight = Find("layout.rowHeight");
            double original = Convert.ToDouble(rowHeight.Getter(first.Draft));
            first.ApplyEdit(new ThemeFieldEdit(
                rowHeight.JsonPath,
                rowHeight.ToJson(original + 10),
                theme => rowHeight.Setter(theme, original + 10)));
            Assert.True(first.Save().Success);

            // 重开：内容一致、未知键保留、无脏状态
            ThemeDocumentSession second = ThemeDocumentSession.Open("经典蓝");
            Assert.Equal(original + 10, Convert.ToDouble(rowHeight.Getter(second.Draft)));
            Assert.False(second.IsDirty);
            Assert.True(second.Draft.ExtensionData.ContainsKey("x-unknown"));

            string saved = File.ReadAllText(Path.Combine(_dir, "经典蓝.json"));
            JsonNode? unknown = JsonNode.Parse(saved)?["x-unknown"]?["keep"];
            Assert.NotNull(unknown);
            Assert.DoesNotContain("\"RowHeight\"", saved);
        }

        [Fact]
        public void 预览启动保护_与生产隔离()
        {
            // 预览与生产必须是两个 guard 实例：预览不写生产 pending、不消耗生产保守状态
            Assert.NotSame(GpuStartupGuard.Shared, GpuThemePreviewControl.PreviewGuard);
        }

        [Fact]
        public void 工作室_打开经典主题_画布产出同源预览证据图()
        {
            Application.EnableVisualStyles();
            File.WriteAllText(Path.Combine(_dir, "经典蓝.json"), ClassicJson, new UTF8Encoding(false));

            string evidenceDir = Path.Combine(Path.GetTempPath(), "LiteMonitor.Tests", "M1-studio-evidence");
            Directory.CreateDirectory(evidenceDir);
            string png = Path.Combine(evidenceDir, "theme-studio-classic.png");

            Exception? failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    using var form = new ThemeStudioForm();
                    form.Show();
                    Application.DoEvents();
                    form.OpenThemeForTesting("经典蓝");
                    form.RefreshPreviewForTesting();
                    Application.DoEvents();

                    Assert.Contains("经典蓝", form.Text);
                    Assert.True(form.InspectorForTesting.Controls.Count > 0);

                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                    form.Close();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(30));

            Assert.Null(failure);
            Assert.True(new FileInfo(png).Length > 0);
            Console.WriteLine("[M1] 工作室证据图：" + png);
        }

        [Fact]
        public void 工作室_GPU主题_目标切换到兼容外观_不改后端()
        {
            Application.EnableVisualStyles();
            const string gpu = @"{
  ""name"": ""玻璃"",
  ""rendering"": { ""version"": 1, ""backend"": ""gpu"", ""material"": ""liquid_glass"",
    ""parameters"": { ""liveBackground"": false },
    ""fallback"": { ""backend"": ""legacy"", ""material"": ""legacy"" } }
}";
            File.WriteAllText(Path.Combine(_dir, "玻璃.json"), gpu, new UTF8Encoding(false));

            Exception? failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    using var form = new ThemeStudioForm();
                    form.Show();
                    Application.DoEvents();
                    form.OpenThemeForTesting("玻璃");
                    form.RefreshPreviewForTesting();
                    Application.DoEvents();

                    // 主效果视图：液态玻璃参数可见，无 fallbackStyle 槽
                    Assert.True(form.InspectorForTesting.Controls.Count > 0);

                    form.SelectTargetForTesting(ThemeEditTarget.GpuFallback);
                    Application.DoEvents();

                    // 兼容外观视图：六槽描述符已绑定（通过目录断言）
                    var fallbackProps = ThemePropertyCatalog.GetProperties(
                        form.InspectorForTesting.Draft!, ThemeEditTarget.GpuFallback);
                    Assert.Contains(fallbackProps, p =>
                        p.JsonPath == "rendering.fallbackStyle.accent" && p.DisplayName == "标题强调色");
                    form.Close();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(30));

            Assert.Null(failure);
            // 切目标不写盘、不改后端
            string saved = File.ReadAllText(Path.Combine(_dir, "玻璃.json"));
            Assert.Equal(gpu, saved);
        }

        [Fact]
        public void 工作室_预览形态切换_横向与任务栏可用()
        {
            Application.EnableVisualStyles();
            File.WriteAllText(Path.Combine(_dir, "经典蓝.json"), ClassicJson, new UTF8Encoding(false));

            Exception? failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    using var form = new ThemeStudioForm();
                    form.Show();
                    Application.DoEvents();
                    form.OpenThemeForTesting("经典蓝");
                    form.RefreshPreviewForTesting();
                    Application.DoEvents();

                    // 横向：同源控件已建立、有列、有内容宽度
                    form.SelectPreviewShapeForTesting(1);
                    Application.DoEvents();
                    Assert.NotNull(form.HorizontalPreviewForTesting);
                    Assert.True(form.HorizontalPreviewForTesting!.Visible);
                    Assert.NotEmpty(form.HorizontalPreviewForTesting.PreviewColumns);
                    Assert.True(form.HorizontalPreviewForTesting.ContentWidth > 0);

                    // 任务栏：事实标注存在（颜色由设置驱动）
                    form.SelectPreviewShapeForTesting(2);
                    Application.DoEvents();
                    Assert.NotNull(form.TaskbarPreviewForTesting);
                    Assert.True(form.TaskbarPreviewForTesting!.Visible);
                    Assert.Contains("颜色由设置驱动", TaskbarPreviewControl.ColorSourceNotice);

                    // 切回竖向：横向隐藏
                    form.SelectPreviewShapeForTesting(0);
                    Application.DoEvents();
                    Assert.False(form.HorizontalPreviewForTesting!.Visible);
                    form.Close();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(30));

            Assert.Null(failure);
        }

        private static ThemePropertyDescriptor Find(string path) =>
            System.Linq.Enumerable.Single(
                ThemePropertyCatalog.ClassicProperties,
                p => p.JsonPath == path);
    }
}
