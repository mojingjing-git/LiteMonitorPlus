using System;
using System.IO;
using System.Linq;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// INF-06 源码级护栏：SettingsForm 需要 UIController/硬件采集器，单测不可实例化。
    /// 这里钉住保存事务的关键顺序与结果接线，行为回归由 Settings/ModuleApplyResult 合同测试覆盖。
    /// </summary>
    public class SettingsApplyResultGuardTests
    {
        [Fact]
        public void 保存失败_必须在合并Live前返回且不关闭对话框()
        {
            string code = FormCode();

            // [S01] 保存入口升级为 TrySaveWithOutcome（区分"凭据保护失败"与一般写入失败）；
            // 护栏意图不变：保存失败必须在 Merge 前返回且不关闭对话框。
            int trySave = code.IndexOf("SettingsSaveOutcome saveOutcome = candidate.TrySaveWithOutcome();", StringComparison.Ordinal);
            int merge = code.IndexOf("SettingsChanger.Merge(_cfg, candidate);", StringComparison.Ordinal);
            Assert.True(trySave > 0, "保存事务必须使用 candidate.TrySaveWithOutcome()（S01：区分凭据保护失败）");
            Assert.True(merge > trySave, "Live 合并必须在保存成功之后");
            // S01 的凭据保护失败分支文案较长，失败返回窗口随之加宽。
            Assert.Contains("return false;", code.Substring(trySave, Math.Min(900, code.Length - trySave)));
            Assert.Contains("if (ApplySettings()) { this.DialogResult = DialogResult.OK; this.Close(); }", code);
        }

        [Fact]
        public void 设置应用_必须走结果感知入口并展示部分失败()
        {
            string code = FormCode();
            Assert.Contains("ApplySettingsSelectivelyWithResults", code);
            Assert.Contains("SettingsApplyPartial", code);
            Assert.Contains("failedModules", code);
        }

        [Fact]
        public void 新增设置应用文案_九种语言都必须存在()
        {
            string[] languages = { "de", "en", "es", "fr", "ja", "ko", "ru", "zh-tw", "zh" };
            foreach (string lang in languages)
            {
                string json = File.ReadAllText(RepoFile("resources", "lang", lang + ".json"));
                Assert.Contains("\"SettingsApplyPartial\"", json);
            }
        }

        private static string FormCode() => File.ReadAllText(RepoFile("src", "UI", "SettingsForm.cs"));

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LiteMonitor.csproj"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
        }
    }
}
