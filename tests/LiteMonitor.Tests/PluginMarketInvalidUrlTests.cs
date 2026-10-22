using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// S4.1b：插件市场【越界反例台账】的自动化覆盖（ENV-003 收口）。
    ///
    /// S4.1（a8fb2fc）把 5 类越界反例连同台账入了库：
    ///   夹具  tests/fixtures/plugin-repo/invalid-examples/&lt;case&gt;/plugin.json
    ///   台账  tests/fixtures/plugin-repo/invalid-examples/README.md
    /// 但台账只是文字 —— 没有任何用例消费它，"这些地址必须被拒绝"实际上没人验证。
    /// 本类把台账变成会跑的断言：
    ///
    ///   1) download_url 一律从**夹具文件**里读，不在测试里硬编码：
    ///      夹具是反例的事实来源；硬编码 URL 会让"改了夹具没改用例"变成静默的假绿，
    ///      台账与代码也会各自漂移。
    ///   2) 每类反例都必须被拒绝（PluginMarket.IsAllowedDownloadUrl == false），
    ///      且拒绝理由必须同时命中【台账登记的措辞】与【生产代码给出的 reason】——
    ///      两边都命中才说明"台账 ↔ 代码"没有漂移
    ///      （改了判定逻辑却没更新台账 → 用例红）。
    ///   3) 台账登记的 download_url 必须与反例文件里的一致（台账不得与夹具脱节）。
    ///
    /// 判定依据（生产代码 src/Plugins/PluginMarket.cs）：
    ///   白名单定义   :160-165        判定逻辑 :204-271
    ///   HTTPS        :221-225        userinfo :228-232
    ///   原始路径取值 :242-243        穿越段判定 :315
    ///   Host 比对    :249（越界出口 :258-262）   路径前缀比对 :251（越界出口 :263-267）
    ///
    /// 仓库根定位：沿用 WebSamplingDemandTests.FindRepoFile（:598-610）的既有套路
    /// （从 AppContext.BaseDirectory 逐级上溯，命中 LiteMonitor.csproj 即为仓库根）。
    /// 夹具不在测试工程的输出目录里（LiteMonitor.Tests.csproj 未复制 tests/fixtures/**），
    /// 因此只能用上溯方式定位；定位失败一律 Assert.Fail，不允许静默 return 冒充通过。
    /// </summary>
    public class PluginMarketInvalidUrlTests
    {
        /// <summary>越界反例夹具根目录（相对仓库根）。</summary>
        private static string InvalidExamplesDir =>
            Path.Combine("tests", "fixtures", "plugin-repo", "invalid-examples");

        /// <summary>越界反例台账（相对仓库根）。</summary>
        private static string LedgerRelPath =>
            Path.Combine(InvalidExamplesDir, "README.md");

        // ================================================================
        // 5 类越界反例 —— 每类一条用例
        // ================================================================

        /// <summary>主机越界：download_url 指向白名单之外的主机。</summary>
        [Fact]
        public void 反例_主机越界_必须被拒绝()
        {
            AssertRejected("host-out-of-scope", "主机", "白名单内");
        }

        /// <summary>路径前缀越界：路径前缀被篡改（plugins-evil），不命中白名单前缀。</summary>
        [Fact]
        public void 反例_路径前缀越界_必须被拒绝()
        {
            AssertRejected("path-prefix-out-of-scope", "路径", "前缀");
        }

        /// <summary>非 HTTPS：明文 http 不得被放行。</summary>
        [Fact]
        public void 反例_非HTTPS_必须被拒绝()
        {
            AssertRejected("non-https", "只允许 HTTPS");
        }

        /// <summary>路径穿越：路径里带 .. 穿越段（按未经 Uri 规范化的原始路径判定）。</summary>
        [Fact]
        public void 反例_路径穿越_必须被拒绝()
        {
            AssertRejected("path-traversal", "路径含穿越段");
        }

        /// <summary>地址含凭据：地址里带 user:password@ 形式的 userinfo。</summary>
        [Fact]
        public void 反例_地址含凭据_必须被拒绝()
        {
            AssertRejected("userinfo", "地址不得包含 userinfo");
        }

        // ================================================================
        // 断言与 helper
        // ================================================================

        /// <summary>
        /// 校验一类越界反例：夹具里的 download_url 必须被拒绝，
        /// 且拒绝理由必须同时命中台账登记的措辞与生产代码给出的 reason。
        /// </summary>
        /// <param name="caseDir">反例目录名（invalid-examples 下的子目录）。</param>
        /// <param name="reasonKeywords">
        /// 拒绝理由关键词：必须同时出现在【台账登记该反例的那一行】与
        /// 【生产代码返回的 reason】里 —— 只命中一边说明台账与代码已经漂移。
        /// </param>
        private static void AssertRejected(string caseDir, params string[] reasonKeywords)
        {
            // ---------- 1. 从夹具读 download_url（不硬编码 URL） ----------
            string fixturePath = FindRepoFile(Path.Combine(InvalidExamplesDir, caseDir, "plugin.json"));
            Assert.True(fixturePath != null,
                "找不到越界反例夹具（仓库根定位失败）: " + InvalidExamplesDir + "/" + caseDir + "/plugin.json");

            string url = ReadDownloadUrl(fixturePath, caseDir);

            // ---------- 2. 核心判定：必须被拒绝 ----------
            Assert.False(PluginMarket.IsAllowedDownloadUrl(url),
                "越界反例 " + caseDir + " 的下载地址本应被拒绝，却通过了白名单校验: " + url);

            bool resolved = PluginMarket.TryResolveAllowedDownloadUri(url, out _, out string reason);
            Assert.False(resolved,
                "越界反例 " + caseDir + " 的下载地址本应被拒绝，却通过了 TryResolveAllowedDownloadUri: " + url);
            Assert.False(string.IsNullOrWhiteSpace(reason),
                "越界反例 " + caseDir + " 被拒绝了，但拒绝理由为空（台账要求写明拒绝理由）");

            // ---------- 3. 台账 ↔ 代码：关键词必须两边都命中 ----------
            string ledgerRow = FindLedgerRow(caseDir);
            foreach (string keyword in reasonKeywords)
            {
                Assert.True(ledgerRow.Contains(keyword, StringComparison.Ordinal),
                    "台账（invalid-examples/README.md）里没有登记越界反例 " + caseDir +
                    " 的拒绝理由关键词 \"" + keyword + "\"");
                Assert.True(reason.Contains(keyword, StringComparison.Ordinal),
                    "越界反例 " + caseDir + " 的拒绝理由与台账登记的措辞不一致：关键词 \"" + keyword +
                    "\" 未出现在 reason 里。reason = " + reason);
            }

            // ---------- 4. 台账登记的 download_url 必须与反例文件一致 ----------
            string ledgerUrl = ReadLedgerDownloadUrl(ledgerRow);
            Assert.True(string.Equals(url, ledgerUrl, StringComparison.Ordinal),
                "台账（invalid-examples/README.md）登记的 download_url 与反例文件 " + caseDir +
                "/plugin.json 不一致。台账 = " + ledgerUrl + "，夹具 = " + url);
        }

        /// <summary>从反例夹具 plugin.json 里取 download_url。</summary>
        private static string ReadDownloadUrl(string fixturePath, string caseDir)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(fixturePath));
            bool has = doc.RootElement.TryGetProperty("download_url", out var el);
            Assert.True(has && el.ValueKind == JsonValueKind.String,
                "越界反例夹具 " + caseDir + "/plugin.json 缺 download_url 字段");

            string url = el.GetString() ?? "";
            Assert.False(string.IsNullOrWhiteSpace(url),
                "越界反例夹具 " + caseDir + "/plugin.json 的 download_url 为空");
            return url;
        }

        /// <summary>
        /// 在台账（invalid-examples/README.md）的表格里找出登记该反例的那一行。
        /// 台账是"反例 ↔ 拒绝理由 ↔ 判定代码行"的对照表，一类反例必须恰好登记一行。
        /// </summary>
        private static string FindLedgerRow(string caseDir)
        {
            string ledgerPath = FindRepoFile(LedgerRelPath);
            Assert.True(ledgerPath != null,
                "找不到越界反例台账（仓库根定位失败）: " + LedgerRelPath);

            var rows = new List<string>();
            foreach (string line in File.ReadAllLines(ledgerPath))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("|", StringComparison.Ordinal)) continue;   // 只认表格行
                if (!trimmed.Contains(caseDir, StringComparison.Ordinal)) continue;
                rows.Add(trimmed);
            }

            Assert.True(rows.Count == 1,
                "台账（invalid-examples/README.md）里应当恰好登记一行反例 " + caseDir +
                "，实际找到 " + rows.Count + " 行");
            return rows[0];
        }

        /// <summary>
        /// 从台账表格行里取登记的 download_url（第 3 列，去掉反引号与空白）。
        /// 台账列序：# | 目录 | download_url | 期望判定 | 期望拒绝理由 | 对应代码行
        /// </summary>
        private static string ReadLedgerDownloadUrl(string ledgerRow)
        {
            string[] cells = ledgerRow.Split('|');
            Assert.True(cells.Length >= 7,
                "台账表格行的列数异常（期望 6 列）: " + ledgerRow);
            return cells[3].Trim().Trim('`');
        }

        /// <summary>
        /// 在仓库里定位文件（从测试输出目录向上找仓库根）。
        /// ★ 与 WebSamplingDemandTests.FindRepoFile（:598-610）同一套路，不另立第三种口径。
        /// </summary>
        private static string FindRepoFile(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, relativePath);
                if (File.Exists(candidate)) return candidate;
                if (File.Exists(Path.Combine(dir.FullName, "LiteMonitor.csproj"))) return null;  // 已到仓库根仍未找到
                dir = dir.Parent;
            }
            return null;
        }
    }
}
