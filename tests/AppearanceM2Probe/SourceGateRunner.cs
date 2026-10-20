using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace LiteMonitor.AppearanceM2Probe
{
    /// <summary>单个探针用例的结果（口径与既有探针的 ProbeCase 一致）。</summary>
    internal sealed class ProbeCase
    {
        /// <summary>用例名。</summary>
        public string Name { get; init; } = "";

        /// <summary>是否通过。</summary>
        public bool Passed { get; private set; } = true;

        /// <summary>失败原因列表（人类可读）。</summary>
        public List<string> Failures { get; } = new();

        /// <summary>证据明细（写入 JSON 的 details 字段）。</summary>
        public JsonObject Details { get; } = new();

        /// <summary>记录一次失败。</summary>
        public void Fail(string reason)
        {
            Passed = false;
            Failures.Add(reason);
        }

        /// <summary>条件不成立时记录一次失败。</summary>
        public void Require(bool condition, string reason)
        {
            if (!condition) Fail(reason);
        }

        /// <summary>写入一条证据明细。</summary>
        public void Detail(string key, JsonNode? value) => Details[key] = value;
    }

    /// <summary>一条捕获原语命中记录。</summary>
    internal sealed class CaptureMatch
    {
        /// <summary>命中所在文件的绝对路径。</summary>
        public string FilePath { get; init; } = "";

        /// <summary>相对仓库根的路径（正斜杠分隔）。</summary>
        public string RelativePath { get; init; } = "";

        /// <summary>命中行号（1 起）。</summary>
        public int Line { get; init; }

        /// <summary>命中的原语名。</summary>
        public string Primitive { get; init; } = "";

        /// <summary>归类（见 <see cref="SourceGateRunner"/> 的分类常量）。</summary>
        public string Classification { get; init; } = "";

        /// <summary>命中行的代码文本（已剔除注释与字符串字面量）。</summary>
        public string Code { get; init; } = "";

        /// <summary>是否属于违规命中（零命中范围命中 / 其它未授权文件命中）。</summary>
        public bool IsViolation =>
            Classification == SourceGateRunner.ClassificationZeroScope
            || Classification == SourceGateRunner.ClassificationOther;
    }

    /// <summary>
    /// 源码扫描器：先把注释与字符串/字符字面量剔掉，再匹配抓屏原语。
    ///
    /// ★ 为什么必须剔除注释：<c>src/System/Backdrop/BackdropRenderer.cs</c> 的文件头
    /// 明确声明「本文件不出现 BitBlt/PrintWindow/DXGI 等抓屏 API」，这句话本身就在注释里。
    /// 若不剔除注释，就会把这条**自证声明**误判成命中 —— 门槛就永远过不了。
    /// </summary>
    internal static class SourceScanner
    {
        /// <summary>抓屏原语清单（M2 合同 §9 门槛 #7 指定的检查项）。</summary>
        public static readonly string[] Primitives =
        {
            "BitBlt",
            "CopyFromScreen",
            "PrintWindow",
            "CreateCompatibleDC",
            "CreateCompatibleBitmap",
            "SetWindowDisplayAffinity",
            "GetDC(",
            "DXGI",
            "D3D11CreateDevice",
            "IDXGIOutputDuplication"
        };

        /// <summary>按字面量匹配的抓屏原语。</summary>
        private static readonly string[] LiteralPrimitives =
        {
            "BitBlt",
            "CopyFromScreen",
            "PrintWindow",
            "CreateCompatibleDC",
            "CreateCompatibleBitmap",
            "SetWindowDisplayAffinity",
            "DXGI",
            "D3D11CreateDevice",
            "IDXGIOutputDuplication"
        };

        /// <summary>需要紧跟左括号的抓屏原语（GetDC）。</summary>
        private const string GetDcName = "GetDC";

        /// <summary>
        /// 把文件内容里的**注释**与**字符串/字符字面量内容**替换为空格（保留换行），
        /// 返回「只剩代码」的文本。命中判定必须基于该文本。
        /// </summary>
        public static string StripCommentsAndLiterals(string text)
        {
            char[] buf = text.ToCharArray();
            int i = 0;
            int n = buf.Length;

            while (i < n)
            {
                char c = buf[i];

                // 行注释 // ... （含 /// XML 文档注释）
                if (c == '/' && i + 1 < n && buf[i + 1] == '/')
                {
                    while (i < n && buf[i] != '\n') { buf[i] = ' '; i++; }
                    continue;
                }

                // 块注释 /* ... */
                if (c == '/' && i + 1 < n && buf[i + 1] == '*')
                {
                    buf[i] = ' '; buf[i + 1] = ' '; i += 2;
                    while (i < n)
                    {
                        if (buf[i] == '*' && i + 1 < n && buf[i + 1] == '/')
                        {
                            buf[i] = ' '; buf[i + 1] = ' '; i += 2;
                            break;
                        }
                        if (buf[i] != '\n') buf[i] = ' ';
                        i++;
                    }
                    continue;
                }

                // 逐字字符串 @"..."（"" 为转义引号）
                if (c == '@' && i + 1 < n && buf[i + 1] == '"')
                {
                    buf[i] = ' '; buf[i + 1] = ' '; i += 2;
                    while (i < n)
                    {
                        if (buf[i] == '"')
                        {
                            if (i + 1 < n && buf[i + 1] == '"')
                            {
                                buf[i] = ' '; buf[i + 1] = ' '; i += 2;
                                continue;
                            }
                            buf[i] = ' '; i++;
                            break;
                        }
                        if (buf[i] != '\n') buf[i] = ' ';
                        i++;
                    }
                    continue;
                }

                if (c == '"')
                {
                    // 原始字符串字面量 """..."""（本仓库当前未使用，仍做防御处理）
                    if (i + 2 < n && buf[i + 1] == '"' && buf[i + 2] == '"')
                    {
                        buf[i] = ' '; buf[i + 1] = ' '; buf[i + 2] = ' '; i += 3;
                        while (i < n)
                        {
                            if (i + 2 < n && buf[i] == '"' && buf[i + 1] == '"' && buf[i + 2] == '"')
                            {
                                buf[i] = ' '; buf[i + 1] = ' '; buf[i + 2] = ' '; i += 3;
                                break;
                            }
                            if (buf[i] != '\n') buf[i] = ' ';
                            i++;
                        }
                        continue;
                    }

                    // 普通字符串字面量
                    buf[i] = ' '; i++;
                    while (i < n)
                    {
                        if (buf[i] == '\\' && i + 1 < n)
                        {
                            if (buf[i + 1] != '\n') buf[i + 1] = ' ';
                            buf[i] = ' '; i += 2;
                            continue;
                        }
                        if (buf[i] == '"') { buf[i] = ' '; i++; break; }
                        if (buf[i] == '\n') break;   // 未闭合的字符串：保守地停在本行
                        buf[i] = ' '; i++;
                    }
                    continue;
                }

                // 字符字面量 'x'
                if (c == '\'')
                {
                    buf[i] = ' '; i++;
                    while (i < n)
                    {
                        if (buf[i] == '\\' && i + 1 < n)
                        {
                            buf[i] = ' '; buf[i + 1] = ' '; i += 2;
                            continue;
                        }
                        if (buf[i] == '\'') { buf[i] = ' '; i++; break; }
                        if (buf[i] == '\n') break;
                        buf[i] = ' '; i++;
                    }
                    continue;
                }

                i++;
            }

            return new string(buf);
        }

        /// <summary>在「只剩代码」的文本里查找抓屏原语命中（带行号）。</summary>
        public static List<CaptureMatch> FindMatches(string codeText)
        {
            List<CaptureMatch> matches = new();
            string[] lines = codeText.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                foreach (string primitive in LiteralPrimitives)
                {
                    int index = 0;
                    while ((index = line.IndexOf(primitive, index, StringComparison.Ordinal)) >= 0)
                    {
                        if (IsWordBoundary(line, index, primitive.Length))
                        {
                            matches.Add(new CaptureMatch
                            {
                                Line = i + 1,
                                Primitive = primitive,
                                Code = line.Trim()
                            });
                        }
                        index += primitive.Length;
                    }
                }

                // GetDC( ：标识符后（允许空白）紧跟左括号才算是调用
                int g = 0;
                while ((g = line.IndexOf(GetDcName, g, StringComparison.Ordinal)) >= 0)
                {
                    bool leftOk = g == 0 || !IsWordChar(line[g - 1]);
                    int k = g + GetDcName.Length;
                    while (k < line.Length && (line[k] == ' ' || line[k] == '\t')) k++;
                    bool rightOk = k < line.Length && line[k] == '(';
                    if (leftOk && rightOk)
                    {
                        matches.Add(new CaptureMatch
                        {
                            Line = i + 1,
                            Primitive = "GetDC(",
                            Code = line.Trim()
                        });
                    }
                    g += GetDcName.Length;
                }
            }

            return matches;
        }

        /// <summary>判断命中处的前后字符是否为标识符字符（用于避免 DXGI 命中 IDXGIOutputDuplication 这类重复）。</summary>
        private static bool IsWordBoundary(string line, int index, int length)
        {
            bool leftOk = index == 0 || !IsWordChar(line[index - 1]);
            int end = index + length;
            bool rightOk = end >= line.Length || !IsWordChar(line[end]);
            return leftOk && rightOk;
        }

        /// <summary>是否为标识符字符（字母、数字、下划线）。</summary>
        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    /// <summary>
    /// M2 合同 §9 的两条源码级门槛实现。
    ///
    /// · 门槛 #7：绘制路径无捕获调用 —— 扫描 src/ 全部 .cs 文件，剔除注释后匹配抓屏原语；
    /// · 门槛 #10：无新增 NuGet 依赖 —— 读取 LiteMonitor.csproj（及 src/ 下若存在的独立 csproj）
    ///   的 PackageReference 集合，与基线逐项比对。
    /// </summary>
    internal sealed class SourceGateRunner
    {
        /// <summary>归类：允许出现抓屏原语的目录（M2 捕获子系统）。</summary>
        public const string ClassificationAllowedDirectory = "允许目录（M2 捕获子系统）";

        /// <summary>归类：既有白名单文件（与外观无关的既有代码）。</summary>
        public const string ClassificationWhitelist = "既有白名单（与外观无关）";

        /// <summary>归类：必须零命中的范围（硬断言）。</summary>
        public const string ClassificationZeroScope = "零命中范围（硬断言）";

        /// <summary>归类：其它未授权的 src 文件。</summary>
        public const string ClassificationOther = "其它 src 文件（未授权）";

        /// <summary>允许出现抓屏原语的目录（相对仓库根）。</summary>
        private const string AllowedDirectory = "src/System/Backdrop";

        /// <summary>既有白名单文件（任务栏页的取色截图，与外观改造无关，属既有代码）。</summary>
        private const string WhitelistFile = "src/UI/Settings/TaskbarPage.cs";

        /// <summary>必须零命中的范围：标签 + 相对路径 + 是否为目录。</summary>
        private static readonly (string Label, string Path, bool IsDirectory)[] ZeroScopes =
        {
            ("外观运行时", "src/UI/AppearanceRuntime.cs", false),
            ("装饰层目录", "src/UI/Decor", true),
            ("主题编辑器目录", "src/ThemeEditor", true),
            ("核心目录", "src/Core", true),
            ("背景层渲染器", "src/System/Backdrop/BackdropRenderer.cs", false)
        };

        /// <summary>NuGet 依赖基线（M2 门槛 #10）。</summary>
        private static readonly (string Id, string Version)[] BaselinePackages =
        {
            ("LibreHardwareMonitorLib", "0.9.6"),
            ("System.Text.Json", "9.0.10")
        };

        private readonly string _repoRoot;

        /// <summary>构造 runner。</summary>
        public SourceGateRunner(string repoRoot)
        {
            _repoRoot = repoRoot;
        }

        /// <summary>执行全部用例。</summary>
        public List<ProbeCase> Cases { get; } = new();

        /// <summary>跑完全部两条门槛。</summary>
        public void RunAll()
        {
            Cases.Add(RunCapturePrimitiveGate());
            Cases.Add(RunNoNewPackageGate());
        }

        // ---------------------------------------------------------------- 门槛 #7

        /// <summary>门槛 #7：绘制路径无捕获调用。</summary>
        private ProbeCase RunCapturePrimitiveGate()
        {
            ProbeCase c = new ProbeCase { Name = "M2 门槛#7 绘制路径无捕获调用" };

            string srcDir = Path.Combine(_repoRoot, "src");
            if (!Directory.Exists(srcDir))
            {
                c.Fail("未找到 src 目录：" + srcDir);
                c.Detail("scannedFileCount", 0);
                return c;
            }

            List<string> files = Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !IsBuildOutput(p))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            List<CaptureMatch> hits = new();
            List<string> scanErrors = new();
            int lineCount = 0;
            int rawMentionCount = 0;

            foreach (string file in files)
            {
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (Exception ex)
                {
                    scanErrors.Add(Relative(file) + " 读取失败：" + ex.Message);
                    continue;
                }

                string code = SourceScanner.StripCommentsAndLiterals(text);
                lineCount += text.Split('\n').Length;

                // 原始文本中的提及数（含注释），用于证明「注释剔除」确实生效
                rawMentionCount += SourceScanner.FindMatches(text).Count;

                foreach (CaptureMatch m in SourceScanner.FindMatches(code))
                {
                    string rel = Relative(file);
                    hits.Add(new CaptureMatch
                    {
                        FilePath = file,
                        RelativePath = rel,
                        Line = m.Line,
                        Primitive = m.Primitive,
                        Classification = Classify(rel),
                        Code = m.Code
                    });
                }
            }

            List<CaptureMatch> violations = hits.Where(h => h.IsViolation).ToList();

            foreach (CaptureMatch h in violations)
            {
                c.Fail($"捕获原语违规：{h.RelativePath}:{h.Line} 命中 {h.Primitive}（{h.Classification}）");
            }

            foreach (string err in scanErrors)
            {
                c.Fail("源码扫描不完整：" + err);
            }

            // ---- 证据明细 ----
            c.Detail("gate", "M2 合同 §9 第 7 条");
            c.Detail("scannedFileCount", files.Count);
            c.Detail("scannedLineCount", lineCount);
            c.Detail("primitives", new JsonArray(SourceScanner.Primitives.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()));
            c.Detail("hitCount", hits.Count);
            c.Detail("violationCount", violations.Count);
            c.Detail("rawTextMentionCount", rawMentionCount);
            c.Detail("suppressedByCommentStripping", rawMentionCount - hits.Count);
            c.Detail("hits", ToJsonArray(hits.Select(h => (JsonNode?)new JsonObject
            {
                ["file"] = h.RelativePath,
                ["line"] = h.Line,
                ["primitive"] = h.Primitive,
                ["classification"] = h.Classification,
                ["isViolation"] = h.IsViolation,
                ["code"] = h.Code
            })));
            c.Detail("allowedDirectoryHits", ToJsonArray(hits
                .Where(h => h.Classification == ClassificationAllowedDirectory)
                .Select(h => (JsonNode?)new JsonObject
                {
                    ["file"] = h.RelativePath,
                    ["line"] = h.Line,
                    ["primitive"] = h.Primitive
                })));
            c.Detail("whitelistHits", ToJsonArray(hits
                .Where(h => h.Classification == ClassificationWhitelist)
                .Select(h => (JsonNode?)new JsonObject
                {
                    ["file"] = h.RelativePath,
                    ["line"] = h.Line,
                    ["primitive"] = h.Primitive
                })));
            c.Detail("zeroHitScopes", ToJsonArray(ZeroScopes.Select(scope =>
            {
                int scopeHits = hits.Count(h => InZeroScope(h.RelativePath, scope.Path, scope.IsDirectory));
                return (JsonNode?)new JsonObject
                {
                    ["scope"] = scope.Label,
                    ["path"] = scope.Path,
                    ["isDirectory"] = scope.IsDirectory,
                    ["exists"] = scope.IsDirectory
                        ? Directory.Exists(Path.Combine(_repoRoot, scope.Path))
                        : File.Exists(Path.Combine(_repoRoot, scope.Path)),
                    ["hitCount"] = scopeHits,
                    ["passed"] = scopeHits == 0
                };
            })));
            c.Detail("scanErrors", ToJsonArray(scanErrors.Select(e => (JsonNode?)JsonValue.Create(e))));
            c.Detail("policy", "允许出现原语的目录：" + AllowedDirectory +
                               "；既有白名单文件：" + WhitelistFile +
                               "；其余 src 文件命中即判失败。匹配前必须剔除行注释与块注释" +
                               "（字符串/字符字面量一并剔除），注释里提到 API 名不算命中。");

            return c;
        }

        /// <summary>命中归类：白名单 → 允许目录 → 零命中范围 → 其它。</summary>
        private static string Classify(string relativePath)
        {
            if (string.Equals(relativePath, WhitelistFile, StringComparison.OrdinalIgnoreCase))
            {
                return ClassificationWhitelist;
            }

            // BackdropRenderer.cs 虽在允许目录内，但它是「必须零命中」的硬断言对象
            if (IsUnder(relativePath, AllowedDirectory)
                && !string.Equals(relativePath, "src/System/Backdrop/BackdropRenderer.cs", StringComparison.OrdinalIgnoreCase))
            {
                return ClassificationAllowedDirectory;
            }

            foreach (var scope in ZeroScopes)
            {
                if (InZeroScope(relativePath, scope.Path, scope.IsDirectory)) return ClassificationZeroScope;
            }

            return ClassificationOther;
        }

        /// <summary>路径是否落在某个零命中范围内。</summary>
        private static bool InZeroScope(string relativePath, string scopePath, bool isDirectory)
        {
            if (isDirectory) return IsUnder(relativePath, scopePath);
            return string.Equals(relativePath, scopePath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>相对路径是否位于指定目录之下。</summary>
        private static bool IsUnder(string relativePath, string directory)
        {
            return relativePath.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>是否为构建产物目录（bin/obj）里的文件。</summary>
        private static bool IsBuildOutput(string path)
        {
            string normalized = path.Replace('\\', '/');
            return normalized.Contains("/bin/") || normalized.Contains("/obj/");
        }

        // ---------------------------------------------------------------- 门槛 #10

        /// <summary>门槛 #10：无新增 NuGet 依赖。</summary>
        private ProbeCase RunNoNewPackageGate()
        {
            ProbeCase c = new ProbeCase { Name = "M2 门槛#10 无新增 NuGet 依赖" };

            string mainProject = Path.Combine(_repoRoot, "LiteMonitor.csproj");
            if (!File.Exists(mainProject))
            {
                c.Fail("未找到主工程文件：" + mainProject);
                c.Detail("actualPackages", new JsonArray());
                return c;
            }

            // 检查范围：根 LiteMonitor.csproj + src/ 下若存在的独立 csproj
            List<string> projects = new List<string> { mainProject };
            string srcDir = Path.Combine(_repoRoot, "src");
            if (Directory.Exists(srcDir))
            {
                projects.AddRange(Directory.EnumerateFiles(srcDir, "*.csproj", SearchOption.AllDirectories)
                    .Where(p => !IsBuildOutput(p))
                    .OrderBy(p => p, StringComparer.Ordinal));
            }

            List<PackageReference> actual = new();
            List<string> readErrors = new();
            foreach (string project in projects)
            {
                try
                {
                    actual.AddRange(ReadPackageReferences(project));
                }
                catch (Exception ex)
                {
                    readErrors.Add(Relative(project) + " 解析失败：" + ex.Message);
                }
            }

            List<string> missing = new();
            List<string> extra = new();
            List<string> versionMismatch = new();

            foreach ((string id, string version) in BaselinePackages)
            {
                bool found = false;
                foreach (PackageReference p in actual)
                {
                    if (!string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
                    found = true;
                    if (!string.Equals(p.Version, version, StringComparison.OrdinalIgnoreCase))
                    {
                        versionMismatch.Add($"{id} 实际版本 {p.Version}，基线版本 {version}（工程：{Relative(p.ProjectPath)}）");
                    }
                }
                if (!found) missing.Add($"{id} {version}");
            }

            foreach (PackageReference p in actual)
            {
                bool inBaseline = BaselinePackages.Any(b =>
                    string.Equals(b.Id, p.Id, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(b.Version, p.Version, StringComparison.OrdinalIgnoreCase));
                if (!inBaseline) extra.Add($"{p.Id} {p.Version}（工程：{Relative(p.ProjectPath)}）");
            }

            foreach (string m in missing) c.Fail("缺少基线包：" + m);
            foreach (string e in extra) c.Fail("出现基线之外的 NuGet 包：" + e);
            foreach (string v in versionMismatch) c.Fail("包版本与基线不符：" + v);
            foreach (string err in readErrors) c.Fail("csproj 解析不完整：" + err);

            // ---- 证据明细 ----
            c.Detail("gate", "M2 合同 §9 第 10 条");
            c.Detail("projectsScanned", ToJsonArray(projects.Select(p => (JsonNode?)JsonValue.Create(Relative(p)))));
            c.Detail("expectedPackages", ToJsonArray(BaselinePackages.Select(b => (JsonNode?)new JsonObject
            {
                ["id"] = b.Id,
                ["version"] = b.Version
            })));
            c.Detail("actualPackages", ToJsonArray(actual.Select(p => (JsonNode?)new JsonObject
            {
                ["id"] = p.Id,
                ["version"] = p.Version,
                ["project"] = Relative(p.ProjectPath)
            })));
            c.Detail("missingPackages", ToJsonArray(missing.Select(m => (JsonNode?)JsonValue.Create(m))));
            c.Detail("extraPackages", ToJsonArray(extra.Select(e => (JsonNode?)JsonValue.Create(e))));
            c.Detail("versionMismatches", ToJsonArray(versionMismatch.Select(v => (JsonNode?)JsonValue.Create(v))));
            c.Detail("readErrors", ToJsonArray(readErrors.Select(e => (JsonNode?)JsonValue.Create(e))));
            c.Detail("otherProjectsOutOfScope", ToJsonArray(FindOtherProjects().Select(p => (JsonNode?)JsonValue.Create(p))));
            c.Detail("policy", "基线 = {LibreHardwareMonitorLib 0.9.6, System.Text.Json 9.0.10}；" +
                               "检查范围 = 根 LiteMonitor.csproj + src/ 下若存在的独立 csproj；" +
                               "多一个或少一个都判失败。");

            return c;
        }

        /// <summary>读取 csproj 里的 PackageReference 集合。</summary>
        private static List<PackageReference> ReadPackageReferences(string projectPath)
        {
            List<PackageReference> result = new();
            XDocument doc = XDocument.Load(projectPath, LoadOptions.None);

            foreach (XElement element in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                string id = ((string?)element.Attribute("Include")
                             ?? (string?)element.Attribute("Update")
                             ?? string.Empty).Trim();

                string version = ((string?)element.Attribute("Version")
                                  ?? element.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value
                                  ?? string.Empty).Trim();

                result.Add(new PackageReference
                {
                    Id = id,
                    Version = version,
                    ProjectPath = projectPath
                });
            }

            return result;
        }

        /// <summary>列出仓库里其它位置的 csproj（仅作提示，不参与本门槛判定）。</summary>
        private List<string> FindOtherProjects()
        {
            List<string> result = new();
            try
            {
                result = Directory.EnumerateFiles(_repoRoot, "*.csproj", SearchOption.TopDirectoryOnly)
                    .Where(p => !IsBuildOutput(p))
                    .Select(p => Relative(p))
                    .Where(p => !string.Equals(p, "LiteMonitor.csproj", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception)
            {
                // 提示性信息，取不到就留空，不影响判定
            }

            return result;
        }

        /// <summary>把绝对路径转成相对仓库根、正斜杠分隔的路径。</summary>
        private string Relative(string path)
        {
            try
            {
                return Path.GetRelativePath(_repoRoot, path).Replace('\\', '/');
            }
            catch (Exception)
            {
                return path.Replace('\\', '/');
            }
        }

        /// <summary>把节点序列转成 JsonArray。</summary>
        private static JsonArray ToJsonArray(IEnumerable<JsonNode?> items)
        {
            return new JsonArray(items.Select(i => (JsonNode?)i).ToArray());
        }
    }

    /// <summary>一条 PackageReference 记录。</summary>
    internal sealed class PackageReference
    {
        /// <summary>包 Id。</summary>
        public string Id { get; init; } = "";

        /// <summary>包版本。</summary>
        public string Version { get; init; } = "";

        /// <summary>声明该包的工程文件绝对路径。</summary>
        public string ProjectPath { get; init; } = "";
    }
}
