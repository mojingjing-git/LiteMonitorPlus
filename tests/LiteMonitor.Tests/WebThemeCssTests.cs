using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Core;
using LiteMonitor.src.WebServer;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// Web 端主题配色适配测试（外观改造计划 A11 / v4 §7.3）。
    ///
    /// 覆盖：
    ///  - 正常主题 → CSS 变量正确（rgba 的 r/g/b/a 数值正确）
    ///  - <c>#AARRGGBB</c> 按 ARGB 拆解（**不能**当成 CSS 的 <c>#RRGGBBAA</c>）
    ///  - <c>#RRGGBB</c> 六位输入正确
    ///  - 非法 / 未知色值 → 明确回退色（不抛异常、不把原文插入）
    ///  - 注入输入（<c>&lt;/style&gt;</c> / <c>&lt;script&gt;</c> / 注释 / url() / 换行 …）
    ///    不会成为注入内容，且输出的变量块结构完整
    ///  - 区域设置（de-DE 小数逗号）不影响输出
    ///  - HTML / 图标请求**不产生**任何硬件采样需求
    ///  - 换主题后重新生成 HTML 得到新配色（刷新跟随，不推送不轮询）
    /// </summary>
    public class WebThemeCssTests
    {
        // ================================================================
        // 辅助
        // ================================================================

        /// <summary>造一个只指定背景色的主题。</summary>
        private static Theme NewTheme(string background)
        {
            Theme theme = new Theme();
            theme.Color = new ColorConfig();
            theme.Color.Background = background;
            return theme;
        }

        /// <summary>
        /// 与生产挂载点**完全一致**的 HTML 组装（WebSessionManager → HandleHttpRequestAsync）。
        /// </summary>
        private static string BuildHtml(string themeCss)
        {
            return WebPageContent.IndexHtml
                .Replace("{{FAVICON}}", "")
                .Replace("{{AUTH_REQUIRED}}", "false")
                .Replace(WebPageContent.ThemeCssPlaceholder, themeCss);
        }

        /// <summary>统计子串出现次数。</summary>
        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return 0;
            int count = 0;
            int index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        /// <summary>在仓库里定位源文件（从测试输出目录向上找仓库根）。</summary>
        private static string FindRepoFile(string relativePath)
        {
            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, relativePath);
                if (File.Exists(candidate)) return candidate;
                if (File.Exists(Path.Combine(dir.FullName, "LiteMonitor.csproj"))) return null;
                dir = dir.Parent;
            }
            return null;
        }

        // ================================================================
        // 1. 正常主题
        // ================================================================

        [Fact]
        public void 正常主题_快照生成正确的CSS变量()
        {
            Theme theme = new Theme();
            theme.Color = new ColorConfig();
            theme.Color.Background = "#202225";
            theme.Color.GroupBackground = "#2B2D31";
            theme.Color.Border = "#404040";
            theme.Color.TextPrimary = "#EAEAEA";
            theme.Color.TextSecondary = "#B0B0B0";
            theme.Color.ValueSafe = "#66FF99";
            theme.Color.ValueWarn = "#FFD666";
            theme.Color.ValueCrit = "#FF6666";

            WebThemeColorSnapshot snapshot = WebThemeColorSnapshot.FromTheme(theme);

            // 快照本身保存的是**解析后的 Color**
            Assert.Equal(Color.FromArgb(255, 0x20, 0x22, 0x25), snapshot.Background);
            Assert.Equal(Color.FromArgb(255, 0x66, 0xFF, 0x99), snapshot.ValueSafe);

            string css = WebThemeCss.BuildCssVariables(snapshot);

            Assert.Contains("--bg: rgba(32,34,37,1);", css);
            Assert.Contains("--card: rgba(43,45,49,1);", css);
            Assert.Contains("--border: rgba(64,64,64,1);", css);
            Assert.Contains("--text-main: rgba(234,234,234,1);", css);
            Assert.Contains("--text-sub: rgba(176,176,176,1);", css);
            Assert.Contains("--c-0: rgba(102,255,153,1);", css);
            Assert.Contains("--c-1: rgba(255,214,102,1);", css);
            Assert.Contains("--c-2: rgba(255,102,102,1);", css);

            // 结构完整：:root 开头、8 个变量、收尾大括号
            Assert.StartsWith(":root {", css);
            Assert.EndsWith("}", css);
            Assert.Equal(8, CountOccurrences(css, "--"));
        }

        [Fact]
        public void 颜色名_按已知颜色解析()
        {
            Theme theme = NewTheme("Red");
            WebThemeColorSnapshot snapshot = WebThemeColorSnapshot.FromTheme(theme);
            // 用 ToArgb 比较（Color.Equals 还会比对颜色名）
            Assert.Equal(Color.Red.ToArgb(), snapshot.Background.ToArgb());
            Assert.Contains("--bg: rgba(255,0,0,1);", WebThemeCss.BuildCssVariables(snapshot));
        }

        [Fact]
        public void 未发布快照_使用写死回退配色_与改造前默认一致()
        {
            string css = WebThemeCss.BuildCssVariables(null);

            // 与网页模板里的旧默认值一致（#09090b / #141417 / #27272a / #f4f4f5 / #71717a）
            Assert.Contains("--bg: rgba(9,9,11,1);", css);
            Assert.Contains("--card: rgba(20,20,23,1);", css);
            Assert.Contains("--border: rgba(39,39,42,1);", css);
            Assert.Contains("--text-main: rgba(244,244,245,1);", css);
            Assert.Contains("--text-sub: rgba(113,113,122,1);", css);
        }

        // ================================================================
        // 2. #AARRGGBB → ARGB（不能当成 #RRGGBBAA）
        // ================================================================

        [Fact]
        public void 八位十六进制_按ARGB拆解_不是CSS的RRGGBBAA()
        {
            Theme theme = NewTheme("#80112233");
            WebThemeColorSnapshot snapshot = WebThemeColorSnapshot.FromTheme(theme);

            // ★ 主题的 8 位写法是 #AARRGGBB：alpha 在**前**
            Assert.Equal(Color.FromArgb(0x80, 0x11, 0x22, 0x33), snapshot.Background);

            string css = WebThemeCss.BuildCssVariables(snapshot);

            // a = 0x80 = 128 → 128/255 = 0.50196… → 0.502
            Assert.Contains("--bg: rgba(17,34,51,0.502);", css);

            // 明确排除"当成 RRGGBBAA"的错误解读（那会得到 rgba(128,17,34,0.2)）
            Assert.DoesNotContain("rgba(128,17,34,", css);
            Assert.DoesNotContain("rgba(128,17,34,0.2)", css);

            // 输出里根本不出现十六进制 → 不存在"主题八位色被直接当 CSS 色"的路径
            Assert.DoesNotContain("#", css);
        }

        [Fact]
        public void 六位十六进制_按RGB解析_不透明()
        {
            Theme theme = NewTheme("#112233");
            WebThemeColorSnapshot snapshot = WebThemeColorSnapshot.FromTheme(theme);

            Assert.Equal(Color.FromArgb(255, 0x11, 0x22, 0x33), snapshot.Background);
            Assert.Contains("--bg: rgba(17,34,51,1);", WebThemeCss.BuildCssVariables(snapshot));
        }

        [Fact]
        public void rgba主题写法_alpha为0到1()
        {
            Theme theme = NewTheme("rgba(17,34,51,0.5)");
            WebThemeColorSnapshot snapshot = WebThemeColorSnapshot.FromTheme(theme);

            // 0.5 × 255 = 127.5 → AwayFromZero → 128
            Assert.Equal(Color.FromArgb(128, 17, 34, 51), snapshot.Background);
            Assert.Contains("--bg: rgba(17,34,51,0.502);", WebThemeCss.BuildCssVariables(snapshot));
        }

        // ================================================================
        // 3. 非法 / 未知色值 → 明确回退
        // ================================================================

        [Fact]
        public void 非法色值_走明确回退色_不抛异常_不把原文插入()
        {
            string[] illegal = new string[]
            {
                null,
                "",
                "   ",
                "#GGGGGG",
                "#12345",
                "#1234567",
                "#123456789",
                "rgba(300,0,0,1)",
                "rgba(0,0,0,2)",
                "rgba(0,0,0,-1)",
                "rgba(0,0,0,NaN)",
                "rgba(0,0,0,Infinity)",
                "notacolor",
                "url(javascript:alert(1))",
            };

            foreach (string raw in illegal)
            {
                Theme theme = NewTheme(raw);
                WebThemeColorSnapshot snapshot = WebThemeColorSnapshot.FromTheme(theme);

                Assert.Equal(WebThemeColorSnapshot.FallbackBackground, snapshot.Background);

                string css = WebThemeCss.BuildCssVariables(snapshot);
                Assert.Contains("--bg: rgba(9,9,11,1);", css);

                if (!string.IsNullOrEmpty(raw) && raw.Trim().Length > 0)
                {
                    // 原文绝不进入 CSS（纯空白载荷没有可比性，跳过）
                    Assert.DoesNotContain(raw, css);
                }
            }
        }

        [Fact]
        public void 非法色值_不修改原主题对象()
        {
            Theme theme = NewTheme("#ZZZZZZ");
            string before = theme.Color.Background;

            WebThemeColorSnapshot.FromTheme(theme);

            // 归一化只作用于运行时副本 → 主题对象一个字节都没变
            Assert.Equal(before, theme.Color.Background);
        }

        // ================================================================
        // 4. 注入安全
        // ================================================================

        [Fact]
        public void 注入输入_不会成为注入内容_且变量块结构完整()
        {
            string[] payloads = new string[]
            {
                "</style>",
                "</style >",
                "</STYLE>",
                "\"><script>alert(1)</script>",
                "<script>alert(1)</script>",
                "</title>",
                "<!--",
                "/*",
                "*/",
                "url(javascript:alert(1))",
                "javascript:alert(1)",
                "';--",
                "red;}body{display:none",
                "\r\n</style><script>alert(1)</script>",
                "\n</style>\n",
            };

            // 基线页面（未发布快照）
            string baseline = BuildHtml(WebThemeCss.BuildCssVariables(null));

            foreach (string payload in payloads)
            {
                Theme theme = new Theme();
                theme.Color = new ColorConfig();
                theme.Color.Background = payload;
                theme.Color.GroupBackground = payload;
                theme.Color.Border = payload;
                theme.Color.TextPrimary = payload;
                theme.Color.TextSecondary = payload;
                theme.Color.ValueSafe = payload;
                theme.Color.ValueWarn = payload;
                theme.Color.ValueCrit = payload;

                string css = WebThemeCss.BuildCssVariables(WebThemeColorSnapshot.FromTheme(theme));
                string html = BuildHtml(css);

                // ① 主题原文没有进入生成的 CSS
                Assert.DoesNotContain(payload, css);

                // ② 生成的 CSS 里不存在任何可能形成注入上下文的字符
                Assert.DoesNotContain("<", css);
                Assert.DoesNotContain(">", css);
                Assert.DoesNotContain("\"", css);
                Assert.DoesNotContain("'", css);
                Assert.DoesNotContain("/", css);
                Assert.DoesNotContain("*", css);
                Assert.DoesNotContain("url(", css, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("javascript", css, StringComparison.OrdinalIgnoreCase);

                // ③ 变量块结构完整
                Assert.StartsWith(":root {", css);
                Assert.EndsWith("}", css);
                Assert.Equal(8, CountOccurrences(css, "--"));
                Assert.Contains("--bg: rgba(9,9,11,1);", css);
                Assert.Contains("--c-2: rgba(239,68,68,1);", css);

                // ④ 整页 HTML 里的 <style> / <script> 数量与基线一致（没有多出可执行上下文）
                Assert.Equal(CountOccurrences(baseline, "</style>"), CountOccurrences(html, "</style>"));
                Assert.Equal(CountOccurrences(baseline, "<script"), CountOccurrences(html, "<script"));
                Assert.Equal(CountOccurrences(baseline, "<!--"), CountOccurrences(html, "<!--"));

                // ⑤ 占位符被替换干净
                Assert.DoesNotContain(WebPageContent.ThemeCssPlaceholder, html);
            }
        }

        [Fact]
        public void 模板_含有主题CSS占位符()
        {
            Assert.Contains(WebPageContent.ThemeCssPlaceholder, WebPageContent.IndexHtml);

            // ★ 级联正确性：占位符必须在基础 :root 变量块**之后**，
            //   否则后出现的默认值会把主题变量覆盖掉（主题就"不生效"了）。
            int baseRoot = WebPageContent.IndexHtml.IndexOf(":root {", StringComparison.Ordinal);
            int placeholder = WebPageContent.IndexHtml.IndexOf(WebPageContent.ThemeCssPlaceholder, StringComparison.Ordinal);
            Assert.True(baseRoot >= 0, "找不到基础 :root 变量块");
            Assert.True(placeholder > baseRoot, "占位符必须位于基础 :root 变量块之后");

            // 组装后的整页也验证一次同样的级联关系
            string html = BuildHtml(WebThemeCss.BuildCssVariables(WebThemeColorSnapshot.FromTheme(NewTheme("#112233"))));
            int baseEnd = html.IndexOf("--c-2: #ef4444;", StringComparison.Ordinal);
            int themeBlock = html.IndexOf("--bg: rgba(17,34,51,1);", StringComparison.Ordinal);
            Assert.True(baseEnd >= 0, "找不到基础变量块的结尾");
            Assert.True(themeBlock > baseEnd, "主题变量块必须出现在基础变量块之后");
        }

        // ================================================================
        // 5. invariant（区域设置不影响输出）
        // ================================================================

        [Fact]
        public void 区域设置_小数逗号_输出不变()
        {
            Theme theme = NewTheme("#80112233");

            string invariantCss = WebThemeCss.BuildCssVariables(WebThemeColorSnapshot.FromTheme(theme));

            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = MakeGermanCulture();
                string germanCss = WebThemeCss.BuildCssVariables(WebThemeColorSnapshot.FromTheme(theme));

                // 小数点必须还是 '.'，且整段输出逐字相同
                Assert.Equal(invariantCss, germanCss);
                Assert.Contains("--bg: rgba(17,34,51,0.502);", germanCss);
                Assert.DoesNotContain("0,502", germanCss);
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        /// <summary>造一个"小数点用逗号"的区域设置（系统没有 de-DE 时退化到等效自定义设置）。</summary>
        private static CultureInfo MakeGermanCulture()
        {
            try
            {
                return new CultureInfo("de-DE");
            }
            catch (CultureNotFoundException)
            {
                CultureInfo custom = (CultureInfo)CultureInfo.InvariantCulture.Clone();
                custom.NumberFormat.NumberDecimalSeparator = ",";
                custom.NumberFormat.NumberGroupSeparator = ".";
                return custom;
            }
        }

        // ================================================================
        // 6. HTML / 图标请求不产生硬件需求
        // ================================================================

        [Fact]
        public async Task HTML与图标请求_不产生任何硬件采样需求()
        {
            int samplingCalls = 0;
            WebSessionManager manager = new WebSessionManager(
                (writer, ct) =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("k", "v");
                    writer.WriteEndObject();
                    return Task.CompletedTask;
                },
                demandState: null,
                timeProvider: null,
                samplingRequester: (ct) =>
                {
                    Interlocked.Increment(ref samplingCalls);
                    return Task.FromResult(true);
                });

            WebThemeColorPublisher.Publish(WebThemeColorSnapshot.FromTheme(NewTheme("#112233")));
            try
            {
                string html = await HttpGetAsync(manager, "/");
                Assert.Contains("HTTP/1.1 200", html);
                Assert.Contains("--bg: rgba(17,34,51,1);", html);
                Assert.DoesNotContain(WebPageContent.ThemeCssPlaceholder, html);

                string favicon = await HttpGetAsync(manager, "/favicon.ico");
                Assert.Contains("HTTP/1.1 404", favicon);

                // 采样入口一次都没被调用；需求状态保持"无需求"
                Assert.Equal(0, Volatile.Read(ref samplingCalls));
                Assert.False(manager.HasWebDataDemand);
                Assert.Equal(0, manager.AuthenticatedConnectionCount);
                Assert.False(manager.HasHttpLease);
            }
            finally
            {
                WebThemeColorPublisher.Clear();
            }
        }

        /// <summary>走一次真实的回环 HTTP 请求（只监听 127.0.0.1，不碰外网）。</summary>
        private static async Task<string> HttpGetAsync(WebSessionManager manager, string path)
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

                using (TcpClient client = new TcpClient())
                {
                    await client.ConnectAsync(IPAddress.Loopback, port);
                    using (TcpClient serverClient = await acceptTask)
                    {
                        Task handle = manager.HandleClientAsync(serverClient);

                        byte[] request = Encoding.ASCII.GetBytes(
                            "GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
                        NetworkStream stream = client.GetStream();
                        await stream.WriteAsync(request, 0, request.Length);
                        await stream.FlushAsync();

                        StringBuilder sb = new StringBuilder();
                        byte[] buffer = new byte[8192];
                        int read;
                        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                        }

                        await handle;
                        return sb.ToString();
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        // ================================================================
        // 7. 换主题 → 刷新跟随（不推送、不轮询）
        // ================================================================

        [Fact]
        public void 换主题后_重新生成HTML得到新配色()
        {
            try
            {
                WebThemeColorPublisher.Publish(WebThemeColorSnapshot.FromTheme(NewTheme("#112233")));
                string htmlA = BuildHtml(WebThemeCss.BuildCssVariables(WebThemeColorPublisher.Current));

                WebThemeColorPublisher.Publish(WebThemeColorSnapshot.FromTheme(NewTheme("#445566")));
                string htmlB = BuildHtml(WebThemeCss.BuildCssVariables(WebThemeColorPublisher.Current));

                Assert.Contains("--bg: rgba(17,34,51,1);", htmlA);
                Assert.DoesNotContain("--bg: rgba(68,85,102,1);", htmlA);

                Assert.Contains("--bg: rgba(68,85,102,1);", htmlB);
                Assert.DoesNotContain("--bg: rgba(17,34,51,1);", htmlB);
            }
            finally
            {
                WebThemeColorPublisher.Clear();
            }
        }

        [Fact]
        public void 快照只在HTML组装处被读取_广播与采样路径不引用它()
        {
            string path = FindRepoFile(Path.Combine("src", "System", "WebServer", "WebSessionManager.cs"));
            Assert.True(path != null && File.Exists(path), "找不到 WebSessionManager.cs（仓库根定位失败）");
            string code = StripLineComments(File.ReadAllText(path));

            // 整份文件里只允许出现一次引用 → 不存在"顺手在广播/采样路径也读一次"的推送或轮询
            Assert.Equal(1, CountOccurrences(code, "WebThemeColorPublisher"));

            int htmlBranch = code.IndexOf("WebPageContent.IndexHtml", StringComparison.Ordinal);
            int snapshotBranch = code.IndexOf("path.StartsWith(\"/api/snapshot\")", StringComparison.Ordinal);
            int usage = code.IndexOf("WebThemeColorPublisher", StringComparison.Ordinal);
            Assert.True(htmlBranch >= 0, "找不到 HTML 组装分支");
            Assert.True(snapshotBranch >= 0, "找不到快照接口分支");
            // 快照不得出现在快照接口路径里（那里才允许触发采样）
            Assert.True(usage > snapshotBranch, "快照不能在快照接口路径里被读取");
            // 且必须紧挨着 HTML 组装分支（同一段代码里）
            Assert.True(Math.Abs(usage - htmlBranch) < 1000, "快照必须在 HTML 组装分支里被读取");
        }

        /// <summary>去掉行注释（只留代码），用于源码级不变量断言。</summary>
        private static string StripLineComments(string source)
        {
            StringBuilder sb = new StringBuilder(source.Length);
            foreach (string line in source.Split('\n'))
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(idx >= 0 ? line.Substring(0, idx) : line);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        [Fact]
        public void 新增的CSS代码_没有任何定时器或轮询()
        {
            string path = FindRepoFile(Path.Combine("src", "System", "WebServer", "WebThemeCss.cs"));
            Assert.True(path != null && File.Exists(path), "找不到 WebThemeCss.cs（仓库根定位失败）");
            string code = File.ReadAllText(path);

            Assert.DoesNotContain("Timer", code);
            Assert.DoesNotContain("Task.Delay", code);
            Assert.DoesNotContain("Thread.Sleep", code);
        }
    }
}
