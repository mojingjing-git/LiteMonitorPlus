using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiteMonitor.ThemeEditor;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// TE03 属性目录回归：目录与生产模型的一致性——路径可读写、缺省值来自模型、
    /// JSON 节点往返不丢类型。逐字段"改变能改变生产输出"的真渲染验证在 TE04 后补。
    /// </summary>
    public class ThemePropertyCatalogTests
    {
        [Fact]
        public void 经典目录_字段齐全_分组与路径规范()
        {
            var props = ThemePropertyCatalog.ClassicProperties;

            Assert.Equal(15, props.Count(p => p.JsonPath.StartsWith("color.", StringComparison.Ordinal)));
            Assert.Equal(7, props.Count(p => p.JsonPath.StartsWith("font.", StringComparison.Ordinal)));
            Assert.Equal(15, props.Count(p => p.JsonPath.StartsWith("layout.", StringComparison.Ordinal)));
            // [S05] 阈值不再是可编辑主题属性：状态阈值来自当前生效设置，主题只负责颜色。
            Assert.DoesNotContain(props, p => p.JsonPath.StartsWith("thresholds.", StringComparison.Ordinal));
            // JSON 键大小写不变式：目录路径必须与模型属性名一致（camelCase），无前后空白
            Assert.All(props, p => Assert.Equal(p.JsonPath.Trim(), p.JsonPath));
            Assert.All(props, p => Assert.False(string.IsNullOrWhiteSpace(p.DisplayName)));
        }

        [Fact]
        public void 逐字段_Getter读Setter写_与生产模型一致()
        {
            var props = ThemePropertyCatalog.ClassicProperties;
            var theme = new Theme();
            var seeded = new Theme();
            seeded.Color.Background = "#112233";
            seeded.Color.TextSecondary = "#AABBCC";
            seeded.Font.Value = 16.5;
            seeded.Layout.RowHeight = 55;
            seeded.Thresholds.Temp.Warn = 66;


            foreach (ThemePropertyDescriptor p in props)
            {
                // 写入种子值的路径必须真的落到生产模型上
                object? seededValue = p.Getter(seeded);
                p.Setter(theme, seededValue);
                Assert.Equal(seededValue, p.Getter(theme));
            }
        }

        [Fact]
        public void 缺省值_来自干净模型实例_而非目录硬编码()
        {
            var theme = new Theme();
            foreach (ThemePropertyDescriptor p in ThemePropertyCatalog.ClassicProperties)
            {
                object? current = p.Getter(theme);
                object? declaredDefault = p.DefaultGetter();
                Assert.Equal(declaredDefault, current);
            }
        }

        [Fact]
        public void ToJson_字符串数字布尔类型保真()
        {
            foreach (ThemePropertyDescriptor p in ThemePropertyCatalog.ClassicProperties)
            {
                object? value = p.DefaultGetter();
                JsonNode? node = p.ToJson(value);

                // 可空配色缺省即 null ⇒ 节点为 null（键不存在的写回语义），合法
                if (value is null)
                {
                    Assert.Null(node);
                    continue;
                }

                Assert.NotNull(node);
                JsonNode? round = p.ToJson(ToOriginal(p, node!));
                Assert.Equal(node!.ToJsonString(), round!.ToJsonString());
            }
        }

        [Fact]
        public void 共享字段_字体布局阈值_作用域标注正确()
        {
            var shared = ThemePropertyCatalog.ClassicProperties
                .Where(p => p.Scope.HasFlag(ThemePropertyScope.Shared))
                .Select(p => p.JsonPath)
                .ToArray();

            // TE00 §2.1/§4.2：GPU 主效果与兼容外观共享字体、布局；
            // 字体仅在 floatingToolbar 下被前景消费，目录层仍标 Shared（由视图过滤）。
            // [S05] 阈值已从目录移除（状态阈值来自生效设置），不再出现在共享字段里。
            string[] fontPaths =
            [
                "font.family", "font.valueFamily", "font.title",
                "font.group", "font.item", "font.value", "font.bold"
            ];
            Assert.Equal(fontPaths, shared.Take(7).ToArray());
            Assert.Equal(15, shared.Count(p => p.StartsWith("layout.", StringComparison.Ordinal)));
            Assert.DoesNotContain(shared, p => p.StartsWith("thresholds.", StringComparison.Ordinal));
            Assert.Equal(22, shared.Length);
        }

        [Fact]
        public void 会话编辑_经属性目录路径落盘_大小写不变()
        {
            string dir = Path.Combine(Path.GetTempPath(), "litemonitor-te03-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                const string source = @"{ ""name"": ""T"", ""layout"": { ""rowHeight"": 40 } }";
                File.WriteAllText(Path.Combine(dir, "T.json"), source);
                ThemeDocumentStore.SetThemeDirForTests(dir);
                try
                {
                    ThemeDocumentSession session = ThemeDocumentSession.FromSourceBytes(
                        "T", Path.Combine(dir, "T.json"), System.Text.Encoding.UTF8.GetBytes(source));

                    ThemePropertyDescriptor rowHeight = ThemePropertyCatalog.ClassicProperties
                        .Single(p => p.JsonPath == "layout.rowHeight");
                    double value = Convert.ToDouble(rowHeight.Getter(session.Draft));
                    session.ApplyEdit(new ThemeFieldEdit(
                        rowHeight.JsonPath,
                        rowHeight.ToJson(value + 8),
                        theme => rowHeight.Setter(theme, value + 8)));

                    Assert.True(session.Save().Success);
                    string saved = File.ReadAllText(Path.Combine(dir, "T.json"));
                    Assert.Contains("\"rowHeight\": 48", saved);
                    Assert.DoesNotContain("\"RowHeight\"", saved);
                }
                finally
                {
                    ThemeDocumentStore.SetThemeDirForTests(null);
                }
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        private static object ToOriginal(ThemePropertyDescriptor p, JsonNode node) =>
            p.EditorKind switch
            {
                ThemePropertyEditorKind.Color or ThemePropertyEditorKind.Text => node.GetValue<string>(),
                ThemePropertyEditorKind.Number or ThemePropertyEditorKind.Choice =>
                    JsonSerializer.SerializeToElement(node).GetDouble(),
                ThemePropertyEditorKind.Boolean => node.GetValue<bool>(),
                _ => node.ToJsonString()
            };
    }
}
