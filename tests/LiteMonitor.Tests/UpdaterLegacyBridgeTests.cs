using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// R05：新主程序启动时必须把"自动升级通道与当前版本不兼容"这件事告诉用户。
    /// </summary>
    /// <remarks>
    /// 背景：旧客户端用单参数调用更新器，新更新器按 LM-016 契约拒绝没有摘要的调用，
    /// 旧客户端随即退出主程序 —— 用户只看到"应用自己关掉了"。
    /// 更新器现在会在主程序目录旁留下一个机器可读标记；这里盯住主程序侧的三件事：
    ///   1. 启动时**读得到**标记，并给出明确的提示（含原因与"请手动下载新版本"）；
    ///   2. 标记**消费后不再重复提示**（不会每次启动都弹同一件事）；
    ///   3. 没有标记 / 标记损坏 ⇒ 不提示、不抛异常、不阻断启动。
    ///
    /// 测试纪律：
    ///   · 只用临时目录里的临时文件，不触网、不碰真实安装目录；
    ///   · 标记 JSON 在这里手写（主程序测试工程不引用更新器工程），
    ///     "更新器写 → 主程序读"的端到端格式对拍放在
    ///     <c>tests/LiteMonitor.Updater.Tests/UpdaterLegacyBridgeTests.cs</c>。
    /// </remarks>
    public sealed class UpdaterLegacyBridgeTests : IDisposable
    {
        /// <summary>
        /// 内置签名公钥的指纹（base64 文本的 SHA-256，大写十六进制）。
        /// </summary>
        /// <remarks>
        /// ★ 与更新器工程里那份拷贝用**同一个**期望值，两边各自断言一次
        ///   ⇒ 两份拷贝一旦漂移，至少有一边会红。
        /// </remarks>
        private const string ExpectedPublicKeyFingerprint =
            "25E6659D954A92A9FCCF1E2622F30F68A7FA9C84390B419B33F3EB0F11F97572";

        private const string MarkerFileName = "update_legacy_bridge.json";

        /// <summary>与更新器写出的标记同构的一份样本。</summary>
        private const string MarkerJson = @"{
  ""schema"": ""liteMonitor.legacyBridge/v1"",
  ""code"": ""missing_digest_rejected"",
  ""reason"": ""调用方没有提供期望 SHA-256 摘要（LM-016）：本更新器只接受 '<update.zip> <sha256>' 形式。"",
  ""guidance"": ""自动升级通道与当前版本不兼容，请手动下载新版本。"",
  ""manualDownloadUrl"": ""https://github.com/mojingjing-git/LiteMonitorPlus/releases"",
  ""updaterVersion"": ""2.1"",
  ""timestamp"": ""2026-10-23T07:33:00.0000000+08:00""
}";

        private readonly string _root;

        public UpdaterLegacyBridgeTests()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "LiteMonitor.Tests.LegacyBridge",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        // ================================================================
        // 1. 读得到 + 给出提示 + 消费后不再重复
        // ================================================================

        [Fact]
        public void 启动时读到标记并给出明确提示()
        {
            WriteMarker(MarkerJson);

            var presented = new List<LegacyBridgeNotice>();
            UpdateChecker.ReportLegacyBridgeNoticeIfAny(_root, presented.Add);

            Assert.Single(presented);
            LegacyBridgeNotice notice = presented[0];

            Assert.Equal("missing_digest_rejected", notice.Code);
            Assert.Contains("摘要", notice.Reason);
            Assert.Equal(
                "https://github.com/mojingjing-git/LiteMonitorPlus/releases",
                notice.ManualDownloadUrl);
            Assert.False(string.IsNullOrWhiteSpace(notice.Timestamp));

            // 提示正文必须明确告诉用户"自动升级通道不可用 + 请手动下载"
            string message = LegacyBridgeNotice.BuildMessage(true, notice);
            Assert.Contains("自动升级通道与当前版本不兼容", message);
            Assert.Contains("手动下载新版本", message);
            Assert.Contains("摘要", message);
            Assert.Contains("https://github.com/mojingjing-git/LiteMonitorPlus/releases", message);

            string english = LegacyBridgeNotice.BuildMessage(false, notice);
            Assert.Contains("download the new version manually", english);
            Assert.Contains("https://github.com/mojingjing-git/LiteMonitorPlus/releases", english);
        }

        [Fact]
        public void 标记被消费后不再重复提示()
        {
            WriteMarker(MarkerJson);

            var presented = new List<LegacyBridgeNotice>();
            UpdateChecker.ReportLegacyBridgeNoticeIfAny(_root, presented.Add);
            Assert.Single(presented);

            // 消费：文件被清掉
            Assert.False(File.Exists(Path.Combine(_root, MarkerFileName)));

            // 再启动一次：同一件事不再弹
            presented.Clear();
            UpdateChecker.ReportLegacyBridgeNoticeIfAny(_root, presented.Add);
            Assert.Empty(presented);
        }

        [Fact]
        public void 消费语义在直接读取时也成立()
        {
            WriteMarker(MarkerJson);

            LegacyBridgeNotice? first = UpdateChecker.TryConsumeLegacyBridgeNotice(_root);
            LegacyBridgeNotice? second = UpdateChecker.TryConsumeLegacyBridgeNotice(_root);

            Assert.NotNull(first);
            Assert.Null(second);
        }

        // ================================================================
        // 2. 没有标记 / 标记损坏 ⇒ 不提示、不抛异常
        // ================================================================

        [Fact]
        public void 没有标记时不提示()
        {
            var presented = new List<LegacyBridgeNotice>();
            UpdateChecker.ReportLegacyBridgeNoticeIfAny(_root, presented.Add);

            Assert.Empty(presented);
            Assert.Null(UpdateChecker.TryConsumeLegacyBridgeNotice(_root));
        }

        [Theory]
        [InlineData("not json at all")]
        [InlineData("{ \"code\": ")]
        [InlineData("{}")]
        [InlineData("[]")]
        public void 标记内容不可用时不提示且不抛异常(string content)
        {
            WriteMarker(content);

            var presented = new List<LegacyBridgeNotice>();
            UpdateChecker.ReportLegacyBridgeNoticeIfAny(_root, presented.Add);

            Assert.Empty(presented);
        }

        [Fact]
        public void 标记缺少指引时用默认指引兜底()
        {
            WriteMarker(@"{ ""reason"": ""只有原因，没有指引"" }");

            LegacyBridgeNotice? notice = UpdateChecker.TryConsumeLegacyBridgeNotice(_root);
            Assert.NotNull(notice);

            string message = LegacyBridgeNotice.BuildMessage(true, notice!);
            Assert.Contains("自动升级通道与当前版本不兼容", message);
            Assert.Contains("手动下载新版本", message);
            // 缺发布页地址时回落到主程序内置的发布页
            Assert.Contains(UpdateChecker.ReleasePageUrl, message);
        }

        // ================================================================
        // 3. 信任锚不漂移
        // ================================================================

        [Fact]
        public void 主程序内置签名公钥指纹与文档一致()
        {
            Assert.Equal(
                ExpectedPublicKeyFingerprint,
                FingerprintOfBase64Text(UpdateChecker.UpdateSigningPublicKeySpkiBase64));
        }

        [Fact]
        public void 主程序内置签名公钥必须是可用的ECDSA主程序公钥()
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(UpdateChecker.UpdateSigningPublicKeySpkiBase64),
                out int bytesRead);

            Assert.True(bytesRead > 0);

            var parameters = ecdsa.ExportParameters(false);
            Assert.Equal(32, parameters.Q.X.Length);
            Assert.Equal(32, parameters.Q.Y.Length);
        }

        // ================================================================
        // 辅助
        // ================================================================

        private void WriteMarker(string content)
        {
            File.WriteAllText(Path.Combine(_root, MarkerFileName), content, new UTF8Encoding(false));
        }

        private static string FingerprintOfBase64Text(string base64Text)
            => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(base64Text)));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
