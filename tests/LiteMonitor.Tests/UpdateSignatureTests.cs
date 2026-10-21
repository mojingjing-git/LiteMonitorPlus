using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 更新元数据签名的回归护栏（修复计划 W1.2）。
    ///
    /// 盯住的是"更新链路里唯一产生信任的那一步"：
    /// 客户端只验签、不信任 URL，因此签名一旦失效或可绕过，
    /// 后面所有摘要比对、回滚、自检都只是装点。
    ///
    /// 三条必须被拒绝的篡改（计划验收条件）：
    ///   1. 改 release ZIP 的一个字节；
    ///   2. 改 version.json 里的 sha256；
    ///   3. 用错误私钥重签。
    ///
    /// 测试纪律：
    ///  - 自造密钥对、自签自验，不依赖 secrets/ 里的真实私钥
    ///    （否则换密钥或换机器会让用例静默失效 / 无法运行）；
    ///  - 只用临时目录里的临时文件，不触网。
    /// </summary>
    public class UpdateSignatureTests
    {
        private const string SampleVersion = "1.0.1";
        private const string SampleDate = "2026-10-21";

        /// <summary>一个形态合法的摘要（64 位十六进制）。</summary>
        private static readonly string SampleSha256 = new string('A', 64);

        // ================================================================
        // 正例
        // ================================================================

        [Fact]
        public void 正确签名的元数据通过验签()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pub = ExportPublicKey(key);
            string sig = SignWith(key, SampleVersion, SampleSha256, SampleDate);

            Assert.True(
                UpdateChecker.VerifyUpdateSignatureWithKey(
                    SampleVersion, SampleSha256, SampleDate, sig, pub),
                "原样签出的元数据必须验签通过");
        }

        // ================================================================
        // 篡改一：改 sha256
        // ================================================================

        [Fact]
        public void 篡改sha256后验签失败()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pub = ExportPublicKey(key);
            string sig = SignWith(key, SampleVersion, SampleSha256, SampleDate);

            // 只改一个十六进制字符
            string tampered = "B" + SampleSha256.Substring(1);

            Assert.False(
                UpdateChecker.VerifyUpdateSignatureWithKey(
                    SampleVersion, tampered, SampleDate, sig, pub),
                "改掉 sha256 必须让签名失效 —— 否则攻击者可以替换安装包内容");
        }

        // ================================================================
        // 篡改二：改版本号 / 发布日期
        // ================================================================

        [Fact]
        public void 篡改版本号后验签失败()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pub = ExportPublicKey(key);
            string sig = SignWith(key, SampleVersion, SampleSha256, SampleDate);

            Assert.False(
                UpdateChecker.VerifyUpdateSignatureWithKey(
                    "9.9.9", SampleSha256, SampleDate, sig, pub),
                "版本号是被签字段：改了它必须验签失败");
        }

        [Fact]
        public void 篡改发布日期后验签失败()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pub = ExportPublicKey(key);
            string sig = SignWith(key, SampleVersion, SampleSha256, SampleDate);

            Assert.False(
                UpdateChecker.VerifyUpdateSignatureWithKey(
                    SampleVersion, SampleSha256, "2020-01-01", sig, pub),
                "发布日期是被签字段：改了它必须验签失败");
        }

        // ================================================================
        // 篡改三：错误私钥重签
        // ================================================================

        [Fact]
        public void 用错误私钥重签后验签失败()
        {
            using var trustedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            string trustedPub = ExportPublicKey(trustedKey);

            // 攻击者用自己的私钥签一份"内容更好"的元数据
            string forgedSha = new string('C', 64);
            string forgedSig = SignWith(attackerKey, "9.9.9", forgedSha, SampleDate);

            Assert.False(
                UpdateChecker.VerifyUpdateSignatureWithKey(
                    "9.9.9", forgedSha, SampleDate, forgedSig, trustedPub),
                "用非发布方私钥签出来的元数据必须被拒绝");
        }

        // ================================================================
        // 失败即不信任：缺字段 / 畸形输入不得放行
        // ================================================================

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void 缺少签名时验签失败(string? signature)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pub = ExportPublicKey(key);

            Assert.False(
                UpdateChecker.VerifyUpdateSignatureWithKey(
                    SampleVersion, SampleSha256, SampleDate, signature, pub),
                "没有签名就等于没有认证，绝不能当成「通过」");
        }

        [Theory]
        [InlineData("")]                                  // 空摘要
        [InlineData("ABCD")]                              // 太短
        [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")] // 非十六进制
        public void 摘要格式非法时直接拒绝(string sha256)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pub = ExportPublicKey(key);
            string sig = SignWith(key, SampleVersion, SampleSha256, SampleDate);

            Assert.False(
                UpdateChecker.VerifyUpdateSignatureWithKey(
                    SampleVersion, sha256, SampleDate, sig, pub),
                "摘要不是 64 位十六进制时应在验签前就被拒（不留「格式怪但哈希对」的缝）");
        }

        [Fact]
        public void 畸形签名不抛异常只返回false()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pub = ExportPublicKey(key);

            // 不是合法 base64
            Assert.False(UpdateChecker.VerifyUpdateSignatureWithKey(
                SampleVersion, SampleSha256, SampleDate, "!!!not-base64!!!", pub));

            // 是合法 base64 但不是 DER 序列
            Assert.False(UpdateChecker.VerifyUpdateSignatureWithKey(
                SampleVersion, SampleSha256, SampleDate,
                Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }), pub));

            // 公钥本身非法
            Assert.False(UpdateChecker.VerifyUpdateSignatureWithKey(
                SampleVersion, SampleSha256, SampleDate,
                Convert.ToBase64String(new byte[] { 1, 2, 3 }), "not-a-key"));
        }

        // ================================================================
        // 载荷定义（防发布脚本与客户端漂移）
        // ================================================================

        /// <summary>
        /// 载荷是 <c>version\nsha256\nreleaseDate</c>，单个 LF、三行、顺序固定。
        /// </summary>
        /// <remarks>
        /// ★ 这条用例存在的意义不是"再测一遍拼接"，而是**锁死跨语言协议**：
        ///   scripts/sign-release.ps1 用 PowerShell 拼同一个字符串。
        ///   任何一方改了顺序、分隔符、行尾或加了字段，都必须同时改另一边，
        ///   否则会出现"发布端签得出来、客户端永远验不过"的静默失效。
        /// </remarks>
        [Fact]
        public void 载荷格式被锁定为三行LF分隔()
        {
            string payload = UpdateChecker.BuildUpdateSignaturePayload(
                SampleVersion, SampleSha256, SampleDate);

            Assert.Equal($"{SampleVersion}\n{SampleSha256}\n{SampleDate}", payload);

            // 显式检查不含 CR —— 发布脚本一旦用 CRLF 拼接就会与客户端不一致
            Assert.DoesNotContain("\r", payload);

            string[] lines = payload.Split('\n');
            Assert.Equal(3, lines.Length);
            Assert.Equal(SampleVersion, lines[0]);
            Assert.Equal(SampleSha256, lines[1]);
            Assert.Equal(SampleDate, lines[2]);
        }

        // ================================================================
        // 篡改 release ZIP 的一个字节 → 摘要必须变
        // ================================================================

        /// <summary>
        /// 对应计划的验收条件一：改 ZIP 一个字节，下载后的摘要比对必须失败。
        /// </summary>
        /// <remarks>
        /// ★ 这里验证的是摘要的**敏感性**（ComputeFileSha256 的输出格式与更新流程一致）。
        ///   摘要比对发生在 UpdateDialog.StartUpdater 之前，不匹配即删除产物并中止。
        /// </remarks>
        [Fact]
        public void 篡改ZIP一个字节后摘要与声明不一致()
        {
            string dir = Path.Combine(Path.GetTempPath(), "litemonitor-sig-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, "release.zip");

            try
            {
                // 造一个"发布包"
                byte[] original = new byte[4096];
                new Random(12345).NextBytes(original);
                File.WriteAllBytes(zip, original);

                // 发布端签名时声明的摘要
                string declared = UpdateChecker.ComputeFileSha256(zip);
                Assert.Equal(64, declared.Length);
                Assert.Equal(declared.ToUpperInvariant(), declared);   // 大写十六进制

                // 攻击者改一个字节
                original[2048] ^= 0xFF;
                File.WriteAllBytes(zip, original);

                string actual = UpdateChecker.ComputeFileSha256(zip);

                Assert.NotEqual(declared, actual);
                Assert.False(string.Equals(actual, declared, StringComparison.OrdinalIgnoreCase),
                    "篡改一个字节后摘要必须不同 —— 否则下载后校验形同虚设");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ================================================================
        // 内置公钥与发布私钥的一致性（有私钥时才跑）
        // ================================================================

        /// <summary>
        /// 若本地存在签名私钥，验证它确实对应客户端内置的公钥。
        /// </summary>
        /// <remarks>
        /// ★ 这是发布端的"防呆"：用错密钥签名，现象只出现在用户那边（"更新源不可信"），
        ///   发布端毫无感知。scripts/sign-release.ps1 里也有同样的自检，
        ///   这里在测试里再钉一次，保证改代码时不会忘记同步密钥。
        /// ★ 私钥不进版本库（.gitignore 排除 secrets/），所以没有私钥时**跳过**而不是失败。
        /// </remarks>
        [Fact]
        public void 本地私钥与内置公钥一致()
        {
            string? keyPath = FindSigningKey();
            if (keyPath == null) return;   // 干净检出：没有私钥，跳过

            using var key = ECDsa.Create();
            byte[] pkcs8 = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
            key.ImportPkcs8PrivateKey(pkcs8, out _);

            string pubFromPrivate = ExportPublicKey(key);

            // 用这份私钥签一份样本，再用内置公钥验 —— 走完整条生产路径
            string sig = SignWith(key, SampleVersion, SampleSha256, SampleDate);

            Assert.True(
                UpdateChecker.TryVerifyUpdateSignature(
                    SampleVersion, SampleSha256, SampleDate, sig),
                "本地私钥签出的样本必须能被内置公钥验通；否则发布出去的 version.json 在用户机器上全部失效。\n" +
                $"本地公钥（SPKI base64）：{pubFromPrivate}");
        }

        // ================================================================
        // helper
        // ================================================================

        private static string ExportPublicKey(ECDsa key)
            => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        private static string SignWith(ECDsa key, string version, string sha256, string releaseDate)
        {
            byte[] payload = Encoding.UTF8.GetBytes(
                UpdateChecker.BuildUpdateSignaturePayload(version, sha256, releaseDate));

            byte[] sig = key.SignData(payload, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);

            return Convert.ToBase64String(sig);
        }

        /// <summary>从仓库根向上找 secrets/update-signing-key.pkcs8（找不到返回 null）。</summary>
        private static string? FindSigningKey()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "secrets", "update-signing-key.pkcs8");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
}
