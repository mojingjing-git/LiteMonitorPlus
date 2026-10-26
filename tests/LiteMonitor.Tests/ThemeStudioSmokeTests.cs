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

        private static ThemePropertyDescriptor Find(string path) =>
            System.Linq.Enumerable.Single(
                ThemePropertyCatalog.ClassicProperties,
                p => p.JsonPath == path);
    }
}
